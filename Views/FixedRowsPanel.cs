using System.Windows;
using System.Windows.Controls;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 固定行数、横向溢出的排列面板（主页「快速筛选」标签墙的 3 行 + 横向滚动用）。
///
/// <para><b>为什么不直接用 WrapPanel</b>：WrapPanel 在横向滚动里拿到的是无限宽，永远不会换行；
/// 而普通换行又会把 170+ 个标签铺成一堵很高的墙（原来就是这样）。这个面板把子元素按
/// <see cref="Rows"/> 行<b>竖着填</b>（第一列从上到下，再填下一列），整体宽度 = 所有列宽之和，
/// 放进 <c>HorizontalScrollBarVisibility="Auto"</c> 的 ScrollViewer 就能左右拖。</para>
///
/// <para><b>尺寸规则</b>：子元素的 Margin 参与测量（DataTemplate 里的 <c>0,0,8,8</c> 就是行列间距），
/// 列宽 = 该列最宽的子元素，行高 = 这一行所有子元素里最高的。子元素按自身宽度左对齐，
/// 但所有行在所有列上对齐 —— 看起来是自然宽度的标签云，而不是被拉成等宽的格子。</para>
///
/// <para>只用于「标签墙」这种数量多、高度固定的场景；不虚拟化（170 个 Button 一次测完没有压力）。</para>
/// </summary>
public sealed class FixedRowsPanel : Panel
{
    /// <summary>固定行数（默认 3，必须 ≥ 1）。改它会重新测量。</summary>
    public static readonly DependencyProperty RowsProperty = DependencyProperty.Register(
        nameof(Rows),
        typeof(int),
        typeof(FixedRowsPanel),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => (int)value >= 1);

    /// <summary>固定行数（默认 3，必须 ≥ 1）。</summary>
    public int Rows
    {
        get => (int)GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    /// <summary>每列的起点 / 子元素个数 / 列宽（每次测量重算，排列时直接用）。</summary>
    private readonly List<ColumnLayout> _columns = new();

    /// <summary>每一行的统一高度（下标 = 行号）。</summary>
    private double[] _rowHeights = Array.Empty<double>();

    /// <summary>一列的布局信息：从第几个子元素开始、有几个、列宽多少。</summary>
    private readonly record struct ColumnLayout(int Start, int Count, double Width);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        _columns.Clear();

        var rows = Math.Max(1, Rows);
        var columnCount = (children.Count + rows - 1) / rows;

        if (_rowHeights.Length != rows) _rowHeights = new double[rows];
        else Array.Clear(_rowHeights, 0, _rowHeights.Length);

        var totalWidth = 0.0;
        for (var column = 0; column < columnCount; column++)
        {
            var start = column * rows;
            var end = Math.Min(start + rows, children.Count);
            var columnWidth = 0.0;

            for (var i = start; i < end; i++)
            {
                // 每个子元素按无限空间量一遍：这里要的是标签的自然尺寸（宽度随文字长短变），
                // 不是按可用宽度压缩 / 拉伸。
                var child = children[i];
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var size = child.DesiredSize;   // 已经含 Margin
                columnWidth = Math.Max(columnWidth, size.Width);
                _rowHeights[i - start] = Math.Max(_rowHeights[i - start], size.Height);
            }

            _columns.Add(new ColumnLayout(start, end - start, columnWidth));
            totalWidth += columnWidth;
        }

        var totalHeight = 0.0;
        foreach (var height in _rowHeights) totalHeight += height;
        return new Size(totalWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var x = 0.0;

        foreach (var column in _columns)
        {
            var y = 0.0;
            for (var row = 0; row < column.Count; row++)
            {
                var child = children[column.Start + row];
                var size = child.DesiredSize;
                // 子元素保持自身宽度（短标签不被拉宽），列内左对齐；行高统一 → 各行都在同一条基线上
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                y += _rowHeights[row];
            }

            x += column.Width;
        }

        return finalSize;
    }
}
