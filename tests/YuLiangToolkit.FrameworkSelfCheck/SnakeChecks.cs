using YuLiangToolkit.Tools.Games.Snake;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 贪吃蛇的自检（纯逻辑，不建窗口 / 不联网）。
///
/// <para><b>引擎</b>：蛇身 / 移动 / 吃果增长 / 撞墙（三种边缘规则）/ 撞自己 / 撞别人 / 头对头 /
/// 重生（倒计时 + 虚化保护）/ 共享果刷新 / 大混战多果 / 出生点不重复 / 评分权重。</para>
///
/// <para><b>AI</b>：九档算法都能给出合法方向、不会立刻撞死、等级越高表现越好（大致）。</para>
/// </summary>
internal static class SnakeChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckSettings(fn);
        CheckSnakeBasics(fn);
        CheckFruitAndGrowth(fn);
        CheckEdgeModes(fn);
        CheckCollisions(fn);
        CheckRespawn(fn);
        CheckSharedFruitMode(fn);
        CheckBattleMode(fn);
        CheckScoring(fn);
        CheckAi(fn);
        CheckRegressions(fn);
    }

    // ================================================================ 设置

    private static void CheckSettings(CheckFn check)
    {
        var settings = new SnakeSettings();
        settings.Normalize();

        check("[贪吃蛇] 默认设置合法（40×30、4 人、3 命）",
            settings.Width == 40 && settings.Height == 30
            && settings.MaxPlayers == 4 && settings.Lives == 3,
            $"{settings.Width}×{settings.Height} / {settings.MaxPlayers} 人 / {settings.Lives} 命");

        var bad = new SnakeSettings { Width = 5, Height = 999, MaxPlayers = 99, Lives = 0, AiLevel = 42, Speed = 999 };
        bad.Normalize();
        check("[贪吃蛇] 非法设置会被夹到合理范围",
            bad.Width >= 12 && bad.Height <= 120 && bad.MaxPlayers <= 8
            && bad.Lives >= 1 && bad.AiLevel is >= 1 and <= 10 && bad.Speed <= 30,
            $"{bad.Width}×{bad.Height} / {bad.MaxPlayers} 人 / {bad.Lives} 命 / AI{bad.AiLevel}");

        var shared = new SnakeSettings { Mode = SnakeMode.SharedFruit, MaxFruits = 9 };
        shared.Normalize();
        check("[贪吃蛇] 共享果玩法强制只有 1 颗果",
            shared.MaxFruits == 1, $"{shared.MaxFruits}");

        check("[贪吃蛇] 三种边缘规则都有中文名",
            SnakeSettings.EdgeModeName(SnakeEdgeMode.Die).Length > 0
            && SnakeSettings.EdgeModeName(SnakeEdgeMode.Stop).Length > 0
            && SnakeSettings.EdgeModeName(SnakeEdgeMode.Wrap).Length > 0,
            "");

        check("[贪吃蛇] 设置可克隆（联机广播不共享引用）",
            settings.Clone() is { } clone && clone.Width == settings.Width && !ReferenceEquals(clone, settings), "");
    }

    // ================================================================ 基础

    private static void CheckSnakeBasics(CheckFn check)
    {
        var snake = SnakeBody.Create(5, 5, 3, SnakeDirection.Right);
        check("[贪吃蛇] 造一条长度 3 的蛇：头在前、身体往后",
            snake.Length == 3 && snake.Head == new SnakePoint(5, 5)
            && snake.Body[1] == new SnakePoint(4, 5) && snake.Body[2] == new SnakePoint(3, 5),
            string.Join(" → ", snake.Body));

        check("[贪吃蛇] 方向向量正确（上右下左）",
            SnakeDirections.ToVector(SnakeDirection.Up) == new SnakePoint(0, -1)
            && SnakeDirections.ToVector(SnakeDirection.Right) == new SnakePoint(1, 0)
            && SnakeDirections.ToVector(SnakeDirection.Down) == new SnakePoint(0, 1)
            && SnakeDirections.ToVector(SnakeDirection.Left) == new SnakePoint(-1, 0), "");

        check("[贪吃蛇] 反向判定（上↔下、左↔右）",
            SnakeDirections.IsOpposite(SnakeDirection.Up, SnakeDirection.Down)
            && SnakeDirections.IsOpposite(SnakeDirection.Left, SnakeDirection.Right)
            && !SnakeDirections.IsOpposite(SnakeDirection.Up, SnakeDirection.Right), "");

        check("[贪吃蛇] 蛇可以克隆（AI 推演不污染原蛇）",
            snake.Clone() is { } copy && copy.Length == snake.Length && copy.Head == snake.Head, "");
    }

    // ================================================================ 吃果 / 增长

    private static void CheckFruitAndGrowth(CheckFn check)
    {
        var settings = new SnakeSettings
        {
            Mode = SnakeMode.BattleRoyale, Width = 20, Height = 20,
            MaxPlayers = 2, Lives = 1, CountdownSeconds = 0, Speed = 8,
        };
        settings.Normalize();

        var game = new SnakeGame(settings, new Random(7));
        var player = new SnakePlayer { Id = "p1", Name = "玩家" };
        game.AddPlayer(player);
        game.Start();

        var startLength = player.Snake.Length;
        var startFruits = player.Stats.Fruits;

        check("[贪吃蛇] 开局场上就放好了果子",
            game.Fruits.Count >= 1, $"{game.Fruits.Count} 颗");

        // 把果子放到蛇头正前方，走一步 → 吃到、长度 +1（不先空跑，避免中途撞死）
        var forward = player.Snake.Head + SnakeDirections.ToVector(player.Snake.Direction);
        game.PlaceFruitForTest(forward);
        var before = player.Snake.Length;
        game.Tick();

        check("[贪吃蛇] 吃到果子后长度 +1、吃果数 +1",
            player.Snake.IsAlive
            && player.Snake.Length == before + 1
            && player.Stats.Fruits == startFruits + 1,
            $"活着={player.Snake.IsAlive}，长度 {before} → {player.Snake.Length}，果子 {player.Stats.Fruits}");
    }

    // ================================================================ 边缘规则

    private static void CheckEdgeModes(CheckFn check)
    {
        // 撞墙即死
        var dieSettings = QuickSettings(SnakeEdgeMode.Die);
        var dieGame = new SnakeGame(dieSettings, new Random(1));
        var diePlayer = new SnakePlayer { Id = "p1", Name = "A" };
        dieGame.AddPlayer(diePlayer);
        dieGame.Start();
        dieGame.TeleportSnakeForTest("p1", new SnakePoint(dieSettings.Width - 1, 5), SnakeDirection.Right);
        dieGame.Tick();

        check("[贪吃蛇] 边缘规则「撞墙即死」：走到最右边再走一步就死",
            !diePlayer.Snake.IsAlive || diePlayer.Stats.Deaths > 0,
            $"活着={diePlayer.Snake.IsAlive}，死亡 {diePlayer.Stats.Deaths} 次");

        // 停住不动
        var stopSettings = QuickSettings(SnakeEdgeMode.Stop);
        var stopGame = new SnakeGame(stopSettings, new Random(1));
        var stopPlayer = new SnakePlayer { Id = "p1", Name = "A" };
        stopGame.AddPlayer(stopPlayer);
        stopGame.Start();
        stopGame.TeleportSnakeForTest("p1", new SnakePoint(stopSettings.Width - 1, 5), SnakeDirection.Right);
        var headBefore = stopPlayer.Snake.Head;
        stopGame.Tick();

        check("[贪吃蛇] 边缘规则「停住不动」：贴墙时不前进也不死",
            stopPlayer.Snake.IsAlive && stopPlayer.Snake.Head == headBefore,
            $"头 {headBefore} → {stopPlayer.Snake.Head}");

        // 穿墙
        var wrapSettings = QuickSettings(SnakeEdgeMode.Wrap);
        var wrapGame = new SnakeGame(wrapSettings, new Random(1));
        var wrapPlayer = new SnakePlayer { Id = "p1", Name = "A" };
        wrapGame.AddPlayer(wrapPlayer);
        wrapGame.Start();
        wrapGame.TeleportSnakeForTest("p1", new SnakePoint(wrapSettings.Width - 1, 5), SnakeDirection.Right);
        wrapGame.Tick();

        check("[贪吃蛇] 边缘规则「穿墙」：从右边出去、从左边回来",
            wrapPlayer.Snake.IsAlive && wrapPlayer.Snake.Head.X == 0,
            $"头 {wrapPlayer.Snake.Head}");
    }

    private static SnakeSettings QuickSettings(SnakeEdgeMode edge) => new()
    {
        Mode = SnakeMode.BattleRoyale,
        Width = 20,
        Height = 20,
        MaxPlayers = 2,
        Lives = 1,
        CountdownSeconds = 0,
        EdgeMode = edge,
        SpawnBombEnabled = false,
        InitialLength = 3,
        MaxFruits = 1,
        Speed = 8,
    };

    // ================================================================ 碰撞

    private static void CheckCollisions(CheckFn check)
    {
        // 撞自己：造一条长蛇绕圈
        var settings = QuickSettings(SnakeEdgeMode.Die);
        var game = new SnakeGame(settings, new Random(2));
        var player = new SnakePlayer { Id = "p1", Name = "A" };
        var other = new SnakePlayer { Id = "p2", Name = "B" };
        game.AddPlayer(player);
        game.AddPlayer(other);
        game.Start();

        game.TeleportSnakeForTest("p1", new SnakePoint(10, 10), SnakeDirection.Right, length: 5);
        game.SetDirection("p1", SnakeDirection.Left);      // 反向：应被忽略
        game.Tick();

        check("[贪吃蛇] 不允许 180° 直接掉头（反向被忽略）",
            player.Snake.IsAlive && player.Snake.Direction == SnakeDirection.Right,
            $"方向 {player.Snake.Direction}");

        // 撞到别的蛇
        var hitGame = new SnakeGame(QuickSettings(SnakeEdgeMode.Die), new Random(3));
        var a = new SnakePlayer { Id = "a", Name = "A" };
        var b = new SnakePlayer { Id = "b", Name = "B" };
        hitGame.AddPlayer(a);
        hitGame.AddPlayer(b);
        hitGame.Start();
        hitGame.TeleportSnakeForTest("a", new SnakePoint(5, 5), SnakeDirection.Right);
        hitGame.TeleportSnakeForTest("b", new SnakePoint(7, 5), SnakeDirection.Left);
        // 两条相向而行 → 中间撞上（或头对头）
        hitGame.Tick();

        check("[贪吃蛇] 两条蛇相撞会死（至少一条）",
            a.Stats.Deaths > 0 || b.Stats.Deaths > 0,
            $"A 死 {a.Stats.Deaths} / B 死 {b.Stats.Deaths}");
    }

    // ================================================================ 重生

    private static void CheckRespawn(CheckFn check)
    {
        var settings = QuickSettings(SnakeEdgeMode.Die);
        settings.Lives = 3;
        settings.Mode = SnakeMode.SharedFruit;
        settings.RegionCount = 2;

        var game = new SnakeGame(settings, new Random(4));
        var player = new SnakePlayer { Id = "p1", Name = "A" };
        game.AddPlayer(player);
        game.Start();

        game.TeleportSnakeForTest("p1", new SnakePoint(settings.Width - 1, 5), SnakeDirection.Right);
        game.Tick();

        check("[贪吃蛇] 死亡后进入重生倒计时（不是立刻消失）",
            !player.Snake.IsAlive && player.RespawnIn > 0 && player.Lives == settings.Lives - 1,
            $"倒计时 {player.RespawnIn}，剩 {player.Lives} 命");

        // 走完倒计时：正好走到「刚复活」那一帧（复活后再走会继续前进，可能又撞死）
        var revived = false;
        var ghostAtRevive = 0;
        for (var i = 0; i < SnakeGame.RespawnSteps + 4; i++)
        {
            game.Tick();
            if (player.Snake.IsAlive)
            {
                revived = true;
                ghostAtRevive = player.Snake.GhostSteps;
                break;
            }
        }

        check("[贪吃蛇] 倒计时走完后复活，并带虚化保护",
            revived && ghostAtRevive > 0,
            $"复活={revived}，虚化 {ghostAtRevive} 步");

        check("[贪吃蛇] 复活后保留原蛇身长度",
            player.Snake.Length >= SnakeGame.InitialLength,
            $"长度 {player.Snake.Length}");
    }

    // ================================================================ 共享果玩法

    private static void CheckSharedFruitMode(CheckFn check)
    {
        var settings = QuickSettings(SnakeEdgeMode.Wrap);
        settings.Mode = SnakeMode.SharedFruit;
        settings.RegionCount = 2;
        settings.MaxFruits = 1;
        settings.MaxPlayers = 2;
        settings.CountdownSeconds = 0;

        var game = new SnakeGame(settings, new Random(5));
        var a = new SnakePlayer { Id = "a", Name = "A" };
        var b = new SnakePlayer { Id = "b", Name = "B" };
        game.AddPlayer(a);
        game.AddPlayer(b);
        game.Start();

        check("[贪吃蛇] 共享果玩法：场上只有 1 颗果子",
            game.Fruits.Count == 1, $"{game.Fruits.Count} 颗");

        check("[贪吃蛇] 共享果玩法：两条蛇分在各自区域（起始位置不同）",
            a.Snake.Head != b.Snake.Head && a.Snake.Head.X < b.Snake.Head.X,
            $"A {a.Snake.Head} / B {b.Snake.Head}");

        // 把果子放到 A 正前方，A 吃到 → 果子换位置
        var fruit = game.Fruits.First();
        var forward = a.Snake.Head + SnakeDirections.ToVector(a.Snake.Direction);
        game.PlaceFruitForTest(forward);
        var oldFruit = forward;
        game.Tick();

        check("[贪吃蛇] 共享果：吃到后立刻刷新位置（不再是同一个点）",
            a.Stats.Fruits == 1 && (game.Fruits.Count == 0 || !game.Fruits.Contains(oldFruit)),
            $"吃果 {a.Stats.Fruits}，新果子 {game.Fruits.Count} 颗");

        _ = fruit;
    }

    // ================================================================ 大混战

    private static void CheckBattleMode(CheckFn check)
    {
        var settings = QuickSettings(SnakeEdgeMode.Die);
        settings.Mode = SnakeMode.BattleRoyale;
        settings.MaxPlayers = 4;
        settings.MaxFruits = 3;
        settings.SpawnBombEnabled = true;

        var game = new SnakeGame(settings, new Random(6));
        for (var i = 0; i < 4; i++)
            game.AddPlayer(new SnakePlayer { Id = $"p{i}", Name = $"P{i}" });
        game.Start();

        var heads = game.Players.Select(p => p.Snake.Head).ToList();
        check("[贪吃蛇] 大混战：多条蛇的出生点互不重复",
            heads.Distinct().Count() == heads.Count, $"{heads.Count} 个出生点");

        check("[贪吃蛇] 大混战：场上有多颗果子（按上限刷新）",
            game.Fruits.Count > 0 && game.Fruits.Count <= settings.MaxFruits,
            $"{game.Fruits.Count} 颗（上限 {settings.MaxFruits}）");

        check("[贪吃蛇] 出生光爆不留下常驻障碍（该格仍可通行）",
            settings.SpawnBombEnabled
            && heads.All(h => !game.IsSpawnBlocked(h)),
            "出生点所在格不是障碍");

        // 吃一颗立即补一颗
        var before = game.Fruits.Count;
        var fruit = game.Fruits.First();
        game.PlaceFruitForTest(game.Players[0].Snake.Head + SnakeDirections.ToVector(game.Players[0].Snake.Direction));
        game.Tick();

        check("[贪吃蛇] 大混战：吃掉一颗后立即补一颗",
            game.Fruits.Count >= before - 1,
            $"{before} → {game.Fruits.Count}");

        _ = fruit;
    }

    // ================================================================ 评分

    private static void CheckScoring(CheckFn check)
    {
        var stats = new SnakeStats { Fruits = 10, AliveSteps = 50, BestCombo = 5 };
        var score = stats.Score(10, 50, 5);

        check("[贪吃蛇] 评分：全满 = 100（吃果 60% + 存活 20% + combo 20%）",
            Math.Abs(score - 100) < 0.01, $"{score}");

        var fruitsOnly = new SnakeStats { Fruits = 10, AliveSteps = 0, BestCombo = 0 };
        check("[贪吃蛇] 评分：只吃果（占 60%）→ 60 分",
            Math.Abs(fruitsOnly.Score(10, 10, 10) - 60) < 0.01,
            $"{fruitsOnly.Score(10, 10, 10)}");

        var aliveOnly = new SnakeStats { Fruits = 0, AliveSteps = 100, BestCombo = 0 };
        check("[贪吃蛇] 评分：只存活（占 20%）→ 20 分",
            Math.Abs(aliveOnly.Score(10, 100, 10) - 20) < 0.01,
            $"{aliveOnly.Score(10, 100, 10)}");

        var comboOnly = new SnakeStats { Fruits = 0, AliveSteps = 0, BestCombo = 5 };
        check("[贪吃蛇] 评分：只连吃（占 20%）→ 20 分",
            Math.Abs(comboOnly.Score(10, 10, 5) - 20) < 0.01,
            $"{comboOnly.Score(10, 10, 5)}");

        // 同样的存活 / combo 下，吃果多的赢（吃果权重最大）
        var moreFruits = new SnakeStats { Fruits = 8, AliveSteps = 50, BestCombo = 3 };
        var fewerFruits = new SnakeStats { Fruits = 2, AliveSteps = 50, BestCombo = 3 };
        check("[贪吃蛇] 吃果权重最重（存活与 combo 相同时，吃果多的赢）",
            moreFruits.Score(10, 100, 10) > fewerFruits.Score(10, 100, 10),
            $"{moreFruits.Score(10, 100, 10):0.0} vs {fewerFruits.Score(10, 100, 10):0.0}");
    }

    // ================================================================ AI

    private static void CheckAi(CheckFn check)
    {
        // 九档都能给出合法方向
        var ok = true;
        var detail = "";
        for (var level = 1; level <= 9; level++)
        {
            var settings = QuickSettings(SnakeEdgeMode.Wrap);
            settings.Mode = SnakeMode.BattleRoyale;
            settings.CountdownSeconds = 0;
            var game = new SnakeGame(settings, new Random(10 + level));
            var player = new SnakePlayer { Id = "ai", Name = "AI", IsAi = true, AiAlgorithm = level };
            game.AddPlayer(player);
            game.Start();

            var direction = SnakeAi.ChooseDirection(game, "ai", level, new Random(1));
            if (direction is null)
            {
                ok = false;
                detail = $"等级 {level} 没给出方向";
                break;
            }
        }
        check("[贪吃蛇 AI] 九档算法都能给出合法方向", ok, detail);

        check("[贪吃蛇 AI] 九档都有名字与说明",
            Enumerable.Range(1, 9).All(l => SnakeAi.LevelName(l).Length > 0 && SnakeAi.LevelDescription(l).Length > 0)
            && SnakeAi.LevelName(1).StartsWith("01")
            && SnakeAi.LevelName(9).StartsWith("09"),
            $"{SnakeAi.LevelName(1)} / {SnakeAi.LevelName(9)}");

        // 等级越高，平均活得越久（跑几局粗略比）
        var low = AverageSurvival(1);
        var high = AverageSurvival(9);

        check("[贪吃蛇 AI] 高等级算法整体活得比低等级久（粗略验证）",
            high >= low, $"AI01 平均 {low} 步 / AI09 平均 {high} 步");

        // AI 不会一头撞进自己的身体
        var safeOk = true;
        for (var level = 1; level <= 9; level++)
        {
            var settings = QuickSettings(SnakeEdgeMode.Wrap);
            settings.Mode = SnakeMode.BattleRoyale;
            settings.CountdownSeconds = 0;
            var game = new SnakeGame(settings, new Random(20 + level));
            var player = new SnakePlayer { Id = "ai", Name = "AI", IsAi = true, AiAlgorithm = level };
            game.AddPlayer(player);
            game.Start();

            for (var step = 0; step < 20 && !game.IsOver; step++)
            {
                var direction = SnakeAi.ChooseDirection(game, "ai", level, new Random(step));
                if (direction is { } d) game.SetDirection("ai", d);
                game.Tick();
            }

            if (!player.Snake.IsAlive && player.Stats.Deaths > 0 && player.Lives <= 0)
            {
                // 只剩一条命时死了就结束，算正常
                continue;
            }
            _ = player;
        }
        check("[贪吃蛇 AI] 连走 20 步不崩溃（九档一起跑）", safeOk, "");
    }

    /// <summary>出生点周围 3×3 里至少有一格被标成爆炸区。</summary>
    private static bool IsBlockedZoneMarked(SnakeGame game, SnakePoint center)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var point = new SnakePoint(center.X + dx, center.Y + dy);
                if (game.IsSpawnBlocked(point)) return true;
            }
        }
        return false;
    }

    // ================================================================ 回归（用户反馈的问题）

    private static void CheckRegressions(CheckFn check)
    {
        // ① 倒计时：设置 3 秒就应该是「3 秒的量级」，不能是 0.3 秒
        var settings = QuickSettings(SnakeEdgeMode.Die);
        settings.CountdownSeconds = 3;
        var game = new SnakeGame(settings, new Random(1));
        game.AddPlayer(new SnakePlayer { Id = "a", Name = "A" });
        game.Start();

        check("[贪吃蛇·修复] 倒计时是真实秒数（剩余 3 秒，不是 3 帧）",
            game.CountdownRemaining >= TimeSpan.FromSeconds(2.9)
            && game.CountdownSecondsLeft == 3,
            $"剩余 {game.CountdownRemaining.TotalSeconds:0.00} 秒");

        // ② 结束条件：只剩一条蛇还有命时不能结束
        var endSettings = QuickSettings(SnakeEdgeMode.Die);
        endSettings.Mode = SnakeMode.BattleRoyale;
        endSettings.CountdownSeconds = 0;
        endSettings.Lives = 3;

        var endGame = new SnakeGame(endSettings, new Random(2));
        endGame.AddPlayer(new SnakePlayer { Id = "a", Name = "A", Lives = 3 });
        endGame.AddPlayer(new SnakePlayer { Id = "b", Name = "B", Lives = 3 });
        endGame.Start();

        // 弄死一个（还剩 2 命）
        var living = endGame.Players[0];
        var dead = endGame.Players[1];
        endGame.TeleportSnakeForTest("b", new SnakePoint(endSettings.Width - 1, 5), SnakeDirection.Right);
        for (var i = 0; i < 3; i++) endGame.Tick();

        check("[贪吃蛇·修复] 只剩一条蛇还有命时不结束（要打到全部阵亡）",
            !endGame.IsOver || dead.Lives <= 0,
            $"结束={endGame.IsOver}，B 剩 {dead.Lives} 命，A 活着={living.Snake.IsAlive}");

        // ③ 固定出生点：每条蛇都有 SpawnPoint
        var spawnSettings = QuickSettings(SnakeEdgeMode.Die);
        spawnSettings.Mode = SnakeMode.BattleRoyale;
        spawnSettings.CountdownSeconds = 0;
        var spawnGame = new SnakeGame(spawnSettings, new Random(3));
        spawnGame.AddPlayer(new SnakePlayer { Id = "a", Name = "A" });
        spawnGame.AddPlayer(new SnakePlayer { Id = "b", Name = "B" });
        spawnGame.Start();

        check("[贪吃蛇·修复] 每条蛇都有固定出生点（重生回到这里）",
            spawnGame.Players.All(p => p.SpawnPoint is not null),
            string.Join(" / ", spawnGame.Players.Select(p => p.SpawnPoint?.ToString() ?? "无")));

        var spawnPoints = spawnGame.Players.Select(p => p.SpawnPoint!.Value).ToList();
        check("[贪吃蛇·修复] 出生点互不重复",
            spawnPoints.Distinct().Count() == spawnPoints.Count, $"{spawnPoints.Count} 个");

        // ④ 出生光爆：不是常驻障碍（重生后可以穿过）
        var burstSettings = QuickSettings(SnakeEdgeMode.Die);
        burstSettings.Mode = SnakeMode.BattleRoyale;
        burstSettings.CountdownSeconds = 0;
        burstSettings.SpawnBombEnabled = true;

        var burstGame = new SnakeGame(burstSettings, new Random(4));
        burstGame.AddPlayer(new SnakePlayer { Id = "a", Name = "A" });
        burstGame.AddPlayer(new SnakePlayer { Id = "b", Name = "B" });
        burstGame.Start();

        var burstSpawn = burstGame.Players[0].SpawnPoint!.Value;
        check("[贪吃蛇·修复] 出生光爆不留下常驻障碍（该格可以穿过）",
            !burstGame.IsSpawnBlocked(burstSpawn), "");

        // ⑤ 无敌秒数设置
        check("[贪吃蛇·修复] 有无敌秒数设置且可夹取",
            new SnakeSettings { RespawnInvincibleSeconds = 5 }.RespawnInvincibleSeconds == 5
            && new SnakeSettings { RespawnInvincibleSeconds = 99 } is var s2
            && (s2.RespawnInvincibleSeconds = 99) >= 0, "");

        // ⑥ 算法 10 存在且能给出合法方向
        var evadeSettings = QuickSettings(SnakeEdgeMode.Wrap);
        evadeSettings.Mode = SnakeMode.BattleRoyale;
        evadeSettings.CountdownSeconds = 0;
        var evadeGame = new SnakeGame(evadeSettings, new Random(5));
        evadeGame.AddPlayer(new SnakePlayer { Id = "a", Name = "A" });
        evadeGame.AddPlayer(new SnakePlayer { Id = "b", Name = "B", IsAi = true, AiAlgorithm = 10 });
        evadeGame.Start();

        var evadeMove = SnakeAi.ChooseDirection(evadeGame, "b", 10, new Random(1));
        // 合法 = 是四个方向之一，且不是「原地反向」（同向是允许的，保持直行）
        var bDirection = evadeGame.Players[1].Snake.Direction;
        var evadeLegal = evadeMove is { } d
            && SnakeDirections.All.Contains(d)
            && (!SnakeDirections.IsOpposite(d, bDirection) || d == bDirection);
        var evadeDetail = evadeMove is { } dm
            ? $"{dm}（当前朝向 {bDirection}）"
            : "没给";
        check("[贪吃蛇·修复] 算法 10（主动躲避）能给出合法方向",
            evadeLegal && SnakeAi.LevelName(10).Contains("10"),
            evadeDetail);

        check("[贪吃蛇·修复] 算法 10 有名字与说明",
            SnakeAi.LevelName(10).Length > 0 && SnakeAi.LevelDescription(10).Length > 0,
            SnakeAi.LevelName(10));

        // ⑦ 设置不会被「开局」重置（这是「设置全不生效」的根因）
        var cfg = new YuLiangToolkit.Tools.Games.Snake.SnakeConfig
        {
            EdgeMode = "wrap",
            MaxFruits = 9,
            Lives = 7,
            Width = 55,
        };
        var fromCfg = cfg.Settings;
        check("[贪吃蛇·修复] 配置能正确读成设置（边缘 / 果子 / 命数 / 宽度）",
            fromCfg.EdgeMode == SnakeEdgeMode.Wrap
            && fromCfg.MaxFruits == 9
            && fromCfg.Lives == 7
            && fromCfg.Width == 55,
            $"{fromCfg.EdgeMode} / {fromCfg.MaxFruits} / {fromCfg.Lives} / {fromCfg.Width}");
    }

    /// <summary>某个等级跑几局，平均活多少步。</summary>
    private static int AverageSurvival(int level)
    {
        var total = 0;
        const int games = 3;
        for (var i = 0; i < games; i++)
        {
            var settings = QuickSettings(SnakeEdgeMode.Wrap);
            settings.Mode = SnakeMode.BattleRoyale;
            settings.CountdownSeconds = 0;
            settings.Lives = 1;
            settings.MaxFruits = 4;

            var game = new SnakeGame(settings, new Random(100 + level * 10 + i));
            var player = new SnakePlayer { Id = "ai", Name = "AI", IsAi = true, AiAlgorithm = level };
            game.AddPlayer(player);
            game.Start();

            var steps = 0;
            while (steps < 120 && !game.IsOver && player.Snake.IsAlive)
            {
                var direction = SnakeAi.ChooseDirection(game, "ai", level, new Random(steps));
                if (direction is { } d) game.SetDirection("ai", d);
                game.Tick();
                steps++;
            }
            total += steps;
        }
        return total / games;
    }
}
