namespace YuLiangToolkit.Core.Effects;

/// <summary>
/// 一条可调参数的取值范围：<see cref="Minimum"/> ~ <see cref="Maximum"/>，步进 <see cref="Step"/>。
/// 设置页的滑块、配置校验、渲染层都只认这一份数字（别在 XAML / VM 里再写一遍）。
/// </summary>
public readonly record struct CursorEffectRange(double Minimum, double Maximum, double Step)
{
    /// <summary>把值夹进范围（<c>NaN</c> 按下限处理，手改配置写坏也不会让渲染层崩）。</summary>
    public double Clamp(double value)
        => double.IsNaN(value) ? Minimum : Math.Clamp(value, Minimum, Maximum);
}

/// <summary>
/// 光标特效的**可调参数**（v0.37.0，主页设置里每组特效下面的滑块）。
///
/// <para>不可变记录：改一个值就换一个新实例，方便 WPF 依赖属性做比较 / 绑定；
/// <see cref="Normalized"/> 会把越界值夹回合法范围（手改配置文件也不会崩）。
/// 默认值 = v0.36.x 的固定行为（老配置缺这些字段时外观不变）。</para>
///
/// <para>各参数的取值范围 / 步进见 <see cref="TrailCountRange"/> 等静态字段：
/// 设置页的 <c>ShellSliderOption</c> 直接读它们生成滑块，<c>ParticleOverlay</c> 再把值配给
/// 粒子引擎 / 各个渲染器 —— 新增参数时三处（这里 + 设置页 VM + 渲染层）各加一条即可。</para>
/// </summary>
public sealed record CursorEffectTuning(
    double TrailParticleCount = 1.0,
    double TrailParticleSize = 1.0,
    double CursorGlowRadius = 46.0,
    double BurstParticleCount = 24.0,
    double BurstRange = 1.0,
    double DispersionRadius = 98.0,
    double GlowFlashRadius = 64.0,
    double InvertCircleRadius = 150.0,
    bool InvertCircleOverlapCancels = true)
{
    // ---------------- 取值范围（滑块 / 校验共用） ----------------

    /// <summary>粒子拖尾：每基准帧生成数量（个/帧）；0.5 的步进可以调得很细。</summary>
    public static CursorEffectRange TrailCountRange { get; } = new(1.0, 20.0, 0.5);

    /// <summary>粒子拖尾：粒子大小倍率（1.0 = 原来的 5px）。</summary>
    public static CursorEffectRange TrailSizeRange { get; } = new(0.4, 2.5, 0.1);

    /// <summary>鼠标散光：光晕半径（DIP）。</summary>
    public static CursorEffectRange GlowRadiusRange { get; } = new(20.0, 300.0, 1.0);

    /// <summary>粒子爆炸：一次炸开多少颗。</summary>
    public static CursorEffectRange BurstCountRange { get; } = new(4.0, 160.0, 1.0);

    /// <summary>粒子爆炸：扩散范围倍率（1.0 = 原来的速度 / 射程）。</summary>
    public static CursorEffectRange BurstRangeRange { get; } = new(0.3, 3.0, 0.05);

    /// <summary>散光扩散：外圈最大半径（DIP）。</summary>
    public static CursorEffectRange DispersionRadiusRange { get; } = new(20.0, 400.0, 1.0);

    /// <summary>光晕闪烁：光团最大半径（DIP）。</summary>
    public static CursorEffectRange GlowFlashRadiusRange { get; } = new(10.0, 240.0, 1.0);

    /// <summary>反色圆环：最大半径（DIP）。</summary>
    public static CursorEffectRange InvertRadiusRange { get; } = new(20.0, 400.0, 1.0);

    /// <summary>全部参数取默认值（= v0.36.x 的固定外观）。</summary>
    public static CursorEffectTuning Default { get; } = new();

    /// <summary>把每个参数夹回各自的取值范围；<c>NaN</c> 用下限。布尔开关（重叠是否变回正色）原样保留。</summary>
    public CursorEffectTuning Normalized() => new(
        TrailCountRange.Clamp(TrailParticleCount),
        TrailSizeRange.Clamp(TrailParticleSize),
        GlowRadiusRange.Clamp(CursorGlowRadius),
        BurstCountRange.Clamp(BurstParticleCount),
        BurstRangeRange.Clamp(BurstRange),
        DispersionRadiusRange.Clamp(DispersionRadius),
        GlowFlashRadiusRange.Clamp(GlowFlashRadius),
        InvertRadiusRange.Clamp(InvertCircleRadius),
        InvertCircleOverlapCancels);
}
