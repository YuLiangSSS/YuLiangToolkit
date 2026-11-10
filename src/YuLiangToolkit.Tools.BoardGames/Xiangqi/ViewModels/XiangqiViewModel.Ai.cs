using System.Windows.Threading;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi.ViewModels;

/// <summary>
/// 中国象棋的「电脑对手」部分（partial 扩展，不动原有联机 / 同机双人逻辑）。
///
/// <para>跟五子棋同一套做法：<b>只在同机模式</b>生效，打开开关后轮到电脑那一方时由 AI 自己走。
/// 人执红先手，电脑执黑后手（象棋习惯）。</para>
/// </summary>
public sealed partial class XiangqiViewModel
{
    private AiDifficulty _aiDifficulty = AiDifficulty.Normal;
    private bool _aiEnabled;
    private bool _aiThinking;
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

    /// <summary>开电脑对手。</summary>
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

    /// <summary>状态文案。</summary>
    public string AiOpponentText => !_aiEnabled
        ? "未开电脑对手（同机双人）"
        : _aiThinking ? $"电脑（{AiDifficulties.DisplayName(_aiDifficulty)}）正在思考…"
        : $"电脑对手：{AiDifficulties.DisplayName(_aiDifficulty)}（执黑后手）";

    /// <summary>电脑执哪一方（黑 = 后手）。</summary>
    public XiangqiSide AiSide => XiangqiSide.Black;

    /// <summary>AI 是否在思考。</summary>
    public bool AiThinking => _aiThinking;

    /// <summary>初始化（主构造函数里调用）。</summary>
    private void InitializeAi()
    {
        _aiTimer = new DispatcherTimer();
        _aiTimer.Tick += OnAiTimerTick;
        _aiDifficulty = AiDifficulties.Parse(Cfg.AiDifficulty);
        _aiEnabled = Cfg.AiEnabled;
    }

    private void NotifyAiState()
    {
        OnPropertyChanged(nameof(AiEnabled));
        OnPropertyChanged(nameof(AiThinking));
        OnPropertyChanged(nameof(AiOpponentText));
    }

    /// <summary>轮到电脑就让它想一手（落子 / 开局后会调）。</summary>
    private void TryStartAiTurn()
    {
        if (!_aiEnabled || Mode != BoardPlayMode.Local) return;
        if (_aiTimer is null) return;
        if (_game.IsOver || _aiThinking) return;
        if (_game.Turn != AiSide) return;

        _aiThinking = true;
        NotifyAiState();
        _aiTimer.Interval = TimeSpan.FromMilliseconds(AiDifficulties.ThinkingDelayMs(_aiDifficulty));
        _aiTimer.Start();
    }

    private void OnAiTimerTick(object? sender, EventArgs e)
    {
        _aiTimer?.Stop();
        if (!_aiEnabled || Mode != BoardPlayMode.Local || _game.IsOver || _game.Turn != AiSide)
        {
            _aiThinking = false;
            NotifyAiState();
            return;
        }

        var move = XiangqiAi.ChooseMove(_game, _aiDifficulty);
        _aiThinking = false;
        NotifyAiState();

        if (move is not { } chosen) return;

        var captured = _game.Board.Get(chosen.ToX, chosen.ToY);
        var error = _game.Play(AiSide, chosen.FromX, chosen.FromY, chosen.ToX, chosen.ToY);
        if (error is not null) return;

        ClearSelection();
        RefreshAll();
        AnnounceAfterMove(captured);
        NotifyAiState();
    }
}
