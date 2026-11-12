using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.WorldClock.Models;

namespace YuLiangToolkit.Tools.WorldClock.Views;

/// <summary>
/// 圆表盘**自绘控件**：底圆 → 刻度 → 时针 → 分针 → 秒针，全部在 <see cref="OnRender"/> 里画。
///
/// <para><b>为什么自绘</b>：一只表盘有 60 根刻度 + 3 根指针，用控件拼一只钟就要几十个元素；
/// 钟墙一屏十来只，每秒重建一次会明显卡。自绘只有一次 OnRender。</para>
///
/// <para><b>颜色</b>：底色 / 刻度 / 指针都是依赖属性，由 XAML 用 <c>{DynamicResource …}</c> 传主题 token
/// （换主题立即生效）；昼夜底色与时钟数据来自 <see cref="Clock"/>（数据绑定）。</para>
///
/// <para><b>重绘时机</b>：只认 <see cref="Clock"/> 这个依赖属性；<see cref="WorldClockItem"/> 每秒刷新时
/// 只要「时 / 分 / 秒」有变就会引发重绘（它继承 ObservableObject，这里订阅属性变更）。
/// 秒针只在 <see cref="ShowSeconds"/> 为真时画（关掉秒针就不用每秒重绘）。</para>
/// </summary>
public sealed class AnalogClockFace : FrameworkElement
{
    /// <summary>刻度：长刻度（12 / 3 / 6 / 9）与短刻度的长度占比。</summary>
    private const double MajorTickRatio = 0.2;

    private const double MinorTickRatio = 0.055;

    // ---------------------------------------------------------------- 依赖属性

