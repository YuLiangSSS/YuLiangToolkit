namespace YuLiangToolkit.Tools.WorldClock.Services;

/// <summary>
/// 一天里的时段（按城市当地时间的小时数分档）。界面据此给卡片换底色、换 Emoji，
/// 让「半夜的城市」一眼就能看出来。
/// </summary>
public enum DayPhase
{
    /// <summary>清晨（5:00 ~ 7:59）。</summary>
    Dawn,

    /// <summary>白天（8:00 ~ 16:59）。</summary>
    Day,

    /// <summary>傍晚（17:00 ~ 19:59）。</summary>
    Evening,

    /// <summary>夜晚（20:00 ~ 4:59）。</summary>
    Night,
}

/// <summary>
/// 时区解析结果：此刻这个城市相对 UTC 的偏移 + 是否夏令时 + 时区是否存在。
/// 把「查系统时区」和「换算显示」分开，纯逻辑部分就能在自检里无头断言。
/// </summary>
/// <param name="Offset">此刻生效的 UTC 偏移（含夏令时）。</param>
/// <param name="IsDaylightSaving">此刻是否处于夏令时。</param>
/// <param name="IsMissing">时区 Id 在本机系统里查不到（或自定义偏移越界）。</param>
public readonly record struct TimeZoneResolution(TimeSpan Offset, bool IsDaylightSaving, bool IsMissing);

/// <summary>
/// 「全球时钟」的纯逻辑：时区解析 / 时间换算 / 显示格式化 / 时段分档 / 列表操作。
///
/// <para><b>为什么不自己算偏移</b>：夏令时的起止规则每个国家都不一张（有的按最后一个周日、
/// 有的干脆没有），硬编码一张表迟早过时。这里全部交给 <see cref="TimeZoneInfo"/>，
/// 它读的是 Windows 自带的时区库（随系统更新维护）。</para>
///
/// <para>本类**不碰界面、不读配置文件**，所以自检可以直接构造任意时刻断言。</para>
/// </summary>
public static class ClockMath
{
    /// <summary>自定义偏移的合法范围（分钟）：UTC-12:00 ~ UTC+14:00（含 +5:30 / +5:45 这种）。</summary>
    public const int MinOffsetMinutes = -12 * 60;

    /// <summary>自定义偏移上限（分钟）。</summary>
    public const int MaxOffsetMinutes = 14 * 60;

    /// <summary>凌晨之前算不算「昨天」的边界小时（用于跨日角标的判断基准）。</summary>
    public const int DayPhaseBoundary = 5;

    // ---------------------------------------------------------------- 时区解析

    /// <summary>
    /// 解析一只钟此刻的时区信息：<paramref name="customOffsetMinutes"/> 非 null 时直接用自定义偏移
    /// （不参与夏令时）；否则查 <see cref="TimeZoneInfo"/>。查不到时退化成 UTC 并标记
    /// <see cref="TimeZoneResolution.IsMissing"/>（界面会提示，而不是白屏 / 抛异常）。
    /// </summary>
    public static TimeZoneResolution Resolve(string? timeZoneId, int? customOffsetMinutes, DateTime utcNow)
    {
        if (customOffsetMinutes is { } minutes)
        {
            var clamped = Math.Clamp(minutes, MinOffsetMinutes, MaxOffsetMinutes);
            return new TimeZoneResolution(TimeSpan.FromMinutes(clamped), false, false);
        }

        if (TryFindZone(timeZoneId) is not { } zone)
            return new TimeZoneResolution(TimeSpan.Zero, false, true);

        var utc = Normalize(utcNow);
        var offset = zone.GetUtcOffset(utc);
        return new TimeZoneResolution(offset, zone.IsDaylightSavingTime(utc), false);
    }

