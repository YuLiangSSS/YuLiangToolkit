namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>玩法。</summary>
public enum SnakeMode
{
    /// <summary>玩法一：多个区域共用一颗果子，谁先吃到谁得分。</summary>
    SharedFruit = 0,

    /// <summary>玩法二：一张大棋盘大混战，多条蛇、多颗果子。</summary>
    BattleRoyale = 1,
}

/// <summary>一条蛇的玩家统计（用于评分）。</summary>
public sealed class SnakeStats
{
    /// <summary>吃到几颗果子。</summary>
    public int Fruits { get; set; }

    /// <summary>连续吃果的「同步数」（combo 计数，断连清零）。</summary>
    public int Combo { get; set; }

    /// <summary>最长连吃（用来说明表现）。</summary>
    public int BestCombo { get; set; }

    /// <summary>活了多久（步数）。</summary>
    public int AliveSteps { get; set; }

    /// <summary>死了几次。</summary>
    public int Deaths { get; set; }

    /// <summary>
    /// 综合评分：<b>吃果 60% + 存活 20% + 连吃 20%</b>（需求里指定的权重）。
    /// </summary>
    /// <param name="maxFruits">全场最多的吃果数（用来归一化）。</param>
    /// <param name="maxSteps">全场最长的存活步数。</param>
    /// <param name="maxCombo">全场最高的连吃。</param>
    public double Score(int maxFruits, int maxSteps, int maxCombo)
    {
        var fruitPart = maxFruits <= 0 ? 0 : (double)Fruits / maxFruits * 0.6;
        var alivePart = maxSteps <= 0 ? 0 : (double)AliveSteps / maxSteps * 0.2;
        var comboPart = maxCombo <= 0 ? 0 : (double)BestCombo / maxCombo * 0.2;
        return (fruitPart + alivePart + comboPart) * 100;
    }
}

/// <summary>一条蛇在引擎里的完整状态（AI / 联机 / 界面共用）。</summary>
public sealed class SnakePlayer
{
    /// <summary>玩家 id（联机用；单机 / AI 用 "ai1" 这种）。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>蛇身。</summary>
    public SnakeBody Snake { get; } = new();

    /// <summary>统计。</summary>
    public SnakeStats Stats { get; } = new();

    /// <summary>是不是 AI。</summary>
    public bool IsAi { get; set; }

    /// <summary>AI 的算法（1~9，对应九条蛇）。</summary>
    public int AiAlgorithm { get; set; }

    /// <summary>剩余命数（玩法二用）。</summary>
    public int Lives { get; set; } = 1;

    /// <summary>重生倒计时（步数；&gt; 0 时还没复活）。</summary>
    public int RespawnIn { get; set; }

    /// <summary>固定出生点（开局分配，重生时回到这里）。</summary>
    public SnakePoint? SpawnPoint { get; set; }

    /// <summary>重生后的无敌剩余时间（秒，渲染 / 提示用）。</summary>
    public double InvincibleSeconds { get; set; }

    /// <summary>现在能不能操作（活着且不在重生）。</summary>
    public bool CanAct => Snake.IsAlive && RespawnIn <= 0;
}

/// <summary>
/// 贪吃蛇对局引擎（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>两种玩法</b>：</para>
/// <list type="number">
///   <item><b>共享果</b>（<see cref="SnakeMode.SharedFruit"/>）：把棋盘横向切成
///         <see cref="RegionCount"/> 个一样大的区域，每条蛇在各自区域里活动，
///         但果子在<b>所有区域里同一个相对位置</b>同步出现 —— 谁先吃到谁得分，
///         吃完之后所有区域一起刷新位置。</item>
///   <item><b>大混战</b>（<see cref="SnakeMode.BattleRoyale"/>）：一张大棋盘、多条蛇、多颗果子、
///         出生点互不重复且尽量隔开，可以开「出生点 3×3 爆炸」防止堵点。</item>
/// </list>
///
/// <para><b>每帧</b>（<see cref="Tick"/>）流程：所有蛇按当前方向前进一格 → 处理吃果 / 撞墙 /
/// 撞蛇 / 撞自己 → 更新统计与重生 → 检查结束条件。</para>
/// </summary>
public sealed class SnakeGame
{
    /// <summary>每条蛇开局长度（需求里的「保留原蛇身长度」也以它作为下限）。</summary>
    public const int InitialLength = 3;

