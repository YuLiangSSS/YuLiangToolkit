namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 拖动惯性（松手后继续滑一段）的**纯逻辑**：采样速度 → 按时间衰减 → 判断什么时候停。
///
/// <para>为什么单独抽出来：这是「手机漫画 App 手感」的核心，但它在界面里只是一堆鼠标事件和定时器；
/// 抽成纯逻辑后，自检可以直接断言「甩得快 → 有速度」「停着 → 没速度」「衰减方向不变、越来越慢」
/// 「太慢就停」，不用真的去动鼠标（见 AGENTS.md 2.19）。</para>
///
/// <para>单位：偏移量是 DIP 像素，时间是毫秒，所以速度是 <b>像素 / 毫秒</b>
/// （60fps 下 <c>0.5 px/ms</c> ≈ 每帧 8px，手感合适）。</para>
/// </summary>
public sealed class PictureInertia
{
    /// <summary>默认摩擦系数（每毫秒衰减的指数）：约 200ms 速度减半，比手机那种“一滑滑到底”收敛得快一点。</summary>
    public const double DefaultFrictionPerMillisecond = 0.0035;

    /// <summary>低于这个速度（像素 / 毫秒）就停：约 20 px/s，肉眼基本看不出还在动。</summary>
    public const double DefaultMinimumSpeed = 0.02;

    /// <summary>只保留最近这么久的采样来算松手瞬间的速度（太久的会拖慢“甩”的手感）。</summary>
    public const double SampleWindowMilliseconds = 120;

    /// <summary>
    /// 叠加后的速度上限（像素 / 毫秒）：连续甩动会一直加速，必须封顶，不然一下就滑穿整本漫画。
    /// 6 px/ms ≈ 360 px/帧（60fps），已经比“正常甩一下”快很多了。
    /// </summary>
    public const double DefaultMaxSpeed = 6.0;

    private readonly List<(double Time, double Main, double Cross)> _samples = new();

    /// <summary>摩擦系数（每毫秒）。越大停得越快。</summary>
    public double FrictionPerMillisecond { get; set; } = DefaultFrictionPerMillisecond;

    /// <summary>起滑 / 继续滑的最低速度（像素 / 毫秒，两轴合成速度）。</summary>
    public double MinimumSpeed { get; set; } = DefaultMinimumSpeed;

    /// <summary>清空采样（开始一次新的拖拽时调用）。</summary>
    public void Reset() => _samples.Clear();

    /// <summary>记录一次拖拽过程中的位置（时间用毫秒，可以是任意单调时钟）。</summary>
    public void Note(double timeMilliseconds, double mainOffset, double crossOffset)
    {
        _samples.Add((timeMilliseconds, mainOffset, crossOffset));

        // 只留最近一小段时间的采样，避免越拖越长
        var cutoff = timeMilliseconds - SampleWindowMilliseconds;
        var remove = 0;
        while (remove < _samples.Count - 1 && _samples[remove].Time < cutoff) remove++;
        if (remove > 0) _samples.RemoveRange(0, remove);
    }

    /// <summary>
    /// 松手瞬间的速度（像素 / 毫秒）：用采样窗口里最早和最新两点算平均。
    /// 采样不足两点 / 时间没走动时返回 (0,0)（= 不甩）。
    /// </summary>
    public (double Main, double Cross) ComputeVelocity()
    {
        if (_samples.Count < 2) return (0, 0);

        var first = _samples[0];
        var last = _samples[^1];
        var dt = last.Time - first.Time;
        if (dt <= 0) return (0, 0);

        return ((last.Main - first.Main) / dt, (last.Cross - first.Cross) / dt);
    }

    /// <summary>速度够不够起滑 / 继续滑（两轴合成速度 ≥ 最小值）。</summary>
    public bool IsFastEnough(double mainVelocity, double crossVelocity)
        => IsFastEnough(mainVelocity, crossVelocity, MinimumSpeed);

    /// <summary>速度够不够（静态版本，自检 / 界面都能用）。</summary>
    public static bool IsFastEnough(double mainVelocity, double crossVelocity, double minimumSpeed)
    {
        if (double.IsNaN(mainVelocity) || double.IsNaN(crossVelocity)) return false;
        if (minimumSpeed <= 0) return true;
        return Math.Sqrt(mainVelocity * mainVelocity + crossVelocity * crossVelocity) >= minimumSpeed;
    }

    /// <summary>过 <paramref name="elapsedMilliseconds"/> 之后的速度（指数衰减，方向不变）。</summary>
    public (double Main, double Cross) Decay(double mainVelocity, double crossVelocity, double elapsedMilliseconds)
    {
        if (elapsedMilliseconds <= 0 || double.IsNaN(elapsedMilliseconds)) return (mainVelocity, crossVelocity);

        var factor = Math.Exp(-Math.Max(0, FrictionPerMillisecond) * elapsedMilliseconds);
        return (mainVelocity * factor, crossVelocity * factor);
    }

    /// <summary>
    /// 把「这一次甩的速度」和「还在滑的残余速度」合成（手机漫画 App 的“越滑越快”手感）：
    /// <paramref name="accumulate"/> = true 时两者相加 —— 连续甩动会一直加速，而不会因为下一次拖动把速度清零；
    /// 相加后超过 <paramref name="maxSpeed"/> 就按比例压回上限（不改变方向）。
    /// </summary>
    public static (double Main, double Cross) Combine(
        double newMain,
        double newCross,
        double carriedMain,
        double carriedCross,
        bool accumulate,
        double maxSpeed = DefaultMaxSpeed)
    {
        if (double.IsNaN(newMain)) newMain = 0;
        if (double.IsNaN(newCross)) newCross = 0;
        if (double.IsNaN(carriedMain)) carriedMain = 0;
        if (double.IsNaN(carriedCross)) carriedCross = 0;

        var main = newMain + (accumulate ? carriedMain : 0);
        var cross = newCross + (accumulate ? carriedCross : 0);

        if (maxSpeed > 0)
        {
            var speed = Math.Sqrt(main * main + cross * cross);
            if (speed > maxSpeed)
            {
                var scale = maxSpeed / speed;
                main *= scale;
                cross *= scale;
            }
        }

        return (main, cross);
    }
}
