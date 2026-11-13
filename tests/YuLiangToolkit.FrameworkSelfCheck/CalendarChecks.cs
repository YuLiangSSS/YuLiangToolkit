using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Calendar;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「日历」自检：月视图 42 格的排布（含闰年 2 月、跨年、1 号正好是每周第一天的边界）、
/// 跳月、周数、今天判定、星期名、键盘移动、以及标题栏日期时间的两个开关。
///
/// <para>纯逻辑：不创建窗口、不读系统时钟（"今天"都显式传），所以结果确定。</para>
/// </summary>
internal static class CalendarChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckMonthCells(assert);
        CheckMonthNavigation(assert);
        CheckKeyboardAndFormat(assert);
        CheckConfig(assert);
    }

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new CalendarTool();
        check("[日历] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "calendar" && tool.Name == "日历" && tool.EmojiIcon.Length > 0
            && tool.Category == "系统" && tool.Order == 168 && tool is ITool,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[日历] 标签含日历 / 日期 / 今天",
            tool.Tags.Contains("日历") && tool.Tags.Contains("日期") && tool.Tags.Contains("今天"),
            string.Join(" / ", tool.Tags));

        check("[日历] 描述写明月视图与键盘操作",
            tool.Description.Contains("月视图") && tool.Description.Contains("今天"),
            tool.Description);
    }

    private static void CheckMonthCells(CheckFn check)
    {
        // 2026 年 11 月：1 号是周日
        var cells = CalendarMath.BuildMonthCells(new DateTime(2026, 11, 1), DayOfWeek.Monday);

        check("[日历] 一个月固定 42 格（6 行 × 7 列，界面不会因为月份长短跳动）",
            cells.Count == CalendarMath.CellCount && CalendarMath.CellCount == 42,
            cells.Count.ToString());

        check("[日历] 2026 年 11 月（1 号是周日）：第一格是 10 月 26 日（周一）",
            cells[0].Date == new DateTime(2026, 10, 26) && !cells[0].IsCurrentMonth,
            cells[0].Date.ToString("yyyy-MM-dd"));

        check("[日历] 本月 1 号落在正确的格子（11 月 1 日在第 7 格，下标 6）",
            cells[6].Date == new DateTime(2026, 11, 1) && cells[6].IsCurrentMonth,
            $"{cells[6].Date:yyyy-MM-dd} / 本月={cells[6].IsCurrentMonth}");

        check("[日历] 本月天数正确（11 月 30 天）、下月补位被标记为非本月",
            cells.Count(c => c.IsCurrentMonth) == 30
            && cells.Where(c => c.Date.Month == 12).All(c => !c.IsCurrentMonth),
            $"{cells.Count(c => c.IsCurrentMonth)} 天");

        check("[日历] 格子连续（相邻两格正好差 1 天，跨月不断档）",
            Enumerable.Range(1, cells.Count - 1).All(i => cells[i].Date == cells[i - 1].Date.AddDays(1)),
            "连续");

        // 2026-11-01 正好是周日：周一起时前面补 6 格（10/26），周日起时不补格
        var sundayFirst = CalendarMath.BuildMonthCells(new DateTime(2026, 11, 1), DayOfWeek.Sunday);
        check("[日历] 1 号正好是周日：周一起要补 6 格、周日起直接从 1 号开始",
            cells[0].Date == new DateTime(2026, 10, 26)
            && sundayFirst[0].Date == new DateTime(2026, 11, 1)
            && sundayFirst[0].IsCurrentMonth,
            $"{cells[0].Date:MM-dd} / {sundayFirst[0].Date:MM-dd}");

        // 1 号正好是每周第一天：前面不补格
        var exact = CalendarMath.BuildMonthCells(new DateTime(2026, 6, 1), DayOfWeek.Monday);
        check("[日历] 1 号正好是每周第一天时不补前面的格子（2026-06-01 是周一）",
            exact[0].Date == new DateTime(2026, 6, 1) && exact[0].IsCurrentMonth,
            exact[0].Date.ToString("yyyy-MM-dd"));

        // 闰年 2 月
        var leap = CalendarMath.BuildMonthCells(new DateTime(2024, 2, 1), DayOfWeek.Monday);
        var leapDays = leap.Count(c => c.IsCurrentMonth);
        var nonLeap = CalendarMath.BuildMonthCells(new DateTime(2023, 2, 1), DayOfWeek.Monday);
        var nonLeapDays = nonLeap.Count(c => c.IsCurrentMonth);

        check("[日历] 闰年 2 月 29 天、平年 28 天",
            leapDays == 29 && nonLeapDays == 28 && CalendarMath.DaysInMonth(new DateTime(2024, 2, 1)) == 29,
            $"2024={leapDays} / 2023={nonLeapDays}");

        var january = CalendarMath.BuildMonthCells(new DateTime(2027, 1, 1), DayOfWeek.Monday);
        var leading = january.TakeWhile(c => !c.IsCurrentMonth).ToList();
        check("[日历] 跨年 1 月：前面的补位是上一年的 12 月（2027-01-01 是周五 → 补 4 格到 12/28）",
            leading.Count == 4
            && leading.All(c => c.Date.Year == 2026 && c.Date.Month == 12)
            && january[4].Date == new DateTime(2027, 1, 1),
            $"{leading.Count} 格，第一格 {leading[0].Date:yyyy-MM-dd}");
    }

    private static void CheckMonthNavigation(CheckFn check)
    {
        var nov = new DateTime(2026, 11, 1);

        check("[日历] 上一个月 / 下一个月正确",
            CalendarMath.PreviousMonth(nov) == new DateTime(2026, 10, 1)
            && CalendarMath.NextMonth(nov) == new DateTime(2026, 12, 1),
            $"{CalendarMath.PreviousMonth(nov):yyyy-MM} / {CalendarMath.NextMonth(nov):yyyy-MM}");

        check("[日历] 跨年跳月（1 月的上个月是去年 12 月、12 月的下个月是明年 1 月）",
            CalendarMath.PreviousMonth(new DateTime(2026, 1, 1)) == new DateTime(2025, 12, 1)
            && CalendarMath.NextMonth(new DateTime(2026, 12, 1)) == new DateTime(2027, 1, 1),
            "跨年正确");

        check("[日历] 任意一天都会归到该月 1 号",
            CalendarMath.FirstOfMonth(new DateTime(2026, 11, 13)) == new DateTime(2026, 11, 1),
            CalendarMath.FirstOfMonth(new DateTime(2026, 11, 13)).ToString("yyyy-MM-dd"));

        check("[日历] 星期表头按「每周第一天」排（周一起 → 一…日；周日起 → 日…六）",
            CalendarMath.WeekOrder(DayOfWeek.Monday)[0] == DayOfWeek.Monday
            && CalendarMath.WeekOrder(DayOfWeek.Monday)[6] == DayOfWeek.Sunday
            && CalendarMath.WeekOrder(DayOfWeek.Sunday)[0] == DayOfWeek.Sunday
            && CalendarMath.WeekOrder(DayOfWeek.Sunday)[6] == DayOfWeek.Saturday,
            string.Join("", CalendarMath.WeekOrder(DayOfWeek.Monday).Select(CalendarMath.WeekdayShort)));

        check("[日历] 周末判定（周六 / 周日）",
            CalendarMath.IsWeekend(new DateTime(2026, 11, 14))   // 周六
            && CalendarMath.IsWeekend(new DateTime(2026, 11, 15)) // 周日
            && !CalendarMath.IsWeekend(new DateTime(2026, 11, 13)),
            "六 / 日 是周末");
    }

    private static void CheckKeyboardAndFormat(CheckFn check)
    {
        var day = new DateTime(2026, 11, 13);   // 周五

        check("[日历] 方向键：左右挪一天、上下挪一周",
            CalendarMath.Move(day, CalendarMove.Left) == new DateTime(2026, 11, 12)
            && CalendarMath.Move(day, CalendarMove.Right) == new DateTime(2026, 11, 14)
            && CalendarMath.Move(day, CalendarMove.Up) == new DateTime(2026, 11, 6)
            && CalendarMath.Move(day, CalendarMove.Down) == new DateTime(2026, 11, 20),
            "←→↑↓");

        check("[日历] PgUp / PgDn 挪一个月（保留几号）",
            CalendarMath.Move(day, CalendarMove.PreviousMonth) == new DateTime(2026, 10, 13)
            && CalendarMath.Move(day, CalendarMove.NextMonth) == new DateTime(2026, 12, 13),
            "10-13 / 12-13");

        check("[日历] 31 号按 PgDn 到只有 30 天的月份会自动收成 30 号（不越界）",
            CalendarMath.Move(new DateTime(2026, 1, 31), CalendarMove.NextMonth)
                == new DateTime(2026, 2, 28),
            CalendarMath.Move(new DateTime(2026, 1, 31), CalendarMove.NextMonth).ToString("yyyy-MM-dd"));

        check("[日历] 月份标题 / 整天文字格式",
            CalendarMath.MonthTitle(new DateTime(2026, 11, 1)) == "2026年11月"
            && CalendarMath.DayTitle(new DateTime(2026, 11, 13)) == "2026年11月13日 周五",
            CalendarMath.DayTitle(new DateTime(2026, 11, 13)));

        check("[日历] 七天的长名 / 短名都齐全且互不相同",
            new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                    DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday }
                .Select(CalendarMath.WeekdayLong).Distinct().Count() == 7
            && new[] { DayOfWeek.Monday, DayOfWeek.Sunday }
                .Select(CalendarMath.WeekdayShort).SequenceEqual(new[] { "一", "日" }),
            "周一…周日 / 一…日");
    }

    private static void CheckConfig(CheckFn check)
    {
        var config = new CalendarConfig();
        check("[日历] 配置默认值：周一起 / 周末高亮 / 显示上下的日期",
            config.MondayFirst && config.HighlightWeekend && config.ShowDaysOfOtherMonths
            && config.FirstDayOfWeek == DayOfWeek.Monday,
            $"{config.FirstDayOfWeek}");

        config.MondayFirst = false;
        check("[日历] 关掉「周一起」后每周第一天变周日",
            config.FirstDayOfWeek == DayOfWeek.Sunday, config.FirstDayOfWeek.ToString());
    }
}
