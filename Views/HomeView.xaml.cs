using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using YuLiangToolkit.App.ViewModels;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 主页（搜索 + 排序）。代码后置只干两件小事：
/// ① 进来就把焦点放进搜索框（主页即搜索页，可以直接打字）；
/// ② 全局快捷键：Ctrl+F 聚焦搜索框、Esc 清空搜索。
/// 这些只在主页挂载期间生效（切到功能页时 Unloaded 会摘掉钩子）。
/// </summary>
public partial class HomeView : UserControl
{
    private Window? _hostWindow;

    public HomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown += OnHostKeyDown;

        // 用 Input 优先级排队，确保窗口已经显示出来再抢焦点
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => SearchBox.Focus()));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown -= OnHostKeyDown;
        _hostWindow = null;
    }

    private void OnHostKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel is { HasSearchText: true } vm)
        {
            vm.SearchText = "";
            SearchBox.Focus();
            e.Handled = true;
        }
    }
}
