using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Launcher.Models;
using YuLiangToolkit.Tools.Launcher.ViewModels;

namespace YuLiangToolkit.Tools.Launcher.Views;

/// <summary>
/// 「软件启动器」界面代码后置：只做纯界面层的事，业务全在 <see cref="LauncherViewModel"/>。
/// <list type="bullet">
///   <item>页面第一次显示时通知 VM 自动刷新状态；</item>
///   <item>把拖进来的 <c>.exe</c> / <c>.lnk</c> 交给 VM 导入；</item>
///   <item>双击图标启动、右键先选中再弹菜单、Enter / F2 / Delete 快捷键；</item>
///   <item>内联重命名框出现时自动聚焦并全选。</item>
/// </list>
/// </summary>
public partial class LauncherView : UserControl
{
    public LauncherView()
    {
        InitializeComponent();
    }

    private LauncherViewModel? ViewModel => DataContext as LauncherViewModel;

    /// <summary>页面显示：让 VM 按配置决定是否自动检查失效项 / 读取图标与版本信息。</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel?.OnViewLoaded();

    // ---------------------------------------------------------------- 拖入导入

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedSupportedFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        var files = GetDroppedSupportedFiles(e);
        if (files.Count == 0 || ViewModel is not { } viewModel) return;

        e.Handled = true;
        try
        {
            await viewModel.ImportPathsAsync(files);
        }
        catch (Exception ex)
        {
            // ImportPathsAsync 内部已按项隔离异常；这里只是兜底，绝不让拖放把程序带崩。
            viewModel.StatusText = "导入失败：" + ex.Message;
        }
    }

    /// <summary>拖入的文件里只留启动器支持的 .exe / .lnk（忽略文件夹与其它类型）。</summary>
    private static IReadOnlyList<string> GetDroppedSupportedFiles(DragEventArgs e)
    {
        try
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return Array.Empty<string>();
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return Array.Empty<string>();
            return files.Where(LauncherRules.IsSupportedPath).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    // ---------------------------------------------------------------- 图标墙交互

    /// <summary>双击图标启动（点在空白处不启动）。</summary>
    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) return;
        if (IconList.SelectedItem is not LauncherItemViewModel item) return;

        ViewModel?.StartItem(item);
        e.Handled = true;
    }

    /// <summary>右键先把鼠标下那一项选中，右键菜单里的命令才有作用对象。</summary>
    private void OnItemRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } container) return;
        container.IsSelected = true;
        IconList.Focus();
    }

    /// <summary>键盘快捷键：Enter 启动、F2 重命名、Delete 移除（移除仍有确认框）。</summary>
    private void OnItemKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        // 内联重命名输入框里按 Enter / Delete 是编辑文字，不能当成图标墙快捷键
        // （PreviewKeyDown 会先于 TextBox 拿到按键，所以必须在这里挡掉）。
        if (viewModel.IsRenaming || e.OriginalSource is TextBox) return;

        switch (e.Key)
        {
            case Key.Enter:
                viewModel.StartItem(viewModel.SelectedItem);
                e.Handled = true;
                break;
            case Key.F2:
                if (viewModel.BeginRenameCommand.CanExecute(null))
                {
                    viewModel.BeginRenameCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Delete:
                if (viewModel.DeleteCommand.CanExecute(null))
                {
                    viewModel.DeleteCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
    }

    // ---------------------------------------------------------------- 内联重命名

    /// <summary>重命名输入框一出现就聚焦并全选，用户可以直接输入新名字。</summary>
    private void OnRenameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox box || !box.IsVisible) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }));
    }

    /// <summary>重命名框里按 Enter 保存、Esc 取消。</summary>
    private void OnRenameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (viewModel.CommitRenameCommand.CanExecute(null))
                {
                    viewModel.CommitRenameCommand.Execute(null);
                    e.Handled = true;
                }
                break;
            case Key.Escape:
                if (viewModel.CancelRenameCommand.CanExecute(null))
                {
                    viewModel.CancelRenameCommand.Execute(null);
                    e.Handled = true;
                }
                break;
        }
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
