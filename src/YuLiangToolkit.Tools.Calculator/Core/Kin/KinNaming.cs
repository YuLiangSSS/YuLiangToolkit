namespace YuLiangToolkit.Tools.Calculator.Core.Kin;

/// <summary>一次辈分计算的结果（全部是给界面直接显示的文本，界面不再做判断）。</summary>
/// <param name="Key">规范化之后的关系链键（<see cref="KinPath.ToToken"/>）。</param>
/// <param name="IsSelf">关系链是空的（结果就是「我」）。</param>
/// <param name="IsKnown">命中了常用称谓表；false = 远亲 / 说不准，走兜底描述。</param>
/// <param name="Title">主标题：按当前南北设置选出来的叫法。</param>
/// <param name="North">北方叫法。</param>
/// <param name="South">南方叫法。</param>
/// <param name="Formal">书面称谓。</param>
/// <param name="Note">白话解释 / 地区差异。</param>
/// <param name="GenerationDelta">辈分差（正 = 长几辈）。</param>
/// <param name="GenerationText">「长一辈」「同辈」「小两辈」。</param>
/// <param name="BranchText">「父系」「母系」「姻亲」…。</param>
/// <param name="PathText">关系链的中文写法：「爸爸的哥哥」。</param>
/// <param name="ReverseTitle">对方该怎么叫我。</param>
/// <param name="ReversePathText">「他 → 我」那条链的中文写法。</param>
/// <param name="ReverseIsKnown">反向称谓是否命中了表。</param>
public sealed record KinResult(
    string Key,
    bool IsSelf,
    bool IsKnown,
    string Title,
    string North,
    string South,
    string Formal,
    string Note,
    int GenerationDelta,
    string GenerationText,
    string BranchText,
    string PathText,
    string ReverseTitle,
    string ReversePathText,
    bool ReverseIsKnown)
{
    /// <summary>界面上的副标题：书面称谓 + 「南北另一种叫法」。</summary>
    public string Subtitle
    {
        get
        {
            if (!IsKnown) return "超出常用称谓表";

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Formal) && Formal != Title) parts.Add($"书面：{Formal}");
            // 两边叫法不一样时，把「另一边叫什么」也写出来；当前标题已经等于其中一边，
            // 所以只补另一边那一个词就够，不用重复列一遍自己（南方「伯伯」时副标题 → 北方「大伯」）。
            if (!string.Equals(North, South, StringComparison.Ordinal))
            {
                var other = string.Equals(Title, North, StringComparison.Ordinal) ? South : North;
                parts.Add($"另一边：{other}");
            }
            return parts.Count == 0 ? "南北方叫法一致" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// 辈分推导与称谓取名（纯逻辑）：把「我 → 目标」的关系链变成一句人话。
///
/// <para>流程：<b>规范化</b>（<see cref="KinPath.Normalize"/>）→ 查 <see cref="KinTermTable"/> →
/// 按南北设置选叫法；查不到就给兜底描述（远亲 / 长幼说不准），绝不瞎编一个称呼。
/// 反向（对方叫我什么）用 <see cref="KinPath.Reverse"/> 把链倒过来再走同一套流程。</para>
/// </summary>
public static class KinNaming
{
    /// <summary>查不到称谓时的主标题。</summary>
    public const string UnknownTitle = "远房亲戚";

    /// <summary>
    /// 查不到称谓时按辈分差给的通用说法：宁可说「长一辈的亲戚」也不硬编一个称呼。
    /// </summary>
    public static string FallbackTitle(int generationDelta) => generationDelta switch
    {
        >= 2 => "更上一辈的亲戚",
        1 => "长一辈的亲戚",
        0 => "同辈的亲戚",
        -1 => "小一辈的亲戚",
        _ => "更小一辈的亲戚",
    };

    /// <summary>算一条关系链。<paramref name="preferNorth"/> 决定主标题用北方还是南方叫法。</summary>
    public static KinResult Resolve(IReadOnlyList<KinStep>? steps, bool selfIsMale, bool preferNorth)
    {
        var original = KinPath.Describe(steps);
        var normalized = KinPath.Normalize(steps);
        var key = KinPath.ToToken(normalized);
        var term = KinTermTable.Find(key);

        var delta = KinPath.GenerationDelta(steps);
        var generationText = KinPath.DescribeGeneration(delta);
        var branchText = KinPath.DescribeBranch(KinPath.BranchOf(steps));

        // 反向：对方叫我什么
        var (reversePath, _) = KinPath.Reverse(steps, selfIsMale);
        var reverseNormalized = KinPath.Normalize(reversePath);
        var reverseTerm = KinTermTable.Find(KinPath.ToToken(reverseNormalized));
        var reverseKnown = reverseTerm is not null;
        var reverseTitle = reverseTerm is null
            ? FallbackTitle(KinPath.GenerationDelta(reverseNormalized))
            : Pick(GetEffective(reverseTerm), preferNorth);

        if (term is null)
        {
            // 兜底：不当「查无此人」，而是把关系链原样讲清楚，并说明为什么没有固定叫法
            var why = normalized.Count >= 4
                ? "这条关系绕得比较远，日常已经不用专门的称呼了 —— 按下面的关系链逐级理解就行。"
                : "这条关系里带了「哥哥还是弟弟」这类看不出实际长幼的地方，说法不固定 —— 按关系链逐级理解，或者跟着对方家那边怎么叫。";

            return new KinResult(
                key,
                KinPath.IsSelf(steps),
                false,
                UnknownTitle,
                FallbackTitle(delta),
                FallbackTitle(delta),
                "—",
                why,
                delta,
                generationText,
                branchText,
                original,
                reverseTitle,
                KinPath.Describe(reversePath),
                reverseKnown);
        }

        var effective = GetEffective(term);
        return new KinResult(
            key,
            KinPath.IsSelf(steps),
            true,
            Pick(effective, preferNorth),
            effective.North,
            effective.South,
            effective.Formal,
            effective.Note,
            delta,
            generationText,
            branchText,
            original,
            reverseTitle,
            KinPath.Describe(reversePath),
            reverseKnown);
    }

    /// <summary>
    /// 一边没写（空字符串）时用另一边补上，保证南北两栏都有字可显示。
    /// </summary>
    private static KinTerm GetEffective(KinTerm term) => term switch
    {
        { North: "", South: "" } => term with { North = term.Formal, South = term.Formal },
        { North: "" } => term with { North = term.South },
        { South: "" } => term with { South = term.North },
        _ => term,
    };

    private static string Pick(KinTerm term, bool preferNorth) => preferNorth ? term.North : term.South;
}
