using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YuLiangToolkit.Core.Net;

/// <summary>
/// 通用 TCP 房间底座的线上协议（纯逻辑，自检可直接断言）：
/// <b>每个包 = 4 字节小端长度前缀 + JSON 头 + 裸载荷</b>。
///
/// <para>这里<b>不含任何业务语义</b>：包类型字符串由使用方自己约定（棋牌房间见
/// <c>BoardProtocol.Kinds</c>），本类只负责「怎么把一帧拼成字节」「怎么解析用户填的端点」
/// 「本机哪个地址可以发给对方」「监听该绑哪个地址、端口被占怎么办」。</para>
///
/// <para>长度前缀只数 <b>JSON 头</b>的字节数（不含前缀自己、不含载荷）；载荷多长由头里的
/// <see cref="RoomFrame.Payload"/> 声明。两处必须一致，写反的症状是
/// 「读头时把载荷一起吃进来」→ JSON 解析失败 / 下一帧错位。</para>
/// </summary>
public static class RoomProtocol
{
    /// <summary>长度前缀字节数（小端 int32）。</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>JSON 头的最大字节数（正常头很小，留够余量即可）。</summary>
    public const int MaxHeaderBytes = 256 * 1024;

    /// <summary>单个包载荷的最大字节数（防呆：对方乱报长度时不要分配几百 MB）。</summary>
    public const int MaxPayloadBytes = 4 * 1024 * 1024;

    /// <summary>多久没收到任何数据算掉线（毫秒）。心跳 15 秒一次，留足余量。</summary>
    public const int IdleTimeoutMs = 90_000;

    /// <summary>心跳间隔（毫秒）。</summary>
    public const int KeepAliveMs = 15_000;

    /// <summary>JSON 序列化选项：camelCase、中文不转义、忽略 null、大小写不敏感（容错读）。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>传输层内部使用的包类型（心跳由 <see cref="RoomLink"/> 自己处理，不会抛给使用方）。</summary>
    public static class Kinds
    {
        public const string Ping = "ping";
        public const string Pong = "pong";
    }

    /// <summary>
    /// 把一个包拼成完整字节：<c>[4 字节长度][JSON 头][载荷]</c>，并顺手把载荷长度写进头里
    /// （调用方只要把字节交进来就不会忘了声明长度）。
    /// </summary>
    public static byte[] BuildFrame(RoomFrame header, ReadOnlySpan<byte> payload = default)
    {
        header.Payload = payload.Length;

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        if (headerBytes.Length > MaxHeaderBytes)
            throw new InvalidOperationException($"协议头过大（{headerBytes.Length} 字节）");
        if (payload.Length > MaxPayloadBytes)
            throw new InvalidOperationException($"单个包的载荷过大（{payload.Length} 字节）");

        var frame = new byte[LengthPrefixBytes + headerBytes.Length + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, headerBytes.Length);
        headerBytes.CopyTo(frame.AsSpan(LengthPrefixBytes));
        payload.CopyTo(frame.AsSpan(LengthPrefixBytes + headerBytes.Length));
        return frame;
    }

    /// <summary>校验长度前缀是否合理（负数 / 0 / 超过头上限都要拒，否则会变成 allocation DoS）。</summary>
    public static bool IsValidFrameLength(int headerLength)
        => headerLength > 0 && headerLength <= MaxHeaderBytes;

    // ---------------------------------------------------------------- 端点解析

    /// <summary>
    /// 解析用户输入的「主机地址」：支持 <c>192.168.1.5</c>、<c>myhost.abc.com</c>、
    /// <c>[fe80::1]</c>、<c>2001:db8::1</c>、<c>192.168.1.5:5200</c>（内网穿透工具给的常常带端口）。
    /// </summary>
    /// <returns>主机名（IPv6 已去掉方括号）+ 可选端口（0 = 文本里没写）；解析不了返回 <c>(false, …)</c>。</returns>
    public static (bool Ok, string Host, int Port, string Error) ParseEndpoint(string? text)
    {
        var raw = (text ?? "").Trim();
        if (raw.Length == 0) return (false, "", 0, "请填写主机地址（例如 192.168.1.5）");

        var host = raw;
        var port = 0;

        if (raw.StartsWith('['))                       // [IPv6]:端口
        {
            var close = raw.IndexOf(']');
            if (close < 0) return (false, "", 0, "IPv6 地址缺少右方括号");
            host = raw[1..close];
            var rest = raw[(close + 1)..];
            if (rest.StartsWith(':') && rest.Length > 1)
            {
                if (!int.TryParse(rest[1..], out port)) return (false, "", 0, "端口不是数字");
            }
        }
        else if (raw.Count(c => c == ':') == 1)        // 主机:端口（IPv6 会有多个冒号，进不来这里）
        {
            var idx = raw.IndexOf(':');
            host = raw[..idx];
            if (!int.TryParse(raw[(idx + 1)..], out port)) return (false, "", 0, "端口不是数字");
        }
        // 其它情况（纯 IPv4 / 纯 IPv6 / 域名）整串就是主机名

        host = host.Trim();
        if (host.Length == 0) return (false, "", 0, "请填写主机地址");
        if (port is < 0 or > 65535) return (false, "", 0, "端口必须在 1~65535 之间");
        return (true, host, port, "");
    }

