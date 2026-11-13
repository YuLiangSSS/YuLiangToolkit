using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Media;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.Countdown.ViewModels;

/// <summary>
/// 倒计时页的 ViewModel：显示 / 按钮 / 到点提醒 / 快捷键副作用。
///
/// <para>引擎常驻在工具上（<see cref="CountdownTool"/>），所以切页面不影响倒计时；
/// 页面只负责刷新与按钮。</para>
/// </summary>
public sealed class CountdownViewModel : ToolViewModelBase
{
    /// <summary>倒数时的刷新间隔（毫秒）：100ms 够让秒数看起来在走，又不至于太费。</summary>
    private const int RunningIntervalMs = 100;

    /// <summary>停着 / 暂停时的刷新间隔。</summary>
    private const int IdleIntervalMs = 500;

    /// <summary>预设时长的快捷按钮。</summary>
    public sealed record Preset(int Seconds, string Label);

    private readonly CountdownTool _tool;
    private readonly CountdownEngine _engine;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly SoundPlayer _sound = new();

    private bool _pageVisible;
    private bool _isForeground;

    public CountdownViewModel(CountdownTool tool) : base(CountdownConfig.ToolId)
    {
        _tool = tool;
        Cfg = LoadConfig<CountdownConfig>();

        _engine = new CountdownEngine(TimeSpan.FromSeconds(Cfg.DurationSeconds));

        StartCommand = new RelayCommand(_ => Start(), _ => !_engine.IsRunning);
        PauseCommand = new RelayCommand(_ => Pause(), _ => _engine.IsRunning);
        ResetCommand = new RelayCommand(_ => Reset(), _ => !_engine.IsIdle);
        AddMinuteCommand = new RelayCommand(_ => AddMinutes(1));
        UsePresetCommand = new RelayCommand(p => UsePreset(p as Preset), p => p is Preset);
        ApplyInputCommand = new RelayCommand(_ => ApplyInput());
        FinishAckCommand = new RelayCommand(_ => Reset(), _ => !_engine.IsIdle);
        PlaySoundTestCommand = new RelayCommand(_ => TestSound());
        BrowseSoundCommand = new RelayCommand(_ => BrowseSound());
        SendTestNotificationCommand = new RelayCommand(_ => SendTestNotification());

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(IdleIntervalMs),
        };
        _timer.Tick += (_, _) => Refresh();

        Cfg.PropertyChanged += (_, _) => OnConfigChanged();
        Cfg.Notification.PropertyChanged += (_, _) => OnPropertyChanged(nameof(NotificationSummary));

