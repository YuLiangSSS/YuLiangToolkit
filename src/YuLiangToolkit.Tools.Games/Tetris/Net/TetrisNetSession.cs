using System.Net.Sockets;
using System.Text.Json;
using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.Games.Tetris.Net;

/// <summary>
/// 一次俄罗斯方块联机会话（1 对 1）：房主端建 <see cref="RoomHost"/> 等人连，
/// 客户端建 <see cref="RoomClient"/> 连过去。<b>只做传输与房间成员管理，不含游戏规则</b>。
///
/// <para><b>为什么不用棋牌那套 <c>BoardRoomSession</c></b>：那一套是给棋盘游戏写的，
/// 深度绑定「两个上场席位 + 观战 + 自动轮换 + 胜负方」，而俄罗斯方块联机是
/// 「双方各一块棋盘、互相塞垃圾行」，用不上席位与轮换；这里只要成员表 + 动作转发 + 状态广播。</para>
///
/// <para><b>线程</b>：所有事件都在后台线程触发，界面侧请用 <c>Ui(...)</c> 切回 UI 线程。
/// 事件回调里的异常会被吞掉（不让一个界面 bug 把连接弄断）。</para>
/// </summary>
public sealed class TetrisNetSession : IAsyncDisposable
{
    private enum Role
    {
        None = 0,
        Host = 1,
        Guest = 2,
    }

