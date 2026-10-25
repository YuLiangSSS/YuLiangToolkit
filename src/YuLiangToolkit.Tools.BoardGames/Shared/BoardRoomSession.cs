using System.Net.Sockets;
using System.Text.Json;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Shared;

/// <summary>
/// 一次棋牌房间会话（主机端与客户端共用同一个门面，内部按需要建 <see cref="RoomHost"/> 或 <see cref="RoomClient"/>）。
///
/// <para><b>职责</b>：握手、房间成员（主机 + 1 客户端，第三人拒绝并提示房间已满；客户端掉线后允许重新加入）、
/// 转发聊天 / 提示 / 请求 / 响应 / 动作 / 快照。<b>这里不碰任何棋局规则</b>——
/// 局面由主机以「包装局面」的形式透传，客户端只认快照。</para>
///
/// <para><b>线程</b>：所有事件都可能从后台线程触发，界面侧请用 <c>Ui(...)</c> 切回 UI 线程。
/// 事件回调的异常会被本类吞掉（不让一个界面 bug 把连接弄断）。</para>
/// </summary>
public sealed class BoardRoomSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);
    private readonly string _gameId;
    private readonly string _localName;

    private RoomHost? _host;
    private RoomClient? _client;
    private RoomLink? _peer;                       // 主机：当前客户端连接；客户端：连主机那条连接
    private Func<BoardWelcome>? _welcomeFactory;
    private TaskCompletionSource<WelcomeInfo>? _welcomeTcs;

    private string _peerName = "";
    private string _byeReason = "";
    private IReadOnlyList<RoomProtocol.ShareAddress> _shareAddresses = Array.Empty<RoomProtocol.ShareAddress>();
    private int _started;
    private int _handshakeDone;
    private int _stopping;
    private BoardDisconnectKind _lastDisconnectKind = BoardDisconnectKind.None;
    private string _lastDisconnectReason = "";

    /// <summary>
    /// 已连着的对端超过这么久没有任何数据（连心跳 pong 都没了）就当它已经死了，
    /// 允许新连接顶替它 —— 真实「拔网线」时对端不会发 FIN，主机的旧连接会一直挂着直到 90 秒超时，
    /// 不主动让位的话掉线重连会被当成「第三人」拒在门外。
    /// </summary>
    private const int PeerStaleMs = 30_000;

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

    /// <summary>主机监听中 / 客户端已连接（TCP 层）。</summary>
    public bool IsOnline => _host?.IsListening == true
                            || (_client is { IsConnected: true } && Volatile.Read(ref _handshakeDone) == 1);

    /// <summary>对方在线：主机 = 客户端已握手；客户端 = 已连上主机并握手完成。</summary>
    public bool HasPeer
    {
        get
        {
            if (_host is not null)
            {
                lock (_gate) return _peer is { IsOpen: true };
            }
            return _client is { IsConnected: true } && Volatile.Read(ref _handshakeDone) == 1;
        }
    }

    /// <summary>对方玩家名（没人在线时为空）。</summary>
    public string PeerName
    {
        get { lock (_gate) return _peerName; }
    }

    /// <summary>主机实际监听地址文本（例如 <c>[::]:5310</c>；没开房时为空）。</summary>
    public string ListenText => _host?.EndPoint is { } ep ? RoomProtocol.FormatEndpoint(ep.Address, ep.Port) : "";

    /// <summary>主机这次监听的端口（没开房间时是 0）。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>这次监听是不是双栈（IPv4 与 IPv6 都能连）。</summary>
    public bool IsDualStack => _host?.DualStack == true;

    /// <summary>最近一次心跳往返延迟（毫秒；主机端取对端连接的值）。</summary>
    public int LatencyMs => _client?.LatencyMs ?? _peer?.LatencyMs ?? 0;

    /// <summary>本机「可以告诉对方」的地址列表（主机开房后才有；已去掉回环 / 链路本地）。</summary>
    public IReadOnlyList<RoomProtocol.ShareAddress> ShareAddresses
    {
        get { lock (_gate) return _shareAddresses; }
    }

    /// <summary>
    /// 最近一次连接结束的原因分类（决定客户端要不要自动重连，见 <see cref="BoardDisconnectKind"/>）。
    /// 客户端拿到 <c>PeerLeft</c> 之后读它来决定「房主关房（不重连）」还是「网络断了（重连）」。
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

    /// <summary>状态变了（开房成功 / 连上 / 断开 / 有人进出）。</summary>
    public event Action? StateChanged;

    /// <summary>握手成功。主机：参数=客户端玩家名；客户端：参数=主机玩家名。</summary>
    public event Action<string>? PeerJoined;

    /// <summary>对方断开 / 被关房（参数=可读原因，可能为空）。</summary>
    public event Action<string>? PeerLeft;

    /// <summary>收到聊天：发送者名字, 文本。</summary>
    public event Action<string, string>? ChatReceived;

    /// <summary>收到系统提示：文本, level（info / warn / error）。</summary>
    public event Action<string, string>? NoticeReceived;

    /// <summary>客户端收到包装后的局面（已包含 seq / hostSide / clientSide / game）。</summary>
    public event Action<JsonElement>? SnapshotReceived;

    /// <summary>主机收到客户端动作：type, payload。</summary>
    public event Action<string, JsonElement?>? ActionReceived;

    /// <summary>收到请求：type, payload。</summary>
    public event Action<string, JsonElement?>? RequestReceived;

    /// <summary>收到应答：type, accept, payload。</summary>
    public event Action<string, bool, JsonElement?>? ResponseReceived;

    // ---------------------------------------------------------------- 开房 / 连接

    /// <summary>
    /// 开一个房间（主机）。<paramref name="port"/> 传 0 让系统分配。
    /// <paramref name="welcomeFactory"/> 在每次客户端（含重连）握手成功时调用，返回「客户端执哪边 + 当前局面包装」。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public async Task<string> StartHostAsync(int port, Func<BoardWelcome> welcomeFactory,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";
        if (_client is not null)
        {
            Interlocked.Exchange(ref _started, 0);
            return "客户端连接中，不能同时开房间";
        }

        _welcomeFactory = welcomeFactory;
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

        lock (_gate) _shareAddresses = RoomProtocol.ListShareAddresses();
        RaiseStateChanged();
        return "";
    }

    /// <summary>
    /// 连接主机（客户端）：发 <c>hello</c> → 等 <c>welcome</c>（10 秒超时）。
    /// <paramref name="addressText"/> 可以带端口（<c>192.168.1.5:5310</c>），
    /// <paramref name="port"/> 填 0 时用它带的那个。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public async Task<string> ConnectAsync(string addressText, int port = 0, CancellationToken ct = default)
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

        var hello = new HelloInfo
        {
            Magic = BoardProtocol.Magic,
            GameId = _gameId,
            PlayerName = _localName,
            ProtocolVersion = BoardProtocol.Version,
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
            _peer = null;                                   // 客户端不用 _peer 存链接，发送走 RoomClient
            _peerName = string.IsNullOrWhiteSpace(welcome.HostName) ? "房主" : welcome.HostName.Trim();
        }
        Volatile.Write(ref _handshakeDone, 1);
        RaiseStateChanged();

        if (welcome.Snapshot is { } snapshot && snapshot.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            Safe(() => SnapshotReceived?.Invoke(snapshot));
        var hostName = PeerName;
        Safe(() => PeerJoined?.Invoke(hostName));
        return "";
    }

    /// <summary>停止监听 / 断开连接（幂等）。会先给对端发一个 <c>bye</c>，让对方立刻知道不是掉线。</summary>
    public async Task StopAsync(string reason = "本端离开了房间")
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        var stopReason = string.IsNullOrWhiteSpace(reason) ? "本端离开了房间" : reason;

        // 主动离开：先把分类记下来（对方发 bye 也会收到，但本端不再重连）
        lock (_gate)
        {
            _lastDisconnectKind = BoardDisconnectKind.LocalLeave;
            _lastDisconnectReason = stopReason;
        }

        var host = _host;
        var client = _client;
        RoomLink? peer;
        lock (_gate)
        {
            peer = _peer;
            _peer = null;
            _peerName = "";
        }
        _host = null;
        _client = null;

        var bye = RoomFrame.New(BoardProtocol.Kinds.Bye, _localName, new ByeInfo { Reason = stopReason });
        try
        {
            if (host is not null && peer is { IsOpen: true })
                await host.SendAsync(peer, bye).ConfigureAwait(false);
            if (client is not null)
                await client.SendAsync(bye).ConfigureAwait(false);
        }
        catch { /* 已经断了就算了 */ }

        try { if (host is not null) await host.StopAsync(stopReason).ConfigureAwait(false); } catch { /* 忽略 */ }
        try { if (client is not null) await client.CloseAsync("").ConfigureAwait(false); } catch { /* 忽略 */ }

        Interlocked.Exchange(ref _handshakeDone, 0);
        lock (_gate) _shareAddresses = Array.Empty<RoomProtocol.ShareAddress>();
        RaiseStateChanged();

        var tcs = _welcomeTcs;
        _welcomeTcs = null;
        if (tcs is not null && !tcs.Task.IsCompleted)
            tcs.TrySetException(new IOException(stopReason));
    }

    /// <summary>
    /// 「网络断了」：直接掐掉底层连接（<b>不发 bye</b>），对方只会看到连接被重置。
    /// <para><b>用途</b>：自检 / 排错时模拟断网，验证自动重连与主机「对局暂停」。
    /// 正常离开请用 <see cref="StopAsync"/>（会先发 bye，对方不会误以为网络故障）。</para>
    /// </summary>
    public void AbortConnection(string reason = "网络连接中断")
    {
        var client = _client;
        if (client is not null)
        {
            client.Abort(reason);
            return;
        }

        RoomLink? peer;
        lock (_gate) peer = _peer;
        try { peer?.Close(reason); } catch { /* 已经断了 */ }
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>主机：把包装后的局面广播给客户端（没有客户端时静默忽略）。</summary>
    public async Task BroadcastSnapshotAsync(JsonElement snapshot, string? notice = null)
    {
        var host = _host;
        RoomLink? peer;
        lock (_gate) peer = _peer;
        if (host is null || peer is not { IsOpen: true }) return;

        var info = new SnapshotInfo { Snapshot = snapshot, Notice = notice };
        await host.SendAsync(peer, RoomFrame.New(BoardProtocol.Kinds.Snapshot, _localName, info)).ConfigureAwait(false);
    }

    /// <summary>发聊天（主机 → 客户端；客户端 → 主机）。</summary>
    public Task SendChatAsync(string text)
        => SendToPeerAsync(BoardProtocol.Kinds.Chat, new ChatInfo { Name = _localName, Text = text ?? "" });

    /// <summary>发系统提示（info / warn / error）。</summary>
    public Task SendNoticeAsync(string text, string level = "info")
        => SendToPeerAsync(BoardProtocol.Kinds.Notice, new NoticeInfo { Text = text ?? "", Level = level ?? "info" });

    /// <summary>客户端：上报游戏动作。</summary>
    public Task SendActionAsync(string type, object? payload = null)
        => SendToPeerAsync(BoardProtocol.Kinds.Action, new ActionInfo { Type = type ?? "", Payload = ToElement(payload) });

    /// <summary>发请求（悔棋 / 求和 / 再来一局 / 认输）。</summary>
    public Task SendRequestAsync(string type, object? payload = null)
        => SendToPeerAsync(BoardProtocol.Kinds.Request, new RequestInfo { Type = type ?? "", Payload = ToElement(payload) });

    /// <summary>回应对方的请求。</summary>
    public Task SendResponseAsync(string type, bool accept, object? payload = null)
        => SendToPeerAsync(BoardProtocol.Kinds.Response,
            new ResponseInfo { Type = type ?? "", Accept = accept, Payload = ToElement(payload) });

    private async Task SendToPeerAsync(string kind, object? extra)
    {
        var frame = RoomFrame.New(kind, _localName, extra);
        var client = _client;
        if (client is not null)
        {
            await client.SendAsync(frame).ConfigureAwait(false);
            return;
        }

        var host = _host;
        RoomLink? peer;
        lock (_gate) peer = _peer;
        if (host is null || peer is not { IsOpen: true }) return;
        await host.SendAsync(peer, frame).ConfigureAwait(false);
    }

    private static JsonElement? ToElement(object? payload)
        => payload switch
        {
            null => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(payload, RoomProtocol.JsonOptions),
        };

    // ---------------------------------------------------------------- 主机：收连接 / 收帧

    private void OnHostClientConnected(RoomLink link)
    {
        if (Volatile.Read(ref _stopping) != 0)
        {
            link.Close("房间已关闭");
            return;
        }
        // 握手要做的事在 OnHostFrameReceived 里（等 hello）；
        // 这里只做快速拒绝：房间里已经有人、且那个连接还活着（最近还有心跳）就别让人占着连接空等。
        // 旧连接长时间没数据 = 大概率已经掉线，放它过去到 hello 阶段再顶替（见 HandleHelloAsync）。
        RoomLink? peer;
        lock (_gate) peer = _peer;
        if (peer is { IsOpen: true } && peer.IdleMs <= PeerStaleMs)
            RejectLink(link, "房间已满：房间里已经有另一位玩家了", "房间已满");
    }

    private void OnHostFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        if (frame.Kind == BoardProtocol.Kinds.Hello)
        {
            _ = HandleHelloAsync(link, frame);
            return;
        }

        RoomLink? peer;
        lock (_gate) peer = _peer;
        if (!ReferenceEquals(link, peer)) return;          // 未握手连接发来的其它包：忽略

        switch (frame.Kind)
        {
            case BoardProtocol.Kinds.Chat:
                if (frame.ExtraAs<ChatInfo>() is { } chat)
                {
                    var name = string.IsNullOrWhiteSpace(chat.Name) ? PeerName : chat.Name;
                    Safe(() => ChatReceived?.Invoke(name, chat.Text ?? ""));
                }
                break;

            case BoardProtocol.Kinds.Notice:
                if (frame.ExtraAs<NoticeInfo>() is { } notice)
                    Safe(() => NoticeReceived?.Invoke(notice.Text ?? "", notice.Level ?? "info"));
                break;

            case BoardProtocol.Kinds.Action:
                if (frame.ExtraAs<ActionInfo>() is { } action)
                    Safe(() => ActionReceived?.Invoke(action.Type ?? "", action.Payload));
                break;

            case BoardProtocol.Kinds.Request:
                if (frame.ExtraAs<RequestInfo>() is { } request)
                    Safe(() => RequestReceived?.Invoke(request.Type ?? "", request.Payload));
                break;

            case BoardProtocol.Kinds.Response:
                if (frame.ExtraAs<ResponseInfo>() is { } response)
                    Safe(() => ResponseReceived?.Invoke(response.Type ?? "", response.Accept, response.Payload));
                break;

            case BoardProtocol.Kinds.Bye:
                if (frame.ExtraAs<ByeInfo>() is { } bye && !string.IsNullOrWhiteSpace(bye.Reason))
                    _byeReason = bye.Reason!;
                link.Close(null);
                break;

            // 客户端发快照没意义（主机才是权威），忽略
            default:
                break;
        }
    }

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
                RejectLink(link, $"协议版本不一致（对方 v{hello.ProtocolVersion}，本机 v{BoardProtocol.Version}），请把两边都更新到同一版本",
                    "协议版本不一致");
                return;
            }
            if (!string.Equals(hello.GameId, _gameId, StringComparison.OrdinalIgnoreCase))
            {
                RejectLink(link, $"游戏类型不一致（对方要玩「{hello.GameId}」，本房间是「{_gameId}」）", "游戏类型不一致");
                return;
            }

            // 旧对端连心跳都没了（拔网线 / 断电 / 被强制休眠）→ 让新连接顶替它，
            // 否则真实的掉线重连会被当成「房间已满」挡在门外（主机要等 90 秒空闲超时才放人）。
            RoomLink? stalePeer = null;
            lock (_gate)
            {
                if (_peer is { IsOpen: true } existing && existing.IdleMs > PeerStaleMs) stalePeer = existing;
            }
            if (stalePeer is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_peer, stalePeer))
                    {
                        _peer = null;
                        _peerName = "";
                        _byeReason = "";
                    }
                }
                try { stalePeer.Close("对端长时间无响应，连接已被新的重连顶替"); } catch { /* 已经断了 */ }
            }

            // 同一时刻只允许一个握手完成（两个客户端几乎同时发 hello 时，后到的按「房间已满」拒绝）
            await _helloGate.WaitAsync().ConfigureAwait(false);
            try
            {
                bool full;
                lock (_gate) full = _peer is { IsOpen: true };
                if (full)
                {
                    RejectLink(link, "房间已满：房间里已经有另一位玩家了", "房间已满");
                    return;
                }

                var name = string.IsNullOrWhiteSpace(hello.PlayerName) ? "对手" : hello.PlayerName.Trim();
                var welcome = _welcomeFactory?.Invoke() ?? new BoardWelcome("", default);
                var welcomeInfo = new WelcomeInfo
                {
                    Magic = BoardProtocol.Magic,
                    GameId = _gameId,
                    HostName = _localName,
                    YourSide = welcome.ClientSide ?? "",
                    ProtocolVersion = BoardProtocol.Version,
                    Snapshot = welcome.Snapshot.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? null : welcome.Snapshot,
                };

                // 先发回执再登记成员：发不出去就当这个客户端没加入成功
                try
                {
                    await link.SendAsync(RoomFrame.New(BoardProtocol.Kinds.Welcome, _localName, welcomeInfo))
                        .ConfigureAwait(false);
                }
                catch
                {
                    link.Close("握手回执发送失败");
                    return;
                }

                lock (_gate)
                {
                    _peer = link;
                    _peerName = name;
                    _byeReason = "";
                }
                RaiseStateChanged();
                Safe(() => PeerJoined?.Invoke(name));
            }
            finally
            {
                _helloGate.Release();
            }
        }
        catch (Exception ex)
        {
            link.Close($"握手失败：{RoomLink.Describe(ex)}");
        }
    }

    private void OnHostClientClosed(RoomLink link, string? error)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        bool wasPeer;
        lock (_gate)
        {
            wasPeer = ReferenceEquals(_peer, link);
            if (wasPeer)
            {
                _peer = null;
                _peerName = "";
            }
        }
        if (!wasPeer) return;

        var reason = _byeReason.Length > 0
            ? _byeReason
            : error is { Length: > 0 } ? error : "对方断开了连接";
        var kind = _byeReason.Length > 0
            ? BoardDisconnectKind.PeerBye
            : error is { Length: > 0 } ? BoardDisconnectKind.NetworkError : BoardDisconnectKind.PeerClosed;
        _byeReason = "";
        lock (_gate)
        {
            _lastDisconnectKind = kind;
            _lastDisconnectReason = reason;
        }
        RaiseStateChanged();
        Safe(() => PeerLeft?.Invoke(reason));
    }

    /// <summary>拒绝一个连接：先发一条提示与 bye，再关掉（让对方界面能显示原因）。</summary>
    private void RejectLink(RoomLink link, string noticeText, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await link.SendAsync(RoomFrame.New(BoardProtocol.Kinds.Notice, _localName,
                    new NoticeInfo { Text = noticeText, Level = "warn" })).ConfigureAwait(false);
                await link.SendAsync(RoomFrame.New(BoardProtocol.Kinds.Bye, _localName,
                    new ByeInfo { Reason = reason })).ConfigureAwait(false);
            }
            catch { /* 对方已经断了 */ }
            link.Close(reason);
        });
    }

    // ---------------------------------------------------------------- 客户端：收帧

    private void OnClientFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        switch (frame.Kind)
        {
            case BoardProtocol.Kinds.Welcome:
            {
                var welcome = frame.ExtraAs<WelcomeInfo>();
                if (welcome is null) { link.Close("握手回执解析失败"); return; }
                if (!string.Equals(welcome.Magic, BoardProtocol.Magic, StringComparison.Ordinal))
                {
                    link.Close("对方不是本程序的棋牌房间（协议标识不匹配）");
                    return;
                }
                if (welcome.ProtocolVersion != BoardProtocol.Version)
                {
                    link.Close($"协议版本不一致（主机 v{welcome.ProtocolVersion}，本机 v{BoardProtocol.Version}）");
                    return;
                }
                if (!string.Equals(welcome.GameId, _gameId, StringComparison.OrdinalIgnoreCase))
                {
                    link.Close($"游戏类型不一致（主机开的是「{welcome.GameId}」，本端是「{_gameId}」）");
                    return;
                }
                _welcomeTcs?.TrySetResult(welcome);
                return;
            }

            case BoardProtocol.Kinds.Chat:
                if (Volatile.Read(ref _handshakeDone) == 0) return;
                if (frame.ExtraAs<ChatInfo>() is { } chat)
                {
                    var name = string.IsNullOrWhiteSpace(chat.Name) ? "房主" : chat.Name;
                    Safe(() => ChatReceived?.Invoke(name, chat.Text ?? ""));
                }
                return;

            case BoardProtocol.Kinds.Notice:
                if (frame.ExtraAs<NoticeInfo>() is { } notice)
                    Safe(() => NoticeReceived?.Invoke(notice.Text ?? "", notice.Level ?? "info"));
                return;

            case BoardProtocol.Kinds.Snapshot:
                if (Volatile.Read(ref _handshakeDone) == 0) return;
                if (frame.ExtraAs<SnapshotInfo>() is { } snapshot)
                {
                    if (!string.IsNullOrWhiteSpace(snapshot.Notice))
                        Safe(() => NoticeReceived?.Invoke(snapshot.Notice!, "info"));
                    if (snapshot.Snapshot is { } wrapped)
                        Safe(() => SnapshotReceived?.Invoke(wrapped));
                }
                return;

            case BoardProtocol.Kinds.Request:
                if (frame.ExtraAs<RequestInfo>() is { } request)
                    Safe(() => RequestReceived?.Invoke(request.Type ?? "", request.Payload));
                return;

            case BoardProtocol.Kinds.Response:
                if (frame.ExtraAs<ResponseInfo>() is { } response)
                    Safe(() => ResponseReceived?.Invoke(response.Type ?? "", response.Accept, response.Payload));
                return;

            case BoardProtocol.Kinds.Bye:
                if (frame.ExtraAs<ByeInfo>() is { } bye && !string.IsNullOrWhiteSpace(bye.Reason))
                    _byeReason = bye.Reason!;
                link.Close(null);
                return;

            default:
                return;
        }
    }

    private void OnClientClosed(RoomLink link, string? error)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        // 握手还没完成就断了：叫醒 ConnectAsync 里的等待
        var tcs = _welcomeTcs;
        if (Volatile.Read(ref _handshakeDone) == 0)
        {
            if (tcs is { Task.IsCompleted: false })
                tcs.TrySetException(new IOException(error is { Length: > 0 } ? error : "对方关闭了连接"));
            return;
        }

        Volatile.Write(ref _handshakeDone, 0);
        var hadBye = _byeReason.Length > 0;
        var reason = hadBye
            ? _byeReason
            : error is { Length: > 0 } ? error : "房主关闭了房间";
        var kind = hadBye
            ? BoardDisconnectKind.PeerBye
            : error is { Length: > 0 } ? BoardDisconnectKind.NetworkError : BoardDisconnectKind.PeerClosed;
        _byeReason = "";
        lock (_gate)
        {
            _peerName = "";
            _lastDisconnectKind = kind;
            _lastDisconnectReason = reason;
        }
        RaiseStateChanged();
        Safe(() => PeerLeft?.Invoke(reason));
    }

    // ---------------------------------------------------------------- 小工具

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
