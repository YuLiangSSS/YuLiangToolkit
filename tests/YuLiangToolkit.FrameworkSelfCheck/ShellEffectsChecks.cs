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
        check("点击效果清单：粒子爆炸 + 散光扩散 + 光晕闪烁",
            click.Select(x => x.Id).SequenceEqual(new[]
                { CursorEffects.ParticleBurst, CursorEffects.Dispersion, CursorEffects.GlowFlash }));
        check("清单按 Order 排序", auto.Zip(auto.Skip(1)).All(pair => pair.First.Order <= pair.Second.Order)
                                   && click.Zip(click.Skip(1)).All(pair => pair.First.Order <= pair.Second.Order));

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
            ClickEffects = new List<string> { CursorEffects.ParticleBurst, CursorEffects.Dispersion, CursorEffects.GlowFlash },
        });
        var json = File.ReadAllText(path);
        var saved = JsonFileStore.Read<ShellConfig>(path);
        check("autoEffects / clickEffects 按数组存盘、读回一致",
            json.Contains("\"autoEffects\"") && json.Contains("\"clickEffects\"")
            && saved?.AutoEffects?.SequenceEqual(new[] { CursorEffects.ParticleTrail, CursorEffects.CursorGlow }) == true
            && saved?.ClickEffects?.Count == 3,
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
