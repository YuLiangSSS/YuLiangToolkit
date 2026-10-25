namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>黑棋禁手的种类。</summary>
public enum GomokuForbiddenKind
{
    /// <summary>不是禁手。</summary>
    None = 0,

    /// <summary>长连禁手：黑棋连成六子及以上一条线（五连不算，五连是赢）。</summary>
    Overline = 1,

    /// <summary>四四禁手：一手同时形成两个「四」（再下一子就能成五的点）。</summary>
    DoubleFour = 2,

    /// <summary>三三禁手：一手同时形成两个「活三」。</summary>
    DoubleThree = 3,
}

/// <summary>
/// 黑棋在某个空点落子的禁手判定结果。
/// <para><see cref="IsWin"/> = 这一手正好形成五连（五连优先于长连与其它禁手，算赢）；
/// <see cref="IsForbidden"/> = 这一手是禁手（黑棋立即判负）；两者不会同时为真。</para>
/// </summary>
/// <param name="IsForbidden">是不是禁手。</param>
/// <param name="Kind">禁手种类（不是禁手时是 <see cref="GomokuForbiddenKind.None"/>）。</param>
/// <param name="IsWin">这一手是不是正好五连（禁手判定里五连优先，算赢）。</param>
/// <param name="Reason">给用户看的中文原因（不是禁手 / 不是五连时为空字符串）。</param>
/// <param name="FourCount">这一手在四个方向上一共形成了几个「四」（每个方向最多算 1 个）。</param>
/// <param name="OpenThreeCount">这一手在四个方向上一共形成了几个「活三」（每个方向最多算 1 个）。</param>
public sealed record GomokuForbiddenInfo(
    bool IsForbidden,
    GomokuForbiddenKind Kind,
    bool IsWin,
    string Reason,
    int FourCount = 0,
    int OpenThreeCount = 0);

/// <summary>
/// 黑棋禁手判定（三三 / 四四 / 长连）——<b>纯函数，不碰界面与网络</b>。
///
/// <para>判定口径（可读、可测，不追求连珠规则全集）：</para>
/// <list type="bullet">
///   <item>只对<b>黑棋</b>生效；白棋没有禁手（白棋长连也算赢）。</item>
///   <item><b>五连优先</b>：这一手在任何方向上<b>正好</b>连成 5 子 → 算赢，不再是禁手
///         （哪怕另一方向同时出现长连）。</item>
///   <item><b>长连</b>：任一方向连成 6 子及以上 → 长连禁手。</item>
///   <item><b>四</b>：某个方向存在一个 5 格窗口「4 黑 + 1 空」，并且该窗口包含落子点
///         → 这个方向算 1 个四（再下一子就能成五）。</item>
///   <item><b>活三</b>：某个方向上存在一个空点，黑棋下在那里能形成「活四」
///         （连续 4 黑且两端皆空、且这个活四包含本次落子点）→ 这个方向算 1 个活三。</item>
///   <item>四 ≥ 2 = 四四禁手；活三 ≥ 2 = 三三禁手；长连优先于四四 / 三三。</item>
/// </list>
/// </summary>
public static class GomokuForbidden
{
    // 四个方向的单位向量：横、竖、撇（右上到左下）、捺（左上到右下）。
    private static readonly (int Dx, int Dy)[] Directions =
    {
        (1, 0),
        (0, 1),
        (1, 1),
        (1, -1),
    };

    /// <summary>
    /// 黑棋若落在 <paramref name="x"/>,<paramref name="y"/> 会不会形成禁手。
    /// <para>棋盘该点必须为空；<paramref name="forbiddenEnabled"/> 为 false 时永远返回「不是禁手」。</para>
    /// </summary>
    public static GomokuForbiddenInfo Evaluate(GomokuBoard board, int x, int y, bool forbiddenEnabled)
    {
        if (board is null || !board.InBounds(x, y) || !board.IsEmpty(x, y))
        {
            return NotForbidden();
        }

        if (!forbiddenEnabled)
        {
            return NotForbidden();
        }

        // 在副本上真的摆一颗黑子再算，绝不改动调用方的棋盘。
        var probe = board.Clone();
        probe.Set(x, y, GomokuCell.Black);
        return EvaluatePlacedBlack(probe, x, y);
    }

