using System.Windows;
using System.Windows.Input;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>运行日志弹窗：DataContext 直接是 ViewModel，所以 <c>LogText</c> / 清空 / 打开文件夹都能实时绑定。</summary>
public partial class MailLogDialog : Window
{
    public MailLogDialog()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
