namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>一枚棋子的位置。</summary>
public enum LudoPieceState
{
    /// <summary>还在大本营（没起飞）。</summary>
    Home = 0,

    /// <summary>在跑道上。</summary>
    Track = 1,

    /// <summary>已经到终点（完成）。</summary>
    Finished = 2,
}

/// <summary>一枚棋子。</summary>
public sealed class LudoPiece
{
    /// <summary>所属颜色。</summary>
    public LudoColor Color { get; set; }

    /// <summary>这是这一方第几个棋子（0~3）。</summary>
    public int Index { get; set; }

    /// <summary>状态。</summary>
    public LudoPieceState State { get; set; } = LudoPieceState.Home;

    /// <summary>
    /// 在外圈上的格数（0 = 刚起飞那一格；52 = 走完一圈）。
    /// <b>只对 <see cref="LudoPieceState.Track"/> 有意义</b>。
    /// </summary>
    public int TrackSteps { get; set; }

    /// <summary>进入回家通道后的步数（1~6，6 = 终点）。</summary>
    public int HomeSteps { get; set; }

    /// <summary>是不是在自己的回家通道里。</summary>
    public bool IsOnHomeStretch => State == LudoPieceState.Track && TrackSteps >= LudoBoard.TrackLength;

    /// <summary>复制一份。</summary>
    public LudoPiece Clone() => new()
    {
        Color = Color,
        Index = Index,
        State = State,
        TrackSteps = TrackSteps,
        HomeSteps = HomeSteps,
    };

    /// <summary>这一枚现在在棋盘上的坐标（回家通道 / 跑道 / 大本营）。</summary>
    public (int X, int Y) Cell()
    {
        if (State == LudoPieceState.Home)
            return LudoColors.HomeCells(Color)[Math.Clamp(Index, 0, 3)];

        if (State == LudoPieceState.Finished) return LudoBoard.Finish;

        if (IsOnHomeStretch)
        {
            var cells = LudoBoard.HomeStretchCells(Color);
            var index = Math.Clamp(TrackSteps - LudoBoard.TrackLength, 0, cells.Count - 1);
            return cells[index];
        }

        // 跑道：自己的起飞点 + 走的格数（绕圈取模）
        var start = LudoBoard.StartIndex(Color);
        var position = (start + TrackSteps) % LudoBoard.TrackLength;
        return LudoBoard.Track[position];
    }
}

/// <summary>一次掷骰 / 走棋的结果（界面提示 / 联机同步用）。</summary>
public sealed record LudoMoveResult(
    bool Success,
    string Text,
    int Dice,
    LudoColor Color,
    int PieceIndex,
    IReadOnlyList<LudoPiece> Captured);

/// <summary>
/// 飞行棋对局（<b>纯逻辑</b>，自检可直接跑）。
///
/// <para><b>规则</b>（按需求实现，全部可在 <see cref="Rules"/> 里开关）：</para>
/// <list type="bullet">
///   <item>掷 6 才能起飞（可另开「掷 5 也能起飞」的后门规则）；</item>
///   <item>掷 6 可以再掷一次；</item>
///   <item><b>临近终点必须掷到刚好点数</b>：掷多了往回退；</item>
///   <item>踩到别人棋子 → 对方棋子直接回大本营；</item>
///   <item><b>经过中间横飞行线</b>时，线上有对方棋子也一起打回家。</item>
/// </list>
/// </summary>
public sealed class LudoGame
{
    private readonly List<LudoPiece> _pieces = new();
    private readonly Random _random;
    private readonly List<LudoColor> _order = new();
    private readonly List<LudoColor> _finished = new();
    private LudoColor _turn = LudoColor.Red;
    private int _extraRolls;
    private string _lastText = "";

    /// <summary>创建一局。</summary>
    /// <param name="rules">规则。</param>
    /// <param name="random">随机源（固定种子可复现，自检用）。</param>
    public LudoGame(LudoRules? rules = null, Random? random = null)
    {
        Rules = rules ?? new LudoRules();
        _random = random ?? new Random();
    }

    /// <summary>规则。</summary>
    public LudoRules Rules { get; }

    /// <summary>全部棋子。</summary>
    public IReadOnlyList<LudoPiece> Pieces => _pieces;

    /// <summary>参与的颜色（按行动顺序）。</summary>
    public IReadOnlyList<LudoColor> Order => _order;

    /// <summary>现在轮到谁。</summary>
    public LudoColor Turn => _turn;

