namespace YuLiangToolkit.Tools.Calendar;

/// <summary>
/// 农历 / 二十四节气 / 公历节日 / 农历节日的换算与命名。
///
/// <para><b>农历怎么算</b>：不引第三方库，也不抄一整张百年表，用的是**压缩过的农历数据表**
/// （每年 4 个十六进制字：闰月 + 12 个月的大小 + 闰月大小）+ 标准的天文近似算法
/// （朔日按 1900 年 1 月 31 日为基准的天数推算）。1900 ~ 2100 年有效，
/// 超出范围返回 null（界面就不显示农历，而不是显示错的）。</para>
///
/// <para><b>节气怎么算</b>：用「太阳视黄经到达 15° 整数倍」的近似时刻。
/// 这里用逐年的常见近似系数表（精确到日，够日历用；不追求到分钟）。</para>
///
/// <para>本类是**纯逻辑**：不碰界面、不读系统时间，自检可以断言任意日期的农历与节气。</para>
/// </summary>
public static class LunarCalendar
{
    /// <summary>农历数据表支持的最早年份。</summary>
    public const int MinYear = 1900;

    /// <summary>农历数据表支持的最晚年份。</summary>
    public const int MaxYear = 2100;

    /// <summary>农历月份名（正月 ~ 十二月）。</summary>
    private static readonly string[] MonthNames =
    {
        "正", "二", "三", "四", "五", "六", "七", "八", "九", "十", "冬", "腊",
    };

    /// <summary>农历日名。</summary>
    private static readonly string[] DayNames =
    {
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十",
    };

    /// <summary>二十四节气名（按黄经顺序）。</summary>
    private static readonly string[] SolarTermNames =
    {
        "小寒", "大寒", "立春", "雨水", "惊蛰", "春分",
        "清明", "谷雨", "立夏", "小满", "芒种", "夏至",
        "小暑", "大暑", "立秋", "处暑", "白露", "秋分",
        "寒露", "霜降", "立冬", "小雪", "大雪", "冬至",
    };

    /// <summary>农历日期。</summary>
    /// <param name="Year">农历年（生肖年）。</param>
    /// <param name="Month">农历月（1~12）。</param>
    /// <param name="Day">农历日（1~30）。</param>
    /// <param name="IsLeapMonth">是不是闰月。</param>
    public readonly record struct LunarDate(int Year, int Month, int Day, bool IsLeapMonth)
    {
        /// <summary>月名（<c>正月</c> / <c>闰四月</c>）。</summary>
        public string MonthName => (IsLeapMonth ? "闰" : "") + MonthNames[Month - 1] + "月";

        /// <summary>日名（<c>初一</c> / <c>廿三</c>）。</summary>
        public string DayName => DayNames[Math.Clamp(Day, 1, 30) - 1];

        /// <summary>月名 + 日名（<c>四月初五</c> / <c>闰四月十五</c>）。</summary>
        public string FullName => MonthName + DayName;

        /// <summary>只是日名（格子里第二行显示的就是它）。</summary>
        public string ShortName => Day == 1 ? MonthName : DayName;
    }

    // ================================================================== 农历数据表

