namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>发牌模式。</summary>
public enum DoudizhuMode
{
    /// <summary>经典：一副牌 54 张，3 人各 17 张 + 3 张底牌。</summary>
    Classic = 0,

    /// <summary>双副牌（炸弹多）：两副牌 108 张，3 人各 36 张（没底牌），4 个王 = 2 个王炸。</summary>
    DoubleDeck = 1,

    /// <summary>底牌明牌：经典发牌，但 3 张底牌一开始就摊开给所有人看。</summary>
    OpenBottom = 2,
}

/// <summary>叫牌阶段（经典：抢地主，叫 1 / 2 / 3 分，叫 3 分直接定）。</summary>
public enum DoudizhuBidKind
{
    /// <summary>不叫。</summary>
    Pass = 0,

    /// <summary>叫 1 分。</summary>
    One = 1,

    /// <summary>叫 2 分。</summary>
    Two = 2,

    /// <summary>叫 3 分（没人再叫就是地主，且直接结束叫牌）。</summary>
    Three = 3,
}

/// <summary>一局斗地主的阶段。</summary>
public enum DoudizhuPhase
{
    /// <summary>还没发牌 / 等待开始。</summary>
    Waiting = 0,

    /// <summary>叫地主中。</summary>
    Bidding = 1,

    /// <summary>打牌中。</summary>
    Playing = 2,

    /// <summary>这一局结束了。</summary>
    Over = 3,
}

/// <summary>座位（固定三家，0 是发牌后先叫牌的那家）。</summary>
public static class DoudizhuSeats
{
    /// <summary>座位数（斗地主固定 3 家）。</summary>
    public const int Count = 3;

    /// <summary>把座位号归一化到 0~2（负数 / 超界都能收敛）。</summary>
    public static int Normalize(int seat)
    {
        var n = seat % Count;
        return n < 0 ? n + Count : n;
    }

    /// <summary>下一个座位（顺时针）。</summary>
    public static int Next(int seat) => Normalize(seat + 1);

    /// <summary>座位的中文名（「我 / 下家 / 上家」由界面按自己的座位换算，这里给通用的「第 N 家」）。</summary>
    public static string DisplayName(int seat) => Normalize(seat) switch
    {
        0 => "第一家",
        1 => "第二家",
        _ => "第三家",
    };
}

/// <summary>某一次出牌的记录（给界面显示「谁出了什么」用）。</summary>
/// <param name="Seat">出牌的座位；-1 = 这一轮过牌。</param>
/// <param name="Cards">出的牌（过牌时为空）。</param>
/// <param name="IsPass">是不是过牌。</param>
public readonly record struct DoudizhuPlayRecord(int Seat, IReadOnlyList<Card> Cards, bool IsPass);

/// <summary>
/// 一局斗地主的<b>完整规则状态机</b>（纯逻辑，不碰界面 / 网络，自检可直接跑完整局）。
///
/// <para><b>流程</b>：<see cref="Deal"/> 发牌 → <see cref="Bid"/> 叫分（1/2/3 分或过）
/// → 定地主、拿底牌 → <see cref="PlayCards"/> / <see cref="Pass"/> 出牌 → 有人出完牌 → <see cref="DoudizhuSettlement"/> 结算。</para>
///
/// <para><b>权威端</b>：联机时只有房主跑这个类，客户端只收快照（手牌按座位各发各的，
/// <see cref="SnapshotFor"/> 会隐藏别人的手牌）。</para>
/// </summary>
public sealed class DoudizhuGame
{
    /// <summary>单副牌每人张数。</summary>
    public const int ClassicHandSize = 17;

    /// <summary>经典底牌张数。</summary>
    public const int BottomSize = 3;

    private readonly List<Card>[] _hands = { new(), new(), new() };
    private readonly List<Card> _bottom = new();
    private readonly List<Card> _played = new();
    private readonly Random _random;

    /// <summary>创建一局（还没发牌）。</summary>
    /// <param name="mode">发牌模式。</param>
    /// <param name="random">随机源（传固定种子可复现，自检用）。</param>
    public DoudizhuGame(DoudizhuMode mode = DoudizhuMode.Classic, Random? random = null)
    {
        Mode = mode;
        _random = random ?? new Random();
        Reset();
    }

