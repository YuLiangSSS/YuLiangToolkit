namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 摆棋相关：标准开局 FEN、让子预设、自由摆棋校验。
///
/// <para><b>让子</b>：让子方（giver）从自己这边去掉棋子；「左手边」按让子方自己的视角算
/// （红方在下、左手边是屏幕左边；黑方在上、左手边是屏幕右边）。</para>
/// </summary>
public static class XiangqiSetup
{
    /// <summary>
    /// 标准开局 FEN（红方在下、黑方在上，红先）。字母约定：红大写 / 黑小写，
    /// 马 = H、象 = E（解析时也认国际通用的 N / B）。
    /// </summary>
    public static readonly string InitialFen =
        "rheakaehr/9/1c5c1/p1p1p1p1p/9/9/P1P1P1P1P/1C5C1/9/RHEAKAEHR w - - 0 1";

    /// <summary>让子种类（只列常用的六种）。</summary>
    public enum XiangqiHandicap
    {
        /// <summary>让单马。</summary>
        SingleHorse = 0,

        /// <summary>让双马。</summary>
        DoubleHorse,

        /// <summary>让单车。</summary>
        SingleRook,

        /// <summary>让车马。</summary>
        RookAndHorse,

        /// <summary>让车马炮。</summary>
        RookHorseCannon,

        /// <summary>让九子（双车、双马、双炮、双相、一仕）。</summary>
        NinePieces,
    }

    /// <summary>一个让子 / 开局预设。<see cref="Handicap"/> 为 null 表示标准开局（不让子）。</summary>
    public sealed record Preset(string Name, XiangqiHandicap? Handicap, string Description);

    /// <summary>界面上的预设清单：标准开局 + 6 个让子预设。</summary>
    public static IReadOnlyList<Preset> Presets { get; } = new[]
    {
        new Preset("标准开局", null, "完整标准布局，红方先走"),
        new Preset("让单马", XiangqiHandicap.SingleHorse, "让子方去掉一匹马"),
        new Preset("让双马", XiangqiHandicap.DoubleHorse, "让子方去掉两匹马"),
        new Preset("让单车", XiangqiHandicap.SingleRook, "让子方去掉一个车"),
        new Preset("让车马", XiangqiHandicap.RookAndHorse, "让子方去掉一个车和一匹马"),
        new Preset("让车马炮", XiangqiHandicap.RookHorseCannon, "让子方去掉一个车、一匹马、一个炮"),
        new Preset("让九子", XiangqiHandicap.NinePieces, "让子方去掉双车、双马、双炮、双相和一仕（共 9 子）"),
    };

    /// <summary>
    /// 在 <paramref name="board"/> 的副本上应用让子：从 <paramref name="giver"/> 一方（让子方）去掉对应棋子。
    /// 不修改传入的棋盘。
    /// </summary>
    public static XiangqiBoard ApplyHandicap(XiangqiBoard board, XiangqiSide giver, XiangqiHandicap handicap)
    {
        ArgumentNullException.ThrowIfNull(board);
        var result = board.Clone();
        switch (handicap)
        {
            case XiangqiHandicap.SingleHorse:
                RemoveFromGiver(result, giver, XiangqiPieceType.Horse, 1);
                break;
            case XiangqiHandicap.DoubleHorse:
                RemoveFromGiver(result, giver, XiangqiPieceType.Horse, 2);
                break;
            case XiangqiHandicap.SingleRook:
                RemoveFromGiver(result, giver, XiangqiPieceType.Rook, 1);
                break;
            case XiangqiHandicap.RookAndHorse:
                RemoveFromGiver(result, giver, XiangqiPieceType.Rook, 1);
                RemoveFromGiver(result, giver, XiangqiPieceType.Horse, 1);
                break;
            case XiangqiHandicap.RookHorseCannon:
                RemoveFromGiver(result, giver, XiangqiPieceType.Rook, 1);
                RemoveFromGiver(result, giver, XiangqiPieceType.Horse, 1);
                RemoveFromGiver(result, giver, XiangqiPieceType.Cannon, 1);
                break;
            case XiangqiHandicap.NinePieces:
                RemoveFromGiver(result, giver, XiangqiPieceType.Rook, 2);
                RemoveFromGiver(result, giver, XiangqiPieceType.Horse, 2);
                RemoveFromGiver(result, giver, XiangqiPieceType.Cannon, 2);
                RemoveFromGiver(result, giver, XiangqiPieceType.Elephant, 2);
                RemoveFromGiver(result, giver, XiangqiPieceType.Advisor, 1);
                break;
        }
        return result;
    }

