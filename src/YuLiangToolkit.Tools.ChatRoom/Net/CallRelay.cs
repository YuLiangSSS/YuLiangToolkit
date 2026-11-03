using YuLiangToolkit.Tools.ChatRoom.Call;
using YuLiangToolkit.Tools.ChatRoom.Protocol;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 主机侧的通话中转（「群通话」的核心）：
/// <list type="bullet">
///   <item>把客户端的媒体连接登记成「某个成员的媒体通道」（同一端口、<c>callHello</c> 握手 + 令牌校验）；</item>
///   <item>把任何一个人的音频 / 视频包转发给其余所有媒体连接（主机自己既发也收）；</item>
///   <item>维护「谁在通话里、麦克风 / 摄像头开没开」的权威快照，变化时让 <see cref="StateChanged"/> 触发
///         <c>callState</c> 广播。</item>
/// </list>
///
/// <para><b>为什么客户端之间不直连</b>：聊天室本来就是「客户端只连主机」的星形拓扑
/// （内网穿透只映射主机那一个端口，客户端之间根本连不上）。群通话沿用这个拓扑，
/// 媒体也走主机中转，不需要另开端口、不需要任何人做端口映射。</para>
///
/// <para>本类只管「转发与状态」，不碰采集 / 编解码 —— 那些在 <c>CallEngine</c> 里，
/// 主机自己的媒体通过 <see cref="BroadcastHostMedia"/> 发出去，收到的包用 <see cref="PacketReceived"/> 交给它。</para>
/// </summary>
internal sealed class CallRelay : IAsyncDisposable
{
    /// <summary>一条客户端媒体连接。</summary>
    private sealed class ClientLink
    {
        public required ChatMember Member { get; init; }
        public required ChatLink Link { get; init; }
        public required CallSendQueue Queue { get; init; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, ClientLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CallParticipantWire> _participants = new(StringComparer.Ordinal);
    private DateTime? _startedAtUtc;
    private string _hostMemberId = "";
    private bool _disposed;

    /// <summary>参与者 / 麦克风 / 摄像头状态变了（ChatHost 收到后广播 <c>callState</c>）。</summary>
    public event Action? StateChanged;

    /// <summary>收到某人的媒体包（主机自己的通话引擎消费；参数：发送方成员 Id、类型、裸字节、时间戳）。</summary>
    public event Action<string, CallMediaKind, byte[], long>? PacketReceived;

    /// <summary>某人离开通话（主机自己的通话引擎要清掉他的混音缓冲 / 画面）。</summary>
    public event Action<string>? ParticipantLeft;

    /// <summary>现在通话里有几个人（含主机自己）。</summary>
    public int ParticipantCount
    {
        get { lock (_gate) return _participants.Count; }
    }

    /// <summary>主机自己现在在不在通话里。</summary>
    public bool IsHostInCall
    {
        get { lock (_gate) return _hostMemberId.Length > 0 && _participants.ContainsKey(_hostMemberId); }
    }

    /// <summary>维护主机自己的成员 Id（<see cref="BroadcastHostMedia"/> 用它标记发送者）。</summary>
    public void SetHostMemberId(string memberId)
    {
        lock (_gate) _hostMemberId = memberId ?? "";
    }

    /// <summary>这条媒体连接对应的成员是不是已经在通话里（重复握手判断用）。</summary>
    public bool Contains(string memberId)
    {
        lock (_gate) return _links.ContainsKey(memberId);
    }

    /// <summary>
    /// 登记一条客户端媒体连接。失败返回 false 并给出原因（人数满 / 重复连接 / 房间已关）。
    /// </summary>
    public bool TryRegisterClient(ChatMember member, ChatLink link, out string error)
    {
        error = "";
        CallSendQueue queue;
        lock (_gate)
        {
            if (_disposed) { error = "房间已关闭"; return false; }
            if (_links.ContainsKey(member.MemberId)) { error = "这个成员已经有一条通话连接了"; return false; }
            if (!_participants.ContainsKey(member.MemberId)
                && _participants.Count >= CallProtocolRules.MaxParticipants)
            {
                error = $"通话人数已满（最多 {CallProtocolRules.MaxParticipants} 人）";
                return false;
            }

            queue = new CallSendQueue(link);
            _links[member.MemberId] = new ClientLink { Member = member.Clone(), Link = link, Queue = queue };
            _participants[member.MemberId] = new CallParticipantWire
            {
                MemberId = member.MemberId,
                Name = member.Name,
                Emoji = member.Emoji,
                IsHost = false,
                MicOn = true,
                CameraOn = true,
                JoinedAtUtc = DateTime.UtcNow,
            };
            _startedAtUtc ??= DateTime.UtcNow;
        }

        link.CallMediaReceived += OnClientMedia;
        link.CallControlReceived += OnClientControl;
        link.Closed += OnClientLinkClosed;
        RaiseStateChanged();
        return true;
    }

    /// <summary>主机的聊天连接断开某个客户端时，把它的媒体连接也一起收掉。</summary>
    public void RemoveClient(string memberId, string reason = "离开了房间")
    {
        ClientLink? entry;
        lock (_gate)
        {
            if (!_links.TryGetValue(memberId, out entry)) return;
            _links.Remove(memberId);
            _participants.Remove(memberId);
        }

        try { entry.Link.Closed -= OnClientLinkClosed; } catch { }
        try { _ = entry.Queue.DisposeAsync().AsTask(); } catch { }
        try { entry.Link.Close(reason); } catch { }

        RaiseStateChanged();
        ParticipantLeft?.Invoke(memberId);
    }

    /// <summary>客户端上报麦克风 / 摄像头开关。</summary>
    private void OnClientControl(ChatLink link, CallControlInfo control)
    {
        var memberId = FindMemberId(link);
        if (memberId is null) return;

        var changed = false;
        lock (_gate)
        {
            if (_participants.TryGetValue(memberId, out var participant))
            {
                if (control.MicOn is { } mic && participant.MicOn != mic) { participant.MicOn = mic; changed = true; }
                if (control.CameraOn is { } camera && participant.CameraOn != camera) { participant.CameraOn = camera; changed = true; }
            }
        }

        if (changed) RaiseStateChanged();
    }

    /// <summary>收到一个客户端的媒体包：交给主机引擎，并转发给其余客户端。</summary>
    private void OnClientMedia(ChatLink source, string fromMemberId, CallMediaKind kind, byte[] payload, CallMediaInfo info)
    {
        var memberId = FindMemberId(source);
        if (memberId is null) return;

        // 主机自己的通话引擎（如果主机也在通话里）
        PacketReceived?.Invoke(memberId, kind, payload, info.TimestampMs);

        // 转发给其余所有客户端（不回首包）
        List<ClientLink> targets;
        lock (_gate) targets = _links.Values.Where(l => !ReferenceEquals(l.Link, source)).ToList();
        foreach (var target in targets)
            target.Queue.Enqueue(memberId, kind, payload, info.TimestampMs);
    }

    private void OnClientLinkClosed(ChatLink link, string? error)
    {
        var memberId = FindMemberId(link);
        if (memberId is null) return;
        RemoveClient(memberId, error is { Length: > 0 } ? $"通话断开（{error}）" : "挂了通话");
    }

    /// <summary>主机自己加入通话（引擎已经起来了之后调用）。</summary>
    public bool TryJoinHost(string memberId, string name, string emoji, bool micOn, bool cameraOn, out string error)
    {
        error = "";
        lock (_gate)
        {
            if (_disposed) { error = "房间已关闭"; return false; }
            if (_hostMemberId.Length == 0) _hostMemberId = memberId;
            if (_participants.ContainsKey(memberId)) { error = "已经在通话里了"; return false; }
            if (_participants.Count >= CallProtocolRules.MaxParticipants)
            {
                error = $"通话人数已满（最多 {CallProtocolRules.MaxParticipants} 人）";
                return false;
            }

            _participants[memberId] = new CallParticipantWire
            {
                MemberId = memberId,
                Name = name,
                Emoji = emoji,
                IsHost = true,
                MicOn = micOn,
                CameraOn = cameraOn,
                JoinedAtUtc = DateTime.UtcNow,
            };
            _startedAtUtc ??= DateTime.UtcNow;
        }

        RaiseStateChanged();
        return true;
    }

    /// <summary>主机自己的麦克风 / 摄像头开关变了。</summary>
    public void UpdateHostState(bool? micOn, bool? cameraOn)
    {
        var changed = false;
        lock (_gate)
        {
            if (_hostMemberId.Length > 0 && _participants.TryGetValue(_hostMemberId, out var participant))
            {
                if (micOn is { } mic && participant.MicOn != mic) { participant.MicOn = mic; changed = true; }
                if (cameraOn is { } camera && participant.CameraOn != camera) { participant.CameraOn = camera; changed = true; }
            }
        }

        if (changed) RaiseStateChanged();
    }

    /// <summary>主机自己挂断（其他客户端可以继续通话，主机继续当中转）。</summary>
    public void LeaveHost(string memberId)
    {
        var removed = false;
        lock (_gate) removed = _participants.Remove(memberId);
        if (!removed) return;
        RaiseStateChanged();
        ParticipantLeft?.Invoke(memberId);
    }

    /// <summary>把主机自己的媒体包发给所有客户端。</summary>
    public void BroadcastHostMedia(CallMediaKind kind, ReadOnlySpan<byte> payload, long timestampMs)
    {
        var sender = _hostMemberId;
        if (sender.Length == 0) return;

        List<ClientLink> targets;
        lock (_gate) targets = _links.Values.ToList();
        foreach (var target in targets)
            target.Queue.Enqueue(sender, kind, payload, timestampMs);
    }

    /// <summary>当前通话快照（广播 <c>callState</c> 用；已按加入时间排序）。</summary>
    public CallStateInfo Snapshot()
    {
        lock (_gate)
        {
            var participants = _participants.Values
                .OrderBy(p => p.JoinedAtUtc)
                .Select(p => p.Clone())
                .ToList();
            return new CallStateInfo
            {
                Active = participants.Count > 0,
                StartedAtUtc = participants.Count > 0 ? _startedAtUtc ?? DateTime.UtcNow : null,
                Participants = participants,
            };
        }
    }

    /// <summary>房间关闭：所有媒体连接一起收掉（不再逐个广播状态，房间都没了）。</summary>
    public void ReleaseAll()
    {
        List<ClientLink> entries;
        lock (_gate)
        {
            entries = _links.Values.ToList();
            _links.Clear();
            _participants.Clear();
            _startedAtUtc = null;
        }

        foreach (var entry in entries)
        {
            try { entry.Link.Closed -= OnClientLinkClosed; } catch { }
            try { _ = entry.Queue.DisposeAsync().AsTask(); } catch { }
            try { entry.Link.Close("房间关闭"); } catch { }
        }
    }

    private string? FindMemberId(ChatLink link)
    {
        lock (_gate)
        {
            foreach (var pair in _links)
                if (ReferenceEquals(pair.Value.Link, link)) return pair.Key;
        }
        return null;
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); } catch { /* 广播失败不影响中转 */ }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            ReleaseAll();
        }
        finally { }
        return ValueTask.CompletedTask;
    }
}
