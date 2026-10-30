using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PdfReader.Rendering;
using YuLiangToolkit.Tools.PdfReader.Text;
using YuLiangToolkit.Tools.PdfReader.ViewModels;

namespace YuLiangToolkit.Tools.PdfReader.Views;

/// <summary>
/// 「PDF 阅读器」的界面后台。这个工具的核心是「PDF 按需渲染 + 自己接管滚动 / 缩放 / 翻页」，所以代码后置比一般功能重：
///
/// <list type="bullet">
///   <item><b>纯逻辑</b>（页怎么排、滚多少、双页怎么配对、惯性怎么衰减、文字怎么搜）全在
///         <see cref="PdfContinuousLayout"/>、<see cref="PdfSpreadLayout"/>、<see cref="PdfInertia"/>、
///         <see cref="PdfTextSearch"/> 里，自检直接断言；</item>
///   <item><b>这里</b>只做必须碰界面的部分：把可见页渲染成 Image、滚动 / 平移偏移、鼠标拖拽 / 惯性、
///         双页翻页动画、缩放、搜索跳页、目录面板、键盘；</item>
///   <item><b>ViewModel</b> 管设置、打开的 PDF、文字层 / 目录、文件关联状态。</item>
/// </list>
///
/// <para><b>「只渲染可见页」</b>：连续模式用 Canvas 摆一条长带，只给视口附近（前后各一屏缓冲）的页创建 Image
/// 并按需渲染，滚出范围的释放掉 —— 大文件也不会把内存吃满；缩放后按新的显示尺寸重新渲染可见页。</para>
///
/// <para><b>「两个轴」</b>：连续模式的阅读轴是纵向（横向模式是水平轴），缩放后另一个轴也能滚；
/// 双页模式放大后可以按住左键拖动平移。偏移量都自己记（不每帧反读 ScrollViewer，原因见
/// docs/dev/pitfalls.md「图片连续阅读器」那条）。</para>
/// </summary>
public partial class PdfReaderView : UserControl
{
    // ---------------------------------------------------------------- 一页的界面对象

    private sealed class PageHost
    {
        public int PageIndex;
        /// <summary>占位底色 + 页码文字 + 渲染出来的 Image。</summary>
        public FrameworkElement Container = null!;
        public Image ImageControl = null!;
        public TextBlock Placeholder = null!;
        public bool Loaded;
        public bool Failed;
        public bool RenderStarted;
    }

    // ---------------------------------------------------------------- 常量

    /// <summary>滚动后延迟多久补一次「可见页渲染 / 离屏释放」（一次滚轮会触发一堆 ScrollChanged）。</summary>
    private const double VisibleRefreshDelayMilliseconds = 120;

    /// <summary>双页翻页滑动动画时长（毫秒）与滑动距离（像素）。</summary>
    private const int SpreadAnimationMilliseconds = 170;
    private const double SpreadSlidePixels = 56;

    /// <summary>拖拽判定阈值：手抖 / 单击不算拖拽。</summary>
    private const double DragThresholdPixels = 4;

    /// <summary>搜索输入防抖（毫秒）：边打字边搜，但不要每个字符都全量扫一遍。</summary>
    private const int SearchDebounceMilliseconds = 280;

    // ---------------------------------------------------------------- 拖动惯性（两个轴）

    private readonly PdfInertia _inertia = new();
    private DispatcherTimer? _inertiaTimer;
    private double _inertiaMainVelocity;
    private double _inertiaCrossVelocity;
    private long _inertiaLastTimestamp;

    /// <summary>「还在滑的残余速度」：下一次拖拽开始时先存下来，松手时叠加（越甩越快）。</summary>
    private double _carriedMainVelocity;
    private double _carriedCrossVelocity;

    // ---------------------------------------------------------------- 连续滚动状态

    private PdfContinuousLayoutResult? _layout;
    private PdfReadMode _builtMode = PdfReadMode.Vertical;
    private bool _horizontal;

    /// <summary>「阅读偏移」的唯一真值（0 = 第一页开头）。</summary>
    private double _readingOffset;

    /// <summary>跨轴偏移（纵向模式的水平偏移 / 横向模式的垂直偏移；缩放后可滚）。</summary>
    private double _crossOffset;

    private readonly Dictionary<int, PageHost> _pageHosts = new();
    private readonly Queue<int> _renderQueue = new();
    private readonly HashSet<int> _renderQueued = new();
    private bool _renderDraining;
    private int _generation;

    private int _visibleFirst = -1;
    private int _visibleLast = -1;

    private DispatcherTimer? _visibleRefreshTimer;
    private bool _needsVisibleRefresh;
    private bool _layoutQueued;

    // ---------------------------------------------------------------- 双页翻页状态

    private bool _spreadActive;
    private int _spreadStart;
    private bool _spreadAnimating;
    private bool? _pendingSpreadForward;
    private int _spreadGeneration;
    private PageHost? _spreadLeft;
    private PageHost? _spreadRight;

    /// <summary>双页缩放后的平移（0 = 左上对齐；放大后拖动 / 惯性改它）。</summary>
    private double _spreadPanX;
    private double _spreadPanY;
    private double _spreadContentWidth;
    private double _spreadContentHeight;
    private TranslateTransform? _spreadPanTransform;

    // ---------------------------------------------------------------- 左键拖拽

    private bool _dragging;
    private bool _dragMoved;
    private bool _releasingMouse;
    private Point _dragStart;
    private double _dragStartReading;
    private double _dragStartCross;
    private double _dragStartPanX;
    private double _dragStartPanY;

    // ---------------------------------------------------------------- 文字搜索

    private IReadOnlyList<PdfSearchHit> _searchHits = Array.Empty<PdfSearchHit>();
    private int _searchIndex = -1;
    private DispatcherTimer? _searchDebounceTimer;
    private string? _pendingSearchQuery;

    // ---------------------------------------------------------------- 其他

    private Window? _hostWindow;

    /// <summary>窗口关闭时可能已经拿不到 DataContext，留一份 VM 引用专门用来落盘阅读位置。</summary>
    private PdfReaderViewModel? _vmForClose;

    /// <summary>宿主窗口（外壳）的全屏开关；视图卸载时就置空（别再往上找 Window，那时已经摘下来了）。</summary>
    private IFullscreenHost? _fullscreenHost;

    /// <summary>全屏时鼠标不动多久就把工具条 + 鼠标指针一起藏起来。</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(2.5);
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _fullscreenHintTimer;

    private bool _unloaded;

    /// <summary>调试 / 截图：滚轮模拟只触发一次（<c>YLT_PDF_WHEEL_SIM</c>）。</summary>
    private bool _wheelSimScheduled;
    private bool _demoStarted;
    private bool _loadingProgrammatically;

    /// <summary>上一次真正搭出界面的文档：打开新 PDF 失败时它不变，就不用重建。</summary>
    private PdfDocumentSession? _builtSession;

    /// <summary>最近一次看到的状态文本（判断要不要弹错误提示）。</summary>
    private string _lastStatusText = "";

    private DispatcherTimer? _errorToastTimer;

    // 重建防重入：重建途中又来的请求排队补跑（不能静默丢，否则界面停在旧模式）
    private bool _building;
    private bool _buildQueued;
    private bool _buildQueuedKeepPage;

