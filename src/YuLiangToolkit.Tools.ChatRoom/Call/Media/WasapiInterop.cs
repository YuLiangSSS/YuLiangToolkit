using System.Runtime.InteropServices;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

// ============================================================================
// 通话设备层的 WASAPI COM 互操作（全部手写，零 NuGet 包）
//
// 为什么手写：项目对 NuGet 的态度是「能手写的小轮子就手写」（AGENTS.md 硬性规则 2），
// 而通话需要的 WASAPI 接口只有下面这几个；自己写能把「线程 / 释放 / 格式」全部握在手里，
// 也不给这个功能引入第三方依赖。
//
// 设计约束（改之前先读）：
//   · 方法签名必须和 Windows 头文件里的 **虚表顺序** 完全一致：多一个 / 少一个 / 换一个顺序，
//     调用都会落到错误的槽位上（表现为崩溃或莫名其妙的数据）。只声明用得到的方法时，
//     必须保持它们在前面的相对顺序。
//   · 每个方法都标了 [PreserveSig]：这样返回值就是原始 HRESULT，调用方按 `hr != 0` 判失败，
//     不会被运行时翻译成 COMException（否则拿不到错误码，也不好写中文失败原因）。
//   · COM 对象「谁创建谁释放」，统一用 [WasapiInterop.Release]；释放失败忽略（进程退出兜底）。
//     注意：ReleaseComObject 只减引用计数，之后不要再使用该变量（本项目里都是置 null）。
//   · PROPVARIANT 在 x64 上是 24 字节（8 字节头 + 16 字节联合），x86 上是 16 字节。
//     这里用 IntPtr 传原始内存 + PtrToStructure 读前 16 字节，避免 marshaller 按 managed
//     结构体大小分配 native 缓冲、导致原生代码写出 8 字节覆盖邻居内存。
//   · 音频格式：只支持 float32 / PCM16 / PCM24 / PCM32（共享模式的混音格式 99% 是 float32，
//     其它几种留给「声卡控制面板把格式改成 16 位」的用户）；不认识的格式在启动时报中文原因。
//   · 本文件只跑 Windows（整个通话设备层都是）。
// ============================================================================

/// <summary>
/// WASAPI（Windows 音频会话 API）的常量、COM 工厂与格式解析。
/// <para>只给 <see cref="AudioDeviceCatalog"/> / <see cref="MicrophoneCapture"/> /
/// <see cref="SpeakerOutput"/> 用（都是 internal），不参与通话协议，也不被界面直接引用。</para>
/// </summary>
internal static class WasapiInterop
{
    // ---------- 常量（值来自 Windows SDK 的 audioclient.h / mmdeviceapi.h / propkey.h） ----------

    /// <summary>数据流方向：播放（Render / 扬声器）。</summary>
    internal const int DataFlowRender = 0;

    /// <summary>数据流方向：采集（Capture / 麦克风）。</summary>
    internal const int DataFlowCapture = 1;

    /// <summary>角色：Console（系统默认设备按「常规」角色取即可）。</summary>
    internal const int RoleConsole = 0;

    /// <summary>端点状态：DEVICE_STATE_ACTIVE，只枚举当前可用的设备。</summary>
    internal const int DeviceStateActive = 1;

    /// <summary>共享模式（AUDCLNT_SHAREMODE_SHARED）：和系统 / 其它程序一起用设备，不会独占。</summary>
    internal const int ShareModeShared = 0;

    /// <summary>CLSCTX_ALL：激活 COM 组件时允许所有上下文（进程内 / 本地服务都可以）。</summary>
    internal const int ClsCtxAll = 23;

    /// <summary>缓冲时长：100 ms（单位 100ns）。共享模式必须 ≥ 设备默认周期。</summary>
    internal const long BufferDuration100Ms = 1_000_000;

    /// <summary>STGM_READ：只读属性存储。</summary>
    internal const int StgmRead = 0;

