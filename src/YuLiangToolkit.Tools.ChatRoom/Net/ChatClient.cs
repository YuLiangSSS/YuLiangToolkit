using System.Net;
using System.Net.Sockets;
using YuLiangToolkit.Tools.ChatRoom.Protocol;
using YuLiangToolkit.Tools.ChatRoom.Traffic;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 客户端：填「主机地址（IPv4 / IPv6 / 域名）+ 端口」连过去。
///
/// <para><b>内网穿透也能用</b>：这里不做任何“局域网专属”的事 —— 域名会走 DNS、
/// IPv6 地址能直连，所以穿透工具（frp / ngrok / 花生壳…）给出的
/// <c>域名:端口</c> 或 <c>公网IP:端口</c> 填进来就行。</para>
/// </summary>
public sealed class ChatClient : IAsyncDisposable
{
    private ChatLink? _link;
    private TaskCompletionSource<WelcomeInfo>? _welcome;
    private IPEndPoint? _serverEndPoint;
    private string _callToken = "";

    public ChatClient(ChatRoomConnection connection)
    {
        Connection = connection;
    }

    /// <summary>客户端的会话（成员名单、流量计量、落盘都在这里面）。</summary>
    public ChatRoomConnection Connection { get; }

    /// <summary>连上了没有（握手完成才算）。</summary>
    public bool IsConnected => _link is { IsOpen: true } && HandshakeDone;

    /// <summary>握手完成（收到 Welcome）。</summary>
    public bool HandshakeDone { get; private set; }

    /// <summary>主机告诉我们的地址文本（界面显示）。</summary>
    public string ServerText { get; private set; } = "";

    /// <summary>连接用时（毫秒），界面上显示一下“延迟大概多少”。</summary>
    public int ConnectLatencyMs { get; private set; }

    /// <summary>主机的主机名（Welcome 里带回来的）。</summary>
    public string HostName { get; private set; } = "";

    /// <summary>状态提示。</summary>
    public event Action<ChatNotice>? Noticed;

    /// <summary>断了（参数是原因；空 = 正常退出）。</summary>
    public event Action<string?>? Disconnected;

    /// <summary>成员名单变了。</summary>
    public event Action<IReadOnlyList<ChatMember>>? MemberListChanged;

    /// <summary>被主机移出房间。</summary>
    public event Action<string>? Kicked;

    /// <summary>收到通话状态（参与者名单 / 麦克风 / 摄像头开关）。</summary>
    public event Action<CallStateInfo>? CallStateReceived;

    /// <summary>主机发给本端的通话令牌（为空 = 对方版本不支持通话）。</summary>
    public string CallToken => _callToken;

