namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>好感度档位（互动反馈用；和 AI 聊天的情绪档位是两套，不要混）。</summary>
public enum AffectionTier
{
    /// <summary>0 ~ 29：还不熟（互动保持礼貌 / 轻微害羞，不主动亲近）。</summary>
    Cold,

    /// <summary>30 ~ 59：普通（会脸红，但不会主动贴上来）。</summary>
    Normal,

    /// <summary>60 ~ 84：亲近（会撒娇、允许摸头 / 牵手）。</summary>
    Warm,

    /// <summary>85 ~ 100：心动（爱心眼、主动前倾、扭扭捏捏；低好感时绝不会出现）。</summary>
    Love,
}

/// <summary>
/// 一条「互动动作 + 台词」预设：把表情（可多个叠加）/ 手势 / 扭动 / 台词绑在一起，
/// 按好感度档位挑一条。以后想加新互动，只在这里加一条数据即可（不用改会话逻辑）。
///
/// <para>匹配规则：同一 <see cref="VoiceEvent"/> 下取「<see cref="MinAffection"/> ≤ 当前好感度」里
/// 门槛最高的一条；没到门槛就走默认台词（老行为）。</para>
/// </summary>
public sealed record InteractionScenario(
    string Id,
    string VoiceEvent,
    int MinAffection,
    string[] Expressions,
    double WiggleSeconds,
    string[] Lines)
{
    /// <summary>可选手势 id（空 = 只做表情 / 扭动，不主动摆手势；避免和互动本身的手势打架）。</summary>
    public string GestureId { get; init; } = "";

    /// <summary>是否要「前倾」（叠加模型自带的 前倾 表情，好感度高时用；低好感不主动凑近）。</summary>
    public bool LeanForward { get; init; }

    /// <summary>档位（由 <see cref="MinAffection"/> 推出来，方便日志 / 自检）。</summary>
    public AffectionTier Tier => InteractionScenarios.ResolveTier(MinAffection);
}

/// <summary>
/// 好感度 → 互动预设目录（数据驱动，用户以后想加对话 / 动作只改这里）。
/// <para>设计口径：<b>低好感不主动亲近</b>（不爱心眼、不前倾、不扭动）；高好感才解锁亲密动作。</para>
/// </summary>
public static class InteractionScenarios
{
    /// <summary>好感度 → 档位。</summary>
    public static AffectionTier ResolveTier(double affection)
    {
        if (double.IsNaN(affection)) affection = 50;
        if (affection >= 85) return AffectionTier.Love;
        if (affection >= 60) return AffectionTier.Warm;
        if (affection >= 30) return AffectionTier.Normal;
        return AffectionTier.Cold;
    }

