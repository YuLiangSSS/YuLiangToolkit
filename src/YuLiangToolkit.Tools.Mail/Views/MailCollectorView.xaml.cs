using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using YuLiangToolkit.Tools.Mail.ViewModels;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>
/// 邮箱代收工具页的代码后置：
/// <list type="bullet">
///   <item>页面第一次显示时通知 ViewModel（按配置决定要不要自动刷新一次）；</item>
///   <item><b>授权码密码框同步</b>：WPF 的 <c>PasswordBox.Password</c> 不是依赖属性、不能直接绑定，
///         所以这里和 ViewModel 的 <c>SelectedAccount.Password</c> 手动双向同步（输入时写入账号，
///         切换账号 / 重新挂回页面时从账号读出）。这样界面上只显示圆点，不会把授权码明文摊在屏幕上。</item>
/// </list>
/// </summary>
public partial class MailCollectorView : UserControl
{
    private MailCollectorViewModel? _subscribed;
    private bool _syncingPassword;

    public MailCollectorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MailCollectorViewModel? ViewModel => DataContext as MailCollectorViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        ViewModel?.OnViewLoaded();

        // 开发截图冒烟用：YLT_MAIL_SETTINGS_BOTTOM=1 时把「账号设置」滚到底（看网络代理区域）
        if (Environment.GetEnvironmentVariable("YLT_MAIL_SETTINGS_BOTTOM") == "1")
        {
            Dispatcher.BeginInvoke(
                new Action(() => SettingsScroll.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachViewModel();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachViewModel();

    private void AttachViewModel()
    {
        var viewModel = ViewModel;
        if (ReferenceEquals(_subscribed, viewModel))
        {
            SyncPasswordFromViewModel();
            return;
        }

        DetachViewModel();
        _subscribed = viewModel;
        if (viewModel is null) return;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncPasswordFromViewModel();
    }

    private void DetachViewModel()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailCollectorViewModel.SelectedAccount)) SyncPasswordFromViewModel();
    }

    private void SyncPasswordFromViewModel()
    {
        _syncingPassword = true;
        try
        {
            PasswordInput.Password = ViewModel?.SelectedAccount?.Password ?? "";
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        if (ViewModel?.SelectedAccount is { } account) account.Password = PasswordInput.Password;
    }
}
