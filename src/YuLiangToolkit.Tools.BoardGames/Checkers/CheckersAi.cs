using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Checkers;

/// <summary>
/// 跳棋 AI（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>评估</b>：每枚棋子「离目标角有多近」+ 跳的步数奖励 + 是否已进入目标角。</para>
///
/// <para><b>难度</b>：</para>
/// <list type="bullet">
///   <item><b>入门</b>：随机挑一个合法走法（会走废棋）；</item>
///   <item><b>普通</b>：挑「目标距离减少最多」的一步；</item>
///   <item><b>困难</b>：一步前瞻 —— 走完之后看对手最好的一步会不会把我顶回来。</item>
/// </list>
/// </summary>
public static class CheckersAi
{
    /// <summary>选一步棋。</summary>
    /// <param name="game">对局。</param>
    /// <param name="playerId">要替谁走。</param>
    /// <param name="difficulty">难度。</param>
    /// <param name="random">随机源。</param>
    /// <returns>选中的走法；没有合法走法返回 null。</returns>
    public static CheckersMove? ChooseMove(
        CheckersGame game, string playerId, AiDifficulty difficulty, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var moves = game.LegalMoves(playerId);
        if (moves.Count == 0) return null;
        if (moves.Count == 1) return moves[0];

        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player is null) return moves[0];

        // 入门：随机（偶尔也挑个好点的，显得不那么傻）
        if (difficulty == AiDifficulty.Beginner || rng.NextDouble() < AiDifficulties.BlunderChance(difficulty))
        {
            var pool = moves.OrderByDescending(m => Score(game, player, m)).Take(3).ToList();
            return pool[rng.Next(pool.Count)];
        }

        var scored = moves.Select(m => (Move: m, Score: Score(game, player, m)))
            .OrderByDescending(x => x.Score)
            .ToList();

        // 困难 / 大师：一步前瞻 = 自己走这步的分 - 对手最好回应能拿的分
        if (difficulty is AiDifficulty.Hard or AiDifficulty.Master)
        {
            var top = scored.Take(difficulty == AiDifficulty.Master ? 6 : 4).ToList();
            var best = top[0].Move;
            var bestValue = double.MinValue;

            foreach (var (move, ownScore) in top)
            {
                // 试走 → 让对手挑最好的一手
                var undo = game.ApplyForSimulation(playerId, move);
                var risk = EstimateOpponentGain(game, playerId);
                game.UndoForSimulation(playerId, undo);

                // ⚠ 必须带上自己的得分：只算 -risk 会挑「自己最烂、对手也赚不到」的棋（踩过）
                var value = ownScore - risk * (difficulty == AiDifficulty.Master ? 1.0 : 0.7);
                if (value > bestValue) { bestValue = value; best = move; }
            }
            return best;
        }

        // 普通：挑分最高的（并列随机）
        var top2 = scored[0].Score;
        var ties = scored.Where(s => Math.Abs(s.Score - top2) < 0.001).Select(s => s.Move).ToList();
        return ties[rng.Next(ties.Count)];
    }

    /// <summary>给一步棋打分（越大越好）。</summary>
    private static double Score(CheckersGame game, CheckersPlayer player, CheckersMove move)
    {
        var score = 0.0;

        // ① 离目标角越近越好
        var targetCells = CheckersBoard.CellsOf(player.Target);
        if (targetCells.Count > 0)
        {
            var beforeDistance = MinDistance(targetCells, move.FromX, move.FromY);
            var afterDistance = MinDistance(targetCells, move.ToX, move.ToY);
            score += (beforeDistance - afterDistance) * 3.0;
        }

        // ② 跳得越多越好（跳是加速器）
        score += move.Jumps * 2.5;

        // ③ 终点就在目标角里，大幅加分
        if (CheckersBoard.CellAt(move.ToX, move.ToY)?.Point == player.Target) score += 12;

        // ④ 别把棋子留在原地不动（原地不动没意义）
        if (move.FromX == move.ToX && move.FromY == move.ToY) score -= 100;

        // ⑤ 已经在目标角里的棋子不要乱动
        if (CheckersBoard.CellAt(move.FromX, move.FromY)?.Point == player.Target
            && CheckersBoard.CellAt(move.ToX, move.ToY)?.Point != player.Target)
            score -= 20;

        _ = game;
        return score;
    }

    /// <summary>到一组格子的最近「步数距离」（用 BFS 太慢，这里用坐标近似）。</summary>
    private static int MinDistance(IReadOnlyList<CheckersCell> cells, int x, int y)
    {
        var best = int.MaxValue;
        foreach (var cell in cells)
        {
            var distance = Math.Abs(cell.X - x) + Math.Abs(cell.Y - y);
            if (distance < best) best = distance;
        }
        return best == int.MaxValue ? 0 : best;
    }

    /// <summary>估算对手下一步能拿到的最好分（用来做一步前瞻）。</summary>
    private static double EstimateOpponentGain(CheckersGame game, string myId)
    {
        var opponent = game.Players.FirstOrDefault(p => p.Id != myId);
        if (opponent is null) return 0;

        var moves = game.LegalMoves(opponent.Id);
        if (moves.Count == 0) return 0;

        return moves.Max(m => Score(game, opponent, m));
    }

    /// <summary>难度说明。</summary>
    public static string DifficultyName(AiDifficulty difficulty) => difficulty switch
    {
        AiDifficulty.Beginner => "入门",
        AiDifficulty.Hard => "困难",
        AiDifficulty.Master => "大师",
        _ => "普通",
    };
}
