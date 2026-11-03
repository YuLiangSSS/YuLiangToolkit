using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// 麦克风采集（WASAPI **共享模式**，不是回环）：把设备混音格式的数据转成「单声道 float」交给通话引擎。
///
/// <para><b>为什么用共享模式</b>：共享模式下 WASAPI 自己把采样率 / 通道数统一成设备混音格式，
/// 不会独占麦克风（用户还能用别的软件录音），也不会因为某个程序占着设备就开不了。
/// 代价是我们要自己做「多声道取平均 → 单声道」和「设备采样率 → 16 kHz 重采样」（重采样在引擎里做）。</para>
///
/// <para><b>线程模型</b>：<see cref="Start"/> 在调用线程完成 COM 初始化并启动音频客户端，
/// 之后由一个后台线程（AboveNormal 优先级）每 5 ms 轮询 <c>GetNextPacketSize</c>；
/// 有数据就整包读出、转单声道、触发 <see cref="SamplesAvailable"/>。
/// <see cref="Dispose"/> 先置停止标志 + <c>IAudioClient.Stop</c>，再 Join 线程、释放 COM。
/// 事件在采集线程上触发，订阅方（引擎）自己保证快速返回、不碰界面（界面刷新要走 Ui）。</para>
///
/// <para><b>已知边界</b>：v1 固定 100 ms 缓冲（共享模式对缓冲区有下限，太小会频繁欠载）；
/// 不做回声消除（AEC），外放会啸叫 —— 文档里要求戴耳机。
/// 设备中途被拔掉时 <c>GetNextPacketSize</c> 会返回失败，线程停下并把原因写进 <see cref="LastError"/>；
/// <see cref="CallContracts"/> 这一层没有「设备变化事件」，由引擎按需重建。</para>
/// </summary>
public sealed class MicrophoneCapture : IMicrophoneSource
{
    /// <summary>AUDCLNT_BUFFERFLAGS_SILENT：这一包是静音（数据指针可能无效，不能读）。</summary>
    private const uint BufferFlagsSilent = 0x2;

    /// <summary>轮询间隔（毫秒）：5 ms 足够跟上 10~20 ms 的音频包，又不会空转烧 CPU。</summary>
    private const int PollIntervalMs = 5;

    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _thread;
    private WasapiSampleConverter? _converter;

    /// <summary>设备混音格式（通道数 / 采样率 / 样本类型），Start 成功后才有意义。</summary>
    private WasapiMixFormat _format;

    /// <summary>单声道输出缓冲（复用，订阅方必须当场消费）。</summary>
    private float[] _mono = Array.Empty<float>();

    private volatile bool _stopping;
    private volatile string? _lastError;
    private volatile int _sampleRate;

    /// <inheritdoc />
    public event Action<float[], int>? SamplesAvailable;

    /// <inheritdoc />
    public int SampleRate => _sampleRate;

    /// <inheritdoc />
    public string? LastError => _lastError;

    /// <summary>
    /// 打开麦克风并开始采集（空的设备 Id = 系统默认麦克风）。
    /// <para>可以重复调用：会先关掉上一次的采集。</para>
    /// </summary>
    /// <returns>(是不是起来了, 失败原因)。失败原因会同时写进 <see cref="LastError"/>。</returns>
    public (bool Ok, string Error) Start(string deviceId)
    {
        // 重复 Start：先把上一路彻底收掉，避免两个线程抢同一个设备。
        Stop();
        SetError(null);
        _sampleRate = 0;

        try
        {
            var device = AudioDeviceCatalog.OpenDevice(WasapiInterop.DataFlowCapture, deviceId);
            if (device is null)
                return Fail("没有找到可用的麦克风（设备可能已拔出、被禁用，或系统音频服务没启动）");
            _device = device;

            // 1) 激活 IAudioClient
            var audioClientIid = typeof(IAudioClient).GUID;
            var hr = device.Activate(ref audioClientIid, WasapiInterop.ClsCtxAll, IntPtr.Zero, out var clientObject);
            if (hr != 0 || clientObject is not IAudioClient client)
                return Fail($"激活麦克风音频客户端失败（hr=0x{hr:X8}）");
            _client = client;

            // 2) 取设备混音格式，按共享模式初始化（第二参数 0：绝不带 LOOPBACK —— 那是抓系统播放声）
            hr = client.GetMixFormat(out var formatPtr);
            if (hr != 0 || formatPtr == IntPtr.Zero)
                return Fail($"读取麦克风混音格式失败（hr=0x{hr:X8}）");

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
                return Fail($"初始化麦克风采集失败（hr=0x{hr:X8}；设备可能被其它程序独占）");

            // 3) 取采集客户端
            var captureIid = typeof(IAudioCaptureClient).GUID;
            hr = client.GetService(ref captureIid, out var captureObject);
            if (hr != 0 || captureObject is not IAudioCaptureClient capture)
                return Fail($"获取麦克风采集客户端失败（hr=0x{hr:X8}）");
            _capture = capture;

            // 4) 准备转换器 / 输出缓冲，启动音频流
            _converter = new WasapiSampleConverter(_format);
            _mono = new float[Math.Max(1024, _format.SampleRate / 10)];
            _sampleRate = _format.SampleRate;

            hr = client.Start();
            if (hr != 0) return Fail($"启动麦克风采集失败（hr=0x{hr:X8}）");

            _stopping = false;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "chat-call-mic",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return (true, "");
        }
        catch (Exception ex)
        {
            return Fail("启动麦克风失败：" + ex.Message);
        }
    }

