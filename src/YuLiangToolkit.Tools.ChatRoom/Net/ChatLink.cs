using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Traffic;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 一条 TCP 连接上的读写（帧格式见 <see cref="ChatProtocol"/>）+ 流量计量 + 心跳。
///
/// <para><b>这里有一条必须记住的约定：文件走的是「控制连接」本身，不另开数据连接。</b>
/// 原因是内网穿透 / 端口映射只把主机那一个监听端口转出来 —— 接收方另开的临时端口从外网根本连不上。
/// 既然只能有一条连接，就把传输排个队（<see cref="_writeGate"/> 一次只放一个发送者），
/// 传文件期间聊天文字排在它后面（对方那边照旧一边收文件一边能发消息：TCP 两个方向互不干扰）。</para>
///
/// <para>读循环读到 <see cref="ChatProtocol.Kinds.TransferStart"/> 时，会紧接着把
/// <see cref="TransferInfo.SizeBytes"/> 个裸字节读干净（<see cref="RawTransferReceived"/>）
/// —— 所以读循环必须能“停在半个包之间”再继续，这也是帧格式里长度前缀要写总长的原因。</para>
/// </summary>
public sealed class ChatLink : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _readLoop;

    private long _lastActivityTicks = Environment.TickCount64;
    private int _closed;

    private ChatLink(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _client.ReceiveBufferSize = 512 * 1024;
        _client.SendBufferSize = 512 * 1024;
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint;
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    /// <summary>从已经连上的 <see cref="TcpClient"/> 建一条连接（主机 accept 之后 / 客户端 connect 之后用）。</summary>
    public static ChatLink From(TcpClient client, TrafficMeter meter) => new(client) { Meter = meter };

    /// <summary>本端流量计量（发送/接收每写/读一次都会累加，含包头）。</summary>
    public TrafficMeter Meter { get; private init; } = new(0, TrafficQuotaScope.Off);

    /// <summary>对端地址。</summary>
    public EndPoint? Remote { get; private set; }

    /// <summary>对端在成员名单里的信息（收到 Text / TransferStart 时一起带上，界面才有的显示）。</summary>
    public ChatMember? Peer { get; set; }

    /// <summary>对端成员的 Id（握手后填）。</summary>
    public string PeerId => Peer?.MemberId ?? "";

    /// <summary>这条连接还活着没有。</summary>
    public bool IsOpen => _closed == 0 && _client.Connected;

    // ---------------------------------------------------------------- 事件

    /// <summary>收到文字消息。</summary>
    public event Action<ChatLink, TextInfo, string>? TextReceived;

    /// <summary>收到系统提示。（内容, 发送方成员Id）</summary>
    public event Action<ChatLink, SystemInfo>? SystemReceived;

    /// <summary>收到成员名单。</summary>
    public event Action<ChatLink, MemberListInfo>? MembersReceived;

    /// <summary>收到握手包（主机用它认客户端，客户端用它认主机）。</summary>
    public event Action<ChatLink, HelloInfo>? HelloReceived;

    /// <summary>收到主机的握手回执（只有客户端会用到）。</summary>
    public event Action<ChatLink, WelcomeInfo>? WelcomeReceived;

    /// <summary>收到「你被移出房间」。</summary>
    public event Action<ChatLink, KickInfo>? KickReceived;

    /// <summary>
    /// 收到对方发来的文件：回调里必须把 <see cref="TransferInfo.SizeBytes"/> 个裸字节读干净
    /// （用 <see cref="PumpRawBytesAsync"/> 灌进文件，或者 <see cref="SkipRawBytesAsync"/> 丢掉）。
    /// </summary>
    public event Func<ChatLink, TransferInfo, CancellationToken, Task>? RawTransferReceived;

    /// <summary>收到文件的接收结果（发送端用它结束进度条）。</summary>
    public event Action<ChatLink, TransferResultInfo>? TransferResultReceived;

    /// <summary>对方取消了传输。</summary>
    public event Action<ChatLink, CancelInfo>? TransferCancelled;

    /// <summary>心跳回包（界面上的延迟）。</summary>
    public event Action<ChatLink, int>? LatencyUpdated;

    /// <summary>连接断开了（<paramref name="error"/> 为空表示正常关闭）。</summary>
    public event Action<ChatLink, string?>? Closed;

    // ---------------------------------------------------------------- 读循环

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        string? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var idle = Environment.TickCount64 - Interlocked.Read(ref _lastActivityTicks);
                if (idle > ChatProtocol.IdleTimeoutMs)
                {
                    error = $"对方 {idle / 1000} 秒没有任何数据（可能已掉线）";
                    break;
                }

                var header = await ReadFrameAsync(ct).ConfigureAwait(false);
                if (header is null) break;                       // 对方正常关闭
                Trace($"收到 {header.Kind} payload={header.Payload}");

                switch (header.Kind)
                {
                    case ChatProtocol.Kinds.Ping:
                        await SendAsync(new ChatFrameHeader { Kind = ChatProtocol.Kinds.Pong, ReplyTo = header.Frame }, ct)
                            .ConfigureAwait(false);
                        break;

                    case ChatProtocol.Kinds.Pong:
                        LatencyUpdated?.Invoke(this, (int)Math.Max(0, Environment.TickCount64 - PingSentTicks));
                        break;

                    case ChatProtocol.Kinds.Hello:
                        if (header.ExtraAs<HelloInfo>() is { } hello) HelloReceived?.Invoke(this, hello);
                        break;

                    case ChatProtocol.Kinds.Text:
                    {
                        var info = header.ExtraAs<TextInfo>();
                        var text = Encoding.UTF8.GetString(await ReadPayloadAsync(header, ct).ConfigureAwait(false));
                        if (info is not null) TextReceived?.Invoke(this, info, text);
                        break;
                    }

                    case ChatProtocol.Kinds.System:
                        if (header.ExtraAs<SystemInfo>() is { } sys) SystemReceived?.Invoke(this, sys);
                        break;

                    case ChatProtocol.Kinds.Members:
                        if (header.ExtraAs<MemberListInfo>() is { } members) MembersReceived?.Invoke(this, members);
                        break;

                    case ChatProtocol.Kinds.Kick:
                        if (header.ExtraAs<KickInfo>() is { } kick) KickReceived?.Invoke(this, kick);
                        break;

                    case ChatProtocol.Kinds.TransferDone:
                        if (header.ExtraAs<TransferResultInfo>() is { } done)
                        {
                            CompleteResult(done);                 // 先叫醒发送端那个 await
                            TransferResultReceived?.Invoke(this, done);
                        }
                        break;

                    case ChatProtocol.Kinds.TransferCancel:
                        if (header.ExtraAs<CancelInfo>() is { } cancel) TransferCancelled?.Invoke(this, cancel);
                        break;

                    case ChatProtocol.Kinds.TransferStart:
                    {
                        var info = header.ExtraAs<TransferInfo>();
                        if (info is null) break;
                        // 注意：这里必须把裸字节读干净，否则后面的帧会被当成文件内容
                        var handler = RawTransferReceived;
                        if (handler is null)
                        {
                            await SkipRawBytesAsync(info.SizeBytes, ct).ConfigureAwait(false);
                            break;
                        }
                        await handler(this, info, ct).ConfigureAwait(false);
                        break;
                    }

                    case ChatProtocol.Kinds.Welcome:
                        // 主机回给客户端的握手回执：由 ChatClient 挂的处理函数接走
                        if (header.ExtraAs<WelcomeInfo>() is { } welcome) WelcomeReceived?.Invoke(this, welcome);
                        break;

                    case ChatProtocol.Kinds.Bye:
                        break;

                    default:
                        await SkipAsync(header.Payload, ct).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* 正常关闭 */ }
        catch (Exception ex)
        {
            // 处理某条包时抛异常（多半是本端的事件处理函数抛的）也算断线，但别把原因吞掉：
            // 带上异常类型与消息，界面上就能一眼看出是谁的问题（自检里全靠这句定位过 bug）。
            error ??= $"{ex.GetType().Name}：{ex.Message}";
        }

        Trace($"读循环退出：{error ?? "(对方正常关闭)"}");
        Close(error);    }

    /// <summary>
    /// 把收到的裸字节原样灌进 <paramref name="target"/>（接收端落盘用）。
    /// <paramref name="onProgress"/> 报已读字节，<paramref name="onChunk"/> 拿到刚刚读到的那一块
    /// （算 CRC 用：缓冲区是复用的，不能留引用，必须当场算掉）。
    /// </summary>
    public async Task PumpRawBytesAsync(long length, Stream target, Action<long>? onProgress, CancellationToken ct,
        Action<ReadOnlySpan<byte>>? onChunk = null)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ChatProtocol.FileChunkBytes);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await _stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) throw new IOException("对方在传文件的过程中断开了连接");
                Touch();
                Meter.AddReceived(read);
                onChunk?.Invoke(buffer.AsSpan(0, read));
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
                onProgress?.Invoke(length - remaining);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    /// <summary>丢掉接下来的 <paramref name="bytes"/> 个裸字节（不接收这个文件时也必须读掉）。</summary>
    public async Task SkipRawBytesAsync(long bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;
        await PumpRawBytesAsync(bytes, Stream.Null, null, ct).ConfigureAwait(false);
    }

    private async Task SkipAsync(long bytes, CancellationToken ct)
    {
        if (bytes <= 0) return;
        var buffer = ArrayPool<byte>.Shared.Rent(ChatProtocol.FileChunkBytes);
        try
        {
            var remaining = bytes;
            while (remaining > 0)
            {
                var want = (int)Math.Min(buffer.Length, remaining);
                var read = await _stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
                if (read <= 0) throw new IOException("连接已关闭");
                Touch();
                Meter.AddReceived(read);
                remaining -= read;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    // ---------------------------------------------------------------- 写

    /// <summary>发一个包（头 + 可选载荷）。</summary>
    public async Task SendAsync(ChatFrameHeader header, CancellationToken ct = default, ReadOnlyMemory<byte> payload = default)
    {
        var frame = ChatProtocol.BuildFrame(header, payload.Span);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
            Meter.AddSent(frame.Length);
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>发一条文字消息。</summary>
    public Task SendTextAsync(TextInfo info, string text, CancellationToken ct = default)
        => SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Text, info.Member?.MemberId, info), ct,
            Encoding.UTF8.GetBytes(text));

    /// <summary>发一条系统提示。</summary>
    public Task SendSystemAsync(SystemInfo info, CancellationToken ct = default)
        => SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.System, info.Member?.MemberId, info), ct);

    /// <summary>发成员名单。</summary>
    public Task SendMembersAsync(MemberListInfo members, CancellationToken ct = default)
        => SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Members, PeerId, members), ct);

    /// <summary>
    /// 把一个本地文件整条推过去：<c>TransferStart 头</c> → 裸字节 → 等对方的 <c>TransferDone</c>。
    /// 返回对方算出的结果（<c>null</c> 表示连接断了没等到）。
    /// </summary>
    public async Task<TransferResultInfo?> SendFileAsync(TransferInfo info, string sourcePath,
        Action<long> onProgress, CancellationToken ct)
    {
        var header = ChatFrameHeader.New(ChatProtocol.Kinds.TransferStart, PeerId, info);

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            header.Payload = 0;                                                // 文件本体跟在包头之后，不算“载荷”
            var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, ChatProtocol.JsonOptions);
            var prefix = ChatProtocol.BuildLengthPrefix(headerBytes.Length);    // 前缀只数包头自己的字节
            await _stream.WriteAsync(prefix, ct).ConfigureAwait(false);
            await _stream.WriteAsync(headerBytes, ct).ConfigureAwait(false);
            Meter.AddSent(prefix.Length + headerBytes.Length);

            await using var file = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ChatProtocol.FileChunkBytes, useAsync: true);
            var buffer = ArrayPool<byte>.Shared.Rent(ChatProtocol.FileChunkBytes);
            try
            {
                long sent = 0;
                int read;
                while ((read = await file.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    await _stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    Meter.AddSent(read);
                    sent += read;
                    onProgress(sent);
                    Touch();
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { _writeGate.Release(); }

        return await WaitForResultAsync(info.TransferId, ct).ConfigureAwait(false);
    }

    /// <summary>等待中的传输回执（TransferDone 到达时唤醒发送端那个 await）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<TransferResultInfo>> _pendingResults = new();

    /// <summary>等某个传输的回执（收到 TransferDone 就唤醒；连接断了由取消令牌叫醒）。</summary>
    private async Task<TransferResultInfo?> WaitForResultAsync(string transferId, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<TransferResultInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingResults[transferId] = waiter;

        using var registration = ct.Register(() => waiter.TrySetCanceled(ct));
        try
        {
            var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromMinutes(30), ct)).ConfigureAwait(false);
            if (completed != waiter.Task) return null;
            return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        finally { _pendingResults.TryRemove(transferId, out _); }
    }

    /// <summary>唤醒等在 <see cref="SendFileAsync"/> 里的发送端。</summary>
    internal void CompleteResult(TransferResultInfo result)
    {
        if (_pendingResults.TryGetValue(result.TransferId, out var waiter))
            waiter.TrySetResult(result);
    }

    /// <summary>发一个接收回执。</summary>
    public Task SendTransferResultAsync(TransferResultInfo result, CancellationToken ct = default)
        => SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.TransferDone, PeerId, result), ct);

    /// <summary>通知对方取消 / 失败。</summary>
    public Task SendCancelAsync(CancelInfo info, CancellationToken ct = default)
        => SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.TransferCancel, PeerId, info), ct);

    /// <summary>发心跳。</summary>
    public Task SendPingAsync(CancellationToken ct = default)
    {
        PingSentTicks = Environment.TickCount64;
        return SendAsync(new ChatFrameHeader { Kind = ChatProtocol.Kinds.Ping, From = PeerId, Frame = ChatFrameHeader.NewFrameId() }, ct);
    }

    /// <summary>发「再见」并关闭（礼貌退出：对方立刻知道你是自己走的）。</summary>
    public async Task SendByeAsync(string reason)
    {
        try { await SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Bye, PeerId, new ByeInfo { Reason = reason })).ConfigureAwait(false); }
        catch { /* 已经断了就算了 */ }
    }

    private long PingSentTicks { get; set; }

    // ---------------------------------------------------------------- 收尾

    /// <summary>关闭连接（幂等；返回值表示是不是这次调用真正关的）。</summary>
    public bool Close(string? error = null)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return false;
        try { _cts.Cancel(); } catch { }
        try { _client.Close(); } catch { }
        try { Closed?.Invoke(this, error); } catch { }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _cts.Dispose();
        _writeGate.Dispose();
        _readGate.Dispose();
    }

    // ---------------------------------------------------------------- 底层读

    /// <summary>读一个帧头（长度前缀 + JSON 头），并<b>不</b>读载荷。</summary>
    private async Task<ChatFrameHeader?> ReadFrameAsync(CancellationToken ct)
    {
        await _readGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 长度前缀 = JSON 头的字节数（载荷长度在头里的 Payload 字段里，别混）
            var prefix = new byte[ChatProtocol.LengthPrefixBytes];
            if (!await ReadExactAsync(prefix, ct).ConfigureAwait(false)) return null;
            var headerLength = BitConverter.ToInt32(prefix, 0);
            if (!ChatProtocol.IsValidFrameLength(headerLength))
                throw new IOException($"对方发来的包头长度不合法：{headerLength}（协议不同或者数据被搅乱了）");

            var headerBytes = new byte[headerLength];
            if (!await ReadExactAsync(headerBytes, ct).ConfigureAwait(false)) return null;
            var header = JsonSerializer.Deserialize<ChatFrameHeader>(headerBytes, ChatProtocol.JsonOptions)
                         ?? throw new IOException("包头解析失败");
            return header;
        }
        finally { _readGate.Release(); }
    }

    /// <summary>读掉当前帧的载荷（普通包用；文件包的载荷是裸字节，走 <see cref="PumpAsync"/>）。</summary>
    private async Task<byte[]> ReadPayloadAsync(ChatFrameHeader header, CancellationToken ct)
    {
        if (header.Payload <= 0) return Array.Empty<byte>();
        if (header.Payload > ChatProtocol.MaxFrameBytes)
            throw new IOException($"对方要发的数据太大：{header.Payload} 字节");
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
            Meter.AddReceived(read);
            offset += read;
        }
        return true;
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, Environment.TickCount64);

    /// <summary>排错用：设了 <c>YLT_CHAT_NET_TRACE</c> 就把每个收发帧写进文件（自检定位协议问题时用）。</summary>
    private void Trace(string text)
    {
        var path = Environment.GetEnvironmentVariable("YLT_CHAT_NET_TRACE");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            lock (TraceGate)
                File.AppendAllText(path,
                    $"{DateTime.Now:HH:mm:ss.fff} [{Remote}] {PeerId} {text}{Environment.NewLine}");
        }
        catch { }
    }

    private static readonly object TraceGate = new();

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
