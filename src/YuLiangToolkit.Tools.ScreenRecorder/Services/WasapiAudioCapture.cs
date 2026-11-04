using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 音频采集来源：系统扬声器回环（录「电脑正在播放的声音」）或默认麦克风。
/// </summary>
public enum AudioCaptureKind
{
    /// <summary>系统回环：默认播放设备的混音（不含麦克风）。</summary>
    SystemLoopback,

    /// <summary>默认麦克风（eCommunications 角色，和常见的语音 / 会议软件一致）。</summary>
    Microphone,
}

/// <summary>
/// 用 Windows 自带 WASAPI 采集音频（零新增 NuGet 包），输出统一为
/// <b>立体声 interleaved float</b>（范围 -1~1，48kHz / 44.1kHz 随设备混音格式）。
///
/// <para>和 Live2D 的 <c>WasapiLoopbackCapture</c> 同一套 COM 互操作与格式转换，区别只有三点：
/// <list type="number">
///   <item>加了 <see cref="AudioCaptureKind.Microphone"/>：<c>eCapture + eCommunications</c> 端点，且
///   <b>不加</b> <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c> 标志；</item>
///   <item>设备切换不再抛事件：每 2 秒检查一次默认设备，变了就写 <see cref="LastError"/> 并停采集，
///   由上层（界面状态栏）提示用户重新开始；</item>
///   <item>出错 / 订阅者抛异常时只把原因写进 <see cref="LastError"/> 并让采集线程退出，不把异常抛给调用方。</item>
/// </list></para>
///
/// <para><b>格式</b>：共享模式下按<b>设备混音格式</b>采集（常见 float32 / 也支持 PCM16 / PCM24 / PCM32），
/// 多通道只取前两路，单声道复制到左右声道，最终统一成 <see cref="DataAvailable"/> 的立体声 float。</para>
///
/// <para><b>线程</b>：<see cref="Start"/> 在调用线程做一次初始化（几十毫秒），之后由后台专用线程轮询采集；
/// <see cref="Stop"/> 停线程、join 后释放 COM 对象。</para>
/// </summary>
public sealed class WasapiAudioCapture : IDisposable
{
    // ---- WASAPI 常量 ----
    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int BufferFlagsSilent = 0x00000002;
    private const long BufferDuration100Ms = 1_000_000; // 100ns 单位
    private const int DataFlowRender = 0;
    private const int DataFlowCapture = 1;
    private const int RoleConsole = 0;
    private const int RoleCommunications = 2;
    private const int ClsCtxAll = 23;

    /// <summary>多久检查一次默认设备是否变化（切蓝牙 / 拔耳机时回环会失效）。</summary>
    private const int DefaultDeviceCheckIntervalMs = 2000;

    private const string LogPrefix = "[录屏音频] ";

    /// <summary>采集线程从 Start 到 Stop 的最长 join 时间；超时也不再阻塞调用方。</summary>
    private static readonly TimeSpan ThreadJoinTimeout = TimeSpan.FromSeconds(2);

    private readonly AudioCaptureKind _kind;

    /// <summary>启停串行化：避免 Start / Stop 并发时出现「线程还没启动就被 join」或 COM 提前释放。</summary>
    private readonly object _lifecycleGate = new();

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private volatile Thread? _thread;
    private volatile bool _stopping;

    /// <summary>最近一次错误（volatile 写，供任意线程读）。</summary>
    private string? _lastError;

    /// <summary>打开时的默认设备 Id（判断「默认设备变了」用）。</summary>
    private string _deviceId = "";

    /// <summary>立体声输出缓冲（复用，下一次回调会被覆盖，订阅者必须当场消费）。</summary>
    private float[] _buffer = new float[48000 * 2 * 2]; // 0.5 秒的立体声空间，不够再扩容

    /// <summary>设备原始样本（多通道 interleaved）的复用缓冲。</summary>
    private float[] _rawSamples = Array.Empty<float>();

    private AudioFormatKind _formatKind = AudioFormatKind.Float32;
    private int _sourceChannels = 2;

    /// <summary>创建采集器；真正打开设备要调 <see cref="Start"/>。</summary>
    public WasapiAudioCapture(AudioCaptureKind kind) => _kind = kind;

    /// <summary>采集到一批数据（参数：立体声 interleaved float 缓冲、帧数；<b>缓冲会被下一次回调复用</b>）。</summary>
    public event Action<float[], int>? DataAvailable;

    /// <summary>采集来源（构造时指定）。</summary>
    public AudioCaptureKind Kind => _kind;

    /// <summary>是否正在采集（异步出错 / 检测到设备切换后会自动变成 false）。</summary>
    public bool IsRunning
    {
        get
        {
            var thread = _thread;
            return thread is { IsAlive: true } && !_stopping;
        }
    }

