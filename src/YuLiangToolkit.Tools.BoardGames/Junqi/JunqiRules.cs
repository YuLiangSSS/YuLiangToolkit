namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>
/// 军棋走法生成与裁判（<b>纯逻辑</b>，自检可直接断言）。
///
/// <para><b>行棋规则</b>：
/// <list type="bullet">
///   <item>公路（普通相邻）：上下左右一格；</item>
///   <item>铁路：沿直线可走任意格（中间不能有子挡着）；<b>工兵可以拐弯</b>走整条铁路网；</item>
///   <item>行营：进去的棋子不能被吃；</item>
///   <item>大本营：进去的棋子不能再动。</item>
/// </list></para>
///
/// <para><b>裁判规则</b>：
/// <list type="bullet">
///   <item>大吃小；同级（含司令撞司令）同归于尽；</item>
///   <item>炸弹碰任何子（含司令、地雷、军旗）同归于尽；</item>
///   <item>地雷：只有工兵能排掉雷（工兵吃雷、雷消失），其它棋子碰雷同归于尽；</item>
///   <item>军旗：被任何子碰到就夺旗取胜。</item>
/// </list></para>
/// </summary>
public static class JunqiRules
{
    /// <summary>一次走法（起点 + 终点）。</summary>
    public readonly record struct Move(int FromX, int FromY, int ToX, int ToY)
    {
        /// <summary>是不是同一个格子（无效走法）。</summary>
        public bool IsNoop => FromX == ToX && FromY == ToY;
    }

    /// <summary>裁判结果。</summary>
    public enum JudgeOutcome
    {
        /// <summary>不该发生（数据有问题）。</summary>
        None = 0,

        /// <summary>进攻方赢：防守棋子被吃，进攻方占位。</summary>
        AttackerWins = 1,

        /// <summary>防守方赢：进攻棋子被吃。</summary>
        DefenderWins = 2,

        /// <summary>同归于尽：两枚都消失，格子空出来。</summary>
        BothDie = 3,

        /// <summary>夺旗：进攻方碰到军旗，直接获胜。</summary>
        CaptureFlag = 4,
    }

    /// <summary>裁判：进攻棋子 vs 防守棋子，谁活下来。</summary>
    public static JudgeOutcome Judge(JunqiRank attacker, JunqiRank defender)
    {
        if (attacker == JunqiRank.None || defender == JunqiRank.None) return JudgeOutcome.None;

        // 军旗：碰到就赢
        if (defender == JunqiRank.Flag) return JudgeOutcome.CaptureFlag;

        // 炸弹：同归于尽
        if (attacker == JunqiRank.Bomb || defender == JunqiRank.Bomb) return JudgeOutcome.BothDie;

        // 地雷：只有工兵能排雷
        if (defender == JunqiRank.Landmine)
            return attacker == JunqiRank.Engineer ? JudgeOutcome.AttackerWins : JudgeOutcome.BothDie;

        // 号数大吃小；同级同归于尽
        if (attacker == defender) return JudgeOutcome.BothDie;
        return attacker > defender ? JudgeOutcome.AttackerWins : JudgeOutcome.DefenderWins;
    }

    /// <summary>
    /// 生成某个棋子的全部合法落点。
    /// </summary>
    /// <param name="board">棋盘（长度 <see cref="JunqiBoard.CellCount"/>）。</param>
    /// <param name="fromX">起点 X。</param>
    /// <param name="fromY">起点 Y。</param>
    /// <param name="allowEnterCamp">是否允许走进空行营（已在自己行营里的棋子也能出来，这里只判目标）。</param>
    public static IReadOnlyList<(int X, int Y)> LegalMoves(
        IReadOnlyList<JunqiPiece> board, int fromX, int fromY, bool allowEnterCamp = true)
    {
        var moves = new List<(int X, int Y)>();
        if (!JunqiBoard.InBounds(fromX, fromY)) return moves;

        var piece = board[JunqiBoard.IndexOf(fromX, fromY)];
        if (piece.IsEmpty || !piece.CanMove) return moves;

        // 大本营里的棋子不能再动
        if (JunqiBoard.KindAt(fromX, fromY) == JunqiCellKind.Headquarters) return moves;

        var visited = new HashSet<int> { JunqiBoard.IndexOf(fromX, fromY) };
        var isEngineer = piece.Rank == JunqiRank.Engineer;
        var fromRailway = JunqiBoard.IsRailway(fromX, fromY);

        // ---- 公路：上下左右一格 ----
        foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
        {
            var nx = fromX + dx;
            var ny = fromY + dy;
            if (!JunqiBoard.InBounds(nx, ny)) continue;
            if (!CanLand(board, piece.Side, nx, ny, allowEnterCamp)) continue;
            if (visited.Add(JunqiBoard.IndexOf(nx, ny))) moves.Add((nx, ny));
        }

        // ---- 铁路：直线连续移动（工兵拐弯）----
        if (fromRailway)
        {
            if (isEngineer) CollectEngineerRailway(board, piece.Side, fromX, fromY, visited, moves, allowEnterCamp);
            else CollectStraightRailway(board, piece.Side, fromX, fromY, visited, moves, allowEnterCamp);
        }

        return moves;
    }

