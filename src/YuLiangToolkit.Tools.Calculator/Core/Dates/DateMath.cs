namespace YuLiangToolkit.Tools.Calculator.Core.Dates;

/// <summary>日期文本的解析结果：成功给日期，失败给一句中文原因（界面直接显示，不弹窗）。</summary>
/// <param name="Success">解析成功。</param>
/// <param name="Date">解析出来的日期（失败时是 <see cref="DateOnly.MinValue"/>）。</param>
/// <param name="Error">失败原因（成功时为空字符串）。</param>
public readonly record struct DateParseResult(bool Success, DateOnly Date, string Error)
{
    /// <summary>成功。</summary>
    public static DateParseResult Ok(DateOnly date) => new(true, date, "");

    /// <summary>失败（带原因）。</summary>
    public static DateParseResult Fail(string error) => new(false, DateOnly.MinValue, error);
}

/// <summary>
/// 日期计算的纯逻辑（不碰界面、不读系统时间 —— 「今天」由界面传进来，自检才能断言）。
///
/// <para>支持两种算法，也是这个模式的两种计算方式：</para>
/// <list type="number">
///   <item><b>相差天数</b>：<see cref="DiffDays"/> —— 从 A 到 B 过了多少天（不含开始当天）；</item>
///   <item><b>加减天数</b>：<see cref="AddDays"/> —— A 加上 / 减去 N 天是哪一天。</item>
/// </list>
///
/// <para><b>日期输入</b>：<see cref="Parse"/> 手写解析，比 <c>DateOnly.TryParse</c> 可控 ——
/// 只认「年在前」的写法（<c>2024-1-5</c> / <c>2024/1/5</c> / <c>2024.1.5</c> / <c>2024年1月5日</c> /
/// <c>20240105</c>），绝不把 <c>1/5/2024</c> 这种月日在前的写法瞎猜成 1 月 5 日。</para>
/// </summary>
public static class DateMath
{
    /// <summary>星期名（索引 = <see cref="DayOfWeek"/>：0 = 星期日）。</summary>
    private static readonly string[] WeekdayNames =
    {
        "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六",
    };

    /// <summary>一年的第几个月有几天（<paramref name="leapYear"/> 决定 2 月是 28 还是 29）。</summary>
    public static int DaysInMonth(int year, int month, bool leapYear) => month switch
    {
        < 1 or > 12 => 0,
        2 => leapYear ? 29 : 28,
        4 or 6 or 9 or 11 => 30,
        _ => 31,
    };

    /// <summary>是不是闰年（公历规则：能被 4 整除且不能被 100 整除，或能被 400 整除）。</summary>
    public static bool IsLeapYear(int year) => DateTime.IsLeapYear(year is >= 1 and <= 9999 ? year : 1);

    /// <summary>某个年月有几天（自动判闰年）。</summary>
    public static int DaysInMonth(int year, int month)
        => DaysInMonth(year, month, year is >= 1 and <= 9999 && DateTime.IsLeapYear(year));

    /// <summary>「星期一」这种中文星期。</summary>
    public static string WeekdayText(DateOnly date) => WeekdayNames[(int)date.DayOfWeek];

    /// <summary>「2024年1月5日」。</summary>
    public static string Format(DateOnly date) => $"{date.Year}年{date.Month}月{date.Day}日";

