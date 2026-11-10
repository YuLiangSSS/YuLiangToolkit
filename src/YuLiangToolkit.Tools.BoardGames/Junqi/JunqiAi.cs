using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>
/// 军棋电脑玩家（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>为什么不做深度搜索</b>：暗棋是「不完全信息」游戏，真正的搜索要维护信念状态（哪些暗子可能是谁），
/// 收益低、复杂度高。这里用「启发式评估 + 一步前瞻」：</para>
/// <list type="bullet">
///   <item><b>已知信息</b>：自己在暗子旁边时，按「最坏情况」评估（当作对方是能赢自己的子）；
///         对方已经被吃掉的棋子不参与；军旗位置已知（大本营）。</item>
///   <item><b>评估</b>：子力分 + 前进分 + 逼旗分 - 送子风险。</item>
///   <item><b>难度</b>：入门只在候选里随机挑（乱走），困难 / 大师会考虑一步前瞻（对手最优应对）。</item>
/// </list>
/// </summary>
public static class JunqiAi
{
    /// <summary>子力分：司令最贵，工兵因为能排雷给得高一点。</summary>
    private static int RankValue(JunqiRank rank) => rank switch
    {
        JunqiRank.Commander => 100,
        JunqiRank.CorpsCommander => 80,
        JunqiRank.DivisionCommander => 60,
        JunqiRank.BrigadeCommander => 45,
        JunqiRank.RegimentalCommander => 34,
        JunqiRank.BattalionCommander => 25,
        JunqiRank.CompanyCommander => 18,
        JunqiRank.PlatoonLeader => 12,
        JunqiRank.Engineer => 14,       // 能排雷 / 能拐弯，别当炮灰
        JunqiRank.Bomb => 30,
        JunqiRank.Landmine => 18,
        JunqiRank.Flag => 200,
        _ => 0,
    };

