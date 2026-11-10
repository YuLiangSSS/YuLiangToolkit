using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.BoardGames.Go;
using YuLiangToolkit.Tools.BoardGames.Go.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Go.Views;

/// <summary>
/// 围棋棋盘（自绘）：<b>木纹底 + 网格 + 星位 + 黑白棋子 + 最后一手标记</b>。
///
/// <para>19 路有 361 个交叉点，用控件画会有 361 个元素、重排很慢；
/// 这里一个 <see cref="DrawingContext"/> 画完，点哪儿算哪儿。</para>
/// </summary>
public sealed class GoBoardView : FrameworkElement
{
    /// <summary>棋盘格子快照。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(IReadOnlyList<GoCellItem>), typeof(GoBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>棋盘路数。</summary>
    public static readonly DependencyProperty BoardSizeProperty = DependencyProperty.Register(
        nameof(BoardSize), typeof(int), typeof(GoBoardView),
        new FrameworkPropertyMetadata(19, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>木纹底色。</summary>
    public static readonly DependencyProperty BoardBrushProperty = RegisterBrush(nameof(BoardBrush));

    /// <summary>网格线颜色。</summary>
    public static readonly DependencyProperty LineBrushProperty = RegisterBrush(nameof(LineBrush));

    /// <summary>点了某个交叉点。</summary>
    public event Action<GoCellItem>? PointClicked;

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(GoBoardView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>构造。</summary>
    public GoBoardView()
    {
        Focusable = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
    }

    /// <summary>格子快照。</summary>
    public IReadOnlyList<GoCellItem>? State
    {
        get => (IReadOnlyList<GoCellItem>?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>棋盘路数。</summary>
    public int BoardSize
    {
        get => (int)GetValue(BoardSizeProperty);
        set => SetValue(BoardSizeProperty, value);
    }

    /// <summary>木纹底色。</summary>
    public Brush? BoardBrush { get => (Brush?)GetValue(BoardBrushProperty); set => SetValue(BoardBrushProperty, value); }

    /// <summary>网格线颜色。</summary>
    public Brush? LineBrush { get => (Brush?)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = BoardSize <= 0 ? 19 : BoardSize;
        var width = double.IsInfinity(availableSize.Width) ? size * 30 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? size * 30 : availableSize.Height;
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        if (BoardBrush is { } board) dc.DrawRectangle(board, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var count = BoardSize <= 0 ? 19 : BoardSize;
        var cell = CellSize(count, out var origin);
        if (cell <= 0) return;

        // ① 网格
        if (LineBrush is { } line)
        {
            var thin = new Pen(line, Math.Max(0.6, cell * 0.02));
            thin.Freeze();
            var thick = new Pen(line, Math.Max(1.0, cell * 0.04));
            thick.Freeze();

            for (var i = 0; i < count; i++)
            {
                var isEdge = i == 0 || i == count - 1;
                var pen = isEdge ? thick : thin;
                var offset = i * cell;
                dc.DrawLine(pen, new Point(origin.X, origin.Y + offset),
                    new Point(origin.X + cell * (count - 1), origin.Y + offset));
                dc.DrawLine(pen, new Point(origin.X + offset, origin.Y),
                    new Point(origin.X + offset, origin.Y + cell * (count - 1)));
            }

            // ② 星位
            var stars = StarPoints(count);
            var starRadius = Math.Max(1.5, cell * 0.055);
            foreach (var (sx, sy) in stars)
            {
                dc.DrawEllipse(line, null,
                    new Point(origin.X + sx * cell, origin.Y + sy * cell), starRadius, starRadius);
            }
        }

        // ③ 棋子
        var state = State;
        if (state is null) return;

        var radius = cell * 0.46;
        var black = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
        var white = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF0));
        black.Freeze();
        white.Freeze();
        var stoneEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), Math.Max(0.7, cell * 0.02));
        stoneEdge.Freeze();

        foreach (var item in state)
        {
            if (item.IsEmpty) continue;
            var center = new Point(origin.X + item.X * cell, origin.Y + item.Y * cell);
            dc.DrawEllipse(item.IsBlack ? black : white, stoneEdge, center, radius, radius);

            // 最后一手：中间画个小点
            if (item.IsLast)
            {
                var markRadius = Math.Max(1.6, cell * 0.1);
                dc.DrawEllipse(item.IsBlack ? Brushes.White : Brushes.Black, null,
                    center, markRadius, markRadius);
            }
        }
    }

    /// <summary>星位（9 路 5 个、13 路 5 个、19 路 9 个）。</summary>
    private static IReadOnlyList<(int X, int Y)> StarPoints(int count)
    {
        if (count == 19)
            return new[] { (3, 3), (9, 3), (15, 3), (3, 9), (9, 9), (15, 9), (3, 15), (9, 15), (15, 15) };
        if (count == 13)
            return new[] { (3, 3), (9, 3), (6, 6), (3, 9), (9, 9) };
        if (count == 9)
            return new[] { (2, 2), (6, 2), (4, 4), (2, 6), (6, 6) };
        return Array.Empty<(int X, int Y)>();
    }

    private double CellSize(int count, out Point origin)
    {
        const double margin = 18;
        var available = new Size(
            Math.Max(0, ActualWidth - margin * 2),
            Math.Max(0, ActualHeight - margin * 2));
        if (available.Width <= 0 || available.Height <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        var cell = Math.Min(available.Width / (count - 1 + 1.0), available.Height / (count - 1 + 1.0));
        if (cell <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        origin = new Point(
            (ActualWidth - cell * (count - 1)) / 2,
            (ActualHeight - cell * (count - 1)) / 2);
        return cell;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var count = BoardSize <= 0 ? 19 : BoardSize;
        var cell = CellSize(count, out var origin);
        if (cell <= 0) return;

        var point = e.GetPosition(this);
        var x = (int)Math.Round((point.X - origin.X) / cell);
        var y = (int)Math.Round((point.Y - origin.Y) / cell);
        if (x < 0 || y < 0 || x >= count || y >= count) return;

        // 离交叉点太远不算点击
        var center = new Point(origin.X + x * cell, origin.Y + y * cell);
        if ((center - point).Length > cell * 0.55) return;

        var state = State;
        var index = y * count + x;
        if (state is null || index >= state.Count) return;
        PointClicked?.Invoke(state[index]);
    }
}
