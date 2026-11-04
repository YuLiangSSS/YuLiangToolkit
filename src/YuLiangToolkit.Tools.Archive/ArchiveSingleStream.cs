using SharpCompress.Compressors;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Lzw;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;

namespace YuLiangToolkit.Tools.Archive;

// =====================================================================================
// 「单文件压缩」（GZ / BZ2 / XZ / ZST / LZW）的流工具（纯逻辑，不引用 WPF）。
//
// SharpCompress 的 ReaderFactory 只认「压缩过的 TAR」和 GZIP；对单独一个
// .bz2 / .xz / .zst / .lzw 文件会抛 InvalidFormatException，所以这里直接用
// SharpCompress 的压缩流解压，输出文件名退回压缩包文件名（GetBaseName）。
// =====================================================================================

internal static class ArchiveSingleStream
{
    /// <summary>按格式创建解压流（不负责关闭 <paramref name="source"/>）。</summary>
    public static Stream Create(Stream source, ArchiveFormat format)
        => format switch
        {
            ArchiveFormat.GZip => new System.IO.Compression.GZipStream(
                source, System.IO.Compression.CompressionMode.Decompress, leaveOpen: true),
            ArchiveFormat.BZip2 => BZip2Stream.Create(
                source, CompressionMode.Decompress, decompressConcatenated: true, leaveOpen: true),
            ArchiveFormat.Xz => new XZStream(source),
            ArchiveFormat.Zstd => new DecompressionStream(source),
            ArchiveFormat.TarLz => LZipStream.Create(source, CompressionMode.Decompress, leaveOpen: true),
            // .lz / .lzw 的实际情况比较杂：.lz 常见是 lzip（LZIP 魔数），.lzw 才是 LZW。
            // 这里按魔数分流，避免拿着 LZW 解压器去解 lzip 数据。
            ArchiveFormat.Lzw => LooksLikeLZip(source)
                ? LZipStream.Create(source, CompressionMode.Decompress, leaveOpen: true)
                : new LzwStream(source),
            _ => throw new NotSupportedException($"不支持的单文件压缩格式：{format}"),
        };

    /// <summary>读文件头判断是不是 lzip（LZIP）数据；会尽量把流位置复位。</summary>
    private static bool LooksLikeLZip(Stream source)
    {
        try
        {
            if (!source.CanSeek)
            {
                return false;
            }

            var position = source.Position;
            Span<byte> magic = stackalloc byte[4];
            var read = source.Read(magic);
            source.Position = position;
            return read == 4 && magic[0] == 0x4C && magic[1] == 0x5A && magic[2] == 0x49 && magic[3] == 0x50;
        }
        catch
        {
            return false;
        }
    }
}