    // ---------------------------------------------------------------- 状态

    /// <summary>发牌模式。</summary>
    public DoudizhuMode Mode { get; }

    /// <summary>当前阶段。</summary>
    public DoudizhuPhase Phase { get; private set; } = DoudizhuPhase.Waiting;

    /// <summary>本轮第一手牌（谁先叫到地主谁先出）。</summary>
    public int LandlordSeat { get; private set; } = -1;

    /// <summary>地主叫的分数（1~3；还没定地主时为 0）。</summary>
    public int BidScore { get; private set; }

    /// <summary>当前轮到谁。</summary>
    public int CurrentSeat { get; private set; }

    /// <summary>桌面上的牌（这一轮目前最大的一手；<see cref="TablePattern"/> 为空 = 自由出牌）。</summary>
    public IReadOnlyList<Card> TableCards => _tableCards;

    private readonly List<Card> _tableCards = new();

    /// <summary>桌面这手牌是谁出的（过牌不改变它）。</summary>
    public int TableSeat { get; private set; } = -1;

    /// <summary>桌面牌型（null = 自由出牌）。</summary>
    public CardPattern? TablePattern { get; private set; }

    /// <summary>连续过牌数（到 3 就清桌、由上一手出牌的人重新自由出）。</summary>
    public int PassCount { get; private set; }

    /// <summary>最近一轮的出牌记录（界面显示用，只留最近 <see cref="MaxHistory"/> 条）。</summary>
    public IReadOnlyList<DoudizhuPlayRecord> History => _history;

    private readonly List<DoudizhuPlayRecord> _history = new();

    /// <summary>历史最多留多少条（界面只显示最近这一轮附近，多了没意义）。</summary>
    public const int MaxHistory = 60;

    /// <summary>结算结果（<see cref="DoudizhuPhase.Over"/> 时才有）。</summary>
    public DoudizhuSettlement? Result { get; private set; }

    /// <summary>每个座位出的牌数（统计 / 结算显示用）。</summary>
    public IReadOnlyList<int> PlayCounts => _playCounts;

    private readonly int[] _playCounts = new int[DoudizhuSeats.Count];

    /// <summary>底牌（经典 / 明牌模式有 3 张；双副牌模式没有底牌 = 空）。</summary>
    public IReadOnlyList<Card> Bottom => _bottom;

    /// <summary>底牌是否对所有人可见（明牌模式 / 已经定完地主）。</summary>
    public bool BottomRevealed
        => Mode == DoudizhuMode.OpenBottom || Phase != DoudizhuPhase.Bidding;

    /// <summary>某个座位的手牌数。</summary>
    public int HandCount(int seat) => _hands[DoudizhuSeats.Normalize(seat)].Count;

    /// <summary>某个座位的手牌（<b>只看自己的</b>；别人要看请用 <see cref="SnapshotFor"/>）。</summary>
    public IReadOnlyList<Card> Hand(int seat) => _hands[DoudizhuSeats.Normalize(seat)];

    /// <summary>是不是地主。</summary>
    public bool IsLandlord(int seat) => LandlordSeat >= 0 && DoudizhuSeats.Normalize(seat) == LandlordSeat;

    // ---------------------------------------------------------------- 发牌

    /// <summary>重新发牌（回到叫牌阶段）。</summary>
    public void Reset()
    {
        for (var i = 0; i < _hands.Length; i++) _hands[i].Clear();
        _bottom.Clear();
        _played.Clear();
        _tableCards.Clear();
        _history.Clear();
        Array.Clear(_playCounts);

        Phase = DoudizhuPhase.Waiting;
        LandlordSeat = -1;
        BidScore = 0;
        TableSeat = -1;
        TablePattern = null;
        PassCount = 0;
        Result = null;

        var decks = Mode == DoudizhuMode.DoubleDeck ? 2 : 1;
        var cards = CardDeck.Create(decks);
        CardDeck.Shuffle(cards, _random);
        BombCount = 0;

        if (Mode == DoudizhuMode.DoubleDeck)
        {
            // 双副牌：108 张全发完，没底牌
            var perHand = cards.Count / DoudizhuSeats.Count;      // 36
            for (var seat = 0; seat < DoudizhuSeats.Count; seat++)
            {
                _hands[seat].AddRange(cards.Skip(seat * perHand).Take(perHand));
            }
            var leftover = cards.Count - perHand * DoudizhuSeats.Count;
            if (leftover > 0) _hands[0].AddRange(cards.Skip(perHand * DoudizhuSeats.Count));
        }
        else
        {
            // 经典：每家 17 张，剩 3 张底牌
            for (var seat = 0; seat < DoudizhuSeats.Count; seat++)
            {
                _hands[seat].AddRange(cards.Skip(seat * ClassicHandSize).Take(ClassicHandSize));
            }
            _bottom.AddRange(cards.Skip(ClassicHandSize * DoudizhuSeats.Count));
        }

        foreach (var hand in _hands) SortHand(hand);

        CurrentSeat = 0;
        Phase = DoudizhuPhase.Bidding;
    }

