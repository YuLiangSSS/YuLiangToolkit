using System.Collections.ObjectModel;
using System.Text.Json;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Doudizhu;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Doudizhu.ViewModels;

/// <summary>手牌里的一张（界面绑定：是否被选中 + 牌面）。</summary>
public sealed class DoudizhuCardItem : ObservableObject
{
    private bool _isSelected;

    /// <summary>创建一张手牌。</summary>
    /// <param name="card">牌。</param>
    public DoudizhuCardItem(Card card) => Card = card;

    /// <summary>牌。</summary>
    public Card Card { get; }

    /// <summary>牌面文本。</summary>
    public string Text => Card.Text;

    /// <summary>牌面（不含花色）。</summary>
    public string RankText => Card.RankText;

    /// <summary>是不是红色（界面用红字）。</summary>
    public bool IsRed => Card.IsRed;

    /// <summary>有没有选中（点一下切换）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

/// <summary>
/// 「斗地主」界面 VM：单机（同一台电脑三个人 / 一家电脑两家 AI 不现实，这里做「房主 + 两个真人」或单机演练）
/// 与联机共用一套显示逻辑。
///
/// <para><b>权威端</b>：联机时房主跑 <see cref="DoudizhuGame"/>；客户端只上报动作、按
/// <see cref="DoudizhuSnapshot"/> 渲染自己的手牌。单机演练时本端直接当房主。</para>
/// </summary>
public sealed class DoudizhuViewModel : MultiSeatRoomViewModelBase
{
    private readonly DoudizhuGame _game;
    private int _mySeat = -1;
    private string _statusText = "选择模式后点「开始一局」发牌";
    private string _resultText = "";
    private bool _showBottom;
    private string _selectedHint = "";

    /// <summary>创建 VM。</summary>
    public DoudizhuViewModel() : base(DoudizhuConfig.ToolId, DoudizhuSeats.Count, new DoudizhuConfig())
    {
        Cfg = (DoudizhuConfig)RoomConfig;
        _game = new DoudizhuGame(Cfg.ModeValue, new Random());

        StartGameCommand = new RelayCommand(_ => StartLocalGame());
        ToggleCardCommand = new RelayCommand(p =>
        {
            if (p is DoudizhuCardItem item) { item.IsSelected = !item.IsSelected; RefreshSelection(); }
        });
        PlayCommand = new RelayCommand(_ => PlaySelected(), _ => CanPlaySelected);
        PassCommand = new RelayCommand(_ => PassTurn(), _ => CanPass);
        HintCommand = new RelayCommand(_ => ShowHint());
        SortCommand = new RelayCommand(_ => RefreshHand());
        BidCommand = new RelayCommand(p => Bid(p));

        ModeOptions = OptionItem.From(
            ("classic", "经典"),
            ("doubleDeck", "双副牌（炸弹多）"),
            ("openBottom", "底牌明牌"));

        // 进页面不自动发牌：手牌在「开始一局」之后才有
        InitializeAi();          // 单机电脑对手
        _game.ResetToLobby();
        RefreshAll();
        StatusText = "点「开始一局」发牌（联机时房主点）";
    }

    /// <summary>配置。</summary>
    public DoudizhuConfig Cfg { get; }

    /// <summary>本端座位（-1 = 不在场上）。</summary>
    public int MySeat => _mySeat;

    /// <summary>手牌（界面绑定，选中状态在这里）。</summary>
    public ObservableCollection<DoudizhuCardItem> Hand { get; } = new();

    /// <summary>桌面上的牌。</summary>
    public ObservableCollection<DoudizhuCardItem> TableCards { get; } = new();

    /// <summary>底牌。</summary>
    public ObservableCollection<DoudizhuCardItem> BottomCards { get; } = new();

    /// <summary>三个座位的显示文案。</summary>
    public ObservableCollection<string> SeatTexts { get; } = new();

    /// <summary>发牌模式选项。</summary>
    public IReadOnlyList<OptionItem<string>> ModeOptions { get; }

