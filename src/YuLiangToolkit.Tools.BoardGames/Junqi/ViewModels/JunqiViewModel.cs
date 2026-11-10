using System.Collections.ObjectModel;
using System.Text.Json;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.BoardGames.Junqi;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Junqi.ViewModels;

/// <summary>棋盘上一格的界面数据。</summary>
public sealed class JunqiCellItem : ObservableObject
{
    private JunqiPiece _piece;
    private bool _isSelected;
    private bool _isTarget;

    /// <summary>创建格子。</summary>
    /// <param name="x">列。</param>
    /// <param name="y">行。</param>
    /// <param name="kind">格子类型。</param>
    public JunqiCellItem(int x, int y, JunqiCellKind kind)
    {
        X = x;
        Y = y;
        Kind = kind;
    }

    /// <summary>列。</summary>
    public int X { get; }

    /// <summary>行。</summary>
    public int Y { get; }

    /// <summary>格子类型。</summary>
    public JunqiCellKind Kind { get; }

    /// <summary>棋子。</summary>
    public JunqiPiece Piece
    {
        get => _piece;
        set
        {
            if (!Set(ref _piece, value)) return;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(IsHidden));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(IsRedSide));
            OnPropertyChanged(nameof(IsBlueSide));
        }
    }

    /// <summary>是不是选中了。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    /// <summary>是不是可以落子（高亮）。</summary>
    public bool IsTarget
    {
        get => _isTarget;
        set => Set(ref _isTarget, value);
    }

    /// <summary>是不是空的。</summary>
    public bool IsEmpty => Piece.IsEmpty;

    /// <summary>是不是背面朝上的暗棋。</summary>
    public bool IsHidden => Piece.IsUnknown;

    /// <summary>显示文字（暗棋显示「？」或空白）。</summary>
    public string Text => Piece.IsUnknown ? "暗" : Piece.IsEmpty ? "" : JunqiRanks.ShortName(Piece.Rank);

    /// <summary>是不是红方棋子。</summary>
    public bool IsRedSide => !Piece.IsEmpty && Piece.Side == JunqiSide.Red;

    /// <summary>是不是蓝方棋子。</summary>
    public bool IsBlueSide => !Piece.IsEmpty && Piece.Side == JunqiSide.Blue;

    /// <summary>格子类型说明（界面提示）。</summary>
    public string KindText => Kind switch
    {
        JunqiCellKind.Camp => "行营（安全格）",
        JunqiCellKind.Headquarters => "大本营",
        JunqiCellKind.Railway => "铁路",
        _ => "兵站",
    };
}

/// <summary>
/// 军棋 VM：布阵（自动 + 手动拖动）、走棋、暗棋裁判显示、联机房间。
/// </summary>
public sealed class JunqiViewModel : MultiSeatRoomViewModelBase
{
    private readonly JunqiGame _game = new();
    private JunqiSide _mySide = JunqiSide.Red;
    private int _mySeat = -1;
    private string _statusText = "点「自动布阵」快速摆好，然后点「开始对局」";
    private string _resultText = "";
    private (int X, int Y)? _selected;
    private JunqiVariant _variant = JunqiVariant.Duel;

    /// <summary>创建 VM。</summary>
    public JunqiViewModel() : base(JunqiConfig.ToolId, 2, new JunqiConfig())
    {
        Cfg = (JunqiConfig)RoomConfig;
        _variant = Cfg.VariantValue;

        Cells = new ObservableCollection<JunqiCellItem>();
        for (var y = 0; y < JunqiBoard.Height; y++)
        {
            for (var x = 0; x < JunqiBoard.Width; x++)
                Cells.Add(new JunqiCellItem(x, y, JunqiBoard.KindAt(x, y)));
        }

        AutoDeployCommand = new RelayCommand(_ => AutoDeploy());
        StartGameCommand = new RelayCommand(_ => StartGame());
        ResignCommand = new RelayCommand(_ => Resign());
        ResetCommand = new RelayCommand(_ => ResetGame());
        ClickCellCommand = new RelayCommand(p => ClickCell(p as JunqiCellItem));

        VariantOptions = OptionItem.From(
            ("duel", "二人军棋（陆战棋）"),
            ("fourKingdoms", "四国军棋（4 人两队）"));

        RefreshAll();

        // 演示 / 截图：YLT_JUNQI_DEMO=1 时自动布阵，截图里能看到棋子
        if (Environment.GetEnvironmentVariable("YLT_JUNQI_DEMO") == "1")
        {
            _game.AutoDeploy(JunqiSide.Red, new Random(7));
            _game.AutoDeploy(JunqiSide.Blue, new Random(9));
            RefreshAll();
            StatusText = "演示局面（双方已布阵）";
        }
    }

