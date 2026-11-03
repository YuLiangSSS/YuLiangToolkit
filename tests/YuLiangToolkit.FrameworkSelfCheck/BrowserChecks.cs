using System.IO;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Browser;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「网页浏览器」自检：地址栏解析（补协议 / localhost / IP / 搜索词）、主页输入校验、
/// 搜索模板、标签栏规则、页面缩放、历史记录 / 下载记录规则、工具元数据与配置默认值。
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
        CheckHomePageInput(check);
        CheckTabBarRules(check);
        CheckZoom(check);
        CheckHistory(check);
        CheckDownloads(check);
        CheckBookmarks(check);
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
        check("[浏览器] 标签 / 描述覆盖多标签、WebView2、历史、下载、缩放",
            tool.Tags.Contains("浏览器") && tool.Tags.Contains("WebView2") && tool.Tags.Contains("标签页")
            && tool.Tags.Contains("历史记录") && tool.Tags.Contains("下载") && tool.Tags.Contains("缩放")
            && tool.Description.Contains("WebView2") && tool.Description.Contains("刷新")
            && tool.Description.Contains("历史") && tool.Description.Contains("下载")
            && tool.Description.Contains("缩放"),
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
            && BrowserUrl.Resolve("www.google.com/search?q=1") == "https://www.google.com/search?q=1",
            BrowserUrl.Resolve("www.google.com/search?q=1") ?? "<null>");

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

        check("[浏览器] 非网址内容按搜索词处理（默认 Google）",
            (BrowserUrl.Resolve("天气 上海") ?? "").StartsWith("https://www.google.com/search?q=", StringComparison.Ordinal)
            && (BrowserUrl.Resolve("hello world") ?? "").Contains("q=hello%20world", StringComparison.Ordinal)
            && (BrowserUrl.Resolve("hello") ?? "").StartsWith("https://www.google.com/search?q=hello", StringComparison.Ordinal),
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
        check("[浏览器] 默认搜索模板是 Google 且带 {0} 占位符",
            BrowserConfig.DefaultSearchUrl.Contains("{0}", StringComparison.Ordinal)
            && BrowserConfig.DefaultSearchUrl.Contains("google.com", StringComparison.Ordinal),
            BrowserConfig.DefaultSearchUrl);

        check("[浏览器] 自定义搜索模板：关键词转义后替换进 {0}",
            BrowserUrl.BuildSearchUrl("a b", "https://s.example/?q={0}") == "https://s.example/?q=a%20b",
            BrowserUrl.BuildSearchUrl("a b", "https://s.example/?q={0}"));

        check("[浏览器] 模板为空 / 没写 {0} 时回退默认搜索地址（不会拼出坏 URL）",
            BrowserUrl.BuildSearchUrl("x", null) == "https://www.google.com/search?q=x"
            && BrowserUrl.BuildSearchUrl("x", "https://s.example/search") == "https://www.google.com/search?q=x",
            BrowserUrl.BuildSearchUrl("x", "https://s.example/search"));
    }

    // ---------------------------------------------------------------- 主页输入校验

    private static void CheckHomePageInput(Action<string, bool, string> check)
    {
        check("[浏览器] 主页输入：裸域名 / localhost 能规范化成网址",
            BrowserUrl.ResolveHomePage("example.com") == "https://example.com"
            && BrowserUrl.ResolveHomePage("  https://www.google.com/  ") == "https://www.google.com/"
            && BrowserUrl.ResolveHomePage("localhost:8080") == "http://localhost:8080",
            BrowserUrl.ResolveHomePage("example.com") ?? "<null>");

        check("[浏览器] 主页输入：搜索词 / 空 / 残缺协议不能当主页",
            BrowserUrl.ResolveHomePage("hello") is null
            && BrowserUrl.ResolveHomePage("天气 上海") is null
            && BrowserUrl.ResolveHomePage("http://") is null
            && BrowserUrl.ResolveHomePage("") is null
            && BrowserUrl.ResolveHomePage(null) is null,
            "hello / 天气 / http:// / null");

        check("[浏览器] 主页输入：javascript: 等危险协议拒绝",
            BrowserUrl.ResolveHomePage("javascript:alert(1)") is null,
            BrowserUrl.ResolveHomePage("javascript:alert(1)") ?? "<null>");
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
            BrowserTabBar.NextActiveAfterRemove(3, 0, 0) == 0
            && BrowserTabBar.NextActiveAfterRemove(3, 2, 2) == 1
            && BrowserTabBar.NextActiveAfterRemove(1, 0, 0) == -1,
            "3/0/0→0 · 3/2/2→1 · 1/0/0→-1");
        check("[浏览器] 关非激活标签：激活位置不动（在前面照旧，在后面退一格）",
            BrowserTabBar.NextActiveAfterRemove(3, 2, 0) == 0
            && BrowserTabBar.NextActiveAfterRemove(3, 0, 2) == 1
            && BrowserTabBar.NextActiveAfterRemove(3, 1, 1) == 1,
            "3/2/0→0 · 3/0/2→1 · 3/1/1→1");
        check("[浏览器] 关越界位置 / 参数异常时不崩（夹回合法激活位）",
            BrowserTabBar.NextActiveAfterRemove(3, 9, 1) == 1
            && BrowserTabBar.NextActiveAfterRemove(3, 1, -5) == 0,
            "3/9/1→1 · 3/1/-5→0");
    }

    // ---------------------------------------------------------------- 页面缩放

    private static void CheckZoom(Action<string, bool, string> check)
    {
        check("[浏览器] 缩放夹取：25% ~ 500%，非法值回 100%",
            BrowserZoom.Clamp(0.01) == BrowserZoom.Min
            && BrowserZoom.Clamp(9.0) == BrowserZoom.Max
            && BrowserZoom.Clamp(double.NaN) == 1.0
            && BrowserZoom.Clamp(double.PositiveInfinity) == 1.0
            && BrowserZoom.Clamp(1.234) == 1.23,
            $"0.01→{BrowserZoom.Clamp(0.01)} · 9→{BrowserZoom.Clamp(9)} · NaN→{BrowserZoom.Clamp(double.NaN)}");

        check("[浏览器] 缩放加减：每次 10%，到边界不再越界",
            BrowserZoom.In(1.0) == 1.1 && BrowserZoom.In(4.95) == BrowserZoom.Max
            && BrowserZoom.Out(1.0) == 0.9 && BrowserZoom.Out(0.25) == BrowserZoom.Min,
            $"1→{BrowserZoom.In(1.0)} · 4.95→{BrowserZoom.In(4.95)} · 0.25→{BrowserZoom.Out(0.25)}");

        check("[浏览器] 缩放百分比文本",
            BrowserZoom.Format(1.0) == "100%" && BrowserZoom.Format(1.25) == "125%"
            && BrowserZoom.Format(0.9) == "90%",
            $"{BrowserZoom.Format(1.0)} / {BrowserZoom.Format(1.25)} / {BrowserZoom.Format(0.9)}");
    }

    // ---------------------------------------------------------------- 历史记录

    private static void CheckHistory(Action<string, bool, string> check)
    {
        var entries = new List<BrowserHistoryEntry>();

        check("[浏览器] 历史：新页面插到最前面",
            BrowserHistory.Append(entries, "https://a.com/", "A", DateTime.Now)
            && BrowserHistory.Append(entries, "https://b.com/", "B", DateTime.Now)
            && entries.Count == 2
            && entries[0].Url == "https://b.com/" && entries[0].Title == "B",
            string.Join(" | ", entries.Select(e => e.Url)));

        var sameTime = new DateTime(2026, 1, 2, 3, 4, 5);
        var changed = BrowserHistory.Append(entries, "https://b.com/", "B 新标题", sameTime);
        check("[浏览器] 历史：连续同址不重复插，只更新标题 / 时间",
            !changed && entries.Count == 2 && entries[0].Title == "B 新标题" && entries[0].VisitedAt == sameTime,
            $"{entries.Count} 条 · {entries[0].Title}");

        check("[浏览器] 历史：空白页 / data URL / 空地址不记录",
            !BrowserHistory.Append(entries, "about:blank", "", DateTime.Now)
            && !BrowserHistory.Append(entries, "data:text/html,hello", "", DateTime.Now)
            && !BrowserHistory.Append(entries, "", "", DateTime.Now)
            && !BrowserHistory.Append(entries, null, "", DateTime.Now)
            && entries.Count == 2,
            "about / data / 空 / null");

        var capped = new List<BrowserHistoryEntry>();
        for (var i = 0; i < BrowserHistory.MaxEntries + 5; i++)
            BrowserHistory.Append(capped, $"https://x{i}.com/", "", DateTime.Now);
        check($"[浏览器] 历史：封顶 {BrowserHistory.MaxEntries} 条",
            capped.Count == BrowserHistory.MaxEntries
            && capped[0].Url == $"https://x{BrowserHistory.MaxEntries + 4}.com/",
            capped.Count.ToString());

        var normalized = BrowserHistory.Normalize(new[]
        {
            new BrowserHistoryEntry { Url = "  https://a.com/  ", Title = " A ", VisitedAt = sameTime },
            new BrowserHistoryEntry { Url = "", Title = "空" },
            new BrowserHistoryEntry { Url = "about:blank", Title = "空白页" },
            null,
        });
        check("[浏览器] 历史清洗：去空地址 / 空白页 / null，去首尾空白",
            normalized.Count == 1 && normalized[0].Url == "https://a.com/" && normalized[0].Title == "A",
            string.Join(" | ", normalized.Select(e => e.Url)));

        // 落盘往返 + 清空（自检数据根是临时目录，不碰真 Data）
        var store = new BrowserHistoryStore(ToolStorage.For(BrowserConfig.ToolId));
        var saved = store.Save(new[]
        {
            new BrowserHistoryEntry { Url = "https://history-a.com/", Title = "A", VisitedAt = sameTime },
            new BrowserHistoryEntry { Url = "https://history-b.com/", Title = "B", VisitedAt = sameTime },
        });
        var loaded = store.Load();
        check("[浏览器] 历史文件：保存后能读回（新的在前）",
            saved && loaded.Count == 2
            && loaded[0].Url == "https://history-a.com/" && loaded[0].Title == "A",
            $"{loaded.Count} 条");

        var cleared = store.Clear() && store.Load().Count == 0;
        check("[浏览器] 历史文件：清空后读回为空", cleared, cleared ? "已清空" : "清空失败");
    }

    // ---------------------------------------------------------------- 下载记录

    private static void CheckDownloads(Action<string, bool, string> check)
    {
        check("[浏览器] 下载进度文本：总量未知 / 已知 / 空",
            BrowserDownloads.FormatProgress(0, null) == "等待数据…"
            && BrowserDownloads.FormatProgress(100, null) == "已下载 100 B"
            && BrowserDownloads.FormatProgress(1536, 4096) == "1.5 KB / 4.0 KB",
            $"{BrowserDownloads.FormatProgress(0, null)} · {BrowserDownloads.FormatProgress(100, null)} · {BrowserDownloads.FormatProgress(1536, 4096)}");

        var records = new List<BrowserDownloadRecord>();
        BrowserDownloads.AddRecord(records, new BrowserDownloadRecord { FilePath = @"C:\a.bin", FileName = "a.bin" });
        BrowserDownloads.AddRecord(records, new BrowserDownloadRecord { FilePath = @"C:\b.bin", FileName = "b.bin" });
        check("[浏览器] 下载记录：新记录插到最前面",
            records.Count == 2 && records[0].FileName == "b.bin",
            string.Join(" | ", records.Select(r => r.FileName)));

        BrowserDownloads.AddRecord(records, new BrowserDownloadRecord { FilePath = @"C:\a.bin", FileName = "a-new.bin" });
        check("[浏览器] 下载记录：同一路径只留最新一条",
            records.Count == 2 && records[0].FileName == "a-new.bin",
            string.Join(" | ", records.Select(r => r.FileName)));

        var cappedRecords = new List<BrowserDownloadRecord>();
        for (var i = 0; i < BrowserDownloads.MaxRecords + 5; i++)
            BrowserDownloads.AddRecord(cappedRecords, new BrowserDownloadRecord { FilePath = $@"C:\x{i}.bin", FileName = $"x{i}.bin" });
        check($"[浏览器] 下载记录：封顶 {BrowserDownloads.MaxRecords} 条",
            cappedRecords.Count == BrowserDownloads.MaxRecords,
            cappedRecords.Count.ToString());

        var normalized = BrowserDownloads.Normalize(new[]
        {
            new BrowserDownloadRecord { FilePath = @"C:\a.bin", FileName = "" },
            new BrowserDownloadRecord { FilePath = "", FileName = "无路径要被丢弃" },
            null,
        });
        check("[浏览器] 下载记录清洗：丢掉无路径，文件名缺省时从路径补",
            normalized.Count == 1 && normalized[0].FileName == "a.bin",
            string.Join(" | ", normalized.Select(r => r.FileName)));

        var item = new BrowserDownloadItem("https://x/file.bin", @"C:\file.bin", DateTime.Now);
        item.UpdateProgress(512, 1024);
        check("[浏览器] 下载项：进度 / 百分比 / 状态文本正确",
            Math.Abs(item.Progress - 0.5) < 0.0001 && item.ProgressText == "512 B / 1.0 KB" && item.IsActive,
            $"{item.Progress:0.00} · {item.ProgressText}");

        item.MarkCompleted(@"C:\file.bin");
        check("[浏览器] 下载项：完成后不再是进行中，记录为已完成",
            !item.IsActive && item.IsCompleted && item.Progress == 1 && item.ToRecord().Completed,
            item.StateText);
    }

    // ---------------------------------------------------------------- 书签

    private static void CheckBookmarks(Action<string, bool, string> check)
    {
        var now = new DateTime(2026, 1, 2, 3, 4, 5);
        var list = new List<BrowserBookmark>();

        var created = BrowserBookmarks.Create("  https://a.com/  ", "  A  ", now);
        check("[浏览器] 书签：创建时去首尾空白",
            created is { Url: "https://a.com/", Title: "A", CreatedAt: var t } && t == now,
            $"{created?.Url} · {created?.Title}");

        check("[浏览器] 书签：添加成功 / 重复地址不重复加",
            BrowserBookmarks.Add(list, created)
            && list.Count == 1
            && !BrowserBookmarks.Add(list, BrowserBookmarks.Create("https://a.com/", "另一个标题", now))
            && list.Count == 1,
            $"{list.Count} 个");

        check("[浏览器] 书签：标题为空时显示地址、ContainsUrl 精确匹配",
            BrowserBookmarks.ContainsUrl(list, " https://a.com/ ")
            && !BrowserBookmarks.ContainsUrl(list, "https://a.com/other")
            && BrowserBookmarks.Create("https://b.com/", "", now) is { DisplayTitle: "https://b.com/" },
            "a.com / other");

        check("[浏览器] 书签：空白页 / data: / javascript: / 空地址不能收藏",
            BrowserBookmarks.Create("about:blank", "x", now) is null
            && BrowserBookmarks.Create("data:text/html,x", "x", now) is null
            && BrowserBookmarks.Create("javascript:alert(1)", "x", now) is null
            && BrowserBookmarks.Create("   ", "x", now) is null
            && BrowserBookmarks.Create(null, "x", now) is null,
            "about / data / javascript / 空 / null");

        BrowserBookmarks.Add(list, BrowserBookmarks.Create("https://b.com/", "B", now));
        check("[浏览器] 书签：新书签插到最前面 / 按地址删除",
            list.Count == 2 && list[0].Url == "https://b.com/"
            && BrowserBookmarks.RemoveByUrl(list, "https://a.com/")
            && list.Count == 1 && list[0].Url == "https://b.com/"
            && !BrowserBookmarks.RemoveByUrl(list, "https://a.com/"),
            string.Join(" | ", list.Select(b => b.Url)));

        var raw = new BrowserBookmark?[]
        {
            new() { Url = "  https://x.com/  ", Title = " X " },
            new() { Url = "", Title = "空" },
            new() { Url = "about:blank", Title = "空白" },
            new() { Url = "https://x.com/", Title = "重复地址" },
            null,
        };
        var normalized = BrowserBookmarks.Normalize(raw);
        check("[浏览器] 书签清洗：去空 / 无意义协议 / null，按地址去重，去首尾空白",
            normalized.Count == 1 && normalized[0].Url == "https://x.com/" && normalized[0].Title == "X",
            string.Join(" | ", normalized.Select(b => b.Url)));

        var many = Enumerable.Range(0, BrowserBookmarks.MaxEntries + 5)
            .Select(i => new BrowserBookmark { Url = $"https://b{i}.com/", Title = $"B{i}" })
            .ToList();
        check($"[浏览器] 书签：封顶 {BrowserBookmarks.MaxEntries} 个",
            BrowserBookmarks.Normalize(many).Count == BrowserBookmarks.MaxEntries,
            BrowserBookmarks.Normalize(many).Count.ToString());

        // 落盘往返（自检数据根是临时目录，不碰真 Data）
        var store = new BrowserBookmarkStore(ToolStorage.For(BrowserConfig.ToolId));
        var saved = store.Save(new[]
        {
            new BrowserBookmark { Url = "https://bookmark-a.com/", Title = "A", CreatedAt = now },
            new BrowserBookmark { Url = "https://bookmark-b.com/", Title = "B", CreatedAt = now },
        });
        var loaded = store.Load();
        check("[浏览器] 书签文件：保存后能读回（新的在前）",
            saved && loaded.Count == 2
            && loaded[0].Url == "https://bookmark-a.com/" && loaded[0].Title == "A",
            $"{loaded.Count} 个");
    }

    // ---------------------------------------------------------------- 配置 / 旧配置迁移

    private static void CheckConfig(Action<string, bool, string> check)
    {
        // 旧默认（Bing）→ 新默认（Google）：只改「恰好还是旧默认」的值
        var legacy = new BrowserConfig
        {
            HomePage = BrowserConfig.LegacyBingHomePage,
            SearchUrl = BrowserConfig.LegacyBingSearchUrl,
        };
        var migrated = BrowserConfig.MigrateLegacyDefaults(legacy);
        check("[浏览器] 旧配置迁移：默认 Bing 主页 / 搜索自动改成 Google",
            migrated && legacy.HomePage == BrowserConfig.DefaultHomePage
            && legacy.SearchUrl == BrowserConfig.DefaultSearchUrl,
            $"{legacy.HomePage} · {legacy.SearchUrl}");

        var custom = new BrowserConfig
        {
            HomePage = "https://my-home.example/",
            SearchUrl = "https://my-search.example/?q={0}",
            LastPage = "data:text/html,test",
        };
        var customMigrated = BrowserConfig.MigrateLegacyDefaults(custom);
        check("[浏览器] 旧配置迁移：测试页 LastPage（data:/about:）会被清掉",
            customMigrated && custom.LastPage.Length == 0,
            $"migrated={customMigrated} · LastPage='{custom.LastPage}'");
        check("[浏览器] 旧配置迁移：自定义主页 / 搜索不被改动",
            custom.HomePage == "https://my-home.example/"
            && custom.SearchUrl == "https://my-search.example/?q={0}",
            $"{custom.HomePage} · {custom.SearchUrl}");

        var store = ToolConfigStore.For(BrowserConfig.ToolId);
        var config = store.Load<BrowserConfig>();

        check("[浏览器] 首次加载即生成 config.json", File.Exists(config.FilePath), config.FilePath);
        check("[浏览器] 默认主页是 Google / 搜索模板是 Google / 记住会话 / 标签栏在上方 / 空会话",
            config.HomePage == "https://www.google.com/"
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
