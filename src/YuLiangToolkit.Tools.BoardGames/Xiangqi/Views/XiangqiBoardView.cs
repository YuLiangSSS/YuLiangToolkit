using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi.Views;

/// <summary>
/// 棋盘控件的<b>不可变渲染状态</b>：界面每次变化都造一个新的交给 <see cref="XiangqiBoardView"/>，
/// 控件只负责画，不持有棋局逻辑。
/// </summary>
public sealed class XiangqiBoardViewState
{
    /// <summary>90 格棋子，下标 = <c>y * 9 + x</c>（红方在下）。</summary>
    public IReadOnlyList<XiangqiPiece> Cells { get; init; } = Array.Empty<XiangqiPiece>();

    /// <summary>最后一步（画起点 / 终点方块标记）。</summary>
    public XiangqiMove? LastMove { get; init; }

    /// <summary>当前选中的棋子坐标（画金色圈）。</summary>
    public (int X, int Y)? Selected { get; init; }

    /// <summary>选中棋子的合法落点（空格画点、可吃子画圈）。</summary>
    public IReadOnlyList<XiangqiMove> LegalTargets { get; init; } = Array.Empty<XiangqiMove>();

    /// <summary>正被将军的一方（把它那一方的将 / 帅圈红）。</summary>
    public XiangqiSide? CheckSide { get; init; }

    /// <summary>显示最后一步标记。</summary>
    public bool ShowLastMove { get; init; } = true;

    /// <summary>显示棋盘坐标（红方一~九 / 黑方 1~9）。</summary>
    public bool ShowCoordinates { get; init; } = true;

    /// <summary>取某格棋子（越界 / 状态不完整返回空格）。</summary>
    public XiangqiPiece Get(int x, int y)
    {
        if (!XiangqiBoard.InBounds(x, y)) return XiangqiPiece.Empty;
        if (Cells.Count != XiangqiBoard.Width * XiangqiBoard.Height) return XiangqiPiece.Empty;
        return Cells[y * XiangqiBoard.Width + x];
    }

    /// <summary>空棋盘状态（控件还没拿到数据时用）。</summary>
    public static XiangqiBoardViewState Empty { get; } = new()
    {
        Cells = new XiangqiPiece[XiangqiBoard.Width * XiangqiBoard.Height],
    };
}

/// <summary>
/// 自绘中国象棋棋盘：木色底 + 9×10 网格 + 楚河汉界 + 九宫斜线 + 炮 / 兵位标记 + 圆形棋子，
/// 以及选中金圈、可走点、最后一步、被将军红圈。<b>支持翻转</b>（执黑时黑方在下）。
///
/// <para>全部用 <see cref="DrawingContext"/> 画（<c>OnRender</c>），不 new 子控件；
/// 点击换算成最近交叉点（容差半格），逻辑坐标 ↔ 屏幕坐标在翻转时互换：
/// <c>sx = flipped ? 8-x : x</c>、<c>sy = flipped ? 9-y : y</c>。</para>
/// </summary>
public sealed class XiangqiBoardView : FrameworkElement
{
    // ---------------- 依赖属性 ----------------

