using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Core.Media;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Notifications;

namespace YuLiangToolkit.Tools.Alarm.ViewModels;

/// <summary>
/// 闹钟页的 ViewModel：闹钟列表 + 到点检测（一个每秒跳的计时器）+ 响铃 / 贪睡 / 停止。
///
/// <para><b>到点检测</b>：每秒算一次「下一次该响的时刻」，到点就进 <see cref="Ringing"/>。
/// 不用系统定时器精确等待 —— 电脑睡眠 / 休眠醒来后，「现在」一跳就正好命中，
/// 不会因为错过的定时器而永久不响。</para>
/// </summary>
public sealed class AlarmViewModel : ToolViewModelBase
{
    /// <summary>响铃时重复播放的间隔（毫秒）。</summary>
    private const int RingRepeatMs = 4000;

    /// <summary>列表里的一行（闹钟 + 下一次响铃说明）。</summary>
    public sealed class AlarmRow
    {
        /// <summary>底层闹钟。</summary>
        public required AlarmEntry Entry { get; init; }

        /// <summary>下一次响铃（启用且能算出时才非 null）。</summary>
        public DateTime? Next { get; init; }

        /// <summary>下一次响铃的可读说明（「明天 07:00 · 还有 8 小时」）。</summary>
        public string NextText { get; init; } = "";

        /// <summary>显示时间。</summary>
        public string Time => Entry.DisplayTime;

        /// <summary>标题（备注或时间）。</summary>
        public string Title => Entry.DisplayTitle;

        /// <summary>重复说明。</summary>
        public string RepeatText => Entry.RepeatText;

        /// <summary>开关状态。</summary>
        public bool Enabled => Entry.Enabled;

        /// <summary>没启用时列表整行变淡。</summary>
        public double RowOpacity => Entry.Enabled ? 1 : 0.45;

        /// <summary>这一行是不是展开着编辑（点时间行切换）。</summary>
        public bool IsExpanded { get; set; }
    }

    private readonly AlarmConfig _cfg;
    private readonly DispatcherTimer _timer;
    private readonly SoundPlayer _sound = new();
    private readonly DispatcherTimer _ringTimer;

    private DateTime _now = DateTime.Now;
    private DateTime? _nextRing;