    /// <summary>
    /// 1900~2100 每年的农历编码（每年 1 个十六进制字，共 201 项）。
    /// <para>低 4 位：闰月月份（0 = 无闰月）；<br/>
    /// 中间 12 位（bit 4~15）：正月到腊月的大小（1 = 30 天，0 = 29 天）；<br/>
    /// 最高位（bit 16）：闰月是大月还是小月。</para>
    /// </summary>
    private static readonly int[] YearData =
    {
        0x04bd8,0x04ae0,0x0a570,0x054d5,0x0d260,0x0d950,0x16554,0x056a0,0x09ad0,0x055d2, // 1900-1909
        0x04ae0,0x0a5b6,0x0a4d0,0x0d250,0x1d255,0x0b540,0x0d6a0,0x0ada2,0x095b0,0x14977, // 1910-1919
        0x04970,0x0a4b0,0x0b4b5,0x06a50,0x06d40,0x1ab54,0x02b60,0x09570,0x052f2,0x04970, // 1920-1929
        0x06566,0x0d4a0,0x0ea50,0x06e95,0x05ad0,0x02b60,0x186e3,0x092e0,0x1c8d7,0x0c950, // 1930-1939
        0x0d4a0,0x1d8a6,0x0b550,0x056a0,0x1a5b4,0x025d0,0x092d0,0x0d2b2,0x0a950,0x0b557, // 1940-1949
        0x06ca0,0x0b550,0x15355,0x04da0,0x0a5b0,0x14573,0x052b0,0x0a9a8,0x0e950,0x06aa0, // 1950-1959
        0x0aea6,0x0ab50,0x04b60,0x0aae4,0x0a570,0x05260,0x0f263,0x0d950,0x05b57,0x056a0, // 1960-1969
        0x096d0,0x04dd5,0x04ad0,0x0a4d0,0x0d4d4,0x0d250,0x0d558,0x0b540,0x0b6a0,0x195a6, // 1970-1979
        0x095b0,0x049b0,0x0a974,0x0a4b0,0x0b27a,0x06a50,0x06d40,0x0af46,0x0ab60,0x09570, // 1980-1989
        0x04af5,0x04970,0x064b0,0x074a3,0x0ea50,0x06b58,0x055c0,0x0ab60,0x096d5,0x092e0, // 1990-1999
        0x0c960,0x0d954,0x0d4a0,0x0da50,0x07552,0x056a0,0x0abb7,0x025d0,0x092d0,0x0cab5, // 2000-2009
        0x0a950,0x0b4a0,0x0baa4,0x0ad50,0x055d9,0x04ba0,0x0a5b0,0x15176,0x052b0,0x0a930, // 2010-2019
        0x07954,0x06aa0,0x0ad50,0x05b52,0x04b60,0x0a6e6,0x0a4e0,0x0d260,0x0ea65,0x0d530, // 2020-2029
        0x05aa0,0x076a3,0x096d0,0x04afb,0x04ad0,0x0a4d0,0x1d0b6,0x0d250,0x0d520,0x0dd45, // 2030-2039
        0x0b5a0,0x056d0,0x055b2,0x049b0,0x0a577,0x0a4b0,0x0aa50,0x1b255,0x06d20,0x0ada0, // 2040-2049
        0x14b63,0x09370,0x049f8,0x04970,0x064b0,0x168a6,0x0ea50,0x06b20,0x1a6c4,0x0aae0, // 2050-2059
        0x0a2e0,0x0d2e3,0x0c960,0x0d557,0x0d4a0,0x0da50,0x05d55,0x056a0,0x0a6d0,0x055d4, // 2060-2069
        0x052d0,0x0a9b8,0x0a950,0x0b4a0,0x0b6a6,0x0ad50,0x055a0,0x0aba4,0x0a5b0,0x052b0, // 2070-2079
        0x0b273,0x06930,0x07337,0x06aa0,0x0ad50,0x14b55,0x04b60,0x0a570,0x054e4,0x0d160, // 2080-2089
        0x0e968,0x0d520,0x0daa0,0x16aa6,0x056d0,0x04ae0,0x0a9d4,0x0a2d0,0x0d150,0x0f252, // 2090-2099
        0x0d520,                                                                        // 2100
    };

    /// <summary>某农历年的闰月月份（0 = 没有闰月）。</summary>
    private static int LeapMonth(int year)
        => IsSupported(year) ? YearData[year - MinYear] & 0xF : 0;

    /// <summary>某农历年闰月的天数（没有闰月返回 0）。</summary>
    private static int LeapMonthDays(int year)
        => LeapMonth(year) == 0 ? 0
            : (YearData[year - MinYear] & 0x10000) != 0 ? 30 : 29;

    /// <summary>某农历年第 <paramref name="month"/> 个月的天数（29 或 30）。</summary>
    private static int MonthDays(int year, int month)
        => IsSupported(year) && (YearData[year - MinYear] & (0x10000 >> month)) != 0 ? 30 : 29;

