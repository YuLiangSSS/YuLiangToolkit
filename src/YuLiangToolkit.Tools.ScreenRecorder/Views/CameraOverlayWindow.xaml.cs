using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.ScreenRecorder.Services;

namespace YuLiangToolkit.Tools.ScreenRecorder.Views;

/// <summary>
/// 摄像头「屏幕覆盖」小窗：置顶、无边框、透明背景，显示实时摄像头画面。
///
/// <para><b>会被录进画面</b>：它就是一个真实窗口，gdigrab 截桌面时会把它一起截进去；
/// 所以录屏时把摄像头拖到录制区域里（例如镜头角落）就能得到 OBS 那种「摄像头叠加」的效果。
/// 预览时也可以先打开它，摆在目标窗口上确认位置，再开始录。</para>
///
/// <para>操作：拖动 = 移动、滚轮 = 缩放、右键 = 镜像 / 重置 / 关闭。
/// 位置和宽度通过 <see cref="PlacementChanged"/> 交给 ViewModel 写回配置；窗口自身不关自己
/// （右键关闭只触发 <see cref="CloseRequested"/>），避免 ViewModel 拿着已关闭窗口的引用。</para>
/// </summary>
public partial class CameraOverlayWindow : Window
{
    private bool _allowClose;
    private bool _dragging;
    private bool _mirror = true;
    private double _aspect = 16.0 / 9.0;

    public CameraOverlayWindow()
    {
        InitializeComponent();
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
        };

        // DragMove() 内部会捕获鼠标，松手在 LostMouseCapture 时结束：拖动结束后把位置写回配置。
        LostMouseCapture += (_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            RaisePlacement();
        };
    }

    /// <summary>拖动 / 滚轮缩放后触发，参数是当前窗口位置（DIP）和宽度（DIP）。</summary>
    public event EventHandler<CameraOverlayPlacement>? PlacementChanged;

    /// <summary>用户从右键菜单选择「关闭摄像头覆盖」。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>用户从右键菜单选择「左右镜像」。</summary>
    public event EventHandler<bool>? MirrorChanged;

    /// <summary>用户从右键菜单选择「重置到录制区域右下角」。</summary>
    public event EventHandler? ResetPositionRequested;

    /// <summary>当前是否镜像。</summary>
    public bool IsMirrored => _mirror;

    /// <summary>当前宽度（DIP）。</summary>
    public double OverlayWidth => ActualWidth > 1 ? ActualWidth : Width;

    /// <summary>更新一帧画面（BGRA、已冻结，可在 UI 线程直接赋值）。</summary>
    public void UpdateFrame(BitmapSource? image)
    {
        PreviewImage.Source = image;
        if (image is not null && image.PixelHeight > 0)
        {
            _aspect = (double)image.PixelWidth / image.PixelHeight;
            ApplySize();
        }
    }

    /// <summary>设置镜像 / 不透明度（不透明度 0.2~1.0，按“黑底混合”实现，窗口本身不透明）。</summary>
    public void ApplyAppearance(bool mirror, double opacity)
    {
        _mirror = mirror;
        MirrorMenuItem.IsChecked = mirror;
        PreviewImage.LayoutTransform = mirror
            ? new ScaleTransform(-1, 1)
            : Transform.Identity;
        PreviewImage.Opacity = Math.Clamp(opacity, 0.2, 1.0);
    }

    /// <summary>设置宽度（DIP）并按摄像头比例更新高度。</summary>
    public void SetOverlayWidth(double width)
    {
        Width = Math.Clamp(width, 120, 800);
        ApplySize();
    }

    /// <summary>把窗口放到指定 DIP 坐标（会夹进虚拟屏幕）。</summary>
    public void MoveTo(double left, double top)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        Width = Math.Clamp(Width, 120, 800);
        ApplySize();

        var minLeft = virtualLeft - ActualWidth + 80;
        var maxLeft = virtualRight - 80;
        var minTop = virtualTop - ActualHeight + 60;
        var maxTop = virtualBottom - 60;
        Left = Math.Clamp(left, minLeft, Math.Max(minLeft, maxLeft));
        Top = Math.Clamp(top, minTop, Math.Max(minTop, maxTop));
    }

    /// <summary>显示窗口（不激活、不抢焦点）。</summary>
    public void ShowOverlay()
    {
        if (!IsVisible) Show();
        ApplySize();
    }

    /// <summary>隐藏但不销毁。</summary>
    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }

    /// <summary>由 ViewModel 真正关闭窗口。</summary>
    public void CloseNow()
    {
        _allowClose = true;
        Close();
    }

    private void ApplySize()
    {
        if (!double.IsFinite(_aspect) || _aspect <= 0.1) _aspect = 16.0 / 9.0;
        var width = Math.Clamp(Width > 1 ? Width : 320, 120, 800);
        Width = width;
        Height = Math.Round(width / _aspect, MidpointRounding.AwayFromZero);
    }

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = true;
        try { DragMove(); } catch { /* 窗口在拖动时被关掉 */ }
        e.Handled = true;
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        RaisePlacement();
        e.Handled = true;
    }

    private void OnRootMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var step = e.Delta > 0 ? 40 : -40;
        Width = Math.Clamp((Width > 1 ? Width : 320) + step, 120, 800);
        ApplySize();
        RaisePlacement();
        e.Handled = true;
    }

    private void OnMirrorMenuClick(object sender, RoutedEventArgs e)
    {
        _mirror = MirrorMenuItem.IsChecked;
        PreviewImage.LayoutTransform = _mirror ? new ScaleTransform(-1, 1) : Transform.Identity;
        MirrorChanged?.Invoke(this, _mirror);
    }

    private void OnResetMenuClick(object sender, RoutedEventArgs e)
        => ResetPositionRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseMenuClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void RaisePlacement()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsNaN(Width)) return;
        PlacementChanged?.Invoke(this, new CameraOverlayPlacement(Left, Top, Width));
    }
}

/// <summary>摄像头覆盖窗口的位置 / 宽度（DIP）。</summary>
public sealed record CameraOverlayPlacement(double Left, double Top, double Width);
