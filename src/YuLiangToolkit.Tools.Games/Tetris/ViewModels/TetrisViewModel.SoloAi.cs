using System.Windows.Threading;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的<b>「单人 vs 电脑」</b>（partial 扩展）。
///
/// <para>开一局时另起一个引擎由 <see cref="TetrisAi"/> 操控，双方各自下沉、各自消行，
/// 界面上和联机对战长得一样（右边是对手棋盘）。</para>
///
/// <para><b>两种玩法</b>：勾上「垃圾行互攻」之后，双方各消 2 / 3 / 4 行就给对方底部塞
/// 1 / 2 / 4 行垃圾行，谁先被顶死谁输（和联机互攻同一套换算，见
/// <see cref="TetrisEngine.GarbageRowsFor"/>）；不勾就是各玩各的，只有分数对比。</para>
///
/// <para><b>速度</b>：AI 定时器的间隔取自 <see cref="TetrisConfig.AiMoveIntervalMs"/>
/// （设置里可调），难度只决定它聪不聪明、不再决定它多快。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    private TetrisEngine? _soloAiEngine;
    private DispatcherTimer? _soloAiTimer;
    private bool _soloAiEnabled;
    private string _soloOutcome = "";

    /// <summary>单人模式是否开启了电脑对手。</summary>
    public bool SoloAiEnabled
    {
        get => _soloAiEnabled;
        set
        {
            if (!Set(ref _soloAiEnabled, value)) return;
            Cfg.SoloAiEnabled = value;
            OnPropertyChanged(nameof(DuelPanelVisible));
            OnPropertyChanged(nameof(LeftBoardTitle));
            OnPropertyChanged(nameof(RightBoardTitle));
            OnPropertyChanged(nameof(DuelBoardHasOpponent));
            OnPropertyChanged(nameof(GarbageScoreVisible));
            OnPropertyChanged(nameof(MyGarbageText));

            if (value) StartSoloAiIfRunning();
            else StopSoloAi();

            SyncDuelBoard();
        }
    }

    /// <summary>单人 vs 电脑是不是也在打垃圾行互攻。</summary>
    public bool SoloGarbageMode
    {
        get => Cfg.SoloGarbageMode;
        set
        {
            if (Cfg.SoloGarbageMode == value) return;
            Cfg.SoloGarbageMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SoloGarbageHint));
            OnPropertyChanged(nameof(GarbageScoreVisible));
            OnPropertyChanged(nameof(MyGarbageText));

            // 改玩法就重开一局（不然两边的欠账对不上，玩起来像出 bug）
            if (SoloAiActive) NewGame();
        }
    }

    /// <summary>互攻开关下面的一行说明。</summary>
    public string SoloGarbageHint => Cfg.SoloGarbageMode
        ? "互攻：双方消 2 / 3 / 4 行分别给对方塞 1 / 2 / 4 行垃圾，谁先顶死谁输"
        : "各玩各的：只比分数，互不干扰（想互相塞垃圾就勾上）";

    /// <summary>这一局是不是「单人 vs 电脑」正在进行（垃圾行互攻只认它）。</summary>
    private bool SoloAiActive => !IsNetMode && _soloAiEnabled && _soloAiEngine is not null;

    /// <summary>单人互攻模式（垃圾行相关文案 / 面板据此显示）。</summary>
    private bool SoloGarbageActive => SoloAiActive && Cfg.SoloGarbageMode;

    /// <summary>单人队手是不是在跑（渲染用）。</summary>
    public bool SoloAiRunning => _soloAiTimer is { IsEnabled: true };

    /// <summary>单人模式下右侧是否显示对手棋盘。</summary>
    public bool SoloDuelVisible => !IsNetMode && _soloAiEnabled;

    /// <summary>这一局的输赢文案（空 = 还没结束）。</summary>
    public string SoloOutcomeText => _soloOutcome;

    /// <summary>初始化（主构造函数调用）。</summary>
    private void InitializeSoloAi()
    {
        _soloAiEnabled = Cfg.SoloAiEnabled;
        _soloAiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Cfg.AiMoveIntervalMs) };
        _soloAiTimer.Tick += OnSoloAiTick;
    }

    /// <summary>开局时启动电脑对手（单机、且开关打开）。</summary>
    private void StartSoloAiIfRunning()
    {
        ResetSoloAiEngine();
        UpdateSoloAiTimer();
    }

    /// <summary>重开一局时把电脑那份棋盘也重来（局终结果清空），随后由定时器同步决定起不起表。</summary>
    private void ResetSoloAiEngine()
    {
        if (IsNetMode || !_soloAiEnabled)
        {
            StopSoloAi();
            return;
        }

        _soloAiEngine = new TetrisEngine(new Random());
        _soloAiEngine.Reset(Cfg.StartLevel);
        _soloOutcome = "";
        OnPropertyChanged(nameof(SoloOutcomeText));
        OnPropertyChanged(nameof(MyGarbageText));
        OnPropertyChanged(nameof(SoloGarbageHint));
        SyncDuelBoardFromSoloAi(_soloAiEngine);
    }

    /// <summary>停掉电脑对手。</summary>
    private void StopSoloAi()
    {
        if (_soloAiTimer is { IsEnabled: true }) _soloAiTimer.Stop();
        _soloAiEngine = null;
        OnPropertyChanged(nameof(SoloAiRunning));
    }

    /// <summary>暂停 / 继续 / 切走时跟玩家那块棋盘一起停或起（AI 不会偷偷继续玩）。</summary>
    private void UpdateSoloAiTimer()
    {
        if (_soloAiTimer is null) return;

        var shouldRun = SoloAiActive && _started && !IsPaused && !_isAway && !_engine.IsGameOver;
        if (shouldRun)
        {
            ApplyAiInterval();
            if (!_soloAiTimer.IsEnabled) _soloAiTimer.Start();
        }
        else if (_soloAiTimer.IsEnabled)
        {
            _soloAiTimer.Stop();
        }

        OnPropertyChanged(nameof(SoloAiRunning));
    }

    /// <summary>电脑对手每 tick 落一块（间隔按设置，不再每帧）。</summary>
    private void OnSoloAiTick(object? sender, EventArgs e)
    {
        if (!SoloAiActive || _soloAiEngine is not { } engine || !_started || IsPaused)
        {
            UpdateSoloAiTimer();
            return;
        }

        if (engine.IsGameOver)
        {
            EndSoloAiMatch(playerWon: true);
            return;
        }

        var plan = TetrisAi.ChoosePlan(engine, _aiDifficulty, garbageFocused: Cfg.SoloGarbageMode);
        if (plan is not { } chosen)
        {
            engine.Lock();
            SyncDuelBoardFromSoloAi(engine);
            if (engine.IsGameOver) EndSoloAiMatch(playerWon: true);
            return;
        }

        var result = TetrisAi.ApplyPlan(engine, chosen);

        // 互攻：电脑消的行也照联机那套换算塞到玩家这边（欠着，等玩家下次落地再插进棋盘）
        if (Cfg.SoloGarbageMode && result.LinesCleared >= 2)
        {
            var rows = TetrisEngine.GarbageRowsFor(result.LinesCleared);
            _engine.EnqueueGarbage(rows);
            OnPropertyChanged(nameof(MyGarbageText));
            Log($"电脑消 {result.LinesCleared} 行，给你塞 {rows} 行垃圾");
        }

        SyncDuelBoardFromSoloAi(engine);

        if (engine.IsGameOver) EndSoloAiMatch(playerWon: true);
    }

    /// <summary>
    /// 玩家自己落地之后（单机）：互攻模式把垃圾行送给电脑，并同步对手区。
    /// <para>由 <c>AfterLock</c> 调用；不是单机对战就什么都不做。</para>
    /// </summary>
    private void AfterLockForSoloAi(TetrisLockResult result)
    {
        if (!SoloAiActive) return;

        if (Cfg.SoloGarbageMode && result.LinesCleared >= 2 && _soloAiEngine is { } ai && !ai.IsGameOver)
        {
            var rows = TetrisEngine.GarbageRowsFor(result.LinesCleared);
            ai.EnqueueGarbage(rows);
            Log($"你消 {result.LinesCleared} 行，给电脑塞 {rows} 行垃圾");
        }

        if (_soloAiEngine is { } engine)
        {
            SyncDuelBoardFromSoloAi(engine);
            if (engine.IsGameOver) EndSoloAiMatch(playerWon: true);
        }

        OnPropertyChanged(nameof(MyGarbageText));
    }

    /// <summary>单人 vs 电脑分出胜负：停表 + 写结果文案。</summary>
    /// <param name="playerWon">玩家赢了没有。</param>
    private void EndSoloAiMatch(bool playerWon)
    {
        if (_soloAiEngine is null) return;

        _soloOutcome = playerWon
            ? $"电脑顶死了 —— 你赢了！（你 {_engine.Score} 分 / 电脑 {_soloAiEngine.Score} 分）"
            : $"被电脑顶死了 —— 这局输了（你 {_engine.Score} 分 / 电脑 {_soloAiEngine.Score} 分）";

        StatusText = _soloOutcome;
        OnPropertyChanged(nameof(SoloOutcomeText));
        OnPropertyChanged(nameof(OverlayHint));
        Log(_soloOutcome);
        UpdateSoloAiTimer();
    }

    private void SyncDuelBoardFromSoloAi(TetrisEngine engine)
    {
        GuestBoardState = TetrisBoardViewState.FromSnapshot(engine.Snapshot());
        OnPropertyChanged(nameof(DuelBoardState));
        OnPropertyChanged(nameof(DuelBoardHasOpponent));
        OnPropertyChanged(nameof(OpponentGarbageText));
    }

    /// <summary>把「右侧对手区」在单机与联机之间切换好。</summary>
    private void SyncDuelBoard()
    {
        OnPropertyChanged(nameof(DuelPanelVisible));
        OnPropertyChanged(nameof(DuelBoardState));
        OnPropertyChanged(nameof(DuelBoardHasOpponent));
        OnPropertyChanged(nameof(GarbageScoreVisible));
        OnPropertyChanged(nameof(MyGarbageText));
    }
}
