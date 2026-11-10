using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.BoardGames.Ludo.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Ludo.Views;

/// <summary>
/// 飞行棋棋盘（15×15 自绘）：外圈 52 格、四色起飞区、回家通道、中央终点、
/// <b>中央横向飞行线</b>高亮、棋子圆点。
/// </summary>
public sealed class LudoBoardView : FrameworkElement
{
    /// <summary>棋子快照。</summary>
    public static readonly DependencyProperty PiecesProperty = DependencyProperty.Register(
        nameof(Pieces), typeof(IReadOnlyList<LudoPieceItem>), typeof(LudoBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>点了某枚可走的棋子。</summary>
    public event Action<LudoPieceItem>? PieceClicked;

    /// <summary>构造。</summary>
    public LudoBoardView()
    {
        Focusable = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
    }

    /// <summary>棋子。</summary>
    public IReadOnlyList<LudoPieceItem>? Pieces
    {
        get => (IReadOnlyList<LudoPieceItem>?)GetValue(PiecesProperty);
        set => SetValue(PiecesProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width;
        return new Size(side, side);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        var paper = new SolidColorBrush(Color.FromRgb(0xFA, 0xF7, 0xF0));
        paper.Freeze();
        dc.DrawRectangle(paper, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        // ① 外圈格子
        var trackBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        trackBrush.Freeze();
        var trackPen = new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC2, 0xB4)), 1);
        trackPen.Freeze();

        foreach (var (x, y) in LudoBoard.Track)
        {
            dc.DrawRectangle(trackBrush, trackPen, Rect(origin, cell, x, y));

            // 飞行线高亮（中央横向那几格）
            if (LudoBoard.IsFlightLine(x, y))
            {
                var flight = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xD5, 0x4F));
                flight.Freeze();
                dc.DrawRectangle(flight, null, Rect(origin, cell, x, y));
            }
        }

        // ② 四色大本营
        foreach (var color in LudoColors.All)
        {
            var (bx, by) = HomeBlock(color);
            dc.DrawRectangle(ColorBrush(color, 0x44), null,
                new Rect(origin.X + bx * cell, origin.Y + by * cell, cell * 4, cell * 4));

            foreach (var (x, y) in LudoColors.HomeCells(color))
            {
                dc.DrawEllipse(ColorBrush(color, 0xAA), null, Center(origin, cell, x, y),
                    cell * 0.36, cell * 0.36);
            }
        }

        // ③ 回家通道
        foreach (var color in LudoColors.All)
        {
            var brush = ColorBrush(color, 0xAA);
            foreach (var (x, y) in LudoBoard.HomeStretchCells(color))
            {
                dc.DrawRectangle(brush, null, Rect(origin, cell, x, y));
            }
        }

        // ④ 中央终点
        var finishBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x2F, 0x36));
        finishBrush.Freeze();
        dc.DrawRectangle(finishBrush, null, Rect(origin, cell, 7, 7));

        // ⑤ 棋子
        var pieces = Pieces;
        if (pieces is null) return;

        var occupied = new Dictionary<(int X, int Y), int>();
        foreach (var piece in pieces)
        {
            var key = (piece.X, piece.Y);
            occupied.TryGetValue(key, out var count);
            occupied[key] = count + 1;

            var center = Center(origin, cell, piece.X, piece.Y);
            var offset = cell * 0.14 * count;
            center = new Point(center.X + offset, center.Y - offset);

            var brush = new SolidColorBrush(ParseColor(piece.ColorKey));
            brush.Freeze();

            var pen = piece.CanMove
                ? new Pen(Brushes.Gold, Math.Max(2, cell * 0.09))
                : new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 1);
            pen.Freeze();

            dc.DrawEllipse(brush, pen, center, cell * 0.32, cell * 0.32);

            if (piece.IsFinished)
            {
                var whitePen = new Pen(Brushes.White, Math.Max(1, cell * 0.05));
                whitePen.Freeze();
                dc.DrawEllipse(null, whitePen, center, cell * 0.18, cell * 0.18);
            }
        }
    }

    /// <summary>颜色块左上角（4×4 区）。</summary>
    private static (int X, int Y) HomeBlock(LudoColor color) => color switch
    {
        LudoColor.Red => (1, 10),
        LudoColor.Yellow => (1, 1),
        LudoColor.Blue => (10, 1),
        _ => (10, 10),
    };

    private static Color ParseColor(string key) => key switch
    {
        "yellow" => Color.FromRgb(0xF2, 0xB1, 0x2B),
        "blue" => Color.FromRgb(0x3A, 0x7B, 0xD5),
        "green" => Color.FromRgb(0x3F, 0xA8, 0x4F),
        _ => Color.FromRgb(0xD6, 0x3B, 0x3B),
    };

    private static SolidColorBrush ColorBrush(LudoColor color, byte alpha)
    {
        var baseColor = ParseColor(LudoColors.ToKey(color));
        var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
        brush.Freeze();
        return brush;
    }

    private static Rect Rect(Point origin, double cell, int x, int y)
        => new(origin.X + x * cell, origin.Y + y * cell, cell, cell);

    private static Point Center(Point origin, double cell, int x, int y)
        => new(origin.X + (x + 0.5) * cell, origin.Y + (y + 0.5) * cell);

    private double CellSize(out Point origin)
    {
        const double margin = 8;
        var available = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - margin * 2);
        if (available <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        var cell = available / LudoBoard.Size;
        origin = new Point(
            (ActualWidth - cell * LudoBoard.Size) / 2,
            (ActualHeight - cell * LudoBoard.Size) / 2);
        return cell;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        var point = e.GetPosition(this);
        var x = (int)Math.Floor((point.X - origin.X) / cell);
        var y = (int)Math.Floor((point.Y - origin.Y) / cell);

        var pieces = Pieces;
        if (pieces is null) return;

        var hit = pieces.FirstOrDefault(p => p.X == x && p.Y == y && p.CanMove);
        if (hit is not null) PieceClicked?.Invoke(hit);
    }
}
