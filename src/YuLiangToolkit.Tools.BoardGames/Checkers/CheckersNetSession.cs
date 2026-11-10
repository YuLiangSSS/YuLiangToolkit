using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Checkers;

/// <summary>
/// 跳棋联机会话（<b>房主权威</b>）：房主跑规则，广播座位 / 棋子 / 回合；客户端只上报「我要坐哪个角」「我要走这一步」。
///
/// <para>复用 <see cref="RoomHost"/> / <see cref="RoomClient"/> / <see cref="RoomFrame"/>，
/// 与飞行棋 / 五子棋同一套传输层。</para>
/// </summary>
public sealed class CheckersNetSession : IAsyncDisposable
{
    /// <summary>房主成员 id。</summary>
    public const string HostMemberId = "host";

    private static class Kinds
    {
        public const string State = "checkers.state";
        public const string Sit = "checkers.sit";
        public const string Move = "checkers.move";
        public const string Hello = "checkers.hello";
    }

    private readonly string _localName;
    private readonly Dictionary<RoomLink, string> _names = new();
    private readonly object _gate = new();
    private RoomHost? _host;
    private RoomClient? _client;
    private int _stopping;

    /// <summary>创建会话。</summary>
    /// <param name="localName">本端名字。</param>
    public CheckersNetSession(string localName) => _localName = localName;

    /// <summary>是不是房主。</summary>
    public bool IsHost => _host is not null;

    /// <summary>监听端口。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>本端成员 id。</summary>
    public string LocalMemberId { get; private set; } = "";

    /// <summary>房间里有几个人（含房主）。</summary>
    public int MemberCount
    {
        get { lock (_gate) return _names.Count + (_host is not null ? 1 : 0); }
    }

    /// <summary>房间成员变化。</summary>
    public event Action? MembersChanged;

    /// <summary>客户端：收到房主状态。</summary>
    public event Action<CheckersNetState>? StateReceived;

    /// <summary>房主：有人请求坐下（成员 id、角键）。</summary>
    public event Action<string, CheckersPoint>? SitRequested;

    /// <summary>房主：有人请求退出座位。</summary>
    public event Action<string>? LeaveSeatRequested;

    /// <summary>房主：有人请求走棋。</summary>
    public event Action<string, int, int, int, int>? MoveRequested;

    // ---------------------------------------------------------------- 开房 / 加入

    /// <summary>开房。</summary>
    public async Task<string> StartHostAsync(int port, CancellationToken ct = default)
    {
        var host = new RoomHost();
        host.ClientConnected += OnClientConnected;
        host.FrameReceived += OnHostFrame;
        host.ClientClosed += OnClientClosed;

        var plan = RoomProtocol.ChooseSimpleListenPlan(port, System.Net.Sockets.Socket.OSSupportsIPv6,
            RoomProtocol.IsPortAvailable);
        var error = await host.StartAsync(plan.Address, plan.Port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            try { await host.DisposeAsync(); } catch { /* 忽略 */ }
            return error;
        }

        _host = host;
        LocalMemberId = HostMemberId;
        return "";
    }

