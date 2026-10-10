using YuLiangToolkit.Tools.FlashDownloader.Core;

namespace YuLiangToolkit.Tools.FlashDownloader.Engine;

/// <summary>解析游戏页的结果。</summary>
/// <param name="Meta">解析到的游戏信息（失败时为 null）。</param>
/// <param name="Error">失败原因（成功时为空）。</param>
public sealed record GameResolveResult(GameMeta? Meta, string Error)
{
    public bool Ok => Meta is not null;

    public static GameResolveResult Success(GameMeta meta) => new(meta, "");

    public static GameResolveResult Fail(string error) => new(null, error);
}

/// <summary>下载一个游戏的结果。</summary>
/// <param name="Ok">成功没有。</param>
/// <param name="FileOrFolderName">落盘后的文件名 / 文件夹名（成功时才有）。</param>
/// <param name="Meta">游戏的网络信息。</param>
/// <param name="Error">失败原因。</param>
public sealed record GameDownloadResult(bool Ok, string FileOrFolderName, GameMeta? Meta, string Error)
{
    public static GameDownloadResult Success(string name, GameMeta meta) => new(true, name, meta, "");

    public static GameDownloadResult Fail(string error, GameMeta? meta = null) => new(false, "", meta, error);
}

/// <summary>
/// 「下载游戏」这条主线的引擎：网址 → 游戏信息 → 文件/文件夹 + <c>SWFNetPath.ini</c>。
/// 对应原始 Python 项目的 <c>GameDownloader</c>。
///
/// <para>不依赖 WPF：进度与日志通过回调抛出去，界面层负责显示。</para>
/// </summary>
public sealed class GameDownloader
{
    private readonly FlashHttp _http;
    private readonly FlashDownloaderConfig _config;

    public GameDownloader(FlashHttp http, FlashDownloaderConfig config)
    {
        _http = http;
        _config = config;
    }

    // ---------------------------------------------------------------- 解析

    /// <summary>
    /// 解析一个游戏页网址，得到「swf 在哪儿 / 网页在哪儿 + 游戏名 + 宽高」。
    /// 每种站点一套规则；域名认不出来时还会<b>看一眼页面内容</b>再决定用哪套规则
    /// （镜像站 / 代理站 / 老地址都可能这样，白扔掉太可惜）。都不像就明确报不支持。
    /// </summary>
    public async Task<GameResolveResult> ResolveAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return GameResolveResult.Fail("请输入游戏网址");
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return GameResolveResult.Fail("网址格式不对（要以 http:// 或 https:// 开头）");

        var (appendix, site) = GamePageParser.GetUrlInfo(trimmed);

        // 粘进来的本身就是个 swf（各站 CDN 的直链都算）：不用解析，它就是游戏本体
        if (GamePageParser.GetUrlExtension(trimmed) == ".swf")
            return GameResolveResult.Success(DirectSwfMeta(trimmed, appendix, site));

        switch (site)
        {
            case GameSite.Site4399:
                return await Resolve4399Async(trimmed, appendix, ct).ConfigureAwait(false);
            case GameSite.Site7k7k:
                return await Resolve7k7kAsync(trimmed, appendix, ct).ConfigureAwait(false);
            case GameSite.Site17yy:
                return await Resolve17yyAsync(trimmed, appendix, ct).ConfigureAwait(false);
            case GameSite.SiteArmorGames:
                return await ResolveArmorGamesAsync(trimmed, appendix, ct).ConfigureAwait(false);
            case GameSite.SiteKongregate:
                return await ResolveKongregateAsync(trimmed, appendix, ct).ConfigureAwait(false);
        }

        // 域名不认识：抓一次页面，按内容猜是哪家的结构（原始工具这里直接报“未知站点”，
        // 我们多做一次尝试，但规则还是那几套，认不出来照样明说）
        var probe = await GetTextAsync(trimmed, null, ct).ConfigureAwait(false);
        if (probe.Error.Length > 0) return GameResolveResult.Fail(probe.Error);

        if (GamePageParser.Get4399DirectSwfPath(probe.Text) is not null
            || GamePageParser.Get4399NewPagePath(probe.Text) is not null)
        {
            return await Resolve4399FromHtmlAsync(trimmed, appendix, probe.Text, ct).ConfigureAwait(false);
        }

