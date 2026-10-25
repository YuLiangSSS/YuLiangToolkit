using System.Text;

namespace YuLiangToolkit.Tools.BoardGames.Gomoku;

/// <summary>一局棋的结果。</summary>
public enum GomokuResult
{
    /// <summary>还在下。</summary>
    Playing = 0,

    /// <summary>黑棋获胜。</summary>
    BlackWin = 1,

    /// <summary>白棋获胜（含黑棋禁手判负）。</summary>
    WhiteWin = 2,

    /// <summary>和棋（棋盘下满都没人连成五子）。</summary>
    Draw = 3,
}

/// <summary>
/// 一局五子棋：落子、轮次、胜负、禁手、悔棋、快照。<b>纯逻辑，不碰界面与网络。</b>
///
/// <para>规则口径：</para>
/// <list type="bullet">
///   <item>黑先；黑白轮流落子。</item>
///   <item><b>禁手关闭</b>（默认）：任何一方连成五子或更多（长连）都算赢。</item>
///   <item><b>禁手开启</b>：黑棋落在三三 / 四四 / 长连（≥6）上 = 禁手，<b>黑棋立即判负</b>；
///         黑棋<b>正好五连</b>永远算赢（五连优先于长连）；白棋没有禁手，长连也算赢。</item>
///   <item>棋盘下满还没人成五 → 和棋。</item>
/// </list>
///
/// <para><see cref="Started"/> 默认是 true（离线 / 同机双人直接就能下）；联机房间在「开始对局」之前
/// 由 ViewModel 显式置 false，此时 <see cref="Play"/> 会拒绝落子。</para>
/// </summary>
public sealed class GomokuGame
{
    private readonly List<GomokuPoint> _winningLine = new();

    /// <summary>新建一局（空棋盘、黑棋先走、处于「已开始」状态）。</summary>
    public GomokuGame(int size = GomokuBoard.DefaultSize, bool forbiddenEnabled = false)
    {
        Board = new GomokuBoard(size);
        Size = Board.Size;
        ForbiddenEnabled = forbiddenEnabled;
        Turn = GomokuCell.Black;
        Result = GomokuResult.Playing;
        Started = true;
    }

    /// <summary>棋盘路数（15 / 19）。</summary>
    public int Size { get; private set; }

    /// <summary>这一局开没开禁手。</summary>
    public bool ForbiddenEnabled { get; private set; }

    /// <summary>棋盘（含棋谱：<see cref="GomokuBoard.Moves"/>）。</summary>
    public GomokuBoard Board { get; private set; }

    /// <summary>轮到谁走。</summary>
    public GomokuCell Turn { get; private set; }

    /// <summary>当前结果。</summary>
    public GomokuResult Result { get; private set; }

    /// <summary>对局结没结束（<see cref="Result"/> 不是 <see cref="GomokuResult.Playing"/>）。</summary>
    public bool IsOver => Result != GomokuResult.Playing;

    /// <summary>结果文案：对局进行中 / 黑棋获胜 / 白棋获胜 / 和棋。</summary>
    public string ResultText => GomokuText.ResultText(Result);

    /// <summary>完整棋谱（按手数顺序；对象引用，别在界面线程外边读边改）。</summary>
    public IReadOnlyList<GomokuMove> Moves => Board.Moves;

    /// <summary>最后一手（没有就是 null）。</summary>
    public GomokuMove? LastMove => Board.Moves.Count > 0 ? Board.Moves[^1] : null;

    /// <summary>最近一次禁手判负的点（界面标红）；没有就是 null。</summary>
    public GomokuPoint? LastForbiddenPoint { get; private set; }

    /// <summary>获胜连线（正好五连给 5 颗；关闭禁手时长连给完整线）；没赢就是空表。</summary>
    public IReadOnlyList<GomokuPoint> WinningLine => _winningLine;

    /// <summary>已经下了多少手。</summary>
    public int MoveCount => Board.Moves.Count;

    /// <summary>
    /// 对局有没有开始。<b>默认 true</b>；联机房间在房主点「开始对局」之前置 false，
    /// 这时界面禁手、<see cref="Play"/> 会拒绝落子。
    /// </summary>
    public bool Started { get; set; } = true;

