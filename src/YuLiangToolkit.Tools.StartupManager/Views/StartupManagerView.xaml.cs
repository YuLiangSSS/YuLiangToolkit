using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace YuLiangToolkit.Tools.StartupManagerTool.Views;

/// <summary>
/// 「开机启动管理」界面代码后置：<c>InitializeComponent()</c> 与「右键先选中该行」。
/// 业务（扫描 / 启用禁用 / 删除）全部在 <c>StartupManagerViewModel</c> 里。
/// </summary>
public partial class StartupManagerView : UserControl
{
    public StartupManagerView()
    {
        InitializeComponent();
    }

    /// <summary>右键：先把鼠标下那一行的启动项选中，右键菜单里的命令才有作用对象。</summary>
    private void OnGridPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row) return;
        row.IsSelected = true;
        (sender as DataGrid)?.Focus();
    }

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
