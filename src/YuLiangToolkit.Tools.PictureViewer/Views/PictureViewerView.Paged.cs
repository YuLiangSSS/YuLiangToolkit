using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YuLiangToolkit.Tools.PictureViewer.Views;

/// <summary>
/// 「图片连续阅读器」的**翻页 / 单张视图**部分（partial）：双页翻页、单页浏览、翻页动画、
/// 缩放后平移、页码/按钮，以及「连续滚动 ↔ 翻页」两种模式的切换。
///
/// <para>为什么拆成 partial：连续滚动那套是「长带 + 两个滚动偏移 + 可见范围解码」，翻页这套是
/// 「一屏固定几页 + 页号 + 平移」，两者几乎不共用状态；放两个文件里比塞成一个巨型文件好读。
/// 纯逻辑（页号、双页配对、槽位排版、平移夹取）在 <see cref="PicturePaging"/> 里，自检直接断言。</para>
/// </summary>
public partial class PictureViewerView
{
    private const int PagedCacheLimit = 12;
    private const double PageTurnDurationMilliseconds = 190;

    private bool _pagedActive;
    private int _pageIndex;
    private bool _pagedAnimating;
    private int _pagedGeneration;
    private double _pagedPanX;
    private double _pagedPanY;
    private double _pagedContentWidth;
    private double _pagedContentHeight;
    private TranslateTransform? _pagedCanvasTransform;
    private readonly List<Image> _pagedLayerImages = new();
    private readonly Dictionary<string, BitmapImage> _pagedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pagedCacheOrder = new();

    /// <summary>当前是不是翻页 / 单张视图（工具条、滚轮、拖拽、键盘都按它分支）。</summary>
    private bool PagedActive => _pagedActive;

    /// <summary>翻页视图里内容能不能平移（放大到超过视口才行）。</summary>
    private bool PagedCanPan => _pagedContentWidth > PagedViewport.ActualWidth + 0.5
                                || _pagedContentHeight > PagedViewport.ActualHeight + 0.5;

    // ---------------------------------------------------------------- 模式切换

    /// <summary>
    /// 按当前「阅读方式」切换连续滚动 / 翻页视图。切换时尽量保持阅读位置：
    /// 连续 → 翻页：按阅读比例换算页号；翻页 → 连续：按页号换算阅读比例；
    /// 翻页 ↔ 翻页：保持页号。
    /// </summary>
    private async Task ApplyReadingModeAsync(bool keepProgress)
    {
        if (Vm is not { } vm) return;

        if (PictureScrollModes.IsPaged(vm.Cfg.ScrollModeEffective))
        {
            var wasContinuous = !_pagedActive;
            var readingRatio = wasContinuous ? GetReadingRatioForMode(_builtMode) : (double?)null;

            if (wasContinuous)
            {
                ExitAnchor();
                AbortInertia();
                _pageIndex = keepProgress && readingRatio is { } ratio && vm.Pages.Count > 0
                    ? (int)Math.Round(ratio * (vm.Pages.Count - 1))
                    : 0;
            }
            else if (!keepProgress)
            {
                _pageIndex = 0;
            }

            _pagedActive = true;
            _pagedGeneration++;
            _generation++;
            lock (_pendingLock) _pendingLoads.Clear();
            ClearSlots();

            ImageScrollViewer.Visibility = Visibility.Collapsed;
            ReadingProgressPanel.Visibility = Visibility.Visible;
            PagedHost.Visibility = Visibility.Visible;

            // 等一帧：刚 Visible 时 ActualWidth/Height 还是 0，排不了版
            await Dispatcher.Yield(DispatcherPriority.Loaded);
            await ShowPagedAsync(resetPan: true);
            ApplyDebugHooks();
            return;
        }

        // 切回连续滚动：把当前页号映射成阅读比例
        var wasPaged = _pagedActive;
        var targetRatio = wasPaged && vm.Pages.Count > 1
            ? (double)_pageIndex / (vm.Pages.Count - 1)
            : 0.0;

        _pagedActive = false;
        PagedHost.Visibility = Visibility.Collapsed;
        ExitPagedVisuals();
        ImageScrollViewer.Visibility = Visibility.Visible;
        ReadingProgressPanel.Visibility = Visibility.Visible;

        await BuildStripAsync(keepProgress && !wasPaged, forcedRatio: wasPaged ? targetRatio : null);
    }