    /// <summary>所有预设（同一事件按 <see cref="MinAffection"/> 从高到低匹配；顺序不影响结果）。</summary>
    public static IReadOnlyList<InteractionScenario> All { get; } = new[]
    {
        // ---------- 摸头：高好感 → 爱心眼 + 脸红 + 前倾（低好感不出现） ----------
        new InteractionScenario(
            "pat_head.love", "pat_head", 85,
            new[] { "爱心眼", "脸红" }, 0, new[]
            {
                "诶…主人再摸一会儿…我、我才没有很舒服…",
                "唔…有点晕晕的…你离我这么近，我心跳好吵…",
                "好、好喜欢你摸这里…再一下下就好。",
                "（小声）…尾巴都翘起来了，不许笑。",
                "嗯…再摸摸头，我就不闹了。",
            })
        { LeanForward = true },
        new InteractionScenario(
            "pat_head.warm", "pat_head", 60,
            new[] { "脸红", "圈圈眼" }, 0, new[]
            {
                "唔…被摸头的时候，心跳会变快…",
                "再、再摸一下也不是不行…",
                "嘿嘿…你的手好暖。",
                "唔…有点晕乎乎的…",
            }),

        // ---------- 摸裙子：高好感 → 脸红 + ＞＜ + 扭扭捏捏（身体先扭、头慢半拍） ----------
        new InteractionScenario(
            "skirt.love", "skirt", 60,
            new[] { "脸红", "＞＜" }, 2.4, new[]
            {
                "呀…不、不要这样啦～",
                "别、别掀裙子啦…好害羞…",
                "唔…你、你要负责啦…",
                "再这样…我要生气了喔…才怪。",
            }),
        new InteractionScenario(
            "skirt.normal", "skirt", 30,
            new[] { "脸红" }, 1.6, new[]
            {
                "裙子、裙子会飞起来的…",
                "只、只能看一眼喔。",
            }),

        // ---------- 捏脸 ----------
        new InteractionScenario(
            "pinch.love", "pinch", 85,
            new[] { "＞＜", "脸红" }, 0, new[]
            {
                "呀！…是、是喜欢才捏的吗…",
                "再捏脸要变成奇怪的表情啦…",
                "哼…只、只给你一个人捏喔。",
            }),
        new InteractionScenario(
            "pinch.warm", "pinch", 60,
            new[] { "＞＜" }, 0, new[]
            {
                "呜…别捏啦，脸会变形的…",
                "轻、轻一点嘛…",
            }),

        // ---------- 挠痒 ----------
        new InteractionScenario(
            "tickle.love", "tickle", 60,
            new[] { "＞＜", "脸红", "屑屑眼", "流泪" }, 1.2, new[]
            {
                "哈哈…别、别挠了…要笑出眼泪了…",
                "不行…那里…真的不行啦～",
                "你、你欺负人…哼。",
                "屑…下次换我挠你。",
            }),

        // ---------- 护住胸口 ----------
        new InteractionScenario(
            "protect.love", "protect", 85,
            new[] { "黑脸", "脸红", "流冷汗" }, 1.4, new[]
            {
                "变态！…才、才不是害羞…",
                "再看就真的生气了喔…",
                "只、只准看脸啦…",
            }),

        // ---------- 腰 / 腿 / 脚 / 尾巴 / 胸口 ----------
        new InteractionScenario(
            "waist.love", "waist", 60,
            new[] { "脸红", "＞＜" }, 2.0, new[]
            {
                "腰、腰不行…好痒…",
                "别戳啦…我、我会站不稳的…",
                "唔…你故意的吧…",
            }),
        new InteractionScenario(
            "legs.love", "legs", 60,
            new[] { "脸红", "＞＜" }, 1.8, new[]
            {
                "腿、腿也不行…",
                "再摸就真的踢你了喔…",
            }),
        new InteractionScenario(
            "feet.love", "feet", 60,
            new[] { "脸红" }, 0, new[]
            {
                "脚、脚底不可以…",
                "鞋子真的会掉啦…",
            }),
        new InteractionScenario(
            "tail.love", "tail", 85,
            new[] { "脸红", "＞＜" }, 2.0, new[]
            {
                "尾巴…尾巴不是给摸的啦…",
                "呀…那里会、会有感觉的…",
                "别、别拽尾巴…要、要站不住了…",
            }),
        new InteractionScenario(
            "chest.love", "chest", 85,
            new[] { "黑脸", "脸红" }, 1.2, new[]
            {
                "不、不行…那里不可以看…",
                "变态…不过…只对你这样。",
            }),

        // ---------- 点额头换表情 / 点手 ----------
        new InteractionScenario(
            "face_cycle.love", "face_cycle", 85,
            new[] { "爱心眼", "脸红" }, 0, new[]
            {
                "这个表情…是专门给你看的喔。",
                "被、被盯着看会害羞啦…",
            }),
        new InteractionScenario(
            "hand.love", "hand", 60,
            new[] { "脸红" }, 0, new[]
            {
                "手…给你牵一下下。",
                "手指…可以牵着吗？",
            }),
    };

    /// <summary>按事件 + 好感度找最合适的一条（没到门槛返回 null = 走默认台词）。</summary>
    public static InteractionScenario? TryMatch(string? voiceEvent, double affection)
    {
        if (string.IsNullOrWhiteSpace(voiceEvent)) return null;
        var eventKey = voiceEvent.Trim();
        InteractionScenario? best = null;
        foreach (var scenario in All)
        {
            if (!string.Equals(scenario.VoiceEvent, eventKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (affection + 0.001 < scenario.MinAffection) continue;
            if (best is null || scenario.MinAffection > best.MinAffection) best = scenario;
        }
        return best;
    }

    /// <summary>挑一句台词（尽量不连续重复上一句；空数组返回空串）。</summary>
    public static string PickLine(InteractionScenario scenario, string? lastLine, Random random)
    {
        var lines = scenario.Lines;
        if (lines.Length == 0) return "";
        if (lines.Length == 1) return lines[0];
        for (var i = 0; i < 6; i++)
        {
            var line = lines[random.Next(lines.Length)];
            if (!string.Equals(line, lastLine, StringComparison.Ordinal)) return line;
        }
        return lines[0];
    }
}
