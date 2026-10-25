using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.BoardGames.Xiangqi;

/// <summary>一局棋的结果。</summary>
public enum XiangqiResult
{
    /// <summary>还在下。</summary>
    Playing = 0,

    /// <summary>红方获胜。</summary>
    RedWin,

    /// <summary>黑方获胜。</summary>
    BlackWin,

    /// <summary>和棋。</summary>
    Draw,
}

/// <summary>
/// 棋谱里的一步：着法 + 中文记谱 + 被吃的子。
/// 除了界面展示所需字段，还带了快照重建悔棋栈要用的少量附加信息（缺了也能降级重建）。
/// </summary>
public sealed class XiangqiMoveInfo
{
    /// <summary>着法（起点 / 终点）。</summary>
    public XiangqiMove Move { get; set; }

    /// <summary>走的棋子（显示汉字，例如「炮」）。</summary>
    public string Piece { get; set; } = "";

    /// <summary>被吃的棋子（显示汉字；空字符串 = 这步没吃子）。</summary>
    public string Captured { get; set; } = "";

    /// <summary>中文记谱，例如「炮二平五」。由走子前的局面算出。</summary>
    public string Notation { get; set; } = "";

    /// <summary>第几个半回合（从 1 起）。</summary>
    public int Number { get; set; }

    /// <summary>走子方："red" / "black"。</summary>
    public string Side { get; set; } = "red";

    /// <summary>走子棋子的 FEN 字母（快照重建用）。</summary>
    public string MovedFen { get; set; } = "";

    /// <summary>被吃棋子的 FEN 字母（快照重建用；没吃子为空）。</summary>
    public string CapturedFen { get; set; } = "";

    /// <summary>这步棋之前轮到哪方（快照重建悔棋用；老快照可能没有）。</summary>
    public string? TurnBefore { get; set; }

    /// <summary>这步棋之前「连续无吃子半回合数」。</summary>
    public int HalfMovesBefore { get; set; }

    /// <summary>这步棋之前「让先剩余步数」。</summary>
    public int OpeningFreeBefore { get; set; }

    /// <summary>走子方阵营（由 <see cref="Side"/> 解析；快照里没有就用红方兜底）。</summary>
    [JsonIgnore]
    public XiangqiSide SideValue => XiangqiSides.Parse(Side) ?? XiangqiSide.Red;

    /// <summary>每步一行的展示文本，例如「1. 炮二平五」。</summary>
    [JsonIgnore]
    public string FullText => $"{Number}. {Notation}";

    /// <summary>复制一份（快照用，避免界面 / 网络侧改到棋局自己的记录）。</summary>
    public XiangqiMoveInfo Clone() => new()
    {
        Move = Move,
        Piece = Piece,
        Captured = Captured,
        Notation = Notation,
        Number = Number,
        Side = Side,
        MovedFen = MovedFen,
        CapturedFen = CapturedFen,
        TurnBefore = TurnBefore,
        HalfMovesBefore = HalfMovesBefore,
        OpeningFreeBefore = OpeningFreeBefore,
    };
}

/// <summary>
/// 一局中国象棋：棋盘 + 轮次 + 棋谱 + 悔棋栈 + 胜负判定 + 让先。
///
/// <para><b>规则要点</b>：红先；不能送将（含将帅照面）；被将军必须应将；
/// 将死判负、困毙（无棋可走）同样判负；连续 <b>120 个半回合</b>无吃子判和（自然限着）。</para>
///
/// <para><b>让先</b>：<see cref="OpeningSide"/> 一方在开局可以连走
/// <see cref="OpeningFreeMovesRemaining"/> 步，走完才恢复正常轮换（0 = 不让先）。</para>
///
/// <para>这个类<b>不碰界面、不碰网络</b>；<see cref="Started"/> 只是给房间界面用的「是否已经开始对局」标记，
/// 不影响 <see cref="Play"/> 的规则校验（自检可以直接摆局面直接下）。</para>
/// </summary>
public sealed class XiangqiGame
{
    /// <summary>自然限着：连续这么多半回合（双方各 60 步）没吃子就判和。</summary>
    public const int NaturalLimitHalfMoves = 120;

