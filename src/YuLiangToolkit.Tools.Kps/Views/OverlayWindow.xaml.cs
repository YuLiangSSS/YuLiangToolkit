using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YuLiangToolkit.Tools.Kps.Views;

/// <summary>
/// 置顶透明 Overlay 窗口：显示键位格子 + KPS 读数（可再开折线图），可锁定成鼠标点击穿透。
///
/// <para>骨架版本：只有对外 API（完整实现由队友完成）。约定：</para>
/// <list type="bullet">
///   <item>构造参数是配置对象，<see cref="ApplyConfig"/> 在设置变化时刷新外观（不负责挪位置）；</item>
///   <item><see cref="SetLocked"/> = true 时窗口鼠标穿透（WS_EX_TRANSPARENT），false 时可用鼠标拖动、滚轮缩放；</item>
///   <item>用户拖动 / 滚轮缩放结束后触发 <see cref="PlacementChanged"/>，由调用方把位置 / 缩放写回配置；</item>
///   <item>调用方负责设置 <c>DataContext</c>（绑定 KPS 视图模型的数据），窗口本自身不依赖具体 ViewModel 类型。</item>
/// </list>
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private KpsConfig _config;

    public OverlayWindow(KpsConfig config)
    {
        _config = config;
        InitializeComponent();
        ApplyConfig(config);
    }

    /// <summary>拖动 / 滚轮缩放后触发（调用方负责把配置写回并保存）。</summary>
    public event EventHandler? PlacementChanged;

    /// <summary>配置变化后刷新窗口外观（背景 / 置顶 / 不透明度等）。</summary>
    public void ApplyConfig(KpsConfig config)
    {
        _config = config;
        Topmost = config.OverlayTopmost;
        Opacity = Math.Clamp(config.OverlayOpacity, 0.1, 1.0);
        Root.CornerRadius = new CornerRadius(Math.Clamp(config.KeyCornerRadius, 0, 40));
        Root.Background = KpsColor.ParseBrush(config.OverlayBackgroundColor, System.Windows.Media.Color.FromArgb(0, 0, 0, 0));
    }

    /// <summary>锁定（点击穿透）/ 解锁（可拖动）。</summary>
    public void SetLocked(bool locked)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExNoActivate | WsExToolWindow;
        if (locked) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    /// <summary>位置越界（换了显示器 / 分辨率）时把窗口拉回可见区域。</summary>
    public void EnsureOnScreen()
    {
        // 骨架：等完整实现里按 SystemParameters.VirtualScreen* 钳制。
    }

    /// <summary>把窗口放回屏幕中央（首次显示 / 「居中」按钮用）。</summary>
    public void CenterOnScreen()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + (area.Height - ActualHeight) / 2;
    }

    /// <summary>通知外部：位置 / 缩放变了（由拖动 / 滚轮逻辑调用）。</summary>
    internal void RaisePlacementChanged() => PlacementChanged?.Invoke(this, EventArgs.Empty);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        SetLocked(_config.OverlayLocked);
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int newStyle);
}
