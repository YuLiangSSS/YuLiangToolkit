using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.BoardGames.Checkers.ViewModels;

/// <summary>
/// 跳棋的<b>联机部分</b>（partial 扩展）：房主权威，客户端只上报「坐哪个角 / 走哪一步」。
///
/// <para><b>为什么单独一个文件</b>：单机逻辑（选角、让棋、AI）已经很完整，
/// 联机只是在它外面加一层「谁的状态算数」——房主跑真引擎，客户端只按广播渲染。</para>
/// </summary>
public sealed partial class CheckersViewModel
{
    private CheckersNetSession? _session;
    private bool _netBusy;
    private bool _isOnline;
    private bool _isHostOnline;
    private string _netNotice = "";

    /// <summary>开房命令。</summary>
    public AsyncRelayCommand StartHostCommand { get; private set; } = null!;

    /// <summary>加入命令。</summary>
    public AsyncRelayCommand JoinRoomCommand { get; private set; } = null!;

    /// <summary>离开命令。</summary>
    public AsyncRelayCommand LeaveRoomCommand { get; private set; } = null!;

    /// <summary>联机中。</summary>
    public bool IsOnline => _isOnline;

    /// <summary>我是房主（联机时）。</summary>
    public bool IsHostOnline => _isHostOnline;

    /// <summary>能不能开 / 加入。</summary>
    public bool CanJoinNet => !_isOnline && !_netBusy;

    /// <summary>能不能离开。</summary>
    public bool CanLeaveNet => _isOnline;

    /// <summary>联机提示。</summary>
    public string NetNotice
    {
        get => _netNotice;
        private set => Set(ref _netNotice, value);
    }

    /// <summary>联机徽标。</summary>
    public string NetBadgeText => !_isOnline
        ? "未联机"
        : _isHostOnline ? $"房主 · {_session?.MemberCount ?? 0} 人" : "已连上房主";

    /// <summary>房主地址（界面显示，给朋友复制）。</summary>
    public string HostAddress => _session?.IsHost == true && _session.ListenPort > 0
        ? $"{Environment.MachineName}:{_session.ListenPort}"
        : "";

    /// <summary>初始化联机命令（主构造函数里调用）。</summary>
    private void InitializeNetCommands()
    {
        StartHostCommand = new AsyncRelayCommand(_ => StartHostAsync(), _ => CanJoinNet);
        JoinRoomCommand = new AsyncRelayCommand(_ => JoinRoomAsync(), _ => CanJoinNet);
        LeaveRoomCommand = new AsyncRelayCommand(_ => LeaveRoomAsync(), _ => CanLeaveNet);
    }

    private async Task StartHostAsync()
    {
        _netBusy = true;
        NotifyNet();
        try
        {
            var session = new CheckersNetSession(_myName);
            WireSession(session);

            var error = await session.StartHostAsync(Cfg.ListenPort);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _isOnline = true;
            _isHostOnline = true;
            _isHost = true;
            _myId = CheckersNetSession.HostMemberId;

            SetNotice($"房间已开（端口 {session.ListenPort}），等朋友加入", "SuccessBrush");
            BroadcastState();
        }
        finally
        {
            _netBusy = false;
            NotifyNet();
        }
    }

