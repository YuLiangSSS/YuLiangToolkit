using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>PDF 导出失败（没有图片 / 全部图都读不动 / 输出写不了）的异常，文案可直接给用户看。</summary>
public sealed class PicturePdfExportException(string message) : Exception(message);

/// <summary>
/// 「一键转 PDF」的纯逻辑导出器：把排序好的图片逐张写成一个 PDF，<b>一张图片一页</b>，
/// 页面尺寸 = 图片像素（1 像素 = 1 pt，PDF 里从上往下翻页 ≈ 阅读器的纵向长带）。
///
/// <para><b>不引第三方包</b>：手写最小 PDF 1.4（Catalog / Pages / Page / Image XObject / 内容流）。
/// 图片这样嵌：</para>
/// <list type="bullet">
///   <item>JPEG（RGB）：原始字节直接 <c>/DCTDecode</c> 嵌入 —— 不重编码、零质量损失、速度最快；</item>
///   <item>JPEG（CMYK，少见）：解码后转白底 JPEG 再嵌（直嵌 CMYK 会在部分阅读器里颜色反转）；</item>
///   <item>其它格式（PNG / BMP / GIF / WebP / TIFF）：WPF 解码 → 透明像素合成到白底 → JPEG（质量 90）再嵌。</item>
/// </list>
///
/// <para>逐张「解码 → 写页」，内存里只有当前一张图的字节；几百张的长带也不会吃满内存。
/// 单页尺寸超过 14400 pt（Acrobat 的上限）时按比例缩小页尺寸，图仍然铺满整页。</para>
///
/// <para>注意：直接嵌入的 JPEG 不会应用 EXIF 旋转标记（漫画 / 截图场景几乎都带不上旋转）；
/// 需要纠正方向的图请先用其它工具转一下。</para>
/// </summary>
public static class PicturePdfExport
{
    /// <summary>PDF 页尺寸上限（pt，1 pt = 1/72 英寸）：Acrobat 拒绝更长的页，超过就等比缩小。</summary>
    public const double MaxPagePoints = 14400;

    /// <summary>重编码 JPEG 的质量（非 JPEG 源 / CMYK 源用）。</summary>
    public const int ReencodeQuality = 90;

    /// <summary>
    /// 把排序好的图片导出成 PDF。抛 <see cref="PicturePdfExportException"/>（文案可直接展示），
    /// 磁盘 IO 异常原样抛出。<paramref name="progress"/> 的参数是 (已完成, 总数)，与调用线程同线程。
    /// </summary>
    public static Task BuildAsync(
        IReadOnlyList<PicturePage> pages,
        string outputPath,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Build(pages, outputPath, progress, cancellationToken), cancellationToken);

    /// <summary>同步实现（<see cref="BuildAsync"/> 的 Task.Run 体）。</summary>
    public static void Build(
        IReadOnlyList<PicturePage> pages,
        string outputPath,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (pages is null || pages.Count == 0)
            throw new PicturePdfExportException("没有可导出的图片（当前清单是空的）");

        // 逐张「解码 → 写入」，内存里只保留当前一张图的 JPEG 字节
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        var writer = new PdfStreamWriter(stream);

        // 对象编号方案：1 = Catalog、2 = Pages；第 i 张（0 起）= 页 3+3i / 内容流 4+3i / 图 5+3i
        writer.WriteHeader();
        writer.BeginObject(1);
        writer.WriteText("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        writer.BeginObject(2);
        var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => $"{3 + i * 3} 0 R"));
        writer.WriteText($"<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");

        var done = 0;
        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (jpeg, pixelWidth, pixelHeight) = EncodePageImage(page);
            var (boxWidth, boxHeight) = FitPage(pixelWidth, pixelHeight);

            var pageNumber = 3 + done * 3;
            var contentNumber = pageNumber + 1;
            var imageNumber = pageNumber + 2;

