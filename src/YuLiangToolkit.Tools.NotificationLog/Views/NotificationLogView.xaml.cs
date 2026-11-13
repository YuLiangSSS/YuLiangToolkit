using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.NotificationLog.ViewModels;

namespace YuLiangToolkit.Tools.NotificationLog.Views;

/// <summary>
/// 通知管理页代码后置：只处理「清空全部」的二次确认（破坏性操作要拦一下）。
/// </summary>
public partial class NotificationLogView : UserControl
{
    public NotificationLogView()
    {
        InitializeComponent();
    }

    /// <summary>页面的 ViewModel。</summary>
    private NotificationLogViewModel? ViewModel => DataContext as NotificationLogViewModel;

    /// <summary>
    /// 清空全部：先确认。绑定里的 Command 也会执行，所以这里用「确认后手动再调一次」的方式会重复，
    /// 改为：命令交给 ViewModel，但确认放这里 —— 取消时把命令的效果挡掉。
    /// </summary>
    /// <remarks>
    /// ⚠ WPF 的 Click 先于 Command 执行顺序不可靠，所以按钮上**只挂 Click、不挂 Command**，
    /// 全部逻辑走这里（见 XAML 注释）。这里只做确认 + 调 ViewModel。
    /// </remarks>
    private void OnClearAllClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;

        if (viewModel.History.Items.Count == 0) return;

        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"确定清空全部 {viewModel.History.Items.Count} 条通知记录吗？这个操作不能撤销。",
            "清空通知历史",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        viewModel.ClearAll();
    }
}
