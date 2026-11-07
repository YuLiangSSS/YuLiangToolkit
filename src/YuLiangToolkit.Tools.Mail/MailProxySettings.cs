using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailKit.Net.Proxy;
using Microsoft.Win32;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>Windows「Internet 选项」里读到的系统代理快照（自检可以自己构造，不碰注册表）。</summary>
public sealed record SystemProxyInfo(bool Enabled, string Server, string BypassList);

/// <summary>一次代理解析的结果：要传给 MailKit 的代理客户端 + 给日志 / 界面看的说明。</summary>
public sealed record ResolvedProxy(IProxyClient? Client, bool IsProxy, string Description);

/// <summary>解析出来的代理端点（系统代理字符串可能是 <c>http=</c> / <c>socks=</c> / 纯 host:port）。</summary>
public sealed record ProxyEndpoint(string Host, int Port, string Type);

/// <summary>
/// 「邮箱代收」的代理设置（全局，存在 <c>Config\mail-collector\config.json</c> 的 <c>proxy</c> 里）。
///
/// <para><b>为什么需要它</b>：Gmail / Outlook 等邮箱的 IMAP 端口在国内网络经常直连不通，而浏览器
/// 走的是本机代理（Clash / v2rayN 之类）。MailKit 默认直连，于是出现「Chrome 能打开 Gmail，工具却连接超时」。
/// 默认 <see cref="Mode"/> = <c>auto</c>：读取 Windows 系统代理（HKCU Internet Settings），
/// 并遵守它的「例外列表」——例如 <c>*.qq.com</c> 在例外列表里时 QQ 仍然直连，只有 Gmail 走代理。</para>
///
/// <para><b>三种模式</b>：<c>auto</c> 跟随系统代理 / <c>direct</c> 直连 / <c>custom</c> 手动填服务器、端口、类型。</para>
/// </summary>
public sealed class MailProxySettings
{
    /// <summary>代理模式：auto（跟随 Windows 系统代理）/ direct（直连）/ custom（自定义）。</summary>
    public string Mode { get; set; } = "auto";

    /// <summary>自定义模式的代理服务器。</summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>自定义模式的代理端口。</summary>
    public int Port { get; set; } = 7890;

    /// <summary>自定义模式的代理类型：http / socks5（https 仅系统代理里可能出现）。</summary>
    public string Type { get; set; } = "http";

    /// <summary>模式的中文名（界面 / 日志用；只读计算属性不落盘）。</summary>
    [JsonIgnore]
    public string ModeLabel => Mode switch
    {
        "direct" => "直连",
        "custom" => "自定义代理",
        _ => "跟随系统代理",
    };

    /// <summary>按当前设置 + 实时系统代理，解析出「某个邮箱主机」要用的代理（界面 / 日志用，不真正连接）。</summary>
    public ResolvedProxy Resolve(string targetHost)
        => Resolve(targetHost, ReadWindowsSystemProxy());

    /// <summary>按给定的系统代理快照解析（自检用这个重载，避免读真实注册表）。</summary>
    public ResolvedProxy Resolve(string targetHost, SystemProxyInfo systemProxy)
    {
        var mode = (Mode ?? "auto").Trim().ToLowerInvariant();
        if (mode == "direct") return new ResolvedProxy(null, false, "直连（已选择不使用代理）");

        if (mode == "custom")
        {
            if (string.IsNullOrWhiteSpace(Host) || Port is <= 0 or > 65535)
                return new ResolvedProxy(null, false, "直连（自定义代理没填完整）");
            var type = NormalizeType(Type);
            return new ResolvedProxy(
                CreateClient(type, Host.Trim(), Port),
                true,
                $"自定义代理 {Host.Trim()}:{Port}（{TypeLabel(type)}）");
        }

        if (!systemProxy.Enabled) return new ResolvedProxy(null, false, "直连（Windows 未启用系统代理）");
        if (IsBypassed(targetHost, systemProxy.BypassList))
            return new ResolvedProxy(null, false, "直连（目标在系统代理例外列表）");

        var endpoint = ParseSystemProxy(systemProxy.Server);
        if (endpoint is null) return new ResolvedProxy(null, false, "直连（系统代理地址无法解析）");

        return new ResolvedProxy(
            CreateClient(endpoint.Type, endpoint.Host, endpoint.Port),
            true,
            $"系统代理 {endpoint.Host}:{endpoint.Port}（{TypeLabel(endpoint.Type)}）");
    }

    /// <summary>一句话描述代理路线（日志用）。</summary>
    public string Describe(string targetHost) => Resolve(targetHost).Description;

    // ---------- 系统代理读取 / 解析 ----------

