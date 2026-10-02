using YuLiangToolkit.Core.Storage;
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

    /// <summary>对端地址文本。</summary>
    public string PeerText => _client?.ServerText ?? "";

    /// <summary>主机名（客户端知道对方主机叫什么）。</summary>
    public string HostNameText => _client?.HostName ?? "";

    /// <summary>已连上的客户端数量（主机端有意义）。</summary>
    public int ClientCount => _host?.ClientCount ?? 0;

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
