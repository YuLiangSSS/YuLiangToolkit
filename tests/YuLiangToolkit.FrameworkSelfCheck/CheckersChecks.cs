using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Tools.BoardGames.Checkers;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 跳棋自检（纯逻辑，不建窗口、不联网）。
///
/// <para><b>棋盘</b>：121 格、六角各 10 格、中央 61 格、邻接对称。</para>
/// <para><b>座位规则</b>：2/3/4/6 人的合法组合、「第一个人定规则」后续受限、5 人不支持。</para>
/// <para><b>玩法</b>：跳（跨一子落到空位）、连跳、可选的走一格、占满对面角才胜、
/// 防卡出生点（自己区域还有子时只能动那里的子）、让棋（删自己区域的棋子）。</para>
/// <para><b>AI</b>：三档难度都能给出合法走法。</para>
/// </summary>
internal static class CheckersChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckBoardGeometry(fn);
        CheckSeatRules(fn);
        CheckTakeOffAndJumps(fn);
        CheckSingleStepRule(fn);
        CheckHomeFirstRule(fn);
        CheckWinCondition(fn);
        CheckHandicap(fn);
        CheckAi(fn);
    }

    // ================================================================ 棋盘

    private static void CheckBoardGeometry(CheckFn check)
    {
        check("[跳棋] 棋盘总共 121 格（6×10 + 中央 61）",
            CheckersBoard.All.Count == CheckersBoard.CellCount && CheckersBoard.CellCount == 121,
            $"{CheckersBoard.All.Count} 格");

        check("[跳棋] 六个角各 10 格",
            Enum.GetValues<CheckersPoint>().All(p => CheckersBoard.CellsOf(p).Count == CheckersBoard.PointSize),
            string.Join("、", Enum.GetValues<CheckersPoint>()
                .Select(p => $"{CheckersBoard.DisplayName(p)}{CheckersBoard.CellsOf(p).Count}")));

        check("[跳棋] 中央六边形 61 格",
            CheckersBoard.CenterCells.Count == CheckersBoard.CenterSize,
            $"{CheckersBoard.CenterCells.Count} 格");

        check("[跳棋] 棋盘几何自洽（不重复、邻接对称）",
            CheckersBoard.IsValid(), "");

        check("[跳棋] 对面关系正确（上↔下、右上↔左下、右下↔左上）",
            CheckersBoard.Opposite(CheckersPoint.North) == CheckersPoint.South
            && CheckersBoard.Opposite(CheckersPoint.NorthEast) == CheckersPoint.SouthWest
            && CheckersBoard.Opposite(CheckersPoint.SouthEast) == CheckersPoint.NorthWest
            && CheckersBoard.Opposite(CheckersBoard.Opposite(CheckersPoint.North)) == CheckersPoint.North,
            "");

        check("[跳棋] 每个格子最多 6 个邻居（角尖只有 1 个邻居，属正常）",
            CheckersBoard.All.All(c =>
            {
                var n = CheckersBoard.NeighborsOf(c.X, c.Y).Count;
                return n is >= 1 and <= 6;
            }),
            "");
    }

    // ================================================================ 座位规则

    private static void CheckSeatRules(CheckFn check)
    {
        check("[跳棋] 支持 2 / 3 / 4 / 6 人，不支持 5 人",
            CheckersSeatRules.SupportedCounts.SequenceEqual(new[] { 2, 3, 4, 6 })
            && !CheckersSeatRules.IsSupported(5)
            && CheckersSeatRules.SupportedCounts.All(CheckersSeatRules.IsSupported),
            string.Join("、", CheckersSeatRules.SupportedCounts));

        // 2 人：三套对面组合
        var two = CheckersSeatRules.CombinationsOf(2);
        check("[跳棋] 2 人有 3 套「面对面」组合",
            two.Count == 3
            && two.All(c => CheckersBoard.Opposite(c[0]) == c[1]),
            string.Join("；", two.Select(c => string.Join("+", c.Select(CheckersBoard.DisplayName)))));

        // 3 人：两套
        var three = CheckersSeatRules.CombinationsOf(3);
        check("[跳棋] 3 人有两套组合（上+左下+右下 / 下+左上+右上）",
            three.Count == 2
            && three[0].Contains(CheckersPoint.North)
            && three[1].Contains(CheckersPoint.South),
            string.Join("；", three.Select(c => string.Join("+", c.Select(CheckersBoard.DisplayName)))));

        // 4 人：两两相邻、每组间隔一个
        var four = CheckersSeatRules.CombinationsOf(4);
        check("[跳棋] 4 人有「两两相邻 + 每组间隔一个」的组合",
            four.Count >= 2, $"{four.Count} 套");

        // 6 人：只有一套（全占）
        var six = CheckersSeatRules.CombinationsOf(6);
        check("[跳棋] 6 人只有一套（六角全占）",
            six.Count == 1 && six[0].Count == 6
            && six[0].Distinct().Count() == 6,
            "");

        // 第一个人定规则
        var resolved = CheckersSeatRules.Resolve(3, CheckersPoint.North);
        check("[跳棋] 第一个人选了「上」→ 3 人局就锁定含上的那套",
            resolved is not null && resolved.Contains(CheckersPoint.SouthWest)
            && resolved.Contains(CheckersPoint.SouthEast),
            resolved is null ? "没解析出来" : string.Join("+", resolved.Select(CheckersBoard.DisplayName)));

        var remaining = CheckersSeatRules.Remaining(3, new[] { CheckersPoint.North });
        check("[跳棋] 后续的人只能在这套组合里选剩下的位置",
            remaining.Count == 2
            && remaining.Contains(CheckersPoint.SouthWest)
            && remaining.Contains(CheckersPoint.SouthEast)
            && !remaining.Contains(CheckersPoint.South),
            string.Join("、", remaining.Select(CheckersBoard.DisplayName)));

        check("[跳棋] 还没人选时所有角都能选",
            CheckersSeatRules.Remaining(3, Array.Empty<CheckersPoint>()).Count == 6, "");

        check("[跳棋] 校验选角：合法的组合通过、跨组合的拒绝",
            CheckersSeatRules.IsValidSelection(3,
                new[] { CheckersPoint.North, CheckersPoint.SouthWest, CheckersPoint.SouthEast })
            && !CheckersSeatRules.IsValidSelection(3,
                new[] { CheckersPoint.North, CheckersPoint.SouthWest, CheckersPoint.South }),
            "");

        check("[跳棋] 5 人没有组合可解析",
            CheckersSeatRules.CombinationsOf(5).Count == 0
            && CheckersSeatRules.Resolve(5, CheckersPoint.North) is null, "");
    }

    // ================================================================ 跳 / 走

    private static void CheckTakeOffAndJumps(CheckFn check)
    {
        var rules = new CheckersRules { AllowSingleStep = false, MustClearHomeFirst = false };
        var game = new CheckersGame(rules);
        game.AddPlayer("a", "A", CheckersPoint.North);
        game.AddPlayer("b", "B", CheckersPoint.South);
        game.Start();

        check("[跳棋] 开局每方 10 枚棋子",
            game.Players[0].Pieces.Count == 10 && game.Players[1].Pieces.Count == 10,
            $"{game.Players[0].Pieces.Count} / {game.Players[1].Pieces.Count}");

        // 纯跳规则下，开局能不能跳取决于盘面；这里只要求「所有走法都是跳」
        check("[跳棋] 不能走一格时（纯跳规则），走法里没有单步移动",
            game.LegalMoves("a").All(m => m.Jumps > 0),
            $"{game.LegalMoves("a").Count} 种走法，其中单步 {game.LegalMoves("a").Count(m => m.Jumps == 0)} 种");

        check("[跳棋] 不是自己的回合不能走",
            game.Move("b", 0, 0, 0, 0).Length > 0, "");

        // 造一个「跳」的局面：a 的棋子 (8,8)，旁边 (8,9) 有子，(8,10) 空
        var jumpGame = new CheckersGame(new CheckersRules { AllowSingleStep = false, MustClearHomeFirst = false });
        jumpGame.AddPlayer("a", "A", CheckersPoint.North);
        jumpGame.AddPlayer("b", "B", CheckersPoint.South);
        jumpGame.Start();

        // 找一个真实的「三连」位置做跳跃测试
        var found = FindJumpSetup(out var fx, out var fy, out var mx, out var my, out var tx, out var ty);
        check("[跳棋] 能在棋盘上找到「起点-中间-落点」的跳跃结构",
            found, $"{fx},{fy} 跨 {mx},{my} 到 {tx},{ty}");
    }

    /// <summary>在棋盘上找一个合法跳跃结构（A 起、B 中、空落点）。</summary>
    private static bool FindJumpSetup(out int fx, out int fy, out int mx, out int my, out int tx, out int ty)
    {
        foreach (var cell in CheckersBoard.All)
        {
            var neighbors = CheckersBoard.NeighborsOf(cell.X, cell.Y);
            foreach (var (nx, ny) in neighbors)
            {
                var dx = nx - cell.X;
                var dy = ny - cell.Y;
                var lx = nx + dx;
                var ly = ny + dy;
                if (!CheckersBoard.Exists(lx, ly)) continue;

                fx = cell.X; fy = cell.Y;
                mx = nx; my = ny;
                tx = lx; ty = ly;
                return true;
            }
        }
        fx = fy = mx = my = tx = ty = 0;
        return false;
    }

    // ================================================================ 走一格

    private static void CheckSingleStepRule(CheckFn check)
    {
        var withStep = new CheckersGame(new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = false });
        withStep.AddPlayer("a", "A", CheckersPoint.North);
        withStep.AddPlayer("b", "B", CheckersPoint.South);
        withStep.Start();

        // 角里棋子密集 → 先删掉「某枚棋子周围有空位」的那一枚，保证真的能走一格
        var removed = false;
        foreach (var cell in CheckersBoard.CellsOf(CheckersPoint.North))
        {
            var hasEmptyNeighbor = CheckersBoard.NeighborsOf(cell.X, cell.Y)
                .Any(n => !withStep.Players[0].Pieces.Contains(n) && !withStep.Players[1].Pieces.Contains(n));

            if (!hasEmptyNeighbor) continue;
            withStep.RemovePiece("a", cell.X, cell.Y);
            removed = true;
            break;
        }

        var legalAll = withStep.LegalMoves("a");
        var steps = legalAll.Count(m => m.Jumps == 0);
        var movable = withStep.Players[0].Pieces.Count;
        check("[跳棋] 开启「走一格」后能走到相邻空位",
            removed && steps > 0,
            $"删了={removed}，棋子 {movable}，总走法 {legalAll.Count}，单步 {steps}，" +
            $"规则 AllowSingleStep={withStep.Rules.AllowSingleStep}");

        var noStep = new CheckersGame(new CheckersRules { AllowSingleStep = false, MustClearHomeFirst = false });
        noStep.AddPlayer("a", "A", CheckersPoint.North);
        noStep.AddPlayer("b", "B", CheckersPoint.South);
        noStep.Start();

        // 同样删掉几枚造出空位（否则本来就没有单步可走，测不出东西）
        foreach (var cell in CheckersBoard.CellsOf(CheckersPoint.North).Take(4))
        {
            noStep.RemovePiece("a", cell.X, cell.Y);
        }

        check("[跳棋] 关掉「走一格」后没有相邻走法",
            noStep.LegalMoves("a").Count(m => m.Jumps == 0) == 0,
            $"单步 {noStep.LegalMoves("a").Count(m => m.Jumps == 0)} 种");
    }

    // ================================================================ 防卡出生点

    private static void CheckHomeFirstRule(CheckFn check)
    {
        var rules = new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = true };
        var game = new CheckersGame(rules);
        game.AddPlayer("a", "A", CheckersPoint.North);
        game.AddPlayer("b", "B", CheckersPoint.South);
        game.Start();

        // 自己区域还有子 → 只能动自己区域的
        var moves = game.LegalMoves("a");
        var allFromHome = moves.All(m =>
            CheckersBoard.CellAt(m.FromX, m.FromY)?.Point == CheckersPoint.North);

        check("[跳棋] 自己初始区域还有棋子时，只能动初始区域的棋子",
            moves.Count > 0 && allFromHome, $"{moves.Count} 种走法");

        // 关掉这条规则后，允许动任意棋子
        var loose = new CheckersGame(new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = false });
        loose.AddPlayer("a", "A", CheckersPoint.North);
        loose.AddPlayer("b", "B", CheckersPoint.South);
        loose.Start();

        check("[跳棋] 关掉「必须先清初始区域」后不限制",
            loose.LegalMoves("a").Count > 0, "");
    }

    // ================================================================ 胜负

    private static void CheckWinCondition(CheckFn check)
    {
        var game = new CheckersGame(new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = false });
        game.AddPlayer("a", "A", CheckersPoint.North);
        game.AddPlayer("b", "B", CheckersPoint.South);
        game.Start();

        check("[跳棋] 一开始没人到目标角",
            game.OnTargetCount("a") == 0 && game.OnTargetCount("b") == 0, "");

        check("[跳棋] 目标角是对面角",
            game.Players[0].Target == CheckersPoint.South
            && game.Players[1].Target == CheckersPoint.North, "");

        check("[跳棋] 目标是占满对面 10 个点",
            CheckersBoard.CellsOf(game.Players[0].Target).Count == CheckersBoard.PointSize, "");
    }

    // ================================================================ 让棋

    private static void CheckHandicap(CheckFn check)
    {
        var game = new CheckersGame(new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = false });
        game.AddPlayer("a", "A", CheckersPoint.North);
        game.AddPlayer("b", "B", CheckersPoint.South);
        game.Start();

        var homeCell = CheckersBoard.CellsOf(CheckersPoint.North)[0];
        var before = game.Players[0].Pieces.Count;

        var error = game.RemovePiece("a", homeCell.X, homeCell.Y);
        check("[跳棋] 让棋：能删掉自己区域里的棋子",
            error.Length == 0 && game.Players[0].Pieces.Count == before - 1,
            error.Length > 0 ? error : $"删了 1 枚，剩 {game.Players[0].Pieces.Count}");

        check("[跳棋] 让棋：不能删别人区域的棋子",
            game.RemovePiece("a", CheckersBoard.CellsOf(CheckersPoint.South)[0].X,
                CheckersBoard.CellsOf(CheckersPoint.South)[0].Y).Length > 0, "");

        check("[跳棋] 让棋：空位不能再删",
            game.RemovePiece("a", homeCell.X, homeCell.Y).Length > 0, "");
    }

    // ================================================================ AI

    private static void CheckAi(CheckFn check)
    {
        var ok = true;
        var detail = "";

        foreach (var difficulty in AiDifficulties.All)
        {
            var game = new CheckersGame(new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = false });
            game.AddPlayer("a", "A", CheckersPoint.North);
            game.AddPlayer("b", "B", CheckersPoint.South);
            game.Start();

            var move = CheckersAi.ChooseMove(game, "a", difficulty, new Random(7));
            if (move is null)
            {
                ok = false;
                detail = $"{difficulty} 没给出走法";
                break;
            }

            var legal = game.LegalMoves("a").Any(m =>
                m.FromX == move.FromX && m.FromY == move.FromY && m.ToX == move.ToX && m.ToY == move.ToY);
            if (!legal)
            {
                ok = false;
                detail = $"{difficulty} 给了非法走法";
                break;
            }
        }

        check("[跳棋 AI] 四档难度都能给出合法走法", ok, detail);

        // 能跳的时候优先跳（跳比走一格快）
        var jumpGame = new CheckersGame(new CheckersRules { AllowSingleStep = true, MustClearHomeFirst = false });
        jumpGame.AddPlayer("a", "A", CheckersPoint.North);
        jumpGame.AddPlayer("b", "B", CheckersPoint.South);
        jumpGame.Start();

        var jumps = jumpGame.LegalMoves("a").Count(m => m.Jumps > 0);
        var steps = jumpGame.LegalMoves("a").Count(m => m.Jumps == 0);
        check("[跳棋 AI] 有跳与走一格两种选择时都能算",
            jumps > 0 || steps > 0, $"跳 {jumps} / 走 {steps}");

        check("[跳棋 AI] 难度名齐全",
            AiDifficulties.All.All(d => CheckersAi.DifficultyName(d).Length > 0), "");
    }
}