    /// <summary>读当前 Windows 用户的系统代理（HKCU\...\Internet Settings）；失败按“未启用”处理。</summary>
    public static SystemProxyInfo ReadWindowsSystemProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (key is null) return new SystemProxyInfo(false, "", "");
            var enabled = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) == 1;
            var server = key.GetValue("ProxyServer") as string ?? "";
            var bypass = key.GetValue("ProxyOverride") as string ?? "";
            return new SystemProxyInfo(enabled, server, bypass);
        }
        catch
        {
            return new SystemProxyInfo(false, "", "");
        }
    }

    /// <summary>
    /// 解析 Windows 的 <c>ProxyServer</c> 字符串：
    /// <c>127.0.0.1:7897</c> / <c>http=127.0.0.1:7897;https=...;socks=127.0.0.1:1080</c>。
    /// 优先级：socks &gt; http &gt; https &gt; 纯 host:port；解析不出返回 null。
    /// </summary>
    public static ProxyEndpoint? ParseSystemProxy(string? server)
    {
        if (string.IsNullOrWhiteSpace(server)) return null;

        ProxyEndpoint? socks = null;
        ProxyEndpoint? http = null;
        ProxyEndpoint? https = null;
        ProxyEndpoint? plain = null;

        foreach (var raw in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var entry = raw.Trim();
            if (entry.Length == 0) continue;

            var equals = entry.IndexOf('=');
            var key = equals > 0 ? entry[..equals].Trim().ToLowerInvariant() : "";
            var value = equals > 0 ? entry[(equals + 1)..].Trim() : entry;
            var parsed = ParseEndpoint(value);
            if (parsed is null) continue;

            if (key.StartsWith("socks", StringComparison.Ordinal))
                socks ??= parsed with { Type = "socks5" };
            else if (key == "http")
                http ??= parsed with { Type = "http" };
            else if (key == "https")
                https ??= parsed with { Type = "https" };
            else if (key.Length == 0)
                plain ??= parsed;
        }

        return socks ?? http ?? https ?? plain;
    }

    /// <summary>
    /// 目标主机是否命中系统代理的例外列表（ProxyOverride）：支持 <c>&lt;local&gt;</c>、<c>*</c>、
    /// <c>*.example.com</c>、<c>192.168.*</c>、精确域名（含子域）。
    /// </summary>
    public static bool IsBypassed(string? host, string? bypassList)
    {
        var target = (host ?? "").Trim().TrimEnd('.');
        if (target.Length == 0 || string.IsNullOrWhiteSpace(bypassList)) return false;
        target = target.ToLowerInvariant();

        foreach (var raw in bypassList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pattern = raw.Trim().TrimEnd('.').ToLowerInvariant();
            if (pattern.Length == 0) continue;

            if (pattern == "<local>")
            {
                if (!target.Contains('.')) return true;
                continue;
            }

            if (pattern == "*") return true;

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = pattern[1..];                       // ".qq.com"
                if (target.EndsWith(suffix, StringComparison.Ordinal) || target == suffix[1..]) return true;
                continue;
            }

            if (pattern.Contains('*'))
            {
                if (WildcardMatch(target, pattern)) return true;
                continue;
            }

            if (target == pattern || target.EndsWith("." + pattern, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>把类型字符串规范成 http / socks5 / https。</summary>
    public static string NormalizeType(string? type)
    {
        var value = (type ?? "").Trim().ToLowerInvariant();
        if (value.StartsWith("socks", StringComparison.Ordinal)) return "socks5";
        if (value == "https") return "https";
        return "http";
    }

    /// <summary>类型的中文 / 英文短标签。</summary>
    public static string TypeLabel(string? type) => NormalizeType(type) switch
    {
        "socks5" => "SOCKS5",
        "https" => "HTTPS",
        _ => "HTTP",
    };

    // ---------- 内部 ----------

    private static IProxyClient CreateClient(string type, string host, int port) => NormalizeType(type) switch
    {
        "socks5" => new Socks5Client(host, port),
        "https" => new HttpsProxyClient(host, port),
        _ => new HttpProxyClient(host, port),
    };

    /// <summary>解析一个 endpoint 值（可能带 http:// / socks5:// / https:// 前缀，支持 [IPv6]:port）。</summary>
    private static ProxyEndpoint? ParseEndpoint(string value)
    {
        var text = value.Trim();
        if (text.Length == 0) return null;

        var type = "http";
        var schemeIndex = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex > 0)
        {
            type = NormalizeType(text[..schemeIndex]);
            text = text[(schemeIndex + 3)..];
        }

        string host;
        string portText;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) return null;
            host = text[1..close];
            if (close + 1 >= text.Length || text[close + 1] != ':') return null;
            portText = text[(close + 2)..];
        }
        else
        {
            var colon = text.LastIndexOf(':');
            if (colon <= 0 || colon == text.Length - 1) return null;
            host = text[..colon];
            portText = text[(colon + 1)..];
        }

        if (!int.TryParse(portText, out var port) || port is <= 0 or > 65535) return null;
        host = host.Trim();
        return host.Length == 0 ? null : new ProxyEndpoint(host, port, type);
    }

    /// <summary>简单通配符匹配（只支持 *），用于 ProxyOverride 里 192.168.* / 10.* 这类条目。</summary>
    private static bool WildcardMatch(string value, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
