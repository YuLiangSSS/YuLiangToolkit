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
        CheckBurstEngine(Check);
        CheckConfigRoundTrip(Check);
        CheckEffectOption(Check);
        CheckSettingsLocation(Check);
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

        // ---- 最大半径按窗口短边夹取，且面积约为 v0.36.0 的一半（半径 × 1/√2） ----
        var defaultRadius = InvertCircleAnimation.ResolveMaxRadius(1280, 820);
        check("反色圆环最大半径：按短边比例、夹在上下限之间",
            Math.Abs(InvertCircleAnimation.ResolveMaxRadius(1024, 700)
                     - 700 * InvertCircleAnimation.RadiusRatio) < 1e-9
            && Math.Abs(InvertCircleAnimation.ResolveMaxRadius(1920, 1080) - InvertCircleAnimation.LargestRadius) < 1e-9
            && Math.Abs(InvertCircleAnimation.ResolveMaxRadius(100, 100) - InvertCircleAnimation.SmallestRadius) < 1e-9
            && Math.Abs(InvertCircleAnimation.ResolveMaxRadius(double.NaN, 500) - InvertCircleAnimation.SmallestRadius) < 1e-9,
            $"默认窗口 1280×820 → {defaultRadius:0.##}");

        check("反色圆环尺寸：默认窗口最大半径约 151（v0.36.0 是 213，面积正好减半）",
            Math.Abs(defaultRadius - 213.2 / Math.Sqrt(2)) < 1e-6,
            $"{defaultRadius:0.##}");

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

        File.WriteAllText(path, "{ \"showParticles\": false }");
        var legacy = JsonFileStore.Read<ShellConfig>(path);
        check("老配置（只有 showParticles）读出来特效列表是 null（外壳按老开关迁移）",
            legacy is { ShowParticles: false, AutoEffects: null, ClickEffects: null });

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
        var option = new ShellEffectOption(definition, isSelected: false, () => changes++);

        check("设置项初始状态 / 元数据正确",
            !option.IsSelected && option.Id == CursorEffects.CursorGlow
            && option.Name == definition.Name && option.Description == definition.Description
            && option.IsAuto);

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
