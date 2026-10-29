using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
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
using YuLiangToolkit.Tools.BoardGames.Xiangqi.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「棋牌」类别的自检（联机五子棋 + 联机象棋 + 多人观战房间 + 自动轮换）。
///
/// <para>分区：注册（两个 ITool 与「棋牌」类别）· 五子棋（棋盘 / 禁手 / 悔棋 / 快照）·
/// 象棋（走法 / 将军 / 摆棋 / 让子 / 让先 / 记谱 / 快照）· 自动轮换（BoardRotationPlanner 纯逻辑）·
/// 配置（自动落盘 + IBoardGameRoomConfig 五字段）· 网络 v2（帧格式 / 端点 / 多客户端回环 / 断线重连 / 容量）·
/// 房间 ViewModel（席位安排 / 观战只读 / 自动轮换 / 暂停重连）。</para>
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
        CheckRotationPlanner(check, plain);
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

    // ---------------------------------------------------------------- 自动轮换（BoardRotationPlanner 纯逻辑：席位 / 补位 / 回退 / 文案）

    private static void CheckRotationPlanner(Action<string, bool, string> checkWithDetail, Action<string, bool> plain)
    {
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);

        static BoardRotationCandidate Cand(string id, string name, int games = 0, int join = 0, bool online = true)
            => new(id, name, online, games, join);

        static BoardRotationResult Plan(
            BoardAutoRotateMode mode, BoardAutoPickMode pick,
            string seatOne, string seatTwo, string? winner, string? loser,
            params BoardRotationCandidate[] members)
            => BoardRotationPlanner.Plan(mode, pick, seatOne, seatTwo, winner, loser, members);

        // ===== 枚举 / 存储键 / 中文文案 =====
        check("[轮换] 枚举序号稳定（配置里存的是字符串，但序号也别乱动）",
            (int)BoardAutoRotateMode.Manual == 0 && (int)BoardAutoRotateMode.LoserOut == 1
            && (int)BoardAutoRotateMode.BothOut == 2
            && (int)BoardAutoPickMode.Rotation == 0 && (int)BoardAutoPickMode.Random == 1, "");
        check("[轮换] 上场方式存储键往返：manual / loserOut / bothOut（大小写不敏感）",
            BoardAutoModes.ToKey(BoardAutoRotateMode.Manual) == "manual"
            && BoardAutoModes.ToKey(BoardAutoRotateMode.LoserOut) == "loserOut"
            && BoardAutoModes.ToKey(BoardAutoRotateMode.BothOut) == "bothOut"
            && BoardAutoModes.ParseRotate("loserOut") == BoardAutoRotateMode.LoserOut
            && BoardAutoModes.ParseRotate("BOTHOUT") == BoardAutoRotateMode.BothOut
            && BoardAutoModes.ParseRotate(" manual ") == BoardAutoRotateMode.Manual
            && BoardAutoModes.ParseRotate("") == BoardAutoRotateMode.Manual
            && BoardAutoModes.ParseRotate(null) == BoardAutoRotateMode.Manual
            && BoardAutoModes.ParseRotate("乱写") == BoardAutoRotateMode.Manual, "");
        check("[轮换] 补位方式存储键往返：rotation / random（认不出按轮流）",
            BoardAutoModes.ToKey(BoardAutoPickMode.Rotation) == "rotation"
            && BoardAutoModes.ToKey(BoardAutoPickMode.Random) == "random"
            && BoardAutoModes.ParsePick("random") == BoardAutoPickMode.Random
            && BoardAutoModes.ParsePick("RANDOM") == BoardAutoPickMode.Random
            && BoardAutoModes.ParsePick("") == BoardAutoPickMode.Rotation
            && BoardAutoModes.ParsePick(null) == BoardAutoPickMode.Rotation
            && BoardAutoModes.ParsePick("乱写") == BoardAutoPickMode.Rotation, "");
        check("[轮换] 中文文案：三种上场方式 / 两种补位方式",
            BoardAutoModes.Describe(BoardAutoRotateMode.Manual) == "手动安排"
            && BoardAutoModes.Describe(BoardAutoRotateMode.LoserOut) == "输的下场"
            && BoardAutoModes.Describe(BoardAutoRotateMode.BothOut) == "双方都下场"
            && BoardAutoModes.Describe(BoardAutoPickMode.Rotation) == "轮流上场"
            && BoardAutoModes.Describe(BoardAutoPickMode.Random) == "随机上场", "");
        check("[轮换] 固定文案常量与契约一致（聊天里会原样出现）",
            BoardRotationPlanner.ManualReason == "手动安排"
            && BoardRotationPlanner.NoReplacementReason == "没有可替换的选手，维持原上场", "");

        // ===== 手动安排：原样返回 =====
        var manual = Plan(BoardAutoRotateMode.Manual, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2), Cand("m3", "丙", join: 3));
        check("[轮换] 手动安排：席位原样不动、Changed=false、Reason=「手动安排」",
            manual.SeatOneMemberId == "m1" && manual.SeatTwoMemberId == "m2"
            && !manual.Changed && manual.Reason == BoardRotationPlanner.ManualReason, manual.Reason);
        var manualEmpty = Plan(BoardAutoRotateMode.Manual, BoardAutoPickMode.Random, "", "", null, null);
        check("[轮换] 手动安排：空席位也原样返回（不换人、不抛）",
            manualEmpty.SeatOneMemberId == "" && manualEmpty.SeatTwoMemberId == ""
            && !manualEmpty.Changed, "");
        var manualTrim = Plan(BoardAutoRotateMode.Manual, BoardAutoPickMode.Rotation, " m1 ", "  ", null, null);
        check("[轮换] 席位 id 会 trim：空格串视为空位",
            manualTrim.SeatOneMemberId == "m1" && manualTrim.SeatTwoMemberId == "",
            $"'{manualTrim.SeatOneMemberId}' / '{manualTrim.SeatTwoMemberId}'");

        // ===== 输的下场 × 轮流 =====
        var loserOut = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "小明", join: 1), Cand("m2", "小红", join: 2),
            Cand("m3", "小刚", join: 3), Cand("m4", "小美", join: 4));
        check("[轮换] 输的下场：赢家留原席位、输家位置换成替补（先手 m1 / 后手 m3）",
            loserOut.SeatOneMemberId == "m1" && loserOut.SeatTwoMemberId == "m3" && loserOut.Changed,
            $"{loserOut.SeatOneMemberId}/{loserOut.SeatTwoMemberId}");
        check("[轮换] 输的下场：Reason 说明谁替换了谁（「输的下场：小刚替换小红」）",
            loserOut.Reason == "输的下场：小刚替换小红", loserOut.Reason);

        var loserOutSwap = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m2", "m1",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2), Cand("m3", "丙", join: 3));
        check("[轮换] 输的下场：赢家在后手位时也留原席位（先手位换新人，不换边）",
            loserOutSwap.SeatOneMemberId == "m3" && loserOutSwap.SeatTwoMemberId == "m2"
            && loserOutSwap.Changed, $"{loserOutSwap.SeatOneMemberId}/{loserOutSwap.SeatTwoMemberId}");

        var loserOutDraw = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", null, null,
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2), Cand("m3", "丙", join: 3));
        check("[轮换] 输的下场：平局（没有输家）→ 维持原上场、Reason 含「平局」",
            !loserOutDraw.Changed && loserOutDraw.SeatOneMemberId == "m1"
            && loserOutDraw.SeatTwoMemberId == "m2"
            && loserOutDraw.Reason == "平局：不轮换，维持原上场", loserOutDraw.Reason);

        var loserOutMismatch = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m9", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2), Cand("m3", "丙", join: 3));
        check("[轮换] 输的下场：赢家不在席位上（数据对不上）→ 不瞎换、Reason 可读",
            !loserOutMismatch.Changed && loserOutMismatch.SeatOneMemberId == "m1"
            && loserOutMismatch.Reason == "本局结果对不上场上选手，维持原上场", loserOutMismatch.Reason);

        var loserOutSame = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m1",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2), Cand("m3", "丙", join: 3));
        check("[轮换] 输的下场：赢家 = 输家（数据异常）→ 维持原上场",
            !loserOutSame.Changed && loserOutSame.Reason.Contains("对不上"), loserOutSame.Reason);

        var loserOutEmptyWinner = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2), Cand("m3", "丙", join: 3));
        check("[轮换] 输的下场：赢家 id 为空 → 按平局处理（维持原上场）",
            !loserOutEmptyWinner.Changed && loserOutEmptyWinner.Reason.Contains("平局"),
            loserOutEmptyWinner.Reason);

        var loserOutNoPool = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2));
        check("[轮换] 输的下场：没有替补（池为空）→ 维持原上场、Reason=固定文案",
            !loserOutNoPool.Changed && loserOutNoPool.Reason == BoardRotationPlanner.NoReplacementReason,
            loserOutNoPool.Reason);

        var loserOutOffline = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2),
            Cand("m3", "离线的人", join: 3, online: false), Cand("m4", "在线的人", join: 4));
        check("[轮换] 输的下场：离线成员不进候选池（跳过 m3，选 m4）",
            loserOutOffline.SeatTwoMemberId == "m4" && loserOutOffline.Changed,
            loserOutOffline.SeatTwoMemberId);

        var loserOutLowestGames = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2),
            Cand("m3", "老手", games: 5, join: 3), Cand("m4", "新人", games: 0, join: 9));
        check("[轮换] 输的下场：上场次数少的优先（新人 0 局赢过老手 5 局）",
            loserOutLowestGames.SeatTwoMemberId == "m4", loserOutLowestGames.SeatTwoMemberId);

        var loserOutJoinOrder = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2),
            Cand("m3", "晚进", games: 1, join: 5), Cand("m4", "早进", games: 1, join: 2));
        check("[轮换] 输的下场：同场次按进房顺序（早进的 m4 优先）",
            loserOutJoinOrder.SeatTwoMemberId == "m4", loserOutJoinOrder.SeatTwoMemberId);

        var loserOutIdTie = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "s1", "s2", "s1", "s2",
            Cand("s1", "甲", join: 1), Cand("s2", "乙", join: 2),
            Cand("c", "同序 B", games: 1, join: 3), Cand("d", "同序 A", games: 1, join: 3));
        check("[轮换] 输的下场：场次 / 进房顺序都一样时按 MemberId 稳定排序（选 c）",
            loserOutIdTie.SeatTwoMemberId == "c", loserOutIdTie.SeatTwoMemberId);

        var loserOutOnSeatNotPool = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "输家甲", games: 0, join: 1), Cand("m2", "输家乙", games: 0, join: 2),
            Cand("m3", "替补", games: 9, join: 3));
        check("[轮换] 输的下场：场上两人不参与补位（哪怕场次更少也不会自己顶回来）",
            loserOutOnSeatNotPool.SeatOneMemberId == "m1" && loserOutOnSeatNotPool.SeatTwoMemberId == "m3",
            $"{loserOutOnSeatNotPool.SeatOneMemberId}/{loserOutOnSeatNotPool.SeatTwoMemberId}");

        var loserOutFallbackName = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", join: 1), Cand("m3", "", join: 3));
        check("[轮换] 输的下场：输家名字找不到时 Reason 用「原选手」兜底；替补空名用成员 id",
            loserOutFallbackName.SeatTwoMemberId == "m3"
            && loserOutFallbackName.Reason.Contains("原选手")
            && loserOutFallbackName.Reason.Contains("m3"), loserOutFallbackName.Reason);

        // 固定种子可复现：同一输入 + 同一个种子 → 两次结果完全一致
        var randomMembers = new[]
        {
            Cand("m1", "甲", games: 1, join: 1), Cand("m2", "乙", games: 1, join: 2),
            Cand("m3", "丙", join: 3), Cand("m4", "丁", join: 4), Cand("m5", "戊", join: 5),
        };
        var randomA = BoardRotationPlanner.Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Random,
            "m1", "m2", "m1", "m2", randomMembers, new Random(42));
        var randomB = BoardRotationPlanner.Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Random,
            "m1", "m2", "m1", "m2", randomMembers, new Random(42));
        check("[轮换] 随机补位：固定种子可复现（两次 new Random(42) 结果一致）",
            randomA.SeatOneMemberId == randomB.SeatOneMemberId
            && randomA.SeatTwoMemberId == randomB.SeatTwoMemberId
            && randomA.Reason == randomB.Reason,
            $"{randomA.SeatOneMemberId}/{randomA.SeatTwoMemberId} vs {randomB.SeatOneMemberId}/{randomB.SeatTwoMemberId}");
        check("[轮换] 随机补位：仍然「没上过场优先」（只会从 0 局的 m3/m4/m5 里挑）",
            randomA.SeatTwoMemberId is "m3" or "m4" or "m5", randomA.SeatTwoMemberId);
        var randomSingle = BoardRotationPlanner.Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Random,
            "m1", "m2", "m1", "m2",
            new[] { Cand("m1", "甲"), Cand("m2", "乙"), Cand("m3", "唯一替补") }, new Random(7));
        check("[轮换] 随机补位：只有 1 个候选时结果确定（m3）",
            randomSingle.SeatTwoMemberId == "m3", randomSingle.SeatTwoMemberId);

        // ===== 双方都下场 × 轮流 =====
        var bothOut = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", games: 1, join: 1), Cand("m2", "乙", games: 1, join: 2),
            Cand("m3", "小刚", join: 3), Cand("m4", "小美", join: 4));
        check("[轮换] 双方都下场：两席都换人（先手 m3 / 后手 m4）、Changed=true",
            bothOut.SeatOneMemberId == "m3" && bothOut.SeatTwoMemberId == "m4" && bothOut.Changed,
            $"{bothOut.SeatOneMemberId}/{bothOut.SeatTwoMemberId}");
        check("[轮换] 双方都下场：Reason 格式「双方下场：A、B上场」",
            bothOut.Reason == "双方下场：小刚、小美上场", bothOut.Reason);

        var bothOutDraw = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", null, null,
            Cand("m1", "甲", games: 1, join: 1), Cand("m2", "乙", games: 1, join: 2),
            Cand("m3", "丙", join: 3), Cand("m4", "丁", join: 4));
        check("[轮换] 双方都下场：平局也换人（不看 winner/loser）",
            bothOutDraw.SeatOneMemberId == "m3" && bothOutDraw.SeatTwoMemberId == "m4"
            && bothOutDraw.Changed, bothOutDraw.Reason);

        var bothOutOne = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲"), Cand("m2", "乙"), Cand("m3", "只有一个替补", join: 3));
        check("[轮换] 双方都下场：替补只有 1 人 → 维持原上场、Reason=固定文案",
            !bothOutOne.Changed && bothOutOne.SeatOneMemberId == "m1"
            && bothOutOne.Reason == BoardRotationPlanner.NoReplacementReason, bothOutOne.Reason);
        var bothOutZero = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲"), Cand("m2", "乙"));
        check("[轮换] 双方都下场：没有替补 → 维持原上场", !bothOutZero.Changed && bothOutZero.SeatOneMemberId == "m1");
        var bothOutNullMembers = BoardRotationPlanner.Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation,
            "m1", "m2", "m1", "m2", null!);
        check("[轮换] 双方都下场：成员列表为 null 时不崩（维持原上场）",
            !bothOutNullMembers.Changed && bothOutNullMembers.Reason == BoardRotationPlanner.NoReplacementReason, "");

        var bothOutSorted = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲", games: 1), Cand("m2", "乙", games: 1),
            Cand("m5", "老手", games: 2, join: 1), Cand("m3", "新人乙", join: 9), Cand("m4", "新人甲", join: 2));
        check("[轮换] 双方都下场：先按场次再按进房顺序（m4、m3）",
            bothOutSorted.SeatOneMemberId == "m4" && bothOutSorted.SeatTwoMemberId == "m3",
            $"{bothOutSorted.SeatOneMemberId}/{bothOutSorted.SeatTwoMemberId}");

        var bothOutOffline = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲"), Cand("m2", "乙"),
            Cand("m3", "离线甲", join: 3, online: false), Cand("m4", "在线甲", join: 4),
            Cand("m5", "在线乙", join: 5));
        check("[轮换] 双方都下场：离线成员被跳过（m4、m5 上场，m3 不上）",
            bothOutOffline.SeatOneMemberId == "m4" && bothOutOffline.SeatTwoMemberId == "m5",
            $"{bothOutOffline.SeatOneMemberId}/{bothOutOffline.SeatTwoMemberId}");

        var bothOutDuplicate = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2",
            Cand("m1", "甲"), Cand("m2", "乙"), Cand("m3", "重复", join: 3), Cand("m3", "重复", join: 3));
        check("[轮换] 成员 id 重复只算一次（有效替补 1 人 → 双方都下场回退）",
            !bothOutDuplicate.Changed && bothOutDuplicate.Reason == BoardRotationPlanner.NoReplacementReason, "");

        var bothOutRandomA = BoardRotationPlanner.Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Random,
            "m1", "m2", "m1", "m2", randomMembers, new Random(123));
        var bothOutRandomB = BoardRotationPlanner.Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Random,
            "m1", "m2", "m1", "m2", randomMembers, new Random(123));
        check("[轮换] 双方都下场 + 随机：固定种子可复现",
            bothOutRandomA.SeatOneMemberId == bothOutRandomB.SeatOneMemberId
            && bothOutRandomA.SeatTwoMemberId == bothOutRandomB.SeatTwoMemberId,
            $"{bothOutRandomA.SeatOneMemberId}/{bothOutRandomA.SeatTwoMemberId}");
        check("[轮换] 双方都下场 + 随机：仍是「没上过场优先」（两席都从 0 局的人里挑）",
            new[] { bothOutRandomA.SeatOneMemberId, bothOutRandomA.SeatTwoMemberId }
                .All(id => id is "m3" or "m4" or "m5"),
            $"{bothOutRandomA.SeatOneMemberId}/{bothOutRandomA.SeatTwoMemberId}");

        // ===== 更多边界 / 存储键 / 文案 =====
        check("[轮换] 手动安排：即使传了随机源 + 随机补位，也绝不换人",
            BoardRotationPlanner.Plan(BoardAutoRotateMode.Manual, BoardAutoPickMode.Random,
                "m1", "m2", "m1", "m2",
                new[] { Cand("m1", "甲"), Cand("m2", "乙"), Cand("m3", "丙") },
                new Random(7)) is { SeatOneMemberId: "m1", SeatTwoMemberId: "m2", Changed: false }, "");
        var emptySeatsLoser = Plan(BoardAutoRotateMode.LoserOut, BoardAutoPickMode.Rotation, "", "", "m1", "m2",
            Cand("m1", "甲"), Cand("m2", "乙"));
        check("[轮换] 输的下场：原席位为空 + 胜负对不上 → 维持空位、原因可读",
            !emptySeatsLoser.Changed && emptySeatsLoser.SeatOneMemberId == ""
            && emptySeatsLoser.Reason.Contains("对不上"), emptySeatsLoser.Reason);
        var bothOutGarbage = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "m1", "m2", "乱写", "乱写",
            Cand("m1", "甲"), Cand("m2", "乙"), Cand("m3", "丙", join: 3), Cand("m4", "丁", join: 4));
        check("[轮换] 双方都下场：不看 winner/loser（传乱写也正常换人）",
            bothOutGarbage.SeatOneMemberId == "m3" && bothOutGarbage.SeatTwoMemberId == "m4"
            && bothOutGarbage.Changed, $"{bothOutGarbage.SeatOneMemberId}/{bothOutGarbage.SeatTwoMemberId}");
        var bothOutEmptySeats = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "", "", null, null,
            Cand("m1", "甲", join: 1), Cand("m2", "乙", join: 2));
        check("[轮换] 双方都下场：原席位为空也能排出两个人",
            bothOutEmptySeats.SeatOneMemberId == "m1" && bothOutEmptySeats.SeatTwoMemberId == "m2"
            && bothOutEmptySeats.Changed
            && bothOutEmptySeats.Reason.Contains("双方下场"), bothOutEmptySeats.Reason);
        var loserOutNullMembers = BoardRotationPlanner.Plan(BoardAutoRotateMode.LoserOut,
            BoardAutoPickMode.Rotation, "m1", "m2", "m1", "m2", null!);
        check("[轮换] 输的下场：成员列表为 null 时按「没有替补」回退",
            !loserOutNullMembers.Changed
            && loserOutNullMembers.Reason == BoardRotationPlanner.NoReplacementReason, loserOutNullMembers.Reason);
        check("[轮换] 存储键解析容忍大小写 / 前后空格（' LoserOut ' / ' Random '）",
            BoardAutoModes.ParseRotate(" LoserOut ") == BoardAutoRotateMode.LoserOut
            && BoardAutoModes.ParsePick(" Random ") == BoardAutoPickMode.Random, "");
        check("[轮换] 补位方式认不出时按轮流（未知字符串不会变成随机）",
            BoardAutoModes.ParsePick("abc") == BoardAutoPickMode.Rotation, "");
        var bothOutIdTie = Plan(BoardAutoRotateMode.BothOut, BoardAutoPickMode.Rotation, "a", "b", "a", "b",
            Cand("a", "甲", join: 1), Cand("b", "乙", join: 2),
            Cand("x", "同序甲", join: 5), Cand("y", "同序乙", join: 5));
        check("[轮换] 双方都下场：场次 / 进房顺序都一样时按 MemberId 稳定排序（x、y）",
            bothOutIdTie.SeatOneMemberId == "x" && bothOutIdTie.SeatTwoMemberId == "y",
            $"{bothOutIdTie.SeatOneMemberId}/{bothOutIdTie.SeatTwoMemberId}");
        check("[轮换] 结果记录是 record：同样输入算出来相等（便于测试 / 日志比较）",
            Plan(BoardAutoRotateMode.Manual, BoardAutoPickMode.Rotation, "m1", "m2", null, null,
                Cand("m1", "甲"), Cand("m2", "乙"))
            == Plan(BoardAutoRotateMode.Manual, BoardAutoPickMode.Rotation, "m1", "m2", null, null,
                Cand("m1", "甲"), Cand("m2", "乙")));
        check("[轮换] 候选记录字段原样保存（id / 名字 / 在线 / 场次 / 进房顺序）",
            Cand("m7", "小七", games: 4, join: 9, online: false) is
            { MemberId: "m7", Name: "小七", GamesPlayed: 4, JoinOrder: 9, IsOnline: false }, "");

        // ===== 成员列表行模型（BoardMemberItem / BoardSeatOption） =====
        var member = new BoardMemberItem { MemberId = "m1" };
        var memberNotifications = new List<string>();
        member.PropertyChanged += (_, e) => memberNotifications.Add(e.PropertyName ?? "");
        member.Apply("小明", isHost: false, isSelf: true, isOnline: true, gamesPlayed: 2, joinOrder: 3, seat: "first");
        check("[成员] 行模型：名字 / 本端 / 在线 / 上场次数一次性刷新",
            member.NameText == "小明（你）" && member.IsSelf && member.IsOnline
            && member.GamesPlayedText == "已打 2 局" && member.JoinOrder == 3, member.ToString());
        check("[成员] 行模型：先手位 / 后手位 位置文案",
            member.SeatText == "先手位" && member.IsSeated, member.SeatText);
        member.Apply("乙", false, false, true, 0, 1, "second");
        check("[成员] 行模型：换成后手位后仍是「已坐下」", member.SeatText == "后手位" && member.IsSeated,
            member.SeatText);
        member.Apply("乙", false, false, true, 0, 1, "");
        check("[成员] 行模型：清空席位后显示「观战」", member.SeatText == "观战" && !member.IsSeated,
            member.SeatText);
        member.Apply("房东", isHost: true, isSelf: false, isOnline: true, gamesPlayed: 5, joinOrder: 1, seat: "second");
        check("[成员] 行模型：房主徽标 / 身份文案",
            member.HostBadgeText == "房主" && member.RoleText == "房主" && member.NameText == "房东",
            member.ToString());
        var playerRow = new BoardMemberItem { MemberId = "m2" };
        playerRow.Apply("玩家乙", false, false, true, 0, 2, "");
        check("[成员] 行模型：普通玩家没有房主徽标",
            playerRow.HostBadgeText == "" && playerRow.RoleText == "玩家", playerRow.ToString());
        member.Apply("房东", true, false, isOnline: false, 5, 1, "");
        var offlineText = member.OnlineText;
        member.Apply("房东", true, false, isOnline: true, 5, 1, "");
        check("[成员] 行模型：离线 / 在线状态文案",
            offlineText == "离线" && member.OnlineText == "在线", $"{offlineText} / {member.OnlineText}");
        var namelessRow = new BoardMemberItem { MemberId = "m9" };
        namelessRow.Apply("", false, false, true, 0, 1, "");
        check("[成员] 行模型：空名字显示「玩家」", namelessRow.NameText == "玩家", namelessRow.NameText);
        check("[成员] 行模型：Apply 会发属性通知（界面能刷新）",
            memberNotifications.Count > 0
            && memberNotifications.Contains(nameof(BoardMemberItem.NameText))
            && memberNotifications.Contains(nameof(BoardMemberItem.SeatText))
            && memberNotifications.Contains(nameof(BoardMemberItem.GamesPlayedText)), "");
        var notificationsBefore = memberNotifications.Count;
        member.Apply("房东", isHost: true, isSelf: false, isOnline: true, gamesPlayed: 5, joinOrder: 1, seat: "");
        check("[成员] 行模型：Apply 同样的值不会重复发通知（避免界面白刷新）",
            memberNotifications.Count == notificationsBefore, $"{notificationsBefore} → {memberNotifications.Count}");

        var emptyOption = BoardSeatOption.Empty();
        check("[成员] 席位下拉：空位选项（MemberId 为空、IsEmpty=true、显示「空位」）",
            emptyOption.IsEmpty && emptyOption.MemberId == "" && emptyOption.DisplayName == "空位"
            && emptyOption.ToString() == "空位", "");
        var memberOption = new BoardSeatOption(" m1 ", " 小明 ");
        check("[成员] 席位下拉：成员选项（id / 名字会 trim、IsEmpty=false）",
            !memberOption.IsEmpty && memberOption.MemberId == "m1"
            && memberOption.DisplayName == "小明" && memberOption.ToString() == "小明", "");
        check("[成员] 席位下拉：空字符串 id 也视为空位",
            new BoardSeatOption("", "空位").IsEmpty && !new BoardSeatOption("m2", "小红").IsEmpty, "");
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
        check("[配置] 五子棋默认值：自动轮换=手动安排、补位=轮流上场",
            gomokuDefaults.AutoRotateMode == "manual" && gomokuDefaults.AutoPickMode == "rotation",
            $"{gomokuDefaults.AutoRotateMode}/{gomokuDefaults.AutoPickMode}");
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
        gomokuRoom.AutoRotateMode = "loserOut";
        gomokuRoom.AutoPickMode = "random";
        check("[配置] 五子棋配置实现 IBoardGameRoomConfig，五个字段可直接读写",
            gomokuDefaults.PlayerName == "接口棋手"
            && gomokuDefaults.LastJoinAddress == "192.168.1.9:5310"
            && gomokuDefaults.ListenPort == 0
            && gomokuDefaults.AutoRotateMode == "loserOut"
            && gomokuDefaults.AutoPickMode == "random", "");

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
        gomokuCfg.AutoRotateMode = "bothOut";
        gomokuCfg.AutoPickMode = "random";
        ToolConfigStore.FlushAll();
        var gomokuReloaded = JsonFileStore.Read<GomokuConfig>(gomokuCfg.FilePath);
        check("[配置] 五子棋配置改完自动落盘、能读回（含自动轮换设置）",
            gomokuReloaded is
            {
                PlayerName: "自检棋手", HostSide: "white", ForbiddenEnabled: true,
                BoardSize: 19, ListenPort: 5399, ShowMoveList: false,
                AutoRotateMode: "bothOut", AutoPickMode: "random",
            },
            JsonFileStore.Serialize(gomokuReloaded).Replace(Environment.NewLine, " "));
        var gomokuJson = File.ReadAllText(gomokuCfg.FilePath);
        check("[配置] 五子棋配置是 camelCase + 中文不转义（含 autoRotateMode / autoPickMode）",
            gomokuJson.Contains("\"playerName\"") && gomokuJson.Contains("自检棋手")
            && gomokuJson.Contains("\"autoRotateMode\"") && gomokuJson.Contains("\"autoPickMode\"")
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
        check("[配置] 象棋默认值：走法提示 / 最后一步 / 坐标全开",
            xiangqiDefaults.ShowLegalMoves && xiangqiDefaults.ShowLastMove
            && xiangqiDefaults.ShowCoordinates, "");
        check("[配置] 象棋默认值：观战视角=红方在下（SpectatorViewSide=red）",
            xiangqiDefaults.SpectatorViewSide == "red"
            && xiangqiDefaults.SpectatorViewSideValue == XiangqiSide.Red,
            $"{xiangqiDefaults.SpectatorViewSide}/{xiangqiDefaults.SpectatorViewSideValue}");
        check("[配置] 象棋默认值：让子方红、让先 0 步",
            xiangqiDefaults.HandicapGiver == "red" && xiangqiDefaults.OpeningFreeMoves == 0
            && xiangqiDefaults.OpeningFreeMovesEffective == 0, "");
        check("[配置] 象棋默认值：自动轮换=手动安排、补位=轮流上场",
            xiangqiDefaults.AutoRotateMode == "manual" && xiangqiDefaults.AutoPickMode == "rotation",
            $"{xiangqiDefaults.AutoRotateMode}/{xiangqiDefaults.AutoPickMode}");
        check("[配置] 象棋计算属性：阵营 / 让先夹取 0~5 / 执子文案 / 观战视角认不出按红",
            xiangqiDefaults.HostSideValue == XiangqiSide.Red
            && xiangqiDefaults.HandicapGiverValue == XiangqiSide.Red
            && new XiangqiConfig { OpeningFreeMoves = 9 }.OpeningFreeMovesEffective == 5
            && new XiangqiConfig { OpeningFreeMoves = -3 }.OpeningFreeMovesEffective == 0
            && new XiangqiConfig { HostSide = "black" }.HostSideText.Contains("黑方")
            && new XiangqiConfig { HostSide = "red" }.HostSideText.Contains("红方")
            && new XiangqiConfig { SpectatorViewSide = "black" }.SpectatorViewSideValue == XiangqiSide.Black
            && new XiangqiConfig { SpectatorViewSide = "乱写" }.SpectatorViewSideValue == XiangqiSide.Red, "");
        IBoardGameRoomConfig xiangqiRoom = xiangqiDefaults;
        xiangqiRoom.PlayerName = "接口棋手";
        xiangqiRoom.LastJoinAddress = "[::1]:5311";
        xiangqiRoom.ListenPort = 0;
        xiangqiRoom.AutoRotateMode = "bothOut";
        xiangqiRoom.AutoPickMode = "random";
        check("[配置] 象棋配置实现 IBoardGameRoomConfig，五个字段可直接读写",
            xiangqiDefaults.PlayerName == "接口棋手"
            && xiangqiDefaults.LastJoinAddress == "[::1]:5311"
            && xiangqiDefaults.ListenPort == 0
            && xiangqiDefaults.AutoRotateMode == "bothOut"
            && xiangqiDefaults.AutoPickMode == "random", "");

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
        xiangqiCfg.AutoRotateMode = "loserOut";
        xiangqiCfg.AutoPickMode = "random";
        xiangqiCfg.SpectatorViewSide = "black";
        ToolConfigStore.FlushAll();
        var xiangqiReloaded = JsonFileStore.Read<XiangqiConfig>(xiangqiCfg.FilePath);
        check("[配置] 象棋配置改完自动落盘、能读回（含自动轮换 / 观战视角设置）",
            xiangqiReloaded is
            {
                PlayerName: "象棋自检", HostSide: "black", ListenPort: 5312,
                ShowCoordinates: false, OpeningFreeMoves: 3,
                AutoRotateMode: "loserOut", AutoPickMode: "random",
                SpectatorViewSide: "black",
            },
            JsonFileStore.Serialize(xiangqiReloaded).Replace(Environment.NewLine, " "));
        var xiangqiJson = File.ReadAllText(xiangqiCfg.FilePath);
        check("[配置] 象棋配置是 camelCase（含 autoRotateMode / autoPickMode / spectatorViewSide）",
            xiangqiJson.Contains("\"autoRotateMode\"") && xiangqiJson.Contains("\"autoPickMode\"")
            && xiangqiJson.Contains("\"spectatorViewSide\"") && xiangqiJson.Contains("\"playerName\""),
            xiangqiJson.Replace(Environment.NewLine, " "));
        check("[配置] 象棋配置里不再出现旧的 autoFlip 字段",
            !xiangqiJson.Contains("autoFlip"), "");
        var legacyXiangqi = JsonSerializer.Deserialize<XiangqiConfig>(
            "{\"autoFlip\":false,\"spectatorViewSide\":\"black\",\"playerName\":\"旧配置\"}",
            RoomProtocol.JsonOptions);
        check("[配置] 旧配置里的 autoFlip 读取时被忽略（不报错、其它字段照常读）",
            legacyXiangqi is { SpectatorViewSide: "black", PlayerName: "旧配置" }, "");
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

        // ===== 棋牌协议常量（v2：多人观战 + 房主安排上场 + 自动轮换） =====
        check("[网络] 棋牌协议常量：magic / 版本 v2 / 两个游戏 Id",
            BoardProtocol.Magic == "YLTBOARD1" && BoardProtocol.Version == 2
            && BoardProtocol.GameGomoku == "gomoku" && BoardProtocol.GameXiangqi == "xiangqi",
            BoardProtocol.Describe());
        var allKinds = new[]
        {
            BoardProtocol.Kinds.Hello, BoardProtocol.Kinds.Welcome, BoardProtocol.Kinds.State,
            BoardProtocol.Kinds.Action, BoardProtocol.Kinds.Chat, BoardProtocol.Kinds.Notice,
            BoardProtocol.Kinds.Request, BoardProtocol.Kinds.Response, BoardProtocol.Kinds.Bye,
        };
        check("[网络] 协议包类型：hello / welcome / state / action / chat / notice / request / response / bye",
            allKinds.SequenceEqual(new[]
            {
                "hello", "welcome", "state", "action", "chat", "notice", "request", "response", "bye",
            }), string.Join("/", allKinds));
        check("[网络] 协议 v2 用 state 包替换旧 snapshot 包",
            BoardProtocol.Kinds.State == "state" && !allKinds.Contains("snapshot"), "");
        check("[网络] 请求类型常量用 BoardRequestTypes（undo / draw / rematch / resign）",
            new[] { BoardRequestTypes.Undo, BoardRequestTypes.Draw, BoardRequestTypes.Rematch, BoardRequestTypes.Resign }
                .SequenceEqual(new[] { "undo", "draw", "rematch", "resign" }), "");
        check("[网络] 断开原因分类齐全且互不相同（决定要不要自动重连）",
            new[]
            {
                BoardDisconnectKind.None, BoardDisconnectKind.LocalLeave,
                BoardDisconnectKind.PeerBye, BoardDisconnectKind.PeerClosed, BoardDisconnectKind.NetworkError,
            }.Distinct().Count() == 5, "");
        check("[网络] 协议说明文案含 v2 / magic / state 包 / 席位（排错与界面提示用）",
            BoardProtocol.Describe().Contains("v2") && BoardProtocol.Describe().Contains(BoardProtocol.Magic)
            && BoardProtocol.Describe().Contains("state") && BoardProtocol.Describe().Contains("席位"),
            BoardProtocol.Describe());

        // ===== 房间状态 DTO：成员 / 两个席位 / 自动轮换设置 =====
        var room = new BoardRoomState
        {
            Members =
            {
                new BoardMemberInfo
                {
                    MemberId = "host", Name = "房主", IsHost = true, IsOnline = true,
                    GamesPlayed = 3, JoinOrder = 1,
                },
                new BoardMemberInfo
                {
                    MemberId = "m1", Name = "小明", IsOnline = true, GamesPlayed = 1, JoinOrder = 2,
                },
                new BoardMemberInfo
                {
                    MemberId = "m2", Name = "小红", IsOnline = false, GamesPlayed = 0, JoinOrder = 3,
                },
            },
            SeatOneMemberId = "host",
            SeatTwoMemberId = "m1",
            AutoRotateMode = "loserOut",
            AutoPickMode = "random",
        };
        check("[网络] 房间状态默认值：空成员 / 空席位 / manual + rotation / 上限 16",
            new BoardRoomState() is
            {
                Members.Count: 0, SeatOneMemberId: "", SeatTwoMemberId: "",
                AutoRotateMode: "manual", AutoPickMode: "rotation", MaxMembers: 16,
            }, "");
        check("[网络] 成员信息默认值（会话里 GamesPlayed 恒 0，真实次数由基类维护）",
            new BoardMemberInfo() is
            {
                MemberId: "", Name: "", IsHost: false, IsOnline: false, GamesPlayed: 0, JoinOrder: 0,
            }, "");
        var memberCopy = room.Members[1].Clone();
        memberCopy.Name = "改过的名字";
        check("[网络] 成员信息 Clone 是拷贝（改副本不影响原成员）",
            memberCopy.MemberId == "m1" && room.Members[1].Name == "小明", memberCopy.Name);

        // ===== 状态包装 {seq, room, game} 往返 =====
        var gameElement = JsonSerializer.SerializeToElement(new { started = true, turn = "white", moves = 3 },
            RoomProtocol.JsonOptions);
        var wrapped = BoardProtocol.WrapState(12, room, gameElement);
        var envelope = BoardProtocol.ReadState(wrapped);
        check("[网络] 状态包装往返：seq / 成员表 / 两个席位 / 自动轮换设置 / 棋局快照都在",
            envelope is { Seq: 12 } && envelope.Room is { } roomInEnvelope
            && roomInEnvelope.Members.Count == 3
            && roomInEnvelope.Members[0].MemberId == "host" && roomInEnvelope.Members[0].IsHost
            && roomInEnvelope.Members[1].GamesPlayed == 1
            && roomInEnvelope.Members[2].IsOnline == false
            && roomInEnvelope.SeatOneMemberId == "host" && roomInEnvelope.SeatTwoMemberId == "m1"
            && roomInEnvelope.AutoRotateMode == "loserOut" && roomInEnvelope.AutoPickMode == "random"
            && roomInEnvelope.MaxMembers == 16
            && envelope.Game is { } gameInEnvelope
            && gameInEnvelope.GetProperty("turn").GetString() == "white"
            && gameInEnvelope.GetProperty("moves").GetInt32() == 3,
            JsonSerializer.Serialize(envelope, RoomProtocol.JsonOptions));
        var rawState = wrapped.GetRawText();
        check("[网络] 状态包装是 camelCase（room / members / memberId / seatOneMemberId / gamesPlayed）",
            rawState.Contains("\"room\"") && rawState.Contains("\"members\"") && rawState.Contains("\"memberId\"")
            && rawState.Contains("\"seatOneMemberId\"") && rawState.Contains("\"gamesPlayed\"")
            && rawState.Contains("\"autoRotateMode\""), rawState[..Math.Min(rawState.Length, 260)]);
        check("[网络] 坏状态包装读不回来时返回 null（容错，不抛异常）",
            BoardProtocol.ReadState(default) is null
            && BoardProtocol.ReadState(JsonSerializer.SerializeToElement(42, RoomProtocol.JsonOptions)) is null, "");
        var emptyState = BoardProtocol.ReadState(
            JsonSerializer.SerializeToElement(new { }, RoomProtocol.JsonOptions));
        check("[网络] 空对象状态包能读回但房间为 null（客户端不会因此崩）",
            emptyState is not null && emptyState.Room is null && emptyState.Game is null, "");
        var noGameState = BoardProtocol.ReadState(BoardProtocol.WrapState(1, new BoardRoomState(), default));
        check("[网络] game 缺省（undefined）时包装成 null 而不是坏 JSON",
            noGameState is { Game: null }, "");

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
            MemberId = "",
        });
        check("[网络] RoomFrame 的 Extra 强类型往返（HelloInfo：magic / 版本 / 游戏 / 名字 / 成员 id）",
            helloFrame.ExtraAs<HelloInfo>() is
            {
                Magic: "YLTBOARD1", GameId: "gomoku", PlayerName: "棋手",
                ProtocolVersion: 2, MemberId: "",
            },
            helloFrame.Extra?.GetRawText() ?? "(null)");
        check("[网络] HelloInfo.MemberId：首次进房为空、重连时带上次成员 id",
            new HelloInfo().MemberId == ""
            && RoomFrame.New("hello").WithExtra(new HelloInfo { MemberId = "m3" }).ExtraAs<HelloInfo>()
                is { MemberId: "m3" }, "");
        var welcomeFrame = RoomFrame.New("welcome").WithExtra(new WelcomeInfo
        {
            Magic = BoardProtocol.Magic,
            GameId = BoardProtocol.GameGomoku,
            HostName = "房主",
            ProtocolVersion = BoardProtocol.Version,
            YourMemberId = "m2",
            State = wrapped,
        });
        var welcomeRound = welcomeFrame.ExtraAs<WelcomeInfo>();
        check("[网络] WelcomeInfo：YourMemberId + State（完整状态包装）往返",
            welcomeRound is { YourMemberId: "m2", HostName: "房主", ProtocolVersion: 2 }
            && welcomeRound.State is { } welcomeState
            && BoardProtocol.ReadState(welcomeState) is { Seq: 12, Room: not null }, "");
        var stateInfoRound = RoomFrame.New("state")
            .WithExtra(new StateInfo { State = wrapped, Notice = "开始新的一局" })
            .ExtraAs<StateInfo>();
        check("[网络] StateInfo：State + Notice 往返",
            stateInfoRound is { Notice: "开始新的一局" } && stateInfoRound.State is { }, "");
        var chatInfoRound = RoomFrame.New("chat")
            .WithExtra(new ChatInfo { Name = "小明", Text = "你好", IsSystem = true })
            .ExtraAs<ChatInfo>();
        check("[网络] ChatInfo：IsSystem 往返（系统提示用灰字显示）",
            chatInfoRound is { Name: "小明", Text: "你好", IsSystem: true }, "");
        var actionRound = RoomFrame.New("action").WithExtra(new ActionInfo
        {
            Type = "move",
            Payload = JsonSerializer.SerializeToElement(new { x = 1, y = 2 }, RoomProtocol.JsonOptions),
        }).ExtraAs<ActionInfo>();
        check("[网络] ActionInfo：动作类型 + payload 往返",
            actionRound is { Type: "move" } && actionRound.Payload is { } actionPayload
            && actionPayload.GetProperty("x").GetInt32() == 1
            && actionPayload.GetProperty("y").GetInt32() == 2, "");
        var requestRound = RoomFrame.New("request")
            .WithExtra(new RequestInfo { Type = BoardRequestTypes.Undo }).ExtraAs<RequestInfo>();
        check("[网络] RequestInfo：请求类型往返（用 BoardRequestTypes 常量）",
            requestRound is { Type: "undo" }, "");
        var responseRound = RoomFrame.New("response")
            .WithExtra(new ResponseInfo { Type = BoardRequestTypes.Draw, Accept = false }).ExtraAs<ResponseInfo>();
        check("[网络] ResponseInfo：类型 + accept 往返",
            responseRound is { Type: "draw", Accept: false }, "");
        var byeRound = RoomFrame.New("bye")
            .WithExtra(new ByeInfo { Reason = "房主关闭了房间" }).ExtraAs<ByeInfo>();
        check("[网络] ByeInfo：中文原因往返（客户端据此显示「房主…」）",
            byeRound is { Reason: "房主关闭了房间" }, "");
        var noticeRound = RoomFrame.New("notice")
            .WithExtra(new NoticeInfo { Text = "提示", Level = "warn" }).ExtraAs<NoticeInfo>();
        check("[网络] NoticeInfo：文本 + 级别往返",
            noticeRound is { Text: "提示", Level: "warn" }, "");
        var welcomeNoState = RoomFrame.New("welcome").WithExtra(new WelcomeInfo
        {
            YourMemberId = "m9",
            HostName = "房主",
        }).ExtraAs<WelcomeInfo>();
        check("[网络] WelcomeInfo：没有 State 时也能解析（State = null，容错）",
            welcomeNoState is { YourMemberId: "m9", HostName: "房主", State: null }, "");
        var stateNoNotice = RoomFrame.New("state")
            .WithExtra(new StateInfo { State = wrapped }).ExtraAs<StateInfo>();
        check("[网络] StateInfo：Notice 可省略",
            stateNoNotice is { Notice: null, State: not null }, "");
        var memberJson = JsonSerializer.Serialize(room.Members[0], RoomProtocol.JsonOptions);
        check("[网络] 成员序列化：camelCase 字段（memberId / name / isHost / isOnline / gamesPlayed / joinOrder）",
            memberJson.Contains("\"memberId\"") && memberJson.Contains("\"name\"")
            && memberJson.Contains("\"isHost\"") && memberJson.Contains("\"isOnline\"")
            && memberJson.Contains("\"gamesPlayed\"") && memberJson.Contains("\"joinOrder\""), memberJson);
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

    // ---------- 本机回环 v2：多客户端房间（成员 / 聊天广播 / 按 id 单发 / 断线重连 / 容量） ----------

    private static async Task RunLoopbackAsync(Action<string, bool, string> check, List<string> report)
    {
        await RunMultiClientSessionAsync(check, report).ConfigureAwait(false);
        await RunCapacityAsync(check, report).ConfigureAwait(false);
        await RunHostClosingAsync(check, report).ConfigureAwait(false);
        await RunAbruptCutAsync(check, report).ConfigureAwait(false);
        await RunGomokuSessionAsync(check, report).ConfigureAwait(false);
        await RunXiangqiSessionAsync(check, report).ConfigureAwait(false);
        await RunConnectFailureAsync(check, report).ConfigureAwait(false);
        await RunRoomViewModelAsync(check, report).ConfigureAwait(false);
        await RunXiangqiViewChecksAsync(check, report).ConfigureAwait(false);
    }

    /// <summary>A. 多客户端会话：3 客户端加入 / 聊天广播 / 状态广播 / 按 id 单发 / 断线保成员 / 同 id 重连 / 重复登录 / 主动 bye 移除。</summary>
    private static async Task RunMultiClientSessionAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "房主");
        var room = new SessionRoomModel();
        room.AddHost("房主");
        var joined = new ConcurrentQueue<BoardMemberInfo>();
        var left = new ConcurrentQueue<(BoardMemberInfo Member, BoardDisconnectKind Kind, string Reason)>();
        var reconnected = new ConcurrentQueue<BoardMemberInfo>();
        var hostChats = new ConcurrentQueue<(string MemberId, string Name, string Text, bool IsSystem)>();
        var hostActions = new ConcurrentQueue<(string MemberId, string Type, JsonElement? Payload)>();
        var hostRequests = new ConcurrentQueue<(string MemberId, string Name, string Type)>();
        var hostResponses = new ConcurrentQueue<(string MemberId, string Type, bool Accept)>();
        WireHostSession(host, room);
        host.MemberJoined += m => joined.Enqueue(m);
        host.MemberLeft += (m, kind, reason) => left.Enqueue((m, kind, reason));
        host.MemberReconnected += m => reconnected.Enqueue(m);
        host.ChatReceived += (id, name, text, isSystem) => hostChats.Enqueue((id, name, text, isSystem));
        host.ActionReceived += (id, type, payload) => hostActions.Enqueue((id, type, payload));
        host.RequestReceived += (id, name, type, _) => hostRequests.Enqueue((id, name, type));
        host.ResponseReceived += (id, type, accept, _) => hostResponses.Enqueue((id, type, accept));

        var (c1, c1States, c1Chats, c1Requests, c1Responses) = NewClient("客户端1");
        var (c2, c2States, c2Chats, c2Requests, c2Responses) = NewClient("客户端2");
        var (c3, c3States, c3Chats, c3Requests, c3Responses) = NewClient("客户端3");
        try
        {
            var startError = await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            check("[多客户端] 主机用端口 0 开房成功（多客户端会话）",
                startError.Length == 0 && host.IsHost && host.IsOnline && host.MemberCount == 0
                && host.LocalMemberId == BoardRoomSession.HostMemberId,
                startError.Length > 0 ? startError : host.ListenText);
            check("[多客户端] 会话常量：房主 id=host、最多 16 人（15 个客户端）",
                BoardRoomSession.HostMemberId == "host" && BoardRoomSession.MaxMembers == 16
                && BoardRoomSession.MaxClients == 15, "");
            var port = host.ListenPort;
            check("[多客户端] 主机报得出真实监听端口",
                port > 0 && host.ListenText.Contains(port.ToString()), host.ListenText);
            report.Add($"多客户端主机监听 {host.ListenText}");

            var e1 = await c1.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            var e2 = await c2.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            var e3 = await c3.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
            await WaitUntilAsync(() => host.MemberCount == 3, 10000).ConfigureAwait(false);
            check("[多客户端] 3 个客户端都握手成功", e1.Length == 0 && e2.Length == 0 && e3.Length == 0
                && c1.IsClientConnected && c2.IsClientConnected && c3.IsClientConnected,
                $"{e1}|{e2}|{e3}");
            check("[多客户端] 主机成员数 = 3（不含房主）", host.MemberCount == 3, host.MemberCount.ToString());
            check("[多客户端] 每个客户端都拿到主机分配的成员 id（都不是 host、互不相同）",
                new[] { c1.LocalMemberId, c2.LocalMemberId, c3.LocalMemberId }
                    .All(id => id.Length > 0 && id != BoardRoomSession.HostMemberId)
                && new[] { c1.LocalMemberId, c2.LocalMemberId, c3.LocalMemberId }.Distinct().Count() == 3,
                $"{c1.LocalMemberId},{c2.LocalMemberId},{c3.LocalMemberId}");
            check("[多客户端] 主机触发 3 次 MemberJoined、名字正确",
                joined.Count == 3
                && joined.Select(m => m.Name).OrderBy(n => n)
                    .SequenceEqual(new[] { "客户端1", "客户端2", "客户端3" }),
                string.Join(",", joined.Select(m => $"{m.MemberId}:{m.Name}")));
            check("[多客户端] 成员进房顺序递增（自动轮换「轮流上场」靠它）",
                joined.Select(m => m.JoinOrder).SequenceEqual(new[] { 1, 2, 3 }),
                string.Join(",", joined.Select(m => m.JoinOrder)));

            // ===== 聊天：客户端发 → 主机收 + 转发其他人；主机广播 → 所有客户端 =====
            await c1.SendChatAsync("大家好").ConfigureAwait(false);
            await WaitUntilAsync(() => hostChats.Any(c => c.Text == "大家好"), 8000).ConfigureAwait(false);
            var firstChat = hostChats.FirstOrDefault(c => c.Text == "大家好");
            check("[多客户端] 客户端聊天：主机收到发送者 id / 名字 / 文本 / 非系统标记",
                firstChat.Text == "大家好" && firstChat.MemberId == c1.LocalMemberId
                && firstChat.Name == "客户端1" && !firstChat.IsSystem,
                $"{firstChat.MemberId}:{firstChat.Name}:{firstChat.Text}");
            await WaitUntilAsync(
                () => c2Chats.Any(c => c.Text == "大家好") && c3Chats.Any(c => c.Text == "大家好"), 8000)
                .ConfigureAwait(false);
            check("[多客户端] 客户端聊天转发给其他客户端（名字用主机记录的成员名）",
                c2Chats.Any(c => c.Text == "大家好" && c.Name == "客户端1")
                && c3Chats.Any(c => c.Text == "大家好" && c.Name == "客户端1"), "");
            check("[多客户端] 发送者自己不会收到自己聊天的转发（避免聊天记录重复）",
                !c1Chats.Any(c => c.Text == "大家好"),
                string.Join(" | ", c1Chats.Select(c => $"{c.Name}:{c.Text}")));

            await host.BroadcastChatAsync("房主", "房主说话", isSystem: false).ConfigureAwait(false);
            await WaitUntilAsync(
                () => c1Chats.Any(c => c.Text == "房主说话") && c2Chats.Any(c => c.Text == "房主说话")
                      && c3Chats.Any(c => c.Text == "房主说话"), 8000).ConfigureAwait(false);
            check("[多客户端] 主机的聊天广播给所有客户端",
                c1Chats.Any(c => c.Name == "房主" && c.Text == "房主说话")
                && c2Chats.Any(c => c.Name == "房主") && c3Chats.Any(c => c.Name == "房主"), "");
            await host.BroadcastChatAsync("", "系统提示（聊天）", isSystem: true).ConfigureAwait(false);
            await WaitUntilAsync(() => c1Chats.Any(c => c.Text == "系统提示（聊天）"), 8000).ConfigureAwait(false);
            check("[多客户端] 系统聊天（isSystem=true）也广播给所有客户端",
                c1Chats.Any(c => c.Text == "系统提示（聊天）" && c.IsSystem)
                && c2Chats.Any(c => c.Text == "系统提示（聊天）" && c.IsSystem), "");
            await host.BroadcastNoticeAsync("站位提示", "warn").ConfigureAwait(false);
            await WaitUntilAsync(() => c2Chats.Any(c => c.Text == "站位提示"), 8000).ConfigureAwait(false);
            check("[多客户端] 主机的系统提示（notice）到客户端会变成系统聊天",
                c2Chats.Any(c => c.Text == "站位提示" && c.IsSystem)
                && c3Chats.Any(c => c.Text == "站位提示" && c.IsSystem), "");

            // ===== 状态广播：席位 / 自动轮换设置 / 成员表 =====
            var c1WelcomeRoom = RoomOf(c1States.FirstOrDefault());
            check("[多客户端] 握手 welcome 带的完整状态：房主 + 自己都在成员表里（都在线）",
                c1WelcomeRoom is { Members: var firstMembers } && firstMembers.Count == 2
                && firstMembers.Any(m => m.MemberId == BoardRoomSession.HostMemberId && m.IsHost && m.IsOnline)
                && firstMembers.Any(m => m.MemberId == c1.LocalMemberId && m.IsOnline),
                c1WelcomeRoom is null ? "(无状态)" : string.Join(",", c1WelcomeRoom.Members.Select(m => $"{m.MemberId}:{m.Name}")));
            room.SetSeats(BoardRoomSession.HostMemberId, c1.LocalMemberId);
            room.SetModes("loserOut", "random");
            await host.BroadcastStateAsync(room.BuildEnvelope()).ConfigureAwait(false);
            await WaitUntilAsync(
                () => RoomOf(c1States.LastOrDefault()) is { } r
                      && r.SeatOneMemberId == BoardRoomSession.HostMemberId
                      && r.SeatTwoMemberId == c1.LocalMemberId, 8000).ConfigureAwait(false);
            await WaitUntilAsync(
                () => c2States.Any(s => RoomOf(s) is { } r && r.SeatTwoMemberId == c1.LocalMemberId)
                      && c3States.Any(s => RoomOf(s) is { } r2 && r2.SeatTwoMemberId == c1.LocalMemberId), 8000)
                .ConfigureAwait(false);
            var latestRoom = RoomOf(c3States.LastOrDefault());
            check("[多客户端] 状态广播：两个席位 / 自动轮换设置 / 4 个成员（含房主）都到客户端",
                latestRoom is { Members.Count: 4, AutoRotateMode: "loserOut", AutoPickMode: "random" }
                && latestRoom.SeatOneMemberId == BoardRoomSession.HostMemberId
                && latestRoom.SeatTwoMemberId == c1.LocalMemberId
                && latestRoom.Members.Count(m => m.IsHost) == 1,
                latestRoom is null ? "(无状态)" : $"{latestRoom.SeatOneMemberId}/{latestRoom.SeatTwoMemberId} {latestRoom.Members.Count} 人");

            // ===== 请求 / 应答：主机按成员 id 单发，其他客户端收不到 =====
            await host.SendRequestToAsync(c2.LocalMemberId, BoardRequestTypes.Undo).ConfigureAwait(false);
            await WaitUntilAsync(() => c2Requests.Any(r => r.Type == BoardRequestTypes.Undo), 8000).ConfigureAwait(false);
            check("[多客户端] 主机按 id 单发请求：只有被指定的客户端收到（from=host）",
                c2Requests.Any(r => r.From == BoardRoomSession.HostMemberId && r.Name == "房主"
                                    && r.Type == BoardRequestTypes.Undo)
                && !c1Requests.Any(r => r.Type == BoardRequestTypes.Undo)
                && !c3Requests.Any(r => r.Type == BoardRequestTypes.Undo), "");
            await c2.SendResponseAsync(BoardRequestTypes.Undo, true).ConfigureAwait(false);
            await WaitUntilAsync(() => hostResponses.Any(r => r.Type == BoardRequestTypes.Undo), 8000)
                .ConfigureAwait(false);
            check("[多客户端] 客户端回应答：主机收到（来自哪个成员 + accept=true）",
                hostResponses.Any(r => r.MemberId == c2.LocalMemberId
                                       && r.Type == BoardRequestTypes.Undo && r.Accept), "");
            await c3.SendRequestAsync(BoardRequestTypes.Draw).ConfigureAwait(false);
            await WaitUntilAsync(() => hostRequests.Any(r => r.Type == BoardRequestTypes.Draw), 8000)
                .ConfigureAwait(false);
            check("[多客户端] 客户端请求：主机收到（成员 id / 名字 / 类型）",
                hostRequests.Any(r => r.MemberId == c3.LocalMemberId
                                      && r.Name == "客户端3" && r.Type == BoardRequestTypes.Draw), "");
            await host.SendResponseToAsync(c3.LocalMemberId, BoardRequestTypes.Draw, false).ConfigureAwait(false);
            await WaitUntilAsync(() => c3Responses.Any(r => r.Type == BoardRequestTypes.Draw), 8000)
                .ConfigureAwait(false);
            check("[多客户端] 主机按 id 单发应答：只有那个客户端收到 accept=false",
                c3Responses.Any(r => r.From == BoardRoomSession.HostMemberId
                                     && r.Type == BoardRequestTypes.Draw && !r.Accept)
                && !c1Responses.Any(r => r.Type == BoardRequestTypes.Draw)
                && !c2Responses.Any(r => r.Type == BoardRequestTypes.Draw), "");

            // ===== 动作：客户端上报，主机拿到发送者 id（基类据此校验是否席上玩家） =====
            await c2.SendActionAsync("move", new { x = 7, y = 7 }).ConfigureAwait(false);
            await WaitUntilAsync(() => hostActions.Any(a => a.Type == "move"), 8000).ConfigureAwait(false);
            var action = hostActions.FirstOrDefault(a => a.Type == "move");
            check("[多客户端] 客户端动作：主机收到发送者成员 id + payload（可用于校验是否席上玩家）",
                action.MemberId == c2.LocalMemberId && action.Payload is { } actionPayload
                && actionPayload.GetProperty("x").GetInt32() == 7
                && actionPayload.GetProperty("y").GetInt32() == 7,
                $"{action.MemberId}:{action.Payload?.GetRawText()}");

            // ===== 断网：成员保留离线；同 id 重连恢复；旧连接活着时同 id 登录被拒 =====
            var c1Id = c1.LocalMemberId;
            c1.AbortConnection("模拟断网");
            await WaitUntilAsync(() => left.Any(l => l.Member.MemberId == c1Id), 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => host.MemberCount == 2, 8000).ConfigureAwait(false);
            var c1Left = left.First(l => l.Member.MemberId == c1Id);
            check("[多客户端] 客户端断网（不带 bye）：主机 MemberLeft 属于网络类原因",
                c1Left.Kind is BoardDisconnectKind.NetworkError or BoardDisconnectKind.PeerClosed
                && !c1Left.Member.IsOnline, $"{c1Left.Kind}: {c1Left.Reason}");
            check("[多客户端] 断网成员保留在成员表里（IsOnline=false，等同 id 回来）",
                host.MemberCount == 2
                && room.Members.FirstOrDefault(m => m.MemberId == c1Id) is { IsOnline: false }, "");
            check("[多客户端] 断网客户端自己记录的分类是 NetworkError（基类据此自动重连）",
                c1.LastDisconnectKind == BoardDisconnectKind.NetworkError,
                c1.LastDisconnectKind.ToString());
            await WaitUntilAsync(
                () => RoomOf(c2States.LastOrDefault()) is { Members: var members }
                      && members.Count == 4 && members.Any(m => m.MemberId == c1Id && !m.IsOnline), 8000)
                .ConfigureAwait(false);
            check("[多客户端] 其他客户端从状态包看到该成员离线、但仍在房间里（人数不变）",
                RoomOf(c2States.LastOrDefault()) is { Members: var offlineMembers }
                && offlineMembers.Count == 4
                && offlineMembers.Any(m => m.MemberId == c1Id && !m.IsOnline), "");

            var (c1r, c1rStates, _, _, _) = NewClient("客户端1");
            try
            {
                var reconnectError = await c1r.ConnectAsync("127.0.0.1", port, previousMemberId: c1Id)
                    .ConfigureAwait(false);
                await WaitUntilAsync(() => host.MemberCount == 3, 8000).ConfigureAwait(false);
                check("[多客户端] 同 id 重连成功、MemberId 复用不变",
                    reconnectError.Length == 0 && c1r.LocalMemberId == c1Id && c1r.IsClientConnected,
                    reconnectError);
                await WaitUntilAsync(() => reconnected.Any(m => m.MemberId == c1Id), 8000).ConfigureAwait(false);
                check("[多客户端] 主机触发 MemberReconnected（成员身份保留）",
                    reconnected.Any(m => m.MemberId == c1Id), string.Join(",", reconnected.Select(m => m.MemberId)));
                await WaitUntilAsync(
                    () => RoomOf(c1rStates.FirstOrDefault()) is { } welcomeRoom2
                          && welcomeRoom2.Members.Count == 4
                          && welcomeRoom2.Members.Any(m => m.MemberId == c1Id && m.IsOnline), 8000)
                    .ConfigureAwait(false);
                check("[多客户端] 重连 welcome 里的状态：该成员已在线、成员数不变",
                    RoomOf(c1rStates.FirstOrDefault()) is { } reconnectWelcome
                    && reconnectWelcome.Members.Count == 4
                    && reconnectWelcome.Members.Any(m => m.MemberId == c1Id && m.IsOnline),
                    RoomOf(c1rStates.FirstOrDefault())?.Members.Count.ToString() ?? "(无状态)");

                var duplicate = new BoardRoomSession(BoardProtocol.GameGomoku, "冒名者");
                try
                {
                    var duplicateError = await duplicate
                        .ConnectAsync("127.0.0.1", port, previousMemberId: c2.LocalMemberId).ConfigureAwait(false);
                    check("[多客户端] 旧连接还活着时用同 id 登录被拒（提示「重复登录」）",
                        duplicateError.Length > 0 && duplicateError.Contains("重复登录"), duplicateError);
                    check("[多客户端] 重复登录失败不影响原成员（c2 仍在线、成员数不变）",
                        c2.IsClientConnected && host.MemberCount == 3, host.MemberCount.ToString());
                }
                finally
                {
                    await SafeStopAsync(duplicate).ConfigureAwait(false);
                }
            }
            finally
            {
                await SafeStopAsync(c1r).ConfigureAwait(false);
            }
            await WaitUntilAsync(() => host.MemberCount == 2, 8000).ConfigureAwait(false);
            check("[多客户端] 重连客户端再次离开后成员数回到 2", host.MemberCount == 2, host.MemberCount.ToString());

            // ===== 主动 bye：成员从成员表移除（基类据此清空席位） =====
            // 说明：网络层已在客户端 StopAsync 里加 80ms 排空窗口，主动 bye 稳定落在 PeerBye，这里不再需要额外等待
            var c3Id = c3.LocalMemberId;
            await c3.StopAsync("主动离开").ConfigureAwait(false);
            await WaitUntilAsync(() => left.Any(l => l.Member.MemberId == c3Id), 8000).ConfigureAwait(false);
            var c3Left = left.First(l => l.Member.MemberId == c3Id);
            check("[多客户端] 客户端主动 bye：MemberLeft 分类 PeerBye 且成员从表里移除",
                c3Left.Kind == BoardDisconnectKind.PeerBye
                && host.MemberCount == 1, $"{c3Left.Kind}: {c3Left.Reason} / 成员数 {host.MemberCount}");
            await WaitUntilAsync(
                () => RoomOf(c2States.LastOrDefault()) is { Members: var afterBye }
                      && afterBye.Count == 2 && afterBye.All(m => m.MemberId != c3Id), 8000).ConfigureAwait(false);
            check("[多客户端] 主动离开后其他客户端状态包里该成员消失（席位由基类清空）",
                RoomOf(c2States.LastOrDefault()) is { Members: var byeMembers }
                && byeMembers.Count == 2 && byeMembers.All(m => m.MemberId != c3Id), "");

            // ===== 状态包 seq / 成员排序 / 房主信息 =====
            var seqs = c2States.Select(e => BoardProtocol.ReadState(e)?.Seq ?? -1).Where(s => s > 0).ToList();
            check("[多客户端] 客户端收到的状态包 seq 单调不减（乱序旧包可丢）",
                seqs.Count >= 2 && seqs.Zip(seqs.Skip(1)).All(pair => pair.First <= pair.Second),
                string.Join(",", seqs.Take(12)));
            var finalRoom = RoomOf(c2States.LastOrDefault());
            check("[多客户端] 状态包成员按进房顺序排（房主 joinOrder = 0、排第一）",
                finalRoom is { Members.Count: > 0 }
                && finalRoom.Members[0].IsHost && finalRoom.Members[0].JoinOrder == 0,
                finalRoom is null ? "(无状态)" : string.Join(",", finalRoom.Members.Select(m => $"{m.Name}:{m.JoinOrder}")));
            check("[多客户端] 房主 LocalMemberId = host、成员数文案 15 上限",
                host.LocalMemberId == BoardRoomSession.HostMemberId
                && BoardRoomSession.MaxClients == BoardRoomSession.MaxMembers - 1, host.LocalMemberId);
            check("[多客户端] 房主 / 客户端的会话角色不会混淆（Host vs Client）",
                host.IsHost && !c2.IsHost && c2.IsClientConnected,
                $"host.IsHost={host.IsHost} c2.IsHost={c2.IsHost} c2.Connected={c2.IsClientConnected}");
            check("[多客户端] 状态包里带上房间人数上限（MaxMembers = 16）",
                RoomOf(c2States.LastOrDefault()) is { MaxMembers: 16 }, "");
            var c3WelcomeRoom = RoomOf(c3States.FirstOrDefault());
            check("[多客户端] 后加入者的 welcome 状态里已有全部 4 个成员（房主 + 3 客户端）",
                c3WelcomeRoom is { Members.Count: 4 }, c3WelcomeRoom?.Members.Count.ToString() ?? "(无状态)");
            check("[多客户端] 默认自动轮换设置：manual + rotation（新房间开局）",
                RoomOf(c1States.FirstOrDefault()) is { AutoRotateMode: "manual", AutoPickMode: "rotation" }, "");
            check("[多客户端] MemberJoined 只统计首次进房（重连不重复触发）",
                joined.Count == 3, joined.Count.ToString());

            await host.StopAsync("自检结束").ConfigureAwait(false);
            // 断开回调里「连接没了」与「分类写好」之间有一瞬，等分类落定再断言
            await WaitUntilAsync(() => !c2.IsClientConnected
                && c2.LastDisconnectKind == BoardDisconnectKind.PeerBye, 8000).ConfigureAwait(false);
            check("[多客户端] 主机关房：剩余客户端收到断开、分类 PeerBye（不会自动重连）",
                !c2.IsClientConnected && c2.LastDisconnectKind == BoardDisconnectKind.PeerBye,
                c2.LastDisconnectKind.ToString());
            check("[多客户端] 主机关房后不再监听", !host.IsOnline && !host.IsHost, "");
        }
        finally
        {
            await SafeStopAsync(c1).ConfigureAwait(false);
            await SafeStopAsync(c2).ConfigureAwait(false);
            await SafeStopAsync(c3).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>B. 容量：房主 + 15 个客户端刚好满（共 16 人）；第 16 个客户端被拒并提示房间已满。</summary>
    private static async Task RunCapacityAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "容量主机");
        var room = new SessionRoomModel();
        room.AddHost("容量主机");
        WireHostSession(host, room);
        var clients = new List<BoardRoomSession>();
        try
        {
            var startError = await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            check("[容量] 主机开房成功", startError.Length == 0 && host.IsOnline, startError);
            var port = host.ListenPort;

            string? failedError = null;
            for (var i = 1; i <= BoardRoomSession.MaxClients; i++)
            {
                var client = new BoardRoomSession(BoardProtocol.GameGomoku, $"客户端{i}");
                clients.Add(client);
                var error = await client.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                if (error.Length > 0)
                {
                    failedError = error;
                    break;
                }
            }

            await WaitUntilAsync(() => host.MemberCount == BoardRoomSession.MaxClients, 15000)
                .ConfigureAwait(false);
            check($"[容量] 房主 + {BoardRoomSession.MaxClients} 个客户端全部加入（共 {BoardRoomSession.MaxMembers} 人）",
                failedError is null && host.MemberCount == BoardRoomSession.MaxClients
                && clients.Count(c => c.IsClientConnected) == BoardRoomSession.MaxClients,
                $"失败：{failedError} / 成员数 {host.MemberCount}");

            var extra = new BoardRoomSession(BoardProtocol.GameGomoku, "第 17 个人");
            try
            {
                var extraError = await extra.ConnectAsync("127.0.0.1", port).ConfigureAwait(false);
                check("[容量] 超过上限（第 16 个客户端）被拒：提示「房间已满」与 16 人上限",
                    extraError.Length > 0 && extraError.Contains("房间已满") && extraError.Contains("16"),
                    extraError);
                check("[容量] 被拒的连接不占成员表（成员数仍是 15）",
                    host.MemberCount == BoardRoomSession.MaxClients, host.MemberCount.ToString());
                check("[容量] 被拒之后原有客户端不受影响",
                    clients.Take(3).All(c => c.IsClientConnected), "");
            }
            finally
            {
                await SafeStopAsync(extra).ConfigureAwait(false);
            }

            await host.StopAsync("自检结束").ConfigureAwait(false);
            check("[容量] 主机 Stop 后不再监听", !host.IsOnline, "");
        }
        finally
        {
            foreach (var client in clients)
                await SafeStopAsync(client).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>C. 主机关房：所有客户端收到 bye（分类 PeerBye）、原因可读、不再联机。</summary>
    private static async Task RunHostClosingAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "关房主机");
        var room = new SessionRoomModel();
        room.AddHost("关房主机");
        WireHostSession(host, room);
        var c1 = new BoardRoomSession(BoardProtocol.GameGomoku, "客户端A");
        var c2 = new BoardRoomSession(BoardProtocol.GameGomoku, "客户端B");
        try
        {
            await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            var e1 = await c1.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            var e2 = await c2.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.MemberCount == 2, 10000).ConfigureAwait(false);
            check("[关房] 两个客户端都加入成功",
                e1.Length == 0 && e2.Length == 0 && host.MemberCount == 2, $"{e1}|{e2}");

            await host.StopAsync("房主关闭了房间").ConfigureAwait(false);
            await WaitUntilAsync(() => !c1.IsClientConnected && !c2.IsClientConnected, 10000).ConfigureAwait(false);
            await WaitUntilAsync(() => c1.LastDisconnectKind != BoardDisconnectKind.None
                && c2.LastDisconnectKind != BoardDisconnectKind.None, 5000).ConfigureAwait(false);
            check("[关房] 主机关房：所有客户端都变成未连接",
                !c1.IsClientConnected && !c2.IsClientConnected, "");
            check("[关房] 客户端断开分类是 PeerBye（基类据此不自动重连）",
                c1.LastDisconnectKind == BoardDisconnectKind.PeerBye
                && c2.LastDisconnectKind == BoardDisconnectKind.PeerBye,
                $"{c1.LastDisconnectKind}/{c2.LastDisconnectKind}");
            check("[关房] 客户端拿到的原因可读（含「房主」）",
                c1.LastDisconnectReason.Contains("房主") && c2.LastDisconnectReason.Contains("房主"),
                $"{c1.LastDisconnectReason} / {c2.LastDisconnectReason}");
            check("[关房] 主机关房后自己也不在线", !host.IsOnline && !host.IsHost, "");
        }
        finally
        {
            await SafeStopAsync(c1).ConfigureAwait(false);
            await SafeStopAsync(c2).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>D. 网络被掐断（AbortConnection 不发 bye）：成员保留离线、原因分类与房主关房不同。</summary>
    private static async Task RunAbruptCutAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "断网主机");
        var room = new SessionRoomModel();
        room.AddHost("断网主机");
        WireHostSession(host, room);
        var left = new ConcurrentQueue<(BoardMemberInfo Member, BoardDisconnectKind Kind, string Reason)>();
        host.MemberLeft += (m, kind, reason) => left.Enqueue((m, kind, reason));
        var c1 = new BoardRoomSession(BoardProtocol.GameGomoku, "断网客户端");
        var c2 = new BoardRoomSession(BoardProtocol.GameGomoku, "正常客户端");
        try
        {
            await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            var e1 = await c1.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            var e2 = await c2.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.MemberCount == 2, 10000).ConfigureAwait(false);
            check("[断网] 两个客户端都加入成功", e1.Length == 0 && e2.Length == 0, $"{e1}|{e2}");

            var c1Id = c1.LocalMemberId;
            c1.AbortConnection("模拟断网");
            await WaitUntilAsync(() => left.Any(l => l.Member.MemberId == c1Id), 8000).ConfigureAwait(false);
            var cut = left.First(l => l.Member.MemberId == c1Id);
            check("[断网] 客户端不发 bye 直接断：主机 MemberLeft 分类为网络类",
                cut.Kind is BoardDisconnectKind.NetworkError or BoardDisconnectKind.PeerClosed,
                $"{cut.Kind}: {cut.Reason}");
            check("[断网] 断网成员保留在房间成员表里（IsOnline=false，可同 id 回来）",
                !cut.Member.IsOnline
                && room.Members.FirstOrDefault(m => m.MemberId == c1Id) is { IsOnline: false }, "");
            check("[断网] 断网端自己的断开分类是 NetworkError（基类据此自动重连）",
                c1.LastDisconnectKind == BoardDisconnectKind.NetworkError,
                c1.LastDisconnectKind.ToString());
            check("[断网] 另一个客户端不受影响", c2.IsClientConnected, "");
            check("[断网] 与房主关房区分：分类不是 PeerBye",
                cut.Kind != BoardDisconnectKind.PeerBye, cut.Kind.ToString());
        }
        finally
        {
            await SafeStopAsync(c1).ConfigureAwait(false);
            await SafeStopAsync(c2).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>E. 五子棋完整小对局（v2 状态包）：房主执黑、客户端执白走完一局；第三人是观战者（收状态、动作不被执行）。</summary>
    private static async Task RunGomokuSessionAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "自检主机");
        var room = new SessionRoomModel();
        room.AddHost("自检主机");
        var gameGate = new object();
        var game = new GomokuGame();
        room.SetGame(GameJson(game));
        WireHostSession(host, room);
        var rejectedActions = new ConcurrentQueue<string>();
        var appliedActions = new ConcurrentQueue<string>();
        host.ActionReceived += (memberId, type, payload) =>
        {
            if (type != "move" || payload is not { } move) return;
            var side = room.IsSeatOne(memberId) ? GomokuCell.Black
                : room.IsSeatTwo(memberId) ? GomokuCell.White
                : GomokuCell.Empty;
            if (side == GomokuCell.Empty)
            {
                rejectedActions.Enqueue(memberId);
                return;
            }
            string? error;
            lock (gameGate)
            {
                error = game.Play(side, move.GetProperty("x").GetInt32(), move.GetProperty("y").GetInt32());
            }
            if (error is not null)
            {
                rejectedActions.Enqueue(memberId);
                return;
            }
            appliedActions.Enqueue(memberId);
            room.SetGame(GameJson(game));
            _ = host.BroadcastStateAsync(room.BuildEnvelope());
        };

        var (player, playerStates, playerChats, _, _) = NewClient("执白玩家");
        var (watcher, watcherStates, _, _, _) = NewClient("观战者");        try
        {
            await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            var ep = await player.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            var ew = await watcher.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.MemberCount == 2, 10000).ConfigureAwait(false);
            check("[五子棋·v2] 主机 + 执白玩家 + 观战者都加入", ep.Length == 0 && ew.Length == 0
                && host.MemberCount == 2, $"{ep}|{ew}");

            room.SetSeats(BoardRoomSession.HostMemberId, player.LocalMemberId);
            await host.BroadcastStateAsync(room.BuildEnvelope()).ConfigureAwait(false);
            await WaitUntilAsync(
                () => RoomOf(playerStates.LastOrDefault()) is { } r
                      && r.SeatOneMemberId == BoardRoomSession.HostMemberId
                      && r.SeatTwoMemberId == player.LocalMemberId, 8000).ConfigureAwait(false);
            var seatsRoom = RoomOf(playerStates.LastOrDefault());
            check("[五子棋·v2] 状态包里的席位：房主先手位 / 客户端后手位",
                seatsRoom is not null && seatsRoom.SeatOneMemberId == BoardRoomSession.HostMemberId
                && seatsRoom.SeatTwoMemberId == player.LocalMemberId
                && seatsRoom.Members.Count == 3,
                seatsRoom is null ? "(无状态)" : $"{seatsRoom.SeatOneMemberId}/{seatsRoom.SeatTwoMemberId}");

            async Task HostPlayAsync(int x, int y)
            {
                lock (gameGate) game.Play(GomokuCell.Black, x, y);
                room.SetGame(GameJson(game));
                await host.BroadcastStateAsync(room.BuildEnvelope()).ConfigureAwait(false);
            }

            async Task ClientPlayAsync(int x, int y)
            {
                int target;
                lock (gameGate) target = game.MoveCount + 1;
                await player.SendActionAsync("move", new { x, y }).ConfigureAwait(false);
                await WaitUntilAsync(() => { lock (gameGate) return game.MoveCount == target; }, 8000)
                    .ConfigureAwait(false);
                await WaitUntilAsync(() => StateGomokuMoveCount(playerStates.LastOrDefault()) == target, 8000)
                    .ConfigureAwait(false);
            }

            await HostPlayAsync(0, 0).ConfigureAwait(false);
            await ClientPlayAsync(7, 7).ConfigureAwait(false);
            await HostPlayAsync(1, 0).ConfigureAwait(false);
            await ClientPlayAsync(7, 8).ConfigureAwait(false);
            await HostPlayAsync(2, 0).ConfigureAwait(false);
            await ClientPlayAsync(7, 9).ConfigureAwait(false);
            await HostPlayAsync(3, 0).ConfigureAwait(false);
            await ClientPlayAsync(8, 7).ConfigureAwait(false);
            await HostPlayAsync(4, 0).ConfigureAwait(false);
            await WaitUntilAsync(() => StateGomokuResult(playerStates.LastOrDefault()) == "blackWin", 8000)
                .ConfigureAwait(false);

            var clientGame = RestoreGomoku(playerStates.LastOrDefault());
            check("[五子棋·v2] 客户端从状态包重建棋局：黑棋获胜、获胜连线 5 颗",
                clientGame is { Result: GomokuResult.BlackWin } && clientGame.WinningLine.Count == 5,
                clientGame?.ResultText ?? "(无法重建)");
            check("[五子棋·v2] 主机端棋局：9 手（黑 5 + 白 4）且黑胜",
                game.Result == GomokuResult.BlackWin && game.MoveCount == 9,
                $"{game.ResultText} / {game.MoveCount} 手");
            check("[五子棋·v2] 客户端上报的 4 手白棋都被主机执行",
                appliedActions.Count >= 4 && appliedActions.All(id => id == player.LocalMemberId),
                appliedActions.Count.ToString());
            check("[五子棋·v2] 观战者持续收到状态包（能看到棋子与结果）",
                StateGomokuMoveCount(watcherStates.LastOrDefault()) == 9
                && StateGomokuResult(watcherStates.LastOrDefault()) == "blackWin", "");

            // 观战者尝试走子：主机按席位校验后拒绝（棋局不变）——观战只读的会话层保障
            var beforeWatchMove = game.MoveCount;
            await watcher.SendActionAsync("move", new { x = 10, y = 10 }).ConfigureAwait(false);
            await WaitUntilAsync(() => rejectedActions.Count >= 1, 8000).ConfigureAwait(false);
            check("[五子棋·v2] 观战者发动作：主机收到但按席位拒绝（棋局手数不变）",
                rejectedActions.Contains(watcher.LocalMemberId) && game.MoveCount == beforeWatchMove,
                $"{string.Join(",", rejectedActions)} / 手数 {game.MoveCount}");
            check("[五子棋·v2] 观战者仍正常收到状态与聊天（只是不能下）",
                watcherStates.Count >= 5 && watcher.IsClientConnected, "");

            await host.StopAsync("自检结束").ConfigureAwait(false);
        }
        finally
        {
            await SafeStopAsync(player).ConfigureAwait(false);
            await SafeStopAsync(watcher).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>F. 象棋一步同步（v2 状态包）：房主执红「炮二平五」→ 客户端执黑「马8进7」→ 双方状态一致。</summary>
    private static async Task RunXiangqiSessionAsync(Action<string, bool, string> check, List<string> report)
    {
        var host = new BoardRoomSession(BoardProtocol.GameXiangqi, "象棋主机");
        var room = new SessionRoomModel();
        room.AddHost("象棋主机");
        var gameGate = new object();
        var game = new XiangqiGame();
        room.SetGame(GameJson(game));
        WireHostSession(host, room);
        host.ActionReceived += (memberId, type, payload) =>
        {
            if (type != "move" || payload is not { } move) return;
            var side = room.IsSeatOne(memberId) ? XiangqiSide.Red
                : room.IsSeatTwo(memberId) ? XiangqiSide.Black
                : XiangqiSide.Red;
            if (!room.IsSeatOne(memberId) && !room.IsSeatTwo(memberId)) return;
            string? error;
            lock (gameGate)
            {
                error = game.Play(side,
                    move.GetProperty("fromX").GetInt32(), move.GetProperty("fromY").GetInt32(),
                    move.GetProperty("toX").GetInt32(), move.GetProperty("toY").GetInt32());
            }
            if (error is not null) return;
            room.SetGame(GameJson(game));
            _ = host.BroadcastStateAsync(room.BuildEnvelope());
        };

        var (player, playerStates, _, _, _) = NewClient("执黑棋手", BoardProtocol.GameXiangqi);
        var (watcher, watcherStates, _, _, _) = NewClient("象棋观战者", BoardProtocol.GameXiangqi);
        try
        {
            await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            var ep = await player.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            var ew = await watcher.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
            await WaitUntilAsync(() => host.MemberCount == 2, 10000).ConfigureAwait(false);
            check("[象棋·v2] 主机 + 执黑玩家 + 观战者都加入", ep.Length == 0 && ew.Length == 0
                && host.MemberCount == 2, $"{ep}|{ew}");

            room.SetSeats(BoardRoomSession.HostMemberId, player.LocalMemberId);
            await host.BroadcastStateAsync(room.BuildEnvelope()).ConfigureAwait(false);
            await WaitUntilAsync(
                () => StateXiangqiHistory(playerStates.LastOrDefault()) >= 0
                      && RoomOf(playerStates.LastOrDefault()) is { } r && r.SeatTwoMemberId == player.LocalMemberId,
                8000).ConfigureAwait(false);
            check("[象棋·v2] 状态包：席位 + 初始局面（0 步）",
                RoomOf(playerStates.LastOrDefault()) is { } xqRoom
                && xqRoom.SeatOneMemberId == BoardRoomSession.HostMemberId
                && xqRoom.SeatTwoMemberId == player.LocalMemberId
                && StateXiangqiHistory(playerStates.LastOrDefault()) == 0,
                $"{StateXiangqiHistory(playerStates.LastOrDefault())} 步");

            lock (gameGate) game.Play(XiangqiSide.Red, 7, 7, 4, 7);
            room.SetGame(GameJson(game));
            await host.BroadcastStateAsync(room.BuildEnvelope()).ConfigureAwait(false);
            await WaitUntilAsync(() => StateXiangqiHistory(playerStates.LastOrDefault()) >= 1, 8000)
                .ConfigureAwait(false);
            var oneMove = RestoreXiangqi(playerStates.LastOrDefault());
            check("[象棋·v2] 主机走红「炮二平五」：客户端按状态包重建出这一步",
                oneMove is { History.Count: 1 } && oneMove.History[0].Notation == "炮二平五"
                && oneMove.Turn == XiangqiSide.Black,
                oneMove is null ? "(无法重建)" : string.Join(" / ", oneMove.History.Select(h => h.Notation)));

            await player.SendActionAsync("move", new { fromX = 7, fromY = 0, toX = 6, toY = 2 })
                .ConfigureAwait(false);
            await WaitUntilAsync(() => { lock (gameGate) return game.History.Count == 2; }, 8000)
                .ConfigureAwait(false);
            await WaitUntilAsync(() => StateXiangqiHistory(playerStates.LastOrDefault()) >= 2, 8000)
                .ConfigureAwait(false);
            var twoMoves = RestoreXiangqi(playerStates.LastOrDefault());
            check("[象棋·v2] 客户端走黑「马8进7」：主机执行并广播回来",
                twoMoves is { History.Count: 2 } && twoMoves.History[1].Notation == "马8进7",
                twoMoves is null ? "(无法重建)" : string.Join(" / ", twoMoves.History.Select(h => h.Notation)));
            check("[象棋·v2] 观战者同样收到两步后的状态", StateXiangqiHistory(watcherStates.LastOrDefault()) == 2,
                StateXiangqiHistory(watcherStates.LastOrDefault()).ToString());

            await host.StopAsync("自检结束").ConfigureAwait(false);
        }
        finally
        {
            await SafeStopAsync(player).ConfigureAwait(false);
            await SafeStopAsync(watcher).ConfigureAwait(false);
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    /// <summary>G. 连不上 / 连错房间 / 旧版本客户端：都是中文错误、不崩、主机不受影响。</summary>
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
            check("[连接失败] 连一个没开房的端口 → 中文错误、不崩",
                error.Length > 0 && !orphan.IsClientConnected, error);
        }
        finally
        {
            await SafeStopAsync(orphan).ConfigureAwait(false);
        }

        // 游戏类型不一致：五子棋主机 + 象棋客户端
        var host = new BoardRoomSession(BoardProtocol.GameGomoku, "五子棋房主");
        var room = new SessionRoomModel();
        room.AddHost("五子棋房主");
        WireHostSession(host, room);
        try
        {
            await host.StartHostAsync(0, () => room.BuildEnvelope()).ConfigureAwait(false);
            var wrongGame = new BoardRoomSession(BoardProtocol.GameXiangqi, "走错门的象棋玩家");
            try
            {
                var error = await wrongGame.ConnectAsync("127.0.0.1", host.ListenPort).ConfigureAwait(false);
                check("[连接失败] 游戏类型对不上：握手失败并给出中文原因（含「游戏类型不一致」）",
                    error.Length > 0 && error.Contains("游戏类型不一致"), error);
                check("[连接失败] 走错门的连接不会占住房间（主机仍可被正常连接）",
                    host.MemberCount == 0 && host.IsOnline, host.MemberCount.ToString());
            }
            finally
            {
                await SafeStopAsync(wrongGame).ConfigureAwait(false);
            }

            // 旧版客户端（协议 v1）：原始 TCP 发 hello，应收到版本不一致的 notice + bye
            using var rawClient = new TcpClient();
            await rawClient.ConnectAsync(IPAddress.Loopback, host.ListenPort).ConfigureAwait(false);
            using var stream = rawClient.GetStream();
            var oldHello = RoomFrame.New("hello", "旧版客户端", new HelloInfo
            {
                Magic = BoardProtocol.Magic,
                GameId = BoardProtocol.GameGomoku,
                PlayerName = "旧版客户端",
                ProtocolVersion = 1,
            });
            await stream.WriteAsync(RoomProtocol.BuildFrame(oldHello)).ConfigureAwait(false);
            var notices = new List<string>();
            var byes = new List<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            try
            {
                while (notices.Count == 0 || byes.Count == 0)
                {
                    var (frame, _) = await ReadRawFrameAsync(stream, cts.Token).ConfigureAwait(false);
                    if (frame is null) break;
                    if (frame.Kind == BoardProtocol.Kinds.Notice)
                        notices.Add(frame.ExtraAs<NoticeInfo>()?.Text ?? "");
                    else if (frame.Kind == BoardProtocol.Kinds.Bye)
                        byes.Add(frame.ExtraAs<ByeInfo>()?.Reason ?? "");
                }
            }
            catch (OperationCanceledException)
            {
                // 收不齐也不抛到上层，下面按实际收到的断言
            }

            check("[连接失败] 旧版客户端（v1）被拒：收到「协议版本不一致」的提示与 bye",
                notices.Any(n => n.Contains("协议版本不一致")) && byes.Any(b => b.Contains("协议版本不一致")),
                $"notice={string.Join("|", notices)} bye={string.Join("|", byes)}");
            check("[连接失败] 旧版客户端被拒后主机没有登记成员",
                host.MemberCount == 0 && host.IsOnline, host.MemberCount.ToString());
        }
        finally
        {
            await SafeStopAsync(host).ConfigureAwait(false);
        }
    }

    // ---------- H. 房间 ViewModel 级（真回环）：房主 + 2 玩家 + 1 观战 → 席位 / 观战 / 自动轮换 / 暂停重连 ----------

    private static async Task RunRoomViewModelAsync(Action<string, bool, string> check, List<string> report)
    {
        // 前面的配置断言改过临时目录里的 gomoku 配置（HostSide / 自动轮换等），
        // VM 级测试先恢复默认语义，避免断言被前一组测试污染
        var roomCfg = ToolConfigStore.For("gomoku").Load<GomokuConfig>();
        roomCfg.HostSide = "black";
        roomCfg.AutoRotateMode = "manual";
        roomCfg.AutoPickMode = "rotation";
        ToolConfigStore.FlushAll();

        var hostVm = new SelfCheckBoardVm { PlayerName = "房主VM" };
        var playerA = new SelfCheckBoardVm();
        var playerB = new SelfCheckBoardVm();
        var watcher = new SelfCheckBoardVm();
        var hostChats = new ConcurrentQueue<string>();
        var playerAChats = new ConcurrentQueue<string>();
        var playerBChats = new ConcurrentQueue<string>();
        TrackChats(hostVm, hostChats);
        TrackChats(playerA, playerAChats);
        TrackChats(playerB, playerBChats);
        TrackChats(watcher, new ConcurrentQueue<string>());

        BoardSeatOption? Option(ObservableCollection<BoardSeatOption> choices, string memberId)
            => choices.FirstOrDefault(o => o.MemberId == memberId);

        int GamesOf(string memberId)
            => SnapshotMembers(hostVm).FirstOrDefault(m => m.MemberId == memberId)?.GamesPlayed ?? -1;

        try
        {
            // ===== 离线态 =====
            check("[联机VM] 新开界面：离线、可以开房/加入、成员 0 人、没有房主控制区",
                hostVm.Mode == BoardPlayMode.Offline && !hostVm.IsOnline && hostVm.CanJoin
                && hostVm.ShowJoinControls && hostVm.MemberCountText == "0 / 16 人"
                && !hostVm.HostControlsVisible && !hostVm.ShowStartNextGameButton
                && !hostVm.IsSeatedPlayer && !hostVm.IsSpectator && !hostVm.CanPlayNow
                && hostVm.MyRoleText == "未进房间", hostVm.ModeBadgeText);
            check("[联机VM] 离线状态文案：徽标「○ 未连接」、连接摘要「未连接」、没有操作按钮",
                hostVm.ModeBadgeText == "○ 未连接"
                && hostVm.ConnectionSummary.Contains("未连接")
                && hostVm.LocalSide == "" && !hostVm.ShowPlayerActions, hostVm.ConnectionSummary);
            check("[联机VM] 席位边文案在离线时已就绪（先手位黑棋 / 后手位白棋）",
                hostVm.SeatOneSideText == "先手位（黑棋）" && hostVm.SeatTwoSideText == "后手位（白棋）", "");
            check("[联机VM] 离线时自动轮换说明有默认文案（手动安排）",
                hostVm.AutoRotateHintText.Contains("手动安排") && !hostVm.AutoRotateEnabled
                && hostVm.SelectedAutoRotateMode.Value == "manual"
                && hostVm.SelectedAutoPickMode.Value == "rotation", hostVm.AutoRotateHintText);

            // ===== 房主开房：默认坐先手位 =====
            hostVm.ListenPortText = "0";
            hostVm.StartHostCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsOnline && hostVm.IsHost, 12000).ConfigureAwait(false);
            check("[联机VM] 房主开房成功：Mode=Host、在线、有房主控制区",
                hostVm.IsOnline && hostVm.IsHost && hostVm.Mode == BoardPlayMode.Host
                && hostVm.HostControlsVisible && hostVm.CanEditSeats && hostVm.ShowStartNextGameButton,
                hostVm.NoticeText);
            var portMatch = Regex.Match(hostVm.ConnectionSummary, @"真实端口 (\d+)");
            var port = portMatch.Success ? int.Parse(portMatch.Groups[1].Value) : 0;
            check("[联机VM] 连接摘要能解析出真实端口（端口 0=系统分配）", port > 0, hostVm.ConnectionSummary);
            var hostMembers = SnapshotMembers(hostVm);
            check("[联机VM] 房主入成员表：1 人、房主徽标、默认坐先手位",
                hostMembers.Count == 1 && hostMembers[0].IsHost
                && hostMembers[0].Seat == "first" && hostMembers[0].SeatText == "先手位"
                && hostMembers[0].RoleText == "房主" && hostMembers[0].NameText.Contains("房主VM"),
                hostMembers.Count == 0 ? "(空)" : hostMembers[0].ToString());
            check("[联机VM] 房主默认执先手（黑棋）：席位 / 身份 / 操作按钮文案正确",
                hostVm.LocalSide == "black" && hostVm.IsSeatedPlayer && !hostVm.IsSpectator
                && hostVm.MyRoleText.Contains("房主") && hostVm.MyRoleText.Contains("先手位")
                && hostVm.SeatOneSideText.Contains("黑棋") && hostVm.SeatTwoSideText.Contains("白棋")
                && hostVm.SeatOneName == "房主VM" && hostVm.SeatTwoName == "空位"
                && hostVm.SeatOneOnline && !hostVm.SeatTwoOnline
                && hostVm.SeatOneOnlineText == "在线" && hostVm.SeatTwoOnlineText == "空位"
                && hostVm.ShowPlayerActions, $"{hostVm.MyRoleText} / {hostVm.SeatStatusText}");
            check("[联机VM] 两个席位没坐满时不能开始、开始按钮文案是「开始对局」",
                !hostVm.CanStartNextGame && !hostVm.StartNextGameCommand.CanExecute(null)
                && hostVm.StartNextGameText == "开始对局", hostVm.NoticeText);
            check("[联机VM] 席位下拉：空位 + 房主自己两个选项",
                hostVm.SeatOneChoices.Count == 2 && hostVm.SeatTwoChoices.Count == 2
                && hostVm.SeatOneChoices[0].IsEmpty && hostVm.SeatOneChoices.Any(o => o.MemberId == "host"),
                string.Join(",", hostVm.SeatTwoChoices.Select(o => o.DisplayName)));

            // ===== 两个客户端加入（默认观战） =====
            playerA.PlayerName = "玩家A";
            playerA.JoinAddress = $"127.0.0.1:{port}";
            playerA.JoinRoomCommand.Execute(null);
            await WaitUntilAsync(() => playerA.IsOnline && playerA.IsClient, 12000).ConfigureAwait(false);
            check("[联机VM] 玩家A加入：客户端模式、默认观战（不坐席位）",
                playerA.IsOnline && playerA.IsClient && playerA.IsSpectator
                && !playerA.IsSeatedPlayer && playerA.LocalSide == "" && !playerA.ShowPlayerActions
                && playerA.MyRoleText == "观战中"
                && playerA.SeatStatusText.Contains("观战")
                && playerA.MemberCountText == "2 / 16 人", $"{playerA.MyRoleText} / {playerA.SeatStatusText}");
            check("[联机VM] 客户端的成员行：不是房主、本端行标记「你」",
                !playerA.IsHost && playerA.LocalMemberId.Length > 0
                && SnapshotMembers(playerA).FirstOrDefault(m => m.MemberId == playerA.LocalMemberId)?.IsSelf == true,
                playerA.LocalMemberId);
            await WaitUntilAsync(() => SnapshotMembers(hostVm).Count == 2, 8000).ConfigureAwait(false);
            check("[联机VM] 房主侧成员表出现玩家A、聊天有加入提示",
                SnapshotMembers(hostVm).Count == 2
                && SnapshotMembers(hostVm).Any(m => m.Name.Contains("玩家A") && !m.IsSeated)
                && hostChats.Any(t => t.Contains("玩家A") && t.Contains("加入了房间")),
                string.Join(" | ", hostChats.Take(5)));
            check("[联机VM] 房主的成员行标着「你」（本端标记在成员同步后刷新）",
                SnapshotMembers(hostVm).FirstOrDefault(m => m.IsHost)?.IsSelf == true, "");

            playerB.PlayerName = "玩家B";
            playerB.JoinAddress = $"127.0.0.1:{port}";
            playerB.JoinRoomCommand.Execute(null);
            await WaitUntilAsync(() => playerB.IsOnline && playerB.IsClient, 12000).ConfigureAwait(false);
            await WaitUntilAsync(() => SnapshotMembers(hostVm).Count == 3, 8000).ConfigureAwait(false);
            check("[联机VM] 玩家B加入后房主成员表 3 人、B 也是观战",
                SnapshotMembers(hostVm).Count == 3 && playerB.IsSpectator
                && hostVm.MemberCountText == "3 / 16 人", hostVm.MemberCountText);
            watcher.PlayerName = "观战者";
            watcher.JoinAddress = $"127.0.0.1:{port}";
            watcher.JoinRoomCommand.Execute(null);
            await WaitUntilAsync(() => watcher.IsOnline && watcher.IsClient, 12000).ConfigureAwait(false);
            await WaitUntilAsync(() => SnapshotMembers(hostVm).Count == 4, 8000).ConfigureAwait(false);
            check("[联机VM] 第 4 个客户端（观战者）加入：成员 4/16、仍然可以进",
                SnapshotMembers(hostVm).Count == 4 && watcher.IsSpectator
                && hostVm.MemberCountText == "4 / 16 人", hostVm.MemberCountText);
            check("[联机VM] 客户端没有房主控制区、不能改席位 / 开始 / 作废",
                !playerA.HostControlsVisible && !playerA.CanEditSeats
                && !playerA.SwapSeatsCommand.CanExecute(null) && !playerA.ClearSeatsCommand.CanExecute(null)
                && !playerA.StartNextGameCommand.CanExecute(null) && !playerA.AbortGameCommand.CanExecute(null),
                "");
            check("[联机VM] 成员列表按进房顺序排（房主 0 → A 1 → B 2 → 观战者 3）",
                SnapshotMembers(hostVm).Select(m => m.JoinOrder).SequenceEqual(new[] { 0, 1, 2, 3 }),
                string.Join(",", SnapshotMembers(hostVm).Select(m => $"{m.NameText}:{m.JoinOrder}")));
            check("[联机VM] 观战者的身份文案：观战中 + 观战提示（等房主安排）",
                watcher.MyRoleText == "观战中" && watcher.SpectatorHintText.Contains("观战")
                && watcher.SeatStatusText.Contains("观战"), $"{watcher.MyRoleText} / {watcher.SeatStatusText}");
            check("[联机VM] 客户端徽标 / 连接摘要显示「观战中」与房间人数",
                playerA.ModeBadgeText.Contains("观战") && playerA.ConnectionSummary.Contains("房间 4 / 16 人")
                && playerA.CanLeaveRoom && !playerA.ShowJoinControls,
                $"{playerA.ModeBadgeText} / {playerA.ConnectionSummary}");

            // ===== 房间聊天（多人）：广播 + 发送者本地只显示一次 =====
            hostVm.ChatText = "房主说话";
            hostVm.SendChatCommand.Execute(null);
            await WaitUntilAsync(() => playerAChats.Any(t => t == "房主说话"), 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => playerBChats.Any(t => t == "房主说话"), 8000).ConfigureAwait(false);
            check("[联机VM] 房间聊天：房主发话所有客户端都收到",
                playerAChats.Any(t => t == "房主说话") && playerBChats.Any(t => t == "房主说话"), "");
            playerA.ChatText = "A说话";
            playerA.SendChatCommand.Execute(null);
            await WaitUntilAsync(() => hostChats.Any(t => t == "A说话"), 8000).ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
            check("[联机VM] 房间聊天：客户端发话主机收到、发送者本地只显示一次（主机不回环）",
                hostChats.Any(t => t == "A说话") && playerAChats.Count(t => t == "A说话") == 1,
                string.Join(" | ", playerAChats));
            check("[联机VM] 房间聊天：另一个客户端也收到转发",
                playerBChats.Count(t => t == "A说话") == 1, string.Join(" | ", playerBChats));
            watcher.ChatText = "观战者说话";
            watcher.SendChatCommand.Execute(null);
            await WaitUntilAsync(() => hostChats.Any(t => t == "观战者说话"), 8000).ConfigureAwait(false);
            check("[联机VM] 观战者也能聊天（只是不能走子）",
                hostChats.Any(t => t == "观战者说话"), "");

            // ===== 房主安排席位 =====
            var aId = playerA.LocalMemberId;
            var bId = playerB.LocalMemberId;
            var wId = watcher.LocalMemberId;
            check("[联机VM] 四个人的成员 id：房主 host、三个客户端各不相同",
                hostVm.LocalMemberId == "host"
                && new[] { aId, bId, wId }.All(id => id.Length > 0 && id != "host")
                && new[] { aId, bId, wId }.Distinct().Count() == 3, $"{aId},{bId},{wId}");

            hostVm.SelectedSeatTwoOption = Option(hostVm.SeatTwoChoices, aId);
            await WaitUntilAsync(() => hostVm.SeatTwoName == "玩家A" && playerA.IsSeatedPlayer, 8000)
                .ConfigureAwait(false);
            check("[联机VM] 房主把后手位安排给玩家A：两席坐满、A 变成选手执白",
                hostVm.SeatTwoName == "玩家A" && hostVm.SeatTwoOnline
                && playerA.IsSeatedPlayer && !playerA.IsSpectator
                && playerA.LocalSide == "white" && playerA.MyRoleText.Contains("选手")
                && playerA.ShowPlayerActions, $"{playerA.MyRoleText}");
            await WaitUntilAsync(() => playerAChats.Any(t => t.Contains("房主把后手位")), 8000).ConfigureAwait(false);
            check("[联机VM] 房主安排席位会在聊天里通知所有人（客户端收到系统提示）",
                playerAChats.Any(t => t.Contains("房主把后手位") && t.Contains("玩家A")),
                string.Join(" | ", playerAChats.Where(t => t.Contains("房主")).Take(4)));
            check("[联机VM] 两席坐满且在线 → 房主可以开始对局",
                hostVm.CanStartNextGame && hostVm.StartNextGameCommand.CanExecute(null),
                hostVm.NoticeText);
            check("[联机VM] 玩家B/观战者仍然是观战（不能下）",
                playerB.IsSpectator && watcher.IsSpectator
                && !playerB.ShowPlayerActions && !watcher.ShowPlayerActions, "");
            check("[联机VM] 观战者不能开始下一局（没有房主权限）",
                !watcher.CanStartNextGame && !watcher.StartNextGameCommand.CanExecute(null), "");

            SwapSeats(hostVm);
            await WaitUntilAsync(() => hostVm.LocalSide == "white" && playerA.LocalSide == "black", 8000)
                .ConfigureAwait(false);
            check("[联机VM] 交换两席：房主改坐后手位（白）、玩家A改坐先手位（黑）",
                hostVm.LocalSide == "white" && playerA.LocalSide == "black"
                && hostVm.SeatOneName == "玩家A" && hostVm.SeatTwoName == "房主VM"
                && hostVm.MyRoleText.Contains("后手位"), $"{hostVm.MyRoleText}");

            // 席位下拉 setter 语义：选中「另一席的人」= 交换；选「空位」= 清空该席
            hostVm.SelectedSeatTwoOption = Option(hostVm.SeatTwoChoices, aId);
            await WaitUntilAsync(() => hostVm.SeatOneName == "房主VM" && hostVm.SeatTwoName == "玩家A", 8000)
                .ConfigureAwait(false);
            check("[联机VM] 席位下拉选中「另一席的人」= 两席交换",
                hostVm.SeatOneName == "房主VM" && hostVm.SeatTwoName == "玩家A",
                $"{hostVm.SeatOneName}/{hostVm.SeatTwoName}");
            hostVm.SelectedSeatOneOption = BoardSeatOption.Empty();
            await WaitUntilAsync(() => hostVm.SeatOneName == "空位", 8000).ConfigureAwait(false);
            check("[联机VM] 席位下拉选「空位」= 清空该席、不影响另一席、也不能开始",
                hostVm.SeatOneName == "空位" && hostVm.SeatTwoName == "玩家A"
                && !hostVm.CanStartNextGame, $"{hostVm.SeatOneName}/{hostVm.SeatTwoName}");
            hostVm.SelectedSeatOneOption = Option(hostVm.SeatOneChoices, "host");
            await WaitUntilAsync(() => hostVm.SeatOneName == "房主VM", 8000).ConfigureAwait(false);

            hostVm.ClearSeatsCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.SeatOneName == "空位" && hostVm.SeatTwoName == "空位", 8000)
                .ConfigureAwait(false);
            await WaitUntilAsync(() => playerA.IsSpectator, 8000).ConfigureAwait(false);
            check("[联机VM] 清空两席：都变空位、房主不能开始、两个客户端变回观战",
                hostVm.SeatOneName == "空位" && hostVm.SeatTwoName == "空位"
                && !hostVm.CanStartNextGame && playerA.IsSpectator, hostVm.NoticeText);
            check("[联机VM] 空席位时点「开始对局」不会开局（提示两席都要有人）",
                !hostVm.IsGameRunning, hostVm.NoticeText);

            // 固定成「房主先手 vs 玩家A后手」，开始第 1 局
            hostVm.SelectedSeatOneOption = Option(hostVm.SeatOneChoices, "host");
            hostVm.SelectedSeatTwoOption = Option(hostVm.SeatTwoChoices, aId);
            await WaitUntilAsync(() => hostVm.SeatTwoName == "玩家A" && playerA.LocalSide == "white", 8000)
                .ConfigureAwait(false);

            // ===== 自动轮换设置：房主改 = 存配置 + 广播 =====
            hostVm.SelectedAutoRotateMode =
                hostVm.AutoRotateModeOptions.First(o => o.Value == "manual");
            await WaitUntilAsync(() => playerA.SelectedAutoRotateMode.Value == "manual", 5000)
                .ConfigureAwait(false);
            check("[联机VM] 自动上场设置会广播给客户端（manual）",
                playerA.SelectedAutoRotateMode.Value == "manual"
                && !hostVm.AutoRotateEnabled, playerA.SelectedAutoRotateMode.Label);

            // ===== 第 1 局：房主 vs 玩家A（房主获胜） =====
            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => playerA.Game.Started && playerA.Game.MoveCount == 0, 8000)
                .ConfigureAwait(false);
            check("[联机VM] 房主开始对局：两边都进入进行中、棋盘可以按轮次下",
                hostVm.IsGameRunning && playerA.Game.Started
                && hostVm.StartNextGameText == "开始下一局"
                && !hostVm.CanEditSeats && !hostVm.CanStartNextGame
                && hostVm.CanPlayNow && !playerA.CanPlayNow
                && hostVm.SeatStatusText.Contains("对局进行中"),
                $"{hostVm.SeatStatusText} / 轮到 {hostVm.Game.Turn}");
            check("[联机VM] 观战者收到棋盘但只读（不能下、没有玩家操作按钮）",
                watcher.IsSpectator && watcher.Game.MoveCount == 0
                && !watcher.CanPlayNow && !watcher.IsBoardInteractive && !watcher.ShowPlayerActions, "");

            // 对局中房主改席位无效
            hostVm.SelectedSeatTwoOption = Option(hostVm.SeatTwoChoices, bId);
            await Task.Delay(150).ConfigureAwait(false);
            check("[联机VM] 对局进行中席位锁定：房主选别人也不会换（B 仍是观战）",
                hostVm.SeatTwoName == "玩家A" && playerB.IsSpectator, hostVm.SeatTwoName);
            check("[联机VM] 开局后进行中：客户端徽标变「对局中」、房主自己仍是房主",
                playerA.ModeBadgeText.Contains("对局中") && hostVm.ModeBadgeText.Contains("房间"),
                $"{hostVm.ModeBadgeText} / {playerA.ModeBadgeText}");
            check("[联机VM] 对局进行中：客户端看到的状态也是进行中、席位文案是「你在后手位…」",
                playerA.IsGameRunning && playerA.SeatStatusText.Contains("你在后手位")
                && !playerA.CanEditSeats, playerA.SeatStatusText);
            check("[联机VM] 对局进行中房主能作废本局（按钮文案就绪）、但不能开始下一局",
                hostVm.CanAbortGame && hostVm.AbortGameText == "结束本局（作废）"
                && !hostVm.CanStartNextGame, hostVm.AbortGameText);

            // 观战者的三个请求都该被回绝（不得产生待确认）
            watcher.RequestUndo();
            watcher.OfferDraw();
            watcher.ResignOnline();
            await Task.Delay(250).ConfigureAwait(false);
            check("[联机VM] 观战者请求悔棋 / 求和 / 认输都被回绝（不弹确认、不等待回应）",
                !watcher.HasPendingWait && !hostVm.HasPendingPrompt
                && watcher.NoticeText.Contains("观战"), watcher.NoticeText);
            check("[联机VM] 观战者棋盘只读（IsBoardInteractive / ShowPlayerActions 均为 false）",
                !watcher.IsBoardInteractive && !watcher.ShowPlayerActions, "");

            // 观战者发动作：主机忽略
            watcher.ClientPlay(10, 10);
            await WaitUntilAsync(() => hostVm.NoticeText.Contains("观战者不能走子"), 8000).ConfigureAwait(false);
            check("[联机VM] 观战者尝试走子：主机拒绝并给出提示（棋局不动）",
                hostVm.NoticeText.Contains("观战者不能走子") && hostVm.Game.MoveCount == 0,
                hostVm.NoticeText);

            async Task HostPlayAsync(int x, int y)
            {
                var target = hostVm.Game.MoveCount + 1;
                hostVm.HostPlay(x, y);
                await WaitUntilAsync(() => hostVm.Game.MoveCount == target, 5000).ConfigureAwait(false);
            }

            async Task WhitePlayAsync(int x, int y)
            {
                var target = hostVm.Game.MoveCount + 1;
                playerA.ClientPlay(x, y);
                await WaitUntilAsync(() => hostVm.Game.MoveCount == target, 8000).ConfigureAwait(false);
                await WaitUntilAsync(() => playerA.Game.MoveCount == target, 8000).ConfigureAwait(false);
            }

            hostVm.HostPlay(0, 0);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 1 && playerA.Game.MoveCount == 1, 8000)
                .ConfigureAwait(false);
            check("[联机VM] 房主落子后客户端收到（观战者也收到）",
                hostVm.Game.MoveCount == 1 && playerA.Game.MoveCount == 1 && watcher.Game.MoveCount == 1,
                $"{hostVm.Game.MoveCount}/{playerA.Game.MoveCount}/{watcher.Game.MoveCount}");
            await WhitePlayAsync(7, 7).ConfigureAwait(false);
            await HostPlayAsync(1, 0).ConfigureAwait(false);
            await WhitePlayAsync(7, 8).ConfigureAwait(false);
            await HostPlayAsync(2, 0).ConfigureAwait(false);
            await WhitePlayAsync(7, 9).ConfigureAwait(false);
            await HostPlayAsync(3, 0).ConfigureAwait(false);
            await WhitePlayAsync(8, 7).ConfigureAwait(false);
            hostVm.HostPlay(4, 0);
            await WaitUntilAsync(() => !hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => playerA.Game.Result == GomokuResult.BlackWin, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => GamesOf("host") == 1 && GamesOf(aId) == 1, 8000).ConfigureAwait(false);
            check("[联机VM] 第 1 局结束：房主黑胜、客户端按状态包看到结果",
                hostVm.Game.Result == GomokuResult.BlackWin && playerA.Game.Result == GomokuResult.BlackWin
                && watcher.Game.Result == GomokuResult.BlackWin,
                $"{hostVm.Game.ResultText} / {playerA.Game.ResultText}");
            check("[联机VM] 第 1 局结束：两人各记 1 场、提示房主安排下一局（手动安排不自动换人）",
                SnapshotMembers(hostVm).First(m => m.MemberId == "host").GamesPlayed == 1
                && SnapshotMembers(hostVm).First(m => m.MemberId == aId).GamesPlayed == 1
                && SnapshotMembers(hostVm).First(m => m.MemberId == bId).GamesPlayed == 0
                && hostVm.SeatTwoName == "玩家A"
                && hostVm.NoticeText.Contains("本局结束"), $"{hostVm.NoticeText}");
            check("[联机VM] 成员行显示上场次数文案（已打 1 局 / 已打 0 局）",
                SnapshotMembers(hostVm).First(m => m.MemberId == "host").GamesPlayedText == "已打 1 局"
                && SnapshotMembers(hostVm).First(m => m.MemberId == bId).GamesPlayedText == "已打 0 局", "");
            check("[联机VM] 对局结束后席位解锁、房主可以开始下一局",
                hostVm.CanEditSeats && hostVm.CanStartNextGame && hostVm.StartNextGameText == "开始下一局", "");
            check("[联机VM] 对局结束后「结束本局（作废）」不可用（已经结束了）",
                !hostVm.CanAbortGame && hostVm.ShowStartNextGameButton, hostVm.AbortGameText);

            // ===== 输的下场 + 轮流上场：第 2 局房主再胜 → 玩家A 下场、玩家B 上场（0 场优先） =====
            hostVm.SelectedAutoRotateMode =
                hostVm.AutoRotateModeOptions.First(o => o.Value == "loserOut");
            hostVm.SelectedAutoPickMode =
                hostVm.AutoPickModeOptions.First(o => o.Value == "rotation");
            await WaitUntilAsync(
                () => playerA.SelectedAutoRotateMode.Value == "loserOut"
                      && playerA.SelectedAutoPickMode.Value == "rotation", 8000).ConfigureAwait(false);
            check("[联机VM] 自动轮换改成「输的下场 + 轮流上场」并广播给客户端",
                hostVm.AutoRotateEnabled && hostVm.Cfg.AutoRotateMode == "loserOut"
                && hostVm.Cfg.AutoPickMode == "rotation"
                && playerA.SelectedAutoRotateMode.Value == "loserOut"
                && hostVm.AutoRotateHintText.Contains("输的下场"), hostVm.AutoRotateHintText);

            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => playerA.Game.Result == GomokuResult.Playing && playerA.Game.MoveCount == 0, 8000)
                .ConfigureAwait(false);
            hostVm.HostPlay(0, 0);
            await WhitePlayAsync(7, 7).ConfigureAwait(false);
            await HostPlayAsync(1, 0).ConfigureAwait(false);
            await WhitePlayAsync(7, 8).ConfigureAwait(false);
            await HostPlayAsync(2, 0).ConfigureAwait(false);
            await WhitePlayAsync(7, 9).ConfigureAwait(false);
            await HostPlayAsync(3, 0).ConfigureAwait(false);
            await WhitePlayAsync(8, 7).ConfigureAwait(false);
            hostVm.HostPlay(4, 0);
            await WaitUntilAsync(() => !hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(
                () => hostVm.SeatTwoName == "玩家B" && playerB.IsSeatedPlayer, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => GamesOf("host") == 2 && GamesOf(aId) == 2, 8000).ConfigureAwait(false);
            check("[联机VM] 输的下场：赢家（房主）留场、输家A 下场，0 场的 B 按进房顺序补位",
                hostVm.SeatOneName == "房主VM" && hostVm.SeatTwoName == "玩家B"
                && playerB.IsSeatedPlayer && playerB.LocalSide == "white"
                && playerA.IsSpectator && playerA.SeatStatusText.Contains("观战")
                && hostVm.NoticeText.Contains("自动安排下一局"), hostVm.NoticeText);
            check("[联机VM] 输的下场：上场次数正确（房主 2 / A 2 / B 0）",
                SnapshotMembers(hostVm).First(m => m.MemberId == "host").GamesPlayed == 2
                && SnapshotMembers(hostVm).First(m => m.MemberId == aId).GamesPlayed == 2
                && SnapshotMembers(hostVm).First(m => m.MemberId == bId).GamesPlayed == 0, "");

            // 第 3 局：房主 vs 玩家B，房主再胜 → B 下场；候选里 W 0 场 < A 2 场 → W 补位
            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            hostVm.HostPlay(0, 0);
            playerB.ClientPlay(7, 7);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 2, 8000).ConfigureAwait(false);
            await HostPlayAsync(1, 0).ConfigureAwait(false);
            playerB.ClientPlay(7, 8);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 4, 8000).ConfigureAwait(false);
            await HostPlayAsync(2, 0).ConfigureAwait(false);
            playerB.ClientPlay(7, 9);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 6, 8000).ConfigureAwait(false);
            await HostPlayAsync(3, 0).ConfigureAwait(false);
            playerB.ClientPlay(8, 7);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 8, 8000).ConfigureAwait(false);
            hostVm.HostPlay(4, 0);
            await WaitUntilAsync(() => !hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => hostVm.SeatTwoName == "观战者", 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => watcher.IsSeatedPlayer && playerB.IsSpectator, 8000).ConfigureAwait(false);
            check("[联机VM] 轮流补位：0 场的观战者上场（房主 vs 观战者），B 下场",
                hostVm.SeatOneName == "房主VM" && hostVm.SeatTwoName == "观战者"
                && watcher.IsSeatedPlayer && playerB.IsSpectator, hostVm.SeatTwoName);

            // ===== 双方都下场：第 4 局房主再胜 → 两人都换（B 1 场 < A 2 场 → B 先手、A 后手） =====
            hostVm.SelectedAutoRotateMode = hostVm.AutoRotateModeOptions.First(o => o.Value == "bothOut");
            await WaitUntilAsync(() => playerA.SelectedAutoRotateMode.Value == "bothOut", 8000).ConfigureAwait(false);
            check("[联机VM] 自动轮换下拉：3 种上场方式 / 2 种补位方式、提示文案随模式变化",
                hostVm.AutoRotateModeOptions.Count == 3 && hostVm.AutoPickModeOptions.Count == 2
                && hostVm.AutoRotateModeOptions.Any(o => o.Label.Contains("输的下场"))
                && hostVm.AutoPickModeOptions.Any(o => o.Label.Contains("随机"))
                && hostVm.AutoRotateHintText.Contains("双方都下场"), hostVm.AutoRotateHintText);
            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => watcher.CanPlayNow || watcher.Game.Started, 8000).ConfigureAwait(false);
            hostVm.HostPlay(0, 0);
            watcher.ClientPlay(7, 7);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 2, 8000).ConfigureAwait(false);
            await HostPlayAsync(1, 0).ConfigureAwait(false);
            watcher.ClientPlay(7, 8);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 4, 8000).ConfigureAwait(false);
            await HostPlayAsync(2, 0).ConfigureAwait(false);
            watcher.ClientPlay(7, 9);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 6, 8000).ConfigureAwait(false);
            await HostPlayAsync(3, 0).ConfigureAwait(false);
            watcher.ClientPlay(8, 7);
            await WaitUntilAsync(() => hostVm.Game.MoveCount == 8, 8000).ConfigureAwait(false);
            hostVm.HostPlay(4, 0);
            await WaitUntilAsync(() => !hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => hostVm.SeatOneName == "玩家B" && hostVm.SeatTwoName == "玩家A", 8000)
                .ConfigureAwait(false);
            await WaitUntilAsync(() => playerB.IsSeatedPlayer && playerA.IsSeatedPlayer, 8000).ConfigureAwait(false);
            check("[联机VM] 双方都下场：房主与观战者都下、B/A 按场次与进房顺序补上（B 先手、A 后手）",
                hostVm.SeatOneName == "玩家B" && hostVm.SeatTwoName == "玩家A"
                && playerB.IsSeatedPlayer && playerA.IsSeatedPlayer && hostVm.IsSpectator
                && hostVm.MyRoleText.Contains("房主") && hostVm.MyRoleText.Contains("观战"),
                $"{hostVm.SeatOneName}/{hostVm.SeatTwoName} / {hostVm.MyRoleText}");

            // ===== 房主不下场也能开始 / 作废本局 =====
            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            check("[联机VM] 房主不坐席位也能开始下一局（裁判视角）",
                hostVm.IsGameRunning && hostVm.IsSpectator && !hostVm.CanPlayNow, hostVm.SeatStatusText);
            hostVm.AbortGameCommand.Execute(null);
            check("[联机VM] 作废本局：第一次点进入二次确认（文案变化、尚未作废）",
                hostVm.IsAbortConfirming && hostVm.AbortGameText == "再点一次确认作废本局"
                && hostVm.IsGameRunning, hostVm.AbortGameText);
            hostVm.AbortGameCommand.Execute(null);
            await WaitUntilAsync(() => !hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => GamesOf(bId) == 1 && GamesOf(aId) == 2, 8000).ConfigureAwait(false);
            check("[联机VM] 再点一次才真的作废：对局回到未开始、席位与上场次数不变",
                !hostVm.IsGameRunning && !hostVm.IsAbortConfirming
                && hostVm.AbortGameText == "结束本局（作废）"
                && hostVm.SeatOneName == "玩家B" && hostVm.SeatTwoName == "玩家A"
                && hostVm.NoticeText.Contains("作废"), hostVm.NoticeText);
            check("[联机VM] 作废不算上场（B/A 的上场次数没有因为作废 +1）",
                SnapshotMembers(hostVm).First(m => m.MemberId == bId).GamesPlayed == 1
                && SnapshotMembers(hostVm).First(m => m.MemberId == aId).GamesPlayed == 2, "");

            // ===== 作废二次确认的自动取消（3 秒内不点第二下 → 取消） =====
            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            hostVm.AbortGameCommand.Execute(null);
            check("[联机VM] 作废第一次点：进入二次确认、对局仍在跑",
                hostVm.IsAbortConfirming && hostVm.IsGameRunning, hostVm.AbortGameText);
            await Task.Delay(3400).ConfigureAwait(false);
            check("[联机VM] 3 秒内没再点 → 作废确认自动取消、对局继续",
                !hostVm.IsAbortConfirming && hostVm.IsGameRunning, hostVm.NoticeText);
            hostVm.AbortGameCommand.Execute(null);
            hostVm.AbortGameCommand.Execute(null);
            await WaitUntilAsync(() => !hostVm.IsGameRunning, 8000).ConfigureAwait(false);

            // ===== 玩家掉线暂停 → 自动重连恢复 =====
            hostVm.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostVm.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => playerB.Game.Started, 8000).ConfigureAwait(false);
            var movesBeforeCut = hostVm.Game.MoveCount;
            playerB.CurrentSession!.AbortConnection("模拟断网");
            await WaitUntilAsync(() => hostVm.IsGamePaused, 10000).ConfigureAwait(false);
            check("[联机VM] 席上玩家掉线：主机侧对局暂停、提示可读",
                hostVm.IsGamePaused && hostVm.GamePauseStatusText.Contains("对局暂停")
                && !hostVm.SeatOneOnline && hostVm.NoticeText.Contains("掉线"), $"{hostVm.GamePauseStatusText}");
            await WaitUntilAsync(() => playerA.IsGamePaused, 8000).ConfigureAwait(false);
            check("[联机VM] 另一个席上玩家也看到「对局暂停」（所有人棋盘只读）",
                playerA.IsGamePaused && playerA.GamePauseStatusText.Contains("对局暂停")
                && !playerA.CanPlayNow, playerA.GamePauseStatusText);
            await WaitUntilAsync(() => playerB.IsReconnecting, 8000).ConfigureAwait(false);
            check("[联机VM] 掉线客户端进入自动重连：棋盘只读、不能重复加入、按钮变「停止重连」",
                playerB.IsReconnecting && playerB.IsGamePaused
                && !playerB.CanPlayNow && !playerB.CanJoin
                && !playerB.JoinRoomCommand.CanExecute(null)
                && playerB.LeaveRoomText == "停止重连", $"{playerB.ModeBadgeText} / {playerB.ReconnectStatusText}");
            await WaitUntilAsync(() => playerB.IsOnline && !playerB.IsReconnecting, 20000).ConfigureAwait(false);
            await WaitUntilAsync(() => !hostVm.IsGamePaused, 10000).ConfigureAwait(false);
            check("[联机VM] 同 id 自动重连后：对局恢复、棋局不丢（手数一致）",
                playerB.IsOnline && !playerB.IsGamePaused && !hostVm.IsGamePaused
                && hostVm.Game.MoveCount == movesBeforeCut && playerB.Game.MoveCount == movesBeforeCut
                && hostVm.SeatTwoOnline && playerB.IsSeatedPlayer, $"{movesBeforeCut} → {hostVm.Game.MoveCount}");
            check("[联机VM] 重连沿用原成员 id / 原席位（房主成员表顺序不变）",
                playerB.LocalMemberId == bId
                && SnapshotMembers(hostVm).Any(m => m.MemberId == bId && m.IsOnline)
                && SnapshotMembers(hostVm).Select(m => m.JoinOrder).SequenceEqual(new[] { 0, 1, 2, 3 }),
                playerB.LocalMemberId);

            // ===== 观战者掉线不暂停（重连后仍然观战） =====
            watcher.CurrentSession!.AbortConnection("观战断网");
            await WaitUntilAsync(
                () => SnapshotMembers(hostVm).Any(m => m.MemberId == wId && !m.IsOnline), 8000)
                .ConfigureAwait(false);
            await Task.Delay(300).ConfigureAwait(false);
            check("[联机VM] 观战者掉线：主机不暂停（只有席上玩家掉线才暂停）",
                !hostVm.IsGamePaused && hostVm.IsGameRunning, $"{hostVm.GamePauseStatusText}");
            await WaitUntilAsync(() => watcher.IsOnline && !watcher.IsReconnecting, 20000).ConfigureAwait(false);
            check("[联机VM] 观战者自动重连成功、重连后仍然是观战",
                watcher.IsOnline && watcher.IsSpectator && !watcher.IsSeatedPlayer, "");

            // ===== 主动离开：席位清空、对局暂停（等房主换人） =====
            // 玩家A 坐的是哪个席位取决于前面自动轮换的结果（不是固定第二个），断言要跟着“他原来那个席位”走，
            // 否则轮到 A 坐一号位时这条会偶发失败（旧写法硬编码 SeatTwoName，flaky）。
            var aWasSeatOne = hostVm.SeatOneName == "玩家A";
            var aWasSeatTwo = hostVm.SeatTwoName == "玩家A";
            playerA.LeaveRoomCommand.Execute(null);
            await WaitUntilAsync(() => !playerA.IsOnline, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => hostVm.SeatTwoName == "空位" || hostVm.SeatOneName == "空位", 10000)
                .ConfigureAwait(false);
            var aSeatCleared = aWasSeatOne ? hostVm.SeatOneName == "空位"
                : aWasSeatTwo ? hostVm.SeatTwoName == "空位"
                : hostVm.SeatOneName == "空位" && hostVm.SeatTwoName == "空位";
            check("[联机VM] 席上玩家主动离开：房主清空他那一个席位、对局因缺人暂停",
                playerA.Mode == BoardPlayMode.Offline && !playerA.IsClient
                && hostVm.IsGamePaused && aSeatCleared
                && SnapshotMembers(hostVm).Count == 3,
                $"{hostVm.SeatOneName}/{hostVm.SeatTwoName} · {hostVm.GamePauseStatusText}");
            check("[联机VM] 玩家离开后不能再走子（房间只认在线席位）",
                !playerA.IsOnline && !playerA.CanPlayNow, "");

            // ===== 房主关房：客户端不自动重连 =====
            hostVm.LeaveRoomCommand.Execute(null);
            await WaitUntilAsync(() => !hostVm.IsOnline, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => playerB.Mode == BoardPlayMode.Offline, 10000).ConfigureAwait(false);
            await WaitUntilAsync(() => watcher.Mode == BoardPlayMode.Offline, 10000).ConfigureAwait(false);
            await Task.Delay(1200).ConfigureAwait(false);
            check("[联机VM] 房主关房：客户端回到离线且不会自动重连",
                playerB.Mode == BoardPlayMode.Offline && !playerB.IsReconnecting
                && watcher.Mode == BoardPlayMode.Offline && !watcher.IsReconnecting
                && (playerBChats.Any(t => t.Contains("房主")) || playerBChats.Any(t => t.Contains("关闭"))),
                $"{playerB.Mode} / {watcher.Mode} / {playerB.NoticeText}");
            check("[联机VM] 房主关房后自己回到离线、可以重新开房",
                hostVm.Mode == BoardPlayMode.Offline && !hostVm.IsOnline && hostVm.CanJoin, "");
            check("[联机VM] 房主关房后界面回到「○ 未连接」、重新显示开房/加入按钮",
                hostVm.ModeBadgeText == "○ 未连接" && hostVm.ShowJoinControls
                && !hostVm.HostControlsVisible, hostVm.ConnectionSummary);
        }
        finally
        {
            try { hostVm.NotifyClosed(); } catch { }
            try { playerA.NotifyClosed(); } catch { }
            try { playerB.NotifyClosed(); } catch { }
            try { watcher.NotifyClosed(); } catch { }
        }

        // ===== 同机双人（本地模式不需要网络） =====
        var localVm = new SelfCheckBoardVm();
        try
        {
            localVm.StartLocalCommand.Execute(null);
            check("[联机VM] 同机双人：Mode=Local、身份文案、没有房主控制区、可以本地两人操作",
                localVm.Mode == BoardPlayMode.Local && localVm.IsLocalMode
                && localVm.MyRoleText == "同机双人"
                && localVm.LocalSideText.Contains("同机轮流")
                && !localVm.IsOnline && !localVm.IsSeatedPlayer && !localVm.IsSpectator
                && localVm.ShowPlayerActions && !localVm.HostControlsVisible
                && !localVm.ShowStartNextGameButton && !localVm.IsGamePaused
                && SnapshotMembers(localVm).Count == 0 && localVm.CanJoin,
                $"{localVm.MyRoleText} / {localVm.ModeBadgeText}");
            check("[联机VM] 同机双人：进本地模式钩子被调一次、可以直接开始本地对局",
                localVm.LocalModeStartedCount >= 1 && localVm.Game.Started, "");
            check("[联机VM] 同机双人的徽标 / 执边文案：● 同机双人、没有联机执边",
                localVm.ModeBadgeText == "● 同机双人" && localVm.LocalSide == ""
                && localVm.LocalSideText.Contains("同机"), $"{localVm.ModeBadgeText} / {localVm.LocalSideText}");
        }
        finally
        {
            localVm.NotifyClosed();
        }
    }

    // ---------- I. 象棋棋盘朝向（执子方视角 / 观战视角 / 本地手动翻转） ----------

    private static async Task RunXiangqiViewChecksAsync(Action<string, bool, string> check, List<string> report)
    {
        // 前面的配置组把观战视角 / 房主坐席改过，先恢复默认语义
        var viewCfg = ToolConfigStore.For("xiangqi").Load<XiangqiConfig>();
        viewCfg.SpectatorViewSide = "red";
        viewCfg.HostSide = "red";
        ToolConfigStore.FlushAll();

        // ===== 离线 / 同机双人：手动翻转 =====
        var offline = new XiangqiViewModel();
        try
        {
            check("[象棋视角] 离线默认不翻转：显示「翻转棋盘」按钮、没有观战视角下拉、没有坐席提示",
                !offline.IsFlipped && offline.ShowManualFlipButton && !offline.ShowSpectatorViewOption
                && offline.ViewSideHintText == "" && offline.FlipBoardCommand.CanExecute(null),
                $"flipped={offline.IsFlipped} manual={offline.ShowManualFlipButton}");
            offline.FlipBoardCommand.Execute(null);
            check("[象棋视角] 离线手动翻转：点一下黑在下", offline.IsFlipped, "");
            offline.FlipBoardCommand.Execute(null);
            check("[象棋视角] 离线手动翻转：再点一下翻回红在下", !offline.IsFlipped, "");
            offline.FlipBoardCommand.Execute(null);
            offline.SelectedHostSide = offline.HostSideOptions.First(o => o.Value == XiangqiSide.Black);
            check("[象棋视角] 本地/离线手动翻转不会被刷新重置（改设置触发刷新后仍是黑在下）",
                offline.IsFlipped, offline.NoticeText);
            offline.SelectedHostSide = offline.HostSideOptions.First(o => o.Value == XiangqiSide.Red);
            offline.FlipBoardCommand.Execute(null);
            check("[象棋视角] 本地再翻回红在下", !offline.IsFlipped, "");
            offline.StartLocalCommand.Execute(null);
            check("[象棋视角] 同机双人：仍是手动翻转状态、显示翻转按钮、没有观战视角下拉",
                offline.IsLocalMode && offline.ShowManualFlipButton && !offline.ShowSpectatorViewOption
                && offline.ViewSideHintText == "", $"local={offline.IsLocalMode}");
            offline.FlipBoardCommand.Execute(null);
            check("[象棋视角] 同机双人手动翻转可用", offline.IsFlipped, "");
            offline.SelectedHostSide = offline.HostSideOptions.First(o => o.Value == XiangqiSide.Black);
            check("[象棋视角] 同机双人手动翻转不会被刷新重置", offline.IsFlipped, "");
        }
        finally
        {
            offline.NotifyClosed();
        }

        // 离线组的「刷新不重置」测试改过房主坐席偏好，回环组开房前恢复成房主执红
        viewCfg.HostSide = "red";
        ToolConfigStore.FlushAll();

        // ===== 真回环：房主执红 / 客户端执黑 / 第三人观战 =====
        var hostX = new XiangqiViewModel { PlayerName = "象棋房主" };
        var clientX = new XiangqiViewModel();
        var watcherX = new XiangqiViewModel();
        try
        {
            hostX.ListenPortText = "0";
            hostX.StartHostCommand.Execute(null);
            await WaitUntilAsync(() => hostX.IsOnline && hostX.IsHost, 12000).ConfigureAwait(false);
            var portMatch = Regex.Match(hostX.ConnectionSummary, @"真实端口 (\d+)");
            var port = portMatch.Success ? int.Parse(portMatch.Groups[1].Value) : 0;
            check("[象棋视角] 房主开房默认执红：红在下（不翻转）、没有两种切换控件、提示跟随执子",
                port > 0 && hostX.LocalSide == "red" && !hostX.IsFlipped
                && !hostX.ShowManualFlipButton && !hostX.ShowSpectatorViewOption
                && hostX.ViewSideHintText.Contains("红方在下"), hostX.ViewSideHintText);

            clientX.PlayerName = "执黑棋手";
            clientX.JoinAddress = $"127.0.0.1:{port}";
            clientX.JoinRoomCommand.Execute(null);
            await WaitUntilAsync(() => clientX.IsOnline && clientX.IsClient, 12000).ConfigureAwait(false);
            var clientId = clientX.LocalMemberId;
            hostX.SelectedSeatTwoOption = hostX.SeatTwoChoices.FirstOrDefault(o => o.MemberId == clientId);
            await WaitUntilAsync(() => clientX.IsSeatedPlayer && clientX.LocalSide == "black", 8000)
                .ConfigureAwait(false);
            await WaitUntilAsync(() => clientX.IsFlipped, 8000).ConfigureAwait(false);
            check("[象棋视角] 执黑玩家：棋盘自动翻转（黑在下）、提示跟随执子、两种手动控件都不显示",
                clientX.IsFlipped && clientX.ViewSideHintText.Contains("黑方在下")
                && !clientX.ShowManualFlipButton && !clientX.ShowSpectatorViewOption
                && !clientX.FlipBoardCommand.CanExecute(null), clientX.ViewSideHintText);
            var clientFlipped = clientX.IsFlipped;
            clientX.FlipBoardCommand.Execute(null);
            await Task.Delay(120).ConfigureAwait(false);
            check("[象棋视角] 执黑玩家点「翻转棋盘」无效（视角自动跟随执子方）",
                clientX.IsFlipped == clientFlipped && clientX.NoticeText.Contains("跟随"),
                clientX.NoticeText);

            watcherX.PlayerName = "观战棋友";
            watcherX.JoinAddress = $"127.0.0.1:{port}";
            watcherX.JoinRoomCommand.Execute(null);
            await WaitUntilAsync(() => watcherX.IsOnline && watcherX.IsClient, 12000).ConfigureAwait(false);
            check("[象棋视角] 观战者默认红方视角（红在下）、显示「观战视角」下拉、没有翻转按钮",
                watcherX.IsSpectator && !watcherX.IsFlipped && watcherX.ShowSpectatorViewOption
                && !watcherX.ShowManualFlipButton && watcherX.ViewSideHintText == ""
                && watcherX.SelectedSpectatorViewSide?.Value == "red", watcherX.SelectedSpectatorViewSide?.Label ?? "");
            watcherX.SelectedSpectatorViewSide =
                watcherX.SpectatorViewOptions.First(o => o.Value == "black");
            check("[象棋视角] 观战切到黑方视角：立即翻转并写入配置",
                watcherX.IsFlipped && watcherX.Cfg.SpectatorViewSide == "black",
                $"{watcherX.Cfg.SpectatorViewSide}/{watcherX.SelectedSpectatorViewSide?.Label}");
            var watcherFlipped = watcherX.IsFlipped;
            watcherX.FlipBoardCommand.Execute(null);
            await Task.Delay(120).ConfigureAwait(false);
            check("[象棋视角] 观战者点「翻转棋盘」无效（提示用观战视角下拉）",
                watcherX.IsFlipped == watcherFlipped && watcherX.NoticeText.Contains("观战视角"),
                watcherX.NoticeText);

            hostX.StartNextGameCommand.Execute(null);
            await WaitUntilAsync(() => hostX.IsGameRunning, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => watcherX.Game.Started, 8000).ConfigureAwait(false);
            hostX.SelectedAutoRotateMode =
                hostX.AutoRotateModeOptions.First(o => o.Value != hostX.SelectedAutoRotateMode.Value);
            await Task.Delay(300).ConfigureAwait(false);
            check("[象棋视角] 观战视角不会被状态包 / 刷新重置（开局并再收状态后仍是黑方视角）",
                watcherX.IsFlipped && watcherX.Game.Started,
                $"flip={watcherX.IsFlipped} started={watcherX.Game.Started} cfg={watcherX.Cfg.SpectatorViewSide} role={watcherX.MyRoleText}");

            ToolConfigStore.FlushAll();
            var viewReloaded = JsonFileStore.Read<XiangqiConfig>(watcherX.Cfg.FilePath);
            check("[象棋视角] 观战视角存进配置、下次打开记得（black）",
                viewReloaded?.SpectatorViewSide == "black", viewReloaded?.SpectatorViewSide ?? "(读不回来)");

            // 房主不下场 = 观战：按观战视角摆
            hostX.AbortGameCommand.Execute(null);
            hostX.AbortGameCommand.Execute(null);
            await WaitUntilAsync(() => !hostX.IsGameRunning, 8000).ConfigureAwait(false);
            hostX.ClearSeatsCommand.Execute(null);
            await WaitUntilAsync(() => hostX.IsSpectator, 8000).ConfigureAwait(false);
            await WaitUntilAsync(() => hostX.IsFlipped, 8000).ConfigureAwait(false);
            check("[象棋视角] 房主不坐席位 = 观战：按观战视角设置摆（当前黑视角 → 翻转）",
                hostX.IsSpectator && hostX.IsFlipped && hostX.ShowSpectatorViewOption,
                $"{hostX.Cfg.SpectatorViewSide}/{hostX.IsFlipped}");
            hostX.SelectedSpectatorViewSide = hostX.SpectatorViewOptions.First(o => o.Value == "red");
            await WaitUntilAsync(() => !hostX.IsFlipped, 8000).ConfigureAwait(false);
            // 客户端那边收状态包有网络延迟：等它把“执黑 → 黑方在下（翻转）”落实再断言，避免偶发失败
            await WaitUntilAsync(() => clientX.IsFlipped, 8000).ConfigureAwait(false);
            check("[象棋视角] 房主观战切回红方视角：不翻转，且不影响已入座的执黑玩家",
                !hostX.IsFlipped && clientX.IsFlipped,
                $"host={hostX.IsFlipped} client={clientX.IsFlipped}");

            // 换座：房主改执黑、客户端改执红 → 朝向跟着变
            hostX.SelectedSeatOneOption = hostX.SeatOneChoices.FirstOrDefault(o => o.MemberId == clientId);
            hostX.SelectedSeatTwoOption = hostX.SeatTwoChoices.FirstOrDefault(o => o.MemberId == "host");
            await WaitUntilAsync(() => hostX.LocalSide == "black" && clientX.LocalSide == "red", 8000)
                .ConfigureAwait(false);
            await WaitUntilAsync(() => hostX.IsFlipped && !clientX.IsFlipped, 8000).ConfigureAwait(false);
            check("[象棋视角] 换座后朝向跟着变：房主改执黑（翻转）、客户端改执红（不翻转）",
                hostX.IsFlipped && !clientX.IsFlipped
                && hostX.ViewSideHintText.Contains("黑方在下")
                && clientX.ViewSideHintText.Contains("红方在下"),
                $"{hostX.ViewSideHintText} / {clientX.ViewSideHintText}");
        }
        finally
        {
            try { watcherX.NotifyClosed(); } catch { }
            try { clientX.NotifyClosed(); } catch { }
            try { hostX.NotifyClosed(); } catch { }
            // 还原配置，避免影响后续 / 用户真实配置
            viewCfg.SpectatorViewSide = "red";
            viewCfg.HostSide = "red";
            ToolConfigStore.FlushAll();
        }
    }

    /// <summary>房主交换两席（命令是 RelayCommand，测试里直接触发；CanExecute 不满足时什么都不做）。</summary>
    private static void SwapSeats(SelfCheckBoardVm vm) => vm.SwapSeatsCommand.Execute(null);

    // ---------- 回环会话用的小工具 ----------

    private static JsonElement GameJson(GomokuGame game)
        => JsonSerializer.SerializeToElement(game.ToSnapshot(), RoomProtocol.JsonOptions);

    private static JsonElement GameJson(XiangqiGame game)
        => JsonSerializer.SerializeToElement(game.ToSnapshot(), RoomProtocol.JsonOptions);

    private static BoardRoomState? RoomOf(JsonElement stateEnvelope)
        => BoardProtocol.ReadState(stateEnvelope)?.Room;

    private static string? StateGomokuResult(JsonElement stateEnvelope)
    {
        if (BoardProtocol.ReadState(stateEnvelope) is not { Game: { } game }) return null;
        return game.TryGetProperty("result", out var result) ? result.GetString() : null;
    }

    private static int? StateGomokuMoveCount(JsonElement stateEnvelope)
    {
        if (BoardProtocol.ReadState(stateEnvelope) is not { Game: { } game }) return null;
        if (!game.TryGetProperty("moves", out var moves) || moves.ValueKind != JsonValueKind.Array) return null;
        return moves.GetArrayLength();
    }

    private static int StateXiangqiHistory(JsonElement stateEnvelope)
    {
        if (BoardProtocol.ReadState(stateEnvelope) is not { Game: { } game }) return -1;
        if (!game.TryGetProperty("history", out var history) || history.ValueKind != JsonValueKind.Array) return -1;
        return history.GetArrayLength();
    }

    private static GomokuGame? RestoreGomoku(JsonElement stateEnvelope)
    {
        if (BoardProtocol.ReadState(stateEnvelope) is not { Game: { } game }) return null;
        var snapshot = JsonSerializer.Deserialize<GomokuSnapshot>(game.GetRawText(), RoomProtocol.JsonOptions);
        return snapshot is null ? null : GomokuGame.FromSnapshot(snapshot).Game;
    }

    private static XiangqiGame? RestoreXiangqi(JsonElement stateEnvelope)
    {
        if (BoardProtocol.ReadState(stateEnvelope) is not { Game: { } game }) return null;
        var snapshot = JsonSerializer.Deserialize<XiangqiSnapshot>(game.GetRawText(), RoomProtocol.JsonOptions);
        return snapshot is null ? null : XiangqiGame.FromSnapshot(snapshot).Game;
    }

    /// <summary>建一个客户端会话，并把收到的状态 / 聊天 / 请求 / 应答都抄到线程安全队列（事件在后台线程）。</summary>
    private static (
        BoardRoomSession Session,
        ConcurrentQueue<JsonElement> States,
        ConcurrentQueue<(string From, string Name, string Text, bool IsSystem)> Chats,
        ConcurrentQueue<(string From, string Name, string Type)> Requests,
        ConcurrentQueue<(string From, string Type, bool Accept)> Responses) NewClient(
            string name, string gameId = BoardProtocol.GameGomoku)
    {
        var session = new BoardRoomSession(gameId, name);
        var states = new ConcurrentQueue<JsonElement>();
        var chats = new ConcurrentQueue<(string From, string Name, string Text, bool IsSystem)>();
        var requests = new ConcurrentQueue<(string From, string Name, string Type)>();
        var responses = new ConcurrentQueue<(string From, string Type, bool Accept)>();
        session.StateReceived += e => states.Enqueue(e);
        session.ChatReceived += (from, senderName, text, isSystem) =>
            chats.Enqueue((from, senderName, text, isSystem));
        session.RequestReceived += (from, fromName, type, _) => requests.Enqueue((from, fromName, type));
        session.ResponseReceived += (from, type, accept, _) => responses.Enqueue((from, type, accept));
        return (session, states, chats, requests, responses);
    }

    /// <summary>把「主机会话事件」接到房间模型上（等价于基类做的事）：成员进出、重连、离线都更新模型并广播状态。</summary>
    private static void WireHostSession(BoardRoomSession host, SessionRoomModel room)
    {
        host.MemberJoined += member =>
        {
            room.AddMember(member);
            _ = host.BroadcastStateAsync(room.BuildEnvelope());
        };
        host.MemberReconnected += member =>
        {
            room.SetOnline(member.MemberId, true);
            _ = host.BroadcastStateAsync(room.BuildEnvelope());
        };
        host.MemberLeft += (member, kind, _reason) =>
        {
            if (kind == BoardDisconnectKind.PeerBye) room.RemoveMember(member.MemberId);
            else room.SetOnline(member.MemberId, false);
            _ = host.BroadcastStateAsync(room.BuildEnvelope());
        };
    }

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

    /// <summary>从一条裸 TCP 连接读一个协议帧（4 字节头长 + JSON 头 + 载荷）；对端关闭返回 (null, 空)。</summary>
    private static async Task<(RoomFrame? Frame, byte[] Payload)> ReadRawFrameAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var prefix = new byte[RoomProtocol.LengthPrefixBytes];
        if (!await ReadExactAsync(stream, prefix, ct).ConfigureAwait(false)) return (null, Array.Empty<byte>());
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (!RoomProtocol.IsValidFrameLength(headerLength)) return (null, Array.Empty<byte>());
        var headerBytes = new byte[headerLength];
        if (!await ReadExactAsync(stream, headerBytes, ct).ConfigureAwait(false)) return (null, Array.Empty<byte>());
        var frame = JsonSerializer.Deserialize<RoomFrame>(headerBytes, RoomProtocol.JsonOptions);
        if (frame is null) return (null, Array.Empty<byte>());
        byte[] payload = Array.Empty<byte>();
        if (frame.Payload > 0)
        {
            if (frame.Payload > RoomProtocol.MaxPayloadBytes) return (null, Array.Empty<byte>());
            payload = new byte[frame.Payload];
            if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false)) return (null, Array.Empty<byte>());
        }

        return (frame, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    /// <summary>把 VM 的聊天列表变化抄到线程安全队列（自检进程没有 Application，<c>Ui()</c> 同步执行，列表会被后台线程改）。</summary>
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

    /// <summary>取成员列表快照（ObservableCollection 可能正被后台线程原地更新，重试几次）。</summary>
    private static List<BoardMemberItem> SnapshotMembers(BoardGameRoomViewModelBase vm)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { return vm.Members.ToList(); }
            catch (InvalidOperationException) { Thread.Sleep(10); }
        }

        return new List<BoardMemberItem>();
    }

    /// <summary>
    /// 自检用的最小棋牌 VM（多人房间契约）：只实现基类 abstract，用五子棋引擎验证房间行为
    /// （成员 / 席位 / 自动轮换 / 暂停 / 观战只读），不碰界面。
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
        public int SnapshotAppliedCount { get; private set; }
        public int SeatsChangedCount { get; private set; }
        public int MembersChangedCount { get; private set; }
        public int PeerJoinedCount { get; private set; }
        public int PeerLeftCount { get; private set; }
        public int ReconnectedCount { get; private set; }
        public int LocalModeStartedCount { get; private set; }
        public int HostRoomOpenedCount { get; private set; }

        /// <summary>模拟游戏 VM 的「本端现在能不能落子」：本地看轮次；联机 = 在线 && 坐席 && 不暂停 && 轮到自己。</summary>
        public bool CanPlayNow
        {
            get
            {
                if (!_game.Started || _game.IsOver) return false;
                if (IsLocalMode) return true;
                return IsOnline && IsSeatedPlayer && !IsGamePaused
                       && LocalSide == GomokuText.SideKey(_game.Turn);
            }
        }

        /// <summary>模拟游戏 VM 的「棋盘能不能点」：和 CanPlayNow 同源。</summary>
        public bool IsBoardInteractive => CanPlayNow;

        /// <summary>主机 / 同机双人：按本端席位执边下一手，并广播状态。</summary>
        public void HostPlay(int x, int y)
        {
            var side = IsLocalMode
                ? _game.Turn
                : GomokuText.ParseSide(LocalSide);
            if (side == GomokuCell.Empty) return;
            _game.Play(side, x, y);
            BroadcastSnapshot();
            NotifyGameFinishedIfNeeded();
        }

        /// <summary>客户端：把一手棋作为动作报给主机。</summary>
        public void ClientPlay(int x, int y) => SendAction("move", new { x, y });

        protected override string FirstSeatSide => "black";

        protected override string PreferredHostSeatSide => Cfg.EffectiveHostSide;

        protected override string OppositeSide(string side) => side == "white" ? "black" : "white";

        protected override string SideDisplayName(string side) => side == "black" ? "黑棋" : "白棋";

        protected override BoardGameStatus ReportGameStatus()
            => new(_game.Started, _game.IsOver, _game.Result switch
            {
                GomokuResult.BlackWin => "black",
                GomokuResult.WhiteWin => "white",
                _ => null,
            });

        protected override void ResetGameToWaiting()
        {
            _game.Reset(_game.Size, _game.ForbiddenEnabled);
            _game.Started = false;
        }

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
            SnapshotAppliedCount++;
            NotifyGameFinishedIfNeeded();
        }

        protected override void OnHostAction(string fromMemberId, string fromSide, string type, JsonElement? payload)
        {
            if (type != "move" || payload is not { } move) return;
            var side = GomokuText.ParseSide(fromSide);
            if (side == GomokuCell.Empty) return;
            _game.Play(side, move.GetProperty("x").GetInt32(), move.GetProperty("y").GetInt32());
            BroadcastSnapshot();
            NotifyGameFinishedIfNeeded();
        }

        protected override bool ApplyUndoFor(string requesterSide)
        {
            var side = GomokuText.ParseSide(requesterSide);
            return side != GomokuCell.Empty && _game.UndoToTurn(side);
        }

        protected override void ApplyDraw() => _game.MarkDraw();

        protected override void ApplyResign(string side)
        {
            var losing = GomokuText.ParseSide(side);
            if (losing != GomokuCell.Empty) _game.MarkResign(losing);
        }

        protected override void BeginNewGame(bool swapSides)
        {
            _game.Reset(_game.Size, _game.ForbiddenEnabled);
            _game.Started = true;
        }

        protected override void OnHostRoomOpened() => HostRoomOpenedCount++;

        protected override void OnLocalModeStarted()
        {
            LocalModeStartedCount++;
            _game.Reset(GomokuBoard.DefaultSize, false);
            _game.Started = true;
        }

        protected override void OnPeerJoined() => PeerJoinedCount++;

        protected override void OnPeerLeft(string? reason) => PeerLeftCount++;

        protected override void OnReconnected() => ReconnectedCount++;

        protected override void OnSeatsChanged() => SeatsChangedCount++;

        protected override void OnMembersChanged() => MembersChangedCount++;

        protected override void OnSnapshotApplied() { }
    }

    /// <summary>
    /// 自检用来扮演「基类」的最小房间模型：维护成员表（含房主）/ 两个席位 / 自动轮换设置，
    /// 并生成 v2 状态包装 <c>{seq, room, game}</c>。纯测试用，不碰界面。
    /// </summary>
    private sealed class SessionRoomModel
    {
        private readonly object _gate = new();
        private readonly List<BoardMemberInfo> _members = new();
        private int _seq;
        private JsonElement _game;

        /// <summary>房间状态（成员表由 <see cref="BuildEnvelope"/> 同步成快照）。</summary>
        public BoardRoomState Room { get; } = new();

        /// <summary>当前成员快照（拷贝，避免测试线程读到半改状态）。</summary>
        public IReadOnlyList<BoardMemberInfo> Members
        {
            get
            {
                lock (_gate) return _members.Select(m => m.Clone()).ToList();
            }
        }

        public void AddHost(string name)
        {
            lock (_gate)
            {
                _members.Add(new BoardMemberInfo
                {
                    MemberId = BoardRoomSession.HostMemberId,
                    Name = name,
                    IsHost = true,
                    IsOnline = true,
                    GamesPlayed = 0,
                    JoinOrder = 0,
                });
                SyncMembersLocked();
            }
        }

        public void AddMember(BoardMemberInfo member)
        {
            lock (_gate)
            {
                var existing = _members.FirstOrDefault(m => m.MemberId == member.MemberId);
                if (existing is null)
                {
                    _members.Add(member.Clone());
                }
                else
                {
                    existing.Name = member.Name;
                    existing.IsOnline = true;
                }
                SyncMembersLocked();
            }
        }

        public void SetOnline(string memberId, bool online)
        {
            lock (_gate)
            {
                var member = _members.FirstOrDefault(m => m.MemberId == memberId);
                if (member is not null) member.IsOnline = online;
                SyncMembersLocked();
            }
        }

        public void RemoveMember(string memberId)
        {
            lock (_gate)
            {
                _members.RemoveAll(m => m.MemberId == memberId);
                SyncMembersLocked();
            }
        }

        public void SetSeats(string seatOne, string seatTwo)
        {
            lock (_gate)
            {
                Room.SeatOneMemberId = seatOne ?? "";
                Room.SeatTwoMemberId = seatTwo ?? "";
            }
        }

        public void SetModes(string rotateMode, string pickMode)
        {
            lock (_gate)
            {
                Room.AutoRotateMode = string.IsNullOrWhiteSpace(rotateMode) ? "manual" : rotateMode;
                Room.AutoPickMode = string.IsNullOrWhiteSpace(pickMode) ? "rotation" : pickMode;
            }
        }

        public void SetGame(JsonElement game)
        {
            lock (_gate) _game = game;
        }

        public bool IsSeatOne(string memberId)
        {
            lock (_gate) return Room.SeatOneMemberId == memberId;
        }

        public bool IsSeatTwo(string memberId)
        {
            lock (_gate) return Room.SeatTwoMemberId == memberId;
        }

        /// <summary>生成最新状态包装（seq 只增；成员表同步成最新快照）。</summary>
        public JsonElement BuildEnvelope()
        {
            lock (_gate)
            {
                SyncMembersLocked();
                return BoardProtocol.WrapState(++_seq, Room, _game);
            }
        }

        private void SyncMembersLocked() => Room.Members = _members.Select(m => m.Clone()).ToList();
    }
}


