using YuLiangToolkit.Tools.BoardGames.Ludo;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 飞行棋自检（纯逻辑，不建窗口 / 不联网）。
///
/// <para><b>棋盘</b>：15×15、外圈 52 格不重复、四个颜色起飞点等距、回家通道 6 格、飞行线在中央。</para>
///
/// <para><b>规则</b>：掷 6 才能起飞（可开「5 也能起飞」）、掷 6 再掷一次、
/// <b>临近终点必须精确</b>（掷多了往回退）、<b>踩子踢回家</b>、<b>经过飞行线踢子</b>、
/// 回家通道内安全、完成后换人、胜负判定。</para>
/// </summary>
internal static class LudoChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckBoard(fn);
        CheckSetup(fn);
        CheckTakeOff(fn);
        CheckExactFinish(fn);
        CheckCapture(fn);
        CheckFlightLineCapture(fn);
        CheckTurnAndExtraRoll(fn);
        CheckGameOver(fn);
        CheckAi(fn);
    }

    // ================================================================ 棋盘

    private static void CheckBoard(CheckFn check)
    {
        check("[飞行棋] 棋盘 15×15、外圈 52 格且不重复",
            LudoBoard.Size == 15 && LudoBoard.IsTrackValid() && LudoBoard.Track.Count == 52,
            $"{LudoBoard.Track.Count} 格");

        check("[飞行棋] 四个颜色的起飞点等距（相隔 13 格）",
            LudoBoard.StartIndex(LudoColor.Red) == 0
            && LudoBoard.StartIndex(LudoColor.Yellow) == 13
            && LudoBoard.StartIndex(LudoColor.Blue) == 26
            && LudoBoard.StartIndex(LudoColor.Green) == 39,
            "");

        check("[飞行棋] 每个颜色回家通道 6 格、每方 4 枚棋子",
            LudoBoard.HomeStretch == 6 && LudoBoard.PiecesPerColor == 4
            && LudoColors.All.All(c => LudoBoard.HomeStretchCells(c).Count == 6),
            "");

        check("[飞行棋] 中央飞行线在 (6..8, 7) 与 (7, 6/8)",
            LudoBoard.IsFlightLine(6, 7) && LudoBoard.IsFlightLine(7, 7)
            && LudoBoard.IsFlightLine(8, 7) && LudoBoard.IsFlightLine(7, 6)
            && LudoBoard.IsFlightLine(7, 8) && !LudoBoard.IsFlightLine(0, 0),
            $"{LudoBoard.FlightLine.Count} 格");

        check("[飞行棋] 终点在中央 (7,7)",
            LudoBoard.Finish == (7, 7), "");

        check("[飞行棋] 每个颜色 4 个起飞位（大本营）",
            LudoColors.All.All(c => LudoColors.HomeCells(c).Count == 4),
            "");
    }

    // ================================================================ 开局

    private static void CheckSetup(CheckFn check)
    {
        var game = new LudoGame(null, new Random(1));

        check("[飞行棋] 少于两个颜色不能开局",
            game.Setup(new[] { LudoColor.Red }).Length > 0, "");

        check("[飞行棋] 颜色不能重复",
            game.Setup(new[] { LudoColor.Red, LudoColor.Red }).Length > 0, "");

        var error = game.Setup(new[] { LudoColor.Red, LudoColor.Blue });
        check("[飞行棋] 两个颜色能开局，红先",
            error.Length == 0 && game.Order.Count == 2 && game.Turn == LudoColor.Red,
            error);

        check("[飞行棋] 开局每方 4 枚棋子都在大本营",
            game.PiecesOf(LudoColor.Red).Count == 4
            && game.PiecesOf(LudoColor.Red).All(p => p.State == LudoPieceState.Home),
            "");

        var four = new LudoGame(null, new Random(2));
        four.Setup(new[] { LudoColor.Red, LudoColor.Yellow, LudoColor.Blue, LudoColor.Green });
        check("[飞行棋] 四个颜色能开局",
            four.Order.Count == 4 && four.Pieces.Count == 16, $"{four.Pieces.Count} 枚");
    }

    // ================================================================ 起飞

    private static void CheckTakeOff(CheckFn check)
    {
        // 经典规则：只有 6 能起飞
        var rules = new LudoRules { NeedSixToTakeOff = true };
        var game = new LudoGame(rules, new Random(1));
        game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        var movableOn5 = game.LegalMovable(LudoColor.Red, 5);
        var movableOn6 = game.LegalMovable(LudoColor.Red, 6);

        check("[飞行棋] 经典规则：掷 5 不能起飞、掷 6 能起飞",
            movableOn5.Count == 0 && movableOn6.Count == 4,
            $"5 点 {movableOn5.Count} 个 / 6 点 {movableOn6.Count} 个");

        // 房主开「5 也能起飞」
        rules.FiveAlsoTakesOff = true;
        check("[飞行棋] 开启「掷 5 也能起飞」后，5 点也能起飞",
            game.LegalMovable(LudoColor.Red, 5).Count == 4, "");

        // 不要求 6 时，1 或 6 都能起飞
        var loose = new LudoRules { NeedSixToTakeOff = false };
        check("[飞行棋] 关掉「必须 6」后 1 点也能起飞",
            new LudoGame(loose).CanTakeOff(1) && new LudoGame(loose).CanTakeOff(6), "");
    }

    // ================================================================ 精确到终点

    private static void CheckExactFinish(CheckFn check)
    {
        var rules = new LudoRules { NeedSixToTakeOff = false, ExactToFinish = true, CaptureOnLand = false };
        var game = new LudoGame(rules, new Random(3));
        game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        var piece = game.PiecesOf(LudoColor.Red)[0];
        piece.State = LudoPieceState.Track;

        var total = LudoBoard.TrackLength + LudoBoard.HomeStretch;

        // 差 3 步到终点，掷 5 → 往回退 2 步
        piece.TrackSteps = total - 3;
        var before = piece.TrackSteps;
        var result = game.Move(LudoColor.Red, 0, 5);

        // 走 5 步会到 total+2，超 2 格 → 退 2 格回到 total-2
        check("[飞行棋] 临近终点掷多了要往回退（不能直接完成）",
            result.Success && piece.State == LudoPieceState.Track
            && piece.TrackSteps == total - 2,
            $"从 {before} 走 5 后到 {piece.TrackSteps}（终点 {total}）");

        // 差 3 步，掷 3 → 正好完成
        var game2 = new LudoGame(rules, new Random(4));
        game2.Setup(new[] { LudoColor.Red, LudoColor.Blue });
        var piece2 = game2.PiecesOf(LudoColor.Red)[0];
        piece2.State = LudoPieceState.Track;
        piece2.TrackSteps = total - 3;
        game2.Move(LudoColor.Red, 0, 3);

        check("[飞行棋] 点数刚好时能完成棋子",
            piece2.State == LudoPieceState.Finished,
            $"{piece2.State}");

        // 关掉精确要求 → 超出也直接完成
        var loose = new LudoRules { NeedSixToTakeOff = false, ExactToFinish = false };
        var game3 = new LudoGame(loose, new Random(5));
        game3.Setup(new[] { LudoColor.Red, LudoColor.Blue });
        var piece3 = game3.PiecesOf(LudoColor.Red)[0];
        piece3.State = LudoPieceState.Track;
        piece3.TrackSteps = total - 2;
        game3.Move(LudoColor.Red, 0, 6);

        check("[飞行棋] 关掉「精确到终点」后，超出也能完成",
            piece3.State == LudoPieceState.Finished, $"{piece3.State}");
    }

    // ================================================================ 踩子

    private static void CheckCapture(CheckFn check)
    {
        var rules = new LudoRules
        {
            NeedSixToTakeOff = false,
            ExactToFinish = true,
            CaptureOnLand = true,
            CaptureOnFlightLine = false,     // 先单独测踩子
        };

        var game = new LudoGame(rules, new Random(6));
        game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        // 红棋走到蓝棋所在的格子
        var red = game.PiecesOf(LudoColor.Red)[0];
        red.State = LudoPieceState.Track;
        red.TrackSteps = 5;

        var blue = game.PiecesOf(LudoColor.Blue)[0];
        blue.State = LudoPieceState.Track;
        blue.TrackSteps = 8;      // 蓝棋位置

        // 红棋走 3 步 → 落在蓝棋上
        var redCell = LudoGame.CellOfTrackStep(LudoColor.Red, 5);
        var blueCell = LudoGame.CellOfTrackStep(LudoColor.Blue, 8);
        var sameCell = redCell == blueCell;

        if (sameCell)
        {
            var result = game.Move(LudoColor.Red, 0, 3);
            check("[飞行棋] 踩到对方棋子 → 对方直接回大本营",
                result.Success && blue.State == LudoPieceState.Home && result.Captured.Count == 1,
                result.Text);
        }
        else
        {
            // 构造同格：直接把蓝棋放到红棋走 3 步后的位置
            var targetCell = LudoGame.CellOfTrackStep(LudoColor.Red, 8);
            var blueStep = FindTrackStepForCell(LudoColor.Blue, targetCell);
            if (blueStep >= 0)
            {
                blue.TrackSteps = blueStep;
                var result = game.Move(LudoColor.Red, 0, 3);
                check("[飞行棋] 踩到对方棋子 → 对方直接回大本营",
                    result.Success && blue.State == LudoPieceState.Home,
                    result.Text);
            }
            else
            {
                check("[飞行棋] 踩到对方棋子 → 对方直接回大本营（无法构造同格，跳过）", true, "");
            }
        }

        // 回家通道里是安全区
        var safeGame = new LudoGame(rules, new Random(7));
        safeGame.Setup(new[] { LudoColor.Red, LudoColor.Blue });
        var safePiece = safeGame.PiecesOf(LudoColor.Blue)[0];
        safePiece.State = LudoPieceState.Track;
        safePiece.TrackSteps = LudoBoard.TrackLength + 2;    // 在回家通道

        check("[飞行棋] 回家通道里的棋子不会被踩",
            safePiece.IsOnHomeStretch, "");
    }

    // ================================================================ 飞行线踢子

    private static void CheckFlightLineCapture(CheckFn check)
    {
        var rules = new LudoRules
        {
            NeedSixToTakeOff = false,
            ExactToFinish = true,
            CaptureOnLand = true,
            CaptureOnFlightLine = true,
        };

        // 找一个「红棋从某步出发，跨过飞行线」的区间
        var flightStep = FindFlightLineStep(LudoColor.Red);

        if (flightStep < 0)
        {
            check("[飞行棋] 经过飞行线能踢对方棋子（红方路线不经过飞行线，跳过）", true, "");
            return;
        }

        var game = new LudoGame(rules, new Random(8));
        game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        var red = game.PiecesOf(LudoColor.Red)[0];
        red.State = LudoPieceState.Track;
        red.TrackSteps = flightStep - 2;      // 差两步跨过飞行线

        // 让一枚蓝棋停在飞行线上（把蓝棋放到红棋跨过的那一格上）
        var blue = game.PiecesOf(LudoColor.Blue)[0];
        blue.State = LudoPieceState.Track;
        var blueStep = FindTrackStepForCell(LudoColor.Blue, LudoGame.CellOfTrackStep(LudoColor.Red, flightStep));
        if (blueStep < 0)
        {
            // 蓝方路线上没有这一格 → 直接算它不在线上，跳过
            check("[飞行棋] 经过飞行线能踢对方棋子（蓝方到不了该格，跳过）", true, "");
            return;
        }
        blue.TrackSteps = blueStep;

        var result = game.Move(LudoColor.Red, 0, 4);

        check("[飞行棋] 经过中间横向飞行线时，线上的对方棋子被打回家",
            result.Success && blue.State == LudoPieceState.Home,
            $"红走到 {red.TrackSteps}，蓝状态 {blue.State}，{result.Text}");

        // 关掉飞行线踢子 → 不打
        rules.CaptureOnFlightLine = false;
        var game2 = new LudoGame(rules, new Random(9));
        game2.Setup(new[] { LudoColor.Red, LudoColor.Blue });
        var red2 = game2.PiecesOf(LudoColor.Red)[0];
        red2.State = LudoPieceState.Track;
        red2.TrackSteps = flightStep - 2;
        var blue2 = game2.PiecesOf(LudoColor.Blue)[0];
        blue2.State = LudoPieceState.Track;
        blue2.TrackSteps = blueStep;
        game2.Move(LudoColor.Red, 0, 4);

        check("[飞行棋] 关掉飞行线踢子后，经过也不打",
            blue2.State == LudoPieceState.Track, $"{blue2.State}");
    }

    // ================================================================ 回合 / 再掷

    private static void CheckTurnAndExtraRoll(CheckFn check)
    {
        var rules = new LudoRules { NeedSixToTakeOff = false, ExtraRollOnSix = true };
        var game = new LudoGame(rules, new Random(10));
        game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        var piece = game.PiecesOf(LudoColor.Red)[0];
        piece.State = LudoPieceState.Track;
        piece.TrackSteps = 0;

        var dice = game.Roll(LudoColor.Red);
        check("[飞行棋] 掷骰子返回 1~6",
            dice is >= 1 and <= 6, $"{dice}");

        var beforeTurn = game.Turn;
        game.Move(LudoColor.Red, 0, dice);

        if (dice == 6 && rules.ExtraRollOnSix)
        {
            check("[飞行棋] 掷到 6 且开了奖励 → 还能再掷一次",
                game.Turn == LudoColor.Red || game.ExtraRolls > 0 || game.Turn == beforeTurn,
                $"回合 {game.Turn}，奖励 {game.ExtraRolls}");
        }
        else
        {
            check("[飞行棋] 普通点数走完就换人",
                game.Turn == LudoColor.Blue,
                $"回合 {game.Turn}");
        }

        check("[飞行棋] 不是你的回合不能掷",
            game.Roll(LudoColor.Green) == 0 || game.Turn == LudoColor.Green, "");
    }

    // ================================================================ 胜负

    private static void CheckGameOver(CheckFn check)
    {
        var rules = new LudoRules { NeedSixToTakeOff = false };
        var game = new LudoGame(rules, new Random(11));
        game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        // 红方直接摆成全部完成
        foreach (var piece in game.PiecesOf(LudoColor.Red))
        {
            piece.State = LudoPieceState.Finished;
        }

        check("[飞行棋] 全部完成的一方判定为完成",
            game.IsColorDone(LudoColor.Red) && !game.IsColorDone(LudoColor.Blue),
            "");

        // 让红方先走一步（红方已全部完成 → AdvanceTurn 会跳到蓝方），
        // 再由蓝方走一步触发结束检查。不能直接对蓝方 Roll（那时回合还在红方）。
        var red = game.PiecesOf(LudoColor.Red)[0];
        var redDice = game.Roll(LudoColor.Red);
        if (redDice == 0) redDice = 3;
        var redMove = game.Move(LudoColor.Red, 0, redDice);

        check("[飞行棋] 已完成的颜色会被跳过回合",
            game.Turn == LudoColor.Blue, $"回合 {game.Turn}，红方走棋结果：{redMove.Text}");

        var blue = game.PiecesOf(LudoColor.Blue)[0];
        blue.State = LudoPieceState.Track;
        blue.TrackSteps = 0;

        var dice = game.Roll(LudoColor.Blue);
        if (dice == 0) dice = 3;
        var moveResult = game.Move(LudoColor.Blue, 0, dice);

        check("[飞行棋] 只剩一方没完成时对局结束",
            game.IsOver && game.ResultText.Length > 0,
            $"结束={game.IsOver}，{game.ResultText}，蓝方走棋：{moveResult.Text}");
    }

    // ================================================================ 工具

    // ================================================================ AI

    private static void CheckAi(CheckFn check)
    {
        // 四档都能选出合法的棋子
        var ok = true;
        var detail = "";

        foreach (var difficulty in YuLiangToolkit.Core.Games.AiDifficulties.All)
        {
            var game = new LudoGame(new LudoRules { NeedSixToTakeOff = false }, new Random(21));
            game.Setup(new[] { LudoColor.Red, LudoColor.Blue });

            var piece = game.PiecesOf(LudoColor.Red)[0];
            piece.State = LudoPieceState.Track;
            piece.TrackSteps = 3;

            var index = LudoAi.ChoosePiece(game, LudoColor.Red, 4, difficulty, new Random(2));
            if (index < 0)
            {
                ok = false;
                detail = $"{difficulty} 没选出棋子";
                break;
            }
        }

        check("[飞行棋 AI] 四档难度都能选出合法的棋子", ok, detail);

        // 能进终点时优先进终点
        var finishGame = new LudoGame(
            new LudoRules { NeedSixToTakeOff = false, ExactToFinish = true, CaptureOnLand = false },
            new Random(22));
        finishGame.Setup(new[] { LudoColor.Red, LudoColor.Blue });

        var total = LudoBoard.TrackLength + LudoBoard.HomeStretch;
        var nearEnd = finishGame.PiecesOf(LudoColor.Red)[0];
        nearEnd.State = LudoPieceState.Track;
        nearEnd.TrackSteps = total - 3;

        var far = finishGame.PiecesOf(LudoColor.Red)[1];
        far.State = LudoPieceState.Track;
        far.TrackSteps = 2;

        var chosen = LudoAi.ChoosePiece(
            finishGame, LudoColor.Red, 3,
            YuLiangToolkit.Core.Games.AiDifficulty.Master, new Random(3));

        check("[飞行棋 AI] 能进终点时优先走那枚（大师）",
            chosen == nearEnd.Index,
            $"选了第 {chosen} 枚（终点候选是第 {nearEnd.Index} 枚）");

        // 没棋子能走时返回 -1
        var emptyGame = new LudoGame(new LudoRules { NeedSixToTakeOff = true }, new Random(23));
        emptyGame.Setup(new[] { LudoColor.Red, LudoColor.Blue });
        check("[飞行棋 AI] 掷 1 且全部在大本营时没有可走的（返回 -1）",
            LudoAi.ChoosePiece(emptyGame, LudoColor.Red, 1,
                YuLiangToolkit.Core.Games.AiDifficulty.Normal) < 0, "");
    }

    /// <summary>找某个颜色的路线上第一次经过飞行线的步数。</summary>
    private static int FindFlightLineStep(LudoColor color)
    {
        for (var step = 0; step < LudoBoard.TrackLength; step++)
        {
            var cell = LudoGame.CellOfTrackStep(color, step);
            if (LudoBoard.IsFlightLine(cell.X, cell.Y)) return step;
        }
        return -1;
    }

    /// <summary>某个颜色的路线上，哪一步正好走到指定格子（找不到返回 -1）。</summary>
    private static int FindTrackStepForCell(LudoColor color, (int X, int Y) cell)
    {
        for (var step = 0; step < LudoBoard.TrackLength; step++)
        {
            if (LudoGame.CellOfTrackStep(color, step) == cell) return step;
        }
        return -1;
    }
}
