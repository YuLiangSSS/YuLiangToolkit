using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ChatRoom.Call;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Sessions;
using YuLiangToolkit.Tools.ChatRoom.Traffic;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 一次完整的聊天会话（主机端与客户端共用同一个类，内部按需要建 <see cref="ChatHost"/> 或 <see cref="ChatClient"/>）。
///
/// <para><b>生命周期</b>：构造 → <see cref="StartHostAsync"/> 或 <see cref="ConnectAsync"/>
/// （这一步会新建本次的会话文件夹）→ 聊天 → <see cref="StopAsync"/> 关闭并补写会话统计。</para>
/// </summary>
public sealed class ChatRoomSession : ChatRoomConnection, IAsyncDisposable
{
    private readonly ChatSessionStore _store;
    private ChatHost? _host;
    private ChatClient? _client;
    private int _started;

    // ---------------- 通话 ----------------
    private CallEngine? _callEngine;
    private ICallTransport? _callTransport;
    private ChatLink? _callMediaLink;
    private int _callStarting;
    private bool _callLeaving;
    private readonly object _callStateGate = new();
    private readonly HashSet<string> _callParticipantIds = new(StringComparer.Ordinal);

    public ChatRoomSession(string toolId, string nickname, string emoji, TrafficMeter meter, ChatSessionStore store)
        : base(toolId, nickname, emoji, meter, store)
    {
        _store = store;
    }

    /// <summary>本端是不是主机。</summary>
    public bool IsHost { get; private set; }

    /// <summary>主机对象（客户端时是 null）。</summary>
    public ChatHost? Host => _host;

    /// <summary>客户端对象（主机时是 null）。</summary>
    public ChatClient? Client => _client;

    /// <summary>是否正在联机（主机 = 在监听；客户端 = 握手完成）。</summary>
    public bool IsOnline => _host?.IsListening == true || _client?.IsConnected == true;

    /// <summary>本端最近一次握手延迟（客户端）；主机端恒为 0。</summary>
    public int LatencyMs => _client?.LatencyMs ?? 0;

    /// <summary>主机实际监听的地址（填 0 端口时这里是真实端口）。</summary>
    public string ListenText => _host?.EndPoint is { } ep ? ChatProtocol.FormatEndpoint(ep.Address, ep.Port) : "";

    /// <summary>主机这次监听的端口（没开房间时是 0）。</summary>
    public int ListenPort => _host?.EndPoint?.Port ?? 0;

    /// <summary>这次监听是不是双栈（IPv4 与 IPv6 都能连）。傻瓜模式绑 <c>::</c> 时为 true。</summary>
    public bool IsDualStack => _host?.DualStack == true;

    /// <summary>对端地址文本。</summary>
    public string PeerText => _client?.ServerText ?? "";

    /// <summary>主机名（客户端知道对方主机叫什么）。</summary>
    public string HostNameText => _client?.HostName ?? "";

    /// <summary>已连上的客户端数量（主机端有意义）。</summary>
    public int ClientCount => _host?.ClientCount ?? 0;

    // ---------------------------------------------------------------- 通话（群语音 / 群视频）

    /// <summary>现在在不在通话里。</summary>
    public bool IsInCall => _callEngine?.IsRunning == true;

    /// <summary>通话引擎（在通话里才非空；界面用它开关麦克风 / 摄像头）。</summary>
    public CallEngine? ActiveCallEngine => _callEngine;

    /// <summary>最近一次通话状态（主机权威，客户端跟随）。</summary>
    public CallStateInfo CurrentCallState { get; private set; } = new();

    /// <summary>通话参与者 / 麦克风 / 摄像头状态变了。</summary>
    public event Action<CallStateInfo>? CallStateChanged;

    /// <summary>本机摄像头预览帧（BGRA 原始字节）。</summary>
    public event Action<CallRawFrame>? CallLocalVideoFrame;

    /// <summary>收到的远端视频帧（JPEG 原始字节）。</summary>
    public event Action<CallRawFrame>? CallRemoteVideoFrame;

    /// <summary>通话统计（约一秒一次）。</summary>
    public event Action<CallStats>? CallStatsChanged;

    /// <summary>通话出错 / 意外断开（参数是给用户看的原因）。</summary>
    public event Action<string>? CallFailed;

