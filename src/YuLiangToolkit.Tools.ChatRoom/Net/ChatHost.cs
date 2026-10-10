using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using YuLiangToolkit.Tools.ChatRoom.Protocol;

namespace YuLiangToolkit.Tools.ChatRoom.Net;

/// <summary>
/// 主机端：开一个 TCP 端口等人连进来，把大家的消息互相转发。
///
/// <para><b>谁负责转发</b>：客户端之间不直连（局域网还好，内网穿透下根本连不上对方），
/// 所以一切消息都走主机中转：客户端发 → 主机（自己也显示一份）→ 其余客户端。
/// 主机自己发的消息同样只写一遍记录、广播给所有客户端。</para>
///
/// <para>主机同时可以监听的地址：默认 <c>0.0.0.0</c>（IPv4 所有网卡）或 <c>[::]</c>（含 IPv6），
/// 也可以只绑某一张网卡。内网穿透工具要求“映射本机端口”时，绑 <c>0.0.0.0</c> 最省事。</para>
/// </summary>
public sealed class ChatHost : IDisposable
{
    private readonly ConcurrentDictionary<string, ClientSession> _clients = new();
    private readonly SemaphoreSlim _helloGate = new(1, 1);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _nextClientNumber;
    private bool _disposed;

    public ChatHost(ChatRoomConnection connection)
    {
        Connection = connection;
    }

    /// <summary>主机端的会话（成员名单、流量计量、落盘都在这里面）。</summary>
    public ChatRoomConnection Connection { get; }

    /// <summary>监听中。</summary>
    public bool IsListening { get; private set; }

    /// <summary>实际监听的端点（端口填 0 自动挑时，这里就是真实端口）。</summary>
    public IPEndPoint? EndPoint { get; private set; }

    /// <summary>
    /// 这个监听是不是双栈的（同一个端口 IPv4 与 IPv6 都收）。
    /// 傻瓜模式绑 <c>::</c> 时是 true —— 界面据此告诉用户「IPv4/IPv6 都能连」。
    /// </summary>
    public bool DualStack { get; private set; }

    /// <summary>已连上的客户端数量。</summary>
    public int ClientCount => _clients.Count;

    /// <summary>客户端加入 / 离开时通知界面（参数是当前成员名单）。</summary>
    public event Action<IReadOnlyList<ChatMember>>? MemberListChanged;

    /// <summary>状态提示（界面状态栏）。</summary>
    public event Action<ChatNotice>? Noticed;

    public sealed class ClientSession
    {
        public required string MemberId { get; init; }
        public required ChatLink Link { get; init; }
        public required ChatMember Member { get; init; }
    }

    /// <summary>所有客户端连接（广播用）。</summary>
    public IReadOnlyList<ChatLink> Links => _clients.Values.Select(c => c.Link).ToList();

    /// <summary>当前成员名单（含主机自己）。</summary>
    public IReadOnlyList<ChatMember> Members => Connection.Members.ToList();