    /// <summary>某农历年共几天。</summary>
    private static int YearDays(int year)
    {
        var sum = 0;
        for (var month = 1; month <= 12; month++) sum += MonthDays(year, month);
        return sum + LeapMonthDays(year);
    }

    private static bool IsSupported(int year) => year is >= MinYear and <= MaxYear;

    // ================================================================== 公历 → 农历

    /// <summary>农历数据表的基准日：1900 年 1 月 31 日 = 农历 1900 年正月初一。</summary>
    private static readonly DateTime BaseDate = new(1900, 1, 31);

    /// <summary>
    /// 公历日期 → 农历日期。超出 1900~2100 返回 null。
    /// </summary>
    public static LunarDate? FromSolar(DateTime solar)
    {
        var date = solar.Date;

        // 范围判断按「公历年份」做：数据表只覆盖 1900~2100，
        // 光靠下面的循环兜不住（2100 年末之后 offset 可能仍小于当年天数，会算出 2101 年的腊月）。
        if (!IsSupported(date.Year)) return null;
        if (date < BaseDate) return null;

        var offset = (int)(date - BaseDate).TotalDays;

        var year = MinYear;
        while (year <= MaxYear)
        {
            var days = YearDays(year);
            if (offset < days) break;
            offset -= days;
            year++;
        }

        if (year > MaxYear) return null;

        // 定位月（先正常 12 个月，再闰月）
        var leap = LeapMonth(year);
        var isLeap = false;
        var month = 1;

        while (month <= 12)
        {
            var days = isLeap ? LeapMonthDays(year) : MonthDays(year, month);

            if (offset < days) break;
            offset -= days;

            if (!isLeap && leap == month)
            {
                // 这个月之后要插一个闰月，下一轮先看闰月
                isLeap = true;
                continue;
            }

            isLeap = false;
            month++;
        }

        return new LunarDate(year, month, offset + 1, isLeap);
    }

    /// <summary>
    /// 格子里第二行要显示的文字：优先「节日」→「节气」→「初一显示月名」→「农历日」。
    ///
    /// <para>这个优先级是按"谁更值得看"定的：节日最醒目、节气次之，平常日子显示农历日。</para>
    /// </summary>
    /// <param name="solar">公历日期。</param>
    public static string DescribeShort(DateTime solar)
    {
        // ① 公历节日（国庆、元旦…）
        if (SolarFestival(solar) is { Length: > 0 } solarFestival) return solarFestival;

        // ② 农历节日（春节、中秋…）—— 也要在节气之前，否则正月初一会被当成普通「正月」，
        //    而它其实是最重要的节日之一。
        if (LunarFestival(solar) is { Length: > 0 } lunarFestival) return lunarFestival;

        // ③ 节气
        if (SolarTerm(solar) is { Length: > 0 } term) return term;

        // ④ 平常日子显示农历日（初一时显示月名）
        if (FromSolar(solar) is { } lunar) return lunar.ShortName;
        return "";
    }

    /// <summary>完整说明（悬停提示用）：<c>2026年11月13日 周五 · 十月初五 · 立冬</c>。</summary>
    public static string DescribeFull(DateTime solar)
    {
        var parts = new List<string> { CalendarMath.DayTitle(solar) };

        if (FromSolar(solar) is { } lunar) parts.Add("农历" + lunar.FullName);
        if (SolarTerm(solar) is { Length: > 0 } term) parts.Add(term);
        if (SolarFestival(solar) is { Length: > 0 } festival) parts.Add(festival);
        if (LunarFestival(solar) is { Length: > 0 } lunarFestival) parts.Add(lunarFestival);

        return string.Join(" · ", parts);
    }

    // ================================================================== 二十四节气

    /// <summary>
    /// 各节气的常数项（20 世纪 / 21 世纪各一套，按「小寒 → 冬至」顺序，与 <see cref="SolarTermNames"/> 一一对应）。
    ///
    /// <para>算法是天文上常用的近似式：<c>日 = floor(年 * 0.2422 + C) - floor(年 / 4)</c>，
    /// 其中「年」取年份后两位。精度到日，1900~2100 够日历用（不追求到分钟）。</para>
    /// </summary>
    private static readonly double[] TermConstant20 =
    {
        6.11, 20.84, 4.6295, 19.4599, 6.3826, 21.4155, 5.59, 20.888,
        6.318, 21.86, 6.5, 22.20, 7.928, 23.65, 8.35, 23.95,
        8.44, 23.822, 9.098, 24.218, 8.218, 23.08, 7.9, 22.60,
    };