    /// <summary>最后一次掷骰点数（0 = 还没掷）。</summary>
    public int LastDice { get; private set; }

    /// <summary>最后一次结果文字。</summary>
    public string LastText => _lastText;

    /// <summary>完成的先后顺序。</summary>
    public IReadOnlyList<LudoColor> Finished => _finished;

    /// <summary>对局是否结束（只剩一个颜色没完成 / 所有颜色完成）。</summary>
    public bool IsOver { get; private set; }

    /// <summary>结果文字。</summary>
    public string ResultText { get; private set; } = "";

    /// <summary>剩余可掷次数（掷 6 奖励）。</summary>
    public int ExtraRolls => _extraRolls;

    // ---------------------------------------------------------------- 开局

    /// <summary>
    /// 设置参与的颜色并开局（2~4 个颜色，按传入顺序行动）。
    /// </summary>
    public string Setup(IReadOnlyList<LudoColor> colors)
    {
        if (colors is null || colors.Count < 2) return "至少需要两个颜色";
        if (colors.Count > 4) return "最多四个颜色";
        if (colors.Distinct().Count() != colors.Count) return "颜色不能重复";

        _pieces.Clear();
        _order.Clear();
        _finished.Clear();
        _order.AddRange(colors);

        foreach (var color in colors)
        {
            for (var i = 0; i < LudoBoard.PiecesPerColor; i++)
            {
                _pieces.Add(new LudoPiece { Color = color, Index = i, State = LudoPieceState.Home });
            }
        }

        _turn = colors[0];
        LastDice = 0;
        _extraRolls = 0;
        _lastText = "";
        IsOver = false;
        ResultText = "";
        return "";
    }

    /// <summary>某个颜色的棋子。</summary>
    public IReadOnlyList<LudoPiece> PiecesOf(LudoColor color)
        => _pieces.Where(p => p.Color == color).ToList();

    /// <summary>某个颜色完成了几枚。</summary>
    public int FinishedCount(LudoColor color)
        => _pieces.Count(p => p.Color == color && p.State == LudoPieceState.Finished);

    /// <summary>这个颜色是不是已经全部完成。</summary>
    public bool IsColorDone(LudoColor color) => FinishedCount(color) >= LudoBoard.PiecesPerColor;

    /// <summary>这个颜色还能不能掷骰子（轮到它且没完成）。</summary>
    public bool CanRoll(LudoColor color)
    {
        if (IsOver) return false;
        SkipFinishedTurns();          // 轮到的人如果已经完成，先跳过去
        return color == _turn && !IsColorDone(color);
    }

    // ---------------------------------------------------------------- 掷骰

    /// <summary>掷骰子（房主权威；界面 / 联机都走这里）。</summary>
    /// <returns>点数（0 = 不该它掷）。</returns>
    public int Roll(LudoColor color)
    {
        if (!CanRoll(color)) return 0;

        var dice = _random.Next(1, 7);
        LastDice = dice;

        // 掷 6 奖励一次
        if (dice == 6 && Rules.ExtraRollOnSix) _extraRolls++;

        _lastText = $"{LudoColors.DisplayName(color)}掷出 {dice}";
        return dice;
    }

    /// <summary>
    /// 把回合推进到下一个「还没完成」的颜色。
    /// <para>⚠ 如果轮到的一方已经全部完成（比如刚被判定完成），必须靠它跳过，
    /// 否则 <see cref="CanRoll"/> 永远返回 false、整个对局卡住（自检抓到过）。</para>
    /// </summary>
    public void SkipFinishedTurns()
    {
        if (_order.Count == 0 || IsOver) return;
        if (!IsColorDone(_turn)) return;
        AdvanceTurn();
    }

    /// <summary>按点数找一个能走的棋子（自动选择，供 AI / 一键走棋用）。</summary>
    public LudoPiece? PickPiece(LudoColor color, int dice)
    {
        var candidates = LegalMovable(colr: color, dice);
        if (candidates.Count == 0) return null;

        // 优先：能踢人的 > 能到终点的 > 离终点近的
        return candidates
            .OrderByDescending(p => CaptureTargets(p, dice).Count)
            .ThenByDescending(p => p.TrackSteps + (p.IsOnHomeStretch ? 0 : 0))
            .First();
    }

