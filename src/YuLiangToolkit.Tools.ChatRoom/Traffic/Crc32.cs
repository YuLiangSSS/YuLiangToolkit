namespace YuLiangToolkit.Tools.ChatRoom.Traffic;

/// <summary>
/// CRC-32（IEEE 多项式，和 zip / PNG 用的同一条）：文件传完之后对一对，
/// 传输过程中少字节 / 串位能立刻发现。<b>纯计算，自检直接断言标准向量。</b>
///
/// <para>为什么用 CRC32 而不是 SHA-256：几 GB 的文件也要边传边算，
/// CRC32 可以用查表法流式算完（几乎不影响传输速度）；SHA-256 留给元数据里的可选字段。</para>
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var value = i;
            for (var bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;
            table[i] = value;
        }
        return table;
    }

    /// <summary>继续累加计算（可以分块喂进来）。</summary>
    public static uint Append(uint current, ReadOnlySpan<byte> data)
    {
        var crc = current;
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc;
    }

    /// <summary>一整块算完。</summary>
    public static uint Compute(ReadOnlySpan<byte> data) => ~Append(0xFFFFFFFFu, data);

    /// <summary>内部状态初值（分块计算时用 <c>state = Crc32.Begin</c>，最后 <c>Finish(state)</c>）。</summary>
    public const uint Begin = 0xFFFFFFFFu;

    /// <summary>把内部状态收尾成最终 CRC（分块计算的最后一步）。</summary>
    public static uint Finish(uint state) => ~state;
}
