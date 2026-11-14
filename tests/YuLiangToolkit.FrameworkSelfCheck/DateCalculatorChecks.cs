using YuLiangToolkit.Tools.Calculator;
using YuLiangToolkit.Tools.Calculator.Core.Dates;
using YuLiangToolkit.Tools.Calculator.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「计算器 → 日期计算模式」自检：日期解析（各种写法 / 中文 / 非法 / 闰年）、
/// 相差天数（含反向与 0）、加减天数（跨月 / 跨年 / 闰日 / 边界溢出）、年月日拆解、
/// 三种算法的选项与配置往返、ViewModel 端到端（固定「今天」，不读系统时间）。
///
/// <para>纯逻辑：不创建窗口、不碰剪贴板、不写真实 Config（配置只在内存里做 JSON 往返）。</para>
/// <para>由 <see cref="CalculatorChecks.Run"/> 调用，共用 <c>calculator</c> 组。</para>
/// </summary>
internal static class DateCalculatorChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckParse(fn);
        CheckLeapYear(fn);
        CheckDiff(fn);
        CheckAdd(fn);
        CheckConfig(fn);
        CheckViewModel(fn);

        report("[计算器] 日期计算模式：解析 / 相差天数 / 加减天数 / 闰年边界 / 三年月日拆解 / 配置", true);
    }

    // ---------------------------------------------------------------- 解析

    private static void CheckParse(CheckFn check)
    {
        check("[日期] 解析：- / 分隔",
            SameParse("2024-1-5", D(2024, 1, 5)) && SameParse("2024-01-05", D(2024, 1, 5)),
            Result("2024-1-5"));

        check("[日期] 解析：/ 与 . 分隔",
            SameParse("2024/1/5", D(2024, 1, 5)) && SameParse("2024.1.5", D(2024, 1, 5)),
            Result("2024/1/5"));

        check("[日期] 解析：中文「年月日 / 号」",
            SameParse("2024年1月5日", D(2024, 1, 5)) && SameParse("2024年1月5号", D(2024, 1, 5)),
            Result("2024年1月5日"));

        check("[日期] 解析：空格分隔与前后空白",
            SameParse("  2024 1 5  ", D(2024, 1, 5)), Result("  2024 1 5  "));

        check("[日期] 解析：8 位紧凑写法 20240105",
            SameParse("20240105", D(2024, 1, 5)), Result("20240105"));

        check("[日期] 解析：全角数字 + 全角斜杠（中文输入法打出来的）也不崩",
            SameParse("２０２４／１／５", D(2024, 1, 5)), Result("２０２４／１／５"));

        check("[日期] 解析：空输入给「请先填日期」",
            !DateMath.Parse("").Success && DateMath.Parse("  ").Error.Contains("请先填", StringComparison.Ordinal),
            DateMath.Parse("").Error);

        check("[日期] 解析：月日在前（1/5/2024）不瞎猜，直接报错",
            !DateMath.Parse("1/5/2024").Success
            && DateMath.Parse("1/5/2024").Error.Contains("4 位", StringComparison.Ordinal),
            DateMath.Parse("1/5/2024").Error);

        check("[日期] 解析：月份越界说清楚",
            !DateMath.Parse("2024-13-5").Success
            && DateMath.Parse("2024-13-5").Error.Contains("1~12", StringComparison.Ordinal),
            DateMath.Parse("2024-13-5").Error);

        check("[日期] 解析：2 月 30 日报错并说出当月只有几天",
            DateMath.Parse("2024-2-30").Error.Contains("只有 29 天", StringComparison.Ordinal)
            && DateMath.Parse("2023-2-29").Error.Contains("只有 28 天", StringComparison.Ordinal),
            DateMath.Parse("2024-2-30").Error);

        check("[日期] 解析：4 / 6 / 9 / 11 月只有 30 天，别写 31 号",
            !DateMath.Parse("2024-4-31").Success
            && DateMath.Parse("2024-4-30").Success
            && DateMath.Parse("2024-1-31").Success,
            DateMath.Parse("2024-4-31").Error);

        check("[日期] 解析：日期 0 号报错、负数（1--5）也不会被当成合法日期",
            !DateMath.Parse("2024-1-0").Success && !DateMath.Parse("2024-1--5").Success,
            $"{DateMath.Parse("2024-1-0").Error} / {DateMath.Parse("2024-1--5").Error}");

        check("[日期] 解析：字母混进来报错（不静默当成 0）",
            !DateMath.Parse("2024-ab-05").Success, DateMath.Parse("2024-ab-05").Error);

        check("[日期] 解析：段数不对（2 段 / 4 段）报错",
            !DateMath.Parse("2024-1").Success && !DateMath.Parse("2024-1-5-1").Success,
            DateMath.Parse("2024-1").Error);

        check("[日期] 解析：合法边界 0001-01-01 与 9999-12-31",
            SameParse("0001-01-01", D(1, 1, 1)) && SameParse("9999-12-31", D(9999, 12, 31)),
            Result("9999-12-31"));
    }

    private static bool SameParse(string text, DateOnly expected)
    {
        var parsed = DateMath.Parse(text);
        return parsed.Success && parsed.Date == expected;
    }

    private static string Result(string text)
    {
        var parsed = DateMath.Parse(text);
        return parsed.Success ? DateMath.Format(parsed.Date) : parsed.Error;
    }

    // ---------------------------------------------------------------- 闰年

    private static void CheckLeapYear(CheckFn check)
    {
        check("[日期] 闰年：2024 是、2023 不是、1900 不是、2000 是（整百年要能被 400 整除）",
            DateMath.IsLeapYear(2024) && !DateMath.IsLeapYear(2023)
            && !DateMath.IsLeapYear(1900) && DateMath.IsLeapYear(2000),
            $"2024={DateMath.IsLeapYear(2024)} / 1900={DateMath.IsLeapYear(1900)}");

        check("[日期] 当月天数：1/3/5/7/8/10/12 月 31 天；4/6/9/11 月 30 天；2 月分闰年",
            DateMath.DaysInMonth(2024, 1) == 31 && DateMath.DaysInMonth(2024, 4) == 30
            && DateMath.DaysInMonth(2024, 2) == 29 && DateMath.DaysInMonth(2023, 2) == 28,
            $"{DateMath.DaysInMonth(2024, 2)} / {DateMath.DaysInMonth(2023, 2)}");

        check("[日期] 当月天数：月份越界返回 0（不抛异常）",
            DateMath.DaysInMonth(2024, 0) == 0 && DateMath.DaysInMonth(2024, 13) == 0,
            $"{DateMath.DaysInMonth(2024, 0)} / {DateMath.DaysInMonth(2024, 13)}");
    }

    // ---------------------------------------------------------------- 相差天数

    private static void CheckDiff(CheckFn check)
    {
        check("[日期] 相差：同一天是 0 天，含首尾是 1 天",
            DateMath.DiffDays(D(2024, 1, 5), D(2024, 1, 5)) == 0
            && DateMath.InclusiveDays(0) == 1,
            "0 / 1");

        check("[日期] 相差：2024-1-5 → 2024-1-10 是 5 天（不算开始当天）",
            DateMath.DiffDays(D(2024, 1, 5), D(2024, 1, 10)) == 5,
            DateMath.DiffDays(D(2024, 1, 5), D(2024, 1, 10)).ToString());

        check("[日期] 相差：跨月 2024-1-31 → 2024-2-1 是 1 天",
            DateMath.DiffDays(D(2024, 1, 31), D(2024, 2, 1)) == 1, "1");

        check("[日期] 相差：整年 2024-1-1 → 2025-1-1 是 366 天（2024 是闰年）",
            DateMath.DiffDays(D(2024, 1, 1), D(2025, 1, 1)) == 366,
            DateMath.DiffDays(D(2024, 1, 1), D(2025, 1, 1)).ToString());

        check("[日期] 相差：平年整年 2023-1-1 → 2024-1-1 是 365 天",
            DateMath.DiffDays(D(2023, 1, 1), D(2024, 1, 1)) == 365,
            DateMath.DiffDays(D(2023, 1, 1), D(2024, 1, 1)).ToString());

        check("[日期] 相差：反向给负数（2024-1-10 → 2024-1-5 是 -5）",
            DateMath.DiffDays(D(2024, 1, 10), D(2024, 1, 5)) == -5,
            DateMath.DiffDays(D(2024, 1, 10), D(2024, 1, 5)).ToString());

        check("[日期] 含首尾：相差 5 天 → 6 天；反向也按绝对值算",
            DateMath.InclusiveDays(5) == 6 && DateMath.InclusiveDays(-5) == 6,
            $"{DateMath.InclusiveDays(5)} / {DateMath.InclusiveDays(-5)}");

        check("[日期] 相差：跨闰日 2024-2-28 → 2024-3-1 是 2 天",
            DateMath.DiffDays(D(2024, 2, 28), D(2024, 3, 1)) == 2, "2");

        check("[日期] 星期：2024-1-5 是星期五、2024-1-7 是星期日",
            DateMath.WeekdayText(D(2024, 1, 5)) == "星期五"
            && DateMath.WeekdayText(D(2024, 1, 7)) == "星期日",
            $"{DateMath.WeekdayText(D(2024, 1, 5))} / {DateMath.WeekdayText(D(2024, 1, 7))}");

        check("[日期] 格式化：y年M月d日 与 yyyy-MM-dd",
            DateMath.Format(D(2024, 1, 5)) == "2024年1月5日"
            && DateMath.FormatIso(D(2024, 1, 5)) == "2024-01-05",
            $"{DateMath.Format(D(2024, 1, 5))} / {DateMath.FormatIso(D(2024, 1, 5))}");
    }

    // ---------------------------------------------------------------- 加减天数

    private static void CheckAdd(CheckFn check)
    {
        check("[日期] 加天数：2024-1-5 + 30 = 2024-2-4",
            AddIs(D(2024, 1, 5), 30, D(2024, 2, 4)), AddText(D(2024, 1, 5), 30));

        check("[日期] 减天数：2024-1-5 − 30 = 2023-12-6（跨年）",
            AddIs(D(2024, 1, 5), -30, D(2023, 12, 6)), AddText(D(2024, 1, 5), -30));

        check("[日期] 加 0 天还是自己",
            AddIs(D(2024, 1, 5), 0, D(2024, 1, 5)), "0");

        check("[日期] 跨闰日：2024-2-28 + 1 = 2024-2-29、+ 2 = 2024-3-1",
            AddIs(D(2024, 2, 28), 1, D(2024, 2, 29)) && AddIs(D(2024, 2, 28), 2, D(2024, 3, 1)),
            $"{AddText(D(2024, 2, 28), 1)} / {AddText(D(2024, 2, 28), 2)}");

        check("[日期] 平年 2 月：2023-2-28 + 1 = 2023-3-1",
            AddIs(D(2023, 2, 28), 1, D(2023, 3, 1)), AddText(D(2023, 2, 28), 1));

        check("[日期] 加一整年：2024-1-1 + 365 = 2024-12-31、+ 366 = 2025-1-1",
            AddIs(D(2024, 1, 1), 365, D(2024, 12, 31)) && AddIs(D(2024, 1, 1), 366, D(2025, 1, 1)),
            $"{AddText(D(2024, 1, 1), 365)} / {AddText(D(2024, 1, 1), 366)}");

        check("[日期] 加减往返：加 N 再减 N 回到原日期",
            RoundTrip(D(2024, 3, 15), 137) && RoundTrip(D(2023, 12, 31), -1) && RoundTrip(D(2024, 2, 29), 400),
            "3 组往返");

        check("[日期] 边界：9999-12-31 + 1 溢出，给出中文原因",
            !DateMath.TryAddDays(D(9999, 12, 31), 1, out _, out var overflow)
            && overflow.Contains("9999", StringComparison.Ordinal),
            overflowOrEmpty());

        check("[日期] 边界：0001-01-01 − 1 溢出，给出中文原因",
            !DateMath.TryAddDays(D(1, 1, 1), -1, out _, out var underflow)
            && underflow.Contains("0001", StringComparison.Ordinal),
            underflowOrEmpty());

        check("[日期] 边界：9999-12-31 加 0 天仍然成功",
            DateMath.TryAddDays(D(9999, 12, 31), 0, out var same, out _) && same == D(9999, 12, 31),
            "9999-12-31");

        check("[日期] 天数输入：+7 / -7 / 7 / 全角＋－ 都认",
            TryParseOffset("+7", 7) && TryParseOffset("-7", -7) && TryParseOffset("7", 7)
            && TryParseOffset("＋7", 7) && TryParseOffset("－7", -7),
            "5 种写法");

        check("[日期] 天数输入：空 / 小数 / 字母报错（不静默当 0）",
            !DateMath.TryParseOffset("", out _, out var emptyError)
            && emptyError.Contains("请先填", StringComparison.Ordinal)
            && !DateMath.TryParseOffset("1.5", out _, out _)
            && !DateMath.TryParseOffset("abc", out _, out _),
            emptyError);
    }

    private static string overflowOrEmpty()
    {
        DateMath.TryAddDays(D(9999, 12, 31), 1, out _, out var error);
        return error;
    }

    private static string underflowOrEmpty()
    {
        DateMath.TryAddDays(D(1, 1, 1), -1, out _, out var error);
        return error;
    }

    private static bool AddIs(DateOnly start, int days, DateOnly expected)
        => DateMath.TryAddDays(start, days, out var result, out _) && result == expected;

    private static string AddText(DateOnly start, int days)
    {
        if (!DateMath.TryAddDays(start, days, out var result, out var error)) return error;
        return DateMath.Format(result);
    }

    private static bool TryParseOffset(string text, int expected)
        => DateMath.TryParseOffset(text, out var days, out _) && days == expected;

    /// <summary>加 N 再减 N 必须回到原日期（闰日 / 跨年都算）。</summary>
    private static bool RoundTrip(DateOnly start, int days)
    {
        if (!DateMath.TryAddDays(start, days, out var forward, out _)) return false;
        if (!DateMath.TryAddDays(forward, -days, out var back, out _)) return false;
        return back == start;
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new CalculatorConfig();
        check("[日期] 配置默认：相差天数 / 三个日期栏是空的 / 天数 0 / 标准模式",
            config.DateCalcKind == "Difference" && config.DateStart == "" && config.DateEnd == ""
            && config.DateOffset == "0" && !config.IsDateMode,
            config.ToJson());

        check("[日期] 模式选项里有「日期计算」，且三个模式互斥判断正确",
            CalculatorConfig.ModeOptions.Contains("日期计算")
            && new CalculatorConfig { Mode = "日期计算" } is { IsDateMode: true, IsKinMode: false, IsStandardMode: false },
            string.Join(" / ", CalculatorConfig.ModeOptions));

        check("[日期] 模式规范化：日期计算保留，乱写回标准计算",
            CalculatorConfig.NormalizeMode("日期计算") == "日期计算"
            && CalculatorConfig.NormalizeMode("辈分计算") == "辈分计算"
            && CalculatorConfig.NormalizeMode("date") == "标准计算",
            $"{CalculatorConfig.NormalizeMode("日期计算")} / {CalculatorConfig.NormalizeMode("date")}");

        var roundTrip = new CalculatorConfig
        {
            Mode = "日期计算",
            DateCalcKind = "Offset",
            DateStart = "2024-1-5",
            DateEnd = "2025/12/31",
            DateOffset = "-30",
        };
        var json = YuLiangToolkit.Core.Storage.JsonFileStore.Serialize(roundTrip);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<CalculatorConfig>(
            json, YuLiangToolkit.Core.Storage.JsonFileStore.Options);

        check("[日期] 配置 JSON 往返：模式 / 算法 / 三个输入栏都不丢（原文照样存）",
            parsed is { IsDateMode: true, DateCalcKind: "Offset", DateStart: "2024-1-5", DateEnd: "2025/12/31", DateOffset: "-30" },
            json);

        check("[日期] 配置 JSON 不写只读计算属性",
            !json.Contains("isDateMode", StringComparison.Ordinal)
            && !json.Contains("needsEndDate", StringComparison.Ordinal),
            json.Length > 200 ? json[..200] + "…" : json);
    }

    // ---------------------------------------------------------------- ViewModel

    private static void CheckViewModel(CheckFn check)
    {
        // 固定「今天」，不读系统时间，自检才能稳定
        var today = D(2024, 1, 5);

        var diffVm = NewViewModel(today, start: "2024-1-5", end: "2024-1-10");
        check("[日期] VM 相差天数：默认算法是相差，结果 5 天，含首尾 6 天",
            diffVm.SelectedKind.Value == DateCalcKind.Difference
            && diffVm.ResultText == "5" && diffVm.ResultUnit == "天"
            && diffVm.Details.Any(line => line.Value.Contains("6 天", StringComparison.Ordinal))
            && diffVm.ErrorText.Length == 0,
            $"{diffVm.ResultText}{diffVm.ResultUnit} / {diffVm.ErrorText}");

        check("[日期] VM 相差天数：明细里有星期与折合",
            diffVm.Details.Any(line => line.Value.Contains("星期五", StringComparison.Ordinal))
            && diffVm.Details.Any(line => line.Label == "折合"),
            string.Join(" | ", diffVm.Details.Take(3).Select(line => line.Display)));

        var offsetVm = NewViewModel(today, start: "2024-1-5", offset: "30", kind: DateCalcKind.Offset);
        check("[日期] VM 加减天数：2024-1-5 + 30 = 2024-2-4，单位是星期",
            offsetVm.ResultText == "2024年2月4日" && offsetVm.ResultUnit == "星期日"
            && offsetVm.NeedsOffset && !offsetVm.NeedsEndDate,
            $"{offsetVm.ResultText} {offsetVm.ResultUnit}");

        var negVm = NewViewModel(today, start: "2024-1-5", offset: "-30", kind: DateCalcKind.Offset);
        check("[日期] VM 加减天数：负数往前推 2023-12-6",
            negVm.ResultText == "2023年12月6日", negVm.ResultText);

        var breakdownVm = NewViewModel(today, start: "2024-1-5", end: "2025-3-8", kind: DateCalcKind.Breakdown);
        check("[日期] VM 拆开算：2024-1-5 → 2025-3-8 是 1 年 2 个月 3 天",
            breakdownVm.ResultText == "1 年 2 个月 3 天", breakdownVm.ResultText);

        var sameDayVm = NewViewModel(today, start: "2024-1-5", end: "2024-1-5", kind: DateCalcKind.Breakdown);
        check("[日期] VM 拆开算：同一天写「同一天」而不是空",
            sameDayVm.ResultText == "同一天", sameDayVm.ResultText);

        var badDateVm = NewViewModel(today, start: "2024-2-30", end: "2024-3-1");
        check("[日期] VM 错误：日期非法时结果为空 + 中文原因指出是哪一栏",
            badDateVm.ResultText.Length == 0 && badDateVm.ErrorText.StartsWith("开始日期：", StringComparison.Ordinal)
            && badDateVm.ErrorText.Contains("29 天", StringComparison.Ordinal),
            badDateVm.ErrorText);

        var badEndVm = NewViewModel(today, start: "2024-1-5", end: "x");
        check("[日期] VM 错误：结束日期填错时提示说的是「结束日期」",
            badEndVm.ErrorText.StartsWith("结束日期：", StringComparison.Ordinal) && badEndVm.ResultText.Length == 0,
            badEndVm.ErrorText);

        var badOffsetVm = NewViewModel(today, start: "2024-1-5", offset: "abc", kind: DateCalcKind.Offset);
        check("[日期] VM 错误：天数不是整数时给出提示",
            badOffsetVm.ResultText.Length == 0 && badOffsetVm.ErrorText.Contains("整数", StringComparison.Ordinal),
            badOffsetVm.ErrorText);

        var swapVm = NewViewModel(today, start: "2024-1-5", end: "2024-1-10");
        var before = (swapVm.StartText, swapVm.EndText);
        swapVm.SwapCommand.Execute(null);
        check("[日期] VM 交换：开始 / 结束互换，结果照样成立",
            swapVm.StartText == before.Item2 && swapVm.EndText == before.Item1
            && swapVm.ResultText == "5",
            $"{swapVm.StartText} / {swapVm.EndText} / {swapVm.ResultText}");

        var todayVm = NewViewModel(today);
        todayVm.UseTodayCommand.Execute("start");
        check("[日期] VM 今天按钮：「今天」填进开始日期（自检里固定为 2024-1-5）",
            todayVm.StartText == "2024年1月5日", todayVm.StartText);

        // 切换算法：三个共用一组输入，切过去内容不丢
        var (kindVm, kindConfig) = NewViewModelWithConfig(today, start: "2024-1-5", end: "2024-1-10", offset: "30");
        kindVm.SelectedKind = kindVm.KindOptions.First(o => o.Value == DateCalcKind.Offset);
        var keptInputs = kindVm.StartText == "2024-1-5" && kindVm.EndText == "2024-1-10"
                         && kindVm.OffsetText == "30" && kindVm.ResultText == "2024年2月4日";
        check("[日期] VM 切算法：三个输入栏的内容都不丢，结果立刻按新算法算",
            keptInputs, $"{kindVm.StartText} / {kindVm.EndText} / {kindVm.OffsetText} / {kindVm.ResultText}");

        check("[日期] VM 输入写回配置：填的日期与算法都记进配置，重开还原",
            kindConfig is { DateStart: "2024-1-5", DateEnd: "2024-1-10", DateOffset: "30", DateCalcKind: "Offset" },
            $"{kindConfig.DateStart} / {kindConfig.DateCalcKind}");

        var restoreVm = new YuLiangToolkit.Tools.Calculator.ViewModels.DateCalculatorViewModel(kindConfig, () => today);
        check("[日期] VM 重开还原：输入 / 算法 / 结果都回到上次的样子",
            restoreVm.StartText == "2024-1-5" && restoreVm.SelectedKind.Value == DateCalcKind.Offset
            && restoreVm.ResultText == "2024年2月4日",
            $"{restoreVm.StartText} / {restoreVm.SelectedKind.Value} / {restoreVm.ResultText}");

        var kindParseVm = YuLiangToolkit.Tools.Calculator.ViewModels.DateCalculatorViewModel.ParseKind("乱写");
        check("[日期] VM 算法解析：不认识的算法回到相差天数",
            kindParseVm == DateCalcKind.Difference
            && YuLiangToolkit.Tools.Calculator.ViewModels.DateCalculatorViewModel.ParseKind("Breakdown")
                == DateCalcKind.Breakdown,
            kindParseVm.ToString());
    }

    private static YuLiangToolkit.Tools.Calculator.ViewModels.DateCalculatorViewModel NewViewModel(
        DateOnly today, string start = "", string end = "", string offset = "", DateCalcKind? kind = null)
        => NewViewModelWithConfig(today, start, end, offset, kind).ViewModel;

    private static (YuLiangToolkit.Tools.Calculator.ViewModels.DateCalculatorViewModel ViewModel, CalculatorConfig Config)
        NewViewModelWithConfig(DateOnly today, string start = "", string end = "", string offset = "", DateCalcKind? kind = null)
    {
        var config = new CalculatorConfig
        {
            DateCalcKind = (kind ?? DateCalcKind.Difference).ToString(),
            DateStart = start,
            DateEnd = end,
            DateOffset = offset.Length == 0 && kind != DateCalcKind.Offset ? "0" : offset,
        };
        return (new YuLiangToolkit.Tools.Calculator.ViewModels.DateCalculatorViewModel(config, () => today), config);
    }
}
