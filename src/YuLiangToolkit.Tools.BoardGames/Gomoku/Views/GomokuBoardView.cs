using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Tools.BoardGames.Gomoku;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku.Views;

/// <summary>
/// 棋盘控件的「不可变渲染状态」：ViewModel 每次棋局变化就造一份新的塞给
/// <see cref="GomokuBoardView.State"/>，控件只管照着画。<b>界面线程只读它，不做任何棋局计算。</b>
///
/// <para>用坐标列表（而不是 <c>GomokuBoard</c> 对象）是为了让渲染和棋局对象解耦：
/// 联机同步、悔棋、演示数据都可以随便换棋盘对象，不会让已经排队的重绘画到半新半旧的盘面。</para>
/// </summary>
public sealed class GomokuBoardViewState
{
    /// <summary>棋盘路数（15 / 19）。</summary>
    public int Size { get; init; } = GomokuBoard.DefaultSize;

    /// <summary>黑子坐标。</summary>
    public IReadOnlyList<GomokuPoint> Black { get; init; } = Array.Empty<GomokuPoint>();

    /// <summary>白子坐标。</summary>
    public IReadOnlyList<GomokuPoint> White { get; init; } = Array.Empty<GomokuPoint>();

    /// <summary>最后一手（画红点标记）。</summary>
    public GomokuPoint? LastMove { get; init; }

    /// <summary>最近一次「禁手判负」的落子点（画红色叉，压在棋子上）。</summary>
    public GomokuPoint? ForbiddenAttempt { get; init; }

    /// <summary>当前所有禁手点（黑方回合的空点；设置里「显示禁手点」关掉时为空）。</summary>
    public IReadOnlyList<GomokuPoint> ForbiddenPoints { get; init; } = Array.Empty<GomokuPoint>();

    /// <summary>获胜连线（画金色描边）。</summary>
    public IReadOnlyList<GomokuPoint> WinningLine { get; init; } = Array.Empty<GomokuPoint>();

    /// <summary>轮到谁走（鼠标悬停时按这个颜色画半透明预览子）。</summary>
    public GomokuCell Turn { get; init; } = GomokuCell.Black;

    /// <summary>这一局开没开禁手（关掉时禁手点不画）。</summary>
    public bool ShowForbidden { get; init; }
}

/// <summary>
/// 五子棋棋盘自绘控件：<b>木纹底 + 网格 + 星位 + 边缘坐标 + 黑白棋子 + 最后一手红点 +
/// 禁手点红叉 + 获胜五连金线</b>，全部用 <see cref="DrawingContext"/> 画（不 new 任何子控件）。
///
/// <para>用法：在 View 里给固定尺寸（外面套 <c>Viewbox</c> 自适应缩放），
/// <c>State</c> 绑 ViewModel 的渲染状态，<c>Interactive</c> 控制能不能点，
/// 点交叉点会抛 <see cref="PointClicked"/>（参数是离点击位置最近的交叉点）。</para>
/// </summary>
public sealed class GomokuBoardView : FrameworkElement
{
    // ---------------------------------------------------------------- 依赖属性

    /// <summary>渲染状态（<see cref="GomokuBoardViewState"/>，变化后自动重绘）。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(GomokuBoardViewState), typeof(GomokuBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnStateChanged));

