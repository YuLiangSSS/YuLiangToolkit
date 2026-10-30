using System.Text;

namespace YuLiangToolkit.Tools.PdfReader.Rendering;

/// <summary>
/// 演示 / 截图 / 自检用的**最小合法 PDF**生成器（纯手写字节，不依赖任何 PDF 库）。
///
/// <para>用途：</para>
/// <list type="bullet">
///   <item>开发时 <c>YLT_PDF_DEMO=1</c> 自动生成一份多页 PDF 并打开（截图冒烟用）；</item>
///   <item>自检里生成临时 PDF，真的走一遍 WinRT 渲染链路，证明「系统 PDF 引擎 + 解码」这条链没断。</item>
/// </list>
///
/// <para>每页是一个纯色背景 + 大号页码 + 几条说明文字（用 PDF 内置的 Helvetica，不嵌字体），
/// 所以文件只有几 KB。内容只用 ASCII：PDF 内置标准字体不带中文字形，中文要嵌字体，演示用不着。</para>
/// </summary>
public static class PdfDemoPdf
{
    /// <summary>演示 PDF 的页数（截图 / 自检默认 6 页，刚好能翻两屏双页）。</summary>
    public const int DefaultPageCount = 6;

    /// <summary>页面尺寸（PDF 点数：400 × 600 ≈ A4 竖版比例）。</summary>
    public const int PageWidthPoints = 400;
    public const int PageHeightPoints = 600;

    private static readonly (double R, double G, double B, string Name)[] Palette =
    {
        (0.10, 0.30, 0.85, "BLUE"),
        (0.10, 0.62, 0.50, "GREEN"),
        (0.85, 0.35, 0.20, "ORANGE"),
        (0.45, 0.28, 0.75, "PURPLE"),
        (0.15, 0.55, 0.80, "CYAN"),
        (0.75, 0.25, 0.45, "PINK"),
    };

    /// <summary>生成一份 <paramref name="pageCount"/> 页的演示 PDF 的完整字节。</summary>
    public static byte[] Create(int pageCount = DefaultPageCount)
    {
        if (pageCount <= 0) pageCount = 1;

        // 对象编号约定：1 = Catalog，2 = Pages，3 = Helvetica 字体；之后每页占两个对象（Page / Contents）
        var objects = new List<string>(3 + pageCount * 2);

        var kids = new StringBuilder();
        for (var i = 0; i < pageCount; i++) kids.Append(4 + i * 2).Append(" 0 R ");
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids.ToString().TrimEnd()}] /Count {pageCount} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        for (var i = 0; i < pageCount; i++)
        {
            var content = BuildPageContent(i, pageCount);
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidthPoints} {PageHeightPoints}] " +
                $"/Resources << /Font << /F1 3 0 R >> >> /Contents {5 + i * 2} 0 R >>");
            objects.Add($"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>(objects.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(builder.ToString()));
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xrefPosition = Encoding.ASCII.GetByteCount(builder.ToString());
        builder.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets) builder.Append(offset.ToString("D10")).Append(" 00000 n \n");
        builder.Append("trailer\n<< /Size ").Append(objects.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xrefPosition).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string BuildPageContent(int pageIndex, int pageCount)
    {
        var color = Palette[pageIndex % Palette.Length];
        var pageNumber = pageIndex + 1;
        var content = new StringBuilder();

        // 纯色背景（整页铺满）
        content.Append($"{color.R:0.###} {color.G:0.###} {color.B:0.###} rg\n");
        content.Append($"0 0 {PageWidthPoints} {PageHeightPoints} re f\n");

        // 顶部深色条 + 底部浅色条：连续滚动时能明显看出页与页的分界
        content.Append("0.08 0.10 0.14 rg\n");
        content.Append($"0 {PageHeightPoints - 46} {PageWidthPoints} 46 re f\n");
        content.Append("1 1 1 rg\n");
        content.Append($"BT /F1 16 Tf 24 {PageHeightPoints - 30} Td (YuLiangToolkit PDF Reader demo) Tj ET\n");

        content.Append("0 0 0 rg\n");
        content.Append($"0 0 {PageWidthPoints} 70 re f\n");
        content.Append("0.85 0.88 0.95 rg\n");
        content.Append($"BT /F1 12 Tf 24 42 Td (Page {pageNumber} / {pageCount}) Tj ET\n");
        content.Append("0.55 0.60 0.70 rg\n");
        content.Append($"BT /F1 10 Tf 24 24 Td (wheel to scroll   drag with inertia   spread mode turns two pages) Tj ET\n");

        // 大号页码
        content.Append("1 1 1 rg\n");
        content.Append($"BT /F1 120 Tf 34 300 Td (Page {pageNumber}) Tj ET\n");

        // 装饰：几条半透明白线，不同页位置不同，翻页 / 滚动时能看出画面在动
        content.Append("1 1 1 rg\n");
        for (var line = 0; line < 3; line++)
        {
            var y = 120 + line * 22 + pageIndex % 3 * 8;
            content.Append($"{48 + pageIndex % 4 * 10} {y} {PageWidthPoints - 96} 6 re f\n");
        }

        return content.ToString();
    }
}