    /// <summary>见 <see cref="TermConstant20"/>（21 世纪用）。</summary>
    private static readonly double[] TermConstant21 =
    {
        5.4055, 20.12, 3.87, 18.73, 5.63, 20.646, 4.81, 20.1,
        5.52, 21.04, 5.678, 21.37, 7.108, 22.83, 7.5, 23.13,
        7.646, 23.042, 8.318, 23.438, 7.438, 22.36, 7.18, 21.94,
    };

    /// <summary>
    /// 某个节气的日期（1~31）。<paramref name="index"/> 0~23 对应 <see cref="SolarTermNames"/>。
    /// <para>⚠ 早先我用「基准日 + 逐年线性偏移」写这一块，结果 11 月 8 日被误判成节气日 ——
    /// 那种写法把闰年修正和线性漂移互相抵消了。现在改用标准的常数表算法。</para>
    /// </summary>
    private static int TermDayOfYear(int year, int index)
    {
        var constants = year >= 2000 ? TermConstant21 : TermConstant20;
        var y = year % 100;
        var leapBase = year >= 2000 ? y / 4 : (y - 1) / 4;

        return (int)(y * 0.2422 + constants[index]) - leapBase;
    }

    /// <summary>
    /// 某天的节气名（不是节气日返回空串）。
    /// </summary>
    public static string SolarTerm(DateTime solar)
    {
        if (!IsSupported(solar.Year)) return "";

        var first = (solar.Month - 1) * 2;
        for (var half = 0; half < 2; half++)
        {
            if (solar.Day == TermDayOfYear(solar.Year, first + half)) return SolarTermNames[first + half];
        }

        return "";
    }

    // ================================================================== 节日

    /// <summary>公历固定节日（月, 日 → 名字）。</summary>
    private static readonly Dictionary<(int Month, int Day), string> SolarFestivals = new()
    {
        [(1, 1)] = "元旦",
        [(2, 14)] = "情人节",
        [(3, 8)] = "妇女节",
        [(3, 12)] = "植树节",
        [(4, 1)] = "愚人节",
        [(5, 1)] = "劳动节",
        [(5, 4)] = "青年节",
        [(6, 1)] = "儿童节",
        [(7, 1)] = "建党节",
        [(8, 1)] = "建军节",
        [(9, 10)] = "教师节",
        [(10, 1)] = "国庆节",
        [(12, 24)] = "平安夜",
        [(12, 25)] = "圣诞节",
    };

    /// <summary>公历节日（不是返回空串）。</summary>
    public static string SolarFestival(DateTime solar)
        => SolarFestivals.TryGetValue((solar.Month, solar.Day), out var name) ? name : "";

    /// <summary>
    /// 农历节日：只有农历日期对上才算（春节 / 元宵 / 端午 / 七夕 / 中秋 / 重阳 / 腊八 / 除夕）。
    /// </summary>
    public static string LunarFestival(DateTime solar)
    {
        if (FromSolar(solar) is not { } lunar || lunar.IsLeapMonth) return "";

        return (lunar.Month, lunar.Day) switch
        {
            (1, 1) => "春节",
            (1, 15) => "元宵节",
            (5, 5) => "端午节",
            (7, 7) => "七夕",
            (8, 15) => "中秋节",
            (9, 9) => "重阳节",
            (12, 8) => "腊八节",
            (12, 30) => "除夕",
            _ => IsLunarNewYearEve(lunar) ? "除夕" : "",
        };
    }

    /// <summary>腊月只有 29 天的年份，腊月廿九就是除夕。</summary>
    private static bool IsLunarNewYearEve(LunarDate lunar)
        => lunar.Month == 12 && lunar.Day == MonthDays(lunar.Year, 12) && lunar.Day == 29;
}
