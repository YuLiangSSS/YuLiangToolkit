using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScreenPenConfigModel = YuLiangToolkit.Tools.ScreenPen.ScreenPenConfig;

namespace YuLiangToolkit.Tools.ScreenPen.Views;

/// <summary>妗岄潰閫忔槑鎵规敞瑕嗙洊灞傦細鐢诲竷 + 鏂囧瓧杈撳叆锛屼笉鍒涘缓浠诲姟鏍忕獥鍙ｃ€?/summary>
public partial class ScreenPenOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly ScreenPenConfigModel _config;
    private ScreenPenToolKind _activeTool = ScreenPenToolKind.Pen;
    private bool _drawingMode = true;
    private bool _suspended;
    private bool _mouseDown;
    private bool _erasing;
    private Point _textPoint;

    public ScreenPenOverlayWindow(ScreenPenConfigModel config)
    {
        _config = config;
        InitializeComponent();
        ApplyConfig();
        _config.PropertyChanged += OnConfigChanged;
        Loaded += (_, _) =>
        {
            ApplyConfig();
            UpdateClickThrough(!_drawingMode || _suspended);
        };
        Closed += (_, _) => _config.PropertyChanged -= OnConfigChanged;
    }

    public event EventHandler? AnnotationChanged;

    public bool IsDrawingMode => _drawingMode;

    public void SetDrawingMode(bool drawing)
    {
        _drawingMode = drawing;
        if (!drawing) CommitText();
        UpdateClickThrough(!drawing || _suspended);
    }

    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
        if (suspended)
        {
            CommitText();
            UpdateClickThrough(true);
        }
        else
        {
            UpdateClickThrough(!_drawingMode);
        }
    }

    public void Clear()
    {
        CommitText();
        Canvas.Clear();
        AnnotationChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        var result = Canvas.Undo();
        if (result) AnnotationChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool Redo()
    {
        var result = Canvas.Redo();
        if (result) AnnotationChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
    public int AnnotationCount => Canvas.Items.Count;

    public void SetBackdrop(string backdrop) => Canvas.SetBackdrop(backdrop);

    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ScreenPenConfigModel.PenColor)
            or nameof(ScreenPenConfigModel.PenThickness)
            or nameof(ScreenPenConfigModel.HighlighterThickness)
            or nameof(ScreenPenConfigModel.ActiveTool)
            or nameof(ScreenPenConfigModel.Backdrop))
        {
            ApplyConfig();
        }
    }

    private void ApplyConfig()
    {
        Canvas.SetBackdrop(_config.Backdrop);
        _activeTool = _config.ActiveTool switch
        {
            "highlighter" => ScreenPenToolKind.Highlighter,
            "eraser" => ScreenPenToolKind.Eraser,
            "line" => ScreenPenToolKind.Line,
            "rectangle" => ScreenPenToolKind.Rectangle,
            "ellipse" => ScreenPenToolKind.Ellipse,
            "arrow" => ScreenPenToolKind.Arrow,
            "text" => ScreenPenToolKind.Text,
            _ => ScreenPenToolKind.Pen,
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        style |= WsExToolWindow | WsExNoActivate;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
        UpdateClickThrough(!_drawingMode || _suspended);
    }

    private void UpdateClickThrough(bool clickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if (clickThrough) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_suspended || !_drawingMode) return;
        if (TextEditor.IsVisible)
        {
            CommitText();
            return;
        }

        var point = e.GetPosition(this);
        if (_activeTool == ScreenPenToolKind.Text)
        {
            BeginText(point);
            e.Handled = true;
            return;
        }

        _mouseDown = true;
        _erasing = _activeTool == ScreenPenToolKind.Eraser;
        if (_erasing)
        {
            Canvas.EraseAt(point);
        }
        else
        {
            var tool = _activeTool;
            var thickness = tool == ScreenPenToolKind.Highlighter
                ? _config.HighlighterThickness
                : _config.PenThickness;
            Canvas.Begin(new ScreenPenAnnotation
            {
                Tool = tool,
                Color = ScreenPenColor.Parse(_config.PenColor),
                Thickness = thickness,
            });
            Canvas.AddPoint(point);
        }

        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_mouseDown || _suspended) return;
        var point = e.GetPosition(this);
        if (_erasing)
        {
            Canvas.EraseAt(point);
            return;
        }

        Canvas.AddPoint(point);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mouseDown) return;
        _mouseDown = false;
        _erasing = false;
        Canvas.Complete();
        AnnotationChanged?.Invoke(this, EventArgs.Empty);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private void BeginText(Point point)
    {
        _textPoint = point;
        System.Windows.Controls.Canvas.SetLeft(TextEditor, point.X);
        System.Windows.Controls.Canvas.SetTop(TextEditor, point.Y);
        TextEditor.Text = "";
        TextEditor.Visibility = Visibility.Visible;
        TextEditor.Focus();
        Keyboard.Focus(TextEditor);
        Activate();
    }

    private void CommitText()
    {
        if (!TextEditor.IsVisible) return;
        var text = TextEditor.Text?.Trim() ?? "";
        TextEditor.Visibility = Visibility.Collapsed;
        if (text.Length == 0) return;

        Canvas.Begin(new ScreenPenAnnotation
        {
            Tool = ScreenPenToolKind.Text,
            Color = ScreenPenColor.Parse(_config.PenColor),
            Thickness = Math.Max(3, _config.PenThickness),
            Text = text,
        });
        Canvas.AddPoint(_textPoint);
        Canvas.Complete();
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TextEditor.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void OnTextEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (TextEditor.IsVisible) CommitText();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}

