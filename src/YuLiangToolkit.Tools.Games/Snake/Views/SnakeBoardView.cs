using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.Games.Snake;

namespace YuLiangToolkit.Tools.Games.Snake.Views;

/// <summary>
/// 贪吃蛇棋盘的<b>自绘控件</b>：网格、果子、死亡残影、出生点爆炸区、
/// 每条蛇（含<b>虚化</b>半透明效果）、头部朝向、倒计时遮罩。
///
/// <para>一屏可能上百格 × 多条蛇，用控件画会很慢；这里一个 <see cref="DrawingContext"/> 画完。</para>
/// </summary>
public sealed class SnakeBoardView : FrameworkElement
{
    /// <summary>一帧数据。</summary>
    public static readonly DependencyProperty FrameProperty = DependencyProperty.Register(
        nameof(Frame), typeof(SnakeRenderFrame), typeof(SnakeBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>我的玩家 id（用来高亮自己的蛇）。</summary>
    public static readonly DependencyProperty MyIdProperty = DependencyProperty.Register(
        nameof(MyId), typeof(string), typeof(SnakeBoardView),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>棋盘底色。</summary>
    public static readonly DependencyProperty BoardBrushProperty = RegisterBrush(nameof(BoardBrush));

    /// <summary>网格线颜色。</summary>
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush));

    /// <summary>一帧数据。</summary>
    public SnakeRenderFrame? Frame
    {
        get => (SnakeRenderFrame?)GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>我的 id。</summary>
    public string MyId
    {
        get => (string)GetValue(MyIdProperty);
        set => SetValue(MyIdProperty, value);
    }

    /// <summary>棋盘底色。</summary>
    public Brush? BoardBrush { get => (Brush?)GetValue(BoardBrushProperty); set => SetValue(BoardBrushProperty, value); }

    /// <summary>网格线颜色。</summary>
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(SnakeBoardView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>构造。</summary>
    public SnakeBoardView()
    {
        Focusable = false;
        ClipToBounds = true;
        UseLayoutRounding = true;
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var frame = Frame;
        var width = frame?.Width ?? 40;
        var height = frame?.Height ?? 30;
        var scale = 16.0;

        var w = double.IsInfinity(availableSize.Width) ? width * scale : availableSize.Width;
        var h = double.IsInfinity(availableSize.Height) ? height * scale : availableSize.Height;
        return new Size(w, h);
    }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        if (BoardBrush is { } board) dc.DrawRectangle(board, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var frame = Frame;
        if (frame is null || frame.Width <= 0 || frame.Height <= 0) return;

        var cell = CellSize(frame, out var origin);
        if (cell <= 0) return;

        // ① 网格（格子多时淡一点，免得花）
        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, Math.Max(0.4, cell * 0.03));
            pen.Freeze();
            for (var x = 0; x <= frame.Width; x++)
            {
                var px = origin.X + x * cell;
                dc.DrawLine(pen, new Point(px, origin.Y), new Point(px, origin.Y + frame.Height * cell));
            }
            for (var y = 0; y <= frame.Height; y++)
            {
                var py = origin.Y + y * cell;
                dc.DrawLine(pen, new Point(origin.X, py), new Point(origin.X + frame.Width * cell, py));
            }
        }

        // ② 出生点爆炸区（淡红）
        var blastBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xD0, 0x3A, 0x3A));
        blastBrush.Freeze();
        foreach (var point in frame.BlastZones)
        {
            if (point.Length < 2) continue;
            dc.DrawRectangle(blastBrush, null, CellRect(origin, cell, point[0], point[1]));
        }

