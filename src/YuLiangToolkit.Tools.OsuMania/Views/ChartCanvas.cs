using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.OsuMania.Core.Editor;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 谱面时间轴的**自绘画布**：轨道 / 网格 / note / LN / 时间点 / 播放头，全部在
/// <see cref="OnRender"/> 里用 <see cref="DrawingContext"/> 画。
///
/// <para><b>为什么不用 ItemsControl / Canvas + 控件集合</b>（这是「大量 note 卡顿」的根因）：</para>
/// <list type="bullet">
///   <item>每个 note 一个 <c>Border</c> / <c>Rectangle</c> 的话，10 万 note 就是 10 万个
///         <c>Visual</c>：创建慢、内存爆、布局遍历 O(n)、每次编辑都要改视觉树；</item>
///   <item>虚拟化也救不了：<c>VirtualizingStackPanel</c> 只按线性顺序虚拟化，而谱面要按
///         **时间窗口**取，并且拖动 / 滚动中随时变；</item>
///   <item>自绘只有一个 Visual，绘制量 = 屏幕上那几个矩形，跟谱面总大小**无关**。</item>
/// </list>
///
/// <para><b>性能要点</b>（改代码时别破坏）：</para>
/// <list type="number">
///   <item>只画快照里的 note（<see cref="ChartRenderState.Build"/> 已经二分裁剪 + 抽稀）；</item>
///   <item>Brush / Pen 全部**缓存并冻结**，不在 <c>OnRender</c> 里 new（一帧几百个对象会很痛）；</item>
///   <item>同色 note 合并成**一次** <c>DrawRectangle</c> 调用 —— 用 <see cref="DrawingContext"/>
///         的批量绘制配合单个 <c>GeometryGroup</c> 反而更慢，这里按颜色分组逐条画矩形，
///         每个矩形都是一次廉价的 DrawRectangle；</item>
///   <item>文字（小节号 / 时间）只在需要时画，且用 <c>FormattedText</c> 缓存（见 <see cref="_timeText"/>）。</item>
/// </list>
///
/// <para><b>颜色</b>：全部是依赖属性，由 XAML 用 <c>{DynamicResource}</c> 传主题 token，
/// 换主题立即生效（不 freeze 主题画刷，靠 <see cref="ClearCache"/> 重建）。</para>
/// </summary>
public sealed class ChartCanvas : FrameworkElement
{
    /// <summary>左侧时间点标尺宽度（像素）。单一来源在 <see cref="LaneLayout.RulerWidth"/>。</summary>
    public const double RulerWidth = LaneLayout.RulerWidth;

    /// <summary>note 最小高度（像素）。实际高度随列宽缩放，见 <see cref="NoteHeightFor"/>。</summary>
    public const double MinNoteHeight = 6;

    /// <summary>note 最大高度（像素）——太厚会在密集谱面里糊成一片（音符间距可能只有十几像素）。</summary>
    public const double MaxNoteHeight = 14;

    /// <summary>
    /// note 高度：取列宽的 0.4，夹在 8~16px 之间。
    /// 固定像素在窗口变宽 / 键数少时像细线，太大又会盖住相邻音符，所以跟着列宽走并设上下限。
    /// </summary>
    public static double NoteHeightFor(double columnWidth)
        => Math.Clamp(columnWidth * 0.4, MinNoteHeight, MaxNoteHeight);

    /// <summary>LN 身体宽度占 note 宽度的比例（比头尾窄一点，看起来才像一条“面条”）。</summary>
    public const double LongNoteWidthRatio = 0.55;

    private static readonly Typeface LabelTypeface = new("Consolas");

    // ---------------------------------------------------------------- 依赖属性

