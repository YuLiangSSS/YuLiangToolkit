using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Vsrg.Audio;
using YuLiangToolkit.Tools.Vsrg.Core;

namespace YuLiangToolkit.Tools.Vsrg.ViewModels;

/// <summary>
/// 「VSRG」的游玩 + 结算：一局会话的生命周期、帧循环、键盘输入、暂停。
///
/// <para><b>时间从哪来</b>：<see cref="AudioClockEngine.PositionMs"/>（设备位置驱动的平滑时钟 +
/// 用户音频偏移）。每帧把它交给 <see cref="GameplayEngine.AdvanceTo"/>，然后重绘。</para>
///
/// <para><b>帧循环</b>：<see cref="DispatcherTimer"/>（Render 优先级，8ms）。暂停 / 结算 / 切走一律停表 ——
/// 外壳会把工具页缓存起来，不停表的话切走之后还在判定。</para>
///
/// <para><b>全屏</b>：开始游玩时若配置开了全屏，先 <see cref="RequestFullscreen"/>(true) 再进游玩页；
/// 结束时（真的打完）先退全屏再进结算页；中途退出（暂停遮罩 / 退出按钮）直接回选歌页。</para>
/// </summary>
public sealed partial class VsrgViewModel
{
    private readonly AudioClockEngine _audio = new();
    private GameplayEngine? _engine;
    private DispatcherTimer? _playTimer;
    private Chart? _playingChart;
    private string _playingDifficultyName = "";

    private bool _isPlayRunning;
    private bool _isPaused;
    private bool _isAway;
    private double _playPositionMs;
    private string _hudJudgementText = "";
    private string _lastEventMark = "";

    // ---------------------------------------------------------------- 公开状态
    /// <summary>这一局是不是正在跑（游玩页 + 已开始 + 没结算）。</summary>
    public bool IsPlayRunning => _isPlayRunning;

    /// <summary>现在是不是暂停着。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!Set(ref _isPaused, value)) return;
            OnPropertyChanged(nameof(PauseOverlayVisible));
            OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    /// <summary>显示暂停遮罩。</summary>
    public bool PauseOverlayVisible => IsPaused && IsPlayPage;

    /// <summary>暂停按钮的文字。</summary>
    public string PauseButtonText => IsPaused ? "继续" : "暂停";

    /// <summary>正在游玩的谱面。</summary>
    public Chart? PlayingChart => _playingChart;

    /// <summary>正在游玩的难度名。</summary>
    public string PlayingDifficultyName => _playingDifficultyName;

    /// <summary>游玩中的会话引擎（界面画布读它）。</summary>
    public GameplayEngine? Engine => _engine;

    /// <summary>当前播放位置（毫秒，含用户偏移）。</summary>
    public double PlayPositionMs
    {
        get => _playPositionMs;
        private set
        {
            if (!Set(ref _playPositionMs, value)) return;
            OnPropertyChanged(nameof(PlayProgressText));
            OnPropertyChanged(nameof(ProgressRatio));
        }
    }

    /// <summary>这一局的总时长（毫秒）：音频与谱面结束时间取**较大**的那个（剪短的音频不会让进度条提前满）。</summary>
    private double PlayDurationMs
    {
        get
        {
            var audio = _audio.DurationMs;
            var chart = _playingChart?.LastNoteMs ?? 0;
            return Math.Max(audio, chart);
        }
    }

    /// <summary>进度条（0~1）。</summary>
    public double ProgressRatio
    {
        get
        {
            var duration = PlayDurationMs;
            if (duration <= 0) return 0;
            return Math.Clamp(PlayPositionMs / duration, 0, 1);
        }
    }

    /// <summary>进度的 <c>m:ss / m:ss</c> 文本。</summary>
    public string PlayProgressText => $"{FormatClock(PlayPositionMs)} / {FormatClock(PlayDurationMs)}";

