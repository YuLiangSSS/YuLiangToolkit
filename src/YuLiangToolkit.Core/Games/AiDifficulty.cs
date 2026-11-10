namespace YuLiangToolkit.Core.Games;

/// <summary>
/// 电脑对手的「智慧程度」（难度）。
/// <para>所有棋牌游戏共用这一套档位，界面上显示成同一组名字，用户学习成本最低。</para>
/// </summary>
public enum AiDifficulty
{
    /// <summary>入门：只防明显的输、偶尔犯傻（适合第一次玩）。</summary>
    Beginner = 0,

    /// <summary>普通：会看一两步，能防守也会进攻。</summary>
    Normal = 1,

    /// <summary>困难：看得更远，会算连招。</summary>
    Hard = 2,

    /// <summary>大师：全深度 / 最强评估，慢一点但明显更强。</summary>
    Master = 3,
}

/// <summary>难度档位的展示与参数（纯逻辑，自检可断言）。</summary>
public static class AiDifficulties
{
    /// <summary>全部档位（界面下拉用）。</summary>
    public static IReadOnlyList<AiDifficulty> All { get; } = new[]
    {
        AiDifficulty.Beginner, AiDifficulty.Normal, AiDifficulty.Hard, AiDifficulty.Master,
    };

    /// <summary>中文名。</summary>
    public static string DisplayName(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => "入门",
        AiDifficulty.Hard => "困难",
        AiDifficulty.Master => "大师",
        _ => "普通",
    };

    /// <summary>一句话说明。</summary>
    public static string Description(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => "只防明显失误，适合练手",
        AiDifficulty.Hard => "会算几步连招，认真对待",
        AiDifficulty.Master => "深度搜索，比较难赢",
        _ => "会防守也会进攻，正常水平",
    };

    /// <summary>存储键（配置 / 协议用）。</summary>
    public static string ToKey(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => "beginner",
        AiDifficulty.Hard => "hard",
        AiDifficulty.Master => "master",
        _ => "normal",
    };

    /// <summary>键 → 档位（认不出按普通）。</summary>
    public static AiDifficulty Parse(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "beginner" => AiDifficulty.Beginner,
        "hard" => AiDifficulty.Hard,
        "master" => AiDifficulty.Master,
        _ => AiDifficulty.Normal,
    };

    /// <summary>搜索深度（棋类用；牌类 / 方块类用不到就忽略）。</summary>
    public static int SearchDepth(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => 1,
        AiDifficulty.Hard => 3,
        AiDifficulty.Master => 4,
        _ => 2,
    };

    /// <summary>
    /// 走「次优解」的概率（0~1）：入门会随机犯傻，越往上越少。
    /// 用固定种子的 <see cref="Random"/> 可以让自检结果可复现。
    /// </summary>
    public static double BlunderChance(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => 0.35,
        AiDifficulty.Hard => 0.04,
        AiDifficulty.Master => 0,
        _ => 0.12,
    };

    /// <summary>思考时的「拟人延迟」上限（毫秒）：入门慢悠悠、大师也留一点，免得瞬间落子太突兀。</summary>
    public static int ThinkingDelayMs(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => 600,
        AiDifficulty.Hard => 380,
        AiDifficulty.Master => 260,
        _ => 480,
    };

    /// <summary>候选走法上限（入门只在前几个里挑，避免它算出最优）。</summary>
    public static int CandidateLimit(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => 6,
        AiDifficulty.Hard => 200,
        AiDifficulty.Master => 400,
        _ => 40,
    };
}
