using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Games.Tetris;
using YuLiangToolkit.Tools.Games.Tetris.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.ViewModels;

/// <summary>
/// 「俄罗斯方块」的 ViewModel：把纯逻辑引擎 <see cref="TetrisEngine"/> 接到界面上 ——
/// 定时下落、命令、状态文案、最高分。
///
/// <para><b>分工</b>：棋盘规则、计分、随机、墙踢全在 <see cref="TetrisEngine"/>（自检无头断言）；
/// 这里只管「什么时候 tick 一下」「界面要显示什么」。所以 ViewModel 里没有任何棋盘算法。</para>
///
/// <para><b>定时器</b>：用 <see cref="DispatcherTimer"/>（UI 线程，和界面同线程最省事），
/// 间隔由 <see cref="TetrisEngine.FallIntervalMs"/> 按等级算；暂停 / 游戏结束 / 页面切走时一律停表，
/// 否则外壳把页面缓存起来之后方还会继续往下掉（切回来已经堆满）。</para>
/// </summary>
public sealed partial class TetrisViewModel : ToolViewModelBase
{
    /// <summary>棋盘渲染快照的宽度 / 高度（引擎的常量，界面排版也按它算）。</summary>
    public const int BoardWidth = TetrisEngine.Width;

    /// <summary>棋盘渲染快照的高度。</summary>
    public const int BoardHeight = TetrisEngine.Height;

    private readonly DispatcherTimer _fallTimer;
    private readonly TetrisEngine _engine;
    private TetrisBoardViewState _boardState = TetrisBoardViewState.Empty;
    private bool _isPaused;
    private bool _isAway;
    private bool _started;
    private bool _newRecord;
    private string _comboText = "";