    /// <summary>这个点数下，哪些棋子能动。</summary>
    public List<LudoPiece> LegalMovable(LudoColor colr, int dice)
    {
        var result = new List<LudoPiece>();
        if (dice <= 0) return result;

        foreach (var piece in PiecesOf(colr))
        {
            if (piece.State == LudoPieceState.Finished) continue;

            if (piece.State == LudoPieceState.Home)
            {
                // 起飞：需要掷到 6（或房主开了「掷 5 也能起飞」）
                if (CanTakeOff(dice)) result.Add(piece);
                continue;
            }

            // 在跑道上：能不能走（精确到终点时，超出的点数要回退，永远能走）
            result.Add(piece);
        }

        return result;
    }

    /// <summary>这个点数能不能起飞。</summary>
    public bool CanTakeOff(int dice)
    {
        if (!Rules.NeedSixToTakeOff) return dice is 1 or 6;
        if (dice == 6) return true;
        return Rules.FiveAlsoTakesOff && dice == 5;
    }

    // ---------------------------------------------------------------- 走棋

    /// <summary>
    /// 走一枚棋子。
    /// </summary>
    /// <param name="color">哪一方（必须轮到它）。</param>
    /// <param name="pieceIndex">第几枚棋子。</param>
    /// <param name="dice">点数。</param>
    public LudoMoveResult Move(LudoColor color, int pieceIndex, int dice)
    {
        if (IsOver) return Fail("对局已经结束", dice, color, pieceIndex);
        if (color != _turn) return Fail("还没轮到你", dice, color, pieceIndex);
        if (dice <= 0) return Fail("先掷骰子", dice, color, pieceIndex);

        var piece = _pieces.FirstOrDefault(p => p.Color == color && p.Index == pieceIndex);
        if (piece is null) return Fail("找不到这枚棋子", dice, color, pieceIndex);

        var captured = new List<LudoPiece>();

        // ① 起飞
        if (piece.State == LudoPieceState.Home)
        {
            if (!CanTakeOff(dice)) return Fail("掷到 6 才能起飞（房主可另开 5 点起飞）", dice, color, pieceIndex);

            piece.State = LudoPieceState.Track;
            piece.TrackSteps = 0;
            piece.HomeSteps = 0;

            // 起飞点上有别人 → 踢回家
            if (Rules.CaptureOnLand) captured.AddRange(CaptureAt(piece.Cell(), color));

            _lastText = $"{LudoColors.DisplayName(color)} 起飞一枚棋子";
            return FinishMove(color, dice, pieceIndex, captured, _lastText);
        }

        // ② 在跑道上走
        if (piece.State == LudoPieceState.Track)
        {
            var total = LudoBoard.TrackLength + LudoBoard.HomeStretch;   // 走完一圈 + 回家通道
            var current = piece.TrackSteps;
            var target = current + dice;

            // 已经过了终点线（在回家通道里）或超出
            if (target > total)
            {
                if (!Rules.ExactToFinish)
                {
                    target = total;     // 不要求精确：直接到终点
                }
                else
                {
                    // 精确到终点：掷多了往回退
                    var overshoot = target - total;
                    target = total - overshoot;
                    if (target < 0) target = 0;
                    _lastText = $"点数太大，往回退 {overshoot} 步";
                }
            }
            else
            {
                _lastText = $"{LudoColors.DisplayName(color)} 走 {dice} 步";
            }

            // 记录走过的路（判断飞行线）
            var flightHits = new List<LudoPiece>();
            if (Rules.CaptureOnFlightLine)
            {
                for (var step = current + 1; step <= Math.Min(target, LudoBoard.TrackLength); step++)
                {
                    var cell = CellOfTrackStep(color, step);
                    if (LudoBoard.IsFlightLine(cell.X, cell.Y))
                    {
                        flightHits.AddRange(CaptureAt(cell, color));
                    }
                }
            }

            piece.TrackSteps = target;

            if (target >= total)
            {
                piece.State = LudoPieceState.Finished;
                piece.HomeSteps = LudoBoard.HomeStretch;
            }

            // 落点踢子
            if (Rules.CaptureOnLand && piece.State == LudoPieceState.Track)
            {
                captured.AddRange(CaptureAt(piece.Cell(), color));
            }

            // 飞行线踢子（去重）
            foreach (var hit in flightHits)
            {
                if (!captured.Contains(hit)) captured.Add(hit);
            }

            return FinishMove(color, dice, pieceIndex, captured, _lastText);
        }

        return Fail("这枚棋子已经在终点", dice, color, pieceIndex);
    }