    /// <summary>悔棋用的一条内部记录：这一步走之前长什么样。</summary>
    private sealed class MoveRecord
    {
        public XiangqiPiece Moved;
        public XiangqiPiece Captured;
        public XiangqiSide TurnBefore;
        public int HalfMovesBefore;
        public int OpeningFreeBefore;
        public XiangqiResult ResultBefore;
        public string ResultReasonBefore = "";
        public XiangqiMove? LastMoveBefore;
    }

    private readonly List<XiangqiMoveInfo> _history = new(128);
    private readonly List<MoveRecord> _records = new(128);
    private XiangqiBoard _board;
    private XiangqiSide _turn;
    private XiangqiResult _result;
    private string _resultReason = "";
    private int _halfMovesSinceCapture;
    private int _openingFreeMovesRemaining;
    private XiangqiSide? _openingSide;
    private XiangqiMove? _lastMove;

    /// <summary>
    /// 造一局棋。<paramref name="board"/> 为空 = 标准开局；传了棋盘会 <b>深拷贝</b> 一份（不会共享）。
    /// <paramref name="openingFreeMoves"/> &gt; 0 时由 <paramref name="openingSide"/> 先连走这么多步。
    /// </summary>
    public XiangqiGame(XiangqiBoard? board = null, XiangqiSide turn = XiangqiSide.Red,
        int openingFreeMoves = 0, XiangqiSide? openingSide = null)
    {
        _board = board?.Clone() ?? XiangqiBoard.CreateInitial();
        _turn = turn;
        _result = XiangqiResult.Playing;
        Reset(board ?? XiangqiBoard.CreateInitial(), turn, openingFreeMoves, openingSide);
    }

    // ---------- 状态 ----------

    /// <summary>棋盘（棋局自己持有一份，外部只读使用；摆棋先摆草稿再 <see cref="Reset"/>）。</summary>
    public XiangqiBoard Board => _board;

    /// <summary>现在轮到哪一方走。</summary>
    public XiangqiSide Turn => _turn;

    /// <summary>结果。</summary>
    public XiangqiResult Result => _result;

    /// <summary>是否已结束。</summary>
    public bool IsOver => _result != XiangqiResult.Playing;

    /// <summary>结果文案：对局进行中 / 红方获胜 / 黑方获胜 / 和棋。</summary>
    public string ResultText => _result switch
    {
        XiangqiResult.RedWin => "红方获胜",
        XiangqiResult.BlackWin => "黑方获胜",
        XiangqiResult.Draw => "和棋",
        _ => "对局进行中",
    };

    /// <summary>结果原因（将死 / 困毙 / 自然限着判和 / 认输 / 求和……）；进行中为空字符串。</summary>
    public string ResultReason => _resultReason;

    /// <summary>当前行棋方是否正被将军（对局结束后仍然如实反映局面）。</summary>
    public bool IsInCheck => XiangqiRules.IsInCheck(_board, _turn);

    /// <summary>正被将军的一方；没人被将军返回 null（棋盘控件画红圈用）。</summary>
    public XiangqiSide? CheckedSide => IsInCheck ? _turn : null;

    /// <summary>最后一步（没有走过棋返回 null）。</summary>
    public XiangqiMove? LastMove => _lastMove;

    /// <summary>棋谱（按半回合顺序，从 1 起编号）。</summary>
    public IReadOnlyList<XiangqiMoveInfo> History => _history;

    /// <summary>连续无吃子的半回合数（到 <see cref="NaturalLimitHalfMoves"/> 判和）。</summary>
    public int HalfMovesSinceCapture => _halfMovesSinceCapture;

    /// <summary>让先还剩几步（被让方在开局还能连走几步）。</summary>
    public int OpeningFreeMovesRemaining => _openingFreeMovesRemaining;

    /// <summary>让先时被让的一方（没让先为 null）。</summary>
    public XiangqiSide? OpeningSide => _openingSide;

