using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.TaskManager.Models;

namespace YuLiangToolkit.Tools.TaskManager.Views;

/// <summary>
/// 性能卡片里那一条实时折线图（自绘，零控件）。
///
/// <para><b>数据来源</b>：绑 <see cref="History"/>（<see cref="PerformanceHistory"/>）+ <see cref="Series"/>，
/// 每次调用 <see cref="Refresh"/> 就按当前历史重画。外层每轮采样后调一次即可，
/// 不用把 WPF 绑定绑定在「第几个点」上（历史是不可变的数组，绑定不到变化的点）。</para>
///
/// <para><b>观感</b>：背景深色渐变 + 水平网格线（25 / 50 / 75%）+ 折线 + 半透明填充，
/// 模仿 Windows 任务管理器性能页的曲线。纵轴固定 0 ~ 100，横轴铺满 <see cref="History"/>.Count 个点。</para>
/// </summary>
public sealed class PerformanceGraph : FrameworkElement
{
    // 曲线满量程是 100%，纵轴固定 0~100 就好，不做自动缩放（自动缩放会让曲线跳动太厉害）。
    private const double MaxValue = 100.0;
    private static readonly Color FillTopColor = Color.FromArgb(0x55, 0x8A, 0x7C, 0xF0);
    private static readonly Color FillBottomColor = Color.FromArgb(0x05, 0x8A, 0x7C, 0xF0);
    private static readonly Brush LineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x7C, 0xF0)));
    private static readonly Pen GridPen = FreezePen(new Pen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen LinePen = FreezePen(new Pen(LineBrush, 1.6));

    /// <summary>数据缓冲（依赖属性，XAML 里可用 {Binding History}）。</summary>
    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(PerformanceHistory), typeof(PerformanceGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>画哪条序列（依赖属性，XAML 里可写 Series="Cpu"）。</summary>
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(PerformanceSeries), typeof(PerformanceGraph),
        new FrameworkPropertyMetadata(PerformanceSeries.Cpu, FrameworkPropertyMetadataOptions.AffectsRender));

    public PerformanceHistory? History
    {
        get => (PerformanceHistory?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    public PerformanceSeries Series
    {
        get => (PerformanceSeries)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    /// <summary>数据更新后调一次（外层采样循环里）。</summary>
    public void Refresh() => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        // 底色（圆角由外层 Border 的 ClipToBounds 裁剪）
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x00, 0x00)), null, new Rect(0, 0, w, h));

        // 水平网格线：25 / 50 / 75%
        foreach (var frac in new[] { 0.25, 0.5, 0.75 })
        {
            var y = h * (1 - frac);
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        var history = History;
        var count = history?.Count ?? 0;
        if (history is null || count == 0) return;

        // 横轴：Capacity 个点铺满整宽（不足时右侧留空，曲线从左侧长出来）
        var capacity = PerformanceHistory.Capacity;
        var stepX = capacity > 1 ? w / (capacity - 1) : w;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var firstX = 0.0;
            var firstY = h * (1 - history[Series, 0] / MaxValue);
            ctx.BeginFigure(new Point(firstX, firstY), isFilled: true, isClosed: true);

            for (var i = 1; i < count; i++)
            {
                var x = i * stepX;
                var y = h * (1 - history[Series, i] / MaxValue);
                ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
            }

            var lastX = (count - 1) * stepX;
            // 收尾：右下 → 左下闭合，形成填充区域
            ctx.LineTo(new Point(lastX, h), isStroked: false, isSmoothJoin: false);
            ctx.LineTo(new Point(firstX, h), isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();

        var fillBrush = new LinearGradientBrush(FillTopColor, FillBottomColor, 90);
        fillBrush.Freeze();
        dc.DrawGeometry(fillBrush, null, geometry);

        // 折线本身（只描上边界，不描闭合的底边）：再建一条只走点的 StreamGeometry
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(0, h * (1 - history[Series, 0] / MaxValue)), isFilled: false, isClosed: false);
            for (var i = 1; i < count; i++)
            {
                ctx.LineTo(new Point(i * stepX, h * (1 - history[Series, i] / MaxValue)), isStroked: true, isSmoothJoin: false);
            }
        }
        line.Freeze();
        dc.DrawGeometry(null, LinePen, line);
    }

    private static Brush Freeze(Brush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Pen pen)
    {
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }
}
