using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// APNG（动图 PNG）解码器。
///
/// <para><b>为什么自己写</b>：Windows 自带的 WIC 只把 APNG 当普通 PNG 解第一帧，SkiaSharp 也不支持 APNG
/// 动画；APNG 的帧数据本身就是标准 PNG 的 IDAT / fdAT 压缩块，所以这里只做「拆块 + 拼帧 + 按
/// dispose/blend 合成」，每一帧仍交给 WIC 解码（不手写 PNG 滤波器 / zlib）。</para>
///
/// <para>支持的常见情况：8 位 RGB / RGBA / 调色板 + tRNS 透明、局部帧、blend SOURCE/OVER、
/// dispose NONE/BACKGROUND/PREVIOUS。不做的事：不校验 CRC、不处理超大帧（统一有帧数 / 像素预算，
/// 超了由调用方退回静态第一帧）。</para>
/// </summary>
internal static class StickerApng
{
    private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>这个文件是不是 APNG（PNG 且带 acTL 块）。只读文件头 / 块头，不解码像素。</summary>
    public static bool IsApngFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var header = new byte[8];
            if (stream.Read(header, 0, header.Length) != header.Length) return false;
            if (!header.AsSpan().SequenceEqual(Signature)) return false;

            while (true)
            {
                var chunkHeader = new byte[8];
                if (stream.Read(chunkHeader, 0, chunkHeader.Length) != chunkHeader.Length) return false;
                var length = BinaryPrimitives.ReadInt32BigEndian(chunkHeader);
                if (length < 0) return false;
                var type = Encoding.ASCII.GetString(chunkHeader, 4, 4);
                if (type == "acTL") return true;
                if (type == "IEND") return false;
                if (stream.CanSeek)
                {
                    if (stream.Length - stream.Position < length + 4L) return false;
                    stream.Seek(length + 4L, SeekOrigin.Current);
                }
                else
                {
                    var skip = new byte[length + 4];
                    if (stream.Read(skip, 0, skip.Length) != skip.Length) return false;
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 APNG 合成成逐帧位图。返回 null = 不用动画（帧不够 / 太大 / 解析失败），原因写进 warning。
    /// </summary>
    public static StickerAnimation? TryLoadAnimation(byte[] bytes, ref string? warning)
    {
        try
        {
            var document = Parse(bytes);
            if (document is null || document.Frames.Count <= 1)
            {
                return null;
            }

            var frameCount = document.Frames.Count;
            var estimate = StickerTiming.EstimateAnimatedPixels(frameCount, document.CanvasWidth, document.CanvasHeight);
            if (!StickerTiming.CanAnimate(frameCount, estimate))
            {
                warning = $"这个 APNG 有 {frameCount} 帧、占内存较多，已按静态第一帧显示（避免贴纸占用过高内存）";
                return null;
            }

            var scale = document.CanvasWidth > StickerTiming.MaxAnimatedFrameWidth
                ? (double)StickerTiming.MaxAnimatedFrameWidth / document.CanvasWidth
                : 1.0;

            var canvas = new byte[document.CanvasWidth * document.CanvasHeight * 4];
            var frames = new List<BitmapSource>(frameCount);
            var delays = new List<int>(frameCount);

            byte[]? previousSnapshot = null;
            var previousDispose = 0;
            var previousRect = (X: 0, Y: 0, Width: 0, Height: 0);

            foreach (var frame in document.Frames)
            {
                // 先处理上一帧的 dispose
                if (previousDispose == 1)
                {
                    ClearRect(canvas, document.CanvasWidth, document.CanvasHeight, previousRect);
                }
                else if (previousDispose == 2 && previousSnapshot is not null)
                {
                    Buffer.BlockCopy(previousSnapshot, 0, canvas, 0, canvas.Length);
                }

                // dispose=PREVIOUS 要在画这一帧之前快照
                var snapshot = frame.Dispose == 2 ? (byte[])canvas.Clone() : null;

                var framePng = BuildFramePng(document, frame);
                var decoded = DecodeFrame(framePng, frame.Width, frame.Height);
                if (decoded is null)
                {
                    warning = "APNG 某一帧解码失败，已按静态第一帧显示（文件可能损坏 / 用了少见的 PNG 特性）";
                    return null;
                }

                BlendOnto(canvas, document.CanvasWidth, document.CanvasHeight,
                    decoded, frame.Width, frame.Height, frame.X, frame.Y, frame.Blend == 0);

                delays.Add(StickerTiming.NormalizeDelayMs(frame.DelayMs));
                frames.Add(StickerMedia.FinalizeFrame(ToBitmapSource(canvas, document.CanvasWidth, document.CanvasHeight), scale));

                previousSnapshot = snapshot;
                previousDispose = frame.Dispose;
                previousRect = (frame.X, frame.Y, frame.Width, frame.Height);
            }

            return new StickerAnimation(frames.AsReadOnly(), delays.AsReadOnly());
        }
        catch (Exception ex)
        {
            warning = "APNG 动画解码失败，已按静态第一帧显示：" + ex.Message;
            return null;
        }
    }

    // ---------------- 解析 ----------------

    private sealed class ApngDocument
    {
        public int CanvasWidth;
        public int CanvasHeight;
        public byte[] Ihdr = Array.Empty<byte>();
        public byte[]? Plte;
        public byte[]? Trns;
        public List<ApngFrame> Frames = new();
    }

    private sealed class ApngFrame
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public int DelayMs;
        public byte Dispose;
        public byte Blend;
        public MemoryStream Data = new();
    }

    private static ApngDocument? Parse(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(Signature)) return null;

        var document = new ApngDocument();
        var defaultImage = new MemoryStream();
        var firstFrameBeforeIdat = false;
        var sawIdat = false;
        var index = 8;

        while (index + 8 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(index));
            if (length < 0 || index + 12 + length > bytes.Length) return null;
            var type = Encoding.ASCII.GetString(bytes, index + 4, 4);
            var data = bytes.AsSpan(index + 8, length);

            switch (type)
            {
                case "IHDR":
                    if (length < 13) return null;
                    document.Ihdr = data.ToArray();
                    document.CanvasWidth = BinaryPrimitives.ReadInt32BigEndian(data);
                    document.CanvasHeight = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    break;

                case "PLTE":
                    document.Plte = data.ToArray();
                    break;

                case "tRNS":
                    document.Trns = data.ToArray();
                    break;

                case "fcTL":
                    if (length < 26) return null;
                    if (!sawIdat) firstFrameBeforeIdat = true;
                    document.Frames.Add(ParseFrameControl(data));
                    break;

                case "IDAT":
                    sawIdat = true;
                    if (firstFrameBeforeIdat && document.Frames.Count == 1 && document.Frames[0].Data.Length == 0)
                    {
                        defaultImage.Write(data);
                    }
                    break;

                case "fdAT":
                    if (length >= 4 && document.Frames.Count > 0)
                    {
                        document.Frames[^1].Data.Write(data[4..]);
                    }
                    break;

                case "IEND":
                    goto Parsed;
            }

            index += 12 + length;
        }

