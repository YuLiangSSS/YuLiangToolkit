using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.OsuMania.Core.Editor;
using YuLiangToolkit.Tools.OsuMania.ViewModels;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 编辑器页面后置代码：**只做「窗口 ↔ ViewModel」的接线**（鼠标坐标、快捷键、尺寸变化），
/// 业务逻辑全在 <see cref="ChartEditor"/>（Core，可无头自检）和 ViewModel 里。
///
/// <para><b>为什么要用后置代码而不是纯命令</b>：画布上的按下 / 拖动 / 松开要连续的坐标，
/// 还得在中途捕获鼠标；WPF 的 <c>ICommand</c> 不适合表达这种手势。这里把画布的原始坐标
/// 原样转给 ViewModel，由后者决定落到哪一列 / 哪一毫秒。</para>
/// </summary>
public partial class OsuManiaEditorView : UserControl
{
    private OsuManiaEditorViewModel? _vm;
    private bool _wired;
    private ChartEditor? _wiredEditor;

    public OsuManiaEditorView()
    {
        InitializeComponent();

        Canvas.Pressed += OnCanvasPressed;
        Canvas.Dragged += OnCanvasDragged;
        Canvas.Released += OnCanvasReleased;
        Canvas.RightPressed += OnCanvasRightPressed;
        Canvas.ScrollDragged += OnCanvasScrollDragged;
        Canvas.WheelZoomed += OnCanvasWheelZoomed;
        Canvas.HoverMoved += OnCanvasHover;

        SizeChanged += (_, _) => PublishViewportSize();
    }

