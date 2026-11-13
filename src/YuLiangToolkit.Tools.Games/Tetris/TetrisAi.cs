using YuLiangToolkit.Tools.Games.Tetris;

namespace YuLiangToolkit.Tools.Games.Tetris;
/// <summary>
/// 俄罗斯方块 AI（<b>纯逻辑</b>，不碰界面，自检可直接跑）。
///
/// <para><b>算法</b>：对当前方块枚举「4 个朝向 × 每个横向位置」的全部落点，每个落点试摆一次，
/// 用经典四项指标评估棋盘：<b>消行数、空洞数、最高堆叠高度、表面起伏</b>，取综合分最高的一手。
/// 大师以上的档位会继续往后推算几块（用「接下来几块」预览当已知信息），取累计分最大的一条线。</para>
///
/// <para><b>为什么用引擎副本而不是自己算位图</b>：落点能不能到、消完行棋盘长什么样，
/// 规则细节全在 <see cref="TetrisEngine"/> 里（SRS 墙踢、垃圾行、顶死判定…）。
/// 自己再实现一遍，任何一处细节偏差都会让「算出来的落点」和「真下出来的棋盘」对不上，
/// 表现是 AI 一路乱堆、几十块就死（踩过：手写位图版长局只有引擎副本版的十分之一）。
/// 一块几十个候选，复制棋盘的开销在落子间隔面前可以忽略。</para>
/// </summary>
public static class TetrisAi
{
    /// <summary>AI 的一步：把方块转到某个朝向、放到某个位置，然后锁定。</summary>
    /// <param name="Rotation">目标朝向（0~3）。</param>
    /// <param name="X">锁定后包围盒左上角的列。</param>
    /// <param name="Y">锁定后包围盒左上角的行（可能为负 = 顶出棋盘上方）。</param>
    /// <param name="Score">这一手的评估分（调试试用）。</param>
    /// <param name="LinesCleared">这一手能消几行。</param>
    public readonly record struct Plan(int Rotation, int X, int Y, int Score, int LinesCleared);

    /// <summary>一个落点候选（放到最底之后的位置）。</summary>
    private readonly record struct Drop(int Rotation, int X, int Y);

    /// <summary>
    /// 给当前的方块挑一个落点。
    /// </summary>
    /// <param name="engine">棋局（只读，不会被改动）。</param>
    /// <param name="level">档位（决定权重与推算深度）。</param>
    /// <param name="garbageFocused">是不是互攻模式（优先消多行给对手压力）。</param>
    /// <param name="random">随机源（低档位犯傻用）。</param>
    /// <returns>落点方案；没有可落的位置返回 null。</returns>
    public static Plan? ChoosePlan(TetrisEngine engine, TetrisAiLevel level, bool garbageFocused, Random? random = null)
    {
        if (engine is null || engine.IsGameOver || engine.Current is not { } piece) return null;

        var rng = random ?? Random.Shared;
        var drops = EnumerateDrops(engine, piece.Kind);
        if (drops.Count == 0) return null;

        var weights = TetrisAiLevels.WeightsOf(level, garbageFocused);
        var depth = Math.Min(TetrisAiLevels.LookaheadPieces(level), engine.NextPieces.Count + 1);
        var beam = TetrisAiLevels.InnerBeam(level);
        var rootBeam = TetrisAiLevels.RootBeam(level);

        // 先把所有落点按「只看当前这一手」的分排一遍：
        // 深度 ≥2 时只对前 rootBeam 个做前瞻 —— 4 块前瞻是 O(候选^4)，
        // 全算一遍一手要几百毫秒，落子会明显一顿。先筛再搜，便宜又不明显变弱。
        var shallow = new List<(Plan Plan, double Value, TetrisEngine? After)>(drops.Count);
        foreach (var drop in drops)
        {
            var (after, lines) = Simulate(engine, piece.Kind, drop);
            if (after is null) continue;
            var value = Evaluate(after, lines, weights, garbageFocused);
            shallow.Add((new Plan(drop.Rotation, drop.X, drop.Y, (int)Math.Round(value), lines), value, after));
        }

        if (shallow.Count == 0) return null;

        var options = new List<(Plan Plan, double Value)>(shallow.Count);
        if (depth <= 1)
        {
            foreach (var item in shallow) options.Add((item.Plan, item.Value));
        }
        else
        {
            // 前瞻只用来「化解平局」：当前这一手分数接近的候选才去算后续，
            // 把后续分当成小权重加成。
            // ⚠ 不能把后续分直接加到当前分上当主判决 —— 那样深度越大越短视，
            // 实测「大师」比「困难」还弱（52 行 vs 114 行）。
            shallow.Sort((a, b) => b.Value.CompareTo(a.Value));
            var cutoff = shallow[0].Value - LookaheadTieWindow(depth);
            for (var i = 0; i < shallow.Count; i++)
            {
                var item = shallow[i];
                var value = item.Value;
                if (i < rootBeam && item.Value >= cutoff && item.After is { } after)
                {
                    value += SearchBest(after, engine.NextPieces, 0, depth - 1, weights, garbageFocused, beam)
                             * LookaheadDiscount;
                }
                options.Add((new Plan(item.Plan.Rotation, item.Plan.X, item.Plan.Y,
                    (int)Math.Round(value), item.Plan.LinesCleared), value));
            }
        }

        // 入门 / 普通：有时候从前几名里随便挑一个（而不是差得离谱的位置）
        if (rng.NextDouble() < TetrisAiLevels.BlunderChance(level))
        {
            var pool = options.OrderByDescending(o => o.Value)
                .Take(Math.Min(TetrisAiLevels.RootBeam(level), options.Count)).ToList();
            return pool[rng.Next(pool.Count)].Plan;
        }

        options.Sort((a, b) => b.Value.CompareTo(a.Value));
        var best = options[0].Value;
        var ties = options.Where(o => Math.Abs(o.Value - best) < 0.001).ToList();
        return ties[rng.Next(ties.Count)].Plan;
    }

