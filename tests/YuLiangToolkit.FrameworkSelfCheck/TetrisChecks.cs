using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Games.Tetris;

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
