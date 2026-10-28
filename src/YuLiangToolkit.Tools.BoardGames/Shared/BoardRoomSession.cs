using System.Net.Sockets;
using System.Text.Json;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Shared;

/// <summary>
/// 一次棋牌房间会话（主机端与客户端共用同一个门面，内部按需要建 <see cref="RoomHost"/> 或 <see cref="RoomClient"/>）。
///
/// <para><b>协议 v2：多人房</b> —— 主机同时维护多个客户端连接（房主 + 最多 15 个客户端 = 16 人），
/// 负责成员表、聊天中转、按成员 id 精确发送请求/响应；客户端只有一条连到主机的连接。
/// 房间里谁坐两个「上场位」（比赛）、谁是观战，由基类维护在 <see cref="BoardRoomState"/> 里随状态包广播，
/// <b>本类不碰任何棋局规则</b>。</para>
///
/// <para><b>线程</b>：所有事件都在后台线程触发，界面侧请用 <c>Ui(...)</c> 切回 UI 线程。
/// 事件回调的异常会被本类吞掉（不让一个界面 bug 把连接弄断）。</para>
/// </summary>
public sealed class BoardRoomSession : IAsyncDisposable
{
    /// <summary>房主固定成员 id（房主不在会话的客户端成员表里，由基类合并进房间成员列表）。</summary>
    public const string HostMemberId = "host";

    /// <summary>房间人数上限（<b>含房主</b>）：房主 + 最多 15 个客户端。</summary>
    public const int MaxMembers = 16;

    /// <summary>会话能同时接入的客户端数（不含房主）——第 17 个人会被拒绝。</summary>
    public const int MaxClients = MaxMembers - 1;

    /// <summary>
    /// 已连着的对端超过这么久没有任何数据（连心跳 pong 都没了）就当它已经死了：
    /// 同一个 MemberId 带新连接来重连时可以顶替它 —— 真实「拔网线」时旧连接不会发 FIN。
    /// </summary>
    private const int PeerStaleMs = 30_000;

