using System.Net;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 地址栏输入解析（<b>纯逻辑、不碰界面</b>，自检可以直接断言）：
/// 把「网址 / 带不带协议 / localhost:端口 / 搜索词」统一成可导航的 URL。
///
/// <para>规则（和常见浏览器一致）：</para>
/// <list type="number">
///   <item>带 <c>http://</c> / <c>https://</c> 的原样使用（但主机名必须合法，否则视为无效）；</item>
///   <item><c>file://</c> / <c>about:</c> / <c>view-source:</c> 等其它协议原样交给 WebView2；</item>
///   <item>像域名的（<c>example.com</c>、<c>www.bing.com/search?q=1</c>）自动补 <c>https://</c>；</item>
///   <item>localhost / IP 地址自动补 <c>http://</c>（本地服务大多没证书）；</item>
///   <item>其余内容当搜索词，套用配置里的搜索地址模板。</item>
/// </list>
/// </summary>
public static class BrowserUrl
{
    /// <summary>带协议的写法（判断是否「已经是网址」时用，大小写不敏感）。</summary>
    private static readonly string[] KnownSchemes =
    {
        "http://", "https://", "file://", "about:", "edge:", "view-source:", "data:", "chrome:", "mailto:",
    };

    private static readonly char[] AuthorityTerminators = { '/', '?', '#' };

    /// <summary>
    /// 把地址栏输入解析成可导航 URL；空输入 / 非法网址返回 <c>null</c>，
    /// 纯搜索词返回按 <paramref name="searchUrl"/> 模板拼好的搜索地址。
    /// </summary>
    /// <param name="input">地址栏里的原始文本。</param>
    /// <param name="searchUrl">搜索模板（<c>{0}</c> = 关键词）；空则用配置默认值。</param>
    public static string? Resolve(string? input, string? searchUrl = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();

        foreach (var scheme in KnownSchemes)
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return NormalizeWithScheme(text);
        }

        // //example.com —— 协议相对写法，按 https 处理
        if (text.StartsWith("//", StringComparison.Ordinal))
            return NormalizeWithScheme("https:" + text);

        if (LooksLikeHost(text))
            return (PrefersHttp(text) ? "http://" : "https://") + text;

        return BuildSearchUrl(text, searchUrl);
    }

    /// <summary>按搜索模板拼搜索地址（模板写坏 / 没有 <c>{0}</c> 时回退到默认搜索地址）。</summary>
    public static string BuildSearchUrl(string query, string? searchUrl)
    {
        var template = searchUrl?.Trim();
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{0}", StringComparison.Ordinal))
            template = BrowserConfig.DefaultSearchUrl;

        return template.Replace("{0}", Uri.EscapeDataString(query.Trim()), StringComparison.Ordinal);
    }

    /// <summary>看起来是不是一个「主机名（+ 可选端口 / 路径）」，而不是搜索词。</summary>
    public static bool LooksLikeHost(string text)
    {
        var authority = GetAuthority(text);
        if (authority.Length == 0) return false;
        if (authority.Any(char.IsWhiteSpace)) return false;

        if (PrefersHttp(text)) return true;

        var host = StripPort(authority);
        if (host.Length < 3 || host.StartsWith('.') || host.EndsWith('.')) return false;
        if (!host.Contains('.', StringComparison.Ordinal)) return false;

        foreach (var label in host.Split('.'))
        {
            if (label.Length == 0) return false;
            foreach (var ch in label)
            {
                if (!char.IsLetterOrDigit(ch) && ch is not ('-' or '_')) return false;
            }
        }
        return true;
    }

    /// <summary>主机名要不要优先补 <c>http://</c>（localhost / 回环 / 任意 IP 字面量）。</summary>
    public static bool PrefersHttp(string text)
    {
        var host = StripPort(GetAuthority(text)).Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out _);
    }

    /// <summary>带协议的输入：http / https 必须能解析出主机名，其它协议原样放行（交给 WebView2 处理）。</summary>
    private static string? NormalizeWithScheme(string text)
    {
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? text
            : null;
    }

    /// <summary>取「协议 / 路径之前」的主机部分（含端口）。</summary>
    private static string GetAuthority(string text)
    {
        var end = text.IndexOfAny(AuthorityTerminators);
        return (end < 0 ? text : text[..end]).Trim();
    }

    /// <summary>去掉主机后面的端口（IPv6 的 <c>[::1]:8080</c> 也认）。</summary>
    private static string StripPort(string authority)
    {
        if (authority.StartsWith("[", StringComparison.Ordinal))
        {
            var close = authority.IndexOf(']');
            return close > 0 ? authority[..(close + 1)] : authority;
        }

        var firstColon = authority.IndexOf(':');
        var lastColon = authority.LastIndexOf(':');
        return firstColon == lastColon && lastColon > 0 ? authority[..lastColon] : authority;
    }
}
