namespace YuLiangToolkit.Tools.Calculator.Core.Kin;

/// <summary>这条关系链属于哪一支（只用于结果卡上的一句说明）。</summary>
public enum KinBranch
{
    /// <summary>就是我自己（链是空的）。</summary>
    Self = 0,

    /// <summary>直系长辈（只有爸爸 / 妈妈往上走）。</summary>
    Direct = 1,

    /// <summary>直系晚辈（只有儿子 / 女儿往下走）。</summary>
    OwnDescendant = 2,

    /// <summary>父系（第一步是爸爸）。</summary>
    Paternal = 3,

    /// <summary>母系（第一步是妈妈）。</summary>
    Maternal = 4,

    /// <summary>同辈旁系（第一步是兄弟姐妹）。</summary>
    Peer = 5,

    /// <summary>姻亲（链里出现了老公 / 老婆）。</summary>
    Affinal = 6,
}

/// <summary>
/// 关系链（从「我」一步步走到目标亲属）的纯逻辑：显示文案、辈分、性别、支系，
/// 以及<b>规范化</b>与<b>反向推导</b>。
///
/// <para><b>规范化</b>只做数学上必然成立的等价替换（自检逐条断言），例如：</para>
/// <list type="bullet">
///   <item>哥哥的爸爸 → 爸爸（兄弟姐妹的爸爸就是我的爸爸）；</item>
///   <item>爸爸的哥哥的爸爸 → 爸爸的爸爸（伯父的爸爸就是我爸爸的爸爸，即爷爷）；</item>
///   <item>儿子的哥哥 → 儿子、儿子的姐姐 → 女儿（我孩子的手足也是我的孩子）；</item>
///   <item>老婆的儿子 → 儿子（按婚生子女的常规理解）；</item>
///   <item>老公的老婆 → 我自己。</item>
/// </list>
/// 规范化之后链才能当称谓表的键（同一门亲戚不管怎么绕出来都落到同一个键上）。
/// </summary>
public static class KinPath
{
    /// <summary>规范化最多来回套用几轮（正常两三轮就稳定了，这里是防死循环的保险）。</summary>
    private const int MaxNormalizeRounds = 12;

    /// <summary>空链（= 我自己）。</summary>
    public static IReadOnlyList<KinStep> Empty { get; } = Array.Empty<KinStep>();

    /// <summary>
    /// 规范化：反复套用等价替换，直到链不再变化。返回新列表，不改入参。
    /// </summary>
    public static List<KinStep> Normalize(IReadOnlyList<KinStep>? steps)
    {
        var result = steps is null ? new List<KinStep>() : new List<KinStep>(steps);

        for (var round = 0; round < MaxNormalizeRounds; round++)
        {
            if (!ApplyOnce(result)) break;
        }
        return result;
    }

