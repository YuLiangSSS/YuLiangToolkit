using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.ScreenPen;

/// <summary>屏幕画笔工具类型。</summary>
public enum ScreenPenToolKind
{
    Pen,
    Highlighter,
    Eraser,
    Line,
    Rectangle,
    Ellipse,
    Arrow,
    Text,
}

/// <summary>颜色解析 / 序列化小工具。</summary>
public static class ScreenPenColor
{
    public static bool IsValid(string? text) => TryParse(text, out _);

    public static Color Parse(string? text, Color? fallback = null)
        => TryParse(text, out var color) ? color : fallback ?? Colors.Red;

    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Red;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();
        if (!value.StartsWith('#')) value = "#" + value;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(value)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToHex(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>一条屏幕批注 / 画笔元素。坐标是屏幕 DIP（相对覆盖层）。</summary>
public sealed class ScreenPenAnnotation
{
    public ScreenPenToolKind Tool { get; init; }
    public List<Point> Points { get; init; } = new();
    public Color Color { get; init; } = Colors.Red;
    public double Thickness { get; init; } = 4;
    public string Text { get; init; } = "";
    public bool Filled { get; init; }

    public bool IsHighlighter => Tool == ScreenPenToolKind.Highlighter;

    public void Draw(DrawingContext dc)
    {
        if (Points.Count == 0) return;

        var color = IsHighlighter
            ? Color.FromArgb(70, Color.R, Color.G, Color.B)
            : Color;

        if (Tool == ScreenPenToolKind.Text)
        {
            var dpi = VisualTreeHelper.GetDpi(new DrawingVisual());
            var formatted = new FormattedText(
                Text ?? "",
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                Math.Max(12, Thickness * 4),
                new SolidColorBrush(color),
                dpi.PixelsPerDip);
            dc.DrawText(formatted, Points[0]);
            return;
        }

        var pen = new Pen(new SolidColorBrush(color), Math.Max(1, Thickness))
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };

        switch (Tool)
        {
            case ScreenPenToolKind.Pen:
            case ScreenPenToolKind.Highlighter:
            case ScreenPenToolKind.Eraser:
                DrawPolyline(dc, pen, Points);
                break;

            case ScreenPenToolKind.Line:
                if (Points.Count >= 2) dc.DrawLine(pen, Points[0], Points[^1]);
                break;

            case ScreenPenToolKind.Arrow:
                if (Points.Count >= 2) DrawArrow(dc, pen, Points[0], Points[^1]);
                break;

            case ScreenPenToolKind.Rectangle:
                if (Points.Count >= 2)
                {
                    var rect = RectFrom(Points[0], Points[^1]);
                    dc.DrawRectangle(Filled ? new SolidColorBrush(color) : null, pen, rect);
                }
                break;

            case ScreenPenToolKind.Ellipse:
                if (Points.Count >= 2)
                {
                    var rect = RectFrom(Points[0], Points[^1]);
                    dc.DrawEllipse(Filled ? new SolidColorBrush(color) : null, pen,
                        new Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2),
                        rect.Width / 2, rect.Height / 2);
                }
                break;
        }
    }

    private static Rect RectFrom(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static void DrawPolyline(DrawingContext dc, Pen pen, IReadOnlyList<Point> points)
    {
        if (points.Count == 1)
        {
            dc.DrawEllipse(new SolidColorBrush(pen.Brush is SolidColorBrush b ? b.Color : Colors.Transparent),
                null, points[0], pen.Thickness / 2, pen.Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], false, false);
            for (var i = 1; i < points.Count; i++) ctx.LineTo(points[i], true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Point start, Point end)
    {
        dc.DrawLine(pen, start, end);

        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1) return;

        var ux = dx / length;
        var uy = dy / length;
        var head = Math.Max(10, pen.Thickness * 3.2);
        var left = new Point(end.X - ux * head - uy * head * 0.55, end.Y - uy * head + ux * head * 0.55);
        var right = new Point(end.X - ux * head + uy * head * 0.55, end.Y - uy * head - ux * head * 0.55);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(left, true, false);
            ctx.LineTo(right, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(pen.Brush, null, geometry);
    }
}
