using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace YuLiangToolkit.Tools.Screenshot.Views;

/// <summary>截图完成后的动作。</summary>
public enum ScreenshotAction
{
    /// <summary>复制到剪贴板。</summary>
    Copy,
    /// <summary>保存为文件。</summary>
    Save,
    /// <summary>贴到桌面。</summary>
    Pin,
}

/// <summary>覆盖层关闭后交给 ViewModel 的结果。</summary>
public sealed record ScreenshotOverlayResult(
    BitmapSource Image,
    ScreenshotPixelRect PhysicalRegion,
    ScreenshotAction Action);

/// <summary>
/// 全屏截图覆盖层：先冻结整块桌面，再在冻结图上框选和标注。
/// 
/// <para>键盘：Enter / Ctrl+C 复制，Ctrl+S 保存，P 贴图，Esc 取消；Ctrl+Z / Ctrl+Y 撤销重做。</para>
/// <para>鼠标：拖动框选；选择工具时可拖动 / 拉八个控制点；选标注工具后在选区内画。</para>
/// </summary>
public partial class ScreenshotOverlayWindow : Window
{
    private enum DragMode
    {
        None,
        CreateSelection,
        DrawShape,
        DrawPen,
        Move,
        ResizeN,
        ResizeS,
        ResizeE,
        ResizeW,
        ResizeNw,
        ResizeNe,
        ResizeSw,
        ResizeSe,
    }

    private const double MinSelectionSize = 2;

    private readonly CapturedDesktop _desktop;
    private readonly ScreenshotConfig _config;
    private readonly List<ScreenshotAnnotation> _annotations = new();
    private readonly Stack<List<ScreenshotAnnotation>> _undo = new();
    private readonly Stack<List<ScreenshotAnnotation>> _redo = new();
    private readonly List<ToggleButton> _toolButtons = new();
    private readonly List<Button> _colorButtons = new();
    private readonly DispatcherTimer _toastTimer;

    private Rect _selection = Rect.Empty;
    private Rect? _initialSelection;
    private Point _dragStart;
    private Rect _dragStartSelection;
    private DragMode _dragMode;
    private bool _mouseDown;
    private Point _lastMouse;

    private ScreenshotDrawTool _tool = ScreenshotDrawTool.Select;
    private Color _color;
    private double _thickness;
    private bool _filled;

    private ScreenshotAnnotation? _draft;
    private List<Point>? _penPoints;
    private int _nextNumber = 1;

    private bool _textEditorActive;
    private Point _textAnchor;

    private bool _finished;

    /// <summary>构造：传入冻结桌面图和默认配置；<paramref name="initialSelectionDip"/> 用于“重复上次区域”。</summary>
    public ScreenshotOverlayWindow(CapturedDesktop desktop, ScreenshotConfig config, Rect? initialSelectionDip = null)
    {
        _desktop = desktop;
        _config = config;
        _color = ScreenshotColor.Parse(config.DefaultColor);
        _thickness = Math.Clamp(config.DefaultThickness, 1, 24);
        _filled = config.DefaultFill;
        _initialSelection = initialSelectionDip;

        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1, SystemParameters.VirtualScreenHeight);

