using System.IO;
using System.IO.Compression;
using System.Text;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Vsrg;
using YuLiangToolkit.Tools.Vsrg.Core;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「游戏 → VSRG」自检：工具与类别注册 / .osu 解析（只认 mode 3、1~10 键、坏行跳过）/
/// .osz 导入（真 zip / 路径穿越 / 非 mania 拒绝 / 重名不覆盖）/ Mapset 扫描 /
/// 判定档与准确率 / 游玩引擎（含面条头尾判定）/ 键位解析 / 配置默认值。
///
/// <para><b>全部是纯逻辑</b>：不创建窗口、不渲染、不出声；谱面与 .osz 都在临时目录里现造，
/// 不碰仓库里的 Data/Config。</para>
/// </summary>
internal static class VsrgChecks
{
    /// <summary>自检框架传进来的是 <c>Action&lt;string,bool,string&gt;</c>，这里包一层带默认值的委托。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;
        CheckFn fn = (name, ok, detail) => check(name, ok, detail);

        CheckMetadata(fn);
        CheckChartParsing(fn);
        CheckOszImport(fn);
        CheckTiers(fn);
        CheckGameplay(fn);
        CheckTailJudgement(fn);
        CheckKeyBindings(fn);
        CheckConfig(fn);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(CheckFn check)
    {
        var tool = new VsrgTool();

        check("[VSRG] 工具 Id 是 vsrg", tool.Id == "vsrg", tool.Id);
        check("[VSRG] 工具名是「VSRG」", tool.Name == "VSRG", tool.Name);
        check("[VSRG] 图标是 🎹", tool.EmojiIcon == "🎹", tool.EmojiIcon);
        check("[VSRG] 分类与类别路径都是「游戏」",
            tool.Category == "游戏" && tool.CategoryPath.SequenceEqual(new[] { "游戏" }),
            $"{tool.Category} / {string.Join("/", tool.CategoryPath)}");
        check("[VSRG] Order = 74（排在俄罗斯方块 70 / 贪吃蛇 72 之后）", tool.Order == 74, tool.Order.ToString());
        check("[VSRG] 标签里能搜到 osu!mania / osz / 音游",
            tool.Tags.Contains("osu!mania") && tool.Tags.Contains("osz") && tool.Tags.Contains("音游"),
            string.Join(",", tool.Tags));
        check("[VSRG] 描述提到谱面集 / 键位 / 判定档",
            tool.Description.Contains("osz") && tool.Description.Contains("键位") && tool.Description.Contains("判定档"),
            tool.Description);

        // 「游戏」类别元数据 + 目录树归位
        check("[VSRG] ShellCategories 里定义了「游戏」类别名", ShellCategories.Games == "游戏", ShellCategories.Games);
        var category = ShellCategories.All.FirstOrDefault(c => c.Path.SequenceEqual(new[] { "游戏" }));
        check("[VSRG] ShellCategories.All 里有「游戏」类别", category is not null,
            string.Join(" / ", ShellCategories.All.Select(c => string.Join("›", c.Path))));

        var tools = new List<ITool> { tool, new YuLiangToolkit.Tools.Games.Tetris.TetrisTool() };
        var root = ToolCatalog.Build(tools, ShellCategories.All);
        var games = root.Children.FirstOrDefault(c => c.Name == "游戏");
        check("[VSRG] 在「游戏」类别页里（根层不再平铺）",
            games is not null && games.Tools.Any(t => t.Id == "vsrg") && !root.Tools.Any(t => t.Id == "vsrg"),
            games is null ? "没有「游戏」节点" : string.Join(",", games.Tools.Select(t => t.Id)));
    }

    // ---------------------------------------------------------------- .osu 解析