    private async Task JoinRoomAsync()
    {
        var address = (Cfg.LastJoinAddress ?? "").Trim();
        if (address.Length == 0) { SetNotice("请先填对方发来的地址", "DangerBrush"); return; }

        _netBusy = true;
        NotifyNet();
        try
        {
            var session = new CheckersNetSession(_myName);
            WireSession(session);

            var error = await session.ConnectAsync(address);
            if (error.Length > 0)
            {
                await session.DisposeAsync();
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _isOnline = true;
            _isHostOnline = false;
            _isHost = false;
            _myId = session.LocalMemberId;
            Cfg.LastJoinAddress = address;

            SetNotice("已连上房主，点角坐下", "SuccessBrush");
        }
        finally
        {
            _netBusy = false;
            NotifyNet();
        }
    }

    private async Task LeaveRoomAsync()
    {
        var session = _session;
        _session = null;
        _isOnline = false;
        _isHostOnline = false;

        if (session is not null)
        {
            try { await session.StopAsync(); } catch { /* 忽略 */ }
            try { await session.DisposeAsync(); } catch { /* 忽略 */ }
        }

        SetNotice("已离开房间", "TextSecondaryBrush");
        NotifyNet();
    }

    /// <summary>把会话事件接上。</summary>
    private void WireSession(CheckersNetSession session)
    {
        session.MembersChanged += () => Ui(NotifyNet);

        session.SitRequested += (memberId, point) => Ui(() =>
        {
            if (!_isHostOnline) return;

            // 房主替客户端处理选座（跟单机 ClickSeat 同一套规则，但用客户端的 id）
            var existing = _seatPlayers.FirstOrDefault(p => p.Home == point);
            if (existing is not null)
            {
                SendNetNotice($"{CheckersBoard.DisplayName(point)}角已经有人了");
                return;
            }

            var picked = _seatPlayers.Select(p => p.Home).ToList();
            if (!CheckersSeatRules.Remaining(_playerCount, picked).Contains(point))
            {
                SendNetNotice("这个位置不在本局组合里");
                return;
            }

            if (_seatPlayers.Count >= _playerCount)
            {
                SendNetNotice("座位已满");
                return;
            }

            _seatPlayers.Add(new CheckersPlayer { Id = memberId, Name = memberId, Home = point });
            RefreshSeats();
            BroadcastState();
        });

        session.LeaveSeatRequested += memberId => Ui(() =>
        {
            if (!_isHostOnline) return;
            var player = _seatPlayers.FirstOrDefault(p => p.Id == memberId);
            if (player is null) return;

            _seatPlayers.Remove(player);
            RefreshSeats();
            BroadcastState();
        });

        session.MoveRequested += (memberId, fromX, fromY, toX, toY) => Ui(() =>
        {
            if (!_isHostOnline || _game.IsOver) return;

            var error = _game.Move(memberId, fromX, fromY, toX, toY);
            if (error.Length > 0) return;

            LastText = _game.LastText;
            RefreshBoard();
            CheckOver();
            BroadcastState();
            TryAiTurn();
        });

        session.StateReceived += state => Ui(() => ApplyNetState(state));
    }

    /// <summary>房主：广播当前完整状态。</summary>
    private void BroadcastState()
    {
        if (!_isHostOnline || _session is null) return;
        _ = _session.BroadcastAsync(BuildNetState());
    }

    private CheckersNetState BuildNetState()
    {
        var seats = Enum.GetValues<CheckersPoint>().Select(point =>
        {
            var owner = _seatPlayers.FirstOrDefault(p => p.Home == point);
            return owner is null ? "" : $"{CheckersBoard.ToKey(point)}={owner.Id}|{owner.Name}";
        }).ToArray();

        var pieces = _game.Players
            .SelectMany(p => p.Pieces.Select(pos => $"{pos.X},{pos.Y},{p.Id}"))
            .ToArray();

        return new CheckersNetState
        {
            Seats = seats,
            PlayerCount = _playerCount,
            CurrentPlayerId = _game.CurrentPlayerId,
            Pieces = pieces,
            Started = _game.Players.Count > 0,
            IsOver = _game.IsOver,
            ResultText = _game.IsOver ? _game.ResultText : "",
            LastText = LastText,
            AllowSingleStep = _rules.AllowSingleStep,
            MustClearHomeFirst = _rules.MustClearHomeFirst,
        };
    }

    /// <summary>客户端：按房主广播渲染。</summary>
    private void ApplyNetState(CheckersNetState state)
    {
        if (_isHostOnline) return;

        _playerCount = CheckersSeatRules.IsSupported(state.PlayerCount) ? state.PlayerCount : 2;
        OnPropertyChanged(nameof(PlayerCount));
        OnPropertyChanged(nameof(PlayerCountText));

        // 座位
        _seatPlayers.Clear();
        foreach (var entry in state.Seats)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var parts = entry.Split('=', 2);
            if (parts.Length != 2) continue;

            var point = CheckersBoard.ParseKey(parts[0]);
            var who = parts[1].Split('|', 2);
            if (who[0].Length == 0) continue;

            _seatPlayers.Add(new CheckersPlayer
            {
                Id = who[0],
                Name = who.Length > 1 ? who[1] : who[0],
                Home = point,
            });
        }

        RefreshSeats();

        // 棋子
        Pieces.Clear();
        foreach (var entry in state.Pieces)
        {
            var parts = entry.Split(',');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) continue;

            var ownerId = parts[2];
            var index = _seatPlayers.FindIndex(p => p.Id == ownerId);
            var colorKey = index switch
            {
                1 => "b",
                2 => "c",
                3 => "d",
                4 => "e",
                5 => "f",
                _ => "a",
            };

            Pieces.Add(new CheckersPieceItem(x, y, ownerId, colorKey, ownerId == _myId, false));
        }

        LastText = state.LastText ?? "";
        ResultText = state.ResultText ?? "";
        StatusText = state.IsOver
            ? state.ResultText ?? "对局结束"
            : $"轮到 {(state.CurrentPlayerId == _myId ? "你" : state.CurrentPlayerId)}";

        OnPropertyChanged(nameof(TurnText));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(CanAutoMove));
        OnPropertyChanged(nameof(Pieces));
    }

    /// <summary>房主给某个客户端发提示（简化：直接改本地提示）。</summary>
    private void SendNetNotice(string text) => NetNotice = text;

    private void NotifyNet()
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(IsHostOnline));
        OnPropertyChanged(nameof(CanJoinNet));
        OnPropertyChanged(nameof(CanLeaveNet));
        OnPropertyChanged(nameof(NetBadgeText));
        OnPropertyChanged(nameof(HostAddress));
    }
}
