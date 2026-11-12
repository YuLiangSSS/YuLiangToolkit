using YuLiangToolkit.Tools.Calculator;
using YuLiangToolkit.Tools.Calculator.Core.Kin;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「计算器 → 辈分计算模式」自检：关系链规范化、称谓表一致性、南北叫法、长幼区分、
/// 反向称谓（含双向对称性）、性别影响、远亲兜底、对照表可用性、配置项。
///
/// <para>纯逻辑：不创建窗口、不碰剪贴板、不写真实 Config（配置只在内存里做 JSON 往返）。</para>
/// <para>由 <see cref="CalculatorChecks.Run"/> 调用，共用 <c>calculator</c> 组。</para>
/// </summary>
internal static class KinCalculatorChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>一条关系的紧凑写法 → 关系链（断言里写起来短）。</summary>
    private static KinStep[] P(string token) => KinPath.ParseToken(token).ToArray();

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckSteps(fn);
        CheckNormalize(fn);
        CheckTermTable(fn);
        CheckRegionTerms(fn);
        CheckGeneration(fn);
        CheckReverse(fn);
        CheckGender(fn);
        CheckFallback(fn);
        CheckPathText(fn);
        CheckConfig(fn);

        report("[计算器] 辈分计算模式：关系链规范化 / 南北称谓 / 长幼 / 反向称谓 / 兜底 / 配置", true);
    }

    // ---------------------------------------------------------------- 一步关系

    private static void CheckSteps(CheckFn check)
    {
        check("[辈分] 十个关系按钮齐全且顺序是 爸爸/妈妈/哥哥/弟弟/姐姐/妹妹/老公/老婆/儿子/女儿",
            KinStepInfo.All.Select(step => step.Label()).SequenceEqual(new[]
            {
                "爸爸", "妈妈", "哥哥", "弟弟", "姐姐", "妹妹", "老公", "老婆", "儿子", "女儿",
            }),
            string.Join(" / ", KinStepInfo.All.Select(step => step.Label())));

        check("[辈分] 辈分方向：爸爸 +1、儿子 -1、哥哥 / 老公 0",
            KinStep.Father.GenerationDelta() == 1 && KinStep.Mother.GenerationDelta() == 1
            && KinStep.Son.GenerationDelta() == -1 && KinStep.Daughter.GenerationDelta() == -1
            && KinStep.OlderBrother.GenerationDelta() == 0 && KinStep.Husband.GenerationDelta() == 0,
            $"{KinStep.Father.GenerationDelta()} / {KinStep.Son.GenerationDelta()} / {KinStep.OlderBrother.GenerationDelta()}");

        check("[辈分] 性别：爸爸 / 哥哥 / 老公 / 儿子 是男性，妈妈 / 姐姐 / 老婆 / 女儿 是女性",
            KinStep.Father.IsMale() && KinStep.OlderBrother.IsMale() && KinStep.Husband.IsMale() && KinStep.Son.IsMale()
            && !KinStep.Mother.IsMale() && !KinStep.OlderSister.IsMale() && !KinStep.Wife.IsMale() && !KinStep.Daughter.IsMale());

        check("[辈分] 紧凑写法往返：十个关系都能 token → 枚举 → token",
            KinStepInfo.All.All(step => KinStepInfo.ParseToken(step.Token()) == step),
            string.Join(" / ", KinStepInfo.All.Select(step => step.Token())));

        check("[辈分] 紧凑写法：不认识的 token 返回 null（配置被手改坏也不会崩）",
            KinStepInfo.ParseToken("ZZ") is null && KinStepInfo.ParseToken(null) is null
            && KinStepInfo.ParseToken("F") == KinStep.Father);
    }

    // ---------------------------------------------------------------- 规范化

    private static void CheckNormalize(CheckFn check)
    {
        check("[辈分] 规范化：哥哥的爸爸 → 爸爸",
            KinPath.ToToken(KinPath.Normalize(P("oB+F"))) == "F",
            KinPath.ToToken(KinPath.Normalize(P("oB+F"))));

        check("[辈分] 规范化：妹妹的妈妈 → 妈妈",
            KinPath.ToToken(KinPath.Normalize(P("yS+M"))) == "M",
            KinPath.ToToken(KinPath.Normalize(P("yS+M"))));

        check("[辈分] 规范化：爸爸的哥哥的爸爸 → 爸爸的爸爸（爷爷）",
            KinPath.ToToken(KinPath.Normalize(P("F+oB+F"))) == "F+F",
            KinPath.ToToken(KinPath.Normalize(P("F+oB+F"))));

        check("[辈分] 规范化：儿子的哥哥 → 儿子、儿子的姐姐 → 女儿",
            KinPath.ToToken(KinPath.Normalize(P("S+oB"))) == "S"
            && KinPath.ToToken(KinPath.Normalize(P("S+oS"))) == "D",
            $"{KinPath.ToToken(KinPath.Normalize(P("S+oB")))} / {KinPath.ToToken(KinPath.Normalize(P("S+oS")))}");

        check("[辈分] 规范化：老婆的儿子 → 儿子、老公的女儿 → 女儿",
            KinPath.ToToken(KinPath.Normalize(P("W+S"))) == "S"
            && KinPath.ToToken(KinPath.Normalize(P("H+D"))) == "D",
            $"{KinPath.ToToken(KinPath.Normalize(P("W+S")))} / {KinPath.ToToken(KinPath.Normalize(P("H+D")))}");

        check("[辈分] 规范化：老公的老婆 → 回到我（空链）",
            KinPath.Normalize(P("H+W")).Count == 0,
            KinPath.ToToken(KinPath.Normalize(P("H+W"))));

        check("[辈分] 规范化：爸爸的老婆 → 妈妈、爷爷的老婆 → 奶奶",
            KinPath.ToToken(KinPath.Normalize(P("F+W"))) == "M"
            && KinPath.ToToken(KinPath.Normalize(P("F+F+W"))) == "F+M",
            $"{KinPath.ToToken(KinPath.Normalize(P("F+W")))} / {KinPath.ToToken(KinPath.Normalize(P("F+F+W")))}");

        check("[辈分] 规范化：长幼同向时才合并（哥哥的姐姐 → 姐姐；哥哥的弟弟说不准，保持原样）",
            KinPath.ToToken(KinPath.Normalize(P("oB+oS"))) == "oS"
            && KinPath.ToToken(KinPath.Normalize(P("oB+yB"))) == "oB+yB",
            $"{KinPath.ToToken(KinPath.Normalize(P("oB+oS")))} / {KinPath.ToToken(KinPath.Normalize(P("oB+yB")))}");

        check("[辈分] 规范化：稳定（再规范化一次结果不变）",
            new[] { "F+oB", "M+M+F", "oB+F+M", "W+oB", "S+D" }
                .All(token =>
                {
                    var once = KinPath.Normalize(P(token));
                    var twice = KinPath.Normalize(once);
                    return KinPath.ToToken(once) == KinPath.ToToken(twice);
                }),
            "5 条链各规范化两次");

        check("[辈分] 规范化：不改入参（原链保持不变）",
            RunPreservesInput());

        check("[辈分] 规范化：不认识的段被丢弃、空 / null 链安全",
            KinPath.ParseToken("F+ZZ+oB").Count == 2
            && KinPath.ParseToken("").Count == 0 && KinPath.ParseToken(null).Count == 0
            && KinPath.Normalize(null).Count == 0,
            $"F+ZZ+oB → {KinPath.ToToken(KinPath.ParseToken("F+ZZ+oB"))}");
    }

    private static bool RunPreservesInput()
    {
        var original = P("oB+F");
        _ = KinPath.Normalize(original);
        return original.Length == 2 && original[0] == KinStep.OlderBrother && original[1] == KinStep.Father;
    }

    // ---------------------------------------------------------------- 称谓表

    private static void CheckTermTable(CheckFn check)
    {
        var all = KinTermTable.All;
        check("[辈分] 称谓表规模够用（≥ 100 条，覆盖直系 / 旁系 / 堂表 / 侄甥 / 姻亲 / 远一层）",
            all.Count >= 100, all.Count.ToString());

        check("[辈分] 称谓表的键没有重复",
            all.Select(term => term.Key).Distinct().Count() == all.Count,
            $"{all.Count} 条 / 去重后 {all.Select(term => term.Key).Distinct().Count()} 条");

        check("[辈分] 称谓表每一行都能被解析成关系链，且链本身已经是规范化形态（表与算法不打架）",
            CheckTableTokens());

        check("[辈分] 称谓表每一行的南北叫法 / 书面称谓都非空（空字符串会被 GetEffective 补上，但表里不该留空）",
            all.All(term => term.North.Length > 0 && term.South.Length > 0 && term.Formal.Length > 0),
            string.Join(" / ", all.Where(t => t.North.Length == 0 || t.South.Length == 0 || t.Formal.Length == 0)
                .Select(t => t.Key)));

        check("[辈分] 查表：空键 / null / 不认识返回对应结果",
            KinTermTable.Find("")?.Formal == "本人"
            && KinTermTable.Find(null) is null
            && KinTermTable.Find("ZZ") is null,
            KinTermTable.Find("")?.Formal ?? "(null)");

        check("[辈分] 表里那几门最常见的亲戚都在：爷爷 / 奶奶 / 姥爷 / 姥姥 / 大伯 / 叔叔 / 姑姑 / 舅舅 / 姨",
            new[] { "F+F", "F+M", "M+F", "M+M", "F+oB", "F+yB", "F+oS", "M+oB", "M+oS" }
                .All(key => KinTermTable.Find(key) is not null),
            "9 个最基本称谓");
    }

    /// <summary>表里每一行：键能解析、链已规范化、且用推导算出来的结果与这一行一致。</summary>
    private static bool CheckTableTokens()
    {
        foreach (var term in KinTermTable.All)
        {
            var path = KinPath.ParseToken(term.Key);
            if (KinPath.ToToken(path) != term.Key) return false;

            var normalized = KinPath.Normalize(path);
            if (KinPath.ToToken(normalized) != term.Key) return false;

            var result = KinNaming.Resolve(path, selfIsMale: true, preferNorth: true);
            if (!result.IsKnown) return false;
            if (result.Title != term.North) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 南北叫法

    private static void CheckRegionTerms(CheckFn check)
    {
        var grandpaM = KinPath.ParseToken("M+F");
        var north = KinNaming.Resolve(grandpaM, selfIsMale: true, preferNorth: true);
        var south = KinNaming.Resolve(grandpaM, selfIsMale: true, preferNorth: false);

        check("[辈分] 南北叫法：妈妈的爸爸 → 北方「姥爷」/ 南方「外公」",
            north.Title == "姥爷" && south.Title == "外公"
            && north.Formal == "外祖父",
            $"{north.Title} / {south.Title} / 书面 {north.Formal}");

        check("[辈分] 南北叫法：妈妈的妈妈 → 北方「姥姥」/ 南方「外婆」",
            KinNaming.Resolve(P("M+M"), true, true).Title == "姥姥"
            && KinNaming.Resolve(P("M+M"), true, false).Title == "外婆",
            $"{KinNaming.Resolve(P("M+M"), true, true).Title} / {KinNaming.Resolve(P("M+M"), true, false).Title}");

        check("[辈分] 南北叫法：爸爸的哥哥 → 北方「大伯」/ 南方「伯伯」",
            KinNaming.Resolve(P("F+oB"), true, true).Title == "大伯"
            && KinNaming.Resolve(P("F+oB"), true, false).Title == "伯伯",
            $"{KinNaming.Resolve(P("F+oB"), true, true).Title} / {KinNaming.Resolve(P("F+oB"), true, false).Title}");

        check("[辈分] 南北叫法：大伯的老婆 → 北方「大娘」/ 南方「伯母」、叔叔的老婆 → 「婶子」/「婶婶」",
            KinNaming.Resolve(P("F+oB+W"), true, true).Title == "大娘"
            && KinNaming.Resolve(P("F+oB+W"), true, false).Title == "伯母"
            && KinNaming.Resolve(P("F+yB+W"), true, true).Title == "婶子"
            && KinNaming.Resolve(P("F+yB+W"), true, false).Title == "婶婶",
            $"{KinNaming.Resolve(P("F+oB+W"), true, true).Title} / {KinNaming.Resolve(P("F+yB+W"), true, false).Title}");

        check("[辈分] 南北叫法：弟弟的老婆 → 北方「弟妹」/ 南方「弟媳」",
            KinNaming.Resolve(P("yB+W"), true, true).Title == "弟妹"
            && KinNaming.Resolve(P("yB+W"), true, false).Title == "弟媳",
            $"{KinNaming.Resolve(P("yB+W"), true, true).Title} / {KinNaming.Resolve(P("yB+W"), true, false).Title}");

        check("[辈分] 南北一致的亲戚：姑姑 / 舅舅 / 叔叔 两边叫法相同，副标题只写书面称谓",
            IsSameBothWays("F+oS") && IsSameBothWays("M+oB") && IsSameBothWays("F+yB")
            && KinNaming.Resolve(P("F+oS"), true, true).Subtitle == "书面：姑母",
            KinNaming.Resolve(P("F+oS"), true, true).Subtitle);

        check("[辈分] 南北不一致时，副标题把另一边的叫法也写出来（姥爷 ↔ 外公、大伯 ↔ 伯伯）",
            KinNaming.Resolve(P("M+F"), true, true).Subtitle.Contains("外公", StringComparison.Ordinal)
            && KinNaming.Resolve(P("M+F"), true, false).Subtitle.Contains("姥爷", StringComparison.Ordinal)
            && KinNaming.Resolve(P("F+oB"), true, true).Subtitle.Contains("伯伯", StringComparison.Ordinal),
            $"{KinNaming.Resolve(P("M+F"), true, true).Subtitle} / {KinNaming.Resolve(P("M+F"), true, false).Subtitle}");

        check("[辈分] 副标题：两边叫法一样时只写书面称谓，不重复同一句话（姑姑 → 书面：姑母）",
            KinNaming.Resolve(P("F+oS"), true, true).Subtitle == "书面：姑母"
            && !KinNaming.Resolve(P("F+oS"), true, true).Subtitle.Contains("另一边", StringComparison.Ordinal),
            KinNaming.Resolve(P("F+oS"), true, true).Subtitle);
    }

    private static bool IsSameBothWays(string token)
    {
        var path = KinPath.ParseToken(token);
        return KinNaming.Resolve(path, true, true).Title == KinNaming.Resolve(path, true, false).Title;
    }

    // ---------------------------------------------------------------- 辈分 / 支系

    private static void CheckGeneration(CheckFn check)
    {
        check("[辈分] 辈分差：爸爸的哥哥 长一辈、哥哥 同辈、侄子 小一辈、孙子 小两辈",
            KinPath.GenerationDelta(P("F+oB")) == 1
            && KinPath.GenerationDelta(P("oB")) == 0
            && KinPath.GenerationDelta(P("oB+S")) == -1
            && KinPath.GenerationDelta(P("S+S")) == -2,
            $"{KinPath.GenerationDelta(P("F+oB"))} / {KinPath.GenerationDelta(P("oB"))} / {KinPath.GenerationDelta(P("oB+S"))} / {KinPath.GenerationDelta(P("S+S"))}");

        check("[辈分] 辈分文案：同辈 / 长一辈 / 长两辈 / 小一辈 / 小两辈",
            KinPath.DescribeGeneration(0) == "同辈"
            && KinPath.DescribeGeneration(1) == "长一辈"
            && KinPath.DescribeGeneration(2) == "长两辈"
            && KinPath.DescribeGeneration(-1) == "小一辈"
            && KinPath.DescribeGeneration(-2) == "小两辈",
            string.Join(" / ", new[] { 0, 1, 2, -1, -2 }.Select(KinPath.DescribeGeneration)));

        check("[辈分] 支系：爸爸的爸爸 直系长辈、爸爸的哥哥 父系、妈妈的哥哥 母系、老婆的爸爸 姻亲、儿子 直系晚辈、空链 自己",
            KinPath.BranchOf(P("F+F")) == KinBranch.Direct
            && KinPath.BranchOf(P("F+oB")) == KinBranch.Paternal
            && KinPath.BranchOf(P("M+oB")) == KinBranch.Maternal
            && KinPath.BranchOf(P("W+F")) == KinBranch.Affinal
            && KinPath.BranchOf(P("S")) == KinBranch.OwnDescendant
            && KinPath.BranchOf(Array.Empty<KinStep>()) == KinBranch.Self,
            $"{KinPath.BranchOf(P("F+F"))} / {KinPath.BranchOf(P("F+oB"))} / {KinPath.BranchOf(P("M+oB"))} / {KinPath.BranchOf(P("W+F"))}");

        check("[辈分] 支系文案：父系 / 母系 / 姻亲 都是中文",
            KinPath.DescribeBranch(KinBranch.Paternal) == "父系"
            && KinPath.DescribeBranch(KinBranch.Maternal) == "母系"
            && KinPath.DescribeBranch(KinBranch.Affinal) == "姻亲",
            KinPath.DescribeBranch(KinBranch.Affinal));

        check("[辈分] 空链 = 我：标题「我」、辈分同辈、结果标记为本人",
            KinNaming.Resolve(null, true, true) is { IsSelf: true, IsKnown: true, Title: "我", GenerationText: "同辈" },
            KinNaming.Resolve(null, true, true).Title);
    }

    // ---------------------------------------------------------------- 反向称谓

    private static void CheckReverse(CheckFn check)
    {
        check("[辈分] 反向：爸爸的哥哥 → 对方叫我「侄子」（我是男的）",
            KinNaming.Resolve(P("F+oB"), selfIsMale: true, preferNorth: true).ReverseTitle == "侄子",
            KinNaming.Resolve(P("F+oB"), true, true).ReverseTitle);

        check("[辈分] 反向：爸爸的哥哥 → 对方叫我「侄女」（我是女的）",
            KinNaming.Resolve(P("F+oB"), selfIsMale: false, preferNorth: true).ReverseTitle == "侄女",
            KinNaming.Resolve(P("F+oB"), false, true).ReverseTitle);

        check("[辈分] 反向：妈妈的姐姐 → 对方叫我「外甥 / 外甥女」",
            KinNaming.Resolve(P("M+oS"), true, true).ReverseTitle == "外甥"
            && KinNaming.Resolve(P("M+oS"), false, true).ReverseTitle == "外甥女",
            $"{KinNaming.Resolve(P("M+oS"), true, true).ReverseTitle} / {KinNaming.Resolve(P("M+oS"), false, true).ReverseTitle}");

        check("[辈分] 反向：爷爷 → 对方叫我「孙子 / 孙女」",
            KinNaming.Resolve(P("F+F"), true, true).ReverseTitle == "孙子"
            && KinNaming.Resolve(P("F+F"), false, true).ReverseTitle == "孙女",
            $"{KinNaming.Resolve(P("F+F"), true, true).ReverseTitle} / {KinNaming.Resolve(P("F+F"), false, true).ReverseTitle}");

        check("[辈分] 反向：儿子 → 对方叫我「爸 / 妈」",
            KinNaming.Resolve(P("S"), true, true).ReverseTitle == "爸"
            && KinNaming.Resolve(P("S"), false, true).ReverseTitle == "妈",
            $"{KinNaming.Resolve(P("S"), true, true).ReverseTitle} / {KinNaming.Resolve(P("S"), false, true).ReverseTitle}");

        check("[辈分] 反向：老婆 → 对方是女的，叫我「老公」；老公 → 对方是男的，叫我「老婆」",
            KinNaming.Resolve(P("W"), true, true).ReverseTitle == "老公"
            && KinNaming.Resolve(P("H"), true, true).ReverseTitle == "老婆",
            $"{KinNaming.Resolve(P("W"), true, true).ReverseTitle} / {KinNaming.Resolve(P("H"), true, true).ReverseTitle}");

        check("[辈分] 反向：反两次回到原链（爸爸的哥哥 → 侄子 → 反推回来还是爸爸的哥哥）",
            ReverseRoundTrip("F+oB", selfIsMale: true)
            && ReverseRoundTrip("M+oS", selfIsMale: false)
            && ReverseRoundTrip("F+F+F", selfIsMale: true)
            && ReverseRoundTrip("oB+S", selfIsMale: false),
            "4 条链各反两次");

        check("[辈分] 反向：辈分差相反（爸爸的哥哥长一辈 → 反过来我小一辈）",
            ReverseGeneration("F+oB") == -1 && ReverseGeneration("S+S") == 2 && ReverseGeneration("oB") == 0,
            $"{ReverseGeneration("F+oB")} / {ReverseGeneration("S+S")} / {ReverseGeneration("oB")}");
    }

    /// <summary>反向再反向必须回到原链（规范化之后比较：规范化会抹掉等价写法）。</summary>
    private static bool ReverseRoundTrip(string token, bool selfIsMale)
    {
        var path = P(token);
        var (reversed, reversedSelfIsMale) = KinPath.Reverse(path, selfIsMale);
        var (back, _) = KinPath.Reverse(reversed, reversedSelfIsMale);
        return KinPath.ToToken(KinPath.Normalize(back)) == KinPath.ToToken(KinPath.Normalize(path));
    }

    private static int ReverseGeneration(string token)
    {
        var (reversed, _) = KinPath.Reverse(P(token), true);
        return KinPath.GenerationDelta(reversed);
    }

    // ---------------------------------------------------------------- 性别

    private static void CheckGender(CheckFn check)
    {
        check("[辈分] 性别只影响反向：正向「爸爸的哥哥」男女都是「大伯」",
            KinNaming.Resolve(P("F+oB"), true, true).Title == KinNaming.Resolve(P("F+oB"), false, true).Title,
            KinNaming.Resolve(P("F+oB"), true, true).Title);

        check("[辈分] 性别影响反向：弟弟 → 我男「哥哥」、我女「姐姐」",
            KinNaming.Resolve(P("yB"), true, true).ReverseTitle == "哥哥"
            && KinNaming.Resolve(P("yB"), false, true).ReverseTitle == "姐姐",
            $"{KinNaming.Resolve(P("yB"), true, true).ReverseTitle} / {KinNaming.Resolve(P("yB"), false, true).ReverseTitle}");

        check("[辈分] 性别影响反向：妹妹 → 我男「哥哥」、我女「姐姐」",
            KinNaming.Resolve(P("yS"), true, true).ReverseTitle == "哥哥"
            && KinNaming.Resolve(P("yS"), false, true).ReverseTitle == "姐姐",
            $"{KinNaming.Resolve(P("yS"), true, true).ReverseTitle} / {KinNaming.Resolve(P("yS"), false, true).ReverseTitle}");

        check("[辈分] 性别影响反向：孙子 → 我男「爷爷」、我女「奶奶」",
            KinNaming.Resolve(P("S+S"), true, true).ReverseTitle == "爷爷"
            && KinNaming.Resolve(P("S+S"), false, true).ReverseTitle == "奶奶",
            $"{KinNaming.Resolve(P("S+S"), true, true).ReverseTitle} / {KinNaming.Resolve(P("S+S"), false, true).ReverseTitle}");
    }

    // ---------------------------------------------------------------- 兜底

    private static void CheckFallback(CheckFn check)
    {
        check("[辈分] 兜底：没收录的远亲不瞎编称呼，给「远房亲戚」+ 关系链描述",
            FallbackKeepsPath("F+F+F+oB+S"), FallbackDetail("F+F+F+oB+S"));

        check("[辈分] 兜底：长幼看不出的链（哥哥的弟弟）也走兜底，不硬猜",
            KinNaming.Resolve(P("oB+yB"), true, true).IsKnown == false, "哥哥的弟弟");

        check("[辈分] 兜底行的 IsKnown=false，界面据此把副标题写成「超出常用称谓表」",
            KinNaming.Resolve(P("oB+yB"), true, true).Subtitle.Contains("超出常用称谓表", StringComparison.Ordinal),
            KinNaming.Resolve(P("oB+yB"), true, true).Subtitle);

        check("[辈分] 兜底：南北两栏按辈分给通用说法（同辈的亲戚 / 更上一辈的亲戚），不硬编叫法",
            KinNaming.Resolve(P("oB+yB"), true, true).North == "同辈的亲戚"
            && KinNaming.Resolve(P("F+F+F+oB+S"), true, true).North == "更上一辈的亲戚",
            $"{KinNaming.Resolve(P("oB+yB"), true, true).North} / {KinNaming.Resolve(P("F+F+F+oB+S"), true, true).North} / delta={KinPath.GenerationDelta(P("F+F+oB+S"))}");

        check("[辈分] 兜底时反向称谓也按辈分给通用说法（不出现「远房亲戚」这种没信息量的字）",
            KinNaming.Resolve(P("F+F+F+oB+S"), true, true).ReverseTitle == "更小一辈的亲戚"
            && !KinNaming.Resolve(P("F+F+F+oB+S"), true, true).ReverseTitle.Contains("远房", StringComparison.Ordinal),
            KinNaming.Resolve(P("F+F+F+oB+S"), true, true).ReverseTitle + " / " +
            KinNaming.Resolve(P("F+F+F+oB+S"), true, true).ReversePathText);

        check("[辈分] 兜底不会让界面空着：标题 / 说明 / 反向称谓都有字",
            NonEmpty("F+F+F+oB+S") && NonEmpty("oB+yB"), "2 条兜底链");

        check("[辈分] 常见的远一层亲戚不该掉进兜底：爸爸的堂兄弟（堂伯 / 堂叔）、表舅、表姨有名字",
            KinNaming.Resolve(P("F+F+oB+S"), true, true).Title == "堂伯 / 堂叔"
            && KinNaming.Resolve(P("M+M+yB+S"), true, true).Title == "表舅"
            && KinNaming.Resolve(P("M+M+oS+S"), true, true).Title == "表姨",
            $"{KinNaming.Resolve(P("F+F+oB+S"), true, true).Title} / {KinNaming.Resolve(P("M+M+yB+S"), true, true).Title}");
    }

    private static bool FallbackKeepsPath(string token)
    {
        var result = KinNaming.Resolve(P(token), true, true);
        return !result.IsKnown && result.Title == KinNaming.UnknownTitle
            && result.PathText == KinPath.Describe(P(token));
    }

    private static string FallbackDetail(string token)
    {
        var result = KinNaming.Resolve(P(token), true, true);
        return $"{result.Title} / {result.PathText}";
    }

    private static bool NonEmpty(string token)
    {
        var result = KinNaming.Resolve(P(token), true, true);
        return result.Title.Length > 0 && result.Note.Length > 0 && result.ReverseTitle.Length > 0;
    }

    // ---------------------------------------------------------------- 关系链文案

    private static void CheckPathText(CheckFn check)
    {
        check("[辈分] 关系链文案：空链写「我」、爸爸的哥哥写「爸爸的哥哥」",
            KinPath.Describe(null) == "我"
            && KinPath.Describe(P("F+oB")) == "爸爸的哥哥"
            && KinPath.Describe(P("M+M+F")) == "妈妈的妈妈的爸爸",
            KinPath.Describe(P("M+M+F")));

        check("[辈分] 关系链文案：反向那条链也从对方出发写清楚（大伯 → 我 = 弟弟的儿子 = 侄子）",
            KinNaming.Resolve(P("F+oB"), true, true).ReversePathText == "弟弟的儿子",
            KinNaming.Resolve(P("F+oB"), true, true).ReversePathText);

        check("[辈分] 副标题：书面称谓与叫法完全相同、南北也一致时写「南北方叫法一致」",
            KinNaming.Resolve(P("S"), true, true).Subtitle == "南北方叫法一致",
            KinNaming.Resolve(P("S"), true, true).Subtitle);

        check("[辈分] 结果对象的 PathText 保留用户点出来的原链（不被规范化改写）",
            KinNaming.Resolve(P("oB+F"), true, true).PathText == "哥哥的爸爸"
            && KinNaming.Resolve(P("oB+F"), true, true).Key == "F",
            KinNaming.Resolve(P("oB+F"), true, true).PathText);
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new CalculatorConfig();
        check("[辈分] 配置默认：标准计算模式 / 北方 / 男 / 空关系链",
            config.Mode == "标准计算" && config.KinRegion == "北方"
            && config.KinGender == "男" && config.KinPath == ""
            && !config.IsKinMode && config.PreferNorth && config.SelfIsMale,
            config.ToJson());

        check("[辈分] 配置选项：模式 / 南北 / 性别各两个选项，顺序固定",
            CalculatorConfig.ModeOptions.SequenceEqual(new[] { "标准计算", "辈分计算" })
            && CalculatorConfig.RegionOptions.SequenceEqual(new[] { "北方", "南方" })
            && CalculatorConfig.GenderOptions.SequenceEqual(new[] { "男", "女" }),
            string.Join(" / ", CalculatorConfig.ModeOptions));

        check("[辈分] 配置规范化：不认识的值安全回退（模式→标准、南北→北方、性别→男）",
            CalculatorConfig.NormalizeMode("乱写") == "标准计算"
            && CalculatorConfig.NormalizeMode(null) == "标准计算"
            && CalculatorConfig.NormalizeMode("辈分计算") == "辈分计算"
            && CalculatorConfig.NormalizeRegion("乱写") == "北方"
            && CalculatorConfig.NormalizeRegion("南方地区") == "南方"
            && CalculatorConfig.NormalizeGender(null) == "男"
            && CalculatorConfig.NormalizeGender("女") == "女",
            $"{CalculatorConfig.NormalizeMode("乱写")} / {CalculatorConfig.NormalizeRegion("南方地区")} / {CalculatorConfig.NormalizeGender(null)}");

        var roundTrip = new CalculatorConfig
        {
            Mode = "辈分计算",
            KinRegion = "南方",
            KinGender = "女",
            KinPath = "F+oB",
        };
        var json = YuLiangToolkit.Core.Storage.JsonFileStore.Serialize(roundTrip);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<CalculatorConfig>(
            json, YuLiangToolkit.Core.Storage.JsonFileStore.Options);

        check("[辈分] 配置 JSON 往返：模式 / 南北 / 性别 / 关系链都不丢",
            parsed is { IsKinMode: true, PreferNorth: false, SelfIsMale: false, KinPath: "F+oB" },
            json);

        check("[辈分] 配置 JSON 不写只读计算属性（isKinMode / preferNorth / selfIsMale）",
            !json.Contains("isKinMode", StringComparison.Ordinal)
            && !json.Contains("preferNorth", StringComparison.Ordinal)
            && !json.Contains("selfIsMale", StringComparison.Ordinal),
            json.Length > 240 ? json[..240] + "…" : json);

        check("[辈分] 关系链存进配置再读回来能还原（端到端一条链）",
            KinPath.ToToken(KinPath.ParseToken(new CalculatorConfig { KinPath = "M+oS+S" }.KinPath)) == "M+oS+S",
            new CalculatorConfig { KinPath = "M+oS+S" }.KinPath);
    }
}