    private static string FormatClock(double ms)
    {
        var total = ms <= 0 ? 0 : (int)Math.Round(ms / 1000.0);
        return $"{total / 60}:{total % 60:00}";
    }

    /// <summary>游玩中的准确率文本。</summary>
    public string PlayAccuracyText => _engine is null ? "100.00%" : _engine.Accuracy.ToString("0.00") + "%";

    /// <summary>游玩中的 combo 文本。</summary>
    public string PlayComboText => _engine is null ? "0" : _engine.Combo.ToString();

    /// <summary>游玩中的最大 combo。</summary>
    public string PlayMaxComboText => _engine is null ? "0" : _engine.MaxCombo.ToString();

    /// <summary>
    /// 判定线下方的按键字母（空格分隔，画布按轨道顺序画）。
    /// 只取这份谱面用到的前 <c>KeyCount</c> 个键。
    /// </summary>
    public string LaneLabelsText
    {
        get
        {
            var keyCount = _playingChart?.KeyCount ?? 0;
            if (keyCount <= 0) return "";

            var names = new List<string>(keyCount);
            for (var i = 0; i < keyCount; i++) names.Add(Cfg.GetKey(i));
            return string.Join(' ', names);
        }
    }

    /// <summary>刚判完的档位名（判定线上的大字）。</summary>
    public string HudJudgementText
    {
        get => _hudJudgementText;
        private set
        {
            if (!Set(ref _hudJudgementText, value)) return;
            OnPropertyChanged(nameof(HasHudJudgement));
        }
    }

    /// <summary>有没有判定弹字要显示。</summary>
    public bool HasHudJudgement => HudJudgementText.Length > 0;

    // ---------------------------------------------------------------- 结算页状态

    /// <summary>结算：准确率。</summary>
    public string ResultAccuracyText { get; private set; } = "100.00%";

    /// <summary>结算：最大 combo。</summary>
    public string ResultMaxComboText { get; private set; } = "0";

    /// <summary>结算：判定档统计（每档名字 + 次数）。</summary>
    public ObservableCollection<TierCountRow> ResultTierCounts { get; } = new();

    /// <summary>结算：谱面标题。</summary>
    public string ResultChartTitle { get; private set; } = "";

    /// <summary>结算：难度副标题。</summary>
    public string ResultChartSubtitle { get; private set; } = "";

    /// <summary>结算：一句话说明。</summary>
    public string ResultTitleText { get; private set; } = "本局完成";

    // ---------------------------------------------------------------- 命令

    /// <summary>暂停 / 继续。</summary>
    public RelayCommand TogglePauseCommand { get; private set; } = null!;

    /// <summary>退出当前歌曲回选歌页。</summary>
    public RelayCommand QuitPlayCommand { get; private set; } = null!;

    /// <summary>结算页：再来一次。</summary>
    public RelayCommand ReplayCommand { get; private set; } = null!;

    /// <summary>结算页：回选歌。</summary>
    public RelayCommand ResultBackCommand { get; private set; } = null!;

    private void InitializePlay()
    {
        // ⚠ 音频放完**不代表这一局结束**：谱面可能比音频长（剪短的音频 / 音频被裁过头）。
        // 这里什么都不做，让引擎按谱面自己的结束时间判断（见 OnPlayTick 的 IsFinished）。
        _audio.PlaybackEnded += (_, _) => StatusText = "音频已经放完，继续打完剩下的音符…";

        TogglePauseCommand = new RelayCommand(_ => TogglePause(), _ => IsPlayRunning);

        // 「退出当前歌曲」= 直接回选歌页（按需求：暂停就能退出去换歌），不进结算。
        // 进结算只发生在「真的打完了」那一次（见 OnPlayTick 的 IsFinished）。
        QuitPlayCommand = new RelayCommand(_ => AbandonPlayback(exitFullscreen: true), _ => IsPlayRunning || IsPaused);

        ReplayCommand = new RelayCommand(_ => ReplaySameChart());
        ResultBackCommand = new RelayCommand(_ =>
        {
            Page = VsrgPage.SongSelect;
            StatusText = Songs.Count == 0 ? "把 .osz 拖进窗口就能导入。" : $"共 {Songs.Count} 张谱面集。";
        });
    }