    /// <summary>「2024-01-05」（紧凑写法，放副标题 / 复制用）。</summary>
    public static string FormatIso(DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>
    /// 两个日期相差多少天（<paramref name="to"/> 减 <paramref name="from"/>，不含开始当天）。
    /// 结果为正表示 <paramref name="to"/> 在后、为负表示在前、0 表示同一天。
    /// </summary>
    public static int DiffDays(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber;

    /// <summary>含首尾的天数（同一天算 1 天）：相差 3 天 → 含首尾 4 天。</summary>
    public static int InclusiveDays(int diffDays) => Math.Abs(diffDays) + 1;

    /// <summary>
    /// 加减天数：<paramref name="days"/> 为正就是往后、为负就是往前。
    /// 溢出 <see cref="DateOnly"/> 范围（0001-01-01 ~ 9999-12-31）时返回 false 并给出原因。
    /// </summary>
    public static bool TryAddDays(DateOnly date, int days, out DateOnly result, out string error)
    {
        var target = (long)date.DayNumber + days;
        if (target < 0 || target > DateOnly.MaxValue.DayNumber)
        {
            result = DateOnly.MinValue;
            error = days >= 0
                ? "加完超过了 9999 年 12 月 31 日，算不了"
                : "减完早于 0001 年 1 月 1 日，算不了";
            return false;
        }

        result = DateOnly.FromDayNumber((int)target);
        error = "";
        return true;
    }

    /// <summary>
    /// 全角数字 / 全角标点折成半角（中文输入法下用户常常打出全角，char.IsAsciiDigit 才可靠）。
    /// 全角 ０-９ 的码位从 0xFF10 开始，正好连续；全角标点单独处理。
    /// </summary>
    private static string NormalizeFullWidthDigits(string text)
    {
        if (text.Length == 0) return text;

        var buffer = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            buffer[i] = ch is >= '０' and <= '９' ? (char)('0' + (ch - '０')) : ch;
        }
        return new string(buffer);
    }

    /// <summary>
    /// 解析用户输入的日期。认这些写法（容忍前后空格、中文「年月日」「号」）：
    /// <c>2024-1-5</c>、<c>2024/1/5</c>、<c>2024.1.5</c>、<c>2024年1月5日</c>、<c>2024 1 5</c>、<c>20240105</c>。
    /// <b>不认</b>月日在前（<c>1/5/2024</c>）的两段年份 —— 那种写法各国习惯不同，宁可报错让用户写清楚。
    /// </summary>
    public static DateParseResult Parse(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return DateParseResult.Fail("请先填日期");

        // 中文 / 中文全角符号统一成 ASCII 分隔符，再按分隔符切
        var normalized = raw
            .Replace("年", "-", StringComparison.Ordinal)
            .Replace("月", "-", StringComparison.Ordinal)
            .Replace("日", "", StringComparison.Ordinal)
            .Replace("号", "", StringComparison.Ordinal)
            .Replace('／', '/')
            .Replace('－', '-')
            .Replace('—', '-')
            .Replace('．', '.');

        int year, month, day;

        // 全角数字（中文输入法下很常见）先折成半角：char.IsDigit 认全角，但 int.Parse 不认，
        // 不折的话下面会抛 FormatException（踩过）。
        var ascii = NormalizeFullWidthDigits(normalized);

        // ⚠ 连续分隔符（2024-1--5）：Split 时中间的负号会被当成空段丢掉，结果错算成 2024-1-5。
        // 这类输入必须直接拒绝，不能「看着像日期」就当合法。
        if (ascii.Contains("--", StringComparison.Ordinal) || ascii.Contains("//", StringComparison.Ordinal)
            || ascii.Contains("..", StringComparison.Ordinal) || ascii.Contains(" -", StringComparison.Ordinal)
            || ascii.Contains("- ", StringComparison.Ordinal) || ascii.Contains(" /", StringComparison.Ordinal)
            || ascii.Contains("/ ", StringComparison.Ordinal))
        {
            return DateParseResult.Fail("日期里有连续的分隔符或多余的空格，请写成 2024-1-5 这样");
        }

        var parts = ascii.Split(new[] { '-', '/', '.', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            // 纯数字：只认 8 位 yyyyMMdd
            var digits = parts[0];
            if (digits.Length != 8 || !digits.All(char.IsAsciiDigit))
                return DateParseResult.Fail("看不懂这个日期：请写成 2024-1-5 或 2024年1月5日");

            year = int.Parse(digits[..4]);
            month = int.Parse(digits[4..6]);
            day = int.Parse(digits[6..]);
        }
        else if (parts.Length == 3)
        {
            if (!parts.All(part => part.All(char.IsAsciiDigit)))
                return DateParseResult.Fail("日期里只能有数字和 - / . 分隔符");

            // 年在前：第一段是年份（必须 4 位，避免「24-1-5」这种年份不明的写法）
            if (parts[0].Length != 4)
                return DateParseResult.Fail("年份请写完整的 4 位，例如 2024-1-5");

            year = int.Parse(parts[0]);
            month = int.Parse(parts[1]);
            day = int.Parse(parts[2]);
        }
        else
        {
            return DateParseResult.Fail("看不懂这个日期：请写成 2024-1-5 或 2024年1月5日");
        }

        if (month is < 1 or > 12) return DateParseResult.Fail($"月份要在 1~12 之间（现在是 {month}）");

        var maxDay = DaysInMonth(year, month);
        if (day < 1) return DateParseResult.Fail("日期要在 1 号以后");
        if (day > maxDay)
        {
            return DateParseResult.Fail($"{year} 年 {month} 月只有 {maxDay} 天（现在是 {day} 号）");
        }

        return DateParseResult.Ok(new DateOnly(year, month, day));
    }

    /// <summary>
    /// 解析「天数」输入（加减天数那一栏）：可正可负，允许 <c>+7</c> / <c>-7</c> / <c>7</c>。
    /// 不是整数、或超出 int 范围就报错。
    /// </summary>
    public static bool TryParseOffset(string? text, out int days, out string error)
    {
        var raw = (text ?? "").Trim().Replace('＋', '+').Replace('－', '-');
        if (raw.Length == 0)
        {
            days = 0;
            error = "请先填要加 / 减的天数";
            return false;
        }

        if (!int.TryParse(raw, out days))
        {
            days = 0;
            error = "天数请填整数（可以带负号，例如 30 或 -30）";
            return false;
        }

        error = "";
        return true;
    }
}
