using System.Collections.ObjectModel;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Media;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.Stopwatch.ViewModels;

/// <summary>
/// 秒表页的 ViewModel：计时显示 + 按钮 + 计次 + 设置绑定 + 快捷键触发的副作用（通知 / 声音）。
///
/// <para><b>它常驻</b>：由 <see cref="StopwatchTool"/> 建一次，切工具页回来还是同一个实例，
/// 所以计时不会因为切页面而中断。刷新用的 <see cref="DispatcherTimer"/> 在页面不可见时自动降频
/// （见 <see cref="OnPageUnloaded"/>），但计时本身照常走（按 <see cref="StopwatchSession"/> 的时间戳算）。</para>
///
/// <para><b>通知与声音</b>：只在「来自快捷键」时触发（<c>*FromShortcut</c> 方法）。
/// 鼠标点按钮时界面本身就在变，再弹通知会吵；这也是用户的原话「软件在后台按快捷键才弹」。</para>
/// </summary>
public sealed class StopwatchViewModel : ToolViewModelBase
{
    /// <summary>计时中时刷新间隔（毫秒）。10ms ≈ 百分秒位看起来是连续走的。</summary>
    private const int RunningIntervalMs = 10;

    /// <summary>停着 / 暂停时的刷新间隔（毫秒）。这时显示不会变，省点 CPU。</summary>
    private const int IdleIntervalMs = 500;

    private readonly StopwatchTool _tool;
    private readonly StopwatchSession _session = new();

    /// <summary>
    /// 单调时钟：秒表的计时只认它（不受系统时间被改的影响）。
    /// <para>⚠ 这里必须写全名：本项目自己的命名空间也叫 <c>Stopwatch</c>，直接写
    /// <c>Stopwatch</c> 会被解析成命名空间（CS0118）。</para>
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly SoundPlayer _sound = new();

    private bool _pageVisible;
    private bool _isForeground;   // 主窗口是否在前台且正在看秒表页

    public StopwatchViewModel(StopwatchTool tool) : base(StopwatchConfig.ToolId)
    {
        _tool = tool;
        Cfg = LoadConfig<StopwatchConfig>();

        StartCommand = new RelayCommand(_ => Start(), _ => !_session.IsRunning);
        PauseCommand = new RelayCommand(_ => Pause(), _ => _session.IsRunning);
        StopCommand = new RelayCommand(_ => Stop(), _ => !_session.IsIdle);
        LapCommand = new RelayCommand(_ => Lap(), _ => !_session.IsIdle);
        ResetLapsCommand = new RelayCommand(_ => ClearLaps(), _ => _session.Laps.Count > 0);
        ClearShortcutCommand = new RelayCommand(p => ClearShortcut(p as string), p => p is string);
        PlaySoundTestCommand = new RelayCommand(p => TestSound(p as string), p => p is string);
        SendTestNotificationCommand = new RelayCommand(_ => SendTestNotification());
        BrowseSoundCommand = new RelayCommand(p => BrowseSound(p as string), p => p is string);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(IdleIntervalMs) };
        _timer.Tick += (_, _) => Refresh();

        Cfg.PropertyChanged += (_, _) => OnConfigChanged();
        Cfg.Notification.PropertyChanged += (_, _) => OnPropertyChanged(nameof(NotificationSummary));

        RefreshSoundOptions();
        Refresh();
        SyncShortcuts();   // 把启用的快捷键注册上（程序启动时也走这里）
        HookSequenceFeedback();   // 分步快捷键：第一步识别 / 按错 的提示