    /// <summary>最近一次错误的中文原因（没有错误 / 成功后为 null）。</summary>
    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>设备混音采样率（一般 48000 / 44100；<see cref="Start"/> 成功后才有效）。</summary>
    public int SampleRate { get; private set; }

    /// <summary>设备原始通道数（一般 2；输出始终是立体声）。</summary>
    public int SourceChannels => _sourceChannels;

    /// <summary>系统回环用播放端点、麦克风用录音端点；角色分别是 eConsole / eCommunications。</summary>
    private int DataFlow => _kind == AudioCaptureKind.SystemLoopback ? DataFlowRender : DataFlowCapture;

    private int Role => _kind == AudioCaptureKind.SystemLoopback ? RoleConsole : RoleCommunications;

    // ===================== 启停 =====================

    /// <summary>
    /// 打开默认设备并启动后台采集线程。返回 false 时 <see cref="LastError"/> 有中文原因
    /// （没有设备 / 被独占 / 不支持的混音格式 / 权限被拒等），不抛异常。
    /// <para>可重复调用：重复 Start 会先停掉上一次再重新打开。</para>
    /// </summary>
    public bool Start()
    {
        lock (_lifecycleGate) return StartCore();
    }

    private bool StartCore()
    {
        // 先清掉上一次（可重复调用）；Stop 与本方法同锁重入，安全
        Stop();
        SetError(null);

        try
        {
            _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();

            var hr = _enumerator.GetDefaultAudioEndpoint(DataFlow, Role, out var device);
            if (hr != 0 || device is null) return FailAndStop(DescribeMissingEndpoint(hr));
            _device = device;

            device.GetId(out var deviceId);
            _deviceId = deviceId ?? "";

            var audioClientIid = typeof(IAudioClient).GUID;
            hr = device.Activate(ref audioClientIid, ClsCtxAll, IntPtr.Zero, out var clientObj);
            if (hr != 0 || clientObj is not IAudioClient client)
                return FailAndStop($"激活音频客户端失败（hr=0x{hr:X8}）");
            _client = client;

            hr = client.GetMixFormat(out var formatPtr);
            if (hr != 0 || formatPtr == IntPtr.Zero)
                return FailAndStop($"读取设备混音格式失败（hr=0x{hr:X8}）");

            try
            {
                ReadFormat(formatPtr);

                // 共享模式：回环要 AUDCLNT_STREAMFLAGS_LOOPBACK，麦克风不能加（加了打开不了录音端点），
                // 格式统一用设备自己的混音格式，避免重采样。
                var streamFlags = _kind == AudioCaptureKind.SystemLoopback ? StreamFlagsLoopback : 0;
                hr = client.Initialize(ShareModeShared, streamFlags, BufferDuration100Ms, 0, formatPtr, IntPtr.Zero);
                if (hr != 0)
                    return FailAndStop($"初始化音频采集失败（hr=0x{hr:X8}；设备可能被独占或格式不支持）");
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }

            _stopping = false;
            var thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = _kind == AudioCaptureKind.SystemLoopback ? "screenrec-wasapi-loopback" : "screenrec-wasapi-mic",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread = thread;
            thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            return FailAndStop((_kind == AudioCaptureKind.SystemLoopback ? "启动系统音频采集失败：" : "启动麦克风采集失败：") + ex.Message);
        }
    }

    /// <summary>停止采集并释放 COM 对象（可重复调用、没启动也安全）。</summary>
    public void Stop()
    {
        lock (_lifecycleGate) StopCore();
    }

