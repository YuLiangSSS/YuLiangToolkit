using SharpCompress.Common;
using SharpCompress.Readers;

namespace YuLiangToolkit.Tools.Archive;

// =====================================================================================
// 压缩包文件头识别（纯逻辑，不引用 WPF）。
//
// 只读文件开头，识别不出 / 读不了都返回 Unknown，不抛异常：
//   ZIP(PK\x03\x04 / PK\x05\x06 / PK\x07\x08)、RAR(Rar!\x1A\x07)、7Z(7z\xBC\xAF\x27\x1C)、
//   GZ(1F 8B)、BZ2(BZh)、XZ(FD 37 7A 58 5A 00)、ZST(28 B5 2F FD)、LZIP、TAR(offset 257 ustar)、
//   ACE(**ACE**)、ARJ(60 EA)。
// 压缩流（GZ / BZ2 / XZ / ZST / LZIP）会再用 ReaderFactory 打开一次，
// 看容器里是不是 TAR，从而区分「.tar.gz / .tar.bz2 ...」和单文件压缩。
// 注意：SharpCompress 自己写的 TAR 头不一定带 "ustar" 魔数（offset 257 是 0），
// 所以压缩版 TAR 不能只看解压头，必须让 ReaderFactory 判断容器类型。
// =====================================================================================

internal static class ArchiveContentDetector
{
    /// <summary>读文件头判断格式；读不了 / 认不出返回 Unknown。</summary>
    public static ArchiveFormat Detect(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return ArchiveFormat.Unknown;

            var head = new byte[512];
            int length;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                length = 0;
                while (length < head.Length)
                {
                    var read = stream.Read(head, length, head.Length - length);
                    if (read <= 0) break;
                    length += read;
                }
            }

            if (length >= 6 && head[0] == 0x37 && head[1] == 0x7A && head[2] == 0xBC && head[3] == 0xAF && head[4] == 0x27 && head[5] == 0x1C)
                return ArchiveFormat.SevenZip;

            if (length >= 6 && head[0] == 0x52 && head[1] == 0x61 && head[2] == 0x72 && head[3] == 0x21 && head[4] == 0x1A && head[5] == 0x07)
                return ArchiveFormat.Rar;

            if (length >= 4 && head[0] == 0x50 && head[1] == 0x4B
                && ((head[2] == 0x03 && head[3] == 0x04) || (head[2] == 0x05 && head[3] == 0x06) || (head[2] == 0x07 && head[3] == 0x08)))
                return ArchiveFormat.Zip;

            if (length >= 2 && head[0] == 0x1F && head[1] == 0x8B)
                return PeekIsTarContainer(path) ? ArchiveFormat.TarGz : ArchiveFormat.GZip;

            if (length >= 3 && head[0] == 0x42 && head[1] == 0x5A && head[2] == 0x68)
                return PeekIsTarContainer(path) ? ArchiveFormat.TarBz2 : ArchiveFormat.BZip2;

            if (length >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 0x7A && head[3] == 0x58 && head[4] == 0x5A && head[5] == 0x00)
                return PeekIsTarContainer(path) ? ArchiveFormat.TarXz : ArchiveFormat.Xz;

            if (length >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD)
                return PeekIsTarContainer(path) ? ArchiveFormat.TarZstd : ArchiveFormat.Zstd;

            if (length >= 4 && head[0] == 0x4C && head[1] == 0x5A && head[2] == 0x49 && head[3] == 0x50)
                return PeekIsTarContainer(path) ? ArchiveFormat.TarLz : ArchiveFormat.Lzw;

            if (length >= 262 && IsTarHeader(head.AsSpan(0, length)))
                return ArchiveFormat.Tar;

            if (length >= 14 && head[7] == 0x2A && head[8] == 0x2A && head[9] == 0x41 && head[10] == 0x43 && head[11] == 0x45 && head[12] == 0x2A && head[13] == 0x2A)
                return ArchiveFormat.Ace;

            if (length >= 2 && head[0] == 0x60 && head[1] == 0xEA)
                return ArchiveFormat.Arj;

            return ArchiveFormat.Unknown;
        }
        catch
        {
            return ArchiveFormat.Unknown;
        }
    }

    /// <summary>TAR 头魔数：offset 257 起是 <c>ustar</c>（老式 V7 / SharpCompress 写的可能没有）。</summary>
    public static bool IsTarHeader(ReadOnlySpan<byte> head)
        => head.Length >= 262
           && head[257] == (byte)'u'
           && head[258] == (byte)'s'
           && head[259] == (byte)'t'
           && head[260] == (byte)'a'
           && head[261] == (byte)'r';

    /// <summary>
    /// 外层是压缩流时，用 ReaderFactory 打开看一眼容器类型是不是 TAR；
    /// 打不开（单独一个 .bz2 / .xz / .zst / .lzw）或读失败都返回 false，绝不抛异常。
    /// </summary>
    private static bool PeekIsTarContainer(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions { LeaveStreamOpen = false });
            return reader.Type == ArchiveType.Tar;
        }
        catch
        {
            return false;
        }
    }
}