        Log("快捷键：" + string.Join(" · ", _tool.GetShortcuts().Select(s => $"{s.Name}={s.DisplayText}")));
        Log($"配置文件：{Cfg.FilePath}");
    }

    /// <summary>
    /// 订阅引擎的分步反馈事件：按完第一步 → 弹「已识别第一步，等第二步」；
    /// 第二步按错（那一下已被吞掉）→ 弹「未被识别的快捷键：…」。
    ///
    /// <para>为什么由工具来弹：提示的角落 / 通道属于「是哪个工具在等第二步」，
    /// 应该用那个工具自己的通知设置。引擎只管识别。
    /// 这里用 <see cref="NotificationKind.Info"/> / <see cref="NotificationKind.Warning"/> 两种类型区分。</para>
    /// </summary>
    private void HookSequenceFeedback()
    {
        var engine = ShortcutHub.Engine;
        if (engine is null) return;   // 自检 / 没有外壳：不订阅

        engine.SequenceStarted += OnSequenceStarted;
        engine.SequenceRejected += OnSequenceRejected;
    }

    private void OnSequenceStarted(object? sender, KeyCombo combo)
    {
        if (_isForeground) return;   // 正在看秒表页：界面自己会变，不打扰

        NotificationCenter.Show(Cfg.Notification, new NotificationRequest(
            $"已识别 {combo}",
            "请按第二步（再按一次第一步可取消）",
            NotificationKind.Info,
            StopwatchConfig.ToolId));
    }

    private void OnSequenceRejected(object? sender, SequenceRejectedEventArgs e)
    {
        // 「按错」必须提示：那一下已经被引擎吞掉了，不提示用户会以为键盘失灵。
        // 不看 _isForeground —— 正在看秒表页也需要知道「刚才那下没被认出来」。
        NotificationCenter.Show(Cfg.Notification, new NotificationRequest(
            "未被识别的快捷键",
            e.Describe(),
            NotificationKind.Warning,
            StopwatchConfig.ToolId));

        StatusText = e.Describe();
    }

    /// <summary>强类型配置。</summary>
    public StopwatchConfig Cfg { get; }

    // ---------- 命令 ----------

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand LapCommand { get; }
    public RelayCommand ResetLapsCommand { get; }
    public RelayCommand ClearShortcutCommand { get; }
    public RelayCommand PlaySoundTestCommand { get; }
    public RelayCommand SendTestNotificationCommand { get; }
    public RelayCommand BrowseSoundCommand { get; }

    // ---------- 显示 ----------

    private string _elapsedText = "00:00.00";

    /// <summary>大号计时常量（<c>mm:ss.cc</c> / <c>h:mm:ss.cc</c>）。</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    private string _stateText = "已停止";

    /// <summary>状态：计时中 / 已暂停 / 已停止。</summary>
    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    /// <summary>正在计时（界面据此高亮按钮）。</summary>
    public bool IsRunning => _session.IsRunning;

    /// <summary>暂停中。</summary>
    public bool IsPaused => _session.IsPaused;

    /// <summary>停着。</summary>
    public bool IsIdle => _session.IsIdle;

    /// <summary>当前状态圆点的颜色（计时中绿 / 暂停橙 / 停止灰）。</summary>
    public string StateColorKey => _session.State switch
    {
        StopwatchState.Running => "SuccessBrush",
        StopwatchState.Paused => "WarningBrush",
        _ => "FaintTextBrush",
    };

    /// <summary>计次列表。</summary>
    public ObservableCollection<StopwatchLapRow> Laps { get; } = new();

    /// <summary>有没有计次（空状态提示用）。</summary>
    public bool HasLaps => Laps.Count > 0;

    /// <summary>快捷键状态说明（「Ctrl+Alt+F9 开始 · F10 暂停 · F11 停止」）。</summary>
    public string ShortcutSummary
    {
        get
        {
            var parts = _tool.GetShortcuts()
                .Where(s => s.Enabled && !s.Primary.IsEmpty)
                .Select(s => $"{s.DisplayText} {s.Name}");
            var text = string.Join(" · ", parts);
            if (!Cfg.ShortcutsEnabled) return "快捷键已全部关闭（在主页设置 → 快捷键里打开）";
            return text.Length > 0 ? text : "还没设置快捷键";
        }
    }

    /// <summary>通知设置摘要。</summary>
    public string NotificationSummary => Cfg.Notification.SummaryText;

    /// <summary>声音选项（配置里存 Id）。</summary>
    public ObservableCollection<SoundChoice> SoundOptions { get; } = new();

    private SoundChoice? _selectedStartSound;
    private SoundChoice? _selectedPauseSound;
    private SoundChoice? _selectedStopSound;

    /// <summary>下拉框选中项桥接：开始音。</summary>
    public SoundChoice? SelectedStartSound
    {
        get => _selectedStartSound ??= FindSound(Cfg.StartSound);
        set
        {
            if (value is null || ReferenceEquals(_selectedStartSound, value)) return;
            _selectedStartSound = value;
            Cfg.StartSound = value.Id;
        }
    }

    /// <summary>下拉框选中项桥接：暂停音。</summary>
    public SoundChoice? SelectedPauseSound
    {
        get => _selectedPauseSound ??= FindSound(Cfg.PauseSound);
        set
        {
            if (value is null || ReferenceEquals(_selectedPauseSound, value)) return;
            _selectedPauseSound = value;
            Cfg.PauseSound = value.Id;
        }
    }

    /// <summary>下拉框选中项桥接：停止音。</summary>
    public SoundChoice? SelectedStopSound
    {
        get => _selectedStopSound ??= FindSound(Cfg.StopSound);
        set
        {
            if (value is null || ReferenceEquals(_selectedStopSound, value)) return;
            _selectedStopSound = value;
            Cfg.StopSound = value.Id;
        }
    }

    /// <summary>按 Id 找选项（必须返回 <see cref="SoundOptions"/> 里的**同一个实例**，
    /// 否则 ComboBox 的 SelectedItem 与 ItemsSource 对不上，会显示成对象 ToString）。</summary>
    private SoundChoice? FindSound(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return SoundOptions.FirstOrDefault();
        return SoundOptions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? SoundOptions.FirstOrDefault();
    }

    // ================================================================== 生命周期

    /// <summary>页面显示出来（开始按运行状态刷显示）。</summary>
    public void OnPageLoaded()
    {
        _pageVisible = true;
        UpdateTimerInterval();
        Refresh();
    }

    /// <summary>页面切走（降频刷新；计时照常走，因为时间戳一直记着）。</summary>
    public void OnPageUnloaded()
    {
        _pageVisible = false;
        UpdateTimerInterval();
    }

    /// <summary>主窗口前台状态变化（决定按快捷键时要不要弹通知）。</summary>
    public void SetForeground(bool foreground) => _isForeground = foreground;

    /// <summary>工具被切走 / 关闭：停掉刷新计时器（热键与计时状态由工具持有，不受影响）。</summary>
    protected override void OnClosed()
    {
        _pageVisible = false;
        _timer.Stop();
    }

    /// <summary>释放声音播放器（由工具在退出时调）。</summary>
    public void ReleaseSounds() => _sound.Dispose();

    private void UpdateTimerInterval()
    {
        _timer.Stop();
        if (!_pageVisible) return;

        _timer.Interval = TimeSpan.FromMilliseconds(_session.IsRunning ? RunningIntervalMs : IdleIntervalMs);
        _timer.Start();
    }

    /// <summary>把配置里启用的快捷键注册到引擎（改键 / 启停 / 启动时都走它）。</summary>
    public void SyncShortcuts()
    {
        var engine = ShortcutHub.Engine;

        // ⚠ 工具构造期间 _viewModel 还没赋值、引擎也还没注入（自检 / 启动早期）：
        // 这时只把配置读好就行，不注册。
        if (engine is null) return;

        // 三条各自注册（按启用状态与是否设了按键）
        // ⚠ 这里只读 Cfg（本对象自己的配置），**不能**碰 _tool.Config：工具构造期间
        // _viewModel 还没赋值，_tool.Config 会 NRE（自检直接踩到）。
        RegisterOne(engine, StopwatchTool.ShortcutIdStart, Cfg.Start, Cfg.StartSecond, Cfg.StartEnabled && Cfg.ShortcutsEnabled);
        RegisterOne(engine, StopwatchTool.ShortcutIdPause, Cfg.Pause, Cfg.PauseSecond, Cfg.PauseEnabled && Cfg.ShortcutsEnabled);
        RegisterOne(engine, StopwatchTool.ShortcutIdStop, Cfg.Stop, Cfg.StopSecond, Cfg.StopEnabled && Cfg.ShortcutsEnabled);

        OnPropertyChanged(nameof(ShortcutSummary));
    }

    /// <summary>注册一条（引擎编号用固定偏移，保证「重注册」能覆盖旧的）。</summary>
    private void RegisterOne(GlobalShortcutEngine engine, string shortcutId, string primaryText,
        string secondaryText, bool enabled)
    {
        var id = StopwatchTool.ShortcutIdStart == shortcutId ? 0x5301
            : StopwatchTool.ShortcutIdPause == shortcutId ? 0x5302
            : 0x5303;

        if (!enabled || !KeyCombo.TryParse(primaryText, out var primary))
        {
            engine.Unregister(id);
            return;
        }

        KeyCombo? secondary = KeyCombo.TryParse(secondaryText, out var second) ? second : null;
        var kind = secondary is null ? ShortcutKind.Single : ShortcutKind.Sequence;

        var ok = engine.Register(id, kind, primary, secondary, _ => _tool.InvokeShortcut(shortcutId), out var error);
        if (!ok && !string.IsNullOrWhiteSpace(error))
        {
            Log($"快捷键「{shortcutId}」注册失败：{error}");
        }
    }

    private void OnConfigChanged()
    {
        RefreshSoundOptions();
        SyncShortcuts();
        OnPropertyChanged(nameof(NotificationSummary));
        OnPropertyChanged(nameof(ShortcutSummary));
        Refresh();
    }

    // ================================================================== 操作（按钮）

    /// <summary>开始 / 继续（按钮）。</summary>
    public void Start() => ApplyStart(notify: false);

    /// <summary>暂停（按钮）。</summary>
    public void Pause() => ApplyPause(notify: false);

    /// <summary>停止并归零（按钮）。</summary>
    public void Stop() => ApplyStop(notify: false);

    /// <summary>计次。</summary>
    public void Lap()
    {
        if (_session.Lap(Now()) is { } lap)
        {
            Laps.Insert(0, new StopwatchLapRow(lap.Index, StopwatchSession.Format(lap.Total, Cfg.ShowMilliseconds),
                StopwatchSession.Format(lap.Split, Cfg.ShowMilliseconds)));
            OnPropertyChanged(nameof(HasLaps));
            StatusText = $"已计次 #{lap.Index}";
            Refresh();
        }
    }

    /// <summary>清空计次。</summary>
    public void ClearLaps()
    {
        _session.ClearLaps();
        Laps.Clear();
        OnPropertyChanged(nameof(HasLaps));
        Refresh();
    }

    // ================================================================== 操作（快捷键）

    /// <summary>快捷键触发：开始。</summary>
    public void StartFromShortcut() => ApplyStart(notify: true);

    /// <summary>快捷键触发：暂停。</summary>
    public void PauseFromShortcut() => ApplyPause(notify: true);

    /// <summary>快捷键触发：停止。</summary>
    public void StopFromShortcut() => ApplyStop(notify: true);

    private void ApplyStart(bool notify)
    {
        if (!_session.Start(Now()))
        {
            // 已经在走：给一句提示，不然用户以为快捷键失灵了
            StatusText = "已经在计时中";
            return;
        }

        PlaySound(Cfg.StartSound);
        StatusText = "开始计时";
        Refresh();
        if (notify) NotifyShortcut("秒表已开始", "正在计时");
    }

    private void ApplyPause(bool notify)
    {
        if (!_session.Pause(Now()))
        {
            StatusText = _session.IsIdle ? "还没开始计时" : "已经是暂停状态";
            return;
        }

        PlaySound(Cfg.PauseSound);
        StatusText = "已暂停";
        Refresh();
        if (notify) NotifyShortcut("秒表已暂停", StopwatchSession.Format(_session.Elapsed, Cfg.ShowMilliseconds));
    }

    private void ApplyStop(bool notify)
    {
        var elapsed = _session.ElapsedAt(Now());
        var hadRun = !_session.IsIdle;

        if (!_session.Stop())
        {
            StatusText = "秒表已经是停止状态";
            return;
        }

        PlaySound(Cfg.StopSound);
        Laps.Clear();
        OnPropertyChanged(nameof(HasLaps));
        StatusText = "已停止并归零";
        Refresh();
        if (notify) NotifyShortcut("秒表已停止", hadRun ? "用时 " + StopwatchSession.Format(elapsed, Cfg.ShowMilliseconds) : "已归零");
    }

    /// <summary>按快捷键时的通知：只有「不在看这个页面」才弹（正在看的话界面本身就在变）。</summary>
    private void NotifyShortcut(string title, string body)
    {
        if (_isForeground) return;   // 前台且正在秒表页 → 不打扰

        NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest(title, body, NotificationKind.Info, StopwatchConfig.ToolId));
    }

    private void PlaySound(string? choiceId)
    {
        if (Cfg.Muted) return;
        SoundPlayer.TryPlay(choiceId);
    }

    // ================================================================== 刷新

    private TimeSpan Now() => _clock.Elapsed;

    /// <summary>按当前时间刷新显示。</summary>
    public void Refresh()
    {
        var elapsed = _session.ElapsedAt(Now());
        _session.Pulse(Now());

        ElapsedText = StopwatchSession.Format(elapsed, Cfg.ShowMilliseconds);
        StateText = _session.StateText;

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(StateColorKey));

        // 跑起来 / 停下来时刷新节奏要跟着变
        var wanted = _session.IsRunning ? RunningIntervalMs : IdleIntervalMs;
        if (_pageVisible && Math.Abs(_timer.Interval.TotalMilliseconds - wanted) > 1)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(wanted);
        }
    }

    /// <summary>通知设置摘要变了（设置块里改完调）。</summary>
    public void RefreshNotificationSummary() => OnPropertyChanged(nameof(NotificationSummary));

    // ================================================================== 设置辅助

    /// <summary>清空某条快捷键（<paramref name="which"/> = start / pause / stop）。</summary>
    public void ClearShortcut(string? which)
    {
        switch (which)
        {
            case StopwatchTool.ShortcutIdStart: Cfg.Start = ""; Cfg.StartSecond = ""; break;
            case StopwatchTool.ShortcutIdPause: Cfg.Pause = ""; Cfg.PauseSecond = ""; break;
            case StopwatchTool.ShortcutIdStop: Cfg.Stop = ""; Cfg.StopSecond = ""; break;
        }

        SyncShortcuts();
        StatusText = "已清空快捷键";
    }

    /// <summary>试听某个声音（<paramref name="which"/> = start / pause / stop）。</summary>
    public void TestSound(string? which)
    {
        var id = which switch
        {
            StopwatchTool.ShortcutIdStart => Cfg.StartSound,
            StopwatchTool.ShortcutIdPause => Cfg.PauseSound,
            _ => Cfg.StopSound,
        };

        if (!SoundPlayer.IsPlayable(id))
        {
            StatusText = "这个声音没法播放：" + SoundPlayer.Describe(id);
            return;
        }

        SoundPlayer.TryPlay(id);
        StatusText = "试听：" + SoundPlayer.Describe(id);
    }

    /// <summary>挑一个自定义 wav 文件作为某个动作的提示音。</summary>
    public void BrowseSound(string? which)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择提示音（只支持 .wav）",
            Filter = "Wave 音频 (*.wav)|*.wav",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        var value = SoundPlayer.FromFile(dialog.FileName);
        switch (which)
        {
            case StopwatchTool.ShortcutIdStart: Cfg.StartSound = value; break;
            case StopwatchTool.ShortcutIdPause: Cfg.PauseSound = value; break;
            case StopwatchTool.ShortcutIdStop: Cfg.StopSound = value; break;
        }

        RefreshSoundOptions();
        StatusText = "已选择提示音：" + SoundPlayer.Describe(value);
    }

    /// <summary>发一条测试通知（验证方位 / 通道设置）。</summary>
    public void SendTestNotification()
    {
        var ok = NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest("秒表 · 测试通知",
                $"通道：{(Cfg.Notification.Mode == NotificationMode.InApp ? "应用内" : "Windows")}"
                + $" · 位置：{NotificationSettings.CornerName(Cfg.Notification.Corner)}",
                NotificationKind.Success,
                StopwatchConfig.ToolId));

        StatusText = ok ? "已发送测试通知" : "通知没发出去（通道不可用）";
    }

    /// <summary>重建声音下拉框（含「当前选中的自定义文件」这一项）。</summary>
    private void RefreshSoundOptions()
    {
        SoundOptions.Clear();
        foreach (var choice in SoundPlayer.BuiltInChoices) SoundOptions.Add(choice);

        void AddCustom(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (SoundOptions.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))) return;
            if (SoundPlayer.TryGetFilePath(id) is null) return;

            SoundOptions.Add(new SoundChoice(id, SoundPlayer.Describe(id)));
        }

        AddCustom(Cfg.StartSound);
        AddCustom(Cfg.PauseSound);
        AddCustom(Cfg.StopSound);

        // 列表换了一批实例：缓存的选择项失效，按配置重新认一遍（否则 ComboBox 选择会丢）
        _selectedStartSound = FindSound(Cfg.StartSound);
        _selectedPauseSound = FindSound(Cfg.PauseSound);
        _selectedStopSound = FindSound(Cfg.StopSound);

        OnPropertyChanged(nameof(SelectedStartSound));
        OnPropertyChanged(nameof(SelectedPauseSound));
        OnPropertyChanged(nameof(SelectedStopSound));
    }
}

/// <summary>计次列表里的一行。</summary>
/// <param name="Index">第几次。</param>
/// <param name="Total">总时长文本。</param>
/// <param name="Split">与上一次的间隔文本。</param>
public sealed record StopwatchLapRow(int Index, string Total, string Split)
{
    /// <summary>序号显示（<c>#1</c>）。</summary>
    public string IndexText => $"#{Index}";

    /// <summary>间隔显示（<c>+00:12.34</c>）。</summary>
    public string SplitText => "+" + Split;
}
