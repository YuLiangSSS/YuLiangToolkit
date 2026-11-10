namespace YuLiangToolkit.Tools.BoardGames.Junqi;

/// <summary>军棋对局阶段。</summary>
public enum JunqiPhase
{
    /// <summary>等待开始。</summary>
    Waiting = 0,

    /// <summary>布阵中（双方摆自己的 25 枚棋子）。</summary>
    Deploying = 1,

    /// <summary>对局中。</summary>
    Playing = 2,

    /// <summary>结束了。</summary>
    Finished = 3,
}

/// <summary>一局军棋的步录（复盘 / 记录用）。</summary>
/// <param name="FromX">起点 X。</param>
/// <param name="FromY">起点 Y。</param>
/// <param name="ToX">终点 X。</param>
/// <param name="ToY">终点 Y。</param>
/// <param name="Attacker">进攻方棋子。</param>
/// <param name="Defender">被攻击的棋子（空 = 走到空位）。</param>
/// <param name="Outcome">裁判结果。</param>
public sealed record JunqiMoveRecord(
    int FromX, int FromY, int ToX, int ToY,
    JunqiPiece Attacker, JunqiPiece Defender, JunqiRules.JudgeOutcome Outcome);

/// <summary>
/// 一局二人军棋的<b>完整状态机</b>（纯逻辑，不碰界面 / 网络）。
///
/// <para><b>暗棋规则</b>：双方各自摆好棋子后，对方的棋子只有「明棋」状态才可见 ——
/// 裁判由引擎自动做（碰子后按 <see cref="JunqiRules.Judge"/> 结算）。
/// 每个棋子在被吃掉的瞬间向双方揭示（记录在 <see cref="KnownRanks"/> 里），
/// 这是最主流的「军棋暗棋 + 自动裁判」玩法。</para>
///
/// <para><b>胜负</b>：① 夺旗；② 一方没有能动的子（子被吃光 / 只剩地雷军旗）；
/// ③ 一方认输。</para>
/// </summary>
public sealed class JunqiGame
{
    private readonly JunqiPiece[] _board = new JunqiPiece[JunqiBoard.CellCount];
    private readonly List<JunqiMoveRecord> _history = new();

    /// <summary>
    /// 双方已知的棋子等级：键 = 格子索引，值 = 已经揭晓的等级。
    /// 棋子被吃掉 / 军旗被夺时记进来，两边都能看（复盘与显示用）。
    /// </summary>
    private readonly Dictionary<int, JunqiRank> _knownRanks = new();

    /// <summary>创建一局。</summary>
    public JunqiGame() => Reset();

    // ---------------------------------------------------------------- 状态

    /// <summary>当前阶段。</summary>
    public JunqiPhase Phase { get; private set; } = JunqiPhase.Waiting;

    /// <summary>当前轮到谁走。</summary>
    public JunqiSide CurrentSide { get; private set; } = JunqiSide.Red;

    /// <summary>赢家（<see cref="JunqiPhase.Finished"/> 时有效；null = 还没结束）。</summary>
    public JunqiSide? Winner { get; private set; }

    /// <summary>结束原因。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>棋盘（只读快照）。</summary>
    public IReadOnlyList<JunqiPiece> Board => _board;

    /// <summary>走子记录。</summary>
    public IReadOnlyList<JunqiMoveRecord> History => _history;

    /// <summary>取某一格。</summary>
    public JunqiPiece At(int x, int y)
        => JunqiBoard.InBounds(x, y) ? _board[JunqiBoard.IndexOf(x, y)] : JunqiPiece.Empty;

    /// <summary>某一方已经布好阵了没有。</summary>
    public bool IsDeployed(JunqiSide side) => side == JunqiSide.Red ? _redReady : _blueReady;

    private bool _redReady;
    private bool _blueReady;

    // ---------------------------------------------------------------- 开局 / 布阵

