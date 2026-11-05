using YuLiangToolkit.Core.Expressions;

namespace YuLiangToolkit.Tools.FunctionPlot.Core;

/// <summary>
/// 函数绘图的「视口」：世界坐标（数学上的 x / y）与屏幕坐标（控件里的 DIP 像素）之间的换算、缩放与平移。
///
/// <para><b>纯逻辑</b>：不碰界面线程、不绘制、不发通知，所有方法都是可断言的小学算术，
/// 所以 <c>FunctionPlotChecks</c> 可以在无窗口进程里直接验它。</para>
///
/// <para><b>坐标约定</b>：屏幕 y 向下为正，世界 y 向上为正；绘图区（<see cref="PlotRect"/>）四周
/// 留出边距给坐标刻度标签（左 / 下为主）。世界 → 屏幕：
/// <c>screenX = Left + (x - XMin) / (XMax - XMin) * Width</c>，
/// <c>screenY = Top + (YMax - y) / (YMax - YMin) * Height</c>。</para>
///
/// <para><b>缩放</b>：<see cref="ZoomAt(double, double, double)"/> 的 <c>factor &lt; 1</c> 表示放大
/// （世界范围变小），光标下的那个世界点保持不动。</para>
/// </summary>
public sealed class PlotViewport
{
    /// <summary>世界范围的最小跨度：缩放到极限 / 手动范围写成同一个值时也不会除零。</summary>
    public const double MinimumSpan = 1e-9;

    /// <summary>默认 X 范围（双击复位用）。</summary>
    public const double DefaultXMin = -10, DefaultXMax = 10;

    /// <summary>默认 Y 范围（双击复位用）。</summary>
    public const double DefaultYMin = -6, DefaultYMax = 6;

    /// <summary>默认左内边距：留够 y 轴刻度标签的位置。</summary>
    public const double DefaultMarginLeft = 58;

    /// <summary>默认右内边距。</summary>
    public const double DefaultMarginRight = 18;

    /// <summary>默认上内边距：留够 x 轴刻度标签在「0 不在可视范围时」的位置。</summary>
    public const double DefaultMarginTop = 18;

    /// <summary>默认下内边距：留够 x 轴刻度标签的位置。</summary>
    public const double DefaultMarginBottom = 30;

    /// <summary>创建视口（世界范围默认 −10..10 / −6..6，尺寸为 0，等控件量出实际大小后调 <see cref="Resize"/>）。</summary>
    public PlotViewport(
        double xMin = DefaultXMin,
        double xMax = DefaultXMax,
        double yMin = DefaultYMin,
        double yMax = DefaultYMax,
        double marginLeft = DefaultMarginLeft,
        double marginRight = DefaultMarginRight,
        double marginTop = DefaultMarginTop,
        double marginBottom = DefaultMarginBottom)
    {
        MarginLeft = marginLeft;
        MarginRight = marginRight;
        MarginTop = marginTop;
        MarginBottom = marginBottom;
        SetBounds(xMin, xMax, yMin, yMax);
    }

    // ---------------------------------------------------------------- 世界范围

    /// <summary>可视世界范围左边界。</summary>
    public double XMin { get; private set; }

    /// <summary>可视世界范围右边界。</summary>
    public double XMax { get; private set; }

    /// <summary>可视世界范围下边界。</summary>
    public double YMin { get; private set; }

    /// <summary>可视世界范围上边界。</summary>
    public double YMax { get; private set; }

    // ---------------------------------------------------------------- 像素尺寸 / 边距

    /// <summary>控件宽度（DIP）；未量出时为 0。</summary>
    public double Width { get; private set; }

    /// <summary>控件高度（DIP）；未量出时为 0。</summary>
    public double Height { get; private set; }

    /// <summary>左侧给 y 轴刻度标签留的像素。</summary>
    public double MarginLeft { get; }

    /// <summary>右侧留白像素。</summary>
    public double MarginRight { get; }

    /// <summary>上侧留白像素。</summary>
    public double MarginTop { get; }

    /// <summary>下侧给 x 轴刻度标签留的像素。</summary>
    public double MarginBottom { get; }

    /// <summary>真正画曲线的矩形（已经扣掉四周标签边距）。</summary>
    public System.Windows.Rect PlotRect
    {
        get
        {
            var width = Math.Max(0, Width - MarginLeft - MarginRight);
            var height = Math.Max(0, Height - MarginTop - MarginBottom);
            return new System.Windows.Rect(MarginLeft, MarginTop, width, height);
        }
    }

    /// <summary>像素尺寸是否已经量出来（否则没法做世界 / 屏幕换算）。</summary>
    public bool HasSize => PlotRect.Width > 0 && PlotRect.Height > 0;

    /// <summary>每一个世界单位在屏幕上占多少像素（X 方向）。</summary>
    public double PixelsPerUnitX
    {
        get
        {
            var span = XMax - XMin;
            var rect = PlotRect;
            return span > 0 && rect.Width > 0 ? rect.Width / span : 0;
        }
    }