    private void ExitPagedMode()
    {
        _pagedActive = false;
        _pagedAnimating = false;
        _pagedGeneration++;
        ExitPagedVisuals();
        ClearPagedCache();
    }

    // ---------------------------------------------------------------- 排版 / 加载

    private async Task ShowPagedAsync(bool resetPan)
    {
        CancelPagedAnimation();
        ClearPagedLayerImages();
        await LayoutPagedAsync(resetPan);
    }

    private async Task LayoutPagedAsync(bool resetPan = false)
    {
        if (!_pagedActive) return;

        var generation = ++_pagedGeneration;
        ClearPagedLayerImages();

        if (Vm is not { } vm || vm.Pages.Count == 0)
        {
            UpdatePagedChrome();
            return;
        }

        var viewportWidth = PagedViewport.ActualWidth;
        var viewportHeight = PagedViewport.ActualHeight;
        if (viewportWidth <= 1 || viewportHeight <= 1) return;

        _pageIndex = PicturePaging.NormalizePageIndex(_pageIndex, vm.Pages.Count);
        var layout = PicturePaging.ComputeLayout(
            Mode, viewportWidth, viewportHeight, vm.Cfg.ZoomScale, vm.Pages.Count, _pageIndex);

        _pagedContentWidth = layout.ContentWidth;
        _pagedContentHeight = layout.ContentHeight;

        if (resetPan)
        {
            _pagedPanX = 0;
            _pagedPanY = 0;
        }

        PagedCanvas.Width = _pagedContentWidth;
        PagedCanvas.Height = _pagedContentHeight;
        _pagedCanvasTransform ??= new TranslateTransform();
        _pagedCanvasTransform.X = 0;
        _pagedCanvasTransform.Y = 0;
        PagedCanvas.RenderTransform = _pagedCanvasTransform;

        foreach (var slot in layout.Slots)
        {
            if (slot.PageIndex is not { } pageIndex) continue;
            var image = await CreatePagedImageAsync(vm.Pages[pageIndex], slot, generation);
            if (image is null || generation != _pagedGeneration) return;
            _pagedLayerImages.Add(image);
            PagedCanvas.Children.Add(image);
        }

        ApplyPagedPan();
        UpdatePagedChrome();
    }

    private async Task<Image?> CreatePagedImageAsync(PicturePage page, PicturePagedSlot slot, int generation)
    {
        if (slot.Width <= 0 || slot.Height <= 0) return null;

        var aspect = page.AspectRatio > 0 ? page.AspectRatio : 1.0;
        var fitWidth = slot.Width;
        var fitHeight = fitWidth / aspect;
        if (fitHeight > slot.Height)
        {
            fitHeight = slot.Height;
            fitWidth = fitHeight * aspect;
        }
        if (fitWidth <= 0 || fitHeight <= 0) return null;

        var dpi = DpiScale;
        var decodeWidth = (int)Math.Ceiling(fitWidth * dpi);
        if (page.PixelWidth > 0) decodeWidth = Math.Min(decodeWidth, page.PixelWidth);

        var bitmap = await LoadPagedBitmapAsync(page, decodeWidth, generation);
        if (bitmap is null || generation != _pagedGeneration) return null;

        var image = new Image
        {
            Source = bitmap,
            Width = fitWidth,
            Height = fitHeight,
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

        // 双页时两页要**在中间接缝处对齐**（左页靠右、右页靠左），不然中间会留一条空白；
        // 单页模式居中。
        double left;
        if (PictureScrollModes.IsSpread(Mode))
            left = slot.X <= 0.5 ? slot.X + slot.Width - fitWidth : slot.X;
        else
            left = slot.X + (slot.Width - fitWidth) / 2;

        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, slot.Y + (slot.Height - fitHeight) / 2);
        return image;
    }

