using System.Windows;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 一张贴纸窗口的位置 + 大小（DIP，和 WPF 的 <c>Window.Left/Top/Width/Height</c> 同一坐标系）。
/// </summary>
public readonly record struct StickerBounds(double Left, double Top, double Width, double Height);

/// <summary>
/// 贴纸窗口可拉伸的方向（四边 + 四角）。窗口按鼠标所在边缘决定光标和拉伸方式。
/// </summary>
public enum StickerResizeHandle
{
    None,
    Left,
    Right,
    Top,
    Bottom,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

/// <summary>
/// 贴纸窗口的纯几何逻辑：默认大小 / 图片比例 / 以鼠标为锚点缩放 / 四边四角拉伸 / 夹回屏幕。
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

    /// <summary>贴纸最大宽度（DIP）：既防手滑放大到离谱，也要给「按图片原始尺寸显示」留够空间。</summary>
    public const double MaxWidth = 4096;

    /// <summary>贴纸最小高度（DIP）：自由拉伸时也不允许压成一条线（超宽图按比例可以更扁）。</summary>
    public const double MinHeight = 16;

    /// <summary>贴纸最大高度（DIP）。</summary>
    public const double MaxHeight = 4096;

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
    /// 图片按目标宽度放进工作区：宽度夹到 48 ~ 4096，同时不超过工作区宽 / 高的 90%
    /// （很大的图 / 竖图都会先缩到屏幕里，保证整张贴纸一开始就看得全）。
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

    /// <summary>
    /// 一张新贴纸的「自身默认大小」：按图片的**原始像素尺寸**（1:1，DIP）显示，
    /// 但不超过 <see cref="MaxWidth"/> / 工作区 90%（手机巨图 / 4K 截图也会先缩到屏幕里）；
    /// 极小图按 <see cref="MinWidth"/> 兜底（保证还能用鼠标抓住）。
    ///
    /// <para>用户要求「贴纸出现时就是它自身的默认大小」，所以不再有「新贴纸默认宽度」设置；
    /// 之后想再回到这个尺寸，用贴纸右键菜单的「恢复默认大小」。</para>
    /// </summary>
    public static StickerBounds NaturalSize(Rect workArea, double pixelWidth, double pixelHeight)
    {
        var aspect = AspectOf(pixelWidth, pixelHeight);
        var width = double.IsNaN(pixelWidth) || double.IsInfinity(pixelWidth) || pixelWidth <= 0
            ? ScreenStickerConfig.DefaultStickerWidth
            : pixelWidth;
        return FitInside(workArea, width, aspect);
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

    /// <summary>把高度夹到 [<see cref="MinHeight"/>, <see cref="MaxHeight"/>]；NaN / 无穷 / 非正数用宽度兜底。</summary>
    public static double ClampHeight(double height, double fallback = MinHeight)
    {
        if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0) height = fallback;
        return Math.Clamp(height, MinHeight, MaxHeight);
    }

    /// <summary>
    /// 把一张已保存的贴纸恢复成「尺寸合法 + 至少留 <see cref="MinVisible"/> 在屏幕里」。
    /// <b>宽高都用配置里存的值</b>（自由拉伸过的贴纸比例可以不是图片原始比例，不能强行按比例改回去）。
    /// </summary>
    public static StickerBounds NormalizeStored(double left, double top, double width, double height, Rect screenBounds)
    {
        var bounds = new StickerBounds(
            double.IsNaN(left) || double.IsInfinity(left) ? 0 : left,
            double.IsNaN(top) || double.IsInfinity(top) ? 0 : top,
            ClampWidth(width),
            ClampHeight(height));
        return ClampToBounds(bounds, screenBounds);
    }