            writer.BeginObject(pageNumber);
            writer.WriteText(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Num(boxWidth)} {Num(boxHeight)}] "
                + $"/Resources << /XObject << /Im1 {imageNumber} 0 R >> >> /Contents {contentNumber} 0 R >>\nendobj\n");

            writer.BeginObject(contentNumber);
            var content = Encoding.ASCII.GetBytes(
                "q\n"
                + $"{Num(boxWidth)} 0 0 {Num(boxHeight)} 0 0 cm\n"
                + "/Im1 Do\nQ\n");
            writer.WriteStreamObject(content, extraDictionary: null);
            writer.BeginObject(imageNumber);
            writer.WriteStreamObject(jpeg, extraDictionary:
                $"/Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} "
                + "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode");

            done++;
            progress?.Invoke(done, pages.Count);
        }

        writer.WriteXrefAndTrailer();
    }

    /// <summary>页尺寸（pt）：像素 1:1；单边超过 <see cref="MaxPagePoints"/> 时整页等比缩到上限内。</summary>
    public static (double Width, double Height) FitPage(double pixelWidth, double pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0) return (1, 1);
        var scale = Math.Min(1.0, MaxPagePoints / Math.Max(pixelWidth, pixelHeight));
        return (pixelWidth * scale, pixelHeight * scale);
    }

    /// <summary>把一张图变成要嵌入 PDF 的 JPEG 字节（返回 图片字节 / 像素宽 / 像素高）。</summary>
    public static (byte[] JpegBytes, int PixelWidth, int PixelHeight) EncodePageImage(PicturePage page)
    {
        // 快路径：JPEG 源文件直接嵌（RGB），不重编码、零损失
        try
        {
            var raw = File.ReadAllBytes(page.FilePath);
            if (IsJpeg(raw) && !IsCmykJpeg(raw))
            {
                return (raw, page.PixelWidth, page.PixelHeight);
            }
        }
        catch (Exception ex)
        {
            throw new PicturePdfExportException($"读不出图片：{page.FileName}（{ex.Message}）");
        }

        // 慢路径：WPF 解码 →（有透明就合成白底）→ JPEG 重编码
        try
        {
            using var stream = File.OpenRead(page.FilePath);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            BitmapSource source;
            if (IsOpaqueFormat(frame.Format))
            {
                source = frame;
            }
            else
            {
                // 统一转 BGRA 再逐像素压到白底（JPEG 没有 alpha，直接转换会把透明区变黑）
                var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
                var stride = width * 4;
                var bgra = new byte[stride * height];
                converted.CopyPixels(bgra, stride, 0);
                var bgr = FlattenToWhiteBgr24(bgra, width, height);
                source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, bgr, width * 3);
            }

            var encoder = new JpegBitmapEncoder { QualityLevel = ReencodeQuality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return (output.ToArray(), width, height);
        }
        catch (Exception ex)
        {
            throw new PicturePdfExportException($"转不出这一页：{page.FileName}（{ex.Message}）");
        }
    }

    /// <summary>BGRA 像素逐点合成到白底，输出 BGR24（纯函数，自检逐值断言）。</summary>
    public static byte[] FlattenToWhiteBgr24(byte[] bgra, int width, int height)
    {
        var strideIn = width * 4;
        var strideOut = width * 3;
        var output = new byte[strideOut * height];
        for (var y = 0; y < height; y++)
        {
            var inRow = y * strideIn;
            var outRow = y * strideOut;
            for (var x = 0; x < width; x++)
            {
                var i = inRow + x * 4;
                var alpha = bgra[i + 3];
                byte oB, oG, oR;
                if (alpha == 255)
                {
                    oB = bgra[i];
                    oG = bgra[i + 1];
                    oR = bgra[i + 2];
                }
                else if (alpha == 0)
                {
                    oB = oG = oR = 255;
                }
                else
                {
                    // c_out = c * a/255 + 255 * (1 - a/255)，白底
                    oB = (byte)((bgra[i] * alpha + 255 * (255 - alpha)) / 255);
                    oG = (byte)((bgra[i + 1] * alpha + 255 * (255 - alpha)) / 255);
                    oR = (byte)((bgra[i + 2] * alpha + 255 * (255 - alpha)) / 255);
                }

                var o = outRow + x * 3;
                output[o] = oB;
                output[o + 1] = oG;
                output[o + 2] = oR;
            }
        }

        return output;
    }

    /// <summary>这些像素格式没有 alpha 通道，可以直接丢给 JPEG 编码器。</summary>
    private static bool IsOpaqueFormat(PixelFormat format)
        => format.Equals(PixelFormats.Bgr24)
           || format.Equals(PixelFormats.Bgr32)
           || format.Equals(PixelFormats.Rgb24)
           || format.Equals(PixelFormats.Gray8)
           || format.Equals(PixelFormats.Gray16)
           || format.Equals(PixelFormats.BlackWhite);

    /// <summary>JPEG 的 SOI 标记（FF D8）。</summary>
    private static bool IsJpeg(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    /// <summary>
    /// 是不是 CMYK JPEG（SOF 段声明 4 个分量）：直嵌会让部分阅读器颜色反转，这种少见文件走重编码。
    /// 扫描段找 SOF（C0/C1/C2/C3/C5/C6/C7/C9/CA/CB/CD/CE/CF）；找不到按 RGB 处理。
    /// </summary>
    private static bool IsCmykJpeg(byte[] bytes)
    {
        var i = 2;
        while (i + 3 < bytes.Length)
        {
            if (bytes[i] != 0xFF) { i++; continue; }
            var marker = bytes[i + 1];
            if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            if (i + 4 >= bytes.Length) break;

            var length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
            {
                // SOF 段：len(2) 精度(1) 高(2) 宽(2) 分量数(1) —— 分量数在段内偏移 7
                if (i + 9 < bytes.Length) return bytes[i + 9] == 4;
                return false;
            }

            i += 2 + length;
        }

        return false;
    }

    /// <summary>数字格式化（PDF 里必须不变文化、不带千分位）。</summary>
    private static string Num(double value)
        => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// PDF 的流式写出器：只认「对象号 0  obj … endobj」这一层，负责记录每个对象的文件偏移，
/// 收尾时写 xref 表 + trailer（见 <see cref="PicturePdfExport.Build"/>）。
/// </summary>
public sealed class PdfStreamWriter(Stream stream) : IDisposable
{
    private readonly Stream _stream = stream;
    private readonly List<long> _offsets = [];

    /// <summary>写文件头（版本行 + 二进制注释行，让传输层别把文件当文本处理）。</summary>
    public void WriteHeader()
    {
        WriteText("%PDF-1.4\n");
        // 这一行是 PDF 规范要求的二进制标记（>127 的四个字节）
        WriteBytes([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
    }

    /// <summary>开始一个对象：记录偏移并写 <c>{n} 0 obj</c>。对象必须按 1,2,3… 连续写入。</summary>
    public void BeginObject(int number)
    {
        while (_offsets.Count < number - 1) _offsets.Add(0);
        _offsets.Add(_stream.Position);
        WriteText($"{number} 0 obj\n");
    }

    /// <summary>写一段 ASCII 文本（对象字典 / endobj 等）。</summary>
    public void WriteText(string text) => WriteBytes(Encoding.ASCII.GetBytes(text));

    /// <summary>写一个带流的对象：字典（外加 extraDictionary）+ stream 数据 + endstream。</summary>
    public void WriteStreamObject(byte[] data, string? extraDictionary)
    {
        var dict = "<< /Length " + data.Length + (extraDictionary is null ? "" : " " + extraDictionary) + " >>\n";
        WriteText(dict);
        WriteText("stream\n");
        WriteBytes(data);
        WriteText("\nendstream\nendobj\n");
    }

    /// <summary>写 xref 表 + trailer + startxref（收尾，之后不能再写对象）。</summary>
    public void WriteXrefAndTrailer()
    {
        var xrefPosition = _stream.Position;
        var count = _offsets.Count + 1;   // 0 号是空闲槽
        var text = new StringBuilder();
        text.Append("xref\n").Append("0 ").Append(count).Append('\n');
        text.Append("0000000000 65535 f \n");
        foreach (var offset in _offsets)
        {
            text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" 00000 n \n");
        }

        text.Append("trailer\n<< /Size ").Append(count).Append(" /Root 1 0 R >>\n");
        text.Append("startxref\n").Append(xrefPosition).Append('\n');
        text.Append("%%EOF\n");
        WriteText(text.ToString());
    }

    private void WriteBytes(byte[] bytes) => _stream.Write(bytes, 0, bytes.Length);

    public void Dispose() => _stream.Dispose();
}
