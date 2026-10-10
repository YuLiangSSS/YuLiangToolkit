using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ChatRoom.Protocol;

/// <summary>
/// 「联机聊天室」的线上协议（纯逻辑，自检直接断言）：
/// <b>每个包 = 4 字节小端长度前缀 + JSON 头 + 裸载荷</b>，长度是整个包（头 + 载荷）的字节数。
///
/// <para>协议全貌有一份人也能读的说明：<c>Protocol\chat-protocol.json</c>（嵌入资源，
/// 首次运行释放到 <c>Data\chat-room\Protocol\</c>）。改这里的常量时记得同步那份文件。</para>
///
/// <para>为什么用「长度前缀 + JSON 头 + 裸载荷」而不是纯文本协议：
/// 文字消息要能带元数据（谁发的、什么时候、消息 Id），文件又要以接近磁盘速度灌字节；
/// 把元数据放 JSON 头、文件本体放裸载荷，两边都不用做 Base64 膨胀（+33% 流量）。</para>
/// </summary>
public static class ChatProtocol
{
    /// <summary>握手用的协议标识：对不上就直接拒连（防止别的程序连进来把连接搞乱）。</summary>
    public const string Magic = "YLTCHAT1";

    /// <summary>协议版本。加减字段不算破坏兼容；改帧格式才动它。</summary>
    public const int Version = 1;

    /// <summary>长度前缀字节数。</summary>
    public const int LengthPrefixBytes = 4;

    /// <summary>单个包的最大字节数（防呆：对方乱报长度时不要分配几百 MB）。</summary>
    public const int MaxFrameBytes = 4 * 1024 * 1024;

    /// <summary>JSON 头的最大字节数（文件包的头也很小，留够余量即可）。</summary>
    public const int MaxHeaderBytes = 256 * 1024;

    /// <summary>文件数据包每条连接的读写缓冲。</summary>
    public const int FileChunkBytes = 64 * 1024;

    /// <summary>没收到任何包多久算掉线（毫秒）。心跳 15 秒一次，留足余量。</summary>
    public const int IdleTimeoutMs = 90_000;

    /// <summary>心跳间隔（毫秒）。</summary>
    public const int KeepAliveMs = 15_000;

    /// <summary>JSON 头序列化选项：camelCase、中文不转义。</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 把一个包拼成完整字节：<c>[4 字节长度][JSON 头][载荷]</c>。
    ///
    /// <para><b>长度前缀 = JSON 头的字节数</b>（<b>不</b>含长度前缀自己，也<b>不</b>含载荷）。
    /// 载荷多长由头里的 <see cref="ChatFrameHeader.Payload"/> 说明 —— 这两处必须一致，
    /// 写反了会变成“读头时把载荷一起吃进来”，<c>JsonException：'0xE4' is invalid after a single JSON value</c>
    /// 就是它的典型症状（自检里踩过，别改回去）。</para>
    ///
    /// <para>文件那种大块裸字节不走这里：先写 <see cref="BuildLengthPrefix"/> + 小头，再直接灌字节
    /// （见 <c>ChatLink.SendFileAsync</c>）。</para>
    /// </summary>
    public static byte[] BuildFrame(ChatFrameHeader header, ReadOnlySpan<byte> payload)
    {
        // 载荷长度由这里统一写进头里：调用方只要把字节交进来就不会忘了声明长度
        // （忘了声明的症状是「对方读到 0 长度的载荷，然后把载荷当成下一个包的长度前缀」，
        //   表现为包头长度不合法 / JSON 解析失败，非常难查 —— 自检里踩过。）
        header.Payload = payload.Length;

        var headerBytes = JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);
        if (headerBytes.Length > MaxHeaderBytes)
            throw new InvalidOperationException($"协议头过大（{headerBytes.Length} 字节）");
        if (payload.Length > MaxFrameBytes)
            throw new InvalidOperationException($"单个包的载荷过大（{payload.Length} 字节）");