    /// <summary>
    /// 加入本房间的群通话（主机 / 客户端都能发起，谁先点谁先入会）。
    /// <para>客户端会另开一条媒体连接到主机同一个端口；主机自己不需要媒体连接。
    /// 返回失败原因时不会有任何设备 / 连接残留在跑。</para>
    /// </summary>
    public async Task<(bool Ok, string Error)> JoinCallAsync(CallSettings settings, ICallMediaFactory mediaFactory)
    {
        if (Interlocked.Exchange(ref _callStarting, 1) == 1) return (false, "通话正在启动中，稍等一下");
        try
        {
            if (IsInCall) return (false, "已经在通话里了");
            if (!IsOnline) return (false, "还没连上房间：先开房间或连主机，再发起通话");
            if (settings is null) return (false, "通话设置为空");

            ICallTransport? transport = null;
            ChatLink? mediaLink = null;
            CallEngine? engine = null;

            try
            {
                if (_client is not null)
                {
                    var (link, openError) = await _client.OpenMediaLinkAsync().ConfigureAwait(false);
                    if (link is null) return (false, openError);
                    mediaLink = link;
                    transport = new ClientCallTransport(link, LocalMemberId);
                }
                else if (_host is not null)
                {
                    transport = new HostCallTransport(_host.Relay, LocalMemberId);
                }
                else
                {
                    return (false, "当前没有连接");
                }

                engine = new CallEngine(transport, mediaFactory, settings.Clone(), LocalMemberId);
                // ⚠ 必须把「收到的媒体包」接进引擎：ICallTransport.PacketReceived 不会自己找人，
                // 漏了这行就是「能发不能收」（两端统计 Received* 恒 0、听不到也看不到别人）。
                transport.PacketReceived += engine.HandlePacket;
                engine.LocalVideoFrame += frame => Raise(CallLocalVideoFrame, frame);
                engine.RemoteVideoFrame += frame => Raise(CallRemoteVideoFrame, frame);
                engine.StatsChanged += stats => Raise(CallStatsChanged, stats);
                engine.Failed += message =>
                {
                    Raise(CallFailed, message);
                    _ = LeaveCallAsync();
                };

                var (started, startError) = await engine.StartAsync().ConfigureAwait(false);
                if (!started)
                {
                    transport.PacketReceived -= engine.HandlePacket;
                    try { await engine.DisposeAsync().ConfigureAwait(false); } catch { }
                    try { if (mediaLink is not null) mediaLink.Close("通话启动失败"); } catch { }
                    return (false, startError.Length > 0 ? startError : "通话启动失败");
                }

                if (_host is not null)
                {
                    _host.Relay.SetHostMemberId(LocalMemberId);
                    if (!_host.Relay.TryJoinHost(LocalMemberId, LocalName, LocalEmoji, engine.MicOn, engine.CameraOn,
                            out var joinError))
                    {
                        try { _host.Relay.ParticipantLeft -= OnCallParticipantLeft; } catch { }
                        try { await engine.DisposeAsync().ConfigureAwait(false); } catch { }
                        return (false, joinError);
                    }
                    _host.Relay.ParticipantLeft += OnCallParticipantLeft;
                }
                else
                {
                    // 告诉主机本端实际的麦克风 / 摄像头状态（主机默认当成都开着）
                    transport.SendControl(engine.MicOn, engine.CameraOn);
                }

                _callEngine = engine;
                _callTransport = transport;
                _callMediaLink = mediaLink;
                if (mediaLink is not null) mediaLink.Closed += OnCallMediaLinkClosed;

                Notice(ChatNoticeLevel.Info,
                    $"{LocalEmoji} {LocalName} 加入了通话（{(engine.CameraOn ? "语音 + 视频" : "语音")}）");
                return (true, "");
            }
            catch (Exception ex)
            {
                try { if (_host is not null) _host.Relay.ParticipantLeft -= OnCallParticipantLeft; } catch { }
                try { if (engine is not null && transport is not null) transport.PacketReceived -= engine.HandlePacket; } catch { }
                try { if (engine is not null) await engine.DisposeAsync().ConfigureAwait(false); } catch { }
                try { if (mediaLink is not null) mediaLink.Close("通话启动失败"); } catch { }
                return (false, $"加入通话失败：{ex.Message}");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _callStarting, 0);
        }
    }

