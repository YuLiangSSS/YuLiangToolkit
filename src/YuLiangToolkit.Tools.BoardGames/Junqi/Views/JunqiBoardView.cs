using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Junqi.Views;

/// <summary>
/// 军棋棋盘的自绘控件：<b>公路 / 铁路连线、行营（圆）、大本营、棋子</b>全部用
/// <see cref="DrawingContext"/> 画（60 格，一格一个控件会重排很慢）。
///
/// <para><b>数据流</b>：VM 把 <see cref="Cells"/>（含每格的棋子与高亮状态）经由
/// <see cref="StateProperty"/> 绑进来；控件只画，不存业务状态。
/// 点击 → 抛 <see cref="CellClicked"/> 事件，由 View 转给 VM。</para>
/// </summary>
public sealed class JunqiBoardView : FrameworkElement
{
    /// <summary>棋盘的格子快照（每次变化由 VM 造一份新的）。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(IReadOnlyList<JunqiCellItem>), typeof(JunqiBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>铁路线颜色（主题 token）。</summary>
    public static readonly DependencyProperty RailBrushProperty = RegisterBrush(nameof(RailBrush));

    /// <summary>公路线颜色。</summary>
    public static readonly DependencyProperty RoadBrushProperty = RegisterBrush(nameof(RoadBrush));

    /// <summary>行营底色。</summary>
    public static readonly DependencyProperty CampBrushProperty = RegisterBrush(nameof(CampBrush));

    /// <summary>棋盘底色。</summary>
    public static readonly DependencyProperty BoardBrushProperty = RegisterBrush(nameof(BoardBrush));