    /// <summary>停止采集并释放 COM 对象（可重复调用；失败原因不会被清掉）。</summary>
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

        WasapiInterop.Release(_capture);
        WasapiInterop.Release(_client);
        WasapiInterop.Release(_device);
        _capture = null;
        _client = null;
        _device = null;
        _converter = null;
        _mono = Array.Empty<float>();
        _sampleRate = 0;
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    // ===================== 采集线程 =====================

    /// <summary>采集线程主体：5 ms 轮询，有多少包读多少包（共享模式下一般每 10 ms 左右一包）。</summary>
    private void CaptureLoop()
    {
        try
        {
            var capture = _capture;
            if (capture is null) return;

            while (!_stopping)
            {
                var hr = capture.GetNextPacketSize(out var pendingFrames);
                if (hr != 0)
                {
                    Fail($"读取麦克风数据包大小失败（hr=0x{hr:X8}；设备可能已被拔出）");
                    return;
                }

                if (pendingFrames == 0)
                {
                    Thread.Sleep(PollIntervalMs);
                    continue;
                }

                // 一次把积压的包全部读完，避免延迟累积（40ms 的包积压几个就听得出卡顿）。
                while (!_stopping)
                {
                    if (!ReadPacket(capture)) return;
                    hr = capture.GetNextPacketSize(out pendingFrames);
                    if (hr != 0)
                    {
                        Fail($"读取麦克风数据包大小失败（hr=0x{hr:X8}；设备可能已被拔出）");
                        return;
                    }
                    if (pendingFrames == 0) break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail("麦克风采集线程异常：" + ex.Message);
        }
    }

    /// <summary>读一包数据：SILENT 输出 0，其它情况解码 + 多声道平均成单声道。</summary>
    /// <returns>false = 出错，采集线程应退出。</returns>
    private bool ReadPacket(IAudioCaptureClient capture)
    {
        var hr = capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
        if (hr != 0)
        {
            Fail($"读取麦克风数据失败（hr=0x{hr:X8}；设备可能已被拔出）");
            return false;
        }

        if (frames == 0) return true;

        try
        {
            var frameCount = (int)frames;
            EnsureMono(frameCount);

            if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
            {
                // 静音包：数据指针不可信，直接输出 0（仍要触发事件，让引擎的节奏不乱）。
                Array.Clear(_mono, 0, frameCount);
            }
            else
            {
                var converter = _converter;
                if (converter is null) return false;

                converter.Decode(data, frameCount, out var interleaved);
                DownmixToMono(interleaved, frameCount, _mono);
            }

            SamplesAvailable?.Invoke(_mono, frameCount);
            return true;
        }
        finally
        {
            // 无论如何都要还包，否则下次读不到新数据。
            capture.ReleaseBuffer(frames);
        }
    }

    /// <summary>把 interleaved 多声道样本按「各通道平均」混成单声道（引擎再重采样到 16 kHz）。</summary>
    private void DownmixToMono(float[] interleaved, int frames, float[] mono)
    {
        var channels = _format.Channels;
        if (channels <= 1)
        {
            var copy = Math.Min(frames, interleaved.Length);
            if (copy > 0) Array.Copy(interleaved, mono, copy);
            if (frames > copy) Array.Clear(mono, copy, frames - copy);
            return;
        }

        var sampleCount = Math.Min(frames * channels, interleaved.Length);
        var usableFrames = sampleCount / channels;
        for (var frame = 0; frame < usableFrames; frame++)
        {
            var offset = frame * channels;
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++) sum += interleaved[offset + channel];
            mono[frame] = sum / channels; // 各通道平均：不会因为取第一路而丢掉右声道的人声
        }

        if (usableFrames < frames) Array.Clear(mono, usableFrames, frames - usableFrames);
    }

    /// <summary>保证单声道缓冲至少能放 frames 个样本。</summary>
    private void EnsureMono(int frames)
    {
        if (_mono.Length >= frames) return;
        _mono = new float[Math.Max(frames, _mono.Length * 2)];
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
        if (message is not null) Trace.WriteLine("[通话麦克风] " + message);
    }
}
