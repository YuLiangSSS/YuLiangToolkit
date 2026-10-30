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
/// <para><b>尺寸</b>：最大半径由主页设置「反色圆环半径」滑块决定（默认 150 DIP，直径约 300），
/// 默认值沿用 v0.36.x 的观感（比 v0.36.0 首次上线时的 213 DIP 面积小一半）。</para>
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
