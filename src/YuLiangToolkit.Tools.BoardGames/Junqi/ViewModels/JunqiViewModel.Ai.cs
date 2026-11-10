using System.Windows.Threading;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;

/// <summary>
/// 军棋的「电脑对手」部分（partial 扩展）：单机时对面那一方由 AI 接管。
///
/// <para>AI 只知道自己该知道的（对手暗棋不可见），所以它会保守地试探，
/// 这跟真人玩暗棋的感觉一致。</para>
/// </summary>
public sealed partial class JunqiViewModel
{
    private AiDifficulty _aiDifficulty = AiDifficulty.Normal;
    private bool _aiEnabled = true;
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
        : $"电脑对手：{AiDifficulties.DisplayName(_aiDifficulty)}（执蓝后手）";

    /// <summary>电脑执哪一方（蓝 = 后手）。</summary>
    public JunqiSide AiSide => JunqiSide.Blue;

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

    /// <summary>轮到电脑就让它想一手。</summary>
    private void TryStartAiTurn()
    {
        if (!_aiEnabled || IsOnline) return;              // 联机时不接管
        if (_aiTimer is null || _aiThinking) return;
        if (_game.Phase != JunqiPhase.Playing) return;
        if (_game.CurrentSide != AiSide) return;

        _aiThinking = true;
        OnPropertyChanged(nameof(AiThinking));
        OnPropertyChanged(nameof(AiOpponentText));
        _aiTimer.Interval = TimeSpan.FromMilliseconds(AiDifficulties.ThinkingDelayMs(_aiDifficulty));
        _aiTimer.Start();
    }

    private void OnAiTimerTick(object? sender, EventArgs e)
    {
        _aiTimer?.Stop();
        if (!_aiEnabled || IsOnline || _game.Phase != JunqiPhase.Playing || _game.CurrentSide != AiSide)
        {
            _aiThinking = false;
            OnPropertyChanged(nameof(AiThinking));
            OnPropertyChanged(nameof(AiOpponentText));
            return;
        }

        var move = JunqiAi.ChooseMove(_game, AiSide, _aiDifficulty);
        _aiThinking = false;
        OnPropertyChanged(nameof(AiThinking));
        OnPropertyChanged(nameof(AiOpponentText));

        if (move is not { } chosen) return;

        var error = _game.Move(AiSide, chosen.FromX, chosen.FromY, chosen.ToX, chosen.ToY);
        if (error.Length > 0)
        {
            // AI 算出的着法被拒（理论上不该发生）：换一个合法着法兜底
            var fallback = JunqiAi.AllMoves(_game, AiSide).FirstOrDefault();
            _game.Move(AiSide, fallback.FromX, fallback.FromY, fallback.ToX, fallback.ToY);
        }

        AfterMove();
    }
}
