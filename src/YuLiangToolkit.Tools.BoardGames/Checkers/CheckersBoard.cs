using System.Collections.Generic;

namespace YuLiangToolkit.Tools.BoardGames.Checkers;

/// <summary>六芒星的六个角（每个角是 10 个格子的三角形）。</summary>
public enum CheckersPoint
{
    /// <summary>上方。</summary>
    North = 0,

    /// <summary>右上。</summary>
    NorthEast = 1,

    /// <summary>右下。</summary>
    SouthEast = 2,

    /// <summary>下方。</summary>
    South = 3,

    /// <summary>左下。</summary>
    SouthWest = 4,

    /// <summary>左上。</summary>
    NorthWest = 5,
}

/// <summary>格子信息。</summary>
public readonly record struct CheckersCell(int X, int Y, CheckersPoint? Point, bool IsCenter);

/// <summary>
/// 六芒星跳棋棋盘（<b>121 格</b>）：6 个角各 <b>10 格</b>（4+3+2+1）+ 中央六边形 <b>61 格</b>。
///
/// <para><b>怎么构造</b>：用<b>轴向坐标</b>（axial q/r，六边形网格的标准做法）。
/// 中央六边形 = 半径 4 的六边形（61 格）；六个角 = 从六边形的六条边往外，
/// 每层分别铺 5-i 个格（i = 1..4 → 4+3+2+1 = 10 格）。</para>
///
/// <para><b>坐标</b>：对外用<b>偏移坐标</b>（odd-r，X/Y），换算纯整数、不累积误差，方便画图与存档。</para>
/// </summary>
public static class CheckersBoard
{
    /// <summary>中央六边形半径（4 → 61 格）。</summary>
    public const int CenterRadius = 4;

    /// <summary>每个角的格子数。</summary>
    public const int PointSize = 10;

    /// <summary>棋盘总格数。</summary>
    public const int CellCount = 121;

    /// <summary>中央六边形格数。</summary>
    public const int CenterSize = 61;

    /// <summary>偏移网格的边长（画图用）。</summary>
    public const int GridSize = 17;

    private static readonly Dictionary<(int X, int Y), CheckersCell> Cells = new();
    private static readonly List<CheckersCell> AllCells = new();
    private static readonly Dictionary<(int X, int Y), List<(int X, int Y)>> NeighborTable = new();

    static CheckersBoard() => Build();

    /// <summary>全部格子。</summary>
    public static IReadOnlyList<CheckersCell> All => AllCells;

    /// <summary>取某一格（不存在返回 null）。</summary>
    public static CheckersCell? CellAt(int x, int y)
        => Cells.TryGetValue((x, y), out var cell) ? cell : null;

    /// <summary>这个坐标在不在棋盘上。</summary>
    public static bool Exists(int x, int y) => Cells.ContainsKey((x, y));

    /// <summary>某个角的全部格子（按行、列排好序）。</summary>
    public static IReadOnlyList<CheckersCell> CellsOf(CheckersPoint point)
        => AllCells.Where(c => c.Point == point).OrderBy(c => c.Y).ThenBy(c => c.X).ToList();

    /// <summary>中央格。</summary>
    public static IReadOnlyList<CheckersCell> CenterCells => AllCells.Where(c => c.IsCenter).ToList();

    /// <summary>某个格子的六个邻居（只返回棋盘上存在的）。</summary>
    public static IReadOnlyList<(int X, int Y)> NeighborsOf(int x, int y)
        => NeighborTable.TryGetValue((x, y), out var list) ? list : Array.Empty<(int X, int Y)>();

    /// <summary>观察者站在某个角时，它的「对面」角。</summary>
    public static CheckersPoint Opposite(CheckersPoint point) => point switch
    {
        CheckersPoint.North => CheckersPoint.South,
        CheckersPoint.NorthEast => CheckersPoint.SouthWest,
        CheckersPoint.SouthEast => CheckersPoint.NorthWest,
        CheckersPoint.South => CheckersPoint.North,
        CheckersPoint.SouthWest => CheckersPoint.NorthEast,
        _ => CheckersPoint.SouthEast,
    };

    /// <summary>角的中文名。</summary>
    public static string DisplayName(CheckersPoint point) => point switch
    {
        CheckersPoint.North => "上",
        CheckersPoint.NorthEast => "右上",
        CheckersPoint.SouthEast => "右下",
        CheckersPoint.South => "下",
        CheckersPoint.SouthWest => "左下",
        _ => "左上",
    };

    /// <summary>键（协议 / 配置用）。</summary>
    public static string ToKey(CheckersPoint point) => point switch
    {
        CheckersPoint.North => "n",
        CheckersPoint.NorthEast => "ne",
        CheckersPoint.SouthEast => "se",
        CheckersPoint.South => "s",
        CheckersPoint.SouthWest => "sw",
        _ => "nw",
    };

