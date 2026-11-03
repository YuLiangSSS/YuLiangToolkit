using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 扬声器播放（WASAPI **共享模式**，拉模型）：引擎按声卡的节奏来「要数据」，这样时钟由声卡决定，
/// 不会像推送模型那样越放越飘。
///
/// <para><b>为什么是拉（pull）</b>：<see cref="CallContracts.IAudioSink"/> 的设计就是
/// <c>Start(deviceId, pullStereo)</c>。播放线程每轮算出「还能写多少帧」，就调
/// <c>pull(frames)</c> 让引擎把所有人的声音混成 <c>frames × 2</c> 的立体声 interleaved float，
/// 再按设备混音格式写进声卡缓冲。引擎的混音不需要自己开线程、也不需要自己做重采样（引擎按
/// <see cref="SampleRate"/> 把各路声音重采样到设备采样率）。</para>
///
/// <para><b>线程模型</b>：<see cref="Start"/> 在调用线程完成 COM 初始化 / 启动音频流，
/// 后台线程（AboveNormal）按「目标水位」补数据：水位低于约 25~40 ms 就写一块（最多 20 ms），
/// 这样不会把 100 ms 的共享模式缓冲一次填满 —— 一次填满等于白送 100 ms 延迟。
/// <see cref="Dispose"/> 停线程 → <c>IAudioClient.Stop</c> → 释放 COM。pull 在播放线程上调用，
/// 引擎必须线程安全且快速返回。</para>
///
/// <para><b>已知边界</b>：设备通道数 &gt; 2 时只取前两路（其余写 0）；单声道设备取左声道。
/// 不做 AEC / 音量归一化（限幅只在写 PCM 时做，float 直接透传）。
/// 设备被拔掉时 GetCurrentPadding / GetBuffer 会返回失败，线程结束并写 <see cref="LastError"/>。</para>
/// </summary>
public sealed class SpeakerOutput : IAudioSink
{
    /// <summary>设备原生缓冲目标水位：约 25 ms；每块最多 20 ms。</summary>
    private const int TargetPaddingMilliseconds = 25;

    /// <summary>每块最多多少毫秒的音频（pull 块太大等于给延迟加码）。</summary>
    private const int MaxChunkMilliseconds = 20;

    /// <summary>补数据时的轮询间隔。</summary>
    private const int SleepMilliseconds = 5;

    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioRenderClient? _render;
    private Thread? _thread;
    private Func<int, float[]>? _pull;
    private WasapiSampleConverter? _converter;
    private WasapiMixFormat _format;

    /// <summary>按设备通道布局整理好的样本缓冲（frames × channels，复用）。</summary>
    private float[] _deviceSamples = Array.Empty<float>();

    /// <summary>音频客户端缓冲总帧数（GetBufferSize）。</summary>
    private int _bufferFrames;

