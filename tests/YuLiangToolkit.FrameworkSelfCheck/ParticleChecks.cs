using System.Windows.Media;
using YuLiangToolkit.Core.Particles;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「鼠标粒子效果」的纯逻辑自检：<see cref="ParticleEngine"/> 不碰 WPF、不依赖 <c>Application.Current</c>，
/// 所以这里直接 new 出来逐帧断言，**不需要 STA / 不开窗口**（界面那层由 <c>ParticleOverlay</c> 负责，
/// 它的绘制/事件挂摘用 <c>YLT_PARTICLE_DEMO=1</c> + <c>YLT_SHOT</c> 截图冒烟验收）。
///
/// <para>钉住的是「早期版本行为对齐 + 早期版本两个毛病已修」：</para>
/// <list type="bullet">
///   <item>对齐：每帧从鼠标位置生成、随机方向 0~2π、自身速度 1~3、寿命 30~89、左键速度+数量爆发、
///   右键只加数量、爆发后逐帧衰减回默认、开关关掉立即清空且不再生成；</item>
///   <item>修复①：高刷屏不再加速 —— 帧步长按 60fps 基准帧归一（60 帧 vs 144 半帧，一秒内生成数一致、位移一致）；</item>
///   <item>修复②：粒子上限 <see cref="ParticleEngine.MaxParticles"/>，压力下也永不无限增长。</item>
/// </list>
///
/// <para>⚠ 本文件**只读**引擎状态（<see cref="ParticleEngine.Particles"/> 等），不做任何 UI 操作。</para>
/// </summary>
internal static class ParticleChecks
{
    /// <summary>自检的断言回调：明细可以省（<c>Check("名字", ok)</c>），与其它 Checks 文件兼容。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;   // 本组断言都有明细，用不到"无明细"那条回调

        void Check(string name, bool ok, string detail = "") => check("[粒子] " + name, ok, detail);

