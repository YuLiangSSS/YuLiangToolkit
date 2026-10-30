namespace YuLiangToolkit.Core.Effects;

/// <summary>光标特效的触发方式：自动（跟随鼠标常驻）/ 点击（按下鼠标时触发）。</summary>
public enum CursorEffectTrigger
{
    /// <summary>自动效果：鼠标移动时持续生效（例如粒子拖尾、鼠标散光）。</summary>
    Auto,

    /// <summary>点击效果：鼠标按下时触发一次（例如粒子爆炸、散光扩散）。</summary>
    Click,
}

/// <summary>
/// 一条光标特效的**元数据**（Id / 显示名 / 说明 / 触发方式 / 排序）。
///
/// <para>设置页、配置校验、渲染层都只认这里的清单：
/// 以后加一种新特效 = ① 在 <see cref="CursorEffects.All"/> 里加一条；② 在
/// <c>ParticleOverlay</c> 的渲染分支里接上对应绘制；③ 自检的清单断言自动覆盖。
/// 配置里存的是 <see cref="Id"/>（稳定字符串，别改），界面显示用 <see cref="Name"/>/<see cref="Description"/>。</para>
/// </summary>
/// <param name="Id">稳定 Id（存进配置，定了就别改）。</param>
/// <param name="Name">设置页上的显示名。</param>
/// <param name="Description">设置页上的一句话说明。</param>
/// <param name="Trigger">触发方式（自动 / 点击）。</param>
/// <param name="Order">同类里的排序（越小越靠前）。</param>
public sealed record CursorEffectDefinition(
    string Id,
    string Name,
    string Description,
    CursorEffectTrigger Trigger,
    int Order);

/// <summary>
/// 光标特效清单与纯逻辑工具（不碰 WPF，自检可直接断言）。
///
/// <para><b>两套特效互相独立</b>：自动效果（粒子拖尾 / 鼠标散光）与点击效果（粒子爆炸 /
/// 散光扩散 / 光晕闪烁 / 反色圆环）各自是「可多选」的 Id 列表 —— 可以只开点击爆炸、不开自动拖尾，
/// 也可以同时勾选全部。配置里存 <c>autoEffects</c> / <c>clickEffects</c> 两个字符串数组。</para>
///
/// <para><b>缺省 / 迁移规则</b>（见 <see cref="Normalize"/>，外壳配置加载时用）：
/// 配置里没有这两个字段（<c>null</c>）→ 按默认值补；显式空数组 → 用户就是不要特效，保持空。
/// 老版本只有 <c>showParticles</c> 布尔开关：开着就迁移成「粒子拖尾 + 粒子爆炸」，关着就两个都空，
/// 保证老用户升级后的行为与以前一致。</para>
///
/// <para>未知 Id（比如降级后残留的新特效）在 <see cref="Normalize"/> 里会被静默忽略，不会崩。</para>
/// </summary>
public static class CursorEffects
{
    // ---------------- 自动效果 Id ----------------

    /// <summary>粒子拖尾：鼠标移动时持续冒出小粒子（原来的「显示鼠标粒子效果」）。</summary>
    public const string ParticleTrail = "particle-trail";

    /// <summary>鼠标散光：鼠标位置一圈微弱的圆形发散光，常驻跟随。</summary>
    public const string CursorGlow = "cursor-glow";

    // ---------------- 点击效果 Id ----------------

    /// <summary>粒子爆炸：点击处向四周炸开一簇粒子。</summary>
    public const string ParticleBurst = "particle-burst";

    /// <summary>散光扩散：点击处扩散一圈彩色色散光环（Glassium 风格的「散光」）。</summary>
    public const string Dispersion = "dispersion";

    /// <summary>光晕闪烁：点击处柔光亮一下再淡出。</summary>
    public const string GlowFlash = "glow-flash";

    /// <summary>反色圆环：点击处一块反色（颜色反转）的圆形区域先扩散、再收缩到消失。</summary>
    public const string InvertCircle = "invert-circle";

