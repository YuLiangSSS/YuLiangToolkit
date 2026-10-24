using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YuLiangToolkit.Tools.OsuMania.Core;
using YuLiangToolkit.Tools.OsuMania.Core.Difficulty;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「osu!mania 转谱」核心算法的自检：解析桥（OsuParsers 往返）/ 星数 / 等级 / 转 K / 转 LN / 批量入口。
///
/// <para>全部用临时目录里的合成谱面跑，不碰仓库的 Data/Config。
/// 这里断言的是「旧版 HeavenUsurperToolkit 的行为在移植后保持不变」以及本次明确修掉的几个旧 bug
/// （详见 docs/porting/osu-core-notes.md）。</para>
/// </summary>
public static class OsuManiaChecks
{
    /// <summary>自检断言（带可选 detail；由 <see cref="Run"/> 转成 Program 里的 Check 委托）。</summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>自检入口（由 Program.cs 统一调用；签名与其它 Checks 文件一致）。</summary>
    public static void Run(Action<string, bool, string> check, Action<string, bool> plain)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        var root = SelfCheckScratch.MakeDirectory("osu-mania");
        var dir = Path.Combine(root, "maps-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        CheckParsing(C, dir);
        CheckSaveRoundTrip(C, dir);
        CheckTimingAndKeyX(C);
        CheckStarRating(C);
        CheckLevel(C);
        CheckKeyConverter(C, dir);
        CheckLnTransformer(C, dir);
        CheckBatch(C, dir);
        CheckThreadSafety(C, dir);
    }

    // =====================================================================
    // 合成谱面文本
    // =====================================================================

    /// <summary>一个 4K 合成谱面：普通键 / 带 hitsound+extras 的键 / LN / 零长度 LN / 同刻双键 / 新连击段。</summary>
    private static string Sample4KText()
    {
        return """
osu file format v14

[General]
AudioFilename: audio.mp3
AudioLeadIn: 0
PreviewTime: -1
Countdown: 0
SampleSet: Soft
StackLeniency: 0.7
Mode: 3
LetterboxInBreaks: 0
SpecialStyle: 0
WidescreenStoryboard: 1

[Metadata]
Title:测试歌曲
TitleUnicode:测试歌曲
Artist:测试艺术家
ArtistUnicode:测试艺术家
Creator:测试谱师
Version:4K Hard
Source:
Tags:test tags
BeatmapID:123
BeatmapSetID:456

[Difficulty]
HPDrainRate:7
CircleSize:4
OverallDifficulty:8
ApproachRate:5
SliderMultiplier:1.4
SliderTickRate:1

[Events]
//Background and Video events
0,0,"bg.jpg",0,0

[TimingPoints]
0,500,4,2,0,60,1,0
12000,400,4,2,0,60,1,0
6000,-100,4,2,0,60,0,0

[HitObjects]
64,192,1000,1,0,0:0:0:0:
192,192,1500,1,4,2:2:0:60:hi.wav
320,192,2000,128,0,3000:0:0:0:0:
448,192,2500,1,0,0:0:0:0:
64,192,2500,1,0,0:0:0:0:
448,192,4000,128,0,4000:0:0:0:0:
64,192,5000,37,0,0:0:0:0:
""";
    }

    /// <summary>生成一个 N 键的简单谱面（每 160ms 一个键，30% 长条），给转谱/批量用例。</summary>
    private static string SimpleMapText(int keys, double od, string creator = "测试谱师", string version = "Test", string mode = "3", int noteCount = 80)
    {
        var sb = new StringBuilder();
        sb.AppendLine("osu file format v14");
        sb.AppendLine();
        sb.AppendLine("[General]");
        sb.AppendLine("AudioFilename: audio.mp3");
        sb.AppendLine("Mode: " + mode);
        sb.AppendLine();
        sb.AppendLine("[Metadata]");
        sb.AppendLine("Title:批量测试");
        sb.AppendLine("TitleUnicode:批量测试");
        sb.AppendLine("Artist:测试艺术家");
        sb.AppendLine("ArtistUnicode:测试艺术家");
        sb.AppendLine("Creator:" + creator);
        sb.AppendLine("Version:" + version);
        sb.AppendLine();
        sb.AppendLine("[Difficulty]");
        sb.AppendLine("CircleSize:" + keys);
        sb.AppendLine("OverallDifficulty:" + od.ToString("0.###"));
        sb.AppendLine("ApproachRate:5");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        sb.AppendLine("0,500,4,2,0,60,1,0");
        sb.AppendLine("12000,400,4,2,0,60,1,0");
        sb.AppendLine("6000,-100,4,2,0,60,0,0");
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");

        int time = 1000;
        for (int i = 0; i < noteCount; i++)
        {
            int column = i % keys;
            int x = OsuManiaBeatmap.ColumnX(keys, column);
            bool ln = i % 10 == 3;
            int end = ln ? time + 380 : time;
            sb.Append(x).Append(",192,").Append(time).Append(ln ? ",128,0," : ",1,0,");
            // 普通键的第 6 列是 hitSample（4 个冒号分隔字段）；长条的第 6 列第一段才是结束时间。
            sb.Append(ln ? end + ":0:0:0:0:" : "0:0:0:0:");
            sb.AppendLine();
            time += 160;
        }

        return sb.ToString();
    }

