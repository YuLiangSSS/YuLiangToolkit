using System.Numerics;
using System.Text.Json;
using YuLiangToolkit.Core.Numbers;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.NumberConverter;
using YuLiangToolkit.Tools.NumberConverter.Core;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「数字转换器」自检：中文数字（小写 / 大写 / 逐位 / 序数 / 中文反向解析）、
/// 人民币金额大小写（圆元 / 补零 / 整 / 零点）、英文（基数词 / 序数词 / 年份 / 金额 / 支票 / 逐位）、
/// 罗马数字双向、千分位 / 4 位分组 / 科学计数法 / 进制互转、整条转换流水线、配置默认值与夹取。
///
/// <para><b>全程纯逻辑</b>：不创建窗口、不碰剪贴板、不写真实 Config（配置往返只在内存里做 JSON 序列化）。</para>
/// </summary>
internal static class NumberConverterChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckChinese(fn);
        CheckChineseMoney(fn);
        CheckChineseParse(fn);
        CheckEnglish(fn);
        CheckRoman(fn);
        CheckFormats(fn);
        CheckPipeline(fn);
        CheckConfig(fn);

        report("[数字转换器] 中文大小写 / 人民币金额 / 英文念法 / 罗马·进制·千分位·科学计数 / 反向解析 / 配置", true);
    }

    // ---------------------------------------------------------------- 小工具

    private static ExactDecimal Dec(string text)
        => ExactDecimal.TryParse(text, out var value) ? value : ExactDecimal.Zero;

    private static string Chinese(object value, bool upper) => value switch
    {
        BigInteger big => ChineseNumber.FormatInteger(big, upper) ?? "<null>",
        _ => ChineseNumber.FormatInteger(BigInteger.Parse(value.ToString()!), upper) ?? "<null>",
    };

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new NumberConverterTool();
        check("[数字转换器] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "number-converter" && tool.Name == "数字转换器" && tool.EmojiIcon == "🔢"
            && tool.Category == "数学" && tool.Order == 516,
            $"{tool.Id} / {tool.Name} / {tool.EmojiIcon} / {tool.Category} / {tool.Order}");

        check("[数字转换器] 工具 Id 与 NumberConverterConfig.ToolId 一致（Data / Config 目录名）",
            tool.Id == NumberConverterConfig.ToolId, NumberConverterConfig.ToolId);

        check("[数字转换器] 描述点明中文大写 / 人民币金额 / 英文念法",
            tool.Description.Contains("大写") && tool.Description.Contains("人民币")
            && tool.Description.Contains("英文念法") && tool.Description.Contains("罗马"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 中文数字

    private static void CheckChinese(CheckFn check)
    {
        var cases = new (string Input, string Lower, string Upper)[]
        {
            ("0", "零", "零"),
            ("1", "一", "壹"),
            ("10", "十", "壹拾"),
            ("12", "十二", "壹拾贰"),
            ("19", "十九", "壹拾玖"),
            ("20", "二十", "贰拾"),
            ("100", "一百", "壹佰"),
            ("101", "一百零一", "壹佰零壹"),
            ("110", "一百一十", "壹佰壹拾"),
            ("111", "一百一十一", "壹佰壹拾壹"),
            ("1000", "一千", "壹仟"),
            ("1001", "一千零一", "壹仟零壹"),
            ("1010", "一千零一十", "壹仟零壹拾"),
            ("1100", "一千一百", "壹仟壹佰"),
            ("1234", "一千二百三十四", "壹仟贰佰叁拾肆"),
            ("10000", "一万", "壹万"),
            ("10001", "一万零一", "壹万零壹"),
            ("12345", "一万二千三百四十五", "壹万贰仟叁佰肆拾伍"),
            ("100000000", "一亿", "壹亿"),
            ("100000001", "一亿零一", "壹亿零壹"),
            ("123456789", "一亿二千三百四十五万六千七百八十九", "壹亿贰仟叁佰肆拾伍万陆仟柒佰捌拾玖"),
            ("1000000000000", "一万亿", "壹万亿"),
        };

        var failures = cases
            .Where(c => Chinese(BigInteger.Parse(c.Input), false) != c.Lower
                        || Chinese(BigInteger.Parse(c.Input), true) != c.Upper)
            .Select(c => $"{c.Input}: {Chinese(BigInteger.Parse(c.Input), false)}/{Chinese(BigInteger.Parse(c.Input), true)}"
                         + $"(期望 {c.Lower}/{c.Upper})")
            .ToList();

        check("[数字转换器] 中文整数读法（小写 + 大写，覆盖 0 / 中间零 / 级间零 / 万·亿·万亿）",
            failures.Count == 0, string.Join(" | ", failures));

        check("[数字转换器] 小写省略开头的「一十」、大写保留「壹拾」（财务习惯）",
            ChineseNumber.FormatInteger(BigInteger.Parse("12"), false) == "十二"
            && ChineseNumber.FormatInteger(BigInteger.Parse("12"), true) == "壹拾贰"
            && ChineseNumber.FormatInteger(BigInteger.Parse("100000"), false) == "十万"
            && ChineseNumber.FormatInteger(BigInteger.Parse("100000"), true) == "壹拾万",
            $"{ChineseNumber.FormatInteger(BigInteger.Parse("12"), false)} / {ChineseNumber.FormatInteger(BigInteger.Parse("12"), true)}");

        check("[数字转换器] 负数读「负」",
            ChineseNumber.FormatIntegerSigned(BigInteger.Parse("-123"), false) == "负一百二十三",
            ChineseNumber.FormatIntegerSigned(BigInteger.Parse("-123"), false) ?? "<null>");

        check("[数字转换器] 小数部分逐位读（3.14 → 三点一四 / 叁点壹肆）",
            ChineseNumber.FormatDecimal(Dec("3.14"), false) == "三点一四"
            && ChineseNumber.FormatDecimal(Dec("3.14"), true) == "叁点壹肆"
            && ChineseNumber.FormatDecimal(Dec("0.05"), false) == "零点零五"
            && ChineseNumber.FormatDecimal(Dec("-12.5"), false) == "负十二点五",
            ChineseNumber.FormatDecimal(Dec("3.14"), false) ?? "<null>");

        check("[数字转换器] 逐位读（1203 → 一二零三，-0.5 → 负零点五）",
            ChineseNumber.FormatDigits("1203", false) == "一二零三"
            && ChineseNumber.FormatDigits("-0.5", false) == "负零点五"
            && ChineseNumber.FormatDigits("1,234", false) == "一二三四",
            ChineseNumber.FormatDigits("1203", false) ?? "<null>");

        check("[数字转换器] 中文序数带「第」",
            ChineseNumber.FormatOrdinal(Dec("123"), false) == "第一百二十三"
            && ChineseNumber.FormatOrdinal(Dec("12"), true) == "第壹拾贰",
            ChineseNumber.FormatOrdinal(Dec("123"), false) ?? "<null>");

        check("[数字转换器] 超出中文读法位数返回 null（不硬编一个没人认得的单位）",
            ChineseNumber.FormatInteger(BigInteger.Pow(10, ChineseNumber.MaxDigits - 1), false) is not null
            && ChineseNumber.FormatInteger(BigInteger.Pow(10, ChineseNumber.MaxDigits), false) is null,
            $"{ChineseNumber.MaxDigits} 位");
    }

    // ---------------------------------------------------------------- 人民币金额

    private static void CheckChineseMoney(CheckFn check)
    {
        var cases = new (string Input, string Upper, string Lower)[]
        {
            ("0", "零圆整", "零圆整"),
            ("1", "壹圆整", "一圆整"),
            ("10", "壹拾圆整", "十圆整"),
            ("12", "壹拾贰圆整", "十二圆整"),
            ("1234", "壹仟贰佰叁拾肆圆整", "一千二百三十四圆整"),
            ("1234.56", "壹仟贰佰叁拾肆圆伍角陆分", "一千二百三十四圆五角六分"),
            ("1680.05", "壹仟陆佰捌拾圆零伍分", "一千六百八十圆零五分"),
            ("1680.32", "壹仟陆佰捌拾圆叁角贰分", "一千六百八十圆三角二分"),
            ("12.30", "壹拾贰圆叁角整", "十二圆三角整"),
            ("0.5", "伍角整", "五角整"),
            ("0.05", "伍分", "五分"),
            ("0.55", "伍角伍分", "五角五分"),
            ("100.10", "壹佰圆壹角整", "一百圆一角整"),
        };

        var failures = cases
            .Where(c => ChineseNumber.FormatMoneyUpper(Dec(c.Input)) != c.Upper
                        || ChineseNumber.FormatMoneyLower(Dec(c.Input)) != c.Lower)
            .Select(c => $"{c.Input}: {ChineseNumber.FormatMoneyUpper(Dec(c.Input))}/{ChineseNumber.FormatMoneyLower(Dec(c.Input))}"
                         + $"(期望 {c.Upper}/{c.Lower})")
            .ToList();

        check("[数字转换器] 人民币金额大写 / 小写（整 / 角分 / 补零 / 不足一元）",
            failures.Count == 0, string.Join(" | ", failures));

        check("[数字转换器] 金额单位可切「圆 / 元」",
            ChineseNumber.FormatMoneyUpper(Dec("12.34"), "元") == "壹拾贰元叁角肆分"
            && ChineseNumber.FormatMoneyUpper(Dec("12.34"), "圆") == "壹拾贰圆叁角肆分",
            ChineseNumber.FormatMoneyUpper(Dec("12.34"), "元") ?? "<null>");

        check("[数字转换器] 关掉「补零」与「整」两个口味后按开关走",
            ChineseNumber.FormatMoneyUpper(Dec("1680.05"), "圆", zeroOnJiaoGap: false) == "壹仟陆佰捌拾圆伍分"
            && ChineseNumber.FormatMoneyUpper(Dec("12.30"), "圆", wholeAfterJiao: false) == "壹拾贰圆叁角",
            ChineseNumber.FormatMoneyUpper(Dec("1680.05"), "圆", zeroOnJiaoGap: false) ?? "<null>");

        check("[数字转换器] 金额四舍五入到分（12.345 → 12.35 的读法）",
            ChineseNumber.FormatMoneyUpper(Dec("12.345")) == "壹拾贰圆叁角伍分"
            && ChineseNumber.FormatMoneyUpper(Dec("0.004")) == "零圆整",
            ChineseNumber.FormatMoneyUpper(Dec("12.345")) ?? "<null>");

        check("[数字转换器] 负数金额带「负」",
            ChineseNumber.FormatMoneyUpper(Dec("-12.34")) == "负壹拾贰圆叁角肆分",
            ChineseNumber.FormatMoneyUpper(Dec("-12.34")) ?? "<null>");
    }

    // ---------------------------------------------------------------- 中文反向解析

    private static void CheckChineseParse(CheckFn check)
    {
        var cases = new (string Text, string Expected)[]
        {
            ("一百二十三", "123"),
            ("十二", "12"),
            ("十", "10"),
            ("一千零一", "1001"),
            ("一亿二千三百四十五万六千七百八十九", "123456789"),
            ("壹拾贰圆叁角肆分", "12.34"),
            ("壹仟陆佰捌拾圆零伍分", "1680.05"),
            ("壹拾贰圆整", "12"),
            ("伍角", "0.5"),
            ("伍分", "0.05"),
            ("十二点零五", "12.05"),
            ("负一百二十三", "-123"),
            ("12圆3角4分", "12.34"),
            ("壹佰贰拾叁", "123"),
        };

        var failures = cases
            .Where(c => !ChineseNumber.TryParse(c.Text, out var parsed, out _)
                        || parsed.ToPlainString() != c.Expected)
            .Select(c => $"{c.Text} → {(ChineseNumber.TryParse(c.Text, out var p, out var e) ? p.ToPlainString() : "失败:" + e)}"
                         + $"(期望 {c.Expected})")
            .ToList();

        check("[数字转换器] 中文 / 金额文本反向解析（含大小写混用与阿拉伯数字混用）",
            failures.Count == 0, string.Join(" | ", failures));

        var bad1 = ChineseNumber.TryParse("abc", out _, out var error1);
        var bad2 = ChineseNumber.TryParse("", out _, out var error2);
        var bad3 = ChineseNumber.TryParse("三点", out _, out var error3);

        check("[数字转换器] 反向解析对非法输入给中文原因、不抛异常",
            !bad1 && !string.IsNullOrEmpty(error1)
            && !bad2 && !string.IsNullOrEmpty(error2)
            && !bad3 && !string.IsNullOrEmpty(error3),
            $"{error1} / {error2} / {error3}");

        check("[数字转换器] 正反向互相对上（1000 个随机整数往返一致）",
            RoundTripChinese(1000, out var firstBad), firstBad);
    }

    /// <summary>随机整数 → 中文大写 → 解析回来，必须一模一样。</summary>
    private static bool RoundTripChinese(int count, out string detail)
    {
        var random = new Random(20260420);
        for (var i = 0; i < count; i++)
        {
            var value = (BigInteger)random.NextInt64(0, 100_000_000_000_000);
            var text = ChineseNumber.FormatInteger(value, true);
            if (text is null || !ChineseNumber.TryParse(text, out var back, out _)
                || back.ToPlainString() != value.ToString())
            {
                detail = $"{value} → {text} → {(ChineseNumber.TryParse(text!, out var b, out _) ? b.ToPlainString() : "失败")}";
                return false;
            }
        }
        detail = $"{count} 个往返全部一致";
        return true;
    }

    // ---------------------------------------------------------------- 英文

    private static void CheckEnglish(CheckFn check)
    {
        var style = EnglishStyle.Default;

        var cases = new (string Input, string Expected)[]
        {
            ("0", "zero"),
            ("1", "one"),
            ("12", "twelve"),
            ("20", "twenty"),
            ("23", "twenty-three"),
            ("100", "one hundred"),
            ("123", "one hundred and twenty-three"),
            ("1000", "a thousand"),
            ("1001", "a thousand and one"),
            ("1010", "a thousand and ten"),
            ("1100", "a thousand one hundred"),
            ("1234", "a thousand two hundred and thirty-four"),
            ("10000", "ten thousand"),
            ("1000000", "a million"),
            ("1000000000", "a billion"),
            ("123456789", "one hundred and twenty-three million four hundred and fifty-six thousand seven hundred and eighty-nine"),
        };

        var failures = cases
            .Where(c => EnglishNumber.FormatCardinal(BigInteger.Parse(c.Input), style) != c.Expected)
            .Select(c => $"{c.Input}: {EnglishNumber.FormatCardinal(BigInteger.Parse(c.Input), style)}(期望 {c.Expected})")
            .ToList();

        check("[数字转换器] 英文基数词（a thousand / 英式 and / 连字符 / 百万级）",
            failures.Count == 0, string.Join(" | ", failures));

        check("[数字转换器] 关掉 a / and / 连字符后变美式写法",
            EnglishNumber.FormatCardinal(BigInteger.Parse("1000"),
                new EnglishStyle { UseA = false, UseAnd = false, Hyphenate = false }) == "one thousand"
            && EnglishNumber.FormatCardinal(BigInteger.Parse("123"),
                new EnglishStyle { UseA = true, UseAnd = false, Hyphenate = false }) == "one hundred twenty three",
            EnglishNumber.FormatCardinal(BigInteger.Parse("123"),
                new EnglishStyle { UseAnd = false, Hyphenate = false }) ?? "<null>");

        check("[数字转换器] 英文负数 / 小数 / 逐位读",
            EnglishNumber.FormatCardinal(BigInteger.Parse("-42"), style) == "minus forty-two"
            && EnglishNumber.FormatDecimal(Dec("3.14"), style) == "three point one four"
            && EnglishNumber.FormatDigits("1203", style) == "one two zero three"
            && EnglishNumber.FormatDigits("-0.5", style) == "minus zero point five",
            EnglishNumber.FormatDecimal(Dec("3.14"), style) ?? "<null>");

        check("[数字转换器] 英文序数词（first / second / third / twentieth / hundredth）",
            EnglishNumber.FormatOrdinal(BigInteger.Parse("1"), style) == "first"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("2"), style) == "second"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("3"), style) == "third"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("12"), style) == "twelfth"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("20"), style) == "twentieth"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("23"), style) == "twenty-third"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("100"), style) == "one hundredth"
            && EnglishNumber.FormatOrdinal(BigInteger.Parse("123"), style) == "one hundred and twenty-third",
            EnglishNumber.FormatOrdinal(BigInteger.Parse("123"), style) ?? "<null>");

        check("[数字转换器] 年份读法（1998 / 1800 / 2005 / 2024 / 1900）",
            EnglishNumber.FormatYear(1998) == "nineteen ninety-eight"
            && EnglishNumber.FormatYear(1800) == "eighteen hundred"
            && EnglishNumber.FormatYear(2005) == "two thousand and five"
            && EnglishNumber.FormatYear(2024) == "twenty twenty-four"
            && EnglishNumber.FormatYear(1900) == "nineteen hundred"
            && EnglishNumber.FormatYear(1905) == "nineteen oh-five",
            $"{EnglishNumber.FormatYear(1998)} / {EnglishNumber.FormatYear(2005)}");

        check("[数字转换器] 年份超出 1000~9999 返回 null",
            EnglishNumber.FormatYear(999) is null && EnglishNumber.FormatYear(10000) is null,
            "");

        check("[数字转换器] 英文金额（dollar / cent 单复数 + and）",
            EnglishNumber.FormatMoney(Dec("12.34"), style) == "twelve dollars and thirty-four cents"
            && EnglishNumber.FormatMoney(Dec("1.01"), style) == "one dollar and one cent"
            && EnglishNumber.FormatMoney(Dec("12"), style) == "twelve dollars"
            && EnglishNumber.FormatMoney(Dec("0.34"), style) == "thirty-four cents",
            EnglishNumber.FormatMoney(Dec("12.34"), style) ?? "<null>");

        check("[数字转换器] 英文支票写法（12.34 → twelve and 34/100 dollars）",
            EnglishNumber.FormatCheque(Dec("12.34"), style) == "twelve and 34/100 dollars"
            && EnglishNumber.FormatCheque(Dec("1234"), style) == "one thousand two hundred thirty-four and 00/100 dollars"
            && EnglishNumber.FormatCheque(Dec("0.05"), style) == "zero and 05/100 dollars",
            EnglishNumber.FormatCheque(Dec("12.34"), style) ?? "<null>");

        check("[数字转换器] 英文超过 36 位返回 null",
            EnglishNumber.FormatCardinal(BigInteger.Pow(10, 36), style) is null
            && EnglishNumber.FormatCardinal(BigInteger.Pow(10, 35), style) is not null,
            $"{EnglishNumber.MaxDigits} 位");
    }

    // ---------------------------------------------------------------- 罗马数字

    private static void CheckRoman(CheckFn check)
    {
        var cases = new (int Value, string Roman)[]
        {
            (1, "I"), (4, "IV"), (9, "IX"), (14, "XIV"), (40, "XL"), (90, "XC"),
            (400, "CD"), (900, "CM"), (1994, "MCMXCIV"), (2024, "MMXXIV"), (3999, "MMMCMXCIX"),
        };

        var failures = cases
            .Where(c => NumberFormats.ToRoman(c.Value) != c.Roman)
            .Select(c => $"{c.Value}: {NumberFormats.ToRoman(c.Value)}(期望 {c.Roman})")
            .ToList();

        check("[数字转换器] 整数 → 罗马数字（含 IV / IX / XL / CD / CM 减法写法）",
            failures.Count == 0, string.Join(" | ", failures));

        check("[数字转换器] 罗马数字只支持 1 ~ 3999",
            NumberFormats.ToRoman(0) is null && NumberFormats.ToRoman(-1) is null && NumberFormats.ToRoman(4000) is null,
            "");

        check("[数字转换器] 罗马数字 → 整数（含大小写与空格）",
            NumberFormats.TryParseRoman("mcmxciv", out var parsed, out _) && parsed == 1994
            && NumberFormats.TryParseRoman("  MMXXIV ", out var parsed2, out _) && parsed2 == 2024,
            parsed.ToString());

        var ok1 = NumberFormats.TryParseRoman("IIII", out _, out var error1);
        var ok2 = NumberFormats.TryParseRoman("VX", out _, out var error2);
        var ok3 = NumberFormats.TryParseRoman("ABC", out _, out var error3);
        var ok4 = NumberFormats.TryParseRoman("", out _, out var error4);

        check("[数字转换器] 非规范罗马写法被拒并提示规范写法",
            !ok1 && error1!.Contains("IV") && !ok2 && !string.IsNullOrEmpty(error2)
            && !ok3 && !string.IsNullOrEmpty(error3) && !ok4 && error4 is not null,
            $"{error1} / {error2}");
    }

    // ---------------------------------------------------------------- 其它写法

    private static void CheckFormats(CheckFn check)
    {
        check("[数字转换器] 千分位（1234567 → 1,234,567，小数部分不动）",
            NumberFormats.GroupThousands("1234567") == "1,234,567"
            && NumberFormats.GroupThousands("1234.5678") == "1,234.5678"
            && NumberFormats.GroupThousands("-999") == "-999"
            && NumberFormats.GroupThousands("1000") == "1,000"
            && NumberFormats.GroupThousands("1,234,567") == "1,234,567",
            NumberFormats.GroupThousands("1234567"));

        check("[数字转换器] 每 4 位分组（念银行卡号 / 手机号）",
            NumberFormats.GroupBy("123456789012", 4) == "1234 5678 9012"
            && NumberFormats.GroupBy("12345678", 4) == "1234 5678"
            && NumberFormats.GroupBy("1234567890", 4) == "12 3456 7890",
            NumberFormats.GroupBy("1234567890", 4));

        check("[数字转换器] 科学计数法（1230000 → 1.23×10⁶、0.000123 → 1.23×10⁻⁴）",
            NumberFormats.ToScientific("1230000") == "1.23×10⁶"
            && NumberFormats.ToScientific("0.000123") == "1.23×10⁻⁴"
            && NumberFormats.ToScientific("5") == "5"
            && NumberFormats.ToScientific("0") == "0"
            && NumberFormats.ToScientific("-1230000") == "-1.23×10⁶",
            NumberFormats.ToScientific("1230000") ?? "<null>");

        check("[数字转换器] 科学计数法可切 e 写法",
            NumberFormats.ToScientific("1230000", unicodeExponent: false) == "1.23e6"
            && NumberFormats.ToScientific("0.000123", unicodeExponent: false) == "1.23e-4",
            NumberFormats.ToScientific("1230000", unicodeExponent: false) ?? "<null>");

        check("[数字转换器] 十进制 → 二 / 八 / 十六 / 三十六进制（带 0b 0o 0x 前缀）",
            NumberFormats.ToBase(31, 16) == "0x1F"
            && NumberFormats.ToBase(31, 2) == "0b11111"
            && NumberFormats.ToBase(31, 8) == "0o37"
            && NumberFormats.ToBase(31, 10) == "31"
            && NumberFormats.ToBase(35, 36) == "Z"
            && NumberFormats.ToBase(35, 36, upper: false) == "z"
            && NumberFormats.ToBase(-16, 16) == "-0x10",
            $"{NumberFormats.ToBase(31, 16)} / {NumberFormats.ToBase(31, 2)} / {NumberFormats.ToBase(31, 8)}");

        var hexOk = NumberFormats.TryParseBase("0xFF", 10, out var hex, out _);
        var binOk = NumberFormats.TryParseBase("0b1010", 10, out var bin, out _);
        var octOk = NumberFormats.TryParseBase("0o17", 10, out var oct, out _);
        var hex2Ok = NumberFormats.TryParseBase("1F", 16, out var hex2, out _);

        check("[数字转换器] 进制 → 十进制（解析 0x / 0b / 0o 前缀）",
            hexOk && hex == 255 && binOk && bin == 10 && octOk && oct == 15 && hex2Ok && hex2 == 31,
            $"{hex} / {bin} / {oct} / {hex2}");

        var bad1 = NumberFormats.TryParseBase("0b102", 10, out _, out var error1);
        var bad2 = NumberFormats.TryParseBase("0xGG", 10, out _, out var error2);
        var bad3 = NumberFormats.TryParseBase("", 16, out _, out var error3);

        check("[数字转换器] 进制解析对非法字符给中文原因",
            !bad1 && error1!.Contains("2 进制") && !bad2 && error2!.Contains("16 进制")
            && !bad3 && error3 is not null,
            $"{error1} / {error2}");
    }

    // ---------------------------------------------------------------- 整条转换流水线

    private static void CheckPipeline(CheckFn check)
    {
        var result = NumberConverterEngine.Convert("1000");
        check("[数字转换器] 转换结果里 1000 的英文是 a thousand、中文是 一千",
            result.Success
            && RowText(result, "英文基数词") == "a thousand"
            && RowText(result, "中文小写") == "一千"
            && RowText(result, "中文大写") == "壹仟",
            RowText(result, "英文基数词"));

        var money = NumberConverterEngine.Convert("12");
        check("[数字转换器] 转换结果里 12 的人民币大写是 壹拾贰圆整",
            money.Success && RowText(money, "人民币金额大写") == "壹拾贰圆整",
            RowText(money, "人民币金额大写"));

        var withYuan = NumberConverterEngine.Convert("12", new ConverterOptions { Currency = "元" });
        check("[数字转换器] 口味里的货币单位会传进金额行",
            withYuan.Success && RowText(withYuan, "人民币金额大写") == "壹拾贰元整",
            RowText(withYuan, "人民币金额大写"));

        var american = NumberConverterEngine.Convert("123", new ConverterOptions
        {
            English = new EnglishStyle { UseAnd = false },
        });
        check("[数字转换器] 口味里的英文 and 开关会传进英文行",
            american.Success && RowText(american, "英文基数词") == "one hundred twenty-three",
            RowText(american, "英文基数词"));

        var chineseInput = NumberConverterEngine.Convert("壹拾贰圆叁角肆分");
        check("[数字转换器] 输入中文金额 → 输出 12.34 的各种写法",
            chineseInput.Success
            && RowText(chineseInput, "标准写法") == "12.34"
            && RowText(chineseInput, "英文金额") == "twelve dollars and thirty-four cents",
            RowText(chineseInput, "标准写法"));

        var hexInput = NumberConverterEngine.Convert("0x1F");
        check("[数字转换器] 输入 0x1F → 标准写法 31、罗马数字 XXXI",
            hexInput.Success && RowText(hexInput, "标准写法") == "31" && RowText(hexInput, "罗马数字") == "XXXI",
            RowText(hexInput, "标准写法"));

        check("[数字转换器] 空输入 / 乱写在结果里给一句中文原因（不抛异常）",
            !NumberConverterEngine.Convert("").Success
            && !NumberConverterEngine.Convert("   ").Success
            && !NumberConverterEngine.Convert("abc").Success
            && NumberConverterEngine.Convert("abc").Error is { Length: > 0 },
            NumberConverterEngine.Convert("abc").Error ?? "");

        check("[数字转换器] 空内容的结果行会被标记为不显示（界面不铺空行）",
            ResultContainsEmptyRow(NumberConverterEngine.Convert("123")) == false,
            "");

        var huge = NumberConverterEngine.Convert("1" + new string('0', 60));
        check("[数字转换器] 超大数（61 位）不崩，中文行说明超出范围",
            huge.Success && RowText(huge, "中文小写") == "" && RowText(huge, "英文基数词") == "",
            RowText(huge, "标准写法"));

        var fractional = NumberConverterEngine.Convert("12.345");
        check("[数字转换器] 小数：罗马数字行说明只支持整数、进制行也不适用",
            fractional.Success
            && RowText(fractional, "罗马数字") == ""
            && RowText(fractional, "进制") == ""
            && RowText(fractional, "千分位") == "12.345",
            RowText(fractional, "千分位"));
    }

    private static string RowText(ConversionResult result, string title)
        => result.Rows.FirstOrDefault(r => r.Title == title)?.Text ?? "<缺少这一行>";

    /// <summary>结果里还有没有「标题在但内容空着」的行（界面上不该出现）。</summary>
    private static bool ResultContainsEmptyRow(ConversionResult result)
        => result.Rows.Any(r => !r.HasText);

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new NumberConverterConfig();
        check("[数字转换器] 配置默认值（圆 / 补零 / 整 / 英式 and / a thousand / 连字符）",
            config.Currency == "圆" && config.ZeroOnJiaoGap && config.WholeAfterJiao
            && config.EnglishUseAnd && config.EnglishUseA && config.EnglishHyphenate
            && config.SafeCurrency == "圆" && config.SafeRadix == 16,
            $"{config.Currency} / {config.SafeRadix}");

        var clamped = new NumberConverterConfig { Currency = "乱写", Radix = 99 };
        check("[数字转换器] 配置里被手改坏的值夹回合法值",
            clamped.SafeCurrency == "圆" && clamped.SafeRadix == 36,
            $"{clamped.SafeCurrency} / {clamped.SafeRadix}");

        var roundTrip = new NumberConverterConfig
        {
            Input = "壹拾贰圆叁角肆分",
            Currency = "元",
            ZeroOnJiaoGap = false,
            WholeAfterJiao = false,
            EnglishUseAnd = false,
            EnglishUseA = false,
            EnglishHyphenate = false,
            Radix = 8,
        };
        var json = JsonSerializer.Serialize(roundTrip, JsonFileStore.Options);
        var parsed = JsonSerializer.Deserialize<NumberConverterConfig>(json, JsonFileStore.Options);

        check("[数字转换器] 配置 JSON 往返（输入 / 口味 / 进制都留住）",
            parsed is
            {
                Input: "壹拾贰圆叁角肆分",
                Currency: "元",
                ZeroOnJiaoGap: false,
                WholeAfterJiao: false,
                EnglishUseAnd: false,
                EnglishUseA: false,
                EnglishHyphenate: false,
                Radix: 8,
            },
            json);

        check("[数字转换器] 计算属性不进 JSON（SafeCurrency / SafeRadix 有 [JsonIgnore]）",
            !json.Contains("SafeCurrency") && !json.Contains("SafeRadix"),
            json);
    }
}
