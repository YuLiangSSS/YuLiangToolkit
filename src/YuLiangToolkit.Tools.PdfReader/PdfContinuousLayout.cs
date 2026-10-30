namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>连续滚动长带里的一页摆在哪（内容坐标系，单位 DIP，原点在内容左上角）。</summary>
public sealed record PdfPageBox(int PageIndex, double X, double Y, double Width, double Height)
{
    /// <summary>这一页在阅读轴上的终点（纵向看 Y + Height，横向看 X + Width）。</summary>
    public double MainEnd(bool horizontal) => horizontal ? X + Width : Y + Height;

    /// <summary>这一页在阅读轴上的起点。</summary>
    public double MainStart(bool horizontal) => horizontal ? X : Y;
}

/// <summary>连续滚动一次排版的结果：内容总尺寸 + 每页位置。</summary>
public sealed record PdfContinuousLayoutResult(
    double ContentWidth,
    double ContentHeight,
    IReadOnlyList<PdfPageBox> Boxes)
{
    public int PageCount => Boxes.Count;
    public bool IsEmpty => Boxes.Count == 0;
}

/// <summary>
/// 「连续滚动」的纯排版数学（纵向 上→下 / 横向 左→右）。界面只负责把结果画出来，
/// 自检直接断言 —— 和「图片连续阅读器」的 PictureScrollMath 是同一个思路：
/// <b>阅读偏移</b> 0 = 第一页开头，最大值 = 最后一页末尾；界面再换算成 ScrollViewer 的物理偏移。
///
/// <para><b>铺满基准</b>：纵向模式页宽 = 视口宽 − 两侧留白（高按宽高比算）；横向模式页高 = 视口高 −
/// 上下留白（宽按宽高比算）。所以「100%」= 刚好铺满一屏，不是 PDF 原始点数 1:1。</para>
/// </summary>
public static class PdfContinuousLayout
{
    /// <summary>页与页之间的默认间距（像素）。</summary>
    public const double DefaultPageGap = 16;

    /// <summary>长带两端 / 铺满方向上留的默认边距（像素）。</summary>
    public const double DefaultSideMargin = 24;

    /// <summary>视口尺寸太小时的兜底页宽 / 页高，避免除 0。</summary>
    private const double MinimumUsableLength = 1;

    /// <summary>
    /// 按当前阅读方式、页尺寸、视口尺寸算一条长带。
    /// 视口没量出来（≤ 0）或没有页时返回空排版；页尺寸坏掉的页按正方形兜底。
    /// </summary>
    public static PdfContinuousLayoutResult Compute(
        PdfReadMode mode,
        IReadOnlyList<PdfPageInfo>? pages,
        double viewportWidth,
        double viewportHeight,
        double pageGap = DefaultPageGap,
        double sideMargin = DefaultSideMargin)
    {
        var normalized = PdfReadModes.Normalize(mode);
        var horizontal = PdfReadModes.IsHorizontal(normalized);
        var gap = HasUsableLength(pageGap) ? Math.Max(0, pageGap) : DefaultPageGap;
        var margin = HasUsableLength(sideMargin) ? Math.Max(0, sideMargin) : DefaultSideMargin;

        var viewportMain = horizontal ? Safe(viewportWidth) : Safe(viewportHeight);
        var viewportCross = horizontal ? Safe(viewportHeight) : Safe(viewportWidth);
        if (viewportMain <= 0 || viewportCross <= 0)
            return new PdfContinuousLayoutResult(0, 0, Array.Empty<PdfPageBox>());

        // 铺满方向：纵向 = 页宽铺满视口宽；横向 = 页高铺满视口高
        var usableCross = Math.Max(MinimumUsableLength, viewportCross - margin * 2);

        if (pages is null || pages.Count == 0)
        {
            // 没有页时内容尺寸 = 视口，界面不需要滚动；不是 0 是为了让空视口也别塌掉
            return new PdfContinuousLayoutResult(viewportWidth, viewportHeight, Array.Empty<PdfPageBox>());
        }

        var boxes = new List<PdfPageBox>(pages.Count);
        var cursor = margin;
        for (var i = 0; i < pages.Count; i++)
        {
            var page = pages[i];
            var cross = usableCross;
            // 铺满方向给了 cross，另一维按宽高比算：
            // 纵向 → 高 = 宽 / (宽/高) = 宽 × 高/宽；横向 → 宽 = 高 × (宽/高)。
            var main = page.HasValidSize
                ? (horizontal ? cross * page.AspectRatio : cross / page.AspectRatio)
                : cross;
            main = Math.Max(MinimumUsableLength, main);

            if (horizontal)
                boxes.Add(new PdfPageBox(i, cursor, margin, main, cross));
            else
                boxes.Add(new PdfPageBox(i, margin, cursor, cross, main));

            cursor += main + gap;
        }

        var contentWidth = horizontal ? Math.Max(viewportWidth, cursor - gap + margin) : Math.Max(viewportWidth, margin * 2 + usableCross);
        var contentHeight = horizontal ? Math.Max(viewportHeight, margin * 2 + usableCross) : Math.Max(viewportHeight, cursor - gap + margin);
        return new PdfContinuousLayoutResult(contentWidth, contentHeight, boxes);
    }