    /// <summary>
    /// 落一手棋。<b>成功返回 null</b>；失败返回中文原因（越界 / 已有棋子 / 没轮到这个颜色 / 对局已结束 / 禁手不算失败，会直接判负）。
    /// </summary>
    public string? Play(GomokuCell player, int x, int y)
    {
        if (player != GomokuCell.Black && player != GomokuCell.White)
        {
            return "落子方无效（只能是黑棋或白棋）";
        }

        if (!Started)
        {
            return "对局还没有开始（请先点「开始对局」）";
        }

        if (IsOver)
        {
            return "对局已经结束（请点「再来一局」）";
        }

        if (!Board.InBounds(x, y))
        {
            return "落子位置超出棋盘";
        }

        if (!Board.IsEmpty(x, y))
        {
            return "这个位置已经有棋子了";
        }

        if (player != Turn)
        {
            return $"还没轮到{GomokuText.SideName(player)}（现在是{GomokuText.SideName(Turn)}的回合）";
        }

        // 禁手判定要在真摆子之前算（需要该点为空）。
        var forbiddenInfo = player == GomokuCell.Black && ForbiddenEnabled
            ? GomokuForbidden.Evaluate(Board, x, y, true)
            : null;

        Board.Set(x, y, player);
        Board.AddMove(new GomokuMove(x, y, GomokuText.SideKey(player), Board.Moves.Count + 1));
        Turn = player == GomokuCell.Black ? GomokuCell.White : GomokuCell.Black;
        LastForbiddenPoint = null;
        _winningLine.Clear();

        // 一、禁手判负（黑棋立即输）。
        if (forbiddenInfo is { IsForbidden: true })
        {
            Result = GomokuResult.WhiteWin;
            LastForbiddenPoint = new GomokuPoint(x, y);
            return null;
        }

        // 二、五连判定：禁手开启时黑棋只认「正好五连」（五连优先于长连），否则 ≥5 都算赢。
        var winning = FindWinningLine(player, x, y, exactlyFive: player == GomokuCell.Black && ForbiddenEnabled);
        if (winning.Count >= 5)
        {
            Result = player == GomokuCell.Black ? GomokuResult.BlackWin : GomokuResult.WhiteWin;
            _winningLine.AddRange(winning);
            return null;
        }

        // 三、和棋：下满还没有人连成五子。
        if (Board.Moves.Count >= Size * Size)
        {
            Result = GomokuResult.Draw;
        }

        return null;
    }

    /// <summary>撤掉最后一手；没有可撤的返回 false。会把结果恢复成「对局进行中」。</summary>
    public bool Undo()
    {
        var move = Board.RemoveLastMove();
        if (move is null) return false;

        var color = GomokuText.ParseSide(move.Player);
        if (Board.InBounds(move.X, move.Y)) Board.Set(move.X, move.Y, GomokuCell.Empty);

        Turn = color == GomokuCell.White ? GomokuCell.White : GomokuCell.Black;
        Result = GomokuResult.Playing;
        LastForbiddenPoint = null;
        _winningLine.Clear();
        return true;
    }

    /// <summary>
    /// 悔棋到「轮到 <paramref name="player"/> 走」为止（联机悔棋的语义）：
    /// 最后一步是对方 → 撤 2 步；最后一步是自己 → 撤 1 步；一手都没有 / 该方没下过子 → false。
    /// </summary>
    public bool UndoToTurn(GomokuCell player)
    {
        if (player != GomokuCell.Black && player != GomokuCell.White) return false;
        if (Board.Moves.Count == 0) return false;
        if (!Board.Moves.Any(m => GomokuText.ParseSide(m.Player) == player)) return false;

        Undo();
        while (Board.Moves.Count > 0 && Turn != player) Undo();
        return Turn == player;
    }

    /// <summary>重开一局：换棋盘大小 / 禁手开关，清空棋谱与结果（<see cref="Started"/> 保持不变，由调用方决定）。</summary>
    public void Reset(int size, bool forbiddenEnabled)
    {
        Board = new GomokuBoard(size);
        Size = Board.Size;
        ForbiddenEnabled = forbiddenEnabled;
        Turn = GomokuCell.Black;
        Result = GomokuResult.Playing;
        LastForbiddenPoint = null;
        _winningLine.Clear();
    }

    /// <summary>直接判和（联机「求和」双方同意 / 同机双人自己点和棋时用）。</summary>
    public void MarkDraw()
    {
        Result = GomokuResult.Draw;
        LastForbiddenPoint = null;
        _winningLine.Clear();
    }

    /// <summary>直接判某方获胜（认输 / 特殊判罚用；<paramref name="winner"/> 不是黑白时忽略）。</summary>
    public void MarkWin(GomokuCell winner)
    {
        if (winner != GomokuCell.Black && winner != GomokuCell.White) return;
        Result = winner == GomokuCell.Black ? GomokuResult.BlackWin : GomokuResult.WhiteWin;
        LastForbiddenPoint = null;
        _winningLine.Clear();
    }

    /// <summary>某方认输 → 对方获胜（<paramref name="loser"/> 不是黑白时忽略）。</summary>
    public void MarkResign(GomokuCell loser)
    {
        if (loser == GomokuCell.Black) MarkWin(GomokuCell.White);
        else if (loser == GomokuCell.White) MarkWin(GomokuCell.Black);
    }

