using System.Collections.ObjectModel;
using System.Text.Json;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Ludo.ViewModels;

/// <summary>棋盘上一枚棋子的显示数据。</summary>
public sealed record LudoPieceItem(int X, int Y, string ColorKey, bool IsMine, bool CanMove, bool IsFinished)
{
    /// <summary>颜色中文名。</summary>
    public string ColorName => ColorKey switch
    {
        "yellow" => "黄",
        "blue" => "蓝",
        "green" => "绿",
        _ => "红",
    };
}

/// <summary>
/// 「飞行棋」界面 VM：座位自选（点颜色坐下 / 退出）、掷骰、走棋、规则设置、联机。
///
/// <para><b>座位</b>：谁进来谁点颜色坐下；点别人占着的位子会提示「已有人」；
/// 点自己的位子是「退出」，空出来给别人。房主负责广播座位表。</para>
/// </summary>
public sealed class LudoViewModel : ToolViewModelBase
{
    private readonly LudoRoom _room = new();
    private LudoGame _game;
    private LudoRules _rules = new();
    private readonly Random _random = new();

    private string _myId = "";
    private string _myName = "";
    private bool _isHost;
    private bool _rollPending;
    private string _statusText = "点右边的颜色坐下，两个颜色以上就能开局";
    private string _noticeText = "";
    private string _lastMoveText = "";
    private string _resultText = "";
    private int _lastDice;
    private LudoColor? _selectedPieceColor;

    // 联机
    private LudoNetSession? _session;
    private bool _netBusy;

    /// <summary>创建 VM。</summary>
    public LudoViewModel() : base(LudoConfig.ToolId)
    {
        Cfg = LoadConfig<LudoConfig>();
        _myName = Cfg.EffectivePlayerName;
        _myId = "local";
        _isHost = true;      // 单机默认自己当房主

        _rules = new LudoRules();
        Cfg.Rules.ApplyTo(_rules);
        _game = new LudoGame(_rules, _random);

        Seats = new ObservableCollection<LudoSeatInfo>();
        Pieces = new ObservableCollection<LudoPieceItem>();

        SeatCommands = LudoColors.All.ToDictionary(
            c => c,
            c => new RelayCommand(_ => ClickSeat(c)));

        RollCommand = new RelayCommand(_ => DoRoll(), _ => CanRoll);
        AutoMoveCommand = new RelayCommand(_ => DoAutoMove(), _ => CanAutoMove);
        NewGameCommand = new RelayCommand(_ => StartGame(), _ => CanStart);
        LeaveSeatCommand = new RelayCommand(_ => ClickSeat(MyColor ?? LudoColor.Red), _ => MyColor is not null);
        StartHostCommand = new AsyncRelayCommand(_ => StartHostAsync(), _ => CanJoinNet);
        JoinRoomCommand = new AsyncRelayCommand(_ => JoinRoomAsync(), _ => CanJoinNet);
        LeaveRoomCommand = new AsyncRelayCommand(_ => LeaveRoomAsync(), _ => CanLeaveNet);

        RefreshAll();
    }

    /// <summary>配置。</summary>
    public LudoConfig Cfg { get; }

    /// <summary>座位（界面绑）。</summary>
    public ObservableCollection<LudoSeatInfo> Seats { get; }

    /// <summary>棋子（界面绑）。</summary>
    public ObservableCollection<LudoPieceItem> Pieces { get; }

    /// <summary>四个颜色的座位命令（界面按钮绑）。</summary>
    public IReadOnlyDictionary<LudoColor, RelayCommand> SeatCommands { get; }

    /// <summary>红位。</summary>
    public RelayCommand SeatRedCommand => SeatCommands[LudoColor.Red];

    /// <summary>黄位。</summary>
    public RelayCommand SeatYellowCommand => SeatCommands[LudoColor.Yellow];

    /// <summary>蓝位。</summary>
    public RelayCommand SeatBlueCommand => SeatCommands[LudoColor.Blue];

    /// <summary>绿位。</summary>
    public RelayCommand SeatGreenCommand => SeatCommands[LudoColor.Green];

