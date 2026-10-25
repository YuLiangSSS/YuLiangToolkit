namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>
/// 五子棋的「坐标 / 棋谱 / 输赢」文案——<b>纯函数</b>，界面、快照、自检都用它，保证文案只在一个地方定义。
///
/// <para>坐标规则（和棋谱惯例一致）：列用字母 <c>A</c> 起（从左往右），
/// 行号<b>从下往上</b>从 1 数（棋盘最下面一行是 1）。15 路时是 <c>A~O / 1~15</c>，
/// 19 路时是 <c>A~S / 1~19</c>；天元（15 路的正中心）是 <c>H8</c>。</para>
/// </summary>
public static class GomokuText
{
    /// <summary>把坐标换成棋谱坐标，例如 15 路天元 <c>(7,7)</c> → <c>H8</c>。越界返回 <c>?</c>。</summary>
    public static string Coordinate(int x, int y, int size)
    {
        if (size <= 0 || !InRange(x, size) || !InRange(y, size)) return "?";
        return ColumnName(x) + (size - y).ToString();
    }

    /// <summary>一手棋的落子方 + 坐标，例如 <c>黑 H8</c>。坐标越界时退回 <c>(x,y)</c> 形式。</summary>
    public static string DescribeMove(GomokuMove move, int size)
    {
        if (move is null) return "?";

        var cell = ParseSide(move.Player);
        var side = cell == GomokuCell.Empty ? move.Player ?? "" : SideShortName(cell);
        var coordinate = Coordinate(move.X, move.Y, size);
        if (coordinate == "?") coordinate = $"({move.X},{move.Y})";
        return $"{side} {coordinate}".Trim();
    }

    /// <summary>棋谱里的一整行，例如 <c>1. 黑 H8</c>。</summary>
    public static string MoveLine(GomokuMove move, int size)
    {
        if (move is null) return "?";
        var number = move.Number > 0 ? move.Number : 0;
        return number > 0 ? $"{number}. {DescribeMove(move, size)}" : DescribeMove(move, size);
    }

    /// <summary>格子的中文名：黑棋 / 白棋 / 空。</summary>
    public static string SideName(GomokuCell cell) => cell switch
    {
        GomokuCell.Black => "黑棋",
        GomokuCell.White => "白棋",
        _ => "空",
    };

    /// <summary>格子的中文简称（棋谱用）：黑 / 白 / 空。</summary>
    public static string SideShortName(GomokuCell cell) => cell switch
    {
        GomokuCell.Black => "黑",
        GomokuCell.White => "白",
        _ => "空",
    };

    /// <summary>格子的协议字符串：<c>black</c> / <c>white</c> / 空字符串。</summary>
    public static string SideKey(GomokuCell cell) => cell switch
    {
        GomokuCell.Black => "black",
        GomokuCell.White => "white",
        _ => "",
    };

    /// <summary>协议字符串 → 格子（认不出返回 <see cref="GomokuCell.Empty"/>）。</summary>
    public static GomokuCell ParseSide(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return GomokuCell.Empty;
        var text = key.Trim();
        if (text.Equals("black", StringComparison.OrdinalIgnoreCase) || text == "b" || text == "黑" || text == "黑棋")
        {
            return GomokuCell.Black;
        }

        if (text.Equals("white", StringComparison.OrdinalIgnoreCase) || text == "w" || text == "白" || text == "白棋")
        {
            return GomokuCell.White;
        }

        return GomokuCell.Empty;
    }

    /// <summary>输赢的中文文案（对局进行中 / 黑棋获胜 / 白棋获胜 / 和棋）。</summary>
    public static string ResultText(GomokuResult result) => result switch
    {
        GomokuResult.BlackWin => "黑棋获胜",
        GomokuResult.WhiteWin => "白棋获胜",
        GomokuResult.Draw => "和棋",
        _ => "对局进行中",
    };

    /// <summary>结果的协议字符串：playing / blackWin / whiteWin / draw。</summary>
    public static string ResultKey(GomokuResult result) => result switch
    {
        GomokuResult.BlackWin => "blackWin",
        GomokuResult.WhiteWin => "whiteWin",
        GomokuResult.Draw => "draw",
        _ => "playing",
    };

    /// <summary>协议字符串 → 结果（认不出按「对局进行中」处理）。</summary>
    public static GomokuResult ParseResult(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return GomokuResult.Playing;
        return key.Trim().ToLowerInvariant() switch
        {
            "blackwin" => GomokuResult.BlackWin,
            "whitewin" => GomokuResult.WhiteWin,
            "draw" => GomokuResult.Draw,
            _ => GomokuResult.Playing,
        };
    }

    /// <summary>阵型的规模文案，例如 <c>115 手</c>（界面用）。</summary>
    public static string MoveCountText(int count) => $"{count} 手";

    private static bool InRange(int value, int size) => value >= 0 && value < size;

    private static string ColumnName(int x)
    {
        if (x < 26) return ((char)('A' + x)).ToString();

        // 理论上只用到 A~S；留个 AA、AB… 的兜底，别因为路数大就崩。
        var major = (char)('A' + x / 26 - 1);
        var minor = (char)('A' + x % 26);
        return $"{major}{minor}";
    }
}
