namespace YuLiangToolkit.Core.Particles;

/// <summary>
/// 一个粒子（由 <see cref="ParticleEngine"/> 逐帧原地更新）。
///
/// <para>坐标是**控件坐标**（DIP，左上角原点、Y 轴向下 —— 所以 <see cref="Angle"/> 用的是屏幕角度，
/// 正方向 = 向右，顺时针为正）。颜色不归引擎管：引擎只管运动与寿命，
/// 画成什么颜色由界面层（<c>ParticleOverlay</c>）决定，这样纯逻辑才能无头自检。</para>
///
/// <para><see cref="Age"/> / <see cref="LifeSpan"/> 的单位是「60fps 基准帧」，不是真实毫秒：
/// 高刷屏一帧只算 0.42 个基准帧，所以速度、寿命、淡出都不会随刷新率变化
/// （早期版本 直接按帧累加，144Hz 屏上速度会快 2.4 倍）。</para>
/// </summary>
public sealed class Particle
{
    /// <summary>圆心 X（DIP）。</summary>
    public double X { get; internal set; }

    /// <summary>圆心 Y（DIP）。</summary>
    public double Y { get; internal set; }

    /// <summary>飞出方向（弧度，0~2π）。</summary>
    public double Angle { get; internal set; }

    /// <summary>自身速度系数（1~3）；真正的位移还要乘引擎的全局速度与帧步长。</summary>
    public double Speed { get; internal set; }

    /// <summary>
    /// 颜色混合系数 0~1（生成时随机）：界面层用它在「颜色 1 → 颜色 2」之间取色。
    /// 0 = 颜色 1，1 = 颜色 2；只启用一种颜色时界面层直接忽略它。引擎不管具体颜色，只负责给一个随机值。
    /// </summary>
    public double ColorMix { get; internal set; }

    /// <summary>已存活时长（60fps 基准帧）。</summary>
    public double Age { get; internal set; }

    /// <summary>寿命（60fps 基准帧，生成时取 30~89）。</summary>
    public double LifeSpan { get; internal set; }

    /// <summary>
    /// 这个粒子的初始直径（DIP）。默认 <see cref="ParticleEngine.BaseSize"/>（拖尾粒子 5px）；
    /// 点击爆炸引擎会用更大的值（3.5~8px，随机），让两种效果一眼能区分。
    /// </summary>
    public double BaseSize { get; internal set; } = ParticleEngine.BaseSize;

    /// <summary>存活进度 0~1（0 = 刚生成，1 = 该消失了）。</summary>
    public double Progress => LifeSpan <= 0 ? 1 : Math.Clamp(Age / LifeSpan, 0, 1);

    /// <summary>当前直径（DIP）：从 <see cref="BaseSize"/> 线性缩到 0。</summary>
    public double Size => BaseSize * (1 - Progress);

    /// <summary>当前不透明度 0~1：从 1 线性淡到 0（画的时候自己乘 255 / 查调色板）。</summary>
    public double Alpha => 1 - Progress;
}
