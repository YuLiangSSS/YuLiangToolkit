using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Shared.ViewModels;

/// <summary>
/// 棋牌功能配置里「房间」相关的三个字段（五子棋 / 象棋的 Config 都实现它）。
/// 基类只碰这三个，其余设置项是各游戏自己的事。
/// </summary>
public interface IBoardGameRoomConfig
{
    /// <summary>玩家名（对方看到的名字）。</summary>
    string PlayerName { get; set; }

    /// <summary>上次加入过的主机地址（下次打开自动带出来）。</summary>
    string LastJoinAddress { get; set; }

    /// <summary>开房时监听的端口（0 = 让系统分配）。</summary>
    int ListenPort { get; set; }
}

/// <summary>当前处于哪种玩法。</summary>
public enum BoardPlayMode
{
    /// <summary>没进房间（也没同机双人）：棋盘可以看，但不能下。</summary>
    Offline = 0,

    /// <summary>同机双人：两个人轮流用这一台电脑。</summary>
    Local = 1,

    /// <summary>主机：本端开房间，执 <see cref="BoardGameRoomViewModelBase.HostSide"/>。</summary>
    Host = 2,

    /// <summary>客户端：连到别人的房间。</summary>
    Client = 3,
}

/// <summary>
/// 两个棋牌游戏 ViewModel 的公共基类：<b>房间开关 / 加入 / 同机双人、配置里的玩家名与地址端口、
/// 地址分享、状态文案、聊天、悔棋 / 求和 / 再来一局 / 认输的联机协议流程、请求确认弹层、
/// 发送快照与动作</b>。这里<b>不碰任何棋盘规则</b> —— 规则全在子类的那几个 abstract 方法里。
///
/// <para><b>线程约定</b>：<see cref="BoardRoomSession"/> 的事件都在后台线程触发，本类内部一律先
/// <c>Ui(...)</c> 再改属性 / 调子类钩子 —— <b>子类可以认为所有钩子都在 UI 线程被调用</b>。</para>
///
/// <para><b>联机请求流程</b>（子类不要重复实现）：</para>
/// <list type="bullet">
///   <item>自己点悔棋 / 求和 / 再来一局 → 基类的 <c>RequestUndo/OfferDraw/RequestRematch</c> → 发 request 并显示「等待回应」。</item>
///   <item>收到 request → 弹确认（<see cref="HasPendingPrompt"/>）；同意后主机执行对应 <c>Apply*</c> 并广播，
///         客户端只回 response(true) 等快照。</item>
///   <item>收到 response → 主机执行 <c>Apply*</c> 并广播；客户端等快照 / 显示被拒。</item>
///   <item>认输：主机直接 <c>ApplyResign(自己)</c> + 广播；客户端发 request "resign"，主机 <c>ApplyResign(对端)</c> + 广播。</item>
/// </list>
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

    /// <summary>读一个正整数环境变量（给自检 / 排错用；非法值用默认值）。</summary>
    private static int ReadIntEnv(string name, int fallback)
    {
        var text = Environment.GetEnvironmentVariable(name);
        return int.TryParse(text, out var value) && value is >= 1 and <= 600 ? value : fallback;
    }

    /// <summary>
    /// 截图 / 演示开关：设了环境变量 <c>YLT_BOARD_DEMO_PAUSE=1</c> 时，界面固定表现为
    /// 「对局暂停 + 正在重连」的状态（用来截图核对暂停 / 重连 UI；<b>不影响真实联机流程</b>）。
    /// </summary>
    private static readonly bool DemoPaused = Environment.GetEnvironmentVariable("YLT_BOARD_DEMO_PAUSE") is not null;

    private readonly DispatcherTimer? _statusTimer;

    private IBoardGameRoomConfig? _roomCfg;
    private BoardRoomSession? _session;
    private BoardPlayMode _mode = BoardPlayMode.Offline;
    private bool _isRoomBusy;
    private bool _hasPeer;
    private string _peerName = "";
    private string _localSide = "";
    private string _peerSide = "";
    private string _noticeText = "还没进入房间：开个房间等对方加入，或填地址加入别人的房间，也可以同机双人";
    private string _noticeBrushKey = "TextSecondaryBrush";
    private string _chatText = "";
    private string _listenPortText = "";
    private int _snapshotSeq;

    private bool _isReconnecting;
    private string _reconnectStatusText = "";
    private bool _peerLeftSinceRoomOpen;
    private bool _closed;
    private int _reconnectGeneration;

    private bool _hasPendingPrompt;
    private string _pendingPromptText = "";
    private string _pendingRequestType = "";
    private bool _hasPendingWait;
    private string _pendingWaitText = "";
    private string _pendingWaitType = "";

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

    /// <summary>
    /// 当前这次房间会话（只读）。<b>给自检 / 排错用</b>：例如
    /// <c>CurrentSession?.AbortConnection()</c> 模拟断网来验证自动重连；正常界面请不要直接操作它。
    /// </summary>
    public BoardRoomSession? CurrentSession => _session;

    /// <summary>
    /// 子类在构造里 <c>LoadConfig&lt;T&gt;()</c> 之后调用，把配置交给基类
    /// （玩家名 / 上次加入地址 / 监听端口三项双向绑定并自动落盘）。
    /// </summary>
    protected void AttachRoomConfig(IBoardGameRoomConfig cfg)
    {
        _roomCfg = cfg;
        _listenPortText = cfg.ListenPort is >= 0 and <= 65535 ? cfg.ListenPort.ToString() : "0";
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(JoinAddress));
        OnPropertyChanged(nameof(ListenPortText));
        OnPropertyChanged(nameof(ListenPort));
    }

    /// <summary>本端玩家名（空的话给个默认名，免得对方看到一个空白名字）。</summary>
    protected string LocalName => string.IsNullOrWhiteSpace(PlayerName) ? "玩家" : PlayerName.Trim();

    // ---------------------------------------------------------------- 绑定给 XAML 的属性

    /// <summary>玩家名（双向落 <see cref="IBoardGameRoomConfig.PlayerName"/>）。</summary>
    public string PlayerName
    {
        get => _roomCfg?.PlayerName ?? "";
        set
        {
            if (_roomCfg is null || _roomCfg.PlayerName == value) return;
            _roomCfg.PlayerName = value ?? "";
            OnPropertyChanged();
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

    /// <summary>当前玩法。</summary>
    public BoardPlayMode Mode => _mode;

    /// <summary>同机双人。</summary>
    public bool IsLocalMode => _mode == BoardPlayMode.Local;

    /// <summary>本端是主机（房间可能还没开成功，以 <see cref="IsOnline"/> 为准）。</summary>
    public bool IsHost => _mode == BoardPlayMode.Host;

    /// <summary>本端是客户端。</summary>
    public bool IsClient => _mode == BoardPlayMode.Client;

    /// <summary>在线：主机监听中 / 客户端握手完成（断线重连期间是 false，看 <see cref="IsReconnecting"/>）。</summary>
    public bool IsOnline => _session?.IsOnline == true;

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

    /// <summary>对方在线（主机 = 客户端已握手；客户端 = 已连上主机）。</summary>
    public bool HasPeer
    {
        get => _hasPeer;
        private set
        {
            if (!Set(ref _hasPeer, value)) return;
            OnPropertyChanged(nameof(PeerSideText));
            OnPropertyChanged(nameof(IsGamePaused));
            OnPropertyChanged(nameof(GamePauseStatusText));
        }
    }

    /// <summary>
    /// 客户端正在自动重连：此时 <see cref="Mode"/> 仍保持 <see cref="BoardPlayMode.Client"/>，
    /// 但 <see cref="IsOnline"/> 为 false、棋盘只读、不能重复点加入；点「离开房间」= 停止重连。
    /// <para>设了 <c>YLT_BOARD_DEMO_PAUSE=1</c> 时恒为 true（仅截图演示）。</para>
    /// </summary>
    public bool IsReconnecting
    {
        get => DemoPaused || _isReconnecting;
        private set
        {
            if (!Set(ref _isReconnecting, value)) return;
            OnPropertyChanged(nameof(CanJoin));
            OnPropertyChanged(nameof(IsGamePaused));
            OnPropertyChanged(nameof(GamePauseStatusText));
            OnPropertyChanged(nameof(ShowJoinControls));
            OnPropertyChanged(nameof(CanLeaveRoom));
            OnPropertyChanged(nameof(LeaveRoomText));
            OnPropertyChanged(nameof(ConnectionSummary));
            OnPropertyChanged(nameof(ModeBadgeText));
            OnPropertyChanged(nameof(ModeBadgeBrushKey));
            InvalidateCommands();
        }
    }

    /// <summary>重连进度文案，例如「连接断开，正在重连（第 3 次）…」；没在重连时为空。</summary>
    public string ReconnectStatusText
    {
        get => DemoPaused ? "正在重连（第 3 次，还剩 25 次）…" : _reconnectStatusText;
        private set => Set(ref _reconnectStatusText, value);
    }

    /// <summary>
    /// 对局暂停：主机等对方重连 / 客户端自己重连中。棋盘应只读、落子按钮应禁用
    /// （游戏侧 <c>CanPlayNow</c> 请一并要求 <see cref="HasPeer"/>，见 WIP 3.6）。
    /// </summary>
    public bool IsGamePaused =>
        DemoPaused || IsReconnecting
        || (_mode == BoardPlayMode.Host && IsOnline && !HasPeer && _peerLeftSinceRoomOpen);

    /// <summary>暂停时给界面看的一句人话（没暂停时为空）。</summary>
    public string GamePauseStatusText
    {
        get
        {
            if (DemoPaused) return "对局暂停 · 等待对方重连";
            if (!IsGamePaused) return "";
            if (IsReconnecting) return "对局暂停 · 连接断开，正在重连…";
            return "对局暂停 · 等待对方重连";
        }
    }

    /// <summary>「开房 / 加入房间」输入区要不要显示（在线、房间开着、重连中都不显示，只留状态/分享/离开）。</summary>
    public bool ShowJoinControls => !IsOnline && !IsReconnecting
                                    && _mode is not (BoardPlayMode.Host or BoardPlayMode.Client);

    /// <summary>有没有可离开的房间 / 可停止的重连（离开房间按钮的可见性与可用性）。</summary>
    public bool CanLeaveRoom => IsOnline || IsReconnecting;

    /// <summary>离开按钮的文案：重连中是「停止重连」，其它时候是「离开房间」。</summary>
    public string LeaveRoomText => IsReconnecting ? "停止重连" : "离开房间";

    /// <summary>对方玩家名（没人在线时为空）。</summary>
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

    /// <summary>状态徽标文案：<c>● 房间开着</c> / <c>● 已连接</c> / <c>● 同机双人</c> / <c>◌ 正在重连…</c> / <c>○ 未连接</c>。</summary>
    public string ModeBadgeText => IsReconnecting
        ? "◌ 正在重连…"
        : _mode switch
        {
            BoardPlayMode.Local => "● 同机双人",
            BoardPlayMode.Host => IsOnline ? (IsGamePaused ? "● 对局暂停" : "● 房间开着") : "○ 未连接",
            BoardPlayMode.Client => IsOnline ? "● 已连接" : "○ 未连接",
            _ => "○ 未连接",
        };

    /// <summary>状态徽标要用的画刷资源名（RoomPanel 用 DataTrigger 映射成 SuccessBrush / DangerBrush / TextSecondaryBrush）。</summary>
    public string ModeBadgeBrushKey => IsReconnecting || IsGamePaused
        ? "DangerBrush"
        : _mode == BoardPlayMode.Local || IsOnline ? "SuccessBrush" : "TextSecondaryBrush";

    /// <summary>一行连接摘要：本机监听地址 / 对方名字 / 延迟 / 重连进度。</summary>
    public string ConnectionSummary
    {
        get
        {
            var session = _session;
            if (IsReconnecting)
                return ReconnectStatusText.Length > 0 ? ReconnectStatusText : "连接断开，正在自动重连…";

            return _mode switch
            {
                BoardPlayMode.Host when session is not null => session.IsOnline
                    ? $"本机监听 {session.ListenText}（真实端口 {session.ListenPort}）"
                      + (HasPeer
                          ? $"　对手：{PeerName}　延迟 {session.LatencyMs} ms"
                          : IsGamePaused ? "　对局暂停，等待对方重连" : "　等待对方加入")
                    : "正在开房间…",
                BoardPlayMode.Client when session is not null => session.IsOnline
                    ? $"已连接 {(PeerName.Length > 0 ? PeerName : "房主")}　延迟 {session.LatencyMs} ms"
                    : "正在连接…",
                BoardPlayMode.Client => "连接已断开",
                BoardPlayMode.Local => "两个人轮流用这一台电脑操作",
                _ => "未连接（可以开房间、加入房间，或同机双人）",
            };
        }
    }

    /// <summary>我方执子文案：<c>我方：黑棋</c> / <c>我方：未定</c> …。</summary>
    public string LocalSideText => _mode switch
    {
        BoardPlayMode.Local => "同机轮流（换手）",
        BoardPlayMode.Offline => "我方：未定",
        _ => string.IsNullOrEmpty(_localSide) ? "我方：未定" : $"我方：{SideDisplayName(_localSide)}",
    };

    /// <summary>对方执子文案。</summary>
    public string PeerSideText
    {
        get
        {
            if (_mode == BoardPlayMode.Local) return "同机轮流（换手）";
            if (_mode == BoardPlayMode.Offline) return "对方：未定";
            if (!HasPeer) return IsHost ? "对方：等待加入" : "对方：未定";
            return string.IsNullOrEmpty(_peerSide) ? "对方：未定" : $"对方：{SideDisplayName(_peerSide)}";
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

    /// <summary>房间卡上最多直接显示几行分享地址（其余折叠，避免把「对局」卡挤到屏幕外）。</summary>
    private const int MaxShareRowsShown = 3;

    /// <summary>房间卡实际显示的那几行地址（<see cref="ShareRows"/> 的前 3 行）。</summary>
    public ObservableCollection<RoomShareRow> ShareRowsVisible { get; } = new();

    /// <summary>有没有可分享的地址。</summary>
    public bool HasShareRows => ShareRows.Count > 0;

    /// <summary>地址太多、被折叠掉了一部分（房间卡上显示一行「更多地址」提示）。</summary>
    public bool HasMoreShareRows => ShareRows.Count > MaxShareRowsShown;

    /// <summary>被折叠时的提示文案（没折叠时为空）。</summary>
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

    /// <summary>往聊天记录里加一条系统提示（灰字）。可从任意线程调用。</summary>
    public void AppendSystemChat(string text) => AppendNoticeChat(text, alsoNotice: false);

    /// <summary>加一条系统提示，同时更新状态栏提示。</summary>
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
        var session = _session;
        if (session is null || !IsOnline)
        {
            SetNotice("还没进房间，发不了聊天", "DangerBrush");
            return;
        }

        ChatText = "";
        AddChatItem(BoardChatItem.Chat(LocalName, text, isSelf: true));
        SendOnSession(s => s.SendChatAsync(text));
    }

    // ---------------------------------------------------------------- 联机请求（悔棋 / 求和 / 再来一局 / 认输）

    /// <summary>请求悔棋（联机时发 request "undo"，等对方同意；同机双人由子类自己处理，别调这个）。</summary>
    public void RequestUndo() => RequestPeer(BoardRequestTypes.Undo);

    /// <summary>求和（联机时发 request "draw"）。</summary>
    public void OfferDraw() => RequestPeer(BoardRequestTypes.Draw);

    /// <summary>请求再来一局（联机时发 request "rematch"）。</summary>
    public void RequestRematch() => RequestPeer(BoardRequestTypes.Rematch);

    /// <summary>
    /// 认输（联机）：主机直接判负并广播；客户端发 request "resign"，由主机执行。
    /// <b>同机双人 / 离线不要调这个</b>（子类自己改棋局状态即可）。
    /// </summary>
    public void ResignOnline()
    {
        if (!IsOnline || !HasPeer)
        {
            SetNotice("还没连上对方，不能认输", "DangerBrush");
            return;
        }

        if (IsHost)
        {
            ApplyResign(HostSide);
            BroadcastSnapshot();
            SetNotice("你认输了，本局结束", "DangerBrush");
            AppendNoticeChat("你认输了，本局结束");
            return;
        }

        SetNotice("已向房主认输，等待对局结束…", "DangerBrush");
        AppendNoticeChat("你认输了");
        SendOnSession(s => s.SendRequestAsync(BoardRequestTypes.Resign));
    }

    /// <summary>有没有「对方请求等你点同意 / 拒绝」。</summary>
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

    private void RequestPeer(string type)
    {
        if (!IsOnline || !HasPeer)
        {
            SetNotice("还没连上对方，不能发这个请求", "DangerBrush");
            return;
        }
        if (_hasPendingWait || _hasPendingPrompt)
        {
            SetNotice("已经有一个请求在处理中了，等它结束再说", "DangerBrush");
            return;
        }

        SetPendingWait(type, $"已请求{RequestLabel(type)}，等待对方回应…");
        SetNotice($"已向对方请求{RequestLabel(type)}", "TextSecondaryBrush");
        AppendNoticeChat($"你请求{RequestLabel(type)}");
        SendOnSession(s => s.SendRequestAsync(type));
    }

    // ---------------------------------------------------------------- 子类必须实现（游戏规则全在这里）

    /// <summary>当前主机执哪边（"black"/"white" 或 "red"/"black"）。</summary>
    protected abstract string HostSide { get; }

    /// <summary>某一边的对面（"black" ↔ "white"、"red" ↔ "black"）。</summary>
    protected abstract string OppositeSide(string side);

    /// <summary>边的显示名（"黑棋"/"白棋"/"红方"/"黑方"）。</summary>
    protected abstract string SideDisplayName(string side);

    /// <summary>游戏状态（含 started / result / moves 等，具体结构由游戏自己定）。主机广播时用。</summary>
    protected abstract JsonElement BuildGameSnapshot();

    /// <summary>客户端收到包装局面后更新自己的棋局；<paramref name="mySide"/> 是本端执边（"black"/"white"/"red"）。</summary>
    protected abstract void ApplyGameSnapshot(JsonElement game, string mySide);

    /// <summary>主机执行客户端动作（例如 <c>move</c>）；执行完由子类自己调 <see cref="BroadcastSnapshot"/>。</summary>
    protected abstract void OnHostAction(string type, JsonElement? payload);

    /// <summary>主机：撤销到「请求方该走」；返回 false 表示撤不了（会回 response(false)）。</summary>
    protected abstract bool ApplyUndoFor(string requesterSide);

    /// <summary>主机：判和。</summary>
    protected abstract void ApplyDraw();

    /// <summary>主机：某方认输。</summary>
    protected abstract void ApplyResign(string side);

    /// <summary>再做一局（本地 / 主机）：重置棋盘、清历史；<paramref name="swapSides"/> 时双方换边。</summary>
    protected abstract void BeginNewGame(bool swapSides);

    /// <summary>开房成功（子类把棋局置「等待对方」）。</summary>
    protected virtual void OnHostRoomOpened() { }

    /// <summary>进入同机双人（子类重置棋局并开局）。</summary>
    protected virtual void OnLocalModeStarted() { }

    /// <summary>对手加入（主机）/ 加入成功（客户端）。</summary>
    protected virtual void OnPeerJoined() { }

    /// <summary>对方离开 / 掉线（参数是可读原因）。</summary>
    protected virtual void OnPeerLeft(string? reason) { }

    /// <summary>
    /// 断线后恢复连接（UI 线程）：
    /// 客户端 = 自动重连成功并已应用最新快照；主机 = 掉线的对方重新连回来了。
    /// 子类可在这里清「等待重连」的界面状态（<see cref="IsGamePaused"/> 此时已是 false）。
    /// </summary>
    protected virtual void OnReconnected() { }

    /// <summary>玩法变化（Offline / Local / Host / Client 互相切换时调用，UI 线程）。子类刷新「能不能下棋」之类的状态。</summary>
    protected virtual void OnModeChanged() { }

    /// <summary>
    /// 客户端收到并应用完主机的局面快照后调用（UI 线程）。
    /// 子类在这里刷新「轮到谁 / 能不能下棋 / 结果文案」等派生状态（也可以直接在 <see cref="ApplyGameSnapshot"/> 里做）。
    /// </summary>
    protected virtual void OnSnapshotApplied() { }

    /// <summary>开房前校验（例如象棋摆棋有误），返回错误文案；null = 可以开。</summary>
    protected virtual string? ValidateHostStart() => null;

    /// <summary>
    /// 「离开房间」前的确认钩子（UI 线程，只在<b>房间在线</b>时询问；默认直接同意）。
    /// 子类可重写做二次确认：返回 false 表示这次不离开（例如第一次点击只是「再点一次确认」）。
    /// <para>重连中点「停止重连」不会走这个钩子（用户已经明确要停）。</para>
    /// </summary>
    protected virtual bool ConfirmLeaveRoom() => true;

    // ---------------------------------------------------------------- 供子类使用

    /// <summary>
    /// 主机：把 <see cref="BuildGameSnapshot"/> 包一层广播（没有客户端时忽略）。
    /// <b>任何改变棋局的操作之后都由主机负责调它</b>，客户端只从快照更新。
    /// </summary>
    protected void BroadcastSnapshot()
    {
        var session = _session;
        if (session is null || !IsHost) return;
        if (!session.HasPeer) return;                    // 没客户端时不用发

        JsonElement wrapped;
        try
        {
            var seq = Interlocked.Increment(ref _snapshotSeq);
            wrapped = BoardProtocol.WrapSnapshot(seq, HostSide, OppositeSide(HostSide), BuildGameSnapshot());
        }
        catch (Exception ex)
        {
            SetNotice($"生成局面快照失败：{ex.Message}", "DangerBrush");
            return;
        }

        SendOnSession(s => s.BroadcastSnapshotAsync(wrapped));
    }

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

    /// <summary>主机侧的「对方执边」（主机视角的对面）。</summary>
    protected string PeerSideForRoom() => OppositeSide(HostSide);

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
        var validation = ValidateHostStart();
        if (!string.IsNullOrWhiteSpace(validation))
        {
            SetNotice(validation, "DangerBrush");
            return;
        }

        IsRoomBusy = true;
        var session = new BoardRoomSession(GameId, LocalName);
        WireSession(session);
        _session = session;
        SetMode(BoardPlayMode.Host);                     // 先切模式，握手事件回来时钩子看到的就是正确状态
        RaiseRoomStateChanged();
        try
        {
            var error = await session.StartHostAsync(ListenPort, BuildWelcome);
            if (error.Length > 0)
            {
                _session = null;
                SetMode(BoardPlayMode.Offline);
                await DisposeSessionAsync(session);
                SetNotice($"开房间失败：{error}", "DangerBrush");
                return;
            }

            RebuildShareRows();
            SetNotice($"房间已开（真实端口 {session.ListenPort}）"
                      + (session.IsDualStack ? "，IPv4 与 IPv6 都能连" : "")
                      + "。把下面的地址发给对方", "SuccessBrush");
            AppendNoticeChat($"房间已开：监听 {session.ListenText}");
            RaiseRoomStateChanged();
            OnHostRoomOpened();
        }
        catch (Exception ex)
        {
            _session = null;
            SetMode(BoardPlayMode.Offline);
            await DisposeSessionAsync(session);
            SetNotice($"开房间失败：{RoomLink.Describe(ex)}", "DangerBrush");
        }
        finally
        {
            IsRoomBusy = false;
        }
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
        RaiseRoomStateChanged();
        try
        {
            var error = await session.ConnectAsync(address);
            if (error.Length > 0)
            {
                _session = null;
                SetMode(BoardPlayMode.Offline);
                await DisposeSessionAsync(session);
                SetNotice($"加入房间失败：{error}", "DangerBrush");
                return;
            }

            RebuildShareRows();
            SetNotice($"已加入 {(session.PeerName.Length > 0 ? session.PeerName : "房主")} 的房间", "SuccessBrush");
            RaiseRoomStateChanged();
        }
        catch (Exception ex)
        {
            _session = null;
            SetMode(BoardPlayMode.Offline);
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
            HasPeer = false;
            PeerName = "";
            _localSide = "";
            _peerSide = "";
            _peerLeftSinceRoomOpen = false;
            ClearPending();
            RebuildShareRows();
            RaiseRoomStateChanged();
            if (wasReconnecting)
            {
                SetNotice("已停止自动重连，回到离线状态", "TextSecondaryBrush");
                AppendNoticeChat("已停止自动重连");
            }
            else
            {
                SetNotice("已离开房间", "TextSecondaryBrush");
                AppendNoticeChat("你离开了房间");
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
        HasPeer = false;
        PeerName = "";
        _localSide = "";
        _peerSide = "";
        _peerLeftSinceRoomOpen = false;
        ClearPending();
        ResetChat();
        RebuildShareRows();
        RaiseRoomStateChanged();
        SetNotice(wasReconnecting ? "已停止重连，进入同机双人" : "已进入同机双人：两个人轮流用这一台电脑下棋", "SuccessBrush");
        AppendNoticeChat("已进入同机双人");
        OnLocalModeStarted();
    }

    private BoardWelcome BuildWelcome()
    {
        var seq = Interlocked.Increment(ref _snapshotSeq);
        return new BoardWelcome(OppositeSide(HostSide),
            BoardProtocol.WrapSnapshot(seq, HostSide, OppositeSide(HostSide), BuildGameSnapshot()));
    }

    // ---------------------------------------------------------------- 会话事件接线

    private void WireSession(BoardRoomSession session)
    {
        session.StateChanged += () => UiForSession(session, RaiseRoomStateChanged);

        session.PeerJoined += name => UiForSession(session, () =>
        {
            HasPeer = true;
            PeerName = name ?? "";
            UpdateSideTexts();
            RebuildShareRows();

            if (IsReconnecting)
            {
                // 客户端自动重连成功：welcome 快照先到（已 ApplyGameSnapshot），这里收尾
                MarkReconnectSucceeded();
                try { OnReconnected(); }
                catch (Exception ex) { SetNoticeInternal($"刷新重连状态失败：{ex.Message}", "DangerBrush"); }
                return;
            }

            if (_mode == BoardPlayMode.Host && _peerLeftSinceRoomOpen)
            {
                // 主机：掉线的对方重新连回来了
                _peerLeftSinceRoomOpen = false;
                SetNoticeInternal("对方已重新连接，继续对局", "SuccessBrush");
                AddChatItem(BoardChatItem.System("对方已重新连接，继续对局"));
                RaiseRoomStateChanged();
                OnPeerJoined();
                try { OnReconnected(); }
                catch (Exception ex) { SetNoticeInternal($"刷新重连状态失败：{ex.Message}", "DangerBrush"); }
                return;
            }

            _peerLeftSinceRoomOpen = false;
            SetNoticeInternal($"{PeerName} 加入了房间", "SuccessBrush");
            AddChatItem(BoardChatItem.System($"{PeerName} 加入了房间"));
            RaiseRoomStateChanged();
            OnPeerJoined();
        });

        session.PeerLeft += reason => UiForSession(session, () =>
        {
            var who = PeerName.Length > 0 ? PeerName : "对方";
            var kind = session.LastDisconnectKind;
            var peerIsHost = !session.IsHost;                 // 本端是客户端 → 对面就是房主
            var text = FriendlyPeerLeftText(reason, kind, peerIsHost);

            HasPeer = false;
            PeerName = "";
            ClearPending();
            UpdateSideTexts();
            SetNoticeInternal(text, "DangerBrush");
            AddChatItem(BoardChatItem.System(who + "：" + text));
            RaiseRoomStateChanged();
            OnPeerLeft(text);

            if (_mode == BoardPlayMode.Host)
            {
                // 主机：棋局保持不动、继续监听等对方重连（WIP 3.6）
                _peerLeftSinceRoomOpen = true;
                SetNoticeInternal("对局暂停 · 等待对方重连（" + text + "）", "DangerBrush");
                RaiseRoomStateChanged();
                return;
            }

            // 客户端：只有「网络问题」才自动重连；房主关房 / 主动离开绝不重连
            if (_mode == BoardPlayMode.Client && ReferenceEquals(_session, session))
            {
                _session = null;
                if (kind is BoardDisconnectKind.NetworkError or BoardDisconnectKind.PeerClosed)
                {
                    _ = DisposeSessionAsync(session);
                    StartReconnect(kind);
                }
                else
                {
                    SetMode(BoardPlayMode.Offline);
                    RebuildShareRows();
                    RaiseRoomStateChanged();
                    _ = DisposeSessionAsync(session);
                }
            }
        });

        session.ChatReceived += (name, text) => UiForSession(session, () =>
            AddChatItem(BoardChatItem.Chat(name, text, isSelf: false)));

        session.NoticeReceived += (text, level) => UiForSession(session, () =>
        {
            SetNoticeInternal(text, BrushForLevel(level));
            AddChatItem(BoardChatItem.System(text));
        });

        session.SnapshotReceived += wrapped => UiForSession(session, () => ApplyWrappedSnapshot(wrapped));

        session.ActionReceived += (type, payload) => UiForSession(session, () =>
        {
            try { OnHostAction(type, payload); }
            catch (Exception ex) { SetNoticeInternal($"处理对方动作失败：{ex.Message}", "DangerBrush"); }
        });

        session.RequestReceived += (type, payload) => UiForSession(session, () => HandleIncomingRequest(type, payload));
        session.ResponseReceived += (type, accept, payload) =>
            UiForSession(session, () => HandleIncomingResponse(type, accept));
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
                // 子类钩子抛异常不能把联机流程弄断（也不该让事件线程看到）
                SetNoticeInternal($"房间操作出错：{ex.Message}", "DangerBrush");
            }
        });
    }

    private static void SendOnSessionCore(BoardRoomSession session, Func<BoardRoomSession, Task> send)
    {
        // 发送失败不该弹到界面上（对方可能已经掉了，Closed/PeerLeft 会给出提示）
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

    // ---------------------------------------------------------------- 断线重连（WIP 3.6）

    /// <summary>把「对方留下的原因」翻译成人话（本端是主机 / 客户端时措辞不一样）。</summary>
    private static string FriendlyPeerLeftText(string? reason, BoardDisconnectKind kind, bool peerIsHost)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            if (kind == BoardDisconnectKind.PeerBye) return peerIsHost ? "房主关闭了房间" : "对方离开了房间";
            return "连接意外断开";
        }

        return reason.Trim() switch
        {
            "本端离开了房间" => peerIsHost ? "房主离开了房间" : "对方离开了房间",
            "工具已关闭" => peerIsHost ? "房主关闭了房间" : "对方关闭了工具",
            "切换到同机双人" => peerIsHost ? "房主切换到了同机双人" : "对方切换到了同机双人",
            "握手超时" => "对方连接超时",
            var text => text,
        };
    }

    /// <summary>客户端意外断线：启动自动重连循环（2 秒一次，最多 30 次 / 60 秒）。</summary>
    private void StartReconnect(BoardDisconnectKind kind)
    {
        if (_closed) return;

        var address = JoinAddress.Trim();
        if (address.Length == 0)
        {
            SetMode(BoardPlayMode.Offline);
            RaiseRoomStateChanged();
            SetNoticeInternal("连接断开，而且没有可重试的房间地址，请手动加入房间", "DangerBrush");
            return;
        }

        // 单实例：每次启动都把代数 +1，旧的循环（如果还有）看到代数不对会立刻退出
        var generation = Interlocked.Increment(ref _reconnectGeneration);
        IsReconnecting = true;
        ReconnectStatusText = "连接断开，正在自动重连…";
        SetNoticeInternal("连接断开，正在自动重连…", "DangerBrush");
        AddChatItem(BoardChatItem.System("连接断开，正在自动重连…"));
        RaiseRoomStateChanged();
        _ = ReconnectLoopAsync(address, generation);
    }

    private bool IsReconnectCanceled(int generation) => Volatile.Read(ref _reconnectGeneration) != generation;

    private async Task ReconnectLoopAsync(string address, int generation)
    {
        var startedTicks = Environment.TickCount64;
        var lastError = "";
        var attempt = 0;
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
                try { error = await session.ConnectAsync(address).ConfigureAwait(false); }
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
                    // 成功：PeerJoined / SnapshotReceived 已在前面的 UI 队列里；这里兜底收尾一次
                    await UiRunAsync(() =>
                    {
                        if (ReferenceEquals(_session, session) && (IsReconnecting || _reconnectStatusText.Length > 0))
                            MarkReconnectSucceeded();
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

        if (IsReconnectCanceled(generation)) return;      // 用户主动取消 / 重新开房：什么都没发生
        await UiRunAsync(() => GiveUpReconnect(lastError, address)).ConfigureAwait(false);
    }

    /// <summary>重连成功：清掉重连/暂停状态并提示「已重新连接，继续对局」。</summary>
    private void MarkReconnectSucceeded()
    {
        IsReconnecting = false;
        ReconnectStatusText = "";
        _peerLeftSinceRoomOpen = false;
        SetNoticeInternal("已重新连接，继续对局", "SuccessBrush");
        AddChatItem(BoardChatItem.System("已重新连接，继续对局"));
        RaiseRoomStateChanged();
    }

    /// <summary>60 秒 / 30 次仍失败：停止自动重试，回到离线但保留加入地址。</summary>
    private void GiveUpReconnect(string lastError, string address)
    {
        if (IsReconnectCanceled(_reconnectGeneration)) return;   // 双保险：已经被取消了就别再改状态
        _session = null;
        SetMode(BoardPlayMode.Offline);
        IsReconnecting = false;
        ReconnectStatusText = "";
        _peerLeftSinceRoomOpen = false;
        RebuildShareRows();
        RaiseRoomStateChanged();
        var tail = lastError.Length > 0 ? $"（最后一次错误：{lastError}）" : "";
        SetNoticeInternal($"自动重连失败，请手动点「加入房间」{tail}；地址已保留：{address}", "DangerBrush");
        AddChatItem(BoardChatItem.System("自动重连失败，请手动点「加入房间」"));
    }

    /// <summary>取消正在跑的重连循环（用户离开 / 切同机双人 / 重新开房 / 关工具时调）。</summary>
    private void CancelReconnect()
    {
        // 代数 +1 = 通知循环「你已经过期了」，循环会在下一个检查点自行退出并清理自己的会话
        Interlocked.Increment(ref _reconnectGeneration);
        if (!IsReconnecting && _reconnectStatusText.Length == 0) return;
        IsReconnecting = false;
        ReconnectStatusText = "";
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
        InvalidateCommands();
    }

    /// <summary>清掉「暂停 / 重连」两个状态位（手动开房、加入、离开、切同机双人时调）。</summary>
    private void ResetPauseState()
    {
        IsReconnecting = false;
        ReconnectStatusText = "";
        _peerLeftSinceRoomOpen = false;
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
    }

    // ---------------------------------------------------------------- 快照

    private void ApplyWrappedSnapshot(JsonElement wrapped)
    {
        var envelope = BoardProtocol.ReadSnapshot(wrapped);
        if (envelope is null)
        {
            SetNoticeInternal("收到一个看不懂的局面快照，已忽略", "DangerBrush");
            return;
        }

        if (envelope.Seq > 0)
        {
            if (envelope.Seq < _snapshotSeq) return;     // 乱序的旧包，丢掉
            _snapshotSeq = envelope.Seq;
        }

        try
        {
            _localSide = envelope.ClientSide ?? "";
            _peerSide = envelope.HostSide ?? "";
            if (envelope.Game is { } game && game.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                ApplyGameSnapshot(game, _localSide);
            OnPropertyChanged(nameof(LocalSideText));
            OnPropertyChanged(nameof(PeerSideText));
            // 局面变了，「能不能下棋」之类的派生状态可能也变了，通知界面重新算一遍
            OnPropertyChanged(nameof(IsOnline));
            OnPropertyChanged(nameof(CanJoin));
            if (_hasPendingWait) ClearPendingWait();
            try { OnSnapshotApplied(); }
            catch (Exception ex) { SetNoticeInternal($"刷新棋局状态失败：{ex.Message}", "DangerBrush"); }
        }
        catch (Exception ex)
        {
            SetNoticeInternal($"同步局面失败：{ex.Message}", "DangerBrush");
        }
    }

    // ---------------------------------------------------------------- 请求 / 应答流程

    private void HandleIncomingRequest(string type, JsonElement? payload)
    {
        if (string.IsNullOrEmpty(type)) return;
        if (!IsOnline || !HasPeer)
        {
            SetNoticeInternal("收到一个请求，但对方已经不在房间里了", "DangerBrush");
            return;
        }

        // 认输不需要确认：主机直接判负并广播（客户端一般不会收到，防御性处理）
        if (type == BoardRequestTypes.Resign)
        {
            if (IsHost)
            {
                ApplyResign(PeerSideForRoom());
                BroadcastSnapshot();
                SetNoticeInternal($"{PeerName} 认输了，本局结束", "DangerBrush");
                AddChatItem(BoardChatItem.System($"{PeerName} 认输了，本局结束"));
            }
            SendOnSession(s => s.SendResponseAsync(BoardRequestTypes.Resign, true));
            return;
        }

        if (_hasPendingPrompt) return;                   // 一次只处理一个，别叠弹层

        _pendingRequestType = type;                      // payload 目前协议不用，留给以后扩展
        PendingPromptText = $"{PeerName} 请求{RequestLabel(type)}，是否同意？";
        HasPendingPrompt = true;
        SetNoticeInternal(PendingPromptText, "TextSecondaryBrush");
    }

    private async Task AcceptPendingAsync()
    {
        if (!_hasPendingPrompt) return;
        var type = _pendingRequestType;
        var session = _session;
        ClearPendingPrompt();
        if (session is null)
        {
            SetNoticeInternal("房间已经关掉了", "DangerBrush");
            return;
        }

        if (IsHost)
        {
            // 这是「对方请求、本主机同意」：请求方是对面那位
            if (!TryApplyRequest(type, PeerSideForRoom()))
            {
                SetNoticeInternal($"{RequestLabel(type)}现在执行不了，已替你拒绝", "DangerBrush");
                await SafeSendAsync(() => session.SendResponseAsync(type, false));
                return;
            }
            BroadcastSnapshot();
            await SafeSendAsync(() => session.SendResponseAsync(type, true));
            SetNoticeInternal($"已同意对方的{RequestLabel(type)}", "SuccessBrush");
            AddChatItem(BoardChatItem.System($"你同意了对方的{RequestLabel(type)}"));
            return;
        }

        // 客户端：只表示同意，等主机广播新快照
        await SafeSendAsync(() => session.SendResponseAsync(type, true));
        SetPendingWait(type, $"已同意{RequestLabel(type)}，等待房主同步局面…");
    }

    private async Task RejectPendingAsync()
    {
        if (!_hasPendingPrompt) return;
        var type = _pendingRequestType;
        var session = _session;
        ClearPendingPrompt();
        if (session is null) return;
        await SafeSendAsync(() => session.SendResponseAsync(type, false));
        SetNoticeInternal($"已拒绝对方的{RequestLabel(type)}", "TextSecondaryBrush");
        AddChatItem(BoardChatItem.System($"你拒绝了对方的{RequestLabel(type)}"));
    }

    private void HandleIncomingResponse(string type, bool accept)
    {
        if (string.IsNullOrEmpty(type)) return;

        var forMyWait = _hasPendingWait && string.Equals(_pendingWaitType, type, StringComparison.Ordinal);

        // 认输的结果由随后的快照决定，这里只给一句清楚的提示（别写成「对方已同意认输」）
        if (type == BoardRequestTypes.Resign)
        {
            if (forMyWait) ClearPendingWait();
            if (accept) SetNoticeInternal("认输已生效，等待房主同步对局结果…", "TextSecondaryBrush");
            else SetNoticeInternal("对方没有处理你的认输请求，可以再试一次", "DangerBrush");
            return;
        }

        if (!accept)
        {
            if (forMyWait) ClearPendingWait();
            SetNoticeInternal($"对方拒绝了{RequestLabel(type)}", "DangerBrush");
            AddChatItem(BoardChatItem.System($"对方拒绝了{RequestLabel(type)}"));
            return;
        }

        if (IsHost)
        {
            if (forMyWait) ClearPendingWait();
            // 这是「本主机请求、对方同意」：请求方是主机自己（悔棋要撤到主机该走）
            if (!TryApplyRequest(type, HostSide))
            {
                SetNoticeInternal($"对方同意了{RequestLabel(type)}，但现在执行不了", "DangerBrush");
                return;
            }
            BroadcastSnapshot();
            SetNoticeInternal($"对方同意了{RequestLabel(type)}", "SuccessBrush");
            AddChatItem(BoardChatItem.System($"对方同意了{RequestLabel(type)}"));
            return;
        }

        // 客户端：主机已经执行完并会广播快照，等快照到达再清掉等待状态
        if (forMyWait)
            SetPendingWait(type, $"对方已同意{RequestLabel(type)}，等待房主同步局面…");
        else
            SetNoticeInternal($"对方已同意{RequestLabel(type)}", "SuccessBrush");
    }

    /// <summary>主机执行一个被同意的请求；返回 false 表示当前状态不允许。</summary>
    /// <param name="type">请求类型（undo / draw / rematch）。</param>
    /// <param name="requesterSide">发起请求的一方执哪边（悔棋要撤到「请求方该走」，这个必须传对）。</param>
    private bool TryApplyRequest(string type, string requesterSide)
    {
        switch (type)
        {
            case BoardRequestTypes.Undo:
                return ApplyUndoFor(requesterSide);

            case BoardRequestTypes.Draw:
                ApplyDraw();
                return true;

            case BoardRequestTypes.Rematch:
                BeginNewGame(swapSides: true);
                UpdateSideTexts();
                return true;

            default:
                return false;
        }
    }

    private static string RequestLabel(string type) => type switch
    {
        BoardRequestTypes.Undo => "悔棋",
        BoardRequestTypes.Draw => "和棋",
        BoardRequestTypes.Rematch => "再来一局",
        BoardRequestTypes.Resign => "认输",
        _ => type,
    };

    private static string BrushForLevel(string level) => level switch
    {
        "warn" => "DangerBrush",
        "error" => "DangerBrush",
        "success" => "SuccessBrush",
        _ => "TextSecondaryBrush",
    };

    // ---------------------------------------------------------------- 状态维护

    private void SetMode(BoardPlayMode mode)
    {
        if (_mode == mode)
        {
            UpdateSideTexts();
            RaiseRoomStateChanged();
            return;
        }

        _mode = mode;
        UpdateSideTexts();
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsLocalMode));
        OnPropertyChanged(nameof(IsHost));
        OnPropertyChanged(nameof(IsClient));
        RaiseRoomStateChanged();
        try { OnModeChanged(); }
        catch (Exception ex) { SetNoticeInternal($"刷新界面状态失败：{ex.Message}", "DangerBrush"); }
    }

    private void UpdateSideTexts()
    {
        if (_mode == BoardPlayMode.Host)
        {
            _localSide = HostSide;
            _peerSide = string.IsNullOrEmpty(_localSide) ? "" : OppositeSide(_localSide);
        }
        else if (_mode != BoardPlayMode.Client)
        {
            _localSide = "";
            _peerSide = "";
        }
        OnPropertyChanged(nameof(LocalSideText));
        OnPropertyChanged(nameof(PeerSideText));
    }

    private void RaiseRoomStateChanged()
    {
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(CanJoin));
        OnPropertyChanged(nameof(ModeBadgeText));
        OnPropertyChanged(nameof(ModeBadgeBrushKey));
        OnPropertyChanged(nameof(ConnectionSummary));
        OnPropertyChanged(nameof(LocalSideText));
        OnPropertyChanged(nameof(PeerSideText));
        OnPropertyChanged(nameof(HasShareRows));
        OnPropertyChanged(nameof(InviteText));
        OnPropertyChanged(nameof(CanSendChat));
        OnPropertyChanged(nameof(IsReconnecting));
        OnPropertyChanged(nameof(ReconnectStatusText));
        OnPropertyChanged(nameof(IsGamePaused));
        OnPropertyChanged(nameof(GamePauseStatusText));
        OnPropertyChanged(nameof(ShowJoinControls));
        OnPropertyChanged(nameof(CanLeaveRoom));
        OnPropertyChanged(nameof(LeaveRoomText));
        InvalidateCommands();
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

        // 房间卡最多显示 3 行（其余折叠成一行提示），避免把右栏的「对局」卡挤下去
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
        HasPendingWait = false;
        PendingWaitText = "";
    }

    private void ClearPendingPrompt()
    {
        _pendingRequestType = "";
        HasPendingPrompt = false;
        PendingPromptText = "";
    }

    private void ClearPending()
    {
        ClearPendingPrompt();
        ClearPendingWait();
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

    private static async Task SafeSendAsync(Func<Task> send)
    {
        try { await send().ConfigureAwait(false); } catch { /* 对方可能已经掉了 */ }
    }

    private static async Task DisposeSessionAsync(BoardRoomSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    /// <summary>房间开关 / 连接中状态。</summary>
    public AsyncRelayCommand StartHostCommand { get; }

    /// <summary>加入别人的房间。</summary>
    public AsyncRelayCommand JoinRoomCommand { get; }

    /// <summary>离开房间（停止监听 / 断开连接）。</summary>
    public AsyncRelayCommand LeaveRoomCommand { get; }

    /// <summary>同机双人。</summary>
    public RelayCommand StartLocalCommand { get; }

    /// <summary>同意对方的请求（悔棋 / 求和 / 再来一局）。</summary>
    public RelayCommand AcceptPendingCommand { get; }

    /// <summary>拒绝对方的请求。</summary>
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
