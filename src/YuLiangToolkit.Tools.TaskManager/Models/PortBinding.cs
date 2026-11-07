namespace YuLiangToolkit.Tools.TaskManager.Models;

/// <summary>
/// 一条端口占用记录（来自 Windows IP Helper：<c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c>），
/// 已经按「所属进程 PID」归好组，所以界面上可以直接把同一个进程的端口列出来。
///
/// <para>TCP 有状态（LISTENING / ESTABLISHED …），UDP 没有连接状态，<see cref="State"/> 是空串。</para>
/// </summary>
public sealed class PortBinding
{
    /// <summary>协议名：TCP / TCPv6 / UDP / UDPv6。</summary>
    public string Protocol { get; init; } = "TCP";

    /// <summary>本地地址（IPv4 点分 / IPv6 冒号形式，不补方括号）。</summary>
    public string LocalAddress { get; init; } = "";

    /// <summary>本地端口（0 = 尚未绑定端口）。</summary>
    public int LocalPort { get; init; }

    /// <summary>远端地址（UDP 或 LISTENING 时为空）。</summary>
    public string RemoteAddress { get; init; } = "";

    /// <summary>远端端口（0 = 没有远端）。</summary>
    public int RemotePort { get; init; }

    /// <summary>TCP 状态文本（LISTENING / ESTABLISHED / TIME_WAIT …）；UDP 为空串。</summary>
    public string State { get; init; } = "";

    /// <summary>占用这条端口的进程 PID。</summary>
    public int Pid { get; init; }

    /// <summary>是否是 IPv6 记录（界面筛选时用）。</summary>
    public bool IsIPv6 => Protocol.EndsWith("v6", StringComparison.OrdinalIgnoreCase);

    /// <summary><c>127.0.0.1:8080</c> 这种本地端点文本（IPv6 自动补方括号）。</summary>
    public string LocalEndpoint => FormatEndpoint(LocalAddress, LocalPort);

    /// <summary>远端端点文本；没有远端（UDP / LISTENING）时为空串。</summary>
    public string RemoteEndpoint
        => RemotePort == 0 && string.IsNullOrEmpty(RemoteAddress) ? "" : FormatEndpoint(RemoteAddress, RemotePort);

    /// <summary>详情面板里的一行：<c>TCP 0.0.0.0:135  LISTENING</c>。</summary>
    public string DisplayText
    {
        get
        {
            var remote = RemoteEndpoint.Length == 0 ? "" : "  →  " + RemoteEndpoint;
            var state = State.Length == 0 ? "" : "  " + State;
            return $"{Protocol}  {LocalEndpoint}{remote}{state}";
        }
    }

    /// <summary>把地址和端口拼成端点文本；IPv6 地址自动加方括号，端口 0 时省略。</summary>
    public static string FormatEndpoint(string? address, int port)
    {
        address ??= "";
        if (port <= 0) return address.Length == 0 ? "*" : address;
        return address.Contains(':') ? $"[{address}]:{port}" : $"{address}:{port}";
    }

    public override string ToString() => DisplayText;
}