    /// <summary>
    /// 客户端主动离开时，发完 bye 之后等这么久再关底层连接（毫秒）。
    /// <para>若发完立刻 close，接收缓冲区里可能还有主机刚广播来的状态帧没读走，
    /// 操作系统会按 TCP 规范发 <c>RST</c>，有概率让主机在「读到 bye」之前先看到连接重置，
    /// 把主动离开误判成 <see cref="BoardDisconnectKind.NetworkError"/>（QA 极偶发遇到过）。
    /// 这 80ms 让读循环把在途帧读干净，主机能稳稳读到 bye；正常离开只多 80ms。</para>
    /// </summary>
    private const int ClientByeDrainMs = 80;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);
    private readonly string _gameId;
    private readonly string _localName;

    private RoomHost? _host;
    private RoomClient? _client;
    private readonly List<BoardMemberInfo> _members = new();          // 主机：客户端成员（含掉线保留的）
    private readonly Dictionary<string, RoomLink> _links = new();     // 主机：memberId → 在线连接
    private readonly Dictionary<RoomLink, string> _linkMembers = new(); // 主机：连接 → memberId（握手后才有）
    private readonly Dictionary<RoomLink, string> _byeReasons = new();  // 主机：连接 → 对方 bye 的原因

    private Func<JsonElement>? _stateFactory;
    private TaskCompletionSource<WelcomeInfo>? _welcomeTcs;
    private IReadOnlyList<RoomProtocol.ShareAddress> _shareAddresses = Array.Empty<RoomProtocol.ShareAddress>();

    private string _localMemberId = "";
    private string _hostName = "";
    private string _handshakeFailReason = "";
    private int _nextMemberNumber;
    private int _nextJoinOrder;
    private int _started;
    private int _handshakeDone;
    private int _stopping;
    private BoardDisconnectKind _lastDisconnectKind = BoardDisconnectKind.None;
    private string _lastDisconnectReason = "";

    /// <summary>创建一次会话（还没开始监听 / 连接）。</summary>
    /// <param name="gameId">游戏类型：<see cref="BoardProtocol.GameGomoku"/> / <see cref="BoardProtocol.GameXiangqi"/>。</param>
    /// <param name="localName">本端玩家名（对方看到的名字）。</param>
    public BoardRoomSession(string gameId, string localName)
    {
        _gameId = (gameId ?? "").Trim();
        _localName = string.IsNullOrWhiteSpace(localName) ? "玩家" : localName.Trim();
    }

    // ---------------------------------------------------------------- 状态

    /// <summary>本端是不是主机。</summary>
    public bool IsHost => _host is not null;

    /// <summary>主机监听中 / 客户端握手完成。</summary>
    public bool IsOnline => _host?.IsListening == true || IsClientConnected;

    /// <summary>客户端：与主机握手完成（主机端恒为 false）。</summary>
    public bool IsClientConnected => _client is { IsConnected: true } && Volatile.Read(ref _handshakeDone) == 1;

    /// <summary>主机：当前在线的客户端数（不含房主；掉线保留的成员不计入）。</summary>
    public int MemberCount
    {
        get { lock (_gate) return _links.Count; }
    }

    /// <summary>本端成员 id：房主固定 <see cref="HostMemberId"/>；客户端为主机分配的 id；未进房为空。</summary>
    public string LocalMemberId => IsHost ? HostMemberId : _localMemberId;

    /// <summary>本端玩家名。</summary>
    public string LocalName => _localName;

    /// <summary>主机实际监听地址文本（例如 <c>[::]:5310</c>；没开房时为空）。</summary>
    public string ListenText => _host?.EndPoint is { } ep ? RoomProtocol.FormatEndpoint(ep.Address, ep.Port) : "";

    /// <summary>主机这次监听的端口（没开房间时是 0）。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>这次监听是不是双栈（IPv4 与 IPv6 都能连）。</summary>
    public bool IsDualStack => _host?.DualStack == true;

    /// <summary>
    /// 最近一次心跳往返延迟（毫秒）：客户端看自己到主机的；主机看所有在线客户端里最大的那个。
    /// </summary>
    public int LatencyMs
    {
        get
        {
            if (_client is not null) return _client.LatencyMs;
            lock (_gate)
            {
                var max = 0;
                foreach (var link in _links.Values)
                {
                    if (link.LatencyMs > max) max = link.LatencyMs;
                }
                return max;
            }
        }
    }

    /// <summary>本机「可以告诉对方」的地址列表（主机开房后才有；已去掉回环 / 链路本地）。</summary>
    public IReadOnlyList<RoomProtocol.ShareAddress> ShareAddresses
    {
        get { lock (_gate) return _shareAddresses; }
    }

    /// <summary>
    /// 最近一次连接结束的原因分类（决定客户端要不要自动重连，见 <see cref="BoardDisconnectKind"/>）。
    /// 客户端断开时：本属性先写好，再触发 <see cref="StateChanged"/> —— 基类在 StateChanged 里读它决定重连。
    /// </summary>
    public BoardDisconnectKind LastDisconnectKind
    {
        get { lock (_gate) return _lastDisconnectKind; }
    }

    /// <summary>最近一次连接结束的可读原因（可能是 bye 里的原因，也可能是网络错误文本）。</summary>
    public string LastDisconnectReason
    {
        get { lock (_gate) return _lastDisconnectReason; }
    }

    /// <summary>本机建议的监听地址：支持 IPv6 用 <c>::</c>（双栈），否则 <c>0.0.0.0</c>。</summary>
    public static string AutoListenAddress() => Socket.OSSupportsIPv6 ? "::" : "0.0.0.0";

    // ---------------------------------------------------------------- 事件（都在后台线程触发）

    /// <summary>状态变了（开房成功 / 连上 / 断开 / 有人进出）。客户端断开时先写好 LastDisconnectKind 再触发。</summary>
    public event Action? StateChanged;

    /// <summary>主机：新客户端握手完成（已登记进成员表，welcome 之前触发，基类据此更新房间成员）。</summary>
    public event Action<BoardMemberInfo>? MemberJoined;

    /// <summary>主机：成员离开（主动 bye / 网络断开 / 被顶替）。掉线保留时 kind 是网络类，成员仍在表里。</summary>
    public event Action<BoardMemberInfo, BoardDisconnectKind, string>? MemberLeft;

    /// <summary>主机：老成员带着原 MemberId 重连成功（已恢复在线）。</summary>
    public event Action<BoardMemberInfo>? MemberReconnected;

    /// <summary>主机：成员信息变化（例如重连时改了名字）。可选事件，基类不用也能工作。</summary>
#pragma warning disable CS0067 // 协议预留事件：目前没有独立触发点（重连信息走 MemberReconnected），保留给基类订阅
    public event Action<BoardMemberInfo>? MemberUpdated;
