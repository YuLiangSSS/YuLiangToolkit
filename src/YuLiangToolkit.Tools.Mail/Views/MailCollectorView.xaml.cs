using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Mail.ViewModels;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>
/// 邮箱代收工具页的代码后置：只负责在页面第一次显示时通知 ViewModel
/// （按配置决定要不要自动刷新一次），界面逻辑全部在 ViewModel 里。
/// </summary>
public partial class MailCollectorView : UserControl
{
    public MailCollectorView()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MailCollectorViewModel viewModel) viewModel.OnViewLoaded();
    }
}