    private static void SortHand(List<Card> hand)
    {
        var sorted = CardDeck.SortForHand(hand);
        hand.Clear();
        hand.AddRange(sorted);
    }

    // ---------------------------------------------------------------- 叫牌

    /// <summary>叫牌：<paramref name="seat"/> 叫 <paramref name="bid"/> 分。</summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public string Bid(int seat, DoudizhuBidKind bid)
    {
        seat = DoudizhuSeats.Normalize(seat);
        if (Phase != DoudizhuPhase.Bidding) return "现在不是叫牌阶段";
        if (seat != CurrentSeat) return "还没轮到你叫牌";

        var score = (int)bid;
        if (score != 0 && score <= BidScore) return $"必须叫得比 {BidScore} 分更高（或者不叫）";

        if (score == 0)
        {
            _history.Add(new DoudizhuPlayRecord(seat, Array.Empty<Card>(), true));
            CurrentSeat = DoudizhuSeats.Next(CurrentSeat);

            // 三家都不叫：重新发牌
            if (_history.Count >= DoudizhuSeats.Count
                && _history.TakeLast(DoudizhuSeats.Count).All(r => r.IsPass))
            {
                Reset();
                return "";
            }
            return "";
        }

        BidScore = score;
        LandlordSeat = seat;
        _history.Add(new DoudizhuPlayRecord(seat, Array.Empty<Card>(), true));

        // 叫 3 分直接定地主
        if (score == 3) { FinishBidding(); return ""; }

        CurrentSeat = DoudizhuSeats.Next(CurrentSeat);
        return "";
    }

    /// <summary>当前叫牌是否已经结束（没人再叫 / 叫到 3 分）。</summary>
    public bool BiddingFinished => Phase != DoudizhuPhase.Bidding;

    /// <summary>结束叫牌：地主拿底牌，进入打牌阶段。</summary>
    public void FinishBidding()
    {
        if (Phase != DoudizhuPhase.Bidding) return;
        if (LandlordSeat < 0)
        {
            Reset();
            return;
        }

        if (_bottom.Count > 0)
        {
            _hands[LandlordSeat].AddRange(_bottom);
            SortHand(_hands[LandlordSeat]);
        }

        Phase = DoudizhuPhase.Playing;
        CurrentSeat = LandlordSeat;
        TableSeat = -1;
        TablePattern = null;
        _tableCards.Clear();
        PassCount = 0;
        _history.Clear();
    }

    // ---------------------------------------------------------------- 出牌

    /// <summary>
    /// 出牌。<paramref name="cards"/> 必须全在手上、且是合法牌型并能压过桌面。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因（界面直接显示）。</returns>
    public string PlayCards(int seat, IReadOnlyList<Card> cards)
    {
        seat = DoudizhuSeats.Normalize(seat);
        if (Phase != DoudizhuPhase.Playing) return "现在不是出牌阶段";
        if (seat != CurrentSeat) return "还没轮到你出牌";
        if (cards is null || cards.Count == 0) return "请选择要出的牌";

        var hand = _hands[seat];
        var remaining = new List<Card>(hand);
        foreach (var card in cards)
        {
            var index = remaining.IndexOf(card);
            if (index < 0) return "出的牌里有不在手上的";
            remaining.RemoveAt(index);
        }

        var pattern = CardPatternRules.Detect(cards);
        if (!pattern.IsValid) return "这不是合法牌型";
        if (!CardPatternRules.CanBeat(pattern, TablePattern))
            return $"压不过桌面上的{TablePattern?.DisplayName ?? "牌"}";

        // 执行
        hand.Clear();
        hand.AddRange(remaining);
        _played.AddRange(cards);
        _playCounts[seat]++;

        // 炸弹翻倍：出炸弹 / 王炸都记一次
        if (pattern.IsBomb) BombCount++;

        _tableCards.Clear();
        _tableCards.AddRange(CardDeck.SortForTable(cards));
        TablePattern = pattern;
        TableSeat = seat;
        PassCount = 0;

        _history.Add(new DoudizhuPlayRecord(seat, cards.ToList(), false));
        TrimHistory();

        // 出完了：结算
        if (hand.Count == 0)
        {
            Finish(seat);
            return "";
        }

        CurrentSeat = DoudizhuSeats.Next(seat);
        return "";
    }

