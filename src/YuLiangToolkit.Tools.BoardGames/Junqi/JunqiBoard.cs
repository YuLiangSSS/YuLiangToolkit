using System.Collections.Generic;

namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>格子类型（决定能不能停、怎么走）。</summary>
public enum JunqiCellKind
{
    /// <summary>普通兵站（铁路以外的格子）。</summary>
    Post = 0,

    /// <summary>铁路（公路 + 铁路，可以直线连续走）。</summary>
    Railway = 1,

    /// <summary>行营（进去的棋子不能被攻击；一格只能停一枚）。</summary>
    Camp = 2,

    /// <summary>大本营（军旗放这里；进去的棋子不能再动）。</summary>
    Headquarters = 3,
}

/// <summary>
/// 二人军棋棋盘（标准陆战棋）：<b>12 列 × 5 行</b>，两侧各 6 列（自己 6 列 + 对方 6 列），
/// 中间是「行营 + 铁路 + 大本营」区域。
///
/// <para><b>坐标</b>：<c>X</c> 0~11（红方在左 0~5，蓝方在右 6~11），<c>Y</c> 0~4。
/// 下标 = <c>Y * Width + X</c>。</para>
///
/// <para><b>布局要点</b>（标准陆战棋）：
/// 每方 30 个格子 = 25 枚棋子 + 5 个空位（3 个行营 + 2 个…… 实际是 5 行 × 6 列 = 30 格，
/// 其中每方有 <b>3 个行营 + 2 个大本营</b>，所以可放棋子的位置是 25 个，正好放 25 枚棋子）。
/// 铁路：每方两条横向铁路（第 1 行与第 3 行），以及贯通全盘的竖向铁路（第 1、5 列在己方一侧）。</para>
/// </summary>
public static class JunqiBoard
{
    /// <summary>棋盘的列数（12）。</summary>
    public const int Width = 12;

    /// <summary>棋盘的行数（5）。</summary>
    public const int Height = 5;

    /// <summary>格子总数。</summary>
    public const int CellCount = Width * Height;

    /// <summary>一方的列数（6）。</summary>
    public const int SideWidth = 6;

    /// <summary>坐标是否在棋盘内。</summary>
    public static bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    /// <summary>某种格子的布局表（静态构造一次，之后只读）。</summary>
    public static IReadOnlyList<JunqiCellKind> Cells { get; } = BuildCells();

    /// <summary>取某格的类型（越界返回普通兵站）。</summary>
    public static JunqiCellKind KindAt(int x, int y)
        => InBounds(x, y) ? Cells[y * Width + x] : JunqiCellKind.Post;

    /// <summary>这个坐标属于哪一方（红 = 左半，蓝 = 右半；中线归各自那一侧）。</summary>
    public static JunqiSide HomeSideOf(int x) => x < SideWidth ? JunqiSide.Red : JunqiSide.Blue;

    /// <summary>某一方的格子是否属于它自己（不能把棋子摆到对面去）。</summary>
    public static bool BelongsTo(JunqiSide side, int x) => HomeSideOf(x) == side;

    /// <summary>坐标的偏移量（<c>y * Width + x</c>）。</summary>
    public static int IndexOf(int x, int y) => y * Width + x;

    /// <summary>索引反解成坐标。</summary>
    public static (int X, int Y) ToPoint(int index) => (index % Width, index / Width);

    /// <summary>两个格子是不是同一条横 / 竖线上的邻居（公路相邻）。</summary>
    public static bool AreAdjacent(int x1, int y1, int x2, int y2)
        => Math.Abs(x1 - x2) + Math.Abs(y1 - y2) == 1;

    /// <summary>
    /// 走铁路时能不能沿着某条直线一直走（工兵可以拐弯，其它棋子只能走直线）。
    /// </summary>
    public static bool IsRailway(int x, int y) => KindAt(x, y) == JunqiCellKind.Railway;