    /// <summary>走完之后的收尾：换人 / 检查胜负。</summary>
    private LudoMoveResult FinishMove(
        LudoColor color, int dice, int pieceIndex, List<LudoPiece> captured, string text)
    {
        var capturedText = captured.Count > 0
            ? $"，把 {captured.Count} 枚对方棋子打回大本营"
            : "";

        // 掷 6（或开了奖励且还有次数）→ 再掷一次
        var again = dice == 6 && Rules.ExtraRollOnSix;
        if (!again && _extraRolls > 0) _extraRolls = 0;      // 用掉奖励

        if (_extraRolls > 0)
        {
            // 留在当前玩家
        }
        else
        {
            AdvanceTurn();
        }

        // 完成检查
        if (IsColorDone(color) && !_finished.Contains(color)) _finished.Add(color);
        CheckGameOver();

        var finalText = text + capturedText;
        _lastText = finalText;
        return new LudoMoveResult(true, finalText, dice, color, pieceIndex, captured);
    }

    /// <summary>换下一个颜色（跳过已完成的）。</summary>
    private void AdvanceTurn()
    {
        if (_order.Count == 0) return;
        var index = _order.IndexOf(_turn);
        for (var i = 1; i <= _order.Count; i++)
        {
            var next = _order[(index + i) % _order.Count];
            if (IsColorDone(next)) continue;
            _turn = next;
            LastDice = 0;
            return;
        }
    }

    /// <summary>把落在某个格子上的对方棋子打回大本营。</summary>
    private List<LudoPiece> CaptureAt((int X, int Y) cell, LudoColor myColor)
    {
        var captured = new List<LudoPiece>();

        // 自己的大本营 / 回家通道是安全区，不踢
        if (LudoColors.HomeCells(myColor).Contains(cell)) return captured;

        foreach (var other in _pieces)
        {
            if (other.Color == myColor) continue;
            if (other.State != LudoPieceState.Track) continue;
            if (other.IsOnHomeStretch) continue;          // 回家通道里安全
            if (other.Cell() != cell) continue;

            other.State = LudoPieceState.Home;
            other.TrackSteps = 0;
            other.HomeSteps = 0;
            captured.Add(other);
        }

        return captured;
    }

    /// <summary>某个颜色走 <paramref name="step"/> 步时对应的棋盘坐标。</summary>
    public static (int X, int Y) CellOfTrackStep(LudoColor color, int step)
    {
        if (step >= LudoBoard.TrackLength)
        {
            var cells = LudoBoard.HomeStretchCells(color);
            var index = Math.Clamp(step - LudoBoard.TrackLength, 0, cells.Count - 1);
            return cells[index];
        }

        var position = (LudoBoard.StartIndex(color) + step) % LudoBoard.TrackLength;
        return LudoBoard.Track[position];
    }

    /// <summary>某枚棋子按点数走之后会落在哪（预览 / AI 用）。</summary>
    public (int X, int Y) PreviewCell(LudoPiece piece, int dice)
    {
        if (piece.State == LudoPieceState.Home)
            return piece.Cell();

        var total = LudoBoard.TrackLength + LudoBoard.HomeStretch;
        var target = piece.TrackSteps + dice;

        if (target > total)
        {
            target = Rules.ExactToFinish ? Math.Max(0, total - (target - total)) : total;
        }

        if (target >= total) return LudoBoard.Finish;

        return CellOfTrackStep(piece.Color, target);
    }

    /// <summary>这枚棋子按点数走会踢掉哪些对方棋子（AI 用）。</summary>
    public List<LudoPiece> CaptureTargets(LudoPiece piece, int dice)
    {
        var result = new List<LudoPiece>();
        var cell = PreviewCell(piece, dice);

        foreach (var other in _pieces)
        {
            if (other.Color == piece.Color) continue;
            if (other.State != LudoPieceState.Track || other.IsOnHomeStretch) continue;
            if (other.Cell() == cell) result.Add(other);
        }
        return result;
    }

    private void CheckGameOver()
    {
        var remaining = _order.Where(c => !IsColorDone(c)).ToList();
        if (remaining.Count <= 1 && _order.Count >= 2)
        {
            IsOver = true;
            var winner = _finished.Count > 0 ? _finished[0] : _order[0];
            ResultText = $"{LudoColors.DisplayName(winner)} 获胜！";
            return;
        }

        if (remaining.Count == 0)
        {
            IsOver = true;
            ResultText = "所有颜色都完成了";
        }
    }

    private LudoMoveResult Fail(string text, int dice, LudoColor color, int pieceIndex)
    {
        _lastText = text;
        return new LudoMoveResult(false, text, dice, color, pieceIndex, Array.Empty<LudoPiece>());
    }
}
