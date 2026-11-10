namespace YuLiangToolkit.Tools.BoardGames.Checkers;

/// <summary>规则开关。</summary>
public sealed class CheckersRules
{
    /// <summary>除了跳跃，是否允许走一格到相邻空位（房主开局前选）。</summary>
    public bool AllowSingleStep { get; set; } = true;

    /// <summary>是否要求只有自己初始区域里还有棋子时，只能先动初始区域的棋子（防卡出生点）。</summary>
    public bool MustClearHomeFirst { get; set; } = true;

    /// <summary>复制。</summary>
    public CheckersRules Clone() => new()
    {
        AllowSingleStep = AllowSingleStep,
        MustClearHomeFirst = MustClearHomeFirst,
    };
}

/// <summary>一次移动（起点 → 终点 + 途经点，用于画连跳路线）。</summary>
/// <param name="FromX">起点 X。</param>
/// <param name="FromY">起点 Y。</param>
/// <param name="ToX">终点 X。</param>
/// <param name="ToY">终点 Y。</param>
/// <param name="Path">整条路径（含起点，依次经过的格子）。</param>
public sealed record CheckersMove(int FromX, int FromY, int ToX, int ToY, IReadOnlyList<(int X, int Y)> Path)
{
    /// <summary>
    /// 跳了几次（0 = 只走一格到相邻点）。
    /// <para>路径点数是「起点 + 每次落点」：一次跳 = 2 个点 → 跳 1 次；连跳 3 次 = 4 个点 → 跳 3 次。
    /// 所以是 <c>Path.Count - 1</c>，<b>不是</b> <c>-2</c>（早期写错过，导致「跳」被当成「走一格」）。</para>
    /// </summary>
    public int Jumps => Math.Max(0, Path.Count - 1);
}

/// <summary>一个玩家（占一个角）。</summary>
public sealed class CheckersPlayer
{
    /// <summary>玩家 id。</summary>
    public string Id { get; set; } = "";

    /// <summary>显示名。</summary>
    public string Name { get; set; } = "";

    /// <summary>占据的角。</summary>
    public CheckersPoint Home { get; set; }

    /// <summary>目标角（对面）。</summary>
    public CheckersPoint Target => CheckersBoard.Opposite(Home);

    /// <summary>是不是 AI。</summary>
    public bool IsAi { get; set; }

    /// <summary>AI 难度（1~3）。</summary>
    public int AiLevel { get; set; } = 2;

    /// <summary>这个玩家的棋子坐标。</summary>
    public HashSet<(int X, int Y)> Pieces { get; } = new();

    /// <summary>还有几枚棋子没到家。</summary>
    public int Remaining => Pieces.Count;
}

/// <summary>
/// 跳棋对局（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>规则</b>：</para>
/// <list type="bullet">
///   <item>棋子放在自己角的 10 个点上；</item>
///   <item><b>跳</b>：沿六个方向跨过紧邻的一枚棋子、落到它后面的空位，可以连跳（可拐弯）；</item>
///   <item>可选<b>走一格</b>到相邻空位（房主开局前设）；</item>
///   <item><b>占满对面角全部 10 个点</b>才获胜；</item>
///   <item><b>防卡出生点</b>：自己初始区域还有棋子时，只能动初始区域里的棋子；</item>
///   <item><b>让棋</b>：开局前可以删掉自己区域里的若干棋子（降低难度，房主可限制权限）。</item>
/// </list>
/// </summary>
public sealed class CheckersGame
{
    private readonly Dictionary<(int X, int Y), string> _board = new();
    private readonly List<CheckersPlayer> _players = new();
    private readonly List<string> _turnOrder = new();
    private int _turnIndex;
    private string _lastText = "";

    /// <summary>创建一局。</summary>
    /// <param name="rules">规则。</param>
    public CheckersGame(CheckersRules? rules = null) => Rules = rules ?? new CheckersRules();

    /// <summary>规则。</summary>
    public CheckersRules Rules { get; }

    /// <summary>玩家。</summary>
    public IReadOnlyList<CheckersPlayer> Players => _players;

    /// <summary>按行动顺序排的玩家 id。</summary>
    public IReadOnlyList<string> TurnOrder => _turnOrder;

    /// <summary>现在轮到谁（玩家 id）。</summary>
    public string CurrentPlayerId => _turnOrder.Count == 0 ? "" : _turnOrder[_turnIndex];

    /// <summary>对局是否结束。</summary>
    public bool IsOver { get; private set; }

    /// <summary>赢家（玩家 id）。</summary>
    public string WinnerId { get; private set; } = "";

    /// <summary>结果文字。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>最近一步说明。</summary>
    public string LastText => _lastText;

    /// <summary>某一格上的棋子属于谁（空 = null）。</summary>
    public string? OwnerAt(int x, int y) => _board.TryGetValue((x, y), out var id) ? id : null;

