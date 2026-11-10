using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.BoardGames.Shared;
using YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>房间里的一个成员（界面用）。</summary>
/// <param name="MemberId">成员 id。</param>
/// <param name="Name">名字。</param>
/// <param name="IsHost">是不是房主。</param>
/// <param name="IsOnline">在不在线。</param>
/// <param name="GamesPlayed">打过几局。</param>
/// <param name="SeatIndex">坐在第几个席位（-1 = 在场下排队）。</param>
public sealed record MultiSeatMember(
    string MemberId,
    string Name,
    bool IsHost,
    bool IsOnline,
    int GamesPlayed,
    int SeatIndex)
{
    /// <summary>是不是在场上。</summary>
    public bool IsSeated => SeatIndex >= 0;

    /// <summary>状态文字。</summary>
    public string StatusText => IsSeated ? $"第 {SeatIndex + 1} 席" : "排队中";

    /// <summary>显示名（含房主 / 自己标记）。</summary>
    public string DisplayText => Name + (IsHost ? "（房主）" : "");
}

/// <summary>
/// <b>多席位（3~4 人）的房间基座</b>：斗地主（3 席）与军棋（2 / 4 席）共用。
///
/// <para><b>为什么不复用 <see cref="BoardGameRoomViewModelBase"/></b>：那个基座写死了「两个席位 +
/// 两个执边 + 胜负边 + 棋盘轮换」，而斗地主是 3 家、四国军棋要 4 家，硬塞进去要改几千行还容易弄坏
/// 五子棋 / 象棋。这里只保留多席位真正需要的东西，<b>复用 Core 的传输层</b>（<see cref="BoardRoomSession"/>
/// 本来就支持最多 16 人）与 <see cref="BoardMultiSeatRotation"/> 的换人规则。</para>
///
/// <para><b>权威端</b>：房主跑真正的游戏引擎；客户端只上报动作、按快照渲染。
/// 快照<b>按成员分别发送</b>（<see cref="BoardRoomSession.SendStatePerMemberAsync"/>），
/// 所以每个人的手牌 / 暗棋只有自己看得到。</para>
/// </summary>
public abstract class MultiSeatRoomViewModelBase : ToolViewModelBase
{
    private readonly object _roomGate = new();
    private readonly Dictionary<string, MultiSeatMember> _members = new(StringComparer.Ordinal);
    private readonly List<string> _seatMemberIds = new();

    private BoardRoomSession? _session;
    private BoardPlayMode _mode = BoardPlayMode.Offline;
    private bool _isRoomBusy;
    private string _notice = "还没进房间：开个房间等别人进来，或者填地址加入别人的房间";
    private string _noticeBrushKey = "TextSecondaryBrush";
    private string _chatText = "";
    private int _roomSeq;
    private string _localMemberId = "";

    /// <summary>创建房间基座。</summary>
    /// <param name="toolId">工具 Id（Data / Config 目录名）。</param>
    /// <param name="seatCount">席位数（斗地主 3、二人军棋 2、四国军棋 4）。</param>
    /// <param name="roomConfig">房间配置（提供玩家名 / 端口 / 上次地址 / 换人方式）。</param>
    protected MultiSeatRoomViewModelBase(string toolId, int seatCount, IBoardGameRoomConfig roomConfig)
        : base(toolId)
    {
        SeatCount = Math.Clamp(seatCount, 2, 6);
        RoomConfig = roomConfig;

        StartHostCommand = new AsyncRelayCommand(_ => StartHostAsync(), _ => CanJoin);
        JoinRoomCommand = new AsyncRelayCommand(_ => JoinRoomAsync(), _ => CanJoin);
        LeaveRoomCommand = new AsyncRelayCommand(_ => LeaveRoomAsync(), _ => CanLeave);
        SendChatCommand = new RelayCommand(_ => SendChat(), _ => CanSendChat);
        CopyTextCommand = new RelayCommand(p =>
        {
            if (p is not string text || text.Length == 0) return;
            try { Clipboard.SetText(text); SetNotice($"已复制：{text}", "SuccessBrush"); }
            catch { SetNotice("复制失败（剪贴板被占用）", "DangerBrush"); }
        });

        for (var i = 0; i < SeatCount; i++) _seatMemberIds.Add("");
        _autoRotateMode = BoardAutoModes.ParseRotate(RoomConfig.AutoRotateMode);
        _autoPickMode = BoardAutoModes.ParsePick(RoomConfig.AutoPickMode);
    }

