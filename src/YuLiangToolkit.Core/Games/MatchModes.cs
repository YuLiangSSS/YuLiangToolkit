namespace YuLiangToolkit.Core.Games;

/// <summary>
/// 一局结束后怎么换人（<b>棋牌与游戏共用</b>：象棋 / 五子棋 / 跳棋 / 俄罗斯方块同一套说法）。
/// </summary>
public enum MatchRotateMode
{
    /// <summary>手动安排（默认）：房主自己指定谁上场。</summary>
    Manual = 0,

    /// <summary>输的下场：赢家留场，输家下场，从观战席补一位。</summary>
    LoserOut = 1,

    /// <summary>全场换人：不论输赢都下场，从观战席补人。</summary>
    BothOut = 2,
}

/// <summary>补位方式（从没上场的玩家里怎么挑）。</summary>
public enum MatchPickMode
{
    /// <summary>轮流上场：没上过场的优先。</summary>
    Rotation = 0,

    /// <summary>随机上场。</summary>
    Random = 1,
}

/// <summary>换人设置的「键 ↔ 枚举 ↔ 中文名」转换（配置 / 协议里只存英文键）。</summary>
public static class MatchModes
{
    /// <summary>换人方式 → 存储键。</summary>
    public static string ToKey(MatchRotateMode mode) => mode switch
    {
        MatchRotateMode.LoserOut => "loserOut",
        MatchRotateMode.BothOut => "bothOut",
        _ => "manual",
    };

    /// <summary>键 → 换人方式（认不出按手动）。</summary>
    public static MatchRotateMode ParseRotate(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "loserout" => MatchRotateMode.LoserOut,
        "bothout" => MatchRotateMode.BothOut,
        _ => MatchRotateMode.Manual,
    };

    /// <summary>补位方式 → 存储键。</summary>
    public static string ToKey(MatchPickMode mode)
        => mode == MatchPickMode.Random ? "random" : "rotation";

    /// <summary>键 → 补位方式。</summary>
    public static MatchPickMode ParsePick(string? key)
        => (key ?? "").Trim().ToLowerInvariant() == "random" ? MatchPickMode.Random : MatchPickMode.Rotation;

    /// <summary>换人方式中文名。</summary>
    public static string Describe(MatchRotateMode mode) => mode switch
    {
        MatchRotateMode.LoserOut => "输的下场",
        MatchRotateMode.BothOut => "全场换人",
        _ => "手动安排",
    };

    /// <summary>补位方式中文名。</summary>
    public static string Describe(MatchPickMode mode)
        => mode == MatchPickMode.Random ? "随机上场" : "轮流上场";
}