    /// <summary>掷骰。</summary>
    public RelayCommand RollCommand { get; }

    /// <summary>自动走一步（AI 代走 / 界面一键）。</summary>
    public RelayCommand AutoMoveCommand { get; }

    /// <summary>开新局。</summary>
    public RelayCommand NewGameCommand { get; }

    /// <summary>退出座位。</summary>
    public RelayCommand LeaveSeatCommand { get; }

    /// <summary>联机命令。</summary>
    public AsyncRelayCommand StartHostCommand { get; }
    public AsyncRelayCommand JoinRoomCommand { get; }
    public AsyncRelayCommand LeaveRoomCommand { get; }

    // ---------------------------------------------------------------- 状态

    /// <summary>我坐的颜色（没坐 = null）。</summary>
    public LudoColor? MyColor => _room.ColorOf(_myId);

    /// <summary>我是不是坐在座位上。</summary>
    public bool IsSeated => MyColor is not null;

    /// <summary>现在轮到谁。</summary>
    public LudoColor Turn => _game.Turn;

    /// <summary>轮到我了吗。</summary>
    public bool IsMyTurn => MyColor is { } c && c == _game.Turn;

    /// <summary>能不能掷骰。</summary>
    public bool CanRoll => IsMyTurn && !_rollPending && !_game.IsOver;

    /// <summary>能不能自动走。</summary>
    public bool CanAutoMove => IsMyTurn && _lastDice > 0 && !_game.IsOver;

    /// <summary>能不能开局（至少两个颜色有人）。</summary>
    public bool CanStart => _room.OccupiedCount >= 2 && _isHost && _session is null;

    /// <summary>骰子点数文字。</summary>
    public string DiceText => _lastDice > 0 ? $"{_lastDice}" : "—";

    /// <summary>轮次文字。</summary>
    public string TurnText => _game.IsOver ? "对局结束" : $"轮到 {LudoColors.DisplayName(_game.Turn)}";

    /// <summary>状态文字。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>提示文字。</summary>
    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    /// <summary>最近一步说明。</summary>
    public string LastMoveText
    {
        get => _lastMoveText;
        private set => Set(ref _lastMoveText, value);
    }

    /// <summary>结果文字。</summary>
    public string ResultText
    {
        get => _resultText;
        private set
        {
            if (!Set(ref _resultText, value)) return;
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>有没有结果。</summary>
    public bool HasResult => ResultText.Length > 0;

    /// <summary>规则：掷 6 才能起飞。</summary>
    public bool NeedSixToTakeOff
    {
        get => _rules.NeedSixToTakeOff;
        set { _rules.NeedSixToTakeOff = value; Cfg.ApplyRules(_rules); OnPropertyChanged(); }
    }

    /// <summary>规则：掷 5 也能起飞（后门）。</summary>
    public bool FiveAlsoTakesOff
    {
        get => _rules.FiveAlsoTakesOff;
        set { _rules.FiveAlsoTakesOff = value; Cfg.ApplyRules(_rules); OnPropertyChanged(); }
    }

    /// <summary>规则：掷 6 再掷一次。</summary>
    public bool ExtraRollOnSix
    {
        get => _rules.ExtraRollOnSix;
        set { _rules.ExtraRollOnSix = value; Cfg.ApplyRules(_rules); OnPropertyChanged(); }
    }

    /// <summary>规则：精确到终点。</summary>
    public bool ExactToFinish
    {
        get => _rules.ExactToFinish;
        set { _rules.ExactToFinish = value; Cfg.ApplyRules(_rules); OnPropertyChanged(); }
    }

    /// <summary>规则：踩子踢回家。</summary>
    public bool CaptureOnLand
    {
        get => _rules.CaptureOnLand;
        set { _rules.CaptureOnLand = value; Cfg.ApplyRules(_rules); OnPropertyChanged(); }
    }

    /// <summary>规则：飞行线踢子。</summary>
    public bool CaptureOnFlightLine
    {
        get => _rules.CaptureOnFlightLine;
        set { _rules.CaptureOnFlightLine = value; Cfg.ApplyRules(_rules); OnPropertyChanged(); }
    }

    // ---------------------------------------------------------------- 座位

    /// <summary>点某个颜色位：空位就坐下，自己的位子就退出。</summary>
    private void ClickSeat(LudoColor color)
    {
        var seat = _room.SeatOf(color);

        if (seat.IsOccupied && seat.MemberId == _myId)
        {
            // 退出自己的位置
            _room.Leave(_myId);
            SetNotice($"你退出了 {LudoColors.DisplayName(color)}位", "TextSecondaryBrush");
            AfterSeatChanged();
            return;
        }

        if (seat.IsOccupied)
        {
            SetNotice($"{LudoColors.DisplayName(color)}位已经有人了（{seat.Name}）", "DangerBrush");
            return;
        }

        var error = _room.Sit(color, _myId, _myName);
        if (error.Length > 0) { SetNotice(error, "DangerBrush"); return; }

        SetNotice($"你坐到了 {LudoColors.DisplayName(color)}位", "SuccessBrush");
        AfterSeatChanged();
    }

    private void AfterSeatChanged()
    {
        RefreshSeats();
        OnPropertyChanged(nameof(MyColor));
        OnPropertyChanged(nameof(IsSeated));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanRoll));
        OnPropertyChanged(nameof(IsMyTurn));
        BroadcastIfHost();
    }