    /// <summary>
    /// 从让子方那边去掉 <paramref name="count"/> 枚某种棋子，优先去掉让子方视角的「左边」那几枚
    /// （红方左手边 = 屏幕左边；黑方左手边 = 屏幕右边）。棋盘上不够就只去掉现有的。
    /// </summary>
    private static void RemoveFromGiver(XiangqiBoard board, XiangqiSide giver, XiangqiPieceType type, int count)
    {
        var pieces = new List<(int X, int Y)>(board.FindPieces(giver, type));
        if (giver == XiangqiSide.Red)
        {
            // 红方在下、面朝上：左手边是 x 小的一侧
            pieces.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : b.Y.CompareTo(a.Y));
        }
        else
        {
            // 黑方在上、面朝下：左手边是 x 大的一侧
            pieces.Sort((a, b) => a.X != b.X ? b.X.CompareTo(a.X) : a.Y.CompareTo(b.Y));
        }
        for (var i = 0; i < Math.Min(count, pieces.Count); i++)
        {
            board.Set(pieces[i].X, pieces[i].Y, XiangqiPiece.Empty);
        }
    }

    /// <summary>
    /// 自由摆棋校验。返回 null = 可以开局；否则是给用户看的中文错误原因。
    ///
    /// <para>检查：双方各必须有且只有一个将 / 帅、都在九宫内、将帅不能照面、
    /// <paramref name="notToMove"/>（非行棋方）不能被将军（传 null 可跳过最后一条）。</para>
    /// </summary>
    public static string? Validate(XiangqiBoard board, XiangqiSide turn, XiangqiSide? notToMove = null)
    {
        if (board is null) return "棋盘不存在";

        var redKings = board.Count(XiangqiPieceType.King, XiangqiSide.Red);
        if (redKings == 0) return "红方缺少帅，请先在九宫里放一个帅";
        if (redKings > 1) return $"红方有 {redKings} 个帅，只能有一个";

        var blackKings = board.Count(XiangqiPieceType.King, XiangqiSide.Black);
        if (blackKings == 0) return "黑方缺少将，请先在九宫里放一个将";
        if (blackKings > 1) return $"黑方有 {blackKings} 个将，只能有一个";

        foreach (var side in new[] { XiangqiSide.Red, XiangqiSide.Black })
        {
            var kings = board.FindPieces(side, XiangqiPieceType.King);
            foreach (var (x, y) in kings)
            {
                if (!XiangqiRules.InPalace(side, x, y))
                {
                    return side == XiangqiSide.Red
                        ? "红方的帅必须放在九宫内（中线三列、红方底线三行）"
                        : "黑方的将必须放在九宫内（中线三列、黑方底线三行）";
                }
            }
        }

        if (XiangqiRules.AreKingsFacing(board))
        {
            return "将帅照面：两个将 / 帅在同一列且中间没有棋子，请挪动或补一个棋子";
        }

        if (notToMove is { } otherSide && XiangqiRules.IsInCheck(board, otherSide))
        {
            return $"{otherSide.DisplayName()}不是行棋方，现在不能被将军（否则对方等于白吃将）";
        }

        return null;
    }

    /// <summary>这个棋盘是不是标准开局（摆棋界面用来显示「已恢复默认开局」之类的提示）。</summary>
    public static bool IsInitialBoard(XiangqiBoard? board)
    {
        if (board is null) return false;
        var (initial, _, _, error) = XiangqiBoard.ParseFen(InitialFen);
        if (!string.IsNullOrEmpty(error)) return false;
        for (var x = 0; x < XiangqiBoard.Width; x++)
        {
            for (var y = 0; y < XiangqiBoard.Height; y++)
            {
                if (board.Get(x, y) != initial.Get(x, y)) return false;
            }
        }
        return true;
    }

    /// <summary>给界面用：让子预设的名字（认不出返回「自定义」）。</summary>
    public static string HandicapName(XiangqiHandicap? handicap)
    {
        if (handicap is null) return "标准开局";
        foreach (var preset in Presets)
        {
            if (preset.Handicap == handicap) return preset.Name;
        }
        return "自定义";
    }
}