    /// <summary>
    /// 按方案真的走一步。
    ///
    /// <para><b>直接摆到位再锁定，不模拟按键</b>：AI 手里有整张棋盘，本来就能算出「这块落到这一格」
    /// 的确切结果。要是照真人那样「先横移再硬降」，方块在出生高度横移时会被上方堆积卡住、
    /// 旋转还会触发墙踢 —— 实际落点和算出来的落点经常不是一回事（表现为「算得挺好、下出来一团糟」）。
    /// 直接摆到算好的位置再锁定，执行结果与评估结果完全一致。</para>
    /// </summary>
    /// <param name="engine">要操作的引擎。</param>
    /// <param name="plan">要执行的方案。</param>
    /// <returns>落地结算结果。</returns>
    public static TetrisLockResult ApplyPlan(TetrisEngine engine, Plan plan)
    {
        if (engine.IsGameOver || engine.Current is not { } piece) return default;

        var target = new TetrisPiece(piece.Kind, plan.X, plan.Y, plan.Rotation);
        if (!engine.Fits(target))
        {
            // 算好的位置现在放不下（理论上不会发生）：退回「就地硬降」，至少不卡住
            return engine.HardDrop();
        }

        engine.PlacePiece(target);
        return engine.Lock();
    }

    // ---------------------------------------------------------------- 模拟 / 搜索

    /// <summary>往后推算那几块的分怎么折算（0.2 = 只算两成）。太大会盖过当前这一手。</summary>
    private const double LookaheadDiscount = 0.2;

    /// <summary>
    /// 只有和最高分差在这个范围以内的候选，才值得去算后面几块（化解平局用）。
    /// 深度越小时筛得越松：浅搜索本身便宜，多算几个更能避免「因为筛掉了最优解而变弱」。
    /// </summary>
    private static double LookaheadTieWindow(int depth) => depth <= 2 ? 40 : 120;

    /// <summary>
    /// 在引擎副本上把这一块放到指定落点，返回「落完、消完行」的棋盘（原棋局不动）。
    /// </summary>
    private static (TetrisEngine? After, int Lines) Simulate(TetrisEngine engine, TetrominoKind kind, Drop drop)
    {
        var probe = Clone(engine);
        probe.PlacePiece(new TetrisPiece(kind, drop.X, drop.Y, drop.Rotation));
        var result = probe.Lock();
        return probe.IsGameOver ? (null, result.LinesCleared) : (probe, result.LinesCleared);
    }

