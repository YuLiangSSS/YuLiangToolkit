namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>摸胸反应档位：低好感生气 / 中好感脸红生气 / 高好感脸红爱心眼 + 微笑。</summary>
public sealed record ChestReactionTier(
    int MinAffection,
    string[] Expressions,
    float MouthFormTarget,
    float MouthSmileTarget,
    string[] Lines);

/// <summary>
/// 点击胸部（protect 热区）的台词 / 表情 / 动作：
/// 动作 = 抱胸致意（作者原生「默认手切举手」进入 / 「举手切默认手」退出）；
/// 台词与表情按好感度分三档，动作与表情到点都还原。
/// </summary>
public static class ChestReaction
{
    /// <summary>动作 / 表情保持秒数（到时自动播退出动作 + 撤掉临时表情）。</summary>
    public const double DurationSeconds = 2.8;

    /// <summary>动作的手势 id（抱胸致意 = 把手搭到胸口，用的是作者「举手」那套过渡）。</summary>
    public const string GestureId = "抱胸致意";

    /// <summary>所有档位（按 MinAffection 从高到低匹配）。</summary>
    public static IReadOnlyList<ChestReactionTier> Tiers { get; } = new[]
    {
        new ChestReactionTier(85,
            new[] { "脸红", "爱心眼" }, 0.55f, 0.45f,
            new[]
            {
                "想摸摸吗？",
                "这个手感，你喜欢吗？",
                "唔…被这样摸，会有点奇怪…",
                "只、只给你摸一下下喔。",
                "笨蛋…手别抖啦。",
                "再摸下去…我可要当真了。",
                "喜欢的话…可以再一下下。",
                "（小声）…只对你这样。",
            }),
        new ChestReactionTier(30,
            new[] { "脸红", "＞＜" }, -0.45f, -0.20f,
            new[]
            {
                "你在摸哪里！",
                "喂…那里不行啦！",
                "手、手拿开…",
                "变态！…才、才不是害羞。",
                "再摸我要生气了喔。",
                "不许乱摸！",
                "你、你故意的吧…",
            }),
        new ChestReactionTier(0,
            new[] { "黑脸" }, -0.70f, -0.15f,
            new[]
            {
                "手拿开！",
                "别碰我。",
                "再碰就咬你了。",
                "离我远点。",
                "你谁啊…走开。",
                "烦死了…别乱摸。",
            }),
    };

    /// <summary>好感度 → 档位（≥85 高 / ≥30 中 / 其余低）。</summary>
    public static ChestReactionTier Resolve(double affection)
    {
        if (double.IsNaN(affection)) affection = 50;
        ChestReactionTier best = Tiers[^1];
        foreach (var tier in Tiers)
        {
            if (affection + 0.001 >= tier.MinAffection && tier.MinAffection >= best.MinAffection) best = tier;
        }
        return best;
    }

    /// <summary>挑一句台词（尽量不和上一句重复）。</summary>
    public static string PickLine(ChestReactionTier tier, string? lastLine, Random random)
    {
        var lines = tier.Lines;
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
