using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.BoardGames.Checkers.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Checkers.Views;

/// <summary>
/// 六芒星跳棋棋盘（自绘）：121 个洞位 + 六角不同底色 + 棋子圆珠 + 目标角提示。
///
/// <para>用 <see cref="CheckersBoard"/> 的偏移坐标换算成屏幕坐标（六边形网格交错排列），
/// 一次 <see cref="DrawingContext"/> 画完，点哪儿算哪儿。</para>
/// </summary>
public sealed class CheckersBoardView : FrameworkElement
{
    /// <summary>棋子。</summary>
    public static readonly DependencyProperty PiecesProperty = DependencyProperty.Register(
        nameof(Pieces), typeof(IReadOnlyList<CheckersPieceItem>), typeof(CheckersBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>我被分到的角（高亮用）。</summary>
    public static readonly DependencyProperty MyHomeProperty = DependencyProperty.Register(
        nameof(MyHome), typeof(string), typeof(CheckersBoardView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>点某一格。</summary>
    public event Action<int, int>? CellClicked;

    /// <summary>构造。</summary>
    public CheckersBoardView()
    {
        Focusable = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
    }

    /// <summary>棋子。</summary>
    public IReadOnlyList<CheckersPieceItem>? Pieces
    {
        get => (IReadOnlyList<CheckersPieceItem>?)GetValue(PiecesProperty);
        set => SetValue(PiecesProperty, value);
    }

    /// <summary>我的角（键）。</summary>
    public string MyHome
    {
        get => (string)GetValue(MyHomeProperty);
        set => SetValue(MyHomeProperty, value);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsInfinity(availableSize.Width) ? 620 : availableSize.Width;
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

        // ① 洞位（六角不同底色）
        foreach (var item in CheckersBoard.All)
        {
            var center = Center(origin, cell, item.X, item.Y);
            var brush = item.IsCenter
                ? new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC))
                : PointBrush(item.Point);
            brush.Freeze();

            dc.DrawEllipse(brush, null, center, cell * 0.34, cell * 0.34);

            // 我的角高亮
            if (!item.IsCenter && item.Point is { } point
                && string.Equals(CheckersBoard.ToKey(point), MyHome, StringComparison.OrdinalIgnoreCase))
            {
                var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0x2F, 0x2F, 0x36)), Math.Max(1.5, cell * 0.06));
                pen.Freeze();
                dc.DrawEllipse(null, pen, center, cell * 0.40, cell * 0.40);
            }
        }

        // ② 棋子
        var pieces = Pieces;
        if (pieces is null) return;

        foreach (var piece in pieces)
        {
            var center = Center(origin, cell, piece.X, piece.Y);
            var brush = new SolidColorBrush(ParseColor(piece.ColorKey));
            brush.Freeze();

            var pen = piece.CanMove
                ? new Pen(Brushes.Gold, Math.Max(2, cell * 0.09))
                : new Pen(new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)), 1);
            pen.Freeze();

            dc.DrawEllipse(brush, pen, center, cell * 0.30, cell * 0.30);
        }
    }

    /// <summary>六角底色。</summary>
    private static SolidColorBrush PointBrush(CheckersPoint? point) => point switch
    {
        CheckersPoint.North => new SolidColorBrush(Color.FromArgb(0x88, 0xF2, 0xB1, 0x2B)),
        CheckersPoint.NorthEast => new SolidColorBrush(Color.FromArgb(0x88, 0x3A, 0x7B, 0xD5)),
        CheckersPoint.SouthEast => new SolidColorBrush(Color.FromArgb(0x88, 0x3F, 0xA8, 0x4F)),
        CheckersPoint.South => new SolidColorBrush(Color.FromArgb(0x88, 0xD6, 0x3B, 0x3B)),
        CheckersPoint.SouthWest => new SolidColorBrush(Color.FromArgb(0x88, 0x8E, 0x5A, 0xD6)),
        CheckersPoint.NorthWest => new SolidColorBrush(Color.FromArgb(0x88, 0x26, 0xB6, 0xC6)),
        _ => new SolidColorBrush(Color.FromRgb(0xE8, 0xE4, 0xDC)),
    };

    private static Color ParseColor(string key) => key switch
    {
        "b" => Color.FromRgb(0x3A, 0x7B, 0xD5),
        "c" => Color.FromRgb(0x26, 0xB6, 0xC6),
        "d" => Color.FromRgb(0x3F, 0xA8, 0x4F),
        "e" => Color.FromRgb(0xF2, 0xB1, 0x2B),
        "f" => Color.FromRgb(0x8E, 0x5A, 0xD6),
        _ => Color.FromRgb(0xD6, 0x3B, 0x3B),
    };

    /// <summary>格子中心（用轴向坐标换算成标准「尖顶六边形」像素位置，避免剪切变形）。</summary>
    private static Point Center(Point origin, double cell, int x, int y)
    {
        var (q, r) = CheckersBoard.OffsetToAxial(x, y);

        // 尖顶六边形的标准换算：x = √3·s·(q + r/2)，y = 1.5·s·r
        var s = cell / Math.Sqrt(3);
        return new Point(
            origin.X + Math.Sqrt(3) * s * (q + r / 2.0),
            origin.Y + 1.5 * s * r);
    }

    /// <summary>
    /// 算格子尺寸与原点：<b>先按 cell=1 算出整张棋盘的真实像素包围盒</b>，
    /// 再按可用空间等比缩放并居中 —— 这样不管坐标系怎么偏移都不会裁掉角。
    /// </summary>
    private double CellSize(out Point origin)
    {
        const double margin = 10;
        var available = Math.Max(0, Math.Min(ActualWidth, ActualHeight) - margin * 2);
        if (available <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        // ① 用单位 cell 算出包围盒
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var item in CheckersBoard.All)
        {
            var (q, r) = CheckersBoard.OffsetToAxial(item.X, item.Y);
            var s = 1 / Math.Sqrt(3);
            var px = Math.Sqrt(3) * s * (q + r / 2.0);
            var py = 1.5 * s * r;

            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        var spanX = Math.Max(0.001, maxX - minX);
        var spanY = Math.Max(0.001, maxY - minY);

        // ② 等比缩放到可用空间（乘 0.86 给圆圈留边距）
        var cell = Math.Min(available / spanX, available / spanY) * 0.86;
        if (cell <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        // ③ 居中：把包围盒的中心对到控件中心
        var drawWidth = spanX * cell;
        var drawHeight = spanY * cell;
        var left = (ActualWidth - drawWidth) / 2;
        var top = (ActualHeight - drawHeight) / 2;

        // Center() 用的是「未偏移」的坐标，这里把 minX/minY 补偿进去
        origin = new Point(left - minX * cell, top - minY * cell);
        return cell;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var cell = CellSize(out var origin);
        if (cell <= 0) return;

        var point = e.GetPosition(this);
        (int X, int Y)? best = null;
        var bestDistance = double.MaxValue;

        foreach (var item in CheckersBoard.All)
        {
            var center = Center(origin, cell, item.X, item.Y);
            var distance = (center - point).Length;
            if (distance < cell * 0.45 && distance < bestDistance)
            {
                bestDistance = distance;
                best = (item.X, item.Y);
            }
        }

        if (best is { } hit) CellClicked?.Invoke(hit.X, hit.Y);
    }
}
