namespace YuLiangToolkit.Tools.BoardGames.Go;

/// <summary>棋盘上的一个交叉点。</summary>
public enum GoCell : byte
{
    /// <summary>空点。</summary>
    Empty = 0,

    /// <summary>黑子。</summary>
    Black = 1,

    /// <summary>白子。</summary>
    White = 2,
}

/// <summary>围棋坐标（<c>X</c> 列、<c>Y</c> 行，都从 0 起）。</summary>
public readonly record struct GoPoint(int X, int Y)
{
    /// <inheritdoc />
    public override string ToString() => $"({X},{Y})";
}

/// <summary>落子结果。</summary>
public enum GoPlayResult
{
    /// <summary>成功。</summary>
    Ok = 0,

    /// <summary>已经分出胜负，不能再下。</summary>
    GameOver = 1,

    /// <summary>坐标越界。</summary>
    OutOfBounds = 2,

    /// <summary>这个点已经有子。</summary>
    Occupied = 3,

    /// <summary>自杀（下完自己的棋块没气）。</summary>
    Suicide = 4,

    /// <summary>打劫（重回上一手之前的面貌）。</summary>
    Ko = 5,

    /// <summary>不是你的回合。</summary>
    NotYourTurn = 6,
}

/// <summary>
/// 围棋的一步记录（棋谱 / 复盘 / 悔棋用）。
/// </summary>
/// <param name="X">列（-1 = 停一手 pass）。</param>
/// <param name="Y">行（-1 = pass）。</param>
/// <param name="Player">落子方。</param>
/// <param name="Captured">这一手提掉的对方棋子坐标。</param>
/// <param name="IsPass">是不是停一手。</param>
public sealed record GoMove(int X, int Y, GoCell Player, IReadOnlyList<GoPoint> Captured, bool IsPass);

/// <summary>终局计分（中国规则：数子，贴目按配置）。</summary>
/// <param name="BlackStones">黑方棋子数（含黑方地盘内的空点）。</param>
/// <param name="WhiteStones">白方棋子数（含白方地盘内的空点）。</param>
/// <param name="Komi">贴目（白棋额外加）。</param>
/// <param name="BlackTotal">黑方总分。</param>
/// <param name="WhiteTotal">白方总分。</param>
/// <param name="Winner">胜方（null = 和棋）。</param>
/// <param name="Text">一句中文结果。</param>
public sealed record GoScoreResult(
    int BlackStones,
    int WhiteStones,
    double Komi,
    double BlackTotal,
    double WhiteTotal,
    GoCell? Winner,
    string Text);

/// <summary>
/// 围棋对局（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>规则</b>：标准围棋 —— 落子、提子（对方棋块没气就整块提掉）、
/// <b>禁自杀</b>、<b>禁打劫</b>（不能立刻回到上一手之前的面貌）、停一手（pass）、
/// 双方连续停一手即终局，按<b>中国规则数子</b>（棋盘上活子 + 围住的空点，白方加贴目）。</para>
///
/// <para><b>气</b>：一个棋块的「气」= 与它相连的空点数。无气的棋块要被提掉。</para>
/// </summary>
public sealed class GoGame
{
    /// <summary>标准棋盘路数。</summary>
    public const int DefaultSize = 19;

    /// <summary>允许的棋盘路数。</summary>
    public static readonly int[] AllowedSizes = { 9, 13, 19 };

    private readonly List<GoMove> _history = new();
    private GoCell[] _board;
    private GoCell _turn = GoCell.Black;
    private int _size;
    private int _passCount;
    private GoCell? _winner;
    private string _resultText = "";
    private bool _started;
    private bool _over;
    private double _komi = 7.5;

    /// <summary>打劫禁着点（上一手刚提掉单个子的位置，下一手不能立刻回提）。</summary>
    private GoPoint? _koPoint;