    /// <summary>PKEY_Device_FriendlyName 的 fmtid（{a45c254e-df1c-4efd-8020-67d146a850e0}）。</summary>
    private static readonly Guid FriendlyNameFormatId = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    /// <summary>PKEY_Device_FriendlyName 的 pid。</summary>
    private const uint FriendlyNamePropertyId = 14;

    /// <summary>PKEY_Device_FriendlyName：属性存储里读友好名用。</summary>
    internal static PropertyKey DeviceFriendlyNameKey => new()
    {
        FormatId = FriendlyNameFormatId,
        PropertyId = FriendlyNamePropertyId,
    };

    // ---------- 工厂 / 释放 / 属性读取 ----------

    /// <summary>创建 MMDeviceEnumerator（WASAPI 的一切都从它开始）。</summary>
    internal static IMMDeviceEnumerator CreateDeviceEnumerator()
        => (IMMDeviceEnumerator)(object)new MMDeviceEnumeratorComObject();

    /// <summary>释放一个 COM 对象（null / 非 COM 对象 / 已释放都安全）。</summary>
    internal static void Release(object? comObject)
    {
        if (comObject is null) return;
        try
        {
            if (Marshal.IsComObject(comObject)) Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 释放阶段不抛异常：最坏情况是进程退出时系统兜底回收。
        }
    }

    /// <summary>
    /// 读设备友好名（PKEY_Device_FriendlyName）。读不到返回 null，由调用方用 Id 尾段兜底。
    /// <para>线程：调用方线程上执行；内部打开的 IPropertyStore 一定释放。</para>
    /// </summary>
    internal static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(StgmRead, out store) != 0 || store is null) return null;

            var key = DeviceFriendlyNameKey;
            // x64 PROPVARIANT 是 24 字节；按原生大小分配，原生代码写 24 字节也不会越过我们的缓冲。
            var native = Marshal.AllocCoTaskMem(24);
            try
            {
                if (store.GetValue(ref key, native) != 0) return null;
                var variant = Marshal.PtrToStructure<PropVariant>(native);
                try
                {
                    if (variant.vt != PropVariant.VtLpwStr || variant.pointerValue == IntPtr.Zero) return null;
                    return Marshal.PtrToStringUni(variant.pointerValue);
                }
                finally
                {
                    // VT_LPWSTR 的字符串是 CoTaskMem 分配的，统一交给 PropVariantClear 释放。
                    try { PropVariantClear(native); } catch { }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(native);
            }
        }
        catch
        {
            // 属性存储失败（驱动不提供 / 设备被占用）不影响枚举：调用方会用 Id 兜底。
            return null;
        }
        finally
        {
            Release(store);
        }
    }

    /// <summary>释放 PROPVARIANT 内部资源（ole32 自带，按类型正确释放）。</summary>
    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(IntPtr propVariant);

    // ---------- 混音格式解析 ----------

    /// <summary>
    /// 从 <c>IAudioClient.GetMixFormat</c> 返回的原生指针解析出设备混音格式。
    /// <para>不认识的格式直接抛 <see cref="NotSupportedException"/>（消息是中文，给用户看）。</para>
    /// </summary>
    internal static WasapiMixFormat ReadMixFormat(IntPtr formatPtr)
    {
        if (formatPtr == IntPtr.Zero) throw new ArgumentException("混音格式指针为空", nameof(formatPtr));

        var format = Marshal.PtrToStructure<WaveFormatEx>(formatPtr);
        var sampleRate = (int)format.nSamplesPerSec;
        var channels = Math.Max(1, (int)format.nChannels);
        var blockAlign = (int)format.nBlockAlign;

        var isFloat = format.wFormatTag == WaveFormatIeeeFloatTag;
        if (format.wFormatTag == WaveFormatExtensibleTag)
        {
            var ext = Marshal.PtrToStructure<WaveFormatExtensible>(formatPtr);
            isFloat = ext.SubFormat == IeeeFloatSubFormat;
        }

        WasapiSampleFormat kind;
        if (isFloat && format.wBitsPerSample == 32)
        {
            kind = WasapiSampleFormat.Float32;
        }
        else if (format.wFormatTag == WaveFormatPcmTag || format.wFormatTag == WaveFormatExtensibleTag)
        {
            kind = format.wBitsPerSample switch
            {
                16 => WasapiSampleFormat.Pcm16,
                24 => WasapiSampleFormat.Pcm24,
                32 => WasapiSampleFormat.Pcm32,
                _ => WasapiSampleFormat.Unsupported,
            };
        }
        else
        {
            kind = WasapiSampleFormat.Unsupported;
        }

        if (kind == WasapiSampleFormat.Unsupported)
        {
            throw new NotSupportedException(
                $"不支持的音频混音格式：tag=0x{format.wFormatTag:X4} bits={format.wBitsPerSample}" +
                "（通话只支持 float32 / PCM16 / PCM24 / PCM32）");
        }

        if (blockAlign <= 0) blockAlign = channels * Math.Max(1, format.wBitsPerSample / 8);
        return new WasapiMixFormat(sampleRate, channels, blockAlign, kind);
    }

    // ---------- 原生格式结构体（顺序 / Pack 必须和 Windows 头文件一致） ----------

    /// <summary>WAVE_FORMAT_PCM。</summary>
    private const ushort WaveFormatPcmTag = 1;

    /// <summary>WAVE_FORMAT_IEEE_FLOAT。</summary>
    private const ushort WaveFormatIeeeFloatTag = 3;

    /// <summary>WAVE_FORMAT_EXTENSIBLE（16 位 / 24 位 PCM 和部分 float 会包在这个壳里）。</summary>
    private const ushort WaveFormatExtensibleTag = 0xFFFE;

    /// <summary>IEEE float 的 SubFormat GUID（WAVEFORMATEXTENSIBLE 里判断是不是 float）。</summary>
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// WAVEFORMATEX。注意 <c>Pack = 2</c>：头文件里是 2 字节对齐（18 字节），
    /// 用默认 8 字节对齐会多出 6 字节填充，解析出的字段就全错位了。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    /// <summary>WAVEFORMATEXTENSIBLE：在 WAVEFORMATEX 后面追加 2 + 4 + 16 字节（共 40 字节）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatExtensible
    {
        public WaveFormatEx Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }
}

