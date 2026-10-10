// 「Flash 下载器」自检：纯逻辑逐条断言 + 自检自己起一个 HTTP 服务器来跑「真下载」。
// 这些东西全是无头的（不弹窗、不开界面），跑完把结果交给调用方汇总。
using System.Net;
using System.Net.Http;
using System.Text;
using YuLiangToolkit.Tools.FlashDownloader;
using YuLiangToolkit.Tools.FlashDownloader.Core;
using YuLiangToolkit.Tools.FlashDownloader.Engine;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.FlashDownloader.ViewModels;
using YuLiangToolkit.Tools.FlashDownloader.Web;
// 本自检文件的命名空间里也有个 GameEntry（假游戏站），这里给真身起个别名，免得撞名
using CoreGameEntry = YuLiangToolkit.Tools.FlashDownloader.Core.GameEntry;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>Flash 下载器的自检集合。</summary>
internal static class FlashDownloaderChecks
{
    /// <summary>
    /// 跑完全部检查。<paramref name="result"/> 由主程序传进来（统一汇总 PASS/FAIL）。
    /// 需要写“详情”的断言用 <paramref name="detail"/>，一句话断言用 <paramref name="plain"/>。
    /// </summary>
    public static void Run(Action<string, bool, string> result, Action<string, bool> plain)
    {
        var work = NewDir("flash");
        var games = Path.Combine(work, "games");
        var tools = Path.Combine(work, "tools");
        Directory.CreateDirectory(games);
        Directory.CreateDirectory(tools);

        CheckParsing(result, plain);
        CheckNewSites(result, plain);
        CheckIni(result, plain, work);
        CheckEntryNaming(result, plain);
        CheckBatchPlan(result, plain, work);
        CheckLibraryOperations(result, plain, work);
        CheckGameFilter(result, plain);
        CheckWebMapping(result, plain, games, tools);
        CheckTemplates(result, plain);
        CheckViewModelWiring(result, plain);
        CheckNetworkAndDownloads(result, plain, work, games, tools);

        // 联网验收：默认跳过。站点改版 / 断网会让它红，而默认自检必须是确定性的。
        // 想跑就设 YLT_LIVE_SITES=1（见 FlashDownloaderChecks.CheckLiveSites 的说明）。
        if (Environment.GetEnvironmentVariable("YLT_LIVE_SITES") == "1") CheckLiveSites(result, plain);
    }

    // ---------------------------------------------------------------- 1. 站点解析（对应 HtmlAnalyse）

    private static void CheckParsing(Action<string, bool, string> check, Action<string, bool> report)
    {
        // toSafeFileName：删非法字符、下划线换连字符、截断 100
        check("文件名清洗：去非法字符 + 下划线换连字符",
            GamePageParser.ToSafeFileName("黄金 矿工/双人:版") == "黄金矿工双人版",
            GamePageParser.ToSafeFileName("黄金 矿工/双人:版"));
        check("文件名清洗：下划线变连字符（点号也会被删掉）",
            GamePageParser.ToSafeFileName("hello_world.swf") == "hello-worldswf",
            GamePageParser.ToSafeFileName("hello_world.swf"));
        report("文件名清洗：超长截断到 100",
            GamePageParser.ToSafeFileName(new string('字', 150)).Length == 100);

        // getURLInfo：/1234.htm → 字母 + 数字
        var (a4399, s4399) = GamePageParser.GetUrlInfo("https://www.4399.com/flash/6853.htm");
        check("识别 4399 游戏页后缀", a4399 == "a6853" && s4399 == GameSite.Site4399, a4399);
        var (a7k7k, s7k7k) = GamePageParser.GetUrlInfo("https://www.7k7k.com/swf/123456.htm");
        check("识别 7k7k 游戏页后缀", a7k7k == "b123456" && s7k7k == GameSite.Site7k7k, a7k7k);
        var (a17yy, s17yy) = GamePageParser.GetUrlInfo("http://www.17yy.com/f/play/123456.html");
        check("识别 17yy 游戏页后缀", a17yy == "c123456" && s17yy == GameSite.Site17yy, a17yy);
        var (unknown, sUnknown) = GamePageParser.GetUrlInfo("https://example.com/game/9.htm");
        check("其它站点识别为未知（后缀字母 z）", unknown == "z9" && sUnknown == GameSite.Unknown, unknown);

        // appendixToURL：反查游戏页
        report("后缀反查 4399 游戏页",
            GamePageParser.AppendixToUrl("a6853")?.Url == "https://www.4399.com/flash/6853.htm");
        report("后缀反查 7k7k 游戏页",
            GamePageParser.AppendixToUrl("b123456")?.Url == "https://www.7k7k.com/swf/123456.htm");
        report("后缀反查 17yy 游戏页",
            GamePageParser.AppendixToUrl("c123456")?.Url == "http://www.17yy.com/f/play/123456.html");
        report("vip / 乱写的后缀返回 null",
            GamePageParser.AppendixToUrl("a") is null && GamePageParser.AppendixToUrl("x123") is null
            && GamePageParser.AppendixToUrl("") is null);

        // getParentUrl / urljoin
        check("取父目录（批量下载的根地址）",
            GamePageParser.GetParentUrl("https://sda.4399.com/4399swf/upload/game/main.swf")
            == "https://sda.4399.com/4399swf/upload/game/",
            GamePageParser.GetParentUrl("https://sda.4399.com/4399swf/upload/game/main.swf"));
        report("相对路径拼成绝对网址",
            GamePageParser.UrlJoin("https://sda.4399.com/a/b/", "assets/level1.swf")
            == "https://sda.4399.com/a/b/assets/level1.swf");
        report("协议相对网址补上协议",
            GamePageParser.UrlJoin("https://www.4399.com/flash/1.htm", "//sda.4399.com/a.swf")
            == "https://sda.4399.com/a.swf");
        report("取网址里的文件名",
            GamePageParser.GetUrlFileName("https://x.com/a/b/main.swf?v=2") == "main.swf");

        // 批量下载的路径校验
        check("批量路径：去掉 ? 查询串",
            GamePageParser.SanitizeBatchPath("assets/a.swf?v=1&t=2") == "assets/a.swf",
            GamePageParser.SanitizeBatchPath("assets/a.swf?v=1&t=2"));
        report("批量路径：含 .. 判为非法", GamePageParser.SanitizeBatchPath("../secret.swf") == "");
        report("批量路径：含空格判为非法", GamePageParser.SanitizeBatchPath("my file.swf") == "");
        report("批量路径：反斜杠仍是合法行（后面会换成 /）",
            GamePageParser.SanitizeBatchPath(@"assets\a.swf") == @"assets\a.swf");
        report("批量网址拼接（行首 / 去掉、行尾 / 去掉）",
            GamePageParser.JoinBatchNetPath("https://a.com/b/", "/x/y.swf") == "https://a.com/b/x/y.swf");
        check("本路径拼接：%xx 解码 + 保留子目录",
            GamePageParser.JoinBatchLocalPath(@"C:\g", "assets/a%20b.swf") is var local
            && local.StartsWith(@"C:\g", StringComparison.OrdinalIgnoreCase) && local.Contains("assets"),
            GamePageParser.JoinBatchLocalPath(@"C:\g", "assets/a%20b.swf"));
        report("本路径拼接：含 .. 返回空", GamePageParser.JoinBatchLocalPath(@"C:\g", "../evil.swf") == "");
        report("文件名合法性：禁 .. 与 ?*\"<>|:",
            GamePageParser.IsGoodFileName("a.swf") && !GamePageParser.IsGoodFileName("a?.swf")
            && !GamePageParser.IsGoodFileName("a..swf") && !GamePageParser.IsGoodFileName(""));

        // 网页编码
        var gbkBytes = Encoding.GetEncoding("gbk")
            .GetBytes("<html><head><meta charset=\"gb2312\"></head><body>中文测试</body></html>");
        var (gbkText, gbkEnc) = GamePageParser.DecodeWebContent(gbkBytes);
        check("网页编码自动识别（gb2312 页面）",
            gbkText.Contains("中文测试") && gbkEnc.StartsWith("gb", StringComparison.OrdinalIgnoreCase),
            $"{gbkEnc} / {gbkText.Contains("中文测试")}");
        var utf8Bytes = Encoding.UTF8.GetBytes("<html><head><meta charset=\"utf-8\"></head><body>中文测试</body></html>");
        report("网页编码自动识别（utf-8 页面）",
            GamePageParser.DecodeWebContent(utf8Bytes).Text.Contains("中文测试"));

        // 404 判定
        report("404 页判定（对应 IsAnyone404Page）",
            GamePageParser.LooksLikeNotFoundPage(Encoding.UTF8.GetBytes("<h1>404 Not Found</h1>"))
            && GamePageParser.LooksLikeNotFoundPage(Encoding.UTF8.GetBytes("Document not found"))
            && !GamePageParser.LooksLikeNotFoundPage(Encoding.UTF8.GetBytes("正常的 swf 内容")));

        // 4399 三种页面结构
        const string html4399Direct =
            "<script>var game_title='黄金矿工双人版';var _strGamePath=\"/upload/game/2013/jtys3.swf\";var _w=800,_h=600;</script>";
        check("4399 直接 swf：游戏名",
            GamePageParser.Get4399GameName(html4399Direct) == "黄金矿工双人版",
            GamePageParser.Get4399GameName(html4399Direct));
        check("4399 直接 swf：拼出 sda 地址",
            GamePageParser.Get4399DirectSwfPath(html4399Direct)
            == "https://sda.4399.com/4399swf/upload/game/2013/jtys3.swf",
            GamePageParser.Get4399DirectSwfPath(html4399Direct) ?? "(null)");
        report("4399 宽高", GamePageParser.Get4399GameWidthHeight(html4399Direct) == (800, 600));
        report("4399 宽高拿不全时返回 (-1,-1)",
            GamePageParser.Get4399GameWidthHeight("var _w=800;") == (-1, -1));

        const string html4399NewPage =
            "<script>var game_title='多层游戏';var _strGamePath=\"/upload/game/2014/jifen.htm\";</script>";
        check("4399 新页面地址",
            GamePageParser.Get4399NewPagePath(html4399NewPage)
            == "https://sda.4399.com/4399swf/upload/game/2014/jifen.htm",
            GamePageParser.Get4399NewPagePath(html4399NewPage) ?? "(null)");
        const string newPageHtml = "<div id=\"flashgame\"><embed src=\"mainSwfGame.swf\"></div>";
        check("4399 新页面里找到 swf（相对路径要 join）",
            GamePageParser.Get4399SwfPathInNewPage(newPageHtml, "https://sda.4399.com/a/b/jifen.htm")
            == "https://sda.4399.com/a/b/mainSwfGame.swf",
            GamePageParser.Get4399SwfPathInNewPage(newPageHtml, "https://sda.4399.com/a/b/jifen.htm") ?? "(null)");
        report("没有 id=flashgame 就返回 null",
            GamePageParser.Get4399SwfPathInNewPage("<div>无</div>", "https://a/b.htm") is null);
        report("h5wan 入口 = 同目录 gameIndex.html",
            GamePageParser.Get4399H5WanGamePath("https://sda.4399.com/a/b/x.htm")
            == "https://sda.4399.com/a/b/gameIndex.html");
        report("h5wan / unity / 404 三种标记判定",
            GamePageParser.Is4399NewPageH5Wan("...h5wan...")
            && GamePageParser.Is4399NewPageUnity("...WebPlayer.unity3d...")
            && GamePageParser.Is4399404Page("<title>404 Not Found</title>")
            && !GamePageParser.Is4399NewPageH5Wan("普通页面"));

        const string h5Page = "<script src=\"https://h.api.4399.com/h5mini-2.0/h5api-interface.php\"></script>"
                              + "<script>document.domain='4399.com';</script>";
        var replaced4399 = GamePageParser.Replace4399Page(h5Page);
        check("4399 h5 页面的官方 API 被换成替代脚本",
            replaced4399.Contains("/tool/substiH5WanApi.js") && !replaced4399.Contains("h.api.4399.com"),
            replaced4399);
        report("4399 h5 页面的 document.domain 被删掉", !replaced4399.Contains("document.domain"));
        report("关掉替换开关时保留官方 API",
            GamePageParser.Replace4399Page(h5Page, replaceH5Api: false).Contains("h.api.4399.com"));

        // 7k7k
        const string html7k7kDirect =
            "<script>var gameName:\"小破孩\";var gamePath:\"https://sda.7k7k.com/a/b.swf\";var gamewidth:800,gameheight:600,</script>";
        check("7k7k 游戏名", GamePageParser.Get7k7kGameName(html7k7kDirect) == "小破孩",
            GamePageParser.Get7k7kGameName(html7k7kDirect));
        report("7k7k 直接 swf",
            GamePageParser.Get7k7kDirectSwfPath(html7k7kDirect) == "https://sda.7k7k.com/a/b.swf");
        report("7k7k 宽高", GamePageParser.Get7k7kGameWidthHeight(html7k7kDirect) == (800, 600));
        report("7k7k 新页面 + 新页面里的 _src_",
            GamePageParser.Get7k7kNewPagePath("gamePath:\"/a/b.htm\"") == "/a/b.htm"
            && GamePageParser.Get7k7kSwfPathInNewPage("_src_='game.swf'", "https://x.com/a/b.htm")
               == "https://x.com/a/game.swf");
        report("7k7k/17yy 的 document.domain 被删掉",
            !GamePageParser.Replace7k7k17yyPage("<script>document.domain='7k7k.com';</script>").Contains("document.domain"));

        // 17yy
        const string html17yy =
            "<script>var m7_gamename=\"冒险王\";var m7_gameid=\"123456\";var date=\"pingtai\";flash_w=960;flash_h=540;</script>";
        check("17yy 游戏名", GamePageParser.Get17yyGameName(html17yy) == "冒险王",
            GamePageParser.Get17yyGameName(html17yy));
        report("17yy 游戏编号",
            GamePageParser.Get17yyGameId(html17yy, "http://www.17yy.com/f/play/1.html") == 123456);
        report("17yy 拿不到 m7_gameid 时退回 URL 里的数字",
            GamePageParser.Get17yyGameId("无", "http://www.17yy.com/f/play/998877.html") == 998877);
        report("17yy 分类 + 拼 swf 地址",
            GamePageParser.Get17yyGameCategory(html17yy) == "pingtai"
            && GamePageParser.Build17yySwfUrl("pingtai", "a/b.swf") == "http://img1.17yy.com/swf/pingtai/a/b.swf");
        report("17yy 宽高", GamePageParser.Get17yyGameWidthHeight(html17yy) == (960, 540));
        report("17yy 标题兜底（m7_gamename 缺席时）",
            GamePageParser.Get17yyGameName("<title>森林冰火人在线玩\"") == "森林冰火人");
    }

