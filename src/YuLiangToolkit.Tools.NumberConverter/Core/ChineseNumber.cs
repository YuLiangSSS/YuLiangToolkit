using System.Globalization;
using System.Numerics;
using System.Text;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.NumberConverter.Core;

/// <summary>
/// 中文数字读法：小写（一百二十三）/ 大写（壹佰贰拾叁）/ 逐位读（一二三）/ 序数（第一百二十三）/
/// 人民币金额大写（壹拾贰圆叁角肆分），以及反向解析（把「壹拾贰圆叁角肆分」读回 12.34）。
///
/// <para>按「每 4 位一节」处理，节单位依次是 万 / 亿 / 兆 / 京 / 垓 / 秭 / 穰 / 沟 / 涧 / 正 / 载
/// （最多 48 位），再大就返回 null —— 让界面提示「超出中文读法范围」，而不是硬编一个没人认得的单位。</para>
///
/// <para>纯逻辑、不碰界面 / 存储，可直接被自检断言。</para>
/// </summary>
public static class ChineseNumber
{
    /// <summary>小写数字（也用于小数位的逐位读）。</summary>
    public const string LowerDigits = "零一二三四五六七八九";

    /// <summary>大写数字（财务用）。</summary>
    public const string UpperDigits = "零壹贰叁肆伍陆柒捌玖";

    /// <summary>最多支持多少节（每节 4 位）：万 / 亿 / 万亿 / 亿亿 / 万亿亿 / 亿亿亿，合计 24 位。</summary>
    private const int MaxSections = 6;

    /// <summary>
    /// 节的单位名。中文大数按「万 / 亿」循环叠用（万亿 = 10¹²、亿亿 = 10¹⁶），
    /// 不用「兆 / 京」—— 大陆口语里「兆」常被当成 10⁶（兆瓦 · 兆像素），用在金额上会读错。
    /// </summary>
    private static string SectionUnit(int index)
    {
        if (index <= 0) return "";

        // index 2k → k 个「亿」；index 2k+1 → 「万」+ k 个「亿」（万亿 / 万亿亿）
        var sb = new StringBuilder();
        if (index % 2 == 1) sb.Append('万');
        for (var i = 0; i < index / 2; i++) sb.Append('亿');
        return sb.ToString();
    }

    /// <summary>节内单位：索引 = 位数（0 = 个位）。</summary>
    private static readonly string[] LowerUnits = { "", "十", "百", "千" };

    private static readonly string[] UpperUnits = { "", "拾", "佰", "仟" };

    /// <summary>中文读法最大支持多少位（6 节 × 4 位 = 24 位）。</summary>
    public static int MaxDigits => MaxSections * 4;

    // ---------------------------------------------------------------- 正向

    /// <summary>
    /// 整数读法。<paramref name="upper"/> = true 用大写（壹佰贰拾叁），false 用小写（一百二十三）。
    /// 小写默认把开头的「一十」简化成「十」（12 → 十二、100000 → 十万）；
    /// 大写、以及 <paramref name="dropLeadingOne"/> = false 时保留「壹拾 / 一十」（财务写法）。
    /// 数值超出 <see cref="MaxDigits"/> 位时返回 null。
    /// </summary>
    public static string? FormatInteger(BigInteger value, bool upper, bool dropLeadingOne = true)
    {
        var digits = upper ? UpperDigits : LowerDigits;
        var units = upper ? UpperUnits : LowerUnits;

        if (value.IsZero) return digits[0].ToString();

        var sections = new List<int>();
        var rest = BigInteger.Abs(value);
        while (!rest.IsZero)
        {
            sections.Add((int)(rest % 10000));
            rest /= 10000;
        }
        if (sections.Count > MaxSections) return null;

        var sb = new StringBuilder();
        var zeroPending = false;
        for (var i = sections.Count - 1; i >= 0; i--)
        {
            var section = sections[i];
            if (section == 0)
            {
                zeroPending = true;
                continue;
            }

            // 节之间补零：中间空过一节，或者这一节不足 4 位（前导 0 要读一个零）
            if (sb.Length > 0 && (zeroPending || section < 1000)) sb.Append(digits[0]);
            AppendSection(sb, section, digits, units);
            sb.Append(SectionUnit(i));
            zeroPending = false;
        }

        // 小写口语省略开头的「一」：一十二 → 十二、一十万 → 十万（大写 / 财务写法保留「壹拾」）
        if (!upper && dropLeadingOne && sb.Length >= 2 && sb[0] == digits[1] && sb[1] == units[1][0])
            sb.Remove(0, 1);

        return sb.ToString();
    }