    /// <summary>构造布局：先全普通兵站，再按标准陆战棋标记铁路 / 行营 / 大本营。</summary>
    private static List<JunqiCellKind> BuildCells()
    {
        var cells = new List<JunqiCellKind>(CellCount);
        for (var i = 0; i < CellCount; i++) cells.Add(JunqiCellKind.Post);

        // ---- 行营：每方 5 个（标准的十字形：第二行的 1、3、5 位 + 第三行的 2、4 位）----
        // 红方（左 6 列，x 0~5）：(1,1) (3,1) (5,1) (2,2) (4,2)
        Set(cells, 1, 1, JunqiCellKind.Camp);
        Set(cells, 3, 1, JunqiCellKind.Camp);
        Set(cells, 5, 1, JunqiCellKind.Camp);
        Set(cells, 2, 2, JunqiCellKind.Camp);
        Set(cells, 4, 2, JunqiCellKind.Camp);

        // 蓝方镜像（右 6 列，x 6~11）
        Set(cells, 10, 1, JunqiCellKind.Camp);
        Set(cells, 8, 1, JunqiCellKind.Camp);
        Set(cells, 6, 1, JunqiCellKind.Camp);
        Set(cells, 9, 2, JunqiCellKind.Camp);
        Set(cells, 7, 2, JunqiCellKind.Camp);

        // ---- 大本营：每方 2 个（最后一行靠内的两个角）----
        Set(cells, 1, 4, JunqiCellKind.Headquarters);
        Set(cells, 3, 4, JunqiCellKind.Headquarters);
        Set(cells, 10, 4, JunqiCellKind.Headquarters);
        Set(cells, 8, 4, JunqiCellKind.Headquarters);

        // ---- 铁路：每方两条横线（第 1 行、第 3 行）整行贯通 ----
        for (var x = 0; x < Width; x++)
        {
            if (KindAt(cells, x, 1) != JunqiCellKind.Camp) Set(cells, x, 1, JunqiCellKind.Railway);
            if (KindAt(cells, x, 3) != JunqiCellKind.Camp) Set(cells, x, 3, JunqiCellKind.Railway);
        }

        // ---- 铁路：竖向四条干线（第 0 列、第 5 列、第 6 列、第 11 列贯穿全盘）----
        for (var y = 0; y < Height; y++)
        {
            if (KindAt(cells, 0, y) != JunqiCellKind.Camp) Set(cells, 0, y, JunqiCellKind.Railway);
            if (KindAt(cells, 5, y) != JunqiCellKind.Camp) Set(cells, 5, y, JunqiCellKind.Railway);
            if (KindAt(cells, 6, y) != JunqiCellKind.Camp) Set(cells, 6, y, JunqiCellKind.Railway);
            if (KindAt(cells, 11, y) != JunqiCellKind.Camp) Set(cells, 11, y, JunqiCellKind.Railway);
        }

        return cells;
    }

    private static JunqiCellKind KindAt(List<JunqiCellKind> cells, int x, int y)
        => InBounds(x, y) ? cells[IndexOf(x, y)] : JunqiCellKind.Post;

    private static void Set(List<JunqiCellKind> cells, int x, int y, JunqiCellKind kind)
    {
        if (!InBounds(x, y)) return;
        cells[IndexOf(x, y)] = kind;
    }

    /// <summary>某一方可以摆棋的格子（自己一侧、不是行营的点，共 25 个）。</summary>
    public static IReadOnlyList<(int X, int Y)> DeployPoints(JunqiSide side)
    {
        var list = new List<(int X, int Y)>(JunqiRanks.ArmySize);
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (!BelongsTo(side, x)) continue;
                // 行营是空位（棋子进去是防守用的）；大本营可以摆（军旗就摆在大本营里）
                if (KindAt(x, y) == JunqiCellKind.Camp) continue;
                list.Add((x, y));
            }
        }
        return list;
    }

    /// <summary>某一方的大本营坐标（军旗只能放在这两个之一）。</summary>
    public static IReadOnlyList<(int X, int Y)> HeadquartersOf(JunqiSide side)
        => side == JunqiSide.Red ? new[] { (1, 4), (3, 4) } : new[] { (10, 4), (8, 4) };
}