    /// <summary>重置（回到布阵阶段）。</summary>
    public void Reset()
    {
        Array.Clear(_board);
        _history.Clear();
        _knownRanks.Clear();
        Phase = JunqiPhase.Waiting;
        CurrentSide = JunqiSide.Red;
        Winner = null;
        ResultText = "";
        _redReady = false;
        _blueReady = false;
    }

    /// <summary>
    /// 布阵：把 25 枚棋子摆到自己一侧的 25 个点上。
    /// <paramref name="deploy"/> 的键是 (X, Y)、值是棋子等级。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public string Deploy(JunqiSide side, IReadOnlyDictionary<(int X, int Y), JunqiRank> deploy)
    {
        if (Phase != JunqiPhase.Waiting && Phase != JunqiPhase.Deploying) return "现在不是布阵阶段";
        if (deploy is null) return "没有布阵数据";
        if (IsDeployed(side)) return $"{SideName(side)}已经布好阵了，不能重复布阵";

        var points = JunqiBoard.DeployPoints(side);
        var flags = 0;
        var used = new HashSet<(int X, int Y)>();

        foreach (var (point, rank) in deploy)
        {
            if (!JunqiBoard.BelongsTo(side, point.X)) return "不能把棋子摆到对方一侧";
            if (!points.Contains(point)) return $"({point.X},{point.Y}) 不是可以摆棋的位置";
            if (!used.Add(point)) return "同一个位置摆了两次";

            if (rank == JunqiRank.Flag) flags++;
        }

        if (flags != 1) return "军旗必须摆 1 面（大本营里的那面）";
        if (deploy.Count != JunqiRanks.ArmySize) return $"要摆满 {JunqiRanks.ArmySize} 枚棋子";

        // 校验数量与标准配置一致
        var expected = JunqiRanks.CreateArmy().GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        var actual = deploy.Values.GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (rank, count) in expected)
        {
            actual.TryGetValue(rank, out var have);
            if (have != count) return $"{JunqiRanks.DisplayName(rank)} 应该有 {count} 枚，实际 {have} 枚";
        }

        // 写入棋盘
        foreach (var (point, rank) in deploy)
            _board[JunqiBoard.IndexOf(point.X, point.Y)] = new JunqiPiece(rank, side, false);

        if (side == JunqiSide.Red) _redReady = true;
        else _blueReady = true;

