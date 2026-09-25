using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using YuLiangToolkit.Tools.FlashDownloader.Core;

namespace YuLiangToolkit.Tools.FlashDownloader.Web;

/// <summary>
/// 内置的网页模板资源（编译进 dll，首次运行时释放到
/// <c>Data\flash-downloader\tools\</c>，之后用户想改就改本地文件）。
///
/// <para>三个文件：</para>
/// <list type="bullet">
///   <item><c>frame.htm</c>：网页游戏的播放页（有背景图、重玩 / 放大 / 缩小 / 全屏、四种配色）；</item>
///   <item><c>FlashPage.htm</c>：把「无新页面的 swf」改成网页打开时用的加载页（内容占位符
///         <c>__YLT_GAME__</c> 会被替换成真实的 swf 文件名）；</item>
///   <item><c>h5api-replacement.js</c>：4399 h5 游戏的替代 API（把广告变成 0 秒、登录/排行直接空实现）。</item>
/// </list>
/// </summary>
public static class FlashWebAssets
{
    /// <summary>播放页文件名。</summary>
    public const string FrameFileName = "frame.htm";

    /// <summary>跨域替代 API 的存放目录名（相对工具目录）。</summary>
    public const string ToolSubDirectory = "tool";

    /// <summary>跨域替代 API 的落地文件名（服务器上就是 <c>/tool/substiH5WanApi.js</c>）。</summary>
    public const string H5ApiFileName = "substiH5WanApi.js";

    /// <summary>Flash 加载页文件名。</summary>
    public const string FlashPageFileName = "FlashPage.htm";

    /// <summary>占位符：加载页里会被替换成真实的 swf 文件名。</summary>
    public const string GamePlaceholder = "__YLT_GAME__";

    /// <summary>启动页大标题的占位符。</summary>
    public const string TitlePlaceholder = "__YLT_TITLE__";

    /// <summary>三个模板的（资源名 → 落地相对路径）。</summary>
    private static readonly (string Resource, string RelativePath)[] Templates =
    {
        ("YuLiangToolkit.Tools.FlashDownloader.Web.frame.htm", FrameFileName),
        ("YuLiangToolkit.Tools.FlashDownloader.Web.FlashPage.htm", FlashPageFileName),
        ("YuLiangToolkit.Tools.FlashDownloader.Web.h5api-replacement.js",
            Path.Combine(ToolSubDirectory, H5ApiFileName)),
    };

    /// <summary>读取嵌入资源（失败 / 不存在返回 null）。</summary>
    public static string? ReadTemplate(string resourceName)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>播放页内容。</summary>
    public static string FrameHtml() => ReadTemplate("YuLiangToolkit.Tools.FlashDownloader.Web.frame.htm") ?? FallbackFrame();

    /// <summary>替代 API 内容。</summary>
    public static string H5ApiScript() => ReadTemplate("YuLiangToolkit.Tools.FlashDownloader.Web.h5api-replacement.js") ?? FallbackH5Api();

    /// <summary>
    /// 生成「无新页面的 swf」用的加载页：把占位符换成真实文件名。
    /// </summary>
    public static string BuildFlashPage(string gameFileName, string title)
    {
        var template = ReadTemplate("YuLiangToolkit.Tools.FlashDownloader.Web.FlashPage.htm") ?? FallbackFlashPage();
        return template
            .Replace(GamePlaceholder, gameFileName)
            .Replace(TitlePlaceholder, title);
    }

    /// <summary>
    /// 把三个模板释放到 <paramref name="toolsDirectory"/>（<b>已存在的文件不覆盖</b>，
    /// 所以用户手改过的模板不会被程序冲掉）。
    /// </summary>
    /// <returns>实际写出去的文件数（0 = 全都已存在）。</returns>
    public static int EnsureTemplates(string toolsDirectory)
    {
        var written = 0;
        try
        {
            Directory.CreateDirectory(toolsDirectory);
            foreach (var (resource, relative) in Templates)
            {
                var target = Path.Combine(toolsDirectory, relative);
                if (File.Exists(target)) continue;
                var content = ReadTemplate(resource);
                if (content is null) continue;
                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(target, content, new UTF8Encoding(false));
                written++;
            }
        }
        catch { /* 只读环境忽略：服务器会退回内存里的模板 */ }
        return written;
    }