    /// <summary>复制一份引擎（棋盘 + 分数等级这些不影响搜索的就不管了）。</summary>
    private static TetrisEngine Clone(TetrisEngine engine)
    {
        var clone = new TetrisEngine(new Random(1));
        clone.Reset(1, seed: 1);
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (engine.CellAt(x, y) is { } kind) clone.SetCell(x, y, kind);
            }
        }
        return clone;
    }

    /// <summary>往后再推 <paramref name="remaining"/> 块：返回这条线能拿到的最高累计分。</summary>
    private static double SearchBest(
        TetrisEngine board,
        IReadOnlyList<TetrominoKind> preview,
        int previewIndex,
        int remaining,
        TetrisAiLevels.Weights weights,
        bool garbageFocused,
        int beam)
    {
        if (remaining <= 0 || previewIndex >= preview.Count) return 0;

        var drops = EnumerateDrops(board, preview[previewIndex]);
        if (drops.Count == 0) return TopOutPenalty;      // 后一块根本出不来

        var scored = new List<(TetrisEngine Board, double Value)>(drops.Count);
        foreach (var drop in drops)
        {
            var (after, lines) = Simulate(board, preview[previewIndex], drop);
            if (after is null) continue;
            scored.Add((after, Evaluate(after, lines, weights, garbageFocused)));
        }

        if (scored.Count == 0) return TopOutPenalty;
        scored.Sort((a, b) => b.Value.CompareTo(a.Value));

        var best = double.NegativeInfinity;
        var take = Math.Min(beam, scored.Count);
        for (var i = 0; i < take; i++)
        {
            var (after, value) = scored[i];
            var deeper = SearchBest(after, preview, previewIndex + 1, remaining - 1, weights, garbageFocused, beam);
            if (value + deeper > best) best = value + deeper;
        }

        return best;
    }

    /// <summary>枚举某种方块在给定棋局上的全部落点（一路硬降到底）。</summary>
    private static List<Drop> EnumerateDrops(TetrisEngine engine, TetrominoKind kind)
    {
        var drops = new List<Drop>(TetrisPieces.RotationCount * TetrisEngine.Width);

        for (var rotation = 0; rotation < TetrisPieces.RotationCount; rotation++)
        {
            for (var x = -3; x < TetrisEngine.Width + 1; x++)
            {
                // 从棋盘上方足够高开始往下试：方块一部分还在画面上方也算放得下
                var candidate = new TetrisPiece(kind, x, -3, rotation);
                if (!engine.Fits(candidate)) continue;

                var dropped = candidate;
                while (engine.Fits(dropped.Moved(0, 1))) dropped = dropped.Moved(0, 1);

                // 落地后至少要有一格真在棋盘里（否则等于「这块根本没进场」，不算可落点）
                var visible = false;
                foreach (var (_, y) in dropped.Cells())
                {
                    if (y >= 0 && y < TetrisEngine.Height) { visible = true; break; }
                }
                if (!visible) continue;

                drops.Add(new Drop(rotation, x, dropped.Y));
            }
        }

        return drops;
    }

    // ---------------------------------------------------------------- 评估

    /// <summary>堆到出生行附近的惩罚分：下一块大概率出不来，等于死棋。</summary>
    private const double TopOutPenalty = -800;

    /// <summary>四项指标算分。</summary>
    private static double Evaluate(
        TetrisEngine board, int lines, TetrisAiLevels.Weights weights, bool garbageFocused)
    {
        var holes = 0;
        var maxHeight = 0;
        var bumpiness = 0;
        var wellDepth = 0;
        var nearComplete = 0;
        var heights = new int[TetrisEngine.Width];

        for (var x = 0; x < TetrisEngine.Width; x++)
        {
            var seenBlock = false;
            for (var y = 0; y < TetrisEngine.Height; y++)
            {
                var occupied = board.CellAt(x, y) is not null;
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

        for (var x = 0; x < TetrisEngine.Width - 1; x++) bumpiness += Math.Abs(heights[x] - heights[x + 1]);

        // 井：某一列明显比左右两边低，就是「只有竖 I 填得动」的深坑
        for (var x = 0; x < TetrisEngine.Width; x++)
        {
            var left = x > 0 ? heights[x - 1] : TetrisEngine.Height;
            var right = x < TetrisEngine.Width - 1 ? heights[x + 1] : TetrisEngine.Height;
            var deeper = Math.Min(left, right) - heights[x];
            if (deeper > 2) wellDepth += deeper - 2;
        }

        // 行完整度：差 1~2 格就满的行最值得补（「主动去消行」的直接信号）
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            var filled = 0;
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (board.CellAt(x, y) is not null) filled++;
            }
            if (filled is 8 or 9) nearComplete += filled - 7;
        }

        var score = lines * weights.Lines
                    + holes * weights.Holes
                    + maxHeight * weights.Height
                    + bumpiness * weights.Bump
                    + wellDepth * weights.Well
                    + nearComplete * NearCompleteBonus;

        // 互攻模式：消 4 行额外加分（等于送对手 4 行）
        if (garbageFocused && lines >= 4) score += 60;

        // 堆到出生行附近 = 下一块可能出不来，当废棋
        if (maxHeight >= TetrisEngine.Height - 1) score += TopOutPenalty;

        return score;
    }

    /// <summary>「差一两格就满」的行额外加分。</summary>
    private const double NearCompleteBonus = 1.5;
}
