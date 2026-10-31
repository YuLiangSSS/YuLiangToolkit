using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YuLiangToolkit.Tools.FlashDownloader.Core;
using YuLiangToolkit.Tools.FlashDownloader.ViewModels;

namespace YuLiangToolkit.Tools.FlashDownloader.Views;

/// <summary>
/// 「Flash 下载器」界面。这里只放“必须碰控件”的几件事：
/// ① 双击游戏列表 = 游玩（按鼠标底下那一行，而不是“当前选中项”，免得双击空白处也开游戏）；
/// ② 日志自动滚到最底；
/// ③ Ctrl+L 聚焦地址栏、Enter = 下载并游玩；Ctrl+F 聚焦游戏搜索框、Esc 清空搜索。
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
        // 截图 / 调试：YLT_FLASH_SCROLL=bottom 滚到页面底部；=数字（像素）滚到指定位置。
        // 长页面（主区域 + 日志）默认一屏看不全，用它核对「区块底边对齐 / 比例」。
        ScheduleDebugScroll();

        // 验证用：YLT_FLASH_SELECT=<文件名或文件夹名> 时启动即选中那一行
        // （截图检查“选中高亮”用；也方便以后写 UI 自动化脚本，见 AGENTS.md）
        var wanted = Environment.GetEnvironmentVariable("YLT_FLASH_SELECT");
        if (!string.IsNullOrWhiteSpace(wanted) && ViewModel is { } vm)
        {
            var hit = vm.Games.FirstOrDefault(g =>
                string.Equals(g.Entry.FileOrFolderName, wanted.Trim(), StringComparison.OrdinalIgnoreCase));
            if (hit is not null) vm.SelectedGame = hit;
        }

        UrlBox.Focus();
        UrlBox.SelectAll();
    }

    /// <summary>
    /// 截图 / 调试：<c>YLT_FLASH_SCROLL=bottom</c> 滚到页面底部，<c>=数字</c> 滚到指定像素位置。
    /// 只影响调试运行（截图用），不改配置；页面较长时靠它核对区块底边对齐。
    /// </summary>
    private void ScheduleDebugScroll()
    {
        var wanted = Environment.GetEnvironmentVariable("YLT_FLASH_SCROLL");
        if (string.IsNullOrWhiteSpace(wanted)) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            // 整页的 ScrollViewer 就是这个 UserControl 的根内容（别再往上找，那是外壳）
            var scroller = Content as ScrollViewer ?? FindAncestor<ScrollViewer>(this);
            if (scroller is null) return;
            scroller.UpdateLayout();

            if (wanted.Trim().Equals("bottom", StringComparison.OrdinalIgnoreCase))
            {
                scroller.ScrollToEnd();
            }
            else if (double.TryParse(wanted, System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out var offset))
            {
                scroller.ScrollToVerticalOffset(Math.Max(0, offset));
            }
        }));
    }

    /// <summary>沿可视化 / 逻辑树往上找第一个指定类型的祖先（找不到返回 null）。</summary>
    private static T? FindAncestor<T>(DependencyObject element) where T : DependencyObject
    {
        for (var node = element; node is not null;)
        {
            var parent = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : null;
            parent ??= LogicalTreeHelper.GetParent(node);
            if (parent is null) return null;
            if (parent is T hit) return hit;
            node = parent;
        }
        return null;
    }

    private void OnGameListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 命中的那一行才是要玩的（双击空白处时往上找不到 ListBoxItem，直接忽略）
        var entry = FindEntryUnder(e.OriginalSource as DependencyObject);
        if (entry is null) return;
        ViewModel?.PlayEntry(entry);
        e.Handled = true;
    }

    /// <summary>从被点到的元素往上找属于哪一行，并取出它的 <see cref="GameEntry"/>。</summary>
    private static GameEntry? FindEntryUnder(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem { DataContext: GameItem item }) return item.Entry;
        }
        return null;
    }

    private void OnLogTextChanged(object sender, TextChangedEventArgs e)
    {
        // 只有用户没往上翻的时候才自动跟到底（免得抢走用户正在看的位置）
        var box = (TextBox)sender;
        if (box.ExtentHeight - box.ViewportHeight - box.VerticalOffset < 24)
            box.ScrollToEnd();
    }

    /// <summary>Enter = 下载并游玩；Ctrl+L = 地址栏；Ctrl+F = 搜索游戏；Esc = 清空搜索。</summary>
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
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key == Key.L)
            {
                UrlBox.Focus();
                UrlBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.F)
            {
                GameSearchBox.Focus();
                GameSearchBox.SelectAll();
                e.Handled = true;
            }
            return;
        }

        if (e.Key == Key.Escape && GameSearchBox.IsKeyboardFocusWithin && ViewModel is { HasGameSearchText: true })
        {
            ViewModel.GameSearchText = "";
            e.Handled = true;
        }
    }
}