    // ---------------------------------------------------------------- 1b. ArmorGames / Kongregate

    /// <summary>
    /// ArmorGames 与 Kongregate 的页面解析（纯字符串，不联网）。
    ///
    /// <para><b>样本都是照抄真页面的关键片段</b>（实测抓下来的），所以站点改版时这里会先红：
    /// ArmorGames 的 swf 明写在 <c>&lt;param name="movie"&gt;</c> / <c>&lt;embed src&gt;</c> 里；
    /// Kongregate 得走三步——游戏页 → 嵌入页（<c>iframeConfig</c>）→ frame 页（<c>swf_location</c>）。</para>
    /// </summary>
    private static void CheckNewSites(Action<string, bool, string> check, Action<string, bool> report)
    {
        // ---- ArmorGames：https://armorgames.com/play/14775 的关键片段 ----
        const string armorHtml = """
            <meta property="og:title"  content="Strike Force Heroes 2 - Play on Armor Games">
            <div id="gamefilearea" style="height:600px; display: none;">
                <div id="game-ruffle" style="display: none;">
            <object classid="clsid:d27cdb6e-ae6d-11cf-96b8-444553540000"
                    codebase="http://fpdownload.macromedia.com/pub/shockwave/cabs/flash/swflash.cab#version=8,0,0,0"
                    width="800" height="600" id="gamefile" align="middle">
                <param name="allowScriptAccess" value="always" />
                <param name="movie" value="https://cache.armorgames.com/files/games/strike-force-heroes--14775.swf" />
                <param name="quality" value="high" />
                <param name="flashvars" value="gameID=14775" />
                <embed src="https://cache.armorgames.com/files/games/strike-force-heroes--14775.swf"
                       width="800"
                       height="600"
                       id="gamefileEmbed"
                       flashvars="gameID=14775" />
            </object>
                </div>
            </div>
            """;
        const string armorSwf = "https://cache.armorgames.com/files/games/strike-force-heroes--14775.swf";

        check("ArmorGames 游戏名：og:title 剪掉「 - Play on Armor Games」",
            GamePageParser.GetArmorGamesGameName(armorHtml) == "Strike Force Heroes 2",
            GamePageParser.GetArmorGamesGameName(armorHtml));
        check("ArmorGames 从 <param name=\"movie\"> 里拿到 swf（不是自己拼的）",
            GamePageParser.GetArmorGamesDirectSwfPath(armorHtml) == armorSwf,
            GamePageParser.GetArmorGamesDirectSwfPath(armorHtml) ?? "(null)");
        check("ArmorGames 宽高：object / embed 的属性跨行也读得到",
            GamePageParser.GetArmorGamesGameWidthHeight(armorHtml) == (800, 600),
            GamePageParser.GetArmorGamesGameWidthHeight(armorHtml).ToString());
        check("ArmorGames 没有 og:title 时退回 <title> 并剪尾巴",
            GamePageParser.GetArmorGamesGameName("<title>Sonny 2 - Play on Armor Games</title>") == "Sonny 2",
            GamePageParser.GetArmorGamesGameName("<title>Sonny 2 - Play on Armor Games</title>"));
        check("ArmorGames 只有 <embed> 时也能找到 swf",
            GamePageParser.GetArmorGamesDirectSwfPath(
                "<embed src=\"https://cache.armorgames.com/files/games/sonny-2-2900.swf\" width=\"640\" height=\"480\" />")
            == "https://cache.armorgames.com/files/games/sonny-2-2900.swf",
            GamePageParser.GetArmorGamesDirectSwfPath(
                "<embed src=\"https://cache.armorgames.com/files/games/sonny-2-2900.swf\" width=\"640\" height=\"480\" />") ?? "(null)");
        report("ArmorGames 页面里没有 swf（HTML5 游戏）→ null",
            GamePageParser.GetArmorGamesDirectSwfPath("<html><body><canvas id=\"game\"></canvas></body></html>") is null);
        report("armorgames.com 的域名识别",
            GameUrlInfo.DetectSite("https://www.armorgames.com/play/14775") == GameSite.SiteArmorGames);
        check("ArmorGames 编号从 /play/14775 里读",
            GamePageParser.GetUrlInfo("https://armorgames.com/play/14775")
            == ("d14775", GameSite.SiteArmorGames),
            GamePageParser.GetUrlInfo("https://armorgames.com/play/14775").Appendix);
        report("ArmorGames 编号也能从 …-game/14775 这种地址读",
            GamePageParser.GetUrlInfo("https://armorgames.com/strike-force-heroes-2-game/14775").Appendix == "d14775");
        report("ArmorGames 的带语言段 / 带 slug 的地址也认",
            GamePageParser.GetUrlInfo("https://armorgames.com/play/14775/strike-force-heroes-2").Appendix == "d14775");

        var armorBack = GamePageParser.AppendixToUrl("d14775");
        check("ArmorGames 后缀能反推回游戏页（装入文件夹 / 改为 htm 要用）",
            armorBack is not null && armorBack.Value.Url == "https://armorgames.com/play/14775"
            && armorBack.Value.Site == GameSite.SiteArmorGames,
            armorBack?.Url ?? "(null)");

        // ---- Kongregate 第 1 步：游戏页（里面那个 iframe 指向嵌入页） ----
        const string kongGameHtml = """
            <title>Scratch Inc. – Play Instantly Online | Kongregate</title>
            <iframe src="/games/Makopaz/scratch-inc/embed" width="100%" height="100%" frameborder="0" allowfullscreen="allowfullscreen" allow="camera *; microphone *; fullscreen *; autoplay *" class="game-embed-iframe" style="border: none; display: block;" data-previews--game-controls-target="embedIframe"></iframe>
            <script>
              window.dataLayer.push({ event: 'gameplay_start', game_id: '324529', game_name: 'Scratch Inc.' });
            </script>
            """;

        check("Kongregate 游戏名：标题剪掉「 – Play Instantly Online | Kongregate」",
            GamePageParser.GetKongregateGameName(kongGameHtml) == "Scratch Inc.",
            GamePageParser.GetKongregateGameName(kongGameHtml));
        check("Kongregate 游戏编号：网址里没有，只能从网页正文的 game_id 读",
            GamePageParser.GetKongregateGameId(kongGameHtml) == "324529",
            GamePageParser.GetKongregateGameId(kongGameHtml));
        check("Kongregate 嵌入页地址：优先用页面里那个 iframe",
            GamePageParser.GetKongregateEmbedUrl(kongGameHtml, "https://www.kongregate.com/en/games/makopaz/scratch-inc")
            == "https://www.kongregate.com/games/Makopaz/scratch-inc/embed",
            GamePageParser.GetKongregateEmbedUrl(kongGameHtml, "https://www.kongregate.com/en/games/makopaz/scratch-inc") ?? "(null)");
        check("Kongregate 嵌入页地址：没有 iframe 时自己拼",
            GamePageParser.BuildKongregateEmbedUrl("https://www.kongregate.com/en/games/makopaz/scratch-inc")
            == "https://www.kongregate.com/en/games/makopaz/scratch-inc/embed",
            GamePageParser.BuildKongregateEmbedUrl("https://www.kongregate.com/en/games/makopaz/scratch-inc") ?? "(null)");
        report("Kongregate 嵌入页地址：粘的就是嵌入页时不重复加 /embed",
            GamePageParser.BuildKongregateEmbedUrl("https://www.kongregate.com/en/games/makopaz/scratch-inc/embed")
            == "https://www.kongregate.com/en/games/makopaz/scratch-inc/embed");
        report("Kongregate 不是 /games/作者/游戏名 的地址拼不出嵌入页",
            GamePageParser.BuildKongregateEmbedUrl("https://www.kongregate.com/en/games") is null);

        // 名字有两个坑，都是联网验收里抓出来的：
        // ① 游戏页标题是「Bloons Super Monkey on Kongregate – Play Now」（不是嵌入页那种尾巴）；
        // ② 只认标题会拼出 BloonsSuperMonkeyonKongregate–PlayNow_e72514.swf 这种鬼名字，
        //    所以优先读脚本里的 game_name / game_title。
        check("Kongregate 游戏页标题的「 on Kongregate – Play Now」也要剪掉",
            GamePageParser.GetKongregateGameName("<title>Bloons Super Monkey on Kongregate – Play Now</title>")
            == "Bloons Super Monkey",
            GamePageParser.GetKongregateGameName("<title>Bloons Super Monkey on Kongregate – Play Now</title>"));
        check("Kongregate 名字优先读脚本里的 game_name（比标题可靠）",
            GamePageParser.GetKongregateScriptName("active_user.setGame(72514, \"/x\", {\"game_name\":\"Bloons Super Monkey\",\"game_type\":\"flash\"});")
            == "Bloons Super Monkey",
            GamePageParser.GetKongregateScriptName("active_user.setGame(72514, \"/x\", {\"game_name\":\"Bloons Super Monkey\",\"game_type\":\"flash\"});"));
        check("Kongregate frame 页的 ttgArgs.game_title 也是名字来源",
            GamePageParser.GetKongregateScriptName(
                "var ttgArgs = { game_title: \"Bloons Super Monkey\", time: 123 };")
            == "Bloons Super Monkey",
            GamePageParser.GetKongregateScriptName("var ttgArgs = { game_title: \"Bloons Super Monkey\", time: 123 };"));
        check("Kongregate 的网址里读不出编号（要靠后面的 game_id 补）",
            GamePageParser.GetUrlInfo("https://www.kongregate.com/games/Ninjakiwi/bloons-super-monkey")
            == ("", GameSite.SiteKongregate),
            GamePageParser.GetUrlInfo("https://www.kongregate.com/games/Ninjakiwi/bloons-super-monkey").Appendix);

        // ---- Kongregate 第 2 步：嵌入页的 iframeConfig（Flash 游戏：Bloons Super Monkey） ----
        const string kongFlashEmbed = """
            <title>Bloons Super Monkey - Play on Kongregate</title>
            <script src="https://unpkg.com/@ruffle-rs/ruffle"></script>
            active_user.setGame(72514, "/en/games/Ninjakiwi/bloons-super-monkey", {"user_vars":"[]","game_type":"flash","game_name":"Bloons Super Monkey","requires_flash":true});
            iframeConfig = {"auto_resize":null,"iframe_url":'https://game72514.konggames.com/games/Ninjakiwi/bloons-super-monkey/frame/' + channel_id + '/?kongregate_host=www.kongregate.com',"alternate_game_file_url":null,"game_width":640,"game_height":480,"max_game_width":null,"max_game_height":null,"game_left":0,"game_top":23,"style_updates":"","iframe_class":"dont_hide","host":"https://www.kongregate.com","api_host":"https://api.kongregate.com","api_path":"https://chat.kongregate.com/flash/API_AS3_5a1d8c231ff1d223d7181f3900f58126.swf","preview":false,"game_permalink":"bloons-super-monkey","game_id":72514,"game_url":"http://www.kongregate.com/en/games/Ninjakiwi/bloons-super-monkey","game_version":1266848660,"flash_var_prefix":"kv_","post_message":true,"iframe_host":"https://game72514.konggames.com","game_host":"https://chat.kongregate.com","channel_id":encodeURIComponent(channel_id),"svid":null,"game_type":"flash"};
            var iframeUrl = 'https://game72514.konggames.com/games/Ninjakiwi/bloons-super-monkey/frame/' + channel_id + '/?kongregate_host=www.kongregate.com';
            var gameType = "flash";
            """;

        var kongConfig = GamePageParser.GetKongregateIframeConfig(kongFlashEmbed);
        check("Kongregate 抠得出 iframeConfig", kongConfig is not null, kongConfig ?? "(null)");
        check("Kongregate 编号也能从 iframeConfig 的 game_id 读到",
            GamePageParser.GetKongregateGameId(kongFlashEmbed) == "72514",
            GamePageParser.GetKongregateGameId(kongFlashEmbed));
        report("Kongregate 判定成 Flash（game_type / requires_flash / game_host 三选一）",
            GamePageParser.IsKongregateFlashGame(kongConfig));
        check("Kongregate 宽高从 iframeConfig 读",
            GamePageParser.GetKongregateGameWidthHeight(kongConfig) == (640, 480),
            GamePageParser.GetKongregateGameWidthHeight(kongConfig).ToString());
        check("Kongregate frame 地址：JS 拼接换成固定串，查询串要保住",
            GamePageParser.GetKongregateFrameUrl(kongConfig)
            == "https://game72514.konggames.com/games/Ninjakiwi/bloons-super-monkey/frame/ylt/?kongregate_host=www.kongregate.com",
            GamePageParser.GetKongregateFrameUrl(kongConfig) ?? "(null)");
        check("Kongregate 嵌入页里只有它自己的 API swf → 不算找到游戏",
            GamePageParser.GetKongregateSwfPath(kongFlashEmbed) is null,
            GamePageParser.GetKongregateSwfPath(kongFlashEmbed) ?? "(null)");
        report("Kongregate 认得出 API 库 swf（API_AS3，不能当成游戏下）",
            GamePageParser.IsKongregateApiSwf("https://chat.kongregate.com/flash/API_AS3_5a1d8c231ff1d223d7181f3900f58126.swf")
            && !GamePageParser.IsKongregateApiSwf("https://chat.kongregate.com/gamez/0007/2514/live/bloonssupermonkey.swf"));

        // ---- Kongregate 第 3 步：frame 页里那行 swf_location ----
        const string kongFrameHtml = """
            <script>
            gameLoader = {
              loadGame: function(urlOptions) {
                var swf_location = "https://chat.kongregate.com/gamez/0007/2514/live/bloonssupermonkey.swf?kongregate_game_version=1266848660";
                var swfobject_flash_vars = {"kongregate":"true","kongregate_api_path":"https%3A%2F%2Fchat.kongregate.com%2Fflash%2FAPI_AS3_5a1d8c231ff1d223d7181f3900f58126.swf"};
              }
            };
            </script>
            """;
        check("Kongregate frame 页里找到真正的游戏 swf（带版本查询串）",
            GamePageParser.GetKongregateSwfPath(kongFrameHtml)
            == "https://chat.kongregate.com/gamez/0007/2514/live/bloonssupermonkey.swf?kongregate_game_version=1266848660",
            GamePageParser.GetKongregateSwfPath(kongFrameHtml) ?? "(null)");

        // ---- Kongregate 的 h5 游戏：iframe_url 是普通字符串（双引号 + \u0026 转义） ----
        const string kongHtml5Embed = """
            iframeConfig = {"auto_resize":null,"iframe_url":"https://game324529.konggames.com/gamez/0032/4529/live/index.html?kongregate_game_version=1790890979\u0026kongregate_host=www.kongregate.com","alternate_game_file_url":null,"game_width":1320,"game_height":850,"api_path":"https://chat.kongregate.com/flash/API_AS3_5a1d8c231ff1d223d7181f3900f58126.swf","game_id":324529,"game_host":"https://game324529.konggames.com","game_type":"html"};
            """;
        var kongHtml5Config = GamePageParser.GetKongregateIframeConfig(kongHtml5Embed);
        report("Kongregate h5 游戏不会被误判成 Flash",
            kongHtml5Config is not null
            && !GamePageParser.IsKongregateFlashGame(kongHtml5Config)
            && GamePageParser.GetKongregateGameWidthHeight(kongHtml5Config) == (1320, 850));
        check("Kongregate h5 游戏的 iframe_url：\\u0026 要还原成 &",
            GamePageParser.GetKongregateFrameUrl(kongHtml5Config)
            == "https://game324529.konggames.com/gamez/0032/4529/live/index.html?kongregate_game_version=1790890979&kongregate_host=www.kongregate.com",
            GamePageParser.GetKongregateFrameUrl(kongHtml5Config) ?? "(null)");

        // ---- 站点表 / 命名规则要认这两个字母 ----
        report("字母表：d=ArmorGames、e=Kongregate 能来回换",
            GameSites.ToLetter(GameSite.SiteArmorGames) == 'd'
            && GameSites.ToLetter(GameSite.SiteKongregate) == 'e'
            && GameSites.FromLetter('D') == GameSite.SiteArmorGames
            && GameSites.FromLetter('e') == GameSite.SiteKongregate
            && GameSites.DisplayName(GameSite.SiteKongregate) == "Kongregate"
            && GameSites.HomePage(GameSite.SiteArmorGames).Contains("armorgames.com"));
        report("Kongregate 的后缀反推不出页面（站点自己就不提供），要如实说出来",
            GamePageParser.AppendixToUrl("e72514") is null
            && !GameSites.CanResolveFromAppendix(GameSite.SiteKongregate)
            && GameSites.CanResolveFromAppendix(GameSite.SiteArmorGames));
        report("e / d 后缀在文件名规则里也认（散放 swf、s / h 文件夹）",
            CoreGameEntry.AppendixOfSwfFile("Bloons_e72514.swf") == "e72514"
            && CoreGameEntry.AppendixOfSwfFile("Strike Force Heroes 2_d14775.swf") == "d14775"
            && CoreGameEntry.FolderExtOf("Bloons_e72514s") == ".swf"
            && CoreGameEntry.FolderExtOf("Bloons_e72514h") == ".htm"
            && CoreGameEntry.GameNameOfFolder("Bloons_e72514s") == "Bloons_e72514"
            && CoreGameEntry.AppendixOfFolder("Bloons_e72514s") == "e72514");
    }

