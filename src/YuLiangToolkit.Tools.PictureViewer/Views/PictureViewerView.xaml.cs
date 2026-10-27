using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
/// 所以滚轮 / 拖拽 / 中键三种方式在三种模式里只有一套逻辑（见 AGENTS.md 2.19）。</para>
/// </summary>
public partial class PictureViewerView : UserControl
{
    // ---------------------------------------------------------------- 一页的界面对象

    private sealed class PageSlot
    {
        public PicturePage Page = null!;
        public Border Container = null!;
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
    private double _maxPixelWidth;
    private double _maxPixelHeight;
    private double _lastCross;
    private int _decodeFailureCount;

    // 「阅读偏移」的唯一真值。⚠ 不要每次从 ScrollViewer 的物理偏移反读：
    // WPF 的 ScrollToXxx 是**排队到布局阶段**才真正生效的，连着滚轮 / 锚点每帧滚时反读会拿到旧值、
    // 把每一下的位移都丢掉（踩过）。这里自己记，只在我们 Set 时改；ScrollViewer 只负责照着显示。
    private double _readingOffset;

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
    private bool _releasingMouse;

    private Window? _hostWindow;

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

        if (vm.HasPages) await BuildStripAsync(keepProgress: true);

        if (!_demoStarted)
        {
            _demoStarted = true;
            await vm.TryStartDemoAsync();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewMouseDown -= OnHostPreviewMouseDownTrace;
        }
        _hostWindow = null;

        ExitAnchor();
        if (_dragging) EndDrag();

        _scrollSettleTimer?.Stop();
        _anchorTimer?.Stop();

        _generation++;
        lock (_pendingLock) _pendingLoads.Clear();

        if (Vm is { } vm)
        {
            vm.PagesChanged -= OnPagesChanged;
            vm.ModeChanged -= OnModeChanged;
            vm.FolderPicker = null;
        }

        ClearSlots();
    }

    private void OnPagesChanged(object? sender, EventArgs e) => _ = BuildStripAsync(keepProgress: false);

