using System.Numerics;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.RandomNumber.Core;

/// <summary>
/// 随机数生成规则的纯逻辑门面（好断言）：配置文本校验、结果条数夹取、生成、排序、拼接。
/// 数值全走 <see cref="ExactDecimal"/> / <see cref="BigInteger"/>，不用整型 / 浮点存。
/// </summary>
public static class ExactRandom
{
    /// <summary>生成条数下限。</summary>
    public const int MinCount = 1;

    /// <summary>生成条数上限（与 DecimalStepRange.MaxCount 一致）。</summary>
    public const int MaxCount = DecimalStepRange.MaxCount;

    /// <summary>分隔符选项（存进配置的就是这几个干净中文值）。</summary>
    public static IReadOnlyList<string> SeparatorOptions { get; } = new[] { "换行", "逗号", "空格", "制表" };

    /// <summary>界面 / 配置里被手改坏的条数夹到合法范围。</summary>
    public static int ClampCount(int count) => Math.Clamp(count, MinCount, MaxCount);

    /// <summary>把分隔符规范成四个合法值之一。</summary>
    public static string NormalizeSeparator(string? separator)
        => SeparatorOptions.Contains(separator ?? "") ? separator! : "换行";

    /// <summary>不重复模式下最多能生成多少条（网格点数可能远超 int）。</summary>
    public static int MaxUniqueCount(BigInteger pointCount)
        => pointCount > MaxCount ? MaxCount : (int)pointCount;

    /// <summary>
    /// 一次生成的完整结果（成功 / 失败 + 中文原因 + 结果文本）。纯逻辑，不碰界面 / 剪贴板。
    /// 随机源可注入以便自检写确定性断言。
    /// </summary>
    public readonly record struct GenerateResult(
        bool Success,
        IReadOnlyList<string> Values,
        string Text,
        string? Error,
        string UpperAlignedHint)
    {
        /// <summary>失败。</summary>
        public static GenerateResult Fail(string error)
            => new(false, Array.Empty<string>(), "", error, "");
    }

    public static GenerateResult Generate(
        string? lowerText,
        string? upperText,
        string? stepText,
        int count,
        bool allowDuplicates,
        bool sortResults,
        string? separator,
        Func<BigInteger, BigInteger>? randomBelow = null)
    {
        if (!DecimalStepRange.TryCreate(lowerText, upperText, stepText, out var range, out var error) || range is null)
            return GenerateResult.Fail(error ?? "参数不合法");

        var wanted = ClampCount(count);
        if (!allowDuplicates) wanted = Math.Min(wanted, MaxUniqueCount(range.PointCount));

        var values = range.Generate(wanted, allowDuplicates, false, randomBelow);
        if (sortResults) values = DecimalStepRange.Sort(values);

        var hint = range.IsUpperAligned(upperText)
            ? ""
            : $"上限已按精确度对齐到 {range.Upper.ToPlainString()}（保证结果不越界）";

        return new GenerateResult(
            true,
            values,
            DecimalStepRange.Join(values, NormalizeSeparator(separator)),
            null,
            hint);
    }
}
