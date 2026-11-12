using System.Globalization;
using System.Numerics;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.NumberConverter.Core;

/// <summary>一条转换结果：标题 + 内容 + 说明（内容为空 = 这一项对当前输入不适用，界面不显示）。</summary>
public sealed record ConversionRow(string Title, string Text, string Hint = "")
{
    /// <summary>有没有内容（空行不显示）。</summary>
    public bool HasText => Text.Length > 0;
}

/// <summary>一次转换的完整结果（成功 / 失败 + 中文原因 + 若干行结果）。</summary>
public sealed record ConversionResult(bool Success, string? Error, IReadOnlyList<ConversionRow> Rows)
{
    /// <summary>成功时的结果。</summary>
    public static ConversionResult Ok(IReadOnlyList<ConversionRow> rows) => new(true, null, rows);

    /// <summary>失败时只有一句中文原因。</summary>
    public static ConversionResult Fail(string error) => new(false, error, Array.Empty<ConversionRow>());
}

/// <summary>读输入的口味（与配置一一对应，逻辑层不直接依赖配置类，方便自检构造）。</summary>
public sealed record ConverterOptions
{
    /// <summary>金额单位：圆 / 元。</summary>
    public string Currency { get; init; } = "圆";

    /// <summary>角位为零而分位有值时补「零」。</summary>
    public bool ZeroOnJiaoGap { get; init; } = true;

    /// <summary>分位为零而角位有值时写「整」。</summary>
    public bool WholeAfterJiao { get; init; } = true;

    /// <summary>英文读法的口味。</summary>
    public EnglishStyle English { get; init; } = EnglishStyle.Default;

    /// <summary>进制（没写 0x / 0b / 0o 前缀时用）。</summary>
    public int Radix { get; init; } = 16;

    /// <summary>默认口味。</summary>
    public static ConverterOptions Default { get; } = new();
}

/// <summary>
/// 「数字转换器」的门面：把一份输入一次性转成所有格式（中文大小写 / 金额 / 英文 / 罗马 / 进制 / 科学计数…）。
/// 界面只负责把结果铺成列表，业务全在这里，可被自检直接断言。
///
/// <para>输入可以是阿拉伯数字（1234.56、-0.5、1e6、1,234），也可以是中文数字（一百二十三、壹拾贰圆叁角肆分），
/// 后者会被读回数值再按所选格式输出。</para>
/// </summary>
public static class NumberConverterEngine
{
    /// <summary>转换结果里最多允许多少位数字（再长中文读法就没有单位了）。</summary>
    public const int MaxInputDigits = 48;

    /// <summary>一条输入最多转出多少个字符就放弃（防手滑把界面卡死）。</summary>
    public const int MaxOutputChars = 200_000;

    /// <summary>
    /// 解析输入：先认出 0x / 0b / 0o 前缀（十六 / 二 / 八进制），再按阿拉伯数字解析
    /// （支持千分位逗号、空格、科学计数法），最后按中文数字解析
    /// （所以「一百二十三」「壹拾贰圆叁角肆分」也能读回数值）。
    /// </summary>
    public static bool TryParseInput(string? text, out ExactDecimal value, out string? error)
    {
        value = ExactDecimal.Zero;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "请先输入要转换的数字";
            return false;
        }

        var trimmed = text.Trim();

        // 0x1F / 0b1010 / 0o17：按对应进制读成整数，后面的流程一律按十进制处理
        if (trimmed.Length > 2 && trimmed[0] == '0' && trimmed[1] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O')
        {
            if (NumberFormats.TryParseBase(trimmed, 10, out var based, out error))
            {
                value = ExactDecimal.FromInteger(based);
                return true;
            }
            return false;
        }

        if (ExactDecimal.TryParse(CleanArabic(trimmed), out value)) return true;
        if (ChineseNumber.TryParse(trimmed, out value, out error)) return true;
        if (string.IsNullOrEmpty(error)) error = "看不懂这个数字";
        return false;
    }