    private void OnModeChanged(object? sender, EventArgs e) => _ = BuildStripAsync(keepProgress: true);

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
    private async Task BuildStripAsync(bool keepProgress)
    {
        if (Vm is not { } vm) return;

        // 重建途中又来了请求（连着换方向 / 换文件夹）：记下来，等这次建完补跑一次。
        // 静默丢掉会让「长带停在旧方向、配置已经是新方向」——之后所有滚动都按新轴算旧布局（见 AGENTS 2.19）。
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
            var ratio = keepProgress ? GetReadingRatioForMode(_builtMode) : 0;

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

                var border = new Border
                {
                    Child = image,
                    Margin = new Thickness(0),
                    Background = Brushes.Transparent,
                };

                _slots.Add(new PageSlot { Page = page, Container = border, ImageControl = image });
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

            LayoutSlots(ratio);
            _builtMode = vm.Cfg.ScrollModeEffective;
            UpdateReadingProgress();
            ApplyDebugHooks();
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

    /// <summary>图片统一缩放后的跨轴尺寸（纵向 = 统一宽度，横向 = 统一高度），单位是 DIP。</summary>
    private double ComputeCrossSize(double viewportCross)
    {
        var maxPixelCross = IsHorizontalMode ? _maxPixelHeight : _maxPixelWidth;
        return PictureScrollMath.UniformCrossSize(maxPixelCross / DpiScale, viewportCross);
    }

    /// <summary>
    /// 按视口重新算每张图的尺寸（统一宽度 / 统一高度），再定位。
    /// <paramref name="targetRatio"/> 是按「阅读比例」定位；<paramref name="targetReadingOffset"/> 非空时按绝对阅读偏移
    /// （窗口缩放走这条：内容长度和视口同比例变，按偏移等比缩放比按比例更贴手）。
    /// </summary>
    private void LayoutSlots(double targetRatio, double? targetReadingOffset = null)
    {
        if (_slots.Count == 0) return;

        var horizontal = IsHorizontalMode;
        var viewportCross = horizontal ? ViewportGrid.ActualHeight : ViewportGrid.ActualWidth;
        if (viewportCross <= 1) return;

        var cross = ComputeCrossSize(viewportCross);
        if (cross <= 0) return;

        var start = 0.0;
        foreach (var slot in _slots)
        {
            var main = PictureScrollMath.MainLengthForAspect(
                horizontal, slot.Page.PixelWidth, slot.Page.PixelHeight, cross);
            if (main <= 0) main = cross;

            slot.MainLength = main;
            slot.Start = start;
            start += main;

            if (horizontal)
            {
                slot.Container.Width = main;
                slot.Container.Height = double.NaN;   // 高度交给 StackPanel 拉满（统一高度）
            }
            else
            {
                slot.Container.Height = main;
                slot.Container.Width = double.NaN;    // 宽度交给 StackPanel 拉满（统一宽度）
            }
        }

        _lastCross = cross;
        ImageScrollViewer.UpdateLayout();
        SetReadingOffset(targetReadingOffset ?? PictureScrollMath.OffsetForRatio(targetRatio, GetReadingMaximum()));
        ProcessVisibleRange();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_slots.Count == 0 || _layoutQueued) return;

        // 图片按视口跨轴缩放，所以内容长度也跟着按比例变：按「绝对阅读偏移 × 新老缩放比」定位最跟手；
        // 只用可滚范围的比例会在窗口一拉高时跳半页（复核报告里的 P2）。
        var reading = GetReadingOffset();
        var viewportCross = IsHorizontalMode ? e.NewSize.Height : e.NewSize.Width;
        var newCross = ComputeCrossSize(viewportCross);
        var scale = _lastCross > 0 && newCross > 0 ? newCross / _lastCross : 0;
        double? targetReading = scale > 0 ? reading * scale : null;

        // 窗口拖拽时会连续触发：排到 Background 再量一次，避免每一帧都重排
        _layoutQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _layoutQueued = false;
            if (_slots.Count == 0) return;
            LayoutSlots(GetReadingRatio(), targetReading);
            UpdateReadingProgress();
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

    private void UpdateReadingProgress()
    {
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

        // 纵向模式按统一宽度解码；横向模式按这一张的显示宽度解码（都是 DIP，乘 DPI 才是设备像素）
        var uniformDips = PictureScrollMath.UniformCrossSize(_maxPixelWidth / dpi, ViewportGrid.ActualWidth);
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

        if (_anchor.IsActive) ExitAnchor();

        e.Handled = true;
        var max = GetReadingMaximum();
        SetReadingOffset(PictureScrollMath.WheelOffset(GetReadingOffset(), e.Delta, vm.Cfg.WheelStepEffective, max));
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

        if (e.ChangedButton == MouseButton.Middle)
        {
            if (!vm.HasPages || IsInsideSettings(source) || !IsFromViewport(source)) return;
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
            if (!vm.HasPages || IsInsideSettings(source) || !IsFromViewport(source)) return;
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
            EndDrag();
            if (moved) e.Handled = true;
        }
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var position = e.GetPosition(this);
        var dx = position.X - _dragStart.X;
        var dy = position.Y - _dragStart.Y;
        if (!_dragMoved && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // 手抖 / 单击，不算拖拽

        _dragMoved = true;
        e.Handled = true;
        SetReadingOffset(_dragStartReading + PictureScrollMath.DragDelta(Mode, dx, dy));
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
        _dragging = true;
        _dragMoved = false;
        _dragStart = e.GetPosition(this);
        _dragStartReading = GetReadingOffset();
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

    // ---------------------------------------------------------------- 中键锚点

    private void BeginAnchor(MouseButtonEventArgs e)
    {
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

        // Esc 优先：设置面板里的输入框正抓着焦点时也要能关闭（文档承诺了）
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.Escape)
        {
            if (_anchor.IsActive)
            {
                ExitAnchor();
                e.Handled = true;
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

        var scrollRatio = Environment.GetEnvironmentVariable("YLT_PICTURE_SCROLL");
        if (!string.IsNullOrWhiteSpace(scrollRatio)
            && double.TryParse(scrollRatio, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio))
        {
            SetReadingOffset(PictureScrollMath.OffsetForRatio(ratio, GetReadingMaximum()));
        }

        if (PictureViewerViewModel.IsEnvironmentOn("YLT_PICTURE_ANCHOR"))
        {
            UpdateAnchorArrowDirection();
            Canvas.SetLeft(AnchorIndicator, Math.Max(0, ViewportGrid.ActualWidth / 2 - AnchorIndicator.Width / 2));
            Canvas.SetTop(AnchorIndicator, Math.Max(0, ViewportGrid.ActualHeight / 2 - AnchorIndicator.Height / 2));
            AnchorIndicator.Visibility = Visibility.Visible;
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
        var first = _slots.Count > 0 ? _slots[0] : null;
        var last = _slots.Count > 0 ? _slots[^1] : null;
        var firstChildIsPage1 = ImageStrip.Children.Count > 0 && first is not null
                                && ReferenceEquals(ImageStrip.Children[0], first.Container);

        var lines = new[]
        {
            $"at={DateTime.Now:HH:mm:ss.fff}",
            $"mode={mode} builtMode={_builtMode} horizontal={PictureScrollModes.IsHorizontal(mode)} reverse={PictureScrollModes.IsReverse(mode)}",
            $"view={ActualWidth:0.#}x{ActualHeight:0.#} viewport={ViewportGrid.ActualWidth:0.#}x{ViewportGrid.ActualHeight:0.#} cross={_lastCross:0.#}",
            $"scrollViewer={ImageScrollViewer.ActualWidth:0.#}x{ImageScrollViewer.ActualHeight:0.#}",
            $"viewportSize={ImageScrollViewer.ViewportWidth:0.#}x{ImageScrollViewer.ViewportHeight:0.#} extent={ImageScrollViewer.ExtentWidth:0.#}x{ImageScrollViewer.ExtentHeight:0.#}",
            $"physicalOffset=({ImageScrollViewer.HorizontalOffset:0.#},{ImageScrollViewer.VerticalOffset:0.#}) scrollable=({ImageScrollViewer.ScrollableWidth:0.#},{ImageScrollViewer.ScrollableHeight:0.#})",
            $"readingOffset={GetReadingOffset():0.#}/{GetReadingMaximum():0.#} ratio={GetReadingRatio():0.###} fromPhysical={PictureScrollMath.ReadingOffset(mode, ImageScrollViewer.HorizontalOffset, ImageScrollViewer.VerticalOffset, ImageScrollViewer.ScrollableWidth, ImageScrollViewer.ScrollableHeight):0.#}",
            $"slots={_slots.Count} firstStart={first?.Start:0.#} firstLen={first?.MainLength:0.#} lastEnd={(last is null ? 0 : last.Start + last.MainLength):0.#}",
            $"stripChildren={ImageStrip.Children.Count} orientation={ImageStrip.Orientation} firstChildIsPage1={firstChildIsPage1}",
            $"anchorVisible={AnchorIndicator.Visibility} arrowHorizontal={ArrowLeft.Visibility} arrowVertical={ArrowUp.Visibility}",
            $"busy={vm.IsBusy} pages={vm.Pages.Count} status={vm.StatusText}",
        };

        try { File.WriteAllLines(path, lines); }
        catch { /* 排查文件写不进去就算了 */ }
    }
}
