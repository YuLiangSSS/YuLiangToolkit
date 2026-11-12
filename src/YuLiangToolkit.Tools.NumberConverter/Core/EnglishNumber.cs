using System.Globalization;
using System.Numerics;
using System.Text;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.NumberConverter.Core;

/// <summary>英文读法的口味参数（界面上一组勾选框）。</summary>
public sealed record EnglishStyle
{
    /// <summary>英式：百位后加 and（one hundred and twenty-three）。关掉 = 美式（one hundred twenty-three）。</summary>
    public bool UseAnd { get; init; } = true;

    /// <summary>开头那个 1 用 a 念（1000 → a thousand、1000000 → a million；单独的 100 仍是 one hundred）。</summary>
    public bool UseA { get; init; } = true;

    /// <summary>十位与个位之间用连字符（twenty-three）；关掉就是空格（twenty three）。</summary>
    public bool Hyphenate { get; init; } = true;

    /// <summary>默认口味：英式 + a thousand + 连字符（也就是用户举的两个例子）。</summary>
    public static EnglishStyle Default { get; } = new();
}

/// <summary>
/// 英文数字读法：基数词（one thousand / a thousand）、序数词（twenty-third）、逐位读（one two three）、
/// 年份（nineteen ninety-eight）、金额（twelve dollars and thirty-four cents）、支票写法（twelve and 34/100）。
///
/// <para>最多支持到 decillion（10^33，36 位）；再大返回 null，让界面提示「超出英文读法范围」。</para>
/// <para>纯逻辑、不碰界面，可直接被自检断言。</para>
/// </summary>
public static class EnglishNumber
{
    private static readonly string[] Units =
    {
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    };

    private static readonly string[] Tens =
    {
        "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
    };

    /// <summary>三位一档的量级名（索引 = 第几档，0 = 个级）。</summary>
    private static readonly string[] Scales =
    {
        "", "thousand", "million", "billion", "trillion", "quadrillion",
        "quintillion", "sextillion", "septillion", "octillion", "nonillion", "decillion",
    };

    /// <summary>英文读法最大支持多少位（12 档 × 3 位）。</summary>
    public static int MaxDigits => Scales.Length * 3;

    // ---------------------------------------------------------------- 基数词

    /// <summary>
    /// 整数基数词。0 → zero；负数前缀 minus；
    /// 例：123 → one hundred and twenty-three、1000 → a thousand（<see cref="EnglishStyle.UseA"/> 关掉是 one thousand）。
    /// 超出 <see cref="MaxDigits"/> 位返回 null。
    /// </summary>
    public static string? FormatCardinal(BigInteger value, EnglishStyle style)
    {
        style ??= EnglishStyle.Default;
        if (value.IsZero) return Units[0];

        var negative = value.Sign < 0;
        var rest = BigInteger.Abs(value);

        var groups = new List<int>();
        while (!rest.IsZero)
        {
            groups.Add((int)(rest % 1000));
            rest /= 1000;
        }
        if (groups.Count > Scales.Length) return null;

        var sb = new StringBuilder();
        var emitted = false;
        for (var i = groups.Count - 1; i >= 0; i--)
        {
            var group = groups[i];
            if (group == 0) continue;

            var isLast = true;
            for (var k = i - 1; k >= 0; k--)
            {
                if (groups[k] != 0)
                {
                    isLast = false;
                    break;
                }
            }

            if (emitted) sb.Append(' ');

            // 英式：最后一个不足百位的档前补 and（1002 → one thousand and two）
            if (style.UseAnd && emitted && isLast && group < 100) sb.Append("and ");

            // 开头的 1 念 a，但只限「带量级」的档（a thousand / a million）；
            // 一百这种纯百位仍念 one hundred（a hundred and twenty-three 不是地道说法）
            if (style.UseA && !emitted && group == 1 && i > 0) sb.Append('a');
            else sb.Append(GroupText(group, style));

            if (i > 0) sb.Append(' ').Append(Scales[i]);
            emitted = true;
        }

        var body = sb.ToString();
        return negative ? "minus " + body : body;
    }

