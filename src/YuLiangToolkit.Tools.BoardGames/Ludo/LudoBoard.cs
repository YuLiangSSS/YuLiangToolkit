namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>飞行棋的四个颜色（同时也是四个座位）。</summary>
public enum LudoColor
{
    /// <summary>红。</summary>
    Red = 0,

    /// <summary>黄。</summary>
    Yellow = 1,

    /// <summary>蓝。</summary>
    Blue = 2,

    /// <summary>绿。</summary>
    Green = 3,
}

/// <summary>颜色工具。</summary>
public static class LudoColors
{
    /// <summary>全部颜色（按顺时针坐次）。</summary>
    public static IReadOnlyList<LudoColor> All { get; } = new[]
    {
        LudoColor.Red, LudoColor.Yellow, LudoColor.Blue, LudoColor.Green,
    };

    /// <summary>中文名。</summary>
    public static string DisplayName(LudoColor color) => color switch
    {
        LudoColor.Red => "红",
        LudoColor.Yellow => "黄",
        LudoColor.Blue => "蓝",
        _ => "绿",
    };

    /// <summary>存储键 / 协议用。</summary>
    public static string ToKey(LudoColor color) => color switch
    {
        LudoColor.Red => "red",
        LudoColor.Yellow => "yellow",
        LudoColor.Blue => "blue",
        _ => "green",
    };

    /// <summary>键 → 颜色（认不出按红）。</summary>
    public static LudoColor Parse(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "yellow" => LudoColor.Yellow,
        "blue" => LudoColor.Blue,
        "green" => LudoColor.Green,
        _ => LudoColor.Red,
    };

    /// <summary>己方大本营（起飞区）在棋盘上的坐标。</summary>
    /// <summary>
    /// 己方大本营（4 个起飞位）。放在本方起点那一侧的角落 4×4 区里，
    /// 不压外圈跑道、也不压回家通道。
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> HomeCells(LudoColor color) => color switch
    {
        LudoColor.Red => new[] { (1, 10), (2, 10), (1, 11), (2, 11) },        // 左下角
        LudoColor.Yellow => new[] { (1, 1), (2, 1), (1, 2), (2, 2) },          // 左上角
        LudoColor.Blue => new[] { (10, 1), (11, 1), (10, 2), (11, 2) },        // 右上角
        _ => new[] { (10, 10), (11, 10), (10, 11), (11, 11) },                 // 右下角
    };
}

/// <summary>掷骰结果 / 规则开关（房主可调）。</summary>
public sealed class LudoRules
{
    /// <summary>掷到 6 才能起飞（经典规则）。关掉则掷到 1 或 6 都能起飞。</summary>
    public bool NeedSixToTakeOff { get; set; } = true;

    /// <summary>房主额外规则：掷 5 也可以起飞（需求里的「后门起飞」）。</summary>
    public bool FiveAlsoTakesOff { get; set; }

    /// <summary>掷到 6 可以再掷一次。</summary>
    public bool ExtraRollOnSix { get; set; } = true;

    /// <summary>进终点必须掷到刚好点数（掷多了往回退）。</summary>
    public bool ExactToFinish { get; set; } = true;

    /// <summary>踩到别人棋子把对方打回家（同格踢子）。</summary>
    public bool CaptureOnLand { get; set; } = true;

    /// <summary>经过「中间横向飞行线」时也能把对方棋子打回家（需求里的规则）。</summary>
    public bool CaptureOnFlightLine { get; set; } = true;

    /// <summary>走一圈后可以继续绕（关掉则必须在终点停）。</summary>
    public bool AllowLap { get; set; }

    /// <summary>复制一份。</summary>
    public LudoRules Clone() => new()
    {
        NeedSixToTakeOff = NeedSixToTakeOff,
        FiveAlsoTakesOff = FiveAlsoTakesOff,
        ExtraRollOnSix = ExtraRollOnSix,
        ExactToFinish = ExactToFinish,
        CaptureOnLand = CaptureOnLand,
        CaptureOnFlightLine = CaptureOnFlightLine,
        AllowLap = AllowLap,
    };
}

/// <summary>
/// 飞行棋棋盘几何：<b>15×15</b> 的十字形外圈 + 四条回家通道 + 中央终点。
///
/// <para><b>路线</b>：外圈一共 52 格（标准 Ludo 布局），每个颜色从自己的起飞点开始，
/// 顺时针走 52 格回到起飞点前一格，然后进入自己的 <b>回家通道</b>（6 格），最后到中央终点。</para>
///
/// <para><b>「中间那条横的飞行线」</b>：外圈里有一段横向的直道（标准 Ludo 里
/// 是 (6..8, 7) 与 (7, 6..8) 这类中心十字段）。棋子在走这条直道时会「飞过去」——
/// 按需求，如果别人刚好停在这条线上，也会被打回家。</para>
/// </summary>
public static class LudoBoard
{
    /// <summary>棋盘边长（15）。</summary>
    public const int Size = 15;

    /// <summary>外圈总格数（52）。</summary>
    public const int TrackLength = 52;