    // ---------------------------------------------------------------- 基本状态

    /// <summary>席位数。</summary>
    public int SeatCount { get; }

    /// <summary>房间配置。</summary>
    protected IBoardGameRoomConfig RoomConfig { get; }

    /// <summary>本端玩家名。</summary>
    public string PlayerName
    {
        get => RoomConfig.PlayerName;
        set { RoomConfig.PlayerName = value; OnPropertyChanged(); }
    }

    /// <summary>加入房间的地址。</summary>
    public string JoinAddress
    {
        get => RoomConfig.LastJoinAddress;
        set { RoomConfig.LastJoinAddress = value; OnPropertyChanged(); }
    }

    /// <summary>开房端口（文本）。</summary>
    public string ListenPortText
    {
        get => RoomConfig.ListenPort.ToString();
        set
        {
            RoomConfig.ListenPort = int.TryParse((value ?? "").Trim(), out var port) ? Math.Clamp(port, 0, 65535) : 0;
            OnPropertyChanged();
        }
    }

    /// <summary>当前玩法模式（离线 / 主机 / 客户端）。</summary>
    public BoardPlayMode Mode => _mode;

    /// <summary>是不是房主。</summary>
    public bool IsHost => _mode == BoardPlayMode.Host;

    /// <summary>是不是客户端。</summary>
    public bool IsClient => _mode == BoardPlayMode.Client;

    /// <summary>房间在线（开房中 / 已连上）。</summary>
    public bool IsOnline => _session?.IsOnline == true;

    /// <summary>还没进房间（显示开房 / 加入控件）。</summary>
    public bool CanJoin => !IsOnline && !_isRoomBusy;

    /// <summary>能离开房间。</summary>
    public bool CanLeave => IsOnline || _isRoomBusy;

    /// <summary>房间操作进行中。</summary>
    public bool IsRoomBusy => _isRoomBusy;

    /// <summary>状态徽标。</summary>
    public string ModeBadgeText => _isRoomBusy ? "连接中…"
        : !IsOnline ? "未联机"
        : IsHost ? $"房主 · {Members.Count}/{BoardRoomSession.MaxMembers} 人"
        : $"已连上房主 · {Members.Count}/{BoardRoomSession.MaxMembers} 人";

    /// <summary>徽标颜色。</summary>
    public string ModeBadgeBrushKey => IsOnline ? "SuccessBrush" : "TextSecondaryBrush";

    /// <summary>状态提示。</summary>
    public string NoticeText => _notice;

    /// <summary>状态提示颜色。</summary>
    public string NoticeBrushKey => _noticeBrushKey;

    /// <summary>本端成员 id。</summary>
    public string LocalMemberId => IsHost ? BoardRoomSession.HostMemberId : _localMemberId;

    /// <summary>本端名字。</summary>
    public string LocalName
    {
        get
        {
            var name = (RoomConfig.PlayerName ?? "").Trim();
            if (name.Length > 0) return name;
            return Environment.UserName is { Length: > 0 } userName ? userName : "玩家";
        }
    }

    // ---------------------------------------------------------------- 命令

    /// <summary>开房间。</summary>
    public AsyncRelayCommand StartHostCommand { get; }

    /// <summary>加入房间。</summary>
    public AsyncRelayCommand JoinRoomCommand { get; }

    /// <summary>离开房间。</summary>
    public AsyncRelayCommand LeaveRoomCommand { get; }

    /// <summary>发聊天。</summary>
    public RelayCommand SendChatCommand { get; }

    /// <summary>复制文本（分享地址用）。</summary>
    public RelayCommand CopyTextCommand { get; }

    /// <summary>房间成员（界面直接绑）。</summary>
    public ObservableCollection<MultiSeatMember> Members { get; } = new();

    /// <summary>本机可以告诉对方的地址。</summary>
    public ObservableCollection<string> ShareAddresses { get; } = new();

    /// <summary>有没有可复制的地址。</summary>
    public bool HasShareAddresses => ShareAddresses.Count > 0;

    /// <summary>聊天 / 系统提示。</summary>
    public ObservableCollection<string> ChatItems { get; } = new();