    /// <summary>带符号的整数基数词（负号读 minus）。</summary>
    public static string? FormatCardinalSigned(BigInteger value, EnglishStyle style)
        => FormatCardinal(value, style);

    /// <summary>小数读法：整数部分按基数词，小数部分逐位（3.14 → three point one four）。</summary>
    public static string? FormatDecimal(ExactDecimal value, EnglishStyle style)
    {
        var text = value.ToPlainString();
        if (text.Length > ExactDecimal.MaxTextLength) return null;

        var negative = text.StartsWith('-');
        if (negative) text = text[1..];

        var dot = text.IndexOf('.');
        var integerText = dot < 0 ? text : text[..dot];
        var fractionText = dot < 0 ? "" : text[(dot + 1)..];

        if (!BigInteger.TryParse(integerText, NumberStyles.None, CultureInfo.InvariantCulture, out var integer))
            return null;

        var body = FormatCardinal(integer, style);
        if (body is null) return null;

        var sb = new StringBuilder();
        if (negative) sb.Append("minus ");
        sb.Append(body);
        if (fractionText.Length > 0)
        {
            sb.Append(" point");
            foreach (var ch in fractionText)
            {
                if (ch is < '0' or > '9') return null;
                sb.Append(' ').Append(Units[ch - '0']);
            }
        }
        return sb.ToString();
    }

    /// <summary>逐位读（念号码）：1203 → one two zero three；小数点读 point。</summary>
    public static string? FormatDigits(string? text, EnglishStyle style)
    {
        style ??= EnglishStyle.Default;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var words = new List<string>();
        foreach (var ch in text.Trim())
        {
            if (ch is >= '0' and <= '9') words.Add(Units[ch - '0']);
            else if (ch == '.') words.Add("point");
            else if (ch == '-') words.Add("minus");
            else if (ch is '+' or ',' or ' ') continue;
            else return null;
        }
        if (words.Count == 0) return null;
        return string.Join(' ', words);
    }

    // ---------------------------------------------------------------- 序数词

    /// <summary>序数词：123 → one hundred and twenty-third（只把最后一个词改成序数）。</summary>
    public static string? FormatOrdinal(BigInteger value, EnglishStyle style)
    {
        var cardinal = FormatCardinal(value, style);
        if (cardinal is null) return null;

        // 连字符写法（twenty-three）要把「-」后面那个词变序数，所以先按空格切分整个词
        var at = cardinal.LastIndexOf(' ');
        var head = at < 0 ? "" : cardinal[..(at + 1)];
        var last = at < 0 ? cardinal : cardinal[(at + 1)..];
        return head + Ordinalize(last);
    }

    /// <summary>小数序数：3.5 → three point five（小数没有序数说法，不硬加后缀）。</summary>
    public static string? FormatDecimalOrdinal(ExactDecimal value, EnglishStyle style)
    {
        if (!value.IsInteger) return FormatDecimal(value, style);
        return value.TryToBigInteger(out var integer) ? FormatOrdinal(integer, style) : null;
    }

    /// <summary>单个英文数词变序数：one → first、twenty → twentieth、hundred → hundredth、twenty-three → twenty-third。</summary>
    private static string Ordinalize(string word)
    {
        // 连字符写法：只把后半截变序数（twenty-three → twenty-third）
        var dash = word.LastIndexOf('-');
        if (dash > 0) return word[..(dash + 1)] + Ordinalize(word[(dash + 1)..]);

        return word switch
        {
            "one" => "first",
            "two" => "second",
            "three" => "third",
            "five" => "fifth",
            "eight" => "eighth",
            "nine" => "ninth",
            "twelve" => "twelfth",
            _ when word.EndsWith('y') => word[..^1] + "ieth",
            _ => word + "th",
        };
    }

    // ---------------------------------------------------------------- 年份

