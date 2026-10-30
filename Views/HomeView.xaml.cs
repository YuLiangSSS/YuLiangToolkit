using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.App.Views;

/// <summary>
/// 主页（搜索 + 排序）。代码后置只干三件小事：
/// ① 进来就把焦点放进搜索框（主页即搜索页，可以直接打字）；
/// ② 全局快捷键：Ctrl+F 聚焦搜索框、Esc 清空搜索；
/// ③ 标签条（3 行 + 横向滚动）的滚轮路由：普通滚轮滚整页、Shift + 滚轮横滚标签条。
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

        // 截图 / 调试：YLT_HOME_TAGS_SCROLL=<像素> 打开主页时把标签条横向滚过去
        // （验证「横着滑」的方向和滚动条滑块位置用；不设就是正常停在最左边）
        var presetTagScroll = Environment.GetEnvironmentVariable("YLT_HOME_TAGS_SCROLL");
        if (double.TryParse(presetTagScroll, out var tagOffset))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => TagStripScroll.ScrollToHorizontalOffset(tagOffset)));
        }
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

    /// <summary>
    /// 标签条（3 行 + 横向滚动条）的滚轮处理。
    ///
    /// <para>为什么必须自己插手：内层 ScrollViewer 只有横向滚动，但 <c>ScrollViewer.OnMouseWheel</c>
    /// 不管滚没滚都会把事件标成已处理（见 <see cref="ScrollChaining"/> 的注释），
    /// 不接管的话鼠标停在标签条上、整个主页就滚不动了。</para>
    ///
    /// <para>规则：<b>普通滚轮</b> = 替外层整页滚一格（步长和直接滚页面一样）；
    /// <b>Shift + 滚轮</b> = 标签条自己横向滚一格（Windows 上横向滚动的习惯操作，
    /// 不拖滚动条也能横着滑）。</para>
    /// </summary>
    private void OnTagStripPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        if (sender is not ScrollViewer strip) return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            // 一格 = WheelScrollLines 行 × 16px（和页面滚动的步长口径一致：默认 3 × 16 = 48px）
            var step = Math.Max(1, SystemParameters.WheelScrollLines) * 16.0;
            var target = strip.HorizontalOffset - Math.Sign(e.Delta) * step;
            strip.ScrollToHorizontalOffset(Math.Max(0, Math.Min(strip.ScrollableWidth, target)));
            e.Handled = true;
            return;
        }

        var outer = FindAncestorScrollViewer(strip);
        if (outer is null) return;
        e.Handled = true;
        ScrollChaining.ScrollOneNotch(outer, e.Delta < 0);
    }

    /// <summary>沿可视化树往上找第一个 ScrollViewer（标签条外面的整页滚动就是它）。</summary>
    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject child)
    {
        for (var node = VisualTreeHelper.GetParent(child); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer viewer) return viewer;
        }
        return null;
    }
}