    /// <summary>键 → 角（认不出按上）。</summary>
    public static CheckersPoint ParseKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "ne" => CheckersPoint.NorthEast,
        "se" => CheckersPoint.SouthEast,
        "s" => CheckersPoint.South,
        "sw" => CheckersPoint.SouthWest,
        "nw" => CheckersPoint.NorthWest,
        _ => CheckersPoint.North,
    };

    // ---------------------------------------------------------------- 坐标换算

    /// <summary>轴向坐标 → 偏移坐标（odd-r，居中到 17×17）。</summary>
    public static (int X, int Y) AxialToOffset(int q, int r)
        => (q + (r - (r & 1)) / 2 + 8, r + 8);

    /// <summary>偏移坐标 → 轴向坐标（odd-r）。</summary>
    public static (int Q, int R) OffsetToAxial(int x, int y)
    {
        var r = y - 8;
        var q = (x - 8) - (r - (r & 1)) / 2;
        return (q, r);
    }

    // ---------------------------------------------------------------- 构造

    private static void Build()
    {
        // ① 中央六边形：半径 4 的所有轴向坐标
        for (var q = -CenterRadius; q <= CenterRadius; q++)
        {
            for (var r = -CenterRadius; r <= CenterRadius; r++)
            {
                var s = -q - r;
                if (Math.Max(Math.Abs(q), Math.Max(Math.Abs(r), Math.Abs(s))) > CenterRadius) continue;

                var (x, y) = AxialToOffset(q, r);
                AddCell(x, y, null, isCenter: true);
            }
        }

        // ② 六个角：沿六个轴向方向往外 4 层，第 i 层宽度 5-i（4+3+2+1 = 10 格）
        //    横向铺开用「垂直于该方向的另一条轴」（由旋转 60° 得到，奇偶方向相反）。
        foreach (var point in Enum.GetValues<CheckersPoint>())
        {
            var (dq, dr) = DirectionOf(point);
            var (rq, rr) = Rotate60(dq, dr);
            // 偶数序号的角用正向横向、奇数用反向，保证三角形是「居中」的
            var flip = ((int)point) % 2 == 0 ? 1 : -1;
            var (pq, pr) = (rq * flip, rr * flip);

            for (var layer = 1; layer <= 4; layer++)
            {
                var width = 5 - layer;
                var centerQ = dq * (CenterRadius + layer);
                var centerR = dr * (CenterRadius + layer);

                for (var i = 0; i < width; i++)
                {
                    var offset = i - (width - 1) / 2;
                    var q = centerQ + offset * pq;
                    var r = centerR + offset * pr;

                    var (x, y) = AxialToOffset(q, r);
                    AddCell(x, y, point, isCenter: false);
                }
            }
        }

        BuildNeighbors();
    }

    /// <summary>六个方向在轴向坐标下的单位向量。</summary>
    private static (int Q, int R) DirectionOf(CheckersPoint point) => point switch
    {
        CheckersPoint.North => (0, -1),
        CheckersPoint.NorthEast => (1, -1),
        CheckersPoint.SouthEast => (1, 0),
        CheckersPoint.South => (0, 1),
        CheckersPoint.SouthWest => (-1, 1),
        _ => (-1, 0),
    };

    /// <summary>轴向坐标旋转 60°（用来求「垂直于某方向」的横向单位）。</summary>
    private static (int Q, int R) Rotate60(int q, int r) => (q + r, -q);

    private static void AddCell(int x, int y, CheckersPoint? point, bool isCenter)
    {
        if (Cells.ContainsKey((x, y))) return;
        var cell = new CheckersCell(x, y, point, isCenter);
        Cells[(x, y)] = cell;
        AllCells.Add(cell);
    }

    /// <summary>建邻接表：odd-r 偏移坐标的六个方向。</summary>
    private static void BuildNeighbors()
    {
        int[][] evenDirs =
        {
            new[] { 1, 0 }, new[] { 0, -1 }, new[] { -1, -1 },
            new[] { -1, 0 }, new[] { -1, 1 }, new[] { 0, 1 },
        };
        int[][] oddDirs =
        {
            new[] { 1, 0 }, new[] { 1, -1 }, new[] { 0, -1 },
            new[] { -1, 0 }, new[] { 0, 1 }, new[] { 1, 1 },
        };

        foreach (var cell in AllCells)
        {
            var dirs = cell.Y % 2 == 0 ? evenDirs : oddDirs;
            var list = new List<(int X, int Y)>(6);

            foreach (var dir in dirs)
            {
                var nx = cell.X + dir[0];
                var ny = cell.Y + dir[1];
                if (Cells.ContainsKey((nx, ny))) list.Add((nx, ny));
            }

            NeighborTable[(cell.X, cell.Y)] = list;
        }
    }

    /// <summary>校验（自检）：总格数、每角 10 格、邻接对称。</summary>
    public static bool IsValid()
    {
        if (AllCells.Count != CellCount) return false;
        if (AllCells.Select(c => (c.X, c.Y)).Distinct().Count() != CellCount) return false;
        if (CenterCells.Count != CenterSize) return false;

        foreach (var point in Enum.GetValues<CheckersPoint>())
        {
            if (CellsOf(point).Count != PointSize) return false;
        }

        foreach (var cell in AllCells)
        {
            foreach (var (nx, ny) in NeighborsOf(cell.X, cell.Y))
            {
                if (!NeighborsOf(nx, ny).Contains((cell.X, cell.Y))) return false;
            }
        }
        return true;
    }
}
