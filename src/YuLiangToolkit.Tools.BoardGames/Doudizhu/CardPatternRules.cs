namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>牌型（经典斗地主的全部合法类型）。</summary>
public enum CardPatternKind
{
    /// <summary>非法（不是任何牌型）。</summary>
    None = 0,

    /// <summary>单张。</summary>
    Single = 1,

    /// <summary>对子。</summary>
    Pair = 2,

    /// <summary>三张。</summary>
    Triple = 3,

    /// <summary>三带一。</summary>
    TripleWithSingle = 4,

    /// <summary>三带二（带一对）。</summary>
    TripleWithPair = 5,

    /// <summary>顺子（至少 5 张连续，3~A，不含 2 和王）。</summary>
    Straight = 6,

    /// <summary>连对（至少 3 对连续）。</summary>
    PairStraight = 7,

    /// <summary>飞机（连续的三张，至少 2 组）。</summary>
    Airplane = 8,

    /// <summary>飞机带单（每组三张带一个单张）。</summary>
    AirplaneWithSingles = 9,

    /// <summary>飞机带对（每组三张带一对）。</summary>
    AirplaneWithPairs = 10,

    /// <summary>四带二（四张 + 两张单）。</summary>
    FourWithTwoSingles = 11,

    /// <summary>四带两对（四张 + 两对）。</summary>
    FourWithTwoPairs = 12,

    /// <summary>炸弹（四张同点）。</summary>
    Bomb = 13,

    /// <summary>王炸（双王，最大）。</summary>
    Rocket = 14,
}

/// <summary>
/// 一副牌的「牌型」：类型 + 比较用的主键 + 张数。
///
/// <para><b>比较规则</b>（经典斗地主）：<see cref="Kind"/> 不同不能比（炸弹 / 王炸除外），
/// 同类型比 <see cref="Key"/>（顺子 / 连对 / 飞机 / 三带等都比三张（或最大那张）的点数），
/// 同 Key 不比（必须更大，不许平）。<see cref="Kind"/> 相同、张数不同也不能比（顺子长度要一致）。</para>
/// </summary>
/// <param name="Kind">牌型。</param>
/// <param name="Key">比大小的主键（点数值；王炸是 0，用它自己的类型比较）。</param>
/// <param name="Length">牌的张数（顺子 / 连对 / 飞机要长度一致才能比）。</param>
public readonly record struct CardPattern(CardPatternKind Kind, int Key, int Length)
{
    /// <summary>不是合法牌型。</summary>
    public static CardPattern Invalid { get; } = new(CardPatternKind.None, 0, 0);

    /// <summary>是不是合法牌型。</summary>
    public bool IsValid => Kind != CardPatternKind.None;

    /// <summary>是不是炸弹（含王炸）—— 炸弹可以压任何非炸弹牌型。</summary>
    public bool IsBomb => Kind is CardPatternKind.Bomb or CardPatternKind.Rocket;

    /// <summary>中文名（界面 / 聊天提示用）。</summary>
    public string DisplayName => Kind switch
    {
        CardPatternKind.Single => "单张",
        CardPatternKind.Pair => "对子",
        CardPatternKind.Triple => "三张",
        CardPatternKind.TripleWithSingle => "三带一",
        CardPatternKind.TripleWithPair => "三带二",
        CardPatternKind.Straight => "顺子",
        CardPatternKind.PairStraight => "连对",
        CardPatternKind.Airplane => "飞机",
        CardPatternKind.AirplaneWithSingles => "飞机带单",
        CardPatternKind.AirplaneWithPairs => "飞机带对",
        CardPatternKind.FourWithTwoSingles => "四带二",
        CardPatternKind.FourWithTwoPairs => "四带两对",
        CardPatternKind.Bomb => "炸弹",
        CardPatternKind.Rocket => "王炸",
        _ => "不成牌型",
    };
}

/// <summary>
/// 牌型识别与比大小（<b>纯逻辑</b>，自检可直接断言）。
///
/// <para><b>顺子 / 连对 / 飞机的边界</b>：只用 3~A（<c>Value</c> 3~14），
/// 不含 2 和王 —— 这是经典规则；<c>10 J Q K A</c> 合法、<c>J Q K A 2</c> 不合法。</para>
///
/// <para><b>双副牌（炸弹多模式）</b>：同一张牌可能出现两次，但牌型判定只看点数分布，
/// 所以两副牌的组合照样能识别（比如 8 张同点其实是两个炸弹，但一次出 8 张同点不是合法牌型）。</para>
/// </summary>
public static class CardPatternRules
{
    /// <summary>顺子最少 5 张。</summary>
    public const int MinStraightLength = 5;

