using System.Windows.Threading;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Gomoku;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku.ViewModels;

/// <summary>
/// 五子棋的「电脑对手」部分（partial 扩展，不动原有联机 / 同机双人逻辑）。
///
/// <para><b>怎么接进来</b>：同机双人模式（<see cref="BoardGameRoomViewModelBase.IsLocalMode"/>）下，
/// 如果打开了「电脑对手」，那么轮到 AI 那一方时，界面落子会被拦住、由 AI 自己算一手再落。
/// 这样不需要改动房间 / 联机的任何路径，单机玩就是「我 + 电脑」。</para>
/// </summary>
public sealed partial class GomokuViewModel
{
    private AiDifficulty _aiDifficulty = AiDifficulty.Normal;
    private bool _aiEnabled;
    private bool _aiThinking;
    private readonly DispatcherTimer _aiTimer;
    /// <summary>电脑对手的难度选项（界面下拉）。</summary>
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

    /// <summary>是不是开了电脑对手。</summary>
    public bool AiEnabled
    {
        get => _aiEnabled;
        set
        {
            if (!Set(ref _aiEnabled, value)) return;
            Cfg.AiEnabled = value;
            OnPropertyChanged(nameof(AiOpponentText));
            if (value) TryStartAiTurn();
        }
    }

    /// <summary>电脑对手状态文案。</summary>
    public string AiOpponentText => !_aiEnabled
        ? "未开电脑对手（同机双人）"
        : _aiThinking ? $"电脑（{AiDifficulties.DisplayName(_aiDifficulty)}）正在思考…"
        : $"电脑对手：{AiDifficulties.DisplayName(_aiDifficulty)}（执白后手）";

    /// <summary>电脑执哪一方（固定后手白棋，人执黑先手）。</summary>
    public GomokuCell AiSide => GomokuCell.White;

    /// <summary>电脑对手相关的状态变化（界面刷新用）。</summary>
    private void NotifyAiState()
    {
        OnPropertyChanged(nameof(AiEnabled));
        OnPropertyChanged(nameof(AiThinking));
        OnPropertyChanged(nameof(AiOpponentText));
    }

    /// <summary>AI 是否正在思考。</summary>
    public bool AiThinking => _aiThinking;

    /// <summary>
    /// 如果现在轮到电脑，就让电脑想一手再落子。
    /// <b>由 <see cref="PlaceAt"/> / 开局 / 悔棋之后调用</b>（在这些地方各加一行即可）。
    /// </summary>
    private void TryStartAiTurn()
    {
        if (!_aiEnabled || !IsLocalMode) return;
        if (!_game.Started || _game.IsOver || _aiThinking) return;
        if (_game.Turn != AiSide) return;

        _aiThinking = true;
        NotifyAiState();

        // 拟人延迟：不让它瞬间落子
        _aiTimer.Interval = TimeSpan.FromMilliseconds(AiDifficulties.ThinkingDelayMs(_aiDifficulty));
        _aiTimer.Start();
    }

    private void OnAiTimerTick(object? sender, EventArgs e)
    {
        _aiTimer.Stop();
        if (!_aiEnabled || !IsLocalMode) { _aiThinking = false; NotifyAiState(); return; }

        if (!_game.Started || _game.IsOver || _game.Turn != AiSide)
        {
            _aiThinking = false;
            NotifyAiState();
            return;
        }

        var point = GomokuAi.ChooseMove(_game, _aiDifficulty);
        _aiThinking = false;
        NotifyAiState();

        if (point is null) return;             // 没地方下了（棋盘满），由界面提示和棋

        var error = _game.Play(AiSide, point.Value.X, point.Value.Y);
        if (error is not null)
        {
            // 极端情况（AI 算出禁手点被拒）：换一个随机空点重试一次
            var fallback = FirstEmptyPoint();
            if (fallback is { } p) _game.Play(AiSide, p.X, p.Y);
        }

        RefreshAll();
        NotifyAiState();
    }

    /// <summary>随便找一个空点（兜底用）。</summary>
    private GomokuPoint? FirstEmptyPoint()
    {
        for (var y = 0; y < _game.Size; y++)
        {
            for (var x = 0; x < _game.Size; x++)
            {
                if (_game.Board.Get(x, y) == GomokuCell.Empty) return new GomokuPoint(x, y);
            }
        }
        return null;
    }
}