    private static void CheckChartParsing(CheckFn check)
    {
        var dir = SelfCheckScratch.MakeDirectory("vsrg-chart");

        try
        {
            // ---- 正常 4K mania 谱面 ----
            var audio = Path.Combine(dir, "audio.ogg");
            File.WriteAllBytes(audio, Array.Empty<byte>());
            var background = Path.Combine(dir, "bg.jpg");
            File.WriteAllBytes(background, Array.Empty<byte>());

            var mapPath = Path.Combine(dir, "4K.osu");
            File.WriteAllText(mapPath, BuildOsu(keys: 4, mode: 3, audioName: "audio.ogg", bgName: "bg.jpg",
                notes: new[]
                {
                    "64,192,1000,1,0,0:0:0:0:",
                    "192,192,1500,128,0,2500:0:0:0:0:",
                    "320,192,3200,1,0,0:0:0:0:",
                    "坏行,192,abc,1",
                }), new UTF8Encoding(false));

            var warnings = new OsuChartReader.ParseWarnings();
            var ok = OsuChartReader.TryLoad(mapPath, out var chart, out var error, warnings);
            check("[VSRG] 4K mania 谱面能读出来", ok && chart is not null, error ?? "");

            if (chart is not null)
            {
                check("[VSRG] 键数取自 CircleSize", chart.KeyCount == 4, chart.KeyCount.ToString());
                check("[VSRG] 标题 / 艺术家取 Unicode 优先", chart.Title == "测试曲" && chart.Artist == "测试手",
                    $"{chart.Artist} - {chart.Title}");
                check("[VSRG] 难度名取 [Metadata] Version", chart.DifficultyName == "标准", chart.DifficultyName);
                check("[VSRG] 打击物 3 个（坏行被跳过）", chart.Notes.Count == 3, chart.Notes.Count.ToString());
                check("[VSRG] 坏行记进警告里", warnings.Lines.Count == 1,
                    string.Join("；", warnings.Lines));
                check("[VSRG] 普通键 / 面条分别计数（1 个面条）",
                    chart.NoteCount == 2 && chart.LongNoteCount == 1,
                    $"普通 {chart.NoteCount} / 面条 {chart.LongNoteCount}");
                check("[VSRG] 面条结束时间从第 6 列解析", chart.Notes.Any(n => n.IsLongNote && Math.Abs(n.EndMs - 2500) < 0.01),
                    string.Join("；", chart.Notes.Select(n => n.ToString())));
                check("[VSRG] 列号 = floor(x × keys / 512)：x=64 → 第 0 列、x=320 → 第 2 列",
                    chart.Notes.Any(n => Math.Abs(n.StartMs - 1000) < 0.01 && n.Column == 0) &&
                    chart.Notes.Any(n => Math.Abs(n.StartMs - 3200) < 0.01 && n.Column == 2),
                    string.Join("；", chart.Notes.Select(n => $"{n.Column}列@{n.StartMs}")));
                check("[VSRG] 音频 / 背景图按实际存在的文件解析出完整路径",
                    chart.AudioPath == audio && chart.BackgroundPath == background,
                    $"{chart.AudioPath} | {chart.BackgroundPath}");
                check("[VSRG] 能游玩（有音频）", chart.CanPlay && chart.PlayBlockReason.Length == 0,
                    chart.PlayBlockReason);
                check("[VSRG] 首尾时间正确（1000 ~ 3200）",
                    Math.Abs(chart.FirstNoteMs - 1000) < 0.01 && Math.Abs(chart.LastNoteMs - 3200) < 0.01,
                    $"{chart.FirstNoteMs} ~ {chart.LastNoteMs}");
                check("[VSRG] 谱面长度文本是 m:ss", chart.LengthText == "0:03", chart.LengthText);
            }

            // ---- 非 mania 谱面拒绝 ----
            var stdPath = Path.Combine(dir, "std.osu");
            File.WriteAllText(stdPath, BuildOsu(keys: 4, mode: 0, audioName: "audio.ogg", bgName: "bg.jpg",
                notes: new[] { "64,192,1000,1,0,0:0:0:0:" }), new UTF8Encoding(false));

            var stdOk = OsuChartReader.TryLoad(stdPath, out var stdChart, out var stdError);
            check("[VSRG] 只认 mode 3：标准谱（Mode=0）被拒",
                !stdOk && stdChart is null && stdError is not null && stdError.Contains("需要 3"),
                stdError ?? "居然读成功了");

            // ---- 键数超范围拒绝 ----
            var bigPath = Path.Combine(dir, "12k.osu");
            File.WriteAllText(bigPath, BuildOsu(keys: 12, mode: 3, audioName: "audio.ogg", bgName: "bg.jpg",
                notes: new[] { "64,192,1000,1,0,0:0:0:0:" }), new UTF8Encoding(false));

            var bigOk = OsuChartReader.TryLoad(bigPath, out _, out var bigError);
            check("[VSRG] 键数 12 被拒（只支持 1~10）",
                !bigOk && bigError is not null && bigError.Contains("不受支持"), bigError ?? "居然读成功了");

            // ---- 1K / 10K 都能读 ----
            var oneK = Path.Combine(dir, "1k.osu");
            File.WriteAllText(oneK, BuildOsu(keys: 1, mode: 3, audioName: "audio.ogg", bgName: "bg.jpg",
                notes: new[] { "256,192,1000,1,0,0:0:0:0:" }), new UTF8Encoding(false));
            var tenK = Path.Combine(dir, "10k.osu");
            File.WriteAllText(tenK, BuildOsu(keys: 10, mode: 3, audioName: "audio.ogg", bgName: "bg.jpg",
                notes: new[] { "486,192,1000,1,0,0:0:0:0:" }), new UTF8Encoding(false));

            var oneOk = OsuChartReader.TryLoad(oneK, out var oneChart, out _);
            var tenOk = OsuChartReader.TryLoad(tenK, out var tenChart, out _);
            check("[VSRG] 1K 谱面能读、列号夹在 0", oneOk && oneChart is { KeyCount: 1 } &&
                                                   oneChart.Notes[0].Column == 0,
                oneChart?.KeyCount.ToString() ?? "读失败");
            check("[VSRG] 10K 谱面能读、x=486 落在第 9 列",
                tenOk && tenChart is { KeyCount: 10 } && tenChart.Notes[0].Column == 9,
                tenChart is null ? "读失败" : $"{tenChart.KeyCount}K 第 {tenChart.Notes[0].Column} 列");

            // ---- 缺音频：能读但不能游玩 ----
            var noAudioPath = Path.Combine(dir, "no-audio.osu");
            File.WriteAllText(noAudioPath, BuildOsu(keys: 4, mode: 3, audioName: "missing.mp3", bgName: "",
                notes: new[] { "64,192,1000,1,0,0:0:0:0:" }), new UTF8Encoding(false));
            var noAudioOk = OsuChartReader.TryLoad(noAudioPath, out var noAudioChart, out _);
            check("[VSRG] 缺音频时能读、但不能游玩（给出原因）",
                noAudioOk && noAudioChart is { CanPlay: false } && noAudioChart.PlayBlockReason.Contains("音频"),
                noAudioChart?.PlayBlockReason ?? "读失败");

            // ---- 安全：谱面里的相对文件名不许逃出目录 ----
            check("[VSRG] 谱面文件名安全检查挡住 ../ 与绝对路径",
                !OsuChartReader.IsSafeRelativeName("../x.ogg") &&
                !OsuChartReader.IsSafeRelativeName("C:\\x.ogg") &&
                !OsuChartReader.IsSafeRelativeName("/x.ogg") &&
                OsuChartReader.IsSafeRelativeName("audio.ogg") &&
                OsuChartReader.IsSafeRelativeName("sub\\audio.ogg"),
                "");

            // ---- 背景行解析 ----
            check("[VSRG] 背景行三种写法都认",
                OsuChartReader.ParseBackgroundFileName("0,0,\"bg.jpg\",0,0") == "bg.jpg" &&
                OsuChartReader.ParseBackgroundFileName("0,0,bg.jpg") == "bg.jpg" &&
                OsuChartReader.ParseBackgroundFileName("Background,0,0,\"bg.jpg\"") == "bg.jpg",
                "");

            check("[VSRG] 视频行 / storyboard 行不算背景",
                OsuChartReader.ParseBackgroundFileName("1,0,\"vid.mp4\",0,0") is null &&
                OsuChartReader.ParseBackgroundFileName("Sprite,Foreground,Centre,\"a.png\",320,240") is null &&
                OsuChartReader.ParseBackgroundFileName("//Background and Video events") is null,
                "");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---------------------------------------------------------------- .osz 导入

    private static void CheckOszImport(CheckFn check)
    {
        var root = SelfCheckScratch.MakeDirectory("vsrg-import");
        var dataDir = Path.Combine(root, "data");
        var storage = ToolStorage.For("vsrg", Path.Combine("vsrg-selfcheck", "data"));

        try
        {
            var library = new MapsetLibrary(storage);
            var osz = BuildOsz(root, "normal.osz", new Dictionary<string, string>
            {
                ["Artist - Title (4K).osu"] = BuildOsu(keys: 4, mode: 3, audioName: "song.ogg", bgName: "bg.jpg",
                    notes: new[] { "64,192,1000,1,0,0:0:0:0:", "320,192,2000,1,0,0:0:0:0:" }),
                ["Artist - Title (7K).osu"] = BuildOsu(keys: 7, mode: 3, audioName: "song.ogg", bgName: "bg.jpg",
                    notes: new[] { "36,192,1000,1,0,0:0:0:0:" }),
                ["song.ogg"] = "fake-ogg",
                ["bg.jpg"] = "fake-jpg",
                ["video.mp4"] = new string('x', 64),              // 视频：跳过
                ["hitsound/normal-hitnormal.wav"] = new string('y', 32),  // hitsound：跳过
            });

            var result = library.Import(osz);
            check("[VSRG] .osz 导入成功", result.Success, result.Error);
            check("[VSRG] 导入结果里有 2 个难度", result.Index?.Difficulties.Count == 2,
                result.Index is null ? "没有索引" : result.Index.Difficulties.Count.ToString());
            check("[VSRG] 谱面集文件夹按「艺术家 - 标题」建",
                Directory.Exists(result.FolderPath) &&
                Path.GetFileName(result.FolderPath) == "测试手 - 测试曲",
                result.FolderPath);
            check("[VSRG] 音频 / 背景图 / 谱面 / 索引都在歌曲文件夹里",
                File.Exists(Path.Combine(result.FolderPath, "song.ogg")) &&
                File.Exists(Path.Combine(result.FolderPath, "bg.jpg")) &&
                File.Exists(Path.Combine(result.FolderPath, "Artist - Title (4K).osu")) &&
                File.Exists(Path.Combine(result.FolderPath, MapsetLibrary.IndexFileName)),
                string.Join(",", Directory.EnumerateFiles(result.FolderPath).Select(Path.GetFileName)));
            check("[VSRG] 视频与 hitsound 没有解出来（省空间）",
                !File.Exists(Path.Combine(result.FolderPath, "video.mp4")) &&
                !Directory.Exists(Path.Combine(result.FolderPath, "hitsound")),
                string.Join(",", Directory.EnumerateFileSystemEntries(result.FolderPath).Select(Path.GetFileName)));
            check("[VSRG] 谱面集根目录是工具数据目录下的 Mapset 子目录",
                string.Equals(Path.GetFileName(library.MapsetRoot), "Mapset", StringComparison.Ordinal) &&
                string.Equals(Path.GetDirectoryName(library.MapsetRoot),
                    storage.Directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
                library.MapsetRoot);

            // ---- 重名不覆盖 ----
            var again = library.Import(osz);
            check("[VSRG] 再导入一次不覆盖：自动加 (2)",
                again.Success && Path.GetFileName(again.FolderPath) == "测试手 - 测试曲 (2)",
                again.FolderPath);
            check("[VSRG] 两次导入后列表里有 2 张谱面集", library.Scan().Count == 2,
                library.Scan().Count.ToString());

            // ---- 非 mania 整包拒绝 ----
            var stdOsz = BuildOsz(root, "std.osz", new Dictionary<string, string>
            {
                ["std.osu"] = BuildOsu(keys: 4, mode: 0, audioName: "s.ogg", bgName: "b.jpg",
                    notes: new[] { "64,192,1000,1,0,0:0:0:0:" }),
                ["s.ogg"] = "x",
                ["b.jpg"] = "x",
            });

            var stdResult = library.Import(stdOsz);
            check("[VSRG] 不含 mania 的 .osz 整包拒绝", !stdResult.Success && stdResult.Error.Contains("mania"),
                stdResult.Error);
            check("[VSRG] 拒绝时不留半个谱面集", library.Scan().Count == 2, library.Scan().Count.ToString());

            // ---- 混合：一张 mania + 一张非 mania → 收 mania，记跳过 ----
            var mixedOsz = BuildOsz(root, "mixed.osz", new Dictionary<string, string>
            {
                ["both (mania).osu"] = BuildOsu(keys: 5, mode: 3, audioName: "m.ogg", bgName: "m.jpg",
                    notes: new[] { "51,192,1000,1,0,0:0:0:0:" }),
                ["both (std).osu"] = BuildOsu(keys: 4, mode: 0, audioName: "m.ogg", bgName: "m.jpg",
                    notes: new[] { "64,192,1000,1,0,0:0:0:0:" }),
                ["m.ogg"] = "x",
                ["m.jpg"] = "x",
            });

            var mixed = library.Import(mixedOsz);
            check("[VSRG] 混合包只收 mania，非 mania 记进「跳过」",
                mixed.Success && mixed.Index?.Difficulties.Count == 1 && mixed.RejectedCharts.Count == 1,
                $"{mixed.Index?.Difficulties.Count} 个难度 / 跳过 {mixed.RejectedCharts.Count}");

            // ---- 路径穿越：压缩包里带 ../ 的条目不许逃出去 ----
            var evilOsz = BuildOsz(root, "evil.osz", new Dictionary<string, string>
            {
                ["../evil.txt"] = "pwned",
                ["../../evil2.osu"] = BuildOsu(keys: 4, mode: 3, audioName: "e.ogg", bgName: "",
                    notes: new[] { "64,192,1000,1,0,0:0:0:0:" }),
                ["good.osu"] = BuildOsu(keys: 4, mode: 3, audioName: "e.ogg", bgName: "",
                    notes: new[] { "64,192,1000,1,0,0:0:0:0:" }),
                ["e.ogg"] = "x",
            });

            var evil = library.Import(evilOsz);
            check("[VSRG] 路径穿越的条目被丢弃（仍能导入干净的那张）",
                evil.Success && evil.Index?.Difficulties.Count == 1, evil.Error);
            check("[VSRG] 穿越文件没有落到 Mapset 外面",
                !File.Exists(Path.Combine(library.MapsetRoot, "evil.txt")) &&
                !File.Exists(Path.Combine(root, "evil.txt")) &&
                !File.Exists(Path.Combine(root, "evil2.osu")),
                string.Join(",", Directory.EnumerateFiles(root).Select(Path.GetFileName)));

            check("[VSRG] 条目名映射：挡绝对路径 / 隐藏名 / 驱动器号",
                !MapsetLibrary.TryMapEntryName("../a.txt", out _) &&
                !MapsetLibrary.TryMapEntryName("/abs/a.txt".Replace("/abs/", "../"), out _) &&
                !MapsetLibrary.TryMapEntryName(".hidden", out _) &&
                !MapsetLibrary.TryMapEntryName("C:\\a.txt", out _) &&
                MapsetLibrary.TryMapEntryName("ok.osu", out var mapped) && mapped == "ok.osu",
                "");

            // ---- 文件夹名清洗 ----
            check("[VSRG] 文件夹名清洗：非法字符去掉、首尾点去掉、长度截断",
                MapsetLibrary.SanitizeFolderName("a/b:c*d?e\"f<g>h|i") == "abcdefghi" &&
                MapsetLibrary.SanitizeFolderName("..") == "" &&
                MapsetLibrary.SanitizeFolderName(new string('x', 200)).Length == 80,
                MapsetLibrary.SanitizeFolderName("a/b:c*d?e\"f<g>h|i"));

            // ---- 删除只删这一首歌 ----
            var entries = library.Scan();
            var first = entries.First();
            var before = entries.Count;
            var deleted = library.Delete(first);
            check("[VSRG] 删除一张谱面集只删它的文件夹",
                deleted && library.Scan().Count == before - 1 && !Directory.Exists(first.FolderPath),
                $"{before} → {library.Scan().Count}");

            // ---- 索引被删掉时能现场重建 ----
            var survivor = library.Scan().FirstOrDefault();
            if (survivor is not null)
            {
                File.Delete(Path.Combine(survivor.FolderPath, MapsetLibrary.IndexFileName));
                var rescanned = library.Scan();
                check("[VSRG] mapset.json 缺失时按文件夹里的 .osu 现场重建",
                    rescanned.Any(e => e.Index.Difficulties.Count > 0),
                    string.Join(",", rescanned.Select(e => $"{e.TitleLine}({e.Index.DifficultyCount})")));
            }
        }
        finally
        {
            TryDelete(root);
            TryDelete(storage.Directory);
            _ = dataDir;
        }
    }

    // ---------------------------------------------------------------- 判定档

    private static void CheckTiers(CheckFn check)
    {
        var tiers = GameplayProfile.StandardTiers();
        check("[VSRG] Standard 预设四档：Marvelous / Good / Bad / Miss",
            tiers.Count == 4 && tiers[0].Name == "Marvelous" && tiers[1].Name == "Good" &&
            tiers[2].Name == "Bad" && tiers[3].Name == "Miss",
            string.Join(" / ", tiers.Select(t => t.Name)));
        check("[VSRG] Standard 窗口 22 / 50 / 90，Miss 是兜底",
            Math.Abs(tiers[0].WindowMs - 22) < 0.01 && Math.Abs(tiers[1].WindowMs - 50) < 0.01 &&
            Math.Abs(tiers[2].WindowMs - 90) < 0.01 && tiers[3].IsMiss,
            string.Join(" / ", tiers.Select(t => $"{t.Name}:{t.WindowMs}")));
        check("[VSRG] Standard 准确率权重 100 / 65 / 30 / 0",
            Math.Abs(tiers[0].AccuracyWeight - 100) < 0.01 && Math.Abs(tiers[1].AccuracyWeight - 65) < 0.01 &&
            Math.Abs(tiers[2].AccuracyWeight - 30) < 0.01 && Math.Abs(tiers[3].AccuracyWeight) < 0.01,
            string.Join(" / ", tiers.Select(t => $"{t.Name}:{t.AccuracyWeight}")));
        check("[VSRG] 四档都是预设（不可删）", tiers.Take(3).All(t => t.IsPreset), "");

        var profile = GameplayProfile.Standard();
        check("[VSRG] 匹配：15ms → Marvelous、40ms → Good、80ms → Bad、200ms → 没有档（Miss）",
            profile.Match(15)?.Name == "Marvelous" && profile.Match(40)?.Name == "Good" &&
            profile.Match(80)?.Name == "Bad" && profile.Match(200) is null,
            $"{profile.Match(15)?.Name} / {profile.Match(40)?.Name} / {profile.Match(80)?.Name} / {profile.Match(200)?.Name ?? "null"}");
        check("[VSRG] 匹配取负数的绝对值（早按晚按一视同仁）",
            profile.Match(-15)?.Name == "Marvelous" && profile.Match(-80)?.Name == "Bad", "");
        check("[VSRG] 最大窗口 = 90ms（最宽松档）", Math.Abs(profile.MaxWindowMs - 90) < 0.01,
            profile.MaxWindowMs.ToString());
        check("[VSRG] Miss 档序号是最后一个", profile.MissTierIndex == 3, profile.MissTierIndex.ToString());

        // 尾窗放大：35ms 的档 × 1.5 = 52.5ms
        var scaled = profile.MatchScaled(52, 1.5);
        check("[VSRG] 尾判窗口按乘数放大（50ms 档 × 1.5 = 75ms → 52ms 仍是 Good）",
            scaled?.Name == "Good", scaled?.Name ?? "null");
        check("[VSRG] 乘数放大不会让超窗的判定混进来（200ms × 1.5 也不够）",
            profile.MatchScaled(200, 1.5) is null, "");

        // 校验
        check("[VSRG] 校验：Standard 配置合法", GameplayProfile.ValidateTiers(tiers) is null,
            GameplayProfile.ValidateTiers(tiers) ?? "");
        check("[VSRG] 校验：没有档时报错", GameplayProfile.ValidateTiers(Array.Empty<JudgementTier>()) is not null, "");
        check("[VSRG] 校验：没有 Miss 档时报错",
            GameplayProfile.ValidateTiers(new[] { tiers[0], tiers[1], tiers[2] }) is not null, "");
        check("[VSRG] 校验：两个 Miss 档也报错",
            GameplayProfile.ValidateTiers(new[] { tiers[0], tiers[3], tiers[3] }) is not null, "");
        check("[VSRG] 校验：权重超 100 报错",
            GameplayProfile.ValidateTiers(new[]
            {
                new JudgementTier { Name = "A", WindowMs = 20, AccuracyWeight = 120 },
                new JudgementTier { Name = "Miss", IsMiss = true },
            }) is not null, "");
        check("[VSRG] 校验：名字为空报错",
            GameplayProfile.ValidateTiers(new[]
            {
                new JudgementTier { Name = "", WindowMs = 20, AccuracyWeight = 100 },
                new JudgementTier { Name = "Miss", IsMiss = true },
            }) is not null, "");

        // 排序后 Miss 仍兜底
        var reordered = new[] { tiers[0], tiers[1], tiers[3], tiers[2] };
        var reorderedProfile = new GameplayProfile(reordered, true, 1.5);
        check("[VSRG] 判定档可排序，且重新排序后仍能匹配",
            reorderedProfile.Match(15)?.Name == "Marvelous" && reorderedProfile.Match(80)?.Name == "Bad",
            $"{reorderedProfile.Match(15)?.Name} / {reorderedProfile.Match(80)?.Name}");
        check("[VSRG] 排序后 Miss 仍是兜底（MissTier 指向它）",
            ReferenceEquals(reorderedProfile.MissTier, tiers[3]) && reorderedProfile.MissTierIndex == 2,
            reorderedProfile.MissTierIndex.ToString());
    }

    // ---------------------------------------------------------------- 游玩引擎（普通键）

    private static void CheckGameplay(CheckFn check)
    {
        var chart = SyntheticChart(4, new[]
        {
            new ChartNote(0, 1000, 1000, false),
            new ChartNote(1, 2000, 2000, false),
            new ChartNote(2, 3000, 3000, false),
        });

        var engine = new GameplayEngine(chart, GameplayProfile.Standard());

        // 精准命中：Marvelous
        engine.AdvanceTo(1000);
        var hit = engine.Press(0);
        check("[VSRG] 正好压线 → Marvelous", hit?.TierName == "Marvelous" && engine.Combo == 1,
            $"{hit?.TierName} combo={engine.Combo}");
        check("[VSRG] 准确率按权重算：1 个 100% = 100%",
            Math.Abs(engine.Accuracy - 100) < 0.01, engine.Accuracy.ToString("0.##"));
        engine.Release(0);

        // 偏差 40ms：Good（65%）
        engine.AdvanceTo(2040);
        var good = engine.Press(1);
        check("[VSRG] 偏 40ms → Good", good?.TierName == "Good", good?.TierName ?? "没判到");
        engine.Release(1);
        check("[VSRG] 两个命中后准确率 = (100+65)/2 = 82.5%",
            Math.Abs(engine.Accuracy - 82.5) < 0.01, engine.Accuracy.ToString("0.##"));
        check("[VSRG] combo 累到 2", engine.Combo == 2 && engine.MaxCombo == 2,
            $"combo={engine.Combo} max={engine.MaxCombo}");

        // 漏掉第 3 个：时间过去 → Miss，combo 归零
        engine.AdvanceTo(3000 + GameplayProfile.Standard().MaxWindowMs + 1);
        check("[VSRG] 超过最大窗口还没按 → Miss", engine.MissCount == 1, engine.MissCount.ToString());
        check("[VSRG] Miss 会把 combo 清零", engine.Combo == 0, engine.Combo.ToString());
        check("[VSRG] 最大 combo 保留（不会被清零）", engine.MaxCombo == 2, engine.MaxCombo.ToString());
        check("[VSRG] 准确率 =(100+65+0)/(3×100) = 55%",
            Math.Abs(engine.Accuracy - 55) < 0.01, engine.Accuracy.ToString("0.##"));
        check("[VSRG] 全部判完后 AllJudged = true", engine.AllJudged, $"{engine.JudgedUnits}/{engine.TotalUnits}");
        check("[VSRG] 时间过完 → IsFinished", engine.IsFinished, $"{engine.NowMs} / {engine.EndMs}");

        // 判定档统计
        var counts = engine.TierCounts;
        check("[VSRG] 各档次数统计（Marvelous 1 / Good 1 / Miss 1）",
            counts[0] == 1 && counts[1] == 1 && counts[3] == 1,
            string.Join(",", counts));

        // ---- 太早按：不判任何东西 ----
        var early = new GameplayEngine(SyntheticChart(4, new[] { new ChartNote(0, 5000, 5000, false) }),
            GameplayProfile.Standard());
        early.AdvanceTo(5000 - GameplayProfile.Standard().MaxWindowMs - 100);
        var earlyHit = early.Press(0);
        check("[VSRG] 太早按不判任何东西（等窗口）", earlyHit is null && early.JudgedUnits == 0,
            $"{earlyHit?.TierName} / {early.JudgedUnits}");

        // ---- 空按（本轨没 note）：不判也不掉 combo ----
        var idle = new GameplayEngine(SyntheticChart(4, new[] { new ChartNote(0, 1000, 1000, false) }),
            GameplayProfile.Standard());
        idle.AdvanceTo(1000);
        idle.Press(0);
        idle.Release(0);
        var idleHit = idle.Press(3);
        check("[VSRG] 本轨没 note 的空按不判、不掉 combo", idleHit is null && idle.Combo == 1, idle.Combo.ToString());

        // ---- 同一时刻多个 note（不同轨）都能打 ----
        var chord = new GameplayEngine(SyntheticChart(4, new[]
        {
            new ChartNote(0, 1000, 1000, false),
            new ChartNote(1, 1000, 1000, false),
        }), GameplayProfile.Standard());
        chord.AdvanceTo(1000);
        chord.Press(0);
        chord.Press(1);
        check("[VSRG] 同刻不同轨的两个 note 都能判到", chord.JudgedUnits == 2 && chord.Combo == 2,
            $"{chord.JudgedUnits} / combo {chord.Combo}");
    }

    // ---------------------------------------------------------------- 面条头尾判定

    private static void CheckTailJudgement(CheckFn check)
    {
        // 一个 1000 → 2000 的面条
        ChartNote[] notes = { new(0, 1000, 2000, true) };

        // ---- 开尾判：头 + 尾各一个判定单元 ----
        var on = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), tailJudgementEnabled: true, tailWindowMultiplier: 1.5));
        check("[VSRG] 开尾判：面条算 2 个判定单元", on.TotalUnits == 2, on.TotalUnits.ToString());

        on.AdvanceTo(1000);
        on.Press(0);
        check("[VSRG] 面条头判命中", on.JudgedUnits == 1 && on.Combo == 1, on.Combo.ToString());
        check("[VSRG] 按住中：引擎知道这条面条正被保持", on.IsHoldingLongNote(0), "");

        // 尾巴 2000，提前 40ms 松（Good 窗口 50 × 1.5 = 75，Marvelous 22 × 1.5 = 33）
        on.AdvanceTo(1960);
        var tail = on.Release(0);
        check("[VSRG] 尾判按「窗口 × 乘数」算（提前 40ms → Good）",
            tail?.Unit == NoteUnit.Tail && tail.Value.TierName == "Good",
            tail?.TierName ?? "没判到");
        check("[VSRG] 尾判也算进准确率分母（2 个单元：(100+65)/200 = 82.5%）",
            Math.Abs(on.Accuracy - 82.5) < 0.01, on.Accuracy.ToString("0.##"));

        // ---- 早松：尾判 Miss ----
        var earlyRelease = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), true, 1.5));
        earlyRelease.AdvanceTo(1000);
        earlyRelease.Press(0);
        earlyRelease.AdvanceTo(1500);   // 还差 500ms 就松了
        var earlyTail = earlyRelease.Release(0);
        check("[VSRG] 提前松手（远早于尾巴的尾窗）→ 尾判 Miss",
            earlyTail?.Unit == NoteUnit.Tail && earlyTail.Value.Miss && earlyRelease.Combo == 0,
            $"{earlyTail?.TierName} combo={earlyRelease.Combo}");

        // ---- 按到底：尾判最好档 ----
        var holdToEnd = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), true, 1.5));
        holdToEnd.AdvanceTo(1000);
        holdToEnd.Press(0);
        holdToEnd.AdvanceTo(2600);    // 2000 + 90×1.5 = 2135，过了
        check("[VSRG] 一直按过尾窗 → 尾判最好档（Marvelous）",
            holdToEnd.JudgedUnits == 2 && holdToEnd.TierCounts[0] == 2,
            string.Join(",", holdToEnd.TierCounts));

        // ---- 头漏了：尾巴一起 Miss ----
        var missedHead = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), true, 1.5));
        missedHead.AdvanceTo(1000 + 91);
        check("[VSRG] 头漏了 → 尾巴一起判 Miss（2 个单元都是 Miss）",
            missedHead.JudgedUnits == 2 && missedHead.MissCount == 2,
            $"{missedHead.JudgedUnits} / Miss {missedHead.MissCount}");

        // ---- 关尾判：只算头 ----
        var off = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), tailJudgementEnabled: false, tailWindowMultiplier: 1.5));
        check("[VSRG] 关尾判：总单元数 = note 数（面条不额外算尾）", off.TotalUnits == 1, off.TotalUnits.ToString());

        off.AdvanceTo(1000);
        off.Press(0);
        off.AdvanceTo(1900);
        var offTail = off.Release(0);
        check("[VSRG] 关尾判：松手不产生尾判", offTail is null && off.JudgedUnits == 1,
            offTail?.TierName ?? "null");
        off.AdvanceTo(9000);
        check("[VSRG] 关尾判：打完也只有一个单元", off.JudgedUnits == 1 && off.IsFinished,
            $"{off.JudgedUnits} / finished={off.IsFinished}");

        // ---- 尾窗乘数真的生效：同一个松手时刻，乘数不同 → 判定不同 ----
        var tight = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), true, 1.0));
        tight.AdvanceTo(1000);
        tight.Press(0);
        tight.AdvanceTo(1940);   // 提前 60ms
        var tightTail = tight.Release(0);

        var loose = new GameplayEngine(SyntheticChart(4, notes),
            new GameplayProfile(GameplayProfile.StandardTiers(), true, 2.0));
        loose.AdvanceTo(1000);
        loose.Press(0);
        loose.AdvanceTo(1940);
        var looseTail = loose.Release(0);

        check("[VSRG] 尾窗乘数生效：提前 60ms 松，×1.0 → Bad、×2.0 → Good",
            tightTail?.TierName == "Bad" && looseTail?.TierName == "Good",
            $"×1.0={tightTail?.TierName} ×2.0={looseTail?.TierName}");
    }

    // ---------------------------------------------------------------- 键位

    private static void CheckKeyBindings(CheckFn check)
    {
        check("[VSRG] 键名往返：A / 分号 / Space 都能解析成 Key",
            LaneKeys.ToKey("A") == System.Windows.Input.Key.A &&
            LaneKeys.ToKey("Space") == System.Windows.Input.Key.Space &&
            LaneKeys.ToKey("分号") == System.Windows.Input.Key.OemSemicolon,
            $"{LaneKeys.ToKey("A")} / {LaneKeys.ToKey("Space")} / {LaneKeys.ToKey("分号")}");

        check("[VSRG] Key → 显示名 → Key 往返一致",
            LaneKeys.FromKey(System.Windows.Input.Key.F) == "F" &&
            LaneKeys.FromKey(System.Windows.Input.Key.Space) == "Space" &&
            LaneKeys.ToKey(LaneKeys.FromKey(System.Windows.Input.Key.OemSemicolon)) ==
            System.Windows.Input.Key.OemSemicolon,
            LaneKeys.FromKey(System.Windows.Input.Key.OemSemicolon));

        check("[VSRG] 不认识的键名 / 空串返回 null",
            LaneKeys.ToKey("这不是键") is null && LaneKeys.ToKey("") is null, "");

        check("[VSRG] 修饰键 / Tab / Esc 不能当游玩键",
            !LaneKeys.IsBindable(System.Windows.Input.Key.LeftShift) &&
            !LaneKeys.IsBindable(System.Windows.Input.Key.LeftCtrl) &&
            !LaneKeys.IsBindable(System.Windows.Input.Key.Tab) &&
            !LaneKeys.IsBindable(System.Windows.Input.Key.Escape) &&
            LaneKeys.IsBindable(System.Windows.Input.Key.A),
            "");

        var defaults = VsrgConfig.DefaultKeys;
        check("[VSRG] 默认 1~10 轨键位是 A S D F G H J K L 分号",
            defaults.Length == 10 && defaults[0] == "A" && defaults[3] == "F" && defaults[9] == "分号",
            string.Join(" ", defaults));

        var map = LaneKeys.BuildLaneMap(defaults, 4);
        var keyA = System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.A);
        var keyF = System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.F);
        var keyG = System.Windows.Input.KeyInterop.VirtualKeyFromKey(System.Windows.Input.Key.G);
        check("[VSRG] 4K 的轨道映射只含前 4 个键（A→0 / F→3，G 不参与）",
            map.Count == 4 && map.TryGetValue(keyA, out var laneA) && laneA == 0 &&
            map.TryGetValue(keyF, out var laneF) && laneF == 3 && !map.ContainsKey(keyG),
            string.Join(",", map.Select(p => $"{p.Key}→{p.Value}")));

        check("[VSRG] 键位冲突能被检出（A 绑了两轨）",
            LaneKeys.HasDuplicate(new[] { "A", "B", "A", "C" }, out var dup, out var first) &&
            dup == 2 && first == 0,
            $"{dup}/{first}");
        check("[VSRG] 没有冲突时返回 false",
            !LaneKeys.HasDuplicate(defaults, out _, out _), "");
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new VsrgConfig();

        check("[VSRG] 配置：默认不全屏", !config.PlayFullscreen, config.PlayFullscreen.ToString());
        check("[VSRG] 配置：默认音频偏移 0ms", config.AudioOffsetMs == 0, config.AudioOffsetMs.ToString());
        check("[VSRG] 配置：默认滚动速度 7.0", Math.Abs(config.ScrollSpeed - 7.0) < 0.001,
            config.ScrollSpeed.ToString());
        check("[VSRG] 配置：默认开面条尾判、乘数 1.5",
            config.TailJudgementEnabled && Math.Abs(config.TailWindowMultiplier - 1.5) < 0.001,
            $"{config.TailJudgementEnabled} / {config.TailWindowMultiplier}");
        check("[VSRG] 配置：默认判定档就是 Standard 四档",
            config.Tiers.Count == 4 && config.Tiers[0].Name == "Marvelous" && config.Tiers[3].IsMiss,
            string.Join(" / ", config.Tiers.Select(t => t.Name)));
        check("[VSRG] 配置：ToolId 是 vsrg（Data / Config 目录名）", VsrgConfig.ToolId == "vsrg", VsrgConfig.ToolId);

        // 夹取
        config.ScrollSpeed = 999;
        check("[VSRG] 滚动速度夹到 20 上限", Math.Abs(config.ScrollSpeed - 20) < 0.001,
            config.ScrollSpeed.ToString());
        config.ScrollSpeed = -5;
        check("[VSRG] 滚动速度夹到 1 下限", Math.Abs(config.ScrollSpeed - 1) < 0.001,
            config.ScrollSpeed.ToString());

        config.AudioOffsetMs = 9999;
        check("[VSRG] 音频偏移夹到 500", config.AudioOffsetMs == 500, config.AudioOffsetMs.ToString());
        config.AudioOffsetMs = -9999;
        check("[VSRG] 音频偏移夹到 -500", config.AudioOffsetMs == -500, config.AudioOffsetMs.ToString());

        config.TailWindowMultiplier = 100;
        check("[VSRG] 尾判乘数夹到 10", Math.Abs(config.TailWindowMultiplier - 10) < 0.001,
            config.TailWindowMultiplier.ToString());

        // 键位读写 + 恢复默认
        config.SetKey(0, "Z");
        config.SetKey(9, "P");
        check("[VSRG] 能按轨道号读写键位", config.GetKey(0) == "Z" && config.GetKey(9) == "P",
            $"{config.GetKey(0)} / {config.GetKey(9)}");

        config.ResetKeys();
        check("[VSRG] 恢复默认键位", config.GetKey(0) == "A" && config.GetKey(9) == "分号",
            $"{config.GetKey(0)} / {config.GetKey(9)}");

        // 判定档恢复默认
        config.Tiers = new List<JudgementTierConfig>
        {
            new() { Name = "只有一档", WindowMs = 40, AccuracyWeight = 100 },
        };
        config.ResetTiers();
        check("[VSRG] 恢复 Standard 判定档", config.Tiers.Count == 4 && config.Tiers[3].IsMiss,
            string.Join(" / ", config.Tiers.Select(t => t.Name)));

        // ApproachMs 跟随速度
        config.ScrollSpeed = 9;
        check("[VSRG] 速度 9 时 note 落满屏幕用 100ms（900 / 9）",
            Math.Abs(config.ApproachMs - 100) < 0.01, config.ApproachMs.ToString("0.##"));

        // 配置拍成判定快照
        var profile = config.BuildProfile();
        check("[VSRG] 配置能拍成游玩用的判定快照",
            profile.Tiers.Count == 4 && profile.TailJudgementEnabled && profile.MissTierIndex == 3,
            $"{profile.Tiers.Count} / miss@{profile.MissTierIndex}");

        // 支持键数列表
        check("[VSRG] 支持 1K~10K", VsrgConfig.SupportedKeyCounts.SequenceEqual(Enumerable.Range(1, 10)),
            string.Join(",", VsrgConfig.SupportedKeyCounts));
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>造一张只有 note 的谱面（不碰文件系统）。</summary>
    private static Chart SyntheticChart(int keyCount, IEnumerable<ChartNote> notes)
    {
        var list = notes.OrderBy(n => n.StartMs).ToList();
        return new Chart
        {
            FilePath = "memory.osu",
            Title = "合成谱面",
            Artist = "自检",
            DifficultyName = "Test",
            KeyCount = keyCount,
            Notes = list,
            NoteCount = list.Count(n => !n.IsLongNote),
            LongNoteCount = list.Count(n => n.IsLongNote),
            FirstNoteMs = list.Count > 0 ? list[0].StartMs : 0,
            LastNoteMs = list.Count > 0 ? list.Max(n => n.IsLongNote ? n.EndMs : n.StartMs) : 0,
        };
    }

    /// <summary>造一段最小的 .osu 文本。</summary>
    private static string BuildOsu(int keys, int mode, string audioName, string bgName, string[] notes)
    {
        var builder = new StringBuilder();
        builder.Append("osu file format v14\n\n");
        builder.Append("[General]\n");
        builder.Append($"AudioFilename: {audioName}\n");
        builder.Append("AudioLeadIn: 0\n");
        builder.Append($"Mode: {mode}\n\n");
        builder.Append("[Metadata]\n");
        builder.Append("Title:测试曲\n");
        builder.Append("TitleUnicode:测试曲\n");
        builder.Append("Artist:测试手\n");
        builder.Append("ArtistUnicode:测试手\n");
        builder.Append("Creator:自检\n");
        builder.Append("Version:标准\n\n");
        builder.Append("[Difficulty]\n");
        builder.Append("HPDrainRate:8\n");
        builder.Append($"CircleSize:{keys}\n");
        builder.Append("OverallDifficulty:8\n\n");
        builder.Append("[Events]\n");
        builder.Append("//Background and Video events\n");
        if (!string.IsNullOrWhiteSpace(bgName)) builder.Append($"0,0,\"{bgName}\",0,0\n");
        builder.Append("//Break Periods\n\n");
        builder.Append("[TimingPoints]\n");
        builder.Append("0,500,4,2,0,60,1,0\n\n");
        builder.Append("[HitObjects]\n");
        foreach (var note in notes) builder.Append(note).Append('\n');
        return builder.ToString();
    }

    /// <summary>造一个最小 .osz（就是 zip）。</summary>
    private static string BuildOsz(string root, string fileName, IReadOnlyDictionary<string, string> entries)
    {
        var path = Path.Combine(root, fileName);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            using var stream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { /* 临时目录删不掉不影响结论 */ }
    }
}
