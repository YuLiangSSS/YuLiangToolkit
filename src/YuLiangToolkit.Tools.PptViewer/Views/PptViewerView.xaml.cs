using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YuLiangToolkit.Tools.PptViewer.ViewModels;

namespace YuLiangToolkit.Tools.PptViewer.Views;

/// <summary>
/// 「PPT 阅读器」页面的代码后置：只做**必须碰输入 / 焦点 / 拖放**的事情 ——
/// 拖入打开、Ctrl+滚轮缩放、Ctrl+F 聚焦查找框、翻页 / 查找快捷键、缩放输入框回车提交、
/// 跟着当前页滚动缩略图列表、切走时把阅读位置落盘。
/// 业务逻辑（打开 / 解析 / 渲染 / 查找命中 / 放映）都在 <see cref="PptViewerViewModel"/> 里。
/// </summary>
public partial class PptViewerView : UserControl
{
    private PptViewerViewModel? _viewModel;

    /// <summary>是否已经挂上 ViewModel 的 PropertyChanged（DataContext 变了 / 视图缓存重挂时要接对）。</summary>
    private bool _subscribed;

    public PptViewerView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseWheel += OnPreviewMouseWheel;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private PptViewerViewModel? ViewModel
    {
        get
        {
            _viewModel ??= DataContext as PptViewerViewModel;
            return _viewModel;
        }
    }

    // ---------------------------------------------------------------- 生命周期 / DataContext

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        _viewModel = DataContext as PptViewerViewModel;
        AttachViewModel();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();

        // 视图被外壳缓存后重新挂回来时，DataContextChanged 不会再触发，这里补一次自动打开
        if (ViewModel is { } viewModel)
        {
            viewModel.TryOpenPendingAutoOpen();

            // 让键盘快捷键（翻页 / F5）落在工具页上（外壳刚切过来时焦点可能还在侧边栏）
            if (!IsKeyboardFocusWithin) Focus();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // 切走工具页（视图被外壳缓存）：把当前页号立刻落盘，切回来位置不变
        SearchResultsPopup.IsOpen = false;
        ViewModel?.FlushReadingPosition(saveNow: true);
        DetachViewModel();
    }

    private void AttachViewModel()
    {
        if (_subscribed || ViewModel is null) return;

        // Popup 不在可视树里，DataContext 不会从 UserControl 继承，手动接上（否则结果列表是空的）
        SearchResultsPopup.DataContext = ViewModel;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _subscribed = true;
    }

    private void DetachViewModel()
    {
        if (!_subscribed || _viewModel is null) return;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = false;
    }

