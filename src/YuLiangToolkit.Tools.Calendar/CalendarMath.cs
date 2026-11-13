using System.Globalization;

namespace YuLiangToolkit.Tools.Calendar;

/// <summary>
/// 月历的纯逻辑：给定月份与「每周第一天」，算出 42 个格子、跳月、今天判定、周数。
///
/// <para>不碰界面、不读系统时间（「今天」由调用方传），所以自检可以断言闰年 / 跨年 / 1 号是周日的边界。</para>
/// </summary>
public static class CalendarMath
{
    /// <summary>一个月历格子。</summary>
    /// <param name="Date">这一格是哪天。</param>
    /// <param name="IsCurrentMonth">是不是当前显示的月份（否则是上/下月补位）。</param>
    public readonly record struct CalendarCell(DateTime Date, bool IsCurrentMonth);

    /// <summary>格子总数（6 行 × 7 列，固定高度，界面不会因为月份长短跳动）。</summary>
    public const int CellCount = 42;

    /// <summary>
    /// 算出一个月的 42 个格子。
    /// </summary>
    /// <param name="month">要显示的月份（任意一天都会归到该月 1 号）。</param>
    /// <param name="firstDayOfWeek">每周第一天。</param>
    public static List<CalendarCell> BuildMonthCells(DateTime month, DayOfWeek firstDayOfWeek)
    {
        var first = new DateTime(month.Year, month.Month, 1);

        // 1 号之前要补几格：把「星期几」换算成距离每周第一天的偏移
        var offset = ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
        var start = first.AddDays(-offset);

        var cells = new List<CalendarCell>(CellCount);
        for (var i = 0; i < CellCount; i++)
        {
            var date = start.AddDays(i);
            cells.Add(new CalendarCell(date, date.Month == first.Month && date.Year == first.Year));
        }

        return cells;
    }

    /// <summary>把任意日期归到它所在月份的 1 号。</summary>
    public static DateTime FirstOfMonth(DateTime date) => new(date.Year, date.Month, 1);

    /// <summary>上一个月（跨年自动处理）。</summary>
    public static DateTime PreviousMonth(DateTime month) => FirstOfMonth(month).AddMonths(-1);

    /// <summary>下一个月（跨年自动处理）。</summary>
    public static DateTime NextMonth(DateTime month) => FirstOfMonth(month).AddMonths(1);

    /// <summary>这个月有几天（闰年 2 月自动正确）。</summary>
    public static int DaysInMonth(DateTime month)
        => DateTime.DaysInMonth(month.Year, month.Month);

    /// <summary>标题文字：<c>2026年11月</c>。</summary>
    public static string MonthTitle(DateTime month)
        => month.ToString("yyyy年M月", CultureInfo.CurrentCulture);

    /// <summary>整天的文字：<c>2026年11月13日 周五</c>。</summary>
    public static string DayTitle(DateTime date)
        => $"{date.Year}年{date.Month}月{date.Day}日 {WeekdayLong(date.DayOfWeek)}";

    /// <summary>星期几的长名（周一 / 周二…）。</summary>
    public static string WeekdayLong(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>星期几的短名（一 / 二 / …，表头用）。</summary>
    public static string WeekdayShort(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日",
    };

    /// <summary>
    /// 按 <paramref name="firstDayOfWeek"/> 排下来的 7 个星期（表头用）。
    /// </summary>
    public static DayOfWeek[] WeekOrder(DayOfWeek firstDayOfWeek)
        => Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)firstDayOfWeek + i) % 7)).ToArray();

    /// <summary>是不是周末。</summary>
    public static bool IsWeekend(DateTime date) => date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>
    /// 键盘移动：按方向键挪一天 / 一周，按 PgUp / PgDn 挪一个月（尽量保留「几号」）。
    /// </summary>
    /// <param name="current">当前选中的日期。</param>
    /// <param name="move">移动方向。</param>
    public static DateTime Move(DateTime current, CalendarMove move) => move switch
    {
        CalendarMove.Left => current.AddDays(-1),
        CalendarMove.Right => current.AddDays(1),
        CalendarMove.Up => current.AddDays(-7),
        CalendarMove.Down => current.AddDays(7),
        CalendarMove.PreviousMonth => current.AddMonths(-1),
        CalendarMove.NextMonth => current.AddMonths(1),
        _ => current,
    };
}

/// <summary>日历里的键盘移动方向。</summary>
public enum CalendarMove
{
    /// <summary>前一天。</summary>
    Left,

    /// <summary>后一天。</summary>
    Right,

    /// <summary>上一周。</summary>
    Up,

    /// <summary>下一周。</summary>
    Down,

    /// <summary>上一个月。</summary>
    PreviousMonth,

    /// <summary>下一个月。</summary>
    NextMonth,
}
