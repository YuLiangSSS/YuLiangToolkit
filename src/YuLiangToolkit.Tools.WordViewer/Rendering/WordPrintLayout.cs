using System.Windows;
using System.Windows.Documents;
using YuLiangToolkit.Tools.WordViewer.Parsing;

namespace YuLiangToolkit.Tools.WordViewer.Rendering;

/// <summary>
/// 打印排版：**另建一份**专供打印的 <see cref="FlowDocument"/>（不复用屏幕上的那一份，
/// 打印完不会影响当前阅读位置 / 滚动状态），并按打印机的可打印区域设好页尺寸与列宽，再交分页器。
///
/// <para><b>为什么要单独一份</b>：屏幕上的文档为了「连续浏览」把 <c>ColumnWidth</c> 设成了无穷大、
/// 也没有页高；直接拿它打印会排成一列无限长。这里按 <c>PrintDialog.PrintableAreaWidth/Height</c>
/// 设置 <c>PageWidth/PageHeight</c> 与有限的 <c>ColumnWidth</c>，分页器才会按纸分页。</para>
///
/// <para>纯 WPF 逻辑、不碰对话框：<c>WordViewerViewModel.PrintCommand</c> 拿到打印区域后调它；
/// 自检可以在 STA 线程里直接断言页尺寸 / 列宽。</para>
/// </summary>
public static class WordPrintLayout
{
    /// <summary>打印页边距（DIP，约 9.5 mm）——比屏幕上的纸面内边距小一点，避免打印浪费纸。</summary>
    public const double PageMarginDip = 36;

    /// <summary>A4 纸 96 DPI 下的回退宽度（拿不到打印机区域时用）。</summary>
    private const double FallbackPageWidth = 793;

    /// <summary>A4 纸 96 DPI 下的回退高度。</summary>
    private const double FallbackPageHeight = 1123;

    /// <summary>
    /// 按打印区域生成可打印文档：正文 + 当前开关（页眉页脚 / 批注 / 修订）都按屏幕当前选项重排。
    /// </summary>
    /// <param name="document">已解析的文档模型。</param>
    /// <param name="options">渲染选项；<c>null</c> = 默认（全部显示）。</param>
    /// <param name="printableArea">打印机可打印区域（<c>PrintDialog.PrintableAreaWidth/Height</c>）。</param>
    public static FlowDocument CreatePrintableDocument(
        WordDocument document, WordRenderOptions? options, Size printableArea)
    {
        ArgumentNullException.ThrowIfNull(document);

        var rendered = WordFlowRenderer.Build(document, options ?? new WordRenderOptions());
        var flow = rendered.Document;

        var width = printableArea.Width;
        var height = printableArea.Height;
        if (!double.IsFinite(width) || width <= 0) width = FallbackPageWidth;
        if (!double.IsFinite(height) || height <= 0) height = FallbackPageHeight;
        width = Math.Max(200, width);
        height = Math.Max(200, height);

        flow.PageWidth = width;
        flow.PageHeight = height;
        flow.PagePadding = new Thickness(PageMarginDip);

        // 打印必须给有限的列宽：屏幕上的 PositiveInfinity 会让分页器只排一列超长内容。
        // 用「纸宽 − 左右页边距」作为正文列宽，内容不会超出可打印区域。
        flow.ColumnWidth = Math.Max(1, width - PageMarginDip * 2);
        flow.ColumnGap = 0;

        // 分页器自己也再设一次页尺寸：部分打印机 / 驱动只认 Paginator.PageSize
        var paginator = ((IDocumentPaginatorSource)flow).DocumentPaginator;
        paginator.PageSize = new Size(width, height);

        return flow;
    }
}