        RefreshPresets();
        RefreshSoundOptions();
        DurationInput = FormatInput(_engine.Configured);
        Refresh();
        SyncShortcuts();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"快捷键：{string.Join(" · ", _tool.GetShortcuts().Where(s => !s.Primary.IsEmpty)
            .Select(s => $"{s.Name}={s.Primary}"))}");
    }

    /// <summary>强类型配置。</summary>
    public CountdownConfig Cfg { get; }

    // ---------- 命令 ----------

    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand AddMinuteCommand { get; }
    public RelayCommand UsePresetCommand { get; }
    public RelayCommand ApplyInputCommand { get; }
    public RelayCommand FinishAckCommand { get; }
    public RelayCommand PlaySoundTestCommand { get; }
    public RelayCommand BrowseSoundCommand { get; }
    public RelayCommand SendTestNotificationCommand { get; }

    // ---------- 显示 ----------

    private string _remainingText = "05:00";

    /// <summary>剩余时间的大号文字。</summary>
    public string RemainingText
    {
        get => _remainingText;
        private set => Set(ref _remainingText, value);
    }

    private string _stateText = "已就绪";

    /// <summary>状态文字。</summary>
    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    private double _progress;

    /// <summary>进度（0~1，进度环用）。</summary>
    /// <remarks>⚠ 基类也有一个 <c>Progress</c>（表示工具页忙碌度），这里是另一个含义，所以用 new 显式隐藏。</remarks>
    public new double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    private bool _isFinished;

    /// <summary>时间到（界面高亮 + 显示「再来一次」）。</summary>
    public bool IsFinished
    {
        get => _isFinished;
        private set => Set(ref _isFinished, value);
    }

    /// <summary>正在走。</summary>
    public bool IsRunning => _engine.IsRunning;

    /// <summary>暂停中。</summary>
    public bool IsPaused => _engine.IsPaused;

    /// <summary>停着。</summary>
    public bool IsIdle => _engine.IsIdle;

    /// <summary>状态色资源名。</summary>
    public string StateBrushKey => _engine.State switch
    {
        CountdownState.Running => "SuccessBrush",
        CountdownState.Paused => "WarningBrush",
        CountdownState.Finished => "DangerBrush",
        _ => "TextSecondaryBrush",
    };

    private string _durationInput = "5:00";

    /// <summary>时长输入框（<c>5:00</c> / <c>90</c> / <c>1h30m</c> 都认）。</summary>
    public string DurationInput
    {
        get => _durationInput;
        set => Set(ref _durationInput, value ?? "");
    }

    private string _inputHint = "";

    /// <summary>输入解析提示（红字）。</summary>
    public string InputHint
    {
        get => _inputHint;
        private set => Set(ref _inputHint, value);
    }

    /// <summary>快捷预设按钮。</summary>
    public ObservableCollection<Preset> Presets { get; } = new();

    /// <summary>声音选项。</summary>
    public ObservableCollection<SoundChoice> SoundOptions { get; } = new();

    private SoundChoice? _selectedSound;

    /// <summary>到点提示音。</summary>
    public SoundChoice? SelectedSound
    {
        get => _selectedSound ??= FindSound(Cfg.FinishSound);
        set
        {
            if (value is null || ReferenceEquals(_selectedSound, value)) return;
            _selectedSound = value;
            Cfg.FinishSound = value.Id;
        }
    }

    /// <summary>通知设置摘要。</summary>
    public string NotificationSummary => Cfg.Notification.SummaryText;

    /// <summary>快捷键状态说明。</summary>
    public string ShortcutSummary
    {
        get
        {
            if (!Cfg.ShortcutsEnabled) return "快捷键已全部关闭（在主页设置 → 快捷键里打开）";

            var parts = _tool.GetShortcuts().Where(s => s.Enabled && !s.Primary.IsEmpty)
                .Select(s => $"{s.Primary} {s.Name}");
            var text = string.Join(" · ", parts);
            return text.Length > 0 ? text : "还没设置快捷键";
        }
    }

    // ================================================================== 生命周期

    /// <summary>页面显示出来。</summary>
    public void OnPageLoaded()
    {
        _pageVisible = true;
        UpdateTimerInterval();
        Refresh();
    }

    /// <summary>页面切走（降频；倒计时照常走，因为引擎时间戳在工具上）。</summary>
    public void OnPageUnloaded()
    {
        _pageVisible = false;
        UpdateTimerInterval();
    }

    /// <summary>主窗口前台状态变化（决定快捷键操作要不要弹提示）。</summary>
    public void SetForeground(bool foreground) => _isForeground = foreground;

    /// <inheritdoc />
    protected override void OnClosed()
    {
        _pageVisible = false;
        _timer.Stop();
    }

    /// <summary>释放声音播放器（工具退出时调）。</summary>
    public void ReleaseSounds() => _sound.Dispose();

    private void UpdateTimerInterval()
    {
        _timer.Stop();
        if (!_pageVisible) return;

        _timer.Interval = TimeSpan.FromMilliseconds(
            _engine.IsRunning ? RunningIntervalMs : IdleIntervalMs);
        _timer.Start();
    }

    /// <summary>把配置里启用的快捷键注册到引擎。</summary>
    public void SyncShortcuts()
    {
        var engine = ShortcutHub.Engine;
        if (engine is null) return;

        Register(engine, 0x5401, CountdownTool.ShortcutIdStart, Cfg.StartHotkey, Cfg.StartEnabled && Cfg.ShortcutsEnabled);
        Register(engine, 0x5402, CountdownTool.ShortcutIdPause, Cfg.PauseHotkey, Cfg.PauseEnabled && Cfg.ShortcutsEnabled);
        Register(engine, 0x5403, CountdownTool.ShortcutIdReset, Cfg.ResetHotkey, Cfg.ResetEnabled && Cfg.ShortcutsEnabled);
        Register(engine, 0x5404, CountdownTool.ShortcutIdAddMinute, Cfg.AddMinuteHotkey, Cfg.AddMinuteEnabled && Cfg.ShortcutsEnabled);

        OnPropertyChanged(nameof(ShortcutSummary));
    }

    private void Register(GlobalShortcutEngine engine, int id, string shortcutId,
        string comboText, bool enabled)
    {
        if (!enabled || !KeyCombo.TryParse(comboText, out var primary) || primary.IsEmpty)
        {
            engine.Unregister(id);
            return;
        }

        var ok = engine.Register(id, ShortcutKind.Single, primary, null,
            _ => _tool.InvokeShortcut(shortcutId), out var error);

        if (!ok && !string.IsNullOrWhiteSpace(error)) Log($"快捷键「{shortcutId}」注册失败：{error}");
    }

    private void OnConfigChanged()
    {
        _engine.Configured = TimeSpan.FromSeconds(Cfg.DurationSeconds);
        RefreshPresets();
        RefreshSoundOptions();
        SyncShortcuts();
        OnPropertyChanged(nameof(NotificationSummary));
        OnPropertyChanged(nameof(ShortcutSummary));
        Refresh();
    }

    // ================================================================== 操作（按钮）

    /// <summary>开始 / 继续。</summary>
    public void Start() => ApplyStart(notify: false);

    /// <summary>暂停。</summary>
    public void Pause() => ApplyPause(notify: false);

    /// <summary>重置。</summary>
    public void Reset() => ApplyReset(notify: false);

    /// <summary>加 / 减分钟（按钮、快捷键都走它；负数用于「减一分钟」）。</summary>
    public void AddMinutes(int minutes)
    {
        _engine.AddTime(Now(), TimeSpan.FromMinutes(minutes));
        Refresh();
        StatusText = minutes >= 0 ? $"已加 {minutes} 分钟" : $"已减 {-minutes} 分钟";
    }

    /// <summary>用预设时长（点快捷按钮）。</summary>
    public void UsePreset(Preset? preset)
    {
        if (preset is null) return;

        Cfg.DurationSeconds = preset.Seconds;
        _engine.Configured = TimeSpan.FromSeconds(preset.Seconds);
        DurationInput = FormatInput(_engine.Configured);
        InputHint = "";
        Refresh();
        StatusText = $"已设为 {CountdownEngine.Describe(_engine.Configured)}";
    }

    /// <summary>把输入框里的时长应用上去。</summary>
    public void ApplyInput()
    {
        if (CountdownEngine.Parse(DurationInput) is not { } parsed)
        {
            InputHint = "写法看不懂：可以写 90（秒）、5:30（分:秒）、1:02:03 或 1h30m";
            return;
        }

        InputHint = "";
        Cfg.DurationSeconds = (int)parsed.TotalSeconds;
        _engine.Configured = parsed;
        DurationInput = FormatInput(parsed);
        Refresh();
        StatusText = $"已设为 {CountdownEngine.Describe(parsed)}";
    }

    // ================================================================== 操作（快捷键）

    /// <summary>快捷键：开始。</summary>
    public void StartFromShortcut() => ApplyStart(notify: true);

    /// <summary>快捷键：暂停。</summary>
    public void PauseFromShortcut() => ApplyPause(notify: true);

    /// <summary>快捷键：重置。</summary>
    public void ResetFromShortcut() => ApplyReset(notify: true);

    /// <summary>快捷键：+1 分钟。</summary>
    public void AddMinuteFromShortcut()
    {
        var before = _engine.RemainingAt(Now());
        _engine.AddTime(Now(), TimeSpan.FromMinutes(1));
        Refresh();

        StatusText = "已加 1 分钟";
        NotifyShortcut("倒计时 · 加 1 分钟", $"剩余 {CountdownEngine.Format(_engine.RemainingAt(Now()))}",
            NotificationKind.Info);

        _ = before;   // 只为对称保留（不参与提示）
    }

    private void ApplyStart(bool notify)
    {
        if (!_engine.Start(Now()))
        {
            StatusText = "已经在倒计时中";
            return;
        }

        StatusText = "开始倒计时";
        Refresh();
        if (notify) NotifyShortcut("倒计时已开始", CountdownEngine.Format(_engine.RemainingAt(Now())),
            NotificationKind.Success);
    }

    private void ApplyPause(bool notify)
    {
        if (!_engine.Pause(Now()))
        {
            StatusText = _engine.IsIdle ? "还没开始" : "已经是暂停状态";
            return;
        }

        StatusText = "已暂停";
        Refresh();
        if (notify) NotifyShortcut("倒计时已暂停", $"剩余 {CountdownEngine.Format(_engine.RemainingAt(Now()))}",
            NotificationKind.Info);
    }

    private void ApplyReset(bool notify)
    {
        _engine.Reset();
        Refresh();
        StatusText = "已重置";
        if (notify) NotifyShortcut("倒计时已重置", CountdownEngine.Describe(_engine.Configured), NotificationKind.Info);
    }

    /// <summary>快捷键类操作的通知：只在没在看这个页面时弹（正在看的话界面本身就在变）。</summary>
    private void NotifyShortcut(string title, string body, NotificationKind kind)
    {
        if (_isForeground) return;

        NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest(title, body, kind, CountdownConfig.ToolId));
    }

    // ================================================================== 刷新

    private TimeSpan Now() => _clock.Elapsed;

    /// <summary>刷新显示（计时器每秒调多次；也会检测到点）。</summary>
    public void Refresh()
    {
        var now = Now();
        var finishedNow = _engine.Pulse(now);

        RemainingText = CountdownEngine.Format(_engine.RemainingAt(now), _engine.Configured.TotalHours >= 1);
        StateText = _engine.StateText;
        Progress = _engine.ProgressAt(now);
        IsFinished = _engine.IsFinished;

        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(StateBrushKey));

        if (finishedNow) OnFinished();

        // 跑起来 / 停下来时刷新节奏要跟着变
        var wanted = _engine.IsRunning ? RunningIntervalMs : IdleIntervalMs;
        if (_pageVisible && Math.Abs(_timer.Interval.TotalMilliseconds - wanted) > 1)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(wanted);
        }
    }

    /// <summary>到点：响铃 + 弹通知（无论前台后台都弹——这是倒计时的意义所在）。</summary>
    private void OnFinished()
    {
        if (!Cfg.Muted) SoundPlayer.TryPlay(Cfg.FinishSound);

        var title = "⏳ 时间到";
        var body = $"「{CountdownEngine.Describe(_engine.Configured)}」的倒计时结束了";

        NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest(title, body, NotificationKind.Warning, CountdownConfig.ToolId));

        StatusText = "时间到！";
        Log($"倒计时结束（{CountdownEngine.Describe(_engine.Configured)}）");
    }

    /// <summary>通知摘要刷新（设置块里改完后调）。</summary>
    public void RefreshNotificationSummary() => OnPropertyChanged(nameof(NotificationSummary));

    // ================================================================== 设置辅助

    /// <summary>试听到点音。</summary>
    public void TestSound()
    {
        if (!SoundPlayer.IsPlayable(Cfg.FinishSound))
        {
            StatusText = "这个声音没法播放：" + SoundPlayer.Describe(Cfg.FinishSound);
            return;
        }

        SoundPlayer.TryPlay(Cfg.FinishSound);
        StatusText = "试听：" + SoundPlayer.Describe(Cfg.FinishSound);
    }

    /// <summary>选一个自定义 wav。</summary>
    public void BrowseSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择提示音（只支持 .wav）",
            Filter = "Wave 音频 (*.wav)|*.wav",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        Cfg.FinishSound = SoundPlayer.FromFile(dialog.FileName);
        RefreshSoundOptions();
        StatusText = "已选择提示音：" + SoundPlayer.Describe(Cfg.FinishSound);
    }

    /// <summary>发一条测试通知。</summary>
    public void SendTestNotification()
    {
        var ok = NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest("倒计时 · 测试通知",
                $"通道：{(Cfg.Notification.Mode == NotificationMode.InApp ? "应用内" : "Windows")}"
                + $" · 位置：{NotificationSettings.CornerName(Cfg.Notification.Corner)}",
                NotificationKind.Success, CountdownConfig.ToolId));

        StatusText = ok ? "已发送测试通知" : "通知没发出去（通道不可用）";
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var seconds in Cfg.Presets)
        {
            Presets.Add(new Preset(seconds, CountdownEngine.Format(TimeSpan.FromSeconds(seconds),
                TimeSpan.FromSeconds(seconds).TotalHours >= 1)));
        }
    }

    private void RefreshSoundOptions()
    {
        SoundOptions.Clear();
        foreach (var choice in SoundPlayer.BuiltInChoices) SoundOptions.Add(choice);

        if (!string.IsNullOrWhiteSpace(Cfg.FinishSound)
            && SoundPlayer.TryGetFilePath(Cfg.FinishSound) is not null
            && SoundOptions.All(s => !string.Equals(s.Id, Cfg.FinishSound, StringComparison.OrdinalIgnoreCase)))
        {
            SoundOptions.Add(new SoundChoice(Cfg.FinishSound, SoundPlayer.Describe(Cfg.FinishSound)));
        }

        _selectedSound = FindSound(Cfg.FinishSound);
        OnPropertyChanged(nameof(SelectedSound));
    }

    private SoundChoice? FindSound(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return SoundOptions.FirstOrDefault();
        return SoundOptions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? SoundOptions.FirstOrDefault();
    }

    /// <summary>把时长写成输入框的样子（整分钟写 <c>5:00</c>，否则写秒数）。</summary>
    private static string FormatInput(TimeSpan value)
        => value.TotalSeconds % 60 == 0 && value.TotalHours < 100
            ? $"{(int)value.TotalMinutes}:00"
            : CountdownEngine.Format(value, value.TotalHours >= 1);
}
