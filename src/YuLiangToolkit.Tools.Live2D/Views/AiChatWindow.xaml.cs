using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using YuLiangToolkit.Tools.Live2D.Ai;
using YuLiangToolkit.Tools.Live2D.Controls;

namespace YuLiangToolkit.Tools.Live2D.Views;

/// <summary>
/// 桌宠的 AI 聊天窗（透明圆角小窗，跟随时贴在桌宠下方 / 旁边）。
///
/// <para><b>职责边界</b>：本窗口只负责「显示 + 收集输入」，聊天逻辑（调 DeepSeek、好感度涨掉、
/// 聊天记录落盘、主动说话调度）全在 <see cref="AiChatService"/> 里；配置改动通过
/// <c>onConfigChanged</c> 回调交给会话层立刻保存并同步工具页。</para>
///
/// <para><b>线程</b>：服务层的 <c>MessageAdded</c> / <c>StateChanged</c> 可能不在 UI 线程触发，
/// 这里统一切到窗口的 Dispatcher 再动控件。</para>
///
/// <para><b>事件生命周期</b>：只在窗口显示期间订阅 <see cref="AiChatService.MessageAdded"/> /
/// <see cref="AiChatService.StateChanged"/>，隐藏 / 关闭时退订（不占着事件、不白白刷新界面）。</para>
///
/// <para><b>不抢焦点</b>：<c>ShowActivated=False</c>，打开时不会抢走正在打字的窗口的焦点；
/// 用户点进输入框就能输入。</para>
/// </summary>
public partial class AiChatWindow : Window
{
    /// <summary>聊天服务（数据源 + 发送入口）。</summary>
    private readonly AiChatService _chat;

    /// <summary>桌宠配置（三个常用开关直接读写这里）。</summary>
    private readonly Live2DConfig _config;

    /// <summary>配置变了的回调（会话层负责保存 + 同步工具页）。</summary>
    private readonly Action? _onConfigChanged;

    /// <summary>点 ⚙ 的回调（把主窗口 / 工具页叫到前面）。</summary>
    private readonly Action? _openSettings;

    /// <summary>是否已经挂上服务事件。</summary>
    private bool _chatEventsHooked;

    /// <summary>正在按配置回填勾选框：此时不要反过来写配置（避免一开窗就触发一轮保存）。</summary>
    private bool _syncingToggles;

    /// <summary>示例数据模式（<see cref="LoadDemoMessages"/> 打开；截图冒烟专用，不碰真实聊天数据）。</summary>
    private bool _demoMode;

    /// <summary>示例消息（只在截图冒烟时替换显示源，真实 <c>chat.Messages</c> 一条都不动）。</summary>
    private readonly ObservableCollection<AiChatMessage> _demoMessages = new();

    /// <summary>快捷短语按钮最多排一行，太长的换行由 WrapPanel 自己处理。</summary>
    private const double DefaultWidth = 372;

    /// <summary>默认高度（和 XAML 里 Width / Height 保持一致，窗口还没显示时用来算位置）。</summary>
    private const double DefaultHeight = 524;

    /// <summary>
    /// 创建聊天窗（由 <see cref="Live2DSession"/> 在第一次打开时 new 出来，之后一直复用、只做隐藏 / 显示）。
    /// </summary>
    /// <param name="chat">AI 聊天服务。</param>
    /// <param name="config">桌宠配置。</param>
    /// <param name="onConfigChanged">聊天窗里改了配置后回调（保存 + 同步工具页）。</param>
    /// <param name="openSettings">点标题栏 ⚙ 时回调（打开工具页设置）。</param>
    public AiChatWindow(
        AiChatService chat,
        Live2DConfig config,
        Action? onConfigChanged,
        Action? openSettings)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _onConfigChanged = onConfigChanged;
        _openSettings = openSettings;

        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.Manual;

        // 「正在输入…」直接绑 chat.IsBusy（服务层发 PropertyChanged 时 WPF 自己刷新）
        DataContext = _chat;