    /// <summary>套用一轮替换；返回是否改动了链。</summary>
    private static bool ApplyOnce(List<KinStep> steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var current = steps[i];

            // 老公的老婆 / 老婆的老公 → 回到我自己（一夫一妻的常规理解）
            if (current.IsSpouse() && i + 1 < steps.Count && steps[i + 1].IsSpouse()
                && steps[i + 1] != current)
            {
                steps.RemoveRange(i, 2);
                return true;
            }

            // 兄弟姐妹的爸爸 / 妈妈 → 我的爸爸 / 妈妈
            if (current.IsSibling() && i + 1 < steps.Count && steps[i + 1].IsParent())
            {
                steps.RemoveAt(i);
                return true;
            }

            // 兄弟姐妹的兄弟姐妹：长幼方向一致时能确定（哥哥的姐姐 = 我的姐姐、弟弟的妹妹 = 我的妹妹）；
            // 方向相反时（哥哥的弟弟）说不好是不是我本人，保持原样不动。
            if (current.IsSibling() && i + 1 < steps.Count && steps[i + 1].IsSibling())
            {
                var firstOlder = current is KinStep.OlderBrother or KinStep.OlderSister;
                var secondOlder = steps[i + 1] is KinStep.OlderBrother or KinStep.OlderSister;
                if (firstOlder == secondOlder)
                {
                    var male = steps[i + 1].IsMale();
                    steps[i + 1] = secondOlder
                        ? (male ? KinStep.OlderBrother : KinStep.OlderSister)
                        : (male ? KinStep.YoungerBrother : KinStep.YoungerSister);
                    steps.RemoveAt(i);
                    return true;
                }
            }

            // 爸爸的哥哥的爸爸 → 爸爸的爸爸（伯父的爸爸 = 我爸爸的爸爸）
            if (current.IsParent() && i + 2 < steps.Count
                && steps[i + 1].IsSibling() && steps[i + 2].IsParent())
            {
                steps.RemoveAt(i + 1);
                return true;
            }

            // 老婆的儿子 / 女儿 → 我的儿子 / 女儿
            if (current.IsSpouse() && i + 1 < steps.Count && steps[i + 1].IsChild())
            {
                steps.RemoveAt(i);
                return true;
            }

            // 爸爸的老婆 → 我的妈妈、爷爷的老婆 → 我的奶奶（配偶的性别与该长辈相反时，
            // 换成同辈的另一位长辈）。按亲生父母的常规理解；继父母情形不在本工具的推导范围内，
            // 结果卡的小提示里写明了这一点。
            if (current.IsParent() && i + 1 < steps.Count && steps[i + 1].IsSpouse()
                && steps[i + 1].IsMale() != current.IsMale())
            {
                steps[i] = current == KinStep.Father ? KinStep.Mother : KinStep.Father;
                steps.RemoveAt(i + 1);
                return true;
            }

            // 儿子的哥哥 → 儿子、儿子的姐姐 → 女儿
            if (current.IsChild() && i + 1 < steps.Count && steps[i + 1].IsSibling())
            {
                var sexOfSibling = steps[i + 1].IsMale();
                steps[i + 1] = sexOfSibling ? KinStep.Son : KinStep.Daughter;
                steps.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>链是否为空（= 目标就是我本人）。</summary>
    public static bool IsSelf(IReadOnlyList<KinStep>? steps) => steps is null || steps.Count == 0;

    /// <summary>
    /// 每走一步「长一辈 / 小一辈」的净和：爸爸 +1、儿子 −1、兄弟姐妹与配偶 0。
    /// 结果 0 = 同辈，正数 = 长几辈，负数 = 小几辈。
    /// </summary>
    public static int GenerationDelta(IReadOnlyList<KinStep>? steps)
    {
        if (steps is null) return 0;
        var delta = 0;
        foreach (var step in steps) delta += step.GenerationDelta();
        return delta;
    }

    /// <summary>「长两辈 / 同辈 / 小一辈」这种给用户看的一句话。</summary>
    public static string DescribeGeneration(int delta) => delta switch
    {
        0 => "同辈",
        > 0 => delta == 1 ? "长一辈" : $"长{ChineseNumber(delta)}辈",
        _ => delta == -1 ? "小一辈" : $"小{ChineseNumber(-delta)}辈",
    };

    /// <summary>1 → 一、2 → 两、3 → 三…（只到 12，够用了）。</summary>
    public static string ChineseNumber(int value) => value switch
    {
        1 => "一",
        2 => "两",
        3 => "三",
        4 => "四",
        5 => "五",
        6 => "六",
        7 => "七",
        8 => "八",
        9 => "九",
        10 => "十",
        11 => "十一",
        12 => "十二",
        _ => value.ToString(),
    };

    /// <summary>链里有没有配偶这一步（有就是姻亲）。</summary>
    public static bool HasSpouse(IReadOnlyList<KinStep>? steps)
        => steps is not null && steps.Any(step => step.IsSpouse());

    /// <summary>这一步链属于哪一支（结果卡的一句说明）。</summary>
    public static KinBranch BranchOf(IReadOnlyList<KinStep>? steps)
    {
        if (IsSelf(steps)) return KinBranch.Self;
        if (HasSpouse(steps)) return KinBranch.Affinal;

        var list = steps!;
        if (list.All(step => step.IsParent())) return KinBranch.Direct;
        if (list.All(step => step.IsChild())) return KinBranch.OwnDescendant;
        return list[0] switch
        {
            KinStep.Father => KinBranch.Paternal,
            KinStep.Mother => KinBranch.Maternal,
            _ => KinBranch.Peer,
        };
    }

    /// <summary>支系的中文说法。</summary>
    public static string DescribeBranch(KinBranch branch) => branch switch
    {
        KinBranch.Self => "你自己",
        KinBranch.Direct => "直系长辈",
        KinBranch.OwnDescendant => "直系晚辈",
        KinBranch.Paternal => "父系",
        KinBranch.Maternal => "母系",
        KinBranch.Peer => "同辈旁系",
        KinBranch.Affinal => "姻亲",
        _ => "",
    };

    /// <summary>
    /// 这一串走下来，最后那个人是不是男性。<paramref name="selfIsMale"/> 是「我」的性别（空链要用）。
    /// </summary>
    public static bool SexOfLast(IReadOnlyList<KinStep>? steps, bool selfIsMale)
    {
        if (steps is null || steps.Count == 0) return selfIsMale;
        return steps[^1].IsMale();
    }

    /// <summary>
    /// 反向推导：把「我 → 目标」的链倒过来，得到「目标 → 我」的链（也就是「他该叫我什么」）。
    /// <paramref name="selfIsMale"/> 是链起点（我）的性别，返回的 <c>selfIsMale</c> 是倒过来以后
    /// 起点（原来的目标）的性别。
    /// </summary>
    public static (List<KinStep> Path, bool SelfIsMale) Reverse(IReadOnlyList<KinStep>? steps, bool selfIsMale)
    {
        if (steps is null || steps.Count == 0) return (new List<KinStep>(), selfIsMale);

        // sexAt[i] = 第 i 个人（我 = 0）的性别
        var sexAt = new bool[steps.Count + 1];
        sexAt[0] = selfIsMale;
        for (var i = 0; i < steps.Count; i++) sexAt[i + 1] = steps[i].IsMale();

        var reversed = new List<KinStep>(steps.Count);
        for (var i = steps.Count - 1; i >= 0; i--)
        {
            var inverse = steps[i].Inverse(sexAt[i]);
            if (inverse is null) return (new List<KinStep>(), selfIsMale);
            reversed.Add(inverse.Value);
        }
        return (reversed, sexAt[^1]);
    }

    /// <summary>链的紧凑写法（称谓表的键 / 存配置）：<c>"F+oB"</c>；空链返回空字符串。</summary>
    public static string ToToken(IReadOnlyList<KinStep>? steps)
        => steps is null || steps.Count == 0 ? "" : string.Join("+", steps.Select(step => step.Token()));

    /// <summary>紧凑写法 → 链；不认识的段直接跳过（配置被手改坏也不会崩）。</summary>
    public static List<KinStep> ParseToken(string? token)
    {
        var result = new List<KinStep>();
        if (string.IsNullOrWhiteSpace(token)) return result;

        foreach (var part in token.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = KinStepInfo.ParseToken(part.Trim());
            if (step is { } value) result.Add(value);
        }
        return result;
    }

    /// <summary>链的中文写法：「爸爸的哥哥」；空链是「我」。</summary>
    public static string Describe(IReadOnlyList<KinStep>? steps)
    {
        if (IsSelf(steps)) return "我";
        return string.Join("的", steps!.Select(step => step.Label()));
    }
}