    /// <summary>当前模式选中项。</summary>
    public OptionItem<string> SelectedMode
    {
        get => OptionItem<string>.Find(ModeOptions, DoudizhuConfig.ModeToKey(Cfg.ModeValue)) ?? ModeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Mode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeDescriptionText));
        }
    }

    /// <summary>模式说明。</summary>
    public string ModeDescriptionText => DoudizhuConfig.ModeDescription(Cfg.ModeValue);

    /// <summary>状态一句话。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>结算 / 结果文案。</summary>
    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    /// <summary>选牌提示（「三带一」「压不过」之类）。</summary>
    public string SelectedHint
    {
        get => _selectedHint;
        private set => Set(ref _selectedHint, value);
    }

    /// <summary>能不能出牌。</summary>
    public bool CanPlaySelected => _game.Phase == DoudizhuPhase.Playing && _selectedPattern.IsValid && CanPlayNow;

    /// <summary>能不能过牌。</summary>
    public bool CanPass => _game.Phase == DoudizhuPhase.Playing && CanPlayNow && _game.TablePattern is not null;

    /// <summary>现在是不是轮到本端。</summary>
    public bool CanPlayNow => _mySeat >= 0 && _game.CurrentSeat == _mySeat;

    /// <summary>是不是叫牌阶段。</summary>
    public bool IsBidding => _game.Phase == DoudizhuPhase.Bidding;

    /// <summary>能不能叫牌。</summary>
    public bool CanBid => IsBidding && CanPlayNow;

    /// <summary>游戏结束。</summary>
    public bool IsOver => _game.Phase == DoudizhuPhase.Over;

    /// <summary>显示底牌区。</summary>
    public bool ShowBottom
    {
        get => _showBottom;
        private set => Set(ref _showBottom, value);
    }

    // ---------------------------------------------------------------- 命令

    public RelayCommand StartGameCommand { get; }
    public RelayCommand ToggleCardCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand PassCommand { get; }
    public RelayCommand HintCommand { get; }
    public RelayCommand SortCommand { get; }
    public RelayCommand BidCommand { get; }

    private CardPattern _selectedPattern = CardPattern.Invalid;

    // ---------------------------------------------------------------- 开局

    /// <summary>开始一局（单机 / 房主）。</summary>
    public void StartLocalGame()
    {
        _game.Reset();
        _mySeat = IsHost || !IsOnline ? 0 : MySeat;
        ResultText = "";
        ShowBottom = _game.BottomRevealed;
        StatusText = "发牌完成，叫地主（1 / 2 / 3 分或不叫）";
        RefreshAll();
        BroadcastStateFromRoom();
        TryRunAiTurn(allowBid: true);
    }

    private void Bid(object? parameter)
    {
        if (!CanBid) return;
        var bid = parameter switch
        {
            DoudizhuBidKind kind => kind,
            int value => (DoudizhuBidKind)Math.Clamp(value, 0, 3),
            string text when int.TryParse(text, out var value) => (DoudizhuBidKind)Math.Clamp(value, 0, 3),
            _ => DoudizhuBidKind.Pass,
        };

        // 客户端：只上报动作，等房主算完回快照
        if (IsClient) { SendBid((int)bid); return; }

        var error = _game.Bid(_mySeat, bid);
        if (error.Length > 0) { SelectedHint = error; return; }

        StatusText = _game.Phase == DoudizhuPhase.Playing ? "地主已产生，开始出牌" : "等待其他人叫牌";
        ShowBottom = _game.BottomRevealed;
        RefreshAll();
        BroadcastStateFromRoom();
        TryRunAiTurn(allowBid: true);
    }

    private void PlaySelected()
    {
        if (!CanPlayNow) return;
        var cards = Hand.Where(c => c.IsSelected).Select(c => c.Card).ToList();
        if (cards.Count == 0) { SelectedHint = "先点选要出的牌"; return; }

        // 客户端：只上报动作
        if (IsClient) { SendPlay(cards); return; }

        var error = _game.PlayCards(_mySeat, cards);
        if (error.Length > 0) { SelectedHint = error; return; }

        SelectedHint = "";
        AfterGameStep();
        TryRunAiTurn(allowBid: false);
    }

    private void PassTurn()
    {
        if (!CanPass) return;

        if (IsClient) { SendPass(); return; }

        var error = _game.Pass(_mySeat);
        if (error.Length > 0) { SelectedHint = error; return; }
        SelectedHint = "";
        AfterGameStep();
        TryRunAiTurn(allowBid: false);
    }

    private void AfterGameStep()
    {
        RefreshAll();
        if (_game.Phase == DoudizhuPhase.Over && _game.Result is { } result)
        {
            ResultText = result.Text + $"（倍数 ×{result.Multiplier}）";
            StatusText = result.Text;
            var winners = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var seat = 0; seat < DoudizhuSeats.Count; seat++)
            {
                var id = SeatMemberId(seat);
                if (id.Length == 0) continue;
                winners[id] = _game.IsLandlord(seat) == result.LandlordWon;
            }
            ApplyRotationAfterGame(winners);
        }
        else
        {
            StatusText = _game.CurrentSeat == _mySeat ? "轮到你出牌" : $"等待第 {_game.CurrentSeat + 1} 家出牌";
        }
        BroadcastStateFromRoom();
    }

    private void ShowHint()
    {
        var patterns = CardPatternRules.SuggestPatterns(Hand.Select(c => c.Card).ToList(), _game.TablePattern);
        SelectedHint = patterns.Count == 0
            ? "没有能压过的牌，建议过牌"
            : $"可出：{string.Join("、", patterns.Take(6).Select(p => $"{p.DisplayName}{(p.Key > 0 ? p.Key.ToString() : "")}"))}";
    }

    private void RefreshSelection()
    {
        var cards = Hand.Where(c => c.IsSelected).Select(c => c.Card).ToList();
        _selectedPattern = cards.Count == 0 ? CardPattern.Invalid : CardPatternRules.Detect(cards);
        SelectedHint = cards.Count == 0
            ? ""
            : _selectedPattern.IsValid ? _selectedPattern.DisplayName : "这不是合法牌型";
        OnPropertyChanged(nameof(CanPlaySelected));
    }

    // ---------------------------------------------------------------- 刷新

    private void RefreshHand()
    {
        var selected = new HashSet<string>(Hand.Where(c => c.IsSelected).Select(c => Key(c.Card)));
        Hand.Clear();
        foreach (var card in _game.Hand(_mySeat >= 0 ? _mySeat : 0))
        {
            var item = new DoudizhuCardItem(card) { IsSelected = selected.Contains(Key(card)) };
            Hand.Add(item);
        }

        static string Key(Card card) => $"{card.Rank}-{card.Suit}-{card.Deck}";
    }

    private void RefreshAll()
    {
        if (_mySeat < 0 && (IsHost || !IsOnline)) _mySeat = 0;
        RefreshHand();

        TableCards.Clear();
        foreach (var card in _game.TableCards) TableCards.Add(new DoudizhuCardItem(card));

        BottomCards.Clear();
        foreach (var card in _game.Bottom) BottomCards.Add(new DoudizhuCardItem(card));

        SeatTexts.Clear();
        for (var seat = 0; seat < DoudizhuSeats.Count; seat++)
        {
            var name = SeatName(seat);
            var tags = new List<string> { $"{seat + 1}号位 {name}" };
            if (seat == _mySeat) tags.Add("（你）");
            if (_game.IsLandlord(seat)) tags.Add("地主");
            tags.Add($"剩 {_game.HandCount(seat)} 张");
            if (_game.CurrentSeat == seat && _game.Phase != DoudizhuPhase.Over) tags.Add("▶ 轮到");
            SeatTexts.Add(string.Join(" · ", tags));
        }

        OnPropertyChanged(nameof(CanPlayNow));
        OnPropertyChanged(nameof(CanPlaySelected));
        OnPropertyChanged(nameof(CanPass));
        OnPropertyChanged(nameof(CanBid));
        OnPropertyChanged(nameof(IsBidding));
        OnPropertyChanged(nameof(IsOver));
        OnPropertyChanged(nameof(MySeat));
        OnPropertyChanged(nameof(SelectedMode));
    }

    // ---------------------------------------------------------------- 房间基类钩子

    /// <inheritdoc />
    protected override string GameDisplayName => "斗地主";

    /// <inheritdoc />
    protected override void UpdateGameSeats()
    {
        _mySeat = MySeatIndex >= 0 ? MySeatIndex : (IsHost ? 0 : -1);
        RefreshAll();
    }

    /// <inheritdoc />
    protected override JsonElement BuildStateFor(string memberId)
    {
        var seat = -1;
        for (var i = 0; i < DoudizhuSeats.Count; i++)
        {
            if (string.Equals(SeatMemberId(i), memberId, StringComparison.Ordinal)) { seat = i; break; }
        }

        var state = new DoudizhuNetState
        {
            Seq = NextRoomSeq(),
            ForMemberId = memberId,
            Seat = seat,
            SeatMemberIds = Enumerable.Range(0, DoudizhuSeats.Count).Select(SeatMemberId).ToArray(),
            SeatNames = Enumerable.Range(0, DoudizhuSeats.Count).Select(SeatName).ToArray(),
            // 不在场上（排队中）的人也要能看牌局：给座位 0 的「观战版」快照（手牌换成 0 张）
            Snapshot = seat >= 0 ? _game.SnapshotFor(seat) : SpectatorSnapshot(),
            AutoRotateMode = AutoRotateModeKey,
            WaitingNames = WaitingMembers.Select(m => m.Name).ToArray(),
        };

        return ToJson(state);
    }

    /// <summary>排队中的人看牌局：座位 0 的快照但清空手牌（不给看任何人的牌）。</summary>
    private DoudizhuSnapshot SpectatorSnapshot()
    {
        var snapshot = _game.SnapshotFor(0);
        snapshot.Hand = new List<Card>();
        snapshot.Seat = -1;
        return snapshot;
    }

    /// <inheritdoc />
    protected override void ApplyState(JsonElement envelope)
    {
        var state = FromJson<DoudizhuNetState>(envelope);
        if (state?.Snapshot is not { } snapshot) return;

        _mySeat = state.Seat;
        ApplySnapshot(snapshot);
        StatusText = snapshot.Phase switch
        {
            DoudizhuPhase.Bidding => "叫地主中",
            DoudizhuPhase.Playing => snapshot.CurrentSeat == _mySeat ? "轮到你出牌" : "等待其他人出牌",
            DoudizhuPhase.Over => snapshot.Result?.Text ?? "本局结束",
            _ => "等待开始",
        };
        if (snapshot.Phase == DoudizhuPhase.Over && snapshot.Result is { } result)
            ResultText = result.Text + $"（倍数 ×{result.Multiplier}）";

        RefreshAll();
    }

    private void ApplySnapshot(DoudizhuSnapshot snapshot)
    {
        _game.ApplySnapshot(snapshot);
        ShowBottom = snapshot.Bottom.Count > 0;
    }

    /// <inheritdoc />
    protected override void HandleAction(string memberId, string type, JsonElement? payload)
    {
        var seat = -1;
        for (var i = 0; i < DoudizhuSeats.Count; i++)
        {
            if (string.Equals(SeatMemberId(i), memberId, StringComparison.Ordinal)) { seat = i; break; }
        }
        if (seat < 0) return;

        switch (type)
        {
            case "bid":
            {
                var value = payload is { } p && p.TryGetProperty("value", out var v) ? v.GetInt32() : 0;
                _game.Bid(seat, (DoudizhuBidKind)Math.Clamp(value, 0, 3));
                break;
            }
            case "play":
            {
                var cards = payload is { } p && p.TryGetProperty("cards", out var arr)
                    ? arr.Deserialize<List<Card>>(YuLiangToolkit.Core.Net.RoomProtocol.JsonOptions) ?? new List<Card>()
                    : new List<Card>();
                _game.PlayCards(seat, cards);
                break;
            }
            case "pass":
                _game.Pass(seat);
                break;
            default:
                return;
        }

        if (_game.Phase == DoudizhuPhase.Over && _game.Result is { } result)
        {
            var winners = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var i = 0; i < DoudizhuSeats.Count; i++)
            {
                var id = SeatMemberId(i);
                if (id.Length > 0) winners[id] = _game.IsLandlord(i) == result.LandlordWon;
            }
            ApplyRotationAfterGame(winners);
        }

        RefreshAll();
        BroadcastStateFromRoom();
    }

    /// <summary>客户端：出牌（上报给房主）。</summary>
    private void SendPlay(IReadOnlyList<Card> cards)
        => _ = Session?.SendActionAsync("play", new { cards = cards.ToArray() });

    /// <summary>客户端：叫牌。</summary>
    private void SendBid(int value) => _ = Session?.SendActionAsync("bid", new { value });

    /// <summary>客户端：过牌。</summary>
    private void SendPass() => _ = Session?.SendActionAsync("pass");

    // ---------------------------------------------------------------- 电脑对手（单机）

    private AiDifficulty _aiDifficulty = AiDifficulty.Normal;
    private bool _aiEnabled = true;
    private bool _aiBusy;

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

    /// <summary>单机时另外两家由电脑接管。</summary>
    public bool AiEnabled
    {
        get => _aiEnabled;
        set
        {
            if (!Set(ref _aiEnabled, value)) return;
            Cfg.AiEnabled = value;
            OnPropertyChanged(nameof(AiOpponentText));
        }
    }

    /// <summary>状态文案。</summary>
    public string AiOpponentText => _aiEnabled
        ? $"单机：你 vs 两个电脑（{AiDifficulties.DisplayName(_aiDifficulty)}）"
        : "单机：三家轮流用这台电脑操作";

    /// <summary>电脑是否正在思考。</summary>
    public bool AiThinking => _aiBusy;

    /// <summary>初始化（主构造函数里调用）。</summary>
    private void InitializeAi()
    {
        _aiDifficulty = AiDifficulties.Parse(Cfg.AiDifficulty);
        _aiEnabled = Cfg.AiEnabled;
    }

    /// <summary>
    /// 单机时如果轮到的不是本端座位，就让电脑接管这一手。
    /// <paramref name="allowBid"/> = 现在是叫牌阶段。
    /// </summary>
    private void TryRunAiTurn(bool allowBid)
    {
        if (!_aiEnabled || IsOnline) return;               // 联机时不接管
        if (_aiBusy) return;
        if (_game.Phase is DoudizhuPhase.Over or DoudizhuPhase.Waiting) return;
        if (_game.CurrentSeat == _mySeat) return;          // 轮到自己

        var seat = _game.CurrentSeat;
        _aiBusy = true;
        OnPropertyChanged(nameof(AiThinking));

        var delay = AiDifficulties.ThinkingDelayMs(_aiDifficulty);
        _ = Task.Delay(delay).ContinueWith(_ => Ui(() => RunAiAction(seat, allowBid)));
    }

    private void RunAiAction(int seat, bool allowBid)
    {
        try
        {
            if (_game.Phase == DoudizhuPhase.Bidding && allowBid)
            {
                var bid = DoudizhuAi.DecideBid(_game.Hand(seat).ToList(), _game.BidScore, _aiDifficulty);
                _game.Bid(seat, bid);
            }
            else if (_game.Phase == DoudizhuPhase.Playing)
            {
                var cards = DoudizhuAi.DecidePlay(
                    _game.Hand(seat).ToList(), _game.TablePattern, _game.TableCards, _aiDifficulty);

                if (cards.Count == 0 || _game.PlayCards(seat, cards).Length > 0)
                {
                    // 出不了 / 出失败：过牌（先手时不能过，那就随便出一张）
                    if (_game.Pass(seat).Length > 0 && _game.Hand(seat).Count > 0)
                        _game.PlayCards(seat, new[] { _game.Hand(seat)[0] });
                }
            }

            if (_game.Phase == DoudizhuPhase.Over && _game.Result is { } result)
            {
                ResultText = result.Text + $"（倍数 ×{result.Multiplier}）";
                StatusText = result.Text;
            }
            else
            {
                StatusText = _game.CurrentSeat == _mySeat
                    ? (_game.Phase == DoudizhuPhase.Bidding ? "轮到你叫地主" : "轮到你出牌")
                    : "电脑思考中…";
            }

            RefreshAll();
        }
        finally
        {
            _aiBusy = false;
            OnPropertyChanged(nameof(AiThinking));
        }

        // 可能又轮到下一家电脑
        TryRunAiTurn(allowBid);
    }
}
