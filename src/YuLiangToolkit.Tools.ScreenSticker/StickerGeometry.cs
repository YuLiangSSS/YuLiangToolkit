using System.Windows;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 一张贴纸窗口的位置 + 大小（DIP，和 WPF 的 <c>Window.Left/Top/Width/Height</c> 同一坐标系）。
/// </summary>
public readonly record struct StickerBounds(double Left, double Top, double Width, double Height);

/// <summary>
/// 贴纸窗口的纯几何逻辑：默认大小 / 图片比例 / 以鼠标为锚点缩放 / 角点拖拽缩放 / 夹回屏幕。
///
/// <para><b>为什么单独抽出来</b>：这些换算最容易出现「贴纸飞没影 / 缩到 0 / 锚点乱跳」这类难复现的问题，
/// 抽成纯函数后自检（<c>ScreenStickerChecks</c>）可以无头断言，窗口代码只负责把结果套到 Window 上。</para>
///
/// <para>坐标系注意：<c>Rect</c> 传工作区 / 虚拟屏幕都行（左上角不一定是 0,0，多显示器时尤其别假设）。</para>
/// </summary>
public static class StickerGeometry
{
    /// <summary>贴纸最小宽度（DIP）：再小就抓不住了。</summary>
    public const double MinWidth = 48;

    /// <summary>贴纸最大宽度（DIP）：再大一张图就能盖满两台显示器。</summary>
    public const double MaxWidth = 2400;

    /// <summary>拖出屏幕后至少留在可见区域里的尺寸（DIP）：保证还能用鼠标抓回来。</summary>
    public const double MinVisible = 48;

    /// <summary>滚轮一格的缩放倍率（向上滚一格 = 放大到 1.12 倍）。</summary>
    public const double WheelStepFactor = 1.12;

    /// <summary>连续贴多张时的层叠错位（DIP）。</summary>
    public const double CascadeOffset = 28;

    /// <summary>层叠错位的循环周期（超过这么多个就从头错开，免得一路跑到屏幕外面）。</summary>
    public const int CascadeCycle = 12;

