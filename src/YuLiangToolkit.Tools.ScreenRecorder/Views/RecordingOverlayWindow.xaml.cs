using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using YuLiangToolkit.Tools.ScreenRecorder.Services;

namespace YuLiangToolkit.Tools.ScreenRecorder.Views;

/// <summary>
/// 录制中的置顶控制条：计时器 + 来源 + 停止按钮，可按住拖动。
///
/// <para>位置策略：区域录制时优先放在区域**外面**（右 → 下 → 上 → 实在不行放里面），
/// 避免被 gdigrab 一起录进去；全屏录制时放主屏右上角（此时不可避免会被录到，
/// 想完全干净就在设置里关掉「录制时显示控制条」，用 F8 停止）。</para>
///
/// <para>会尝试对窗口设置 <c>WDA_EXCLUDEFROMCAPTURE</c>，在部分系统上能让它不被录进画面；
/// gdigrab 走 GDI，不能保证一定排除，所以才有上面的位置策略。</para>
/// </summary>
public partial class RecordingOverlayWindow : Window
{
    private readonly DispatcherTimer _timer;
    private DateTime _startedAt;
    private bool _allowClose;

    public RecordingOverlayWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _timer.Tick += (_, _) => UpdateElapsed();

        // 控制条只能由 ViewModel 关：用户 Alt+F4 时取消关闭，避免 VM 还拿着已关闭的 Window 引用。
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
        };

        SourceInitialized += (_, _) =>
        {
            try { NativeScreen.TryExcludeFromCapture(new WindowInteropHelper(this).Handle); }
            catch { /* best effort */ }
        };
    }

    /// <summary>用户点了「停止」。</summary>
    public event EventHandler? StopRequested;

    /// <summary>显示控制条并定位。</summary>
    public void ShowFor(string sourceText, DateTime startedAt, CaptureRect region, bool isFullScreen)
    {
        _startedAt = startedAt;
        SourceText.Text = sourceText;
        UpdateElapsed();

        Show();
        UpdateLayout();
        PlaceNear(region, isFullScreen);
        _timer.Start();
    }

    /// <summary>隐藏控制条（不关窗口，下次录制直接复用）。</summary>
    public void StopAndHide()
    {
        _timer.Stop();
        if (IsLoaded) Hide();
    }

    /// <summary>由 ViewModel 在工具销毁时真正关闭窗口。</summary>
    public void CloseNow()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    private void UpdateElapsed()
        => ElapsedText.Text = RecordingTime.FormatDuration(DateTime.Now - _startedAt);

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnDragAreaMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try { DragMove(); } catch { /* 拖动时窗口被关掉 */ }
    }

    /// <summary>把控制条摆在采集区域外面；全屏时放虚拟屏幕右上角。</summary>
    private void PlaceNear(CaptureRect region, bool isFullScreen)
    {
        var primary = NativeScreen.PrimaryScreen;
        var virtualScreen = NativeScreen.VirtualScreen;
        if (virtualScreen.IsEmpty) virtualScreen = primary;

        // 物理像素 → WPF DIP 的缩放（系统 DPI 感知下主屏缩放足够用）
        var scale = primary.Width > 0 ? SystemParameters.PrimaryScreenWidth / primary.Width : 1;
        if (double.IsNaN(scale) || scale <= 0) scale = 1;

        var widthPx = ActualWidth * scale;
        var heightPx = ActualHeight * scale;
        var margin = 12.0;
        double x;
        double y;

        if (isFullScreen || region.IsEmpty)
        {
            // 全屏录制：放主屏右上角（多屏时不要跑到最右边的副屏上，用户会找不到控制条）
            x = primary.Right - widthPx - 16;
            y = primary.Y + 16;
        }
        else if (region.Right + margin + widthPx <= virtualScreen.Right)
        {
            x = region.Right + margin;
            y = region.Y;
        }
        else if (region.Bottom + margin + heightPx <= virtualScreen.Bottom)
        {
            x = region.X;
            y = region.Bottom + margin;
        }
        else if (region.Y - margin - heightPx >= virtualScreen.Y)
        {
            x = region.X;
            y = region.Y - margin - heightPx;
        }
        else
        {
            // 四周都没地方：退回区域内部左上角（会被录进去，但至少能操作）
            x = region.X + margin;
            y = region.Y + margin;
        }

        x = SafeClamp(x, virtualScreen.X + 4, virtualScreen.Right - widthPx - 4, virtualScreen.X + 4);
        y = SafeClamp(y, virtualScreen.Y + 4, virtualScreen.Bottom - heightPx - 4, virtualScreen.Y + 4);

        Left = SystemParameters.VirtualScreenLeft + (x - virtualScreen.X) / scale;
        Top = SystemParameters.VirtualScreenTop + (y - virtualScreen.Y) / scale;
    }

    private static double SafeClamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
        if (min > max) return fallback;
        return Math.Clamp(value, min, max);
    }
}