    /// <summary>回家通道格数（6）。</summary>
    public const int HomeStretch = 6;

    /// <summary>每个颜色棋子数（4）。</summary>
    public const int PiecesPerColor = 4;

    /// <summary>
    /// 外圈 52 格的坐标（从红光起飞点开始顺时针）。
    /// <para>用标准 Ludo 十字布局推导：外圈 6 格一段 × 8 段 + 4 个角 = 52。</para>
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> Track { get; } = BuildTrack();

    /// <summary>某一颜色的起飞点在外圈上的下标。</summary>
    public static int StartIndex(LudoColor color) => color switch
    {
        LudoColor.Red => 0,
        LudoColor.Yellow => 13,
        LudoColor.Blue => 26,
        _ => 39,
    };

    /// <summary>某一颜色回家通道的坐标（从外圈进入，走到终点）。</summary>
    /// <summary>
    /// 某一颜色的回家通道（从外圈进入中央终点，共 6 格）。
    /// <para>方向要跟「起点在哪一侧」一致：红方起点在底边（(6,13)），通道就是中列从下往上；
    /// 黄方起点在左边（(1,6)），通道就是中行从左往右；蓝方在顶边、绿方在右边。</para>
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> HomeStretchCells(LudoColor color) => color switch
    {
        LudoColor.Red => new[] { (7, 13), (7, 12), (7, 11), (7, 10), (7, 9), (7, 8) },     // 底 → 中
        LudoColor.Yellow => new[] { (1, 7), (2, 7), (3, 7), (4, 7), (5, 7), (6, 7) },      // 左 → 中
        LudoColor.Blue => new[] { (7, 1), (7, 2), (7, 3), (7, 4), (7, 5), (7, 6) },        // 顶 → 中
        _ => new[] { (13, 7), (12, 7), (11, 7), (10, 7), (9, 7), (8, 7) },                // 右 → 中
    };

    /// <summary>中央终点坐标。</summary>
    public static (int X, int Y) Finish => (7, 7);

    /// <summary>
    /// 「中间横向飞行线」的坐标集合：外圈穿过中心十字的那几格。
    /// <para>这些格子两侧直连，棋子经过时算「飞过去」，可以打到停在这里的对方棋子。</para>
    /// </summary>
    public static IReadOnlySet<(int X, int Y)> FlightLine { get; } = new HashSet<(int X, int Y)>
    {
        (6, 7), (7, 7), (8, 7),
        (7, 6), (7, 8),
    };

    /// <summary>某一点是不是飞行线。</summary>
    public static bool IsFlightLine(int x, int y) => FlightLine.Contains((x, y));

    /// <summary>
    /// 【测试 / 自检用】外圈坐标是不是合法（都在棋盘内、且不重复）。
    /// </summary>
    public static bool IsTrackValid()
    {
        if (Track.Count != TrackLength) return false;
        if (Track.Distinct().Count() != TrackLength) return false;
        return Track.All(p => p.X >= 0 && p.Y >= 0 && p.X < Size && p.Y < Size);
    }

    /// <summary>构造外圈 52 格（标准 Ludo 十字路线）。</summary>
    private static List<(int X, int Y)> BuildTrack()
    {
        // 标准 15×15 Ludo：十字宽 3，外圈 52 格
        // 从红方起飞点 (6,13) 开始，顺时针（左 → 上 → 右 → 下）
        var track = new List<(int X, int Y)>(TrackLength);

        // ① 左侧竖道：从 (6,13) 上行到 (6,9)
        for (var y = 13; y >= 9; y--) track.Add((6, y));

        // ② 左上角： (5,8) (4,8) (3,8) (2,8) (1,8) (0,8)

        for (var x = 5; x >= 0; x--) track.Add((x, 8));

        // ③ 左侧上拐： (0,7) (0,6)
        track.Add((0, 7));
        track.Add((0, 6));

        // ④ 上方横道： (1,6) .. (5,6)
        for (var x = 1; x <= 5; x++) track.Add((x, 6));

        // ⑤ 上方竖道： (6,5) .. (6,0)
        for (var y = 5; y >= 0; y--) track.Add((6, y));

        // ⑥ 右上角： (7,0) (8,0)
        track.Add((7, 0));
        track.Add((8, 0));

        // ⑦ 右上方竖道： (8,1) .. (8,5)
        for (var y = 1; y <= 5; y++) track.Add((8, y));

        // ⑧ 右上横道： (9,6) .. (14,6)
        for (var x = 9; x <= 14; x++) track.Add((x, 6));

        // ⑨ 右侧： (14,7) (14,8)
        track.Add((14, 7));
        track.Add((14, 8));

        // ⑩ 右下横道： (13,8) .. (9,8)
        for (var x = 13; x >= 9; x--) track.Add((x, 8));

        // ⑪ 右侧下行： (8,9) .. (8,14)
        for (var y = 9; y <= 14; y++) track.Add((8, y));

        // ⑫ 底部： (7,14) (6,14)
        track.Add((7, 14));
        track.Add((6, 14));

        return track;
    }
}
