using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Go;

/// <summary>
/// 围棋 AI（<b>纯逻辑</b>，不碰界面 / 网络，自检可直接跑）。
///
/// <para><b>算法</b>：围棋的分支因子太大，纯 α-β 搜索到不了像样的棋力。
/// 这里用「启发式打分 + 随机对手模拟」的组合，对休闲对局足够：</para>
/// <list type="bullet">
///   <item><b>打吃 / 提子</b>：能提对方子的点优先；自己被叫吃时优先逃；</item>
///   <item><b>连接 / 分断</b>：能连上自己棋块 / 切断对方的点加分；</item>
///   <item><b>靠近已有棋子</b>：布局阶段不下在天涯海角（远离现有棋子的点降权）；</item>
///   <item><b>角与边</b>：开局阶段角、边优先（金角银边草肚皮）；</item>
///   <item><b>模拟验证</b>（困难 / 大师）：对几个候选点各做若干次「随机走到底」估胜率，
///         取胜率最高的；入门 / 普通只用启发式打分。</item>
/// </list>
///
/// <para><b>难度</b>：入门只在候选里随机挑（会下出明显坏棋）、普通用纯启发式、
/// 困难 / 大师加模拟验证（大师模拟次数更多）。</para>
/// </summary>
public static class GoAi
{
    /// <summary>
    /// 选一手棋。<paramref name="player"/> 必须等于当前回合方。
    /// </summary>
    /// <returns>落点；null 表示建议停一手（pass）。</returns>
    public static GoPoint? ChooseMove(GoGame game, GoCell player, AiDifficulty difficulty, Random? random = null)
    {
        if (game is null || game.IsOver || game.Turn != player) return null;

        var rng = random ?? Random.Shared;
        var size = game.Size;

        // 候选点：空点，且不是打劫禁着点
        var candidates = new List<GoPoint>(size * size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (game.Get(x, y) != GoCell.Empty) continue;
                if (game.IsKoPoint(x, y)) continue;
                candidates.Add(new GoPoint(x, y));
            }
        }

        if (candidates.Count == 0) return null;

        // 开局第一手：天元 / 星位（更像人下的棋）
        if (game.DisplayMoveCount == 0)
        {
            var star = size >= 19 ? 3 : size >= 13 ? 2 : 2;
            return new GoPoint(star + 1, star + 1);
        }

        var opponent = GoGame.Other(player);

        // ---- 启发式打分 ----
        var scored = new List<(GoPoint Point, double Score)>(candidates.Count);
        foreach (var point in candidates)
        {
            var result = game.Play(player, point.X, point.Y);
            if (result != GoPlayResult.Ok) continue;

            var score = EvaluateAfterMove(game, player, opponent, point, rng);
            scored.Add((point, score));

            game.Undo();
        }

        if (scored.Count == 0) return null;

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // 入门：在候选里随机挑（会下出坏棋）
        if (rng.NextDouble() < AiDifficulties.BlunderChance(difficulty))
        {
            var pool = scored.Take(Math.Min(8, scored.Count)).ToList();
            return pool[rng.Next(pool.Count)].Point;
        }

        // 困难 / 大师：对前几个候选做随机模拟，取胜率最高的
        if (difficulty is AiDifficulty.Hard or AiDifficulty.Master)
        {
            var topN = Math.Min(difficulty == AiDifficulty.Master ? 5 : 3, scored.Count);
            var sims = difficulty == AiDifficulty.Master ? 24 : 10;

            var best = scored[0].Point;
            var bestWinRate = -1.0;

            for (var i = 0; i < topN; i++)
            {
                var point = scored[i].Point;
                var winRate = SimulateWinRate(game, player, point, sims, rng);
                if (winRate > bestWinRate)
                {
                    bestWinRate = winRate;
                    best = point;
                }
            }
            return best;
        }