    /// <summary>
    /// 开始监听。<paramref name="ipText"/> 支持 <c>0.0.0.0</c>、<c>::</c>、<c>[::]</c>、具体网卡地址、主机名。
    /// </summary>
    /// <returns>成功返回空字符串，失败返回原因（界面直接显示）。</returns>
    public async Task<string> StartAsync(string ipText, int port, CancellationToken ct = default)
    {
        if (IsListening) return "";
        if (_disposed) return "已释放，不能再用";

        var (ok, host, _, error) = ChatProtocol.ParseEndpoint(ipText.Trim().Length == 0 ? "0.0.0.0" : ipText);
        if (!ok) return error;
        // ParseEndpoint 会把 "192.168.1.5:5200" 这种拆开；主机这里只接受纯地址，端口用参数
        host = host.Trim('[', ']');
        if (host.Length == 0) host = "0.0.0.0";

        IPAddress address;
        if (!IPAddress.TryParse(host, out var parsed))
        {
            // 允许填主机名（例如 localhost 或本机计算机名）
            var (resolved, endPoint, _, resErr) = await ChatProtocol.ResolveAsync(host, port <= 0 ? 1 : port, ct).ConfigureAwait(false);
            if (!resolved || endPoint is not IPEndPoint ipEnd || ipEnd.Address is null)
                return resErr.Length > 0 ? resErr : $"监听地址 {host} 解析失败";
            address = ipEnd.Address;
        }
        else address = parsed;

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
            return "本机不支持 IPv6，请改用 IPv4 地址（0.0.0.0）";

        port = port is >= 1 and <= 65535 ? port : 0;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(address, port);
            // ⚠ 双栈必须显式打开：Windows 上 IPv6 通配地址默认「只收 IPv6」（IPV6_V6ONLY=1），
            // 不设这一行，绑 "::" 之后 IPv4 的客户端**一个都连不进来**，而界面上的按钮还写着
            // 「同时收 IPv4 与 IPv6 的连接」——实测确认过（自检里有回归断言）。
            if (address.Equals(IPAddress.IPv6Any)) _listener.Server.DualMode = true;
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            EndPoint = (IPEndPoint)_listener.LocalEndpoint;
            DualStack = _listener.Server.DualMode;
            IsListening = true;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Info,
                $"正在监听 {ChatProtocol.FormatEndpoint(EndPoint.Address, EndPoint.Port)}"
                + $"（{(DualStack ? "双栈：IPv4 + IPv6" : address.AddressFamily == AddressFamily.InterNetworkV6 ? "仅 IPv6" : "仅 IPv4")}）"));
            return "";
        }
        catch (SocketException ex)
        {
            IsListening = false;
            Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Error, $"开不了监听：{ChatLink.Describe(ex)}"));
            return ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"端口 {port} 已经被别的程序占用了，换一个（常见的 5200 / 5201）"
                : ChatLink.Describe(ex);
        }
        catch (Exception ex)
        {
            IsListening = false;
            return ChatLink.Describe(ex);
        }
    }

    /// <summary>停止监听并断开所有客户端。</summary>
    public async Task StopAsync(string reason = "主机关闭了房间")
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        IsListening = false;

        foreach (var client in _clients.Values)
        {
            try { await client.Link.SendByeAsync(reason).ConfigureAwait(false); } catch { }
            client.Link.Close(reason);
        }
        _clients.Clear();
        Connection.Members.RemoveAll(m => !m.IsHost);

        if (_acceptLoop is not null)
        {
            try { await Task.WhenAny(_acceptLoop, Task.Delay(1500)).ConfigureAwait(false); } catch { }
            _acceptLoop = null;
        }
        cts?.Dispose();
        EndPoint = null;
        DualStack = false;
        Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Info, "已停止监听"));
        MemberListChanged?.Invoke(Members);
    }

    // ---------------------------------------------------------------- 接受连接

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }
            catch (Exception) { continue; }

            _ = Task.Run(() => HandleClientAsync(tcp, ct), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, CancellationToken ct)
    {
        var number = Interlocked.Increment(ref _nextClientNumber);
        var link = ChatLink.From(tcp, Connection.Meter);
        link.RawTransferReceived += (l, info, token) => Connection.ReceiveFileAsync(l, info, token);
        var memberId = $"c{number}-{Guid.NewGuid():N}"[..10];

        var session = new ClientSession
        {
            MemberId = memberId,
            Link = link,
            Member = new ChatMember
            {
                MemberId = memberId,
                Name = $"访客{number}",
                Emoji = "🙂",
                Role = "Client",
                Endpoint = tcp.Client.RemoteEndPoint?.ToString() ?? "",
                JoinedAt = DateTime.Now,
            },
        };

        var welcomed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.HelloReceived += (l, hello) =>
        {
            if (!string.Equals(hello.Magic, ChatProtocol.Magic, StringComparison.Ordinal))
            {
                Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Warn, $"有一个连接不是本程序的聊天室（magic 不对），已拒绝：{session.Member.Endpoint}"));
                l.Close("协议不匹配");
                return;
            }
            if (hello.ProtocolVersion != ChatProtocol.Version)
            {
                l.Close($"协议版本不一致（对方 v{hello.ProtocolVersion}，本机 v{ChatProtocol.Version}）");
                return;
            }
            session.Member.Name = string.IsNullOrWhiteSpace(hello.Name) ? session.Member.Name : hello.Name.Trim();
            session.Member.Emoji = string.IsNullOrWhiteSpace(hello.Emoji) ? "🙂" : hello.Emoji.Trim();
            l.Peer = session.Member;
            welcomed.TrySetResult(true);
        };
        link.TransferResultReceived += (_, _) => { };                 // 回执由 ChatLink 内部唤醒等待者
        link.TextReceived += (l, info, text) =>
        {
            Connection.HandleIncomingText(l, info, text);
            // 转发给其余客户端（这个回调本身就在读循环里，不 await 也无所谓：
            // 注意不要 await —— await 会让这一条连接的读循环等在所有客户端写完之后）
            _ = Broadcast(new ChatFrameHeader
            {
                Kind = ChatProtocol.Kinds.Text,
                From = session.MemberId,
                Frame = ChatFrameHeader.NewFrameId(),
            }.WithExtra(info), System.Text.Encoding.UTF8.GetBytes(text), except: l);
        };
        link.SystemReceived += (l, info) =>
        {
            Connection.HandleIncomingSystem(l, info);
        };
        link.Closed += (l, err) => OnClientClosed(session, err);

        try
        {
            // 主机先自报家门（客户端要知道主机是谁、协议对不对得上）
            var hello = new HelloInfo
            {
                Role = "Host",
                MemberId = Connection.LocalMemberId,
                Name = Connection.LocalName,
                Emoji = Connection.LocalEmoji,
                AppVersion = Connection.Info.AppVersion,
                ListenPort = EndPoint?.Port ?? 0,
            };
            await link.SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Hello, Connection.LocalMemberId, hello), ct)
                .ConfigureAwait(false);

            // 等客户端的 hello（10 秒不报家门就当不是本程序，踢掉）
            var completed = await Task.WhenAny(welcomed.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            if (completed != welcomed.Task)
            {
                link.Close("握手超时");
                return;
            }

            await _helloGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _clients[session.MemberId] = session;
                Connection.Members.RemoveAll(m => m.MemberId == session.MemberId);
                Connection.Members.Add(session.Member);

                var welcome = new WelcomeInfo
                {
                    MemberId = session.MemberId,
                    SessionName = Connection.SessionName,
                    HostName = Connection.LocalName,
                    Members = Connection.Members.Select(m => m.Clone()).ToList(),
                };
                await link.SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Welcome, Connection.LocalMemberId, welcome), ct)
                    .ConfigureAwait(false);
            }
            finally { _helloGate.Release(); }

            var joinText = $"{session.Member.Emoji} {session.Member.Name} 加入了房间（{session.Member.Endpoint}）";
            Connection.PostLocalSystem(joinText);
            _ = BroadcastSystem(new SystemInfo
            {
                MessageId = Guid.NewGuid().ToString("N")[..12],
                Text = joinText,
                SentLocal = DateTime.Now,
                Member = session.Member.Clone(),
            }, except: link);
            await BroadcastMembersAsync(link).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Warn, $"一个连接握手失败：{ChatLink.Describe(ex)}"));
            link.Close(ChatLink.Describe(ex));
        }
    }

    private void OnClientClosed(ClientSession session, string? error)
    {
        if (!_clients.TryRemove(session.MemberId, out _)) return;
        Connection.Members.RemoveAll(m => m.MemberId == session.MemberId);

        var leaveText = error is { Length: > 0 }
            ? $"{session.Member.Emoji} {session.Member.Name} 断开了连接（{error}）"
            : $"{session.Member.Emoji} {session.Member.Name} 离开了房间";
        Connection.PostLocalSystem(leaveText);
        BroadcastSystem(new SystemInfo
        {
            MessageId = Guid.NewGuid().ToString("N")[..12],
            Text = leaveText,
            SentLocal = DateTime.Now,
            Member = session.Member.Clone(),
        });
        _ = BroadcastMembersAsync(null);
    }

    // ---------------------------------------------------------------- 广播

    /// <summary>主机自己发一条文字：本端记录一遍，再广播给所有客户端。</summary>
    public Task SendTextAsync(string text)
        => Connection.SendLocalTextAsync(text, (info, body) =>
            Broadcast(new ChatFrameHeader
            {
                Kind = ChatProtocol.Kinds.Text,
                From = Connection.LocalMemberId,
                Frame = ChatFrameHeader.NewFrameId(),
            }.WithExtra(info), System.Text.Encoding.UTF8.GetBytes(body)));

    /// <summary>主机发一个文件给所有客户端（串行，一个接一个）。</summary>
    public async Task SendFileAsync(string path, string note = "")
    {
        var links = Links;
        if (links.Count == 0)
        {
            Noticed?.Invoke(new ChatNotice(ChatNoticeLevel.Warn, "现在还没有客户端连进来"));
            return;
        }
        foreach (var link in links)
            await Connection.SendFileToAsync(link, path, note).ConfigureAwait(false);
    }

    /// <summary>把某个客户端移出房间。</summary>
    public async Task KickAsync(string memberId, string reason = "被主机移出房间")
    {
        if (!_clients.TryGetValue(memberId, out var session)) return;
        try
        {
            await session.Link.SendAsync(ChatFrameHeader.New(ChatProtocol.Kinds.Kick, Connection.LocalMemberId,
                new KickInfo { MemberId = memberId, Reason = reason })).ConfigureAwait(false);
        }
        catch { }
        session.Link.Close(reason);
    }

    /// <summary>把所有客户端移出房间。</summary>
    public async Task KickAllAsync(string reason = "主机清空了房间")
    {
        foreach (var id in _clients.Keys.ToList())
            await KickAsync(id, reason).ConfigureAwait(false);
    }

    private Task BroadcastSystem(SystemInfo info, ChatLink? except = null)
        => Broadcast(new ChatFrameHeader
        {
            Kind = ChatProtocol.Kinds.System,
            From = Connection.LocalMemberId,
            Frame = ChatFrameHeader.NewFrameId(),
        }.WithExtra(info), default, except);

    private async Task BroadcastMembersAsync(ChatLink? except)
    {
        var list = new MemberListInfo { Members = Connection.Members.Select(m => m.Clone()).ToList() };
        MemberListChanged?.Invoke(list.Members);
        Connection.RaiseMembersChanged();
        await Broadcast(new ChatFrameHeader
        {
            Kind = ChatProtocol.Kinds.Members,
            From = Connection.LocalMemberId,
            Frame = ChatFrameHeader.NewFrameId(),
        }.WithExtra(list), default, except).ConfigureAwait(false);
    }

    /// <summary>给所有客户端发一个包（<paramref name="except"/> 排除发起者自己）。</summary>
    private async Task Broadcast(ChatFrameHeader header, ReadOnlyMemory<byte> payload, ChatLink? except = null)
    {
        foreach (var client in _clients.Values.ToList())
        {
            if (except is not null && ReferenceEquals(client.Link, except)) continue;
            try
            {
                await client.Link.SendAsync(new ChatFrameHeader
                {
                    Kind = header.Kind,
                    From = header.From,
                    Frame = header.Frame,
                    ReplyTo = header.ReplyTo,
                    Extra = header.Extra,
                }, CancellationToken.None, payload).ConfigureAwait(false);
            }
            catch { /* 单个客户端发失败不影响其它人 */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        foreach (var client in _clients.Values)
        {
            try { client.Link.Close("主机退出"); } catch { }
        }
        _clients.Clear();
        _helloGate.Dispose();
    }
}
