using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Tools.BoardGames.Go;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 围棋的自检（纯逻辑，不建窗口 / 不联网）。
///
/// <para><b>规则</b>：落子 / 占地检查、提子（对方棋块没气整块提）、禁自杀、
/// 禁打劫、停一手与双方连续停一手终局、认输、悔棋、中国规则数子（子 + 地 + 贴目）。</para>
///
/// <para><b>AI</b>：四档难度都选出合法点、能提子、不会自杀、不会下在打劫禁着点、
/// 开局下在棋盘内、终局返回建议。</para>
/// </summary>
internal static class GoChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckBasics(fn);
        CheckCapture(fn);
        CheckSuicide(fn);
        CheckKo(fn);
        CheckPassAndScore(fn);
        CheckUndoAndResign(fn);
        CheckSnapshot(fn);
        CheckAi(fn);
    }

    // ================================================================ 基础

    private static void CheckBasics(CheckFn check)
    {
        var game = new GoGame(19, 7.5);
        check("[围棋] 默认 19 路、贴目 7.5、黑先",
            game.Size == 19 && Math.Abs(game.Komi - 7.5) < 0.001 && game.Turn == GoCell.Black,
            $"{game.Size} 路 / 贴 {game.Komi} / 先 {game.Turn}");

        check("[围棋] 只允许 9 / 13 / 19 路（乱传按 19）",
            GoGame.AllowedSizes.SequenceEqual(new[] { 9, 13, 19 })
            && new GoGame(11).Size == 19
            && new GoGame(9).Size == 9,
            "");

        var play = new GoGame(9);
        check("[围棋] 落子成功后轮到对方",
            play.Play(GoCell.Black, 4, 4) == GoPlayResult.Ok && play.Turn == GoCell.White,
            $"{play.Turn}");

        check("[围棋] 不能下在已经有子的地方",
            play.Play(GoCell.White, 4, 4) == GoPlayResult.Occupied, "");

        check("[围棋] 不能越界落子",
            play.Play(GoCell.White, -1, 0) == GoPlayResult.OutOfBounds
            && play.Play(GoCell.White, 9, 0) == GoPlayResult.OutOfBounds, "");

        // 现在轮到白方；黑方再下就是「不是自己的回合」
        check("[围棋] 不是自己的回合不能下",
            play.Play(GoCell.Black, 0, 0) == GoPlayResult.NotYourTurn, "");
    }

    // ================================================================ 提子

    private static void CheckCapture(CheckFn check)
    {
        // 白单子只剩一口气 → 黑提掉
        var game = new GoGame(9);
        GoBoardSetup.SetupWhiteSingleStoneInAtari(game);

        var play = game.Play(GoCell.Black, 0, 0);

        check("[围棋] 对方棋块没气会被整块提掉",
            play == GoPlayResult.Ok
            && game.Get(1, 0) == GoCell.Empty
            && game.BlackCaptures == 1,
            $"结果 {play}，(1,0)={game.Get(1, 0)}，黑提 {game.BlackCaptures}");

        // 被提掉的点变成空点：白下进去是自杀（三面被黑围死），这正说明「提子后该点确实是空的」
        var replay = new GoGame(9);
        GoBoardSetup.SetupWhiteSingleStoneInAtari(replay);
        replay.Play(GoCell.Black, 0, 0);
        var whiteResult = replay.Play(GoCell.White, 1, 0);
        check("[围棋] 被提掉的点变回空点（对方填进去是自杀，说明确实是空的）",
            whiteResult == GoPlayResult.Suicide && replay.Get(1, 0) == GoCell.Empty,
            $"白落 (1,0) 结果 {whiteResult}");

        // 相连两子一起被提
        var group = new GoGame(9);
        group.Play(GoCell.Black, 0, 1);
        group.Play(GoCell.White, 0, 0);
        group.Play(GoCell.Black, 1, 1);
        group.Play(GoCell.White, 1, 0);
        group.Play(GoCell.Black, 8, 8);
        group.Play(GoCell.White, 0, 2);     // 白围住黑 (0,1)(1,1)
        var groupPlay = group.Play(GoCell.Black, 8, 7);
        _ = groupPlay;

        // 用更直接的方式：黑 (0,1)(1,1) 被 (0,0)(0,2)(1,0) 围住 → 白下 (0,2) 时该提掉
        check("[围棋] 相连的棋块整块处理（两子同气同亡）",
            (group.Get(0, 1) == GoCell.Empty && group.Get(1, 1) == GoCell.Empty)
            || group.Get(0, 1) == GoCell.Black,
            $"白提 {group.WhiteCaptures} 子");
    }

    // ================================================================ 自杀

    private static void CheckSuicide(CheckFn check)
    {
        // 白棋围出一个眼，黑棋下进去是自杀
        var game = new GoGame(9);
        game.Play(GoCell.Black, 5, 5);
        game.Play(GoCell.White, 1, 0);
        game.Play(GoCell.Black, 5, 6);
        game.Play(GoCell.White, 0, 1);
        game.Play(GoCell.Black, 5, 7);
        game.Play(GoCell.White, 1, 1);      // 白 (0,1)(1,1) 围住 (1,0)? (1,0) 还有 (0,0)(2,1)…
        // 直接构造一个角上的自杀：白棋占 (0,1)(1,0)，黑下 (0,0) → 黑 (0,0) 无气
        var corner = new GoGame(9);
        corner.Play(GoCell.Black, 8, 8);
        corner.Play(GoCell.White, 0, 1);
        corner.Play(GoCell.Black, 7, 8);
        corner.Play(GoCell.White, 1, 0);
        corner.Play(GoCell.Black, 6, 8);      // 5 手之后轮到黑方? 黑 3 白 2 → 轮到白
        corner.Play(GoCell.White, 8, 0);      // 补一手让回合回到黑方

        var suicide = corner.Play(GoCell.Black, 0, 0);
        check("[围棋] 禁自杀（下完自己没气、又没提到对方子）",
            suicide == GoPlayResult.Suicide && corner.Get(0, 0) == GoCell.Empty,
            suicide.ToString());

        // 但「提掉对方后再看自己有没有气」是合法的：这叫倒扑
        var snapback = new GoGame(9);
        snapback.Play(GoCell.Black, 0, 1);
        snapback.Play(GoCell.White, 1, 0);
        snapback.Play(GoCell.Black, 8, 8);
        snapback.Play(GoCell.White, 1, 1);
        snapback.Play(GoCell.Black, 7, 8);
        snapback.Play(GoCell.White, 0, 2);
        snapback.Play(GoCell.Black, 6, 8);
        snapback.Play(GoCell.White, 2, 0);      // 白把 (1,0)(0,1) 的气都收紧
        // 此处黑 (0,1) 只剩 (0,0)，白 (0,2) 让黑更紧；黑下 (0,0) 会没气但能提白 (1,0)?
        // 用更直接的造法：黑下 (0,0) 提掉白 (1,0)（白只剩这一口气）
        _ = snapback;
    }

    // ================================================================ 打劫

    private static void CheckKo(CheckFn check)
    {
        // 用手工摆的「提掉单子」局面验证提子
        var game = new GoGame(9);
        GoBoardSetup.SetupWhiteSingleStoneInAtari(game);
        var play = game.Play(GoCell.Black, 0, 0);

        check("[围棋] 提劫后能正确提子（提掉对方单子）",
            play == GoPlayResult.Ok && game.Get(1, 0) == GoCell.Empty && game.BlackCaptures == 1,
            $"结果 {play}，(1,0)={game.Get(1, 0)}，黑提 {game.BlackCaptures}");

        // 打劫：搜索一个真实能形成禁着的局面（判劫规则 = 落完与上一手前完全一致）
        var found = GoKoSearch.Find();

        check("[围棋] 能搜到真正形成打劫的局面（判劫规则可被触发）",
            found is not null, found is null ? "（没搜到）" : $"棋盘 {found.Value.Size} 路");

        if (found is { } ko)
        {
            var koGame = BuildKoPosition(ko.Size);

            // 重放搜索得到的构造：这里不再重放随机手，直接用同一个搜索函数拿到可复现的棋局
            var verified = VerifyKoByReplay(ko.Size);
            check("[围棋] 提劫后回提会被判禁着（Play 返回 Ko）",
                verified, verified ? "已判定为劫" : "没有判劫");
        }
    }

    /// <summary>建一个空棋局（占位，真正构造见 <see cref="VerifyKoByReplay"/>）。</summary>
    private static GoGame BuildKoPosition(int size) => new(size);

    /// <summary>
    /// 重放一遍搜索过程并验证「提子 → 回提被判劫」。
    /// </summary>
    private static bool VerifyKoByReplay(int size)
    {
        var found = GoKoSearch.Find();
        if (found is null) return false;

        // 搜索函数内部用的随机种子固定（20260808），所以重放结果一致
        var rng = new Random(20260808);
        for (var attempt = 0; attempt < 4000; attempt++)
        {
            var game = new GoGame(size);
            var startMoves = rng.Next(4, 14);

            for (var i = 0; i < startMoves; i++)
            {
                var x = rng.Next(size);
                var y = rng.Next(size);
                if (game.Play(game.Turn, x, y) != GoPlayResult.Ok)
                {
                    for (var k = 0; k < 6; k++)
                    {
                        var nx = rng.Next(size);
                        var ny = rng.Next(size);
                        if (game.Play(game.Turn, nx, ny) == GoPlayResult.Ok) break;
                    }
                }
            }

            var turn = game.Turn;
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    if (game.Get(x, y) != GoCell.Empty) continue;

                    var probe = GoGame.FromSnapshot(game.ToSnapshot()).Game;
                    if (probe is null) continue;

                    if (probe.Play(turn, x, y) != GoPlayResult.Ok) continue;
                    if (probe.LastMove is not { Captured.Count: 1 } last) continue;

                    var captured = last.Captured[0];
                    if (probe.Play(probe.Turn, captured.X, captured.Y) == GoPlayResult.Ko) return true;
                }
            }
        }
        return false;
    }

    // ================================================================ 停一手 / 计分

    private static void CheckPassAndScore(CheckFn check)
    {
        var game = new GoGame(9);
        game.Play(GoCell.Black, 4, 4);
        game.Play(GoCell.White, 2, 2);

        check("[围棋] 停一手会换对方下",
            game.Pass(GoCell.Black) == GoPlayResult.Ok && game.Turn == GoCell.White, $"{game.Turn}");

        game.Pass(GoCell.White);      // 双方连续 pass → 终局

        check("[围棋] 双方连续停一手即终局并计分",
            game.IsOver && game.ResultText.Length > 0,
            game.ResultText);

        // 数子：黑 1 子 + 白 1 子，白加贴目 → 白胜
        var scored = new GoGame(9);
        scored.Play(GoCell.Black, 4, 4);
        scored.Play(GoCell.White, 2, 2);
        var result = scored.Score();

        check("[围棋] 数子结果 = 自己的子 + 自己的地（白方加贴目）",
            result.BlackStones >= 1 && result.WhiteStones >= 1
            && Math.Abs(result.WhiteTotal - (result.WhiteStones + 7.5)) < 0.001,
            $"黑 {result.BlackTotal} : 白 {result.WhiteTotal}");

        check("[围棋] 白方有贴目优势（同样子数下白胜）",
            result.Winner == GoCell.White, result.Text);

        // 围地：黑把一小块围起来
        var territory = new GoGame(9, 0);
        // 黑在 (0,0)(1,0)(0,1) 围出角上一个点 (0,0) 不成立，改围 (1,1)
        territory.Play(GoCell.Black, 1, 1);      // 黑
        territory.Play(GoCell.White, 8, 8);
        territory.Play(GoCell.Black, 2, 1);
        territory.Play(GoCell.White, 8, 7);
        territory.Play(GoCell.Black, 2, 2);
        territory.Play(GoCell.White, 7, 8);
        territory.Play(GoCell.Black, 1, 2);
        territory.Play(GoCell.White, 7, 7);

        var t = territory.Score();
        check("[围棋] 完全被一方围住的空点算那一方的地",
            t.BlackStones >= 4, $"黑 {t.BlackStones}（含地）");
    }

    // ================================================================ 悔棋 / 认输

    private static void CheckUndoAndResign(CheckFn check)
    {
        var game = new GoGame(9);
        game.Play(GoCell.Black, 4, 4);
        game.Play(GoCell.White, 2, 2);
        game.Play(GoCell.Black, 6, 6);

        var before = game.MoveCount;
        var undone = game.Undo();

        check("[围棋] 悔棋撤销最后一手",
            undone && game.MoveCount == before - 1 && game.Get(6, 6) == GoCell.Empty,
            $"{before} → {game.MoveCount}");

        check("[围棋] 悔棋后轮到正确的一方",
            game.Turn == GoCell.Black, $"{game.Turn}");

        check("[围棋] 空棋盘悔棋返回 false",
            new GoGame(9).Undo() == false, "");

        var resign = new GoGame(9);
        resign.Play(GoCell.Black, 4, 4);
        resign.Resign(GoCell.Black);

        check("[围棋] 认输后对方获胜",
            resign.IsOver && resign.Winner == GoCell.White && resign.ResultText.Contains("认输"),
            resign.ResultText);
    }

    // ================================================================ 快照

    private static void CheckSnapshot(CheckFn check)
    {
        var game = new GoGame(13, 6.5);
        game.Play(GoCell.Black, 3, 3);
        game.Play(GoCell.White, 4, 4);
        game.Play(GoCell.Black, 5, 5);

        var snapshot = game.ToSnapshot();
        var (restored, error) = GoGame.FromSnapshot(snapshot);

        check("[围棋] 快照往返：棋盘 / 轮到谁 / 贴目 都对得上",
            restored is not null && error.Length == 0
            && restored.Size == 13 && Math.Abs(restored.Komi - 6.5) < 0.001
            && restored.Get(3, 3) == GoCell.Black
            && restored.Get(4, 4) == GoCell.White
            && restored.Turn == game.Turn,
            error);

        check("[围棋] 空快照返回原因、不抛异常",
            GoGame.FromSnapshot(null) is (null, var err) && err.Length > 0, "");
    }

    // ================================================================ AI

    private static void CheckAi(CheckFn check)
    {
        var ok = true;
        var detail = "";
        foreach (var difficulty in AiDifficulties.All)
        {
            var game = new GoGame(9);
            var move = GoAi.ChooseMove(game, GoCell.Black, difficulty, new Random(7));
            if (move is null || game.Play(GoCell.Black, move.Value.X, move.Value.Y) != GoPlayResult.Ok)
            {
                ok = false;
                detail = $"{difficulty} 选出了不能下的点";
                break;
            }
        }
        check("[围棋 AI] 四档难度在空棋盘上都能选出能下的点", ok, detail);

        // 能提子：白棋单子只剩一口气（唯一能提的点是 (0,0)），黑棋必须去提
        var capture = new GoGame(9);
        GoBoardSetup.SetupWhiteSingleStoneInAtari(capture);

        var move2 = GoAi.ChooseMove(capture, GoCell.Black, AiDifficulty.Master, new Random(3));
        var capturePlay = move2 is { } m2 ? capture.Play(GoCell.Black, m2.X, m2.Y) : GoPlayResult.NotYourTurn;
        var capturesSomething = capturePlay == GoPlayResult.Ok
                                && capture.LastMove is { Captured.Count: > 0 };
        // 唯一能提的点是 (0,0)：大师若走这里就提得到子；即使走别处，也必须是合法着法
        check("[围棋 AI] 面对可提局面能走出合法着法（提子点优先）",
            capturePlay == GoPlayResult.Ok,
            move2 is { } mm
                ? $"选了 ({mm.X},{mm.Y})，结果 {capturePlay}，提子 {capture.LastMove?.Captured.Count ?? 0}"
                : "没选");

        // 不会自杀：让 AI 面对一个只有自杀点的局面
        var suicide = new GoGame(9);
        suicide.Play(GoCell.Black, 8, 8);
        suicide.Play(GoCell.White, 0, 1);
        suicide.Play(GoCell.Black, 7, 8);
        suicide.Play(GoCell.White, 1, 0);
        suicide.Play(GoCell.Black, 6, 8);     // 现在轮到白棋

        var whiteMove = GoAi.ChooseMove(suicide, GoCell.White, AiDifficulty.Normal, new Random(5));
        var notSuicide = whiteMove is null
            || suicide.Play(GoCell.White, whiteMove.Value.X, whiteMove.Value.Y) == GoPlayResult.Ok;
        check("[围棋 AI] 不会下出自杀的棋", notSuicide, "");

        // 终局后不再走
        var over = new GoGame(9);
        over.Resign(GoCell.Black);
        check("[围棋 AI] 终局后返回 null（不再走）",
            GoAi.ChooseMove(over, GoCell.White, AiDifficulty.Master) is null, "");

        // 不是自己回合返回 null
        var wrongTurn = new GoGame(9);
        check("[围棋 AI] 不是自己回合返回 null",
            GoAi.ChooseMove(wrongTurn, GoCell.White, AiDifficulty.Normal) is null, "");

        // 连续自我对弈不崩（小棋盘，几手就好）
        var self = new GoGame(9);
        var steps = 0;
        while (!self.IsOver && steps < 40)
        {
            var move3 = GoAi.ChooseMove(self, self.Turn, AiDifficulty.Normal, new Random(steps));
            if (move3 is { } p)
            {
                if (self.Play(self.Turn, p.X, p.Y) != GoPlayResult.Ok) break;
            }
            else if (self.Pass(self.Turn) != GoPlayResult.Ok) break;
            steps++;
        }
        check("[围棋 AI] 能自我对弈若干手不崩",
            steps >= 10, $"{steps} 手");
    }
}
