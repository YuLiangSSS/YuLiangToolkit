using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using YuLiangToolkit.Core.Games;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Tools.BoardGames.Go;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Go.ViewModels;

/// <summary>棋盘上一格的界面数据。</summary>
public sealed class GoCellItem : ObservableObject
{
    private GoCell _cell;
    private bool _isLast;
    private bool _isHover;

    /// <summary>创建格子。</summary>
    /// <param name="x">列。</param>
    /// <param name="y">行。</param>
    public GoCellItem(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>列。</summary>
    public int X { get; }

    /// <summary>行。</summary>
    public int Y { get; }

    /// <summary>棋子。</summary>
    public GoCell Cell
    {
        get => _cell;
        set
        {
            if (!Set(ref _cell, value)) return;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsBlack));
            OnPropertyChanged(nameof(IsWhite));
        }
    }

    /// <summary>是不是空点。</summary>
    public bool IsEmpty => _cell == GoCell.Empty;

    /// <summary>是不是黑子。</summary>
    public bool IsBlack => _cell == GoCell.Black;

    /// <summary>是不是白子。</summary>
    public bool IsWhite => _cell == GoCell.White;

    /// <summary>是不是最后一手（画个小标记）。</summary>
    public bool IsLast
    {
        get => _isLast;
        set => Set(ref _isLast, value);
    }

    /// <summary>鼠标是否悬停（预览落子）。</summary>
    public bool IsHover
    {
        get => _isHover;
        set => Set(ref _isHover, value);
    }
}

/// <summary>
/// 「围棋」界面 VM：单机（含电脑对手）/ 联机（TCP 房间 + 观战 + 换人）共用。
///
/// <para><b>联机复用</b>：直接继承 <see cref="BoardGameRoomViewModelBase"/>，
/// 房间成员、上场席位、房主安排、自动换人、观战、断线重连全都有现成的 ——
/// 围棋只需要实现「游戏规则快照 + 动作」这几个钩子。</para>
/// </summary>
public sealed class GoViewModel : BoardGameRoomViewModelBase
{
    private GoGame _game = new();
    private GoCellItem[] _cells = Array.Empty<GoCellItem>();
    private (int X, int Y)? _lastMove;
    private bool _waitingHostMove;
    private AiDifficulty _aiDifficulty = AiDifficulty.Normal;
    private bool _aiEnabled;
    private bool _aiThinking;
    private DispatcherTimer? _aiTimer;
    private bool _insideRefresh;

    /// <summary>创建 VM。</summary>
    public GoViewModel() : base(GoConfig.ToolId, "围棋")
    {
        Cfg = LoadConfig<GoConfig>();
        AttachRoomConfig(Cfg);

        _game = new GoGame(Cfg.BoardSize, Cfg.Komi) { Started = false };

        BoardSizeOptions = OptionItem.From((9, "9 路（快棋）"), (13, "13 路（中盘）"), (19, "19 路（标准）"));
        KomiOptions = OptionItem.From((6.5, "6.5 目"), (7.5, "7.5 目（中国规则常用）"), (5.5, "5.5 目"));
        AiDifficultyOptions = OptionItem.From(
            AiDifficulties.All.Select(d => (AiDifficulties.ToKey(d), AiDifficulties.DisplayName(d))).ToArray());

        PlaceCommand = new RelayCommand(p =>
        {
            if (p is GoCellItem item) PlaceAt(item);
        });
        PassCommand = new RelayCommand(_ => PlayPass(), _ => CanPlayNow);
        UndoCommand = new RelayCommand(_ => UndoOne(), _ => CanUndo);
        ResignCommand = new RelayCommand(_ => Resign(), _ => CanResignPlay);
        ScoreCommand = new RelayCommand(_ => ManualScore(), _ => CanScore);
        CopyMovesCommand = new RelayCommand(_ => CopyMoves());

        PropertyChanged += (_, e) =>
        {
            if (_insideRefresh) return;
            if (e.PropertyName is nameof(IsOnline) or nameof(Mode) or nameof(LocalSide)
                or nameof(IsSeatedPlayer) or nameof(IsSpectator))
            {
                RefreshAll();
            }
        };

        _aiTimer = new DispatcherTimer();
        _aiTimer.Tick += OnAiTick;
        _aiDifficulty = AiDifficulties.Parse(Cfg.AiDifficulty);
        _aiEnabled = Cfg.AiEnabled;

        RebuildCells();
        RefreshAll();
    }

    /// <summary>配置。</summary>
    public GoConfig Cfg { get; }

    /// <summary>棋盘格子。</summary>
    public ObservableCollection<GoCellItem> Cells { get; } = new();

