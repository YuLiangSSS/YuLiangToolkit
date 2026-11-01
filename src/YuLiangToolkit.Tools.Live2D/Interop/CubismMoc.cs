using System.Runtime.InteropServices;
using System.Text;

namespace YuLiangToolkit.Tools.Live2D.Interop;

/// <summary>
/// 一个已加载的 moc3 模型数据（Cubism Core 的 <c>csmMoc</c>）。
///
/// <para>moc3 数据必须放在 <b>64 字节对齐</b>的原生内存里再交给 Core；整个生命周期内不能移动 / 释放。
/// 本类用 <see cref="NativeMemory.AlignedAlloc"/> 分配，<see cref="Dispose"/> 时释放。
/// 一个 <see cref="CubismMoc"/> 可以创建多个 <see cref="CubismModel"/>（这里只用一个）。</para>
/// </summary>
public sealed unsafe class CubismMoc : IDisposable
{
    private const int MocAlignment = 64;

    private byte* _buffer;
    private readonly int _length;
    private bool _disposed;

    private CubismMoc(byte* buffer, int length, IntPtr handle)
    {
        _buffer = buffer;
        _length = length;
        Handle = handle;
    }

    /// <summary>Core 里的 moc 指针（<c>csmMoc*</c>）。</summary>
    public IntPtr Handle { get; }

    /// <summary>moc3 文件长度（字节）。</summary>
    public int Length => _length;

    /// <summary>moc3 文件格式版本（1 = Cubism 3.0，2 = 3.3，3 = 4.0，4 = 4.2，5 = 5.0+）。</summary>
    public uint MocVersion { get; private init; }

    /// <summary>加载时 Core 的一致性校验结果（true = moc 数据完好）。</summary>
    public bool IsConsistent { get; private init; }

    /// <summary>从 moc3 文件加载。</summary>
    public static CubismMoc Load(string mocPath)
    {
        if (string.IsNullOrWhiteSpace(mocPath)) throw new ArgumentException("moc3 路径为空", nameof(mocPath));
        if (!File.Exists(mocPath)) throw new FileNotFoundException("找不到 moc3 文件", mocPath);
        return FromBytes(File.ReadAllBytes(mocPath));
    }

    /// <summary>从内存字节加载（会复制进 64 字节对齐的原生缓冲区）。</summary>
    public static CubismMoc FromBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 8) throw new InvalidDataException("moc3 文件太小，不是合法的 moc3");

        var buffer = (byte*)NativeMemory.AlignedAlloc((nuint)bytes.Length, MocAlignment);
        try
        {
            bytes.AsSpan().CopyTo(new Span<byte>(buffer, bytes.Length));

            var version = CubismCore.csmGetMocVersion((IntPtr)buffer, (uint)bytes.Length);
            var consistent = CubismCore.csmHasMocConsistency((IntPtr)buffer, (uint)bytes.Length) != 0;
            var handle = CubismCore.csmReviveMocInPlace((IntPtr)buffer, (uint)bytes.Length);
            if (handle == IntPtr.Zero)
            {
                throw new InvalidDataException(
                    $"moc3 复活失败（格式版本 {version}，Core 支持到 {CubismCore.csmGetLatestMocVersion()}）；" +
                    "多半是 Core 版本比 moc3 旧，或文件损坏");
            }

            return new CubismMoc(buffer, bytes.Length, handle)
            {
                MocVersion = version,
                IsConsistent = consistent,
            };
        }
        catch
        {
            NativeMemory.AlignedFree(buffer);
            throw;
        }
    }

    /// <summary>moc 版本的展示文本（"Cubism 5" / "Cubism 4.2" …）。</summary>
    public static string DescribeMocVersion(uint version) => version switch
    {
        1 => "Cubism 3.0 ~ 3.2",
        2 => "Cubism 3.3",
        3 => "Cubism 4.0 ~ 4.1",
        4 => "Cubism 4.2",
        5 => "Cubism 5.0+",
        _ => $"未知（{version}）",
    };

    /// <summary>Core 库版本展示文本（例如 "5.0.0"）。</summary>
    public static string GetCoreVersionText()
    {
        var version = CubismCore.csmGetVersion();
        // 高 8 位主版本 / 次 8 位次版本 / 再 8 位补丁号，最低 8 位保留
        var major = (version >> 24) & 0xFF;
        var minor = (version >> 16) & 0xFF;
        var patch = (version >> 8) & 0xFF;
        return $"{major}.{minor}.{patch}";
    }

    /// <summary>直接用 Core 读一个 moc3 文件的版本（不完整加载）。</summary>
    public static uint ReadMocVersion(string mocPath)
    {
        var header = new byte[64];
        using var stream = File.OpenRead(mocPath);
        var read = stream.Read(header, 0, header.Length);
        if (read < 64) throw new InvalidDataException("moc3 文件太小");
        var headerText = Encoding.ASCII.GetString(header, 0, 4);
        if (headerText != "MOC3") throw new InvalidDataException("不是 moc3 文件（缺少 MOC3 头）");
        return (uint)header[4];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer is not null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }
}
