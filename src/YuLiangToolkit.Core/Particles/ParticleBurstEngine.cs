namespace YuLiangToolkit.Core.Particles;

/// <summary>
/// 点击「粒子爆炸」的纯逻辑引擎：点一下往四周炸开一簇粒子，粒子边飞边减速、缩小、淡出后消失。
///
/// <para><b>为什么单独一个引擎</b>：原来的 <see cref="ParticleEngine"/> 是「自动拖尾」模型 ——
/// 开关关掉就完全不动、点击也不生效。新需求要求「自动效果关掉、点击爆炸照常」，
/// 所以把点击爆发拆成独立引擎：<see cref="Burst"/> 随时可以加粒子，<see cref="Update"/> 只负责推进/回收，
/// 与拖尾引擎互不影响；<c>ParticleOverlay</c> 把两边画在同一套调色板上。</para>
///
/// <para>与拖尾引擎一样，本类不碰 WPF、可注入随机种子，自检直接逐帧断言；
/// 所有时间量都以 60fps 基准帧为单位（高刷屏按真实时间换算，速度/寿命不随刷新率变化），
/// 并有硬上限 <see cref="MaxParticles"/>，不会无限堆积。</para>
/// </summary>
public sealed class ParticleBurstEngine
{
    // ---------------- 行为常量（改了记得同步自检） ----------------

    /// <summary>一次点击默认炸开多少颗。</summary>
    public const int DefaultBurstCount = 24;

    /// <summary>右键（「来更多」）默认炸开多少颗。</summary>
    public const int RightBurstCount = 40;

    /// <summary>粒子总上限（安全阀：到顶就丢弃新粒子）。</summary>
    public const int MaxParticles = 900;

    /// <summary>寿命下限（基准帧）。</summary>
    public const double MinLifeSpan = 20;

    /// <summary>寿命上限（基准帧；随机取 [20, 48)）。</summary>
    public const double MaxLifeSpan = 48;

    /// <summary>爆炸粒子的初始直径范围（DIP）。</summary>
    public const double MinBaseSize = 3.5;

    /// <summary>爆炸粒子的初始直径上限（DIP）。</summary>
    public const double MaxBaseSize = 8.0;

    /// <summary>爆炸粒子的自身速度范围（DIP / 基准帧）。</summary>
    public const double MinSpeed = 2.2;

    /// <summary>爆炸粒子的自身速度上限（DIP / 基准帧）。</summary>
    public const double MaxSpeed = 6.2;

    /// <summary>每基准帧的速度衰减系数（爆炸感：先冲出去再慢下来）。</summary>
    public const double DragPerFrame = 0.985;

    /// <summary>「爆炸范围」倍率的可调下限（主页设置滑块；1.0 = 原来的速度 / 射程）。</summary>
    public const double MinSpeedScale = 0.1;

    /// <summary>「爆炸范围」倍率的可调上限。</summary>
    public const double MaxSpeedScale = 5.0;

    // ---------------- 状态 ----------------

    private readonly List<Particle> _particles = new(256);
    private Random _random;
    private double _speedScale = 1.0;

    /// <summary>用随机种子构造（自检可复现）。</summary>
    public ParticleBurstEngine(int seed)
    {
        _random = new Random(seed);
    }

    /// <summary>正常构造：随机种子。</summary>
    public ParticleBurstEngine() : this(Random.Shared.Next())
    {
    }

    /// <summary>当前存活的爆炸粒子（界面层遍历绘制；元素会被每帧原地更新）。</summary>
    public IReadOnlyList<Particle> Particles => _particles;

    /// <summary>现在有没有粒子（界面层用它决定还要不要继续渲染）。</summary>
    public bool HasParticles => _particles.Count > 0;

    /// <summary>累计生成过多少颗（诊断/自检用）。</summary>
    public int TotalSpawned { get; private set; }

    /// <summary>累计触发了多少次爆炸（诊断/自检用）。</summary>
    public int TotalBursts { get; private set; }

    /// <summary>
    /// 爆炸粒子的速度倍率（主页设置「粒子爆炸范围」滑块）：初始速度和射程一起放大 / 缩小。
    /// 夹在 <see cref="MinSpeedScale"/> ~ <see cref="MaxSpeedScale"/>；只影响之后新炸出的粒子。
    /// </summary>
    public double SpeedScale
    {
        get => _speedScale;
        set => _speedScale = double.IsNaN(value) ? 1.0 : Math.Clamp(value, MinSpeedScale, MaxSpeedScale);
    }

    /// <summary>换随机种子（自检可复现；正常代码不需要调用）。</summary>
    public void Reseed(int seed) => _random = new Random(seed);

    /// <summary>
    /// 在指定位置炸开一簇粒子。返回实际生成数量（受 <see cref="MaxParticles"/> 限制）。
    /// <paramref name="count"/> &lt;= 0 时不生成（但仍计一次爆发，方便诊断）。
    /// </summary>
    public int Burst(double x, double y, int count = DefaultBurstCount)
    {
        TotalBursts++;
        if (count <= 0) return 0;

        var spawned = 0;
        for (var i = 0; i < count; i++)
        {
            if (_particles.Count >= MaxParticles) break;
            _particles.Add(new Particle
            {
                X = x,
                Y = y,
                Angle = _random.NextDouble() * Math.PI * 2,
                Speed = (MinSpeed + _random.NextDouble() * (MaxSpeed - MinSpeed)) * _speedScale,
                ColorMix = _random.NextDouble(),
                Age = 0,
                LifeSpan = MinLifeSpan + _random.NextDouble() * (MaxLifeSpan - MinLifeSpan),
                BaseSize = MinBaseSize + _random.NextDouble() * (MaxBaseSize - MinBaseSize),
            });
            TotalSpawned++;
            spawned++;
        }

        return spawned;
    }

    /// <summary>按 60fps 基准帧推进（界面层按真实经过时间换算后传入）。</summary>
    public void Update(double frameSteps)
    {
        if (double.IsNaN(frameSteps) || frameSteps <= 0) return;
        if (double.IsInfinity(frameSteps)) frameSteps = ParticleEngine.MaxFrameStep;
        if (frameSteps > ParticleEngine.MaxFrameStep) frameSteps = ParticleEngine.MaxFrameStep;

        var drag = Math.Pow(DragPerFrame, frameSteps);
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Age += frameSteps;
            if (particle.Age >= particle.LifeSpan)
            {
                _particles.RemoveAt(i);
                continue;
            }

            particle.Speed *= drag;
            var step = particle.Speed * frameSteps;
            particle.X += Math.Cos(particle.Angle) * step;
            particle.Y += Math.Sin(particle.Angle) * step;
        }
    }

    /// <summary>清空所有爆炸粒子（鼠标离开 / 窗口失活 / 关闭特效时调用）。</summary>
    public void Clear()
    {
        if (_particles.Count > 0) _particles.Clear();
    }
}
