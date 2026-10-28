using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;
using YuLiangToolkit.Tools.BoardGames.Shared;

namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 棋牌功能配置里「房间」相关的字段（五子棋 / 象棋的 Config 都实现它）。
/// 基类只碰这些，其余设置项是各游戏自己的事。
/// </summary>
public interface IBoardGameRoomConfig
{
    /// <summary>玩家名（别人看到的名字）。</summary>
    string PlayerName { get; set; }

    /// <summary>上次加入过的主机地址（下次打开自动带出来）。</summary>
    string LastJoinAddress { get; set; }

    /// <summary>开房时监听的端口（0 = 让系统分配）。</summary>
    int ListenPort { get; set; }

    /// <summary>房主上次选的自动上场方式（<c>manual</c> / <c>loserOut</c> / <c>bothOut</c>）。</summary>
    string AutoRotateMode { get; set; }

    /// <summary>房主上次选的补位方式（<c>rotation</c> / <c>random</c>）。</summary>
    string AutoPickMode { get; set; }
}

/// <summary>当前处于哪种玩法。</summary>
public enum BoardPlayMode
{
    /// <summary>没进房间（也没同机双人）：棋盘可以看，但不能下。</summary>
    Offline = 0,

    /// <summary>同机双人：两个人轮流用这一台电脑。</summary>
    Local = 1,

    /// <summary>主机（房主）：本端开房间，可以坐席位也可以只当房主/观战。</summary>
    Host = 2,

    /// <summary>客户端：连到别人的房间（坐席位 = 比赛，不坐 = 观战）。</summary>
    Client = 3,
}

/// <summary>
/// 一局棋的「状态快照」，基类用它判断暂停 / 能不能开始下一局 / 要不要自动轮换。
/// </summary>
/// <param name="Started">这一局已经开始（棋盘上真的有棋）。</param>
/// <param name="Over">这一局已经结束（分胜负 / 和棋 / 认输 / 禁手判负都算）。</param>
/// <param name="WinnerSide">赢家执哪边（<c>black</c>/<c>white</c>/<c>red</c>）；平局 / 不知道为 null。</param>
public readonly record struct BoardGameStatus(bool Started, bool Over, string? WinnerSide);

/// <summary>
/// 两个棋牌游戏 ViewModel 的公共基类，<b>多人房间版（协议 v2）</b>：
/// 房间生命周期、成员列表、两个上场席位、房主安排上场、自动轮换、观战 / 请求路由、断线重连。
///
/// <para><b>分工</b>：这里<b>不碰任何棋盘规则</b> —— 规则全在子类的 abstract 方法 / 钩子里；
/// 基类负责「谁是房主、谁在线、谁坐上哪个席位、下一局该谁上、请求该转给谁」。</para>
///
/// <para><b>线程约定</b>：<see cref="BoardRoomSession"/> 的事件都在后台线程触发，
/// 而且 <c>MemberJoined/MemberReconnected</c> 会在「网络线程、stateFactory 之前」同步回调 ——
/// 所以本类把<b>权威房间模型</b>（成员表 + 两个席位 + 自动轮换设置）加锁维护，
/// 事件处理器里<b>同步</b>更新模型；界面（<c>Members</c> 等）随后用 <c>Ui(...)</c> 刷新。
/// stateFactory 只读「权威房间模型 + 缓存的棋局快照」，不碰 UI 线程的游戏对象。</para>
///
/// <para><b>子类可以认为所有 virtual 钩子都在 UI 线程被调用</b>（demo 房间初始化那一下除外，
/// 它只填数据、不回调钩子）。</para>
/// </summary>
public abstract class BoardGameRoomViewModelBase : ToolViewModelBase
{
    /// <summary>聊天列表最多保留多少条（再多就丢最老的，避免长时间聊天把界面拖慢）。</summary>
    private const int MaxChatItems = 400;

    /// <summary>自动重连：两次尝试之间的间隔（毫秒）。自检可用环境变量 YLT_BOARD_RECONNECT_MS 临时缩短。</summary>
    private static readonly int ReconnectIntervalMs = ReadIntEnv("YLT_BOARD_RECONNECT_MS", 2_000);

    /// <summary>自动重连：最多尝试多少次。自检可用环境变量 YLT_BOARD_RECONNECT_TRIES 临时减少。</summary>
    private static readonly int ReconnectMaxAttempts = ReadIntEnv("YLT_BOARD_RECONNECT_TRIES", 30);

    /// <summary>自动重连：总共最多等多久（毫秒，默认 2 秒 × 30 次 = 60 秒）。</summary>
    private static readonly int ReconnectWindowMs = ReconnectIntervalMs * ReconnectMaxAttempts;

    /// <summary>「结束本局（作废）」的二次确认有效期（毫秒）：点第一下后 3 秒内没再点就自动取消。</summary>
    private const int AbortConfirmMs = 3_000;

    /// <summary>截图 / 演示开关：设了环境变量 <c>YLT_BOARD_DEMO_PAUSE=1</c> 时，界面固定表现为暂停状态（不联网）。</summary>
    private static readonly bool DemoPaused = Environment.GetEnvironmentVariable("YLT_BOARD_DEMO_PAUSE") is not null;

    /// <summary>
    /// 截图 / 演示开关：<c>YLT_BOARD_DEMO_ROOM=1</c> 假装房主开着一个 4 人房间；
    /// <c>=spectator</c> 假装客户端观战（本端 = 小红，不坐席位）。<b>不联网、不影响真实房间</b>。
    /// </summary>
    private static readonly string DemoRoomEnv =
        (Environment.GetEnvironmentVariable("YLT_BOARD_DEMO_ROOM") ?? "").Trim().ToLowerInvariant();

    /// <summary>读一个正整数环境变量（给自检 / 排错用；非法值用默认值）。</summary>
    private static int ReadIntEnv(string name, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return int.TryParse(text, out var value) && value is >= 1 and <= 600 ? value : fallback;
    }

    private readonly DispatcherTimer? _statusTimer;

    /// <summary>权威房间模型的锁（成员表 + 两席 id + 自动轮换设置）：网络线程和 UI 线程都会摸。</summary>
    private readonly object _roomGate = new();

    /// <summary>权威成员表（房主 + 客户端 + 掉线保留的成员）。会话的成员表<b>不含房主</b>，这里合并。</summary>
    private readonly Dictionary<string, BoardMemberInfo> _roomMembers = new(StringComparer.Ordinal);

    /// <summary>成员列表行（原地更新，界面直接绑）。</summary>
    private readonly Dictionary<string, BoardMemberItem> _memberItems = new(StringComparer.Ordinal);

    /// <summary>最近一次在 UI 线程生成的棋局快照（stateFactory 在网络线程只读它，不碰游戏对象）。</summary>
    private JsonElement _lastGameSnapshot;

    private IBoardGameRoomConfig? _roomCfg;
    private BoardRoomSession? _session;
    private BoardPlayMode _mode = BoardPlayMode.Offline;
    private bool _isRoomBusy;
    private bool _hasPeer;
    private string _peerName = "";
    private string _noticeText = "还没进入房间：开个房间等别人加入，或填地址加入别人的房间，也可以同机双人";
    private string _noticeBrushKey = "TextSecondaryBrush";
    private string _chatText = "";
    private string _listenPortText = "";
    private int _stateSeq;
    private int _fallbackJoinOrder;

    // ------- 权威房间模型（读写都进 _roomGate）-------
    private string _seatOneMemberId = "";
    private string _seatTwoMemberId = "";
    private BoardAutoRotateMode _autoRotateMode = BoardAutoRotateMode.Manual;
    private BoardAutoPickMode _autoPickMode = BoardAutoPickMode.Rotation;

    // ------- 对局 / 自动轮换 -------
    private bool _finishedNotified;
    private bool _hasPlayedGame;
    private bool _isAbortConfirming;
    private int _abortConfirmGeneration;

    // ------- 请求 / 应答 -------
    private bool _hasPendingPrompt;
    private string _pendingPromptText = "";
    private string _pendingRequestType = "";
    private string _pendingFromMemberId = "";
    private string _pendingFromName = "";
    private bool _hasPendingWait;
    private string _pendingWaitText = "";
    private string _pendingWaitType = "";
    private string _hostWaitingOpponentId = "";
    private string _hostWaitingType = "";
    private readonly Dictionary<string, PendingRelay> _relayPending = new(StringComparer.Ordinal);

    // ------- 断线重连 -------
    private bool _isReconnecting;
    private string _reconnectStatusText = "";
    private bool _clientOnline;
    private bool _closed;
    private int _reconnectGeneration;
    private string _lastMemberId = "";

    // ------- 演示房间 -------
    private bool _demoRoom;
    private bool _demoInitialized;
    private string _demoLocalMemberId = "";

    /// <summary>待转达的请求（房主是裁判：客户端 A 请求 → 房主转给对手 B → B 回应后房主应用）。</summary>
    private sealed record PendingRelay(string RequesterMemberId, string RequesterName, string Type);

    /// <summary>创建棋牌房间基类。</summary>
    /// <param name="toolId">功能 Id（同时是游戏 Id：<c>gomoku</c> / <c>xiangqi</c>）。</param>
    /// <param name="gameName">游戏中文名（用在文案里，例如「五子棋」）。</param>
    protected BoardGameRoomViewModelBase(string toolId, string gameName) : base(toolId)
    {
        GameName = string.IsNullOrWhiteSpace(gameName) ? "棋牌" : gameName.Trim();

        // ------- 命令（XAML 直接绑）-------
        StartHostCommand = new AsyncRelayCommand(_ => StartHostInternalAsync(), _ => CanJoin);
        JoinRoomCommand = new AsyncRelayCommand(_ => JoinRoomInternalAsync(), _ => CanJoin);
        LeaveRoomCommand = new AsyncRelayCommand(_ => LeaveRoomInternalAsync(), _ => CanLeaveRoom);
        StartLocalCommand = new RelayCommand(_ => StartLocalMode(), _ => !IsOnline && !_isRoomBusy);
        AcceptPendingCommand = new RelayCommand(_ => _ = AcceptPendingAsync(), _ => HasPendingPrompt);
        RejectPendingCommand = new RelayCommand(_ => _ = RejectPendingAsync(), _ => HasPendingPrompt);
        SendChatCommand = new RelayCommand(_ => SendChat(), _ => CanSendChat);
        ClearChatCommand = new RelayCommand(_ => ResetChat());
        CopyInviteCommand = new RelayCommand(_ => CopyToClipboard(InviteText));
        CopyTextCommand = new RelayCommand(p => CopyToClipboard(p as string ?? ""));

        SwapSeatsCommand = new RelayCommand(_ => SwapSeats(), _ => CanEditSeats);
        ClearSeatsCommand = new RelayCommand(_ => ClearSeats(), _ => CanEditSeats);
        StartNextGameCommand = new RelayCommand(_ => StartNextGameFromUi(), _ => CanStartNextGame);
        AbortGameCommand = new RelayCommand(_ => AbortGame(), _ => CanAbortGame);

        // ------- 延迟 / 对方名字会变，定时刷新一下摘要 -------
        if (Application.Current is not null)
        {
            _statusTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += (_, _) =>
            {
                if (!IsOnline) return;
                OnPropertyChanged(nameof(ConnectionSummary));
                OnPropertyChanged(nameof(ModeBadgeText));
            };
            _statusTimer.Start();
        }
    }

    // ---------------------------------------------------------------- 子类接入点

    /// <summary>游戏中文名（"五子棋" / "中国象棋"）。</summary>
    public string GameName { get; }

    /// <summary>游戏 Id（默认等于工具 Id；两个游戏不需要重写）。</summary>
    protected virtual string GameId => ToolId;

    /// <summary>当前这次房间会话（只读，<b>给自检 / 排错用</b>：例如 <c>CurrentSession?.AbortConnection()</c> 模拟断网）。</summary>
    public BoardRoomSession? CurrentSession => _session;