    /// <summary>阅读轴长度：纵向看内容高，横向看内容宽。</summary>
    public static double ReadingLength(PdfContinuousLayoutResult? layout, PdfReadMode mode)
    {
        if (layout is null) return 0;
        return PdfReadModes.IsHorizontal(mode) ? layout.ContentWidth : layout.ContentHeight;
    }

    /// <summary>视口在阅读轴上的长度：纵向看视口高，横向看视口宽。</summary>
    public static double ReadingViewport(double viewportWidth, double viewportHeight, PdfReadMode mode)
    {
        var value = PdfReadModes.IsHorizontal(mode) ? viewportWidth : viewportHeight;
        return HasUsableLength(value) ? value : 0;
    }

    /// <summary>阅读偏移的最大值（内容比视口短时为 0，表示没得滚）。</summary>
    public static double MaximumOffset(
        PdfContinuousLayoutResult? layout, PdfReadMode mode, double viewportWidth, double viewportHeight)
    {
        var length = ReadingLength(layout, mode);
        var viewport = ReadingViewport(viewportWidth, viewportHeight, mode);
        return Math.Max(0, length - viewport);
    }

    /// <summary>把任意阅读偏移夹进 [0, 最大值]（坏值当 0）。</summary>
    public static double ClampOffset(double offset, double maximum)
    {
        if (maximum <= 0 || double.IsNaN(maximum)) return 0;
        if (double.IsNaN(offset)) return 0;
        return Math.Clamp(offset, 0, maximum);
    }

    /// <summary>某一页开头对应的阅读偏移（界面「定位到某页」用；最后由调用方夹取）。</summary>
    public static double OffsetOfPage(PdfContinuousLayoutResult? layout, PdfReadMode mode, int pageIndex)
    {
        if (layout is null || layout.IsEmpty) return 0;
        var index = Math.Clamp(pageIndex, 0, layout.PageCount - 1);
        return layout.Boxes[index].MainStart(PdfReadModes.IsHorizontal(mode));
    }

    /// <summary>
    /// 阅读偏移最前面那一页的页号（页没过完就算它）。偏移超过最后页末尾 → 最后一页。
    /// 空排版返回 0。
    /// </summary>
    public static int PageIndexAtOffset(PdfContinuousLayoutResult? layout, PdfReadMode mode, double offset)
    {
        if (layout is null || layout.IsEmpty) return 0;
        var horizontal = PdfReadModes.IsHorizontal(mode);
        foreach (var box in layout.Boxes)
        {
            if (box.MainEnd(horizontal) > offset) return box.PageIndex;
        }
        return layout.PageCount - 1;
    }

    /// <summary>
    /// 视口中心落在哪一页（状态栏「第 x / n 页」用它，比用最上面那页更接近人的阅读位置）。
    /// </summary>
    public static int PageIndexAtViewportCenter(
        PdfContinuousLayoutResult? layout, PdfReadMode mode, double offset, double viewportLength)
    {
        var point = offset + Math.Max(0, viewportLength) / 2;
        return PageIndexAtOffset(layout, mode, point);
    }

    /// <summary>当前屏幕里能看到的第一页 / 最后一页（含两端；空排版返回 (-1, -1)）。</summary>
    public static (int First, int Last) VisibleRange(
        PdfContinuousLayoutResult? layout, PdfReadMode mode, double offset, double viewportLength)
    {
        if (layout is null || layout.IsEmpty) return (-1, -1);

        var horizontal = PdfReadModes.IsHorizontal(mode);
        var from = offset;
        var to = offset + Math.Max(0, viewportLength);

        var first = -1;
        var last = -1;
        foreach (var box in layout.Boxes)
        {
            if (box.MainEnd(horizontal) <= from) continue;
            if (box.MainStart(horizontal) >= to) break;
            if (first < 0) first = box.PageIndex;
            last = box.PageIndex;
        }

        if (first < 0)
        {
            // 视口跑到最后页之后（贴底时）：至少把最后一页算成可见
            return (layout.PageCount - 1, layout.PageCount - 1);
        }
        return (first, last);
    }

    /// <summary>按比例（0~1）取阅读偏移（换方式 / 换窗口大小保持位置用）。</summary>
    public static double OffsetForRatio(double ratio, double maximum)
    {
        if (maximum <= 0 || double.IsNaN(ratio)) return 0;
        return ClampOffset(Math.Clamp(ratio, 0, 1) * maximum, maximum);
    }

    /// <summary>当前阅读偏移占总可滚范围的比例（0~1，进度条用）。</summary>
    public static double RatioOf(double offset, double maximum)
        => maximum <= 0 ? 0 : ClampOffset(offset, maximum) / maximum;

    private static bool HasUsableLength(double value) => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

    private static double Safe(double value) => HasUsableLength(value) || value == 0 ? value : 0;
}