    /// <summary>
    /// 查系统时区。先按 Windows Id 找；找不到再试 IANA Id（<c>Asia/Shanghai</c>）——
    /// 用户手改配置 / 从别的系统抄来配置时可能写成 IANA 名。
    /// </summary>
    public static TimeZoneInfo? TryFindZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)) return null;
        var id = timeZoneId.Trim();

        try
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone is not null) return zone;
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or System.Security.SecurityException)
        {
            // 时区数据损坏 / 无权限读：当成找不到，界面用 UTC 兜底
        }

        // IANA 名字：.NET 在 Windows 上默认不做映射，这里自己试一次转换
        try
        {
            if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId)
                && !string.IsNullOrEmpty(windowsId)
                && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out var converted)
                && converted is not null)
            {
                return converted;
            }
        }
        catch (Exception ex) when (ex is InvalidTimeZoneException or System.Security.SecurityException)
        {
            // 同上，忽略
        }

        return null;
    }

    // ---------------------------------------------------------------- 时间换算

    /// <summary>把 UTC 时刻换成某个偏移下的「墙上时间」（Kind 固定为 Unspecified，只是给人看的数字）。</summary>
    public static DateTime ToWallClock(DateTime utcNow, TimeSpan offset)
        => Normalize(utcNow).Add(offset);

    /// <summary>
    /// 统一拿 UTC：传入值若是 <see cref="DateTimeKind.Unspecified"/> 按 UTC 处理
    /// （自检里构造 <c>new DateTime(2024,1,1,0,0,0)</c> 时不用每次写 Kind）。
    /// </summary>
    public static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// 本机当前偏移（含夏令时）：拿 <paramref name="utcNow"/> 那一刻问本机时区，
    /// 而不是用 <c>TimeZoneInfo.Local.BaseUtcOffset</c>（后者不含夏令时，对有夏令时的地区差一小时）。
    /// </summary>
    public static TimeSpan LocalOffset(DateTime utcNow) => TimeZoneInfo.Local.GetUtcOffset(Normalize(utcNow));

    // ---------------------------------------------------------------- 格式化

    /// <summary>
    /// 时间文本：24 小时制 <c>14:05</c> / <c>14:05:09</c>；
    /// 12 小时制 <c>下午 2:05</c> / <c>下午 2:05:09</c>（午夜与正午分别是「凌晨 12:xx」「中午 12:xx」）。
    /// </summary>
    public static string FormatTime(DateTime wallClock, bool hour24, bool showSeconds)
    {
        var second = showSeconds ? $":{wallClock.Second:00}" : "";
        if (hour24) return $"{wallClock.Hour:00}:{wallClock.Minute:00}{second}";

        var (prefix, hour12) = wallClock.Hour switch
        {
            0 => ("凌晨", 12),
            < 6 => ("凌晨", wallClock.Hour),
            < 12 => ("上午", wallClock.Hour),
            12 => ("中午", 12),
            < 18 => ("下午", wallClock.Hour - 12),
            _ => ("晚上", wallClock.Hour - 12),
        };
        return $"{prefix} {hour12}:{wallClock.Minute:00}{second}";
    }

    /// <summary>日期 + 星期，例如「3月5日 周三」（年份不同本地时才补上年份，见 <see cref="FormatDate(DateTime, int)"/>）。</summary>
    public static string FormatDate(DateTime wallClock) => $"{wallClock.Month}月{wallClock.Day}日 {WeekdayText(wallClock.DayOfWeek)}";

    /// <summary>
    /// 日期 + 星期。跨年时把年份带上（12 月 31 日看东京的 1 月 1 日，不写年份容易看错）。
    /// </summary>
    /// <param name="wallClock">该城市的墙上时间。</param>
    /// <param name="localYear">本地时间是哪一年（比较用）。</param>
    public static string FormatDate(DateTime wallClock, int localYear)
        => wallClock.Year == localYear
            ? FormatDate(wallClock)
            : $"{wallClock.Year}年{FormatDate(wallClock)}";

    /// <summary>星期几的中文短名。</summary>
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
    /// 跨日说明：0 = 同一天（空串，界面隐藏这个角标）；+1 = 明天；-1 = 昨天；
    /// 更远的写「2 天后」「3 天前」（理论上只有 ±1，自定义偏移范围也到不了 2 天，兜底而已）。
    /// </summary>
    public static string DescribeDayOffset(int days)
    {
        if (days == 0) return "";
        if (days == 1) return "明天";
        if (days == -1) return "昨天";
        return days > 0 ? $"{days} 天后" : $"{-days} 天前";
    }

    /// <summary>UTC 偏移的展示写法：<c>UTC+08:00</c> / <c>UTC-05:30</c> / <c>UTC</c>（零偏移不写 +00:00）。</summary>
    public static string FormatOffset(TimeSpan offset)
    {
        if (offset == TimeSpan.Zero) return "UTC";
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset < TimeSpan.Zero ? offset.Negate() : offset;
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }

    /// <summary>
    /// 与本地时差的说明：<c>与本地相同</c> / <c>快 8 小时</c> / <c>慢 5 小时 30 分</c> / <c>慢 45 分钟</c>。
    /// </summary>
    public static string FormatDiff(TimeSpan diff)
    {
        if (diff == TimeSpan.Zero) return "与本地相同";

        var negative = diff < TimeSpan.Zero;
        var abs = negative ? diff.Negate() : diff;
        var hours = (int)abs.TotalHours;
        var minutes = abs.Minutes;
        var direction = negative ? "慢" : "快";

        if (hours == 0) return $"{direction} {minutes} 分钟";
        return minutes == 0 ? $"{direction} {hours} 小时" : $"{direction} {hours} 小时 {minutes} 分";
    }

    /// <summary>
    /// 时段分档：5:00~7:59 清晨、8:00~16:59 白天、17:00~19:59 傍晚、其余夜晚。
    /// 分档口径集中在这里，改档位记得同步 <see cref="PhaseText"/> 与自检。
    /// </summary>
    public static DayPhase ClassifyDayPhase(int hour) => hour switch
    {
        >= 5 and < 8 => DayPhase.Dawn,
        >= 8 and < 17 => DayPhase.Day,
        >= 17 and < 20 => DayPhase.Evening,
        _ => DayPhase.Night,
    };

    /// <summary>时段文字。</summary>
    public static string PhaseText(DayPhase phase) => phase switch
    {
        DayPhase.Dawn => "清晨",
        DayPhase.Day => "白天",
        DayPhase.Evening => "傍晚",
        _ => "夜晚",
    };

    /// <summary>时段 Emoji（都是常见码位，不会变豆腐块）。</summary>
    public static string PhaseEmoji(DayPhase phase) => phase switch
    {
        DayPhase.Dawn => "🌅",
        DayPhase.Day => "☀️",
        DayPhase.Evening => "🌇",
        _ => "🌙",
    };

    // ---------------------------------------------------------------- 刷新节奏

    /// <summary>
    /// 离下一个整秒还有多少毫秒（1 ~ 1000）。计时器据此对齐到整秒再刷新，
    /// 而不是「每 200ms 刷一次、数字乱跳」。
    /// </summary>
    public static int DelayToNextSecond(DateTime utcNow)
    {
        var ms = Normalize(utcNow).Millisecond;
        var delay = 1000 - ms % 1000;
        return delay is <= 0 or > 1000 ? 1000 : delay;
    }

    // ---------------------------------------------------------------- 列表操作

    /// <summary>
    /// 把 <paramref name="index"/> 位置的元素移到 <paramref name="targetIndex"/>（都夹到合法范围）。
    /// 返回是否真的移动了（下标越界 / 原地不动返回 false，界面就不用刷新了）。
    /// </summary>
    public static bool Move<T>(IList<T> list, int index, int targetIndex)
    {
        if (list is null || list.Count == 0) return false;
        if (index < 0 || index >= list.Count) return false;
        var target = Math.Clamp(targetIndex, 0, list.Count - 1);
        if (target == index) return false;

        var item = list[index];
        list.RemoveAt(index);
        list.Insert(target, item);
        return true;
    }

    /// <summary>
    /// 计算拖放后的目标下标：把 <paramref name="fromIndex"/> 拖到「第 <paramref name="insertBefore"/> 项之前」
    /// （<paramref name="insertBefore"/> = 列表长度表示拖到末尾）。
    /// <para>⚠ 这是拖放最容易错的地方：先移除再插入，所以往下拖时插入点要减 1，
    /// 否则「拖到下一项前面」会变成原地不动。这个换算单独抽出来，自检直接断言。</para>
    /// </summary>
    public static int ResolveDropIndex(int fromIndex, int insertBefore, int count)
    {
        if (count <= 0) return 0;
        var before = Math.Clamp(insertBefore, 0, count);
        return fromIndex < before ? before - 1 : before;
    }

    /// <summary>把分钟数格式化成「+8」「+5:30」「-3:30」这种紧凑写法（自定义偏移输入框旁提示用）。</summary>
    public static string FormatOffsetMinutes(int minutes)
    {
        var clamped = Math.Clamp(minutes, MinOffsetMinutes, MaxOffsetMinutes);
        var abs = Math.Abs(clamped);
        var hours = abs / 60;
        var rest = abs % 60;
        var sign = clamped < 0 ? "-" : "+";
        return rest == 0 ? $"{sign}{hours}" : $"{sign}{hours}:{rest:00}";
    }

    /// <summary>
    /// 解析手动输入的 UTF 偏移：接受 <c>+8</c> / <c>-5:30</c> / <c>5.5</c> / <c>UTC+8</c> / <c>+05:45</c>。
    /// 解析失败返回 null（界面提示「写法看不懂」而不是静默成 0）。
    /// </summary>
    public static int? ParseOffsetMinutes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var value = text.Trim().ToUpperInvariant();
        if (value.StartsWith("UTC", StringComparison.Ordinal)) value = value[3..].Trim();
        if (value.StartsWith("GMT", StringComparison.Ordinal)) value = value[3..].Trim();
        if (value.Length == 0) return 0;

        if (value.Contains(':'))
        {
            var parts = value.Split(':');
            if (parts.Length != 2) return null;
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var hourPart)) return null;
            if (!int.TryParse(parts[1], out var minutePart) || minutePart is < 0 or > 59) return null;

            var sign = hourPart < 0 ? -1 : 1;
            var total = (int)Math.Round(Math.Abs(hourPart) * 60) + minutePart;
            return Math.Clamp(sign * total, MinOffsetMinutes, MaxOffsetMinutes);
        }

        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var hours)) return null;

        var minutes = (int)Math.Round(hours * 60);
        return Math.Clamp(minutes, MinOffsetMinutes, MaxOffsetMinutes);
    }
}
