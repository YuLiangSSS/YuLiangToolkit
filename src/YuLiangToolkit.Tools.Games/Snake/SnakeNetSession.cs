using YuLiangToolkit.Core.Net;

namespace YuLiangToolkit.Tools.Games.Snake;

/// <summary>
/// 贪吃蛇联机会话（<b>房主权威</b>）：房主跑引擎、广播状态；客户端只上报方向。
///
/// <para>复用 <see cref="RoomHost"/> / <see cref="RoomClient"/> / <see cref="RoomFrame"/>，
/// 跟象棋 / 围棋的房间是同一套传输层，但贪吃蛇的玩法更实时（每帧广播一帧状态），
/// 所以这里只做<b>轻量协议</b>：设置同步、方向输入、状态帧。</para>
/// </summary>
public sealed class SnakeNetSession : IAsyncDisposable
{
    /// <summary>房主成员 id。</summary>
    public const string HostMemberId = "host";

    /// <summary>包类型。</summary>
    private static class Kinds
    {
        public const string Settings = "snake.settings";
        public const string Direction = "snake.dir";
        public const string State = "snake.state";
        public const string Notice = "snake.notice";
    }

    private readonly string _localName;
    private RoomHost? _host;
    private RoomClient? _client;
    private readonly Dictionary<RoomLink, string> _members = new();
    private readonly object _gate = new();
    private int _stopping;

    /// <summary>创建会话。</summary>
    /// <param name="localName">本端名字。</param>
    public SnakeNetSession(string localName) => _localName = localName;

    /// <summary>是不是房主。</summary>
    public bool IsHost => _host is not null;

    /// <summary>房间人数（含房主）。</summary>
    public int MemberCount
    {
        get { lock (_gate) return _members.Count + (_host is not null ? 1 : 0); }
    }

    /// <summary>本端成员 id。</summary>
    public string LocalMemberId { get; private set; } = "";

    /// <summary>监听端口（房主）。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>人数变化。</summary>
    public event Action? PlayersChanged;

    /// <summary>房主 → 客户端：对局设置。</summary>
    public event Action<SnakeSettings>? SettingsReceived;

    /// <summary>房主 → 客户端：状态帧。</summary>
    public event Action<SnakeRenderFrame>? StateReceived;

    /// <summary>客户端 → 房主：方向输入。</summary>
    public event Action<string, SnakeDirection>? DirectionReceived;

    // ---------------------------------------------------------------- 开房 / 连接

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

        // 打个招呼，让房主登记
        await client.SendAsync(RoomFrame.New(Kinds.Notice, _localName,
            new SnakeNotice { Text = "join", Name = _localName }), ct).ConfigureAwait(false);
        return "";
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>房主：把设置发给所有人（新客户端加入时也会补发）。</summary>
    public async Task SendSettingsAsync(SnakeSettings settings)
    {
        var frame = RoomFrame.New(Kinds.Settings, HostMemberId, settings.Clone());
        await BroadcastAsync(frame).ConfigureAwait(false);
    }

    /// <summary>房主：广播一帧状态。</summary>
    public async Task BroadcastAsync(SnakeRenderFrame frame, string resultText = "")
    {
        if (_host is null) return;

        var payload = new SnakeStatePayload
        {
            Frame = frame,
            ResultText = resultText ?? "",
        };

        var roomFrame = RoomFrame.New(Kinds.State, HostMemberId, payload);
        try { await _host.BroadcastAsync(roomFrame).ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    /// <summary>客户端：上报方向。</summary>
    public async Task SendDirectionAsync(SnakeDirection direction)
    {
        var client = _client;
        if (client is null) return;
        try
        {
            await client.SendAsync(RoomFrame.New(Kinds.Direction, _localName,
                new SnakeDirectionPayload { Direction = (int)direction })).ConfigureAwait(false);
        }
        catch { /* 连接可能断了 */ }
    }

    private async Task BroadcastAsync(RoomFrame frame)
    {
        if (_host is null) return;
        try { await _host.BroadcastAsync(frame).ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 收包

    private void OnClientConnected(RoomLink link)
    {
        // 客户端连接后先登记（名字靠 hello 包带过来；这里先用占位名）
        lock (_gate) _members[link] = "玩家";
        PlayersChanged?.Invoke();
    }

    private void OnHostFrame(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;

        switch (frame.Kind)
        {
            case Kinds.Notice:
                if (frame.ExtraAs<SnakeNotice>() is { } notice && notice.Name.Length > 0)
                {
                    lock (_gate) _members[link] = notice.Name;
                    PlayersChanged?.Invoke();
                }
                return;

            case Kinds.Direction:
                if (frame.ExtraAs<SnakeDirectionPayload>() is { } dir)
                {
                    var id = MemberIdOf(link);
                    DirectionReceived?.Invoke(id, (SnakeDirection)Math.Clamp(dir.Direction, 0, 3));
                }
                return;

            default:
                return;
        }
    }

    private void OnClientFrame(RoomLink link, RoomFrame frame, byte[] payload)
    {
        _ = payload;
        if (Volatile.Read(ref _stopping) != 0) return;

        switch (frame.Kind)
        {
            case Kinds.Settings:
                if (frame.ExtraAs<SnakeSettings>() is { } settings)
                    SettingsReceived?.Invoke(settings);
                return;

            case Kinds.State:
                if (frame.ExtraAs<SnakeStatePayload>() is { } state && state.Frame is not null)
                {
                    state.Frame.ResultText = state.ResultText ?? "";
                    StateReceived?.Invoke(state.Frame);
                }
                return;

            default:
                return;
        }
    }

    private void OnClientClosed(RoomLink link, string? error)
    {
        _ = error;
        if (Volatile.Read(ref _stopping) != 0) return;

        lock (_gate) _members.Remove(link);
        PlayersChanged?.Invoke();
    }

    private void OnClientClosed(RoomLink link, string? error, bool _)
        => OnClientClosed(link, error);

    private string MemberIdOf(RoomLink link)
    {
        lock (_gate)
        {
            return _members.TryGetValue(link, out var name) ? name : "玩家";
        }
    }

    // ---------------------------------------------------------------- 收尾

    /// <summary>停止（幂等）。</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;

        lock (_gate) _members.Clear();

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

/// <summary>设置包。</summary>
public sealed class SnakeSettingsPayload
{
    /// <summary>设置。</summary>
    public SnakeSettings? Settings { get; set; }
}

/// <summary>方向包。</summary>
public sealed class SnakeDirectionPayload
{
    /// <summary>方向（0~3）。</summary>
    public int Direction { get; set; }
}

/// <summary>状态包。</summary>
public sealed class SnakeStatePayload
{
    /// <summary>一帧状态。</summary>
    public SnakeRenderFrame? Frame { get; set; }

    /// <summary>结果文字。</summary>
    public string? ResultText { get; set; }
}

/// <summary>握手 / 提示包。</summary>
public sealed class SnakeNotice
{
    /// <summary>文本。</summary>
    public string Text { get; set; } = "";

    /// <summary>名字。</summary>
    public string Name { get; set; } = "";
}
