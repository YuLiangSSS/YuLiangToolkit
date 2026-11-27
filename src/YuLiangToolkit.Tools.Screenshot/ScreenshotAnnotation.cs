using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>截图编辑工具。</summary>
public enum ScreenshotDrawTool
{
    /// <summary>选择 / 调整框选。</summary>
    Select,
    /// <summary>矩形。</summary>
    Rectangle,
    /// <summary>椭圆。</summary>
    Ellipse,
    /// <summary>箭头。</summary>
    Arrow,
    /// <summary>直线。</summary>
    Line,
    /// <summary>画笔。</summary>
    Pen,
    /// <summary>荧光笔。</summary>
    Highlighter,
    /// <summary>文字。</summary>
    Text,
    /// <summary>序号。</summary>
    Number,
    /// <summary>马赛克。</summary>
    Mosaic,
    /// <summary>高斯模糊。</summary>
    Blur,
    /// <summary>取色（点一下复制颜色，不参与撤销）。</summary>
    PickColor,
    /// <summary>橡皮擦（删除点到的标注）。</summary>
    Eraser,
}

/// <summary>
/// 一条截图标注（坐标全部是覆盖层 DIP 的绝对坐标；渲染时再换算到裁剪区域）。
/// 纯数据对象；预览和导出各自负责把它画出来。
/// </summary>
public sealed class ScreenshotAnnotation
{
    /// <summary>工具类型。</summary>
    public ScreenshotDrawTool Tool { get; init; }

    /// <summary>关键点：矩形 / 椭圆 / 箭头 / 直线 / 马赛克 / 模糊是两点；画笔 / 荧光笔是折线；文字 / 序号是一个锚点。</summary>
    public List<Point> Points { get; init; } = new();

    /// <summary>颜色。</summary>
    public Color Color { get; init; } = Colors.Red;

    /// <summary>线宽（DIP）。</summary>
    public double Thickness { get; init; } = 3;

    /// <summary>文字 / 序号内容。</summary>
    public string Text { get; set; } = "";

    /// <summary>文字字号（DIP）。</summary>
    public double FontSize { get; init; } = 22;

    /// <summary>矩形 / 椭圆是否填充半透明色（文字时表示背景底色）。</summary>
    public bool Filled { get; init; }

    /// <summary>序号值（从 1 开始）。</summary>
    public int Number { get; init; }

    /// <summary>包围盒（DIP；文字在测完后由调用方写回）。</summary>
    public Rect Bounds { get; set; }

    /// <summary>从两点生成矩形 / 椭圆 / 箭头 / 直线 / 马赛克 / 模糊。</summary>
    public static ScreenshotAnnotation FromTwoPoints(ScreenshotDrawTool tool, Point start, Point end,
        Color color, double thickness, bool filled = false)
        => new()
        {
            Tool = tool,
            Points = { start, end },
            Color = color,
            Thickness = Math.Clamp(thickness, 1, 24),
            Filled = filled,
            Bounds = RectFromPoints(start, end),
        };

    /// <summary>从一串点生成画笔 / 荧光笔。</summary>
    public static ScreenshotAnnotation FromPoints(ScreenshotDrawTool tool, IEnumerable<Point> points,
        Color color, double thickness)
    {
        var list = points.ToList();
        return new ScreenshotAnnotation
        {
            Tool = tool,
            Points = list,
            Color = color,
            Thickness = Math.Clamp(thickness, 1, 24),
            Bounds = GetBounds(list),
        };
    }

    /// <summary>文字标注。</summary>
    public static ScreenshotAnnotation TextAt(Point anchor, string text, Color color, double fontSize, bool filled)
        => new()
        {
            Tool = ScreenshotDrawTool.Text,
            Points = { anchor },
            Text = text,
            Color = color,
            FontSize = Math.Clamp(fontSize, 8, 96),
            Filled = filled,
            Bounds = new Rect(anchor, new Size(0, 0)),   // 由预览控件测量后写回
        };

    /// <summary>序号标注。</summary>
    public static ScreenshotAnnotation NumberAt(Point anchor, int number, Color color, double diameter = 26)
        => new()
        {
            Tool = ScreenshotDrawTool.Number,
            Points = { anchor },
            Number = number,
            Color = color,
            FontSize = Math.Clamp(diameter * 0.62, 10, 36),
            Bounds = new Rect(anchor, new Size(diameter, diameter)),
        };

    /// <summary>给文字测完大小后更新包围盒。</summary>
    public void SetTextBounds(Rect bounds) => Bounds = bounds;