    /// <summary>导出可序列化快照（联机时由主机广播给客户端）。</summary>
    public GomokuSnapshot ToSnapshot()
    {
        var cells = new StringBuilder(Size * Size);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                cells.Append(Board.Get(x, y) switch
                {
                    GomokuCell.Black => 'b',
                    GomokuCell.White => 'w',
                    _ => '.',
                });
            }
        }

        return new GomokuSnapshot
        {
            Size = Size,
            ForbiddenEnabled = ForbiddenEnabled,
            Started = Started,
            Turn = GomokuText.SideKey(Turn),
            Result = GomokuText.ResultKey(Result),
            Cells = cells.ToString(),
            Moves = Board.Moves.Select(m => m.Clone()).ToList(),
            LastForbiddenPoint = LastForbiddenPoint,
            WinningLine = _winningLine.Count > 0 ? _winningLine.ToList() : null,
        };
    }

    /// <summary>
    /// 从快照恢复棋局。<b>成功返回 <c>(game, "")</c></b>；失败返回 <c>(null, 中文原因)</c>。
    /// 兼容性优先：棋谱可以缺，但棋盘大小 / 棋子布局必须完整合法。
    /// </summary>
    public static (GomokuGame? Game, string Error) FromSnapshot(GomokuSnapshot? snapshot)
    {
        if (snapshot is null) return (null, "局面数据为空");

        var size = snapshot.Size;
        if (size < GomokuBoard.MinSize || size > GomokuBoard.MaxSize)
        {
            return (null, $"棋盘大小无效：{size}（应在 {GomokuBoard.MinSize}~{GomokuBoard.MaxSize} 之间）");
        }

        var cells = snapshot.Cells ?? "";
        var expected = size * size;
        if (cells.Length != expected)
        {
            return (null, $"棋子布局长度不对：应为 {expected} 个字符，实际 {cells.Length} 个");
        }

        var board = new GomokuBoard(size);
        for (var i = 0; i < cells.Length; i++)
        {
            var cell = cells[i] switch
            {
                '.' => GomokuCell.Empty,
                'b' or 'B' => GomokuCell.Black,
                'w' or 'W' => GomokuCell.White,
                _ => (GomokuCell)255,
            };
            if ((byte)cell == 255) return (null, $"棋子布局里有无法识别的字符：'{cells[i]}'");
            board.Set(i % size, i / size, cell);
        }

        var game = new GomokuGame(size, snapshot.ForbiddenEnabled)
        {
            Started = snapshot.Started,
            Board = board,
            Turn = GomokuText.ParseSide(snapshot.Turn),
            Result = GomokuText.ParseResult(snapshot.Result),
            LastForbiddenPoint = snapshot.LastForbiddenPoint,
        };

        if (game.Turn == GomokuCell.Empty)
        {
            // 快照没写 / 写坏了轮次 → 按手数奇偶猜（黑先）。
            var moveCount = snapshot.Moves?.Count ?? 0;
            game.Turn = moveCount % 2 == 0 ? GomokuCell.Black : GomokuCell.White;
        }

        if (snapshot.Moves is { Count: > 0 } moves)
        {
            foreach (var move in moves)
            {
                if (move is not null) board.AddMove(move.Clone());
            }
        }

        if (snapshot.WinningLine is { Count: > 0 } line)
        {
            game._winningLine.AddRange(line);
        }

        return (game, "");
    }

    /// <summary>
    /// 找出 (x,y) 这一手在某个方向上形成的获胜连线。
    /// <paramref name="exactlyFive"/> = true 时只认正好 5 子（禁手开启时的黑棋）。
    /// </summary>
    private List<GomokuPoint> FindWinningLine(GomokuCell player, int x, int y, bool exactlyFive)
    {
        var best = new List<GomokuPoint>();
        foreach (var (dx, dy) in FourDirections)
        {
            var run = GomokuForbidden.CountRun(Board, x, y, dx, dy, player);
            if (run < 5) continue;
            if (exactlyFive && run != 5) continue;

            var line = CollectLine(x, y, dx, dy, player);
            if (line.Count > best.Count) best = line;
            if (best.Count >= 5 && !exactlyFive) break;
        }

        return best;
    }

    /// <summary>沿方向收集从一端到另一端的整条同色连子（按坐标顺序）。</summary>
    private List<GomokuPoint> CollectLine(int x, int y, int dx, int dy, GomokuCell player)
    {
        var startX = x;
        var startY = y;
        while (true)
        {
            var nx = startX - dx;
            var ny = startY - dy;
            if (Board.InBounds(nx, ny) && Board.Get(nx, ny) == player)
            {
                startX = nx;
                startY = ny;
            }
            else
            {
                break;
            }
        }

        var line = new List<GomokuPoint>();
        for (int px = startX, py = startY;
             Board.InBounds(px, py) && Board.Get(px, py) == player;
             px += dx, py += dy)
        {
            line.Add(new GomokuPoint(px, py));
        }

        return line;
    }

    private static readonly (int Dx, int Dy)[] FourDirections =
    {
        (1, 0),
        (0, 1),
        (1, 1),
        (1, -1),
    };
}
