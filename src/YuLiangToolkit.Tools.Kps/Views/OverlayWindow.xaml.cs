using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace YuLiangToolkit.Tools.Kps.Views;

/// <summary>
/// 置顶透明 Overlay 窗口：显示 KPS 读数 + 键位网格（可再开折线图），可锁定成鼠标点击穿透。
///
/// <para><b>对外约定</b>：构造参数是 <see cref="KpsConfig"/>；<see cref="ApplyConfig"/> 刷新外观（不负责挪位置）；
/// <see cref="SetLocked"/> = true 时窗口鼠标穿透（<c>WS_EX_TRANSPARENT</c>），false 时可拖动 / 滚轮缩放；
/// 拖动 / 缩放结束后触发 <see cref="PlacementChanged"/>，由调用方把位置 / 缩放写回配置并保存。
/// <b>窗口自身不 Show、不依赖具体 ViewModel 类型</b>，DataContext 由调用方在 Show 之前设置。</para>
///
/// <para><b>点击穿透</b>：锁定后给 HWND 加 <c>WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>；
/// 解锁只去掉 TRANSPARENT，保留 NOACTIVATE / TOOLWINDOW —— 拖动时不会抢游戏 / 其它窗口的焦点，
/// 也不出现在 Alt+Tab / 任务栏里。⚠ 不要在 Loaded 前给窗口设 Owner：透明 + Topmost + NoActivate 组合
/// 设 Owner 容易把置顶层级 / 激活顺序搞乱。</para>
///
/// <para><b>缩放</b>：整体 <see cref="FrameworkElement.LayoutTransform"/> 用 <see cref="ScaleTransform"/>，
/// 窗口是 <c>SizeToContent=WidthAndHeight</c>，所以缩放后窗口尺寸跟着内容变；滚轮按
/// <see cref="KpsConfig.OverlayScaleAnchor"/> 固定锚点修正 Left/Top。</para>
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>缩放范围 / 步进（与配置说明一致）。</summary>
    private const double MinScale = 0.4;
    private const double MaxScale = 3.0;
    private const double ScaleStep = 0.05;

    /// <summary>首次显示时距主屏工作区右下角的边距（DIP）。</summary>
    private const double DefaultMargin = 40;

    /// <summary>窗口被拖出屏幕后至少保留的可见尺寸（DIP），保证还能用热键 / 鼠标抓回来。</summary>
    private const double MinVisibleWidth = 80;
    private const double MinVisibleHeight = 40;

    private KpsConfig _config;
    private bool _locked;
    private bool _dragging;
    private double _dragStartLeft;
    private double _dragStartTop;
    private Point _dragStartCursor;
    private bool _placementInitialized;
    private bool _placementScheduled;
    private int _placementAttempts;
    private int _centerAttempts;

    public OverlayWindow(KpsConfig config)
    {
        _config = config;
        _locked = config.OverlayLocked;
        InitializeComponent();

        // 拖出 / 释放鼠标捕获时结束拖动（Alt+Tab 切走等异常情况也能收尾）
        LostMouseCapture += (_, _) => FinishDrag();
        // 尺寸可能在加载过程中变（键位网格 / 绑定数据陆续就位），统一用 ApplicationIdle 延后到布局稳定再定位
        Loaded += (_, _) => SchedulePlacementInitialization();

        ApplyConfig(config);
    }

    /// <summary>拖动 / 滚轮缩放后触发（调用方负责把配置写回并保存）。</summary>
    public event EventHandler? PlacementChanged;

    // ============================== 公开 API ==============================

    /// <summary>配置变化后刷新窗口外观（背景 / 圆角 / 置顶 / 不透明度 / 缩放），不负责挪位置。</summary>
    public void ApplyConfig(KpsConfig config)
    {
        _config = config;
        _locked = config.OverlayLocked;

        Topmost = config.OverlayTopmost;
        Opacity = SafeClamp(config.OverlayOpacity, 0.1, 1.0, 1.0);
        Root.CornerRadius = new CornerRadius(SafeClamp(config.KeyCornerRadius, 0, 40, 12));
        Root.Background = KpsColor.ParseBrush(config.OverlayBackgroundColor, Colors.Transparent);

        // LayoutTransform 的 ScaleX/Y 在 XAML 里绑 ViewModel.OverlayScale；配置直改时同步当前值
        if (Root.LayoutTransform is ScaleTransform transform)
        {
            var scale = SafeClamp(config.OverlayScale, MinScale, MaxScale, 1.0);
            transform.SetCurrentValue(ScaleTransform.ScaleXProperty, scale);
            transform.SetCurrentValue(ScaleTransform.ScaleYProperty, scale);
        }

        if (IsLoaded) SetLocked(config.OverlayLocked);

        // 配置里有保存位置时在 Show 之前就摆好，避免窗口先在系统默认位置闪一下
        if (!_placementInitialized && config.OverlayLeft is { } savedLeft && config.OverlayTop is { } savedTop)
        {
            Left = savedLeft;
            Top = savedTop;
        }
    }

    /// <summary>锁定（点击穿透）/ 解锁（可拖动、滚轮缩放）。</summary>
    public void SetLocked(bool locked)
    {
        _locked = locked;
        if (locked && _dragging) FinishDrag();

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;   // 还没建 HWND：等 SourceInitialized 再应用

        var style = GetExtendedStyle(hwnd);
        style |= WsExNoActivate | WsExToolWindow;
        if (locked) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        SetExtendedStyle(hwnd, style);

        // 扩展样式变化不会自动刷新窗口框架，保险起见 SWP_FRAMECHANGED 一次（不移动 / 不激活 / 不改 Z 序）
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);

        // 未锁定时鼠标显示「可拖动」；锁定后穿透，光标交给下面的窗口
        if (locked) Root.ClearValue(CursorProperty);
        else Root.Cursor = Cursors.SizeAll;
    }

    /// <summary>
    /// 位置越界（换了显示器 / 分辨率）时把窗口拉回可见区域，至少留 80×40 可见。
    /// 窗口还没完成布局 / 首次定位时安全返回（内部会等布局稳定后再定位），不抛异常。
    /// </summary>
    public void EnsureOnScreen()
    {
        if (!_placementInitialized)
        {
            // 首次定位还没做：不要拿未稳定的尺寸硬算，等布局稳定（ApplicationIdle）后统一定位
            SchedulePlacementInitialization();
            return;
        }

        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (ClampToVisibleArea()) NotifyPlacement();
    }

    /// <summary>把窗口放回主屏工作区中央（首次显示 / 「居中」按钮用）。</summary>
    public void CenterOnScreen()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            // 还没布局：等布局完成再居中（最多重试几次，避免窗口没显示时死循环排队）
            if (++_centerAttempts <= 8)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(CenterOnScreen));
            return;
        }

        _centerAttempts = 0;
        _placementInitialized = true;   // 手动居中优先于首次默认定位，别让后者再挪走
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + (area.Height - ActualHeight) / 2;
        NotifyPlacement();
    }

    // ============================== 生命周期 / 定位 ==============================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 首次建 HWND 就应用锁定状态（NoActivate / ToolWindow 从第一帧起就生效，不抢焦点）
        SetLocked(_locked);

        // SizeToContent + 键位网格数据绑定要等布局 / 首帧渲染稳定才有真实尺寸，延后到 ApplicationIdle 再定位
        SchedulePlacementInitialization();
    }

    /// <summary>安排一次「布局稳定后的首次定位」（去重；ApplicationIdle 比 Loaded / Render 更晚，尺寸已稳定）。</summary>
    private void SchedulePlacementInitialization()
    {
        if (_placementInitialized || _placementScheduled) return;
        _placementScheduled = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            _placementScheduled = false;
            InitializePlacement();
        }));
    }

    /// <summary>首次定位：配置里有保存值就用保存值，否则主屏工作区右下角距边 40 DIP。</summary>
    private void InitializePlacement()
    {
        if (_placementInitialized) return;

        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            // SizeToContent 还没算出来：稍后再试（正常一两轮就够，不会无限排队）
            if (++_placementAttempts <= 8)
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(InitializePlacement));
            return;
        }

        _placementInitialized = true;
        var savedLeft = _config.OverlayLeft;
        var savedTop = _config.OverlayTop;

        if (savedLeft is { } left && savedTop is { } top)
        {
            Left = left;
            Top = top;
        }
        else
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - DefaultMargin;
            Top = area.Bottom - ActualHeight - DefaultMargin;
        }

        var clamped = ClampToVisibleArea();
        // 首次（没有保存值）或越界修正后写回配置，让调用方落盘
        if (savedLeft is null || savedTop is null || clamped) NotifyPlacement();
    }

    /// <summary>把窗口钳制在虚拟屏幕内（多显示器）；有移动返回 true。</summary>
    private bool ClampToVisibleArea()
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        var minLeft = screenLeft - ActualWidth + MinVisibleWidth;
        var maxLeft = screenRight - MinVisibleWidth;
        var minTop = screenTop - ActualHeight + MinVisibleHeight;
        var maxTop = screenBottom - MinVisibleHeight;

        var newLeft = ClampPosition(Left, minLeft, maxLeft, screenLeft);
        var newTop = ClampPosition(Top, minTop, maxTop, screenTop);

        if (Math.Abs(newLeft - Left) < 0.1 && Math.Abs(newTop - Top) < 0.1) return false;
        Left = newLeft;
        Top = newTop;
        return true;
    }

    /// <summary>把当前 Left/Top 写回配置并通知调用方（调用方负责保存）。</summary>
    private void NotifyPlacement()
    {
        if (!double.IsNaN(Left)) _config.OverlayLeft = Left;
        if (!double.IsNaN(Top)) _config.OverlayTop = Top;
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    // ============================== 拖动 / 滚轮缩放 ==============================

    private void OnOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked || e.ChangedButton != MouseButton.Left) return;
        if (!TryGetCursorPosition(out var cursor)) return;

        _dragging = true;
        _dragStartLeft = Left;
        _dragStartTop = Top;
        _dragStartCursor = cursor;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnOverlayMouseMove(object sender, MouseEventArgs e)
    {
        // ⚠ 锁定状态绝不能在 MouseMove 里挪窗口（数据绑定 / 布局也可能触发 MouseMove）
        if (!_dragging || _locked) return;
        if (!TryGetCursorPosition(out var cursor))
        {
            FinishDrag();
            return;
        }

        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X);
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y);
        e.Handled = true;
    }

    private void OnOverlayMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
        NotifyPlacement();
    }

    private void OnOverlayMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_locked) return;
        if (Root.LayoutTransform is not ScaleTransform transform) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        var current = transform.ScaleX;
        if (double.IsNaN(current) || current <= 0) current = SafeClamp(_config.OverlayScale, MinScale, MaxScale, 1.0);

        var next = Math.Round((current + (e.Delta > 0 ? ScaleStep : -ScaleStep)) * 100) / 100;
        var target = SafeClamp(next, MinScale, MaxScale, 1.0);
        e.Handled = true;
        if (Math.Abs(target - current) < 0.0001) return;

        // 缩放前后固定锚点：先按预测尺寸顶住，布局结束（SizeToContent 更新完）再按实际尺寸精修
        var (relX, relY) = GetScaleAnchor(_config.OverlayScaleAnchor);
        var width = ActualWidth;
        var height = ActualHeight;
        var anchorX = Left + relX * width;
        var anchorY = Top + relY * height;
        var ratio = target / current;

        transform.SetCurrentValue(ScaleTransform.ScaleXProperty, target);
        transform.SetCurrentValue(ScaleTransform.ScaleYProperty, target);

        Left = anchorX - relX * width * ratio;
        Top = anchorY - relY * height * ratio;
        _config.OverlayScale = target;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                Left = anchorX - relX * ActualWidth;
                Top = anchorY - relY * ActualHeight;
            }
            NotifyPlacement();
        }));
    }

    /// <summary>滚轮缩放时固定哪个位置：TopLeft / TopRight / BottomLeft / BottomRight / Center。</summary>
    private static (double X, double Y) GetScaleAnchor(string? anchor) => anchor?.Trim().ToLowerInvariant() switch
    {
        "topright" => (1, 0),
        "bottomleft" => (0, 1),
        "bottomright" => (1, 1),
        "center" => (0.5, 0.5),
        _ => (0, 0),
    };

    // ============================== Win32 辅助 ==============================

    /// <summary>取鼠标屏幕坐标（DIP）：GetCursorPos 是物理像素，按窗口当前 DPI 换算成和 Left/Top 同一坐标系。</summary>
    private bool TryGetCursorPosition(out Point point)
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

    private static long GetExtendedStyle(IntPtr hwnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64()
            : GetWindowLong32(hwnd, GwlExStyle);

    private static void SetExtendedStyle(IntPtr hwnd, long style)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(style));
        else SetWindowLong32(hwnd, GwlExStyle, unchecked((int)style));
    }

    /// <summary>夹到合法范围；非法值（NaN / 无穷）用兜底值。</summary>
    private static double SafeClamp(double value, double min, double max, double fallback)
        => double.IsNaN(value) || double.IsInfinity(value) ? fallback : Math.Clamp(value, min, max);

    /// <summary>坐标夹取：min &gt; max（虚拟屏异常小）或 NaN 时用兜底值，绝不抛异常。</summary>
    private static double ClampPosition(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return fallback;
        if (min > max) return fallback;
        return Math.Clamp(value, min, max);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}

/// <summary>
/// Overlay 统计行的转换器：把「是否显示 + 文本」三组绑定拼成 <c>总计 1,234 · 峰值 12 · 平均 8</c>，
/// 同一次输出若目标类型是 <see cref="Visibility"/> 则返回「一个都不显示就折叠」。
/// </summary>
public sealed class OverlayStatsConverter : IMultiValueConverter
{
    /// <summary>绑定顺序：showTotal, totalText, showPeak, peakText, showAvg, avgText。</summary>
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = new List<string>(3);
        if (values.Length >= 6)
        {
            AddPart(parts, "总计", values[0], values[1]);
            AddPart(parts, "峰值", values[2], values[3]);
            AddPart(parts, "平均", values[4], values[5]);
        }

        if (targetType == typeof(Visibility))
            return parts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        return string.Join(" · ", parts);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static void AddPart(List<string> parts, string label, object? flag, object? value)
    {
        if (flag is not bool show || !show) return;

        var text = ReferenceEquals(value, DependencyProperty.UnsetValue) ? "" : value?.ToString() ?? "";
        if (text.Length == 0) text = "0";
        parts.Add(label + " " + text);
    }
}
