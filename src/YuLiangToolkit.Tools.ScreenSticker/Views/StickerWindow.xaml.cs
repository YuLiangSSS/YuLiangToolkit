using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YuLiangToolkit.Tools.ScreenSticker.Views;

/// <summary>
/// 一张桌面贴纸（独立透明无边框窗口）：显示一张图片，可拖动、滚轮缩放、角点拖拽缩放、
/// 右键调层级 / 单独强制置顶 / 关闭。
///
/// <para><b>为什么一张图一个窗口</b>：需求要「每张贴纸单独选择是否强制置顶」，
/// 一个全屏大画布做不到「这张置顶、那张不置顶」；而且独立窗口的层级 / 激活 / 拖动都交给 Windows 管，
/// 比自己在画布上模拟 z-order 稳得多（Live2D 桌宠也是同一种窗口）。</para>
///
/// <para><b>位置 / 大小会写回配置</b>（通过 <see cref="StickerSession"/> 防抖落盘），下次启动自动恢复。</para>
///
/// <para><b>截图调试</b>：环境变量 <c>YLT_STICKER_SHOT=&lt;png&gt;</c> 时，窗口出现后约 0.8 秒把窗口内容
/// （透明底垫深色）存成 PNG，然后退出程序（开发 / 验收用）。</para>
/// </summary>
public partial class StickerWindow : Window
{
    // Win32：把窗口重排到 z-order 顶部（不移动 / 不改大小 / 不抢焦点）
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    private readonly StickerSession _session;
    private readonly StickerEntry _entry;
    private readonly StickerMedia _media;
    private readonly StickerAnimation? _animation;
    private readonly double _aspect;
    private readonly DispatcherTimer _frameTimer;

    private int _frameIndex;
    private double _speed;

    private bool _dragging;
    private Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;

    private bool _resizing;
    private StickerBounds _resizeStart;
    private Point _resizeStartCursor;

    private bool _closingForShutdown;
    private bool _menuOpen;
    private bool _debugCaptureScheduled;

    public StickerWindow(StickerSession session, StickerEntry entry, StickerMedia media)
    {
        _session = session;
        _entry = entry;
        _media = media;
        _animation = media.Animation;
        _speed = StickerTiming.ClampSpeed(entry.GifSpeed);
        InitializeComponent();

        _aspect = media.Aspect;
        Picture.Source = media.Still;

        // GIF 帧播放器：一个窗口一个定时器，按当前帧的延时 × 播放速度切 Image.Source。
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(StickerTiming.DefaultFrameDelayMs),
        };
        _frameTimer.Tick += OnAnimationTick;

        // 恢复：尺寸合法 + 至少留一角在屏幕里（显示器拔掉 / 分辨率变小也不会「丢」）。
        var bounds = StickerGeometry.NormalizeStored(
            entry.Left, entry.Top, entry.Width, _aspect, StickerSession.VirtualScreenBounds);
        ApplyBounds(bounds, save: false);
        Topmost = entry.Topmost;

        Loaded += OnLoaded;
        Closed += OnClosed;
        Activated += OnActivated;

