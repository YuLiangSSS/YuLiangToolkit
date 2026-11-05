using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Core.Expressions;
using YuLiangToolkit.Tools.FunctionPlot.Core;
using YuLiangToolkit.Tools.FunctionPlot.ViewModels;

namespace YuLiangToolkit.Tools.FunctionPlot.Views;

/// <summary>
/// 函数绘图的自绘控件：网格 → 坐标轴与刻度 → 多条曲线 → 图例 → 十字准星，全部在
/// <see cref="OnRender"/> 里用 <see cref="DrawingContext"/> 画（不 new 任何子控件，也不做 IO）。
///
/// <para><b>数据流</b>：ViewModel 把算好的 <see cref="State"/>（不可变快照）绑进来；控件只做
/// 「世界坐标 → 屏幕坐标」换算与绘制。鼠标操作（拖动平移 / 滚轮缩放 / 双击复位 / 光标读数）
/// 通过普通 .NET 事件抛给 View 后置代码，再转给 ViewModel，控件自己不保存业务状态。</para>
///
/// <para><b>颜色</b>：网格 / 坐标轴 / 文字 / 十字准星 / 图例底色全部是依赖属性，由 XAML 用
/// <c>{DynamicResource …}</c> 传入主题 token（换主题立即生效）；曲线颜色是数据色，来自每条函数的配置。
/// Pen / Brush 都缓存并冻结，不在每帧 new。</para>
/// </summary>
public sealed class PlotCanvas : FrameworkElement
{
    /// <summary>曲线线宽（DIP）。</summary>
    public const double CurveThickness = 1.6;

    private static readonly Typeface TextTypeface = new("Microsoft YaHei UI");

    // ---------------------------------------------------------------- 依赖属性

