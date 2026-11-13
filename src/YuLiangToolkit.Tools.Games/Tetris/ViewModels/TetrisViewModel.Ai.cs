using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的「电脑对手」设置与限速（partial 扩展）：难度档位、落子间隔、联机模式下的 AI 循环。
///
/// <para><b>怎么接</b>：联机模式里客户端那一份棋盘由房主代跑（<c>_remoteEngine</c>）。
/// 打开「电脑对手」后，房主会在客户端那份引擎上让 <see cref="TetrisAi"/> 自动落子，
/// 等于「你 vs 电脑」的联机对战 —— 垃圾行互攻 / 各自计分两种模式都能用。</para>
///
/// <para><b>限速</b>：<c>DispatcherTimer</c> 默认间隔是 <b>0ms</b>，以前忘了设，
/// 于是每一帧都能落一块（CPU 多快就落多快）。现在统一按
/// <see cref="TetrisConfig.AiMoveIntervalMs"/> 设间隔，暂停 / 切走时和玩家那块棋盘一起停。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    private TetrisAiLevel _aiDifficulty = TetrisAiLevel.Normal;
    private bool _aiEnabled;
    private DispatcherTimer? _aiTimer;

    /// <summary>难度选项（下拉显示用）。</summary>
    public IReadOnlyList<OptionItem<string>> AiDifficultyOptions { get; } = OptionItem.From(
        TetrisAiLevels.All.Select(l => (TetrisAiLevels.ToKey(l), TetrisAiLevels.DisplayName(l))).ToArray());

    /// <summary>当前难度。</summary>
    public TetrisAiLevel AiDifficulty
    {
        get => _aiDifficulty;
        set
        {
            if (!Set(ref _aiDifficulty, value)) return;
            OnPropertyChanged(nameof(SelectedAiDifficulty));
            OnPropertyChanged(nameof(AiDifficultyHint));
            OnPropertyChanged(nameof(AiOpponentText));
            Cfg.AiDifficulty = TetrisAiLevels.ToKey(value);
        }
    }

    /// <summary>难度下拉选中项。</summary>
    public OptionItem<string> SelectedAiDifficulty
    {
        get => OptionItem<string>.Find(AiDifficultyOptions, TetrisAiLevels.ToKey(_aiDifficulty)) ?? AiDifficultyOptions[1];
        set
        {
            if (value is null) return;
            AiDifficulty = TetrisAiLevels.Parse(value.Value);
        }
    }

    /// <summary>难度说明。</summary>
    public string AiDifficultyHint => TetrisAiLevels.Description(_aiDifficulty);

    /// <summary>AI 落子间隔（毫秒，绑定到设置里的滑块）。</summary>
    public int AiMoveIntervalMs
    {
        get => Cfg.AiMoveIntervalMs;
        set
        {
            if (Cfg.AiMoveIntervalMs == value) return;
            Cfg.AiMoveIntervalMs = value;
            ApplyAiInterval();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiSpeedText));
        }
    }

    /// <summary>落子间隔的一行说明（「约每 X 秒落一块」）。</summary>
    public string AiSpeedText => Cfg.AiMoveIntervalMs < 1000
        ? $"约每 {Cfg.AiMoveIntervalMs} 毫秒落一块"
        : $"约每 {Cfg.AiMoveIntervalMs / 1000.0:0.0} 秒落一块";

    /// <summary>落子间隔滑块的取值范围（XAML 绑定用）。</summary>
    public int AiMoveIntervalMin => TetrisConfig.MinAiMoveIntervalMs;

    /// <summary>落子间隔滑块的上限。</summary>
    public int AiMoveIntervalMax => TetrisConfig.MaxAiMoveIntervalMs;

    /// <summary>开电脑对手（仅在联机模式下有意义）。</summary>
    public bool AiEnabled
    {
        get => _aiEnabled;
        set
        {
            if (!Set(ref _aiEnabled, value)) return;
            Cfg.AiEnabled = value;
            OnPropertyChanged(nameof(AiOpponentText));
            OnPropertyChanged(nameof(AiPanelVisible));
            if (value) StartAiLoop();
            else StopAiLoop();
        }
    }

    /// <summary>电脑对手状态文案。</summary>
    public string AiOpponentText => !_aiEnabled
        ? "未开电脑对手（需要真人对手）"
        : $"电脑对手：{TetrisAiLevels.DisplayName(_aiDifficulty)}（代替联机对手）";

    /// <summary>AI 面板是否显示（联机模式下才有意义）。</summary>
    public bool AiPanelVisible => IsNetMode;

    /// <summary>初始化（主构造函数里调用）。</summary>
    private void InitializeAi()
    {
        _aiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Cfg.AiMoveIntervalMs) };
        _aiTimer.Tick += OnAiTick;
        _aiDifficulty = TetrisAiLevels.Parse(Cfg.AiDifficulty);
        _aiEnabled = Cfg.AiEnabled;
    }

    /// <summary>把落子间隔同步到两个 AI 定时器上（改设置、开局、暂停恢复时都走这里）。</summary>
    private void ApplyAiInterval()
    {
        var interval = TimeSpan.FromMilliseconds(Cfg.AiMoveIntervalMs);
        if (_aiTimer is not null) _aiTimer.Interval = interval;
        if (_soloAiTimer is not null) _soloAiTimer.Interval = interval;
    }

    private void StartAiLoop()
    {
        if (!_aiEnabled || _aiTimer is null) return;
        if (!IsNetMode) return;
        if (IsPaused) return;                       // 暂停中不落子，等继续时再起表
        ApplyAiInterval();
        if (!_aiTimer.IsEnabled) _aiTimer.Start();
    }

    private void StopAiLoop()
    {
        if (_aiTimer is { IsEnabled: true }) _aiTimer.Stop();
    }

    /// <summary>
    /// AI 定时器：每 tick 让「客户端那份引擎」落一块（间隔由设置决定，不是每帧）。
    /// <para>房主开着电脑对手时就等于自己在和电脑打。</para>
    /// </summary>
    private void OnAiTick(object? sender, EventArgs e)
    {
        if (!_aiEnabled || !IsNetMode || _session?.IsHost != true) { StopAiLoop(); return; }
        if (_netPhase != TetrisNetPhase.Playing || _remoteEngine is not { } engine || engine.IsGameOver)
            return;

        var plan = TetrisAi.ChoosePlan(engine, _aiDifficulty, garbageFocused: IsGarbageMode);
        if (plan is not { } chosen)
        {
            // 没有可落的位置：直接锁（等价于顶死）
            engine.Lock();
            PublishAiResult(engine);
            return;
        }

        var result = TetrisAi.ApplyPlan(engine, chosen);

        // 互攻模式：AI 消行也要给自己这边的对手（也就是玩家）送垃圾
        if (IsGarbageMode && result.LinesCleared >= 2)
            _engine.EnqueueGarbage(TetrisEngine.GarbageRowsFor(result.LinesCleared));

        PublishAiResult(engine);
    }

    /// <summary>AI 落完一块后同步界面并广播。</summary>
    private void PublishAiResult(TetrisEngine engine)
    {
        _opponentScore = engine.Score;
        _opponentLines = engine.Lines;
        _opponentLevel = engine.Level;
        _opponentPendingGarbage = engine.PendingGarbage;
        _opponentDead = engine.IsGameOver;
        GuestBoardState = TetrisBoardViewState.FromSnapshot(engine.Snapshot());

        OnPropertyChanged(nameof(GuestScoreText));
        OnPropertyChanged(nameof(OpponentGarbageText));
        OnPropertyChanged(nameof(MyGarbageText));

        if (engine.IsGameOver) FinishNetGame();
        else PublishNetState();
    }
}
