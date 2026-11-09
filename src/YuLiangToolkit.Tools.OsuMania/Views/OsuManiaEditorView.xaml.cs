using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.OsuMania.Core.Editor;
using YuLiangToolkit.Tools.OsuMania.ViewModels;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 编辑器页面后置代码：**只做「窗口 ↔ ViewModel」的接线**（鼠标坐标、快捷键、尺寸变化、全屏），
/// 业务逻辑全在 <see cref="ChartEditor"/>（Core，可无头自检）和 ViewModel 里。
///
/// <para><b>为什么要用后置代码而不是纯命令</b>：画布上的按下 / 拖动 / 松开要连续的坐标，
/// 还得在中途捕获鼠标；WPF 的 <c>ICommand</c> 不适合表达这种手势。这里把画布的原始坐标
/// 原样转给 ViewModel，由后者决定落到哪一列 / 哪一毫秒。</para>
///
/// <para><b>两个画布共用一套逻辑</b>：窗口内一个（<see cref="Canvas"/>）、全屏覆盖层里一个
/// （<see cref="FullscreenCanvas"/>）。它们连的是**同一个** <see cref="ChartEditor"/>，
/// 所以事件处理全部提取成 <c>Handle*</c> 方法，两边各自转发进来即可。</para>
/// </summary>
public partial class OsuManiaEditorView : UserControl
{
    private OsuManiaEditorViewModel? _vm;
    private ChartEditor? _wiredEditor;

    /// <summary>外壳（MainWindow 实现 <see cref="IFullscreenHost"/>）：全屏要靠它藏标题栏 / 侧边栏。</summary>
    private IFullscreenHost? _fullscreenHost;

    /// <summary>当前是不是全屏（本页自己的状态；外壳状态以 <see cref="_fullscreenHost"/> 为准）。</summary>
    private bool _fullscreen;

    public OsuManiaEditorView()
    {
        InitializeComponent();

        WireCanvas(Canvas);
        WireCanvas(FullscreenCanvas);

        SizeChanged += (_, _) => PublishViewportSize();
        FullscreenOverlay.SizeChanged += (_, _) => PublishViewportSize();
    }

