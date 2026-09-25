using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YuLiangToolkit.Tools.FlashDownloader.ViewModels;

namespace YuLiangToolkit.Tools.FlashDownloader.Views;

/// <summary>
/// 「Flash 下载器」界面。这里只放“必须碰控件”的三件事：
/// ① 双击游戏列表 = 游玩；
/// ② 日志自动滚到最底；
/// ③ Ctrl+L 聚焦地址栏、Enter = 下载并游玩（懒人用法）。
/// </summary>
public partial class FlashDownloaderView : UserControl
{
    public FlashDownloaderView()
    {
        InitializeComponent();
        GameList.MouseDoubleClick += OnGameListDoubleClick;
        LogBox.TextChanged += OnLogTextChanged;
        Loaded += OnLoaded;
    }

    private FlashDownloaderViewModel? ViewModel => DataContext as FlashDownloaderViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    private void OnGameListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var command = ViewModel?.PlayCommand;
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        // 只有用户没往上翻的时候才自动跟到底（免得抢走用户正在看的位置）
        var box = (TextBox)sender;
        if (box.ExtentHeight - box.ViewportHeight - box.VerticalOffset < 24)
            box.ScrollToEnd();
    }

    /// <summary>Enter = 下载并游玩；Ctrl+L = 选中地址栏内容。</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Enter && UrlBox.IsKeyboardFocusWithin)
        {
            var command = ViewModel?.DownloadAndPlayCommand;
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.L && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            UrlBox.Focus();
            UrlBox.SelectAll();
            e.Handled = true;
        }
    }
}