    /// <summary>全部特效清单（设置页按触发方式分组显示）。</summary>
    public static IReadOnlyList<CursorEffectDefinition> All { get; } = new CursorEffectDefinition[]
    {
        new(ParticleTrail, "粒子拖尾", "鼠标移动时持续冒出小粒子（原来的鼠标粒子效果）",
            CursorEffectTrigger.Auto, 10),
        new(CursorGlow, "鼠标散光", "鼠标位置一圈微弱的圆形发散光，跟随鼠标",
            CursorEffectTrigger.Auto, 20),
        new(ParticleBurst, "粒子爆炸", "点击时向四周炸开一簇粒子",
            CursorEffectTrigger.Click, 10),
        new(Dispersion, "散光扩散", "点击处扩散一圈彩色色散光环（Glassium 风格散光）",
            CursorEffectTrigger.Click, 20),
        new(GlowFlash, "光晕闪烁", "点击处柔光亮一下再淡出",
            CursorEffectTrigger.Click, 30),
        new(InvertCircle, "反色圆环", "点击处一块反色的圆形区域先扩大再收缩（只反色窗口内容）",
            CursorEffectTrigger.Click, 40),
    };

    /// <summary>取某一触发方式的全部特效（按 <see cref="CursorEffectDefinition.Order"/> 排序）。</summary>
    public static IReadOnlyList<CursorEffectDefinition> For(CursorEffectTrigger trigger)
        => All.Where(x => x.Trigger == trigger).OrderBy(x => x.Order).ToList();

    /// <summary>按 Id 找定义（大小写不敏感；未知返回 <c>null</c>）。</summary>
    public static CursorEffectDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var trimmed = id.Trim();
        return All.FirstOrDefault(x => string.Equals(x.Id, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>某一触发方式的默认勾选（新安装 / 老配置字段缺失时用）。</summary>
    public static IReadOnlyList<string> DefaultSelection(CursorEffectTrigger trigger)
        => trigger == CursorEffectTrigger.Auto
            ? new[] { ParticleTrail }
            : new[] { ParticleBurst };

    /// <summary>默认选中集合的字符串形式（设置页 / 渲染层用）。</summary>
    public static string DefaultWire(CursorEffectTrigger trigger) => Join(DefaultSelection(trigger));

    /// <summary>
    /// 归一化一份配置里的特效 Id 列表：
    /// <list type="bullet">
    ///   <item><paramref name="raw"/> 为 <c>null</c>（配置里没这个字段）→ 用 <paramref name="fallbackWhenNull"/>，没给就用默认值；</item>
    ///   <item>显式空数组 → 返回空（用户故意关了）；</item>
    ///   <item>过滤掉未知 Id / 属于另一种触发方式的 Id，去重，按清单顺序输出（存盘稳定，不随点击顺序变）。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Normalize(
        IEnumerable<string>? raw,
        CursorEffectTrigger trigger,
        IReadOnlyList<string>? fallbackWhenNull = null)
    {
        if (raw is null) return fallbackWhenNull ?? DefaultSelection(trigger);

        var allowed = For(trigger).Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in raw)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            var trimmed = id.Trim();
            if (allowed.Contains(trimmed)) selected.Add(trimmed);
        }

        return For(trigger)
            .Where(x => selected.Contains(x.Id))
            .Select(x => x.Id)
            .ToList();
    }

    /// <summary>把 Id 列表拼成渲染层依赖属性用的逗号分隔字符串（空列表 = 空串）。</summary>
    public static string Join(IEnumerable<string>? ids)
        => ids is null ? "" : string.Join(",", ids.Where(id => !string.IsNullOrWhiteSpace(id)));

    /// <summary>把逗号分隔字符串拆回 Id 列表（渲染层依赖属性用；空 / null 返回空列表）。</summary>
    public static IReadOnlyList<string> Split(string? wire)
        => string.IsNullOrWhiteSpace(wire)
            ? Array.Empty<string>()
            : wire.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