    /// <summary>当前页变化时把缩略图列表滚到对应项（否则翻页看不出来是第几张）。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PptViewerViewModel.CurrentSlide) or nameof(PptViewerViewModel.HasDocument)))
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsLoaded) return;   // 视图已经切走 / 被回收：不要再动列表

            var current = ViewModel?.CurrentSlide;
            if (current is not null) SlideList.ScrollIntoView(current);
        }));
    }

    // ---------------------------------------------------------------- 拖入打开

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        e.Effects = TryGetDroppedPresentation(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        var path = TryGetDroppedPresentation(e);
        if (path is null || ViewModel is null) return;
        _ = ViewModel.OpenDroppedFileAsync(path);
    }

    /// <summary>拖放数据里第一个受支持的演示文稿（拖一堆文件时只打开第一个能认的）。</summary>
    private static string? TryGetDroppedPresentation(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return null;
        return paths.FirstOrDefault(PptViewerViewModel.IsSupportedExtension);
    }

    // ---------------------------------------------------------------- 缩放

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 只有按着 Ctrl 才是缩放，普通滚轮留给缩略图列表 / 大图滚动
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || ViewModel is null) return;

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
        if (ViewModel is not { } viewModel) return;

        // 全局快捷键：文本框 / 下拉框聚焦时也要能用
        switch (e.Key)
        {
            case Key.O when Keyboard.Modifiers == ModifierKeys.Control:
                if (viewModel.OpenCommand.CanExecute(null)) viewModel.OpenCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.F when Keyboard.Modifiers == ModifierKeys.Control:
                FocusSearchBox();
                e.Handled = true;
                return;

            case Key.F5:
                if (viewModel.StartPresentationCommand.CanExecute(null))
                    viewModel.StartPresentationCommand.Execute(null);
                e.Handled = true;
                return;

            case Key.D0 when Keyboard.Modifiers == ModifierKeys.Control:
            case Key.NumPad0 when Keyboard.Modifiers == ModifierKeys.Control:
                // Ctrl+0 恢复 100% 缩放（会关掉「适应窗口」；和 Word 阅读器一致）
                viewModel.SetZoomPercent(PptViewerConfig.DefaultZoomPercent);
                e.Handled = true;
                return;

            case Key.Escape:
                // 只清自己的查找状态，不抢外壳的 Esc（全屏等）
                if (viewModel.SearchText.Length == 0) return;
                viewModel.ClearSearch();
                e.Handled = true;
                return;
        }

        // 在输入框 / 下拉框里打字时不要抢方向键、空格、Home / End
        if (IsTextInputFocused()) return;

        var focusInList = IsInsideListBox(Keyboard.FocusedElement as DependencyObject);

        switch (e.Key)
        {
            case Key.PageDown:
                if (viewModel.NextSlideCommand.CanExecute(null)) viewModel.NextSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.PageUp:
                if (viewModel.PrevSlideCommand.CanExecute(null)) viewModel.PrevSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right:
                if (viewModel.NextSlideCommand.CanExecute(null)) viewModel.NextSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left:
                if (viewModel.PrevSlideCommand.CanExecute(null)) viewModel.PrevSlideCommand.Execute(null);
                e.Handled = true;
                break;

            // ↑ / ↓ / Home / End 在缩略图列表里交给 ListBox 自己改选中项（选中即切页）
            case Key.Down when !focusInList:
                if (viewModel.NextSlideCommand.CanExecute(null)) viewModel.NextSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Up when !focusInList:
                if (viewModel.PrevSlideCommand.CanExecute(null)) viewModel.PrevSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Home when !focusInList:
                if (viewModel.FirstSlideCommand.CanExecute(null)) viewModel.FirstSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.End when !focusInList:
                if (viewModel.LastSlideCommand.CanExecute(null)) viewModel.LastSlideCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Space:
                // 空格在按钮 / 勾选框上表示「按一下」，别抢；其它位置当「下一页」
                if (IsActivationControlFocused() || focusInList) return;
                if (viewModel.NextSlideCommand.CanExecute(null)) viewModel.NextSlideCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        switch (e.Key)
        {
            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) viewModel.SearchPrev();
                else viewModel.SearchNext();
                e.Handled = true;
                break;

            case Key.Escape:
                viewModel.ClearSearch();
                e.Handled = true;
                break;
        }
    }

    private void FocusSearchBox()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>焦点是不是在文字输入框 / 下拉框里（此时不抢方向键与空格）。</summary>
    private static bool IsTextInputFocused()
        => Keyboard.FocusedElement is TextBoxBase or ComboBox or ComboBoxItem or PasswordBox;

    /// <summary>焦点是不是在「空格 / 回车会激活」的控件上（按钮 / 勾选框；勾选框也是 ButtonBase）。</summary>
    private static bool IsActivationControlFocused()
        => Keyboard.FocusedElement is ButtonBase or ComboBox or ComboBoxItem;

    /// <summary>沿可视树往上找，判断某个元素是不是缩略图 ListBox 里的（键盘导航不要和 ListBox 打架）。</summary>
    private static bool IsInsideListBox(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ListBox) return true;
            element = element is Visual visual
                ? VisualTreeHelper.GetParent(visual)
                : LogicalTreeHelper.GetParent(element);
        }

        return false;
    }
}