    // ---------------------------------------------------------------- 开局

    /// <summary>加入一个玩家（占一个角）。</summary>
    public string AddPlayer(string id, string name, CheckersPoint home, bool isAi = false, int aiLevel = 2)
    {
        if (_players.Any(p => p.Id == id)) return "这个玩家已经在局里了";
        if (_players.Any(p => p.Home == home)) return $"{CheckersBoard.DisplayName(home)}角已经有人了";

        var player = new CheckersPlayer
        {
            Id = id,
            Name = name,
            Home = home,
            IsAi = isAi,
            AiLevel = aiLevel,
        };

        foreach (var cell in CheckersBoard.CellsOf(home))
        {
            player.Pieces.Add((cell.X, cell.Y));
        }

        _players.Add(player);
        return "";
    }

    /// <summary>设置行动顺序（房主可指定 / 随机抽签）。</summary>
    public string SetTurnOrder(IReadOnlyList<string> order)
    {
        var ids = _players.Select(p => p.Id).ToList();
        if (order.Count != ids.Count) return "顺序人数不对";
        if (order.Distinct().Count() != order.Count) return "顺序里有重复的人";
        foreach (var id in order)
        {
            if (!ids.Contains(id)) return "顺序里有不在局里的人";
        }

        _turnOrder.Clear();
        _turnOrder.AddRange(order);
        _turnIndex = 0;
        return "";
    }

    /// <summary>开局：把棋子摆到棋盘上。</summary>
    public string Start()
    {
        if (_players.Count < 2) return "至少需要两个玩家";
        if (_turnOrder.Count == 0)
        {
            foreach (var player in _players) _turnOrder.Add(player.Id);
            _turnIndex = 0;
        }

        _board.Clear();
        foreach (var player in _players)
        {
            foreach (var (x, y) in player.Pieces) _board[(x, y)] = player.Id;
        }

        IsOver = false;
        WinnerId = "";
        ResultText = "";
        _lastText = "";
        return "";
    }

    /// <summary>让棋：删掉某个玩家自己区域里的若干棋子（降低难度）。</summary>
    public string RemovePiece(string playerId, int x, int y)
    {
        var player = _players.FirstOrDefault(p => p.Id == playerId);
        if (player is null) return "找不到这个玩家";

        // 只能删自己初始区域里、还没动过的棋子
        if (CheckersBoard.CellAt(x, y)?.Point != player.Home) return "只能删自己区域里的棋子";
        if (!player.Pieces.Remove((x, y))) return "这里没有你的棋子";
        _board.Remove((x, y));

        _lastText = $"{player.Name} 让掉一枚棋子";
        return "";
    }

    // ---------------------------------------------------------------- 走法

    /// <summary>某个玩家全部合法走法。</summary>
    public IReadOnlyList<CheckersMove> LegalMoves(string playerId)
    {
        var player = _players.FirstOrDefault(p => p.Id == playerId);
        if (player is null || IsOver) return Array.Empty<CheckersMove>();

        var movable = MovablePieces(player);
        var moves = new List<CheckersMove>();

        foreach (var (x, y) in movable)
        {
            // ① 跳（可连跳）
            var jumps = FindJumps(x, y, playerId);
            moves.AddRange(jumps);

            // ② 走一格（如果开了）
            if (Rules.AllowSingleStep)
            {
                foreach (var (nx, ny) in CheckersBoard.NeighborsOf(x, y))
                {
                    if (_board.ContainsKey((nx, ny))) continue;

                    // 单步：路径只放起点（Jumps = Path.Count - 1 = 0，明确区别于「跳」）
                    moves.Add(new CheckersMove(x, y, nx, ny, new[] { (x, y) }));
                }
            }
        }

        return moves;
    }

    /// <summary>
    /// 现在能动的棋子。
    /// <para>「防卡出生点」：自己初始区域还有棋子时，只能动初始区域里的棋子。</para>
    /// </summary>
    private List<(int X, int Y)> MovablePieces(CheckersPlayer player)
    {
        var all = player.Pieces.ToList();
        if (!Rules.MustClearHomeFirst) return all;

        var stillHome = all.Where(p => CheckersBoard.CellAt(p.X, p.Y)?.Point == player.Home).ToList();
        return stillHome.Count > 0 ? stillHome : all;
    }

