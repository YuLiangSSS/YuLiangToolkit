using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Kps.Views.Controls;

/// <summary>
/// KPS 折线图控件（骨架版本；完整绘制由队友实现）。
///
/// <para>绑定 <see cref="Values"/>（每个点 = 一次采样的当前 KPS）和 <see cref="MaxValue"/>（纵轴上限），
/// 自己 <c>OnRender</c> 画折线 + 填充 + 网格，不依赖任何图表库（项目约定不引第三方包）。</para>
/// </summary>
public sealed class KpsGraphView : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxValueProperty = DependencyProperty.Register(
        nameof(MaxValue), typeof(double), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(1d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillBrushProperty = DependencyProperty.Register(
        nameof(FillBrush), typeof(Brush), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(KpsGraphView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>采样点（按时间先后；最后一个点是最新的）。</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>纵轴上限（>= 1；截图 / 空数据时也能画一条底线）。</summary>
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

    protected override void OnRender(DrawingContext drawingContext)
    {
        // 骨架：只画背景 / 网格占位；完整折线由队友实现。
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            for (var i = 0; i <= 4; i++)
            {
                var y = bounds.Height * i / 4d;
                drawingContext.DrawLine(pen, new Point(0, y), new Point(bounds.Width, y));
            }
        }
    }
}
