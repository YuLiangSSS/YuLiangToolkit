using System.Globalization;
using System.Text;

namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>
/// Excel 序列日期换算：Excel 里日期其实是一个 <see cref="double"/>（整数部分是日期序号，小数部分是时间）。
///
/// <para><b>为什么不能直接 <c>DateTime.FromOADate</c></b>：Excel 有两种日期系统（1900 / 1904），
/// 而且 1900 系统里有一个著名的「1900-02-29 假闰日」Bug（Excel 为了兼容 Lotus 1-2-3 故意保留）。
/// 直接套 OLE 的换算差一天，会让所有「2024-03-01 之前的日期」整体偏移，自检直接断言这一点。</para>
/// </summary>
public static class ExcelSerialDate
{
    /// <summary>1900 日期系统里「假闰日」之后使用的基准：1899-12-30 + 序号 = 日期。</summary>
    private static readonly DateTime Epoch1900 = new(1899, 12, 30);

    /// <summary>1900 日期系统里「假闰日」之前（序号 &lt; 60）使用的基准：1899-12-31 + 序号 = 日期。</summary>
    private static readonly DateTime Epoch1900BeforeFakeLeap = new(1899, 12, 31);

    /// <summary>1904 日期系统（Mac Excel 老默认）的基准：1904-01-01 是序号 0。</summary>
    private static readonly DateTime Epoch1904 = new(1904, 1, 1);

    /// <summary>
    /// 序列号 → <see cref="DateTime"/>（含时间部分，精确到毫秒）。
    /// 负数 / NaN / 无穷大按「无效日期」处理，返回 <see cref="DateTime.MinValue"/>，不抛异常
    /// （坏文件里偶尔出现 &lt; 0 的日期，不该让整张表打不开）。
    /// </summary>
    public static DateTime ToDateTime(double serial, bool date1904)
    {
        if (double.IsNaN(serial) || double.IsInfinity(serial) || serial <= -657435) return DateTime.MinValue;

        var baseDate = date1904
            ? Epoch1904
            : serial < 60 ? Epoch1900BeforeFakeLeap : Epoch1900;

        var days = (int)Math.Floor(serial);
        // 先取整日、再单独四舍五入小数部分：避免 44927.99999999 被浮点误差显示成 23:59:59.999
        var milliseconds = (long)Math.Round((serial - days) * 86400000.0, MidpointRounding.AwayFromZero);

        try
        {
            return baseDate.AddDays(days).AddMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }
}

/// <summary>
/// Excel 数字格式的「显示值」换算：内置格式 id + 自定义格式代码（0 / # / ? / . / , / % / E+ /
/// yyyy yy m mm d dd h hh m mm s ss AM/PM / [h] / @ / 引号字面量 / 分号分段）。
///
/// <para><b>只做显示，不做公式计算</b>：单元格里存的是「缓存值」，这里只把它按格式变成用户看到的文字。
/// 完全实现 Excel 的格式语言不现实（区域 / 条件 / 颜色 / 分数 / 缩进都很细），
/// 这里覆盖「日常表格里真正会遇到的常用格式」，剩下的按最接近的方式显示。</para>
/// </summary>
public static class ExcelNumberFormat
{
    /// <summary>常规格式（空串与 "General" 等价）。</summary>
    public const string GeneralFormat = "General";

    // ---------------------------------------------------------------- 内置格式

    /// <summary>
    /// 内置数字格式 id → 格式代码（只列常用 id；不认识的返回空串 = 常规格式）。
    /// 依据 ECMA-376 的内置 numFmtId 表。
    /// </summary>
    public static string BuiltInFormatCode(int numFmtId) => numFmtId switch
    {
        0 => "",
        1 => "0",
        2 => "0.00",
        3 => "#,##0",
        4 => "#,##0.00",
        5 => "$#,##0_);($#,##0)",          // 货币（美元）—— 显示符号就够了，不还原区域货币
        6 => "$#,##0_);[Red]($#,##0)",
        7 => "$#,##0.00_);($#,##0.00)",
        8 => "$#,##0.00_);[Red]($#,##0.00)",
        9 => "0%",
        10 => "0.00%",
        11 => "0.00E+00",
        12 => "# ?/?",
        13 => "# ??/??",
        14 => "yyyy-mm-dd",                // 内置 14 是「区域短日期」，这里固定成 ISO 形式，跨区域稳定
        15 => "d-mmm-yy",
        16 => "d-mmm",
        17 => "mmm-yy",
        18 => "h:mm AM/PM",
        19 => "h:mm:ss AM/PM",
        20 => "h:mm",
        21 => "h:mm:ss",
        22 => "yyyy-mm-dd h:mm",
        37 => "#,##0_);(#,##0)",
        38 => "#,##0_);[Red](#,##0)",
        39 => "#,##0.00_);(#,##0.00)",
        40 => "#,##0.00_);[Red](#,##0.00)",
        41 => "_(* #,##0_);_(* (#,##0);_(* \"-\"_);_(@_)",
        42 => "_($* #,##0_);_($* (#,##0);_($* \"-\"_);_(@_)",
        43 => "_(* #,##0.00_);_(* (#,##0.00);_(* \"-\"??_);_(@_)",
        44 => "_($* #,##0.00_);_($* (#,##0.00);_($* \"-\"??_);_(@_)",
        45 => "mm:ss",
        46 => "[h]:mm:ss",
        47 => "mm:ss.0",
        48 => "##0.0E+0",
        49 => "@",
        _ => "",
    };

