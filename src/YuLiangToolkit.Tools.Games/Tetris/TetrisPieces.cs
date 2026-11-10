using System.Windows.Media;

namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>七种方块（标准 Tetromino 命名）。</summary>
public enum TetrominoKind
{
    /// <summary>长条 I。</summary>
    I,

    /// <summary>J 形。</summary>
    J,

    /// <summary>反 L 形。</summary>
    L,

    /// <summary>方块 O。</summary>
    O,

    /// <summary>S 形。</summary>
    S,

    /// <summary>T 形。</summary>
    T,

    /// <summary>Z 形。</summary>
    Z,

    /// <summary>垃圾行（联机互攻时对手送来的行；画成灰色，没有对应方块形状）。</summary>
    Garbage,
}

/// <summary>
/// 方块的**形状与旋转数据**（纯数据 + 纯函数，不碰界面）。
///
/// <para><b>坐标约定</b>：每种方块有自己的正方形包围盒（I 是 4×4、O 是 2×2、其余 3×3），
/// 单元格坐标是**包围盒内的相对偏移**，<c>X</c> 向右为正、<c>Y</c> **向下为正**（与棋盘一致）。</para>
///
/// <para><b>旋转</b>：四个状态（0 = 出生朝向，1 = 顺时针 90°，…）按 SRS 标准形状写死；
/// 旋转失败时按 SRS 的**墙踢表**依次尝试偏移（表里已把「Y 向上为正」换算成向下为正）。</para>
/// </summary>
public static class TetrisPieces
{
    /// <summary>旋转状态数（0 / R / 2 / L）。</summary>
    public const int RotationCount = 4;

    /// <summary>全部方块（7-bag 用的固定顺序）。</summary>
    public static IReadOnlyList<TetrominoKind> All { get; } = new[]
    {
        TetrominoKind.I, TetrominoKind.J, TetrominoKind.L, TetrominoKind.O,
        TetrominoKind.S, TetrominoKind.T, TetrominoKind.Z,
    };

    // ---------------------------------------------------------------- 形状（index = kind * 4 + rotation）

    private static readonly (int X, int Y)[][] Shapes =
    {
        // ---- I（4×4 包围盒） ----
        new[] { (0, 1), (1, 1), (2, 1), (3, 1) },
        new[] { (2, 0), (2, 1), (2, 2), (2, 3) },
        new[] { (0, 2), (1, 2), (2, 2), (3, 2) },
        new[] { (1, 0), (1, 1), (1, 2), (1, 3) },

        // ---- J（3×3） ----
        new[] { (0, 0), (0, 1), (1, 1), (2, 1) },
        new[] { (1, 0), (2, 0), (1, 1), (1, 2) },
        new[] { (0, 1), (1, 1), (2, 1), (2, 2) },
        new[] { (1, 0), (1, 1), (0, 2), (1, 2) },

        // ---- L（3×3） ----
        new[] { (2, 0), (0, 1), (1, 1), (2, 1) },
        new[] { (1, 0), (1, 1), (1, 2), (2, 2) },
        new[] { (0, 1), (1, 1), (2, 1), (0, 2) },
        new[] { (0, 0), (1, 0), (1, 1), (1, 2) },

        // ---- O（2×2，四个朝向完全一样） ----
        new[] { (0, 0), (1, 0), (0, 1), (1, 1) },
        new[] { (0, 0), (1, 0), (0, 1), (1, 1) },
        new[] { (0, 0), (1, 0), (0, 1), (1, 1) },
        new[] { (0, 0), (1, 0), (0, 1), (1, 1) },

        // ---- S（3×3） ----
        new[] { (1, 0), (2, 0), (0, 1), (1, 1) },
        new[] { (1, 0), (1, 1), (2, 1), (2, 2) },
        new[] { (1, 1), (2, 1), (0, 2), (1, 2) },
        new[] { (0, 0), (0, 1), (1, 1), (1, 2) },

        // ---- T（3×3） ----
        new[] { (1, 0), (0, 1), (1, 1), (2, 1) },
        new[] { (1, 0), (1, 1), (2, 1), (1, 2) },
        new[] { (0, 1), (1, 1), (2, 1), (1, 2) },
        new[] { (1, 0), (0, 1), (1, 1), (1, 2) },

        // ---- Z（3×3） ----
        new[] { (0, 0), (1, 0), (1, 1), (2, 1) },
        new[] { (2, 0), (1, 1), (2, 1), (1, 2) },
        new[] { (0, 1), (1, 1), (1, 2), (2, 2) },
        new[] { (1, 0), (0, 1), (1, 1), (0, 2) },
    };

