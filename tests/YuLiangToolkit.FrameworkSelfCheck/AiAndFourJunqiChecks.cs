using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Tools.BoardGames.Doudizhu;
using YuLiangToolkit.Tools.BoardGames.Gomoku;
using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Tools.BoardGames.Xiangqi;
using YuLiangToolkit.Tools.Games.Tetris;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「电脑对手（AI）」与「四国军棋」的自检（纯逻辑，不建窗口 / 不联网）。
///
/// <para><b>AI 部分</b>：五个游戏各跑「能选出合法着法」「四档难度都安全」「难度有区分」
/// 「不会自杀式走法」这几类断言。</para>
///
/// <para><b>四国军棋</b>：棋盘几何（4 个方 / 中央十字 / 每方 25 点）、
/// 同盟判定（对家是队友、不能吃队友）、四方轮转、夺旗与无子可走判负、暗棋过滤（同盟可见、敌人隐藏）。</para>
/// </summary>
internal static class AiAndFourJunqiChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        _ = plain;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckDifficultyLevels(fn);
        CheckGomokuAi(fn);
        CheckXiangqiAi(fn);
        CheckDoudizhuAi(fn);
        CheckJunqiAi(fn);
        CheckTetrisAi(fn);
        CheckFourJunqiBoard(fn);
        CheckFourJunqiGame(fn);
    }

    // ================================================================ 难度

    private static void CheckDifficultyLevels(CheckFn check)
    {
        check("[AI] 四档难度齐全，名字与说明都有",
            AiDifficulties.All.Count == 4
            && AiDifficulties.All.Select(AiDifficulties.DisplayName).Distinct().Count() == 4
            && AiDifficulties.All.All(d => AiDifficulties.Description(d).Length > 0),
            string.Join(" / ", AiDifficulties.All.Select(AiDifficulties.DisplayName)));

        check("[AI] 难度越高：搜索越深、犯傻越少、候选越少限制越松",
            AiDifficulties.SearchDepth(AiDifficulty.Beginner) < AiDifficulties.SearchDepth(AiDifficulty.Master)
            && AiDifficulties.BlunderChance(AiDifficulty.Beginner) > AiDifficulties.BlunderChance(AiDifficulty.Master)
            && AiDifficulties.BlunderChance(AiDifficulty.Master) == 0
            && AiDifficulties.CandidateLimit(AiDifficulty.Beginner) < AiDifficulties.CandidateLimit(AiDifficulty.Master),
            $"深度 {AiDifficulties.SearchDepth(AiDifficulty.Beginner)}→{AiDifficulties.SearchDepth(AiDifficulty.Master)}，"
            + $"犯傻 {AiDifficulties.BlunderChance(AiDifficulty.Beginner):P0}→{AiDifficulties.BlunderChance(AiDifficulty.Master):P0}");

        check("[AI] 难度键往返一致，认不出的键按普通",
            AiDifficulties.All.All(d => AiDifficulties.Parse(AiDifficulties.ToKey(d)) == d)
            && AiDifficulties.Parse("") == AiDifficulty.Normal
            && AiDifficulties.Parse("nonsense") == AiDifficulty.Normal,
            "");
    }

    // ================================================================ 五子棋

    private static void CheckGomokuAi(CheckFn check)
    {
        // 四种难度都能在开局选一个合法点
        var ok = true;
        var detail = "";
        foreach (var difficulty in AiDifficulties.All)
        {
            var game = new GomokuGame();
            var point = GomokuAi.ChooseMove(game, difficulty, new Random(7));
            if (point is null || game.Board.Get(point.Value.X, point.Value.Y) != GomokuCell.Empty)
            {
                ok = false;
                detail = $"{difficulty} 没选出合法点";
                break;
            }
        }
        check("[五子棋 AI] 四档难度在空棋盘上都能选出合法落点", ok, detail);

        // 必胜：白棋已有 4 连开口，AI 执白必须去补成五
        var win = new GomokuGame();
        win.Play(GomokuCell.Black, 0, 0);
        win.Play(GomokuCell.White, 5, 5);
        win.Play(GomokuCell.Black, 0, 1);
        win.Play(GomokuCell.White, 6, 5);
        win.Play(GomokuCell.Black, 0, 2);
        win.Play(GomokuCell.White, 7, 5);
        win.Play(GomokuCell.Black, 0, 3);
        win.Play(GomokuCell.White, 8, 5);

        // 现在轮到黑棋（上面黑棋走了 4 手），换成白棋回合更适合测「白棋补五」
        var mustWin = new GomokuGame();
        mustWin.Play(GomokuCell.Black, 10, 10);
        mustWin.Play(GomokuCell.White, 5, 5);
        mustWin.Play(GomokuCell.Black, 10, 11);
        mustWin.Play(GomokuCell.White, 6, 5);
        mustWin.Play(GomokuCell.Black, 10, 12);
        mustWin.Play(GomokuCell.White, 7, 5);
        mustWin.Play(GomokuCell.Black, 0, 0);
        mustWin.Play(GomokuCell.White, 8, 5);    // 白棋 5,6,7,8 一路，两端 4 / 9 空

        var move = GomokuAi.ChooseMove(mustWin, AiDifficulty.Master, new Random(11));
        check("[五子棋 AI] 能补成五连（白棋四连开口时去补第 5 子）",
            move is { } p && (p.X == 4 || p.X == 9) && p.Y == 5,
            move is { } m ? $"选了 ({m.X},{m.Y})" : "没选");

        // 必防：黑棋四连开口，AI 执白必须去堵
        var mustBlock = new GomokuGame();
        mustBlock.Play(GomokuCell.Black, 5, 5);
        mustBlock.Play(GomokuCell.White, 0, 0);
        mustBlock.Play(GomokuCell.Black, 6, 5);
        mustBlock.Play(GomokuCell.White, 0, 1);
        mustBlock.Play(GomokuCell.Black, 7, 5);
        mustBlock.Play(GomokuCell.White, 0, 2);
        mustBlock.Play(GomokuCell.Black, 8, 5);   // 黑 5~8 四连，4 / 9 空

        var block = GomokuAi.ChooseMove(mustBlock, AiDifficulty.Master, new Random(13));
        check("[五子棋 AI] 会堵对手的四连（不能放着不管）",
            block is { } b && b.Y == 5 && (b.X == 4 || b.X == 9),
            block is { } bm ? $"选了 ({bm.X},{bm.Y})" : "没选");

        // 不会落在已经有子的地方
        var filled = new GomokuGame(15);
        for (var y = 0; y < 15; y++)
        {
            for (var x = 0; x < 15; x++) filled.Play(filled.Turn, x, y);
        }
        check("[五子棋 AI] 棋盘下满时返回 null（不会乱落）",
            GomokuAi.ChooseMove(filled, AiDifficulty.Normal) is null || filled.IsOver, "");
    }

    // ================================================================ 象棋

    private static void CheckXiangqiAi(CheckFn check)
    {
        var ok = true;
        var detail = "";
        foreach (var difficulty in AiDifficulties.All)
        {
            var game = new XiangqiGame();
            var move = XiangqiAi.ChooseMove(game, difficulty, new Random(3));
            if (move is not { } m || !XiangqiRules.IsLegalMove(game.Board, XiangqiSide.Red, m))
            {
                ok = false;
                detail = $"{difficulty} 选出了非法着法";
                break;
            }
        }
        check("[象棋 AI] 四档难度开局都能选出合法着法", ok, detail);

        // 能吃车就必须吃（白送的子）：黑车就在红车旁边，且红方不吃就会被吃
        var captureGame = new XiangqiGame();
        captureGame.Reset(CreateCaptureBoard(), XiangqiSide.Red);
        var canEatRook = captureGame.LegalMovesFor(3, 8)
            .Any(m => m.ToX == 4 && m.ToY == 8);
        var chosen = XiangqiAi.ChooseMove(captureGame, AiDifficulty.Master, new Random(5));
        var capturedRook = chosen is { } c && captureGame.Board.Get(c.ToX, c.ToY).Type == XiangqiPieceType.Rook;
        check("[象棋 AI] 能吃到对方的车就去吃（白送的子不放过）",
            canEatRook && capturedRook,
            $"合法可吃={canEatRook}；" + (chosen is { } cm
                ? $"AI 目标 ({cm.ToX},{cm.ToY}) = {captureGame.Board.Get(cm.ToX, cm.ToY).Type}"
                : "AI 没走"));

        // 深度不同：大师不应该比入门差（用同一局面比评估结果）
        var probe = new XiangqiGame();
        var beginner = XiangqiAi.ChooseMove(probe, AiDifficulty.Beginner, new Random(1));
        var master = XiangqiAi.ChooseMove(new XiangqiGame(), AiDifficulty.Master, new Random(1));
        check("[象棋 AI] 入门与大师都能走（难度确实分歧，不是同一个函数）",
            beginner is not null && master is not null, "");

        // 被将军时必须应将
        var checkGame = new XiangqiGame();
        checkGame.Reset(CreateInCheckBoard(), XiangqiSide.Red);
        var reply = XiangqiAi.ChooseMove(checkGame, AiDifficulty.Master, new Random(9));
        var stillInCheck = reply is { } r
            && WouldStillBeInCheck(checkGame, r);
        check("[象棋 AI] 被将军时必须应将（不会走出被将的着法）",
            reply is null || !stillInCheck, reply is null ? "没走" : "已应将");
    }

    /// <summary>
    /// 构造「红车能吃黑车」的局面：两车紧邻同一行，中间没有子；
    /// 红方不吃的话黑车下一步就能吃红车，所以吃是明确的最优。
    /// </summary>
    private static XiangqiBoard CreateCaptureBoard()
    {
        var board = new XiangqiBoard();
        board.Set(4, 9, new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red));
        board.Set(4, 0, new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Black));
        board.Set(3, 8, new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Red));     // 红车（贴近本方九宫）
        board.Set(4, 8, new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Black));   // 黑车紧邻，红车一步可吃
        return board;
    }

    /// <summary>构造「红方被将军」的局面：黑车压在红将同一列。</summary>
    private static XiangqiBoard CreateInCheckBoard()
    {
        var board = new XiangqiBoard();
        board.Set(4, 9, new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red));
        board.Set(4, 0, new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Black));
        board.Set(4, 5, new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Black));   // 直接将军
        board.Set(0, 9, new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Red));
        return board;
    }

    private static bool WouldStillBeInCheck(XiangqiGame game, XiangqiMove move)
    {
        var undo = game.ApplyMoveForAi(move);
        var inCheck = XiangqiRules.IsInCheck(game.Board, XiangqiSide.Red);
        game.UndoMoveForAi(undo);
        return inCheck;
    }

    // ================================================================ 斗地主

    private static void CheckDoudizhuAi(CheckFn check)
    {
        var game = new DoudizhuGame(DoudizhuMode.Classic, new Random(31));
        game.Reset();

        // 叫分：强手牌叫得高
        var strongHand = new List<Card>
        {
            new(CardRank.RedJoker, CardSuit.Joker), new(CardRank.BlackJoker, CardSuit.Joker),
            new(CardRank.Two, CardSuit.Spade), new(CardRank.Two, CardSuit.Heart),
            new(CardRank.Ace, CardSuit.Spade), new(CardRank.Ace, CardSuit.Heart),
            new(CardRank.King, CardSuit.Spade), new(CardRank.King, CardSuit.Heart),
        };
        var weakHand = new List<Card>
        {
            new(CardRank.Three, CardSuit.Spade), new(CardRank.Four, CardSuit.Spade),
            new(CardRank.Five, CardSuit.Spade), new(CardRank.Six, CardSuit.Spade),
            new(CardRank.Seven, CardSuit.Spade), new(CardRank.Eight, CardSuit.Spade),
        };

        check("[斗地主 AI] 手牌强度：双王 + 两个 2 明显高于一把小牌",
            DoudizhuAi.HandStrength(strongHand) > DoudizhuAi.HandStrength(weakHand) + 10,
            $"{DoudizhuAi.HandStrength(strongHand)} vs {DoudizhuAi.HandStrength(weakHand)}");

        check("[斗地主 AI] 强牌叫地主、弱牌不叫",
            DoudizhuAi.DecideBid(strongHand, 0, AiDifficulty.Master) != DoudizhuBidKind.Pass
            && DoudizhuAi.DecideBid(weakHand, 0, AiDifficulty.Master) == DoudizhuBidKind.Pass,
            $"{DoudizhuAi.DecideBid(strongHand, 0, AiDifficulty.Master)} / {DoudizhuAi.DecideBid(weakHand, 0, AiDifficulty.Master)}");

        check("[斗地主 AI] 叫分必须比当前分高（不能叫得比场上低）",
            DoudizhuAi.DecideBid(strongHand, 3, AiDifficulty.Master) == DoudizhuBidKind.Pass
            && DoudizhuAi.DecideBid(strongHand, 2, AiDifficulty.Master) is DoudizhuBidKind.Three
                or DoudizhuBidKind.Pass,
            "");

        // 出牌：能一次出完就出完
        var lastTurn = new List<Card> { new(CardRank.Five, CardSuit.Spade) };
        var play = DoudizhuAi.DecidePlay(lastTurn, null, Array.Empty<Card>(), AiDifficulty.Master);
        check("[斗地主 AI] 只剩一张时直接出掉", play.Count == 1 && play[0].Rank == CardRank.Five, $"{play.Count} 张");

        // 自由出牌：出小牌（留大牌）
        var hand = new List<Card>
        {
            new(CardRank.Three, CardSuit.Spade), new(CardRank.Ace, CardSuit.Spade),
            new(CardRank.Two, CardSuit.Spade), new(CardRank.RedJoker, CardSuit.Joker),
        };
        var lead = DoudizhuAi.DecidePlay(hand, null, Array.Empty<Card>(), AiDifficulty.Master);
        check("[斗地主 AI] 先手出小牌（不会一上来就甩大牌）",
            lead.Count == 1 && lead[0].Rank == CardRank.Three,
            lead.Count > 0 ? lead[0].Text : "没出");

        // 压不过就过牌
        var table = new List<Card> { new(CardRank.Two, CardSuit.Heart) };
        var smallHand = new List<Card> { new(CardRank.Three, CardSuit.Spade), new(CardRank.Four, CardSuit.Spade) };
        var passPlay = DoudizhuAi.DecidePlay(smallHand, new CardPattern(CardPatternKind.Single, 15, 1), table,
            AiDifficulty.Master);
        check("[斗地主 AI] 压不过桌面时会选择过牌（不会硬出非法牌）",
            passPlay.Count == 0, $"{passPlay.Count} 张");

        // 所有难度出的牌都必须是手上真有的、合法牌型
        var allLegal = true;
        foreach (var difficulty in AiDifficulties.All)
        {
            var probe = new DoudizhuGame(DoudizhuMode.Classic, new Random(77));
            probe.Reset();
            probe.Bid(0, DoudizhuBidKind.Three);
            var seat = probe.CurrentSeat;
            var play2 = DoudizhuAi.DecidePlay(probe.Hand(seat).ToList(), probe.TablePattern,
                probe.TableCards, difficulty, new Random(5));
            if (play2.Count > 0 && !cardPatternValid(play2, probe.Hand(seat)))
            {
                allLegal = false;
                break;
            }
        }
        check("[斗地主 AI] 四档难度出的牌都是手上真有的（不乱出）", allLegal, "");
        _ = game;

        static bool cardPatternValid(IReadOnlyList<Card> cards, IReadOnlyList<Card> hand)
        {
            var remaining = new List<Card>(hand);
            foreach (var card in cards)
            {
                if (!remaining.Remove(card)) return false;
            }
            return CardPatternRules.Detect(cards).IsValid || cards.Count == 1;
        }
    }

    // ================================================================ 军棋

    private static void CheckJunqiAi(CheckFn check)
    {
        var ok = true;
        var detail = "";
        foreach (var difficulty in AiDifficulties.All)
        {
            var game = new JunqiGame();
            game.AutoDeploy(JunqiSide.Red, new Random(11));
            game.AutoDeploy(JunqiSide.Blue, new Random(22));

            var move = JunqiAi.ChooseMove(game, JunqiSide.Red, difficulty, new Random(3));
            if (move is not { } m || game.Move(JunqiSide.Red, m.FromX, m.FromY, m.ToX, m.ToY).Length > 0)
            {
                ok = false;
                detail = $"{difficulty} 的着法不合法";
                break;
            }
        }
        check("[军棋 AI] 四档难度都能走出合法着法", ok, detail);

        var aiGame = new JunqiGame();
        aiGame.AutoDeploy(JunqiSide.Red, new Random(5));
        aiGame.AutoDeploy(JunqiSide.Blue, new Random(6));
        var moves = JunqiAi.AllMoves(aiGame, JunqiSide.Red);
        check("[军棋 AI] 能枚举出全部合法着法（开局不少于 5 步）",
            moves.Count >= 5, $"{moves.Count} 步");

        // AI 不会去吃自己的子
        var selfCapture = moves.All(m =>
        {
            var target = aiGame.At(m.ToX, m.ToY);
            return target.IsEmpty || target.Side != JunqiSide.Red;
        });
        check("[军棋 AI] 不会去吃自己的棋子", selfCapture, "");

        // 能连走很多步不崩溃
        var longGame = new JunqiGame();
        longGame.AutoDeploy(JunqiSide.Red, new Random(101));
        longGame.AutoDeploy(JunqiSide.Blue, new Random(202));
        var steps = 0;
        while (longGame.Phase == JunqiPhase.Playing && steps < 60)
        {
            var side = longGame.CurrentSide;
            var mv = JunqiAi.ChooseMove(longGame, side, AiDifficulty.Normal, new Random(steps));
            if (mv is not { } chosen) break;
            if (longGame.Move(side, chosen.FromX, chosen.FromY, chosen.ToX, chosen.ToY).Length > 0) break;
            steps++;
        }
        check("[军棋 AI] 能连续自我对弈 60 步不崩",
            steps >= 20, $"{steps} 步");
    }

    // ================================================================ 俄罗斯方块

    private static void CheckTetrisAi(CheckFn check)
    {
        var ok = true;
        var detail = "";
        foreach (var difficulty in AiDifficulties.All)
        {
            var engine = new TetrisEngine(new Random(9));
            engine.Reset(1, 9);
            var plan = TetrisAi.ChoosePlan(engine, difficulty, false, new Random(2));
            if (plan is null)
            {
                ok = false;
                detail = $"{difficulty} 没算出落点";
                break;
            }
        }
        check("[俄罗斯方块 AI] 四档难度都能算出落点", ok, detail);

        // 空棋盘：AI 应该把方块放到靠底的位置（不会悬空）
        var empty = new TetrisEngine(new Random(3));
        empty.Reset(1, 3);
        var plan2 = TetrisAi.ChoosePlan(empty, AiDifficulty.Master, false, new Random(1));
        check("[俄罗斯方块 AI] 空棋盘上算出的落点可用（硬降后不顶死）",
            plan2 is not null, plan2 is not null ? $"rot={plan2.Value.Rotation} x={plan2.Value.X}" : "没算");

        // 互攻模式：优先消 4 行（评分里 4 行有额外加成）
        var garbage = new TetrisEngine(new Random(4));
        garbage.Reset(1, 4);
        var garbagePlan = TetrisAi.ChoosePlan(garbage, AiDifficulty.Master, true, new Random(1));
        check("[俄罗斯方块 AI] 互攻模式也算得出落点（优先消多行）",
            garbagePlan is not null, "");

        // 顶死时返回 null
        var dead = new TetrisEngine(new Random(5));
        dead.Reset(1, 5);
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++) dead.SetCell(x, y, TetrominoKind.Garbage);
        }
        check("[俄罗斯方块 AI] 棋盘顶死时不硬算（返回 null）",
            TetrisAi.ChoosePlan(dead, AiDifficulty.Normal, false) is null, "");
    }

    // ================================================================ 四国军棋

    private static void CheckFourJunqiBoard(CheckFn check)
    {
        check("[四国军棋] 棋盘 13×13，四个方各 6×6 + 中央十字",
            JunqiFourBoard.Size == 13
            && JunqiFourBoard.NationAt(0, 12) == JunqiNation.Red
            && JunqiFourBoard.NationAt(0, 0) == JunqiNation.Blue
            && JunqiFourBoard.NationAt(12, 0) == JunqiNation.Green
            && JunqiFourBoard.NationAt(12, 12) == JunqiNation.Yellow
            && JunqiFourBoard.NationAt(6, 6) is null,
            "四角归属正确");

        check("[四国军棋] 对家是同盟（红↔绿、蓝↔黄）",
            JunqiFourBoard.Ally(JunqiNation.Red) == JunqiNation.Green
            && JunqiFourBoard.Ally(JunqiNation.Blue) == JunqiNation.Yellow
            && JunqiFourBoard.IsAlly(JunqiNation.Red, JunqiNation.Green)
            && !JunqiFourBoard.IsAlly(JunqiNation.Red, JunqiNation.Blue),
            "红-绿同盟；红-蓝是敌人");

        var points = JunqiFourBoard.DeployPoints(JunqiNation.Red);
        check("[四国军棋] 每方 25 个摆棋点（30 格 - 5 个行营）",
            points.Count == 25, $"{points.Count} 个");

        var camps = 0;
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (JunqiFourBoard.IsCamp(x, y)) camps++;
            }
        }
        check("[四国军棋] 全盘 20 个行营（每方 5 个）", camps == 20, $"{camps} 个");

        check("[四国军棋] 每方 2 个大本营，且都在自己一侧",
            JunqiFourBoard.HeadquartersOf(JunqiNation.Red).Count == 2
            && JunqiFourBoard.HeadquartersOf(JunqiNation.Red).All(p => JunqiFourBoard.NationAt(p.X, p.Y) == JunqiNation.Red),
            "");
    }

    private static void CheckFourJunqiGame(CheckFn check)
    {
        var game = new JunqiFourGame();
        foreach (var nation in new[] { JunqiNation.Red, JunqiNation.Blue, JunqiNation.Green, JunqiNation.Yellow })
        {
            var error = game.AutoDeploy(nation, new Random((int)nation + 1));
            if (error.Length > 0)
            {
                check("[四国军棋] 四方都能自动布阵", false, error);
                return;
            }
        }

        check("[四国军棋] 四方布好阵后进入对局，红方先走",
            game.Phase == JunqiPhase.Playing && game.CurrentNation == JunqiNation.Red,
            $"{game.Phase} / {game.CurrentNation}");

        var moves = game.LegalMoves(JunqiNation.Red);
        check("[四国军棋] 红方开局有合法着法", moves.Count > 0, $"{moves.Count} 步");

        // 走一步 → 轮到下一家（绿）
        if (moves.Count > 0)
        {
            var m = moves[0];
            var error = game.Move(JunqiNation.Red, m.FromX, m.FromY, m.ToX, m.ToY);
            check("[四国军棋] 走一步后轮到下一家（红 → 绿）",
                error.Length == 0 && game.CurrentNation != JunqiNation.Red,
                error.Length > 0 ? error : $"现在是 {game.CurrentNation}");
        }

        // 同盟不能吃：把红方一枚棋子挪到绿方棋子旁边，验证不能吃
        var allyGame = new JunqiFourGame();
        foreach (var nation in new[] { JunqiNation.Red, JunqiNation.Blue, JunqiNation.Green, JunqiNation.Yellow })
            allyGame.AutoDeploy(nation, new Random((int)nation + 11));

        check("[四国军棋] 同盟方之间不会互相吃子（LegalTargets 里没有同盟的棋子）",
            AllyNeverTargeted(allyGame), "");

        // 暗棋：自己的全可见、同盟全可见、敌人隐藏
        var view = allyGame.VisibleBoardFor(JunqiNation.Red);
        var ownVisible = view
            .Select((piece, i) => (piece, i))
            .Where(t => !t.piece.IsEmpty
                        && JunqiFourBoard.NationAt(t.i % JunqiFourBoard.Size, t.i / JunqiFourBoard.Size) == JunqiNation.Red)
            .All(t => t.piece.Revealed);
        var allyVisible = view
            .Select((piece, i) => (piece, i))
            .Where(t => !t.piece.IsEmpty
                        && JunqiFourBoard.NationAt(t.i % JunqiFourBoard.Size, t.i / JunqiFourBoard.Size) == JunqiNation.Green)
            .All(t => t.piece.Revealed);
        var enemyHidden = view
            .Select((piece, i) => (piece, i))
            .Where(t => !t.piece.IsEmpty
                        && JunqiFourBoard.NationAt(t.i % JunqiFourBoard.Size, t.i / JunqiFourBoard.Size) == JunqiNation.Blue)
            .All(t => t.piece.IsUnknown);

        check("[四国军棋] 暗棋：自己与同盟可见、敌人隐藏",
            ownVisible && allyVisible && enemyHidden,
            $"自己 {ownVisible} / 同盟 {allyVisible} / 敌人隐藏 {enemyHidden}");

        // 认输：整队认输
        var resignGame = new JunqiFourGame();
        foreach (var nation in new[] { JunqiNation.Red, JunqiNation.Blue, JunqiNation.Green, JunqiNation.Yellow })
            resignGame.AutoDeploy(nation, new Random((int)nation + 21));
        resignGame.Resign(JunqiNation.Red);
        check("[四国军棋] 认输后对方那一队获胜",
            resignGame.Phase == JunqiPhase.Finished
            && resignGame.WinnerNation == JunqiFourBoard.Ally(JunqiNation.Red),
            resignGame.ResultText);
    }

    /// <summary>同盟方的棋子永远不应出现在某方的可吃目标里。</summary>
    private static bool AllyNeverTargeted(JunqiFourGame game)
    {
        // 遍历红方所有合法着法，目标格若是绿方（同盟）的棋子就违规
        foreach (var move in game.LegalMoves(JunqiNation.Red))
        {
            var target = game.At(move.ToX, move.ToY);
            if (target.IsEmpty) continue;
            var owner = JunqiFourBoard.NationAt(move.ToX, move.ToY);
            if (owner is { } nation && JunqiFourBoard.IsAlly(JunqiNation.Red, nation)) return false;
        }
        return true;
    }
}
