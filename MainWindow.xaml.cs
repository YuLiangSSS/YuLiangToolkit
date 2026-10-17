using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App;

/// <summary>
/// 主窗口。除了自定义标题栏，还负责两件“外壳级”的事：
/// <list type="bullet">
///   <item><see cref="IFullscreenHost"/>：功能页（播放器）要求全屏时，藏掉标题栏 / 工具条 / 侧边栏 / 状态栏，
///   把窗口铺满**整个屏幕**（含任务栏区域，所以要临时置顶），退出时原样还原；</item>
///   <item>左下角迷你播放条由 XAML 里的 <c>views:MiniPlayerPanel</c> 承载（只看外壳的 <c>MiniPanelVisible</c>）。</item>
/// </list>
/// </summary>
public partial class MainWindow : Window, IFullscreenHost
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint ptReserved;
        public NativePoint ptMaxSize;
        public NativePoint ptMaxPosition;
        public NativePoint ptMinTrackSize;
        public NativePoint ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private bool _fullscreen;
    private Rect _restoreBounds;
    private WindowState _restoreState;
    private bool _restoreTopmost;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += OnWindowStateChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
    }

    /// <summary>无边框窗口最大化时，将边界钳制到工作区（避免盖住任务栏/溢出屏幕）。全屏时改成整块显示器。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        if (msg == wmGetMinMaxInfo)
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var (left, top, width, height) = GetMaximizeRect();   // 物理像素

            mmi.ptMaxPosition = new NativePoint { X = left, Y = top };
            mmi.ptMaxSize = new NativePoint { X = width, Y = height };
            mmi.ptMaxTrackSize = new NativePoint { X = width, Y = height };

            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 最大化时窗口该占多大（物理像素）：平时是**工作区**；全屏时是**整块显示器**（连任务栏那块也占）。
    ///
    /// <para>⚠ 不要试图用 <c>Left/Top/Width/Height</c> 直接铺满屏幕 —— 会被窗口管理器夹回工作区。
    /// 正路就是“临时改掉 WM_GETMINMAXINFO 再最大化”（就是这里）。</para>
    /// </summary>
    private (int Left, int Top, int Width, int Height) GetMaximizeRect()
    {
        if (_fullscreen)
        {
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                // 窗口最大化时会多出一圈“可拖拽的边框”（属于非客户区），只按显示器尺寸给的话
                // 那圈边框就露在屏幕里、四周出现一道亮边（截图里看得很清楚）。
                // 把它推到屏幕外面：位置各退一个边框宽、尺寸各加两个边框宽 → 客户区正好等于整块屏幕。
                var (scaleX, scaleY) = GetScale();
                var border = SystemParameters.WindowResizeBorderThickness;
                var borderX = (int)Math.Round(border.Left * scaleX);
                var borderY = (int)Math.Round(border.Top * scaleY);

                return (info.rcMonitor.Left - borderX,
                        info.rcMonitor.Top - borderY,
                        info.rcMonitor.Right - info.rcMonitor.Left + borderX * 2,
                        info.rcMonitor.Bottom - info.rcMonitor.Top + borderY * 2);
            }
        }

        var wa = SystemParameters.WorkArea;   // DIP → 物理像素
        var (sx, sy) = GetScale();
        return ((int)Math.Round(wa.Left * sx), (int)Math.Round(wa.Top * sy),
                (int)Math.Round(wa.Width * sx), (int)Math.Round(wa.Height * sy));
    }

    private (double X, double Y) GetScale()
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
        {
            return (ct.TransformToDevice.M11, ct.TransformToDevice.M22);
        }
        return (1.0, 1.0);
    }

    // ---------- 自定义标题栏窗口控制 ----------

    /// <summary>标题栏手动拖拽 + 双击最大化（CaptionHeight=0 时窗口没有原生拖拽区）。</summary>
    private void OnTitleBarMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        // 点击窗口按钮时不触发拖拽/双击最大化（事件会冒泡到这里）
        if (e.OriginalSource is DependencyObject source && FindVisualAncestor<Button>(source) is not null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }

        if (WindowState == WindowState.Normal)
        {
            try { DragMove(); } catch { /* 拖动过程中被中断等情况忽略 */ }
        }
    }

    private static T? FindVisualAncestor<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T match) return match;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return null;
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e)
        => Close();

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // 最大化时切换为“还原”图标
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ---------- 全屏（IFullscreenHost） ----------

    /// <summary>当前是不是全屏状态。</summary>
    public bool IsFullscreen => _fullscreen;

    /// <summary>
    /// 进入 / 退出全屏。返回操作之后的状态。
    ///
    /// <para>做法：把标题栏 / 工具条 / 状态栏所在的行高度改成 0、侧边栏列宽改成 0（边框跟着一起消失），
    /// 然后窗口切回 <see cref="WindowState.Normal"/> 并**直接设成显示器的整块矩形**（不是工作区，
    /// 所以会盖住任务栏），再临时 <c>Topmost = true</c> —— 任务栏是置顶窗口，不置顶盖不住它。
    /// 退出时把这些全部还原。</para>
    /// </summary>
    public bool SetFullscreen(bool on)
    {
        if (on == _fullscreen) return _fullscreen;
        if (on) EnterFullscreen();
        else ExitFullscreen();
        return _fullscreen;
    }

    private void EnterFullscreen()
    {
        _restoreState = WindowState;
        _restoreBounds = new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        _restoreTopmost = Topmost;

        // 藏掉外壳（行高 / 列宽归零，边框跟着一起消失）
        TitleRow.Height = new GridLength(0);
        ToolbarRow.Height = new GridLength(0);
        StatusRow.Height = new GridLength(0);
        SidebarColumn.Width = new GridLength(0);

        _fullscreen = true;                 // 先置位：下面最大化时 WndProc 才会按整块显示器算
        WindowState = WindowState.Normal;   // 先还原，避免“Normal → Maximized”被当成同尺寸而忽略
        WindowState = WindowState.Maximized;
        Topmost = true;                     // 任务栏是置顶窗口，不置顶盖不住它
    }

    private void ExitFullscreen()
    {
        Topmost = _restoreTopmost;

        TitleRow.Height = new GridLength(40);
        ToolbarRow.Height = GridLength.Auto;
        StatusRow.Height = GridLength.Auto;
        SidebarColumn.Width = new GridLength(232);

        _fullscreen = false;                // 再清：还原窗口时又按工作区算
        WindowState = _restoreState;
        if (_restoreState == WindowState.Normal && _restoreBounds.Width > 0 && _restoreBounds.Height > 0)
        {
            Left = _restoreBounds.Left;
            Top = _restoreBounds.Top;
            Width = _restoreBounds.Width;
            Height = _restoreBounds.Height;
        }
    }

    /// <summary>开发调试用：将当前界面渲染为 PNG 并退出（由 App 的 YLT_SHOT 环境变量触发）。</summary>
    public void CaptureTo(string path)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, async () =>
        {
            await Task.Delay(1200);
            try
            {
                var w = (int)Math.Max(1, ActualWidth);
                var h = (int)Math.Max(1, ActualHeight);
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                if (Content is Visual visual) rtb.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path))
                {
                    encoder.Save(fs);
                }
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(path + ".err.txt", ex.ToString()); } catch { /* 忽略 */ }
            }
            Application.Current?.Shutdown();
        });
    }
}