    /// <summary>能不能落在这一格：不能是自己的子；空行营可以进（可配置）；别人的行营里的子不能吃。</summary>
    private static bool CanLand(IReadOnlyList<JunqiPiece> board, JunqiSide side, int x, int y, bool allowEnterCamp)
    {
        if (!JunqiBoard.InBounds(x, y)) return false;
        var target = board[JunqiBoard.IndexOf(x, y)];

        if (!target.IsEmpty)
        {
            if (target.Side == side) return false;                      // 自己的子
            if (JunqiBoard.KindAt(x, y) == JunqiCellKind.Camp) return false;   // 行营里的子不能被吃
            return true;
        }

        if (JunqiBoard.KindAt(x, y) == JunqiCellKind.Camp && !allowEnterCamp) return false;
        return true;
    }

    /// <summary>非工兵：沿铁路走直线，遇子停。</summary>
    private static void CollectStraightRailway(
        IReadOnlyList<JunqiPiece> board, JunqiSide side, int fromX, int fromY,
        HashSet<int> visited, List<(int X, int Y)> moves, bool allowEnterCamp)
    {
        foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
        {
            var x = fromX;
            var y = fromY;
            while (true)
            {
                x += dx;
                y += dy;
                if (!JunqiBoard.InBounds(x, y)) break;
                if (!JunqiBoard.IsRailway(x, y)) break;      // 走出铁路了（行营 / 兵站断开线路）

                var target = board[JunqiBoard.IndexOf(x, y)];
                if (!target.IsEmpty)
                {
                    if (CanLand(board, side, x, y, allowEnterCamp) && visited.Add(JunqiBoard.IndexOf(x, y)))
                        moves.Add((x, y));
                    break;                                        // 碰到子就停
                }

                if (visited.Add(JunqiBoard.IndexOf(x, y))) moves.Add((x, y));
            }
        }
    }

    /// <summary>工兵：在整个铁路网上 BFS（可以拐弯），遇子停。</summary>
    private static void CollectEngineerRailway(
        IReadOnlyList<JunqiPiece> board, JunqiSide side, int fromX, int fromY,
        HashSet<int> visited, List<(int X, int Y)> moves, bool allowEnterCamp)
    {
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((fromX, fromY));

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
            {
                var nx = x + dx;
                var ny = y + dy;
                if (!JunqiBoard.InBounds(nx, ny)) continue;
                if (!JunqiBoard.IsRailway(nx, ny)) continue;

                var index = JunqiBoard.IndexOf(nx, ny);
                var target = board[index];

                if (!target.IsEmpty)
                {
                    // 碰到子：能吃就记下来，然后这条支路不再延伸
                    if (CanLand(board, side, nx, ny, allowEnterCamp) && visited.Add(index)) moves.Add((nx, ny));
                    continue;
                }

                if (!visited.Add(index)) continue;
                moves.Add((nx, ny));
                queue.Enqueue((nx, ny));
            }
        }
    }

    /// <summary>
    /// 棋盘上还有没有军旗（某一方的）。
    /// </summary>
    public static bool HasFlag(IReadOnlyList<JunqiPiece> board, JunqiSide side)
    {
        foreach (var piece in board)
        {
            if (!piece.IsEmpty && piece.Side == side && piece.Rank == JunqiRank.Flag) return true;
        }
        return false;
    }

    /// <summary>某一方还剩多少个能动的子（不含地雷 / 军旗）。</summary>
    public static int MovableCount(IReadOnlyList<JunqiPiece> board, JunqiSide side)
    {
        var count = 0;
        foreach (var piece in board)
        {
            if (!piece.IsEmpty && piece.Side == side && piece.CanMove) count++;
        }
        return count;
    }

    /// <summary>
    /// 这一方是不是已经没得走了（判负条件之一：子全灭 / 只剩地雷军旗）。
    /// </summary>
    public static bool HasNoMove(IReadOnlyList<JunqiPiece> board, JunqiSide side)
    {
        if (MovableCount(board, side) == 0) return true;

        for (var y = 0; y < JunqiBoard.Height; y++)
        {
            for (var x = 0; x < JunqiBoard.Width; x++)
            {
                var piece = board[JunqiBoard.IndexOf(x, y)];
                if (piece.IsEmpty || piece.Side != side || !piece.CanMove) continue;
                if (LegalMoves(board, x, y).Count > 0) return false;
            }
        }
        return true;
    }
}
