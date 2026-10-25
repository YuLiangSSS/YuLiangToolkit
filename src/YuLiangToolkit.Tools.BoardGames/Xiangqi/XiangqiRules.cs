namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 中国象棋走子规则（纯函数，全部不修改传入的棋盘，除非注释另说）。
///
/// <para><b>两层 API</b>：</para>
/// <list type="bullet">
///   <item><see cref="GenerateMoves"/> = <b>伪合法着法</b>：只看这枚棋子自己的走法（含马腿 / 象眼 / 炮架 / 九宫 /
///         过河 / 兵横走），<b>不检查</b>走完之后自己会不会被将军。做「这枚子能攻击哪里」时用它。</item>
///   <item><see cref="GenerateLegalMoves"/> / <see cref="IsLegalMove"/> = <b>真合法着法</b>：再额外检查
///         「不能送将」「将帅不能照面」。</item>
/// </list>
/// </summary>
public static class XiangqiRules
{
    /// <summary>将 / 帅的四个直行方向。</summary>
    private static readonly (int Dx, int Dy)[] KingSteps = { (0, -1), (0, 1), (-1, 0), (1, 0) };

    /// <summary>仕 / 士的四个斜行方向。</summary>
    private static readonly (int Dx, int Dy)[] AdvisorSteps = { (-1, -1), (-1, 1), (1, -1), (1, 1) };

    /// <summary>相 / 象的四个「田」字方向。</summary>
    private static readonly (int Dx, int Dy)[] ElephantSteps = { (-2, -2), (-2, 2), (2, -2), (2, 2) };

    /// <summary>马的八个「日」字方向。</summary>
    private static readonly (int Dx, int Dy)[] HorseSteps =
    {
        (-2, -1), (-2, 1), (2, -1), (2, 1), (-1, -2), (1, -2), (-1, 2), (1, 2),
    };

    /// <summary>车 / 炮的四个直行方向。</summary>
    private static readonly (int Dx, int Dy)[] StraightDirections = { (0, -1), (0, 1), (-1, 0), (1, 0) };

    // ---------- 基本判定 ----------

    /// <summary>该格是否在 side 这一方的九宫里（九宫 = 中路 3 列 × 本方 3 行）。</summary>
    public static bool InPalace(XiangqiSide side, int x, int y)
    {
        if (x < 3 || x > 5) return false;
        return side == XiangqiSide.Red ? y is >= 7 and <= 9 : y is >= 0 and <= 2;
    }

    /// <summary>过河没有：红方 y &lt;= 4 即过河，黑方 y &gt;= 5 即过河。</summary>
    public static bool HasCrossedRiver(XiangqiSide side, int y)
        => side == XiangqiSide.Red ? y <= 4 : y >= 5;

    /// <summary>该格是否在本方半场（相 / 象不能过河）。</summary>
    public static bool InOwnHalf(XiangqiSide side, int y)
        => side == XiangqiSide.Red ? y >= 5 : y <= 4;

    /// <summary>找将 / 帅；找不到返回 false。</summary>
    public static bool TryFindKing(XiangqiBoard board, XiangqiSide side, out int x, out int y)
    {
        for (var yy = 0; yy < XiangqiBoard.Height; yy++)
        {
            for (var xx = 0; xx < XiangqiBoard.Width; xx++)
            {
                var piece = board.Get(xx, yy);
                if (piece.Type == XiangqiPieceType.King && piece.Side == side)
                {
                    x = xx;
                    y = yy;
                    return true;
                }
            }
        }
        x = -1;
        y = -1;
        return false;
    }

    /// <summary>将帅是否照面（同一列、中间没有棋子）—— 照面方等于送将。</summary>
    public static bool AreKingsFacing(XiangqiBoard board)
    {
        if (!TryFindKing(board, XiangqiSide.Red, out var redX, out var redY)) return false;
        if (!TryFindKing(board, XiangqiSide.Black, out var blackX, out var blackY)) return false;
        if (redX != blackX) return false;
        var from = Math.Min(redY, blackY) + 1;
        var to = Math.Max(redY, blackY);
        for (var y = from; y < to; y++)
        {
            if (!board.IsEmpty(redX, y)) return false;
        }
        return true;
    }

    // ---------- 伪合法着法 ----------

    /// <summary>
    /// 生成 (x, y) 上这枚棋子的<b>伪合法着法</b>（只按该棋子的走法，不检查己方是否被将军）。
    /// 空格、越界返回空表。
    /// </summary>
    public static IReadOnlyList<XiangqiMove> GenerateMoves(XiangqiBoard board, int x, int y)
    {
        var moves = new List<XiangqiMove>(20);
        if (board is null || !XiangqiBoard.InBounds(x, y)) return moves;
        var piece = board.Get(x, y);
        if (piece.IsEmpty) return moves;

        switch (piece.Type)
        {
            case XiangqiPieceType.King:
                foreach (var (dx, dy) in KingSteps)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (InPalace(piece.Side, nx, ny) && CanLand(board, piece, nx, ny)) moves.Add(new XiangqiMove(x, y, nx, ny));
                }
                break;

            case XiangqiPieceType.Advisor:
                foreach (var (dx, dy) in AdvisorSteps)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (InPalace(piece.Side, nx, ny) && CanLand(board, piece, nx, ny)) moves.Add(new XiangqiMove(x, y, nx, ny));
                }
                break;