    /// <summary>过牌。</summary>
    public string Pass(int seat)
    {
        seat = DoudizhuSeats.Normalize(seat);
        if (Phase != DoudizhuPhase.Playing) return "现在不是出牌阶段";
        if (seat != CurrentSeat) return "还没轮到你";
        if (TablePattern is null) return "你是本轮先手，必须出牌";

        _history.Add(new DoudizhuPlayRecord(seat, Array.Empty<Card>(), true));
        TrimHistory();
        PassCount++;

        // 另外两家都不要：桌面清空，上一手出牌的人重新自由出
        if (PassCount >= DoudizhuSeats.Count - 1)
        {
            var starter = TableSeat >= 0 ? TableSeat : seat;
            _tableCards.Clear();
            TablePattern = null;
            TableSeat = -1;
            PassCount = 0;
            CurrentSeat = starter;
            return "";
        }

        CurrentSeat = DoudizhuSeats.Next(seat);
        return "";
    }

    private void TrimHistory()
    {
        if (_history.Count > MaxHistory) _history.RemoveRange(0, _history.Count - MaxHistory);
    }

    // ---------------------------------------------------------------- 结算

    private void Finish(int winnerSeat)
    {
        Phase = DoudizhuPhase.Over;
        var landlordWon = IsLandlord(winnerSeat);

        // 计分（经典）：地主赢 = +2 倍叫分，农民各 -1 倍；农民赢反过来。
        // 炸弹翻倍：每个炸弹 / 王炸 × 2（这是经典玩法里的「炸弹翻倍」）。
        var multiplier = Math.Max(1, BidScore) * (1 << Math.Min(BombCount, 6));
        var landlordScore = landlordWon ? multiplier * 2 : -multiplier * 2;

        Result = new DoudizhuSettlement(
            WinnerSeat: winnerSeat,
            LandlordSeat: LandlordSeat,
            LandlordWon: landlordWon,
            Multiplier: multiplier,
            Scores: new[]
            {
                LandlordScoreOf(0, landlordScore),
                LandlordScoreOf(1, landlordScore),
                LandlordScoreOf(2, landlordScore),
            },
            Text: landlordWon
                ? $"地主（{DoudizhuSeats.DisplayName(LandlordSeat)}）赢了！"
                : $"农民赢了！（地主 {DoudizhuSeats.DisplayName(LandlordSeat)} 输了）");
    }

    private int LandlordScoreOf(int seat, int landlordScore)
        => IsLandlord(seat) ? landlordScore : -landlordScore / 2;

    /// <summary>
    /// 测试 / 调试专用：直接把某个座位的手牌换成指定牌（用来构造「快打完了」的局面）。
    /// <b>正式玩法不要调用</b> —— 它会绕过发牌，破坏不重不漏的前提。
    /// </summary>
    public void SetHandForTest(int seat, IReadOnlyList<Card> cards)
    {
        seat = DoudizhuSeats.Normalize(seat);
        _hands[seat].Clear();
        if (cards is not null) _hands[seat].AddRange(cards);
        SortHand(_hands[seat]);
    }

    /// <summary>本局打出的炸弹 / 王炸总数（结算翻倍用）。</summary>
    public int BombCount { get; private set; }

    // ---------------------------------------------------------------- 快照（联机用）