    /// <summary>给 <paramref name="side"/> 选一步棋。</summary>
    /// <param name="game">棋局。</param>
    /// <param name="side">要替哪一方走。</param>
    /// <param name="difficulty">难度。</param>
    /// <param name="random">随机源。</param>
    public static JunqiRules.Move? ChooseMove(JunqiGame game, JunqiSide side, AiDifficulty difficulty, Random? random = null)
    {
        if (game is null || game.Phase != JunqiPhase.Playing) return null;

        var rng = random ?? Random.Shared;
        var moves = AllMoves(game, side);
        if (moves.Count == 0) return null;
        if (moves.Count == 1) return moves[0];

        // 入门：经常乱走
        if (rng.NextDouble() < AiDifficulties.BlunderChance(difficulty) * 1.4)
            return moves[rng.Next(moves.Count)];

        var limit = Math.Min(AiDifficulties.CandidateLimit(difficulty) * 4, moves.Count);
        var scored = new List<(JunqiRules.Move Move, int Score)>(moves.Count);

        foreach (var move in moves)
        {
            var score = ScoreMove(game, side, move, difficulty);
            scored.Add((move, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 最高分并列时随机挑一个，避免每局套路一样
        var best = scored[0].Score;
        var ties = scored.Take(limit).Where(s => s.Score == best).ToList();
        return ties.Count > 0 ? ties[rng.Next(ties.Count)].Move : scored[0].Move;
    }

    /// <summary>收集某一方的全部合法走法。</summary>
    public static List<JunqiRules.Move> AllMoves(JunqiGame game, JunqiSide side)
    {
        var moves = new List<JunqiRules.Move>();
        for (var y = 0; y < JunqiBoard.Height; y++)
        {
            for (var x = 0; x < JunqiBoard.Width; x++)
            {
                var piece = game.At(x, y);
                if (piece.IsEmpty || piece.Side != side || !piece.CanMove) continue;

                foreach (var (tx, ty) in game.LegalMoves(x, y))
                    moves.Add(new JunqiRules.Move(x, y, tx, ty));
            }
        }
        return moves;
    }

    /// <summary>给一步棋打分（越大越好）。</summary>
    private static int ScoreMove(JunqiGame game, JunqiSide side, JunqiRules.Move move, AiDifficulty difficulty)
    {
        var from = game.At(move.FromX, move.FromY);
        var target = game.At(move.ToX, move.ToY);
        var score = 0;

        // ① 吃子：已知的敌人按牌面赚；暗子按「最坏情况」保守估计
        if (!target.IsEmpty)
        {
            if (!target.IsUnknown)
            {
                var judge = JunqiRules.Judge(from.Rank, target.Rank);
                score += judge switch
                {
                    JunqiRules.JudgeOutcome.CaptureFlag => 100_000,
                    JunqiRules.JudgeOutcome.AttackerWins => RankValue(target.Rank),
                    JunqiRules.JudgeOutcome.DefenderWins => -RankValue(from.Rank),
                    JunqiRules.JudgeOutcome.BothDie => RankValue(target.Rank) - RankValue(from.Rank),
                    _ => 0,
                };
            }
            else
            {
                // 暗子：乐观一半、悲观一半（谁也不知道）
                score += 2;
            }
        }

        // ② 逼旗：离对方大本营越近越好（工兵 / 炸弹冲旗）
        var enemySide = side == JunqiSide.Red ? JunqiSide.Blue : JunqiSide.Red;
        var hq = JunqiBoard.HeadquartersOf(enemySide);
        var currentDistance = hq.Min(p => Math.Abs(p.X - move.ToX) + Math.Abs(p.Y - move.ToY));
        var beforeDistance = hq.Min(p => Math.Abs(p.X - move.FromX) + Math.Abs(p.Y - move.FromY));
        if (currentDistance < beforeDistance) score += 6;

        // ③ 走出行营 / 大本营要谨慎（行营是安全格，别把子送出去）
        if (JunqiBoard.KindAt(move.FromX, move.FromY) == JunqiCellKind.Camp
            && JunqiBoard.KindAt(move.ToX, move.ToY) != JunqiCellKind.Camp)
            score -= 3;

        // ④ 别让司令 / 军长往敌人堆里冲（周围有暗子就扣分）
        if (from.Rank >= JunqiRank.DivisionCommander && HasAdjacentEnemy(game, move.ToX, move.ToY, enemySide))
            score -= RankValue(from.Rank) / 4;

        // ⑤ 工兵优先去排雷 / 靠近铁路
        if (from.Rank == JunqiRank.Engineer && JunqiBoard.IsRailway(move.ToX, move.ToY)) score += 4;

        // ⑥ 困难以上：一步前瞻（看对手最凶的一手能赚多少）
        if (difficulty is AiDifficulty.Hard or AiDifficulty.Master)
            score -= LookAheadRisk(game, side, move) / 2;

        return score;
    }

    /// <summary>落点周围有没有敌人（含暗子）。</summary>
    private static bool HasAdjacentEnemy(JunqiGame game, int x, int y, JunqiSide enemy)
    {
        foreach (var (dx, dy) in new[] { (0, -1), (0, 1), (-1, 0), (1, 0) })
        {
            var piece = game.At(x + dx, y + dy);
            if (!piece.IsEmpty && piece.Side == enemy) return true;
        }
        return false;
    }

    /// <summary>一步前瞻：假设我走这一步，对手最赚的一手能赚多少（返回正数表示我吃亏）。</summary>
    private static int LookAheadRisk(JunqiGame game, JunqiSide side, JunqiRules.Move move)
    {
        // 只在「目标格有敌人」时算：走到空位通常不会立刻被吃（真被吃也不亏子力）
        var target = game.At(move.ToX, move.ToY);
        if (target.IsEmpty) return 0;

        var enemy = side == JunqiSide.Red ? JunqiSide.Blue : JunqiSide.Red;
        var enemyMoves = AllMoves(game, enemy);
        var worst = 0;

        foreach (var reply in enemyMoves)
        {
            if (reply.ToX != move.ToX || reply.ToY != move.ToY) continue;
            var attacker = game.At(reply.FromX, reply.FromY);
            if (attacker.IsUnknown) { worst = Math.Max(worst, 6); continue; }
            worst = Math.Max(worst, RankValue(attacker.Rank) / 3);
        }
        return worst;
    }
}