    /// <summary>每一个世界单位在屏幕上占多少像素（Y 方向）。</summary>
    public double PixelsPerUnitY
    {
        get
        {
            var span = YMax - YMin;
            var rect = PlotRect;
            return span > 0 && rect.Height > 0 ? rect.Height / span : 0;
        }
    }

    // ---------------------------------------------------------------- 尺寸 / 范围更新

    /// <summary>控件尺寸变化时调用（窗口缩放 / 分隔条拖动都会走到这里）。</summary>
    public void Resize(double width, double height)
    {
        Width = double.IsFinite(width) && width > 0 ? width : 0;
        Height = double.IsFinite(height) && height > 0 ? height : 0;
    }

    /// <summary>重设世界范围：上下界写反自动交换，跨度太小自动撑到 <see cref="MinimumSpan"/>；非有限值忽略。</summary>
    public void SetBounds(double xMin, double xMax, double yMin, double yMax)
    {
        // 任何一个值非法就整次忽略：宁可视口不动，也不要画出 NaN 网格
        if (!double.IsFinite(xMin) || !double.IsFinite(xMax) || !double.IsFinite(yMin) || !double.IsFinite(yMax)) return;

        if (xMin > xMax) (xMin, xMax) = (xMax, xMin);
        if (yMin > yMax) (yMin, yMax) = (yMax, yMin);

        EnsureSpan(ref xMin, ref xMax);
        EnsureSpan(ref yMin, ref yMax);

        XMin = xMin;
        XMax = xMax;
        YMin = yMin;
        YMax = yMax;
    }

    /// <summary>只改 X 世界范围（Y 保持不动）。</summary>
    public void SetXRange(double min, double max) => SetBounds(min, max, YMin, YMax);

    /// <summary>只改 Y 世界范围（X 保持不动）。</summary>
    public void SetYRange(double min, double max) => SetBounds(XMin, XMax, min, max);

    /// <summary>复制一份当前状态（绘图控件渲染时用快照，避免和鼠标事件里的实时视口互相踩）。</summary>
    public PlotViewport Clone()
        => new(XMin, XMax, YMin, YMax, MarginLeft, MarginRight, MarginTop, MarginBottom)
        {
            Width = Width,
            Height = Height,
        };

    // ---------------------------------------------------------------- 世界 ↔ 屏幕

    /// <summary>世界坐标 → 屏幕坐标（DIP 像素）。没有尺寸时返回 <c>(NaN, NaN)</c>。</summary>
    public System.Windows.Point WorldToScreen(double x, double y)
    {
        var rect = PlotRect;
        if (rect.Width <= 0 || rect.Height <= 0) return new System.Windows.Point(double.NaN, double.NaN);

        var px = rect.Left + (x - XMin) / (XMax - XMin) * rect.Width;
        var py = rect.Top + (YMax - y) / (YMax - YMin) * rect.Height;
        return new System.Windows.Point(px, py);
    }

    /// <summary>世界 x → 屏幕 x。</summary>
    public double WorldToScreenX(double x)
    {
        var rect = PlotRect;
        if (rect.Width <= 0) return double.NaN;
        return rect.Left + (x - XMin) / (XMax - XMin) * rect.Width;
    }

    /// <summary>世界 y → 屏幕 y。</summary>
    public double WorldToScreenY(double y)
    {
        var rect = PlotRect;
        if (rect.Height <= 0) return double.NaN;
        return rect.Top + (YMax - y) / (YMax - YMin) * rect.Height;
    }

    /// <summary>屏幕坐标 → 世界坐标。没有尺寸时返回 <c>(NaN, NaN)</c>。</summary>
    public (double X, double Y) ScreenToWorld(double screenX, double screenY)
    {
        var rect = PlotRect;
        if (rect.Width <= 0 || rect.Height <= 0) return (double.NaN, double.NaN);

        var x = XMin + (screenX - rect.Left) / rect.Width * (XMax - XMin);
        var y = YMax - (screenY - rect.Top) / rect.Height * (YMax - YMin);
        return (x, y);
    }

    /// <summary>屏幕 x → 世界 x（点值表 / 光标读数用）。</summary>
    public double ScreenToWorldX(double screenX)
    {
        var rect = PlotRect;
        if (rect.Width <= 0) return double.NaN;
        return XMin + (screenX - rect.Left) / rect.Width * (XMax - XMin);
    }

    /// <summary>屏幕 y → 世界 y。</summary>
    public double ScreenToWorldY(double screenY)
    {
        var rect = PlotRect;
        if (rect.Height <= 0) return double.NaN;
        return YMax - (screenY - rect.Top) / rect.Height * (YMax - YMin);
    }

    // ---------------------------------------------------------------- 交互：平移 / 缩放