    /// <summary>界面用：是否已经开始对局（房间「开始对局」前为 false）。引擎不拿它做校验。</summary>
    public bool Started { get; set; }

    /// <summary>现在是不是「开局让先」阶段。</summary>
    public bool IsOpeningFreePhase => _openingSide is not null && _openingFreeMovesRemaining > 0;

    // ---------- 走子 ----------

    /// <summary>
    /// 当前行棋方某个棋子的全部合法着法（选中棋子时高亮可走点用）。
    /// 不是当前行棋方的子 / 空格 / 已结束都返回空表。
    /// </summary>
    public IReadOnlyList<XiangqiMove> LegalMovesFor(int x, int y)
    {
        if (IsOver) return Array.Empty<XiangqiMove>();
        var piece = _board.Get(x, y);
        if (piece.IsEmpty || piece.Side != _turn) return Array.Empty<XiangqiMove>();
        return XiangqiRules.GenerateLegalMoves(_board, _turn, x, y);
    }

    /// <summary>某方是否被将军（摆棋校验 / 界面提示用）。</summary>
    public bool IsInCheckFor(XiangqiSide side) => XiangqiRules.IsInCheck(_board, side);

    /// <summary>
    /// 走一步棋。<b>成功返回 null</b>，失败返回中文原因（不抛异常，界面直接显示）。
    /// </summary>
    public string? Play(XiangqiSide player, int fromX, int fromY, int toX, int toY)
    {
        if (IsOver) return "对局已经结束，不能再走棋";
        if (player != _turn) return $"现在轮到{XiangqiSides.DisplayName(_turn)}走棋";
        if (!XiangqiBoard.InBounds(fromX, fromY) || !XiangqiBoard.InBounds(toX, toY)) return "坐标超出了棋盘范围";

        var piece = _board.Get(fromX, fromY);
        if (piece.IsEmpty) return "起点没有棋子";
        if (piece.Side != player) return "该位置是对方的棋子";

        var move = new XiangqiMove(fromX, fromY, toX, toY);
        var pseudo = XiangqiRules.GenerateMoves(_board, fromX, fromY);
        var found = false;
        foreach (var candidate in pseudo)
        {
            if (candidate.Equals(move))
            {
                found = true;
                break;
            }
        }
        if (!found) return "这枚棋子不能这么走";

        if (piece.Type == XiangqiPieceType.King && _board.Get(toX, toY).Type == XiangqiPieceType.King)
        {
            return "将帅不能直接照面 / 吃对方的将帅";
        }

        if (XiangqiRules.WouldLeaveKingInCheck(_board, player, move))
        {
            return XiangqiRules.IsInCheck(_board, player)
                ? "正在被将军，这步棋不能解除将军（等于送将）"
                : "不能送将：走完后自己的将 / 帅会被将军（含将帅照面）";
        }

        ApplyMove(move, piece);
        return null;
    }

    /// <summary>真正把一步棋落到棋盘上（<see cref="Play"/> 校验通过后调用）。</summary>
    private void ApplyMove(XiangqiMove move, XiangqiPiece piece)
    {
        var captured = _board.Get(move.ToX, move.ToY);

        // 记谱必须在走子之前算（它要数「同一路上有几个同种子」）
        var notation = XiangqiNotation.Describe(_board, move);

        var record = new MoveRecord
        {
            Moved = piece,
            Captured = captured,
            TurnBefore = _turn,
            HalfMovesBefore = _halfMovesSinceCapture,
            OpeningFreeBefore = _openingFreeMovesRemaining,
            ResultBefore = _result,
            ResultReasonBefore = _resultReason,
            LastMoveBefore = _lastMove,
        };

        var info = new XiangqiMoveInfo
        {
            Move = move,
            Piece = piece.DisplayChar,
            Captured = captured.IsEmpty ? "" : captured.DisplayChar,
            Notation = notation,
            Number = _history.Count + 1,
            Side = piece.Side.ToKey(),
            MovedFen = piece.FenChar,
            CapturedFen = captured.IsEmpty ? "" : captured.FenChar,
            TurnBefore = _turn.ToKey(),
            HalfMovesBefore = _halfMovesSinceCapture,
            OpeningFreeBefore = _openingFreeMovesRemaining,
        };

        _board.Set(move.ToX, move.ToY, piece);
        _board.Set(move.FromX, move.FromY, XiangqiPiece.Empty);
        _halfMovesSinceCapture = captured.IsEmpty ? _halfMovesSinceCapture + 1 : 0;
        _lastMove = move;
        _history.Add(info);
        _records.Add(record);

        AdvanceTurn(piece.Side);
        EvaluateResult(piece.Side);
    }