// ============================================================================
// 音频样本格式 / 转换
// ============================================================================

/// <summary>设备混音格式的样本类型；<see cref="Unsupported"/> 表示启动时就该失败。</summary>
internal enum WasapiSampleFormat
{
    Unsupported = 0,
    Float32 = 1,
    Pcm16 = 2,
    Pcm24 = 3,
    Pcm32 = 4,
}

/// <summary>设备混音格式（采样率 / 通道数 / 每帧字节数 / 样本类型）。</summary>
internal readonly struct WasapiMixFormat
{
    public WasapiMixFormat(int sampleRate, int channels, int blockAlign, WasapiSampleFormat format)
    {
        SampleRate = sampleRate;
        Channels = channels;
        BlockAlign = blockAlign;
        Format = format;
    }

    /// <summary>采样率（一般 48000 / 44100）。</summary>
    public int SampleRate { get; }

    /// <summary>设备通道数（一般 1 / 2；7.1 声卡可能 8）。</summary>
    public int Channels { get; }

    /// <summary>每帧字节数（nBlockAlign）。</summary>
    public int BlockAlign { get; }

    /// <summary>样本类型。</summary>
    public WasapiSampleFormat Format { get; }
}

/// <summary>
/// 设备原生缓冲与「interleaved float（-1~1）」之间的转换器。
/// <para><b>为什么单独做成对象</b>：音频线程每 5~10 ms 就要转一次，不能每包 new 数组；
/// 内部缓冲在第一次用到时分配、之后复用（每个采集 / 播放器各持一个实例，线程安全由各自动保证）。</para>
/// <para>线程：一个实例只被它所属的采集 / 播放线程使用，不跨线程共享。</para>
/// </summary>
internal sealed class WasapiSampleConverter
{
    private readonly WasapiMixFormat _format;
    private float[] _floatBuffer = Array.Empty<float>();
    private short[] _shortBuffer = Array.Empty<short>();
    private int[] _intBuffer = Array.Empty<int>();
    private byte[] _byteBuffer = Array.Empty<byte>();

