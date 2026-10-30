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
    /// 按视口尺寸 + 间距 + 缩放算这一屏的排版。
    ///
    /// <para><b>排版规则</b>：同一屏的两页先按「可用高度」等比放到一样高，再检查两页并排（中间留 gap）
    /// 会不会超出可用宽度 —— 超了就把两页一起按比例缩小，让「两页 + 间距」正好占满可用宽度；
    /// 整组在视口里水平 / 垂直居中。所以 100% 时页面少留横向空白（双页看起来是贴着的），
    /// 间距 <paramref name="pageGap"/> 是两页之间**真正**的空隙。缩放 &gt; 100% 后内容超出视口，可平移。</para>
    ///
    /// <para>视口没量出来（≤ 0）或没有页时返回空结果；单页（最后不足两页 / 只有一页）单独居中。</para>
    /// </summary>
    public static PdfSpreadLayoutResult Compute(
        IReadOnlyList<PdfPageInfo>? pages,
        int startPageIndex,
        double viewportWidth,
        double viewportHeight,
        double pageGap = DefaultPageGap,
        double sideMargin = DefaultSideMargin,
        double zoomScale = 1.0)
    {
        var width = HasUsableLength(viewportWidth) ? viewportWidth : 0;
        var height = HasUsableLength(viewportHeight) ? viewportHeight : 0;
        if (width <= 0 || height <= 0 || pages is null || pages.Count == 0)
            return new PdfSpreadLayoutResult(width, height, null, null);

        // ⚠ 0 是合法间距（两页贴在一起），只有负数 / NaN / ∞ 才回退默认值
        var gap = pageGap >= 0 && !double.IsNaN(pageGap) && !double.IsInfinity(pageGap)
            ? pageGap
            : DefaultPageGap;
        var margin = HasUsableLength(sideMargin) ? Math.Max(0, sideMargin) : DefaultSideMargin;
        var zoom = PdfContinuousLayout.EffectiveZoom(zoomScale);

        var usableWidth = Math.Max(MinimumUsableLength, (width - margin * 2) * zoom);
        var usableHeight = Math.Max(MinimumUsableLength, (height - margin * 2) * zoom);
        var contentWidth = Math.Max(width, margin * 2 + usableWidth);
        var contentHeight = Math.Max(height, margin * 2 + usableHeight);

        var (leftPage, rightPage) = VisiblePages(startPageIndex, pages.Count);
        if (leftPage is null)
            return new PdfSpreadLayoutResult(contentWidth, contentHeight, null, null);

        PdfSpreadSlot leftSlot;
        PdfSpreadSlot? rightSlot = null;

        if (rightPage is not { } rightIndex)
        {
            // 单页（最后一屏不足两页）：整页塞进可用区域并居中
            var (singleWidth, singleHeight) = FitSize(pages[leftPage.Value], usableWidth, usableHeight);
            leftSlot = new PdfSpreadSlot(
                leftPage.Value,
                (contentWidth - singleWidth) / 2,
                (contentHeight - singleHeight) / 2,
                singleWidth,
                singleHeight);
        }
        else
        {
            var left = pages[leftPage.Value];
            var right = pages[rightIndex];

            // 先让两页一样高（可用高度），再按「两页 + 间距不超过可用宽度」整体缩放
            var naturalLeftWidth = WidthForHeight(left, usableHeight);
            var naturalRightWidth = WidthForHeight(right, usableHeight);
            var naturalWidths = Math.Max(MinimumUsableLength, naturalLeftWidth + naturalRightWidth);
            var naturalTotal = naturalWidths + gap;
            var scale = naturalTotal > usableWidth
                ? Math.Clamp((usableWidth - gap) / naturalWidths, 0.01, 1.0)
                : 1.0;

            var pageHeight = Math.Max(MinimumUsableLength, usableHeight * scale);
            var leftWidth = Math.Max(MinimumUsableLength, naturalLeftWidth * scale);
            var rightWidth = Math.Max(MinimumUsableLength, naturalRightWidth * scale);
            var totalWidth = leftWidth + rightWidth + gap;

            var x = (contentWidth - totalWidth) / 2;
            var y = (contentHeight - pageHeight) / 2;
            leftSlot = new PdfSpreadSlot(leftPage.Value, x, y, leftWidth, pageHeight);
            rightSlot = new PdfSpreadSlot(rightIndex, x + leftWidth + gap, y, rightWidth, pageHeight);
        }

        return new PdfSpreadLayoutResult(contentWidth, contentHeight, leftSlot, rightSlot);
    }

    /// <summary>一页以指定高度等比显示时的宽度（页面尺寸坏掉时按方形算）。</summary>
    private static double WidthForHeight(PdfPageInfo page, double height)
    {
        var safeHeight = Math.Max(MinimumUsableLength, height);
        return page.HasValidSize
            ? Math.Max(MinimumUsableLength, safeHeight * page.AspectRatio)
            : safeHeight;
    }

    /// <summary>
    /// 双页放大后的平移夹取（平移量按“已经往右 / 下移过去多少”算，≥ 0）：
    /// 内容不比视口大时那个方向不能平移（返回 0）。
    /// </summary>
    public static (double X, double Y) ClampPan(
        double panX,
        double panY,
        double contentWidth,
        double contentHeight,
        double viewportWidth,
        double viewportHeight)
    {
        var maxX = Math.Max(0, contentWidth - Math.Max(0, viewportWidth));
        var maxY = Math.Max(0, contentHeight - Math.Max(0, viewportHeight));
        return (double.IsNaN(panX) ? 0 : Math.Clamp(panX, 0, maxX),
                double.IsNaN(panY) ? 0 : Math.Clamp(panY, 0, maxY));
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
