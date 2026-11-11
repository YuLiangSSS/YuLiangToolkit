namespace YuLiangToolkit.Tools.Games.Tetris;

/// <summary>一个方块在棋盘上的摆放（左上角坐标 + 朝向）。</summary>
/// <param name="Kind">方块种类。</param>
/// <param name="X">包围盒左上角的列（可为负 / 越界，放置前由调用方校验）。</param>
/// <param name="Y">包围盒左上角的行（向下为正，可为负 = 还没完全进场）。</param>
/// <param name="Rotation">朝向 0 ~ 3（见 <see cref="TetrisPieces"/>）。</param>
public readonly record struct TetrisPiece(TetrominoKind Kind, int X, int Y, int Rotation)
{
    /// <summary>这个方块占用的四个**棋盘**格子（<c>X</c> 列 / <c>Y</c> 行，Y 向下为正）。</summary>
    public IReadOnlyList<(int X, int Y)> Cells()
    {
        var cells = TetrisPieces.CellsOf(Kind, Rotation);
        var result = new (int X, int Y)[cells.Count];
        for (var i = 0; i < cells.Count; i++) result[i] = (X + cells[i].X, Y + cells[i].Y);
        return result;
    }

    /// <summary>平移（dx 向右、dy 向下）。</summary>
    public TetrisPiece Moved(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>一次「落地结算」的结果（纯数据，界面据此显示提示）。</summary>
/// <param name="LinesCleared">这一手消掉的行数（0 ~ 4）。</param>
/// <param name="ScoreGained">这一手得到的分数。</param>
/// <param name="Level">结算后的等级。</param>
/// <param name="ComboText">结算文案（空 = 没消行）。</param>
public readonly record struct TetrisLockResult(int LinesCleared, int ScoreGained, int Level, string ComboText);

/// <summary>旋转尝试的结果：成功时 <see cref="Piece"/> 是旋转（并按需踢墙）之后的位置。</summary>
/// <param name="Success">旋转是否成立（false = 五个踢墙偏移都撞墙，按规则保持原样）。</param>
/// <param name="Piece">成功后的方块；失败时等于传入的原方块。</param>
/// <param name="KickIndex">用的是第几个踢墙偏移（0 = 原地转）。</param>
public readonly record struct TetrisRotateResult(bool Success, TetrisPiece Piece, int KickIndex);

/// <summary>
/// 俄罗斯方块的**纯逻辑引擎**：棋盘、7-bag 出块、碰撞、移动 / 旋转（SRS 墙踢）/ 硬降、
/// 锁定与消行、计分与等级。<b>完全不碰 WPF</b>，所以自检里可以直接无头跑几千手。
///
/// <para><b>坐标约定</b>：<c>Width = 10</c> 列、<c>Height = 20</c> 行，行下标 0 在最上面、19 在最下面；
/// 数组是一维的，下标 = <c>y * Width + x</c>，<c>null</c> = 空格，否则记着落在这一格的是哪种方块
/// （画的时候按种类取颜色）。</para>
///
/// <para><b>随机</b>：7-bag —— 每一轮把 7 种方块洗一遍依次给出，所以不会连着出同一个、也不会久等不来；
/// 出块序列可以在构造时注入，自检因此完全确定性。</para>
/// </summary>
public sealed class TetrisEngine
{
    /// <summary>棋盘列数。</summary>
    public const int Width = 10;

    /// <summary>棋盘行数（可见区域）。</summary>
    public const int Height = 20;

    /// <summary>预览区最多显示几块（界面按这个数画）。</summary>
    public const int PreviewCount = 3;

    /// <summary>预览块数（由设置决定，1~8）。</summary>
    public int PreviewSlots { get; set; } = PreviewCount;

    /// <summary>出生行的上边界（方块顶格在 y = 0，允许先占住第 0 行）。</summary>
    public const int SpawnY = 0;

    /// <summary>消行得分表：下标 = 一次消掉的行数（1 ~ 4）。</summary>
    private static readonly int[] LineScores = { 0, 100, 300, 500, 800 };

    /// <summary>等级上限（再快也按这个来，避免锁到 1 格 / 帧那种不可玩的速度）。</summary>
    public const int MaxLevel = 20;

    private readonly Queue<TetrominoKind> _preview = new();
    private readonly List<TetrominoKind> _bag = new();
    private readonly TetrominoKind?[] _board = new TetrominoKind?[Width * Height];

    private Random _random = new(1);

    /// <summary>起始等级（消行升级时从它往上加）。</summary>
    private int _startLevel = 1;

    /// <summary>构造：<paramref name="random"/> 不传就新建；传固定种子 / 注入序列可以让出块完全确定性。</summary>
    public TetrisEngine(Random? random = null)
    {
        _random = random ?? new Random();
        Reset();
    }

    // ---------------------------------------------------------------- 状态

    /// <summary>当前正在下落的方块（游戏没开始时为 null）。</summary>
    public TetrisPiece? Current { get; private set; }

    /// <summary>
    /// 暂存格（<b>可以有多个</b>，按插入顺序排列；按 C 时轮流取）。
    /// <para>第一次 Hold 把当前块放进第 1 格；第 2 格起靠「再次 Hold」依次填满，
    /// 之后每次按 C 都从最前面取一块换出来，被换进去的那块排到队尾。</para>
    /// </summary>
    private readonly List<TetrominoKind> _heldSlots = new();

    /// <summary>暂存格数量上限（由设置决定，1~5）。</summary>
    public int HoldSlots { get; set; } = 1;

    /// <summary>暂存格里的方块（按顺序，只读快照）。</summary>
    public IReadOnlyList<TetrominoKind> HeldSlots => _heldSlots;

    /// <summary>第一格（兼容旧接口 / 自检）。</summary>
    public TetrominoKind? Held => _heldSlots.Count > 0 ? _heldSlots[0] : null;

    /// <summary>这一手之前是否已经 Hold 过（落地后才会恢复成可以再 Hold）。</summary>
    public bool HasHeldThisTurn { get; private set; }

    /// <summary>当前分数。</summary>
    public int Score { get; private set; }

    /// <summary>已消行数。</summary>
    public int Lines { get; private set; }

    /// <summary>当前等级（1 起，按消行数提升，封顶 <see cref="MaxLevel"/>）。</summary>
    public int Level { get; private set; }

    /// <summary>这一手（同一次落地）已经消掉多少行（界面用来放「+分数」飘字）。</summary>
    public int LastClearedLines { get; private set; }

    /// <summary>
    /// 还欠着、没插进棋盘的垃圾行数（联机互攻模式）：<b>下一次锁定结算时</b>一次性插到底部。
    /// 单人玩法恒为 0。
    /// </summary>
    public int PendingGarbage { get; private set; }

    /// <summary>这一局送出去多少垃圾行（对手欠下的）。</summary>
    public int GarbageSent { get; private set; }

    /// <summary>这一局收到并已经插进棋盘的垃圾行数。</summary>
    public int GarbageTaken { get; private set; }

    /// <summary>是否已经结束（方块出生位置被占住）。</summary>
    public bool IsGameOver { get; private set; }

    /// <summary>能不能换暂存（这一手还没换过、且没结束）。</summary>
    public bool CanHold => !IsGameOver && !HasHeldThisTurn && Current is not null;

    /// <summary>接下来 <see cref="PreviewCount"/> 块（只读快照，界面画预览用）。</summary>
    public IReadOnlyList<TetrominoKind> NextPieces
    {
        get
        {
            EnsurePreview();
            return _preview.ToArray();
        }
    }

    /// <summary>取某一格的方块种类（越界返回 null）。</summary>
    public TetrominoKind? CellAt(int x, int y)
        => InBounds(x, y) ? _board[y * Width + x] : null;

    /// <summary>坐标是否在棋盘内。</summary>
    public static bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    /// <summary>
    /// 当前等级对应的一次下落间隔（毫秒）。等级 1 = 800ms，每级快 12%，封顶 <see cref="MaxLevel"/>。
    /// （纯函数，界面用 DispatcherTimer 按它改 Interval，自检直接断言。）
    /// </summary>
    public static int FallIntervalMs(int level)
    {
        var clamped = Math.Clamp(level, 1, MaxLevel);
        var interval = 800 * Math.Pow(0.88, clamped - 1);
        return (int)Math.Round(Math.Clamp(interval, 60, 800));
    }

    // ---------------------------------------------------------------- 开局 / 结束

    /// <summary>重新开始：清空棋盘、重置分数等级与暂存，并立刻出第一块。</summary>
    /// <param name="startLevel">起始等级（1 ~ <see cref="MaxLevel"/>）；等级按「起始等级 + 每 10 行升 1 级」走。</param>
    /// <param name="seed">
    /// 随机种子。<c>null</c> = 每次都不一样（单人玩法）；
    /// 联机「各自计分」模式两边传同一个种子，出块序列才完全一致，比分数才公平。
    /// </param>
    public void Reset(int startLevel = 1, int? seed = null)
    {
        Array.Clear(_board);
        _preview.Clear();
        _bag.Clear();
        Current = null;
        _heldSlots.Clear();
        HasHeldThisTurn = false;
        _startLevel = Math.Clamp(startLevel, 1, MaxLevel);
        Score = 0;
        Lines = 0;
        Level = _startLevel;
        LastClearedLines = 0;
        IsGameOver = false;
        PendingGarbage = 0;
        GarbageSent = 0;
        GarbageTaken = 0;

        if (seed is { } fixedSeed) Reseed(fixedSeed);

        EnsurePreview();
        SpawnNext();
    }

    /// <summary>换一个随机源（同一个种子 → 同一套 7-bag 顺序）。联机开新局时用。</summary>
    public void Reseed(int seed)
    {
        _random = new Random(seed);
        _preview.Clear();
        _bag.Clear();
    }

    // ---------------------------------------------------------------- 移动 / 旋转 / 落地

    /// <summary>左右移动：返回是否真的动了（撞墙 / 撞块返回 false，界面据此决定要不要播放提示）。</summary>
    public bool MoveLeft() => TryMove(-1, 0);

    /// <summary>向右移动。</summary>
    public bool MoveRight() => TryMove(1, 0);

    /// <summary>软降（玩家按下方向键）：能下去就下去并 +1 分。</summary>
    public bool SoftDrop()
    {
        if (IsGameOver) return false;
        if (!TryMove(0, 1)) return false;
        Score += 1;
        return true;
    }

    /// <summary>能不能再往下走一格（界面用来判断「要不要立刻锁定」）。</summary>
    public bool CanMoveDown() => Current is { } piece && Fits(piece.Moved(0, 1));

    /// <summary>
    /// 自然下落一格（定时器每 tick 调它）。返回 false = 已经落到底、调用方应当锁定
    /// （引擎不在这里锁：锁定要等界面播完那一下的动效，见 <see cref="Lock"/>）。
    /// </summary>
    public bool Step() => !IsGameOver && TryMove(0, 1);

    /// <summary>
    /// 顺时针 / 逆时针旋转（SRS 墙踢）：<paramref name="clockwise"/> 为 false 时逆时针。
    /// O 方块没有实际变化（只返回成功）。
    /// </summary>
    public TetrisRotateResult Rotate(bool clockwise)
    {
        if (IsGameOver || Current is not { } piece)
            return new TetrisRotateResult(false, new TetrisPiece(), 0);

        var from = TetrisPieces.NormalizeRotation(piece.Rotation);
        var to = TetrisPieces.NormalizeRotation(clockwise ? from + 1 : from - 1);

        // O 方块四个朝向完全一样：不试踢墙，直接算成功（避免出现「按了没反应但吃了一次输入」）
        if (piece.Kind == TetrominoKind.O)
        {
            var same = piece with { Rotation = to };
            Current = same;
            return new TetrisRotateResult(true, same, 0);
        }

        var kicks = TetrisPieces.KickOffsets(piece.Kind, from, to);
        for (var i = 0; i < kicks.Count; i++)
        {
            var candidate = new TetrisPiece(piece.Kind, piece.X + kicks[i].X, piece.Y + kicks[i].Y, to);
            if (!Fits(candidate)) continue;
            Current = candidate;
            return new TetrisRotateResult(true, candidate, i);
        }

        return new TetrisRotateResult(false, piece, -1);
    }

    /// <summary>硬降：一直落到底并立刻锁定。返回落地结算结果。</summary>
    public TetrisLockResult HardDrop()
    {
        if (IsGameOver || Current is not { } piece) return default;

        var dropped = 0;
        var candidate = piece;
        while (Fits(candidate.Moved(0, 1)))
        {
            candidate = candidate.Moved(0, 1);
            dropped++;
        }

        Current = candidate;
        Score += dropped * 2;   // 硬降的格子也算分，鼓励快攻
        return Lock();
    }

    /// <summary>
    /// 锁定当前方块：写进棋盘 → 消行 → 计分 / 升级 → 出下一块。
    /// 方块顶格还在棋盘上方之外（<c>Y &lt; 0</c>）的部分直接丢弃（标准做法）。
    /// 已经结束 / 没有当前方块时返回全 0。
    /// </summary>
    public TetrisLockResult Lock()
    {
        if (IsGameOver || Current is not { } piece) return default;

        foreach (var (x, y) in piece.Cells())
        {
            if (InBounds(x, y)) _board[y * Width + x] = piece.Kind;
        }

        var cleared = ClearLines();
        var gained = LineScores[Math.Clamp(cleared, 0, 4)] * Level;
        Score += gained;
        Lines += cleared;
        LastClearedLines = cleared;
        var newLevel = Math.Clamp(_startLevel + Lines / 10, 1, MaxLevel);
        Level = newLevel;

        // 联机互攻：这一手消得够多，就给对手送垃圾行（消 1 行不送）。
        // SetSinglePlayerGarbage(true) 时也让自己吃一份（自检 / 单人体验用），默认关着。
        if (cleared >= 2)
        {
            var rows = GarbageRowsFor(cleared);
            GarbageSent += rows;
            if (_allowSelfGarbage) EnqueueGarbage(rows);
        }

        // 欠着的垃圾行在「自己没消行」的落地时才插进来，否则会被自己这一手的消行立刻抵掉
        if (cleared == 0 && PendingGarbage > 0) ApplyGarbage(PendingGarbage);

        Current = null;
        HasHeldThisTurn = false;
        EnsurePreview();
        SpawnNext();

        var text = cleared switch
        {
            1 => "单行消除",
            2 => "双行消除！",
            3 => "三行消除！",
            4 => "四行消除！TETRIS",
            _ => "",
        };

        return new TetrisLockResult(cleared, gained, Level, text);
    }

    /// <summary>
    /// 暂存（Hold）：把当前方块存起来；手上没存过就换成预览里的下一块。
    /// 同一个方块落地之前只能换一次（<see cref="CanHold"/>）。
    /// </summary>
    public bool Hold()
    {
        if (!CanHold || Current is not { } piece) return false;

        var currentKind = piece.Kind;
        var slots = Math.Max(1, Math.Min(5, HoldSlots));

        // 还没填满：把当前块塞进下一个空位，手上换成预览的下一块
        if (_heldSlots.Count < slots)
        {
            _heldSlots.Add(currentKind);
            HasHeldThisTurn = true;
            Spawn(TakeFromPreview());
            return true;
        }

        // 已填满：拿第 1 格换出来，当前块排到队尾（轮流）
        var swapIn = _heldSlots[0];
        _heldSlots.RemoveAt(0);
        _heldSlots.Add(currentKind);

        HasHeldThisTurn = true;
        Spawn(swapIn);
        return true;
    }

    /// <summary>当前方块落到最底（幽灵方块）时的位置，界面用它画半透明预览。</summary>
    public TetrisPiece? GhostPiece()
    {
        if (Current is not { } piece) return null;
        var ghost = piece;
        while (Fits(ghost.Moved(0, 1))) ghost = ghost.Moved(0, 1);
        return ghost;
    }

    // ---------------------------------------------------------------- 联机：垃圾行 / 快照

    /// <summary>
    /// 一次消掉 <paramref name="cleared"/> 行要送给对手多少垃圾行。
    /// 现代俄罗斯方块的标准值：消 2 行送 1、消 3 行送 2、消 4 行送 4，消 1 行不送。
    /// </summary>
    public static int GarbageRowsFor(int cleared) => Math.Clamp(cleared, 0, 4) switch
    {
        2 => 1,
        3 => 2,
        4 => 4,
        _ => 0,
    };

    /// <summary>对手送来的垃圾行先记在这儿，下一次「自己没消行」的落地时插进棋盘。</summary>
    public void EnqueueGarbage(int rows)
    {
        if (rows <= 0 || IsGameOver) return;
        PendingGarbage = Math.Min(PendingGarbage + rows, Height - 1);
    }

    /// <summary>
    /// 联机互攻：把「该送给对手」的垃圾行记进 <see cref="OpponentGarbageToSend"/>，
    /// 由 ViewModel 在广播前取走转给对方。单人玩法不会调用它，行为完全不变。
    /// </summary>
    public void QueueGarbageForOpponent(int rows)
    {
        if (rows <= 0) return;
        OpponentGarbageToSend += rows;
        GarbageSent += rows;
    }

    /// <summary>还没转给对手的垃圾行（ViewModel 取走后清零）。</summary>
    public int OpponentGarbageToSend { get; private set; }

    /// <summary>取走待转给对手的垃圾行数并清零。</summary>
    public int TakeOpponentGarbage()
    {
        var rows = OpponentGarbageToSend;
        OpponentGarbageToSend = 0;
        return rows;
    }

    /// <summary>
    /// 单人玩法是否也启用垃圾行（默认关）。联机模式由 ViewModel 关掉这个开关，
    /// 因为垃圾行该由「对手那边」送过来，而不是自己送给自己。
    /// </summary>
    public void SetSinglePlayerGarbage(bool enabled) => _allowSelfGarbage = enabled;

    private bool _allowSelfGarbage;

    /// <summary>
    /// 把棋盘整体上移 <paramref name="rows"/> 行，底部补上垃圾行（每行留 1 格随机缺口）。
    /// 顶出去的行直接丢掉；新插入的行把方块往上顶，顶到出生位置就判负。
    /// </summary>
    private void ApplyGarbage(int rows)
    {
        var take = Math.Min(Math.Max(rows, 0), Height - 1);
        if (take == 0) return;

        PendingGarbage = 0;
        GarbageTaken += take;

        // 整体上移
        if (take < Height)
        {
            Array.Copy(_board, take * Width, _board, 0, (Height - take) * Width);
        }

        // 底部补垃圾行：缺口列随机；相邻两行缺口尽量错开（看起来才像真的垃圾行）
        var previousGap = -1;
        for (var y = Height - take; y < Height; y++)
        {
            var gap = _random.Next(Width);
            if (gap == previousGap) gap = (gap + 1 + _random.Next(Width - 1)) % Width;
            previousGap = gap;

            for (var x = 0; x < Width; x++)
            {
                _board[y * Width + x] = x == gap ? null : TetrominoKind.Garbage;
            }
        }

        // 方块被顶上去：原位放不下就往上挪，挪到顶都放不下 = 顶死
        if (Current is { } piece)
        {
            var moved = piece;
            var guard = 0;
            while (!Fits(moved) && guard++ < Height)
            {
                moved = moved.Moved(0, -1);
            }
            Current = Fits(moved) ? moved : piece;
            if (!Fits(moved)) IsGameOver = true;
        }
    }

    /// <summary>这一局的对局快照（联机时房主广播给客户端渲染用）。</summary>
    public TetrisEngineSnapshot Snapshot() => new()
    {
        Cells = (TetrominoKind?[])_board.Clone(),
        Current = Current,
        Held = Held,
        Score = Score,
        Lines = Lines,
        Level = Level,
        PendingGarbage = PendingGarbage,
        GarbageSent = GarbageSent,
        GarbageTaken = GarbageTaken,
        IsGameOver = IsGameOver,
        Next = _preview.ToArray(),
    };

    /// <summary>
    /// 用快照覆盖本端状态（客户端渲染房主权威的棋盘）。
    /// <b>不改动随机源</b>：客户端不负责出块，块由快照里的 <c>Current</c> / <c>Next</c> 决定。
    /// </summary>
    public void ApplySnapshot(TetrisEngineSnapshot snapshot)
    {
        if (snapshot is null) return;

        for (var i = 0; i < _board.Length; i++)
            _board[i] = i < snapshot.Cells.Length ? snapshot.Cells[i] : null;

        Current = snapshot.Current;
        _heldSlots.Clear();
        if (snapshot.HeldSlots is { Count: > 0 } slots) _heldSlots.AddRange(slots.Where(k => k != TetrominoKind.Garbage));
        else if (snapshot.Held is { } single) _heldSlots.Add(single);
        Score = snapshot.Score;
        Lines = snapshot.Lines;
        Level = Math.Clamp(snapshot.Level, 1, MaxLevel);
        PendingGarbage = snapshot.PendingGarbage;
        GarbageSent = snapshot.GarbageSent;
        GarbageTaken = snapshot.GarbageTaken;
        IsGameOver = snapshot.IsGameOver;

        _preview.Clear();
        foreach (var kind in snapshot.Next) _preview.Enqueue(kind);
        EnsurePreview();
    }

    /// <summary>把当前方块搬到<paramref name="target"/>（自检 / 调试用，正式玩不会用到）。</summary>
    public void PlacePiece(TetrisPiece? target) => Current = target;

    /// <summary>往棋盘里直接放一块（自检造场景用：铺满某几行、造一个 T 旋地形）。</summary>
    public void SetCell(int x, int y, TetrominoKind? kind)
    {
        if (InBounds(x, y)) _board[y * Width + x] = kind;
    }

    // ---------------------------------------------------------------- 内部

    private bool TryMove(int dx, int dy)
    {
        if (IsGameOver || Current is not { } piece) return false;
        var candidate = piece.Moved(dx, dy);
        if (!Fits(candidate)) return false;
        Current = candidate;
        return true;
    }

    /// <summary>这个摆放是否合法：不出左右边界、不穿棋盘底部、不和已落下的块重叠。</summary>
    public bool Fits(TetrisPiece piece)
    {
        foreach (var (x, y) in piece.Cells())
        {
            if (x < 0 || x >= Width) return false;      // 左右撞墙
            if (y >= Height) return false;              // 穿底
            if (y < 0) continue;                        // 还没进场（允许）
            if (_board[y * Width + x] is not null) return false;
        }
        return true;
    }

    private void SpawnNext()
    {
        EnsurePreview();
        Spawn(TakeFromPreview());
    }

    private void Spawn(TetrominoKind kind)
    {
        var piece = new TetrisPiece(kind, TetrisPieces.SpawnX(kind), SpawnY, 0);
        if (!Fits(piece))
        {
            // 出生位置被占 = 顶死，游戏结束；Current 留 null，界面显示结束遮罩
            Current = null;
            IsGameOver = true;
            return;
        }

        Current = piece;
    }

    /// <summary>消掉填满的行，返回消掉几行（剩下的行整体下落）。</summary>
    private int ClearLines()
    {
        var cleared = 0;
        for (var y = Height - 1; y >= 0; y--)
        {
            if (!IsRowFull(y)) continue;

            cleared++;
            // 把 y 行以上整体往下挤一行，顶上补空行
            for (var row = y; row > 0; row--)
            {
                Array.Copy(_board, (row - 1) * Width, _board, row * Width, Width);
            }
            Array.Clear(_board, 0, Width);
            y++;    // 新的 y 行是从上面挤下来的，必须原地再看一次
        }
        return cleared;
    }

    private bool IsRowFull(int y)
    {
        for (var x = 0; x < Width; x++)
        {
            if (_board[y * Width + x] is null) return false;
        }
        return true;
    }

    private void EnsurePreview()
    {
        while (_preview.Count < Math.Max(1, Math.Min(8, PreviewSlots))) _preview.Enqueue(TakeFromBag());
    }

    private TetrominoKind TakeFromPreview()
    {
        EnsurePreview();
        var next = _preview.Dequeue();
        EnsurePreview();
        return next;
    }

    /// <summary>7-bag：袋子空了就把 7 种方块洗一遍（洗牌用注入的 <see cref="Random"/>）。</summary>
    private TetrominoKind TakeFromBag()
    {
        if (_bag.Count == 0)
        {
            _bag.AddRange(TetrisPieces.All);
            // Fisher–Yates
            for (var i = _bag.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
            }
        }

        var kind = _bag[^1];
        _bag.RemoveAt(_bag.Count - 1);
        return kind;
    }
}