    /// <summary>
    /// 当前棋盘上所有「黑棋禁手点」（必须是空点）；<paramref name="forbiddenEnabled"/> 为 false 时返回空表。
    /// <para>界面用它画红色小叉（设置里「显示禁手点」开着时才需要）。</para>
    /// </summary>
    public static IReadOnlyList<GomokuPoint> FindForbiddenPoints(GomokuBoard board, bool forbiddenEnabled)
    {
        if (board is null || !forbiddenEnabled) return Array.Empty<GomokuPoint>();

        var points = new List<GomokuPoint>();
        for (var y = 0; y < board.Size; y++)
        {
            for (var x = 0; x < board.Size; x++)
            {
                if (!board.IsEmpty(x, y)) continue;
                if (Evaluate(board, x, y, forbiddenEnabled).IsForbidden)
                {
                    points.Add(new GomokuPoint(x, y));
                }
            }
        }

        return points;
    }

    /// <summary>
    /// 在「已经摆好黑子」的棋盘上判定（内部用：<see cref="Evaluate"/> 与 <see cref="GomokuGame"/> 都调它）。
    /// </summary>
    internal static GomokuForbiddenInfo EvaluatePlacedBlack(GomokuBoard board, int x, int y)
    {
        var hasWin = false;
        var hasOverline = false;
        var fourCount = 0;
        var openThreeCount = 0;

        foreach (var (dx, dy) in Directions)
        {
            var run = CountRun(board, x, y, dx, dy, GomokuCell.Black);
            if (run == 5) hasWin = true;
            else if (run >= 6) hasOverline = true;

            if (HasFourInDirection(board, x, y, dx, dy)) fourCount++;
            if (HasOpenThreeInDirection(board, x, y, dx, dy)) openThreeCount++;
        }

        // 五连优先于长连 / 四四 / 三三：正好五连永远算赢。
        if (hasWin)
        {
            return new GomokuForbiddenInfo(false, GomokuForbiddenKind.None, true,
                "正好五连，算赢（五连优先于长连与其它禁手）", fourCount, openThreeCount);
        }

        if (hasOverline)
        {
            return new GomokuForbiddenInfo(true, GomokuForbiddenKind.Overline, false,
                "长连禁手：黑棋连成了六子及以上的一条线", fourCount, openThreeCount);
        }

        if (fourCount >= 2)
        {
            return new GomokuForbiddenInfo(true, GomokuForbiddenKind.DoubleFour, false,
                $"四四禁手：这一手同时形成了 {fourCount} 个四", fourCount, openThreeCount);
        }

        if (openThreeCount >= 2)
        {
            return new GomokuForbiddenInfo(true, GomokuForbiddenKind.DoubleThree, false,
                $"三三禁手：这一手同时形成了 {openThreeCount} 个活三", fourCount, openThreeCount);
        }

        return new GomokuForbiddenInfo(false, GomokuForbiddenKind.None, false, "", fourCount, openThreeCount);
    }

    /// <summary>沿一个方向数 (x,y) 所在同色连线的长度（包括自己）。</summary>
    internal static int CountRun(GomokuBoard board, int x, int y, int dx, int dy, GomokuCell color)
    {
        if (color == GomokuCell.Empty || board.Get(x, y) != color) return 0;

        var count = 1;
        for (var step = 1; ; step++)
        {
            var nx = x + dx * step;
            var ny = y + dy * step;
            if (!board.InBounds(nx, ny) || board.Get(nx, ny) != color) break;
            count++;
        }

        for (var step = 1; ; step++)
        {
            var nx = x - dx * step;
            var ny = y - dy * step;
            if (!board.InBounds(nx, ny) || board.Get(nx, ny) != color) break;
            count++;
        }

        return count;
    }

