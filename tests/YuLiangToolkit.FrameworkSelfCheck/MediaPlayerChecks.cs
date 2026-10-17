using System.IO;
using YuLiangToolkit.App.Views;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.MediaPlayer;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「视频 / 音乐播放器」的自检：扩展名归类 / 时间与快进快退 / 自然排序 / 扫文件夹 / 合并去重 /
/// 播放顺序（顺序 · 单曲 · 随机）这些**纯逻辑**逐条断言；再塞一个**假播放器**（<see cref="FakeMediaPlayback"/>）
/// 把 ViewModel 的播放、快进快退、右键上一首下一首、播完自动接下一首、记住上次列表整条链路无头跑一遍。
///
/// <para>⚠ 这里**不放真音频**：<c>MediaElement</c> 要真的解码，无头跑既慢又依赖本机编解码器。
/// 播放器被抽成 <see cref="IMediaPlayback"/> 就是为这个（见 AGENTS.md 2.14）。</para>
/// </summary>
internal static class MediaPlayerChecks
{
    /// <summary>自检的断言回调：说明文字可以省（<c>check("名字", ok)</c>），与其它 Checks 文件兼容。</summary>
    internal delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckFn assert = (name, ok, detail) => check("[播放器] " + name, ok, detail);

        CheckTypes(assert);
        CheckTime(assert);
        CheckNaturalSort(assert);
        CheckScanner(assert);
        CheckMerger(assert);
        CheckQueue(assert);
        CheckConfig(assert);
        CheckViewModel(assert);
        CheckFullscreenAndBackground(assert);
        CheckMiniPanelMarquee(assert);
    }

    // ---------------------------------------------------------------- 迷你条标题（来回滚）

    /// <summary>
    /// 左下角迷你条那个「标题来回滚」的算术：放得下就**不滚**，放不下才滚，且越长的滚越久。
    /// 动画本身是 WPF 故事板（无头测不了），但“要不要滚 / 一趟多久”是纯函数，这里逐条钉住。
    /// </summary>
    private static void CheckMiniPanelMarquee(CheckFn check)
    {
        const double view = 244;   // 264 的小窗减掉左右内边距

        check("迷你条标题：短标题放得下 → 不滚（完整显示，不会缺字）",
            MiniPlayerPanel.ScrollDuration(180, view) is null,
            MiniPlayerPanel.ScrollDuration(180, view)?.ToString() ?? "null");

        check("迷你条标题：正好塞满 → 也不滚",
            MiniPlayerPanel.ScrollDuration(view, view) is null);

        var oneWay = MiniPlayerPanel.ScrollDuration(344, view);   // 溢出 100px
        check("迷你条标题：长标题 → 要滚，一趟 ≈ 溢出宽度 ÷ 速度",
            oneWay is { } d && d.TotalSeconds > 2.5 && d.TotalSeconds < 3.3,
            oneWay?.ToString() ?? "null");

        var barely = MiniPlayerPanel.ScrollDuration(view + 6, view);   // 只溢出 6px
        check("迷你条标题：只溢出一点点 → 也要至少滚满 1 秒（不至于闪一下）",
            barely is { } b && Math.Abs(b.TotalSeconds - 1.0) < 0.01,
            barely?.ToString() ?? "null");

        var longer = MiniPlayerPanel.ScrollDuration(800, view);
        check("迷你条标题：越长滚得越久（速度恒定）",
            oneWay is { } o && longer is { } l && l > o,
            $"{oneWay?.TotalSeconds:0.00}s → {longer?.TotalSeconds:0.00}s");

        check("迷你条标题：还没量出宽度（0 / NaN）→ 先不动，等 SizeChanged 再来",
            MiniPlayerPanel.ScrollDuration(0, view) is null
            && MiniPlayerPanel.ScrollDuration(double.NaN, view) is null
            && MiniPlayerPanel.ScrollDuration(300, 0) is null);
    }

    // ---------------------------------------------------------------- 扩展名归类

    private static void CheckTypes(CheckFn check)
    {
        check("扩展名归类：音频 / 视频 / 都不是",
            MediaTypes.KindOf("a.mp3") == MediaKind.Audio
            && MediaTypes.KindOf(@"C:\m\b.MP4") == MediaKind.Video
            && MediaTypes.KindOf("c.txt") is null
            && MediaTypes.KindOf("noext") is null
            && MediaTypes.KindOf("") is null,
            $"{MediaTypes.KindOf("a.mp3")} / {MediaTypes.KindOf(@"C:\m\b.MP4")} / {MediaTypes.KindOf("c.txt")}");

        check("扩展名归类：大小写不敏感（.MP3 / .Flac 都认）",
            MediaTypes.IsAudio("A.MP3") && MediaTypes.IsAudio("b.Flac") && MediaTypes.IsVideo("c.MkV"));

        check("读取方式过滤：一并读取 = 两种都收，txt 永远不收",
            MediaTypes.Accept("a.mp3", MediaIncludeMode.AudioAndVideo)
            && MediaTypes.Accept("a.mp4", MediaIncludeMode.AudioAndVideo)
            && !MediaTypes.Accept("a.txt", MediaIncludeMode.AudioAndVideo));

        check("读取方式过滤：仅音频收音频不收视频",
            MediaTypes.Accept("a.mp3", MediaIncludeMode.AudioOnly)
            && !MediaTypes.Accept("a.mp4", MediaIncludeMode.AudioOnly));

        check("读取方式过滤：仅视频收视频不收音频",
            MediaTypes.Accept("a.mp4", MediaIncludeMode.VideoOnly)
            && !MediaTypes.Accept("a.mp3", MediaIncludeMode.VideoOnly));

        check("读取方式的中文说明与图标",
            MediaTypes.Describe(MediaIncludeMode.AudioAndVideo) == "音频 + 视频"
            && MediaTypes.Describe(MediaIncludeMode.AudioOnly) == "仅音频"
            && MediaTypes.Describe(MediaIncludeMode.VideoOnly) == "仅视频"
            && MediaTypes.KindText(MediaKind.Audio) == "音频"
            && MediaTypes.KindGlyph(MediaKind.Video) == "🎬");

        var audioHint = MediaTypes.DescribeExtensions(MediaIncludeMode.AudioOnly);
        var bothHint = MediaTypes.DescribeExtensions(MediaIncludeMode.AudioAndVideo);
        check("读取方式提示：仅音频只列音频扩展名，一并读取两种都列",
            audioHint.Contains("mp3") && !audioHint.Contains("mp4")
            && bothHint.Contains("mp3") && bothHint.Contains("mp4"),
            bothHint.Length > 200 ? bothHint[..120] + "…" : bothHint);
    }

    // ---------------------------------------------------------------- 时间与快进快退

    private static void CheckTime(CheckFn check)
    {
        check("时间文本：分:秒 / 时:分:秒 / 负数与 0",
            MediaTime.Format(TimeSpan.Zero) == "0:00"
            && MediaTime.Format(TimeSpan.FromSeconds(65)) == "1:05"
            && MediaTime.Format(TimeSpan.FromSeconds(3723)) == "1:02:03"
            && MediaTime.Format(TimeSpan.FromSeconds(-5)) == "0:00"
            && MediaTime.FormatSeconds(305) == "5:05",
            $"{MediaTime.Format(TimeSpan.FromSeconds(65))} / {MediaTime.Format(TimeSpan.FromSeconds(3723))}");

        check("步长规整：0.1 秒精度 + 夹在 0.1 ~ 60 秒",
            MediaTime.RoundStep(3.46) == 3.5
            && MediaTime.RoundStep(3.44) == 3.4
            && MediaTime.RoundStep(0.04) == 0.1
            && MediaTime.RoundStep(-3) == 0.1
            && MediaTime.RoundStep(999) == 60
            && MediaTime.RoundStep(double.NaN) == 0.1,
            $"3.46→{MediaTime.RoundStep(3.46)} 0.04→{MediaTime.RoundStep(0.04)} 999→{MediaTime.RoundStep(999)}");

        var duration = TimeSpan.FromSeconds(10);
        check("快进快退：夹在 0 ~ 时长之间（时长未知时只夹下界）",
            MediaTime.Seek(TimeSpan.FromSeconds(5), duration, 7) == duration
            && MediaTime.Seek(TimeSpan.FromSeconds(5), duration, -7) == TimeSpan.Zero
            && MediaTime.Seek(TimeSpan.FromSeconds(5), duration, 0.1) == TimeSpan.FromSeconds(5.1)
            && MediaTime.Clamp(TimeSpan.FromSeconds(5), TimeSpan.Zero) == TimeSpan.FromSeconds(5),
            $"{MediaTime.Seek(TimeSpan.FromSeconds(5), duration, 0.1).TotalSeconds}s");

        check("进度比例：位置 → 0~1 → 位置（越界会夹住）",
            Math.Abs(MediaTime.Ratio(TimeSpan.FromSeconds(5), duration) - 0.5) < 1e-9
            && MediaTime.Ratio(TimeSpan.FromSeconds(5), TimeSpan.Zero) == 0
            && MediaTime.FromRatio(0.25, duration) == TimeSpan.FromSeconds(2.5)
            && MediaTime.FromRatio(1.5, duration) == duration
            && MediaTime.FromRatio(-1, duration) == TimeSpan.Zero);

        check("进度比例：位置超过时长时比例仍然是 1（不会画出界）",
            MediaTime.Ratio(TimeSpan.FromSeconds(12), duration) == 1);
    }

    // ---------------------------------------------------------------- 自然排序

    private static void CheckNaturalSort(CheckFn check)
    {
        var cmp = NaturalStringComparer.Instance;

        check("自然排序：2.mp3 排在 10.mp3 前面（字典序会反过来）",
            cmp.Compare("2.mp3", "10.mp3") < 0 && string.CompareOrdinal("2.mp3", "10.mp3") > 0);

        check("自然排序：01 - 序章 < 02 - 第二章（前导零照样按数比）",
            cmp.Compare("01 - 序章.mp3", "02 - 第二章.mp3") < 0);

        check("自然排序：a9b < a10b（数字段按数值比）",
            cmp.Compare("a9b", "a10b") < 0);

        check("自然排序：01 与 1 视为相等（稳定的先后不影响）",
            cmp.Compare("01.mp3", "1.mp3") == 0 && cmp.Compare("x", "x") == 0);

        var unsorted = new List<string> { "10.mp3", "2.mp3", "1.mp3", "b.mp4", "a.flac" };
        unsorted.Sort(cmp);
        check("自然排序：一整份清单排出来是 1、2、10、a、b",
            unsorted.SequenceEqual(new[] { "1.mp3", "2.mp3", "10.mp3", "a.flac", "b.mp4" }),
            string.Join("、", unsorted));
    }

    // ---------------------------------------------------------------- 扫文件夹

    private static void CheckScanner(CheckFn check)
    {
        var root = SelfCheckScratch.MakeDirectory("ylt-media");
        var sub = Path.Combine(root, "专辑");
        var deep = Path.Combine(sub, "内页");
        Directory.CreateDirectory(deep);

        WriteFile(root, "01 - 序章.mp3");
        WriteFile(root, "02 - 第二章.mp3");
        WriteFile(root, "10 - 尾声.mp3");
        WriteFile(root, "现场.mp4");
        WriteFile(root, "说明.txt");
        WriteFile(sub, "03 - 附加曲.flac");
        WriteFile(sub, "花絮.mkv");
        WriteFile(deep, "04 - 隐藏曲.mp3");

        // 干扰项：一个叫 xxx.mp3 的**目录**（只扫文件，不该被当成媒体）
        Directory.CreateDirectory(Path.Combine(root, "假文件.mp3"));

        var flat = MediaScanner.EnumerateFiles(root, recursive: false, MediaIncludeMode.AudioAndVideo);
        check("扫文件夹：不看子文件夹时只有本层那 4 个媒体文件",
            flat.Count == 4, $"{flat.Count} 个：{string.Join("、", flat.Select(Path.GetFileName))}");

        check("扫文件夹：txt 与「名字像媒体的目录」都不会被收进来",
            flat.All(f => !f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            && flat.All(f => File.Exists(f)));

        check("扫文件夹：按自然顺序排（01、02、10，不是 01、02、10 的字典序）",
            flat.Select(Path.GetFileName).SequenceEqual(new[] { "01 - 序章.mp3", "02 - 第二章.mp3", "10 - 尾声.mp3", "现场.mp4" }),
            string.Join("、", flat.Select(Path.GetFileName)));

        var recursive = MediaScanner.EnumerateFiles(root, recursive: true, MediaIncludeMode.AudioAndVideo);
        check("扫文件夹：递归时把子目录、孙目录都收进来（4 + 2 + 1）",
            recursive.Count == 7, $"{recursive.Count} 个");

        var audioOnly = MediaScanner.EnumerateFiles(root, recursive: true, MediaIncludeMode.AudioOnly);
        check("扫文件夹：仅音频 = 5 个（mp3 ×4 + flac ×1，mkv/mp4 被滤掉）",
            audioOnly.Count == 5 && audioOnly.All(f => MediaTypes.IsAudio(f)),
            string.Join("、", audioOnly.Select(Path.GetFileName)));

        var videoOnly = MediaScanner.EnumerateFiles(root, recursive: true, MediaIncludeMode.VideoOnly);
        check("扫文件夹：仅视频 = 2 个（mp4 + mkv）",
            videoOnly.Count == 2 && videoOnly.All(f => MediaTypes.IsVideo(f)),
            string.Join("、", videoOnly.Select(Path.GetFileName)));

        check("扫文件夹：文件夹不存在时返回空、不抛异常",
            MediaScanner.EnumerateFiles(Path.Combine(root, "根本没有这个目录"), true, MediaIncludeMode.AudioAndVideo).Count == 0
            && MediaScanner.EnumerateFiles("", true, MediaIncludeMode.AudioAndVideo).Count == 0);

        var items = MediaScanner.BuildItems(root, recursive: false, MediaIncludeMode.AudioAndVideo);
        check("扫文件夹：包成列表条目时类别分得对（3 音频 + 1 视频）",
            items.Count == 4 && items.Count(i => i.Kind == MediaKind.Audio) == 3 && items.Count(i => i.Kind == MediaKind.Video) == 1);
        check("列表条目：标题去掉扩展名、类别文本与行号可读",
            items[0].Title == "01 - 序章" && items[0].KindText == "音频" && items[0].KindGlyph == "🎵"
            && items[0].ToString().Contains("01 - 序章"));

        var picked = MediaScanner.FromFiles(
            new[] { items[0].FullPath, items[0].FullPath, Path.Combine(root, "说明.txt") },
            MediaIncludeMode.AudioAndVideo);
        check("手动挑文件：重复路径只留一条、非媒体被丢掉",
            picked.Count == 1, $"{picked.Count} 条");

        check("手动挑文件：按读取方式过滤（仅音频时 mp4 进不来）",
            MediaScanner.FromFiles(new[] { items[0].FullPath, items[3].FullPath }, MediaIncludeMode.AudioOnly).Count == 1);
    }

    // ---------------------------------------------------------------- 合并（追加 / 覆盖）

    private static void CheckMerger(CheckFn check)
    {
        var a = new MediaItem(@"C:\m\a.mp3", MediaKind.Audio);
        var b = new MediaItem(@"C:\m\b.mp4", MediaKind.Video);
        var c = new MediaItem(@"C:\m\c.mp3", MediaKind.Audio);
        var existing = new List<MediaItem> { a, b };

        var append = PlaylistMerger.Merge(existing, new[] { b, c }, append: true);
        check("合并·追加：原有的留着、重复的不再加",
            append.Total == 3 && append.Added == 1 && append.Skipped == 1 && append.Displaced == 0,
            $"共 {append.Total}，新增 {append.Added}，跳过 {append.Skipped}");

        check("合并·追加：状态栏文案会带上「跳过重复」",
            append.Describe(append: true).Contains("跳过重复 1 个"), append.Describe(true));

        var replace = PlaylistMerger.Merge(existing, new[] { b, c }, append: false);
        check("合并·覆盖：列表只剩新扫到的这批",
            replace.Total == 2 && replace.Added == 2 && replace.Displaced == 2
            && replace.Items.Select(i => i.FileName).SequenceEqual(new[] { "b.mp4", "c.mp3" }),
            replace.Describe(false));

        var samePathDifferentCase = PlaylistMerger.Merge(
            new[] { new MediaItem(@"C:\Music\Song.mp3", MediaKind.Audio) },
            new[] { new MediaItem(@"c:\music\song.MP3", MediaKind.Audio) },
            append: true);
        check("合并：去重不看大小写（Windows 同一个文件不会变成两条）",
            samePathDifferentCase.Total == 1 && samePathDifferentCase.Skipped == 1);

        var cleared = PlaylistMerger.Merge(existing, Array.Empty<MediaItem>(), append: false);
        check("合并·覆盖 + 空结果 = 列表清空（但会说明原本有多少条被顶掉）",
            cleared.Total == 0 && cleared.Displaced == 2);
    }

    // ---------------------------------------------------------------- 播放顺序

    private static void CheckQueue(CheckFn check)
    {
        check("播放顺序：列表循环 = 播完最后一首接第一首",
            AutoNexts(new PlayQueue { Mode = PlayOrderMode.LoopAll }, start: 0, count: 3, times: 4)
                .SequenceEqual(new[] { 1, 2, 0, 1 }));

        check("播放顺序：播到底不会变成 -1（列表循环一直转）",
            AutoNexts(new PlayQueue { Mode = PlayOrderMode.LoopAll }, start: 4, count: 5, times: 2)
                .SequenceEqual(new[] { 0, 1 }));

        check("播放顺序：单曲循环——自动播完留在原地",
            AutoNexts(new PlayQueue { Mode = PlayOrderMode.RepeatOne }, start: 1, count: 3, times: 3)
                .SequenceEqual(new[] { 1, 1, 1 }));

        check("播放顺序：单曲循环——用户点「下一首」还是会换（不然按钮像坏了）",
            new PlayQueue { Mode = PlayOrderMode.RepeatOne }.Next(3, autoAdvance: false) == 0
            && NextAfter(new PlayQueue { Mode = PlayOrderMode.RepeatOne }, start: 1, count: 3, autoAdvance: false) == 2);

        check("播放顺序：上一首——到头了绕回最后一首",
            PreviousAfter(new PlayQueue { Mode = PlayOrderMode.LoopAll }, start: 0, count: 3) == 2
            && PreviousAfter(new PlayQueue { Mode = PlayOrderMode.LoopAll }, start: 2, count: 3) == 1);

        check("播放顺序：空列表返回 -1（上一首 / 下一首都不会越界）",
            new PlayQueue().Next(0, autoAdvance: false) == -1 && new PlayQueue().Previous(0) == -1);

        // 随机的严格性质用固定种子断言（VM 里那个用的是系统随机源）
        var shuffle = new PlayQueue(new Random(20240101)) { Mode = PlayOrderMode.Shuffle };
        var roundOne = new List<int> { shuffle.Next(5, autoAdvance: false) };
        for (var i = 0; i < 4; i++) roundOne.Add(shuffle.Next(5, autoAdvance: false));
        check("播放顺序：随机——第一轮 5 首不重复，且把 5 首全放到",
            roundOne.Distinct().Count() == 5, string.Join("、", roundOne));

        var nextAfterRound = shuffle.Next(5, autoAdvance: false);
        check("播放顺序：随机——一轮放完才重洗，且不会接着重复刚放完的那首",
            nextAfterRound != roundOne[^1], $"{roundOne[^1]} → {nextAfterRound}");

        var roundTwo = new List<int> { nextAfterRound };
        for (var i = 0; i < 4; i++) roundTwo.Add(shuffle.Next(5, autoAdvance: false));
        check("播放顺序：随机——第二轮同样是 5 首不重复",
            roundTwo.Distinct().Count() == 5, string.Join("、", roundTwo));

        var shufflePrev = new PlayQueue(new Random(7)) { Mode = PlayOrderMode.Shuffle };
        shufflePrev.Next(4, autoAdvance: false);
        shufflePrev.Next(4, autoAdvance: false);
        var back = shufflePrev.Previous(4);
        check("播放顺序：随机——上一首沿着同一轮的洗牌顺序往回走",
            back >= 0 && back < 4, $"回到第 {back} 首");

        var reset = new PlayQueue(new Random(3)) { Mode = PlayOrderMode.Shuffle };
        reset.Next(4, autoAdvance: false);
        reset.Reset();
        check("播放顺序：列表变了（Reset）之后还能继续算下一首，不抛异常",
            reset.Next(4, autoAdvance: false) is >= 0 and < 4);

        check("播放顺序：一个按钮轮换三种模式（列表循环 → 单曲循环 → 随机 → 列表循环）",
            PlayQueue.Cycle(PlayOrderMode.LoopAll) == PlayOrderMode.RepeatOne
            && PlayQueue.Cycle(PlayOrderMode.RepeatOne) == PlayOrderMode.Shuffle
            && PlayQueue.Cycle(PlayOrderMode.Shuffle) == PlayOrderMode.LoopAll);

        check("播放顺序：三种模式的图标与中文名",
            PlayQueue.ModeText(PlayOrderMode.LoopAll) == "列表循环"
            && PlayQueue.ModeText(PlayOrderMode.RepeatOne) == "单曲循环"
            && PlayQueue.ModeText(PlayOrderMode.Shuffle) == "随机播放"
            && PlayQueue.ModeGlyph(PlayOrderMode.Shuffle) == "🔀"
            && PlayQueue.ModeTooltip(PlayOrderMode.RepeatOne).Contains("单曲循环"));

        check("播放顺序：切模式不影响当前这首（SetCurrent 之后 Current 不变）",
            SetCurrentThenMode(2) == 2);
    }

    private static int SetCurrentThenMode(int current)
    {
        var queue = new PlayQueue { Mode = PlayOrderMode.LoopAll };
        queue.SetCurrent(current);
        queue.Mode = PlayOrderMode.Shuffle;
        return queue.Current;
    }

    private static int NextAfter(PlayQueue queue, int start, int count, bool autoAdvance)
    {
        queue.SetCurrent(start);
        return queue.Next(count, autoAdvance);
    }

    private static int PreviousAfter(PlayQueue queue, int start, int count)
    {
        queue.SetCurrent(start);
        return queue.Previous(count);
    }

    private static string ModeTextOf(MediaPlayerViewModel vm, PlayOrderMode mode)
    {
        vm.PlayMode = mode;
        return vm.ModeShortText;
    }

    private static int[] AutoNexts(PlayQueue queue, int start, int count, int times)
    {
        queue.SetCurrent(start);
        var result = new int[times];
        for (var i = 0; i < times; i++) result[i] = queue.Next(count, autoAdvance: true);
        return result;
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var cfg = new MediaPlayerConfig();
        check("配置默认值：两种都收 / 递归 / 追加 / 音量 80 / 列表循环 / 步长 5 秒 / 恢复列表与位置 / 后台播放开着",
            cfg.IncludeMode == MediaIncludeMode.AudioAndVideo
            && cfg.ScanRecursive
            && cfg.AppendWhenOpeningFolder
            && cfg.VolumeEffective == 80
            && cfg.PlayOrder == PlayOrderMode.LoopAll
            && cfg.SeekStepEffective == 5.0
            && cfg.RestorePlaylist
            && cfg.ResumeLastPlayed
            && cfg.BackgroundPlayback);

        cfg.SeekStepSeconds = 3.46;
        var step = cfg.SeekStepEffective;
        cfg.SeekStepSeconds = 0;
        var stepLow = cfg.SeekStepEffective;
        cfg.SeekStepSeconds = 500;
        var stepHigh = cfg.SeekStepEffective;

        check("配置：步长读出来是 0.1 秒精度、并且夹在 0.1 ~ 60 秒",
            step == 3.5 && stepLow == 0.1 && stepHigh == 60,
            $"3.46→{step}　0→{stepLow}　500→{stepHigh}");

        cfg.SeekStepSeconds = 7.5;
        check("配置：步长文本「7.5 秒」", cfg.SeekStepText == "7.5 秒", cfg.SeekStepText);

        cfg.Volume = -20;
        var lowVolume = cfg.VolumeEffective;
        cfg.Volume = 150;
        var highVolume = cfg.VolumeEffective;
        check("配置：音量夹在 0 ~ 100", lowVolume == 0 && highVolume == 100, $"{lowVolume} / {highVolume}");

        cfg.IncludeMode = MediaIncludeMode.VideoOnly;
        check("配置：读取方式提示跟着变", cfg.IncludeModeHint.Contains("mp4") && !cfg.IncludeModeHint.Contains("mp3"),
            cfg.IncludeModeHint[..Math.Min(80, cfg.IncludeModeHint.Length)]);

        // 枚举按字符串存（C# 原名），加减成员不会让老配置错位
        cfg.PlayOrder = PlayOrderMode.Shuffle;
        cfg.IncludeMode = MediaIncludeMode.AudioOnly;
        cfg.SeekStepSeconds = 2.5;
        cfg.Volume = 55;
        var path = SelfCheckScratch.MakeFile("media-config.json");
        JsonFileStore.Write(path, cfg);
        var json = File.ReadAllText(path);
        var reloaded = JsonFileStore.Read<MediaPlayerConfig>(path);

        check("配置：枚举按字符串存（\"Shuffle\" / \"AudioOnly\"，不是数字）",
            json.Contains("\"Shuffle\"") && json.Contains("\"AudioOnly\""),
            json.Contains("\"Shuffle\"") ? "有" : "没找到");

        check("配置落盘再读回来：播放顺序 / 读取方式 / 步长 / 音量都对",
            reloaded is not null
            && reloaded.PlayOrder == PlayOrderMode.Shuffle
            && reloaded.IncludeMode == MediaIncludeMode.AudioOnly
            && reloaded.SeekStepEffective == 2.5
            && reloaded.VolumeEffective == 55);

        check("配置：只读计算属性不写进 JSON（步长文本 / 扩展名提示都不该出现）",
            !json.Contains("seekStepText") && !json.Contains("includeModeHint"));
    }

    // ---------------------------------------------------------------- ViewModel（假播放器）

    private static void CheckViewModel(CheckFn check)
    {
        var root = SelfCheckScratch.MakeDirectory("ylt-media-vm");
        var sub = Path.Combine(root, "子专辑");
        Directory.CreateDirectory(sub);
        var s1 = WriteFile(root, "01 第一首.mp3");
        var s2 = WriteFile(root, "02 第二首.mp3");
        var v1 = WriteFile(root, "现场片段.mp4");
        WriteFile(root, "歌单说明.txt");
        var s3 = WriteFile(sub, "03 子目录里的歌.mp3");

        var vm = new MediaPlayerViewModel { Confirm = _ => true };
        var fake = new FakeMediaPlayback();
        vm.AttachPlayer(fake);

        check("VM：刚打开时列表是空的、播放器已接上",
            vm.Items.Count == 0 && vm.IsPlaylistEmpty && vm.PlaylistSummary.Contains("空的") && ReferenceEquals(vm.Player, fake),
            vm.PlaylistSummary);

        // ---- 打开文件 ----
        vm.AddFilesAsync(new[] { s1, s2, v1, s1 }).GetAwaiter().GetResult();
        check("VM：打开文件 → 去重后 3 条，音频 2 / 视频 1，自然排序",
            vm.Items.Count == 3
            && vm.Items.Select(i => i.FileName).SequenceEqual(new[] { "01 第一首.mp3", "02 第二首.mp3", "现场片段.mp4" })
            && vm.PlaylistSummary.Contains("音频 2") && vm.PlaylistSummary.Contains("视频 1"),
            vm.PlaylistSummary);

        var countBeforeReopen = vm.Items.Count;
        vm.AddFilesAsync(new[] { s1, s2, v1, s1 }).GetAwaiter().GetResult();
        check("VM：再打开一遍同样的文件 → 全部跳过、不重复添加",
            vm.Items.Count == 3 && vm.StatusText.Contains("跳过重复 3 个"),
            $"调用前 {countBeforeReopen} 条 / 调用后 {vm.Items.Count} 条　{vm.StatusText}");

        check("VM：行号是连续的 1、2、3", vm.Items.Select(i => i.Number).SequenceEqual(new[] { 1, 2, 3 }));

        // ---- 播放 ----
        vm.SelectedItem = vm.Items[1];
        vm.PlaySelectedCommand.Execute(null);
        check("VM：播放选中行 → 播放器装的是这个文件、状态与高亮都对",
            fake.LoadedPath == s2 && fake.IsPlaying && vm.IsPlaying && vm.CurrentItem == vm.Items[1]
            && vm.Items[1].IsPlaying && !vm.Items[0].IsPlaying
            && vm.TitleText == "02 第二首" && vm.StatusText.Contains("正在播放"),
            vm.StatusText);

        fake.RaiseOpened(TimeSpan.FromMinutes(3), hasVideo: false);
        check("VM：打开成功后拿到时长，音频显示封面、不显示视频",
            vm.DurationText == "3:00" && vm.PositionText == "0:00" && !vm.HasVideo && vm.CoverVisible,
            $"{vm.PositionText} / {vm.DurationText}");

        // ---- 快进快退（步长可调、夹取、右键换歌） ----
        vm.SeekStepSeconds = 2.5;
        check("VM：步长改成 2.5 → 配置与提示文本跟着变",
            vm.Cfg.SeekStepSeconds == 2.5 && vm.SeekStepText == "2.5 秒"
            && vm.SeekForwardTooltip.Contains("2.5 秒") && vm.SeekBackwardTooltip.Contains("右键：上一首"),
            vm.SeekForwardTooltip);

        fake.Position = TimeSpan.FromSeconds(30);
        vm.SeekForwardCommand.Execute(null);
        check("VM：左键快进 = 当前位置 + 步长，并报出来",
            fake.Position == TimeSpan.FromSeconds(32.5) && vm.PositionText == "0:32" && vm.StatusText.Contains("快进 2.5 秒"),
            $"{fake.Position.TotalSeconds}s　{vm.StatusText}");

        vm.SeekBackwardCommand.Execute(null);
        check("VM：左键快退 = 往回退一个步长",
            fake.Position == TimeSpan.FromSeconds(30) && vm.StatusText.Contains("快退 2.5 秒"),
            $"{fake.Position.TotalSeconds}s");

        fake.Position = TimeSpan.FromSeconds(1);
        vm.SeekBackwardCommand.Execute(null);
        check("VM：退到 0 就不动了（不会变成负数）", fake.Position == TimeSpan.Zero);

        fake.Position = TimeSpan.FromSeconds(179);
        vm.SeekForwardCommand.Execute(null);
        check("VM：快进到头就停在时长上（不会超过 3:00）", fake.Position == TimeSpan.FromMinutes(3));

        // ---- 右键：上一首 / 下一首（界面上就是这两个命令） ----
        vm.PlayItem(vm.Items[0]);
        vm.NextCommand.Execute(null);
        check("VM：下一首（右键快进）→ 换成第二条", vm.CurrentItem == vm.Items[1]);
        vm.PreviousCommand.Execute(null);
        check("VM：上一首（右键快退）→ 回到第一条", vm.CurrentItem == vm.Items[0]);

        // ---- 播完自动接下一首 ----
        vm.PlayMode = PlayOrderMode.LoopAll;
        vm.PlayItem(vm.Items[0]);
        fake.RaiseEnded();
        check("VM：列表循环——播完自动接下一首", vm.CurrentItem == vm.Items[1] && fake.IsPlaying);

        vm.PlayMode = PlayOrderMode.RepeatOne;
        vm.PlayItem(vm.Items[0]);
        var loadsBefore = fake.LoadCount;
        fake.Position = TimeSpan.FromMinutes(2.9);
        fake.RaiseEnded();
        check("VM：单曲循环——播完留在原地重放（不换歌、位置回 0）",
            vm.CurrentItem == vm.Items[0] && fake.Position == TimeSpan.Zero
            && fake.LoadCount == loadsBefore && fake.PlayCount > 0 && vm.StatusText.Contains("单曲循环"),
            vm.StatusText);

        vm.PlayMode = PlayOrderMode.Shuffle;
        vm.PlayItem(vm.Items[0]);
        var visited = new List<MediaItem?>();
        for (var i = 0; i < 6; i++)
        {
            fake.RaiseEnded();
            visited.Add(vm.CurrentItem);
        }

        var noBackToBack = true;
        for (var i = 1; i < visited.Count; i++)
        {
            if (ReferenceEquals(visited[i], visited[i - 1])) noBackToBack = false;
        }
        check("VM：随机播放——连续 6 次播完都不重复同一首，也没卡住",
            noBackToBack && visited.All(v => v is not null) && visited.Select(v => v!.FullPath).Distinct().Count() >= 2,
            string.Join(" → ", visited.Select(v => v?.Title)));

        // ---- 停止 / 暂停 ----
        vm.PlayMode = PlayOrderMode.LoopAll;
        vm.PlayItem(vm.Items[2]);
        fake.RaiseOpened(TimeSpan.FromSeconds(90), hasVideo: true);
        check("VM：视频文件打开后换成视频显示（不再显示封面）", vm.HasVideo && !vm.CoverVisible);

        vm.StopCommand.Execute(null);
        check("VM：停止 → 位置回 0、不在播放、状态说明清楚",
            fake.Position == TimeSpan.Zero && !vm.IsPlaying && vm.PositionText == "0:00" && vm.StatusText == "已停止",
            vm.StatusText);

        vm.PlayPauseCommand.Execute(null);
        check("VM：停止后按播放 → 重新开这一首（重新 Load）", fake.LoadCount > 0 && fake.IsPlaying && vm.IsPlaying);

        check("VM：播放/暂停按钮的图标跟着状态走（矢量三角形 ↔ 两根竖条）",
            !vm.ShowPlayIcon && vm.ShowPauseIcon);

        vm.PlayPauseCommand.Execute(null);
        check("VM：再按一下就是暂停（不会重新加载）",
            vm.StatusText.Contains("已暂停") && !vm.IsPlaying && fake.PauseCount > 0, vm.StatusText);

        check("VM：暂停后按钮又变回三角形", vm.ShowPlayIcon && !vm.ShowPauseIcon);

        // ---- 音量 / 播放顺序 / 设置面板 ----
        vm.VolumeValue = 55;
        check("VM：音量 55 → 配置 55、播放器 0.55",
            vm.Cfg.Volume == 55 && Math.Abs(fake.Volume - 0.55) < 1e-9 && vm.VolumeText == "55%",
            $"{fake.Volume}");

        vm.CyclePlayModeCommand.Execute(null);
        check("VM：点模式按钮轮换 → 单曲循环 → 随机 → 列表循环",
            vm.PlayMode == PlayOrderMode.RepeatOne && vm.PlayModeText == "单曲循环" && vm.ModeShortText == "单曲"
            && vm.PlayModeGlyph == "🔂" && vm.Cfg.PlayOrder == PlayOrderMode.RepeatOne,
            $"{vm.PlayModeText} / {vm.ModeShortText}");

        check("VM：模式按钮上的两个字跟着变（循环 / 单曲 / 随机）",
            vm.ModeShortText == "单曲"
            && ModeTextOf(vm, PlayOrderMode.Shuffle) == "随机"
            && ModeTextOf(vm, PlayOrderMode.LoopAll) == "循环"
            && ModeTextOf(vm, PlayOrderMode.RepeatOne) == "单曲");

        vm.ToggleSettingsCommand.Execute(null);
        check("VM：齿轮打开设置面板（同页覆盖，不弹子窗口）",
            vm.IsSettingsVisible && vm.SettingsGlyph == "\uE72B");
        vm.ToggleSettingsCommand.Execute(null);
        check("VM：再点一下关掉设置面板，图标变回齿轮",
            !vm.IsSettingsVisible && vm.SettingsGlyph == "\uE713");

        // ---- 读取方式 / 追加覆盖 / 递归 ----
        vm.SelectedIncludeMode = vm.IncludeModeOptions.First(o => o.Value == MediaIncludeMode.AudioOnly);
        check("VM：读取方式改成「仅音频」→ 配置立刻记下、提示跟着变",
            vm.Cfg.IncludeMode == MediaIncludeMode.AudioOnly && vm.IncludeModeHint.Contains("mp3"),
            vm.IncludeModeHint[..Math.Min(60, vm.IncludeModeHint.Length)]);

        var before = vm.Items.Count;
        vm.AddFilesAsync(new[] { v1 }).GetAwaiter().GetResult();
        check("VM：仅音频时再打开视频文件 → 不进列表", vm.Items.Count == before && vm.StatusText.Contains("不是可读取的媒体"),
            vm.StatusText);

        vm.SelectedIncludeMode = vm.IncludeModeOptions.First(o => o.Value == MediaIncludeMode.AudioAndVideo);
        vm.SelectedAppendMode = vm.AppendModeOptions.First(o => !o.Value);
        check("VM：选「覆盖」→ 配置里 AppendWhenOpeningFolder = false", !vm.Cfg.AppendWhenOpeningFolder);

        // 根目录 3 个 + 子目录 1 个（递归默认是开的）
        vm.ScanRecursive = true;
        vm.SelectedAppendMode = vm.AppendModeOptions.First(o => !o.Value);
        vm.AddFoldersAsync(new[] { root }).GetAwaiter().GetResult();
        check("VM：覆盖模式打开文件夹 → 列表只剩这个文件夹里的 4 个（含子目录那首）",
            vm.Items.Count == 4 && vm.StatusText.Contains("替换"), $"{vm.Items.Count} 条　{vm.StatusText}");

        vm.SelectedAppendMode = vm.AppendModeOptions.First(o => o.Value);
        vm.AddFoldersAsync(new[] { root }).GetAwaiter().GetResult();
        check("VM：追加模式再打开一次 → 条数不变（全是重复）",
            vm.Items.Count == 4 && vm.StatusText.Contains("跳过重复 4 个"), vm.StatusText);

        vm.ScanRecursive = false;
        vm.SelectedAppendMode = vm.AppendModeOptions.First(o => !o.Value);
        vm.AddFoldersAsync(new[] { root }).GetAwaiter().GetResult();
        check("VM：关掉「读取子文件夹」→ 子目录里的那首不进列表（3 条）",
            vm.Items.Count == 3 && !vm.Items.Any(i => i.FullPath == s3),
            $"{vm.Items.Count} 条");

        vm.ScanRecursive = true;
        vm.AddFoldersAsync(new[] { root }).GetAwaiter().GetResult();
        check("VM：打开递归 → 子目录那首也进来了（4 条）",
            vm.Items.Count == 4 && vm.Items.Any(i => i.FullPath == s3), $"{vm.Items.Count} 条");

        vm.SelectedAppendMode = vm.AppendModeOptions.First(o => o.Value);

        // ---- 移除 / 清空（二次确认是可替换出口） ----
        vm.SelectedItem = vm.Items[0];
        var countBeforeRemove = vm.Items.Count;
        vm.RemoveSelectedCommand.Execute(null);
        check("VM：从列表移除选中行 → 少一条、行号重排",
            vm.Items.Count == countBeforeRemove - 1
            && vm.Items.Select(i => i.Number).SequenceEqual(Enumerable.Range(1, vm.Items.Count)));

        vm.PlayItem(vm.Items[0]);
        vm.RemoveSelectedCommand.Execute(null);
        check("VM：把正在播的那首移除 → 停掉播放，不留下悬空的当前曲目",
            !vm.IsPlaying && vm.CurrentItem is null, $"当前：{vm.CurrentItem?.Title ?? "(无)"}");

        vm.Confirm = _ => false;
        var countBeforeClear = vm.Items.Count;
        vm.ClearPlaylistCommand.Execute(null);
        check("VM：清空要二次确认，用户点「否」就什么都不做",
            vm.Items.Count == countBeforeClear && countBeforeClear > 0);

        vm.Confirm = _ => true;

        // ---- 记住上次的列表 ----
        vm.PlayItem(vm.Items[0]);
        fake.RaiseOpened(TimeSpan.FromSeconds(200), hasVideo: false);
        fake.Position = TimeSpan.FromSeconds(42);
        vm.UpdatePosition(fake.Position, TimeSpan.FromSeconds(200));
        vm.SavePlaylistState();

        check("VM：列表快照落在 Data\\media-player\\playlist.json",
            File.Exists(vm.PlaylistStatePath) && vm.PlaylistStatePath.EndsWith("playlist.json", StringComparison.OrdinalIgnoreCase),
            vm.PlaylistStatePath);

        var reopened = new MediaPlayerViewModel { Confirm = _ => true };
        check("VM：重新打开工具 → 上次的列表自动回来（文件都还在）",
            reopened.Items.Count == vm.Items.Count && reopened.Items[0].FullPath == vm.Items[0].FullPath,
            $"{reopened.Items.Count} 条");

        check("VM：上次听到的位置也恢复了（只选中、不自动出声）",
            reopened.CurrentItem is not null
            && reopened.CurrentItem.FullPath == vm.Items[0].FullPath
            && reopened.PositionText == "0:42"
            && !reopened.IsPlaying,
            $"{reopened.PositionText}　{reopened.StatusText}");

        var fakeForReopened = new FakeMediaPlayback();
        reopened.AttachPlayer(fakeForReopened);
        reopened.PlayPauseCommand.Execute(null);
        fakeForReopened.RaiseOpened(TimeSpan.FromSeconds(200), hasVideo: false);
        check("VM：恢复后一按播放就从上次那秒接着放",
            fakeForReopened.Position == TimeSpan.FromSeconds(42) && fakeForReopened.IsPlaying,
            $"{fakeForReopened.Position.TotalSeconds}s");

        // ---- 文件不见了 ----
        var gone = Path.Combine(root, "已经删掉的歌.mp3");
        var ghost = new MediaItem(gone, MediaKind.Audio);
        reopened.Items.Add(ghost);
        reopened.PlayItem(ghost);
        check("VM：文件被删了 → 提示一句，不崩、也不进播放器",
            reopened.StatusText.Contains("文件不在了") && fakeForReopened.LoadedPath != gone,
            reopened.StatusText);
        reopened.Items.Remove(ghost);

        // ---- 恢复默认设置 ----
        reopened.VolumeValue = 12;
        reopened.SeekStepSeconds = 30;
        reopened.PlayMode = PlayOrderMode.Shuffle;
        reopened.ResetSettingsCommand.Execute(null);
        check("VM：恢复默认设置 → 音量 80 / 步长 5.0 / 列表循环 / 两种都收 / 递归 / 追加",
            reopened.VolumeValue == 80
            && reopened.SeekStepSeconds == 5.0
            && reopened.PlayMode == PlayOrderMode.LoopAll
            && reopened.Cfg.IncludeMode == MediaIncludeMode.AudioAndVideo
            && reopened.ScanRecursive
            && reopened.Cfg.AppendWhenOpeningFolder
            && reopened.StatusText.Contains("恢复默认"),
            reopened.StatusText);

        // ---- 关掉工具 ----
        reopened.BackgroundPlayback = false;   // 后台播放默认是开的；关掉就是“切走即停”的老行为
        reopened.NotifyClosed();
        check("VM：关掉后台播放后切走 → 摘掉播放器（不会在背后继续出声）", reopened.Player is null);
    }

    // ---------------------------------------------------------------- 全屏 / 后台播放

    /// <summary>
    /// 全屏状态机 + 后台播放（切走不停）。真窗口的操作（藏壳、铺满屏幕）在外壳里，
    /// 这里只断言 ViewModel 该负责的那一层：状态、图标、事件、以及“切走时要不要拆播放器”。
    /// </summary>
    private static void CheckFullscreenAndBackground(CheckFn check)
    {
        var root = SelfCheckScratch.MakeDirectory("ylt-media-bg");
        var first = WriteFile(root, "后台-01.mp3");
        var second = WriteFile(root, "后台-02.mp3");

        var vm = new MediaPlayerViewModel { Confirm = _ => true };
        vm.ClearPlaylistCommand.Execute(null);   // 前面的用例存过列表快照，这里从空开始才有确定性
        vm.BackgroundPlayback = true;            // 配置对象是全局缓存的，前面的用例可能把它关过

        var fake = new FakeMediaPlayback();
        vm.AttachPlayer(fake);
        vm.AddFilesAsync(new[] { first, second }).GetAwaiter().GetResult();

        // ---- 全屏 ----
        var events = new List<bool>();
        vm.FullscreenChanged += (_, on) => events.Add(on);

        check("全屏：默认不是全屏，图标是「进入全屏」、右边列表还在",
            !vm.IsFullscreen && vm.FullscreenGlyph == "\uE740" && vm.ShowPlaylist && vm.FullscreenTooltip.Contains("全屏"),
            vm.FullscreenTooltip);

        vm.ToggleFullscreenCommand.Execute(null);
        check("全屏：点一下 → 状态切过去、图标变「退出全屏」、播放列表收起、事件发出 true",
            vm.IsFullscreen && vm.FullscreenGlyph == "\uE73F" && !vm.ShowPlaylist
            && events.Count == 1 && events[0],
            $"glyph=0x{((int)vm.FullscreenGlyph[0]):X4}");

        vm.ExitFullscreen();
        check("全屏：退出 → 恢复原样，事件发出 false",
            !vm.IsFullscreen && vm.ShowPlaylist && events.SequenceEqual(new[] { true, false }));

        vm.ExitFullscreen();
        check("全屏：重复退出不会再发事件（不会把外壳来回折腾）", events.Count == 2);

        // ---- 全屏时的控制条自动隐藏 ----
        check("全屏：普通模式控制条一直露着，画面右下角有那个圆钮",
            vm.ControlsVisible && vm.ShowCornerFullscreenButton);

        vm.ToggleFullscreenCommand.Execute(null);
        check("全屏：进去时控制条先亮着（2.5 秒后由界面计时器缩回去），画面角上的圆钮让位给控制条",
            vm.ControlsVisible && !vm.ShowCornerFullscreenButton);

        vm.HideControls();
        check("全屏：鼠标闲置 → 控制条缩回去（沉浸式看视频）", !vm.ControlsVisible);

        vm.ShowControls();
        check("全屏：鼠标一动 → 控制条又出来", vm.ControlsVisible);

        vm.HideControls();
        vm.IsSettingsVisible = true;
        vm.HideControls();
        check("全屏：正开着设置面板时不缩控制条（不然鼠标一动也点不到）", vm.ControlsVisible);
        vm.IsSettingsVisible = false;

        vm.ExitFullscreen();
        check("全屏：退出全屏 → 控制条恢复常显，圆钮回来",
            vm.ControlsVisible && vm.ShowCornerFullscreenButton);
        vm.HideControls();
        check("全屏：普通模式下叫它藏也不会藏（只有全屏才自动隐藏）", vm.ControlsVisible);

        // ---- 后台播放 ----
        check("后台播放：开着时 → 切走要保活、并且该显示左下角小窗，没曲目时两样都不做",
            vm.BackgroundPlayback && !vm.KeepAliveOnLeave && !vm.ShowMiniPanel);

        vm.PlayItem(vm.Items[0]);
        fake.RaiseOpened(TimeSpan.FromSeconds(120), hasVideo: false);
        check("后台播放：有曲目 → 切走要保活，并且该显示左下角小窗",
            vm.KeepAliveOnLeave && vm.ShowMiniPanel);

        var stateEvents = 0;
        vm.BackgroundStateChanged += (_, _) => stateEvents++;
        vm.BackgroundPlayback = false;
        check("后台播放：关掉设置 → 通知外壳刷新，且不再保活 / 不再显示小窗",
            stateEvents > 0 && !vm.KeepAliveOnLeave && !vm.ShowMiniPanel);
        vm.BackgroundPlayback = true;

        vm.NotifyClosed();
        check("后台播放：开着时切走 → 播放器没被摘掉、还在放（声音不断）",
            ReferenceEquals(vm.Player, fake) && fake.IsPlaying, vm.StatusText);

        vm.NotifyOpened();
        check("后台播放：再切回来 → 还是同一个播放器，不需要重新加载", ReferenceEquals(vm.Player, fake));

        // 关掉后台播放就是老行为：切走即停
        var stopVm = new MediaPlayerViewModel { Confirm = _ => true };
        stopVm.ClearPlaylistCommand.Execute(null);
        var stopFake = new FakeMediaPlayback();
        stopVm.AttachPlayer(stopFake);
        stopVm.AddFilesAsync(new[] { first }).GetAwaiter().GetResult();
        stopVm.PlayItem(stopVm.Items[0]);
        stopFake.RaiseOpened(TimeSpan.FromSeconds(60), hasVideo: false);
        stopVm.BackgroundPlayback = false;
        stopVm.NotifyClosed();
        check("后台播放：关着时切走 → 摘掉播放器（切走即停的老行为）", stopVm.Player is null);

        // 全屏时切走，窗口要能还原
        var fullscreenVm = new MediaPlayerViewModel { Confirm = _ => true };
        fullscreenVm.ClearPlaylistCommand.Execute(null);
        fullscreenVm.BackgroundPlayback = false;
        fullscreenVm.ToggleFullscreenCommand.Execute(null);
        fullscreenVm.NotifyClosed();
        check("全屏：切走工具时自动退出全屏（不会留下一个“藏了壳”的窗口）", !fullscreenVm.IsFullscreen);
    }


    private static string WriteFile(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "self-check placeholder");
        return path;
    }

    /// <summary>
    /// 假播放器：不碰磁盘、不解码，只记下「被要求干了什么」，并把事件手动抛出来。
    /// ViewModel 就是靠它无头跑完整条链路的（真播放器见 <c>MediaElementPlayback</c>）。
    /// </summary>
    private sealed class FakeMediaPlayback : IMediaPlayback
    {
        public string? CurrentPath { get; private set; }
        public string? LoadedPath => CurrentPath;
        public bool IsLoaded => !string.IsNullOrEmpty(CurrentPath);
        public bool HasVideo { get; private set; }
        public bool IsPlaying { get; private set; }
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; private set; }
        public double Volume { get; set; } = 1.0;

        public int LoadCount { get; private set; }
        public int PlayCount { get; private set; }
        public int PauseCount { get; private set; }
        public int StopCount { get; private set; }

        public void Load(string path, bool autoPlay)
        {
            LoadCount++;
            CurrentPath = path;
            Position = TimeSpan.Zero;
            Duration = TimeSpan.Zero;
            IsPlaying = autoPlay;
        }

        public void Play()
        {
            PlayCount++;
            IsPlaying = true;
        }

        public void Pause()
        {
            PauseCount++;
            IsPlaying = false;
        }

        public void Stop()
        {
            StopCount++;
            IsPlaying = false;
            Position = TimeSpan.Zero;
        }

        public void Seek(TimeSpan position) => Position = MediaTime.Clamp(position, Duration);

        public void Close()
        {
            CurrentPath = null;
            IsPlaying = false;
        }

        /// <summary>模拟「打开成功」：真实实现里这是 <c>MediaElement.MediaOpened</c>。</summary>
        public void RaiseOpened(TimeSpan duration, bool hasVideo)
        {
            Duration = duration;
            HasVideo = hasVideo;
            Opened?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>模拟「放完了」。</summary>
        public void RaiseEnded()
        {
            IsPlaying = false;
            Ended?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>模拟「解码失败」。</summary>
        public void RaiseFailed(string reason) => Failed?.Invoke(this, reason);

        public event EventHandler? Opened;
        public event EventHandler? Ended;
        public event EventHandler<string>? Failed;
    }
}
