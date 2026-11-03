using System.Threading.Channels;
using YuLiangToolkit.Tools.ChatRoom.Call;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 通话媒体包的发送队列（每条媒体连接一个）。
///
/// <para><b>为什么要有队列</b>：采集线程 / 转发线程都不能被 TCP 发送卡住 —— 某个人网络慢、
/// 或者一条连接暂时堵住时，不能把人家的声音也一起拖死。所以发送是「非阻塞入队 + 单独一个写协程」，
/// 队列满了就**丢旧包**（视频优先丢，音频尽量保留）。</para>
///
/// <para>音频与视频分两个通道：写协程每次先尽量清空音频，再发 1~2 帧视频，
/// 保证拥塞时先保声音（听得见比看得清重要）。</para>
/// </summary>
internal sealed class CallSendQueue : IAsyncDisposable
{
    private const int AudioCapacity = 60;   // 60 × 40ms ≈ 2.4 秒的音频缓冲
    private const int VideoCapacity = 16;   // 16 帧 @10fps ≈ 1.6 秒

    private readonly ChatLink _link;
    private readonly Channel<OutPacket> _audio = Channel.CreateBounded<OutPacket>(
        new BoundedChannelOptions(AudioCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly Channel<OutPacket> _video = Channel.CreateBounded<OutPacket>(
        new BoundedChannelOptions(VideoCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writer;
    private int _droppedVideo;
    private int _droppedAudio;

    public CallSendQueue(ChatLink link)
    {
        _link = link;
        _writer = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    /// <summary>因为队列满被丢掉的视频帧数（界面统计用）。</summary>
    public int DroppedVideoFrames => Volatile.Read(ref _droppedVideo);

    /// <summary>因为队列满被丢掉的音频包数（很少会涨；涨了说明网络已经很差）。</summary>
    public int DroppedAudioPackets => Volatile.Read(ref _droppedAudio);

    /// <summary>
    /// 入队一个媒体包（立刻返回，绝不 await）。<paramref name="fromMemberId"/> 是**原始发送者**：
    /// 主机转发别人的包时要保留它，客户端才能把画面 / 声音对到人。
    /// </summary>
    public void Enqueue(string fromMemberId, CallMediaKind kind, ReadOnlySpan<byte> payload, long timestampMs)
    {
        var channel = kind == CallMediaKind.Audio ? _audio : _video;
        if (channel.Reader.Count >= (kind == CallMediaKind.Audio ? AudioCapacity : VideoCapacity))
        {
            if (kind == CallMediaKind.Audio) Interlocked.Increment(ref _droppedAudio);
            else Interlocked.Increment(ref _droppedVideo);
        }

        // Copy：调用方的缓冲是复用的（采集 / 读到的那一块），入队后必须自己留一份
        channel.Writer.TryWrite(new OutPacket(fromMemberId, kind, payload.ToArray(), timestampMs));
        try { _signal.Release(); }
        catch (Exception) { /* 队列刚好被 Dispose：丢掉这次唤醒，不能把转发线程带崩 */ }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 200ms 超时只是为了让停止请求能及时被看到；正常情况是入队时唤醒
                    await _signal.WaitAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }

                // 先清音频（最多一次 12 包，防止单个循环饿死其它事）
                for (var i = 0; i < 12 && _audio.Reader.TryRead(out var audio); i++)
                {
                    if (!await SendAsync(audio, ct).ConfigureAwait(false)) return;
                }

                // 再发 1~2 帧视频
                for (var i = 0; i < 2 && _video.Reader.TryRead(out var video); i++)
                {
                    if (!await SendAsync(video, ct).ConfigureAwait(false)) return;
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception) { /* 连接断了：静默停止，由 Closed 事件通知上层 */ }
    }

    private async Task<bool> SendAsync(OutPacket packet, CancellationToken ct)
    {
        try
        {
            await _link.SendCallMediaAsync(packet.Kind, packet.FromMemberId, packet.Payload,
                packet.TimestampMs, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception) { return false; }   // 连接已断：写协程退出，Closed 会让上层收尾
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch { }
        try { await _writer.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        _audio.Writer.TryComplete();
        _video.Writer.TryComplete();
        _cts.Dispose();
        _signal.Dispose();
    }

    private readonly record struct OutPacket(string FromMemberId, CallMediaKind Kind, byte[] Payload, long TimestampMs);
}