    /// <summary>把宽度夹到 [<see cref="MinWidth"/>, <see cref="MaxWidth"/>]；NaN / 无穷 / 非正数用 <paramref name="fallback"/>。</summary>
    public static double ClampWidth(double width, double fallback = ScreenStickerConfig.DefaultStickerWidth)
    {
        if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0) width = fallback;
        return Math.Clamp(width, MinWidth, MaxWidth);
    }

    /// <summary>把「高 / 宽」比例夹到合理范围（0.05 ~ 20）；坏值当 1:1。</summary>
    public static double ClampAspect(double heightOverWidth)
    {
        if (double.IsNaN(heightOverWidth) || double.IsInfinity(heightOverWidth) || heightOverWidth <= 0) return 1;
        return Math.Clamp(heightOverWidth, 0.05, 20);
    }

    /// <summary>图片像素尺寸 → 高 / 宽 比例（取不到像素尺寸时按 1:1）。</summary>
    public static double AspectOf(double pixelWidth, double pixelHeight)
    {
        if (double.IsNaN(pixelWidth) || double.IsNaN(pixelHeight) || pixelWidth <= 0 || pixelHeight <= 0) return 1;
        return ClampAspect(pixelHeight / pixelWidth);
    }

    /// <summary>按「目标宽度 + 图片比例」算尺寸（不夹屏幕，只夹宽度范围）。</summary>
    public static StickerBounds SizeForWidth(double width, double aspect)
        => new(0, 0, ClampWidth(width), ClampWidth(width) * ClampAspect(aspect));

    /// <summary>
    /// 图片按目标宽度放进工作区：宽度夹到 48 ~ 2400，同时不超过工作区宽 / 高的 90%
    /// （4K 竖图默认 320 宽也会比屏幕高，这时按高度再缩一次）。
    /// </summary>
    public static StickerBounds FitInside(Rect workArea, double width, double aspect)
    {
        aspect = ClampAspect(aspect);
        var maxWidth = MaxWidth;
        if (workArea.Width > 0) maxWidth = Math.Min(maxWidth, workArea.Width * 0.9);
        var fittedWidth = ClampWidth(Math.Min(ClampWidth(width), maxWidth));
        if (workArea.Height > 0 && fittedWidth * aspect > workArea.Height * 0.9)
        {
            fittedWidth = Math.Max(MinWidth, Math.Min(fittedWidth, workArea.Height * 0.9 / aspect));
        }
        return new StickerBounds(0, 0, fittedWidth, fittedWidth * aspect);
    }

    /// <summary>在工作区正中放一个 <paramref name="width"/> × <paramref name="height"/>（已按图片比例）的贴纸。</summary>
    public static StickerBounds CenterIn(Rect workArea, double width, double height)
        => new(
            workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + (workArea.Height - height) / 2,
            width,
            height);

    /// <summary>
    /// 新贴纸的落点：工作区正中 + 按已有贴纸数量做一点层叠错位（连续贴多张不会完全重叠）。
    /// </summary>
    public static StickerBounds PlacementForNew(Rect workArea, StickerBounds size, int existingCount)
    {
        var centered = CenterIn(workArea, size.Width, size.Height);
        var step = Math.Max(0, existingCount) % CascadeCycle;
        var bounds = new StickerBounds(
            centered.Left + step * CascadeOffset,
            centered.Top + step * CascadeOffset,
            size.Width,
            size.Height);
        return ClampToBounds(bounds, workArea);
    }

    /// <summary>
    /// 把一张已保存的贴纸恢复成「尺寸合法 + 至少留 <see cref="MinVisible"/> 在屏幕里」。
    /// 图片比例以磁盘上的图片为准（配置里的 Height 只作参考，比例变了就按新的算）。
    /// </summary>
    public static StickerBounds NormalizeStored(double left, double top, double width, double aspect, Rect screenBounds)
    {
        var size = SizeForWidth(width, aspect);
        var bounds = new StickerBounds(
            double.IsNaN(left) || double.IsInfinity(left) ? 0 : left,
            double.IsNaN(top) || double.IsInfinity(top) ? 0 : top,
            size.Width,
            size.Height);
        return ClampToBounds(bounds, screenBounds);
    }

    /// <summary>
    /// 以 (<paramref name="anchorX"/>, <paramref name="anchorY"/>)（鼠标屏幕坐标）为不动点，按倍率缩放。
    /// 鼠标下面的那一点会一直留在鼠标下面，不会「一滚就从手指底下滑走」。
    /// </summary>
    public static StickerBounds ScaleAround(
        StickerBounds current, double factor, double anchorX, double anchorY, Rect screenBounds)
    {
        if (double.IsNaN(factor) || double.IsInfinity(factor) || factor <= 0) return current;
        var width = ClampWidth(current.Width * factor);
        if (Math.Abs(width - current.Width) < 0.01) return current;

        var ratio = width / Math.Max(0.01, current.Width);
        var height = Math.Max(1, current.Height * ratio);
        var left = anchorX - (anchorX - current.Left) * ratio;
        var top = anchorY - (anchorY - current.Top) * ratio;
        return ClampToBounds(new StickerBounds(left, top, width, height), screenBounds);
    }

    /// <summary>滚轮一格（<paramref name="delta"/> 通常 120）对应多少倍率。</summary>
    public static double WheelFactor(int delta)
    {
        if (delta == 0) return 1;
        return Math.Pow(WheelStepFactor, delta / 120.0);
    }

    /// <summary>
    /// 右下角拖拽缩放：先按横 / 竖位移里更明显的那个折算成宽度增量（保持图片比例），
    /// 再夹宽度范围。左上角不动，所以只需要起点 + 增量。
    /// </summary>
    public static double WidthDeltaForCorner(double dx, double dy, double width, double height)
    {
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx;
        // 竖向位移折算成等比例的横向位移：dh = dy → dw = dy * 宽 / 高
        if (double.IsNaN(height) || height <= 0) return 0;
        return dy * Math.Max(1, width) / height;
    }

    /// <summary>右下角拖拽的结果（左上角不变）。</summary>
    public static StickerBounds ResizeFromCorner(
        StickerBounds start, double dx, double dy, Rect screenBounds)
    {
        var width = ClampWidth(start.Width + WidthDeltaForCorner(dx, dy, start.Width, start.Height));
        var ratio = width / Math.Max(0.01, start.Width);
        var height = Math.Max(1, start.Height * ratio);
        return ClampToBounds(new StickerBounds(start.Left, start.Top, width, height), screenBounds);
    }

    /// <summary>把宽度改成 <paramref name="width"/>，中心点不动（右键菜单「重置缩放」用）。</summary>
    public static StickerBounds ResetWidthKeepingCenter(
        StickerBounds current, double width, double aspect, Rect screenBounds)
    {
        var size = SizeForWidth(width, aspect);
        var centerX = current.Left + current.Width / 2;
        var centerY = current.Top + current.Height / 2;
        var bounds = new StickerBounds(
            centerX - size.Width / 2, centerY - size.Height / 2, size.Width, size.Height);
        return ClampToBounds(bounds, screenBounds);
    }

    /// <summary>
    /// 夹回屏幕：尺寸先夹到合法范围；位置保证窗口至少有 <see cref="MinVisible"/> 见方留在
    /// <paramref name="screenBounds"/> 里（拖到屏幕外也能抓回来）。多显示器时传虚拟屏幕矩形。
    /// </summary>
    public static StickerBounds ClampToBounds(StickerBounds bounds, Rect screenBounds)
    {
        var width = ClampWidth(bounds.Width);
        var height = double.IsNaN(bounds.Height) || double.IsInfinity(bounds.Height) || bounds.Height <= 0
            ? width
            : bounds.Height;
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0) return new StickerBounds(bounds.Left, bounds.Top, width, height);

        var visibleX = Math.Min(MinVisible, width);
        var visibleY = Math.Min(MinVisible, height);
        var minLeft = screenBounds.Left - (width - visibleX);
        var maxLeft = screenBounds.Right - visibleX;
        var minTop = screenBounds.Top - (height - visibleY);
        var maxTop = screenBounds.Bottom - visibleY;

        var left = double.IsNaN(bounds.Left) ? minLeft : Math.Clamp(bounds.Left, minLeft, Math.Max(minLeft, maxLeft));
        var top = double.IsNaN(bounds.Top) ? minTop : Math.Clamp(bounds.Top, minTop, Math.Max(minTop, maxTop));
        return new StickerBounds(left, top, width, height);
    }

    /// <summary>两个矩形中心是否足够接近（恢复布局 / 自检判断「没有乱跳」用）。</summary>
    public static bool NearlyEqual(StickerBounds a, StickerBounds b, double tolerance = 0.5)
        => Math.Abs(a.Left - b.Left) <= tolerance
           && Math.Abs(a.Top - b.Top) <= tolerance
           && Math.Abs(a.Width - b.Width) <= tolerance
           && Math.Abs(a.Height - b.Height) <= tolerance;
}