    /// <summary>配置。</summary>
    public JunqiConfig Cfg { get; }

    /// <summary>棋盘格子（12 × 5 = 60）。</summary>
    public ObservableCollection<JunqiCellItem> Cells { get; }

    /// <summary>玩法选项。</summary>
    public IReadOnlyList<OptionItem<string>> VariantOptions { get; }

    /// <summary>玩法选中项。</summary>
    public OptionItem<string> SelectedVariant
    {
        get => OptionItem<string>.Find(VariantOptions, JunqiConfig.VariantToKey(_variant)) ?? VariantOptions[0];
        set
        {
            if (value is null) return;
            _variant = value.Value == "fourKingdoms" ? JunqiVariant.FourKingdoms : JunqiVariant.Duel;
            Cfg.Variant = JunqiConfig.VariantToKey(_variant);
            OnPropertyChanged();
            OnPropertyChanged(nameof(VariantDescriptionText));
            OnPropertyChanged(nameof(IsFourKingdoms));
        }
    }

    /// <summary>玩法说明。</summary>
    public string VariantDescriptionText => JunqiConfig.VariantDescription(_variant);

    /// <summary>是不是四国军棋。</summary>
    public bool IsFourKingdoms => _variant == JunqiVariant.FourKingdoms;

    /// <summary>状态一句话。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>结果文案。</summary>
    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    /// <summary>本端执哪一方。</summary>
    public JunqiSide MySide => _mySide;

    /// <summary>轮到我走。</summary>
    public bool IsMyTurn => _game.Phase == JunqiPhase.Playing && _game.CurrentSide == _mySide;

    /// <summary>对局进行中。</summary>
    public bool IsPlaying => _game.Phase == JunqiPhase.Playing;

    /// <summary>结束了。</summary>
    public bool IsFinished => _game.Phase == JunqiPhase.Finished;

    /// <summary>步数文案。</summary>
    public string MoveCountText => $"已走 {_game.History.Count} 步";

    public RelayCommand AutoDeployCommand { get; }
    public RelayCommand StartGameCommand { get; }
    public RelayCommand ResignCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand ClickCellCommand { get; }

    // ---------------------------------------------------------------- 布阵 / 开局

    private void AutoDeploy()
    {
        if (IsClient) return;
        var errorRed = _game.IsDeployed(JunqiSide.Red) ? "" : _game.AutoDeploy(JunqiSide.Red, new Random());
        var errorBlue = _game.IsDeployed(JunqiSide.Blue) ? "" : _game.AutoDeploy(JunqiSide.Blue, new Random());
        if (errorRed.Length > 0 || errorBlue.Length > 0)
        {
            StatusText = errorRed.Length > 0 ? errorRed : errorBlue;
            return;
        }

        _gameStart();
        RefreshAll();
        BroadcastStateFromRoom();
    }

    private void _gameStart()
    {
        StatusText = "双方都已布阵，点「开始对局」（红方先走）";
        RefreshAll();
    }

    private void StartGame()
    {
        if (IsClient) return;
        if (!_game.IsDeployed(JunqiSide.Red) || !_game.IsDeployed(JunqiSide.Blue))
        {
            StatusText = "两边都要先布好阵（点「自动布阵」最快）";
            return;
        }

        StatusText = IsMyTurn ? "轮到你走" : "等待对方走棋";
        RefreshAll();
        BroadcastStateFromRoom();
    }

    private void Resign()
    {
        if (IsClient) { _ = Session?.SendRequestAsync("resign"); return; }
        _game.Resign(_mySide == JunqiSide.Red ? JunqiSide.Red : JunqiSide.Blue);
        AfterMove();
    }