    // ---------------------------------------------------------------- 内置兜底（资源没编进去时也不至于白屏）

    private static string FallbackFrame() => """
        <!DOCTYPE html>
        <html lang="zh-CN"><head><meta charset="UTF-8"><title>小游戏加载中…</title>
        <style>body{background:#161616;color:#eee;font-family:"Microsoft YaHei UI",sans-serif;margin:0;padding:24px}
        #gameBox{width:800px;height:600px;margin:0 auto;background:#000;position:relative;overflow:hidden}
        iframe{border:0;width:100%;height:100%}#loader{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;font-size:20px}</style>
        </head><body>
        <div id="gameBox"><div id="loader">准备中</div><iframe id="gameFrame" scrolling="no" style="display:none"></iframe></div>
        <script>
        const p=new URLSearchParams(location.search),game=p.get('game');
        const w=parseInt(p.get('width'))||800,h=parseInt(p.get('height'))||600;
        document.getElementById('gameBox').style.width=w+'px';
        document.getElementById('gameBox').style.height=h+'px';
        const f=document.getElementById('gameFrame'),l=document.getElementById('loader');
        if(!game){l.textContent='缺少 game 参数';}else{
          const path=`games/${game}h/${game}.htm`;
          fetch(path,{method:'HEAD'}).then(r=>{if(!r.ok)throw 0;l.style.display='none';f.src=path;f.style.display='block';})
          .catch(()=>{l.textContent='游戏文件不存在，请先用本工具下载';});
        }
        </script></body></html>
        """;

    private static string FallbackFlashPage() => $"""
        <!DOCTYPE HTML PUBLIC "-//W3C//DTD HTML 4.0 Transitional//EN">
        <html><head><meta content="text/html; charset=UTF-8" http-equiv="Content-Type">
        <title>{TitlePlaceholder}</title></head>
        <body bottommargin="0" leftmargin="0" topmargin="0" rightmargin="0" scroll="no"
              oncontextmenu="return false" ondragstart="return false" onselectstart="return false">
        <p align="center" style="padding:0px;margin:0px;">
        <object id="flashgame" classid="clsid:D27CDB6E-AE6D-11cf-96B8-444553540000" width="100%" height="100%">
        <param name="movie" value="{GamePlaceholder}" /><param name="quality" value="high" />
        <param name="allowScriptAccess" value="always" /><param name="allowNetworking" value="all" />
        <embed id="flashgame1" src="{GamePlaceholder}" quality="high" type="application/x-shockwave-flash"
               width="100%" height="100%" allowScriptAccess="always" allowNetworking="all" />
        </object></p></body></html>
        """;

    private static string FallbackH5Api() => """
        // 4399 h5 游戏的替代 API：把广告变 0 秒，登录 / 排行 / 存档都空实现。
        window.h5api = window.h5api || {};
        ['initGame','progress','submitScore','getRank','playYlhAd','playInterstitialAd','share','login',
         'getUserAvatar','getUserSmallAvatar','getUserBigAvatar','getUserLargeAvatar','submitRanking',
         'submitRankScore','getMyRanking','getRanking','showRanking','showRankList','getNearRanking',
         'checkWord','showRecommend','save','gameMode','showGuide'].forEach(function(name){
          window.h5api[name]=function(){ console.warn('试图调用替代 api: '+name); };
        });
        window.h5api.canPlayAd=function(cb){ if(cb)cb({canPlayAd:true}); return true; };
        window.h5api.playAd=function(cb){ if(cb){cb({code:10000,message:'开始播放'});cb({code:10001,message:'播放结束'});} };
        window.h5api.isLogin=function(){ return false; };
        window.h5api.checkAPI=function(){ console.warn('正在使用内置的替代 api，许多功能将无法使用'); };
        window.h5api.checkAPI();
        """;
}