    /// <summary>聊天输入。</summary>
    public string ChatText
    {
        get => _chatText;
        set { if (Set(ref _chatText, value ?? "")) OnPropertyChanged(nameof(CanSendChat)); }
    }

    /// <summary>能不能发聊天。</summary>
    public bool CanSendChat => IsOnline && ChatText.Trim().Length > 0;

    // ---------------------------------------------------------------- 换人设置（房主可改）

    private BoardAutoRotateMode _autoRotateMode;
    private BoardAutoPickMode _autoPickMode;

    /// <summary>换人方式（手动 / 输的下场 / 全场换人）。</summary>
    public string AutoRotateModeKey
    {
        get => BoardAutoModes.ToKey(_autoRotateMode);
        set
        {
            var parsed = BoardAutoModes.ParseRotate(value);
            if (parsed == _autoRotateMode) return;
            _autoRotateMode = parsed;
            RoomConfig.AutoRotateMode = BoardAutoModes.ToKey(parsed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoRotateHintText));
            if (IsHost) BroadcastStateFromRoom();
        }
    }

    /// <summary>补位方式。</summary>
    public string AutoPickModeKey
    {
        get => BoardAutoModes.ToKey(_autoPickMode);
        set
        {
            var parsed = BoardAutoModes.ParsePick(value);
            if (parsed == _autoPickMode) return;
            _autoPickMode = parsed;
            RoomConfig.AutoPickMode = BoardAutoModes.ToKey(parsed);
            OnPropertyChanged();
            if (IsHost) BroadcastStateFromRoom();
        }
    }

    /// <summary>换人方式说明。</summary>
    public string AutoRotateHintText => _autoRotateMode switch
    {
        BoardAutoRotateMode.LoserOut => "输的人下场，换排队中的下一位上场",
        BoardAutoRotateMode.BothOut => "无论输赢，场上的人全下场，换排队中的人上场",
        _ => "手动：一局结束后房主自己指定谁上场",
    };

    /// <summary>换人方式下拉选项。</summary>
    public IReadOnlyList<OptionItem<string>> AutoRotateOptions { get; } = OptionItem.From(
        ("manual", "手动安排（房主指定）"),
        ("loserOut", "输的人下场，换下一位"),
        ("bothOut", "全场换人（不论输赢）"));

    /// <summary>补位方式选项。</summary>
    public IReadOnlyList<OptionItem<string>> AutoPickOptions { get; } = OptionItem.From(
        ("rotation", "轮流上场（没上过场的优先）"),
        ("random", "随机上场"));

    /// <summary>换人方式选中项（下拉框绑定）。</summary>
    public OptionItem<string> SelectedAutoRotate
    {
        get => OptionItem<string>.Find(AutoRotateOptions, AutoRotateModeKey) ?? AutoRotateOptions[0];
        set { if (value is not null) AutoRotateModeKey = value.Value; }
    }

    /// <summary>补位方式选中项。</summary>
    public OptionItem<string> SelectedAutoPick
    {
        get => OptionItem<string>.Find(AutoPickOptions, AutoPickModeKey) ?? AutoPickOptions[0];
        set { if (value is not null) AutoPickModeKey = value.Value; }
    }

    // ---------------------------------------------------------------- 席位

    /// <summary>某席位上的成员 id（空 = 空位）。</summary>
    public string SeatMemberId(int seat)
        => seat >= 0 && seat < _seatMemberIds.Count ? _seatMemberIds[seat] : "";

    /// <summary>某席位上的名字。</summary>
    public string SeatName(int seat)
    {
        var id = SeatMemberId(seat);
        if (id.Length == 0) return "空位";
        return _members.TryGetValue(id, out var member) ? member.Name : id;
    }

    /// <summary>本端坐在第几席（-1 = 不在场上）。</summary>
    public int MySeatIndex => _seatMemberIds.IndexOf(LocalMemberId);

    /// <summary>本端在不在场上。</summary>
    public bool IsSeated => MySeatIndex >= 0;

    /// <summary>场下排队的成员。</summary>
    public IReadOnlyList<MultiSeatMember> WaitingMembers
        => Members.Where(m => !m.IsSeated && m.IsOnline).ToList();

    /// <summary>席位汇总文案。</summary>
    public string SeatSummaryText
    {
        get
        {
            var names = new List<string>();
            for (var i = 0; i < SeatCount; i++) names.Add($"{i + 1}号位：{SeatName(i)}");
            var waiting = WaitingMembers.Count;
            return string.Join(" · ", names) + (waiting > 0 ? $"　（{waiting} 人排队中）" : "");
        }
    }

    // ---------------------------------------------------------------- 子类钩子

    /// <summary>游戏名（提示用）。</summary>
    protected abstract string GameDisplayName { get; }

    /// <summary>
    /// 房主：生成「这个成员的完整状态包装」（客户端只能看到自己该看的）。
    /// 在 <b>UI 线程</b>调用，可以直接读游戏对象。
    /// </summary>
    protected abstract JsonElement BuildStateFor(string memberId);

    /// <summary>
    /// 客户端：收到房主发给自己的状态。
    /// </summary>
    protected abstract void ApplyState(JsonElement envelope);

    /// <summary>房主：收到某个成员的动作。</summary>
    protected abstract void HandleAction(string memberId, string type, JsonElement? payload);

    /// <summary>席位 / 成员变了（UI 线程）。</summary>
    protected virtual void OnRoomChanged() { }

    /// <summary>本端身份确定后（房主开房成功 / 客户端握手完成）。</summary>
    protected virtual void OnEnteredRoom() { }

    /// <summary>对端全部离开（房主视角：没人了）。</summary>
    protected virtual void OnEveryoneLeft() { }

    // ---------------------------------------------------------------- 开房 / 加入 / 离开

    private async Task StartHostAsync()
    {
        if (IsOnline) return;
        _isRoomBusy = true;
        NotifyRoomState();
        try
        {
            var session = CreateSession(LocalName);
            WireSession(session);

            // stateFactory 在客户端握手时（网络线程）被调用：这里生成「新成员能看到的初始状态」
            var error = await session.StartHostAsync(
                RoomConfig.ListenPort,
                () => BuildStateFor(BoardRoomSession.HostMemberId == null ? "" : "")).ConfigureAwait(true);

            if (error.Length > 0)
            {
                try { await session.DisposeAsync(); } catch { /* 忽略 */ }
                SetNotice($"开房间失败：{error}", "DangerBrush");
                return;
            }

            _session = session;
            _mode = BoardPlayMode.Host;

            lock (_roomGate)
            {
                _members.Clear();
                _members[BoardRoomSession.HostMemberId] =
                    new MultiSeatMember(BoardRoomSession.HostMemberId, LocalName, true, true, 0, 0);
                for (var i = 0; i < _seatMemberIds.Count; i++) _seatMemberIds[i] = "";
                _seatMemberIds[0] = BoardRoomSession.HostMemberId;
            }

            ShareAddresses.Clear();
            foreach (var address in session.ShareAddresses.Take(4))
                ShareAddresses.Add(RoomProtocol.WithPort(address.Text, session.ListenPort));

            SyncMembers();
            SetNotice($"房间已开（端口 {session.ListenPort}），把地址发给朋友", "SuccessBrush");
            AppendChat($"房间已开：{LocalName}（房主）");
            NotifyRoomState();
            OnEnteredRoom();
            BroadcastStateFromRoom();
        }
        finally
        {
            _isRoomBusy = false;
            NotifyRoomState();
        }
    }

    private async Task JoinRoomAsync()
    {
        var address = (JoinAddress ?? "").Trim();
        if (address.Length == 0)
        {
            SetNotice("请先填对方发来的地址", "DangerBrush");
            return;
        }

        _isRoomBusy = true;
        NotifyRoomState();
        try
        {
            var session = CreateSession(LocalName);
            WireSession(session);
            var error = await session.ConnectAsync(address).ConfigureAwait(true);
            if (error.Length > 0)
            {
                try { await session.DisposeAsync(); } catch { /* 忽略 */ }
                SetNotice(error, "DangerBrush");
                return;
            }

            _session = session;
            _mode = BoardPlayMode.Client;
            _localMemberId = session.LocalMemberId;
            RoomConfig.LastJoinAddress = address;
            SetNotice("已连上房主", "SuccessBrush");
            NotifyRoomState();
            OnEnteredRoom();
        }
        finally
        {
            _isRoomBusy = false;
            NotifyRoomState();
        }
    }

    private async Task LeaveRoomAsync()
    {
        var session = _session;
        _session = null;
        _mode = BoardPlayMode.Offline;
        _localMemberId = "";

        if (session is not null)
        {
            try { await session.StopAsync("离开房间"); } catch { /* 忽略 */ }
            try { await session.DisposeAsync(); } catch { /* 忽略 */ }
        }

        Members.Clear();
        ShareAddresses.Clear();
        for (var i = 0; i < _seatMemberIds.Count; i++) _seatMemberIds[i] = "";
        SetNotice("已离开房间", "TextSecondaryBrush");
        NotifyRoomState();
    }

    /// <summary>发聊天。</summary>
    private void SendChat()
    {
        var text = ChatText.Trim();
        if (text.Length == 0 || !IsOnline) return;
        ChatText = "";
        AppendChat($"{LocalName}：{text}");
        _ = _session?.SendChatAsync(text);
    }

    // ---------------------------------------------------------------- 广播 / 接收

    /// <summary>房主：给每个成员发各自的状态（客户端只看到自己该看的）。</summary>
    protected void BroadcastStateFromRoom()
    {
        var session = _session;
        if (session is null || !IsHost) return;

        var envelopes = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in Members)
        {
            if (!member.IsOnline) continue;
            var envelope = BuildStateFor(member.MemberId);
            if (envelope.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
            envelopes[member.MemberId] = envelope;
        }

        _ = session.SendStatePerMemberAsync(envelopes);
    }

    /// <summary>席位变化后：把最新席位广播出去（房主）。</summary>
    protected void BroadcastSeats()
    {
        UpdateGameSeats();
        BroadcastStateFromRoom();
    }

    /// <summary>把 <see cref="_seatMemberIds"/> 交给子类（子类据此更新游戏内的座位）。</summary>
    protected abstract void UpdateGameSeats();

    /// <summary>房主：按当前换人规则算下一局席位。</summary>
    protected void ApplyRotationAfterGame(IReadOnlyDictionary<string, bool> winnersByMemberId)
    {
        if (!IsHost) return;

        var seats = new List<BoardMultiSeatRotation.SeatOutcome>(SeatCount);
        for (var i = 0; i < SeatCount; i++)
        {
            var id = SeatMemberId(i);
            seats.Add(new BoardMultiSeatRotation.SeatOutcome(id,
                id.Length > 0 && winnersByMemberId.TryGetValue(id, out var win) && win));
        }

        var candidates = _members.Values
            .Select(m => new BoardRotationCandidate(m.MemberId, m.Name, m.IsOnline, m.GamesPlayed, 0))
            .ToList();

        var result = BoardMultiSeatRotation.Plan(_autoRotateMode, _autoPickMode, seats, candidates);
        if (!result.Changed)
        {
            if (result.Reason.Length > 0) AppendChat($"换人：{result.Reason}");
            return;
        }

        lock (_roomGate)
        {
            for (var i = 0; i < SeatCount; i++)
                _seatMemberIds[i] = i < result.Seats.Count ? result.Seats[i] : "";
        }

        AppendChat($"换人：{result.Reason}");
        SyncMembers();
        BroadcastSeats();
        NotifyRoomState();
    }

    // ---------------------------------------------------------------- 会话接线

    /// <summary>取会话（子类做动作上报用）。</summary>
    protected BoardRoomSession? Session => _session;

    /// <summary>子类可以覆盖（少数需要自定义握手参数的场合）。</summary>
    protected virtual BoardRoomSession CreateSession(string localName) => new(ToolId, localName);

    /// <summary>把会话事件接到基类（后台线程 → UI）。</summary>
    protected void WireSession(BoardRoomSession session)
    {
        session.StateChanged += () => Ui(NotifyRoomState);
        session.MemberJoined += member => Ui(() => OnMemberJoined(member));
        session.MemberLeft += (member, kind, reason) => Ui(() => OnMemberLeft(member, kind, reason));
        session.MemberReconnected += member => Ui(() => { SyncMembers(); NotifyRoomState(); });
        session.ChatReceived += (memberId, name, text, isSystem) => Ui(() => AppendChat(isSystem ? $"[系统] {text}" : $"{name}：{text}"));
        session.ActionReceived += (memberId, type, payload) => Ui(() => HandleAction(memberId, type, payload));
        session.StateReceived += envelope => Ui(() => ApplyState(envelope));
    }

    private void OnMemberJoined(BoardMemberInfo member)
    {
        lock (_roomGate)
        {
            _members[member.MemberId] = new MultiSeatMember(
                member.MemberId, member.Name, member.IsHost, member.IsOnline, member.GamesPlayed,
                _seatMemberIds.IndexOf(member.MemberId));
        }

        AppendChat($"{member.Name} 加入了房间");
        SyncMembers();
        NotifyRoomState();

        // 新成员来了：优先补进空席位
        if (IsHost)
        {
            var emptySeat = _seatMemberIds.IndexOf("");
            if (emptySeat >= 0)
            {
                _seatMemberIds[emptySeat] = member.MemberId;
                SyncMembers();
            }
            BroadcastSeats();
        }
    }

    private void OnMemberLeft(BoardMemberInfo member, BoardDisconnectKind kind, string reason)
    {
        lock (_roomGate)
        {
            if (_members.TryGetValue(member.MemberId, out var existing)) _members[member.MemberId] = existing with { IsOnline = false };
            var seat = _seatMemberIds.IndexOf(member.MemberId);
            if (seat >= 0) _seatMemberIds[seat] = "";
        }

        AppendChat($"{member.Name} 离开了房间（{reason}）");
        SyncMembers();
        NotifyRoomState();

        if (IsHost)
        {
            BroadcastSeats();
            if (WaitingMembers.Count == 0 && Members.All(m => !m.IsOnline)) OnEveryoneLeft();
        }
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>重建成员列表（加锁读权威模型）。</summary>
    private void SyncMembers()
    {
        List<MultiSeatMember> snapshot;
        lock (_roomGate)
        {
            snapshot = _members.Values
                .OrderBy(m => _seatMemberIds.IndexOf(m.MemberId) >= 0 ? 0 : 1)
                .ThenBy(m => _seatMemberIds.IndexOf(m.MemberId))
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        Members.Clear();
        foreach (var member in snapshot) Members.Add(member);

        OnPropertyChanged(nameof(Members));
        OnPropertyChanged(nameof(SeatSummaryText));
        OnPropertyChanged(nameof(WaitingMembers));
        OnPropertyChanged(nameof(IsSeated));
        OnPropertyChanged(nameof(MySeatIndex));
        OnRoomChanged();
    }

    /// <summary>加一条聊天 / 系统提示。</summary>
    protected void AppendChat(string line)
    {
        ChatItems.Add($"{DateTime.Now:HH:mm} {line}");
        if (ChatItems.Count > 300) ChatItems.RemoveAt(0);
    }

    /// <summary>设置状态提示。</summary>
    protected void SetNotice(string text, string brushKey)
    {
        _notice = text;
        _noticeBrushKey = brushKey;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(NoticeBrushKey));
    }

    /// <summary>房间状态一次性通知。</summary>
    protected void NotifyRoomState()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(IsClient));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(CanLeave));
        OnPropertyChanged(nameof(IsRoomBusy));
        OnPropertyChanged(nameof(ModeBadgeText));
        OnPropertyChanged(nameof(ModeBadgeBrushKey));
        OnPropertyChanged(nameof(HasShareAddresses));
        OnPropertyChanged(nameof(CanSendChat));
        OnPropertyChanged(nameof(SeatSummaryText));
        OnPropertyChanged(nameof(SelectedAutoRotate));
        OnPropertyChanged(nameof(SelectedAutoPick));
        OnPropertyChanged(nameof(AutoRotateHintText));
    }

    /// <summary>房间序号（各游戏状态包装里的 seq 用它，保证递增）。</summary>
    protected int NextRoomSeq() => Interlocked.Increment(ref _roomSeq);

    /// <summary>序列化一个对象成 JSON（状态包装用）。</summary>
    protected static JsonElement ToJson<T>(T value)
        => JsonSerializer.SerializeToElement(value, RoomProtocol.JsonOptions);

    /// <summary>把 JSON 读成对象（容错：坏包返回 null）。</summary>
    protected static T? FromJson<T>(JsonElement element) where T : class
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        try { return element.Deserialize<T>(RoomProtocol.JsonOptions); }
        catch { return null; }
    }

    /// <summary>聊天 / 状态变更后让子类也刷新一次。</summary>
    protected void RefreshRoomUi() => NotifyRoomState();
}
