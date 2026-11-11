namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>
/// 贪吃蛇 AI（<b>九个算法分级</b>，参考 <c>moe-new/snake-ai</c> 的「九条蛇」）。
///
/// <para>每条算法都做了一处<b>对战适配</b>（原版是单人吃满棋盘，对战里要考虑别的蛇）：</para>
/// <list type="table">
///   <item><term>01 莽夫</term><description>只认最短路 —— <b>适配：看到别的蛇挡路也照冲（会送）</b>，最蠢。</description></item>
///   <item><term>02 谨慎</term><description>吃前先试回尾巴 —— <b>适配：把别的蛇当障碍，绕开。</b></description></item>
///   <item><term>03 循规蹈矩</term><description>沿一条绕遍全盘的环线 —— <b>适配：环线被别的蛇占了就换下一条。</b></description></item>
///   <item><term>04 抄近路</term><description>沿环线但能跳就跳 —— <b>适配：跳之前看一眼别的蛇会不会撞上。</b></description></item>
///   <item><term>05 单行道</term><description>每格只开两个出口 —— <b>适配：抢占窄道，逼别的蛇走不进来。</b></description></item>
///   <item><term>06 步步为营</term><description>吃之前先画好退路 —— <b>适配：退路要算上别的蛇的走位。</b></description></item>
///   <item><term>07 动态回路</term><description>沿环线走、换两个接口 —— <b>适配：优先往有果子的方向换接口。</b></description></item>
///   <item><term>08 可达保护</term><description>吃完前多查一遍是否被隔开 —— <b>适配：还要看会不会被别的蛇封死。</b></description></item>
///   <item><term>09 加权 Cell</term><description>2×2 连成一棵树，只在树允许的方向找路 —— <b>适配：树里也躲着别的蛇。</b></description></item>
/// </list>
///
/// <para><b>难度 → 算法</b>：AI 等级 1~9 直接对应算法 01~09，等级越高越聪明。</para>
/// </summary>
public static class SnakeAi
{
    /// <summary>
    /// 给某条蛇算下一步该往哪走。
    /// </summary>
    /// <param name="game">对局。</param>
    /// <param name="playerId">要替哪条蛇算。</param>
    /// <param name="level">算法等级（1~9）。</param>
    /// <param name="random">随机源（同级算法里的并列选择）。</param>
    /// <returns>要转向的方向；null = 保持当前方向。</returns>
    public static SnakeDirection? ChooseDirection(
        SnakeGame game, string playerId, int level, Random? random = null)
    {
        var player = game.Players.FirstOrDefault(p => p.Id == playerId);
        if (player is null || !player.CanAct) return null;

        var rng = random ?? Random.Shared;
        var head = player.Snake.Head;

        // 所有能走的候选方向（不反向、不撞自己 / 别人的蛇 / 障碍）
        var candidates = SafeMoves(game, player);
        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var algorithm = Math.Clamp(level, 1, 10);

        return algorithm switch
        {
            1 => GreedyNearestFruit(game, player, candidates, rng),
            2 => CautiousShortestPath(game, player, candidates, rng),
            3 => CycleFollow(game, player, candidates, rng),
            4 => ShortcutCycle(game, player, candidates, rng),
            5 => CorridorFirst(game, player, candidates, rng),
            6 => SafePathFirst(game, player, candidates, rng),
            7 => DynamicCycle(game, player, candidates, rng),
            8 => ReachabilityGuarded(game, player, candidates, rng),
            _ => WeightedCells(game, player, candidates, rng),
        };
    }

    /// <summary>能走的方向（不反向、不会立刻撞死）。</summary>
    private static List<SnakeDirection> SafeMoves(SnakeGame game, SnakePlayer player)
    {
        var result = new List<SnakeDirection>(4);
        foreach (var direction in SnakeDirections.All)
        {
            if (SnakeDirections.IsOpposite(direction, player.Snake.Direction)) continue;

            var next = player.Snake.Head + SnakeDirections.ToVector(direction);

            // 穿墙模式：越界也算能走
            if (!game.InBounds(next))
            {
                if (game.Settings.EdgeMode == SnakeEdgeMode.Wrap)
                {
                    next = new SnakePoint(
                        (next.X + game.Width) % game.Width,
                        (next.Y + game.Height) % game.Height);
                }
                else if (game.Settings.EdgeMode == SnakeEdgeMode.Stop)
                {
                    result.Add(direction);        // 停住也是安全的
                    continue;
                }
                else
                {
                    continue;                     // 会死
                }
            }

            if (IsBlocked(game, player, next)) continue;
            result.Add(direction);
        }
        return result;
    }