        CheckDefaultState(Check);
        CheckTunable(Check);
        CheckSpawnAndMotion(Check);
        CheckLifetime(Check);
        CheckBoosts(Check);
        CheckActiveSwitch(Check);
        CheckFrameStep(Check);
        CheckCap(Check);
        CheckDeterminism(Check);
        CheckColors(Check);
        CheckRobustness(Check);
    }

    /// <summary>造一个"已启动、发射点在 (320,240)"的确定引擎。</summary>
    private static ParticleEngine Running(int seed, double x = 320, double y = 240)
    {
        var engine = new ParticleEngine(seed) { Active = true };
        engine.SetEmitter(x, y);
        return engine;
    }

    // ---------------------------------------------------------------- 初始状态

    private static void CheckDefaultState(CheckFn check)
    {
        var engine = new ParticleEngine(seed: 20241024);

        check("默认未启用、没有任何粒子",
            !engine.Active && engine.Particles.Count == 0 && engine.TotalSpawned == 0);

        check("默认参数对齐早期版本：全局速度 0.15、每帧 1 个",
            Math.Abs(engine.Speed - ParticleEngine.DefaultSpeed) < 1e-9
            && Math.Abs(engine.Count - ParticleEngine.DefaultSpawnPerFrame) < 1e-9,
            $"速度={engine.Speed:0.###}，数量={engine.Count:0.###}");

        // 未启用时 Update 必须什么都不生成（早期版本 IsParticleEffectEnabled=false 时也走这条路）
        engine.SetEmitter(100, 100);
        engine.Update();
        engine.Update(1.0);
        check("未启用时 Update 不生成任何粒子", engine.Particles.Count == 0 && engine.TotalSpawned == 0);

        // 未启用时 boost 不生效（早期版本 TriggerXxxBoost 里也先判 IsParticleEffectEnabled）
        engine.BoostSpeedAndCount();
        engine.BoostCount();
        check("未启用时左/右键爆发不生效",
            Math.Abs(engine.Speed - ParticleEngine.DefaultSpeed) < 1e-9
            && Math.Abs(engine.Count - ParticleEngine.DefaultSpawnPerFrame) < 1e-9);
    }

    // ---------------------------------------------------------------- 可调参数（v0.37.0 滑块）

    /// <summary>主页设置里的「粒子拖尾数量 / 大小」滑块：默认值保持早期行为，改值立即生效、有上下限。</summary>
    private static void CheckTunable(CheckFn check)
    {
        var engine = Running(seed: 20261030);
        check("可调参数默认值 = 早期行为（安静 1 个/帧、大小 1.0×）",
            Math.Abs(engine.BaseSpawn - ParticleEngine.DefaultSpawnPerFrame) < 1e-9
            && Math.Abs(engine.SizeScale - 1) < 1e-9);

        engine.BaseSpawn = 6;
        engine.Update();
        check("拖尾数量滑块：改成 6 个/帧后当帧生成 6 个",
            Math.Abs(engine.Count - 6) < 1e-9 && engine.TotalSpawned == 6,
            $"实际 {engine.TotalSpawned} 个");

        engine.BoostCount();
        check("数量滑块低于爆发上限时，爆发照旧拉满 10",
            Math.Abs(engine.Count - ParticleEngine.MaxSpawnPerFrame) < 1e-9);
        for (var i = 0; i < 40; i++) engine.Update();
        check("爆发后数量衰减回滑块设定的基准值 6",
            Math.Abs(engine.Count - 6) < 1e-9, $"实际 {engine.Count:0.##}");

        var half = Running(seed: 20261034);
        half.BaseSpawn = 6;
        half.Update(0.5);
        check("数量滑块与帧步长无关（半帧生成 3 个）", half.TotalSpawned == 3);

        var high = Running(seed: 20261031);
        high.BaseSpawn = 20;
        high.BoostCount();
        check("数量滑块高于爆发上限时，爆发不会把数量压回去",
            Math.Abs(high.Count - 20) < 1e-9);

        var sized = Running(seed: 20261032);
        sized.SizeScale = 1.5;
        sized.Update();
        check("拖尾大小滑块：新粒子初始直径 = 5 × 1.5",
            Math.Abs(sized.Particles[0].BaseSize - ParticleEngine.BaseSize * 1.5) < 1e-9,
            $"{sized.Particles[0].BaseSize:0.##}px");

        var clampedHigh = Running(seed: 20261033);
        clampedHigh.BaseSpawn = 1000;
        clampedHigh.SizeScale = 1000;
        check("拖尾数量 / 大小有上下限（滑块再大也不会失控）",
            Math.Abs(clampedHigh.BaseSpawn - ParticleEngine.MaxBaseSpawn) < 1e-9
            && Math.Abs(clampedHigh.SizeScale - ParticleEngine.MaxSizeScale) < 1e-9);

        clampedHigh.BaseSpawn = double.NaN;
        clampedHigh.SizeScale = double.NaN;
        check("拖尾数量 / 大小 NaN 回到默认值",
            Math.Abs(clampedHigh.BaseSpawn - ParticleEngine.DefaultSpawnPerFrame) < 1e-9
            && Math.Abs(clampedHigh.SizeScale - 1) < 1e-9);
    }

    // ---------------------------------------------------------------- 生成 / 运动 / 外观

    private static void CheckSpawnAndMotion(CheckFn check)
    {
        var engine = Running(seed: 1, x: 100, y: 100);
        engine.Update();

        check("启用后一帧从发射点生成 1 个粒子（默认 1 个/帧）",
            engine.Particles.Count == 1 && engine.TotalSpawned == 1);

        var particle = engine.Particles[0];

        // 生成当帧就会先老 1 帧、动 1 帧（与早期版本"创建后马上进入更新循环"一致）
        var distanceFromEmitter = Math.Sqrt(
            (particle.X - 100) * (particle.X - 100) + (particle.Y - 100) * (particle.Y - 100));
        check("生成位置≈发射点（当帧漂移最多 3×0.15=0.45px）", distanceFromEmitter <= 0.5,
            $"距发射点 {distanceFromEmitter:0.###}px");

        check("随机参数都在早期版本范围：角度 0~2π、自身速度 1~3、寿命 30~89",
            particle.Angle >= 0 && particle.Angle < Math.PI * 2
            && particle.Speed >= 1 && particle.Speed <= 3
            && particle.LifeSpan >= ParticleEngine.MinLifeSpan && particle.LifeSpan < ParticleEngine.MaxLifeSpan,
            $"角度={particle.Angle:0.###}，速度={particle.Speed:0.###}，寿命={particle.LifeSpan}");

        check("刚生成：几乎满尺寸（≈5px）、几乎不透明",
            particle.Size > 4.5 && particle.Size <= 5
            && particle.Alpha > 0.9 && particle.Alpha <= 1,
            $"大小={particle.Size:0.###}px，透明度={particle.Alpha:0.###}");

        // 安静状态下的位移 = 自身速度 × 0.15
        var beforeX = particle.X;
        var beforeY = particle.Y;
        engine.Update();
        var moved = Math.Sqrt((particle.X - beforeX) * (particle.X - beforeX)
                              + (particle.Y - beforeY) * (particle.Y - beforeY));
        check("安静时每帧位移 = 自身速度 × 0.15（0.15~0.45px）", moved > 0.1 && moved <= 0.5,
            $"位移 {moved:0.###}px");

        // 发射点跟随鼠标：改发射点后新粒子从新位置出来
        engine.SetEmitter(500, 300);
        engine.Update();
        var newest = engine.Particles[^1];
        var distanceFromNewEmitter = Math.Sqrt(
            (newest.X - 500) * (newest.X - 500) + (newest.Y - 300) * (newest.Y - 300));
        check("发射点跟随鼠标（SetEmitter 后新粒子从新位置飞出）", distanceFromNewEmitter <= 0.5,
            $"距新发射点 {distanceFromNewEmitter:0.###}px");

        // 所有存活粒子的数值必须始终合法（防止 NaN 把界面画崩）
        check("所有存活粒子数值合法（坐标有限、大小/透明度在 0~1 范围）",
            engine.Particles.All(p =>
                !double.IsNaN(p.X) && !double.IsInfinity(p.X)
                && !double.IsNaN(p.Y) && !double.IsInfinity(p.Y)
                && p.Size > 0 && p.Size <= ParticleEngine.BaseSize
                && p.Alpha > 0 && p.Alpha <= 1));
    }

    // ---------------------------------------------------------------- 寿命

    private static void CheckLifetime(CheckFn check)
    {
        var engine = Running(seed: 7);
        engine.Update();
        var doomed = engine.Particles[0];
        var lifeSpan = doomed.LifeSpan;

        var frames = 0;
        while (engine.Particles.Contains(doomed) && frames < 500)
        {
            engine.Update();
            frames++;
        }

        check("寿命到了就被移出列表（不留在数组里慢慢攒）",
            !engine.Particles.Contains(doomed),
            $"寿命 {lifeSpan} 帧，实际 {frames + 1} 帧后消失");

        check("移除时机≈寿命（误差 ≤1 帧）", Math.Abs(frames + 1 - lifeSpan) <= 1,
            $"期望 {lifeSpan} 帧，实际 {frames + 1} 帧");

        // 连续生成时，任何时刻存活数都远小于"历史生成总数"——说明真的在回收
        var running = Running(seed: 8);
        for (var i = 0; i < 240; i++) running.Update();
        check("持续运行会回收旧粒子（存活数 ≤ 生成总数，不会只增不减）",
            running.TotalSpawned == 240 && running.Particles.Count > 0
            && running.Particles.Count < running.TotalSpawned,
            $"生成 {running.TotalSpawned}，当前存活 {running.Particles.Count}");
    }

    // ---------------------------------------------------------------- 爆发（左键 / 右键）

    private static void CheckBoosts(CheckFn check)
    {
        // ---- 左键：速度 + 数量拉满 ----
        var left = Running(seed: 11);
        left.BoostSpeedAndCount();
        check("左键爆发：全局速度拉满 3、每帧数量拉满 10",
            Math.Abs(left.Speed - ParticleEngine.MaxSpeed) < 1e-9
            && Math.Abs(left.Count - ParticleEngine.MaxSpawnPerFrame) < 1e-9,
            $"速度={left.Speed:0.###}，数量={left.Count:0.###}");

        // 当帧生成个数：10 先按每帧 -0.5 衰减成 9.5，再向下取整 = 9（与早期版本 (int) 截断一致）
        left.Update();
        check("数量爆发当帧生成 9 个（10 先衰减 0.5 再向下取整，与早期版本一致）",
            left.TotalSpawned == 9, $"实际 {left.TotalSpawned} 个");

        // ---- 右键：只加数量，不动速度 ----
        var right = Running(seed: 12);
        right.BoostCount();
        check("右键爆发：只把数量拉满，全局速度保持默认",
            Math.Abs(right.Count - ParticleEngine.MaxSpawnPerFrame) < 1e-9
            && Math.Abs(right.Speed - ParticleEngine.DefaultSpeed) < 1e-9);

        // ---- 爆发真的让粒子飞得更快（用同一个随机种子对比同一个粒子） ----
        var calm = Running(seed: 13);
        calm.Update();
        var calmParticle = calm.Particles[0];
        var calmX = calmParticle.X;
        var calmY = calmParticle.Y;
        calm.Update();
        var calmStep = Math.Sqrt((calmParticle.X - calmX) * (calmParticle.X - calmX)
                                 + (calmParticle.Y - calmY) * (calmParticle.Y - calmY));

        var burst = Running(seed: 13);
        burst.Update();
        var burstParticle = burst.Particles[0];   // 同种子 → 同一个粒子（同角度/同速度）
        var burstX = burstParticle.X;
        var burstY = burstParticle.Y;
        burst.BoostSpeedAndCount();
        burst.Update();
        var burstStep = Math.Sqrt((burstParticle.X - burstX) * (burstParticle.X - burstX)
                                  + (burstParticle.Y - burstY) * (burstParticle.Y - burstY));

        check("左键后同一个粒子飞得快得多（速度爆发真的生效）",
            calmStep > 0 && burstStep > calmStep * 10,
            $"安静 {calmStep:0.###}px/帧 → 爆发 {burstStep:0.###}px/帧");

        // ---- 两种爆发都会在约 0.5 秒内衰减回默认 ----
        var decay = Running(seed: 14);
        decay.BoostSpeedAndCount();
        for (var i = 0; i < 40; i++) decay.Update();
        check("爆发后在约 0.5 秒内衰减回默认（速度 0.15 / 数量 1）",
            Math.Abs(decay.Speed - ParticleEngine.DefaultSpeed) < 1e-9
            && Math.Abs(decay.Count - ParticleEngine.DefaultSpawnPerFrame) < 1e-9,
            $"速度={decay.Speed:0.###}，数量={decay.Count:0.###}");
    }

    // ---------------------------------------------------------------- 开关

    private static void CheckActiveSwitch(CheckFn check)
    {
        var engine = Running(seed: 21);
        engine.Update();
        engine.Update();
        var alive = engine.Particles.Count;
        var spawnedBefore = engine.TotalSpawned;

        engine.Active = false;
        check("IsActive=false：立即清空现存粒子", alive > 0 && engine.Particles.Count == 0,
            $"之前 {alive} 个 → 现在 {engine.Particles.Count} 个");

        engine.Update();
        engine.Update(3.0);
        check("关掉之后不再生成（Update 多少次都没用）",
            engine.Particles.Count == 0 && engine.TotalSpawned == spawnedBefore);

        // 重新打开：从干净状态继续
        engine.Active = true;
        engine.Update();
        check("重新打开后恢复生成，且从干净状态开始",
            engine.Particles.Count == 1 && engine.TotalSpawned == spawnedBefore + 1
            && Math.Abs(engine.Speed - ParticleEngine.DefaultSpeed) < 1e-9
            && Math.Abs(engine.Count - ParticleEngine.DefaultSpawnPerFrame) < 1e-9);

        // Clear：只清粒子，不影响开关
        engine.Update();
        var beforeClear = engine.TotalSpawned;
        engine.Clear();
        check("Clear：清空粒子但保持启用状态", engine.Particles.Count == 0 && engine.Active);
        engine.Update();
        check("Clear 后还能继续生成", engine.Particles.Count == 1 && engine.TotalSpawned == beforeClear + 1);

        // Reset：清空 + 参数回默认 + 发射点归零
        engine.BoostSpeedAndCount();
        engine.Reset();
        check("Reset：速度/数量回默认、发射点归零、粒子清空（开关不变）",
            engine.Particles.Count == 0 && engine.Active
            && Math.Abs(engine.Speed - ParticleEngine.DefaultSpeed) < 1e-9
            && Math.Abs(engine.Count - ParticleEngine.DefaultSpawnPerFrame) < 1e-9
            && engine.EmitterX == 0 && engine.EmitterY == 0);
    }

    // ---------------------------------------------------------------- 帧步进（高刷屏修复）

    private static void CheckFrameStep(CheckFn check)
    {
        // ---- Update() 必须等价于 Update(1.0) ----
        var plainUpdate = Running(seed: 31);
        plainUpdate.Update();
        var explicitUpdate = Running(seed: 31);
        explicitUpdate.Update(1.0);
        check("Update() 等价于 Update(1)（1 帧 = 60fps 基准帧）",
            plainUpdate.TotalSpawned == explicitUpdate.TotalSpawned
            && plainUpdate.Particles.Count == explicitUpdate.Particles.Count
            && plainUpdate.Particles.Zip(explicitUpdate.Particles)
                .All(pair => pair.First.X == pair.Second.X && pair.First.Y == pair.Second.Y));

        // ---- 一次 Update(2) 的位移 = 两次 Update(1) 的位移（帧步长真的被乘进位移） ----
        var twoFrames = Running(seed: 32);
        twoFrames.Update();
        var traced = twoFrames.Particles[0];
        var startX = traced.X;
        var startY = traced.Y;
        twoFrames.Update();
        var oneStep = Math.Sqrt((traced.X - startX) * (traced.X - startX)
                                + (traced.Y - startY) * (traced.Y - startY));

        var doubled = Running(seed: 32);
        doubled.Update();
        var doubledParticle = doubled.Particles[0];
        var doubledStartX = doubledParticle.X;
        var doubledStartY = doubledParticle.Y;
        doubled.Update(2.0);
        var twoStep = Math.Sqrt((doubledParticle.X - doubledStartX) * (doubledParticle.X - doubledStartX)
                                + (doubledParticle.Y - doubledStartY) * (doubledParticle.Y - doubledStartY));

        check("帧步长归一：Update(2) 的位移 ≈ 2 × Update(1) 的位移",
            oneStep > 0 && Math.Abs(twoStep - oneStep * 2) < 1e-6,
            $"1 帧 {oneStep:0.####}px vs 2 帧 {twoStep:0.####}px");

        // ---- 不足一帧的零头留到下一帧（不是每帧硬塞一个） ----
        var half = Running(seed: 33);
        half.Update(0.5);
        check("半个基准帧还不够生成一个（零头留到下一帧）",
            half.TotalSpawned == 0 && half.Particles.Count == 0);
        half.Update(0.5);
        check("两个半帧凑够一帧 → 生成 1 个（生成速率与刷新率无关）",
            half.TotalSpawned == 1, $"实际 {half.TotalSpawned} 个");

        // ---- 60fps（60 × dt=1）vs 144fps（144 × dt=0.4167）：一秒内生成数一致、不会多 2.4 倍 ----
        var fps60 = Running(seed: 34);
        for (var i = 0; i < 60; i++) fps60.Update();

        var fps144 = Running(seed: 34);
        for (var i = 0; i < 144; i++) fps144.Update(60.0 / 144.0);

        check("刷新率无关：1 秒内生成总数一致（60fps vs 144fps）",
            Math.Abs(fps60.TotalSpawned - fps144.TotalSpawned) <= 2,
            $"60fps 生成 {fps60.TotalSpawned} 个，144fps 生成 {fps144.TotalSpawned} 个");

        check("高刷屏没有把生成数放大 2.4 倍（早期版本的病）",
            fps144.TotalSpawned < 70,
            $"144fps 一秒生成 {fps144.TotalSpawned} 个（早期版本会是 144 个）");

        check("刷新率无关：1 秒后存活粒子数接近",
            Math.Abs(fps60.Particles.Count - fps144.Particles.Count) <= 5,
            $"60fps 存活 {fps60.Particles.Count} 个，144fps 存活 {fps144.Particles.Count} 个");
    }

    // ---------------------------------------------------------------- 上限

    private static void CheckCap(CheckFn check)
    {
        var engine = Running(seed: 31415);

        var peak = 0;
        for (var i = 0; i < 400; i++)
        {
            engine.BoostCount();   // 每帧都点右键，制造最高生成压力
            engine.Update();
            if (engine.Particles.Count > peak) peak = engine.Particles.Count;
        }

        check("粒子上限：任何时刻都不会超过 600 个（永不无限增长）",
            peak <= ParticleEngine.MaxParticles,
            $"峰值 {peak} 个，上限 {ParticleEngine.MaxParticles} 个");

        check("压力测试确实堆出了很多粒子（上限不是靠少生成「作弊」的）",
            engine.TotalSpawned > 1000 && peak > 200,
            $"累计生成 {engine.TotalSpawned} 个，峰值存活 {peak} 个");
    }

    // ---------------------------------------------------------------- 确定性随机

    private static void CheckDeterminism(CheckFn check)
    {
        ParticleEngine Build(int seed)
        {
            var engine = new ParticleEngine(seed) { Active = true };
            engine.SetEmitter(640, 360);
            return engine;
        }

        var first = Build(42);
        var second = Build(42);
        for (var i = 0; i < 60; i++)
        {
            first.Update();
            second.Update();
        }

        var reproducible = first.TotalSpawned == second.TotalSpawned
                           && first.Particles.Count == second.Particles.Count
                           && first.Particles.Zip(second.Particles).All(pair =>
                               pair.First.X == pair.Second.X
                               && pair.First.Y == pair.Second.Y
                               && pair.First.Angle == pair.Second.Angle
                               && pair.First.Speed == pair.Second.Speed
                               && pair.First.Age == pair.Second.Age
                               && pair.First.LifeSpan == pair.Second.LifeSpan);

        check("相同随机种子 + 相同输入 → 完全可复现（自检/回放靠它）", reproducible,
            $"生成 {first.TotalSpawned} vs {second.TotalSpawned}，存活 {first.Particles.Count} vs {second.Particles.Count}");

        var other = Build(43);
        for (var i = 0; i < 60; i++) other.Update();
        var differs = other.TotalSpawned != first.TotalSpawned
                      || other.Particles.Count != first.Particles.Count
                      || other.Particles.Zip(first.Particles).Any(pair =>
                          pair.First.X != pair.Second.X || pair.First.Speed != pair.Second.Speed);
        check("不同随机种子 → 结果不同（随机方向/寿命真的随机）", differs);
    }

    // ---------------------------------------------------------------- 颜色

    /// <summary>自定义颜色：粒子带 0~1 的随机混色系数，且两种颜色的插值数学正确（v0.22.0）。</summary>
    private static void CheckColors(CheckFn check)
    {
        var engine = Running(seed: 9);
        for (var i = 0; i < 300; i++) engine.Update();

        check("每个粒子都带 0~1 的随机颜色混合系数",
            engine.Particles.Count > 0 && engine.Particles.All(p => p.ColorMix >= 0 && p.ColorMix <= 1));
        var minMix = engine.Particles.Min(p => p.ColorMix);
        var maxMix = engine.Particles.Max(p => p.ColorMix);
        check("颜色混合系数真的随机分布（不是全 0 / 全 1）",
            minMix < 0.2 && maxMix > 0.8, $"范围 {minMix:0.###} ~ {maxMix:0.###}");

        var first = Color.FromRgb(0x10, 0x20, 0x30);
        var second = Color.FromRgb(0xF0, 0xA0, 0x60);
        check("颜色插值：0 = 颜色 1，1 = 颜色 2",
            ParticleColorRange.Lerp(first, second, 0) == first
            && ParticleColorRange.Lerp(first, second, 1) == second);
        var mid = ParticleColorRange.Lerp(first, second, 0.5);
        check("颜色插值：0.5 = 两色每个通道的中点",
            mid.R == 0x80 && mid.G == 0x60 && mid.B == 0x48,
            $"#{mid.R:X2}{mid.G:X2}{mid.B:X2}");
        check("颜色插值：越界 / NaN 会被夹住（不会出现非法颜色）",
            ParticleColorRange.Lerp(first, second, -1) == first
            && ParticleColorRange.Lerp(first, second, 2) == second
            && ParticleColorRange.Lerp(first, second, double.NaN) == first);
        check("只启用一种颜色时固定返回那一种（不做随机）",
            ParticleColorRange.Resolve(0.9, first, second, onlyOne: true) == first
            && ParticleColorRange.Resolve(0.1, second, first, onlyOne: true) == second);
    }

    // ---------------------------------------------------------------- 健壮性

    private static void CheckRobustness(CheckFn check)
    {
        var engine = Running(seed: 55);

        var threw = false;
        try
        {
            engine.Update(0);
            engine.Update(-5);
            engine.Update(double.NaN);
            engine.Update(double.PositiveInfinity);
            engine.Update(10000);
            engine.Update();
        }
        catch (Exception ex)
        {
            threw = true;
            check("异常帧步长（0 / 负数 / NaN / 超大）不会抛异常", false, ex.GetType().Name);
        }
        if (!threw)
            check("异常帧步长（0 / 负数 / NaN / 超大）不会抛异常，且之后还能正常跑",
                engine.Particles.Count > 0 && engine.Particles.All(p => !double.IsNaN(p.X) && !double.IsNaN(p.Y)),
                $"存活 {engine.Particles.Count} 个");

        // 空引擎 Update / 未设置发射点也不该崩（覆盖层加载后第一帧可能还没收到鼠标事件）
        var fresh = new ParticleEngine(seed: 56) { Active = true };
        var freshThrew = false;
        try
        {
            fresh.Update();
            fresh.Update(0.5);
            fresh.Clear();
            fresh.Update();
        }
        catch
        {
            freshThrew = true;
        }
        check("没设发射点 / 空引擎连续 Update 不崩", !freshThrew);
    }
}
