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
using YuLiangToolkit.Tools.PdfReader.Rendering;
using YuLiangToolkit.Tools.PdfReader.ViewModels;

namespace YuLiangToolkit.Tools.PdfReader.Views;

/// <summary>
/// 「PDF 阅读器」的界面后台。这个工具的核心是「PDF 按需渲染 + 自己接管滚动」，所以代码后置比一般功能重一些，
/// 分工仍然是清楚的：
///
/// <list type="bullet">
///   <item><b>纯逻辑</b>（页怎么排、滚多少、双页怎么配对、惯性怎么衰减）全在
///         <see cref="PdfContinuousLayout"/>、<see cref="PdfSpreadLayout"/>、<see cref="PdfInertia"/> 里，自检直接断言；</item>
///   <item><b>这里</b>只做必须碰界面的部分：把可见页渲染成 Image、滚动偏移、鼠标拖拽 / 惯性、双页翻页动画、键盘；</item>
///   <item><b>ViewModel</b> 只放设置、打开的 PDF 与状态文案。</item>
/// </list>
///
/// <para><b>「只渲染可见页」</b>：PDF 可能几百上千页，不可能每页都渲染成位图。连续模式用 Canvas 摆一条长带，
/// 只给视口附近（前后各一屏缓冲）的页创建 Image 并按需渲染，滚出范围的释放掉 —— 大文件也不会把内存吃满。
/// 所以快速甩动时可能短暂看到占位底色，停一下就会补齐。</para>
///
/// <para><b>「阅读偏移」</b>：0 = 第一页开头，最大值 = 最后一页末尾；由 <see cref="PdfContinuousLayout"/> 算，
/// 界面自己记（不每帧反读 ScrollViewer 的物理偏移，原因见 docs/dev/pitfalls.md「图片连续阅读器」那条）。</para>
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

    // ---------------------------------------------------------------- 拖动惯性

    private readonly PdfInertia _inertia = new();
    private DispatcherTimer? _inertiaTimer;
    private double _inertiaVelocity;
    private long _inertiaLastTimestamp;

    /// <summary>「还在滑的残余速度」：下一次拖拽开始时先存下来，松手时叠加（越甩越快）。</summary>
    private double _carriedVelocity;

    // ---------------------------------------------------------------- 连续滚动状态

    private PdfContinuousLayoutResult? _layout;
    private PdfReadMode _builtMode = PdfReadMode.Vertical;
    private bool _horizontal;

    /// <summary>「阅读偏移」的唯一真值（0 = 第一页开头）。</summary>
    private double _readingOffset;

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

    // ---------------------------------------------------------------- 左键拖拽

    private bool _dragging;
    private bool _dragMoved;
    private bool _releasingMouse;
    private Point _dragStart;
    private double _dragStartOffset;

    // ---------------------------------------------------------------- 其他

    private Window? _hostWindow;
    private bool _unloaded;
    private bool _demoStarted;
    private bool _loadingFromDemo;
    private bool _loadingFromEnvironment;

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

    /// <summary>连续模式下视口在阅读轴上的长度（纵向看高，横向看宽）。</summary>
    private double ReadingViewportLength => _horizontal ? ViewportGrid.ActualWidth : ViewportGrid.ActualHeight;

    /// <summary>当前长带的最大阅读偏移（内容不够一屏时为 0）。</summary>
    private double MaximumOffset => _layout is null
        ? 0
        : PdfContinuousLayout.MaximumOffset(_layout, _builtMode, ViewportGrid.ActualWidth, ViewportGrid.ActualHeight);

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
        }

        vm.PdfPicker = PickPdf;
        vm.DocumentChanged -= OnDocumentChanged;
        vm.DocumentChanged += OnDocumentChanged;
        vm.ModeChanged -= OnModeChanged;
        vm.ModeChanged += OnModeChanged;

        UpdateModeChrome();
        UpdateEmptyStatus();
        MaybeShowStatusError();

        if (vm.HasDocument)
        {
            await BuildAsync(keepPage: true);
            return;
        }

        if (_demoStarted) return;
        _demoStarted = true;

        // 开发 / 截图：YLT_PDF_DEMO=1 → 生成一份演示 PDF 并打开（正常使用不会走到这里）
        _loadingFromDemo = true;
        try
        {
            await vm.TryStartDemoAsync();
        }
        finally
        {
            _loadingFromDemo = false;
        }

        if (vm.HasDocument) await BuildAsync(keepPage: false);
        else UpdateEmptyStatus();
        MaybeShowStatusError();

        // 开发 / 截图：YLT_PDF_OPEN=路径 直接打开指定 PDF（不弹对话框）
        if (!vm.HasDocument)
        {
            _loadingFromEnvironment = true;
            try
            {
                await vm.TryOpenFromEnvironmentAsync();
            }
            finally
            {
                _loadingFromEnvironment = false;
            }

            if (vm.HasDocument) await BuildAsync(keepPage: false);
            MaybeShowStatusError();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _unloaded = true;

        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow = null;
        }

        if (Vm is { } vm)
        {
            vm.DocumentChanged -= OnDocumentChanged;
            vm.ModeChanged -= OnModeChanged;
            vm.PdfPicker = null;
        }

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

        _errorToastTimer?.Stop();
        _errorToastTimer = null;

        ClearContinuousVisuals();
        ClearSpreadVisuals();
        _spreadActive = false;

        PageIndicatorPanel.Visibility = Visibility.Collapsed;
        HintPanel.Visibility = Visibility.Collapsed;
    }

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
        MaybeShowStatusError();

        var vm = Vm;
        if (vm?.HasDocument != true)
        {
            CancelInertia();
            ClearContinuousVisuals();
            ClearSpreadVisuals();
            _spreadActive = false;
            _builtSession = null;
            return;
        }

        // 演示 / 环境变量直接打开时由 OnLoaded 自己负责搭界面，避免「先建一次再被第二次覆盖」
        if (_loadingFromDemo || _loadingFromEnvironment) return;

        // 打开新 PDF 失败时 _session 还是原来那份：状态文案弹提示就行，别把阅读位置重置回第一页
        if (ReferenceEquals(vm.Session, _builtSession)) return;

        _ = BuildAsync(keepPage: false);
    }

    private void OnModeChanged(object? sender, EventArgs e)
    {
        UpdateModeChrome();
        UpdateHint();
        if (Vm?.HasDocument == true) _ = BuildAsync(keepPage: true);
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

            // 换模式时停在当前页；打开新 PDF 时吃一次「截图定位页」环境变量
            var targetPage = keepPage
                ? CurrentPageIndex()
                : vm.ConsumePendingPageIndex() ?? 0;

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
                await BuildContinuousAsync(targetPage);
            }

            _builtSession = session;
            UpdateModeChrome();
            UpdatePageIndicator();
            UpdateProgress();
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

    /// <summary>
    /// 当前阅读位置对应的页号（换阅读方式 / 窗口缩放时尽量停在这一页）。
    /// 用「阅读偏移最前面那一页」而不是视口中心页：横向连续滚动一屏能并排看到两三页，
    /// 用中心页会在窗口尺寸变化时把整条长带往后跳一页（第一页跑到左边看不见了）。
    /// </summary>
    private int CurrentPageIndex()
    {
        var vm = Vm;
        if (vm?.Session is not { } session || session.PageCount == 0) return 0;

        if (_spreadActive) return _spreadStart;
        if (_layout is null || _layout.IsEmpty) return 0;

        return PdfContinuousLayout.PageIndexAtOffset(_layout, _builtMode, _readingOffset);
    }

    // ---------------------------------------------------------------- 连续滚动：排版 / 渲染

    private async Task BuildContinuousAsync(int targetPage)
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

        _layout = PdfContinuousLayout.Compute(_builtMode, session.Pages, width, height);
        PageCanvas.Width = _layout.ContentWidth;
        PageCanvas.Height = _layout.ContentHeight;
        ContinuousScroll.UpdateLayout();

        SetReadingOffset(PdfContinuousLayout.OffsetOfPage(_layout, _builtMode, targetPage));

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
        if (_horizontal) ContinuousScroll.ScrollToHorizontalOffset(_readingOffset);
        else ContinuousScroll.ScrollToVerticalOffset(_readingOffset);

        UpdatePageIndicator();
        UpdateProgress();
        ScheduleVisibleRefresh();
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

        var (first, last) = PdfContinuousLayout.VisibleRange(_layout, _builtMode, _readingOffset, viewport);
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

    /// <summary>按当前 <see cref="_spreadStart"/> 重排双页画面（不带动画）。</summary>
    private async Task ShowSpreadAsync()
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

        var layout = PdfSpreadLayout.Compute(session.Pages, _spreadStart, width, height);
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

        if (SpreadCanvas is not null)
        {
            SpreadCanvas.Children.Clear();
            SpreadCanvas.RenderTransform = new TranslateTransform();
        }
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

            var layout = PdfSpreadLayout.Compute(session.Pages, target, width, height);
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
            foreach (var oldHost in oldHosts)
                animations.Add(PlaySlideAsync(oldHost.Container, 0, slide, 1, 0, duration));
            foreach (var newHost in newHosts)
                animations.Add(PlaySlideAsync(newHost.Container, -slide, 0, 0, 1, duration));
            await Task.WhenAll(animations);
            if (generation != _spreadGeneration) return;

            foreach (var oldHost in oldHosts) SpreadCanvas.Children.Remove(oldHost.Container);
            foreach (var newHost in newHosts)
            {
                newHost.Container.RenderTransform = new TranslateTransform();
                newHost.Container.Opacity = 1;
            }

            _spreadLeft = newLeft;
            _spreadRight = newRight;
            _spreadStart = target;
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

    /// <summary>双页跳到某一屏（Home / End 用）。</summary>
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

    // ---------------------------------------------------------------- 拖拽 + 惯性（只在连续滚动模式）

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Vm is not { } vm || !vm.HasDocument || vm.IsBusy) return;
        if (Mouse.Captured is not null && !ReferenceEquals(Mouse.Captured, this)) return;
        if (!IsFromViewport(e.OriginalSource as DependencyObject)) return;

        e.Handled = true;
        AbortInertia();

        if (_spreadActive)
        {
            _ = TurnSpreadAsync(forward: e.Delta < 0);
            return;
        }

        var step = vm.Cfg.WheelStepEffective;
        SetReadingOffset(_readingOffset + (e.Delta < 0 ? step : -step));
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
        if (_spreadActive) return;                  // 双页模式不拖画面

        e.Handled = true;
        BeginDrag(e);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var position = e.GetPosition(this);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < DragThresholdPixels) return;

        _dragMoved = true;
        e.Handled = true;

        // 内容跟着手走：纵向模式看上下、横向模式看左右
        var delta = _horizontal ? -dx : -dy;
        SetReadingOffset(_dragStartOffset + delta);

        // 惯性速度用「实际生效的偏移」采样：撞到边界就不动了，松手自然不会在边上滑
        _inertia.Note(NowMilliseconds(), _readingOffset, 0);
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_dragging) return;

        var moved = _dragMoved;
        var (velocity, _) = moved ? _inertia.ComputeVelocity() : (0.0, 0.0);
        EndDrag();

        if (moved)
        {
            e.Handled = true;
            StartInertia(velocity);
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
        _carriedVelocity = accumulate ? _inertiaVelocity : 0;
        CancelInertia();

        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(this);
        _dragStartOffset = _readingOffset;
        _inertia.Reset();
        _inertia.Note(NowMilliseconds(), _readingOffset, 0);

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

    private void StartInertia(double sampleVelocity)
    {
        if (Vm is not { } vm || !vm.Cfg.InertiaEnabled)
        {
            AbortInertia();
            return;
        }

        var (velocity, _) = PdfInertia.Combine(
            sampleVelocity, 0, _carriedVelocity, 0, accumulate: true, PdfInertia.DefaultMaxSpeed);
        _carriedVelocity = 0;

        if (!_inertia.IsFastEnough(velocity, 0)) return;

        _inertiaVelocity = velocity;

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
        if (_spreadActive || _layout is null)
        {
            CancelInertia();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _inertiaLastTimestamp) * 1000.0 / Stopwatch.Frequency;
        _inertiaLastTimestamp = now;
        if (elapsed <= 0 || elapsed > 100) elapsed = 16.0;   // 掉帧 / 休眠回来：别一下跳很远

        var (velocity, _) = _inertia.Decay(_inertiaVelocity, 0, elapsed);

        var before = _readingOffset;
        var target = before + velocity * elapsed;
        SetReadingOffset(target);
        var after = _readingOffset;

        // 撞到边界（请求了但实际没动）就把速度清掉，别在边上“嗡嗡”震
        if (Math.Abs(after - target) > 0.5) velocity = 0;
        _inertiaVelocity = velocity;

        if (!_inertia.IsFastEnough(velocity, 0)) CancelInertia();
    }

    /// <summary>停下惯性动画（保留「残余速度」，下一次拖拽还能叠加）。</summary>
    private void CancelInertia()
    {
        _inertiaTimer?.Stop();
        _inertiaVelocity = 0;
    }

    /// <summary>彻底停下惯性：动画 + 残余速度一起清掉（滚轮 / 换模式 / 切工具时用）。</summary>
    private void AbortInertia()
    {
        CancelInertia();
        _carriedVelocity = 0;
    }

    // ---------------------------------------------------------------- 视口尺寸变化

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

        if (_building)
        {
            _buildQueued = true;
            _buildQueuedKeepPage = true;
            return;
        }

        if (_spreadActive)
        {
            await ShowSpreadAsync();
            return;
        }

        if (_layout is null)
        {
            await BuildAsync(keepPage: true);
            return;
        }

        // 连续模式：窗口大小变了长带要重排；尽量停在同一页
        await BuildContinuousAsync(CurrentPageIndex());
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

        if (!vm.HasDocument || vm.IsBusy) return;

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
                SetReadingOffset(_readingOffset - smallStep);
                break;

            case Key.Down:
            case Key.Right:
                e.Handled = true;
                SetReadingOffset(_readingOffset + smallStep);
                break;

            case Key.PageUp:
                e.Handled = true;
                SetReadingOffset(_readingOffset - bigStep);
                break;

            case Key.PageDown:
            case Key.Space:
                e.Handled = true;
                SetReadingOffset(_readingOffset + bigStep);
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

    // ---------------------------------------------------------------- 界面 chrome（提示 / 页码 / 进度）

    private void UpdateModeChrome()
    {
        var hasDocument = Vm?.HasDocument == true;

        PageIndicatorPanel.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        HintPanel.Visibility = hasDocument ? Visibility.Visible : Visibility.Collapsed;
        InertiaCheckBox.IsEnabled = hasDocument && !_spreadActive;

        UpdateHint();
    }

    private void UpdateHint()
    {
        HintText.Text = Mode switch
        {
            PdfReadMode.Horizontal => "滚轮左右滚 · 左键按住拖动，松手惯性滑一段 · Home/End 到开头/结尾",
            PdfReadMode.Spread => "滚轮 / ← → / PageUp PageDown / 空格 翻页 · 屏幕两侧按钮也可以",
            _ => "滚轮上下滚 · 左键按住拖动，松手惯性滑一段 · Home/End 到开头/结尾",
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
            return;
        }

        if (_layout is null || _layout.IsEmpty)
        {
            PageIndicatorText.Text = $"共 {count} 页";
            return;
        }

        var page = PdfContinuousLayout.PageIndexAtOffset(_layout, _builtMode, _readingOffset);
        PageIndicatorText.Text = $"第 {page + 1} / {count} 页";
    }

    private void UpdateProgress()
    {
        if (ReadingProgressFill is null) return;

        var ratio = _spreadActive
            ? PdfSpreadLayout.ProgressRatio(_spreadStart, Vm?.PageCount ?? 0)
            : PdfContinuousLayout.RatioOf(_readingOffset, MaximumOffset);

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

    /// <summary>这一下点击是不是落在按钮上（翻页按钮的点击不能被拖拽吃掉）。</summary>
    private bool IsOnClickableControl(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, ViewportGrid))
        {
            if (source is ButtonBase) return true;
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