    /// <summary>要不要画边缘的坐标（A~S / 1~19）。</summary>
    public static readonly DependencyProperty ShowCoordinatesProperty = DependencyProperty.Register(
        nameof(ShowCoordinates), typeof(bool), typeof(GomokuBoardView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>能不能点（false 时点击不抛事件，鼠标指针变回普通箭头）。</summary>
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(GomokuBoardView), new PropertyMetadata(false));

    /// <summary>用户点了一个交叉点（参数已换算成棋盘坐标）。</summary>
    public event Action<GomokuPoint>? PointClicked;

    private GomokuPoint? _hover;

    /// <summary>换了一份渲染状态：清掉鼠标悬停预览（旧位置的预览可能已经压在棋子上了）。</summary>
    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GomokuBoardView view) view._hover = null;
    }

    /// <summary>构造：让控件自己接收鼠标事件。</summary>
    public GomokuBoardView()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    /// <summary>渲染状态。</summary>
    public GomokuBoardViewState? State
    {
        get => (GomokuBoardViewState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>要不要画坐标。</summary>
    public bool ShowCoordinates
    {
        get => (bool)GetValue(ShowCoordinatesProperty);
        set => SetValue(ShowCoordinatesProperty, value);
    }

    /// <summary>能不能点。</summary>
    public bool Interactive
    {
        get => (bool)GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    // ---------------------------------------------------------------- 鼠标

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = Interactive ? Cursors.Hand : Cursors.Arrow;

        if (!Interactive)
        {
            SetHover(null);
            return;
        }

        var point = HitPoint(e.GetPosition(this));
        var state = State;
        SetHover(point is { } p && state is not null && IsEmptyPoint(state, p) ? p : null);
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Cursor = Cursors.Arrow;
        SetHover(null);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Interactive) return;

        var point = HitPoint(e.GetPosition(this));
        if (point is not { } p) return;

        e.Handled = true;
        PointClicked?.Invoke(p);
    }

    private void SetHover(GomokuPoint? point)
    {
        if (_hover == point) return;
        _hover = point;
        InvalidateVisual();
    }

    private static bool IsEmptyPoint(GomokuBoardViewState state, GomokuPoint point)
        => !state.Black.Contains(point) && !state.White.Contains(point);

    /// <summary>屏幕坐标 → 最近的交叉点（容差约半格）；离任何交叉点都远则返回 null。</summary>
    private GomokuPoint? HitPoint(Point position)
    {
        var layout = ComputeLayout();
        if (layout is not { } l) return null;

        var gx = Math.Round((position.X - l.OriginX) / l.Cell);
        var gy = Math.Round((position.Y - l.OriginY) / l.Cell);
        if (gx < 0 || gy < 0 || gx > l.Size - 1 || gy > l.Size - 1) return null;

        var cx = l.OriginX + gx * l.Cell;
        var cy = l.OriginY + gy * l.Cell;
        var tolerance = l.Cell * 0.55;
        if (Math.Abs(position.X - cx) > tolerance || Math.Abs(position.Y - cy) > tolerance) return null;

        return new GomokuPoint((int)gx, (int)gy);
    }

    // ---------------------------------------------------------------- 绘制

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;

        var layout = ComputeLayout();
        if (layout is not { } l) return;

        DrawBoardBackground(dc, width, height);
        DrawGrid(dc, l);
        DrawStarPoints(dc, l);
        if (ShowCoordinates) DrawCoordinates(dc, l);

        var state = State;
        if (state is null) return;

        DrawHover(dc, l, state);
        DrawStones(dc, l, state);
        DrawWinningLine(dc, l, state);
        DrawLastMove(dc, l, state);
        DrawForbiddenPoints(dc, l, state);
        DrawForbiddenAttempt(dc, l, state);
    }

    /// <summary>棋盘布局：格数、格子边长、左上角交叉点坐标、棋子半径。</summary>
    private readonly record struct BoardLayout(int Size, double Cell, double OriginX, double OriginY, double Radius);

    private BoardLayout? ComputeLayout()
    {
        var size = State?.Size ?? GomokuBoard.DefaultSize;
        if (size < 2) return null;

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return null;

        // 坐标文字占一圈边；再给棋子半径留余量（否则边上的棋子会被裁掉半个）。
        var outer = ShowCoordinates ? 26.0 : 10.0;
        var span = Math.Min(width, height);
        var cell = (span - outer * 2) / (size - 1 + 0.9);
        var radius = Math.Max(2.0, cell * 0.44);
        cell = (span - outer * 2 - radius * 2) / (size - 1);
        radius = Math.Max(2.0, cell * 0.44);

        var boardSpan = cell * (size - 1);
        return new BoardLayout(
            Size: size,
            Cell: cell,
            OriginX: (width - boardSpan) / 2,
            OriginY: (height - boardSpan) / 2,
            Radius: radius);
    }

    private static void DrawBoardBackground(DrawingContext dc, double width, double height)
    {
        var rect = new Rect(0, 0, width, height);
        dc.DrawGeometry(WoodBrush, WoodBorderPen, new RectangleGeometry(rect, 10, 10));

        // 很淡的横向木纹，让底色不那么「塑料」。
        for (var i = 1; i <= 5; i++)
        {
            var y = height * i / 6.0;
            dc.DrawLine(WoodGrainPen,
                new Point(12, y - 1.5),
                new Point(width - 12, y + 1.5));
        }
    }

    private static void DrawGrid(DrawingContext dc, BoardLayout l)
    {
        var right = l.OriginX + l.Cell * (l.Size - 1);
        var bottom = l.OriginY + l.Cell * (l.Size - 1);

        for (var i = 0; i < l.Size; i++)
        {
            var x = l.OriginX + i * l.Cell;
            var y = l.OriginY + i * l.Cell;
            var pen = i == 0 || i == l.Size - 1 ? OuterGridPen : GridPen;
            dc.DrawLine(pen, new Point(x, l.OriginY), new Point(x, bottom));
            dc.DrawLine(pen, new Point(l.OriginX, y), new Point(right, y));
        }
    }

    private static void DrawStarPoints(DrawingContext dc, BoardLayout l)
    {
        var radius = Math.Max(2.4, l.Cell * 0.1);
        foreach (var point in StarPoints(l.Size))
        {
            dc.DrawEllipse(StarBrush, null,
                new Point(l.OriginX + point.X * l.Cell, l.OriginY + point.Y * l.Cell),
                radius, radius);
        }
    }

    /// <summary>星位：15 路五个（四角 + 天元），19 路九个；其它路数只画天元。</summary>
    private static IEnumerable<GomokuPoint> StarPoints(int size)
    {
        if (size == 15)
        {
            yield return new GomokuPoint(3, 3);
            yield return new GomokuPoint(11, 3);
            yield return new GomokuPoint(3, 11);
            yield return new GomokuPoint(11, 11);
            yield return new GomokuPoint(7, 7);
            yield break;
        }

        if (size == 19)
        {
            foreach (var x in new[] { 3, 9, 15 })
            {
                foreach (var y in new[] { 3, 9, 15 })
                {
                    yield return new GomokuPoint(x, y);
                }
            }

            yield break;
        }

        yield return new GomokuPoint(size / 2, size / 2);
    }

    private void DrawCoordinates(DrawingContext dc, BoardLayout l)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var fontSize = Math.Max(8.0, Math.Min(13.0, l.Cell * 0.34));

        for (var x = 0; x < l.Size; x++)
        {
            var text = ColumnName(x);
            var ft = MakeText(text, fontSize, pixelsPerDip);
            var px = l.OriginX + x * l.Cell - ft.Width / 2;
            var py = l.OriginY - l.Radius - 5 - ft.Height;
            dc.DrawText(ft, new Point(px, py));
        }

        for (var y = 0; y < l.Size; y++)
        {
            var text = (l.Size - y).ToString(CultureInfo.InvariantCulture);
            var ft = MakeText(text, fontSize, pixelsPerDip);
            var px = l.OriginX - l.Radius - 5 - ft.Width;
            var py = l.OriginY + y * l.Cell - ft.Height / 2;
            dc.DrawText(ft, new Point(px, py));
        }
    }

    private FormattedText MakeText(string text, double size, double pixelsPerDip)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CoordinateTypeface,
            size, CoordinateBrush, pixelsPerDip);

    private static string ColumnName(int x)
    {
        if (x < 26) return ((char)('A' + x)).ToString();
        return $"{(char)('A' + x / 26 - 1)}{(char)('A' + x % 26)}";
    }

    private void DrawHover(DrawingContext dc, BoardLayout l, GomokuBoardViewState state)
    {
        if (_hover is not { } p) return;

        var center = Center(l, p);
        var brush = state.Turn == GomokuCell.White ? HoverWhiteBrush : HoverBlackBrush;
        var pen = state.Turn == GomokuCell.White ? HoverWhitePen : null;
        dc.DrawEllipse(brush, pen, center, l.Radius * 0.92, l.Radius * 0.92);
    }

    private static void DrawStones(DrawingContext dc, BoardLayout l, GomokuBoardViewState state)
    {
        foreach (var point in state.Black)
        {
            DrawStone(dc, l, point, BlackStoneBrush, null);
        }

        foreach (var point in state.White)
        {
            DrawStone(dc, l, point, WhiteStoneBrush, WhiteStonePen);
        }
    }

    private static void DrawStone(DrawingContext dc, BoardLayout l, GomokuPoint point, Brush brush, Pen? pen)
    {
        var center = Center(l, point);
        var shadowCenter = new Point(center.X + l.Radius * 0.1, center.Y + l.Radius * 0.14);
        dc.DrawEllipse(StoneShadowBrush, null, shadowCenter, l.Radius, l.Radius);
        dc.DrawEllipse(brush, pen, center, l.Radius, l.Radius);
    }

    private static void DrawWinningLine(DrawingContext dc, BoardLayout l, GomokuBoardViewState state)
    {
        if (state.WinningLine.Count < 2) return;

        var start = Center(l, state.WinningLine[0]);
        var end = Center(l, state.WinningLine[^1]);
        dc.DrawLine(WinLineGlowPen, start, end);
        dc.DrawLine(WinLinePen, start, end);
    }

    private static void DrawLastMove(DrawingContext dc, BoardLayout l, GomokuBoardViewState state)
    {
        if (state.LastMove is not { } point) return;

        var center = Center(l, point);
        var radius = Math.Max(2.0, l.Radius * 0.26);
        dc.DrawEllipse(LastMoveBrush, null, center, radius, radius);
    }

    private static void DrawForbiddenPoints(DrawingContext dc, BoardLayout l, GomokuBoardViewState state)
    {
        if (!state.ShowForbidden) return;

        var length = l.Radius * 0.42;
        foreach (var point in state.ForbiddenPoints)
        {
            var center = Center(l, point);
            dc.DrawEllipse(ForbiddenFillBrush, null, center, l.Radius * 0.3, l.Radius * 0.3);
            dc.DrawLine(ForbiddenPen,
                new Point(center.X - length, center.Y - length),
                new Point(center.X + length, center.Y + length));
            dc.DrawLine(ForbiddenPen,
                new Point(center.X + length, center.Y - length),
                new Point(center.X - length, center.Y + length));
        }
    }

    private static void DrawForbiddenAttempt(DrawingContext dc, BoardLayout l, GomokuBoardViewState state)
    {
        if (state.ForbiddenAttempt is not { } point) return;

        var center = Center(l, point);
        dc.DrawEllipse(null, ForbiddenRingPen, center, l.Radius * 0.98, l.Radius * 0.98);

        var length = l.Radius * 0.72;
        dc.DrawLine(ForbiddenHeavyPen,
            new Point(center.X - length, center.Y - length),
            new Point(center.X + length, center.Y + length));
        dc.DrawLine(ForbiddenHeavyPen,
            new Point(center.X + length, center.Y - length),
            new Point(center.X - length, center.Y + length));
    }

    private static Point Center(BoardLayout l, GomokuPoint point)
        => new(l.OriginX + point.X * l.Cell, l.OriginY + point.Y * l.Cell);

    // ---------------------------------------------------------------- 画刷 / 画笔（全部冻结，渲染时不再 new）

    private static readonly Typeface CoordinateTypeface = new("Microsoft YaHei");
    private static readonly Brush CoordinateBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5D, 0x42, 0x23)));

    private static readonly Brush WoodBrush = Freeze(CreateWoodBrush());
    private static readonly Pen WoodBorderPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x63, 0x39))), 2));
    private static readonly Pen WoodGrainPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(22, 0x6B, 0x4A, 0x2A))), 1));

    private static readonly Pen GridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x6B, 0x4A, 0x2A))), 1.1));
    private static readonly Pen OuterGridPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x32, 0x18))), 2));
    private static readonly Brush StarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x32, 0x18)));

    private static readonly Brush StoneShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(44, 0, 0, 0)));
    private static readonly Brush BlackStoneBrush = Freeze(CreateStoneBrush(
        Color.FromRgb(0x72, 0x72, 0x72), Color.FromRgb(0x1A, 0x1A, 0x1A), Color.FromRgb(0x04, 0x04, 0x04)));
    private static readonly Brush WhiteStoneBrush = Freeze(CreateStoneBrush(
        Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xF1, 0xEE, 0xE8), Color.FromRgb(0xC4, 0xC0, 0xB8)));
    private static readonly Pen WhiteStonePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0x94, 0x88))), 1));

    private static readonly Brush HoverBlackBrush = Freeze(new SolidColorBrush(Color.FromArgb(96, 0x18, 0x18, 0x18)));
    private static readonly Brush HoverWhiteBrush = Freeze(new SolidColorBrush(Color.FromArgb(120, 0xFF, 0xFF, 0xFF)));
    private static readonly Pen HoverWhitePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(180, 0x9C, 0x94, 0x88))), 1));

    private static readonly Brush LastMoveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly Pen WinLineGlowPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(110, 0xFF, 0xD5, 0x4A))), 8));
    private static readonly Pen WinLinePen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xB3, 0x01))), 3.2));
    private static readonly Brush ForbiddenFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(120, 0xD3, 0x2F, 0x2F)));
    private static readonly Pen ForbiddenPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28))), 1.8));
    private static readonly Pen ForbiddenRingPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35))), 2.2));
    private static readonly Pen ForbiddenHeavyPen = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0xD3, 0x2F, 0x2F))), 3));

    private static Brush CreateWoodBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xF1, 0xD6, 0xA9), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xE3, 0xC0, 0x8B), 0.45));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(0xD4, 0xA9, 0x71), 1.0));
        return brush;
    }

    private static Brush CreateStoneBrush(Color highlight, Color mid, Color edge)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.33, 0.3),
            RadiusX = 0.78,
            RadiusY = 0.78,
        };
        brush.GradientStops.Add(new GradientStop(highlight, 0.0));
        brush.GradientStops.Add(new GradientStop(mid, 0.55));
        brush.GradientStops.Add(new GradientStop(edge, 1.0));
        return brush;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }
}