        // 消息列表先接上真实聊天记录；截图冒烟时再换成示例集合
        MessageItems.ItemsSource = _chat.Messages;

        LoadConfigToggles();
        RefreshAll();
        MoveToDefaultCorner();

        Closed += OnWindowClosed;
    }

    // ==================== 对外 API（会话层用） ====================

    /// <summary>聊天窗当前是否显示着（隐藏 / 关闭都为 false）。</summary>
    public bool IsChatVisible => IsVisible;

    /// <summary>
    /// 显示聊天窗（幂等；已显示时只刷新一次界面）。真正显示时才订阅服务事件。
    /// <para>不 Activate、不 Focus，避免把用户正在打字的窗口的焦点抢走。</para>
    /// </summary>
    public void ShowChat()
    {
        HookChatEvents();

        if (!_demoMode && !ReferenceEquals(MessageItems.ItemsSource, _chat.Messages))
        {
            MessageItems.ItemsSource = _chat.Messages;
        }

        if (!IsVisible) Show();

        RefreshAll();
        if (!_demoMode) ScrollToEnd();
    }

    /// <summary>隐藏聊天窗（不销毁，下次打开很快）；隐藏后立刻退订服务事件。</summary>
    public void HideChat()
    {
        UnhookChatEvents();
        if (IsVisible) Hide();
    }

    /// <summary>
    /// 把聊天窗摆到桌宠下方 / 左侧 / 右侧（会话层每帧调一次，跟着桌宠拖动 / 缩放 / 换屏）。
    /// <para>定位规则见 <see cref="AiChatPlacement.Calculate"/>；这里只负责取桌宠矩形 + 屏幕工作区。</para>
    /// </summary>
    public void PlaceBelow(Window? pet)
    {
        if (pet is null) return;

        // 窗口刚 Show 出来时 ActualWidth 可能还是 0，退回 XAML 里写的 Width / Height
        var width = ActualWidth > 1 ? ActualWidth : Width;
        var height = ActualHeight > 1 ? ActualHeight : Height;
        if (double.IsNaN(width) || width <= 1) width = DefaultWidth;
        if (double.IsNaN(height) || height <= 1) height = DefaultHeight;

        var petWidth = pet.ActualWidth > 1 ? pet.ActualWidth : pet.Width;
        var petHeight = pet.ActualHeight > 1 ? pet.ActualHeight : pet.Height;
        if (double.IsNaN(petWidth) || petWidth < 0) petWidth = 0;
        if (double.IsNaN(petHeight) || petHeight < 0) petHeight = 0;

        // 单位说明：Window.Left / Top / ActualWidth / ActualHeight 和 SystemParameters.WorkArea 都是 DIP，可直接算。
        // 多屏：这里用系统主屏的工作区兜底（不引 WinForms）；桌宠一般就在主屏，桌宠跑到副屏时
        // 聊天窗会被夹回主屏边缘（最小惊讶原则：宁可贴边也不跑到看不见的地方）。
        var area = SystemParameters.WorkArea;
        var (left, top) = AiChatPlacement.Calculate(
            pet.Left, pet.Top, petWidth, petHeight,
            width, height,
            area.Left, area.Top, area.Width, area.Height);

        if (double.IsNaN(left) || double.IsNaN(top)) return;
        if (Math.Abs(Left - left) > 0.5) Left = left;
        if (Math.Abs(Top - top) > 0.5) Top = top;
    }

    /// <summary>
    /// 截图冒烟专用：摆几条示例消息 + 把好感度显示在 78（开心），方便截图看气泡 / 试管 / 配色。
    /// <para><b>不写盘、不动真实聊天数据</b>：只是把消息列表的显示源换成示例集合、
    /// 直接改界面上的数字，<see cref="AiChatService.Messages"/> 与 <see cref="AiChatService.Affection"/> 都不碰。</para>
    /// </summary>
    public void LoadDemoMessages()
    {
        _demoMode = true;

        var now = DateTimeOffset.Now;
        _demoMessages.Clear();
        AddDemoMessage("notice", "已切到 DeepSeek · 思考关闭 · 单次 160 token（省着花）", now.AddMinutes(-7));
        AddDemoMessage("assistant", "主人今天回来得好晚…我一直趴在桌角等你呢，尾巴都等困了。", now.AddMinutes(-5));
        AddDemoMessage("user", "今天加班有点累，想听你说点好听的。", now.AddMinutes(-4));
        AddDemoMessage("assistant", "辛苦啦～快把手手给我，本鱼给你吹一吹。今晚不许再熬夜了，我盯着你哦。", now.AddMinutes(-3));

        // 换成示例集合（真实聊天记录留在 chat.Messages 里，一条没动）
        MessageItems.ItemsSource = _demoMessages;

        // 好感度只改「界面显示」：78 = 开心档，试管里的液体约 78% 高
        var demoValue = 78.0;
        Vial.Value = demoValue;
        Vial.Mood = AffectionEngine.ResolveMood(demoValue);
        AffectionValueText.Text = demoValue.ToString("0.0", CultureInfo.InvariantCulture);
        MoodTextBlock.Text = AffectionEngine.MoodDisplayName(Vial.Mood);
        TitleStatusText.Text = "示例 · " + MoodTextBlock.Text;
        StatusTextBlock.Text = "示例数据（截图冒烟用，不落盘）";
        TokenTextBlock.Text = "今日约 1,280 token";

        UpdateEmptyHint();
        LoadConfigToggles();
        ScrollToEnd();
    }

    // ==================== 窗口内部：位置 / 勾选框 / 界面刷新 ====================

    /// <summary>第一次显示前的落点：主屏右下角（之后会话层会按桌宠位置覆盖）。</summary>
    private void MoveToDefaultCorner()
    {
        try
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left + 8, area.Right - DefaultWidth - 24);
            Top = Math.Max(area.Top + 8, area.Bottom - DefaultHeight - 24);
        }
        catch
        {
            // 拿不到屏幕信息（极端情况）：交给 WPF 默认位置
        }
    }

    /// <summary>按配置回填三个勾选框（回填期间不反向写配置）。</summary>
    private void LoadConfigToggles()
    {
        _syncingToggles = true;
        try
        {
            ProactiveCheck.IsChecked = _config.AiProactiveEnabled;
            NaturalCheck.IsChecked = _config.AiAffectionNatural;
            VoiceCheck.IsChecked = _config.AiSpeakReply;
        }
        finally
        {
            _syncingToggles = false;
        }
    }

    /// <summary>把服务里的状态刷到界面：好感度 / 情绪 / 状态行 / 今日 token / 「正在输入…」。</summary>
    private void RefreshAll()
    {
        // 示例数据模式：保持 LoadDemoMessages() 摆好的展示，别被真实聊天状态盖掉
        if (_demoMode)
        {
            UpdateEmptyHint();
            RefreshTypingIndicator();
            return;
        }

        try
        {
            var value = AffectionVial.ClampValue(_chat.AffectionValue);
            Vial.Value = value;
            Vial.Mood = _chat.Affection.Mood;
            AffectionValueText.Text = value.ToString("0.0", CultureInfo.InvariantCulture);

            var moodText = _chat.MoodText;
            MoodTextBlock.Text = string.IsNullOrWhiteSpace(moodText) ? "平常" : moodText;

            var status = _chat.StatusText;
            if (string.IsNullOrWhiteSpace(status))
            {
                status = _chat.HasUsableApiKey
                    ? "就绪"
                    : "离线兜底：没填 API Key（点右上角 ⚙ 打开设置卡）";
            }
            StatusTextBlock.Text = status;

            TokenTextBlock.Text = $"今日约 {_chat.TodayTokens:N0} token";
            TitleStatusText.Text = (_chat.HasUsableApiKey ? "在线 · " : "离线 · ") + MoodTextBlock.Text;

            // 勾选框可能刚在工具页里改过，跟着刷新（回填期间不会反向写配置）
            LoadConfigToggles();

            // 兜底：即使服务层没为 IsBusy 发 PropertyChanged，也强制让绑定重新取值，
            // 「正在输入…」不会因为一次漏发就卡在错误状态。
            RefreshTypingIndicator();
            UpdateEmptyHint();
        }
        catch
        {
            // 服务正在构造 / 释放（或配置还没准备好）：保持上一次界面，后台异常不把窗口带崩
        }
    }

    /// <summary>强制让「正在输入…」的绑定重新取值（不清掉绑定，只刷新一次)。</summary>
    private void RefreshTypingIndicator()
    {
        try
        {
            TypingRow.GetBindingExpression(VisibilityProperty)?.UpdateTarget();
        }
        catch
        {
            // 绑定还没建立起来：下一轮刷新再说
        }
    }

    /// <summary>没消息时显示空状态提示。</summary>
    private void UpdateEmptyHint()
    {
        try
        {
            EmptyHint.Visibility = MessageItems.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== 服务事件（都在 Dispatcher 上处理） ====================

    private void HookChatEvents()
    {
        if (_chatEventsHooked) return;
        _chat.MessageAdded += OnChatMessageAdded;
        _chat.StateChanged += OnChatStateChanged;
        _chatEventsHooked = true;
    }

    private void UnhookChatEvents()
    {
        if (!_chatEventsHooked) return;
        _chat.MessageAdded -= OnChatMessageAdded;
        _chat.StateChanged -= OnChatStateChanged;
        _chatEventsHooked = false;
    }

    /// <summary>新消息加入：滚到底（等布局跑完再滚，否则滚的是旧高度）。</summary>
    private void OnChatMessageAdded(object? sender, AiChatMessage e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => OnChatMessageAdded(sender, e)));
            return;
        }

        UpdateEmptyHint();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToEndNow));
    }

    /// <summary>状态变化（忙碌 / 状态行 / 好感度 / token）：整屏刷新一次。</summary>
    private void OnChatStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => OnChatStateChanged(sender, e)));
            return;
        }

        RefreshAll();
    }

    /// <summary>窗口真正被关闭（会话层退出时 Close）：退订事件，别留着引用。</summary>
    private void OnWindowClosed(object? sender, EventArgs e)
    {
        UnhookChatEvents();
    }

    // ==================== 交互 ====================

    /// <summary>标题条按住拖动（按钮上的按下不算拖动，交给按钮自己处理）。</summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;

        try
        {
            DragMove();
        }
        catch
        {
            // 鼠标在 DragMove 前已经松开之类的极端情况：忽略
        }
    }

    /// <summary>⚙：打开工具页里的「AI 聊天 · 好感度」设置卡。</summary>
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _openSettings?.Invoke();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "打开设置失败：" + ex.Message;
        }
    }

    /// <summary>✕：只隐藏 + 记住「关着」，不销毁窗口、不退出工具。</summary>
    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _config.AiChatWindowOpen = false;
            _onConfigChanged?.Invoke();
        }
        catch
        {
            // 保存失败不影响隐藏
        }

        HideChat();
    }

    /// <summary>三个常用开关（主动聊天 / 自然好感度 / 语音）：写配置 → 回调保存 + 同步工具页。</summary>
    private void OnConfigToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;

        try
        {
            var proactive = ProactiveCheck.IsChecked == true;
            var natural = NaturalCheck.IsChecked == true;
            var voice = VoiceCheck.IsChecked == true;

            var changed = false;
            if (_config.AiProactiveEnabled != proactive)
            {
                _config.AiProactiveEnabled = proactive;
                changed = true;
            }
            if (_config.AiAffectionNatural != natural)
            {
                _config.AiAffectionNatural = natural;
                changed = true;
            }
            if (_config.AiSpeakReply != voice)
            {
                _config.AiSpeakReply = voice;
                changed = true;
            }
            if (!changed) return;

            // 会话层负责立刻保存配置 + ApplyAiSettings + 刷新工具页绑定
            _onConfigChanged?.Invoke();

            // 同时让聊天服务重读设置（自然模式关掉时好感度会变成纯手动，数值展示跟着变）
            try
            {
                _chat.ApplySettings();
            }
            catch
            {
                // 服务还没初始化好：下一轮 StateChanged 会再刷新
            }

            RefreshAll();
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "设置没保存上：" + ex.Message;
        }
    }

    /// <summary>快捷短语：直接发对应文本（走同一个发送方法）。</summary>
    private void OnQuickPhraseClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string phrase }) SendText(phrase);
    }

    /// <summary>点发送按钮。</summary>
    private void OnSendClick(object sender, RoutedEventArgs e) => SendFromInput();

    /// <summary>输入框按键：Enter 发送、Shift+Enter 换行（换行交给 TextBox 默认行为）。</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        SendFromInput();
    }

    /// <summary>发输入框里的内容，发完清空输入框（失败也不把用户打好的字弄丢？——发送时已经取走，失败写系统提示）。</summary>
    private void SendFromInput()
    {
        var text = InputBox.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        InputBox.Text = "";
        SendText(text);
    }

    /// <summary>
    /// 发一句话给聊天服务（async void 包 try/catch：任何异常都只写一条系统提示，绝不把窗口带崩）。
    /// </summary>
    private async void SendText(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;

        if (_chat.IsBusy)
        {
            StatusTextBlock.Text = "上一条还在等回复，稍等一下～";
            return;
        }

        try
        {
            // 从示例数据切回真实聊天（截图冒烟之后继续聊也能用）
            _demoMode = false;
            if (!ReferenceEquals(MessageItems.ItemsSource, _chat.Messages))
            {
                MessageItems.ItemsSource = _chat.Messages;
            }

            var reply = await _chat.SendUserMessageAsync(text);
            if (reply is { IsFallback: true, Error: { Length: > 0 } error })
            {
                // 服务层已经加了一条系统消息说明原因，这里只补状态行，不重复刷屏
                StatusTextBlock.Text = "离线兜底：" + error;
            }
        }
        catch (Exception ex)
        {
            AppendNotice("发送失败：" + ex.Message);
        }
        finally
        {
            RefreshAll();
            ScrollToEnd();
        }
    }

    /// <summary>往聊天记录里补一条「系统提示」（居中灰字）；集合不可用时退化成状态行文案。</summary>
    private void AppendNotice(string text)
    {
        if (!string.IsNullOrWhiteSpace(text) && !_demoMode)
        {
            try
            {
                _chat.Messages.Add(new AiChatMessage
                {
                    Role = "notice",
                    Text = text,
                    TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
                });
                UpdateEmptyHint();
                ScrollToEnd();
                return;
            }
            catch
            {
                // 服务已释放 / 集合不可写：退化成状态行
            }
        }

        StatusTextBlock.Text = text;
    }

    // ==================== 小工具 ====================

    /// <summary>示例消息（截图冒烟用；不改真实聊天数据）。</summary>
    private void AddDemoMessage(string role, string text, DateTimeOffset time)
    {
        _demoMessages.Add(new AiChatMessage
        {
            Role = role,
            Text = text,
            TimestampUnix = time.ToUnixTimeSeconds(),
        });
    }

    /// <summary>滚到消息列表最底部（等布局更新完再滚）。</summary>
    private void ScrollToEnd()
    {
        Ui(ScrollToEndNow);
    }

    private void ScrollToEndNow()
    {
        try
        {
            MessageScroll.UpdateLayout();
            MessageScroll.ScrollToEnd();
        }
        catch
        {
            // 列表还没生成可视项（刚显示 / 正在销毁）：忽略
        }
    }

    /// <summary>把动作切到窗口的 Dispatcher 上执行（服务事件可能来自别的线程）。</summary>
    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(action));
    }

    /// <summary>鼠标按在按钮上时不要触发窗口拖动（按钮自己会处理按下）。</summary>
    private static bool IsInsideButton(DependencyObject? node)
    {
        for (var depth = 0; depth < 8 && node is not null; depth++)
        {
            if (node is ButtonBase) return true;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : null;
        }
        return false;
    }
}