    /// <summary>
    /// 把用户填的地址解析成可以 <c>ConnectAsync</c> 的端点：
    /// 先当 IP 解析（IPv4 / IPv6 都试），不是 IP 就当域名交给 DNS（内网穿透的域名常是这种）。
    /// </summary>
    public static async Task<(bool Ok, EndPoint? EndPoint, string Display, string Error)> ResolveAsync(
        string host, int port, CancellationToken ct = default)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return (false, null, "", "主机地址为空");
        if (port is <= 0 or > 65535) return (false, null, "", "端口必须在 1~65535 之间");

        if (IPAddress.TryParse(host.Trim('[', ']'), out var ip))
            return (true, new IPEndPoint(ip, port), FormatEndpoint(ip, port), "");

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            // 优先 IPv4（局域网 + 常见穿透工具基本都是 v4），没有再用 IPv6
            var pick = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                       ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            if (pick is null) return (false, null, "", $"域名 {host} 没有解析到地址（DNS 没结果）");
            return (true, new IPEndPoint(pick, port), FormatEndpoint(pick, port), "");
        }
        catch (Exception ex)
        {
            return (false, null, "", $"域名 {host} 解析失败：{ex.Message}");
        }
    }

    /// <summary>端点 → 可读文本（IPv6 自动加方括号，方便直接复制给别人）。</summary>
    public static string FormatEndpoint(IPAddress address, int port)
        => address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{address}]:{port}"
            : $"{address}:{port}";

    // ---------------------------------------------------------------- 本机地址分享

    /// <summary>一个「可以告诉对方」的本机地址（开完房间后照着念就行）。</summary>
    /// <param name="Text">地址本体（IPv6 带方括号，直接就能拼端口）。</param>
    /// <param name="Family"><c>IPv4</c> / <c>IPv6</c>。</param>
    /// <param name="Interface">网卡名（对方连不上时好排查是哪张卡）。</param>
    /// <param name="IsRecommended">是不是最可能连得通的那一个（有线 &gt; 无线 &gt; 虚拟网卡）。</param>
    public sealed record ShareAddress(string Text, string Family, string Interface, bool IsRecommended);

    /// <summary>傻瓜模式挑出来的监听方案（纯数据，自检直接断言）。</summary>
    /// <param name="Address">监听地址：<c>::</c>（双栈）或 <c>0.0.0.0</c>。</param>
    /// <param name="Port">监听端口（0 = 让系统挑）。</param>
    /// <param name="DualStack">这个监听是不是同时收 IPv4 与 IPv6。</param>
    /// <param name="Reason">一句话说明为什么这么挑（日志与界面都用它）。</param>
    public sealed record SimpleListenPlan(string Address, int Port, bool DualStack, string Reason);

    /// <summary>有没有「能给别人用」的 IPv6 地址（链路本地 fe80:: 不算，别人连不上）。</summary>
    public static bool HasUsableIPv6 => ListShareAddresses().Any(a => a.Family == "IPv6");

    /// <summary>没有可用 IPv6 时给用户看的那句话。</summary>
    public const string NoIpv6Text = "IPv6：当前网络没有 IPv6（运营商 / 路由器没开），对方用上面 IPv4 的地址就行";

    /// <summary>
    /// 本机「可以告诉对方」的地址列表，IPv4 在前、IPv6 在后，各按推荐度排序。
    ///
    /// <para><b>筛掉什么</b>：回环（127.0.0.1 / ::1）、自动私有地址（169.254.*）、
    /// IPv6 链路本地（fe80::，必须带 %网卡 才通）、多播、IPv4 映射地址、
    /// 以及隧道网卡（Teredo / ISATAP 那类，Windows 上一般也连不进来）。</para>
    /// </summary>
    public static IReadOnlyList<ShareAddress> ListShareAddresses()
    {
        var list = new List<(ShareAddress Address, int Score)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;

                var score = ScoreOf(nic);
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = addr.Address;
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var text = ip.ToString();
                        if (text.StartsWith("127.") || text.StartsWith("169.254.")) continue;
                        list.Add((new ShareAddress(text, "IPv4", nic.Name, false), score));
                    }
                    else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal) continue;
                        if (ip.Equals(IPAddress.IPv6Loopback)) continue;
                        if (ip.IsIPv4MappedToIPv6) continue;
                        list.Add((new ShareAddress($"[{ip}]", "IPv6", nic.Name, false), score));
                    }
                }
            }
        }
        catch { /* 取不到就返回空列表 */ }

        var ordered = list
            .OrderBy(x => x.Address.Family == "IPv4" ? 0 : 1)
            .ThenBy(x => x.Score)
            .Select(x => x.Address)
            .DistinctBy(a => a.Text)
            .ToList();

        // 每一族里排第一的算「推荐」——界面上标一下，用户不用猜该发哪个
        var firstV4 = ordered.FindIndex(a => a.Family == "IPv4");
        var firstV6 = ordered.FindIndex(a => a.Family == "IPv6");
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i == firstV4 || i == firstV6) ordered[i] = ordered[i] with { IsRecommended = true };
        }
        return ordered;
    }

    /// <summary>网卡推荐度：有线 0、无线 2、虚拟网卡再加 5（越小的越可能被别人连上）。</summary>
    private static int ScoreOf(NetworkInterface nic)
    {
        var score = nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 2;
        var name = nic.Name + " " + nic.Description;
        foreach (var virtualName in new[] { "VMware", "VirtualBox", "Hyper-V", "WSL", "Loopback", "Radmin" })
        {
            if (name.Contains(virtualName, StringComparison.OrdinalIgnoreCase)) score += 5;
        }
        return score;
    }

    /// <summary>
    /// 傻瓜模式：工具自己挑「监听哪个地址、哪个端口」，用户一个都不用填。
    ///
    /// <para><b>为什么优先 <c>::</c></b>：Windows 上 IPv6 通配地址默认「只收 IPv6」，
    /// 必须由 <see cref="RoomHost"/> 显式开双栈（<see cref="Socket.DualMode"/>）才同时收 IPv4 ——
    /// 一个双栈端口就把两族都覆盖了，用户也只需要报一个端口。</para>
    ///
    /// <para><b>端口</b>：优先用上次那个（好记、内网穿透映射也稳定），被占了就让系统随便挑一个空闲的
    /// —— 傻瓜模式的目标是「点一下一定能开起来」，不能因为端口冲突就失败。</para>
    /// </summary>
    /// <param name="preferredPort">优先想用的端口（一般是上次用的那个）。</param>
    /// <param name="supportsIPv6">本机跑得动 IPv6 吗（<see cref="Socket.OSSupportsIPv6"/>）。</param>
    /// <param name="portAvailable">探测「这个地址 + 端口能不能绑上」（自检里换成假的，纯逻辑可断言）。</param>
    public static SimpleListenPlan ChooseSimpleListenPlan(
        int preferredPort, bool supportsIPv6, Func<string, int, bool> portAvailable)
    {
        var address = supportsIPv6 ? "::" : "0.0.0.0";
        var addressText = supportsIPv6 ? "::（双栈：IPv4 与 IPv6 都能连）" : "0.0.0.0（只用 IPv4）";

        var wanted = preferredPort is >= 1024 and <= 65535 ? preferredPort : 0;
        if (wanted > 0 && portAvailable(address, wanted))
            return new SimpleListenPlan(address, wanted, supportsIPv6, $"自动监听 {addressText}，端口 {wanted}");

        var reason = wanted > 0
            ? $"自动监听 {addressText}；端口 {wanted} 已被占用，改成让系统挑一个空闲端口"
            : $"自动监听 {addressText}；没设过端口，让系统挑一个空闲端口";
        return new SimpleListenPlan(address, 0, supportsIPv6, reason);
    }

    /// <summary>
    /// 探测「这个地址 + 端口」现在能不能绑上（傻瓜模式挑端口用）。
    /// <b>故意不设 <c>SO_REUSEADDR</c></b>：Windows 上带它的绑定能“抢”已被占用的端口，
    /// 探出来的结果就没意义了。
    /// </summary>
    public static bool IsPortAvailable(string? addressText, int port)
    {
        if (port is < 1 or > 65535) return false;
        try
        {
            var text = (addressText ?? "").Trim().Trim('[', ']');
            var address = text.Length == 0 ? IPAddress.Any : IPAddress.Parse(text);
            var listener = new TcpListener(address, port);
            if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把一个地址和一个端口拼成能直接发给别人的文本（IPv6 自动加方括号）。</summary>
    public static string WithPort(string? addressText, int port)
    {
        var text = (addressText ?? "").Trim();
        if (text.Length == 0) return "";
        return text.StartsWith('[') ? $"{text}:{port}" : text.Contains(':') ? $"[{text}]:{port}" : $"{text}:{port}";
    }
}
