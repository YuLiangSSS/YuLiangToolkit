using System.Net;
using System.Net.Sockets;

namespace YuLiangToolkit.Core.Net;

/// <summary>
/// 房间主机端：开一个 TCP 监听等人连进来，把收到的帧转给使用方（本层<b>不转发、不懂业务</b>，
/// 转发策略由上层决定 —— 棋牌房间是「主机权威、只保留一个客户端」）。
///
/// <para>监听地址传空字符串时自动挑：本机支持 IPv6 就用 <c>::</c>（双栈，IPv4 与 IPv6 都能连），
/// 否则 <c>0.0.0.0</c>。端口传 0 让系统分配，<see cref="EndPoint"/> 里是真实端口。</para>
///
/// <para>⚠ <b>双栈必须显式打开</b>（<c>listener.Server.DualMode = true</c>）：Windows 上 IPv6
/// 通配地址默认「只收 IPv6」，不设的话绑 <c>::</c> 之后 IPv4 客户端一个都连不进来。</para>
/// </summary>
public sealed class RoomHost : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<RoomLink> _links = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _disposed;

    /// <summary>是不是正在监听。</summary>
    public bool IsListening { get; private set; }

    /// <summary>实际监听的端点（端口填 0 时这里是系统分配的真实端口）。</summary>
    public IPEndPoint? EndPoint { get; private set; }

    /// <summary>这个监听是不是双栈的（同一个端口 IPv4 与 IPv6 都收）。</summary>
    public bool DualStack { get; private set; }

    /// <summary>当前连着的客户端数量。</summary>
    public int ClientCount
    {
        get { lock (_gate) return _links.Count; }
    }

    /// <summary>当前连着的客户端连接（快照，遍历时不会因为有人断开而抛异常）。</summary>
    public IReadOnlyList<RoomLink> Links
    {
        get { lock (_gate) return _links.ToList(); }
    }

    /// <summary>有客户端连进来（accept 之后立刻触发；<b>后台线程</b>）。</summary>
    public event Action<RoomLink>? ClientConnected;

    /// <summary>收到某个客户端的帧（<b>后台线程</b>；ping/pong 已由 <see cref="RoomLink"/> 内部处理）。</summary>
    public event Action<RoomLink, RoomFrame, byte[]>? FrameReceived;

    /// <summary>某个客户端断开（<b>后台线程</b>；error 为 null 表示对方正常关闭）。</summary>
    public event Action<RoomLink, string?>? ClientClosed;

    /// <summary>
    /// 开始监听。<paramref name="listenAddress"/> 支持空（自动挑）、<c>0.0.0.0</c>、<c>::</c>、
    /// <c>[::]</c>、具体网卡地址、主机名；也可以带端口（<c>0.0.0.0:5310</c>，此时以文本里的为准）。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因（界面直接显示）。</returns>
    public async Task<string> StartAsync(string listenAddress, int port, CancellationToken ct = default)
    {
        if (IsListening) return "";
        if (Volatile.Read(ref _disposed) != 0) return "已释放，不能再用";

        var raw = (listenAddress ?? "").Trim();
        if (raw.Length == 0) raw = Socket.OSSupportsIPv6 ? "::" : "0.0.0.0";

        var (ok, host, portFromText, error) = RoomProtocol.ParseEndpoint(raw);
        if (!ok) return error;
        if (portFromText > 0) port = portFromText;      // 允许 "0.0.0.0:5310" 这种写法
        host = host.Trim('[', ']');
        if (host.Length == 0) host = "0.0.0.0";

        IPAddress address;
        if (!IPAddress.TryParse(host, out var parsed))
        {
            // 允许填主机名（例如 localhost 或本机计算机名）
            var (resolved, endPoint, _, resError) =
                await RoomProtocol.ResolveAsync(host, port <= 0 ? 1 : port, ct).ConfigureAwait(false);
            if (!resolved || endPoint is not IPEndPoint ipEnd)
                return resError.Length > 0 ? resError : $"监听地址 {host} 解析失败";
            address = ipEnd.Address;
        }
        else
        {
            address = parsed;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            return "本机不支持 IPv6，请改用 IPv4 地址（0.0.0.0）";

        port = port is >= 0 and <= 65535 ? port : 0;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(address, port);
            // ⚠ 双栈必须显式打开：Windows 上 IPv6 通配地址默认「只收 IPv6」
            if (address.Equals(IPAddress.IPv6Any)) _listener.Server.DualMode = true;
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            DualStack = address.AddressFamily == AddressFamily.InterNetworkV6 && _listener.Server.DualMode;
            IsListening = true;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return "";
        }
        catch (SocketException ex)
        {
            IsListening = false;
            if (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
                return $"端口 {port} 开不了监听：{RoomLink.Describe(ex)}（换一个端口试试）";
            return $"开不了监听：{RoomLink.Describe(ex)}";
        }
        catch (Exception ex)
        {
            IsListening = false;
            return $"开不了监听：{RoomLink.Describe(ex)}";
        }
    }

    /// <summary>停止监听并断开所有客户端（幂等）。</summary>
    public async Task StopAsync(string reason = "")
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { /* 已释放 */ }
        try { _listener?.Stop(); } catch { /* 已停止 */ }
        _listener = null;
        IsListening = false;

        List<RoomLink> links;
        lock (_gate)
        {
            links = _links.ToList();
            _links.Clear();
        }
        foreach (var link in links)
        {
            try { link.Close(string.IsNullOrEmpty(reason) ? null : reason); } catch { /* 已经断了 */ }
        }

        if (_acceptLoop is not null)
        {
            try { await Task.WhenAny(_acceptLoop, Task.Delay(1500)).ConfigureAwait(false); } catch { /* 忽略 */ }
            _acceptLoop = null;
        }

        cts?.Dispose();
        EndPoint = null;
        DualStack = false;
    }

    // ---------------------------------------------------------------- 接受连接

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception) { continue; }

            Accept(tcp);
        }
    }

    private void Accept(TcpClient tcp)
    {
        // 先建连接但暂不读 → 挂完事件再开始读，避免「连上就发来的第一帧」丢掉
        var link = RoomLink.FromDeferred(tcp);
        link.FrameReceived += OnLinkFrameReceived;
        link.Closed += OnLinkClosed;
        lock (_gate) _links.Add(link);
        link.StartReading();

        try { ClientConnected?.Invoke(link); } catch { /* 上层事件异常不能弄断 accept 循环 */ }
    }

    private void OnLinkFrameReceived(RoomLink link, RoomFrame frame, byte[] payload)
    {
        try { FrameReceived?.Invoke(link, frame, payload); } catch { /* 上层异常不影响读循环 */ }
    }

    private void OnLinkClosed(RoomLink link, string? error)
    {
        bool removed;
        lock (_gate) removed = _links.Remove(link);
        if (!removed) return;                          // 不是本主机管的连接（或已经移除过）
        try { ClientClosed?.Invoke(link, error); } catch { /* 忽略 */ }
    }

    // ---------------------------------------------------------------- 发送

    /// <summary>给所有客户端发一帧（<paramref name="except"/> 排除发起者自己）；单个失败不影响其它人。</summary>
    public async Task BroadcastAsync(RoomFrame header, RoomLink? except = null, CancellationToken ct = default)
    {
        foreach (var link in Links)
        {
            if (except is not null && ReferenceEquals(link, except)) continue;
            try
            {
                // 每个连接都复制一份头：RoomLink.SendAsync 会改写 Payload，共用一个对象会在并发下打架
                var copy = new RoomFrame
                {
                    Kind = header.Kind,
                    From = header.From,
                    Frame = header.Frame,
                    ReplyTo = header.ReplyTo,
                    Extra = header.Extra,
                    Payload = 0,
                };
                await link.SendAsync(copy, default, ct).ConfigureAwait(false);
            }
            catch { /* 单个客户端发失败不影响其它人 */ }
        }
    }

    /// <summary>给某个客户端发一帧（失败不抛给调用方：界面上不该因为对方掉线弹异常）。</summary>
    public async Task SendAsync(RoomLink link, RoomFrame header, CancellationToken ct = default)
    {
        if (!link.IsOpen) return;
        try
        {
            await link.SendAsync(header, default, ct).ConfigureAwait(false);
        }
        catch { /* 单个客户端发失败不影响其它人 / 不打断调用方 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
    }
}
