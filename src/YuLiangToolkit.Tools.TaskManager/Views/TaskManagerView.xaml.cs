using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace YuLiangToolkit.Tools.TaskManager.Views;

/// <summary>
/// 「任务管理器」界面代码后置：只有 <c>InitializeComponent()</c> 和「右键先选中该行」这一点纯界面逻辑，
/// 业务（采样 / 结束任务 / 开机启动项）全部在 <c>TaskManagerViewModel</c> 里。
///
/// <para>表头与数据列的对齐全部在 XAML 里解决（表头写在 ListBox 的 ControlTemplate 内，与数据行同处一列，
/// 末尾再占一列滚动条宽度）—— 这里不做任何布局同步。</para>
/// </summary>
public partial class TaskManagerView : UserControl
{
    public TaskManagerView()
    {
        InitializeComponent();
    }

    /// <summary>进程列表右键：先把鼠标下那一行的进程选中，右键菜单里的命令才有作用对象。</summary>
    private void OnProcessListPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } container) return;
        container.IsSelected = true;
        (sender as ListBox)?.Focus();
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>沿可视树 / 逻辑树往上找指定类型的祖先（与其它工具页一致的最小写法）。</summary>
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
