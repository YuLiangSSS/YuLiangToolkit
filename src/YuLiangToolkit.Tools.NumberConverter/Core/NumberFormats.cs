using System.Globalization;
using System.Numerics;
using System.Text;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.NumberConverter.Core;

/// <summary>
/// 生活中常见的其它数字写法：
/// 罗马数字（IV）、千分位（1,234,567）、中文金额式分组、科学计数法（1.23×10⁶）、
/// 二 / 八 / 十六进制（0b / 0o / 0x）、每 4 位分组（1234 5678，念银行卡号用）。
///
/// <para>纯逻辑、不碰界面，可直接被自检断言。</para>
/// </summary>
public static class NumberFormats
{
    // ---------------------------------------------------------------- 罗马数字

    private static readonly (int Value, string Symbol)[] RomanTable =
    {
        (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
        (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
        (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
    };

    /// <summary>罗马数字能表示的上下限（罗马人只到 3999；这里放到 0 和负数上就不管了）。</summary>
    public const int RomanMax = 3999;

    /// <summary>整数转罗马数字（1~3999）；超出范围或非正数返回 null。</summary>
    public static string? ToRoman(BigInteger value)
    {
        if (value < 1 || value > RomanMax) return null;

        var rest = (int)value;
        var sb = new StringBuilder();
        foreach (var (unit, symbol) in RomanTable)
        {
            while (rest >= unit)
            {
                sb.Append(symbol);
                rest -= unit;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 罗马数字转整数（严格校验：只认规范写法，IIII / VX 这种会被拒）。
    /// 成功返回 true；失败时 <paramref name="error"/> 说明原因。
    /// </summary>
    public static bool TryParseRoman(string? text, out int value, out string? error)
    {
        value = 0;
        error = null;
        var s = (text ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0)
        {
            error = "请输入罗马数字（I / V / X / L / C / D / M）";
            return false;
        }

        // 规范写法的正则不好维护，直接用「贪心减」再回写比对：能减完且回写的文本与原文本一致才算合法
        var rest = s;
        var total = 0;
        foreach (var (unit, symbol) in RomanTable)
        {
            while (rest.StartsWith(symbol, StringComparison.Ordinal))
            {
                total += unit;
                rest = rest[symbol.Length..];
            }
        }

        if (rest.Length > 0)
        {
            error = $"看不懂「{rest}」这一段（只认 I V X L C D M）";
            return false;
        }
        if (total is < 1 or > RomanMax)
        {
            error = $"罗马数字只支持 1 ~ {RomanMax}";
            return false;
        }
        if (ToRoman(total) != s)
        {
            error = $"「{s}」不是规范的罗马数字写法（规范写法是 {ToRoman(total)}）";
            return false;
        }

        value = total;
        return true;
    }

    // ---------------------------------------------------------------- 分组

    /// <summary>
    /// 千分位分组：整数部分每 3 位一个逗号（1234567 → 1,234,567），小数部分不动。
    /// 也接受已经带逗号 / 空格的输入（重复分组不会出错）。
    /// </summary>
    public static string GroupThousands(string? text, string separator = ",")
    {
        var (negative, integer, fraction) = SplitSignAndDigits(text);
        var sb = new StringBuilder();
        if (negative) sb.Append('-');

        for (var i = 0; i < integer.Length; i++)
        {
            if (i > 0 && (integer.Length - i) % 3 == 0) sb.Append(separator);
            sb.Append(integer[i]);
        }
        if (fraction.Length > 0) sb.Append('.').Append(fraction);
        return sb.ToString();
    }

    /// <summary>每 4 位一组（12345678 → 1234 5678，念银行卡 / 手机号时看得清）。</summary>
    public static string GroupBy(string? text, int size, string separator = " ")
    {
        size = Math.Clamp(size, 1, 8);
        var (negative, integer, fraction) = SplitSignAndDigits(text);

        var sb = new StringBuilder();
        if (negative) sb.Append('-');

        // 从左往右，第一组可以不满
        var lead = integer.Length % size;
        if (lead == 0) lead = size;
        sb.Append(integer[..lead]);
        for (var i = lead; i < integer.Length; i += size)
        {
            sb.Append(separator).Append(integer.AsSpan(i, Math.Min(size, integer.Length - i)));
        }
        if (fraction.Length > 0) sb.Append('.').Append(fraction);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 进制

    /// <summary>进制选项（界面下拉框；2 / 8 / 16 / 10；36 也能用，但界面只放常用的）。</summary>
    public static IReadOnlyList<int> BaseOptions { get; } = new[] { 2, 8, 10, 16, 36 };

    /// <summary>进制前缀（写给人看的习惯写法）。</summary>
    public static string BasePrefix(int radix) => radix switch
    {
        2 => "0b",
        8 => "0o",
        16 => "0x",
        _ => "",
    };

    /// <summary>
    /// 非负整数转任意进制（2~36）。数字用 0-9A-Z，大写；负号照原样带上。
    /// 超过 36 进制或数值过大（转换后超过 20 万字符）返回 null。
    /// </summary>
    public static string? ToBase(BigInteger value, int radix, bool upper = true, bool withPrefix = true)
    {
        if (radix is < 2 or > 36) return null;
        if (value.IsZero) return withPrefix ? BasePrefix(radix) + "0" : "0";

        var negative = value.Sign < 0;
        var rest = BigInteger.Abs(value);
        var big = new BigInteger(radix);
        var chars = new List<char>();

        while (!rest.IsZero)
        {
            rest = BigInteger.DivRem(rest, big, out var digit);
            var d = (int)digit;
            var ch = d < 10 ? (char)('0' + d) : (char)((upper ? 'A' : 'a') + d - 10);
            chars.Add(ch);
            if (chars.Count > 200_000) return null;
        }
        chars.Reverse();

        var body = new string(chars.ToArray());
        if (withPrefix) body = BasePrefix(radix) + body;
        return negative ? "-" + body : body;
    }

    /// <summary>
    /// 按前缀 / 进制解析：0x1F → 31、0b1010 → 10、0o17 → 15、123 → 123。
    /// 认不出返回 false + 中文原因。
    /// </summary>
    public static bool TryParseBase(string? text, int radix, out BigInteger value, out string? error)
    {
        value = BigInteger.Zero;
        error = null;

        var s = (text ?? "").Trim().Replace("_", "").Replace(" ", "");
        if (s.Length == 0)
        {
            error = "请输入要转换的数字";
            return false;
        }

        var negative = false;
        if (s[0] is '+' or '-')
        {
            negative = s[0] == '-';
            s = s[1..];
        }

        // 前缀优先决定进制（0x / 0b / 0o），没有前缀就用界面上选的
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { radix = 16; s = s[2..]; }
        else if (s.StartsWith("0b", StringComparison.OrdinalIgnoreCase)) { radix = 2; s = s[2..]; }
        else if (s.StartsWith("0o", StringComparison.OrdinalIgnoreCase)) { radix = 8; s = s[2..]; }

        if (radix is < 2 or > 36)
        {
            error = "进制必须在 2 ~ 36 之间";
            return false;
        }
        if (s.Length == 0)
        {
            error = "前缀后面没有数字";
            return false;
        }
        if (s.Length > 200_000)
        {
            error = "数字太长，换个短点的再来";
            return false;
        }

        var result = BigInteger.Zero;
        var big = new BigInteger(radix);
        foreach (var ch in s)
        {
            var d = DigitOfBase(ch);
            if (d < 0 || d >= radix)
            {
                error = $"「{ch}」不是 {radix} 进制里的数字（{DescribeRadix(radix)}）";
                return false;
            }
            result = result * big + d;
        }

        value = negative ? -result : result;
        return true;
    }

    /// <summary>字符在 36 进制里的数值（0-9 / A-Z / a-z）；不认识的字符返回 -1。</summary>
    private static int DigitOfBase(char ch)
    {
        if (ch is >= '0' and <= '9') return ch - '0';
        if (ch is >= 'A' and <= 'Z') return ch - 'A' + 10;
        if (ch is >= 'a' and <= 'z') return ch - 'a' + 10;
        return -1;
    }

    private static string DescribeRadix(int radix) => radix switch
    {
        2 => "只能有 0 和 1",
        8 => "只能有 0 ~ 7",
        16 => "只能有 0 ~ 9 和 A ~ F",
        _ => $"只能用到不超过 {Math.Min(radix - 1, 35)} 的数字 / 字母",
    };

    // ---------------------------------------------------------------- 科学计数法

    /// <summary>
    /// 科学计数法：1230000 → 1.23×10⁶（<paramref name="unicodeExponent"/> = false 时写成 1.23e6）。
    /// 0 直接给 0。
    /// </summary>
    public static string? ToScientific(string? text, bool unicodeExponent = true, int maxSignificandDecimals = 15)
    {
        if (!ExactDecimal.TryParse(text, out var value)) return null;
        if (value.IsZero) return "0";

        var plain = value.ToPlainString();
        if (plain.Length > 4000) return null;   // 防手滑输一长串数字把界面卡住

        var negative = plain.StartsWith('-');
        if (negative) plain = plain[1..];

        var dot = plain.IndexOf('.');
        var integerPart = dot < 0 ? plain : plain[..dot];
        var fractionPart = dot < 0 ? "" : plain[(dot + 1)..];

        // 指数 = 第一个非零数字所在的位置：整数部分有 n 位有效数字时是 n-1；
        // 整数部分是 0（如 0.000123）时是「小数里第一个非零数字前面有几个 0」的相反数
        int exponent;
        var allDigits = integerPart + fractionPart;
        var firstNonZero = allDigits.IndexOfAny("123456789".ToCharArray());
        if (firstNonZero < 0) return "0";

        exponent = integerPart.TrimStart('0').Length > 0
            ? integerPart.TrimStart('0').Length - 1
            : -(firstNonZero - integerPart.Length + 1);

        var significant = allDigits[firstNonZero..].TrimEnd('0');

        var significand = new StringBuilder();
        significand.Append(significant[0]);
        var rest = significant[1..];
        if (rest.Length > 0)
        {
            if (rest.Length > maxSignificandDecimals) rest = rest[..maxSignificandDecimals];
            rest = rest.TrimEnd('0');
            if (rest.Length > 0) significand.Append('.').Append(rest);
        }

        var body = significand.ToString();
        if (negative) body = "-" + body;
        if (exponent == 0) return body;

        return unicodeExponent
            ? body + "×10" + ToSuperscript(exponent)
            : body + "e" + exponent.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>把指数写成上标（-6 → ⁻⁶）。</summary>
    private static string ToSuperscript(int exponent)
    {
        const string superscripts = "⁰¹²³⁴⁵⁶⁷⁸⁹";
        var text = Math.Abs(exponent).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        if (exponent < 0) sb.Append('⁻');
        foreach (var ch in text) sb.Append(superscripts[ch - '0']);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>去掉千分位 / 空格，拆成（负号, 整数位, 小数位）。非数字字符原样留在整数位里，由调用方决定怎么处理。</summary>
    private static (bool Negative, string Integer, string Fraction) SplitSignAndDigits(string? text)
    {
        var s = (text ?? "").Trim().Replace(",", "").Replace(" ", "").Replace("_", "");
        var negative = s.StartsWith('-');
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+')) s = s[1..];

        var dot = s.IndexOf('.');
        var integer = dot < 0 ? s : s[..dot];
        var fraction = dot < 0 ? "" : s[(dot + 1)..];
        if (integer.Length == 0) integer = "0";
        return (negative, integer, fraction);
    }
}