    /// <summary>这个内置 id 是不是日期 / 时间格式（解析器用它决定单元格的 <see cref="ExcelCellKind"/>）。</summary>
    public static bool IsBuiltInDateFormat(int numFmtId) => numFmtId is >= 14 and <= 22 or 45 or 46 or 47;

    /// <summary>单元格类型是不是日期：自定义格式看代码，内置 id 看表。</summary>
    public static bool IsDateFormatCodeOrBuiltIn(int numFmtId, string? formatCode)
        => IsBuiltInDateFormat(numFmtId) || IsDateFormat(formatCode);

    // ---------------------------------------------------------------- 日期格式判断

    /// <summary>
    /// 这个格式代码是不是日期 / 时间格式。
    /// <para>做法：去掉引号字面量、转义字符、下划线 / 星号占位、方括号段（<c>[Red]</c> 这种颜色不能当日期），
    /// 然后看还有没有 y / m / d / h / s 这几个日期字母。<c>[h]</c> 这种「经过时间」也算日期格式。</para>
    /// </summary>
    public static bool IsDateFormat(string? formatCode)
    {
        if (string.IsNullOrWhiteSpace(formatCode)) return false;

        var section = FirstSection(formatCode);
        var builder = new StringBuilder(section.Length);

        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];

            if (c == '"')
            {
                i = SkipQuoted(section, i);
            }
            else if (c == '\\')
            {
                i++;   // 转义字符本身不参与判断
            }
            else if (c is '_' or '*')
            {
                i++;   // 跳过「占位宽度」的下一个字符
            }
            else if (c == '[')
            {
                var close = section.IndexOf(']', i + 1);
                if (close < 0) break;

                var content = section[(i + 1)..close].Trim().ToLowerInvariant();
                // [h] / [hh] / [m] / [s] = 经过时间格式，属于日期时间
                if (content.Length > 0 && content.All(ch => ch is 'h' or 'm' or 's')) return true;

                i = close;
            }
            else
            {
                builder.Append(c);
            }
        }

        foreach (var c in builder.ToString())
        {
            if (c is 'y' or 'Y' or 'm' or 'M' or 'd' or 'D' or 'h' or 'H' or 's' or 'S') return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- 对外入口

    /// <summary>
    /// 把一个数字按格式代码变成显示文本（日期格式内部会先换成日期再格式化）。
    /// <paramref name="date1904"/> = 工作簿用 1904 日期系统（Apple 老 Excel）。格式代码为空 = 常规格式。
    /// </summary>
    public static string Format(double value, string? formatCode, bool date1904)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "";

        if (string.IsNullOrWhiteSpace(formatCode) || IsGeneralFormat(formatCode))
            return FormatGeneral(value);

        var sections = SplitSections(formatCode);
        if (sections.Count == 0) return FormatGeneral(value);

        string section;
        var needsNegativeSign = false;

        if (value < 0)
        {
            if (sections.Count >= 2)
            {
                section = sections[1];               // 负数段自己带符号 / 括号，不再另加负号
            }
            else
            {
                section = sections[0];
                needsNegativeSign = true;
            }
        }
        else if (value == 0 && sections.Count >= 3)
        {
            section = sections[2];                   // 零值段（例："0;-0;" 的 0 显示成空）
        }
        else
        {
            section = sections[0];
        }

        if (string.IsNullOrWhiteSpace(section)) return "";

        if (IsDateFormat(section))
        {
            // 日期格式按原值（带符号会算出 1899 年，所以不加负号逻辑）
            var date = ExcelSerialDate.ToDateTime(value, date1904);
            if (date == DateTime.MinValue) return FormatGeneral(value);
            return FormatDateCore(date, section, TimeSpan.FromDays(value));
        }

        return FormatNumberCore(Math.Abs(value), section, needsNegativeSign);
    }