    /// <summary>刷新座位列表。</summary>
    private void RefreshSeats()
    {
        Seats.Clear();
        foreach (var seat in _room.Seats)
        {
            Seats.Add(new LudoSeatInfo(
                seat.Color,
                LudoColors.DisplayName(seat.Color),
                seat.IsOccupied ? seat.Name : "空位",
                seat.IsOccupied,
                seat.MemberId == _myId,
                LudoColors.ToKey(seat.Color)));
        }
    }

    // ---------------------------------------------------------------- 对局

    /// <summary>开新局（房主 / 单机）。</summary>
    private void StartGame()
    {
        var seated = _room.Seats.Where(s => s.IsOccupied).Select(s => s.Color).ToList();
        if (seated.Count < 2) { SetNotice("至少两个颜色有人才能开局", "DangerBrush"); return; }

        _game = new LudoGame(_rules, _random);
        var error = _game.Setup(seated);
        if (error.Length > 0) { SetNotice(error, "DangerBrush"); return; }

        _lastDice = 0;
        ResultText = "";
        LastMoveText = "";
        StatusText = $"开局：{string.Join("、", seated.Select(LudoColors.DisplayName))}";
        RefreshBoard();
        BroadcastIfHost();
    }

    private void DoRoll()
    {
        if (!CanRoll) return;

        if (_session is not null && !_isHost)
        {
            _ = _session.SendRollAsync();
            _rollPending = true;
            NotifyActions();
            return;
        }

        var dice = _game.Roll(_game.Turn);
        if (dice == 0) return;

        _lastDice = dice;
        LastMoveText = $"{LudoColors.DisplayName(_game.Turn)} 掷出 {dice}";
        NotifyActions();
        RefreshBoard();
        BroadcastIfHost();
    }

    /// <summary>掷完之后自动走一步（AI 选择：优先能踢人的）。</summary>
    private void DoAutoMove()
    {
        if (!CanAutoMove) return;

        if (_session is not null && !_isHost)
        {
            _ = _session.SendMoveAsync(-1);      // -1 = 让房主替他挑
            return;
        }

        var color = _game.Turn;
        var piece = _game.PickPiece(color, _lastDice);
        if (piece is null)
        {
            SetNotice("这个点数没有能走的棋子", "DangerBrush");
            _lastDice = 0;
            NotifyActions();
            BroadcastIfHost();
            return;
        }

        var result = _game.Move(color, piece.Index, _lastDice);
        LastMoveText = result.Text;
        _lastDice = 0;

        if (result.Captured.Count > 0)
            SetNotice($"踢回家 {result.Captured.Count} 枚！", "SuccessBrush");

        if (_game.IsOver)
        {
            ResultText = _game.ResultText;
            StatusText = _game.ResultText;
        }

        RefreshBoard();
        NotifyActions();
        BroadcastIfHost();
    }