    /// <summary>连对最少 3 对（6 张）。</summary>
    public const int MinPairStraightPairs = 3;

    /// <summary>飞机最少 2 组三张。</summary>
    public const int MinAirplaneTriples = 2;

    /// <summary>识别一手牌的牌型；不是合法牌型返回 <see cref="CardPattern.Invalid"/>。</summary>
    public static CardPattern Detect(IReadOnlyList<Card> cards)
    {
        if (cards is null || cards.Count == 0) return CardPattern.Invalid;

        var count = cards.Count;
        var byValue = new SortedDictionary<int, int>();
        foreach (var card in cards)
        {
            var value = card.Value;
            byValue[value] = byValue.TryGetValue(value, out var n) ? n + 1 : 1;
        }

        var groups = byValue.OrderBy(kv => kv.Key).ToList();

        // ---- 王炸 ----
        if (count == 2 && groups.Count == 2
            && groups[0].Key == (int)CardRank.BlackJoker && groups[1].Key == (int)CardRank.RedJoker
            && groups[0].Value == 1 && groups[1].Value == 1)
        {
            return new CardPattern(CardPatternKind.Rocket, 0, 2);
        }

        // ---- 单张 / 对子 / 三张 / 炸弹 ----
        if (groups.Count == 1)
        {
            var value = groups[0].Key;
            return groups[0].Value switch
            {
                1 => new CardPattern(CardPatternKind.Single, value, 1),
                2 => new CardPattern(CardPatternKind.Pair, value, 2),
                3 => new CardPattern(CardPatternKind.Triple, value, 3),
                4 => new CardPattern(CardPatternKind.Bomb, value, 4),
                _ => CardPattern.Invalid,      // 5 张以上同点（双副牌凑的）不是合法牌型
            };
        }

        // ---- 三带一 / 三带二 ----
        var triples = groups.Where(g => g.Value == 3).ToList();
        var quads = groups.Where(g => g.Value == 4).ToList();
        var pairs = groups.Where(g => g.Value == 2).ToList();
        var singles = groups.Where(g => g.Value == 1).ToList();

        if (count == 4 && triples.Count == 1 && singles.Count == 1)
            return new CardPattern(CardPatternKind.TripleWithSingle, triples[0].Key, 4);

        if (count == 5 && triples.Count == 1 && pairs.Count == 1)
            return new CardPattern(CardPatternKind.TripleWithPair, triples[0].Key, 5);

        // ---- 四带二 / 四带两对 ----
        if (count == 6 && quads.Count == 1)
        {
            // 四张 + 两张单（不能是一对？经典规则允许「四带二」带两张单，也允许是同一对拆开，按两张单处理）
            var rest = count - 4;
            if (rest == 2 && singles.Count == 2) return new CardPattern(CardPatternKind.FourWithTwoSingles, quads[0].Key, 6);
            if (rest == 2 && pairs.Count == 1) return new CardPattern(CardPatternKind.FourWithTwoSingles, quads[0].Key, 6);
        }

        if (count == 8 && quads.Count == 1 && pairs.Count == 2)
            return new CardPattern(CardPatternKind.FourWithTwoPairs, quads[0].Key, 8);

        // ---- 顺子 ----
        if (count >= MinStraightLength && groups.All(g => g.Value == 1) && IsConsecutive(groups.Select(g => g.Key)))
        {
            var max = groups[^1].Key;
            return new CardPattern(CardPatternKind.Straight, max, count);
        }

        // ---- 连对 ----
        if (count >= MinPairStraightPairs * 2 && pairs.Count == groups.Count && groups.Count >= MinPairStraightPairs
            && IsConsecutive(groups.Select(g => g.Key)))
        {
            return new CardPattern(CardPatternKind.PairStraight, groups[^1].Key, count);
        }

        // ---- 飞机（纯飞机 / 带单 / 带对） ----
        if (triples.Count >= MinAirplaneTriples)
        {
            var tripleKeys = triples.Select(t => t.Key).ToList();
            var pureLength = triples.Count * 3;

            // 主三张必须连续（不含 2 / 王）
            if (IsConsecutive(tripleKeys) && tripleKeys.All(k => k <= (int)CardRank.Ace))
            {
                if (count == pureLength)
                    return new CardPattern(CardPatternKind.Airplane, tripleKeys[^1], count);

                // 带单：多出来的牌全部是单张（也不能带王炸当单张的「对」—— 按张数算即可）
                if (count == pureLength + triples.Count && singles.Count + pairs.Count * 2 + quads.Count * 4 == count - pureLength)
                {
                    var wingCount = count - pureLength;
                    var wingGroups = groups.Count - triples.Count;
                    if (wingCount == triples.Count && wingGroups == wingCount)
                        return new CardPattern(CardPatternKind.AirplaneWithSingles, tripleKeys[^1], count);
                }

                // 带对：多出来的牌刚好是对子
                if (count == pureLength + triples.Count * 2 && pairs.Count == triples.Count
                    && groups.Count == triples.Count + pairs.Count)
                {
                    return new CardPattern(CardPatternKind.AirplaneWithPairs, tripleKeys[^1], count);
                }
            }
        }

        return CardPattern.Invalid;
    }

