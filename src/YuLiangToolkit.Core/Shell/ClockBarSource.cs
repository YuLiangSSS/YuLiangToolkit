using YuLiangToolkit.Core.Input;

namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// 「标题栏要显示哪个时区的日期时间」—— 一处算出、两处使用（标题栏显示区 + 主页设置的说明文字）。
///
/// <para><b>谁提供时区</b>：全球时钟工具。它启动时把「当前跟随的那只钟」注册进来（<see cref="Provider"/>），
/// 外壳只拿结果。这样 Core 不用认识具体工具，时钟工具被关掉 / 还没加载时也能优雅退回本机时区。</para>
///
/// <para><b>刻意简单</b>：只有「一个时区」（不是并列多城市），因为标题栏空间有限，
/// 多城市留给时钟工具页面本身。</para>
/// </summary>
public static class ClockBarSource
{
    /// <summary>
    /// 提供「现在跟随的时区」：返回 (显示名, 时区 Id, 自定义偏移分钟)。第二个为 null 表示走系统时区。
    /// <para>没注册（时钟工具没加载 / 用户一只钟都没有）时返回 null，调用方用本机时区。</para>
    /// </summary>
    public static Func<(string Name, string TimeZoneId, int? CustomOffsetMinutes)?>? Provider { get; set; }

    /// <summary>最近的可用时区（从 provider 现取；没有就 null）。</summary>
    public static (string Name, string TimeZoneId, int? CustomOffsetMinutes)? Current()
    {
        try { return Provider?.Invoke(); }
        catch { return null; }   // provider 出问题不能连累标题栏
    }

    /// <summary>时区来源变了（时钟工具改了「跟随哪只钟」/ 加了删了城市）——标题栏据此立即刷新。</summary>
    public static event EventHandler? Changed;

    /// <summary>通知标题栏：时区来源变了。</summary>
    public static void NotifyChanged()
    {
        try { Changed?.Invoke(null, EventArgs.Empty); } catch { /* 订阅方异常不影响 */ }
    }

    /// <summary>
    /// 算出这个时区此刻的墙上时间。
    /// <para>时区查不到（配置手改坏 / 系统缺时区）时退回 UTC，不抛异常。</para>
    /// </summary>
    /// <param name="utcNow">当前 UTC 时刻。</param>
    public static (DateTime WallClock, TimeSpan Offset, bool IsDst, string Name, bool IsFallback) Resolve(DateTime utcNow)
    {
        var source = Current();

        if (source is { } s)
        {
            if (s.CustomOffsetMinutes is { } minutes)
            {
                var offset = TimeSpan.FromMinutes(Math.Clamp(minutes, -12 * 60, 14 * 60));
                return (utcNow + offset, offset, false, s.Name, false);
            }

            if (TryFindZone(s.TimeZoneId) is { } zone)
            {
                var offset = zone.GetUtcOffset(utcNow);
                return (utcNow + offset, offset, zone.IsDaylightSavingTime(utcNow), s.Name, false);
            }

            // 时区坏掉：退回本机时区（比 UTC 更贴近用户预期），并标记 fallback
            var localOffset = TimeZoneInfo.Local.GetUtcOffset(utcNow);
            return (utcNow + localOffset, localOffset, TimeZoneInfo.Local.IsDaylightSavingTime(utcNow),
                s.Name + "（时区无效，已用本机）", true);
        }

        // 没有任何钟：本机时区
        var offsetLocal = TimeZoneInfo.Local.GetUtcOffset(utcNow);
        return (utcNow + offsetLocal, offsetLocal, TimeZoneInfo.Local.IsDaylightSavingTime(utcNow),
            TimeZoneInfo.Local.DisplayName, true);
    }

    /// <summary>查系统时区（Windows Id 优先，IANA 兜底）。</summary>
    private static TimeZoneInfo? TryFindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        try
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone) && zone is not null) return zone;
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or System.Security.SecurityException)
        {
            // 时区数据损坏：按查不到处理
        }

        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id.Trim(), out var windowsId)
                && !string.IsNullOrEmpty(windowsId)
                && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var converted)
                && converted is not null)
            {
                return converted;
            }
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or System.Security.SecurityException)
        {
            // 同上
        }

        return null;
    }

    /// <summary>
    /// 标题栏上的日期文本：<c>2026年11月13日 周五</c>。
    /// </summary>
    public static string FormatDate(DateTime wallClock)
        => $"{wallClock.Year}年{wallClock.Month}月{wallClock.Day}日 {WeekdayText(wallClock.DayOfWeek)}";

    /// <summary>标题栏上的时间文本：<c>14:05:09</c>（24 小时制，带秒）。</summary>
    public static string FormatTime(DateTime wallClock) => wallClock.ToString("HH:mm:ss");

    /// <summary>星期几（与全球时钟同一套写法）。</summary>
    public static string WeekdayText(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>
    /// 拼出标题栏那一整行（按两个显示开关）。
    /// <para>两个都关 → 空串（界面整块隐藏）。</para>
    /// </summary>
    /// <param name="wallClock">该时区的墙上时间。</param>
    /// <param name="showDate">显示日期。</param>
    /// <param name="showTime">显示时间。</param>
    public static string FormatBar(DateTime wallClock, bool showDate, bool showTime)
    {
        if (!showDate && !showTime) return "";
        if (showDate && !showTime) return FormatDate(wallClock);
        if (!showDate) return FormatTime(wallClock);

        return $"{FormatDate(wallClock)}  {FormatTime(wallClock)}";
    }
}
