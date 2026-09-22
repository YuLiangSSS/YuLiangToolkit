using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YuLiangToolkit.App;

public partial class MainWindow : Window
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

    /// <summary>无边框窗口最大化时，将边界钳制到工作区（避免盖住任务栏/溢出屏幕）。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int wmGetMinMaxInfo = 0x0024;
        if (msg == wmGetMinMaxInfo)
        {
            var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            var wa = SystemParameters.WorkArea; // DIP

            // DIP → 物理像素
            var scaleX = 1.0;
            var scaleY = 1.0;
            if (PresentationSource.FromVisual(this)?.CompositionTarget is { } ct)
            {
                scaleX = ct.TransformToDevice.M11;
                scaleY = ct.TransformToDevice.M22;
            }

            mmi.ptMaxPosition = new NativePoint
            {
                X = (int)Math.Round(wa.Left * scaleX),
                Y = (int)Math.Round(wa.Top * scaleY),
            };
            mmi.ptMaxSize = new NativePoint
            {
                X = (int)Math.Round(wa.Width * scaleX),
                Y = (int)Math.Round(wa.Height * scaleY),
            };
            mmi.ptMaxTrackSize = new NativePoint
            {
                X = (int)Math.Round(wa.Width * scaleX),
                Y = (int)Math.Round(wa.Height * scaleY),
            };
            Marshal.StructureToPtr(mmi, lParam, true);
            handled = true;
        }
        return IntPtr.Zero;
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