    /// <summary>死亡后重生倒计时（步数）。</summary>
    public const int RespawnSteps = 6;

    /// <summary>重生后的虚化保护步数（需求：「重生后两秒无敌」≈ 这么多步）。</summary>
    public const int GhostProtectionSteps = 4;

    /// <summary>死亡残影的透明度（需求里提到的 80% 透明 ≈ 0.2 不透明度）。</summary>
    public const double DeathGhostOpacity = 0.2;

    private readonly List<SnakePlayer> _players = new();
    private readonly Random _random;
    private readonly HashSet<SnakePoint> _fruits = new();
    private readonly List<SnakePoint> _deathMarks = new();
    private readonly HashSet<SnakePoint> _blockedSpawns = new();

    /// <summary>创建一局。</summary>
    /// <param name="settings">设置。</param>
    /// <param name="random">随机源（固定种子可复现，自检用）。</param>
    public SnakeGame(SnakeSettings settings, Random? random = null)
    {
        Settings = settings;
        _random = random ?? new Random();
        _players.Capacity = settings.MaxPlayers;
    }

    // ---------------------------------------------------------------- 配置 / 状态

    /// <summary>对局设置。</summary>
    public SnakeSettings Settings { get; }

    /// <summary>棋盘宽度（像素格）。</summary>
    public int Width => Settings.Width;

    /// <summary>棋盘高度。</summary>
    public int Height => Settings.Height;

    /// <summary>全部玩家（含 AI）。</summary>
    public IReadOnlyList<SnakePlayer> Players => _players;

    /// <summary>场上的果子。</summary>
    public IReadOnlyCollection<SnakePoint> Fruits => _fruits;

    /// <summary>死亡残影（只用于渲染，不参与碰撞）。</summary>
    public IReadOnlyList<SnakePoint> DeathMarks => _deathMarks;

    /// <summary>走了多少步（也当作时间）。</summary>
    public int Step { get; private set; }

    /// <summary>对局是否开始（倒计时阶段为 false）。</summary>
    public bool Started { get; private set; }

    /// <summary>对局是否结束。</summary>
    public bool IsOver { get; private set; }

    /// <summary>结束原因。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>排名（结束或中途查看时算，按评分从高到低）。</summary>
    public IReadOnlyList<(SnakePlayer Player, double Score)> Ranking { get; private set; } =
        Array.Empty<(SnakePlayer, double)>();

    /// <summary>
    /// 开局倒计时的剩余时间（<b>真实时间</b>）。
    /// <para>⚠ 之前这里存的是「步数」，但一步只有 1/Speed 秒，3 秒被当成 3 步 → 实际 0.3 秒（踩过）。
    /// 现在改成基于 <see cref="Tick"/> 的调用时刻累计真实经过的时间。</para>
    /// </summary>
    public TimeSpan CountdownRemaining { get; private set; }

    /// <summary>计时用的「上次 Tick 时刻」（单调时钟）。</summary>
    private long _lastTickTimestamp;

    /// <summary>倒计时还剩几秒（界面显示用，向上取整）。</summary>
    public int CountdownSecondsLeft
        => CountdownRemaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(CountdownRemaining.TotalSeconds);

    // ---------------------------------------------------------------- 开局

    /// <summary>加入一个玩家（开局前调用）。</summary>
    /// <returns>成功返回空字符串，失败返回原因。</returns>
    public string AddPlayer(SnakePlayer player)
    {
        if (Started) return "对局已经开始了，不能再加入";
        if (_players.Count >= Settings.MaxPlayers) return "人数已满";
        _players.Add(player);
        return "";
    }

