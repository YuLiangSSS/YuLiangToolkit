namespace YuLiangToolkit.Tools.BoardGames.Doudizhu;

/// <summary>
/// 牌面大小（3 最小，2 次之，王最大）。
/// <para>数值直接用来比大小：3~15 = 3~2，16 = 小王，17 = 大王。</para>
/// </summary>
public enum CardRank
{
    /// <summary>3。</summary>
    Three = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Jack = 11,
    Queen = 12,
    King = 13,
    Ace = 14,
    /// <summary>2（比 A 大、比王小）。</summary>
    Two = 15,
    /// <summary>小王。</summary>
    BlackJoker = 16,
    /// <summary>大王。</summary>
    RedJoker = 17,
}

/// <summary>花色（只影响显示，不影响大小）。</summary>
public enum CardSuit
{
    Spade = 0,
    Heart = 1,
    Club = 2,
    Diamond = 3,
    /// <summary>王（没有花色）。</summary>
    Joker = 4,
}

/// <summary>
/// 一张牌。<see cref="Deck"/> 是牌堆序号 —— 「炸弹多」模式用两副牌，
/// 同一张牌会出现两次（花色相同、<see cref="Deck"/> 不同），出牌判重时必须带上它。
/// </summary>
/// <param name="Rank">牌面。</param>
/// <param name="Suit">花色。</param>
/// <param name="Deck">第几副牌（单副牌恒为 0，双副牌是 0 / 1）。</param>
public readonly record struct Card(CardRank Rank, CardSuit Suit, int Deck = 0)
{
    /// <summary>是不是王（大小王都不参与顺子 / 连对 / 飞机）。</summary>
    public bool IsJoker => Rank is CardRank.BlackJoker or CardRank.RedJoker;

    /// <summary>是不是 2（2 也不参与顺子 / 连对 / 飞机）。</summary>
    public bool IsTwo => Rank == CardRank.Two;

    /// <summary>牌面点数（3~17）。</summary>
    public int Value => (int)Rank;

    /// <summary>显示用的短名（♠3 / ♥K / 小王 / 大王）。</summary>
    public string Text => Rank switch
    {
        CardRank.BlackJoker => "小王",
        CardRank.RedJoker => "大王",
        _ => Suit switch
        {
            CardSuit.Spade => "♠",
            CardSuit.Heart => "♥",
            CardSuit.Club => "♣",
            CardSuit.Diamond => "♦",
            _ => "",
        } + RankText,
    };

    /// <summary>牌面文本（不含花色）。</summary>
    public string RankText => Rank switch
    {
        CardRank.Ten => "10",
        CardRank.Jack => "J",
        CardRank.Queen => "Q",
        CardRank.King => "K",
        CardRank.Ace => "A",
        CardRank.Two => "2",
        CardRank.BlackJoker => "小王",
        CardRank.RedJoker => "大王",
        _ => ((int)Rank).ToString(),
    };

    /// <summary>是不是红色的牌（显示用）。</summary>
    public bool IsRed => Suit is CardSuit.Heart or CardSuit.Diamond || Rank == CardRank.RedJoker;

    public override string ToString() => Text;
}

/// <summary>牌堆：怎么造一副 / 两副牌、怎么洗、怎么按牌面排序。</summary>
public static class CardDeck
{
    /// <summary>单副牌 54 张。</summary>
    public const int SingleDeckSize = 54;

    /// <summary>「炸弹多」模式用两副牌（108 张，4 个王 = 2 个王炸、每种点数 8 张 = 更多炸弹）。</summary>
    public const int DoubleDeckSize = SingleDeckSize * 2;

    /// <summary>造一副（<paramref name="decks"/> 副）完整的牌，按副数编号。</summary>
    public static List<Card> Create(int decks = 1)
    {
        var cards = new List<Card>(SingleDeckSize * Math.Max(1, decks));
        for (var deck = 0; deck < Math.Max(1, decks); deck++)
        {
            foreach (var suit in new[] { CardSuit.Spade, CardSuit.Heart, CardSuit.Club, CardSuit.Diamond })
            {
                for (var value = (int)CardRank.Three; value <= (int)CardRank.Two; value++)
                    cards.Add(new Card((CardRank)value, suit, deck));
            }

            cards.Add(new Card(CardRank.BlackJoker, CardSuit.Joker, deck));
            cards.Add(new Card(CardRank.RedJoker, CardSuit.Joker, deck));
        }
        return cards;
    }

    /// <summary>Fisher–Yates 洗牌（传固定种子可复现，自检用）。</summary>
    public static void Shuffle<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// 手牌排序：大的在前（看牌习惯），同点数按花色、副数排 —— 保证同一手牌任何时刻排序结果一致。
    /// </summary>
    public static List<Card> SortForHand(IEnumerable<Card> cards)
        => cards
            .OrderByDescending(c => c.Value)
            .ThenBy(c => c.Suit)
            .ThenBy(c => c.Deck)
            .ToList();

    /// <summary>出牌区排序：小的在前（桌面上看得清是什么牌型）。</summary>
    public static List<Card> SortForTable(IEnumerable<Card> cards)
        => cards
            .OrderBy(c => c.Value)
            .ThenBy(c => c.Suit)
            .ThenBy(c => c.Deck)
            .ToList();
}
