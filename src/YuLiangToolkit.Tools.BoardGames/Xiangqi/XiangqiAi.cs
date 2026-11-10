using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>
/// 中国象棋 AI（<b>纯逻辑</b>，不碰界面 / 网络，自检可直接跑）。
///
/// <para><b>算法</b>：极小化极大 + alpha-beta 剪枝，落子排序（先试吃子 / 将军），
/// 叶子节点用「子力 + 位置分」评估。这是最经典的弈棋 AI 结构，靠深度和评估质量变强。</para>
///
/// <para><b>难度</b>：<see cref="AiDifficulty"/> 决定搜索深度（1 / 2 / 3 / 4 层）；
/// 入门会以一定概率随机挑一个合法着法（犯傻），不会算杀。</para>
/// </summary>
public static class XiangqiAi
{
    /// <summary>子力价值表（车 &gt; 马炮 &gt; 兵卒 &gt; 士象）。</summary>
    private static int PieceValue(XiangqiPieceType type) => type switch
    {
        XiangqiPieceType.King => 100_000,
        XiangqiPieceType.Rook => 900,
        XiangqiPieceType.Cannon => 450,
        XiangqiPieceType.Horse => 420,
        XiangqiPieceType.Advisor => 200,
        XiangqiPieceType.Elephant => 200,
        XiangqiPieceType.Pawn => 100,
        _ => 0,
    };

    /// <summary>给某一方选一步棋。</summary>
    /// <param name="game">棋局（会临时改动棋盘做搜索，结束后自动恢复）。</param>
    /// <param name="difficulty">难度。</param>
    /// <param name="random">随机源（入门犯傻用；固定种子可复现）。</param>
    /// <returns>选中的着法；没有合法着法返回 null。</returns>
    public static XiangqiMove? ChooseMove(XiangqiGame game, AiDifficulty difficulty, Random? random = null)
    {
        if (game is null || game.IsOver) return null;

        var rng = random ?? Random.Shared;
        var side = game.Turn;
        var moves = XiangqiRules.GenerateAllLegalMoves(game.Board, side);
        if (moves.Count == 0) return null;
        if (moves.Count == 1) return moves[0];

        // 入门：一定概率随便走一步（看起来像新手）
        if (rng.NextDouble() < AiDifficulties.BlunderChance(difficulty))
            return moves[rng.Next(moves.Count)];

        var depth = AiDifficulties.SearchDepth(difficulty);
        var ordered = OrderMoves(game.Board, moves, side);

        var bestMove = ordered[0];
        var bestScore = int.MinValue;
        var alpha = int.MinValue + 1;
        var beta = int.MaxValue;

        foreach (var move in ordered)
        {
            var undo = game.ApplyMoveForAi(move);
            var score = -Search(game, depth - 1, -beta, -alpha, Other(side));
            game.UndoMoveForAi(undo);

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = move;
            }
            if (score > alpha) alpha = score;
        }

        return bestMove;
    }

    /// <summary>Negamax + alpha-beta。</summary>
    private static int Search(XiangqiGame game, int depth, int alpha, int beta, XiangqiSide side)
    {
        if (depth <= 0 || game.IsOver) return Evaluate(game, side);

        var moves = XiangqiRules.GenerateAllLegalMoves(game.Board, side);
        if (moves.Count == 0) return -90_000 + depth;          // 被将死：越晚被杀越好

        var ordered = OrderMoves(game.Board, moves, side);
        var best = int.MinValue + 1;

        foreach (var move in ordered)
        {
            var undo = game.ApplyMoveForAi(move);
            var score = -Search(game, depth - 1, -beta, -alpha, Other(side));
            game.UndoMoveForAi(undo);

            if (score > best) best = score;
            if (best > alpha) alpha = best;
            if (alpha >= beta) break;                          // 剪枝
        }

        return best;
    }

    /// <summary>评估：站在 <paramref name="side"/> 的角度算分。</summary>
    private static int Evaluate(XiangqiGame game, XiangqiSide side)
    {
        var score = 0;
        for (var y = 0; y < XiangqiBoard.Height; y++)
        {
            for (var x = 0; x < XiangqiBoard.Width; x++)
            {
                var piece = game.Board.Get(x, y);
                if (piece.IsEmpty) continue;

                var value = PieceValue(piece.Type);
                score += piece.Side == side ? value : -value;
            }
        }
        return score;
    }

    /// <summary>落子排序：吃子的着法优先（让 alpha-beta 更早剪枝）。</summary>
    private static List<XiangqiMove> OrderMoves(XiangqiBoard board, IReadOnlyList<XiangqiMove> moves, XiangqiSide side)
    {
        var scored = new List<(XiangqiMove Move, int Score)>(moves.Count);
        foreach (var move in moves)
        {
            var target = board.Get(move.ToX, move.ToY);
            var score = target.IsEmpty ? 0 : PieceValue(target.Type);
            if (!target.IsEmpty && target.Side != side) score += 50;

            // 顺手把车 / 炮走到开阔处也算好棋（防止它把大子憋在角落）
            if (!target.IsEmpty) score += 10;
            scored.Add((move, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        return scored.Select(s => s.Move).ToList();
    }

    private static XiangqiSide Other(XiangqiSide side)
        => side == XiangqiSide.Red ? XiangqiSide.Black : XiangqiSide.Red;
}
