namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>棋盘上一个交叉点的状态。</summary>
public enum GomokuCell : byte
{
    /// <summary>空点。</summary>
    Empty = 0,

    /// <summary>黑棋（先手）。</summary>
    Black = 1,

    /// <summary>白棋（后手）。</summary>
    White = 2,
}

/// <summary>
/// 棋盘上的一个交叉点：<b>x 从左往右 0 起，y 从上往下 0 起</b>（画棋盘的屏幕坐标同向，不用翻转）。
/// 只读结构体，可安全地放进快照 / 集合 / 字典。
/// </summary>
public readonly record struct GomokuPoint(int X, int Y);

/// <summary>
/// 一手棋（棋谱用）：落子坐标 + 落子方 + 手数。
/// <para>序列化到快照 / 网络时字段名是 <c>x</c> / <c>y</c> / <c>player</c> / <c>number</c>；
/// <see cref="Player"/> 存 <c>"black"</c> / <c>"white"</c>（跟协议里的 side 字符串一致）。</para>
/// </summary>
public sealed class GomokuMove
{
    /// <summary>列（0 起，从左往右）。</summary>
    public int X { get; set; }

    /// <summary>行（0 起，从上往下）。</summary>
    public int Y { get; set; }

    /// <summary>落子方：<c>"black"</c> / <c>"white"</c>。</summary>
    public string Player { get; set; } = "black";

    /// <summary>手数（从 1 开始）。</summary>
    public int Number { get; set; }

    /// <summary>反序列化用。</summary>
    public GomokuMove()
    {
    }

    /// <summary>方便构造一手棋（逻辑与自检里用）。</summary>
    public GomokuMove(int x, int y, string player, int number)
    {
        X = x;
        Y = y;
        Player = player;
        Number = number;
    }

    /// <summary>落子方对应的格子状态（认不出时返回 <see cref="GomokuCell.Empty"/>）。</summary>
    public GomokuCell Color => GomokuText.ParseSide(Player);

    /// <summary>复制一份（快照不共享引用）。</summary>
    public GomokuMove Clone()
        => new(X, Y, Player, Number);

    /// <inheritdoc />
    public override string ToString() => $"{Number}. {Player} ({X},{Y})";
}

/// <summary>
/// 五子棋棋盘：只存棋子本身（<b>不判胜负、不管轮次</b>，那些在 <see cref="GomokuGame"/> 里）。
///
/// <para>坐标范围是 <c>0 .. Size-1</c>；<see cref="Get"/> 越界时返回 <see cref="GomokuCell.Empty"/>
/// （方便沿方向扫描时不用每次先判边界），<see cref="Set"/> 越界则抛 <see cref="ArgumentOutOfRangeException"/>。</para>
///
/// <para><see cref="Moves"/> 由 <see cref="GomokuGame"/> 维护（落子 / 悔棋时增删），棋盘自己不会往里写；
/// 摆谱（只设棋子）时留空即可。</para>
/// </summary>
public sealed class GomokuBoard
{
    /// <summary>标准棋盘路数（五子棋默认 15 路）。</summary>
    public const int DefaultSize = 15;

    /// <summary>允许的最小路数。</summary>
    public const int MinSize = 5;

    /// <summary>允许的最大路数（界面只开放 15 / 19，留点余量给自检）。</summary>
    public const int MaxSize = 30;

    private readonly GomokuCell[] _cells;
    private readonly List<GomokuMove> _moves = new();

    /// <summary>建一个 <paramref name="size"/> 路空棋盘。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> 不在 <see cref="MinSize"/>~<see cref="MaxSize"/> 之间。</exception>
    public GomokuBoard(int size = DefaultSize)
    {
        if (size < MinSize || size > MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size,
                $"棋盘路数必须在 {MinSize}~{MaxSize} 之间（五子棋常用 15 或 19）。");
        }

        Size = size;
        _cells = new GomokuCell[size * size];
    }

    /// <summary>棋盘路数（15 / 19 …）。</summary>
    public int Size { get; }

    /// <summary>棋谱（由 <see cref="GomokuGame"/> 维护；棋盘自己只存棋子）。</summary>
    public IReadOnlyList<GomokuMove> Moves => _moves;

    /// <summary>某个点是不是在棋盘内。</summary>
    public bool InBounds(int x, int y) => (uint)x < (uint)Size && (uint)y < (uint)Size;

    /// <summary>取某个点的棋子；<b>越界返回 <see cref="GomokuCell.Empty"/></b>（不抛异常）。</summary>
    public GomokuCell Get(int x, int y) => InBounds(x, y) ? _cells[y * Size + x] : GomokuCell.Empty;

    /// <summary>放 / 改一个棋子（摆谱、载入快照用；也允许覆盖已有棋子）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">坐标越界。</exception>
    public void Set(int x, int y, GomokuCell cell)
    {
        if (!InBounds(x, y))
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"坐标 ({x},{y}) 超出 {Size}×{Size} 的棋盘。");
        }

        _cells[y * Size + x] = cell;
    }

    /// <summary>这个点是不是空的（越界视作不空）。</summary>
    public bool IsEmpty(int x, int y) => InBounds(x, y) && _cells[y * Size + x] == GomokuCell.Empty;

    /// <summary>数某种棋子的个数。</summary>
    public int Count(GomokuCell cell)
    {
        var count = 0;
        foreach (var c in _cells)
        {
            if (c == cell) count++;
        }

        return count;
    }

    /// <summary>复制一份（棋子 + 棋谱都复制，改副本不影响原棋盘）。</summary>
    public GomokuBoard Clone()
    {
        var copy = new GomokuBoard(Size);
        Array.Copy(_cells, copy._cells, _cells.Length);
        foreach (var move in _moves) copy._moves.Add(move.Clone());
        return copy;
    }

    // ---------------------------------------------------------------- 棋谱维护（只有 GomokuGame 用）

    /// <summary>追加一手棋（内部用：落子成功时调用）。</summary>
    internal void AddMove(GomokuMove move)
    {
        if (move is null) return;
        _moves.Add(move);
    }

    /// <summary>撤掉最后一手棋并返回它（内部用：悔棋时调用）。</summary>
    internal GomokuMove? RemoveLastMove()
    {
        if (_moves.Count == 0) return null;
        var move = _moves[^1];
        _moves.RemoveAt(_moves.Count - 1);
        return move;
    }

    /// <summary>清空棋谱（内部用：重置棋局时调用）。</summary>
    internal void ClearMoves() => _moves.Clear();
}