    /// <summary>点是否命中这条标注（橡皮擦用；带一点容差，细线也容易点中）。</summary>
    public bool HitTest(Point point)
    {
        var tolerance = Math.Max(6, Thickness + 4);
        return Tool switch
        {
            ScreenshotDrawTool.Line or ScreenshotDrawTool.Arrow => Points.Count >= 2
                && DistanceToSegment(point, Points[0], Points[1]) <= tolerance,
            ScreenshotDrawTool.Pen or ScreenshotDrawTool.Highlighter => Points.Count >= 2
                && Enumerable.Range(1, Points.Count - 1)
                    .Any(i => DistanceToSegment(point, Points[i - 1], Points[i]) <= tolerance),
            _ => Inflate(Bounds, tolerance).Contains(point),
        };
    }

    /// <summary>把两点/多点的包围盒算出来。</summary>
    public static Rect GetBounds(IReadOnlyList<Point> points)
    {
        if (points.Count == 0) return Rect.Empty;
        var minX = points.Min(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxX = points.Max(p => p.X);
        var maxY = points.Max(p => p.Y);
        return new Rect(new Point(minX, minY), new Point(maxX, maxY));
    }

    private static Rect RectFromPoints(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private static Rect Inflate(Rect rect, double amount)
        => new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2, rect.Height + amount * 2);

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        if (Math.Abs(dx) < 0.0001 && Math.Abs(dy) < 0.0001) return (p - a).Length;

        var t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / (dx * dx + dy * dy);
        t = Math.Clamp(t, 0, 1);
        var closest = new Point(a.X + t * dx, a.Y + t * dy);
        return (p - closest).Length;
    }
}

/// <summary>把用户输入的颜色文本解析成 <see cref="Color"/>；坏值退回红色。</summary>
public static class ScreenshotColor
{
    /// <summary>解析 #RRGGBB / #AARRGGBB（也接受不带 #）。</summary>
    public static Color Parse(string? text, Color? fallback = null)
    {
        var value = (text ?? "").Trim().TrimStart('#');
        try
        {
            if (value.Length == 6)
                return Color.FromRgb(Convert.ToByte(value[..2], 16), Convert.ToByte(value[2..4], 16), Convert.ToByte(value[4..], 16));
            if (value.Length == 8)
                return Color.FromArgb(Convert.ToByte(value[..2], 16), Convert.ToByte(value[2..4], 16),
                    Convert.ToByte(value[4..6], 16), Convert.ToByte(value[6..], 16));
        }
        catch
        {
            // 走下面的兜底
        }
        return fallback ?? Color.FromRgb(0xE5, 0x39, 0x35);
    }

    /// <summary>输出 #AARRGGBB 文本。</summary>
    public static string ToHex(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>校验 #RRGGBB / #AARRGGBB（用于自定义调色板输入）。</summary>
    public static bool IsValid(string? text)
    {
        var value = (text ?? "").Trim().TrimStart('#');
        if (value.Length != 6 && value.Length != 8) return false;
        try
        {
            _ = Convert.ToByte(value[..2], 16);
            _ = Convert.ToByte(value[2..4], 16);
            _ = Convert.ToByte(value[4..6], 16);
            if (value.Length == 8) _ = Convert.ToByte(value[6..], 16);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>画笔 / 高亮笔用的调色板。</summary>
    public static IReadOnlyList<Color> Palette { get; } = new[]
    {
        Color.FromRgb(0xE5, 0x39, 0x35), // 红
        Color.FromRgb(0xFF, 0x8A, 0x00), // 橙
        Color.FromRgb(0xFF, 0xD6, 0x00), // 黄
        Color.FromRgb(0x43, 0xA0, 0x47), // 绿
        Color.FromRgb(0x1E, 0x88, 0xE5), // 蓝
        Color.FromRgb(0x8E, 0x24, 0xAA), // 紫
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 白
        Color.FromRgb(0x11, 0x11, 0x11), // 黑
    };

    /// <summary>完整调色板：固定色 + 额外 16 个常用色。</summary>
    public static IReadOnlyList<Color> ExtendedPalette { get; } = Palette.Concat(new[]
    {
        Color.FromRgb(0xFF, 0x6B, 0x6B), Color.FromRgb(0xFF, 0xB4, 0x8A),
        Color.FromRgb(0xFF, 0xE4, 0x8A), Color.FromRgb(0x9B, 0xE0, 0x9B),
        Color.FromRgb(0x7E, 0xD3, 0xD3), Color.FromRgb(0x8D, 0xC8, 0xFF),
        Color.FromRgb(0xB0, 0xA8, 0xFF), Color.FromRgb(0xE0, 0xA8, 0xFF),
        Color.FromRgb(0xFF, 0xA8, 0xD8), Color.FromRgb(0xD9, 0xA8, 0x7E),
        Color.FromRgb(0xC8, 0xC8, 0xC8), Color.FromRgb(0x66, 0x66, 0x66),
        Color.FromRgb(0x00, 0xC8, 0x53), Color.FromRgb(0x2E, 0x9B, 0xFF),
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0xCC, 0x00),
    }).ToList();
}