    /// <summary>轮到下一方；让先阶段则被让方继续连走，走完才换边。</summary>
    private void AdvanceTurn(XiangqiSide mover)
    {
        if (_openingSide is not null && _openingFreeMovesRemaining > 0)
        {
            _openingFreeMovesRemaining--;
            if (_openingFreeMovesRemaining <= 0)
            {
                _openingFreeMovesRemaining = 0;
                _turn = mover.Opposite();
            }
            else
            {
                _turn = mover;
            }
            return;
        }
        _turn = mover.Opposite();
    }

    /// <summary>走完一步后判断胜负：对方无棋可走（将死 / 困毙）判负，自然限着判和。</summary>
    private void EvaluateResult(XiangqiSide mover)
    {
        var opponent = _turn;
        if (!XiangqiRules.HasAnyLegalMove(_board, opponent))
        {
            _result = mover == XiangqiSide.Red ? XiangqiResult.RedWin : XiangqiResult.BlackWin;
            _resultReason = XiangqiRules.IsInCheck(_board, opponent) ? "将死" : "困毙（无棋可走，判负）";
            return;
        }
        if (_halfMovesSinceCapture >= NaturalLimitHalfMoves)
        {
            _result = XiangqiResult.Draw;
            _resultReason = "自然限着判和（连续 120 个半回合无吃子）";
        }
    }

    // ---------- 认输 / 和棋（房间流程用） ----------

    /// <summary>某方认输：对方获胜。</summary>
    public void MarkResigned(XiangqiSide side)
    {
        if (IsOver) return;
        _result = side == XiangqiSide.Red ? XiangqiResult.BlackWin : XiangqiResult.RedWin;
        _resultReason = $"{XiangqiSides.DisplayName(side)}认输";
    }

    /// <summary>判和（双方同意 / 自然限着）。</summary>
    public void MarkDraw(string reason = "双方同意和棋")
    {
        if (IsOver) return;
        _result = XiangqiResult.Draw;
        _resultReason = reason;
    }

    /// <summary>某方超时（房间流程用，可选）。</summary>
    public void MarkTimeout(XiangqiSide side)
    {
        if (IsOver) return;
        _result = side == XiangqiSide.Red ? XiangqiResult.BlackWin : XiangqiResult.RedWin;
        _resultReason = $"{XiangqiSides.DisplayName(side)}超时";
    }

    // ---------- 悔棋 ----------

    /// <summary>撤掉最后一步（棋谱、轮次、无吃子计数、让先步数、结果一起还原）。</summary>
    public bool Undo()
    {
        if (_records.Count == 0) return false;
        var info = _history[^1];
        var record = _records[^1];

        _board.Set(info.Move.FromX, info.Move.FromY, record.Moved);
        _board.Set(info.Move.ToX, info.Move.ToY, record.Captured);
        _turn = record.TurnBefore;
        _halfMovesSinceCapture = record.HalfMovesBefore;
        _openingFreeMovesRemaining = record.OpeningFreeBefore;
        _result = record.ResultBefore;
        _resultReason = record.ResultReasonBefore;
        _lastMove = record.LastMoveBefore;
        _history.RemoveAt(_history.Count - 1);
        _records.RemoveAt(_records.Count - 1);
        return true;
    }