            case XiangqiPieceType.Elephant:
                foreach (var (dx, dy) in ElephantSteps)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (!XiangqiBoard.InBounds(nx, ny) || !InOwnHalf(piece.Side, ny)) continue;   // 相 / 象不过河
                    if (!board.IsEmpty(x + dx / 2, y + dy / 2)) continue;                          // 塞象眼
                    if (CanLand(board, piece, nx, ny)) moves.Add(new XiangqiMove(x, y, nx, ny));
                }
                break;

            case XiangqiPieceType.Horse:
                foreach (var (dx, dy) in HorseSteps)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (!XiangqiBoard.InBounds(nx, ny)) continue;
                    // 蹩马腿：横向两格时腿在 (x + dx/2, y)，纵向两格时腿在 (x, y + dy/2)
                    var legX = Math.Abs(dx) == 2 ? x + dx / 2 : x;
                    var legY = Math.Abs(dy) == 2 ? y + dy / 2 : y;
                    if (!board.IsEmpty(legX, legY)) continue;
                    if (CanLand(board, piece, nx, ny)) moves.Add(new XiangqiMove(x, y, nx, ny));
                }
                break;

            case XiangqiPieceType.Rook:
                foreach (var (dx, dy) in StraightDirections)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    while (XiangqiBoard.InBounds(nx, ny))
                    {
                        var target = board.Get(nx, ny);
                        if (target.IsEmpty)
                        {
                            moves.Add(new XiangqiMove(x, y, nx, ny));
                        }
                        else
                        {
                            if (target.Side != piece.Side) moves.Add(new XiangqiMove(x, y, nx, ny));
                            break;
                        }
                        nx += dx;
                        ny += dy;
                    }
                }
                break;

            case XiangqiPieceType.Cannon:
                foreach (var (dx, dy) in StraightDirections)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    // 第一段：没有炮架之前，只能走空格
                    while (XiangqiBoard.InBounds(nx, ny) && board.IsEmpty(nx, ny))
                    {
                        moves.Add(new XiangqiMove(x, y, nx, ny));
                        nx += dx;
                        ny += dy;
                    }
                    // 碰到炮架（第一个子）；跨过它之后，第一个子若为敌子就能吃
                    if (!XiangqiBoard.InBounds(nx, ny)) continue;
                    nx += dx;
                    ny += dy;
                    while (XiangqiBoard.InBounds(nx, ny) && board.IsEmpty(nx, ny))
                    {
                        nx += dx;
                        ny += dy;
                    }
                    if (!XiangqiBoard.InBounds(nx, ny)) continue;
                    if (board.Get(nx, ny).Side != piece.Side) moves.Add(new XiangqiMove(x, y, nx, ny));
                }
                break;

            case XiangqiPieceType.Pawn:
            {
                var forward = piece.Side == XiangqiSide.Red ? -1 : 1;
                if (CanLand(board, piece, x, y + forward)) moves.Add(new XiangqiMove(x, y, x, y + forward));
                if (HasCrossedRiver(piece.Side, y))
                {
                    if (CanLand(board, piece, x - 1, y)) moves.Add(new XiangqiMove(x, y, x - 1, y));
                    if (CanLand(board, piece, x + 1, y)) moves.Add(new XiangqiMove(x, y, x + 1, y));
                }
                break;
            }
        }

        return moves;
    }

    /// <summary>终点能不能落子：在棋盘内、且不是己方棋子。</summary>
    private static bool CanLand(XiangqiBoard board, XiangqiPiece piece, int x, int y)
    {
        if (!XiangqiBoard.InBounds(x, y)) return false;
        var target = board.Get(x, y);
        return target.IsEmpty || target.Side != piece.Side;
    }

    // ---------- 真合法着法（含送将 / 照面检查） ----------

    /// <summary>
    /// (x, y) 上这枚棋子对 <paramref name="side"/> 而言的<b>全部合法着法</b>
    /// （= 伪合法着法再过滤掉「走完自己被将军 / 将帅照面」的）。
    /// </summary>
    public static IReadOnlyList<XiangqiMove> GenerateLegalMoves(XiangqiBoard board, XiangqiSide side, int x, int y)
    {
        var piece = board.Get(x, y);
        if (piece.IsEmpty || piece.Side != side) return Array.Empty<XiangqiMove>();
        var pseudo = GenerateMoves(board, x, y);
        if (pseudo.Count == 0) return pseudo;
        var legal = new List<XiangqiMove>(pseudo.Count);
        foreach (var move in pseudo)
        {
            if (!WouldLeaveKingInCheck(board, side, move)) legal.Add(move);
        }
        return legal;
    }

    /// <summary>这步棋对 side 来说是否合法（真合法：走法 + 不送将 / 不照面）。</summary>
    public static bool IsLegalMove(XiangqiBoard board, XiangqiSide side, XiangqiMove move)
    {
        if (board is null || move.IsNone) return false;
        var piece = board.Get(move.FromX, move.FromY);
        if (piece.IsEmpty || piece.Side != side) return false;
        var target = board.Get(move.ToX, move.ToY);
        if (!target.IsEmpty && target.Side == side) return false;
        if (!Contains(GenerateMoves(board, move.FromX, move.FromY), move)) return false;
        return !WouldLeaveKingInCheck(board, side, move);
    }

    /// <summary>
    /// 试走一步，看走完之后 side 是否被将军（含将帅照面）。棋盘会原样还原。
    /// </summary>
    public static bool WouldLeaveKingInCheck(XiangqiBoard board, XiangqiSide side, XiangqiMove move)
    {
        var from = board.Get(move.FromX, move.FromY);
        var to = board.Get(move.ToX, move.ToY);
        board.Set(move.ToX, move.ToY, from);
        board.Set(move.FromX, move.FromY, XiangqiPiece.Empty);
        var inCheck = IsInCheck(board, side);
        board.Set(move.FromX, move.FromY, from);
        board.Set(move.ToX, move.ToY, to);
        return inCheck;
    }

    /// <summary>side 这一方现在是否被将军（被吃将 / 将帅照面都算）。</summary>
    public static bool IsInCheck(XiangqiBoard board, XiangqiSide side)
    {
        if (!TryFindKing(board, side, out var x, out var y)) return false;
        return IsAttacked(board, side.Opposite(), x, y);
    }

    /// <summary>
    /// (x, y) 是否被 <paramref name="bySide"/> 一方攻击。
    /// 与走子规则一致：炮隔子打、马看马腿、兵看方向；若目标是将/帅，再额外算「将帅照面」。
    /// </summary>
    public static bool IsAttacked(XiangqiBoard board, XiangqiSide bySide, int x, int y)
    {
        for (var sy = 0; sy < XiangqiBoard.Height; sy++)
        {
            for (var sx = 0; sx < XiangqiBoard.Width; sx++)
            {
                var piece = board.Get(sx, sy);
                if (piece.IsEmpty || piece.Side != bySide) continue;
                foreach (var move in GenerateMoves(board, sx, sy))
                {
                    if (move.ToX == x && move.ToY == y) return true;
                }
            }
        }

        // 飞将：将帅同列且中间无子时，将帅本身就是「互相攻击」的关系
        var target = board.Get(x, y);
        if (target.Type == XiangqiPieceType.King)
        {
            if (TryFindKing(board, bySide, out var kingX, out var kingY) && kingX == x)
            {
                var from = Math.Min(kingY, y) + 1;
                var to = Math.Max(kingY, y);
                var clear = true;
                for (var yy = from; yy < to; yy++)
                {
                    if (!board.IsEmpty(kingX, yy))
                    {
                        clear = false;
                        break;
                    }
                }
                if (clear) return true;
            }
        }

        return false;
    }

    /// <summary>side 这一方是否还有任何合法着法（将死 / 困毙判定用）。</summary>
    public static bool HasAnyLegalMove(XiangqiBoard board, XiangqiSide side)
    {
        for (var y = 0; y < XiangqiBoard.Height; y++)
        {
            for (var x = 0; x < XiangqiBoard.Width; x++)
            {
                var piece = board.Get(x, y);
                if (piece.IsEmpty || piece.Side != side) continue;
                if (GenerateLegalMoves(board, side, x, y).Count > 0) return true;
            }
        }
        return false;
    }

    /// <summary>side 是否被将死：正被将军、且没有任何合法着法。</summary>
    public static bool IsCheckmate(XiangqiBoard board, XiangqiSide side)
        => IsInCheck(board, side) && !HasAnyLegalMove(board, side);

    /// <summary>side 是否被困毙（无棋可走但没被将军）—— 中国象棋里困毙同样判负。</summary>
    public static bool IsStalemate(XiangqiBoard board, XiangqiSide side)
        => !IsInCheck(board, side) && !HasAnyLegalMove(board, side);

    /// <summary>该方的将 / 帅现在是不是处于被将军状态（找不到将返回 false）。</summary>
    public static bool IsKingChecked(XiangqiBoard board, XiangqiSide side) => IsInCheck(board, side);

    /// <summary>判断着法表里有没有这一步（<see cref="XiangqiMove"/> 是值类型，直接比 4 个坐标）。</summary>
    private static bool Contains(IReadOnlyList<XiangqiMove> moves, XiangqiMove move)
    {
        for (var i = 0; i < moves.Count; i++)
        {
            if (moves[i].Equals(move)) return true;
        }
        return false;
    }
}
