using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.BoardGames.Gomoku;
using YuLiangToolkit.Tools.BoardGames.Shared;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;
using YuLiangToolkit.Tools.BoardGames.Xiangqi;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「棋牌」类别的自检（联机五子棋 + 联机象棋 + 房间网络层）。
///
/// <para>分区：注册（两个 ITool 与「棋牌」类别）· 五子棋（棋盘 / 禁手 / 悔棋 / 快照）·
/// 象棋（走法 / 将军 / 摆棋 / 让子 / 让先 / 记谱 / 快照）· 配置（自动落盘 + IBoardGameRoomConfig）·
/// 网络（帧格式 / 端点解析 / 在 127.0.0.1 用端口 0 真开房间跑一局）。</para>
///
/// <para>全部断言确定性、不联网（只回环）、不碰真实 Data/Config：自检进程的数据根在启动时
/// 已被 Program.cs 指向临时目录（<c>YLT_HOME</c>），网络监听跑完一律 Stop。</para>
/// </summary>
public static class BoardGamesChecks
{
    /// <summary>跑完所有「棋牌」断言。<paramref name="check"/> 带 detail；<paramref name="plain"/> 是简写。</summary>
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        CheckRegistry(check, plain);
        CheckGomoku(check, plain);
        CheckXiangqi(check, plain);
        CheckConfigs(check, plain);
        CheckNetwork(check, plain);
    }

    // ---------------------------------------------------------------- 注册（两个工具 + 「棋牌」类别）

    private static void CheckRegistry(Action<string, bool, string> check, Action<string, bool> plain)
    {
        var gomoku = new GomokuTool();
        var xiangqi = new XiangqiTool();
        ITool[] tools = { gomoku, xiangqi };

        // ---- 五子棋工具入口 ----
        check("[注册] 五子棋工具 Id 是 gomoku", gomoku.Id == "gomoku", gomoku.Id);
        check("[注册] 五子棋工具名是「联机五子棋」", gomoku.Name == "联机五子棋", gomoku.Name);
        check("[注册] 五子棋归入「棋牌」类别（CategoryPath）",
            gomoku.CategoryPath.SequenceEqual(new[] { "棋牌" }), string.Join("/", gomoku.CategoryPath));
        check("[注册] 五子棋 Category 也是「棋牌」", gomoku.Category == "棋牌", gomoku.Category);
        check("[注册] 五子棋 Order = 60", gomoku.Order == 60, gomoku.Order.ToString());
        check("[注册] 五子棋有图标、说明、标签",
            gomoku.EmojiIcon.Length > 0 && gomoku.Description.Length > 0 && gomoku.Tags.Count > 0,
            $"{gomoku.EmojiIcon} / {gomoku.Description} / {gomoku.Tags.Count} 个标签");
        check("[注册] 五子棋标签里能搜到「五子棋」「联机」「禁手」",
            gomoku.Tags.Contains("五子棋") && gomoku.Tags.Contains("联机") && gomoku.Tags.Contains("禁手"),
            string.Join(",", gomoku.Tags));

        // ---- 象棋工具入口 ----
        check("[注册] 象棋工具 Id 是 xiangqi", xiangqi.Id == "xiangqi", xiangqi.Id);
        check("[注册] 象棋工具名是「联机象棋」", xiangqi.Name == "联机象棋", xiangqi.Name);
        check("[注册] 象棋归入「棋牌」类别（CategoryPath）",
            xiangqi.CategoryPath.SequenceEqual(new[] { "棋牌" }), string.Join("/", xiangqi.CategoryPath));
        check("[注册] 象棋 Category 也是「棋牌」", xiangqi.Category == "棋牌", xiangqi.Category);
        check("[注册] 象棋 Order = 61", xiangqi.Order == 61, xiangqi.Order.ToString());
        check("[注册] 象棋有图标、说明、标签",
            xiangqi.EmojiIcon.Length > 0 && xiangqi.Description.Length > 0 && xiangqi.Tags.Count > 0,
            $"{xiangqi.EmojiIcon} / {xiangqi.Description} / {xiangqi.Tags.Count} 个标签");
        check("[注册] 象棋标签里能搜到「象棋」「联机」「让子」「让先」",
            xiangqi.Tags.Contains("象棋") && xiangqi.Tags.Contains("联机")
            && xiangqi.Tags.Contains("让子") && xiangqi.Tags.Contains("让先"),
            string.Join(",", xiangqi.Tags));

        // 两个工具不能撞 Id（Data/Config 按 Id 分文件夹，撞了会互相覆盖）
        check("[注册] 两个工具 Id 不重复",
            tools.Select(t => t.Id).Distinct().Count() == 2,
            string.Join(",", tools.Select(t => t.Id)));

        // ---- 「棋牌」类别元数据 ----
        check("[注册] ShellCategories 里定义了「棋牌」类别名",
            ShellCategories.BoardGames == "棋牌", ShellCategories.BoardGames);
        var category = ShellCategories.All.FirstOrDefault(c => c.Path.SequenceEqual(new[] { "棋牌" }));
        check("[注册] ShellCategories.All 里有「棋牌」类别", category is not null,
            string.Join(" / ", ShellCategories.All.Select(c => string.Join("›", c.Path))));
        if (category is not null)
        {
            check("[注册] 「棋牌」类别图标是 🎲", category.EmojiIcon == "🎲", category.EmojiIcon);
            check("[注册] 「棋牌」类别 Order = 20", category.Order == 20, category.Order.ToString());
            check("[注册] 「棋牌」类别有说明文案", category.Description.Length > 0, category.Description);
        }

        // ---- 建树：两个工具都收进「棋牌」节点，主页根层不再直接平铺它们 ----
        var root = ToolCatalog.Build(tools, ShellCategories.All);
        var boardNode = root.FindByPath(new[] { "棋牌" });
        check("[注册] 目录树里有「棋牌」节点", boardNode is not null,
            string.Join(" / ", root.Children.Select(c => c.Name)));
        if (boardNode is not null)
        {
            check("[注册] 「棋牌」节点下挂着两个工具（按 Order 排：五子棋在前）",
                boardNode.Tools.Select(t => t.Id).SequenceEqual(new[] { "gomoku", "xiangqi" }),
                string.Join(",", boardNode.Tools.Select(t => t.Id)));
            check("[注册] 「棋牌」节点递归计数 = 2", boardNode.ToolCount == 2, boardNode.ToolCount.ToString());
        }
        check("[注册] 两个工具都不再挂在主页根层", root.Tools.Count == 0,
            string.Join(",", root.Tools.Select(t => t.Name)));
        check("[注册] 目录树总工具数 = 2（没有漏挂）", root.ToolCount == 2, root.ToolCount.ToString());
    }

    // ---------------------------------------------------------------- 五子棋（棋盘 / 落子 / 胜负 / 禁手 / 悔棋 / 快照 / 文案）

    private static void CheckGomoku(Action<string, bool, string> checkWithDetail, Action<string, bool> plain)
    {
        // 断言多的时候不写 detail 更清爽（Program.cs 传进来的两个委托语义不变）
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);

        // ===== 1. 棋盘本身 =====
        var board = new GomokuBoard();
        check("[五子棋] 默认棋盘是 15 路", board.Size == 15 && GomokuBoard.DefaultSize == 15, board.Size.ToString());
        check("[五子棋] 19 路棋盘能建出来", new GomokuBoard(19).Size == 19);
        var badSizeThrew = false;
        try { _ = new GomokuBoard(GomokuBoard.MinSize - 1); } catch (ArgumentOutOfRangeException) { badSizeThrew = true; }
        check("[五子棋] 路数越界抛 ArgumentOutOfRangeException（不是静默建错棋盘）", badSizeThrew);
        check("[五子棋] 新棋盘：225 个空点、没有棋子、没有棋谱",
            board.Count(GomokuCell.Empty) == 225 && board.Count(GomokuCell.Black) == 0
            && board.Count(GomokuCell.White) == 0 && board.Moves.Count == 0,
            $"空 {board.Count(GomokuCell.Empty)} / 棋谱 {board.Moves.Count}");
        check("[五子棋] InBounds 边界判断正确",
            board.InBounds(0, 0) && board.InBounds(14, 14) && !board.InBounds(15, 0)
            && !board.InBounds(0, 15) && !board.InBounds(-1, 0), "");
        check("[五子棋] Get 越界返回 Empty（沿方向扫描不用先判边界）",
            board.Get(15, 0) == GomokuCell.Empty && board.Get(-1, 3) == GomokuCell.Empty);
        var setThrew = false;
        try { board.Set(15, 0, GomokuCell.Black); } catch (ArgumentOutOfRangeException) { setThrew = true; }
        check("[五子棋] Set 越界抛异常", setThrew);
        board.Set(3, 3, GomokuCell.Black);
        board.Set(4, 4, GomokuCell.White);
        check("[五子棋] Set/Get/IsEmpty 正常读写",
            board.Get(3, 3) == GomokuCell.Black && board.Get(4, 4) == GomokuCell.White
            && !board.IsEmpty(3, 3) && board.IsEmpty(5, 5)
            && board.Count(GomokuCell.Black) == 1 && board.Count(GomokuCell.White) == 1, "");
        check("[五子棋] IsEmpty 对越界点返回 false（越界视作不空）", !board.IsEmpty(15, 15) && !board.IsEmpty(-2, 0));

        var boardWithMoves = new GomokuGame();
        boardWithMoves.Play(GomokuCell.Black, 7, 7);
        boardWithMoves.Play(GomokuCell.White, 8, 8);
        var cloned = boardWithMoves.Board.Clone();
        cloned.Set(7, 7, GomokuCell.Empty);
        cloned.Set(9, 9, GomokuCell.White);
        check("[五子棋] Clone 是深拷贝：改副本不影响原棋盘",
            boardWithMoves.Board.Get(7, 7) == GomokuCell.Black
            && boardWithMoves.Board.Get(9, 9) == GomokuCell.Empty, "");
        check("[五子棋] Clone 连棋谱一起复制（副本改棋谱不影响原局）",
            cloned.Moves.Count == boardWithMoves.Board.Moves.Count && cloned.Moves.Count == 2,
            $"{cloned.Moves.Count} / {boardWithMoves.Board.Moves.Count}");

        // ===== 2. 落子与轮次 =====
        var game = new GomokuGame();
        check("[五子棋] 黑先：开局轮次是黑棋", game.Turn == GomokuCell.Black);
        check("[五子棋] 开局已开始、结果进行中、没有最后一手",
            game.Started && !game.IsOver && game.Result == GomokuResult.Playing
            && game.LastMove is null && game.MoveCount == 0, game.ResultText);
        var playError = game.Play(GomokuCell.Black, 7, 7);
        check("[五子棋] 合法落子返回 null（成功）", playError is null, playError ?? "");
        check("[五子棋] 落子后棋盘上多了黑棋", game.Board.Get(7, 7) == GomokuCell.Black);
        check("[五子棋] 落子后轮到白棋", game.Turn == GomokuCell.White);
        check("[五子棋] 手数 / 最后一手同步更新",
            game.MoveCount == 1 && game.LastMove is { X: 7, Y: 7, Number: 1, Player: "black" },
            game.LastMove?.ToString() ?? "");
        var repeatError = game.Play(GomokuCell.White, 7, 7);
        check("[五子棋] 重复落子被拒并说明原因（含「已经」）",
            repeatError is { Length: > 0 } && repeatError.Contains("已经"), repeatError ?? "(null)");
        check("[五子棋] 重复落子不改变手数与轮次", game.MoveCount == 1 && game.Turn == GomokuCell.White);
        var outOfBoundsError = game.Play(GomokuCell.White, 15, 0);
        check("[五子棋] 越界落子被拒（含「超出」）",
            outOfBoundsError is { Length: > 0 } && outOfBoundsError.Contains("超出"), outOfBoundsError ?? "(null)");
        var wrongTurnError = game.Play(GomokuCell.Black, 8, 8);
        check("[五子棋] 没轮到的一方落子被拒（含「轮」）",
            wrongTurnError is { Length: > 0 } && wrongTurnError.Contains("轮"), wrongTurnError ?? "(null)");
        var invalidPlayerError = game.Play(GomokuCell.Empty, 0, 0);
        check("[五子棋] 无效落子方（Empty）被拒（含「无效」）",
            invalidPlayerError is { Length: > 0 } && invalidPlayerError.Contains("无效"), invalidPlayerError ?? "(null)");

        var notStarted = new GomokuGame { Started = false };
        var notStartedError = notStarted.Play(GomokuCell.Black, 7, 7);
        check("[五子棋] 对局没开始（房间等主机开始）时不能落子",
            notStartedError is { Length: > 0 } && notStartedError.Contains("开始"), notStartedError ?? "(null)");

        // ===== 3. 胜负判定（四个方向 + 白棋 + 长连） =====
        static bool PlayMoves(GomokuGame target, params (int X, int Y)[] points)
        {
            for (var i = 0; i < points.Length; i++)
            {
                var player = i % 2 == 0 ? GomokuCell.Black : GomokuCell.White;
                if (target.Play(player, points[i].X, points[i].Y) is not null) return false;
            }

            return true;
        }

        var horizontal = new GomokuGame();
        var horizontalOk = PlayMoves(horizontal, (0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1), (3, 0), (3, 1), (4, 0));
        check("[五子棋] 横向五连：黑棋获胜", horizontalOk && horizontal.Result == GomokuResult.BlackWin,
            horizontal.ResultText);
        check("[五子棋] 获胜连线 5 颗且都真的是黑棋",
            horizontal.WinningLine.Count == 5
            && horizontal.WinningLine.All(p => horizontal.Board.Get(p.X, p.Y) == GomokuCell.Black),
            string.Join(",", horizontal.WinningLine.Select(p => $"({p.X},{p.Y})")));
        check("[五子棋] 结果文案「黑棋获胜」", horizontal.ResultText == "黑棋获胜", horizontal.ResultText);
        check("[五子棋] 胜负已分后不能再落子（含「结束」）",
            horizontal.Play(GomokuCell.White, 10, 10) is { } endError && endError.Contains("结束"),
            horizontal.Play(GomokuCell.White, 10, 10) ?? "(null)");

        var vertical = new GomokuGame();
        var verticalOk = PlayMoves(vertical, (0, 0), (1, 0), (0, 1), (1, 1), (0, 2), (1, 2), (0, 3), (1, 3), (0, 4));
        check("[五子棋] 竖向五连：黑棋获胜", verticalOk && vertical.Result == GomokuResult.BlackWin, vertical.ResultText);

        var diagonalDown = new GomokuGame();
        var diagonalDownOk = PlayMoves(diagonalDown,
            (0, 0), (10, 0), (1, 1), (10, 1), (2, 2), (10, 2), (3, 3), (10, 3), (4, 4));
        check("[五子棋] 斜向（左上→右下）五连：黑棋获胜",
            diagonalDownOk && diagonalDown.Result == GomokuResult.BlackWin, diagonalDown.ResultText);

        var diagonalUp = new GomokuGame();
        var diagonalUpOk = PlayMoves(diagonalUp,
            (0, 4), (10, 0), (1, 3), (10, 1), (2, 2), (10, 2), (3, 1), (10, 3), (4, 0));
        check("[五子棋] 斜向（左下→右上）五连：黑棋获胜",
            diagonalUpOk && diagonalUp.Result == GomokuResult.BlackWin, diagonalUp.ResultText);

        var whiteWin = new GomokuGame();
        var whiteWinOk = PlayMoves(whiteWin,
            (0, 0), (0, 14), (2, 0), (1, 14), (4, 0), (2, 14), (6, 0), (3, 14), (8, 0), (4, 14));
        check("[五子棋] 白棋五连：白棋获胜", whiteWinOk && whiteWin.Result == GomokuResult.WhiteWin,
            whiteWin.ResultText);
        check("[五子棋] 白胜结果文案是「白棋获胜」", whiteWin.ResultText == "白棋获胜", whiteWin.ResultText);
        check("[五子棋] 白棋获胜连线也是 5 颗",
            whiteWin.WinningLine.Count == 5
            && whiteWin.WinningLine.All(p => whiteWin.Board.Get(p.X, p.Y) == GomokuCell.White), "");

        // 5×5 真下满且没有五连 → 和棋（用「棋盘上恒无五连」的 13 黑 12 白交替图案）
        var drawGame = new GomokuGame(5);
        var blackCells = new List<(int X, int Y)>();
        var whiteCells = new List<(int X, int Y)>();
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 5; x++)
            {
                if ((2 * y + x) % 4 < 2) blackCells.Add((x, y));
                else whiteCells.Add((x, y));
            }
        }

        var drawOk = true;
        for (var i = 0; i < 25 && drawOk; i++)
        {
            var player = i % 2 == 0 ? GomokuCell.Black : GomokuCell.White;
            var point = player == GomokuCell.Black ? blackCells[i / 2] : whiteCells[i / 2];
            drawOk = drawGame.Play(player, point.X, point.Y) is null;
        }

        check("[五子棋] 棋盘下满且没人成五 → 和棋（在 5×5 上真下满 25 手）",
            drawOk && drawGame.Result == GomokuResult.Draw && drawGame.MoveCount == 25,
            $"ok={drawOk} result={drawGame.Result} moves={drawGame.MoveCount}");
        check("[五子棋] 和棋文案是「和棋」", drawGame.ResultText == "和棋", drawGame.ResultText);

        // ===== 4. 禁手（长连 / 四四 / 三三 / 五连优先 / 白棋无禁手） =====
        static GomokuBoard BoardOf(params (int X, int Y, GomokuCell C)[] stones)
        {
            var result = new GomokuBoard();
            foreach (var (x, y, cell) in stones) result.Set(x, y, cell);
            return result;
        }

        static string CellsOf(int size, params (int X, int Y, char C)[] stones)
        {
            var cells = new char[size * size];
            Array.Fill(cells, '.');
            foreach (var (x, y, c) in stones) cells[y * size + x] = c;
            return new string(cells);
        }

        static GomokuGame GameOf(string cells, string turn = "black", bool forbidden = false, bool started = true)
        {
            var (restored, error) = GomokuGame.FromSnapshot(new GomokuSnapshot
            {
                Size = cells.Length == 361 ? 19 : 15,
                Cells = cells,
                Turn = turn,
                Result = "playing",
                Started = started,
                ForbiddenEnabled = forbidden,
            });
            return restored ?? throw new InvalidOperationException("快照摆棋失败：" + error);
        }

        // 关闭禁手时：长连也算赢（必须「一手补成 6 子」，否则补到第 5 子时就已经赢了）
        var longWin = GameOf(CellsOf(15,
            (0, 0, 'b'), (1, 0, 'b'), (2, 0, 'b'), (4, 0, 'b'), (5, 0, 'b')));
        var longWinError = longWin.Play(GomokuCell.Black, 3, 0);
        check("[五子棋] 禁手关：黑棋长连（一手补成 6 子）也算赢",
            longWinError is null && longWin.Result == GomokuResult.BlackWin
            && longWin.WinningLine.Count >= 6, $"{longWin.ResultText} / 连线 {longWin.WinningLine.Count}");

        // 关闭禁手时：白棋长连也算赢
        var whiteLong = GameOf(CellsOf(15,
            (0, 0, 'w'), (1, 0, 'w'), (2, 0, 'w'), (3, 0, 'w'), (4, 0, 'w')), turn: "white");
        var whiteLongError = whiteLong.Play(GomokuCell.White, 5, 0);
        check("[五子棋] 禁手关：白棋长连（6 子）也算赢",
            whiteLongError is null && whiteLong.Result == GomokuResult.WhiteWin
            && whiteLong.WinningLine.Count >= 6, whiteLong.ResultText);

        // 开启禁手时：黑棋长连判负
        var overlineGame = GameOf(CellsOf(15,
            (0, 0, 'b'), (1, 0, 'b'), (2, 0, 'b'), (3, 0, 'b'), (4, 0, 'b')), forbidden: true);
        var overlineError = overlineGame.Play(GomokuCell.Black, 5, 0);
        check("[五子棋] 禁手开：黑棋长连（6 子）立即判负（白棋获胜）",
            overlineError is null && overlineGame.Result == GomokuResult.WhiteWin,
            overlineGame.ResultText);
        check("[五子棋] 禁手判负会记下「禁手点」（界面标红用）",
            overlineGame.LastForbiddenPoint is { X: 5, Y: 0 }, overlineGame.LastForbiddenPoint?.ToString() ?? "");
        check("[五子棋] 禁手判负后没有获胜连线（不是黑棋赢了）", overlineGame.WinningLine.Count == 0);
        check("[五子棋] 禁手判负的那一手仍然摆在棋盘上（手数 +1）",
            overlineGame.Board.Get(5, 0) == GomokuCell.Black && overlineGame.MoveCount == 1,
            $"moves={overlineGame.MoveCount}");

        // 开启禁手时：黑棋正好五连永远算赢（五连优先于长连）
        var exactlyFive = GameOf(CellsOf(15,
            (0, 0, 'b'), (1, 0, 'b'), (2, 0, 'b'), (3, 0, 'b')), forbidden: true);
        var exactlyFiveError = exactlyFive.Play(GomokuCell.Black, 4, 0);
        check("[五子棋] 禁手开：黑棋正好五连算赢（不判禁手）",
            exactlyFiveError is null && exactlyFive.Result == GomokuResult.BlackWin
            && exactlyFive.LastForbiddenPoint is null, exactlyFive.ResultText);

        var priorityGame = GameOf(CellsOf(15,
            (0, 0, 'b'), (1, 0, 'b'), (2, 0, 'b'), (3, 0, 'b'),
            (4, 1, 'b'), (4, 2, 'b'), (4, 3, 'b'), (4, 4, 'b'), (4, 5, 'b')), forbidden: true);
        var priorityError = priorityGame.Play(GomokuCell.Black, 4, 0);
        check("[五子棋] 禁手开：五连优先于长连（横向正好五 + 纵向六子 → 黑胜）",
            priorityError is null && priorityGame.Result == GomokuResult.BlackWin, priorityGame.ResultText);
        check("[五子棋] 五连优先时获胜连线就是那 5 颗横向子",
            priorityGame.WinningLine.Count == 5 && priorityGame.WinningLine.All(p => p.Y == 0),
            string.Join(",", priorityGame.WinningLine.Select(p => $"({p.X},{p.Y})")));

        // 禁手判定纯函数：Evaluate / FindForbiddenPoints
        var evaluateDisabled = GomokuForbidden.Evaluate(
            BoardOf((0, 0, GomokuCell.Black), (1, 0, GomokuCell.Black), (2, 0, GomokuCell.Black),
                (3, 0, GomokuCell.Black), (4, 0, GomokuCell.Black)), 5, 0, false);
        check("[五子棋] 禁手关时 Evaluate 永远返回「不是禁手」",
            !evaluateDisabled.IsForbidden && !evaluateDisabled.IsWin
            && evaluateDisabled.Kind == GomokuForbiddenKind.None, evaluateDisabled.Reason);

        var evaluateOccupied = GomokuForbidden.Evaluate(
            BoardOf((3, 0, GomokuCell.Black), (4, 0, GomokuCell.Black)), 4, 0, true);
        check("[五子棋] Evaluate 对已有棋子的点返回「不是禁手」（不抛异常）",
            !evaluateOccupied.IsForbidden && evaluateOccupied.Kind == GomokuForbiddenKind.None);

        var overlineInfo = GomokuForbidden.Evaluate(
            BoardOf((0, 0, GomokuCell.Black), (1, 0, GomokuCell.Black), (2, 0, GomokuCell.Black),
                (3, 0, GomokuCell.Black), (4, 0, GomokuCell.Black)), 5, 0, true);
        check("[五子棋] 长连禁手识别为 Overline，并给出中文原因（含「长连」）",
            overlineInfo.IsForbidden && overlineInfo.Kind == GomokuForbiddenKind.Overline
            && overlineInfo.Reason.Contains("长连"), overlineInfo.Reason);

        var doubleThreeBoard = BoardOf(
            (5, 7, GomokuCell.Black), (6, 7, GomokuCell.Black),
            (7, 5, GomokuCell.Black), (7, 6, GomokuCell.Black));
        var doubleThreeInfo = GomokuForbidden.Evaluate(doubleThreeBoard, 7, 7, true);
        check("[五子棋] 三三禁手识别为 DoubleThree（两个方向各一个活三）",
            doubleThreeInfo.IsForbidden && doubleThreeInfo.Kind == GomokuForbiddenKind.DoubleThree
            && doubleThreeInfo.OpenThreeCount >= 2,
            $"{doubleThreeInfo.Kind} 活三={doubleThreeInfo.OpenThreeCount}");

        var doubleFourBoard = BoardOf(
            (4, 7, GomokuCell.Black), (5, 7, GomokuCell.Black), (6, 7, GomokuCell.Black),
            (7, 4, GomokuCell.Black), (7, 5, GomokuCell.Black), (7, 6, GomokuCell.Black));
        var doubleFourInfo = GomokuForbidden.Evaluate(doubleFourBoard, 7, 7, true);
        check("[五子棋] 四四禁手识别为 DoubleFour（两个方向各一个四）",
            doubleFourInfo.IsForbidden && doubleFourInfo.Kind == GomokuForbiddenKind.DoubleFour
            && doubleFourInfo.FourCount >= 2,
            $"{doubleFourInfo.Kind} 四={doubleFourInfo.FourCount}");

        check("[五子棋] 「一定是禁手」与「一定是赢」不会同时成立",
            new[] { overlineInfo, doubleThreeInfo, doubleFourInfo }
                .All(i => !(i.IsForbidden && i.IsWin)),
            string.Join(" / ", new[] { overlineInfo, doubleThreeInfo, doubleFourInfo }.Select(i => i.Kind)));

        var fivePriorityInfo = GomokuForbidden.Evaluate(
            BoardOf((0, 0, GomokuCell.Black), (1, 0, GomokuCell.Black), (2, 0, GomokuCell.Black),
                (3, 0, GomokuCell.Black), (4, 1, GomokuCell.Black), (4, 2, GomokuCell.Black),
                (4, 3, GomokuCell.Black), (4, 4, GomokuCell.Black), (4, 5, GomokuCell.Black)),
            4, 0, true);
        check("[五子棋] Evaluate 里五连优先：正好五连 → IsWin（不是禁手）",
            fivePriorityInfo.IsWin && !fivePriorityInfo.IsForbidden, fivePriorityInfo.Reason);

        check("[五子棋] FindForbiddenPoints 在禁手关时返回空表",
            GomokuForbidden.FindForbiddenPoints(doubleThreeBoard, false).Count == 0);
        var forbiddenPoints = GomokuForbidden.FindForbiddenPoints(doubleThreeBoard, true);
        check("[五子棋] FindForbiddenPoints 找出三三禁手点（包含 (7,7)）",
            forbiddenPoints.Contains(new GomokuPoint(7, 7)),
            string.Join(",", forbiddenPoints.Select(p => $"({p.X},{p.Y})")));
        check("[五子棋] FindForbiddenPoints 只返回空点（不会标已有棋子的位置）",
            forbiddenPoints.All(p => doubleThreeBoard.IsEmpty(p.X, p.Y)), "");

        var doubleThreeGame = GameOf(CellsOf(15,
            (5, 7, 'b'), (6, 7, 'b'), (7, 5, 'b'), (7, 6, 'b')), forbidden: true);
        var doubleThreeError = doubleThreeGame.Play(GomokuCell.Black, 7, 7);
        check("[五子棋] 三三禁手在棋局里判黑棋负",
            doubleThreeError is null && doubleThreeGame.Result == GomokuResult.WhiteWin
            && doubleThreeGame.LastForbiddenPoint is { X: 7, Y: 7 }, doubleThreeGame.ResultText);

        var doubleFourGame = GameOf(CellsOf(15,
            (4, 7, 'b'), (5, 7, 'b'), (6, 7, 'b'),
            (7, 4, 'b'), (7, 5, 'b'), (7, 6, 'b')), forbidden: true);
        var doubleFourError = doubleFourGame.Play(GomokuCell.Black, 7, 7);
        check("[五子棋] 四四禁手在棋局里判黑棋负",
            doubleFourError is null && doubleFourGame.Result == GomokuResult.WhiteWin
            && doubleFourGame.LastForbiddenPoint is { X: 7, Y: 7 }, doubleFourGame.ResultText);

        // 白棋没有禁手：同样形状换成白棋，落下后照样算赢（长连）
        var whiteNoForbidden = GameOf(CellsOf(15,
            (0, 0, 'w'), (1, 0, 'w'), (2, 0, 'w'), (3, 0, 'w'), (4, 0, 'w')), turn: "white", forbidden: true);
        var whiteNoForbiddenError = whiteNoForbidden.Play(GomokuCell.White, 5, 0);
        check("[五子棋] 白棋没有禁手：白棋长连照样算赢",
            whiteNoForbiddenError is null && whiteNoForbidden.Result == GomokuResult.WhiteWin
            && whiteNoForbidden.LastForbiddenPoint is null, whiteNoForbidden.ResultText);

        // ===== 5. 悔棋 =====
        var undoGame = new GomokuGame();
        PlayMoves(undoGame, (7, 7), (8, 8));
        var undoOnce = undoGame.Undo();
        check("[五子棋] 悔棋撤掉最后一手（轮到白棋重下、棋盘那一格空了）",
            undoOnce && undoGame.MoveCount == 1 && undoGame.Turn == GomokuCell.White
            && undoGame.Board.IsEmpty(8, 8), $"moves={undoGame.MoveCount} turn={undoGame.Turn}");
        var undoTwice = undoGame.Undo();
        check("[五子棋] 再悔一手回到空盘、黑棋回合",
            undoTwice && undoGame.MoveCount == 0 && undoGame.Turn == GomokuCell.Black
            && undoGame.Board.IsEmpty(7, 7), "");
        check("[五子棋] 空棋盘悔棋返回 false（不崩）", !undoGame.Undo());

        var undoWin = new GomokuGame();
        PlayMoves(undoWin, (0, 0), (0, 1), (1, 0), (1, 1), (2, 0), (2, 1), (3, 0), (3, 1), (4, 0));
        var undoWinMoves = undoWin.MoveCount;
        var undoWinOk = undoWin.Undo();
        check("[五子棋] 胜利后悔棋：结果恢复「进行中」、获胜连线清空、棋子被拿掉",
            undoWinOk && undoWin.Result == GomokuResult.Playing && undoWin.WinningLine.Count == 0
            && undoWin.Board.IsEmpty(4, 0) && undoWin.MoveCount == undoWinMoves - 1,
            $"result={undoWin.Result} line={undoWin.WinningLine.Count} moves={undoWin.MoveCount}");

        var undoToBlack = new GomokuGame();
        PlayMoves(undoToBlack, (0, 0), (0, 1));
        check("[五子棋] 联机悔棋（黑请求）：撤 2 步回到「轮到黑棋」",
            undoToBlack.UndoToTurn(GomokuCell.Black) && undoToBlack.MoveCount == 0
            && undoToBlack.Turn == GomokuCell.Black, $"moves={undoToBlack.MoveCount} turn={undoToBlack.Turn}");

        var undoToWhite = new GomokuGame();
        PlayMoves(undoToWhite, (0, 0), (0, 1));
        check("[五子棋] 联机悔棋（白请求）：只撤 1 步（白棋刚走的那手）",
            undoToWhite.UndoToTurn(GomokuCell.White) && undoToWhite.MoveCount == 1
            && undoToWhite.Turn == GomokuCell.White, $"moves={undoToWhite.MoveCount} turn={undoToWhite.Turn}");

        check("[五子棋] 空棋局联机悔棋返回 false", !new GomokuGame().UndoToTurn(GomokuCell.Black));
        var undoNotYet = new GomokuGame();
        undoNotYet.Play(GomokuCell.Black, 0, 0);
        check("[五子棋] 对方还没下过子时不能「悔到对方该走」",
            !undoNotYet.UndoToTurn(GomokuCell.White) && undoNotYet.MoveCount == 1);

        // ===== 6. 快照往返 =====
        var snapGame = new GomokuGame(19, forbiddenEnabled: true);
        PlayMoves(snapGame, (0, 0), (1, 1), (18, 18));
        var snapshot = snapGame.ToSnapshot();
        check("[五子棋] 快照：大小 / 禁手开关 / 轮次 / 手数齐全",
            snapshot.Size == 19 && snapshot.ForbiddenEnabled && snapshot.Started
            && snapshot.Turn == "white" && snapshot.Result == "playing" && snapshot.Moves.Count == 3,
            $"size={snapshot.Size} turn={snapshot.Turn} moves={snapshot.Moves.Count}");
        check("[五子棋] 快照：cells 长度 = 路数²，按「从左到右、从上到下」索引",
            snapshot.Cells.Length == 19 * 19
            && snapshot.Cells[0] == 'b'
            && snapshot.Cells[1 * 19 + 1] == 'w'
            && snapshot.Cells[18 * 19 + 18] == 'b',
            snapshot.Cells[..Math.Min(40, snapshot.Cells.Length)]);

        var (restoredGame, restoreError) = GomokuGame.FromSnapshot(snapshot);
        check("[五子棋] 快照往返成功（没有错误）", restoredGame is not null && restoreError.Length == 0,
            restoreError);
        check("[五子棋] 往返后大小 / 禁手 / 轮次 / 手数 / 棋子都一致",
            restoredGame is { Size: 19, ForbiddenEnabled: true, Turn: GomokuCell.White, MoveCount: 3 }
            && restoredGame.Board.Get(18, 18) == GomokuCell.Black
            && restoredGame.LastMove is { X: 18, Y: 18, Player: "black" },
            $"turn={restoredGame?.Turn} moves={restoredGame?.MoveCount}");
        check("[五子棋] 往返后能正常接着下（轮次没乱）",
            restoredGame is not null && restoredGame.Play(GomokuCell.White, 5, 5) is null
            && restoredGame.MoveCount == 4 && restoredGame.Turn == GomokuCell.Black, "");

        var (nullSnapGame, nullSnapError) = GomokuGame.FromSnapshot(null);
        check("[五子棋] 空快照 → 返回中文错误、不崩溃",
            nullSnapGame is null && nullSnapError.Length > 0, nullSnapError);
        var (shortCellsGame, shortCellsError) = GomokuGame.FromSnapshot(new GomokuSnapshot
        {
            Size = 15,
            Cells = "...",
            Turn = "black",
        });
        check("[五子棋] cells 长度不对 → 中文错误（含「长度」）",
            shortCellsGame is null && shortCellsError.Contains("长度"), shortCellsError);
        var (badCharGame, badCharError) = GomokuGame.FromSnapshot(new GomokuSnapshot
        {
            Size = 5,
            Cells = "x...." + new string('.', 20),
            Turn = "black",
        });
        check("[五子棋] cells 有非法字符 → 中文错误（说明是哪个字符）",
            badCharGame is null && badCharError.Contains("x"), badCharError);
        var (badSizeGame, badSizeError) = GomokuGame.FromSnapshot(new GomokuSnapshot
        {
            Size = GomokuBoard.MinSize - 1,
            Cells = "",
            Turn = "black",
        });
        check("[五子棋] 快照里棋盘大小非法 → 中文错误",
            badSizeGame is null && badSizeError.Length > 0, badSizeError);

        var winSnapshot = horizontal.ToSnapshot();
        check("[五子棋] 快照保留结果与获胜连线",
            winSnapshot.Result == "blackWin" && winSnapshot.WinningLine is { Count: 5 },
            $"{winSnapshot.Result} / {winSnapshot.WinningLine?.Count}");
        var (winRestored, winRestoreError) = GomokuGame.FromSnapshot(winSnapshot);
        check("[五子棋] 胜局往返后仍是「黑棋获胜」，且不能再落子",
            winRestored is { Result: GomokuResult.BlackWin } && winRestored.IsOver
            && winRestored.Play(GomokuCell.White, 10, 10) is not null,
            winRestoreError);

        var overlineSnapshot = overlineGame.ToSnapshot();
        var (overlineRestored, overlineRestoreError) = GomokuGame.FromSnapshot(overlineSnapshot);
        check("[五子棋] 快照保留禁手判负点与结果",
            overlineSnapshot.Result == "whiteWin"
            && overlineSnapshot.LastForbiddenPoint is { X: 5, Y: 0 }
            && overlineRestored?.LastForbiddenPoint is { X: 5, Y: 0 },
            overlineRestoreError);

        var waitingSnapshot = new GomokuSnapshot
        {
            Size = 15,
            Cells = CellsOf(15),
            Turn = "black",
            Result = "playing",
            Started = false,
        };
        var (waitingRestored, waitingError) = GomokuGame.FromSnapshot(waitingSnapshot);
        check("[五子棋] 快照保留「还没开始」（房间里等主机开始）",
            waitingRestored is { Started: false } && waitingRestored.Play(GomokuCell.Black, 7, 7) is not null,
            waitingError);

        var guessSnapshot = new GomokuSnapshot
        {
            Size = 15,
            Cells = CellsOf(15, (0, 0, 'b')),
            Turn = "",                       // 故意写坏：按手数奇偶猜
            Moves = { new GomokuMove(0, 0, "black", 1) },
        };
        var (guessGame, _) = GomokuGame.FromSnapshot(guessSnapshot);
        check("[五子棋] 快照轮次缺失时按手数奇偶猜（1 手 = 轮到白棋）",
            guessGame?.Turn == GomokuCell.White, guessGame?.Turn.ToString() ?? "");

        var resetGame = new GomokuGame();
        PlayMoves(resetGame, (0, 0), (0, 1));
        resetGame.Started = false;
        resetGame.Reset(19, forbiddenEnabled: true);
        check("[五子棋] 重置：换大小 / 禁手、清空棋盘与棋谱、黑先、进行中",
            resetGame.Size == 19 && resetGame.Board.Size == 19 && resetGame.ForbiddenEnabled
            && resetGame.MoveCount == 0 && resetGame.Turn == GomokuCell.Black
            && resetGame.Result == GomokuResult.Playing && resetGame.Board.Count(GomokuCell.Black) == 0,
            $"size={resetGame.Size} turn={resetGame.Turn}");
        check("[五子棋] 重置不清「是否已开始」（由调用方决定等待 / 开局）", !resetGame.Started);

        // ===== 7. 坐标 / 棋谱 / 结果文案 =====
        check("[五子棋] 坐标文案：15 路四角与天元",
            GomokuText.Coordinate(0, 14, 15) == "A1" && GomokuText.Coordinate(14, 14, 15) == "O1"
            && GomokuText.Coordinate(0, 0, 15) == "A15" && GomokuText.Coordinate(7, 7, 15) == "H8",
            $"{GomokuText.Coordinate(0, 14, 15)} / {GomokuText.Coordinate(7, 7, 15)}");
        check("[五子棋] 坐标文案：19 路右下角是 S19", GomokuText.Coordinate(18, 0, 19) == "S19",
            GomokuText.Coordinate(18, 0, 19));
        check("[五子棋] 坐标越界给 ?",
            GomokuText.Coordinate(-1, 0, 15) == "?" && GomokuText.Coordinate(15, 0, 15) == "?");
        check("[五子棋] DescribeMove：黑 H8",
            GomokuText.DescribeMove(new GomokuMove(7, 7, "black", 12), 15) == "黑 H8",
            GomokuText.DescribeMove(new GomokuMove(7, 7, "black", 12), 15));
        check("[五子棋] DescribeMove：白 A1",
            GomokuText.DescribeMove(new GomokuMove(0, 14, "white", 13), 15) == "白 A1",
            GomokuText.DescribeMove(new GomokuMove(0, 14, "white", 13), 15));
        check("[五子棋] MoveLine：1. 黑 H8",
            GomokuText.MoveLine(new GomokuMove(7, 7, "black", 1), 15) == "1. 黑 H8",
            GomokuText.MoveLine(new GomokuMove(7, 7, "black", 1), 15));
        check("[五子棋] MoveLine：9. 白 A1",
            GomokuText.MoveLine(new GomokuMove(0, 14, "white", 9), 15) == "9. 白 A1",
            GomokuText.MoveLine(new GomokuMove(0, 14, "white", 9), 15));
        check("[五子棋] SideKey / ParseSide 往返（black / white / 空 / 中文别名）",
            GomokuText.SideKey(GomokuCell.Black) == "black" && GomokuText.SideKey(GomokuCell.White) == "white"
            && GomokuText.ParseSide("black") == GomokuCell.Black && GomokuText.ParseSide("WHITE") == GomokuCell.White
            && GomokuText.ParseSide("黑棋") == GomokuCell.Black && GomokuText.ParseSide("白") == GomokuCell.White
            && GomokuText.ParseSide("") == GomokuCell.Empty && GomokuText.ParseSide("xyz") == GomokuCell.Empty, "");
        check("[五子棋] ResultKey / ParseResult 往返（4 种结果，大小写不敏感）",
            GomokuText.ResultKey(GomokuResult.BlackWin) == "blackWin"
            && GomokuText.ResultKey(GomokuResult.WhiteWin) == "whiteWin"
            && GomokuText.ResultKey(GomokuResult.Draw) == "draw"
            && GomokuText.ResultKey(GomokuResult.Playing) == "playing"
            && GomokuText.ParseResult("blackWin") == GomokuResult.BlackWin
            && GomokuText.ParseResult("WHITEWIN") == GomokuResult.WhiteWin
            && GomokuText.ParseResult("draw") == GomokuResult.Draw
            && GomokuText.ParseResult("乱写") == GomokuResult.Playing, "");
        check("[五子棋] ResultText 四种文案",
            GomokuText.ResultText(GomokuResult.BlackWin) == "黑棋获胜"
            && GomokuText.ResultText(GomokuResult.WhiteWin) == "白棋获胜"
            && GomokuText.ResultText(GomokuResult.Draw) == "和棋"
            && GomokuText.ResultText(GomokuResult.Playing) == "对局进行中", "");
        check("[五子棋] 手数文案", GomokuText.MoveCountText(3) == "3 手", GomokuText.MoveCountText(3));
    }

    // ---------------------------------------------------------------- 象棋（棋子 / FEN / 走法 / 将军 / 摆棋 / 让子 / 让先 / 记谱 / 快照）

    private static void CheckXiangqi(Action<string, bool, string> checkWithDetail, Action<string, bool> plain)
    {
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);

        // ===== 0. 小工具：造棋盘 / 找着法 =====
        static XiangqiBoard XBoard(params (char Fen, int X, int Y)[] pieces)
        {
            var board = new XiangqiBoard();
            foreach (var (fen, x, y) in pieces) board.Set(x, y, XiangqiPiece.FromFenChar(fen));
            return board;
        }

        static bool HasMove(IReadOnlyList<XiangqiMove> moves, int fx, int fy, int tx, int ty)
            => moves.Any(m => m.FromX == fx && m.FromY == fy && m.ToX == tx && m.ToY == ty);

        static int LegalTotal(XiangqiBoard board, XiangqiSide side)
        {
            var total = 0;
            for (var y = 0; y < XiangqiBoard.Height; y++)
            {
                for (var x = 0; x < XiangqiBoard.Width; x++)
                {
                    total += XiangqiRules.GenerateLegalMoves(board, side, x, y).Count;
                }
            }

            return total;
        }

        static bool AllInPalace(IReadOnlyList<XiangqiMove> moves, XiangqiSide side)
            => moves.All(m => XiangqiRules.InPalace(side, m.ToX, m.ToY));

        // ===== 1. 棋子 / 着法 / 阵营的小工具 =====
        check("[象棋] 红先：Red = 0、Black = 1",
            (int)XiangqiSide.Red == 0 && (int)XiangqiSide.Black == 1, "");
        check("[象棋] 空格子：IsEmpty、FEN / 显示字符都是空串",
            XiangqiPiece.Empty.IsEmpty && XiangqiPiece.Empty.FenChar == ""
            && XiangqiPiece.Empty.DisplayChar == "" && XiangqiPiece.Empty.DisplayName == "空格", "");
        check("[象棋] 红方棋子 FEN 是大写 K/A/E/H/R/C/P",
            new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red).FenChar == "K"
            && new XiangqiPiece(XiangqiPieceType.Advisor, XiangqiSide.Red).FenChar == "A"
            && new XiangqiPiece(XiangqiPieceType.Elephant, XiangqiSide.Red).FenChar == "E"
            && new XiangqiPiece(XiangqiPieceType.Horse, XiangqiSide.Red).FenChar == "H"
            && new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Red).FenChar == "R"
            && new XiangqiPiece(XiangqiPieceType.Cannon, XiangqiSide.Red).FenChar == "C"
            && new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Red).FenChar == "P", "");
        check("[象棋] 黑方棋子 FEN 是小写 k/a/e/h/r/c/p",
            new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Black).FenChar == "k"
            && new XiangqiPiece(XiangqiPieceType.Advisor, XiangqiSide.Black).FenChar == "a"
            && new XiangqiPiece(XiangqiPieceType.Elephant, XiangqiSide.Black).FenChar == "e"
            && new XiangqiPiece(XiangqiPieceType.Horse, XiangqiSide.Black).FenChar == "h"
            && new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Black).FenChar == "r"
            && new XiangqiPiece(XiangqiPieceType.Cannon, XiangqiSide.Black).FenChar == "c"
            && new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Black).FenChar == "p", "");
        check("[象棋] 显示汉字：红方帅仕相马车炮兵、黑方将士象马车炮卒",
            new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red).DisplayChar == "帅"
            && new XiangqiPiece(XiangqiPieceType.Advisor, XiangqiSide.Red).DisplayChar == "仕"
            && new XiangqiPiece(XiangqiPieceType.Elephant, XiangqiSide.Red).DisplayChar == "相"
            && new XiangqiPiece(XiangqiPieceType.Horse, XiangqiSide.Red).DisplayChar == "马"
            && new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Red).DisplayChar == "车"
            && new XiangqiPiece(XiangqiPieceType.Cannon, XiangqiSide.Red).DisplayChar == "炮"
            && new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Red).DisplayChar == "兵"
            && new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Black).DisplayChar == "将"
            && new XiangqiPiece(XiangqiPieceType.Advisor, XiangqiSide.Black).DisplayChar == "士"
            && new XiangqiPiece(XiangqiPieceType.Elephant, XiangqiSide.Black).DisplayChar == "象"
            && new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Black).DisplayChar == "卒", "");
        var fenChars = new[] { "K", "A", "E", "H", "R", "C", "P", "k", "a", "e", "h", "r", "c", "p" };
        check("[象棋] FEN 字母 → 棋子 → FEN 字母 往返一致",
            fenChars.All(c => XiangqiPiece.FromFenChar(c[0]).FenChar == c),
            string.Join(",", fenChars.Select(c => XiangqiPiece.FromFenChar(c[0]).FenChar)));
        check("[象棋] 解析 FEN 棋子时也认国际写法 N（马）/ B（象）",
            XiangqiPiece.FromFenChar('N').Type == XiangqiPieceType.Horse
            && XiangqiPiece.FromFenChar('B').Type == XiangqiPieceType.Elephant, "");
        check("[象棋] 按显示汉字还原棋子（帅 → 红帅、卒 → 黑卒、认不出 → 空格）",
            XiangqiPiece.FromDisplayChar('帅') == new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red)
            && XiangqiPiece.FromDisplayChar('卒') == new XiangqiPiece(XiangqiPieceType.Pawn, XiangqiSide.Black)
            && XiangqiPiece.FromDisplayChar('X').IsEmpty, "");
        check("[象棋] 棋子中文全名带颜色（红方车 / 黑方炮 / 空格）",
            new XiangqiPiece(XiangqiPieceType.Rook, XiangqiSide.Red).DisplayName == "红方车"
            && new XiangqiPiece(XiangqiPieceType.Cannon, XiangqiSide.Black).DisplayName == "黑方炮"
            && XiangqiPiece.Empty.DisplayName == "空格", "");
        check("[象棋] 着法 Key 是稳定的「fxfy-txty」",
            new XiangqiMove(7, 7, 4, 7).Key == "77-47", new XiangqiMove(7, 7, 4, 7).Key);
        check("[象棋] 着法 Key 往返一致",
            XiangqiMove.Parse("77-47") == new XiangqiMove(7, 7, 4, 7)
            && XiangqiMove.Parse("00-99") == new XiangqiMove(0, 0, 9, 9), "");
        check("[象棋] 坏着的 Key 解析成 None（不抛异常）",
            XiangqiMove.Parse(null).IsNone && XiangqiMove.Parse("").IsNone
            && XiangqiMove.Parse("777-4").IsNone && XiangqiMove.Parse("xx-yy").IsNone
            && XiangqiMove.None.IsNone, "");
        check("[象棋] 阵营工具：取反 / 中文名 / 协议串 / FEN 字符 / 解析",
            XiangqiSide.Red.Opposite() == XiangqiSide.Black
            && XiangqiSide.Red.DisplayName() == "红方" && XiangqiSide.Black.ShortName() == "黑"
            && XiangqiSide.Red.ToKey() == "red" && XiangqiSide.Black.ToKey() == "black"
            && XiangqiSide.Red.ToFenChar() == "w" && XiangqiSide.Black.ToFenChar() == "b"
            && XiangqiSides.Parse("红") == XiangqiSide.Red && XiangqiSides.Parse("w") == XiangqiSide.Red
            && XiangqiSides.Parse("black") == XiangqiSide.Black && XiangqiSides.Parse("") is null
            && XiangqiSides.Parse("xyz") is null, "");

        // ===== 2. 棋盘与 FEN =====
        check("[象棋] 棋盘是 9 列 × 10 行", XiangqiBoard.Width == 9 && XiangqiBoard.Height == 10, "");
        check("[象棋] InBounds 边界判断正确",
            XiangqiBoard.InBounds(0, 0) && XiangqiBoard.InBounds(8, 9)
            && !XiangqiBoard.InBounds(9, 0) && !XiangqiBoard.InBounds(0, 10) && !XiangqiBoard.InBounds(-1, 0), "");
        var initial = XiangqiBoard.CreateInitial();
        check("[象棋] 标准开局双方各 16 子",
            initial.Count(XiangqiSide.Red) == 16 && initial.Count(XiangqiSide.Black) == 16,
            $"红 {initial.Count(XiangqiSide.Red)} / 黑 {initial.Count(XiangqiSide.Black)}");
        check("[象棋] 标准开局帅在 (4,9)、将在 (4,0)",
            initial.Get(4, 9) == new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red)
            && initial.Get(4, 0) == new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Black), "");
        check("[象棋] 标准开局红车在 (0,9) 与 (8,9)",
            initial.Get(0, 9).Type == XiangqiPieceType.Rook && initial.Get(8, 9).Type == XiangqiPieceType.Rook
            && initial.Get(0, 9).Side == XiangqiSide.Red, "");
        check("[象棋] 标准开局各类棋子数量正确（车马炮相仕各 2、兵 5、帅 1）",
            initial.Count(XiangqiPieceType.Rook, XiangqiSide.Red) == 2
            && initial.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 2
            && initial.Count(XiangqiPieceType.Cannon, XiangqiSide.Red) == 2
            && initial.Count(XiangqiPieceType.Elephant, XiangqiSide.Red) == 2
            && initial.Count(XiangqiPieceType.Advisor, XiangqiSide.Red) == 2
            && initial.Count(XiangqiPieceType.Pawn, XiangqiSide.Red) == 5
            && initial.Count(XiangqiPieceType.King, XiangqiSide.Red) == 1, "");
        check("[象棋] FindPieces 找得到红车的两个位置",
            initial.FindPieces(XiangqiSide.Red, XiangqiPieceType.Rook).SequenceEqual(new[] { (0, 9), (8, 9) }),
            string.Join(",", initial.FindPieces(XiangqiSide.Red, XiangqiPieceType.Rook).Select(p => $"({p.X},{p.Y})")));
        check("[象棋] 标准开局 FEN 字符串与约定一致（红大写、黑小写、w 行棋）",
            initial.ToFen(XiangqiSide.Red) == XiangqiSetup.InitialFen,
            initial.ToFen(XiangqiSide.Red));
        var (parsedInitial, parsedTurn, parsedHalf, parsedError) = XiangqiBoard.ParseFen(XiangqiSetup.InitialFen);
        check("[象棋] 标准开局 FEN 能解析回来（棋盘 / 红先 / 错误为空）",
            parsedError.Length == 0 && parsedTurn == XiangqiSide.Red && parsedHalf == 0
            && parsedInitial.ToFen(XiangqiSide.Red) == XiangqiSetup.InitialFen, parsedError);
        var (blackTurnBoard, blackTurn, _, blackTurnError) = XiangqiBoard.ParseFen(
            XiangqiSetup.InitialFen.Replace(" w ", " b "));
        check("[象棋] FEN 里的行棋方 b 解析成黑方",
            blackTurnError.Length == 0 && blackTurn == XiangqiSide.Black
            && blackTurnBoard.Get(4, 0).Type == XiangqiPieceType.King, blackTurnError);
        var (halfBoard, _, halfMoves, _) = XiangqiBoard.ParseFen(
            "9/9/9/9/9/9/9/9/9/4K4 w - - 7 12");
        check("[象棋] FEN 里的无吃子半回合数能读出来", halfMoves == 7 && halfBoard.Get(4, 9).Type == XiangqiPieceType.King,
            halfMoves.ToString());
        var (badRowsBoard, _, _, badRowsError) = XiangqiBoard.ParseFen("9/9/9 w");
        check("[象棋] FEN 行数不对 → 中文错误（不抛异常）",
            badRowsBoard.Count(XiangqiSide.Red) == 0 && badRowsError.Contains("10"), badRowsError);
        var (badCharBoard, _, _, badCharError) = XiangqiBoard.ParseFen("9/9/9/9/9/9/9/9/9/9X");
        check("[象棋] FEN 有非法字符 → 中文错误",
            badCharBoard.Count(XiangqiSide.Red) == 0 && badCharError.Contains("X"), badCharError);
        check("[象棋] 空 FEN → 中文错误", XiangqiBoard.ParseFen("").Error.Length > 0);
        var cloneBoard = initial.Clone();
        cloneBoard.Set(4, 9, XiangqiPiece.Empty);
        check("[象棋] 棋盘 Clone 是深拷贝（改副本不影响原棋盘）",
            initial.Get(4, 9).Type == XiangqiPieceType.King && cloneBoard.IsEmpty(4, 9), "");
        check("[象棋] Get 越界返回空格、Set 越界不抛异常（静默忽略）",
            initial.Get(9, 0).IsEmpty && initial.Get(-1, 0).IsEmpty, "");
        var clearedBoard = initial.Clone();
        clearedBoard.Clear();
        check("[象棋] Clear 清空整个棋盘",
            clearedBoard.Count(XiangqiSide.Red) == 0 && clearedBoard.Count(XiangqiSide.Black) == 0, "");

        // ===== 3. 走法（每种子都逐条验证） =====
        check("[象棋] 标准开局红方共有 44 个合法着法", LegalTotal(initial, XiangqiSide.Red) == 44,
            LegalTotal(initial, XiangqiSide.Red).ToString());
        check("[象棋] 标准开局黑方也有 44 个合法着法", LegalTotal(initial, XiangqiSide.Black) == 44,
            LegalTotal(initial, XiangqiSide.Black).ToString());

        var rookBoard = XBoard(('R', 4, 5));
        check("[象棋] 车在空棋盘上有 17 个着法（横 8 + 竖 9）",
            XiangqiRules.GenerateMoves(rookBoard, 4, 5).Count == 17,
            XiangqiRules.GenerateMoves(rookBoard, 4, 5).Count.ToString());
        var rookBlocked = XBoard(('R', 4, 5), ('P', 4, 7), ('p', 4, 2));
        var rookMoves = XiangqiRules.GenerateMoves(rookBlocked, 4, 5);
        check("[象棋] 车被己方子挡住就停、能吃掉对方子（含吃子着法）",
            rookMoves.Count == 12 && HasMove(rookMoves, 4, 5, 4, 2) && !HasMove(rookMoves, 4, 5, 4, 7),
            $"{rookMoves.Count} 个着法");

        var horseBoard = XBoard(('H', 4, 5));
        check("[象棋] 马在空棋盘中心有 8 个着法",
            XiangqiRules.GenerateMoves(horseBoard, 4, 5).Count == 8,
            XiangqiRules.GenerateMoves(horseBoard, 4, 5).Count.ToString());
        var horseBlocked = XBoard(('H', 4, 5), ('P', 4, 4));
        check("[象棋] 蹩马腿：马腿上有子时那两个方向走不了（8 → 6）",
            XiangqiRules.GenerateMoves(horseBlocked, 4, 5).Count == 6
            && !HasMove(XiangqiRules.GenerateMoves(horseBlocked, 4, 5), 4, 5, 3, 3),
            XiangqiRules.GenerateMoves(horseBlocked, 4, 5).Count.ToString());

        var elephantBoard = XBoard(('E', 4, 7));
        check("[象棋] 相在空棋盘本方半场有 4 个着法",
            XiangqiRules.GenerateMoves(elephantBoard, 4, 7).Count == 4,
            XiangqiRules.GenerateMoves(elephantBoard, 4, 7).Count.ToString());
        var elephantEye = XBoard(('E', 4, 7), ('P', 3, 6));
        check("[象棋] 塞象眼：象眼上有子时那个田字走不了（4 → 3）",
            XiangqiRules.GenerateMoves(elephantEye, 4, 7).Count == 3
            && !HasMove(XiangqiRules.GenerateMoves(elephantEye, 4, 7), 4, 7, 2, 5),
            XiangqiRules.GenerateMoves(elephantEye, 4, 7).Count.ToString());
        var elephantRiver = XBoard(('E', 2, 5));
        var elephantRiverMoves = XiangqiRules.GenerateMoves(elephantRiver, 2, 5);
        check("[象棋] 相不能过河（着法终点全在本方半场）",
            elephantRiverMoves.Count == 2 && elephantRiverMoves.All(m => m.ToY >= 5),
            string.Join(",", elephantRiverMoves.Select(m => $"({m.ToX},{m.ToY})")));

        var advisorBoard = XBoard(('A', 4, 8));
        var advisorMoves = XiangqiRules.GenerateMoves(advisorBoard, 4, 8);
        check("[象棋] 仕在九宫中心有 4 个斜走着法，且都留在九宫",
            advisorMoves.Count == 4 && AllInPalace(advisorMoves, XiangqiSide.Red),
            string.Join(",", advisorMoves.Select(m => $"({m.ToX},{m.ToY})")));
        check("[象棋] 仕在九宫角上只有 1 个着法",
            XiangqiRules.GenerateMoves(XBoard(('A', 3, 9)), 3, 9).Count == 1, "");
        var kingMoves = XiangqiRules.GenerateMoves(XBoard(('K', 4, 9)), 4, 9);
        check("[象棋] 帅在九宫底有 3 个着法（上 / 左 / 右），且都留在九宫",
            kingMoves.Count == 3 && AllInPalace(kingMoves, XiangqiSide.Red),
            string.Join(",", kingMoves.Select(m => $"({m.ToX},{m.ToY})")));
        check("[象棋] 帅在九宫左侧（3,7）只有 2 个着法（不能出九宫）",
            XiangqiRules.GenerateMoves(XBoard(('K', 3, 7)), 3, 7).Count == 2, "");

        check("[象棋] 炮在空棋盘上跟车一样（17 个着法）",
            XiangqiRules.GenerateMoves(XBoard(('C', 4, 5)), 4, 5).Count == 17, "");
        var cannonBoard = XBoard(('C', 4, 5), ('P', 4, 3), ('p', 4, 1));
        var cannonMoves = XiangqiRules.GenerateMoves(cannonBoard, 4, 5);
        check("[象棋] 炮隔一子能吃（越过己方炮架吃黑子），不能吃炮架、不能落在炮架后空格",
            HasMove(cannonMoves, 4, 5, 4, 1) && !HasMove(cannonMoves, 4, 5, 4, 3)
            && !HasMove(cannonMoves, 4, 5, 4, 2) && !HasMove(cannonMoves, 4, 5, 4, 0),
            string.Join(",", cannonMoves.Select(m => $"({m.ToX},{m.ToY})")));
        var cannonNoScreen = XBoard(('C', 4, 5), ('p', 4, 3));
        check("[象棋] 炮没有炮架时不能吃子（第一段只能走空格）",
            XiangqiRules.GenerateMoves(cannonNoScreen, 4, 5).Count == 13
            && !HasMove(XiangqiRules.GenerateMoves(cannonNoScreen, 4, 5), 4, 5, 4, 3),
            XiangqiRules.GenerateMoves(cannonNoScreen, 4, 5).Count.ToString());

        check("[象棋] 兵没过河只能向前走 1 步", XiangqiRules.GenerateMoves(XBoard(('P', 4, 6)), 4, 6).Count == 1);
        var redCrossedPawn = XiangqiRules.GenerateMoves(XBoard(('P', 4, 4)), 4, 4);
        check("[象棋] 兵过河后能向前 + 左右横走（3 步），不能后退",
            redCrossedPawn.Count == 3 && HasMove(redCrossedPawn, 4, 4, 4, 3)
            && HasMove(redCrossedPawn, 4, 4, 3, 4) && HasMove(redCrossedPawn, 4, 4, 5, 4)
            && !HasMove(redCrossedPawn, 4, 4, 4, 5),
            string.Join(",", redCrossedPawn.Select(m => $"({m.ToX},{m.ToY})")));
        check("[象棋] 卒没过河只能向前走 1 步", XiangqiRules.GenerateMoves(XBoard(('p', 2, 3)), 2, 3).Count == 1);
        var blackCrossedPawn = XiangqiRules.GenerateMoves(XBoard(('p', 4, 5)), 4, 5);
        check("[象棋] 卒过河后能向前 + 左右横走（3 步），不能后退",
            blackCrossedPawn.Count == 3 && HasMove(blackCrossedPawn, 4, 5, 4, 6)
            && HasMove(blackCrossedPawn, 4, 5, 3, 5) && HasMove(blackCrossedPawn, 4, 5, 5, 5)
            && !HasMove(blackCrossedPawn, 4, 5, 4, 4),
            string.Join(",", blackCrossedPawn.Select(m => $"({m.ToX},{m.ToY})")));

        // ===== 4. 飞将 / 送将 / 将军 / 应将 =====
        check("[象棋] 将帅同列且中间无子 → 照面（AreKingsFacing）",
            XiangqiRules.AreKingsFacing(XBoard(('K', 4, 9), ('k', 4, 0))));
        check("[象棋] 将帅中间有子 → 不算照面",
            !XiangqiRules.AreKingsFacing(XBoard(('K', 4, 9), ('k', 4, 0), ('P', 4, 5))));

        var flyGame = new XiangqiGame(XBoard(('K', 4, 9), ('k', 4, 0), ('R', 4, 5)));
        var flyError = flyGame.Play(XiangqiSide.Red, 4, 5, 3, 5);
        check("[象棋] 飞将（走成将帅照面）非法（Play 返回中文原因，含「送将」）",
            flyError is { Length: > 0 } && flyError.Contains("送将"), flyError ?? "(null)");
        var stayGame = new XiangqiGame(XBoard(('K', 4, 9), ('k', 4, 0), ('R', 4, 5)));
        check("[象棋] 但同一枚车沿着中线走（继续挡住将帅）是合法的",
            stayGame.Play(XiangqiSide.Red, 4, 5, 4, 4) is null, "");

        var checkBoard = XBoard(('K', 4, 9), ('k', 4, 0), ('r', 4, 5), ('P', 0, 6));
        var checkedGame = new XiangqiGame(checkBoard, XiangqiSide.Red);
        check("[象棋] 被将军判定正确（黑车沿中线将红帅）",
            checkedGame.IsInCheck && checkedGame.IsInCheckFor(XiangqiSide.Red) && !checkedGame.IsInCheckFor(XiangqiSide.Black)
            && checkedGame.CheckedSide == XiangqiSide.Red, "");
        var escapeGame = new XiangqiGame(checkBoard, XiangqiSide.Red);
        check("[象棋] 应将：帅往旁边挪开能解将（合法）",
            escapeGame.Play(XiangqiSide.Red, 4, 9, 3, 9) is null && !escapeGame.IsInCheck,
            escapeGame.IsInCheck.ToString());
        var ignoreCheckGame = new XiangqiGame(checkBoard, XiangqiSide.Red);
        var ignoreCheckError = ignoreCheckGame.Play(XiangqiSide.Red, 0, 6, 0, 5);
        check("[象棋] 被将军时走别的子（不解除将军）= 送将，非法（含「将军」）",
            ignoreCheckError is { Length: > 0 } && ignoreCheckError.Contains("将军"), ignoreCheckError ?? "(null)");

        var pinnedBoard = XBoard(('K', 4, 9), ('R', 4, 5), ('r', 4, 0), ('k', 3, 0));
        var pinnedMoves = XiangqiRules.GenerateLegalMoves(pinnedBoard, XiangqiSide.Red, 4, 5);
        check("[象棋] 被牵制的车只能沿中线走（横向着法全部被过滤掉）",
            pinnedMoves.Count == 8 && pinnedMoves.All(m => m.FromX == m.ToX),
            string.Join(",", pinnedMoves.Select(m => $"({m.ToX},{m.ToY})")));
        check("[象棋] 试走检查不会改动传入的棋盘（WouldLeaveKingInCheck 会还原）",
            pinnedBoard.Get(4, 5).Type == XiangqiPieceType.Rook
            && pinnedBoard.Get(4, 0).Type == XiangqiPieceType.Rook, "");

        // 将死（谓词）：黑将 (4,0) 被 (4,2) 的车将，三路都被封死
        var mateBoard = XBoard(('K', 3, 9), ('k', 4, 0), ('R', 4, 2), ('R', 3, 5), ('R', 5, 5));
        check("[象棋] 将死判定：被将军且无任何合法着法",
            XiangqiRules.IsCheckmate(mateBoard, XiangqiSide.Black)
            && !XiangqiRules.HasAnyLegalMove(mateBoard, XiangqiSide.Black)
            && !XiangqiRules.IsStalemate(mateBoard, XiangqiSide.Black), "");
        // 困毙（谓词）：黑将没被将军，但三个格子全被控制
        var stalemateBoard = XBoard(('K', 3, 9), ('k', 4, 0), ('R', 3, 5), ('R', 5, 5), ('P', 4, 2));
        check("[象棋] 困毙判定：没被将军但无棋可走",
            XiangqiRules.IsStalemate(stalemateBoard, XiangqiSide.Black)
            && !XiangqiRules.IsCheckmate(stalemateBoard, XiangqiSide.Black)
            && !XiangqiRules.IsInCheck(stalemateBoard, XiangqiSide.Black), "");

        var mateGame = new XiangqiGame(XBoard(('K', 3, 9), ('k', 4, 0), ('R', 8, 2), ('R', 3, 5), ('R', 5, 5)));
        var mateError = mateGame.Play(XiangqiSide.Red, 8, 2, 4, 2);
        check("[象棋] 走出将死的一步 → 红方获胜（结果原因含「将死」）",
            mateError is null && mateGame.Result == XiangqiResult.RedWin && mateGame.ResultText == "红方获胜"
            && mateGame.ResultReason.Contains("将死"),
            $"{mateGame.ResultText} / {mateGame.ResultReason}");
        check("[象棋] 将死后不能再走棋",
            mateGame.Play(XiangqiSide.Black, 4, 0, 3, 0) is { } mateOverError && mateOverError.Contains("结束"),
            mateGame.Play(XiangqiSide.Black, 4, 0, 3, 0) ?? "(null)");

        var staleGame = new XiangqiGame(XBoard(('K', 3, 9), ('k', 4, 0), ('R', 3, 6), ('R', 5, 5), ('P', 4, 2)));
        var staleError = staleGame.Play(XiangqiSide.Red, 3, 6, 3, 5);
        check("[象棋] 走出困毙的一步 → 同样判红方获胜（中国象棋困毙判负）",
            staleError is null && staleGame.Result == XiangqiResult.RedWin
            && staleGame.ResultReason.Contains("困毙"),
            $"{staleGame.ResultText} / {staleGame.ResultReason}");

        // 自然限着：双方各 60 步（共 120 个半回合）无吃子 → 和棋
        var limitGame = new XiangqiGame(XBoard(('K', 3, 9), ('k', 4, 0), ('R', 0, 9), ('r', 8, 0)));
        var redAtBottom = true;
        var blackAtTop = true;
        var limitMoveError = "";
        for (var i = 0; i < XiangqiGame.NaturalLimitHalfMoves && limitMoveError.Length == 0; i++)
        {
            if (i % 2 == 0)
            {
                var error = redAtBottom
                    ? limitGame.Play(XiangqiSide.Red, 0, 9, 0, 8)
                    : limitGame.Play(XiangqiSide.Red, 0, 8, 0, 9);
                redAtBottom = !redAtBottom;
                limitMoveError = error ?? "";
            }
            else
            {
                var error = blackAtTop
                    ? limitGame.Play(XiangqiSide.Black, 8, 0, 8, 1)
                    : limitGame.Play(XiangqiSide.Black, 8, 1, 8, 0);
                blackAtTop = !blackAtTop;
                limitMoveError = error ?? "";
            }
        }

        check("[象棋] 连续 120 个半回合无吃子 → 自然限着判和",
            limitMoveError.Length == 0 && limitGame.Result == XiangqiResult.Draw
            && limitGame.ResultText == "和棋" && limitGame.ResultReason.Contains("自然限着")
            && limitGame.HalfMovesSinceCapture >= XiangqiGame.NaturalLimitHalfMoves,
            $"error={limitMoveError} result={limitGame.Result} half={limitGame.HalfMovesSinceCapture}");
        var limitUndo = limitGame.Undo();
        check("[象棋] 自然限着判和后悔棋：结果恢复进行中、计数退回 119",
            limitUndo && limitGame.Result == XiangqiResult.Playing
            && limitGame.HalfMovesSinceCapture == XiangqiGame.NaturalLimitHalfMoves - 1,
            $"{limitGame.Result} / {limitGame.HalfMovesSinceCapture}");

        // ===== 5. 悔棋 =====
        var undoGame = new XiangqiGame();
        undoGame.Play(XiangqiSide.Red, 0, 6, 0, 5);
        undoGame.Play(XiangqiSide.Black, 0, 3, 0, 4);
        check("[象棋] 悔棋撤掉最后一步：棋子回原位、棋谱少一步、轮次还原",
            undoGame.Undo() && undoGame.History.Count == 1 && undoGame.Turn == XiangqiSide.Black
            && undoGame.Board.IsEmpty(0, 4) && undoGame.Board.Get(0, 3).Type == XiangqiPieceType.Pawn,
            $"history={undoGame.History.Count} turn={undoGame.Turn}");
        check("[象棋] 再悔一手回到标准开局（棋盘 FEN 与开局一致）",
            undoGame.Undo() && undoGame.History.Count == 0 && undoGame.Turn == XiangqiSide.Red
            && undoGame.Board.ToFen(XiangqiSide.Red) == XiangqiSetup.InitialFen, "");
        check("[象棋] 空棋局悔棋返回 false", !new XiangqiGame().Undo());

        var captureBoard = XBoard(
            ('K', 3, 9), ('k', 4, 0), ('R', 4, 5), ('h', 4, 2));
        var captureGame = new XiangqiGame(captureBoard, XiangqiSide.Red);
        var captureError = captureGame.Play(XiangqiSide.Red, 4, 5, 4, 2);
        check("[象棋] 车吃马成功，并在棋谱里记下被吃的子",
            captureError is null && captureGame.Board.IsEmpty(4, 5)
            && captureGame.Board.Get(4, 2).Type == XiangqiPieceType.Rook
            && captureGame.History.Count == 1 && captureGame.History[0].Captured == "马",
            $"captured={captureGame.History.FirstOrDefault()?.Captured ?? "(无)"}");
        var captureUndo = captureGame.Undo();
        check("[象棋] 悔棋把被吃的子放回来（马回到 (4,2)、车回到 (4,5)）",
            captureUndo && captureGame.Board.Get(4, 2).Type == XiangqiPieceType.Horse
            && captureGame.Board.Get(4, 2).Side == XiangqiSide.Black
            && captureGame.Board.Get(4, 5).Type == XiangqiPieceType.Rook
            && captureGame.History.Count == 0 && captureGame.HalfMovesSinceCapture == 0, "");

        var undoMateGame = new XiangqiGame(XBoard(('K', 3, 9), ('k', 4, 0), ('R', 8, 2), ('R', 3, 5), ('R', 5, 5)));
        undoMateGame.Play(XiangqiSide.Red, 8, 2, 4, 2);
        var undoMateOk = undoMateGame.Undo();
        check("[象棋] 将死后悔棋：结果与原因一并恢复成进行中",
            undoMateOk && undoMateGame.Result == XiangqiResult.Playing
            && undoMateGame.ResultReason.Length == 0 && undoMateGame.LastMove is null, "");

        var undoToRed = new XiangqiGame();
        undoToRed.Play(XiangqiSide.Red, 0, 6, 0, 5);
        undoToRed.Play(XiangqiSide.Black, 0, 3, 0, 4);
        check("[象棋] 联机悔棋（红请求）：撤 2 步回到红方该走",
            undoToRed.UndoToTurn(XiangqiSide.Red) && undoToRed.History.Count == 0
            && undoToRed.Turn == XiangqiSide.Red, $"history={undoToRed.History.Count}");
        var undoToBlack = new XiangqiGame();
        undoToBlack.Play(XiangqiSide.Red, 0, 6, 0, 5);
        undoToBlack.Play(XiangqiSide.Black, 0, 3, 0, 4);
        check("[象棋] 联机悔棋（黑请求）：只撤 1 步（黑刚走的那手）",
            undoToBlack.UndoToTurn(XiangqiSide.Black) && undoToBlack.History.Count == 1
            && undoToBlack.Turn == XiangqiSide.Black, $"history={undoToBlack.History.Count}");
        check("[象棋] 空棋局联机悔棋返回 false", !new XiangqiGame().UndoToTurn(XiangqiSide.Red), "");

        // ===== 6. FEN 往返（标准开局 + 走几步后） =====
        var fenGame = new XiangqiGame();
        check("[象棋] 标准开局炮二平五（红）能走",
            fenGame.Play(XiangqiSide.Red, 7, 7, 4, 7) is null, "");
        check("[象棋] 黑方应马8进7能走", fenGame.Play(XiangqiSide.Black, 7, 0, 6, 2) is null, "");
        check("[象棋] 红方马二进三能走", fenGame.Play(XiangqiSide.Red, 7, 9, 6, 7) is null, "");
        var movedFen = fenGame.Board.ToFen(fenGame.Turn, fenGame.HalfMovesSinceCapture);
        var (fenRoundBoard, fenRoundTurn, fenRoundHalf, fenRoundError) = XiangqiBoard.ParseFen(movedFen);
        check("[象棋] 走几步后的 FEN 往返（棋盘 / 轮次 / 无吃子计数）一致",
            fenRoundError.Length == 0 && fenRoundTurn == fenGame.Turn
            && fenRoundHalf == fenGame.HalfMovesSinceCapture
            && fenRoundBoard.ToFen(fenGame.Turn, fenRoundHalf) == movedFen,
            fenRoundError.Length > 0 ? fenRoundError : movedFen);
        check("[象棋] 走子后棋谱记录：序号 / 走子方 / 中文记谱齐全",
            fenGame.History.Count == 3
            && fenGame.History[0].Number == 1 && fenGame.History[0].Side == "red"
            && fenGame.History[0].Notation == "炮二平五"
            && fenGame.History[1].Side == "black" && fenGame.History[1].Notation == "马8进7"
            && fenGame.History[2].Notation == "马二进三",
            string.Join(" / ", fenGame.History.Select(h => $"{h.Number}.{h.Side}:{h.Notation}")));

        // ===== 7. 自由摆棋校验 =====
        check("[象棋] 标准开局摆棋校验通过（返回 null）", XiangqiSetup.Validate(initial, XiangqiSide.Red) is null, "");
        var noRedKing = initial.Clone();
        noRedKing.Set(4, 9, XiangqiPiece.Empty);
        check("[象棋] 缺帅 → 校验报错（含「帅」）",
            XiangqiSetup.Validate(noRedKing, XiangqiSide.Red) is { } noRedError && noRedError.Contains("帅"),
            XiangqiSetup.Validate(noRedKing, XiangqiSide.Red) ?? "(null)");
        var noBlackKing = initial.Clone();
        noBlackKing.Set(4, 0, XiangqiPiece.Empty);
        check("[象棋] 缺将 → 校验报错（含「将」）",
            XiangqiSetup.Validate(noBlackKing, XiangqiSide.Red) is { } noBlackError && noBlackError.Contains("将"),
            XiangqiSetup.Validate(noBlackKing, XiangqiSide.Red) ?? "(null)");
        var twoRedKings = initial.Clone();
        twoRedKings.Set(3, 9, new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red));
        check("[象棋] 两个帅 → 校验报错",
            XiangqiSetup.Validate(twoRedKings, XiangqiSide.Red) is { } twoKingError && twoKingError.Contains("2"),
            XiangqiSetup.Validate(twoRedKings, XiangqiSide.Red) ?? "(null)");
        var kingOutside = initial.Clone();
        kingOutside.Set(4, 9, XiangqiPiece.Empty);
        kingOutside.Set(4, 6, new XiangqiPiece(XiangqiPieceType.King, XiangqiSide.Red));
        check("[象棋] 帅在九宫外 → 校验报错（含「九宫」）",
            XiangqiSetup.Validate(kingOutside, XiangqiSide.Red) is { } outsideError && outsideError.Contains("九宫"),
            XiangqiSetup.Validate(kingOutside, XiangqiSide.Red) ?? "(null)");
        var facingBoard = XBoard(('K', 4, 9), ('k', 4, 0));
        check("[象棋] 将帅照面 → 校验报错（含「照面」）",
            XiangqiSetup.Validate(facingBoard, XiangqiSide.Red) is { } facingError && facingError.Contains("照面"),
            XiangqiSetup.Validate(facingBoard, XiangqiSide.Red) ?? "(null)");
        var checkBeforeStart = XBoard(('K', 3, 9), ('k', 4, 0), ('R', 4, 5));
        check("[象棋] 非行棋方被将军 → 校验报错（含「将军」）",
            XiangqiSetup.Validate(checkBeforeStart, XiangqiSide.Red, XiangqiSide.Black) is { } beforeError
            && beforeError.Contains("将军"),
            XiangqiSetup.Validate(checkBeforeStart, XiangqiSide.Red, XiangqiSide.Black) ?? "(null)");
        check("[象棋] 摆棋校验传入 null 棋盘不崩（返回错误文案）",
            XiangqiSetup.Validate(null!, XiangqiSide.Red) is { Length: > 0 },
            XiangqiSetup.Validate(null!, XiangqiSide.Red) ?? "(null)");

        // ===== 8. 让子预设 =====
        check("[象棋] 让子预设清单 = 标准开局 + 6 种让子",
            XiangqiSetup.Presets.Count == 7 && XiangqiSetup.Presets[0].Handicap is null
            && XiangqiSetup.Presets.Skip(1).All(p => p.Handicap is not null)
            && XiangqiSetup.Presets.Select(p => p.Name).Distinct().Count() == 7,
            string.Join(",", XiangqiSetup.Presets.Select(p => p.Name)));
        check("[象棋] 让子预设名字齐全（单马 / 双马 / 单车 / 车马 / 车马炮 / 九子）",
            new[] { "让单马", "让双马", "让单车", "让车马", "让车马炮", "让九子" }
                .All(name => XiangqiSetup.Presets.Any(p => p.Name == name)), "");
        check("[象棋] HandicapName 能把枚举翻回预设名",
            XiangqiSetup.HandicapName(null) == "标准开局"
            && XiangqiSetup.HandicapName(XiangqiSetup.XiangqiHandicap.SingleHorse) == "让单马"
            && XiangqiSetup.HandicapName(XiangqiSetup.XiangqiHandicap.NinePieces) == "让九子", "");

        var handicapBase = XiangqiBoard.CreateInitial();
        var singleHorse = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Red,
            XiangqiSetup.XiangqiHandicap.SingleHorse);
        check("[象棋] 让单马（红方让）：红方少 1 匹、黑方仍有 2 匹",
            singleHorse.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 1
            && singleHorse.Count(XiangqiPieceType.Horse, XiangqiSide.Black) == 2
            && singleHorse.Count(XiangqiSide.Red) == 15,
            $"红马 {singleHorse.Count(XiangqiPieceType.Horse, XiangqiSide.Red)} / 红子 {singleHorse.Count(XiangqiSide.Red)}");
        check("[象棋] 让单马去的是红方左手边那匹（原 (1,9)，留下 (7,9)）",
            singleHorse.IsEmpty(1, 9) && singleHorse.Get(7, 9).Type == XiangqiPieceType.Horse,
            $"({(singleHorse.IsEmpty(1, 9) ? "空" : "有子")},{singleHorse.Get(7, 9).Type})");
        var doubleHorse = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Red,
            XiangqiSetup.XiangqiHandicap.DoubleHorse);
        check("[象棋] 让双马（红方让）：红方一匹马都不剩",
            doubleHorse.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 0, "");
        var singleRook = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Red,
            XiangqiSetup.XiangqiHandicap.SingleRook);
        check("[象棋] 让单车（红方让）：红方少 1 个车",
            singleRook.Count(XiangqiPieceType.Rook, XiangqiSide.Red) == 1
            && singleRook.Count(XiangqiPieceType.Rook, XiangqiSide.Black) == 2, "");
        var rookHorse = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Red,
            XiangqiSetup.XiangqiHandicap.RookAndHorse);
        check("[象棋] 让车马（红方让）：红方少 1 车 1 马",
            rookHorse.Count(XiangqiPieceType.Rook, XiangqiSide.Red) == 1
            && rookHorse.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 1, "");
        var rookHorseCannon = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Red,
            XiangqiSetup.XiangqiHandicap.RookHorseCannon);
        check("[象棋] 让车马炮（红方让）：红方各少 1 车 1 马 1 炮",
            rookHorseCannon.Count(XiangqiPieceType.Rook, XiangqiSide.Red) == 1
            && rookHorseCannon.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 1
            && rookHorseCannon.Count(XiangqiPieceType.Cannon, XiangqiSide.Red) == 1, "");
        var ninePieces = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Red,
            XiangqiSetup.XiangqiHandicap.NinePieces);
        check("[象棋] 让九子（红方让）：红方正好少 9 子（车马炮相各 2 + 仕 1）",
            ninePieces.Count(XiangqiSide.Red) == 7
            && ninePieces.Count(XiangqiPieceType.Rook, XiangqiSide.Red) == 0
            && ninePieces.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 0
            && ninePieces.Count(XiangqiPieceType.Cannon, XiangqiSide.Red) == 0
            && ninePieces.Count(XiangqiPieceType.Elephant, XiangqiSide.Red) == 0
            && ninePieces.Count(XiangqiPieceType.Advisor, XiangqiSide.Red) == 1
            && ninePieces.Count(XiangqiPieceType.King, XiangqiSide.Red) == 1
            && ninePieces.Count(XiangqiPieceType.Pawn, XiangqiSide.Red) == 5,
            $"红方剩 {ninePieces.Count(XiangqiSide.Red)} 子");
        check("[象棋] 让九子后黑方还是完整 16 子", ninePieces.Count(XiangqiSide.Black) == 16,
            ninePieces.Count(XiangqiSide.Black).ToString());
        var blackSingleHorse = XiangqiSetup.ApplyHandicap(handicapBase, XiangqiSide.Black,
            XiangqiSetup.XiangqiHandicap.SingleHorse);
        check("[象棋] 让子方是黑方时从黑方那边去掉（黑方左手边 = 屏幕右侧那匹）",
            blackSingleHorse.Count(XiangqiPieceType.Horse, XiangqiSide.Black) == 1
            && blackSingleHorse.IsEmpty(7, 0)
            && blackSingleHorse.Get(1, 0).Type == XiangqiPieceType.Horse, "");
        check("[象棋] ApplyHandicap 不改原棋盘（返回的是副本）",
            handicapBase.Count(XiangqiSide.Red) == 16
            && handicapBase.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 2, "");
        check("[象棋] 让子后的局面能通过摆棋校验、可以直接开局",
            XiangqiSetup.Validate(singleHorse, XiangqiSide.Red) is null
            && XiangqiSetup.Validate(ninePieces, XiangqiSide.Red) is null, "");
        var handicapGame = new XiangqiGame(singleHorse);
        check("[象棋] 用让子棋盘开一局：红方直接少一匹马",
            handicapGame.Board.Count(XiangqiPieceType.Horse, XiangqiSide.Red) == 1
            && handicapGame.Turn == XiangqiSide.Red, "");

        // ===== 9. 让先 =====
        var freeRed = new XiangqiGame(null, XiangqiSide.Red, 2, XiangqiSide.Red);
        check("[象棋] 让先（红方被让 2 步）：开局就是红方走，还剩 2 步",
            freeRed.Turn == XiangqiSide.Red && freeRed.OpeningFreeMovesRemaining == 2
            && freeRed.OpeningSide == XiangqiSide.Red && freeRed.IsOpeningFreePhase, "");
        check("[象棋] 让先：红方第一步走完还是红方走（剩余 1）",
            freeRed.Play(XiangqiSide.Red, 0, 6, 0, 5) is null
            && freeRed.Turn == XiangqiSide.Red && freeRed.OpeningFreeMovesRemaining == 1,
            $"turn={freeRed.Turn} left={freeRed.OpeningFreeMovesRemaining}");
        check("[象棋] 让先：第 2 步走完才轮到黑方（剩余 0、让先阶段结束）",
            freeRed.Play(XiangqiSide.Red, 2, 6, 2, 5) is null
            && freeRed.Turn == XiangqiSide.Black && freeRed.OpeningFreeMovesRemaining == 0
            && !freeRed.IsOpeningFreePhase,
            $"turn={freeRed.Turn} left={freeRed.OpeningFreeMovesRemaining}");
        check("[象棋] 让先结束后恢复正常轮换（黑一步 → 红）",
            freeRed.Play(XiangqiSide.Black, 2, 3, 2, 4) is null && freeRed.Turn == XiangqiSide.Red, "");

        var freeBlack = new XiangqiGame(null, XiangqiSide.Red, 1, XiangqiSide.Black);
        check("[象棋] 让先（黑方被让 1 步）：黑方先走",
            freeBlack.Turn == XiangqiSide.Black && freeBlack.OpeningSide == XiangqiSide.Black, "");
        check("[象棋] 让先（黑方）走完 1 步 → 轮到红方",
            freeBlack.Play(XiangqiSide.Black, 0, 3, 0, 4) is null && freeBlack.Turn == XiangqiSide.Red
            && freeBlack.OpeningFreeMovesRemaining == 0, "");
        var freeNone = new XiangqiGame(null, XiangqiSide.Red, 0, null);
        check("[象棋] 不让先（0 步）：正常红先", freeNone.Turn == XiangqiSide.Red
            && freeNone.OpeningFreeMovesRemaining == 0 && freeNone.OpeningSide is null, "");
        var freeUndo = new XiangqiGame(null, XiangqiSide.Red, 2, XiangqiSide.Red);
        freeUndo.Play(XiangqiSide.Red, 0, 6, 0, 5);
        var freeUndoOk = freeUndo.Undo();
        check("[象棋] 让先阶段悔棋：剩余步数与轮次都还原",
            freeUndoOk && freeUndo.OpeningFreeMovesRemaining == 2 && freeUndo.Turn == XiangqiSide.Red, "");

        // ===== 10. 中文记谱 =====
        var notationBoard = XiangqiBoard.CreateInitial();
        check("[象棋] 记谱：炮二平五",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(7, 7, 4, 7)) == "炮二平五",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(7, 7, 4, 7)));
        check("[象棋] 记谱：马8进7（黑方路数从黑方右侧数）",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(7, 0, 6, 2)) == "马8进7",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(7, 0, 6, 2)));
        check("[象棋] 记谱：车九进一",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(0, 9, 0, 8)) == "车九进一",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(0, 9, 0, 8)));
        check("[象棋] 记谱：兵七进一",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(2, 6, 2, 5)) == "兵七进一",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(2, 6, 2, 5)));
        check("[象棋] 记谱：卒3进1（黑方用阿拉伯数字）",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(2, 3, 2, 4)) == "卒3进1",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(2, 3, 2, 4)));
        check("[象棋] 记谱：炮8平5（黑炮平中）",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(7, 2, 4, 2)) == "炮8平5",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(7, 2, 4, 2)));
        var twoRooksBoard = XBoard(('K', 4, 9), ('k', 4, 0), ('R', 0, 7), ('R', 0, 9));
        check("[象棋] 记谱：同一路两车用「前车 / 后车」区分",
            XiangqiNotation.Describe(twoRooksBoard, new XiangqiMove(0, 7, 0, 6)) == "前车进一"
            && XiangqiNotation.Describe(twoRooksBoard, new XiangqiMove(0, 9, 0, 8)) == "后车进一",
            $"{XiangqiNotation.Describe(twoRooksBoard, new XiangqiMove(0, 7, 0, 6))} / "
            + XiangqiNotation.Describe(twoRooksBoard, new XiangqiMove(0, 9, 0, 8)));
        check("[象棋] 记谱：起点没棋子 / 空着法返回空串",
            XiangqiNotation.Describe(notationBoard, new XiangqiMove(4, 4, 4, 5)) == ""
            && XiangqiNotation.Describe(notationBoard, XiangqiMove.None) == "", "");
        check("[象棋] 记谱：路数换算（红九…一 / 黑 1…9）",
            XiangqiNotation.FileLabel(0, true) == "九" && XiangqiNotation.FileLabel(8, true) == "一"
            && XiangqiNotation.FileLabel(0, false) == "1" && XiangqiNotation.FileLabel(8, false) == "9", "");
        check("[象棋] 记谱：行序号格式「1. 红」/「1... 黑」",
            XiangqiNotation.FormatMoveNumber(1, XiangqiSide.Red) == "1. 红"
            && XiangqiNotation.FormatMoveNumber(1, XiangqiSide.Black) == "1... 黑", "");
        var historyLines = XiangqiNotation.FormatHistory(fenGame.History);
        check("[象棋] 棋谱文本每行两个半回合、带序号",
            historyLines.Count == 2 && historyLines[0].Contains("1. 炮二平五")
            && historyLines[0].Contains("马8进7") && historyLines[1].Contains("马二进三"),
            string.Join(" | ", historyLines));
        check("[象棋] BuildHistoryText 可复制的整段棋谱（多行）",
            XiangqiNotation.BuildHistoryText(fenGame.History).Split(Environment.NewLine).Length == 2, "");

        // ===== 11. 快照往返 / 结果 / 状态 =====
        check("[象棋] 新棋局 Started 默认 false（房间等「开始对局」）", !new XiangqiGame().Started, "");
        fenGame.Started = true;
        var xqSnapshot = fenGame.ToSnapshot();
        check("[象棋] 快照：FEN 带行棋方、棋谱 3 步、Started 保留",
            xqSnapshot.Fen.Contains(" w") || xqSnapshot.Fen.Contains(" b")
            && xqSnapshot.History.Count == 3 && xqSnapshot.Started && xqSnapshot.Result == "playing",
            $"fen={xqSnapshot.Fen} history={xqSnapshot.History.Count}");
        var (xqRestored, xqRestoreError) = XiangqiGame.FromSnapshot(xqSnapshot);
        check("[象棋] 快照往返成功（棋盘 / 轮次 / 棋谱 / Started 一致）",
            xqRestored is not null && xqRestoreError.Length == 0
            && xqRestored.Board.ToFen(xqRestored.Turn, xqRestored.HalfMovesSinceCapture) == xqSnapshot.Fen
            && xqRestored.Turn == fenGame.Turn && xqRestored.History.Count == 3 && xqRestored.Started,
            xqRestoreError);
        check("[象棋] 往返后能接着走（轮次没乱）",
            xqRestored is not null && xqRestored.Play(XiangqiSide.Black, 2, 3, 2, 4) is null, "");
        check("[象棋] 往返后还能悔棋（悔棋栈从快照重建）",
            xqRestored is not null && xqRestored.Undo() && xqRestored.History.Count == 3, "");

        var xqWinSnapshot = mateGame.ToSnapshot();
        var (xqWinRestored, xqWinError) = XiangqiGame.FromSnapshot(xqWinSnapshot);
        check("[象棋] 快照保留胜负与原因（红胜 / 将死）",
            xqWinSnapshot.Result == "redWin" && xqWinSnapshot.ResultReason is { } winReason && winReason.Contains("将死")
            && xqWinRestored is { Result: XiangqiResult.RedWin }
            && xqWinRestored.ResultText == "红方获胜", xqWinError);
        var (nullXqGame, nullXqError) = XiangqiGame.FromSnapshot(null);
        check("[象棋] 空快照 → 中文错误", nullXqGame is null && nullXqError.Length > 0, nullXqError);
        var (badFenGame, badFenError) = XiangqiGame.FromSnapshot(new XiangqiSnapshot
        {
            Fen = "这不是 FEN",
            Result = "playing",
        });
        check("[象棋] 快照里的 FEN 不合法 → 中文错误（不崩）",
            badFenGame is null && badFenError.Contains("局面"), badFenError);

        var freeSnapshotGame = new XiangqiGame(null, XiangqiSide.Red, 2, XiangqiSide.Red);
        freeSnapshotGame.Play(XiangqiSide.Red, 0, 6, 0, 5);
        var freeSnapshot = freeSnapshotGame.ToSnapshot();
        var (freeRestored, _) = XiangqiGame.FromSnapshot(freeSnapshot);
        check("[象棋] 快照保留让先状态（被让方 + 剩余步数）",
            freeSnapshot.OpeningSide == "red" && freeSnapshot.OpeningFreeMovesRemaining == 1
            && freeRestored?.OpeningSide == XiangqiSide.Red
            && freeRestored.OpeningFreeMovesRemaining == 1, "");

        check("[象棋] 结果字符串往返（redWin / blackWin / draw / playing）",
            XiangqiGame.ResultToKey(XiangqiResult.RedWin) == "redWin"
            && XiangqiGame.ResultToKey(XiangqiResult.BlackWin) == "blackWin"
            && XiangqiGame.ResultToKey(XiangqiResult.Draw) == "draw"
            && XiangqiGame.ParseResult("REDWIN") == XiangqiResult.RedWin
            && XiangqiGame.ParseResult("blackWin") == XiangqiResult.BlackWin
            && XiangqiGame.ParseResult("draw") == XiangqiResult.Draw
            && XiangqiGame.ParseResult("乱写") == XiangqiResult.Playing, "");

        var resignGame = new XiangqiGame();
        resignGame.MarkResigned(XiangqiSide.Red);
        check("[象棋] 认输：红方认输 → 黑方获胜（原因含「认输」）",
            resignGame.Result == XiangqiResult.BlackWin && resignGame.ResultReason.Contains("认输"),
            resignGame.ResultReason);
        resignGame.MarkDraw();
        check("[象棋] 已经结束的棋局不会被再次改判",
            resignGame.Result == XiangqiResult.BlackWin, resignGame.Result.ToString());
        var drawGame2 = new XiangqiGame();
        drawGame2.MarkDraw();
        check("[象棋] 判和：结果与文案都是和棋",
            drawGame2.Result == XiangqiResult.Draw && drawGame2.ResultText == "和棋", "");
        check("[象棋] 结果文案四种（进行中 / 红胜 / 黑胜 / 和棋）",
            new XiangqiGame().ResultText == "对局进行中"
            && mateGame.ResultText == "红方获胜" && resignGame.ResultText == "黑方获胜"
            && drawGame2.ResultText == "和棋",
            $"{mateGame.ResultText} / {resignGame.ResultText} / {drawGame2.ResultText}");

        var legalGame = new XiangqiGame();
        check("[象棋] LegalMovesFor：开局帅只有 1 步、左车有 2 步、空格 0 步、对方的子 0 步",
            legalGame.LegalMovesFor(4, 9).Count == 1
            && legalGame.LegalMovesFor(0, 9).Count == 2
            && legalGame.LegalMovesFor(4, 4).Count == 0
            && legalGame.LegalMovesFor(0, 0).Count == 0,
            $"帅 {legalGame.LegalMovesFor(4, 9).Count} / 车 {legalGame.LegalMovesFor(0, 9).Count}");
        check("[象棋] 对局结束后 LegalMovesFor 返回空表", mateGame.LegalMovesFor(3, 5).Count == 0, "");

        var checkSnapshotGame = new XiangqiGame(checkBoard, XiangqiSide.Red);
        check("[象棋] 被将军的面：IsInCheck / CheckedSide 正确",
            checkSnapshotGame.IsInCheck && checkSnapshotGame.CheckedSide == XiangqiSide.Red
            && checkSnapshotGame.IsInCheckFor(XiangqiSide.Black) == false, "");
        var checkSnapshot = checkSnapshotGame.ToSnapshot();
        var (checkRestored, _) = XiangqiGame.FromSnapshot(checkSnapshot);
        check("[象棋] 被将军的局面往返后仍然是被将军",
            checkRestored is not null && checkRestored.IsInCheck
            && checkRestored.CheckedSide == XiangqiSide.Red, "");
    }

    // ---------------------------------------------------------------- 配置（自动落盘 + IBoardGameRoomConfig 三字段往返）

    private static void CheckConfigs(Action<string, bool, string> checkWithDetail, Action<string, bool> plain)
    {
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);

        // ===== 五子棋配置：默认值 =====
        var gomokuDefaults = new GomokuConfig();
        check("[配置] 五子棋默认值：玩家名 / 端口 5310 / 房主执黑",
            gomokuDefaults.PlayerName == "玩家" && gomokuDefaults.LastJoinAddress == ""
            && gomokuDefaults.ListenPort == 5310 && gomokuDefaults.HostSide == "black", "");
        check("[配置] 五子棋默认值：禁手关、显示禁手点开、15 路、显示棋谱",
            !gomokuDefaults.ForbiddenEnabled && gomokuDefaults.ShowForbiddenPoints
            && gomokuDefaults.BoardSize == 15 && gomokuDefaults.ShowMoveList, "");
        check("[配置] 五子棋 ToolId 常量是 gomoku", GomokuConfig.ToolId == "gomoku", GomokuConfig.ToolId);
        check("[配置] 五子棋计算属性：EffectiveBoardSize 只认 15 / 19",
            gomokuDefaults.EffectiveBoardSize == 15
            && new GomokuConfig { BoardSize = 19 }.EffectiveBoardSize == 19
            && new GomokuConfig { BoardSize = 17 }.EffectiveBoardSize == 15, "");
        check("[配置] 五子棋计算属性：EffectiveHostSide 只认 black / white（大小写不敏感）",
            gomokuDefaults.EffectiveHostSide == "black"
            && new GomokuConfig { HostSide = "WHITE" }.EffectiveHostSide == "white"
            && new GomokuConfig { HostSide = "乱写" }.EffectiveHostSide == "black"
            && new GomokuConfig { HostSide = "white" }.HostPlaysWhite, "");
        check("[配置] 五子棋计算属性：空玩家名回落「玩家」",
            new GomokuConfig { PlayerName = "   " }.EffectivePlayerName == "玩家"
            && new GomokuConfig { PlayerName = " 小明 " }.EffectivePlayerName == "小明", "");
        check("[配置] 五子棋禁手说明文案随开关变化（开启时含「三三」）",
            !gomokuDefaults.ForbiddenSummary.Contains("三三")
            && new GomokuConfig { ForbiddenEnabled = true }.ForbiddenSummary.Contains("三三"), "");
        IBoardGameRoomConfig gomokuRoom = gomokuDefaults;
        gomokuRoom.PlayerName = "接口棋手";
        gomokuRoom.LastJoinAddress = "192.168.1.9:5310";
        gomokuRoom.ListenPort = 0;
        check("[配置] 五子棋配置实现 IBoardGameRoomConfig，三字段可直接读写",
            gomokuDefaults.PlayerName == "接口棋手"
            && gomokuDefaults.LastJoinAddress == "192.168.1.9:5310"
            && gomokuDefaults.ListenPort == 0, "");

        // ===== 五子棋配置：落盘 =====
        var gomokuStore = ToolConfigStore.For(GomokuConfig.ToolId);
        var gomokuCfg = gomokuStore.Load<GomokuConfig>();
        check("[配置] 五子棋配置落在 Config\\gomoku\\config.json",
            gomokuCfg.FilePath.EndsWith(Path.Combine("Config", "gomoku", "config.json"), StringComparison.OrdinalIgnoreCase)
            && File.Exists(gomokuCfg.FilePath), gomokuCfg.FilePath);
        gomokuCfg.PlayerName = "自检棋手";
        gomokuCfg.HostSide = "white";
        gomokuCfg.ForbiddenEnabled = true;
        gomokuCfg.BoardSize = 19;
        gomokuCfg.ListenPort = 5399;
        gomokuCfg.ShowMoveList = false;
        ToolConfigStore.FlushAll();
        var gomokuReloaded = JsonFileStore.Read<GomokuConfig>(gomokuCfg.FilePath);
        check("[配置] 五子棋配置改完自动落盘、能读回",
            gomokuReloaded is
            {
                PlayerName: "自检棋手", HostSide: "white", ForbiddenEnabled: true,
                BoardSize: 19, ListenPort: 5399, ShowMoveList: false,
            },
            JsonFileStore.Serialize(gomokuReloaded).Replace(Environment.NewLine, " "));
        var gomokuJson = File.ReadAllText(gomokuCfg.FilePath);
        check("[配置] 五子棋配置是 camelCase + 中文不转义",
            gomokuJson.Contains("\"playerName\"") && gomokuJson.Contains("自检棋手")
            && !gomokuJson.Contains("\\u"), gomokuJson.Replace(Environment.NewLine, " "));
        check("[配置] 五子棋 [JsonIgnore] 计算属性不会被写进配置文件",
            !gomokuJson.Contains("effectiveBoardSize") && !gomokuJson.Contains("forbiddenSummary")
            && !gomokuJson.Contains("hostPlaysWhite"), "");
        check("[配置] 五子棋同一配置重复 Load 返回同一实例（不会两份互相覆盖）",
            ReferenceEquals(gomokuStore.Load<GomokuConfig>(), gomokuCfg), "");

        // ===== 象棋配置：默认值 =====
        var xiangqiDefaults = new XiangqiConfig();
        check("[配置] 象棋默认值：玩家名 / 端口 5311 / 房主执红",
            xiangqiDefaults.PlayerName == "玩家" && xiangqiDefaults.LastJoinAddress == ""
            && xiangqiDefaults.ListenPort == 5311 && xiangqiDefaults.HostSide == "red", "");
        check("[配置] 象棋默认值：走法提示 / 自动翻转 / 最后一步 / 坐标全开",
            xiangqiDefaults.ShowLegalMoves && xiangqiDefaults.AutoFlip
            && xiangqiDefaults.ShowLastMove && xiangqiDefaults.ShowCoordinates, "");
        check("[配置] 象棋默认值：让子方红、让先 0 步",
            xiangqiDefaults.HandicapGiver == "red" && xiangqiDefaults.OpeningFreeMoves == 0
            && xiangqiDefaults.OpeningFreeMovesEffective == 0, "");
        check("[配置] 象棋计算属性：阵营 / 让先夹取 0~5 / 执子文案",
            xiangqiDefaults.HostSideValue == XiangqiSide.Red
            && xiangqiDefaults.HandicapGiverValue == XiangqiSide.Red
            && new XiangqiConfig { OpeningFreeMoves = 9 }.OpeningFreeMovesEffective == 5
            && new XiangqiConfig { OpeningFreeMoves = -3 }.OpeningFreeMovesEffective == 0
            && new XiangqiConfig { HostSide = "black" }.HostSideText == "黑方", "");
        IBoardGameRoomConfig xiangqiRoom = xiangqiDefaults;
        xiangqiRoom.PlayerName = "接口棋手";
        xiangqiRoom.LastJoinAddress = "[::1]:5311";
        xiangqiRoom.ListenPort = 0;
        check("[配置] 象棋配置实现 IBoardGameRoomConfig，三字段可直接读写",
            xiangqiDefaults.PlayerName == "接口棋手"
            && xiangqiDefaults.LastJoinAddress == "[::1]:5311"
            && xiangqiDefaults.ListenPort == 0, "");

        // ===== 象棋配置：落盘 =====
        var xiangqiStore = ToolConfigStore.For("xiangqi");
        var xiangqiCfg = xiangqiStore.Load<XiangqiConfig>();
        check("[配置] 象棋配置落在 Config\\xiangqi\\config.json",
            xiangqiCfg.FilePath.EndsWith(Path.Combine("Config", "xiangqi", "config.json"), StringComparison.OrdinalIgnoreCase)
            && File.Exists(xiangqiCfg.FilePath), xiangqiCfg.FilePath);
        xiangqiCfg.PlayerName = "象棋自检";
        xiangqiCfg.HostSide = "black";
        xiangqiCfg.ListenPort = 5312;
        xiangqiCfg.ShowCoordinates = false;
        xiangqiCfg.OpeningFreeMoves = 3;
        ToolConfigStore.FlushAll();
        var xiangqiReloaded = JsonFileStore.Read<XiangqiConfig>(xiangqiCfg.FilePath);
        check("[配置] 象棋配置改完自动落盘、能读回",
            xiangqiReloaded is
            {
                PlayerName: "象棋自检", HostSide: "black", ListenPort: 5312,
                ShowCoordinates: false, OpeningFreeMoves: 3,
            },
            JsonFileStore.Serialize(xiangqiReloaded).Replace(Environment.NewLine, " "));
        var xiangqiJson = File.ReadAllText(xiangqiCfg.FilePath);
        check("[配置] 象棋 [JsonIgnore] 计算属性不会被写进配置文件",
            !xiangqiJson.Contains("hostSideValue") && !xiangqiJson.Contains("openingFreeMovesEffective")
            && !xiangqiJson.Contains("hostSideText"), "");
        check("[配置] 象棋配置重复 Load 返回同一实例",
            ReferenceEquals(xiangqiStore.Load<XiangqiConfig>(), xiangqiCfg), "");

        // 两个功能的配置目录必须按 Id 分开（否则会互相覆盖）
        check("[配置] 两个功能的配置目录互不相同（按工具 Id 分文件夹）",
            Path.GetDirectoryName(gomokuCfg.FilePath) != Path.GetDirectoryName(xiangqiCfg.FilePath)
            && Path.GetFileName(Path.GetDirectoryName(gomokuCfg.FilePath)!) == "gomoku"
            && Path.GetFileName(Path.GetDirectoryName(xiangqiCfg.FilePath)!) == "xiangqi", "");
    }

    // ---------------------------------------------------------------- 网络层（帧格式 / 端点 / 回环真开房间 / 断线重连）

    private static void CheckNetwork(Action<string, bool, string> checkWithDetail, Action<string, bool> plain)
    {
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);

        // 自检加速：自动重连间隔调小（基类的静态字段第一次用到时读，必须赶在构造 VM 之前设）
        Environment.SetEnvironmentVariable("YLT_BOARD_RECONNECT_MS", "300");
        Environment.SetEnvironmentVariable("YLT_BOARD_RECONNECT_TRIES", "30");

        CheckProtocolAndEndpoint(check);

        // 回环会话在后台线程跑（有 TCP 等待），主线程等它结束，避免单个卡死拖住整组自检
        Exception? failure = null;
        var report = new List<string>();
        var worker = Task.Run(async () =>
        {
            try { await RunLoopbackAsync(check, report).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; }
        });

        if (!worker.Wait(TimeSpan.FromSeconds(240)))
            failure = new TimeoutException("棋牌回环会话 240 秒没跑完（多半卡在连接上了）");

        check("[网络] 回环会话整体跑完没有抛异常", failure is null,
            failure is null
                ? string.Join("；", report)
                : $"{failure.GetType().Name}: {failure.Message} @ {failure.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
    }

    // ---------- 协议常量 / 帧格式 / 端点解析（纯逻辑，不联网） ----------

    private static void CheckProtocolAndEndpoint(Action<string, bool, string> checkWithDetail)
    {
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);

        // ===== 棋牌协议常量 =====
        check("[网络] 棋牌协议常量：magic / 版本 / 两个游戏 Id",
            BoardProtocol.Magic == "YLTBOARD1" && BoardProtocol.Version == 1
            && BoardProtocol.GameGomoku == "gomoku" && BoardProtocol.GameXiangqi == "xiangqi",
            BoardProtocol.Describe());
        var allKinds = new[]
        {
            BoardProtocol.Kinds.Hello, BoardProtocol.Kinds.Welcome, BoardProtocol.Kinds.Snapshot,
            BoardProtocol.Kinds.Action, BoardProtocol.Kinds.Chat, BoardProtocol.Kinds.Notice,
            BoardProtocol.Kinds.Request, BoardProtocol.Kinds.Response, BoardProtocol.Kinds.Bye,
        };
        check("[网络] 协议包类型齐全、值与握手约定一致、没有重复",
            allKinds.SequenceEqual(new[]
            {
                "hello", "welcome", "snapshot", "action", "chat", "notice", "request", "response", "bye",
            }), string.Join("/", allKinds));
        check("[网络] 请求类型常量用 BoardRequestTypes（undo / draw / rematch / resign）",
            new[] { BoardRequestTypes.Undo, BoardRequestTypes.Draw, BoardRequestTypes.Rematch, BoardRequestTypes.Resign }
                .SequenceEqual(new[] { "undo", "draw", "rematch", "resign" }), "");
        check("[网络] 断开原因分类齐全且互不相同（决定要不要自动重连）",
            new[]
            {
                BoardDisconnectKind.None, BoardDisconnectKind.LocalLeave,
                BoardDisconnectKind.PeerBye, BoardDisconnectKind.PeerClosed, BoardDisconnectKind.NetworkError,
            }.Distinct().Count() == 5, "");
        check("[网络] 协议说明文案含版本、magic 与包类型（排错 / 界面提示用）",
            BoardProtocol.Describe().Contains("v1") && BoardProtocol.Describe().Contains(BoardProtocol.Magic)
            && BoardProtocol.Describe().Contains("snapshot"), BoardProtocol.Describe());

        // ===== 局面包装 {seq, hostSide, clientSide, game} =====
        var gameElement = JsonSerializer.SerializeToElement(new { started = true, turn = "white", moves = 3 },
            RoomProtocol.JsonOptions);
        var wrapped = BoardProtocol.WrapSnapshot(12, "black", "white", gameElement);
        var envelope = BoardProtocol.ReadSnapshot(wrapped);
        check("[网络] 局面包装往返：seq / hostSide / clientSide / game 都在",
            envelope is { Seq: 12, HostSide: "black", ClientSide: "white" }
            && envelope.Game is { } gameInEnvelope
            && gameInEnvelope.GetProperty("turn").GetString() == "white"
            && gameInEnvelope.GetProperty("moves").GetInt32() == 3,
            JsonSerializer.Serialize(envelope, RoomProtocol.JsonOptions));
        check("[网络] 坏局面包装读不回来时返回 null（容错，不抛异常）",
            BoardProtocol.ReadSnapshot(default) is null
            && BoardProtocol.ReadSnapshot(JsonSerializer.SerializeToElement(42, RoomProtocol.JsonOptions)) is null, "");

        // ===== RoomFrame =====
        var frame = RoomFrame.New("hello", "玩家", new { text = "中文" });
        check("[网络] RoomFrame.New 自动生成帧 Id（12 位）、Kind / From 正确",
            frame.Kind == "hello" && frame.From == "玩家" && frame.Frame is { Length: 12 }, frame.Frame ?? "(null)");
        var helloFrame = RoomFrame.New("hello", "玩家").WithExtra(new HelloInfo
        {
            Magic = BoardProtocol.Magic,
            GameId = BoardProtocol.GameGomoku,
            PlayerName = "棋手",
            ProtocolVersion = BoardProtocol.Version,
        });
        check("[网络] RoomFrame 的 Extra 强类型往返（HelloInfo）",
            helloFrame.ExtraAs<HelloInfo>() is { Magic: "YLTBOARD1", GameId: "gomoku", PlayerName: "棋手", ProtocolVersion: 1 },
            helloFrame.Extra?.GetRawText() ?? "(null)");
        check("[网络] Extra 缺失 / 类型不对时 ExtraAs 返回 null（不抛）",
            RoomFrame.New("bye").ExtraAs<HelloInfo>() is null
            && RoomFrame.New("bye").WithExtra("随便字符串").ExtraAs<HelloInfo>() is null, "");
        var replyFrame = RoomFrame.New("response", "房主", null, replyTo: "abc123");
        check("[网络] 应答帧能带 ReplyTo、无 Extra 时为 null",
            replyFrame.ReplyTo == "abc123" && replyFrame.Extra is null, replyFrame.ReplyTo ?? "");

        // ===== 帧格式：4 字节小端头长 + JSON 头 + 裸载荷 =====
        check("[网络] 协议常量：4 字节长度 / 头 256KB / 载荷 4MB / 空闲 90s / 心跳 15s",
            RoomProtocol.LengthPrefixBytes == 4 && RoomProtocol.MaxHeaderBytes == 256 * 1024
            && RoomProtocol.MaxPayloadBytes == 4 * 1024 * 1024 && RoomProtocol.IdleTimeoutMs == 90_000
            && RoomProtocol.KeepAliveMs == 15_000, "");
        var payloadBytes = new byte[] { 1, 2, 3, 0xFF };
        var headerFrame = RoomFrame.New("snapshot", "自检主机");
        var built = RoomProtocol.BuildFrame(headerFrame, payloadBytes);
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(built);
        check("[网络] 帧 = 4 字节小端头长 + JSON 头 + 裸载荷",
            built.Length == 4 + headerLength + payloadBytes.Length
            && BinaryPrimitives.ReadInt32LittleEndian(built.AsSpan(0, 4)) == headerLength
            && built.AsSpan(4 + headerLength).SequenceEqual(payloadBytes), $"头 {headerLength} 字节");
        check("[网络] BuildFrame 自动把载荷长度写进头（调用方不用自己维护）",
            headerFrame.Payload == payloadBytes.Length, headerFrame.Payload.ToString());
        var headerJson = Encoding.UTF8.GetString(built, 4, headerLength);
        var parsedFrame = JsonSerializer.Deserialize<RoomFrame>(headerJson, RoomProtocol.JsonOptions);
        check("[网络] 头是 camelCase JSON（中文不转义），能反序列化回 RoomFrame",
            headerJson.Contains("\"kind\":\"snapshot\"") && headerJson.Contains("自检主机")
            && !headerJson.Contains("\\u") && parsedFrame?.Kind == "snapshot"
            && parsedFrame.Frame == headerFrame.Frame, headerJson);
        var emptyPayloadHeader = RoomFrame.New("bye");
        var emptyPayloadFrame = RoomProtocol.BuildFrame(emptyPayloadHeader);
        check("[网络] 空载荷也是合法帧（头里写的载荷长度是 0）",
            emptyPayloadHeader.Payload == 0
            && BinaryPrimitives.ReadInt32LittleEndian(emptyPayloadFrame) == emptyPayloadFrame.Length - 4, "");
        check("[网络] IsValidFrameLength：正数且在头上限内才算合法（负数 / 0 / 超限都拒）",
            RoomProtocol.IsValidFrameLength(1) && RoomProtocol.IsValidFrameLength(RoomProtocol.MaxHeaderBytes)
            && !RoomProtocol.IsValidFrameLength(0) && !RoomProtocol.IsValidFrameLength(-1)
            && !RoomProtocol.IsValidFrameLength(RoomProtocol.MaxHeaderBytes + 1), "");
        var oversizedHeaderThrew = false;
        try { _ = RoomProtocol.BuildFrame(RoomFrame.New(new string('x', RoomProtocol.MaxHeaderBytes + 100))); }
        catch (InvalidOperationException) { oversizedHeaderThrew = true; }
        check("[网络] 头超过 256KB → 拒绝并抛异常（防 allocation DoS）", oversizedHeaderThrew);
        var oversizedPayloadThrew = false;
        try { _ = RoomProtocol.BuildFrame(RoomFrame.New("bye"), new byte[RoomProtocol.MaxPayloadBytes + 1]); }
        catch (InvalidOperationException) { oversizedPayloadThrew = true; }
        check("[网络] 载荷超过 4MB → 拒绝并抛异常", oversizedPayloadThrew);
        check("[网络] JsonOptions：camelCase + 大小写不敏感（容错读）",
            RoomProtocol.JsonOptions.PropertyNamingPolicy?.ConvertName("Kind") == "kind"
            && RoomProtocol.JsonOptions.PropertyNameCaseInsensitive, "");

        // ===== 端点解析 =====
        static bool EndpointOk(string text, string host, int port)
        {
            var (ok, parsedHost, parsedPort, error) = RoomProtocol.ParseEndpoint(text);
            return ok && error.Length == 0 && parsedHost == host && parsedPort == port;
        }

        static bool EndpointBad(string text, string errorPart)
        {
            var (ok, _, _, error) = RoomProtocol.ParseEndpoint(text);
            return !ok && error.Contains(errorPart);
        }

        check("[网络] 端点解析：纯 IPv4 / IPv4 带端口",
            EndpointOk("192.168.1.5", "192.168.1.5", 0)
            && EndpointOk("192.168.1.5:5310", "192.168.1.5", 5310), "");
        check("[网络] 端点解析：域名 / 域名带端口",
            EndpointOk("game.example.com", "game.example.com", 0)
            && EndpointOk("game.example.com:5310", "game.example.com", 5310), "");
        check("[网络] 端点解析：IPv6 带方括号 / 带方括号带端口",
            EndpointOk("[::1]", "::1", 0) && EndpointOk("[fe80::1]:5310", "fe80::1", 5310), "");
        check("[网络] 端点解析：不带方括号的 IPv6 整串当主机名",
            EndpointOk("2001:db8::1", "2001:db8::1", 0), "");
        check("[网络] 端点解析：空 / 全空白 → 中文错误",
            EndpointBad("", "请填写") && EndpointBad("   ", "请填写"), "");
        check("[网络] 端点解析：IPv6 缺右方括号 → 中文错误", EndpointBad("[::1", "括号"));
        check("[网络] 端点解析：端口不是数字 → 中文错误", EndpointBad("host:abc", "端口"));
        check("[网络] 端点解析：端口越界（70000 / -5）→ 中文错误",
            EndpointBad("host:70000", "端口") && EndpointBad("host:-5", "端口"), "");
        check("[网络] 端点格式化：IPv6 自动加方括号（能直接复制给人）",
            RoomProtocol.FormatEndpoint(IPAddress.Parse("192.168.1.5"), 5310) == "192.168.1.5:5310"
            && RoomProtocol.FormatEndpoint(IPAddress.IPv6Loopback, 5310) == "[::1]:5310", "");
        check("[网络] WithPort：IPv4 / 带方括号 IPv6 / 不带方括号 IPv6 / 空串",
            RoomProtocol.WithPort("192.168.1.5", 5310) == "192.168.1.5:5310"
            && RoomProtocol.WithPort("[::1]", 5310) == "[::1]:5310"
            && RoomProtocol.WithPort("::1", 5310) == "[::1]:5310"
            && RoomProtocol.WithPort("", 5310) == "", "");

        // ===== ResolveAsync（IP 直接解析，不查 DNS） =====
        var (v4ResolveOk, v4EndPoint, v4Display, v4ResolveError) =
            RoomProtocol.ResolveAsync("127.0.0.1", 5310).GetAwaiter().GetResult();
        check("[网络] ResolveAsync：IP 直接解析成端点",
            v4ResolveOk && v4EndPoint is IPEndPoint { Port: 5310 } && v4Display == "127.0.0.1:5310"
            && v4ResolveError.Length == 0, v4Display);
        var (v6ResolveOk, _, v6Display, _) = RoomProtocol.ResolveAsync("::1", 5310).GetAwaiter().GetResult();
        check("[网络] ResolveAsync：IPv6 显示成 [::1]:5310", v6ResolveOk && v6Display == "[::1]:5310", v6Display);
        var (badPortResolveOk, _, _, badPortResolveError) =
            RoomProtocol.ResolveAsync("127.0.0.1", 0).GetAwaiter().GetResult();
        check("[网络] ResolveAsync：端口 0 → 中文错误", !badPortResolveOk && badPortResolveError.Contains("端口"),
            badPortResolveError);
        var (emptyHostResolveOk, _, _, emptyHostResolveError) =
            RoomProtocol.ResolveAsync("  ", 5310).GetAwaiter().GetResult();
        check("[网络] ResolveAsync：空主机 → 中文错误", !emptyHostResolveOk && emptyHostResolveError.Contains("主机"),
            emptyHostResolveError);

        // ===== 傻瓜模式挑监听（纯逻辑，端口探测传假函数） =====
        var v6Plan = RoomProtocol.ChooseSimpleListenPlan(5310, true, (_, _) => true);
        check("[网络] 监听方案：支持 IPv6 时用双栈 :: 并沿用首选端口",
            v6Plan.Address == "::" && v6Plan.Port == 5310 && v6Plan.DualStack
            && v6Plan.Reason.Contains("5310"), v6Plan.Reason);
        var v4Plan = RoomProtocol.ChooseSimpleListenPlan(5310, false, (_, _) => true);
        check("[网络] 监听方案：不支持 IPv6 时用 0.0.0.0",
            v4Plan.Address == "0.0.0.0" && !v4Plan.DualStack, v4Plan.Reason);
        var busyPlan = RoomProtocol.ChooseSimpleListenPlan(5310, true, (_, _) => false);
        check("[网络] 监听方案：首选端口被占 → 让系统挑（端口 0）",
            busyPlan.Port == 0 && busyPlan.Reason.Contains("占用"), busyPlan.Reason);
        var autoPlan = RoomProtocol.ChooseSimpleListenPlan(0, true, (_, _) => true);
        check("[网络] 监听方案：没设过端口 → 端口 0", autoPlan.Port == 0, autoPlan.Reason);

        // ===== 本机可分享地址 =====
        var shares = RoomProtocol.ListShareAddresses();
        static bool WorthSharing(RoomProtocol.ShareAddress address)
        {
            var text = address.Text.Trim('[', ']');
            if (!IPAddress.TryParse(text, out var ip)) return false;
            if (IPAddress.IsLoopback(ip)) return false;
            if (ip.AddressFamily == AddressFamily.InterNetwork) return !text.StartsWith("169.254.");
            return !ip.IsIPv6LinkLocal && !ip.IsIPv6Multicast && !ip.IsIPv6SiteLocal;
        }

        check("[网络] 本机分享地址：去掉回环 / 169.254 / 链路本地（每条都能告诉对方）",
            shares.All(WorthSharing), string.Join(" | ", shares.Select(a => $"{a.Family}:{a.Text}")));
        check("[网络] 本机分享地址：IPv6 带方括号、每族最多推荐一条",
            shares.Where(a => a.Family == "IPv6").All(a => a.Text.StartsWith('[') && a.Text.EndsWith(']'))
            && shares.Count(a => a.Family == "IPv4" && a.IsRecommended) <= 1
            && shares.Count(a => a.Family == "IPv6" && a.IsRecommended) <= 1, "");
        check("[网络] HasUsableIPv6 与地址列表一致、没有 IPv6 时有给用户看的一句话",
            RoomProtocol.HasUsableIPv6 == shares.Any(a => a.Family == "IPv6")
            && RoomProtocol.NoIpv6Text.Contains("IPv6"), RoomProtocol.NoIpv6Text);
    }

    // ---------- 本机回环：五子棋完整小对局 + 断线重连 + 第三人拒绝 + 象棋同步 ----------

    private static async Task RunLoopbackAsync(Action<string, bool, string> check, List<string> report)
    {
        await RunGomokuSessionAsync(check, report).ConfigureAwait(false);
        await RunReconnectSessionAsync(check, report).ConfigureAwait(false);
        await RunHostClosingAsync(check, report).ConfigureAwait(false);
        await RunAbruptCutAsync(check, report).ConfigureAwait(false);
        await RunXiangqiSessionAsync(check, report).ConfigureAwait(false);
        await RunConnectFailureAsync(check, report).ConfigureAwait(false);
        await RunRoomViewModelAsync(check, report).ConfigureAwait(false);
    }

    /// <summary>A. 五子棋完整小对局：握手 / 聊天 / 快照 / 动作 / 请求应答 / 五连获胜后客户端收到结果快照。</summary>
    private static async Task RunGomokuSessionAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "自检主机");
        var client = new BoardRoomSession(BoardProtocol.GameGomoku, "自检客户端");
        var game = new GomokuGame();
        var gate = new object();
        var seq = 0;
        JsonElement Wrap()
        {
            lock (gate) return BoardProtocol.WrapSnapshot(++seq, "black", "white", GameJson(game));
        }

        var initialWrapped = Wrap();
        var clientSnapshots = new ConcurrentQueue<JsonElement>();
        var clientChats = new ConcurrentQueue<(string Name, string Text)>();
        var hostChats = new ConcurrentQueue<(string Name, string Text)>();
        var clientNotices = new ConcurrentQueue<(string Text, string Level)>();
        var hostActions = new ConcurrentQueue<(string Type, JsonElement? Payload)>();
        var hostRequests = new ConcurrentQueue<string>();
        var clientResponses = new ConcurrentQueue<(string Type, bool Accept)>();
        var hostPeerJoined = new ConcurrentQueue<string>();
        var clientPeerJoined = new ConcurrentQueue<string>();

        client.SnapshotReceived += e => clientSnapshots.Enqueue(e);
        client.ChatReceived += (name, text) => clientChats.Enqueue((name, text));
        host.ChatReceived += (name, text) => hostChats.Enqueue((name, text));
        client.NoticeReceived += (text, level) => clientNotices.Enqueue((text, level));
        host.PeerJoined += name => hostPeerJoined.Enqueue(name);
        client.PeerJoined += name => clientPeerJoined.Enqueue(name);
        host.RequestReceived += (type, _) => hostRequests.Enqueue(type);
        client.ResponseReceived += (type, accept, _) => clientResponses.Enqueue((type, accept));

        // 主机收到客户端的走子动作 → 落到棋局 → 广播最新局面（模拟游戏 VM 的 OnHostAction）
        void ApplyClientMove(JsonElement? payload)
        {
            if (payload is not { } move
                || !move.TryGetProperty("x", out var x) || !move.TryGetProperty("y", out var y)) return;
            lock (gate) game.Play(GomokuCell.White, x.GetInt32(), y.GetInt32());
            _ = host.BroadcastSnapshotAsync(Wrap());
        }

        host.ActionReceived += (type, payload) =>
        {
            hostActions.Enqueue((type, payload));
            if (type == "move") ApplyClientMove(payload);
        };

        async Task HostMoveAsync(int x, int y)
        {
            lock (gate) game.Play(GomokuCell.Black, x, y);
            await host.BroadcastSnapshotAsync(Wrap()).ConfigureAwait(false);
        }

        async Task ClientMoveAsync(int x, int y)
        {
            int target;
            lock (gate) target = game.MoveCount + 1;
            await client.SendActionAsync("move", new { x, y }).ConfigureAwait(false);
            await WaitUntilAsync(() => { lock (gate) return game.MoveCount == target; }, 5000).ConfigureAwait(false);
        }

        try
        {
            var startError = await host.StartHostAsync(0, () => new BoardWelcome("white", initialWrapped))
                .ConfigureAwait(false);
            check("[网络] 主机用端口 0 开房成功（系统分配真实端口）",
                startError.Length == 0 && host.IsHost && host.IsOnline && host.ListenPort > 0,
                startError.Length > 0 ? startError : host.ListenText);
            check("[网络] 主机报得出监听地址与端口（ListenText 含真实端口）",
                host.ListenPort > 0 && host.ListenText.Contains(host.ListenPort.ToString()), host.ListenText);
            check("[网络] 双栈监听：支持 IPv6 时监听 :: 且双栈，否则 0.0.0.0",
                host.IsDualStack == Socket.OSSupportsIPv6,
                $"dual={host.IsDualStack} supportsIPv6={Socket.OSSupportsIPv6} listen={host.ListenText}");
            report.Add($"主机监听 {host.ListenText}");

            var connectError = await client.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            check("[网络] 客户端连上并完成握手（hello → welcome）",
                connectError.Length == 0 && client.IsOnline, connectError);
            await WaitUntilAsync(() => host.HasPeer && client.HasPeer && hostPeerJoined.Count >= 1, 8000)
                .ConfigureAwait(false);
            check("[网络] 双方都认为对方在线（HasPeer）", host.HasPeer && client.HasPeer,
                $"host={host.HasPeer} client={client.HasPeer}");
            check("[网络] 主机知道对手名字、客户端知道房主名字",
                host.PeerName == "自检客户端" && client.PeerName == "自检主机",
                $"{host.PeerName} / {client.PeerName}");
            check("[网络] 握手时主机触发 PeerJoined、客户端也触发",
                hostPeerJoined.Contains("自检客户端") && clientPeerJoined.Contains("自检主机"),
                $"{string.Join(",", hostPeerJoined)} / {string.Join(",", clientPeerJoined)}");
            check("[网络] 握手回执里带了包装局面（客户端执白、seq 有效）",
                clientSnapshots.Count >= 1
                && BoardProtocol.ReadSnapshot(clientSnapshots.First()) is { ClientSide: "white", Seq: > 0 },
                clientSnapshots.Count.ToString());

            // 聊天 + 系统提示
            await host.SendChatAsync("主机说：你好").ConfigureAwait(false);
            await WaitUntilAsync(() => clientChats.Any(c => c.Text == "主机说：你好"), 8000).ConfigureAwait(false);
            check("[网络] 主机 → 客户端聊天送达（带发送者名字）",
                clientChats.Any(c => c.Name == "自检主机" && c.Text == "主机说：你好"),
                string.Join(" | ", clientChats.Select(c => $"{c.Name}:{c.Text}")));
            await client.SendChatAsync("客户端说：收到").ConfigureAwait(false);
            await WaitUntilAsync(() => hostChats.Any(c => c.Text == "客户端说：收到"), 8000).ConfigureAwait(false);
            check("[网络] 客户端 → 主机聊天送达",
                hostChats.Any(c => c.Name == "自检客户端" && c.Text == "客户端说：收到"),
                string.Join(" | ", hostChats.Select(c => $"{c.Name}:{c.Text}")));
            await host.SendNoticeAsync("测试系统提示", "warn").ConfigureAwait(false);
            await WaitUntilAsync(() => clientNotices.Any(n => n.Text == "测试系统提示"), 8000).ConfigureAwait(false);
            check("[网络] 系统提示（notice）带 level 送达",
                clientNotices.Any(n => n.Text == "测试系统提示" && n.Level == "warn"),
                string.Join(" | ", clientNotices.Select(n => $"{n.Level}:{n.Text}")));

            // 快照广播
            var seqBeforeBroadcast = seq;
            await host.BroadcastSnapshotAsync(Wrap(), "对局开始").ConfigureAwait(false);
            await WaitUntilAsync(
                () => clientSnapshots.Any(e => BoardProtocol.ReadSnapshot(e)?.Seq == seqBeforeBroadcast + 1), 8000)
                .ConfigureAwait(false);
            check("[网络] 主机广播快照 → 客户端收到（seq 递增）",
                BoardProtocol.ReadSnapshot(clientSnapshots.Last())?.Seq >= seqBeforeBroadcast + 1,
                $"seq={seqBeforeBroadcast} → {BoardProtocol.ReadSnapshot(clientSnapshots.Last())?.Seq}");

            // 五子棋完整小对局：黑（主机）先连成五子
            await HostMoveAsync(0, 0).ConfigureAwait(false);
            await ClientMoveAsync(7, 7).ConfigureAwait(false);
            await HostMoveAsync(1, 0).ConfigureAwait(false);
            await ClientMoveAsync(7, 8).ConfigureAwait(false);
            await HostMoveAsync(2, 0).ConfigureAwait(false);
            await ClientMoveAsync(7, 9).ConfigureAwait(false);
            await HostMoveAsync(3, 0).ConfigureAwait(false);
            await ClientMoveAsync(8, 7).ConfigureAwait(false);
            await HostMoveAsync(4, 0).ConfigureAwait(false);
            await WaitUntilAsync(() => SnapshotGomokuResult(clientSnapshots.LastOrDefault()) == "blackWin", 8000)
                .ConfigureAwait(false);

            var lastEnvelope = BoardProtocol.ReadSnapshot(clientSnapshots.Last());
            var clientGame = RestoreGomoku(clientSnapshots.Last());
            check("[网络] 五子棋完整小对局：主机五连获胜后客户端收到结果快照",
                SnapshotGomokuResult(clientSnapshots.Last()) == "blackWin"
                && lastEnvelope is { HostSide: "black", ClientSide: "white" },
                SnapshotGomokuResult(clientSnapshots.Last()) ?? "(无结果)");
            check("[网络] 客户端按快照重建棋局：黑棋获胜、获胜连线 5 颗",
                clientGame is { Result: GomokuResult.BlackWin } && clientGame.WinningLine.Count == 5,
                clientGame?.ResultText ?? "(无法重建)");
            check("[网络] 主机端棋局：9 手（黑 5 + 白 4）且黑胜",
                game.Result == GomokuResult.BlackWin && game.MoveCount == 9,
                $"{game.ResultText} / {game.MoveCount} 手");
            check("[网络] 客户端发来的动作主机都收到并落盘（4 步）",
                hostActions.Count(a => a.Type == "move") >= 4,
                hostActions.Count.ToString());
            var firstAction = hostActions.FirstOrDefault(a => a.Type == "move");
            check("[网络] 动作 payload（{x,y}）原样送到主机",
                firstAction.Payload is { } actionPayload
                && actionPayload.GetProperty("x").GetInt32() == 7
                && actionPayload.GetProperty("y").GetInt32() == 7,
                firstAction.Payload?.GetRawText() ?? "(null)");

            // 请求 / 应答
            await client.SendRequestAsync(BoardRequestTypes.Undo).ConfigureAwait(false);
            await WaitUntilAsync(() => hostRequests.Contains(BoardRequestTypes.Undo), 8000).ConfigureAwait(false);
            check("[网络] 请求（悔棋）送达主机，类型用常量",
                hostRequests.Contains(BoardRequestTypes.Undo), string.Join(",", hostRequests));
            await host.SendResponseAsync(BoardRequestTypes.Undo, false).ConfigureAwait(false);
            await WaitUntilAsync(
                () => clientResponses.Any(r => r.Type == BoardRequestTypes.Undo), 8000).ConfigureAwait(false);
            check("[网络] 应答（拒绝悔棋 accept=false）送达客户端",
                clientResponses.Any(r => r.Type == BoardRequestTypes.Undo && !r.Accept),
                string.Join(",", clientResponses.Select(r => $"{r.Type}:{r.Accept}")));
            await client.SendRequestAsync(BoardRequestTypes.Draw).ConfigureAwait(false);
            await WaitUntilAsync(() => hostRequests.Contains(BoardRequestTypes.Draw), 8000).ConfigureAwait(false);
            await host.SendResponseAsync(BoardRequestTypes.Draw, true).ConfigureAwait(false);
            await WaitUntilAsync(
                () => clientResponses.Any(r => r.Type == BoardRequestTypes.Draw), 8000).ConfigureAwait(false);
            check("[网络] 应答（同意求和 accept=true）送达客户端",
                clientResponses.Any(r => r.Type == BoardRequestTypes.Draw && r.Accept), "");

            await client.StopAsync("自检结束").ConfigureAwait(false);
            await WaitUntilAsync(() => !host.HasPeer, 8000).ConfigureAwait(false);
            check("[网络] 一方主动 Stop：本端分类 LocalLeave、对端分类 PeerBye（不当成网络故障）",
                client.LastDisconnectKind == BoardDisconnectKind.LocalLeave
                && host.LastDisconnectKind == BoardDisconnectKind.PeerBye,
                $"client={client.LastDisconnectKind} host={host.LastDisconnectKind}");
            await host.StopAsync("自检结束").ConfigureAwait(false);
            check("[网络] 会话 Stop 后不再联机（监听已关 / 连接已断）",
                !host.IsOnline && !client.IsOnline && !host.IsHost, "");
        }
        finally
        {
            await SafeStopAsync(client).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>B. 客户端掉线重连（用户点名的验收重点）：主机保持房间与棋局；新客户端拿到最新局面并能继续下。</summary>
    private static async Task RunReconnectSessionAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "重连主机");
        var game = new GomokuGame();
        var gate = new object();
        var seq = 0;
        var welcomeCount = 0;
        JsonElement Wrap()
        {
            lock (gate) return BoardProtocol.WrapSnapshot(++seq, "black", "white", GameJson(game));
        }

        var current = Wrap();
        var hostPeerJoined = new ConcurrentQueue<string>();
        var hostPeerLeft = new ConcurrentQueue<string>();
        var hostActions = new ConcurrentQueue<(string Type, JsonElement? Payload)>();
        var clientSnapshots = new ConcurrentQueue<JsonElement>();

        host.PeerJoined += name => hostPeerJoined.Enqueue(name);
        host.PeerLeft += reason => hostPeerLeft.Enqueue(reason);
        host.ActionReceived += (type, payload) =>
        {
            hostActions.Enqueue((type, payload));
            if (type != "move" || payload is not { } move) return;
            lock (gate)
            {
                game.Play(GomokuCell.White,
                    move.GetProperty("x").GetInt32(), move.GetProperty("y").GetInt32());
                current = Wrap();               // welcome 用「最新」：一定要在广播前更新
            }
            _ = host.BroadcastSnapshotAsync(current);
        };

        try
        {
            var startError = await host.StartHostAsync(0, () =>
            {
                Interlocked.Increment(ref welcomeCount);
                return new BoardWelcome("white", current);
            }).ConfigureAwait(false);
            check("[重连] 主机开房成功（端口 0）", startError.Length == 0 && host.IsOnline,
                startError.Length > 0 ? startError : host.ListenText);
            var port = host.ListenPort;

            var client1 = new BoardRoomSession(BoardProtocol.GameGomoku, "掉线客户端");
            var joinError = await client1.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            await WaitUntilAsync(() => host.HasPeer && client1.HasPeer, 8000).ConfigureAwait(false);
            check("[重连] 客户端加入成功（握手完成）",
                joinError.Length == 0 && client1.IsOnline && host.HasPeer, joinError);

            // 下两步：主机黑 (0,0)，客户端白 (7,7)
            lock (gate) game.Play(GomokuCell.Black, 0, 0);
            current = Wrap();
            await host.BroadcastSnapshotAsync(current).ConfigureAwait(false);
            await client1.SendActionAsync("move", new { x = 7, y = 7 }).ConfigureAwait(false);
            await WaitUntilAsync(() => { lock (gate) return game.MoveCount == 2; }, 8000).ConfigureAwait(false);

            int movesBefore;
            int seqBefore;
            lock (gate)
            {
                movesBefore = game.MoveCount;
                seqBefore = seq;
            }

            // 模拟客户端掉线（Dispose = 直接断开这条连接）
            await client1.DisposeAsync().ConfigureAwait(false);
            await WaitUntilAsync(() => !host.HasPeer, 8000).ConfigureAwait(false);
            int movesAfter;
            int seqAfter;
            lock (gate)
            {
                movesAfter = game.MoveCount;
                seqAfter = seq;
            }

            check("[重连] 客户端掉线后：主机仍在监听（房间不关、端口不变）",
                host.IsOnline && host.IsHost && host.ListenPort == port, host.ListenText);
            check("[重连] 客户端掉线后：主机 HasPeer=false（对局暂停）", !host.HasPeer, "");
            check("[重连] 客户端掉线后：主机棋局状态完全不变（手数 / 快照 seq / 结果都不动）",
                movesAfter == movesBefore && seqAfter == seqBefore && game.Result == GomokuResult.Playing,
                $"手数 {movesBefore}→{movesAfter}，seq {seqBefore}→{seqAfter}，结果 {game.Result}");
            check("[重连] 客户端掉线后：主机收到 PeerLeft（原因可读）",
                hostPeerLeft.Count >= 1 && !string.IsNullOrWhiteSpace(hostPeerLeft.Last()),
                hostPeerLeft.LastOrDefault() ?? "(无)");

            // 新客户端重新连同一端口
            var client2 = new BoardRoomSession(BoardProtocol.GameGomoku, "重连客户端");
            client2.SnapshotReceived += e => clientSnapshots.Enqueue(e);
            try
            {
                var reconnectError = await client2.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                await WaitUntilAsync(() => client2.HasPeer && clientSnapshots.Count >= 1, 8000).ConfigureAwait(false);
                check("[重连] 新客户端能连上同一端口并完成握手",
                    reconnectError.Length == 0 && client2.IsOnline && client2.HasPeer, reconnectError);
                check("[重连] 重连时 welcomeFactory 又调用了一次（每次握手都给最新局面）",
                    welcomeCount == 2, welcomeCount.ToString());
                var welcomeEnvelope = BoardProtocol.ReadSnapshot(clientSnapshots.First());
                var welcomeGame = RestoreGomoku(clientSnapshots.First());
                check("[重连] 重连拿到的最新快照与断线前一致（seq / 手数 / 轮到谁）",
                    welcomeEnvelope is { Seq: var welcomeSeq } && welcomeSeq >= seqBefore
                    && welcomeGame is { MoveCount: var welcomeMoves } && welcomeMoves == movesBefore
                    && welcomeGame.Turn == GomokuCell.Black,
                    $"seq={welcomeEnvelope?.Seq} moves={welcomeGame?.MoveCount} turn={welcomeGame?.Turn}");
                check("[重连] 重连后主机再次触发 PeerJoined（重连被当成一次新的接入）",
                    hostPeerJoined.Count >= 2, string.Join(",", hostPeerJoined));

                // 继续对局：主机再走一步（黑），客户端收到快照
                lock (gate) game.Play(GomokuCell.Black, 1, 0);
                current = Wrap();
                await host.BroadcastSnapshotAsync(current).ConfigureAwait(false);
                await WaitUntilAsync(() => SnapshotMoveCount(clientSnapshots.LastOrDefault()) == movesBefore + 1, 8000)
                    .ConfigureAwait(false);
                check("[重连] 重连后：主机继续走棋，客户端收到最新快照",
                    SnapshotMoveCount(clientSnapshots.LastOrDefault()) == movesBefore + 1,
                    SnapshotMoveCount(clientSnapshots.LastOrDefault())?.ToString() ?? "(无)");

                // 客户端继续发动作 → 主机执行并广播回来
                var targetMoves = movesBefore + 2;
                await client2.SendActionAsync("move", new { x = 7, y = 8 }).ConfigureAwait(false);
                await WaitUntilAsync(() => { lock (gate) return game.MoveCount == targetMoves; }, 8000)
                    .ConfigureAwait(false);
                await WaitUntilAsync(() => SnapshotMoveCount(clientSnapshots.LastOrDefault()) == targetMoves, 8000)
                    .ConfigureAwait(false);
                check("[重连] 重连后：客户端继续发动作，主机执行并广播回来",
                    hostActions.Any(a => a.Type == "move") && SnapshotMoveCount(clientSnapshots.LastOrDefault()) == targetMoves,
                    $"手数={game.MoveCount} 快照手数={SnapshotMoveCount(clientSnapshots.LastOrDefault())}");

                // 第三人：房间已满
                var third = new BoardRoomSession(BoardProtocol.GameGomoku, "第三个人");
                var thirdNotices = new ConcurrentQueue<string>();
                third.NoticeReceived += (text, _) => thirdNotices.Enqueue(text);
                try
                {
                    var thirdError = await third.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                    check("[重连] 第三人被拒：握手失败并收到「房间已满」提示",
                        thirdError.Length > 0 && thirdNotices.Any(t => t.Contains("房间已满")),
                        $"{thirdError} / {string.Join(" | ", thirdNotices)}");
                    check("[重连] 第三人被拒后，房间里原有的两个人不受影响",
                        host.HasPeer && client2.IsOnline, "");
                }
                finally
                {
                    await SafeStopAsync(third).ConfigureAwait(false);
                }
            }
            finally
            {
                await SafeStopAsync(client2).ConfigureAwait(false);
            }

            await host.StopAsync("自检结束").ConfigureAwait(false);
            check("[重连] 主机关房后不再监听", !host.IsOnline && !host.IsHost, "");
        }
        finally
        {
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>C. 主机关房：客户端收到 bye 里的原因、不再联机。</summary>
    private static async Task RunHostClosingAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "关房主机");
        var client = new BoardRoomSession(BoardProtocol.GameGomoku, "被关房客户端");
        var peerLeft = new ConcurrentQueue<string>();
        client.PeerLeft += reason => peerLeft.Enqueue(reason);
        try
        {
            await host.StartHostAsync(0, () => new BoardWelcome("white",
                BoardProtocol.WrapSnapshot(1, "black", "white", GameJson(new GomokuGame())))).ConfigureAwait(false);
            var joinError = await client.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.HasPeer, 8000).ConfigureAwait(false);
            check("[网络] 关房场景：客户端已连上", joinError.Length == 0 && host.HasPeer, joinError);

            await host.StopAsync("房主关闭了房间").ConfigureAwait(false);
            await WaitUntilAsync(() => peerLeft.Count >= 1, 8000).ConfigureAwait(false);
            check("[网络] 主机关房 → 客户端收到可读的断开原因（bye 里的「房主关闭了房间」）",
                peerLeft.Any(r => r.Contains("房主")), string.Join(" | ", peerLeft));
            check("[网络] 主机关房 → 断开原因分类是 PeerBye（客户端据此决定不自动重连）",
                client.LastDisconnectKind == BoardDisconnectKind.PeerBye,
                $"{client.LastDisconnectKind}: {client.LastDisconnectReason}");
            check("[网络] 主机关房 → 客户端变成未连接（不会一直显示已连接）",
                !client.IsOnline && !client.HasPeer, "");
            check("[网络] 主机关房 → 客户端不会自动重连（会话层没有重连循环，仍是同一状态）",
                !client.IsOnline, "");
        }
        finally
        {
            await SafeStopAsync(client).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>D. 网络被掐断（AbortConnection 不发 bye）：双方都会收到断开，但原因分类与房主关房不同。</summary>
    private static async Task RunAbruptCutAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "断网主机");
        var client = new BoardRoomSession(BoardProtocol.GameGomoku, "断网客户端");
        var peerLeft = new ConcurrentQueue<string>();
        client.PeerLeft += reason => peerLeft.Enqueue(reason);
        try
        {
            await host.StartHostAsync(0, () => new BoardWelcome("white",
                BoardProtocol.WrapSnapshot(1, "black", "white", GameJson(new GomokuGame())))).ConfigureAwait(false);
            var joinError = await client.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.HasPeer && client.HasPeer, 8000).ConfigureAwait(false);
            check("[网络] 断网场景：客户端连上主机", joinError.Length == 0 && host.HasPeer, joinError);

            // 「网络断了」：不发 bye，直接掐掉底层连接
            client.AbortConnection("模拟断网");
            await WaitUntilAsync(() => peerLeft.Count >= 1, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => host.LastDisconnectKind != BoardDisconnectKind.None, 8000).ConfigureAwait(false);

            check("[网络] AbortConnection 模拟断网：本端分类 NetworkError（不是主动离开）",
                client.LastDisconnectKind == BoardDisconnectKind.NetworkError,
                $"{client.LastDisconnectKind}: {client.LastDisconnectReason}");
            check("[网络] AbortConnection 对端：分类是 PeerClosed / NetworkError（不是 PeerBye）",
                host.LastDisconnectKind is BoardDisconnectKind.PeerClosed or BoardDisconnectKind.NetworkError,
                $"{host.LastDisconnectKind}: {host.LastDisconnectReason}");
            check("[网络] 断线原因区分：房主关房 = PeerBye +「房主…」，网络掐断两者都不是",
                client.LastDisconnectKind != BoardDisconnectKind.PeerBye
                && host.LastDisconnectKind != BoardDisconnectKind.PeerBye
                && host.LastDisconnectReason.Length > 0 && !host.LastDisconnectReason.Contains("房主"),
                $"{client.LastDisconnectKind}/{host.LastDisconnectKind}: {host.LastDisconnectReason}");
        }
        finally
        {
            await SafeStopAsync(client).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>E. 象棋一步同步：主机走红「炮二平五」→ 客户端重建局面 → 客户端走黑「马8进7」→ 主机广播回来。</summary>
    private static async Task RunXiangqiSessionAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameXiangqi, "象棋主机");
        var client = new BoardRoomSession(BoardProtocol.GameXiangqi, "象棋客户端");
        var game = new XiangqiGame();
        var gate = new object();
        var seq = 0;
        JsonElement Wrap()
        {
            lock (gate) return BoardProtocol.WrapSnapshot(++seq, "red", "black", GameJson(game));
        }

        var current = Wrap();
        var clientSnapshots = new ConcurrentQueue<JsonElement>();
        var hostActions = new ConcurrentQueue<string>();
        client.SnapshotReceived += e => clientSnapshots.Enqueue(e);
        host.ActionReceived += (type, payload) =>
        {
            hostActions.Enqueue(type);
            if (type != "move" || payload is not { } move) return;
            lock (gate)
            {
                game.Play(XiangqiSide.Black,
                    move.GetProperty("fromX").GetInt32(), move.GetProperty("fromY").GetInt32(),
                    move.GetProperty("toX").GetInt32(), move.GetProperty("toY").GetInt32());
            }
            _ = host.BroadcastSnapshotAsync(Wrap());
        };

        try
        {
            await host.StartHostAsync(0, () => new BoardWelcome("black", current)).ConfigureAwait(false);
            var joinError = await client.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.HasPeer && client.HasPeer, 8000).ConfigureAwait(false);
            check("[网络] 象棋房间：客户端连上（房主执红、客户端执黑）",
                joinError.Length == 0 && host.HasPeer && client.HasPeer, joinError);

            // 主机走红：炮二平五
            lock (gate) game.Play(XiangqiSide.Red, 7, 7, 4, 7);
            current = Wrap();
            await host.BroadcastSnapshotAsync(current, "对局开始").ConfigureAwait(false);
            await WaitUntilAsync(() => SnapshotXiangqiHistory(clientSnapshots.LastOrDefault()) >= 1, 8000)
                .ConfigureAwait(false);
            var clientGame = RestoreXiangqi(clientSnapshots.LastOrDefault());
            check("[网络] 象棋一步同步：客户端按快照重建出「炮二平五」",
                clientGame is { History.Count: 1 } && clientGame.History[0].Notation == "炮二平五"
                && clientGame.Turn == XiangqiSide.Black,
                clientGame is null ? "(无法重建)" : string.Join(" / ", clientGame.History.Select(h => h.Notation)));

            // 客户端走黑：马8进7
            await client.SendActionAsync("move", new { fromX = 7, fromY = 0, toX = 6, toY = 2 })
                .ConfigureAwait(false);
            await WaitUntilAsync(() => { lock (gate) return game.History.Count == 2; }, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => SnapshotXiangqiHistory(clientSnapshots.LastOrDefault()) >= 2, 8000)
                .ConfigureAwait(false);
            var clientGame2 = RestoreXiangqi(clientSnapshots.LastOrDefault());
            check("[网络] 象棋一步同步：客户端的动作主机执行并广播回来（马8进7）",
                hostActions.Contains("move") && clientGame2 is { History.Count: 2 }
                && clientGame2.History[1].Notation == "马8进7",
                clientGame2 is null ? "(无法重建)" : string.Join(" / ", clientGame2.History.Select(h => h.Notation)));
        }
        finally
        {
            await SafeStopAsync(client).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>F. 连不上 / 连错房间：中文错误、不崩、之后还能重新连接。</summary>
    private static async Task RunConnectFailureAsync(Action<string, bool, string> check, List<string> report)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var orphan = new BoardRoomSession(BoardProtocol.GameGomoku, "连不上的人");
        try
        {
            var error = await orphan.ConnectAsync("127.0.0.1", deadPort).ConfigureAwait(false);
            check("[网络] 连一个没开房的端口 → 中文错误、不崩",
                error.Length > 0 && !orphan.IsOnline, error);
        }
        finally
        {
            await SafeStopAsync(orphan).ConfigureAwait(false);
        }

        // 游戏类型对不上（五子棋房间 + 象棋客户端）
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "五子棋房主");
        var wrongClient = new BoardRoomSession(BoardProtocol.GameXiangqi, "走错门的象棋玩家");
        var wrongNotices = new ConcurrentQueue<string>();
        wrongClient.NoticeReceived += (text, _) => wrongNotices.Enqueue(text);
        try
        {
            await host.StartHostAsync(0, () => new BoardWelcome("white",
                BoardProtocol.WrapSnapshot(1, "black", "white", GameJson(new GomokuGame())))).ConfigureAwait(false);
            var error = await wrongClient.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            check("[网络] 游戏类型对不上（象棋客户端进五子棋房间）→ 握手失败、不崩",
                error.Length > 0 && !wrongClient.IsOnline, error);
            check("[网络] 握手失败时能收到「游戏类型不一致」的提示（原因不会被吞掉）",
                wrongNotices.Any(t => t.Contains("游戏类型")), string.Join(" | ", wrongNotices));
            check("[网络] 走错门的连接不会占住房间（主机仍可正常被连）",
                !host.HasPeer && host.IsOnline, $"hasPeer={host.HasPeer}");
        }
        finally
        {
            await SafeStopAsync(wrongClient).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    // ---------- G. 房间 ViewModel 级：开房 / 加入 / 聊天 / 同机双人 / 断线自动重连 / 关房不重连 ----------

    private static async Task RunRoomViewModelAsync(Action<string, bool, string> check, List<string> report)
    {
        var hostVm = new SelfCheckBoardVm();
        var clientVm = new SelfCheckBoardVm();
        // 聊天列表由后台线程追加，测试线程直接枚举会偶发「Collection was modified」——先抄到线程安全队列
        var hostVmChats = new ConcurrentQueue<string>();
        var clientVmChats = new ConcurrentQueue<string>();
        TrackChats(hostVm, hostVmChats);
        TrackChats(clientVm, clientVmChats);
        try
        {
            check("[联机VM] 新开界面：Mode=Offline、未连接、徽标「○ 未连接」、入口可用",
                hostVm.Mode == BoardPlayMode.Offline && !hostVm.IsOnline && hostVm.CanJoin
                && hostVm.ModeBadgeText == "○ 未连接" && hostVm.ShowJoinControls, hostVm.ModeBadgeText);

            hostVm.PlayerName = "主机VM";
            hostVm.ListenPortText = "0";
            hostVm.StartHostCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsOnline, 10000).ConfigureAwait(false);
            check("[联机VM] 主机开房：Mode=Host、在线、不能重复开房、徽标「● 房间开着」",
                hostVm.IsOnline && hostVm.IsHost && hostVm.Mode == BoardPlayMode.Host
                && !hostVm.CanJoin && !hostVm.StartHostCommand.CanExecute(null)
                && hostVm.ModeBadgeText.Contains("房间开着"),
                $"{hostVm.NoticeText} / {hostVm.ModeBadgeText}");
            var portMatch = Regex.Match(hostVm.ConnectionSummary, @"真实端口 (\d+)");
            var port = portMatch.Success ? int.Parse(portMatch.Groups[1].Value) : 0;
            check("[联机VM] 连接摘要里能拿到真实端口（端口 0 = 系统分配）", port > 0, hostVm.ConnectionSummary);
            check("[联机VM] 开房后提示「等待对方加入」、不是暂停、可以离开房间",
                hostVm.ConnectionSummary.Contains("等待对方加入") && !hostVm.IsGamePaused
                && hostVm.CanLeaveRoom && hostVm.LeaveRoomText == "离开房间", hostVm.ConnectionSummary);

            clientVm.PlayerName = "客户端VM";
            clientVm.JoinAddress = $"127.0.0.1:{port}";
            clientVm.JoinRoomCommand.Execute(null);
            await WaitUntilAsync(() => clientVm.IsOnline && hostVm.HasPeer && clientVm.HasPeer, 12000)
                .ConfigureAwait(false);
            check("[联机VM] 客户端加入：Mode=Client、双方 HasPeer、重复加入按钮被禁用",
                clientVm.IsOnline && clientVm.IsClient && hostVm.HasPeer && clientVm.HasPeer
                && !clientVm.CanJoin && !clientVm.JoinRoomCommand.CanExecute(null), clientVm.NoticeText);
            check("[联机VM] 双方执子文案（主机黑 / 客户端白）",
                hostVm.LocalSideText.Contains("黑棋") && clientVm.LocalSideText.Contains("白棋"),
                $"{hostVm.LocalSideText} / {clientVm.LocalSideText}");
            check("[联机VM] 主机侧收到 PeerJoined 钩子、聊天里有「加入了房间」系统提示",
                hostVm.PeerJoinedCount >= 1
                && hostVmChats.Any(t => t.Contains("加入了房间")),
                string.Join(" | ", hostVmChats.Take(5)));
            check("[联机VM] 客户端侧看到我方执白、对方执黑",
                clientVm.LocalSideText.Contains("白棋") && clientVm.PeerSideText.Contains("黑棋"),
                $"{clientVm.LocalSideText} / {clientVm.PeerSideText}");
            check("[联机VM] 主机 CurrentSession 就是当前房间（自检可据此模拟断网）",
                hostVm.CurrentSession is { IsHost: true, IsOnline: true }, "");

            // 聊天双向
            hostVm.ChatText = "VM 聊天：你好";
            hostVm.SendChatCommand.Execute(null);
            await WaitUntilAsync(() => clientVmChats.Contains("VM 聊天：你好"), 8000)
                .ConfigureAwait(false);
            check("[联机VM] 聊天：主机的文字进了客户端聊天记录",
                clientVmChats.Contains("VM 聊天：你好"), "");
            check("[联机VM] 发完聊天输入框自动清空", hostVm.ChatText.Length == 0, hostVm.ChatText);
            clientVm.ChatText = "VM 聊天：收到";
            clientVm.SendChatCommand.Execute(null);
            await WaitUntilAsync(() => hostVmChats.Contains("VM 聊天：收到"), 8000)
                .ConfigureAwait(false);
            check("[联机VM] 聊天：客户端的文字进了主机聊天记录",
                hostVmChats.Contains("VM 聊天：收到"), "");

            // 主机走子 → 客户端快照
            hostVm.PlayBlack(0, 0);
            await WaitUntilAsync(() => clientVm.ReceivedSnapshotCount >= 1 && clientVm.Game.MoveCount == 1, 8000)
                .ConfigureAwait(false);
            check("[联机VM] 主机走子 → 快照广播 → 客户端 ApplyGameSnapshot 重建局面",
                clientVm.ReceivedSnapshotCount >= 1 && clientVm.Game.MoveCount == 1
                && clientVm.LastAppliedSide == "white", $"快照 {clientVm.ReceivedSnapshotCount} 次");
            // 客户端报动作 → 主机执行 → 广播回来
            clientVm.PlayWhite(7, 7);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 2, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => clientVm.Game.MoveCount == 2, 8000).ConfigureAwait(false);
            check("[联机VM] 客户端报动作 → 主机 OnHostAction 执行 → 广播 → 客户端收到（2 手）",
                hostVm.Game.MoveCount == 2 && clientVm.Game.MoveCount == 2,
                $"host={hostVm.Game.MoveCount} client={clientVm.Game.MoveCount}");
            var movesBeforeCut = hostVm.Game.MoveCount;

            // ===== 断线重连（用户点名的验收重点） =====
            clientVm.CurrentSession!.AbortConnection("模拟断网");
            await WaitUntilAsync(() => clientVm.IsReconnecting, 8000).ConfigureAwait(false);
            check("[联机VM] 网络断了：客户端进入自动重连（IsReconnecting、Mode 仍是 Client）",
                clientVm.IsReconnecting && clientVm.Mode == BoardPlayMode.Client && !clientVm.IsOnline,
                $"{clientVm.ModeBadgeText} / {clientVm.ReconnectStatusText}");
            check("[联机VM] 重连中：徽标「正在重连」、对局暂停、不能重复加入、按钮变「停止重连」",
                clientVm.ModeBadgeText.Contains("正在重连") && clientVm.IsGamePaused
                && clientVm.GamePauseStatusText.Contains("重连") && !clientVm.CanJoin
                && !clientVm.JoinRoomCommand.CanExecute(null) && !clientVm.ShowJoinControls
                && clientVm.CanLeaveRoom && clientVm.LeaveRoomText.Contains("停止重连")
                && clientVm.Game.MoveCount == movesBeforeCut,
                $"{clientVm.GamePauseStatusText} / {clientVm.ReconnectStatusText}");
            await WaitUntilAsync(() => hostVm.IsGamePaused, 8000).ConfigureAwait(false);
            check("[联机VM] 客户端断线后主机侧：对局暂停、等待对方重连、棋局不动",
                hostVm.IsGamePaused && hostVm.IsOnline && !hostVm.HasPeer
                && hostVm.GamePauseStatusText.Contains("等待对方重连")
                && hostVm.Game.MoveCount == movesBeforeCut,
                $"{hostVm.GamePauseStatusText} / {hostVm.NoticeText}");

            await WaitUntilAsync(() => clientVm.IsOnline && !clientVm.IsReconnecting, 20000).ConfigureAwait(false);
            check("[联机VM] 自动重连成功：回到在线、暂停解除、OnReconnected 被调、聊天提示已重连",
                clientVm.IsOnline && !clientVm.IsReconnecting && !clientVm.IsGamePaused
                && clientVm.HasPeer && clientVm.ReconnectedCount >= 1
                && clientVmChats.Any(t => t.Contains("已重新连接")),
                $"{clientVm.NoticeText} / {clientVm.ReconnectStatusText}");
            await WaitUntilAsync(() => hostVm.HasPeer && !hostVm.IsGamePaused, 10000).ConfigureAwait(false);
            check("[联机VM] 重连后主机侧：对方回来、暂停解除、棋局仍是断线前那一步",
                hostVm.HasPeer && !hostVm.IsGamePaused && hostVm.Game.MoveCount == movesBeforeCut
                && hostVm.ReconnectedCount >= 1, hostVm.NoticeText);

            // 重连后链路完好：主机走子 / 客户端动作 / 聊天
            hostVm.PlayBlack(1, 0);
            await WaitUntilAsync(() => clientVm.Game.MoveCount == movesBeforeCut + 1, 8000).ConfigureAwait(false);
            check("[联机VM] 重连后继续走：主机落子客户端收到",
                clientVm.Game.MoveCount == movesBeforeCut + 1, clientVm.Game.MoveCount.ToString());
            clientVm.PlayWhite(7, 8);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == movesBeforeCut + 2, 8000).ConfigureAwait(false);
            check("[联机VM] 重连后继续走：客户端动作主机执行",
                hostVm.Game.MoveCount == movesBeforeCut + 2, hostVm.Game.MoveCount.ToString());
            clientVm.ChatText = "重连后的聊天";
            clientVm.SendChatCommand.Execute(null);
            await WaitUntilAsync(() => hostVmChats.Contains("重连后的聊天"), 8000)
                .ConfigureAwait(false);
            check("[联机VM] 重连后聊天链路也恢复", hostVmChats.Contains("重连后的聊天"), "");

            // ===== 房主关房：客户端不自动重连 =====
            hostVm.LeaveRoomCommand.Execute(null);
            await WaitUntilAsync(() => !hostVm.IsOnline, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => clientVm.Mode == BoardPlayMode.Offline, 10000).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);         // 超过一次重连间隔：确认没有偷偷复活
            check("[联机VM] 房主关房：客户端收到「房主…」原因、回到离线、且不会自动重连",
                clientVm.Mode == BoardPlayMode.Offline && !clientVm.IsOnline && !clientVm.IsReconnecting
                && !clientVm.IsClient && clientVmChats.Any(t => t.Contains("房主")),
                $"{clientVm.Mode} / {clientVm.NoticeText}");
            check("[联机VM] 房主关房后：客户端界面恢复可操作（可重新加入）",
                clientVm.CanJoin && clientVm.ShowJoinControls, "");
            check("[联机VM] 房主关房后：主机自己也回到离线、不再是 Host",
                !hostVm.IsHost && hostVm.Mode == BoardPlayMode.Offline && !hostVm.IsOnline, "");

            // ===== 重新开房：主动离开不重连 / 重连中途停止 =====
            var stopClientVm = new SelfCheckBoardVm();
            try
            {
                hostVm.StartHostCommand.Execute(null);
                await WaitUntilAsync(() => hostVm.IsOnline, 10000).ConfigureAwait(false);
                var port2Match = Regex.Match(hostVm.ConnectionSummary, @"真实端口 (\d+)");
                var port2 = port2Match.Success ? int.Parse(port2Match.Groups[1].Value) : 0;
                stopClientVm.JoinAddress = $"127.0.0.1:{port2}";
                stopClientVm.JoinRoomCommand.Execute(null);
                await WaitUntilAsync(() => stopClientVm.IsOnline && hostVm.HasPeer, 12000).ConfigureAwait(false);
                check("[联机VM] 退出房间后可以重新开房 + 再次加入（房间可重复用）",
                    hostVm.IsOnline && stopClientVm.IsOnline && hostVm.HasPeer, stopClientVm.NoticeText);

                // 重连中途停止：先掐断，等进入重连，再点「停止重连」
                stopClientVm.CurrentSession!.AbortConnection("模拟断网");
                await WaitUntilAsync(() => stopClientVm.IsReconnecting, 8000).ConfigureAwait(false);
                stopClientVm.LeaveRoomCommand.Execute(null);
                await WaitUntilAsync(() => !stopClientVm.IsReconnecting, 8000).ConfigureAwait(false);
                await Task.Delay(1000).ConfigureAwait(false);
                check("[联机VM] 重连中途点「停止重连」：回到离线且不会偷偷复活",
                    !stopClientVm.IsReconnecting && !stopClientVm.IsClient && !stopClientVm.IsOnline
                    && stopClientVm.Mode == BoardPlayMode.Offline, stopClientVm.NoticeText);

                // 用户主动离开不触发重连
                stopClientVm.JoinRoomCommand.Execute(null);
                await WaitUntilAsync(() => stopClientVm.IsOnline, 12000).ConfigureAwait(false);
                check("[联机VM] 停止重连后还能手动重新加入", stopClientVm.IsOnline, stopClientVm.NoticeText);
                stopClientVm.LeaveRoomCommand.Execute(null);
                await WaitUntilAsync(() => !stopClientVm.IsOnline, 8000).ConfigureAwait(false);
                await Task.Delay(1000).ConfigureAwait(false);
                check("[联机VM] 用户主动离开房间：不触发自动重连（等 1 秒仍未复活）",
                    !stopClientVm.IsOnline && !stopClientVm.IsReconnecting
                    && stopClientVm.Mode == BoardPlayMode.Offline, stopClientVm.NoticeText);
            }
            finally
            {
                stopClientVm.NotifyClosed();
            }
        }
        finally
        {
            try { clientVm.NotifyClosed(); } catch { }
            try { hostVm.NotifyClosed(); } catch { }
        }

        // ===== 同机双人（不需要网络） =====
        var localVm = new SelfCheckBoardVm();
        try
        {
            localVm.StartLocalCommand.Execute(null);
            check("[联机VM] 同机双人：Mode=Local、徽标「● 同机双人」、不需要网络",
                localVm.Mode == BoardPlayMode.Local && localVm.IsLocalMode
                && localVm.ModeBadgeText.Contains("同机双人") && !localVm.IsOnline
                && !localVm.IsGamePaused && localVm.CanJoin, localVm.ModeBadgeText);
            check("[联机VM] 同机双人：进本地模式时钩子被调一次", localVm.LocalModeStartedCount >= 1,
                localVm.LocalModeStartedCount.ToString());
        }
        finally
        {
            localVm.NotifyClosed();
        }
    }

    // ---------- 回环会话用的小工具 ----------

    private static JsonElement GameJson(GomokuGame game)
        => JsonSerializer.SerializeToElement(game.ToSnapshot(), RoomProtocol.JsonOptions);

    private static JsonElement GameJson(XiangqiGame game)
        => JsonSerializer.SerializeToElement(game.ToSnapshot(), RoomProtocol.JsonOptions);

    private static async Task SafeStopAsync(BoardRoomSession session)
    {
        try { await session.StopAsync("自检结束").ConfigureAwait(false); } catch { /* 已经断了就算了 */ }
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* 同上 */ }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(40).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>
    /// 把 VM 的聊天列表变化抄一份到线程安全队列。
    /// <para>聊天列表是 <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>，
    /// 后台线程会往里追加（自检进程里没有 Application，<c>Ui()</c> 是同步执行的），
    /// 测试线程直接枚举会偶发「Collection was modified」——所以断言只读这份快照。</para>
    /// </summary>
    private static void TrackChats(BoardGameRoomViewModelBase vm, ConcurrentQueue<string> sink)
    {
        vm.ChatItems.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is null) return;
            foreach (var item in e.NewItems)
            {
                if (item is BoardChatItem chat) sink.Enqueue(chat.Text);
            }
        };
    }

    private static string? SnapshotGomokuResult(JsonElement envelope)
    {
        if (BoardProtocol.ReadSnapshot(envelope) is not { Game: { } game }) return null;
        return game.TryGetProperty("result", out var result) ? result.GetString() : null;
    }

    private static int? SnapshotMoveCount(JsonElement envelope)
    {
        if (BoardProtocol.ReadSnapshot(envelope) is not { Game: { } game }) return null;
        if (!game.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.Array) return null;
        return moves.GetArrayLength();
    }

    private static int SnapshotXiangqiHistory(JsonElement envelope)
    {
        if (BoardProtocol.ReadSnapshot(envelope) is not { Game: { } game }) return -1;
        if (!game.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array) return -1;
        return history.GetArrayLength();
    }

    private static GomokuGame? RestoreGomoku(JsonElement envelope)
    {
        if (BoardProtocol.ReadSnapshot(envelope) is not { Game: { } game }) return null;
        var snapshot = JsonSerializer.Deserialize<GomokuSnapshot>(game.GetRawText(), RoomProtocol.JsonOptions);
        return snapshot is null ? null : GomokuGame.FromSnapshot(snapshot).Game;
    }

    private static XiangqiGame? RestoreXiangqi(JsonElement envelope)
    {
        if (BoardProtocol.ReadSnapshot(envelope) is not { Game: { } game }) return null;
        var snapshot = JsonSerializer.Deserialize<XiangqiSnapshot>(game.GetRawText(), RoomProtocol.JsonOptions);
        return snapshot is null ? null : XiangqiGame.FromSnapshot(snapshot).Game;
    }

    /// <summary>
    /// 自检用的最小棋牌 VM：只实现基类的 abstract 钩子（五子棋规则本身不是被测对象），
    /// 用来无头验证房间生命周期、聊天、快照与<b>断线自动重连</b>这些基类行为。
    /// </summary>
    private sealed class SelfCheckBoardVm : BoardGameRoomViewModelBase
    {
        private GomokuGame _game = new();

        public SelfCheckBoardVm() : base("gomoku", "五子棋")
        {
            Cfg = LoadConfig<GomokuConfig>();
            AttachRoomConfig(Cfg);
        }

        public GomokuConfig Cfg { get; }
        public GomokuGame Game => _game;
        public int ReceivedSnapshotCount { get; private set; }
        public string LastAppliedSide { get; private set; } = "";
        public int PeerJoinedCount { get; private set; }
        public int PeerLeftCount { get; private set; }
        public string LastPeerLeftReason { get; private set; } = "";
        public int ReconnectedCount { get; private set; }
        public int LocalModeStartedCount { get; private set; }
        public int UndoAppliedCount { get; private set; }
        public int ResignAppliedCount { get; private set; }

        /// <summary>主机：走一手黑棋并广播（模拟游戏 VM 的落子）。</summary>
        public void PlayBlack(int x, int y)
        {
            _game.Play(GomokuCell.Black, x, y);
            BroadcastSnapshot();
        }

        /// <summary>客户端：把一手白棋作为动作报给主机。</summary>
        public void PlayWhite(int x, int y) => SendAction("move", new { x, y });

        protected override string HostSide => "black";

        protected override string OppositeSide(string side) => side == "white" ? "black" : "white";

        protected override string SideDisplayName(string side) => side == "black" ? "黑棋" : "白棋";

        protected override JsonElement BuildGameSnapshot() => GameJson(_game);

        protected override void ApplyGameSnapshot(JsonElement game, string mySide)
        {
            var snapshot = JsonSerializer.Deserialize<GomokuSnapshot>(game.GetRawText(), RoomProtocol.JsonOptions);
            if (snapshot is null) return;
            var (restored, error) = GomokuGame.FromSnapshot(snapshot);
            if (restored is null)
            {
                SetNotice($"自检 VM 重建局面失败：{error}", "DangerBrush");
                return;
            }

            _game = restored;
            ReceivedSnapshotCount++;
            LastAppliedSide = mySide;
        }

        protected override void OnHostAction(string type, JsonElement? payload)
        {
            if (type != "move" || payload is not { } move) return;
            _game.Play(GomokuCell.White,
                move.GetProperty("x").GetInt32(), move.GetProperty("y").GetInt32());
            BroadcastSnapshot();
        }

        protected override bool ApplyUndoFor(string requesterSide)
        {
            UndoAppliedCount++;
            return _game.UndoToTurn(GomokuText.ParseSide(requesterSide));
        }

        protected override void ApplyDraw() => _game.MarkDraw();

        protected override void ApplyResign(string side)
        {
            ResignAppliedCount++;
            _game.MarkResign(GomokuText.ParseSide(side));
        }

        protected override void BeginNewGame(bool swapSides) => _game.Reset(_game.Size, _game.ForbiddenEnabled);

        protected override void OnLocalModeStarted()
        {
            LocalModeStartedCount++;
            _game.Reset(GomokuBoard.DefaultSize, false);
        }

        protected override void OnPeerJoined() => PeerJoinedCount++;

        protected override void OnPeerLeft(string? reason)
        {
            PeerLeftCount++;
            LastPeerLeftReason = reason ?? "";
        }

        protected override void OnReconnected() => ReconnectedCount++;
    }
}


