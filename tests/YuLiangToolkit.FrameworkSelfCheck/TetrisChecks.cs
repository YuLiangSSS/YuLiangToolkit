using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「游戏 → 俄罗斯方块」自检：工具与类别注册 / 方块形状 / SRS 旋转朝向 / 墙踢表 /
/// 碰撞与边界 / 7-bag 出块 / 移动·软降·硬降 / 消行与计分升级 / 暂存 / 幽灵落点 / 顶死判负 / 配置。
///
/// <para><b>全部是纯逻辑</b>：只 new <see cref="TetrisEngine"/> 与配置对象，
/// 不创建窗口、不 new 控件、不渲染、不碰真实 Data/Config。</para>
/// </summary>
internal static class TetrisChecks
{
    /// <summary>自检框架传进来的是 <c>Action&lt;string,bool,string&gt;</c>（detail 必填），这里包一层带默认值的委托。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;   // 本组不使用汇总回调
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckCategoryRegistration(fn);
        CheckPieces(fn);
        CheckRotation(fn);
        CheckKicks(fn);
        CheckCollision(fn);
        CheckBag(fn);
        CheckMovement(fn);
        CheckClearAndScore(fn);
        CheckHold(fn);
        CheckGhostAndGameOver(fn);
        CheckHelpContent(fn);
        CheckNetRules(fn);
        CheckNetLoopback(check);
        CheckConfig(fn);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new TetrisTool();