    public WasapiSampleConverter(WasapiMixFormat format) => _format = format;

    /// <summary>设备混音格式。</summary>
    public WasapiMixFormat Format => _format;

    /// <summary>
    /// 把设备缓冲解码成 interleaved float（通道顺序和设备一致），返回样本总数（帧数 × 通道数）。
    /// <para>返回的数组是复用的，订阅方必须当场消费。</para>
    /// </summary>
    public int Decode(IntPtr source, int frameCount, out float[] samples)
    {
        var sampleCount = checked(Math.Max(0, frameCount) * _format.Channels);
        switch (_format.Format)
        {
            case WasapiSampleFormat.Float32:
            {
                Ensure(ref _floatBuffer, sampleCount);
                if (sampleCount > 0) Marshal.Copy(source, _floatBuffer, 0, sampleCount);
                break;
            }
            case WasapiSampleFormat.Pcm16:
            {
                Ensure(ref _shortBuffer, sampleCount);
                Ensure(ref _floatBuffer, sampleCount);
                if (sampleCount > 0) Marshal.Copy(source, _shortBuffer, 0, sampleCount);
                for (var i = 0; i < sampleCount; i++) _floatBuffer[i] = _shortBuffer[i] / 32768f;
                break;
            }
            case WasapiSampleFormat.Pcm24:
            {
                var byteCount = checked(sampleCount * 3);
                Ensure(ref _byteBuffer, byteCount);
                Ensure(ref _floatBuffer, sampleCount);
                if (byteCount > 0) Marshal.Copy(source, _byteBuffer, 0, byteCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    var offset = i * 3;
                    var value = _byteBuffer[offset] | (_byteBuffer[offset + 1] << 8) | (_byteBuffer[offset + 2] << 16);
                    if ((value & 0x800000) != 0) value |= unchecked((int)0xFF000000);
                    _floatBuffer[i] = value / 8388608f;
                }
                break;
            }
            case WasapiSampleFormat.Pcm32:
            {
                Ensure(ref _intBuffer, sampleCount);
                Ensure(ref _floatBuffer, sampleCount);
                if (sampleCount > 0) Marshal.Copy(source, _intBuffer, 0, sampleCount);
                for (var i = 0; i < sampleCount; i++) _floatBuffer[i] = _intBuffer[i] / 2147483648f;
                break;
            }
            default:
            {
                Ensure(ref _floatBuffer, sampleCount);
                if (sampleCount > 0) Array.Clear(_floatBuffer, 0, sampleCount);
                break;
            }
        }

        samples = _floatBuffer;
        return sampleCount;
    }