    // ---------------------------------------------------------------- 生命周期

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 外壳会缓存工具视图：每次挂回来都要重新接线（Loaded 可能触发多次）
        AttachViewModel();
        HookHostKeys(true);
        PublishViewportSize();
        _vm?.BuildAndPublishState(Canvas.ActualHeight);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HookHostKeys(false);
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
            }

            _vm = vm;

            if (_vm is not null)
            {
                _vm.RedrawRequested += OnRedrawRequested;
                _vm.ScrollToTimeRequested += OnScrollToTimeRequested;
            }

            _wiredEditor = null;
        }

        // 换过谱面（Editor 实例变了）→ 把缩放 / 吸附等偏好同步过去
        if (_vm?.Editor is { } editor && !ReferenceEquals(editor, _wiredEditor))
        {
            _wiredEditor = editor;
            editor.GridDivisor = _vm.GridDivisor;
            editor.SnapToGrid = _vm.SnapToGrid;
            editor.Tool = _vm.LongNoteTool ? ChartEditor.EditTool.LongNote : ChartEditor.EditTool.Note;
            editor.Viewport.SetScale(_vm.PxPerMs);
        }

        _wired = _vm is not null;
        _ = _wired;
    }

    private void OnRedrawRequested(object? sender, EventArgs e)
    {
        if (Canvas is null) return;

        // 把最新快照挂到画布；同实例也重新赋值（WPF 会按值比较，但快照每次都是新对象）
        Canvas.State = _vm?.RenderState;
        Canvas.InvalidateVisual();
    }

    private void OnScrollToTimeRequested(object? sender, double timeMs)
    {
        if (_vm?.Editor is not { } editor) return;
        editor.CenterOnTime(timeMs);
        _vm.BuildAndPublishState(Canvas.ActualHeight);
    }

    /// <summary>把画布宽度告诉 ViewModel（构建快照要用它算列宽）。</summary>
    private void PublishViewportSize()
    {
        if (_vm is null) return;

        var laneWidth = Math.Max(40, Canvas.ActualWidth - ChartCanvas.RulerWidth);
        _vm.ChartCanvasLaneWidth = laneWidth;
        _vm.Editor?.Viewport.Resize(Math.Max(1, Canvas.ActualHeight));
    }

    // ---------------------------------------------------------------- 画布鼠标

    private void OnCanvasPressed(Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        var time = editor.YToTime(point.Y);

        // 点左标尺 = 把播放头拖过去（对齐节奏的主要手势）
        if (point.X < ChartCanvas.RulerWidth)
        {
            if (editor.HitTest(point.X, point.Y).Kind == ChartEditor.HoverKind.TimingPoint) return;
            _vm.SeekTo(editor.Snap(time));
            _scrubbingPlayhead = true;
            return;
        }

        if (editor.PressAt(point.X, point.Y))
        {
            _vm.BuildAndPublishState(Canvas.ActualHeight);
        }
    }

    private bool _scrubbingPlayhead;

    private void OnCanvasDragged(Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        if (_scrubbingPlayhead && point.X < ChartCanvas.RulerWidth * 1.5)
        {
            _vm.SeekTo(editor.Snap(editor.YToTime(point.Y)));
            AutoScrollNearEdge(point.Y);
            return;
        }

        _scrubbingPlayhead = false;

        if (editor.DragTo(point.X, point.Y))
        {
            AutoScrollNearEdge(point.Y);
            _vm.BuildAndPublishState(Canvas.ActualHeight);
        }
    }

    private void OnCanvasReleased(Point point)
    {
        _scrubbingPlayhead = false;
        if (_vm?.Editor is not { } editor) return;

        editor.Release();
        _vm.BuildAndPublishState(Canvas.ActualHeight);
    }

    private void OnCanvasRightPressed(Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        if (editor.DeleteAt(point.X, point.Y))
        {
            _vm.BuildAndPublishState(Canvas.ActualHeight);
        }
    }

    private void OnCanvasScrollDragged(double deltaY)
    {
        if (_vm?.Editor is not { } editor) return;
        editor.Viewport.ScrollByPixels(deltaY);
        _vm.BuildAndPublishState(Canvas.ActualHeight);
    }

    private void OnCanvasWheelZoomed(Point point, bool zoomIn)
    {
        if (_vm?.Editor is not { } editor) return;

        editor.Viewport.ZoomAt(point.Y, zoomIn ? 1.2 : 1 / 1.2);
        _vm.SyncZoomFromEditor();
        _vm.BuildAndPublishState(Canvas.ActualHeight);
    }

    private void OnCanvasHover(Point point)
    {
        if (_vm?.Editor is not { } editor) return;

        var (kind, noteIndex, _) = editor.HitTest(point.X, point.Y);
        Canvas.Cursor = kind switch
        {
            ChartEditor.HoverKind.NoteTail => Cursors.SizeNS,
            ChartEditor.HoverKind.NoteBody => Cursors.Hand,
            ChartEditor.HoverKind.TimingPoint => Cursors.SizeAll,
            _ => point.X < ChartCanvas.RulerWidth ? Cursors.SizeAll : Cursors.Cross,
        };

        // 悬停同时把光标所在位置的时间显示到状态栏（对齐时很好用）
        _ = noteIndex;
    }

    /// <summary>拖动到画布上下边缘时自动滚动时间轴（不然拖 LN 拖不出视野）。</summary>
    private void AutoScrollNearEdge(double y)
    {
        if (_vm?.Editor is not { } editor) return;

        const double edge = 28;
        var height = Canvas.ActualHeight;
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
        if (_vm is null || TimingList.SelectedItem is not TimingPointRow row) return;
        _vm.SeekTo(row.Point.Time);
        _vm.BuildAndPublishState(Canvas.ActualHeight);
    }

    // ---------------------------------------------------------------- 拖入文件

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        // 拖放回调由 OLE 直接调，异常必须自己兜住，否则会带崩程序
        try
        {
            if (_vm is null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            var chart = paths.FirstOrDefault(p => p.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            if (chart is not null)
            {
                _vm.LoadChart(chart);
                return;
            }
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
            case Key.Space when !ctrl:
                _vm.TogglePlay();
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.StopPlayback();
                e.Handled = true;
                break;
            case Key.S when ctrl:
                _vm.SaveChart();
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                if (shift) _vm.DoRedo();
                else _vm.DoUndo();
                e.Handled = true;
                break;
            case Key.Y when ctrl:
                _vm.DoRedo();
                e.Handled = true;
                break;
            case Key.Delete:
                _vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D1:
                _vm.LongNoteTool = false;
                e.Handled = true;
                break;
            case Key.D2:
                _vm.LongNoteTool = true;
                e.Handled = true;
                break;
            case Key.Q:
                _vm.SnapToGrid = !_vm.SnapToGrid;
                e.Handled = true;
                break;
            case Key.Left when !ctrl:
                _vm.Nudge(-10);
                e.Handled = true;
                break;
            case Key.Right when !ctrl:
                _vm.Nudge(10);
                e.Handled = true;
                break;
        }
    }
}
