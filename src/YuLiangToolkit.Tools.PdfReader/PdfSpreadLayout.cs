namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>双页画面里的一页摆在哪（内容坐标系，单位 DIP）。</summary>
public sealed record PdfSpreadSlot(int PageIndex, double X, double Y, double Width, double Height);

/// <summary>一次双页排版的结果：左右两页（最后一屏可能只有左页）+ 内容总尺寸。</summary>
public sealed record PdfSpreadLayoutResult(
    double ContentWidth,
    double ContentHeight,
    PdfSpreadSlot? Left,
    PdfSpreadSlot? Right);

/// <summary>
/// 「双页翻页」的纯逻辑：页号夹取、左右配对、前后翻页、把一页按比例塞进半屏槽位。
/// 界面只负责把结果画出来，自检直接断言。
///
/// <para><b>配对规则（按书页顺序，最直白的那种）</b>：一屏两页 = 第 <c>i</c> 页（左）+ 第 <c>i+1</c> 页（右），
/// 下一页是 <c>i+2</c> —— 即「一次翻两页」，相邻两屏不重叠；总页数是奇数时最后一屏只有左页。
/// （不像图片阅读器那样一次翻一张、相邻屏重叠一张：翻书 / 看 PDF 时整屏翻过去更符合直觉。）</para>
/// </summary>
public static class PdfSpreadLayout
{
    /// <summary>左右两页之间的默认间距（像素）。</summary>
    public const double DefaultPageGap = 16;

    /// <summary>视口四周默认留白（像素）。</summary>
    public const double DefaultSideMargin = 24;

    private const double MinimumUsableLength = 1;

    /// <summary>
    /// 最后一屏的起始页号（一定是偶数）：页数 ≤ 1 → 0；页数奇数 → 最后一页单独占左槽。
    /// </summary>
    public static int LastStartIndex(int pageCount)
        => pageCount <= 0 ? 0 : (pageCount - 1) / 2 * 2;

    /// <summary>把任意页号对齐到这一屏的起始页（偶数、且不超过最后一屏）。</summary>
    public static int NormalizeStart(int pageIndex, int pageCount)
    {
        if (pageCount <= 0) return 0;
        var clamped = Math.Clamp(pageIndex, 0, pageCount - 1);
        var start = clamped / 2 * 2;
        return Math.Min(start, LastStartIndex(pageCount));
    }

    /// <summary>这一屏显示哪两页：左 / 右槽的页号（最后一屏不足两页时右槽为 null）。</summary>
    public static (int? Left, int? Right) VisiblePages(int startPageIndex, int pageCount)
    {
        if (pageCount <= 0) return (null, null);

        var start = NormalizeStart(startPageIndex, pageCount);
        var left = start < pageCount ? start : (int?)null;
        var right = start + 1 < pageCount ? start + 1 : (int?)null;
        return (left, right);
    }

    /// <summary>还能不能往前翻（上一屏）。</summary>
    public static bool CanGoPrevious(int startPageIndex, int pageCount)
        => pageCount > 0 && NormalizeStart(startPageIndex, pageCount) > 0;

    /// <summary>还能不能往后翻（下一屏）。</summary>
    public static bool CanGoNext(int startPageIndex, int pageCount)
        => pageCount > 0 && NormalizeStart(startPageIndex, pageCount) < LastStartIndex(pageCount);

    /// <summary>按阅读方向翻一屏（一次两页），两端夹住。</summary>
    public static int Step(int startPageIndex, int pageCount, bool forward)
    {
        if (pageCount <= 0) return 0;
        var start = NormalizeStart(startPageIndex, pageCount);
        return forward
            ? Math.Min(LastStartIndex(pageCount), start + 2)
            : Math.Max(0, start - 2);
    }

    /// <summary>把页号对齐到所在屏的起始页（从连续滚动切到双页时用）。</summary>
    public static int StartForPage(int pageIndex, int pageCount) => NormalizeStart(pageIndex, pageCount);

