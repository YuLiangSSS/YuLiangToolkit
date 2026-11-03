using System.IO;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Browser;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「网页浏览器」自检：地址栏解析（补协议 / localhost / IP / 搜索词）、搜索模板、
/// 标签栏规则（位置归一化 / 会话清洗 / 关标签补位）、工具元数据与配置默认值。
/// <b>全程不启动 WebView2、不联网</b>（网页内核要真窗口，属于人工截图冒烟的部分）。
/// </summary>
internal static class BrowserChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckMetadata(check);
        CheckUrlResolution(check);
        CheckHostDetection(check);
        CheckSearchTemplate(check);
        CheckTabBarRules(check);
        CheckConfig(check);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new BrowserTool();
        check("[浏览器] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "browser" && tool.Name == "网页浏览器"
            && tool.Category == "网络" && tool.Order == 12,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");
        check("[浏览器] 标签里有「浏览器」「WebView2」，描述说明多标签与刷新",
            tool.Tags.Contains("浏览器") && tool.Tags.Contains("WebView2") && tool.Tags.Contains("标签页")
            && tool.Description.Contains("WebView2") && tool.Description.Contains("刷新")
            && tool.Description.Contains("标签"),
            tool.Description);

        // 工具 Id 必须等于配置/缓存文件夹名（Data / Config / Cache 都按它建目录）
        check("[浏览器] 工具 Id 与 BrowserConfig.ToolId 一致", tool.Id == BrowserConfig.ToolId, BrowserConfig.ToolId);
    }

    // ---------------------------------------------------------------- 地址栏解析

    private static void CheckUrlResolution(Action<string, bool, string> check)
    {
        check("[浏览器] 带 https / http 协议的地址原样使用",
            BrowserUrl.Resolve("https://example.com/a?b=1") == "https://example.com/a?b=1"
            && BrowserUrl.Resolve("http://example.com") == "http://example.com",
            BrowserUrl.Resolve("https://example.com/a?b=1") ?? "<null>");

        check("[浏览器] 裸域名自动补 https://",
            BrowserUrl.Resolve("example.com") == "https://example.com"
            && BrowserUrl.Resolve("www.bing.com/search?q=1") == "https://www.bing.com/search?q=1",
            BrowserUrl.Resolve("www.bing.com/search?q=1") ?? "<null>");

        check("[浏览器] localhost / IP 自动补 http://（本地服务通常没证书）",
            BrowserUrl.Resolve("localhost:8080") == "http://localhost:8080"
            && BrowserUrl.Resolve("127.0.0.1:5000/x") == "http://127.0.0.1:5000/x"
            && BrowserUrl.Resolve("192.168.1.5") == "http://192.168.1.5",
            BrowserUrl.Resolve("127.0.0.1:5000/x") ?? "<null>");

        check("[浏览器] 协议相对写法 //example.com 按 https 处理",
            BrowserUrl.Resolve("//example.com/x") == "https://example.com/x",
            BrowserUrl.Resolve("//example.com/x") ?? "<null>");

        check("[浏览器] file:// / about: 等其它协议原样交给 WebView2",
            BrowserUrl.Resolve("file:///C:/temp/a.html") == "file:///C:/temp/a.html"
            && BrowserUrl.Resolve("about:blank") == "about:blank",
            BrowserUrl.Resolve("file:///C:/temp/a.html") ?? "<null>");

        check("[浏览器] 空输入 / 残缺协议返回 null（不导航）",
            BrowserUrl.Resolve("") is null && BrowserUrl.Resolve("   ") is null
            && BrowserUrl.Resolve("http://") is null && BrowserUrl.Resolve(null) is null,
            "空 / 空白 / http:// / null");

        check("[浏览器] 非网址内容按搜索词处理（中文 / 带空格 / 单词）",
            (BrowserUrl.Resolve("天气 上海") ?? "").StartsWith("https://www.bing.com/search?q=", StringComparison.Ordinal)
            && (BrowserUrl.Resolve("hello world") ?? "").Contains("q=hello%20world", StringComparison.Ordinal)
            && (BrowserUrl.Resolve("hello") ?? "").StartsWith("https://www.bing.com/search?q=hello", StringComparison.Ordinal),
            BrowserUrl.Resolve("天气 上海") ?? "<null>");
    }

    // ---------------------------------------------------------------- 主机名识别

    private static void CheckHostDetection(Action<string, bool, string> check)
    {
        check("[浏览器] 域名 / 带端口 / 带路径识别为网址",
            BrowserUrl.LooksLikeHost("example.com")
            && BrowserUrl.LooksLikeHost("example.com/path?q=1")
            && BrowserUrl.LooksLikeHost("localhost:8080")
            && BrowserUrl.LooksLikeHost("127.0.0.1"),
            "example.com / localhost:8080 / 127.0.0.1");

        check("[浏览器] 搜索词 / 带空格的 / 已带协议的不误判成主机名",
            !BrowserUrl.LooksLikeHost("hello")
            && !BrowserUrl.LooksLikeHost("hello world")
            && !BrowserUrl.LooksLikeHost("http://example.com")
            && !BrowserUrl.LooksLikeHost("天气"),
            "hello / hello world / http://example.com");

        check("[浏览器] localhost 与 IP 优先 http，普通域名优先 https",
            BrowserUrl.PrefersHttp("localhost:8080") && BrowserUrl.PrefersHttp("127.0.0.1")
            && BrowserUrl.PrefersHttp("192.168.0.2") && !BrowserUrl.PrefersHttp("example.com"),
            "localhost / 127.0.0.1 / example.com");
    }

    // ---------------------------------------------------------------- 搜索模板

    private static void CheckSearchTemplate(Action<string, bool, string> check)
    {
        check("[浏览器] 默认搜索模板是 Bing 且带 {0} 占位符",
            BrowserConfig.DefaultSearchUrl.Contains("{0}", StringComparison.Ordinal)
            && BrowserConfig.DefaultSearchUrl.Contains("bing.com", StringComparison.Ordinal),
            BrowserConfig.DefaultSearchUrl);

        check("[浏览器] 自定义搜索模板：关键词转义后替换进 {0}",
            BrowserUrl.BuildSearchUrl("a b", "https://s.example/?q={0}") == "https://s.example/?q=a%20b",
            BrowserUrl.BuildSearchUrl("a b", "https://s.example/?q={0}"));

        check("[浏览器] 模板为空 / 没写 {0} 时回退默认搜索地址（不会拼出坏 URL）",
            BrowserUrl.BuildSearchUrl("x", null) == "https://www.bing.com/search?q=x"
            && BrowserUrl.BuildSearchUrl("x", "https://s.example/search") == "https://www.bing.com/search?q=x",
            BrowserUrl.BuildSearchUrl("x", "https://s.example/search"));
    }

    // ---------------------------------------------------------------- 标签栏规则

    private static void CheckTabBarRules(Action<string, bool, string> check)
    {
        // 位置归一化：大小写 / 空白容忍，写坏回退上方
        check("[浏览器] 标签栏位置归一化：left / LEFT / 空白 → Left，其余（含写坏）→ Top",
            BrowserTabBar.NormalizePosition("left") == BrowserTabBar.LeftPosition
            && BrowserTabBar.NormalizePosition(" LEFT ") == BrowserTabBar.LeftPosition
            && BrowserTabBar.NormalizePosition("Top") == BrowserTabBar.TopPosition
            && BrowserTabBar.NormalizePosition("right") == BrowserTabBar.TopPosition
            && BrowserTabBar.NormalizePosition("") == BrowserTabBar.TopPosition
            && BrowserTabBar.NormalizePosition(null) == BrowserTabBar.TopPosition,
            BrowserTabBar.NormalizePosition("left") + " / " + BrowserTabBar.NormalizePosition("right"));

        // 会话清洗：空地址过滤、封顶、不去重（同址两个标签是用户真实状态）
        var raw = new[]
        {
            new BrowserSessionTab { Url = "  https://a.com/  ", Title = " A " },
            new BrowserSessionTab { Url = "", Title = "空地址要被过滤" },
            new BrowserSessionTab { Url = "   ", Title = "" },
            new BrowserSessionTab { Url = "https://a.com/", Title = "同址不去重" },
        };
        var cleaned = BrowserTabBar.SanitizeSession(raw);
        check("[浏览器] 会话清洗：去掉空地址、首尾空白，同址不去重",
            cleaned.Count == 2
            && cleaned[0].Url == "https://a.com/" && cleaned[0].Title == "A"
            && cleaned[1].Url == "https://a.com/",
            string.Join(" | ", cleaned.Select(t => t.Url)));

        var overflow = Enumerable.Range(0, BrowserTabBar.MaxSessionTabs + 5)
            .Select(i => new BrowserSessionTab { Url = $"https://x{i}.com/" }).ToList();
        check($"[浏览器] 会话封顶：最多记 {BrowserTabBar.MaxSessionTabs} 个标签",
            BrowserTabBar.SanitizeSession(overflow).Count == BrowserTabBar.MaxSessionTabs,
            BrowserTabBar.SanitizeSession(overflow).Count.ToString());
        check("[浏览器] 会话清洗：null / 空 / 含 null 元素的列表安全",
            BrowserTabBar.SanitizeSession(null).Count == 0
            && BrowserTabBar.SanitizeSession(new List<BrowserSessionTab?> { null }.Cast<BrowserSessionTab>()).Count == 0,
            "null / 空 / 含 null");

        // 激活索引夹取
        check("[浏览器] 激活索引夹回合法范围（空列表 → 0）",
            BrowserTabBar.ClampActiveIndex(5, 3) == 2 && BrowserTabBar.ClampActiveIndex(-1, 3) == 0
            && BrowserTabBar.ClampActiveIndex(1, 0) == 0 && BrowserTabBar.ClampActiveIndex(2, 3) == 2,
            "5/3→2 · -1/3→0 · 1/0→0");

        // 关标签后的补位：关激活标签原地补位（末尾退一格）；关别的标签激活位置不动；关到 0 → -1
        check("[浏览器] 关激活标签：原地补位，末尾退一格",
            BrowserTabBar.NextActiveAfterRemove(3, 0, 0) == 0    // 关第 0 个（激活）→ 还是 0
            && BrowserTabBar.NextActiveAfterRemove(3, 2, 2) == 1 // 关最后一个（激活）→ 退到 1
            && BrowserTabBar.NextActiveAfterRemove(1, 0, 0) == -1, // 只剩一个也关了
            "3/0/0→0 · 3/2/2→1 · 1/0/0→-1");
        check("[浏览器] 关非激活标签：激活位置不动（在前面照旧，在后面退一格）",
            BrowserTabBar.NextActiveAfterRemove(3, 2, 0) == 0    // 关后面的
            && BrowserTabBar.NextActiveAfterRemove(3, 0, 2) == 1 // 关前面的
            && BrowserTabBar.NextActiveAfterRemove(3, 1, 1) == 1, // 关激活的中位 → 原地补位
            "3/2/0→0 · 3/0/2→1 · 3/1/1→1");
        check("[浏览器] 关越界位置 / 参数异常时不崩（夹回合法激活位）",
            BrowserTabBar.NextActiveAfterRemove(3, 9, 1) == 1
            && BrowserTabBar.NextActiveAfterRemove(3, 1, -5) == 0,
            "3/9/1→1 · 3/1/-5→0");
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(Action<string, bool, string> check)
    {
        var store = ToolConfigStore.For(BrowserConfig.ToolId);
        var config = store.Load<BrowserConfig>();

        check("[浏览器] 首次加载即生成 config.json", File.Exists(config.FilePath), config.FilePath);
        check("[浏览器] 默认主页 / 搜索模板 / 记住上次页面 / 标签栏在上方 / 空会话",
            config.HomePage == BrowserConfig.DefaultHomePage
            && config.SearchUrl == BrowserConfig.DefaultSearchUrl
            && config.RememberLastPage && config.LastPage.Length == 0
            && config.TabBarPosition == BrowserTabBar.TopPosition
            && config.SessionTabs.Count == 0 && config.ActiveTabIndex == 0,
            $"{config.HomePage} · {config.SearchUrl} · {config.TabBarPosition}");

        config.HomePage = "https://example.org/";
        config.LastPage = "https://example.org/page";
        config.TabBarPosition = "Left";
        config.SessionTabs = new List<BrowserSessionTab>
        {
            new() { Url = "https://example.org/a", Title = "A" },
            new() { Url = "https://example.org/b", Title = "B" },
        };
        config.ActiveTabIndex = 1;
        config.SaveNow();

        var reloaded = JsonFileStore.Read<BrowserConfig>(config.FilePath);
        check("[浏览器] 改主页 / 上次页面 / 标签栏位置 / 会话列表后立即落盘并能读回",
            reloaded is { HomePage: "https://example.org/", LastPage: "https://example.org/page"
                , TabBarPosition: "Left", ActiveTabIndex: 1 }
            && reloaded.SessionTabs.Count == 2
            && reloaded.SessionTabs[1].Url == "https://example.org/b"
            && reloaded.SessionTabs[1].Title == "B",
            $"{reloaded?.HomePage} · {reloaded?.TabBarPosition} · {reloaded?.SessionTabs.Count} 个标签");

        // 还原成默认，避免影响下一轮 / 其它检查
        config.HomePage = BrowserConfig.DefaultHomePage;
        config.LastPage = "";
        config.TabBarPosition = BrowserTabBar.TopPosition;
        config.SessionTabs = new List<BrowserSessionTab>();
        config.ActiveTabIndex = 0;
        config.SaveNow();
    }
}