    /// <summary>整数读法（带负号：负一百二十三）。</summary>
    public static string? FormatIntegerSigned(BigInteger value, bool upper)
    {
        var body = FormatInteger(BigInteger.Abs(value), upper);
        if (body is null) return null;
        return value.Sign < 0 ? "负" + body : body;
    }

    /// <summary>小数读法：整数部分按 <see cref="FormatInteger"/>，小数部分逐位读（3.14 → 三点一四）。</summary>
    public static string? FormatDecimal(ExactDecimal value, bool upper)
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

        var digits = upper ? UpperDigits : LowerDigits;
        var body = FormatInteger(integer, upper);
        if (body is null) return null;

        var sb = new StringBuilder();
        if (negative) sb.Append('负');
        sb.Append(body);
        if (fractionText.Length > 0)
        {
            sb.Append('点');
            foreach (var ch in fractionText)
            {
                if (ch is < '0' or > '9') return null;
                sb.Append(digits[ch - '0']);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// 逐位读（念号码用）：1203 → 一二零三，-0.5 → 负零点五。
    /// 负号读「负」、小数点读「点」，其余字符必须是数字。
    /// </summary>
    public static string? FormatDigits(string text, bool upper)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var digits = upper ? UpperDigits : LowerDigits;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (ch is >= '0' and <= '9') sb.Append(digits[ch - '0']);
            else if (ch == '.') sb.Append('点');
            else if (ch == '-') sb.Append('负');
            else if (ch == '+') continue;
            else if (ch == ',' || ch == ' ') continue;
            else return null;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>中文序数：第一百二十三（小数照样接在后面）。</summary>
    public static string? FormatOrdinal(ExactDecimal value, bool upper)
    {
        var body = FormatDecimal(value, upper);
        return body is null ? null : "第" + body;
    }

    // ---------------------------------------------------------------- 金额大写

    /// <summary>
    /// 人民币金额大写。<paramref name="currency"/> 是「圆」或「元」；
    /// <paramref name="zeroOnJiaoGap"/> = 角位为零而分位有值时补「零」（1680.05 → 壹仟陆佰捌拾圆零伍分）；
    /// <paramref name="wholeAfterJiao"/> = 角有值而分为零时写「整」（12.30 → 壹拾贰圆叁角整）。
    /// 金额四舍五入到「分」。
    /// </summary>
    public static string? FormatMoneyUpper(
        ExactDecimal value, string currency = "圆", bool zeroOnJiaoGap = true, bool wholeAfterJiao = true)
        => FormatMoneyCore(value, currency, true, zeroOnJiaoGap, wholeAfterJiao);

    /// <summary>
    /// 人民币金额小写（中文小写数字，不是阿拉伯数字）：1234.56 → 一千二百三十四圆五角六分。
    /// 规则与 <see cref="FormatMoneyUpper"/> 一致，只是把「壹贰叁」换成「一二三」。
    /// </summary>
    public static string? FormatMoneyLower(
        ExactDecimal value, string currency = "圆", bool zeroOnJiaoGap = true, bool wholeAfterJiao = true)
        => FormatMoneyCore(value, currency, false, zeroOnJiaoGap, wholeAfterJiao);

    private static string? FormatMoneyCore(
        ExactDecimal value, string currency, bool upper, bool zeroOnJiaoGap, bool wholeAfterJiao)
    {
        var (negative, yuan, jiao, fen) = MoneyParts.Split(value);
        var digits = upper ? UpperDigits : LowerDigits;

        var sb = new StringBuilder();
        if (negative) sb.Append('负');

        if (!yuan.IsZero)
        {
            // 金额：大写用财务写法、不省略开头的「一」（壹拾贰圆）；小写按口语读（十二元、十元）
            var yuanText = FormatInteger(yuan, upper, dropLeadingOne: !upper);
            if (yuanText is null) return null;
            sb.Append(yuanText).Append(currency);
        }

        if (jiao == 0 && fen == 0)
        {
            if (yuan.IsZero) sb.Append(digits[0]).Append(currency);
            sb.Append('整');
            return sb.ToString();
        }

        if (!yuan.IsZero && jiao == 0 && fen > 0 && zeroOnJiaoGap) sb.Append(digits[0]);
        if (jiao > 0) sb.Append(digits[jiao]).Append('角');
        if (fen > 0) sb.Append(digits[fen]).Append('分');
        else if (jiao > 0 && wholeAfterJiao) sb.Append('整');

        return sb.ToString();
    }

    // ---------------------------------------------------------------- 反向解析

    /// <summary>
    /// 把中文数字 / 金额文本读回数值：一百二十三 → 123、壹拾贰圆叁角肆分 → 12.34、
    /// 十二点零五 → 12.05。大小写数字可混用，阿拉伯数字也能混在中间（12圆3角4分 → 12.34）。
    /// 认不出时返回 false 并给一句中文原因（界面直接显示，不弹窗）。
    ///
    /// <para>大单位按「万进」老单位处理（万 10⁴ / 亿 10⁸ / 兆 10¹² / 京 10¹⁶ …），
    /// 降序（肆兆贰仟…亿…万…）和升序（一万亿 = 10¹²）两种写法都认。</para>
    /// </summary>
    public static bool TryParse(string? text, out ExactDecimal value, out string? error)
    {
        value = ExactDecimal.Zero;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请先输入要解析的内容";
            return false;
        }

        BigInteger total = 0;       // 已经结算到「大单位」的量
        BigInteger section = 0;     // 当前四位节里由 十 / 百 / 千 累加出来的值
        BigInteger number = 0;      // 当前正在拼的阿拉伯 / 中文数字
        var lastUnitExponent = -1;  // 上一个遇到的大单位的指数（判断升序还是降序）
        var fraction = new StringBuilder();
        var jiao = -1;
        var fen = -1;
        var negative = false;
        var sawDigit = false;
        var sawAnything = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            var digit = DigitOf(ch);
            if (digit >= 0)
            {
                number = number * 10 + digit;
                sawDigit = sawAnything = true;
                continue;
            }

            switch (ch)
            {
                case '十' or '拾':
                    section += (number.IsZero ? 1 : number) * 10;
                    number = 0;
                    sawAnything = true;
                    break;
                case '百' or '佰':
                    section += (number.IsZero ? 1 : number) * 100;
                    number = 0;
                    sawAnything = true;
                    break;
                case '千' or '仟':
                    section += (number.IsZero ? 1 : number) * 1000;
                    number = 0;
                    sawAnything = true;
                    break;
                case '万' or '萬' or '亿' or '億' or '兆' or '京' or '垓' or '秭' or '穰' or '沟' or '涧' or '正' or '载':
                {
                    var exponent = UnitExponent(ch);
                    var pending = section + number;

                    // 「一万亿」这种连写的大单位：后面紧跟更高的单位时，单位按乘积算（万 × 亿 = 10¹²）
                    while (i + 1 < text.Length && UnitExponent(text[i + 1]) > 0)
                    {
                        var next = UnitExponent(text[i + 1]);
                        exponent += next;
                        i++;
                    }

                    if (lastUnitExponent >= 0 && exponent > lastUnitExponent)
                    {
                        // 升序（一万亿 = 1 万 × 1 亿）：把已结算的量一起抬上去
                        total = (total + pending) * BigInteger.Pow(10, exponent);
                    }
                    else
                    {
                        total += pending * BigInteger.Pow(10, exponent);
                    }
                    lastUnitExponent = exponent;
                    section = 0;
                    number = 0;
                    sawAnything = true;
                    break;
                }
                case '圆' or '元' or '圓':
                    total += section + number;
                    section = 0;
                    number = 0;
                    lastUnitExponent = -1;
                    sawAnything = true;
                    break;
                case '角':
                    jiao = number < 10 ? (int)number : -1;
                    number = 0;
                    sawAnything = true;
                    break;
                case '分':
                    fen = number < 10 ? (int)number : -1;
                    number = 0;
                    sawAnything = true;
                    break;
                case '点' or '點':
                {
                    sawAnything = true;
                    for (var k = i + 1; k < text.Length; k++)
                    {
                        var fd = DigitOf(text[k]);
                        if (fd < 0) return Fail("小数点后面只能是数字", out value, out error);
                        fraction.Append((char)('0' + fd));
                    }
                    if (fraction.Length == 0) return Fail("小数点后面缺少数字", out value, out error);
                    i = text.Length;   // 小数点吃掉了剩下的全部内容
                    break;
                }
                case '整':
                    sawAnything = true;
                    break;
                case '负':
                    negative = true;
                    sawAnything = true;
                    break;
                case ' ' or '\t' or ',' or '，' or '¥' or '￥':
                    // 千分位逗号、空格、货币符号都直接跳过
                    break;
                default:
                    return Fail($"看不懂「{ch}」这个字（只认中文数字、大小写金额和阿拉伯数字）", out value, out error);
            }
        }

        if (!sawAnything || (!sawDigit && total.IsZero && section.IsZero))
        {
            return Fail("里面没有数字", out value, out error);
        }

        total += section + number;

        var plain = total.ToString(CultureInfo.InvariantCulture);
        string fractionText;
        if (jiao >= 0 || fen >= 0)
            fractionText = (jiao < 0 ? 0 : jiao).ToString(CultureInfo.InvariantCulture)
                           + (fen < 0 ? 0 : fen).ToString(CultureInfo.InvariantCulture);
        else
            fractionText = fraction.ToString();

        if (fractionText.Length > 0) plain += "." + fractionText;
        if (negative) plain = "-" + plain;

        if (!ExactDecimal.TryParse(plain, out var parsed))
            return Fail("数值超出可转换的范围", out value, out error);

        value = parsed;
        return true;
    }