    private void ResetGame()
    {
        if (IsClient) return;
        _game.Reset();
        StatusText = "已重开：点「自动布阵」再摆一次";
        ResultText = "";
        RefreshAll();
        BroadcastStateFromRoom();
    }

    private void ClickCell(JunqiCellItem? cell)
    {
        if (cell is null || _game.Phase != JunqiPhase.Playing) return;
        if (!IsMyTurn && !IsClient) return;

        if (_selected is { } from)
        {
            // 点自己另一个子：换选中
            if (!cell.Piece.IsEmpty && cell.Piece.Side == _mySide)
            {
                Select(cell);
                return;
            }

            // 走棋
            if (IsClient)
            {
                _ = Session?.SendActionAsync("move", new { fromX = from.X, fromY = from.Y, toX = cell.X, toY = cell.Y });
                ClearSelection();
                return;
            }

            var error = _game.Move(_mySide, from.X, from.Y, cell.X, cell.Y);
            if (error.Length > 0) { StatusText = error; return; }
            ClearSelection();
            AfterMove();
            return;
        }

        if (!cell.Piece.IsEmpty && cell.Piece.Side == _mySide) Select(cell);
    }

    private void Select(JunqiCellItem cell)
    {
        ClearSelection();
        _selected = (cell.X, cell.Y);
        cell.IsSelected = true;

        foreach (var target in _game.LegalMoves(cell.X, cell.Y))
        {
            var item = CellAt(target.X, target.Y);
            if (item is not null) item.IsTarget = true;
        }
        StatusText = $"已选中 {JunqiRanks.DisplayName(cell.Piece.Rank)}，点高亮位置走棋";
    }

    private void ClearSelection()
    {
        _selected = null;
        foreach (var cell in Cells)
        {
            cell.IsSelected = false;
            cell.IsTarget = false;
        }
    }