    // ---------------------------------------------------------------- 墙踢表（index = from * 4 + to）

    /// <summary>J / L / S / T / Z 的墙踢表（SRS 标准，Y 已换算成向下为正）。</summary>
    private static readonly (int X, int Y)[][] StandardKicks = BuildKickTable(new()
    {
        // 0 → R
        [0 * 4 + 1] = new[] { (0, 0), (-1, 0), (-1, -1), (0, 2), (-1, 2) },
        // R → 0
        [1 * 4 + 0] = new[] { (0, 0), (1, 0), (1, 1), (0, -2), (1, -2) },
        // R → 2
        [1 * 4 + 2] = new[] { (0, 0), (1, 0), (1, 1), (0, -2), (1, -2) },
        // 2 → R
        [2 * 4 + 1] = new[] { (0, 0), (-1, 0), (-1, -1), (0, 2), (-1, 2) },
        // 2 → L
        [2 * 4 + 3] = new[] { (0, 0), (1, 0), (1, -1), (0, 2), (1, 2) },
        // L → 2
        [3 * 4 + 2] = new[] { (0, 0), (-1, 0), (-1, 1), (0, -2), (-1, -2) },
        // L → 0
        [3 * 4 + 0] = new[] { (0, 0), (-1, 0), (-1, 1), (0, -2), (-1, -2) },
        // 0 → L
        [0 * 4 + 3] = new[] { (0, 0), (1, 0), (1, -1), (0, 2), (1, 2) },
    });

    /// <summary>I 的墙踢表（SRS 的 I 专用表，比其它方块跨得更远）。</summary>
    private static readonly (int X, int Y)[][] IKicks = BuildKickTable(new()
    {
        [0 * 4 + 1] = new[] { (0, 0), (-2, 0), (1, 0), (-2, 1), (1, -2) },
        [1 * 4 + 0] = new[] { (0, 0), (2, 0), (-1, 0), (2, -1), (-1, 2) },
        [1 * 4 + 2] = new[] { (0, 0), (-1, 0), (2, 0), (-1, -2), (2, 1) },
        [2 * 4 + 1] = new[] { (0, 0), (1, 0), (-2, 0), (1, 2), (-2, -1) },
        [2 * 4 + 3] = new[] { (0, 0), (2, 0), (-1, 0), (2, -1), (-1, 2) },
        [3 * 4 + 2] = new[] { (0, 0), (-2, 0), (1, 0), (-2, 1), (1, -2) },
        [3 * 4 + 0] = new[] { (0, 0), (1, 0), (-2, 0), (1, 2), (-2, -1) },
        [0 * 4 + 3] = new[] { (0, 0), (-1, 0), (2, 0), (-1, -2), (2, 1) },
    });

    /// <summary>没写进表里的组合（O）一律只有「原地不动」一种尝试。</summary>
    private static (int X, int Y)[] NoKick { get; } = { (0, 0) };

    private static (int X, int Y)[][] BuildKickTable(Dictionary<int, (int, int)[]> source)
    {
        var table = new (int X, int Y)[TetrominoKindCount * RotationCount][];
        for (var i = 0; i < table.Length; i++) table[i] = source.TryGetValue(i, out var kicks) ? kicks : NoKick;
        return table;
    }

    private const int TetrominoKindCount = 7;

    // ---------------------------------------------------------------- 查询

