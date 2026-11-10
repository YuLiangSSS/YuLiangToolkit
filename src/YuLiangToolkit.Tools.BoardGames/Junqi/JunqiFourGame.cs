using YuLiangToolkit.Tools.BoardGames.Junqi;

namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>
/// 四国军棋对局（<b>纯逻辑</b>）：4 个阵营、对家同盟，夺对方<b>两面军旗</b>的一队获胜。
///
/// <para><b>与二人军棋的区别</b>：
/// <list type="bullet">
///   <item>棋盘更大（13×13 十字），每方 25 枚棋子；</item>
///   <item>行棋顺序按「红 → 绿 → 蓝 → 黄」顺时针；</item>
///   <item><b>同盟队伤</b>：碰到同盟的棋子<b>不判裁判</b>，也不允许吃（按非法着法处理）；</item>
///   <item><b>暗棋</b>：同二人军棋，自己全可见，别人的只有被吃掉的才明；</item>
///   <item><b>胜负</b>：一队夺到对方两面军旗即胜；或对方两人的能动棋子都没了；或认输。</item>
/// </list></para>
/// </summary>
public sealed class JunqiFourGame
{
    private readonly JunqiPiece[] _board = new JunqiPiece[JunqiFourBoard.CellCount];
    private readonly List<JunqiFourMoveRecord> _history = new();
    private readonly Dictionary<int, JunqiRank> _knownRanks = new();
    private readonly HashSet<JunqiNation> _deployed = new();

    /// <summary>创建一局。</summary>
    public JunqiFourGame() => Reset();

    // ---------------------------------------------------------------- 状态

    /// <summary>当前阶段。</summary>
    public JunqiPhase Phase { get; private set; } = JunqiPhase.Waiting;

    /// <summary>轮到哪个阵营。</summary>
    public JunqiNation CurrentNation { get; private set; } = JunqiNation.Red;

    /// <summary>赢的队（用队里任一阵营表示；null = 还没结束）。</summary>
    public JunqiNation? WinnerNation { get; private set; }

    /// <summary>结束原因。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>棋盘。</summary>
    public IReadOnlyList<JunqiPiece> Board => _board;

    /// <summary>走子记录。</summary>
    public IReadOnlyList<JunqiFourMoveRecord> History => _history;

    /// <summary>取某一格。</summary>
    public JunqiPiece At(int x, int y)
        => JunqiFourBoard.InBounds(x, y) ? _board[y * JunqiFourBoard.Size + x] : JunqiPiece.Empty;

    /// <summary>某一方布阵了没有。</summary>
    public bool IsDeployed(JunqiNation nation) => _deployed.Contains(nation);

    /// <summary>四方都布好阵了没有。</summary>
    public bool AllDeployed => _deployed.Count >= 4;

    /// <summary>重置。</summary>
    public void Reset()
    {
        Array.Clear(_board);
        _history.Clear();
        _knownRanks.Clear();
        _deployed.Clear();
        Phase = JunqiPhase.Waiting;
        CurrentNation = JunqiNation.Red;
        WinnerNation = null;
        ResultText = "";
    }

    // ---------------------------------------------------------------- 布阵

    /// <summary>布阵（每方 25 枚）。</summary>
    public string Deploy(JunqiNation nation, IReadOnlyDictionary<(int X, int Y), JunqiRank> deploy)
    {
        if (Phase is not (JunqiPhase.Waiting or JunqiPhase.Deploying)) return "现在不是布阵阶段";
        if (IsDeployed(nation)) return $"{JunqiFourBoard.DisplayName(nation)}已经布好阵了";

        var points = JunqiFourBoard.DeployPoints(nation);
        var flags = 0;
        foreach (var (point, rank) in deploy)
        {
            if (JunqiFourBoard.NationAt(point.X, point.Y) != nation) return "不能把棋子摆到别人的地盘";
            if (!points.Contains(point)) return "这个位置不能摆棋";
            if (rank == JunqiRank.Flag) flags++;
        }

        if (flags != 1) return "军旗必须摆 1 面";
        if (deploy.Count != JunqiRanks.ArmySize) return $"要摆满 {JunqiRanks.ArmySize} 枚棋子";

        var expected = JunqiRanks.CreateArmy().GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        var actual = deploy.Values.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (rank, count) in expected)
        {
            actual.TryGetValue(rank, out var have);
            if (have != count) return $"{JunqiRanks.DisplayName(rank)} 应该有 {count} 枚，实际 {have} 枚";
        }