    /// <summary>棋盘路数选项。</summary>
    public IReadOnlyList<OptionItem<int>> BoardSizeOptions { get; }

    /// <summary>贴目选项。</summary>
    public IReadOnlyList<OptionItem<double>> KomiOptions { get; }

    /// <summary>难度选项。</summary>
    public IReadOnlyList<OptionItem<string>> AiDifficultyOptions { get; }

    /// <summary>命令。</summary>
    public RelayCommand PlaceCommand { get; }
    public RelayCommand PassCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand ResignCommand { get; }
    public RelayCommand ScoreCommand { get; }
    public RelayCommand CopyMovesCommand { get; }

    // ---------------------------------------------------------------- 状态

    /// <summary>现在轮到哪一方。</summary>
    public GoCell Turn => _game.Turn;

    /// <summary>轮到谁的文字。</summary>
    public string TurnText => _game.IsOver ? "对局结束" : $"轮到 {GoGame.PlayerName(_game.Turn)}";

    /// <summary>我的手牌 / 执子文字。</summary>
    public string MySideText => IsLocalMode
        ? (_aiEnabled ? "你执黑 vs 电脑" : "同机双人")
        : IsOnline && IsSeatedPlayer ? $"你执 {(LocalSide == "black" ? "黑" : "白")}"
        : IsOnline ? "观战中（不能落子）" : "未进房间";

    /// <summary>提子数文字。</summary>
    public string CaptureText => $"黑提 {_game.BlackCaptures} 子 · 白提 {_game.WhiteCaptures} 子";

    /// <summary>手数文字。</summary>
    public string MoveCountText => $"已走 {_game.DisplayMoveCount} 手";

    /// <summary>结果文字。</summary>
    public string ResultText => _game.ResultText;

    /// <summary>能不能落子。</summary>
    public bool CanPlayNow
        => _game.Started && !_game.IsOver
           && (IsLocalMode || (IsOnline && IsSeatedPlayer && !_waitingHostMove))
           && (!IsLocalMode || !_aiEnabled || _game.Turn != AiSide);

    /// <summary>能不能悔棋。</summary>
    public bool CanUndo => _game.Started && _game.MoveCount > 0 && !IsOnline;

    /// <summary>能不能认输。</summary>
    public bool CanResignPlay => _game.Started && !_game.IsOver && (IsLocalMode || (IsOnline && IsSeatedPlayer));

    /// <summary>能不能数子。</summary>
    public bool CanScore => _game.Started && !_game.IsOver;

    /// <summary>电脑执哪一方。</summary>
    public GoCell AiSide => GoCell.White;

    /// <summary>难度。</summary>
    public AiDifficulty AiDifficulty
    {
        get => _aiDifficulty;
        set
        {
            if (!Set(ref _aiDifficulty, value)) return;
            OnPropertyChanged(nameof(SelectedAiDifficulty));
            OnPropertyChanged(nameof(AiDifficultyHint));
            Cfg.AiDifficulty = AiDifficulties.ToKey(value);
        }
    }

    /// <summary>难度选中项。</summary>
    public OptionItem<string> SelectedAiDifficulty
    {
        get => OptionItem<string>.Find(AiDifficultyOptions, AiDifficulties.ToKey(_aiDifficulty)) ?? AiDifficultyOptions[1];
        set { if (value is not null) AiDifficulty = AiDifficulties.Parse(value.Value); }
    }

    /// <summary>难度说明。</summary>
    public string AiDifficultyHint => AiDifficulties.Description(_aiDifficulty);

    /// <summary>开电脑对手。</summary>
    public bool AiEnabled
    {
        get => _aiEnabled;
        set
        {
            if (!Set(ref _aiEnabled, value)) return;
            Cfg.AiEnabled = value;
            OnPropertyChanged(nameof(AiOpponentText));
            OnPropertyChanged(nameof(MySideText));
            OnPropertyChanged(nameof(CanPlayNow));
            if (value) TryStartAiTurn();
        }
    }

    /// <summary>电脑状态文字。</summary>
    public string AiOpponentText => !_aiEnabled
        ? "未开电脑对手（同机双人）"
        : _aiThinking ? $"电脑（{AiDifficulties.DisplayName(_aiDifficulty)}）正在思考…"
        : $"电脑对手：{AiDifficulties.DisplayName(_aiDifficulty)}（执白后手）";

    /// <summary>是不是电脑在思考。</summary>
    public bool AiThinking => _aiThinking;

