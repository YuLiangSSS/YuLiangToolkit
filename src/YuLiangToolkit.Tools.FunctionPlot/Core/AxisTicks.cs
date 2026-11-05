using YuLiangToolkit.Core.Expressions;

namespace YuLiangToolkit.Tools.FunctionPlot.Core;

/// <summary>一组坐标轴刻度：步长 + 具体刻度值（世界坐标）。</summary>
public readonly record struct TickSet(double Step, IReadOnlyList<double> Values)
{
    /// <summary>刻度个数。</summary>
    public int Count => Values.Count;

    /// <summary>是不是空（范围非法 / 算不出来）。</summary>
    public bool IsEmpty => Values.Count == 0;

    /// <summary>空刻度集。</summary>
    public static TickSet Empty => new(0, Array.Empty<double>());
}

/// <summary>
/// 「好看刻度」生成器：把任意世界范围切成 1 / 2 / 5 × 10ⁿ 的步长，刻度数量控制在
/// <see cref="MinGeneratedTicks"/> ~ <see cref="MaxGeneratedTicks"/> 之间。
///
/// <para>纯逻辑、无界面依赖；刻度文字用共享的 <see cref="MathFormat.FormatTick"/>（自检会核对两者一致），
/// 这样主程序和函数绘图里的数字格式不会各说各话。</para>
/// </summary>
public static class AxisTicks
{
    /// <summary>允许的最少刻度数（太少会显得网格稀稀拉拉）。</summary>
    public const int MinGeneratedTicks = 4;

    /// <summary>允许的最多刻度数（太多标签会挤在一起）。</summary>
    public const int MaxGeneratedTicks = 12;

    /// <summary>1 / 2 / 5 × 10ⁿ：把原始步长往上取整成「人看着舒服」的步长。</summary>
    public static double NiceStep(double rawStep)
    {
        if (!double.IsFinite(rawStep) || rawStep <= 0) return 1;

        var exponent = Math.Floor(Math.Log10(rawStep));
        var power = Math.Pow(10, exponent);
        var fraction = rawStep / power;   // [1, 10)

        // 留一点浮点容差：0.30000000000000004 这种不该被抬到 0.5
        var mantissa = fraction <= 1.0000001 ? 1.0
            : fraction <= 2.0000001 ? 2.0
            : fraction <= 5.0000001 ? 5.0
            : 10.0;

        return mantissa * power;
    }

    /// <summary>刻度标签（统一走共享格式化，别在绘图控件里另写一套）。</summary>
    public static string FormatLabel(double value) => MathFormat.FormatTick(value);

    /// <summary>
    /// 在 [<paramref name="min"/>, <paramref name="max"/>] 里生成「好看刻度」。
    /// <paramref name="maxTicks"/> 是期望上限（会被夹到 4~12）；上下界写反自动交换，
    /// 非有限值返回空集，范围退化成单点则先撑成 ±1。
    /// </summary>
    public static TickSet Generate(double min, double max, int maxTicks = 10)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return TickSet.Empty;
        if (min > max) (min, max) = (max, min);

        var span = max - min;
        if (span <= 0)
        {
            // 范围退化成一个点：左右各撑 1，至少还能看到刻度
            min -= 1;
            max += 1;
            span = max - min;
        }

        maxTicks = Math.Clamp(maxTicks, MinGeneratedTicks, MaxGeneratedTicks);
        var step = NiceStep(span / Math.Max(1, maxTicks - 1));

        // 往 1/2/5 序列里前后挪，直到刻度数落进 [4, maxTicks]
        for (var guard = 0; guard < 16; guard++)
        {
            var count = CountTicks(min, max, step);

            if (count > maxTicks)
            {
                var bigger = NextNiceStep(step, up: true);
                if (!(bigger > step)) break;
                step = bigger;
                continue;
            }

            if (count < MinGeneratedTicks)
            {
                var smaller = NextNiceStep(step, up: false);
                if (!(smaller > 0) || smaller >= step) break;
                if (CountTicks(min, max, smaller) > maxTicks) break;
                step = smaller;
                continue;
            }

            break;
        }

        return new TickSet(step, BuildTicks(min, max, step));
    }

    /// <summary>按步长数出覆盖范围内会有多少个刻度（用于判断多了 / 少了）。</summary>
    public static int CountTicks(double min, double max, double step)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || !double.IsFinite(step) || step <= 0) return 0;
        if (min > max) (min, max) = (max, min);

        var first = FirstTick(min, step);
        if (!double.IsFinite(first) || first > max + step * 1e-9) return 0;

        var raw = (max - first) / step;
        if (!double.IsFinite(raw) || raw > MaxGeneratedTicks * 4.0) return MaxGeneratedTicks * 4;
        return (int)Math.Floor(raw + 1e-9) + 1;
    }

    // ---------------------------------------------------------------- 私有

    /// <summary>第一个 ≥ min 的刻度值（先除再乘，避免浮点累积误差）。</summary>
    private static double FirstTick(double min, double step)
    {
        var index = Math.Ceiling(min / step - 1e-9);
        var value = index * step;
        return Math.Abs(value) < step * 1e-9 ? 0 : value;
    }

    /// <summary>真正列出刻度值（用乘法算，不用 v += step，避免长范围误差积累）。</summary>
    private static List<double> BuildTicks(double min, double max, double step)
    {
        var values = new List<double>(MaxGeneratedTicks + 2);
        if (!double.IsFinite(step) || step <= 0) return values;

        var first = FirstTick(min, step);
        for (var i = 0; i < MaxGeneratedTicks * 2 + 2; i++)
        {
            var value = first + i * step;
            if (!double.IsFinite(value) || value > max + step * 1e-9) break;

            // 0 附近的负零 / 1e-17 统一收成 0，标签才不会出现 "-0"
            if (Math.Abs(value) < step * 1e-9) value = 0;
            values.Add(value);
        }

        return values;
    }

    /// <summary>1/2/5 序列里的上一个 / 下一个档位。</summary>
    private static double NextNiceStep(double step, bool up)
    {
        if (!double.IsFinite(step) || step <= 0) return up ? 1 : 0.5;

        var exponent = Math.Floor(Math.Log10(step));
        var power = Math.Pow(10, exponent);
        var mantissa = step / power;   // ≈ 1 / 2 / 5 / 10

        if (up)
        {
            if (mantissa < 1.5) return 2 * power;
            if (mantissa < 3.5) return 5 * power;
            return 10 * power;
        }

        if (mantissa > 7) return 5 * power;
        if (mantissa > 3) return 2 * power;
        if (mantissa > 1.5) return power;
        return 5 * power / 10;   // 0.5 × 10ⁿ
    }
}