    /// <summary>
    /// 一直撤到「轮到自己走」为止（联机悔棋语义：最后一步是对方 → 撤 2 步；最后一步是自己 → 撤 1 步；
    /// 一步都没有 / 撤到了也不到自己 → false）。
    /// </summary>
    public bool UndoToTurn(XiangqiSide side)
    {
        var targetIndex = -1;
        for (var i = _records.Count - 1; i >= 0; i--)
        {
            if (_records[i].TurnBefore == side)
            {
                targetIndex = i;
                break;
            }
        }
        if (targetIndex < 0) return false;
        while (_records.Count > targetIndex) Undo();
        return true;
    }

    // ---------- 重开 / 快照 ----------

    /// <summary>重开一局：换棋盘（深拷贝）、轮次、让先。<see cref="Started"/> 保持不变，由调用方决定。</summary>
    public void Reset(XiangqiBoard board, XiangqiSide turn, int openingFreeMoves = 0, XiangqiSide? openingSide = null)
    {
        _board = board?.Clone() ?? XiangqiBoard.CreateInitial();
        _history.Clear();
        _records.Clear();
        _halfMovesSinceCapture = 0;
        _lastMove = null;
        _result = XiangqiResult.Playing;
        _resultReason = "";
        _openingSide = openingSide;
        _openingFreeMovesRemaining = Math.Max(0, openingFreeMoves);
        _turn = _openingSide is not null && _openingFreeMovesRemaining > 0 ? _openingSide.Value : turn;
    }

    /// <summary>把当前局面打包成快照（可 JSON 化后发给客户端）。</summary>
    public XiangqiSnapshot ToSnapshot()
    {
        var history = new List<XiangqiMoveInfo>(_history.Count);
        foreach (var info in _history) history.Add(info.Clone());
        return new XiangqiSnapshot
        {
            Fen = _board.ToFen(_turn, _halfMovesSinceCapture),
            Started = Started,
            Result = ResultToKey(_result),
            ResultReason = _resultReason,
            HalfMovesSinceCapture = _halfMovesSinceCapture,
            OpeningFreeMovesRemaining = _openingFreeMovesRemaining,
            OpeningSide = _openingSide?.ToKey(),
            History = history,
        };
    }

    /// <summary>从快照恢复棋局（含悔棋栈）。失败返回 null + 中文原因。</summary>
    public static (XiangqiGame? Game, string Error) FromSnapshot(XiangqiSnapshot? snapshot)
    {
        if (snapshot is null) return (null, "快照是空的");

        var (board, turn, fenHalfMoves, fenError) = XiangqiBoard.ParseFen(snapshot.Fen);
        if (!string.IsNullOrEmpty(fenError)) return (null, $"快照里的局面不合法：{fenError}");

        var game = new XiangqiGame
        {
            Started = snapshot.Started,
            _board = board,
            _turn = turn,
            _result = ParseResult(snapshot.Result),
            _resultReason = snapshot.ResultReason ?? "",
            _halfMovesSinceCapture = Math.Max(0, snapshot.HalfMovesSinceCapture > 0 ? snapshot.HalfMovesSinceCapture : fenHalfMoves),
            _openingFreeMovesRemaining = Math.Max(0, snapshot.OpeningFreeMovesRemaining),
            _openingSide = XiangqiSides.Parse(snapshot.OpeningSide),
        };

        var history = snapshot.History ?? new List<XiangqiMoveInfo>();
        game._history.Clear();
        game._records.Clear();

        // 1) 每步的走子 / 被吃棋子（快照里有 Fen 就用 Fen，没有就按显示汉字兜底）
        var moved = new XiangqiPiece[history.Count];
        var captured = new XiangqiPiece[history.Count];
        for (var i = 0; i < history.Count; i++)
        {
            var info = history[i] ?? throw new ArgumentException($"棋谱第 {i + 1} 步是空的");
            moved[i] = ParseMovePiece(info.MovedFen, info.Piece);
            captured[i] = ParseMovePiece(info.CapturedFen, info.Captured);
            if (moved[i].IsEmpty) return (null, $"棋谱第 {i + 1} 步缺少走子棋子信息，快照无法恢复");
        }

        // 2) 让先的「总共让了几步」：让先还剩余额时可以直接算；已经走完就找第一个由被让方对面走的着法
        var openingSide = game._openingSide;
        var openingTotal = 0;
        if (openingSide is not null)
        {
            if (game._openingFreeMovesRemaining > 0)
            {
                openingTotal = history.Count + game._openingFreeMovesRemaining;
            }
            else
            {
                for (var i = 0; i < history.Count; i++)
                {
                    if (XiangqiSides.Parse(history[i].Side) is { } side && side != openingSide.Value)
                    {
                        openingTotal = i;
                        break;
                    }
                }
            }
        }

        // 3) 无吃子计数：从当前值倒推每步之前的值
        var halfBefore = new int[history.Count];
        var runningHalf = game._halfMovesSinceCapture;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            halfBefore[i] = captured[i].IsEmpty ? Math.Max(0, runningHalf - 1) : 0;
            runningHalf = halfBefore[i];
        }