        var frame = new byte[LengthPrefixBytes + headerBytes.Length + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, headerBytes.Length);
        headerBytes.CopyTo(frame.AsSpan(LengthPrefixBytes));
        payload.CopyTo(frame.AsSpan(LengthPrefixBytes + headerBytes.Length));
        return frame;
    }

    /// <summary>只写长度前缀（数据连接上先写 JSON 头，再流式灌文件本体时用）。</summary>
    public static byte[] BuildLengthPrefix(int headerLength)
    {
        var buffer = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, headerLength);
        return buffer;
    }

    /// <summary>校验长度前缀是否合理（负数 / 0 / 超过头上限都要拒，否则会变成 allocation DoS）。</summary>
    public static bool IsValidFrameLength(int headerLength)
        => headerLength > 0 && headerLength <= MaxHeaderBytes;

    /// <summary>包类型（线上是字符串，加减类型不会让老版本错位）。</summary>
    public static class Kinds
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Text = "text";
        public const string System = "system";
        public const string Members = "members";
        public const string Kick = "kick";
        public const string TransferStart = "transferStart";
        public const string TransferDone = "transferDone";
        public const string TransferCancel = "transferCancel";
        public const string Bye = "bye";
    }

    /// <summary>
    /// 解析用户输入的「主机地址」：支持 <c>192.168.1.5</c>、<c>myhost.abc.com</c>、
    /// <c>[fe80::1]</c>、<c>2001:db8::1</c>、<c>192.168.1.5:5200</c>（内网穿透工具给的常常带端口）。
    /// </summary>
    /// <returns>主机名（IPv6 已去掉方括号）+ 可选端口；解析不了返回 <c>(false, …)</c>。</returns>
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
    /// 把用户填的地址解析成可以 <c>ConnectAsync</c> 的 <see cref="IPEndPoint"/> 或主机名端点：
    /// 先当 IP 解析（IPv4 / IPv6 都试），不是 IP 就当域名交给 DNS（内网穿透的域名常是这种）。
    /// </summary>
    public static async Task<(bool Ok, EndPoint? EndPoint, string Display, string Error)> ResolveAsync(
        string host, int port, CancellationToken ct = default)
    {
        host = (host ?? "").Trim();
        if (host.Length == 0) return (false, null, "", "主机地址为空");
        if (port is <= 0 or > 65535) return (false, null, "", "端口必须在 1~65535 之间");

        if (IPAddress.TryParse(host, out var ip))
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

    /// <summary>
    /// 本机在局域网里“最可能被对方连上”的 IPv4 地址（第一个非回环、非虚拟网卡的可用地址）。
    /// 找不到返回空字符串（例如完全没联网）——界面上就显示“手动填 ipconfig 里的地址”。
    /// </summary>
    public static string DetectLanIPv4()
    {
        try
        {
            var candidates = new List<(string Text, int Score)>();
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var text = addr.Address.ToString();
                    if (text.StartsWith("127.") || text.StartsWith("169.254.")) continue;   // 回环 / 自动私有地址
                    // 有线优先，其次无线；虚拟网卡（VMware / VirtualBox / WSL）排后面
                    var name = nic.Name + " " + nic.Description;
                    var score = nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ? 0 : 2;
                    if (name.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("WSL", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)) score += 5;
                    candidates.Add((text, score));
                }
            }
            return candidates.OrderBy(c => c.Score).Select(c => c.Text).FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>本机所有可用地址文本（界面上给用户挑，含 IPv6）。</summary>
    public static IReadOnlyList<string> ListLocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    var text = addr.Address.ToString();
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        if (text.StartsWith("169.254.")) continue;                 // 自动私有地址，连不通
                        list.Add(text);
                    }
                    else if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        if (addr.Address.IsIPv6LinkLocal || addr.Address.IsIPv6Multicast) continue;
                        list.Add($"[{text}]");                                     // 可复制形式
                    }
                }
            }
        }
        catch { /* 取不到就返回空列表 */ }
        return list.Distinct().ToList();
    }

    // ---------------------------------------------------------------- 傻瓜模式：地址与监听

    /// <summary>
    /// 一个「可以告诉对方」的本机地址（傻瓜模式开完房间后照着念就行）。
    /// </summary>
    /// <param name="Text">地址本体（IPv6 带方括号，直接就能拼端口）。</param>
    /// <param name="Family"><c>IPv4</c> / <c>IPv6</c>。</param>
    /// <param name="Interface">网卡名（对方连不上时好排查是哪张卡）。</param>
    /// <param name="IsRecommended">是不是最可能连得通的那一个（有线 &gt; 无线 &gt; 虚拟网卡）。</param>
    public sealed record ShareAddress(string Text, string Family, string Interface, bool IsRecommended);

    /// <summary>
    /// 傻瓜模式挑出来的监听方案（纯数据，自检直接断言）。
    /// </summary>
    /// <param name="Address">监听地址：<c>::</c>（双栈）或 <c>0.0.0.0</c>。</param>
    /// <param name="Port">监听端口（0 = 让系统挑）。</param>
    /// <param name="DualStack">这个监听是不是同时收 IPv4 与 IPv6。</param>
    /// <param name="Reason">一句话说明为什么这么挑（日志与界面都用它）。</param>
    public sealed record SimpleListenPlan(string Address, int Port, bool DualStack, string Reason);

    /// <summary>有没有「能给别人用」的 IPv6 地址（口令地址 fe80:: 不算，别人连不上）。</summary>
    public static bool HasUsableIPv6 => ListShareAddresses().Any(a => a.Family == "IPv6");

    /// <summary>没有可用 IPv6 时给用户看的那句话。</summary>
    public const string NoIpv6Text = "IPv6：当前网络没有 IPv6（运营商 / 路由器没开），对方用上面 IPv4 的地址就行";

    /// <summary>
    /// 本机「可以告诉对方」的地址列表，IPv4 在前、IPv6 在后，各按推荐度排序。
    ///
    /// <para><b>筛掉什么</b>：回环（127.0.0.1 / ::1）、自动私有地址（169.254.*）、
    /// IPv6 链路本地（fe80:: —— 必须带 %网卡 才通，复制给别人基本没用）、多播、
    /// 以及隧道网卡（Teredo / ISATAP 那类，Windows 上一般也连不进来）。</para>
    /// </summary>
    public static IReadOnlyList<ShareAddress> ListShareAddresses()
    {
        var list = new List<(ShareAddress Address, int Score)>();
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                // 隧道网卡（Teredo 等）：地址看着像公网 IPv6，实际连不进来，别误导人
                if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;

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
                        // IPv4 映射地址（::ffff:1.2.3.4）跟 IPv4 那行重复，跳过
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
    private static int ScoreOf(System.Net.NetworkInformation.NetworkInterface nic)
    {
        var score = nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ? 0 : 2;
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
    /// <para><b>为什么优先 <c>::</c></b>：Windows 上 IPv6 通配地址默认是「只收 IPv6」，
    /// 必须显式开双栈（<see cref="Socket.DualMode"/>）才同时收 IPv4 —— 见 <c>ChatHost.StartAsync</c>。
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
    /// 探出来的结果就没意义了（实测：不设才会老实报 AddressAlreadyInUse）。
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
        catch { return false; }
    }

    /// <summary>把一个地址和一个端口拼成能直接发给别人的文本（IPv6 自动加方括号）。</summary>
    public static string WithPort(string? addressText, int port)
    {
        var text = (addressText ?? "").Trim();
        if (text.Length == 0) return "";
        return text.StartsWith('[') ? $"{text}:{port}" : text.Contains(':') ? $"[{text}]:{port}" : $"{text}:{port}";
    }

    /// <summary>把聊天室协议说明释放到数据目录（首次运行写一份，用户改过就不覆盖）。</summary>
    public static string EnsureProtocolDoc(string directory)
    {
        var target = Path.Combine(directory, "chat-protocol.json");
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            if (File.Exists(target)) return target;
            var asm = typeof(ChatProtocol).Assembly;
            using var stream = asm.GetManifestResourceStream("YuLiangToolkit.Tools.ChatRoom.Protocol.chat-protocol.json");
            if (stream is null) return target;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            File.WriteAllText(target, reader.ReadToEnd());
        }
        catch { /* 释放失败不影响功能 */ }
        return target;
    }

    /// <summary>当前协议的 JSON 摘要（界面上点「查看协议」用）。</summary>
    public static string Describe()
        => $"""
           YuLiangToolkit 聊天室协议 v{Version}
           magic={Magic}
           帧格式：4 字节小端长度 + JSON 头 + 裸载荷（长度 = 头长 + 载荷长）
           单包上限：{MaxFrameBytes / 1024 / 1024} MB　　头上限：{MaxHeaderBytes / 1024} KB
           包类型：{string.Join(" / ", new[]
           {
               Kinds.Hello, Kinds.Welcome, Kinds.Ping, Kinds.Pong, Kinds.Text, Kinds.System,
               Kinds.Members, Kinds.Kick, Kinds.TransferStart, Kinds.TransferDone, Kinds.TransferCancel, Kinds.Bye,
           })}
           说明文档：Data\chat-room\Protocol\chat-protocol.json（首次运行自动释放）
           """;
}
