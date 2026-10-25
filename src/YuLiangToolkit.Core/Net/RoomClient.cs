using System.Net;
using System.Net.Sockets;

namespace YuLiangToolkit.Core.Net;

/// <summary>
/// 房间客户端：填「主机地址（IPv4 / IPv6 / 域名）+ 端口」连过去。
///
/// <para><b>内网穿透也能用</b>：这里不做任何「局域网专属」的事 —— 域名会走 DNS、
/// IPv6 地址能直连，所以穿透工具（frp / ngrok / 花生壳…）给出的
/// <c>域名:端口</c> 或 <c>公网IP:端口</c> 填进来就行。</para>
///
/// <para>本层只管连接与帧收发，<b>握手由上层做</b>（棋牌房间的 hello/welcome 在 <c>BoardRoomSession</c>）。</para>
/// </summary>
public sealed class RoomClient : IAsyncDisposable
{
    private RoomLink? _link;
    private int _disposed;

    /// <summary>连上了没有（TCP 已连；不代表业务握手完成）。</summary>
    public bool IsConnected => _link is { IsOpen: true };

    /// <summary>对端地址。</summary>
    public EndPoint? Remote => _link?.Remote;

    /// <summary>最近一次心跳往返延迟（毫秒）。</summary>
    public int LatencyMs => _link?.LatencyMs ?? 0;

    /// <summary>实际连过去时的地址文本（域名会换成解析后的 IP；界面显示用）。</summary>
    public string ServerText { get; private set; } = "";

    /// <summary>收到一帧（<b>后台线程</b>；ping/pong 已由 <see cref="RoomLink"/> 内部处理）。</summary>
    public event Action<RoomLink, RoomFrame, byte[]>? FrameReceived;

    /// <summary>连接断开（<b>后台线程</b>；error 为 null 表示对方正常关闭）。</summary>
    public event Action<RoomLink, string?>? Closed;

    /// <summary>
    /// 连接主机。<paramref name="addressText"/> 可以带端口
    /// （<c>192.168.1.5:5200</c> / <c>[fe80::1]:5200</c> / <c>域名:端口</c>），
    /// 端口参数填 0 时用它带的那个。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回中文原因（界面直接显示）。</returns>
    public async Task<string> ConnectAsync(string addressText, int port = 0, CancellationToken ct = default)
    {
        if (_link is not null) return "已经连着了，先断开再连";
        if (Volatile.Read(ref _disposed) != 0) return "已释放，不能再用";

        var (ok, host, portFromText, error) = RoomProtocol.ParseEndpoint(addressText);
        if (!ok) return error;
        if (portFromText > 0) port = portFromText;
        if (port is <= 0 or > 65535) return "请填写端口（1~65535）";

        var (resolved, endPoint, display, resError) = await RoomProtocol.ResolveAsync(host, port, ct).ConfigureAwait(false);
        if (!resolved || endPoint is not IPEndPoint ipEnd) return resError;

        var tcp = new TcpClient(ipEnd.AddressFamily);
        var started = Environment.TickCount64;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(ipEnd, connectCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { tcp.Close(); } catch { /* 已经关了 */ }
            return $"连接 {display} 超时（10 秒没连上）：检查地址 / 端口 / 防火墙，内网穿透的话看映射还在不在";
        }
        catch (Exception ex)
        {
            try { tcp.Close(); } catch { /* 已经关了 */ }
            return $"连不上 {display}：{RoomLink.Describe(ex)}";
        }

        _ = started;                                    // 连接耗时留给上层提示（RoomLink 延迟另有测量）
        var link = RoomLink.FromDeferred(tcp);
        link.FrameReceived += (l, frame, payload) =>
        {
            try { FrameReceived?.Invoke(l, frame, payload); } catch { /* 上层异常不影响读循环 */ }
        };
        link.Closed += (l, err) =>
        {
            if (ReferenceEquals(_link, l)) _link = null;
            try { Closed?.Invoke(l, err); } catch { /* 忽略 */ }
        };
        _link = link;
        ServerText = display;
        link.StartReading();
        return "";
    }

    /// <summary>发一帧（没连上 / 已断开时静默忽略，不抛给界面线程）。</summary>
    public async Task SendAsync(RoomFrame header, CancellationToken ct = default)
    {
        var link = _link;
        if (link is null || !link.IsOpen) return;
        try
        {
            await link.SendAsync(header, default, ct).ConfigureAwait(false);
        }
        catch { /* 发送失败按掉线处理（读循环 / 心跳会关掉连接） */ }
    }

    /// <summary>
    /// 「网络断了」：直接掐掉底层连接（<b>不发 bye</b>），对端只会看到连接被重置。
    /// 自检 / 排错时用它模拟断网；正常离开请用 <see cref="CloseAsync"/>（会先发 bye）。
    /// </summary>
    public void Abort(string reason = "网络连接中断")
    {
        var link = _link;
        _link = null;
        try { link?.Close(reason); } catch { /* 已经断了 */ }
    }

    /// <summary>断开（幂等）。</summary>
    public async Task CloseAsync(string reason = "")
    {
        var link = _link;
        _link = null;
        if (link is null) return;
        link.Close(string.IsNullOrEmpty(reason) ? null : reason);
        try { await link.DisposeAsync().ConfigureAwait(false); } catch { /* 忽略 */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync().ConfigureAwait(false);
    }
}
