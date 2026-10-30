namespace YuLiangToolkit.Core.Effects;

/// <summary>
/// 点击效果「反色圆环」的时间线（**纯数学，不碰 WPF**，自检可以直接断言）：
/// 单击处先出现一个反色的圆形区域，半径按 <see cref="Easings.OutQuint"/> 快速扩散、再按同一条曲线反向收缩到 0。
///
/// <para><b>曲线</b>：扩散阶段 <c>r = R × OutQuint(t)</c>（一开始半径涨得很快、最后变慢）；
/// 收缩阶段把同一条 easeOut 用在「剩余半径」上：<c>r = R × OutQuint(1-t)</c>
/// （一开始半径掉得很慢、最后掉得很快）—— 正好是需求里描述的「非线性变大缩小」，两段都是先快后慢的 easeOut 家族。</para>
///
/// <para><b>时间单位</b>与粒子引擎一致：60fps 基准帧（覆盖层把真实经过时间换算成帧步长再传进来）。</para>
/// </summary>
public static class InvertCircleAnimation
{
    /// <summary>扩散阶段持续的基准帧数（约 0.27 秒）。</summary>
    public const double GrowFrames = 16;

    /// <summary>收缩阶段持续的基准帧数（约 0.43 秒）。</summary>
    public const double ShrinkFrames = 26;

    /// <summary>整个反色圆环持续的基准帧数（约 0.7 秒）。</summary>
    public const double TotalFrames = GrowFrames + ShrinkFrames;

    /// <summary>窗口很小时的最小最大半径（DIP；保证小窗里也看得清）。</summary>
    public const double SmallestRadius = 150;

    /// <summary>窗口很大时的最大半径上限（DIP；再大就糊成一片了）。</summary>
    public const double LargestRadius = 240;

    /// <summary>最大半径相对窗口短边的比例（1280×820 的默认窗口约 213 DIP）。</summary>
    public const double RadiusRatio = 0.26;

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
            // 扩散：一开始半径涨得很快，越接近最大半径越慢
            return maxRadius * Easings.OutQuint(age / GrowFrames);
        }

        // 收缩：剩余半径 = OutQuint(1 - 进度)，所以一开始几乎不掉、最后掉得很快
        var progress = (age - GrowFrames) / ShrinkFrames;
        return maxRadius * Easings.OutQuint(1.0 - progress);
    }

    /// <summary>这一击是不是已经播完（<see cref="TotalFrames"/> 帧后可以回收）。</summary>
    public static bool IsFinished(double age)
        => double.IsNaN(age) || age >= TotalFrames;
}
