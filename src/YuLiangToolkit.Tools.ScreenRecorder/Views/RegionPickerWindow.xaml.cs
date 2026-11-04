using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Tools.ScreenRecorder.Services;

namespace YuLiangToolkit.Tools.ScreenRecorder.Views;

/// <summary>
/// 全屏框选浮窗：拖出一个矩形，Enter 确认、Esc / 右键取消。
///
/// <para>坐标换算：窗口铺满虚拟屏幕（DIP），框选是窗口内的 DIP；确认时用
/// <see cref="VisualTreeHelper.GetDpi"/> 的缩放把 DIP 换成**物理像素**，
/// 再夹进虚拟屏幕，得到 FFmpeg gdigrab 能直接用的 <see cref="CaptureRect"/>。</para>
///
/// <para>窗口自己会处理 DPI 缩放（WPF 默认系统 DPI 感知）。多显示器如果每台缩放不同，
/// 这里按系统 DPI 换算，副屏上会有明显偏移 —— 混合缩放的多屏建议先把显示缩放统一。</para>
/// </summary>
public partial class RegionPickerWindow : Window
{
    private const double MinSelectionSize = 16;

    private CaptureRect? _initialRegion;
    private Rect _selection;
    private Point _dragStart;
    private bool _dragging;

    public RegionPickerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        KeyDown += OnKeyDown;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += (_, _) => Cancel();
        Closed += (_, _) => Mouse.OverrideCursor = null;
    }

    /// <summary>用户确认后的区域（物理像素）；取消 / 直接关闭时为 null。</summary>
    public CaptureRect? SelectedRegion { get; private set; }

    /// <summary>打开时预填上次的区域（物理像素）。</summary>
    public void UseInitialRegion(CaptureRect region) => _initialRegion = region;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = Math.Max(1, SystemParameters.VirtualScreenWidth);
        Height = Math.Max(1, SystemParameters.VirtualScreenHeight);
        RootCanvas.Width = Width;
        RootCanvas.Height = Height;

        Mouse.OverrideCursor = Cursors.Cross;
        UpdateGeometry();

        if (_initialRegion is { } initial && !initial.IsEmpty)
        {
            var rect = PhysicalToWindowRect(initial);
            _selection = ClampToCanvas(rect);
            UpdateGeometry();
        }

        Activate();
        Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Cancel();
            e.Handled = true;
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        _dragStart = e.GetPosition(RootCanvas);
        _selection = new Rect(_dragStart, _dragStart);
        _dragging = true;
        CaptureMouse();
        UpdateGeometry();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var current = e.GetPosition(RootCanvas);
        var left = Math.Min(_dragStart.X, current.X);
        var top = Math.Min(_dragStart.Y, current.Y);
        var right = Math.Max(_dragStart.X, current.X);
        var bottom = Math.Max(_dragStart.Y, current.Y);
        _selection = ClampToCanvas(new Rect(left, top, right - left, bottom - top));
        UpdateGeometry();
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
            _selection = Rect.Empty;   // 点一下没拖动 = 清空选择

        UpdateGeometry();
        e.Handled = true;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Confirm();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Cancel();
    }

    private void Confirm()
    {
        if (_selection.Width < MinSelectionSize || _selection.Height < MinSelectionSize)
        {
            Cancel();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var physical = ScreenCoordinateMapper.DipToPhysical(
            _selection.Left, _selection.Top, _selection.Width, _selection.Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            dpi.DpiScaleX, dpi.DpiScaleY);

        physical = physical.ClampTo(NativeScreen.VirtualScreen).MakeEven();
        if (physical.IsEmpty)
        {
            Cancel();
            return;
        }

        SelectedRegion = physical;
        DialogResult = true;
        Close();
    }

    private void Cancel()
    {
        SelectedRegion = null;
        DialogResult = false;
        Close();
    }

    /// <summary>把物理像素区域换算成窗口内 DIP 矩形（用于打开时预填上次的选择）。</summary>
    private Rect PhysicalToWindowRect(CaptureRect physical)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = physical.X / dpi.DpiScaleX - SystemParameters.VirtualScreenLeft;
        var top = physical.Y / dpi.DpiScaleY - SystemParameters.VirtualScreenTop;
        return new Rect(left, top, physical.Width / dpi.DpiScaleX, physical.Height / dpi.DpiScaleY);
    }

    private Rect ClampToCanvas(Rect rect)
    {
        var canvas = new Rect(0, 0, Math.Max(0, RootCanvas.ActualWidth), Math.Max(0, RootCanvas.ActualHeight));
        if (canvas.Width <= 0 || canvas.Height <= 0) return rect;

        var left = Math.Max(canvas.Left, Math.Min(rect.Left, canvas.Right));
        var top = Math.Max(canvas.Top, Math.Min(rect.Top, canvas.Bottom));
        var right = Math.Max(left, Math.Min(rect.Right, canvas.Right));
        var bottom = Math.Max(top, Math.Min(rect.Bottom, canvas.Bottom));
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>重画遮罩 / 选框 / 尺寸标签。</summary>
    private void UpdateGeometry()
    {
        var full = new Rect(0, 0, Math.Max(0, RootCanvas.ActualWidth), Math.Max(0, RootCanvas.ActualHeight));
        var hasSelection = _selection.Width >= MinSelectionSize && _selection.Height >= MinSelectionSize;

        if (!hasSelection)
        {
            DimPath.Data = new RectangleGeometry(full);
            SelectionRect.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            ConfirmButton.IsEnabled = false;
            return;
        }

        ConfirmButton.IsEnabled = true;
        DimPath.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(_selection));

        SelectionRect.Visibility = Visibility.Visible;
        SelectionRect.Width = _selection.Width;
        SelectionRect.Height = _selection.Height;
        Canvas.SetLeft(SelectionRect, _selection.Left);
        Canvas.SetTop(SelectionRect, _selection.Top);

        SizeBadge.Visibility = Visibility.Visible;
        SizeText.Text = BuildPhysicalSizeText();
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badgeWidth = SizeBadge.DesiredSize.Width;
        var badgeHeight = SizeBadge.DesiredSize.Height;
        var badgeLeft = Math.Max(0, _selection.Right - badgeWidth - 6);
        var badgeTop = _selection.Bottom + 6;
        if (badgeTop + badgeHeight > full.Height) badgeTop = Math.Max(0, _selection.Bottom - badgeHeight - 6);
        Canvas.SetLeft(SizeBadge, badgeLeft);
        Canvas.SetTop(SizeBadge, badgeTop);
    }

    /// <summary>尺寸标签显示**实际会录到的物理像素**（缩放 ≠ 100% 时不显示 DIP 数字，避免误解）。</summary>
    private string BuildPhysicalSizeText()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var physical = ScreenCoordinateMapper.DipToPhysical(
            _selection.Left, _selection.Top, _selection.Width, _selection.Height,
            SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            dpi.DpiScaleX, dpi.DpiScaleY);
        return physical.IsEmpty ? "0 × 0" : $"{physical.Width} × {physical.Height}";
    }
}