    /// <summary>直接用日期格式化（不经过数字 / 日期系统换算）。格式代码为空时给一个稳妥的默认值。</summary>
    public static string FormatDate(DateTime value, string? formatCode)
    {
        var code = string.IsNullOrWhiteSpace(formatCode) ? "yyyy-mm-dd" : formatCode;
        return FormatDateCore(value, code, value.TimeOfDay);
    }

    /// <summary>
    /// 常规格式：整数原样，小数最多 15 位有效数字（去掉浮点尾巴，如 0.1+0.2 显示 0.3）。
    /// Excel 的「常规」对超大数也用科学计数法，这里保持同样的观感。
    /// </summary>
    public static string FormatGeneral(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "";
        if (value == 0) return "0";
        if (value == Math.Truncate(value) && Math.Abs(value) < 1e15)
            return value.ToString("0", CultureInfo.InvariantCulture);

        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------- 数字格式实现

    /// <summary>格式代码里的一个记号（数字占位符 / 小数点 / 千分位 / 百分号 / 科学计数 / 字面量）。</summary>
    private enum NumberTokenKind
    {
        Zero,
        Hash,
        Question,
        Dot,
        Comma,
        Percent,
        Exponent,
        Literal,
    }

    private readonly record struct NumberToken(NumberTokenKind Kind, string Text);

    private static string FormatNumberCore(double value, string section, bool needsNegativeSign)
    {
        var tokens = TokenizeNumber(section);
        var percentCount = tokens.Count(t => t.Kind == NumberTokenKind.Percent);

        var firstDigit = tokens.FindIndex(t => t.Kind is NumberTokenKind.Zero or NumberTokenKind.Hash or NumberTokenKind.Question);
        if (firstDigit < 0)
        {
            // 没有数字占位符（例如格式写成了纯文字）：直接把字面量拼出来，不加数字
            return RenderLiterals(tokens);
        }

        var lastDigit = tokens.FindLastIndex(t => t.Kind is NumberTokenKind.Zero or NumberTokenKind.Hash or NumberTokenKind.Question);

        var prefix = RenderLiterals(tokens.GetRange(0, firstDigit));
        var suffix = RenderLiterals(tokens.GetRange(lastDigit + 1, tokens.Count - lastDigit - 1));
        var pattern = tokens.GetRange(firstDigit, lastDigit - firstDigit + 1);

        var exponentIndex = pattern.FindIndex(t => t.Kind == NumberTokenKind.Exponent);
        var mantissaTokens = exponentIndex < 0 ? pattern : pattern.GetRange(0, exponentIndex);

        var grouping = mantissaTokens.Any(t => t.Kind == NumberTokenKind.Comma);
        var dotIndex = mantissaTokens.FindIndex(t => t.Kind == NumberTokenKind.Dot);

        var integerTokens = dotIndex < 0 ? mantissaTokens : mantissaTokens.GetRange(0, dotIndex);
        var decimalTokens = dotIndex < 0 ? new List<NumberToken>() : mantissaTokens.GetRange(dotIndex + 1, mantissaTokens.Count - dotIndex - 1);

        var minimumIntegerDigits = integerTokens.Count(t => t.Kind == NumberTokenKind.Zero);
        var decimalPlaces = decimalTokens.Count(t => t.Kind is NumberTokenKind.Zero or NumberTokenKind.Hash or NumberTokenKind.Question);
        var requiredDecimals = decimalTokens.Count(t => t.Kind == NumberTokenKind.Zero);

        var scaled = value;
        for (var i = 0; i < percentCount; i++) scaled *= 100.0;

        string numberText;
        if (exponentIndex >= 0)
        {
            // 科学计数：0.00E+00 → 小数位 = 小数点后的占位符数量，指数固定两位（Excel 常见写法）
            var exponentDigits = pattern.Skip(exponentIndex + 1).Count(t => t.Kind == NumberTokenKind.Zero);
            if (exponentDigits <= 0) exponentDigits = 2;

            var format = "0." + new string('0', decimalPlaces) + "E+" + new string('0', exponentDigits);
            if (decimalPlaces == 0) format = "0E+" + new string('0', exponentDigits);
            numberText = scaled.ToString(format, CultureInfo.InvariantCulture);
        }
        else
        {
            var rounded = Math.Round(scaled, Math.Min(decimalPlaces, 15), MidpointRounding.AwayFromZero);
            var fixedText = rounded.ToString("F" + Math.Min(decimalPlaces, 15), CultureInfo.InvariantCulture);

            var dot = fixedText.IndexOf('.');
            var integerPart = dot < 0 ? fixedText : fixedText[..dot];
            var fractionPart = dot < 0 ? "" : fixedText[(dot + 1)..];

            // '#' / '?' 是「可有可无」的小数位：把尾巴上的 0 去掉，但至少保留 0 的个数（requiredDecimals）
            while (fractionPart.Length > requiredDecimals && fractionPart.EndsWith('0'))
                fractionPart = fractionPart[..^1];

            if (integerPart.Length < minimumIntegerDigits)
                integerPart = integerPart.PadLeft(minimumIntegerDigits, '0');

            if (grouping && integerPart.Length > 3)
                integerPart = GroupThousands(integerPart);

            numberText = fractionPart.Length > 0 ? integerPart + "." + fractionPart : integerPart;
        }

        var sign = needsNegativeSign ? "-" : "";
        return prefix + sign + numberText + suffix;
    }

    private static string GroupThousands(string digits)
    {
        var builder = new StringBuilder(digits.Length + digits.Length / 3);
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0) builder.Append(',');
            builder.Append(digits[i]);
        }
        return builder.ToString();
    }

    /// <summary>把一段记号里的字面量拼出来（数字占位符之外的静态文字 / 百分号等）。</summary>
    private static string RenderLiterals(List<NumberToken> tokens)
    {
        var builder = new StringBuilder();
        foreach (var token in tokens)
        {
            builder.Append(token.Kind switch
            {
                NumberTokenKind.Literal => token.Text,
                NumberTokenKind.Percent => "%",
                NumberTokenKind.Dot => ".",
                NumberTokenKind.Comma => ",",
                NumberTokenKind.Exponent => token.Text,
                _ => "",
            });
        }
        return builder.ToString();
    }

    private static List<NumberToken> TokenizeNumber(string section)
    {
        var tokens = new List<NumberToken>();
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            switch (c)
            {
                case '"':
                    i = ReadQuoted(section, i, out var quoted);
                    tokens.Add(new NumberToken(NumberTokenKind.Literal, quoted));
                    break;
                case '\\':
                    if (i + 1 < section.Length) tokens.Add(new NumberToken(NumberTokenKind.Literal, section[++i].ToString()));
                    break;
                case '_':
                    i++;   // 下划线 = 留出下一个字符的宽度（对齐用），用空格近似
                    tokens.Add(new NumberToken(NumberTokenKind.Literal, " "));
                    break;
                case '*':
                    i++;   // 星号 = 用下一个字符填满剩余宽度，忽略（WPF 单元格里没法这样填）
                    break;
                case '[':
                {
                    var close = section.IndexOf(']', i + 1);
                    i = close < 0 ? section.Length : close;   // 颜色 / 条件 / 经过时间：数字格式里忽略
                    break;
                }
                case '0': tokens.Add(new NumberToken(NumberTokenKind.Zero, "0")); break;
                case '#': tokens.Add(new NumberToken(NumberTokenKind.Hash, "#")); break;
                case '?': tokens.Add(new NumberToken(NumberTokenKind.Question, "?")); break;
                case '.': tokens.Add(new NumberToken(NumberTokenKind.Dot, ".")); break;
                case ',': tokens.Add(new NumberToken(NumberTokenKind.Comma, ",")); break;
                case '%': tokens.Add(new NumberToken(NumberTokenKind.Percent, "%")); break;
                case 'E' or 'e' when i + 1 < section.Length && section[i + 1] is '+' or '-':
                    tokens.Add(new NumberToken(NumberTokenKind.Exponent, "E" + section[i + 1]));
                    i++;
                    break;
                case '@':
                    tokens.Add(new NumberToken(NumberTokenKind.Literal, ""));   // 文本占位符：数字格式里不适用
                    break;
                default:
                    tokens.Add(new NumberToken(NumberTokenKind.Literal, c.ToString()));
                    break;
            }
        }
        return tokens;
    }

    // ---------------------------------------------------------------- 日期格式实现

    private enum DateTokenKind
    {
        Literal,
        Year,
        Month,
        Day,
        Hour,
        Minute,
        Second,
        AmPm,
        ElapsedHour,
        ElapsedMinute,
        ElapsedSecond,
    }

    private readonly record struct DateToken(DateTokenKind Kind, int Count, string Text);

    private static string FormatDateCore(DateTime value, string formatCode, TimeSpan elapsed)
    {
        var tokens = TokenizeDate(formatCode);
        var hasAmPm = tokens.Any(t => t.Kind == DateTokenKind.AmPm);
        var builder = new StringBuilder(formatCode.Length + 8);

        foreach (var token in tokens)
        {
            switch (token.Kind)
            {
                case DateTokenKind.Literal:
                    builder.Append(token.Text);
                    break;

                case DateTokenKind.Year:
                    builder.Append(token.Count <= 2
                        ? (value.Year % 100).ToString("00", CultureInfo.InvariantCulture)
                        : value.Year.ToString("0000", CultureInfo.InvariantCulture));
                    break;

                case DateTokenKind.Month:
                    if (token.Count <= 2)
                        builder.Append(Pad(value.Month, token.Count));
                    else
                        builder.Append(MonthName(value.Month, token.Count >= 4));
                    break;

                case DateTokenKind.Day:
                    if (token.Count <= 2)
                        builder.Append(Pad(value.Day, token.Count));
                    else
                        builder.Append(WeekdayName(value.DayOfWeek, token.Count >= 4));
                    break;

                case DateTokenKind.Hour:
                    var hour = hasAmPm ? (value.Hour % 12 == 0 ? 12 : value.Hour % 12) : value.Hour;
                    builder.Append(Pad(hour, token.Count));
                    break;

                case DateTokenKind.Minute:
                    builder.Append(Pad(value.Minute, token.Count));
                    break;

                case DateTokenKind.Second:
                    builder.Append(Pad(value.Second, token.Count));
                    break;

                case DateTokenKind.AmPm:
                    builder.Append(value.Hour < 12 ? "AM" : "PM");
                    break;

                case DateTokenKind.ElapsedHour:
                    builder.Append(Pad((long)elapsed.TotalHours, token.Count));
                    break;

                case DateTokenKind.ElapsedMinute:
                    builder.Append(Pad((long)elapsed.TotalMinutes, token.Count));
                    break;

                case DateTokenKind.ElapsedSecond:
                    builder.Append(Pad((long)elapsed.TotalSeconds, token.Count));
                    break;
            }
        }

        return builder.ToString();
    }

    private static List<DateToken> TokenizeDate(string formatCode)
    {
        var tokens = new List<DateToken>();

        for (var i = 0; i < formatCode.Length; i++)
        {
            var c = formatCode[i];

            if (c == '"')
            {
                i = ReadQuoted(formatCode, i, out var quoted);
                tokens.Add(new DateToken(DateTokenKind.Literal, 0, quoted));
                continue;
            }

            if (c == '\\')
            {
                if (i + 1 < formatCode.Length)
                {
                    tokens.Add(new DateToken(DateTokenKind.Literal, 0, formatCode[++i].ToString()));
                }
                continue;
            }

            if (c is '_' or '*')
            {
                i++;
                continue;
            }

            if (c == '[')
            {
                var close = formatCode.IndexOf(']', i + 1);
                if (close < 0) break;

                var content = formatCode[(i + 1)..close].Trim().ToLowerInvariant();
                if (content.Length > 0 && content.All(ch => ch == 'h'))
                    tokens.Add(new DateToken(DateTokenKind.ElapsedHour, content.Length, ""));
                else if (content.Length > 0 && content.All(ch => ch == 'm'))
                    tokens.Add(new DateToken(DateTokenKind.ElapsedMinute, content.Length, ""));
                else if (content.Length > 0 && content.All(ch => ch == 's'))
                    tokens.Add(new DateToken(DateTokenKind.ElapsedSecond, content.Length, ""));
                // 其余方括号（颜色 / 条件）忽略

                i = close;
                continue;
            }

            var lower = char.ToLowerInvariant(c);
            if (lower is 'y' or 'm' or 'd' or 'h' or 's')
            {
                var count = 1;
                while (i + count < formatCode.Length && char.ToLowerInvariant(formatCode[i + count]) == lower) count++;

                var kind = lower switch
                {
                    'y' => DateTokenKind.Year,
                    'm' => DateTokenKind.Month,
                    'd' => DateTokenKind.Day,
                    'h' => DateTokenKind.Hour,
                    _ => DateTokenKind.Second,
                };

                tokens.Add(new DateToken(kind, count, ""));
                i += count - 1;
                continue;
            }

            // AM/PM 与 A/P（大小写不敏感）
            if ((c == 'A' || c == 'a') && TryReadAmPm(formatCode, i, out var amPmLength))
            {
                tokens.Add(new DateToken(DateTokenKind.AmPm, 0, ""));
                i += amPmLength - 1;
                continue;
            }

            tokens.Add(new DateToken(DateTokenKind.Literal, 0, c.ToString()));
        }

        // 关键歧义：m 既可能是「月」也可能是「分钟」。
        // Excel 的规则是：紧跟在 h 后面、或紧挨着 s 前面时是分钟；其它情况是月。
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != DateTokenKind.Month) continue;

            var previous = PreviousRealToken(tokens, i);
            var next = NextRealToken(tokens, i);
            var isMinute = previous == DateTokenKind.Hour || next == DateTokenKind.Second;
            if (isMinute) tokens[i] = tokens[i] with { Kind = DateTokenKind.Minute };
        }

        return tokens;
    }

    private static DateTokenKind? PreviousRealToken(List<DateToken> tokens, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (tokens[i].Kind != DateTokenKind.Literal) return tokens[i].Kind;
        }
        return null;
    }

    private static DateTokenKind? NextRealToken(List<DateToken> tokens, int index)
    {
        for (var i = index + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != DateTokenKind.Literal) return tokens[i].Kind;
        }
        return null;
    }

    private static bool TryReadAmPm(string text, int index, out int length)
    {
        length = 0;
        if (index + 5 <= text.Length
            && string.Compare(text, index, "AM/PM", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
        {
            length = 5;
            return true;
        }

        if (index + 3 <= text.Length
            && string.Compare(text, index, "A/P", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
        {
            length = 3;
            return true;
        }

        return false;
    }

    private static string Pad(long value, int count)
        => count <= 1 ? value.ToString(CultureInfo.InvariantCulture) : value.ToString(new string('0', count), CultureInfo.InvariantCulture);

    private static string MonthName(int month, bool full)
    {
        string[] shortNames = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
        string[] fullNames =
        {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December",
        };

        var index = Math.Clamp(month - 1, 0, 11);
        return full ? fullNames[index] : shortNames[index];
    }

    private static string WeekdayName(DayOfWeek dayOfWeek, bool full)
    {
        string[] shortNames = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        string[] fullNames = { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        return full ? fullNames[(int)dayOfWeek] : shortNames[(int)dayOfWeek];
    }

    // ---------------------------------------------------------------- 公共小工具

    private static bool IsGeneralFormat(string formatCode)
        => formatCode.Trim().Equals(GeneralFormat, StringComparison.OrdinalIgnoreCase);

    private static string FirstSection(string formatCode)
    {
        var sections = SplitSections(formatCode);
        return sections.Count > 0 ? sections[0] : formatCode;
    }

    /// <summary>按分号拆格式段（正数;负数;零;文本），引号 / 转义里的分号不算。</summary>
    private static List<string> SplitSections(string formatCode)
    {
        var sections = new List<string>();
        var builder = new StringBuilder();

        for (var i = 0; i < formatCode.Length; i++)
        {
            var c = formatCode[i];
            if (c == '"')
            {
                var start = i;
                i = ReadQuoted(formatCode, i, out _);
                builder.Append(formatCode.AsSpan(start, i - start + 1));
                continue;
            }

            if (c == '\\' && i + 1 < formatCode.Length)
            {
                builder.Append(c).Append(formatCode[i + 1]);
                i++;
                continue;
            }

            if (c == ';')
            {
                sections.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(c);
        }

        sections.Add(builder.ToString());
        return sections;
    }

    /// <summary>读引号字面量：<paramref name="index"/> 指向开引号，返回闭引号位置；没有闭引号时读到结尾。</summary>
    private static int ReadQuoted(string text, int index, out string content)
    {
        var builder = new StringBuilder();
        var i = index + 1;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    builder.Append('"');   // 连续两个引号 = 字面量引号
                    i += 2;
                    continue;
                }
                break;
            }

            builder.Append(text[i]);
            i++;
        }

        content = builder.ToString();
        return i;   // 指向闭引号（或末尾）
    }

    private static int SkipQuoted(string text, int index)
    {
        var i = index + 1;
        while (i < text.Length && text[i] != '"') i++;
        return i;
    }
}
