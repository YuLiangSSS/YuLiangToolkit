namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>棋盘坐标（列 / 行，都从 0 起）。</summary>
public readonly record struct SnakePoint(int X, int Y)
{
    /// <summary>加法（移动方向用）。</summary>
    public static SnakePoint operator +(SnakePoint a, SnakePoint b) => new(a.X + b.X, a.Y + b.Y);

    /// <inheritdoc />
    public override string ToString() => $"({X},{Y})";
}

/// <summary>方向（四向）。</summary>
public enum SnakeDirection
{
    /// <summary>上。</summary>
    Up = 0,

    /// <summary>右。</summary>
    Right = 1,

    /// <summary>下。</summary>
    Down = 2,

    /// <summary>左。</summary>
    Left = 3,
}

/// <summary>方向工具。</summary>
public static class SnakeDirections
{
    /// <summary>方向 → 单位向量。</summary>
    public static SnakePoint ToVector(SnakeDirection direction) => direction switch
    {
        SnakeDirection.Up => new SnakePoint(0, -1),
        SnakeDirection.Right => new SnakePoint(1, 0),
        SnakeDirection.Down => new SnakePoint(0, 1),
        _ => new SnakePoint(-1, 0),
    };

    /// <summary>两个方向是否相反（不能 180° 掉头）。</summary>
    public static bool IsOpposite(SnakeDirection a, SnakeDirection b)
        => (((int)a) + 2) % 4 == (int)b;

    /// <summary>全部方向。</summary>
    public static IReadOnlyList<SnakeDirection> All { get; } = new[]
    {
        SnakeDirection.Up, SnakeDirection.Right, SnakeDirection.Down, SnakeDirection.Left,
    };

    /// <summary>中文名。</summary>
    public static string DisplayName(SnakeDirection direction) => direction switch
    {
        SnakeDirection.Up => "上",
        SnakeDirection.Right => "右",
        SnakeDirection.Down => "下",
        _ => "左",
    };
}

/// <summary>撞到边缘时的处理方式。</summary>
public enum SnakeEdgeMode
{
    /// <summary>撞墙即死。</summary>
    Die = 0,

    /// <summary>停住不动（不前进，也不死）。</summary>
    Stop = 1,

    /// <summary>穿墙（从另一边出来）——额外送的经典选项。</summary>
    Wrap = 2,
}

/// <summary>
/// 一条蛇。
///
/// <para><b>蛇身</b>：<see cref="Body"/> 从蛇头（索引 0）到蛇尾。移动时把新头插到最前面、
/// 没吃到果子就去掉尾巴（等价于整条蛇往前爬一格）。</para>
///
/// <para><b>虚化（重生保护）</b>：<see cref="GhostSteps"/> &gt; 0 时这条蛇处于虚化状态 ——
/// 能穿过别的蛇、不受伤；每走一步 <see cref="GhostSteps"/> 减 1，减到 0 就实化。
/// 虚化期间走过的格子记在 <see cref="GhostTrail"/> 里，实化时按顺序逐节变成实体。</para>
/// </summary>
public sealed class SnakeBody
{
    /// <summary>蛇身（索引 0 = 蛇头）。</summary>
    public List<SnakePoint> Body { get; } = new();

    /// <summary>当前朝向。</summary>
    public SnakeDirection Direction { get; set; } = SnakeDirection.Right;

    /// <summary>下一帧要转弯成的方向（避免一帧内连转两次掉头）。</summary>
    public SnakeDirection? PendingDirection { get; set; }

    /// <summary>是不是活的（死了就停在原地、由外部决定重生）。</summary>
    public bool IsAlive { get; set; } = true;

    /// <summary>剩余虚化步数（&gt; 0 = 无敌 + 可穿透）。</summary>
    public int GhostSteps { get; set; }

    /// <summary>虚化状态下走过的格子（按时间顺序，实化时逐节变实体）。</summary>
    public List<SnakePoint> GhostTrail { get; } = new();

    /// <summary>蛇头位置。</summary>
    public SnakePoint Head => Body.Count > 0 ? Body[0] : new SnakePoint(-1, -1);

    /// <summary>长度。</summary>
    public int Length => Body.Count;

    /// <summary>是不是处于虚化 / 无敌状态。</summary>
    public bool IsGhost => GhostSteps > 0;

    /// <summary>这条蛇是否占着某一格。</summary>
    public bool Occupies(SnakePoint point)
    {
        foreach (var segment in Body)
        {
            if (segment == point) return true;
        }
        return false;
    }

    /// <summary>复制一份（模拟 / AI 推演用）。</summary>
    public SnakeBody Clone()
    {
        var clone = new SnakeBody
        {
            Direction = Direction,
            PendingDirection = PendingDirection,
            IsAlive = IsAlive,
            GhostSteps = GhostSteps,
        };
        clone.Body.AddRange(Body);
        clone.GhostTrail.AddRange(GhostTrail);
        return clone;
    }

    /// <summary>在 (x,y) 造一条朝右、长度为 <paramref name="length"/> 的蛇。</summary>
    public static SnakeBody Create(int x, int y, int length, SnakeDirection direction = SnakeDirection.Right)
    {
        var snake = new SnakeBody { Direction = direction };
        var dir = SnakeDirections.ToVector(direction);
        var clamped = Math.Max(1, length);
        for (var i = 0; i < clamped; i++)
        {
            // 身体往「反向」延伸
            snake.Body.Add(new SnakePoint(x - dir.X * i, y - dir.Y * i));
        }
        return snake;
    }
}
