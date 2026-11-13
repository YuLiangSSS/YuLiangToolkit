using System.Globalization;

namespace YuLiangToolkit.Tools.Alarm;

/// <summary>
/// 闹钟的时刻计算（**纯逻辑**：不碰界面、不读系统时钟，"现在"由调用方传）。
///
/// <para>三件事：</para>
/// <list type="bullet">
///   <item><see cref="ParseDays"/> / <see cref="FormatDays"/>：重复星期的存取（<c>1,2,3</c>）；</item>
///   <item><see cref="NextOccurrence"/>：下一次该响的时刻（跨天 / 跨周 / 只响一次都算对）；</item>
///   <item><see cref="TrimSnooze"/>：贪睡到什么时候。</item>
/// </list>
/// </summary>
public static class AlarmSchedule
{
    /// <summary>贪睡默认推迟多少分钟。</summary>
    public const int DefaultSnoozeMinutes = 5;

    /// <summary>星期的中文名（周一到周日）。</summary>
    private static readonly string[] WeekdayNames = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };

    /// <summary>星期名的短写法（界面上那七个勾选框）。</summary>
    private static readonly string[] WeekdayShortNames = { "一", "二", "三", "四", "五", "六", "日" };

    /// <summary>星期 <paramref name="day"/> 的中文名（周一…周日）。</summary>
    public static string WeekdayName(DayOfWeek day) => WeekdayNames[Index(day)];

    /// <summary>星期 <paramref name="day"/> 的短名（一…日）。</summary>
    public static string WeekdayShortName(DayOfWeek day) => WeekdayShortNames[Index(day)];

    /// <summary>把 <see cref="DayOfWeek"/>（0 = 周日）转成「周一 = 0 … 周日 = 6」的下标。</summary>
    private static int Index(DayOfWeek day) => ((int)day + 6) % 7;

    /// <summary>
    /// 解析重复天：<c>"1,2,3"</c>（周一 = 1 … 周日 = 7）；空 = 不重复。
    /// 非法项忽略。
    /// </summary>
    public static IReadOnlyList<DayOfWeek> ParseDays(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<DayOfWeek>();

        var result = new List<DayOfWeek>();
        foreach (var part in text.Split(new[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), out var number) || number is < 1 or > 7) continue;

            // 1 = 周一 … 7 = 周日
            var day = number == 7 ? DayOfWeek.Sunday : (DayOfWeek)number;
            if (!result.Contains(day)) result.Add(day);
        }

        return result;
    }

    /// <summary>把星期集合写成配置字符串（周一在前）。</summary>
    public static string FormatDays(IEnumerable<DayOfWeek> days)
        => string.Join(",", days
            .Distinct()
            .OrderBy(Index)
            .Select(day => day == DayOfWeek.Sunday ? 7 : (int)day));

    /// <summary>
    /// 下一次该响的时刻。
    /// <para>规则：</para>
    /// <list type="bullet">
    ///   <item>只响一次（没有重复天）：取**今天或明天**最近的那个时刻；</item>
    ///   <item>有重复天：往后找最近的那个星期（今天还没到点就算今天）；</item>
    ///   <item>算出来的时刻必须**严格大于** <paramref name="now"/>（否则会立刻触发）。</item>
    /// </list>
    /// </summary>
    /// <param name="time">响铃时刻（当天的哪个时间点）。</param>
    /// <param name="days">重复的星期（空 = 只响一次）。</param>
    /// <param name="now">「现在」。</param>
    public static DateTime? NextOccurrence(TimeSpan time, IReadOnlyList<DayOfWeek> days, DateTime now)
    {
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) return null;

        // 8 天的窗口足够覆盖「今天已过 + 下周同一天」
        for (var offset = 0; offset <= 8; offset++)
        {
            var candidate = now.Date.AddDays(offset) + time;
            if (candidate <= now) continue;   // 今天这个点已经过了

            if (days.Count == 0)
            {
                // 只响一次：找到第一个未来时刻就返回（最多明天）
                return candidate;
            }

            if (days.Contains(candidate.DayOfWeek)) return candidate;
        }

        return null;
    }

    /// <summary>贪睡到什么时候（从 <paramref name="now"/> 起推迟若干分钟）。</summary>
    public static DateTime TrimSnooze(DateTime now, int minutes = DefaultSnoozeMinutes)
        => now.AddMinutes(Math.Clamp(minutes, 1, 60));

    /// <summary>
    /// 距离下次响铃还有多久的可读写法（「1 小时 23 分」/「不到 1 分」）。
    /// </summary>
    public static string DescribeUntil(DateTime next, DateTime now)
    {
        var delta = next - now;
        if (delta <= TimeSpan.Zero) return "即将响铃";

        if (delta.TotalMinutes < 1) return "不到 1 分";

        var hours = (int)delta.TotalHours;
        var minutes = delta.Minutes;

        return hours > 0 ? $"{hours} 小时 {minutes} 分" : $"{minutes} 分";
    }

    /// <summary>
    /// 下一次响铃的文字（「今天 07:00」/「明天 07:00」/「周三 07:00」）。
    /// </summary>
    public static string DescribeNext(DateTime next, DateTime now)
    {
        var time = next.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (next.Date == now.Date) return $"今天 {time}";
        if (next.Date == now.Date.AddDays(1)) return $"明天 {time}";

        return $"{WeekdayName(next.DayOfWeek)} {time}";
    }
}