    public TetrisViewModel() : base(TetrisConfig.ToolId)
    {
        Cfg = LoadConfig<TetrisConfig>();
        _engine = new TetrisEngine();

        _fallTimer = new DispatcherTimer(DispatcherPriority.Render);
        _fallTimer.Tick += OnFallTick;

        NewGameCommand = new RelayCommand(_ => NewGame());
        PauseCommand = new RelayCommand(_ => TogglePause(), _ => _started && !_engine.IsGameOver);
        MoveLeftCommand = new RelayCommand(_ => MoveLeft());
        MoveRightCommand = new RelayCommand(_ => MoveRight());
        SoftDropCommand = new RelayCommand(_ => SoftDrop());
        HardDropCommand = new RelayCommand(_ => HardDrop());
        RotateClockwiseCommand = new RelayCommand(_ => Rotate(clockwise: true));
        RotateCounterClockwiseCommand = new RelayCommand(_ => Rotate(clockwise: false));
        HoldCommand = new RelayCommand(_ => Hold());
        StartOrResumeCommand = new RelayCommand(_ => StartOrResume());
        InitializeNetCommands();
        InitializeAi();          // 电脑对手（见 TetrisViewModel.Ai.cs）
        InitializeDuelCommands();   // 预览位置 / 观战视角（见 TetrisViewModel.Duel.cs）

        Cfg.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TetrisConfig.ShowGhost) or nameof(TetrisConfig.ShowGrid))
                RefreshBoard();
            if (e.PropertyName is nameof(TetrisConfig.HighScore))
                OnPropertyChanged(nameof(HighScoreText));
        };

        // 开局就摆一块在场上：打开页面就能看到棋盘长什么样（还没开始计时，按键或点「开始」才真的下落）
        StartNewGame(autoStart: false);
        StatusText = "按「开始游戏」或任意移动键开始";

        // 演示棋盘要在「初始化联机演示」之前铺：
        // 联机演示（YLT_TETRIS_NET）内部会自己重开一局并铺自己的棋盘，
        // 顺序反了会把它铺好的局面又冲掉（表现为分数 / 棋盘全是初始值）。
        if (IsDemoMode) SeedDemoBoard();

        // 联机演示必须放在这之后（它会自己 Reset 引擎、铺对抗局面）
        ApplyDemoNetMode();
    }

    // ---------------------------------------------------------------- 配置 / 命令

    /// <summary>本工具的配置（最高分 / 起始等级 / 显示开关）。</summary>
    public TetrisConfig Cfg { get; }

    public RelayCommand NewGameCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand MoveLeftCommand { get; }
    public RelayCommand MoveRightCommand { get; }
    public RelayCommand SoftDropCommand { get; }
    public RelayCommand HardDropCommand { get; }
    public RelayCommand RotateClockwiseCommand { get; }
    public RelayCommand RotateCounterClockwiseCommand { get; }
    public RelayCommand HoldCommand { get; }
    public RelayCommand StartOrResumeCommand { get; }

    // ---------------------------------------------------------------- 界面状态

    /// <summary>棋盘渲染快照（自绘控件绑它）。</summary>
    public TetrisBoardViewState BoardState
    {
        get => _boardState;
        private set => Set(ref _boardState, value);
    }

    /// <summary>接下来几块（预览区）。</summary>
    public IReadOnlyList<TetrominoKind> NextPieces => _engine.NextPieces;

    /// <summary>暂存的方块（没有时为空列表，预览控件据此画空格）。</summary>
    public IReadOnlyList<TetrominoKind> HeldPieces
        => _engine.Held is { } held ? new[] { held } : Array.Empty<TetrominoKind>();

    /// <summary>分数文本。</summary>
    public string ScoreText => _engine.Score.ToString();

    /// <summary>最高分文本（「新纪录」时加个标）。</summary>
    public string HighScoreText => _newRecord ? $"{Cfg.HighScore} · 新纪录" : Cfg.HighScore.ToString();

    /// <summary>当前等级文本。</summary>
    public string LevelText => _engine.Level.ToString();

    /// <summary>已消行数文本。</summary>
    public string LinesText => _engine.Lines.ToString();

    /// <summary>这一手的结算文案（消行提示；没消行为空）。</summary>
    public string ComboText => _comboText;

    /// <summary>暂停中。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!Set(ref _isPaused, value)) return;
            OnPropertyChanged(nameof(OverlayVisible));
            OnPropertyChanged(nameof(OverlayTitle));
            OnPropertyChanged(nameof(OverlayHint));
        }
    }

    /// <summary>游戏结束。</summary>
    public bool IsGameOver => _engine.IsGameOver;

    /// <summary>还没开始（棋盘摆好了但没在计时）。</summary>
    public bool IsWaitingToStart => !_started && !_engine.IsGameOver;

    /// <summary>要不要盖遮罩层（暂停 / 结束 / 还没开始）。</summary>
    public bool OverlayVisible => !_started || _isPaused || _engine.IsGameOver;

    /// <summary>遮罩上的大标题。</summary>
    public string OverlayTitle => _engine.IsGameOver ? "游戏结束" : _isPaused ? "已暂停" : "准备好了吗";

    /// <summary>遮罩上的提示行。</summary>
    public string OverlayHint => _engine.IsGameOver
        ? $"得分 {_engine.Score} · 消行 {_engine.Lines} · 等级 {_engine.Level}；按 R 或点「重新开始」再来一局"
        : _isPaused
            ? "按 Esc / P 或点「继续」回到游戏"
            : "按任意方向键或点「开始游戏」（←→ 移动 · ↑ 旋转 · 空格 硬降）";

    /// <summary>暂停按钮上的字。</summary>
    public string PauseButtonText => _isPaused ? "继续" : "暂停";

    // ---------------------------------------------------------------- 操作（键盘 / 按钮共用）

    /// <summary>新开一局（立即开始下落）。</summary>
    public void NewGame()
    {
        _newRecord = false;
        StartNewGame(autoStart: true);
        StatusText = "新的一局开始了";
        Log($"新开一局 · 起始等级 {Cfg.StartLevel}");
    }

    /// <summary>开始 / 继续（遮罩上的按钮，或者按 Enter）。</summary>
    public void StartOrResume()
    {
        if (_engine.IsGameOver) NewGame();
        else if (_isPaused) TogglePause();
        else if (!_started) BeginFalling();
    }

    /// <summary>
    /// 遮罩状态变化：把界面上依赖它的几项一次性通知到位（遮罩 / 标题 / 提示 / 暂停按钮 / 开始态）。
    /// </summary>
    private void NotifyOverlay()
    {
        OnPropertyChanged(nameof(OverlayVisible));
        OnPropertyChanged(nameof(OverlayTitle));
        OnPropertyChanged(nameof(OverlayHint));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(IsWaitingToStart));
        OnPropertyChanged(nameof(IsGameOver));
    }

    /// <summary>暂停 / 继续切换。</summary>
    public void TogglePause()
    {
        if (_engine.IsGameOver || !_started) return;

        IsPaused = !IsPaused;
        StatusText = IsPaused ? "已暂停" : "继续";
        UpdateTimer();
        OnPropertyChanged(nameof(PauseButtonText));
    }

    /// <summary>左移。</summary>
    public void MoveLeft()
    {
        if (!EnsureRunning()) return;
        if (ReportToHost(TetrisNetProtocol.Actions.Left)) return;
        if (!_engine.MoveLeft()) return;
        RefreshBoard();
    }

    /// <summary>右移。</summary>
    public void MoveRight()
    {
        if (!EnsureRunning()) return;
        if (ReportToHost(TetrisNetProtocol.Actions.Right)) return;
        if (!_engine.MoveRight()) return;
        RefreshBoard();
    }

    /// <summary>软降一格（按住方向键会连续触发）。</summary>
    public void SoftDrop()
    {
        if (!EnsureRunning()) return;
        if (ReportToHost(TetrisNetProtocol.Actions.SoftDrop)) return;
        if (_engine.SoftDrop())
        {
            RestartFallTimer();
            RefreshBoard();
            RefreshScore();
            return;
        }

        // 已经到底：直接锁（和「按下方向键立刻落地」的老手感一致）
        LockAndContinue();
    }

    /// <summary>硬降（空格）。</summary>
    public void HardDrop()
    {
        if (!EnsureRunning()) return;
        if (ReportToHost(TetrisNetProtocol.Actions.HardDrop)) return;
        var result = _engine.HardDrop();
        AfterLock(result);
    }

    /// <summary>旋转；<paramref name="clockwise"/> 为 false 时逆时针。</summary>
    public void Rotate(bool clockwise)
    {
        if (!EnsureRunning()) return;
        if (ReportToHost(clockwise ? TetrisNetProtocol.Actions.RotateCw : TetrisNetProtocol.Actions.RotateCcw)) return;
        if (!_engine.Rotate(clockwise).Success) return;
        RefreshBoard();
    }

    /// <summary>暂存 / 换出（C）。</summary>
    public void Hold()
    {
        if (!EnsureRunning()) return;
        if (ReportToHost(TetrisNetProtocol.Actions.Hold)) return;
        if (!_engine.Hold()) return;
        RefreshBoard();
        OnPropertyChanged(nameof(HeldPieces));
        OnPropertyChanged(nameof(NextPieces));
    }

    /// <summary>
    /// 联机客户端：把按键上报给房主，由房主在权威引擎上执行（本端不跑规则）。
    /// 返回 true = 已经交给网络处理，调用方不要再改本地引擎。
    /// <para>房主与单人玩法返回 false，照旧在本地执行。</para>
    /// </summary>
    private bool ReportToHost(string action)
    {
        if (!IsNetMode) return false;

        // 观战者只能看，任何操作都吞掉（服务端也会忽略）
        if (IsSpectating) return true;

        var session = _session;
        if (session is null || session.IsHost) return false;
        if (_netPhase != TetrisNetPhase.Playing) return true;      // 还没开始：吞掉按键，本地不动
        _ = session.SendActionAsync(action);
        return true;
    }

    /// <summary>
    /// 视图被切走（外壳会缓存工具页，不会调 OnClosed）：先暂停，免得切回来方已经堆到底。
    /// 视图 <c>Loaded</c> 时再调 <see cref="ResumeAfterAway"/> 决定要不要恢复。
    /// </summary>
    public void PauseForAway()
    {
        _isAway = true;
        UpdateTimer();
    }

    /// <summary>视图重新回到界面：如果是因为切走才停的表，就接着跑。</summary>
    public void ResumeAfterAway()
    {
        _isAway = false;
        UpdateTimer();
    }

    /// <summary>窗口失去焦点时的自动暂停（设置里可关）。</summary>
    public void PauseForLostFocus()
    {
        if (!Cfg.PauseOnLostFocus || !_started || _engine.IsGameOver || IsPaused) return;
        TogglePause();
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>调试 / 截图用：<c>YLT_TETRIS_DEMO=1</c> 时摆一个像玩到一半的棋盘（不写配置）。</summary>
    private static bool IsDemoMode
        => Environment.GetEnvironmentVariable("YLT_TETRIS_DEMO") == "1";

    private void StartNewGame(bool autoStart)
    {
        _engine.Reset(Cfg.StartLevel);
        _started = false;
        IsPaused = false;
        _comboText = "";

        RefreshBoard();
        RefreshScore();
        OnPropertyChanged(nameof(NextPieces));
        OnPropertyChanged(nameof(HeldPieces));
        OnPropertyChanged(nameof(ComboText));
        NotifyOverlay();

        if (autoStart) BeginFalling();
        else UpdateTimer();
    }

    private void BeginFalling()
    {
        _started = true;
        _comboText = "";
        NotifyOverlay();
        OnPropertyChanged(nameof(ComboText));
        StatusText = "进行中";
        RestartFallTimer();
        StartAiLoop();          // 联机模式下电脑对手开始自动落子（单人模式里是空操作）
    }

    /// <summary>按键类操作的前置检查：遮罩没关就先开局，暂停中不动。</summary>
    private bool EnsureRunning()
    {
        if (_engine.IsGameOver) return false;
        if (!_started)
        {
            BeginFalling();
            return true;
        }
        return !IsPaused;
    }

    private void OnFallTick(object? sender, EventArgs e)
    {
        if (_engine.IsGameOver || IsPaused)
        {
            UpdateTimer();
            return;
        }

        if (_engine.Step())
        {
            RefreshBoard();
            return;
        }

        LockAndContinue();
    }

    private void LockAndContinue()
    {
        var result = _engine.Lock();
        AfterLock(result);
    }

    private void AfterLock(TetrisLockResult result)
    {
        _comboText = result.ComboText;

        // 联机互攻模式下，落地要额外负责「送垃圾行 / 判定胜负并广播」
        if (IsNetMode && _session is not null && _session.IsHost)
        {
            AfterLockForNet(result);
            return;
        }

        if (_engine.IsGameOver)
        {
            if (IsNetMode) FinishNetGame();
            else EndGame();
            return;
        }

        RefreshBoard();
        RefreshScore();
        OnPropertyChanged(nameof(HeldPieces));
        RestartFallTimer();

        if (_comboText.Length > 0) Log($"消除 {result.LinesCleared} 行 +{result.ScoreGained}（等级 {result.Level}）");

        if (IsNetMode) PublishNetState();
    }

    private void EndGame()
    {
        _started = false;
        UpdateTimer();
        _comboText = "";
        RefreshBoard();
        RefreshScore();

        var record = Cfg.SubmitScore(_engine.Score);
        _newRecord = record;
        OnPropertyChanged(nameof(HighScoreText));
        NotifyOverlay();
        OnPropertyChanged(nameof(ComboText));

        StatusText = record ? $"游戏结束 · 新纪录 {_engine.Score} 分！" : $"游戏结束 · {_engine.Score} 分";
        Log($"游戏结束：{_engine.Score} 分 · 消行 {_engine.Lines} · 等级 {_engine.Level}{(record ? "（新纪录）" : "")}");
    }

    private void RefreshBoard()
    {
        BoardState = TetrisBoardViewState.Create(
            _engine,
            showGhost: Cfg.ShowGhost,
            showGrid: Cfg.ShowGrid);
    }

    private void RefreshScore()
    {
        OnPropertyChanged(nameof(ScoreText));
        OnPropertyChanged(nameof(LevelText));
        OnPropertyChanged(nameof(LinesText));
        OnPropertyChanged(nameof(NextPieces));
        OnPropertyChanged(nameof(IsGameOver));
        OnPropertyChanged(nameof(IsWaitingToStart));
    }

    /// <summary>接下来的下落间隔（按等级）。</summary>
    private TimeSpan FallInterval
        => TimeSpan.FromMilliseconds(TetrisEngine.FallIntervalMs(_engine.Level));

    private void UpdateTimer()
    {
        var shouldRun = _started && !IsPaused && !_isAway && !_engine.IsGameOver;
        if (shouldRun)
        {
            _fallTimer.Interval = FallInterval;
            if (!_fallTimer.IsEnabled) _fallTimer.Start();
            return;
        }

        if (_fallTimer.IsEnabled) _fallTimer.Stop();
    }

    private void RestartFallTimer()
    {
        _fallTimer.Stop();
        UpdateTimer();
    }

    /// <summary>重写：外壳释放这个工具页时把定时器停掉（不然看不见的页面还在掉方）。</summary>
    protected override void OnClosed()
    {
        _fallTimer.Stop();
        _fallTimer.Tick -= OnFallTick;
        base.OnClosed();
    }

    /// <summary>调试 / 截图：摆一个「玩到一半」的棋盘，让截图不是一片空白。</summary>
    private void SeedDemoBoard()
    {
        var kinds = TetrisPieces.All;
        for (var y = BoardHeight - 4; y < BoardHeight; y++)
        {
            for (var x = 0; x < BoardWidth; x++)
            {
                // 留出几个缺口，看起来像真的在玩
                if ((x + y) % 5 == 0) continue;
                _engine.SetCell(x, y, kinds[(x + y) % kinds.Count]);
            }
        }

        _engine.PlacePiece(new TetrisPiece(TetrominoKind.T, 3, 6, 1));
        _started = true;
        IsPaused = false;
        RefreshBoard();
        RefreshScore();
        OnPropertyChanged(nameof(OverlayVisible));
        StatusText = "演示棋盘（YLT_TETRIS_DEMO=1）";
    }
}

