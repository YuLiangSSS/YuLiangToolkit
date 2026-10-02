using System.Net;
using System.Net.Sockets;
using System.Text;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.MapDownloader;
using YuLiangToolkit.Tools.MapDownloader.Core;
using YuLiangToolkit.Tools.MapDownloader.Engine;
using YuLiangToolkit.Tools.MapDownloader.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「osu! 下图器」的自检：查询串 / 文件名清洗 / 本地筛选这些**纯逻辑**逐条断言，
/// 再用一个**自检自己起的假镜像**（<see cref="HttpListener"/>）把下载器整条链路真跑一遍
/// （落地文件名、跳过已存在、404 计入失败、取消不留半个 .part），最后无头构造 ViewModel 走一遍。
///
/// <para>全程**不碰外网**：osu! 官方 API 那段（换 Token / 搜索）没有可用的假服务，
/// 只断言"URL 拼得对不对"和"没 Token 时的表现"，真联网那步留给手动。</para>
/// </summary>
internal static class MapDownloaderChecks
{
    /// <summary>自检的断言回调：说明文字可以省（<c>check("名字", ok)</c>），与其它 Checks 文件的写法兼容。</summary>
    internal delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckQuery(assert);
        CheckNaming(assert);
        CheckFiltering(assert);
        CheckOptions(assert);
        CheckConfig(assert);
        CheckDownloader(assert, report);
        CheckViewModel(assert, report);
    }

    // ---------------------------------------------------------------- 纯逻辑

    private static void CheckQuery(CheckFn check)
    {
        var empty = OsuQuery.BuildSearchQuery(new OsuSearchRequest());
        check("[下图器] 空条件：查询串为空、URL 后面不带 ?",
            empty.Length == 0 && OsuQuery.BuildSearchUrl(new OsuSearchRequest()) == OsuQuery.ApiBaseUrl + "/beatmapsets/search",
            OsuQuery.BuildSearchUrl(new OsuSearchRequest()));

        var full = OsuQuery.BuildSearchQuery(new OsuSearchRequest(
            Query: "a b", Mode: 3, Status: "ranked", Sort: "plays_desc", IncludeNsfw: true, CursorString: "abc+/="));
        check("[下图器] 查询串：关键词与游标都转义、参数顺序与原版一致",
            full == "q=a%20b&m=3&s=ranked&sort=plays_desc&nsfw=true&cursor_string=abc%2B%2F%3D", full);

        var chinese = OsuQuery.BuildSearchQuery(new OsuSearchRequest(Query: "初音"));
        check("[下图器] 查询串：中文关键词走 UTF-8 百分号编码",
            chinese == "q=%E5%88%9D%E9%9F%B3", chinese);

        check("[下图器] 查询串：只填了模式也不带多余参数",
            OsuQuery.BuildSearchQuery(new OsuSearchRequest(Mode: 1)) == "m=1");

        var form = OsuQuery.BuildTokenForm(12345, "s3cret");
        check("[下图器] 换 Token 表单：client_credentials + scope=public",
            form.Count == 4
            && form[0] is { Key: "client_id", Value: "12345" }
            && form[1] is { Key: "client_secret", Value: "s3cret" }
            && form[2] is { Key: "grant_type", Value: "client_credentials" }
            && form[3] is { Key: "scope", Value: "public" },
            string.Join("&", form.Select(p => p.Key + "=" + p.Value)));
    }

    private static void CheckNaming(CheckFn check)
    {
        check("[下图器] 文件名清洗：非法字符换成 _",
            OszNaming.Sanitize("a/b:c*d?e\"f<g>h|i") == "a_b_c_d_e_f_g_h_i",
            OszNaming.Sanitize("a/b:c*d?e\"f<g>h|i"));

        check("[下图器] 文件名清洗：空标题给 _、首尾空白去掉",
            OszNaming.Sanitize("") == "_" && OszNaming.Sanitize(null) == "_" && OszNaming.Sanitize("  x  ") == "x");

        check("[下图器] 文件名清洗：超长截断到 80",
            OszNaming.Sanitize(new string('x', 200)).Length == 80);

        check("[下图器] 落盘文件名：{Id} {标题}.osz",
            OszNaming.FileName(123456, "My Map") == "123456 My Map.osz"
            && OszNaming.FileName(new OszItem(7, "a/b")) == "7 a_b.osz",
            OszNaming.FileName(123456, "My Map"));

        check("[下图器] 下载地址：无视频 novideo / 含视频 full",
            OszNaming.BuildUrl(42, false) == "https://dl.sayobot.cn/beatmaps/download/novideo/42"
            && OszNaming.BuildUrl(42, true) == "https://dl.sayobot.cn/beatmaps/download/full/42",
            OszNaming.BuildUrl(42, false));

        check("[下图器] 下载地址：镜像可换、末尾多一个 / 也不怕",
            OszNaming.BuildUrl(7, false, "http://127.0.0.1:8080/mirror/") == "http://127.0.0.1:8080/mirror/novideo/7");
    }

    private static void CheckFiltering(CheckFn check)
    {
        var normal = Set(1, "2020-05-04T12:00:00+00:00", 4.0, 7.0);
        var old = Set(2, "2015-01-01T00:00:00+00:00", 5.0);
        var noDate = Set(3, null, 4.0);
        var tenK = Set(4, "2021-03-03T00:00:00+00:00", 10.4);

        check("[下图器] 日期筛选：两头都不填 = 全收",
            BeatmapFilter.MatchesDate(normal, null, null));

        check("[下图器] 日期筛选：下界 / 上界 / 区间",
            BeatmapFilter.MatchesDate(normal, new DateTime(2020, 1, 1), null)
            && !BeatmapFilter.MatchesDate(normal, new DateTime(2021, 1, 1), null)
            && !BeatmapFilter.MatchesDate(normal, null, new DateTime(2019, 1, 1))
            && BeatmapFilter.MatchesDate(normal, new DateTime(2020, 1, 1), new DateTime(2020, 12, 31)));

        check("[下图器] 日期筛选：没有上架日期的，一开筛选就落选",
            !BeatmapFilter.MatchesDate(noDate, new DateTime(2000, 1, 1), null));

        check("[下图器] Key 数筛选：不限 / 命中 / 不命中",
            BeatmapFilter.MatchesKeyCount(normal, "")
            && BeatmapFilter.MatchesKeyCount(normal, "4")
            && BeatmapFilter.MatchesKeyCount(normal, "7")
            && !BeatmapFilter.MatchesKeyCount(normal, "5"));

        check("[下图器] Key 数筛选：10K+ = 任意难度 ≥ 10 键",
            BeatmapFilter.MatchesKeyCount(tenK, "10+") && !BeatmapFilter.MatchesKeyCount(normal, "10+"));

        check("[下图器] Key 数筛选：没有难度列表时（选了具体键数）落选",
            !BeatmapFilter.MatchesKeyCount(Set(5, "2020-01-01T00:00:00+00:00"), "4"));

        check("[下图器] 日期 + Key 数一起判（Passes）",
            BeatmapFilter.Passes(normal, new DateTime(2020, 1, 1), null, "7")
            && !BeatmapFilter.Passes(old, new DateTime(2020, 1, 1), null, "7"));

        check("[下图器] 日期文本解析：空 / 写错 → 不限，正常日期能认",
            BeatmapFilter.ParseDate("") is null
            && BeatmapFilter.ParseDate("乱写的") is null
            && BeatmapFilter.ParseDate("2020-05-04") == new DateTime(2020, 5, 4));

        check("[下图器] 展示文案：日期 yyyy-MM-dd / 解析不出来就截前 10 个字符",
            BeatmapFilter.FormatDate(normal) == "2020-05-04"
            && BeatmapFilter.FormatDate(noDate) == ""
            && BeatmapFilter.FormatDate(Set(6, "2020-05-04")) == "2020-05-04",
            BeatmapFilter.FormatDate(normal));

        check("[下图器] 展示文案：CS 范围 4.0-7.0 / 单值只写一个",
            BeatmapFilter.FormatKeyRange(normal) == "4.0-7.0"
            && BeatmapFilter.FormatKeyRange(Set(7, "2020-01-01T00:00:00+00:00", 5.0)) == "5.0"
            && BeatmapFilter.FormatKeyRange(Set(8, "2020-01-01T00:00:00+00:00")) == "");

        check("[下图器] 展示文案：状态首字母大写、缺字段给 unknown",
            BeatmapFilter.StatusText(normal) == "Ranked"
            && BeatmapFilter.StatusText(Set(9, "2020-01-01T00:00:00+00:00", [], status: "loved")) == "Loved"
            && BeatmapFilter.StatusText(Set(10, "2020-01-01T00:00:00+00:00", [], status: null)) == "unknown");

        check("[下图器] 标题为空时用 unknown 当文件名（免得落个空名字）",
            BeatmapFilter.DisplayTitle(Set(11, "2020-01-01T00:00:00+00:00", [], title: "  ")) == "unknown");
    }

    private static void CheckOptions(CheckFn check)
    {
        check("[下图器] 模式值：空 = 不限，其余转成 API 要的整数",
            OsuOptions.ToModeValue("") is null && OsuOptions.ToModeValue("3") == 3 && OsuOptions.ToModeValue("x") is null);

        check("[下图器] Key 数只在 mania（mode=3）下有意义",
            OsuOptions.KeyCountApplies("3") && !OsuOptions.KeyCountApplies("0") && !OsuOptions.KeyCountApplies(""));

        check("[下图器] 下拉框选项：值和标签分离（存的是 API 认的值）",
            OsuOptions.Pick(OsuOptions.Modes, "1").Label == "Taiko"
            && OsuOptions.Pick(OsuOptions.Statuses, "loved").Label == "Loved"
            && OsuOptions.Pick(OsuOptions.Sorts, "plays_desc").Value == "plays_desc"
            && OsuOptions.Pick(OsuOptions.Modes, "没有这个").Label == "不限");
    }

    private static void CheckConfig(CheckFn check)
    {
        var cfg = ToolConfigStore.For(MapDownloaderConfig.ToolId).Load<MapDownloaderConfig>();
        ResetConfig(cfg);

        check("[下图器] 配置默认值：自动换 Token 开着、并发 5、凭据与下载目录都是空的",
            cfg.AutoGetToken && cfg.ParallelDownloads == 5 && cfg.ClientId == "" && cfg.ClientSecret == ""
            && cfg.AccessToken == "" && cfg.DownloadFolder == "" && !cfg.IncludeNsfw);

        cfg.DefaultDownloadFolder = Path.Combine(AppPaths.DataRoot, "map-downloader", "downloads");
        check("[下图器] 下载目录：没配就用默认的（Data\\map-downloader\\downloads）",
            cfg.EffectiveDownloadFolder == cfg.DefaultDownloadFolder,
            cfg.EffectiveDownloadFolder);

        cfg.DownloadFolder = Path.Combine(Path.GetTempPath(), "maps");
        check("[下图器] 下载目录：配了就用配的", cfg.EffectiveDownloadFolder == cfg.DownloadFolder);

        cfg.ClientId = "abc";
        check("[下图器] 凭据判定：Client ID 不是数字 → 不算齐", !cfg.HasCredentials);
        cfg.ClientId = " 12345 ";
        cfg.ClientSecret = "secret";
        check("[下图器] 凭据判定：填齐（Client ID 允许带空格）", cfg.HasCredentials);

        cfg.AccessToken = "token";
        cfg.TokenExpiresAtUtc = DateTime.UtcNow.AddHours(1);
        check("[下图器] Token 判定：没过期 = 有效", cfg.HasValidToken);
        cfg.TokenExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);
        check("[下图器] Token 判定：过期了 = 无效", !cfg.HasValidToken);

        ToolConfigStore.FlushAll();
        var again = ToolConfigStore.For(MapDownloaderConfig.ToolId).Load<MapDownloaderConfig>();
        check("[下图器] 配置落盘再读回来：密码 / Token / 下载目录都在",
            again.ClientSecret == "secret" && again.AccessToken == "token"
            && again.DownloadFolder == cfg.DownloadFolder,
            again.FilePath);

        var raw = File.Exists(again.FilePath) ? File.ReadAllText(again.FilePath) : "";
        check("[下图器] [JsonIgnore] 管用：默认目录 / 有效期计算属性都不写进 JSON",
            !raw.Contains("defaultDownloadFolder", StringComparison.OrdinalIgnoreCase)
            && !raw.Contains("effectiveDownloadFolder", StringComparison.OrdinalIgnoreCase)
            && !raw.Contains("hasValidToken", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- 下载（假镜像）

    private static void CheckDownloader(CheckFn check, Action<string, bool> report)
    {
        using var mirror = FakeMirror.TryStart();
        if (mirror is null)
        {
            report("[下图器] 假镜像起不来（端口都被占了？），这一组跳过", true);
            return;
        }

        var folder = SelfCheckScratch.MakeDirectory("ylt-maps");

        // ① 正常下一个（无视频）
        var downloader = new OszDownloader
        {
            MirrorBase = mirror.BaseUrl,
            DownloadFolder = folder,
            Concurrency = 3,
        };

        // 进度回调是从下载线程来的（可能并发），用线程安全的队列收集
        var progress = new System.Collections.Concurrent.ConcurrentQueue<OszProgress>();
        var items = new[] { new OszItem(101, "Alpha"), new OszItem(102, "Beta"), new OszItem(103, "Gamma") };
        var first = downloader.DownloadAsync(items, includeVideo: false, progress.Enqueue).GetAwaiter().GetResult();

        check("[下图器] 下载：三个都成功落地",
            first is { Downloaded: 3, Failed: 0, Skipped: 0, Cancelled: false },
            first.Describe());

        check("[下图器] 下载：文件名就是 {Id} {标题}.osz",
            File.Exists(Path.Combine(folder, "101 Alpha.osz"))
            && File.Exists(Path.Combine(folder, "102 Beta.osz"))
            && File.Exists(Path.Combine(folder, "103 Gamma.osz")),
            string.Join("、", Directory.GetFiles(folder).Select(Path.GetFileName)));

        check("[下图器] 下载：内容就是镜像给的那份（novideo = 1 KB）",
            new FileInfo(Path.Combine(folder, "101 Alpha.osz")).Length == mirror.NovideoBytes,
            new FileInfo(Path.Combine(folder, "101 Alpha.osz")).Length + " 字节");

        var progressList = progress.ToArray();
        check("[下图器] 下载：进度回调一路涨到总数",
            progressList.Length >= 3 && progressList[^1].Finished == 3
            && progressList[^1].Total == 3 && progressList[^1].Downloaded == 3,
            progressList.Length == 0 ? "(没有回调)" : $"{progressList.Length} 次回调，最后一次 {progressList[^1]}");

        check("[下图器] 下载：不留 .part 临时文件",
            Directory.GetFiles(folder, "*.part").Length == 0);

        // ② 再下一遍 → 全部跳过（不重新下）
        var second = downloader.DownloadAsync(items, includeVideo: false).GetAwaiter().GetResult();
        check("[下图器] 已存在就跳过（不重复下载）",
            second is { Downloaded: 0, Skipped: 3, Failed: 0 },
            second.Describe());

        // ③ 含视频 = 另一个地址、另一份内容
        var full = downloader.DownloadAsync([new OszItem(101, "Alpha")], includeVideo: true).GetAwaiter().GetResult();
        check("[下图器] 含视频（full）和 无视频（novideo）走的是两个地址",
            full.Downloaded == 0 && full.Skipped == 1,   // 同名文件已存在 → 跳过
            full.Describe());

        var videoFolder = SelfCheckScratch.MakeDirectory("ylt-maps-full");
        downloader.DownloadFolder = videoFolder;
        var video = downloader.DownloadAsync([new OszItem(101, "Alpha")], includeVideo: true).GetAwaiter().GetResult();
        var videoFile = new FileInfo(Path.Combine(videoFolder, "101 Alpha.osz"));
        check("[下图器] 含视频下下来的确实是镜像 full 那份（4 KB）",
            video.Downloaded == 1 && videoFile.Exists && videoFile.Length == mirror.FullBytes,
            video.Describe() + $"／{videoFile.Length} 字节");

        // ④ 镜像没有的 → 记失败，并且不留垃圾
        downloader.DownloadFolder = folder;
        var missing = downloader.DownloadAsync([new OszItem(999, "没有这张图")], includeVideo: false).GetAwaiter().GetResult();
        check("[下图器] 镜像 404 → 计入失败并带上原因",
            missing is { Failed: 1, Downloaded: 0 } && missing.Errors.Count == 1 && missing.Errors[0].Contains("404"),
            missing.Errors.FirstOrDefault() ?? "(没有错误信息)");

        check("[下图器] 404 之后不留 .part、也不会假装成功",
            Directory.GetFiles(folder, "*.part").Length == 0
            && !File.Exists(Path.Combine(folder, "999 没有这张图.osz")));

        // ⑤ 取消：镜像对 9xx 的请求故意慢慢回，下到一半就取消
        var slowFolder = SelfCheckScratch.MakeDirectory("ylt-maps-slow");
        downloader.DownloadFolder = slowFolder;
        using var cts = new CancellationTokenSource();
        var slowItems = Enumerable.Range(901, 4).Select(i => new OszItem(i, $"慢 {i}")).ToArray();
        var slowTask = downloader.DownloadAsync(slowItems, includeVideo: false, ct: cts.Token);
        Thread.Sleep(250);
        cts.Cancel();
        var cancelled = slowTask.GetAwaiter().GetResult();

        check("[下图器] 取消：能停下来、报告 Cancelled、不留 .part 半个文件",
            cancelled.Cancelled
            && Directory.GetFiles(slowFolder, "*.part").Length == 0
            && Directory.GetFiles(slowFolder, "*.osz").Length == 0,
            cancelled.Describe());

        // ⑥ 目录不存在会自动建
        var nested = Path.Combine(SelfCheckScratch.MakeDirectory("ylt-maps-nested"), "a", "b");
        downloader.DownloadFolder = nested;
        var made = downloader.DownloadAsync([new OszItem(102, "Beta")], includeVideo: false).GetAwaiter().GetResult();
        check("[下图器] 多层的目标目录会自动建出来",
            made.Downloaded == 1 && Directory.Exists(nested) && File.Exists(Path.Combine(nested, "102 Beta.osz")),
            nested);
    }

    // ---------------------------------------------------------------- ViewModel（无头）

    private static void CheckViewModel(CheckFn check, Action<string, bool> report)
    {
        var previous = Environment.GetEnvironmentVariable("YLT_NO_SERVER");
        Environment.SetEnvironmentVariable("YLT_NO_SERVER", "1");   // 别让它偷偷去换 Token
        try
        {
            var vm = new MapDownloaderViewModel();
            ResetConfig(vm.Cfg);

            check("[下图器] VM：没凭据时不算已认证、搜索命令不可用、提示指路",
                !vm.IsAuthenticated && !vm.SearchCommand.CanExecute(null)
                && vm.TokenStatusText.Contains("Client ID", StringComparison.Ordinal),
                vm.TokenStatusText);

            check("[下图器] VM：默认下载目录指向 Data\\map-downloader\\downloads",
                vm.FolderText.EndsWith(Path.Combine("map-downloader", "downloads"), StringComparison.OrdinalIgnoreCase),
                vm.FolderText);

            vm.Cfg.ClientId = "12345";
            vm.Cfg.ClientSecret = "s";
            vm.Cfg.AccessToken = "token";
            vm.Cfg.TokenExpiresAtUtc = DateTime.UtcNow.AddHours(2);

            check("[下图器] VM：Token 有效后状态与搜索命令都跟上",
                vm.IsAuthenticated && vm.SearchCommand.CanExecute(null)
                && vm.TokenStatusText.Contains("Token 有效", StringComparison.Ordinal),
                vm.TokenStatusText);

            // 本地筛选：日期只留 2020 年之后的那条
            vm.Cfg.DateFrom = "2020-01-01";
            var addedByDate = vm.AddSearchResults([Set(1, "2020-05-04T12:00:00+00:00", 4.0), Set(2, "2015-01-01T00:00:00+00:00", 4.0)]);
            check("[下图器] VM：日期是本地筛的（收一条、丢一条）",
                addedByDate == 1 && vm.Sets.Count == 1,
                $"收进 {addedByDate} 条，列表 {vm.Sets.Count} 条");

            // Key 数：只留 7K 那条
            vm.Cfg.DateFrom = "";
            vm.Cfg.KeyCount = "7";
            var addedByKey = vm.AddSearchResults([Set(3, "2021-01-01T00:00:00+00:00", 7.0), Set(4, "2021-01-01T00:00:00+00:00", 4.0)]);
            check("[下图器] VM：Key 数筛选也是本地的",
                addedByKey == 1 && vm.Sets.Count == 2 && vm.Sets.Any(s => s.KeyRangeText == "7.0"),
                $"收进 {addedByKey} 条，列表 {vm.Sets.Count} 条");

            check("[下图器] VM：行的展示字段（标题 / 状态 / 日期 / CS）都对",
                vm.Sets[0].Title == "T" && vm.Sets[0].StatusText == "Ranked"
                && vm.Sets[0].DateText == "2020-05-04" && vm.Sets[0].ToString() == $"{vm.Sets[0].Id} T",
                vm.Sets[0].ToString());

            check("[下图器] VM：结果统计文案",
                vm.ResultSummary.Contains("已显示 2 条", StringComparison.Ordinal), vm.ResultSummary);

            // 选择与按钮可用性
            check("[下图器] VM：没勾选时下载按钮不可用", !vm.DownloadSelectedNoVideoCommand.CanExecute(null));
            vm.SelectAllCommand.Execute(null);
            check("[下图器] VM：全选 → 计数与文案、下载按钮变可用",
                vm.SelectedCount == 2 && vm.SelectionText.Contains("2", StringComparison.Ordinal)
                && vm.DownloadSelectedNoVideoCommand.CanExecute(null) && vm.DownloadAllFullCommand.CanExecute(null),
                vm.SelectionText);

            vm.ClearSelectionCommand.Execute(null);
            check("[下图器] VM：清空选择 → 计数归零、按钮又灰掉",
                vm.SelectedCount == 0 && !vm.DownloadSelectedNoVideoCommand.CanExecute(null));

            // 真走一遍下载流程（指向假镜像，不碰外网）
            using var mirror = FakeMirror.TryStart();
            if (mirror is null)
            {
                report("[下图器] VM 下载流程：假镜像起不来，跳过", true);
                return;
            }

            var folder = SelfCheckScratch.MakeDirectory("ylt-maps-vm");
            vm.Cfg.DownloadFolder = folder;
            vm.Downloader.MirrorBase = mirror.BaseUrl;
            vm.Cfg.KeyCount = "";      // 清掉上一组留下的本地筛选，否则下面喂的假数据会被筛没
            vm.Cfg.DateFrom = "";
            vm.Cfg.DateTo = "";
            vm.Sets.Clear();
            var fed = vm.AddSearchResults([Set(101, "2020-05-04T12:00:00+00:00", 4.0), Set(102, "2020-05-04T12:00:00+00:00", 5.0)]);
            check("[下图器] VM：为下载流程喂进 2 条（本地筛选已清空）",
                fed == 2 && vm.Sets.Count == 2, $"收进 {fed} 条，列表 {vm.Sets.Count} 条");

            vm.SelectAllCommand.Execute(null);
            vm.DownloadAsync(selectedOnly: true, includeVideo: false).GetAwaiter().GetResult();

            check("[下图器] VM：下载流程跑完 → 文件落地 + 统计文案 + 状态栏都更新",
                File.Exists(Path.Combine(folder, "101 T.osz")) && File.Exists(Path.Combine(folder, "102 T.osz"))
                && vm.DownloadSummaryText.Contains("成功 2", StringComparison.Ordinal)
                && vm.StatusText.Contains("成功 2", StringComparison.Ordinal),
                vm.DownloadSummaryText);

            check("[下图器] VM：下载完不再是忙碌态、进度条收起",
                !vm.IsBusy && !vm.ProgressVisible);

            vm.NotifyClosed();
        }
        finally
        {
            Environment.SetEnvironmentVariable("YLT_NO_SERVER", previous);
        }
    }

    // ---------------------------------------------------------------- 小工具

    /// <summary>造一个谱面集（只填断言用得到的字段）。</summary>
    private static BeatmapSet Set(int id, string? rankedDate, params double[] cs)
        => Set(id, rankedDate, cs, "T", "A", "C", "ranked");

    private static BeatmapSet Set(int id, string? rankedDate, double cs, string title = "T",
        string? artist = "A", string? creator = "C", string? status = "ranked")
        => Set(id, rankedDate, [cs], title, artist, creator, status);

    private static BeatmapSet Set(int id, string? rankedDate, double[] cs, string title = "T",
        string? artist = "A", string? creator = "C", string? status = "ranked")
        => new()
        {
            Id = id,
            Title = title,
            Artist = artist,
            Creator = creator,
            Status = status,
            RankedDate = rankedDate,
            PlayCount = 100,
            FavouriteCount = 3,
            Beatmaps = cs.Length == 0
                ? null
                : cs.Select((c, i) => new BeatmapInfo { Id = id * 10 + i, CS = c, Version = $"v{i}", ModeInt = 0 }).ToList(),
        };

    /// <summary>把配置恢复成"刚装好"的样子（自检里几组断言共用同一个缓存实例）。</summary>
    private static void ResetConfig(MapDownloaderConfig cfg)
    {
        cfg.ClientId = "";
        cfg.ClientSecret = "";
        cfg.AutoGetToken = true;
        cfg.AccessToken = "";
        cfg.TokenExpiresAtUtc = default;
        cfg.LastQuery = "";
        cfg.Mode = "";
        cfg.Status = "";
        cfg.Sort = "";
        cfg.KeyCount = "";
        cfg.IncludeNsfw = false;
        cfg.DateFrom = "";
        cfg.DateTo = "";
        cfg.DownloadFolder = "";
        cfg.ParallelDownloads = 5;
    }

    /// <summary>
    /// 自检自己起的「假镜像」：把 dl.sayobot.cn 的那套路径照搬过来 ——
    /// <c>/beatmaps/download/{novideo|full}/{id}</c>，只服务几个固定 Id，其它一律 404；
    /// 9xx 的请求故意慢 3 秒，用来测取消（三秒内没等到响应就取消，正好验证"不留半个文件"）。
    ///
    /// <para>⚠ 这里**故意不用 HttpListener**：它要往 http.sys 注册前缀，有些环境（受限沙箱 / 权限不足）
    /// <c>HttpListener.IsSupported</c> 直接是 false，那一整组断言就只能"跳过"。
    /// 用裸 <see cref="TcpListener"/> 手写几十行 HTTP/1.1 响应，到哪儿都能跑（下载器只认状态码和 body）。</para>
    /// </summary>
    private sealed class FakeMirror : IDisposable
    {
        private const int SlowFrom = 900;
        private static readonly TimeSpan SlowDelay = TimeSpan.FromSeconds(3);
        private static readonly HashSet<int> Served = [101, 102, 103, 201];

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public int NovideoBytes => 1024;
        public int FullBytes => 4096;
        public string BaseUrl { get; }

        private FakeMirror(TcpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _ = Task.Run(LoopAsync);
        }

        /// <summary>随便挑个没被占的端口起一个（占用了就换一个）。</summary>
        public static FakeMirror? TryStart()
        {
            for (var attempt = 0; attempt < 30; attempt++)
            {
                var port = Random.Shared.Next(20000, 60000);
                var listener = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    listener.Start();
                    return new FakeMirror(listener, $"http://127.0.0.1:{port}/beatmaps/download");
                }
                catch
                {
                    try { listener.Stop(); } catch { /* 忽略 */ }
                }
            }
            return null;
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var path = await ReadRequestPathAsync(stream).ConfigureAwait(false);
                    if (path is null) return;

                    var parts = path.Trim('/').Split('/');
                    var type = parts.Length >= 4 ? parts[2] : "";       // /beatmaps/download/{type}/{id}
                    var idText = parts.Length >= 4 ? parts[3] : "";

                    if (!int.TryParse(idText, out var id) || (type != "novideo" && type != "full"))
                    {
                        await WriteAsync(stream, 404, []).ConfigureAwait(false);
                        return;
                    }

                    if (id >= SlowFrom) await Task.Delay(SlowDelay, _stop.Token).ConfigureAwait(false);

                    if (!Served.Contains(id))
                    {
                        await WriteAsync(stream, 404, []).ConfigureAwait(false);
                        return;
                    }

                    var size = type == "full" ? FullBytes : NovideoBytes;
                    var payload = Encoding.ASCII.GetBytes(new string(type == "full" ? 'F' : 'N', size));
                    await WriteAsync(stream, 200, payload).ConfigureAwait(false);
                }
                catch
                {
                    // 被取消 / 客户端断开都很正常，忽略
                }
            }
        }

        /// <summary>读到请求头结束（空行）为止，返回请求行里的路径。</summary>
        private static async Task<string?> ReadRequestPathAsync(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var text = new StringBuilder();

            while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (text.Length > 16 * 1024) break;   // 防止有人乱发东西把内存吃掉
            }

            var requestLine = text.ToString().Split("\r\n")[0].Split(' ');
            return requestLine.Length >= 2 ? requestLine[1] : null;
        }

        private static async Task WriteAsync(NetworkStream stream, int status, byte[] body)
        {
            var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n"
                       + "Content-Type: application/octet-stream\r\n"
                       + $"Content-Length: {body.Length}\r\n"
                       + "Connection: close\r\n\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
            if (body.Length > 0) await stream.WriteAsync(body).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { /* 忽略 */ }
            _stop.Dispose();
        }
    }
}