    /// <summary>
    /// 按「鼠标拖了多少像素」平移视口：内容跟着鼠标走，所以世界窗口往反方向移。
    /// <paramref name="dx"/> 向右为正、<paramref name="dy"/> 向下为正。
    /// </summary>
    public void PanByPixels(double dx, double dy)
    {
        var ppuX = PixelsPerUnitX;
        var ppuY = PixelsPerUnitY;
        if (ppuX <= 0 || ppuY <= 0) return;
        if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;

        var worldDx = dx / ppuX;
        var worldDy = dy / ppuY;
        SetBounds(XMin - worldDx, XMax - worldDx, YMin + worldDy, YMax + worldDy);
    }

    /// <summary>
    /// 以屏幕点 <paramref name="screenX"/>, <paramref name="screenY"/> 为不动点缩放：
    /// <paramref name="factor"/> &lt; 1 放大（范围变小）、&gt; 1 缩小。缩放后该屏幕点对应的世界坐标不变。
    /// </summary>
    public void ZoomAt(double screenX, double screenY, double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return;

        var (anchorX, anchorY) = ScreenToWorld(screenX, screenY);
        if (!double.IsFinite(anchorX) || !double.IsFinite(anchorY)) return;

        SetBounds(
            anchorX + (XMin - anchorX) * factor,
            anchorX + (XMax - anchorX) * factor,
            anchorY + (YMin - anchorY) * factor,
            anchorY + (YMax - anchorY) * factor);
    }

    /// <summary>把视口复位到默认范围（−10..10 / −6..6）。</summary>
    public void Reset() => SetBounds(DefaultXMin, DefaultXMax, DefaultYMin, DefaultYMax);

    // ---------------------------------------------------------------- 纯函数

    /// <summary>把任意两个数规整成「合法的世界范围」：非有限值回退到 <paramref name="fallbackMin"/>..<paramref name="fallbackMax"/>。</summary>
    public static (double Min, double Max) NormalizeRange(
        double min,
        double max,
        double fallbackMin = DefaultXMin,
        double fallbackMax = DefaultXMax)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            min = fallbackMin;
            max = fallbackMax;
            if (!double.IsFinite(min) || !double.IsFinite(max))
            {
                min = DefaultXMin;
                max = DefaultXMax;
            }
        }

        if (min > max) (min, max) = (max, min);
        EnsureSpan(ref min, ref max);
        return (min, max);
    }

    /// <summary>
    /// 自动 Y 范围：从采样折线里算出「好看、不被单个尖峰毁掉」的上下界。
    ///
    /// <para>规则：① 忽略 NaN / ∞；② 一个有效值都没有时用回退范围（写反自动交换）；
    /// ③ 数据点够多时按 <paramref name="percentileTrim"/> 两端各裁掉一小撮（tan 的极点不会把 Y 撑到 1e12）；
    /// ④ 上下各留 <paramref name="paddingRatio"/> 的空白；⑤ 最终跨度不小于 <paramref name="minSpan"/>。</para>
    /// </summary>
    public static (double Min, double Max) ComputeAutoYRange(
        IEnumerable<CurvePoint[]>? segments,
        double fallbackMin = DefaultYMin,
        double fallbackMax = DefaultYMax,
        double paddingRatio = 0.08,
        double minSpan = 1e-6,
        double percentileTrim = 0.05)
    {
        var fallback = NormalizeRange(fallbackMin, fallbackMax, DefaultYMin, DefaultYMax);

        var values = new List<double>();
        if (segments is not null)
        {
            foreach (var segment in segments)
            {
                if (segment is null) continue;
                foreach (var point in segment)
                {
                    if (double.IsFinite(point.Y)) values.Add(point.Y);
                }
            }
        }

        if (values.Count == 0) return fallback;

        values.Sort();

        // 数据点太少时不做分位裁剪（否则 2 个点会被裁成 1 个的宽度）
        var trim = values.Count >= 20 ? Math.Clamp(percentileTrim, 0, 0.45) : 0;
        var lowIndex = Math.Clamp((int)Math.Floor(trim * (values.Count - 1)), 0, values.Count - 1);
        var highIndex = Math.Clamp((int)Math.Ceiling((1 - trim) * (values.Count - 1)), 0, values.Count - 1);

        var min = values[lowIndex];
        var max = values[highIndex];
        if (min > max) (min, max) = (max, min);

        if (!double.IsFinite(minSpan) || minSpan <= 0) minSpan = 1e-6;

        var span = max - min;
        if (span < minSpan)
        {
            var middle = (min + max) / 2;
            min = middle - minSpan / 2;
            max = middle + minSpan / 2;
            span = max - min;
        }

        var padding = Math.Max(span * Math.Clamp(paddingRatio, 0, 1), minSpan);
        return (min - padding, max + padding);
    }

    // ---------------------------------------------------------------- 私有

    /// <summary>跨度太小（两个端点几乎重合）时围绕中点撑到最小跨度。</summary>
    private static void EnsureSpan(ref double min, ref double max)
    {
        if (max - min >= MinimumSpan) return;

        var middle = (min + max) / 2;
        if (!double.IsFinite(middle)) middle = 0;
        min = middle - MinimumSpan / 2;
        max = middle + MinimumSpan / 2;
    }
}