    /// <summary>
    /// 开始对局：摆蛇、放果子、进入倒计时。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回原因。</returns>
    public string Start()
    {
        if (_players.Count == 0) return "没有玩家";
        if (Started) return "对局已经开始了";

        // 清空
        _fruits.Clear();
        _deathMarks.Clear();
        _blockedSpawns.Clear();
        _ownBlastZones.Clear();
        Step = 0;
        IsOver = false;
        ResultText = "";

        foreach (var player in _players)
        {
            player.Stats.Fruits = 0;
            player.Stats.Combo = 0;
            player.Stats.BestCombo = 0;
            player.Stats.AliveSteps = 0;
            player.Stats.Deaths = 0;
            player.Lives = Settings.Lives;
            player.RespawnIn = 0;
            player.Snake.Body.Clear();
            player.Snake.GhostTrail.Clear();
            player.Snake.GhostSteps = 0;
            player.Snake.IsAlive = true;
        }

        // 摆蛇
        if (Settings.Mode == SnakeMode.SharedFruit)
        {
            PlaceSharedFruitSnakes();
        }
        else
        {
            PlaceBattleSnakes();
        }

        // 果子
        RefillFruits();

        CountdownRemaining = TimeSpan.FromSeconds(Math.Max(0, Settings.CountdownSeconds));
        _lastTickTimestamp = 0;
        Started = true;
        return "";
    }

    /// <summary>共享果玩法：每个玩家占一个横向区域，蛇从各自区域中间出发。</summary>
    private void PlaceSharedFruitSnakes()
    {
        var regionWidth = Math.Max(6, Width / Math.Max(1, _players.Count));
        for (var i = 0; i < _players.Count; i++)
        {
            var player = _players[i];
            var startX = i * regionWidth + regionWidth / 2;
            var startY = Height / 2;
            player.SpawnPoint = new SnakePoint(startX, startY);
            FillSnake(player, startX, startY, SnakeDirection.Right, Settings.InitialLength);
        }
    }

    /// <summary>大混战：随机出生点，蛇与蛇之间尽量隔开、互不重复。</summary>
    private void PlaceBattleSnakes()
    {
        var occupied = new List<SnakePoint>();

        foreach (var player in _players)
        {
            var spawn = FindSpawnPoint(occupied);
            var direction = (SnakeDirection)_random.Next(4);
            FillSnake(player, spawn.X, spawn.Y, direction, Settings.InitialLength);

            // 记录固定出生点（重生时回到这里）；光爆只在「出生那一刻」生效，不留常驻障碍
            player.SpawnPoint = spawn;
            if (Settings.SpawnBombEnabled) SpawnBurst(spawn, player);

            occupied.AddRange(player.Snake.Body);
            occupied.Add(spawn);
        }
    }

    /// <summary>找一个离已有蛇尽量远、且不重复的出生点。</summary>
    private SnakePoint FindSpawnPoint(IReadOnlyList<SnakePoint> occupied)
    {
        var margin = 2;
        var best = new SnakePoint(Width / 2, Height / 2);
        var bestDistance = -1;

        for (var attempt = 0; attempt < 400; attempt++)
        {
            var point = new SnakePoint(
                margin + _random.Next(Math.Max(1, Width - margin * 2)),
                margin + _random.Next(Math.Max(1, Height - margin * 2)));

            if (_blockedSpawns.Contains(point)) continue;
            if (occupied.Any(p => p == point)) continue;

            var distance = occupied.Count == 0 ? int.MaxValue : occupied.Min(p => Math.Abs(p.X - point.X) + Math.Abs(p.Y - point.Y));
            if (distance > bestDistance)
            {
                bestDistance = distance;
                best = point;
            }
            if (bestDistance >= 8) break;
        }

        return best;
    }

    /// <summary>
    /// 在出生点周围 3×3 布置爆炸区（防止有人蹲点堵出生）。
    /// <para><paramref name="owner"/> 自己的蛇身与出生格<b>不算</b>在爆炸区里 —— 否则一出生就死。</para>
    /// </summary>
    private void MarkSpawnBlast(SnakePoint center, SnakePlayer owner)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var point = new SnakePoint(center.X + dx, center.Y + dy);
                if (!InBounds(point)) continue;