    /// <summary>
    /// 这一手能不能压过 <paramref name="previous"/>（null / 非法 = 场上没有牌，随便出）。
    /// </summary>
    public static bool CanBeat(CardPattern current, CardPattern? previous)
    {
        if (!current.IsValid) return false;
        if (previous is not { IsValid: true } prev) return true;

        // 王炸最大：谁都压不过
        if (prev.Kind == CardPatternKind.Rocket) return false;

        // 王炸压一切
        if (current.Kind == CardPatternKind.Rocket) return true;

        // 炸弹：炸弹压非炸弹；炸弹之间比点数
        if (current.Kind == CardPatternKind.Bomb)
        {
            if (prev.Kind != CardPatternKind.Bomb) return true;
            return current.Key > prev.Key;
        }

        // 非炸弹压炸弹：不行
        if (prev.Kind == CardPatternKind.Bomb) return false;

        // 同类型、同张数、点数更大
        return current.Kind == prev.Kind
               && current.Length == prev.Length
               && current.Key > prev.Key;
    }

    /// <summary>点数组是不是连续的（用于顺子 / 连对 / 飞机；调用方另外挡掉 2 与王）。</summary>
    private static bool IsConsecutive(IEnumerable<int> sortedKeys)
    {
        var keys = sortedKeys.ToList();
        if (keys.Count == 0) return false;
        if (keys.Any(k => k >= (int)CardRank.Two)) return false;      // 2 与王不参与
        for (var i = 1; i < keys.Count; i++)
        {
            if (keys[i] != keys[i - 1] + 1) return false;
        }
        return true;
    }

    /// <summary>
    /// 从一手牌里找出所有能出的牌型（提示 / 自动出牌用；只列「能压过 previous」的组合）。
    /// <para>只覆盖常用牌型（单 / 对 / 三 / 三带 / 顺 / 连对 / 飞机 / 炸弹 / 王炸），
    /// 四带二这种不常用的不做提示 —— 提示是帮玩家快速出牌，不是穷举器。</para>
    /// </summary>
    /// <param name="hand">本端手牌。</param>
    /// <param name="previous">要压的牌型（null = 随便出）。</param>
    /// <param name="maxResults">最多返回多少种（防止手牌多时结果爆炸）。</param>
    public static IReadOnlyList<CardPattern> SuggestPatterns(IReadOnlyList<Card> hand, CardPattern? previous, int maxResults = 40)
    {
        var results = new List<CardPattern>();
        if (hand is null || hand.Count == 0) return results;

        var maxValue = hand.Max(c => c.Value);

        // 单张
        foreach (var card in hand)
        {
            var pattern = new CardPattern(CardPatternKind.Single, card.Value, 1);
            if (CanBeat(pattern, previous) && !results.Contains(pattern)) results.Add(pattern);
        }

        // 对子 / 三张 / 炸弹（按点数统计）
        var byValue = hand.GroupBy(c => c.Value).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (value, cards) in byValue.OrderBy(kv => kv.Key))
        {
            if (cards.Count >= 2)
            {
                var pair = new CardPattern(CardPatternKind.Pair, value, 2);
                if (CanBeat(pair, previous)) results.Add(pair);
            }
            if (cards.Count >= 3)
            {
                var triple = new CardPattern(CardPatternKind.Triple, value, 3);
                if (CanBeat(triple, previous)) results.Add(triple);
            }
            if (cards.Count >= 4)
            {
                var bomb = new CardPattern(CardPatternKind.Bomb, value, 4);
                if (CanBeat(bomb, previous)) results.Add(bomb);
            }
        }