    /// <summary>创建一局围棋。</summary>
    /// <param name="size">棋盘路数（9 / 13 / 19）。</param>
    /// <param name="komi">贴目（白方额外加，默认 7.5）。</param>
    public GoGame(int size = DefaultSize, double komi = 7.5)
    {
        _size = AllowedSizes.Contains(size) ? size : DefaultSize;
        _komi = komi;
        _board = new GoCell[_size * _size];
    }

    // ---------------------------------------------------------------- 状态

    /// <summary>棋盘路数。</summary>
    public int Size => _size;

    /// <summary>贴目。</summary>
    public double Komi => _komi;

    /// <summary>现在轮到谁。</summary>
    public GoCell Turn => _turn;

    /// <summary>对局有没有开始（界面用）。</summary>
    public bool Started
    {
        get => _started;
        set => _started = value;
    }

    /// <summary>是不是已经终局。</summary>
    public bool IsOver => _over;

    /// <summary>胜方（终局后才有）。</summary>
    public GoCell? Winner => _winner;

    /// <summary>结果文案。</summary>
    public string ResultText => _resultText;

    /// <summary>走了几手（pass 也算）。</summary>
    public int MoveCount => _history.Count;

    /// <summary>棋谱。</summary>
    public IReadOnlyList<GoMove> History => _history;

    /// <summary>最后一手。</summary>
    public GoMove? LastMove => _history.Count > 0 ? _history[^1] : null;

    /// <summary>取某一格。</summary>
    public GoCell Get(int x, int y)
        => InBounds(x, y) ? _board[y * _size + x] : GoCell.Empty;

    /// <summary>坐标是否在棋盘内。</summary>
    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _size && y < _size;

    /// <summary>黑棋提子总数。</summary>
    public int BlackCaptures => _history.Where(m => m.Player == GoCell.Black).Sum(m => m.Captured.Count);

    /// <summary>白棋提子总数。</summary>
    public int WhiteCaptures => _history.Where(m => m.Player == GoCell.White).Sum(m => m.Captured.Count);

    /// <summary>某个点是不是打劫禁着点。</summary>
    public bool IsKoPoint(int x, int y) => _koPoint is { } ko && ko.X == x && ko.Y == y;

    // ---------------------------------------------------------------- 重置

    /// <summary>重开一局（指定路数 / 贴目）。</summary>
    public void Reset(int? size = null, double? komi = null)
    {
        if (size is { } s && AllowedSizes.Contains(s)) _size = s;
        if (komi is { } k && k >= 0) _komi = k;

        _board = new GoCell[_size * _size];
        _boardBeforeLastMove = null;
        _history.Clear();
        _turn = GoCell.Black;
        _passCount = 0;
        _koPoint = null;
        _winner = null;
        _resultText = "";
        _over = false;
    }

    // ---------------------------------------------------------------- 落子

    /// <summary>
    /// 保存「上一手之前的棋盘」，用来做标准打劫判定：
    /// <b>如果这一手落完之后棋盘跟上一手之前完全一样，就是劫</b>（这是最严谨的判法）。
    /// </summary>
    private GoCell[]? _boardBeforeLastMove;

    /// <summary>
    /// 落子。<paramref name="player"/> 必须等于当前回合方。
    /// </summary>
    /// <returns>成功返回 <see cref="GoPlayResult.Ok"/>，失败返回具体原因。</returns>
    public GoPlayResult Play(GoCell player, int x, int y)
    {
        if (_over) return GoPlayResult.GameOver;
        if (!InBounds(x, y)) return GoPlayResult.OutOfBounds;
        if (player != _turn) return GoPlayResult.NotYourTurn;
        if (Get(x, y) != GoCell.Empty) return GoPlayResult.Occupied;
        if (IsKoPoint(x, y)) return GoPlayResult.Ko;

        var opponent = Other(player);

        // 在副本上试下，判断自杀 / 打劫
        var trial = (GoCell[])_board.Clone();
        trial[y * _size + x] = player;

        var captured = new List<GoPoint>();

        // ① 先提掉没气的对方棋块
        foreach (var (nx, ny) in Neighbors(x, y))
        {
            if (trial[ny * _size + nx] != opponent) continue;
            if (HasLiberty(trial, nx, ny)) continue;
            captured.AddRange(RemoveGroup(trial, nx, ny));
        }

        // ② 再看自己的棋块有没有气（没气 = 自杀）
        if (!HasLiberty(trial, x, y) && captured.Count == 0)
            return GoPlayResult.Suicide;

        // ③ 打劫：落完之后棋盘与「上一手之前」完全一样 → 禁着（简单且标准的判法）
        if (_boardBeforeLastMove is { } before && SameBoard(trial, before))
            return GoPlayResult.Ko;

        // 真正落子（先把「这一手之前」的棋盘存下来，供下一手判劫）
        _boardBeforeLastMove = _board;
        _board = trial;
        _passCount = 0;
        _koPoint = null;

        _history.Add(new GoMove(x, y, player, captured, false));
        _turn = opponent;

        return GoPlayResult.Ok;
    }