/// <summary>
/// 本地 HTTP 服务器：把游戏目录架在 <c>http://localhost:端口/</c> 上，
/// 并且<b>拦截游戏对不存在文件的请求，先去网上把它下下来再答复</b>——
/// 这就是「自动下载所需文件」的原理（对应原始 Python 项目的 <c>PortServer</c> + <c>AutoDownloader</c>）。
///
/// <para>URL 约定（与原始工具一致）：</para>
/// <list type="bullet">
///   <item><c>/</c> → <c>Data\flash-downloader\tools\</c>（播放页、替代 API、图标、背景图）；</item>
///   <item><c>/games/…</c> → 游戏目录（<c>/games/游戏名_a123h/游戏名_a123.htm</c>）。</item>
/// </list>
///
/// <para>注意：端口只在程序开着的时候有效——关掉本工具，<c>http://localhost:1055/</c> 就打不开了。</para>
/// </summary>
public sealed partial class FlashWebServer : IDisposable
{
    /// <summary>被占用时最多往后试几个端口。</summary>
    public const int MaxPortAttempts = 100;

    private readonly GamesLibrary _library;
    private readonly string _toolsDirectory;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, Task<AutoDownloadOutcome>> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloadGate = new(4, 4);

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public FlashWebServer(GamesLibrary library, string toolsDirectory, Action<string>? log = null)
    {
        _library = library;
        _toolsDirectory = toolsDirectory;
        _log = log;
    }

    /// <summary>实际监听的端口（没启动是 0）。</summary>
    public int Port { get; private set; }

    /// <summary>是不是在跑。</summary>
    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>服务器的根地址，例如 <c>http://localhost:1055/</c>。</summary>
    public string BaseUrl => Port > 0 ? $"http://localhost:{Port}/" : "";

    /// <summary>当前「自动下载所需文件」针对的游戏（null = 没在下载）。</summary>
    public AutoDownloadSession? Session { get; private set; }

    /// <summary>启动时用的起始端口（被占用就往后试）。</summary>
    public int StartPort { get; private set; }

    /// <summary>启动失败的原因（成功时为空）。</summary>
    public string LastError { get; private set; } = "";

    /// <summary>HTTP 文件下载器（自动下载缺失文件用）。</summary>
    public Func<AutoDownloadFetch, CancellationToken, Task<FetchOutcome>>? FileFetcher { get; set; }

    /// <summary>一次取文件的简化结果。</summary>
    /// <param name="Ok">拿到了没有。</param>
    /// <param name="NotFound">是不是 404。</param>
    /// <param name="Message">失败原因。</param>
    public readonly record struct FetchOutcome(bool Ok, bool NotFound, string Message);

    /// <summary>
    /// 「自动下载」交给上层去取一个文件的全部信息。
    /// 本地路径由服务器算好（它才知道游戏文件夹在哪、相对路径是什么），上层只管下载。
    /// </summary>
    /// <param name="Url">要下载的完整网址。</param>
    /// <param name="Referer">上一级页面（防盗链）。</param>
    /// <param name="LocalFullPath">要写到的本地绝对路径。</param>
    /// <param name="RelativePath">相对游戏文件夹的路径（记日志用）。</param>
    public sealed record AutoDownloadFetch(string Url, string Referer, string LocalFullPath, string RelativePath);

    /// <summary>
    /// 启动服务器。已经在跑就直接返回 true。找不到可用端口返回 false（<see cref="LastError"/> 有原因）。
    /// </summary>
    public bool Start(int startPort = 1055)
    {
        if (IsRunning) return true;
        StartPort = Math.Clamp(startPort, 1024, 65000);
        LastError = "";

        for (var offset = 0; offset < MaxPortAttempts; offset++)
        {
            var port = StartPort + offset;
            if (port > 65000) break;
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            try
            {
                listener.Start();
            }
            catch (Exception ex)
            {
                listener.Close();
                LastError = ex.Message;
                continue; // 端口被占用，试下一个
            }

            _listener = listener;
            Port = port;
            _cts = new CancellationTokenSource();
            _log?.Invoke($"本地服务器已启动：{BaseUrl}（只有本工具开着时能访问）");
            _loop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            return true;
        }

        _log?.Invoke($"无法在 {StartPort} 附近找到可用端口：{LastError}");
        return false;
    }