    /// <summary>
    /// 这个方向有没有「四」：存在一个包含落子点 (x,y) 的 5 格窗口，里面正好 4 颗黑子 + 1 个空点。
    /// </summary>
    private static bool HasFourInDirection(GomokuBoard board, int x, int y, int dx, int dy)
    {
        // 5 格窗口必须包含落子点 → 窗口起点相对落子点只能取 -4 ~ 0。
        for (var start = -4; start <= 0; start++)
        {
            var black = 0;
            var empty = 0;
            var valid = true;

            for (var i = 0; i < 5; i++)
            {
                var px = x + dx * (start + i);
                var py = y + dy * (start + i);
                if (!board.InBounds(px, py))
                {
                    valid = false;
                    break;
                }

                var cell = board.Get(px, py);
                if (cell == GomokuCell.Black) black++;
                else if (cell == GomokuCell.Empty) empty++;
                else
                {
                    // 被白子挡住，这个窗口不可能成五。
                    valid = false;
                    break;
                }
            }

            if (valid && black == 4 && empty == 1) return true;
        }

        return false;
    }

    /// <summary>
    /// 这个方向有没有「活三」：存在一个空点，黑棋下在那里能形成「活四」
    /// （连续 4 黑、两端皆空），且那个活四包含本次落子点 (x,y)。
    /// </summary>
    private static bool HasOpenThreeInDirection(GomokuBoard board, int x, int y, int dx, int dy)
    {
        // 活四包含落子点 → 那个空点离落子点最多 4 格。
        for (var step = -4; step <= 4; step++)
        {
            if (step == 0) continue;

            var px = x + dx * step;
            var py = y + dy * step;
            if (!board.InBounds(px, py) || !board.IsEmpty(px, py)) continue;

            board.Set(px, py, GomokuCell.Black);
            bool formsLiveFour;
            try
            {
                formsLiveFour = FormsLiveFour(board, px, py, dx, dy, x, y);
            }
            finally
            {
                board.Set(px, py, GomokuCell.Empty);
            }

            if (formsLiveFour) return true;
        }

        return false;
    }

    /// <summary>
    /// 在「已经把 (px,py) 摆成黑子」的棋盘上，判断这个方向是不是形成活四：
    /// 连续 4 颗黑子、两端都是空点（且在棋盘内），并且这 4 颗里包含 <paramref name="centerX"/>,<paramref name="centerY"/>。
    /// </summary>
    private static bool FormsLiveFour(
        GomokuBoard board, int px, int py, int dx, int dy, int centerX, int centerY)
    {
        var run = CountRun(board, px, py, dx, dy, GomokuCell.Black);
        if (run != 4) return false;

        // 找到这 4 颗连子最靠「反方向」的那一颗作为起点。
        var startX = px;
        var startY = py;
        while (true)
        {
            var nx = startX - dx;
            var ny = startY - dy;
            if (board.InBounds(nx, ny) && board.Get(nx, ny) == GomokuCell.Black)
            {
                startX = nx;
                startY = ny;
            }
            else
            {
                break;
            }
        }

        // 两端必须都是空点（活四）。
        var beforeX = startX - dx;
        var beforeY = startY - dy;
        var afterX = startX + dx * 4;
        var afterY = startY + dy * 4;
        if (!board.InBounds(beforeX, beforeY) || !board.IsEmpty(beforeX, beforeY)) return false;
        if (!board.InBounds(afterX, afterY) || !board.IsEmpty(afterX, afterY)) return false;

        // 这个活四必须包含本次评估的落子点（否则是落子前就有的形状，不算这一手造出来的三）。
        for (var k = 0; k < 4; k++)
        {
            if (startX + dx * k == centerX && startY + dy * k == centerY) return true;
        }

        return false;
    }

    private static GomokuForbiddenInfo NotForbidden()
        => new(false, GomokuForbiddenKind.None, false, "");
}
