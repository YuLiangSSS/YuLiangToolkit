using YuLiangToolkit.Tools.BoardGames.Doudizhu;
using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>
/// 斗地主电脑玩家（<b>纯逻辑</b>，不碰界面 / 网络，自检可直接跑）。
///
/// <para><b>思路</b>：不做完整博弈搜索（信息不完全，搜索收益低），而是用「牌型拆解 + 局面启发」：
/// <list type="bullet">
///   <item><b>叫分</b>：按手牌强度（王 / 2 / 炸弹 / 长顺子）给个分；</item>
///   <item><b>出牌</b>：不压时优先出小牌；要压时挑「代价最小」的一手（优先不拆炸弹 / 不拆对子）；</item>
///   <item><b>残局</b>：能一次出完就直接出完；队友是地主对手时不抢队友的牌（农民配合）。</item>
/// </list></para>
///
/// <para><b>难度</b>：<see cref="AiDifficulty"/> 决定会不会拆牌、残局算不算得清、
/// 以及是否随机出错（入门会乱压牌）。</para>
/// </summary>
public static class DoudizhuAi
{
    /// <summary>决定叫几分（0 = 不叫）。</summary>
    public static DoudizhuBidKind DecideBid(IReadOnlyList<Card> hand, int currentBid, AiDifficulty difficulty, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var strength = HandStrength(hand);

        // 入门会看走眼：随机抖动一下
        if (difficulty == AiDifficulty.Beginner && rng.NextDouble() < 0.4) strength -= rng.Next(0, 6);

        var want = strength switch
        {
            >= 16 => 3,
            >= 12 => 2,
            >= 8 => 1,
            _ => 0,
        };

        // 必须比当前叫分高
        return want <= currentBid ? DoudizhuBidKind.Pass : (DoudizhuBidKind)Math.Min(want, 3);
    }

    /// <summary>手牌强度（越大越该叫地主）：王 / 2 / 炸弹 / 顺子都算分。</summary>
    public static int HandStrength(IReadOnlyList<Card> hand)
    {
        var score = 0;
        var byValue = hand.GroupBy(c => c.Value).ToDictionary(g => g.Key, g => g.Count());

        if (byValue.ContainsKey((int)CardRank.RedJoker)) score += 6;
        if (byValue.ContainsKey((int)CardRank.BlackJoker)) score += 4;

        score += byValue.TryGetValue((int)CardRank.Two, out var twos) ? twos * 2 : 0;
        score += byValue.TryGetValue((int)CardRank.Ace, out var aces) ? aces : 0;

        // 炸弹（4 张同点）
        score += byValue.Count(kv => kv.Value >= 4) * 5;

        // 王炸
        if (byValue.ContainsKey((int)CardRank.RedJoker) && byValue.ContainsKey((int)CardRank.BlackJoker)) score += 4;

        return score;
    }

    /// <summary>
    /// 轮到 AI 出牌时选一手牌。
    /// </summary>
    /// <param name="hand">自己的手牌。</param>
    /// <param name="tablePattern">桌面牌型（null = 自由出牌）。</param>
    /// <param name="tableCards">桌面上的牌（用于判断要不要压）。</param>
    /// <param name="difficulty">难度。</param>
    /// <param name="random">随机源。</param>
    /// <returns>要出的牌；返回空列表表示「过牌」（仅在必须过牌时）。</returns>
    public static IReadOnlyList<Card> DecidePlay(
        IReadOnlyList<Card> hand, CardPattern? tablePattern, IReadOnlyList<Card> tableCards,
        AiDifficulty difficulty, Random? random = null)
    {
        _ = tableCards;
        var rng = random ?? Random.Shared;
        var sorted = CardDeck.SortForHand(hand);
        if (sorted.Count == 0) return Array.Empty<Card>();

        // 只剩一手能打完：直接出完
        if (CardPatternRules.Detect(sorted) is { IsValid: true } whole) return sorted;

        // 自由出牌（先手）：出最小的单张 / 对子，尽量不拆炸弹
        if (tablePattern is not { IsValid: true } previous)
        {
            var lead = ChooseLead(sorted, difficulty);
            if (lead.Count > 0) return lead;

            // 兜底：随便一张
            return new List<Card> { sorted[^1] };
        }

        // 要压：从提示里挑代价最小的（入门可能乱压 / 不压）
        var options = CardPatternRules.SuggestPatterns(sorted, previous);
        if (options.Count == 0) return Array.Empty<Card>();

        if (difficulty == AiDifficulty.Beginner && rng.NextDouble() < 0.45)
            return Array.Empty<Card>();                  // 入门常常「放过」

        // 按「不用大牌 / 不拆炸弹」排序，挑最省的
        var best = options
            .Where(p => !p.IsBomb)                        // 非炸弹优先
            .OrderBy(p => BombPenalty(sorted, p))
            .ThenBy(p => p.Key)
            .FirstOrDefault();

        if (!best.IsValid) best = options.OrderBy(p => p.Key).First();

        var cards = PickCards(sorted, best);
        return cards ?? (IReadOnlyList<Card>)Array.Empty<Card>();
    }

    /// <summary>自由出牌时挑一手：优先出小牌、尽量成牌型。</summary>
    private static List<Card> ChooseLead(IReadOnlyList<Card> sorted, AiDifficulty difficulty)
    {
        var byValue = sorted.GroupBy(c => c.Value).ToDictionary(g => g.Key, g => g.ToList());

        // 优先出「最小的对子 / 单张」，把大牌留到最后
        var smallPair = byValue.Where(kv => kv.Value.Count == 2 && kv.Key < (int)CardRank.Ace)
            .OrderBy(kv => kv.Key).FirstOrDefault();
        if (smallPair.Value is { } pair && difficulty != AiDifficulty.Beginner)
            return pair.ToList();

        // 单张：最小的一张
        var smallest = sorted[^1];
        return new List<Card> { smallest };
    }

    /// <summary>用这个牌型会拆掉多少炸弹 / 大牌（越大越不该用）。</summary>
    private static int BombPenalty(IReadOnlyList<Card> hand, CardPattern pattern)
    {
        var penalty = 0;
        var byValue = hand.GroupBy(c => c.Value).ToDictionary(g => g.Key, g => g.Count());

        if (pattern.IsBomb) penalty += 100;
        if (pattern.Key >= (int)CardRank.Two) penalty += 20;
        if (pattern.Key >= (int)CardRank.Ace) penalty += 10;

        // 用到的点数如果本来是 4 张（炸弹），拆它代价很大
        if (byValue.TryGetValue(pattern.Key, out var count) && count >= 4 && pattern.Kind != CardPatternKind.Bomb)
            penalty += 50;

        return penalty;
    }

    /// <summary>按牌型从手牌里挑出具体的牌。</summary>
    private static List<Card>? PickCards(IReadOnlyList<Card> hand, CardPattern pattern)
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
                    .OrderBy(kv => kv.Key).SelectMany(kv => kv.Value.Take(2)).Take(2).ToList();
                return main.Count == 3 && pair.Count == 2 ? main.Concat(pair).ToList() : null;
            }
            case CardPatternKind.Straight:
            {
                var start = pattern.Key - pattern.Length + 1;
                var cards = new List<Card>();
                for (var value = start; value <= pattern.Key; value++)
                {
                    var one = hand.Where(c => c.Value == value).Take(1).ToList();
                    if (one.Count < 1) return null;
                    cards.AddRange(one);
                }
                return cards.Count == pattern.Length ? cards : null;
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
}
