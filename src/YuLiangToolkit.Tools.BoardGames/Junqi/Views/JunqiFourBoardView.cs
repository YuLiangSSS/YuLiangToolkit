using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Junqi.Views;

/// <summary>
/// <b>四国军棋</b>专用棋盘（13 × 13 十字形）：四个 6×6 阵营区 + 中央十字通道。
///
/// <para>为什么不复用二人军棋的 <see cref="JunqiBoardView"/>：那个是 12×5、
/// 只有红蓝两色、铁路按二人规则画；四国是 13×13、四色、中间是公共十字，
/// 硬套会把两边规则都搞乱。这里只画四国需要的：</para>
/// <list type="bullet">
///   <item>只画真实存在的格子（四个阵营区 + 中央那一行 / 列），不画整张方阵；</item>
///   <item>行营画圆、大本营画方框；</item>
///   <item>棋子按阵营着色（红 / 蓝 / 绿 / 黄），暗棋画成灰色背面；</item>
///   <item>铁路用粗线、公路用细线。</item>
/// </list>
/// </summary>
public sealed class JunqiFourBoardView : FrameworkElement
{
    /// <summary>格子快照。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(IReadOnlyList<JunqiCellItem>), typeof(JunqiFourBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>铁路线颜色。</summary>
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
        => DependencyProperty.Register(name, typeof(Brush), typeof(JunqiFourBoardView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>点了某一格。</summary>
    public event Action<JunqiCellItem>? CellClicked;

    /// <summary>构造。</summary>
    public JunqiFourBoardView()
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
        var width = double.IsInfinity(availableSize.Width) ? JunqiFourBoard.Size * 46 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? JunqiFourBoard.Size * 46 : availableSize.Height;
        return new Size(width, height);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        if (BoardBrush is { } board) dc.DrawRectangle(board, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        var cellWidth = cell * 0.82;

        // ① 连线（只连两个格子都存在、且相邻的方向）
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (!Exists(x, y)) continue;
                DrawLink(dc, origin, cell, x, y, x + 1, y);
                DrawLink(dc, origin, cell, x, y, x, y + 1);
            }
        }

        // ② 行营 / 大本营底纹
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (!Exists(x, y)) continue;
                var point = Center(origin, cell, x, y);

                if (JunqiFourBoard.IsCamp(x, y) && CampBrush is { } camp)
                {
                    var pen = new Pen(camp, 1);
                    pen.Freeze();
                    dc.DrawEllipse(camp, pen, point, cellWidth / 2, cellWidth / 2);
                }

                if (JunqiFourBoard.IsHeadquarters(x, y) && RoadBrush is { } hq)
                {
                    var rect = new Rect(point.X - cellWidth / 2, point.Y - cellWidth / 2, cellWidth, cellWidth);
                    var pen = new Pen(hq, 1.4);
                    pen.Freeze();
                    dc.DrawRoundedRectangle(null, pen, rect, 4, 4);
                }
            }
        }

        // ③ 棋子
        var state = State;
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (!Exists(x, y)) continue;
                var item = ItemAt(state, x, y);
                if (item is null || item.Piece.IsEmpty) continue;
                DrawPiece(dc, Center(origin, cell, x, y), cell, item);
            }
        }
    }

    /// <summary>这一格在四国棋盘上存在吗（四个阵营区内 + 中央十字）。</summary>
    private static bool Exists(int x, int y)
        => JunqiFourBoard.InBounds(x, y)
           && (JunqiFourBoard.NationAt(x, y) is not null || x == 6 || y == 6);

    private void DrawLink(DrawingContext dc, Point origin, double cell, int x1, int y1, int x2, int y2)
    {
        if (!Exists(x1, y1) || !Exists(x2, y2)) return;

        var isRail = JunqiFourBoard.IsRailway(x1, y1) && JunqiFourBoard.IsRailway(x2, y2);
        var brush = isRail ? RailBrush : RoadBrush;
        if (brush is null) return;

        var pen = new Pen(brush, isRail ? Math.Max(2, cell * 0.055) : Math.Max(1, cell * 0.018));
        pen.Freeze();
        dc.DrawLine(pen, Center(origin, cell, x1, y1), Center(origin, cell, x2, y2));
    }

    /// <summary>画棋子（按阵营四色；暗棋 = 灰色背面）。</summary>
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
            fill = item.SideColorKey switch
            {
                "blue" => new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xC0)),
                "green" => new SolidColorBrush(Color.FromRgb(0x2E, 0x8B, 0x57)),
                "yellow" => new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x1A)),
                _ => new SolidColorBrush(Color.FromRgb(0xC8, 0x3A, 0x3A)),
            };
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
            Math.Max(8, cell * 0.34),
            text,
            dpi);
        dc.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
    }

    private static JunqiCellItem? ItemAt(IReadOnlyList<JunqiCellItem>? state, int x, int y)
    {
        if (state is null) return null;
        var index = y * JunqiFourBoard.Size + x;
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

        var cell = Math.Min(
            available.Width / (JunqiFourBoard.Size - 1 + 1.0),
            available.Height / (JunqiFourBoard.Size - 1 + 1.0));
        if (cell <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        var boardWidth = cell * (JunqiFourBoard.Size - 1);
        var boardHeight = cell * (JunqiFourBoard.Size - 1);
        origin = new Point((ActualWidth - boardWidth) / 2, (ActualHeight - boardHeight) / 2);
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
        if (!Exists(x, y)) return;

        var center = Center(origin, cell, x, y);
        if ((center - point).Length > cell * 0.5) return;

        var item = ItemAt(State, x, y);
        if (item is not null) CellClicked?.Invoke(item);
    }
}