                // 记录进「自己的爆炸区」（自己免疫）
                if (!_ownBlastZones.TryGetValue(owner.Id, out var own))
                {
                    own = new HashSet<SnakePoint>();
                    _ownBlastZones[owner.Id] = own;
                }
                own.Add(point);

                // 自己的出生格 / 自己的蛇身不进爆炸区
                if (owner.Snake.Body.Contains(point)) continue;

                _blockedSpawns.Add(point);
                _deathMarks.Add(point);

                // 把范围内已有的（别人的）蛇身也炸掉（开局一般是空的，留给 AI 复用时用得上）
                foreach (var player in _players)
                {
                    if (ReferenceEquals(player, owner)) continue;
                    player.Snake.Body.RemoveAll(p => p == point);
                }
            }
        }
    }

    /// <summary>摆一条蛇（自动截断到棋盘内）。</summary>
    private void FillSnake(SnakePlayer player, int x, int y, SnakeDirection direction, int length)
    {
        player.Snake.Body.Clear();
        player.Snake.Direction = direction;
        player.Snake.PendingDirection = null;
        player.Snake.IsAlive = true;
        player.Snake.GhostSteps = 0;
        player.Snake.GhostTrail.Clear();

        var dir = SnakeDirections.ToVector(direction);
        for (var i = 0; i < Math.Max(InitialLength, length); i++)
        {
            var point = new SnakePoint(
                Math.Clamp(x - dir.X * i, 0, Width - 1),
                Math.Clamp(y - dir.Y * i, 0, Height - 1));

            // 蛇身不能重叠
            if (player.Snake.Body.Contains(point)) break;
            player.Snake.Body.Add(point);
        }
    }

    // ---------------------------------------------------------------- 输入

    /// <summary>给某条蛇设置方向（不允许 180° 掉头）。</summary>
    public void SetDirection(string playerId, SnakeDirection direction)
    {
        var player = _players.FirstOrDefault(p => p.Id == playerId);
        if (player is null || !player.CanAct) return;

        // 不能直接反向（否则会撞自己第二格）
        if (player.Snake.Body.Count > 1 && SnakeDirections.IsOpposite(direction, player.Snake.Direction)) return;

        player.Snake.PendingDirection = direction;
    }

    // ---------------------------------------------------------------- 每帧推进

    /// <summary>推进一帧。</summary>
    public void Tick()
    {
        if (!Started || IsOver) return;

        // 倒计时阶段：按<b>真实经过的时间</b>扣（不是按帧扣 —— 按帧扣会快上好几倍）
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = _lastTickTimestamp == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((now - _lastTickTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency);
        _lastTickTimestamp = now;

        if (CountdownRemaining > TimeSpan.Zero)
        {
            CountdownRemaining -= elapsed;
            if (CountdownRemaining < TimeSpan.Zero) CountdownRemaining = TimeSpan.Zero;
            return;
        }

        Step++;

        // ① 先算出所有蛇的新头位置（同时计算，避免先后顺序影响公平性）
        var planned = new Dictionary<string, SnakePoint>(StringComparer.Ordinal);
        var stopped = new HashSet<string>(StringComparer.Ordinal);

        foreach (var player in _players)
        {
            if (!player.CanAct) continue;

            var snake = player.Snake;
            if (snake.PendingDirection is { } pending && !SnakeDirections.IsOpposite(pending, snake.Direction))
            {
                snake.Direction = pending;
            }
            snake.PendingDirection = null;

            var next = snake.Head + SnakeDirections.ToVector(snake.Direction);

            // 边缘规则
            if (!InBounds(next))
            {
                switch (Settings.EdgeMode)
                {
                    case SnakeEdgeMode.Wrap:
                        next = new SnakePoint(
                            (next.X + Width) % Width,
                            (next.Y + Height) % Height);
                        break;
                    case SnakeEdgeMode.Stop:
                        stopped.Add(player.Id);
                        continue;
                    default:
                        Kill(player, "撞墙");
                        continue;
                }
            }

            planned[player.Id] = next;
        }

        // ② 吃果判定（先算，被吃掉的果子本帧不再参与碰撞）
        var eatenThisTick = new HashSet<SnakePoint>();
        foreach (var (playerId, next) in planned)
        {
            if (!_fruits.Contains(next)) continue;
            var player = _players.First(p => p.Id == playerId);
            player.Stats.Fruits++;
            player.Stats.Combo++;
            player.Stats.BestCombo = Math.Max(player.Stats.BestCombo, player.Stats.Combo);
            eatenThisTick.Add(next);
        }

        if (eatenThisTick.Count > 0)
        {
            // 共享果：一颗被吃，全场刷新
            if (Settings.Mode == SnakeMode.SharedFruit)
            {
                _fruits.Clear();
                RefillFruits();
            }
            else
            {
                foreach (var point in eatenThisTick) _fruits.Remove(point);
                RefillFruits();
            }
        }

        // ③ 移动 + 碰撞
        var heads = new Dictionary<SnakePoint, string>();
        foreach (var (playerId, next) in planned)
        {
            var player = _players.First(p => p.Id == playerId);

            // 头对头：两条蛇同时进同一格 → 都死
            if (heads.ContainsKey(next))
            {
                Kill(player, "头对头");
                var other = _players.First(p => p.Id == heads[next]);
                Kill(other, "头对头");
                continue;
            }
            heads[next] = playerId;

            // 撞墙（穿墙模式下已经在上面处理）
            if (!InBounds(next))
            {
                Kill(player, "撞墙");
                continue;
            }

            // 撞自己 / 撞别人（虚化状态可穿透）
            if (!player.Snake.IsGhost)
            {
                var hit = _players.FirstOrDefault(p =>
                    p != player && p.Snake.IsAlive && p.Snake.Occupies(next));
                if (hit is not null)
                {
                    Kill(player, $"撞到 {hit.Name}");
                    continue;
                }

                // 撞自己：尾巴这一帧会移走，所以允许进原尾巴格
                var selfBody = player.Snake.Body;
                if (selfBody.Count > 2)
                {
                    for (var i = 0; i < selfBody.Count - 1; i++)
                    {
                        if (selfBody[i] == next)
                        {
                            Kill(player, "撞到自己");
                            break;
                        }
                    }
                    if (!player.Snake.IsAlive) continue;
                }

            }

            MoveSnake(player, next, eatenThisTick.Contains(next));
        }

        // ④ 重生计时 / 存活统计
        foreach (var player in _players)
        {
            if (player.Snake.IsAlive)
            {
                player.Stats.AliveSteps++;
                if (player.Snake.IsGhost)
                {
                    player.Snake.GhostSteps--;
                }
            }
            else if (player.RespawnIn > 0)
            {
                player.RespawnIn--;
                if (player.RespawnIn == 0) Respawn(player);
            }
        }

        // ⑤ 断开 combo：这一帧没吃到果子就清零（共享果玩法可配置是否保留）
        if (Settings.Mode == SnakeMode.BattleRoyale || Settings.ComboBreaksOnMiss)
        {
            foreach (var player in _players)
            {
                if (!player.CanAct) continue;
                if (!eatenThisTick.Any(p => player.Snake.Head == p)) player.Stats.Combo = 0;
            }
        }

        CheckGameOver();
    }

    /// <summary>把蛇往前挪一格。</summary>
    private void MoveSnake(SnakePlayer player, SnakePoint next, bool ate)
    {
        var body = player.Snake.Body;
        body.Insert(0, next);
        if (!ate) body.RemoveAt(body.Count - 1);

        // 虚化期间记录走过的格子（实化时逐节变实体）
        if (player.Snake.IsGhost)
        {
            player.Snake.GhostTrail.Add(next);
            // 虚化结束：把走过的痕迹清掉（身体已经实化）
            if (player.Snake.GhostSteps <= 0) player.Snake.GhostTrail.Clear();
        }
    }

    /// <summary>杀掉一条蛇。</summary>
    private void Kill(SnakePlayer player, string reason)
    {
        if (!player.Snake.IsAlive) return;

        player.Snake.IsAlive = false;
        player.Stats.Deaths++;
        player.Stats.Combo = 0;
        _lastLength[player.Id] = Math.Max(_lastLength.GetValueOrDefault(player.Id, 0), player.Snake.Body.Count);

        // 死亡残影（80% 透明）
        foreach (var segment in player.Snake.Body) _deathMarks.Add(segment);

        player.Lives--;
        if (player.Lives > 0)
        {
            player.RespawnIn = RespawnSteps;
        }
        else
        {
            player.RespawnIn = 0;
        }

        _ = reason;
    }

    /// <summary>
    /// 重生：<b>回到自己的固定出生点</b>，朝<b>空场方向</b>出去；出生瞬间原地「光爆」一下 ——
    /// 炸死出生点范围内的其它蛇（自己免疫），然后光爆消失、该点恢复成可通行的空地。
    /// </summary>
    private void Respawn(SnakePlayer player)
    {
        if (player.Lives <= 0) return;

        // ① 固定出生点（开局时分配的），找不到才退回随机
        var spawn = player.SpawnPoint ?? new SnakePoint(Width / 2, Height / 2);
        if (!InBounds(spawn) || IsOccupiedByOther(player, spawn))
            spawn = FindSpawnForRespawn(player);

        // ② 朝「空场方向」出去：选一个前方是空地的方向
        var direction = ChooseOpenDirection(spawn, player);

        // ③ 出生光爆（可选）：临时标记，炸完立刻消失，不是常驻障碍
        if (Settings.SpawnBombEnabled) SpawnBurst(spawn, player);

        var keepLength = Settings.RespawnKeepsLength ? Math.Max(Settings.InitialLength, _lastLength[player.Id]) : Settings.InitialLength;
        FillSnake(player, spawn.X, spawn.Y, direction, keepLength);

        player.Snake.IsAlive = true;
        player.Snake.GhostSteps = GhostStepsFor(Settings.RespawnInvincibleSeconds);
        player.RespawnIn = 0;

        // 提示文字由界面 / VM 组装（引擎只管状态）
    }

    /// <summary>无敌秒数 → 步数。</summary>
    private static int GhostStepsFor(double seconds)
        => Math.Max(0, (int)Math.Round(seconds * 8));      // 8 格/秒为基准

    /// <summary>这条蛇上次的长度（重生时保留长度用）。</summary>
    private readonly Dictionary<string, int> _lastLength = new(StringComparer.Ordinal);

    /// <summary>
    /// 出生光爆：炸掉出生点周围 3×3 里<b>其它</b>的活蛇（自己免疫），
    /// 然后立刻清掉标记 —— <b>不留下常驻障碍</b>，之后就能穿过。
    /// </summary>
    private void SpawnBurst(SnakePoint center, SnakePlayer owner)
    {
        var zone = new List<SnakePoint>();
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var point = new SnakePoint(center.X + dx, center.Y + dy);
                if (InBounds(point)) zone.Add(point);
            }
        }

        // 一秒的视觉残留（只用于渲染，不参与碰撞）
        _burstMarks[owner.Id] = (zone, Environment.TickCount64 + 600);

        foreach (var other in _players)
        {
            if (ReferenceEquals(other, owner) || !other.Snake.IsAlive) continue;
            if (!zone.Any(p => other.Snake.Occupies(p))) continue;

            Kill(other, "被出生光爆命中");
        }

        // 光爆不写进 _blockedSpawns，也不进 _deathMarks 的碰撞判定 → 之后可以穿过
    }

    /// <summary>光爆的视觉标记（玩家 id → 格子 + 到期时刻）。</summary>
    private readonly Dictionary<string, (List<SnakePoint> Zone, long ExpireAt)> _burstMarks = new(StringComparer.Ordinal);

    /// <summary>当前还没消失的光爆格子（渲染用）。</summary>
    public IReadOnlyList<SnakePoint> ActiveBursts()
    {
        var now = Environment.TickCount64;
        var result = new List<SnakePoint>();
        foreach (var (_, (zone, expireAt)) in _burstMarks)
        {
            if (now > expireAt) continue;
            result.AddRange(zone);
        }
        return result;
    }

    /// <summary>这一点是不是被别的蛇占着。</summary>
    private bool IsOccupiedByOther(SnakePlayer owner, SnakePoint point)
        => _players.Any(p => !ReferenceEquals(p, owner) && p.Snake.IsAlive && p.Snake.Occupies(point));

    /// <summary>选一个「前方是空地」的方向，让蛇能从出生点出去。</summary>
    private SnakeDirection ChooseOpenDirection(SnakePoint spawn, SnakePlayer player)
    {
        var order = new[] { SnakeDirection.Up, SnakeDirection.Right, SnakeDirection.Down, SnakeDirection.Left };

        foreach (var direction in order)
        {
            var next = spawn + SnakeDirections.ToVector(direction);
            if (!InBounds(next)) continue;
            if (IsOccupiedByOther(player, next)) continue;
            return direction;
        }
        return SnakeDirection.Right;
    }

    /// <summary>重生时找不到固定点时，找一个附近的空格。</summary>
    private SnakePoint FindSpawnForRespawn(SnakePlayer player)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var point = new SnakePoint(_random.Next(Width), _random.Next(Height));
            if (IsFree(point) && !IsOccupiedByOther(player, point)) return point;
        }
        return new SnakePoint(Width / 2, Height / 2);
    }

    /// <summary>共享果：在自己区域里找一个空位重生。</summary>
    private SnakePoint FindSharedFruitSpawn(SnakePlayer player)
    {
        var index = _players.IndexOf(player);
        var regionWidth = Math.Max(6, Width / Math.Max(1, _players.Count));
        var left = index * regionWidth;
        var right = Math.Min(Width - 1, left + regionWidth - 1);

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var point = new SnakePoint(left + _random.Next(Math.Max(1, right - left + 1)), _random.Next(Height));
            if (IsFree(point)) return point;
        }
        return new SnakePoint((left + right) / 2, Height / 2);
    }

    /// <summary>大混战：全盘找一个空位重生。</summary>
    private SnakePoint FindBattleRespawn(SnakePlayer player)
    {
        _ = player;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var point = new SnakePoint(_random.Next(Width), _random.Next(Height));
            if (IsFree(point) && !_blockedSpawns.Contains(point)) return point;
        }
        return new SnakePoint(Width / 2, Height / 2);
    }

    /// <summary>这一点是不是空的（没蛇、没果子、没残影、没雷）。</summary>
    public bool IsFree(SnakePoint point)
    {
        if (!InBounds(point)) return false;
        if (_fruits.Contains(point)) return false;
        if (_blockedSpawns.Contains(point)) return false;
        foreach (var player in _players)
        {
            if (player.Snake.IsAlive && player.Snake.Occupies(point)) return false;
        }
        return true;
    }

    /// <summary>
    /// 共享果：把一个「区域坐标」映射到每个区域的真实坐标。
    /// <para>所有区域里果子的相对位置相同，所以只要按区域偏移即可。</para>
    /// </summary>
    public SnakePoint RegionPointToBoard(int regionIndex, SnakePoint point)
    {
        var regionWidth = Math.Max(6, Width / Math.Max(1, _players.Count));
        return new SnakePoint(point.X + regionIndex * regionWidth, point.Y);
    }

    /// <summary>补果：补到设置的上限。</summary>
    private void RefillFruits()
    {
        var max = Settings.Mode == SnakeMode.SharedFruit ? 1 : Math.Max(1, Settings.MaxFruits);
        var guard = 0;
        while (_fruits.Count < max && guard++ < 500)
        {
            var point = new SnakePoint(_random.Next(Width), _random.Next(Height));
            if (_fruits.Contains(point)) continue;
            if (_blockedSpawns.Contains(point) && Settings.SpawnBombEnabled) continue;

            var occupied = _players.Any(p => p.Snake.IsAlive && p.Snake.Occupies(point));
            if (occupied) continue;

            _fruits.Add(point);
        }
    }

    /// <summary>结束判定。</summary>
    private void CheckGameOver()
    {
        if (IsOver) return;

        // 玩法一：时间到、或所有玩家都用完命了
        if (Settings.Mode == SnakeMode.SharedFruit)
        {
            if (Settings.TimeLimitSteps > 0 && Step >= Settings.TimeLimitSteps)
            {
                Finish("时间到");
                return;
            }

            // 还有命没死透就不算结束（只剩一条蛇但还有命要继续打）
            if (_players.All(p => p.Lives <= 0))
            {
                Finish("所有人都阵亡了");
            }
            return;
        }

        // 玩法二：时间到、或<b>所有蛇都用完命</b>才结束
        // （不能「只剩一条蛇就停」—— 玩家要求打完，最后一条自己活到最后）
        if (Settings.TimeLimitSteps > 0 && Step >= Settings.TimeLimitSteps)
        {
            Finish("时间到");
            return;
        }
        if (_players.Count > 0 && _players.All(p => p.Lives <= 0))
        {
            Finish("所有蛇都阵亡了");
        }
    }

    /// <summary>结束并算排名。</summary>
    private void Finish(string reason)
    {
        IsOver = true;
        if (_players.Count == 0) { ResultText = reason; return; }
        var maxFruits = _players.Max(p => p.Stats.Fruits);
        var maxSteps = _players.Max(p => p.Stats.AliveSteps);
        var maxCombo = _players.Max(p => p.Stats.BestCombo);

        Ranking = _players
            .Select(p => (p, p.Stats.Score(maxFruits, maxSteps, maxCombo)))
            .OrderByDescending(x => x.Item2)
            .ToList();

        var top = Ranking.FirstOrDefault();
        ResultText = top.Item1 is null
            ? reason
            : $"{reason}：{top.Item1.Name} 获胜（评分 {top.Item2:0.0}）";
    }

    /// <summary>坐标是否在棋盘内。</summary>
    public bool InBounds(SnakePoint point)
        => point.X >= 0 && point.Y >= 0 && point.X < Width && point.Y < Height;

    /// <summary>每条蛇自己的出生爆炸区（自己免疫）。</summary>
    private readonly Dictionary<string, HashSet<SnakePoint>> _ownBlastZones = new(StringComparer.Ordinal);

    /// <summary>这一点在不在自己的出生爆炸区里。</summary>
    private bool IsInOwnBlastZone(SnakePlayer player, SnakePoint point)
        => _ownBlastZones.TryGetValue(player.Id, out var zone) && zone.Contains(point);

    /// <summary>这一点是不是出生点爆炸区（只在开启时有效）。</summary>
    public bool IsSpawnBlocked(SnakePoint point)
        => Settings.SpawnBombEnabled && _blockedSpawns.Contains(point);

    // ---------------------------------------------------------------- 测试 / 调试专用

    /// <summary>
    /// <b>测试专用</b>：把某条蛇瞬移到指定位置（构造撞墙 / 相撞局面用）。
    /// 正式玩法不要调用。
    /// </summary>
    public void TeleportSnakeForTest(string playerId, SnakePoint head, SnakeDirection direction, int length = 3)
    {
        var player = _players.FirstOrDefault(p => p.Id == playerId);
        if (player is null) return;

        FillSnake(player, head.X, head.Y, direction, length);
        player.Snake.IsAlive = true;
        player.RespawnIn = 0;
    }

    /// <summary><b>测试专用</b>：在指定位置放一颗果子。</summary>
    public void PlaceFruitForTest(SnakePoint point)
    {
        if (!InBounds(point)) return;
        _fruits.Clear();
        _fruits.Add(point);
    }

    /// <summary>结算排名（中途查看用）。</summary>
    public IReadOnlyList<(SnakePlayer Player, double Score)> ComputeRanking()
    {
        // 空局（刚进页面、还没开局）直接返回空表 —— Max 对空集合会抛异常（踩过）
        if (_players.Count == 0) return Array.Empty<(SnakePlayer, double)>();

        var maxFruits = _players.Max(p => p.Stats.Fruits);
        var maxSteps = _players.Max(p => p.Stats.AliveSteps);
        var maxCombo = _players.Max(p => p.Stats.BestCombo);

        return _players
            .Select(p => (p, p.Stats.Score(maxFruits, maxSteps, maxCombo)))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }
}