    public PdfReaderView()
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
        SpreadViewport.SizeChanged += OnSpreadViewportSizeChanged;
    }

    private PdfReaderViewModel? Vm => DataContext as PdfReaderViewModel;

    private PdfReadMode Mode => Vm?.CurrentReadMode ?? PdfReadMode.Vertical;

    /// <summary>当前 DPI 缩放：渲染宽度要按**设备像素**算，否则 125% / 150% 缩放下 PDF 会糊。</summary>
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

    // ---------------------------------------------------------------- 连续模式：偏移 / 尺寸

    /// <summary>连续模式下视口在阅读轴上的长度（纵向看高，横向看宽）。</summary>
    private double ReadingViewportLength => _horizontal ? ViewportGrid.ActualWidth : ViewportGrid.ActualHeight;

    /// <summary>连续模式下视口在跨轴上的长度（纵向看宽，横向看高）。</summary>
    private double CrossViewportLength => _horizontal ? ViewportGrid.ActualHeight : ViewportGrid.ActualWidth;

    private double ReadingLength => PdfContinuousLayout.ReadingLength(_layout, _builtMode);

    private double CrossLength => PdfContinuousLayout.CrossLength(_layout, _builtMode);

    /// <summary>当前长带的最大阅读偏移（内容不够一屏时为 0）。</summary>
    private double MaximumOffset => _layout is null
        ? 0
        : PdfContinuousLayout.MaximumOffset(_layout, _builtMode, ViewportGrid.ActualWidth, ViewportGrid.ActualHeight);

    /// <summary>当前长带的最大跨轴偏移（缩放 ≤ 100% 时为 0）。</summary>
    private double MaximumCrossOffset => _layout is null
        ? 0
        : PdfContinuousLayout.MaximumCrossOffset(_layout, _builtMode, ViewportGrid.ActualWidth, ViewportGrid.ActualHeight);

    private double GetReadingOffset() => PdfContinuousLayout.ClampOffset(_readingOffset, MaximumOffset);

    private double GetCrossOffset() => PdfContinuousLayout.ClampOffset(_crossOffset, MaximumCrossOffset);

    // ---------------------------------------------------------------- 生命周期

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _unloaded = false;

        if (Vm is not { } vm) return;

        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewKeyDown += OnHostPreviewKeyDown;

            // 关窗口 / 退出程序时把最后阅读位置立刻写盘（这条路径不一定会走到 View.Unloaded）
            _hostWindow.Closing -= OnHostWindowClosing;
            _hostWindow.Closing += OnHostWindowClosing;
        }
        _vmForClose = vm;

        vm.PdfPicker = PickPdf;
        vm.DocumentChanged -= OnDocumentChanged;
        vm.DocumentChanged += OnDocumentChanged;
        vm.ModeChanged -= OnModeChanged;
        vm.ModeChanged += OnModeChanged;
        vm.ZoomChanged -= OnZoomChanged;
        vm.ZoomChanged += OnZoomChanged;
        vm.LayoutChanged -= OnLayoutChanged;
        vm.LayoutChanged += OnLayoutChanged;
        vm.TextLayerChanged -= OnTextLayerChanged;
        vm.TextLayerChanged += OnTextLayerChanged;

        // 全屏：ViewModel 只切状态，真正的窗口操作交给外壳（MainWindow 实现了 IFullscreenHost）
        _fullscreenHost = Window.GetWindow(this) as IFullscreenHost;
        vm.FullscreenChanged -= OnFullscreenChanged;
        vm.FullscreenChanged += OnFullscreenChanged;
        if (vm.IsFullscreen) OnFullscreenChanged(vm, true);   // 截图 / 视图重建时同步一次

        UpdateModeChrome();
        UpdateEmptyStatus();
        UpdateSearchChrome();
        UpdateOutlineChrome();
        UpdateTextLayerHint();
        MaybeShowStatusError();

        // 双击 .pdf 启动（文件关联 / 命令行）：先打开它，再搭界面
        if (vm.HasLaunchPdf)
        {
            await RunProgrammaticallyAsync(vm.LoadLaunchPdfAsync);
            if (vm.HasDocument) await BuildAsync(keepPage: false);
            else UpdateEmptyStatus();
            MaybeShowStatusError();
            ApplyFullscreenDebugHooks();
            return;
        }

        if (vm.HasDocument)
        {
            await BuildAsync(keepPage: true);
            ApplyFullscreenDebugHooks();
            return;
        }

        if (_demoStarted) return;
        _demoStarted = true;

        // 开发 / 截图：YLT_PDF_DEMO=1 → 生成演示 PDF；YLT_PDF_OPEN=路径 → 直接打开指定 PDF
        await RunProgrammaticallyAsync(vm.TryStartDemoAsync);
        if (!vm.HasDocument) await RunProgrammaticallyAsync(vm.TryOpenFromEnvironmentAsync);

        if (vm.HasDocument) await BuildAsync(keepPage: false);
        else
        {
            UpdateEmptyStatus();
            UpdateOutlineChrome();
        }

        MaybeShowStatusError();
        ApplyDebugUiHooks();
        ApplyFullscreenDebugHooks();
    }

    // ---------------------------------------------------------------- 全屏（沉浸阅读）

    /// <summary>全屏状态变了 → 让外壳去藏 / 恢复标题栏、侧边栏、状态栏并把窗口铺满屏幕。</summary>
    private void OnFullscreenChanged(object? sender, bool on)
    {
        _fullscreenHost?.SetFullscreen(on);
        UpdateModeChrome();

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

        // 鼠标停在工具条上 / 正在工具条里输入（搜索框、缩放框）/ 面板开着 → 别缩，重新计时
        if (ToolbarBorder.IsMouseOver || ToolbarBorder.IsKeyboardFocusWithin
            || vm.IsSettingsVisible || vm.IsOutlineVisible)
        {
            StartIdleTimer();
            return;
        }

        vm.HideControls();
        if (!vm.ControlsVisible) Cursor = Cursors.None;   // 指针也一起藏起来（沉浸式）
    }

    /// <summary>截图 / 调试：全屏启动后按 <c>YLT_PDF_CONTROLS=0</c> 直接把工具条收起来（拍“沉浸”那一帧）。</summary>
    private void ApplyFullscreenDebugHooks()
    {
        if (Vm is not { } vm || !vm.IsFullscreen) return;

        var text = Environment.GetEnvironmentVariable("YLT_PDF_CONTROLS");
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

    /// <summary>
    /// 开发 / 截图钩子（不设环境变量时全是空转）：
    /// <c>YLT_PDF_SEARCH=文字</c> 打开后直接搜一次、<c>YLT_PDF_OUTLINE=1</c> 打开目录面板、
    /// <c>YLT_PDF_SETTINGS=1</c> 打开设置面板。
    /// </summary>
    private void ApplyDebugUiHooks()
    {
        var vm = Vm;
        if (vm is null || !vm.HasDocument) return;

        if (PdfReaderViewModel.IsEnvironmentOn("YLT_PDF_OUTLINE")) vm.IsOutlineVisible = true;
        if (PdfReaderViewModel.IsEnvironmentOn("YLT_PDF_SETTINGS")) vm.IsSettingsVisible = true;
        if (PdfReaderViewModel.IsEnvironmentOn("YLT_PDF_RECENT")) vm.IsRecentVisible = true;

        var search = Environment.GetEnvironmentVariable("YLT_PDF_SEARCH");
        if (!string.IsNullOrWhiteSpace(search))
        {
            SearchBox.Text = search.Trim();
            RunSearch(SearchBox.Text, autoJump: true);
        }

        // 截图 / 调试：YLT_PDF_JUMP=4 打开后直接走一次「跳页输入框」跳到第 4 页
        var jump = Environment.GetEnvironmentVariable("YLT_PDF_JUMP");
        if (!string.IsNullOrWhiteSpace(jump))
        {
            PageJumpBox.Text = jump.Trim();
            TryJumpToPageInput();
        }

        // 截图 / 调试：YLT_PDF_WHEEL_SIM=-120 打开后模拟「向下滚一格」，用来核对滚轮惯性
        if (!_wheelSimScheduled && int.TryParse(Environment.GetEnvironmentVariable("YLT_PDF_WHEEL_SIM"), out var wheelSimDelta))
        {
            _wheelSimScheduled = true;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SimulateWheel(wheelSimDelta);
            };
            timer.Start();
        }
    }

    /// <summary>调试 / 截图用：构造一个滚轮事件走正常处理链路（方向 / 惯性 / 页码都会跟着变）。</summary>
    private void SimulateWheel(int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = _spreadActive ? SpreadHost : ContinuousScroll,
        };
        OnPreviewMouseWheel(this, args);
    }

    private async Task RunProgrammaticallyAsync(Func<Task> load)
    {
        _loadingProgrammatically = true;
        try
        {
            await load();
        }
        finally
        {
            _loadingProgrammatically = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;

        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.Closing -= OnHostWindowClosing;
            _hostWindow = null;
        }
        _vmForClose = null;

        if (Vm is { } vm)
        {
            // ⚠ 顺序不能反：先 ExitFullscreen（此时还订阅着，事件能传出去让外壳还原窗口），再摘事件。
            // 反过来的话外壳会一直停在“藏了壳”的全屏状态，切到别的页面就没有标题栏了。
            vm.ExitFullscreen();
            vm.FlushReadingPosition(saveNow: true);   // 关窗口 / 切走的最后兜底：记住读到哪
            vm.FullscreenChanged -= OnFullscreenChanged;

            vm.DocumentChanged -= OnDocumentChanged;
            vm.ModeChanged -= OnModeChanged;
            vm.ZoomChanged -= OnZoomChanged;
            vm.LayoutChanged -= OnLayoutChanged;
            vm.TextLayerChanged -= OnTextLayerChanged;
            vm.PdfPicker = null;
        }
        _fullscreenHost = null;

        _idleTimer?.Stop();
        _idleTimer = null;
        _fullscreenHintTimer?.Stop();
        _fullscreenHintTimer = null;
        Cursor = null;

        if (_dragging)
        {
            _dragging = false;
            _dragMoved = false;
            Cursor = null;
            ReleaseMouseCaptureSafely();
        }

        CancelInertia();
        _visibleRefreshTimer?.Stop();
        _visibleRefreshTimer = null;
        _needsVisibleRefresh = false;
        _searchDebounceTimer?.Stop();
        _searchDebounceTimer = null;
        _errorToastTimer?.Stop();
        _errorToastTimer = null;

        ClearContinuousVisuals();
        ClearSpreadVisuals();
        _spreadActive = false;

        PageIndicatorPanel.Visibility = Visibility.Collapsed;
        HintPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>主窗口关闭 / 退出程序时把最后阅读位置立刻写盘（这条路径不一定会走到 View.Unloaded）。</summary>
    private void OnHostWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        => (Vm ?? _vmForClose)?.FlushReadingPosition(saveNow: true);

    private string? PickPdf()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 PDF 文件",
            Filter = "PDF 文件 (*.pdf)|*.pdf|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        var last = Vm?.Cfg.LastFolder;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last)) dialog.InitialDirectory = last;
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        UpdateModeChrome();
        UpdateEmptyStatus();
        UpdateSearchChrome();
        UpdateOutlineChrome();
        UpdateTextLayerHint();
        MaybeShowStatusError();

        var vm = Vm;
        if (vm?.HasDocument != true)
        {
            CancelInertia();
            ClearContinuousVisuals();
            ClearSpreadVisuals();
            _spreadActive = false;
            _builtSession = null;
            ClearSearch(clearText: false);
            return;
        }

        // 程序自己加载（演示 / 环境变量 / 双击启动）由 OnLoaded 负责搭界面，避免重复重建
        if (_loadingProgrammatically) return;

        // 打开新 PDF 失败时 _session 还是原来那份：状态文案弹提示就行，别把阅读位置重置回第一页
        if (ReferenceEquals(vm.Session, _builtSession)) return;

        _ = BuildAsync(keepPage: false);
    }

    private void OnModeChanged(object? sender, EventArgs e)
    {
        UpdateModeChrome();
        if (Vm?.HasDocument == true) _ = BuildAsync(keepPage: true);
    }

    private void OnZoomChanged(object? sender, EventArgs e)
    {
        UpdateModeChrome();
        if (Vm?.HasDocument == true) _ = RebuildKeepingCenterAsync();
    }

    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        // 目前只有「双页页面间距」：只影响双页画面，重排时保持平移中心
        if (Vm?.HasDocument == true && _spreadActive) _ = RebuildSpreadKeepingCenterAsync();
    }

    private void OnTextLayerChanged(object? sender, EventArgs e)
    {
        UpdateOutlineChrome();
        UpdateTextLayerHint();
        UpdateSearchChrome();

        // 提取期间用户已经输入了搜索词：提取完自动跑一次并跳到第一个命中
        if (_pendingSearchQuery is { } query)
        {
            _pendingSearchQuery = null;
            RunSearch(query, autoJump: true);
        }
    }

    // ---------------------------------------------------------------- 建视图（连续 / 双页统一切换）

    private async Task BuildAsync(bool keepPage)
    {
        if (_building)
        {
            _buildQueued = true;
            _buildQueuedKeepPage |= keepPage;
            return;
        }

        _building = true;
        try
        {
            var vm = Vm;
            if (vm?.Session is not { } session)
            {
                _builtSession = null;
                ClearContinuousVisuals();
                ClearSpreadVisuals();
                _spreadActive = false;
                UpdateModeChrome();
                return;
            }

            // 换模式时停在当前页；打开新 PDF 时：截图定位页（环境变量）> 上次阅读位置 > 第一页
            PdfReadingPosition? restore = null;
            int targetPage;
            if (keepPage)
            {
                targetPage = CurrentPageIndex();
            }
            else
            {
                var pendingPage = vm.ConsumePendingPageIndex();
                if (pendingPage is null) restore = vm.ConsumeRestorePosition();
                targetPage = pendingPage ?? restore?.PageIndex ?? 0;
            }

            if (PdfReadModes.IsSpread(vm.CurrentReadMode))
            {
                CancelInertia();
                if (_dragging) EndDrag();
                ClearContinuousVisuals();
                _spreadActive = true;
                _spreadStart = PdfSpreadLayout.StartForPage(targetPage, session.PageCount);
                ContinuousScroll.Visibility = Visibility.Collapsed;
                SpreadHost.Visibility = Visibility.Visible;
                await ShowSpreadAsync();
            }
            else
            {
                ClearSpreadVisuals();
                _spreadActive = false;
                SpreadHost.Visibility = Visibility.Collapsed;
                ContinuousScroll.Visibility = Visibility.Visible;
                var restoreRatio = restore is { } stored && stored.ModeEffective == vm.CurrentReadMode
                    ? stored.ReadingRatio
                    : (double?)null;
                await BuildContinuousAsync(targetPage, restoreReadingRatio: restoreRatio);
            }

            _builtSession = session;
            UpdateModeChrome();
            UpdatePageIndicator();
            UpdateProgress();
            UpdateTextLayerHint();
        }
        finally
        {
            _building = false;
            if (_buildQueued)
            {
                _buildQueued = false;
                var keep = _buildQueuedKeepPage;
                _buildQueuedKeepPage = false;
                _ = BuildAsync(keep);
            }
        }
    }

    /// <summary>当前阅读位置对应的页号（换阅读方式 / 窗口缩放时尽量停在这一页）。</summary>
    private int CurrentPageIndex()
    {
        var vm = Vm;
        if (vm?.Session is not { } session || session.PageCount == 0) return 0;

        if (_spreadActive) return _spreadStart;
        if (_layout is null || _layout.IsEmpty) return 0;

        return PdfContinuousLayout.PageIndexAtOffset(_layout, _builtMode, GetReadingOffset());
    }

    // ---------------------------------------------------------------- 连续滚动：排版 / 渲染

    private async Task BuildContinuousAsync(
        int targetPage,
        (double Main, double Cross)? center = null,
        double? restoreReadingRatio = null)
    {
        var vm = Vm;
        if (vm?.Session is not { } session)
        {
            ClearContinuousVisuals();
            return;
        }

        var width = ViewportGrid.ActualWidth;
        var height = ViewportGrid.ActualHeight;
        if (width <= 1 || height <= 1) return;   // 视口还没量出来：等 SizeChanged 再来

        CancelInertia();
        if (_dragging) EndDrag();

        ClearContinuousVisuals();
        var generation = _generation;

        _spreadActive = false;
        _builtMode = vm.CurrentReadMode;
        _horizontal = PdfReadModes.IsHorizontal(_builtMode);

        _layout = PdfContinuousLayout.Compute(
            _builtMode,
            session.Pages,
            width,
            height,
            PdfReaderConfig.ContinuousPageGapPixels,
            PdfReaderConfig.SideMarginPixels,
            vm.Cfg.ZoomScale);

        PageCanvas.Width = _layout.ContentWidth;
        PageCanvas.Height = _layout.ContentHeight;
        ContinuousScroll.UpdateLayout();

        if (center is { } viewCenter)
        {
            ApplyViewportCenter(viewCenter.Main, viewCenter.Cross);
        }
        else if (restoreReadingRatio is { } storedRatio)
        {
            // 上次也是连续滚动：按整体阅读比例恢复（比只回到页开头更接近离开时的位置）
            SetReadingOffset(PdfContinuousLayout.OffsetForRatio(storedRatio, MaximumOffset));
            SetCrossOffset(0);
        }
        else
        {
            SetReadingOffset(PdfContinuousLayout.OffsetOfPage(_layout, _builtMode, targetPage));
            SetCrossOffset(0);
        }

        await Dispatcher.Yield(DispatcherPriority.Loaded);
        if (generation != _generation || _layout is null) return;

        RefreshVisiblePages(force: true);
        ApplyDebugScrollHook();
        UpdatePageIndicator();
        UpdateProgress();
    }

    private void ClearContinuousVisuals()
    {
        _generation++;

        foreach (var host in _pageHosts.Values)
        {
            host.ImageControl.Source = null;
            host.Loaded = false;
        }
        _pageHosts.Clear();
        PageCanvas.Children.Clear();

        _renderQueue.Clear();
        _renderQueued.Clear();

        _layout = null;
        _readingOffset = 0;
        _crossOffset = 0;
        _visibleFirst = -1;
        _visibleLast = -1;

        if (ContinuousScroll is not null)
        {
            ContinuousScroll.ScrollToHorizontalOffset(0);
            ContinuousScroll.ScrollToVerticalOffset(0);
        }
    }

    /// <summary>改阅读偏移（连续模式唯一入口）：夹进 [0, 最大值] 并同步 ScrollViewer。</summary>
    private void SetReadingOffset(double offset)
    {
        if (_layout is null)
        {
            _readingOffset = 0;
            return;
        }

        _readingOffset = PdfContinuousLayout.ClampOffset(offset, MaximumOffset);
        ApplyScrollOffsets();
        UpdatePageIndicator();
        UpdateProgress();
        ScheduleVisibleRefresh();
    }

    /// <summary>改跨轴偏移（缩放后纵向模式的左右 / 横向模式的上下）。</summary>
    private void SetCrossOffset(double offset)
    {
        if (_layout is null)
        {
            _crossOffset = 0;
            return;
        }

        _crossOffset = PdfContinuousLayout.ClampOffset(offset, MaximumCrossOffset);
        ApplyScrollOffsets();
        ScheduleVisibleRefresh();
    }

    private void ApplyScrollOffsets()
    {
        if (_horizontal)
        {
            ContinuousScroll.ScrollToHorizontalOffset(_readingOffset);
            ContinuousScroll.ScrollToVerticalOffset(_crossOffset);
        }
        else
        {
            ContinuousScroll.ScrollToHorizontalOffset(_crossOffset);
            ContinuousScroll.ScrollToVerticalOffset(_readingOffset);
        }
    }

    private (double Main, double Cross) CaptureViewportCenter()
    {
        if (_layout is null) return (0.5, 0.5);

        var readingLength = ReadingLength;
        var readingViewport = ReadingViewportLength;
        var main = readingLength > 0
            ? Math.Clamp((GetReadingOffset() + readingViewport / 2) / readingLength, 0, 1)
            : 0.5;

        var crossLength = CrossLength;
        var crossViewport = CrossViewportLength;
        var cross = crossLength > crossViewport && crossLength > 0
            ? Math.Clamp((GetCrossOffset() + crossViewport / 2) / crossLength, 0, 1)
            : 0.5;

        return (main, cross);
    }

    private void ApplyViewportCenter(double mainRatio, double crossRatio)
    {
        if (_layout is null) return;

        var readingLength = ReadingLength;
        var readingViewport = ReadingViewportLength;
        SetReadingOffset(mainRatio * readingLength - readingViewport / 2);

        var crossLength = CrossLength;
        var crossViewport = CrossViewportLength;
        SetCrossOffset(crossLength > crossViewport ? crossRatio * crossLength - crossViewport / 2 : 0);
    }

    private void OnContinuousScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_spreadActive) return;
        UpdatePageIndicator();
        UpdateProgress();
        ScheduleVisibleRefresh();
    }

    /// <summary>滚完 / 拖完延后补一次「可见页渲染」。已经在计时就不重置（连续滚时才能等到到期时刻）。</summary>
    private void ScheduleVisibleRefresh()
    {
        _needsVisibleRefresh = true;

        if (_visibleRefreshTimer is null)
        {
            _visibleRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(VisibleRefreshDelayMilliseconds),
            };
            _visibleRefreshTimer.Tick += (_, _) =>
            {
                _visibleRefreshTimer?.Stop();
                if (!_needsVisibleRefresh) return;
                _needsVisibleRefresh = false;
                RefreshVisiblePages(force: false);
            };
        }

        if (!_visibleRefreshTimer.IsEnabled) _visibleRefreshTimer.Start();
    }

    /// <summary>把视口附近（前后各一屏缓冲）的页排进渲染队列，离屏太远的释放掉。</summary>
    private void RefreshVisiblePages(bool force)
    {
        if (_spreadActive || _layout is null || _layout.IsEmpty) return;

        var viewport = ReadingViewportLength;
        if (viewport <= 0) return;

        var (first, last) = PdfContinuousLayout.VisibleRange(_layout, _builtMode, GetReadingOffset(), viewport);
        _visibleFirst = first;
        _visibleLast = last;

        var keepFrom = Math.Max(0, first - 1);
        var keepTo = Math.Min(_layout.PageCount - 1, last + 1);

        foreach (var index in _pageHosts.Keys.ToList())
        {
            if (index < keepFrom || index > keepTo) RemovePageHost(index);
        }

        for (var i = keepFrom; i <= keepTo; i++)
        {
            var host = EnsurePageHost(i);
            if (host.Loaded || host.Failed || host.RenderStarted) continue;
            EnqueueRender(i);
        }

        UpdatePageIndicator();
        UpdateProgress();
    }

    private PageHost EnsurePageHost(int pageIndex)
    {
        if (_pageHosts.TryGetValue(pageIndex, out var existing)) return existing;

        var box = _layout!.Boxes[pageIndex];
        var host = CreatePageHost(box.PageIndex, box.X, box.Y, box.Width, box.Height);
        PageCanvas.Children.Add(host.Container);
        _pageHosts[pageIndex] = host;
        return host;
    }

    private void RemovePageHost(int pageIndex)
    {
        if (!_pageHosts.Remove(pageIndex, out var host)) return;
        host.ImageControl.Source = null;
        PageCanvas.Children.Remove(host.Container);
    }

    private void EnqueueRender(int pageIndex)
    {
        if (!_renderQueued.Add(pageIndex)) return;
        _renderQueue.Enqueue(pageIndex);

        if (_renderDraining) return;
        _renderDraining = true;
        _ = DrainRendersAsync();
    }

    /// <summary>一个 worker 顺序渲染队列里的页（旧代 / 已滚出屏幕的条目直接丢）。</summary>
    private async Task DrainRendersAsync()
    {
        try
        {
            while (true)
            {
                if (_renderQueue.Count == 0) break;

                var pageIndex = _renderQueue.Dequeue();
                _renderQueued.Remove(pageIndex);

                if (_spreadActive) continue;
                if (!_pageHosts.TryGetValue(pageIndex, out var host)) continue;
                if (host.Loaded || host.Failed || host.RenderStarted) continue;

                await RenderHostAsync(host, _generation);
            }
        }
        finally
        {
            _renderDraining = false;
            if (_renderQueue.Count > 0 && !_spreadActive)
            {
                _renderDraining = true;
                _ = DrainRendersAsync();
            }
        }
    }

    /// <summary>把一页渲染进界面对象（连续 / 双页共用）。</summary>
    private async Task RenderHostAsync(PageHost host, int generation)
    {
        var vm = Vm;
        if (vm?.Session is not { } session || host.PageIndex >= session.PageCount) return;

        host.RenderStarted = true;
        try
        {
            var page = session.Pages[host.PageIndex];
            var desiredWidth = (int)Math.Ceiling(Math.Max(1, host.Container.Width) * DpiScale);
            var pixelWidth = PdfDocumentSession.EffectiveRenderWidth(page, desiredWidth);
            var bitmap = await session.RenderPageAsync(host.PageIndex, pixelWidth);

            if (_unloaded) return;
            if (generation != (_spreadActive ? _spreadGeneration : _generation)) return;

            host.ImageControl.Source = bitmap;
            host.ImageControl.Visibility = Visibility.Visible;
            host.Placeholder.Visibility = Visibility.Collapsed;
            host.Loaded = true;
        }
        catch (Exception ex)
        {
            if (_unloaded) return;
            if (generation != (_spreadActive ? _spreadGeneration : _generation)) return;

            host.Failed = true;
            host.Placeholder.Text = $"第 {host.PageIndex + 1} 页渲染失败";
            host.Placeholder.Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0x6A, 0x6A));
            if (vm is not null) vm.StatusText = $"第 {host.PageIndex + 1} 页渲染失败：{ex.Message}";
        }
        finally
        {
            host.RenderStarted = false;
        }
    }

    // ---------------------------------------------------------------- 双页翻页

    /// <summary>按当前 <see cref="_spreadStart"/> / 间距 / 缩放重排双页画面（不带动画）。</summary>
    private async Task ShowSpreadAsync(double? panCenterX = null, double? panCenterY = null)
    {
        if (!_spreadActive) return;

        var vm = Vm;
        if (vm?.Session is not { } session)
        {
            ClearSpreadVisuals();
            return;
        }

        var width = SpreadViewport.ActualWidth;
        var height = SpreadViewport.ActualHeight;
        if (width <= 1 || height <= 1) return;   // 还没量出来：等 SizeChanged

        ClearSpreadVisuals();
        var generation = _spreadGeneration;

        var layout = PdfSpreadLayout.Compute(
            session.Pages,
            _spreadStart,
            width,
            height,
            vm.Cfg.SpreadGapEffective,
            PdfReaderConfig.SideMarginPixels,
            vm.Cfg.ZoomScale);

        _spreadContentWidth = layout.ContentWidth;
        _spreadContentHeight = layout.ContentHeight;
        SpreadCanvas.Width = layout.ContentWidth;
        SpreadCanvas.Height = layout.ContentHeight;

        if (layout.Left is { } leftSlot)
        {
            _spreadLeft = CreatePageHost(leftSlot.PageIndex, leftSlot.X, leftSlot.Y, leftSlot.Width, leftSlot.Height);
            SpreadCanvas.Children.Add(_spreadLeft.Container);
        }

        if (layout.Right is { } rightSlot)
        {
            _spreadRight = CreatePageHost(rightSlot.PageIndex, rightSlot.X, rightSlot.Y, rightSlot.Width, rightSlot.Height);
            SpreadCanvas.Children.Add(_spreadRight.Container);
        }

        if (panCenterX is { } centerX && panCenterY is { } centerY)
            SetSpreadPanCenter(centerX, centerY);
        else
            SetSpreadPan(0, 0);

        UpdateSpreadChrome();
        UpdatePageIndicator();
        UpdateProgress();

        if (_spreadLeft is not null) await RenderHostAsync(_spreadLeft, generation);
        if (generation != _spreadGeneration) return;
        if (_spreadRight is not null) await RenderHostAsync(_spreadRight, generation);
    }

    private void ClearSpreadVisuals()
    {
        _spreadGeneration++;

        if (_spreadLeft is not null)
        {
            _spreadLeft.ImageControl.Source = null;
            _spreadLeft.Loaded = false;
        }
        if (_spreadRight is not null)
        {
            _spreadRight.ImageControl.Source = null;
            _spreadRight.Loaded = false;
        }

        _spreadLeft = null;
        _spreadRight = null;
        _spreadAnimating = false;
        _pendingSpreadForward = null;
        _spreadPanX = 0;
        _spreadPanY = 0;
        _spreadContentWidth = 0;
        _spreadContentHeight = 0;
        _spreadPanTransform = null;

        if (SpreadCanvas is not null)
        {
            SpreadCanvas.Children.Clear();
            SpreadCanvas.RenderTransform = new TranslateTransform();
        }
    }

    /// <summary>双页平移：内容跟着手走（放大到超出视口后才有得动）。</summary>
    private void SetSpreadPan(double x, double y)
    {
        var (clampedX, clampedY) = PdfSpreadLayout.ClampPan(
            x, y, _spreadContentWidth, _spreadContentHeight,
            SpreadViewport.ActualWidth, SpreadViewport.ActualHeight);

        _spreadPanX = clampedX;
        _spreadPanY = clampedY;

        _spreadPanTransform ??= new TranslateTransform();
        SpreadCanvas.RenderTransform = _spreadPanTransform;
        _spreadPanTransform.X = -_spreadPanX;
        _spreadPanTransform.Y = -_spreadPanY;
    }

    private (double X, double Y) CaptureSpreadPanCenter()
    {
        var width = SpreadViewport.ActualWidth;
        var height = SpreadViewport.ActualHeight;
        var centerX = _spreadContentWidth > width && _spreadContentWidth > 0
            ? Math.Clamp((_spreadPanX + width / 2) / _spreadContentWidth, 0, 1)
            : 0.5;
        var centerY = _spreadContentHeight > height && _spreadContentHeight > 0
            ? Math.Clamp((_spreadPanY + height / 2) / _spreadContentHeight, 0, 1)
            : 0.5;
        return (centerX, centerY);
    }

    private void SetSpreadPanCenter(double centerX, double centerY)
    {
        SetSpreadPan(
            centerX * _spreadContentWidth - SpreadViewport.ActualWidth / 2,
            centerY * _spreadContentHeight - SpreadViewport.ActualHeight / 2);
    }

    /// <summary>翻一屏（一次翻两页）：先把新页渲染好，再让旧页滑出、新页滑入。</summary>
    private async Task TurnSpreadAsync(bool forward)
    {
        if (!_spreadActive) return;

        if (_spreadAnimating)
        {
            _pendingSpreadForward = forward;   // 动画期间又翻了：记下来，动画结束补跑
            return;
        }

        var vm = Vm;
        if (vm?.Session is not { } session) return;

        var target = PdfSpreadLayout.Step(_spreadStart, session.PageCount, forward);
        if (target == _spreadStart) return;

        _spreadAnimating = true;
        var generation = ++_spreadGeneration;
        try
        {
            var width = SpreadViewport.ActualWidth;
            var height = SpreadViewport.ActualHeight;
            if (width <= 1 || height <= 1) return;

            var layout = PdfSpreadLayout.Compute(
                session.Pages,
                target,
                width,
                height,
                vm.Cfg.SpreadGapEffective,
                PdfReaderConfig.SideMarginPixels,
                vm.Cfg.ZoomScale);

            _spreadContentWidth = layout.ContentWidth;
            _spreadContentHeight = layout.ContentHeight;
            SpreadCanvas.Width = layout.ContentWidth;
            SpreadCanvas.Height = layout.ContentHeight;

            var oldHosts = new List<PageHost>(2);
            if (_spreadLeft is not null) oldHosts.Add(_spreadLeft);
            if (_spreadRight is not null) oldHosts.Add(_spreadRight);

            PageHost? newLeft = null;
            PageHost? newRight = null;
            if (layout.Left is { } leftSlot)
            {
                newLeft = CreatePageHost(leftSlot.PageIndex, leftSlot.X, leftSlot.Y, leftSlot.Width, leftSlot.Height);
                SpreadCanvas.Children.Add(newLeft.Container);
            }
            if (layout.Right is { } rightSlot)
            {
                newRight = CreatePageHost(rightSlot.PageIndex, rightSlot.X, rightSlot.Y, rightSlot.Width, rightSlot.Height);
                SpreadCanvas.Children.Add(newRight.Container);
            }

            // 旧页继续显示，新页在后台渲染好再滑进来（不闪白）
            if (newLeft is not null) await RenderHostAsync(newLeft, generation);
            if (generation != _spreadGeneration) return;
            if (newRight is not null) await RenderHostAsync(newRight, generation);
            if (generation != _spreadGeneration) return;

            var newHosts = new List<PageHost>(2);
            if (newLeft is not null) newHosts.Add(newLeft);
            if (newRight is not null) newHosts.Add(newRight);

            var slide = forward ? -SpreadSlidePixels : SpreadSlidePixels;   // 旧页往哪边滑出
            var duration = SpreadAnimationMilliseconds;
            var animations = new List<Task>();
            var zoomed = _spreadPanX > 0.5 || _spreadPanY > 0.5
                         || _spreadContentWidth > width + 0.5 || _spreadContentHeight > height + 0.5;
            if (!zoomed)
            {
                foreach (var oldHost in oldHosts)
                    animations.Add(PlaySlideAsync(oldHost.Container, 0, slide, 1, 0, duration));
                foreach (var newHost in newHosts)
                    animations.Add(PlaySlideAsync(newHost.Container, -slide, 0, 0, 1, duration));
                await Task.WhenAll(animations);
                if (generation != _spreadGeneration) return;
            }

            foreach (var oldHost in oldHosts) SpreadCanvas.Children.Remove(oldHost.Container);
            foreach (var newHost in newHosts)
            {
                newHost.Container.RenderTransform = new TranslateTransform();
                newHost.Container.Opacity = 1;
            }

            _spreadLeft = newLeft;
            _spreadRight = newRight;
            _spreadStart = target;

            // 翻页后回到左上角（放大状态下也不要把上一屏的平移带过去）
            SetSpreadPan(0, 0);
            UpdateSpreadChrome();
            UpdatePageIndicator();
            UpdateProgress();
        }
        finally
        {
            _spreadAnimating = false;

            if (_pendingSpreadForward is { } pending)
            {
                _pendingSpreadForward = null;
                if (!_unloaded && _spreadActive) await TurnSpreadAsync(pending);
            }
        }
    }

    /// <summary>双页跳到某一屏（Home / End / 搜索 / 目录用）。</summary>
    private void GoToSpreadStart(int pageIndex)
    {
        if (!_spreadActive) return;
        var count = Vm?.PageCount ?? 0;
        var normalized = PdfSpreadLayout.NormalizeStart(pageIndex, count);
        if (normalized == _spreadStart) return;

        _spreadStart = normalized;
        _pendingSpreadForward = null;
        _ = ShowSpreadAsync();
    }

    private void OnPrevPageClick(object sender, RoutedEventArgs e) => _ = TurnSpreadAsync(forward: false);

    private void OnNextPageClick(object sender, RoutedEventArgs e) => _ = TurnSpreadAsync(forward: true);

    private void UpdateSpreadChrome()
    {
        var count = Vm?.PageCount ?? 0;
        var canPrevious = PdfSpreadLayout.CanGoPrevious(_spreadStart, count);
        var canNext = PdfSpreadLayout.CanGoNext(_spreadStart, count);

        PrevPageButton.IsEnabled = canPrevious;
        NextPageButton.IsEnabled = canNext;
        ToolbarPrevButton.IsEnabled = canPrevious;
        ToolbarNextButton.IsEnabled = canNext;
    }

    // ---------------------------------------------------------------- 缩放 / 窗口尺寸变化后的重排

    private async Task RebuildKeepingCenterAsync()
    {
        if (_unloaded) return;
        var vm = Vm;
        if (vm?.HasDocument != true || vm.IsBusy) return;

        if (_building)
        {
            _buildQueued = true;
            _buildQueuedKeepPage = true;
            return;
        }

        if (_spreadActive)
        {
            await RebuildSpreadKeepingCenterAsync();
            return;
        }

        if (_layout is null)
        {
            await BuildAsync(keepPage: true);
            return;
        }

        var center = CaptureViewportCenter();
        await BuildContinuousAsync(CurrentPageIndex(), center);
    }

    private async Task RebuildSpreadKeepingCenterAsync()
    {
        if (!_spreadActive) return;
        var center = CaptureSpreadPanCenter();
        await ShowSpreadAsync(center.X, center.Y);
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layoutQueued) return;
        _layoutQueued = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _layoutQueued = false;
            _ = RebuildForViewportChangeAsync();
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// 双页视口自己量出尺寸时补一次排版：SpreadHost 刚从 Collapsed 变 Visible 时 ActualWidth 还是 0，
    /// 第一次双页排版会拿到 0 而直接返回 —— 不补这一次，双页模式就会停在「只有页码、没有页」的空屏（踩过）。
    /// </summary>
    private void OnSpreadViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_spreadActive || _spreadAnimating || _unloaded) return;
        if (_spreadLeft is not null || _spreadRight is not null) return;   // 已有画面：窗口缩放走 ViewportGrid 的 handler

        _ = ShowSpreadAsync();
    }

    private async Task RebuildForViewportChangeAsync()
    {
        if (_unloaded) return;
        var vm = Vm;
        if (vm?.HasDocument != true || vm.IsBusy) return;
        if (ViewportGrid.ActualWidth <= 1 || ViewportGrid.ActualHeight <= 1) return;

        await RebuildKeepingCenterAsync();
    }

    // ---------------------------------------------------------------- 拖拽 + 惯性

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm || !vm.HasDocument || vm.IsBusy) return;
        if (Mouse.Captured is not null && !ReferenceEquals(Mouse.Captured, this)) return;

        var source = (e.OriginalSource ?? e.Source) as DependencyObject;
        if (!IsFromViewport(source) || IsInsideOverlayPanel(source)) return;

        e.Handled = true;
        AbortInertia();

        // Ctrl+滚轮 = 以画面中心缩放（两种模式都生效）
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Delta > 0) vm.ZoomInCommand.Execute(null);
            else if (e.Delta < 0) vm.ZoomOutCommand.Execute(null);
            return;
        }

        var step = vm.Cfg.WheelStepEffective;

        if (_spreadActive)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                var beforePan = _spreadPanY;
                SetSpreadPan(_spreadPanX, beforePan + (e.Delta < 0 ? step : -step));
                StartWheelInertia(0, _spreadPanY - beforePan, step);
                return;
            }

            _ = TurnSpreadAsync(forward: e.Delta < 0);
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            var beforeCross = GetCrossOffset();
            SetCrossOffset(beforeCross + (e.Delta < 0 ? step : -step));
            StartWheelInertia(0, GetCrossOffset() - beforeCross, step);
            return;
        }

        var beforeMain = GetReadingOffset();
        SetReadingOffset(beforeMain + (e.Delta < 0 ? step : -step));
        StartWheelInertia(GetReadingOffset() - beforeMain, 0, step);
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var vm = Vm;
        if (vm?.HasDocument != true) return;

        // 别的控件正握着鼠标（ComboBox 下拉弹层 / 输入框选字）时整个都别碰
        if (Mouse.Captured is not null && !ReferenceEquals(Mouse.Captured, this)) return;
        if (e.ChangedButton != MouseButton.Left) return;

        var source = e.OriginalSource as DependencyObject;
        if (!IsFromViewport(source)) return;
        if (IsOnClickableControl(source)) return;   // 翻页按钮的点击不能被拖拽吃掉
        if (IsInsideOverlayPanel(source)) return;   // 目录 / 设置面板上的点击不拖画面

        e.Handled = true;
        BeginDrag(e);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        WakeControls();   // 全屏时鼠标一动先把工具条亮出来（窗口模式里是空转）

        if (!_dragging) return;

        var position = e.GetPosition(this);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < DragThresholdPixels) return;

        _dragMoved = true;
        e.Handled = true;

        if (_spreadActive)
        {
            // 双页放大后：内容跟着手走
            SetSpreadPan(_dragStartPanX - dx, _dragStartPanY - dy);
        }
        else
        {
            // 阅读轴沿用原来的方向规则，跨轴按「内容跟着手走」算
            var reading = _dragStartReading + (_horizontal ? -dx : -dy);
            var cross = _dragStartCross + (_horizontal ? -dy : -dx);
            SetReadingOffset(reading);
            SetCrossOffset(cross);
        }

        // 惯性速度用「实际生效的偏移」采样：撞到边界就不动了，松手自然不会在边上滑
        var (mainOffset, crossOffset) = GetInertiaOffsets();
        _inertia.Note(NowMilliseconds(), mainOffset, crossOffset);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_dragging) return;

        var moved = _dragMoved;
        var (mainVelocity, crossVelocity) = moved ? _inertia.ComputeVelocity() : (0.0, 0.0);
        EndDrag();

        if (moved)
        {
            e.Handled = true;
            StartInertia(mainVelocity, crossVelocity);
        }
        else
        {
            AbortInertia();   // 只是点了一下：把残余速度扔掉，别继续滑
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_releasingMouse) return;
        if (!_dragging) return;

        _dragging = false;
        _dragMoved = false;
        Cursor = null;
    }

    private void BeginDrag(MouseButtonEventArgs e)
    {
        // 连续甩动越滑越快：先把残余速度接过来（惯性设置开着才叠加），再停掉当前惯性
        var accumulate = Vm?.Cfg.InertiaEnabled == true;
        _carriedMainVelocity = accumulate ? _inertiaMainVelocity : 0;
        _carriedCrossVelocity = accumulate ? _inertiaCrossVelocity : 0;
        CancelInertia();

        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(this);
        _dragStartReading = GetReadingOffset();
        _dragStartCross = GetCrossOffset();
        _dragStartPanX = _spreadPanX;
        _dragStartPanY = _spreadPanY;
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
        ReleaseMouseCaptureSafely();
    }

    private void ReleaseMouseCaptureSafely()
    {
        if (!IsMouseCaptured) return;
        _releasingMouse = true;
        try { ReleaseMouseCapture(); }
        finally { _releasingMouse = false; }
    }

    private void StartInertia(double sampleMainVelocity, double sampleCrossVelocity)
    {
        if (Vm is not { } vm || !vm.Cfg.InertiaEnabled)
        {
            AbortInertia();
            return;
        }

        var (mainVelocity, crossVelocity) = PdfInertia.Combine(
            sampleMainVelocity, sampleCrossVelocity,
            _carriedMainVelocity, _carriedCrossVelocity,
            accumulate: true, PdfInertia.DefaultMaxSpeed);
        _carriedMainVelocity = 0;
        _carriedCrossVelocity = 0;

        if (!_inertia.IsFastEnough(mainVelocity, crossVelocity)) return;

        _inertiaMainVelocity = mainVelocity;
        _inertiaCrossVelocity = crossVelocity;

        StartInertiaTimer();
    }

    /// <summary>
    /// 滚轮惯性：滚一格后用滚轮步长算初速度继续滑一段（和拖动惯性分开，设置里可关）。
    /// <paramref name="appliedMain"/> / <paramref name="appliedCross"/> 是这一格**实际**走掉的偏移
    /// （撞到边界可能不足一格甚至为 0），方向直接取它的符号，横向 / 反向阅读模式也不会滑反。
    /// </summary>
    private void StartWheelInertia(double appliedMain, double appliedCross, double stepPixels)
    {
        if (Vm is not { } vm || !vm.Cfg.WheelInertiaEnabled) return;
        if (Math.Abs(appliedMain) < 0.01 && Math.Abs(appliedCross) < 0.01) return;   // 已经在头 / 尾了

        var velocity = PdfInertia.WheelVelocity(stepPixels);
        _inertiaMainVelocity = Math.Abs(appliedMain) >= 0.01 ? Math.Sign(appliedMain) * velocity : 0;
        _inertiaCrossVelocity = Math.Abs(appliedCross) >= 0.01 ? Math.Sign(appliedCross) * velocity : 0;

        if (!_inertia.IsFastEnough(_inertiaMainVelocity, _inertiaCrossVelocity)) return;

        StartInertiaTimer();
    }

    /// <summary>启动 / 重启惯性动画（拖动松手与滚轮惯性共用同一套定时器与衰减逻辑）。</summary>
    private void StartInertiaTimer()
    {
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
        if ((!_spreadActive && _layout is null) || (_spreadActive && _spreadContentWidth <= 0))
        {
            CancelInertia();
            return;
        }

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

    /// <summary>彻底停下惯性：动画 + 残余速度一起清掉（滚轮 / 换模式 / 切工具时用）。</summary>
    private void AbortInertia()
    {
        CancelInertia();
        _carriedMainVelocity = 0;
        _carriedCrossVelocity = 0;
    }

    /// <summary>惯性采样 / 施加用的「当前偏移」：连续模式是两个轴，双页模式是平移。</summary>
    private (double Main, double Cross) GetInertiaOffsets()
        => _spreadActive ? (_spreadPanX, _spreadPanY) : (GetReadingOffset(), GetCrossOffset());

    private void ApplyInertiaOffsets(double main, double cross)
    {
        if (_spreadActive) SetSpreadPan(main, cross);
        else
        {
            SetReadingOffset(main);
            SetCrossOffset(cross);
        }
    }

    // ---------------------------------------------------------------- 文字搜索

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ScheduleSearch();

    private void ScheduleSearch()
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ClearSearch(clearText: false);
            return;
        }

        _pendingSearchQuery = query;

        if (_searchDebounceTimer is null)
        {
            _searchDebounceTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(SearchDebounceMilliseconds),
            };
            _searchDebounceTimer.Tick += (_, _) =>
            {
                _searchDebounceTimer?.Stop();
                var pending = _pendingSearchQuery;
                _pendingSearchQuery = null;
                if (!string.IsNullOrEmpty(pending)) RunSearch(pending, autoJump: false);
            };
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            var forward = (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift;
            RunSearch(SearchBox.Text.Trim(), autoJump: true, forward: forward);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ClearSearch(clearText: true);
            Keyboard.ClearFocus();
        }
    }

    /// <summary>跑一次搜索；<paramref name="autoJump"/> = 搜完直接跳到第一个 / 最后一个命中。</summary>
    private void RunSearch(string query, bool autoJump, bool forward = true)
    {
        _pendingSearchQuery = null;

        var vm = Vm;
        if (vm is null || !vm.HasDocument)
        {
            _searchHits = Array.Empty<PdfSearchHit>();
            _searchIndex = -1;
            UpdateSearchChrome();
            return;
        }

        query = query.Trim();
        if (query.Length == 0)
        {
            _searchHits = Array.Empty<PdfSearchHit>();
            _searchIndex = -1;
            UpdateSearchChrome();
            return;
        }

        if (vm.TextLayer is null)
        {
            _searchHits = Array.Empty<PdfSearchHit>();
            _searchIndex = -1;
            if (vm.IsTextLayerLoading) _pendingSearchQuery = query;   // 提取完自动再搜
            UpdateSearchChrome();
            return;
        }

        _searchHits = vm.Search(query);
        _searchIndex = _searchHits.Count == 0 ? -1 : (forward ? 0 : _searchHits.Count - 1);
        UpdateSearchChrome();

        if (autoJump && _searchIndex >= 0) JumpToHit(_searchIndex);
    }

    private void OnSearchNextClick(object sender, RoutedEventArgs e) => StepSearch(1);

    private void OnSearchPrevClick(object sender, RoutedEventArgs e) => StepSearch(-1);

    private void StepSearch(int delta)
    {
        if (_searchHits.Count == 0)
        {
            RunSearch(SearchBox.Text.Trim(), autoJump: true, forward: delta >= 0);
            return;
        }

        var count = _searchHits.Count;
        _searchIndex = ((_searchIndex + delta) % count + count) % count;
        UpdateSearchChrome();
        JumpToHit(_searchIndex);
    }

    private void JumpToHit(int index)
    {
        if (index < 0 || index >= _searchHits.Count) return;
        JumpToPage(_searchHits[index].PageIndex);
        UpdateSearchChrome();
    }

    private void OnSearchClearClick(object sender, RoutedEventArgs e) => ClearSearch(clearText: true);

    private void ClearSearch(bool clearText)
    {
        _searchHits = Array.Empty<PdfSearchHit>();
        _searchIndex = -1;
        if (clearText) SearchBox.Text = "";
        UpdateSearchChrome();
    }

    private void UpdateSearchChrome()
    {
        if (SearchBox is null) return;

        var vm = Vm;
        var hasDocument = vm?.HasDocument == true;
        SearchBox.IsEnabled = hasDocument;
        SearchPrevButton.IsEnabled = hasDocument && _searchHits.Count > 0;
        SearchNextButton.IsEnabled = hasDocument && _searchHits.Count > 0;
        SearchClearButton.IsEnabled = hasDocument
                                       && (SearchBox.Text.Length > 0 || _searchHits.Count > 0);

        if (!hasDocument)
        {
            SearchCountText.Text = "";
            return;
        }

        var query = SearchBox.Text.Trim();
        if (_searchHits.Count > 0)
        {
            SearchCountText.Text = $"{_searchIndex + 1} / {_searchHits.Count} 处";
            return;
        }

        if (query.Length == 0)
        {
            SearchCountText.Text = vm?.IsTextLayerLoading == true ? "提取文字…" : "";
            return;
        }

        SearchCountText.Text = vm?.IsTextLayerLoading == true
            ? "正在提取文字…"
            : vm?.TextLayer is null || !vm.TextLayer.HasText
                ? "无可搜索文字"
                : "没有找到";
    }

    /// <summary>跳到指定页（连续模式滚到该页开头；双页模式翻到该页所在的一屏）。</summary>
    private void JumpToPage(int pageIndex)
    {
        var vm = Vm;
        if (vm?.Session is not { } session) return;

        var page = Math.Clamp(pageIndex, 0, session.PageCount - 1);

        if (_spreadActive)
        {
            _spreadStart = PdfSpreadLayout.StartForPage(page, session.PageCount);
            _pendingSpreadForward = null;
            _ = ShowSpreadAsync();
            return;
        }

        if (_layout is null) return;

        SetReadingOffset(PdfContinuousLayout.OffsetOfPage(_layout, _builtMode, page));
        SetCrossOffset(0);
    }

    // ---------------------------------------------------------------- 目录 / 设置面板

    private void UpdateOutlineChrome()
    {
        if (OutlineEmptyText is null) return;

        var vm = Vm;
        var hasOutline = vm?.HasOutline == true;
        OutlineEmptyText.Visibility = hasOutline ? Visibility.Collapsed : Visibility.Visible;
        OutlineEmptyText.Text = vm is null
            ? ""
            : vm.IsTextLayerLoading
                ? "正在读取目录…"
                : (!string.IsNullOrWhiteSpace(vm.TextLayerStatusText)
                    ? vm.TextLayerStatusText
                    : "这个 PDF 没有书签目录。");
    }

    private void UpdateTextLayerHint()
    {
        if (TextLayerHintText is null) return;

        var vm = Vm;
        TextLayerHintText.Text = vm?.HasDocument != true
            ? ""
            : vm.IsTextLayerLoading
                ? "正在提取文字与目录…"
                : vm.TextLayer is null
                    ? "文字层不可用"
                    : !vm.TextLayer.HasText
                        ? "这个 PDF 没有可搜索的文字"
                        : vm.HasOutline
                            ? $"目录 {vm.OutlineEntries.Count} 项 · Ctrl+F 搜索"
                            : "Ctrl+F 搜索文字";
    }

    private void OnOutlineItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int pageIndex }) JumpToPage(pageIndex);
    }

    private void OnCloseOutlineClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsOutlineVisible = false;
    }

    private void OnCloseSettingsClick(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) vm.IsSettingsVisible = false;
    }

    // ---------------------------------------------------------------- 键盘

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O)
        {
            e.Handled = true;
            vm.OpenPdfCommand.Execute(null);
            return;
        }

        // Ctrl+F：聚焦搜索框（正在输入框里也允许，方便再按一次全选）
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
            return;
        }

        // F11：全屏 / 退出全屏（没打开 PDF 也能切，壳不会藏）
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F11)
        {
            e.Handled = true;
            vm.ToggleFullscreen();
            return;
        }

        if (!vm.HasDocument || vm.IsBusy) return;

        // Esc：关搜索 → 关目录 / 设置面板 → 退出全屏
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            if (SearchBox.IsKeyboardFocusWithin)
            {
                ClearSearch(clearText: true);
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }
            if (vm.IsRecentVisible)
            {
                vm.IsRecentVisible = false;
                e.Handled = true;
                return;
            }
            if (vm.IsOutlineVisible || vm.IsSettingsVisible)
            {
                vm.IsOutlineVisible = false;
                vm.IsSettingsVisible = false;
                e.Handled = true;
                return;
            }
            if (vm.IsFullscreen)
            {
                e.Handled = true;
                vm.ToggleFullscreen();
                return;
            }
        }

        // 正在输入框 / 下拉框里时别抢键
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem or ListBox or ListBoxItem
            or Slider or CheckBox or RadioButton) return;
        if (Keyboard.Modifiers != ModifierKeys.None) return;

        if (_spreadActive)
        {
            switch (e.Key)
            {
                case Key.Left:
                case Key.PageUp:
                    e.Handled = true;
                    _ = TurnSpreadAsync(forward: false);
                    return;

                case Key.Right:
                case Key.PageDown:
                case Key.Space:
                    e.Handled = true;
                    _ = TurnSpreadAsync(forward: true);
                    return;

                case Key.Home:
                    e.Handled = true;
                    GoToSpreadStart(0);
                    return;

                case Key.End:
                    e.Handled = true;
                    GoToSpreadStart(PdfSpreadLayout.LastStartIndex(vm.PageCount));
                    return;
            }
            return;
        }

        var smallStep = vm.Cfg.WheelStepEffective;
        var bigStep = Math.Max(smallStep, ReadingViewportLength * 0.9);

        switch (e.Key)
        {
            case Key.Up:
            case Key.Left:
                e.Handled = true;
                SetReadingOffset(GetReadingOffset() - smallStep);
                break;

            case Key.Down:
            case Key.Right:
                e.Handled = true;
                SetReadingOffset(GetReadingOffset() + smallStep);
                break;

            case Key.PageUp:
                e.Handled = true;
                SetReadingOffset(GetReadingOffset() - bigStep);
                break;

            case Key.PageDown:
            case Key.Space:
                e.Handled = true;
                SetReadingOffset(GetReadingOffset() + bigStep);
                break;

            case Key.Home:
                e.Handled = true;
                SetReadingOffset(0);
                break;

            case Key.End:
                e.Handled = true;
                SetReadingOffset(MaximumOffset);
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

    // ---------------------------------------------------------------- 界面 chrome（提示 / 页码 / 进度）

    private void UpdateModeChrome()
    {
        var vm = Vm;
        var hasDocument = vm?.HasDocument == true;

        PageIndicatorPanel.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        // 全屏沉浸阅读时底部提示条也藏起来（页码保留，底部进度条保留）
        HintPanel.Visibility = hasDocument && vm?.IsFullscreen != true
            ? Visibility.Visible
            : Visibility.Collapsed;
        InertiaCheckBox.IsEnabled = hasDocument;
        UpdatePageJumpChrome();

        UpdateHint();
    }

    private void UpdateHint()
    {
        HintText.Text = Mode switch
        {
            PdfReadMode.Horizontal => "滚轮左右滚 · 左键拖动带惯性 · Ctrl+滚轮缩放 · 目录 / 搜索点顶部",
            PdfReadMode.Spread => "滚轮 / ← → / PageUp PageDown / 空格 翻页 · 放大后左键拖动平移 · Ctrl+滚轮缩放",
            _ => "滚轮上下滚 · 左键拖动带惯性 · Ctrl+滚轮缩放 · 目录 / 搜索点顶部",
        };
    }

    private void UpdateEmptyStatus()
    {
        var vm = Vm;
        if (vm is null) return;

        var show = !vm.HasDocument
                   && !string.IsNullOrWhiteSpace(vm.StatusText)
                   && vm.StatusText != "就绪";

        EmptyStatusText.Text = show ? vm.StatusText : "";
        EmptyStatusText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 状态栏变成错误文案时，在阅读区底部弹一条提示（几秒后自动消失）。
    /// 打开 PDF 失败 / 某页渲染失败都走这里 —— 用户不用去别处找原因。
    /// </summary>
    private void MaybeShowStatusError()
    {
        var vm = Vm;
        if (vm is null) return;

        var status = vm.StatusText ?? "";
        if (status == _lastStatusText) return;
        _lastStatusText = status;

        if (IsErrorStatus(status)) ShowErrorToast(status);
    }

    private static bool IsErrorStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status) || status == "就绪") return false;
        // 成功文案都以「已打开」开头；其余带这些关键词的都当错误提示
        if (status.StartsWith("已打开", StringComparison.Ordinal)) return false;
        return status.StartsWith("PDF 打不开", StringComparison.Ordinal)
               || status.StartsWith("文件不存在", StringComparison.Ordinal)
               || status.StartsWith("没有权限", StringComparison.Ordinal)
               || status.StartsWith("这个 PDF", StringComparison.Ordinal)
               || status.StartsWith("文件路径", StringComparison.Ordinal)
               || status.StartsWith("演示 PDF", StringComparison.Ordinal)
               || status.Contains("失败", StringComparison.Ordinal);
    }

    private void ShowErrorToast(string text)
    {
        ErrorToastText.Text = text;
        ErrorToast.Visibility = Visibility.Visible;

        if (_errorToastTimer is null)
        {
            _errorToastTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(5),
            };
            _errorToastTimer.Tick += (_, _) =>
            {
                _errorToastTimer?.Stop();
                ErrorToast.Visibility = Visibility.Collapsed;
            };
        }

        _errorToastTimer.Stop();
        _errorToastTimer.Start();
    }

    private void UpdatePageIndicator()
    {
        var vm = Vm;
        var count = vm?.PageCount ?? 0;
        if (vm?.HasDocument != true || count == 0)
        {
            PageIndicatorPanel.Visibility = Visibility.Collapsed;
            PageIndicatorText.Text = "";
            return;
        }

        PageIndicatorPanel.Visibility = Visibility.Visible;

        if (_spreadActive)
        {
            var (left, right) = PdfSpreadLayout.VisiblePages(_spreadStart, count);
            PageIndicatorText.Text = (left, right) switch
            {
                ({ } l, { } r) => $"第 {l + 1}–{r + 1} 页 / 共 {count} 页",
                ({ } l, null) => $"第 {l + 1} 页 / 共 {count} 页",
                _ => $"共 {count} 页",
            };

            var currentStart = PdfSpreadLayout.NormalizeStart(_spreadStart, count);
            UpdatePageJumpTooltip(currentStart);
            Vm?.NoteReadingPosition(currentStart, PdfSpreadLayout.ProgressRatio(currentStart, count));
            return;
        }

        if (_layout is null || _layout.IsEmpty)
        {
            PageIndicatorText.Text = $"共 {count} 页";
            return;
        }

        var page = PdfContinuousLayout.PageIndexAtOffset(_layout, _builtMode, GetReadingOffset());
        PageIndicatorText.Text = $"第 {page + 1} / {count} 页";
        UpdatePageJumpTooltip(page);
        Vm?.NoteReadingPosition(page, PdfContinuousLayout.RatioOf(GetReadingOffset(), MaximumOffset));
    }

    /// <summary>跳页输入框的可用状态（没打开 PDF 时禁用并清空）。</summary>
    private void UpdatePageJumpChrome()
    {
        if (PageJumpBox is null) return;

        var hasDocument = Vm?.HasDocument == true;
        PageJumpBox.IsEnabled = hasDocument;
        PageJumpButton.IsEnabled = hasDocument;
        if (!hasDocument) PageJumpBox.Clear();
    }

    /// <summary>跳页输入框的悬停提示带上总页数 / 当前页（不用额外占界面位置）。</summary>
    private void UpdatePageJumpTooltip(int currentPageIndex)
    {
        if (PageJumpBox is null) return;

        var count = Vm?.PageCount ?? 0;
        PageJumpBox.ToolTip = count > 0
            ? $"输入页码后回车跳到该页（1 ~ {count}）；当前第 {Math.Clamp(currentPageIndex + 1, 1, count)} 页"
            : "输入页码后回车跳到该页";
    }

    private void OnPageJumpBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TryJumpToPageInput();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            PageJumpBox.Clear();
            Keyboard.ClearFocus();
        }
    }

    private void OnPageJumpClick(object sender, RoutedEventArgs e) => TryJumpToPageInput();

    /// <summary>读「跳页」输入框 → 校验页码 → 跳到对应页；非法页码弹提示并把输入框全选。</summary>
    private void TryJumpToPageInput()
    {
        var vm = Vm;
        if (vm?.HasDocument != true) return;

        if (!vm.TryParsePageNumber(PageJumpBox.Text, out var pageIndex))
        {
            ShowErrorToast($"页码要填 1 ~ {vm.PageCount} 之间的整数");
            PageJumpBox.SelectAll();
            PageJumpBox.Focus();
            return;
        }

        JumpToPage(pageIndex);
        PageJumpBox.Clear();
        Keyboard.ClearFocus();
    }

    private void UpdateProgress()
    {
        if (ReadingProgressFill is null) return;

        var ratio = _spreadActive
            ? PdfSpreadLayout.ProgressRatio(_spreadStart, Vm?.PageCount ?? 0)
            : PdfContinuousLayout.RatioOf(GetReadingOffset(), MaximumOffset);

        ReadingProgressFill.HorizontalAlignment = HorizontalAlignment.Left;
        ReadingProgressFill.Width = Math.Clamp(ratio, 0, 1) * Math.Max(0, ViewportGrid.ActualWidth);
    }

    // ---------------------------------------------------------------- 通用小工具

    /// <summary>造一个「页占位 + Image」的界面对象，并摆到 Canvas 上。</summary>
    private PageHost CreatePageHost(int pageIndex, double x, double y, double width, double height)
    {
        var safeWidth = Math.Max(1, width);
        var safeHeight = Math.Max(1, height);

        var placeholder = new TextBlock
        {
            Text = $"第 {pageIndex + 1} 页",
            FontSize = Math.Clamp(Math.Min(safeWidth, safeHeight) / 12, 12, 26),
            Foreground = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var image = new Image
        {
            Stretch = Stretch.Fill,
            Visibility = Visibility.Collapsed,
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        var content = new Grid();
        content.Children.Add(placeholder);
        content.Children.Add(image);

        var border = new Border
        {
            Width = safeWidth,
            Height = safeHeight,
            Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1A, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = content,
            SnapsToDevicePixels = true,
        };

        Canvas.SetLeft(border, x);
        Canvas.SetTop(border, y);

        return new PageHost
        {
            PageIndex = pageIndex,
            Container = border,
            ImageControl = image,
            Placeholder = placeholder,
        };
    }

    /// <summary>翻页动画：把元素从 fromX 平移到 toX、透明度 from → to，动画结束后落到终值。</summary>
    private static async Task PlaySlideAsync(
        FrameworkElement element, double fromX, double toX, double fromOpacity, double toOpacity, int durationMilliseconds)
    {
        var duration = TimeSpan.FromMilliseconds(durationMilliseconds);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        var transform = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = transform;
        transform.X = fromX;
        element.Opacity = fromOpacity;

        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(fromX, toX, duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd });
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(fromOpacity, toOpacity, duration) { EasingFunction = easing, FillBehavior = FillBehavior.HoldEnd });

        await Task.Delay(durationMilliseconds);

        transform.BeginAnimation(TranslateTransform.XProperty, null);
        element.BeginAnimation(OpacityProperty, null);
        transform.X = toX;
        element.Opacity = toOpacity;
    }

    /// <summary>事件源是不是「阅读区里的东西」（工具条 / 下拉弹层不算）。</summary>
    private bool IsFromViewport(DependencyObject? source)
    {
        if (source is null) return false;
        if (ReferenceEquals(source, ViewportGrid)) return true;
        return source is Visual && ViewportGrid.IsAncestorOf(source);
    }

    /// <summary>这一下点击是不是落在按钮之类的可点击控件上（翻页按钮的点击不能被拖拽吃掉）。</summary>
    private bool IsOnClickableControl(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, ViewportGrid))
        {
            if (source is ButtonBase or TextBoxBase or Slider or ComboBox) return true;
            source = source is Visual visual
                ? VisualTreeHelper.GetParent(visual)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>点击 / 滚轮是不是落在「目录 / 设置」浮层上（浮层里的操作不能带动后面的页面）。</summary>
    private bool IsInsideOverlayPanel(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, ViewportGrid))
        {
            if (ReferenceEquals(source, OutlinePanel) || ReferenceEquals(source, SettingsPanel)) return true;
            source = source is Visual visual
                ? VisualTreeHelper.GetParent(visual)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static double NowMilliseconds() => Environment.TickCount64;

    /// <summary>
    /// 开发 / 截图用：<c>YLT_PDF_SCROLL=0~1</c> 在连续模式搭好长带后先滚到指定比例
    /// （核对横向模式 / 长带布局用；不设环境变量时完全是空转）。
    /// </summary>
    private void ApplyDebugScrollHook()
    {
        var text = Environment.GetEnvironmentVariable("YLT_PDF_SCROLL");
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)) return;

        SetReadingOffset(PdfContinuousLayout.OffsetForRatio(ratio, MaximumOffset));
    }
}
