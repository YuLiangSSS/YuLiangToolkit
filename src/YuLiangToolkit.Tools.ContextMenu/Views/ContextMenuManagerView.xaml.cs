using System.Windows.Controls;

namespace YuLiangToolkit.Tools.ContextMenu.Views;

/// <summary>
/// 「右键菜单管理」的页面。所有逻辑都在 <c>ContextMenuManagerViewModel</c> 里，
/// 这里只保留 <see cref="InitializeComponent"/>（数据上下文由 <c>ContextMenuManagerTool.CreateView</c> 注入）。
/// </summary>
public partial class ContextMenuManagerView : UserControl
{
    public ContextMenuManagerView()
    {
        InitializeComponent();
    }
}