    /// <summary>点某枚棋子走棋。</summary>
    public void MovePiece(string colorKey, int pieceIndex)
    {
        if (_lastDice <= 0) return;
        var color = LudoColors.Parse(colorKey);
        if (color != _game.Turn) return;

        if (_session is not null && !_isHost)
        {
            _ = _session.SendMoveAsync(pieceIndex);
            return;
        }

        var result = _game.Move(color, pieceIndex, _lastDice);
        if (!result.Success) { SetNotice(result.Text, "DangerBrush"); return; }

        LastMoveText = result.Text;
        _lastDice = 0;
        RefreshBoard();
        NotifyActions();
        BroadcastIfHost();
    }

    // ---------------------------------------------------------------- 刷新

    private void RefreshBoard()
    {
        Pieces.Clear();

        foreach (var piece in _game.Pieces)
        {
            var cell = piece.Cell();
            var mine = MyColor is { } c && c == piece.Color;
            var canMove = mine && _lastDice > 0 && _game.Turn == piece.Color;
            Pieces.Add(new LudoPieceItem(
                cell.X, cell.Y, LudoColors.ToKey(piece.Color), mine, canMove,
                piece.State == LudoPieceState.Finished));
        }

        OnPropertyChanged(nameof(Turn));
        OnPropertyChanged(nameof(IsMyTurn));
        OnPropertyChanged(nameof(DiceText));
        OnPropertyChanged(nameof(TurnText));
        NotifyActions();
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanRoll));
        OnPropertyChanged(nameof(CanAutoMove));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(IsSeated));
        OnPropertyChanged(nameof(MyColor));
    }

    private void RefreshAll()
    {
        RefreshSeats();
        RefreshBoard();
        NotifyActions();
    }

    private void SetNotice(string text, string brush)
    {
        NoticeText = text;
        _ = brush;
    }

    // ---------------------------------------------------------------- 联机

    /// <summary>是不是房主。</summary>
    public bool IsHost => _isHost;

    /// <summary>能不能开 / 加入房间。</summary>
    public bool CanJoinNet => _session is null && !_netBusy;

    /// <summary>能不能离开。</summary>
    public bool CanLeaveNet => _session is not null;

    /// <summary>联机徽标。</summary>
    public string NetBadgeText => _session is null ? "未联机"
        : _isHost ? $"房主 · {_room.OccupiedCount} 人入座" : $"已连上房主 · {_room.OccupiedCount} 人入座";

    /// <summary>开房。</summary>
    private async Task StartHostAsync()
    {
        _netBusy = true;
        NotifyNet();
        try
        {
            var session = new LudoNetSession(_myName);
            WireSession(session);

            var error = await session.StartHostAsync(Cfg.ListenPort);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _isHost = true;
            _myId = LudoNetSession.HostMemberId;
            SetNotice($"房间已开（端口 {session.ListenPort}），把地址发给朋友", "SuccessBrush");
            BroadcastIfHost();
        }
        finally
        {
            _netBusy = false;
            NotifyNet();
        }
    }

    /// <summary>加入房间。</summary>
    private async Task JoinRoomAsync()
    {
        var address = (Cfg.LastJoinAddress ?? "").Trim();
        if (address.Length == 0) { SetNotice("请先填对方发来的地址", "DangerBrush"); return; }

        _netBusy = true;
        NotifyNet();
        try
        {
            var session = new LudoNetSession(_myName);
            WireSession(session);

            var error = await session.ConnectAsync(address);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _isHost = false;
            _myId = session.LocalMemberId;
            Cfg.LastJoinAddress = address;
            SetNotice("已连上房主，点颜色坐下", "SuccessBrush");
        }
        finally
        {
            _netBusy = false;
            NotifyNet();
        }
    }

    /// <summary>离开房间。</summary>
    private async Task LeaveRoomAsync()
    {
        var session = _session;
        _session = null;
        _isHost = true;
        _myId = "local";
        _room.Leave(_myId);

        if (session is not null)
        {
            try { await session.StopAsync(); } catch { /* 忽略 */ }
            try { await session.DisposeAsync(); } catch { /* 忽略 */ }
        }

        SetNotice("已离开房间", "TextSecondaryBrush");
        RefreshAll();
        NotifyNet();
    }

    private void WireSession(LudoNetSession session)
    {
        session.StateReceived += state => Ui(() => ApplyRemoteState(state));
        session.RollReceived += () => Ui(() =>
        {
            if (!_isHost) return;
            var dice = _game.Roll(_game.Turn);
            if (dice > 0) _lastDice = dice;
            LastMoveText = $"{LudoColors.DisplayName(_game.Turn)} 掷出 {dice}";
            RefreshBoard();
            BroadcastIfHost();
        });
        session.MoveReceived += pieceIndex => Ui(() =>
        {
            if (!_isHost) return;
            var color = _game.Turn;
            var piece = pieceIndex >= 0
                ? _game.PiecesOf(color).FirstOrDefault(p => p.Index == pieceIndex)
                : _game.PickPiece(color, _lastDice);

            if (piece is null) return;
            var result = _game.Move(color, piece.Index, _lastDice);
            LastMoveText = result.Text;
            _lastDice = 0;
            if (_game.IsOver) ResultText = _game.ResultText;
            RefreshBoard();
            BroadcastIfHost();
        });
    }

    private void ApplyRemoteState(LudoNetState state)
    {
        if (_isHost) return;

        _room.Import(state.Seats);
        LudoRulesState rulesState = state.Rules ?? new LudoRulesState();
        rulesState.ApplyTo(_rules);

        if (state.Pieces is { } pieces && pieces.Count > 0)
        {
            _game = new LudoGame(_rules, _random);
            var colors = _room.Seats.Where(s => s.IsOccupied).Select(s => s.Color).ToList();
            if (colors.Count >= 2) _game.Setup(colors);

            var myColor = MyColor;
            _lastDice = state.Dice;
            ResultText = state.ResultText ?? "";
        }

        // 棋子位置由房主直接下发
        Pieces.Clear();
        foreach (var item in state.Pieces ?? new List<LudoPieceWire>())
        {
            var mine = MyColor is { } c && LudoColors.ToKey(c) == item.Color;
            Pieces.Add(new LudoPieceItem(item.X, item.Y, item.Color, mine, false, item.Finished));
        }

        LastMoveText = state.LastText ?? "";
        _lastDice = state.Dice;

        RefreshSeats();
        NotifyActions();
        NotifyNet();
    }

    private void BroadcastIfHost()
    {
        if (!_isHost || _session is null) return;

        var state = new LudoNetState
        {
            Seats = _room.Export(),
            Rules = LudoRulesState.From(_rules),
            Dice = _lastDice,
            LastText = LastMoveText,
            ResultText = ResultText,
            Pieces = _game.Pieces.Select(p =>
            {
                var cell = p.Cell();
                return new LudoPieceWire
                {
                    X = cell.X,
                    Y = cell.Y,
                    Color = LudoColors.ToKey(p.Color),
                    Finished = p.State == LudoPieceState.Finished,
                };
            }).ToList(),
        };

        _ = _session.BroadcastAsync(state);
    }

    private void NotifyNet()
    {
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(CanJoinNet));
        OnPropertyChanged(nameof(CanLeaveNet));
        OnPropertyChanged(nameof(NetBadgeText));
        OnPropertyChanged(nameof(CanStart));
    }
}

/// <summary>界面上的一个座位。</summary>
public sealed record LudoSeatInfo(
    LudoColor Color, string ColorName, string OccupantName, bool IsOccupied, bool IsMe, string ColorKey)
{
    /// <summary>按钮文字。</summary>
    public string DisplayText => $"{ColorName}：{OccupantName}";

    /// <summary>「你」标记。</summary>
    public string MyMark => IsMe ? "（你）" : "";
}