    public AlarmViewModel() : base(AlarmConfig.ToolId)
    {
        _cfg = LoadConfig<AlarmConfig>();
        Cfg = _cfg;
        EnsureFirstAlarm();

        AddAlarmCommand = new RelayCommand(_ => AddAlarm());
        RemoveAlarmCommand = new RelayCommand(p => RemoveAlarm(p as AlarmEntry), p => p is AlarmEntry);
        ToggleEnabledCommand = new RelayCommand(p => ToggleEnabled(p as AlarmEntry), p => p is AlarmEntry);
        StopRingCommand = new RelayCommand(_ => StopRinging(), _ => IsRinging);
        SnoozeCommand = new RelayCommand(_ => Snooze(), _ => IsRinging && _ringing?.SnoozeEnabled == true);
        TestSoundCommand = new RelayCommand(_ => TestSound());
        BrowseSoundCommand = new RelayCommand(_ => BrowseSound());
        ClearAllCommand = new RelayCommand(_ => ClearAll(), _ => Cfg.Alarms.Count > 0);
        ToggleExpandCommand = new RelayCommand(p => ToggleExpand(p as AlarmEntry), p => p is AlarmEntry);

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        _ringTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RingRepeatMs),
        };
        _ringTimer.Tick += (_, _) =>
        {
            if (Cfg.RepeatSound && !Cfg.Muted) SoundPlayer.TryPlay(Cfg.Sound);
        };

        Cfg.PropertyChanged += OnConfigChanged;
        Cfg.Alarms.CollectionChanged += (_, _) => Ui(() =>
        {
            HookAlarms();
            RefreshRows();
        });
        HookAlarms();

        RefreshSoundOptions();
        UpdateNow();
        RefreshRows();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"闹钟 {Cfg.Alarms.Count} 个（启用 {Cfg.EnabledCount} 个）");
    }

    /// <summary>强类型配置。</summary>
    public AlarmConfig Cfg { get; }

    // ---------- 命令 ----------

    public RelayCommand AddAlarmCommand { get; }
    public RelayCommand RemoveAlarmCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }
    public RelayCommand StopRingCommand { get; }
    public RelayCommand SnoozeCommand { get; }
    public RelayCommand TestSoundCommand { get; }
    public RelayCommand BrowseSoundCommand { get; }
    public RelayCommand ClearAllCommand { get; }

    /// <summary>展开 / 收起某个闹钟的编辑区。</summary>
    public RelayCommand ToggleExpandCommand { get; }

    // ---------- 列表 ----------

    /// <summary>列表行（按时间排序）。</summary>
    public ObservableCollection<AlarmRow> Rows { get; } = new();

    /// <summary>一个闹钟都没有（空状态）。</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>页头统计。</summary>
    public string SummaryText
    {
        get
        {
            if (Cfg.Alarms.Count == 0) return "还没有闹钟";
            var enabled = Cfg.EnabledCount;
            var next = Rows.Select(r => r.Next).Where(n => n is not null).OrderBy(n => n).FirstOrDefault();

            var text = $"共 {Cfg.Alarms.Count} 个 · 启用 {enabled} 个";
            if (next is { } n) text += $" · 下一次 {AlarmSchedule.DescribeNext(n, _now)}";
            return text;
        }
    }

    // ---------- 响铃状态 ----------

    private AlarmEntry? _ringing;

    /// <summary>正在响的那个闹钟（null = 没在响）。</summary>
    public AlarmEntry? Ringing
    {
        get => _ringing;
        private set
        {
            if (!Set(ref _ringing, value)) return;
            OnPropertyChanged(nameof(IsRinging));
            OnPropertyChanged(nameof(RingingTitle));
            OnPropertyChanged(nameof(RingingText));
            OnPropertyChanged(nameof(CanSnooze));
        }
    }

    /// <summary>正在响铃。</summary>
    public bool IsRinging => _ringing is not null;

    /// <summary>响铃标题。</summary>
    public string RingingTitle => _ringing is null ? "" : "⏰ " + _ringing.DisplayTitle;

    /// <summary>响铃正文。</summary>
    public string RingingText
    {
        get
        {
            if (_ringing is null) return "";
            return $"{_ringing.Time} 的闹钟" + (string.IsNullOrWhiteSpace(_ringing.Label) ? "" : $"（{_ringing.Label}）");
        }
    }

    /// <summary>这个闹钟能不能贪睡。</summary>
    public bool CanSnooze => _ringing?.SnoozeEnabled == true;

    // ---------- 声音 ----------

    /// <summary>响铃音选项。</summary>
    public ObservableCollection<SoundChoice> SoundOptions { get; } = new();

    private SoundChoice? _selectedSound;

    /// <summary>当前选的响铃音。</summary>
    public SoundChoice? SelectedSound
    {
        get => _selectedSound ??= FindSound(Cfg.Sound);
        set
        {
            if (value is null || ReferenceEquals(_selectedSound, value)) return;
            _selectedSound = value;
            Cfg.Sound = value.Id;
        }
    }

    // ---------- 操作 ----------

    /// <summary>加一个闹钟（默认为「下一个整点」）。</summary>
    public void AddAlarm()
    {
        var nextHour = DateTime.Now.AddHours(1);
        var entry = new AlarmEntry
        {
            Time = nextHour.ToString("HH:mm"),
            Label = "",
            Days = "",
            Enabled = true,
        };

        Cfg.Alarms.Add(entry);
        StatusText = $"已添加 {entry.Time} 的闹钟";
        Log($"添加闹钟 {entry.Time}");
        RefreshRows();
    }

    /// <summary>删掉一个闹钟（正在响的那个被删掉就停响）。</summary>
    public void RemoveAlarm(AlarmEntry? entry)
    {
        if (entry is null) return;

        if (ReferenceEquals(entry, _ringing)) StopRinging();

        if (Cfg.Alarms.Remove(entry))
        {
            StatusText = $"已删除 {entry.Time} 的闹钟";
            Log($"删除闹钟 {entry.Time}");
            RefreshRows();
        }
    }

    /// <summary>开 / 关一个闹钟。</summary>
    public void ToggleEnabled(AlarmEntry? entry)
    {
        if (entry is null) return;

        entry.Enabled = !entry.Enabled;
        StatusText = entry.Enabled ? $"已启用 {entry.Time}" : $"已关闭 {entry.Time}";
        RefreshRows();
    }

    /// <summary>展开 / 收起某个闹钟的编辑区（同一时刻只展开一个，省得列表太长）。</summary>
    public void ToggleExpand(AlarmEntry? entry)
    {
        if (entry is null) return;

        var wasExpanded = _expandedId == entry.Id;
        _expandedId = wasExpanded ? null : entry.Id;
        RefreshRows();
    }

    /// <summary>当前展开着编辑的闹钟 Id（null = 都收起）。</summary>
    private string? _expandedId;

    /// <summary>清空全部闹钟。</summary>
    public void ClearAll()
    {
        StopRinging();
        Cfg.Alarms.Clear();
        StatusText = "已清空全部闹钟";
        RefreshRows();
    }

    /// <summary>停止响铃。</summary>
    public void StopRinging()
    {
        if (_ringing is null) return;

        var stopped = _ringing;

        // 只响一次的闹钟：响过就自动关掉
        if (stopped.IsOneShot) stopped.Enabled = false;

        Ringing = null;
        _ringTimer.Stop();
        SoundPlayer.Stop();

        StatusText = $"已停止 {stopped.Time} 的闹钟";
        Log($"闹钟停止 {stopped.Time}");
        RefreshRows();
    }

    /// <summary>贪睡：停止本次，推迟若干分钟再响。</summary>
    public void Snooze()
    {
        if (_ringing is not { SnoozeEnabled: true } entry) return;

        var until = AlarmSchedule.TrimSnooze(DateTime.Now, Cfg.SnoozeMinutes);

        Ringing = null;
        _ringTimer.Stop();
        SoundPlayer.Stop();

        // 用一个临时闹钟代表这次贪睡（只响一次，响完自动关）
        var snooze = new AlarmEntry
        {
            Time = until.ToString("HH:mm"),
            Label = (string.IsNullOrWhiteSpace(entry.Label) ? entry.Time : entry.Label) + "（贪睡）",
            Days = "",
            Enabled = true,
            SnoozeEnabled = false,
        };

        Cfg.Alarms.Add(snooze);
        StatusText = $"贪睡到 {snooze.Time}";
        RefreshRows();
        Log($"贪睡到 {until:HH:mm}");
    }

    // ---------- 到点检测 ----------

    /// <summary>
    /// 每秒调一次：更新「现在」，看有没有闹钟该响。
    ///
    /// <para><b>判定方式</b>：给每个闹钟记「上次响铃的日期」，只要「到了设定时刻 且 今天还没响过」就响。
    /// 不用「算出的下一次时刻 == 现在」那种精确匹配 —— 计时器一次 Tick 可能跨过整点几毫秒，
    /// 精确匹配会永久漏响。这个写法对「电脑睡眠醒来」也成立（醒来那一刻直接补齐）。</para>
    /// </summary>
    public void Tick()
    {
        UpdateNow();

        if (_ringing is null)
        {
            var hit = Cfg.Alarms.FirstOrDefault(a => a.Enabled && ShouldRingNow(a, _now));
            if (hit is not null) StartRinging(hit);
        }

        RefreshRows();
    }

    /// <summary>
    /// 这个闹钟现在是否该响。
    ///
    /// <para><b>判定</b>：到了设定时刻 + 今天还没响过 + 星期匹配。</para>
    ///
    /// <para>⚠ 还要加一条"不补给过去"：程序刚启动时今天 07:00 早就过了，光按前两条会**一打开就响**。
    /// 所以每个闹钟记第一次见到它的时间，只响"见到之后才到达"的那些时刻 ——
    /// 这既避免了开机就炸，又保留了"电脑睡眠跨过整点、醒来补响"的效果
    /// （睡眠期间进程还在，第一次见到的时间早于睡眠，醒来后照样判定为"该响"）。</para>
    /// </summary>
    private bool ShouldRingNow(AlarmEntry entry, DateTime now)
    {
        // 今天的设定时刻还没到 → 不响
        var dueToday = now.Date + entry.TimeOfDay;
        if (now < dueToday) return false;

        // 已经响过（同一天同一闹钟只响一次）
        if (_lastRung.TryGetValue(entry.Id, out var lastDate) && lastDate == now.Date) return false;

        // 程序是今天这个时刻之后才见到它的 → 这次的点已经错过了，不补
        if (_firstSeen.TryGetValue(entry.Id, out var firstSeen) && firstSeen > dueToday) return false;

        // 只响一次：响过就关掉了，不用再判断星期
        var days = entry.RepeatDays;
        if (days.Count == 0) return true;

        return days.Contains(now.DayOfWeek);
    }

    /// <summary>每个闹钟第一次被看见的时刻（用来避免"启动就补响过去的点"）。</summary>
    private readonly Dictionary<string, DateTime> _firstSeen = new();

    /// <summary>每个闹钟上次响铃的日期（同一天只响一次）。</summary>
    private readonly Dictionary<string, DateTime> _lastRung = new();

    /// <summary>开始响铃。</summary>
    private void StartRinging(AlarmEntry entry)
    {
        Ringing = entry;

        if (!Cfg.Muted) SoundPlayer.TryPlay(Cfg.Sound);
        if (Cfg.RepeatSound) _ringTimer.Start();

        NotificationCenter.Show(Cfg.Notification,
            new NotificationRequest("⏰ " + entry.DisplayTitle, $"{entry.Time} 的闹钟" +
                (string.IsNullOrWhiteSpace(entry.Label) ? "" : $"（{entry.Label}）"),
                NotificationKind.Warning, AlarmConfig.ToolId));

        _lastRung[entry.Id] = _now.Date;
        Log($"闹钟响铃 {entry.Time}");

        // 只响一次的闹钟（含贪睡产生的临时闹钟）：响过就关掉
        if (entry.IsOneShot) entry.Enabled = false;
        RefreshRows();
    }

    /// <summary>更新「现在」与「下一次响铃」。</summary>
    private void UpdateNow()
    {
        _now = DateTime.Now;

        // 第一次见到某个闹钟：记下时刻（ShouldRingNow 用它排除"启动前就过去的点"）
        foreach (var alarm in Cfg.Alarms)
        {
            if (!_firstSeen.ContainsKey(alarm.Id)) _firstSeen[alarm.Id] = _now;
        }

        DateTime? earliest = null;
        foreach (var alarm in Cfg.Alarms.Where(a => a.Enabled))
        {
            if (AlarmSchedule.NextOccurrence(alarm.TimeOfDay, alarm.RepeatDays, _now) is not { } next) continue;
            if (earliest is null || next < earliest) earliest = next;
        }

        _nextRing = earliest;
    }

    /// <summary>重算列表行（时间 + 倒计时说明）。</summary>
    private void RefreshRows()
    {
        var rows = Cfg.Alarms
            .Select(alarm =>
            {
                var next = alarm.Enabled
                    ? AlarmSchedule.NextOccurrence(alarm.TimeOfDay, alarm.RepeatDays, _now)
                    : null;

                var text = next is { } n
                    ? $"{AlarmSchedule.DescribeNext(n, _now)} · 还有 {AlarmSchedule.DescribeUntil(n, _now)}"
                    : alarm.Enabled ? "算不出下次时间" : "已关闭";

                return new AlarmRow
                {
                    Entry = alarm,
                    Next = next,
                    NextText = text,
                    IsExpanded = _expandedId == alarm.Id,
                };
            })
            .OrderBy(row => row.Next ?? DateTime.MaxValue)
            .ToList();

        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    // ---------- 设置辅助 ----------

    /// <summary>试听响铃音。</summary>
    public void TestSound()
    {
        if (!SoundPlayer.IsPlayable(Cfg.Sound))
        {
            StatusText = "这个声音没法播放：" + SoundPlayer.Describe(Cfg.Sound);
            return;
        }

        SoundPlayer.TryPlay(Cfg.Sound);
        StatusText = "试听：" + SoundPlayer.Describe(Cfg.Sound);
    }

    /// <summary>选一个自定义 wav。</summary>
    public void BrowseSound()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择响铃音（只支持 .wav）",
            Filter = "Wave 音频 (*.wav)|*.wav",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true) return;

        Cfg.Sound = SoundPlayer.FromFile(dialog.FileName);
        RefreshSoundOptions();
        StatusText = "已选择响铃音：" + SoundPlayer.Describe(Cfg.Sound);
    }

    private void RefreshSoundOptions()
    {
        SoundOptions.Clear();
        foreach (var choice in SoundPlayer.BuiltInChoices) SoundOptions.Add(choice);

        if (!string.IsNullOrWhiteSpace(Cfg.Sound)
            && SoundPlayer.TryGetFilePath(Cfg.Sound) is not null
            && SoundOptions.All(s => !string.Equals(s.Id, Cfg.Sound, StringComparison.OrdinalIgnoreCase)))
        {
            SoundOptions.Add(new SoundChoice(Cfg.Sound, SoundPlayer.Describe(Cfg.Sound)));
        }

        _selectedSound = FindSound(Cfg.Sound);
        OnPropertyChanged(nameof(SelectedSound));
    }

    private SoundChoice? FindSound(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return SoundOptions.FirstOrDefault();
        return SoundOptions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? SoundOptions.FirstOrDefault();
    }

    /// <summary>首次运行放入一个工作日 07:00 的闹钟（用户可删）。</summary>
    private void EnsureFirstAlarm()
    {
        if (Cfg.Alarms.Count > 0 || !IsFirstRun) return;
        Cfg.Alarms.Add(AlarmEntry.CreateWorkdayDefault());
    }

    private void HookAlarms()
    {
        foreach (var alarm in Cfg.Alarms)
        {
            alarm.PropertyChanged -= OnAlarmChanged;
            alarm.PropertyChanged += OnAlarmChanged;
        }
    }

    private void OnAlarmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AlarmEntry.Enabled) or nameof(AlarmEntry.Time) or nameof(AlarmEntry.Days))
        {
            UpdateNow();
            RefreshRows();
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AlarmConfig.Sound)) RefreshSoundOptions();
        UpdateNow();
        RefreshRows();
    }

    /// <summary>页面可见：计时器已经在跑（工具级），这里只重算一次。</summary>
    public void OnPageLoaded()
    {
        UpdateNow();
        RefreshRows();
    }

    /// <summary>停掉所有计时器与响铃（工具关闭 / 自检收尾用）。</summary>
    public void StopAll()
    {
        _timer.Stop();
        _ringTimer.Stop();
        SoundPlayer.Stop();
    }

    /// <summary>工具被切走 / 关闭：停掉响铃定时器（到点检测的计时器也停，见 OnClosed）。</summary>
    protected override void OnClosed()
    {
        _timer.Stop();
        _ringTimer.Stop();
        SoundPlayer.Stop();
    }
}