    /// <summary>
    /// 连接主机（握手完成才返回成功）。<paramref name="addressText"/> 可以带端口
    /// （<c>192.168.1.5:5200</c> / <c>[fe80::1]:5200</c>），端口参数填 0 时用它带的那个。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回原因。</returns>
    public async Task<string> ConnectAsync(string addressText, int port, CancellationToken ct = default)
    {
        if (_link is not null) return "已经连着了，先断开再连";

        var (ok, host, portFromText, error) = ChatProtocol.ParseEndpoint(addressText);
        if (!ok) return error;
        if (portFromText > 0) port = portFromText;
        if (port is <= 0 or > 65535) return "请填写端口（1~65535）";

        var (resolved, endPoint, display, resError) = await ChatProtocol.ResolveAsync(host, port, ct).ConfigureAwait(false);
        if (!resolved || endPoint is not IPEndPoint ipEnd) return resError;

        var tcp = new TcpClient(ipEnd.AddressFamily);
        var started = Environment.TickCount64;
        try
        {
            var connectTask = tcp.ConnectAsync(ipEnd, ct).AsTask();
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(12), ct)).ConfigureAwait(false);
            if (completed != connectTask)
            {
                try { tcp.Close(); } catch { }
                return $"连接 {display} 超时（12 秒没连上）：检查地址 / 端口 / 防火墙，内网穿透的话看映射还在不在";
            }
            await connectTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try { tcp.Close(); } catch { }
            return $"连不上 {display}：{ChatLink.Describe(ex)}";
        }

        ConnectLatencyMs = (int)(Environment.TickCount64 - started);
        ServerText = display;
        _serverEndPoint = ipEnd;

        var link = ChatLink.From(tcp, Connection.Meter);
        _link = link;
        _welcome = new TaskCompletionSource<WelcomeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);

        link.RawTransferReceived += (l, info, token) => Connection.ReceiveFileAsync(l, info, token);
        link.HelloReceived += (l, hello) =>
        {
            if (!string.Equals(hello.Magic, ChatProtocol.Magic, StringComparison.Ordinal))
            {
                l.Close("对方不是本程序的聊天室（magic 不匹配）");
                return;
            }
            if (hello.ProtocolVersion != ChatProtocol.Version)
            {
                l.Close($"协议版本不一致（主机 v{hello.ProtocolVersion}，本机 v{ChatProtocol.Version}）");
                return;
            }
            HostName = hello.Name;
            l.Peer = new ChatMember
            {
                MemberId = hello.MemberId,
                Name = hello.Name,
                Emoji = hello.Emoji,
                Role = "Host",
                Endpoint = display,
            };
        };
        link.TextReceived += (l, info, text) => Connection.HandleIncomingText(l, info, text);
        link.VoiceReceived += (l, info, bytes) => Connection.HandleIncomingVoice(l, info, bytes);
        link.SystemReceived += (l, info) => Connection.HandleIncomingSystem(l, info);
        link.MembersReceived += (_, list) =>
        {
            Connection.Members.Clear();
            if (list.Members.Count > 0) Connection.Members.AddRange(list.Members);
            else
            {
                Connection.Members.Add(Connection.Self.Clone());
                if (link.Peer is not null) Connection.Members.Insert(0, link.Peer.Clone());
            }
            MemberListChanged?.Invoke(Connection.Members.ToList());
            Connection.RaiseMembersChanged();
        };
        link.KickReceived += (_, kick) =>
        {
            HandshakeDone = false;
            Kicked?.Invoke(kick.Reason.Length > 0 ? kick.Reason : "被主机移出房间");
        };
        link.LatencyUpdated += (_, ms) => LatencyMs = ms;
        link.WelcomeReceived += (_, welcome) => _welcome?.TrySetResult(welcome);
        link.CallStateReceived += (_, state) => CallStateReceived?.Invoke(state);
        link.Closed += (l, err) =>
        {
            if (!ReferenceEquals(_link, l)) return;    // 旧连接的余波，忽略
            if (!HandshakeDone && _welcome is { Task.IsCompleted: false })
                _welcome.TrySetException(new IOException(err ?? "对方关闭了连接"));
            HandshakeDone = false;
            Disconnected?.Invoke(err);
        };

        try
        {
            // 客户端先报家门
            var hello = new HelloInfo
            {
                Role = "Client",
                MemberId = Connection.LocalMemberId,
                Name = Connection.LocalName,
                Emoji = Connection.LocalEmoji,
                AppVersion = Connection.Info.AppVersion,
            };
            await link.SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Hello, Connection.LocalMemberId, hello), ct)
                .ConfigureAwait(false);

            var completed = await Task.WhenAny(_welcome.Task, Task.Delay(TimeSpan.FromSeconds(12), ct)).ConfigureAwait(false);
            if (completed != _welcome.Task)
            {
                await CloseAsync("握手超时", null).ConfigureAwait(false);
                return "连上了但主机没有回应握手（对方可能不是这个聊天室，或者被防火墙拦了包）";
            }

            var welcome = await _welcome.Task.ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(welcome.MemberId)) Connection.AdoptHostMemberId(welcome.MemberId);
            if (!string.IsNullOrWhiteSpace(welcome.HostName)) HostName = welcome.HostName;
            _callToken = welcome.CallToken ?? "";

            Connection.Members.Clear();
            if (welcome.Members.Count > 0) Connection.Members.AddRange(welcome.Members);
            if (Connection.Members.All(m => m.MemberId != Connection.LocalMemberId))
                Connection.Members.Add(Connection.Self.Clone());
            if (link.Peer is not null && Connection.Members.All(m => m.MemberId != link.Peer.MemberId))
                Connection.Members.Insert(0, link.Peer.Clone());

            HandshakeDone = true;
            MemberListChanged?.Invoke(Connection.Members.ToList());
            Connection.RaiseMembersChanged();
            Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Info,
                $"已连上主机 {HostName}（{display}，握手 {ConnectLatencyMs} ms）"));
            return "";
        }
        catch (Exception ex)
        {
            await CloseAsync("", null).ConfigureAwait(false);
            return $"握手失败：{ChatLink.Describe(ex)}";
        }
    }

    /// <summary>最近的握手往返延迟（毫秒）。</summary>
    public int LatencyMs { get; private set; }

    /// <summary>发一条文字（自己发的那份也会落盘 + 显示）。</summary>
    public Task SendTextAsync(string text)
    {
        var link = _link;
        if (link is null || !HandshakeDone) return Task.CompletedTask;
        return Connection.SendLocalTextAsync(text, (info, body) => link.SendTextAsync(info, body, CancellationToken.None));
    }

    /// <summary>发一条语音消息（自己发的那份也会落盘 + 显示气泡）。</summary>
    public Task SendVoiceAsync(string mp3Path, int durationSeconds)
    {
        var link = _link;
        if (link is null || !HandshakeDone) return Task.CompletedTask;
        return Connection.SendLocalVoiceAsync(mp3Path, durationSeconds,
            (info, bytes) => link.SendVoiceAsync(info, bytes, CancellationToken.None));
    }

    /// <summary>发一个文件给主机（主机收到后不再转发给别的客户端 —— 见 docs/dev/pitfalls.md 的说明）。</summary>
    public async Task SendFileAsync(string path, string note = "")
    {
        var link = _link;
        if (link is null || !HandshakeDone)
        {
            Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Warn, "还没连上主机"));
            return;
        }
        await Connection.SendFileToAsync(link, path, note).ConfigureAwait(false);
    }

    /// <summary>发心跳（界面上的“测延迟”按钮）。</summary>
    public Task PingAsync() => _link?.SendPingAsync(CancellationToken.None) ?? Task.CompletedTask;

    /// <summary>
    /// 建立**通话媒体连接**：连主机同一个监听端口，用 <c>callHello</c> + 通话令牌握手。
    /// <para>媒体连接跟聊天连接分开，原因见 <see cref="ChatLink"/> 的说明：文件传输会长时间占住写锁，
    /// 混在一条连接上会把声音卡成 PPT。</para>
    /// </summary>
    /// <returns>(媒体连接, 失败原因)。成功时调用方负责在挂断时关掉这条连接。</returns>
    public async Task<(ChatLink? Link, string Error)> OpenMediaLinkAsync(CancellationToken ct = default)
    {
        if (_link is null || !HandshakeDone) return (null, "还没连上主机");
        if (_serverEndPoint is null) return (null, "不知道主机地址（断开重连一次再试）");
        if (_callToken.Length == 0)
            return (null, "主机没有开启通话支持（对方版本可能太旧）：请两边都用新版本后重连");

        TcpClient tcp;
        try
        {
            tcp = new TcpClient(_serverEndPoint.AddressFamily);
            var connectTask = tcp.ConnectAsync(_serverEndPoint, ct).AsTask();
            var completed = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            if (completed != connectTask)
            {
                try { tcp.Close(); } catch { }
                return (null, "建立通话连接超时（10 秒没连上主机）");
            }
            await connectTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return (null, $"建立通话连接失败：{ChatLink.Describe(ex)}");
        }

        var link = ChatLink.From(tcp, new TrafficMeter(0, TrafficQuotaScope.Off));
        var accepted = new TaskCompletionSource<WelcomeInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.WelcomeReceived += (_, welcome) => accepted.TrySetResult(welcome);
        link.Closed += (_, err) =>
        {
            if (!accepted.Task.IsCompleted)
                accepted.TrySetException(new IOException(err ?? "通话连接被主机关闭"));
        };

        try
        {
            await link.SendCallHelloAsync(new HelloInfo
            {
                Magic = ChatProtocol.Magic,
                ProtocolVersion = ChatProtocol.Version,
                Role = "Media",
                IsMedia = true,
                MemberId = Connection.LocalMemberId,
                CallToken = _callToken,
                Name = Connection.LocalName,
                Emoji = Connection.LocalEmoji,
                AppVersion = Connection.Info.AppVersion,
            }, ct).ConfigureAwait(false);

            var completed = await Task.WhenAny(accepted.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            if (completed != accepted.Task)
            {
                link.Close("通话握手超时");
                return (null, "主机没有回通话握手（对方版本可能太旧 / 拒绝了这个连接）");
            }

            var welcome = await accepted.Task.ConfigureAwait(false);
            if (!welcome.MediaAccepted)
            {
                link.Close("通话被拒绝");
                return (null, welcome.MediaRejectReason is { Length: > 0 } reason ? reason : "主机拒绝了这个通话连接");
            }

            return (link, "");
        }
        catch (Exception ex)
        {
            link.Close();
            return (null, $"通话握手失败：{ChatLink.Describe(ex)}");
        }
    }

    /// <summary>断开（会先礼貌地说一声再见）。<paramref name="error"/> 非空时会触发 <see cref="Disconnected"/>。</summary>
    public async Task CloseAsync(string reason = "本端主动断开", string? error = null)
    {
        var link = _link;
        _link = null;
        HandshakeDone = false;
        if (link is null) return;

        try { await link.SendByeAsync(reason).ConfigureAwait(false); } catch { }
        link.Close(error);
        try { await link.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}