        // ArmorGames 的结构：页面里带着它 CDN 上的 swf，或者标题上写着 Play on Armor Games
        if (GamePageParser.GetArmorGamesDirectSwfPath(probe.Text) is not null
            || probe.Text.Contains("Play on Armor Games", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveArmorGamesFromHtml(trimmed, appendix, probe.Text);
        }

        // Kongregate 的结构：页面里有 game_id，地址又像 /games/作者/游戏名
        if (GamePageParser.GetKongregateGameId(probe.Text).Length > 0
            && GamePageParser.BuildKongregateEmbedUrl(trimmed) is not null)
        {
            return await ResolveKongregateFromHtmlAsync(trimmed, appendix, probe.Text, ct).ConfigureAwait(false);
        }

        return GameResolveResult.Fail(
            "未知站点：本工具只认 4399 / 7k7k / 17yy / ArmorGames / Kongregate 的页面结构。"
            + "其它网站请用「自由下载工具」按说明手工下载。");
    }

    /// <summary>用后缀重新解析（「装入文件夹 / 改为 htm 打开 / 自动识别」用这个）。</summary>
    public async Task<GameResolveResult> ResolveByAppendixAsync(string appendix, CancellationToken ct = default)
    {
        var text = (appendix ?? "").Trim();
        var converted = GamePageParser.AppendixToUrl(text);
        if (converted is null)
        {
            // Kongregate 的后缀只有编号，站点又没有「编号 → 页面」的跳转，这里只能说实话
            if (text.Length > 0 && GameSites.FromLetter(text[0]) == GameSite.SiteKongregate)
            {
                return GameResolveResult.Fail(
                    $"Kongregate 的后缀「{text}」只记了游戏编号，站点不提供「编号 → 游戏页」的地址，"
                    + "没法自动找回网络路径。请重新粘贴游戏页网址下载一次，"
                    + "或手工把 swf 地址填进这个文件夹的 SWFNetPath.ini。");
            }
            return GameResolveResult.Fail($"后缀「{text}」认不出站点（应为 a/b/c/d/e + 数字）");
        }
        return await ResolveAsync(converted.Value.Url, ct).ConfigureAwait(false);
    }

    /// <summary>直接粘一个 swf 直链时构造的游戏信息（名字退回文件名）。</summary>
    private static GameMeta DirectSwfMeta(string swfUrl, string appendix, GameSite site)
    {
        var name = GamePageParser.ToSafeFileName(
            Path.GetFileNameWithoutExtension(GamePageParser.GetUrlFileName(swfUrl)));
        return new GameMeta
        {
            OriginUrl = swfUrl,
            CleanName = name,
            Appendix = appendix,
            Site = site,
            Kind = GameContentKind.Swf,
            ContentUrl = swfUrl,
            IsNewPage = false,
        };
    }

    private async Task<GameResolveResult> ResolveArmorGamesAsync(string url, string appendix, CancellationToken ct)
    {
        var html = await GetTextAsync(url, null, ct).ConfigureAwait(false);
        if (html.Error.Length > 0) return GameResolveResult.Fail(html.Error);
        return ResolveArmorGamesFromHtml(url, appendix, html.Text);
    }