    /// <summary>大单位（万进）的指数：万 4、亿 8、兆 12、京 16…（每级差 4）。</summary>
    private static int UnitExponent(char ch) => ch switch
    {
        '万' or '萬' => 4,
        '亿' or '億' => 8,
        '兆' => 12,
        '京' => 16,
        '垓' => 20,
        '秭' => 24,
        '穰' => 28,
        '沟' => 32,
        '涧' => 36,
        '正' => 40,
        '载' => 44,
        _ => -1,
    };

    private static bool Fail(string message, out ExactDecimal value, out string? error)
    {
        value = ExactDecimal.Zero;
        error = message;
        return false;
    }

    /// <summary>中文（大小写）或阿拉伯数字 → 0~9；其它字符返回 -1。</summary>
    private static int DigitOf(char ch)
    {
        if (ch is >= '0' and <= '9') return ch - '0';
        var at = LowerDigits.IndexOf(ch);
        if (at >= 0) return at;
        at = UpperDigits.IndexOf(ch);
        return at >= 0 ? at : -1;
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>读一节（0~9999）的 千百十个，节内的连续零只读一个、末尾的零不读。</summary>
    private static void AppendSection(StringBuilder sb, int section, string digits, string[] units)
    {
        var started = false;
        var zeroPending = false;
        for (var pos = 3; pos >= 0; pos--)
        {
            var divisor = pos == 3 ? 1000 : pos == 2 ? 100 : pos == 1 ? 10 : 1;
            var d = section / divisor % 10;
            if (d == 0)
            {
                if (started) zeroPending = true;
                continue;
            }

            if (zeroPending)
            {
                sb.Append(digits[0]);
                zeroPending = false;
            }
            sb.Append(digits[d]);
            if (pos > 0) sb.Append(units[pos]);
            started = true;
        }
    }
}