        // 最高分有多个时随机挑
        var top = scored[0].Score;
        var ties = scored.Where(s => Math.Abs(s.Score - top) < 0.001).ToList();
        return ties.Count > 0 ? ties[rng.Next(ties.Count)].Point : scored[0].Point;
    }

    /// <summary>
    /// 下完这一手之后的启发式评分（局面越好分越高，站在 <paramref name="player"/> 角度）。
    /// </summary>
    private static double EvaluateAfterMove(GoGame game, GoCell player, GoCell opponent, GoPoint point, Random rng)
    {
        _ = rng;
        var score = 0.0;
        var size = game.Size;

        // ① 提子：这一手提掉对方多少子（从棋谱最后一手看）
        if (game.LastMove is { } last && !last.IsPass)
        {
            score += last.Captured.Count * 14;

            // 提子后自己的棋块气多，加分
            score += CountLiberties(game, point.X, point.Y) * 1.2;

            // ② 打吃：让对手的棋块只剩 1 口气
            score += CountAtariAgainst(game, player, point) * 6;
        }

        // ③ 连接 / 分断：这一手旁边有多少自己的棋子 / 对方棋子
        var ownNeighbors = 0;
        var enemyNeighbors = 0;
        foreach (var (nx, ny) in Neighbors(size, point.X, point.Y))
        {
            var cell = game.Get(nx, ny);
            if (cell == player) ownNeighbors++;
            else if (cell == opponent) enemyNeighbors++;
        }
        score += ownNeighbors * 1.0 + enemyNeighbors * 0.6;

        // ④ 角与边（开局阶段）：靠近角和边的点优先
        var edgeDistance = Math.Min(
            Math.Min(point.X, size - 1 - point.X),
            Math.Min(point.Y, size - 1 - point.Y));
        if (game.DisplayMoveCount < size * 2)
            score += (3 - Math.Min(edgeDistance, 3)) * 3.0;

        // ⑤ 远离已有棋子要降权（别下在天涯海角）
        var nearest = NearestStoneDistance(game, point);
        if (nearest > 4) score -= (nearest - 4) * 2.5;

        // ⑥ 别把自己的棋子送到虎口（下完只剩 1 口气、又提不到对方子）
        var myLiberties = CountLiberties(game, point.X, point.Y);
        if (myLiberties == 1 && (game.LastMove?.Captured.Count ?? 0) == 0) score -= 22;
        else if (myLiberties == 2) score -= 3;

        // ⑦ 下在对方棋子密集处要慎重
        score -= CountGroupAtariAgainst(game, opponent) * 2;

        return score;
    }

    /// <summary>这一手让多少个对方棋块变成「叫吃」状态。</summary>
    private static int CountAtariAgainst(GoGame game, GoCell player, GoPoint point)
    {
        _ = player;
        var size = game.Size;
        var opponent = game.Turn;      // 这一手之后轮到对方
        var checkedGroups = new HashSet<int>();
        var count = 0;

        foreach (var (nx, ny) in Neighbors(size, point.X, point.Y))
        {
            if (game.Get(nx, ny) != opponent) continue;

            var index = ny * size + nx;
            if (checkedGroups.Contains(index)) continue;

            var liberties = CountLiberties(game, nx, ny);
            if (liberties == 1) count++;

            // 标记这一块，避免重复统计
            foreach (var (gx, gy) in GroupPoints(game, nx, ny)) checkedGroups.Add(gy * size + gx);
        }
        return count;
    }

    /// <summary>自己有多少棋块正处于叫吃（用来避免越走越差）。</summary>
    private static int CountGroupAtariAgainst(GoGame game, GoCell side)
    {
        var size = game.Size;
        var visited = new HashSet<int>();
        var atari = 0;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (game.Get(x, y) != side) continue;
                var index = y * size + x;
                if (visited.Contains(index)) continue;

                if (CountLiberties(game, x, y) == 1) atari++;
                foreach (var (gx, gy) in GroupPoints(game, x, y)) visited.Add(gy * size + gx);
            }
        }
        return atari;
    }

    /// <summary>某个棋块占的所有点。</summary>
    private static IEnumerable<(int X, int Y)> GroupPoints(GoGame game, int x, int y)
    {
        var color = game.Get(x, y);
        if (color == GoCell.Empty) yield break;

        var size = game.Size;
        var visited = new HashSet<int> { y * size + x };
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));

        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            yield return (cx, cy);

            foreach (var (nx, ny) in Neighbors(size, cx, cy))
            {
                if (game.Get(nx, ny) != color) continue;
                if (!visited.Add(ny * size + nx)) continue;
                stack.Push((nx, ny));
            }
        }
    }

    /// <summary>数某个棋块的气。</summary>
    private static int CountLiberties(GoGame game, int x, int y)
    {
        var color = game.Get(x, y);
        if (color == GoCell.Empty) return 0;

        var size = game.Size;
        var visited = new HashSet<int> { y * size + x };
        var liberties = new HashSet<int>();
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));

        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            foreach (var (nx, ny) in Neighbors(size, cx, cy))
            {
                var cell = game.Get(nx, ny);
                var index = ny * size + nx;
                if (cell == GoCell.Empty) { liberties.Add(index); continue; }
                if (cell != color || !visited.Add(index)) continue;
                stack.Push((nx, ny));
            }
        }
        return liberties.Count;
    }

    /// <summary>离最近一颗棋子的曼哈顿距离（棋盘为空时返回大数）。</summary>
    private static int NearestStoneDistance(GoGame game, GoPoint point)
    {
        var size = game.Size;
        var best = int.MaxValue;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (game.Get(x, y) == GoCell.Empty) continue;
                var distance = Math.Abs(x - point.X) + Math.Abs(y - point.Y);
                if (distance < best) best = distance;
            }
        }
        return best == int.MaxValue ? 99 : best;
    }

    /// <summary>
    /// 随机模拟：落这一手之后，双方都随机走到底（最多若干手），看 <paramref name="player"/> 胜率。
    /// </summary>
    private static double SimulateWinRate(GoGame game, GoCell player, GoPoint point, int simulations, Random rng)
    {
        if (game.Play(player, point.X, point.Y) != GoPlayResult.Ok) return -1;

        var snapshot = game.ToSnapshot();
        var wins = 0;

        for (var i = 0; i < simulations; i++)
        {
            var (sim, error) = GoGame.FromSnapshot(snapshot);
            if (sim is null) { _ = error; continue; }

            SimulateToEnd(sim, player, rng);

            if (sim.IsOver && sim.Winner == player) wins++;
        }

        game.Undo();
        return simulations == 0 ? 0 : (double)wins / simulations;
    }

    /// <summary>随机走到底（最多走 2×棋盘格数 手，够估出个大概）。</summary>
    private static void SimulateToEnd(GoGame game, GoCell player, Random rng)
    {
        var size = game.Size;
        var limit = size * size * 2;

        for (var move = 0; move < limit && !game.IsOver; move++)
        {
            var empties = new List<GoPoint>();
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (game.Get(x, y) == GoCell.Empty && !game.IsKoPoint(x, y)) empties.Add(new GoPoint(x, y));
                }
            }

            if (empties.Count == 0) { game.Score(); return; }

            // 随机顺序试着落子，落不下就换下一个
            var played = false;
            for (var attempt = 0; attempt < 8 && empties.Count > 0; attempt++)
            {
                var index = rng.Next(empties.Count);
                var point = empties[index];
                empties.RemoveAt(index);

                if (game.Play(game.Turn, point.X, point.Y) == GoPlayResult.Ok)
                {
                    played = true;
                    break;
                }
            }

            if (!played) game.Pass(game.Turn);
        }

        if (!game.IsOver) game.Score();
    }

    private static IEnumerable<(int X, int Y)> Neighbors(int size, int x, int y)
    {
        if (x > 0) yield return (x - 1, y);
        if (x < size - 1) yield return (x + 1, y);
        if (y > 0) yield return (x, y - 1);
        if (y < size - 1) yield return (x, y + 1);
    }
}
