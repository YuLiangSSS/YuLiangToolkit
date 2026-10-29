using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Kps.Views.Controls;

/// <summary>
/// KPS 折线图控件：不依赖任何图表库，自己 <c>OnRender</c> 画「最近 KPS 折线 + 折线下方半透明填充 + 可选网格」。
///
/// <para><b>数据口径</b>：<see cref="Values"/> 按时间先后排列（最后一个点 = 最新采样），
/// 横轴把整条曲线铺满控件宽度、最后一个点固定在最右边；纵轴按 <c>值 / max(1, <see cref="MaxValue"/>)</c>
/// 映射到顶部留 6px 的绘图区，超出上限的值贴顶（不裁剪掉）。没有数据时只画一条底部基线。</para>
///
/// <para><b>性能</b>：只画一根折线（最多 600 个点），每次 DP 变化重绘一次（UI 10Hz 足够）；
/// Pen / Brush 都缓存并尽量冻结，不在每帧重新 new；不订阅任何全局渲染事件。</para>
///
/// <para><b>画刷约定</b>：<see cref="LineBrush"/> / <see cref="GridBrush"/> 为空时用内置浅色；
/// <see cref="FillBrush"/> 为空时从 <see cref="LineBrush"/> 派生，纯色填充会乘上 0.18 透明度
/// （已经带透明度的画刷按原样使用，方便调用方自己调深浅）。</para>
/// </summary>
public sealed class KpsGraphView : FrameworkElement
{
    // ---------- 内置配色（调用方不设时用；Overlay 在 XAML 里显式给，避免依赖主题） ----------

    /// <summary>默认折线色（浅蓝）。</summary>
    private static readonly Color DefaultLineColor = Color.FromRgb(0x7F, 0xD1, 0xFF);
    private static readonly Brush DefaultLineBrush = CreateFrozenBrush(DefaultLineColor, 0xFF);
    private static readonly Brush DefaultGridBrush = CreateFrozenBrush(Color.FromRgb(0x80, 0x80, 0x80), 0x33);

    /// <summary>顶部留白（DIP）：峰值不要贴着控件上边。</summary>
    private const double TopPadding = 6;

    /// <summary>底部留白（DIP）：0 值折线和基线不重叠。</summary>
    private const double BottomPadding = 2;

    /// <summary>横向网格线条数（不含上下边界）。</summary>
    private const int HorizontalGridLines = 4;

    /// <summary>纵向网格线条数（均分，不画左右边界）。</summary>
    private const int VerticalGridLines = 6;

    /// <summary>纯色填充透明度（0.18）。</summary>
    private const double FillOpacity = 0.18;

    private Pen? _linePen;
    private Brush? _linePenSource;
    private Pen? _gridPen;
    private Brush? _gridPenSource;
    private Brush? _fillBrush;
    private Brush? _fillSource;

    // ============================== 依赖属性 ==============================