/// <summary>
/// 棋盘渲染快照（不可变）：自绘控件只认这一份数据，绘制时不回读引擎、不加锁。
/// 每次局面变化由 ViewModel 造一个新的塞进去（依赖属性一变即重绘）。
/// </summary>
public sealed class TetrisBoardViewState
{
    /// <summary>棋盘格子：<c>Width × Height</c>，<c>null</c> = 空。</summary>
    public IReadOnlyList<TetrominoKind?> Cells { get; init; } = Array.Empty<TetrominoKind?>();

    /// <summary>当前方块（null = 没有）。</summary>
    public TetrisPiece? Piece { get; init; }

    /// <summary>幽灵方块落点（null = 不画 / 没有）。</summary>
    public TetrisPiece? Ghost { get; init; }

    /// <summary>画网格线。</summary>
    public bool ShowGrid { get; init; } = true;

    /// <summary>还没开局 / 已结束时的空状态（控件拿到它也能正常画）。</summary>
    public static TetrisBoardViewState Empty { get; } = new()
    {
        Cells = new TetrominoKind?[TetrisEngine.Width * TetrisEngine.Height],
    };

    /// <summary>从引擎生成快照。</summary>
    public static TetrisBoardViewState Create(TetrisEngine engine, bool showGhost, bool showGrid)
    {
        var cells = new TetrominoKind?[TetrisEngine.Width * TetrisEngine.Height];
        for (var y = 0; y < TetrisEngine.Height; y++)
        {
            for (var x = 0; x < TetrisEngine.Width; x++)
            {
                cells[y * TetrisEngine.Width + x] = engine.CellAt(x, y);
            }
        }

        return new TetrisBoardViewState
        {
            Cells = cells,
            Piece = engine.Current,
            Ghost = showGhost ? engine.GhostPiece() : null,
            ShowGrid = showGrid,
        };
    }

    /// <summary>从联机快照生成渲染状态（客户端画对手 / 自己的棋盘用；不画幽灵，按格子还原）。</summary>
    public static TetrisBoardViewState FromSnapshot(TetrisEngineSnapshot snapshot)
        => new()
        {
            Cells = snapshot.Cells,
            Piece = snapshot.Current,
            Ghost = null,
            ShowGrid = true,
        };

    /// <summary>反向：把渲染状态还原成快照（房主广播对手棋盘缩略图时用）。</summary>
    public TetrisEngineSnapshot ToSnapshot() => new()
    {
        Cells = Cells.ToArray(),
        Current = Piece,
        Level = 1,
        IsGameOver = false,
        Next = Array.Empty<TetrominoKind>(),
    };

    /// <summary>取某一格（越界 / 数据不完整返回 null）。</summary>
    public TetrominoKind? CellAt(int x, int y)
    {
        if (x < 0 || x >= TetrisEngine.Width || y < 0 || y >= TetrisEngine.Height) return null;
        var index = y * TetrisEngine.Width + x;
        return index < Cells.Count ? Cells[index] : null;
    }
}
