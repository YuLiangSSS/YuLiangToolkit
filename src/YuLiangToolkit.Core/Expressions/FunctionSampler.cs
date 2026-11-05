namespace YuLiangToolkit.Core.Expressions;

/// <summary>函数图像上的一个采样点（世界坐标）。</summary>
public readonly record struct CurvePoint(double X, double Y);

/// <summary>
/// 函数采样：把一条表达式在 <c>[xMin, xMax]</c> 上按等间距采成折线，供绘图控件直接画。
///
/// <para>只做纯数学，不碰界面 / 不碰像素坐标（世界坐标 → 屏幕坐标由绘图工具自己的
/// <c>PlotViewport</c> 负责），所以可以直接被自检断言。</para>
///
/// <para>需要处理的三类「不该连成线」的情况（都在 <see cref="Sample"/> 里切成多段）：
/// ① 函数值无定义（NaN / ∞）；② 超出当前 y 可视范围；③ 渐近线附近相邻两点纵向跳变过大
/// （例如 <c>tan(x)</c>、<c>1/x</c>，直接连线会画出一根假竖线）。</para>
/// </summary>
public static class FunctionSampler
{
    /// <summary>采样点数下限（两个点才成一条线段）。</summary>
    public const int MinSamples = 2;

    /// <summary>采样点数上限（防止界面卡死；2 万个点画折线已经足够平滑）。</summary>
    public const int MaxSamples = 20000;

    /// <summary>默认的「渐近线跳变」倍数：相邻两点纵向差超过 y 范围 × 这个倍数就断线。</summary>
    public const double DefaultAsymptoteFactor = 20.0;

    /// <summary>单点求值（计算器 / 点值表用）。</summary>
    public static double EvaluateAt(MathExpression expression, double x, AngleUnit angleUnit = AngleUnit.Radians)
        => expression.Evaluate(x, angleUnit);

    /// <summary>
    /// 在 <c>[xMin, xMax]</c> 上采样，返回若干段连续折线（每段至少 2 个点）。
    /// <para>参数会被宽容处理：x / y 上下界写反自动交换，采样点数夹到
    /// <see cref="MinSamples"/>~<see cref="MaxSamples"/>，非有限边界直接返回空结果。</para>
    /// </summary>
    public static IReadOnlyList<CurvePoint[]> Sample(
        MathExpression expression,
        double xMin,
        double xMax,
        int samples,
        AngleUnit angleUnit = AngleUnit.Radians,
        double yMin = double.NegativeInfinity,
        double yMax = double.PositiveInfinity,
        double asymptoteFactor = DefaultAsymptoteFactor)
    {
        if (expression is null) return Array.Empty<CurvePoint[]>();
        if (!double.IsFinite(xMin) || !double.IsFinite(xMax)) return Array.Empty<CurvePoint[]>();

        if (xMin > xMax) (xMin, xMax) = (xMax, xMin);
        if (yMin > yMax) (yMin, yMax) = (yMax, yMin);

        samples = Math.Clamp(samples, MinSamples, MaxSamples);

        var hasYLimit = double.IsFinite(yMin) && double.IsFinite(yMax);
        var jumpLimit = hasYLimit ? (yMax - yMin) * Math.Max(1.0, asymptoteFactor) : double.PositiveInfinity;

        var segments = new List<CurvePoint[]>();
        var current = new List<CurvePoint>(Math.Min(samples, 1024));

        void Flush()
        {
            if (current.Count >= 2) segments.Add(current.ToArray());
            current.Clear();
        }

        var step = (xMax - xMin) / (samples - 1);
        var previousX = double.NaN;
        var previousY = double.NaN;
        var previousInside = false;

        for (var i = 0; i < samples; i++)
        {
            // 最后一个点用 xMax 收尾，避免浮点累积误差让曲线差一丢丢
            var x = i == samples - 1 ? xMax : xMin + i * step;
            var y = expression.Evaluate(x, angleUnit);

            if (!double.IsFinite(y))
            {
                Flush();
                previousInside = false;
                previousX = x;
                previousY = double.NaN;
                continue;
            }

            var inside = y >= yMin && y <= yMax;

            if (!inside)
            {
                // 从可视范围里穿出去：补一个边界交点再断开，曲线不会在边缘「突然消失」
                if (previousInside && double.IsFinite(previousY))
                {
                    var exit = FindBoundaryCrossing(previousX, previousY, x, y, yMin, yMax);
                    if (exit is { } exitPoint) current.Add(exitPoint);
                }
                Flush();
                previousInside = false;
                previousX = x;
                previousY = y;
                continue;
            }

            // 进入可视范围：补一个边界交点作为线段起点
            if (!previousInside && double.IsFinite(previousY))
            {
                var entry = FindBoundaryCrossing(previousX, previousY, x, y, yMin, yMax);
                if (entry is { } entryPoint) current.Add(entryPoint);
            }

            // 渐近线：纵向跳变过大就断线（tan / 1x 在极点附近）
            if (previousInside && double.IsFinite(previousY) && Math.Abs(y - previousY) > jumpLimit)
                Flush();

            current.Add(new CurvePoint(x, y));
            previousInside = true;
            previousX = x;
            previousY = y;
        }

        Flush();
        return segments;
    }

    /// <summary>求线段 (x0,y0)→(x1,y1) 与 y = yMin / yMax 两条水平边的交点（只取范围内的那一个）。</summary>
    private static CurvePoint? FindBoundaryCrossing(
        double x0, double y0, double x1, double y1, double yMin, double yMax)
    {
        if (!double.IsFinite(y0) || !double.IsFinite(y1)) return null;
        if (Math.Abs(y1 - y0) < 1e-12) return null;

        double target;
        if (y0 < yMin && y1 >= yMin) target = yMin;
        else if (y0 > yMax && y1 <= yMax) target = yMax;
        else if (y0 >= yMin && y0 <= yMax && (y1 < yMin || y1 > yMax)) target = y1 < yMin ? yMin : yMax;
        else return null;

        var t = (target - y0) / (y1 - y0);
        if (t < 0 || t > 1) return null;
        return new CurvePoint(x0 + (x1 - x0) * t, target);
    }
}