    /// <summary>某个方块某个朝向的四个格子（包围盒内相对坐标，Y 向下为正）。</summary>
    public static IReadOnlyList<(int X, int Y)> CellsOf(TetrominoKind kind, int rotation)
    {
        // 垃圾行不是可操作的方块：真被传进来（数据错乱）就给 I 的形状，别抛异常把游戏搞崩
        var index = kind == TetrominoKind.Garbage ? 0 : (int)kind % TetrominoKindCount;
        return Shapes[index * RotationCount + NormalizeRotation(rotation)];
    }

    /// <summary>包围盒边长：I = 4、O = 2、其余 3。</summary>
    public static int BoxSize(TetrominoKind kind) => kind switch
    {
        TetrominoKind.I => 4,
        TetrominoKind.O => 2,
        _ => 3,
    };

    /// <summary>出生时的左上角 X（让方块在 10 列棋盘上居中）。</summary>
    public static int SpawnX(TetrominoKind kind) => kind switch
    {
        TetrominoKind.O => 4,
        _ => 3,
    };

    /// <summary>方向归一化到 0 ~ 3（负数与超界都能收敛）。</summary>
    public static int NormalizeRotation(int rotation)
    {
        var normalized = rotation % RotationCount;
        return normalized < 0 ? normalized + RotationCount : normalized;
    }

    /// <summary>某个方块从 <paramref name="from"/> 转到 <paramref name="to"/> 时依次尝试的偏移。</summary>
    public static IReadOnlyList<(int X, int Y)> KickOffsets(TetrominoKind kind, int from, int to)
    {
        var index = NormalizeRotation(from) * RotationCount + NormalizeRotation(to);
        var table = kind == TetrominoKind.I ? IKicks : StandardKicks;
        return kind == TetrominoKind.O ? NoKick : table[index];
    }

    /// <summary>每种方块的中文名 / 字母代号里，垃圾行单独处理（它不是可操作方块）。</summary>
    public static bool IsPlayable(TetrominoKind kind) => kind != TetrominoKind.Garbage;

    /// <summary>方块的中文名（界面 / 说明文字用）。</summary>
    public static string DisplayName(TetrominoKind kind) => kind switch
    {
        TetrominoKind.I => "I 长条",
        TetrominoKind.J => "J 形",
        TetrominoKind.L => "L 形",
        TetrominoKind.O => "O 方块",
        TetrominoKind.S => "S 形",
        TetrominoKind.T => "T 形",
        TetrominoKind.Garbage => "垃圾行",
        _ => "Z 形",
    };

    /// <summary>方块的单个字母代号（I / J / L / O / S / T / Z，预览区排版用）。</summary>
    public static string LetterOf(TetrominoKind kind) => kind switch
    {
        TetrominoKind.I => "I",
        TetrominoKind.J => "J",
        TetrominoKind.L => "L",
        TetrominoKind.O => "O",
        TetrominoKind.S => "S",
        TetrominoKind.T => "T",
        TetrominoKind.Garbage => "G",
        _ => "Z",
    };
}

/// <summary>
/// 七个方块的**数据色**（不是主题色）：取自经典俄罗斯方块的配色，但在深色背景上都调亮过，
/// 浅色主题下同样看得清。画刷只建一次并冻结，绘制时不 new。
/// </summary>
public static class TetrominoPalette
{
    private static readonly Dictionary<TetrominoKind, SolidColorBrush> Cache = new();

    /// <summary>某个方块的颜色（<c>#RRGGBB</c>）。垃圾行是灰色的，一眼能和自己的方块区分开。</summary>
    public static string HexOf(TetrominoKind kind) => kind switch
    {
        TetrominoKind.I => "#38D6E0",
        TetrominoKind.J => "#4C8DFF",
        TetrominoKind.L => "#FF9F43",
        TetrominoKind.O => "#F2C94C",
        TetrominoKind.S => "#3FB950",
        TetrominoKind.T => "#B47CFF",
        TetrominoKind.Garbage => "#6B7280",
        _ => "#E5534B",
    };

    /// <summary>某个方块的填充画刷（冻结缓存）。</summary>
    public static SolidColorBrush BrushOf(TetrominoKind kind)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(kind, out var cached)) return cached;
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(HexOf(kind)));
            brush.Freeze();
            Cache[kind] = brush;
            return brush;
        }
    }
}