    /// <summary>整本翻到第几屏了（0~1，双页模式的进度条用）。只有一屏时为 0。</summary>
    public static double ProgressRatio(int startPageIndex, int pageCount)
    {
        var last = LastStartIndex(pageCount);
        if (last <= 0) return 0;
        return (double)NormalizeStart(startPageIndex, pageCount) / last;
    }

    /// <summary>
    /// 按视口尺寸算这一屏的排版：内容 = 视口，左右各一个槽位（中间留缝、四周留白），
    /// 每页按自己的宽高比塞进槽位并水平 / 垂直居中（不拉伸、不变形）。
    /// 视口没量出来（≤ 0）或没有页时返回空结果。
    /// </summary>
    public static PdfSpreadLayoutResult Compute(
        IReadOnlyList<PdfPageInfo>? pages,
        int startPageIndex,
        double viewportWidth,
        double viewportHeight,
        double pageGap = DefaultPageGap,
        double sideMargin = DefaultSideMargin)
    {
        var width = HasUsableLength(viewportWidth) ? viewportWidth : 0;
        var height = HasUsableLength(viewportHeight) ? viewportHeight : 0;
        if (width <= 0 || height <= 0 || pages is null || pages.Count == 0)
            return new PdfSpreadLayoutResult(width, height, null, null);

        var gap = HasUsableLength(pageGap) ? Math.Max(0, pageGap) : DefaultPageGap;
        var margin = HasUsableLength(sideMargin) ? Math.Max(0, sideMargin) : DefaultSideMargin;
        var usableWidth = Math.Max(MinimumUsableLength, width - margin * 2);
        var usableHeight = Math.Max(MinimumUsableLength, height - margin * 2);
        var slotWidth = Math.Max(MinimumUsableLength, (usableWidth - gap) / 2);

        var (leftPage, rightPage) = VisiblePages(startPageIndex, pages.Count);
        var leftSlot = leftPage is { } leftIndex
            ? FitIntoSlot(pages[leftIndex], leftIndex, margin, margin, slotWidth, usableHeight)
            : null;
        var rightSlot = rightPage is { } rightIndex
            ? FitIntoSlot(pages[rightIndex], rightIndex, margin + slotWidth + gap, margin, slotWidth, usableHeight)
            : null;

        return new PdfSpreadLayoutResult(width, height, leftSlot, rightSlot);
    }

    /// <summary>
    /// 把一页按比例塞进一个槽位并居中（PDF 页面宽高比 = 原始尺寸，不能被拉变形）。
    /// 页面尺寸坏掉时直接铺满槽位。
    /// </summary>
    public static PdfSpreadSlot FitIntoSlot(
        PdfPageInfo page,
        int pageIndex,
        double slotX,
        double slotY,
        double slotWidth,
        double slotHeight)
    {
        var (fitWidth, fitHeight) = FitSize(page, slotWidth, slotHeight);
        var x = slotX + (Math.Max(MinimumUsableLength, slotWidth) - fitWidth) / 2;
        var y = slotY + (Math.Max(MinimumUsableLength, slotHeight) - fitHeight) / 2;
        return new PdfSpreadSlot(pageIndex, x, y, fitWidth, fitHeight);
    }

    /// <summary>一页塞进「槽宽 × 槽高」后的显示尺寸（等比缩小 / 放大，不拉伸）。</summary>
    public static (double Width, double Height) FitSize(PdfPageInfo page, double slotWidth, double slotHeight)
    {
        var slotW = Math.Max(MinimumUsableLength, slotWidth);
        var slotH = Math.Max(MinimumUsableLength, slotHeight);
        if (!page.HasValidSize) return (slotW, slotH);

        var scale = Math.Min(slotW / page.WidthDip, slotH / page.HeightDip);
        return (Math.Max(MinimumUsableLength, page.WidthDip * scale),
                Math.Max(MinimumUsableLength, page.HeightDip * scale));
    }

    private static bool HasUsableLength(double value)
        => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
}