    /// <summary>把一个画布的鼠标事件接到统一处理。</summary>
    private void WireCanvas(ChartCanvas canvas)
    {
        canvas.Pressed += point => HandlePressed(canvas, point);
        canvas.Dragged += point => HandleDragged(canvas, point);
        canvas.Released += point => HandleReleased(point);
        canvas.RightPressed += point => HandleRightPressed(point);
        canvas.ScrollDragged += deltaY => HandleScrollDragged(deltaY);
        canvas.WheelZoomed += (point, zoomIn) => HandleWheelZoomed(point, zoomIn);
        canvas.HoverMoved += point => HandleHover(canvas, point);
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 外壳会缓存工具视图：每次挂回来都要重新接线（Loaded 可能触发多次）
        AttachViewModel();
        HookHostKeys(true);
        PublishViewportSize();
        _vm?.BuildAndPublishState(ActiveCanvas.ActualHeight);

        // 自动保存计时从这里开始：视图没挂上之前（还在构造 / 切页中）不该算“用户无操作”
        _vm?.StartAutoSaveTimerFromView();
        HookActivityWatchers(true);

        // 截图 / 冒烟用：YLT_OSU_EDITOR_FULLSCREEN=1 直接以全屏覆盖层启动
        if (Environment.GetEnvironmentVariable("YLT_OSU_EDITOR_FULLSCREEN") is not null && !_fullscreen)
        {
            Dispatcher.BeginInvoke(new Action(() => _vm?.SetFullscreen(true)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HookHostKeys(false);
        HookActivityWatchers(false);

        // 视图要离开了：如果还在全屏，先把外壳还原（否则切走后侧边栏 / 标题栏还是藏着的）
        if (_fullscreen && _fullscreenHost is not null)
        {
            _fullscreenHost.SetFullscreen(false);
        }

        // 视图离开屏幕（切走工具页）：先把还没到点的改动落盘，再停计时。
        // 外壳切走时**不调 NotifyClosed**（视图进缓存），这里是最可靠的兜底时机。
        _vm?.AutoSaveFromView();
        _vm?.StopAutoSaveTimerFromView();
    }

    /// <summary>
    /// 把 ViewModel 接上（幂等）。**每次 Loaded 都调**：外壳缓存视图时 DataContext 不变，
    /// 但事件可能在上次 Unloaded 被摘掉。
    /// </summary>
    private void AttachViewModel()
    {
        var vm = DataContext as OsuManiaEditorViewModel;
        if (!ReferenceEquals(vm, _vm))
        {
            if (_vm is not null)
            {
                _vm.RedrawRequested -= OnRedrawRequested;
                _vm.ScrollToTimeRequested -= OnScrollToTimeRequested;
                _vm.FullscreenRequested -= OnFullscreenRequested;
            }

            _vm = vm;

            if (_vm is not null)
            {
                _vm.RedrawRequested += OnRedrawRequested;
                _vm.ScrollToTimeRequested += OnScrollToTimeRequested;
                _vm.FullscreenRequested += OnFullscreenRequested;
            }

            _wiredEditor = null;
        }

        _fullscreenHost = Window.GetWindow(this) as IFullscreenHost;

        // 换过谱面（Editor 实例变了）→ 把缩放 / 吸附等偏好同步过去
        if (_vm?.Editor is { } editor && !ReferenceEquals(editor, _wiredEditor))
        {
            _wiredEditor = editor;
            editor.GridDivisor = _vm.GridDivisor;
            editor.SnapToGrid = _vm.SnapToGrid;
            editor.Tool = _vm.Tool;
            editor.Viewport.SetScale(_vm.PxPerMs);
        }
    }

    private void OnRedrawRequested(object? sender, EventArgs e)
    {
        // 把最新快照挂到两个画布；同实例也重新赋值（快照每次都是新对象）
        var state = _vm?.RenderState;
        Canvas.State = state;
        FullscreenCanvas.State = state;

        // 只重绘屏幕上真正可见的那个（另一个刷新一下也不亏，但没必要每帧都画）
        ActiveCanvas.InvalidateVisual();
    }

    private void OnScrollToTimeRequested(object? sender, double timeMs)
    {
        if (_vm?.Editor is not { } editor) return;
        editor.CenterOnTime(timeMs);
        _vm.BuildAndPublishState(ActiveCanvas.ActualHeight);
    }

    /// <summary>当前正在用的画布（全屏时是覆盖层里那个）。</summary>
    private ChartCanvas ActiveCanvas => _fullscreen ? FullscreenCanvas : Canvas;

    /// <summary>把画布尺寸告诉 ViewModel（构建快照要用它算列宽与居中）。</summary>
    private void PublishViewportSize()
    {
        if (_vm is null) return;

        var canvas = ActiveCanvas;
        _vm.ChartCanvasWidth = Math.Max(ChartCanvas.RulerWidth + LaneLayout.MinLaneWidth, canvas.ActualWidth);
        _vm.Editor?.Viewport.Resize(Math.Max(1, canvas.ActualHeight));
    }

    // ---------------------------------------------------------------- 全屏

    private void OnFullscreenRequested(object? sender, bool on)
    {
        if (on == _fullscreen) return;

        _fullscreen = on;
        FullscreenOverlay.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // 外壳：藏标题栏 / 侧边栏 / 状态栏，窗口铺满整块显示器（含任务栏区域）
        _fullscreenHost?.SetFullscreen(on);

        _vm?.SyncFullscreenState(on);

        // 换画布后尺寸变了：等布局跑完再按真实高度重算
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PublishViewportSize();
            _vm?.BuildAndPublishState(ActiveCanvas.ActualHeight);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------------------------------------------------------------- 活动监听（自动保存用）

    /// <summary>
    /// 挂上「用户还在操作」的监听：**在页面根节点上接 Preview 事件**，
    /// 这样鼠标移动、滚轮、按键、按钮点击、滚动条拖动……全都会走这里，不用在每个处理器里
    /// 逐个调 <c>NoteActivity()</c>（漏一个就会在用户还在操作时自动保存）。
    /// </summary>
    private void HookActivityWatchers(bool attach)
    {
        if (attach)
        {
            if (_activityHooked) return;
            _activityHooked = true;

            PreviewMouseMove += OnActivityEvent;
            PreviewMouseDown += OnActivityEvent;
            PreviewMouseUp += OnActivityEvent;
            PreviewMouseWheel += OnActivityEvent;
            PreviewKeyDown += OnActivityEvent;
            PreviewKeyUp += OnActivityEvent;
            PreviewTouchMove += OnActivityTouch;
            PreviewStylusMove += OnActivityStylus;
            return;
        }

        if (!_activityHooked) return;
        _activityHooked = false;

        PreviewMouseMove -= OnActivityEvent;
        PreviewMouseDown -= OnActivityEvent;
        PreviewMouseUp -= OnActivityEvent;
        PreviewMouseWheel -= OnActivityEvent;
        PreviewKeyDown -= OnActivityEvent;
        PreviewKeyUp -= OnActivityEvent;
        PreviewTouchMove -= OnActivityTouch;
        PreviewStylusMove -= OnActivityStylus;
    }

    private bool _activityHooked;

    private void OnActivityEvent(object sender, InputEventArgs e)
    {
        // ⚠ 不要设 e.Handled：这里只观察，不能影响正常交互
        _vm?.NoteActivity();
    }

    private void OnActivityTouch(object sender, TouchEventArgs e) => _vm?.NoteActivity();

    private void OnActivityStylus(object sender, StylusEventArgs e) => _vm?.NoteActivity();

    // ---------------------------------------------------------------- 画布鼠标（两个画布共用）

    /// <summary>正在用鼠标拖播放头（拖左标尺）。</summary>
    private bool _scrubbingPlayhead;

    private void HandlePressed(ChartCanvas canvas, Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        // Ctrl 状态同步给编辑器（多选判定在 Core 里）
        editor.IsCtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        // 鼠标坐标要先用画布当前的居中布局换算（轨道可能没铺满，左边有留白）
        canvas.ApplyLayoutTo(editor);
        var time = editor.YToTime(point.Y);

        // 点左标尺 = 把播放头拖过去（对齐节奏的主要手势）
        // ⚠ 标尺不是从 0 开始的：「标尺 + 轨道」整块居中时它会被推到右边，所以按 LaneLeft 判断。
        if (point.X < canvas.LaneLeft)
        {
            if (editor.HitTest(point.X, point.Y).Kind == ChartEditor.HoverKind.TimingPoint) return;
            _vm.SeekTo(editor.Snap(time));
            _scrubbingPlayhead = true;
            return;
        }

        if (editor.PressAt(point.X, point.Y))
        {
            _vm.BuildAndPublishState(canvas.ActualHeight);
        }
    }

    /// <summary>
    /// 把画布的实际横向布局（标尺 + 居中的轨道区）同步给编辑器：
    /// 命中测试 / 列号换算都靠它，不同步就会出现「点左边却选中右边那列」。
    /// </summary>
    private static void ApplyCanvasLayout(ChartCanvas canvas, ChartEditor editor)
        => canvas.ApplyLayoutTo(editor);

    private void HandleDragged(ChartCanvas canvas, Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        canvas.ApplyLayoutTo(editor);

        if (_scrubbingPlayhead && point.X < canvas.LaneLeft)
        {
            _vm.SeekTo(editor.Snap(editor.YToTime(point.Y)));
            AutoScrollNearEdge(canvas, point.Y);
            return;
        }

        _scrubbingPlayhead = false;

        if (editor.DragTo(point.X, point.Y))
        {
            AutoScrollNearEdge(canvas, point.Y);
            _vm.BuildAndPublishState(canvas.ActualHeight);
        }
    }

    private void HandleReleased(Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        editor.Release();
        _vm.BuildAndPublishState(ActiveCanvas.ActualHeight);
    }

    private void HandleRightPressed(Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        ActiveCanvas.ApplyLayoutTo(editor);

        if (editor.DeleteAt(point.X, point.Y))
        {
            _vm.BuildAndPublishState(ActiveCanvas.ActualHeight);
        }
    }

    private void HandleScrollDragged(double deltaY)
    {
        if (_vm?.Editor is not { } editor) return;
        editor.Viewport.ScrollByPixels(deltaY);
        _vm.BuildAndPublishState(ActiveCanvas.ActualHeight);
        _vm.SyncZoomFromEditor();
    }

    private void HandleWheelZoomed(Point point, bool zoomIn)
    {
        if (_vm?.Editor is not { } editor) return;

        editor.Viewport.ZoomAt(point.Y, zoomIn ? 1.2 : 1 / 1.2);
        _vm.SyncZoomFromEditor();
        _vm.BuildAndPublishState(ActiveCanvas.ActualHeight);
    }

    private void HandleHover(ChartCanvas canvas, Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        var (kind, _, _) = editor.HitTest(point.X, point.Y);
        canvas.Cursor = kind switch
        {
            ChartEditor.HoverKind.NoteTail => Cursors.SizeNS,
            ChartEditor.HoverKind.NoteBody => Cursors.Hand,
            ChartEditor.HoverKind.TimingPoint => Cursors.SizeAll,
            _ => point.X < canvas.LaneLeft ? Cursors.SizeAll
                : (editor.Tool == ChartEditor.EditTool.Select ? Cursors.Arrow : Cursors.Cross),
        };
    }

    /// <summary>拖动到画布上下边缘时自动滚动时间轴（不然拖 LN 拖不出视野）。</summary>
    private void AutoScrollNearEdge(ChartCanvas canvas, double y)
    {
        if (_vm?.Editor is not { } editor) return;

        const double edge = 28;
        var height = canvas.ActualHeight;
        if (height <= edge * 2) return;

        if (y < edge)
        {
            editor.Viewport.ScrollByPixels(-(edge - y) * 0.9);
        }
        else if (y > height - edge)
        {
            editor.Viewport.ScrollByPixels((y - (height - edge)) * 0.9);
        }
    }

    private void OnTimingListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm is null) return;

        var list = sender as ListBox;
        if (list?.SelectedItem is not TimingPointRow row) return;

        _vm.SeekTo(row.Point.Time);
        _vm.BuildAndPublishState(ActiveCanvas.ActualHeight);
    }

    // ---------------------------------------------------------------- 拖入文件

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // 拖放回调由 OLE 直接调，异常必须自己兜住，否则会带崩程序
        try
        {
            if (_vm is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            // .osu → 打开谱面
            var chart = paths.FirstOrDefault(p => p.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            if (chart is not null)
            {
                _vm.LoadChart(chart);
                return;
            }

            // 图片 → 询问是否设为背景（用户明确要求：拖进 Note 浏览区就弹窗问）
            var inNoteArea = IsDropInsideLane(e);
            _vm.TryHandleBackgroundDrop(paths, inNoteArea);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[OsuManiaEditorView.OnDrop] {ex}");
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>拖放点是不是落在 Note 浏览区（轨道）上 —— 只用于提示文案，不阻止设置。</summary>
    private bool IsDropInsideLane(DragEventArgs e)
    {
        try
        {
            var point = e.GetPosition(ActiveCanvas);
            return point.X >= ActiveCanvas.LaneLeft &&
                   point.X <= ActiveCanvas.LaneLeft + ActiveCanvas.LaneActualWidth;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 快捷键（挂在宿主窗口上）

    private Window? _host;

    private void HookHostKeys(bool attach)
    {
        var window = Window.GetWindow(this);
        if (attach)
        {
            if (ReferenceEquals(window, _host)) return;
            UnhookHostKeys();
            _host = window;
            if (_host is not null) _host.PreviewKeyDown += OnHostPreviewKeyDown;
        }
        else
        {
            UnhookHostKeys();
        }
    }

    private void UnhookHostKeys()
    {
        if (_host is not null) _host.PreviewKeyDown -= OnHostPreviewKeyDown;
        _host = null;
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        // 焦点在文本输入框里时不要抢按键（否则输入框里打字会被当成快捷键）
        if (Keyboard.FocusedElement is TextBox) return;

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            // ---- 播放 ----
            case Key.Space when !ctrl:
                _vm.TogglePlay();
                e.Handled = true;
                break;
            case Key.Escape:
                if (_fullscreen) _vm.SetFullscreen(false);
                else _vm.StopPlayback();
                e.Handled = true;
                break;

            // ---- 文件 ----
            case Key.S when ctrl:
                _vm.SaveChart();
                e.Handled = true;
                break;
            case Key.O when ctrl:
                _vm.OpenChartCommand.Execute(null);
                e.Handled = true;
                break;

            // ---- 撤销 / 重做 ----
            case Key.Z when ctrl:
                if (shift) _vm.DoRedo();
                else _vm.DoUndo();
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                _vm.DoRedo();
                e.Handled = true;
                break;
            case Key.A when ctrl:
                _vm.SelectAllCommand.Execute(null);
                e.Handled = true;
                break;

            // ---- 编辑 ----
            case Key.Delete:
                _vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Q:
                _vm.SnapToGrid = !_vm.SnapToGrid;
                e.Handled = true;
                break;

            // ---- 工具切换（1 选择 / 2 单点 / 3 长条） ----
            case Key.D1:
                _vm.Tool = ChartEditor.EditTool.Select;
                e.Handled = true;
                break;
            case Key.D2:
                _vm.Tool = ChartEditor.EditTool.Note;
                e.Handled = true;
                break;
            case Key.D3:
                _vm.Tool = ChartEditor.EditTool.LongNote;
                e.Handled = true;
                break;

            // ---- osu! 惯用：Z = 第一个 note，V = 最后一个 note ----
            // ⚠ 它们在 osu! 里是「往复选区间」的快捷键；这里扩展成「跳转 + 把区间内同轨的 note 加进选择」。
            // Ctrl 时不触发（避免和 Ctrl+Z 之类冲突）。
            case Key.Z when !ctrl:
                _vm.JumpToFirstNote(alsoExtendSelection: true);
                e.Handled = true;
                break;
            case Key.V when !ctrl:
                _vm.JumpToLastNote(alsoExtendSelection: true);
                e.Handled = true;
                break;

            // ---- 微调播放头 ----
            case Key.Left when !ctrl:
                _vm.Nudge(-10);
                e.Handled = true;
                break;
            case Key.Right when !ctrl:
                _vm.Nudge(10);
                e.Handled = true;
                break;
            case Key.Left when ctrl:
                _vm.Nudge(-1);
                e.Handled = true;
                break;
            case Key.Right when ctrl:
                _vm.Nudge(1);
                e.Handled = true;
                break;

            // ---- 全屏 ----
            case Key.F11:
                _vm.ToggleFullscreen();
                e.Handled = true;
                break;
            case Key.F when ctrl && shift:
                _vm.ToggleFullscreen();
                e.Handled = true;
                break;
        }
    }
}