    /// <summary>
    /// 把 interleaved float（长度按设备通道数排好）编码进设备缓冲。
    /// <para>调用方负责把数据整理成「设备通道布局」；这里只管按样本类型写原生内存。</para>
    /// </summary>
    public void Encode(IntPtr destination, float[] interleaved, int sampleCount)
    {
        var count = Math.Min(Math.Max(0, sampleCount), interleaved.Length);
        if (count == 0) return;

        switch (_format.Format)
        {
            case WasapiSampleFormat.Float32:
                Marshal.Copy(interleaved, 0, destination, count);
                break;
            case WasapiSampleFormat.Pcm16:
            {
                Ensure(ref _shortBuffer, count);
                for (var i = 0; i < count; i++)
                {
                    var value = ClampSample(interleaved[i]);
                    _shortBuffer[i] = (short)(value * 32767f);
                }
                Marshal.Copy(_shortBuffer, 0, destination, count);
                break;
            }
            case WasapiSampleFormat.Pcm24:
            {
                Ensure(ref _byteBuffer, count * 3);
                for (var i = 0; i < count; i++)
                {
                    var value = (int)(ClampSample(interleaved[i]) * 8388607f) & 0xFFFFFF;
                    var offset = i * 3;
                    _byteBuffer[offset] = (byte)value;
                    _byteBuffer[offset + 1] = (byte)(value >> 8);
                    _byteBuffer[offset + 2] = (byte)(value >> 16);
                }
                Marshal.Copy(_byteBuffer, 0, destination, count * 3);
                break;
            }
            case WasapiSampleFormat.Pcm32:
            {
                Ensure(ref _intBuffer, count);
                for (var i = 0; i < count; i++)
                    _intBuffer[i] = (int)(ClampSample(interleaved[i]) * 2147483647f);
                Marshal.Copy(_intBuffer, 0, destination, count);
                break;
            }
            default:
                // 启动阶段已经拒绝不支持的格式，这里不会走到；保守起见写静音。
                Ensure(ref _byteBuffer, count * 4);
                Array.Clear(_byteBuffer, 0, count * 4);
                Marshal.Copy(_byteBuffer, 0, destination, count * 4);
                break;
        }
    }

    /// <summary>浮点样本限幅到 [-1, 1]（引擎混音后仍可能略超出，直接乘会溢出成噪音）。</summary>
    private static float ClampSample(float value)
    {
        if (value > 1f) return 1f;
        if (value < -1f) return -1f;
        return value;
    }

    /// <summary>把缓冲扩到至少 count 个元素（只增不减，避免音频线程反复分配）。</summary>
    private static void Ensure<T>(ref T[] buffer, int count)
    {
        if (buffer.Length >= count) return;
        buffer = new T[Math.Max(count, buffer.Length * 2)];
    }
}

// ============================================================================
// COM 结构体
// ============================================================================

/// <summary>
/// PROPERTYKEY：{ GUID fmtid; DWORD pid; }。
/// <para><c>Pack = 4</c> 让结构体在 x64 上也是 20 字节（默认 8 字节对齐会带 4 字节尾巴），
/// 并且接口里用 <c>ref</c> 传（原生是 REFPROPERTYKEY，即指针），不按值传更稳。</para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

/// <summary>
/// PROPVARIANT 的「头 + 指针」部分。原生结构在 x64 上是 24 字节，这里只声明前 16 字节，
/// 实际内存由调用方按 24 字节分配（<see cref="WasapiInterop.ReadFriendlyName"/>），
/// <see cref="IPropertyStore.GetValue"/> 用 IntPtr 传地址，避免 marshaller 按 managed 大小分配。
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal struct PropVariant
{
    /// <summary>VT_LPWSTR（宽字符串）。</summary>
    internal const ushort VtLpwStr = 31;

    [FieldOffset(0)] public ushort vt;
    [FieldOffset(2)] public ushort wReserved1;
    [FieldOffset(4)] public ushort wReserved2;
    [FieldOffset(6)] public ushort wReserved3;

    /// <summary>联合体：常见类型里我们只关心 VT_LPWSTR 的字符串指针。</summary>
    [FieldOffset(8)] public IntPtr pointerValue;
}

// ============================================================================
// COM 接口（⚠ 方法顺序 = 原生虚表顺序，不能乱）
// ============================================================================

/// <summary>MMDeviceEnumerator 的类厂 CLSID：{BCDE0395-E52F-467C-8E3D-C4579291692E}。</summary>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}

/// <summary>IMMDeviceEnumerator：枚举端点 / 取默认设备 / 按 Id 取设备。</summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    /// <summary>按数据流方向枚举当前 ACTIVE 的端点。</summary>
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);

    /// <summary>取某个数据流方向的系统默认端点（例如默认麦克风 / 默认扬声器）。</summary>
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

    /// <summary>按端点 Id 取设备（设备被拔掉 / Id 失效时返回失败 HRESULT）。</summary>
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
}

