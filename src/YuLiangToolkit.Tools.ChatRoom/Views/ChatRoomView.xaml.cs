using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YuLiangToolkit.Tools.ChatRoom.ViewModels;

namespace YuLiangToolkit.Tools.ChatRoom.Views;

/// <summary>
/// 界面代码后置：只做三件必须碰到可视树的事 ——
/// 双击图片打开原图、播放 / 暂停音视频、滚到底跟进新消息。
/// 其余全部走绑定（命令都在 ViewModel 里）。
/// </summary>
public partial class ChatRoomView : UserControl
{
    private ChatRoomViewModel? _vm;
    private bool _autoScroll = true;
    /// <summary>界面刚开始那几帧不认「用户往上翻了」——启动过程本身会发一串 ScrollChanged，</summary>
    private long _autoScrollArmedAt;

    public ChatRoomView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            ApplyWindowSizeOverride();
            InputBox.Focus();
            // 先把页面拉回顶部：初次聚焦输入框时，WPF 会把「让焦点控件可见」的请求一路传到外层 ScrollViewer，
            // 结果一打开工具看到的是页面中段（标题被顶出视口）。等布局稳定后显式归零一次
            // （截图要看底部时用 YLT_CHAT_SCROLL 指定位置，这里就不抢了）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Environment.GetEnvironmentVariable("YLT_CHAT_SCROLL") is { Length: > 0 }) ApplyScrollOverride();
                else OuterScroll?.ScrollToTop();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            // 再把聊天记录滚到底（只有真的有消息时才做）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                MessageList.UpdateLayout();     // 先把列表自身的布局跑完，否则 ScrollToEnd 时 ExtentHeight 还是 0
                ScrollMessagesToEnd();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        // 消息列表：初始滚到底 + 用户自己往上翻时不抢滚动位置
        // （ScrollChanged 是附加事件，会从 ListBox 内部那个 ScrollViewer 冒上来）
        MessageList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnMessageListScrollChanged));
        MessageList.Loaded += (_, _) =>
        {
            // 布局跑完之前 ExtentHeight 还是 0，那时候滚不到底；等一回合 + 500ms 各滚一次最稳
            _autoScrollArmedAt = Environment.TickCount64 + 600;
            Dispatcher.BeginInvoke(new Action(ScrollMessagesToEnd), System.Windows.Threading.DispatcherPriority.Background);
            var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            settle.Tick += (_, _) =>
            {
                settle.Stop();
                if (_autoScroll) ScrollMessagesToEnd();
                _autoScrollArmedAt = 0;
            };
            settle.Start();
        };
    }

    /// <summary>本页最外层的滚动宿主（页面很长，整页滚动）。</summary>
    private ScrollViewer? OuterScroll => Content as ScrollViewer;

    /// <summary>
    /// 排错 / 截图用：<c>YLT_CHAT_SIZE=1600x1000</c> 时把窗口改成这个尺寸再截图。
    /// 页面很长，底部三栏在默认 1280×820 里看不到，要核对它们的布局就得先把窗口放大。
    /// </summary>
    private void ApplyWindowSizeOverride()
    {
        var text = Environment.GetEnvironmentVariable("YLT_CHAT_SIZE");
        if (string.IsNullOrWhiteSpace(text)) return;
        var parts = text.Trim().ToLowerInvariant().Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height)) return;
        if (Window.GetWindow(this) is not { } window) return;
        window.Width = Math.Clamp(width, 1024, 4000);
        window.Height = Math.Clamp(height, 700, 2400);
    }

    /// <summary>
    /// 排错 / 截图用：<c>YLT_CHAT_SCROLL=700</c> 时把页面滚到指定位置再截图
    /// （配合 <c>YLT_CHAT_SIZE</c> 才能看到底部的流量 / 发送选项 / 文件传输三栏）。
    /// </summary>
    private void ApplyScrollOverride()
    {
        var text = Environment.GetEnvironmentVariable("YLT_CHAT_SCROLL");
        if (string.IsNullOrWhiteSpace(text) || !double.TryParse(text, out var offset)) return;
        OuterScroll?.ScrollToVerticalOffset(Math.Max(0, offset));
    }

    /// <summary>
    /// 滚到消息列表最底下。
    ///
    /// <para>⚠ <b>这里必须直接操作 ListBox 内部那个 ScrollViewer，不能用 <c>ListBox.ScrollIntoView</c></b>：
    /// <c>ScrollIntoView</c> 走的是 <c>BringIntoView</c> 路由，会把请求一路传到<b>外层页面</b>的
    /// ScrollViewer —— 结果打开工具时整页被顶下去（标题跑出视口），怎么调优先级都没用。
    /// 直接 <c>ScrollViewer.ScrollToEnd()</c> 只动内层，外层纹丝不动。</para>
    /// </summary>
    /// <summary>
    /// 滚到消息列表最底下（初始打开时把最新的消息摆出来；新消息到达时也走这里）。
    ///
    /// <para>⚠ <b>必须直接操作 ListBox 内部那个 ScrollViewer，不能用 <c>ListBox.ScrollIntoView</c></b>：
    /// <c>ScrollIntoView</c> 走的是 <c>BringIntoView</c> 路由，请求会一路传到<b>外层页面</b>的 ScrollViewer ——
    /// 结果打开工具时整页被顶下去（标题跑出视口），怎么调优先级都没用。
    /// 直接 <c>ScrollViewer.ScrollToEnd()</c> 只动内层，外层纹丝不动。</para>
    /// </summary>
    private void ScrollMessagesToEnd()
    {
        if (MessageList.Items.Count == 0) return;
        FindChild<ScrollViewer>(MessageList)?.ScrollToEnd();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.MessagesChanged -= OnMessagesChanged;
        _vm = e.NewValue as ChatRoomViewModel;
        if (_vm is not null) _vm.MessagesChanged += OnMessagesChanged;
    }

    /// <summary>新消息来了：如果用户没有往上翻（滚动条在底部附近），就自动滚到底。</summary>
    private void OnMessagesChanged()
    {
        if (!_autoScroll) return;
        // 延到下一回合再滚：正在处理集合变更时强制布局会让 ItemsControl 计数不同步
        // （见 代码记录.md 的拖动排序那条踩坑）
        Dispatcher.BeginInvoke(new Action(ScrollMessagesToEnd),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>监听滚动：用户自己往上翻的时候不要抢滚动位置。</summary>
    private void OnMessageListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 刚打开的头 600ms 内不认“用户往上翻了”：启动过程（量布局、滚到底）自己会发一串 ScrollChanged，
        // 认了就会把自动滚到底直接关掉，表现成“一打开工具列表停在最上面”（踩过）。
        if (_autoScrollArmedAt != 0 && Environment.TickCount64 < _autoScrollArmedAt) return;

        var scrollViewer = FindChild<ScrollViewer>(MessageList);
        if (scrollViewer is null) return;
        _autoScroll = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 40;
        _ = e;
    }

    // ---------------------------------------------------------------- 文件操作

    /// <summary>双击图片 = 用系统默认看图程序打开原图。</summary>
    private void OnImageClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount < 2) return;
        if ((sender as FrameworkElement)?.DataContext is not ChatMessageItem item) return;
        OpenPath(item.LocalPath);
        e.Handled = true;
    }

    /// <summary>「打开文件 / 打开」按钮：<c>Tag</c> 里是本机完整路径。</summary>
    private void OnOpenFileButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) OpenPath(path);
    }

    /// <summary>「打开所在文件夹」按钮。</summary>
    private void OnRevealFileButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var item = element.DataContext as ChatMessageItem;
        if (DataContext is ChatRoomViewModel vm) vm.RevealFile(item);
    }

    /// <summary>播放 / 暂停同一条消息里的音频或视频（找同一条消息里的 MediaElement，不去碰别的）。</summary>
    private void OnToggleMediaClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        var media = FindSiblingMedia(element);
        if (media is null) return;

        try
        {
            if (media.CanPause)
            {
                media.Pause();
                return;
            }
            // 同一个列表里可能有多个播放器，先停掉其它的，免得两个声音一起响
            foreach (var other in FindAll<MediaElement>(MessageList))
                if (!ReferenceEquals(other, media)) other.Stop();
            media.Play();
        }
        catch (Exception ex)
        {
            if (DataContext is ChatRoomViewModel vm) vm.NoticeExternal($"播放失败：{ex.Message}");
        }
    }

    /// <summary>在同一个消息的可视树里找那个 <see cref="MediaElement"/>。</summary>
    private static MediaElement? FindSiblingMedia(DependencyObject start)
    {
        // 往上找到那一行（ListBoxItem），再往下搜
        var current = start;
        while (current is not null and not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);
        return current is null ? null : FindChild<MediaElement>(current);
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* 打不开就算了，界面上还有「打开所在文件夹」 */ }
    }

    // ---------------------------------------------------------------- 可视树小工具

    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var nested = FindChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) yield break;
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }
}