    private async Task<BitmapImage?> LoadPagedBitmapAsync(PicturePage page, int decodeWidth, int generation)
    {
        decodeWidth = Math.Max(1, decodeWidth);
        var key = $"{page.FilePath}|{decodeWidth}";

        if (_pagedCache.TryGetValue(key, out var cached))
        {
            TouchPagedCache(key);
            return cached;
        }

        try
        {
            var bitmap = await Task.Run(() =>
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(page.FilePath);
                image.DecodePixelWidth = decodeWidth;
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                return image;
            });

            if (generation != _pagedGeneration) return null;

            _pagedCache[key] = bitmap;
            _pagedCacheOrder.Add(key);
            TrimPagedCache();
            return bitmap;
        }
        catch
        {
            return null;   // 坏图 / 文件被删：先空着，翻走再回来会重试
        }
    }

    private void TouchPagedCache(string key)
    {
        var index = _pagedCacheOrder.IndexOf(key);
        if (index < 0) return;
        _pagedCacheOrder.RemoveAt(index);
        _pagedCacheOrder.Add(key);
    }

    private void TrimPagedCache()
    {
        while (_pagedCacheOrder.Count > PagedCacheLimit)
        {
            var oldest = _pagedCacheOrder[0];
            _pagedCacheOrder.RemoveAt(0);
            _pagedCache.Remove(oldest);
        }
    }

    private void ClearPagedCache()
    {
        _pagedCache.Clear();
        _pagedCacheOrder.Clear();
    }

    private void ClearPagedLayerImages()
    {
        foreach (var image in _pagedLayerImages)
        {
            image.Source = null;
        }
        _pagedLayerImages.Clear();
        PagedCanvas.Children.Clear();
    }

    private void ExitPagedVisuals()
    {
        CancelPagedAnimation();
        ClearPagedLayerImages();
        _pagedContentWidth = 0;
        _pagedContentHeight = 0;
        _pagedPanX = 0;
        _pagedPanY = 0;
    }

    // ---------------------------------------------------------------- 平移（缩放后拖）

    internal (double X, double Y) PagedPan => (_pagedPanX, _pagedPanY);

    internal void PanPaged(double x, double y)
    {
        _pagedPanX = x;
        _pagedPanY = y;
        ApplyPagedPan();
    }

    private void ApplyPagedPan()
    {
        if (!_pagedActive) return;

        var (x, y) = PicturePaging.ClampPan(
            _pagedPanX, _pagedPanY,
            _pagedContentWidth, _pagedContentHeight,
            PagedViewport.ActualWidth, PagedViewport.ActualHeight);
        _pagedPanX = x;
        _pagedPanY = y;

        if (_pagedCanvasTransform is null)
        {
            _pagedCanvasTransform = new TranslateTransform();
            PagedCanvas.RenderTransform = _pagedCanvasTransform;
        }
        _pagedCanvasTransform.X = -x;
        _pagedCanvasTransform.Y = -y;
    }

    private void ResetPagedPan()
    {
        _pagedPanX = 0;
        _pagedPanY = 0;
        if (_pagedCanvasTransform is not null)
        {
            _pagedCanvasTransform.X = 0;
            _pagedCanvasTransform.Y = 0;
        }
    }

    // ---------------------------------------------------------------- 翻页

    private async Task TurnPageAsync(bool forward)
    {
        if (!_pagedActive || _pagedAnimating || Vm is not { } vm || vm.Pages.Count == 0) return;

        var count = vm.Pages.Count;
        var current = PicturePaging.NormalizePageIndex(_pageIndex, count);
        if (forward ? !PicturePaging.CanGoNext(current, count) : !PicturePaging.CanGoPrevious(current, count)) return;

        var target = PicturePaging.StepPage(current, count, forward);
        var canAnimate = vm.Cfg.PageTurnAnimation
                         && count > 1
                         && Math.Abs(vm.Cfg.ZoomScale - 1.0) < 0.001
                         && Math.Abs(_pagedPanX) < 0.5
                         && Math.Abs(_pagedPanY) < 0.5
                         && PagedViewport.ActualWidth > 8
                         && PagedViewport.ActualHeight > 8;

        if (!canAnimate)
        {
            _pageIndex = target;
            ResetPagedPan();
            await LayoutPagedAsync(resetPan: true);
            return;
        }

        _pagedAnimating = true;
        var generation = ++_pagedGeneration;
        var completed = false;

        try
        {
            var viewportWidth = PagedViewport.ActualWidth;
            var viewportHeight = PagedViewport.ActualHeight;
            var spread = PictureScrollModes.IsSpread(Mode);
            var reverse = PictureScrollModes.IsReverse(Mode);
            var slotWidth = spread ? viewportWidth / 2 : viewportWidth;

            // 前进方向决定整页往哪边滑：左→右前进往左滑；右→左（日式）前进往右滑；后退反过来
            var negative = forward != reverse;
            var shift = negative ? -slotWidth : slotWidth;

            var layoutOld = PicturePaging.ComputeLayout(Mode, viewportWidth, viewportHeight, 1.0, count, current);
            var layoutNew = PicturePaging.ComputeLayout(Mode, viewportWidth, viewportHeight, 1.0, count, target);
            var oldPages = layoutOld.Slots
                .Where(s => s.PageIndex is not null)
                .Select(s => s.PageIndex!.Value)
                .ToHashSet();
            var incomingSlot = layoutNew.Slots
                .FirstOrDefault(s => s.PageIndex is { } p && !oldPages.Contains(p));

            ClearPagedLayerImages();
            PagedCanvas.Width = viewportWidth;
            PagedCanvas.Height = viewportHeight;
            _pagedCanvasTransform ??= new TranslateTransform();
            _pagedCanvasTransform.X = 0;
            _pagedCanvasTransform.Y = 0;
            PagedCanvas.RenderTransform = _pagedCanvasTransform;

            var animations = new List<(TranslateTransform Transform, double From, double To)>();

            foreach (var slot in layoutOld.Slots)
            {
                if (slot.PageIndex is not { } pageIndex) continue;
                var image = await CreatePagedImageAsync(vm.Pages[pageIndex], slot, generation);
                if (image is null || generation != _pagedGeneration) return;

                var transform = new TranslateTransform();
                image.RenderTransform = transform;
                PagedCanvas.Children.Add(image);
                animations.Add((transform, 0, shift));
            }

            if (incomingSlot?.PageIndex is { } incomingPage && incomingSlot is not null)
            {
                var image = await CreatePagedImageAsync(vm.Pages[incomingPage], incomingSlot, generation);
                if (image is null || generation != _pagedGeneration) return;

                var transform = new TranslateTransform { X = -shift };
                image.RenderTransform = transform;
                PagedCanvas.Children.Add(image);
                animations.Add((transform, -shift, 0));
            }

            var duration = TimeSpan.FromMilliseconds(PageTurnDurationMilliseconds);
            foreach (var (transform, from, to) in animations)
            {
                var animation = new DoubleAnimation(from, to, duration)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                transform.BeginAnimation(TranslateTransform.XProperty, animation);
            }

            await Task.Delay((int)duration.TotalMilliseconds);
            if (generation != _pagedGeneration) return;

            _pageIndex = target;
            ResetPagedPan();
            completed = true;
        }
        finally
        {
            _pagedAnimating = false;
        }

        if (completed && generation == _pagedGeneration) await LayoutPagedAsync(resetPan: true);
    }

    private async Task GoToPageAsync(int pageIndex)
    {
        if (!_pagedActive || Vm is not { } vm) return;
        _pageIndex = PicturePaging.NormalizePageIndex(pageIndex, vm.Pages.Count);
        ResetPagedPan();
        await LayoutPagedAsync(resetPan: true);
    }

    private void CancelPagedAnimation()
    {
        _pagedAnimating = false;
        _pagedGeneration++;
    }

    private void UpdatePagedChrome()
    {
        if (Vm is not { } vm) return;

        var count = vm.Pages.Count;
        var (left, right) = PicturePaging.VisiblePages(Mode, _pageIndex, count);
        if (count == 0)
        {
            PageIndicatorText.Text = "";
        }
        else if (PictureScrollModes.IsSpread(Mode) && left is { } leftPage && right is { } rightPage)
        {
            // 日式双页是「右页在前、左页在后」，页码按阅读顺序从小到大显示更好认
            var low = Math.Min(leftPage, rightPage) + 1;
            var high = Math.Max(leftPage, rightPage) + 1;
            PageIndicatorText.Text = $"第 {low}–{high} 张 / 共 {count} 张";
        }
        else
        {
            var current = (left ?? right ?? 0) + 1;
            PageIndicatorText.Text = $"第 {current} 张 / 共 {count} 张";
        }

        var (leftIsPrevious, rightIsPrevious) = PicturePaging.ButtonDirections(Mode);
        var canPrevious = PicturePaging.CanGoPrevious(_pageIndex, count);
        var canNext = PicturePaging.CanGoNext(_pageIndex, count);

        PrevPageButton.IsEnabled = leftIsPrevious ? canPrevious : canNext;
        NextPageButton.IsEnabled = rightIsPrevious ? canPrevious : canNext;
        PrevPageButton.ToolTip = leftIsPrevious ? "上一张" : "下一张";
        NextPageButton.ToolTip = rightIsPrevious ? "上一张" : "下一张";
    }

    private void UpdatePagedProgress()
    {
        var count = Vm?.Pages.Count ?? 0;
        var ratio = count > 1
            ? (double)PicturePaging.NormalizePageIndex(_pageIndex, count) / (count - 1)
            : 0;

        ReadingProgressFill.HorizontalAlignment = PictureScrollModes.IsReverse(Mode)
            ? HorizontalAlignment.Right
            : HorizontalAlignment.Left;
        ReadingProgressFill.Width = ratio * Math.Max(0, ViewportGrid.ActualWidth);
    }

    // ---------------------------------------------------------------- 翻页动画 / 缩放后的重新排版

    private async Task RelayoutPagedKeepCenterAsync()
    {
        if (!_pagedActive) return;

        var viewportWidth = PagedViewport.ActualWidth;
        var viewportHeight = PagedViewport.ActualHeight;
        var centerX = _pagedContentWidth > 0 ? (_pagedPanX + viewportWidth / 2) / _pagedContentWidth : 0.5;
        var centerY = _pagedContentHeight > 0 ? (_pagedPanY + viewportHeight / 2) / _pagedContentHeight : 0.5;

        await LayoutPagedAsync();
        if (!_pagedActive) return;

        _pagedPanX = centerX * _pagedContentWidth - viewportWidth / 2;
        _pagedPanY = centerY * _pagedContentHeight - viewportHeight / 2;
        ApplyPagedPan();
    }

    // ---------------------------------------------------------------- 按钮 / 键盘 / 调试钩子

    private void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        // 左边那个按钮：非日式模式 = 上一张；日式（反向）模式 = 下一张
        _ = TurnPageAsync(forward: PictureScrollModes.IsReverse(Mode));
    }

    private void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        _ = TurnPageAsync(forward: !PictureScrollModes.IsReverse(Mode));
    }

    /// <summary>翻页模式下的方向键 / Home / End / PageUp / PageDown。</summary>
    private bool HandlePagedKey(Key key)
    {
        var reverse = PictureScrollModes.IsReverse(Mode);
        var count = Vm?.Pages.Count ?? 0;

        switch (key)
        {
            case Key.Left:
                _ = TurnPageAsync(forward: reverse);
                return true;
            case Key.Right:
                _ = TurnPageAsync(forward: !reverse);
                return true;
            case Key.PageUp:
                _ = TurnPageAsync(forward: false);
                return true;
            case Key.PageDown:
            case Key.Space:
                _ = TurnPageAsync(forward: true);
                return true;
            case Key.Home:
                _ = GoToPageAsync(0);
                return true;
            case Key.End:
                _ = GoToPageAsync(Math.Max(0, count - 1));
                return true;
            default:
                return false;
        }
    }

    /// <summary>截图 / 排查钩子：<c>YLT_PICTURE_PAGE=3</c> 直接跳到第 3 张（1 基）。</summary>
    private void ApplyPagedDebugHooks()
    {
        if (Vm is not { } vm || !_pagedActive) return;

        var text = Environment.GetEnvironmentVariable("YLT_PICTURE_PAGE");
        if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text, out var page)) return;

        _pageIndex = PicturePaging.NormalizePageIndex(page - 1, vm.Pages.Count);
        _ = ShowPagedAsync(resetPan: true);
    }
}
