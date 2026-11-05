using System.Globalization;

namespace YuLiangToolkit.Core.Expressions;

/// <summary>
/// 数字 → 界面文本的格式化（计算器结果、绘图坐标轴刻度、点值表共用）。
///
/// <para>规则：NaN → 「无定义」；±∞ → 「±∞」；0 → 「0」（不出现 -0）；
/// 普通数字按有效位截断并去掉末尾多余的 0；特别大 / 特别小改用 <c>1.23e+15</c> 这样的科学计数法。
/// 全程用 <see cref="CultureInfo.InvariantCulture"/>，避免系统区域设置把小数点变成逗号。</para>
/// </summary>
public static class MathFormat
{
    /// <summary>默认有效数字位数（计算器「精度」设置的上限参考值）。</summary>
    public const int DefaultSignificantDigits = 12;

    /// <summary>把计算结果格式化成给人看的文本。</summary>
    public static string Format(double value, int significantDigits = DefaultSignificantDigits)
    {
        if (double.IsNaN(value)) return "无定义";
        if (double.IsPositiveInfinity(value)) return "+∞";
        if (double.IsNegativeInfinity(value)) return "-∞";
        if (value == 0) return "0";

        significantDigits = Math.Clamp(significantDigits, 1, 15);

        var abs = Math.Abs(value);
        var exponent = (int)Math.Floor(Math.Log10(abs));

        // 特别大 / 特别小：科学计数法更清楚（不在整数位后面堆一长串 0）
        if (exponent >= 12 || exponent <= -7)
            return CleanExponent(value.ToString("E" + (significantDigits - 1), CultureInfo.InvariantCulture));

        var decimals = Math.Clamp(significantDigits - 1 - exponent, 0, 15);
        var rounded = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
        if (rounded == 0) return "0";   // 防止出现 "-0.00"

        return TrimZeros(rounded.ToString("F" + decimals, CultureInfo.InvariantCulture));
    }

    /// <summary>固定小数位（数字输入的校验提示、坐标刻度用）。</summary>
    public static string FormatFixed(double value, int decimals)
    {
        if (double.IsNaN(value)) return "无定义";
        if (double.IsPositiveInfinity(value)) return "+∞";
        if (double.IsNegativeInfinity(value)) return "-∞";

        var rounded = Math.Round(value, Math.Clamp(decimals, 0, 15), MidpointRounding.AwayFromZero);
        if (rounded == 0) return decimals > 0
            ? TrimZeros(0.0.ToString("F" + decimals, CultureInfo.InvariantCulture))
            : "0";
        return TrimZeros(rounded.ToString("F" + Math.Clamp(decimals, 0, 15), CultureInfo.InvariantCulture));
    }

    /// <summary>坐标轴刻度用的「短」格式：最多 6 位有效数字，避免刻度标签太长。</summary>
    public static string FormatTick(double value) => Format(value, 6);

    private static string TrimZeros(string text)
    {
        if (!text.Contains('.')) return text;
        text = text.TrimEnd('0');
        if (text.EndsWith('.')) text = text[..^1];
        return text.Length == 0 ? "0" : text;
    }

    /// <summary>把 .NET 的 <c>1.234E+012</c> 收拾成 <c>1.234e+12</c>。</summary>
    private static string CleanExponent(string text)
    {
        var index = text.IndexOf('E');
        if (index < 0) return text;

        var mantissa = TrimZeros(text[..index]);
        var exponentText = text[(index + 1)..];
        var sign = exponentText.StartsWith('-') ? "-" : "+";
        var digits = exponentText.TrimStart('+', '-').TrimStart('0');
        if (digits.Length == 0) digits = "0";
        return $"{mantissa}e{sign}{digits}";
    }
}