    /// <summary>找某个棋子的所有连跳路线。</summary>
    private List<CheckersMove> FindJumps(int x, int y, string playerId)
    {
        var results = new List<CheckersMove>();
        var path = new List<(int X, int Y)> { (x, y) };
        var visited = new HashSet<(int X, int Y)> { (x, y) };
        Walk(x, y);

        return results;

        void Walk(int cx, int cy)
        {
            var jumped = false;

            foreach (var (nx, ny) in CheckersBoard.NeighborsOf(cx, cy))
            {
                // 中间必须有一枚棋子（任何人的都行——跳棋可以跳过自己的棋子）
                if (!_board.ContainsKey((nx, ny))) continue;

                // 落点：沿同方向再走一格
                var dx = nx - cx;
                var dy = ny - cy;
                var lx = nx + dx;
                var ly = ny + dy;
                if (!CheckersBoard.Exists(lx, ly)) continue;
                if (_board.ContainsKey((lx, ly))) continue;
                if (visited.Contains((lx, ly))) continue;

                jumped = true;
                path.Add((lx, ly));
                visited.Add((lx, ly));
                Walk(lx, ly);

                // 记录当前路线（每一步落点都算一个合法终点）
                results.Add(new CheckersMove(x, y, lx, ly, path.ToList()));

                visited.Remove((lx, ly));
                path.RemoveAt(path.Count - 1);
            }

            _ = jumped;
        }
    }

    /// <summary>走一步。</summary>
    public string Move(string playerId, int fromX, int fromY, int toX, int toY)
    {
        if (IsOver) return "对局已经结束";
        if (CurrentPlayerId != playerId) return "还没轮到你";

        var legal = LegalMoves(playerId).FirstOrDefault(m =>
            m.FromX == fromX && m.FromY == fromY && m.ToX == toX && m.ToY == toY);
        if (legal is null) return "这一步走不了";

        _board.Remove((fromX, fromY));
        _board[(toX, toY)] = playerId;

        var player = _players.First(p => p.Id == playerId);
        player.Pieces.Remove((fromX, fromY));
        player.Pieces.Add((toX, toY));

        _lastText = $"{player.Name} 走了 {legal.Path.Count - 1} 步" + (legal.Jumps > 0 ? $"（跳 {legal.Jumps} 次）" : "");

        CheckWin(player);
        if (!IsOver) AdvanceTurn();
        return "";
    }

    /// <summary>跳过（没得走时用）。</summary>
    public void Pass(string playerId)
    {
        if (IsOver || CurrentPlayerId != playerId) return;
        _lastText = $"{_players.FirstOrDefault(p => p.Id == playerId)?.Name} 没得走，跳过";
        AdvanceTurn();
    }

    private void AdvanceTurn()
    {
        if (_turnOrder.Count == 0) return;
        _turnIndex = (_turnIndex + 1) % _turnOrder.Count;
    }

    /// <summary>胜负：占满对面角全部 10 个点。</summary>
    private void CheckWin(CheckersPlayer player)
    {
        var target = CheckersBoard.CellsOf(player.Target);
        var onTarget = target.Count(c => _board.TryGetValue((c.X, c.Y), out var id) && id == player.Id);

        if (onTarget >= CheckersBoard.PointSize)
        {
            IsOver = true;
            WinnerId = player.Id;
            ResultText = $"{player.Name} 占满{CheckersBoard.DisplayName(player.Target)}角，获胜！";
        }
    }

    // ---------------------------------------------------------------- AI 试走（成对调用）

    /// <summary>
    /// AI 前瞻用：把一步棋直接落到棋盘上（<b>不校验、不换人</b>），返回可撤销的凭据。
    /// <para>⚠ 仅供 AI 成对调用 <see cref="UndoForSimulation"/>；正式走棋请用 <see cref="Move"/>。</para>
    /// </summary>
    public (int FromX, int FromY, int ToX, int ToY) ApplyForSimulation(string playerId, CheckersMove move)
    {
        _board.Remove((move.FromX, move.FromY));
        _board[(move.ToX, move.ToY)] = playerId;

        var player = _players.First(p => p.Id == playerId);
        player.Pieces.Remove((move.FromX, move.FromY));
        player.Pieces.Add((move.ToX, move.ToY));

        return (move.FromX, move.FromY, move.ToX, move.ToY);
    }

    /// <summary>AI 前瞻用：撤销 <see cref="ApplyForSimulation"/> 的那一步。</summary>
    public void UndoForSimulation(string playerId, (int FromX, int FromY, int ToX, int ToY) undo)
    {
        _board.Remove((undo.ToX, undo.ToY));
        _board[(undo.FromX, undo.FromY)] = playerId;

        var player = _players.First(p => p.Id == playerId);
        player.Pieces.Remove((undo.ToX, undo.ToY));
        player.Pieces.Add((undo.FromX, undo.FromY));
    }

    /// <summary>某个玩家有多少棋子到了目标角。</summary>
    public int OnTargetCount(string playerId)
    {
        var player = _players.FirstOrDefault(p => p.Id == playerId);
        if (player is null) return 0;

        return player.Pieces.Count(p => CheckersBoard.CellAt(p.X, p.Y)?.Point == player.Target);
    }
}