    /// <summary>
    /// 子类在构造里 <c>LoadConfig&lt;T&gt;()</c> 之后调用，把配置交给基类
    /// （玩家名 / 上次加入地址 / 监听端口 / 自动轮换设置双向绑定并自动落盘）。
    /// </summary>
    protected void AttachRoomConfig(IBoardGameRoomConfig cfg)
    {
        _roomCfg = cfg;
        _listenPortText = cfg.ListenPort is >= 0 and <= 65535 ? cfg.ListenPort.ToString() : "0";
        lock (_roomGate)
        {
            _autoRotateMode = BoardAutoModes.ParseRotate(cfg.AutoRotateMode);
            _autoPickMode = BoardAutoModes.ParsePick(cfg.AutoPickMode);
        }
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(JoinAddress));
        OnPropertyChanged(nameof(ListenPortText));
        OnPropertyChanged(nameof(ListenPort));
        InitDemoRoomIfNeeded();
    }

    /// <summary>本端玩家名（空的话给个默认名，免得别人看到一个空白名字）。</summary>
    protected string LocalName => string.IsNullOrWhiteSpace(PlayerName) ? "玩家" : PlayerName.Trim();

    // ---------------------------------------------------------------- 配置绑定

    /// <summary>玩家名（双向落 <see cref="IBoardGameRoomConfig.PlayerName"/>）。</summary>
    public string PlayerName
    {
        get => _roomCfg?.PlayerName ?? "";
        set
        {
            if (_roomCfg is null || _roomCfg.PlayerName == value) return;
            _roomCfg.PlayerName = value ?? "";
            OnPropertyChanged();
            // 房主在房间里改名字：更新权威模型里的房主名（下一局 / 下次广播生效）
            if (IsHost && IsOnline && !_demoRoom) UpdateHostMemberName(LocalName);
        }
    }

    /// <summary>加入房间时要连的地址（<c>192.168.1.5:5310</c> / <c>[::1]:5310</c> / 域名:端口）。</summary>
    public string JoinAddress
    {
        get => _roomCfg?.LastJoinAddress ?? "";
        set
        {
            if (_roomCfg is null || _roomCfg.LastJoinAddress == value) return;
            _roomCfg.LastJoinAddress = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>开房端口文本（<c>0</c> = 自动挑；非法文本按上次有效值处理）。</summary>
    public string ListenPortText
    {
        get => _listenPortText;
        set
        {
            var text = value ?? "";
            if (_listenPortText == text) return;
            _listenPortText = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ListenPort));
            if (_roomCfg is not null && int.TryParse(text.Trim(), out var port) && port is >= 0 and <= 65535)
                _roomCfg.ListenPort = port;
        }
    }

    /// <summary>实际要用的监听端口（0 = 让系统分配）。</summary>
    public int ListenPort
    {
        get
        {
            var text = ListenPortText.Trim();
            if (text.Length > 0 && int.TryParse(text, out var parsed) && parsed is >= 0 and <= 65535)
                return parsed;
            return _roomCfg?.ListenPort is >= 0 and <= 65535 ? _roomCfg.ListenPort : 0;
        }
    }

    // ---------------------------------------------------------------- 房间 / 连接状态

    /// <summary>当前玩法。</summary>
    public BoardPlayMode Mode => _mode;

    /// <summary>同机双人。</summary>
    public bool IsLocalMode => _mode == BoardPlayMode.Local;

    /// <summary>本端是主机（房主）。demo 房间（YLT_BOARD_DEMO_ROOM=1）也算。</summary>
    public bool IsHost => _mode == BoardPlayMode.Host;

    /// <summary>本端是客户端。</summary>
    public bool IsClient => _mode == BoardPlayMode.Client;

    /// <summary>在线：主机监听中 / 客户端握手完成（断线重连期间是 false，看 <see cref="IsReconnecting"/>）。</summary>
    public bool IsOnline => _demoRoom || _session?.IsOnline == true;

    /// <summary>是不是 YLT_BOARD_DEMO_ROOM 生效的演示房间（不联网；游戏侧据此只注入演示棋、跳过同机双人）。</summary>
    public bool IsDemoRoom => _demoRoom;

    /// <summary>还没在线、不在重连、也不在开房/连接中（三个「进入房间」按钮的可用条件）。</summary>
    public bool CanJoin => !IsOnline && !IsReconnecting && !_isRoomBusy
                           && _mode is not (BoardPlayMode.Host or BoardPlayMode.Client);

    /// <summary>开房 / 连接中（按钮禁用，防连点）。</summary>
    public bool IsRoomBusy
    {
        get => _isRoomBusy;
        private set
        {
            if (!Set(ref _isRoomBusy, value)) return;
            OnPropertyChanged(nameof(CanJoin));
            InvalidateCommands();
        }
    }

    /// <summary>房间里有没有别的在线成员（兼容旧 API；多客户端语义 = 至少一个客户端在线）。</summary>
    public bool HasPeer
    {
        get => _demoRoom || _hasPeer;
        private set
        {
            if (!Set(ref _hasPeer, value)) return;
            OnPropertyChanged(nameof(PeerSideText));
            OnPropertyChanged(nameof(IsGamePaused));
            OnPropertyChanged(nameof(GamePauseStatusText));
        }
    }

    /// <summary>
    /// 客户端正在自动重连：<see cref="Mode"/> 仍保持 <see cref="BoardPlayMode.Client"/>，
    /// 但 <see cref="IsOnline"/> 为 false、棋盘只读、不能重复点加入；点「离开房间」= 停止重连。
    /// <para>设了 <c>YLT_BOARD_DEMO_PAUSE=1</c> 时恒为 true（仅截图演示）。</para>
    /// </summary>
    public bool IsReconnecting
    {
        get => DemoPaused || _isReconnecting;
        private set
        {
            if (!Set(ref _isReconnecting, value)) return;
            RaiseAllDerived();
        }
    }

    /// <summary>重连进度文案，例如「连接断开，正在重连（第 3 次）…」；没在重连时为空。</summary>
    public string ReconnectStatusText
    {
        get => DemoPaused ? "正在重连（第 3 次，还剩 25 次）…" : _reconnectStatusText;
        private set => Set(ref _reconnectStatusText, value);
    }

    /// <summary>当前棋局状态（子类没实现好 / 抛异常时按「未开始」处理，不让界面崩）。</summary>
    private BoardGameStatus CurrentGameStatus()
    {
        try { return ReportGameStatus(); }
        catch { return default; }
    }

    /// <summary>本局正在进行中（已开始、还没结束）。</summary>
    public bool IsGameRunning
    {
        get
        {
            var status = CurrentGameStatus();
            return status.Started && !status.Over;
        }
    }

    /// <summary>
    /// 对局暂停：演示开关 / 客户端重连中 / 对局进行中但席位上有人掉线或席位空着。
    /// 观战者掉线不影响；房主换人 / 作废 / 重开会解除。
    /// </summary>
    public bool IsGamePaused
    {
        get
        {
            if (DemoPaused) return true;
            if (IsReconnecting) return true;
            if (IsLocalMode || _demoRoom) return false;
            var status = CurrentGameStatus();
            if (!status.Started || status.Over) return false;
            var oneId = _seatOneMemberId;
            var twoId = _seatTwoMemberId;
            if (oneId.Length == 0 || twoId.Length == 0) return true;
            return !IsMemberOnline(oneId) || !IsMemberOnline(twoId);
        }
    }

    /// <summary>暂停时给界面看的一句人话（没暂停时为空）。</summary>
    public string GamePauseStatusText
    {
        get
        {
            if (DemoPaused) return "对局暂停 · 等待对方重连";
            if (!IsGamePaused) return "";
            if (IsReconnecting) return "对局暂停 · 连接断开，正在重连…";
            if (_seatOneMemberId.Length == 0 || _seatTwoMemberId.Length == 0)
                return "对局暂停 · 席位空着，等待房主安排上场";
            return "对局暂停 · 等席上的玩家重连回来";
        }
    }

    /// <summary>「开房 / 加入房间」输入区要不要显示（在线、房间开着、重连中都不显示）。</summary>
    public bool ShowJoinControls => !IsOnline && !IsReconnecting
                                    && _mode is not (BoardPlayMode.Host or BoardPlayMode.Client);

    /// <summary>有没有可离开的房间 / 可停止的重连。</summary>
    public bool CanLeaveRoom => IsOnline || IsReconnecting;

    /// <summary>离开按钮的文案：重连中是「停止重连」，其它时候是「离开房间」。</summary>
    public string LeaveRoomText => IsReconnecting ? "停止重连" : "离开房间";

    /// <summary>兼容旧界面：房间里另一个人的名字（客户端=房主；主机=对手/第一个在线成员）。</summary>
    public string PeerName
    {
        get => _peerName;
        private set
        {
            if (!Set(ref _peerName, value)) return;
            OnPropertyChanged(nameof(PeerSideText));
            OnPropertyChanged(nameof(ConnectionSummary));
        }
    }

    /// <summary>状态徽标文案。</summary>
    public string ModeBadgeText => IsReconnecting
        ? "◌ 正在重连…"
        : _mode switch
        {
            BoardPlayMode.Local => "● 同机双人",
            BoardPlayMode.Host => IsOnline ? (IsGamePaused ? "● 对局暂停" : "● 房间开着") : "○ 未连接",
            BoardPlayMode.Client => IsOnline ? (IsSeatedPlayer ? "● 对局中" : "● 观战中") : "○ 未连接",
            _ => "○ 未连接",
        };

    /// <summary>状态徽标要用的画刷资源名（RoomPanel 用 DataTrigger 映射）。</summary>
    public string ModeBadgeBrushKey => IsReconnecting || IsGamePaused
        ? "DangerBrush"
        : _mode == BoardPlayMode.Local || IsOnline ? "SuccessBrush" : "TextSecondaryBrush";

    /// <summary>一行连接摘要：本机监听地址 / 房间人数 / 延迟 / 重连进度。</summary>
    public string ConnectionSummary
    {
        get
        {
            if (_demoRoom)
                return IsHost ? "演示房间：房主视角（不联网，YLT_BOARD_DEMO_ROOM=1）" : "演示房间：观战视角（不联网）";
            if (IsReconnecting)
                return ReconnectStatusText.Length > 0 ? ReconnectStatusText : "连接断开，正在自动重连…";

            var session = _session;
            return _mode switch
            {
                BoardPlayMode.Host when session is not null => session.IsOnline
                    ? $"本机监听 {session.ListenText}（真实端口 {session.ListenPort}）　房间 {Members.Count} / {BoardRoomSession.MaxMembers} 人"
                      + (HasPeer ? $"　延迟 {session.LatencyMs} ms" : "　等别人加入")
                    : "正在开房间…",
                BoardPlayMode.Client when session is not null => session.IsOnline
                    ? $"已连接 {HostMemberName}　延迟 {session.LatencyMs} ms　房间 {Members.Count} / {BoardRoomSession.MaxMembers} 人"
                    : "正在连接…",
                BoardPlayMode.Client => "连接已断开",
                BoardPlayMode.Local => "两个人轮流用这一台电脑操作",
                _ => "未连接（可以开房间、加入房间，或同机双人）",
            };
        }
    }

    /// <summary>我方执子文案（RoomPanel 用；游戏界面请用 <see cref="MyRoleText"/>）。</summary>
    public string LocalSideText => _mode switch
    {
        BoardPlayMode.Local => "同机轮流（换手）",
        BoardPlayMode.Offline => "我方：未定",
        _ => LocalSide.Length > 0
            ? $"我方：{SideDisplayName(LocalSide)}"
            : IsOnline ? "我方：观战中" : "我方：未定",
    };

    /// <summary>对方执子文案（RoomPanel 用）。</summary>
    public string PeerSideText
    {
        get
        {
            if (_mode == BoardPlayMode.Local) return "同机轮流（换手）";
            if (_mode == BoardPlayMode.Offline) return "对方：未定";
            if (!IsOnline) return IsClient ? "房主：已断开" : "对方：未定";
            if (IsSeatedPlayer)
            {
                var opponentId = OtherSeatMemberId(LocalMemberId);
                if (opponentId.Length == 0) return "对手：空位";
                var name = MemberDisplayName(opponentId);
                return name.Length > 0 ? $"对手：{name}" : "对手：未定";
            }
            return "对手：—（你在观战）";
        }
    }

    /// <summary>最近一条系统提示。</summary>
    public string NoticeText
    {
        get => _noticeText;
        private set => Set(ref _noticeText, value);
    }

    /// <summary>提示要用的画刷资源名（RoomPanel 用 DataTrigger 映射）。</summary>
    public string NoticeBrushKey
    {
        get => _noticeBrushKey;
        private set => Set(ref _noticeBrushKey, value);
    }

    // ---------------------------------------------------------------- 地址分享

    /// <summary>主机开房后的「地址:端口」若干行（客户端 / 离线时为空）。</summary>
    public ObservableCollection<RoomShareRow> ShareRows { get; } = new();

    private const int MaxShareRowsShown = 3;

    /// <summary>房间卡实际显示的那几行地址（前 3 行）。</summary>
    public ObservableCollection<RoomShareRow> ShareRowsVisible { get; } = new();

    /// <summary>有没有可分享的地址。</summary>
    public bool HasShareRows => ShareRows.Count > 0;

    /// <summary>地址太多、被折叠掉了一部分。</summary>
    public bool HasMoreShareRows => ShareRows.Count > MaxShareRowsShown;

    /// <summary>被折叠时的提示文案。</summary>
    public string ShareMoreText => HasMoreShareRows
        ? $"…另外 {ShareRows.Count - MaxShareRowsShown} 个地址已折叠；点「复制邀请」会把推荐地址一起发过去"
        : "";

    /// <summary>一键邀请文案（含地址:端口；没有可分享地址时为空）。</summary>
    public string InviteText
    {
        get
        {
            if (ShareRows.Count == 0) return "";
            var row = ShareRows.FirstOrDefault(r => r.IsRecommended && !r.IsIpv6)
                      ?? ShareRows.FirstOrDefault(r => !r.IsIpv6)
                      ?? ShareRows[0];
            return $"来联机下{GameName}！我开好房间了，地址：{row.AddressText}";
        }
    }

    // ---------------------------------------------------------------- 聊天

    /// <summary>房间聊天记录（系统提示也在里面，用灰字）。</summary>
    public ObservableCollection<BoardChatItem> ChatItems { get; } = new();

    /// <summary>聊天输入框内容。</summary>
    public string ChatText
    {
        get => _chatText;
        set
        {
            if (!Set(ref _chatText, value)) return;
            OnPropertyChanged(nameof(CanSendChat));
            InvalidateCommands();
        }
    }

    /// <summary>可以发送聊天（在线 + 非空）。</summary>
    public bool CanSendChat => IsOnline && ChatText.Trim().Length > 0;

    /// <summary>往聊天记录里加一条系统提示（灰字，只在本端显示）。可从任意线程调用。</summary>
    public void AppendSystemChat(string text) => AppendNoticeChat(text, alsoNotice: false);

    /// <summary>加一条系统提示，同时更新状态栏提示（只在本端显示）。</summary>
    public void AppendNoticeChat(string text) => AppendNoticeChat(text, alsoNotice: true);

    private void AppendNoticeChat(string text, bool alsoNotice)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;
        Ui(() =>
        {
            AddChatItem(BoardChatItem.System(trimmed));
            if (alsoNotice) SetNoticeInternal(trimmed, "TextSecondaryBrush");
        });
    }

    /// <summary>清空聊天记录（切模式 / 重新开房时也用它）。</summary>
    public void ResetChat() => Ui(() => ChatItems.Clear());

    private void AddChatItem(BoardChatItem item)
    {
        ChatItems.Add(item);
        while (ChatItems.Count > MaxChatItems) ChatItems.RemoveAt(0);
    }

    private void SendChat()
    {
        var text = ChatText.Trim();
        if (text.Length == 0) return;
        if (!IsOnline)
        {
            SetNotice("还没进房间，发不了聊天", "DangerBrush");
            return;
        }

        ChatText = "";
        AddChatItem(BoardChatItem.Chat(LocalName, text, isSelf: true));
        if (_demoRoom) return;
        if (IsHost) SendOnSession(s => s.BroadcastChatAsync(LocalName, text, false));
        else SendOnSession(s => s.SendChatAsync(text));
    }

    /// <summary>
    /// 主机往聊天里发一条系统提示：本机加一条，并广播给所有客户端（客户端也当成系统聊天显示）。
    /// 客户端调用 = 只在本机加一条（不会往上发）。
    /// </summary>
    protected void AnnounceSystemChat(string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;
        Ui(() =>
        {
            AddChatItem(BoardChatItem.System(trimmed));
            if (IsHost && IsOnline && !_demoRoom)
                SendOnSession(s => s.BroadcastChatAsync(LocalName, trimmed, true));
        });
    }

    // ---------------------------------------------------------------- 成员列表（公开给界面）

    /// <summary>房间成员列表（房主在第一位，其余按进房顺序；原地更新）。</summary>
    public ObservableCollection<BoardMemberItem> Members { get; } = new();

    /// <summary>人数文案：「3 / 16 人」。</summary>
    public string MemberCountText => $"{Members.Count} / {BoardRoomSession.MaxMembers} 人";

    /// <summary>本端成员 id：房主固定 <c>host</c>，客户端为主机分配的 id；离线 / 未进房为空。</summary>
    public string LocalMemberId
    {
        get
        {
            if (IsLocalMode || !IsOnline) return "";
            if (IsHost) return BoardRoomSession.HostMemberId;
            if (_demoRoom) return _demoLocalMemberId;
            var id = _session?.LocalMemberId ?? "";
            return id.Length > 0 ? id : _lastMemberId;
        }
    }

    /// <summary>本端是不是正坐在某个上场席位上（在线 && 坐席）。</summary>
    public bool IsSeatedPlayer
    {
        get
        {
            if (IsLocalMode || !IsOnline) return false;
            var me = LocalMemberId;
            if (me.Length == 0) return false;
            return string.Equals(me, _seatOneMemberId, StringComparison.Ordinal)
                   || string.Equals(me, _seatTwoMemberId, StringComparison.Ordinal);
        }
    }

    /// <summary>本端是不是观战（在线 && 没坐席位）。</summary>
    public bool IsSpectator => !IsLocalMode && IsOnline && !IsSeatedPlayer;

    /// <summary>
    /// 本端执哪一边：<b>只有「联机 && 坐在席位上」才返回边字符串</b>（<c>black</c>/<c>white</c>/<c>red</c>）；
    /// 观战 / 同机双人 / 离线 / 未入座一律空字符串。
    /// </summary>
    public string LocalSide
    {
        get
        {
            if (IsLocalMode || !IsOnline) return "";
            var me = LocalMemberId;
            if (me.Length == 0) return "";
            if (string.Equals(me, _seatOneMemberId, StringComparison.Ordinal)) return SeatOneSide;
            if (string.Equals(me, _seatTwoMemberId, StringComparison.Ordinal)) return SeatTwoSide;
            return "";
        }
    }

    /// <summary>先手位执哪边（五子棋 = 黑棋，象棋 = 红方）。</summary>
    public string SeatOneSide => FirstSeatSide;

    /// <summary>后手位执哪边。</summary>
    public string SeatTwoSide => OppositeSide(FirstSeatSide);

    /// <summary>先手位文案，例如「先手位（黑棋）」。</summary>
    public string SeatOneSideText => $"先手位（{SideDisplayName(SeatOneSide)}）";

    /// <summary>后手位文案，例如「后手位（白棋）」。</summary>
    public string SeatTwoSideText => $"后手位（{SideDisplayName(SeatTwoSide)}）";

    /// <summary>先手位上的成员名（空位 = "空位"）。</summary>
    public string SeatOneName => MemberDisplayName(_seatOneMemberId);

    /// <summary>后手位上的成员名。</summary>
    public string SeatTwoName => MemberDisplayName(_seatTwoMemberId);

    /// <summary>先手位上的成员在不在线（空位 = false）。</summary>
    public bool SeatOneOnline => _seatOneMemberId.Length > 0 && IsMemberOnline(_seatOneMemberId);

    /// <summary>后手位上的成员在不在线。</summary>
    public bool SeatTwoOnline => _seatTwoMemberId.Length > 0 && IsMemberOnline(_seatTwoMemberId);

    /// <summary>先手位状态文案（在线 / 离线 / 空位）。</summary>
    public string SeatOneOnlineText => _seatOneMemberId.Length == 0 ? "空位" : SeatOneOnline ? "在线" : "离线";

    /// <summary>后手位状态文案。</summary>
    public string SeatTwoOnlineText => _seatTwoMemberId.Length == 0 ? "空位" : SeatTwoOnline ? "在线" : "离线";

    /// <summary>
    /// 本端身份一句话（醒目展示）：「房主 · 先手位（黑棋）」/「观战中」/「同机双人」/「未进房间」。
    /// </summary>
    public string MyRoleText
    {
        get
        {
            if (IsLocalMode) return "同机双人";
            if (!IsOnline) return "未进房间";
            var me = LocalMemberId;
            var seat = "";
            if (me.Length > 0)
            {
                if (string.Equals(me, _seatOneMemberId, StringComparison.Ordinal)) seat = SeatOneSideText;
                else if (string.Equals(me, _seatTwoMemberId, StringComparison.Ordinal)) seat = SeatTwoSideText;
            }
            if (IsSeatedPlayer)
                return (IsHost ? "房主" : "选手") + " · " + seat;
            return IsHost ? "房主 · 观战中" : "观战中";
        }
    }

    /// <summary>
    /// 本端情况一句话（灰字）：等待房主安排 / 你在先手位… / 观战中…。
    /// </summary>
    public string SeatStatusText
    {
        get
        {
            if (IsReconnecting) return "连接断开，正在自动重连…";
            if (IsLocalMode) return "同机双人：两个人轮流用这一台电脑";
            if (!IsOnline) return "还没进入房间：开个房间等别人加入，或加入别人的房间";

            var status = CurrentGameStatus();
            var running = status.Started && !status.Over;
            var me = LocalMemberId;
            var seat = "";
            if (me.Length > 0 && string.Equals(me, _seatOneMemberId, StringComparison.Ordinal)) seat = SeatOneSideText;
            else if (me.Length > 0 && string.Equals(me, _seatTwoMemberId, StringComparison.Ordinal)) seat = SeatTwoSideText;

            if (IsSeatedPlayer)
            {
                if (running)
                    return IsGamePaused ? $"你在{seat}，对局暂停中（有人掉线 / 席位空着）" : $"你在{seat}，对局进行中";
                if (IsHost)
                    return $"你在{seat}，点「{StartNextGameText}」开始这一局";
                return $"你在{seat}，等房主点「{StartNextGameText}」";
            }

            if (IsHost) return "你在观战；点上面的席位下拉安排下一局上场的两个人";
            return "你在观战，等房主安排上场；可以看棋、聊天。";
        }
    }

    /// <summary>观战提示一句话（客户端 / 房主自己不下场时显示在 RosterPanel 里）。</summary>
    public string SpectatorHintText => IsHost
        ? "你在观战；用上面的席位下拉安排下一局上场的人（也可以把自己排上去）。"
        : "你在观战，等房主安排上场；可以看棋、聊天。";

    // ---------------------------------------------------------------- 房主控制（公开给界面）

    /// <summary>房主控制区可见：房主 && 在线。</summary>
    public bool HostControlsVisible => IsHost && IsOnline;

    /// <summary>能不能改席位：房主 && 在线 && 对局没在进行中（演示房间不受棋局状态限制，方便截图核对）。</summary>
    public bool CanEditSeats => HostControlsVisible && (_demoRoom || !IsGameRunning);

    /// <summary>两个席位都安排好了、且都在线（开始下一局的前置条件之一）。</summary>
    private bool SeatsReadyForStart(out string reason)
    {
        var oneId = _seatOneMemberId;
        var twoId = _seatTwoMemberId;
        if (oneId.Length == 0 || twoId.Length == 0)
        {
            reason = "两个席位都要有人才能开始对局";
            return false;
        }
        if (string.Equals(oneId, twoId, StringComparison.Ordinal))
        {
            reason = "两个人不能坐同一个席位";
            return false;
        }
        if (!IsMemberOnline(oneId) || !IsMemberOnline(twoId))
        {
            reason = "席位上有人不在线：等对方重连，或换一个在线的人";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>房主能开始（下一）局：在线 && 对局没在进行中 && 两席都有人且在线 && 开局校验通过。</summary>
    public bool CanStartNextGame
    {
        get
        {
            if (!HostControlsVisible) return false;
            if (!_demoRoom && IsGameRunning) return false;
            if (!SeatsReadyForStart(out _)) return false;
            return ValidateHostStartSafe() is null;
        }
    }

    /// <summary>开始按钮的文案：「开始对局」/「开始下一局」。</summary>
    public string StartNextGameText => _hasPlayedGame ? "开始下一局" : "开始对局";

    /// <summary>开始按钮可不可见（房主 && 在线；本地 / 观战不显示）。</summary>
    public bool ShowStartNextGameButton => HostControlsVisible;

    /// <summary>能不能「结束本局（作废）」：房主 && 在线 && 对局进行中。</summary>
    public bool CanAbortGame => HostControlsVisible && (_demoRoom || IsGameRunning);

    /// <summary>「结束本局（作废）」是不是在等第二次确认。</summary>
    public bool IsAbortConfirming
    {
        get => _isAbortConfirming;
        private set
        {
            if (!Set(ref _isAbortConfirming, value)) return;
            OnPropertyChanged(nameof(AbortGameText));
            InvalidateCommands();
        }
    }

    /// <summary>「结束本局（作废）」按钮文案。</summary>
    public string AbortGameText => _isAbortConfirming ? "再点一次确认作废本局" : "结束本局（作废）";

    /// <summary>悔棋 / 认输 / 求和这些「玩家操作」要不要显示：同机双人 || 坐在席位上。</summary>
    public bool ShowPlayerActions => IsLocalMode || IsSeatedPlayer;

    // ---------------------------------------------------------------- 席位下拉

    /// <summary>先手位下拉的选项（成员 + 「空位」）。</summary>
    public ObservableCollection<BoardSeatOption> SeatOneChoices { get; } = new();

    /// <summary>后手位下拉的选项。</summary>
    public ObservableCollection<BoardSeatOption> SeatTwoChoices { get; } = new();

    private bool _suppressSeatSelection;

    /// <summary>先手位当前选中项；<b>set 即生效并广播</b>（只有房主 && 对局未进行中能改）。</summary>
    public BoardSeatOption? SelectedSeatOneOption
    {
        get => FindSeatOption(SeatOneChoices, _seatOneMemberId);
        set
        {
            if (_suppressSeatSelection || value is null) return;
            if (!CanEditSeats)
            {
                OnPropertyChanged();
                return;
            }
            if (string.Equals(value.MemberId, _seatOneMemberId, StringComparison.Ordinal))
            {
                OnPropertyChanged();
                return;
            }
            var newId = value.MemberId;
            string chat;
            lock (_roomGate)
            {
                if (newId.Length > 0 && string.Equals(newId, _seatTwoMemberId, StringComparison.Ordinal))
                {
                    // 选中的成员已经坐在另一席 → 两席交换
                    _seatTwoMemberId = _seatOneMemberId;
                    _seatOneMemberId = newId;
                    chat = $"房主把两席交换了：{SeatOneSideText} = {MemberDisplayName(newId)}";
                }
                else
                {
                    _seatOneMemberId = newId;
                    chat = newId.Length == 0
                        ? "房主清空了先手位"
                        : $"房主把{SeatOneSideText}安排给了「{MemberDisplayName(newId)}」";
                }
            }
            CommitSeatChanged(chat);
        }
    }

    /// <summary>后手位当前选中项；set 即生效并广播。</summary>
    public BoardSeatOption? SelectedSeatTwoOption
    {
        get => FindSeatOption(SeatTwoChoices, _seatTwoMemberId);
        set
        {
            if (_suppressSeatSelection || value is null) return;
            if (!CanEditSeats)
            {
                OnPropertyChanged();
                return;
            }
            if (string.Equals(value.MemberId, _seatTwoMemberId, StringComparison.Ordinal))
            {
                OnPropertyChanged();
                return;
            }
            var newId = value.MemberId;
            string chat;
            lock (_roomGate)
            {
                if (newId.Length > 0 && string.Equals(newId, _seatOneMemberId, StringComparison.Ordinal))
                {
                    _seatOneMemberId = _seatTwoMemberId;
                    _seatTwoMemberId = newId;
                    chat = $"房主把两席交换了：{SeatTwoSideText} = {MemberDisplayName(newId)}";
                }
                else
                {
                    _seatTwoMemberId = newId;
                    chat = newId.Length == 0
                        ? "房主清空了后手位"
                        : $"房主把{SeatTwoSideText}安排给了「{MemberDisplayName(newId)}」";
                }
            }
            CommitSeatChanged(chat);
        }
    }

    /// <summary>交换两席（房主）。</summary>
    private void SwapSeats()
    {
        if (!CanEditSeats) return;
        string one, two;
        lock (_roomGate)
        {
            if (_seatOneMemberId.Length == 0 && _seatTwoMemberId.Length == 0) return;
            (one, two) = (_seatTwoMemberId, _seatOneMemberId);
            _seatOneMemberId = one;
            _seatTwoMemberId = two;
        }
        CommitSeatChanged("房主交换了先手位与后手位");
    }

    /// <summary>清空两席（房主）。</summary>
    private void ClearSeats()
    {
        if (!CanEditSeats) return;
        lock (_roomGate)
        {
            if (_seatOneMemberId.Length == 0 && _seatTwoMemberId.Length == 0) return;
            _seatOneMemberId = "";
            _seatTwoMemberId = "";
        }
        CommitSeatChanged("房主清空了两个席位");
    }

    /// <summary>席位改了之后的统一收尾：刷新列表 / 下拉 / 派生属性、调子类钩子、广播状态、聊天提示。</summary>
    private void CommitSeatChanged(string chatText)
    {
        RebuildSeatChoices();
        SyncMembersView();
        RaiseAllDerived();
        SafeHook(OnSeatsChanged);
        if (IsHost && IsOnline) BroadcastState();
        if (chatText.Length > 0)
        {
            Ui(() => AddChatItem(BoardChatItem.System(chatText)));
            HostBroadcastSystemChat(chatText);
        }
        SetNotice(chatText.Length > 0 ? chatText : "席位已更新", "TextSecondaryBrush");
    }

    // ---------------------------------------------------------------- 自动轮换设置

    /// <summary>自动上场方式下拉选项。</summary>
    public IReadOnlyList<OptionItem<string>> AutoRotateModeOptions { get; } = OptionItem.From(
        ("manual", "手动安排（房主自己选人）"),
        ("loserOut", "输的下场（赢家留场，输家下场）"),
        ("bothOut", "双方都下场（两个人一起换）"));

    /// <summary>补位方式下拉选项。</summary>
    public IReadOnlyList<OptionItem<string>> AutoPickModeOptions { get; } = OptionItem.From(
        ("rotation", "轮流上场（没上过场优先，再按进房顺序）"),
        ("random", "随机上场（没上过场的人里随机挑）"));

    /// <summary>当前自动上场方式；set = 存配置 + 广播（房主）。</summary>
    public OptionItem<string> SelectedAutoRotateMode
    {
        get => OptionItem<string>.Find(AutoRotateModeOptions, BoardAutoModes.ToKey(_autoRotateMode)) ?? AutoRotateModeOptions[0];
        set
        {
            if (value is null) return;
            var mode = BoardAutoModes.ParseRotate(value.Value);
            if (mode == _autoRotateMode) return;
            lock (_roomGate) _autoRotateMode = mode;
            if (_roomCfg is not null) _roomCfg.AutoRotateMode = BoardAutoModes.ToKey(mode);
            OnPropertyChanged();
            RaiseAllDerived();
            if (IsHost && IsOnline && !_demoRoom)
            {
                BroadcastState();
                HostBroadcastSystemChat($"房主把自动上场方式改成了「{BoardAutoModes.Describe(mode)}」");
            }
        }
    }

    /// <summary>当前补位方式；set = 存配置 + 广播（房主）。</summary>
    public OptionItem<string> SelectedAutoPickMode
    {
        get => OptionItem<string>.Find(AutoPickModeOptions, BoardAutoModes.ToKey(_autoPickMode)) ?? AutoPickModeOptions[0];
        set
        {
            if (value is null) return;
            var mode = BoardAutoModes.ParsePick(value.Value);
            if (mode == _autoPickMode) return;
            lock (_roomGate) _autoPickMode = mode;
            if (_roomCfg is not null) _roomCfg.AutoPickMode = BoardAutoModes.ToKey(mode);
            OnPropertyChanged();
            RaiseAllDerived();
            if (IsHost && IsOnline && !_demoRoom)
            {
                BroadcastState();
                HostBroadcastSystemChat($"房主把补位方式改成了「{BoardAutoModes.Describe(mode)}」");
            }
        }
    }

    /// <summary>是不是开了自动轮换（不是「手动安排」）。</summary>
    public bool AutoRotateEnabled => _autoRotateMode != BoardAutoRotateMode.Manual;

    /// <summary>自动轮换的一句话规则说明（RosterPanel 小字）。</summary>
    public string AutoRotateHintText
    {
        get
        {
            var pick = BoardAutoModes.Describe(_autoPickMode);
            return _autoRotateMode switch
            {
                BoardAutoRotateMode.LoserOut =>
                    $"输的下场：一局结束后赢家留在场上，输家下场；空位按「{pick}」从没上场的人里补。"
                    + "人数不够时保持原上场；排好后房主点「开始下一局」。",
                BoardAutoRotateMode.BothOut =>
                    $"双方都下场：一局结束后两个人都下场；空位按「{pick}」从没上场的人里补。"
                    + "人数不够时保持原上场；排好后房主点「开始下一局」。",
                _ => "手动安排：每局结束后由你（房主）选下一局上场的两个人，选好点「开始下一局」。",
            };
        }
    }

    // ---------------------------------------------------------------- 联机请求（悔棋 / 求和 / 再来一局 / 认输）

    /// <summary>请求悔棋（联机时按角色发给对手 / 房主；同机双人由子类自己处理，别调这个）。</summary>
    public void RequestUndo() => RequestPeer(BoardRequestTypes.Undo);

    /// <summary>求和。</summary>
    public void OfferDraw() => RequestPeer(BoardRequestTypes.Draw);

    /// <summary>
    /// 请求再来一局：客户端玩家 = 发请求给房主（房主决定）；房主自己 = 直接开始下一局。
    /// <b>同机双人 / 离线不要调这个</b>（子类自己改棋局状态即可）。
    /// </summary>
    public void RequestRematch()
    {
        if (IsLocalMode) return;
        if (!IsOnline)
        {
            SetNotice("还没进房间，不能请求再来一局", "DangerBrush");
            return;
        }
        if (IsHost)
        {
            if (CanStartNextGame) StartNextGameFromUi();
            else SetNotice("现在还不能开始下一局：两席都要有在线的选手，且本局已经结束", "DangerBrush");
            return;
        }
        if (!IsSeatedPlayer)
        {
            SetNotice("你在观战，不能请求再来一局（等房主安排上场）", "DangerBrush");
            return;
        }
        RequestPeer(BoardRequestTypes.Rematch);
    }

    /// <summary>
    /// 认输（联机）：房主是玩家时直接判负并广播；客户端玩家发请求给房主执行。
    /// <b>同机双人 / 离线不要调这个</b>。
    /// </summary>
    public void ResignOnline()
    {
        if (IsLocalMode) return;
        if (!IsOnline)
        {
            SetNotice("还没进房间，不能认输", "DangerBrush");
            return;
        }
        if (!IsSeatedPlayer)
        {
            SetNotice("你在观战，不能认输", "DangerBrush");
            return;
        }
        if (IsHost)
        {
            var side = LocalSide;
            try { ApplyResign(side); }
            catch (Exception ex) { SetNoticeInternal($"认输处理失败：{ex.Message}", "DangerBrush"); return; }
            SetNotice("你认输了，本局结束", "DangerBrush");
            AnnounceSystemChat("你认输了，本局结束");
            CacheGameSnapshot();
            BroadcastState();
            return;
        }

        SetNotice("已向房主认输，等待对局结束…", "DangerBrush");
        AppendNoticeChat("你认输了，等房主确认");
        SendOnSession(s => s.SendRequestAsync(BoardRequestTypes.Resign));
    }

    /// <summary>有没有「别人请求你，等你点同意 / 拒绝」。</summary>
    public bool HasPendingPrompt
    {
        get => _hasPendingPrompt;
        private set
        {
            if (!Set(ref _hasPendingPrompt, value)) return;
            InvalidateCommands();
        }
    }

    /// <summary>待确认请求的文案。</summary>
    public string PendingPromptText
    {
        get => _pendingPromptText;
        private set => Set(ref _pendingPromptText, value);
    }

    /// <summary>自己发的请求还在等对方回应。</summary>
    public bool HasPendingWait
    {
        get => _hasPendingWait;
        private set => Set(ref _hasPendingWait, value);
    }

    /// <summary>等待回应的文案。</summary>
    public string PendingWaitText
    {
        get => _pendingWaitText;
        private set => Set(ref _pendingWaitText, value);
    }

    /// <summary>本端（坐席玩家）主动发一个请求：房主直接按成员 id 发给对手，客户端发给房主转达。</summary>
    private void RequestPeer(string type)
    {
        if (IsLocalMode || _demoRoom) return;
        if (!IsOnline)
        {
            SetNotice("还没进房间，不能发这个请求", "DangerBrush");
            return;
        }
        if (!IsSeatedPlayer)
        {
            SetNotice("只有场上的选手才能发这个请求（你在观战）", "DangerBrush");
            return;
        }
        if (_hasPendingWait || _hasPendingPrompt)
        {
            SetNotice("已经有一个请求在处理中了，等它结束再说", "DangerBrush");
            return;
        }

        if (IsHost)
        {
            var opponentId = OtherSeatMemberId(LocalMemberId);
            if (opponentId.Length == 0)
            {
                SetNotice("另一席上没有人，发不了请求", "DangerBrush");
                return;
            }
            _hostWaitingOpponentId = opponentId;
            _hostWaitingType = type;
            SetPendingWait(type, $"已请求{RequestLabel(type)}，等待对方回应…");
            SetNotice($"已向{MemberDisplayName(opponentId)}请求{RequestLabel(type)}", "TextSecondaryBrush");
            AppendNoticeChat($"你请求{RequestLabel(type)}");
            SendOnSession(s => s.SendRequestToAsync(opponentId, type, null));
            return;
        }

        SetPendingWait(type, $"已请求{RequestLabel(type)}，等待房主转达对方回应…");
        SetNotice($"已请求{RequestLabel(type)}，等待回应…", "TextSecondaryBrush");
        AppendNoticeChat($"你请求{RequestLabel(type)}");
        SendOnSession(s => s.SendRequestAsync(type));
    }

    // ---------------------------------------------------------------- 子类必须实现（游戏规则全在这里）

    /// <summary>先手位执哪边（"black" = 五子棋先手 / "red" = 象棋先手）。</summary>
    protected abstract string FirstSeatSide { get; }

    /// <summary>房主开房时默认坐哪一边（读游戏配置；默认坐先手位）。</summary>
    protected virtual string PreferredHostSeatSide => FirstSeatSide;

    /// <summary>某一边的对面（"black" ↔ "white"、"red" ↔ "black"）。</summary>
    protected abstract string OppositeSide(string side);

    /// <summary>边的显示名（"黑棋"/"白棋"/"红方"/"黑方"）。</summary>
    protected abstract string SideDisplayName(string side);

    /// <summary>当前对局状态快照（基类判断暂停 / 开始 / 自动轮换用）。</summary>
    protected abstract BoardGameStatus ReportGameStatus();

    /// <summary>回到「未开始」：重置棋盘、<c>Started=false</c>（开房 / 作废本局时由基类调用；不要写聊天）。</summary>
    protected abstract void ResetGameToWaiting();

    /// <summary>游戏状态（含 started / result / moves 等，具体结构由游戏自己定）。主机广播时用。</summary>
    protected abstract JsonElement BuildGameSnapshot();

    /// <summary>客户端收到包装状态后更新自己的棋局；<paramref name="mySide"/> 是本端执边（观战时为空字符串）。</summary>
    protected abstract void ApplyGameSnapshot(JsonElement game, string mySide);

    /// <summary>主机执行某个成员的动作（例如走棋）；执行完由子类自己调 <see cref="BroadcastSnapshot"/>。</summary>
    /// <param name="fromMemberId">发动作的成员 id。</param>
    /// <param name="fromSide">该成员执哪边（基类已按席位校验过）。</param>
    /// <param name="type">动作类型。</param>
    /// <param name="payload">动作数据。</param>
    protected abstract void OnHostAction(string fromMemberId, string fromSide, string type, JsonElement? payload);

    /// <summary>主机：撤销到「请求方该走」；返回 false 表示撤不了（会回 response(false)）。</summary>
    protected abstract bool ApplyUndoFor(string requesterSide);

    /// <summary>主机：判和。</summary>
    protected abstract void ApplyDraw();

    /// <summary>主机：某方认输。</summary>
    protected abstract void ApplyResign(string side);

    /// <summary>开始（下一）局：重置棋盘并 <c>Started=true</c>（联机与本地新局都走它）。</summary>
    /// <param name="swapSides">要不要换边（新版由席位安排决定，基类传 false）。</param>
    protected abstract void BeginNewGame(bool swapSides);

    /// <summary>开房成功（子类做提示 / 补充状态；棋局已在 <see cref="ResetGameToWaiting"/> 里重置）。</summary>
    protected virtual void OnHostRoomOpened() { }

    /// <summary>进入同机双人（子类重置棋局并开局）。</summary>
    protected virtual void OnLocalModeStarted() { }

    /// <summary>有人加入（主机 = 新客户端；客户端 = 自己加入成功）。</summary>
    protected virtual void OnPeerJoined() { }

    /// <summary>有人离开 / 掉线（参数是可读原因）。</summary>
    protected virtual void OnPeerLeft(string? reason) { }

    /// <summary>
    /// 断线后恢复连接（UI 线程）：客户端 = 自动重连成功并已应用最新状态；主机 = 掉线的玩家重新连回来了。
    /// </summary>
    protected virtual void OnReconnected() { }

    /// <summary>玩法变化（Offline / Local / Host / Client 互相切换时调用，UI 线程）。</summary>
    protected virtual void OnModeChanged() { }

    /// <summary>客户端收到并应用完主机的状态包后调用（UI 线程）。</summary>
    protected virtual void OnSnapshotApplied() { }

    /// <summary>
    /// 席位变化 / 本端座位变化（UI 线程）：子类在这里刷新 <c>_mySide</c> / 能不能下棋之类的状态。
    /// <para>「应用状态包之后」也会触发一次（客户端靠它同步本端执边）。</para>
    /// </summary>
    protected virtual void OnSeatsChanged() { }

    /// <summary>成员列表变化（UI 线程）：子类刷新依赖在线人数 / 座位名字的界面状态。</summary>
    protected virtual void OnMembersChanged() { }

    /// <summary>开房 / 开始下一局前的校验（例如象棋摆棋有误），返回错误文案；null = 可以开。</summary>
    protected virtual string? ValidateHostStart() => null;

    /// <summary>「离开房间」前的确认钩子（UI 线程，只在房间在线时询问；默认直接同意）。</summary>
    protected virtual bool ConfirmLeaveRoom() => true;

    // ---------------------------------------------------------------- 供子类使用

    /// <summary>
    /// 主机：把「完整房间状态 + 当前棋局」广播给所有客户端（没有客户端时静默忽略）。
    /// <b>任何会改棋局 / 可影响别人的操作之后，都由子类调它</b>，客户端只从状态包更新。
    /// </summary>
    protected void BroadcastSnapshot() => BroadcastState();

    /// <summary>客户端：上报一个游戏动作（例如 <c>SendAction("move", new { x, y })</c>）。</summary>
    protected void SendAction(string type, object? payload = null)
    {
        var session = _session;
        if (session is null || !IsClient) return;
        SendOnSession(s => s.SendActionAsync(type, payload));
    }

    /// <summary>设置状态栏提示（<paramref name="brushKey"/> 用 App.xaml 资源名：TextSecondaryBrush / SuccessBrush / DangerBrush）。</summary>
    protected void SetNotice(string text, string brushKey = "TextSecondaryBrush") => Ui(() => SetNoticeInternal(text, brushKey));

    private void SetNoticeInternal(string text, string brushKey)
    {
        NoticeText = text ?? "";
        NoticeBrushKey = brushKey;
    }

    /// <summary>成员 id 对应的玩家名（找不到 / 空 id → "空位"）。</summary>
    protected string NameOfMember(string memberId)
    {
        var id = (memberId ?? "").Trim();
        if (id.Length == 0) return "空位";
        lock (_roomGate)
        {
            return _roomMembers.TryGetValue(id, out var member)
                ? (string.IsNullOrWhiteSpace(member.Name) ? id : member.Name.Trim())
                : "已离开的玩家";
        }
    }

    /// <summary>
    /// 游戏 VM 在 <c>RefreshAll()</c>（或任何会改棋局状态的地方）末尾调用：
    /// 基类判断「本局是否从进行中变成结束」，每局只处理一次（加分 / 自动轮换 / 广播 / 提示）。
    /// 客户端调用无副作用（非主机直接返回）。
    /// </summary>
    protected void NotifyGameFinishedIfNeeded()
    {
        if (!IsHost || _demoRoom) return;
        var status = CurrentGameStatus();
        if (!status.Over) return;
        if (_finishedNotified) return;
        _finishedNotified = true;
        HandleGameFinished(status);
    }

    // ---------------------------------------------------------------- 房间生命周期

    private async Task StartHostInternalAsync()
    {
        if (IsOnline)
        {
            SetNotice("已经在房间里了，先离开房间", "DangerBrush");
            return;
        }

        CancelReconnect();                               // 手动开房 = 放弃之前的重连
        ResetPauseState();
        var validation = ValidateHostStartSafe();
        if (!string.IsNullOrWhiteSpace(validation))
        {
            SetNotice(validation!, "DangerBrush");
            return;
        }

        IsRoomBusy = true;
        var session = new BoardRoomSession(GameId, LocalName);
        WireSession(session);
        _session = session;
        SetMode(BoardPlayMode.Host);                     // 先切模式，握手事件回来时钩子看到的就是正确状态
        PrepareHostRoom();
        RaiseAllDerived();
        try
        {
            var error = await session.StartHostAsync(ListenPort, BuildWelcomeState);
            if (error.Length > 0)
            {
                _session = null;
                SetMode(BoardPlayMode.Offline);
                ClearRoomState();
                await DisposeSessionAsync(session);
                SetNotice($"开房间失败：{error}", "DangerBrush");
                return;
            }

            RebuildShareRows();
            // 开房瞬间 PrepareHostRoom 里 IsOnline 还没就绪，成员行的「你」标记 / 席位下拉的「（你）」
            // 会是旧值；监听成功后再同步一次，保证开房后的第一帧就是正确身份。
            SyncMembersView();
            RebuildSeatChoices();
            SetNotice($"房间已开（真实端口 {session.ListenPort}）"
                      + (session.IsDualStack ? "，IPv4 与 IPv6 都能连" : "")
                      + "。把下面的地址发给别人", "SuccessBrush");
            AnnounceSystemChat($"房间已开：监听 {session.ListenText}，最多 {BoardRoomSession.MaxMembers} 人");
            RaiseAllDerived();
            SafeHook(OnSeatsChanged);                 // 在线后本端执边才算得出来（房主默认坐席）
            SafeHook(OnHostRoomOpened);
        }
        catch (Exception ex)
        {
            _session = null;
            SetMode(BoardPlayMode.Offline);
            ClearRoomState();
            await DisposeSessionAsync(session);
            SetNotice($"开房间失败：{RoomLink.Describe(ex)}", "DangerBrush");
        }
        finally
        {
            IsRoomBusy = false;
        }
    }

    /// <summary>开房：搭好权威房间模型（房主入成员表、按偏好坐席、重置棋局、读回自动轮换设置）。</summary>
    private void PrepareHostRoom()
    {
        ClearPending();
        _relayPending.Clear();
        _clientOnline = false;
        _lastMemberId = "";
        lock (_roomGate)
        {
            _roomMembers.Clear();
            _roomMembers[BoardRoomSession.HostMemberId] = new BoardMemberInfo
            {
                MemberId = BoardRoomSession.HostMemberId,
                Name = LocalName,
                IsHost = true,
                IsOnline = true,
                GamesPlayed = 0,
                JoinOrder = 0,
            };
            _fallbackJoinOrder = 0;
            _autoRotateMode = BoardAutoModes.ParseRotate(_roomCfg?.AutoRotateMode);
            _autoPickMode = BoardAutoModes.ParsePick(_roomCfg?.AutoPickMode);
            _seatOneMemberId = "";
            _seatTwoMemberId = "";
        }

        // 房主默认坐席：按游戏的「房主执子」偏好（先手位 / 后手位）
        var firstSide = SafeGetSide(() => FirstSeatSide);
        var preferred = SafeGetSide(() => PreferredHostSeatSide);
        if (preferred.Length == 0) preferred = firstSide;
        lock (_roomGate)
        {
            if (firstSide.Length > 0 && !string.Equals(preferred, firstSide, StringComparison.Ordinal))
                _seatTwoMemberId = BoardRoomSession.HostMemberId;
            else
                _seatOneMemberId = BoardRoomSession.HostMemberId;
        }

        _finishedNotified = false;
        _hasPlayedGame = false;
        ResetAbortConfirm();
        try { ResetGameToWaiting(); }
        catch (Exception ex) { SetNoticeInternal($"重置棋局失败：{ex.Message}", "DangerBrush"); }
        CacheGameSnapshot();
        SyncMembersView();
        RebuildSeatChoices();
        RaiseAllDerived();
        SafeHook(OnSeatsChanged);
        SafeHook(OnMembersChanged);
    }

    private async Task JoinRoomInternalAsync()
    {
        if (IsOnline)
        {
            SetNotice("已经在房间里了，先离开房间", "DangerBrush");
            return;
        }

        var address = JoinAddress.Trim();
        if (address.Length == 0)
        {
            SetNotice("请先填写主机地址（例如 192.168.1.5:5310）", "DangerBrush");
            return;
        }

        CancelReconnect();                               // 手动加入 = 放弃之前的重连
        ResetPauseState();
        IsRoomBusy = true;
        var session = new BoardRoomSession(GameId, LocalName);
        WireSession(session);
        _session = session;
        SetMode(BoardPlayMode.Client);
        RaiseAllDerived();
        try
        {
            var error = await session.ConnectAsync(address);
            if (error.Length > 0)
            {
                _session = null;
                SetMode(BoardPlayMode.Offline);
                ClearRoomState();
                await DisposeSessionAsync(session);
                SetNotice($"加入房间失败：{error}", "DangerBrush");
                return;
            }

            _clientOnline = true;
            _lastMemberId = session.LocalMemberId;
            RebuildShareRows();
            SetNotice("已加入房间，等房主安排上场", "SuccessBrush");
            AnnounceSystemChat("已加入房间");
            RaiseAllDerived();
        }
        catch (Exception ex)
        {
            _session = null;
            SetMode(BoardPlayMode.Offline);
            ClearRoomState();
            await DisposeSessionAsync(session);
            SetNotice($"加入房间失败：{RoomLink.Describe(ex)}", "DangerBrush");
        }
        finally
        {
            IsRoomBusy = false;
        }
    }

    private async Task LeaveRoomInternalAsync()
    {
        if (_demoRoom)
        {
            SetNotice("这是演示房间（YLT_BOARD_DEMO_ROOM），不能离开", "TextSecondaryBrush");
            return;
        }

        var wasReconnecting = IsReconnecting;

        // 在线时给子类一次确认机会（例如「再点一次确认」）；重连中是「停止重连」，不再问
        if (!wasReconnecting && IsOnline)
        {
            bool confirmed;
            try { confirmed = ConfirmLeaveRoom(); }
            catch { confirmed = true; }
            if (!confirmed) return;
        }

        CancelReconnect();                               // 点「离开房间 / 停止重连」= 立刻放弃自动重连
        ResetPauseState();
        if (_isRoomBusy) return;

        IsRoomBusy = true;
        var session = _session;
        _session = null;
        try
        {
            SetMode(BoardPlayMode.Offline);
            ClearRoomState();
            if (wasReconnecting)
            {
                SetNotice("已停止自动重连，回到离线状态", "TextSecondaryBrush");
                AnnounceSystemChat("已停止自动重连");
            }
            else
            {
                SetNotice("已离开房间", "TextSecondaryBrush");
                AnnounceSystemChat("你离开了房间");
            }
            if (session is not null)
            {
                try { await session.StopAsync("本端离开了房间"); } catch { /* 已经断了 */ }
                await DisposeSessionAsync(session);
            }
        }
        finally
        {
            IsRoomBusy = false;
        }
    }

    private void StartLocalMode()
    {
        if (IsOnline)
        {
            SetNotice("还在房间里，先离开房间再进入同机双人", "DangerBrush");
            return;
        }

        var wasReconnecting = IsReconnecting;
        CancelReconnect();                               // 切同机双人 = 放弃自动重连
        ResetPauseState();
        var session = _session;
        _session = null;
        if (session is not null) SendOnSessionCore(session, s => s.StopAsync("切换到同机双人"));

        SetMode(BoardPlayMode.Local);
        ClearRoomState();
        ResetChat();
        SetNotice(wasReconnecting ? "已停止重连，进入同机双人" : "已进入同机双人：两个人轮流用这一台电脑下棋", "SuccessBrush");
        AnnounceSystemChat("已进入同机双人");
        SafeHook(OnLocalModeStarted);
    }

    /// <summary>清空房间模型（离开 / 切模式 / 开房失败时用）：成员、席位、待处理请求、下拉。</summary>
    private void ClearRoomState()
    {
        lock (_roomGate)
        {
            _roomMembers.Clear();
            _seatOneMemberId = "";
            _seatTwoMemberId = "";
            _autoRotateMode = BoardAutoModes.ParseRotate(_roomCfg?.AutoRotateMode);
            _autoPickMode = BoardAutoModes.ParsePick(_roomCfg?.AutoPickMode);
        }
        _clientOnline = false;
        _lastMemberId = "";
        _finishedNotified = false;
        _hasPlayedGame = false;
        ResetAbortConfirm();
        ClearPending();
        _relayPending.Clear();
        _lastGameSnapshot = default;
        HasPeer = false;
        PeerName = "";
        RebuildShareRows();
        SyncMembersView();
        RebuildSeatChoices();
        RaiseAllDerived();
    }

    // ---------------------------------------------------------------- 会话事件接线

    private void WireSession(BoardRoomSession session)
    {
        _clientOnline = false;
        session.StateChanged += () => OnSessionStateChanged(session);

        // 主机事件（网络线程：同步更新权威模型！界面随后 Ui 刷新）
        session.MemberJoined += info => OnHostMemberJoined(session, info);
        session.MemberLeft += (info, kind, reason) => OnHostMemberLeft(session, info, kind, reason);
        session.MemberReconnected += info => OnHostMemberReconnected(session, info);
        session.MemberUpdated += info => OnHostMemberUpdated(session, info);

        session.ChatReceived += (memberId, name, text, isSystem) =>
            UiForSession(session, () => OnChatReceived(memberId, name, text, isSystem));
        session.RequestReceived += (fromMemberId, fromName, type, payload) =>
            UiForSession(session, () => HandleIncomingRequest(fromMemberId, fromName, type, payload));
        session.ResponseReceived += (fromMemberId, type, accept, payload) =>
            UiForSession(session, () => HandleIncomingResponse(fromMemberId, type, accept));
        session.ActionReceived += (memberId, type, payload) =>
            UiForSession(session, () => HandleRemoteAction(memberId, type, payload));
        session.StateReceived += wrapped => UiForSession(session, () => ApplyStateEnvelope(wrapped));
    }

    /// <summary>只在「事件来自当前这一次会话」时才在 UI 线程执行（旧会话的余波全部丢掉）。</summary>
    private void UiForSession(BoardRoomSession session, Action action)
    {
        Ui(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            try { action(); }
            catch (Exception ex)
            {
                // 子类钩子 / 我们的处理抛异常不能把联机流程弄断
                SetNoticeInternal($"房间操作出错：{ex.Message}", "DangerBrush");
            }
        });
    }

    /// <summary>会话状态变化（后台线程）：主机只刷新界面；客户端在这里发现「断线了」并决定要不要重连。</summary>
    private void OnSessionStateChanged(BoardRoomSession session)
    {
        if (!ReferenceEquals(_session, session)) return;

        if (session.IsHost)
        {
            UiForSession(session, RaiseAllDerived);
            return;
        }

        if (session.IsClientConnected)
        {
            _clientOnline = true;
            UiForSession(session, RaiseAllDerived);
            return;
        }

        if (!_clientOnline) return;                      // 本来就没连上（开房/重连尝试中），不重复处理
        _clientOnline = false;
        var kind = session.LastDisconnectKind;
        var reason = session.LastDisconnectReason;
        UiForSession(session, () => HandleClientDisconnected(session, kind, reason));
    }

    // ---------------------------------------------------------------- 主机：成员事件（同步改权威模型）

    private void OnHostMemberJoined(BoardRoomSession session, BoardMemberInfo info)
    {
        if (!ReferenceEquals(_session, session)) return;
        UpsertMember(info, online: true);

        // 网络线程：先不要广播（welcome 会带最新状态给新人），界面刷新与广播都排到 UI 线程
        UiForSession(session, () =>
        {
            SyncMembersView();
            RebuildSeatChoices();
            RaiseAllDerived();
            var name = NameOfMember(info.MemberId);
            var text = $"{name} 加入了房间（{Members.Count} / {BoardRoomSession.MaxMembers} 人）";
            SetNoticeInternal(text, "SuccessBrush");
            AddChatItem(BoardChatItem.System(text));
            HostBroadcastSystemChat(text);
            SafeHook(OnPeerJoined);
            SafeHook(OnMembersChanged);
            BroadcastState();
        });
    }

    private void OnHostMemberLeft(BoardRoomSession session, BoardMemberInfo info, BoardDisconnectKind kind, string reason)
    {
        if (!ReferenceEquals(_session, session)) return;

        var byBye = kind == BoardDisconnectKind.PeerBye;
        var name = "";
        var seatChanged = false;
        lock (_roomGate)
        {
            if (_roomMembers.TryGetValue(info.MemberId, out var member))
            {
                name = string.IsNullOrWhiteSpace(member.Name) ? info.MemberId : member.Name;
                if (byBye)
                {
                    _roomMembers.Remove(info.MemberId);
                    if (string.Equals(_seatOneMemberId, info.MemberId, StringComparison.Ordinal))
                    {
                        _seatOneMemberId = "";
                        seatChanged = true;
                    }
                    if (string.Equals(_seatTwoMemberId, info.MemberId, StringComparison.Ordinal))
                    {
                        _seatTwoMemberId = "";
                        seatChanged = true;
                    }
                }
                else
                {
                    member.IsOnline = false;
                }
            }
        }

        UiForSession(session, () =>
        {
            ClearPendingForMember(info.MemberId);
            SyncMembersView();
            RebuildSeatChoices();
            RaiseAllDerived();
            var text = byBye
                ? $"{name} 离开了房间"
                : $"{name} 掉线了，席位保留，等他重连（{(string.IsNullOrWhiteSpace(reason) ? "网络断开" : reason)}）";
            SetNoticeInternal(text, byBye ? "TextSecondaryBrush" : "DangerBrush");
            AddChatItem(BoardChatItem.System(text));
            HostBroadcastSystemChat(text);
            SafeHook(() => OnPeerLeft(text));
            SafeHook(OnMembersChanged);
            if (seatChanged) SafeHook(OnSeatsChanged);
            BroadcastState();
        });
    }

    private void OnHostMemberReconnected(BoardRoomSession session, BoardMemberInfo info)
    {
        if (!ReferenceEquals(_session, session)) return;
        UpsertMember(info, online: true);

        UiForSession(session, () =>
        {
            SyncMembersView();
            RebuildSeatChoices();
            RaiseAllDerived();
            var name = NameOfMember(info.MemberId);
            var text = $"{name} 重新连接了";
            SetNoticeInternal(text + (IsGamePaused ? "，对局继续" : ""), "SuccessBrush");
            AddChatItem(BoardChatItem.System(text));
            HostBroadcastSystemChat(text);
            SafeHook(OnReconnected);
            SafeHook(OnMembersChanged);
            if (IsSeatedPlayerOnly(info.MemberId) || !IsGameRunning) SafeHook(OnSeatsChanged);
            BroadcastState();
        });
    }

    private void OnHostMemberUpdated(BoardRoomSession session, BoardMemberInfo info)
    {
        if (!ReferenceEquals(_session, session)) return;
        UpsertMember(info, online: info.IsOnline);
        UiForSession(session, () =>
        {
            SyncMembersView();
            RaiseAllDerived();
            BroadcastState();
        });
    }

    /// <summary>把一个成员合并进权威成员表（已存在则保留「上场次数 / 进房顺序」）。</summary>
    private void UpsertMember(BoardMemberInfo info, bool online)
    {
        if (info is null) return;
        var id = (info.MemberId ?? "").Trim();
        if (id.Length == 0) return;
        lock (_roomGate)
        {
            if (_roomMembers.TryGetValue(id, out var existing))
            {
                if (!string.IsNullOrWhiteSpace(info.Name)) existing.Name = info.Name.Trim();
                existing.IsOnline = online;
                if (info.JoinOrder > 0) existing.JoinOrder = info.JoinOrder;
                return;
            }
            _roomMembers[id] = new BoardMemberInfo
            {
                MemberId = id,
                Name = string.IsNullOrWhiteSpace(info.Name) ? id : info.Name.Trim(),
                IsHost = false,
                IsOnline = online,
                GamesPlayed = Math.Max(0, info.GamesPlayed),
                JoinOrder = info.JoinOrder > 0 ? info.JoinOrder : ++_fallbackJoinOrder,
            };
        }
    }

    private void UpdateHostMemberName(string name)
    {
        lock (_roomGate)
        {
            if (_roomMembers.TryGetValue(BoardRoomSession.HostMemberId, out var host))
                host.Name = string.IsNullOrWhiteSpace(name) ? "房主" : name.Trim();
        }
        SyncMembersView();
        RaiseAllDerived();
    }

    // ---------------------------------------------------------------- 客户端断线 / 自动重连

    private void HandleClientDisconnected(BoardRoomSession session, BoardDisconnectKind kind, string reason)
    {
        var text = FriendlyClientDisconnectedText(kind, reason);
        ClearPending();
        _relayPending.Clear();

        if (kind is BoardDisconnectKind.PeerBye or BoardDisconnectKind.LocalLeave)
        {
            // 房主关房 / 被移出房间：不重连，直接回离线
            _session = null;
            SetMode(BoardPlayMode.Offline);
            _ = DisposeSessionAsync(session);
            ClearRoomState();
            SetNoticeInternal(text, "DangerBrush");
            AddChatItem(BoardChatItem.System(text));
            RaiseAllDerived();
            return;
        }

        // 网络问题：保留棋局，开始自动重连
        var address = JoinAddress.Trim();
        if (address.Length == 0)
        {
            _session = null;
            SetMode(BoardPlayMode.Offline);
            _ = DisposeSessionAsync(session);
            ClearRoomState();
            SetNoticeInternal("连接断开，而且没有可重试的房间地址，请手动加入房间", "DangerBrush");
            AddChatItem(BoardChatItem.System("连接断开，请手动加入房间"));
            RaiseAllDerived();
            return;
        }

        _session = null;
        _ = DisposeSessionAsync(session);
        RaiseAllDerived();
        StartReconnect(kind);
    }

    /// <summary>把客户端断线原因翻译成人话。</summary>
    private static string FriendlyClientDisconnectedText(BoardDisconnectKind kind, string? reason)
    {
        var tail = string.IsNullOrWhiteSpace(reason) ? "" : $"（{reason.Trim()}）";
        return kind switch
        {
            BoardDisconnectKind.PeerBye => $"房主关闭了房间{tail}",
            BoardDisconnectKind.LocalLeave => $"已离开房间{tail}",
            BoardDisconnectKind.PeerClosed => $"连接被断开{tail}",
            _ => $"网络连接中断{tail}",
        };
    }

    /// <summary>客户端意外断线：启动自动重连循环（2 秒一次，最多 30 次 / 60 秒）。</summary>
    private void StartReconnect(BoardDisconnectKind kind)
    {
        _ = kind;
        if (_closed) return;

        var address = JoinAddress.Trim();
        if (address.Length == 0)
        {
            SetMode(BoardPlayMode.Offline);
            RaiseAllDerived();
            SetNoticeInternal("连接断开，而且没有可重试的房间地址，请手动加入房间", "DangerBrush");
            return;
        }

        var generation = Interlocked.Increment(ref _reconnectGeneration);
        IsReconnecting = true;
        ReconnectStatusText = "连接断开，正在自动重连…";
        SetNoticeInternal("连接断开，正在自动重连…", "DangerBrush");
        AddChatItem(BoardChatItem.System("连接断开，正在自动重连…"));
        RaiseAllDerived();
        _ = ReconnectLoopAsync(address, generation);
    }

    private bool IsReconnectCanceled(int generation) => Volatile.Read(ref _reconnectGeneration) != generation;

    private async Task ReconnectLoopAsync(string address, int generation)
    {
        var startedTicks = Environment.TickCount64;
        var lastError = "";
        var attempt = 0;
        var previousMemberId = _lastMemberId;
        try
        {
            while (!IsReconnectCanceled(generation)
                   && attempt < ReconnectMaxAttempts
                   && Environment.TickCount64 - startedTicks < ReconnectWindowMs)
            {
                attempt++;
                await Task.Delay(ReconnectIntervalMs).ConfigureAwait(false);
                if (IsReconnectCanceled(generation)) return;

                var status = $"连接断开，正在重连（第 {attempt} 次 / 最多 {ReconnectMaxAttempts} 次）…";
                await UiRunAsync(() =>
                {
                    if (!IsReconnectCanceled(generation)) ReconnectStatusText = status;
                }).ConfigureAwait(false);
                if (IsReconnectCanceled(generation)) return;

                var session = new BoardRoomSession(GameId, LocalName);
                WireSession(session);
                await UiRunAsync(() =>
                {
                    if (!IsReconnectCanceled(generation)) _session = session;
                }).ConfigureAwait(false);
                if (IsReconnectCanceled(generation))
                {
                    await DisposeSessionAsync(session).ConfigureAwait(false);
                    return;
                }

                string error;
                try { error = await session.ConnectAsync(address, 0, previousMemberId).ConfigureAwait(false); }
                catch (Exception ex) { error = RoomLink.Describe(ex); }

                if (IsReconnectCanceled(generation))
                {
                    await UiRunAsync(() =>
                    {
                        if (ReferenceEquals(_session, session)) _session = null;
                    }).ConfigureAwait(false);
                    await DisposeSessionAsync(session).ConfigureAwait(false);
                    return;
                }

                if (error.Length == 0)
                {
                    var newMemberId = session.LocalMemberId;
                    await UiRunAsync(() =>
                    {
                        _lastMemberId = newMemberId.Length > 0 ? newMemberId : _lastMemberId;
                        if (ReferenceEquals(_session, session)) MarkReconnectSucceeded();
                    }).ConfigureAwait(false);
                    return;
                }

                lastError = error;
                await UiRunAsync(() =>
                {
                    if (ReferenceEquals(_session, session)) _session = null;
                    if (!IsReconnectCanceled(generation))
                        ReconnectStatusText = $"连接断开，正在重连（第 {attempt} 次 / 最多 {ReconnectMaxAttempts} 次）…"
                                              + $"上次失败：{error}";
                }).ConfigureAwait(false);
                await DisposeSessionAsync(session).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            lastError = RoomLink.Describe(ex);
        }

        if (IsReconnectCanceled(generation)) return;
        await UiRunAsync(() => GiveUpReconnect(lastError, address)).ConfigureAwait(false);
    }

    /// <summary>重连成功：清掉重连 / 暂停状态并提示「已重新连接，继续对局」。</summary>
    private void MarkReconnectSucceeded()
    {
        _clientOnline = true;
        IsReconnecting = false;
        ReconnectStatusText = "";
        SetNoticeInternal("已重新连接，继续对局", "SuccessBrush");
        AddChatItem(BoardChatItem.System("已重新连接，继续对局"));
        RaiseAllDerived();
        SafeHook(OnReconnected);
    }

    /// <summary>60 秒 / 30 次仍失败：停止自动重试，回到离线但保留加入地址。</summary>
    private void GiveUpReconnect(string lastError, string address)
    {
        if (IsReconnectCanceled(_reconnectGeneration)) return;
        _session = null;
        SetMode(BoardPlayMode.Offline);
        IsReconnecting = false;
        ReconnectStatusText = "";
        ClearRoomState();
        var tail = lastError.Length > 0 ? $"（最后一次错误：{lastError}）" : "";
        SetNoticeInternal($"自动重连失败，请手动点「加入房间」{tail}；地址已保留：{address}", "DangerBrush");
        AddChatItem(BoardChatItem.System("自动重连失败，请手动点「加入房间」"));
        RaiseAllDerived();
    }

    /// <summary>取消正在跑的重连循环（用户离开 / 切同机双人 / 重新开房 / 关工具时调）。</summary>
    private void CancelReconnect()
    {
        Interlocked.Increment(ref _reconnectGeneration);
        if (!IsReconnecting && _reconnectStatusText.Length == 0) return;
        _isReconnecting = false;
        _reconnectStatusText = "";
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(ReconnectStatusText));
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
        InvalidateCommands();
    }

    /// <summary>清掉「暂停 / 重连」两个状态位（手动开房、加入、离开、切同机双人时调）。</summary>
    private void ResetPauseState()
    {
        _isReconnecting = false;
        _reconnectStatusText = "";
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(ReconnectStatusText));
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
    }

    // ---------------------------------------------------------------- 状态包（广播 / 应用）

    /// <summary>把当前棋局对象拍成快照缓存起来（只在 UI 线程调；stateFactory 在网络线程只读缓存）。</summary>
    private void CacheGameSnapshot()
    {
        try { _lastGameSnapshot = BuildGameSnapshot(); }
        catch { /* 保留上一份，别让一个坏状态把房间搞崩 */ }
    }

    /// <summary>
    /// 拼一个完整状态包装（房间权威模型 + 缓存的棋局快照）。
    /// <b>可能在网络线程被 stateFactory 调用</b>：这里只读加锁的模型和缓存，不碰 UI / 游戏对象。
    /// </summary>
    private JsonElement BuildWelcomeState()
    {
        try
        {
            var seq = Interlocked.Increment(ref _stateSeq);
            var room = BuildRoomState();
            return BoardProtocol.WrapState(seq, room, _lastGameSnapshot);
        }
        catch
        {
            return default;
        }
    }

    /// <summary>从权威模型复制一份房间状态（加锁；按进房顺序排好）。</summary>
    private BoardRoomState BuildRoomState()
    {
        var state = new BoardRoomState
        {
            SeatOneMemberId = "",
            SeatTwoMemberId = "",
            MaxMembers = BoardRoomSession.MaxMembers,
        };
        lock (_roomGate)
        {
            state.SeatOneMemberId = _seatOneMemberId;
            state.SeatTwoMemberId = _seatTwoMemberId;
            state.AutoRotateMode = BoardAutoModes.ToKey(_autoRotateMode);
            state.AutoPickMode = BoardAutoModes.ToKey(_autoPickMode);
            foreach (var member in _roomMembers.Values
                         .OrderBy(m => m.JoinOrder)
                         .ThenBy(m => m.MemberId, StringComparer.Ordinal))
            {
                state.Members.Add(member.Clone());
            }
        }
        return state;
    }

    /// <summary>主机：广播完整状态（房间 + 棋局）。</summary>
    private void BroadcastState()
    {
        if (!IsHost || !IsOnline) return;
        var session = _session;
        if (session is null) return;                     // demo 房间 / 尚未开房成功

        CacheGameSnapshot();
        JsonElement wrapped;
        try { wrapped = BuildWelcomeState(); }
        catch (Exception ex)
        {
            SetNoticeInternal($"生成房间状态失败：{ex.Message}", "DangerBrush");
            return;
        }
        SendOnSession(s => s.BroadcastStateAsync(wrapped));
    }

    /// <summary>客户端：应用主机广播的完整状态。</summary>
    private void ApplyStateEnvelope(JsonElement wrapped)
    {
        var envelope = BoardProtocol.ReadState(wrapped);
        if (envelope is null)
        {
            SetNoticeInternal("收到一个看不懂的房间状态，已忽略", "DangerBrush");
            return;
        }

        if (envelope.Seq > 0)
        {
            if (envelope.Seq < _stateSeq) return;        // 乱序的旧包丢掉
            _stateSeq = envelope.Seq;
        }

        var seatChanged = false;
        var membersChanged = false;
        if (envelope.Room is { } room)
            ApplyRoomState(room, out seatChanged, out membersChanged);

        try
        {
            if (envelope.Game is { } game && game.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                ApplyGameSnapshot(game, LocalSide);
        }
        catch (Exception ex)
        {
            SetNoticeInternal($"同步棋局失败：{ex.Message}", "DangerBrush");
        }

        _lastMemberId = _session?.LocalMemberId ?? _lastMemberId;
        SyncMembersView();
        RebuildSeatChoices();
        if (seatChanged) SafeHook(OnSeatsChanged);
        if (membersChanged) SafeHook(OnMembersChanged);
        ClearPendingWait();
        RaiseAllDerived();
        SafeHook(OnSnapshotApplied);
    }

    /// <summary>把状态包里的房间部分写进本地模型；返回席位 / 成员列表有没有变。</summary>
    private void ApplyRoomState(BoardRoomState room, out bool seatChanged, out bool membersChanged)
    {
        var oldSignature = MemberSignature();
        var oldOne = _seatOneMemberId;
        var oldTwo = _seatTwoMemberId;
        var oldLocalSide = LocalSide;
        var oldRotate = _autoRotateMode;
        var oldPick = _autoPickMode;

        lock (_roomGate)
        {
            _roomMembers.Clear();
            foreach (var member in room.Members ?? new List<BoardMemberInfo>())
            {
                if (member is null) continue;
                var id = (member.MemberId ?? "").Trim();
                if (id.Length == 0) continue;
                var isHost = member.IsHost || string.Equals(id, BoardRoomSession.HostMemberId, StringComparison.Ordinal);
                _roomMembers[id] = new BoardMemberInfo
                {
                    MemberId = id,
                    Name = string.IsNullOrWhiteSpace(member.Name) ? id : member.Name.Trim(),
                    IsHost = isHost,
                    IsOnline = member.IsOnline,
                    GamesPlayed = Math.Max(0, member.GamesPlayed),
                    JoinOrder = member.JoinOrder,
                };
                if (member.GamesPlayed > 0) _hasPlayedGame = true;
            }
            _seatOneMemberId = (room.SeatOneMemberId ?? "").Trim();
            _seatTwoMemberId = (room.SeatTwoMemberId ?? "").Trim();
            _autoRotateMode = BoardAutoModes.ParseRotate(room.AutoRotateMode);
            _autoPickMode = BoardAutoModes.ParsePick(room.AutoPickMode);
        }

        seatChanged = !string.Equals(oldOne, _seatOneMemberId, StringComparison.Ordinal)
                      || !string.Equals(oldTwo, _seatTwoMemberId, StringComparison.Ordinal)
                      || !string.Equals(oldLocalSide, LocalSide, StringComparison.Ordinal)
                      || oldRotate != _autoRotateMode
                      || oldPick != _autoPickMode;
        membersChanged = !string.Equals(oldSignature, MemberSignature(), StringComparison.Ordinal);
    }

    /// <summary>成员列表的「指纹」：判断状态包有没有真的改成员列表。</summary>
    private string MemberSignature()
    {
        lock (_roomGate)
        {
            var parts = _roomMembers.Values
                .OrderBy(m => m.JoinOrder)
                .ThenBy(m => m.MemberId, StringComparer.Ordinal)
                .Select(m => $"{m.MemberId}:{m.Name}:{m.IsOnline}:{m.GamesPlayed}:{m.JoinOrder}");
            return string.Join("|", parts);
        }
    }

    // ---------------------------------------------------------------- 成员列表 / 席位下拉重建

    /// <summary>把权威模型同步到 <see cref="Members"/>（原地更新，尽量不重建行对象）。</summary>
    private void SyncMembersView()
    {
        var source = new List<BoardMemberInfo>();
        lock (_roomGate)
        {
            foreach (var member in _roomMembers.Values
                         .OrderBy(m => m.JoinOrder)
                         .ThenBy(m => m.MemberId, StringComparer.Ordinal))
            {
                source.Add(member.Clone());
            }
        }

        var target = new List<BoardMemberItem>(source.Count);
        var alive = new HashSet<string>(StringComparer.Ordinal);
        var me = LocalMemberId;
        var oneId = _seatOneMemberId;
        var twoId = _seatTwoMemberId;
        foreach (var info in source)
        {
            alive.Add(info.MemberId);
            if (!_memberItems.TryGetValue(info.MemberId, out var item))
            {
                item = new BoardMemberItem { MemberId = info.MemberId };
                _memberItems[info.MemberId] = item;
            }
            var seat = string.Equals(info.MemberId, oneId, StringComparison.Ordinal) ? "first"
                : string.Equals(info.MemberId, twoId, StringComparison.Ordinal) ? "second"
                : "";
            item.Apply(info.Name, info.IsHost || string.Equals(info.MemberId, BoardRoomSession.HostMemberId, StringComparison.Ordinal),
                string.Equals(info.MemberId, me, StringComparison.Ordinal),
                info.IsOnline, info.GamesPlayed, info.JoinOrder, seat);
            target.Add(item);
        }

        // 删掉已经不在房间里的行
        foreach (var id in _memberItems.Keys.Where(id => !alive.Contains(id)).ToList())
            _memberItems.Remove(id);

        // 按目标顺序原地同步 ObservableCollection
        for (var i = 0; i < target.Count; i++)
        {
            if (i < Members.Count && ReferenceEquals(Members[i], target[i])) continue;
            var existing = Members.IndexOf(target[i]);
            if (existing >= 0) Members.Move(existing, i);
            else Members.Insert(i, target[i]);
        }
        while (Members.Count > target.Count) Members.RemoveAt(Members.Count - 1);

        RefreshPeerName();
        OnPropertyChanged(nameof(MemberCountText));
    }

    /// <summary>重建两个席位下拉（成员 + 空位），并保持当前选中（不触发 setter 逻辑）。</summary>
    private void RebuildSeatChoices()
    {
        _suppressSeatSelection = true;
        try
        {
            var options = new List<BoardSeatOption> { BoardSeatOption.Empty() };
            List<BoardMemberInfo> members;
            lock (_roomGate)
            {
                members = _roomMembers.Values
                    .OrderBy(m => m.JoinOrder)
                    .ThenBy(m => m.MemberId, StringComparer.Ordinal)
                    .ToList();
            }
            var me = LocalMemberId;
            foreach (var member in members)
            {
                var name = string.IsNullOrWhiteSpace(member.Name) ? member.MemberId : member.Name.Trim();
                var suffix = "";
                if (string.Equals(member.MemberId, me, StringComparison.Ordinal)) suffix += "（你）";
                if (!member.IsOnline) suffix += "（离线）";
                options.Add(new BoardSeatOption(member.MemberId, name + suffix));
            }

            SeatOneChoices.Clear();
            SeatTwoChoices.Clear();
            foreach (var option in options)
            {
                SeatOneChoices.Add(option);
                SeatTwoChoices.Add(option);
            }
            OnPropertyChanged(nameof(SelectedSeatOneOption));
            OnPropertyChanged(nameof(SelectedSeatTwoOption));
        }
        finally
        {
            _suppressSeatSelection = false;
        }
    }

    private static BoardSeatOption? FindSeatOption(IEnumerable<BoardSeatOption> options, string memberId)
    {
        var id = (memberId ?? "").Trim();
        foreach (var option in options)
        {
            if (string.Equals(option.MemberId, id, StringComparison.Ordinal)) return option;
        }
        return null;
    }

    /// <summary>刷新兼容字段 <see cref="HasPeer"/> / <see cref="PeerName"/>（多客户端时取「席上对手」或第一个在线成员）。</summary>
    private void RefreshPeerName()
    {
        var name = "";
        bool hasPeer;
        var me = LocalMemberId;

        if (_demoRoom)
        {
            hasPeer = Members.Any(m => !m.IsHost && m.IsOnline);
            name = Members.FirstOrDefault(m => !m.IsHost && m.IsOnline)?.Name ?? "";
        }
        else if (IsClient)
        {
            hasPeer = IsOnline;
            name = HostMemberName;
        }
        else if (IsHost)
        {
            string? opponentId = me.Length > 0 ? OtherSeatMemberId(me) : null;
            var opponent = opponentId is { Length: > 0 } ? FindMemberSnapshot(opponentId) : null;
            if (opponent is { IsOnline: true }) name = opponent.Name;
            else
            {
                var firstOnline = FindFirstOnlineMember();
                name = firstOnline?.Name ?? "";
            }
            hasPeer = FindFirstOnlineMember() is not null;
        }
        else
        {
            hasPeer = false;
        }

        _peerName = name;
        OnPropertyChanged(nameof(PeerName));
        if (_hasPeer != hasPeer)
        {
            _hasPeer = hasPeer;
            OnPropertyChanged(nameof(HasPeer));
        }
    }

    private BoardMemberInfo? FindMemberSnapshot(string memberId)
    {
        lock (_roomGate)
        {
            return _roomMembers.TryGetValue(memberId, out var member) ? member.Clone() : null;
        }
    }

    private BoardMemberInfo? FindFirstOnlineMember()
    {
        lock (_roomGate)
        {
            return _roomMembers.Values
                .Where(m => !m.IsHost && m.IsOnline)
                .OrderBy(m => m.JoinOrder)
                .FirstOrDefault()?
                .Clone();
        }
    }

    /// <summary>客户端视角的房主名字。</summary>
    private string HostMemberName
    {
        get
        {
            lock (_roomGate)
            {
                return _roomMembers.TryGetValue(BoardRoomSession.HostMemberId, out var host) && !string.IsNullOrWhiteSpace(host.Name)
                    ? host.Name.Trim()
                    : "房主";
            }
        }
    }

    private bool IsMemberOnline(string memberId)
    {
        if (_demoRoom) return true;
        lock (_roomGate)
        {
            return _roomMembers.TryGetValue(memberId, out var member) && member.IsOnline;
        }
    }

    private string MemberDisplayName(string memberId)
    {
        var id = (memberId ?? "").Trim();
        if (id.Length == 0) return "空位";
        lock (_roomGate)
        {
            if (!_roomMembers.TryGetValue(id, out var member)) return "已离开的玩家";
            return string.IsNullOrWhiteSpace(member.Name) ? id : member.Name.Trim();
        }
    }

    private bool IsSeatedPlayerOnly(string memberId)
    {
        var id = (memberId ?? "").Trim();
        return id.Length > 0
               && (string.Equals(id, _seatOneMemberId, StringComparison.Ordinal)
                   || string.Equals(id, _seatTwoMemberId, StringComparison.Ordinal));
    }

    private string OtherSeatMemberId(string memberId)
    {
        var id = (memberId ?? "").Trim();
        if (id.Length == 0) return "";
        if (string.Equals(id, _seatOneMemberId, StringComparison.Ordinal)) return _seatTwoMemberId;
        if (string.Equals(id, _seatTwoMemberId, StringComparison.Ordinal)) return _seatOneMemberId;
        return "";
    }

    private string SideOfMember(string memberId)
    {
        var id = (memberId ?? "").Trim();
        if (id.Length == 0) return "";
        if (string.Equals(id, _seatOneMemberId, StringComparison.Ordinal)) return SeatOneSide;
        if (string.Equals(id, _seatTwoMemberId, StringComparison.Ordinal)) return SeatTwoSide;
        return "";
    }

    // ---------------------------------------------------------------- 对局生命周期（开始 / 作废 / 结束）

    private void StartNextGameFromUi()
    {
        if (!TryStartNextGameCore()) return;
        SetNotice("对局开始！", "SuccessBrush");
        AnnounceSystemChat($"房主开始了{(StartNextGameText == "开始下一局" ? "下一局" : "对局")}：{SeatOneName}（{SideDisplayName(SeatOneSide)}）vs {SeatTwoName}（{SideDisplayName(SeatTwoSide)}）");
        RaiseAllDerived();
    }

    /// <summary>开始（下一）局：校验 → BeginNewGame → 广播。成功返回 true。</summary>
    private bool TryStartNextGameCore()
    {
        if (!HostControlsVisible) return false;
        // 演示房间（YLT_BOARD_DEMO_ROOM）允许直接开始下一局，方便截图核对控制区；真实房间进行中要拦住
        if (!_demoRoom && IsGameRunning)
        {
            SetNoticeInternal("对局还在进行中：等它下完，或点「结束本局（作废）」", "DangerBrush");
            return false;
        }
        if (!SeatsReadyForStart(out var reason))
        {
            SetNoticeInternal(reason, "DangerBrush");
            return false;
        }
        var validation = ValidateHostStartSafe();
        if (!string.IsNullOrWhiteSpace(validation))
        {
            SetNoticeInternal(validation!, "DangerBrush");
            return false;
        }

        try { BeginNewGame(swapSides: false); }
        catch (Exception ex)
        {
            SetNoticeInternal($"开始对局失败：{ex.Message}", "DangerBrush");
            return false;
        }
        _finishedNotified = false;
        _hasPlayedGame = true;
        ResetAbortConfirm();
        ClearPending();
        _relayPending.Clear();
        CacheGameSnapshot();
        RaiseAllDerived();
        BroadcastState();
        return true;
    }

    /// <summary>「结束本局（作废）」：第一次点进入二次确认，3 秒内再点一次才作废。</summary>
    private void AbortGame()
    {
        if (!CanAbortGame) return;
        if (!_isAbortConfirming)
        {
            IsAbortConfirming = true;
            var generation = ++_abortConfirmGeneration;
            SetNotice("再点一次「结束本局（作废）」确认作废这一局", "DangerBrush");
            _ = AutoCancelAbortAsync(generation);
            return;
        }

        _abortConfirmGeneration++;
        IsAbortConfirming = false;
        try { ResetGameToWaiting(); }
        catch (Exception ex) { SetNoticeInternal($"作废本局失败：{ex.Message}", "DangerBrush"); return; }
        _finishedNotified = false;
        _hasPlayedGame = true;
        ClearPending();
        _relayPending.Clear();
        CacheGameSnapshot();
        RaiseAllDerived();
        BroadcastState();
        SafeHook(OnSeatsChanged);
        SetNotice("本局已作废，可以重新安排后再开始", "TextSecondaryBrush");
        AnnounceSystemChat("本局作废（房主结束了这一局）");
    }

    private async Task AutoCancelAbortAsync(int generation)
    {
        try { await Task.Delay(AbortConfirmMs).ConfigureAwait(false); }
        catch { return; }
        Ui(() =>
        {
            if (generation != _abortConfirmGeneration || !_isAbortConfirming) return;
            IsAbortConfirming = false;
            SetNoticeInternal("作废确认已取消（3 秒内没再点一次）", "TextSecondaryBrush");
        });
    }

    private void ResetAbortConfirm()
    {
        _abortConfirmGeneration++;
        if (_isAbortConfirming) IsAbortConfirming = false;
    }

    /// <summary>对局结束处理（主机，只在 <see cref="NotifyGameFinishedIfNeeded"/> 里调用一次）。</summary>
    private void HandleGameFinished(BoardGameStatus status)
    {
        _finishedNotified = true;
        _hasPlayedGame = true;

        string oneId, twoId;
        lock (_roomGate)
        {
            oneId = _seatOneMemberId;
            twoId = _seatTwoMemberId;
        }

        // 上场次数：完整打完一局 +1（作废 / 中途退出不算）
        if (oneId.Length > 0) AddGamePlayed(oneId);
        if (twoId.Length > 0) AddGamePlayed(twoId);
        SyncMembersView();

        var winnerSide = (status.WinnerSide ?? "").Trim();
        string? winnerId = null;
        string? loserId = null;
        if (winnerSide.Length > 0)
        {
            if (string.Equals(winnerSide, SeatOneSide, StringComparison.OrdinalIgnoreCase))
            {
                winnerId = oneId.Length > 0 ? oneId : null;
                loserId = twoId.Length > 0 ? twoId : null;
            }
            else if (string.Equals(winnerSide, SeatTwoSide, StringComparison.OrdinalIgnoreCase))
            {
                winnerId = twoId.Length > 0 ? twoId : null;
                loserId = oneId.Length > 0 ? oneId : null;
            }
        }

        var title = winnerId is not null
            ? $"本局结束：{MemberDisplayName(winnerId)}（{SideDisplayName(winnerSide)}）获胜"
            : "本局结束：平局";

        ClearPending();
        _relayPending.Clear();

        BoardAutoRotateMode rotate;
        BoardAutoPickMode pick;
        lock (_roomGate)
        {
            rotate = _autoRotateMode;
            pick = _autoPickMode;
        }

        if (rotate == BoardAutoRotateMode.Manual)
        {
            SetNoticeInternal($"{title}。房主可安排下一局上场选手", "TextSecondaryBrush");
            AnnounceSystemChat($"{title}。房主可安排下一局上场选手");
            CacheGameSnapshot();
            BroadcastState();
            RaiseAllDerived();
            return;
        }

        var result = BoardRotationPlanner.Plan(
            rotate, pick, oneId, twoId, winnerId, loserId, BuildRotationCandidates());

        if (result.Changed)
        {
            lock (_roomGate)
            {
                _seatOneMemberId = result.SeatOneMemberId;
                _seatTwoMemberId = result.SeatTwoMemberId;
            }
            RebuildSeatChoices();
            SyncMembersView();
            RaiseAllDerived();
            SafeHook(OnSeatsChanged);
            SetNoticeInternal($"{title}。自动安排下一局：{result.Reason}。房主点「开始下一局」开始", "SuccessBrush");
            AnnounceSystemChat($"{title}。自动安排下一局：{result.Reason}。等房主开始下一局");
        }
        else
        {
            SetNoticeInternal($"{title}。{result.Reason}。房主可手动调整后开始下一局", "TextSecondaryBrush");
            AnnounceSystemChat($"{title}。{result.Reason}");
        }
        CacheGameSnapshot();
        BroadcastState();
        RaiseAllDerived();
    }

    /// <summary>给自动轮换用的候选成员（含当前场上的两人，planner 内部自己筛）。</summary>
    private IReadOnlyList<BoardRotationCandidate> BuildRotationCandidates()
    {
        var list = new List<BoardRotationCandidate>();
        lock (_roomGate)
        {
            foreach (var member in _roomMembers.Values
                         .OrderBy(m => m.JoinOrder)
                         .ThenBy(m => m.MemberId, StringComparer.Ordinal))
            {
                list.Add(new BoardRotationCandidate(
                    member.MemberId,
                    string.IsNullOrWhiteSpace(member.Name) ? member.MemberId : member.Name.Trim(),
                    member.IsOnline,
                    member.GamesPlayed,
                    member.JoinOrder));
            }
        }
        return list;
    }

    private void AddGamePlayed(string memberId)
    {
        var id = (memberId ?? "").Trim();
        if (id.Length == 0) return;
        lock (_roomGate)
        {
            if (_roomMembers.TryGetValue(id, out var member)) member.GamesPlayed = Math.Max(0, member.GamesPlayed) + 1;
        }
    }

    // ---------------------------------------------------------------- 请求路由

    private void HandleIncomingRequest(string fromMemberId, string fromName, string type, JsonElement? payload)
    {
        if (string.IsNullOrWhiteSpace(type)) return;
        if (!IsOnline) return;
        if (IsHost) HandleHostIncomingRequest(fromMemberId, fromName, type, payload);
        else HandleClientIncomingRequest(fromMemberId, fromName, type, payload);
    }

    /// <summary>房主收到请求：认输直接判负；再来一局房主决定；悔棋 / 求和按「房主是玩家 or 裁判」路由。</summary>
    private void HandleHostIncomingRequest(string fromMemberId, string fromName, string type, JsonElement? payload)
    {
        _ = payload;
        var name = string.IsNullOrWhiteSpace(fromName) ? MemberDisplayName(fromMemberId) : fromName.Trim();

        // 房主不在这张客户端成员表里；请求来自客户端一定是客户端成员
        var requesterSide = SideOfMember(fromMemberId);
        if (requesterSide.Length == 0)
        {
            SetNoticeInternal($"{name} 不是场上的选手，请求已回绝", "DangerBrush");
            SendOnSession(s => s.SendResponseToAsync(fromMemberId, type, false));
            return;
        }

        // 认输：房主直接执行，不需要确认
        if (type == BoardRequestTypes.Resign)
        {
            try { ApplyResign(requesterSide); }
            catch (Exception ex) { SetNoticeInternal($"处理认输失败：{ex.Message}", "DangerBrush"); return; }
            var text = $"{name} 认输了，本局结束";
            SetNoticeInternal(text, "DangerBrush");
            AddChatItem(BoardChatItem.System(text));
            HostBroadcastSystemChat(text);
            CacheGameSnapshot();
            BroadcastState();
            SendOnSession(s => s.SendResponseToAsync(fromMemberId, type, true));
            RaiseAllDerived();
            return;
        }

        // 再来一局：房主决定（同意 = 开始下一局）
        if (type == BoardRequestTypes.Rematch)
        {
            if (_hasPendingPrompt)
            {
                SendOnSession(s => s.SendResponseToAsync(fromMemberId, type, false));
                SetNoticeInternal("已经有一个请求在等房主处理了，稍后再试", "DangerBrush");
                return;
            }
            _pendingRequestType = type;
            _pendingFromMemberId = fromMemberId;
            _pendingFromName = name;
            PendingPromptText = $"{name} 请求再来一局，是否开始下一局？";
            HasPendingPrompt = true;
            SetNoticeInternal(PendingPromptText, "TextSecondaryBrush");
            return;
        }

        // 悔棋 / 求和：对手是谁？
        var opponentId = OtherSeatMemberId(fromMemberId);
        if (opponentId.Length == 0 || !IsMemberOnline(opponentId))
        {
            SetNoticeInternal("对手不在线，请求已回绝", "DangerBrush");
            SendOnSession(s => s.SendResponseToAsync(fromMemberId, type, false));
            return;
        }

        if (string.Equals(opponentId, BoardRoomSession.HostMemberId, StringComparison.Ordinal))
        {
            // 房主本人就是对手：弹给房主确认
            if (_hasPendingPrompt)
            {
                SendOnSession(s => s.SendResponseToAsync(fromMemberId, type, false));
                SetNoticeInternal("已经有一个请求在等房主处理了，稍后再试", "DangerBrush");
                return;
            }
            _pendingRequestType = type;
            _pendingFromMemberId = fromMemberId;
            _pendingFromName = name;
            PendingPromptText = $"{name} 请求{RequestLabel(type)}，是否同意？";
            HasPendingPrompt = true;
            SetNoticeInternal(PendingPromptText, "TextSecondaryBrush");
            return;
        }

        // 房主是裁判（两席都是客户端）：把请求转给对手
        var relayKey = RelayKey(opponentId, type);
        if (_relayPending.ContainsKey(relayKey))
        {
            SendOnSession(s => s.SendResponseToAsync(fromMemberId, type, false));
            SetNoticeInternal("上一个同样的请求还没回应，稍后再试", "DangerBrush");
            return;
        }
        _relayPending[relayKey] = new PendingRelay(fromMemberId, name, type);
        SendOnSession(s => s.SendRequestToAsync(opponentId, type, new { fromName = name }));
        SetNoticeInternal($"已把 {name} 的{RequestLabel(type)}请求转达给 {MemberDisplayName(opponentId)}，等回应…", "TextSecondaryBrush");
    }

    /// <summary>客户端收到请求（房主本人发 / 房主转达）：弹确认，回给房主。</summary>
    private void HandleClientIncomingRequest(string fromMemberId, string fromName, string type, JsonElement? payload)
    {
        _ = payload;
        if (!IsSeatedPlayer)
        {
            // 观战者不该收到请求；防御性回绝
            SendOnSession(s => s.SendResponseAsync(type, false));
            return;
        }
        if (type == BoardRequestTypes.Resign)
        {
            SetNoticeInternal("房主通知：有人认输了，等待对局结束…", "TextSecondaryBrush");
            return;
        }
        if (_hasPendingPrompt)
        {
            SendOnSession(s => s.SendResponseAsync(type, false));
            return;
        }

        var name = string.IsNullOrWhiteSpace(fromName) || fromName == HostMemberName ? "房主" : fromName.Trim();
        _pendingRequestType = type;
        _pendingFromMemberId = fromMemberId;
        _pendingFromName = name;
        PendingPromptText = $"{name} 请求{RequestLabel(type)}，是否同意？";
        HasPendingPrompt = true;
        SetNoticeInternal(PendingPromptText, "TextSecondaryBrush");
    }

    private async Task AcceptPendingAsync()
    {
        if (!_hasPendingPrompt) return;
        var type = _pendingRequestType;
        var fromMemberId = _pendingFromMemberId;
        ClearPendingPrompt();
        var session = _session;
        if (session is null || !IsOnline)
        {
            SetNoticeInternal("房间已经关掉了", "DangerBrush");
            return;
        }

        if (IsHost)
        {
            if (type == BoardRequestTypes.Rematch)
            {
                var started = TryStartNextGameCore();
                await SafeSendAsync(() => session.SendResponseToAsync(fromMemberId, type, started));
                if (started)
                {
                    SetNoticeInternal("已同意对方的再来一局请求，新的一局开始", "SuccessBrush");
                    AnnounceSystemChat("房主同意了再来一局，新的一局开始");
                }
                else
                {
                    SetNoticeInternal("现在还不能开始下一局，已替你拒绝", "DangerBrush");
                }
                return;
            }

            var requesterSide = SideOfMember(fromMemberId);
            if (requesterSide.Length > 0 && TryApplyRequest(type, requesterSide))
            {
                CacheGameSnapshot();
                BroadcastState();
                await SafeSendAsync(() => session.SendResponseToAsync(fromMemberId, type, true));
                var text = $"已同意{_pendingFromName}的{RequestLabel(type)}";
                SetNoticeInternal(text, "SuccessBrush");
                AddChatItem(BoardChatItem.System(text));
                HostBroadcastSystemChat(text);
                RaiseAllDerived();
            }
            else
            {
                await SafeSendAsync(() => session.SendResponseToAsync(fromMemberId, type, false));
                SetNoticeInternal($"{RequestLabel(type)}现在执行不了，已替你拒绝", "DangerBrush");
            }
            return;
        }

        // 客户端：只表示同意，等房主应用后广播状态
        await SafeSendAsync(() => session.SendResponseAsync(type, true));
        SetPendingWait(type, $"已同意{RequestLabel(type)}，等待房主同步…");
    }

    private async Task RejectPendingAsync()
    {
        if (!_hasPendingPrompt) return;
        var type = _pendingRequestType;
        var fromMemberId = _pendingFromMemberId;
        var fromName = _pendingFromName;
        ClearPendingPrompt();
        var session = _session;
        if (session is null) return;

        if (IsHost) await SafeSendAsync(() => session.SendResponseToAsync(fromMemberId, type, false));
        else await SafeSendAsync(() => session.SendResponseAsync(type, false));

        SetNoticeInternal($"已拒绝{fromName}的{RequestLabel(type)}", "TextSecondaryBrush");
        AddChatItem(BoardChatItem.System($"你拒绝了{fromName}的{RequestLabel(type)}"));
    }

    /// <summary>收到应答：房主处理「转达请求的回应 / 自己请求的回应」；客户端只更新等待状态。</summary>
    private void HandleIncomingResponse(string fromMemberId, string type, bool accept)
    {
        if (string.IsNullOrWhiteSpace(type)) return;
        if (!IsHost)
        {
            var mine = _hasPendingWait && string.Equals(_pendingWaitType, type, StringComparison.Ordinal);
            if (mine) ClearPendingWait();
            if (accept)
            {
                SetNoticeInternal($"对方同意了{RequestLabel(type)}，等待房主同步…", "SuccessBrush");
                AddChatItem(BoardChatItem.System($"对方同意了{RequestLabel(type)}"));
            }
            else
            {
                SetNoticeInternal($"对方拒绝了{RequestLabel(type)}", "DangerBrush");
                AddChatItem(BoardChatItem.System($"对方拒绝了{RequestLabel(type)}"));
            }
            RaiseAllDerived();
            return;
        }

        var relayKey = RelayKey(fromMemberId, type);
        if (_relayPending.TryGetValue(relayKey, out var relay))
        {
            _relayPending.Remove(relayKey);
            var responderName = MemberDisplayName(fromMemberId);
            if (!accept)
            {
                SendOnSession(s => s.SendResponseToAsync(relay.RequesterMemberId, type, false));
                var text = $"{responderName} 拒绝了{RequestLabel(type)}";
                SetNoticeInternal(text, "TextSecondaryBrush");
                AddChatItem(BoardChatItem.System(text));
                HostBroadcastSystemChat(text);
                return;
            }

            var requesterSide = SideOfMember(relay.RequesterMemberId);
            if (requesterSide.Length > 0 && TryApplyRequest(type, requesterSide))
            {
                CacheGameSnapshot();
                BroadcastState();
                SendOnSession(s => s.SendResponseToAsync(relay.RequesterMemberId, type, true));
                var text = $"{responderName} 同意了{RequestLabel(type)}，已生效";
                SetNoticeInternal(text, "SuccessBrush");
                AddChatItem(BoardChatItem.System(text));
                HostBroadcastSystemChat(text);
                RaiseAllDerived();
            }
            else
            {
                SendOnSession(s => s.SendResponseToAsync(relay.RequesterMemberId, type, false));
                SetNoticeInternal($"{RequestLabel(type)}现在执行不了，已替你拒绝", "DangerBrush");
            }
            return;
        }

        // 房主自己（玩家）发出的请求的回应
        if (_hasPendingWait && string.Equals(_pendingWaitType, type, StringComparison.Ordinal)
            && string.Equals(fromMemberId, _hostWaitingOpponentId, StringComparison.Ordinal))
        {
            ClearPendingWait();
            if (!accept)
            {
                SetNoticeInternal($"对方拒绝了{RequestLabel(type)}", "DangerBrush");
                AddChatItem(BoardChatItem.System($"对方拒绝了{RequestLabel(type)}"));
                return;
            }
            var ok = TryApplyRequest(type, LocalSide);
            if (ok)
            {
                CacheGameSnapshot();
                BroadcastState();
                SetNoticeInternal($"对方同意了{RequestLabel(type)}", "SuccessBrush");
                AddChatItem(BoardChatItem.System($"对方同意了{RequestLabel(type)}"));
                RaiseAllDerived();
            }
            else
            {
                SetNoticeInternal($"对方同意了{RequestLabel(type)}，但现在执行不了", "DangerBrush");
            }
        }
    }

    /// <summary>主机执行一个被同意的请求；返回 false 表示当前状态不允许。</summary>
    private bool TryApplyRequest(string type, string requesterSide)
    {
        try
        {
            return type switch
            {
                BoardRequestTypes.Undo => ApplyUndoFor(requesterSide),
                BoardRequestTypes.Draw => ApplyDrawResult(),
                _ => false,
            };
        }
        catch (Exception ex)
        {
            SetNoticeInternal($"执行{RequestLabel(type)}失败：{ex.Message}", "DangerBrush");
            return false;
        }
    }

    private bool ApplyDrawResult()
    {
        ApplyDraw();
        return true;
    }

    /// <summary>主机收到客户端动作：校验发送者确实坐在席位上，再交给游戏落子。</summary>
    private void HandleRemoteAction(string memberId, string type, JsonElement? payload)
    {
        if (!IsHost) return;
        var side = SideOfMember(memberId);
        if (side.Length == 0)
        {
            SetNoticeInternal("收到非场上玩家的动作，已忽略（观战者不能走子）", "DangerBrush");
            return;
        }
        try { OnHostAction(memberId, side, type, payload); }
        catch (Exception ex) { SetNoticeInternal($"处理对方动作失败：{ex.Message}", "DangerBrush"); }
    }

    /// <summary>客户端收到系统聊天 / 普通聊天。</summary>
    private void OnChatReceived(string memberId, string name, string text, bool isSystem)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;
        if (isSystem)
        {
            AddChatItem(BoardChatItem.System(trimmed));
            return;
        }
        var isSelf = string.Equals(memberId, LocalMemberId, StringComparison.Ordinal);
        AddChatItem(BoardChatItem.Chat(string.IsNullOrWhiteSpace(name) ? "对方" : name, trimmed, isSelf));
    }

    private static string RelayKey(string responderMemberId, string type)
        => $"{(responderMemberId ?? "").Trim()}|{(type ?? "").Trim()}";

    private static string RequestLabel(string type) => type switch
    {
        BoardRequestTypes.Undo => "悔棋",
        BoardRequestTypes.Draw => "和棋",
        BoardRequestTypes.Rematch => "再来一局",
        BoardRequestTypes.Resign => "认输",
        _ => type,
    };

    private void ClearPendingForMember(string memberId)
    {
        var id = (memberId ?? "").Trim();
        if (id.Length == 0) return;
        if (string.Equals(_pendingFromMemberId, id, StringComparison.Ordinal)) ClearPendingPrompt();
        if (string.Equals(_hostWaitingOpponentId, id, StringComparison.Ordinal)) ClearPendingWait();
        foreach (var key in _relayPending.Keys.ToList())
        {
            var relay = _relayPending[key];
            if (string.Equals(relay.RequesterMemberId, id, StringComparison.Ordinal)
                || key.StartsWith(id + "|", StringComparison.Ordinal))
                _relayPending.Remove(key);
        }
    }

    // ---------------------------------------------------------------- 主机：向客户端广播聊天

    /// <summary>主机把一条系统提示只广播给客户端（本机显示由调用方自己加）。</summary>
    private void HostBroadcastSystemChat(string text)
    {
        if (!IsHost || !IsOnline || _demoRoom) return;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return;
        SendOnSession(s => s.BroadcastChatAsync(LocalName, trimmed, true));
    }

    // ---------------------------------------------------------------- 发送 / 工具

    private static void SendOnSessionCore(BoardRoomSession session, Func<BoardRoomSession, Task> send)
    {
        // 发送失败不该弹到界面上（对方可能已经掉了，StateChanged / MemberLeft 会给出提示）
        _ = Task.Run(async () =>
        {
            try { await send(session).ConfigureAwait(false); }
            catch { /* 忽略 */ }
        });
    }

    private void SendOnSession(Func<BoardRoomSession, Task> send)
    {
        var session = _session;
        if (session is null) return;
        SendOnSessionCore(session, send);
    }

    private Task UiRunAsync(Action action) => UiAsync(() => { action(); return Task.CompletedTask; });

    private static async Task SafeSendAsync(Func<Task> send)
    {
        try { await send().ConfigureAwait(false); } catch { /* 对方可能已经掉了 */ }
    }

    private static async Task DisposeSessionAsync(BoardRoomSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    private void CopyToClipboard(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            SetNotice("已复制到剪贴板", "SuccessBrush");
        }
        catch (Exception ex)
        {
            SetNotice($"复制失败：{ex.Message}", "DangerBrush");
        }
    }

    private void InvalidateCommands()
    {
        try { CommandManager.InvalidateRequerySuggested(); } catch { /* 无头环境忽略 */ }
    }

    private void RebuildShareRows()
    {
        ShareRows.Clear();
        var session = _session;
        if (session is not null && session.IsHost)
        {
            var port = session.ListenPort;
            foreach (var address in session.ShareAddresses)
            {
                var text = RoomProtocol.WithPort(address.Text, port);
                if (text.Length == 0) continue;
                ShareRows.Add(new RoomShareRow
                {
                    AddressText = text,
                    DisplayText = text,
                    Family = address.Family,
                    InterfaceText = address.Interface + (address.IsRecommended ? "（推荐）" : ""),
                    IsRecommended = address.IsRecommended,
                });
            }
        }

        ShareRowsVisible.Clear();
        foreach (var row in ShareRows.Take(MaxShareRowsShown)) ShareRowsVisible.Add(row);

        OnPropertyChanged(nameof(HasShareRows));
        OnPropertyChanged(nameof(HasMoreShareRows));
        OnPropertyChanged(nameof(ShareMoreText));
        OnPropertyChanged(nameof(InviteText));
    }

    private void SetPendingWait(string type, string text)
    {
        _pendingWaitType = type;
        PendingWaitText = text;
        HasPendingWait = true;
    }

    private void ClearPendingWait()
    {
        _pendingWaitType = "";
        _hostWaitingType = "";
        _hostWaitingOpponentId = "";
        HasPendingWait = false;
        PendingWaitText = "";
    }

    private void ClearPendingPrompt()
    {
        _pendingRequestType = "";
        _pendingFromMemberId = "";
        _pendingFromName = "";
        HasPendingPrompt = false;
        PendingPromptText = "";
    }

    private void ClearPending()
    {
        ClearPendingPrompt();
        ClearPendingWait();
    }

    // ---------------------------------------------------------------- 状态维护 / 通知

    private void SetMode(BoardPlayMode mode)
    {
        if (_mode == mode)
        {
            RaiseAllDerived();
            return;
        }

        _mode = mode;
        RaiseAllDerived();
        try { OnModeChanged(); }
        catch (Exception ex) { SetNoticeInternal($"刷新界面状态失败：{ex.Message}", "DangerBrush"); }
    }

    /// <summary>把「所有跟房间 / 席位 / 成员 / 对局状态有关的派生属性」都通知一遍。</summary>
    private void RaiseAllDerived()
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(IsClient));
        OnPropertyChanged(nameof(IsDemoRoom));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(HasPeer));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(ReconnectStatusText));
        OnPropertyChanged(nameof(IsGameRunning));
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
        OnPropertyChanged(nameof(ShowJoinControls));
        OnPropertyChanged(nameof(CanLeaveRoom));
        OnPropertyChanged(nameof(LeaveRoomText));
        OnPropertyChanged(nameof(ModeBadgeText));
        OnPropertyChanged(nameof(ModeBadgeBrushKey));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(LocalSideText));
        OnPropertyChanged(nameof(PeerSideText));

        OnPropertyChanged(nameof(Members));
        OnPropertyChanged(nameof(MemberCountText));
        OnPropertyChanged(nameof(LocalMemberId));
        OnPropertyChanged(nameof(IsSeatedPlayer));
        OnPropertyChanged(nameof(IsSpectator));
        OnPropertyChanged(nameof(LocalSide));
        OnPropertyChanged(nameof(SeatOneSide));
        OnPropertyChanged(nameof(SeatTwoSide));
        OnPropertyChanged(nameof(SeatOneSideText));
        OnPropertyChanged(nameof(SeatTwoSideText));
        OnPropertyChanged(nameof(SeatOneName));
        OnPropertyChanged(nameof(SeatTwoName));
        OnPropertyChanged(nameof(SeatOneOnline));
        OnPropertyChanged(nameof(SeatTwoOnline));
        OnPropertyChanged(nameof(SeatOneOnlineText));
        OnPropertyChanged(nameof(SeatTwoOnlineText));
        OnPropertyChanged(nameof(MyRoleText));
        OnPropertyChanged(nameof(SeatStatusText));
        OnPropertyChanged(nameof(SpectatorHintText));

        OnPropertyChanged(nameof(HostControlsVisible));
        OnPropertyChanged(nameof(CanEditSeats));
        OnPropertyChanged(nameof(CanStartNextGame));
        OnPropertyChanged(nameof(StartNextGameText));
        OnPropertyChanged(nameof(ShowStartNextGameButton));
        OnPropertyChanged(nameof(CanAbortGame));
        OnPropertyChanged(nameof(IsAbortConfirming));
        OnPropertyChanged(nameof(AbortGameText));
        OnPropertyChanged(nameof(ShowPlayerActions));

        OnPropertyChanged(nameof(SelectedSeatOneOption));
        OnPropertyChanged(nameof(SelectedSeatTwoOption));
        OnPropertyChanged(nameof(SelectedAutoRotateMode));
        OnPropertyChanged(nameof(SelectedAutoPickMode));
        OnPropertyChanged(nameof(AutoRotateEnabled));
        OnPropertyChanged(nameof(AutoRotateHintText));

        OnPropertyChanged(nameof(HasShareRows));
        OnPropertyChanged(nameof(HasMoreShareRows));
        OnPropertyChanged(nameof(ShareMoreText));
        OnPropertyChanged(nameof(InviteText));
        OnPropertyChanged(nameof(CanSendChat));
        OnPropertyChanged(nameof(PendingWaitText));
        InvalidateCommands();
    }

    // ---------------------------------------------------------------- 子类钩子安全调用

    private void SafeHook(Action hook)
    {
        try { hook(); }
        catch (Exception ex) { SetNoticeInternal($"刷新棋局状态失败：{ex.Message}", "DangerBrush"); }
    }

    private string? ValidateHostStartSafe()
    {
        try { return ValidateHostStart(); }
        catch (Exception ex) { return $"开局校验出错：{ex.Message}"; }
    }

    private static string SafeGetSide(Func<string> getter)
    {
        try { return (getter() ?? "").Trim(); }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- 演示房间（YLT_BOARD_DEMO_ROOM）

    /// <summary>
    /// 演示房间初始化：只填「权威模型 + 界面集合」，<b>不回调任何 virtual 钩子</b>
    /// （它在子类构造函数里被 <see cref="AttachRoomConfig"/> 调起，那时子类字段可能还没初始化好）。
    /// </summary>
    private void InitDemoRoomIfNeeded()
    {
        if (DemoRoomEnv.Length == 0 || _demoInitialized) return;
        _demoInitialized = true;
        _demoRoom = true;

        var spectator = DemoRoomEnv == "spectator";
        var hostId = BoardRoomSession.HostMemberId;
        const string mingId = "m1";      // 小明
        const string gangId = "m2";      // 小刚（已打 1 局）
        const string hongId = "m3";      // 小红（没上过场，观战）
        _demoLocalMemberId = spectator ? hongId : hostId;

        var hostName = string.IsNullOrWhiteSpace(PlayerName) ? "房主" : PlayerName.Trim();

        lock (_roomGate)
        {
            _roomMembers.Clear();
            _roomMembers[hostId] = new BoardMemberInfo
            {
                MemberId = hostId, Name = hostName, IsHost = true, IsOnline = true,
                GamesPlayed = 1, JoinOrder = 0,
            };
            _roomMembers[mingId] = new BoardMemberInfo
            {
                MemberId = mingId, Name = "小明", IsHost = false, IsOnline = true,
                GamesPlayed = 1, JoinOrder = 1,
            };
            _roomMembers[gangId] = new BoardMemberInfo
            {
                MemberId = gangId, Name = "小刚", IsHost = false, IsOnline = true,
                GamesPlayed = 1, JoinOrder = 2,
            };
            _roomMembers[hongId] = new BoardMemberInfo
            {
                MemberId = hongId, Name = "小红", IsHost = false, IsOnline = true,
                GamesPlayed = 0, JoinOrder = 3,
            };

            // 房主默认坐席：按游戏的「房主执子」偏好；取不到就坐先手位
            var firstSide = SafeGetSide(() => FirstSeatSide);
            var preferred = SafeGetSide(() => PreferredHostSeatSide);
            if (preferred.Length == 0) preferred = firstSide;
            _seatOneMemberId = "";
            _seatTwoMemberId = "";
            if (firstSide.Length > 0 && !string.Equals(preferred, firstSide, StringComparison.Ordinal))
            {
                _seatOneMemberId = mingId;
                _seatTwoMemberId = hostId;
            }
            else
            {
                _seatOneMemberId = hostId;
                _seatTwoMemberId = mingId;
            }

            _autoRotateMode = BoardAutoRotateMode.LoserOut;
            _autoPickMode = BoardAutoPickMode.Rotation;
            _hasPlayedGame = true;
            _finishedNotified = false;
        }

        // 注意：这里在子类构造函数里（AttachRoomConfig 触发），只直接改字段、不调 virtual 钩子
        _mode = spectator ? BoardPlayMode.Client : BoardPlayMode.Host;
        SyncMembersView();
        RebuildSeatChoices();
        SetNoticeInternal(spectator
            ? "演示房间（观战视角）：你在观战，等房主安排上场"
            : "演示房间（房主视角）：安排席位 / 自动轮换后点「开始下一局」", "TextSecondaryBrush");
        AddChatItem(BoardChatItem.System(spectator
            ? "演示房间：你是观战者（YLT_BOARD_DEMO_ROOM=spectator）"
            : "演示房间：4 人房，房主 + 小明在场上（YLT_BOARD_DEMO_ROOM=1）"));
        RaiseAllDerived();
    }

    // ---------------------------------------------------------------- 命令

    /// <summary>房主：交换两个席位。</summary>
    public RelayCommand SwapSeatsCommand { get; }

    /// <summary>房主：清空两个席位。</summary>
    public RelayCommand ClearSeatsCommand { get; }

    /// <summary>房主：开始（下一）局。</summary>
    public RelayCommand StartNextGameCommand { get; }

    /// <summary>房主：结束本局（作废），需要点两次确认。</summary>
    public RelayCommand AbortGameCommand { get; }

    /// <summary>开房间（主机）。</summary>
    public AsyncRelayCommand StartHostCommand { get; }

    /// <summary>加入别人的房间。</summary>
    public AsyncRelayCommand JoinRoomCommand { get; }

    /// <summary>离开房间（停止监听 / 断开连接 / 停止重连）。</summary>
    public AsyncRelayCommand LeaveRoomCommand { get; }

    /// <summary>同机双人。</summary>
    public RelayCommand StartLocalCommand { get; }

    /// <summary>同意别人的请求（悔棋 / 求和 / 再来一局）。</summary>
    public RelayCommand AcceptPendingCommand { get; }

    /// <summary>拒绝别人的请求。</summary>
    public RelayCommand RejectPendingCommand { get; }

    /// <summary>发送聊天。</summary>
    public RelayCommand SendChatCommand { get; }

    /// <summary>清空聊天记录。</summary>
    public RelayCommand ClearChatCommand { get; }

    /// <summary>复制邀请文案。</summary>
    public RelayCommand CopyInviteCommand { get; }

    /// <summary>复制指定文本（CommandParameter = 要复制的文本）。</summary>
    public RelayCommand CopyTextCommand { get; }

    // ---------------------------------------------------------------- 生命周期

    /// <summary>工具被切走 / 关闭：停掉房间、取消自动重连并停掉状态刷新定时器（配置由基类 Flush）。</summary>
    protected override void OnClosed()
    {
        base.OnClosed();
        _closed = true;
        if (IsDemoRoom) return;
        CancelReconnect();                               // 关工具 = 放弃重连，循环不得再动界面
        _statusTimer?.Stop();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            _ = Task.Run(async () =>
            {
                try { await session.StopAsync("工具已关闭").ConfigureAwait(false); } catch { /* 忽略 */ }
                try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
            });
        }
    }
}