    /// <summary>
    /// 开始游玩当前选中的难度：加载谱面 + 音频 → 拍判定快照 → 需要时进全屏 → 切游玩页 → 起帧循环。
    /// </summary>
    public void StartPlay()
    {
        if (SelectedSong is null || SelectedDifficulty is null) return;

        var chart = MapsetLibrary.LoadChart(SelectedSong, SelectedDifficulty);
        if (chart is null)
        {
            ImportStatus = "这个谱面读不出来（文件可能被删了），试着刷新一下。";
            return;
        }

        if (!chart.CanPlay)
        {
            ImportStatus = chart.PlayBlockReason;
            return;
        }

        var tierError = GameplayProfile.ValidateTiers(Tiers.Select(t => t.ToTier()).ToList());
        if (tierError is not null)
        {
            ImportStatus = "判定档配置有问题（先去设置页改好）：" + tierError;
            GoToSettings();
            return;
        }

        // 判定快照：这一局用开局时的判定档，游玩途中改设置不影响本局
        var profile = Cfg.BuildProfile();

        if (!_audio.Load(chart.AudioPath))
        {
            ImportStatus = "音频打不开：" + _audio.LastError;
            return;
        }

        _audio.Volume = Cfg.MusicVolume;
        _audio.UserOffsetMs = Cfg.AudioOffsetMs;

        _playingChart = chart;
        _playingDifficultyName = chart.DifficultyLine;
        _engine = new GameplayEngine(chart, profile);

        PlayPositionMs = 0;
        HudJudgementText = "";
        _lastEventMark = "";

        IsPaused = false;
        _isAway = false;
        _isPlayRunning = true;

        // 全屏要在切页之前发：外壳藏掉标题栏 / 侧边栏，游玩页才真的是整块屏幕
        if (Cfg.PlayFullscreen) RequestFullscreen(true);

        Page = VsrgPage.Play;
        OnPropertyChanged(nameof(PlayingChart));
        OnPropertyChanged(nameof(PlayingDifficultyName));
        OnPropertyChanged(nameof(Engine));
        OnPropertyChanged(nameof(LaneLabelsText));
        OnPropertyChanged(nameof(PlayAccuracyText));
        OnPropertyChanged(nameof(PlayComboText));
        OnPropertyChanged(nameof(PlayMaxComboText));
        OnPropertyChanged(nameof(PlayProgressText));
        OnPropertyChanged(nameof(ProgressRatio));

        EnsurePlayTimer();

        if (!_audio.PlayFromStart())
        {
            ImportStatus = "播放失败：" + _audio.LastError;
            AbandonPlayback(exitFullscreen: true);
            return;
        }

        _playTimer!.Start();
        StatusText = $"正在游玩：{chart.TitleLine} [{chart.DifficultyName}]";
    }

    /// <summary>同一张谱面再来一次。</summary>
    private void ReplaySameChart()
    {
        // 结算页还留着刚才选中的谱面集 / 难度（没有清空过），直接按它们重开一局
        StartPlay();
    }