    private volatile bool _stopping;
    private volatile string? _lastError;
    private volatile int _sampleRate;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <summary>
    /// 打开扬声器开始播放。空的设备 Id = 系统默认扬声器。
    /// <para>可以重复调用：会先关掉上一次的播放。</para>
    /// </summary>
    /// <param name="deviceId">WASAPI 播放端点 Id。</param>
    /// <param name="pullStereo">
    /// 引擎的混音回调：给 frames，返回 frames × 2 的立体声 interleaved float（-1~1）。
    /// 返回 null / 长度不够时按静音补；抛异常时写 <see cref="LastError"/> 并继续放静音（不中断通话）。
    /// </param>
    /// <returns>(是不是起来了, 失败原因)。</returns>
    public (bool Ok, string Error) Start(string deviceId, Func<int, float[]> pullStereo)
    {
        // 重复 Start：先收掉上一次的播放线程，避免两个线程同时往一个 device 写。
        Stop();
        SetError(null);
        _sampleRate = 0;

        if (pullStereo is null) return Fail("没有提供播放数据回调");
        _pull = pullStereo;

        try
        {
            var device = AudioDeviceCatalog.OpenDevice(WasapiInterop.DataFlowRender, deviceId);
            if (device is null)
                return Fail("没有找到可用的扬声器（设备可能已拔出、被禁用，或系统音频服务没启动）");
            _device = device;

            // 1) 激活 IAudioClient
            var audioClientIid = typeof(IAudioClient).GUID;
            var hr = device.Activate(ref audioClientIid, WasapiInterop.ClsCtxAll, IntPtr.Zero, out var clientObject);
            if (hr != 0 || clientObject is not IAudioClient client)
                return Fail($"激活扬声器音频客户端失败（hr=0x{hr:X8}）");
            _client = client;

            // 2) 取设备混音格式，按共享模式初始化（flags = 0：普通播放，不是回环）
            hr = client.GetMixFormat(out var formatPtr);
            if (hr != 0 || formatPtr == IntPtr.Zero)
                return Fail($"读取扬声器混音格式失败（hr=0x{hr:X8}）");

            try
            {
                _format = WasapiInterop.ReadMixFormat(formatPtr); // 不支持的格式抛中文原因的 NotSupportedException
                hr = client.Initialize(WasapiInterop.ShareModeShared, 0, WasapiInterop.BufferDuration100Ms, 0,
                    formatPtr, IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }

            if (hr != 0)
                return Fail($"初始化扬声器播放失败（hr=0x{hr:X8}；设备可能被其它程序独占）");

            hr = client.GetBufferSize(out var bufferFrames);
            if (hr != 0 || bufferFrames == 0)
                return Fail($"读取扬声器缓冲大小失败（hr=0x{hr:X8}）");
            // 缓冲总帧数远小于 int.MaxValue（最多几秒的音频），直接转。
            _bufferFrames = (int)bufferFrames;

            // 3) 取播放客户端
            var renderIid = typeof(IAudioRenderClient).GUID;
            hr = client.GetService(ref renderIid, out var renderObject);
            if (hr != 0 || renderObject is not IAudioRenderClient render)
                return Fail($"获取扬声器播放客户端失败（hr=0x{hr:X8}）");
            _render = render;

            // 4) 准备转换器，启动音频流
            _converter = new WasapiSampleConverter(_format);
            _deviceSamples = new float[Math.Max(1024, _format.Channels * (_format.SampleRate / 10))];
            _sampleRate = _format.SampleRate;

            hr = client.Start();
            if (hr != 0) return Fail($"启动扬声器播放失败（hr=0x{hr:X8}）");

            _stopping = false;
            _thread = new Thread(PlaybackLoop)
            {
                IsBackground = true,
                Name = "chat-call-speaker",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return (true, "");
        }
        catch (Exception ex)
        {
            return Fail("启动扬声器失败：" + ex.Message);
        }
    }

    /// <summary>停止播放并释放 COM 对象（可重复调用；失败原因不会被清掉）。</summary>
    public void Stop()
    {
        _stopping = true;
        try { _client?.Stop(); } catch { /* 没启动 / 已释放都忽略 */ }

        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(TimeSpan.FromSeconds(1.5)); } catch { }
        }

        WasapiInterop.Release(_render);
        WasapiInterop.Release(_client);
        WasapiInterop.Release(_device);
        _render = null;
        _client = null;
        _device = null;
        _converter = null;
        _pull = null; // 别把引擎的闭包一直挂着（否则释放掉的引擎还会被委托引用）
        _deviceSamples = Array.Empty<float>();
        _bufferFrames = 0;
        _sampleRate = 0;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    // ===================== 播放线程 =====================

    /// <summary>
    /// 播放线程主体：按「目标水位」补数据。
    /// <para>为什么不是「有多少可写就写多少」：共享模式缓冲是 100 ms，一次填满就等于把延迟
    /// 固定成 100 ms；只维持约 25 ms 水位，延迟能压到 ~30~50 ms（对语音通话很关键）。</para>
    /// </summary>
    private void PlaybackLoop()
    {
        try
        {
            var client = _client;
            var render = _render;
            var pull = _pull;
            var converter = _converter;
            if (client is null || render is null || pull is null || converter is null) return;

            var maxChunkFrames = Math.Max(1, _format.SampleRate * MaxChunkMilliseconds / 1000);
            var targetPaddingFrames = Math.Max(maxChunkFrames * 2, _format.SampleRate * TargetPaddingMilliseconds / 1000);

            while (!_stopping)
            {
                var hr = client.GetCurrentPadding(out var paddingRaw);
                if (hr != 0)
                {
                    Fail($"读取扬声器播放状态失败（hr=0x{hr:X8}；设备可能已被拔出）");
                    return;
                }

                var padding = (int)paddingRaw; // 已排队的帧数，不可能超过 int 最大值
                if (padding >= targetPaddingFrames)
                {
                    // 水位够高：设备还在放，先歇一下，别把缓冲灌满。
                    Thread.Sleep(SleepMilliseconds);
                    continue;
                }

                var writable = _bufferFrames > padding ? _bufferFrames - padding : 0;
                var frames = Math.Min(Math.Min(writable, targetPaddingFrames - padding), maxChunkFrames);
                if (frames <= 0)
                {
                    Thread.Sleep(SleepMilliseconds);
                    continue;
                }

                if (!FillAndRelease(render, converter, pull, frames)) return;
                Thread.Sleep(SleepMilliseconds);
            }
        }
        catch (Exception ex)
        {
            Fail("扬声器播放线程异常：" + ex.Message);
        }
    }

    /// <summary>取一帧引擎混音数据 → 转设备格式 → 写进声卡缓冲并提交。</summary>
    /// <returns>false = 出错，播放线程应退出。</returns>
    private bool FillAndRelease(IAudioRenderClient render, WasapiSampleConverter converter,
        Func<int, float[]> pull, int frames)
    {
        float[]? stereo = null;
        try
        {
            stereo = pull(frames);
        }
        catch (Exception ex)
        {
            // 引擎混音出错不能把设备的音频线程弄死：本块放静音，继续跑（LastError 留给界面显示）。
            SetError("混音回调异常：" + ex.Message);
        }

        var channels = _format.Channels;
        var sampleCount = checked(frames * channels);
        EnsureDeviceSamples(sampleCount);
        BuildDeviceSamples(stereo, frames, channels);

        var hr = render.GetBuffer((uint)frames, out var destination);
        if (hr != 0)
        {
            Fail($"申请扬声器播放缓冲失败（hr=0x{hr:X8}）");
            return false;
        }

        converter.Encode(destination, _deviceSamples, sampleCount);

        hr = render.ReleaseBuffer((uint)frames, 0);
        if (hr != 0)
        {
            Fail($"提交扬声器播放数据失败（hr=0x{hr:X8}）");
            return false;
        }

        return true;
    }

    /// <summary>
    /// 把引擎给的立体声 interleaved 数据铺成设备通道布局：
    /// 单声道取左声道；两声道用 L/R；两声道以上只取前两路、其余写 0。
    /// </summary>
    private void BuildDeviceSamples(float[]? stereo, int frames, int channels)
    {
        var stereoLength = stereo?.Length ?? 0;
        if (channels <= 1)
        {
            for (var frame = 0; frame < frames; frame++)
                _deviceSamples[frame] = SampleAt(stereo, stereoLength, frame * 2); // 单声道设备：取左声道
            return;
        }

        for (var frame = 0; frame < frames; frame++)
        {
            var offset = frame * channels;
            _deviceSamples[offset] = SampleAt(stereo, stereoLength, frame * 2);
            _deviceSamples[offset + 1] = SampleAt(stereo, stereoLength, frame * 2 + 1);
            for (var channel = 2; channel < channels; channel++) _deviceSamples[offset + channel] = 0f;
        }
    }

    /// <summary>越界 / 缺数据按静音处理（引擎的 pull 契约是「必须返回 frames×2」，但别让它崩掉播放）。</summary>
    private static float SampleAt(float[]? source, int length, int index)
        => source is not null && index >= 0 && index < length ? source[index] : 0f;

    /// <summary>保证设备布局缓冲至少能放 sampleCount 个样本。</summary>
    private void EnsureDeviceSamples(int sampleCount)
    {
        if (_deviceSamples.Length >= sampleCount) return;
        _deviceSamples = new float[Math.Max(sampleCount, _deviceSamples.Length * 2)];
    }

    /// <summary>记录错误并回到停止状态；Start 里可以直接 <c>return Fail(...)</c>。</summary>
    private (bool Ok, string Error) Fail(string message)
    {
        SetError(message);
        Stop();
        return (false, message);
    }

    private void SetError(string? message)
    {
        _lastError = message;
        if (message is not null) Trace.WriteLine("[通话扬声器] " + message);
    }
}