    /// <summary>
    /// 年份读法（1000~9999）：1998 → nineteen ninety-eight、1800 → eighteen hundred、
    /// 2005 → two thousand (and) five、2024 → twenty twenty-four、1905 → nineteen oh-five。
    /// 超出范围返回 null。
    /// </summary>
    public static string? FormatYear(int year, EnglishStyle? style = null)
    {
        style ??= EnglishStyle.Default;
        if (year is < 1000 or > 9999) return null;

        var high = year / 100;
        var low = year % 100;

        // 2000~2099：2000~2009 念 two thousand (and) five；2010 起念 twenty twenty-four
        if (high == 20)
        {
            if (low == 0) return "two thousand";
            return low < 10
                ? "two thousand" + (style.UseAnd ? " and " : " ") + GroupText(low, style)
                : "twenty " + GroupText(low, style);
        }

        // 1100 / 1800 / 1900：整百年念 …hundred
        if (low == 0) return GroupText(high, style) + " hundred";

        // 1905：nineteen oh-five（中间那个 0 念 oh）
        if (low < 10) return GroupText(high, style) + " oh-" + Units[low];

        return GroupText(high, style) + " " + GroupText(low, style);
    }

    // ---------------------------------------------------------------- 金额 / 支票

    /// <summary>
    /// 金额读法（四舍五入到分）：12.34 → twelve dollars and thirty-four cents；
    /// 整元 → twelve dollars；只有零钱 → thirty-four cents；单数 → one dollar / one cent。
    /// </summary>
    public static string? FormatMoney(
        ExactDecimal value, EnglishStyle style, string singular = "dollar", string plural = "dollars",
        string centSingular = "cent", string centPlural = "cents")
    {
        var (negative, whole, jiao, fen) = MoneyParts.Split(value);
        var part = jiao * 10 + fen;

        var sb = new StringBuilder();
        if (negative) sb.Append("minus ");

        if (!whole.IsZero || part == 0)
        {
            var wholeText = FormatCardinal(whole, style);
            if (wholeText is null) return null;
            sb.Append(wholeText).Append(' ').Append(whole.IsOne ? singular : plural);
            if (part > 0) sb.Append(" and ");
        }

        if (part > 0)
        {
            var partText = GroupText(part, style);
            sb.Append(partText).Append(' ').Append(part == 1 ? centSingular : centPlural);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 英文支票写法（美式，数字部分不带 and）：12.34 → twelve and 34/100；
    /// 整元 → twelve and 00/100。末尾可带货币名（美元的支票上通常已经印好 Dollars）。
    /// </summary>
    public static string? FormatCheque(ExactDecimal value, EnglishStyle style, string currency = "dollars")
    {
        var (negative, whole, jiao, fen) = MoneyParts.Split(value);
        var part = jiao * 10 + fen;

        var wholeText = FormatCardinal(whole, style with { UseAnd = false, UseA = false });
        if (wholeText is null) return null;

        var body = wholeText + " and " + part.ToString("00", CultureInfo.InvariantCulture) + "/100";
        if (!string.IsNullOrEmpty(currency)) body += " " + currency;
        return negative ? "minus " + body : body;
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>0~999 的英文：123 → one hundred and twenty-three。</summary>
    private static string GroupText(int group, EnglishStyle style)
    {
        if (group <= 0) return Units[0];

        var sb = new StringBuilder();
        var hundreds = group / 100;
        var rest = group % 100;

        if (hundreds > 0)
        {
            sb.Append(Units[hundreds]).Append(" hundred");
            if (rest > 0 && style.UseAnd) sb.Append(" and ");
        }
        if (rest > 0)
        {
            if (sb.Length > 0 && !style.UseAnd) sb.Append(' ');
            sb.Append(TensText(rest, style));
        }
        return sb.ToString();
    }

    /// <summary>1~99 的英文（含十位个位之间的连字符）。</summary>
    private static string TensText(int value, EnglishStyle style)
    {
        if (value < 20) return Units[value];

        var tens = Tens[value / 10];
        var unit = value % 10;
        if (unit == 0) return tens;
        return tens + (style.Hyphenate ? "-" : " ") + Units[unit];
    }
}
