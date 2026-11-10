namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>四国军棋的四个阵营（对家是同盟）。</summary>
public enum JunqiNation
{
    /// <summary>红（左下）。</summary>
    Red = 0,

    /// <summary>蓝（左上）。</summary>
    Blue = 1,

    /// <summary>绿（右上）。</summary>
    Green = 2,

    /// <summary>黄（右下）。</summary>
    Yellow = 3,
}

/// <summary>四国军棋棋盘（标准十字形：4 个 6×6 的方，中间 3×3 公共区）。</summary>
public static class JunqiFourBoard
{
    /// <summary>棋盘边长（13 个坐标点：每个方 6 格 + 中间 1 格）。</summary>
    public const int Size = 13;

    /// <summary>格子总数。</summary>
    public const int CellCount = Size * Size;

    /// <summary>坐标是否在棋盘内。</summary>
    public static bool InBounds(int x, int y) => x >= 0 && x < Size && y >= 0 && y < Size;

    /// <summary>某个坐标属于哪个阵营的「方」（中间公共区返回 null）。</summary>
    public static JunqiNation? NationAt(int x, int y)
    {
        if (!InBounds(x, y)) return null;
        if (x == 6 || y == 6) return null;          // 中线 / 中心区（公共）

        var left = x < 6;      // 左半
        var top = y < 6;       // 上半
        return (left, top) switch
        {
            (true, false) => JunqiNation.Red,       // 左下
            (true, true) => JunqiNation.Blue,       // 左上
            (false, true) => JunqiNation.Green,     // 右上
            _ => JunqiNation.Yellow,                // 右下
        };
    }

    /// <summary>对家（同盟）阵营。</summary>
    public static JunqiNation Ally(JunqiNation nation) => (JunqiNation)(((int)nation + 2) % 4);

    /// <summary>是不是同盟（含自己）。</summary>
    public static bool IsAlly(JunqiNation a, JunqiNation b) => a == b || Ally(a) == b;

    /// <summary>按「顺时针」的下一个阵营。</summary>
    public static JunqiNation Next(JunqiNation nation) => (JunqiNation)(((int)nation + 1) % 4);

    /// <summary>
    /// 某一方的可摆棋点：<b>标准 25 个</b>（四方完全同构）。
    /// <para>本方 6×6 = 36 格，去掉 5 个行营剩 31 格；四国军棋每方 25 枚棋子，
    /// 所以固定留空 6 格（本方首列 / 末列各靠后的三格），正好剩 25 个 —— 2 个大本营都在其中（军旗摆那里）。</para>
    /// </summary>
    public static IReadOnlyList<(int X, int Y)> DeployPoints(JunqiNation nation)
    {
        var points = new List<(int X, int Y)>(JunqiRanks.ArmySize);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (NationAt(x, y) != nation) continue;
                if (IsCamp(x, y)) continue;

                var (lx, ly) = LocalPoint(nation, x, y);

                // 留空 6 格：首列 / 末列各靠后的三格（对称，四方一致）
                if ((lx == 0 || lx == 5) && ly >= 3) continue;

                points.Add((x, y));
            }
        }
        return points;
    }

    /// <summary>
    /// 本方 6×6 区域的左上角坐标（把绝对坐标换算成本方局部坐标用）。
    /// </summary>
    public static (int X, int Y) OriginOf(JunqiNation nation) => nation switch
    {
        JunqiNation.Blue => (0, 0),        // 左上
        JunqiNation.Green => (7, 0),       // 右上
        JunqiNation.Yellow => (7, 7),      // 右下
        _ => (0, 7),                       // 红：左下
    };

    /// <summary>
    /// 绝对坐标 → 本方局部坐标（0~5，<b>原点放在本方自己那一侧的左下角</b>）。
    /// <para>这样「行营在靠后、大本营在最底行」对四个方都成立，判断逻辑只写一份。
    /// 直接按各方块的左上角相减会让上下两侧的行营 / 大本营方向相反（早期版本踩过）。</para>
    /// </summary>
    public static (int X, int Y) LocalPoint(JunqiNation nation, int x, int y)
    {
        var (ox, oy) = OriginOf(nation);
        var lx = x - ox;
        var ly = y - oy;
        // 上方的两方（蓝、绿）要上下翻转，让 ly=5 始终是「自己最靠后的那一行」
        return nation is JunqiNation.Blue or JunqiNation.Green ? (lx, 5 - ly) : (lx, ly);
    }

    /// <summary>行营（每方 5 个，四个方完全同构）。</summary>
    public static bool IsCamp(int x, int y)
    {
        var nation = NationAt(x, y);
        if (nation is null) return false;

        var (lx, ly) = LocalPoint(nation.Value, x, y);
        return (lx, ly) is (1, 1) or (3, 1) or (5, 1) or (2, 2) or (4, 2);
    }

    /// <summary>大本营（每方 2 个，位于本方底行靠内的两个点）。</summary>
    public static bool IsHeadquarters(int x, int y)
    {
        var nation = NationAt(x, y);
        if (nation is null) return false;

        var (lx, ly) = LocalPoint(nation.Value, x, y);
        return (lx, ly) is (1, 5) or (3, 5);
    }

    /// <summary>某一方的大本营坐标。</summary>
    public static IReadOnlyList<(int X, int Y)> HeadquartersOf(JunqiNation nation)
    {
        var list = new List<(int X, int Y)>(2);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                if (NationAt(x, y) == nation && IsHeadquarters(x, y)) list.Add((x, y));
            }
        }
        return list;
    }

    /// <summary>铁路：本方两条横线 + 两条竖线，再加中间十字通道。</summary>
    public static bool IsRailway(int x, int y)
    {
        if (!InBounds(x, y)) return false;

        // 中间十字通道（x==6 或 y==6）贯穿全盘
        if (x == 6 || y == 6) return true;

        var nation = NationAt(x, y);
        if (nation is null) return false;

        if (IsCamp(x, y) || IsHeadquarters(x, y)) return false;

        var (lx, ly) = LocalPoint(nation.Value, x, y);
        return ly is 1 or 3 || lx is 0 or 5;
    }

    /// <summary>某个阵营的中文名。</summary>
    public static string DisplayName(JunqiNation nation) => nation switch
    {
        JunqiNation.Blue => "蓝方",
        JunqiNation.Green => "绿方",
        JunqiNation.Yellow => "黄方",
        _ => "红方",
    };

    /// <summary>键（协议 / 配置用）。</summary>
    public static string ToKey(JunqiNation nation) => nation switch
    {
        JunqiNation.Blue => "blue",
        JunqiNation.Green => "green",
        JunqiNation.Yellow => "yellow",
        _ => "red",
    };

    /// <summary>键 → 阵营。</summary>
    public static JunqiNation Parse(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "blue" => JunqiNation.Blue,
        "green" => JunqiNation.Green,
        "yellow" => JunqiNation.Yellow,
        _ => JunqiNation.Red,
    };
}