    /// <summary>挂断（可重复调用；客户端会关掉媒体连接，主机会把自己从参与者里去掉）。</summary>
    public async Task LeaveCallAsync()
    {
        if (_callLeaving) return;
        _callLeaving = true;
        try
        {
            var engine = _callEngine;
            var transport = _callTransport;
            var mediaLink = _callMediaLink;
            var host = _host;
            _callEngine = null;
            _callTransport = null;
            _callMediaLink = null;

            if (host is not null)
            {
                try { host.Relay.ParticipantLeft -= OnCallParticipantLeft; } catch { }
                try { host.Relay.LeaveHost(LocalMemberId); } catch { }
            }
            if (mediaLink is not null) mediaLink.Closed -= OnCallMediaLinkClosed;

            if (engine is not null && transport is not null)
            {
                try { transport.PacketReceived -= engine.HandlePacket; } catch { }
            }

            if (engine is not null)
            {
                try { await engine.DisposeAsync().ConfigureAwait(false); }
                catch { }
            }
            else
            {
                try { transport?.Dispose(); } catch { }
            }

            lock (_callStateGate) _callParticipantIds.Clear();
        }
        finally
        {
            _callLeaving = false;
        }
    }

    /// <summary>收到主机广播的通话状态（主机端也走同一个处理，保证两端逻辑一致）。</summary>
    private void ApplyCallState(CallStateInfo state)
    {
        CurrentCallState = state;
        List<string> removed = new();
        lock (_callStateGate)
        {
            var current = new HashSet<string>(state.Participants.Select(p => p.MemberId), StringComparer.Ordinal);
            foreach (var id in _callParticipantIds)
                if (!current.Contains(id)) removed.Add(id);
            _callParticipantIds.Clear();
            foreach (var id in current) _callParticipantIds.Add(id);
        }

        var engine = _callEngine;
        if (engine is not null)
            foreach (var id in removed) engine.RemoveParticipant(id);

        Raise(CallStateChanged, state);
    }

    private void OnCallParticipantLeft(string memberId)
    {
        try { _callEngine?.RemoveParticipant(memberId); } catch { }
    }

    private void OnCallMediaLinkClosed(ChatLink link, string? error)
    {
        if (_callLeaving) return;
        var reason = error is { Length: > 0 } ? $"通话连接断开（{error}）" : "通话连接已断开";
        Raise(CallFailed, reason);
        _ = LeaveCallAsync();
    }

    /// <summary>
    /// 安全触发事件：这些事件可能从网络读循环 / WASAPI 线程 / WinRT 线程上触发，
    /// 界面处理函数万一抛异常，不能把底层连接或采集线程一起带崩。
    /// </summary>
    private static void Raise<T>(Action<T>? handler, T value)
    {
        if (handler is null) return;
        try { handler(value); } catch { /* 订阅方自己的问题，不影响通话链路 */ }
    }

    // ---------------------------------------------------------------- 开 / 关

    /// <summary>
    /// 开一个房间（主机）：建会话文件夹 → 开始监听。
    /// </summary>
    public async Task<string> StartHostAsync(string listenAddress, int port, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";
        IsHost = true;
        InitSelf($"host-{Guid.NewGuid():N}"[..10], "Host");
        BeginSession("Host", "");

        _host = new ChatHost(this);
        _host.Noticed += RaiseNoticed;
        _host.CallStateChanged += state => ApplyCallState(state);
        _host.MemberListChanged += list =>
        {
            Members.Clear();
            Members.Add(Self.Clone());
            Members.AddRange(list.Where(m => m.MemberId != Self.MemberId));
            RaiseMembersChanged();
        };

        var error = await _host.StartAsync(listenAddress, port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            Notice(ChatNoticeLevel.Error, error);
            Interlocked.Exchange(ref _started, 0);
            IsHost = false;
            return error;
        }

        Notice(ChatNoticeLevel.Info,
            $"房间已开：监听 {ListenText}。把「{DetectAdvertisedAddress(listenAddress)}:{_host.EndPoint?.Port}」告诉对方。");
        return "";
    }