        // ②½ 出生光爆（亮一下，然后消失；不是障碍物）
        var burstBrush = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xE0, 0x66));
        burstBrush.Freeze();
        foreach (var point in frame.Bursts)
        {
            if (point.Length < 2) continue;
            var center = CellCenter(origin, cell, point[0], point[1]);
            dc.DrawEllipse(burstBrush, null, center, cell * 0.45, cell * 0.45);
        }

        // ③ 死亡残影（80% 透明）
        var markBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88));
        markBrush.Freeze();
        foreach (var point in frame.DeathMarks)
        {
            if (point.Length < 2) continue;
            var rect = CellRect(origin, cell, point[0], point[1]);
            rect.Inflate(-cell * 0.18, -cell * 0.18);
            dc.DrawRoundedRectangle(markBrush, null, rect, cell * 0.2, cell * 0.2);
        }

        // ④ 果子
        var fruitBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x3A, 0x3A));
        fruitBrush.Freeze();
        var fruitPen = new Pen(new SolidColorBrush(Color.FromRgb(0x8C, 0x1A, 0x1A)), Math.Max(0.6, cell * 0.04));
        fruitPen.Freeze();
        foreach (var point in frame.Fruits)
        {
            if (point.Length < 2) continue;
            var center = CellCenter(origin, cell, point[0], point[1]);
            dc.DrawEllipse(fruitBrush, fruitPen, center, cell * 0.34, cell * 0.34);
        }

        // ⑤ 蛇
        for (var i = 0; i < frame.Snakes.Count; i++)
        {
            var snake = frame.Snakes[i];
            if (!snake.IsAlive && snake.Body.Count == 0) continue;

            var color = SnakeColor(i);
            var isMe = snake.Id == MyId;

            // 虚化：半透明（重生保护）
            var opacity = snake.IsGhost ? 0.4 : 1.0;

            for (var segment = 0; segment < snake.Body.Count; segment++)
            {
                var point = snake.Body[segment];
                if (point.Length < 2) continue;

                var brush = new SolidColorBrush(color) { Opacity = opacity };
                brush.Freeze();

                var rect = CellRect(origin, cell, point[0], point[1]);
                rect.Inflate(-cell * 0.1, -cell * 0.1);

                // 死了的蛇画成灰
                if (!snake.IsAlive)
                {
                    brush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));
                    brush.Freeze();
                }

                var pen = isMe && segment == 0 ? new Pen(Brushes.White, Math.Max(1.2, cell * 0.08)) : null;
                pen?.Freeze();

                dc.DrawRoundedRectangle(brush, pen, rect, cell * 0.22, cell * 0.22);

                // 蛇头眼睛
                if (segment == 0 && snake.IsAlive)
                {
                    var center = CellCenter(origin, cell, point[0], point[1]);
                    var eyeBrush = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10));
                    eyeBrush.Freeze();
                    dc.DrawEllipse(eyeBrush, null, center, cell * 0.13, cell * 0.13);
                }
            }

            // 虚化提示：头上画个虚线圈
            if (snake.IsGhost && snake.Body.Count > 0 && snake.Body[0].Length >= 2)
            {
                var head = CellCenter(origin, cell, snake.Body[0][0], snake.Body[0][1]);
                var pen = new Pen(Brushes.White, Math.Max(1, cell * 0.05)) { DashStyle = DashStyles.Dash };
                pen.Freeze();
                dc.DrawEllipse(null, pen, head, cell * 0.45, cell * 0.45);
            }
        }

        // ⑥ 倒计时遮罩
        if (frame.Countdown > 0)
        {
            var overlay = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
            overlay.Freeze();
            dc.DrawRectangle(overlay, null, new Rect(0, 0, ActualWidth, ActualHeight));

            var text = new FormattedText(
                frame.Countdown.ToString(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface("Microsoft YaHei UI"),
                Math.Min(ActualWidth, ActualHeight) * 0.35,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(
                (ActualWidth - text.Width) / 2,
                (ActualHeight - text.Height) / 2));
        }
    }

    /// <summary>按序号分配蛇的颜色。</summary>
    private static Color SnakeColor(int index) => index switch
    {
        0 => Color.FromRgb(0x4C, 0xAF, 0x50),   // 绿
        1 => Color.FromRgb(0x42, 0x9F, 0xF0),   // 蓝
        2 => Color.FromRgb(0xFF, 0xA7, 0x26),   // 橙
        3 => Color.FromRgb(0xAB, 0x47, 0xBC),   // 紫
        4 => Color.FromRgb(0x26, 0xC6, 0xDA),   // 青
        5 => Color.FromRgb(0xEC, 0x40, 0x7A),   // 粉
        6 => Color.FromRgb(0x8D, 0x6E, 0x63),   // 棕
        _ => Color.FromRgb(0x9E, 0x9E, 0x9E),   // 灰
    };

    private Rect CellRect(Point origin, double cell, int x, int y)
        => new(origin.X + x * cell, origin.Y + y * cell, cell, cell);

    private Point CellCenter(Point origin, double cell, int x, int y)
        => new(origin.X + (x + 0.5) * cell, origin.Y + (y + 0.5) * cell);

    private double CellSize(SnakeRenderFrame frame, out Point origin)
    {
        const double margin = 6;
        var available = new Size(
            Math.Max(0, ActualWidth - margin * 2),
            Math.Max(0, ActualHeight - margin * 2));
        if (available.Width <= 0 || available.Height <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        var cell = Math.Min(available.Width / frame.Width, available.Height / frame.Height);
        if (cell <= 0)
        {
            origin = new Point(0, 0);
            return 0;
        }

        origin = new Point(
            (ActualWidth - cell * frame.Width) / 2,
            (ActualHeight - cell * frame.Height) / 2);
        return cell;
    }
}
