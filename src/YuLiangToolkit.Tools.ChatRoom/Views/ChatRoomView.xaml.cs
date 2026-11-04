using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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

    /// <summary>录音时是否真的抓住了鼠标（抓住后指针移出按钮再松开也能收到 MouseUp；没抓住就靠 Esc 兜底）。</summary>
    private bool _voiceMouseCaptured;

    /// <summary>
    /// 「正在主动松开鼠标捕获」：松开鼠标本身也会触发 LostMouseCapture，
    /// 靠这个标志把它和「录音中途被抢走捕获（窗口失焦等）」区分开 ——
    /// 不然松手会先被 LostMouseCapture 当成取消，Stop 就发不出去了。
    /// </summary>
    private bool _voiceReleasing;

    /// <summary>
    /// 这一次「按住」是否还处于按下状态（按下 → true，松开 / Esc 取消 / 丢捕获后 → false）。
    /// 用途：Esc 取消后鼠标可能还停在按钮上、松开时仍会来一次 PreviewMouseLeftButtonUp，
    /// 没这个标志会把已取消的录音再 Stop 一遍。
    /// </summary>
    private bool _voicePressed;

    /// <summary>
    /// 「按住」次数（每次按下 +1）。极快地点一下时 Start 还在开麦克风，要等它真正开始再补一次 Stop；
    /// 用这个代号保证「等开始再停」的旧任务不会误伤下一次按住。
    /// </summary>
    private int _voicePressGeneration;

    // ---------------- 语音消息播放进度（QQ 那种可拖进度条）----------------
    private DispatcherTimer? _voicePlaybackTimer;
    private MediaElement? _voicePlayingMedia;
    private ChatMessageItem? _voicePlayingItem;

    public ChatRoomView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // 视图被卸载（切走工具页 / 缓存淘汰）时停掉语音播放：MediaElement 是视图里的控件，
        // 不能留一个「看不见还在播、进度条却没人更新」的孤儿播放器。
        Unloaded += (_, _) => StopVoicePlayback();
        // 录音中按 Esc = 取消：只挂在本视图的 PreviewKeyDown 上，不动外层窗口。
        // ⚠ 挂在这里而不是 Loaded：外壳会缓存视图、Loaded 可能触发多次，构造函数每个实例只跑一次，天然防重复挂。
        PreviewKeyDown += OnVoicePreviewKeyDown;
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

            // 截图专用的第三件事：有 YLT_CHAT_CALL_DEMO 时把通话浮层滚进视野。
            // 原因：主机在线时「连接信息」面板很高，默认 1280×820 下聊天区块被顶到窗口外，
            // 光看截图只能看到浮层顶栏、看不到参与者磁贴。这里排在「滚回顶部」之后执行，
            // 只在演示模式生效（正常使用 / 普通截图完全不受影响）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Environment.GetEnvironmentVariable("YLT_CHAT_CALL_DEMO") is null) return;
                if (Environment.GetEnvironmentVariable("YLT_CHAT_SCROLL") is { Length: > 0 }) return;
                CallPanel.BringIntoView();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

        // 截图 / 排错兜底：自动开局是异步的，DataContextChanged 时演示通话可能还没塞进去；
        // 等界面 Loaded 再补一次（SeedCallDemoIfRequested 是幂等的，塞过就直接返回）。
        Loaded += (_, _) => (DataContext as ChatRoomViewModel)?.SeedCallDemoIfRequested();
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
        if (_vm is not null)
        {
            _vm.MessagesChanged += OnMessagesChanged;
            // 截图 / 排错：YLT_CHAT_CALL_DEMO=1 时补一份通话演示数据。
            // 会话层接线时已经塞过一次；这里兜底「没连上房间（例如关了自动开局）」也要能看到面板。
            _vm.SeedCallDemoIfRequested();
        }
    }

    /// <summary>新消息来了：如果用户没有往上翻（滚动条在底部附近），就自动滚到底。</summary>
    private void OnMessagesChanged()
    {
        if (!_autoScroll) return;
        // 延到下一回合再滚：正在处理集合变更时强制布局会让 ItemsControl 计数不同步
        // （见 docs/dev/pitfalls.md 的拖动排序那条踩坑）
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

        // 语音消息有自己的「播放 / 暂停 + 进度条」逻辑（见 ToggleVoicePlayback）
        if (element.DataContext is ChatMessageItem { IsVoiceMessage: true } voiceItem)
        {
            ToggleVoicePlayback(media, voiceItem);
            return;
        }

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

    // ---------------------------------------------------------------- 语音消息（按住说话）

    /// <summary>
    /// 按住「🎙 按住说话」：先抓住鼠标（保证指针移出按钮后松开也还能收到 MouseUp），再让 ViewModel 开始录音。
    /// <para>不 await 录音任务：事件处理器要保持轻，麦克风启动期间界面照常刷新（按钮文案由绑定自己更新）。</para>
    /// </summary>
    private void OnVoiceRecordDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ChatRoomViewModel vm) return;
        if (e.ChangedButton != MouseButton.Left) return;

        _voiceReleasing = false;
        _voicePressed = true;
        _voicePressGeneration++;
        _voiceMouseCaptured = VoiceRecordButton.CaptureMouse();
        _ = vm.StartVoiceRecordingAsync();
        e.Handled = true;
    }

    /// <summary>
    /// 松开「🎙 按住说话」：先松开鼠标捕获（打上「主动松开」标记），再让 ViewModel 停下并自动发送。
    /// </summary>
    private void OnVoiceRecordUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ChatRoomViewModel vm) return;
        if (e.ChangedButton != MouseButton.Left) return;
        // Esc 取消 / 丢捕获取消之后，鼠标还停在按钮上时松开仍会走到这里 —— 已经取消过了，别再 Stop 一次
        if (!_voicePressed) return;
        _voicePressed = false;

        _voiceReleasing = true;
        try
        {
            if (VoiceRecordButton.IsMouseCaptured) VoiceRecordButton.ReleaseMouseCapture();
            _voiceMouseCaptured = false;
            if (vm.IsRecordingVoice)
            {
                // 正常情况：录音已经起来了，立刻停（Stop 的同步部分就会把 IsRecordingVoice 置 false，不卡界面）
                _ = vm.StopVoiceRecordingAsync(send: true);
            }
            else
            {
                // 极快地点一下：StartVoiceRecordingAsync 可能还停在「打开麦克风」那一步，这时 Stop 会因为
                // IsRecordingVoice 仍是 false 直接落空 —— 等它真正开始再补一次，否则录音会一直开着（见 helper 注释）。
                _ = StopVoiceWhenStartedAsync(vm, _voicePressGeneration);
            }
        }
        finally
        {
            _voiceReleasing = false;
        }
        e.Handled = true;
    }

    /// <summary>
    /// 「按下后很快又松开」的补偿：<see cref="ChatRoomViewModel.StartVoiceRecordingAsync"/> 打开麦克风是异步的，
    /// 松开时 <see cref="ChatRoomViewModel.IsRecordingVoice"/> 可能还是 false，直接 Stop 会落空、录音随后才真正开始
    /// 并一直录下去。这里等录音真正开始（最多约 5 秒，正常设备 &lt;1 秒）再停 + 自动发送。
    /// <para>用 <paramref name="generation"/> 认领这一次按住：等待期间用户又按了一次（代号变了）就作废，别停错录的那条。</para>
    /// </summary>
    private async Task StopVoiceWhenStartedAsync(ChatRoomViewModel vm, int generation)
    {
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(50);
            if (generation != _voicePressGeneration) return;
            if (!vm.IsRecordingVoice) continue;
            await vm.StopVoiceRecordingAsync(send: true);
            return;
        }
    }

    /// <summary>
    /// 录音中丢了鼠标捕获（窗口失焦、系统弹窗抢焦点、别的控件抢捕获）→ 取消这条语音，不发半截录音。
    /// <para>⚠ 松开鼠标本身也会走到这里：<see cref="_voiceReleasing"/> 为 true 时直接返回，
    /// 否则会「先取消、再 Stop」，刚录好的语音就发不出去了。</para>
    /// </summary>
    private void OnVoiceRecordLostCapture(object sender, MouseEventArgs e)
    {
        var hadCapture = _voiceMouseCaptured;
        _voiceMouseCaptured = false;
        if (!hadCapture || _voiceReleasing) return;
        if (DataContext is ChatRoomViewModel { IsRecordingVoice: true } vm)
        {
            _voicePressed = false;
            vm.CancelVoiceRecording();
        }
    }

    /// <summary>录音中按 Esc = 取消（不发送）。平时不拦 Esc，外层窗口 / 工具该干嘛干嘛。</summary>
    private void OnVoicePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (DataContext is not ChatRoomViewModel { IsRecordingVoice: true } vm) return;

        // 先摘掉鼠标捕获再取消，免得 LostMouseCapture 又跑一遍取消逻辑
        _voicePressed = false;
        _voiceReleasing = true;
        try
        {
            if (VoiceRecordButton.IsMouseCaptured) VoiceRecordButton.ReleaseMouseCapture();
            _voiceMouseCaptured = false;
        }
        finally
        {
            _voiceReleasing = false;
        }
        vm.CancelVoiceRecording();
        e.Handled = true;
    }

    // ---------------------------------------------------------------- 语音消息播放进度（可拖进度条）

    /// <summary>
    /// 点语音气泡上的「▶ 播放 / ⏸ 暂停」。
    /// <para>同一时刻只播一条语音：其它语音条会 Stop 并把进度归零（和 QQ 一样，点另一条就换着听）。</para>
    /// <para>播到结尾 / 拖到结尾后再点播放，从 0 重新开始。</para>
    /// </summary>
    private void ToggleVoicePlayback(MediaElement media, ChatMessageItem item)
    {
        try
        {
            if (!item.CanPlayVoice)
            {
                // 演示数据 / 本地文件被删了：没有可播的 MP3，别把按钮切成「暂停」骗人
                if (DataContext is ChatRoomViewModel vm) vm.NoticeExternal("这条语音没有本地文件（演示数据或文件已被删）");
                return;
            }

            if (item.IsVoicePlaying)
            {
                media.Pause();
                item.IsVoicePlaying = false;
                StopVoiceTimer();
                return;
            }

            foreach (var other in FindAll<MediaElement>(MessageList))
            {
                if (ReferenceEquals(other, media)) continue;
                try { other.Stop(); } catch { }
                if (other.DataContext is ChatMessageItem { IsVoiceMessage: true } otherVoice)
                    otherVoice.ResetVoicePlayback();
            }

            // 已经播到结尾（或拖到结尾）再点播放：先回到 0，否则 Play() 可能只响一声就结束
            if (media.NaturalDuration.HasTimeSpan
                && media.Position >= media.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(80))
                media.Position = TimeSpan.Zero;

            media.Play();
            item.IsVoicePlaying = true;
            if (media.NaturalDuration.HasTimeSpan && media.NaturalDuration.TimeSpan.TotalSeconds > 0)
                item.VoiceTotalSeconds = media.NaturalDuration.TimeSpan.TotalSeconds;
            _voicePlayingMedia = media;
            _voicePlayingItem = item;
            StartVoiceTimer();
        }
        catch (Exception ex)
        {
            item.IsVoicePlaying = false;
            if (DataContext is ChatRoomViewModel vm) vm.NoticeExternal($"播放失败：{ex.Message}");
        }
    }

    /// <summary>MediaElement 打开完成：用真实时长更新进度条（比消息里记录的整秒更准）。</summary>
    private void OnVoiceMediaOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement media || media.DataContext is not ChatMessageItem item) return;
        try
        {
            if (media.NaturalDuration.HasTimeSpan && media.NaturalDuration.TimeSpan.TotalSeconds > 0)
                item.VoiceTotalSeconds = media.NaturalDuration.TimeSpan.TotalSeconds;
            item.VoicePositionSeconds = media.Position.TotalSeconds;
        }
        catch { /* 取不到时长就用消息里记录的秒数 */ }
    }

    /// <summary>播到结尾：进度归零、按钮变回「▶ 播放」，可以再点一次从头听（反复听）。</summary>
    private void OnVoiceMediaEnded(object sender, RoutedEventArgs e)
    {
        if (sender is not MediaElement media || media.DataContext is not ChatMessageItem item) return;
        try { media.Position = TimeSpan.Zero; } catch { }
        item.ResetVoicePlayback();
        if (ReferenceEquals(_voicePlayingMedia, media)) StopVoiceTimer();
    }

    /// <summary>播放失败（文件被删 / 格式不支持）：复位界面并提示一句。</summary>
    private void OnVoiceMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is not MediaElement media || media.DataContext is not ChatMessageItem item) return;
        item.ResetVoicePlayback();
        if (ReferenceEquals(_voicePlayingMedia, media)) StopVoiceTimer();
        if (DataContext is ChatRoomViewModel vm)
            vm.NoticeExternal($"语音播放失败：{e.ErrorException?.Message ?? "文件损坏或格式不支持"}");
    }

    /// <summary>
    /// 点 / 拖进度条：把 MediaElement 的 Position 设到对应秒数（暂停时也能拖，拖完点播放就从那儿开始）。
    /// <para>⚠ 播放时定时器会改 <c>VoicePositionSeconds</c>，绑定回写 Slider.Value 也会触发本事件：
    /// 位置已经差不多就直接返回，否则每 200ms 都会把播放位置重新拽一次（听感上是「卡顿 / 跳回」）。</para>
    /// </summary>
    private void OnVoiceSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        if (sender is not Slider slider || slider.DataContext is not ChatMessageItem item) return;
        var media = FindSiblingMedia(slider);
        if (media is null) return;

        try
        {
            var target = TimeSpan.FromSeconds(Math.Max(0, e.NewValue));
            if (media.NaturalDuration.HasTimeSpan && target > media.NaturalDuration.TimeSpan)
                target = media.NaturalDuration.TimeSpan;
            if ((media.Position - target).Duration() < TimeSpan.FromMilliseconds(180)) return;   // 程序刷新，不是用户拖动
            media.Position = target;
            item.VoicePositionSeconds = target.TotalSeconds;
        }
        catch { /* 还没打开 / 播放器忙：这次拖动忽略，下一次再试 */ }
    }

    private void StartVoiceTimer()
    {
        _voicePlaybackTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _voicePlaybackTimer.Tick -= OnVoicePlaybackTimerTick;
        _voicePlaybackTimer.Tick += OnVoicePlaybackTimerTick;
        _voicePlaybackTimer.Start();
    }

    private void StopVoiceTimer()
    {
        if (_voicePlaybackTimer is null) return;
        _voicePlaybackTimer.Tick -= OnVoicePlaybackTimerTick;
        _voicePlaybackTimer.Stop();
        _voicePlayingMedia = null;
        _voicePlayingItem = null;
    }

    /// <summary>每 200ms 把播放位置刷到进度条（只更新当前那条语音）。</summary>
    private void OnVoicePlaybackTimerTick(object? sender, EventArgs e)
    {
        var media = _voicePlayingMedia;
        var item = _voicePlayingItem;
        if (media is null || item is null)
        {
            StopVoiceTimer();
            return;
        }

        try
        {
            var position = media.Position.TotalSeconds;
            if (Math.Abs(position - item.VoicePositionSeconds) > 0.05)
                item.VoicePositionSeconds = position;
            if (item.VoiceTotalSeconds <= 0 && media.NaturalDuration.HasTimeSpan)
                item.VoiceTotalSeconds = media.NaturalDuration.TimeSpan.TotalSeconds;
            // 播放器自己停了（被外部 Stop / 文件没了）：按钮状态兜底复位
            if (!media.CanPause && item.IsVoicePlaying)
            {
                item.IsVoicePlaying = false;
                StopVoiceTimer();
            }
        }
        catch
        {
            StopVoiceTimer();
        }
    }

    /// <summary>停掉当前语音播放并把界面复位（视图卸载 / 切走工具页时用）。</summary>
    private void StopVoicePlayback()
    {
        var media = _voicePlayingMedia;
        var item = _voicePlayingItem;
        try { media?.Pause(); } catch { }
        item?.ResetVoicePlayback();
        StopVoiceTimer();
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
