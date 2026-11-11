using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>
/// 五子棋 AI（<b>纯逻辑</b>，不碰界面，自检可直接跑）。
///
/// <para><b>算法</b>：只考虑已有棋子周围的空点（离棋子太远的地方下棋没意义），
/// 对每个候选点分别算「自己下这里能形成什么」和「对手下这里能形成什么」，
/// 再用棋型分值综合打分（进攻分 + 防守分加权）。
/// 这是经典的「启发式 + 棋型评估」做法，对休闲玩家足够强，而且快、可解释、好调试。</para>
///
/// <para><b>难度</b>：<see cref="AiDifficulty"/> 决定候选点数量上限、
/// 是否随机挑次优解（入门会犯傻）、以及会不会补算一步「对手的反杀」。</para>
///
/// <para><b>禁手</b>：带禁手规则时，黑棋的禁手点直接排除（不能自己往禁手里送）。</para>
/// </summary>
public static class GomokuAi
{
    /// <summary>棋型分值：五连最高，其次活四 / 冲四 / 活三等。</summary>
    private const int ScoreFive = 10_000_000;
    private const int ScoreOpenFour = 1_000_000;
    private const int ScoreFour = 120_000;
    private const int ScoreOpenThree = 20_000;
    private const int ScoreThree = 1_200;
    private const int ScoreOpenTwo = 400;
    private const int ScoreTwo = 60;

    /// <summary>防守权重：对手在这个点能形成的威胁，乘这个系数后从「我下这里」的分里扣掉。</summary>
    private const double DefenseWeight = 0.95;

    /// <summary>
    /// 选一个落点。<paramref name="game"/> 的当前轮到方就是 AI 要替的那一方。
    /// </summary>
    /// <param name="game">棋局。</param>
    /// <param name="difficulty">难度。</param>
    /// <param name="random">随机源（入门犯傻 / 平局打破用；传固定种子可复现）。</param>
    /// <returns>落点；没有合法点（棋盘满了）返回 null。</returns>
    public static GomokuPoint? ChooseMove(GomokuGame game, AiDifficulty difficulty, Random? random = null)
    {
        if (game is null || game.IsOver) return null;

        var rng = random ?? Random.Shared;
        var me = game.Turn;
        var opponent = me == GomokuCell.Black ? GomokuCell.White : GomokuCell.Black;
        var board = game.Board;

        // 空棋盘：直接下天元
        if (board.Moves.Count == 0 && board.Get(game.Size / 2, game.Size / 2) == GomokuCell.Empty)
            return new GomokuPoint(game.Size / 2, game.Size / 2);

        var candidates = CollectCandidates(game, me);
        if (candidates.Count == 0) return null;

        var scored = new List<(GomokuPoint Point, double Score)>(candidates.Count);
        foreach (var point in candidates)
        {
            if (IsForbidden(game, me, point)) continue;

            var attack = EvaluateAt(board, point.X, point.Y, me, game.Size);
            var defense = EvaluateAt(board, point.X, point.Y, opponent, game.Size);
            scored.Add((point, attack + defense * DefenseWeight));
        }

        if (scored.Count == 0) return null;
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 难度：入门只在前面几个里随机挑（会错过最优解 → 显得「菜」）
        var limit = Math.Min(AiDifficulties.CandidateLimit(difficulty), scored.Count);
        if (limit <= 0) limit = 1;

        // 「犯傻」概率：在候选里随机挑一个（不一定是最好）
        if (rng.NextDouble() < AiDifficulties.BlunderChance(difficulty))
        {
            var pool = scored.Take(Math.Max(limit, 2)).ToList();
            return pool[rng.Next(pool.Count)].Point;
        }

        // 最高分有多个时随机挑一个，免得每局都一模一样
        var best = scored[0].Score;
        var ties = scored.Take(limit).Where(s => Math.Abs(s.Score - best) < 0.001).ToList();
        return ties.Count > 0 ? ties[rng.Next(ties.Count)].Point : scored[0].Point;
    }

    /// <summary>收集候选点：已有棋子周围 2 格以内的空点。</summary>
    private static List<GomokuPoint> CollectCandidates(GomokuGame game, GomokuCell me)
    {
        _ = me;
        var board = game.Board;
        var size = game.Size;
        var seen = new HashSet<int>();
        var list = new List<GomokuPoint>();

        foreach (var move in board.Moves)
        {
            for (var dy = -2; dy <= 2; dy++)
            {
                for (var dx = -2; dx <= 2; dx++)
                {
                    var x = move.X + dx;
                    var y = move.Y + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    if (board.Get(x, y) != GomokuCell.Empty) continue;
                    if (!seen.Add(y * size + x)) continue;
                    list.Add(new GomokuPoint(x, y));
                }
            }
        }

        // 棋盘上还没有棋：中心点
        if (list.Count == 0 && board.Get(size / 2, size / 2) == GomokuCell.Empty)
            list.Add(new GomokuPoint(size / 2, size / 2));

        return list;
    }

    /// <summary>带禁手规则时：黑棋下这个点会不会是禁手（三三 / 四四 / 长连）。</summary>
    private static bool IsForbidden(GomokuGame game, GomokuCell player, GomokuPoint point)
    {
        if (!game.ForbiddenEnabled || player != GomokuCell.Black) return false;
        return GomokuForbidden.Evaluate(game.Board, point.X, point.Y, game.ForbiddenEnabled).IsForbidden;
    }

    /// <summary>
    /// 假设 <paramref name="player"/> 落在 (x,y)，评估四个方向上的棋型总分。
    /// </summary>
    private static int EvaluateAt(GomokuBoard board, int x, int y, GomokuCell player, int size)
    {
        var total = 0;
        foreach (var (dx, dy) in Directions)
        {
            var count = 1;            // 自己这一枚
            var openEnds = 0;
            var blockedEnds = 0;

            // 正方向
            var step = 0;
            while (step < 4)
            {
                step++;
                var nx = x + dx * step;
                var ny = y + dy * step;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) { blockedEnds++; break; }
                var cell = board.Get(nx, ny);
                if (cell == player) { count++; continue; }
                if (cell == GomokuCell.Empty) { openEnds++; }
                else { blockedEnds++; }
                break;
            }

            // 反方向
            step = 0;
            while (step < 4)
            {
                step++;
                var nx = x - dx * step;
                var ny = y - dy * step;
                if (nx < 0 || ny < 0 || nx >= size || ny >= size) { blockedEnds++; break; }
                var cell = board.Get(nx, ny);
                if (cell == player) { count++; continue; }
                if (cell == GomokuCell.Empty) { openEnds++; }
                else { blockedEnds++; }
                break;
            }

            total += ScoreShape(count, openEnds);
        }
        return total;
    }

    /// <summary>连续子数 + 两端开放情况 → 棋型分值。</summary>
    private static int ScoreShape(int count, int openEnds)
    {
        if (count >= 5) return ScoreFive;
        if (openEnds == 0) return 0;               // 两头都被堵死，没价值

        return count switch
        {
            4 => openEnds >= 2 ? ScoreOpenFour : ScoreFour,
            3 => openEnds >= 2 ? ScoreOpenThree : ScoreThree,
            2 => openEnds >= 2 ? ScoreOpenTwo : ScoreTwo,
            1 => openEnds >= 2 ? 12 : 6,
            _ => 0,
        };
    }

    private static readonly (int Dx, int Dy)[] Directions =
    {
        (1, 0), (0, 1), (1, 1), (1, -1),
    };
}
