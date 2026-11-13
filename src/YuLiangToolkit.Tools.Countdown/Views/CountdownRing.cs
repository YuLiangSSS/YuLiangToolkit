using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Countdown.Views;

/// <summary>
/// 倒计时进度环（自绘）：底环 + 进度弧。
///
/// <para>自绘而不是用控件拼：只有两段弧，自绘省控件、也不会因为动画掉帧。</para>
///
/// <para>⚠ 颜色全部由 XAML 用主题 token 传进来（<see cref="RingBrush"/> / <see cref="TrackBrush"/>），
/// 这样换主题立即生效；本类不自己读资源。</para>
/// </summary>
public sealed class CountdownRing : FrameworkElement
{
    /// <summary>进度（0~1，从顶部顺时针填充）。</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(CountdownRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>是否显示环（关掉时只留数字）。</summary>
    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.Register(
        nameof(ShowRing), typeof(bool), typeof(CountdownRing),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>进度弧颜色。</summary>
    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(CountdownRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>底环颜色。</summary>
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(CountdownRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>时间到（整环换成警示色）。</summary>
    public static readonly DependencyProperty FinishedProperty = DependencyProperty.Register(
        nameof(Finished), typeof(bool), typeof(CountdownRing),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>时间到时的警示色。</summary>
    public static readonly DependencyProperty FinishedBrushProperty = DependencyProperty.Register(
        nameof(FinishedBrush), typeof(Brush), typeof(CountdownRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>构造：不参与命中测试（点击穿透到数字）。</summary>
    public CountdownRing()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>进度（0~1）。</summary>
    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    /// <summary>是否显示进度环。</summary>
    public bool ShowRing
    {
        get => (bool)GetValue(ShowRingProperty);
        set => SetValue(ShowRingProperty, value);
    }

    /// <summary>进度弧颜色。</summary>
    public Brush? RingBrush
    {
        get => (Brush?)GetValue(RingBrushProperty);
        set => SetValue(RingBrushProperty, value);
    }

    /// <summary>底环颜色。</summary>
    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    /// <summary>是否已经时间到。</summary>
    public bool Finished
    {
        get => (bool)GetValue(FinishedProperty);
        set => SetValue(FinishedProperty, value);
    }

    /// <summary>时间到时的警示色。</summary>
    public Brush? FinishedBrush
    {
        get => (Brush?)GetValue(FinishedBrushProperty);
        set => SetValue(FinishedBrushProperty, value);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 8) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var thickness = Math.Max(6, size * 0.055);
        var radius = size / 2 - thickness / 2 - 2;
        if (radius <= 2) return;

        // 底环
        if (TrackBrush is { } track) dc.DrawEllipse(null, Freeze(new Pen(track, thickness)), center, radius, radius);

        if (!ShowRing) return;

        var progress = Math.Clamp(Progress, 0, 1);
        if (progress <= 0) return;

        // 时间到：整环换成警示色（一眼看出结束了）
        var brush = Finished && FinishedBrush is not null ? FinishedBrush : RingBrush;
        if (brush is null) return;

        // 进度弧：从 12 点方向顺时针，画到 progress 位置
        var pen = Freeze(new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

        // 少量进度时用整圆更省事（也避免圆弧退化）
        if (progress >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var start = AnglePoint(center, radius, -90);
        var end = AnglePoint(center, radius, -90 + 360 * progress);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0,
                isLargeArc: progress > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    /// <summary>圆上某角度的点（0° = 3 点方向，顺时针为正）。</summary>
    private static Point AnglePoint(Point center, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }

    private static Pen Freeze(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