    /// <summary>
    /// 老接口：用宽度 + 图片比例算高度（自检 / 兼容旧配置用）。
    /// </summary>
    public static StickerBounds NormalizeStoredByAspect(double left, double top, double width, double aspect, Rect screenBounds)
    {
        var size = SizeForWidth(width, aspect);
        return NormalizeStored(left, top, size.Width, size.Height, screenBounds);
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
    /// 四边 / 四角拉伸。
    ///
    /// <para><paramref name="dx"/>/<paramref name="dy"/> 是鼠标相对按下位置的位移（DIP）。默认**自由拉伸**
    /// （宽高独立变化，图片会被拉长 / 压扁）；<paramref name="keepAspect"/> = true（按住 Shift）时
    /// 保持按下时的宽高比：左右边只按宽度驱动、上下边只按高度驱动、角点取变化更明显的那个轴。</para>
    ///
    /// <para>被拖的反方向 / 对边固定；结果只保证宽高在合法范围，位置是否越界由调用方在松手时
    /// <see cref="ClampToBounds"/> 处理（拖出屏幕时体验更连续）。</para>
    /// </summary>
    public static StickerBounds ResizeByHandle(
        StickerBounds start, StickerResizeHandle handle, double dx, double dy, bool keepAspect)
    {
        if (handle == StickerResizeHandle.None) return start;
        if (double.IsNaN(dx) || double.IsInfinity(dx)) dx = 0;
        if (double.IsNaN(dy) || double.IsInfinity(dy)) dy = 0;

        var resizeLeft = handle is StickerResizeHandle.Left or StickerResizeHandle.TopLeft or StickerResizeHandle.BottomLeft;
        var resizeRight = handle is StickerResizeHandle.Right or StickerResizeHandle.TopRight or StickerResizeHandle.BottomRight;
        var resizeTop = handle is StickerResizeHandle.Top or StickerResizeHandle.TopLeft or StickerResizeHandle.TopRight;
        var resizeBottom = handle is StickerResizeHandle.Bottom or StickerResizeHandle.BottomLeft or StickerResizeHandle.BottomRight;

        var width = start.Width + (resizeRight ? dx : 0) - (resizeLeft ? dx : 0);
        var height = start.Height + (resizeBottom ? dy : 0) - (resizeTop ? dy : 0);
        var aspect = ClampAspect(start.Height / Math.Max(1, start.Width));

        if (keepAspect)
        {
            var horizontalOnly = (resizeLeft || resizeRight) && !resizeTop && !resizeBottom;
            var verticalOnly = (resizeTop || resizeBottom) && !resizeLeft && !resizeRight;

            if (horizontalOnly)
            {
                width = ClampResizeWidth(width);
                height = ClampResizeHeight(width * aspect);
            }
            else if (verticalOnly)
            {
                height = ClampResizeHeight(height);
                width = ClampResizeWidth(height / aspect);
            }
            else
            {
                // 角点：看哪个方向的变化更明显，用它当驱动轴
                var widthChange = Math.Abs(width - start.Width) / Math.Max(1, start.Width);
                var heightChange = Math.Abs(height - start.Height) / Math.Max(1, start.Height);
                if (widthChange >= heightChange)
                {
                    width = ClampResizeWidth(width);
                    height = ClampResizeHeight(width * aspect);
                }
                else
                {
                    height = ClampResizeHeight(height);
                    width = ClampResizeWidth(height / aspect);
                }
            }
        }
        else
        {
            width = ClampResizeWidth(width);
            height = ClampResizeHeight(height);
        }

        var left = resizeLeft ? start.Left + start.Width - width : start.Left;
        var top = resizeTop ? start.Top + start.Height - height : start.Top;
        return new StickerBounds(left, top, width, height);
    }

    /// <summary>拉伸时的宽度夹取：负数 / 0 都是「拖过头」→ 直接贴到最小宽度（不是回退默认 320）。</summary>
    private static double ClampResizeWidth(double width)
    {
        if (double.IsNaN(width)) return MinWidth;
        return Math.Clamp(width, MinWidth, MaxWidth);
    }

    /// <summary>拉伸时的高度夹取：负数 / 0 都贴到最小高度。</summary>
    private static double ClampResizeHeight(double height)
    {
        if (double.IsNaN(height)) return MinHeight;
        return Math.Clamp(height, MinHeight, MaxHeight);
    }

    /// <summary>把宽度改成 <paramref name="width"/>，中心点不动（右键菜单「恢复默认大小」用）。</summary>
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
