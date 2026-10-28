namespace YuLiangToolkit.Core.Particles;

/// <summary>
/// 鼠标粒子效果的**纯逻辑引擎**：不碰 WPF、不依赖 <c>Application.Current</c> / 渲染线程，
/// 自检可以直接 new 出来逐帧断言。
///
/// <para><b>行为要点</b>：</para>
/// <list type="bullet">
///   <item>每帧从鼠标位置生成粒子，方向随机 0~2π、自身速度 1~3、寿命 <see cref="MinLifeSpan"/>~<see cref="MaxLifeSpan"/>；</item>
///   <item>全局速度默认 <see cref="DefaultSpeed"/>，最大 <see cref="MaxSpeed"/>，每帧衰减 <see cref="SpeedDecayPerFrame"/>；</item>
///   <item>每帧生成数量默认 <see cref="DefaultSpawnPerFrame"/>，最大 <see cref="MaxSpawnPerFrame"/>，每帧衰减 <see cref="SpawnDecayPerFrame"/>；</item>
///   <item>左键 = 速度 + 数量都拉满（<see cref="BoostSpeedAndCount"/>），右键 = 只拉数量（<see cref="BoostCount"/>）；</item>
///   <item>粒子随寿命线性缩小到 0、淡出到透明（<see cref="Particle.Size"/>/<see cref="Particle.Alpha"/> 按需算）；</item>
///   <item>关掉开关就清空、不再生成（<see cref="Active"/> = false）。</item>
/// </list>
///
/// <para><b>两个关键修正</b>：</para>
/// <list type="number">
///   <item><b>高刷屏速度翻倍</b>：按「帧」固定累加时，144Hz 屏上粒子速度快 2.4 倍、寿命也短 2.4 倍。
///   这里所有时间量都以 60fps 为基准帧（<see cref="ReferenceFps"/>），<see cref="Update(double)"/>
///   接真实经过时间换算出来的帧步长，于是「每秒位移 / 每秒生成数 / 存活秒数」与刷新率无关；</item>
///   <item><b>粒子上限</b>：生成数量点满后不能无限堆积，否则会 OOM / GC 抖动。这里硬上限
///   <see cref="MaxParticles"/> 个，到顶就丢弃新粒子，永不无限增长（也不会有 GC 抖动）。</item>
/// </list>
///
/// <para>⚠ 生成是**按时间累加**的（不是「每帧固定几个」）：60fps 时 1 个/帧，144fps 时约每 2.4 帧 1 个，
/// 一秒下来都是 60 个 —— 这才是「每帧生成」在高刷屏上的正确写法，自检里钉住了这一点。</para>
///
/// <para>本类非线程安全：约定只在 UI 线程（<c>ParticleOverlay</c> 的渲染回调）里读写。</para>
/// </summary>
public sealed class ParticleEngine
{
    // ---------------- 行为常量（数值尽量与早期版本一致，改了记得同步改自检） ----------------

    /// <summary>基准帧率：<see cref="Update(double)"/> 的步长 1.0 = 60fps 的一帧。</summary>
    public const double ReferenceFps = 60.0;

    /// <summary>全局速度的「安静值」：鼠标不动时粒子以 自身速度 × 0.15 漂移。</summary>
    public const double DefaultSpeed = 0.15;

    /// <summary>全局速度上限（左键爆发时拉满）。</summary>
    public const double MaxSpeed = 3.0;

    /// <summary>全局速度每基准帧的衰减量（早期版本 <c>SpeedDecay</c>）。</summary>
    public const double SpeedDecayPerFrame = 0.1;

    /// <summary>每基准帧生成的粒子数（安静值）。</summary>
    public const int DefaultSpawnPerFrame = 1;

    /// <summary>每基准帧生成数量的上限（左/右键爆发时拉满）。</summary>
    public const int MaxSpawnPerFrame = 10;

    /// <summary>生成数量每基准帧的衰减量（早期版本 <c>ParticleCountDecay</c>；内部用 double，取整时向下取）。</summary>
    public const double SpawnDecayPerFrame = 0.5;

    /// <summary>寿命下限（基准帧）。</summary>
    public const double MinLifeSpan = 30;