    /// <summary>棋盘路数选中项。</summary>
    public OptionItem<int> SelectedBoardSize
    {
        get => OptionItem<int>.Find(BoardSizeOptions, Cfg.BoardSize) ?? BoardSizeOptions[2];
        set
        {
            if (value is null) return;
            Cfg.BoardSize = value.Value;
            OnPropertyChanged();
            if (!IsOnline && !_game.Started) NewLocalGame();
        }
    }

    /// <summary>贴目选中项。</summary>
    public OptionItem<double> SelectedKomi
    {
        get => OptionItem<double>.Find(KomiOptions, Cfg.Komi) ?? KomiOptions[1];
        set
        {
            if (value is null) return;
            Cfg.Komi = value.Value;
            OnPropertyChanged();
        }
    }

    // ---------------------------------------------------------------- 落子

    /// <summary>在某个点落子（界面点击调用）。</summary>
    public void PlaceAt(GoCellItem? item)
    {
        if (item is null) return;

        if (!_game.Started)
        {
            SetNotice(IsLocalMode ? "点「新开一局」开始" : "对局还没开始：等房主点「开始对局」", "DangerBrush");
            return;
        }
        if (_game.IsOver) { SetNotice("这一局已经结束了", "DangerBrush"); return; }
        if (IsLocalMode && _aiEnabled && _game.Turn == AiSide)
        {
            SetNotice("现在是电脑在走", "TextSecondaryBrush");
            return;
        }

        var mySide = CurrentPlaySide();
        if (mySide == GoCell.Empty) { SetNotice("你现在不能落子（观战 / 未上场）", "DangerBrush"); return; }
        if (_game.Turn != mySide) { SetNotice("还没轮到你", "DangerBrush"); return; }

        if (IsClient)
        {
            // 客户端只上报，等房主算完回快照
            SendAction("play", new { x = item.X, y = item.Y });
            _waitingHostMove = true;
            SetNotice("已提交，等房主确认…", "TextSecondaryBrush");
            RefreshAll();
            return;
        }

        var result = _game.Play(mySide, item.X, item.Y);
        if (result != GoPlayResult.Ok)
        {
            SetNotice(DescribeResult(result), "DangerBrush");
            return;
        }

        if (Mode == BoardPlayMode.Host) BroadcastSnapshot();
        RefreshAll();
        Announce(GoGame.PlayerName(mySide), item.X, item.Y);
        TryStartAiTurn();
    }

    private GoCell CurrentPlaySide()
    {
        if (IsLocalMode) return _game.Turn;                       // 同机：谁该走就替谁走
        if (!IsOnline || !IsSeatedPlayer) return GoCell.Empty;    // 观战
        return GoGame.Parse(LocalSide);
    }

    private void PlayPass()
    {
        var side = CurrentPlaySide();
        if (side == GoCell.Empty || _game.Turn != side) return;

        if (IsClient) { SendAction("pass", null); _waitingHostMove = true; return; }

        var result = _game.Pass(side);
        if (result != GoPlayResult.Ok) { SetNotice(DescribeResult(result), "DangerBrush"); return; }

        if (Mode == BoardPlayMode.Host) BroadcastSnapshot();
        RefreshAll();
        SetNotice($"{GoGame.PlayerName(side)}停一手", "TextSecondaryBrush");
        if (_game.IsOver) SetNotice(_game.ResultText, "SuccessBrush");
        TryStartAiTurn();
    }

    private void UndoOne()
    {
        if (IsOnline) { SetNotice("联机时不能悔棋（会跟对方不同步）", "DangerBrush"); return; }
        if (!_game.Undo()) return;
        RefreshAll();
        SetNotice("已悔棋", "TextSecondaryBrush");
    }

    private void Resign()
    {
        var side = CurrentPlaySide();
        if (side == GoCell.Empty) return;

        if (IsClient) { SendAction("resign", null); return; }

        _game.Resign(side);
        if (Mode == BoardPlayMode.Host) BroadcastSnapshot();
        RefreshAll();
        SetNotice(_game.ResultText, "SuccessBrush");
    }

    private void ManualScore()
    {
        var result = _game.Score();
        if (Mode == BoardPlayMode.Host) BroadcastSnapshot();
        RefreshAll();
        SetNotice(result.Text, "SuccessBrush");
    }

    private void CopyMoves()
    {
        var moves = _game.History
            .Where(m => !m.IsPass)
            .Select(m => $"{GoGame.PlayerName(m.Player)} ({(char)('A' + m.X)}{m.Y + 1})");
        SetNotice(string.Join("  ", moves), "TextSecondaryBrush");
    }