    // ---------------------------------------------------------------- 1c. 联网验收（默认不跑）

    /// <summary>
    /// <b>联网验收</b>：拿真站点的页面走一遍「解析 → 下载」，确认抓取规则没被站点改版打掉。
    ///
    /// <para><b>默认不跑</b>，要 <c>$env:YLT_LIVE_SITES=1</c> 才开：站点改版 / 断网 / 被墙都会让它红，
    /// 而默认自检必须是确定性的（离线也能全绿）。改了 4399 / 7k7k / 17yy / ArmorGames / Kongregate
    /// 的解析规则之后，跑一次这个看看真页面还认不认。</para>
    ///
    /// <para>这里只验两个国外站点：它们没有防盗链，谁的机器上都能跑；国内站点有时会挑 IP，
    /// 放进来会让「联网验收」变得时灵时不灵。</para>
    /// </summary>
    public static void CheckLiveSites(Action<string, bool, string> check, Action<string, bool> report)
    {
        var root = NewDir("live-sites");
        var config = new FlashDownloaderConfig { ConnectTimeoutSeconds = 10, ReadTimeoutSeconds = 90 };
        using var http = new FlashHttp(config.ConnectTimeoutSeconds, config.ReadTimeoutSeconds);
        var downloader = new GameDownloader(http, config);

        // ---- ArmorGames：swf 明写在游戏页上 ----
        var armor = downloader.ResolveAsync("https://armorgames.com/play/14775").GetAwaiter().GetResult();
        check("联网 ArmorGames：解析出 swf + 编号 d14775 + 宽高",
            armor.Ok && armor.Meta is
            {
                Kind: GameContentKind.Swf, Site: GameSite.SiteArmorGames, Appendix: "d14775",
                Width: 800, Height: 600,
            }
            && armor.Meta.ContentUrl.Contains("armorgames.com", StringComparison.OrdinalIgnoreCase),
            armor.Ok ? armor.Meta!.Describe() : armor.Error);

        // ArmorGames 的游戏动辄十几 MB，联网验收里不整个拖下来；只探头三个字节确认那真是个 swf
        // （FWS = 未压缩 / CWS = zlib / ZWS = LZMA，Flash 文件头就这三种）。
        if (armor.Ok && armor.Meta is not null)
        {
            check("联网 ArmorGames：那个地址真的在发 swf（只看文件头）",
                SwfHeaderOf(http, armor.Meta.ContentUrl, armor.Meta.Referer) is "FWS" or "CWS" or "ZWS",
                armor.Meta.ContentUrl);
        }

        // ---- Kongregate：游戏页 → 嵌入页 → frame 页，三跳之后才是 swf ----
        var kong = downloader.ResolveAsync("https://www.kongregate.com/games/Ninjakiwi/bloons-super-monkey")
            .GetAwaiter().GetResult();
        check("联网 Kongregate：三跳之后解析出 swf + 编号 e72514 + 宽高",
            kong.Ok && kong.Meta is
            {
                Kind: GameContentKind.Swf, Site: GameSite.SiteKongregate, Appendix: "e72514",
                Width: 640, Height: 480, IsNewPage: true,
            }
            && kong.Meta.ContentUrl.Contains("chat.kongregate.com", StringComparison.OrdinalIgnoreCase),
            kong.Ok ? kong.Meta!.Describe() : kong.Error);

        // Kongregate 的 swf 只有两三 MB，可以真下一遍把整条链路验穿
        if (kong.Ok && kong.Meta is not null)
        {
            var library = new GamesLibrary(Path.Combine(root, "games"));
            var download = downloader.DownloadAsync(library, kong.Meta).GetAwaiter().GetResult();
            var file = download.Ok ? Path.Combine(library.Root, download.FileOrFolderName) : "";
            check("联网 Kongregate：真下载落成 游戏名_e72514.swf 且内容是 swf",
                download.Ok && download.FileOrFolderName == "BloonsSuperMonkey_e72514.swf"
                && File.Exists(file) && new FileInfo(file).Length > 100_000
                && SwfHeaderOfFile(file) is "FWS" or "CWS" or "ZWS",
                download.Ok ? $"{download.FileOrFolderName}（{new FileInfo(file).Length / 1024} KB）" : download.Error);
        }

        // ---- Kongregate 的 h5 游戏不能被当成 Flash ----
        var kongH5 = downloader.ResolveAsync("https://www.kongregate.com/games/Makopaz/scratch-inc")
            .GetAwaiter().GetResult();
        check("联网 Kongregate：h5 游戏识别成网页版（不是 swf），并记下编号 e324529",
            kongH5.Ok && kongH5.Meta is { Kind: GameContentKind.Htm, Appendix: "e324529" },
            kongH5.Ok ? kongH5.Meta!.Describe() : kongH5.Error);

        // ---- 顺手确认「编号 → 游戏页」这条：ArmorGames 能回得去，Kongregate 回不去（站点不给）----
        var armorBack = downloader.ResolveByAppendixAsync("d14775").GetAwaiter().GetResult();
        report("联网 ArmorGames：光有后缀 d14775 也能重新解析出游戏",
            armorBack.Ok && armorBack.Meta is { Appendix: "d14775", Kind: GameContentKind.Swf });
        var kongBack = downloader.ResolveByAppendixAsync("e72514").GetAwaiter().GetResult();
        report("联网 Kongregate：光有后缀 e72514 反推不出页面，但要把话说清楚（不是崩 / 不是空指针）",
            !kongBack.Ok && kongBack.Error.Contains("编号", StringComparison.Ordinal));
    }