        foreach (var (point, rank) in deploy)
            _board[point.Y * JunqiFourBoard.Size + point.X] =
                new JunqiPiece(rank, SideOf(nation), false);

        // 记录阵营（JunqiPiece 只有 Red/Blue 两方，四国用「格子归属」推阵营）
        _deployed.Add(nation);
        Phase = _deployed.Count >= 4 ? JunqiPhase.Playing : JunqiPhase.Deploying;
        if (Phase == JunqiPhase.Playing) CurrentNation = JunqiNation.Red;
        return "";
    }

    /// <summary>自动布阵（随机摆）。</summary>
    public string AutoDeploy(JunqiNation nation, Random? random = null)
    {
        var points = JunqiFourBoard.DeployPoints(nation).ToList();
        var army = JunqiRanks.CreateArmy();
        var rng = random ?? Random.Shared;
        for (var i = army.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (army[i], army[j]) = (army[j], army[i]);
        }

        var deploy = new Dictionary<(int X, int Y), JunqiRank>();
        for (var i = 0; i < points.Count && i < army.Count; i++) deploy[points[i]] = army[i];
        return Deploy(nation, deploy);
    }

    // ---------------------------------------------------------------- 走棋

    /// <summary>某一方的全部合法走法。</summary>
    public IReadOnlyList<JunqiRules.Move> LegalMoves(JunqiNation nation)
    {
        var moves = new List<JunqiRules.Move>();
        if (Phase != JunqiPhase.Playing || nation != CurrentNation) return moves;

        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (JunqiFourBoard.NationAt(x, y) != nation) continue;
                var piece = At(x, y);
                if (piece.IsEmpty || !piece.CanMove) continue;

                foreach (var (tx, ty) in LegalTargets(nation, x, y))
                    moves.Add(new JunqiRules.Move(x, y, tx, ty));
            }
        }
        return moves;
    }

    /// <summary>某个棋子的合法落点（含同盟不能吃、行营安全、大本营不能动、铁路规则）。</summary>
    public IReadOnlyList<(int X, int Y)> LegalTargets(JunqiNation nation, int x, int y)
    {
        var piece = At(x, y);
        if (piece.IsEmpty || !piece.CanMove) return Array.Empty<(int X, int Y)>();
        if (JunqiFourBoard.IsHeadquarters(x, y)) return Array.Empty<(int X, int Y)>();

        var targets = new List<(int X, int Y)>();
        var visited = new HashSet<int> { y * JunqiFourBoard.Size + x };

        // 公路一格
        foreach (var (dx, dy) in Neighbors)
        {
            var nx = x + dx;
            var ny = y + dy;
            if (CanLand(nation, nx, ny) && visited.Add(ny * JunqiFourBoard.Size + nx)) targets.Add((nx, ny));
        }

        // 铁路：直线多格；工兵拐弯
        if (JunqiFourBoard.IsRailway(x, y))
        {
            var queue = new Queue<(int X, int Y)>();
            queue.Enqueue((x, y));
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                foreach (var (dx, dy) in Neighbors)
                {
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if (!JunqiFourBoard.InBounds(nx, ny)) continue;
                    if (!JunqiFourBoard.IsRailway(nx, ny)) continue;

                    var index = ny * JunqiFourBoard.Size + nx;
                    var target = At(nx, ny);
                    if (!target.IsEmpty)
                    {
                        if (CanLand(nation, nx, ny) && visited.Add(index)) targets.Add((nx, ny));
                        continue;                 // 碰到子就停（工兵也一样，不能穿子）
                    }

                    if (!visited.Add(index)) continue;
                    targets.Add((nx, ny));
                    // 非工兵只能走直线：只有工兵继续 BFS 拐弯
                    if (piece.Rank == JunqiRank.Engineer) queue.Enqueue((nx, ny));
                }
            }

            // 非工兵的「直线」需要在上面 BFS 之外单独处理（BFS 只在工兵时入队）
            if (piece.Rank != JunqiRank.Engineer)
            {
                foreach (var (dx, dy) in Neighbors)
                {
                    var cx = x;
                    var cy = y;
                    while (true)
                    {
                        cx += dx;
                        cy += dy;
                        if (!JunqiFourBoard.InBounds(cx, cy)) break;
                        if (!JunqiFourBoard.IsRailway(cx, cy)) break;

                        var index = cy * JunqiFourBoard.Size + cx;
                        var target = At(cx, cy);
                        if (!target.IsEmpty)
                        {
                            if (CanLand(nation, cx, cy) && visited.Add(index)) targets.Add((cx, cy));
                            break;
                        }
                        if (visited.Add(index)) targets.Add((cx, cy));
                    }
                }
            }
        }

        return targets;
    }

    private bool CanLand(JunqiNation nation, int x, int y)
    {
        if (!JunqiFourBoard.InBounds(x, y)) return false;
        var target = At(x, y);
        if (target.IsEmpty) return true;

        var owner = JunqiFourBoard.NationAt(x, y);
        if (owner is { } o && JunqiFourBoard.IsAlly(nation, o)) return false;   // 同盟不能吃
        if (JunqiFourBoard.IsCamp(x, y)) return false;                           // 行营里的子不能被吃
        return true;
    }

    /// <summary>走一步（含自动裁判）。</summary>
    public string Move(JunqiNation nation, int fromX, int fromY, int toX, int toY)
    {
        if (Phase != JunqiPhase.Playing) return "对局还没开始";
        if (nation != CurrentNation) return "还没轮到你走";

        var piece = At(fromX, fromY);
        if (piece.IsEmpty) return "这一格没有棋子";
        if (JunqiFourBoard.NationAt(fromX, fromY) != nation) return "这不是你的棋子";
        if (!LegalTargets(nation, fromX, fromY).Contains((toX, toY))) return "这一步走不了";

        var defender = At(toX, toY);
        var outcome = defender.IsEmpty
            ? JunqiRules.JudgeOutcome.AttackerWins
            : JunqiRules.Judge(piece.Rank, defender.Rank);

        if (!defender.IsEmpty)
        {
            var owner = JunqiFourBoard.NationAt(toX, toY);
            if (owner is not null) _knownRanks[toY * JunqiFourBoard.Size + toX] = defender.Rank;

            // 夺取军旗：一队要夺到对方两面旗才算赢（CheckTeamVictory 统一判）
            _ = owner;
        }

        _history.Add(new JunqiFourMoveRecord(fromX, fromY, toX, toY, nation, piece, defender, outcome));

        var fromIndex = fromY * JunqiFourBoard.Size + fromX;
        var toIndex = toY * JunqiFourBoard.Size + toX;

        switch (outcome)
        {
            case JunqiRules.JudgeOutcome.AttackerWins:
            case JunqiRules.JudgeOutcome.CaptureFlag:
                _board[fromIndex] = JunqiPiece.Empty;
                _board[toIndex] = piece;
                break;
            case JunqiRules.JudgeOutcome.BothDie:
                _board[fromIndex] = JunqiPiece.Empty;
                _board[toIndex] = JunqiPiece.Empty;
                break;
            case JunqiRules.JudgeOutcome.DefenderWins:
                _board[fromIndex] = JunqiPiece.Empty;
                break;
        }

        if (outcome == JunqiRules.JudgeOutcome.CaptureFlag)
        {
            CheckTeamVictory();
            if (Phase == JunqiPhase.Playing) CurrentNation = NextTurn(nation);
            return "";
        }

        CheckTeamVictory();
        if (Phase == JunqiPhase.Playing) CurrentNation = NextTurn(nation);
        return "";
    }

    /// <summary>认输（整队认输）。</summary>
    public void Resign(JunqiNation nation)
    {
        if (Phase != JunqiPhase.Playing) return;
        var winner = JunqiFourBoard.Ally(nation);
        Finish(winner, $"{JunqiFourBoard.DisplayName(nation)}认输，{JunqiFourBoard.DisplayName(winner)}那队获胜");
    }

    /// <summary>
    /// 阵营 → 棋子的 <see cref="JunqiSide"/>：只有红方算 Red，其它三个阵营都算 Blue。
    /// <para>四色由界面按格子归属（<see cref="JunqiFourBoard.NationAt"/>）着色，
    /// 这里只用来做「是不是红方」的粗判断。</para>
    /// </summary>
    public static JunqiSide SideOf(JunqiNation nation)
        => nation == JunqiNation.Red ? JunqiSide.Red : JunqiSide.Blue;

    /// <summary>下一个该走的阵营（跳过已经出局的队）。</summary>
    private JunqiNation NextTurn(JunqiNation from)
    {
        var next = JunqiFourBoard.Next(from);
        for (var i = 0; i < 4; i++)
        {
            if (HasAnyPiece(next) || HasAnyPiece(JunqiFourBoard.Ally(next))) return next;
            next = JunqiFourBoard.Next(next);
        }
        return next;
    }

    /// <summary>胜负：一队两面军旗全被夺 / 一队两人都没能动的子。</summary>
    private void CheckTeamVictory()
    {
        for (var i = 0; i < 4; i++)
        {
            var nation = (JunqiNation)i;
            var ally = JunqiFourBoard.Ally(nation);
            if (nation == ally) continue;

            // 只处理「红 / 蓝」两个代表，避免重复判定
            if (nation is JunqiNation.Green or JunqiNation.Yellow) continue;

            var flags = CountFlags(nation) + CountFlags(ally);
            if (flags == 0)
            {
                var other = JunqiFourBoard.Ally(nation);   // 对面那队
                Finish(other, $"{JunqiFourBoard.DisplayName(nation)}队的军旗全被夺，{JunqiFourBoard.DisplayName(other)}队获胜");
                return;
            }

            var movable = CountMovable(nation) + CountMovable(ally);
            if (movable == 0)
            {
                var other = JunqiFourBoard.Ally(nation);
                Finish(other, $"{JunqiFourBoard.DisplayName(nation)}队已无子可走，{JunqiFourBoard.DisplayName(other)}队获胜");
                return;
            }
        }
    }

    private void Finish(JunqiNation winner, string text)
    {
        Phase = JunqiPhase.Finished;
        WinnerNation = winner;
        ResultText = text;
    }

    private int CountFlags(JunqiNation nation)
    {
        var count = 0;
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (JunqiFourBoard.NationAt(x, y) != nation) continue;
                var piece = At(x, y);
                if (!piece.IsEmpty && piece.Rank == JunqiRank.Flag) count++;
            }
        }
        return count;
    }

    private bool HasAnyPiece(JunqiNation nation)
    {
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (JunqiFourBoard.NationAt(x, y) != nation) continue;
                if (!At(x, y).IsEmpty) return true;
            }
        }
        return false;
    }

    private int CountMovable(JunqiNation nation)
    {
        var count = 0;
        for (var y = 0; y < JunqiFourBoard.Size; y++)
        {
            for (var x = 0; x < JunqiFourBoard.Size; x++)
            {
                if (JunqiFourBoard.NationAt(x, y) != nation) continue;
                var piece = At(x, y);
                if (!piece.IsEmpty && piece.CanMove) count++;
            }
        }
        return count;
    }

    // ---------------------------------------------------------------- 快照（按人过滤暗棋）

    /// <summary>
    /// 给某个阵营看的棋盘：自己的全可见、同盟全可见（同盟是队友），其余对手只有已知的可见。
    /// <para><b>颜色</b>：<see cref="JunqiPiece"/> 只有红 / 蓝两个 <c>Side</c> 值，四国靠「格子归属」推阵营。
    /// 这里把蓝 / 绿 / 黄都映射成 <see cref="JunqiSide.Blue"/>（非红），
    /// 界面上真正区分阵营用 <see cref="JunqiCellItem.NationKey"/> —— 不要指望 <c>Side</c> 能分出四色。</para>
    /// </summary>
    public IReadOnlyList<JunqiPiece> VisibleBoardFor(JunqiNation viewer)
    {
        var board = new JunqiPiece[_board.Length];
        for (var i = 0; i < _board.Length; i++)
        {
            var piece = _board[i];
            if (piece.IsEmpty) { board[i] = JunqiPiece.Empty; continue; }

            var owner = JunqiFourBoard.NationAt(i % JunqiFourBoard.Size, i / JunqiFourBoard.Size);
            if (owner is { } nation && JunqiFourBoard.IsAlly(viewer, nation))
            {
                board[i] = piece.WithRevealed(true);
                continue;
            }

            board[i] = _knownRanks.TryGetValue(i, out var known)
                ? new JunqiPiece(known, piece.Side, true)
                : JunqiPiece.Unknown(piece.Side);
        }
        return board;
    }

    private static readonly (int Dx, int Dy)[] Neighbors = { (0, -1), (0, 1), (-1, 0), (1, 0) };
}

/// <summary>四国军棋的一步记录。</summary>
public sealed record JunqiFourMoveRecord(
    int FromX, int FromY, int ToX, int ToY,
    JunqiNation Nation, JunqiPiece Attacker, JunqiPiece Defender, JunqiRules.JudgeOutcome Outcome);
