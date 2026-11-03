using YuLiangToolkit.Tools.ChatRoom.Call;
using YuLiangToolkit.Tools.ChatRoom.Protocol;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 客户端侧的 <see cref="ICallTransport"/>：包走那条独立的通话媒体连接（<see cref="ChatLink"/>）。
/// </summary>
internal sealed class ClientCallTransport : ICallTransport
{
    private readonly ChatLink _link;
    private readonly CallSendQueue _queue;
    private readonly string _localMemberId;
    private readonly Timer _keepAlive;
    private int _closed;

    public ClientCallTransport(ChatLink link, string localMemberId)
    {
        _link = link;
        _localMemberId = localMemberId ?? "";
        _queue = new CallSendQueue(link);
        _link.CallMediaReceived += OnMediaReceived;
        _link.Closed += OnLinkClosed;

        // 媒体连接也要心跳：纯语音 + 静音 + 关摄像头时可能长时间一个包都不发，
        // ChatLink 的 90 秒空闲判定会把连接当成掉线掐掉。15 秒 ping 一次刚好。
        _keepAlive = new Timer(_ =>
        {
            try { if (Volatile.Read(ref _closed) == 0) _ = _link.SendPingAsync(); } catch { }
        }, null, 15_000, 15_000);
    }

    public bool IsOpen => Volatile.Read(ref _closed) == 0 && _link.IsOpen;

    public int DroppedVideoFrames => _queue.DroppedVideoFrames;

    public event Action<string, CallMediaKind, byte[], long>? PacketReceived;

    public event Action<string>? Closed;

    public void SendAudio(ReadOnlySpan<byte> ulaw)
        => _queue.Enqueue(_localMemberId, CallMediaKind.Audio, ulaw, Environment.TickCount64);

    public void SendVideo(ReadOnlySpan<byte> jpeg)
        => _queue.Enqueue(_localMemberId, CallMediaKind.Video, jpeg, Environment.TickCount64);

    public void SendControl(bool? micOn, bool? cameraOn)
    {
        try
        {
            _ = _link.SendCallControlAsync(new CallControlInfo { MicOn = micOn, CameraOn = cameraOn });
        }
        catch { /* 连接已经断了：状态上报失败无所谓，Closed 会收尾 */ }
    }

    private void OnMediaReceived(ChatLink link, string fromMemberId, CallMediaKind kind, byte[] payload, CallMediaInfo info)
    {
        try { PacketReceived?.Invoke(fromMemberId, kind, payload, info.TimestampMs); }
        catch { /* 引擎异常不能把读循环带走 */ }
    }

    private void OnLinkClosed(ChatLink link, string? error)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { Closed?.Invoke(error is { Length: > 0 } ? $"通话连接断开（{error}）" : "通话连接已断开"); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _keepAlive.Dispose(); } catch { }
        _link.CallMediaReceived -= OnMediaReceived;
        _link.Closed -= OnLinkClosed;
        try { _ = _queue.DisposeAsync().AsTask(); } catch { }
        try
        {
            _ = _link.SendByeAsync("结束通话");
            _link.Close("结束通话");
            _ = _link.DisposeAsync().AsTask();
        }
        catch { }
    }
}

/// <summary>
/// 主机侧的 <see cref="ICallTransport"/>：主机把自己的包交给 <see cref="CallRelay"/> 广播给所有客户端；
/// 客户端发来的包由 relay 通过 <see cref="CallRelay.PacketReceived"/> 转给本端引擎。
/// </summary>
internal sealed class HostCallTransport : ICallTransport
{
    private readonly CallRelay _relay;
    private readonly string _localMemberId;
    private int _closed;

    public HostCallTransport(CallRelay relay, string localMemberId)
    {
        _relay = relay;
        _localMemberId = localMemberId ?? "";
        _relay.PacketReceived += OnPacketReceived;
    }

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    /// <summary>主机侧丢帧由每条客户端连接各自的发送队列决定，这里不做统计。</summary>
    public int DroppedVideoFrames => 0;

    public event Action<string, CallMediaKind, byte[], long>? PacketReceived;

    /// <summary>主机侧没有「通话连接被对端断开」这回事（要退出是主机自己点的），所以这个事件不会被触发。</summary>
    public event Action<string>? Closed
    {
        add { /* 主机侧由 ChatHost / CallRelay 管连接生命周期，这里不需要订阅 */ }
        remove { }
    }

    public void SendAudio(ReadOnlySpan<byte> ulaw)
        => _relay.BroadcastHostMedia(CallMediaKind.Audio, ulaw, Environment.TickCount64);

    public void SendVideo(ReadOnlySpan<byte> jpeg)
        => _relay.BroadcastHostMedia(CallMediaKind.Video, jpeg, Environment.TickCount64);

    public void SendControl(bool? micOn, bool? cameraOn)
        => _relay.UpdateHostState(micOn, cameraOn);

    private void OnPacketReceived(string fromMemberId, CallMediaKind kind, byte[] payload, long timestampMs)
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try { PacketReceived?.Invoke(fromMemberId, kind, payload, timestampMs); }
        catch { /* 引擎异常不能把读循环带走 */ }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _relay.PacketReceived -= OnPacketReceived; } catch { }
    }
}