    /// <summary>
    /// 给某个座位生成的快照：<b>只含自己的手牌</b>，别人只有张数。
    /// <para>房主广播时给每个座位各发一份（客户端按自己的座位取对应那份），
    /// 这样别人看不到你的牌，也不需要额外做「按人过滤」的传输层。</para>
    /// </summary>
    public DoudizhuSnapshot SnapshotFor(int seat)
    {
        seat = DoudizhuSeats.Normalize(seat);
        return new DoudizhuSnapshot
        {
            Mode = Mode,
            Phase = Phase,
            LandlordSeat = LandlordSeat,
            BidScore = BidScore,
            CurrentSeat = CurrentSeat,
            Seat = seat,
            Hand = _hands[seat].ToList(),
            HandCounts = new[] { _hands[0].Count, _hands[1].Count, _hands[2].Count },
            Bottom = BottomRevealed ? _bottom.ToList() : new List<Card>(),
            BottomCount = _bottom.Count,
            TableCards = _tableCards.ToList(),
            TablePattern = TablePattern?.Kind ?? CardPatternKind.None,
            TablePatternKey = TablePattern?.Key ?? 0,
            TablePatternLength = TablePattern?.Length ?? 0,
            TableSeat = TableSeat,
            PlayCounts = _playCounts.ToArray(),
            Result = Result,
            BombCount = BombCount,
        };
    }
}

/// <summary>结算结果。</summary>
/// <param name="WinnerSeat">谁先出完牌。</param>
/// <param name="LandlordSeat">谁是地主。</param>
/// <param name="LandlordWon">地主赢没赢。</param>
/// <param name="Multiplier">本局倍数（叫分 × 炸弹翻倍）。</param>
/// <param name="Scores">三个座位各自的得分（地主 ±2 倍，农民 ∓1 倍）。</param>
/// <param name="Text">一句中文结果。</param>
public sealed record DoudizhuSettlement(
    int WinnerSeat,
    int LandlordSeat,
    bool LandlordWon,
    int Multiplier,
    IReadOnlyList<int> Scores,
    string Text);

/// <summary>
/// 一局斗地主的传输快照（<b>按座位生成</b>：只带这个座位自己的手牌）。
/// </summary>
public sealed class DoudizhuSnapshot
{
    public DoudizhuMode Mode { get; set; }
    public DoudizhuPhase Phase { get; set; }
    public int LandlordSeat { get; set; } = -1;
    public int BidScore { get; set; }
    public int CurrentSeat { get; set; }

    /// <summary>这份快照是给哪个座位生成的。</summary>
    public int Seat { get; set; }

    /// <summary>这个座位的手牌（别人的手牌不在里面）。</summary>
    public List<Card> Hand { get; set; } = new();

    /// <summary>三家各有几张（公开信息）。</summary>
    public int[] HandCounts { get; set; } = Array.Empty<int>();

    /// <summary>底牌（明牌模式 / 已定地主时才有）。</summary>
    public List<Card> Bottom { get; set; } = new();

    /// <summary>底牌总数（没公开时只知道有几张）。</summary>
    public int BottomCount { get; set; }

    /// <summary>桌面上的牌。</summary>
    public List<Card> TableCards { get; set; } = new();

    /// <summary>桌面牌型（<see cref="CardPatternKind.None"/> = 自由出牌）。</summary>
    public CardPatternKind TablePattern { get; set; }

    /// <summary>桌面牌型的主键与张数（还原 <see cref="CardPattern"/> 用）。</summary>
    public int TablePatternKey { get; set; }
    public int TablePatternLength { get; set; }

    /// <summary>桌面这手是谁出的。</summary>
    public int TableSeat { get; set; } = -1;

    /// <summary>三家出过几手牌。</summary>
    public int[] PlayCounts { get; set; } = Array.Empty<int>();

    /// <summary>结算结果。</summary>
    public DoudizhuSettlement? Result { get; set; }

    /// <summary>炸弹数（翻倍显示用）。</summary>
    public int BombCount { get; set; }

    /// <summary>桌面牌型还原成 <see cref="CardPattern"/>（没有 = null）。</summary>
    public CardPattern? Pattern => TablePattern == CardPatternKind.None
        ? null
        : new CardPattern(TablePattern, TablePatternKey, TablePatternLength);
}