    private static string WriteText(string dir, string fileName, string text)
    {
        string path = Path.Combine(dir, fileName);
        // UTF-8 无 BOM（与 osu! 客户端写出来的文件一致；带 BOM 的读取另有一项检查）
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static OsuBeatmapFile Reload(string path)
    {
        if (!OsuBeatmapFile.TryLoad(path, out var file, out var error))
        {
            throw new InvalidOperationException($"自检里重新加载谱面失败：{error}");
        }

        return file!;
    }

    // =====================================================================
    // 1. 解析桥
    // =====================================================================

    private static void CheckParsing(CheckFn check, string dir)
    {
        string samplePath = WriteText(dir, "sample4k.osu", Sample4KText());

        bool loaded = OsuBeatmapFile.TryLoad(samplePath, out var file, out var error);
        check("解析：4K 谱面能加载", loaded && file is not null, error ?? "");
        if (file is null)
        {
            return;
        }

        var bm = file.Beatmap;
        check("解析：格式版本 = 14", bm.FormatVersion == 14, bm.FormatVersion.ToString());
        check("解析：键数 = 4", bm.KeyCount == 4, bm.KeyCount.ToString());
        check("解析：OD = 8", Math.Abs(bm.OverallDifficulty - 8) < 1e-6, bm.OverallDifficulty.ToString("R"));
        check("解析：标题 / 艺术家 / 谱师 / 难度名",
            bm.Metadata.Title == "测试歌曲" && bm.Metadata.Artist == "测试艺术家"
            && bm.Metadata.Creator == "测试谱师" && bm.Metadata.Difficulty == "4K Hard",
            $"{bm.Metadata.Title}/{bm.Metadata.Artist}/{bm.Metadata.Creator}/{bm.Metadata.Difficulty}");
        check("解析：AudioFilename / Tags / ID",
            bm.AudioFilename == "audio.mp3" && bm.Metadata.Tags == "test tags"
            && bm.Metadata.BeatmapID == 123 && bm.Metadata.BeatmapSetID == 456,
            $"{bm.AudioFilename}/{bm.Metadata.Tags}/{bm.Metadata.BeatmapID}/{bm.Metadata.BeatmapSetID}");
        check("解析：对象数 = 7", bm.HitObjects.Count == 7, bm.HitObjects.Count.ToString());

        var notes = bm.HitObjects;
        check("解析：x=64/192/320/448 对应列 0/1/2/3",
            notes[0].Column == 0 && notes[1].Column == 1 && notes[2].Column == 2 && notes[3].Column == 3,
            string.Join(",", notes.Select(n => n.Column)));
        check("解析：普通键 IsLongNote=false 且 EndTime=StartTime",
            !notes[0].IsLongNote && notes[0].EndTime == notes[0].StartTime && notes[0].StartTime == 1000,
            $"{notes[0].IsLongNote}/{notes[0].StartTime}/{notes[0].EndTime}");
        check("解析：长条起止时间正确",
            notes[2].IsLongNote && notes[2].StartTime == 2000 && notes[2].EndTime == 3000,
            $"{notes[2].IsLongNote}/{notes[2].StartTime}/{notes[2].EndTime}");
        check("解析：零长度长条保留 type=128 语义（IsLongNote=true 且两端相等）",
            notes[5].IsLongNote && notes[5].StartTime == 4000 && notes[5].EndTime == 4000,
            $"{notes[5].IsLongNote}/{notes[5].StartTime}/{notes[5].EndTime}");
        check("解析：HitSound 保留（第 2 个键是 Finish=4）", notes[1].HitSound == 4, notes[1].HitSound.ToString());
        check("解析：Extras 采样信息保留",
            notes[1].Extras.SampleSet == 2 && notes[1].Extras.AdditionSet == 2
            && notes[1].Extras.CustomIndex == 0 && notes[1].Extras.Volume == 60
            && notes[1].Extras.SampleFileName == "hi.wav",
            $"{notes[1].Extras.SampleSet}/{notes[1].Extras.AdditionSet}/{notes[1].Extras.CustomIndex}/{notes[1].Extras.Volume}/{notes[1].Extras.SampleFileName}");
        check("解析：新连击段 / 连击偏移保留",
            notes[6].IsNewCombo && notes[6].ComboOffset == 2,
            $"{notes[6].IsNewCombo}/{notes[6].ComboOffset}");

        check("解析：时间点 3 条（2 红 1 绿）", bm.TimingPoints.Count == 3
            && bm.TimingPoints.Count(t => t.BeatLength >= 0) == 2
            && bm.TimingPoints.Count(t => t.BeatLength < 0) == 1,
            string.Join(" / ", bm.TimingPoints.Select(t => $"{t.Time}:{t.BeatLength}")));

        check("解析：FilePath / Directory / OriginalFileName", file.FilePath == samplePath
            && file.Directory == Path.GetDirectoryName(samplePath) && file.OriginalFileName == "sample4k.osu",
            $"{file.FilePath} | {file.Directory} | {file.OriginalFileName}");

        check("解析：加载后星数已算好（>0 且有限）", bm.StarRating > 0 && double.IsFinite(bm.StarRating), bm.StarRating.ToString("R"));

        // Load 抛异常的版本
        bool loadThrew = false;
        try
        {
            _ = OsuBeatmapFile.Load(samplePath);
        }
        catch
        {
            loadThrew = true;
        }
        check("解析：Load 成功路径不抛异常", !loadThrew);

        // 带 BOM 的 UTF-8 文件（真实 osu 谱面常见）
        string bomPath = Path.Combine(dir, "bom.osu");
        File.WriteAllText(bomPath, Sample4KText(), new UTF8Encoding(true));
        bool bomOk = OsuBeatmapFile.TryLoad(bomPath, out var bomFile, out var bomError);
        check("解析：带 UTF-8 BOM 的谱面也能读", bomOk && bomFile!.Beatmap.KeyCount == 4, bomError ?? "");

        // 拒绝：非 mania
        string stdPath = WriteText(dir, "standard.osu", SimpleMapText(4, 8, mode: "0"));
        bool stdRejected = !OsuBeatmapFile.TryLoad(stdPath, out _, out var stdError);
        check("解析：非 mania 谱面被拒绝并给原因", stdRejected && stdError is not null && stdError.Contains("mania"),
            stdError ?? "(未拒绝)");

        // 拒绝：键数越界
        string keys12 = WriteText(dir, "keys12.osu", SimpleMapText(4, 8).Replace("CircleSize:4", "CircleSize:12"));
        bool keys12Rejected = !OsuBeatmapFile.TryLoad(keys12, out _, out var keys12Error);
        check("解析：键数 12 被拒绝并给原因", keys12Rejected && keys12Error is not null && keys12Error.Contains("键数"),
            keys12Error ?? "(未拒绝)");

        string keys0 = WriteText(dir, "keys0.osu", SimpleMapText(4, 8).Replace("CircleSize:4", "CircleSize:0"));
        check("解析：键数 0 被拒绝", !OsuBeatmapFile.TryLoad(keys0, out _, out _));

        // 拒绝：文件不存在 / 内容不是谱面
        check("解析：不存在的文件被拒绝", !OsuBeatmapFile.TryLoad(Path.Combine(dir, "nope.osu"), out _, out _));
        string broken = WriteText(dir, "broken.osu", "这不是谱面文件\n随便写点东西");
        check("解析：损坏文件被拒绝（不抛异常）", !OsuBeatmapFile.TryLoad(broken, out _, out _));
    }

    // =====================================================================
    // 2. 保存往返 / 输出命名规则
    // =====================================================================

    private static void CheckSaveRoundTrip(CheckFn check, string dir)
    {
        string samplePath = WriteText(dir, "roundtrip.osu", Sample4KText());
        var file = Reload(samplePath);
        var before = file.Beatmap;
        var beforeNotes = before.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime, n.IsLongNote, n.HitSound)).ToList();

        string savedPath = file.SaveTo(Path.Combine(dir, "roundtrip-out.osu"));
        check("保存：文件已写出", File.Exists(savedPath), savedPath);