    /// <summary>渲染快照（变化即重绘）。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(PlotRenderState), typeof(PlotCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>网格线颜色（主题 token）。</summary>
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush));

    /// <summary>坐标轴线颜色（主题 token）。</summary>
    public static readonly DependencyProperty AxisBrushProperty = RegisterBrush(nameof(AxisBrush));

    /// <summary>刻度 / 图例 / 提示文字颜色（主题 token）。</summary>
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush));

    /// <summary>十字准星颜色（主题 token）。</summary>
    public static readonly DependencyProperty CrosshairBrushProperty = RegisterBrush(nameof(CrosshairBrush));

    /// <summary>图例底板颜色（主题 token）。</summary>
    public static readonly DependencyProperty LegendBackgroundBrushProperty = RegisterBrush(nameof(LegendBackgroundBrush));

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(PlotCanvas),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                static (d, _) => ((PlotCanvas)d).ClearPenCache()));

    // ---------------------------------------------------------------- 鼠标事件（交给 View 转给 ViewModel）

    /// <summary>鼠标在绘图区移动：世界坐标 x / y。</summary>
    public event Action<double, double>? CursorMoved;

    /// <summary>鼠标离开绘图区（或移到四周刻度边距上）。</summary>
    public event Action? CursorLeft;

    /// <summary>左键拖动平移：像素位移（向右 / 向下为正）。</summary>
    public event Action<double, double>? PanRequested;

    /// <summary>滚轮缩放：屏幕点 + 是否放大（true = 滚轮向上）。</summary>
    public event Action<Point, bool>? ZoomRequested;

    /// <summary>双击复位。</summary>
    public event Action? ResetRequested;

    // ---------------------------------------------------------------- 私有状态

    private readonly Dictionary<string, Pen> _curvePenCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CurveGeometry> _geometries = new();
    private readonly RectangleGeometry _clipGeometry = new();

    private Brush? _gridPenBrush;
    private Pen? _gridPen;
    private Brush? _axisPenBrush;
    private Pen? _axisPen;
    private Brush? _crosshairPenBrush;
    private Pen? _crosshairPen;

    private PlotRenderState? _geometryState;
    private double _geometryWidth = double.NaN;
    private double _geometryHeight = double.NaN;

    private Point? _cursor;
    private bool _dragging;
    private Point _lastDragPoint;

    /// <summary>构造：让控件自己接收鼠标事件，避免曲线画在边距外面。</summary>
    public PlotCanvas()
    {
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
    }

    /// <summary>渲染快照。</summary>
    public PlotRenderState? State
    {
        get => (PlotRenderState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>网格线颜色。</summary>
    public Brush? GridBrush
    {
        get => (Brush?)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    /// <summary>坐标轴线颜色。</summary>
    public Brush? AxisBrush
    {
        get => (Brush?)GetValue(AxisBrushProperty);
        set => SetValue(AxisBrushProperty, value);
    }

    /// <summary>文字颜色。</summary>
    public Brush? TextBrush
    {
        get => (Brush?)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>十字准星颜色。</summary>
    public Brush? CrosshairBrush
    {
        get => (Brush?)GetValue(CrosshairBrushProperty);
        set => SetValue(CrosshairBrushProperty, value);
    }

    /// <summary>图例底板颜色。</summary>
    public Brush? LegendBackgroundBrush
    {
        get => (Brush?)GetValue(LegendBackgroundBrushProperty);
        set => SetValue(LegendBackgroundBrushProperty, value);
    }

    // ---------------------------------------------------------------- 绘制

    /// <inheritdoc />
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 2 || height < 2) return;

        // 透明底：保证「空图」时鼠标事件也落在这个控件上（拖动 / 双击 / 滚轮才有反应）
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        var state = State;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (state is null)
        {
            DrawEmptyHint(dc, width, height, FunctionPlotViewModel.EmptyPlotHint, pixelsPerDip);
            return;
        }

        var viewport = state.Viewport;
        viewport.Resize(width, height);
        if (!viewport.HasSize) return;

        if (!state.HideGrid) DrawGrid(dc, viewport);
        if (!state.HideAxes) DrawAxes(dc, viewport, pixelsPerDip);

        EnsureGeometryCache(state, width, height);
        DrawCurves(dc, viewport);

        if (state.ShowLegend) DrawLegend(dc, state, viewport, pixelsPerDip);
        if (state.ShowCrosshair) DrawCrosshair(dc, viewport, pixelsPerDip);

        if (state.EmptyHint is { Length: > 0 } hint)
        {
            DrawEmptyHint(dc, width, height, hint, pixelsPerDip);
        }
    }

    private void DrawGrid(DrawingContext dc, PlotViewport viewport)
    {
        var pen = GridPen;
        if (pen is null) return;

        var rect = viewport.PlotRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var xTicks = AxisTicks.Generate(viewport.XMin, viewport.XMax, TicksForLength(rect.Width, 90));
        var yTicks = AxisTicks.Generate(viewport.YMin, viewport.YMax, TicksForLength(rect.Height, 55));

        _clipGeometry.Rect = rect;
        dc.PushClip(_clipGeometry);
        dc.PushOpacity(0.55);

        foreach (var value in xTicks.Values)
        {
            var x = viewport.WorldToScreenX(value);
            if (!double.IsFinite(x)) continue;
            dc.DrawLine(pen, new Point(x, rect.Top), new Point(x, rect.Bottom));
        }

        foreach (var value in yTicks.Values)
        {
            var y = viewport.WorldToScreenY(value);
            if (!double.IsFinite(y)) continue;
            dc.DrawLine(pen, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        dc.Pop();
        dc.Pop();
    }

    private void DrawAxes(DrawingContext dc, PlotViewport viewport, double pixelsPerDip)
    {
        var rect = viewport.PlotRect;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var xTicks = AxisTicks.Generate(viewport.XMin, viewport.XMax, TicksForLength(rect.Width, 90));
        var yTicks = AxisTicks.Generate(viewport.YMin, viewport.YMax, TicksForLength(rect.Height, 55));

        var hasXAxis = viewport.YMin <= 0 && viewport.YMax >= 0;
        var hasYAxis = viewport.XMin <= 0 && viewport.XMax >= 0;

        var pen = AxisPen;
        if (pen is not null)
        {
            if (hasXAxis)
            {
                var axisY = viewport.WorldToScreenY(0);
                if (double.IsFinite(axisY)) dc.DrawLine(pen, new Point(rect.Left, axisY), new Point(rect.Right, axisY));
            }

            if (hasYAxis)
            {
                var axisX = viewport.WorldToScreenX(0);
                if (double.IsFinite(axisX)) dc.DrawLine(pen, new Point(axisX, rect.Top), new Point(axisX, rect.Bottom));
            }
        }

        var textBrush = TextBrush;
        if (textBrush is null) return;

        // x 轴刻度：统一放在下方留白区（不在坐标轴上调文字，避免和曲线挤在一起）
        foreach (var value in xTicks.Values)
        {
            var x = viewport.WorldToScreenX(value);
            if (!double.IsFinite(x)) continue;

            var text = MakeText(AxisTicks.FormatLabel(value), 10.5, textBrush, pixelsPerDip);
            var left = Math.Clamp(x - text.Width / 2, 0, Math.Max(0, ActualWidth - text.Width));
            var top = rect.Bottom + 5;
            if (top + text.Height > ActualHeight) top = Math.Max(0, ActualHeight - text.Height);
            dc.DrawText(text, new Point(left, top));
        }

        // y 轴刻度：放在左侧留白区；x 轴也可见时跳过「0」，避免原点处两个 0 叠在一起
        foreach (var value in yTicks.Values)
        {
            if (hasXAxis && Math.Abs(value) < yTicks.Step * 1e-9) continue;

            var y = viewport.WorldToScreenY(value);
            if (!double.IsFinite(y)) continue;

            var text = MakeText(AxisTicks.FormatLabel(value), 10.5, textBrush, pixelsPerDip);
            var left = Math.Max(2, rect.Left - text.Width - 6);
            var top = Math.Clamp(y - text.Height / 2, 0, Math.Max(0, ActualHeight - text.Height));
            dc.DrawText(text, new Point(left, top));
        }
    }

    private void DrawCurves(DrawingContext dc, PlotViewport viewport)
    {
        if (_geometries.Count == 0) return;

        var rect = viewport.PlotRect;
        _clipGeometry.Rect = rect;
        dc.PushClip(_clipGeometry);

        foreach (var item in _geometries)
        {
            dc.DrawGeometry(null, GetCurvePen(item.Curve), item.Geometry);
        }

        dc.Pop();
    }

    private void DrawLegend(DrawingContext dc, PlotRenderState state, PlotViewport viewport, double pixelsPerDip)
    {
        if (state.Curves.Count == 0) return;

        var textBrush = TextBrush;
        if (textBrush is null) return;

        var rect = viewport.PlotRect;
        if (rect.Width < 120 || rect.Height < 60) return;

        var entries = new List<(PlotCurve Curve, FormattedText Text)>();
        foreach (var curve in state.Curves)
        {
            if (entries.Count >= 10) break;
            entries.Add((curve, MakeText(curve.Label, 10.5, textBrush, pixelsPerDip)));
        }

        var width = entries.Max(entry => entry.Text.Width) + 42;
        var height = entries.Count * 17 + 12;
        if (width > rect.Width - 12 || height > rect.Height - 12) return;

        var box = new Rect(rect.Right - width - 10, rect.Top + 10, width, height);

        var background = LegendBackgroundBrush;
        if (background is not null)
        {
            dc.PushOpacity(0.88);
            dc.DrawRoundedRectangle(background, null, box, 8, 8);
            dc.Pop();
        }

        var y = box.Top + 6;
        foreach (var (curve, text) in entries)
        {
            var pen = GetCurvePen(curve);
            dc.DrawLine(pen, new Point(box.Left + 9, y + text.Height / 2), new Point(box.Left + 27, y + text.Height / 2));
            dc.DrawText(text, new Point(box.Left + 32, y));
            y += 17;
        }
    }

    private void DrawCrosshair(DrawingContext dc, PlotViewport viewport, double pixelsPerDip)
    {
        if (_cursor is not { } cursor) return;

        var rect = viewport.PlotRect;
        if (!rect.Contains(cursor)) return;

        var pen = CrosshairPen;
        if (pen is not null)
        {
            dc.DrawLine(pen, new Point(cursor.X, rect.Top), new Point(cursor.X, rect.Bottom));
            dc.DrawLine(pen, new Point(rect.Left, cursor.Y), new Point(rect.Right, cursor.Y));
        }

        var textBrush = TextBrush;
        if (textBrush is null) return;

        var (worldX, worldY) = viewport.ScreenToWorld(cursor.X, cursor.Y);
        if (!double.IsFinite(worldX) || !double.IsFinite(worldY)) return;

        var text = MakeText(
            $"({MathFormat.FormatTick(worldX)}, {MathFormat.FormatTick(worldY)})",
            10.5, textBrush, pixelsPerDip);

        var left = cursor.X + 10;
        if (left + text.Width > rect.Right) left = cursor.X - text.Width - 10;
        var top = cursor.Y - text.Height - 8;
        if (top < rect.Top) top = cursor.Y + 10;

        dc.DrawText(text, new Point(left, top));
    }

    private void DrawEmptyHint(DrawingContext dc, double width, double height, string hint, double pixelsPerDip)
    {
        var textBrush = TextBrush;
        if (textBrush is null) return;

        var title = MakeText(hint, 15, textBrush, pixelsPerDip);
        var subtitle = MakeText("左键拖动平移 · 滚轮以光标为中心缩放 · 双击复位到 −10..10 / −6..6", 11, textBrush, pixelsPerDip);

        var centerX = width / 2;
        var centerY = height / 2;

        dc.PushOpacity(0.85);
        dc.DrawText(title, new Point(centerX - title.Width / 2, centerY - title.Height - 4));
        dc.DrawText(subtitle, new Point(centerX - subtitle.Width / 2, centerY + 4));
        dc.Pop();
    }

    // ---------------------------------------------------------------- 几何缓存

    /// <summary>按「快照 + 控件尺寸」缓存每条曲线的 StreamGeometry；平移 / 缩放换了快照才重建。</summary>
    private void EnsureGeometryCache(PlotRenderState state, double width, double height)
    {
        if (ReferenceEquals(_geometryState, state)
            && Math.Abs(_geometryWidth - width) < 0.01
            && Math.Abs(_geometryHeight - height) < 0.01)
        {
            return;
        }

        _geometries.Clear();
        _geometryState = state;
        _geometryWidth = width;
        _geometryHeight = height;

        var viewport = state.Viewport;
        viewport.Resize(width, height);

        foreach (var curve in state.Curves)
        {
            var geometry = BuildGeometry(curve, viewport);
            if (geometry is not null) _geometries.Add(new CurveGeometry(curve, geometry));
        }
    }

    private static StreamGeometry? BuildGeometry(PlotCurve curve, PlotViewport viewport)
    {
        var geometry = new StreamGeometry();
        var hasFigure = false;

        using (var context = geometry.Open())
        {
            foreach (var segment in curve.Segments)
            {
                if (segment.Length < 2) continue;

                var points = new List<Point>(segment.Length);
                var valid = true;
                foreach (var worldPoint in segment)
                {
                    var screen = viewport.WorldToScreen(worldPoint.X, worldPoint.Y);
                    if (!double.IsFinite(screen.X) || !double.IsFinite(screen.Y))
                    {
                        valid = false;
                        break;
                    }

                    points.Add(screen);
                }

                if (!valid || points.Count < 2) continue;

                context.BeginFigure(points[0], isFilled: false, isClosed: false);
                context.PolyLineTo(points.GetRange(1, points.Count - 1), isStroked: true, isSmoothJoin: false);
                hasFigure = true;
            }
        }

        if (!hasFigure) return null;

        geometry.Freeze();
        return geometry;
    }

    // ---------------------------------------------------------------- 鼠标

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var position = e.GetPosition(this);

        if (_dragging)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
            }
            else
            {
                var dx = position.X - _lastDragPoint.X;
                var dy = position.Y - _lastDragPoint.Y;
                if (Math.Abs(dx) >= 0.01 || Math.Abs(dy) >= 0.01)
                {
                    _lastDragPoint = position;
                    PanRequested?.Invoke(dx, dy);   // ViewModel 同步重算 → State 更新
                }

                UpdateCursor(position);
                return;
            }
        }

        UpdateCursor(position);
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (CurrentViewport() is null) return;

        if (e.ClickCount == 2)
        {
            ResetRequested?.Invoke();
            e.Handled = true;
            return;
        }

        _dragging = true;
        _lastDragPoint = e.GetPosition(this);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    /// <inheritdoc />
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    /// <inheritdoc />
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursor = null;
        if (!_dragging) Cursor = Cursors.Arrow;
        CursorLeft?.Invoke();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (CurrentViewport() is null) return;

        var position = e.GetPosition(this);
        ZoomRequested?.Invoke(position, e.Delta > 0);
        UpdateCursor(position);   // 缩放后同一像素对应的世界坐标变了，读数要跟着刷新
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
    }

    /// <summary>更新光标位置并抛出世界坐标（只在绘图区内才读数）。</summary>
    private void UpdateCursor(Point position)
    {
        var viewport = CurrentViewport();
        if (viewport is null)
        {
            _cursor = null;
            InvalidateVisual();
            return;
        }

        var rect = viewport.PlotRect;
        if (!rect.Contains(position))
        {
            _cursor = null;
            if (!_dragging) Cursor = Cursors.Arrow;
            CursorLeft?.Invoke();
            InvalidateVisual();
            return;
        }

        _cursor = position;
        if (!_dragging) Cursor = Cursors.Cross;

        var (worldX, worldY) = viewport.ScreenToWorld(position.X, position.Y);
        CursorMoved?.Invoke(worldX, worldY);
        InvalidateVisual();
    }

    /// <summary>拿一份「尺寸已更新」的视口用于换算；没有快照 / 尺寸时返回 null。</summary>
    private PlotViewport? CurrentViewport()
    {
        var state = State;
        if (state is null || ActualWidth < 2 || ActualHeight < 2) return null;
        state.Viewport.Resize(ActualWidth, ActualHeight);
        return state.Viewport;
    }

    // ---------------------------------------------------------------- 缓存 / 工具

    private Pen? GridPen
    {
        get
        {
            var brush = GridBrush;
            if (brush is null) return null;
            if (ReferenceEquals(_gridPenBrush, brush) && _gridPen is not null) return _gridPen;

            _gridPenBrush = brush;
            _gridPen = CreatePen(brush, 1.0);
            return _gridPen;
        }
    }

    private Pen? AxisPen
    {
        get
        {
            var brush = AxisBrush;
            if (brush is null) return null;
            if (ReferenceEquals(_axisPenBrush, brush) && _axisPen is not null) return _axisPen;

            _axisPenBrush = brush;
            _axisPen = CreatePen(brush, 1.4);
            return _axisPen;
        }
    }

    private Pen? CrosshairPen
    {
        get
        {
            var brush = CrosshairBrush;
            if (brush is null) return null;
            if (ReferenceEquals(_crosshairPenBrush, brush) && _crosshairPen is not null) return _crosshairPen;

            _crosshairPenBrush = brush;
            _crosshairPen = new Pen(brush, 1.0)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0),
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            _crosshairPen.Freeze();
            return _crosshairPen;
        }
    }

    private Pen GetCurvePen(PlotCurve curve)
    {
        if (_curvePenCache.TryGetValue(curve.ColorHex, out var cached)) return cached;

        var pen = new Pen(curve.Stroke, CurveThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        _curvePenCache[curve.ColorHex] = pen;
        return pen;
    }

    private static Pen CreatePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>主题色换了：把基于旧画刷的 Pen 全部丢掉，下帧重建。</summary>
    private void ClearPenCache()
    {
        _gridPenBrush = null;
        _gridPen = null;
        _axisPenBrush = null;
        _axisPen = null;
        _crosshairPenBrush = null;
        _crosshairPen = null;
        _curvePenCache.Clear();
    }

    private FormattedText MakeText(string text, double fontSize, Brush brush, double pixelsPerDip)
        => new(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            TextTypeface,
            fontSize,
            brush,
            pixelsPerDip);

    /// <summary>按可用像素长度决定刻度上限（控件越宽刻度越多，但夹在 4~12 之间）。</summary>
    private static int TicksForLength(double length, double spacing)
        => Math.Clamp((int)(length / spacing), AxisTicks.MinGeneratedTicks, AxisTicks.MaxGeneratedTicks);

    /// <summary>一条曲线 + 它在当前快照下的屏幕折线。</summary>
    private sealed class CurveGeometry
    {
        public CurveGeometry(PlotCurve curve, StreamGeometry geometry)
        {
            Curve = curve;
            Geometry = geometry;
        }

        public PlotCurve Curve { get; }

        public StreamGeometry Geometry { get; }
    }
}
