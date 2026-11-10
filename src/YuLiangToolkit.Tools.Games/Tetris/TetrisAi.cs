using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Core.Games;

namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>
/// 俄罗斯方块 AI（<b>纯逻辑</b>，不碰界面，自检可直接跑）。
///
/// <para><b>算法</b>：对当前方块枚举「4 个朝向 × 每个横向位置」的全部落点，
/// 每个落点试摆一次，用经典的四项指标评估棋盘：
/// <b>消行数、空洞数、最高堆叠高度、表面起伏</b>，取综合分最高的一手。</para>
///
/// <para><b>难度</b>：入门只看「别堆太高」（会制造空洞、不主动消大行），
/// 越往上权重越接近标准评估，大师会额外优先「一次消多行」（互攻模式里更有用）。</para>
/// </summary>
public static class TetrisAi
{
    /// <summary>AI 的一步：把方块转到某个朝向、左右移到某列，然后硬降。</summary>
    /// <param name="Rotation">目标朝向（0~3）。</param>
    /// <param name="X">硬降后包围盒左上角的列。</param>
    /// <param name="Score">这一手的评估分（调试试用）。</param>
    /// <param name="LinesCleared">这一手能消几行。</param>
    public readonly record struct Plan(int Rotation, int X, int Score, int LinesCleared);

    /// <summary>
    /// 给当前的方块挑一个落点。
    /// </summary>
    /// <param name="engine">棋局（只读，不会被改动）。</param>
    /// <param name="difficulty">难度。</param>
    /// <param name="garbageFocused">是不是互攻模式（优先消多行给对手压力）。</param>
    /// <param name="random">随机源（入门犯傻用）。</param>
    /// <returns>落点方案；没有可落的位置返回 null。</returns>
    public static Plan? ChoosePlan(TetrisEngine engine, AiDifficulty difficulty, bool garbageFocused, Random? random = null)
    {
        if (engine is null || engine.IsGameOver || engine.Current is not { } piece) return null;

        var rng = random ?? Random.Shared;
        var options = new List<Plan>();

        for (var rotation = 0; rotation < TetrisPieces.RotationCount; rotation++)
        {
            for (var x = -3; x < TetrisEngine.Width + 1; x++)
            {
                var candidate = new TetrisPiece(piece.Kind, x, piece.Y, rotation);
                if (!engine.Fits(candidate)) continue;

                // 落到最底
                var dropped = candidate;
                while (engine.Fits(dropped.Moved(0, 1))) dropped = dropped.Moved(0, 1);
                if (!engine.Fits(dropped)) continue;

                var (score, lines) = EvaluateDrop(engine, dropped, difficulty, garbageFocused);
                options.Add(new Plan(rotation, x, score, lines));
            }
        }

        if (options.Count == 0) return null;

        // 入门：有时候乱放（明显更差的位置）
        if (rng.NextDouble() < AiDifficulties.BlunderChance(difficulty))
        {
            var pool = options.OrderByDescending(o => o.Score).Take(Math.Min(6, options.Count)).ToList();
            return pool[rng.Next(pool.Count)];
        }

        options.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = options[0].Score;
        var ties = options.Where(o => o.Score == best).ToList();
        return ties[rng.Next(ties.Count)];
    }

    /// <summary>把方块真的放上去，算评估分（在棋盘副本上算，不影响原棋局）。</summary>
    private static (int Score, int Lines) EvaluateDrop(
        TetrisEngine engine, TetrisPiece dropped, AiDifficulty difficulty, bool garbageFocused)
    {
        // 用一个临时引擎当画布：复制当前棋盘的格子
        var probe = new TetrisEngine(new Random(1));
        probe.Reset(1, seed: 1);
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                var cell = engine.CellAt(x, y);
                if (cell is { } kind) probe.SetCell(x, y, kind);
            }
        }

        // 把这块摆上去（不经过 Lock，免得触发垃圾行 / 出块）
        var before = new bool[TetrisEngine.Height];
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            before[y] = true;
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (engine.CellAt(x, y) is null) { before[y] = false; break; }
            }
        }

        foreach (var (x, y) in dropped.Cells())
        {
            if (y >= 0 && y < TetrisEngine.Height) probe.SetCell(x, y, dropped.Kind);
        }

        // 算指标
        var lines = 0;
        var maxHeight = 0;
        var holes = 0;
        var bumpiness = 0;
        var heights = new int[TetrisEngine.Width];

        for (var x = 0; x < TetrisEngine.Width; x++)
        {
            var seenBlock = false;
            for (var y = 0; y < TetrisEngine.Height; y++)
            {
                var occupied = probe.CellAt(x, y) is not null;
                if (occupied && !seenBlock)
                {
                    seenBlock = true;
                    heights[x] = TetrisEngine.Height - y;
                    if (heights[x] > maxHeight) maxHeight = heights[x];
                }
                else if (!occupied && seenBlock)
                {
                    holes++;
                }
            }
        }

        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            var full = true;
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (probe.CellAt(x, y) is null) { full = false; break; }
            }
            if (full) lines++;
        }

        for (var x = 0; x < TetrisEngine.Width - 1; x++) bumpiness += Math.Abs(heights[x] - heights[x + 1]);

        // 权重按难度调整：入门更怕高，高手更看重消行与平整
        var (wLines, wHoles, wHeight, wBump) = difficulty switch
        {
            AiDifficulty.Beginner => (0, -10.0, -6.0, -1.2),
            AiDifficulty.Hard => (garbageFocused ? 14.0 : 12.0, -12.0, -0.9, -0.6),
            AiDifficulty.Master => (garbageFocused ? 18.0 : 14.0, -14.0, -0.7, -0.5),
            _ => (8.0, -11.0, -1.2, -0.8),
        };

        var score = (int)Math.Round(
            lines * wLines + holes * wHoles + maxHeight * wHeight + bumpiness * wBump);

        // 互攻模式：消 4 行额外加分（送对手 4 行）
        if (garbageFocused && lines >= 4) score += 60;

        return (score, lines);
    }
}