    /// <summary>寿命上限（基准帧；随机取 [30, 90)）。</summary>
    public const double MaxLifeSpan = 90;

    /// <summary>粒子初始直径（DIP，早期版本固定 5px）。</summary>
    public const double BaseSize = 5.0;

    /// <summary>粒子上限（安全阀：到顶就不再生成，永不无限增长）。</summary>
    public const int MaxParticles = 600;

    /// <summary>单次 <see cref="Update(double)"/> 最多推进多少基准帧（防止卡顿/最小化恢复后粒子瞬移一大段）。</summary>
    public const double MaxFrameStep = 4.0;

    // ---------------- 状态 ----------------

    private readonly List<Particle> _particles = new(256);
    private Random _random;
    private bool _active;

    private double _speed = DefaultSpeed;
    private double _count = DefaultSpawnPerFrame;

    /// <summary>生成累加器：不足 1 个的零头留到下一帧（保证高刷屏生成速率不变）。</summary>
    private double _pendingSpawn;

    /// <summary>用随机种子构造（自检用：同样的种子 + 同样的输入 = 完全一样的结果）。</summary>
    public ParticleEngine(int seed)
    {
        _random = new Random(seed);
    }

    /// <summary>正常构造：种子随机（同一秒反复 new 也不会重样）。</summary>
    public ParticleEngine() : this(Random.Shared.Next())
    {
    }

    // ---------------- 公开状态 ----------------