    /// <summary>停服务器。</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        Port = 0;
        Session = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _log?.Invoke("本地服务器已停止");
    }

    /// <summary>
    /// 让服务器去「自动下载」某个游戏缺的文件。
    /// </summary>
    /// <param name="folderName">游戏文件夹名（如 <c>黄金矿工_a123h</c>）。</param>
    /// <param name="netBaseUrl">swf / 网页所在目录的网址（批量下载的基地址）。</param>
    /// <param name="referer">上一级页面。</param>
    /// <param name="mainPageRelativeName">游戏主页相对文件夹的路径（等于它的请求不算“缺文件”）。</param>
    public AutoDownloadSession BeginSession(string folderName, string netBaseUrl, string referer, string mainPageRelativeName)
    {
        var session = new AutoDownloadSession
        {
            FolderName = folderName,
            NetBaseUrl = netBaseUrl,
            Referer = referer,
            MainPageRelativeName = mainPageRelativeName,
            FolderRelaBase = "/games/" + folderName + "h/", // 服务器映射时会先做百分号解码，这里别再转义一次
            Enabled = true,
        };
        Session = session;
        _log?.Invoke($"开始记录并自动下载「{folderName}」需要的文件（在游戏里点“重玩”或刷新网页会触发下载）");
        return session;
    }

    /// <summary>结束自动下载（不清空已下载列表，只是不再下新的）。</summary>
    public void EndSession()
    {
        if (Session is null) return;
        Session.Enabled = false;
        _log?.Invoke("已停止自动下载");
    }

    // ---------------------------------------------------------------- 请求处理

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                break; // 停止时 GetContextAsync 会抛，正常退出
            }

            _ = Task.Run(() =>
            {
                try { Handle(context); }
                catch (Exception ex) { _log?.Invoke($"服务器处理请求出错：{ex.Message}"); }
            }, CancellationToken.None);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var request = context.Request;
        var method = request.HttpMethod?.ToUpperInvariant() ?? "GET";
        var rawPath = request.Url?.AbsolutePath ?? "/";

        if (method is not ("GET" or "HEAD"))
        {
            Respond(context, 405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("只支持 GET / HEAD"));
            return;
        }

        var resolution = Resolve(rawPath, _library.Root, _toolsDirectory);
        switch (resolution.Kind)
        {
            case WebResolutionKind.File when resolution.FullPath.Length > 0 && File.Exists(resolution.FullPath):
            {
                RespondFile(context, resolution.FullPath, method == "HEAD", 200);
                return;
            }

            case WebResolutionKind.GameFile:
            {
                // ① 本地有就直接给
                if (resolution.FullPath.Length > 0 && File.Exists(resolution.FullPath))
                {
                    RespondFile(context, resolution.FullPath, method == "HEAD", 200);
                    return;
                }

                // ② 本地没有：先看能不能自动下载（这就是「自动下载所需文件」）
                var downloaded = TryAutoDownload(resolution, _cts?.Token ?? CancellationToken.None);
                if (downloaded is not null)
                {
                    if (downloaded.Value.Ok && File.Exists(resolution.FullPath))
                    {
                        RespondFile(context, resolution.FullPath, method == "HEAD", 200);
                        return;
                    }
                    // 404：记得告诉玩游戏那一侧「这个文件确实不存在」，别塞个空文件进去
                    Respond(context, downloaded.Value.NotFound ? 404 : 502, "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes(downloaded.Value.NotFound ? "404 Not Found" : "download failed"));
                    return;
                }

                Respond(context, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("404 Not Found"));
                return;
            }

            default:
                Respond(context, 404, "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes("<html><body><h3>404 Not Found</h3><p>这个地址不在本地服务器上。</p></body></html>"));
                return;
        }
    }

    /// <summary>缺文件时尝试自动下载；没开自动下载返回 null（调用方回 404）。</summary>
    private AutoDownloadOutcome? TryAutoDownload(WebResolution resolution, CancellationToken ct)
    {
        var session = Session;
        if (session is null || !session.Enabled) return null;
        if (FileFetcher is null) return null;

        // 只认这个游戏文件夹里的请求
        var relativeToFolder = session.MakeRelativePath(resolution.GameRelativePath);
        if (relativeToFolder.Length == 0) return null;
        if (string.Equals(relativeToFolder, session.MainPageRelativeName, StringComparison.OrdinalIgnoreCase))
            return null; // 读自己不算缺文件

        // 已经下过 / 已经有文件都不重复下
        lock (session.Gate)
        {
            if (session.DownloadedPaths.Contains(relativeToFolder)) return null;
            if (File.Exists(resolution.FullPath)) return null;
            session.DownloadedPaths.Add(relativeToFolder);
        }

        var url = Core.GamePageParser.JoinBatchNetPath(session.NetBaseUrl, relativeToFolder);
        if (url.Length == 0)
        {
            lock (session.Gate) session.DownloadedPaths.Remove(relativeToFolder);
            return new AutoDownloadOutcome(false, false, "路径不规范");
        }

        // 同一个文件被并发请求时只下一次；不同文件并发受 _downloadGate 限制。
        // 注意：这里是**同步等待**——游戏那边会感觉“网络很慢”，下完才答复（原工具就是这么做的）。
        var request = new AutoDownloadFetch(url, session.Referer, resolution.FullPath, relativeToFolder);
        var task = _inFlight.GetOrAdd(url, key => Task.Run(async () =>
        {
            await _downloadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                session.MarkStarted(relativeToFolder);
                FetchOutcome outcome;
                try
                {
                    outcome = await FileFetcher(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    session.MarkFinished(relativeToFolder, FetchOutcomeKind.Failed);
                    return new AutoDownloadOutcome(false, false, "已取消");
                }
                catch (Exception ex)
                {
                    session.MarkFinished(relativeToFolder, FetchOutcomeKind.Failed);
                    return new AutoDownloadOutcome(false, false, ex.Message);
                }

                var kind = outcome.Ok ? FetchOutcomeKind.Success
                    : outcome.NotFound ? FetchOutcomeKind.NotFound
                    : FetchOutcomeKind.Failed;
                session.MarkFinished(relativeToFolder, kind);
                if (kind != FetchOutcomeKind.Success)
                {
                    lock (session.Gate) session.DownloadedPaths.Remove(relativeToFolder);
                }
                return new AutoDownloadOutcome(outcome.Ok, outcome.NotFound, outcome.Message);
            }
            finally
            {
                _downloadGate.Release();
                _inFlight.TryRemove(url, out var _);
            }
        }, CancellationToken.None));

        try
        {
            return task.Wait(TimeSpan.FromMinutes(5)) ? task.Result : new AutoDownloadOutcome(false, false, "下载超时");
        }
        catch (AggregateException ex)
        {
            return new AutoDownloadOutcome(false, false, ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>自动下载的最终结果。</summary>
    private readonly record struct AutoDownloadOutcome(bool Ok, bool NotFound, string Message);

    // ---------------------------------------------------------------- 路径映射（纯逻辑，自检直接断言）

    /// <summary>
    /// 把请求路径映射到磁盘文件。
    /// <paramref name="gamesRoot"/> 是游戏目录（也可传一个目录用来测试）。
    /// </summary>
    public static WebResolution Resolve(string rawPath, string gamesRoot, string toolsDirectory)
    {
        var path = (rawPath ?? "/").Replace('\\', '/');
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        path = Core.GamePageParser.UrlDecode(path);
        if (!path.StartsWith('/')) path = "/" + path;
        // 注意：这里**不能**看到 ".." 就一律 404 —— 浏览器会把 /games/../img/x.jpg 这种
        // 已经规范化过的地址发过来（NormalizePath），一律拒绝会让本该正常的请求也拿不到文件。
        // 越界防护交给 SafeCombine 的 GetFullPath + 前缀检查，它才是真正判断“有没有跑出根目录”的地方。

        // 目录请求 → index.html
        if (path.EndsWith('/')) path += "index.html";

        if (path.StartsWith("/games/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = path["/games/".Length..];
            if (relative.Length == 0) return WebResolution.NotFound();
            var full = SafeCombine(gamesRoot, relative, out var normalizedRelative);
            return full.Length == 0
                ? WebResolution.NotFound()
                : new WebResolution(WebResolutionKind.GameFile, full,
                    normalizedRelative.Replace('/', Path.DirectorySeparatorChar));
        }

        var staticFile = SafeCombine(toolsDirectory, path.TrimStart('/'), out _);
        return staticFile.Length == 0
            ? WebResolution.NotFound()
            : new WebResolution(WebResolutionKind.File, staticFile, "");
    }

    /// <summary>
    /// 把相对路径安全地接到根目录下：<b>先规范化再判断有没有跑出根目录</b>，
    /// 所以 <c>a/../b.swf</c> 这种（浏览器规范化之后很常见）能正常取到文件，
    /// 而 <c>../../windows/win.ini</c> 会被拒。越界 / 非法返回空字符串。
    /// </summary>
    private static string SafeCombine(string root, string relative, out string normalizedRelative)
    {
        normalizedRelative = "";
        if (string.IsNullOrWhiteSpace(root)) return "";
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (normalized.Length == 0) return "";
        try
        {
            var full = Path.GetFullPath(Path.Combine(root, normalized));
            var rootFull = Path.GetFullPath(root);
            var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";
            normalizedRelative = Path.GetRelativePath(rootFull, full);
            return full;
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- 响应

    private static void RespondFile(HttpListenerContext context, string fullPath, bool headOnly, int status)
    {
        try
        {
            var info = new FileInfo(fullPath);
            context.Response.StatusCode = status;
            context.Response.ContentType = MimeTypeOf(fullPath);
            context.Response.ContentLength64 = info.Length;
            context.Response.Headers["Cache-Control"] = "no-store";
            if (headOnly)
            {
                context.Response.Close();
                return;
            }
            using var stream = File.OpenRead(fullPath);
            stream.CopyTo(context.Response.OutputStream);
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { /* 客户端已断开 */ }
        }
    }

    private static void Respond(HttpListenerContext context, int status, string contentType, byte[] body)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.Close();
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>扩展名 → MIME（Flash 那边不挑，但浏览器要认对 htm / js）。</summary>
    public static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".htm" or ".html" => "text/html; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".xml" => "text/xml; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".swf" => "application/x-shockwave-flash",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".ogg" => "audio/ogg",
        ".wav" => "audio/wav",
        ".mp4" => "video/mp4",
        ".ttf" => "font/ttf",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".unity3d" => "application/vnd.unity",
        ".dat" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        Stop();
        _downloadGate.Dispose();
    }
}

/// <summary>请求路径映射的结果类型。</summary>
public enum WebResolutionKind
{
    /// <summary>没映射到任何东西（404）。</summary>
    NotFound = 0,

    /// <summary>工具目录里的静态资源（播放页 / 图片 / 替代 API）。</summary>
    File = 1,

    /// <summary>游戏目录里的文件（本地没有时可能触发自动下载）。</summary>
    GameFile = 2,
}

/// <summary>请求路径 → 磁盘路径的映射结果。</summary>
/// <param name="Kind">映射到哪一类。</param>
/// <param name="FullPath">磁盘绝对路径（NotFound 时为空）。</param>
/// <param name="GameRelativePath">相对游戏目录的路径（只有 <see cref="WebResolutionKind.GameFile"/> 有值）。</param>
public sealed record WebResolution(WebResolutionKind Kind, string FullPath, string GameRelativePath)
{
    public static WebResolution NotFound() => new(WebResolutionKind.NotFound, "", "");
}

/// <summary>自动下载的统计（对应原始 Python 项目 <c>AutoDownloader</c> 里那一堆计数器）。</summary>
public sealed class AutoDownloadSession
{
    /// <summary>并发保护（服务器是多线程的）。</summary>
    public object Gate { get; } = new();

    /// <summary>游戏文件夹名。</summary>
    public string FolderName { get; init; } = "";

    /// <summary>批量下载的基地址（swf / 网页所在目录，以 / 结尾）。</summary>
    public string NetBaseUrl { get; init; } = "";

    /// <summary>上一级页面（防盗链）。</summary>
    public string Referer { get; init; } = "";

    /// <summary>游戏主页相对文件夹的路径（等于它的请求不算“缺文件”）。</summary>
    public string MainPageRelativeName { get; init; } = "";

    /// <summary>游戏在服务器上的路径前缀，例如 <c>/games/黄金矿工_a123h/</c>。</summary>
    public string FolderRelaBase { get; init; } = "";

    /// <summary>自动下载开着没有。</summary>
    public bool Enabled { get; set; }

    /// <summary>已经处理过的相对路径（避免同一个文件反复下）。</summary>
    public HashSet<string> DownloadedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>最近若干条下载日志（环形，界面直接读）。</summary>
    public Queue<string> RecentLog { get; } = new();

    /// <summary>总共发现了多少个要下载的文件。</summary>
    public int Total { get; private set; }

    /// <summary>正在下载几个。</summary>
    public int Loading { get; private set; }

    /// <summary>成功几个。</summary>
    public int Success { get; private set; }

    /// <summary>404 几个。</summary>
    public int NotFound { get; private set; }

    /// <summary>失败几个。</summary>
    public int Failed { get; private set; }

    /// <summary>最后一个在下载的文件。</summary>
    public string LastDownloading { get; private set; } = "";

    /// <summary>最后一次下载成功了没有。</summary>
    public bool LastDownloadOk { get; private set; } = true;

    /// <summary>当前还有没有下载在跑（关窗口时要等它）。</summary>
    public bool HasPendingDownloads
    {
        get { lock (Gate) return Loading > 0; }
    }

    /// <summary>把服务器上的绝对路径（<c>/games/xxx/rel</c>）换算成相对游戏文件夹的路径。</summary>
    public string MakeRelativePath(string gameRelativePath)
    {
        if (gameRelativePath.Length == 0) return "";
        var normalized = gameRelativePath.Replace('\\', '/');
        var prefix = FolderName.Length > 0
            ? FolderName.Replace('\\', '/') + "/"
            : "";
        if (prefix.Length > 0 && normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return normalized[prefix.Length..];
        return "";
    }

    /// <summary>开始下载一个文件。</summary>
    internal void MarkStarted(string relativePath)
    {
        lock (Gate)
        {
            Loading++;
            Total++;
            LastDownloading = relativePath;
            LastDownloadOk = false;
            Push($"{Total}. 开始下载 {relativePath}");
        }
    }

    /// <summary>一个文件下完了。</summary>
    internal void MarkFinished(string relativePath, FetchOutcomeKind kind)
    {
        lock (Gate)
        {
            Loading = Math.Max(0, Loading - 1);
            switch (kind)
            {
                case FetchOutcomeKind.Success:
                    Success++;
                    Push($"{Total}. 下载完成 {relativePath}");
                    break;
                case FetchOutcomeKind.NotFound:
                    NotFound++;
                    Push($"{Total}. [404] {relativePath} （已自动删掉）");
                    break;
                default:
                    Failed++;
                    Push($"{Total}. [失败] {relativePath}");
                    break;
            }
            LastDownloading = relativePath;
            LastDownloadOk = true;
        }
    }

    /// <summary>清空日志。</summary>
    public void ClearLog()
    {
        lock (Gate) RecentLog.Clear();
    }

    /// <summary>最近 N 行日志（新的在最后）。</summary>
    public string Describe(int maxLines = 200)
    {
        lock (Gate)
        {
            var lines = RecentLog.ToArray();
            if (lines.Length > maxLines) lines = lines[^maxLines..];
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>统计文本（原工具状态栏那一句）。</summary>
    public string DescribeStats()
    {
        lock (Gate)
        {
            var prefix = Enabled ? "下载中… " : "已停止，";
            var text = $"{prefix}总共{Total}个";
            if (Loading > 0) text += $"，下载中{Loading}个";
            if (Success > 0) text += $"，成功{Success}个";
            if (NotFound > 0) text += $"，404有{NotFound}个";
            if (Failed > 0) text += $"，失败{Failed}个";
            return text;
        }
    }

    private void Push(string line)
    {
        RecentLog.Enqueue(line);
        while (RecentLog.Count > 2000) RecentLog.Dequeue();
    }
}

/// <summary>自动下载一个文件的结果分类。</summary>
public enum FetchOutcomeKind
{
    Success = 0,
    NotFound = 1,
    Failed = 2,
}
