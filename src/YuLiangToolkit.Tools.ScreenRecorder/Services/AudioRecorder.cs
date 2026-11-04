using System.Diagnostics;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 一次录音会话：把 <see cref="WasapiAudioCapture"/> 采到的立体声 float 按
/// <see cref="GainProvider"/> 的实时增益写进 <see cref="PcmWavWriter"/>。
///
/// <para><b>用法</b>：界面在开始录制时 <see cref="Start"/>，停止录制时 <see cref="Stop"/>；
/// 停止后 WAV 已经回填头并关闭，Lead 可以立刻交给 FFmpeg 合成。</para>
///
/// <para><b>增益</b>：<see cref="GainProvider"/> 每个音频包都会读一次，所以界面滑块拖动时
/// 音量会实时变化（不用重启录制）。没设置时按 1.0（100%）处理。</para>
///
/// <para><b>错误</b>：<see cref="Start"/> 返回 false 时 <see cref="LastError"/> 有中文原因；
/// 采集线程之后异步失败（设备被拔 / 切换、麦可风权限被关）也会通过 <see cref="LastError"/> 读到
/// （它同时看自己的错误和 <see cref="WasapiAudioCapture.LastError"/>）。所有失败都不抛异常。</para>
///
/// <para><b>没采到声音时</b>：WAV 仍然是一个合法的 44 字节空文件；用 <see cref="HasData"/>
/// 判断要不要把它当音轨交给 FFmpeg（系统回环在完全没声音时可能一包数据都没有）。</para>
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    /// <summary>WAV 固定写立体声（采集层已经把单声道 / 多声道统一成立体声）。</summary>
    private const int WavChannels = 2;

    /// <summary>设备混音采样率读不到时的兜底值（正常不会用到）。</summary>
    private const int FallbackSampleRate = 48000;

    private const string LogPrefix = "[录屏录音] ";

    private readonly AudioCaptureKind _kind;
    private readonly object _gate = new();

    private WasapiAudioCapture? _capture;
    private PcmWavWriter? _writer;
    private string? _lastError;
    private volatile bool _hasData;
    private bool _disposed;

    /// <summary>创建录音会话（不会打开设备，<see cref="Start"/> 才打开）。</summary>
    /// <param name="kind">采系统回环还是麦克风。</param>
    /// <param name="wavPath">输出 WAV 路径（目录不存在会自动创建）。</param>
    public AudioRecorder(AudioCaptureKind kind, string wavPath)
    {
        _kind = kind;
        OutputPath = wavPath ?? "";

        // 构造不能抛异常（界面是在 ViewModel 构造链里 new 的），路径问题留到 Start 再报
        if (string.IsNullOrWhiteSpace(OutputPath)) SetError("录音文件路径为空");
    }

    /// <summary>录制中实时读取当前音量（1.0 = 100%，内部按 0~2 夹取）；界面滑块会改它。</summary>
    public Func<float>? GainProvider { get; set; }

    /// <summary>是否已经真正收到过音频数据（判断空 WAV 用）。</summary>
    public bool HasData => _hasData;

    /// <summary>最近一次错误的中文原因（没有错误 / 成功后为 null）。</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                // 采集线程异步失败时错误在 capture 上：优先返回自己的，再看 capture 的
                return _lastError ?? _capture?.LastError;
            }
        }
    }

    /// <summary>输出 WAV 路径（构造时传入）。</summary>
    public string OutputPath { get; }

    /// <summary>采集来源（构造时指定）。</summary>
    public AudioCaptureKind Kind => _kind;

    /// <summary>
    /// 开始采集 + 写 WAV。失败返回 false（<see cref="LastError"/> 有原因），不抛异常。
    /// <para>重复调用：已经开始时直接返回 true。</para>
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_disposed) { _lastError = "录音器已经释放"; return false; }
            if (_capture is not null) return true; // 已经在录
            if (string.IsNullOrWhiteSpace(OutputPath)) { _lastError = "录音文件路径为空"; return false; }
            _lastError = null;
        }

        var capture = new WasapiAudioCapture(_kind);
        PcmWavWriter? writer = null;
        try
        {
            if (!capture.Start())
            {
                var message = capture.LastError ?? "启动音频采集失败";
                capture.Dispose();
                SetError(message);
                return false;
            }

            // 采样率要等设备打开后才知道（一般 48000 / 44100），所以 WAV 在 Start 之后才建
            var sampleRate = capture.SampleRate > 0 ? capture.SampleRate : FallbackSampleRate;
            writer = new PcmWavWriter(OutputPath, sampleRate, WavChannels);
            if (writer.LastError is not null)
            {
                var message = writer.LastError;
                writer.Dispose();
                capture.Stop();
                capture.Dispose();
                SetError(message);
                return false;
            }

            // 先挂字段再订阅事件：帧回调线程一定能看到非空的 writer
            lock (_gate)
            {
                _capture = capture;
                _writer = writer;
            }
            _hasData = false;
            capture.DataAvailable += OnDataAvailable;
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) { _capture = null; _writer = null; }
            try { capture.DataAvailable -= OnDataAvailable; } catch { /* 忽略 */ }
            try { capture.Stop(); } catch { /* 忽略 */ }
            try { capture.Dispose(); } catch { /* 忽略 */ }
            if (writer is not null)
            {
                // WAV 已经创建过（哪怕只写了占位头）就收尾成合法文件，并把原因记下来
                try { writer.Complete(); } catch { /* 忽略 */ }
                try { writer.Dispose(); } catch { /* 忽略 */ }
                SetError(writer.LastError ?? ("启动录音失败：" + ex.Message));
                return false;
            }

            SetError("启动录音失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 停止采集并收尾 WAV（可重复调用、没开始也安全）。
    /// <para>顺序很重要：先摘事件 → 停采集线程（join，不再有 Write 并发）→ 才 Complete 回填头，
    /// 否则可能出现「正在写样本时回填头」，文件长度对不上。</para>
    /// </summary>
    public void Stop()
    {
        WasapiAudioCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }

        if (capture is not null)
        {
            try { capture.DataAvailable -= OnDataAvailable; } catch { /* 忽略 */ }
            try { capture.Stop(); } catch { /* 忽略 */ }
            var captureError = capture.LastError;
            try { capture.Dispose(); } catch { /* 忽略 */ }

            // 异步失败（拔设备 / 权限 / 设备切换）的原因要留给界面看
            if (!string.IsNullOrEmpty(captureError))
            {
                lock (_gate) _lastError ??= captureError;
            }
        }

        PcmWavWriter? writer;
        lock (_gate)
        {
            writer = _writer;
            _writer = null;
        }

        if (writer is not null)
        {
            try { writer.Complete(); } catch { /* Complete 自己会记 LastError */ }
            var writerError = writer.LastError;
            try { writer.Dispose(); } catch { /* 忽略 */ }
            if (!string.IsNullOrEmpty(writerError))
            {
                lock (_gate) _lastError ??= writerError;
            }
        }
    }

    /// <summary>停止并释放（= <see cref="Stop"/>；重复调用安全）。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        Stop();
    }

    /// <summary>采集线程回调：取实时增益 → 写 WAV。异常只记录，不抛回采集线程。</summary>
    private void OnDataAvailable(float[] samples, int frames)
    {
        if (frames <= 0) return;

        try
        {
            var writer = Volatile.Read(ref _writer);
            if (writer is null) return;

            var gain = 1f;
            var provider = GainProvider;
            if (provider is not null)
            {
                try
                {
                    gain = provider();
                }
                catch (Exception ex)
                {
                    // 界面绑定写错 / 滑块值异常时按 100% 继续录，不能因为增益把整段录音弄停
                    SetError("读取录音增益失败（按 100% 处理）：" + ex.Message);
                    gain = 1f;
                }
            }

            writer.Write(samples, frames, gain);
            _hasData = true;
        }
        catch (Exception ex)
        {
            SetError("写入录音数据失败：" + ex.Message);
        }
    }

    private void SetError(string? message)
    {
        lock (_gate) _lastError = message;
        if (message is not null) Trace.WriteLine(LogPrefix + message);
    }
}