/// <summary>IMMDeviceCollection：端点集合（先 GetCount 再 Item）。</summary>
[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int Item(int index, out IMMDevice? device);
}

/// <summary>IMMDevice：一个音频端点设备。</summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>激活设备上的某个接口（IAudioClient 等）；<paramref name="iid"/> 按 ref 传。</summary>
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePtr);

    /// <summary>打开只读属性存储（友好名、设备描述等）。</summary>
    [PreserveSig]
    int OpenPropertyStore(int access, out IPropertyStore? properties);

    /// <summary>取端点 Id（WASAPI 设备 Id 字符串，界面里持久化用的就是它）。</summary>
    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string? id);

    /// <summary>取端点状态（ACTIVE / DISABLED / NOTPRESENT / UNPLUGGED）。</summary>
    [PreserveSig]
    int GetState(out int state);
}

/// <summary>IPropertyStore：设备属性存储（只读用 GetValue 读友好名）。</summary>
[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetAt(int index, out PropertyKey key);

    /// <summary>读一个属性值。原生 PROPVARIANT* 用 IntPtr 传（见 <see cref="PropVariant"/> 的说明）。</summary>
    [PreserveSig]
    int GetValue(ref PropertyKey key, IntPtr value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

/// <summary>IAudioClient：音频客户端（初始化 / 启停 / 取服务接口）。</summary>
[ComImport]
[Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    /// <summary>初始化音频流。共享模式必须传设备的混音格式（GetMixFormat 拿到的那份）。</summary>
    [PreserveSig]
    int Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity,
        IntPtr format, IntPtr audioSessionGuid);

    [PreserveSig]
    int GetBufferSize(out uint bufferSize);

    [PreserveSig]
    int GetStreamLatency(out long latency);

    /// <summary>当前缓冲里已排队 / 可读的帧数（播放时用来算还能写多少）。</summary>
    [PreserveSig]
    int GetCurrentPadding(out uint padding);

    [PreserveSig]
    int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

    /// <summary>取设备混音格式（CoTaskMem 分配，调用方必须 <c>Marshal.FreeCoTaskMem</c>）。</summary>
    [PreserveSig]
    int GetMixFormat(out IntPtr format);

    [PreserveSig]
    int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

    [PreserveSig]
    int Start();

    [PreserveSig]
    int Stop();

    [PreserveSig]
    int Reset();

    [PreserveSig]
    int SetEventHandle(IntPtr eventHandle);

    /// <summary>取采集 / 播放客户端（IID 用 typeof(...).GUID）。</summary>
    [PreserveSig]
    int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePtr);
}

/// <summary>IAudioCaptureClient：共享模式采集，按包读数据。</summary>
[ComImport]
[Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    /// <summary>取一包数据；<paramref name="flags"/> 里可能有 SILENT / DATA_DISCONTINUITY。</summary>
    [PreserveSig]
    int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition,
        out ulong qpcPosition);

    /// <summary>还回这一包（不还的话下次读不到新数据）。</summary>
    [PreserveSig]
    int ReleaseBuffer(uint frames);

    /// <summary>当前还有多少帧可读（0 = 先睡一会儿）。</summary>
    [PreserveSig]
    int GetNextPacketSize(out uint packetSize);
}

/// <summary>IAudioRenderClient：共享模式播放，按帧写数据。</summary>
[ComImport]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    /// <summary>申请一块可写缓冲（<paramref name="data"/> 指向 <paramref name="numFramesRequested"/> 帧的空间）。</summary>
    [PreserveSig]
    int GetBuffer(uint numFramesRequested, out IntPtr data);

    /// <summary>提交已写的帧数（flags = 0 表示都是有效数据）。</summary>
    [PreserveSig]
    int ReleaseBuffer(uint numFramesWritten, uint flags);
}