        Phase = _redReady && _blueReady ? JunqiPhase.Playing : JunqiPhase.Deploying;
        if (Phase == JunqiPhase.Playing) CurrentSide = JunqiSide.Red;
        return "";
    }

    /// <summary>用「默认阵型」自动布阵（自检 / 快速开局 / 托管用）。</summary>
    public string AutoDeploy(JunqiSide side, Random? random = null)
    {
        var points = JunqiBoard.DeployPoints(side).ToList();
        var army = JunqiRanks.CreateArmy();
        CardShuffle(army, random ?? Random.Shared);

        var deploy = new Dictionary<(int X, int Y), JunqiRank>();
        for (var i = 0; i < points.Count && i < army.Count; i++) deploy[points[i]] = army[i];
        return Deploy(side, deploy);
    }

    private static void CardShuffle<T>(IList<T> list, Random random)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    // ---------------------------------------------------------------- 走棋

    /// <summary>某个棋子的合法落点。</summary>
    public IReadOnlyList<(int X, int Y)> LegalMoves(int x, int y)
        => Phase == JunqiPhase.Playing ? JunqiRules.LegalMoves(_board, x, y) : Array.Empty<(int X, int Y)>();

    /// <summary>
    /// 走棋（含自动裁判）。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public string Move(JunqiSide side, int fromX, int fromY, int toX, int toY)
    {
        if (Phase != JunqiPhase.Playing) return "对局还没开始";
        if (side != CurrentSide) return "还没轮到你走";

        var piece = At(fromX, fromY);
        if (piece.IsEmpty) return "这一格没有棋子";
        if (piece.Side != side) return "这不是你的棋子";

        var legal = JunqiRules.LegalMoves(_board, fromX, fromY);
        if (!legal.Contains((toX, toY))) return "这一步走不了";

        var defender = At(toX, toY);
        var outcome = defender.IsEmpty
            ? JunqiRules.JudgeOutcome.AttackerWins
            : JunqiRules.Judge(piece.Rank, defender.Rank);

        // 记录（被吃的棋子对双方明牌）
        if (!defender.IsEmpty)
        {
            _knownRanks[JunqiBoard.IndexOf(toX, toY)] = defender.Rank;
        }

        _history.Add(new JunqiMoveRecord(fromX, fromY, toX, toY, piece, defender, outcome));

        var fromIndex = JunqiBoard.IndexOf(fromX, fromY);
        var toIndex = JunqiBoard.IndexOf(toX, toY);

        switch (outcome)
        {
            case JunqiRules.JudgeOutcome.AttackerWins:
                _board[fromIndex] = JunqiPiece.Empty;
                _board[toIndex] = piece;
                break;

            case JunqiRules.JudgeOutcome.BothDie:
                _board[fromIndex] = JunqiPiece.Empty;
                _board[toIndex] = JunqiPiece.Empty;
                break;

            case JunqiRules.JudgeOutcome.DefenderWins:
                _board[fromIndex] = JunqiPiece.Empty;     // 进攻方死
                break;

            case JunqiRules.JudgeOutcome.CaptureFlag:
                _board[fromIndex] = JunqiPiece.Empty;
                Finish(side, $"{SideName(side)}夺旗获胜！");
                return "";

            default:
                return "裁判出错";
        }

        CheckAfterMove(side);
        if (Phase == JunqiPhase.Playing) CurrentSide = side == JunqiSide.Red ? JunqiSide.Blue : JunqiSide.Red;
        return "";
    }

    /// <summary>认输。</summary>
    public void Resign(JunqiSide side)
    {
        if (Phase != JunqiPhase.Playing) return;
        Finish(side == JunqiSide.Red ? JunqiSide.Blue : JunqiSide.Red, $"{SideName(side)}认输");
    }

    /// <summary>走完一步后的胜负判定：没军旗 / 没得走。</summary>
    private void CheckAfterMove(JunqiSide mover)
    {
        var other = mover == JunqiSide.Red ? JunqiSide.Blue : JunqiSide.Red;

        if (!JunqiRules.HasFlag(_board, other))
        {
            Finish(mover, $"{SideName(other)}的军旗没了，{SideName(mover)}获胜！");
            return;
        }
        if (!JunqiRules.HasFlag(_board, mover))
        {
            Finish(other, $"{SideName(mover)}的军旗没了，{SideName(other)}获胜！");
            return;
        }

        if (JunqiRules.HasNoMove(_board, other))
        {
            Finish(mover, $"{SideName(other)}已经没有能走的棋子，{SideName(mover)}获胜！");
        }
    }

    private void Finish(JunqiSide winner, string text)
    {
        Phase = JunqiPhase.Finished;
        Winner = winner;
        ResultText = text;
    }

    /// <summary>某一方的中文名。</summary>
    public static string SideName(JunqiSide side) => side == JunqiSide.Red ? "红方" : "蓝方";

    // ---------------------------------------------------------------- 快照（联机用，按人过滤暗棋）

    /// <summary>
    /// 给某一方生成的可见棋盘：<b>自己的棋子全部可见，对方的只有「明棋」可见</b>。
    /// 联机时房主按座位各发一份（客户端看不到对手暗棋，和真人对战一致）。
    /// <para>对方的暗棋会返回「属于对方但等级未知」的棋子（<see cref="JunqiPiece.Revealed"/>=false），
    /// 界面据此画成背面朝上的棋子。</para>
    /// </summary>
    public IReadOnlyList<JunqiPiece> VisibleBoardFor(JunqiSide viewer)
    {
        var board = new JunqiPiece[_board.Length];
        for (var i = 0; i < _board.Length; i++)
        {
            var piece = _board[i];
            if (piece.IsEmpty)
            {
                board[i] = JunqiPiece.Empty;
                continue;
            }

            if (piece.Side == viewer)
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
}