    private void StopCore()
    {
        _stopping = true;

        // 先让 audio client 停止出数据；再 join 采集线程，确保没有回调在跑、最后一批数据已经读完。
        var client = _client;
        try { client?.Stop(); } catch { /* 已经停了 */ }

        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(ThreadJoinTimeout); } catch { /* 忽略 */ }
        }

        ReleaseCom();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    // ===================== 采集线程 =====================

    private void CaptureLoop()
    {
        var client = _client;
        if (client is null) return;

        try
        {
            var capIid = typeof(IAudioCaptureClient).GUID;
            var hr = client.GetService(ref capIid, out var captureObj);
            if (hr != 0 || captureObj is not IAudioCaptureClient capture)
            {
                FailInThread($"获取音频采集接口失败（hr=0x{hr:X8}）");
                return;
            }

            _capture = capture;
            hr = client.Start();
            if (hr != 0)
            {
                // 麦克风常见：隐私设置没放行（0x80070005）/ 被独占
                FailInThread($"启动音频采集失败（hr=0x{hr:X8}；设备可能被独占，或系统隐私设置不允许访问）");
                return;
            }

            var lastDeviceCheck = Environment.TickCount64;
            while (!_stopping)
            {
                capture.GetNextPacketSize(out var pending);
                if (pending == 0)
                {
                    Thread.Sleep(5);
                }
                else
                {
                    while (!_stopping)
                    {
                        if (!ReadPacket(capture)) return;
                        capture.GetNextPacketSize(out pending);
                        if (pending == 0) break;
                    }
                }

                // 每 2 秒确认一次默认设备还是不是打开时那个：切蓝牙 / 拔耳机 / 禁麦克风后，
                // 旧的采集端点会一直没数据，与其让用户以为在录，不如报错停下让界面提示重启。
                var now = Environment.TickCount64;
                if (now - lastDeviceCheck < DefaultDeviceCheckIntervalMs) continue;
                lastDeviceCheck = now;
                if (!DefaultDeviceChanged()) continue;

                FailInThread(_kind == AudioCaptureKind.SystemLoopback
                    ? "默认播放设备已切换（例如拔掉耳机 / 连接蓝牙），录制已停止，请重新开始录制"
                    : "默认麦克风设备已切换，录制已停止，请重新开始录制");
                return;
            }
        }
        catch (Exception ex)
        {
            FailInThread("音频采集线程异常：" + ex.Message);
        }
        finally
        {
            // 线程自己退出（出错 / 设备切换）时也把 client 停下来；Stop() 里再调一次是幂等的。
            try { client.Stop(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>读一包数据并转成 / 发布立体声 float。返回 false 表示线程应该退出。</summary>
    private bool ReadPacket(IAudioCaptureClient capture)
    {
        var hr = capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
        if (hr != 0 || frames == 0) return true;

        try
        {
            var needed = checked((int)frames * 2);
            if (_buffer.Length < needed) Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));

            if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
            {
                // 静音包也要发：保持时间轴连续，否则合成的音轨会和视频错位
                Array.Clear(_buffer, 0, needed);
            }
            else
            {
                ConvertToStereoFloat(data, (int)frames);
            }

            var handler = DataAvailable;
            if (handler is not null)
            {
                try
                {
                    handler(_buffer, (int)frames);
                }
                catch (Exception ex)
                {
                    // 订阅者（写 WAV / 分析音量）抛异常不能弄死采集线程：记录原因、停采集，由上层 Stop 收尾。
                    FailInThread("音频数据回调异常：" + ex.Message);
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            FailInThread("读取音频数据失败：" + ex.Message);
            return false;
        }
        finally
        {
            // 无论成功失败都要还缓冲，否则 WASAPI 很快会没有可用空间
            try { capture.ReleaseBuffer(frames); } catch { /* 忽略 */ }
        }
    }

    /// <summary>把设备原始数据转成 <see cref="_buffer"/> 里的立体声 float。</summary>
    private void ConvertToStereoFloat(IntPtr data, int frames)
    {
        var sourceSamples = frames * _sourceChannels;
        switch (_formatKind)
        {
            case AudioFormatKind.Float32:
            {
                if (_rawSamples.Length < sourceSamples) _rawSamples = new float[sourceSamples];
                Marshal.Copy(data, _rawSamples, 0, sourceSamples);
                MapToStereo(_rawSamples, frames);
                break;
            }

            case AudioFormatKind.Pcm16:
            {
                var bytes = new byte[sourceSamples * 2];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (_rawSamples.Length < sourceSamples) _rawSamples = new float[sourceSamples];
                for (var i = 0; i < sourceSamples; i++)
                    _rawSamples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                MapToStereo(_rawSamples, frames);
                break;
            }

            case AudioFormatKind.Pcm24:
            {
                var bytes = new byte[sourceSamples * 3];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (_rawSamples.Length < sourceSamples) _rawSamples = new float[sourceSamples];
                for (var i = 0; i < sourceSamples; i++)
                {
                    var value = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                    if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000); // 符号扩展
                    _rawSamples[i] = value / 8388608f;
                }

                MapToStereo(_rawSamples, frames);
                break;
            }

            case AudioFormatKind.Pcm32:
            {
                var bytes = new byte[sourceSamples * 4];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (_rawSamples.Length < sourceSamples) _rawSamples = new float[sourceSamples];
                for (var i = 0; i < sourceSamples; i++)
                    _rawSamples[i] = BitConverter.ToInt32(bytes, i * 4) / 2147483648f;
                MapToStereo(_rawSamples, frames);
                break;
            }
        }
    }

    /// <summary>多声道取前两路织成立体声；单声道复制到左右两路。</summary>
    private void MapToStereo(float[] source, int frames)
    {
        if (_sourceChannels >= 2)
        {
            for (var i = 0; i < frames; i++)
            {
                _buffer[i * 2] = source[i * _sourceChannels];
                _buffer[i * 2 + 1] = source[i * _sourceChannels + 1];
            }
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                var value = source[i];
                _buffer[i * 2] = value;
                _buffer[i * 2 + 1] = value;
            }
        }
    }

    /// <summary>当前默认设备是否已经和打开时不一样（查询失败按「没变」处理，避免误停）。</summary>
    private bool DefaultDeviceChanged()
    {
        try
        {
            var enumerator = _enumerator;
            if (enumerator is null) return false;

            var hr = enumerator.GetDefaultAudioEndpoint(DataFlow, Role, out var device);
            if (hr != 0 || device is null) return false;
            try
            {
                device.GetId(out var id);
                return !string.Equals(id, _deviceId, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            return false;
        }
    }

    // ===================== 格式解析 =====================

    private void ReadFormat(IntPtr formatPtr)
    {
        var format = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
        SampleRate = (int)format.nSamplesPerSec;
        _sourceChannels = Math.Max(1, (int)format.nChannels);

        var isFloat = format.wFormatTag == 3; // WAVE_FORMAT_IEEE_FLOAT
        if (format.wFormatTag == 0xFFFE)      // WAVE_FORMAT_EXTENSIBLE
        {
            if (format.cbSize < 22)
                throw new NotSupportedException("音频混音格式的扩展信息不完整，无法读取子格式");

            var ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(formatPtr);
            isFloat = ext.SubFormat == IeeeFloatSubFormat;
        }

        if (isFloat && format.wBitsPerSample == 32)
        {
            _formatKind = AudioFormatKind.Float32;
        }
        else if (format.wFormatTag == 1 || format.wFormatTag == 0xFFFE)
        {
            _formatKind = format.wBitsPerSample switch
            {
                16 => AudioFormatKind.Pcm16,
                24 => AudioFormatKind.Pcm24,
                32 => AudioFormatKind.Pcm32,
                _ => AudioFormatKind.Unsupported,
            };
        }
        else
        {
            _formatKind = AudioFormatKind.Unsupported;
        }

        if (_formatKind == AudioFormatKind.Unsupported)
            throw new NotSupportedException($"不支持的音频混音格式：tag={format.wFormatTag} bits={format.wBitsPerSample}");
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // ===================== 错误处理 =====================

    private string DescribeMissingEndpoint(int hr) => _kind == AudioCaptureKind.SystemLoopback
        ? $"没有找到默认播放设备（可能是没有可用的扬声器 / 耳机；hr=0x{hr:X8}）"
        : $"没有找到默认麦克风（请检查麦克风是否连接，或到 Windows 设置 → 隐私和安全性 → 麦克风 允许应用使用麦克风；hr=0x{hr:X8}）";

    /// <summary>Start 阶段失败：记录原因并释放已经拿到的 COM（此时还没有采集线程，释放安全）。</summary>
    private bool FailAndStop(string message)
    {
        SetError(message);
        Stop();
        return false;
    }

    /// <summary>采集线程里失败：只记录原因并让循环退出；COM 由之后调用方的 <see cref="Stop"/> 统一释放。</summary>
    private void FailInThread(string message)
    {
        SetError(message);
        _stopping = true;
    }

    private void SetError(string? message)
    {
        Volatile.Write(ref _lastError, message);
        if (message is not null) Trace.WriteLine(LogPrefix + message);
    }

    private void ReleaseCom()
    {
        try { if (_capture is not null) Marshal.ReleaseComObject(_capture); } catch { /* 忽略 */ }
        try { if (_client is not null) Marshal.ReleaseComObject(_client); } catch { /* 忽略 */ }
        try { if (_device is not null) Marshal.ReleaseComObject(_device); } catch { /* 忽略 */ }
        try { if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator); } catch { /* 忽略 */ }

        _capture = null;
        _client = null;
        _device = null;
        _enumerator = null;
        _deviceId = "";
    }

    // ===================== COM 互操作（顺序必须和 Windows 头文件一致） =====================

    private enum AudioFormatKind
    {
        Unsupported,
        Float32,
        Pcm16,
        Pcm24,
        Pcm32,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object interfacePtr);

        int OpenPropertyStore(int access, out IntPtr properties);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format,
            IntPtr audioSessionGuid);

        int GetBufferSize(out uint bufferSize);
        int GetStreamLatency(out long latency);
        int GetCurrentPadding(out uint padding);
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        int GetMixFormat(out IntPtr format);
        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        int Start();
        int Stop();
        int Reset();
        int SetEventHandle(IntPtr eventHandle);
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePtr);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        int ReleaseBuffer(uint frames);
        int GetNextPacketSize(out uint packetSize);
    }
}
