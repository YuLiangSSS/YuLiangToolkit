using System.Numerics;
using YuLiangToolkit.Core.Numbers;

namespace YuLiangToolkit.Tools.NumberConverter.Core;

/// <summary>
/// 金额换算的小工具：把 <see cref="ExactDecimal"/> 四舍五入到「分」并拆成整元 / 角 / 分。
/// 中文金额大写与英文金额读法共用，避免两边各写一套取整（自检里对拍同一组用例）。
/// </summary>
internal static class MoneyParts
{
    /// <summary>四舍五入到分后的金额（单位：分）。</summary>
    public static BigInteger ToCents(ExactDecimal value)
    {
        var scaled = value * ExactDecimal.FromInteger(100);
        var divisor = ExactDecimal.Pow10(scaled.Scale);
        var quotient = BigInteger.DivRem(BigInteger.Abs(scaled.Unscaled), divisor, out var remainder);
        if (remainder * 2 >= divisor) quotient += 1;
        return scaled.IsNegative ? -quotient : quotient;
    }

    /// <summary>拆成（是否负数, 整元, 角, 分）。角 / 分都是 0~9。</summary>
    public static (bool Negative, BigInteger Whole, int Jiao, int Fen) Split(ExactDecimal value)
    {
        var cents = ToCents(value);
        var whole = BigInteger.DivRem(BigInteger.Abs(cents), 100, out var rest);
        return (cents.Sign < 0, whole, (int)(rest / 10), (int)(rest % 10));
    }
}