    private void EnsurePlayTimer()
    {
        if (_playTimer is not null) return;

        _playTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(8),
        };
        _playTimer.Tick += OnPlayTick;
    }

    /// <summary>
    /// 每帧：推进音频时钟 → 交给判定引擎 → 刷新界面数字。
    /// </summary>
    private void OnPlayTick(object? sender, EventArgs e)
    {
        if (_engine is null || _playingChart is null) return;

        _audio.Update();
        if (_isPaused) return;

        var position = _audio.PositionMs;
        PlayPositionMs = position;

        _engine.AdvanceTo(position);

        // 判定弹字：引擎只记「最近一次判定」，同一帧里可能连续判好几个，这里按「是否换了一条」弹
        var last = _engine.LastEvent;
        if (last is { } evt)
        {
            var mark = MarkEvent(evt);
            if (!string.Equals(mark, _lastEventMark, StringComparison.Ordinal))
            {
                _lastEventMark = mark;
                HudJudgementText = evt.Display;
            }
        }

        OnPropertyChanged(nameof(PlayAccuracyText));
        OnPropertyChanged(nameof(PlayComboText));
        OnPropertyChanged(nameof(PlayMaxComboText));

        if (_engine.IsFinished) FinishPlay();
    }

    private static string MarkEvent(JudgementEvent evt)
        => $"{evt.NoteIndex}|{evt.Unit}|{evt.TierIndex}";

    // ---------------------------------------------------------------- 键盘输入

    /// <summary>
    /// 按下某个键：命中本局键位表就打一轨。返回 true 表示这个键被吃掉了（界面不要再处理）。
    /// </summary>
    public bool HandleKeyDown(System.Windows.Input.Key key)
    {
        if (!IsPlayRunning || _engine is null) return false;

        if (key is System.Windows.Input.Key.Escape or System.Windows.Input.Key.P)
        {
            TogglePause();
            return true;
        }

        if (IsPaused) return false;

        var lane = LaneOf(key);
        if (lane < 0) return false;

        var result = _engine.Press(lane);
        if (result is { } evt) ShowJudgement(evt);
        return true;
    }

    /// <summary>松开某个键：面条尾判在这里结算。</summary>
    public bool HandleKeyUp(System.Windows.Input.Key key)
    {
        if (_engine is null) return false;

        var lane = LaneOf(key);
        if (lane < 0) return false;

        if (IsPlayRunning && !IsPaused)
        {
            var result = _engine.Release(lane);
            if (result is { } evt) ShowJudgement(evt);
        }
        else
        {
            _engine.Release(lane);
        }

        return true;
    }

    private void ShowJudgement(JudgementEvent evt)
    {
        HudJudgementText = evt.Display;
        _lastEventMark = MarkEvent(evt);
    }

    /// <summary>把 WPF 的键换算成这次游玩的轨道号（不在键位表里返回 -1）。</summary>
    private int LaneOf(System.Windows.Input.Key key)
    {
        if (_playingChart is null) return -1;

        var keyCount = _playingChart.KeyCount;
        var names = new string[keyCount];
        for (var i = 0; i < keyCount; i++) names[i] = Cfg.GetKey(i);

        var map = LaneKeys.BuildLaneMap(names, keyCount);
        var virtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        return map.TryGetValue(virtualKey, out var lane) ? lane : -1;
    }

    /// <summary>暂停 / 继续。</summary>
    public void TogglePause()
    {
        if (!IsPlayRunning) return;
        SetPaused(!IsPaused);
    }

    /// <summary>设置暂停状态（音频跟着暂停 / 继续）。</summary>
    public void SetPaused(bool paused)
    {
        if (!IsPlayRunning) return;
        if (IsPaused == paused) return;

        IsPaused = paused;
        if (paused)
        {
            _audio.Pause();
            _engine?.ReleaseAll();
        }
        else
        {
            _audio.Resume();
        }

        StatusText = paused ? "已暂停（Esc / P 继续）" : $"正在游玩：{_playingChart?.TitleLine}";
    }

    /// <summary>切走工具页（外壳缓存视图）：必须停表 + 暂停。</summary>
    public void PauseForAway()
    {
        if (!IsPlayRunning) return;
        _isAway = true;
        SetPaused(true);
        _playTimer?.Stop();
    }

    /// <summary>切回来：接着跑（仍然是暂停态，等用户自己点继续）。</summary>
    public void ResumeAfterAway()
    {
        if (!IsPlayRunning) return;
        _isAway = false;
        _playTimer?.Start();
    }

    /// <summary>切走程序窗口：按配置自动暂停。</summary>
    public void PauseForLostFocus()
    {
        if (!IsPlayRunning || !Cfg.PauseOnLostFocus) return;
        SetPaused(true);
    }

    /// <summary>窗口重新获得焦点：如果是「切窗口自动暂停」造成的，自动继续。</summary>
    public void ResumeAfterFocus()
    {
        if (!IsPlayRunning || !Cfg.PauseOnLostFocus) return;
        if (!IsPaused) return;
        if (_isAway) return;      // 整个工具页被切走的，等用户点继续
        SetPaused(false);
    }

    // ---------------------------------------------------------------- 结束 / 退出

    /// <summary>打完一首：退全屏 → 进结算页。</summary>
    private void FinishPlay()
    {
        if (_engine is null || _playingChart is null) return;

        CollectResult();

        _playTimer?.Stop();
        _audio.Stop();
        _audio.Close();

        _isPlayRunning = false;
        IsPaused = false;
        HudJudgementText = "";

        // 先退全屏再切页：否则结算页会在全屏里闪一下（标题栏 / 侧边栏重新出现时更明显）
        if (Cfg.PlayFullscreen) RequestFullscreen(false);

        Page = VsrgPage.Result;
        StatusText = "本局完成。";
    }

    /// <summary>把这一局的成绩拍进结算页的状态。</summary>
    private void CollectResult()
    {
        var engine = _engine!;
        var chart = _playingChart!;

        ResultAccuracyText = engine.Accuracy.ToString("0.00") + "%";
        ResultMaxComboText = engine.MaxCombo.ToString();
        ResultChartTitle = chart.TitleLine;
        ResultChartSubtitle = chart.DifficultyLine;
        ResultTitleText = "本局完成";

        ResultTierCounts.Clear();
        for (var i = 0; i < engine.Profile.Tiers.Count; i++)
        {
            var tier = engine.Profile.Tiers[i];
            ResultTierCounts.Add(new TierCountRow(tier.Name, engine.TierCounts[i], tier.IsMiss));
        }

        OnPropertyChanged(nameof(ResultAccuracyText));
        OnPropertyChanged(nameof(ResultMaxComboText));
        OnPropertyChanged(nameof(ResultChartTitle));
        OnPropertyChanged(nameof(ResultChartSubtitle));
        OnPropertyChanged(nameof(ResultTitleText));
    }

    /// <summary>
    /// 把播放 / 判定全部停掉、退回选歌页（不再产生结算 —— 结算只在真的打完时出现）。
    /// </summary>
    public void AbandonPlayback(bool exitFullscreen)
    {
        _playTimer?.Stop();
        _audio.Stop();
        _audio.Close();

        _isPlayRunning = false;
        IsPaused = false;
        HudJudgementText = "";
        _engine = null;
        _playingChart = null;

        if (exitFullscreen) RequestFullscreen(false);

        Page = VsrgPage.SongSelect;
        StatusText = Songs.Count == 0 ? "把 .osz 拖进窗口就能导入。" : $"共 {Songs.Count} 张谱面集。";
    }
}

/// <summary>结算页的一行「判定档 + 次数」。</summary>
public sealed class TierCountRow
{
    /// <summary>构造。</summary>
    public TierCountRow(string name, int count, bool isMiss)
    {
        Name = name;
        Count = count;
        IsMiss = isMiss;
    }

    /// <summary>档位名。</summary>
    public string Name { get; }

    /// <summary>次数。</summary>
    public int Count { get; }

    /// <summary>是不是 Miss 档（界面上标红）。</summary>
    public bool IsMiss { get; }

    /// <summary>次数的文本。</summary>
    public string CountText => Count.ToString();
}
