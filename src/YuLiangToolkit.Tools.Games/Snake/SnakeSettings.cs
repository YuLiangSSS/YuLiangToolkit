namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>
/// 贪吃蛇对局设置（房主可改；界面 / 配置 / 联机协议共用）。
/// </summary>
public sealed class SnakeSettings
{
    // ---------------------------------------------------------------- 棋盘

    /// <summary>棋盘宽度（格）。</summary>
    public int Width { get; set; } = 40;

    /// <summary>棋盘高度（格）。</summary>
    public int Height { get; set; } = 30;

    // ---------------------------------------------------------------- 玩法

    /// <summary>玩法。</summary>
    public SnakeMode Mode { get; set; } = SnakeMode.BattleRoyale;

    /// <summary>共享果玩法：分几个区域（= 几条蛇）。</summary>
    public int RegionCount { get; set; } = 2;

    /// <summary>人数上限（含 AI）。</summary>
    public int MaxPlayers { get; set; } = 4;

    /// <summary>果子刷新数量上限（共享果玩法恒为 1）。</summary>
    public int MaxFruits { get; set; } = 5;

    // ---------------------------------------------------------------- 生命 / 时间

    /// <summary>每条蛇有几条命（玩法二用；玩法一可以设 1 或更多）。</summary>
    public int Lives { get; set; } = 3;

    /// <summary>开局倒计时（秒）。</summary>
    public int CountdownSeconds { get; set; } = 3;

    /// <summary>限时（步数；0 = 不限时）。</summary>
    public int TimeLimitSteps { get; set; }

    /// <summary>开局蛇长。</summary>
    public int InitialLength { get; set; } = SnakeGame.InitialLength;

    // ---------------------------------------------------------------- 规则

    /// <summary>撞到边缘怎么处理。</summary>
    public SnakeEdgeMode EdgeMode { get; set; } = SnakeEdgeMode.Die;

    /// <summary>共享果玩法：没吃到果子时要不要断 combo。</summary>
    public bool ComboBreaksOnMiss { get; set; } = true;

    /// <summary>重生是否保留原蛇身长度。</summary>
    public bool RespawnKeepsLength { get; set; } = true;

    /// <summary>出生点是否产生 3×3 爆炸（防堵点）。</summary>
    public bool SpawnBombEnabled { get; set; } = true;

    // ---------------------------------------------------------------- 加速

    /// <summary>是否开启「按住加速」。</summary>
    public bool SpeedBoostEnabled { get; set; } = true;

    /// <summary>加速倍率（按住加速键时步进速度是平时的多少倍）。</summary>
    public double SpeedBoostMultiplier { get; set; } = 2.0;

    // ---------------------------------------------------------------- AI

    /// <summary>AI 数量（房主设置）。</summary>
    public int AiCount { get; set; } = 1;

    /// <summary>AI 难度（1~9，对应九条蛇算法；越高越聪明）。</summary>
    public int AiLevel { get; set; } = 3;

    /// <summary>基础爬行速度（格子 / 秒）。</summary>
    public double Speed { get; set; } = 8;

    /// <summary>复制一份（房间 / 联机广播时避免共享引用）。</summary>
    public SnakeSettings Clone() => new()
    {
        Width = Width,
        Height = Height,
        Mode = Mode,
        RegionCount = RegionCount,
        MaxPlayers = MaxPlayers,
        MaxFruits = MaxFruits,
        Lives = Lives,
        CountdownSeconds = CountdownSeconds,
        TimeLimitSteps = TimeLimitSteps,
        InitialLength = InitialLength,
        EdgeMode = EdgeMode,
        ComboBreaksOnMiss = ComboBreaksOnMiss,
        RespawnKeepsLength = RespawnKeepsLength,
        SpawnBombEnabled = SpawnBombEnabled,
        SpeedBoostEnabled = SpeedBoostEnabled,
        SpeedBoostMultiplier = SpeedBoostMultiplier,
        AiCount = AiCount,
        AiLevel = AiLevel,
        Speed = Speed,
    };

    /// <summary>把设置夹到合法范围（界面输入 / 联机收到脏数据时用）。</summary>
    public void Normalize()
    {
        Width = Math.Clamp(Width, 12, 160);
        Height = Math.Clamp(Height, 12, 120);
        RegionCount = Math.Clamp(RegionCount, 2, 6);
        MaxPlayers = Math.Clamp(MaxPlayers, 2, 8);
        MaxFruits = Math.Clamp(MaxFruits, 1, 40);
        Lives = Math.Clamp(Lives, 1, 10);
        CountdownSeconds = Math.Clamp(CountdownSeconds, 0, 10);
        TimeLimitSteps = Math.Clamp(TimeLimitSteps, 0, 100_000);
        InitialLength = Math.Clamp(InitialLength, 2, 12);
        SpeedBoostMultiplier = Math.Clamp(SpeedBoostMultiplier, 1.2, 4.0);
        AiCount = Math.Clamp(AiCount, 0, 7);
        AiLevel = Math.Clamp(AiLevel, 1, 9);
        Speed = Math.Clamp(Speed, 1, 30);

        if (Mode == SnakeMode.SharedFruit) MaxFruits = 1;
    }

    /// <summary>边缘规则的中文名。</summary>
    public static string EdgeModeName(SnakeEdgeMode mode) => mode switch
    {
        SnakeEdgeMode.Stop => "停住不动",
        SnakeEdgeMode.Wrap => "穿墙（从另一边出来）",
        _ => "撞墙即死",
    };

    /// <summary>玩法的中文名。</summary>
    public static string ModeName(SnakeMode mode)
        => mode == SnakeMode.SharedFruit ? "共享果（分区域抢果）" : "大混战（多蛇多果）";
}