    /// <summary>只取一个网址的头三个字节（判定 swf 文件头用，避免把十几 MB 全拖下来）。</summary>
    private static string SwfHeaderOf(FlashHttp http, string url, string? referer)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", GamePageParser.DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Range", "bytes=0-2");
            if (!string.IsNullOrWhiteSpace(referer)) request.Headers.TryAddWithoutValidation("Referer", referer);
            using var response = http.Client.Send(request);
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            return bytes.Length >= 3 ? Encoding.ASCII.GetString(bytes, 0, 3) : "";
        }
        catch { return ""; }
    }

    /// <summary>读本地文件的前三个字节（swf 文件头）。</summary>
    private static string SwfHeaderOfFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[3];
            return stream.Read(buffer, 0, 3) == 3 ? Encoding.ASCII.GetString(buffer) : "";
        }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- 2. SWFNetPath.ini

    private static void CheckIni(Action<string, bool, string> check, Action<string, bool> report, string work)
    {
        // swf 型：六行（有新页面）
        var swfIni = new SwfNetPathIni
        {
            FilePath = Path.Combine(work, "ini-swf", "SWFNetPath.ini"),
            FolderExt = ".swf",
            Kind = GameFolderKind.Swf,
            NetPath = "https://sda.4399.com/a/b/main.swf",
            Width = 800,
            Height = 600,
            IsNewPage = SwfNetPathIni.NewPageTrue,
            NewPageUrl = "https://sda.4399.com/a/b/jifen.htm",
        };
        swfIni.Save();
        var swfLines = File.ReadAllText(swfIni.FilePath).Split('\n');
        check("swf 型 ini 的行结构（路径/swf/宽/高/True/新页网址）",
            swfLines.Length == 6 && swfLines[1] == "swf" && swfLines[4] == "True",
            string.Join(" | ", swfLines));

        var reloaded = SwfNetPathIni.FromFile(swfIni.FilePath, ".swf");
        check("ini 往返：swf 型全部字段都对",
            reloaded.Kind == GameFolderKind.Swf && reloaded.NetPath == swfIni.NetPath
            && reloaded.Width == 800 && reloaded.Height == 600
            && reloaded.IsNewPage == SwfNetPathIni.NewPageTrue
            && reloaded.NewPageUrl == swfIni.NewPageUrl && !reloaded.HasProblem,
            reloaded.ErrorText);

        // htm 型：四行
        var htmIni = new SwfNetPathIni
        {
            FilePath = Path.Combine(work, "ini-htm", "SWFNetPath.ini"),
            FolderExt = ".htm",
            Kind = GameFolderKind.Htm,
            NetPath = "https://sda.4399.com/a/b/index.htm",
            Width = -1,
            Height = -1,
        };
        htmIni.Save();
        var htmLines = File.ReadAllText(htmIni.FilePath).Split('\n');
        check("htm 型 ini 的行结构（路径/htm/Unknown/Unknown）",
            htmLines.Length == 4 && htmLines[1] == "htm" && htmLines[2] == "Unknown" && htmLines[3] == "Unknown",
            string.Join(" | ", htmLines));
        var htmBack = SwfNetPathIni.FromFile(htmIni.FilePath, ".htm");
        check("ini 往返：htm 型（未知宽高）",
            htmBack.Kind == GameFolderKind.Htm && htmBack.Width == -1 && htmBack.Height == -1 && !htmBack.HasProblem,
            htmBack.ErrorText);

        // 容错
        var broken = SwfNetPathIni.Parse("https://a/b.swf\nhtm\n不是数字\nUnknown\nTrue\nhttps://a/p.htm", ".swf");
        check("ini 容错：类型不匹配会被指出并按文件夹后缀归一",
            broken.Kind == GameFolderKind.Swf && broken.ErrorText.Contains("类型不匹配"), broken.ErrorText);
        check("ini 容错：宽高乱写记问题并当未知（提示的行号是第 3 行）",
            broken.Width == -1 && broken.ErrorText.Contains("第3行"), broken.ErrorText);
        var missing = SwfNetPathIni.FromFile(Path.Combine(work, "没有这个文件.ini"), ".swf");
        report("ini 不存在时不抛异常（IsMissing 立起来）", missing.IsMissing && missing.NetPath.Length == 0);
        report("旧版 ini（没有类型行）会提示需要更新",
            SwfNetPathIni.Parse("https://a/b.swf", ".swf").HasProblem);
        report("文件夹后缀 → ini 类型",
            SwfNetPathIni.KindFromFolderExt(".swf") == GameFolderKind.Swf
            && SwfNetPathIni.KindFromFolderExt(".htm") == GameFolderKind.Htm
            && SwfNetPathIni.KindFromFolderExt("") == GameFolderKind.Unknown);
        check("ini 摘要文案（含新页面时带上网址）",
            reloaded.Describe().Contains("新页面") && reloaded.Describe().Contains("800×600"),
            reloaded.Describe());
    }

    // ---------------------------------------------------------------- 3. 命名规则（对应 InfoItem）

    private static void CheckEntryNaming(Action<string, bool, string> check, Action<string, bool> report)
    {
        check("文件夹名 → 游戏名（s 后缀只去尾字母）", CoreGameEntry.GameNameOfFolder("黄金矿工_a12345s") == "黄金矿工_a12345",
            CoreGameEntry.GameNameOfFolder("黄金矿工_a12345s"));
        check("文件夹名 → 游戏名（h 后缀只去尾字母）", CoreGameEntry.GameNameOfFolder("森林冰火人_c999h") == "森林冰火人_c999",
            CoreGameEntry.GameNameOfFolder("森林冰火人_c999h"));
        check("文件夹名 → 游戏名（旧格式没有 s/h，原样返回，界面会提示改名）", CoreGameEntry.GameNameOfFolder("老游戏_b77") == "老游戏_b77",
            CoreGameEntry.GameNameOfFolder("老游戏_b77"));
        check("文件夹名 → 后缀",
            CoreGameEntry.AppendixOfFolder("黄金矿工_a12345s") == "a12345"
            && CoreGameEntry.AppendixOfFolder("森林冰火人_c999h") == "c999"
            && CoreGameEntry.AppendixOfFolder("老游戏_b77") == "b77",
            CoreGameEntry.AppendixOfFolder("森林冰火人_c999h"));
        report("文件夹扩展名含义（s → .swf / h → .htm）",
            CoreGameEntry.FolderExtOf("x_a1s") == ".swf" && CoreGameEntry.FolderExtOf("x_a1h") == ".htm"
            && CoreGameEntry.FolderExtOf("x_a1") == ".unknown" && CoreGameEntry.FolderExtOf("乱起名") == ".unknown");
        report("旧格式文件夹被识别出来（要提醒用户改名）",
            CoreGameEntry.IsLegacy("老游戏_b77") && !CoreGameEntry.IsLegacy("新游戏_b77s")
            && !CoreGameEntry.IsLegacy("新游戏_b77h") && !CoreGameEntry.IsLegacy("乱起名"));
        check("swf 文件名的后缀",
            CoreGameEntry.AppendixOfSwfFile("黄金矿工_a12345.swf") == "a12345"
            && CoreGameEntry.AppendixOfSwfFile("没有后缀.swf") == "",
            CoreGameEntry.AppendixOfSwfFile("没有后缀.swf"));

        // 不撞车命名：游戏名_a1 → 游戏名_1_a1（对应 Python 里的 counter 循环）
        var taken = new HashSet<string> { "黄金矿工_a1" };
        check("重名时加序号：黄金矿工_a1 → 黄金矿工_1_a1",
            GamesLibrary.BuildUniqueGameName("黄金矿工", "a1", taken.Contains) == "黄金矿工_1_a1",
            GamesLibrary.BuildUniqueGameName("黄金矿工", "a1", taken.Contains));
        taken.Add("黄金矿工_1_a1");
        report("重名两次就排到 _2",
            GamesLibrary.BuildUniqueGameName("黄金矿工", "a1", taken.Contains) == "黄金矿工_2_a1");
        report("没名字时用「未命名」兜底",
            GamesLibrary.BuildUniqueGameName("", "a1", _ => false) == "未命名_a1");
        report("文件夹名 = 游戏名 + s / h",
            GamesLibrary.FolderNameForSwf("x_a1") == "x_a1s" && GamesLibrary.FolderNameForHtm("x_a1") == "x_a1h");

        // 文件夹撞车时自动挑一个空位
        var dirBase = NewDir("unique-dir");
        var desired = Path.Combine(dirBase, "abc");
        report("空目录直接用原名", BatchDownloadPlanner.MakeUniqueDirectory(desired) == desired);
        Directory.CreateDirectory(desired);
        check("目录已存在时补序号（abc → abc2）",
            BatchDownloadPlanner.MakeUniqueDirectory(desired) == desired + "2",
            BatchDownloadPlanner.MakeUniqueDirectory(desired));
    }

    // ---------------------------------------------------------------- 4. 批量下载计划（预处理统计）

    private static void CheckBatchPlan(Action<string, bool, string> check, Action<string, bool> report, string work)
    {
        var folder = NewDir("batch-local");
        File.WriteAllText(Path.Combine(folder, "already.swf"), "x");

        var lines = new[]
        {
            "assets/level1.swf",
            "",                        // 空行忽略（不计入总数）
            "  ",                      // 纯空白行也忽略
            "assets/level1.swf",       // 重复
            "../bad.swf",              // ..
            "has space.swf",           // 空格
            "already.swf",             // 已存在
            "assets/level2.swf",
        };
        var (entries, summary) = BatchDownloadPlanner.Plan(lines, folder, skipExisting: true, File.Exists);

        check("批量计划：空行不计入总数（8 行里只有 6 行有效）", summary.Total == 6, summary.Total.ToString());
        check("批量计划：不规范 2 个（含 .. 与空格）", summary.InvalidName == 2, summary.InvalidName.ToString());
        check("批量计划：重复 1 个", summary.Duplicate == 1, summary.Duplicate.ToString());
        check("批量计划：已存在 1 个", summary.AlreadyExists == 1, summary.AlreadyExists.ToString());
        check("批量计划：真正要下 2 个（已存在的那行被跳过）",
            entries.Count(e => e.Status == BatchLineStatus.Ready) == 2,
            string.Join(",", entries.Where(e => e.Status == BatchLineStatus.Ready).Select(e => e.RelativePath)));
        report("批量计划：每一行都有状态与说明",
            entries.Count == 6 && entries.All(e => e.Status != BatchLineStatus.Ready || e.Message.Length == 0));

        var (entries2, summary2) = BatchDownloadPlanner.Plan(lines, folder, skipExisting: false, File.Exists);
        report("关掉「不覆盖已有文件」之后已存在的那行也去下",
            summary2.AlreadyExists == 0 && entries2.Count(e => e.Status == BatchLineStatus.Ready) == 3);

        check("统计文案只列出非 0 项",
            summary.Describe().Contains("不规范名称 2 个") && summary.Describe().Contains("重复 1 个")
            && !summary.Describe().Contains("404"),
            summary.Describe("下载完成，"));
    }

    // ---------------------------------------------------------------- 5. 游戏目录操作（装入文件夹 / 改为 htm）

    private static void CheckLibraryOperations(Action<string, bool, string> check, Action<string, bool> report, string work)
    {
        var root = NewDir("library");
        var library = new GamesLibrary(root);

        File.WriteAllBytes(Path.Combine(root, "黄金矿工_a12345.swf"), new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(Path.Combine(root, "网页游戏_c999h"));
        File.WriteAllBytes(Path.Combine(root, "网页游戏_c999h", "网页游戏_c999.htm"),
            Encoding.UTF8.GetBytes("<html></html>"));
        new SwfNetPathIni
        {
            FilePath = Path.Combine(root, "网页游戏_c999h", "SWFNetPath.ini"),
            FolderExt = ".htm",
            Kind = GameFolderKind.Htm,
            NetPath = "https://x.com/a/index.htm",
            Width = 640,
            Height = 480,
        }.Save();
        Directory.CreateDirectory(Path.Combine(root, "老游戏_b7"));
        File.WriteAllText(Path.Combine(root, "说明.txt"), "不该出现在列表里");

        var list = library.List();
        check("扫列表：文件夹 + 顶层 swf，其它文件不算",
            list.Count == 3 && list.Any(e => e.FileOrFolderName == "黄金矿工_a12345.swf")
            && list.Any(e => e.FileOrFolderName == "网页游戏_c999h")
            && list.All(e => e.FileOrFolderName != "说明.txt"),
            string.Join(",", list.Select(e => e.FileOrFolderName)));

        var swfEntry = library.Find("黄金矿工_a12345.swf")!;
        report("散放 swf 条目：可装入文件夹、站点是 4399、不可转 htm",
            swfEntry.CanMoveIntoFolder && !swfEntry.CanConvertToHtm
            && swfEntry.Site == GameSite.Site4399 && swfEntry.OpenModeText == "Flash 播放器");
        var htmEntry = library.Find("网页游戏_c999h")!;
        report("网页文件夹条目：打开方式是网页、可批量下载",
            htmEntry.FolderExt == ".htm" && htmEntry.OpenModeText == "网页" && htmEntry.CanDownloadRelated
            && htmEntry.CanPlay && !htmEntry.CanConvertToHtm);
        report("旧命名文件夹：被标记出来、不让转 htm",
            library.Find("老游戏_b7") is { IsLegacyFolderName: true, CanConvertToHtm: false, FolderExt: ".unknown" });
        check("旧命名文件夹会被汇总列出",
            library.FindLegacyFolderNames().SequenceEqual(new[] { "老游戏_b7" }),
            string.Join(",", library.FindLegacyFolderNames()));

        // 装入文件夹：散放 swf → 游戏名_a12345s/游戏名_a12345.swf + ini
        var meta = new GameMeta
        {
            OriginUrl = "https://www.4399.com/flash/12345.htm",
            CleanName = "黄金矿工",
            Appendix = "a12345",
            Site = GameSite.Site4399,
            Width = 800,
            Height = 600,
            Kind = GameContentKind.Swf,
            ContentUrl = "https://sda.4399.com/4399swf/a/b/main.swf",
            IsNewPage = false,
        };
        var moved = library.MoveIntoFolder(swfEntry, meta);
        check("装入文件夹：建出 xxx_a12345s 目录并把 swf 放进去",
            moved.Ok && File.Exists(Path.Combine(root, "黄金矿工_a12345s", "黄金矿工_a12345.swf")), moved.Message);
        report("装入文件夹：原文件被移走（不是复制）", !File.Exists(Path.Combine(root, "黄金矿工_a12345.swf")));

        var folderEntry = library.Find("黄金矿工_a12345s")!;
        var movedIni = SwfNetPathIni.FromFile(folderEntry.IniFullPath, ".swf");
        check("装入文件夹：写好了 SWFNetPath.ini（路径 / 宽高 / 新页面=无）",
            movedIni.NetPath == meta.ContentUrl && movedIni.Width == 800 && movedIni.Height == 600
            && movedIni.IsNewPage == SwfNetPathIni.NewPageFalse,
            File.ReadAllText(folderEntry.IniFullPath).Replace("\n", " | "));

        // 改为 htm 打开
        var renamed = library.RenameFolderToHtm(folderEntry,
            new GamesLibrary.HtmConversion(meta.ContentUrl, "mainSwfGame.swf", 800, 600, FromNewPage: false));
        check("改为 htm：目录改名成 xxx_z12345h", renamed.Ok && renamed.Message == "黄金矿工_a12345h",
            $"ok={renamed.Ok} msg={renamed.Message} 条目名={folderEntry.FileOrFolderName} 游戏名={folderEntry.GameName} 后缀={folderEntry.Appendix} 目录里还有={string.Join(",", Directory.GetDirectories(root).Select(Path.GetFileName))}");
        report("改为 htm：里面的 swf 换成了 mainSwfGame.swf",
            File.Exists(Path.Combine(root, "黄金矿工_a12345h", "mainSwfGame.swf")));
        var htmFolder = library.Find("黄金矿工_a12345h");
        if (htmFolder is null)
        {
            report("改为 htm：条目变成网页型（找不到目录，后续断言跳过）", false);
            return;
        }
        report("改为 htm：条目变成网页型", htmFolder.FolderExt == ".htm" && htmFolder.OpenModeText == "网页");
        var convertedIni = SwfNetPathIni.FromFile(htmFolder.IniFullPath, ".htm");
        check("改为 htm：ini 变成 4 行的 htm 型且宽高保留",
            convertedIni.Kind == GameFolderKind.Htm && convertedIni.Width == 800 && convertedIni.Height == 600,
            File.ReadAllText(htmFolder.IniFullPath).Replace("\n", " | "));

        var wrote = library.WriteMainPage("黄金矿工_a12345h", "黄金矿工_a12345", Encoding.UTF8.GetBytes("<html>游戏主页</html>"));
        report("改为 htm：主页写进去之后 HasGameFile = true",
            wrote.Ok && library.Find("黄金矿工_a12345h")!.HasGameFile);

        // 删除
        var deleted = library.Delete(library.Find("黄金矿工_a12345h")!);
        report("删除文件夹条目：连同里面文件一起没了",
            deleted.Ok && !Directory.Exists(Path.Combine(root, "黄金矿工_a12345h")));
        var deleted2 = library.Delete(library.Find("网页游戏_c999h")!);
        report("删除文件夹（第二个）也成功",
            deleted2.Ok && !Directory.Exists(Path.Combine(root, "网页游戏_c999h")));

        // 路径越界防护
        report("游戏目录内相对路径解析正常",
            library.TryResolve("a/b.swf", out var resolved)
            && resolved.EndsWith(Path.Combine("a", "b.swf"), StringComparison.OrdinalIgnoreCase));
        report("拒绝 .. 越界", !library.TryResolve("../evil.swf", out _));
        report("拒绝绝对路径", !library.TryResolve(@"C:\Windows\evil.exe", out _));
    }

    // ---------------------------------------------------------------- 6. 游戏列表的搜索 / 排序

    /// <summary>
    /// 游戏列表搜索 / 排序（与主页的 ToolFilter 一个套路，纯函数直接断言）。
    /// 这一组是补 v0.5.1 的新功能：搜索框、排序下拉、正逆序。
    /// </summary>
    private static void CheckGameFilter(Action<string, bool, string> check, Action<string, bool> report)
    {
        var root = NewDir("filter");
        var library = new GamesLibrary(root);

        // 造三个游戏：4399 的散放 swf、17yy 的网页文件夹、7k7k 的播放器文件夹。
        // 大小刻意拉开（100 / 200 / 300 字节），否则「按大小」会平局、断言就依赖排序稳定性了。
        // 注意「文件夹大小」= 里面所有文件之和，所以 h 文件夹里那个 htm 桩也要算进去。
        File.WriteAllBytes(Path.Combine(root, "黄金矿工_a100.swf"), new byte[100]);
        Directory.CreateDirectory(Path.Combine(root, "森林冰火人_c200h"));
        File.WriteAllBytes(Path.Combine(root, "森林冰火人_c200h", "森林冰火人_c200.htm"), new byte[200]);
        Directory.CreateDirectory(Path.Combine(root, "小破孩_b300s"));
        File.WriteAllBytes(Path.Combine(root, "小破孩_b300s", "小破孩_b300.swf"), new byte[300]);

        var all = library.List();
        check("搜索/排序的测试数据（3 个游戏）", all.Count == 3, string.Join(",", all.Select(e => e.FileOrFolderName)));

        // ---- 关键词切分 ----
        check("关键词切分：空格 / 中英文逗号 / 顿号都算分隔符",
            GameFilter.SplitKeywords("4399 网页, 冰火、a100").SequenceEqual(new[] { "4399", "网页", "冰火", "a100" }),
            string.Join("|", GameFilter.SplitKeywords("4399 网页, 冰火、a100")));
        report("空搜索词切出空数组（调用方据此跳过过滤）",
            GameFilter.SplitKeywords("").Length == 0 && GameFilter.SplitKeywords("   ").Length == 0
            && GameFilter.SplitKeywords(null).Length == 0);

        // ---- 搜索命中 ----
        IReadOnlyList<string> Names(IEnumerable<GameEntry> entries) => entries.Select(e => e.FileOrFolderName).ToList();

        check("搜索：按名字命中",
            Names(GameFilter.Apply(all, "冰火", GameSortMode.Default)).SequenceEqual(new[] { "森林冰火人_c200h" }),
            string.Join(",", Names(GameFilter.Apply(all, "冰火", GameSortMode.Default))));
        report("搜索：按站点后缀命中（a100）",
            Names(GameFilter.Apply(all, "a100", GameSortMode.Default)).SequenceEqual(new[] { "黄金矿工_a100.swf" }));
        check("搜索：按站点名命中（4399 → 散放的 a100 那条）",
            Names(GameFilter.Apply(all, "4399", GameSortMode.Default)).SequenceEqual(new[] { "黄金矿工_a100.swf" }),
            string.Join(",", Names(GameFilter.Apply(all, "4399", GameSortMode.Default))));
        check("搜索：按打开方式命中（网页 → 只剩 h 文件夹）",
            Names(GameFilter.Apply(all, "网页", GameSortMode.Default)).SequenceEqual(new[] { "森林冰火人_c200h" }),
            string.Join(",", Names(GameFilter.Apply(all, "网页", GameSortMode.Default))));
        report("搜索：大小写不敏感", GameFilter.Apply(all, "C200", GameSortMode.Default).Count == 1);
        report("搜索：多个关键词是 AND（4399 + 矿工 命中；4399 + 冰火 不命中）",
            GameFilter.Apply(all, "4399 矿工", GameSortMode.Default).Count == 1
            && GameFilter.Apply(all, "4399 冰火", GameSortMode.Default).Count == 0);
        report("搜索：没命中就是空列表（界面会给出提示文案）",
            GameFilter.Apply(all, "不存在的游戏", GameSortMode.Default).Count == 0);

        // ---- 排序 ----
        // 名字排序用的是 StringComparer.CurrentCulture（中文系统按拼音），所以这里只断言
        // 「顺序稳定 + 正逆序互补」，不写死具体次序（写死了换台机器/换个区域就红）。
        var byName = Names(GameFilter.Sort(all, GameSortMode.Name, false));
        report("排序：按名字（3 个都在，且正序与逆序正好相反）",
            byName.Count == 3
            && byName.SequenceEqual(Names(GameFilter.Sort(all, GameSortMode.Name, true)).Reverse()));
        check("排序：按大小（小 → 大）",
            Names(GameFilter.Sort(all, GameSortMode.Size, false))
                .SequenceEqual(new[] { "黄金矿工_a100.swf", "森林冰火人_c200h", "小破孩_b300s" }),
            string.Join(",", Names(GameFilter.Sort(all, GameSortMode.Size, false))));
        check("排序：按站点分组的固定次序（4399 → 7k7k → 17yy）",
            Names(GameFilter.Sort(all, GameSortMode.Site, false))
                .SequenceEqual(new[] { "黄金矿工_a100.swf", "小破孩_b300s", "森林冰火人_c200h" }),
            string.Join(",", Names(GameFilter.Sort(all, GameSortMode.Site, false))));
        report("排序：站点次序不是按字符串比（否则 7k7k 会跑到 4399 前面）",
            all.First(e => e.Appendix[0] == 'a').SiteSortKey < all.First(e => e.Appendix[0] == 'b').SiteSortKey
            && all.First(e => e.Appendix[0] == 'b').SiteSortKey < all.First(e => e.Appendix[0] == 'c').SiteSortKey);

        // ---- 「按上次游玩」：刚玩过的排最上面，从没玩过的垫底 ----
        var played = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase)
        {
            ["黄金矿工_a100.swf"] = new DateTime(2026, 1, 1, 10, 0, 0),
            ["森林冰火人_c200h"] = new DateTime(2026, 5, 1, 10, 0, 0),
        };
        check("排序：按上次游玩（最近玩的在最上面，从没玩过的垫底）",
            Names(GameFilter.Sort(all, GameSortMode.LastPlayed, false, played))
                .SequenceEqual(new[] { "森林冰火人_c200h", "黄金矿工_a100.swf", "小破孩_b300s" }),
            string.Join(",", Names(GameFilter.Sort(all, GameSortMode.LastPlayed, false, played))));
        report("排序：按上次游玩 + 逆序 = 整个翻过来",
            Names(GameFilter.Sort(all, GameSortMode.LastPlayed, true, played))
                .SequenceEqual(new[] { "小破孩_b300s", "黄金矿工_a100.swf", "森林冰火人_c200h" }));
        report("排序：没给游玩记录时不会崩（全当从没玩过）",
            GameFilter.Sort(all, GameSortMode.LastPlayed).Count == 3);
        report("排序：搜索和排序可以叠加（搜 4399 再按大小排）",
            Names(GameFilter.Apply(all, "4399", GameSortMode.Size, false, played))
                .SequenceEqual(new[] { "黄金矿工_a100.swf" }));

        // ---- 空状态提示 ----
        check("空状态：目录里真没游戏时说的是“去下载”",
            GameFilter.EmptyHint(0, 0, "").Contains("还没有游戏"), GameFilter.EmptyHint(0, 0, ""));
        check("空状态：被搜索筛没了时说清楚是筛的（带上关键词与总数）",
            GameFilter.EmptyHint(3, 0, "冰火").Contains("冰火") && GameFilter.EmptyHint(3, 0, "冰火").Contains("3"),
            GameFilter.EmptyHint(3, 0, "冰火"));
        report("空状态：有结果时不给提示（控件靠空串隐藏）", GameFilter.EmptyHint(3, 2, "冰火").Length == 0);

        // ---- 游玩时间记录（VM 用的表） ----
        report("游玩时间表的键是“文件名/文件夹名”，与列表项对得上",
            played.Keys.All(k => all.Any(e => e.FileOrFolderName == k)));
    }

    /// <summary>
    /// 删掉一个游戏后，过期的「上次游玩」记录应该被清掉（否则配置会越滚越大）。
    /// 清理发生在 <c>MarkPlayed</c> 里，所以这里先删文件、刷新、再玩一个游戏来触发它。
    /// </summary>
    private static bool PrunePlayedAndCheck(FlashDownloaderViewModel vm, string gamesDir)
    {
        var victim = vm.Games.FirstOrDefault(g => g.Entry.FileOrFolderName == "森林冰火人_c300h");
        if (victim is null) return false;
        Directory.Delete(Path.Combine(gamesDir, victim.Entry.FileOrFolderName), recursive: true);
        vm.RefreshGames(false);

        var another = vm.Games.FirstOrDefault();
        if (another is null) return false;
        vm.MarkPlayed(another.Entry); // 触发 MarkPlayed → 顺手清理死记录（同样不碰 PlayEntry）

        return !vm.Cfg.LastPlayedAt.ContainsKey(victim.Entry.FileOrFolderName);
    }

    // ---------------------------------------------------------------- 7. 本地服务器的路径映射

    private static void CheckWebMapping(Action<string, bool, string> check, Action<string, bool> report,
        string games, string tools)
    {
        File.WriteAllText(Path.Combine(tools, "frame.htm"), "<html>frame</html>");
        var gameFolder = Path.Combine(games, "游戏_a1h");
        Directory.CreateDirectory(gameFolder);
        File.WriteAllText(Path.Combine(gameFolder, "游戏_a1.htm"), "<html>game</html>");

        var frame = FlashWebServer.Resolve("/frame.htm", games, tools);
        check("/frame.htm 映射到工具目录",
            frame.Kind == WebResolutionKind.File && frame.FullPath.EndsWith("frame.htm", StringComparison.OrdinalIgnoreCase),
            frame.FullPath);

        var img = FlashWebServer.Resolve("/img/bg1.jpg?x=1", games, tools);
        check("查询串会被去掉，/img/… 也在工具目录里",
            img.Kind == WebResolutionKind.File
            && img.FullPath.EndsWith(Path.Combine("img", "bg1.jpg"), StringComparison.OrdinalIgnoreCase),
            img.FullPath);

        var api = FlashWebServer.Resolve("/tool/substiH5WanApi.js", games, tools);
        check("/tool/substiH5WanApi.js 映射到工具目录（跨域替代 API）",
            api.Kind == WebResolutionKind.File
            && api.FullPath.EndsWith(Path.Combine("tool", "substiH5WanApi.js"), StringComparison.OrdinalIgnoreCase),
            api.FullPath);

        var game = FlashWebServer.Resolve("/games/%E6%B8%B8%E6%88%8F_a1h/%E6%B8%B8%E6%88%8F_a1.htm", games, tools);
        check("/games/… 百分号解码后映射到游戏目录",
            game.Kind == WebResolutionKind.GameFile && File.Exists(game.FullPath), game.FullPath);

        var nested = FlashWebServer.Resolve("/games/游戏_a1h/assets/level1.swf", games, tools);
        check("游戏目录里可以带子目录",
            nested.Kind == WebResolutionKind.GameFile
            && nested.GameRelativePath == Path.Combine("游戏_a1h", "assets", "level1.swf"),
            nested.GameRelativePath);

        report("拒绝 .. 逃逸",
            FlashWebServer.Resolve("/games/../../windows/win.ini", games, tools).Kind == WebResolutionKind.NotFound
            && FlashWebServer.Resolve("/../secret", games, tools).Kind == WebResolutionKind.NotFound);
        report("目录请求会补 index.html",
            FlashWebServer.Resolve("/games/游戏_a1h/", games, tools).GameRelativePath
                == Path.Combine("游戏_a1h", "index.html"));
        report("顶层路径映射到工具目录（文件不存在时由服务器回 404）",
            FlashWebServer.Resolve("/nothing-here", games, tools) is { Kind: WebResolutionKind.File } r
            && !File.Exists(r.FullPath));
        report("根目录没放 index.html 时，/ 会解析到一个不存在的文件（由服务器回 404）",
            !File.Exists(FlashWebServer.Resolve("/", games, tools).FullPath));
        File.WriteAllText(Path.Combine(tools, "index.html"), "<html>index</html>");
        report("工具目录里放了 index.html 之后，根目录就能访问了",
            FlashWebServer.Resolve("/", games, tools).Kind == WebResolutionKind.File
            && File.Exists(FlashWebServer.Resolve("/", games, tools).FullPath));
        File.Delete(Path.Combine(tools, "index.html"));
        report("MIME 判定（htm / swf / js / png）",
            FlashWebServer.MimeTypeOf("a.htm").StartsWith("text/html")
            && FlashWebServer.MimeTypeOf("a.swf") == "application/x-shockwave-flash"
            && FlashWebServer.MimeTypeOf("a.js").StartsWith("application/javascript")
            && FlashWebServer.MimeTypeOf("a.png") == "image/png");
    }

    // ---------------------------------------------------------------- 7. 内置网页模板

    private static void CheckTemplates(Action<string, bool, string> check, Action<string, bool> report)
    {
        var frame = FlashWebAssets.FrameHtml();
        check("播放页模板：用 URLSearchParams 取 game/width/height，并先 HEAD 探测",
            frame.Contains("URLSearchParams") && frame.Contains("HEAD") && frame.Contains("games/"),
            $"len={frame.Length}");
        report("播放页模板：重玩 / 放大 / 缩小 / 全屏四个按钮都在",
            frame.Contains("btnReplay") && frame.Contains("btnZoom1") && frame.Contains("btnZoom2")
            && frame.Contains("btnFullscreen"));

        var flashPage = FlashWebAssets.BuildFlashPage("mainSwfGame.swf", "黄金矿工");
        report("Flash 加载页：占位符被替换成真实 swf 名与标题",
            flashPage.Contains("mainSwfGame.swf") && flashPage.Contains("黄金矿工")
            && !flashPage.Contains(FlashWebAssets.GamePlaceholder)
            && !flashPage.Contains(FlashWebAssets.TitlePlaceholder));

        var api = FlashWebAssets.H5ApiScript();
        report("替代 API：playAd 直接回调“播放结束”（等于 0 秒广告）",
            api.Contains("playAd") && api.Contains("10001") && api.Contains("10000"));
        report("替代 API：checkAPI / canPlayAd / save 都在",
            api.Contains("checkAPI") && api.Contains("canPlayAd") && api.Contains("save"));

        var tools = NewDir("templates");
        var written = FlashWebAssets.EnsureTemplates(tools);
        check("模板释放到磁盘（3 个文件，含 tool/substiH5WanApi.js）",
            written == 3
            && File.Exists(Path.Combine(tools, "frame.htm"))
            && File.Exists(Path.Combine(tools, "FlashPage.htm"))
            && File.Exists(Path.Combine(tools, "tool", "substiH5WanApi.js")),
            written.ToString());
        File.WriteAllText(Path.Combine(tools, "frame.htm"), "用户改过的内容");
        var again = FlashWebAssets.EnsureTemplates(tools);
        check("第二次释放不会覆盖用户改过的模板（可定制）",
            again == 0 && File.ReadAllText(Path.Combine(tools, "frame.htm")) == "用户改过的内容", again.ToString());
    }

    // ---------------------------------------------------------------- 8. ViewModel 接线（无头构造，不碰界面）

    /// <summary>
    /// 直接把 ViewModel 造出来检查“接线”有没有接错。
    /// 这一组是补上一次真实踩坑：`Library` 曾经写成字段初始化器，而它在构造函数体之前执行，
    /// 于是拿到空目录 —— 首次运行的说明文件被写到了仓库根目录（截图时才看见）。
    /// 构造 VM 不需要 WPF Application（`ToolViewModelBase.Ui` 会直接执行）。
    /// </summary>
    private static void CheckViewModelWiring(Action<string, bool, string> check, Action<string, bool> report)
    {
        var home = NewDir("vm-home");
        var previousHome = Environment.GetEnvironmentVariable("YLT_HOME");
        var previousNoServer = Environment.GetEnvironmentVariable("YLT_NO_SERVER");
        Environment.SetEnvironmentVariable("YLT_HOME", home);
        // 自检里别真去占 1055 端口（截图 / 别的自检可能在用）
        Environment.SetEnvironmentVariable("YLT_NO_SERVER", "1");
        try
        {
            var vm = new FlashDownloaderViewModel();
            try
            {
                // 注意：AppPaths.Root 是进程级缓存的单例，这里改 YLT_HOME 已经晚了（本进程早就解析过），
                // 所以“期望值”必须按当前生效的数据根来算，不能假设等于 home
                var expectedGames = Path.Combine(AppPaths.ToolDataDir("flash-downloader"), "games");
                check("VM 构造后游戏目录不是空的（字段初始化顺序坑）",
                    vm.Library.Root.Length > 0 && vm.GamesDirectory.Length > 0, $"[{vm.Library.Root}]");
                check("VM 默认游戏目录 = <数据根>\\Data\\flash-downloader\\games",
                    string.Equals(vm.Library.Root, expectedGames, StringComparison.OrdinalIgnoreCase),
                    vm.Library.Root);
                check("首次运行的「怎么放游戏.txt」写在游戏目录里（不是仓库根）",
                    File.Exists(Path.Combine(expectedGames, "怎么放游戏.txt")), expectedGames);
                report("没有往当前工作目录乱写文件",
                    !File.Exists(Path.Combine(Environment.CurrentDirectory, "怎么放游戏.txt")));
                report("自检模式下本地服务器不启动（YLT_NO_SERVER=1）", !vm.Server.IsRunning);
                report("VM 的选项默认值可用（并发 / 端口 / 生效目录都在）",
                    vm.Cfg.MaxParallel > 0 && vm.Cfg.ServerStartPort > 0
                    && vm.Cfg.EffectiveGamesDirectory == expectedGames);

                // 换目录：EffectiveGamesDirectory 要跟着走，并且绝对路径与默认目录互不干扰
                var custom = Path.Combine(home, "外部游戏目录");
                vm.Cfg.GamesFolder = custom;
                report("填了 GamesFolder 之后生效目录变成它",
                    string.Equals(vm.Cfg.EffectiveGamesDirectory, custom, StringComparison.OrdinalIgnoreCase));
                vm.Cfg.GamesFolder = "";
                report("清空 GamesFolder 之后回落到默认目录",
                    string.Equals(vm.Cfg.EffectiveGamesDirectory, expectedGames, StringComparison.OrdinalIgnoreCase));

                // ---- 列表的搜索 / 排序 / 游玩记录（v0.5.1 新增，这里跑 VM 这一层） ----
                var gamesDir = vm.Library.Root;
                Directory.CreateDirectory(gamesDir);
                File.WriteAllBytes(Path.Combine(gamesDir, "黄金矿工_a100.swf"), new byte[100]);
                File.WriteAllBytes(Path.Combine(gamesDir, "小破孩_b200.swf"), new byte[200]);
                Directory.CreateDirectory(Path.Combine(gamesDir, "森林冰火人_c300h"));
                File.WriteAllBytes(Path.Combine(gamesDir, "森林冰火人_c300h", "森林冰火人_c300.htm"),
                    new byte[300]);

                vm.RefreshGames(false);
                check("VM：扫出 3 个游戏并显示成 3 行", vm.Games.Count == 3 && vm.GamesSummary.Contains("共 3 个"),
                    $"{vm.Games.Count} / {vm.GamesSummary}");

                // 默认排序是「按上次游玩」：三个都没玩过 → 顺序稳定即可
                check("VM：默认排序方式是按上次游玩",
                    vm.SelectedGameSort.Value == GameSortMode.LastPlayed, vm.SelectedGameSort.Label);

                vm.GameSearchText = "冰火";
                check("VM：搜索框一输入就过滤（1 行）",
                    vm.Games.Count == 1 && vm.Games[0].Entry.FileOrFolderName == "森林冰火人_c300h",
                    string.Join(",", vm.Games.Select(g => g.Entry.FileOrFolderName)));
                check("VM：统计文案显示“筛出 1 / 共 3 个”",
                    vm.GamesSummary.Contains("筛出 1 / 共 3"), vm.GamesSummary);
                report("VM：搜索时能识别“有搜索词”（✕ 按钮靠它显示）", vm.HasGameSearchText);

                vm.GameSearchText = "找不到的东西";
                check("VM：搜不到时列表为空并给出提示",
                    vm.Games.Count == 0 && vm.GamesEmptyHint.Contains("找不到的东西"), vm.GamesEmptyHint);

                vm.GameSearchText = "";
                report("VM：清空搜索词后 3 个游戏都回来", vm.Games.Count == 3 && vm.GamesEmptyHint.Length == 0);

                // 排序切换
                vm.SelectedGameSort = OptionItem<GameSortMode>.Find(vm.GameSortOptions, GameSortMode.Size)!;
                check("VM：切换成“按大小”后小游戏在最前面",
                    vm.Games[0].Entry.FileOrFolderName == "黄金矿工_a100.swf",
                    string.Join(",", vm.Games.Select(g => g.Entry.FileOrFolderName)));
                var beforeToggle = vm.SortDirectionText;
                vm.ToggleSortDirectionCommand.Execute(null);
                check("VM：正逆序按钮真的把顺序翻过来了",
                    vm.SortDirectionText != beforeToggle
                    && vm.Games[0].Entry.FileOrFolderName == "森林冰火人_c300h",
                    $"{vm.SortDirectionText} / {string.Join(",", vm.Games.Select(g => g.Entry.FileOrFolderName))}");
                report("VM：正逆序状态写进了配置（下次打开还是它）", vm.Cfg.GameSortDescending);

                // 游玩记录 → 「按上次游玩」排序
                vm.SelectedGameSort = OptionItem<GameSortMode>.Find(vm.GameSortOptions, GameSortMode.LastPlayed)!;
                var target = vm.Games.First(g => g.Entry.FileOrFolderName == "小破孩_b200.swf");
                // ⚠ 这里绝对不能调 PlayEntry —— 它会真的启动 FlashPlayer 窗口，无头自检会卡死（踩过）。
                //   「玩过」的簿记直接调 MarkPlayed 验证。
                vm.MarkPlayed(target.Entry);
                report("VM：游玩后会写进 LastPlayedAt（键 = 文件名）",
                    vm.Cfg.LastPlayedAt.ContainsKey("小破孩_b200.swf"));
                if (vm.Cfg.LastPlayedAt.ContainsKey("小破孩_b200.swf"))
                {
                    vm.Cfg.LastPlayedAt["小破孩_b200.swf"] = DateTime.Now.AddMinutes(5); // 保证比别的都新
                    vm.Cfg.GameSortDescending = false;
                    vm.RefreshGames(false);
                    check("VM：刚玩过的自动窜到最上面",
                        vm.Games[0].Entry.FileOrFolderName == "小破孩_b200.swf",
                        string.Join(",", vm.Games.Select(g => g.Entry.FileOrFolderName)));
                }
                check("VM：删掉游戏后再刷新，过期的游玩记录会被清掉",
                    PrunePlayedAndCheck(vm, gamesDir), string.Join(",", vm.Cfg.LastPlayedAt.Keys));
            }
            finally
            {
                vm.NotifyClosed();
                vm.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("YLT_HOME", previousHome);
            Environment.SetEnvironmentVariable("YLT_NO_SERVER", previousNoServer);
        }
    }

    // ---------------------------------------------------------------- 9. 真下载 + 本地服务器 + 自动下载

    private static void CheckNetworkAndDownloads(Action<string, bool, string> check, Action<string, bool> report,
        string work, string games, string tools)
    {
        // 自检自己起一个「假游戏站」，把真实下载链路整条跑通（不碰外网）
        using var site = new FakeGameSite();
        if (!site.Started)
        {
            if (!System.Net.HttpListener.IsSupported)
            {
                // 环境不支持 HttpListener（HTTP.SYS 不可用：某些受限沙箱 / 精简系统就是这样），
                // 整个「真下载」分组直接跳过。**不算失败**——它验的是下载逻辑，不是本机能不能开 HTTP 服务。
                check("跳过整组「真下载」自检：当前环境不支持 HttpListener（HttpListener.IsSupported=False）",
                    true, "本机 HTTP.SYS 不可用");
                return;
            }
            // 支持却起不来，那就是真问题：把原因打出来
            check("自检用的假游戏站能启动（本地 HTTP 服务器）", false,
                $"最近一次失败：{site.Error}（单独试 10999：{ProbeListener()}）");
            return;
        }

        var root = NewDir("net-games");
        var library = new GamesLibrary(root);
        var config = new FlashDownloaderConfig
        {
            ConnectTimeoutSeconds = 5,
            ReadTimeoutSeconds = 15,
            Replace4399H5Api = true,
        };
        using var http = new FlashHttp(config.ConnectTimeoutSeconds, config.ReadTimeoutSeconds);
        var downloader = new GameDownloader(http, config);

        // ---- (a) 4399 直接 swf ----
        var resolved = downloader.ResolveAsync(site.DirectGameUrl).GetAwaiter().GetResult();
        check("解析 4399 直接 swf 的游戏页",
            resolved.Ok && resolved.Meta is { Kind: GameContentKind.Swf, IsNewPage: false }
            && resolved.Meta.CleanName == "黄金矿工双人版"
            && resolved.Meta.ContentUrl == site.DirectSwfUrl,
            resolved.Ok ? resolved.Meta!.Describe() : resolved.Error);

        check("解析结果里带上了游戏宽高", resolved.Meta is { Width: 800, Height: 600 },
            resolved.Meta?.SizeText ?? "");

        var download = downloader.DownloadAsync(library, resolved.Meta!).GetAwaiter().GetResult();
        check("下载直接 swf：落成 游戏名_a12345.swf 且内容正确",
            download.Ok && download.FileOrFolderName == "黄金矿工双人版_z12345.swf"
            && File.Exists(Path.Combine(root, download.FileOrFolderName)),
            download.Ok ? download.FileOrFolderName : download.Error);
        check("下载的 swf 内容与服务器上的一致",
            download.Ok && File.ReadAllText(Path.Combine(root, download.FileOrFolderName)) == FakeGameSite.SwfBody,
            download.FileOrFolderName);

        // ---- (b) 再下一次：自动改名不覆盖 ----
        var download2 = downloader.DownloadAsync(library, resolved.Meta!).GetAwaiter().GetResult();
        check("重复下载同一个游戏会自动加序号，不覆盖已有文件",
            download2.Ok && download2.FileOrFolderName == "黄金矿工双人版_1_z12345.swf",
            download2.FileOrFolderName);

        // ---- (c) 4399 新页面 → swf ----
        var newPage = downloader.ResolveAsync(site.NewPageGameUrl).GetAwaiter().GetResult();
        check("解析 4399「新页面」游戏：拿到 swf 与新页面地址",
            newPage.Ok && newPage.Meta is { IsNewPage: true, Kind: GameContentKind.Swf }
            && newPage.Meta.NewPageUrl == site.NewPageUrl
            && newPage.Meta.ContentUrl.EndsWith("/mainSwfGame.swf", StringComparison.OrdinalIgnoreCase),
            newPage.Ok ? newPage.Meta!.Describe() : newPage.Error);

        var newPageDownload = downloader.DownloadAsync(library, newPage.Meta!).GetAwaiter().GetResult();
        check("下载新页面里的 swf（必须带 Referer 才拿得到）",
            newPageDownload.Ok && File.Exists(Path.Combine(root, newPageDownload.FileOrFolderName)),
            newPageDownload.Ok ? newPageDownload.FileOrFolderName : newPageDownload.Error);

        // ---- (d) h5 网页游戏 ----
        var h5 = downloader.ResolveAsync(site.H5GameUrl).GetAwaiter().GetResult();
        var h5PageText = h5.Meta?.PageContent is null ? "" : Encoding.UTF8.GetString(h5.Meta.PageContent);
        check("解析 h5 网页游戏：类型是 htm 且官方 API 已被替换",
            h5.Ok && h5.Meta is { Kind: GameContentKind.Htm }
            && h5PageText.Contains("/tool/substiH5WanApi.js") && !h5PageText.Contains("h.api.4399.com"),
            h5.Ok ? h5.Meta!.Describe() : h5.Error);

        var h5Download = downloader.DownloadAsync(library, h5.Meta!).GetAwaiter().GetResult();
        var h5Folder = h5Download.FileOrFolderName;
        check("下载 h5 游戏：建出 游戏名_a12346h 文件夹 + 网页 + ini",
            h5Download.Ok && h5Folder == "欢乐厨房_z12347h"
            && File.Exists(Path.Combine(root, h5Folder, "欢乐厨房_z12347.htm"))
            && File.Exists(Path.Combine(root, h5Folder, "SWFNetPath.ini")),
            h5Download.Ok ? h5Folder : h5Download.Error);
        var h5Ini = SwfNetPathIni.FromFile(Path.Combine(root, h5Folder, "SWFNetPath.ini"), ".htm");
        check("h5 游戏的 ini 记下了网页地址与宽高",
            h5Ini.Kind == GameFolderKind.Htm && h5Ini.NetPath == site.H5PageUrl
            && h5Ini.Width == 960 && h5Ini.Height == 540,
            h5Ini.Describe());

        // ---- (e) 关联文件批量下载（真下载，含 404 / 重复 / 已存在 / 不规范） ----
        var h5Entry = library.Find(h5Folder)!;
        var batchLines = new[]
        {
            "assets/communal.swf",
            "assets/level1.swf",
            "assets/missing.swf",      // 服务器返回 404
            "assets/communal.swf",     // 重复
            "../evil.swf",             // 不规范
            "欢乐厨房_z12347.htm",      // 已存在（就是游戏主页本身）
        };
        var (plan, summary) = BatchDownloadPlanner.Plan(batchLines, h5Entry.FullPath, true, File.Exists);
        var related = new RelatedFileDownloader(http);
        related.RunAsync(
            GamePageParser.GetParentUrl(h5Ini.NetPath),
            h5Ini.NetPath,
            h5Entry.FullPath,
            plan,
            summary,
            4,
            null,
            CancellationToken.None).GetAwaiter().GetResult();

        check("批量下载：两个文件下成功（含子目录自动创建）",
            summary.Success == 2
            && File.Exists(Path.Combine(h5Entry.FullPath, "assets", "communal.swf"))
            && File.Exists(Path.Combine(h5Entry.FullPath, "assets", "level1.swf")),
            summary.Describe());
        check("批量下载：404 被单独统计，且不会留下空文件",
            summary.NotFound == 1 && !File.Exists(Path.Combine(h5Entry.FullPath, "assets", "missing.swf")),
            summary.Describe());
        check("批量下载：重复 / 不规范 / 已存在都体现在统计里",
            summary.Duplicate == 1 && summary.InvalidName == 1 && summary.AlreadyExists == 1,
            summary.Describe());
        report("批量下载：不规范的行不会写到游戏文件夹外面", !File.Exists(Path.Combine(work, "evil.swf")));

        // ---- (f) 本地服务器 ----
        var webRoot = NewDir("web-tools");
        FlashWebAssets.EnsureTemplates(webRoot);
        using var server = new FlashWebServer(library, webRoot);
        if (!server.Start(10655))
        {
            check("本地服务器能启动（从 10655 起找端口）", false, server.LastError);
            return;
        }
        check("本地服务器启动并拿到地址", server.IsRunning && server.BaseUrl.StartsWith("http://localhost:"),
            server.BaseUrl);

        var frameText = HttpGet(server.BaseUrl + "frame.htm");
        check("本地服务器：/frame.htm 能打开（内容里有重玩按钮）",
            frameText.Contains("btnReplay"), $"len={frameText.Length}");
        var apiText = HttpGet(server.BaseUrl + "tool/substiH5WanApi.js");
        check("本地服务器：/tool/substiH5WanApi.js 能打开（跨域替代 API）",
            apiText.Contains("h5api"), $"len={apiText.Length}");
        var gameText = HttpGet(server.BaseUrl + "games/" + Uri.EscapeDataString(h5Folder) + "/assets/level1.swf");
        check("本地服务器：游戏目录里的文件能取到", gameText == FakeGameSite.Level1Body, gameText);

        // ---- (g) 自动下载所需文件 ----
        var autoPath1 = Path.Combine(h5Entry.FullPath, "assets", "auto1.swf");
        var autoPath2 = Path.Combine(h5Entry.FullPath, "assets", "auto2.swf");
        report("自动下载前：这两个文件本地确实没有",
            !File.Exists(autoPath1) && !File.Exists(autoPath2));

        server.FileFetcher = async (request, ct) =>
        {
            var localPath = request.LocalFullPath;

            var result = await http.DownloadToFileAsync(request.Url, localPath, request.Referer, true, ct);
            if (result.Ok) return new FlashWebServer.FetchOutcome(true, false, "");
            return new FlashWebServer.FetchOutcome(false, result.Status == FetchStatus.NotFound, result.Message);
        };
        server.BeginSession(h5Folder, GamePageParser.GetParentUrl(h5Ini.NetPath), h5Ini.NetPath,
            "欢乐厨房_z12347.htm");

        // 按原工具的设计：游戏向 localhost 要一个本地没有的文件，服务器先下下来再答复
        var autoBody = HttpGet(server.BaseUrl + "games/" + Uri.EscapeDataString(h5Folder) + "/assets/auto1.swf");
        check("自动下载：游戏请求缺失文件 → 服务器先下载再答复（拿到真正的内容）",
            autoBody == FakeGameSite.Auto1Body && File.Exists(autoPath1), autoBody);

        var auto2Body = HttpGet(server.BaseUrl + "games/" + Uri.EscapeDataString(h5Folder) + "/assets/auto2.swf");
        check("自动下载：第二个缺失文件也能补上",
            auto2Body == FakeGameSite.Auto2Body && File.Exists(autoPath2), auto2Body);

        var session = server.Session!;
        check("自动下载：统计正确（总共 2 个、成功 2 个）",
            session.Total == 2 && session.Success == 2 && session.NotFound == 0 && session.Failed == 0,
            session.DescribeStats());
        check("自动下载：面板文案里带上统计", session.DescribeStats().Contains("成功2个"),
            session.DescribeStats());

        var again = HttpGet(server.BaseUrl + "games/" + Uri.EscapeDataString(h5Folder) + "/assets/auto1.swf");
        check("自动下载过的文件第二次直接命中本地（不再重复下载）",
            again == FakeGameSite.Auto1Body && session.Total == 2, session.DescribeStats());

        var missingCode = HttpGetStatus(server.BaseUrl + "games/" + Uri.EscapeDataString(h5Folder) + "/assets/none.swf",
            out var missingBody);
        check("自动下载：网上也没有的文件如实回 404（不塞假内容）",
            missingCode == HttpStatusCode.NotFound && session.NotFound == 1,
            $"{missingCode} / {missingBody}");

        server.EndSession();
        var afterStop = HttpGetStatus(server.BaseUrl + "games/" + Uri.EscapeDataString(h5Folder) + "/assets/auto3.swf",
            out _);
        check("停止自动下载之后：缺失文件直接 404（不再联网）", afterStop == HttpStatusCode.NotFound,
            afterStop.ToString());

        server.Stop();
        report("本地服务器能停下来", !server.IsRunning);
    }

    // ---------------------------------------------------------------- 自检用的小工具


    private static string HttpGet(string url)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            return client.GetStringAsync(url).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            return $"[请求失败] {ex.Message}";
        }
    }

    private static HttpStatusCode HttpGetStatus(string url, out string body)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var response = client.GetAsync(url).GetAwaiter().GetResult();
            body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return response.StatusCode;
        }
        catch (Exception ex)
        {
            body = ex.Message;
            return 0;
        }
    }

    private static string NewDir(string name)
    {
        // 用 SelfCheckScratch：系统临时目录不可写的环境（受限沙箱）会自动退到自检程序旁边
        var dir = SelfCheckScratch.MakeFile($"ylt-flash-{name}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 自检专用的「假游戏站」：模拟 4399 的三种页面结构（直接 swf / 新页面 / h5），
    /// 并且像真站一样检查 Referer，让下载链路（解析 → 下载 → 批量下载 → 自动下载）能离线跑通。
    /// </summary>
    private sealed class FakeGameSite : IDisposable
    {
        public const string SwfBody = "FLASH-SWF-BODY-DIRECT";
        public const string NewPageSwfBody = "FLASH-SWF-BODY-NEWPAGE";
        public const string Level1Body = "LEVEL1-SWF";
        public const string Auto1Body = "AUTO1-SWF";
        public const string Auto2Body = "AUTO2-SWF";

        private HttpListener _listener = new();

        public int Port { get; private set; }
        public bool Started { get; private set; }
        public string Error { get; private set; } = "";

        public string Base => $"http://localhost:{Port}";
        /// <summary>
        /// 游戏页地址刻意用 localhost（自检不碰外网）：域名认不出来时靠「页面内容探测」兜底，
        /// 这也顺便验证了那条容错路径——真实世界里镜像站/代理站就是这种情况。
        /// </summary>
        public string DirectGameUrl => $"{Base}/flash/12345.htm";
        public string NewPageGameUrl => $"{Base}/flash/12346.htm";
        public string H5GameUrl => $"{Base}/flash/12347.htm";
        public string DirectSwfUrl => $"{Base}/4399swf/upload/2013/jtys3.swf";
        public string NewPageUrl => $"{Base}/4399swf/upload/2014/jifen.htm";
        public string H5PageUrl => $"{Base}/4399swf/upload/2015/gameIndex.html";

        public FakeGameSite()
        {
            // ⚠ 每次重试都要 new 一个 HttpListener：同一个实例 Start() 失败之后再碰它（连 Prefixes 都会）
            // 会抛 ObjectDisposedException —— .NET 在 Start 失败时就把底层 listener 释放了，
            // 表现成「自检随机挂一条 ObjectDisposedException / 句柄无效」（本机必现，已修）。
            for (var port = 10755; port < 10820; port++)
            {
                var listener = new HttpListener();
                listener.Prefixes.Add($"http://localhost:{port}/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    Started = true;
                    break;
                }
                catch (Exception ex)
                {
                    Error = $"端口 {port}：{ex.GetType().Name} {ex.Message}";
                    try { listener.Close(); } catch { }
                }
            }
            if (Started) _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(() => Respond(context));
            }
        }

        private void Respond(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            var referer = context.Request.Headers["Referer"] ?? "";

            // 防盗链：swf 必须带 Referer，否则回 403（复刻游戏站的行为）
            if (path.EndsWith(".swf", StringComparison.OrdinalIgnoreCase) && referer.Length == 0)
            {
                Write(context, 403, "text/plain; charset=utf-8", "forbidden: no referer");
                return;
            }

            switch (path)
            {
                case "/flash/12345.htm":
                    Write(context, 200, "text/html; charset=utf-8", $"""
                        <html><head><meta charset="utf-8"><title>黄金矿工双人版小游戏</title></head><body>
                        <script>
                          var game_title='黄金矿工双人版';
                          var _strGamePath="{Base}/4399swf/upload/2013/jtys3.swf";
                          var _w=800,_h=600;
                        </script></body></html>
                        """);
                    return;

                case "/flash/12346.htm":
                    Write(context, 200, "text/html; charset=utf-8", $"""
                        <html><head><meta charset="utf-8"></head><body>
                        <script>var game_title='多层游戏';var _strGamePath="{Base}/4399swf/upload/2014/jifen.htm";</script>
                        </body></html>
                        """);
                    return;

                case "/flash/12347.htm":
                    Write(context, 200, "text/html; charset=utf-8", $"""
                        <html><head><meta charset="utf-8"></head><body>
                        <script>var game_title='欢乐厨房';var _strGamePath="{Base}/4399swf/upload/2015/jifen.htm";var _w=960,_h=540;</script>
                        </body></html>
                        """);
                    return;

                case "/4399swf/upload/2013/jtys3.swf":
                    Write(context, 200, "application/x-shockwave-flash", SwfBody);
                    return;

                case "/4399swf/upload/2014/jifen.htm":
                    Write(context, 200, "text/html; charset=utf-8",
                        "<html><body><div id=\"flashgame\"><embed src=\"mainSwfGame.swf\"></div></body></html>");
                    return;

                case "/4399swf/upload/2014/mainSwfGame.swf":
                    Write(context, 200, "application/x-shockwave-flash", NewPageSwfBody);
                    return;

                case "/4399swf/upload/2015/jifen.htm":
                    // h5wan 外壳页（只有标记，没有 swf）
                    Write(context, 200, "text/html; charset=utf-8",
                        "<html><body><script>var h5wan=true;var h5api_core='x';</script></body></html>");
                    return;

                case "/4399swf/upload/2015/gameIndex.html":
                    Write(context, 200, "text/html; charset=utf-8", $"""
                        <html><head><meta charset="utf-8"></head><body>
                        <script src="https://h.api.4399.com/h5mini-2.0/h5api-interface.php"></script>
                        <script>document.domain='4399.com';</script>
                        <div>欢乐厨房</div></body></html>
                        """);
                    return;

                case "/4399swf/upload/2015/assets/communal.swf":
                    Write(context, 200, "application/x-shockwave-flash", "COMMUNAL-SWF");
                    return;

                case "/4399swf/upload/2015/assets/level1.swf":
                    Write(context, 200, "application/x-shockwave-flash", Level1Body);
                    return;

                case "/4399swf/upload/2015/assets/auto1.swf":
                    Write(context, 200, "application/x-shockwave-flash", Auto1Body);
                    return;

                case "/4399swf/upload/2015/assets/auto2.swf":
                    Write(context, 200, "application/x-shockwave-flash", Auto2Body);
                    return;

                default:
                    Write(context, 404, "text/html; charset=utf-8", "<html><body>404 Not Found</body></html>");
                    return;
            }
        }

        private static void Write(HttpListenerContext context, int status, string contentType, string body)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = contentType;
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                context.Response.Close();
            }
            catch { /* 客户端断开就忽略 */ }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    /// <summary>排错用：单独试一次 HttpListener，区分「整个进程都开不了」和「这批端口开不了」。</summary>
    private static string ProbeListener()
    {
        try
        {
            using var probe = new HttpListener();
            probe.Prefixes.Add("http://localhost:10999/");
            probe.Start();
            probe.Stop();
            return "单独试 10999 能开（那就是那几个端口的问题）";
        }
        catch (Exception ex)
        {
            return $"单独试 10999 也开不了：{ex.GetType().Name} {ex.Message}";
        }
    }
}
