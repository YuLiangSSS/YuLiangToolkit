using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.WordViewer.Parsing;
using YuLiangToolkit.Tools.WordViewer.ViewModels;

namespace YuLiangToolkit.Tools.WordViewer.Views;

/// <summary>
/// 「Word 阅读器」页面的代码后置：只做**必须碰输入 / 焦点 / 拖放**的事情 ——
/// 拖入打开、Ctrl+滚轮缩放、Ctrl+F 聚焦查找框、回车 / Shift+回车 / Esc 查找导航、点大纲跳转。
/// 业务逻辑（打开 / 解析 / 排版 / 查找命中）都在 <see cref="WordViewerViewModel"/> 里。
/// </summary>
public partial class WordViewerView : UserControl
{
    private WordViewerViewModel? _viewModel;
    private bool _autoOpenRequested;

    public WordViewerView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private WordViewerViewModel? ViewModel
    {
        get
        {
            _viewModel ??= DataContext as WordViewerViewModel;
            return _viewModel;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel ??= DataContext as WordViewerViewModel;
        if (_viewModel is null) return;

        // 开发 / 截图用：YLT_WORD_OPEN 指定的文档只自动打开一次
        if (_autoOpenRequested) return;
        _autoOpenRequested = true;
        _viewModel.TryOpenPendingAutoOpen();
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
}