    /// <summary>渲染状态（<see cref="XiangqiBoardViewState"/>）。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(XiangqiBoardViewState), typeof(XiangqiBoardView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>棋盘是否翻转（黑方在下）。</summary>
    public static readonly DependencyProperty FlippedProperty = DependencyProperty.Register(
        nameof(Flipped), typeof(bool), typeof(XiangqiBoardView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>能不能点（不能点时点击不响应、鼠标不变手型）。</summary>
    public static readonly DependencyProperty InteractiveProperty = DependencyProperty.Register(
        nameof(Interactive), typeof(bool), typeof(XiangqiBoardView),
        new FrameworkPropertyMetadata(true));

    /// <summary>渲染状态。</summary>
    public XiangqiBoardViewState? State
    {
        get => (XiangqiBoardViewState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>棋盘翻转（执黑时自动 / 手动翻转）。</summary>
    public bool Flipped
    {
        get => (bool)GetValue(FlippedProperty);
        set => SetValue(FlippedProperty, value);
    }

    /// <summary>能不能点棋盘。</summary>
    public bool Interactive
    {
        get => (bool)GetValue(InteractiveProperty);
        set => SetValue(InteractiveProperty, value);
    }

    /// <summary>点了某个交叉点（参数是棋盘内部坐标：x=0 最左、y=0 最上，红方在下）。</summary>
    public event Action<int, int>? PointClicked;

    // ---------------- 画刷 / 画笔（静态缓存，别每帧 new） ----------------

    private static readonly Brush WoodBrush = CreateWoodBrush();
    private static readonly Brush WoodInnerBrush = CreateSolid(0x30, 0xFF, 0xE9, 0xC9);
    private static readonly Pen OuterBorderPen = CreatePen(CreateSolid(0xFF, 0xA9, 0x7B, 0x45), 2.2);
    private static readonly Pen InnerBorderPen = CreatePen(CreateSolid(0xFF, 0xC8, 0x9B, 0x5F), 1.0);
    private static readonly Pen GridPen = CreatePen(CreateSolid(0xFF, 0x6B, 0x4A, 0x2A), 1.15);
    private static readonly Pen MarkPen = CreatePen(CreateSolid(0xFF, 0x6B, 0x4A, 0x2A), 1.0);
    private static readonly Brush RiverTextBrush = CreateSolid(0xFF, 0x8A, 0x5A, 0x2E);
    private static readonly Brush CoordTextBrush = CreateSolid(0xD0, 0x6B, 0x4A, 0x2A);
    private static readonly Brush PieceFaceBrush = CreatePieceFaceBrush();
    private static readonly Pen PieceOuterPen = CreatePen(CreateSolid(0xFF, 0x5E, 0x40, 0x21), 1.7);
    private static readonly Pen PieceInnerPen = CreatePen(CreateSolid(0x90, 0x8A, 0x62, 0x35), 1.0);
    private static readonly Brush ShadowBrush = CreateSolid(0x40, 0x2E, 0x1A, 0x08);
    private static readonly Brush RedTextBrush = CreateSolid(0xFF, 0xB2, 0x1E, 0x1E);
    private static readonly Brush BlackTextBrush = CreateSolid(0xFF, 0x1D, 0x1D, 0x1D);
    private static readonly Pen SelectedPen = CreatePen(CreateSolid(0xFF, 0xF0, 0xA8, 0x30), 3.2);
    private static readonly Pen CheckPen = CreatePen(CreateSolid(0xFF, 0xE5, 0x39, 0x35), 3.0);
    private static readonly Pen LastMovePen = CreatePen(CreateSolid(0xE0, 0xE0, 0x9B, 0x30), 2.2);
    private static readonly Brush TargetDotBrush = CreateSolid(0xC0, 0x27, 0x7B, 0x3D);
    private static readonly Pen TargetDotPen = CreatePen(CreateSolid(0xFF, 0x1E, 0x5E, 0x2E), 1.0);
    private static readonly Pen CaptureRingPen = CreatePen(CreateSolid(0xD0, 0xC6, 0x3B, 0x2E), 2.6);

    private static readonly string[] RedDigits = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };
    private static readonly (int X, int Y)[] MarkPoints = CreateMarkPoints();

    // 布局（每次 OnRender 重算，点击换算也用）
    private double _left;
    private double _top;
    private double _cell;

    /// <summary>造一个棋盘控件（面在 XAML 里给：<c>Width="500" Height="565"</c>，外层 Viewbox 缩放）。</summary>
    public XiangqiBoardView()
    {
        ClipToBounds = true;
        SnapsToDevicePixels = true;
    }

    // ---------------- 渲染 ----------------

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 60 || height < 60) return;

        ComputeLayout(width, height);

        // 1) 木色底 + 边框
        var full = new Rect(0, 0, width, height);
        dc.DrawRoundedRectangle(WoodBrush, OuterBorderPen, full, 14, 14);
        dc.DrawRoundedRectangle(null, InnerBorderPen,
            new Rect(6, 6, Math.Max(1, width - 12), Math.Max(1, height - 12)), 10, 10);
        dc.DrawRoundedRectangle(WoodInnerBrush, null,
            new Rect(_left - _cell * 0.55, _top - _cell * 0.55,
                _cell * 8 + _cell * 1.1, _cell * 9 + _cell * 1.1), 8, 8);

        // 2) 网格（河界处竖线断开）
        for (var y = 0; y <= 9; y++)
        {
            dc.DrawLine(GridPen, ScreenPoint(0, y), ScreenPoint(8, y));
        }
        for (var x = 0; x <= 8; x++)
        {
            if (x is 0 or 8)
            {
                dc.DrawLine(GridPen, ScreenPoint(x, 0), ScreenPoint(x, 9));
            }
            else
            {
                dc.DrawLine(GridPen, ScreenPoint(x, 0), ScreenPoint(x, 4));
                dc.DrawLine(GridPen, ScreenPoint(x, 5), ScreenPoint(x, 9));
            }
        }

        // 3) 九宫斜线
        dc.DrawLine(GridPen, ScreenPoint(3, 0), ScreenPoint(5, 2));
        dc.DrawLine(GridPen, ScreenPoint(5, 0), ScreenPoint(3, 2));
        dc.DrawLine(GridPen, ScreenPoint(3, 7), ScreenPoint(5, 9));
        dc.DrawLine(GridPen, ScreenPoint(5, 7), ScreenPoint(3, 9));

        // 4) 炮 / 兵位的十字标记
        foreach (var (markX, markY) in MarkPoints) DrawPositionMark(dc, markX, markY);

        // 5) 楚河 · 汉界
        DrawRiverText(dc);

        // 6) 棋子 / 选中 / 可走点 / 最后一步 / 将军
        var state = State;
        if (state is not null) DrawPieces(dc, state);

        // 7) 坐标
        if (state?.ShowCoordinates == true) DrawCoordinates(dc);
    }

    /// <summary>算棋盘几何：8 格宽、9 格高，整体在控件里居中（留出坐标字的位置）。</summary>
    private void ComputeLayout(double width, double height)
    {
        var padX = Math.Max(16.0, width * 0.05);
        var padY = Math.Max(16.0, height * 0.05);
        _cell = Math.Min((width - padX * 2) / 8.0, (height - padY * 2) / 9.0);
        if (_cell <= 0) _cell = 1;
        _left = (width - _cell * 8) / 2;
        _top = (height - _cell * 9) / 2;
    }

    /// <summary>逻辑坐标 → 屏幕坐标（翻转时互换）。</summary>
    private Point ScreenPoint(int x, int y)
    {
        var screenX = Flipped ? 8 - x : x;
        var screenY = Flipped ? 9 - y : y;
        return new Point(_left + screenX * _cell, _top + screenY * _cell);
    }

    /// <summary>画一个炮位 / 兵位的小十字角标。</summary>
    private void DrawPositionMark(DrawingContext dc, int x, int y)
    {
        var center = ScreenPoint(x, y);
        var gap = _cell * 0.14;
        var length = _cell * 0.2;
        foreach (var signX in new[] { -1, 1 })
        {
            // 棋盘最左 / 最右的兵位不画靠外那一半（传统画法）
            if (signX < 0 && x == 0) continue;
            if (signX > 0 && x == 8) continue;
            foreach (var signY in new[] { -1, 1 })
            {
                var ox = center.X + signX * gap;
                var oy = center.Y + signY * gap;
                dc.DrawLine(MarkPen, new Point(ox, oy), new Point(ox + signX * length, oy));
                dc.DrawLine(MarkPen, new Point(ox, oy), new Point(ox, oy + signY * length));
            }
        }
    }

    /// <summary>河界里的「楚河」「汉界」。</summary>
    private void DrawRiverText(DrawingContext dc)
    {
        var size = Math.Max(12, _cell * 0.42);
        var typeface = new Typeface(new FontFamily("KaiTi"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var y = _top + _cell * 4.5;
        DrawCenteredText(dc, "楚　河", typeface, size, RiverTextBrush, new Point(_left + _cell * 2.0, y), pixelsPerDip);
        DrawCenteredText(dc, "汉　界", typeface, size, RiverTextBrush, new Point(_left + _cell * 6.0, y), pixelsPerDip);
    }

    /// <summary>画所有棋子 + 叠加标记。</summary>
    private void DrawPieces(DrawingContext dc, XiangqiBoardViewState state)
    {
        var radius = _cell * 0.42;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var pieceTypeface = new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold,
            FontStretches.Normal);

        for (var y = 0; y < XiangqiBoard.Height; y++)
        {
            for (var x = 0; x < XiangqiBoard.Width; x++)
            {
                var piece = state.Get(x, y);
                if (piece.IsEmpty) continue;
                var center = ScreenPoint(x, y);

                // 阴影 → 木色圆面 → 双圈 → 汉字
                dc.DrawEllipse(ShadowBrush, null,
                    new Point(center.X + _cell * 0.035, center.Y + _cell * 0.05), radius, radius);
                dc.DrawEllipse(PieceFaceBrush, PieceOuterPen, center, radius, radius);
                dc.DrawEllipse(null, PieceInnerPen, center, radius * 0.8, radius * 0.8);

                var text = new FormattedText(piece.DisplayChar, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    pieceTypeface, _cell * 0.5, piece.IsRed ? RedTextBrush : BlackTextBrush, pixelsPerDip);
                dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            }
        }

        // 最后一步：起点 / 终点画金色角标
        if (state.ShowLastMove && state.LastMove is { } last)
        {
            DrawMoveMarker(dc, last.FromX, last.FromY);
            DrawMoveMarker(dc, last.ToX, last.ToY);
        }

        // 选中金圈
        if (state.Selected is { } selected)
        {
            var center = ScreenPoint(selected.X, selected.Y);
            dc.DrawEllipse(null, SelectedPen, center, radius + _cell * 0.06, radius + _cell * 0.06);
        }

        // 可走点：空格画实心小点；可吃子画红圈
        foreach (var move in state.LegalTargets)
        {
            var target = state.Get(move.ToX, move.ToY);
            var center = ScreenPoint(move.ToX, move.ToY);
            if (target.IsEmpty)
            {
                dc.DrawEllipse(TargetDotBrush, TargetDotPen, center, _cell * 0.1, _cell * 0.1);
            }
            else
            {
                dc.DrawEllipse(null, CaptureRingPen, center, radius + _cell * 0.05, radius + _cell * 0.05);
            }
        }

        // 被将军的将 / 帅：红圈
        if (state.CheckSide is { } checkSide)
        {
            for (var y = 0; y < XiangqiBoard.Height; y++)
            {
                for (var x = 0; x < XiangqiBoard.Width; x++)
                {
                    var piece = state.Get(x, y);
                    if (piece.Type != XiangqiPieceType.King || piece.Side != checkSide) continue;
                    var center = ScreenPoint(x, y);
                    dc.DrawEllipse(null, CheckPen, center, radius + _cell * 0.1, radius + _cell * 0.1);
                }
            }
        }
    }

    /// <summary>一步棋的起点 / 终点：四个金色小角。</summary>
    private void DrawMoveMarker(DrawingContext dc, int x, int y)
    {
        var center = ScreenPoint(x, y);
        var gap = _cell * 0.3;
        var length = _cell * 0.18;
        foreach (var signX in new[] { -1, 1 })
        {
            if (signX < 0 && x == 0) continue;
            if (signX > 0 && x == 8) continue;
            foreach (var signY in new[] { -1, 1 })
            {
                var ox = center.X + signX * gap;
                var oy = center.Y + signY * gap;
                dc.DrawLine(LastMovePen, new Point(ox, oy), new Point(ox + signX * length, oy));
                dc.DrawLine(LastMovePen, new Point(ox, oy), new Point(ox, oy + signY * length));
            }
        }
    }

    /// <summary>棋盘边缘的路数坐标：红方一~九（右起）、黑方 1~9（左起）。</summary>
    private void DrawCoordinates(DrawingContext dc)
    {
        var size = Math.Max(9, _cell * 0.3);
        var typeface = new Typeface(new FontFamily("Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal,
            FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var topIsRed = Flipped;   // 翻转后红方在上

        for (var screenX = 0; screenX <= 8; screenX++)
        {
            var logicalX = Flipped ? 8 - screenX : screenX;
            var topLabel = FileLabel(logicalX, topIsRed);
            var bottomLabel = FileLabel(logicalX, !topIsRed);
            DrawCenteredText(dc, topLabel, typeface, size, CoordTextBrush,
                new Point(_left + screenX * _cell, _top - _cell * 0.4), pixelsPerDip);
            DrawCenteredText(dc, bottomLabel, typeface, size, CoordTextBrush,
                new Point(_left + screenX * _cell, _top + _cell * 9 + _cell * 0.4), pixelsPerDip);
        }
    }

    /// <summary>路数文字：红方 9-x（一~九），黑方 x+1（1~9）。</summary>
    private static string FileLabel(int logicalX, bool isRed)
        => isRed ? RedDigits[Math.Clamp(9 - logicalX - 1, 0, 8)] : (logicalX + 1).ToString();

    /// <summary>在指定中心点画一段居中的文字。</summary>
    private static void DrawCenteredText(DrawingContext dc, string text, Typeface typeface, double size,
        Brush brush, Point center, double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text)) return;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, size, brush, pixelsPerDip);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    // ---------------- 交互 ----------------

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!Interactive) return;
        if (!TryHitTest(e.GetPosition(this), out var x, out var y)) return;
        PointClicked?.Invoke(x, y);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var wanted = Interactive ? Cursors.Hand : Cursors.Arrow;
        if (Cursor != wanted) Cursor = wanted;
    }