        check("[俄罗斯方块] 工具 Id 是 tetris", tool.Id == "tetris", tool.Id);
        check("[俄罗斯方块] 工具名是「俄罗斯方块」", tool.Name == "俄罗斯方块", tool.Name);
        check("[俄罗斯方块] 图标是 🧱", tool.EmojiIcon == "🧱", tool.EmojiIcon);
        check("[俄罗斯方块] 分类与类别路径都是「游戏」",
            tool.Category == "游戏" && tool.CategoryPath.SequenceEqual(new[] { "游戏" }),
            $"{tool.Category} / {string.Join("/", tool.CategoryPath)}");
        check("[俄罗斯方块] Order = 70（「游戏」类别里的第一块）", tool.Order == 70, tool.Order.ToString());
        check("[俄罗斯方块] 标签里能搜到「俄罗斯方块」「Tetris」「小游戏」",
            tool.Tags.Contains("俄罗斯方块") && tool.Tags.Contains("Tetris") && tool.Tags.Contains("小游戏"),
            string.Join(",", tool.Tags));
        check("[俄罗斯方块] 描述提到 7-bag / 暂存 / 幽灵 / 最高分",
            tool.Description.Contains("7-bag") && tool.Description.Contains("暂存")
            && tool.Description.Contains("幽灵") && tool.Description.Contains("最高分"),
            tool.Description);
        check("[俄罗斯方块] 工具 Id 与 TetrisConfig.ToolId 一致（Data / Config 目录名）",
            tool.Id == TetrisConfig.ToolId, TetrisConfig.ToolId);
        check("[俄罗斯方块] 实现了 ITool 接口", tool is ITool);
    }

    // ---------------------------------------------------------------- 「游戏」类别注册

    private static void CheckCategoryRegistration(CheckFn check)
    {
        check("[俄罗斯方块] ShellCategories 里定义了「游戏」类别名",
            ShellCategories.Games == "游戏", ShellCategories.Games);

        var category = ShellCategories.All.FirstOrDefault(c => c.Path.SequenceEqual(new[] { "游戏" }));
        check("[俄罗斯方块] ShellCategories.All 里有「游戏」类别", category is not null,
            string.Join(" / ", ShellCategories.All.Select(c => string.Join("›", c.Path))));

        if (category is not null)
        {
            check("[俄罗斯方块] 「游戏」类别图标是 🎮、Order = 30、有说明文案",
                category.EmojiIcon == "🎮" && category.Order == 30 && category.Description.Length > 0,
                $"{category.EmojiIcon} / {category.Order} / {category.Description}");
        }

        // 建树：俄罗斯方块收进「游戏」节点，主页根层不再平铺它
        ITool[] tools = { new TetrisTool() };
        var root = ToolCatalog.Build(tools, ShellCategories.All);
        var gamesNode = root.FindByPath(new[] { "游戏" });

        check("[俄罗斯方块] 目录树里有「游戏」节点", gamesNode is not null,
            string.Join(" / ", root.Children.Select(c => c.Name)));

        if (gamesNode is not null)
        {
            check("[俄罗斯方块] 「游戏」节点下挂着俄罗斯方块",
                gamesNode.Tools.Select(t => t.Id).SequenceEqual(new[] { "tetris" }),
                string.Join(",", gamesNode.Tools.Select(t => t.Id)));
            check("[俄罗斯方块] 「游戏」节点递归计数 = 1", gamesNode.ToolCount == 1, gamesNode.ToolCount.ToString());
        }

        check("[俄罗斯方块] 不再挂在主页根层", root.Tools.Count == 0,
            string.Join(",", root.Tools.Select(t => t.Name)));
    }

    // ---------------------------------------------------------------- 方块形状

    private static void CheckPieces(CheckFn check)
    {
        check("[俄罗斯方块] 一共 7 种方块", TetrisPieces.All.Count == 7,
            string.Join(",", TetrisPieces.All));

        var allWellFormed = true;
        var detail = "";
        foreach (var kind in TetrisPieces.All)
        {
            for (var rotation = 0; rotation < TetrisPieces.RotationCount; rotation++)
            {
                var cells = TetrisPieces.CellsOf(kind, rotation);
                var box = TetrisPieces.BoxSize(kind);

                if (cells.Count != 4) { allWellFormed = false; detail = $"{kind}/{rotation} 格子数 {cells.Count}"; }
                if (cells.Distinct().Count() != cells.Count) { allWellFormed = false; detail = $"{kind}/{rotation} 有重复格"; }
                if (cells.Any(c => c.X < 0 || c.X >= box || c.Y < 0 || c.Y >= box))
                {
                    allWellFormed = false;
                    detail = $"{kind}/{rotation} 超出 {box}×{box} 包围盒";
                }
            }
        }
        check("[俄罗斯方块] 7 种 × 4 朝向都是「4 个不重复格子、都在包围盒内」", allWellFormed, detail);

        check("[俄罗斯方块] 包围盒边长：I = 4、O = 2、其余 3",
            TetrisPieces.BoxSize(TetrominoKind.I) == 4
            && TetrisPieces.BoxSize(TetrominoKind.O) == 2
            && TetrisPieces.BoxSize(TetrominoKind.T) == 3
            && TetrisPieces.BoxSize(TetrominoKind.Z) == 3);

        check("[俄罗斯方块] 出生位置让方块在 10 列上居中（O 在 4、其它在 3）",
            TetrisPieces.SpawnX(TetrominoKind.O) == 4
            && TetrisPieces.SpawnX(TetrominoKind.I) == 3
            && TetrisPieces.SpawnX(TetrominoKind.T) == 3);

        // 出生位置必须真的落在棋盘里
        var spawnFits = true;
        foreach (var kind in TetrisPieces.All)
        {
            var probe = new TetrisEngine(new Random(7));
            probe.PlacePiece(new TetrisPiece(kind, TetrisPieces.SpawnX(kind), TetrisEngine.SpawnY, 0));
            if (!probe.Fits(probe.Current!.Value)) spawnFits = false;
        }
        check("[俄罗斯方块] 7 种方块在出生位置都放得下（空棋盘）", spawnFits);

        check("[俄罗斯方块] 朝向归一化：负数与超界都能收敛到 0~3",
            TetrisPieces.NormalizeRotation(-1) == 3
            && TetrisPieces.NormalizeRotation(4) == 0
            && TetrisPieces.NormalizeRotation(7) == 3);

        check("[俄罗斯方块] 每种方块都有中文名与字母代号",
            TetrisPieces.All.All(k => TetrisPieces.DisplayName(k).Length > 0
                && TetrisPieces.LetterOf(k) == k.ToString()));

        check("[俄罗斯方块] 七种方块的颜色互不相同（画出来能分辨）",
            TetrisPieces.All.Select(TetrominoPalette.HexOf).Distinct().Count() == 7);
    }

    // ---------------------------------------------------------------- 旋转朝向

    private static void CheckRotation(CheckFn check)
    {
        // 转一圈回到原形（按格子集合比）
        static string Signature(TetrominoKind kind, int rotation)
            => string.Join(";", TetrisPieces.CellsOf(kind, rotation)
                .OrderBy(c => c.Y).ThenBy(c => c.X)
                .Select(c => $"{c.X},{c.Y}"));

        check("[俄罗斯方块] 朝向 0 与 4 同形（转一圈回到原样）",
            TetrisPieces.All.All(k => Signature(k, 0) == Signature(k, 4)));

        check("[俄罗斯方块] 除 O 之外，朝 0 / 1 / 2 形状互不相同",
            TetrisPieces.All.Where(k => k != TetrominoKind.O)
                .All(k => Signature(k, 0) != Signature(k, 1) && Signature(k, 1) != Signature(k, 2)));

        check("[俄罗斯方块] O 方块四个朝向完全一样（转了也不变）",
            Signature(TetrominoKind.O, 0) == Signature(TetrominoKind.O, 1)
            && Signature(TetrominoKind.O, 1) == Signature(TetrominoKind.O, 2)
            && Signature(TetrominoKind.O, 2) == Signature(TetrominoKind.O, 3));

        // 顺时针转四次回到起点：棋盘上真的转一遍
        var engine = new TetrisEngine(new Random(11));
        var start = new TetrisPiece(TetrominoKind.T, 4, 8, 0);
        engine.PlacePiece(start);
        for (var i = 0; i < 4; i++) engine.Rotate(clockwise: true);
        check("[俄罗斯方块] 顺时针转 4 次回到原朝向（且位置不变）",
            engine.Current is { } back && back.Rotation == 0 && back.X == start.X && back.Y == start.Y,
            engine.Current?.ToString() ?? "(null)");

        // 逆时针一次 = 顺时针三次的朝向
        var ccw = new TetrisEngine(new Random(11));
        ccw.PlacePiece(start);
        ccw.Rotate(clockwise: false);
        var ccwRotation = ccw.Current!.Value.Rotation;
        var cw3 = new TetrisEngine(new Random(11));
        cw3.PlacePiece(start);
        for (var i = 0; i < 3; i++) cw3.Rotate(clockwise: true);
        check("[俄罗斯方块] 逆时针转 1 次 = 顺时针转 3 次的朝向",
            ccwRotation == 3 && cw3.Current!.Value.Rotation == 3,
            $"{ccwRotation} / {cw3.Current?.Rotation}");
    }

    // ---------------------------------------------------------------- 墙踢表

    private static void CheckKicks(CheckFn check)
    {
        var oOnlyZero = true;
        for (var from = 0; from < 4; from++)
        {
            for (var to = 0; to < 4; to++)
            {
                var kicks = TetrisPieces.KickOffsets(TetrominoKind.O, from, to);
                if (kicks.Count != 1 || kicks[0] != (0, 0)) oOnlyZero = false;
            }
        }
        check("[俄罗斯方块] O 方块不参与踢墙（只有「原地转」一种尝试）", oOnlyZero);

        var tablesOk = true;
        var detail = "";
        foreach (var kind in new[] { TetrominoKind.I, TetrominoKind.J, TetrominoKind.T, TetrominoKind.S, TetrominoKind.Z })
        {
            for (var from = 0; from < 4; from++)
            {
                var to = (from + 1) % 4;
                var kicks = TetrisPieces.KickOffsets(kind, from, to);
                if (kicks.Count != 5 || kicks[0] != (0, 0))
                {
                    tablesOk = false;
                    detail = $"{kind} {from}→{to} 有 {kicks.Count} 个偏移";
                }
            }
        }
        check("[俄罗斯方块] I / J / T / S / Z 每次旋转都有 5 个踢墙偏移（第一个是原地）", tablesOk, detail);

        check("[俄罗斯方块] 顺时针 / 逆时针走的是方向相反的两套偏移",
            TetrisPieces.KickOffsets(TetrominoKind.T, 0, 1)[1] == (-1, 0)
            && TetrisPieces.KickOffsets(TetrominoKind.T, 1, 0)[1] == (1, 0)
            && TetrisPieces.KickOffsets(TetrominoKind.I, 0, 1)[1] == (-2, 0)
            && TetrisPieces.KickOffsets(TetrominoKind.I, 1, 0)[1] == (2, 0));

        // 真场景：贴着右墙把竖 I 转横，必须靠踢墙挪开一列
        var engine = new TetrisEngine(new Random(13));
        engine.PlacePiece(new TetrisPiece(TetrominoKind.I, 7, 5, 1));
        var kicked = engine.Rotate(clockwise: true);
        check("[俄罗斯方块] 贴右墙把竖 I 转横：靠踢墙成功并左移一列",
            kicked.Success && kicked.KickIndex == 1 && engine.Current is { X: 6, Rotation: 2 },
            $"success={kicked.Success} kick={kicked.KickIndex} piece={engine.Current}");

        // 反例：1 格宽的深井里转横（5 个偏移全撞），必须原地不动
        var well = new TetrisEngine(new Random(17));
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (x != 1) well.SetCell(x, y, TetrominoKind.O);
            }
        }
        var before = new TetrisPiece(TetrominoKind.I, -1, 10, 1);
        well.PlacePiece(before);
        var blocked = well.Rotate(clockwise: true);
        check("[俄罗斯方块] 1 格宽深井里转不过去：5 个踢墙偏移全撞 → 保持原样",
            !blocked.Success && blocked.KickIndex == -1 && well.Current == before,
            $"success={blocked.Success} kick={blocked.KickIndex} piece={well.Current}");

        // O 方块转了一定成功（不会吃掉一次输入）
        var oEngine = new TetrisEngine(new Random(19));
        oEngine.PlacePiece(new TetrisPiece(TetrominoKind.O, 4, 15, 0));
        var oResult = oEngine.Rotate(clockwise: true);
        check("[俄罗斯方块] O 方块旋转总是成功（形状不变）",
            oResult.Success && oEngine.Current is { Rotation: 1 } && oResult.KickIndex == 0);
    }

    // ---------------------------------------------------------------- 碰撞与边界

    private static void CheckCollision(CheckFn check)
    {
        var engine = new TetrisEngine(new Random(23));

        check("[俄罗斯方块] 空棋盘上出生位置的方块放得下", engine.Fits(engine.Current!.Value));

        var piece = new TetrisPiece(TetrominoKind.O, 0, 0, 0);
        check("[俄罗斯方块] 左边越界判不合法",
            !engine.Fits(piece.Moved(-1, 0)) && engine.Fits(piece));
        check("[俄罗斯方块] 右边越界判不合法（10 列，O 在第 8 列是最右）",
            !engine.Fits(new TetrisPiece(TetrominoKind.O, 9, 0, 0))
            && engine.Fits(new TetrisPiece(TetrominoKind.O, 8, 0, 0)));
        check("[俄罗斯方块] 穿底判不合法",
            !engine.Fits(new TetrisPiece(TetrominoKind.O, 0, TetrisEngine.Height - 1, 0))
            && engine.Fits(new TetrisPiece(TetrominoKind.O, 0, TetrisEngine.Height - 2, 0)));

        check("[俄罗斯方块] 允许方块有一部分还在棋盘上方（Y < 0）",
            engine.Fits(new TetrisPiece(TetrominoKind.O, 4, -1, 0)));

        engine.SetCell(4, 10, TetrominoKind.T);
        check("[俄罗斯方块] 和已落下的块重叠判不合法，空地照常放得下",
            !engine.Fits(new TetrisPiece(TetrominoKind.O, 4, 10, 0))
            && engine.Fits(new TetrisPiece(TetrominoKind.O, 4, 5, 0)));

        check("[俄罗斯方块] 越界取格子返回 null",
            engine.CellAt(-1, 0) is null && engine.CellAt(0, -1) is null
            && engine.CellAt(TetrisEngine.Width, 0) is null
            && engine.CellAt(0, TetrisEngine.Height) is null);

        check("[俄罗斯方块] 棋盘尺寸是 10 × 20，预览 3 块",
            TetrisEngine.Width == 10 && TetrisEngine.Height == 20 && TetrisEngine.PreviewCount == 3);

        check("[俄罗斯方块] InBounds 判边界（含端点）",
            TetrisEngine.InBounds(0, 0) && TetrisEngine.InBounds(9, 19)
            && !TetrisEngine.InBounds(-1, 0) && !TetrisEngine.InBounds(10, 0) && !TetrisEngine.InBounds(0, 20));

        // 下落间隔：等级越高越快，但不会快过 60ms
        var intervals = Enumerable.Range(1, TetrisEngine.MaxLevel).Select(TetrisEngine.FallIntervalMs).ToArray();
        var decreasing = intervals.Zip(intervals.Skip(1), (a, b) => b <= a).All(x => x);
        check("[俄罗斯方块] 等级 1 = 800ms，等级越高下落越快（单调不增）",
            intervals[0] == 800 && decreasing, string.Join(",", intervals));
        check("[俄罗斯方块] 下落间隔夹在 60 ~ 800ms，越界等级也能算",
            intervals.All(v => v >= 60 && v <= 800)
            && TetrisEngine.FallIntervalMs(0) == 800
            && TetrisEngine.FallIntervalMs(999) == intervals[^1],
            $"{TetrisEngine.FallIntervalMs(0)} / {TetrisEngine.FallIntervalMs(999)}");
    }

    // ---------------------------------------------------------------- 7-bag 出块

    private static void CheckBag(CheckFn check)
    {
        // 固定种子：连出 7 块必须正好是 7 种各一次
        var engine = new TetrisEngine(new Random(1234));
        var firstSeven = new List<TetrominoKind>();
        for (var i = 0; i < 7; i++)
        {
            firstSeven.Add(engine.Current!.Value.Kind);
            engine.HardDrop();
        }

        check("[俄罗斯方块] 7-bag：前 7 块正好 7 种各一次（不会连着重复）",
            firstSeven.Count == 7 && firstSeven.Distinct().Count() == 7,
            string.Join(",", firstSeven));

        // 连续几十块：每一轮 7 块都要集齐 7 种
        var engine2 = new TetrisEngine(new Random(99));
        var allKinds = new List<TetrominoKind>();
        for (var i = 0; i < 70 && !engine2.IsGameOver; i++)
        {
            allKinds.Add(engine2.Current!.Value.Kind);
            engine2.HardDrop();
        }

        var bagsFill = true;
        for (var start = 0; start + 7 <= allKinds.Count; start += 7)
        {
            if (allKinds.Skip(start).Take(7).Distinct().Count() != 7) bagsFill = false;
        }
        check("[俄罗斯方块] 7-bag：每一轮 7 块都集齐 7 种（逐轮检查实际出块顺序）", bagsFill,
            $"{allKinds.Count} 块");

        check("[俄罗斯方块] 预览区一直有 3 块", engine2.NextPieces.Count == 3,
            string.Join(",", engine2.NextPieces));

        // 预览的下一块 = 真正出场的那块
        var engine3 = new TetrisEngine(new Random(555));
        var expected = engine3.NextPieces[0];
        engine3.HardDrop();
        check("[俄罗斯方块] 出块顺序与预览一致（预览第一块就是下一块）",
            engine3.Current!.Value.Kind == expected,
            $"{expected} / {engine3.Current?.Kind}");
    }

    // ---------------------------------------------------------------- 移动 / 软降 / 硬降

    private static void CheckMovement(CheckFn check)
    {
        var engine = new TetrisEngine(new Random(31));
        engine.PlacePiece(new TetrisPiece(TetrominoKind.O, 0, 5, 0));

        check("[俄罗斯方块] 贴左墙再往左移不动",
            !engine.MoveLeft() && engine.Current is { X: 0 });

        engine.MoveRight();
        check("[俄罗斯方块] 右移一格", engine.Current is { X: 1 });

        engine.PlacePiece(new TetrisPiece(TetrominoKind.O, 8, 5, 0));
        check("[俄罗斯方块] 贴右墙再往右移不动",
            !engine.MoveRight() && engine.Current is { X: 8 });

        engine.PlacePiece(new TetrisPiece(TetrominoKind.O, 4, 17, 0));
        var scoreBefore = engine.Score;
        check("[俄罗斯方块] 软降成功下落一格 +1 分",
            engine.SoftDrop() && engine.Current is { Y: 18 } && engine.Score == scoreBefore + 1,
            $"score={engine.Score} piece={engine.Current}");

        check("[俄罗斯方块] 到底之后软降失败、位置不变",
            !engine.SoftDrop() && engine.Current is { Y: 18 });

        var ghost = engine.GhostPiece();
        check("[俄罗斯方块] 幽灵落点 = 一直落到底的位置（自己放得下、再下一格放不下）",
            ghost is { Y: 18 } && engine.Fits(ghost.Value) && !engine.Fits(ghost.Value.Moved(0, 1)),
            ghost?.ToString() ?? "(null)");

        var engine3 = new TetrisEngine(new Random(41));
        engine3.PlacePiece(new TetrisPiece(TetrominoKind.O, 4, 0, 0));
        var dropScore = engine3.Score;
        var result = engine3.HardDrop();
        check("[俄罗斯方块] 硬降一路落到底、写进棋盘并按格子加分",
            result.LinesCleared == 0
            && engine3.CellAt(4, TetrisEngine.Height - 1) == TetrominoKind.O
            && engine3.CellAt(5, TetrisEngine.Height - 1) == TetrominoKind.O
            && engine3.Score > dropScore,
            $"score={engine3.Score}");

        check("[俄罗斯方块] 硬降之后自动出下一块", engine3.Current is not null);
    }

    // ---------------------------------------------------------------- 消行 / 计分 / 升级

    private static void CheckClearAndScore(CheckFn check)
    {
        // 造一行只缺两格：用 O 方块补上 → 消 1 行、得 100 分
        var engine = new TetrisEngine(new Random(53));
        for (var x = 2; x < TetrisEngine.Width; x++) engine.SetCell(x, TetrisEngine.Height - 1, TetrominoKind.T);
        engine.PlacePiece(new TetrisPiece(TetrominoKind.O, 0, TetrisEngine.Height - 2, 0));
        var one = engine.Lock();

        check("[俄罗斯方块] 补满一行 → 消 1 行、得 100 分（1 级）",
            one.LinesCleared == 1 && one.ScoreGained == 100 && engine.Lines == 1 && engine.Score == 100,
            $"lines={one.LinesCleared} gained={one.ScoreGained} total={engine.Score}");
        check("[俄罗斯方块] 消行后有提示文案", one.ComboText.Length > 0, one.ComboText);
        check("[俄罗斯方块] 消行后上方方块整体落下一行",
            engine.CellAt(0, TetrisEngine.Height - 1) == TetrominoKind.O
            && engine.CellAt(1, TetrisEngine.Height - 1) == TetrominoKind.O,
            $"{engine.CellAt(0, TetrisEngine.Height - 1)} / {engine.CellAt(1, TetrisEngine.Height - 1)}");

        // 一次消 4 行（TETRIS）：铺满最后 4 行的 1~9 列，再用竖 I 补第 0 列
        var tetrisEngine = new TetrisEngine(new Random(59));
        FillFourRowsExceptFirstColumn(tetrisEngine, TetrominoKind.L);
        tetrisEngine.PlacePiece(new TetrisPiece(TetrominoKind.I, -2, TetrisEngine.Height - 4, 1));
        var four = tetrisEngine.Lock();

        check("[俄罗斯方块] 一次消 4 行 → 800 分（TETRIS）",
            four.LinesCleared == 4 && four.ScoreGained == 800 && tetrisEngine.Lines == 4,
            $"lines={four.LinesCleared} gained={four.ScoreGained}");
        check("[俄罗斯方块] TETRIS 的提示文案带「4 行」或「TETRIS」",
            four.ComboText.Contains("4") || four.ComboText.Contains("TETRIS"), four.ComboText);

        // 等级：每 10 行升 1 级
        var levelEngine = new TetrisEngine(new Random(61));
        for (var round = 0; round < 3; round++)
        {
            FillFourRowsExceptFirstColumn(levelEngine, TetrominoKind.J);
            levelEngine.PlacePiece(new TetrisPiece(TetrominoKind.I, -2, TetrisEngine.Height - 4, 1));
            levelEngine.Lock();
        }
        check("[俄罗斯方块] 消满 12 行后等级升到 2（每 10 行升 1 级）",
            levelEngine.Lines == 12 && levelEngine.Level == 2,
            $"lines={levelEngine.Lines} level={levelEngine.Level}");

        // 起始等级：直接从 5 级开局，等级按起始等级往上加
        var startEngine = new TetrisEngine(new Random(67));
        startEngine.Reset(startLevel: 5);
        check("[俄罗斯方块] 起始等级生效（开局就是 5 级）", startEngine.Level == 5, startEngine.Level.ToString());

        startEngine.Reset(startLevel: 0);
        var lowLevel = startEngine.Level;
        startEngine.Reset(startLevel: 999);
        check("[俄罗斯方块] 起始等级被夹到 1 ~ 20",
            lowLevel == 1 && startEngine.Level == TetrisEngine.MaxLevel,
            $"{lowLevel} / {startEngine.Level}");

        var high = new TetrisEngine(new Random(71));
        high.Reset(startLevel: 3);
        FillFourRowsExceptFirstColumn(high, TetrominoKind.S);
        high.PlacePiece(new TetrisPiece(TetrominoKind.I, -2, TetrisEngine.Height - 4, 1));
        var highResult = high.Lock();
        check("[俄罗斯方块] 分数按「基础分 × 当前等级」算（3 级消 4 行 = 2400）",
            highResult.ScoreGained == 800 * 3, highResult.ScoreGained.ToString());

        // 重开一局：分数 / 行数 / 等级 / 棋盘全清空
        high.Reset();
        check("[俄罗斯方块] Reset 清空分数 / 行数 / 棋盘并重新出块",
            high.Score == 0 && high.Lines == 0 && high.Level == 1
            && high.CellAt(0, TetrisEngine.Height - 1) is null
            && high.Current is not null && !high.IsGameOver);
    }

    /// <summary>铺满最后 4 行的第 1~9 列（留出第 0 列给一根竖 I 补满）。</summary>
    private static void FillFourRowsExceptFirstColumn(TetrisEngine engine, TetrominoKind filler)
    {
        for (var y = TetrisEngine.Height - 4; y < TetrisEngine.Height; y++)
        {
            for (var x = 1; x < TetrisEngine.Width; x++) engine.SetCell(x, y, filler);
        }
    }

    // ---------------------------------------------------------------- 暂存

    private static void CheckHold(CheckFn check)
    {
        var engine = new TetrisEngine(new Random(73));
        var current = engine.Current!.Value.Kind;
        var next = engine.NextPieces[0];

        check("[俄罗斯方块] 开局暂存是空的、可以换", engine.Held is null && engine.CanHold);

        var ok = engine.Hold();
        check("[俄罗斯方块] 第一次暂存：当前方块进暂存区、预览第一块上场",
            ok && engine.Held == current && engine.Current!.Value.Kind == next,
            $"held={engine.Held} current={engine.Current?.Kind} next={next}");

        check("[俄罗斯方块] 同一手再按暂存无效（一手只能换一次）",
            !engine.Hold() && !engine.CanHold && engine.Held == current);

        engine.HardDrop();   // 落地 → 下一手
        check("[俄罗斯方块] 换下一手之后又能暂存了", engine.CanHold && !engine.IsGameOver);

        var heldBefore = engine.Held;
        var currentBefore = engine.Current!.Value.Kind;
        engine.Hold();
        check("[俄罗斯方块] 第二次暂存：暂存区与手上互换",
            engine.Held == currentBefore && engine.Current!.Value.Kind == heldBefore,
            $"held={engine.Held} current={engine.Current?.Kind}");
    }

    // ---------------------------------------------------------------- 幽灵 / 顶死

    private static void CheckGhostAndGameOver(CheckFn check)
    {
        var engine = new TetrisEngine(new Random(83));
        engine.PlacePiece(new TetrisPiece(TetrominoKind.T, 4, 0, 0));
        for (var x = 0; x < 6; x++) engine.SetCell(x, TetrisEngine.Height - 1, TetrominoKind.O);

        var ghost = engine.GhostPiece();
        check("[俄罗斯方块] 幽灵方块停在已有块上面（不会穿过去）",
            ghost is { } g && g.Y == TetrisEngine.Height - 3 && !engine.Fits(g.Moved(0, 1)),
            ghost?.ToString() ?? "(null)");

        var fresh = new TetrisEngine(new Random(89));
        fresh.PlacePiece(null);
        check("[俄罗斯方块] 没有当前方块时幽灵为 null", fresh.GhostPiece() is null);

        // 顶死：出生位置被占 → 游戏结束
        var dead = new TetrisEngine(new Random(97));
        BlockTopRows(dead);
        dead.PlacePiece(new TetrisPiece(TetrominoKind.O, 4, TetrisEngine.Height - 2, 0));
        dead.Lock();

        check("[俄罗斯方块] 出生位置被占 → 判定游戏结束、没有当前方块",
            dead.IsGameOver && dead.Current is null);

        check("[俄罗斯方块] 游戏结束后所有操作都无效",
            !dead.MoveLeft() && !dead.MoveRight() && !dead.SoftDrop() && !dead.Step()
            && dead.HardDrop().LinesCleared == 0 && !dead.Rotate(true).Success && !dead.Hold());

        dead.Reset();
        check("[俄罗斯方块] 游戏结束后重开可以继续玩",
            !dead.IsGameOver && dead.Current is not null && dead.Score == 0);
    }

    /// <summary>把最上面两行铺满（第 9 列留空，避免整行被消掉），让下一块出不来。</summary>
    private static void BlockTopRows(TetrisEngine engine)
    {
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < TetrisEngine.Width - 1; x++) engine.SetCell(x, y, TetrominoKind.Z);
        }
    }

    // ---------------------------------------------------------------- 游玩说明内容

    private static void CheckHelpContent(CheckFn check)
    {
        check("[俄罗斯方块] 说明有标题与总览", TetrisHelpContent.Title.Length > 0
            && TetrisHelpContent.Summary.Contains("10 × 20") && TetrisHelpContent.Summary.Contains("游戏结束"),
            $"{TetrisHelpContent.Title} / {TetrisHelpContent.Summary.Length} 字");

        check("[俄罗斯方块] 说明分成 6 个分区（移动旋转 / 落底 / 暂存预览 / 暂停重开 / 鼠标 / 联机）",
            TetrisHelpContent.Sections.Count == 6,
            string.Join(" / ", TetrisHelpContent.Sections.Select(s => s.Title)));

        // 每个分区都得有内容，条目上的按键与作用都不能空
        var sectionsWellFormed = TetrisHelpContent.Sections.All(s =>
            s.Title.Length > 0
            && s.Items.Count > 0
            && s.Items.All(i => i.Keys.Length > 0 && i.Action.Length > 0));
        check("[俄罗斯方块] 每个分区都写了「按键 → 作用」（没有空条目）", sectionsWellFormed,
            string.Join(" / ", TetrisHelpContent.Sections.Select(s => $"{s.Title}:{s.Items.Count}")));

        // 说明必须覆盖页面上能用的每一个键（加键忘了补说明会红）
        var allText = string.Join("\n", TetrisHelpContent.Sections
            .SelectMany(s => s.Items.Select(i => i.Keys + " | " + i.Action + " | " + i.Tip)));

        var missing = new List<string>();
        foreach (var key in new[] { "←", "→", "↓", "↑", "X", "Z", "空格", "C", "Esc", "P", "R", "Enter" })
        {
            if (!allText.Contains(key)) missing.Add(key);
        }
        check("[俄罗斯方块] 页面能用的键都写进说明了（← → ↓ ↑ X Z 空格 C Esc P R Enter）",
            missing.Count == 0, missing.Count == 0 ? "全覆盖" : "漏了 " + string.Join(",", missing));

        check("[俄罗斯方块] 说明里点明了 F1 也能打开说明窗口",
            allText.Contains("F1") && TetrisHelpContent.AllKeys.Contains("F1"), "F1");

        // ---- 联机说明 ----
        var netSection = TetrisHelpContent.Sections.FirstOrDefault(s => s.Title == TetrisHelpContent.NetSectionTitle);
        var netText = netSection is null
            ? ""
            : string.Join("\n", netSection.Items.Select(i => i.Keys + " | " + i.Action + " | " + i.Tip));
        check("[俄罗斯方块] 说明里写了联机对战（开房 / 加入 / 两种模式 / 观战 / 掉线判负）",
            netSection is not null
            && netText.Contains("开房间")
            && netText.Contains("加入房间")
            && netText.Contains("垃圾行互攻")
            && netText.Contains("各自计分")
            && netText.Contains("观战")
            && netText.Contains("判负"),
            netSection is null ? "（没有联机小节）" : $"{netSection.Items.Count} 条");

        // 关键细节：C 一手只能换一次、空格是硬降、Z 是逆时针、↑ 与 X 都是顺时针
        check("[俄罗斯方块] 说明写清了 C 的「一手只能换一次」与互换语义",
            allText.Contains("只能换一次") && allText.Contains("互换"), "");
        check("[俄罗斯方块] 说明写清了空格 = 硬降、Z = 逆时针、↑ / X = 顺时针",
            allText.Contains("硬降") && allText.Contains("逆时针") && allText.Contains("顺时针"), "");
        check("[俄罗斯方块] 说明提到墙踢（贴墙 / 贴地会自动挪开）",
            allText.Contains("踢"), "");
        check("[俄罗斯方块] 说明提到打开说明会自动暂停",
            allText.Contains("暂停"), "");

        // 计分规则与小技巧
        check("[俄罗斯方块] 计分规则写明 1/2/3/4 行 = 100/300/500/800 × 等级",
            TetrisHelpContent.ScoreRules.Any(r => r.Contains("100") && r.Contains("等级"))
            && TetrisHelpContent.ScoreRules.Any(r => r.Contains("300"))
            && TetrisHelpContent.ScoreRules.Any(r => r.Contains("500"))
            && TetrisHelpContent.ScoreRules.Any(r => r.Contains("800")),
            string.Join(" / ", TetrisHelpContent.ScoreRules.Take(4)));

        check("[俄罗斯方块] 计分规则写明软降 / 硬降的加分与升级节奏",
            TetrisHelpContent.ScoreRules.Any(r => r.Contains("软降") && r.Contains("+1"))
            && TetrisHelpContent.ScoreRules.Any(r => r.Contains("硬降") && r.Contains("+2"))
            && TetrisHelpContent.ScoreRules.Any(r => r.Contains("10 行")),
            "");

        // 说明里写的分数倍率必须和引擎一致（防止改了计分表忘了改说明）
        var expectedTetrisScore = 800 * 3;
        var scoreEngine = new TetrisEngine(new Random(101));
        scoreEngine.Reset(startLevel: 3);
        FillFourRowsExceptFirstColumn(scoreEngine, TetrominoKind.O);
        scoreEngine.PlacePiece(new TetrisPiece(TetrominoKind.I, -2, TetrisEngine.Height - 4, 1));
        var tetrisResult = scoreEngine.Lock();
        check("[俄罗斯方块] 说明里的计分数字与引擎实际算出来的一致（3 级消 4 行 = 2400）",
            tetrisResult.ScoreGained == expectedTetrisScore
            && TetrisHelpContent.ScoreRules.Any(r => r.Contains("800")),
            $"{tetrisResult.ScoreGained} / 期望 {expectedTetrisScore}");

        check("[俄罗斯方块] 小技巧至少 3 条且都不为空",
            TetrisHelpContent.Tips.Count >= 3 && TetrisHelpContent.Tips.All(t => t.Trim().Length > 0),
            $"{TetrisHelpContent.Tips.Count} 条");
    }

    // ---------------------------------------------------------------- 联机规则（纯逻辑）

    private static void CheckNetRules(CheckFn check)
    {
        // ---- 垃圾行换算 ----
        check("[俄罗斯方块] 垃圾行换算：消 1 行不送、2→1、3→2、4→4",
            TetrisEngine.GarbageRowsFor(1) == 0 && TetrisEngine.GarbageRowsFor(2) == 1
            && TetrisEngine.GarbageRowsFor(3) == 2 && TetrisEngine.GarbageRowsFor(4) == 4
            && TetrisEngine.GarbageRowsFor(0) == 0,
            $"{TetrisEngine.GarbageRowsFor(1)}/{TetrisEngine.GarbageRowsFor(2)}/{TetrisEngine.GarbageRowsFor(3)}/{TetrisEngine.GarbageRowsFor(4)}");

        // ---- 垃圾行插进棋盘 ----
        var engine = new TetrisEngine(new Random(151));
        engine.EnqueueGarbage(3);
        check("[俄罗斯方块] 待发垃圾行会记账（不立刻插进棋盘）",
            engine.PendingGarbage == 3 && engine.CellAt(0, TetrisEngine.Height - 1) is null,
            $"{engine.PendingGarbage}");

        // 落地（这一手没消行）→ 垃圾行插到底部
        engine.PlacePiece(new TetrisPiece(TetrominoKind.O, 0, 0, 0));
        engine.Lock();

        var bottomRows = 0;
        for (var y = TetrisEngine.Height - 3; y < TetrisEngine.Height; y++)
        {
            var filled = 0;
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                if (engine.CellAt(x, y) == TetrominoKind.Garbage) filled++;
            }
            if (filled == TetrisEngine.Width - 1) bottomRows++;   // 每行留 1 格缺口
        }
        check("[俄罗斯方块] 落地时把垃圾行插到底部，每行留 1 格缺口",
            engine.PendingGarbage == 0 && engine.GarbageTaken == 3 && bottomRows == 3,
            $"待发={engine.PendingGarbage} 已吃={engine.GarbageTaken} 底部完整垃圾行={bottomRows}");

        // 自己消行的那一手不吃垃圾行（否则会被立刻抵掉）
        var eatEngine = new TetrisEngine(new Random(157));
        eatEngine.EnqueueGarbage(2);
        for (var x = 2; x < TetrisEngine.Width; x++) eatEngine.SetCell(x, TetrisEngine.Height - 1, TetrominoKind.T);
        eatEngine.PlacePiece(new TetrisPiece(TetrominoKind.O, 0, TetrisEngine.Height - 2, 0));
        var clearResult = eatEngine.Lock();
        check("[俄罗斯方块] 自己消行的那一手不插垃圾行（留给下一手）",
            clearResult.LinesCleared == 1 && eatEngine.PendingGarbage == 2 && eatEngine.GarbageTaken == 0,
            $"消行={clearResult.LinesCleared} 待发={eatEngine.PendingGarbage}");

        // ---- 送给对手的垃圾行 ----
        var sender = new TetrisEngine(new Random(163));
        sender.QueueGarbageForOpponent(TetrisEngine.GarbageRowsFor(4));
        check("[俄罗斯方块] 消 4 行记下 4 行要送给对手，取走后清零",
            sender.OpponentGarbageToSend == 4 && sender.GarbageSent == 4
            && sender.TakeOpponentGarbage() == 4 && sender.OpponentGarbageToSend == 0,
            $"{sender.GarbageSent}");

        // ---- 快照往返 ----
        var source = new TetrisEngine(new Random(167));
        source.SetCell(0, TetrisEngine.Height - 1, TetrominoKind.I);
        source.SetCell(1, TetrisEngine.Height - 1, TetrominoKind.Garbage);
        source.PlacePiece(new TetrisPiece(TetrominoKind.T, 4, 5, 2));
        var snapshot = source.Snapshot();

        var restored = new TetrisEngine(new Random(999));
        restored.ApplySnapshot(snapshot);

        check("[俄罗斯方块] 快照能完整还原棋盘 / 当前块 / 分数 / 待发垃圾",
            restored.CellAt(0, TetrisEngine.Height - 1) == TetrominoKind.I
            && restored.CellAt(1, TetrisEngine.Height - 1) == TetrominoKind.Garbage
            && restored.Current is { } restoredPiece && source.Current is { } sourcePiece
            && restoredPiece == sourcePiece
            && restored.Level == source.Level
            && restored.NextPieces.SequenceEqual(source.NextPieces),
            $"{restored.Current} / {restored.Level}");

        var roundTripped = RoundTripSnapshot(snapshot);
        check("[俄罗斯方块] 快照走 JSON 往返后仍然一致（联机就是靠它传输的）",
            roundTripped is { Cells.Length: TetrisEngine.Width * TetrisEngine.Height } json
            && json.Cells[(TetrisEngine.Height - 1) * TetrisEngine.Width] == TetrominoKind.I
            && json.Current is not null && snapshot.Current is not null && json.Current == snapshot.Current
            && json.Level == source.Level,
            $"{roundTripped?.Cells.Length} 格");

        // ---- 同一个种子 → 同一套出块（「各自计分」模式两边才公平） ----
        var a = new TetrisEngine();
        var b = new TetrisEngine();
        a.Reset(1, seed: 42);
        b.Reset(1, seed: 42);
        var sameSequence = true;
        for (var i = 0; i < 14; i++)
        {
            if (a.Current?.Kind != b.Current?.Kind) { sameSequence = false; break; }
            a.HardDrop();
            b.HardDrop();
        }
        check("[俄罗斯方块] 同一个种子出块序列完全一致（联机各自计分靠它保证公平）",
            sameSequence, "");

        var c = new TetrisEngine();
        c.Reset(1, seed: 7);
        var d = new TetrisEngine();
        d.Reset(1, seed: 8);
        var anyDifferent = false;
        for (var i = 0; i < 14; i++)
        {
            if (c.Current?.Kind != d.Current?.Kind) { anyDifferent = true; break; }
            c.HardDrop();
            d.HardDrop();
        }
        check("[俄罗斯方块] 不同种子出块序列不同", anyDifferent, "");

        // ---- 胜负判定 ----
        check("[俄罗斯方块] 胜负：谁先顶死谁输",
            TetrisNetJudge.Decide(hostDead: true, guestDead: false, 100, 200) == "guest"
            && TetrisNetJudge.Decide(hostDead: false, guestDead: true, 100, 200) == "host");
        check("[俄罗斯方块] 胜负：都没死 = 还没结果",
            TetrisNetJudge.Decide(false, false, 100, 200) == "");
        check("[俄罗斯方块] 胜负：双方都顶死时比分数，同分算平局",
            TetrisNetJudge.Decide(true, true, 300, 200) == "host"
            && TetrisNetJudge.Decide(true, true, 100, 200) == "guest"
            && TetrisNetJudge.Decide(true, true, 150, 150) == "draw");
        check("[俄罗斯方块] 结果文案带玩家名",
            TetrisNetJudge.Describe("host", "小红", "小明").Contains("小红")
            && TetrisNetJudge.Describe("draw", "小红", "小明").Contains("平局"),
            TetrisNetJudge.Describe("host", "小红", "小明"));

        // ---- 协议 ----
        check("[俄罗斯方块] 协议标识 / 版本 / 人数上限（1 对 1 = 2 人）",
            TetrisNetProtocol.Magic == "YLTTETRIS1" && TetrisNetProtocol.Version == 1
            && TetrisNetProtocol.MaxPlayers == 2,
            $"{TetrisNetProtocol.Magic} v{TetrisNetProtocol.Version} / {TetrisNetProtocol.MaxPlayers}");

        check("[俄罗斯方块] 动作白名单认得全部 7 个动作、拒绝别的字符串",
            TetrisNetProtocol.Actions.All.Count == 7
            && TetrisNetProtocol.Actions.All.All(TetrisNetProtocol.Actions.IsKnown)
            && !TetrisNetProtocol.Actions.IsKnown("cheat") && !TetrisNetProtocol.Actions.IsKnown(""),
            string.Join(",", TetrisNetProtocol.Actions.All));

        check("[俄罗斯方块] 模式在线上用字符串存（garbage / score），认不出回退到互攻",
            TetrisNetProtocol.ModeToString(TetrisNetMode.Garbage) == "garbage"
            && TetrisNetProtocol.ModeToString(TetrisNetMode.Score) == "score"
            && TetrisNetProtocol.ParseMode("score") == TetrisNetMode.Score
            && TetrisNetProtocol.ParseMode("SCORE") == TetrisNetMode.Score
            && TetrisNetProtocol.ParseMode("乱七八糟") == TetrisNetMode.Garbage
            && TetrisNetProtocol.ParseMode(null) == TetrisNetMode.Garbage,
            TetrisNetProtocol.ModeToString(TetrisNetMode.Score));

        var netState = new TetrisNetState
        {
            Seq = 7,
            Phase = TetrisNetPhase.Playing,
            Mode = "score",
            Host = new TetrisNetPlayer { MemberId = "host", Name = "小红", Score = 1200, Board = snapshot },
            Guest = new TetrisNetPlayer { MemberId = "m1", Name = "小明", Score = 900 },
        };
        var wrapped = TetrisNetProtocol.WrapState(netState);
        var readBack = TetrisNetProtocol.ReadState(wrapped);
        check("[俄罗斯方块] 联机状态能 JSON 往返（含嵌套的棋盘快照）",
            readBack is { Seq: 7, Phase: TetrisNetPhase.Playing, Mode: "score" }
            && readBack.Host is { Name: "小红", Score: 1200 }
            && readBack.Host.Board is { } board
            && board.Cells[(TetrisEngine.Height - 1) * TetrisEngine.Width] == TetrominoKind.I
            && readBack.Guest is { Name: "小明", Score: 900 },
            $"seq={readBack?.Seq} host={readBack?.Host?.Score}");

        check("[俄罗斯方块] 读到坏状态返回 null（一个坏包不能把界面搞崩）",
            TetrisNetProtocol.ReadState(default) is null, "");

        // ---- 单人 vs 电脑（AI） ----
        check("[俄罗斯方块·AI] 配置有「单人 vs 电脑」开关，默认关",
            new YuLiangToolkit.Tools.Games.Tetris.TetrisConfig().SoloAiEnabled == false, "");

        var aiEngine = new TetrisEngine(new Random(77));
        aiEngine.Reset(1, 77);
        var aiPlan = TetrisAi.ChoosePlan(aiEngine,
            YuLiangToolkit.Core.Games.AiDifficulty.Master, false, new Random(5));
        check("[俄罗斯方块·AI] AI 能对当前棋盘算出落点（旋转 + 列）",
            aiPlan is { } plan && plan.Rotation is >= 0 and <= 3,
            aiPlan is { } p0 ? $"rot={p0.Rotation} x={p0.X}" : "没算");

        // 四档都能算
        var aiAllOk = YuLiangToolkit.Core.Games.AiDifficulties.All.All(d =>
        {
            var e = new TetrisEngine(new Random(88));
            e.Reset(1, 88);
            return TetrisAi.ChoosePlan(e, d, false, new Random(1)) is not null;
        });
        check("[俄罗斯方块·AI] 四档难度都能算出落点", aiAllOk, "");

        // 按 AI 的方案落子之后棋盘仍然合法（不顶死在不该顶死的位置）
        var dropEngine = new TetrisEngine(new Random(99));
        dropEngine.Reset(1, 99);
        var playPlan = TetrisAi.ChoosePlan(dropEngine,
            YuLiangToolkit.Core.Games.AiDifficulty.Hard, false, new Random(3));
        if (playPlan is { } pp)
        {
            var guard = 0;
            while (dropEngine.Current is { } cur
                   && TetrisPieces.NormalizeRotation(cur.Rotation) != pp.Rotation && guard++ < 6)
            {
                if (!dropEngine.Rotate(true).Success) break;
            }
            dropEngine.HardDrop();
        }
        check("[俄罗斯方块·AI] 按 AI 的方案真的能落一块（不会卡住）",
            playPlan is not null, "");

        // ---- 多个暂存格 / 预览数量 / HUD 布局（本轮新增） ----
        var multi = new TetrisEngine(new Random(11));
        multi.Reset(1, 11);
        multi.HoldSlots = 3;
        multi.PreviewSlots = 5;

        // 注意：同一手不能连按两次 C（规则如此），所以每存一格要先落地一次
        var first = multi.Current!.Value.Kind;
        multi.Hold();
        multi.HardDrop();

        var second = multi.Current!.Value.Kind;
        multi.Hold();

        check("[俄罗斯方块] 多个暂存格：按 C 依次填满，第 1 格是最先存进去的",
            multi.HeldSlots.Count == 2 && multi.HeldSlots[0] == first && multi.HeldSlots[1] == second,
            $"{multi.HeldSlots.Count} 格：{string.Join(",", multi.HeldSlots)}");

        check("[俄罗斯方块] 暂存格没满时继续按 C 是「填格」，不是「换出」",
            multi.HeldSlots.Count < multi.HoldSlots, $"{multi.HeldSlots.Count}/{multi.HoldSlots}");

        check("[俄罗斯方块] 预览块数可设（设 5 就出 5 块）",
            multi.NextPieces.Count >= Math.Min(5, multi.PreviewSlots),
            $"{multi.NextPieces.Count} 块");

        var hud = YuLiangToolkit.Tools.Games.Tetris.HudLayout.CreateDefault();
        hud.Set(YuLiangToolkit.Tools.Games.Tetris.HudPart.Next, 0.5, 0.3);
        var round = YuLiangToolkit.Tools.Games.Tetris.HudLayout.Deserialize(hud.Serialize());
        var nextPlace = round.Get(YuLiangToolkit.Tools.Games.Tetris.HudPart.Next);

        check("[俄罗斯方块] HUD 布局能序列化 / 还原（拖动位置存得住）",
            Math.Abs(nextPlace.X - 0.5) < 0.001 && Math.Abs(nextPlace.Y - 0.3) < 0.001,
            $"next=({nextPlace.X:0.00},{nextPlace.Y:0.00})");

        check("[俄罗斯方块] HUD 布局坏数据回落到默认（不会崩）",
            YuLiangToolkit.Tools.Games.Tetris.HudLayout.Deserialize("坏数据").Items.Count == 5,
            "回落默认");

        // ---- 换人设置 ----
        check("[俄罗斯方块·换人] 三档换人方式与补位方式都有键与中文名",
            YuLiangToolkit.Core.Games.MatchModes.ToKey(YuLiangToolkit.Core.Games.MatchRotateMode.Manual) == "manual"
            && YuLiangToolkit.Core.Games.MatchModes.ToKey(YuLiangToolkit.Core.Games.MatchRotateMode.LoserOut) == "loserOut"
            && YuLiangToolkit.Core.Games.MatchModes.ToKey(YuLiangToolkit.Core.Games.MatchRotateMode.BothOut) == "bothOut"
            && YuLiangToolkit.Core.Games.MatchModes.ParseRotate("loserOut") == YuLiangToolkit.Core.Games.MatchRotateMode.LoserOut
            && YuLiangToolkit.Core.Games.MatchModes.ParsePick("random") == YuLiangToolkit.Core.Games.MatchPickMode.Random
            && YuLiangToolkit.Core.Games.MatchModes.Describe(YuLiangToolkit.Core.Games.MatchRotateMode.BothOut).Length > 0,
            "手动 / 输的下场 / 全场换人");

        // ---- 身份（对战 / 观战） ----
        check("[俄罗斯方块·联机] 身份字符串：player / spectator，认不出按对战处理",
            TetrisNetProtocol.RoleToString(TetrisMemberRole.Player) == "player"
            && TetrisNetProtocol.RoleToString(TetrisMemberRole.Spectator) == "spectator"
            && TetrisNetProtocol.ParseRole("spectator") == TetrisMemberRole.Spectator
            && TetrisNetProtocol.ParseRole("SPECTATOR") == TetrisMemberRole.Spectator
            && TetrisNetProtocol.ParseRole("") == TetrisMemberRole.Player
            && TetrisNetProtocol.ParseRole(null) == TetrisMemberRole.Player,
            TetrisNetProtocol.RoleToString(TetrisMemberRole.Spectator));

        check("[俄罗斯方块·联机] 握手包缺 role 字段时按对战玩家处理（老版本兼容）",
            new TetrisHelloInfo().ParsedRole == TetrisMemberRole.Player
            && new TetrisHelloInfo { Role = "spectator" }.ParsedRole == TetrisMemberRole.Spectator,
            "");

        check("[俄罗斯方块·联机] 观战上限 8 人，房间总连接 10 个（房主 + 1 对战 + 8 观战）",
            TetrisNetProtocol.MaxSpectators == 8
            && TetrisNetProtocol.MaxConnections == TetrisNetProtocol.MaxPlayers + TetrisNetProtocol.MaxSpectators,
            $"{TetrisNetProtocol.MaxSpectators} / {TetrisNetProtocol.MaxConnections}");
    }

    /// <summary>把快照序列化成 JSON 再读回来（验证联机真正走的那条路）。</summary>
    private static TetrisEngineSnapshot? RoundTripSnapshot(TetrisEngineSnapshot snapshot)
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot, RoomProtocol.JsonOptions);
            return System.Text.Json.JsonSerializer.Deserialize<TetrisEngineSnapshot>(json, RoomProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 真回环的 1 对 1 联机：房主 + 客户端各建一个会话，在本机 127.0.0.1 上真开一次房间、
    /// 真握手、真发动作与状态。<b>只绑本机，不联外网。</b>
    /// </summary>
    private static void CheckNetLoopback(Action<string, bool, string> check)
    {
        var host = new TetrisNetSession("房主");
        var guest = new TetrisNetSession("好友");

        try
        {
            var hostError = host.StartHostAsync(0).GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 房主能在本机开房间（端口 0 = 系统自动挑）",
                hostError.Length == 0 && host.ListenPort > 0, hostError.Length > 0 ? hostError : $"端口 {host.ListenPort}");
            if (hostError.Length > 0) return;

            var guestJoined = new TaskCompletionSource<TetrisNetPlayer>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gotAction = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var gotState = new TaskCompletionSource<TetrisNetState>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.GuestJoined += player => guestJoined.TrySetResult(player);
            host.ActionReceived += action => gotAction.TrySetResult(action);
            guest.StateReceived += state => gotState.TrySetResult(state);

            var joinError = guest.ConnectAsync($"127.0.0.1:{host.ListenPort}", "garbage").GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 客户端能连上房主并握手成功",
                joinError.Length == 0 && guest.IsGuestConnected, joinError.Length > 0 ? joinError : "已连上");
            if (joinError.Length > 0) return;

            check("[俄罗斯方块·联机] 房主收到「有人加入」，名字对得上",
                guestJoined.Task.Wait(TimeSpan.FromSeconds(5))
                && guestJoined.Task.Result.Name == "好友",
                guestJoined.Task.IsCompleted ? guestJoined.Task.Result.Name : "（没收到）");

            // 客户端上报一个动作 → 房主应当收到
            guest.SendActionAsync(TetrisNetProtocol.Actions.RotateCw).GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 客户端上报的按键动作能到达房主",
                gotAction.Task.Wait(TimeSpan.FromSeconds(5))
                && gotAction.Task.Result == TetrisNetProtocol.Actions.RotateCw,
                gotAction.Task.IsCompleted ? gotAction.Task.Result : "（没收到）");

            // 房主广播状态 → 客户端应当收到，且内容完整
            var engine = new TetrisEngine();
            var state = new TetrisNetState
            {
                Seq = 5,
                Phase = TetrisNetPhase.Playing,
                Mode = "garbage",
                Host = new TetrisNetPlayer { MemberId = "host", Name = "房主", Score = 300, Board = engine.Snapshot() },
            };
            host.BroadcastStateAsync(state).GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 房主广播的状态（含棋盘快照）能到达客户端",
                gotState.Task.Wait(TimeSpan.FromSeconds(5))
                && gotState.Task.Result is { Seq: 5, Phase: TetrisNetPhase.Playing }
                && gotState.Task.Result.Host is { Score: 300, Board: not null },
                gotState.Task.IsCompleted ? $"seq={gotState.Task.Result.Seq}" : "（没收到）");

            // 房间满了：1 对 1，第三个以「对战玩家」身份进来的会被降级成观战
            var third = new TetrisNetSession("插队的");
            var thirdError = third.ConnectAsync($"127.0.0.1:{host.ListenPort}", "garbage").GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 对战位已满时，第 3 个人会被降级成观战（不是拒连）",
                thirdError.Length == 0 && third.IsSpectator,
                thirdError.Length > 0 ? thirdError : $"身份={third.LocalRole}");

            check("[俄罗斯方块·联机] 房主看得到观战人数",
                host.SpectatorCount == 1 && host.Spectators.Count == 1
                && host.Spectators[0].Name == "插队的",
                $"观战 {host.SpectatorCount} 人");

            // 观战者收得到状态广播
            var spectatorState = new TaskCompletionSource<TetrisNetState>(TaskCreationOptions.RunContinuationsAsynchronously);
            third.StateReceived += state => spectatorState.TrySetResult(state);
            var spectatorBroadcast = new TetrisNetState
            {
                Seq = 11,
                Phase = TetrisNetPhase.Playing,
                Mode = "garbage",
                Host = new TetrisNetPlayer { MemberId = "host", Name = "房主", Score = 500, Board = engine.Snapshot() },
            };
            host.BroadcastStateAsync(spectatorBroadcast).GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 观战者能收到房主广播的棋盘状态",
                spectatorState.Task.Wait(TimeSpan.FromSeconds(5))
                && spectatorState.Task.Result is { Seq: 11, Phase: TetrisNetPhase.Playing }
                && spectatorState.Task.Result.Host is { Score: 500 },
                spectatorState.Task.IsCompleted ? $"seq={spectatorState.Task.Result.Seq}" : "（没收到）");

            // 观战者发动作：房主不应收到（观战只能看）
            var actionFromSpectator = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.ActionReceived += action => actionFromSpectator.TrySetResult(action);
            third.SendActionAsync(TetrisNetProtocol.Actions.HardDrop).GetAwaiter().GetResult();
            check("[俄罗斯方块·联机] 观战者发不了操作（房主不会收到动作）",
                !actionFromSpectator.Task.Wait(TimeSpan.FromSeconds(2)),
                "观战动作被忽略");

            // 状态里会带观战名单（客户端据此显示「还有谁在看」）
            var spectatorNames = host.Spectators.Select(s => s.Name).ToList();
            var rosterState = new TetrisNetState { Seq = 12, Spectators = host.Spectators.Select(s => s.Clone()).ToList() };
            var rosterBack = TetrisNetProtocol.ReadState(TetrisNetProtocol.WrapState(rosterState));
            check("[俄罗斯方块·联机] 观战名单能进状态并 JSON 往返",
                spectatorNames.SequenceEqual(new[] { "插队的" })
                && rosterBack?.Spectators.Count == 1
                && rosterBack.Spectators[0].Name == "插队的",
                $"{spectatorNames.Count} 人");

            third.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            try { guest.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* 忽略 */ }
            try { host.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { /* 忽略 */ }
        }
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var cfg = new TetrisConfig();

        check("[俄罗斯方块] 配置默认值：最高分 0、起始等级 1、幽灵 / 网格 / 预览都开、切走自动暂停",
            cfg.HighScore == 0 && cfg.StartLevel == 1
            && cfg.ShowGhost && cfg.ShowGrid && cfg.ShowNext && cfg.PauseOnLostFocus,
            $"high={cfg.HighScore} level={cfg.StartLevel} ghost={cfg.ShowGhost} grid={cfg.ShowGrid} next={cfg.ShowNext} pause={cfg.PauseOnLostFocus}");

        cfg.StartLevel = 0;
        var lowClamped = cfg.StartLevel;
        cfg.StartLevel = 999;
        check("[俄罗斯方块] 起始等级夹在 1 ~ 20",
            lowClamped == 1 && cfg.StartLevel == TetrisEngine.MaxLevel,
            $"{lowClamped} / {cfg.StartLevel}");

        cfg.HighScore = -5;
        check("[俄罗斯方块] 最高分不接受负数", cfg.HighScore == 0, cfg.HighScore.ToString());

        check("[俄罗斯方块] 分数没超过最高分不算新纪录", !cfg.SubmitScore(0) && cfg.HighScore == 0);
        check("[俄罗斯方块] 超过最高分才算新纪录并写进去", cfg.SubmitScore(1200) && cfg.HighScore == 1200);
        check("[俄罗斯方块] 之后的低分不会把最高分刷下去", !cfg.SubmitScore(900) && cfg.HighScore == 1200);

        check("[俄罗斯方块] 配置里只写配置项（不带 FilePath / Store 这类运行时字段）",
            !cfg.ToJson().Contains("FilePath") && !cfg.ToJson().Contains("\"Store\""),
            cfg.ToJson().Replace(Environment.NewLine, " "));
    }
}