    /// <summary>采样点（按时间先后；最后一个点是最新的；null / 空 = 无数据）。</summary>
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>纵轴上限（内部按 max(1, 值) 处理；NaN / 无穷也当 1）。</summary>
    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>折线颜色（null = 内置浅蓝）。</summary>
    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>折线下方填充色（null = 从 <see cref="LineBrush"/> 派生 0.18 透明度）。</summary>
    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>网格线颜色（null = 内置半透明灰）。</summary>
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>是否画网格线。</summary>
    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public KpsGraphView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    /// <summary>采样点（按时间先后；最后一个点是最新的）。</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>纵轴上限（>= 1）。</summary>
    public double MaxValue
    {
        get => (double)GetValue(MaxValueProperty);
        set => SetValue(MaxValueProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? FillBrush
    {
        get => (Brush?)GetValue(FillBrushProperty);
        set => SetValue(FillBrushProperty, value);
    }

    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public bool ShowGrid
    {
        get => (bool)GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    // ============================== 绘制 ==============================

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var plot = new Rect(0, TopPadding, width, Math.Max(1, height - TopPadding - BottomPadding));
        var max = NormalizeMax(MaxValue);
        var lineBrush = LineBrush ?? DefaultLineBrush;
        var linePen = GetLinePen(lineBrush);

        if (ShowGrid) DrawGrid(drawingContext, width, plot);

        // 无数据：只画一条底部基线（不抛异常、不画脏东西）
        if (Values is not { Count: > 0 } values)
        {
            drawingContext.DrawLine(linePen, new Point(0, plot.Bottom), new Point(width, plot.Bottom));
            return;
        }

        var count = values.Count;

        // 折线（最后一个点固定在最右侧）
        var lineGeometry = new StreamGeometry();
        using (var ctx = lineGeometry.Open())
        {
            ctx.BeginFigure(PointAt(0), false, false);
            for (var i = 1; i < count; i++) ctx.LineTo(PointAt(i), true, false);
        }
        lineGeometry.Freeze();

        // 折线下方区域：从基线左下角起，沿折线走到右下角收口
        var fillGeometry = new StreamGeometry();
        using (var ctx = fillGeometry.Open())
        {
            ctx.BeginFigure(new Point(0, plot.Bottom), true, true);
            for (var i = 0; i < count; i++) ctx.LineTo(PointAt(i), true, false);
            ctx.LineTo(new Point(width, plot.Bottom), true, false);
        }
        fillGeometry.Freeze();

        drawingContext.DrawGeometry(GetFillBrush(lineBrush), null, fillGeometry);
        drawingContext.DrawGeometry(null, linePen, lineGeometry);

        // 只有一个采样点时折线退化成一个点：补个小圆点（靠右放，避免被控件边界裁掉一半）
        if (count == 1)
            drawingContext.DrawEllipse(lineBrush, null, new Point(Math.Max(0, width - 2.5), PointAt(0).Y), 2.2, 2.2);

        Point PointAt(int index)
        {
            var value = values[index];
            if (double.IsNaN(value) || value < 0) value = 0;
            var x = count > 1 ? width * index / (count - 1) : width;
            var y = plot.Bottom - Math.Clamp(value / max, 0, 1) * plot.Height;
            return new Point(x, y);
        }
    }

    private void DrawGrid(DrawingContext drawingContext, double width, Rect plot)
    {
        var gridPen = GetGridPen(GridBrush ?? DefaultGridBrush);

        for (var i = 1; i <= HorizontalGridLines; i++)
        {
            var y = plot.Top + plot.Height * i / (HorizontalGridLines + 1);
            drawingContext.DrawLine(gridPen, new Point(0, y), new Point(width, y));
        }

        for (var i = 1; i < VerticalGridLines; i++)
        {
            var x = width * i / VerticalGridLines;
            drawingContext.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }

        // 底部基线用网格笔单独画一条，让「0 KPS」的位置更明确
        drawingContext.DrawLine(gridPen, new Point(0, plot.Bottom), new Point(width, plot.Bottom));
    }

    // ============================== 画笔缓存 ==============================

    private Pen GetLinePen(Brush brush)
    {
        if (_linePen is null || !ReferenceEquals(brush, _linePenSource))
        {
            _linePenSource = brush;
            _linePen = CreatePen(brush, 2);
        }
        return _linePen;
    }

    private Pen GetGridPen(Brush brush)
    {
        if (_gridPen is null || !ReferenceEquals(brush, _gridPenSource))
        {
            _gridPenSource = brush;
            _gridPen = CreatePen(brush, 1);
        }
        return _gridPen;
    }

    private Brush GetFillBrush(Brush lineBrush)
    {
        var source = FillBrush ?? lineBrush;
        if (_fillBrush is not null && ReferenceEquals(source, _fillSource)) return _fillBrush;

        _fillSource = source;
        _fillBrush = source switch
        {
            // 纯色：已经带透明度就按调用方给的来（方便自行调深浅），不透明才乘 0.18
            SolidColorBrush solid => CreateFrozenBrush(
                solid.Color,
                solid.Color.A < 0xFF ? solid.Color.A : (byte)Math.Round(FillOpacity * 255)),
            // 渐变等自定义画刷：直接交给 WPF 画（透明度由画刷自己控制）
            _ => source,
        };
        return _fillBrush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        // 画刷可能绑着界面 / 主题资源，冻不了就保持可写（不影响功能，只是渲染略慢）
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static Brush CreateFrozenBrush(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>纵轴上限归一化：非法值 / &lt; 1 都当 1（空数据时也能画底线）。</summary>
    private static double NormalizeMax(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 1 : Math.Max(1, value);
}