        Loaded += OnLoaded;
        Closed += OnClosed;
        _toastTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2.5) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); ToastPanel.Visibility = Visibility.Collapsed; };
        BuildToolButtons();
        BuildColorButtons();
        BuildThicknessChoices();
        SelectTool(ParseTool(config.DefaultTool));
        FillCheck.IsChecked = _filled;
        Mouse.OverrideCursor = Cursors.Cross;
    }

    /// <summary>用户确认后的结果；取消时为 null。</summary>
    public ScreenshotOverlayResult? Result { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Backdrop.Source = _desktop.Image;
        Root.Width = Width;
        Root.Height = Height;
        SelectionCanvas.Width = ActualWidth;
        SelectionCanvas.Height = ActualHeight;
        AnnotationCanvas.Width = ActualWidth;
        AnnotationCanvas.Height = ActualHeight;
        HandleCanvas.Width = ActualWidth;
        HandleCanvas.Height = ActualHeight;
        ChromeCanvas.Width = ActualWidth;
        ChromeCanvas.Height = ActualHeight;

        if (_initialSelection is { } initial && ScreenshotGeometry.IsUsable(initial))
            SetSelection(ScreenshotGeometry.ClampToCanvas(initial, ActualWidth, ActualHeight));
        else
            RefreshSelectionVisual();

        Activate();
        Focus();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Mouse.OverrideCursor = null;
        if (!_finished && Result is null) { /* 用户按关闭 = 取消 */ }
    }

    // ================================================================== 工具栏

    private void BuildToolButtons()
    {
        AddTool(ScreenshotDrawTool.Select, "↖", "选择 / 移动框选");
        AddTool(ScreenshotDrawTool.Rectangle, "▭", "矩形");
        AddTool(ScreenshotDrawTool.Ellipse, "◯", "椭圆");
        AddTool(ScreenshotDrawTool.Arrow, "↗", "箭头");
        AddTool(ScreenshotDrawTool.Line, "／", "直线");
        AddTool(ScreenshotDrawTool.Pen, "✎", "画笔");
        AddTool(ScreenshotDrawTool.Highlighter, "🖍", "荧光笔");
        AddTool(ScreenshotDrawTool.Text, "T", "文字");
        AddTool(ScreenshotDrawTool.Number, "①", "序号");
        AddTool(ScreenshotDrawTool.Mosaic, "▦", "马赛克");
        AddTool(ScreenshotDrawTool.Blur, "◒", "模糊");
        AddTool(ScreenshotDrawTool.PickColor, "🎨", "取色（点击复制颜色）");
        AddTool(ScreenshotDrawTool.Eraser, "⌫", "橡皮擦（点击删除标注）");

        var ocr = new Button
        {
            Content = "文",
            ToolTip = "OCR 文字识别（复制选区文字）",
            Style = (Style)Resources["OverlayActionButtonStyle"],
            Height = 29,
            Margin = new Thickness(4, 2, 2, 2),
            Padding = new Thickness(9, 0, 9, 0),
        };
        ocr.Click += OnOcrClick;
        ToolButtonsHost.Children.Add(ocr);
    }

    private void AddTool(ScreenshotDrawTool tool, string glyph, string tip)
    {
        var button = new ToggleButton
        {
            Content = glyph,
            ToolTip = tip,
            Tag = tool,
            Style = (Style)Resources["OverlayToolButtonStyle"],
            IsChecked = tool == _tool,
        };
        button.Click += OnToolButtonClick;
        _toolButtons.Add(button);
        ToolButtonsHost.Children.Add(button);
    }

    private void BuildColorButtons()
    {
        foreach (var color in ScreenshotColor.Palette)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(color),
                BorderBrush = Brushes.Transparent,
                Tag = color,
                ToolTip = ScreenshotColor.ToHex(color),
                Style = (Style)Resources["OverlayColorButtonStyle"],
            };
            button.Click += OnColorButtonClick;
            _colorButtons.Add(button);
            ColorPaletteHost.Children.Add(button);
        }
        RefreshColorButtons();
    }

    private void BuildThicknessChoices()
    {
        ThicknessCombo.ItemsSource = new[] { 1.0, 2.0, 3.0, 4.0, 6.0, 8.0, 12.0 };
        ThicknessCombo.SelectedItem = _thickness;
    }

    private void OnToolButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: ScreenshotDrawTool tool }) SelectTool(tool);
    }

    private void SelectTool(ScreenshotDrawTool tool)
    {
        _tool = tool;
        foreach (var button in _toolButtons)
        {
            button.IsChecked = button.Tag is ScreenshotDrawTool candidate && candidate == tool;
        }

        if (tool is ScreenshotDrawTool.Text)
            Cursor = Cursors.IBeam;
        else if (tool is ScreenshotDrawTool.Pen or ScreenshotDrawTool.Highlighter)
            Cursor = Cursors.Pen;
        else
            Cursor = Cursors.Cross;

        UpdateSelectionVisual();
    }

    private void UpdateSelectionVisual() => RefreshSelectionVisual();

    private void OnColorButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Color color })
        {
            _color = color;
            RefreshColorButtons();
        }
    }

    private void OnPaletteClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ScreenshotPaletteWindow(ScreenshotColor.ExtendedPalette, _color)
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (dialog.ShowDialog() == true)
        {
            _color = dialog.SelectedColor;
            RefreshColorButtons();
            ShowToast("已选择颜色 " + ScreenshotColor.ToHex(_color));
        }
    }

    private void RefreshColorButtons()
    {
        foreach (var button in _colorButtons)
        {
            var selected = button.Tag is Color color && color == _color;
            button.BorderBrush = selected ? Brushes.White : Brushes.Transparent;
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(0);
        }
    }

    private void OnThicknessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThicknessCombo.SelectedItem is double value) _thickness = Math.Clamp(value, 1, 24);
    }

    private void OnFillChanged(object sender, RoutedEventArgs e)
        => _filled = FillCheck.IsChecked == true;

    private static ScreenshotDrawTool ParseTool(string? value)
        => Enum.TryParse<ScreenshotDrawTool>(value, true, out var tool) ? tool : ScreenshotDrawTool.Select;

    // ================================================================== 选择框

    private void SetSelection(Rect selection)
    {
        _selection = ScreenshotGeometry.IsUsable(selection)
            ? ScreenshotGeometry.ClampToCanvas(selection, ActualWidth, ActualHeight)
            : Rect.Empty;

        _annotations.Clear();
        _draft = null;
        _undo.Clear();
        _redo.Clear();
        _nextNumber = 1;
        CancelTextEditor();
        RefreshSelectionVisual();
        RenderAnnotations();
    }

    private void RefreshSelectionVisual()
    {
        var width = Math.Max(0, ActualWidth);
        var height = Math.Max(0, ActualHeight);
        var full = new Rect(0, 0, width, height);
        var hasSelection = ScreenshotGeometry.IsUsable(_selection);

        DimPath.Data = hasSelection
            ? new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(_selection))
            : new RectangleGeometry(full);

        SelectionRect.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (hasSelection)
        {
            SelectionRect.Width = _selection.Width;
            SelectionRect.Height = _selection.Height;
            Canvas.SetLeft(SelectionRect, _selection.Left);
            Canvas.SetTop(SelectionRect, _selection.Top);
        }

        AnnotationCanvas.Clip = hasSelection ? new RectangleGeometry(_selection) : null;
        HintPanel.Visibility = !hasSelection && _config.ShowCursorHint ? Visibility.Visible : Visibility.Collapsed;
        PositionHint();
        Toolbar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (hasSelection) PositionToolbar();
        UpdateSizeBadge();
        DrawHandles();
    }

    private void UpdateSizeBadge()
    {
        var hasSelection = ScreenshotGeometry.IsUsable(_selection);
        if (!hasSelection || !_config.ShowSizeBadge)
        {
            SizeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var physical = MapSelectionToPixels();
        SizeText.Text = physical.IsEmpty ? "0 × 0" : $"{physical.Width} × {physical.Height}";
        SizeBadge.Visibility = Visibility.Visible;
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = SizeBadge.DesiredSize.Width;
        var h = SizeBadge.DesiredSize.Height;
        var x = Math.Clamp(_selection.Right - w, 4, Math.Max(4, ActualWidth - w - 4));
        var y = _selection.Bottom + 7;
        if (y + h > ActualHeight - 4) y = Math.Max(4, _selection.Top - h - 7);
        Canvas.SetLeft(SizeBadge, x);
        Canvas.SetTop(SizeBadge, y);
    }

    private void PositionToolbar()
    {
        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Toolbar.DesiredSize;
        var x = Math.Clamp(_selection.Right - size.Width, 6, Math.Max(6, ActualWidth - size.Width - 6));
        var y = _selection.Top - size.Height - 10;
        if (y < 6) y = Math.Min(Math.Max(6, ActualHeight - size.Height - 6), _selection.Bottom + 10);
        Canvas.SetLeft(Toolbar, x);
        Canvas.SetTop(Toolbar, y);
    }

    private void PositionHint()
    {
        if (HintPanel.Visibility != Visibility.Visible) return;
        HintPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = HintPanel.DesiredSize.Width;
        Canvas.SetLeft(HintPanel, Math.Max(6, (ActualWidth - w) / 2));
        Canvas.SetTop(HintPanel, 18);
    }

    private void DrawHandles()
    {
        HandleCanvas.Children.Clear();
        // 有标注后锁定选区，避免坐标换算和用户预期不一致；重新框选会清空标注。
        if (!ScreenshotGeometry.IsUsable(_selection) || _annotations.Count > 0) return;

        foreach (var (_, point) in GetHandleCenters())
        {
            var handle = new Rectangle
            {
                Width = 7,
                Height = 7,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x33, 0x3A, 0x46)),
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(handle, point.X - 3.5);
            Canvas.SetTop(handle, point.Y - 3.5);
            HandleCanvas.Children.Add(handle);
        }
    }

    private IReadOnlyList<(DragMode Mode, Point Point)> GetHandleCenters()
    {
        var s = _selection;
        var cx = s.X + s.Width / 2;
        var cy = s.Y + s.Height / 2;
        return new[]
        {
            (DragMode.ResizeNw, new Point(s.Left, s.Top)),
            (DragMode.ResizeN, new Point(cx, s.Top)),
            (DragMode.ResizeNe, new Point(s.Right, s.Top)),
            (DragMode.ResizeE, new Point(s.Right, cy)),
            (DragMode.ResizeSe, new Point(s.Right, s.Bottom)),
            (DragMode.ResizeS, new Point(cx, s.Bottom)),
            (DragMode.ResizeSw, new Point(s.Left, s.Bottom)),
            (DragMode.ResizeW, new Point(s.Left, cy)),
        };
    }

    private DragMode HitTestHandle(Point p)
    {
        if (!ScreenshotGeometry.IsUsable(_selection) || _annotations.Count > 0) return DragMode.None;
        foreach (var (mode, point) in GetHandleCenters())
        {
            if (Math.Abs(p.X - point.X) <= 6 && Math.Abs(p.Y - point.Y) <= 6) return mode;
        }
        return DragMode.None;
    }

    // ================================================================== 鼠标

    private bool IsInsideChrome(DependencyObject? source)
    {
        if (source is null) return false;
        return IsDescendantOf(source, Toolbar) || IsDescendantOf(source, TextEditorHost);
    }

    private static bool IsDescendantOf(DependencyObject source, DependencyObject ancestor)
    {
        try
        {
            for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, ancestor)) return true;
        }
        catch { /* 非 Visual 元素 */ }
        return false;
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideChrome(e.OriginalSource as DependencyObject)) return;

        var p = e.GetPosition(Root);
        _lastMouse = p;
        _mouseDown = true;

        if (e.ClickCount == 2 && ScreenshotGeometry.IsUsable(_selection) && _selection.Contains(p))
        {
            Finish(ScreenshotAction.Copy);
            e.Handled = true;
            return;
        }

        if (TextEditorHost.Visibility == Visibility.Visible)
            CommitTextEditor();

        if (!ScreenshotGeometry.IsUsable(_selection))
        {
            BeginCreate(p);
            e.Handled = true;
            return;
        }

        var handle = HitTestHandle(p);
        if (handle != DragMode.None)
        {
            _dragMode = handle;
            _dragStart = p;
            _dragStartSelection = _selection;
            CaptureMouse();
            e.Handled = true;
            return;
        }

        if (_tool == ScreenshotDrawTool.Select)
        {
            if (_selection.Contains(p) && _annotations.Count == 0)
            {
                _dragMode = DragMode.Move;
                _dragStart = p;
                _dragStartSelection = _selection;
                CaptureMouse();
            }
            else if (!_selection.Contains(p) && _annotations.Count == 0)
            {
                BeginCreate(p);
            }
            e.Handled = true;
            return;
        }

        if (!_selection.Contains(p)) return;

        switch (_tool)
        {
            case ScreenshotDrawTool.Text:
                _mouseDown = false;
                StartTextEditor(p);
                break;
            case ScreenshotDrawTool.Number:
                AddAnnotation(ScreenshotAnnotation.NumberAt(p, _nextNumber++, _color, Math.Max(20, _thickness * 8)));
                break;
            case ScreenshotDrawTool.PickColor:
                PickColorAt(p);
                break;
            case ScreenshotDrawTool.Eraser:
                EraseAt(p);
                break;
            case ScreenshotDrawTool.Rectangle:
            case ScreenshotDrawTool.Ellipse:
            case ScreenshotDrawTool.Arrow:
            case ScreenshotDrawTool.Line:
            case ScreenshotDrawTool.Mosaic:
            case ScreenshotDrawTool.Blur:
                _dragMode = DragMode.DrawShape;
                _mouseDown = true;
                _dragStart = p;
                _draft = ScreenshotAnnotation.FromTwoPoints(_tool, p, p, _color, _thickness, _filled);
                RenderAnnotations();
                CaptureMouse();
                break;
            case ScreenshotDrawTool.Pen:
            case ScreenshotDrawTool.Highlighter:
                _dragMode = DragMode.DrawPen;
                _dragStart = p;
                _penPoints = new List<Point> { p };
                _draft = ScreenshotAnnotation.FromPoints(_tool, _penPoints, _color, _thickness);
                RenderAnnotations();
                CaptureMouse();
                break;
        }
        e.Handled = true;
    }

    private void BeginCreate(Point p)
    {
        _dragMode = DragMode.CreateSelection;
        _dragStart = p;
        _selection = Rect.Empty;
        _annotations.Clear();
        _draft = null;
        _undo.Clear();
        _redo.Clear();
        _nextNumber = 1;
        RefreshSelectionVisual();
        CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_finished) return;
        var p = e.GetPosition(Root);
        _lastMouse = p;

        if (_mouseDown && _dragMode != DragMode.None)
        {
            switch (_dragMode)
            {
                case DragMode.DrawShape:
                    UpdateDraft(p);
                    break;
                case DragMode.DrawPen:
                    _penPoints?.Add(p);
                    _draft = ScreenshotAnnotation.FromPoints(_tool, _penPoints ?? new List<Point>(), _color, _thickness);
                    RenderAnnotations();
                    break;
                case DragMode.CreateSelection:
                    _selection = ScreenshotGeometry.ClampToCanvas(
                        RectFromPoints(_dragStart, p), ActualWidth, ActualHeight);
                    RefreshSelectionVisual();
                    break;
                case DragMode.Move:
                    MoveSelection(p);
                    break;
                case DragMode.ResizeN:
                case DragMode.ResizeS:
                case DragMode.ResizeE:
                case DragMode.ResizeW:
                case DragMode.ResizeNw:
                case DragMode.ResizeNe:
                case DragMode.ResizeSw:
                case DragMode.ResizeSe:
                    ResizeSelection(p);
                    break;
            }
            e.Handled = true;
            return;
        }

        UpdateMagnifier(p);
        UpdateCursor(p);
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown)
            return;

        _mouseDown = false;
        var wasMode = _dragMode;
        _dragMode = DragMode.None;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (wasMode is DragMode.DrawShape or DragMode.DrawPen)
        {
            CommitDraft();
            e.Handled = true;
            return;
        }

        if (ScreenshotGeometry.IsUsable(_selection))
        {
            if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
                _selection = Rect.Empty;
        }

        HideTextEditorIfNeeded();
        RefreshSelectionVisual();
        e.Handled = true;
    }

    private void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInsideChrome(e.OriginalSource as DependencyObject)) return;
        if (TextEditorHost.Visibility == Visibility.Visible) { CancelTextEditor(); e.Handled = true; return; }
        Cancel();
        e.Handled = true;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl + 滚轮快速调线宽（不改变选区）。
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        _thickness = Math.Clamp(_thickness + (e.Delta > 0 ? 1 : -1), 1, 24);
        ThicknessCombo.SelectedItem = _thickness;
        e.Handled = true;
    }

    private void UpdateCursor(Point p)
    {
        if (_tool != ScreenshotDrawTool.Select)
        {
            Cursor = _tool switch
            {
                ScreenshotDrawTool.Text => Cursors.IBeam,
                ScreenshotDrawTool.Pen or ScreenshotDrawTool.Highlighter => Cursors.Pen,
                _ => Cursors.Cross,
            };
            return;
        }

        var handle = HitTestHandle(p);
        Cursor = handle switch
        {
            DragMode.ResizeN or DragMode.ResizeS => Cursors.SizeNS,
            DragMode.ResizeE or DragMode.ResizeW => Cursors.SizeWE,
            DragMode.ResizeNw or DragMode.ResizeSe => Cursors.SizeNWSE,
            DragMode.ResizeNe or DragMode.ResizeSw => Cursors.SizeNESW,
            _ => ScreenshotGeometry.IsUsable(_selection) && _selection.Contains(p) ? Cursors.SizeAll : Cursors.Cross,
        };
    }

    private void MoveSelection(Point p)
    {
        var dx = p.X - _dragStart.X;
        var dy = p.Y - _dragStart.Y;
        var moved = new Rect(_dragStartSelection.X + dx, _dragStartSelection.Y + dy,
            _dragStartSelection.Width, _dragStartSelection.Height);
        _selection = ScreenshotGeometry.ClampToCanvas(moved, ActualWidth, ActualHeight);
        RefreshSelectionVisual();
    }

    private void ResizeSelection(Point p)
    {
        var s = _dragStartSelection;
        var left = s.Left;
        var top = s.Top;
        var right = s.Right;
        var bottom = s.Bottom;

        switch (_dragMode)
        {
            case DragMode.ResizeN: top = p.Y; break;
            case DragMode.ResizeS: bottom = p.Y; break;
            case DragMode.ResizeW: left = p.X; break;
            case DragMode.ResizeE: right = p.X; break;
            case DragMode.ResizeNw: left = p.X; top = p.Y; break;
            case DragMode.ResizeNe: right = p.X; top = p.Y; break;
            case DragMode.ResizeSw: left = p.X; bottom = p.Y; break;
            case DragMode.ResizeSe: right = p.X; bottom = p.Y; break;
        }

        _selection = ScreenshotGeometry.ClampToCanvas(RectFromPoints(new Point(left, top), new Point(right, bottom)),
            ActualWidth, ActualHeight);
        RefreshSelectionVisual();
    }

    private static Rect RectFromPoints(Point a, Point b)
        => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private ScreenshotPixelRect MapSelectionToPixels()
        => ScreenshotGeometry.MapSelection(_selection, ActualWidth, ActualHeight, _desktop.Bounds);

    // ================================================================== 标注

    private void UpdateDraft(Point p)
    {
        if (_draft is null) return;
        if (_draft.Tool is ScreenshotDrawTool.Pen or ScreenshotDrawTool.Highlighter)
            return;

        _draft = ScreenshotAnnotation.FromTwoPoints(_tool, _dragStart, p, _color, _thickness, _filled);
        RenderAnnotations();
    }

    private void CommitDraft()
    {
        if (_draft is null) return;
        var draft = _draft;
        _draft = null;
        _penPoints = null;

        if (draft.Tool is ScreenshotDrawTool.Rectangle or ScreenshotDrawTool.Ellipse
            or ScreenshotDrawTool.Arrow or ScreenshotDrawTool.Line
            or ScreenshotDrawTool.Mosaic or ScreenshotDrawTool.Blur)
        {
            if (draft.Bounds.Width < 3 && draft.Bounds.Height < 3) { RenderAnnotations(); return; }
        }
        else if (draft.Points.Count < 2) { RenderAnnotations(); return; }

        AddAnnotation(draft);
    }

    private void AddAnnotation(ScreenshotAnnotation annotation)
    {
        PushUndo();
        _annotations.Add(annotation);
        RenderAnnotations();
    }

    private void PushUndo()
    {
        _undo.Push(_annotations.ToList());
        _redo.Clear();
    }

    private void EraseAt(Point p)
    {
        for (var i = _annotations.Count - 1; i >= 0; i--)
        {
            if (!_annotations[i].HitTest(p)) continue;
            PushUndo();
            _annotations.RemoveAt(i);
            RenderAnnotations();
            return;
        }
    }

    private void OnUndoClick(object sender, RoutedEventArgs e) => Undo();
    private void OnRedoClick(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_annotations.ToList());
        var snapshot = _undo.Pop();
        _annotations.Clear();
        _annotations.AddRange(snapshot);
        RenderAnnotations();
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_annotations.ToList());
        var snapshot = _redo.Pop();
        _annotations.Clear();
        _annotations.AddRange(snapshot);
        RenderAnnotations();
    }

    private void RenderAnnotations()
    {
        AnnotationCanvas.Children.Clear();
        foreach (var annotation in _annotations)
        {
            var element = CreatePreviewElement(annotation);
            if (element is not null) AnnotationCanvas.Children.Add(element);
        }

        if (_draft is not null)
        {
            var element = CreatePreviewElement(_draft);
            if (element is not null) AnnotationCanvas.Children.Add(element);
        }

        DrawHandles();
    }

    private FrameworkElement? CreatePreviewElement(ScreenshotAnnotation a)
    {
        var brush = new SolidColorBrush(a.Color);
        var stroke = Math.Clamp(a.Thickness, 1, 24);
        switch (a.Tool)
        {
            case ScreenshotDrawTool.Rectangle:
            case ScreenshotDrawTool.Ellipse:
            {
                if (a.Points.Count < 2) return null;
                var rect = new Rect(a.Points[0], a.Points[1]);
                Shape shape = a.Tool == ScreenshotDrawTool.Rectangle
                    ? new Rectangle()
                    : new Ellipse();
                shape.Width = Math.Max(0, rect.Width);
                shape.Height = Math.Max(0, rect.Height);
                shape.Stroke = brush;
                shape.StrokeThickness = stroke;
                shape.Fill = a.Filled ? new SolidColorBrush(Color.FromArgb(70, a.Color.R, a.Color.G, a.Color.B)) : null;
                shape.IsHitTestVisible = false;
                Canvas.SetLeft(shape, rect.X);
                Canvas.SetTop(shape, rect.Y);
                return shape;
            }

            case ScreenshotDrawTool.Line:
            case ScreenshotDrawTool.Arrow:
            {
                if (a.Points.Count < 2) return null;
                var path = new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = BuildArrowGeometry(a.Points[0], a.Points[1], a.Tool == ScreenshotDrawTool.Arrow, stroke),
                    IsHitTestVisible = false,
                };
                return path;
            }

            case ScreenshotDrawTool.Pen:
            case ScreenshotDrawTool.Highlighter:
            {
                if (a.Points.Count < 2) return null;
                var line = new Polyline
                {
                    Points = new PointCollection(a.Points),
                    Stroke = a.Tool == ScreenshotDrawTool.Highlighter
                        ? new SolidColorBrush(Color.FromArgb(95, a.Color.R, a.Color.G, a.Color.B))
                        : brush,
                    StrokeThickness = a.Tool == ScreenshotDrawTool.Highlighter ? stroke * 4 : stroke,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false,
                };
                return line;
            }

            case ScreenshotDrawTool.Text:
            {
                if (a.Points.Count == 0 || string.IsNullOrEmpty(a.Text)) return null;
                var text = new TextBlock
                {
                    Text = a.Text,
                    Foreground = brush,
                    FontSize = a.FontSize,
                    FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(1),
                    IsHitTestVisible = false,
                };
                if (a.Filled)
                {
                    text.Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
                    text.Padding = new Thickness(4, 2, 4, 2);
                }
                Canvas.SetLeft(text, a.Points[0].X);
                Canvas.SetTop(text, a.Points[0].Y);
                return text;
            }

            case ScreenshotDrawTool.Number:
            {
                if (a.Points.Count == 0) return null;
                var diameter = Math.Max(16, a.Bounds.Width);
                var grid = new Grid { Width = diameter, Height = diameter, IsHitTestVisible = false };
                grid.Children.Add(new Ellipse { Fill = brush });
                grid.Children.Add(new TextBlock
                {
                    Text = a.Number.ToString(),
                    Foreground = Brushes.White,
                    FontSize = a.FontSize,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                Canvas.SetLeft(grid, a.Points[0].X);
                Canvas.SetTop(grid, a.Points[0].Y);
                return grid;
            }

            case ScreenshotDrawTool.Mosaic:
            case ScreenshotDrawTool.Blur:
                return CreateEffectPreview(a);

            default:
                return null;
        }
    }

    private FrameworkElement? CreateEffectPreview(ScreenshotAnnotation a)
    {
        if (a.Points.Count < 2) return null;
        var rect = new Rect(a.Points[0], a.Points[1]);
        if (rect.Width < 2 || rect.Height < 2) return null;

        try
        {
            var x = (int)Math.Round(rect.X / ActualWidth * _desktop.Image.PixelWidth);
            var y = (int)Math.Round(rect.Y / ActualHeight * _desktop.Image.PixelHeight);
            var w = Math.Max(1, (int)Math.Round(rect.Width / ActualWidth * _desktop.Image.PixelWidth));
            var h = Math.Max(1, (int)Math.Round(rect.Height / ActualHeight * _desktop.Image.PixelHeight));
            x = Math.Clamp(x, 0, _desktop.Image.PixelWidth - 1);
            y = Math.Clamp(y, 0, _desktop.Image.PixelHeight - 1);
            w = Math.Min(w, _desktop.Image.PixelWidth - x);
            h = Math.Min(h, _desktop.Image.PixelHeight - y);
            if (w <= 0 || h <= 0) return null;

            var crop = new CroppedBitmap(_desktop.Image, new Int32Rect(x, y, w, h));
            crop.Freeze();
            var image = new Image
            {
                Source = crop,
                Stretch = Stretch.Fill,
                Width = rect.Width,
                Height = rect.Height,
                IsHitTestVisible = false,
            };
            if (a.Tool == ScreenshotDrawTool.Mosaic)
            {
                var small = new TransformedBitmap(crop, new ScaleTransform(1.0 / 14, 1.0 / 14));
                small.Freeze();
                image.Source = small;
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            }
            else
            {
                image.Effect = new BlurEffect { Radius = 10, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
            }
            Canvas.SetLeft(image, rect.X);
            Canvas.SetTop(image, rect.Y);
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static Geometry BuildArrowGeometry(Point start, Point end, bool arrow, double thickness)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.LineTo(end, true, false);
            if (arrow)
            {
                var dx = end.X - start.X;
                var dy = end.Y - start.Y;
                var length = Math.Sqrt(dx * dx + dy * dy);
                if (length > 1)
                {
                    var head = Math.Clamp(thickness * 4.2, 12, 34);
                    var half = head * 0.5;
                    var ux = dx / length;
                    var uy = dy / length;
                    var bx = end.X - ux * head;
                    var by = end.Y - uy * head;
                    var left = new Point(bx - uy * half, by + ux * half);
                    var right = new Point(bx + uy * half, by - ux * half);
                    ctx.BeginFigure(end, true, true);
                    ctx.LineTo(left, true, false);
                    ctx.LineTo(right, true, false);
                }
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private void PickColorAt(Point p)
    {
        var pixelX = (int)Math.Round(p.X / Math.Max(1, ActualWidth) * _desktop.Image.PixelWidth);
        var pixelY = (int)Math.Round(p.Y / Math.Max(1, ActualHeight) * _desktop.Image.PixelHeight);
        var color = ScreenCaptureService.SampleColor(_desktop.Image, pixelX, pixelY);
        if (color is null) return;
        _color = color.Value;
        RefreshColorButtons();
        ScreenshotClipboard.TrySetText(ScreenshotColor.ToHex(color.Value), out _);
        ShowToast($"已复制颜色 {ScreenshotColor.ToHex(color.Value)} · RGB({color.Value.R},{color.Value.G},{color.Value.B})");
    }

    private async void OnOcrClick(object sender, RoutedEventArgs e)
    {
        if (!ScreenshotGeometry.IsUsable(_selection)) return;
        var physical = MapSelectionToPixels();
        if (physical.IsEmpty) return;

        ShowToast("正在识别文字…");
        try
        {
            var image = ScreenshotRenderer.Render(_desktop.Image, _desktop.Bounds, physical, _selection,
                Array.Empty<ScreenshotAnnotation>());
            var text = await ScreenshotOcrService.RecognizeAsync(image);
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowToast("没有识别到文字（系统可能未安装 OCR 语言包）");
                return;
            }
            if (ScreenshotClipboard.TrySetText(text, out var error))
                ShowToast($"OCR 已复制：{Truncate(text.Replace('\r', ' ').Replace('\n', ' '), 90)}");
            else
                ShowToast("OCR 完成，但复制失败：" + error);
        }
        catch (Exception ex)
        {
            ShowToast("OCR 失败：" + ex.Message);
        }
    }

    private void ShowToast(string text)
    {
        ToastText.Text = text;
        ToastPanel.Visibility = Visibility.Visible;
        ToastPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = ToastPanel.DesiredSize.Width;
        var h = ToastPanel.DesiredSize.Height;
        Canvas.SetLeft(ToastPanel, Math.Max(6, (ActualWidth - w) / 2));
        Canvas.SetTop(ToastPanel, Math.Max(6, ActualHeight - h - 54));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    // ================================================================== 文字

    private void StartTextEditor(Point p)
    {
        _textEditorActive = true;
        _textAnchor = p;
        TextEditor.Text = "";
        TextEditorHost.Visibility = Visibility.Visible;
        TextEditorHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = Math.Max(180, TextEditorHost.DesiredSize.Width);
        const double h = 40;
        var x = Math.Min(p.X, Math.Max(4, ActualWidth - w - 4));
        var y = Math.Min(p.Y, Math.Max(4, ActualHeight - h - 4));
        Canvas.SetLeft(TextEditorHost, x);
        Canvas.SetTop(TextEditorHost, y);
        TextEditor.Focus();
        Keyboard.Focus(TextEditor);
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelTextEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            CommitTextEditor();
            e.Handled = true;
        }
    }

    private void OnTextEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        => CommitTextEditor();

    private void CommitTextEditor()
    {
        if (!_textEditorActive) return;
        var text = TextEditor.Text;
        _textEditorActive = false;
        TextEditorHost.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrWhiteSpace(text))
        {
            var block = new TextBlock
            {
                Text = text,
                FontSize = 22,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(4, 2, 4, 2),
            };
            block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = block.DesiredSize;
            var annotation = ScreenshotAnnotation.TextAt(_textAnchor, text, _color, 22, _filled);
            annotation.SetTextBounds(new Rect(_textAnchor, new Size(Math.Max(8, size.Width), Math.Max(8, size.Height))));
            AddAnnotation(annotation);
        }

        TextEditor.Text = "";
        Focus();
    }

    private void CancelTextEditor()
    {
        _textEditorActive = false;
        TextEditorHost.Visibility = Visibility.Collapsed;
        TextEditor.Text = "";
    }

    private void HideTextEditorIfNeeded()
    {
        if (_textEditorActive) CommitTextEditor();
    }

    // ================================================================== 放大镜 / 颜色

    private void UpdateMagnifier(Point p)
    {
        if (!_config.MagnifierEnabled || TextEditorHost.Visibility == Visibility.Visible)
        {
            Magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        var ux = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
        var uy = Math.Clamp(p.Y / Math.Max(1, ActualHeight), 0, 1);
        var halfW = 0.025;
        var halfH = 0.04;
        MagnifierBrush.Viewbox = new Rect(
            Math.Max(0, ux - halfW), Math.Max(0, uy - halfH),
            Math.Min(1, Math.Min(halfW * 2, 1 - Math.Max(0, ux - halfW))),
            Math.Min(1, Math.Min(halfH * 2, 1 - Math.Max(0, uy - halfH))));
        Magnifier.Visibility = Visibility.Visible;

        var pixelX = (int)Math.Round(ux * (_desktop.Image.PixelWidth - 1));
        var pixelY = (int)Math.Round(uy * (_desktop.Image.PixelHeight - 1));
        var color = ScreenCaptureService.SampleColor(_desktop.Image, pixelX, pixelY);
        MagnifierColorText.Text = color is null
            ? $"{pixelX}, {pixelY}"
            : $"{ScreenshotColor.ToHex(color.Value)}   RGB({color.Value.R},{color.Value.G},{color.Value.B})";

        Magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var mw = Magnifier.DesiredSize.Width;
        var mh = Magnifier.DesiredSize.Height;
        var x = p.X + 18;
        var y = p.Y + 18;
        if (x + mw > ActualWidth - 6) x = p.X - mw - 18;
        if (y + mh > ActualHeight - 6) y = p.Y - mh - 18;
        Canvas.SetLeft(Magnifier, Math.Max(4, x));
        Canvas.SetTop(Magnifier, Math.Max(4, y));
    }

    // ================================================================== 完成 / 取消

    private void OnCopyClick(object sender, RoutedEventArgs e) => Finish(ScreenshotAction.Copy);
    private void OnSaveClick(object sender, RoutedEventArgs e) => Finish(ScreenshotAction.Save);
    private void OnPinClick(object sender, RoutedEventArgs e) => Finish(ScreenshotAction.Pin);
    private void OnCancelClick(object sender, RoutedEventArgs e) => Cancel();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textEditorActive) return;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C) { Finish(ScreenshotAction.Copy); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { Finish(ScreenshotAction.Save); e.Handled = true; return; }

        switch (e.Key)
        {
            case Key.Enter:
                Finish(ScreenshotAction.Copy);
                e.Handled = true;
                break;
            case Key.P:
                Finish(ScreenshotAction.Pin);
                e.Handled = true;
                break;
            case Key.S:
                Finish(ScreenshotAction.Save);
                e.Handled = true;
                break;
            case Key.Escape:
                if (_tool != ScreenshotDrawTool.Select) SelectTool(ScreenshotDrawTool.Select);
                else Cancel();
                e.Handled = true;
                break;
        }
    }

    private void Finish(ScreenshotAction action)
    {
        if (!ScreenshotGeometry.IsUsable(_selection)) return;
        var physical = MapSelectionToPixels();
        if (physical.IsEmpty) return;

        var image = ScreenshotRenderer.Render(_desktop.Image, _desktop.Bounds, physical, _selection, _annotations);
        Result = new ScreenshotOverlayResult(image, physical, action);
        _finished = true;
        DialogResult = true;
        Close();
    }

    private void Cancel()
    {
        _finished = true;
        Result = null;
        DialogResult = false;
        Close();
    }
}
