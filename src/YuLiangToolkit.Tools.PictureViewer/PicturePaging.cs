namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>翻页 / 单张模式下的一页摆在哪：<paramref name="PageIndex"/> 为空 = 这个槽位空着（最后不足两页时）。</summary>
public sealed record PicturePagedSlot(int? PageIndex, double X, double Y, double Width, double Height);

/// <summary>
/// 一次翻页/单张画面的排版结果（内容总尺寸 + 每页槽位 + 当前左右页号）。
/// 坐标是**内容坐标系**（原点在内容左上角），界面再按缩放 / 平移把它画到视口里。
/// </summary>
public sealed record PicturePagedLayout(
    double ContentWidth,
    double ContentHeight,
    IReadOnlyList<PicturePagedSlot> Slots,
    int? LeftPageIndex,
    int? RightPageIndex);

/// <summary>
/// 翻页 / 单张模式的**纯逻辑**：页号夹取、双页配对、翻页步进、缩放后的槽位排版、平移夹取。
/// 界面只负责把结果画出来，自检可以直接断言（见 AGENTS.md 2.19）。
///
/// <para><b>双页配对</b>（和用户描述一致：一次翻一“页”，左右会重叠一张）：</para>
/// <list type="bullet">
///   <item>左→右：左槽 = 第 i 张、右槽 = 第 i+1 张；前进 i←i+1（右边移到左边、新的从右边进）；</item>
///   <item>右→左（日式）：右槽 = 第 i 张、左槽 = 第 i+1 张；前进 i←i+1（左边移到右边、新的从左边进）。</item>
/// </list>
/// </summary>
public static class PicturePaging
{
    /// <summary>把页号夹进 [0, pageCount-1]；没有图片时是 0。</summary>
    public static int NormalizePageIndex(int pageIndex, int pageCount)
        => pageCount <= 0 ? 0 : Math.Clamp(pageIndex, 0, pageCount - 1);

    /// <summary>还能不能往前翻（上一张）。</summary>
    public static bool CanGoPrevious(int pageIndex, int pageCount)
        => pageCount > 0 && NormalizePageIndex(pageIndex, pageCount) > 0;

    /// <summary>还能不能往后翻（下一张）。</summary>
    public static bool CanGoNext(int pageIndex, int pageCount)
        => pageCount > 0 && NormalizePageIndex(pageIndex, pageCount) < pageCount - 1;

    /// <summary>按阅读方向前进一步 / 后退一步（<paramref name="forward"/> = 往后读）。</summary>
    public static int StepPage(int pageIndex, int pageCount, bool forward)
        => NormalizePageIndex(NormalizePageIndex(pageIndex, pageCount) + (forward ? 1 : -1), pageCount);

    /// <summary>
    /// 当前画面显示哪两页（单页模式只返回左页、右页为 null）。
    /// 日式双页左右槽互换：右槽是当前页，左槽是下一张。
    /// </summary>
    public static (int? Left, int? Right) VisiblePages(
        PictureScrollMode mode, int pageIndex, int pageCount)
    {
        if (pageCount <= 0) return (null, null);

        var normalizedMode = PictureScrollModes.Normalize(mode);
        var current = NormalizePageIndex(pageIndex, pageCount);

        if (!PictureScrollModes.IsSpread(normalizedMode))
            return (current, null);

        var next = current + 1 < pageCount ? current + 1 : (int?)null;
        return PictureScrollModes.IsReverse(normalizedMode) ? (next, current) : (current, next);
    }

    /// <summary>
    /// 按视口尺寸 + 缩放算这一屏的排版（内容坐标系）。
    /// 双页：内容宽 = 视口宽 × 缩放，左右各占一半；单页：内容 = 视口 × 缩放。
    /// </summary>
    public static PicturePagedLayout ComputeLayout(
        PictureScrollMode mode,
        double viewportWidth,
        double viewportHeight,
        double zoomScale,
        int pageCount,
        int pageIndex)
    {
        var width = viewportWidth > 0 && !double.IsNaN(viewportWidth) ? viewportWidth : 0;
        var height = viewportHeight > 0 && !double.IsNaN(viewportHeight) ? viewportHeight : 0;
        var scale = zoomScale > 0 && !double.IsNaN(zoomScale) ? zoomScale : 1.0;

        var (left, right) = VisiblePages(mode, pageIndex, pageCount);

        if (PictureScrollModes.IsSpread(PictureScrollModes.Normalize(mode)))
        {
            var slotWidth = width / 2 * scale;
            var slotHeight = height * scale;
            var slots = new[]
            {
                new PicturePagedSlot(left, 0, 0, slotWidth, slotHeight),
                new PicturePagedSlot(right, slotWidth, 0, slotWidth, slotHeight),
            };
            return new PicturePagedLayout(slotWidth * 2, slotHeight, slots, left, right);
        }

        var singleWidth = width * scale;
        var singleHeight = height * scale;
        return new PicturePagedLayout(
            singleWidth,
            singleHeight,
            new[] { new PicturePagedSlot(left, 0, 0, singleWidth, singleHeight) },
            left,
            right);
    }

    /// <summary>平移距离夹取（偏移量按“已滚过去多少”算，≥0）：内容比视口小的那个方向不允许平移。</summary>
    public static (double X, double Y) ClampPan(
        double panX,
        double panY,
        double contentWidth,
        double contentHeight,
        double viewportWidth,
        double viewportHeight)
    {
        var maxX = Math.Max(0, contentWidth - viewportWidth);
        var maxY = Math.Max(0, contentHeight - viewportHeight);
        return (Math.Clamp(panX, 0, maxX), Math.Clamp(panY, 0, maxY));
    }

    /// <summary>单页里「上一张 / 下一张」在物理左 / 右按钮上的映射（日式要反过来）。</summary>
    public static (bool LeftIsPrevious, bool RightIsPrevious) ButtonDirections(PictureScrollMode mode)
        => PictureScrollModes.IsReverse(mode) ? (false, true) : (true, false);
}