    private static string DescribeResult(GoPlayResult result) => result switch
    {
        GoPlayResult.Occupied => "这里已经有子了",
        GoPlayResult.Suicide => "禁自杀：下在这里自己的棋没气",
        GoPlayResult.Ko => "打劫：不能立刻回提（先在别处走一手）",
        GoPlayResult.NotYourTurn => "还没轮到你",
        GoPlayResult.OutOfBounds => "下到棋盘外面了",
        GoPlayResult.GameOver => "对局已经结束",
        _ => "这一步不能下",
    };

    private void Announce(string who, int x, int y)
    {
        if (_game.IsOver) { SetNotice(_game.ResultText, "SuccessBrush"); return; }
        var coordinate = $"{(char)('A' + x)}{y + 1}";
        SetNotice($"{who}落在 {coordinate}，轮到 {GoGame.PlayerName(_game.Turn)}", "TextSecondaryBrush");
    }

    // ---------------------------------------------------------------- 刷新

    private void RebuildCells()
    {
        Cells.Clear();
        _cells = new GoCellItem[_game.Size * _game.Size];
        for (var y = 0; y < _game.Size; y++)
        {
            for (var x = 0; x < _game.Size; x++)
            {
                var item = new GoCellItem(x, y);
                _cells[y * _game.Size + x] = item;
                Cells.Add(item);
            }
        }
        OnPropertyChanged(nameof(Cells));
    }

