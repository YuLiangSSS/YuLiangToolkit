using System;
using System.Collections.Generic;
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
using YuLiangToolkit.Tools.Live2D.Play;

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
///
/// <para><b>养成 HUD</b>：好感度下面多一条「🍙 饱食 72%（正常）· 🪙 35 · 🍙 投喂 / 📋 任务」；
/// 点「投喂」会在消息区上方盖一个半透明深色抽屉（4 行饭团商店），点「任务」显示 3 行每日任务。
/// 数据来自 <see cref="PetCareService"/>（care 为 null / <c>PetCareEnabled=false</c> 时整条隐藏）；
/// 点「买一个 / 扔出去」都走会话给的 <c>spawnRiceBall</c> 回调（内部处理买 / 用库存 / 丢到屏幕上）。</para>
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

    /// <summary>养成服务（null = 没接养成 → HUD / 投喂入口整条隐藏）。</summary>
    private readonly PetCareService? _care;

    /// <summary>会话给了「丢一颗饭团到屏幕上」的回调（内部处理买 / 用库存；成功返回 true）。</summary>
    private readonly Func<RiceBallKind, bool>? _spawnRiceBall;

    /// <summary>养成服务事件是否已挂上（窗口隐藏时退订，显示时重订）。</summary>
    private bool _careEventsHooked;

    /// <summary>抽屉当前是不是「任务」模式（false = 投喂模式）。</summary>
    private bool _careDrawerShowsTasks;

    /// <summary>投喂行的库存文本（按饭团种类，建一次行后复用）。</summary>
    private readonly Dictionary<RiceBallKind, TextBlock> _feedInventoryTexts = new();

    /// <summary>任务行的勾选 / 进度文本（按任务种类）。</summary>
    private readonly Dictionary<DailyTaskKind, TaskRowUi> _taskRowUis = new();

    // 抽屉是固定深色半透明面板，文字 / 按钮统一用这套浅色画刷（跟主题走反而在深色面板上看不清）。
    private static readonly Brush CareStrongTextBrush = FrozenBrush(Color.FromRgb(0xF2, 0xF5, 0xF8));
    private static readonly Brush CareDimTextBrush = FrozenBrush(Color.FromRgb(0xB9, 0xC2, 0xCE));
    private static readonly Brush CareFaintTextBrush = FrozenBrush(Color.FromRgb(0x8F, 0x9A, 0xA8));
    private static readonly Brush CareGoldBrush = FrozenBrush(Color.FromRgb(0xFF, 0xD9, 0x8A));
    private static readonly Brush CareDoneBrush = FrozenBrush(Color.FromRgb(0x8B, 0xE2, 0x8B));
    private static readonly Brush CareWarnBrush = FrozenBrush(Color.FromRgb(0xFF, 0xC9, 0x7A));
    private static readonly Brush CareRowBackgroundBrush = FrozenBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CareRowBorderBrush = FrozenBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));

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
    /// <param name="care">养成服务（可空：null = 不显示养成 HUD / 投喂入口）。</param>
    /// <param name="spawnRiceBall">会话的「丢一颗饭团到屏幕上」回调（内部处理买 / 库存；可空）。</param>
    /// <param name="onConfigChanged">聊天窗里改了配置后回调（保存 + 同步工具页）。</param>
    /// <param name="openSettings">点标题栏 ⚙ / 抽屉里「⚙ 养成设置」时回调（打开工具页设置）。</param>
    public AiChatWindow(
        AiChatService chat,
        Live2DConfig config,
        PetCareService? care = null,
        Func<RiceBallKind, bool>? spawnRiceBall = null,
        Action? onConfigChanged = null,
        Action? openSettings = null)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _care = care;
        _spawnRiceBall = spawnRiceBall;
        _onConfigChanged = onConfigChanged;
        _openSettings = openSettings;

        InitializeComponent();

        WindowStartupLocation = WindowStartupLocation.Manual;

        // 「正在输入…」直接绑 chat.IsBusy（服务层发 PropertyChanged 时 WPF 自己刷新）
        DataContext = _chat;

        // 消息列表先接上真实聊天记录；截图冒烟时再换成示例集合
        MessageItems.ItemsSource = _chat.Messages;

        // 抽屉的 4 行饭团 / 3 行任务按目录建一次（之后各刷新文本；care 为 null 时不建）
        BuildCareDrawerRows();

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

    /// <summary>隐藏聊天窗（不销毁，下次打开很快）；隐藏后立刻退订服务事件、收起养成抽屉。</summary>
    public void HideChat()
    {
        UnhookChatEvents();
        CloseCareDrawer();
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
        RefreshCareHud();   // 示例模式：HUD 显示真实养成数值（只展示，不改数据）
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
            RefreshCareHud();
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
            RefreshCareHud();
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
        if (!_chatEventsHooked)
        {
            _chat.MessageAdded += OnChatMessageAdded;
            _chat.StateChanged += OnChatStateChanged;
            _chatEventsHooked = true;
        }

        // 养成服务同样只在显示期间订阅（隐藏时退订，不白白刷新界面）
        if (_care is not null && !_careEventsHooked)
        {
            _care.StateChanged += OnCareStateChanged;
            _careEventsHooked = true;
        }
    }

    private void UnhookChatEvents()
    {
        if (_chatEventsHooked)
        {
            _chat.MessageAdded -= OnChatMessageAdded;
            _chat.StateChanged -= OnChatStateChanged;
            _chatEventsHooked = false;
        }

        if (_care is not null && _careEventsHooked)
        {
            _care.StateChanged -= OnCareStateChanged;
            _careEventsHooked = false;
        }
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

    /// <summary>养成状态变化（饱食度 / 金币 / 库存 / 任务）：切到 UI 线程刷新 HUD 与抽屉。</summary>
    private void OnCareStateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => OnCareStateChanged(sender, e)));
            return;
        }

        RefreshCareHud();
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

    // ==================== 养成 HUD / 投喂抽屉 ====================

    /// <summary>养成 HUD 是否该显示（没接养成服务 / 配置里关了养成 → 隐藏）。</summary>
    private bool IsCareHudAvailable => _care is not null && _config.PetCareEnabled;

    /// <summary>
    /// 刷新养成 HUD（饱食 / 金币 / 按钮）与（打开时）投喂抽屉。
    /// <para>公开：会话在改完 <c>PetCareEnabled</c> / ApplyCareSettings 之后可以主动调一次立刻生效。</para>
    /// </summary>
    public void RefreshCareHud()
    {
        try
        {
            if (!IsCareHudAvailable)
            {
                CareHudPanel.Visibility = Visibility.Collapsed;
                CloseCareDrawer();
                return;
            }

            var care = _care!;
            CareHudPanel.Visibility = Visibility.Visible;
            CareFullnessText.Text = $"🍙 饱食 {care.Fullness:0}%（{care.TierText}）";
            CareCoinsText.Text = $"🪙 {care.Coins}";
            FeedDrawerButton.ToolTip = $"买 / 扔大白饭团喂桌宠（库存共 {care.TotalInventory} 个）";

            if (CareDrawer.Visibility == Visibility.Visible) RefreshCareDrawer();
        }
        catch
        {
            // 服务正在构造 / 释放：保持上一次界面，后台异常不把窗口带崩
        }
    }

    /// <summary>刷新抽屉：顶部金币 / 饱食 / 库存总览、4 行饭团库存、3 行任务进度（打开时才有意义）。</summary>
    private void RefreshCareDrawer()
    {
        if (_care is null) return;
        var care = _care;

        CareDrawerSummaryText.Text =
            $"🪙 {care.Coins} · 🍙 饱食 {care.Fullness:0}%（{care.TierText}）· 库存共 {care.TotalInventory} 个";

        foreach (var pair in _feedInventoryTexts)
        {
            pair.Value.Text = $"库存 {care.GetInventory(pair.Key)}";
        }

        foreach (var pair in _taskRowUis)
        {
            var definition = DailyTaskCatalog.Get(pair.Key);
            var progress = Math.Min(care.GetTaskProgress(pair.Key), definition.TargetCount);
            var done = care.IsTaskRewarded(pair.Key) || progress >= definition.TargetCount;
            pair.Value.Mark.Text = done ? "✓" : "○";
            pair.Value.Mark.Foreground = done ? CareDoneBrush : CareFaintTextBrush;
            pair.Value.Progress.Text = $"{progress}/{definition.TargetCount}";
            pair.Value.Progress.Foreground = done ? CareDoneBrush : CareDimTextBrush;
        }

        CareTaskSummaryText.Text =
            $"今日任务 {care.CompletedTaskCount}/{DailyTaskCatalog.Count} 已完成 · 全部完成共 +{DailyTaskCatalog.TotalReward} 金币";
    }

    /// <summary>打开抽屉（<paramref name="showTasks"/> = true 显示任务，false 显示饭团商店）。</summary>
    private void OpenCareDrawer(bool showTasks)
    {
        if (!IsCareHudAvailable) return;

        _careDrawerShowsTasks = showTasks;
        CareDrawerTitle.Text = showTasks ? "📋 今日任务" : "🍙 投喂 · 大白饭团";
        CareFeedSection.Visibility = showTasks ? Visibility.Collapsed : Visibility.Visible;
        CareTaskSection.Visibility = showTasks ? Visibility.Visible : Visibility.Collapsed;
        CareDrawer.Visibility = Visibility.Visible;
        ShowDrawerHint("");
        RefreshCareDrawer();
    }

    /// <summary>收起抽屉（隐藏窗口 / 投喂成功后都会调）。</summary>
    private void CloseCareDrawer()
    {
        CareDrawer.Visibility = Visibility.Collapsed;
        ShowDrawerHint("");
    }

    /// <summary>
    /// 截图冒烟用：不点按钮，直接把养成抽屉打开（<paramref name="showTasks"/> = true 显示 3 行任务，
    /// false 显示 4 行饭团商店）。会话 / 截图脚本在 <c>ShowChat()</c> 之后调一次即可截图。
    /// </summary>
    public void OpenCareDrawerForDemo(bool showTasks = false) => OpenCareDrawer(showTasks);

    /// <summary>HUD「🍙 投喂」：打开 / 收起投喂抽屉。</summary>
    private void OnFeedDrawerClick(object sender, RoutedEventArgs e)
    {
        if (CareDrawer.Visibility == Visibility.Visible && !_careDrawerShowsTasks) CloseCareDrawer();
        else OpenCareDrawer(showTasks: false);
    }

    /// <summary>HUD「📋 任务」：打开 / 收起今日任务抽屉。</summary>
    private void OnTaskDrawerClick(object sender, RoutedEventArgs e)
    {
        if (CareDrawer.Visibility == Visibility.Visible && _careDrawerShowsTasks) CloseCareDrawer();
        else OpenCareDrawer(showTasks: true);
    }

    /// <summary>抽屉右上角 ✕：收起。</summary>
    private void OnCareDrawerCloseClick(object sender, RoutedEventArgs e) => CloseCareDrawer();

    /// <summary>抽屉底部「⚙ 养成设置」：打开工具页里的养成设置卡。</summary>
    private void OnCareSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _openSettings?.Invoke();
        }
        catch (Exception ex)
        {
            ShowDrawerHint("打开养成设置失败：" + ex.Message, warning: true);
        }
    }

    /// <summary>饭团行的「买一个 / 扔出去」：都走 spawnRiceBall（内部自己决定买还是用库存）。</summary>
    private void OnFeedActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RiceBallKind kind }) return;
        TrySpawnRiceBall(kind);
    }

    /// <summary>让会话丢一颗饭团到屏幕上；失败在抽屉里给一句提示（不弹窗、不打断聊天）。</summary>
    private void TrySpawnRiceBall(RiceBallKind kind)
    {
        var food = RiceBallCatalog.Get(kind);
        if (_spawnRiceBall is null)
        {
            ShowDrawerHint("投喂还没接上：会话没有提供生成饭团的回调。", warning: true);
            return;
        }

        bool success;
        try
        {
            success = _spawnRiceBall(kind);
        }
        catch (Exception ex)
        {
            ShowDrawerHint("投喂失败：" + ex.Message, warning: true);
            return;
        }

        if (success)
        {
            CloseCareDrawer();
            StatusTextBlock.Text = $"已把「{food.Name}」丢到屏幕上：拖到桌宠嘴边喂它～";
            RefreshCareHud();
            return;
        }

        ShowDrawerHint(
            $"投喂「{food.Name}」失败：金币不够（{food.Price} 金币）或者没有库存，先点「买一个」试试。",
            warning: true);
    }

    /// <summary>抽屉提示文本（空串 = 隐藏）。</summary>
    private void ShowDrawerHint(string text, bool warning = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            CareDrawerHintText.Text = "";
            CareDrawerHintText.Visibility = Visibility.Collapsed;
            return;
        }

        CareDrawerHintText.Text = text;
        CareDrawerHintText.Foreground = warning ? CareWarnBrush : CareDimTextBrush;
        CareDrawerHintText.Visibility = Visibility.Visible;
    }

    /// <summary>按目录建抽屉内容：4 行饭团 + 3 行每日任务（只建一次，之后各刷新文本）。</summary>
    private void BuildCareDrawerRows()
    {
        if (_care is null) return;

        foreach (var food in RiceBallCatalog.Foods)
        {
            CareFeedRowsPanel.Children.Add(BuildFeedRow(food));
        }

        foreach (var task in DailyTaskCatalog.Tasks)
        {
            CareTaskRowsPanel.Children.Add(BuildTaskRow(task));
        }
    }

    /// <summary>一行饭团商店：Glyph + 名字 / 价格·饱食·好感 + 库存 +「买一个」「扔出去」。</summary>
    private Border BuildFeedRow(RiceBallFood food)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // Glyph
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 名字 / 数值
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // 库存
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // 按钮

        var glyph = new TextBlock
        {
            Text = food.Glyph,
            FontSize = 17,
            Width = 24,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = CareStrongTextBrush,
        };
        grid.Children.Add(glyph);

        var textPanel = new StackPanel { Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = food.Name,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = CareStrongTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = $"价格 {food.Price} 金币 · 饱食 +{food.FullnessGain:0} · 好感 +{food.AffectionGain:0}",
            FontSize = 9,
            Foreground = CareDimTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        var inventory = new TextBlock
        {
            Text = $"库存 {_care?.GetInventory(food.Kind) ?? 0}",
            FontSize = 9.5,
            Margin = new Thickness(4, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CareGoldBrush,
        };
        Grid.SetColumn(inventory, 2);
        grid.Children.Add(inventory);
        _feedInventoryTexts[food.Kind] = inventory;

        var buttons = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        buttons.Children.Add(CreateFeedButton("买一个", food.Kind));
        var throwButton = CreateFeedButton("扔出去", food.Kind);
        throwButton.Margin = new Thickness(0, 3, 0, 0);
        buttons.Children.Add(throwButton);
        Grid.SetColumn(buttons, 3);
        grid.Children.Add(buttons);

        return new Border
        {
            Background = CareRowBackgroundBrush,
            BorderBrush = CareRowBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 5),
            Child = grid,
        };
    }

    /// <summary>一行每日任务：✓/○ + 标题·说明 + 进度 x/y + 奖励金币。</summary>
    private Border BuildTaskRow(DailyTaskDefinition task)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // ✓
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });  // 标题 / 说明
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // 进度
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                         // 奖励

        var mark = new TextBlock
        {
            Text = "○",
            FontSize = 13,
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Foreground = CareFaintTextBrush,
        };
        grid.Children.Add(mark);

        var textPanel = new StackPanel { Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        textPanel.Children.Add(new TextBlock
        {
            Text = task.Title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = CareStrongTextBrush,
        });
        textPanel.Children.Add(new TextBlock
        {
            Text = task.Description,
            FontSize = 9,
            Foreground = CareDimTextBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(textPanel, 1);
        grid.Children.Add(textPanel);

        var progress = new TextBlock
        {
            Text = $"0/{task.TargetCount}",
            FontSize = 10,
            Margin = new Thickness(4, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CareDimTextBrush,
        };
        Grid.SetColumn(progress, 2);
        grid.Children.Add(progress);

        var reward = new TextBlock
        {
            Text = $"+{task.RewardCoins} 金币",
            FontSize = 9.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = CareGoldBrush,
        };
        Grid.SetColumn(reward, 3);
        grid.Children.Add(reward);

        _taskRowUis[task.Kind] = new TaskRowUi { Mark = mark, Progress = progress };

        return new Border
        {
            Background = CareRowBackgroundBrush,
            BorderBrush = CareRowBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 4, 6, 4),
            Margin = new Thickness(0, 0, 0, 5),
            Child = grid,
        };
    }

    /// <summary>建抽屉里的小按钮（Tag = 饭团种类，点击统一走 OnFeedActionClick）。</summary>
    private Button CreateFeedButton(string text, RiceBallKind kind)
    {
        var button = new Button
        {
            Content = text,
            Tag = kind,
            Style = (Style)FindResource("CareDrawerButtonStyle"),
            Margin = new Thickness(0),
            Padding = new Thickness(7, 2, 7, 2),
            FontSize = 9.5,
        };
        button.Click += OnFeedActionClick;
        return button;
    }

    /// <summary>一行每日任务的界面引用（✓ 与进度文本；刷新时只改这两处）。</summary>
    private sealed class TaskRowUi
    {
        /// <summary>✓ / ○。</summary>
        public required TextBlock Mark { get; init; }

        /// <summary>x/y。</summary>
        public required TextBlock Progress { get; init; }
    }

    /// <summary>建一个冻结的纯色画刷（抽屉固定深色面板用）。</summary>
    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
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
