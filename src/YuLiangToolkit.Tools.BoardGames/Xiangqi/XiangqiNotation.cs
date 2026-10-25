namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 中国象棋中文记谱（<b>只算文本，不碰棋局状态</b>）。
///
/// <para><b>规则</b>：</para>
/// <list type="bullet">
///   <item>红方路数从<b>红方右侧</b>往左数「一~九」（红方在下 → 屏幕右边的第一路是一）；
///         黑方路数从<b>黑方右侧</b>往左数「1~9」（黑方在上 → 屏幕左边的第一路是 1）。</item>
///   <item>直行（车 / 炮 / 帅 / 兵卒向前）用「进 / 退 + 走了几路」；平移用「平 + 目标路数」；
///         马 / 相 / 仕斜走用「进 / 退 + 目标路数」。</item>
///   <item>同一路上有多个同种子时，用「前 / 后（三子还有 中）」代替起点路数，例如「前车进一」。</item>
/// </list>
/// <example>
/// <code>
/// XiangqiNotation.Describe(board, new XiangqiMove(7, 7, 4, 7));   // → "炮二平五"
/// XiangqiNotation.Describe(board, new XiangqiMove(7, 0, 6, 2));   // → "马8进7"
/// XiangqiNotation.Describe(board, new XiangqiMove(0, 9, 0, 8));   // → "车九进一"
/// </code>
/// </example>
public static class XiangqiNotation
{
    private static readonly string[] RedDigits = { "一", "二", "三", "四", "五", "六", "七", "八", "九" };

    /// <summary>
    /// 算一步棋的中文记谱（例如 <c>炮二平五</c>）。<paramref name="before"/> 是<b>走子之前</b>的棋盘
    /// （要数同一路上有几个同种子）。着法不合法 / 起点没棋子时返回空字符串。
    /// </summary>
    public static string Describe(XiangqiBoard before, XiangqiMove move)
    {
        if (before is null || move.IsNone) return "";
        var piece = before.Get(move.FromX, move.FromY);
        if (piece.IsEmpty) return "";

        var isRed = piece.Side == XiangqiSide.Red;
        // 「前 / 后」要放在棋子名前面（前车进一），普通情况是「棋子 + 起点路数」（炮二平五）
        var prefix = DescribePositionPrefix(before, piece, move.FromX, move.FromY, isRed);
        var head = prefix is null ? piece.DisplayChar + FileLabel(move.FromX, isRed) : prefix + piece.DisplayChar;
        var dx = move.ToX - move.FromX;
        var dy = move.ToY - move.FromY;

        string action;
        string destination;
        if (dx == 0)
        {
            // 竖着走：进 / 退 + 步数
            var forward = isRed ? dy < 0 : dy > 0;
            action = forward ? "进" : "退";
            destination = Numeral(Math.Abs(dy), isRed);
        }
        else if (dy == 0)
        {
            // 横着走 = 平 + 目标路数
            action = "平";
            destination = FileLabel(move.ToX, isRed);
        }
        else
        {
            // 斜着走（马 / 相 / 仕）：进 / 退 + 目标路数
            var forward = isRed ? dy < 0 : dy > 0;
            action = forward ? "进" : "退";
            destination = FileLabel(move.ToX, isRed);
        }

        return head + action + destination;
    }

    /// <summary>
    /// 起点部分：一般用起点路数；同一路有多个同种子时返回「前 / 中 / 后」前缀（没有就返回 null）。
    /// </summary>
    private static string? DescribePositionPrefix(XiangqiBoard board, XiangqiPiece piece, int x, int y, bool isRed)
    {
        var sameFile = new List<int>(5);
        for (var yy = 0; yy < XiangqiBoard.Height; yy++)
        {
            var other = board.Get(x, yy);
            if (!other.IsEmpty && other.Type == piece.Type && other.Side == piece.Side) sameFile.Add(yy);
        }

        if (sameFile.Count < 2) return null;

        // 从「前」往「后」排：前 = 离对方更近。红方在下 → y 小的是前；黑方在上 → y 大的是前
        sameFile.Sort();
        if (!isRed) sameFile.Reverse();
        var index = sameFile.IndexOf(y);
        if (index < 0) return null;
        return PositionPrefix(index, sameFile.Count, isRed);
    }

    /// <summary>
    /// 同路多子的位置前缀：前 / 中 / 后（4 个以上中间用第二、第三……表示）。
    /// </summary>
    private static string PositionPrefix(int index, int count, bool isRed)
    {
        if (index == 0) return "前";
        if (index == count - 1) return "后";
        if (count == 3) return "中";
        return isRed ? RedDigits[index] : (index + 1).ToString();
    }

    /// <summary>路数：红方 9-x（右起一~九），黑方 x+1（左起 1~9）。</summary>
    public static string FileLabel(int x, bool isRed)
        => isRed ? Numeral(9 - x, true) : Numeral(x + 1, false);

    /// <summary>数字：红方用汉字「一~九」，黑方用阿拉伯数字。</summary>
    public static string Numeral(int value, bool isRed)
    {
        if (value < 1) return isRed ? RedDigits[0] : "1";
        if (isRed) return value <= 9 ? RedDigits[value - 1] : value.ToString();
        return value.ToString();
    }

    /// <summary>一行的序号前缀："1. 红" / "1... 黑"（按契约）。</summary>
    public static string FormatMoveNumber(int index, XiangqiSide side)
        => side == XiangqiSide.Red ? $"{index}. 红" : $"{index}... 黑";

    /// <summary>
    /// 把棋谱排成「一行两个半回合」的文本行（界面棋谱列表直接用）：
    /// <c>1. 炮二平五    马8进7</c>。
    /// </summary>
    public static IReadOnlyList<string> FormatHistory(IReadOnlyList<XiangqiMoveInfo>? history)
    {
        var lines = new List<string>();
        if (history is null) return lines;
        for (var i = 0; i < history.Count; i += 2)
        {
            var first = history[i];
            if (first is null) continue;
            var line = $"{first.Number}. {first.Notation}";
            if (i + 1 < history.Count)
            {
                var second = history[i + 1];
                if (second is not null)
                {
                    var gap = second.Notation.Length + first.Notation.Length < 8 ? "        " : "    ";
                    line += gap + second.Notation;
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>把整段棋谱拼成可复制的纯文本（每行两条，带序号）。</summary>
    public static string BuildHistoryText(IReadOnlyList<XiangqiMoveInfo>? history)
        => string.Join(Environment.NewLine, FormatHistory(history));
}
