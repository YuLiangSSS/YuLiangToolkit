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
        switch (site)
        {
            case GameSite.Site4399:
                return await Resolve4399Async(trimmed, appendix, ct).ConfigureAwait(false);
            case GameSite.Site7k7k:
                return await Resolve7k7kAsync(trimmed, appendix, ct).ConfigureAwait(false);
            case GameSite.Site17yy:
                return await Resolve17yyAsync(trimmed, appendix, ct).ConfigureAwait(false);
        }

        // 域名不认识：抓一次页面，按内容猜是哪家的结构（原始工具这里直接报“未知站点”，
        // 我们多做一次尝试，但规则还是那三套，认不出来照样明说）
        var probe = await GetTextAsync(trimmed, null, ct).ConfigureAwait(false);
        if (probe.Error.Length > 0) return GameResolveResult.Fail(probe.Error);

        if (GamePageParser.Get4399DirectSwfPath(probe.Text) is not null
            || GamePageParser.Get4399NewPagePath(probe.Text) is not null)
        {
            return await Resolve4399FromHtmlAsync(trimmed, appendix, probe.Text, ct).ConfigureAwait(false);
        }

        return GameResolveResult.Fail(
            "未知站点：本工具只认 4399 / 7k7k / 17yy 的页面结构。其它网站请用「自由下载工具」按说明手工下载。");
    }

    /// <summary>用后缀重新解析（「装入文件夹 / 改为 htm 打开 / 自动识别」用这个）。</summary>
    public async Task<GameResolveResult> ResolveByAppendixAsync(string appendix, CancellationToken ct = default)
    {
        var converted = GamePageParser.AppendixToUrl(appendix);
        if (converted is null) return GameResolveResult.Fail($"后缀「{appendix}」认不出站点（应为 a/b/c + 数字）");
        return await ResolveAsync(converted.Value.Url, ct).ConfigureAwait(false);
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
                var replaced = meta.Site == GameSite.Site4399
                    ? GamePageParser.Replace4399Page(text, _config.Replace4399H5Api)
                    : GamePageParser.Replace7k7k17yyPage(text);
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
