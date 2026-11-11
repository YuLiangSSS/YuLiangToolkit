using System.Windows.Threading;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 俄罗斯方块的<b>「单人 vs 电脑」</b>（partial 扩展）。
///
/// <para>之前 AI 只在联机模式里代替客户端那份棋盘；这里补上真正的<b>单机人机对战</b>：
/// 开一局时另起一个引擎由 <see cref="TetrisAi"/> 操控，双方各自下沉、各自消行，
/// 界面上和联机对战长得一样（右边是对手棋盘）。</para>
/// </summary>
public sealed partial class TetrisViewModel
{
    private TetrisEngine? _soloAiEngine;
    private DispatcherTimer? _soloAiTimer;
    private bool _soloAiEnabled;

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

            if (value) StartSoloAiIfRunning();
            else StopSoloAi();

            SyncDuelBoard();
        }
    }

    /// <summary>单人队手是不是在跑（渲染用）。</summary>
    public bool SoloAiRunning => _soloAiTimer is { IsEnabled: true };

    /// <summary>单人模式下右侧是否显示对手棋盘。</summary>
    public bool SoloDuelVisible => !IsNetMode && _soloAiEnabled;

    /// <summary>初始化（主构造函数调用）。</summary>
    private void InitializeSoloAi()
    {
        _soloAiEnabled = Cfg.SoloAiEnabled;
        _soloAiTimer = new DispatcherTimer();
        _soloAiTimer.Tick += OnSoloAiTick;
    }

    /// <summary>开局时启动电脑对手（单机、且开关打开）。</summary>
    private void StartSoloAiIfRunning()
    {
        if (IsNetMode || !_soloAiEnabled || _soloAiTimer is null) return;

        _soloAiEngine = new TetrisEngine(new Random());
        _soloAiEngine.Reset(Cfg.StartLevel);
        _soloAiTimer.Start();
        OnPropertyChanged(nameof(SoloAiRunning));
    }

    /// <summary>停掉电脑对手。</summary>
    private void StopSoloAi()
    {
        if (_soloAiTimer is { IsEnabled: true }) _soloAiTimer.Stop();
        _soloAiEngine = null;
        OnPropertyChanged(nameof(SoloAiRunning));
    }

    /// <summary>电脑对手每 tick 落一块（节奏按难度定）。</summary>
    private void OnSoloAiTick(object? sender, EventArgs e)
    {
        if (IsNetMode || !_soloAiEnabled || _soloAiEngine is not { } engine)
        {
            StopSoloAi();
            return;
        }

        if (engine.IsGameOver)
        {
            StopSoloAi();
            return;
        }

        var plan = TetrisAi.ChoosePlan(engine, _aiDifficulty, garbageFocused: false);
        if (plan is not { } chosen)
        {
            engine.Lock();
            SyncDuelBoardFromSoloAi(engine);
            return;
        }

        var guard = 0;
        while (engine.Current is { } piece && TetrisPieces.NormalizeRotation(piece.Rotation) != chosen.Rotation
               && guard++ < 6)
        {
            if (!engine.Rotate(clockwise: true).Success) break;
        }

        guard = 0;
        while (engine.Current is { } piece && piece.X != chosen.X && guard++ < 20)
        {
            var moved = piece.X < chosen.X ? engine.MoveRight() : engine.MoveLeft();
            if (!moved) break;
        }

        engine.HardDrop();
        SyncDuelBoardFromSoloAi(engine);
    }

    private void SyncDuelBoardFromSoloAi(TetrisEngine engine)
    {
        GuestBoardState = TetrisBoardViewState.FromSnapshot(engine.Snapshot());
        OnPropertyChanged(nameof(DuelBoardState));
        OnPropertyChanged(nameof(DuelBoardHasOpponent));
    }

    /// <summary>把「右侧对手区」在单机与联机之间切换好。</summary>
    private void SyncDuelBoard()
    {
        OnPropertyChanged(nameof(DuelPanelVisible));
        OnPropertyChanged(nameof(DuelBoardState));
        OnPropertyChanged(nameof(DuelBoardHasOpponent));
    }
}
