namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>军棋棋子等级（数值越大越强；特殊棋子单独处理）。</summary>
public enum JunqiRank
{
    /// <summary>空。</summary>
    None = 0,

    /// <summary>工兵（可以挖雷、能拐弯走铁路）。</summary>
    Engineer = 1,

    /// <summary>排长。</summary>
    PlatoonLeader = 2,

    /// <summary>连长。</summary>
    CompanyCommander = 3,

    /// <summary>营长。</summary>
    BattalionCommander = 4,

    /// <summary>团长。</summary>
    RegimentalCommander = 5,

    /// <summary>旅长。</summary>
    BrigadeCommander = 6,

    /// <summary>师长。</summary>
    DivisionCommander = 7,

    /// <summary>军长。</summary>
    CorpsCommander = 8,

    /// <summary>司令。</summary>
    Commander = 9,

    /// <summary>地雷（不能动；只有工兵能排，其他子碰雷同归于尽）。</summary>
    Landmine = 10,

    /// <summary>炸弹（同归于尽，谁都能炸）。</summary>
    Bomb = 11,

    /// <summary>军旗（被扛就输；不能移动）。</summary>
    Flag = 12,
}

/// <summary>阵营。</summary>
public enum JunqiSide
{
    /// <summary>红方。</summary>
    Red = 0,

    /// <summary>蓝方。</summary>
    Blue = 1,
}

/// <summary>一枚棋子。</summary>
/// <param name="Rank">等级 / 类型。</param>
/// <param name="Side">属于哪一方。</param>
/// <param name="Revealed">有没有被翻开（暗棋：翻开的双方都看得到）。</param>
public readonly record struct JunqiPiece(JunqiRank Rank, JunqiSide Side, bool Revealed)
{
    /// <summary>空位。</summary>
    public static JunqiPiece Empty { get; } = new(JunqiRank.None, JunqiSide.Red, false);

    /// <summary>
    /// 「对方有一枚棋子，但你还不知道是什么」（暗棋）：<see cref="Rank"/> 是 None、
    /// 但 <see cref="Side"/> 有值 —— 界面画成背面朝上的棋子。
    /// </summary>
    public static JunqiPiece Unknown(JunqiSide side) => new(JunqiRank.None, side, false);

    /// <summary>是不是空。</summary>
    public bool IsEmpty => Rank == JunqiRank.None;

    /// <summary>是不是「有子但等级未知」（暗棋）。</summary>
    public bool IsUnknown => Rank == JunqiRank.None && !Revealed;

    /// <summary>能不能被移动（地雷 / 军旗不能动）。</summary>
    public bool CanMove => Rank is not (JunqiRank.None or JunqiRank.Landmine or JunqiRank.Flag);

    /// <summary>中文名。</summary>
    public string DisplayName => JunqiRanks.DisplayName(Rank);

    /// <summary>翻开这个棋子。</summary>
    public JunqiPiece WithRevealed(bool revealed) => this with { Revealed = revealed };
}

/// <summary>棋子名称与棋力描述。</summary>
public static class JunqiRanks
{
    /// <summary>中文名。</summary>
    public static string DisplayName(JunqiRank rank) => rank switch
    {
        JunqiRank.Engineer => "工兵",
        JunqiRank.PlatoonLeader => "排长",
        JunqiRank.CompanyCommander => "连长",
        JunqiRank.BattalionCommander => "营长",
        JunqiRank.RegimentalCommander => "团长",
        JunqiRank.BrigadeCommander => "旅长",
        JunqiRank.DivisionCommander => "师长",
        JunqiRank.CorpsCommander => "军长",
        JunqiRank.Commander => "司令",
        JunqiRank.Landmine => "地雷",
        JunqiRank.Bomb => "炸弹",
        JunqiRank.Flag => "军旗",
        _ => "空",
    };

    /// <summary>单字简称（棋盘上格子小，用单字更清楚：司 / 军 / 师 / 旅 / 团 / 营 / 连 / 排 / 工 / 雷 / 炸 / 旗）。</summary>
    public static string ShortName(JunqiRank rank) => rank switch
    {
        JunqiRank.Engineer => "工",
        JunqiRank.PlatoonLeader => "排",
        JunqiRank.CompanyCommander => "连",
        JunqiRank.BattalionCommander => "营",
        JunqiRank.RegimentalCommander => "团",
        JunqiRank.BrigadeCommander => "旅",
        JunqiRank.DivisionCommander => "师",
        JunqiRank.CorpsCommander => "军",
        JunqiRank.Commander => "司",
        JunqiRank.Landmine => "雷",
        JunqiRank.Bomb => "炸",
        JunqiRank.Flag => "旗",
        _ => "",
    };

    /// <summary>每种棋子有多少个（二人军棋标准配置：司令 1、军长 1、师长 2、旅长 2、团长 2、营长 2、连长 3、排长 3、工兵 3、地雷 3、炸弹 2、军旗 1 = 25 枚）。</summary>
    public static IReadOnlyList<(JunqiRank Rank, int Count)> StandardArmy { get; } = new[]
    {
        (JunqiRank.Commander, 1),
        (JunqiRank.CorpsCommander, 1),
        (JunqiRank.DivisionCommander, 2),
        (JunqiRank.BrigadeCommander, 2),
        (JunqiRank.RegimentalCommander, 2),
        (JunqiRank.BattalionCommander, 2),
        (JunqiRank.CompanyCommander, 3),
        (JunqiRank.PlatoonLeader, 3),
        (JunqiRank.Engineer, 3),
        (JunqiRank.Landmine, 3),
        (JunqiRank.Bomb, 2),
        (JunqiRank.Flag, 1),
    };

    /// <summary>一方的棋子总数（25）。</summary>
    public static int ArmySize { get; } = StandardArmy.Sum(x => x.Count);

    /// <summary>按标准配置造一份棋子列表（未摆到棋盘上）。</summary>
    public static List<JunqiRank> CreateArmy()
    {
        var list = new List<JunqiRank>(ArmySize);
        foreach (var (rank, count) in StandardArmy)
        {
            for (var i = 0; i < count; i++) list.Add(rank);
        }
        return list;
    }
}
