using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Tools.WordViewer.Parsing;
using YuLiangToolkit.Tools.WordViewer.ViewModels;

namespace YuLiangToolkit.Tools.WordViewer.Views;

/// <summary>
/// 「Word 阅读器」页面的代码后置：只做**必须碰输入 / 焦点 / 拖放 / 滚动位置**的事情 ——
/// 拖入打开、Ctrl+滚轮缩放、Ctrl+F 聚焦查找框、回车 / Shift+回车 / Esc 查找导航、点大纲跳转、
/// 记住 / 恢复阅读位置。业务逻辑（打开 / 解析 / 排版 / 查找命中）都在 <see cref="WordViewerViewModel"/> 里。
/// </summary>
public partial class WordViewerView : UserControl
{
    private WordViewerViewModel? _viewModel;
    private bool _autoOpenRequested;

    /// <summary>恢复阅读位置时「滚动范围还没量出来」的重试次数（FlowDocument 刚换过时要等布局）。</summary>
    private int _pendingScrollApplyAttempts;

    /// <summary>
    /// <c>FlowDocumentScrollViewer</c> 自己不暴露滚动偏移 / 滚动范围，要拿模板里的 <see cref="ScrollViewer"/>。
    /// 找到后缓存（视图被缓存再挂回来时还能用）。
    /// </summary>
    private ScrollViewer? _documentScrollViewer;