    Parsed:
        if (document.Ihdr.Length == 0 || document.CanvasWidth <= 0 || document.CanvasHeight <= 0)
        {
            return null;
        }

        // fcTL 出现在 IDAT 之前：默认图像本身就是第一帧
        if (firstFrameBeforeIdat && document.Frames.Count > 0 && defaultImage.Length > 0
            && document.Frames[0].Data.Length == 0)
        {
            document.Frames[0].Data = defaultImage;
        }

        return document.Frames.Count > 0 ? document : null;
    }

    private static ApngFrame ParseFrameControl(ReadOnlySpan<byte> data)
    {
        var delayNum = BinaryPrimitives.ReadUInt16BigEndian(data[20..]);
        var delayDen = BinaryPrimitives.ReadUInt16BigEndian(data[22..]);
        var delayMs = delayDen == 0
            ? StickerTiming.DefaultFrameDelayMs
            : (int)Math.Round(delayNum * 1000.0 / delayDen);

        return new ApngFrame
        {
            Width = BinaryPrimitives.ReadInt32BigEndian(data[4..]),
            Height = BinaryPrimitives.ReadInt32BigEndian(data[8..]),
            X = BinaryPrimitives.ReadInt32BigEndian(data[12..]),
            Y = BinaryPrimitives.ReadInt32BigEndian(data[16..]),
            DelayMs = delayMs,
            Dispose = data[24],
            Blend = data[25],
        };
    }

    // ---------------- 单帧重组成 PNG，交给 WIC 解码 ----------------

    private static byte[] BuildFramePng(ApngDocument document, ApngFrame frame)
    {
        var ihdr = (byte[])document.Ihdr.Clone();
        BinaryPrimitives.WriteInt32BigEndian(ihdr, frame.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), frame.Height);

        using var output = new MemoryStream();
        output.Write(Signature);

        WriteChunk(output, "IHDR", ihdr);
        if (document.Plte is not null) WriteChunk(output, "PLTE", document.Plte);
        if (document.Trns is not null) WriteChunk(output, "tRNS", document.Trns);
        WriteChunk(output, "IDAT", frame.Data.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);

        var crcInput = new byte[4 + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, 4);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        output.Write(crc);
    }

    private static byte[]? DecodeFrame(byte[] pngBytes, int width, int height)
    {
        try
        {
            using var stream = new MemoryStream(pngBytes);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);
            return pixels;
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 合成 ----------------

    private static void ClearRect(byte[] canvas, int canvasWidth, int canvasHeight, (int X, int Y, int Width, int Height) rect)
    {
        var x0 = Math.Clamp(rect.X, 0, canvasWidth);
        var y0 = Math.Clamp(rect.Y, 0, canvasHeight);
        var x1 = Math.Clamp(rect.X + rect.Width, 0, canvasWidth);
        var y1 = Math.Clamp(rect.Y + rect.Height, 0, canvasHeight);
        if (x1 <= x0 || y1 <= y0) return;

        for (var y = y0; y < y1; y++)
        {
            Array.Clear(canvas, (y * canvasWidth + x0) * 4, (x1 - x0) * 4);
        }
    }

    /// <summary>把 frame（BGRA，直通 alpha）按 offset 画到 canvas 上；source=true 是 APNG 的 SOURCE（覆盖），否则 OVER。</summary>
    private static void BlendOnto(byte[] canvas, int canvasWidth, int canvasHeight,
        byte[] frame, int frameWidth, int frameHeight, int offsetX, int offsetY, bool source)
    {
        for (var row = 0; row < frameHeight; row++)
        {
            var canvasY = offsetY + row;
            if (canvasY < 0 || canvasY >= canvasHeight) continue;

            for (var column = 0; column < frameWidth; column++)
            {
                var canvasX = offsetX + column;
                if (canvasX < 0 || canvasX >= canvasWidth) continue;

                var sourceIndex = (row * frameWidth + column) * 4;
                var targetIndex = (canvasY * canvasWidth + canvasX) * 4;

                if (source)
                {
                    canvas[targetIndex] = frame[sourceIndex];
                    canvas[targetIndex + 1] = frame[sourceIndex + 1];
                    canvas[targetIndex + 2] = frame[sourceIndex + 2];
                    canvas[targetIndex + 3] = frame[sourceIndex + 3];
                    continue;
                }

                var sourceAlpha = frame[sourceIndex + 3];
                if (sourceAlpha == 0) continue;
                if (sourceAlpha == 255)
                {
                    canvas[targetIndex] = frame[sourceIndex];
                    canvas[targetIndex + 1] = frame[sourceIndex + 1];
                    canvas[targetIndex + 2] = frame[sourceIndex + 2];
                    canvas[targetIndex + 3] = 255;
                    continue;
                }

                var targetAlpha = canvas[targetIndex + 3];
                var normalizedSource = sourceAlpha / 255f;
                var normalizedTarget = targetAlpha / 255f;
                var outAlpha = normalizedSource + normalizedTarget * (1 - normalizedSource);
                if (outAlpha <= 0.0001f) continue;

                canvas[targetIndex] = (byte)Math.Clamp(
                    (frame[sourceIndex] * normalizedSource + canvas[targetIndex] * normalizedTarget * (1 - normalizedSource)) / outAlpha, 0, 255);
                canvas[targetIndex + 1] = (byte)Math.Clamp(
                    (frame[sourceIndex + 1] * normalizedSource + canvas[targetIndex + 1] * normalizedTarget * (1 - normalizedSource)) / outAlpha, 0, 255);
                canvas[targetIndex + 2] = (byte)Math.Clamp(
                    (frame[sourceIndex + 2] * normalizedSource + canvas[targetIndex + 2] * normalizedTarget * (1 - normalizedSource)) / outAlpha, 0, 255);
                canvas[targetIndex + 3] = (byte)Math.Clamp(outAlpha * 255f, 0, 255);
            }
        }
    }

    private static BitmapSource ToBitmapSource(byte[] canvas, int width, int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), canvas, width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------- CRC32 ----------------

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }
        return ~crc;
    }
}