    /// <summary>两块棋盘是否完全相同。</summary>
    private static bool SameBoard(GoCell[] a, GoCell[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    /// <summary>停一手（pass）。双方连续 pass 即终局计分。</summary>
    public GoPlayResult Pass(GoCell player)
    {
        if (_over) return GoPlayResult.GameOver;
        if (player != _turn) return GoPlayResult.NotYourTurn;

        _history.Add(new GoMove(-1, -1, player, Array.Empty<GoPoint>(), true));
        _passCount++;
        _koPoint = null;
        _boardBeforeLastMove = null;      // 停一手之后不再有劫
        _turn = Other(player);

        if (_passCount >= 2) Score();
        return GoPlayResult.Ok;
    }

    /// <summary>认输。</summary>
    public void Resign(GoCell player)
    {
        if (_over) return;
        _over = true;
        _winner = Other(player);
        _resultText = $"{PlayerName(player)}认输，{PlayerName(Other(player))}获胜";
    }

    /// <summary>悔棋（撤销最后一手）；成功返回 true。</summary>
    public bool Undo()
    {
        if (_history.Count == 0) return false;

        // 重放（简单可靠：围棋的提子使「原地撤销」很麻烦）
        var moves = _history.ToList();
        moves.RemoveAt(moves.Count - 1);

        var size = _size;
        var komi = _komi;
        Reset(size, komi);
        foreach (var move in moves)
        {
            if (move.IsPass) Pass(move.Player);
            else Play(move.Player, move.X, move.Y);
        }

        _started = true;
        return true;
    }

    // ---------------------------------------------------------------- 气 / 提子

    /// <summary>某个点所在棋块有没有气。</summary>
    private bool HasLiberty(GoCell[] board, int x, int y)
    {
        var color = board[y * _size + x];
        if (color == GoCell.Empty) return true;

        var visited = new HashSet<int>();
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        visited.Add(y * _size + x);

        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            foreach (var (nx, ny) in Neighbors(cx, cy))
            {
                var index = ny * _size + nx;
                var cell = board[index];
                if (cell == GoCell.Empty) return true;          // 有气
                if (cell != color || !visited.Add(index)) continue;
                stack.Push((nx, ny));
            }
        }
        return false;
    }

    /// <summary>数某个棋块的气数。</summary>
    private int CountLiberties(GoCell[] board, int x, int y)
    {
        var color = board[y * _size + x];
        if (color == GoCell.Empty) return 0;

        var visited = new HashSet<int> { y * _size + x };
        var liberties = new HashSet<int>();
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));

        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            foreach (var (nx, ny) in Neighbors(cx, cy))
            {
                var index = ny * _size + nx;
                var cell = board[index];
                if (cell == GoCell.Empty) { liberties.Add(index); continue; }
                if (cell != color || !visited.Add(index)) continue;
                stack.Push((nx, ny));
            }
        }
        return liberties.Count;
    }

    /// <summary>把某个棋块从棋盘上拿掉，返回被拿掉的点。</summary>
    private List<GoPoint> RemoveGroup(GoCell[] board, int x, int y)
    {
        var color = board[y * _size + x];
        var removed = new List<GoPoint>();
        if (color == GoCell.Empty) return removed;

        var visited = new HashSet<int> { y * _size + x };
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));

        while (stack.Count > 0)
        {
            var (cx, cy) = stack.Pop();
            board[cy * _size + cx] = GoCell.Empty;
            removed.Add(new GoPoint(cx, cy));

            foreach (var (nx, ny) in Neighbors(cx, cy))
            {
                var index = ny * _size + nx;
                if (board[index] != color || !visited.Add(index)) continue;
                stack.Push((nx, ny));
            }
        }
        return removed;
    }

    private IEnumerable<(int X, int Y)> Neighbors(int x, int y)
    {
        if (x > 0) yield return (x - 1, y);
        if (x < _size - 1) yield return (x + 1, y);
        if (y > 0) yield return (x, y - 1);
        if (y < _size - 1) yield return (x, y + 1);
    }

    // ---------------------------------------------------------------- 计分

    /// <summary>
    /// 按中国规则数子：棋盘上每一块「被某一方完全围住的连通空区」算那一方的地；
    /// 双方都没围住的空点不算。总分 = 自己的子 + 自己的地（白方再加贴目）。
    /// </summary>
    public GoScoreResult Score()
    {
        var black = 0;
        var white = 0;

        // 先数子
        foreach (var cell in _board)
        {
            if (cell == GoCell.Black) black++;
            else if (cell == GoCell.White) white++;
        }

        // 再数地（连通空区，看四周接触到哪些颜色）
        var visited = new bool[_board.Length];
        for (var y = 0; y < _size; y++)
        {
            for (var x = 0; x < _size; x++)
            {
                var index = y * _size + x;
                if (visited[index] || _board[index] != GoCell.Empty) continue;

                var region = new List<int>();
                var borders = new HashSet<GoCell>();
                var stack = new Stack<(int X, int Y)>();
                stack.Push((x, y));
                visited[index] = true;

                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    region.Add(cy * _size + cx);

                    foreach (var (nx, ny) in Neighbors(cx, cy))
                    {
                        var ni = ny * _size + nx;
                        var cell = _board[ni];
                        if (cell == GoCell.Empty)
                        {
                            if (!visited[ni]) { visited[ni] = true; stack.Push((nx, ny)); }
                        }
                        else
                        {
                            borders.Add(cell);
                        }
                    }
                }

                // 只被一种颜色围住的空区才算地
                if (borders.Count == 1)
                {
                    if (borders.Contains(GoCell.Black)) black += region.Count;
                    else white += region.Count;
                }
            }
        }

        var blackTotal = black;
        var whiteTotal = white + _komi;

        GoCell? winner = Math.Abs(blackTotal - whiteTotal) < 0.001
            ? null
            : blackTotal > whiteTotal ? GoCell.Black : GoCell.White;

        var diff = Math.Abs(blackTotal - whiteTotal);
        var text = winner is null
            ? $"和棋（黑 {blackTotal} : 白 {whiteTotal}，贴 {_komi} 目）"
            : $"{PlayerName(winner.Value)}胜 {diff:0.#} 子（黑 {blackTotal:0.#} : 白 {whiteTotal:0.#}，贴 {_komi} 目）";

        _over = true;
        _winner = winner;
        _resultText = text;

        return new GoScoreResult(black, white, _komi, blackTotal, whiteTotal, winner, text);
    }

    /// <summary>某一方的中文名。</summary>
    public static string PlayerName(GoCell player) => player == GoCell.Black ? "黑棋" : "白棋";

    /// <summary>另一方。</summary>
    public static GoCell Other(GoCell player) => player == GoCell.Black ? GoCell.White : GoCell.Black;

    /// <summary>键（协议 / 配置用）。</summary>
    public static string ToKey(GoCell cell) => cell switch
    {
        GoCell.Black => "black",
        GoCell.White => "white",
        _ => "",
    };

    /// <summary>键 → 棋子颜色（认不出按空）。</summary>
    public static GoCell Parse(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "black" => GoCell.Black,
        "white" => GoCell.White,
        _ => GoCell.Empty,
    };

    // ---------------------------------------------------------------- 快照（联机用）

    /// <summary>生成快照（棋盘 + 轮到谁 + 结果 + 棋谱末手）。</summary>
    public GoSnapshot ToSnapshot()
    {
        var cells = new int[_board.Length];
        for (var i = 0; i < _board.Length; i++) cells[i] = (int)_board[i];

        return new GoSnapshot
        {
            Size = _size,
            Komi = _komi,
            Cells = cells,
            Turn = ToKey(_turn),
            Started = _started,
            Over = _over,
            Winner = _winner is { } w ? ToKey(w) : "",
            ResultText = _resultText,
            MoveCount = _history.Count,
            LastX = LastMove is { IsPass: false } last ? last.X : -1,
            LastY = LastMove is { IsPass: false } last2 ? last2.Y : -1,
            KoX = _koPoint?.X ?? -1,
            KoY = _koPoint?.Y ?? -1,
            BeforeCells = _boardBeforeLastMove?.Select(c => (int)c).ToArray() ?? Array.Empty<int>(),
            BlackCaptures = BlackCaptures,
            WhiteCaptures = WhiteCaptures,
        };
    }

    /// <summary>从快照恢复（客户端用）。</summary>
    /// <returns>成功返回棋局，失败返回错误原因。</returns>
    public static (GoGame? Game, string Error) FromSnapshot(GoSnapshot? snapshot)
    {
        if (snapshot is null) return (null, "局面数据为空");

        var size = AllowedSizes.Contains(snapshot.Size) ? snapshot.Size : DefaultSize;
        var game = new GoGame(size, snapshot.Komi)
        {
            _started = snapshot.Started,
            _over = snapshot.Over,
            _winner = snapshot.Winner.Length > 0 ? Parse(snapshot.Winner) : null,
            _resultText = snapshot.ResultText ?? "",
        };

        game._board = new GoCell[size * size];
        for (var i = 0; i < game._board.Length && i < snapshot.Cells.Length; i++)
            game._board[i] = (GoCell)snapshot.Cells[i];

        game._turn = Parse(snapshot.Turn) is var turn && turn != GoCell.Empty ? turn : GoCell.Black;

        if (snapshot.KoX >= 0 && snapshot.KoY >= 0) game._koPoint = new GoPoint(snapshot.KoX, snapshot.KoY);

        if (snapshot.BeforeCells.Length == game._board.Length)
            game._boardBeforeLastMove = snapshot.BeforeCells.Select(c => (GoCell)c).ToArray();

        // 棋谱不随快照传输（太大），把步数记个大概，界面上只显示「已走 N 手」
        game._replayedMoveCount = snapshot.MoveCount;
        return (game, "");
    }

    private int _replayedMoveCount;

    /// <summary>走子步数（客户端从快照恢复后用这个字段）。</summary>
    public int DisplayMoveCount => _history.Count > 0 ? _history.Count : _replayedMoveCount;
}

/// <summary>围棋局面的传输快照。</summary>
public sealed class GoSnapshot
{
    public int Size { get; set; } = GoGame.DefaultSize;
    public double Komi { get; set; } = 7.5;
    public int[] Cells { get; set; } = Array.Empty<int>();
    public string Turn { get; set; } = "black";
    public bool Started { get; set; }
    public bool Over { get; set; }
    public string Winner { get; set; } = "";
    public string ResultText { get; set; } = "";
    public int MoveCount { get; set; }
    public int LastX { get; set; } = -1;
    public int LastY { get; set; } = -1;
    public int KoX { get; set; } = -1;
    public int KoY { get; set; } = -1;

    /// <summary>上一手之前的棋盘（判劫用；空 = 没有劫基准）。</summary>
    public int[] BeforeCells { get; set; } = Array.Empty<int>();
    public int BlackCaptures { get; set; }
    public int WhiteCaptures { get; set; }
}