    private void RefreshAll()
    {
        _insideRefresh = true;
        try
        {
            if (_cells.Length != _game.Size * _game.Size) RebuildCells();

            for (var y = 0; y < _game.Size; y++)
            {
                for (var x = 0; x < _game.Size; x++)
                {
                    _cells[y * _game.Size + x].Cell = _game.Get(x, y);
                    _cells[y * _game.Size + x].IsLast = false;
                }
            }

            if (_game.LastMove is { IsPass: false } last && _game.InBounds(last.X, last.Y))
                _cells[last.Y * _game.Size + last.X].IsLast = true;

            OnPropertyChanged(nameof(TurnText));
            OnPropertyChanged(nameof(MySideText));
            OnPropertyChanged(nameof(CaptureText));
            OnPropertyChanged(nameof(MoveCountText));
            OnPropertyChanged(nameof(ResultText));
            OnPropertyChanged(nameof(CanPlayNow));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanResignPlay));
            OnPropertyChanged(nameof(CanScore));
            OnPropertyChanged(nameof(AiOpponentText));
            OnPropertyChanged(nameof(SelectedBoardSize));
            OnPropertyChanged(nameof(SelectedKomi));
            OnPropertyChanged(nameof(SelectedAiDifficulty));
        }
        finally
        {
            _insideRefresh = false;
        }
    }

    // ---------------------------------------------------------------- 电脑对手

    private void TryStartAiTurn()
    {
        if (!_aiEnabled || !IsLocalMode) return;
        if (_aiTimer is null || _aiThinking) return;
        if (!_game.Started || _game.IsOver || _game.Turn != AiSide) return;

        _aiThinking = true;
        OnPropertyChanged(nameof(AiThinking));
        OnPropertyChanged(nameof(AiOpponentText));
        OnPropertyChanged(nameof(CanPlayNow));

        // 围棋思考慢一点也正常，给个「拟人」延迟
        _aiTimer.Interval = TimeSpan.FromMilliseconds(AiDifficulties.ThinkingDelayMs(_aiDifficulty));
        _aiTimer.Start();
    }

    private void OnAiTick(object? sender, EventArgs e)
    {
        _aiTimer?.Stop();
        if (!_aiEnabled || !IsLocalMode || _game.IsOver || _game.Turn != AiSide)
        {
            _aiThinking = false;
            OnPropertyChanged(nameof(AiThinking));
            return;
        }

        var move = GoAi.ChooseMove(_game, AiSide, _aiDifficulty);
        _aiThinking = false;

        if (move is { } point)
        {
            if (_game.Play(AiSide, point.X, point.Y) != GoPlayResult.Ok)
                _game.Pass(AiSide);          // 兜底：落不下就停一手
        }
        else
        {
            _game.Pass(AiSide);
        }

        OnPropertyChanged(nameof(AiThinking));
        OnPropertyChanged(nameof(AiOpponentText));
        RefreshAll();
    }

    // ---------------------------------------------------------------- 房间基类钩子

    /// <inheritdoc />
    protected override string FirstSeatSide => "black";

    /// <inheritdoc />
    protected override string PreferredHostSeatSide => Cfg.HostSide;

    /// <inheritdoc />
    protected override string OppositeSide(string side)
        => string.Equals(side, "black", StringComparison.OrdinalIgnoreCase) ? "white" : "black";

    /// <inheritdoc />
    protected override string SideDisplayName(string side)
        => string.Equals(side, "black", StringComparison.OrdinalIgnoreCase) ? "黑棋" : "白棋";

    /// <inheritdoc />
    protected override BoardGameStatus ReportGameStatus()
    {
        var winner = _game.Winner switch
        {
            GoCell.Black => "black",
            GoCell.White => "white",
            _ => null,
        };
        return new BoardGameStatus(_game.Started, _game.IsOver, winner);
    }

    /// <inheritdoc />
    protected override void ResetGameToWaiting()
    {
        _game = new GoGame(Cfg.BoardSize, Cfg.Komi) { Started = false };
        _waitingHostMove = false;
        RefreshAll();
    }

    /// <inheritdoc />
    protected override JsonElement BuildGameSnapshot()
        => JsonSerializer.SerializeToElement(_game.ToSnapshot(), RoomProtocol.JsonOptions);

    /// <inheritdoc />
    protected override void ApplyGameSnapshot(JsonElement game, string mySide)
    {
        GoSnapshot? snapshot;
        try { snapshot = game.Deserialize<GoSnapshot>(RoomProtocol.JsonOptions); }
        catch (Exception ex)
        {
            SetNotice($"局面数据解析失败：{ex.Message}", "DangerBrush");
            return;
        }

        var (restored, error) = GoGame.FromSnapshot(snapshot);
        if (restored is null)
        {
            SetNotice(error, "DangerBrush");
            return;
        }

        _game = restored;
        _waitingHostMove = false;
        _ = mySide;
        RefreshAll();
        TryStartAiTurn();
    }

    /// <inheritdoc />
    protected override void OnHostAction(string fromMemberId, string fromSide, string type, JsonElement? payload)
    {
        var side = GoGame.Parse(fromSide);
        if (side == GoCell.Empty) return;

        switch (type)
        {
            case "play":
            {
                if (payload is not { } p) return;
                var x = p.TryGetProperty("x", out var xv) ? xv.GetInt32() : -1;
                var y = p.TryGetProperty("y", out var yv) ? yv.GetInt32() : -1;
                if (_game.Play(side, x, y) != GoPlayResult.Ok) return;
                break;
            }
            case "pass":
                if (_game.Pass(side) != GoPlayResult.Ok) return;
                break;
            case "resign":
                _game.Resign(side);
                break;
            default:
                return;
        }

        _ = fromMemberId;
        BroadcastSnapshot();
        RefreshAll();
    }

    /// <inheritdoc />
    protected override bool ApplyUndoFor(string requesterSide)
    {
        _ = requesterSide;
        return _game.Undo();
    }

    /// <inheritdoc />
    protected override void ApplyDraw() => _game.Score();

    /// <inheritdoc />
    protected override void ApplyResign(string side)
    {
        var cell = GoGame.Parse(side);
        if (cell != GoCell.Empty) _game.Resign(cell);
    }

    /// <inheritdoc />
    protected override void BeginNewGame(bool swapSides)
    {
        _game = new GoGame(Cfg.BoardSize, Cfg.Komi) { Started = true };
        _waitingHostMove = false;
        RefreshAll();
        AnnounceLocalStart();
    }

    /// <summary>同机 / 离线开新局。</summary>
    private void NewLocalGame()
    {
        _game = new GoGame(Cfg.BoardSize, Cfg.Komi) { Started = true };
        _waitingHostMove = false;
        RefreshAll();
        AnnounceLocalStart();
        TryStartAiTurn();
    }

    private void AnnounceLocalStart()
    {
        var text = _aiEnabled ? "新开一局：你执黑先手，电脑执白" : "新开一局：黑棋先手，同机双人轮流下";
        SetNotice(text, "SuccessBrush");
    }

    /// <inheritdoc />
    protected override void OnLocalModeStarted()
    {
        _game = new GoGame(Cfg.BoardSize, Cfg.Komi) { Started = true };
        _waitingHostMove = false;
        RefreshAll();
        AnnounceLocalStart();
        TryStartAiTurn();
    }

    /// <inheritdoc />
    protected override void OnSeatsChanged() => RefreshAll();

    /// <inheritdoc />
    protected override void OnMembersChanged() => RefreshAll();

    /// <inheritdoc />
    protected override void OnReconnected() => RefreshAll();

    /// <inheritdoc />
    protected override void OnModeChanged() => RefreshAll();

    /// <inheritdoc />
    protected override void OnSnapshotApplied() => RefreshAll();

    /// <inheritdoc />
    protected override void OnHostRoomOpened()
    {
        RefreshAll();
        AppendSystemChat("房间已开：点「房间成员」里的席位下拉安排上场，选好点「开始对局」");
    }
}
