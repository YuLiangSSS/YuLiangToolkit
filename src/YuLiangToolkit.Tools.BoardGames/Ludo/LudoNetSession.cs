using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.BoardGames.Ludo;

/// <summary>飞行棋联机会话（房主权威）。</summary>
public sealed class LudoNetSession : IAsyncDisposable
{
    /// <summary>房主成员 id。</summary>
    public const string HostMemberId = "host";

    private static class Kinds
    {
        public const string State = "ludo.state";
        public const string Roll = "ludo.roll";
        public const string Move = "ludo.move";
        public const string Hello = "ludo.hello";
    }

    private readonly string _localName;
    private RoomHost? _host;
    private RoomClient? _client;

    /// <summary>创建。</summary>
    public LudoNetSession(string localName) => _localName = localName;

    /// <summary>是不是房主。</summary>
    public bool IsHost => _host is not null;

    /// <summary>监听端口。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>本端 id。</summary>
    public string LocalMemberId { get; private set; } = "";

    /// <summary>收到房主状态。</summary>
    public event Action<LudoNetState>? StateReceived;

    /// <summary>房主：收到客户端的掷骰请求。</summary>
    public event Action? RollReceived;

    /// <summary>房主：收到客户端走棋请求（-1 = 让房主挑）。</summary>
    public event Action<int>? MoveReceived;

    /// <summary>开房。</summary>
    public async Task<string> StartHostAsync(int port, CancellationToken ct = default)
    {
        var host = new RoomHost();
        host.FrameReceived += OnHostFrame;

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

        var error = await client.ConnectAsync(address, 0, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            try { await client.DisposeAsync(); } catch { /* 忽略 */ }
            return error;
        }

        _client = client;
        LocalMemberId = _localName;
        await client.SendAsync(RoomFrame.New(Kinds.Hello, _localName, new LudoHello { Name = _localName }), ct)
            .ConfigureAwait(false);
        return "";
    }

    /// <summary>房主广播状态。</summary>
    public async Task BroadcastAsync(LudoNetState state)
    {
        if (_host is null) return;
        try { await _host.BroadcastAsync(RoomFrame.New(Kinds.State, HostMemberId, state)).ConfigureAwait(false); }
        catch { /* 忽略 */ }
    }

    /// <summary>客户端请求掷骰。</summary>
    public async Task SendRollAsync()
    {
        if (_client is null) return;
        try { await _client.SendAsync(RoomFrame.New(Kinds.Roll, LocalMemberId, new LudoHello { Name = LocalMemberId })).ConfigureAwait(false); }
        catch { /* 忽略 */ }
    }

    /// <summary>客户端请求走棋。</summary>
    public async Task SendMoveAsync(int pieceIndex)
    {
        if (_client is null) return;
        try
        {
            await _client.SendAsync(RoomFrame.New(Kinds.Move, LocalMemberId,
                new LudoMoveRequest { PieceIndex = pieceIndex })).ConfigureAwait(false);
        }
        catch { /* 忽略 */ }
    }

    private void OnHostFrame(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        switch (frame.Kind)
        {
            case Kinds.Roll:
                RollReceived?.Invoke();
                return;
            case Kinds.Move:
                if (frame.ExtraAs<LudoMoveRequest>() is { } request) MoveReceived?.Invoke(request.PieceIndex);
                return;
            default:
                return;
        }
    }

    private void OnClientFrame(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (frame.Kind != Kinds.State) return;
        if (frame.ExtraAs<LudoNetState>() is { } state) StateReceived?.Invoke(state);
    }

    /// <summary>停止。</summary>
    public async Task StopAsync()
    {
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
public sealed class LudoHello
{
    public string Name { get; set; } = "";
}

/// <summary>走棋请求。</summary>
public sealed class LudoMoveRequest
{
    public int PieceIndex { get; set; }
}

/// <summary>房主广播的完整状态。</summary>
public sealed class LudoNetState
{
    public string[] Seats { get; set; } = Array.Empty<string>();
    public LudoRulesState? Rules { get; set; }
    public int Dice { get; set; }
    public string? LastText { get; set; }
    public string? ResultText { get; set; }
    public List<LudoPieceWire>? Pieces { get; set; }
}

/// <summary>一枚棋子的网络表示（与引擎里的 <c>LudoPieceState</c> 枚举区分开）。</summary>
public sealed class LudoPieceWire
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Color { get; set; } = "red";
    public bool Finished { get; set; }
}