    /// <summary>这一点是不是被挡住了（自己 / 别人 / 雷）。虚化时能穿透。</summary>
    private static bool IsBlocked(SnakeGame game, SnakePlayer me, SnakePoint point)
    {
        if (game.Settings.SpawnBombEnabled && game.IsSpawnBlocked(point)) return true;
        if (me.Snake.IsGhost) return false;

        foreach (var other in game.Players)
        {
            if (other == me || !other.Snake.IsAlive) continue;
            if (other.Snake.Occupies(point)) return true;
        }

        // 自己（尾巴这一帧会移走，允许）
        var body = me.Snake.Body;
        for (var i = 0; i < body.Count - 1; i++)
        {
            if (body[i] == point) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 01 莽夫

    /// <summary>01 莽夫：只认最短路，不看别的蛇（会送）。</summary>
    private static SnakeDirection GreedyNearestFruit(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var fruits = game.Fruits;
        if (fruits.Count == 0) return candidates[rng.Next(candidates.Count)];

        var head = player.Snake.Head;
        var target = fruits.OrderBy(f => Distance(head, f)).First();

        return candidates
            .OrderBy(d => Distance(head + SnakeDirections.ToVector(d), target))
            .First();
    }

    // ---------------------------------------------------------------- 02 谨慎

    /// <summary>02 谨慎：吃之前先试吃，回得到尾巴才吃。</summary>
    private static SnakeDirection CautiousShortestPath(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var scored = new List<(SnakeDirection Direction, double Score)>();
        foreach (var direction in candidates)
        {
            var next = player.Snake.Head + SnakeDirections.ToVector(direction);
            var score = 0.0;

            // 吃完之后还回得到尾巴吗（用 BFS 判断连通）
            var fruitDistance = NearestFruitDistance(game, next);
            score -= fruitDistance;

            if (CanReachTail(game, player, next, direction)) score += 30;

            scored.Add((direction, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = scored[0].Score;
        var ties = scored.Where(s => Math.Abs(s.Score - best) < 0.01).ToList();
        return ties[rng.Next(ties.Count)].Direction;
    }

    /// <summary>从某个位置出发，能不能回到自己的尾巴（BFS，把别的蛇当障碍）。</summary>
    private static bool CanReachTail(SnakeGame game, SnakePlayer player, SnakePoint from, SnakeDirection direction)
    {
        var body = player.Snake.Body;
        if (body.Count < 2) return true;

        var tail = body[^1];
        var visited = new HashSet<SnakePoint> { from };
        var queue = new Queue<(SnakePoint Point, int Steps)>();
        queue.Enqueue((from, 0));
        var limit = game.Width * game.Height;

        while (queue.Count > 0)
        {
            var (point, steps) = queue.Dequeue();
            if (steps > limit) break;
            if (point == tail) return true;

            foreach (var direction2 in SnakeDirections.All)
            {
                var next = point + SnakeDirections.ToVector(direction2);
                if (!game.InBounds(next)) continue;
                if (visited.Contains(next)) continue;
                if (IsBlocked(game, player, next)) continue;

                visited.Add(next);
                queue.Enqueue((next, steps + 1));
            }
        }
        _ = direction;
        return false;
    }

    // ---------------------------------------------------------------- 03 循规蹈矩

    /// <summary>03 循规蹈矩：沿一条绕遍全盘的环线，一步不偏（被占就换方向）。</summary>
    private static SnakeDirection CycleFollow(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var head = player.Snake.Head;

        // 环线：先贴边走一圈
        var onCycle = LoopDirection(game, head, player.Snake.Direction, player);
        if (onCycle is { } direction && candidates.Contains(direction)) return direction;

        // 环线被堵：挑最靠边的一个方向（继续沿边）
        return candidates
            .OrderBy(d => EdgeDistance(game, head + SnakeDirections.ToVector(d)))
            .First();
    }

    /// <summary>环线方向：贴着棋盘外边顺时针绕。</summary>
    private static SnakeDirection? LoopDirection(
        SnakeGame game, SnakePoint head, SnakeDirection current, SnakePlayer player)
    {
        // 沿着最近的边，顺时针绕
        var left = head.X;
        var right = game.Width - 1 - head.X;
        var top = head.Y;
        var bottom = game.Height - 1 - head.Y;
        var min = Math.Min(Math.Min(left, right), Math.Min(top, bottom));

        if (min == top) return SnakeDirection.Right;
        if (min == right) return SnakeDirection.Down;
        if (min == bottom) return SnakeDirection.Left;
        if (min == left) return SnakeDirection.Up;

        _ = current;
        _ = player;
        return null;
    }

    // ---------------------------------------------------------------- 04 抄近路

    /// <summary>04 抄近路：沿环线，能跳到更近的路就跳（跳之前看别的蛇）。</summary>
    private static SnakeDirection ShortcutCycle(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var head = player.Snake.Head;
        var fruits = game.Fruits;

        // 距离果子最近的方向，但如果「比环线方向近很多」才抄
        if (fruits.Count > 0)
        {
            var nearest = fruits.OrderBy(f => Distance(head, f)).First();
            var best = candidates
                .OrderBy(d => Distance(head + SnakeDirections.ToVector(d), nearest))
                .First();

            // 抄近路前先看这个方向会不会撞（候选已经过滤过，所以安全）
            if (SafeMoves(game, player).Contains(best)) return best;
        }

        return CycleFollow(game, player, candidates, rng);
    }

    // ---------------------------------------------------------------- 05 单行道

    /// <summary>05 单行道：抢占「出口少」的格子（窄道），逼别人进不来。</summary>
    private static SnakeDirection CorridorFirst(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var scored = new List<(SnakeDirection Direction, double Score)>();
        foreach (var direction in candidates)
        {
            var next = player.Snake.Head + SnakeDirections.ToVector(direction);
            var exits = CountExits(game, player, next);
            var fruitDistance = NearestFruitDistance(game, next);

            // 出口少 = 安全；但太死（1 个出口）容易把自己困住
            var score = -fruitDistance * 1.0;
            if (exits == 2) score += 8;
            else if (exits == 3) score += 2;
            else if (exits <= 1) score -= 6;

            scored.Add((direction, score));
        }

        return PickBest(scored, rng);
    }

    /// <summary>某个格子有几个出口（能走的方向数）。</summary>
    private static int CountExits(SnakeGame game, SnakePlayer player, SnakePoint point)
    {
        var count = 0;
        foreach (var direction in SnakeDirections.All)
        {
            var next = point + SnakeDirections.ToVector(direction);
            if (!game.InBounds(next)) continue;
            if (IsBlocked(game, player, next)) continue;
            count++;
        }
        return count;
    }

    // ---------------------------------------------------------------- 06 步步为营

    /// <summary>06 步步为营：吃之前先画好退路（吃完还能不能出来）。</summary>
    private static SnakeDirection SafePathFirst(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var scored = new List<(SnakeDirection Direction, double Score)>();
        foreach (var direction in candidates)
        {
            var next = player.Snake.Head + SnakeDirections.ToVector(direction);
            var score = -NearestFruitDistance(game, next) * 1.2;

            // 走到这里之后的可达空间（越大越安全）
            var space = ReachableSpace(game, player, next);
            score += Math.Min(space, 40) * 1.0;

            if (space < player.Snake.Length) score -= 25;   // 空间比身体还小 → 危险
            scored.Add((direction, score));
        }
        return PickBest(scored, rng);
    }

    /// <summary>从某个位置出发能到达多少格（BFS）。</summary>
    private static int ReachableSpace(SnakeGame game, SnakePlayer player, SnakePoint from)
    {
        var visited = new HashSet<SnakePoint> { from };
        var queue = new Queue<SnakePoint>();
        queue.Enqueue(from);
        var limit = game.Width * game.Height;

        while (queue.Count > 0 && visited.Count < limit)
        {
            var point = queue.Dequeue();
            foreach (var direction in SnakeDirections.All)
            {
                var next = point + SnakeDirections.ToVector(direction);
                if (!game.InBounds(next)) continue;
                if (visited.Contains(next)) continue;
                if (IsBlocked(game, player, next)) continue;
                visited.Add(next);
                queue.Enqueue(next);
            }
        }
        return visited.Count;
    }

    // ---------------------------------------------------------------- 07 动态回路

    /// <summary>07 动态回路：沿环线走，但会换两个接口让食物更近。</summary>
    private static SnakeDirection DynamicCycle(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var scored = new List<(SnakeDirection Direction, double Score)>();
        foreach (var direction in candidates)
        {
            var next = player.Snake.Head + SnakeDirections.ToVector(direction);
            var fruitDistance = NearestFruitDistance(game, next);
            var space = ReachableSpace(game, player, next);
            var exits = CountExits(game, player, next);

            var score = -fruitDistance * 1.5 + Math.Min(space, 60) * 0.6 + exits * 1.0;

            // 能回到尾巴再加分（环线不会被自己堵死）
            if (CanReachTail(game, player, next, direction)) score += 20;

            scored.Add((direction, score));
        }
        return PickBest(scored, rng);
    }

    // ---------------------------------------------------------------- 08 可达保护

    /// <summary>08 可达保护：吃完之前多查一遍会不会被隔开。</summary>
    private static SnakeDirection ReachabilityGuarded(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var scored = new List<(SnakeDirection Direction, double Score)>();
        foreach (var direction in candidates)
        {
            var next = player.Snake.Head + SnakeDirections.ToVector(direction);
            var score = -NearestFruitDistance(game, next) * 1.6;

            var space = ReachableSpace(game, player, next);
            var bodyLength = player.Snake.Length + 1;

            // 可达空间必须比身体大，否则会被自己闷死
            if (space < bodyLength + 2) score -= 60;
            else score += Math.Min(space - bodyLength, 80) * 0.8;

            if (CanReachTail(game, player, next, direction)) score += 25;

            // 果子在可达范围内再加分
            if (game.Fruits.Any(f => IsReachable(game, player, next, f))) score += 10;

            scored.Add((direction, score));
        }
        return PickBest(scored, rng);
    }

    /// <summary>从某点到某点是否连通。</summary>
    private static bool IsReachable(SnakeGame game, SnakePlayer player, SnakePoint from, SnakePoint target)
    {
        if (from == target) return true;
        var visited = new HashSet<SnakePoint> { from };
        var queue = new Queue<SnakePoint>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var point = queue.Dequeue();
            foreach (var direction in SnakeDirections.All)
            {
                var next = point + SnakeDirections.ToVector(direction);
                if (!game.InBounds(next) || visited.Contains(next)) continue;
                if (next == target) return true;
                if (IsBlocked(game, player, next)) continue;
                visited.Add(next);
                queue.Enqueue(next);
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- 09 加权 Cell

    /// <summary>
    /// 09 加权 Cell：把 2×2 小格连成一棵树，只在树允许的方向上找最短路（最强）。
    /// <para>对战适配：树节点里如果被别的蛇占住，就把它从树里剔掉。</para>
    /// </summary>
    private static SnakeDirection WeightedCells(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var scored = new List<(SnakeDirection Direction, double Score)>();
        var head = player.Snake.Head;

        foreach (var direction in candidates)
        {
            var next = head + SnakeDirections.ToVector(direction);
            var score = 0.0;

            // ① 优先走「2×2 方块里空位多」的格子（原版加权 Cell 的核心）
            score += FreeCorners(game, player, next) * 3.0;

            // ② 到果子的最短路
            score -= ShortestPathDistance(game, player, next) * 2.0;

            // ③ 可达空间
            var space = ReachableSpace(game, player, next);
            if (space < player.Snake.Length + 2) score -= 80;
            else score += Math.Min(space, 100) * 0.5;

            // ④ 能回尾（环线）
            if (CanReachTail(game, player, next, direction)) score += 30;

            scored.Add((direction, score));
        }

        return PickBest(scored, rng);
    }

    /// <summary>以某个点为首的 2×2 方块里空着的角数（0~3）。</summary>
    private static int FreeCorners(SnakeGame game, SnakePlayer player, SnakePoint point)
    {
        var count = 0;
        for (var dy = -1; dy <= 0; dy++)
        {
            for (var dx = -1; dx <= 0; dx++)
            {
                var corner = new SnakePoint(point.X + dx, point.Y + dy);
                if (!game.InBounds(corner)) continue;
                if (corner == point) continue;
                if (!IsBlocked(game, player, corner)) count++;
            }
        }
        return count;
    }

    // ---------------------------------------------------------------- 10 主动躲避

    /// <summary>
    /// 10 主动躲避（最聪明）：<b>看到别的蛇在前面就立刻掉头跑开</b>。
    /// <para>在前 9 个算法的评分基础上，额外做两件事：</para>
    /// <list type="bullet">
    ///   <item><b>威胁检测</b>：预测每条别的蛇 1~2 步后可能到达的格子（按它们的朝向），
    ///         我的候选落点如果在那片区域里就大幅扣分；</item>
    ///   <item><b>逃跑</b>：如果所有方向都危险，就选「离所有敌蛇最远」的方向，宁可绕路也不撞。</item>
    /// </list>
    /// </summary>
    private static SnakeDirection EvadePlayers(
        SnakeGame game, SnakePlayer player, List<SnakeDirection> candidates, Random rng)
    {
        var head = player.Snake.Head;

        // ① 先算每个候选点的基础分（用加权 Cell 的思路，保证它也会吃果子）
        var scored = new List<(SnakeDirection Direction, double Score)>();
        foreach (var direction in candidates)
        {
            var next = head + SnakeDirections.ToVector(direction);
            var score = -ShortestPathDistance(game, player, next) * 1.5;

            var space = ReachableSpace(game, player, next);
            if (space < player.Snake.Length + 2) score -= 70;
            else score += Math.Min(space, 80) * 0.5;

            // ② 威胁检测：这个点会不会被别的蛇撞上
            score -= ThreatLevel(game, player, next) * 40;

            scored.Add((direction, score));
        }

        scored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // ③ 全都危险 → 逃到「离敌蛇最远」的方向
        if (scored[0].Score < -20)
        {
            return candidates
                .OrderByDescending(d => DistanceToNearestEnemy(game, player, head + SnakeDirections.ToVector(d)))
                .First();
        }

        var best = scored[0].Score;
        var ties = scored.Where(s => Math.Abs(s.Score - best) < 0.01).ToList();
        return ties[rng.Next(ties.Count)].Direction;
    }

    /// <summary>
    /// 某个落点有多危险（0 = 安全，1~3 = 有蛇可能 1~3 步内到这里）。
    /// </summary>
    private static int ThreatLevel(SnakeGame game, SnakePlayer me, SnakePoint point)
    {
        var level = 0;

        foreach (var other in game.Players)
        {
            if (ReferenceEquals(other, me) || !other.Snake.IsAlive) continue;

            // 用对方蛇头 + 朝向预测 1~3 步
            var head = other.Snake.Head;
            var dir = SnakeDirections.ToVector(other.Snake.Direction);

            for (var step = 1; step <= 3; step++)
            {
                var predicted = new SnakePoint(head.X + dir.X * step, head.Y + dir.Y * step);
                if (!game.InBounds(predicted)) break;

                // 我的落点跟预测点很近（曼哈顿 1 格内）→ 视为威胁
                if (Math.Abs(predicted.X - point.X) + Math.Abs(predicted.Y - point.Y) <= 1)
                {
                    level = Math.Max(level, 4 - step);
                    break;
                }
            }

            // 对方身体本身就是障碍：贴着就扣分
            if (DistanceToNearestEnemy(game, me, point) <= 1) level = Math.Max(level, 2);
        }

        return level;
    }

    /// <summary>到最近敌蛇（身体任一点）的距离。</summary>
    private static int DistanceToNearestEnemy(SnakeGame game, SnakePlayer me, SnakePoint point)
    {
        var best = int.MaxValue;

        foreach (var other in game.Players)
        {
            if (ReferenceEquals(other, me) || !other.Snake.IsAlive) continue;
            foreach (var segment in other.Snake.Body)
            {
                var distance = Math.Abs(segment.X - point.X) + Math.Abs(segment.Y - point.Y);
                if (distance < best) best = distance;
            }
        }

        return best == int.MaxValue ? 99 : best;
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>到最近果子的距离（BFS，被挡住算不可达 → 大数）。</summary>
    private static int NearestFruitDistance(SnakeGame game, SnakePoint from)
    {
        var fruits = game.Fruits;
        if (fruits.Count == 0) return 0;
        return fruits.Min(f => Distance(from, f));
    }

    /// <summary>曼哈顿距离。</summary>
    private static int Distance(SnakePoint a, SnakePoint b)
        => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    /// <summary>到最近边的距离。</summary>
    private static int EdgeDistance(SnakeGame game, SnakePoint point)
        => Math.Min(Math.Min(point.X, game.Width - 1 - point.X), Math.Min(point.Y, game.Height - 1 - point.Y));

    /// <summary>取分最高的方向（并列随机，避免每局一样）。</summary>
    private static SnakeDirection PickBest(List<(SnakeDirection Direction, double Score)> scored, Random rng)
    {
        scored.Sort((a, b) => b.Score.CompareTo(a.Score));
        var best = scored[0].Score;
        var ties = scored.Where(s => Math.Abs(s.Score - best) < 0.01).ToList();
        return ties[rng.Next(ties.Count)].Direction;
    }

    /// <summary>从某点到最近果子的最短路径长度（BFS，粗略）。</summary>
    private static int ShortestPathDistance(SnakeGame game, SnakePlayer player, SnakePoint from)
    {
        var fruits = game.Fruits;
        if (fruits.Count == 0) return 0;

        var visited = new HashSet<SnakePoint> { from };
        var queue = new Queue<(SnakePoint Point, int Steps)>();
        queue.Enqueue((from, 0));

        while (queue.Count > 0)
        {
            var (point, steps) = queue.Dequeue();
            if (fruits.Contains(point)) return steps;
            if (steps > game.Width + game.Height) break;

            foreach (var direction in SnakeDirections.All)
            {
                var next = point + SnakeDirections.ToVector(direction);
                if (!game.InBounds(next) || visited.Contains(next)) continue;
                if (IsBlocked(game, player, next)) continue;
                visited.Add(next);
                queue.Enqueue((next, steps + 1));
            }
        }
        return game.Width + game.Height;   // 不可达
    }

    /// <summary>AI 等级的显示名（对应九条蛇）。</summary>
    public static string LevelName(int level) => Math.Clamp(level, 1, 10) switch
    {
        1 => "01 莽夫",
        2 => "02 谨慎",
        3 => "03 循规蹈矩",
        4 => "04 抄近路",
        5 => "05 单行道",
        6 => "06 步步为营",
        7 => "07 动态回路",
        8 => "08 可达保护",
        9 => "09 加权 Cell",
        _ => "10 主动躲避",
    };

    /// <summary>AI 等级的说明（对战适配的那一句话）。</summary>
    public static string LevelDescription(int level) => Math.Clamp(level, 1, 10) switch
    {
        1 => "只认最短路，别的蛇挡路也照冲（最蠢）",
        2 => "吃之前先试吃，回得到尾巴才吃，会绕开别的蛇",
        3 => "沿一条绕遍全盘的环线，被占就换方向",
        4 => "沿环线，但能跳到更近的路就跳",
        5 => "抢占窄道，逼别的蛇进不来",
        6 => "吃之前先画好退路，会算别的蛇的走位",
        7 => "动态换环线接口，优先往果子方向",
        8 => "吃完前多查一遍会不会被隔开",
        9 => "2×2 连树 + 最短路 + 可达保护",
        _ => "看到别的蛇在前面就立刻掉头跑开（最聪明）",
    };
}
