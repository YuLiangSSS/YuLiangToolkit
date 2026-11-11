using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>
/// 飞行棋 AI（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>为什么不能搜索</b>：飞行棋是纯运气 + 局部决策（掷骰子不可控），
/// 深层搜索没有意义。所以 AI 做的是「<b>给定点数后，选哪枚棋子走最划算</b>」，
/// 外加「什么时候该掷」。</para>
///
/// <para><b>四档难度</b>：</para>
/// <list type="bullet">
///   <item><b>入门</b>：随机挑一枚能走的（会浪费好局面）；</item>
///   <item><b>普通</b>：按「踢人 &gt; 进终点 &gt; 起飞 &gt; 走得远」排序；</item>
///   <item><b>困难</b>：再加「别送子」—— 走到别人射程内要扣分，能进终点就直接进；</item>
///   <item><b>大师</b>：全局评估 —— 算走进终点后的完成数、剩余棋子的总进度、被反杀的风险，
///         并用「一圈总步数 - 当前进度」估算还要多久到家。</item>
/// </list>
/// </summary>
public static class LudoAi
{
    /// <summary>走一圈 + 回家通道的总步数。</summary>
    private static int TotalSteps => LudoBoard.TrackLength + LudoBoard.HomeStretch;

    /// <summary>
    /// 选一枚棋走。<paramref name="dice"/> 是已经掷出来的点数。
    /// </summary>
    /// <returns>要走的棋子序号；返回 -1 表示这手没得走（调用方应跳过）。</returns>
    public static int ChoosePiece(LudoGame game, LudoColor color, int dice, AiDifficulty difficulty, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var movable = game.LegalMovable(colr: color, dice);
        if (movable.Count == 0) return -1;
        if (movable.Count == 1) return movable[0].Index;

        // 入门：随机（会错过踢人 / 进终点）
        if (difficulty == AiDifficulty.Beginner || rng.NextDouble() < AiDifficulties.BlunderChance(difficulty))
            return movable[rng.Next(movable.Count)].Index;

        var scored = movable
            .Select(piece => (Piece: piece, Score: Score(game, color, piece, dice, difficulty)))
            .OrderByDescending(x => x.Score)
            .ToList();

        // 并列时随机，避免每局套路一样
        var best = scored[0].Score;
        var ties = scored.Where(s => Math.Abs(s.Score - best) < 0.001).Select(s => s.Piece).ToList();
        return ties[rng.Next(ties.Count)].Index;
    }

    /// <summary>给一枚棋子的这一手打分（越大越好）。</summary>
    private static double Score(
        LudoGame game, LudoColor color, LudoPiece piece, int dice, AiDifficulty difficulty)
    {
        var score = 0.0;

        // ① 能进终点：最优先（直接完成一枚）
        var preview = game.PreviewCell(piece, dice);
        if (preview == LudoBoard.Finish) score += 1000;

        // ② 起飞：把大本营里的子送上跑道
        if (piece.State == LudoPieceState.Home) score += 120;

        // ③ 踢人：能踩到对方 / 能飞线打到对方
        var captures = game.CaptureTargets(piece, dice);
        foreach (var victim in captures)
        {
            // 踢掉对方越靠后的棋子越值钱（它快到家了）
            score += 150 + victim.TrackSteps * 0.8;
        }

        if (game.Rules.CaptureOnFlightLine)
            score += FlightLineHits(game, color, piece, dice) * 60;

        // ④ 进度：离终点越近越好（走完一圈 + 回家通道）
        if (piece.State == LudoPieceState.Track)
        {
            var after = Math.Min(TotalSteps, piece.TrackSteps + dice);
            var beforeProgress = piece.TrackSteps / (double)TotalSteps;
            var afterProgress = after / (double)TotalSteps;
            score += (afterProgress - beforeProgress) * 200;

            // 困难以上：别停在别人射程内（被反杀风险）
            if (difficulty is AiDifficulty.Hard or AiDifficulty.Master)
                score -= DangerAt(game, color, preview) * 40;
        }

        // ⑤ 大师：全局局面 —— 已完成数量 + 剩余棋子的总进度
        if (difficulty == AiDifficulty.Master)
        {
            score += game.FinishedCount(color) * 50;

            var totalProgress = game.PiecesOf(color)
                .Where(p => p.State != LudoPieceState.Finished)
                .Sum(p => p.State == LudoPieceState.Home ? 0 : p.TrackSteps);
            score += totalProgress * 0.05;
        }

        return score;
    }

    /// <summary>这一手经过飞行线能打到几个对方棋子。</summary>
    private static int FlightLineHits(LudoGame game, LudoColor color, LudoPiece piece, int dice)
    {
        if (piece.State != LudoPieceState.Track) return 0;

        var hits = 0;
        var from = piece.TrackSteps;
        var to = Math.Min(from + dice, LudoBoard.TrackLength);

        for (var step = from + 1; step <= to; step++)
        {
            var cell = LudoGame.CellOfTrackStep(color, step);
            if (!LudoBoard.IsFlightLine(cell.X, cell.Y)) continue;

            foreach (var other in game.Pieces)
            {
                if (other.Color == color) continue;
                if (other.State != LudoPieceState.Track || other.IsOnHomeStretch) continue;
                if (other.Cell() == cell) hits++;
            }
        }

        return hits;
    }

    /// <summary>某个格子有多危险（0 = 安全；数字越大表示越容易被对方踩）。</summary>
    private static int DangerAt(LudoGame game, LudoColor color, (int X, int Y) cell)
    {
        var danger = 0;

        foreach (var other in game.Pieces)
        {
            if (other.Color == color) continue;
            if (other.State != LudoPieceState.Track || other.IsOnHomeStretch) continue;

            // 对方距离这个格子 1~6 步（一个骰子范围内）→ 有被踩的风险
            var distance = ((other.TrackSteps - StepOf(other.Color, cell)) % LudoBoard.TrackLength
                            + LudoBoard.TrackLength) % LudoBoard.TrackLength;
            if (distance is >= 1 and <= 6) danger++;
        }

        return danger;
    }

    /// <summary>某个颜色走到某一格需要多少步（找不到返回 -1）。</summary>
    private static int StepOf(LudoColor color, (int X, int Y) cell)
    {
        for (var step = 0; step < LudoBoard.TrackLength; step++)
        {
            if (LudoGame.CellOfTrackStep(color, step) == cell) return step;
        }
        return -1;
    }

    /// <summary>
    /// 掷骰前的决策：现在该不该掷（目前总是掷；留这个接口给以后做「等一手」的策略）。
    /// </summary>
    public static bool ShouldRoll(LudoGame game, LudoColor color) => game.CanRoll(color);
}
