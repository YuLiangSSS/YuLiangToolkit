using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace YuLiangToolkit.Core.Net;

/// <summary>
/// 一条 TCP 连接上的帧读写 + 心跳（帧格式见 <see cref="RoomProtocol"/>）。
///
/// <para><b>读循环约定</b>：每读到一帧，先把 <see cref="RoomFrame.Payload"/> 个裸字节
/// <b>读干净</b>再抛 <see cref="FrameReceived"/> —— 不读干净，下一帧的长度前缀就会错位，
/// 表现为「包头长度不合法 / JSON 解析失败」，非常难查。</para>
///
/// <para><b>心跳</b>：后台每 <see cref="RoomProtocol.KeepAliveMs"/> 发一次 <c>ping</c>，
/// 对方回 <c>pong</c>（内部处理，不会抛给使用方）；超过 <see cref="RoomProtocol.IdleTimeoutMs"/>
/// 一点数据都没收到就算掉线，主动 <see cref="Close"/> 并带上中文原因。</para>
///
/// <para><b>线程</b>：所有 <see cref="FrameReceived"/> / <see cref="Closed"/> 回调都在后台线程触发，
/// 界面侧请自行切回 UI 线程。</para>
/// </summary>
public sealed class RoomLink : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private Task? _readLoop;
    private Task? _keepAliveLoop;
    private long _lastActivityTicks = Environment.TickCount64;
    private long _pingSentTicks;
    private int _latencyMs;
    private int _closed;

    private RoomLink(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _client.ReceiveBufferSize = 256 * 1024;
        _client.SendBufferSize = 256 * 1024;
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint;
    }

    /// <summary>
    /// 从已经连上的 <see cref="TcpClient"/> 建一条连接并<b>立刻开始读</b>
    /// （主机 accept 之后 / 客户端 connect 之后用）。
    /// </summary>
    public static RoomLink From(TcpClient client) => FromDeferred(client, startNow: true);

    /// <summary>
    /// 内部用：先建好连接但暂不读，等调用方挂完事件再 <see cref="StartReading"/>。
    /// 这样「对方连上就立刻发来的第一帧」不会因为事件还没挂上而丢掉。
    /// </summary>
    internal static RoomLink FromDeferred(TcpClient client, bool startNow = false)
    {
        var link = new RoomLink(client);
        if (startNow) link.StartReading();
        return link;
    }

    /// <summary>开始读循环（只该调用一次；重复调用无副作用）。</summary>
    internal void StartReading()
    {
        if (_readLoop is not null) return;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
    }

    /// <summary>对端地址。</summary>
    public EndPoint? Remote { get; }

    /// <summary>这条连接还活着没有。</summary>
    public bool IsOpen => Volatile.Read(ref _closed) == 0 && _client.Connected;

    /// <summary>最近一次 ping→pong 的往返延迟（毫秒；还没测过时是 0）。</summary>
    public int LatencyMs => Volatile.Read(ref _latencyMs);

    /// <summary>
    /// 距离上一次收到<b>任何</b>数据过了多少毫秒（心跳 pong 也会刷新）。
    /// 主机用它判断「已经连着的那个客户端是不是早就掉线了但 TCP 还没断」——
    /// 活着的对端每 15 秒至少有一个 pong，所以这个值正常不会超过 15 秒。
    /// </summary>
    public int IdleMs => (int)Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks));

    /// <summary>收到一帧（<c>ping</c>/<c>pong</c> 已由内部处理，不会抛到这里；载荷已读干净）。</summary>
    public event Action<RoomLink, RoomFrame, byte[]>? FrameReceived;

    /// <summary>连接断开（<paramref name="error"/> 为 null 表示对方正常关闭）。</summary>
    public event Action<RoomLink, string?>? Closed;

    // ---------------------------------------------------------------- 读循环

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        string? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var idle = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);
                if (idle > RoomProtocol.IdleTimeoutMs)
                {
                    error = $"对方 {idle / 1000} 秒没有任何数据（可能已掉线）";
                    break;
                }

                var header = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (header is null) break;                    // 对方正常关闭

                // 关键：载荷先读干净，再抛事件（否则下一帧会错位）
                var payload = await ReadPayloadAsync(header, ct).ConfigureAwait(false);

                switch (header.Kind)
                {
                    case RoomProtocol.Kinds.Ping:
                        await SendAsync(new RoomFrame
                        {
                            Kind = RoomProtocol.Kinds.Pong,
                            From = header.From,
                            ReplyTo = header.Frame,
                        }, ct: ct).ConfigureAwait(false);
                        break;

                    case RoomProtocol.Kinds.Pong:
                        Volatile.Write(ref _latencyMs,
                            (int)Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref _pingSentTicks)));
                        break;

                    default:
                        FrameReceived?.Invoke(this, header, payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        catch (Exception ex)
        {
            // 处理某条帧时抛异常（多半是本端事件处理函数抛的）也算掉线，但别把原因吞掉
            error ??= $"{ex.GetType().Name}：{ex.Message}";
        }

        Close(error);
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(RoomProtocol.KeepAliveMs, ct).ConfigureAwait(false);

                if (Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks) > RoomProtocol.IdleTimeoutMs)
                {
                    Close($"对方 {RoomProtocol.IdleTimeoutMs / 1000} 秒没有任何数据（可能已掉线）");
                    return;
                }

                if (!IsOpen) return;
                await SendPingAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        catch (Exception ex)
        {
            Close($"心跳失败：{Describe(ex)}");
        }
    }

    // ---------------------------------------------------------------- 写

    /// <summary>发一个包（头 + 可选载荷）。写失败会抛出，调用方自己决定是否当成掉线。</summary>
    public async Task SendAsync(RoomFrame header, ReadOnlyMemory<byte> payload = default,
        CancellationToken ct = default)
    {
        if (!IsOpen) throw new IOException("连接已关闭");
        var frame = RoomProtocol.BuildFrame(header, payload.Span);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            Touch();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>发心跳（内部定时调用；界面「测延迟」也可以主动调一次）。</summary>
    public Task SendPingAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _pingSentTicks, Environment.TickCount64);
        return SendAsync(new RoomFrame
        {
            Kind = RoomProtocol.Kinds.Ping,
            Frame = RoomFrame.NewFrameId(),
        }, ct: ct);
    }

    // ---------------------------------------------------------------- 收尾

    /// <summary>关闭连接（幂等；返回值表示是不是这次调用真正关的）。</summary>
    public bool Close(string? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return false;
        try { _cts.Cancel(); } catch { /* 已释放 */ }
        try { _client.Close(); } catch { /* 已经断了 */ }
        try { Closed?.Invoke(this, error); } catch { /* 事件处理函数抛异常不影响关闭 */ }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { if (_readLoop is not null) await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* 超时就算了 */ }
        try { if (_keepAliveLoop is not null) await _keepAliveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* 超时就算了 */ }
        _cts.Dispose();
        _writeGate.Dispose();
    }

    // ---------------------------------------------------------------- 底层读

    /// <summary>读一个帧头（长度前缀 + JSON 头），并<b>不</b>读载荷。</summary>
    private async Task<RoomFrame?> ReadFrameAsync(CancellationToken ct)
    {
        var prefix = new byte[RoomProtocol.LengthPrefixBytes];
        if (!await ReadExactAsync(prefix, ct).ConfigureAwait(false)) return null;

        // 长度前缀 = JSON 头的字节数（载荷长度在头里的 Payload 字段里，别混）
        var headerLength = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (!RoomProtocol.IsValidFrameLength(headerLength))
            throw new IOException($"对方发来的包头长度不合法：{headerLength}（协议不同或者数据被搅乱了）");

        var headerBytes = new byte[headerLength];
        if (!await ReadExactAsync(headerBytes, ct).ConfigureAwait(false)) return null;
        var header = JsonSerializer.Deserialize<RoomFrame>(headerBytes, RoomProtocol.JsonOptions)
                     ?? throw new IOException("包头解析失败");
        return header;
    }

    /// <summary>读掉当前帧的载荷（普通包用；不读干净下一帧就会错位）。</summary>
    private async Task<byte[]> ReadPayloadAsync(RoomFrame header, CancellationToken ct)
    {
        if (header.Payload <= 0) return Array.Empty<byte>();
        if (header.Payload > RoomProtocol.MaxPayloadBytes)
            throw new IOException($"对方要发的数据太大：{header.Payload} 字节（超过单包上限）");
        var buffer = new byte[header.Payload];
        if (!await ReadExactAsync(buffer, ct).ConfigureAwait(false))
            throw new IOException("连接在读取数据时关闭");
        return buffer;
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read <= 0) return false;
            Touch();
            offset += read;
        }
        return true;
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    /// <summary>把 socket 异常翻译成用户能看懂的话。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        SocketException se => se.SocketErrorCode switch
        {
            SocketError.ConnectionRefused => "对方拒绝连接（主机没开监听？端口填错了？防火墙拦了？）",
            SocketError.TimedOut => "连接超时（地址或端口不通：内网穿透的映射是不是掉了？）",
            SocketError.HostNotFound => "找不到这个地址（域名解析失败）",
            SocketError.NetworkUnreachable => "网络不可达（不在同一个局域网 / 没连网）",
            SocketError.ConnectionReset => "连接被对方重置（对方程序关掉了 / 防火墙切断）",
            _ => $"网络错误：{se.SocketErrorCode}（{se.Message}）",
        },
        IOException io => io.Message,
        OperationCanceledException => "已取消",
        _ => $"{ex.GetType().Name}：{ex.Message}",
    };
}