    /// <summary>
    /// 连主机（客户端）：建会话文件夹 → 连接 → 握手。
    /// </summary>
    public async Task<string> ConnectAsync(string addressText, int port, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return "这一次连接已经开始了";
        IsHost = false;
        InitSelf($"me-{Guid.NewGuid():N}"[..10], "Client");

        _client = new ChatClient(this);
        _client.Noticed += RaiseNoticed;
        _client.CallStateReceived += state => ApplyCallState(state);
        _client.MemberListChanged += list =>
        {
            Members.Clear();
            Members.AddRange(list);
            RaiseMembersChanged();
        };
        _client.Kicked += reason =>
        {
            Notice(ChatNoticeLevel.Warn, $"被主机移出房间：{reason}");
            _ = StopAsync($"被移出：{reason}");
        };

        var error = await _client.ConnectAsync(addressText, port, ct).ConfigureAwait(false);
        if (error.Length > 0)
        {
            Notice(ChatNoticeLevel.Error, error);
            Interlocked.Exchange(ref _started, 0);
            return error;
        }

        // 客户端的会话文件夹在拿到主机分配的成员 Id 之后再建（抬头里要记准）
        BeginSession("Client", _client.ServerText);
        Notice(ChatNoticeLevel.Info, $"已加入 {_client.HostName} 的房间（{_client.ServerText}）");
        return "";
    }

    /// <summary>断开 / 关房间，并补写会话统计。</summary>
    public async Task StopAsync(string reason = "本端停止")
    {
        if (Interlocked.Exchange(ref _started, 0) == 0 && SessionName.Length == 0) return;

        // 先把通话收掉（设备 / 媒体连接），再断聊天连接
        try { await LeaveCallAsync().ConfigureAwait(false); } catch { }

        if (_host is not null)
        {
            try { await _host.StopAsync(reason).ConfigureAwait(false); } catch { }
        }
        if (_client is not null)
        {
            try { await _client.CloseAsync(reason, null).ConfigureAwait(false); } catch { }
        }
        EndSession(null);
    }

    // ---------------------------------------------------------------- 发东西

    /// <summary>发文字（主机 = 广播给所有人，客户端 = 发给主机）。</summary>
    public async Task SendTextAsync(string text)
    {
        if (_host is not null) await _host.SendTextAsync(text).ConfigureAwait(false);
        else if (_client is not null) await _client.SendTextAsync(text).ConfigureAwait(false);
    }

    /// <summary>发文件（主机 = 挨个发给所有客户端，客户端 = 发给主机）。</summary>
    public async Task SendFileAsync(string path, string note = "")
    {
        if (_host is not null) await _host.SendFileAsync(path, note).ConfigureAwait(false);
        else if (_client is not null) await _client.SendFileAsync(path, note).ConfigureAwait(false);
    }

    /// <summary>把某个客户端移出房间（主机端）。</summary>
    public Task KickAsync(string memberId) => _host?.KickAsync(memberId) ?? Task.CompletedTask;

    /// <summary>清空房间（主机端把所有客户端请出去）。</summary>
    public Task KickAllAsync() => _host?.KickAllAsync() ?? Task.CompletedTask;

    /// <summary>测一次延迟（客户端）。</summary>
    public Task PingAsync() => _client?.PingAsync() ?? Task.CompletedTask;

    /// <summary>本机在局域网里的地址（告诉对方该填什么）。</summary>
    public static string DetectLanAddress() => ChatProtocol.DetectLanIPv4();

    /// <summary>本机所有可用地址（界面上的下拉/提示）。</summary>
    public static IReadOnlyList<string> LocalAddresses() => ChatProtocol.ListLocalAddresses();

    private static string DetectAdvertisedAddress(string listenAddress)
    {
        var text = (listenAddress ?? "").Trim();
        if (text.Length > 0 && text is not "0.0.0.0" and not "::" and not "[::]") return text.Trim('[', ']');
        var lan = ChatProtocol.DetectLanIPv4();
        return lan.Length > 0 ? lan : "（用 ipconfig 看一下本机 IPv4）";
    }

    public override async ValueTask DisposeAsync()
    {
        try { await LeaveCallAsync().ConfigureAwait(false); } catch { }
        try { await StopAsync("程序关闭").ConfigureAwait(false); } catch { }
        _host?.Dispose();
        if (_client is not null)
        {
            try { await _client.DisposeAsync().ConfigureAwait(false); } catch { }
        }
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
