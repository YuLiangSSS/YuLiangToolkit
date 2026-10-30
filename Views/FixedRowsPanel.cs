using System.Windows;
using System.Windows.Controls;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 固定行数、横向溢出的「多行文字式」排列面板（主页「快速筛选」标签墙的 3 行 + 横向滚动用）。
///
/// <para><b>排法</b>：像一段文字换行 —— 从左到右一个挨一个紧密排，一行排到差不多满了再排下一行，
/// 一共固定 <see cref="Rows"/> 行；行与行之间<b>不按列对齐</b>（每行都从最左边重新开始，标签保持自己的
/// 自然宽度），整块比视口宽，放进 <c>HorizontalScrollBarVisibility="Auto"</c> 的 ScrollViewer 就能左右拖。</para>
///
/// <para><b>每行多宽</b>：先用一遍动态规划把标签切成 <see cref="Rows"/> 行，让每行宽度尽量接近
/// 「总宽 / 行数」（就像两端对齐的文字换行），所以三行看起来是均匀的几段，
/// 不会出现第一行塞满、最后一行只剩几个的情况。子元素的 Margin 参与测量
/// （DataTemplate 里的 <c>0,0,8,8</c> 就是标签间距）。</para>
///
/// <para>只用于「标签墙」这种数量多、高度固定的场景；不虚拟化（150+ 个 Button 一次测完没有压力）。</para>
/// </summary>
public sealed class FixedRowsPanel : Panel
{
    /// <summary>固定行数（默认 3，必须 ≥ 1；标签比行数还少时按标签数排行，不留空行）。改它会重新测量。</summary>
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

    /// <summary>每一行的布局信息（按行从上到下；每行包含自己的起点 / 个数 / 行宽 / 行高）。</summary>
    private readonly List<RowLayout> _rows = new();

    /// <summary>每个子元素测量出来的期望尺寸（含 Margin；每次测量重算，排列时直接用）。</summary>
    private double[] _itemWidths = Array.Empty<double>();
    private double[] _itemHeights = Array.Empty<double>();

    /// <summary>一行的布局信息：从第几个子元素开始、有几个、行宽多少、行高多少。</summary>
    private readonly record struct RowLayout(int Start, int Count, double Width, double Height);

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = InternalChildren;
        _rows.Clear();

        var count = children.Count;
        if (count == 0) return default;

        if (_itemWidths.Length != count)
        {
            _itemWidths = new double[count];
            _itemHeights = new double[count];
        }

        var totalWidth = 0.0;
        for (var i = 0; i < count; i++)
        {
            // 每个子元素按无限空间量一遍：这里要的是标签的自然尺寸（宽度随文字长短变），
            // 不是按可用宽度压缩 / 拉伸。
            var child = children[i];
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            var size = child.DesiredSize;   // 已经含 Margin
            _itemWidths[i] = size.Width;
            _itemHeights[i] = size.Height;
            totalWidth += size.Width;
        }

        var rowCount = Math.Min(Math.Max(1, Rows), count);   // 标签比行数还少时不用空行
        var targetRowWidth = totalWidth / rowCount;

        // 前缀和：prefix[i] = 前 i 个标签的总宽（用来 O(1) 算任意一段的宽度）
        var prefix = new double[count + 1];
        for (var i = 0; i < count; i++) prefix[i + 1] = prefix[i] + _itemWidths[i];

        // 动态规划：把前 i 个标签切成 row 行，代价 = 各行「行宽 − 目标行宽」的平方和；越小越均匀。
        // splitAt[row][i] 记录最后一行（第 row 行，1-based）从第几个标签开始，最后据此还原每行。
        var infinity = double.PositiveInfinity;
        var previous = new double[count + 1];
        var current = new double[count + 1];
        Array.Fill(previous, infinity);
        previous[0] = 0;

        var splitAt = new int[rowCount + 1][];
        for (var r = 0; r <= rowCount; r++) splitAt[r] = new int[count + 1];

        for (var row = 1; row <= rowCount; row++)
        {
            Array.Fill(current, infinity);
            for (var end = row; end <= count; end++)          // 前 end 个标签切成 row 行（每行至少 1 个）
            {
                for (var start = row - 1; start < end; start++)   // 最后一行 = 标签 start..end-1
                {
                    if (double.IsPositiveInfinity(previous[start])) continue;

                    var rowWidth = prefix[end] - prefix[start];
                    var diff = rowWidth - targetRowWidth;
                    var cost = previous[start] + diff * diff;
                    if (cost < current[end])
                    {
                        current[end] = cost;
                        splitAt[row][end] = start;
                    }
                }
            }

            (previous, current) = (current, previous);
        }

        // 从最后一行往前还原每行的起点，再算每行的宽 / 高
        var rowStarts = new int[rowCount + 1];
        var nextStart = count;
        for (var row = rowCount; row >= 1; row--)
        {
            rowStarts[row] = splitAt[row][nextStart];
            nextStart = rowStarts[row];
        }

        var maxRowWidth = 0.0;
        var totalHeight = 0.0;
        for (var row = 1; row <= rowCount; row++)
        {
            var start = rowStarts[row];
            var end = row == rowCount ? count : rowStarts[row + 1];

            var rowWidth = prefix[end] - prefix[start];
            var rowHeight = 0.0;
            for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, _itemHeights[i]);

            _rows.Add(new RowLayout(start, end - start, rowWidth, rowHeight));
            maxRowWidth = Math.Max(maxRowWidth, rowWidth);
            totalHeight += rowHeight;
        }

        return new Size(maxRowWidth, totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var children = InternalChildren;
        var y = 0.0;

        foreach (var row in _rows)
        {
            var x = 0.0;
            for (var i = 0; i < row.Count; i++)
            {
                var child = children[row.Start + i];
                var size = child.DesiredSize;
                // 每行从最左边开始挨着排：标签保持自然宽度，行与行之间不做列对齐
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width;
            }

            y += row.Height;
        }

        return finalSize;
    }
}
