using System.Numerics;
using System.Text.Json;
using YuLiangToolkit.Core.Numbers;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.RandomNumber;
using YuLiangToolkit.Tools.RandomNumber.Core;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「随机数生成器」自检：十进制步长网格（任意大边界 / 任意细步长 / 上限对齐 / 非法输入）、
/// 无偏抽样（注入确定性随机源，断言边界、去重、条数夹取）、排序与分隔符拼接、
/// 精确数轮子 <see cref="ExactDecimal"/>（解析 / 四则 / 除尽判定 / 格式化），以及配置默认值与夹取。
///
/// <para><b>全程纯逻辑</b>：不创建窗口、不碰剪贴板、不写真实 Config（配置往返只在内存里做 JSON 序列化）。</para>
/// </summary>
internal static class RandomNumberChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckExactDecimal(fn);
        CheckGrid(fn);
        CheckSampling(fn);
        CheckOutput(fn);
        CheckConfig(fn);

        report("[随机数生成器] 步长网格 / 上限对齐 / 无偏抽样 / 去重排序 / 精确数轮子 / 配置", true);
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>确定性「随机」源：按序吐出给定刻度编号，用来断言边界与去重逻辑。</summary>
    private static Func<BigInteger, BigInteger> Sequence(params BigInteger[] values)
    {
        var index = 0;
        return _ =>
        {
            var value = values[index % values.Length];
            index++;
            return value;
        };
    }

    /// <summary>总是返回 0 的随机源（取到网格第一个点 = 下限）。</summary>
    private static readonly Func<BigInteger, BigInteger> FirstPoint = _ => BigInteger.Zero;

    private static string Describe(ExactRandom.GenerateResult result)
        => $"Success={result.Success} Text=\"{result.Text}\" Error=\"{result.Error}\"";

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new RandomNumberTool();
        check("[随机数] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "random-number" && tool.Name == "随机数生成器" && tool.EmojiIcon == "🎲"
            && tool.Category == "数学" && tool.Order == 515,
            $"{tool.Id} / {tool.Name} / {tool.EmojiIcon} / {tool.Category} / {tool.Order}");

        check("[随机数] 工具 Id 与 RandomNumberConfig.ToolId 一致（Data / Config 目录名）",
            tool.Id == RandomNumberConfig.ToolId, RandomNumberConfig.ToolId);

        check("[随机数] 描述点明下限 / 上限 / 精确度与「不丢位」",
            tool.Description.Contains("下限") && tool.Description.Contains("上限")
            && tool.Description.Contains("精确度") && tool.Description.Contains("丢位"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 精确数轮子

    private static void CheckExactDecimal(CheckFn check)
    {
        var textA = ExactDecimal.TryParse("123", out var a) ? a.ToPlainString() : "";
        var textB = ExactDecimal.TryParse("0.30", out var b) ? b.ToPlainString() : "";
        var textC = ExactDecimal.TryParse("1e-30", out var c) ? c.ToPlainString() : "";
        var textD = ExactDecimal.TryParse("-2.5E+2", out var d) ? d.ToPlainString() : "";
        check("[随机数] 精确数：解析整数 / 小数 / 科学计数法，输出不带浮点尾巴",
            textA == "123" && textB == "0.3"
            && textC == "0." + new string('0', 29) + "1"
            && textD == "-250",
            $"{textA} / {textB} / {textC} / {textD}");

        check("[随机数] 精确数：非法文本（空 / 字母 / 两个小数点 / 指数残缺）都返回 false，不抛异常",
            !ExactDecimal.TryParse("", out _) && !ExactDecimal.TryParse("abc", out _)
            && !ExactDecimal.TryParse("1.2.3", out _) && !ExactDecimal.TryParse("1e", out _)
            && !ExactDecimal.TryParse(null, out _),
            "空 / abc / 1.2.3 / 1e / null");

        ExactDecimal.TryParse("0.1", out var p1);
        ExactDecimal.TryParse("0.2", out var p2);
        check("[随机数] 精确数：0.1 + 0.2 恰好等于 0.3（double 会得到 0.30000000000000004）",
            (p1 + p2).ToPlainString() == "0.3", (p1 + p2).ToPlainString());

        ExactDecimal.TryParse("6", out var six);
        ExactDecimal.TryParse("4", out var four);
        ExactDecimal.TryParse("1", out var one);
        ExactDecimal.TryParse("3", out var three);
        check("[随机数] 精确数：除得尽才接管（6/4=1.5），除不尽返回 false（1/3）",
            ExactDecimal.TryDivide(six, four, out var half) && half.ToPlainString() == "1.5"
            && !ExactDecimal.TryDivide(one, three, out _),
            $"{half}");

        check("[随机数] 精确数：乘方与阶乘超出显示上限时抛异常（调用方回退），不把内存撑爆",
            Throws(() => { ExactDecimal.TryParse("2", out var two); _ = two.Pow(2_000_000); })
            && Throws(() => { ExactDecimal.TryParse("2", out var two); _ = two.Pow(-1); }),
            "2^2000000 / 2^-1");

        ExactDecimal.TryParse("100000000000000000000", out var big);
        ExactDecimal.TryParse("1", out var addOne);
        check("[随机数] 精确数：20 位大整数相加不丢位（100000000000000000000 + 1）",
            (big + addOne).ToPlainString() == "100000000000000000001",
            (big + addOne).ToPlainString());

        var compared = ExactDecimal.TryParse("1.50", out var x) & ExactDecimal.TryParse("1.5", out var y);
        check("[随机数] 精确数：比较与等值按数值（1.50 == 1.5）",
            compared && x == y && x.CompareTo(y) == 0,
            $"{x} vs {y}");
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (ArithmeticException) { return true; }
        catch (ArgumentOutOfRangeException) { return true; }
    }

    // ---------------------------------------------------------------- 步长网格

    private static void CheckGrid(CheckFn check)
    {
        check("[随机数] 网格：0~100 步长 1 → 101 个刻度，首尾就是上下限",
            DecimalStepRange.TryCreate("0", "100", "1", out var r1, out _) && r1 is not null
            && r1.PointCount == 101
            && r1.ValueAt(0).ToPlainString() == "0"
            && r1.ValueAt(50).ToPlainString() == "50"
            && r1.ValueAt(100).ToPlainString() == "100",
            r1?.PointCount.ToString() ?? "null");

        check("[随机数] 网格：0~1 步长 0.1 → 11 个刻度，刻度值是精确小数（0.3 不是 0.30000000000000004）",
            DecimalStepRange.TryCreate("0", "1", "0.1", out var r2, out _) && r2 is not null
            && r2.PointCount == 11 && r2.ValueAt(3).ToPlainString() == "0.3",
            r2?.ValueAt(3).ToPlainString() ?? "null");

        check("[随机数] 网格：上限不在网格上时向下对齐（0~1 步长 0.3 → 0 / 0.3 / 0.6 / 0.9）",
            DecimalStepRange.TryCreate("0", "1", "0.3", out var r3, out _) && r3 is not null
            && r3.PointCount == 4 && r3.Upper.ToPlainString() == "0.9",
            $"{r3?.PointCount} / {r3?.Upper}");

        check("[随机数] 网格：超大步长 / 超大边界仍能算（1e30 ~ 1e30+2 步长 1 → 3 个刻度）",
            DecimalStepRange.TryCreate("1000000000000000000000000000000",
                "1000000000000000000000000000002", "1", out var r4, out _) && r4 is not null
            && r4.PointCount == 3
            && r4.ValueAt(1).ToPlainString() == "1000000000000000000000000000001",
            describeGrid(r4));

        check("[随机数] 网格：极细步长 1e-18 也能用（0~1 步长 1e-18 → 10^18+1 个刻度）",
            DecimalStepRange.TryCreate("0", "1", "1e-18", out var r5, out _) && r5 is not null
            && r5.PointCount == BigInteger.Pow(10, 18) + 1
            && r5.ValueAt(1).ToPlainString() == "0.000000000000000001",
            describeGrid(r5));

        check("[随机数] 网格：负数区间（-5 ~ 5 步长 0.5 → 21 个刻度，含 0）",
            DecimalStepRange.TryCreate("-5", "5", "0.5", out var r6, out _) && r6 is not null
            && r6.PointCount == 21 && r6.ValueAt(10).ToPlainString() == "0",
            describeGrid(r6));

        check("[随机数] 网格：非法输入给出中文原因（下限>上限 / 步长 0 / 非数字 / 空）",
            Rejected("10", "0", "1", "上限不能小于下限")
            && Rejected("0", "10", "0", "精确度必须大于 0")
            && Rejected("0", "10", "abc", "精确度")
            && Rejected("", "10", "1", "下限"),
            "10>0 / step 0 / abc / 空下限");

        var tinyOk = DecimalStepRange.TryCreate("0", "0.5", "1", out var tiny, out _);
        var thirdOk = DecimalStepRange.TryCreate("0", "1", "0.33", out var third, out _);
        check("[随机数] 网格：区间比步长小时只剩一个刻度（0~0.5 步长 1 → 只有 0），除不尽的步长按上限对齐",
            tinyOk && tiny is not null && tiny.PointCount == 1 && tiny.Upper.ToPlainString() == "0"
            && thirdOk && third is not null && third.PointCount == 4 && third.Upper.ToPlainString() == "0.99",
            tiny is null ? "null" : $"{tiny.PointCount} / {tiny.Upper} · {third?.PointCount} / {third?.Upper}");

        check("[随机数] 网格：ValueAt 越界抛参数异常（防止下标算错悄悄返回错值）",
            Throws(() => r1!.ValueAt(101)) && Throws(() => r1!.ValueAt(-1)),
            "101 / -1");

        static string describeGrid(DecimalStepRange? r)
            => r is null ? "null" : $"{r.PointCount} · {r.Lower} ~ {r.Upper}";
    }

    private static bool Rejected(string lower, string upper, string step, string expectedFragment)
        => !DecimalStepRange.TryCreate(lower, upper, step, out var range, out var error)
           || range is null || (error is not null && error.Contains(expectedFragment, StringComparison.Ordinal));

    // ---------------------------------------------------------------- 抽样

    private static void CheckSampling(CheckFn check)
    {
        DecimalStepRange.TryCreate("0", "1", "0.1", out var range, out _);

        check("[随机数] 抽样：随机源返回 0 时取到下限，返回 PointCount-1 时取到上限（闭区间）",
            range is not null
            && range.Generate(1, true, false, FirstPoint)[0] == "0"
            && range.Generate(1, true, false, _ => range.PointCount - 1)[0] == "1",
            range?.Generate(1, true, false, FirstPoint).FirstOrDefault() ?? "null");

        check("[随机数] 抽样：注入序列随机源时结果与刻度一一对应",
            range!.Generate(3, true, false, Sequence(1, 4, 10)).SequenceEqual(new[] { "0.1", "0.4", "1" }),
            string.Join(" | ", range.Generate(3, true, false, Sequence(1, 4, 10))));

        var partialSource = Sequence(3, 3, 4, 4, 5, 6, 7);
        var partial = range.Generate(5, false, false, partialSource);
        check("[随机数] 抽样：不允许重复时不会给出重复值（重复的随机源会被跳过）",
            partial.Count == 5 && partial.Distinct().Count() == 5
            && partial.Contains("0.3") && partial.Contains("0.7"),
            string.Join(" | ", partial));

        // 随机源退化成恒定值时不要死循环：凑不满就顺次补齐，仍然保证不重复
        var degenerate = range.Generate(5, false, false, _ => 3);
        check("[随机数] 抽样：随机源退化成恒定值时也能终止（凑不满就补齐，结果不重复）",
            degenerate.Count == 5 && degenerate.Distinct().Count() == 5,
            string.Join(" | ", degenerate));

        check("[随机数] 抽样：不允许重复时结果个数不超过网格点数",
            DecimalStepRange.TryCreate("0", "3", "1", out var small, out _)
            && small!.Generate(10, false, false, DecimalStepRange.UnbiasedBelow).Count == 4,
            small?.PointCount.ToString() ?? "null");

        check("[随机数] 抽样：真实随机源分布落在区间内且大多数不重复（100 次全部合法）",
            AllSamplesLegal(range, out var sampleDetail), sampleDetail);

        check("[随机数] 抽样：无偏随机源不会越界（1000 次都 &lt; n），n=1 时恒为 0",
            AllInRange() && DecimalStepRange.UnbiasedBelow(BigInteger.One).IsZero
            && ThrowsByArgument(() => DecimalStepRange.UnbiasedBelow(BigInteger.Zero)),
            "0 <= r < n");
    }

    private static bool AllSamplesLegal(DecimalStepRange range, out string detail)
    {
        var text = string.Join(" ", range.Generate(100, true, false));
        var legal = true;
        foreach (var item in range.Generate(100, true, false))
        {
            if (!ExactDecimal.TryParse(item, out var value) || value < range.Lower || value > range.Upper)
            {
                legal = false;
                break;
            }
        }
        detail = legal ? "100 个样本全在 [下限, 上限]" : text;
        return legal;
    }

    private static bool AllInRange()
    {
        for (var i = 0; i < 1000; i++)
        {
            var n = 1 + i % 97;
            if (DecimalStepRange.UnbiasedBelow(n) >= n) return false;
        }
        return true;
    }

    private static bool ThrowsByArgument(Action action)
    {
        try { action(); return false; }
        catch (ArgumentOutOfRangeException) { return true; }
    }

    // ---------------------------------------------------------------- 输出 / 排序 / 分隔符

    private static void CheckOutput(CheckFn check)
    {
        check("[随机数] 输出：按数值排序（不是按字符串字典序，2 排在 10 前面）",
            DecimalStepRange.Sort(new[] { "10", "2", "1.5", "-3" }).SequenceEqual(new[] { "-3", "1.5", "2", "10" }),
            string.Join(" | ", DecimalStepRange.Sort(new[] { "10", "2", "1.5", "-3" })));

        check("[随机数] 输出：分隔符（换行 / 逗号 / 空格 / 制表）与未知值回退换行",
            DecimalStepRange.Join(new[] { "1", "2" }, "逗号") == "1, 2"
            && DecimalStepRange.Join(new[] { "1", "2" }, "空格") == "1 2"
            && DecimalStepRange.Join(new[] { "1", "2" }, "制表") == "1\t2"
            && DecimalStepRange.Join(new[] { "1", "2" }, "换行") == "1" + Environment.NewLine + "2"
            && DecimalStepRange.Join(new[] { "1" }, "乱写") == "1",
            DecimalStepRange.Join(new[] { "1", "2" }, "逗号"));

        check("[随机数] 输出：分隔符只认四个合法值，未知值回退「换行」",
            ExactRandom.NormalizeSeparator("逗号") == "逗号"
            && ExactRandom.NormalizeSeparator(null) == "换行"
            && ExactRandom.NormalizeSeparator("乱写") == "换行",
            ExactRandom.NormalizeSeparator("乱写"));

        check("[随机数] 输出：生成条数夹到 1 ~ 10000",
            ExactRandom.ClampCount(0) == 1 && ExactRandom.ClampCount(99999) == 10000
            && ExactRandom.ClampCount(37) == 37,
            $"{ExactRandom.ClampCount(0)} / {ExactRandom.ClampCount(99999)}");

        var generated = ExactRandom.Generate("0", "1", "0.5", 3, true, true, "逗号", Sequence(1, 0, 2));
        check("[随机数] 输出：完整生成（参数合法 → 结果按排序后的精确文本 + 分隔符）",
            generated.Success && generated.Values.SequenceEqual(new[] { "0", "0.5", "1" })
            && generated.Text == "0, 0.5, 1" && string.IsNullOrEmpty(generated.UpperAlignedHint),
            Describe(generated));

        var aligned = ExactRandom.Generate("0", "1", "0.3", 1, true, false, "换行", FirstPoint);
        check("[随机数] 输出：上限被对齐时给出中文提示",
            aligned.Success && aligned.UpperAlignedHint.Contains("0.9", StringComparison.Ordinal),
            aligned.UpperAlignedHint);

        var failed = ExactRandom.Generate("10", "0", "1", 1, true, false, "换行", FirstPoint);
        check("[随机数] 输出：参数不合法时返回失败 + 中文原因，不抛异常",
            !failed.Success && !string.IsNullOrEmpty(failed.Error) && failed.Values.Count == 0,
            Describe(failed));

        check("[随机数] 输出：不重复模式的条数上限按网格点数夹取",
            ExactRandom.MaxUniqueCount(BigInteger.Pow(10, 30)) == ExactRandom.MaxCount
            && ExactRandom.MaxUniqueCount(4) == 4,
            ExactRandom.MaxUniqueCount(4).ToString());
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new RandomNumberConfig();
        check("[随机数] 配置默认：0 ~ 100 步长 1，1 个，允许重复，不排序，换行分隔",
            config.Lower == "0" && config.Upper == "100" && config.Step == "1"
            && config.Count == 1 && config.AllowDuplicates && !config.SortResults
            && config.Separator == "换行" && config.SafeCount == 1 && config.SafeSeparator == "换行",
            config.ToJson());

        var clamped = new RandomNumberConfig { Count = 99999, Separator = "乱写" };
        check("[随机数] 配置夹取：条数上限 10000、分隔符回退换行",
            clamped.SafeCount == 10000 && clamped.SafeSeparator == "换行",
            $"{clamped.SafeCount} / {clamped.SafeSeparator}");

        var roundTrip = new RandomNumberConfig
        {
            Lower = "-100000000000000000000",
            Upper = "1e30",
            Step = "0.0001",
            Count = 25,
            AllowDuplicates = false,
            SortResults = true,
            Separator = "逗号",
        };
        var json = JsonFileStore.Serialize(roundTrip);
        var parsed = JsonSerializer.Deserialize<RandomNumberConfig>(json, JsonFileStore.Options);

        check("[随机数] 配置 JSON 往返：大数边界与精确步长按文本原样保存（不被浮点化）",
            parsed is not null
            && parsed.Lower == "-100000000000000000000" && parsed.Upper == "1e30"
            && parsed.Step == "0.0001" && parsed.Count == 25
            && !parsed.AllowDuplicates && parsed.SortResults && parsed.Separator == "逗号",
            json);
    }
}