    /// <summary>
    /// ArmorGames 页面结构的解析主体（域名认识与否都走这里）。
    ///
    /// <para>ArmorGames 的 swf 就明写在页面的 <c>&lt;param name="movie"&gt;</c> / <c>&lt;embed src&gt;</c> 里，
    /// 一下就能拿到；页面里没有 swf 的就是 HTML5 游戏，退回「整页下载 + 网页打开」。</para>
    /// </summary>
    private GameResolveResult ResolveArmorGamesFromHtml(string url, string appendix, string htmlText)
    {
        var name = GamePageParser.ToSafeFileName(GamePageParser.GetArmorGamesGameName(htmlText));
        var (w, h) = GamePageParser.GetArmorGamesGameWidthHeight(htmlText);

        var swf = GamePageParser.GetArmorGamesDirectSwfPath(htmlText);
        if (!string.IsNullOrEmpty(swf))
        {
            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = url,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.SiteArmorGames,
                Width = w,
                Height = h,
                Kind = GameContentKind.Swf,
                ContentUrl = swf,
                IsNewPage = false,
            });
        }

        // 页面里压根没有 swf → HTML5 游戏，只能整页拿下来用网页跑
        return GameResolveResult.Success(new GameMeta
        {
            OriginUrl = url,
            CleanName = name,
            Appendix = appendix,
            Site = GameSite.SiteArmorGames,
            Width = w,
            Height = h,
            Kind = GameContentKind.Htm,
            ContentUrl = url,
            IsNewPage = false,
            PageContent = System.Text.Encoding.UTF8.GetBytes(htmlText),
        });
    }

    private async Task<GameResolveResult> Resolve4399Async(string url, string appendix, CancellationToken ct)
    {
        var html = await GetTextAsync(url, null, ct).ConfigureAwait(false);
        if (html.Error.Length > 0) return GameResolveResult.Fail(html.Error);
        return await Resolve4399FromHtmlAsync(url, appendix, html.Text, ct).ConfigureAwait(false);
    }

    /// <summary>4399 页面结构的解析主体（域名认识与否都走这里）。</summary>
    private async Task<GameResolveResult> Resolve4399FromHtmlAsync(
        string url, string appendix, string htmlText, CancellationToken ct)
    {
        var name = GamePageParser.ToSafeFileName(GamePageParser.Get4399GameName(htmlText));
        var (w, h) = GamePageParser.Get4399GameWidthHeight(htmlText);

        var direct = GamePageParser.Get4399DirectSwfPath(htmlText);
        if (!string.IsNullOrEmpty(direct))
        {
            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = url,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.Site4399,
                Width = w,
                Height = h,
                Kind = GameContentKind.Swf,
                ContentUrl = direct,
                IsNewPage = false,
            });
        }

        var newPage = GamePageParser.Get4399NewPagePath(htmlText);
        if (string.IsNullOrEmpty(newPage))
            return GameResolveResult.Fail("无法找到游戏所在页面（4399 的页面结构可能变了）");

        var newHtml = await GetTextAsync(newPage, url, ct).ConfigureAwait(false);
        if (newHtml.Error.Length > 0) return GameResolveResult.Fail(newHtml.Error);

        var swf = GamePageParser.Get4399SwfPathInNewPage(newHtml.Text, newPage);
        if (!string.IsNullOrEmpty(swf))
        {
            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = url,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.Site4399,
                Width = w,
                Height = h,
                Kind = GameContentKind.Swf,
                ContentUrl = swf,
                IsNewPage = true,
                NewPageUrl = newPage,
            });
        }

        // 不是 swf，只能整页拿下来用网页跑
        if (GamePageParser.Is4399NewPageH5Wan(newHtml.Text) && !GamePageParser.Is4399404Page(newHtml.Text))
        {
            // h5wan 游戏外面还套了一层 gameIndex.html，那层才是真正的主页
            var h5Path = GamePageParser.Get4399H5WanGamePath(newPage);
            var h5Html = await GetTextAsync(h5Path, newPage, ct).ConfigureAwait(false);
            if (h5Html.Error.Length == 0 && !GamePageParser.Is4399404Page(h5Html.Text))
            {
                var replaced = GamePageParser.Replace4399Page(h5Html.Text, _config.Replace4399H5Api);
                return GameResolveResult.Success(new GameMeta
                {
                    OriginUrl = url,
                    CleanName = name,
                    Appendix = appendix,
                    Site = GameSite.Site4399,
                    Width = w,
                    Height = h,
                    Kind = GameContentKind.Htm,
                    ContentUrl = h5Path,
                    IsNewPage = true,
                    NewPageUrl = h5Path,
                    PageContent = System.Text.Encoding.UTF8.GetBytes(replaced),
                });
            }
            // 说是 h5wan 又打不开，退回用新页面本身
        }

        var fallback = GamePageParser.Replace4399Page(newHtml.Text, _config.Replace4399H5Api);
        return GameResolveResult.Success(new GameMeta
        {
            OriginUrl = url,
            CleanName = name,
            Appendix = appendix,
            Site = GameSite.Site4399,
            Width = w,
            Height = h,
            Kind = GameContentKind.Htm,
            ContentUrl = newPage,
            IsNewPage = true,
            NewPageUrl = newPage,
            PageContent = System.Text.Encoding.UTF8.GetBytes(fallback),
        });
    }

    private async Task<GameResolveResult> Resolve7k7kAsync(string url, string appendix, CancellationToken ct)
    {
        // 用户可能粘的是上一级页面，换成真正的游戏页
        var target = url;
        if (target.Contains("7k7k.com/flash/", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Replace("7k7k.com/flash/", "7k7k.com/swf/", StringComparison.OrdinalIgnoreCase);
            (appendix, _) = GamePageParser.GetUrlInfo(target);
        }

        var html = await GetTextAsync(target, null, ct).ConfigureAwait(false);
        if (html.Error.Length > 0) return GameResolveResult.Fail(html.Error);

        var name = GamePageParser.ToSafeFileName(GamePageParser.Get7k7kGameName(html.Text));
        var (w, h) = GamePageParser.Get7k7kGameWidthHeight(html.Text);

        // ⚠ 7k7k 页面上的 gamePath 常常只是一个**加载器**（几十 KB，真正的主 swf 在它旁边，
        // 要靠「关联文件下载器」补）。实测《黑帮跑车终极无敌版》：直接下拿到 17 KB 的 loader，
        // 而新页面里能找到真正的 mainSwfGame.swf。所以这里**优先走新页面**，
        // 只有页面里压根没有新页面时才用那个直接的地址。
        var newPage = GamePageParser.Get7k7kNewPagePath(html.Text);
        if (!string.IsNullOrEmpty(newPage))
        {
            var newPageUrl = GamePageParser.UrlJoin(target, newPage);
            var newHtml = await GetTextAsync(newPageUrl, target, ct).ConfigureAwait(false);
            if (newHtml.Error.Length == 0)
            {
                var swfInPage = GamePageParser.Get7k7kSwfPathInNewPage(newHtml.Text, newPageUrl);
                if (!string.IsNullOrEmpty(swfInPage))
                {
                    return GameResolveResult.Success(new GameMeta
                    {
                        OriginUrl = target,
                        CleanName = name,
                        Appendix = appendix,
                        Site = GameSite.Site7k7k,
                        Width = w,
                        Height = h,
                        Kind = GameContentKind.Swf,
                        ContentUrl = swfInPage,
                        IsNewPage = true,
                        NewPageUrl = newPageUrl,
                    });
                }

                var replacedPage = GamePageParser.Replace7k7k17yyPage(newHtml.Text);
                return GameResolveResult.Success(new GameMeta
                {
                    OriginUrl = target,
                    CleanName = name,
                    Appendix = appendix,
                    Site = GameSite.Site7k7k,
                    Width = w,
                    Height = h,
                    Kind = GameContentKind.Htm,
                    ContentUrl = newPageUrl,
                    IsNewPage = true,
                    NewPageUrl = newPageUrl,
                    PageContent = System.Text.Encoding.UTF8.GetBytes(replacedPage),
                });
            }
        }

        // 页面里没有可用的新页面结构 → 用页面直接给出的 gamePath。
        // ⚠ 7k7k 有不少游戏的这个文件只是个“外壳”，运行时还会去旁边加载别的文件
        // （实测《黑帮跑车终极无敌版》只有 17 KB）。这类游戏请用
        // 「装入文件夹」+「关联文件下载器」把缺的文件补进同一个文件夹。
        var direct = GamePageParser.Get7k7kDirectSwfPath(html.Text);
        if (!string.IsNullOrEmpty(direct))
        {
            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = target,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.Site7k7k,
                Width = w,
                Height = h,
                Kind = GameContentKind.Swf,
                ContentUrl = GamePageParser.UrlJoin(target, direct),
                IsNewPage = false,
            });
        }

        return GameResolveResult.Fail("无法找到游戏所在页面（7k7k 的页面结构可能变了）");
    }

    private async Task<GameResolveResult> Resolve17yyAsync(string url, string appendix, CancellationToken ct)
    {
        var target = url;
        if (target.Contains("17yy.com/f/", StringComparison.OrdinalIgnoreCase)
            && !target.Contains("17yy.com/f/play/", StringComparison.OrdinalIgnoreCase))
        {
            target = target.Replace("17yy.com/f/", "17yy.com/f/play/", StringComparison.OrdinalIgnoreCase);
            (appendix, _) = GamePageParser.GetUrlInfo(target);
        }

        var html = await GetTextAsync(target, null, ct).ConfigureAwait(false);
        if (html.Error.Length > 0) return GameResolveResult.Fail(html.Error);

        var name = GamePageParser.ToSafeFileName(GamePageParser.Get17yyGameName(html.Text));
        var (w, h) = GamePageParser.Get17yyGameWidthHeight(html.Text);

        var category = GamePageParser.Get17yyGameCategory(html.Text);
        if (string.IsNullOrEmpty(category)) return GameResolveResult.Fail("无法获取游戏类型");

        var gameId = GamePageParser.Get17yyGameId(html.Text, target);
        if (gameId < 0) return GameResolveResult.Fail("无法获取游戏 ID");

        // 17yy 的游戏路径要问它的 php 接口（对应 Python 的 PHPTool.callPhpApi）
        var (data, error) = await _http.PostFormForJsonAsync(
            "http://www.17yy.com/e/payapi/vip_ajax.php",
            new Dictionary<string, string> { ["action"] = "getStatus", ["id"] = gameId.ToString() },
            target,
            ct).ConfigureAwait(false);
        if (data is null) return GameResolveResult.Fail($"php 接口没返回数据：{error}");

        var latterPath = FlashHttp.DigString(data, "", "data", "game_path");
        if (latterPath.Length == 0)
            return GameResolveResult.Fail("php 未返回游戏路径，可能是 VIP 游戏。若不是请重试");

        var gotPath = GamePageParser.Build17yySwfUrl(category, latterPath);
        var ext = GamePageParser.GetUrlExtension(gotPath);
        if (ext == ".swf")
        {
            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = target,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.Site17yy,
                Width = w,
                Height = h,
                Kind = GameContentKind.Swf,
                ContentUrl = gotPath,
                IsNewPage = false,
            });
        }

        if (ext is ".htm" or ".html")
        {
            var pageHtml = await GetTextAsync(gotPath, target, ct).ConfigureAwait(false);
            if (pageHtml.Error.Length > 0) return GameResolveResult.Fail(pageHtml.Error);

            var swf = GamePageParser.Get17yySwfPathInNewPage(pageHtml.Text, gotPath);
            if (!string.IsNullOrEmpty(swf))
            {
                return GameResolveResult.Success(new GameMeta
                {
                    OriginUrl = target,
                    CleanName = name,
                    Appendix = appendix,
                    Site = GameSite.Site17yy,
                    Width = w,
                    Height = h,
                    Kind = GameContentKind.Swf,
                    ContentUrl = swf,
                    IsNewPage = true,
                    NewPageUrl = gotPath,
                });
            }

            var replaced = GamePageParser.Replace7k7k17yyPage(pageHtml.Text);
            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = target,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.Site17yy,
                Width = w,
                Height = h,
                Kind = GameContentKind.Htm,
                ContentUrl = gotPath,
                IsNewPage = true,
                NewPageUrl = gotPath,
                PageContent = System.Text.Encoding.UTF8.GetBytes(replaced),
            });
        }

        return GameResolveResult.Fail($"未知类型的路径：{gotPath}");
    }

    // ---------------------------------------------------------------- Kongregate

    /// <summary>
    /// Kongregate 要比别的站点多走两步，因为它的 swf <b>不写在游戏页上</b>：
    /// <list type="number">
    ///   <item>游戏页 <c>…/games/作者/游戏名</c> → 拿游戏名、编号（<c>game_id</c>）与嵌入页地址；</item>
    ///   <item>嵌入页 <c>…/embed</c> → 拿 <c>iframeConfig</c>（是不是 Flash、宽高、frame 地址）；</item>
    ///   <item>frame 页 → 里面那行 <c>var swf_location = "…swf"</c> 才是真正的游戏本体。</item>
    /// </list>
    /// h5 游戏这一步就到此为止，退回「整页下载 + 网页打开」。
    /// </summary>
    private async Task<GameResolveResult> ResolveKongregateAsync(string url, string appendix, CancellationToken ct)
    {
        // 用户粘的可能就是嵌入页（…/embed），先去掉尾巴当作游戏页
        var pageUrl = NormalizeKongregatePageUrl(url);
        var html = await GetTextAsync(pageUrl, null, ct).ConfigureAwait(false);
        if (html.Error.Length > 0) return GameResolveResult.Fail(html.Error);
        return await ResolveKongregateFromHtmlAsync(pageUrl, appendix, html.Text, ct).ConfigureAwait(false);
    }

    /// <summary>Kongregate 页面结构的解析主体（域名认识与否都走这里）。</summary>
    private async Task<GameResolveResult> ResolveKongregateFromHtmlAsync(
        string pageUrl, string appendix, string pageHtml, CancellationToken ct)
    {
        var name = KongregateNameOf(pageHtml);

        // Kongregate 的网址里没有编号，编号只在网页正文里 —— 解析到了就补进后缀
        var gameId = GamePageParser.GetKongregateGameId(pageHtml);
        if (appendix.Length == 0 && gameId.Length > 0)
            appendix = GameSites.ToLetter(GameSite.SiteKongregate) + gameId;

        var embedUrl = GamePageParser.GetKongregateEmbedUrl(pageHtml, pageUrl)
                       ?? GamePageParser.BuildKongregateEmbedUrl(pageUrl);
        if (string.IsNullOrEmpty(embedUrl))
            return GameResolveResult.Fail("认不出 Kongregate 的游戏页地址（应该形如 …/games/作者/游戏名）");

        var embed = await GetTextAsync(embedUrl, pageUrl, ct).ConfigureAwait(false);
        if (embed.Error.Length > 0) return GameResolveResult.Fail($"打不开游戏嵌入页：{embed.Error}");

        var config = GamePageParser.GetKongregateIframeConfig(embed.Text);
        var frameUrl = GamePageParser.GetKongregateFrameUrl(config)
                       ?? GamePageParser.GetKongregateIframeUrlVariable(embed.Text);
        if (config is null && frameUrl is null)
            return GameResolveResult.Fail("Kongregate 的嵌入页结构变了（既没有 iframeConfig，也没有 iframeUrl）");

        var (w, h) = GamePageParser.GetKongregateGameWidthHeight(config);
        if (name.Length == 0) name = KongregateNameOf(embed.Text);

        var isFlash = GamePageParser.IsKongregateFlashGame(config)
                      || GamePageParser.GetKongregateGameTypeVariable(embed.Text)
                          .Equals("flash", StringComparison.OrdinalIgnoreCase);

        if (isFlash)
        {
            // ① 嵌入页自己给了 swf（alternate_game_file_url），或者 iframe_url 直接就是 swf
            var swf = FirstSwfUrl(GamePageParser.GetKongregateConfigValue(config, "alternate_game_file_url"), frameUrl);
            // ② 再看嵌入页正文里有没有（会排掉 Kongregate 自己的 API swf）
            swf ??= GamePageParser.GetKongregateSwfPath(embed.Text);

            // ③ 真正的地址写在 frame 页里，去把它抠出来
            if (swf is null)
            {
                if (frameUrl is null)
                    return GameResolveResult.Fail("Kongregate 说这是 Flash 游戏，但嵌入页里没有可用的 frame 地址");

                var frame = await GetTextAsync(frameUrl, embedUrl, ct).ConfigureAwait(false);
                if (frame.Error.Length > 0) return GameResolveResult.Fail($"打不开游戏 frame 页：{frame.Error}");
                swf = GamePageParser.GetKongregateSwfPath(frame.Text);
                if (name.Length == 0) name = KongregateNameOf(frame.Text);
            }

            if (swf is null)
                return GameResolveResult.Fail("Kongregate 说这是 Flash 游戏，但没能在页面里找到 swf 地址");

            return GameResolveResult.Success(new GameMeta
            {
                OriginUrl = pageUrl,
                CleanName = name,
                Appendix = appendix,
                Site = GameSite.SiteKongregate,
                Width = w,
                Height = h,
                Kind = GameContentKind.Swf,
                ContentUrl = swf,
                // 把游戏页记成「新页面」：换 Referer 要用它，ini 里也留个能找到家的线索
                IsNewPage = true,
                NewPageUrl = pageUrl,
            });
        }

        // 不是 Flash：h5 游戏。iframe_url 指向真正的游戏页（在 game编号.konggames.com 上），
        // 拿它比拿外层的介绍页有用得多（关联文件下载器能顺着它把资源补下来）。
        var gameUrl = frameUrl;
        var page = pageHtml;
        if (!string.IsNullOrEmpty(gameUrl) && !gameUrl.Equals(pageUrl, StringComparison.OrdinalIgnoreCase))
        {
            var inner = await GetTextAsync(gameUrl, embedUrl, ct).ConfigureAwait(false);
            if (inner.Error.Length == 0) page = inner.Text;
            else gameUrl = pageUrl; // 内层打不开就退回外层页面，至少还能留个记录
        }
        else
        {
            gameUrl = pageUrl;
        }

        return GameResolveResult.Success(new GameMeta
        {
            OriginUrl = pageUrl,
            CleanName = name,
            Appendix = appendix,
            Site = GameSite.SiteKongregate,
            Width = w,
            Height = h,
            Kind = GameContentKind.Htm,
            ContentUrl = gameUrl,
            IsNewPage = false,
            PageContent = System.Text.Encoding.UTF8.GetBytes(page),
        });
    }

    /// <summary>
    /// Kongregate 的游戏名：先看脚本里的 <c>game_name</c> / <c>game_title</c>，再退回 <c>&lt;title&gt;</c>。
    ///
    /// <para>⚠ 只认标题会踩坑：游戏页标题是「Bloons Super Monkey on Kongregate – Play Now」，
    /// 不剪干净的话文件名会变成 <c>BloonsSuperMonkeyonKongregate–PlayNow_e72514.swf</c>
    /// （联网验收里抓出来的）。</para>
    /// </summary>
    private static string KongregateNameOf(string html)
    {
        var raw = GamePageParser.GetKongregateScriptName(html);
        if (raw.Length == 0) raw = GamePageParser.GetKongregateGameName(html);
        return GamePageParser.ToSafeFileName(raw);
    }

    /// <summary>去掉 Kongregate 地址尾巴上的 <c>/embed</c>（用户可能直接粘嵌入页）。</summary>
    private static string NormalizeKongregatePageUrl(string url)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)) return trimmed;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/embed", StringComparison.OrdinalIgnoreCase)) path = path[..^"/embed".Length];
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>参数里第一个像 swf 地址的（其余情况返回 null）。</summary>
    private static string? FirstSwfUrl(params string?[] candidates)
        => candidates.FirstOrDefault(c => GamePageParser.GetUrlExtension(c) == ".swf");

    // ---------------------------------------------------------------- 下载

    /// <summary>
    /// 把解析好的游戏下载到 <paramref name="library"/> 目录：
    /// <list type="bullet">
    ///   <item>swf → <c>游戏名_a123.swf</c>（散放，可以直接双击玩）；</item>
    ///   <item>网页 → <c>游戏名_a123h/</c> 目录，里面是 <c>游戏名_a123.htm</c> + <c>SWFNetPath.ini</c>
    ///         （多文件游戏请接着用「关联文件下载器」补文件）。</item>
    /// </list>
    /// </summary>
    /// <param name="library">游戏目录。</param>
    /// <param name="meta">解析结果。</param>
    /// <param name="onStep">进度回调（给日志用）。</param>
    public async Task<GameDownloadResult> DownloadAsync(
        GamesLibrary library,
        GameMeta meta,
        Action<string>? onStep = null,
        CancellationToken ct = default)
    {
        if (!meta.IsValid) return GameDownloadResult.Fail("游戏信息不完整，无法下载", meta);
        library.EnsureCreated();

        var baseName = meta.DisplayNameOrFallback();
        var appendix = meta.Appendix.Length > 0 ? meta.Appendix : "z0";

        if (meta.Kind == GameContentKind.Swf)
        {
            // 单个 swf：散放在游戏目录里，命名为 游戏名_a123.swf
            var name = GamesLibrary.BuildUniqueGameName(baseName, appendix,
                candidate => File.Exists(Path.Combine(library.Root, candidate + ".swf")));
            var target = Path.Combine(library.Root, name + ".swf");

            onStep?.Invoke($"正在下载 SWF：{meta.ContentUrl}");
            var result = await _http.DownloadToFileAsync(meta.ContentUrl, target, meta.Referer, false, ct)
                .ConfigureAwait(false);
            if (!result.Ok)
            {
                TryDelete(target);
                return GameDownloadResult.Fail($"找到 SWF 路径但下载失败：{result.Message}", meta);
            }

            return GameDownloadResult.Success(name + ".swf", meta);
        }

        if (meta.Kind == GameContentKind.Htm)
        {
            // 网页游戏：单独放一个文件夹（避免它的资源文件和别的游戏打架）
            var name = GamesLibrary.BuildUniqueGameName(baseName, appendix,
                candidate => Directory.Exists(Path.Combine(library.Root, GamesLibrary.FolderNameForHtm(candidate))));
            var folderName = GamesLibrary.FolderNameForHtm(name);
            var folder = Path.Combine(library.Root, folderName);
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                return GameDownloadResult.Fail($"无法创建文件夹 {folder}：{ex.Message}", meta);
            }

            var ini = new SwfNetPathIni
            {
                FilePath = Path.Combine(folder, "SWFNetPath.ini"),
                FolderExt = ".htm",
                Kind = GameFolderKind.Htm,
                NetPath = meta.NewPageUrl.Length > 0 ? meta.NewPageUrl : meta.ContentUrl,
                Width = meta.Width,
                Height = meta.Height,
            };
            try
            {
                ini.Save();
            }
            catch (Exception ex)
            {
                return GameDownloadResult.Fail($"写入 SWFNetPath.ini 失败：{ex.Message}", meta);
            }

            byte[]? page = meta.PageContent;
            if (page is null)
            {
                onStep?.Invoke($"正在下载网页：{meta.ContentUrl}");
                var fetched = await _http.GetBytesAsync(meta.ContentUrl, meta.Referer, ct).ConfigureAwait(false);
                if (!fetched.Ok || fetched.Content is null)
                    return GameDownloadResult.Fail($"下载网页失败：{fetched.Message}", meta);

                var text = GamePageParser.DecodeWebContent(fetched.Content, fetched.ContentType).Text;
                var replaced = meta.Site switch
                {
                    GameSite.Site4399 => GamePageParser.Replace4399Page(text, _config.Replace4399H5Api),
                    GameSite.Site7k7k or GameSite.Site17yy => GamePageParser.Replace7k7k17yyPage(text),
                    // ArmorGames / Kongregate 的页面里没有要摘掉的站内脚本，原样保存
                    _ => text,
                };
                page = System.Text.Encoding.UTF8.GetBytes(replaced);
            }

            var written = library.WriteMainPage(folderName, name, page);
            if (!written.Ok) return GameDownloadResult.Fail(written.Message, meta);

            return GameDownloadResult.Success(folderName, meta);
        }

        return GameDownloadResult.Fail($"未知的内容类型：{meta.Kind}", meta);
    }

    /// <summary>先解析再下载（界面上的「下载」按钮走这条）。</summary>
    public async Task<GameDownloadResult> ResolveAndDownloadAsync(
        GamesLibrary library,
        string url,
        Action<string>? onStep = null,
        CancellationToken ct = default)
    {
        onStep?.Invoke($"正在解析游戏页：{url}");
        var resolved = await ResolveAsync(url, ct).ConfigureAwait(false);
        if (!resolved.Ok || resolved.Meta is null) return GameDownloadResult.Fail(resolved.Error);

        onStep?.Invoke($"已解析：{resolved.Meta.Describe()}");
        return await DownloadAsync(library, resolved.Meta, onStep, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>取网页文本（顺便把编码问题解决掉）。</summary>
    private async Task<(string Text, byte[]? Bytes, string Error)> GetTextAsync(
        string url, string? referer, CancellationToken ct)
    {
        var result = await _http.GetBytesAsync(url, referer, ct).ConfigureAwait(false);
        if (!result.Ok || result.Content is null || result.Content.Length == 0)
            return ("", null, $"下载失败：{result.Message}（{url}）");
        var (text, _) = GamePageParser.DecodeWebContent(result.Content, result.ContentType);
        return (text, result.Content, "");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
