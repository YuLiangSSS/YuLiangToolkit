using System;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>
/// 星数 → 等级（Level）的换算公式（旧版 <c>LevelCalculator.xaml.cs</c> 的逐行移植）。
///
/// <para>旧版是一个 WPF 页面里的两个方法；这里抽成纯函数，供“等级计算器 / 转谱预览”复用。</para>
/// </summary>
public static class LevelCalculator
{
    /// <summary>拟合公式的下界（旧版常量 LOWER_BOUND，逐位照搬）。</summary>
    public const double LowerBound = 2.76257856739498;

    /// <summary>拟合公式的上界（旧版常量 UPPPER_BOUND，逐位照搬；旧代码就是三个 P）。</summary>
    public const double UpperBound = 10.5541834716376;

    /// <summary>
    /// 由星数算等级（旧版 <c>CalculateLevel</c> 的逐行移植）。
    ///
    /// <list type="bullet">
    /// <item>2.76257856739498 ~ 10.5541834716376：走拟合公式 <c>6.9615 * e^(0.1374 * x)</c>；</item>
    /// <item>0 &lt; x &lt; 下界：<c>3.6198 * x</c>；</item>
    /// <item>上界 &lt; x &lt; 12.3456789：<c>2.791 * x + 0.5436</c>；</item>
    /// <item>其它（含 0、负数、&gt;= 12.3456789、NaN）：返回 <see cref="double.NaN"/>。</item>
    /// </list>
    /// </summary>
    public static double CalculateLevel(double starRating)
    {
        if (starRating >= LowerBound && starRating <= UpperBound)
        {
            return FittingFormula(starRating);
        }

        if (starRating < LowerBound && starRating > 0)
        {
            return 3.6198 * starRating;
        }

        if (starRating > UpperBound && starRating < 12.3456789)
        {
            return (2.791 * starRating) + 0.5436;
        }

        return double.NaN;
    }

    /// <summary>拟合公式 <c>6.9615 * e^(0.1374 * x)</c>。</summary>
    public static double FittingFormula(double x)
    {
        double y = 6.9615 * Math.Pow(Math.E, 0.1374 * x);
        return y;
    }

    /// <summary>由谱面当前的星数算等级（<see cref="OsuManiaBeatmap.StarRating"/>）。</summary>
    public static double CalculateLevel(OsuManiaBeatmap beatmap) => CalculateLevel(beatmap.StarRating);

    /// <summary>
    /// 方便界面显示：NaN 时给 “-/异常”，否则保留 <paramref name="digits"/> 位小数
    /// （旧版界面用 <c>Math.Round(..., 2)</c>，默认 2 位）。
    /// </summary>
    public static string FormatLevel(double level, int digits = 2)
        => double.IsNaN(level) ? "-" : Math.Round(level, digits).ToString("0." + new string('#', digits));
}