    /// <summary>去掉阿拉伯数字里的千分位逗号 / 空格 / 下划线 / 货币符号（中文数字解析会自己处理这些）。</summary>
    private static string CleanArabic(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (ch is ',' or ' ' or '\t' or '_' or '¥' or '￥') continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 完整转换：把输入转成所有能用的格式。失败时 <see cref="ConversionResult.Error"/> 是中文原因。
    /// </summary>
    public static ConversionResult Convert(string? text, ConverterOptions? options = null)
    {
        options ??= ConverterOptions.Default;

        if (!TryParseInput(text, out var value, out var parseError))
            return ConversionResult.Fail(parseError ?? "看不懂这个数字");

        var rows = new List<ConversionRow>();

        // ---------- 原值 / 规范化 ----------
        var plain = value.ToPlainString();
        rows.Add(new ConversionRow("标准写法", plain, "去掉千分位、多余的 0 和前导 + 号"));

        var digits = CountIntegerDigits(value);
        var tooManyDigits = digits > MaxInputDigits;

        // ---------- 中文 ----------
        if (tooManyDigits)
        {
            rows.Add(new ConversionRow("中文小写", "", $"整数超过 {MaxInputDigits} 位，中文读法没有对应的单位了"));
            rows.Add(new ConversionRow("中文大写", "", ""));
        }
        else
        {
            rows.Add(new ConversionRow("中文小写", ChineseNumber.FormatDecimal(value, false) ?? "",
                "一百二十三点四五"));
            rows.Add(new ConversionRow("中文大写", ChineseNumber.FormatDecimal(value, true) ?? "",
                "壹佰贰拾叁点肆伍（财务 / 票据）"));
            rows.Add(new ConversionRow("中文序数", ChineseNumber.FormatOrdinal(value, false) ?? "",
                "第一百二十三（排名 / 楼层）"));
        }

        // ---------- 金额 ----------
        if (tooManyDigits)
        {
            rows.Add(new ConversionRow("人民币金额大写", "", ""));
            rows.Add(new ConversionRow("人民币金额小写", "", ""));
        }
        else
        {
            rows.Add(new ConversionRow("人民币金额大写",
                ChineseNumber.FormatMoneyUpper(value, options.Currency, options.ZeroOnJiaoGap, options.WholeAfterJiao) ?? "",
                "写支票 / 转款备注用这一行"));
            rows.Add(new ConversionRow("人民币金额小写",
                ChineseNumber.FormatMoneyLower(value, options.Currency, options.ZeroOnJiaoGap, options.WholeAfterJiao) ?? "",
                "同样是中文数字，只是不用大写"));
        }

        // ---------- 英文 ----------
        var style = options.English;
        var englishCardinal = EnglishNumber.FormatDecimal(value, style);
        if (englishCardinal is null)
        {
            rows.Add(new ConversionRow("英文基数词", "", $"整数超过 {EnglishNumber.MaxDigits} 位，英文读法只到 decillion"));
            rows.Add(new ConversionRow("英文序数词", "", ""));
            rows.Add(new ConversionRow("英文金额", "", ""));
            rows.Add(new ConversionRow("英文支票", "", ""));
        }
        else
        {
            rows.Add(new ConversionRow("英文基数词", englishCardinal, "one hundred and twenty-three"));
            rows.Add(new ConversionRow("英文序数词", EnglishNumber.FormatDecimalOrdinal(value, style) ?? "",
                "twenty-third（只把最后一个词变序数）"));
            rows.Add(new ConversionRow("英文金额",
                EnglishNumber.FormatMoney(value, style) ?? "", "twelve dollars and thirty-four cents"));
            rows.Add(new ConversionRow("英文支票",
                EnglishNumber.FormatCheque(value, style) ?? "", "twelve and 34/100 dollars（支票那一栏）"));
        }

        // ---------- 生活中的其它写法 ----------
        var englishDigits = EnglishNumber.FormatDigits(plain, style);
        rows.Add(new ConversionRow("英文逐位读", englishDigits ?? "", "念号码 / 订单号：one two three"));

        if (value.IsInteger && value.TryToBigInteger(out var integer))
        {
            rows.Add(new ConversionRow("罗马数字", NumberFormats.ToRoman(integer) ?? "",
                integer >= 1 && integer <= NumberFormats.RomanMax
                    ? "1 ~ 3999（章节 / 钟表 / 赛事届数）"
                    : $"罗马数字只支持 1 ~ {NumberFormats.RomanMax}"));
        }
        else
        {
            rows.Add(new ConversionRow("罗马数字", "", "只有 1 ~ 3999 的整数能写罗马数字"));
        }

        rows.Add(new ConversionRow("千分位", NumberFormats.GroupThousands(plain), "1,234,567.89"));
        rows.Add(new ConversionRow("每 4 位分组", NumberFormats.GroupBy(plain, 4), "念银行卡号：1234 5678 9012"));
        rows.Add(new ConversionRow("科学计数法", NumberFormats.ToScientific(plain) ?? "", "1.23×10⁶"));

        if (value.IsInteger && value.TryToBigInteger(out var bigInteger) && bigInteger.Sign >= 0
            && bigInteger.ToString(CultureInfo.InvariantCulture).Length <= 2000)
        {
            for (var i = 0; i < NumberFormats.BaseOptions.Count; i++)
            {
                var radix = NumberFormats.BaseOptions[i];
                var text2 = NumberFormats.ToBase(bigInteger, radix);
                if (text2 is null || text2.Length > MaxOutputChars) continue;
                rows.Add(new ConversionRow($"{radix} 进制", text2, radix == 10 ? "十进制（原样）" : RadixHint(radix)));
            }
        }
        else
        {
            rows.Add(new ConversionRow("进制", "", "只有非负整数才能转进制"));
        }

        return ConversionResult.Ok(rows);
    }

    private static string RadixHint(int radix) => radix switch
    {
        2 => "0b 前缀（二进制）",
        8 => "0o 前缀（八进制）",
        16 => "0x 前缀（十六进制）",
        36 => "0 ~ 9 + A ~ Z（短链接常用）",
        _ => "",
    };

    /// <summary>整数部分有多少位（0 算 1 位）。</summary>
    private static int CountIntegerDigits(ExactDecimal value)
    {
        var text = value.ToPlainString();
        if (text.StartsWith('-')) text = text[1..];
        var dot = text.IndexOf('.');
        if (dot >= 0) text = text[..dot];
        text = text.TrimStart('0');
        return text.Length == 0 ? 1 : text.Length;
    }

    /// <summary>一行结果是否超长（界面用它决定要不要截断 / 提示）。</summary>
    public static bool IsTooLong(string text) => text.Length > MaxOutputChars;
}