#pragma warning restore CS0067

    /// <summary>收到聊天：发送者成员 id, 发送者名字, 文本, 是否系统提示。</summary>
    public event Action<string, string, string, bool>? ChatReceived;

    /// <summary>收到请求：来自成员 id, 来自名字, type, payload。</summary>
    public event Action<string, string, string, JsonElement?>? RequestReceived;

    /// <summary>收到应答：来自成员 id, type, accept, payload。</summary>
    public event Action<string, string, bool, JsonElement?>? ResponseReceived;

    /// <summary>主机：收到客户端的游戏动作（memberId, type, payload）。</summary>
    public event Action<string, string, JsonElement?>? ActionReceived;

    /// <summary>客户端：收到主机的完整状态包装（<see cref="BoardStateEnvelope"/> 的 JSON）。</summary>
    public event Action<JsonElement>? StateReceived;

    // ---------------------------------------------------------------- 主机：开房

    /// <summary>
    /// 开一个房间（主机）。<paramref name="port"/> 传 0 让系统分配。
    /// <paramref name="stateFactory"/> 在<b>每个客户端握手成功时</b>调用（此时成员已登记、MemberJoined 已触发），
    /// 返回「包含新成员的完整状态包装」——会话把它放进 welcome 发给新客户端。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public async Task<string> StartHostAsync(int port, Func<JsonElement> stateFactory, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";
        if (_client is not null)
        {
            Interlocked.Exchange(ref _started, 0);
            return "客户端连接中，不能同时开房间";
        }

        _stateFactory = stateFactory;
        var host = new RoomHost();
        host.ClientConnected += OnHostClientConnected;
        host.FrameReceived += OnHostFrameReceived;
        host.ClientClosed += OnHostClientClosed;
        _host = host;

        var error = await host.StartAsync(AutoListenAddress(), port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            _host = null;
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
            Interlocked.Exchange(ref _started, 0);
            return error;
        }

        lock (_gate)
        {
            _shareAddresses = RoomProtocol.ListShareAddresses();
            _localMemberId = HostMemberId;
        }
        RaiseStateChanged();
        return "";
    }

    private void OnHostClientConnected(RoomLink link)
    {
        // 容量在 hello 里判断（这里还不知道对方是谁 / 是不是老成员重连）；什么都不做。
        _ = link;
    }

    private void OnHostFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;

        if (frame.Kind == BoardProtocol.Kinds.Hello)
        {
            _ = HandleHelloAsync(link, frame);
            return;
        }

        string memberId;
        string name;
        lock (_gate)
        {
            if (!_linkMembers.TryGetValue(link, out memberId!)) return;   // 未握手的连接发别的包：忽略
            var member = FindMemberLocked(memberId);
            if (member is null) return;
            name = member.Name;
        }

        switch (frame.Kind)
        {
            case BoardProtocol.Kinds.Chat:
                if (frame.ExtraAs<ChatInfo>() is not { } chat) return;
                Safe(() => ChatReceived?.Invoke(memberId, name, chat.Text ?? "", chat.IsSystem));
                // 客户端发来的聊天由主机转发给其他所有客户端（发送者自己已经本地显示过）
                RelayChatToOthers(link, memberId, name, chat.Text ?? "", chat.IsSystem);
                return;

            case BoardProtocol.Kinds.Action:
                if (frame.ExtraAs<ActionInfo>() is { } action)
                    Safe(() => ActionReceived?.Invoke(memberId, action.Type ?? "", action.Payload));
                return;

            case BoardProtocol.Kinds.Request:
                if (frame.ExtraAs<RequestInfo>() is { } request)
                    Safe(() => RequestReceived?.Invoke(memberId, name, request.Type ?? "", request.Payload));
                return;

            case BoardProtocol.Kinds.Response:
                if (frame.ExtraAs<ResponseInfo>() is { } response)
                    Safe(() => ResponseReceived?.Invoke(memberId, response.Type ?? "", response.Accept, response.Payload));
                return;

            case BoardProtocol.Kinds.Bye:
                // 记录原因，关闭连接后由 OnHostClientClosed 把成员从表里移除并通知基类
                if (frame.ExtraAs<ByeInfo>() is { } bye && !string.IsNullOrWhiteSpace(bye.Reason))
                {
                    lock (_gate) _byeReasons[link] = bye.Reason!;
                }
                link.Close(null);
                return;

            default:
                return;
        }
    }

    /// <summary>把聊天转发给其余在线客户端（name 用主机记录的成员名，From 用成员 id）。</summary>
    private void RelayChatToOthers(RoomLink except, string memberId, string name, string text, bool isSystem)
    {
        var host = _host;
        if (host is null) return;
        List<RoomLink> targets;
        lock (_gate) targets = _links.Values.Where(l => !ReferenceEquals(l, except) && l.IsOpen).ToList();
        if (targets.Count == 0) return;

        _ = Task.Run(async () =>
        {
            foreach (var target in targets)
            {
                try
                {
                    var frame = RoomFrame.New(BoardProtocol.Kinds.Chat, memberId,
                        new ChatInfo { Name = name, Text = text, IsSystem = isSystem });
                    await host.SendAsync(target, frame).ConfigureAwait(false);
                }
                catch { /* 单个客户端发失败不影响其它人 */ }
            }
        });
    }

    // ---------------------------------------------------------------- 主机：握手

    private async Task HandleHelloAsync(RoomLink link, RoomFrame frame)
    {
        try
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                link.Close("房间已关闭");
                return;
            }

            var hello = frame.ExtraAs<HelloInfo>();
            if (hello is null)
            {
                RejectLink(link, "握手包解析失败，请确认双方都是最新版本", "握手失败");
                return;
            }
            if (!string.Equals(hello.Magic, BoardProtocol.Magic, StringComparison.Ordinal))
            {
                RejectLink(link, "对方不是本程序的棋牌房间（协议标识不匹配）", "协议不匹配");
                return;
            }
            if (hello.ProtocolVersion != BoardProtocol.Version)
            {
                RejectLink(link,
                    $"协议版本不一致（对方 v{hello.ProtocolVersion}，本机 v{BoardProtocol.Version}），请把两边都更新到同一版本",
                    "协议版本不一致");
                return;
            }
            if (!string.Equals(hello.GameId, _gameId, StringComparison.OrdinalIgnoreCase))
            {
                RejectLink(link, $"游戏类型不一致（对方要玩「{hello.GameId}」，本房间是「{_gameId}」）", "游戏类型不一致");
                return;
            }

            var name = string.IsNullOrWhiteSpace(hello.PlayerName) ? "玩家" : hello.PlayerName.Trim();

            await _helloGate.WaitAsync().ConfigureAwait(false);
            try
            {
                RoomLink? staleToClose = null;
                BoardMemberInfo member;
                bool isNew = false;

                lock (_gate)
                {
                    var requestedId = (hello.MemberId ?? "").Trim();
                    var existing = requestedId.Length == 0 ? null : FindMemberLocked(requestedId);
                    if (existing is not null)
                    {
                        if (_links.TryGetValue(existing.MemberId, out var oldLink))
                        {
                            if (oldLink.IdleMs <= PeerStaleMs)
                            {
                                // 旧连接还活着（有心跳）：不许顶号，避免两个人互相把对方挤下线
                                RejectLink(link, "这个成员已经在房间里了（旧连接还活着，不能重复登录）", "重复登录");
                                return;
                            }
                            // 旧连接只剩一个僵死的 TCP 壳（拔网线 / 断电）：让新连接顶替它
                            _linkMembers.Remove(oldLink);
                            _links.Remove(existing.MemberId);
                            staleToClose = oldLink;
                        }

                        member = existing;
                        member.Name = name;
                        member.IsOnline = true;
                        _linkMembers[link] = member.MemberId;
                        _links[member.MemberId] = link;
                    }
                    else
                    {
                        if (_members.Count >= MaxClients)
                        {
                            RejectLink(link, $"房间已满（最多 {MaxMembers} 人，含房主）", "房间已满");
                            return;
                        }

                        member = new BoardMemberInfo
                        {
                            MemberId = $"m{++_nextMemberNumber}",
                            Name = name,
                            IsHost = false,
                            IsOnline = true,
                            GamesPlayed = 0,
                            JoinOrder = ++_nextJoinOrder,
                        };
                        _members.Add(member);
                        _linkMembers[link] = member.MemberId;
                        _links[member.MemberId] = link;
                        isNew = true;
                    }
                }

                // 僵死的旧连接在新连接登记好之后再关，这样它的 Closed 回调找不到映射、不会误报 MemberLeft
                if (staleToClose is not null)
                {
                    try { staleToClose.Close("该成员已从新连接登录"); } catch { /* 已经断了 */ }
                }

                var memberSnapshot = member.Clone();
                if (isNew) Safe(() => MemberJoined?.Invoke(memberSnapshot));
                else Safe(() => MemberReconnected?.Invoke(memberSnapshot));

                // 顺序要求：成员已登记 + 事件已触发（基类把成员/席位更新好）→ 再取状态 → 发 welcome
                JsonElement state = default;
                try
                {
                    state = _stateFactory?.Invoke() ?? default;
                }
                catch (Exception ex)
                {
                    RollbackHandshake(link, member.MemberId, isNew);
                    link.Close($"生成房间状态失败：{ex.Message}");
                    return;
                }

                var welcome = new WelcomeInfo
                {
                    Magic = BoardProtocol.Magic,
                    GameId = _gameId,
                    HostName = _localName,
                    ProtocolVersion = BoardProtocol.Version,
                    YourMemberId = member.MemberId,
                    State = state.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : state,
                };

                var sent = await SendFrameSafeAsync(link, BoardProtocol.Kinds.Welcome, member.MemberId, welcome)
                    .ConfigureAwait(false);
                if (!sent)
                {
                    RollbackHandshake(link, member.MemberId, isNew);
                    link.Close("握手回执发送失败");
                    return;
                }

                RaiseStateChanged();
            }
            finally
            {
                _helloGate.Release();
            }
        }
        catch (Exception ex)
        {
            try { link.Close($"握手失败：{RoomLink.Describe(ex)}"); } catch { /* 忽略 */ }
        }
    }

    /// <summary>握手最后一步失败时回滚：拆掉连接映射、删掉新成员（或把老成员标回离线）。</summary>
    private void RollbackHandshake(RoomLink link, string memberId, bool isNew)
    {
        lock (_gate)
        {
            if (_linkMembers.TryGetValue(link, out var mapped) && mapped == memberId) _linkMembers.Remove(link);
            if (_links.TryGetValue(memberId, out var mappedLink) && ReferenceEquals(mappedLink, link)) _links.Remove(memberId);

            var member = FindMemberLocked(memberId);
            if (member is not null)
            {
                if (isNew) _members.Remove(member);
                else member.IsOnline = false;
            }
        }
        RaiseStateChanged();
    }

    private void OnHostClientClosed(RoomLink link, string? error)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        string? memberId = null;
        string reason = "";
        bool removedByBye = false;
        BoardMemberInfo? memberSnapshot = null;

        lock (_gate)
        {
            if (!_linkMembers.TryGetValue(link, out var id)) return;   // 未握手的连接 / 已被顶替的旧连接
            _linkMembers.Remove(link);
            memberId = id;

            var member = FindMemberLocked(id);
            if (member is null) return;

            if (_byeReasons.TryGetValue(link, out var byeReason))
            {
                _byeReasons.Remove(link);
                removedByBye = true;
                reason = string.IsNullOrWhiteSpace(byeReason) ? "离开了房间" : byeReason;
                member.IsOnline = false;
                memberSnapshot = member.Clone();
                _members.Remove(member);
            }
            else
            {
                member.IsOnline = false;
                memberSnapshot = member.Clone();
            }

            if (_links.TryGetValue(id, out var current) && ReferenceEquals(current, link)) _links.Remove(id);
        }

        var kind = removedByBye
            ? BoardDisconnectKind.PeerBye
            : error is { Length: > 0 } ? BoardDisconnectKind.NetworkError : BoardDisconnectKind.PeerClosed;
        if (!removedByBye) reason = error is { Length: > 0 } ? error : "连接断开了";

        RaiseStateChanged();
        if (memberSnapshot is not null)
            Safe(() => MemberLeft?.Invoke(memberSnapshot, kind, reason));
    }

    /// <summary>拒绝一个连接：先发一条提示与 bye，再关掉（让对方界面能显示原因）。</summary>
    private void RejectLink(RoomLink link, string noticeText, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await link.SendAsync(RoomFrame.New(BoardProtocol.Kinds.Notice, HostMemberId,
                    new NoticeInfo { Text = noticeText, Level = "warn" })).ConfigureAwait(false);
                await link.SendAsync(RoomFrame.New(BoardProtocol.Kinds.Bye, HostMemberId,
                    new ByeInfo { Reason = reason })).ConfigureAwait(false);
            }
            catch { /* 对方已经断了 */ }
            link.Close(reason);
        });
    }

    // ---------------------------------------------------------------- 客户端：连接 / 收帧

    /// <summary>
    /// 连接主机（客户端）：发 <c>hello</c> → 等 <c>welcome</c>（10 秒超时）。
    /// <paramref name="addressText"/> 可以带端口（<c>192.168.1.5:5310</c>），
    /// <paramref name="port"/> 填 0 时用它带的那个。
    /// <paramref name="previousMemberId"/> 是上次连接时主机分配的成员 id（重连用；空 = 首次进房）。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public async Task<string> ConnectAsync(string addressText, int port = 0, string? previousMemberId = null,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";
        if (_host is not null)
        {
            Interlocked.Exchange(ref _started, 0);
            return "主机端不能同时当客户端（先关房间）";
        }

        var client = new RoomClient();
        client.FrameReceived += OnClientFrameReceived;
        client.Closed += OnClientClosed;
        _client = client;

        var error = await client.ConnectAsync(addressText, port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            _client = null;
            Interlocked.Exchange(ref _started, 0);
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
            return error;
        }

        var welcomeTcs = new TaskCompletionSource<WelcomeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _welcomeTcs = welcomeTcs;
        _handshakeFailReason = "";

        var hello = new HelloInfo
        {
            Magic = BoardProtocol.Magic,
            GameId = _gameId,
            PlayerName = _localName,
            ProtocolVersion = BoardProtocol.Version,
            MemberId = (previousMemberId ?? "").Trim(),
        };
        await client.SendAsync(RoomFrame.New(BoardProtocol.Kinds.Hello, _localName, hello), ct).ConfigureAwait(false);

        var completed = await Task.WhenAny(welcomeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
        if (completed != welcomeTcs.Task)
        {
            await StopAsync("握手超时").ConfigureAwait(false);
            return "连上了但主机没有回应握手（对方可能不是棋牌房间，或者被防火墙拦了包）";
        }

        WelcomeInfo welcome;
        try
        {
            welcome = await welcomeTcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StopAsync("握手失败").ConfigureAwait(false);
            return $"握手失败：{ex.Message}";
        }

        lock (_gate)
        {
            _localMemberId = (welcome.YourMemberId ?? "").Trim();
            _hostName = string.IsNullOrWhiteSpace(welcome.HostName) ? "房主" : welcome.HostName.Trim();
        }
        Volatile.Write(ref _handshakeDone, 1);
        RaiseStateChanged();

        // welcome 里带的完整状态也走 StateReceived（基类只认这一条路径）
        if (welcome.State is { } state && state.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            Safe(() => StateReceived?.Invoke(state));
        return "";
    }

    private void OnClientFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;
        var handshaken = Volatile.Read(ref _handshakeDone) == 1;

        switch (frame.Kind)
        {
            case BoardProtocol.Kinds.Welcome:
            {
                var welcome = frame.ExtraAs<WelcomeInfo>();
                if (welcome is null)
                {
                    FailHandshake(link, "握手回执解析失败");
                    return;
                }
                if (!string.Equals(welcome.Magic, BoardProtocol.Magic, StringComparison.Ordinal))
                {
                    FailHandshake(link, "对方不是本程序的棋牌房间（协议标识不匹配）");
                    return;
                }
                if (welcome.ProtocolVersion != BoardProtocol.Version)
                {
                    FailHandshake(link,
                        $"协议版本不一致（主机 v{welcome.ProtocolVersion}，本机 v{BoardProtocol.Version}），请把两边都更新到同一版本");
                    return;
                }
                if (!string.Equals(welcome.GameId, _gameId, StringComparison.OrdinalIgnoreCase))
                {
                    FailHandshake(link, $"游戏类型不一致（主机开的是「{welcome.GameId}」，本端是「{_gameId}」）");
                    return;
                }
                _welcomeTcs?.TrySetResult(welcome);
                return;
            }

            case BoardProtocol.Kinds.Notice:
            {
                var notice = frame.ExtraAs<NoticeInfo>();
                if (notice is null) return;
                if (!handshaken)
                {
                    // 握手期间主机发来的提示（房间已满 / 重复登录 / 版本不一致）——用于拼错误文案
                    _handshakeFailReason = notice.Text ?? "";
                    return;
                }
                // 后握手阶段的系统提示统一转成「系统聊天」交给基类显示
                Safe(() => ChatReceived?.Invoke(frame.From ?? HostMemberId, SenderName(frame.From), notice.Text ?? "", true));
                return;
            }

            case BoardProtocol.Kinds.Bye:
            {
                var bye = frame.ExtraAs<ByeInfo>();
                lock (_gate)
                {
                    _gotBye = true;
                    if (!string.IsNullOrWhiteSpace(bye?.Reason)) _lastDisconnectReason = bye!.Reason!;
                }
                if (!handshaken)
                {
                    _welcomeTcs?.TrySetException(new IOException(
                        _handshakeFailReason.Length > 0 ? _handshakeFailReason
                            : (bye?.Reason is { Length: > 0 } byeReason ? byeReason : "被主机拒绝加入房间")));
                }
                link.Close(null);
                return;
            }

            case BoardProtocol.Kinds.State:
                if (!handshaken) return;
                if (frame.ExtraAs<StateInfo>() is { } stateInfo && stateInfo.State is { } state)
                    Safe(() => StateReceived?.Invoke(state));
                return;

            case BoardProtocol.Kinds.Chat:
                if (!handshaken) return;
                if (frame.ExtraAs<ChatInfo>() is { } chat)
                    Safe(() => ChatReceived?.Invoke(frame.From ?? "", chat.Name ?? "", chat.Text ?? "", chat.IsSystem));
                return;

            case BoardProtocol.Kinds.Request:
                if (!handshaken) return;
                if (frame.ExtraAs<RequestInfo>() is { } request)
                    Safe(() => RequestReceived?.Invoke(frame.From ?? HostMemberId, SenderName(frame.From),
                        request.Type ?? "", request.Payload));
                return;

            case BoardProtocol.Kinds.Response:
                if (!handshaken) return;
                if (frame.ExtraAs<ResponseInfo>() is { } response)
                    Safe(() => ResponseReceived?.Invoke(frame.From ?? HostMemberId, response.Type ?? "",
                        response.Accept, response.Payload));
                return;

            default:
                return;
        }
    }

    /// <summary>客户端的「发送者名字」：主机 → 房主名；其他（客户端转达）暂时留空，由基类用通用文案。</summary>
    private string SenderName(string? memberId)
        => string.Equals(memberId, HostMemberId, StringComparison.Ordinal) ? _hostName : "";

    private void FailHandshake(RoomLink link, string reason)
    {
        _handshakeFailReason = reason;
        try
        {
            _welcomeTcs?.TrySetException(new IOException(reason));
        }
        catch { /* 忽略 */ }
        link.Close(reason);
    }

    private void OnClientClosed(RoomLink link, string? error)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        var handshaken = Volatile.Read(ref _handshakeDone) == 1;
        if (!handshaken)
        {
            // 握手还没完成就断了：叫醒 ConnectAsync 里的等待
            var reason = _handshakeFailReason.Length > 0
                ? _handshakeFailReason
                : error is { Length: > 0 } ? error : "对方关闭了连接";
            _welcomeTcs?.TrySetException(new IOException(reason));
            return;
        }

        Volatile.Write(ref _handshakeDone, 0);
        var byeReason = "";
        lock (_gate) byeReason = _lastDisconnectReason;
        var hadBye = LastByeSeen();
        var reason2 = hadBye
            ? (byeReason.Length > 0 ? byeReason : "房主关闭了房间")
            : error is { Length: > 0 } ? error : "房主关闭了房间";
        var kind = hadBye
            ? BoardDisconnectKind.PeerBye
            : error is { Length: > 0 } ? BoardDisconnectKind.NetworkError : BoardDisconnectKind.PeerClosed;
        lock (_gate)
        {
            _lastDisconnectKind = kind;
            _lastDisconnectReason = reason2;
        }
        RaiseStateChanged();
    }

    /// <summary>客户端收到过 bye（用 LastDisconnectReason 里记的原因无法区分 bye/网络错误，单开一个标记）。</summary>
    private bool LastByeSeen()
    {
        lock (_gate) return _gotBye;
    }

    private bool _gotBye;

    // ---------------------------------------------------------------- 主机：发送

    /// <summary>主机：把完整状态包装广播给所有在线客户端（没有客户端时静默忽略）。</summary>
    public async Task BroadcastStateAsync(JsonElement envelope, CancellationToken ct = default)
    {
        var host = _host;
        if (host is null) return;
        List<RoomLink> targets;
        lock (_gate) targets = _links.Values.Where(l => l.IsOpen).ToList();
        foreach (var target in targets)
        {
            try
            {
                var frame = RoomFrame.New(BoardProtocol.Kinds.State, HostMemberId,
                    new StateInfo { State = envelope });
                await host.SendAsync(target, frame, ct).ConfigureAwait(false);
            }
            catch { /* 单个客户端发失败不影响其它人 */ }
        }
    }

    /// <summary>主机：把一条聊天/系统提示广播给所有客户端（主机本地由基类自己加，不会回环给自己）。</summary>
    public async Task BroadcastChatAsync(string name, string text, bool isSystem = false)
    {
        var host = _host;
        if (host is null) return;
        List<RoomLink> targets;
        lock (_gate) targets = _links.Values.Where(l => l.IsOpen).ToList();
        foreach (var target in targets)
        {
            try
            {
                var frame = RoomFrame.New(BoardProtocol.Kinds.Chat, HostMemberId,
                    new ChatInfo { Name = name ?? "", Text = text ?? "", IsSystem = isSystem });
                await host.SendAsync(target, frame).ConfigureAwait(false);
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>主机：给所有客户端发一条系统提示（客户端会当成系统聊天显示）。</summary>
    public async Task BroadcastNoticeAsync(string text, string level = "info")
    {
        var host = _host;
        if (host is null) return;
        List<RoomLink> targets;
        lock (_gate) targets = _links.Values.Where(l => l.IsOpen).ToList();
        foreach (var target in targets)
        {
            try
            {
                var frame = RoomFrame.New(BoardProtocol.Kinds.Notice, HostMemberId,
                    new NoticeInfo { Text = text ?? "", Level = level ?? "info" });
                await host.SendAsync(target, frame).ConfigureAwait(false);
            }
            catch { /* 忽略 */ }
        }
    }

    /// <summary>主机：把请求精确发给某个在线成员（多客户端时必须按 id 找对人）。</summary>
    public Task SendRequestToAsync(string memberId, string type, object? payload = null)
        => SendToMemberAsync(memberId, BoardProtocol.Kinds.Request, HostMemberId,
            new RequestInfo { Type = type ?? "", Payload = ToElement(payload) });

    /// <summary>主机：把应答精确发给某个在线成员。</summary>
    public Task SendResponseToAsync(string memberId, string type, bool accept, object? payload = null)
        => SendToMemberAsync(memberId, BoardProtocol.Kinds.Response, HostMemberId,
            new ResponseInfo { Type = type ?? "", Accept = accept, Payload = ToElement(payload) });

    private async Task SendToMemberAsync(string memberId, string kind, string from, object? extra)
    {
        var host = _host;
        if (host is null || string.IsNullOrWhiteSpace(memberId)) return;
        RoomLink? link;
        lock (_gate) _links.TryGetValue(memberId, out link);
        if (link is null || !link.IsOpen) return;
        try
        {
            await host.SendAsync(link, RoomFrame.New(kind, from, extra)).ConfigureAwait(false);
        }
        catch { /* 对方可能已经掉了 */ }
    }

    // ---------------------------------------------------------------- 客户端：发送

    /// <summary>客户端：发聊天（主机收到后转发给其他客户端，同时记到房主的聊天记录里）。</summary>
    public Task SendChatAsync(string text)
        => SendFromClientAsync(BoardProtocol.Kinds.Chat,
            new ChatInfo { Name = _localName, Text = text ?? "", IsSystem = false });

    /// <summary>客户端：上报一个游戏动作（例如 <c>SendActionAsync("move", new { x, y })</c>）。</summary>
    public Task SendActionAsync(string type, object? payload = null)
        => SendFromClientAsync(BoardProtocol.Kinds.Action,
            new ActionInfo { Type = type ?? "", Payload = ToElement(payload) });

    /// <summary>客户端：发请求（悔棋 / 求和 / 再来一局 / 认输）。</summary>
    public Task SendRequestAsync(string type, object? payload = null)
        => SendFromClientAsync(BoardProtocol.Kinds.Request,
            new RequestInfo { Type = type ?? "", Payload = ToElement(payload) });

    /// <summary>客户端：回应主机转达的请求。</summary>
    public Task SendResponseAsync(string type, bool accept, object? payload = null)
        => SendFromClientAsync(BoardProtocol.Kinds.Response,
            new ResponseInfo { Type = type ?? "", Accept = accept, Payload = ToElement(payload) });

    private async Task SendFromClientAsync(string kind, object? extra)
    {
        var client = _client;
        if (client is null) return;
        try
        {
            await client.SendAsync(RoomFrame.New(kind, _localMemberId, extra)).ConfigureAwait(false);
        }
        catch { /* 连接可能已经断了 */ }
    }

    private static JsonElement? ToElement(object? payload)
        => payload switch
        {
            null => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(payload, RoomProtocol.JsonOptions),
        };

    // ---------------------------------------------------------------- 收尾

    /// <summary>停止监听 / 断开连接（幂等）。会先给对端发 <c>bye</c>，让对方立刻知道不是掉线。</summary>
    public async Task StopAsync(string reason = "本端离开了房间")
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        var stopReason = string.IsNullOrWhiteSpace(reason) ? "本端离开了房间" : reason;
        lock (_gate)
        {
            _lastDisconnectKind = BoardDisconnectKind.LocalLeave;
            _lastDisconnectReason = stopReason;
            _gotBye = false;
        }

        var host = _host;
        var client = _client;
        _host = null;
        _client = null;
        Volatile.Write(ref _handshakeDone, 0);

        var bye = RoomFrame.New(BoardProtocol.Kinds.Bye, HostMemberId, new ByeInfo { Reason = stopReason });
        if (host is not null)
        {
            List<RoomLink> links;
            lock (_gate) links = _links.Values.Where(l => l.IsOpen).ToList();
            foreach (var link in links)
            {
                try { await host.SendAsync(link, bye).ConfigureAwait(false); } catch { /* 已经断了 */ }
            }
        }
        if (client is not null)
        {
            try { await client.SendAsync(bye).ConfigureAwait(false); } catch { /* 已经断了 */ }

            // 短暂的「排空」窗口：让读循环把在途帧读掉、让主机先读到 bye，再关连接。
            // 否则 close 时接收缓冲区非空会触发 RST，主机可能把主动离开误判成网络断开。
            try { await Task.Delay(ClientByeDrainMs).ConfigureAwait(false); } catch { /* 忽略 */ }
        }

        try { if (host is not null) await host.StopAsync(stopReason).ConfigureAwait(false); } catch { /* 忽略 */ }
        try { if (client is not null) await client.CloseAsync("").ConfigureAwait(false); } catch { /* 忽略 */ }

        lock (_gate)
        {
            _members.Clear();
            _links.Clear();
            _linkMembers.Clear();
            _byeReasons.Clear();
            _shareAddresses = Array.Empty<RoomProtocol.ShareAddress>();
        }
        RaiseStateChanged();

        var tcs = _welcomeTcs;
        _welcomeTcs = null;
        if (tcs is not null && !tcs.Task.IsCompleted)
            tcs.TrySetException(new IOException(stopReason));
    }

    /// <summary>
    /// 「网络断了」：直接掐掉底层连接（<b>不发 bye</b>），对方只会看到连接被重置。
    /// <para><b>用途</b>：自检 / 排错时模拟断网，验证自动重连与主机「成员掉线保留」。
    /// 正常离开请用 <see cref="StopAsync"/>（会先发 bye）。</para>
    /// </summary>
    public void AbortConnection(string reason = "网络连接中断")
    {
        var client = _client;
        if (client is not null)
        {
            client.Abort(reason);
            return;
        }

        List<RoomLink> links;
        lock (_gate) links = _links.Values.ToList();
        foreach (var link in links)
        {
            try { link.Close(reason); } catch { /* 已经断了 */ }
        }
    }

    // ---------------------------------------------------------------- 小工具

    private BoardMemberInfo? FindMemberLocked(string memberId)
        => _members.FirstOrDefault(m => string.Equals(m.MemberId, memberId, StringComparison.Ordinal));

    private async Task<bool> SendFrameSafeAsync(RoomLink link, string kind, string from, object? extra)
    {
        try
        {
            await link.SendAsync(RoomFrame.New(kind, from, extra)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void RaiseStateChanged() => Safe(() => StateChanged?.Invoke());

    /// <summary>事件回调的异常不能漏到读循环里（否则一个界面 bug 会把连接弄断）。</summary>
    private static void Safe(Action action)
    {
        try { action(); } catch { /* 忽略 */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        try { _helloGate.Dispose(); } catch { /* 忽略 */ }
    }
}
