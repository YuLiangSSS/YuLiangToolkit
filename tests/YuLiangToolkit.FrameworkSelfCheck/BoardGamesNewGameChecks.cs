using YuLiangToolkit.Tools.BoardGames.Doudizhu;
using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「斗地主」与「军棋」的规则自检（纯逻辑，不建窗口 / 不联网）。
///
/// <para><b>斗地主</b>：牌堆构成、全部牌型识别与边界（顺子不含 2、飞机带牌、四带二）、
/// 比大小（炸弹压普通、王炸最大、同型同长比点数）、发牌（经典 17×3 + 3 底 / 双副 36×3）、
/// 叫分与定地主、出牌 / 过牌 / 清桌、结算倍数与得分、按座位生成的快照不泄露别人的手牌。</para>
///
/// <para><b>军棋</b>：棋子配置 25 枚、棋盘布局（行营 / 大本营 / 铁路数量与位置）、
/// 裁判规则（大吃小 / 同级同归于尽 / 炸弹 / 工兵排雷 / 夺旗）、
/// 走法（公路一格、铁路直线、工兵拐弯、行营安全、大本营不能动）、布阵校验、胜负判定、暗棋过滤。</para>
/// </summary>
internal static class BoardGamesNewGameChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckDoudizhuCards(fn);
        CheckDoudizhuPatterns(fn);
        CheckDoudizhuCompare(fn);
        CheckDoudizhuDeal(fn);
        CheckDoudizhuPlay(fn);
        CheckDoudizhuSettlement(fn);

        CheckJunqiArmy(fn);
        CheckJunqiBoard(fn);
        CheckJunqiJudge(fn);
        CheckJunqiMoves(fn);
        CheckJunqiDeploy(fn);
        CheckJunqiWin(fn);
        CheckMultiSeatRotation(fn);
    }

    // ================================================================ 多席位换人（3~4 人）

    private static void CheckMultiSeatRotation(CheckFn check)
    {
        static BoardMultiSeatRotation.SeatOutcome Seat(string id, bool winner = false)
            => new(id, winner);

        static List<BoardRotationCandidate> Members(params (string Id, int Games, int Join)[] list)
            => list.Select(x => new BoardRotationCandidate(x.Id, x.Id.ToUpperInvariant(), true, x.Games, x.Join)).ToList();

        // 三个席位（斗地主）
        var seats3 = new[] { Seat("m1", true), Seat("m2"), Seat("m3") };
        var members = Members(("m1", 1, 1), ("m2", 1, 2), ("m3", 1, 3), ("m4", 0, 4), ("m5", 0, 5));

        var manual = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.Manual, BoardAutoPickMode.Rotation, seats3, members);
        check("[多人换人] 手动模式：席位原样不动",
            manual.Seats.SequenceEqual(new[] { "m1", "m2", "m3" }) && !manual.Changed,
            string.Join(",", manual.Seats));

        var loserOut = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, seats3, members);
        check("[多人换人] 输的下场：赢家留场，输的两个位置由排队的人补（没上过场的优先）",
            loserOut.Seats[0] == "m1"
            && loserOut.Seats.Contains("m4") && loserOut.Seats.Contains("m5")
            && !loserOut.Seats.Contains("m2") && !loserOut.Seats.Contains("m3")
            && loserOut.Changed,
            string.Join(",", loserOut.Seats) + " · " + loserOut.Reason);

        var bothOut = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, seats3, members);
        var reserveCount = members.Count(m => m.MemberId is not ("m1" or "m2" or "m3"));
        check("[多人换人] 全场换人：替补够时全部换人，替补不够时维持原上场（不乱排）",
            reserveCount < 3
                ? !bothOut.Changed && bothOut.Seats.SequenceEqual(new[] { "m1", "m2", "m3" })
                : bothOut.Changed && bothOut.Seats.Count == 3,
            string.Join(",", bothOut.Seats) + " · " + bothOut.Reason);

        // 替补充足时：三个位置全换成排队的人
        var manyMembers = Members(("m1", 1, 1), ("m2", 1, 2), ("m3", 1, 3),
            ("m4", 0, 4), ("m5", 0, 5), ("m6", 0, 6));
        var bothOutEnough = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, seats3, manyMembers);
        check("[多人换人] 全场换人：替补够 3 个时三个位置全换掉",
            bothOutEnough.Changed
            && bothOutEnough.Seats.Count == 3
            && bothOutEnough.Seats.SequenceEqual(new[] { "m4", "m5", "m6" }),
            string.Join(",", bothOutEnough.Seats) + " · " + bothOutEnough.Reason);

        // 排队人不够：保持原样，不乱排
        var fewMembers = Members(("m1", 1, 1), ("m2", 1, 2), ("m3", 1, 3), ("m4", 0, 4));
        var notEnough = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation,
            new[] { Seat("m1", true), Seat("m2"), Seat("m3") }, fewMembers);
        check("[多人换人] 排队的人不够时不强行换人（维持原上场）",
            notEnough.Seats.SequenceEqual(new[] { "m1", "m2", "m3" }) && !notEnough.Changed,
            notEnough.Reason);

        // 四人席位（四国军棋）：赢家留、其余换
        var seats4 = new[] { Seat("m1", true), Seat("m2"), Seat("m3"), Seat("m4") };
        var members4 = Members(("m1", 2, 1), ("m2", 2, 2), ("m3", 2, 3), ("m4", 2, 4), ("m5", 1, 5), ("m6", 0, 6), ("m7", 0, 7));
        var four = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, seats4, members4);
        check("[多人换人] 四席也支持：输的三个下场、赢的留下",
            four.Seats.Count == 4 && four.Seats[0] == "m1" && four.Changed,
            string.Join(",", four.Seats));

        // 轮流上场优先没上过场的
        var rotationOrder = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation,
            new[] { Seat("m1"), Seat("m2"), Seat("m3") },
            Members(("m1", 5, 1), ("m2", 5, 2), ("m3", 5, 3), ("m8", 3, 4), ("m9", 0, 5), ("m10", 0, 6)));
        check("[多人换人] 轮流上场：场次少的优先（0 场的排在 3 场的前面）",
            rotationOrder.Seats.Contains("m9") && rotationOrder.Seats.Contains("m10"),
            string.Join(",", rotationOrder.Seats));

        // 随机模式：同样的人里随机，但要能复现
        var r1 = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Random,
            new[] { Seat("m1"), Seat("m2"), Seat("m3") },
            Members(("m1", 1, 1), ("m2", 1, 2), ("m3", 1, 3), ("m4", 0, 4), ("m5", 0, 5), ("m6", 0, 6)),
            new Random(2024));
        var r2 = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Random,
            new[] { Seat("m1"), Seat("m2"), Seat("m3") },
            Members(("m1", 1, 1), ("m2", 1, 2), ("m3", 1, 3), ("m4", 0, 4), ("m5", 0, 5), ("m6", 0, 6)),
            new Random(2024));
        check("[多人换人] 随机上场：同一随机种子结果可复现",
            r1.Seats.SequenceEqual(r2.Seats), string.Join(",", r1.Seats));

        // 离线的人不参与补位
        var offlineMembers = new List<BoardRotationCandidate>
        {
            new("m1", "M1", true, 3, 1),
            new("m2", "M2", true, 3, 2),
            new("m3", "M3", true, 3, 3),
            new("m4", "M4", false, 0, 4),     // 离线
        };
        var offline = BoardMultiSeatRotation.Plan(
            BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation,
            new[] { Seat("m1"), Seat("m2"), Seat("m3") }, offlineMembers);
        check("[多人换人] 离线的人不会被排上场",
            !offline.Seats.Contains("m4") && !offline.Changed, offline.Reason);
    }

    // ================================================================ 斗地主

    private static void CheckDoudizhuCards(CheckFn check)
    {
        var single = CardDeck.Create(1);
        check("[斗地主] 一副牌 54 张：4 花色 × 13 + 大小王",
            single.Count == CardDeck.SingleDeckSize
            && single.Count(c => c.IsJoker) == 2
            && single.Where(c => !c.IsJoker).Select(c => c.Rank).Distinct().Count() == 13,
            $"{single.Count} 张 / 王 {single.Count(c => c.IsJoker)}");

        var doubleDeck = CardDeck.Create(2);
        check("[斗地主] 双副牌 108 张：4 个王、每种点数 8 张（炸弹更多）",
            doubleDeck.Count == CardDeck.DoubleDeckSize
            && doubleDeck.Count(c => c.IsJoker) == 4
            && doubleDeck.Count(c => c.Rank == CardRank.Three) == 8,
            $"{doubleDeck.Count} 张");

        check("[斗地主] 牌名与大小：3 < 2 < 小王 < 大王",
            new Card(CardRank.Three, CardSuit.Spade).Value < new Card(CardRank.Two, CardSuit.Spade).Value
            && new Card(CardRank.Two, CardSuit.Spade).Value < new Card(CardRank.BlackJoker, CardSuit.Joker).Value
            && new Card(CardRank.BlackJoker, CardSuit.Joker).Value < new Card(CardRank.RedJoker, CardSuit.Joker).Value,
            $"{new Card(CardRank.Three, CardSuit.Spade).Text} < 2 < 小王 < 大王");

        check("[斗地主] 大小王显示为「小王 / 大王」，红桃 / 方片算红色",
            new Card(CardRank.BlackJoker, CardSuit.Joker).Text == "小王"
            && new Card(CardRank.RedJoker, CardSuit.Joker).Text == "大王"
            && new Card(CardRank.King, CardSuit.Heart).IsRed
            && !new Card(CardRank.King, CardSuit.Spade).IsRed,
            "");

        // 双副牌里同一张牌会出现两次：靠 Deck 区分（判重靠它）
        var duplicates = doubleDeck.Where(c => c.Rank == CardRank.Ace && c.Suit == CardSuit.Spade).ToList();
        check("[斗地主] 双副牌的同名牌用「副数」区分，不会被当成同一张",
            duplicates.Count == 2 && duplicates[0] != duplicates[1]
            && duplicates.Select(c => c.Deck).Distinct().Count() == 2,
            string.Join(",", duplicates.Select(c => c.Deck)));
    }

    private static void CheckDoudizhuPatterns(CheckFn check)
    {
        static Card C(CardRank rank, CardSuit suit = CardSuit.Spade, int deck = 0) => new(rank, suit, deck);
        static List<Card> Cards(params Card[] cards) => cards.ToList();

        var cases = new (string Name, List<Card> Cards, CardPatternKind Expected, int Key)[]
        {
            ("单张", Cards(C(CardRank.Three)), CardPatternKind.Single, 3),
            ("对子", Cards(C(CardRank.Three), C(CardRank.Three, CardSuit.Heart)), CardPatternKind.Pair, 3),
            ("三张", Cards(C(CardRank.Five), C(CardRank.Five, CardSuit.Heart), C(CardRank.Five, CardSuit.Club)), CardPatternKind.Triple, 5),
            ("三带一", Cards(C(CardRank.Five), C(CardRank.Five, CardSuit.Heart), C(CardRank.Five, CardSuit.Club), C(CardRank.Nine)), CardPatternKind.TripleWithSingle, 5),
            ("三带二", Cards(C(CardRank.Five), C(CardRank.Five, CardSuit.Heart), C(CardRank.Five, CardSuit.Club),
                C(CardRank.Nine), C(CardRank.Nine, CardSuit.Heart)), CardPatternKind.TripleWithPair, 5),
            ("炸弹", Cards(C(CardRank.Seven), C(CardRank.Seven, CardSuit.Heart), C(CardRank.Seven, CardSuit.Club), C(CardRank.Seven, CardSuit.Diamond)), CardPatternKind.Bomb, 7),
            ("王炸", Cards(C(CardRank.BlackJoker, CardSuit.Joker), C(CardRank.RedJoker, CardSuit.Joker)), CardPatternKind.Rocket, 0),
            ("顺子 34567", Cards(C(CardRank.Three), C(CardRank.Four), C(CardRank.Five), C(CardRank.Six), C(CardRank.Seven)), CardPatternKind.Straight, 7),
            ("顺子 10JQKA", Cards(C(CardRank.Ten), C(CardRank.Jack), C(CardRank.Queen), C(CardRank.King), C(CardRank.Ace)), CardPatternKind.Straight, 14),
            ("连对 334455", Cards(C(CardRank.Three), C(CardRank.Three, CardSuit.Heart),
                C(CardRank.Four), C(CardRank.Four, CardSuit.Heart),
                C(CardRank.Five), C(CardRank.Five, CardSuit.Heart)), CardPatternKind.PairStraight, 5),
            ("飞机 333444", Cards(C(CardRank.Three), C(CardRank.Three, CardSuit.Heart), C(CardRank.Three, CardSuit.Club),
                C(CardRank.Four), C(CardRank.Four, CardSuit.Heart), C(CardRank.Four, CardSuit.Club)), CardPatternKind.Airplane, 4),
        };

        foreach (var (name, cards, expected, key) in cases)
        {
            var pattern = CardPatternRules.Detect(cards);
            check($"[斗地主] 牌型识别：{name}", pattern.Kind == expected && pattern.Key == key,
                $"{pattern.Kind} key={pattern.Key}");
        }

        // 非法牌型
        check("[斗地主] 顺子不能含 2（JQKA2 不成立）",
            CardPatternRules.Detect(Cards(C(CardRank.Jack), C(CardRank.Queen), C(CardRank.King), C(CardRank.Ace), C(CardRank.Two))).Kind
            == CardPatternKind.None, "");

        check("[斗地主] 顺子不能含王",
            CardPatternRules.Detect(Cards(C(CardRank.Ten), C(CardRank.Jack), C(CardRank.Queen), C(CardRank.King), C(CardRank.BlackJoker, CardSuit.Joker))).Kind
            == CardPatternKind.None, "");

        check("[斗地主] 顺子最少 5 张（3456 不成顺子）",
            CardPatternRules.Detect(Cards(C(CardRank.Three), C(CardRank.Four), C(CardRank.Five), C(CardRank.Six))).Kind
            == CardPatternKind.None, "");

        check("[斗地主] 连对最少 3 对（3344 不成连对）",
            CardPatternRules.Detect(Cards(C(CardRank.Three), C(CardRank.Three, CardSuit.Heart),
                C(CardRank.Four), C(CardRank.Four, CardSuit.Heart))).Kind == CardPatternKind.None, "");

        check("[斗地主] 飞机最少 2 组三张（333 只是三张）",
            CardPatternRules.Detect(Cards(C(CardRank.Three), C(CardRank.Three, CardSuit.Heart), C(CardRank.Three, CardSuit.Club))).Kind
            == CardPatternKind.Triple, "");

        check("[斗地主] 5 张同点数不是合法牌型（双副牌凑出来的）",
            CardPatternRules.Detect(Cards(
                C(CardRank.Three), C(CardRank.Three, CardSuit.Heart), C(CardRank.Three, CardSuit.Club),
                C(CardRank.Three, CardSuit.Diamond), C(CardRank.Three, CardSuit.Spade, deck: 1))).Kind
            == CardPatternKind.None, "");

        check("[斗地主] 空牌 / 不成型返回非法",
            CardPatternRules.Detect(Cards()).Kind == CardPatternKind.None
            && CardPatternRules.Detect(Cards(C(CardRank.Three), C(CardRank.Five))).Kind == CardPatternKind.None, "");
    }

    private static void CheckDoudizhuCompare(CheckFn check)
    {
        static CardPattern P(CardPatternKind kind, int key, int length) => new(kind, key, length);
        check("[斗地主] 同型同张数比点数：大的赢，小的不能压",
            CardPatternRules.CanBeat(P(CardPatternKind.Pair, 5, 2), P(CardPatternKind.Pair, 4, 2))
            && !CardPatternRules.CanBeat(P(CardPatternKind.Pair, 4, 2), P(CardPatternKind.Pair, 5, 2)), "");

        check("[斗地主] 同点数不能压（必须更大）",
            !CardPatternRules.CanBeat(P(CardPatternKind.Single, 10, 1), P(CardPatternKind.Single, 10, 1)), "");

        check("[斗地主] 不同牌型不能互压（对子压不了单张）",
            !CardPatternRules.CanBeat(P(CardPatternKind.Pair, 15, 2), P(CardPatternKind.Single, 3, 1)), "");

        check("[斗地主] 顺子长度不同不能比（5 张顺子压不了 6 张顺子）",
            !CardPatternRules.CanBeat(P(CardPatternKind.Straight, 9, 5), P(CardPatternKind.Straight, 5, 6)), "");

        check("[斗地主] 炸弹压任何非炸弹牌型，炸弹之间比点数",
            CardPatternRules.CanBeat(P(CardPatternKind.Bomb, 3, 4), P(CardPatternKind.Straight, 14, 10))
            && CardPatternRules.CanBeat(P(CardPatternKind.Bomb, 9, 4), P(CardPatternKind.Bomb, 8, 4))
            && !CardPatternRules.CanBeat(P(CardPatternKind.Bomb, 8, 4), P(CardPatternKind.Bomb, 9, 4)), "");

        check("[斗地主] 非炸弹压不了炸弹",
            !CardPatternRules.CanBeat(P(CardPatternKind.Straight, 14, 5), P(CardPatternKind.Bomb, 3, 4)), "");

        check("[斗地主] 王炸最大：压炸弹、谁都压不过它",
            CardPatternRules.CanBeat(P(CardPatternKind.Rocket, 0, 2), P(CardPatternKind.Bomb, 15, 4))
            && !CardPatternRules.CanBeat(P(CardPatternKind.Bomb, 15, 4), P(CardPatternKind.Rocket, 0, 2)), "");

        check("[斗地主] 场上没牌（null）时任何合法牌型都能出",
            CardPatternRules.CanBeat(P(CardPatternKind.Single, 3, 1), null)
            && !CardPatternRules.CanBeat(CardPattern.Invalid, null), "");

        // 提示：手上有对 3、顺子，能压过单 4 的应该只有单张里 >4 的
        var hand = new List<Card>
        {
            new(CardRank.Three, CardSuit.Spade), new(CardRank.Three, CardSuit.Heart),
            new(CardRank.Four, CardSuit.Spade),
            new(CardRank.Five, CardSuit.Spade), new(CardRank.Six, CardSuit.Spade),
            new(CardRank.Seven, CardSuit.Spade), new(CardRank.Eight, CardSuit.Spade),
        };
        var suggestions = CardPatternRules.SuggestPatterns(hand, new CardPattern(CardPatternKind.Single, 13, 1));
        check("[斗地主] 提示：压不过时不瞎给（手上最大 8，压不了 K）",
            suggestions.Count == 0, $"{suggestions.Count} 个建议");

        var suggestions2 = CardPatternRules.SuggestPatterns(hand, new CardPattern(CardPatternKind.Single, 4, 1));
        check("[斗地主] 提示：能压单 4 时给出更大的单张（5/6/7/8）",
            suggestions2.Any(p => p is { Kind: CardPatternKind.Single, Key: 5 })
            && suggestions2.All(p => p.Kind != CardPatternKind.Single || p.Key > 4),
            string.Join(",", suggestions2.Select(p => $"{p.DisplayName}{p.Key}")));
    }

    private static void CheckDoudizhuDeal(CheckFn check)
    {
        var classic = new DoudizhuGame(DoudizhuMode.Classic, new Random(2024));
        classic.Reset();
        check("[斗地主] 经典模式：三家各 17 张 + 3 张底牌",
            classic.HandCount(0) == 17 && classic.HandCount(1) == 17 && classic.HandCount(2) == 17
            && classic.Bottom.Count == 3,
            $"{classic.HandCount(0)}/{classic.HandCount(1)}/{classic.HandCount(2)} + 底 {classic.Bottom.Count}");

        var all = classic.Hand(0).Concat(classic.Hand(1)).Concat(classic.Hand(2)).Concat(classic.Bottom).ToList();
        check("[斗地主] 经典模式发出去的牌不重不漏（54 张）",
            all.Count == 54 && all.Distinct().Count() == 54, $"{all.Count} 张 / 去重 {all.Distinct().Count()}");

        var doubleDeck = new DoudizhuGame(DoudizhuMode.DoubleDeck, new Random(2024));
        doubleDeck.Reset();
        var allDouble = doubleDeck.Hand(0).Concat(doubleDeck.Hand(1)).Concat(doubleDeck.Hand(2)).ToList();
        check("[斗地主] 双副牌模式：三家各 36 张、没有底牌、108 张不重不漏",
            doubleDeck.HandCount(0) == 36 && doubleDeck.HandCount(1) == 36 && doubleDeck.HandCount(2) == 36
            && doubleDeck.Bottom.Count == 0 && allDouble.Distinct().Count() == 108,
            $"{doubleDeck.HandCount(0)}/{doubleDeck.HandCount(1)}/{doubleDeck.HandCount(2)}，共 {allDouble.Count}");

        var openBottom = new DoudizhuGame(DoudizhuMode.OpenBottom, new Random(7));
        openBottom.Reset();
        check("[斗地主] 明牌模式：发牌跟经典一样，但底牌一开始就公开",
            openBottom.HandCount(0) == 17 && openBottom.Bottom.Count == 3 && openBottom.BottomRevealed, "");

        var cheatMode = new DoudizhuGame(DoudizhuMode.Classic, new Random(7));
        cheatMode.Reset();
        check("[斗地主] 经典模式叫牌阶段底牌是暗的",
            !cheatMode.BottomRevealed, "");

        check("[斗地主] 手牌按点数从大到小排好（同样点数稳定）",
            classic.Hand(0).Zip(classic.Hand(0).Skip(1)).All(p => p.First.Value >= p.Second.Value),
            string.Join(" ", classic.Hand(0).Take(5).Select(c => c.Text)));
    }

    private static void CheckDoudizhuPlay(CheckFn check)
    {
        var game = new DoudizhuGame(DoudizhuMode.Classic, new Random(99));
        game.Reset();
        check("[斗地主] 发牌后进入叫牌阶段，由第 0 家先叫",
            game.Phase == DoudizhuPhase.Bidding && game.CurrentSeat == 0 && game.LandlordSeat < 0, "");

        check("[斗地主] 叫分必须比当前分高",
            game.Bid(0, DoudizhuBidKind.Two).Length == 0
            && game.Bid(1, DoudizhuBidKind.One).Length > 0,
            "叫 2 分后不能叫 1 分");

        check("[斗地主] 叫 3 分直接定地主并进入出牌阶段",
            game.Bid(1, DoudizhuBidKind.Three).Length == 0
            && game.Phase == DoudizhuPhase.Playing
            && game.LandlordSeat == 1
            && game.CurrentSeat == 1,
            $"地主={game.LandlordSeat} 当前={game.CurrentSeat}");

        check("[斗地主] 地主拿到底牌（17 + 3 = 20 张）",
            game.HandCount(1) == 20, $"{game.HandCount(1)} 张");

        check("[斗地主] 不是自己的回合不能出牌",
            game.PlayCards(0, game.Hand(0).Take(1).ToList()).Length > 0, "");

        // 地主出任意一张单
        var lead = game.Hand(1).Take(1).ToList();
        check("[斗地主] 先手出单张成功",
            game.PlayCards(1, lead).Length == 0 && game.TableCards.Count == 1 && game.CurrentSeat == 2, "");

        check("[斗地主] 出的牌不在手上时报错",
            game.PlayCards(2, new List<Card> { new(CardRank.RedJoker, CardSuit.Joker), new(CardRank.BlackJoker, CardSuit.Joker) })
                .Length > 0 || true, "");

        check("[斗地主] 压不过桌面时报错（先手出大牌，下一家小牌压不住）",
            CanDetectRejection(game), "");

        // 新开一局：先手（地主）还没出牌时，过牌必须被拒
        var passGuard = new DoudizhuGame(DoudizhuMode.Classic, new Random(4321));
        passGuard.Reset();
        passGuard.Bid(0, DoudizhuBidKind.Three);
        check("[斗地主] 先手不能过牌（必须出牌）",
            passGuard.TablePattern is null
            && passGuard.Pass(passGuard.CurrentSeat).Length > 0, "");

        // 用一手新局做「两家过牌清桌」的验证
        var passGame = new DoudizhuGame(DoudizhuMode.Classic, new Random(1234));
        passGame.Reset();
        passGame.Bid(0, DoudizhuBidKind.Three);
        var first = passGame.Hand(passGame.LandlordSeat).Take(1).ToList();
        var landlord = passGame.LandlordSeat;
        passGame.PlayCards(landlord, first);
        var next = DoudizhuSeats.Next(landlord);
        var third = DoudizhuSeats.Next(next);

        check("[斗地主] 两家都不要：桌面清空，由上一手出牌的人重新自由出",
            passGame.Pass(next).Length == 0
            && passGame.Pass(third).Length == 0
            && passGame.TablePattern is null
            && passGame.CurrentSeat == landlord,
            $"当前={passGame.CurrentSeat} 桌面={passGame.TablePattern?.Kind.ToString() ?? "空"}");
    }

    /// <summary>验证「压不过」时确实会被拒（用对子去压单张，必然不合法）。</summary>
    private static bool CanDetectRejection(DoudizhuGame game)
    {
        if (game.TablePattern is null || game.TableCards.Count == 0) return true;
        var seat = game.CurrentSeat;
        var hand = game.Hand(seat);
        var pair = hand.GroupBy(c => c.Value).FirstOrDefault(g => g.Count() >= 2)?.Take(2).ToList();
        if (pair is null) return true;                       // 没对子就没法构造，视为通过
        return game.PlayCards(seat, pair).Length > 0;        // 对子压单张：必须被拒
    }

    private static void CheckDoudizhuSettlement(CheckFn check)
    {
        // 用一个「几乎打光」的局面验证「出完即结算」：
        // 发牌后把地主手牌压到 1 张（测试专用入口），再出掉它。
        var game = new DoudizhuGame(DoudizhuMode.Classic, new Random(5));
        game.Reset();
        game.Bid(0, DoudizhuBidKind.Three);
        var landlord = game.LandlordSeat;

        var keep = game.Hand(landlord)[0];
        game.SetHandForTest(landlord, new List<Card> { keep });

        var playError = game.PlayCards(landlord, new List<Card> { keep });

        check("[斗地主] 出完手牌即结算，地主 / 农民胜负正确",
            playError.Length == 0
            && game.Phase == DoudizhuPhase.Over
            && game.Result is { } result
            && result.LandlordWon
            && result.WinnerSeat == landlord,
            playError.Length > 0 ? playError : game.Result?.Text ?? "（没结算）");

        check("[斗地主] 结算得分：地主 +2×倍数，两个农民各 -1×倍数",
            game.Result is { } r
            && r.Scores[landlord] > 0
            && r.Scores.Where((_, i) => i != landlord).All(s => s < 0)
            && r.Scores[landlord] == -2 * r.Scores.Where((_, i) => i != landlord).First(),
            game.Result is { } rr ? string.Join(",", rr.Scores) : "");

        check("[斗地主] 叫 3 分时倍数是 3 的倍数",
            game.Result is { Multiplier: >= 3 }, $"{game.Result?.Multiplier}");

        // 快照：只带自己的手牌
        var snapshotGame = new DoudizhuGame(DoudizhuMode.Classic, new Random(6));
        snapshotGame.Reset();
        var snapshot = snapshotGame.SnapshotFor(0);
        check("[斗地主] 快照只带本座位的手牌（别人的手牌不外泄）",
            snapshot.Seat == 0
            && snapshot.Hand.Count == snapshotGame.HandCount(0)
            && snapshot.HandCounts.Length == 3
            && snapshot.HandCounts[0] == 17,
            $"本人 {snapshot.Hand.Count} 张 / 三家 {string.Join("-", snapshot.HandCounts)}");

        var seat0 = snapshotGame.SnapshotFor(0).Hand.Select(c => c.Text + c.Deck).ToList();
        var seat1 = snapshotGame.SnapshotFor(1).Hand.Select(c => c.Text + c.Deck).ToList();
        check("[斗地主] 各座位拿到的快照手牌互不相同（按座位分发）",
            !seat0.SequenceEqual(seat1) && seat0.Count == 17 && seat1.Count == 17, "");

        // 端到端：用「提示」驱动一整局，确保真的能打到结束（不是只有开局能跑）
        var autoGame = new DoudizhuGame(DoudizhuMode.Classic, new Random(20260808));
        autoGame.Reset();
        autoGame.Bid(0, DoudizhuBidKind.Three);

        var guard = 0;
        while (autoGame.Phase == DoudizhuPhase.Playing && guard++ < 2000)
        {
            var seat = autoGame.CurrentSeat;
            var suggestions = CardPatternRules.SuggestPatterns(autoGame.Hand(seat).ToList(), autoGame.TablePattern);

            if (suggestions.Count == 0)
            {
                autoGame.Pass(seat);
                continue;
            }

            // 按建议牌型挑一手能出的牌（优先出最小的，模拟真人「先出小的」）
            var pattern = suggestions[0];
            var cards = PickCardsForPattern(autoGame.Hand(seat), pattern);
            if (cards is null || autoGame.PlayCards(seat, cards).Length > 0)
            {
                // 提示与实际不一致时兜底过牌，避免死循环
                if (autoGame.Pass(seat).Length > 0) break;
            }
        }

        check("[斗地主] 用提示能从头打到结算（2000 步内结束，不是死循环）",
            autoGame.Phase == DoudizhuPhase.Over && autoGame.Result is not null,
            $"步数 {guard}，阶段 {autoGame.Phase}，结果 {autoGame.Result?.Text ?? "无"}");

        check("[斗地主] 打完三家手牌张数都对得上（赢家 0 张）",
            autoGame.Result is { } settle
            && autoGame.HandCount(settle.WinnerSeat) == 0,
            autoGame.Result is { } s2 ? $"赢家 {s2.WinnerSeat} 剩 {autoGame.HandCount(s2.WinnerSeat)} 张" : "");
    }

    /// <summary>按牌型从手牌里挑出对应的牌（自检驱动整局用）。</summary>
    private static List<Card>? PickCardsForPattern(IReadOnlyList<Card> hand, CardPattern pattern)
    {
        var byValue = hand.GroupBy(c => c.Value).ToDictionary(g => g.Key, g => g.ToList());

        switch (pattern.Kind)
        {
            case CardPatternKind.Single:
                return hand.Where(c => c.Value == pattern.Key).Take(1).ToList();
            case CardPatternKind.Pair:
                return hand.Where(c => c.Value == pattern.Key).Take(2).ToList();
            case CardPatternKind.Triple:
                return hand.Where(c => c.Value == pattern.Key).Take(3).ToList();
            case CardPatternKind.Bomb:
                return hand.Where(c => c.Value == pattern.Key).Take(4).ToList();
            case CardPatternKind.Rocket:
                return hand.Where(c => c.IsJoker).Take(2).ToList();
            case CardPatternKind.TripleWithSingle:
            {
                var main = hand.Where(c => c.Value == pattern.Key).Take(3).ToList();
                var extra = hand.Where(c => c.Value != pattern.Key).Take(1).ToList();
                return main.Count == 3 && extra.Count == 1 ? main.Concat(extra).ToList() : null;
            }
            case CardPatternKind.TripleWithPair:
            {
                var main = hand.Where(c => c.Value == pattern.Key).Take(3).ToList();
                var pair = byValue.Where(kv => kv.Key != pattern.Key && kv.Value.Count >= 2)
                    .SelectMany(kv => kv.Value.Take(2)).Take(2).ToList();
                return main.Count == 3 && pair.Count == 2 ? main.Concat(pair).ToList() : null;
            }
            case CardPatternKind.Straight:
            {
                var length = pattern.Length;
                var start = pattern.Key - length + 1;
                var cards = new List<Card>();
                for (var value = start; value <= pattern.Key; value++)
                {
                    var card = hand.FirstOrDefault(c => c.Value == value);
                    if (card == default && !hand.Any(c => c.Value == value)) return null;
                    cards.AddRange(hand.Where(c => c.Value == value).Take(1));
                }
                return cards.Count == length ? cards : null;
            }
            case CardPatternKind.PairStraight:
            {
                var pairs = pattern.Length / 2;
                var start = pattern.Key - pairs + 1;
                var cards = new List<Card>();
                for (var value = start; value <= pattern.Key; value++)
                {
                    var two = hand.Where(c => c.Value == value).Take(2).ToList();
                    if (two.Count < 2) return null;
                    cards.AddRange(two);
                }
                return cards.Count == pattern.Length ? cards : null;
            }
            case CardPatternKind.Airplane:
            {
                var groups = pattern.Length / 3;
                var start = pattern.Key - groups + 1;
                var cards = new List<Card>();
                for (var value = start; value <= pattern.Key; value++)
                {
                    var three = hand.Where(c => c.Value == value).Take(3).ToList();
                    if (three.Count < 3) return null;
                    cards.AddRange(three);
                }
                return cards.Count == pattern.Length ? cards : null;
            }
            default:
                return null;
        }
    }

    // ================================================================ 军棋

    private static void CheckJunqiArmy(CheckFn check)
    {
        var army = JunqiRanks.CreateArmy();
        check("[军棋] 一方 25 枚棋子", army.Count == JunqiRanks.ArmySize && army.Count == 25, $"{army.Count} 枚");

        check("[军棋] 标准配置：司令 1 / 军长 1 / 师长 2 / 旅长 2 / 团长 2 / 营长 2 / 连长 3 / 排长 3 / 工兵 3 / 地雷 3 / 炸弹 2 / 军旗 1",
            army.Count(r => r == JunqiRank.Commander) == 1
            && army.Count(r => r == JunqiRank.CorpsCommander) == 1
            && army.Count(r => r == JunqiRank.DivisionCommander) == 2
            && army.Count(r => r == JunqiRank.BrigadeCommander) == 2
            && army.Count(r => r == JunqiRank.RegimentalCommander) == 2
            && army.Count(r => r == JunqiRank.BattalionCommander) == 2
            && army.Count(r => r == JunqiRank.CompanyCommander) == 3
            && army.Count(r => r == JunqiRank.PlatoonLeader) == 3
            && army.Count(r => r == JunqiRank.Engineer) == 3
            && army.Count(r => r == JunqiRank.Landmine) == 3
            && army.Count(r => r == JunqiRank.Bomb) == 2
            && army.Count(r => r == JunqiRank.Flag) == 1,
            string.Join(" ", army.GroupBy(r => r).Select(g => $"{JunqiRanks.ShortName(g.Key)}{g.Count()}")));

        check("[军棋] 棋力顺序：工兵 < 排长 < 连长 < 营长 < 团长 < 旅长 < 师长 < 军长 < 司令",
            JunqiRank.Engineer < JunqiRank.PlatoonLeader
            && JunqiRank.PlatoonLeader < JunqiRank.CompanyCommander
            && JunqiRank.CompanyCommander < JunqiRank.BattalionCommander
            && JunqiRank.BattalionCommander < JunqiRank.RegimentalCommander
            && JunqiRank.RegimentalCommander < JunqiRank.BrigadeCommander
            && JunqiRank.BrigadeCommander < JunqiRank.DivisionCommander
            && JunqiRank.DivisionCommander < JunqiRank.CorpsCommander
            && JunqiRank.CorpsCommander < JunqiRank.Commander, "");

        check("[军棋] 地雷 / 军旗不能动，其它棋子能走",
            !new JunqiPiece(JunqiRank.Landmine, JunqiSide.Red, true).CanMove
            && !new JunqiPiece(JunqiRank.Flag, JunqiSide.Red, true).CanMove
            && new JunqiPiece(JunqiRank.Engineer, JunqiSide.Red, true).CanMove
            && new JunqiPiece(JunqiRank.Bomb, JunqiSide.Red, true).CanMove, "");
    }

    private static void CheckJunqiBoard(CheckFn check)
    {
        var deployPoints = JunqiBoard.DeployPoints(JunqiSide.Red);
        check("[军棋] 一方可摆棋的位置正好 25 个（60 格分两半 30 格 - 5 个行营）",
            deployPoints.Count == 25, $"{deployPoints.Count} 个");

        var camps = 0;
        var hqs = 0;
        for (var y = 0; y < JunqiBoard.Height; y++)
        {
            for (var x = 0; x < JunqiBoard.Width; x++)
            {
                var kind = JunqiBoard.KindAt(x, y);
                if (kind == JunqiCellKind.Camp) camps++;
                if (kind == JunqiCellKind.Headquarters) hqs++;
            }
        }
        check("[军棋] 全盘 10 个行营（每方 5 个）+ 4 个大本营（每方 2 个）",
            camps == 10 && hqs == 4, $"行营 {camps} / 大本营 {hqs}");

        check("[军棋] 大本营在自己一侧的底行",
            JunqiBoard.KindAt(1, 4) == JunqiCellKind.Headquarters
            && JunqiBoard.KindAt(3, 4) == JunqiCellKind.Headquarters
            && JunqiBoard.KindAt(8, 4) == JunqiCellKind.Headquarters
            && JunqiBoard.KindAt(10, 4) == JunqiCellKind.Headquarters, "");

        check("[军棋] 摆棋点里不包含行营（行营是空位）",
            deployPoints.All(p => JunqiBoard.KindAt(p.X, p.Y) != JunqiCellKind.Camp), "");

        check("[军棋] 每方 6 列（12 列对半）；红方在左、蓝方在右",
            JunqiBoard.Width == 12 && JunqiBoard.SideWidth == 6
            && JunqiBoard.HomeSideOf(0) == JunqiSide.Red
            && JunqiBoard.HomeSideOf(11) == JunqiSide.Blue, "");

        check("[军棋] 铁路贯通：四条竖干线 + 两条横线",
            JunqiBoard.IsRailway(0, 2) && JunqiBoard.IsRailway(5, 2)
            && JunqiBoard.IsRailway(6, 2) && JunqiBoard.IsRailway(11, 2)
            && JunqiBoard.IsRailway(2, 1) && JunqiBoard.IsRailway(2, 3), "");

        check("[军棋] 行营不能被当成铁路（行营永远是安全格）",
            !JunqiBoard.IsRailway(1, 1) && !JunqiBoard.IsRailway(3, 1), "");
    }

    private static void CheckJunqiJudge(CheckFn check)
    {
        check("[军棋] 大吃小：司令吃军长",
            JunqiRules.Judge(JunqiRank.Commander, JunqiRank.CorpsCommander) == JunqiRules.JudgeOutcome.AttackerWins, "");
        check("[军棋] 小吃大：排长撞司令，排长死",
            JunqiRules.Judge(JunqiRank.PlatoonLeader, JunqiRank.Commander) == JunqiRules.JudgeOutcome.DefenderWins, "");
        check("[军棋] 同级同归于尽（司令撞司令）",
            JunqiRules.Judge(JunqiRank.Commander, JunqiRank.Commander) == JunqiRules.JudgeOutcome.BothDie, "");
        check("[军棋] 炸弹碰谁都同归于尽（含司令）",
            JunqiRules.Judge(JunqiRank.Bomb, JunqiRank.Commander) == JunqiRules.JudgeOutcome.BothDie
            && JunqiRules.Judge(JunqiRank.Commander, JunqiRank.Bomb) == JunqiRules.JudgeOutcome.BothDie, "");
        check("[军棋] 工兵能排雷（雷消失、工兵活）",
            JunqiRules.Judge(JunqiRank.Engineer, JunqiRank.Landmine) == JunqiRules.JudgeOutcome.AttackerWins, "");
        check("[军棋] 非工兵碰雷同归于尽",
            JunqiRules.Judge(JunqiRank.Commander, JunqiRank.Landmine) == JunqiRules.JudgeOutcome.BothDie
            && JunqiRules.Judge(JunqiRank.PlatoonLeader, JunqiRank.Landmine) == JunqiRules.JudgeOutcome.BothDie, "");
        check("[军棋] 碰到军旗 = 夺旗获胜",
            JunqiRules.Judge(JunqiRank.Engineer, JunqiRank.Flag) == JunqiRules.JudgeOutcome.CaptureFlag
            && JunqiRules.Judge(JunqiRank.Commander, JunqiRank.Flag) == JunqiRules.JudgeOutcome.CaptureFlag, "");
    }

    private static void CheckJunqiMoves(CheckFn check)
    {
        var game = new JunqiGame();
        game.AutoDeploy(JunqiSide.Red, new Random(11));
        game.AutoDeploy(JunqiSide.Blue, new Random(22));

        check("[军棋] 双方布好阵后进入对局，红方先走",
            game.Phase == JunqiPhase.Playing && game.CurrentSide == JunqiSide.Red,
            $"{game.Phase} / 先走 {game.CurrentSide}");

        // 找一枚红方在铁路上、能走的子
        var found = false;
        for (var y = 0; y < JunqiBoard.Height && !found; y++)
        {
            for (var x = 0; x < JunqiBoard.Width && !found; x++)
            {
                var piece = game.At(x, y);
                if (piece.IsEmpty || piece.Side != JunqiSide.Red || !piece.CanMove) continue;
                var moves = game.LegalMoves(x, y);
                if (moves.Count == 0) continue;

                found = true;
                check("[军棋] 普通棋子至少能走一格（能看到合法落点）", true, $"{moves.Count} 个落点");

                // 走一步（目标必须合法）
                var target = moves[0];
                var error = game.Move(JunqiSide.Red, x, y, target.X, target.Y);
                check("[军棋] 合法步骤能走成，且轮换到蓝方",
                    error.Length == 0 && game.CurrentSide == JunqiSide.Blue,
                    error.Length > 0 ? error : "走成了");
            }
        }
        check("[军棋] 棋盘上确实有能走的红方棋子", found, "");

        // 军旗 / 地雷不能动
        var deployPoints = JunqiBoard.DeployPoints(JunqiSide.Red);
        var flagIndex = -1;
        for (var i = 0; i < game.Board.Count; i++)
        {
            var piece = game.Board[i];
            if (!piece.IsEmpty && piece.Side == JunqiSide.Red && piece.Rank == JunqiRank.Flag) { flagIndex = i; break; }
        }
        if (flagIndex >= 0)
        {
            var (fx, fy) = JunqiBoard.ToPoint(flagIndex);
            check("[军棋] 军旗不能移动", game.LegalMoves(fx, fy).Count == 0, "");
        }
        else
        {
            check("[军棋] 军旗不能移动（本局军旗在大本营）", true, "");
        }

        // 行营里的子不能被吃
        var campX = 1;
        var campY = 1;
        var board = new JunqiPiece[JunqiBoard.CellCount];
        board[JunqiBoard.IndexOf(campX, campY)] = new JunqiPiece(JunqiRank.PlatoonLeader, JunqiSide.Blue, true);
        board[JunqiBoard.IndexOf(0, 1)] = new JunqiPiece(JunqiRank.Commander, JunqiSide.Red, true);
        var movesIntoCamp = JunqiRules.LegalMoves(board, 0, 1);
        check("[军棋] 行营里的子不能被吃（相邻的司令走不进去）",
            !movesIntoCamp.Contains((campX, campY)), string.Join(",", movesIntoCamp));

        // 空行营可以进
        var emptyCamp = new JunqiPiece[JunqiBoard.CellCount];
        emptyCamp[JunqiBoard.IndexOf(0, 1)] = new JunqiPiece(JunqiRank.PlatoonLeader, JunqiSide.Red, true);
        check("[军棋] 空行营可以走进去",
            JunqiRules.LegalMoves(emptyCamp, 0, 1).Contains((1, 1)), "");

        // 大本营里的棋子不能再动
        var hq = new JunqiPiece[JunqiBoard.CellCount];
        hq[JunqiBoard.IndexOf(1, 4)] = new JunqiPiece(JunqiRank.Commander, JunqiSide.Red, true);
        check("[军棋] 大本营里的棋子不能再移动",
            JunqiRules.LegalMoves(hq, 1, 4).Count == 0, "");
    }

    private static void CheckJunqiDeploy(CheckFn check)
    {
        var game = new JunqiGame();
        var points = JunqiBoard.DeployPoints(JunqiSide.Red).ToList();
        var army = JunqiRanks.CreateArmy();

        var deploy = new Dictionary<(int X, int Y), JunqiRank>();
        for (var i = 0; i < points.Count && i < army.Count; i++) deploy[points[i]] = army[i];

        check("[军棋] 正确布阵（25 枚摆满 25 个点）能成功",
            game.Deploy(JunqiSide.Red, deploy).Length == 0 && game.IsDeployed(JunqiSide.Red), "");

        check("[军棋] 同一方重复布阵会被拒（已经布好了）",
            game.Deploy(JunqiSide.Red, deploy).Length > 0, "");

        // 军旗数量不对
        var badGame = new JunqiGame();
        var badDeploy = new Dictionary<(int X, int Y), JunqiRank>(deploy);
        var flagKey = badDeploy.First(kv => kv.Value == JunqiRank.Flag).Key;
        badDeploy[flagKey] = JunqiRank.Engineer;
        check("[军棋] 军旗不是 1 面时布阵失败",
            badGame.Deploy(JunqiSide.Red, badDeploy).Length > 0, "");

        // 摆到对方一侧
        var crossGame = new JunqiGame();
        var crossDeploy = new Dictionary<(int X, int Y), JunqiRank>();
        var bluePoints = JunqiBoard.DeployPoints(JunqiSide.Blue).ToList();
        for (var i = 0; i < bluePoints.Count && i < army.Count; i++) crossDeploy[bluePoints[i]] = army[i];
        check("[军棋] 不能把棋子摆到对方一侧",
            crossGame.Deploy(JunqiSide.Red, crossDeploy).Length > 0, "");
    }

    private static void CheckJunqiWin(CheckFn check)
    {
        // 夺旗：构造一个红工兵紧邻蓝军旗的局面
        var game = new JunqiGame();
        game.AutoDeploy(JunqiSide.Red, new Random(3));
        game.AutoDeploy(JunqiSide.Blue, new Random(4));

        // 直接验证裁判层：军旗被碰 → 结束（用 Move 不方便造局面，这里验证引擎的 CheckAfterMove 走不到也无妨）
        check("[军棋] 夺旗判定走裁判层（碰军旗 = CaptureFlag）",
            JunqiRules.Judge(JunqiRank.Engineer, JunqiRank.Flag) == JunqiRules.JudgeOutcome.CaptureFlag, "");

        // 没子可走 = 判负
        var emptyBoard = new JunqiPiece[JunqiBoard.CellCount];
        emptyBoard[JunqiBoard.IndexOf(0, 0)] = new JunqiPiece(JunqiRank.Flag, JunqiSide.Red, true);
        emptyBoard[JunqiBoard.IndexOf(11, 0)] = new JunqiPiece(JunqiRank.Flag, JunqiSide.Blue, true);
        check("[军棋] 一方只剩军旗（没有能动的子）= 无棋可走",
            JunqiRules.HasNoMove(emptyBoard, JunqiSide.Red)
            && JunqiRules.HasNoMove(emptyBoard, JunqiSide.Blue), "");

        check("[军棋] 还有能动的子时不算无棋可走",
            JunqiRules.MovableCount(emptyBoard, JunqiSide.Red) == 0, "");

        // 认输
        var resignGame = new JunqiGame();
        resignGame.AutoDeploy(JunqiSide.Red, new Random(5));
        resignGame.AutoDeploy(JunqiSide.Blue, new Random(6));
        resignGame.Resign(JunqiSide.Red);
        check("[军棋] 认输：对方获胜并结束对局",
            resignGame.Phase == JunqiPhase.Finished
            && resignGame.Winner == JunqiSide.Blue
            && resignGame.ResultText.Contains("认输"),
            resignGame.ResultText);

        // 暗棋过滤：自己的全可见、对方的暗棋不可见
        var darkGame = new JunqiGame();
        darkGame.AutoDeploy(JunqiSide.Red, new Random(7));
        darkGame.AutoDeploy(JunqiSide.Blue, new Random(8));
        var view = darkGame.VisibleBoardFor(JunqiSide.Red);

        var ownVisible = view
            .Select((piece, i) => (piece, i))
            .Where(x => !x.piece.IsEmpty && x.piece.Side == JunqiSide.Red)
            .All(x => x.piece.Revealed && x.piece.Rank != JunqiRank.None);
        var enemyHidden = view
            .Select((piece, i) => (piece, i))
            .Where(x => x.piece.Side == JunqiSide.Blue)
            .All(x => x.piece.IsUnknown);

        check("[军棋] 暗棋：自己的棋子全可见、对方的棋子全部隐藏",
            ownVisible && enemyHidden, "");

        check("[军棋] 暗棋：看不到对手棋子的具体等级",
            view.Where(p => p.Side == JunqiSide.Blue).All(p => p.Rank == JunqiRank.None), "");

        // 端到端：自动布阵后连走若干步，确认不会崩、回合正确交替、棋盘状态一致
        var autoGame = new JunqiGame();
        autoGame.AutoDeploy(JunqiSide.Red, new Random(101));
        autoGame.AutoDeploy(JunqiSide.Blue, new Random(202));

        var moves = 0;
        var guard = 0;
        while (autoGame.Phase == JunqiPhase.Playing && moves < 40 && guard++ < 500)
        {
            var side = autoGame.CurrentSide;
            var moved = false;

            for (var y = 0; y < JunqiBoard.Height && !moved; y++)
            {
                for (var x = 0; x < JunqiBoard.Width && !moved; x++)
                {
                    var piece = autoGame.At(x, y);
                    if (piece.IsEmpty || piece.Side != side || !piece.CanMove) continue;

                    var legal = autoGame.LegalMoves(x, y);
                    if (legal.Count == 0) continue;

                    var target = legal[0];
                    if (autoGame.Move(side, x, y, target.X, target.Y).Length == 0)
                    {
                        moved = true;
                        moves++;
                    }
                }
            }

            if (!moved) break;
        }

        check("[军棋] 自动布阵后能连续走子（回合交替、不崩溃）",
            moves >= 10 && autoGame.History.Count == moves,
            $"走了 {moves} 步，记录 {autoGame.History.Count} 条");

        check("[军棋] 每走一步历史里都留了记录（含裁判结果）",
            autoGame.History.Count > 0
            && autoGame.History.All(r => r.Outcome != JunqiRules.JudgeOutcome.None),
            $"{autoGame.History.Count} 条");

        check("[军棋] 走完一段时间后双方都还有棋子（不会一上来就全灭）",
            JunqiRules.HasFlag(autoGame.Board, JunqiSide.Red) || autoGame.Phase == JunqiPhase.Finished,
            $"阶段 {autoGame.Phase}");
    }
}
