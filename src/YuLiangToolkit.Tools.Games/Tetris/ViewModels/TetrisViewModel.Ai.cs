using System.Windows.Threading;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的「电脑对手」部分（partial 扩展）。
///
/// <para><b>怎么接</b>：联机模式里客户端那一份棋盘由房主代跑（<c>_remoteEngine</c>）。
/// 打开「电脑对手」后，房主会在客户端那份引擎上让 <see cref="TetrisAi"/> 自动落子，
/// 等于「你 vs 电脑」的联机对战 —— 垃圾行互攻 / 各自计分两种模式都能用。</para>
///
/// <para><b>单人模式不接管</b>：单人玩法是纯自己玩，加个 AI 没有对手，没意义。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    private AiDifficulty _aiDifficulty = AiDifficulty.Normal;
    private bool _aiEnabled;
    private DispatcherTimer? _aiTimer;

    /// <summary>难度选项。</summary>
    public IReadOnlyList<OptionItem<string>> AiDifficultyOptions { get; } = OptionItem.From(
        AiDifficulties.All.Select(d => (AiDifficulties.ToKey(d), AiDifficulties.DisplayName(d))).ToArray());

    /// <summary>当前难度。</summary>
    public AiDifficulty AiDifficulty
    {
        get => _aiDifficulty;
        set
        {
            if (!Set(ref _aiDifficulty, value)) return;
            OnPropertyChanged(nameof(SelectedAiDifficulty));
            OnPropertyChanged(nameof(AiDifficultyHint));
            Cfg.AiDifficulty = AiDifficulties.ToKey(value);
        }
    }

    /// <summary>难度下拉选中项。</summary>
    public OptionItem<string> SelectedAiDifficulty
    {
        get => OptionItem<string>.Find(AiDifficultyOptions, AiDifficulties.ToKey(_aiDifficulty)) ?? AiDifficultyOptions[1];
        set
        {
            if (value is null) return;
            AiDifficulty = AiDifficulties.Parse(value.Value);
        }
    }

    /// <summary>难度说明。</summary>
    public string AiDifficultyHint => AiDifficulties.Description(_aiDifficulty);

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
        : $"电脑对手：{AiDifficulties.DisplayName(_aiDifficulty)}（代替联机对手）";

    /// <summary>AI 面板是否显示（联机模式下才有意义）。</summary>
    public bool AiPanelVisible => IsNetMode;

    /// <summary>初始化（主构造函数里调用）。</summary>
    private void InitializeAi()
    {
        _aiTimer = new DispatcherTimer();
        _aiTimer.Tick += OnAiTick;
        _aiDifficulty = AiDifficulties.Parse(Cfg.AiDifficulty);
        _aiEnabled = Cfg.AiEnabled;
    }

    private void StartAiLoop()
    {
        if (!_aiEnabled || _aiTimer is null) return;
        if (!IsNetMode) return;
        if (!_aiTimer.IsEnabled) _aiTimer.Start();
    }

    private void StopAiLoop()
    {
        if (_aiTimer is { IsEnabled: true }) _aiTimer.Stop();
    }

    /// <summary>
    /// AI 定时器：每 tick 让「客户端那份引擎」落一块。
    /// <para>房主开着电脑对手时就等于自己在和电脑打；节奏按难度定，看起来像真人。</para>
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

        // 按方案：先转到目标朝向，再横移到目标列，最后硬降
        var guard = 0;
        while (engine.Current is { } piece && TetrisPieces.NormalizeRotation(piece.Rotation) != chosen.Rotation
               && guard++ < 6)
        {
            var rotated = engine.Rotate(clockwise: true);
            if (!rotated.Success) break;                       // 转不动就放弃
        }

        guard = 0;
        while (engine.Current is { } piece && piece.X != chosen.X && guard++ < 20)
        {
            var moved = piece.X < chosen.X ? engine.MoveRight() : engine.MoveLeft();
            if (!moved) break;
        }

        var result = engine.HardDrop();

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

        if (engine.IsGameOver) FinishNetGame();
        else PublishNetState();
    }
}
