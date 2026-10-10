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
    /// Kongregate 的 frame 地址里有个 JS 变量 <c>channel_id</c>（每局随机生成的会话号），
    /// 抓取时把它换成这个固定串。服务端不校验它的内容（实测随便填也能打开页面）。
    /// </summary>
    private const string ChannelIdPlaceholder = "ylt";

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
        return GameUrlInfo.Describe(trimmed, GetUrlNumber(trimmed));
    }

    /// <summary>
    /// 从游戏页网址里抽出「游戏在站点里的编号」（没有就是空字符串）。
    ///
    /// <para>各站点的编号位置不一样：4399 / 7k7k / 17yy 是 <c>/12345.htm</c>，
    /// ArmorGames 是 <c>/play/14775</c>（或 <c>…-game/14775</c>），
    /// 而 <b>Kongregate 的网址里根本没有编号</b>——它的页面地址是「作者 / 游戏名」，
    /// 编号只写在网页正文的 <c>game_id</c> 里，要等拉到网页才拿得到
    /// （所以这里返回空串，由 <c>ResolveKongregateAsync</c> 解析完再补上后缀）。</para>
    /// </summary>
    public static string GetUrlNumber(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        var trimmed = url.Trim();
        switch (GameUrlInfo.DetectSite(trimmed))
        {
            case GameSite.SiteArmorGames:
                var armor = ArmorGamesIdPattern().Match(trimmed);
                return armor.Success ? armor.Groups[1].Value : "";
            case GameSite.SiteKongregate:
                return "";
        }
        var match = UrlNumberPattern().Match(trimmed);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// 后缀 → 游戏页 URL（「装入文件夹 / 改为 htm 打开 / 自动识别」都靠它重新找到游戏）。
    /// 认不出来返回 null。
    ///
    /// <para>⚠ Kongregate 的后缀（<c>e12345</c>）<b>反推不出页面</b>：
    /// 它的页面地址是「作者 / 游戏名」，编号只在网页正文里，站点也不提供「编号 → 页面」的跳转
    /// （试过 <c>/games/12345</c>、<c>/games/game_id/12345</c>、<c>/games/12345/游戏名</c> 等写法，全是 404，
    /// 见 <see cref="GameSites.CanResolveFromAppendix"/>）。所以这里返回 null，让上层给出人话提示。</para>
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
            'd' => ($"https://armorgames.com/play/{number}", GameSite.SiteArmorGames),
            'e' => null, // Kongregate：编号反推不出页面，见上面的说明
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

    // ---------------------------------------------------------------- ArmorGames

    /// <summary>
    /// ArmorGames 游戏名：优先 <c>og:title</c>，退回 <c>&lt;title&gt;</c>，
    /// 最后把「 - Play on Armor Games」这截尾巴剪掉。
    /// </summary>
    public static string GetArmorGamesGameName(string? html)
    {
        var text = html ?? "";
        var name = MetaContent(text, "og:title");
        if (name.Length == 0)
        {
            var title = TitleTagPattern().Match(text);
            name = title.Success ? title.Groups[1].Value : "";
        }
        return TrimArmorGamesSuffix(name);
    }

    /// <summary>
    /// ArmorGames 页面上 swf 的地址：游戏本体写在
    /// <c>&lt;param name="movie" value="…swf"&gt;</c> 与 <c>&lt;embed src="…swf"&gt;</c> 里
    /// （实测《Strike Force Heroes 2》：<c>https://cache.armorgames.com/files/games/strike-force-heroes--14775.swf</c>）。
    ///
    /// <para>⚠ 文件名没法靠规律拼：有的用两个连字符 <c>名字--编号.swf</c>，有的只有一个
    /// （<c>the-enchanted-cave-2-17682.swf</c>），所以必须从页面里读出来，不能自己拼。</para>
    ///
    /// <para>找不到就返回 null（HTML5 游戏就是这样，页面里压根没有 swf）。</para>
    /// </summary>
    public static string? GetArmorGamesDirectSwfPath(string? html)
    {
        var text = html ?? "";

        // ① 先看 param / embed 这两个正经的加载器标签（属性顺序随便，逐个属性读）
        foreach (Match tag in EmbedLoaderTagPattern().Matches(text))
        {
            var raw = tag.Value;
            if (raw.StartsWith("<param", StringComparison.OrdinalIgnoreCase))
            {
                // <param> 里只有 name="movie" 那条才是本体（其余的 width/quality/flashvars 都不是）
                var name = AttributeOf(raw, "name");
                if (!name.Equals("movie", StringComparison.OrdinalIgnoreCase)) continue;
            }
            var value = AttributeOf(raw, "src");
            if (value.Length == 0) value = AttributeOf(raw, "value");
            if (LooksLikeSwf(value)) return value;
        }

        // ② 退回：整页扫一遍绝对地址，优先 ArmorGames 自己域名下的
        var candidates = AbsoluteSwfPattern().Matches(text)
            .Select(m => m.Value)
            .Where(LooksLikeSwf)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return candidates.FirstOrDefault(u => u.Contains("armorgames.com", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// ArmorGames 游戏宽高：读 <c>&lt;object&gt;</c> / <c>&lt;embed&gt;</c> 上的 width / height。
    /// 优先带 .swf 的那个标签（广告 / 分享按钮也有 object 标签，但不会指向 swf）。
    /// </summary>
    public static (int Width, int Height) GetArmorGamesGameWidthHeight(string? html)
    {
        var text = html ?? "";
        var tags = EmbedLoaderTagPattern().Matches(text).Select(m => m.Value).ToList();

        foreach (var raw in tags.Where(t => LooksLikeSwf(AttributeOf(t, "src")) || LooksLikeSwf(AttributeOf(t, "value"))))
        {
            var (w, h) = SizeOfTag(raw);
            if (w > 0 && h > 0) return (w, h);
        }

        foreach (var raw in tags)
        {
            var (w, h) = SizeOfTag(raw);
            if (w > 0 && h > 0) return (w, h);
        }
        return (-1, -1);
    }

    /// <summary>ArmorGames 的 404 页（页面没了 / 编号写错）。</summary>
    public static bool IsArmorGames404Page(string? html)
        => (html ?? "").Contains("404", StringComparison.Ordinal)
           && (html ?? "").Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string TrimArmorGamesSuffix(string name)
    {
        var trimmed = ArmorGamesTitleSuffixPattern().Replace(name ?? "", "").Trim();
        return trimmed.Length > 0 ? trimmed : (name ?? "").Trim();
    }

    /// <summary>读某个标签上的 width / height（都是正整数才算数）。</summary>
    private static (int Width, int Height) SizeOfTag(string tag)
    {
        var w = AttributeOf(tag, "width");
        var h = AttributeOf(tag, "height");
        return int.TryParse(w, out var pw) && int.TryParse(h, out var ph) ? (pw, ph) : (-1, -1);
    }

    // ---------------------------------------------------------------- Kongregate

    /// <summary>
    /// Kongregate 游戏页 / 嵌入页里的游戏编号（<c>game_id</c>）。拿不到返回空字符串。
    ///
    /// <para>页面里出现的形态有：<c>game_id: '324529'</c>、<c>"game_id":"324529"</c>、
    /// 被 HTML 转义的 <c>&amp;quot;game_id&amp;quot;:&amp;quot;324529&amp;quot;</c>、
    /// 以及嵌入页的 <c>active_user.setGame(324529, …)</c>。前三种用一条正则覆盖，
    /// 最后那种单独兜底。</para>
    /// </summary>
    public static string GetKongregateGameId(string? html)
    {
        var text = html ?? "";
        var match = KongregateGameIdPattern().Match(text);
        if (match.Success) return match.Groups[1].Value;
        match = KongregateGameIdEncodedPattern().Match(text);
        if (match.Success) return match.Groups[1].Value;
        match = KongregateSetGamePattern().Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>Kongregate 游戏名：<c>&lt;title&gt;</c> 去掉「 - Play on Kongregate」这类尾巴。</summary>
    public static string GetKongregateGameName(string? html)
    {
        var match = TitleTagPattern().Match(html ?? "");
        return match.Success ? TrimKongregateSuffix(match.Groups[1].Value) : "";
    }

    /// <summary>
    /// Kongregate 脚本里那句 <c>game_name</c> / <c>game_title</c>（嵌入页 <c>setGame</c> 的参数、
    /// frame 页的 <c>ttgArgs</c>），<b>比标题可靠</b>：标题会带
    /// 「Bloons Super Monkey on Kongregate – Play Now」这类站名尾巴，而且各语言还不一样。
    /// 认不到返回空字符串。
    /// </summary>
    public static string GetKongregateScriptName(string? html)
    {
        var text = html ?? "";
        var match = KongregateGameNamePattern().Match(text);
        if (match.Success && match.Groups[1].Value.Length > 0) return match.Groups[1].Value;
        match = KongregateGameTitlePattern().Match(text);
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// 从游戏页网址算出「嵌入页」地址（<c>…/games/作者/游戏名</c> → <c>…/games/作者/游戏名/embed</c>）。
    /// 真正的 swf 就在嵌入页里，所以这一步是 Kongregate 的必经之路。
    /// 网址里没有 <c>/games/作者/游戏名</c> 这一段就返回 null。
    /// </summary>
    public static string? BuildKongregateEmbedUrl(string? gamePageUrl)
    {
        if (string.IsNullOrWhiteSpace(gamePageUrl)) return null;
        if (!Uri.TryCreate(gamePageUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        if (!KongregateGamePathPattern().IsMatch(uri.AbsolutePath)) return null;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/embed", StringComparison.OrdinalIgnoreCase)) return $"{uri.Scheme}://{uri.Authority}{path}";
        return $"{uri.Scheme}://{uri.Authority}{path}/embed";
    }

    /// <summary>
    /// 游戏页里嵌入页的 iframe 地址（页面自己写着的那个，比我们自己拼更可靠）。
    /// 找不到返回 null。
    /// </summary>
    public static string? GetKongregateEmbedUrl(string? html, string? gamePageUrl)
    {
        foreach (Match tag in IframeTagPattern().Matches(html ?? ""))
        {
            var raw = tag.Value;
            var src = AttributeOf(raw, "src");
            if (src.Length == 0) continue;
            var cls = AttributeOf(raw, "class");
            var isEmbed = cls.Contains("game-embed", StringComparison.OrdinalIgnoreCase)
                          || src.EndsWith("/embed", StringComparison.OrdinalIgnoreCase);
            if (isEmbed) return UrlJoin(gamePageUrl, src);
        }
        return null;
    }

    /// <summary>
    /// 嵌入页里那段 <c>iframeConfig = {…};</c> 的原文（不是严格 JSON：<c>iframe_url</c> 用的是单引号
    /// 拼 JS 变量，所以只能当文本抠出来）。嵌的是 h5 还是 swf、宽高、游戏编号全在里面。
    /// </summary>
    public static string? GetKongregateIframeConfig(string? html)
    {
        var match = KongregateIframeConfigPattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 从 <c>iframeConfig</c> 文本里取一个字段的值（字符串 / 数字都行，<c>null</c> 当成空字符串）。
    ///
    /// <para>字段名是调用方传进来的，没法用 <c>GeneratedRegex</c>，走 <see cref="Regex.Match(string,string,RegexOptions)"/>
    /// 的静态缓存（实际用到的字段就那几个，不会把缓存撑爆）。</para>
    /// </summary>
    public static string GetKongregateConfigValue(string? config, string key)
    {
        var text = config ?? "";
        if (text.Length == 0 || key.Length == 0) return "";
        var name = Regex.Escape(key);

        var quoted = Regex.Match(text, $"[\"']{name}[\"']\\s*:\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);
        if (quoted.Success) return UnescapeJsValue(quoted.Groups[1].Value);

        var plain = Regex.Match(text, $"[\"']{name}[\"']\\s*:\\s*([^,}}]+)", RegexOptions.IgnoreCase);
        if (!plain.Success) return "";
        var value = UnescapeJsValue(plain.Groups[1].Value.Trim());
        return value.Equals("null", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    /// <summary>嵌入页里的游戏宽高（h5 游戏也有，读不到返回 (-1,-1)）。</summary>
    public static (int Width, int Height) GetKongregateGameWidthHeight(string? config)
    {
        var w = GetKongregateConfigValue(config, "game_width");
        var h = GetKongregateConfigValue(config, "game_height");
        return int.TryParse(w, out var pw) && int.TryParse(h, out var ph) && pw > 0 && ph > 0
            ? (pw, ph)
            : (-1, -1);
    }

    /// <summary>
    /// 这个 Kongregate 游戏是不是 Flash（swf）。
    ///
    /// <para>判据取三样，命中一个就算：<c>game_type</c> 是 flash、<c>requires_flash</c> 为真、
    /// 或者 <c>game_host</c> 指向 <c>chat.kongregate.com</c>（Flash 游戏的 swf 就放在那儿；
    /// h5 游戏的 game_host 是 <c>game编号.konggames.com</c>）。</para>
    /// </summary>
    public static bool IsKongregateFlashGame(string? config)
    {
        var text = config ?? "";
        if (text.Length == 0) return false;
        if (GetKongregateConfigValue(text, "game_type").Equals("flash", StringComparison.OrdinalIgnoreCase)) return true;
        if (GetKongregateConfigValue(text, "requires_flash").Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        return GetKongregateConfigValue(text, "game_host").Contains("chat.kongregate.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 嵌入页里那个真正装着游戏的 iframe 地址。
    ///
    /// <para>⚠ 这个字段不能走 <see cref="GetKongregateConfigValue"/>：Flash 游戏的写法是
    /// <c>"iframe_url":'…/frame/' + channel_id + '/?…'</c>，按普通字符串截到第一个引号就断了
    /// （只剩 <c>…/frame/</c>，后面的 <c>?kongregate_host=…</c> 全丢）。所以单独用一条正则
    /// 一路吃到「引号后面紧跟 <c>,</c> 或 <c>}</c>」为止，再把 JS 拼接换成固定串。</para>
    ///
    /// <para><c>channel_id</c> 是每局随机生成的会话号，服务端不校验它的内容（实测随便填也能打开）。</para>
    /// </summary>
    public static string? GetKongregateFrameUrl(string? config)
    {
        var text = config ?? "";
        if (text.Length == 0) return null;
        var match = KongregateIframeUrlRawPattern().Match(text);
        if (!match.Success) return null;
        var url = KongregateChannelIdPattern().Replace(UnescapeJsValue(match.Groups[1].Value), ChannelIdPlaceholder);
        return url.Length > 0 ? url : null;
    }

    /// <summary>拿不到 iframeConfig 时，页面里还有一行 <c>var iframeUrl = '…';</c> 可以用。</summary>
    public static string? GetKongregateIframeUrlVariable(string? html)
    {
        var match = KongregateIframeUrlVariablePattern().Match(html ?? "");
        if (!match.Success) return null;
        var url = KongregateChannelIdPattern().Replace(UnescapeJsValue(match.Groups[1].Value), ChannelIdPlaceholder);
        return url.Length > 0 ? url : null;
    }

    /// <summary>嵌入页里的 <c>var gameType = "flash";</c>（没有就是空字符串）。</summary>
    public static string GetKongregateGameTypeVariable(string? html)
    {
        var match = KongregateGameTypeVariablePattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// 网页里那一行 <c>var swf_location = "…swf";</c>（Kongregate 的 frame 页就是这么写的，
    /// 实测《Bloons Super Monkey》：<c>https://chat.kongregate.com/gamez/0007/2514/live/bloonssupermonkey.swf?…</c>）。
    /// </summary>
    public static string? GetKongregateSwfLocation(string? html)
    {
        var match = KongregateSwfLocationPattern().Match(html ?? "");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// 整页扫一遍真正的游戏 swf。
    ///
    /// <para>⚠ 一定要排掉 Kongregate 自己的 API 库 <c>chat.kongregate.com/flash/API_AS3_*.swf</c>
    /// ——它每个页面都有（<c>api_path</c>），十几万字节，当成游戏下下来是个死循环的壳子。</para>
    /// </summary>
    public static string? GetKongregateSwfPath(string? html)
    {
        var location = GetKongregateSwfLocation(html);
        if (location is not null) return location;

        return AbsoluteSwfPattern().Matches(html ?? "")
            .Select(m => m.Value)
            .Where(LooksLikeSwf)
            .FirstOrDefault(u => !IsKongregateApiSwf(u));
    }

    /// <summary>是不是 Kongregate 的 API 库 swf（不是游戏本体）。</summary>
    public static bool IsKongregateApiSwf(string? url)
    {
        var text = url ?? "";
        return text.Contains("/flash/API_AS3", StringComparison.OrdinalIgnoreCase)
               || text.Contains("API_AS3", StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimKongregateSuffix(string name)
    {
        var trimmed = name ?? "";
        foreach (var pattern in new[]
                 {
                     KongregateTitleSuffixOnPattern(), KongregateTitleSuffixInstantlyPattern(),
                     KongregateTitleSuffixPipePattern(), KongregateTitleSuffixOnNowPattern(),
                 })
        {
            var next = pattern.Replace(trimmed, "").Trim();
            if (next.Length > 0) trimmed = next;
        }
        return trimmed.Trim();
    }

    /// <summary>把 JS 字符串里的转义还原（<c>\u0026</c> → <c>&amp;</c>，<c>\/</c> → <c>/</c>）。</summary>
    private static string UnescapeJsValue(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var text = value.Replace("\\/", "/", StringComparison.Ordinal);
        text = Regex.Replace(text, @"\\u([0-9a-fA-F]{4})",
            m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        // 网页里可能被 HTML 转义过（iframeConfig 出现在 <script> 里时 & 会写成 &amp;）
        return text.Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从网页里读一个 <c>&lt;meta property="…" content="…"&gt;</c>（属性顺序随便写）。
    /// 顺带也认 <c>name=</c>（有些站用 name 代替 property）。
    /// </summary>
    private static string MetaContent(string html, string propertyName)
    {
        foreach (Match tag in HtmlTagPattern().Matches(html ?? ""))
        {
            if (!tag.Value.StartsWith("<meta", StringComparison.OrdinalIgnoreCase)) continue;
            var key = AttributeOf(tag.Value, "property");
            if (key.Length == 0) key = AttributeOf(tag.Value, "name");
            if (key.Equals(propertyName, StringComparison.OrdinalIgnoreCase)) return AttributeOf(tag.Value, "content");
        }
        return "";
    }

    /// <summary>
    /// 读一个标签上的属性值（属性顺序随便写）。
    /// 为什么不用一条“专用正则”：这些页面里同一个标签的属性能换行、能换顺序，
    /// 逐个属性读比给每种写法各写一条正则稳得多。
    /// </summary>
    private static string AttributeOf(string tag, string name)
    {
        foreach (Match attribute in HtmlAttributePattern().Matches(tag ?? ""))
        {
            if (attribute.Groups[1].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return attribute.Groups[2].Value;
        }
        return "";
    }

    /// <summary>这个字符串像不像一个 swf 地址（<c>.swf</c> 可以带查询串）。</summary>
    private static bool LooksLikeSwf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        var query = text.IndexOf('?');
        if (query >= 0) text = text[..query];
        return text.EndsWith(".swf", StringComparison.OrdinalIgnoreCase);
    }

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

    // ---- ArmorGames ----

    /// <summary>ArmorGames 游戏编号：<c>/play/14775</c> 或 <c>…-game/14775</c>（整段数字才算）。</summary>
    [GeneratedRegex(@"/(?:play/)?(\d+)(?=[/?#]|$)")]
    private static partial Regex ArmorGamesIdPattern();

    /// <summary>加载器标签：<c>&lt;param …&gt;</c> 与 <c>&lt;embed …&gt;</c>（<c>[^&gt;]*</c> 保证一个标签一个匹配）。</summary>
    [GeneratedRegex(@"<(?:param|embed)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex EmbedLoaderTagPattern();

    /// <summary><c>&lt;meta&gt;</c> / <c>&lt;iframe&gt;</c> 标签（属性换行也能匹配，所以开 Singleline）。</summary>
    [GeneratedRegex(@"<(?:meta|iframe)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlTagPattern();

    /// <summary>标签里的一个属性：<c>名字="值"</c>（单双引号都认）。</summary>
    [GeneratedRegex(@"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*[""']([^""']*)[""']")]
    private static partial Regex HtmlAttributePattern();

    /// <summary><c>&lt;title&gt;…&lt;/title&gt;</c> 的正文。</summary>
    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTagPattern();

    /// <summary>ArmorGames 标题尾巴：<c> - Play on Armor Games</c>。</summary>
    [GeneratedRegex(@"\s*[-–—|]\s*Play on Armor Games.*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ArmorGamesTitleSuffixPattern();

    /// <summary>网页里的绝对 swf 地址（<c>//host/a.swf?x=1</c> 这种协议相对的也算）。</summary>
    [GeneratedRegex(@"(?:https?:)?//[^""'\s<>\\]+\.swf(?:\?[^""'\s<>\\]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteSwfPattern();

    /// <summary>Kongregate 嵌入页的 iframe 标签。</summary>
    [GeneratedRegex(@"<iframe\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IframeTagPattern();

    // ---- Kongregate ----

    /// <summary><c>game_id: '324529'</c> / <c>"game_id":"324529"</c> / <c>game_id: 324529</c>。</summary>
    [GeneratedRegex(@"game_id[""']?\s*[:=]\s*[""']?(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateGameIdPattern();

    /// <summary>被 HTML 转义过的 <c>&amp;quot;game_id&amp;quot;:&amp;quot;324529&amp;quot;</c>（广告参数里那份）。</summary>
    [GeneratedRegex(@"game_id&quot;\s*:\s*&quot;(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateGameIdEncodedPattern();

    /// <summary>嵌入页的 <c>active_user.setGame(324529, …)</c>。</summary>
    [GeneratedRegex(@"setGame\(\s*(\d+)")]
    private static partial Regex KongregateSetGamePattern();

    /// <summary>游戏页路径：<c>/games/作者/游戏名</c>，前面可能有个语言段（<c>/en</c>、<c>/zh-cn</c>），后面可能有 <c>/embed</c>。</summary>
    [GeneratedRegex(@"^/(?:[a-z]{2}(?:-[a-z]{2})?/)?games/[^/]+/[^/]+(?:/embed)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateGamePathPattern();

    /// <summary>嵌入页里的 <c>iframeConfig = {…};</c>（贪婪到分号，所以用懒惰量词 + 行尾分号）。</summary>
    [GeneratedRegex(@"iframeConfig\s*=\s*(\{.*?\})\s*;", RegexOptions.Singleline)]
    private static partial Regex KongregateIframeConfigPattern();

    /// <summary>frame 地址里那段 JS 拼接：<c>' + channel_id + '</c>（单双引号都认）。</summary>
    [GeneratedRegex(@"[""']\s*\+\s*channel_id\s*\+\s*[""']")]
    private static partial Regex KongregateChannelIdPattern();

    /// <summary>嵌入页里的 <c>var iframeUrl = '…';</c>（iframeConfig 抠不出来时的备用来源）。</summary>
    [GeneratedRegex(@"var\s+iframeUrl\s*=\s*[""'](.*?)[""']", RegexOptions.Singleline)]
    private static partial Regex KongregateIframeUrlVariablePattern();

    /// <summary>
    /// <c>iframeConfig</c> 里的 <c>iframe_url</c> 值：一路吃到「引号后面紧跟 <c>,</c> / <c>}</c>」为止，
    /// 这样 Flash 那种 <c>'…' + channel_id + '…'</c> 的写法也能整段拿到。
    /// </summary>
    [GeneratedRegex(@"[""']iframe_url[""']\s*:\s*[""'](.*?)[""']\s*[,}]", RegexOptions.Singleline)]
    private static partial Regex KongregateIframeUrlRawPattern();

    /// <summary>嵌入页里的 <c>var gameType = "flash";</c>。</summary>
    [GeneratedRegex(@"var\s+gameType\s*=\s*[""']([^""']*)[""']")]
    private static partial Regex KongregateGameTypeVariablePattern();

    /// <summary>frame 页里的 <c>var swf_location = "…swf";</c>——真正的游戏地址。</summary>
    [GeneratedRegex(@"var\s+swf_location\s*=\s*[""']([^""']+\.swf[^""']*)[""']", RegexOptions.Singleline)]
    private static partial Regex KongregateSwfLocationPattern();

    /// <summary>Kongregate 标题尾巴：<c> - Play on Kongregate</c>。</summary>
    [GeneratedRegex(@"\s*[-–—]\s*Play on Kongregate\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateTitleSuffixOnPattern();

    /// <summary>游戏页标题尾巴：<c> – Play Instantly Online | Kongregate</c>。</summary>
    [GeneratedRegex(@"\s*[-–—]\s*Play Instantly Online\s*(?:\|\s*Kongregate)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateTitleSuffixInstantlyPattern();

    /// <summary>标题尾巴：<c> | Kongregate</c>。</summary>
    [GeneratedRegex(@"\s*\|\s*Kongregate\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateTitleSuffixPipePattern();

    /// <summary>
    /// 游戏页的标题尾巴：<c>Bloons Super Monkey on Kongregate – Play Now</c>。
    /// 这条是实测抓出来的：联网验收里名字一度变成「BloonsSuperMonkeyonKongregate–PlayNow」。
    /// </summary>
    [GeneratedRegex(@"\s+on Kongregate\s*(?:[-–—]\s*Play Now)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateTitleSuffixOnNowPattern();

    /// <summary>脚本里的 <c>game_name: "…"</c>（嵌入页 setGame 的参数，键带不带引号都认）。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])[""']?game_name[""']?\s*[:=]\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateGameNamePattern();

    /// <summary>frame 页的 <c>game_title: "…"</c>（<c>var ttgArgs = { game_title: … }</c>）。</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])[""']?game_title[""']?\s*[:=]\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex KongregateGameTitlePattern();
}
