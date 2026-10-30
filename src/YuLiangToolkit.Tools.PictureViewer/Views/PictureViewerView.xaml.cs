using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PictureViewer.ViewModels;

namespace YuLiangToolkit.Tools.PictureViewer.Views;

/// <summary>
/// 「图片连续阅读器」的界面后台。
///
/// <para>这个工具的核心就是「一长条图片 + 自己接管滚动」，所以代码后置比一般功能重一些，分工仍然是清楚的：</para>
/// <list type="bullet">
///   <item><b>纯逻辑</b>（滚多少 / 往哪滚 / 中键状态机 / 页序 / 排版尺寸 / 阅读偏移换算）全在
///         <see cref="PictureScrollMath"/>、<see cref="PictureAnchorMachine"/>、
///         <see cref="PictureScanner"/> 里，自检直接断言；</item>
///   <item><b>这里</b>只做必须碰界面的部分：造 Image/Border、按视口算尺寸、把 ScrollViewer 的
///         物理偏移换成阅读偏移、可见范围解码 / 离屏释放、鼠标与键盘事件；</item>
///   <item><b>ViewModel</b> 只放设置与页清单。</item>
/// </list>
///
/// <para><b>「阅读偏移」</b>：0 = 第一张图开头，最大值 = 最后一张图末尾。日式右→左模式下图片是
/// **物理反着摆**的（第一张在最右边），ScrollViewer 的物理偏移 0 在最左边 —— 两者靠
/// <see cref="PictureScrollMath.ReadingOffset"/> / <see cref="PictureScrollMath.PhysicalHorizontalOffset"/> 换算，
/// 所以滚轮 / 拖拽 / 中键三种方式在三种模式里只有一套逻辑（见 docs/dev/pitfalls.md）。</para>
/// </summary>
public partial class PictureViewerView : UserControl
{
    // ---------------------------------------------------------------- 一页的界面对象

    private sealed class PageSlot
    {
        public PicturePage Page = null!;
        /// <summary>展示用的外层控件：有边框时是「外边框 → 填充 → 内边框 → Image」的嵌套 Border，没边框时直接是 Image。</summary>
        public FrameworkElement Container = null!;
        public Image ImageControl = null!;
        /// <summary>主轴长度：纵向模式是高度，横向模式是宽度。</summary>
        public double MainLength;
        /// <summary>在**阅读方向**上的起点（与物理摆放无关，日式右→左也照样从 0 累加）。</summary>
        public double Start;
        public volatile bool IsBitmapLoaded;
        public volatile bool IsLoading;
        /// <summary>解码失败过（坏图 / 文件被删）→ 不再重复入队，省得每滚一下都白试一次。</summary>
        public volatile bool LoadFailed;
        public DateTime LastAccessTime;
    }

    private readonly List<PageSlot> _slots = new();

    // 图片解码：最多 4 张并行；排队等可见范围（条目自带 generation，换过文件夹/方向的旧条目直接丢）
    private readonly SemaphoreSlim _loadSemaphore = new(4, 4);
    private readonly Queue<(PageSlot Slot, int DecodeWidth, int Generation)> _pendingLoads = new();
    private readonly object _pendingLock = new();
    private bool _draining;
    private int _generation;

    // 重建状态：重建途中又来的请求排队重跑（不能静默丢，否则长带会停在旧页/旧方向）
    private bool _rebuilding;
    private bool _rebuildQueued;
    private bool _rebuildQueuedKeepProgress;
    private PictureScrollMode _builtMode = PictureScrollMode.Vertical;

    private bool _layoutQueued;
    private bool _demoStarted;
    private bool _measureScheduled;
    private DispatcherTimer? _borderRebuildTimer;
    private double _maxPixelWidth;
    private double _maxPixelHeight;
    private double _lastCross;
    private int _decodeFailureCount;

    // 「阅读偏移」的唯一真值。⚠ 不要每次从 ScrollViewer 的物理偏移反读：
    // WPF 的 ScrollToXxx 是**排队到布局阶段**才真正生效的，连着滚轮 / 锚点每帧滚时反读会拿到旧值、
    // 把每一下的位移都丢掉（踩过）。这里自己记，只在我们 Set 时改；ScrollViewer 只负责照着显示。
    private double _readingOffset;

    /// <summary>跨轴偏移：纵向模式是水平偏移（缩放后可左右拖），横向模式是垂直偏移。同样由我们自己记。</summary>
    private double _crossOffset;

    // 拖动惯性：松手后按甩动速度继续滑一段（设置里可关）。速度采样与衰减在纯逻辑 PictureInertia 里。
    private readonly PictureInertia _inertia = new();
    private DispatcherTimer? _inertiaTimer;
    private double _inertiaMainVelocity;
    private double _inertiaCrossVelocity;
    private long _inertiaLastTimestamp;

    // 「还在滑的残余速度」：下一次拖拽开始时先存下来，松手时叠加（手机漫画 App 的“越滑越快”）。
    private double _carriedMainVelocity;
    private double _carriedCrossVelocity;

    // 滚动后的加载防抖（一次滚轮 / 拖拽会触发一堆 ScrollChanged）
    private DispatcherTimer? _scrollSettleTimer;
    private bool _needsLoadAfterSettle;

    // 中键锚点
    private readonly PictureAnchorMachine _anchor = new();
    private DispatcherTimer? _anchorTimer;
    private Point _anchorPoint;

    // 左键拖拽
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStart;
    private double _dragStartReading;
    private double _dragStartCross;
    private double _dragStartPanX;
    private double _dragStartPanY;
    private bool _releasingMouse;

    private Window? _hostWindow;

    /// <summary>宿主窗口（外壳）的全屏开关；视图卸载时就置空（别再往上找 Window，那时已经摘下来了）。</summary>
    private IFullscreenHost? _fullscreenHost;

    /// <summary>全屏时鼠标不动多久就把工具条 + 鼠标指针一起藏起来。</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2.5);
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _fullscreenHintTimer;

    /// <summary>开发排错用：<c>YLT_PICTURE_INPUT_TRACE=文件</c> 时把每次鼠标按下 / 松开 / 丢捕获写进去。</summary>
    private string? _inputTracePath;