    /// <summary>连到房主。</summary>
    public async Task<string> ConnectAsync(string address, CancellationToken ct = default)
    {
        var client = new RoomClient();
        client.FrameReceived += OnClientFrame;
        client.Closed += OnClientClosed;

        var error = await client.ConnectAsync(address, 0, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            try { await client.DisposeAsync(); } catch { /* 忽略 */ }
            return error;
        }

        _client = client;
        LocalMemberId = _localName;

        await client.SendAsync(RoomFrame.New(Kinds.Hello, _localName,
            new CheckersHello { Name = _localName }), ct).ConfigureAwait(false);
        return "";
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>房主：广播完整状态。</summary>
    public async Task BroadcastAsync(CheckersNetState state)
    {
        if (_host is null) return;
        try
        {
            await _host.BroadcastAsync(RoomFrame.New(Kinds.State, HostMemberId, state)).ConfigureAwait(false);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>客户端：请求坐某个角。</summary>
    public async Task SendSitAsync(CheckersPoint point)
    {
        if (_client is null) return;
        try
        {
            await _client.SendAsync(RoomFrame.New(Kinds.Sit, LocalMemberId,
                new CheckersSitRequest { Point = CheckersBoard.ToKey(point), MemberId = LocalMemberId, Name = _localName }))
                .ConfigureAwait(false);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>客户端：请求退出座位。</summary>
    public async Task SendLeaveSeatAsync()
    {
        if (_client is null) return;
        try
        {
            await _client.SendAsync(RoomFrame.New(Kinds.Sit, LocalMemberId,
                new CheckersSitRequest { Point = "", MemberId = LocalMemberId, Name = _localName }))
                .ConfigureAwait(false);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>客户端：请求走棋。</summary>
    public async Task SendMoveAsync(int fromX, int fromY, int toX, int toY)
    {
        if (_client is null) return;
        try
        {
            await _client.SendAsync(RoomFrame.New(Kinds.Move, LocalMemberId,
                new CheckersMoveRequest { FromX = fromX, FromY = fromY, ToX = toX, ToY = toY }))
                .ConfigureAwait(false);
        }
        catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 收包

    private void OnClientConnected(RoomLink link)
    {
        lock (_gate) _names[link] = "玩家";
        MembersChanged?.Invoke();
    }

    private void OnHostFrame(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;

        switch (frame.Kind)
        {
            case Kinds.Hello:
                if (frame.ExtraAs<CheckersHello>() is { } hello)
                {
                    lock (_gate) _names[link] = string.IsNullOrWhiteSpace(hello.Name) ? "玩家" : hello.Name;
                    MembersChanged?.Invoke();
                }
                return;

            case Kinds.Sit:
                if (frame.ExtraAs<CheckersSitRequest>() is { } sit)
                {
                    var id = MemberIdOf(link);
                    if ((sit.Point ?? "").Trim().Length == 0) LeaveSeatRequested?.Invoke(id);
                    else SitRequested?.Invoke(id, CheckersBoard.ParseKey(sit.Point));
                }
                return;

            case Kinds.Move:
                if (frame.ExtraAs<CheckersMoveRequest>() is { } move)
                {
                    MoveRequested?.Invoke(MemberIdOf(link), move.FromX, move.FromY, move.ToX, move.ToY);
                }
                return;

            default:
                return;
        }
    }

    private void OnClientFrame(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = link;
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;

        if (frame.Kind != Kinds.State) return;
        if (frame.ExtraAs<CheckersNetState>() is { } state) StateReceived?.Invoke(state);
    }

    private void OnClientClosed(RoomLink link, string? error)
    {
        _ = error;
        if (Volatile.Read(ref _stopping) != 0) return;

        lock (_gate) _names.Remove(link);
        MembersChanged?.Invoke();
    }

    /// <summary>某个连接对应的成员 id（用名字当 id）。</summary>
    private string MemberIdOf(RoomLink link)
    {
        lock (_gate) return _names.TryGetValue(link, out var name) ? name : "玩家";
    }

    /// <summary>房间成员名（房主用）。</summary>
    public IReadOnlyList<string> MemberNames
    {
        get { lock (_gate) return _names.Values.ToList(); }
    }

    // ---------------------------------------------------------------- 收尾

    /// <summary>停止。</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        lock (_gate) _names.Clear();

        try { if (_host is not null) await _host.StopAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        try { if (_client is not null) await _client.CloseAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        try { if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
        try { if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
    }
}

/// <summary>握手包。</summary>
public sealed class CheckersHello
{
    public string Name { get; set; } = "";
}

/// <summary>选座请求（点的键为空 = 退出座位）。</summary>
public sealed class CheckersSitRequest
{
    public string Point { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>走棋请求。</summary>
public sealed class CheckersMoveRequest
{
    public int FromX { get; set; }
    public int FromY { get; set; }
    public int ToX { get; set; }
    public int ToY { get; set; }
}

/// <summary>房主广播的完整状态。</summary>
public sealed class CheckersNetState
{
    /// <summary>座位表（每项 <c>n=成员id|名字</c>，空位为空串）。</summary>
    public string[] Seats { get; set; } = Array.Empty<string>();

    /// <summary>人数。</summary>
    public int PlayerCount { get; set; } = 2;

    /// <summary>现在轮到谁（成员 id）。</summary>
    public string CurrentPlayerId { get; set; } = "";

    /// <summary>棋子（每项 <c>x,y,成员id</c>）。</summary>
    public string[] Pieces { get; set; } = Array.Empty<string>();

    /// <summary>是否已开局。</summary>
    public bool Started { get; set; }

    /// <summary>是否结束。</summary>
    public bool IsOver { get; set; }

    /// <summary>结果文字。</summary>
    public string ResultText { get; set; } = "";

    /// <summary>最近一步说明。</summary>
    public string LastText { get; set; } = "";

    /// <summary>规则：允许走一格。</summary>
    public bool AllowSingleStep { get; set; } = true;

    /// <summary>规则：必须先清初始区域。</summary>
    public bool MustClearHomeFirst { get; set; } = true;
}