    /// <summary>渲染快照（变化即重绘）。</summary>
    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(ChartRenderState), typeof(ChartCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender,
            static (d, _) => ((ChartCanvas)d).ClearCache()));

    /// <summary>轨道背景色（隔列交替）。</summary>
    public static readonly DependencyProperty LaneBrushProperty = RegisterBrush(nameof(LaneBrush));

    /// <summary>轨道分隔线颜色。</summary>
    public static readonly DependencyProperty LaneLineBrushProperty = RegisterBrush(nameof(LaneLineBrush));

    /// <summary>小节线颜色。</summary>
    public static readonly DependencyProperty BarLineBrushProperty = RegisterBrush(nameof(BarLineBrush));

    /// <summary>拍线颜色。</summary>
    public static readonly DependencyProperty BeatLineBrushProperty = RegisterBrush(nameof(BeatLineBrush));

    /// <summary>细分网格线颜色。</summary>
    public static readonly DependencyProperty GridLineBrushProperty = RegisterBrush(nameof(GridLineBrush));

    /// <summary>普通键颜色。</summary>
    public static readonly DependencyProperty NoteBrushProperty = RegisterBrush(nameof(NoteBrush));

    /// <summary>LN 身体颜色。</summary>
    public static readonly DependencyProperty LongNoteBrushProperty = RegisterBrush(nameof(LongNoteBrush));

    /// <summary>选中 / 拖动中的高亮色。</summary>
    public static readonly DependencyProperty SelectionBrushProperty = RegisterBrush(nameof(SelectionBrush));

    /// <summary>播放头颜色。</summary>
    public static readonly DependencyProperty PlayheadBrushProperty = RegisterBrush(nameof(PlayheadBrush));

    /// <summary>红线颜色。</summary>
    public static readonly DependencyProperty RedLineBrushProperty = RegisterBrush(nameof(RedLineBrush));

    /// <summary>绿线颜色。</summary>
    public static readonly DependencyProperty GreenLineBrushProperty = RegisterBrush(nameof(GreenLineBrush));

    /// <summary>标尺文字颜色。</summary>
    public static readonly DependencyProperty RulerTextBrushProperty = RegisterBrush(nameof(RulerTextBrush));

    /// <summary>标尺底色。</summary>
    public static readonly DependencyProperty RulerBackgroundBrushProperty = RegisterBrush(nameof(RulerBackgroundBrush));

    private static DependencyProperty RegisterBrush(string name)
        => DependencyProperty.Register(name, typeof(Brush), typeof(ChartCanvas),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsRender,
                static (d, _) => ((ChartCanvas)d).ClearCache()));

    // ---------------------------------------------------------------- 缓存的画笔

    private readonly System.Collections.Generic.Dictionary<string, Pen> _pens = new();
    private readonly System.Collections.Generic.Dictionary<string, Brush> _brushes = new();

    /// <summary>标尺上「时间文字」的缓存（同样的字符串不重复造 FormattedText）。</summary>
    private readonly System.Collections.Generic.Dictionary<string, FormattedText> _timeText = new();

    /// <summary>本帧的小节号文字缓存（键 = 小节序号）。</summary>
    private readonly System.Collections.Generic.Dictionary<string, FormattedText> _barText = new();

    public ChartCanvas()
    {
        // 自绘控件用 OnRender 里铺满的矩形做命中区域（透明画刷在 WPF 里是可命中的，null 不行）
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        Focusable = true;
    }

    /// <summary>渲染快照。</summary>
    public ChartRenderState? State
    {
        get => (ChartRenderState?)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    /// <summary>轨道背景色。</summary>
    public Brush? LaneBrush { get => (Brush?)GetValue(LaneBrushProperty); set => SetValue(LaneBrushProperty, value); }

    /// <summary>轨道分隔线颜色。</summary>
    public Brush? LaneLineBrush { get => (Brush?)GetValue(LaneLineBrushProperty); set => SetValue(LaneLineBrushProperty, value); }

    /// <summary>小节线颜色。</summary>
    public Brush? BarLineBrush { get => (Brush?)GetValue(BarLineBrushProperty); set => SetValue(BarLineBrushProperty, value); }

    /// <summary>拍线颜色。</summary>
    public Brush? BeatLineBrush { get => (Brush?)GetValue(BeatLineBrushProperty); set => SetValue(BeatLineBrushProperty, value); }

    /// <summary>细分线颜色。</summary>
    public Brush? GridLineBrush { get => (Brush?)GetValue(GridLineBrushProperty); set => SetValue(GridLineBrushProperty, value); }

    /// <summary>普通键颜色。</summary>
    public Brush? NoteBrush { get => (Brush?)GetValue(NoteBrushProperty); set => SetValue(NoteBrushProperty, value); }

    /// <summary>LN 身体颜色。</summary>
    public Brush? LongNoteBrush { get => (Brush?)GetValue(LongNoteBrushProperty); set => SetValue(LongNoteBrushProperty, value); }

    /// <summary>选中色。</summary>
    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }

    /// <summary>播放头颜色。</summary>
    public Brush? PlayheadBrush { get => (Brush?)GetValue(PlayheadBrushProperty); set => SetValue(PlayheadBrushProperty, value); }

    /// <summary>红线颜色。</summary>
    public Brush? RedLineBrush { get => (Brush?)GetValue(RedLineBrushProperty); set => SetValue(RedLineBrushProperty, value); }

    /// <summary>绿线颜色。</summary>
    public Brush? GreenLineBrush { get => (Brush?)GetValue(GreenLineBrushProperty); set => SetValue(GreenLineBrushProperty, value); }

    /// <summary>标尺文字颜色。</summary>
    public Brush? RulerTextBrush { get => (Brush?)GetValue(RulerTextBrushProperty); set => SetValue(RulerTextBrushProperty, value); }

    /// <summary>标尺底色。</summary>
    public Brush? RulerBackgroundBrush { get => (Brush?)GetValue(RulerBackgroundBrushProperty); set => SetValue(RulerBackgroundBrushProperty, value); }

    /// <summary>内容区的栏位信息（View 后置代码把鼠标坐标转成编辑器坐标时用）。</summary>
    public double LaneWidth => Math.Max(1, ActualWidth - RulerWidth);

    /// <summary>
    /// 柱状区（轨道）在画布里的左边界：按当前快照的居中布局算；
    /// 没有快照时退回「标尺右边」。
    /// </summary>
    public double LaneLeft => State?.LaneLeft ?? RulerWidth;

    /// <summary>标尺在本画布里的左边界（「标尺 + 轨道」整块居中时它不是 0）。</summary>
    public double RulerLeft => State?.RulerLeft ?? 0;

    /// <summary>当前轨道区实际宽度（没有快照时按铺满算）。</summary>
    public double LaneActualWidth => State?.LaneWidth ?? LaneWidth;

    /// <summary>
    /// 把当前的横向布局同步给编辑器（命中测试 / 列号换算都靠它）。
    /// 直接用快照里的值，不做二次推算 —— 免得两处算法走偏。
    /// </summary>
    public void ApplyLayoutTo(Core.Editor.ChartEditor editor)
    {
        if (editor is null) return;
        editor.SetLayout(LaneLeft, LaneActualWidth, editor.Beatmap.KeyCount);
    }

    // ---------------------------------------------------------------- 鼠标事件（交给 View 转给 ViewModel）

    /// <summary>左键按下：内容区坐标。</summary>
    public event Action<Point>? Pressed;

    /// <summary>左键按住移动。</summary>
    public event Action<Point>? Dragged;

    /// <summary>左键松开。</summary>
    public event Action<Point>? Released;

    /// <summary>右键（删除）。</summary>
    public event Action<Point>? RightPressed;

    /// <summary>中键按下（拖动滚动时间轴，不移动播放头）。</summary>
    public event Action<Point>? MiddlePressed;

    /// <summary>中键 / 左键拖动滚动：像素位移。</summary>
    public event Action<double>? ScrollDragged;

    /// <summary>滚轮：鼠标点 + 滚轮方向（true = 向上 = 放大）。</summary>
    public event Action<Point, bool>? WheelZoomed;

    /// <summary>鼠标移动（没有按键）：用于光标形状。</summary>
    public event Action<Point>? HoverMoved;

    protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        Pressed?.Invoke(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if (IsMouseCaptured && e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            Dragged?.Invoke(point);
            return;
        }

        if (IsMouseCaptured && e.MiddleButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            ScrollDragged?.Invoke(point.Y - _lastMiddleY);
            _lastMiddleY = point.Y;
            return;
        }

        HoverMoved?.Invoke(point);
    }

    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = e.GetPosition(this);
        if (IsMouseCaptured) ReleaseMouseCapture();
        Released?.Invoke(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        RightPressed?.Invoke(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseDown(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;

        CaptureMouse();
        _lastMiddleY = e.GetPosition(this).Y;
        MiddlePressed?.Invoke(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle) return;
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(System.Windows.Input.MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        WheelZoomed?.Invoke(e.GetPosition(this), e.Delta > 0);
        e.Handled = true;
    }

    /// <summary>中键拖动时上一个鼠标 Y（算增量用）。</summary>
    private double _lastMiddleY;

    // ---------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var state = State;
        if (state is null)
        {
            dc.DrawRectangle(LaneBrush ?? Brushes.Transparent, null, new Rect(0, 0, width, height));
            DrawEmptyHint(dc, width, height);
            return;
        }

        // 轨道区用快照里的居中布局（宽度可由用户调）；标尺固定贴左边。
        // 轨道区以外的部分画成更暗的底色，让「Note 浏览区」的边界一眼可见。
        var laneLeft = state.LaneLeft;
        var laneWidth = Math.Max(1, state.LaneWidth);
        var columnWidth = laneWidth / Math.Max(1, state.KeyCount);

        dc.DrawRectangle(GetBrushFor("outside", LaneBrush, 0.35) ?? LaneBrush ?? Brushes.Transparent,
            null, new Rect(0, 0, width, height));
        dc.DrawRectangle(LaneBrush ?? Brushes.Transparent, null, new Rect(laneLeft, 0, laneWidth, height));

        DrawLaneBackground(dc, state, laneLeft, laneWidth, columnWidth, height);
        DrawGrid(dc, state, laneLeft, laneWidth, height);
        DrawLaneSeparators(dc, state, laneLeft, columnWidth, height);
        DrawLongNotes(dc, state, laneLeft, columnWidth);
        DrawNotes(dc, state, laneLeft, columnWidth);
        DrawCreatePreview(dc, state, laneLeft, columnWidth);
        DrawMarquee(dc, state, laneLeft, columnWidth);
        DrawTimingPoints(dc, state, laneLeft, laneWidth, height);
        DrawPlayhead(dc, state, laneLeft, width);
        DrawRuler(dc, state, height);
    }

    private void DrawEmptyHint(DrawingContext dc, double width, double height)
    {
        var text = MakeText("打开一个 .osu 谱面开始编辑", 13, RulerTextBrush);
        dc.DrawText(text, new Point((width - text.Width) / 2, (height - text.Height) / 2));
    }

    /// <summary>轨道底：奇偶列交替一点点亮度，让人一眼看出分栏。</summary>
    private void DrawLaneBackground(DrawingContext dc, ChartRenderState state, double laneLeft,
        double laneWidth, double columnWidth, double height)
    {
        var alt = GetBrushFor("lane-alt", LaneBrush, 0.5);
        if (alt is null) return;

        for (var column = 0; column < state.KeyCount; column += 2)
        {
            var x = laneLeft + column * columnWidth;
            dc.DrawRectangle(alt, null, new Rect(x, 0, columnWidth, height));
        }
    }

    private void DrawGrid(DrawingContext dc, ChartRenderState state, double laneLeft, double laneWidth, double height)
    {
        var barPen = GetPen("bar", BarLineBrush, 1.4);
        var beatPen = GetPen("beat", BeatLineBrush, 1.0);
        var subPen = GetPen("sub", GridLineBrush, 0.8);

        foreach (var line in state.GridLines)
        {
            var y = (line.Time - state.StartTime) * state.PxPerMs;
            if (y < -2 || y > height + 2) continue;

            var pen = line.Kind switch
            {
                BeatGrid.GridLineKind.Bar => barPen,
                BeatGrid.GridLineKind.Beat => beatPen,
                _ => subPen,
            };
            if (pen is null) continue;

            dc.DrawLine(pen, new Point(laneLeft, y), new Point(laneLeft + laneWidth, y));
        }
    }

    private void DrawLaneSeparators(DrawingContext dc, ChartRenderState state, double laneLeft,
        double columnWidth, double height)
    {
        var pen = GetPen("lane-line", LaneLineBrush, 1.0);
        if (pen is null) return;

        for (var column = 0; column <= state.KeyCount; column++)
        {
            var x = Math.Round(laneLeft + column * columnWidth) + 0.5;
            dc.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }
    }

    /// <summary>先画所有 LN 身体（在普通键下面，避免盖住头尾）。</summary>
    private void DrawLongNotes(DrawingContext dc, ChartRenderState state, double laneLeft, double columnWidth)
    {
        // 身体用半透明的 LN 色：和头尾（实色）拉开层次，一眼就能看出是面条而不是普通键
        var brush = GetBrushFor("ln-body", LongNoteBrush ?? NoteBrush, 0.55);
        var selectedBrush = SelectionBrush;
        if (brush is null) return;

        var height = ActualHeight;
        var noteHeight = NoteHeightFor(columnWidth);
        var inset = Math.Max(1, columnWidth * 0.08);
        var noteWidth = Math.Max(2, columnWidth - inset * 2);
        // LN 身体比头尾窄、且水平居中（看起来才像一根面条，而不是一块砖）
        var bodyWidth = Math.Max(2, noteWidth * LongNoteWidthRatio);
        var bodyInset = (noteWidth - bodyWidth) / 2;

        for (var slot = 0; slot < state.Notes.Count; slot++)
        {
            var note = state.Notes[slot].Note;
            if (!note.IsLongNote) continue;

            var x = laneLeft + note.Column * columnWidth + inset + bodyInset;
            var y1 = (note.StartTime - state.StartTime) * state.PxPerMs;
            var y2 = (note.EndTime - state.StartTime) * state.PxPerMs;

            // 零长度 LN：画成一条细线，让人知道它存在
            if (y2 - y1 < 1)
            {
                dc.DrawRectangle(brush, null, new Rect(x, y1, bodyWidth, 1.5));
                continue;
            }

            var top = Math.Max(-2, y1);
            var bottom = Math.Min(height + 2, y2);
            if (bottom <= top) continue;

            // 上下各延伸半个 note 高度：让身体真正接到头尾的中心，否则会露出一截缝（看起来像断了）
            var half = noteHeight / 2;
            var bodyTop = Math.Max(-2, top - half);
            var bodyBottom = Math.Min(height + 2, bottom + half);

            var useBrush = state.SelectedIndices.Contains(state.Notes[slot].Index)
                ? selectedBrush ?? brush
                : brush;

            dc.DrawRectangle(useBrush, null, new Rect(x, bodyTop, bodyWidth, bodyBottom - bodyTop));
        }
    }

    /// <summary>普通键 + LN 的头尾。</summary>
    private void DrawNotes(DrawingContext dc, ChartRenderState state, double laneLeft, double columnWidth)
    {
        var brush = NoteBrush;
        var selectedBrush = SelectionBrush;
        if (brush is null) return;

        var height = ActualHeight;
        var noteHeight = NoteHeightFor(columnWidth);
        var halfHeight = noteHeight / 2;
        var inset = Math.Max(1, columnWidth * 0.08);
        var noteWidth = Math.Max(2, columnWidth - inset * 2);

        for (var slot = 0; slot < state.Notes.Count; slot++)
        {
            var note = state.Notes[slot].Note;
            var y = (note.StartTime - state.StartTime) * state.PxPerMs;
            if (y < -noteHeight * 2 || y > height + noteHeight * 2) continue;

            var useBrush = state.SelectedIndices.Contains(state.Notes[slot].Index)
                ? selectedBrush ?? brush
                : (note.IsLongNote ? LongNoteBrush ?? brush : brush);

            var x = laneLeft + note.Column * columnWidth + inset;

            // 普通键整块；LN 只画头尾两条（身体已经在下面画过）
            if (!note.IsLongNote)
            {
                dc.DrawRectangle(useBrush, null, new Rect(x, y - halfHeight, noteWidth, noteHeight));
                continue;
            }

            dc.DrawRectangle(useBrush, null, new Rect(x, y - halfHeight, noteWidth, noteHeight));
            var tailY = (note.EndTime - state.StartTime) * state.PxPerMs;
            if (tailY - y >= 1)
            {
                dc.DrawRectangle(useBrush, null, new Rect(x, tailY - halfHeight, noteWidth, noteHeight));
            }
        }
    }

    /// <summary>拖动创建长条时的预览（半透明）。</summary>
    private void DrawCreatePreview(DrawingContext dc, ChartRenderState state, double laneLeft, double columnWidth)
    {
        if (!state.CreatePreview.HasValue || SelectionBrush is null) return;

        var preview = state.CreatePreview;
        var brush = GetBrushFor("preview", SelectionBrush, 0.45);
        if (brush is null) return;

        var inset = Math.Max(1, columnWidth * 0.08);
        var noteWidth = Math.Max(2, columnWidth - inset * 2);
        var bodyWidth = Math.Max(2, noteWidth * LongNoteWidthRatio);
        var x = laneLeft + preview.Column * columnWidth + inset + (noteWidth - bodyWidth) / 2;
        var y1 = (preview.StartTime - state.StartTime) * state.PxPerMs;
        var y2 = (preview.EndTime - state.StartTime) * state.PxPerMs;
        var top = Math.Min(y1, y2);
        var h = Math.Max(NoteHeightFor(columnWidth), Math.Abs(y2 - y1));

        dc.DrawRectangle(brush, null, new Rect(x, top, bodyWidth, h));
    }

    /// <summary>框选矩形（半透明填充 + 亮边；和 osu! 编辑器的选择框一个意思）。</summary>
    private void DrawMarquee(DrawingContext dc, ChartRenderState state, double laneLeft, double columnWidth)
    {
        if (!state.Marquee.HasValue) return;

        var box = state.Marquee.Box;

        var x1 = laneLeft + box.FromColumn * columnWidth;
        var x2 = laneLeft + (box.ToColumn + 1) * columnWidth;
        var y1 = (box.FromTime - state.StartTime) * state.PxPerMs;
        var y2 = (box.ToTime - state.StartTime) * state.PxPerMs;

        var rect = new Rect(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));
        if (rect.Width <= 0 || rect.Height <= 0) return;

        var fill = GetBrushFor("marquee-fill", SelectionBrush, 0.16);
        var pen = GetPen("marquee-edge", SelectionBrush, 1.2);
        if (fill is null || pen is null) return;

        dc.DrawRectangle(fill, pen, rect);
    }

    /// <summary>红线 / 绿线（画在左标尺里 + 贯穿轨道区的细线）。</summary>
    private void DrawTimingPoints(DrawingContext dc, ChartRenderState state, double laneLeft,
        double laneWidth, double height)
    {
        var redPen = GetPen("tp-red", RedLineBrush, 1.2);
        var greenPen = GetPen("tp-green", GreenLineBrush, 1.0);

        foreach (var item in state.TimingPoints)
        {
            var y = (item.Point.Time - state.StartTime) * state.PxPerMs;
            if (y < -2 || y > height + 2) continue;

            var pen = item.IsRed ? redPen : greenPen;
            if (pen is null) continue;

            dc.DrawLine(pen, new Point(laneLeft, y), new Point(laneLeft + laneWidth, y));

            // 标尺里的小三角 / 竖条，方便点中
            var markerBrush = item.IsRed ? RedLineBrush : GreenLineBrush;
            if (markerBrush is not null)
            {
                dc.DrawRectangle(markerBrush, null,
                    new Rect(item.IsRed ? 4 : 12, y - 1.5, item.IsRed ? 12 : 8, 3));
            }
        }
    }

    /// <summary>播放头（贯穿轨道区，带一点发光感：两根线叠）。</summary>
    private void DrawPlayhead(DrawingContext dc, ChartRenderState state, double laneLeft, double width)
    {
        if (!state.ShowPlayhead) return;

        var y = (state.PlayheadMs - state.StartTime) * state.PxPerMs;
        if (y < -20 || y > ActualHeight + 20) return;

        var glow = GetBrushFor("playhead-glow", PlayheadBrush, 0.25);
        var pen = GetPen("playhead", PlayheadBrush, 1.6);
        if (glow is null || pen is null) return;

        dc.DrawRectangle(glow, null, new Rect(laneLeft, y - 6, width - laneLeft, 12));
        dc.DrawLine(pen, new Point(laneLeft, y), new Point(width, y));
    }

    /// <summary>左标尺：小节号 / 时间刻度（跟着「标尺 + 轨道」整块一起居中）。</summary>
    private void DrawRuler(DrawingContext dc, ChartRenderState state, double height)
    {
        var rulerLeft = state.RulerLeft;
        var rulerRight = rulerLeft + RulerWidth;

        if (RulerBackgroundBrush is not null)
        {
            dc.DrawRectangle(RulerBackgroundBrush, null, new Rect(rulerLeft, 0, RulerWidth, height));
        }

        var pen = GetPen("ruler-tick", LaneLineBrush, 1.0);
        var barIndex = 0;
        var lastLabelY = double.NegativeInfinity;

        foreach (var line in state.GridLines)
        {
            var y = (line.Time - state.StartTime) * state.PxPerMs;
            if (y < -10 || y > height + 10) continue;

            if (line.Kind == BeatGrid.GridLineKind.Bar)
            {
                barIndex++;

                // 小节号：间距太小就跳过，避免糊成一片
                if (y - lastLabelY >= 16)
                {
                    var key = barIndex.ToString(CultureInfo.InvariantCulture);
                    if (!_barText.TryGetValue(key, out var label))
                    {
                        label = MakeText(key, 10, RulerTextBrush);
                        _barText[key] = label;
                    }
                    dc.DrawText(label, new Point(rulerRight - label.Width - 6, y - label.Height / 2));
                    lastLabelY = y;
                }

                if (pen is not null) dc.DrawLine(pen, new Point(rulerRight - 8, y), new Point(rulerRight, y));
            }
            else if (line.Kind == BeatGrid.GridLineKind.Beat && pen is not null)
            {
                dc.DrawLine(pen, new Point(rulerRight - 4, y), new Point(rulerRight, y));
            }
        }

        // 时间文字（每秒一条，按缩放决定间隔）
        var stepMs = ChooseTimeLabelStep(state.PxPerMs);
        var firstLabel = Math.Ceiling(state.StartTime / stepMs) * stepMs;
        for (var t = firstLabel; t <= state.EndTime; t += stepMs)
        {
            var y = (t - state.StartTime) * state.PxPerMs;
            if (y < 8 || y > height - 8) continue;

            var key = t.ToString("0", CultureInfo.InvariantCulture);
            if (!_timeText.TryGetValue(key, out var text))
            {
                text = MakeText((t / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s", 9.5, RulerTextBrush);
                _timeText[key] = text;
            }
            dc.DrawText(text, new Point(rulerLeft + 4, y + 1));
        }

        // 标尺右边一条分隔线
        if (pen is not null)
        {
            dc.DrawLine(pen, new Point(rulerRight - 0.5, 0), new Point(rulerRight - 0.5, height));
        }
    }

    /// <summary>按当前缩放挑「时间标签」的间隔，保证相邻标签至少 34 像素。</summary>
    private static double ChooseTimeLabelStep(double pxPerMs)
    {
        var candidates = new double[] { 100, 200, 500, 1000, 2000, 5000, 10000, 30000, 60000 };
        foreach (var candidate in candidates)
        {
            if (candidate * pxPerMs >= 34) return candidate;
        }
        return candidates[^1];
    }

    // ---------------------------------------------------------------- 画笔缓存

    private Pen? GetPen(string key, Brush? brush, double thickness)
    {
        if (brush is null) return null;

        if (_pens.TryGetValue(key, out var pen)) return pen;

        pen = new Pen(brush, thickness);
        if (pen.CanFreeze) pen.Freeze();
        _pens[key] = pen;
        return pen;
    }

    private Brush? GetBrushFor(string key, Brush? source, double opacity)
    {
        if (source is null) return null;
        if (opacity >= 0.999) return source;

        if (_brushes.TryGetValue(key, out var cached)) return cached;

        // 复制一份改透明度：绝不能在原 brush 上改 Opacity（那是共享的主题 token）
        var clone = source.Clone();
        clone.Opacity = opacity;
        if (clone.CanFreeze) clone.Freeze();
        _brushes[key] = clone;
        return clone;
    }

    private FormattedText MakeText(string value, double size, Brush? brush)
    {
        return new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            LabelTypeface, size, brush ?? Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>主题画刷变了：丢掉缓存（下次绘制重建）。</summary>
    private void ClearCache()
    {
        _pens.Clear();
        _brushes.Clear();
        _timeText.Clear();
        _barText.Clear();
    }
}