    /// <summary>屏幕点 → 最近的交叉点（半格容差）；翻转时再换回逻辑坐标。</summary>
    private bool TryHitTest(Point point, out int x, out int y)
    {
        x = -1;
        y = -1;
        if (_cell <= 0) return false;

        var screenX = (int)Math.Round((point.X - _left) / _cell);
        var screenY = (int)Math.Round((point.Y - _top) / _cell);
        if (screenX is < 0 or > 8 || screenY is < 0 or > 9) return false;

        var centerX = _left + screenX * _cell;
        var centerY = _top + screenY * _cell;
        if (Math.Abs(point.X - centerX) > _cell * 0.55 || Math.Abs(point.Y - centerY) > _cell * 0.55) return false;

        x = Flipped ? 8 - screenX : screenX;
        y = Flipped ? 9 - screenY : screenY;
        return true;
    }

    // ---------------- 静态资源构造 ----------------

    private static Brush CreateSolid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Brush CreateWoodBrush()
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(0xF4, 0xDD, 0xB0),
            Color.FromRgb(0xE1, 0xBB, 0x81),
            new Point(0, 0), new Point(0.35, 1));
        brush.Freeze();
        return brush;
    }

    private static Brush CreatePieceFaceBrush()
    {
        var brush = new RadialGradientBrush(
            Color.FromRgb(0xFF, 0xFA, 0xEC),
            Color.FromRgb(0xE7, 0xCD, 0x9D))
        {
            GradientOrigin = new Point(0.35, 0.3),
            Center = new Point(0.4, 0.35),
            RadiusX = 0.75,
            RadiusY = 0.75,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>炮位（1,2）（7,2）（1,7）（7,7）与每排兵位的横坐标。</summary>
    private static (int X, int Y)[] CreateMarkPoints()
    {
        var points = new List<(int X, int Y)>(14)
        {
            (1, 2), (7, 2), (1, 7), (7, 7),
        };
        for (var x = 0; x <= 8; x += 2)
        {
            points.Add((x, 3));
            points.Add((x, 6));
        }
        return points.ToArray();
    }
}