    /// <summary>客户端发完 bye 之后等这么久再关连接（毫秒），让对端先把在途帧读干净。</summary>
    private const int ClientByeDrainMs = 80;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);

    private RoomHost? _host;
    private RoomClient? _client;
    private Role _role;

    /// <summary>房主：唯一那条客户端连接（1 对 1，所以就是一条）。</summary>
    private RoomLink? _guestLink;

    /// <summary>房主：客户端成员信息（没人时为 null）。</summary>
    private TetrisNetPlayer? _guest;

    private string _localName;
    private string _localMemberId = "";
    private string _hostName = "";
    private string _helloFailReason = "";
    private TaskCompletionSource<TetrisWelcomeInfo>? _welcomeTcs;
    private IReadOnlyList<RoomProtocol.ShareAddress> _shareAddresses = Array.Empty<RoomProtocol.ShareAddress>();
    private int _handshakeDone;
    private int _started;
    private int _stopping;

    /// <summary>创建一次会话（还没开始监听 / 连接）。</summary>
    /// <param name="localName">本端玩家名。</param>
    public TetrisNetSession(string localName)
        => _localName = string.IsNullOrWhiteSpace(localName) ? "玩家" : localName.Trim();

    // ---------------------------------------------------------------- 状态

    /// <summary>本端是不是房主。</summary>
    public bool IsHost { get { lock (_gate) return _role == Role.Host; } }

    /// <summary>本端是不是客户端（且已经握手完成）。</summary>
    public bool IsGuestConnected => _client is { IsConnected: true } && Volatile.Read(ref _handshakeDone) == 1;

    /// <summary>房主：是不是有人连进来了。</summary>
    public bool HasGuest
    {
        get { lock (_gate) return _guestLink is { IsOpen: true }; }
    }

    /// <summary>房主：连进来的那位玩家（没人时为 null）。</summary>
    public TetrisNetPlayer? Guest
    {
        get { lock (_gate) return _guest?.Clone(); }
    }

    /// <summary>本端玩家名。</summary>
    public string LocalName => _localName;

    /// <summary>房主名（客户端握手后拿到）。</summary>
    public string HostName { get { lock (_gate) return _hostName; } }

    /// <summary>本端成员 id：房主固定 <c>host</c>；客户端由房主分配；未进房为空。</summary>
    public string LocalMemberId => IsHost ? TetrisNetProtocol.HostMemberId : _localMemberId;

    /// <summary>房主监听地址文本（没开房时为空）。</summary>
    public string ListenText => _host?.EndPoint is { } ep ? RoomProtocol.FormatEndpoint(ep.Address, ep.Port) : "";

    /// <summary>房主这次监听的端口（没开房时是 0）。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>本机「可以告诉对方」的地址（房主开房后才有）。</summary>
    public IReadOnlyList<RoomProtocol.ShareAddress> ShareAddresses
    {
        get { lock (_gate) return _shareAddresses; }
    }

    /// <summary>心跳往返延迟（毫秒）。</summary>
    public int LatencyMs
    {
        get
        {
            if (_client is not null) return _client.LatencyMs;
            lock (_gate) return _guestLink?.LatencyMs ?? 0;
        }
    }

    /// <summary>最近一次断开的原因（给界面显示）。</summary>
    public string LastDisconnectReason { get; private set; } = "";

    // ---------------------------------------------------------------- 事件（都在后台线程触发）

    /// <summary>状态变了（开房成功 / 有人进来 / 有人离开）。</summary>
    public event Action? StateChanged;

    /// <summary>房主：客户端握手完成（已能收状态、能上报动作）。</summary>
    public event Action<TetrisNetPlayer>? GuestJoined;

    /// <summary>房主：客户端离开 / 掉线（<paramref name="reason"/> 是中文原因）。</summary>
    public event Action<TetrisNetPlayer, string>? GuestLeft;

    /// <summary>房主：收到客户端的按键动作。（<c>action</c> 见 <see cref="TetrisNetProtocol.Actions"/>）</summary>
    public event Action<string>? ActionReceived;

    /// <summary>客户端：收到房主广播的完整状态。</summary>
    public event Action<TetrisNetState>? StateReceived;

    /// <summary>双向：系统提示文字。</summary>
    public event Action<string>? NoticeReceived;

    /// <summary>客户端：与房主的连接断了（原因）。</summary>
    public event Action<string>? Disconnected;

    // ---------------------------------------------------------------- 房主：开房

    /// <summary>
    /// 开一个房间等客户端连进来。<paramref name="port"/> 传 0 让系统挑。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public async Task<string> StartHostAsync(int port, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";

        var host = new RoomHost();
        host.ClientConnected += OnHostClientConnected;
        host.FrameReceived += OnHostFrameReceived;
        host.ClientClosed += OnHostClientClosed;

        var plan = RoomProtocol.ChooseSimpleListenPlan(port, Socket.OSSupportsIPv6, RoomProtocol.IsPortAvailable);
        var error = await host.StartAsync(plan.Address, plan.Port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            try { await host.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
            Interlocked.Exchange(ref _started, 0);
            return error;
        }

        lock (_gate)
        {
            _host = host;
            _role = Role.Host;
            _localMemberId = TetrisNetProtocol.HostMemberId;
            _shareAddresses = RoomProtocol.ListShareAddresses();
        }
        RaiseStateChanged();
        return "";
    }

    private void OnHostClientConnected(RoomLink link)
    {
        // 容量 / 身份在 hello 里判断（1 对 1，第二个人会被拒）。
        _ = link;
    }

    private void OnHostFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;

        if (frame.Kind == TetrisNetProtocol.Kinds.Hello)
        {
            _ = HandleHelloAsync(link, frame);
            return;
        }

        // 还没握手的连接发别的包：忽略
        lock (_gate)
        {
            if (!ReferenceEquals(_guestLink, link)) return;
        }

        switch (frame.Kind)
        {
            case TetrisNetProtocol.Kinds.Action:
                if (frame.ExtraAs<TetrisActionInfo>() is { } action && TetrisNetProtocol.Actions.IsKnown(action.Action))
                    Safe(() => ActionReceived?.Invoke(action.Action.Trim()));
                return;

            case TetrisNetProtocol.Kinds.Bye:
                link.Close(null);
                return;

            default:
                return;
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

            var hello = frame.ExtraAs<TetrisHelloInfo>();
            if (hello is null)
            {
                RejectLink(link, "握手包解析失败，请确认双方都是最新版本", "握手失败");
                return;
            }
            if (!string.Equals(hello.Magic, TetrisNetProtocol.Magic, StringComparison.Ordinal))
            {
                RejectLink(link, "对方不是本程序的俄罗斯方块联机房间（协议标识不匹配）", "协议不匹配");
                return;
            }
            if (hello.ProtocolVersion != TetrisNetProtocol.Version)
            {
                RejectLink(link,
                    $"协议版本不一致（对方 v{hello.ProtocolVersion}，本机 v{TetrisNetProtocol.Version}），请把两边都更新到同一版本",
                    "协议版本不一致");
                return;
            }

            await _helloGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var name = string.IsNullOrWhiteSpace(hello.PlayerName) ? "玩家" : hello.PlayerName.Trim();

                TetrisNetPlayer guest;
                lock (_gate)
                {
                    if (_guestLink is { IsOpen: true })
                    {
                        RejectLink(link, "房间已满（1 对 1，最多 2 人）", "房间已满");
                        return;
                    }

                    guest = new TetrisNetPlayer
                    {
                        MemberId = "m1",
                        Name = name,
                        IsHost = false,
                    };
                    _guest = guest;
                    _guestLink = link;
                }

                var welcome = new TetrisWelcomeInfo
                {
                    Magic = TetrisNetProtocol.Magic,
                    ProtocolVersion = TetrisNetProtocol.Version,
                    HostName = _localName,
                    YourMemberId = guest.MemberId,
                    Mode = TetrisNetProtocol.ModeToString(TetrisNetProtocol.ParseMode(hello.Mode)),
                };

                var sent = await SendToLinkSafeAsync(link, TetrisNetProtocol.Kinds.Welcome,
                    TetrisNetProtocol.HostMemberId, welcome).ConfigureAwait(false);
                if (!sent)
                {
                    lock (_gate)
                    {
                        _guest = null;
                        _guestLink = null;
                    }
                    link.Close("握手回执发送失败");
                    return;
                }

                Volatile.Write(ref _handshakeDone, 1);
                var snapshot = guest.Clone();
                Safe(() => GuestJoined?.Invoke(snapshot));
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

    private void OnHostClientClosed(RoomLink link, string? error)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        TetrisNetPlayer? guest;
        lock (_gate)
        {
            if (!ReferenceEquals(_guestLink, link)) return;
            guest = _guest;
            _guest = null;
            _guestLink = null;
        }
        Volatile.Write(ref _handshakeDone, 0);

        var reason = error is { Length: > 0 } ? error : "对方离开了房间";
        LastDisconnectReason = reason;
        RaiseStateChanged();
        if (guest is not null) Safe(() => GuestLeft?.Invoke(guest, reason));
    }

    /// <summary>拒绝连接：先发提示与 bye，再关（让对方界面能显示原因）。</summary>
    private void RejectLink(RoomLink link, string noticeText, string reason)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await link.SendAsync(RoomFrame.New(TetrisNetProtocol.Kinds.Notice, TetrisNetProtocol.HostMemberId,
                    new TetrisNoticeInfo { Text = noticeText, Level = "warn" })).ConfigureAwait(false);
                await link.SendAsync(RoomFrame.New(TetrisNetProtocol.Kinds.Bye, TetrisNetProtocol.HostMemberId,
                    new TetrisByeInfo { Reason = reason })).ConfigureAwait(false);
            }
            catch { /* 对方已经断了 */ }
            link.Close(reason);
        });
    }

    // ---------------------------------------------------------------- 客户端：连接

    /// <summary>
    /// 连到房主。<paramref name="addressText"/> 可以带端口（<c>192.168.1.5:5310</c>），
    /// <paramref name="port"/> 填 0 时用它带的那个。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因。</returns>
    public async Task<string> ConnectAsync(string addressText, string mode, int port = 0,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";

        var client = new RoomClient();
        client.FrameReceived += OnClientFrameReceived;
        client.Closed += OnClientClosed;

        var error = await client.ConnectAsync(addressText, port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            Interlocked.Exchange(ref _started, 0);
            try { await client.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
            return error;
        }

        lock (_gate)
        {
            _client = client;
            _role = Role.Guest;
        }

        var welcomeTcs = new TaskCompletionSource<TetrisWelcomeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _welcomeTcs = welcomeTcs;
        _helloFailReason = "";

        await client.SendAsync(RoomFrame.New(TetrisNetProtocol.Kinds.Hello, _localName, new TetrisHelloInfo
        {
            Magic = TetrisNetProtocol.Magic,
            ProtocolVersion = TetrisNetProtocol.Version,
            PlayerName = _localName,
            Mode = mode,
        }), ct).ConfigureAwait(false);

        var completed = await Task.WhenAny(welcomeTcs.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
        if (completed != welcomeTcs.Task)
        {
            await StopAsync("握手超时").ConfigureAwait(false);
            return "连上了但房主没有回应握手（对方可能不是俄罗斯方块房间，或者被防火墙拦了包）";
        }

        TetrisWelcomeInfo welcome;
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

        if (welcome.State is { } state && state.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            && TetrisNetProtocol.ReadState(state) is { } parsed)
        {
            Safe(() => StateReceived?.Invoke(parsed));
        }
        return "";
    }

    private void OnClientFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;
        var handshaken = Volatile.Read(ref _handshakeDone) == 1;

        switch (frame.Kind)
        {
            case TetrisNetProtocol.Kinds.Welcome:
            {
                var welcome = frame.ExtraAs<TetrisWelcomeInfo>();
                if (welcome is null) { FailHandshake(link, "握手回执解析失败"); return; }
                if (!string.Equals(welcome.Magic, TetrisNetProtocol.Magic, StringComparison.Ordinal))
                {
                    FailHandshake(link, "对方不是本程序的俄罗斯方块联机房间（协议标识不匹配）");
                    return;
                }
                if (welcome.ProtocolVersion != TetrisNetProtocol.Version)
                {
                    FailHandshake(link,
                        $"协议版本不一致（房主 v{welcome.ProtocolVersion}，本机 v{TetrisNetProtocol.Version}），请把两边都更新到同一版本");
                    return;
                }
                _welcomeTcs?.TrySetResult(welcome);
                return;
            }

            case TetrisNetProtocol.Kinds.Notice:
            {
                var notice = frame.ExtraAs<TetrisNoticeInfo>();
                if (notice is null) return;
                if (!handshaken)
                {
                    _helloFailReason = notice.Text ?? "";
                    return;
                }
                Safe(() => NoticeReceived?.Invoke(notice.Text ?? ""));
                return;
            }

            case TetrisNetProtocol.Kinds.Bye:
            {
                var bye = frame.ExtraAs<TetrisByeInfo>();
                var reason = bye?.Reason is { Length: > 0 } r ? r : "房主关闭了房间";
                LastDisconnectReason = reason;
                if (!handshaken)
                {
                    _welcomeTcs?.TrySetException(new IOException(
                        _helloFailReason.Length > 0 ? _helloFailReason : reason));
                }
                link.Close(null);
                return;
            }

            case TetrisNetProtocol.Kinds.State:
                if (!handshaken) return;
                if (frame.ExtraAs<TetrisStateInfo>() is { } info && info.State is { } state
                    && TetrisNetProtocol.ReadState(state) is { } parsed)
                {
                    Safe(() => StateReceived?.Invoke(parsed));
                }
                return;

            default:
                return;
        }
    }

    private void FailHandshake(RoomLink link, string reason)
    {
        _helloFailReason = reason;
        try { _welcomeTcs?.TrySetException(new IOException(reason)); } catch { /* 忽略 */ }
        link.Close(reason);
    }

    private void OnClientClosed(RoomLink link, string? error)
    {
        if (Volatile.Read(ref _stopping) != 0) return;

        var handshaken = Volatile.Read(ref _handshakeDone) == 1;
        var reason = LastDisconnectReason is { Length: > 0 } known
            ? known
            : error is { Length: > 0 } ? error : "房主关闭了房间";

        if (!handshaken)
        {
            _welcomeTcs?.TrySetException(new IOException(
                _helloFailReason.Length > 0 ? _helloFailReason : reason));
            return;
        }

        Volatile.Write(ref _handshakeDone, 0);
        LastDisconnectReason = reason;
        RaiseStateChanged();
        Safe(() => Disconnected?.Invoke(reason));
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>房主：把完整状态广播给客户端（没人时静默忽略）。</summary>
    public async Task BroadcastStateAsync(TetrisNetState state)
    {
        RoomLink? link;
        lock (_gate) link = _guestLink;
        if (link is null || !link.IsOpen) return;

        await SendToLinkSafeAsync(link, TetrisNetProtocol.Kinds.State, TetrisNetProtocol.HostMemberId,
            new TetrisStateInfo { State = TetrisNetProtocol.WrapState(state) }).ConfigureAwait(false);
    }

    /// <summary>房主：给客户端发一条系统提示。</summary>
    public async Task SendNoticeAsync(string text, string level = "info")
    {
        RoomLink? link;
        lock (_gate) link = _guestLink;
        if (link is null || !link.IsOpen) return;

        await SendToLinkSafeAsync(link, TetrisNetProtocol.Kinds.Notice, TetrisNetProtocol.HostMemberId,
            new TetrisNoticeInfo { Text = text, Level = level }).ConfigureAwait(false);
    }

    /// <summary>客户端：上报一个按键动作。</summary>
    public async Task SendActionAsync(string action)
    {
        var client = _client;
        if (client is null || !TetrisNetProtocol.Actions.IsKnown(action)) return;
        try
        {
            await client.SendAsync(RoomFrame.New(TetrisNetProtocol.Kinds.Action, _localMemberId,
                new TetrisActionInfo { Action = action })).ConfigureAwait(false);
        }
        catch { /* 连接可能已经断了 */ }
    }

    private async Task<bool> SendToLinkSafeAsync(RoomLink link, string kind, string from, object? extra)
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

    // ---------------------------------------------------------------- 收尾

    /// <summary>停止监听 / 断开连接（幂等）。会先发 bye 让对方知道不是掉线。</summary>
    public async Task StopAsync(string reason = "本端离开了房间")
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        var stopReason = string.IsNullOrWhiteSpace(reason) ? "本端离开了房间" : reason;
        LastDisconnectReason = stopReason;

        RoomHost? host;
        RoomClient? client;
        RoomLink? guestLink;
        lock (_gate)
        {
            host = _host;
            client = _client;
            guestLink = _guestLink;
            _host = null;
            _client = null;
            _guestLink = null;
            _guest = null;
            _role = Role.None;
            _shareAddresses = Array.Empty<RoomProtocol.ShareAddress>();
        }
        Volatile.Write(ref _handshakeDone, 0);

        var bye = RoomFrame.New(TetrisNetProtocol.Kinds.Bye, TetrisNetProtocol.HostMemberId,
            new TetrisByeInfo { Reason = stopReason });

        if (guestLink is { IsOpen: true })
        {
            try { await guestLink.SendAsync(bye).ConfigureAwait(false); } catch { /* 已经断了 */ }
        }
        if (client is not null)
        {
            try { await client.SendAsync(bye).ConfigureAwait(false); } catch { /* 已经断了 */ }
            // 让读循环把在途帧读干净，别让对端把「主动离开」看成网络重置
            try { await Task.Delay(ClientByeDrainMs).ConfigureAwait(false); } catch { /* 忽略 */ }
        }

        try { if (host is not null) await host.StopAsync(stopReason).ConfigureAwait(false); } catch { /* 忽略 */ }
        try { if (client is not null) await client.CloseAsync("").ConfigureAwait(false); } catch { /* 忽略 */ }

        RaiseStateChanged();

        var tcs = _welcomeTcs;
        _welcomeTcs = null;
        if (tcs is not null && !tcs.Task.IsCompleted) tcs.TrySetException(new IOException(stopReason));
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

/// <summary><see cref="TetrisNetPlayer"/> 的复制扩展（事件里传副本，避免拿到会被后续改动的同一个对象）。</summary>
internal static class TetrisNetPlayerExtensions
{
    /// <summary>浅复制（<see cref="TetrisNetPlayer.Board"/> 直接共用引用：快照本身就是不可变的）。</summary>
    public static TetrisNetPlayer Clone(this TetrisNetPlayer player) => new()
    {
        MemberId = player.MemberId,
        Name = player.Name,
        IsHost = player.IsHost,
        Score = player.Score,
        Lines = player.Lines,
        Level = player.Level,
        IsDead = player.IsDead,
        PendingGarbage = player.PendingGarbage,
        GarbageSent = player.GarbageSent,
        GarbageTaken = player.GarbageTaken,
        Board = player.Board,
    };
}