    public PictureViewerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewMouseWheel += OnPreviewMouseWheel;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewMouseUp += OnPreviewMouseUp;
        PreviewMouseMove += OnPreviewMouseMove;
        LostMouseCapture += OnLostMouseCapture;
        ViewportGrid.SizeChanged += OnViewportSizeChanged;
    }

    private PictureViewerViewModel? Vm => DataContext as PictureViewerViewModel;

    private PictureScrollMode Mode => Vm?.Cfg.ScrollModeEffective ?? PictureScrollMode.Vertical;

    private bool IsHorizontalMode => PictureScrollModes.IsHorizontal(Mode);

    /// <summary>当前 DPI 缩放：解码宽度要按**设备像素**算，否则 125% / 150% 缩放下图会糊。</summary>
    private double DpiScale
    {
        get
        {
            try
            {
                var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                return scale > 0 ? scale : 1.0;
            }
            catch
            {
                return 1.0;
            }
        }
    }

    // ---------------------------------------------------------------- 生命周期

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewKeyDown += OnHostPreviewKeyDown;
            _hostWindow.PreviewMouseDown -= OnHostPreviewMouseDownTrace;
            _hostWindow.PreviewMouseDown += OnHostPreviewMouseDownTrace;
        }

        var tracePath = Environment.GetEnvironmentVariable("YLT_PICTURE_INPUT_TRACE");
        if (!string.IsNullOrWhiteSpace(tracePath))
        {
            try
            {
                File.WriteAllText(tracePath, "");
                _inputTracePath = tracePath;
            }
            catch
            {
                _inputTracePath = null;
            }
        }

        vm.FolderPicker = PickFolder;
        vm.PagesChanged -= OnPagesChanged;
        vm.PagesChanged += OnPagesChanged;
        vm.ModeChanged -= OnModeChanged;
        vm.ModeChanged += OnModeChanged;
        vm.ZoomChanged -= OnZoomChanged;
        vm.ZoomChanged += OnZoomChanged;
        vm.BorderChanged -= OnBorderChanged;
        vm.BorderChanged += OnBorderChanged;

        // 全屏：ViewModel 只切状态，真正的窗口操作交给外壳（MainWindow 实现了 IFullscreenHost）
        _fullscreenHost = Window.GetWindow(this) as IFullscreenHost;
        vm.FullscreenChanged -= OnFullscreenChanged;
        vm.FullscreenChanged += OnFullscreenChanged;
        if (vm.IsFullscreen) OnFullscreenChanged(vm, true);   // 截图 / 视图重建时同步一次

        // 双击图片启动（文件关联 / 命令行带了一张图）：先加载它所在的文件夹，再跳到那一张。
        var launchRequest = vm.HasLaunchImage;
        if (launchRequest)
        {
            await vm.LoadLaunchImageAsync();
            await ApplyReadingModeAsync(keepProgress: true);
        }
        else if (vm.HasPages)
        {
            await ApplyReadingModeAsync(keepProgress: true);
        }

        // 演示图片只在「没通过命令行指定图片」时加载，免得把用户双击的那张替换掉
        if (!launchRequest && !_demoStarted)
        {
            _demoStarted = true;
            await vm.TryStartDemoAsync();
        }

        ApplyFullscreenDebugHooks();
    }

    /// <summary>
    /// 把长带滚动到第 index 张的**开头**（双击图片打开时用）。
    /// 普通方向直接取该页主轴起点；日式右→左用「总长 − 该页末尾」换算成阅读偏移。
    /// </summary>
    private void ScrollToPage(int index)
    {
        if (index < 0 || index >= _slots.Count || _slots.Count == 0) return;

        var totalLength = _slots[^1].Start + _slots[^1].MainLength;
        var slot = _slots[index];
        var reading = PictureScrollMath.ReadingOffsetForPage(
            PictureScrollModes.IsReverse(Mode), totalLength, slot.Start, slot.MainLength);

        SetReadingOffset(reading);
        UpdateReadingProgress();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewMouseDown -= OnHostPreviewMouseDownTrace;
        }
        _hostWindow = null;

        _idleTimer?.Stop();
        _idleTimer = null;
        _fullscreenHintTimer?.Stop();
        _fullscreenHintTimer = null;
        Cursor = null;

        ExitAnchor();
        if (_dragging) EndDrag();
        ExitPagedMode();
        AbortInertia();

        _scrollSettleTimer?.Stop();
        _anchorTimer?.Stop();
        _borderRebuildTimer?.Stop();
        _borderRebuildTimer = null;

        _generation++;
        lock (_pendingLock) _pendingLoads.Clear();

        if (Vm is { } vm)
        {
            // ⚠ 顺序不能反：先 ExitFullscreen（此时还订阅着，事件能传出去让外壳还原窗口），再摘事件。
            // 反过来的话外壳会一直停在“藏了壳”的全屏状态，切到别的页面就没有标题栏了。
            vm.ExitFullscreen();
            vm.FullscreenChanged -= OnFullscreenChanged;

            vm.PagesChanged -= OnPagesChanged;
            vm.ModeChanged -= OnModeChanged;
            vm.ZoomChanged -= OnZoomChanged;
            vm.BorderChanged -= OnBorderChanged;
            vm.FolderPicker = null;
        }
        _fullscreenHost = null;

        ClearSlots();
    }

    private void OnPagesChanged(object? sender, EventArgs e) => _ = ApplyReadingModeAsync(keepProgress: false);

    private void OnModeChanged(object? sender, EventArgs e) => _ = ApplyReadingModeAsync(keepProgress: true);

    /// <summary>边框设置变了：节流 180ms 再重排（拖粗细滑块 / 连打颜色数字时不要每一格都重建整条长带）。</summary>
    private void OnBorderChanged(object? sender, EventArgs e)
    {
        if (_borderRebuildTimer is null)
        {
            _borderRebuildTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180),
            };
            _borderRebuildTimer.Tick += (_, _) =>
            {
                _borderRebuildTimer!.Stop();
                RebuildForBorderChange();
            };
        }

        _borderRebuildTimer.Stop();
        _borderRebuildTimer.Start();
    }

    private void RebuildForBorderChange()
    {
        if (Vm is not { } vm || !vm.HasPages) return;
        if (_pagedActive) _ = ShowPagedAsync(resetPan: false);
        else _ = BuildStripAsync(keepProgress: true);
    }

    private string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择包含图片的文件夹" };
        var last = Vm?.Cfg.LastFolder;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last)) dialog.InitialDirectory = last;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    // ---------------------------------------------------------------- 搭长带

    /// <summary>
    /// 按当前页清单 / 滚动方式重建长带。
    /// <paramref name="keepProgress"/> = true 时尽量停在原来的阅读位置（切换滚动方式）。
    /// </summary>
    private async Task BuildStripAsync(bool keepProgress, double? forcedRatio = null)
    {
        if (Vm is not { } vm) return;

        // 重建途中又来了请求（连着换方向 / 换文件夹）：记下来，等这次建完补跑一次。
        // 静默丢掉会让「长带停在旧方向、配置已经是新方向」——之后所有滚动都按新轴算旧布局（见 docs/dev/pitfalls.md）。
        if (_rebuilding)
        {
            _rebuildQueued = true;
            _rebuildQueuedKeepProgress |= keepProgress;
            return;
        }

        _rebuilding = true;
        try
        {
            // ⚠ 比例必须用「上一次真正建成的方向」来算：ModeChanged 是在 Cfg.ScrollMode 改完之后才发的，
            // 这时按新方向去读旧布局的偏移（轴都不一样）会永远得到 0，换来换去就跳回第一张。
            var ratio = forcedRatio ?? (keepProgress ? GetReadingRatioForMode(_builtMode) : 0);

            // 先把上一批整掉（生成号变了，还在解的那几张回来也会被丢掉）
            _generation++;
            var generation = _generation;
            lock (_pendingLock) _pendingLoads.Clear();
            ClearSlots();
            _decodeFailureCount = 0;

            var horizontal = PictureScrollModes.IsHorizontal(vm.Cfg.ScrollModeEffective);
            var reverse = PictureScrollModes.IsReverse(vm.Cfg.ScrollModeEffective);

            ImageStrip.Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical;
            ImageStrip.HorizontalAlignment = reverse ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            ImageStrip.Margin = new Thickness(0);
            ImageScrollViewer.Padding = new Thickness(0);
            // 滚动条藏起来：三种方向都靠滚轮 / 拖拽 / 中键，底部那条细进度条负责「读到哪了」
            ImageScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ImageScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
            ImageScrollViewer.FlowDirection = FlowDirection.LeftToRight;

            _maxPixelWidth = 0;
            _maxPixelHeight = 0;
            foreach (var page in vm.Pages)
            {
                _maxPixelWidth = Math.Max(_maxPixelWidth, page.PixelWidth);
                _maxPixelHeight = Math.Max(_maxPixelHeight, page.PixelHeight);
            }

            foreach (var page in vm.Pages)
            {
                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.Both,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Margin = new Thickness(0),
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

                var container = WrapImageWithFrame(image);

                _slots.Add(new PageSlot { Page = page, Container = container, ImageControl = image });
            }

            // 物理摆放：日式右→左时反着加，让第 1 张摆在最右边（阅读从右往左）
            if (reverse)
            {
                for (var i = _slots.Count - 1; i >= 0; i--) ImageStrip.Children.Add(_slots[i].Container);
            }
            else
            {
                foreach (var slot in _slots) ImageStrip.Children.Add(slot.Container);
            }

            await Dispatcher.Yield(DispatcherPriority.Loaded);
            if (generation != _generation) return;

            AbortInertia();
            if (RelayoutItems())
            {
                SetReadingOffset(PictureScrollMath.OffsetForRatio(ratio, GetReadingMaximum()));
                SetCrossOffset(0);
            }
            _builtMode = vm.Cfg.ScrollModeEffective;
            UpdateReadingProgress();
            ProcessVisibleRange();
            ApplyDebugHooks();

            // 双击图片启动：长带搭好后直接滚到指定的那一张（只消费一次，换模式不会反复跳）
            if (vm.ConsumePendingPageIndex() is { } launchPage)
            {
                ScrollToPage(launchPage);
            }
        }
        finally
        {
            _rebuilding = false;

            if (_rebuildQueued)
            {
                _rebuildQueued = false;
                var again = _rebuildQueuedKeepProgress;
                _rebuildQueuedKeepProgress = false;
                _ = BuildStripAsync(again);
            }
        }
    }

    /// <summary>图片统一缩放后的跨轴尺寸（纵向 = 统一宽度，横向 = 统一高度），单位是 DIP；已含缩放百分比。</summary>
    private double ComputeCrossSize(double viewportCross)
    {
        // 边框会占掉视口：100% 时「图 + 边框」刚好铺满一屏（不把图挤出视口）
        var extra = Vm?.BorderStyle.TotalExtra ?? 0;
        var usable = Math.Max(1.0, viewportCross - extra);
        var maxPixelCross = IsHorizontalMode ? _maxPixelHeight : _maxPixelWidth;
        var fit = PictureScrollMath.UniformCrossSize(maxPixelCross / DpiScale, usable);
        return fit * (Vm?.Cfg.ZoomScale ?? 1.0);
    }

    /// <summary>
    /// 给一张图套上「内边框 → 填充 → 外边框」三层边框（没开边框 / 三层都是 0 时直接返回图片本身）。
    /// 尺寸由调用方给外层容器 / Canvas 定位决定：外层每边多出 <see cref="PictureBorderStyle.TotalExtra"/>。
    /// </summary>
    private FrameworkElement WrapImageWithFrame(Image image)
    {
        var style = Vm?.BorderStyle ?? PictureBorderStyle.Default;
        if (!style.HasAnyBorder) return image;

        var inner = new Border
        {
            BorderThickness = new Thickness(style.InnerSize),
            BorderBrush = CreateArgbBrush(style.InnerColor),
            Child = image,
        };
        var fill = new Border
        {
            Background = CreateArgbBrush(style.FillColor),
            Padding = new Thickness(style.FillSize),
            Child = inner,
        };
        return new Border
        {
            BorderThickness = new Thickness(style.OuterSize),
            BorderBrush = CreateArgbBrush(style.OuterColor),
            Child = fill,
        };
    }

    private static SolidColorBrush CreateArgbBrush(string hex)
    {
        if (!PictureBorderStyle.TryParseArgb(hex, out var argb)) argb = 0xFF000000;
        var (r, g, b, a) = PictureBorderStyle.Split(argb);
        var brush = new SolidColorBrush(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 按当前缩放 / 视口重算每张图的尺寸（宽高都显式给），**不动偏移** ——
    /// 定位交给调用方：重建按阅读比例、窗口缩放按绝对偏移、改缩放按画面中心。
    /// </summary>
    private bool RelayoutItems()
    {
        if (_slots.Count == 0) return false;

        var horizontal = IsHorizontalMode;
        var viewportCross = horizontal ? ViewportGrid.ActualHeight : ViewportGrid.ActualWidth;
        if (viewportCross <= 1) return false;

        var cross = ComputeCrossSize(viewportCross);
        if (cross <= 0) return false;

        // 每张图的「图 + 边框」总尺寸：主轴 / 跨轴都加上边框（每边 extra）
        var extra = Vm?.BorderStyle.TotalExtra ?? 0;
        var start = 0.0;
        foreach (var slot in _slots)
        {
            var main = PictureScrollMath.MainLengthForAspect(
                horizontal, slot.Page.PixelWidth, slot.Page.PixelHeight, cross);
            if (main <= 0) main = cross;

            slot.MainLength = main + extra;
            slot.Start = start;
            start += slot.MainLength;

            // ⚠ 宽高都要显式给：缩放后**另一个轴**也得能滚。只给一个轴、另一个靠拉伸的话，
            // 内容宽 / 高还是视口那么大，ScrollViewer 会认为“没得滚”，缩放后另一个方向就拖不动了。
            slot.Container.Width = horizontal ? slot.MainLength : cross + extra;
            slot.Container.Height = horizontal ? cross + extra : slot.MainLength;
        }

        _lastCross = cross;
        ImageScrollViewer.UpdateLayout();
        return true;
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pagedActive)
        {
            if (_layoutQueued) return;
            _layoutQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _layoutQueued = false;
                if (_pagedActive) _ = RelayoutPagedKeepCenterAsync();
            }), DispatcherPriority.Background);
            return;
        }

        if (_slots.Count == 0 || _layoutQueued) return;

        // 图片按视口跨轴缩放，所以内容长度也跟着按比例变：按「偏移 × 新老缩放比」定位最跟手；
        // 只用可滚范围的比例会在窗口一拉高时跳半页（复核报告里的 P2）。
        var reading = GetReadingOffset();
        var cross = GetCrossOffset();
        var viewportCross = IsHorizontalMode ? e.NewSize.Height : e.NewSize.Width;
        var newCross = ComputeCrossSize(viewportCross);
        var scale = _lastCross > 0 && newCross > 0 ? newCross / _lastCross : 0;

        // 窗口拖拽时会连续触发：排到 Background 再量一次，避免每一帧都重排
        _layoutQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _layoutQueued = false;
            if (_slots.Count == 0) return;
            if (!RelayoutItems()) return;
            if (scale > 0)
            {
                SetReadingOffset(reading * scale);
                SetCrossOffset(cross * scale);
            }
            UpdateReadingProgress();
            ProcessVisibleRange();
        }), DispatcherPriority.Background);
    }

    private void ClearSlots()
    {
        foreach (var slot in _slots)
        {
            slot.ImageControl.Source = null;
            slot.IsBitmapLoaded = false;
            slot.IsLoading = false;
        }
        _slots.Clear();
        ImageStrip.Children.Clear();
        _lastCross = 0;
        _readingOffset = 0;
        _crossOffset = 0;
        AbortInertia();
    }

    // ---------------------------------------------------------------- 阅读偏移 ↔ ScrollViewer 偏移

    private double GetReadingMaximum()
        => PictureScrollMath.ReadingMaximum(
            Mode, ImageScrollViewer.ScrollableWidth, ImageScrollViewer.ScrollableHeight);

    private double GetReadingViewportLength()
        => IsHorizontalMode ? ImageScrollViewer.ViewportWidth : ImageScrollViewer.ViewportHeight;

    /// <summary>当前阅读偏移（0 = 第一张开头）。日式右→左是「总长 − 物理水平偏移」。</summary>
    private double GetReadingOffset() => PictureScrollMath.ClampOffset(_readingOffset, GetReadingMaximum());

    /// <summary>按**某一种**滚动方式算阅读比例（换方向时要用旧方向的总长，见 BuildStripAsync）。</summary>
    private double GetReadingRatioForMode(PictureScrollMode mode)
    {
        var max = PictureScrollMath.ReadingMaximum(
            mode, ImageScrollViewer.ScrollableWidth, ImageScrollViewer.ScrollableHeight);
        return PictureScrollMath.RatioOf(_readingOffset, max);
    }

    /// <summary>改阅读偏移；日式右→左时换算成「从右边量」的物理偏移。</summary>
    private void SetReadingOffset(double readingOffset)
    {
        var max = GetReadingMaximum();
        _readingOffset = PictureScrollMath.ClampOffset(readingOffset, max);

        if (IsHorizontalMode)
        {
            ImageScrollViewer.ScrollToHorizontalOffset(
                PictureScrollMath.PhysicalHorizontalOffset(Mode, _readingOffset, ImageScrollViewer.ScrollableWidth));
        }
        else
        {
            ImageScrollViewer.ScrollToVerticalOffset(_readingOffset);
        }
    }

    private double GetReadingRatio() => PictureScrollMath.RatioOf(GetReadingOffset(), GetReadingMaximum());

    // ---------------------------------------------------------------- 跨轴偏移（缩放后另一个方向）

    private double GetCrossMaximum()
        => IsHorizontalMode ? ImageScrollViewer.ScrollableHeight : ImageScrollViewer.ScrollableWidth;

    private double GetCrossViewportLength()
        => IsHorizontalMode ? ImageScrollViewer.ViewportHeight : ImageScrollViewer.ViewportWidth;

    /// <summary>当前「跨轴偏移」：纵向模式是水平偏移，横向模式是垂直偏移。</summary>
    private double GetCrossOffset() => PictureScrollMath.ClampOffset(_crossOffset, GetCrossMaximum());

    private void SetCrossOffset(double crossOffset)
    {
        var max = GetCrossMaximum();
        _crossOffset = PictureScrollMath.ClampOffset(crossOffset, max);

        if (IsHorizontalMode) ImageScrollViewer.ScrollToVerticalOffset(_crossOffset);
        else ImageScrollViewer.ScrollToHorizontalOffset(_crossOffset);
    }

    // ---------------------------------------------------------------- 缩放

    private void OnZoomChanged(object? sender, EventArgs e)
    {
        if (_pagedActive) _ = RelayoutPagedKeepCenterAsync();
        else ApplyZoomChange();
    }

    /// <summary>缩放变了：重新排版，并尽量让**画面中心**停在原来那张图的同一处（像手机漫画 App 那样）。</summary>
    private void ApplyZoomChange()
    {
        if (_slots.Count == 0)
        {
            UpdateReadingProgress();
            return;
        }

        var mainViewport = GetReadingViewportLength();
        var crossViewport = GetCrossViewportLength();
        var mainContent = GetReadingMaximum() + mainViewport;
        var crossContent = GetCrossMaximum() + crossViewport;
        var mainCenter = mainContent > 0 ? (GetReadingOffset() + mainViewport / 2) / mainContent : 0.5;
        var crossCenter = crossContent > 0 ? (GetCrossOffset() + crossViewport / 2) / crossContent : 0.5;

        AbortInertia();
        if (!RelayoutItems()) return;

        var newMainViewport = GetReadingViewportLength();
        var newCrossViewport = GetCrossViewportLength();
        var newMainContent = GetReadingMaximum() + newMainViewport;
        var newCrossContent = GetCrossMaximum() + newCrossViewport;

        SetReadingOffset(mainCenter * newMainContent - newMainViewport / 2);
        SetCrossOffset(crossCenter * newCrossContent - newCrossViewport / 2);
        UpdateReadingProgress();
        ProcessVisibleRange();
    }

    private void UpdateReadingProgress()
    {
        if (_pagedActive)
        {
            UpdatePagedProgress();
            return;
        }

        var ratio = Math.Clamp(GetReadingRatio(), 0, 1);

        // 日式右→左是往左读，进度条也从右边长出来
        ReadingProgressFill.HorizontalAlignment = PictureScrollModes.IsReverse(Mode)
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        ReadingProgressFill.Width = ratio * Math.Max(0, ViewportGrid.ActualWidth);
    }

    // ---------------------------------------------------------------- 可见范围：解码 / 释放

    private void OnImageScrollViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        UpdateReadingProgress();
        ScheduleVisibleLoad();
    }

    /// <summary>排到 150ms 后补一次可见范围。**已经在计时就不重置** —— 见方法里的说明。</summary>
    private void ScheduleVisibleLoad()
    {
        _needsLoadAfterSettle = true;

        if (_scrollSettleTimer is null)
        {
            _scrollSettleTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _scrollSettleTimer.Tick += (_, _) =>
            {
                _scrollSettleTimer?.Stop();
                if (!_needsLoadAfterSettle) return;
                _needsLoadAfterSettle = false;
                ProcessVisibleRange();
            };
        }

        // ⚠ 不能每次 Stop();Start();：连续滚轮 / 拖拽 / 锚点自动滚动时 ScrollChanged 每帧都来，
        // 一旦重置就永远等不到到期时刻，移动中一张图都不会补（复核发现的 P0）。
        if (!_scrollSettleTimer.IsEnabled) _scrollSettleTimer.Start();
    }

    /// <summary>把可见范围（含前后各一屏缓冲）里的图排进解码队列，离屏太久的释放掉。</summary>
    private void ProcessVisibleRange()
    {
        if (Vm is not { } vm || _slots.Count == 0) return;

        var viewportLength = GetReadingViewportLength();
        if (viewportLength <= 0) return;

        var horizontal = IsHorizontalMode;
        var offset = GetReadingOffset();
        var buffer = viewportLength * 2;
        var from = offset - buffer;
        var to = offset + viewportLength + buffer;

        var now = DateTime.UtcNow;
        var grace = TimeSpan.FromSeconds(3);
        var autoUnload = vm.Cfg.AutoUnloadOffscreen;
        var dpi = DpiScale;

        // 跨轴显示尺寸（含缩放百分比）：纵向模式 = 统一宽度，横向模式按每张自己的显示宽度
        var uniformDips = ComputeCrossSize(IsHorizontalMode ? ViewportGrid.ActualHeight : ViewportGrid.ActualWidth);
        var uniformWidth = (int)Math.Ceiling(Math.Max(1, uniformDips) * dpi);

        foreach (var slot in _slots)
        {
            if (slot.MainLength <= 0) continue;

            var visible = slot.Start + slot.MainLength >= from && slot.Start <= to;
            if (visible)
            {
                slot.LastAccessTime = now;
                if (!slot.IsBitmapLoaded && !slot.IsLoading && !slot.LoadFailed)
                {
                    var displayDips = horizontal ? slot.MainLength : uniformDips;
                    var displayWidth = (int)Math.Ceiling(Math.Max(1, displayDips) * dpi);
                    if (slot.Page.PixelWidth > 0) displayWidth = Math.Min(displayWidth, slot.Page.PixelWidth);
                    EnqueueLoad(slot, displayWidth);
                }
            }
            else if (autoUnload && slot.IsBitmapLoaded && now - slot.LastAccessTime > grace)
            {
                UnloadSlot(slot);
            }
        }
    }

    private void EnqueueLoad(PageSlot slot, int decodeWidth)
    {
        slot.IsLoading = true;
        lock (_pendingLock) _pendingLoads.Enqueue((slot, decodeWidth, _generation));
        if (_draining) return;

        _draining = true;
        _ = DrainQueueAsync();
    }

    /// <summary>一个 worker 排空队列（不再每次入队都新起一个），条目自带 generation，旧代的直接丢掉。</summary>
    private async Task DrainQueueAsync()
    {
        try
        {
            while (true)
            {
                (PageSlot Slot, int DecodeWidth, int Generation) item;
                lock (_pendingLock)
                {
                    if (_pendingLoads.Count == 0) break;
                    item = _pendingLoads.Dequeue();
                }

                await _loadSemaphore.WaitAsync();
                try
                {
                    // 换过文件夹 / 方向的旧槽位已经不在界面上了；新一代的条目用自己的 generation 解码
                    if (item.Generation != _generation || !_slots.Contains(item.Slot))
                    {
                        item.Slot.IsLoading = false;
                        continue;
                    }
                    await LoadOneAsync(item.Slot, item.DecodeWidth, item.Generation);
                }
                finally
                {
                    _loadSemaphore.Release();
                }
            }
        }
        finally
        {
            _draining = false;
        }
    }

    private async Task LoadOneAsync(PageSlot slot, int decodeWidth, int generation)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(slot.Page.FilePath);
                image.DecodePixelWidth = Math.Max(1, decodeWidth);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            });

            if (generation != _generation) return;

            slot.ImageControl.Source = bitmap;
            slot.IsBitmapLoaded = true;
            slot.LastAccessTime = DateTime.UtcNow;
        }
        catch
        {
            if (generation != _generation) return;

            // 坏图 / 读文件时就被人删了：标记失败（以后不再白试），并告诉用户一声
            slot.LoadFailed = true;
            _decodeFailureCount++;
            if (Vm is { } vm) vm.StatusText = $"{_decodeFailureCount} 张图片无法解码（已跳过）";
        }
        finally
        {
            slot.IsLoading = false;
        }
    }

    private void UnloadSlot(PageSlot slot)
    {
        if (!slot.IsBitmapLoaded) return;
        slot.ImageControl.Source = null;
        slot.IsBitmapLoaded = false;
    }

    // ---------------------------------------------------------------- 滚轮

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm || !vm.HasPages) return;                       // 没图时不吃滚轮
        if (Mouse.Captured is not null && !ReferenceEquals(Mouse.Captured, this)) return;   // 别的控件（输入框 / 下拉弹层）正掌握鼠标
        if (!IsFromViewport(e.OriginalSource as DependencyObject)) return;  // 鼠标在工具条 / 设置面板上时别抢滚轮
        if (IsInsideSettings(e.OriginalSource as DependencyObject)) return;

        if (_pagedActive)
        {
            AbortInertia();
            e.Handled = true;

            var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (ctrl || (!shift && vm.Cfg.PagedWheelEffective == PictureWheelAction.Zoom))
            {
                if (e.Delta > 0) vm.ZoomInCommand.Execute(null);
                else if (e.Delta < 0) vm.ZoomOutCommand.Execute(null);
                return;
            }

            if (shift)
            {
                var panStep = vm.Cfg.WheelStepEffective;
                PanPaged(_pagedPanX + (e.Delta < 0 ? panStep : -panStep), _pagedPanY);
                return;
            }

            // 默认：滚轮往下 = 下一张（按阅读方向，日式模式同样是“往后读”）
            _ = TurnPageAsync(forward: e.Delta < 0);
            return;
        }

        AbortInertia();
        if (_anchor.IsActive) ExitAnchor();

        e.Handled = true;

        // Ctrl+滚轮 = 缩放（以画面中心为基准）；Shift+滚轮 = 往另一个轴平移；否则沿阅读方向滚
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Delta > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta < 0) vm.ZoomOutCommand.Execute(null);
            return;
        }

        var step = vm.Cfg.WheelStepEffective;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            SetCrossOffset(PictureScrollMath.WheelOffset(GetCrossOffset(), e.Delta, step, GetCrossMaximum()));
            return;
        }

        SetReadingOffset(PictureScrollMath.WheelOffset(GetReadingOffset(), e.Delta, step, GetReadingMaximum()));
    }

    // ---------------------------------------------------------------- 鼠标：中键锚点 / 左键拖拽

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm) return;
        var source = e.OriginalSource as DependencyObject;
        TraceInput($"View.PreviewMouseDown {e.ChangedButton} src={(source as FrameworkElement)?.GetType().Name ?? source?.GetType().Name} " +
                   $"fromViewport={IsFromViewport(source)} insideSettings={IsInsideSettings(source)} captured={Mouse.Captured?.GetType().Name} " +
                   $"latched={_anchor.IsLatched} dragging={_dragging} handled={e.Handled}");

        // ⚠ 别的控件正握着鼠标（例如 ComboBox 的下拉弹层、输入框拖动选择文字）时，整个事件都别碰：
        // WPF 在「某控件捕获鼠标」时会把鼠标事件隧道经过它的所有祖先 —— 我们按位置判断「在视口里」就会
        // 误以为是拖拽，把事件吃掉并抢走捕获，导致下拉项/输入框点不动（用户报的 bug）。
        if (Mouse.Captured is not null && !ReferenceEquals(Mouse.Captured, this)) return;

        // 单点中键留下的锚点：再按任意键就关掉（PictureView 原来的行为），这一下点击不再传给下面的控件
        if (_anchor.IsLatched)
        {
            ExitAnchor();
            e.Handled = true;
            return;
        }

        // 工具条 / 设置面板上的点击：不接管，但要停下惯性（用户已经在点别的东西了）
        if (!IsFromViewport(source))
        {
            AbortInertia();
            return;
        }

        // 翻页视图里的「上一张 / 下一张」按钮（还有以后加的任何按钮）：点击留给 Button，别被拖拽吃掉
        if (IsOnClickableControl(source)) return;

        if (e.ChangedButton == MouseButton.Middle)
        {
            if (_pagedActive) return;   // 翻页 / 单张模式没有“沿阅读轴自动滚”这个概念
            if (!vm.HasPages || IsInsideSettings(source)) return;
            if (_dragging) return;          // 正拖着画面时按中键：忽略，别让两套滚动同时改偏移
            if (_anchor.IsActive) return;   // 按住中键的过程中重复按下（理论上不会发生）

            e.Handled = true;
            _anchor.HoldThresholdMilliseconds = vm.Cfg.HoldMillisecondsEffective;
            _anchor.MiddleDown(NowMilliseconds());
            BeginAnchor(e);
            return;
        }

        if (e.ChangedButton == MouseButton.Left && !_anchor.IsActive)
        {
            if (!vm.HasPages || IsInsideSettings(source)) return;
            e.Handled = true;
            BeginDrag(e);
        }
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _anchor.IsPressed)
        {
            var phase = _anchor.MiddleUp(NowMilliseconds());
            e.Handled = true;
            // 按得短 → Latched（锚点留下）；按得久 → None（松手就消失）
            if (phase != PictureAnchorPhase.Latched) ExitAnchor();
            return;
        }

        if (e.ChangedButton == MouseButton.Left && _dragging)
        {
            var moved = _dragMoved;
            var (velocityMain, velocityCross) = moved ? _inertia.ComputeVelocity() : (0.0, 0.0);
            EndDrag();
            if (moved)
            {
                e.Handled = true;
                StartInertia(velocityMain, velocityCross);
            }
            else
            {
                AbortInertia();   // 只是点了一下（没拖动）：把残余速度扔掉，别继续滑
            }
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        WakeControls();   // 全屏时鼠标一动先把工具条亮出来（窗口模式里是空转）
        if (!_dragging) return;

        var position = e.GetPosition(this);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // 手抖 / 单击，不算拖拽

        _dragMoved = true;
        e.Handled = true;

        if (_pagedActive)
        {
            // 翻页模式：内容跟着手走（放大后平移）。没放大时内容不溢出，夹取后等于不动。
            PanPaged(_dragStartPanX - dx, _dragStartPanY - dy);
        }
        else
        {
            // 两个轴都能拖：阅读轴沿用原来的方向规则，跨轴按「内容跟着手走」算
            var reading = _dragStartReading + PictureScrollMath.DragDelta(Mode, dx, dy);
            var cross = _dragStartCross + PictureScrollMath.CrossDragDelta(Mode, dx, dy);
            SetReadingOffset(reading);
            SetCrossOffset(cross);
        }

        // 惯性速度用「实际生效的偏移」采样：撞到边界就不动了，松手自然不会在边上滑
        var (mainOffset, crossOffset) = GetInertiaOffsets();
        _inertia.Note(NowMilliseconds(), mainOffset, crossOffset);
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        TraceInput($"View.LostMouseCapture releasing={_releasingMouse} dragging={_dragging} anchor={_anchor.Phase}");
        if (_releasingMouse) return;

        if (_dragging)
        {
            _dragging = false;
            _dragMoved = false;
            Cursor = null;
        }

        if (_anchor.IsActive) ExitAnchor();
    }

    private void BeginDrag(MouseButtonEventArgs e)
    {
        // 连续甩动会越滑越快：先把“还在滑的残余速度”接过来（设置里可关），再停掉当前惯性
        var accumulate = Vm?.Cfg.InertiaAccelerate == true;
        _carriedMainVelocity = accumulate ? _inertiaMainVelocity : 0;
        _carriedCrossVelocity = accumulate ? _inertiaCrossVelocity : 0;
        CancelInertia();

        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(this);
        _dragStartReading = GetReadingOffset();
        _dragStartCross = GetCrossOffset();
        _dragStartPanX = _pagedPanX;
        _dragStartPanY = _pagedPanY;
        _inertia.Reset();
        var (mainOffset, crossOffset) = GetInertiaOffsets();
        _inertia.Note(NowMilliseconds(), mainOffset, crossOffset);
        Cursor = Cursors.SizeAll;
        CaptureMouse();
    }

    private void EndDrag()
    {
        _dragging = false;
        _dragMoved = false;
        Cursor = null;
        if (!_anchor.IsActive) ReleaseMouseCaptureSafely();   // 捕获谁在用谁释放
    }

    // ---------------------------------------------------------------- 拖动惯性

    /// <summary>松手：速度够快且设置里开着惯性时，继续滑一段（<see cref="PictureInertia"/> 负责衰减）。</summary>
    private void StartInertia(double sampleMainVelocity, double sampleCrossVelocity)
    {
        if (Vm is not { } vm || !vm.Cfg.InertiaEnabled)
        {
            AbortInertia();
            return;
        }

        // 「越滑越快」：把上一次还在滑的残余速度叠加上去（设置里可关），并封顶防止滑穿整本
        var (mainVelocity, crossVelocity) = PictureInertia.Combine(
            sampleMainVelocity,
            sampleCrossVelocity,
            _carriedMainVelocity,
            _carriedCrossVelocity,
            vm.Cfg.InertiaAccelerate,
            PictureInertia.DefaultMaxSpeed);

        _carriedMainVelocity = 0;
        _carriedCrossVelocity = 0;

        if (!_inertia.IsFastEnough(mainVelocity, crossVelocity)) return;

        _inertiaMainVelocity = mainVelocity;
        _inertiaCrossVelocity = crossVelocity;

        _inertiaTimer ??= new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        if (_inertiaTimer.Tag is null)
        {
            _inertiaTimer.Tick += OnInertiaTick;
            _inertiaTimer.Tag = true;
        }

        _inertiaLastTimestamp = Stopwatch.GetTimestamp();
        _inertiaTimer.Start();
    }

    private void OnInertiaTick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _inertiaLastTimestamp) * 1000.0 / Stopwatch.Frequency;
        _inertiaLastTimestamp = now;
        if (elapsed <= 0 || elapsed > 100) elapsed = 16.0;   // 掉帧 / 休眠回来：别一下跳很远

        var (main, cross) = _inertia.Decay(_inertiaMainVelocity, _inertiaCrossVelocity, elapsed);
        var (beforeMain, beforeCross) = GetInertiaOffsets();
        var targetMain = beforeMain + main * elapsed;
        var targetCross = beforeCross + cross * elapsed;

        ApplyInertiaOffsets(targetMain, targetCross);

        var (afterMain, afterCross) = GetInertiaOffsets();
        // 撞到边界（请求了但实际没动）就把这一轴速度清掉，别在边上“嗡嗡”震
        if (Math.Abs(afterMain - targetMain) > 0.5) main = 0;
        if (Math.Abs(afterCross - targetCross) > 0.5) cross = 0;

        _inertiaMainVelocity = main;
        _inertiaCrossVelocity = cross;

        if (!_inertia.IsFastEnough(main, cross)) CancelInertia();
    }

    /// <summary>停下惯性动画（保留「残余速度」，下一次拖拽还能叠加）。</summary>
    private void CancelInertia()
    {
        _inertiaTimer?.Stop();
        _inertiaMainVelocity = 0;
        _inertiaCrossVelocity = 0;
    }

    /// <summary>彻底停下惯性：动画 + 残余速度一起清掉（滚轮 / 缩放 / 换页 / 切工具时用）。</summary>
    private void AbortInertia()
    {
        CancelInertia();
        _carriedMainVelocity = 0;
        _carriedCrossVelocity = 0;
    }

    // ---------------------------------------------------------------- 中键锚点

    private void BeginAnchor(MouseButtonEventArgs e)
    {
        AbortInertia();
        _anchorPoint = e.GetPosition(this);

        var point = e.GetPosition(ViewportGrid);
        UpdateAnchorArrowDirection();
        AnchorIndicator.Visibility = Visibility.Visible;
        Canvas.SetLeft(AnchorIndicator, Math.Clamp(
            point.X - AnchorIndicator.Width / 2, 0, Math.Max(0, ViewportGrid.ActualWidth - AnchorIndicator.Width)));
        Canvas.SetTop(AnchorIndicator, Math.Clamp(
            point.Y - AnchorIndicator.Height / 2, 0, Math.Max(0, ViewportGrid.ActualHeight - AnchorIndicator.Height)));

        CaptureMouse();
        StartAnchorTimer();
    }

    private void StartAnchorTimer()
    {
        _anchorTimer ??= new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        if (_anchorTimer.Tag is null)
        {
            _anchorTimer.Tick += OnAnchorTick;
            _anchorTimer.Tag = true;
        }
        _anchorTimer.Start();
    }

    /// <summary>每约一帧：按鼠标离锚点的距离往阅读方向滚（原理与 PictureView 的中键自动滚动一致）。</summary>
    private void OnAnchorTick(object? sender, EventArgs e)
    {
        if (!_anchor.IsActive)
        {
            _anchorTimer?.Stop();
            return;
        }

        var pointer = Mouse.GetPosition(this);
        var delta = PictureScrollMath.AnchorDelta(Mode, pointer.X, pointer.Y, _anchorPoint.X, _anchorPoint.Y);
        if (delta == 0) return;   // 鼠标还停在锚点上（含被夹在边界时）→ 不动

        SetReadingOffset(GetReadingOffset() + delta);
    }

    private void ExitAnchor()
    {
        _anchor.Reset();
        _anchorTimer?.Stop();
        AnchorIndicator.Visibility = Visibility.Collapsed;
        if (!_dragging) Cursor = null;
        if (!_dragging) ReleaseMouseCaptureSafely();   // 捕获谁在用谁释放
    }

    private void ReleaseMouseCaptureSafely()
    {
        if (!IsMouseCaptured) return;
        _releasingMouse = true;
        try { ReleaseMouseCapture(); }
        finally { _releasingMouse = false; }
    }

    /// <summary>指示器上的箭头跟着滚动轴走：纵向是上下箭头，横向是左右箭头。</summary>
    private void UpdateAnchorArrowDirection()
    {
        var horizontal = IsHorizontalMode;
        ArrowUp.Visibility = horizontal ? Visibility.Collapsed : Visibility.Visible;
        ArrowDown.Visibility = horizontal ? Visibility.Collapsed : Visibility.Visible;
        ArrowLeft.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
        ArrowRight.Visibility = horizontal ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>全屏状态变了 → 让外壳去藏 / 恢复标题栏、侧边栏、状态栏并把窗口铺满屏幕。</summary>
    private void OnFullscreenChanged(object? sender, bool on)
    {
        _fullscreenHost?.SetFullscreen(on);

        if (!on)
        {
            _idleTimer?.Stop();
            Cursor = null;    // 退出全屏把鼠标指针还回来（不然会一直是“隐藏”状态）
            HideFullscreenHint();
            return;
        }

        // 刚进全屏先把工具条亮一下，2.5 秒后自动缩；视图重建时若已藏着就只补倒计时
        if (Vm?.ControlsVisible == true) WakeControls();
        else StartIdleTimer();

        ShowFullscreenHint();
    }

    /// <summary>进入全屏时在右下角提示 4 秒「F11 / Esc 退出 · 鼠标移到顶部显示工具条」。</summary>
    private void ShowFullscreenHint()
    {
        if (FullscreenHintPanel is null) return;

        FullscreenHintPanel.Visibility = Visibility.Visible;

        _fullscreenHintTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4),
        };
        if (_fullscreenHintTimer.Tag is null)
        {
            _fullscreenHintTimer.Tick += (_, _) => HideFullscreenHint();
            _fullscreenHintTimer.Tag = true;
        }

        _fullscreenHintTimer.Stop();
        _fullscreenHintTimer.Start();
    }

    private void HideFullscreenHint()
    {
        _fullscreenHintTimer?.Stop();
        if (FullscreenHintPanel is not null) FullscreenHintPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>鼠标一动：亮出工具条、恢复鼠标指针，并重新计时（不在全屏时什么都不做）。</summary>
    private void WakeControls()
    {
        if (_forceHideControlsForScreenshot) return;
        if (Vm is not { } vm || !vm.IsFullscreen) return;

        vm.ShowControls();
        Cursor = null;
        StartIdleTimer();
    }

    /// <summary>截图模式：强制收起工具条后忽略鼠标活动（拍“沉浸”那一帧时鼠标可能正好在窗口里）。</summary>
    private bool _forceHideControlsForScreenshot;

    /// <summary>开始 / 重新开始“鼠标闲置”倒计时（只挂一次 Tick）。</summary>
    private void StartIdleTimer()
    {
        _idleTimer ??= new DispatcherTimer(DispatcherPriority.Background) { Interval = IdleDelay };
        if (_idleTimer.Tag is null)
        {
            _idleTimer.Tick += OnIdleTick;
            _idleTimer.Tag = true;
        }

        _idleTimer.Stop();
        _idleTimer.Start();
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        _idleTimer?.Stop();

        if (Vm is not { } vm || !vm.IsFullscreen) return;

        // 鼠标停在工具条上 / 正在工具条里输入（缩放框）/ 设置面板开着 → 别缩，重新计时
        if (ToolbarBorder.IsMouseOver || ToolbarBorder.IsKeyboardFocusWithin || vm.IsSettingsVisible)
        {
            StartIdleTimer();
            return;
        }

        vm.HideControls();
        if (!vm.ControlsVisible) Cursor = Cursors.None;   // 指针也一起藏起来（沉浸式）
    }

    /// <summary>截图 / 调试：全屏启动后按 <c>YLT_PICTURE_CONTROLS=0</c> 直接把工具条收起来（拍“沉浸”那一帧）。</summary>
    private void ApplyFullscreenDebugHooks()
    {
        if (Vm is not { } vm || !vm.IsFullscreen) return;

        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_CONTROLS");
        if (text is "0" or "off" or "false" or "no")
        {
            _forceHideControlsForScreenshot = true;
            vm.HideControls();
            Cursor = Cursors.None;
            HideFullscreenHint();   // 拍“沉浸”帧时连提示也别露
        }
        else
        {
            WakeControls();
        }
    }

    // ---------------------------------------------------------------- 键盘

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            vm.OpenFolderCommand.Execute(null);
            return;
        }

        // F11：全屏 / 退出全屏
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F11)
        {
            e.Handled = true;
            vm.ToggleFullscreen();
            return;
        }

        // Esc 优先：锚点 → 全屏 → 设置面板（设置面板里的输入框正抓着焦点时也要能关闭）
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            if (_anchor.IsActive)
            {
                ExitAnchor();
                e.Handled = true;
                return;
            }
            if (vm.IsFullscreen)
            {
                e.Handled = true;
                vm.ToggleFullscreen();
                return;
            }
            if (vm.IsSettingsVisible)
            {
                vm.IsSettingsVisible = false;
                e.Handled = true;
                return;
            }
        }

        // 正在输入框 / 下拉框 / 列表里时别抢键
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or ListBox or ListBoxItem
            or Slider or CheckBox or RadioButton) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        // 翻页 / 单张模式：方向键翻页（日式模式左右互换）、PageUp/PageDown/空格、Home/End
        if (_pagedActive && HandlePagedKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Home:
                e.Handled = true;
                SetReadingOffset(0);
                break;

            case Key.End:
                e.Handled = true;
                SetReadingOffset(GetReadingMaximum());
                break;
        }
    }

    /// <summary>缩放输入框：回车立刻生效并离开焦点；Esc 还原成当前值。</summary>
    private void OnZoomBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ZoomBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ZoomBox.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();   // 还原显示
            Keyboard.ClearFocus();
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static double NowMilliseconds() => Environment.TickCount64;

    /// <summary>排错用：<c>YLT_PICTURE_INPUT_TRACE=文件</c> 时把鼠标事件写进去（不设就完全是空转）。</summary>
    private void TraceInput(string line)
    {
        if (string.IsNullOrWhiteSpace(_inputTracePath)) return;
        try { File.AppendAllText(_inputTracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); }
        catch { /* 排查文件写不进去就算了 */ }
    }

    private void OnHostPreviewMouseDownTrace(object sender, MouseButtonEventArgs e)
        => TraceInput($"Window.PreviewMouseDown {e.ChangedButton} src={(e.OriginalSource as FrameworkElement)?.GetType().Name} " +
                      $"posWin={e.GetPosition(_hostWindow)} handled={e.Handled}");

    /// <summary>
    /// 事件源是不是「长带视口里的东西」（图片 /ScrollViewer / 空状态面板…）。
    /// 比「按坐标判断在不在视口里」可靠：弹层（Popup）是独立窗口，按坐标会算成“在视口里”，
    /// 但它的元素根本不在视口里 —— 按坐标判断就会把弹层的点击当成拖拽吃掉（见 OnPreviewMouseDown 的说明）。
    /// </summary>
    private bool IsFromViewport(DependencyObject? source)
    {
        if (source is null) return false;
        if (ReferenceEquals(source, ViewportGrid)) return true;
        return source is Visual && ViewportGrid.IsAncestorOf(source);
    }

    /// <summary>这一下点击是不是落在按钮之类的可点击控件上（翻页箭头的点击不能被拖拽吃掉）。</summary>
    private bool IsOnClickableControl(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, ViewportGrid))
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>惯性采样 / 施加用的「当前偏移」：连续模式是阅读 + 跨轴偏移，翻页模式是平移偏移。</summary>
    private (double Main, double Cross) GetInertiaOffsets()
        => _pagedActive ? (_pagedPanX, _pagedPanY) : (GetReadingOffset(), GetCrossOffset());

    private void ApplyInertiaOffsets(double main, double cross)
    {
        if (_pagedActive) PanPaged(main, cross);
        else
        {
            SetReadingOffset(main);
            SetCrossOffset(cross);
        }
    }

    private bool IsInsideSettings(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, SettingsPanel)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ---------------------------------------------------------------- 开发 / 截图钩子（不设环境变量时全是空转）

    /// <summary>
    /// <c>YLT_PICTURE_SCROLL=0~1</c>：加载完先滚到指定阅读比例（截图核对「左右方向到底从哪头开始」）；
    /// <c>YLT_PICTURE_ANCHOR=1</c>：把锚点指示器画在正中间（截图核对箭头方向与样式）；
    /// <c>YLT_PICTURE_MEASURE=文件</c>：约 0.9 秒后把真实几何写进文件。
    /// </summary>
    private void ApplyDebugHooks()
    {
        if (Vm is not { } vm || !vm.HasPages) return;

        if (_pagedActive)
        {
            ApplyPagedDebugHooks();
        }
        else
        {
            var scrollRatio = Environment.GetEnvironmentVariable("YLT_PICTURE_SCROLL");
            if (!string.IsNullOrWhiteSpace(scrollRatio)
                && double.TryParse(scrollRatio, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
            {
                SetReadingOffset(PictureScrollMath.OffsetForRatio(ratio, GetReadingMaximum()));
            }

            var crossRatio = Environment.GetEnvironmentVariable("YLT_PICTURE_CROSS");
            if (!string.IsNullOrWhiteSpace(crossRatio)
                && double.TryParse(crossRatio, NumberStyles.Float, CultureInfo.InvariantCulture, out var cross))
            {
                SetCrossOffset(PictureScrollMath.OffsetForRatio(cross, GetCrossMaximum()));
            }

            if (PictureViewerViewModel.IsEnvironmentOn("YLT_PICTURE_ANCHOR"))
            {
                UpdateAnchorArrowDirection();
                Canvas.SetLeft(AnchorIndicator, Math.Max(0, ViewportGrid.ActualWidth / 2 - AnchorIndicator.Width / 2));
                Canvas.SetTop(AnchorIndicator, Math.Max(0, ViewportGrid.ActualHeight / 2 - AnchorIndicator.Height / 2));
                AnchorIndicator.Visibility = Visibility.Visible;
            }
        }

        if (PictureViewerViewModel.IsEnvironmentOn("YLT_PICTURE_SETTINGS")) vm.IsSettingsVisible = true;

        var measurePath = Environment.GetEnvironmentVariable("YLT_PICTURE_MEASURE");
        if (!string.IsNullOrWhiteSpace(measurePath) && !_measureScheduled)
        {
            _measureScheduled = true;
            ScheduleMeasure(measurePath);
        }
    }

    private void ScheduleMeasure(string path)
    {
        // 不切方向：0.9 秒后记一次几何（等图片解码 / 布局稳定）
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_PICTURE_SWITCH_MODE")))
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(900),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                WriteMeasure(path);
            };
            timer.Start();
            return;
        }

        // 切方向验收：先记「换方向前」（此刻布局已由 BuildStripAsync 完成），再切，0.35 秒后记「换方向后」
        WriteMeasure(path + ".before");
        if (!TrySwitchModeFromEnvironment()) return;

        var second = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        second.Tick += (_, _) =>
        {
            second.Stop();
            WriteMeasure(path);
        };
        second.Start();
    }

    /// <summary>开发钩子：<c>YLT_PICTURE_SWITCH_MODE=vertical|ltr|rtl</c> 在测量时切一次滚动方式。</summary>
    private bool TrySwitchModeFromEnvironment()
    {
        if (Vm is not { } vm) return false;

        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_SWITCH_MODE");
        if (string.IsNullOrWhiteSpace(text)) return false;

        PictureScrollMode? mode = text.Trim().ToLowerInvariant() switch
        {
            "vertical" or "v" or "上下" or "1" => PictureScrollMode.Vertical,
            "ltr" or "left-to-right" or "lefttoright" or "从左到右" or "2" => PictureScrollMode.HorizontalLeftToRight,
            "rtl" or "right-to-left" or "righttoleft" or "从右到左" or "3" => PictureScrollMode.HorizontalRightToLeft,
            "spread-ltr" or "spreadltr" or "4" => PictureScrollMode.SpreadLeftToRight,
            "spread-rtl" or "spreadrtl" or "5" => PictureScrollMode.SpreadRightToLeft,
            "single" or "single-page" or "singlepage" or "6" => PictureScrollMode.SinglePage,
            _ => null,
        };
        if (mode is null) return false;

        var option = vm.ModeOptions.FirstOrDefault(o => o.Value == mode.Value);
        if (option is null) return false;

        vm.SelectedMode = option;
        return true;
    }

    private void WriteMeasure(string path)
    {
        if (Vm is not { } vm) return;

        var mode = vm.Cfg.ScrollModeEffective;
        var lines = new List<string>
        {
            $"at={DateTime.Now:HH:mm:ss.fff}",
            $"mode={mode} builtMode={_builtMode} paged={_pagedActive} horizontal={PictureScrollModes.IsHorizontal(mode)} reverse={PictureScrollModes.IsReverse(mode)}",
            $"view={ActualWidth:0.#}x{ActualHeight:0.#} viewport={ViewportGrid.ActualWidth:0.#}x{ViewportGrid.ActualHeight:0.#} cross={_lastCross:0.#}",
            $"zoom={vm.Cfg.ZoomPercentEffective}%",
        };

        if (_pagedActive)
        {
            var (left, right) = PicturePaging.VisiblePages(mode, _pageIndex, vm.Pages.Count);
            lines.Add($"pageIndex={_pageIndex} left={left?.ToString() ?? "-"} right={right?.ToString() ?? "-"} pageCount={vm.Pages.Count}");
            lines.Add($"pagedContent={_pagedContentWidth:0.#}x{_pagedContentHeight:0.#} pagedViewport={PagedViewport.ActualWidth:0.#}x{PagedViewport.ActualHeight:0.#} pan=({_pagedPanX:0.#},{_pagedPanY:0.#}) animating={_pagedAnimating}");
            lines.Add($"pagedImages={_pagedLayerImages.Count} canvasChildren={PagedCanvas.Children.Count} indicator={PageIndicatorText.Text}");
        }
        else
        {
            var first = _slots.Count > 0 ? _slots[0] : null;
            var last = _slots.Count > 0 ? _slots[^1] : null;
            var firstChildIsPage1 = ImageStrip.Children.Count > 0 && first is not null
                                    && ReferenceEquals(ImageStrip.Children[0], first.Container);

            lines.Add($"scrollViewer={ImageScrollViewer.ActualWidth:0.#}x{ImageScrollViewer.ActualHeight:0.#}");
            lines.Add($"viewportSize={ImageScrollViewer.ViewportWidth:0.#}x{ImageScrollViewer.ViewportHeight:0.#} extent={ImageScrollViewer.ExtentWidth:0.#}x{ImageScrollViewer.ExtentHeight:0.#}");
            lines.Add($"physicalOffset=({ImageScrollViewer.HorizontalOffset:0.#},{ImageScrollViewer.VerticalOffset:0.#}) scrollable=({ImageScrollViewer.ScrollableWidth:0.#},{ImageScrollViewer.ScrollableHeight:0.#})");
            lines.Add($"readingOffset={GetReadingOffset():0.#}/{GetReadingMaximum():0.#} ratio={GetReadingRatio():0.###} fromPhysical={PictureScrollMath.ReadingOffset(mode, ImageScrollViewer.HorizontalOffset, ImageScrollViewer.VerticalOffset, ImageScrollViewer.ScrollableWidth, ImageScrollViewer.ScrollableHeight):0.#}");
            lines.Add($"crossOffset={GetCrossOffset():0.#}/{GetCrossMaximum():0.#}");
            lines.Add($"slots={_slots.Count} firstStart={first?.Start:0.#} firstLen={first?.MainLength:0.#} lastEnd={(last is null ? 0 : last.Start + last.MainLength):0.#}");
            lines.Add($"stripChildren={ImageStrip.Children.Count} orientation={ImageStrip.Orientation} firstChildIsPage1={firstChildIsPage1}");
            if (first is not null)
            {
                var border = vm.BorderStyle;
                lines.Add($"border={border.Enabled} sizes={border.InnerSize}/{border.FillSize}/{border.OuterSize} extra={border.TotalExtra} container={first.Container.ActualWidth:0.#}x{first.Container.ActualHeight:0.#} image={first.ImageControl.ActualWidth:0.#}x{first.ImageControl.ActualHeight:0.#}");
            }
            lines.Add($"anchorVisible={AnchorIndicator.Visibility} arrowHorizontal={ArrowLeft.Visibility} arrowVertical={ArrowUp.Visibility}");
        }

        lines.Add($"busy={vm.IsBusy} pages={vm.Pages.Count} status={vm.StatusText}");

        try { File.WriteAllLines(path, lines); }
        catch { /* 排查文件写不进去就算了 */ }
    }
}
