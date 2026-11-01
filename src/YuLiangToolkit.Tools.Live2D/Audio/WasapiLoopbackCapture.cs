using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.Live2D.Audio;

/// <summary>
/// 用 WASAPI **回环（loopback）** 抓取「系统正在播放的声音」（扬声器混音，不申请麦克风、不录音到磁盘）。
///
/// <para>为什么不用第三方库：Win 自带的 WASAPI 就够——共享模式 + <c>AUDCLNT_STREAMFLAGS_LOOPBACK</c>
/// 就能拿到默认播放设备的混音数据（48kHz 立体声浮点），再用少量 COM 接口包装即可，零新增 NuGet。</para>
///
/// <para><b>格式</b>：优先按设备混音格式取 float32；万一设备混音格式是 PCM16/24/32 也做转换，
/// 最终统一输出<b>立体声 interleaved float</b>（范围 -1~1）。<see cref="DataAvailable"/> 给的数组是复用的，
/// 订阅者必须当场消费（<c>MusicReactiveSource</c> 就是同步喂给分析器）。</para>
///
/// <para><b>线程</b>：<see cref="Start"/> 在调用线程做一次初始化（几十毫秒），之后由后台专用线程轮询采集；
/// <see cref="Stop"/> 停止线程并释放 COM 对象。</para>
/// </summary>
public sealed class WasapiLoopbackCapture : IDisposable
{
    private const int ShareModeShared = 0;
    private const int StreamFlagsLoopback = 0x00020000;
    private const int BufferFlagsSilent = 0x00000002;
    private const long BufferDuration100Ms = 1_000_000; // 100ns 单位
    private const int DataFlowRender = 0;
    private const int RoleConsole = 0;

    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private Thread? _thread;
    private volatile bool _stopping;

    private float[] _buffer = new float[48000 * 2 * 2]; // 0.5 秒的立体声空间，按需扩容
    private float[] _rawSamples = Array.Empty<float>();

    private AudioFormatKind _formatKind = AudioFormatKind.Float32;
    private int _sourceChannels = 2;

    /// <summary>采集到一批数据（参数：立体声 interleaved float 缓冲、帧数；缓冲会在下一次回调被复用）。</summary>
    public event Action<float[], int>? DataAvailable;

    /// <summary>默认播放设备变了（比如拔耳机 / 切蓝牙）：需要重启采集。</summary>
    public event EventHandler? DeviceChanged;

    /// <summary>采集中途出错（线程会停掉，需要重建）。</summary>
    public event EventHandler<string>? Failed;

    /// <summary>是否正在采集。</summary>
    public bool IsRunning => _thread is { IsAlive: true } && !_stopping;

    /// <summary>最近一次错误（空 = 没有错误）。</summary>
    public string? LastError { get; private set; }

    /// <summary>设备混音采样率（一般 48000；也可能 44100）。</summary>
    public int SampleRate { get; private set; }

    /// <summary>设备混音通道数（一般 2）。</summary>
    public int DeviceChannels => _sourceChannels;

    /// <summary>默认播放设备 id（调试 / 设备切换判断用）。</summary>
    public string DeviceId { get; private set; } = "";