        // 4) 逐步重建悔棋栈
        XiangqiMove? lastMoveBefore = null;
        for (var i = 0; i < history.Count; i++)
        {
            var info = history[i];
            var turnBefore = ResolveTurnBefore(info, openingSide, openingTotal, i);
            game._history.Add(info);
            game._records.Add(new MoveRecord
            {
                Moved = moved[i],
                Captured = captured[i],
                TurnBefore = turnBefore,
                HalfMovesBefore = halfBefore[i],
                OpeningFreeBefore = openingSide is null ? 0 : Math.Max(0, openingTotal - i),
                ResultBefore = XiangqiResult.Playing,
                ResultReasonBefore = "",
                LastMoveBefore = lastMoveBefore,
            });
            lastMoveBefore = info.Move;
        }

        game._lastMove = history.Count > 0 ? history[^1].Move : null;
        return (game, "");
    }

    /// <summary>解一步之前轮到谁走：快照带了就直接用，否则按让先规则与轮换推。</summary>
    private static XiangqiSide ResolveTurnBefore(XiangqiMoveInfo info, XiangqiSide? openingSide, int openingTotal, int index)
    {
        if (XiangqiSides.Parse(info.TurnBefore) is { } stored) return stored;
        if (openingSide is not null)
        {
            if (index < openingTotal) return openingSide.Value;
            // 让先结束后从「被让方的对面」开始轮换（index 0 起的步号）
            var stepsAfterOpening = index - openingTotal;
            var first = openingSide.Value.Opposite();
            return stepsAfterOpening % 2 == 0 ? first : first.Opposite();
        }
        return info.SideValue.Opposite();
    }

    /// <summary>按 Fen 字母 / 显示汉字还原一步棋里的棋子；都没有则返回空格。</summary>
    private static XiangqiPiece ParseMovePiece(string? fenChar, string? displayChar)
    {
        if (!string.IsNullOrWhiteSpace(fenChar)) return XiangqiPiece.FromFenChar(fenChar.Trim()[0]);
        if (string.IsNullOrWhiteSpace(displayChar)) return XiangqiPiece.Empty;
        return XiangqiPiece.FromDisplayChar(displayChar.Trim()[0]);
    }

    /// <summary>结果 → 快照里的字符串（playing / redWin / blackWin / draw）。</summary>
    public static string ResultToKey(XiangqiResult result) => result switch
    {
        XiangqiResult.RedWin => "redWin",
        XiangqiResult.BlackWin => "blackWin",
        XiangqiResult.Draw => "draw",
        _ => "playing",
    };

    /// <summary>快照字符串 → 结果（认不出按进行中）。</summary>
    public static XiangqiResult ParseResult(string? key) => key?.Trim().ToLowerInvariant() switch
    {
        "redwin" or "red" or "红胜" => XiangqiResult.RedWin,
        "blackwin" or "black" or "黑胜" => XiangqiResult.BlackWin,
        "draw" or "和棋" or "和" => XiangqiResult.Draw,
        _ => XiangqiResult.Playing,
    };
}