    /// <summary>文字颜色。</summary>
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush));

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(JunqiBoardView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>点了某一格。</summary>
    public event Action<JunqiCellItem>? CellClicked;

    /// <summary>构造。</summary>
    public JunqiBoardView()
    {
        Focusable = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
    }

    /// <summary>格子快照。</summary>
    public IReadOnlyList<JunqiCellItem>? State
    {
        get => (IReadOnlyList<JunqiCellItem>?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>铁路线颜色。</summary>
    public Brush? RailBrush { get => (Brush?)GetValue(RailBrushProperty); set => SetValue(RailBrushProperty, value); }

    /// <summary>公路线颜色。</summary>
    public Brush? RoadBrush { get => (Brush?)GetValue(RoadBrushProperty); set => SetValue(RoadBrushProperty, value); }

    /// <summary>行营底色。</summary>
    public Brush? CampBrush { get => (Brush?)GetValue(CampBrushProperty); set => SetValue(CampBrushProperty, value); }

    /// <summary>棋盘底色。</summary>
    public Brush? BoardBrush { get => (Brush?)GetValue(BoardBrushProperty); set => SetValue(BoardBrushProperty, value); }

    /// <summary>文字颜色。</summary>
    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? JunqiBoard.Width * 60 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? JunqiBoard.Height * 60 : availableSize.Height;
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        if (BoardBrush is { } board) dc.DrawRectangle(board, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        var state = State;
        var cellWidth = cell * 0.82;

        // ---- 连线：先把所有公路 / 铁路画出来 ----
        for (var y = 0; y < JunqiBoard.Height; y++)
        {
            for (var x = 0; x < JunqiBoard.Width; x++)
            {
                DrawLink(dc, origin, cell, x, y, x + 1, y);
                DrawLink(dc, origin, cell, x, y, x, y + 1);
            }
        }

        // ---- 格子：先画底色标记（行营圆 / 大本营框），再画棋子 ----
        // 注意顺序：铁路线在上一段已经画完，这里把行营 / 大本营画在线**上面**，
        // 否则穿过行营的铁路线会盖住行营底纹（早期版本踩过）。
        for (var y = 0; y < JunqiBoard.Height; y++)
        {
            for (var x = 0; x < JunqiBoard.Width; x++)
            {
                var point = Center(origin, cell, x, y);
                var kind = JunqiBoard.KindAt(x, y);

                if (kind == JunqiCellKind.Camp && CampBrush is { } camp)
                {
                    var campPen = new Pen(CampBrush as Brush ?? Brushes.Gray, 1);
                    campPen.Freeze();
                    dc.DrawEllipse(CampBrush, campPen, point, cellWidth / 2, cellWidth / 2);
                }

                if (kind == JunqiCellKind.Headquarters && RoadBrush is { } hq)
                {
                    var rect = new Rect(point.X - cellWidth / 2, point.Y - cellWidth / 2, cellWidth, cellWidth);
                    var pen = new Pen(hq, 1.4);
                    pen.Freeze();
                    dc.DrawRoundedRectangle(null, pen, rect, 4, 4);
                }

                var item = ItemAt(State, x, y);
                if (item is null || item.Piece.IsEmpty) continue;
                DrawPiece(dc, point, cell, item);
            }
        }
    }

    /// <summary>画一条连线（铁路粗、公路细）。</summary>
    private void DrawLink(DrawingContext dc, Point origin, double cell, int x1, int y1, int x2, int y2)
    {
        if (!JunqiBoard.InBounds(x2, y2)) return;

        var isRail = JunqiBoard.IsRailway(x1, y1) && JunqiBoard.IsRailway(x2, y2);
        var brush = isRail ? RailBrush : RoadBrush;
        if (brush is null) return;

        var pen = new Pen(brush, isRail ? Math.Max(2, cell * 0.055) : Math.Max(1, cell * 0.02));
        pen.Freeze();
        dc.DrawLine(pen, Center(origin, cell, x1, y1), Center(origin, cell, x2, y2));
    }

    /// <summary>画一个棋子：圆底 + 单字（红方红字、蓝方蓝字；暗棋画成灰色背面）。</summary>
    private void DrawPiece(DrawingContext dc, Point center, double cell, JunqiCellItem item)
    {
        var radius = cell * 0.38;

        Brush fill;
        Brush text;
        if (item.IsHidden)
        {
            fill = new SolidColorBrush(Color.FromRgb(0x4A, 0x50, 0x5A));
            text = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xAE));
        }
        else
        {
            fill = item.IsRedSide
                ? new SolidColorBrush(Color.FromRgb(0xC8, 0x3A, 0x3A))
                : new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xC0));
            text = Brushes.White;
        }
        fill.Freeze();
        text.Freeze();

        var border = new Pen(item.IsSelected ? Brushes.Gold : Brushes.Black, item.IsSelected ? 2.5 : 1);
        if (!item.IsSelected) border.Brush.Freeze();
        dc.DrawEllipse(fill, border, center, radius, radius);

        if (item.IsTarget)
        {
            var targetPen = new Pen(Brushes.LimeGreen, 2);
            targetPen.Freeze();
            dc.DrawEllipse(null, targetPen, center, radius + 3, radius + 3);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(
            item.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"),
            Math.Max(9, cell * 0.34),
            text,
            dpi);
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
    }

    private static JunqiCellItem? ItemAt(IReadOnlyList<JunqiCellItem>? state, int x, int y)
    {
        if (state is null) return null;
        var index = JunqiBoard.IndexOf(x, y);
        return index >= 0 && index < state.Count ? state[index] : null;
    }

    private static Point Center(Point origin, double cell, int x, int y)
        => new(origin.X + x * cell, origin.Y + y * cell);

    private double CellSize(out Point origin)
    {
        const double margin = 10;
        var available = new Size(
            Math.Max(0, ActualWidth - margin * 2),
            Math.Max(0, ActualHeight - margin * 2));
        if (available.Width <= 0 || available.Height <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        // 横向 12 个坐标点 → 11 段；纵向 5 行 → 4 段。
        // 取两轴里更小的那个，保证棋盘完整可见；再按「格子 + 半个边距」算实际占用，避免贴边。
        var cell = Math.Min(
            available.Width / (JunqiBoard.Width - 1 + 1.0),
            available.Height / (JunqiBoard.Height - 1 + 1.0));
        if (cell <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        var boardWidth = cell * (JunqiBoard.Width - 1);
        var boardHeight = cell * (JunqiBoard.Height - 1);
        origin = new Point(
            (ActualWidth - boardWidth) / 2,
            (ActualHeight - boardHeight) / 2);
        return cell;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        var point = e.GetPosition(this);
        var x = (int)Math.Round((point.X - origin.X) / cell);
        var y = (int)Math.Round((point.Y - origin.Y) / cell);
        if (!JunqiBoard.InBounds(x, y)) return;

        // 离格子中心太远不算点击（避免点到空白也走子）
        var center = Center(origin, cell, x, y);
        if ((center - point).Length > cell * 0.5) return;

        var item = ItemAt(State, x, y);
        if (item is not null) CellClicked?.Invoke(item);
    }
}