        // 恢复 / 新增时如果尺寸是 0（老配置 / 手改坏），这里兜一下，保证窗口能正常显示。
        if (Width < StickerGeometry.MinWidth || Height <= 0)
        {
            var fallback = StickerGeometry.FitInside(StickerSession.WorkArea, entry.Width, _aspect);
            ApplyBounds(StickerGeometry.CenterIn(StickerSession.WorkArea, fallback.Width, fallback.Height), save: false);
        }
    }

    /// <summary>这张贴纸在配置里的记录（位置 / 大小 / 置顶状态都写这里）。</summary>
    public StickerEntry Entry => _entry;

    /// <summary>图片路径（工具页列表显示用）。</summary>
    public string ImagePath => _entry.Path;

    /// <summary>图片「高 / 宽」比例。</summary>
    public double ImageAspect => _aspect;

    /// <summary>是不是会动的 GIF（贴纸列表据此决定要不要显示倍速按钮）。</summary>
    public bool IsAnimated => _animation is not null;

    /// <summary>当前 GIF 播放速度（1× = GIF 自带速度；静态图也有值但无效果）。</summary>
    public double PlaybackSpeed => _speed;

    /// <summary>当前 GIF 一共几帧（静态图 = 1）。</summary>
    public int FrameCount => _animation?.FrameCount ?? 1;

    /// <summary>是否已经因为「程序退出 / 关闭全部」而关闭（这种关闭保留配置记录）。</summary>
    public bool IsClosingForShutdown => _closingForShutdown;

    /// <summary>当前窗口的位置 + 大小（DIP）。</summary>
    public StickerBounds CurrentBounds => new(Left, Top, Width, Height);

    // ===================== 对外操作（会话 / 右键菜单共用） =====================

    /// <summary>改 GIF 播放速度（立即按新间隔继续播当前帧，不从头开始）。</summary>
    public void ApplyPlaybackSpeed(double speed)
    {
        _speed = StickerTiming.ClampSpeed(speed);
        if (IsAnimated) RestartFrameTimer();
    }

    /// <summary>设置是否强制置顶（配置字段由会话写；这里只动窗口）。</summary>
    public void ApplyTopmost(bool topmost)
    {
        if (Topmost != topmost) Topmost = topmost;
        BringToFront();
    }

    /// <summary>把宽度改成默认宽度，中心点不动（右键「重置缩放」）。</summary>
    public void ResetZoom(double width)
    {
        var bounds = StickerGeometry.ResetWidthKeepingCenter(
            CurrentBounds, width, _aspect, StickerSession.VirtualScreenBounds);
        ApplyBounds(bounds);
    }

    /// <summary>在工作区居中（大小不变）。</summary>
    public void CenterOnWorkArea()
    {
        var area = StickerSession.WorkArea;
        var bounds = new StickerBounds(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2,
            Width,
            Height);
        ApplyBounds(StickerGeometry.ClampToBounds(bounds, StickerSession.VirtualScreenBounds));
    }

    /// <summary>把窗口重排到 z-order 顶部（同一 topmost 带内最靠上）。</summary>
    public void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>在资源管理器里选中图片文件。</summary>
    public void RevealImage()
    {
        try
        {
            if (!File.Exists(_entry.Path)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{_entry.Path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 打开资源管理器失败不影响贴纸 */ }
    }

    /// <summary>
    /// 程序退出 / 「关闭全部贴纸」时关闭：<b>保留配置记录</b>，下次启动还能恢复。
    /// 用户自己点「关闭这张贴纸」不走这里（那种要删记录）。
    /// </summary>
    public void CloseForShutdown()
    {
        _closingForShutdown = true;
        try { Close(); } catch { /* 关闭失败也不影响退出 */ }
    }

    // ===================== 窗口生命周期 =====================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // GIF：窗口出来后开始播（静态图这里什么都不做）
        StartAnimation();

        // 调试截图：只对第一张（被 YLT_STICKER_SHOT 指定的）窗口生效，避免多张时抢着退出。
        var shotPath = Environment.GetEnvironmentVariable("YLT_STICKER_SHOT");
        if (!_debugCaptureScheduled && !string.IsNullOrWhiteSpace(shotPath)
            && Environment.GetEnvironmentVariable("YLT_STICKER_SHOT_TARGET") is null or "")
        {
            _debugCaptureScheduled = true;
            Environment.SetEnvironmentVariable("YLT_STICKER_SHOT_TARGET", _entry.Path);
            ScheduleDebugCapture(shotPath);
        }
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        // 用户点中某张贴纸 = 它被抬到其它普通窗口上面；同步工具页里的层级列表顺序。
        if (!_dragging && !_resizing) _session.NotifyWindowActivated(this);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try { _frameTimer.Stop(); } catch { /* 窗口已关忽略 */ }
        _frameTimer.Tick -= OnAnimationTick;
        Picture.Source = null;   // 尽快释放动画帧（GIF 帧可能占不少内存）

        if (_closingForShutdown) _session.NotifyWindowClosedKeepEntry(this);
        else _session.NotifyWindowClosedByUser(this);
    }

    // ===================== GIF 动画 =====================

    /// <summary>开始播 GIF（如果这贴是静态图则什么都不做）。</summary>
    private void StartAnimation()
    {
        if (_animation is null || _animation.FrameCount <= 1) return;

        _frameIndex = 0;
        Picture.Source = _animation.Frames[0];
        RestartFrameTimer();
        _frameTimer.Start();
    }

    /// <summary>按「当前帧原始延时 ÷ 播放速度」更新定时器间隔（改速度 / 换帧后调用）。</summary>
    private void RestartFrameTimer()
    {
        if (_animation is null || _animation.FrameCount == 0) return;

        var index = Math.Clamp(_frameIndex, 0, _animation.FrameCount - 1);
        var interval = StickerTiming.EffectiveIntervalMs(_animation.DelaysMs[index], _speed);
        _frameTimer.Interval = TimeSpan.FromMilliseconds(interval);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (_animation is null || _animation.FrameCount <= 1)
        {
            _frameTimer.Stop();
            return;
        }

        _frameIndex = (_frameIndex + 1) % _animation.FrameCount;
        Picture.Source = _animation.Frames[_frameIndex];
        RestartFrameTimer();
    }

    // ===================== 鼠标：拖动 / 滚轮缩放 =====================

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ResizeGrip.IsMouseOver) return;                 // 手柄自己处理
        if (!TryGetCursorDip(out var cursor)) return;

        _dragging = true;
        _dragStartCursor = cursor;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        if (!TryGetCursorDip(out var cursor))
        {
            FinishDrag();
            return;
        }

        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X);
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y);
        e.Handled = true;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        FinishDrag();
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();

        var bounds = StickerGeometry.ClampToBounds(CurrentBounds, StickerSession.VirtualScreenBounds);
        ApplyBounds(bounds);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!TryGetCursorDip(out var cursor)) return;

        var factor = StickerGeometry.WheelFactor(e.Delta);
        var bounds = StickerGeometry.ScaleAround(CurrentBounds, factor, cursor.X, cursor.Y, StickerSession.VirtualScreenBounds);
        if (StickerGeometry.NearlyEqual(bounds, CurrentBounds)) return;

        ApplyBounds(bounds);
        e.Handled = true;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e) => SetHoverChrome(true);

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging || _resizing || _menuOpen) return;
        SetHoverChrome(false);
    }

    private void SetHoverChrome(bool visible)
    {
        HoverFrame.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ResizeGrip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== 右下角手柄缩放 =====================

    private void OnGripDragStarted(object sender, DragStartedEventArgs e)
    {
        if (!TryGetCursorDip(out var cursor)) return;
        _resizing = true;
        _resizeStart = CurrentBounds;
        _resizeStartCursor = cursor;
        SetHoverChrome(true);
        e.Handled = true;
    }

    private void OnGripDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (!_resizing) return;
        if (!TryGetCursorDip(out var cursor)) return;

        var bounds = StickerGeometry.ResizeFromCorner(
            _resizeStart,
            cursor.X - _resizeStartCursor.X,
            cursor.Y - _resizeStartCursor.Y,
            StickerSession.VirtualScreenBounds);
        ApplyBounds(bounds);
        e.Handled = true;
    }

    private void OnGripDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (!_resizing) return;
        _resizing = false;
        ApplyBounds(StickerGeometry.ClampToBounds(CurrentBounds, StickerSession.VirtualScreenBounds));
        e.Handled = true;
    }

    // ===================== 右键菜单 =====================

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        _menuOpen = true;
        MenuItemTopmost.IsChecked = _entry.Topmost;
        UpdateGifSpeedMenu();
    }

    /// <summary>右键菜单打开时刷新「GIF 播放速度」：静态图整项禁用，动画图显示当前倍速并勾选。</summary>
    private void UpdateGifSpeedMenu()
    {
        MenuItemGifSpeed.IsEnabled = IsAnimated;
        MenuItemGifSpeed.Header = IsAnimated
            ? $"GIF 播放速度（当前 {StickerTiming.FormatSpeed(_speed)}）"
            : "GIF 播放速度（这是静态图）";

        var items = new (MenuItem Item, double Speed)[]
        {
            (MenuItemGifSpeed025, 0.25),
            (MenuItemGifSpeed050, 0.5),
            (MenuItemGifSpeed075, 0.75),
            (MenuItemGifSpeed100, 1.0),
            (MenuItemGifSpeed125, 1.25),
            (MenuItemGifSpeed150, 1.5),
            (MenuItemGifSpeed200, 2.0),
            (MenuItemGifSpeed300, 3.0),
        };
        foreach (var (item, speed) in items)
        {
            item.IsChecked = Math.Abs(_speed - speed) < 0.001;
        }
    }

    private void OnMenuGifSpeedClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed)) return;
        _session.SetStickerSpeed(this, speed);
    }

    private void OnMenuGifSpeedResetClick(object sender, RoutedEventArgs e)
        => _session.SetStickerSpeed(this, _session.EnsureConfig().DefaultGifSpeedEffective);

    private void OnMenuClosed(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        if (!IsMouseOver) SetHoverChrome(false);
    }

    private void OnMenuTopmostClick(object sender, RoutedEventArgs e)
        => _session.SetTopmost(this, !_entry.Topmost);

    private void OnMenuMoveUpClick(object sender, RoutedEventArgs e) => _session.MoveStickerUp(this);

    private void OnMenuMoveDownClick(object sender, RoutedEventArgs e) => _session.MoveStickerDown(this);

    private void OnMenuMoveToTopClick(object sender, RoutedEventArgs e) => _session.MoveStickerToTop(this);

    private void OnMenuMoveToBottomClick(object sender, RoutedEventArgs e) => _session.MoveStickerToBottom(this);

    private void OnMenuResetZoomClick(object sender, RoutedEventArgs e)
        => _session.ResetStickerZoom(this);

    private void OnMenuCenterClick(object sender, RoutedEventArgs e)
    {
        CenterOnWorkArea();
        _menuOpen = false;
        SetHoverChrome(false);
    }

    private void OnMenuRevealClick(object sender, RoutedEventArgs e)
    {
        RevealImage();
        _menuOpen = false;
    }

    private void OnMenuOpenToolPageClick(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        try
        {
            if (Application.Current?.MainWindow is { } main)
            {
                if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                main.Activate();
            }
        }
        catch { /* 叫主窗口失败不影响贴纸 */ }
    }

    private void OnMenuCloseClick(object sender, RoutedEventArgs e)
    {
        _menuOpen = false;
        Close();
    }

    // ===================== 内部：把几何结果套到窗口 =====================

    private void ApplyBounds(StickerBounds bounds, bool save = true)
    {
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        _entry.Left = bounds.Left;
        _entry.Top = bounds.Top;
        _entry.Width = bounds.Width;
        _entry.Height = bounds.Height;
        if (save) _session.NotifyWindowGeometryChanged(this);
    }

    /// <summary>取鼠标屏幕坐标（DIP）：GetCursorPos 是物理像素，按窗口 DPI 换算成和 Left/Top 同一套坐标。</summary>
    private bool TryGetCursorDip(out Point point)
    {
        point = default;
        if (!GetCursorPos(out var native)) return false;

        var source = PresentationSource.FromVisual(this);
        var matrix = source?.CompositionTarget?.TransformFromDevice;
        point = matrix is { } transform
            ? transform.Transform(new Point(native.X, native.Y))
            : new Point(native.X, native.Y);
        return true;
    }

    // ===================== 调试截图 =====================

    private void ScheduleDebugCapture(string path)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(800);
            try
            {
                CaptureToPng(path);
            }
            catch (Exception ex)
            {
                try { await File.WriteAllTextAsync(path + ".err.txt", ex.ToString()); } catch { /* 忽略 */ }
            }
            try { Application.Current?.Shutdown(); } catch { /* 忽略 */ }
        }));
    }

    /// <summary>把窗口当前画面渲染成 PNG（透明底垫深色，方便看清图片边界）。</summary>
    private void CaptureToPng(string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C)), null,
                new Rect(0, 0, width, height));
        }
        bitmap.Render(background);
        if (Content is Visual content) bitmap.Render(content);
        bitmap.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ===================== Win32 =====================

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
