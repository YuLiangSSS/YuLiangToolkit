using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>
/// 游戏页 / 网页文本解析（纯函数，无 IO、无 WPF，自检里逐条断言）。
/// 从原始 Python 项目的 <c>HtmlAnalyse</c> 移植，正则与判定顺序保持一致。
/// </summary>
public static partial class GamePageParser
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>
    /// .NET Core 起，<c>gbk</c> / <c>gb2312</c> 这些中文代码页默认不可用，要显式注册。
    /// 老游戏站的网页大量是 GBK，缺了这一步中文会全是乱码（自检里踩过）。
    /// </summary>
    static GamePageParser()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch { /* 注册不了就退化成 UTF-8 猜测 */ }
    }

    /// <summary>访问网页时统一使用的 User-Agent（4399 会挡没有 UA 的请求）。</summary>
    public static string DefaultUserAgent => UserAgent;

    // ---------------------------------------------------------------- 文件名 / URL

    /// <summary>
    /// 把任意字符串转成安全的文件名：删掉 <c>/ \ : ? &lt; &gt; * | . 空格 制表 换行</c>，
    /// 下划线换成连字符，最后截断到 100 字（对应 Python 的 <c>toSafeFileName</c>）。
    /// </summary>
    public static string ToSafeFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var chars = new List<char>(name.Length);
        foreach (var ch in name)
        {
            if (ch is '/' or '\\' or ':' or '?' or '<' or '>' or '*' or '|' or '.' or ' ' or '\t' or '\n' or '\r')
                continue;
            chars.Add(ch == '_' ? '-' : ch);
        }
        var result = new string(chars.ToArray()).Trim();
        return result.Length > 100 ? result[..100] : result;
    }

    /// <summary>取 URL 里的文件名（不含查询串），例如 <c>.../main.swf?v=1</c> → <c>main.swf</c>。</summary>
    public static string GetUrlFileName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "";
        return Path.GetFileName(uri.AbsolutePath);
    }

    /// <summary>取 URL 里的扩展名（小写，含点），例如 <c>.swf</c>。</summary>
    public static string GetUrlExtension(string? url)
        => Path.GetExtension(GetUrlFileName(url)).ToLowerInvariant();

    /// <summary>
    /// 从游戏页 URL 里认出「后缀 + 站点」。
    /// 例：<c>https://www.4399.com/flash/6853.htm</c> → <c>("a6853", Site4399)</c>。
    /// 站点判定与字母兜底都搬到了 <see cref="GameUrlInfo"/>（那边有详细说明），这里只负责抽出编号。
    /// </summary>
    public static (string Appendix, GameSite Site) GetUrlInfo(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return ("", GameSite.Unknown);
        var trimmed = url.Trim();
        var match = UrlNumberPattern().Match(trimmed);
        return GameUrlInfo.Describe(trimmed, match.Success ? match.Groups[1].Value : "");
    }
    /// <summary>
    /// 后缀 → 游戏页 URL（「装入文件夹 / 改为 htm 打开 / 自动识别」都靠它重新找到游戏）。
    /// 认不出来返回 null。
    /// </summary>
    public static (string Url, GameSite Site)? AppendixToUrl(string? appendix)
    {
        var match = AppendixPattern().Match((appendix ?? "").Trim());
        if (!match.Success) return null;
        var letter = match.Groups[1].Value[0];
        var number = match.Groups[2].Value;
        return char.ToLowerInvariant(letter) switch
        {
            'a' => ($"https://www.4399.com/flash/{number}.htm", GameSite.Site4399),
            'b' => ($"https://www.7k7k.com/swf/{number}.htm", GameSite.Site7k7k),
            'c' => ($"http://www.17yy.com/f/play/{number}.html", GameSite.Site17yy),
            _ => null,
        };
    }

    /// <summary>相对路径 → 绝对 URL（对应 python 的 <c>urljoin</c>）。</summary>
    public static string UrlJoin(string? baseUrl, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return "";
        var rel = relative.Trim();
        // 协议相对：//sda.4399.com/a.swf —— 必须在“当绝对地址试”之前判断
        //（否则会被 Uri 当成 file:// 主机名，得到 file://sda.4399.com/a.swf 这种怪东西）
        if (rel.StartsWith("//", StringComparison.Ordinal))
        {
            var scheme = Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var b) ? b.Scheme : "https";
            return $"{scheme}:{rel}";
        }
        if (Uri.TryCreate(rel, UriKind.Absolute, out var absolute)) return absolute.ToString();
        if (string.IsNullOrWhiteSpace(baseUrl)) return rel;
        return Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri)
               && Uri.TryCreate(baseUri, rel, out var joined)
            ? joined.ToString()
            : rel;
    }

    /// <summary>
    /// 取某个网址的父目录（以 / 结尾）——批量下载时用它把相对文件名拼成完整网址。
    /// 对应 Python 的 <c>fromSWFPathGetParentPath</c>。
    /// </summary>
    public static string GetParentUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "";
        var path = uri.AbsolutePath;
        var index = path.LastIndexOf('/');
        var dir = index >= 0 ? path[..(index + 1)] : "/";
        return $"{uri.Scheme}://{uri.Authority}{dir}";
    }

    // ---------------------------------------------------------------- 批量下载的路径校验

    /// <summary>
    /// 校验批量下载里的一行文件路径：去掉 <c>?</c> 后面的部分，含 <c>..</c> 或
    /// <c># [ ] @ ! $ &amp; ' ( ) * + , ; = " &lt; &gt; 空格 | : `</c> 一律判为非法（返回空字符串）。
    /// 对应 Python 的 <c>getBatchDownloadValidName</c>。
    /// </summary>
    public static string SanitizeBatchPath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return "";
        var text = fileName.Trim();
        var query = text.IndexOf('?');
        if (query >= 0) text = text[..query];
        if (text.Contains("..", StringComparison.Ordinal)) return "";
        foreach (var ch in text)
        {
            if ("#[]@!$&'()*+,;=\"<> |:`".IndexOf(ch) >= 0) return "";
        }
        return text;
    }

    /// <summary>
    /// 把「swf 所在目录的网址」与一行相对路径拼成完整网址。
    /// <paramref name="baseUrl"/> 必须以 <c>/</c> 结尾；行首的 <c>/</c> 会被去掉。
    /// 对应 Python 的 <c>BatchDownloadJoinNetPath</c>。
    /// </summary>
    public static string JoinBatchNetPath(string? baseUrl, string? path)
    {
        var safe = SanitizeBatchPath(path);
        if (safe.Length == 0) return "";
        var baseText = baseUrl ?? "";
        if (baseText.Length == 0) return "";
        var rest = safe.Replace('\\', '/');
        rest = rest.TrimStart('/');
        rest = rest.TrimEnd('/'); // 分不清主页是 index.htm 还是 index.html，干脆不补
        return baseText + rest;
    }

    /// <summary>
    /// 把一行相对路径（可能带 %xx 转义）拼成本地绝对路径。含 <c>..</c> 或 Windows 非法字符时返回空字符串。
    /// 对应 Python 的 <c>BatchDownloadJoinLocalPath</c>。
    /// </summary>
    public static string JoinBatchLocalPath(string? localFolder, string? path)
    {
        if (string.IsNullOrWhiteSpace(localFolder)) return "";
        var decoded = UrlDecode(path ?? "").Replace('/', Path.DirectorySeparatorChar);
        var relative = decoded.Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (!IsGoodFileName(relative)) return "";
        return Path.Combine(localFolder, relative);
    }

    /// <summary>是不是一个“能当文件名用”的字符串（禁 <c>..</c> 与 <c>? * " &lt; &gt; | :</c> 和控制字符）。</summary>
    public static bool IsGoodFileName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        foreach (var ch in name)
        {
            if ("?*\"<>|:".IndexOf(ch) >= 0) return false;
            if (ch < 0x20) return false;
        }
        return true;
    }

    /// <summary>宽容的百分号解码：解不开就原样返回。</summary>
    public static string UrlDecode(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch { return value; }
    }

    // ---------------------------------------------------------------- 网页文本解码

    /// <summary>
    /// 网页字节 → 文本。优先按声明（<c>&lt;meta charset&gt;</c> 或响应头）猜，
    /// 猜不到就依次试 UTF-8 / GBK / GB2312，全失败用 UTF-8 宽松解码
    /// （对应 Python 的 <c>decodeWebContent</c>）。
    /// </summary>
    /// <returns>解码后的文本与真正用到的编码名。</returns>
    public static (string Text, string Encoding) DecodeWebContent(byte[]? content, string? declaredCharset = null)
    {
        if (content is null || content.Length == 0) return ("", "utf-8");

        var charset = NormalizeCharset(declaredCharset);
        if (charset is null)
        {
            // 从开头 4KB 里找 <meta charset=...> / <meta http-equiv=... charset=...>
            var head = Encoding.ASCII.GetString(content, 0, Math.Min(content.Length, 4096));
            var match = MetaCharsetPattern().Match(head);
            if (match.Success) charset = NormalizeCharset(match.Groups[1].Value);
        }

        if (charset is not null)
        {
            try { return (charset.GetString(content), charset.WebName); }
            catch { /* 落到下面的猜测 */ }
        }

        foreach (var candidate in new[] { "utf-8", "gbk", "gb2312" })
        {
            try
            {
                var encoding = Encoding.GetEncoding(candidate);
                return (encoding.GetString(content), candidate);
            }
            catch (DecoderFallbackException) { }
            catch (ArgumentException) { } // 本机没装这个代码页
        }

        var fallback = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        return (fallback.GetString(content), "utf-8");
    }

    private static Encoding? NormalizeCharset(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return null;
        var name = charset.Trim().Trim('"', '\'').ToLowerInvariant();
        if (name.Length == 0) return null;
        try { return Encoding.GetEncoding(name); }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 404 判定

    /// <summary>
    /// 下载下来的内容是不是「文件不存在」页（对应 Python 的 <c>IsAnyone404Page</c>）。
    /// 命中就说明服务器回了个 404 页面而不是真文件，自动下载时要把它删掉——
    /// 有些游戏靠“文件不存在”来决定分支。
    /// </summary>
    public static bool LooksLikeNotFoundPage(byte[]? content)
    {
        if (content is null || content.Length == 0 || content.Length > 256 * 1024) return false;
        var text = DecodeWebContent(content).Text;
        return text.Contains("404 Not Found", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Document not found", StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- 4399

    /// <summary>4399 游戏名：优先 <c>game_title='…'</c>，退回 <c>title='…'</c>。</summary>
    public static string Get4399GameName(string? html)
    {
        var text = html ?? "";
        var match = GameTitle4399Pattern().Match(text);
        if (match.Success) return match.Groups[1].Value;
        match = Title4399Pattern().Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>4399 页面上直接给出的 swf 地址（没有则是 null）。</summary>
    public static string? Get4399DirectSwfPath(string? html)
    {
        var text = html ?? "";
        var match = GamePathSwfPattern().Match(text);
        if (match.Success) return To4399Absolute(match.Groups[1].Value);
        match = Src4399SwfPattern().Match(text);
        return match.Success ? "https:" + match.Groups[1].Value : null;
    }

    /// <summary>
    /// 4399 页面上的路径 → 完整地址：本来就是绝对地址就原样返回，
    /// 否则补上 <c>https://sda.4399.com/4399swf</c> 前缀（页面里给的是站内相对路径）。
    /// </summary>
    private static string To4399Absolute(string path)
        => path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || path.StartsWith("//", StringComparison.Ordinal)
            ? path
            : "https://sda.4399.com/4399swf" + path;

    /// <summary>4399 页面上给出的「新网页」地址（游戏藏在 iframe 里时）。</summary>
    public static string? Get4399NewPagePath(string? html)
    {
        var text = html ?? "";
        var match = GamePathHtmlPattern().Match(text);
        if (match.Success) return To4399Absolute(match.Groups[1].Value);
        match = Src4399HtmlPattern().Match(text);
        if (match.Success) return "https:" + match.Groups[1].Value;
        return null;
    }

    /// <summary>新网页里 <c>id="flashgame"</c> 后面的 swf 地址。</summary>
    public static string? Get4399SwfPathInNewPage(string? html, string? newPageUrl)
    {
        var text = html ?? "";
        var anchor = text.IndexOf("id=\"flashgame\"", StringComparison.OrdinalIgnoreCase);
        if (anchor < 0) return null;
        var match = SrcSwfPattern().Match(text[anchor..]);
        return match.Success ? UrlJoin(newPageUrl, match.Groups[1].Value) : null;
    }

    /// <summary>h5wan 游戏的入口：新网页同目录下的 <c>gameIndex.html</c>。</summary>
    public static string Get4399H5WanGamePath(string? newPageUrl)
        => GetParentUrl(newPageUrl) + "gameIndex.html";

    /// <summary>这层新网页是不是 UnityWebPlayer 游戏。</summary>
    public static bool Is4399NewPageUnity(string? html)
    {
        var text = html ?? "";
        return text.Contains("WebPlayer.unity3d", StringComparison.OrdinalIgnoreCase)
            || text.Contains("unityPlayer", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这层新网页是不是 4399 的 h5wan（h5 小游戏外壳）。</summary>
    public static bool Is4399NewPageH5Wan(string? html)
    {
        var text = html ?? "";
        return text.Contains("h5wan", StringComparison.OrdinalIgnoreCase)
            || text.Contains("h5api-core", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>4399 自己的 404 页。</summary>
    public static bool Is4399404Page(string? html)
        => (html ?? "").Contains("<title>404 Not Found</title>", StringComparison.OrdinalIgnoreCase);

    /// <summary>4399 游戏宽高：<c>_w=800,_h=600;</c> 形式，拿不全返回 (-1,-1)。</summary>
    public static (int Width, int Height) Get4399GameWidthHeight(string? html)
        => Pair(Width4399Pattern().Match(html ?? ""), Height4399Pattern().Match(html ?? ""));

    /// <summary>
    /// 把 h5 游戏页面里的官方 h5api 换成内置替代脚本，并删掉写死 <c>document.domain='4399.com'</c> 的语句
    /// （对应 Python 的 <c>Page4399Replace</c>）。
    /// </summary>
    public static string Replace4399Page(string? html, bool replaceH5Api = true)
    {
        var text = html ?? "";
        if (replaceH5Api)
            text = text.Replace("https://h.api.4399.com/h5mini-2.0/h5api-interface.php", "/tool/substiH5WanApi.js");
        return DocumentDomain4399Pattern().Replace(text, "");
    }

    // ---------------------------------------------------------------- 7k7k

    /// <summary>7k7k 游戏名：<c>gameName:"…"</c>。</summary>
    public static string Get7k7kGameName(string? html)
    {
        var match = GameName7k7kPattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>7k7k 页面上直接给出的 swf 地址（可能是相对路径，调用方用 <see cref="UrlJoin"/> 补全）。</summary>
    public static string? Get7k7kDirectSwfPath(string? html)
    {
        var match = GamePath7k7kSwfPattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>7k7k 页面上给出的「新网页」地址（可能是相对路径）。</summary>
    public static string? Get7k7kNewPagePath(string? html)
    {
        var match = GamePath7k7kHtmlPattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>新网页里的 <c>_src_='….swf'</c>（17yy 也共用这一条）。</summary>
    public static string? Get7k7kSwfPathInNewPage(string? html, string? newPageUrl)
    {
        var match = Src7k7kPattern().Match(html ?? "");
        return match.Success ? UrlJoin(newPageUrl, match.Groups[1].Value) : null;
    }

    /// <summary>7k7k 游戏宽高：<c>gamewidth:800,</c> / <c>gameheight:600,</c>。</summary>
    public static (int Width, int Height) Get7k7kGameWidthHeight(string? html)
        => Pair(Width7k7kPattern().Match(html ?? ""), Height7k7kPattern().Match(html ?? ""));

    /// <summary>把 7k7k / 17yy 页面里写死的 <c>document.domain='…'</c> 删掉。</summary>
    public static string Replace7k7k17yyPage(string? html)
        => DocumentDomain7k7kPattern().Replace(html ?? "", "");

    // ---------------------------------------------------------------- 17yy

    /// <summary>17yy 游戏名：<c>m7_gamename="…"</c>，退回 <c>&lt;title&gt;…在线玩"</c>。</summary>
    public static string Get17yyGameName(string? html)
    {
        var text = html ?? "";
        var match = GameName17yyPattern().Match(text);
        if (match.Success) return match.Groups[1].Value;
        match = Title17yyPattern().Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>17yy 游戏分类（拼 swf 地址时要用）：<c>var date="pingtai";</c>。</summary>
    public static string? Get17yyGameCategory(string? html)
    {
        var match = Category17yyPattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>17yy 游戏编号（要拿去问它的 php 接口）。</summary>
    public static int Get17yyGameId(string? html, string? url)
    {
        var match = GameId17yyPattern().Match(html ?? "");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var id)) return id;
        match = UrlId17yyPattern().Match(url ?? "");
        return match.Success && int.TryParse(match.Groups[1].Value, out var id2) ? id2 : -1;
    }

    /// <summary>17yy 新网页里的 swf（样本太少，与 7k7k 同一套规则）。</summary>
    public static string? Get17yySwfPathInNewPage(string? html, string? newPageUrl)
        => Get7k7kSwfPathInNewPage(html, newPageUrl);

    /// <summary>17yy 游戏宽高：<c>flash_w=800;</c> / <c>flash_h=600;</c>。</summary>
    public static (int Width, int Height) Get17yyGameWidthHeight(string? html)
        => Pair(Width17yyPattern().Match(html ?? ""), Height17yyPattern().Match(html ?? ""));

    /// <summary>17yy 的 php 接口返回的相对路径 → 完整 swf 地址。</summary>
    public static string Build17yySwfUrl(string? category, string? latterPath)
        => string.IsNullOrEmpty(category) || string.IsNullOrEmpty(latterPath)
            ? ""
            : $"http://img1.17yy.com/swf/{category}/{latterPath}";

    // ---------------------------------------------------------------- 内部小工具

    private static (int Width, int Height) Pair(Match width, Match height)
    {
        var w = width.Success && int.TryParse(width.Groups[1].Value, out var pw) ? pw : -1;
        var h = height.Success && int.TryParse(height.Groups[1].Value, out var ph) ? ph : -1;
        return w > 0 && h > 0 ? (w, h) : (-1, -1);
    }

    // ---------------------------------------------------------------- 正则（与 Python 版逐条对应）

    [GeneratedRegex(@"/([0-9]+)(_[0-9]+)?\.html?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlNumberPattern();

    [GeneratedRegex(@"^([a-zA-Z])([0-9]+)$")]
    private static partial Regex AppendixPattern();

    [GeneratedRegex(@"charset\s*=\s*[""']?\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaCharsetPattern();

    [GeneratedRegex(@"game_title\s*=\s*'(.*?)'", RegexOptions.Singleline)]
    private static partial Regex GameTitle4399Pattern();

    [GeneratedRegex(@"title\s*=\s*'(.*?)'", RegexOptions.Singleline)]
    private static partial Regex Title4399Pattern();

    [GeneratedRegex(@"_strGamePath\s*=\s*""(.*?\.swf)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex GamePathSwfPattern();

    [GeneratedRegex(@"src='(//sda\.4399\.com/.*?\.swf)'", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Src4399SwfPattern();

    [GeneratedRegex(@"_strGamePath\s*=\s*""(.*?\.html?)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex GamePathHtmlPattern();

    [GeneratedRegex(@"src='(//sda\.4399\.com/.*?\.html?)'", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Src4399HtmlPattern();

    [GeneratedRegex(@"src=""(.*?\.swf)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SrcSwfPattern();

    [GeneratedRegex(@"_w\s*=\s*(\d+)[,;]")]
    private static partial Regex Width4399Pattern();

    [GeneratedRegex(@"_h\s*=\s*(\d+)[,;]")]
    private static partial Regex Height4399Pattern();

    [GeneratedRegex(@"document\.domain\s*=\s*['""]4399\.com['""]\s*[,;]")]
    private static partial Regex DocumentDomain4399Pattern();

    [GeneratedRegex(@"gameName\s*[:=]\s*[""'](.*?)[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex GameName7k7kPattern();

    [GeneratedRegex(@"gamePath:\s*""(.*?\.swf)(?:\?[^""]*)?[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex GamePath7k7kSwfPattern();

    [GeneratedRegex(@"gamePath:\s*""(.*?\.html?)(?:\?[^""]*)?[""']", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex GamePath7k7kHtmlPattern();

    [GeneratedRegex(@"_src_\s*=\s*'(.*?\.swf)'", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Src7k7kPattern();

    [GeneratedRegex(@"gamewidth:\s*(\d+),")]
    private static partial Regex Width7k7kPattern();

    [GeneratedRegex(@"gameheight:\s*(\d+),")]
    private static partial Regex Height7k7kPattern();

    [GeneratedRegex(@"document\.domain\s*=\s*['""](7k7k|17yy)\.com['""]\s*[,;]")]
    private static partial Regex DocumentDomain7k7kPattern();

    [GeneratedRegex(@"m7_gamename\s*=\s*""(.*?)""", RegexOptions.Singleline)]
    private static partial Regex GameName17yyPattern();

    [GeneratedRegex(@"<title>(.*?)在线玩", RegexOptions.Singleline)]
    private static partial Regex Title17yyPattern();

    [GeneratedRegex(@"var\s+date\s*=\s*""(.*?)""", RegexOptions.Singleline)]
    private static partial Regex Category17yyPattern();

    [GeneratedRegex(@"m7_gameid\s*=\s*""([0-9]+)""")]
    private static partial Regex GameId17yyPattern();

    [GeneratedRegex(@"/([0-9]+)\.html?")]
    private static partial Regex UrlId17yyPattern();

    [GeneratedRegex(@"flash_w\s*=\s*(\d+);")]
    private static partial Regex Width17yyPattern();

    [GeneratedRegex(@"flash_h\s*=\s*(\d+);")]
    private static partial Regex Height17yyPattern();
}