    /// <summary>
    /// 打开默认播放设备的回环采集并启动后台线程。
    /// 返回 false 时 <see cref="LastError"/> 有原因（没有播放设备 / 被独占 / 不支持的格式）。
    /// </summary>
    public bool Start()
    {
        Stop();
        LastError = null;
        try
        {
            _enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();
            var hr = _enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out _device);
            if (hr != 0 || _device is null) return Fail($"没有找到默认播放设备（hr=0x{hr:X8}）");

            _device.GetId(out var deviceId);
            DeviceId = deviceId;

            var audioClientIid = typeof(IAudioClient).GUID;
            hr = _device.Activate(ref audioClientIid, 23 /*CLSCTX_ALL*/, IntPtr.Zero, out var clientObj);
            if (hr != 0 || clientObj is not IAudioClient client) return Fail($"激活音频客户端失败（hr=0x{hr:X8}）");
            _client = client;

            hr = client.GetMixFormat(out var formatPtr);
            if (hr != 0 || formatPtr == IntPtr.Zero) return Fail($"读取混音格式失败（hr=0x{hr:X8}）");
            try
            {
                ReadFormat(formatPtr);

                // 共享模式 + LOOPBACK：抓默认播放设备的混音；格式用设备自己的混音格式
                hr = client.Initialize(ShareModeShared, StreamFlagsLoopback, BufferDuration100Ms, 0, formatPtr, IntPtr.Zero);
                if (hr != 0) return Fail($"初始化回环采集失败（hr=0x{hr:X8}；设备可能被独占/格式不支持）");
            }
            finally
            {
                Marshal.FreeCoTaskMem(formatPtr);
            }

            _stopping = false;
            _thread = new Thread(CaptureLoop)
            {
                IsBackground = true,
                Name = "live2d-wasapi-loopback",
                Priority = ThreadPriority.AboveNormal,
            };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            return Fail("启动音频回环失败：" + ex.Message);
        }
    }

    private IntPtr formatPtrForInit = IntPtr.Zero;

    private void ReadFormat(IntPtr formatPtr)
    {
        var format = Marshal.PtrToStructure<WAVEFORMATEX>(formatPtr);
        SampleRate = (int)format.nSamplesPerSec;
        _sourceChannels = Math.Max(1, (int)format.nChannels);

        var isFloat = format.wFormatTag == 3; // WAVE_FORMAT_IEEE_FLOAT
        if (format.wFormatTag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
        {
            var ext = Marshal.PtrToStructure<WAVEFORMATEXTENSIBLE>(formatPtr);
            isFloat = ext.SubFormat == IeeeFloatSubFormat;
        }

        if (isFloat && format.wBitsPerSample == 32) _formatKind = AudioFormatKind.Float32;
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
        else _formatKind = AudioFormatKind.Unsupported;

        if (_formatKind == AudioFormatKind.Unsupported)
            throw new NotSupportedException($"不支持的音频混音格式：tag={format.wFormatTag} bits={format.wBitsPerSample}");
    }

    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    private bool Fail(string message)
    {
        LastError = message;
        Stop();
        Failed?.Invoke(this, message);
        return false;
    }

    private void CaptureLoop()
    {
        try
        {
            if (_client is null) return;
            var capIid = typeof(IAudioCaptureClient).GUID;
            var hr = _client.GetService(ref capIid, out var capObj);
            if (hr != 0 || capObj is not IAudioCaptureClient capture)
            {
                Fail($"获取采集客户端失败（hr=0x{hr:X8}）");
                return;
            }
            _capture = capture;
            hr = _client.Start();
            if (hr != 0) { Fail($"启动采集中失败（hr=0x{hr:X8}）"); return; }

            var pending = 0u;
            var lastDeviceCheck = Environment.TickCount64;
            while (!_stopping)
            {
                pending = 0;
                _capture.GetNextPacketSize(out pending);
                if (pending == 0)
                {
                    Thread.Sleep(5);
                }
                else
                {
                    while (!_stopping && pending > 0)
                    {
                        if (!ReadPacket()) return;
                        _capture.GetNextPacketSize(out pending);
                    }
                }

                // 每 2 秒检查一次默认播放设备是否变化（切蓝牙 / 拔耳机时 loopback 会失效）
                var now = Environment.TickCount64;
                if (now - lastDeviceCheck >= 2000)
                {
                    lastDeviceCheck = now;
                    if (DefaultDeviceChanged())
                    {
                        DeviceChanged?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fail("音频回环线程异常：" + ex.Message);
        }
    }

    private bool ReadPacket()
    {
        if (_capture is null) return false;
        var hr = _capture.GetBuffer(out var data, out var frames, out var flags, out _, out _);
        if (hr != 0 || frames == 0) return true;

        try
        {
            var needed = checked((int)frames * 2);
            if (_buffer.Length < needed) Array.Resize(ref _buffer, Math.Max(needed, _buffer.Length * 2));

            if ((flags & BufferFlagsSilent) != 0 || data == IntPtr.Zero)
            {
                Array.Clear(_buffer, 0, needed);
            }
            else
            {
                ConvertToStereoFloat(data, (int)frames);
            }

            DataAvailable?.Invoke(_buffer, (int)frames);
            return true;
        }
        catch (Exception ex)
        {
            Fail("读取音频数据失败：" + ex.Message);
            return false;
        }
        finally
        {
            _capture.ReleaseBuffer(frames);
        }
    }

    private void ConvertToStereoFloat(IntPtr data, int frames)
    {
        var sourceFrames = frames * _sourceChannels;
        switch (_formatKind)
        {
            case AudioFormatKind.Float32:
            {
                if (_rawSamples.Length < sourceFrames) _rawSamples = new float[sourceFrames];
                Marshal.Copy(data, _rawSamples, 0, sourceFrames);
                MapToStereo(_rawSamples, frames, 1.0f);
                break;
            }
            case AudioFormatKind.Pcm16:
            {
                var bytes = new byte[sourceFrames * 2];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (_rawSamples.Length < sourceFrames) _rawSamples = new float[sourceFrames];
                for (var i = 0; i < sourceFrames; i++)
                    _rawSamples[i] = BitConverter.ToInt16(bytes, i * 2) / 32768f;
                MapToStereo(_rawSamples, frames, 1.0f);
                break;
            }
            case AudioFormatKind.Pcm24:
            {
                var bytes = new byte[sourceFrames * 3];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (_rawSamples.Length < sourceFrames) _rawSamples = new float[sourceFrames];
                for (var i = 0; i < sourceFrames; i++)
                {
                    var v = bytes[i * 3] | (bytes[i * 3 + 1] << 8) | (bytes[i * 3 + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    _rawSamples[i] = v / 8388608f;
                }
                MapToStereo(_rawSamples, frames, 1.0f);
                break;
            }
            case AudioFormatKind.Pcm32:
            {
                var bytes = new byte[sourceFrames * 4];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                if (_rawSamples.Length < sourceFrames) _rawSamples = new float[sourceFrames];
                for (var i = 0; i < sourceFrames; i++)
                    _rawSamples[i] = BitConverter.ToInt32(bytes, i * 4) / 2147483648f;
                MapToStereo(_rawSamples, frames, 1.0f);
                break;
            }
        }
    }

    private void MapToStereo(float[] source, int frames, float gain)
    {
        if (_sourceChannels >= 2)
        {
            for (var i = 0; i < frames; i++)
            {
                _buffer[i * 2] = source[i * _sourceChannels] * gain;
                _buffer[i * 2 + 1] = source[i * _sourceChannels + 1] * gain;
            }
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                var v = source[i] * gain;
                _buffer[i * 2] = v;
                _buffer[i * 2 + 1] = v;
            }
        }
    }

    private bool DefaultDeviceChanged()
    {
        try
        {
            if (_enumerator is null) return false;
            var hr = _enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device);
            if (hr != 0 || device is null) return false;
            device.GetId(out var id);
            Marshal.ReleaseComObject(device);
            return !string.Equals(id, DeviceId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>停止采集并释放 COM 对象（可重复调用）。</summary>
    public void Stop()
    {
        _stopping = true;
        try { _client?.Stop(); } catch { }
        var thread = _thread;
        _thread = null;
        if (thread is not null && thread != Thread.CurrentThread)
        {
            try { thread.Join(TimeSpan.FromSeconds(1.5)); } catch { }
        }

        try { if (_capture is not null) Marshal.ReleaseComObject(_capture); } catch { }
        try { if (_client is not null) Marshal.ReleaseComObject(_client); } catch { }
        try { if (_device is not null) Marshal.ReleaseComObject(_device); } catch { }
        try { if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator); } catch { }
        _capture = null;
        _client = null;
        _device = null;
        _enumerator = null;
    }

    public void Dispose() => Stop();

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
