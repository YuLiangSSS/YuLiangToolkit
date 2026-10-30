using System.IO;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Effects;
using YuLiangToolkit.Core.Particles;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「光标特效（自动效果 / 点击效果）」的纯逻辑自检（v0.26.0，`shell` 分组）。
///
/// <para>覆盖四块，都不需要开窗口：</para>
/// <list type="bullet">
///   <item><b>特效清单</b>（<see cref="CursorEffects"/>）：Id 唯一、说明非空、自动/点击分组正确、
///   默认勾选有效、未知 Id / 错误触发方式的 Id 会被过滤、输出顺序稳定（存盘不会因点击顺序抖动）；</item>
///   <item><b>反色圆环时间线</b>（<see cref="InvertCircleAnimation"/> / <see cref="Easings"/>）：
///   easeOutQuint 的端点与「先快后慢」形状、半径在扩散段单调变大 / 收缩段单调变小、
///   收缩段「先缩得慢、最后缩得快」、最大半径按窗口尺寸夹取；</item>
///   <item><b>可调参数</b>（<see cref="CursorEffectTuning"/> / <see cref="ShellSliderOption"/>，v0.37.0）：
///   默认值 = 旧外观、每条参数范围合法、越界 / NaN 被夹住、滑块吸附 / 回调 / 文案；</item>
///   <item><b>点击爆炸引擎</b>（<see cref="ParticleBurstEngine"/>）：一次爆发数量、从点击点出发、
///   寿命/大小/速度范围、逐帧老化回收、上限、清空、固定种子可复现、右键 = 更多；</item>
///   <item><b>配置</b>：<see cref="ShellConfig"/> 的 <c>autoEffects</c> / <c>clickEffects</c> 数组往返、
///   老配置没有这两个字段时是 <c>null</c>（由外壳按 <c>showParticles</c> 迁移，见 MainViewModel）；</item>
///   <item><b>设置项模型</b>（<see cref="ShellEffectOption"/>）：勾选变化回调一次、初始状态正确。</item>
/// </list>
/// </summary>
internal static class ShellEffectsChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;   // 本组断言都带明细

        void Check(string name, bool ok, string detail = "") => check("[特效] " + name, ok, detail);

        CheckCatalog(Check);
        CheckNormalize(Check);
        CheckInvertAnimation(Check);
        CheckTuning(Check);
        CheckBurstEngine(Check);
        CheckConfigRoundTrip(Check);
        CheckEffectOption(Check);
        CheckSliderOption(Check);
        CheckSettingsLocation(Check);
    }

    // ---------------------------------------------------------------- 可调参数（v0.37.0 滑块）

    private static void CheckTuning(CheckFn check)
    {
        var defaults = CursorEffectTuning.Default;
        check("可调参数默认值 = v0.36.x 的固定外观（老配置升级后外观不变）",
            defaults.TrailParticleCount == 1 && defaults.TrailParticleSize == 1
            && defaults.CursorGlowRadius == 46 && defaults.BurstParticleCount == 24
            && defaults.BurstRange == 1 && defaults.DispersionRadius == 98
            && defaults.GlowFlashRadius == 64 && defaults.InvertCircleRadius == 150);
        check("默认值本身就在各自范围内（Normalized 不会改动默认值）",
            CursorEffectTuning.Default.Normalized() == CursorEffectTuning.Default);
        check("反色圆环「重叠变正色」开关：默认打开，Normalized 原样保留",
            CursorEffectTuning.Default.InvertCircleOverlapCancels
            && !(CursorEffectTuning.Default with { InvertCircleOverlapCancels = false })
                .Normalized().InvertCircleOverlapCancels);

        var ranges = new (string Name, CursorEffectRange Range)[]
        {
            ("拖尾数量", CursorEffectTuning.TrailCountRange),
            ("拖尾大小", CursorEffectTuning.TrailSizeRange),
            ("鼠标散光半径", CursorEffectTuning.GlowRadiusRange),
            ("爆炸数量", CursorEffectTuning.BurstCountRange),
            ("爆炸范围", CursorEffectTuning.BurstRangeRange),
            ("散光扩散半径", CursorEffectTuning.DispersionRadiusRange),
            ("光晕闪烁半径", CursorEffectTuning.GlowFlashRadiusRange),
            ("反色圆环半径", CursorEffectTuning.InvertRadiusRange),
        };
        check("每条参数的范围合法（min < max、步进 > 0）",
            ranges.All(x => x.Range.Minimum < x.Range.Maximum && x.Range.Step > 0));
        check("可调范围够大（滑块能覆盖到旧观感的数倍）",
            CursorEffectTuning.TrailCountRange.Maximum >= 10
            && CursorEffectTuning.BurstCountRange.Maximum >= 100
            && CursorEffectTuning.BurstRangeRange.Maximum >= 2
            && CursorEffectTuning.DispersionRadiusRange.Maximum >= 300
            && CursorEffectTuning.InvertRadiusRange.Maximum >= 300,
            $"数量 {CursorEffectTuning.TrailCountRange.Minimum}~{CursorEffectTuning.TrailCountRange.Maximum}，"
            + $"爆炸 {CursorEffectTuning.BurstCountRange.Minimum}~{CursorEffectTuning.BurstCountRange.Maximum}");

        var huge = new CursorEffectTuning(1e6, 1e6, 1e6, 1e6, 1e6, 1e6, 1e6, 1e6).Normalized();
        var tiny = new CursorEffectTuning(-1e6, -1e6, -1e6, -1e6, -1e6, -1e6, -1e6, -1e6).Normalized();
        var broken = new CursorEffectTuning(double.NaN, double.NaN, double.NaN, double.NaN,
            double.NaN, double.NaN, double.NaN, double.NaN).Normalized();
        check("越界 / NaN 参数被夹回各自范围（手改配置也不会崩）",
            huge.TrailParticleCount == CursorEffectTuning.TrailCountRange.Maximum
            && huge.BurstParticleCount == CursorEffectTuning.BurstCountRange.Maximum
            && huge.InvertCircleRadius == CursorEffectTuning.InvertRadiusRange.Maximum
            && tiny.TrailParticleCount == CursorEffectTuning.TrailCountRange.Minimum
            && tiny.BurstRange == CursorEffectTuning.BurstRangeRange.Minimum
            && broken.GlowFlashRadius == CursorEffectTuning.GlowFlashRadiusRange.Minimum
            && broken.BurstParticleCount == CursorEffectTuning.BurstCountRange.Minimum);
    }

    // ---------------------------------------------------------------- 清单

    private static void CheckCatalog(CheckFn check)
    {
        var all = CursorEffects.All;
        check("特效清单不为空且 Id 唯一",
            all.Count >= 5 && all.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count,
            $"共 {all.Count} 种");
        check("每条特效都有 Id / 显示名 / 说明",
            all.All(x => !string.IsNullOrWhiteSpace(x.Id)
                         && !string.IsNullOrWhiteSpace(x.Name)
                         && !string.IsNullOrWhiteSpace(x.Description)));

        var auto = CursorEffects.For(CursorEffectTrigger.Auto);
        var click = CursorEffects.For(CursorEffectTrigger.Click);
        check("自动效果清单：粒子拖尾 + 鼠标散光",
            auto.Select(x => x.Id).SequenceEqual(new[] { CursorEffects.ParticleTrail, CursorEffects.CursorGlow }));
        check("点击效果清单：粒子爆炸 + 散光扩散 + 光晕闪烁 + 反色圆环",
            click.Select(x => x.Id).SequenceEqual(new[]
            {
                CursorEffects.ParticleBurst, CursorEffects.Dispersion,
                CursorEffects.GlowFlash, CursorEffects.InvertCircle,
            }));
        check("清单按 Order 排序", auto.Zip(auto.Skip(1)).All(pair => pair.First.Order <= pair.Second.Order)
                                   && click.Zip(click.Skip(1)).All(pair => pair.First.Order <= pair.Second.Order));

        var invert = CursorEffects.Find(CursorEffects.InvertCircle);
        check("反色圆环：点击效果、Id 稳定 \"invert-circle\"、显示名 / 说明提到反色",
            invert is { Trigger: CursorEffectTrigger.Click }
            && invert.Id == "invert-circle"
            && invert.Name.Contains("反色")
            && invert.Description.Contains("反色"));

        check("按 Id 找定义（大小写不敏感）",
            CursorEffects.Find("PARTICLE-TRAIL")?.Id == CursorEffects.ParticleTrail
            && CursorEffects.Find(CursorEffects.Dispersion)?.Trigger == CursorEffectTrigger.Click);
        check("找不到 / 空 Id 返回 null",
            CursorEffects.Find("不存在") is null && CursorEffects.Find("") is null && CursorEffects.Find(null) is null);

        check("默认勾选：自动 = 粒子拖尾、点击 = 粒子爆炸",
            CursorEffects.DefaultSelection(CursorEffectTrigger.Auto).SequenceEqual(new[] { CursorEffects.ParticleTrail })
            && CursorEffects.DefaultSelection(CursorEffectTrigger.Click).SequenceEqual(new[] { CursorEffects.ParticleBurst }));
        check("默认 Wire 与默认勾选一致",
            CursorEffects.DefaultWire(CursorEffectTrigger.Auto) == CursorEffects.ParticleTrail
            && CursorEffects.DefaultWire(CursorEffectTrigger.Click) == CursorEffects.ParticleBurst);
    }

    // ---------------------------------------------------------------- 归一化 / 存盘字符串

    private static void CheckNormalize(CheckFn check)
    {
        check("null（老配置没有字段）→ 默认值",
            CursorEffects.Normalize(null, CursorEffectTrigger.Auto)
                .SequenceEqual(CursorEffects.DefaultSelection(CursorEffectTrigger.Auto))
            && CursorEffects.Normalize(null, CursorEffectTrigger.Click)
                .SequenceEqual(CursorEffects.DefaultSelection(CursorEffectTrigger.Click)));

        check("null + 自定义兜底 → 用兜底",
            CursorEffects.Normalize(null, CursorEffectTrigger.Auto, Array.Empty<string>()).Count == 0);

        check("显式空数组 → 保持空（用户就是不要特效）",
            CursorEffects.Normalize(Array.Empty<string>(), CursorEffectTrigger.Auto).Count == 0
            && CursorEffects.Normalize(Array.Empty<string>(), CursorEffectTrigger.Click).Count == 0);

        check("未知 Id 被忽略、不抛异常",
            CursorEffects.Normalize(
                new[] { "不认识的特效", CursorEffects.ParticleTrail, "another-unknown" },
                CursorEffectTrigger.Auto).SequenceEqual(new[] { CursorEffects.ParticleTrail }));

        check("属于另一种触发方式的 Id 被过滤",
            CursorEffects.Normalize(
                new[] { CursorEffects.ParticleBurst, CursorEffects.Dispersion, CursorEffects.CursorGlow },
                CursorEffectTrigger.Auto).SequenceEqual(new[] { CursorEffects.CursorGlow }));

        check("重复 Id 去重、输出顺序按清单（与输入顺序无关）",
            CursorEffects.Normalize(
                new[] { CursorEffects.GlowFlash, CursorEffects.ParticleBurst, CursorEffects.GlowFlash },
                CursorEffectTrigger.Click).SequenceEqual(new[] { CursorEffects.ParticleBurst, CursorEffects.GlowFlash }));

        check("Auto/Click 各自可多选（全选也合法）",
            CursorEffects.Normalize(
                CursorEffects.For(CursorEffectTrigger.Auto).Select(x => x.Id),
                CursorEffectTrigger.Auto).Count == CursorEffects.For(CursorEffectTrigger.Auto).Count
            && CursorEffects.Normalize(
                CursorEffects.For(CursorEffectTrigger.Click).Select(x => x.Id),
                CursorEffectTrigger.Click).Count == CursorEffects.For(CursorEffectTrigger.Click).Count);

        var wire = CursorEffects.Join(new[] { CursorEffects.ParticleBurst, CursorEffects.Dispersion });
        check("Join / Split 往返一致",
            CursorEffects.Split(wire).SequenceEqual(new[] { CursorEffects.ParticleBurst, CursorEffects.Dispersion }),
            wire);
        check("Split 兼容逗号 / 分号 / 空格，空串返回空",
            CursorEffects.Split($"{CursorEffects.ParticleBurst};{CursorEffects.Dispersion}").Count == 2
            && CursorEffects.Split($"{CursorEffects.ParticleBurst} {CursorEffects.Dispersion}").Count == 2
            && CursorEffects.Split("").Count == 0
            && CursorEffects.Split(null).Count == 0);
        check("Join 空列表 = 空串", CursorEffects.Join(Array.Empty<string>()) == "" && CursorEffects.Join(null) == "");
    }

    // ---------------------------------------------------------------- 反色圆环（缓动 + 时间线）

    private static void CheckInvertAnimation(CheckFn check)
    {
        // ---- easeOutCubic：端点 / 越界 / NaN 都被夹住 ----
        check("easeOutCubic：0→0、1→1、越界与 NaN 被夹住",
            Math.Abs(Easings.OutCubic(0)) < 1e-12
            && Math.Abs(Easings.OutCubic(1) - 1) < 1e-12
            && Math.Abs(Easings.OutCubic(-3)) < 1e-12
            && Math.Abs(Easings.OutCubic(2) - 1) < 1e-12
            && Math.Abs(Easings.OutCubic(double.NaN)) < 1e-12);

        var monotonic = true;
        for (var i = 1; i <= 200; i++)
        {
            if (Easings.OutCubic(i / 200.0) < Easings.OutCubic((i - 1) / 200.0) - 1e-12) monotonic = false;
        }
        check("easeOutCubic：单调递增（半径不会中途反向抖一下）", monotonic);

        // 仍是「先快后慢」，但起步不冲、减速更平顺：前 1/4 时间走约 58%，后 1/4 时间只走约 1.6%
        var firstQuarter = Easings.OutCubic(0.25);
        var lastQuarter = 1.0 - Easings.OutCubic(0.75);
        check("easeOutCubic：先快后慢，且起步 / 减速都比五次方柔和",
            firstQuarter is > 0.5 and < 0.7 && lastQuarter < 0.05,
            $"前 1/4 行程={firstQuarter:0.###}（quint 是 0.76），后 1/4 行程={lastQuarter:0.###}");

        // ---- 时间线：两段时长 ----
        check("反色圆环时间线：扩散 + 收缩 = 总时长（都 > 0）",
            InvertCircleAnimation.GrowFrames > 0
            && InvertCircleAnimation.ShrinkFrames > 0
            && Math.Abs(InvertCircleAnimation.TotalFrames
                        - (InvertCircleAnimation.GrowFrames + InvertCircleAnimation.ShrinkFrames)) < 1e-9,
            $"扩散 {InvertCircleAnimation.GrowFrames} 帧 + 收缩 {InvertCircleAnimation.ShrinkFrames} 帧");

        const double maxRadius = 150;
        var growEnd = InvertCircleAnimation.GrowFrames;
        var total = InvertCircleAnimation.TotalFrames;

        check("反色圆环半径：开始为 0、扩散结束达到最大、播完回到 0",
            InvertCircleAnimation.RadiusAt(0, maxRadius) == 0
            && Math.Abs(InvertCircleAnimation.RadiusAt(growEnd, maxRadius) - maxRadius) < 1e-6
            && InvertCircleAnimation.RadiusAt(total, maxRadius) == 0
            && InvertCircleAnimation.RadiusAt(-1, maxRadius) == 0
            && InvertCircleAnimation.RadiusAt(double.NaN, maxRadius) == 0
            && InvertCircleAnimation.RadiusAt(5, double.NaN) == 0,
            $"最大半径 {maxRadius}");

        // 扩散段单调变大、收缩段单调变小（不出现忽大忽小）
        var growMonotonic = true;
        var previous = 0.0;
        for (var age = 0.0; age <= growEnd; age += 0.25)
        {
            var radius = InvertCircleAnimation.RadiusAt(age, maxRadius);
            if (radius < previous - 1e-9) growMonotonic = false;
            previous = radius;
        }

        var shrinkMonotonic = true;
        previous = maxRadius;
        for (var age = growEnd; age <= total; age += 0.25)
        {
            var radius = InvertCircleAnimation.RadiusAt(age, maxRadius);
            if (radius > previous + 1e-9) shrinkMonotonic = false;
            previous = radius;
        }
        check("反色圆环半径：扩散段单调变大、收缩段单调变小", growMonotonic && shrinkMonotonic);

        // 收缩「一开始半径减少很慢、后面减少变快」：后半程掉掉的半径明显多于前半程
        var halfway = InvertCircleAnimation.RadiusAt(growEnd + InvertCircleAnimation.ShrinkFrames / 2, maxRadius);
        var almostDone = InvertCircleAnimation.RadiusAt(total - 1, maxRadius);
        var firstHalfDrop = maxRadius - halfway;
        var secondHalfDrop = halfway - almostDone;
        check("反色圆环收缩：先缩得慢、最后缩得快",
            firstHalfDrop < maxRadius * 0.2 && secondHalfDrop > firstHalfDrop * 4,
            $"前半程掉 {firstHalfDrop:0.##}，后半程掉 {secondHalfDrop:0.##}");

        // 半径完全由调用方传入（主页设置滑块），这里验证 maxRadius 参数被正确使用
        check("反色圆环半径：最大半径由参数决定（主页设置滑块直接生效）",
            Math.Abs(InvertCircleAnimation.RadiusAt(growEnd, 200) - 200) < 1e-6
            && Math.Abs(InvertCircleAnimation.RadiusAt(growEnd, 60) - 60) < 1e-6);

        check("反色圆环：播完判定（到总时长算播完、NaN 也算播完）",
            !InvertCircleAnimation.IsFinished(total - 0.1)
            && InvertCircleAnimation.IsFinished(total)
            && InvertCircleAnimation.IsFinished(total + 5)
            && InvertCircleAnimation.IsFinished(double.NaN));
    }

    // ---------------------------------------------------------------- 点击爆炸引擎

    private static void CheckBurstEngine(CheckFn check)
    {
        var engine = new ParticleBurstEngine(seed: 20261029);
        check("新引擎没有粒子", !engine.HasParticles && engine.Particles.Count == 0 && engine.TotalBursts == 0);

        var spawned = engine.Burst(100, 120);
        check("默认一次爆炸 24 颗",
            spawned == ParticleBurstEngine.DefaultBurstCount && engine.Particles.Count == spawned
            && engine.TotalSpawned == spawned && engine.TotalBursts == 1, $"{spawned} 颗");

        var first = engine.Particles[0];
        check("粒子从点击点出发（当帧最多漂移 6.2×4≈25px，这里还没 Update 应为 0）",
            Math.Abs(first.X - 100) < 1e-9 && Math.Abs(first.Y - 120) < 1e-9);
        check("粒子寿命 / 大小 / 速度都在设计范围",
            first.LifeSpan >= ParticleBurstEngine.MinLifeSpan && first.LifeSpan < ParticleBurstEngine.MaxLifeSpan
            && first.BaseSize >= ParticleBurstEngine.MinBaseSize && first.BaseSize <= ParticleBurstEngine.MaxBaseSize
            && first.Speed >= ParticleBurstEngine.MinSpeed && first.Speed <= ParticleBurstEngine.MaxSpeed,
            $"寿命={first.LifeSpan:0.#} 大小={first.BaseSize:0.##} 速度={first.Speed:0.##}");

        var distanceBefore = Math.Sqrt((first.X - 100) * (first.X - 100) + (first.Y - 120) * (first.Y - 120));
        engine.Update(1);
        var distanceAfter = Math.Sqrt((first.X - 100) * (first.X - 100) + (first.Y - 120) * (first.Y - 120));
        check("Update 后粒子向外飞、速度按帧衰减",
            distanceAfter > distanceBefore && first.Speed < ParticleBurstEngine.MaxSpeed);

        var ageBefore = first.Age;
        engine.Update(1);
        check("Update 会让粒子老化", first.Age > ageBefore);

        // 一次推进 200 帧（内部按 MaxFrameStep 截断，但反复推几次就全该没了）
        for (var i = 0; i < 40; i++) engine.Update(ParticleEngine.MaxFrameStep);
        check("寿命到了会回收（推完足够帧数后清空）", !engine.HasParticles && engine.Particles.Count == 0);

        // 右键 = 更多
        engine.Clear();
        var rightCount = engine.Burst(10, 10, ParticleBurstEngine.RightBurstCount);
        check("右键爆炸更多（40 颗）", rightCount == ParticleBurstEngine.RightBurstCount && engine.Particles.Count == rightCount);
        engine.Clear();
        check("Clear 清空粒子但保留统计",
            !engine.HasParticles && engine.TotalSpawned > 0 && engine.TotalBursts >= 2);

        // 上限
        var capped = new ParticleBurstEngine(seed: 7);
        var cappedSpawned = capped.Burst(0, 0, ParticleBurstEngine.MaxParticles + 500);
        check("粒子有硬上限（不会无限堆积）",
            cappedSpawned == ParticleBurstEngine.MaxParticles && capped.Particles.Count == ParticleBurstEngine.MaxParticles,
            $"实际 {cappedSpawned} 颗");

        // 固定种子可复现（自检 / 回归用）
        var a = new ParticleBurstEngine(seed: 12345);
        var b = new ParticleBurstEngine(seed: 12345);
        a.Burst(50, 50, 10);
        b.Burst(50, 50, 10);
        check("固定种子结果可复现",
            a.Particles.Take(10).Select(p => (p.Angle, p.Speed, p.LifeSpan, p.BaseSize))
                .SequenceEqual(b.Particles.Take(10).Select(p => (p.Angle, p.Speed, p.LifeSpan, p.BaseSize))));

        var zero = new ParticleBurstEngine(seed: 1);
        check("数量 0 不生成粒子、但记一次爆发",
            zero.Burst(1, 2, 0) == 0 && zero.Particles.Count == 0 && zero.TotalBursts == 1);

        // 「爆炸范围」滑块：速度倍率（默认 1.0 保持原行为）
        var scaled = new ParticleBurstEngine(seed: 20261030) { SpeedScale = 2.0 };
        scaled.Burst(0, 0, 24);
        check("爆炸范围倍率放大粒子速度（2× → 射程约 2×）",
            scaled.Particles.All(p => p.Speed >= ParticleBurstEngine.MinSpeed * 2 - 1e-9
                                      && p.Speed <= ParticleBurstEngine.MaxSpeed * 2 + 1e-9)
            && scaled.Particles.Any(p => p.Speed > ParticleBurstEngine.MaxSpeed),
            $"最大 {scaled.Particles.Max(p => p.Speed):0.##}（原上限 {ParticleBurstEngine.MaxSpeed}）");

        scaled.SpeedScale = double.NaN;
        check("爆炸范围 NaN 倍率回到 1.0（不会把速度算成 NaN）", scaled.SpeedScale == 1.0);
        scaled.SpeedScale = 1000;
        check("爆炸范围倍率有上限（不会飞出去几万像素）",
            scaled.SpeedScale == ParticleBurstEngine.MaxSpeedScale,
            $"实际 {scaled.SpeedScale}");
    }

    // ---------------------------------------------------------------- 滑块设置项（v0.37.0）

    private static void CheckSliderOption(CheckFn check)
    {
        var changes = 0;
        var last = 0.0;
        var slider = new ShellSliderOption(
            "burst-count", CursorEffects.ParticleBurst, "粒子爆炸 · 数量", "一次点击炸开多少颗",
            CursorEffectTuning.BurstCountRange, 24, " 颗",
            value => { last = value; changes++; });

        check("滑块：初始值 / 范围 / 单位 / 文案正确",
            slider.Value == 24 && slider.Minimum == 4 && slider.Maximum == 160 && slider.Step == 1
            && slider.LargeStep == 10 && slider.ValueText == "24 颗"
            && slider.EffectId == CursorEffects.ParticleBurst);

        slider.Value = 30;
        check("滑块：赋值触发一次回调、文案跟着变",
            changes == 1 && last == 30 && slider.Value == 30 && slider.ValueText == "30 颗");
        slider.Value = 30;
        check("滑块：重复赋同值不再回调（Set 去重）", changes == 1);

        slider.Value = 1e9;
        check("滑块：超上限被夹住", slider.Value == 160 && last == 160);
        slider.Value = -5;
        check("滑块：超下限被夹住", slider.Value == 4 && last == 4);
        slider.Value = 27.4;
        check("滑块：按步进吸附（1 档 → 27）", slider.Value == 27 && last == 27);

        // 小数步进：0.05 档，显示两位小数
        var fine = new ShellSliderOption(
            "burst-range", CursorEffects.ParticleBurst, "粒子爆炸 · 范围", "射程倍率",
            CursorEffectTuning.BurstRangeRange, 1.0, " ×", _ => { });
        check("滑块：小数步进显示两位小数（1.00 ×）", fine.ValueText == "1.00 ×");
        fine.Value = 1.234;
        check("滑块：0.05 步进吸附到 1.25", fine.Value == 1.25 && fine.ValueText == "1.25 ×",
            $"实际 {fine.Value}");
    }

    // ---------------------------------------------------------------- 配置往返

    private static void CheckConfigRoundTrip(CheckFn check)
    {
        var defaults = new ShellConfig();
        check("新配置：autoEffects / clickEffects 缺省为 null（交给外壳按 showParticles 迁移）",
            defaults.AutoEffects is null && defaults.ClickEffects is null && defaults.ShowParticles);

        var dir = SelfCheckScratch.MakeDirectory("shell-effects");
        var path = Path.Combine(dir, "config.json");

        JsonFileStore.Write(path, new ShellConfig
        {
            AutoEffects = new List<string> { CursorEffects.ParticleTrail, CursorEffects.CursorGlow },
            ClickEffects = new List<string>
            {
                CursorEffects.ParticleBurst, CursorEffects.Dispersion,
                CursorEffects.GlowFlash, CursorEffects.InvertCircle,
            },
            TrailParticleCount = 6.5,
            TrailParticleSize = 1.4,
            CursorGlowRadius = 120,
            BurstParticleCount = 80,
            BurstRange = 1.95,
            DispersionRadius = 240,
            GlowFlashRadius = 180,
            InvertCircleRadius = 90,
            InvertCircleOverlapCancels = false,
        });
        var json = File.ReadAllText(path);
        var saved = JsonFileStore.Read<ShellConfig>(path);
        check("autoEffects / clickEffects 按数组存盘、读回一致",
            json.Contains("\"autoEffects\"") && json.Contains("\"clickEffects\"")
            && json.Contains("\"invert-circle\"")
            && saved?.AutoEffects?.SequenceEqual(new[] { CursorEffects.ParticleTrail, CursorEffects.CursorGlow }) == true
            && saved?.ClickEffects?.SequenceEqual(new[]
            {
                CursorEffects.ParticleBurst, CursorEffects.Dispersion,
                CursorEffects.GlowFlash, CursorEffects.InvertCircle,
            }) == true,
            json.Replace(Environment.NewLine, " "));

        check("特效参数（滑块）按字段存盘、读回一致",
            json.Contains("\"trailParticleCount\"") && json.Contains("\"invertCircleRadius\"")
            && json.Contains("\"invertCircleOverlapCancels\"")
            && saved is { TrailParticleCount: 6.5, TrailParticleSize: 1.4, CursorGlowRadius: 120 }
            && saved is { BurstParticleCount: 80, BurstRange: 1.95, DispersionRadius: 240 }
            && saved is { GlowFlashRadius: 180, InvertCircleRadius: 90 }
            && saved.InvertCircleOverlapCancels == false);

        File.WriteAllText(path, "{ \"showParticles\": false }");
        var legacy = JsonFileStore.Read<ShellConfig>(path);
        check("老配置（只有 showParticles）读出来特效列表是 null（外壳按老开关迁移）",
            legacy is { ShowParticles: false, AutoEffects: null, ClickEffects: null });
        check("老配置缺特效参数字段 → 用默认值（外观与升级前一致）",
            legacy is
            {
                TrailParticleCount: 1, TrailParticleSize: 1, CursorGlowRadius: 46,
                BurstParticleCount: 24, BurstRange: 1, DispersionRadius: 98,
                GlowFlashRadius: 64, InvertCircleRadius: 150,
            }
            && legacy.InvertCircleOverlapCancels);

        File.WriteAllText(path, "{ \"autoEffects\": [], \"clickEffects\": [] }");
        var explicitEmpty = JsonFileStore.Read<ShellConfig>(path);
        check("显式空数组读回空列表（= 用户不要任何特效）",
            explicitEmpty?.AutoEffects is { Count: 0 } && explicitEmpty?.ClickEffects is { Count: 0 });
    }

    // ---------------------------------------------------------------- 设置项模型

    private static void CheckEffectOption(CheckFn check)
    {
        var changes = 0;
        var definition = CursorEffects.Find(CursorEffects.CursorGlow)!;
        var slider = new ShellSliderOption(
            "glow-radius", CursorEffects.CursorGlow, "鼠标散光 · 半径", "说明",
            CursorEffectTuning.GlowRadiusRange, 46, " DIP", _ => { });
        var option = new ShellEffectOption(definition, isSelected: false, () => changes++, new[] { slider });

        check("设置项初始状态 / 元数据正确",
            !option.IsSelected && option.Id == CursorEffects.CursorGlow
            && option.Name == definition.Name && option.Description == definition.Description
            && option.IsAuto);

        check("特效项携带自己的参数滑块 + 反色标记（设置页嵌在勾选框下面，没勾选不显示）",
            option.Sliders.Count == 1 && option.Sliders[0].Id == "glow-radius"
            && !option.IsInvertCircle
            && new ShellEffectOption(CursorEffects.Find(CursorEffects.InvertCircle)!, isSelected: true, () => { }).IsInvertCircle);

        option.IsSelected = true;
        check("勾选后触发一次变化回调", option.IsSelected && changes == 1);
        option.IsSelected = true;
        check("重复赋同值不再回调（Set 去重）", changes == 1);
        option.IsSelected = false;
        check("取消勾选再触发一次回调", !option.IsSelected && changes == 2);
    }

    // ---------------------------------------------------------------- 导航位置

    private static void CheckSettingsLocation(CheckFn check)
    {
        var settings = ShellLocation.Settings;
        check("设置页历史位置：Kind / Key / 标题",
            settings.Kind == ShellLocationKind.Settings && settings.Key == "settings" && settings.Title == "主页设置");
        check("设置页与主页不相等、自己是相等的",
            !settings.Equals(ShellLocation.Home) && settings.Equals(ShellLocation.Settings));
        check("ShellPageKind 里有设置页", Enum.GetNames<ShellPageKind>().Contains("Settings"));

        var homeCrumb = BreadcrumbItem.ForHome(null!, isCurrent: false);
        var settingsCrumb = BreadcrumbItem.ForSettings(isCurrent: true, isFirst: false);
        check("面包屑：主页 › 主页设置",
            homeCrumb is { Title: "主页", IsCurrent: false, IsClickable: true }
            && settingsCrumb is { Title: "主页设置", IsSettings: true, IsCurrent: true, IsClickable: false }
            && settingsCrumb.DisplayText.Contains("主页设置"));
    }
}
