using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using YuLiangToolkit.Tools.MemeBoard.ViewModels;

namespace YuLiangToolkit.Tools.MemeBoard.Views;

/// <summary>
/// 「表情包收藏夹」界面的代码后置：只做纯界面层的事，业务全在 <see cref="MemeBoardViewModel"/>。
/// <list type="bullet">
///   <item>页面第一次显示时通知 VM 检测 QQ 窗口；</item>
///   <item>把拖进来的图片文件交给 VM 导入（只接受 png / jpg / jpeg / gif / bmp）；</item>
///   <item>单击格子触发动作（不是双击）；</item>
///   <item>右键先把鼠标下那一格选中，右键菜单里的命令才有作用对象。</item>
/// </list>
/// </summary>
public partial class MemeBoardView : UserControl
{
    public MemeBoardView()
    {
        InitializeComponent();
    }

    private MemeBoardViewModel? ViewModel => DataContext as MemeBoardViewModel;

    /// <summary>页面显示：让 VM 异步检测一次 QQ 聊天窗口。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel?.OnViewLoaded();

    // ---------------------------------------------------------------- 拖入导入

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedImageFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        var files = GetDroppedImageFiles(e);
        if (files.Count == 0 || ViewModel is not { } viewModel) return;

        e.Handled = true;
        try
        {
            await viewModel.ImportPathsAsync(files);
        }
        catch (Exception ex)
        {
            // ImportPathsAsync 内部已按文件隔离异常；这里只是兜底，绝不让拖放把程序带崩。
            viewModel.StatusText = "导入失败：" + ex.Message;
        }
    }

    /// <summary>拖入的文件里只留支持的图片格式（忽略文件夹与其它类型）。</summary>
    private static IReadOnlyList<string> GetDroppedImageFiles(DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return Array.Empty<string>();
            return files.Where(MemeFiles.IsSupportedImage).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- 网格交互

    /// <summary>单击格子 = 触发动作（复制并粘贴 / 仅复制）；点在空白处不触发。</summary>
    private void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        if (ItemList.SelectedItem is not MemeItem item) return;

        var command = ViewModel?.ActivateCommand;
        if (command is not null && command.CanExecute(item)) command.Execute(item);
        e.Handled = true;
    }

    /// <summary>右键先把鼠标下那一格选中，右键菜单里的命令才有作用对象。</summary>
    private void OnItemPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } container) return;
        container.IsSelected = true;
        ItemList.Focus();
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>沿可视树 / 逻辑树往上找指定类型的祖先。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T hit) return hit;
            node = node is Visual or Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
