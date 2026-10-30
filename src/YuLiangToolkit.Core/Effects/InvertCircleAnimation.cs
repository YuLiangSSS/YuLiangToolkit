namespace YuLiangToolkit.Core.Effects;

/// <summary>
/// 点击效果「反色圆环」的时间线（**纯数学，不碰 WPF**，自检可以直接断言）：
/// 单击处先出现一个反色的圆形区域，半径按 <see cref="Easings.OutCubic"/> 快速扩散、再按同一条曲线反向收缩到 0。
///
/// <para><b>曲线</b>：扩散阶段 <c>r = R × OutCubic(t)</c>（一开始半径涨得快、越接近最大越快不起来）；
/// 收缩阶段把同一条 easeOut 用在「剩余半径」上：<c>r = R × OutCubic(1-t)</c>
/// （一开始半径掉得很慢、最后掉得很快）。</para>
///
/// <para><b>为什么是三次方</b>：v0.36.0 先用的是 easeOutQuint（五次方），实测开头变大太冲、
/// 减速段又太突然；换成 easeOutCubic 后起点速度降到原来的 3/5，减速也更线性，观感平滑不少。</para>
///
/// <para><b>尺寸</b>：最大半径取窗口短边 × <see cref="RadiusRatio"/>（默认窗口约 150.8 DIP，直径约 302），
/// 相对 v0.36.0 的半径 ÷ √2 —— **面积正好缩一半**。</para>
///
/// <para><b>时间单位</b>与粒子引擎一致：60fps 基准帧（覆盖层把真实经过时间换算成帧步长再传进来）。</para>
/// </summary>
public static class InvertCircleAnimation
{
    /// <summary>扩散阶段持续的基准帧数（约 0.33 秒）。</summary>
    public const double GrowFrames = 20;

    /// <summary>收缩阶段持续的基准帧数（约 0.5 秒）。</summary>
    public const double ShrinkFrames = 30;

    /// <summary>整个反色圆环持续的基准帧数（约 0.83 秒）。</summary>
    public const double TotalFrames = GrowFrames + ShrinkFrames;

    /// <summary>窗口很小时的最小最大半径（DIP；保证小窗里也看得清）。</summary>
    public const double SmallestRadius = 106.07;

    /// <summary>窗口很大时的最大半径上限（DIP；再大就糊成一片了）。</summary>
    public const double LargestRadius = 169.71;

    /// <summary>
    /// 最大半径相对窗口短边的比例：0.183847763…（= v0.36.0 的 0.26 ÷ √2，**面积缩小一半**）。
    /// 默认 1280×820 窗口约 150.8 DIP（直径约 302）。
    /// </summary>
    public const double RadiusRatio = 0.18384776310850235;

    /// <summary>
    /// 按窗口尺寸算这一击的最大半径（DIP）：窗口短边 × <see cref="RadiusRatio"/>，
    /// 夹在 <see cref="SmallestRadius"/> ~ <see cref="LargestRadius"/> 之间。
    /// 尺寸还没量出来 / 非法时按最小半径处理。
    /// </summary>
    public static double ResolveMaxRadius(double width, double height)
    {
        var shortest = Math.Min(width, height);
        if (double.IsNaN(shortest) || double.IsInfinity(shortest) || shortest <= 1) return SmallestRadius;
        return Math.Clamp(shortest * RadiusRatio, SmallestRadius, LargestRadius);
    }

    /// <summary>
    /// 某一时刻的半径（DIP）：<paramref name="age"/> 是已经走过的基准帧数。
    /// 未开始 / 已结束 / 非法值都返回 0（调用方据此跳过绘制）。
    /// </summary>
    public static double RadiusAt(double age, double maxRadius)
    {
        if (double.IsNaN(maxRadius) || double.IsInfinity(maxRadius) || maxRadius <= 0) return 0;
        if (double.IsNaN(age) || age <= 0 || age >= TotalFrames) return 0;

        if (age < GrowFrames)
        {
            // 扩散：一开始半径涨得快、越接近最大半径越慢
            return maxRadius * Easings.OutCubic(age / GrowFrames);
        }

        // 收缩：剩余半径 = OutCubic(1 - 进度)，所以一开始几乎不掉、最后掉得很快
        var progress = (age - GrowFrames) / ShrinkFrames;
        return maxRadius * Easings.OutCubic(1.0 - progress);
    }

    /// <summary>这一击是不是已经播完（<see cref="TotalFrames"/> 帧后可以回收）。</summary>
    public static bool IsFinished(double age)
        => double.IsNaN(age) || age >= TotalFrames;
}