    private void AfterMove()
    {
        RefreshAll();
        if (_game.Phase == JunqiPhase.Finished)
        {
            ResultText = _game.ResultText;
            StatusText = _game.ResultText;
            var winners = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var seat = 0; seat < SeatCount; seat++)
            {
                var id = SeatMemberId(seat);
                if (id.Length == 0) continue;
                winners[id] = (seat == 0 && _game.Winner == JunqiSide.Red) || (seat == 1 && _game.Winner == JunqiSide.Blue);
            }
            ApplyRotationAfterGame(winners);
        }
        else
        {
            StatusText = IsMyTurn ? "轮到你走" : "等待对方走棋";
        }
        BroadcastStateFromRoom();
    }

    private JunqiCellItem? CellAt(int x, int y)
        => JunqiBoard.InBounds(x, y) ? Cells[JunqiBoard.IndexOf(x, y)] : null;

    // ---------------------------------------------------------------- 刷新

    private void RefreshAll()
    {
        for (var i = 0; i < Cells.Count && i < _game.Board.Count; i++)
            Cells[i].Piece = _game.Board[i];

        OnPropertyChanged(nameof(IsMyTurn));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(MoveCountText));
        OnPropertyChanged(nameof(MySide));
        OnPropertyChanged(nameof(SelectedVariant));
        OnPropertyChanged(nameof(IsFourKingdoms));
    }

    // ---------------------------------------------------------------- 房间基类钩子

    /// <inheritdoc />
    protected override string GameDisplayName => "军棋";

    /// <inheritdoc />
    protected override void UpdateGameSeats()
    {
        _mySeat = MySeatIndex;
        _mySide = _mySeat == 1 ? JunqiSide.Blue : JunqiSide.Red;
        RefreshAll();
    }

    /// <inheritdoc />
    protected override JsonElement BuildStateFor(string memberId)
    {
        var seat = -1;
        for (var i = 0; i < SeatCount; i++)
        {
            if (string.Equals(SeatMemberId(i), memberId, StringComparison.Ordinal)) { seat = i; break; }
        }

        // 每个人只看得到自己该看的棋盘（对手暗棋不可见）
        var side = seat == 1 ? JunqiSide.Blue : JunqiSide.Red;
        var visible = _game.VisibleBoardFor(side);

        var state = new JunqiNetState
        {
            Seq = NextRoomSeq(),
            ForMemberId = memberId,
            Seat = seat,
            MySide = side == JunqiSide.Red ? "red" : "blue",
            Phase = _game.Phase,
            CurrentSide = _game.CurrentSide == JunqiSide.Red ? "red" : "blue",
            Variant = JunqiConfig.VariantToKey(_variant),
            Cells = visible.Select(p => new JunqiNetPiece
            {
                Rank = (int)p.Rank,
                Side = p.Side == JunqiSide.Red ? "red" : "blue",
                Known = !p.IsUnknown,
            }).ToArray(),
            Winner = _game.Winner is { } w ? (w == JunqiSide.Red ? "red" : "blue") : "",
            ResultText = _game.ResultText,
            WaitingNames = WaitingMembers.Select(m => m.Name).ToArray(),
        };

        return ToJson(state);
    }

    /// <inheritdoc />
    protected override void ApplyState(JsonElement envelope)
    {
        var state = FromJson<JunqiNetState>(envelope);
        if (state is null) return;

        _mySeat = state.Seat;
        _mySide = state.MySide == "blue" ? JunqiSide.Blue : JunqiSide.Red;

        for (var i = 0; i < Cells.Count && i < state.Cells.Length; i++)
        {
            var net = state.Cells[i];
            Cells[i].Piece = net.Known
                ? new JunqiPiece((JunqiRank)net.Rank, net.Side == "red" ? JunqiSide.Red : JunqiSide.Blue, true)
                : JunqiPiece.Unknown(net.Side == "red" ? JunqiSide.Red : JunqiSide.Blue);
        }

        StatusText = state.Phase switch
        {
            JunqiPhase.Playing => state.CurrentSide == state.MySide ? "轮到你走" : "等待对方走棋",
            JunqiPhase.Finished => state.ResultText,
            _ => "等待布阵",
        };
        ResultText = state.Phase == JunqiPhase.Finished ? state.ResultText : "";
        RefreshAll();
    }

    /// <inheritdoc />
    protected override void HandleAction(string memberId, string type, JsonElement? payload)
    {
        if (type != "move" || payload is not { } p) return;

        var seat = -1;
        for (var i = 0; i < SeatCount; i++)
        {
            if (string.Equals(SeatMemberId(i), memberId, StringComparison.Ordinal)) { seat = i; break; }
        }
        if (seat < 0) return;

        var side = seat == 1 ? JunqiSide.Blue : JunqiSide.Red;
        var fromX = p.TryGetProperty("fromX", out var fx) ? fx.GetInt32() : -1;
        var fromY = p.TryGetProperty("fromY", out var fy) ? fy.GetInt32() : -1;
        var toX = p.TryGetProperty("toX", out var tx) ? tx.GetInt32() : -1;
        var toY = p.TryGetProperty("toY", out var ty) ? ty.GetInt32() : -1;

        _game.Move(side, fromX, fromY, toX, toY);
        RefreshAll();
        BroadcastStateFromRoom();
    }
}

/// <summary>军棋联机状态（房主 → 单个成员）。</summary>
public sealed class JunqiNetState
{
    public int Seq { get; set; }
    public string ForMemberId { get; set; } = "";
    public int Seat { get; set; } = -1;
    public string MySide { get; set; } = "red";
    public JunqiPhase Phase { get; set; }
    public string CurrentSide { get; set; } = "red";
    public string Variant { get; set; } = "duel";
    public JunqiNetPiece[] Cells { get; set; } = Array.Empty<JunqiNetPiece>();
    public string Winner { get; set; } = "";
    public string ResultText { get; set; } = "";
    public string[] WaitingNames { get; set; } = Array.Empty<string>();
}

/// <summary>一枚棋子在网络上的表示（<see cref="Known"/>=false 表示对方暗棋，只知阵营不知等级）。</summary>
public sealed class JunqiNetPiece
{
    public int Rank { get; set; }
    public string Side { get; set; } = "red";
    public bool Known { get; set; }
}