    public WordViewerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
    }

    private WordViewerViewModel? ViewModel
    {
        get
        {
            _viewModel ??= DataContext as WordViewerViewModel;
            return _viewModel;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = DataContext as WordViewerViewModel;
        if (_viewModel is null) return;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ScrollRatioProvider = CaptureScrollRatio;
        _viewModel.CenterBlockProvider = CenterBlockInViewer;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel ??= DataContext as WordViewerViewModel;
        if (_viewModel is null) return;

        // 视图被外壳缓存后重新挂回来时，仍要把「读滚动比例 / 居中跳转」的能力接上
        _viewModel.ScrollRatioProvider = CaptureScrollRatio;
        _viewModel.CenterBlockProvider = CenterBlockInViewer;
        _documentScrollViewer = null;   // 模板可能被重建，下次用到时重新找

        // 开发 / 截图用：YLT_WORD_OPEN 指定的文档只自动打开一次
        if (_autoOpenRequested) return;
        _autoOpenRequested = true;
        _viewModel.TryOpenPendingAutoOpen();
    }

    /// <summary>
    /// 切走工具页（视图被外壳回收 / 缓存）时保存阅读位置：把当前滚动比例交给 ViewModel 并立刻落盘。
    /// 切回来时视图没有重新排版，位置保持原样。
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        var scrollViewer = GetDocumentScrollViewer();
        viewModel.NoteReadingPosition(
            scrollViewer?.VerticalOffset ?? 0,
            scrollViewer?.ScrollableHeight ?? 0);
        viewModel.FlushReadingPosition(saveNow: true);
    }

    // ---------------------------------------------------------------- 阅读位置

    /// <summary>
    /// 取 <c>FlowDocumentScrollViewer</c> 模板里的滚动容器：它自己的公开 API 没有 VerticalOffset /
    /// ScrollableHeight / ScrollToVerticalOffset，只能沿可视树往下找第一个 <see cref="ScrollViewer"/>。
    /// </summary>
    private ScrollViewer? GetDocumentScrollViewer()
    {
        if (_documentScrollViewer is not null) return _documentScrollViewer;
        _documentScrollViewer = FindChild<ScrollViewer>(DocumentViewer);
        return _documentScrollViewer;
    }

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;

        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } found) return found;
        }

        return null;
    }

    /// <summary>读当前竖向滚动比例（0 ~ 1）；没文档 / 内容撑不满一屏时返回 null。</summary>
    private double? CaptureScrollRatio()
    {
        var scrollViewer = GetDocumentScrollViewer();
        if (scrollViewer is null || DocumentViewer.Document is null) return null;

        var scrollable = scrollViewer.ScrollableHeight;
        if (scrollable <= 0.5) return null;
        return Math.Clamp(scrollViewer.VerticalOffset / scrollable, 0, 1);
    }

    /// <summary>
    /// 把某个块尽量滚到**窗口中间**（批注面板点击跳转用）：
    /// 先 <c>BringIntoView</c> 保证可见，再按该段起始字符的文档坐标把视口中心对过去。
    /// 表格 / 图片块也支持（<see cref="TextElement.ContentStart"/> 是 <see cref="Block"/> 的公共成员）；
    /// 取不到坐标时退回「只滚到可见」，返回 <c>false</c> 让 ViewModel 的状态栏措辞更准确。
    /// </summary>
    private bool CenterBlockInViewer(int blockIndex)
    {
        var block = ViewModel?.GetRenderedBlock(blockIndex);
        var scrollViewer = GetDocumentScrollViewer();
        if (block is null || scrollViewer is null) return false;

        try
        {
            block.BringIntoView();
            if (block is not TextElement element) return false;

            var rect = element.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty || scrollViewer.ViewportHeight <= 0) return false;

            // GetCharacterRect 给的是文档内容坐标；把它和当前偏移相加，让这段文字落在视口中间
            var target = scrollViewer.VerticalOffset + rect.Top - scrollViewer.ViewportHeight / 2;
            if (double.IsNaN(target) || double.IsInfinity(target)) return false;

            scrollViewer.ScrollToVerticalOffset(Math.Max(0, target));
            return true;
        }
        catch
        {
            // 布局还没完成 / 元素已被换掉：居中失败不影响跳转本身
            return false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 打开文档 / 切换页眉页脚后 FlowDocument 会被换掉，等布局完成再把滚动位置应用上去
        if (e.PropertyName == nameof(WordViewerViewModel.FlowDocument)) ApplyPendingScrollRatio();
    }

    private void ApplyPendingScrollRatio()
    {
        if (ViewModel?.ConsumePendingScrollRatio() is not { } ratio) return;

        _pendingScrollApplyAttempts = 0;
        TryApply(ratio);

        // 局部函数递归：滚动范围=0 说明还没布局完，空闲优先级下重试几次（最多 3 次）
        void TryApply(double targetRatio)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (DocumentViewer.Document is null) return;

                var scrollViewer = GetDocumentScrollViewer();
                if (scrollViewer is null) return;

                var scrollable = scrollViewer.ScrollableHeight;
                if (scrollable <= 0.5 && _pendingScrollApplyAttempts < 3)
                {
                    _pendingScrollApplyAttempts++;
                    TryApply(targetRatio);
                    return;
                }

                _pendingScrollApplyAttempts = 0;
                var safeRatio = double.IsNaN(targetRatio) || double.IsInfinity(targetRatio)
                    ? 0
                    : Math.Clamp(targetRatio, 0, 1);
                scrollViewer.ScrollToVerticalOffset(safeRatio * Math.Max(0, scrollable));
            }));
        }
    }

    // ---------------------------------------------------------------- 拖入打开

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = TryGetDroppedDocument(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var path = TryGetDroppedDocument(e);
        if (path is null || ViewModel is null) return;
        _ = ViewModel.OpenDroppedFileAsync(path);
    }

    /// <summary>拖放数据里第一个受支持的 .docx / .docm（拖一堆文件时只打开第一个能认的）。</summary>
    private static string? TryGetDroppedDocument(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return null;
        return paths.FirstOrDefault(WordDocumentParser.IsSupportedExtension);
    }

    // ---------------------------------------------------------------- 缩放

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 只有按着 Ctrl 才是缩放，普通滚轮留给正文滚动
        if (Keyboard.Modifiers != ModifierKeys.Control || ViewModel is null) return;
        e.Handled = true;
        ViewModel.ZoomBySteps(e.Delta > 0 ? 1 : -1);
    }

    private void OnZoomBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        switch (e.Key)
        {
            // TextBox.Text 默认 LostFocus 才写回：回车手动提交，Esc 还原成当前生效值
            case Key.Enter:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
                break;
            case Key.Escape:
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- 键盘

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null) return;

        switch (e.Key)
        {
            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                FocusFindBox();
                e.Handled = true;
                break;

            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                if (ViewModel.OpenCommand.CanExecute(null)) ViewModel.OpenCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemPlus when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.Add when Keyboard.Modifiers == ModifierKeys.Control:
                ViewModel.ZoomBySteps(1);
                e.Handled = true;
                break;

            case Key.OemMinus when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.Subtract when Keyboard.Modifiers == ModifierKeys.Control:
                ViewModel.ZoomBySteps(-1);
                e.Handled = true;
                break;

            case Key.D0 when Keyboard.Modifiers == ModifierKeys.Control:
                ViewModel.SetZoomPercent(WordViewerConfig.DefaultZoomPercent);
                e.Handled = true;
                break;

            case Key.Escape:
                // 只清自己的查找状态，不抢外壳的 Esc（全屏等）
                if (ViewModel.SearchText.Length == 0) return;
                ViewModel.ClearSearch();
                e.Handled = true;
                break;
        }
    }

    private void FocusFindBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is null) return;

        switch (e.Key)
        {
            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) ViewModel.SearchPrev();
                else ViewModel.SearchNext();
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel.ClearSearch();
                e.Handled = true;
                break;
        }
    }

    // ---------------------------------------------------------------- 大纲跳转

    private void OnOutlineItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not WordOutlineItem item) return;
        ViewModel.BringBlockIntoView(item.BlockIndex);
    }

    // ---------------------------------------------------------------- 批注面板 / 打印 / 导出

    /// <summary>点批注面板里的一条：跳正文 + 尽量居中（具体滚动在 <see cref="CenterBlockInViewer"/>）。</summary>
    private void OnCommentItemClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if ((sender as FrameworkElement)?.DataContext is not WordCommentItem item) return;
        ViewModel.BringCommentIntoView(item);
    }

    /// <summary>
    /// 「导出…」按钮左键也弹菜单（默认只有右键会开 ContextMenu）。菜单项自己带 Click，
    /// 不依赖 ContextMenu 继承 DataContext（ContextMenu 不在可视树里，绑定容易踩空）。
    /// </summary>
    private void OnExportButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void OnExportHtmlClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _ = ViewModel.ExportHtmlAsync();
    }

    private void OnExportPlainTextClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        _ = ViewModel.ExportPlainTextAsync();
    }
}