        var reloaded = Reload(savedPath);
        var after = reloaded.Beatmap;
        check("保存往返：键数 / OD / 标题 / 谱师不变",
            after.KeyCount == before.KeyCount && Math.Abs(after.OverallDifficulty - before.OverallDifficulty) < 1e-6
            && after.Metadata.Title == before.Metadata.Title && after.Metadata.Creator == before.Metadata.Creator,
            $"{after.KeyCount}/{after.OverallDifficulty}/{after.Metadata.Title}/{after.Metadata.Creator}");
        check("保存往返：对象数一致", after.HitObjects.Count == beforeNotes.Count,
            $"{beforeNotes.Count} → {after.HitObjects.Count}");
        check("保存往返：所有对象的列 / 时间不变",
            after.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))
                 .SequenceEqual(beforeNotes.Select(n => (n.Column, n.StartTime, n.EndTime))),
            string.Join(" / ", after.HitObjects.Select(n => $"{n.Column}:{n.StartTime}-{n.EndTime}")));
        check("保存往返：长条 IsLongNote 与结束时间保留",
            after.HitObjects[2].IsLongNote && after.HitObjects[2].EndTime == 3000
            && after.HitObjects[5].IsLongNote && after.HitObjects[5].EndTime == 4000,
            string.Join(" / ", after.HitObjects.Select(n => $"{n.IsLongNote}:{n.StartTime}-{n.EndTime}")));
        check("保存往返：HitSound / Extras 保留（关键：没复制就会变成 Normal）",
            after.HitObjects[1].HitSound == 4 && after.HitObjects[1].Extras.SampleSet == 2
            && after.HitObjects[1].Extras.SampleFileName == "hi.wav",
            $"{after.HitObjects[1].HitSound}/{after.HitObjects[1].Extras.SampleSet}/{after.HitObjects[1].Extras.SampleFileName}");
        check("保存往返：新连击段 / 连击偏移保留",
            after.HitObjects[6].IsNewCombo && after.HitObjects[6].ComboOffset == 2,
            $"{after.HitObjects[6].IsNewCombo}/{after.HitObjects[6].ComboOffset}");
        check("保存往返：时间点数量不变", after.TimingPoints.Count == before.TimingPoints.Count,
            $"{before.TimingPoints.Count} → {after.TimingPoints.Count}");
        check("保存往返：重新解析出的星数与原来一致",
            Math.Abs(after.StarRating - before.StarRating) < 1e-6,
            $"{before.StarRating:R} vs {after.StarRating:R}");

        // 输出文件名：旧版规则 = 原目录 + {Artist} - {Title} ({Creator}) [{Difficulty}] + .osu
        var renamed = Reload(samplePath);
        renamed.Beatmap.Metadata.Creator = "新谱师";
        renamed.Beatmap.Metadata.Difficulty = "7K Test";
        string expectedName = "测试艺术家 - 测试歌曲 (新谱师) [7K Test].osu";
        check("命名：BuildOutputFileName 按旧版规则拼",
            renamed.BuildOutputFileName() == expectedName,
            $"{renamed.BuildOutputFileName()} != {expectedName}");
        check("命名：输出目录 = 原文件目录",
            renamed.BuildOutputPath() == Path.Combine(file.Directory, expectedName),
            renamed.BuildOutputPath());

        renamed.Beatmap.KeyCount = 7;
        string autoPath = renamed.Save();
        check("命名：Save() 写到原目录 + 元数据文件名（不是覆盖原文件）",
            autoPath == Path.Combine(file.Directory, expectedName) && File.Exists(autoPath)
            && File.Exists(samplePath),
            autoPath);
        var autoReloaded = Reload(autoPath);
        check("命名：自动命名的文件重新解析键数 = 7", autoReloaded.Beatmap.KeyCount == 7,
            autoReloaded.Beatmap.KeyCount.ToString());

        // 非法字符清洗：\\ / : * ? " < > | 全部去掉
        var nasty = Reload(samplePath);
        nasty.Beatmap.Metadata.Artist = "A/B:C*D?E\"F<G>H|I\\J";
        nasty.Beatmap.Metadata.Title = "T";
        nasty.Beatmap.Metadata.Creator = "C";
        nasty.Beatmap.Metadata.Difficulty = "D";
        string nastyName = nasty.BuildOutputFileName();
        check("命名：9 个 Windows 非法字符全部去掉",
            nastyName == "ABCDEFGHIJ - T (C) [D].osu" && !nastyName.Any(c => OsuBeatmapFile.ForbiddenChars.Contains(c)),
            nastyName);
    }

    // =====================================================================
    // 3. 时间点 / 列坐标
    // =====================================================================

    private static void CheckTimingAndKeyX(CheckFn check)
    {
        var bm = new OsuManiaBeatmap { KeyCount = 4 };
        bm.TimingPoints.Add(new OsuTimingPoint { Time = 0, BeatLength = 500 });
        bm.TimingPoints.Add(new OsuTimingPoint { Time = 12000, BeatLength = 400 });
        bm.TimingPoints.Add(new OsuTimingPoint { Time = 6000, BeatLength = -100, Inherited = true });

        check("时间点：0ms 用第一条红线（500）", Math.Abs(bm.BeatLengthAt(0) - 500) < 1e-9, bm.BeatLengthAt(0).ToString("R"));
        check("时间点：11999ms 仍是第一条红线", Math.Abs(bm.BeatLengthAt(11999) - 500) < 1e-9);
        check("时间点：12000ms 切到第二条红线", Math.Abs(bm.BeatLengthAt(12000) - 400) < 1e-9);
        check("时间点：超过最后一条红线时兜底用最后一条",
            Math.Abs(bm.BeatLengthAt(99999) - 400) < 1e-9);
        check("时间点：绿线（负 BeatLength）不会被当作红线", Math.Abs(bm.BeatLengthAt(6000) - 500) < 1e-9);
        check("时间点：TimingPointAt 返回的时间点 Time 正确", bm.TimingPointAt(12500).Time == 12000);

        // 只有一条红线 → 直接返回它
        var single = new OsuManiaBeatmap();
        single.TimingPoints.Add(new OsuTimingPoint { Time = 3000, BeatLength = 250 });
        check("时间点：只有一条红线时直接返回它", single.TimingPointAt(0).Time == 3000);

        // 空 TimingPoints → 抛带说明的异常（不静默返回错误值）
        var empty = new OsuManiaBeatmap();
        bool emptyThrew = false;
        try { _ = empty.TimingPointAt(0); } catch (InvalidOperationException) { emptyThrew = true; }
        check("时间点：空 TimingPoints 抛 InvalidOperationException", emptyThrew);

        // 只有绿线 → 旧版会下标越界；新版抛带说明的异常
        var greenOnly = new OsuManiaBeatmap();
        greenOnly.TimingPoints.Add(new OsuTimingPoint { Time = 0, BeatLength = -50, Inherited = true });
        string? greenMessage = null;
        try { _ = greenOnly.TimingPointAt(0); }
        catch (InvalidOperationException ex) { greenMessage = ex.Message; }
        check("时间点：只有绿线时抛带说明的异常", greenMessage is not null && greenMessage.Contains("红线"),
            greenMessage ?? "(未抛)");

        // 列坐标：3~10K 与旧版 KeyX 常量表逐列一致（表 = floor(512*(col+0.5)/keys)）
        var mismatches = new List<string>();
        for (int keys = 3; keys <= 10; keys++)
        {
            for (int column = 0; column < keys; column++)
            {
                int expected = (int)Math.Floor(512.0 * (column + 0.5) / keys);
                int actual = OsuManiaBeatmap.ColumnX(keys, column);
                if (expected != actual || actual != OsuManiaBeatmap.KeyX[keys][column])
                {
                    mismatches.Add($"{keys}K{column} exp={expected} act={actual}");
                }
            }
        }
        check("列坐标：3~10K 与旧版 KeyX 常量表完全一致", mismatches.Count == 0, string.Join(",", mismatches.Take(5)));

        check("列坐标：4K = 64/192/320/448",
            OsuManiaBeatmap.ColumnX(4, 0) == 64 && OsuManiaBeatmap.ColumnX(4, 1) == 192
            && OsuManiaBeatmap.ColumnX(4, 2) == 320 && OsuManiaBeatmap.ColumnX(4, 3) == 448);
        check("列坐标：7K = 36/109/182/256/329/402/475",
            Enumerable.Range(0, 7).Select(c => OsuManiaBeatmap.ColumnX(7, c))
                .SequenceEqual(new[] { 36, 109, 182, 256, 329, 402, 475 }));
        check("列坐标：1K / 2K 不再越界（旧版 bug 修复）",
            OsuManiaBeatmap.ColumnX(1, 0) == 256
            && OsuManiaBeatmap.ColumnX(2, 0) == 128 && OsuManiaBeatmap.ColumnX(2, 1) == 384);

        bool columnThrew = false;
        try { _ = OsuManiaBeatmap.ColumnX(4, 4); } catch (ArgumentOutOfRangeException) { columnThrew = true; }
        check("列坐标：越界列号抛 ArgumentOutOfRangeException", columnThrew);

        bool keysThrew = false;
        try { _ = OsuManiaBeatmap.ColumnX(11, 0); } catch (ArgumentOutOfRangeException) { keysThrew = true; }
        check("列坐标：不支持的键数抛 ArgumentOutOfRangeException", keysThrew);
        check("列坐标：IsSupportedKeyCount 边界", OsuManiaBeatmap.IsSupportedKeyCount(1)
            && OsuManiaBeatmap.IsSupportedKeyCount(10) && !OsuManiaBeatmap.IsSupportedKeyCount(0)
            && !OsuManiaBeatmap.IsSupportedKeyCount(11));
    }

    // =====================================================================
    // 4. 星数
    // =====================================================================

    private static void CheckStarRating(CheckFn check)
    {
        var map = new OsuManiaBeatmap { KeyCount = 4, OverallDifficulty = 8 };
        var rnd = new Random(20241024);
        int t = 1000;
        for (int i = 0; i < 200; i++)
        {
            t += rnd.Next(60, 260);
            int column = rnd.Next(4);
            bool ln = rnd.Next(100) < 25;
            map.HitObjects.Add(new ManiaNote(column, t, ln ? t + rnd.Next(150, 1200) : t));
        }

        double sr = StarRatingCalculator.CalculateStarRating(map.HitObjects, map.OverallDifficulty, map.KeyCount);
        check("星数：合成谱面 SR > 0 且有限", sr > 0 && double.IsFinite(sr), sr.ToString("R"));

        double srOdLow = StarRatingCalculator.CalculateStarRating(map.HitObjects, 4, 4);
        double srOdHigh = StarRatingCalculator.CalculateStarRating(map.HitObjects, 9.5, 4);
        check("星数：换 OD 会改变 SR（同谱不同 OD）", Math.Abs(srOdLow - srOdHigh) > 1e-6,
            $"OD4={srOdLow:R} OD9.5={srOdHigh:R}");

        double srRate15 = StarRatingCalculator.CalculateStarRating(map.HitObjects, 8, 4, 1.5);
        check("星数：rate=1.5 与 1.0 不同", Math.Abs(srRate15 - sr) > 1e-6, $"{sr:R} vs {srRate15:R}");

        check("星数：空谱面返回 0（旧版抛异常）",
            StarRatingCalculator.CalculateStarRating(new List<ManiaNote>(), 8, 4) == 0);
        check("星数：越界键数 / 异常输入不抛异常（返回 0）",
            StarRatingCalculator.CalculateStarRating(new List<ManiaNote> { new(0, 0, 0) }, 8, 0) == 0);

        var bm1K = new OsuManiaBeatmap { KeyCount = 1, OverallDifficulty = 5 };
        bm1K.HitObjects.Add(new ManiaNote(0, 1000, 1000));
        bm1K.HitObjects.Add(new ManiaNote(0, 1200, 1200));
        double sr1K = StarRatingCalculator.CalculateStarRating(bm1K.HitObjects, 5, 1);
        check("星数：1K 不越界且结果有限", double.IsFinite(sr1K) && sr1K >= 0, sr1K.ToString("R"));

        // 长条占比高时 SR 通常更高（方向性检查，不依赖具体数值）
        var noteOnly = new OsuManiaBeatmap { KeyCount = 4, OverallDifficulty = 8 };
        var lnHeavy = new OsuManiaBeatmap { KeyCount = 4, OverallDifficulty = 8 };
        int tt = 1000;
        var rnd2 = new Random(7);
        for (int i = 0; i < 150; i++)
        {
            tt += 120;
            noteOnly.HitObjects.Add(new ManiaNote(i % 4, tt, tt));
            lnHeavy.HitObjects.Add(new ManiaNote(i % 4, tt, tt + 260 + rnd2.Next(0, 300)));
        }
        double srNotes = StarRatingCalculator.CalculateStarRating(noteOnly.HitObjects, 8, 4);
        double srLns = StarRatingCalculator.CalculateStarRating(lnHeavy.HitObjects, 8, 4);
        check("星数：LN 多时 SR 与纯单点不同（方向合理）", double.IsFinite(srNotes) && double.IsFinite(srLns) && srLns != srNotes,
            $"notes={srNotes:R} lns={srLns:R}");
    }

    // =====================================================================
    // 5. 等级公式
    // =====================================================================

    private static void CheckLevel(CheckFn check)
    {
        static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 1e-9 * Math.Max(1.0, Math.Abs(expected));

        check("等级：sr=1.0 走低段 3.6198*sr", Near(LevelCalculator.CalculateLevel(1.0), 3.6198),
            LevelCalculator.CalculateLevel(1.0).ToString("R"));
        check("等级：sr=2.0 走低段 = 7.2396", Near(LevelCalculator.CalculateLevel(2.0), 7.2396));
        check("等级：sr=3.0 走拟合公式 = 10.512814781216521",
            Near(LevelCalculator.CalculateLevel(3.0), 10.512814781216521),
            LevelCalculator.CalculateLevel(3.0).ToString("R"));
        check("等级：sr=5.0 走拟合公式 = 13.837675326835969",
            Near(LevelCalculator.CalculateLevel(5.0), 13.837675326835969));
        check("等级：sr=8.0 走拟合公式 = 20.896777664818835",
            Near(LevelCalculator.CalculateLevel(8.0), 20.896777664818835));
        check("等级：下界 2.76257856739498 用拟合公式 = 10.175402248071727",
            Near(LevelCalculator.CalculateLevel(LevelCalculator.LowerBound), 10.175402248071727));
        check("等级：上界 10.5541834716376 用拟合公式 = 29.68196960257794",
            Near(LevelCalculator.CalculateLevel(LevelCalculator.UpperBound), 29.68196960257794));
        check("等级：sr=11.0 走上段 2.791*sr+0.5436 = 31.2446",
            Near(LevelCalculator.CalculateLevel(11.0), 31.244600000000002));
        check("等级：sr=12.0 走上段 = 34.0356", Near(LevelCalculator.CalculateLevel(12.0), 34.0356));

        check("等级：sr=0 返回 NaN", double.IsNaN(LevelCalculator.CalculateLevel(0)));
        check("等级：sr<0 返回 NaN", double.IsNaN(LevelCalculator.CalculateLevel(-1)));
        check("等级：sr=12.5 超出范围返回 NaN", double.IsNaN(LevelCalculator.CalculateLevel(12.5)));
        check("等级：NaN 输入返回 NaN", double.IsNaN(LevelCalculator.CalculateLevel(double.NaN)));
        check("等级：下界稍下方走低段",
            Near(LevelCalculator.CalculateLevel(Math.BitDecrement(LevelCalculator.LowerBound)),
                 3.6198 * Math.BitDecrement(LevelCalculator.LowerBound)));
        check("等级：上界稍上方走上段",
            Near(LevelCalculator.CalculateLevel(LevelCalculator.UpperBound + 0.5),
                 2.791 * (LevelCalculator.UpperBound + 0.5) + 0.5436));

        check("等级：NaN 显示为 '-'", LevelCalculator.FormatLevel(double.NaN) == "-");
        check("等级：正常值显示两位小数", LevelCalculator.FormatLevel(10.51281478) == "10.51",
            LevelCalculator.FormatLevel(10.51281478));

        var map = new OsuManiaBeatmap { StarRating = 3.0 };
        check("等级：可由谱面星数算等级", Near(LevelCalculator.CalculateLevel(map), 10.512814781216521));
    }

    // =====================================================================
    // 6. 转 K
    // =====================================================================

    private static void CheckKeyConverter(CheckFn check, string dir)
    {
        string samplePath = WriteText(dir, "kc-sample.osu", Sample4KText());

        // 4K → 7K 基本转换
        var file = Reload(samplePath);
        var sourceNotes = file.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime)).ToList();
        var result = KeyConverterEngine.Apply(file.Beatmap, new KeyConverterOptions
        {
            SeedText = "114514",
            TargetKeys = 7,
            Blank = 0,
            Clean = 4,
            Gap = 10,
        });
        check("转K：4K→7K 转换成功", result.Converted, result.SkipReason ?? "");
        check("转K：键数变为 7", file.Beatmap.KeyCount == 7);
        check("转K：所有对象列号在 0~6", file.Beatmap.HitObjects.Count > 0
            && file.Beatmap.HitObjects.All(n => n.Column is >= 0 and <= 6),
            string.Join(",", file.Beatmap.HitObjects.Select(n => n.Column).Distinct()));
        check("转K：所有对象 StartTime<=EndTime", file.Beatmap.HitObjects.All(n => n.StartTime <= n.EndTime));
        check("转K：难度名带 [4To7C] 前缀",
            file.Beatmap.Metadata.Difficulty.StartsWith("[4To7C]"),
            file.Beatmap.Metadata.Difficulty);
        check("转K：谱师追加 ' & KeyConverter'",
            file.Beatmap.Metadata.Creator.EndsWith(" & KeyConverter"),
            file.Beatmap.Metadata.Creator);
        check("转K：输出对象数 > 0", file.Beatmap.HitObjects.Count > 0, file.Beatmap.HitObjects.Count.ToString());
        check("转K：转换后能重新算星数（有限）",
            double.IsFinite(file.Beatmap.CalculateStarRating()) && file.Beatmap.CalculateStarRating() > 0,
            file.Beatmap.CalculateStarRating().ToString("R"));

        // 保存往返：转 K 后的谱面写到磁盘再读回，列 / 时间 / 键数仍正确
        string kcOut = Path.Combine(dir, "kc-out.osu");
        file.SaveTo(kcOut);
        var kcReloaded = Reload(kcOut);
        check("转K：保存往返后键数 / 对象数 / 列时间不变",
            kcReloaded.Beatmap.KeyCount == 7
            && kcReloaded.Beatmap.HitObjects.Count == file.Beatmap.HitObjects.Count
            && kcReloaded.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))
                .SequenceEqual(file.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))),
            $"{kcReloaded.Beatmap.KeyCount}/{kcReloaded.Beatmap.HitObjects.Count}");

        // 固定种子可复现
        var a = Reload(samplePath);
        var b = Reload(samplePath);
        var options = new KeyConverterOptions { SeedText = "114514", TargetKeys = 7, Gap = 10, Clean = 4 };
        KeyConverterEngine.Apply(a.Beatmap, options);
        KeyConverterEngine.Apply(b.Beatmap, options);
        check("转K：固定种子两次结果完全一致（列 + 时间）",
            a.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))
                .SequenceEqual(b.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))));

        // 不同种子一般应产生不同结果（用更大的谱面降低巧合概率）
        string bigPath = WriteText(dir, "kc-big.osu", SimpleMapText(4, 8, noteCount: 400));
        var c1 = Reload(bigPath);
        var c2 = Reload(bigPath);
        KeyConverterEngine.Apply(c1.Beatmap, new KeyConverterOptions { SeedText = "seed-A", TargetKeys = 7, Gap = 5 });
        KeyConverterEngine.Apply(c2.Beatmap, new KeyConverterOptions { SeedText = "seed-B", TargetKeys = 7, Gap = 5 });
        check("转K：不同种子得到不同结果",
            !c1.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime))
                .SequenceEqual(c2.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime))));

        // 目标键数 <= 源键数 → 拒绝并给原因
        var reject = Reload(samplePath);
        var rejectResult = KeyConverterEngine.Apply(reject.Beatmap, new KeyConverterOptions { TargetKeys = 4 });
        check("转K：目标键数<=源键数时拒绝并给原因",
            !rejectResult.Converted && rejectResult.SkipReason is not null
            && rejectResult.SkipReason.Contains("目标键数必须大于源键数"),
            rejectResult.SkipReason ?? "(无原因)");
        var rejectLower = Reload(samplePath);
        var rejectLowerResult = KeyConverterEngine.Apply(rejectLower.Beatmap, new KeyConverterOptions { TargetKeys = 3 });
        check("转K：高转低被拒绝（只支持低转高）", !rejectLowerResult.Converted && rejectLowerResult.SkipReason is not null);

        // CheckKeys 过滤
        var checkKeysHit = Reload(samplePath);
        var checkKeysHitResult = KeyConverterEngine.Apply(checkKeysHit.Beatmap, new KeyConverterOptions { TargetKeys = 7, CheckKeys = new[] { 4 } });
        check("转K：勾选源键数命中时转换", checkKeysHitResult.Converted, checkKeysHitResult.SkipReason ?? "");
        var checkKeysMiss = Reload(samplePath);
        var checkKeysMissResult = KeyConverterEngine.Apply(checkKeysMiss.Beatmap, new KeyConverterOptions { TargetKeys = 7, CheckKeys = new[] { 5, 7 } });
        check("转K：勾选源键数不匹配时跳过并给原因",
            !checkKeysMissResult.Converted && checkKeysMissResult.SkipReason is not null
            && checkKeysMissResult.SkipReason.Contains("勾选"),
            checkKeysMissResult.SkipReason ?? "(无原因)");

        // 源 10K（>9）拒绝
        var tenKeys = new OsuManiaBeatmap { KeyCount = 10, OverallDifficulty = 8 };
        tenKeys.TimingPoints.Add(new OsuTimingPoint { Time = 0, BeatLength = 500 });
        tenKeys.HitObjects.Add(new ManiaNote(0, 1000, 1000));
        var tenResult = KeyConverterEngine.Apply(tenKeys, new KeyConverterOptions { TargetKeys = 10 });
        check("转K：源 10K（>9）拒绝并给原因",
            !tenResult.Converted && tenResult.SkipReason is not null && tenResult.SkipReason.Contains("9K"),
            tenResult.SkipReason ?? "(无原因)");

        // Blank 夹取：4→7 时 blank=9 会被夹到 3，难度名应为 [4To7C3B]
        var blankFile = Reload(samplePath);
        KeyConverterEngine.Apply(blankFile.Beatmap, new KeyConverterOptions { TargetKeys = 7, Blank = 9 });
        check("转K：Blank 超过目标-源时被夹到 3，难度名 [4To7C3B]",
            blankFile.Beatmap.Metadata.Difficulty.StartsWith("[4To7C3B]"),
            blankFile.Beatmap.Metadata.Difficulty);

        // Creator 处理
        var creatorFile = Reload(samplePath);
        KeyConverterEngine.Apply(creatorFile.Beatmap, new KeyConverterOptions { TargetKeys = 7, CreatorIsChecked = true, Creator = "新谱师名" });
        check("转K：勾选 Creator 且非空时替换谱师", creatorFile.Beatmap.Metadata.Creator == "新谱师名",
            creatorFile.Beatmap.Metadata.Creator);
        var creatorEmpty = Reload(samplePath);
        KeyConverterEngine.Apply(creatorEmpty.Beatmap, new KeyConverterOptions { TargetKeys = 7, CreatorIsChecked = true, Creator = "" });
        check("转K：勾选 Creator 但为空时仍追加后缀",
            creatorEmpty.Beatmap.Metadata.Creator.EndsWith(" & KeyConverter"),
            creatorEmpty.Beatmap.Metadata.Creator);
        var creatorSpace = Reload(samplePath);
        KeyConverterEngine.Apply(creatorSpace.Beatmap, new KeyConverterOptions { TargetKeys = 7, CreatorIsChecked = true, Creator = "   " });
        check("转K：Creator 只填空格时按旧版 IsNullOrEmpty 语义原样替换",
            creatorSpace.Beatmap.Metadata.Creator == "   ",
            $"'{creatorSpace.Beatmap.Metadata.Creator}'");

        // HitSound / Extras 保留（旧版 bug 修复）
        var hsFile = Reload(samplePath);
        foreach (var note in hsFile.Beatmap.HitObjects)
        {
            note.HitSound = 7;
            note.Extras.SampleSet = 2;
            note.Extras.AdditionSet = 3;
            note.Extras.CustomIndex = 5;
            note.Extras.Volume = 60;
            note.Extras.SampleFileName = "hit.wav";
        }
        KeyConverterEngine.Apply(hsFile.Beatmap, new KeyConverterOptions { TargetKeys = 7 });
        check("转K：转换后 HitSound / Extras 保留（旧版会全丢）",
            hsFile.Beatmap.HitObjects.Count > 0 && hsFile.Beatmap.HitObjects.All(n =>
                n.HitSound == 7 && n.Extras.SampleSet == 2 && n.Extras.AdditionSet == 3
                && n.Extras.CustomIndex == 5 && n.Extras.Volume == 60 && n.Extras.SampleFileName == "hit.wav"),
            string.Join(",", hsFile.Beatmap.HitObjects.Take(3).Select(n => $"{n.HitSound}/{n.Extras.SampleSet}/{n.Extras.SampleFileName}")));

        // 旧版会死循环的输入：目标键数 > 2 × 源键数（2K→4K / 4K→9K 等），新版必须能跑完
        string twoKPath = WriteText(dir, "kc-2k.osu", SimpleMapText(2, 8, noteCount: 60));
        var twoK = Reload(twoKPath);
        var twoKResult = KeyConverterEngine.Apply(twoK.Beatmap, new KeyConverterOptions { TargetKeys = 4, Gap = 5 });
        check("转K：2K→4K 能跑完（旧版此处死循环，已修）",
            twoKResult.Converted && twoK.Beatmap.KeyCount == 4
            && twoK.Beatmap.HitObjects.All(n => n.Column is >= 0 and <= 3),
            twoKResult.SkipReason ?? "");
        string fourToNinePath = WriteText(dir, "kc-4to9.osu", SimpleMapText(4, 8, noteCount: 120));
        var fourToNine = Reload(fourToNinePath);
        var fourToNineResult = KeyConverterEngine.Apply(fourToNine.Beatmap, new KeyConverterOptions { TargetKeys = 9, Gap = 10 });
        check("转K：4K→9K 能跑完（旧版此处死循环，已修）",
            fourToNineResult.Converted && fourToNine.Beatmap.HitObjects.All(n => n.Column is >= 0 and <= 8),
            fourToNineResult.SkipReason ?? "");

        // Clean=0：旧版可达行为就是“不去子弹”，转换仍成功
        var clean0 = Reload(bigPath);
        var clean0Result = KeyConverterEngine.Apply(clean0.Beatmap, new KeyConverterOptions { TargetKeys = 7, Clean = 0, Gap = 10 });
        check("转K：Clean=0 不做去子弹但转换成功", clean0Result.Converted && clean0.Beatmap.HitObjects.Count > 0,
            clean0Result.SkipReason ?? "");

        // Gap=0：整张谱面一次处理
        var gap0 = Reload(bigPath);
        var gap0Result = KeyConverterEngine.Apply(gap0.Beatmap, new KeyConverterOptions { TargetKeys = 7, Gap = 0 });
        check("转K：Gap=0（整张一次处理）成功", gap0Result.Converted, gap0Result.SkipReason ?? "");

        // 空谱面：转换成功、键数改变、对象仍为空（旧版也是这样写文件）
        var emptyMap = new OsuManiaBeatmap { KeyCount = 4, OverallDifficulty = 8 };
        emptyMap.TimingPoints.Add(new OsuTimingPoint { Time = 0, BeatLength = 500 });
        var emptyResult = KeyConverterEngine.Apply(emptyMap, new KeyConverterOptions { TargetKeys = 7 });
        check("转K：空谱面也能转换（对象数保持 0）",
            emptyResult.Converted && emptyMap.KeyCount == 7 && emptyMap.HitObjects.Count == 0,
            emptyResult.SkipReason ?? "");
    }

    // =====================================================================
    // 7. 转 LN
    // =====================================================================

    private static void CheckLnTransformer(CheckFn check, string dir)
    {
        string samplePath = WriteText(dir, "ln-sample.osu", Sample4KText());
        string bigPath = WriteText(dir, "ln-big.osu", SimpleMapText(4, 8, noteCount: 300));

        // Level 0 / 3 / 10 各跑一次
        foreach (int level in new[] { 0, 3, 10 })
        {
            var file = Reload(bigPath);
            var beforeKeyCount = file.Beatmap.KeyCount;
            var result = LnTransformerEngine.Apply(file.Beatmap, new LnTransformerOptions
            {
                SeedText = "114514",
                Level = level,
                Percentage = 100,
                Divide = 4,
                Column = 10,
                Gap = 12,
                FixError = true,
                Ignore = false,
                KeepOverallDifficulty = true,
            });
            check($"转LN：Level {level} 转换成功", result.Converted, result.SkipReason ?? "");
            check($"转LN：Level {level} 后所有 StartTime<=EndTime",
                file.Beatmap.HitObjects.All(n => n.StartTime <= n.EndTime));
            check($"转LN：Level {level} 键数不变", file.Beatmap.KeyCount == beforeKeyCount);
            check($"转LN：Level {level} 难度名带 [LN-Lv{level}] 前缀",
                file.Beatmap.Metadata.Difficulty.StartsWith($"[LN-Lv{level}"),
                file.Beatmap.Metadata.Difficulty);
            check($"转LN：Level {level} 谱师追加 ' & LNTransformer'",
                file.Beatmap.Metadata.Creator.EndsWith(" & LNTransformer"),
                file.Beatmap.Metadata.Creator);
            check($"转LN：Level {level} 输出对象数 > 0", file.Beatmap.HitObjects.Count > 0,
                file.Beatmap.HitObjects.Count.ToString());
        }

        // Level -3：Hold off（所有长条压成普通键，数量不变）
        var holdOff = Reload(samplePath);
        int sourceCount = holdOff.Beatmap.HitObjects.Count;
        var holdOffResult = LnTransformerEngine.Apply(holdOff.Beatmap, new LnTransformerOptions
        {
            SeedText = "114514",
            Level = -3,
            Percentage = 100,
            Column = 10,
            Gap = 12,
            Ignore = false,
            FixError = true,
        });
        check("转LN：Level -3（Hold off）转换成功", holdOffResult.Converted, holdOffResult.SkipReason ?? "");
        check("转LN：Level -3 后没有长条（全部压成普通键）",
            holdOff.Beatmap.HitObjects.All(n => n.StartTime == n.EndTime),
            string.Join(",", holdOff.Beatmap.HitObjects.Where(n => n.StartTime != n.EndTime).Select(n => $"{n.StartTime}-{n.EndTime}")));
        check("转LN：Level -3 对象数不变", holdOff.Beatmap.HitObjects.Count == sourceCount,
            $"{sourceCount} → {holdOff.Beatmap.HitObjects.Count}");

        // Percentage 方向：100% 的长条数 >= 50%
        var pct100 = Reload(bigPath);
        var pct50 = Reload(bigPath);
        LnTransformerEngine.Apply(pct100.Beatmap, new LnTransformerOptions { Level = 3, Percentage = 100, Ignore = false });
        LnTransformerEngine.Apply(pct50.Beatmap, new LnTransformerOptions { Level = 3, Percentage = 50, Ignore = false });
        int ln100 = pct100.Beatmap.HitObjects.Count(n => n.StartTime != n.EndTime);
        int ln50 = pct50.Beatmap.HitObjects.Count(n => n.StartTime != n.EndTime);
        check("转LN：LN 占比随 Percentage 增大（100% >= 50%）", ln100 >= ln50,
            $"100%={ln100} 50%={ln50}");

        // Level 方向：Level 5 的 LN 平均时长应大于 Level 0（mu = 1 vs 55）
        var lv0 = Reload(bigPath);
        var lv5 = Reload(bigPath);
        LnTransformerEngine.Apply(lv0.Beatmap, new LnTransformerOptions { Level = 0, Percentage = 100, Ignore = false, FixError = false });
        LnTransformerEngine.Apply(lv5.Beatmap, new LnTransformerOptions { Level = 5, Percentage = 100, Ignore = false, FixError = false });
        double avg0 = lv0.Beatmap.HitObjects.Where(n => n.StartTime != n.EndTime).Select(n => (double)(n.EndTime - n.StartTime)).DefaultIfEmpty(0).Average();
        double avg5 = lv5.Beatmap.HitObjects.Where(n => n.StartTime != n.EndTime).Select(n => (double)(n.EndTime - n.StartTime)).DefaultIfEmpty(0).Average();
        check("转LN：LN 平均时长随 Level 增大（Lv5 > Lv0）", avg5 > avg0,
            $"Lv0={avg0:F1}ms Lv5={avg5:F1}ms");

        // 固定种子可复现
        var seedA = Reload(bigPath);
        var seedB = Reload(bigPath);
        var seedOptions = new LnTransformerOptions { SeedText = "114514", Level = 3, Percentage = 100, Ignore = false, FixError = true };
        LnTransformerEngine.Apply(seedA.Beatmap, seedOptions);
        LnTransformerEngine.Apply(seedB.Beatmap, seedOptions);
        check("转LN：固定种子两次结果完全一致",
            seedA.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))
                .SequenceEqual(seedB.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))));

        // Ignore：已转换过的谱面再次转换会被拒绝
        var ignored = Reload(bigPath);
        LnTransformerEngine.Apply(ignored.Beatmap, new LnTransformerOptions { Level = 3, Ignore = false });
        check("转LN：转换后 IsConverted 为 true", LnTransformerEngine.IsConverted(ignored.Beatmap));
        var ignoredAgain = LnTransformerEngine.Apply(ignored.Beatmap, new LnTransformerOptions { Level = 3, Ignore = true });
        check("转LN：Ignore 勾选时跳过已转换谱面并给原因",
            !ignoredAgain.Converted && ignoredAgain.SkipReason is not null
            && ignoredAgain.SkipReason.Contains("已经转换过"),
            ignoredAgain.SkipReason ?? "(无原因)");
        var notIgnored = LnTransformerEngine.Apply(ignored.Beatmap, new LnTransformerOptions { Level = 3, Ignore = false });
        check("转LN：Ignore 不勾选时允许再次转换", notIgnored.Converted, notIgnored.SkipReason ?? "");

        // Ignore 也会识别难度名里的 [LN...] 标记（不只看 Creator）
        var nameMarked = Reload(bigPath);
        nameMarked.Beatmap.Metadata.Difficulty = "[LN-Lv3]原来的难度名";
        check("转LN：IsConverted 识别难度名里的 [LN...] 标记", LnTransformerEngine.IsConverted(nameMarked.Beatmap));

        // CheckKeys 过滤
        var keyMiss = Reload(bigPath);
        var keyMissResult = LnTransformerEngine.Apply(keyMiss.Beatmap, new LnTransformerOptions { Level = 3, CheckKeys = new[] { 7 } });
        check("转LN：勾选键数不匹配时跳过并给原因",
            !keyMissResult.Converted && keyMissResult.SkipReason is not null && keyMissResult.SkipReason.Contains("勾选"),
            keyMissResult.SkipReason ?? "(无原因)");

        // OD：默认保留原谱（KeepOverallDifficulty = true）
        var odKeep = Reload(bigPath);
        LnTransformerEngine.Apply(odKeep.Beatmap, new LnTransformerOptions
        {
            Level = 3,
            Ignore = false,
            KeepOverallDifficulty = true,
            OverallDifficulty = 0,
        });
        check("转LN：默认保留原谱 OD（修复旧版把 OD 写成 0 的问题）",
            Math.Abs(odKeep.Beatmap.OverallDifficulty - 8) < 1e-6,
            odKeep.Beatmap.OverallDifficulty.ToString("R"));

        var odWrite = Reload(bigPath);
        LnTransformerEngine.Apply(odWrite.Beatmap, new LnTransformerOptions
        {
            Level = 3,
            Ignore = false,
            KeepOverallDifficulty = false,
            OverallDifficulty = 9.5,
        });
        check("转LN：关掉 KeepOverallDifficulty 时写入指定 OD",
            Math.Abs(odWrite.Beatmap.OverallDifficulty - 9.5) < 1e-6,
            odWrite.Beatmap.OverallDifficulty.ToString("R"));

        // OriginalLN：勾选后源谱面所有 LN 都保留（旧版只保留最后一列，已修）
        var originalLn = Reload(samplePath);
        var sourceLns = originalLn.Beatmap.HitObjects
            .Where(n => n.StartTime != n.EndTime)
            .Select(n => (n.Column, n.StartTime, n.EndTime))
            .ToList();
        var originalLnResult = LnTransformerEngine.Apply(originalLn.Beatmap, new LnTransformerOptions
        {
            Level = 3,
            Percentage = 100,
            Column = 1,
            Gap = 0,
            Ignore = false,
            OriginalLN = true,
            FixError = true,
        });
        bool allSourceLnsKept = sourceLns.Count > 0 && sourceLns.All(ln => originalLn.Beatmap.HitObjects.Any(n =>
            n.Column == ln.Column && n.StartTime == ln.StartTime && n.EndTime == ln.EndTime && n.IsLongNote));
        check("转LN：OriginalLN 勾选时保留源谱面全部 LN（旧版漏列 bug 已修）",
            originalLnResult.Converted && allSourceLnsKept,
            $"{sourceLns.Count} 条源 LN，保留 {originalLn.Beatmap.HitObjects.Count(n => n.StartTime != n.EndTime)} 条");

        // FixError：Level 10（反相）时开着 FixError 的尾判更贴近节拍网格
        var fixOn = Reload(bigPath);
        var fixOff = Reload(bigPath);
        LnTransformerEngine.Apply(fixOn.Beatmap, new LnTransformerOptions { Level = 10, Ignore = false, FixError = true });
        LnTransformerEngine.Apply(fixOff.Beatmap, new LnTransformerOptions { Level = 10, Ignore = false, FixError = false });
        int alignedOn = CountBeatAligned(fixOn.Beatmap, fixOn.Beatmap.HitObjects);
        int alignedOff = CountBeatAligned(fixOff.Beatmap, fixOff.Beatmap.HitObjects);
        int lnOn = fixOn.Beatmap.HitObjects.Count(n => n.StartTime != n.EndTime);
        check("转LN：FixError 让更多 LN 尾判落在 1/BeatLength 网格附近（Level 10）",
            lnOn > 0 && alignedOn >= alignedOff && alignedOn > 0,
            $"FixError开={alignedOn}/{lnOn} 关={alignedOff}");

        // 空 TimingPoints：不应闪退，转成“跳过 + 原因”
        var noTiming = Reload(samplePath);
        noTiming.Beatmap.TimingPoints.Clear();
        var noTimingResult = LnTransformerEngine.Apply(noTiming.Beatmap, new LnTransformerOptions { Level = 3, Ignore = false });
        check("转LN：缺时间点时返回跳过（不闪退）",
            !noTimingResult.Converted && noTimingResult.SkipReason is not null
            && noTimingResult.SkipReason.Contains("转换失败"),
            noTimingResult.SkipReason ?? "(未返回原因)");

        // 保存往返：转 LN 后写到磁盘再读回，列 / 时间 / 键数仍正确
        var rt = Reload(bigPath);
        LnTransformerEngine.Apply(rt.Beatmap, new LnTransformerOptions { Level = 3, Ignore = false });
        string rtPath = Path.Combine(dir, "ln-roundtrip.osu");
        rt.SaveTo(rtPath);
        var rtReloaded = Reload(rtPath);
        check("转LN：保存往返后键数 / 对象数 / 列时间不变",
            rtReloaded.Beatmap.KeyCount == rt.Beatmap.KeyCount
            && rtReloaded.Beatmap.HitObjects.Count == rt.Beatmap.HitObjects.Count
            && rtReloaded.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))
                .SequenceEqual(rt.Beatmap.HitObjects.Select(n => (n.Column, n.StartTime, n.EndTime))),
            $"{rtReloaded.Beatmap.HitObjects.Count} vs {rt.Beatmap.HitObjects.Count}");
    }

    /// <summary>统计尾判落在 “时间点.Time + k * BeatLength/等分数” 网格 2.5ms 内的 LN 数量（FixError 的判定）。</summary>
    private static int CountBeatAligned(OsuManiaBeatmap beatmap, IReadOnlyList<ManiaNote> notes)
    {
        int[] divides = { 2, 4, 8, 3, 6, 9, 5, 7, 12, 16, 48, 35, 64 };
        int aligned = 0;
        foreach (var note in notes)
        {
            if (note.StartTime == note.EndTime)
            {
                continue;
            }

            OsuTimingPoint point;
            try
            {
                point = beatmap.TimingPointAt(note.EndTime);
            }
            catch
            {
                continue;
            }

            double relative = note.EndTime - point.Time;
            bool ok = false;
            foreach (int divide in divides)
            {
                double grid = point.BeatLength / divide;
                if (grid <= 0)
                {
                    continue;
                }
                double remainder = relative % grid;
                double distance = Math.Min(Math.Abs(remainder), Math.Abs(grid - remainder));
                if (distance <= 2.5)
                {
                    ok = true;
                    break;
                }
            }

            if (ok)
            {
                aligned++;
            }
        }

        return aligned;
    }

    // =====================================================================
    // 8. 批量入口
    // =====================================================================

    private sealed class SyncProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = new();

        public void Report(T value) => Items.Add(value);
    }

    private static void CheckBatch(CheckFn check, string dir)
    {
        string batchDir = Path.Combine(dir, "batch-" + Guid.NewGuid().ToString("N")[..8]);
        string subDir = Path.Combine(batchDir, "sub");
        Directory.CreateDirectory(subDir);
        WriteText(batchDir, "a.osu", SimpleMapText(4, 8, noteCount: 40));
        WriteText(batchDir, "b.OSU", SimpleMapText(3, 7, noteCount: 40));
        WriteText(batchDir, "notes.txt", "不是谱面");
        WriteText(subDir, "c.osu", SimpleMapText(5, 8, noteCount: 40));
        string single = Path.Combine(batchDir, "single.osu");
        WriteText(batchDir, "single.osu", SimpleMapText(4, 8, noteCount: 40));

        var collected = OsuFileBatch.CollectOsuFiles(new[] { batchDir });
        check("批量：递归收集 .osu（大小写不敏感，忽略非谱面）",
            collected.Count == 4 && collected.Any(p => p.EndsWith("b.OSU")) && !collected.Any(p => p.EndsWith(".txt")),
            $"{collected.Count} 个");

        var dedup = OsuFileBatch.CollectOsuFiles(new[] { batchDir, single });
        check("批量：文件 + 文件夹重复传入会去重", dedup.Count == collected.Count, $"{dedup.Count} vs {collected.Count}");
        check("批量：不存在的路径被忽略", OsuFileBatch.CollectOsuFiles(new[] { Path.Combine(batchDir, "nope") }).Count == 0);
        check("批量：IsOsuFile 大小写不敏感",
            OsuFileBatch.IsOsuFile("a.osu") && OsuFileBatch.IsOsuFile("a.OSU") && !OsuFileBatch.IsOsuFile("a.txt"));

        // 单文件转 K + 结果带输出路径
        var singleItem = OsuFileBatch.ConvertOne(single, new KeyConverterOptions { TargetKeys = 7 });
        check("批量：单文件转 K 成功且带输出路径",
            singleItem.Status == OsuBatchItemStatus.Converted && singleItem.OutputPath is not null
            && File.Exists(singleItem.OutputPath),
            $"{singleItem.Status}/{singleItem.Message}/{singleItem.OutputPath}");
        check("批量：单文件转 K 输出文件名按元数据规则（Difficulty 已带 [4To7C] 前缀）",
            singleItem.OutputPath is not null
            && Path.GetFileName(singleItem.OutputPath) == "测试艺术家 - 批量测试 (测试谱师 & KeyConverter) [[4To7C]Test].osu",
            Path.GetFileName(singleItem.OutputPath ?? ""));

        // 单文件转 LN + 结果带输出路径
        var lnItem = OsuFileBatch.ConvertOne(Path.Combine(batchDir, "a.osu"), new LnTransformerOptions { Level = 3 });
        check("批量：单文件转 LN 成功且带输出路径",
            lnItem.Status == OsuBatchItemStatus.Converted && lnItem.OutputPath is not null && File.Exists(lnItem.OutputPath),
            $"{lnItem.Status}/{lnItem.Message}");

        // 非 mania 文件 → Failed（不中断整批）
        WriteText(batchDir, "d-standard.osu", SimpleMapText(4, 8, mode: "0", noteCount: 20));
        var badItem = OsuFileBatch.ConvertOne(Path.Combine(batchDir, "d-standard.osu"), new KeyConverterOptions { TargetKeys = 7 });
        check("批量：非 mania 文件计为失败并带原因",
            badItem.Status == OsuBatchItemStatus.Failed && badItem.Message is not null && badItem.Message.Contains("mania"),
            badItem.Message ?? "");

        // 整批转 K：进度回调 + 汇总（每次批量用独立目录：转换输出也写在源目录里，
        // 复用目录会让下一轮把上一轮的输出也当成输入收进来）
        string NewRunDir(string prefix)
        {
            string d = Path.Combine(dir, prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(d);
            WriteText(d, "1.osu", SimpleMapText(4, 8, noteCount: 60));
            WriteText(d, "2.osu", SimpleMapText(3, 7, noteCount: 60));
            WriteText(d, "3.osu", SimpleMapText(4, 5, mode: "0", noteCount: 10));
            return d;
        }

        string runDir = NewRunDir("batch-run");
        var progress = new SyncProgress<OsuBatchProgress>();
        var batchResult = OsuFileBatch.RunKeyConverter(new[] { runDir }, new KeyConverterOptions { TargetKeys = 7 }, progress);
        check("批量：整批统计（成功 2 / 失败 1 / 跳过 0）",
            batchResult.Total == 3 && batchResult.ConvertedCount == 2 && batchResult.FailedCount == 1
            && batchResult.SkippedCount == 0,
            batchResult.SummaryText);
        check("批量：进度回调次数 = 文件数", progress.Items.Count == batchResult.Total,
            $"{progress.Items.Count}/{batchResult.Total}");
        check("批量：汇总文案可直接显示", batchResult.SummaryText.Contains("共 3 个"),
            batchResult.SummaryText);
        check("批量：成功的两个文件都写出了输出文件",
            batchResult.Items.Where(i => i.Status == OsuBatchItemStatus.Converted).All(i => i.OutputPath is not null && File.Exists(i.OutputPath)));

        // 整批转 LN
        var lnBatch = OsuFileBatch.RunLnTransformer(new[] { NewRunDir("batch-ln") }, new LnTransformerOptions { Level = 3 });
        check("批量：整批转 LN 成功数 >= 2", lnBatch.ConvertedCount >= 2, lnBatch.SummaryText);

        // 取消：剩余文件计为跳过
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = OsuFileBatch.RunKeyConverter(new[] { NewRunDir("batch-cancel") },
            new KeyConverterOptions { TargetKeys = 7 }, null, cts.Token);
        check("批量：取消后不抛异常且全部计为跳过",
            cancelled.Total == 3 && cancelled.SkippedCount == 3 && cancelled.ConvertedCount == 0,
            cancelled.SummaryText);
    }

    // =====================================================================
    // 9. OsuParsers 解码 / 编码的线程安全（静态状态，必须串行）
    // =====================================================================

    private static void CheckThreadSafety(CheckFn check, string dir)
    {
        // 造几个不同的谱面文件
        var paths = new List<string>();
        for (int i = 0; i < 4; i++)
        {
            paths.Add(WriteText(dir, $"thread-{i}.osu", SimpleMapText(4 + i % 3, 8, noteCount: 50 + i * 10)));
        }

        // 并行加载：桥内部有锁，必须 0 失败（OsuParsers 解码器本身是 static 状态、非线程安全）
        int failures = 0;
        int wrongKeys = 0;
        var tasks = new List<Task>();
        for (int round = 0; round < 8; round++)
        {
            foreach (var path in paths)
            {
                tasks.Add(Task.Run(() =>
                {
                    if (!OsuBeatmapFile.TryLoad(path, out var file, out _) || file is null)
                    {
                        Interlocked.Increment(ref failures);
                        return;
                    }
                    // 校验没串台：文件名 thread-N 与键数一致（4 + N%3）
                    int expectedKeys = 4 + int.Parse(Path.GetFileNameWithoutExtension(path).Split('-')[1]) % 3;
                    if (file.Beatmap.KeyCount != expectedKeys)
                    {
                        Interlocked.Increment(ref wrongKeys);
                    }
                }));
            }
        }
        Task.WaitAll(tasks.ToArray());
        check("线程安全：8 轮 × 4 文件并行加载 0 失败", failures == 0, $"失败 {failures} 次");
        check("线程安全：并行加载没有串台（键数与文件一致）", wrongKeys == 0, $"串台 {wrongKeys} 次");

        // 并行保存：编码器同样是 static 状态，桥内部串行
        int saveFailures = 0;
        var saveTasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            int index = i;
            saveTasks.Add(Task.Run(() =>
            {
                try
                {
                    if (!OsuBeatmapFile.TryLoad(paths[index % paths.Count], out var file, out _))
                    {
                        Interlocked.Increment(ref saveFailures);
                        return;
                    }
                    file!.SaveTo(Path.Combine(dir, $"thread-save-{index}.osu"));
                }
                catch
                {
                    Interlocked.Increment(ref saveFailures);
                }
            }));
        }
        Task.WaitAll(saveTasks.ToArray());
        check("线程安全：并行保存 8 个文件 0 失败", saveFailures == 0, $"失败 {saveFailures} 次");
        check("线程安全：并行保存的文件都能重新加载",
            Enumerable.Range(0, 8).All(i => OsuBeatmapFile.TryLoad(Path.Combine(dir, $"thread-save-{i}.osu"), out _, out _)));
    }
}