    /// <summary>
    /// 是否启用。关掉时**立即清空**所有粒子，且 <see cref="Update(double)"/> 不再生成；
    /// 再打开后从干净状态继续（速度/数量回到默认值，不会残留上一轮的爆发）。
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            if (!value) Clear();
            else ResetBurst();
        }
    }

    /// <summary>当前全局速度（只读；介于 <see cref="DefaultSpeed"/> 与 <see cref="MaxSpeed"/> 之间）。</summary>
    public double Speed => _speed;

    /// <summary>当前每基准帧生成数量（只读；介于 <see cref="DefaultSpawnPerFrame"/> 与 <see cref="MaxSpawnPerFrame"/> 之间）。</summary>
    public double Count => _count;

    /// <summary>发射点 X（鼠标坐标，控件坐标）。</summary>
    public double EmitterX { get; set; }

    /// <summary>发射点 Y（鼠标坐标，控件坐标）。</summary>
    public double EmitterY { get; set; }

    /// <summary>
    /// 当前所有存活粒子的**只读视图**（元素本身是活的、每帧原地更新，界面层直接遍历它画就行）。
    /// 不要跨线程读；也不要对返回的列表做增删。
    /// </summary>
    public IReadOnlyList<Particle> Particles => _particles;

    /// <summary>本轮累计生成过多少个粒子（诊断/自检用：验证「高刷屏不会多生成」）。</summary>
    public int TotalSpawned { get; private set; }

    // ---------------- 操作 ----------------

    /// <summary>设置发射点（界面层每次鼠标移动调用）。</summary>
    public void SetEmitter(double x, double y)
    {
        EmitterX = x;
        EmitterY = y;
    }

    /// <summary>清空所有粒子与生成累加器（开关、离开窗口、失活时调用）；速度/数量等状态保留。</summary>
    public void Clear()
    {
        if (_particles.Count > 0) _particles.Clear();
        _pendingSpawn = 0;
    }

    /// <summary>复位到初始状态：清空粒子 + 速度/数量回到默认 + 发射点归零（开关重新打开时用）。</summary>
    public void Reset()
    {
        Clear();
        ResetBurst();
        EmitterX = 0;
        EmitterY = 0;
    }

    /// <summary>换随机种子（自检可复现；正常代码不需要调用）。</summary>
    public void Reseed(int seed) => _random = new Random(seed);

    /// <summary>左键爆发：全局速度、每帧数量都拉到最大（等价早期版本 <c>TriggerParticleSpeedBoost</c> + <c>TriggerParticleCountBoost</c>）。</summary>
    public void BoostSpeedAndCount()
    {
        if (!_active) return;   // 与早期版本一致：开关关着时点击不生效
        _speed = MaxSpeed;
        _count = MaxSpawnPerFrame;
    }

    /// <summary>右键爆发：只把每帧数量拉到最大（等价早期版本 <c>TriggerParticleCountBoost</c>）。</summary>
    public void BoostCount()
    {
        if (!_active) return;
        _count = MaxSpawnPerFrame;
    }

    /// <summary>按 60fps 基准帧推进一帧（等价 <c>Update(1.0)</c>）。</summary>
    public void Update() => Update(1.0);

    /// <summary>
    /// 推进一帧。<paramref name="frameSteps"/> 是「这一帧相当于几个 60fps 基准帧」：
    /// 界面层用 <c>经过秒数 × 60</c> 算出来，于是 144Hz 屏每帧约 0.42、60Hz 屏为 1.0，速度/寿命/生成率都一致。
    ///
    /// <para>顺序与早期版本一致：先衰减全局速度/数量 → 生成新粒子 → 再统一更新位置与寿命（所以刚生成的
    /// 粒子当帧也会先老 1 帧、动 1 帧）。</para>
    /// </summary>
    /// <param name="frameSteps">基准帧步长；&lt;=0 / NaN 视为不动，超大值按 <see cref="MaxFrameStep"/> 截断。</param>
    public void Update(double frameSteps)
    {
        if (!_active)
        {
            // 与早期版本一致：未启用时把残留粒子清掉（正常路径在 Active 的 setter 里就清了，这里是兜底）
            if (_particles.Count > 0) Clear();
            return;
        }

        if (double.IsNaN(frameSteps) || frameSteps <= 0) return;
        if (double.IsInfinity(frameSteps)) frameSteps = MaxFrameStep;
        if (frameSteps > MaxFrameStep) frameSteps = MaxFrameStep;

        // 1) 全局速度衰减（只在爆发后 > 默认值时才衰减，安静时恒为 0.15）
        if (_speed > DefaultSpeed)
        {
            _speed -= SpeedDecayPerFrame * frameSteps;
            if (_speed < DefaultSpeed) _speed = DefaultSpeed;
        }

        // 2) 生成数量衰减（double 递减、取整时向下取整 —— 与早期版本「-0.5 再 (int) 截断」等效）
        if (_count > DefaultSpawnPerFrame)
        {
            _count -= SpawnDecayPerFrame * frameSteps;
            if (_count < DefaultSpawnPerFrame) _count = DefaultSpawnPerFrame;
        }

        // 3) 按时间累加生成（不足 1 个的零头留到下一帧；高刷屏不会因此多生成）
        _pendingSpawn += _count * frameSteps;
        if (_pendingSpawn >= 1)
        {
            var spawn = (int)_pendingSpawn;
            _pendingSpawn -= spawn;
            for (var i = 0; i < spawn; i++)
            {
                if (_particles.Count >= MaxParticles) break;   // 安全阀：到顶就丢弃，不再增长
                SpawnOne();
            }
        }

        // 4) 更新所有粒子（倒序方便顺手删除；新粒子也包含在内）
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];
            particle.Age += frameSteps;

            if (particle.Age >= particle.LifeSpan)
            {
                _particles.RemoveAt(i);
                continue;
            }

            // 位移 = 自身速度 × 全局速度 × 帧步长（高刷屏步长小，一秒下来的总位移相同）
            var step = particle.Speed * _speed * frameSteps;
            particle.X += Math.Cos(particle.Angle) * step;
            particle.Y += Math.Sin(particle.Angle) * step;
        }
    }

    // ---------------- 内部 ----------------

    private void SpawnOne()
    {
        _particles.Add(new Particle
        {
            X = EmitterX,
            Y = EmitterY,
            Angle = _random.NextDouble() * Math.PI * 2,
            Speed = 1 + _random.NextDouble() * 2,          // 早期版本：random 1~3
            ColorMix = _random.NextDouble(),                // 0~1：界面层用来在两种自定义颜色之间取色
            Age = 0,
            LifeSpan = _random.Next((int)MinLifeSpan, (int)MaxLifeSpan),   // 早期版本：30~89
        });
        TotalSpawned++;
    }

    /// <summary>速度与数量回到默认值（不动粒子列表）。</summary>
    private void ResetBurst()
    {
        _speed = DefaultSpeed;
        _count = DefaultSpawnPerFrame;
    }
}