        // 王炸
        var hasBlack = byValue.ContainsKey((int)CardRank.BlackJoker);
        var hasRed = byValue.ContainsKey((int)CardRank.RedJoker);
        if (hasBlack && hasRed)
        {
            var rocket = new CardPattern(CardPatternKind.Rocket, 0, 2);
            if (CanBeat(rocket, previous)) results.Add(rocket);
        }

        // 三带一 / 三带二
        var triplesInHand = byValue.Where(kv => kv.Value.Count >= 3).Select(kv => kv.Key).ToList();
        var singlesInHand = byValue.Where(kv => kv.Value.Count >= 1).Select(kv => kv.Key).ToList();
        var pairsInHand = byValue.Where(kv => kv.Value.Count >= 2).Select(kv => kv.Key).ToList();
        foreach (var tripleKey in triplesInHand)
        {
            foreach (var singleKey in singlesInHand.Where(k => k != tripleKey))
            {
                var pattern = new CardPattern(CardPatternKind.TripleWithSingle, tripleKey, 4);
                if (CanBeat(pattern, previous)) results.Add(pattern);
            }
            foreach (var pairKey in pairsInHand.Where(k => k != tripleKey))
            {
                var pattern = new CardPattern(CardPatternKind.TripleWithPair, tripleKey, 5);
                if (CanBeat(pattern, previous)) results.Add(pattern);
            }
        }

        // 顺子（3~A 的连续区间，长度 5 起）
        var available = byValue.Keys.Where(k => k <= (int)CardRank.Ace).OrderBy(k => k).ToList();
        foreach (var run in ConsecutiveRuns(available))
        {
            for (var start = 0; start < run.Count; start++)
            {
                for (var length = MinStraightLength; start + length <= run.Count; length++)
                {
                    var pattern = new CardPattern(CardPatternKind.Straight, run[start + length - 1], length);
                    if (CanBeat(pattern, previous)) results.Add(pattern);
                }
            }
        }

        // 连对
        var pairKeys = pairsInHand.Where(k => k <= (int)CardRank.Ace).OrderBy(k => k).ToList();
        foreach (var run in ConsecutiveRuns(pairKeys))
        {
            for (var start = 0; start < run.Count; start++)
            {
                for (var pairs = MinPairStraightPairs; start + pairs <= run.Count; pairs++)
                {
                    var pattern = new CardPattern(CardPatternKind.PairStraight, run[start + pairs - 1], pairs * 2);
                    if (CanBeat(pattern, previous)) results.Add(pattern);
                }
            }
        }

        // 飞机（纯三张，连续 2 组以上）
        var tripleKeys = triplesInHand.Where(k => k <= (int)CardRank.Ace).OrderBy(k => k).ToList();
        foreach (var run in ConsecutiveRuns(tripleKeys))
        {
            for (var start = 0; start < run.Count; start++)
            {
                for (var groups = MinAirplaneTriples; start + groups <= run.Count; groups++)
                {
                    var pattern = new CardPattern(CardPatternKind.Airplane, run[start + groups - 1], groups * 3);
                    if (CanBeat(pattern, previous)) results.Add(pattern);
                }
            }
        }

        _ = maxValue;
        return results.Take(maxResults).ToList();
    }

    /// <summary>把排好序的点数组切成一段段连续区间（1,2,3,5,6 → [1,2,3]、[5,6]）。</summary>
    private static List<List<int>> ConsecutiveRuns(IReadOnlyList<int> sorted)
    {
        var runs = new List<List<int>>();
        if (sorted.Count == 0) return runs;

        var current = new List<int> { sorted[0] };
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == sorted[i - 1] + 1) current.Add(sorted[i]);
            else
            {
                runs.Add(current);
                current = new List<int> { sorted[i] };
            }
        }
        runs.Add(current);
        return runs;
    }
}