    /// <summary>要画的钟（数据源）。</summary>
    public static readonly DependencyProperty ClockProperty = DependencyProperty.Register(
        nameof(Clock), typeof(WorldClockItem), typeof(AnalogClockFace),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnClockChanged));

    /// <summary>表盘底色（主题 token；夜间由 XAML 换成更暗的一支）。</summary>
    public static readonly DependencyProperty FaceBrushProperty = RegisterBrush(nameof(FaceBrush));

    /// <summary>表盘边框色。</summary>
    public static readonly DependencyProperty RimBrushProperty = RegisterBrush(nameof(RimBrush));

    /// <summary>刻度色。</summary>
    public static readonly DependencyProperty TickBrushProperty = RegisterBrush(nameof(TickBrush));

    /// <summary>时针 / 分针颜色。</summary>
    public static readonly DependencyProperty HandBrushProperty = RegisterBrush(nameof(HandBrush));

    /// <summary>秒针颜色。</summary>
    public static readonly DependencyProperty SecondHandBrushProperty = RegisterBrush(nameof(SecondHandBrush));

    /// <summary>表盘上的中心小圆点颜色。</summary>
    public static readonly DependencyProperty HubBrushProperty = RegisterBrush(nameof(HubBrush));

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(AnalogClockFace),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>要画的钟。</summary>
    public WorldClockItem? Clock
    {
        get => (WorldClockItem?)GetValue(ClockProperty);
        set => SetValue(ClockProperty, value);
    }

    /// <summary>表盘底色。</summary>
    public Brush? FaceBrush
    {
        get => (Brush?)GetValue(FaceBrushProperty);
        set => SetValue(FaceBrushProperty, value);
    }

    /// <summary>表盘边框色。</summary>
    public Brush? RimBrush
    {
        get => (Brush?)GetValue(RimBrushProperty);
        set => SetValue(RimBrushProperty, value);
    }

    /// <summary>刻度色。</summary>
    public Brush? TickBrush
    {
        get => (Brush?)GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    /// <summary>时针 / 分针颜色。</summary>
    public Brush? HandBrush
    {
        get => (Brush?)GetValue(HandBrushProperty);
        set => SetValue(HandBrushProperty, value);
    }

    /// <summary>秒针颜色。</summary>
    public Brush? SecondHandBrush
    {
        get => (Brush?)GetValue(SecondHandBrushProperty);
        set => SetValue(SecondHandBrushProperty, value);
    }

    /// <summary>中心点颜色。</summary>
    public Brush? HubBrush
    {
        get => (Brush?)GetValue(HubBrushProperty);
        set => SetValue(HubBrushProperty, value);
    }

    /// <summary>画不画秒针（与配置里的「显示秒」一致；关掉就不必每秒重绘）。</summary>
    public bool ShowSeconds
    {
        get => (bool)GetValue(ShowSecondsProperty);
        set => SetValue(ShowSecondsProperty, value);
    }

    /// <summary>画不画刻度。</summary>
    public bool ShowTicks
    {
        get => (bool)GetValue(ShowTicksProperty);
        set => SetValue(ShowTicksProperty, value);
    }

    /// <summary>秒针开关的依赖属性（XAML 直接绑配置）。</summary>
    public static readonly DependencyProperty ShowSecondsProperty = DependencyProperty.Register(
        nameof(ShowSeconds), typeof(bool), typeof(AnalogClockFace),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>刻度开关的依赖属性。</summary>
    public static readonly DependencyProperty ShowTicksProperty = DependencyProperty.Register(
        nameof(ShowTicks), typeof(bool), typeof(AnalogClockFace),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>构造：不需要焦点、按像素对齐（细指针不会糊）。</summary>
    public AnalogClockFace()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    // ---------------------------------------------------------------- 数据源订阅

    /// <summary>换了一只钟：退订旧的、订阅新的（只在「时 / 分 / 秒」变化时重绘）。</summary>
    private static void OnClockChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var face = (AnalogClockFace)d;
        if (e.OldValue is WorldClockItem oldClock) oldClock.PropertyChanged -= face.OnClockPropertyChanged;
        if (e.NewValue is WorldClockItem newClock) newClock.PropertyChanged += face.OnClockPropertyChanged;
        face.InvalidateVisual();
    }

    private void OnClockPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 只关心影响画面的几个字段：颜色 / 文字的变化由 XAML 那边的绑定处理，这里不用管
        if (e.PropertyName is nameof(WorldClockItem.Hour)
            or nameof(WorldClockItem.Minute)
            or nameof(WorldClockItem.Second)
            or nameof(WorldClockItem.Phase)
            or nameof(WorldClockItem.IsZoneMissing))
        {
            InvalidateVisual();
        }
    }

    // ---------------------------------------------------------------- 绘制
    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - 2;
        if (radius <= 2) return;

        var facePen = RimBrush is { } rim ? Freeze(new Pen(rim, 1.5)) : null;
        dc.DrawEllipse(FaceBrush, facePen, center, radius, radius);

        if (ShowTicks) DrawTicks(dc, center, radius);

        var clock = Clock;
        var hour = clock?.Hour ?? 0;
        var minute = clock?.Minute ?? 0;
        var second = clock?.Second ?? 0;

        // 指针角度：从 12 点方向顺时针；时针算上分钟、分针算上秒，指针才不会「跳着走」
        // 长度都留在数字之内（数字在 0.60R 处），指针不压字、刻度也不压数字。
        DrawHand(dc, center, radius * 0.3, (hour % 12 + minute / 60.0) * 30, 3.4, HandBrush);
        DrawHand(dc, center, radius * 0.44, minute + second / 60.0, 2.4, HandBrush);
        if (ShowSeconds) DrawHand(dc, center, radius * 0.5, second * 6, 1.2, SecondHandBrush);

        if (HubBrush is { } hub) dc.DrawEllipse(hub, null, center, 3.2, 3.2);
    }

    /// <summary>
    /// 画 60 根刻度：整点位置（12 / 3 / 6 / 9）更长更粗。
    /// 打开刻度时顺带在 12 / 3 / 6 / 9 内侧写数字 —— 光看刻度数不出几点，数字是给人读的。
    /// </summary>
    private void DrawTicks(DrawingContext dc, Point center, double radius)
    {
        if (TickBrush is not { } brush) return;

        var minorPen = Freeze(new Pen(brush, 1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var majorPen = Freeze(new Pen(brush, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        for (var i = 0; i < 60; i++)
        {
            var isMajor = i % 5 == 0;
            var length = radius * (isMajor ? MajorTickRatio : MinorTickRatio);
            var angle = i * 6 * Math.PI / 180;
            var outer = PointOnCircle(center, radius - radius * 0.04, angle);
            var inner = PointOnCircle(center, radius - radius * 0.04 - length, angle);
            dc.DrawLine(isMajor ? majorPen : minorPen, outer, inner);
        }

        DrawHourNumerals(dc, center, radius, brush);
    }

    /// <summary>12 / 3 / 6 / 9 四个数字（表盘小了就不画，免得糊成一团）。</summary>
    private void DrawHourNumerals(DrawingContext dc, Point center, double radius, Brush brush)
    {
        if (radius < 46) return;

        var size = Math.Clamp(radius * 0.2, 9, 20);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Microsoft YaHei UI");

        foreach (var (label, hour) in new[] { ("12", 0), ("3", 3), ("6", 6), ("9", 9) })
        {
            var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, size, brush, pixelsPerDip);
            var angle = hour * 30 * Math.PI / 180;
            var position = PointOnCircle(center, radius * 0.6, angle);
            dc.DrawText(text, new Point(position.X - text.Width / 2, position.Y - text.Height / 2));
        }
    }

    /// <summary>画一根指针。<paramref name="angleDegrees"/> 从 12 点方向顺时针。</summary>
    private static void DrawHand(DrawingContext dc, Point center, double length, double angleDegrees,
        double thickness, Brush? brush)
    {
        if (brush is null) return;

        var angle = angleDegrees * Math.PI / 180;
        // 指针往反方向多伸一小截（更像真表），尾部长度取指针长度的 18%
        var tail = length * 0.18;
        var start = PointOnCircle(center, -tail, angle);
        var end = PointOnCircle(center, length, angle);

        var pen = Freeze(new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        dc.DrawLine(pen, start, end);
    }

    /// <summary>圆上的点（角度 0 = 12 点方向，顺时针为正）。</summary>
    private static Point PointOnCircle(Point center, double radius, double angleRadians)
        => new(center.X + radius * Math.Sin(angleRadians), center.Y - radius * Math.Cos(angleRadians));

    /// <summary>把画笔冻结（每帧 new 出来的对象冻结后交给 WPF 更省事）。</summary>
    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
