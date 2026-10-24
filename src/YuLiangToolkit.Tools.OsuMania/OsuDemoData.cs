using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.OsuMania;

/// <summary>
/// 开发调试用的「合成谱面」生成器（只在 <c>YLT_OSU_DEMO=1</c> 时被三个 osu 工具调用）。
///
/// <para>目的：无头截图 / UI 冒烟时需要界面上“有内容”，但项目约定 **不发网络请求、不读用户目录**，
/// 所以在自己的数据目录 <c>Data\&lt;工具Id&gt;\demo\</c> 下现写几张最小的 v14 mania 谱面再加载。
/// 不设这个环境变量时本类完全不会被用到，也不产生任何文件。</para>
///
/// <para>谱面是按 osu! 文件格式手拼的：4K（无 LN，适合看 Key 转换效果）、7K（含 LN，适合看 LN 转换 /
/// 保留原 LN）、以及一张故意不是 mania 的谱面（给等级计算器演示“加载失败”的红色行）。</para>
/// </summary>
internal static class OsuDemoData
{
    /// <summary>是否打开了 demo 数据开关（<c>YLT_OSU_DEMO=1</c>，值随意）。</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("YLT_OSU_DEMO") is not null;

    /// <summary>
    /// 是否在 demo 数据塞好之后**再自动跑一遍转换**（<c>YLT_OSU_AUTORUN=1</c>，配合 <c>YLT_OSU_DEMO=1</c>）。
    /// 只给无头截图 / 冒烟看日志、进度和行状态用；正常使用不会触发。
    /// </summary>
    public static bool AutoRun => Environment.GetEnvironmentVariable("YLT_OSU_AUTORUN") is not null;

    /// <summary>4K 示例谱面文件名（无 LN）。</summary>
    public const string Demo4KName = "demo-4k.osu";

    /// <summary>7K 示例谱面文件名（含 LN）。</summary>
    public const string Demo7KName = "demo-7k-ln.osu";

    /// <summary>故意非 mania 的示例谱面文件名（给等级计算器演示加载失败）。</summary>
    public const string DemoInvalidName = "demo-not-mania.osu";

    /// <summary>
    /// 在 <paramref name="storage"/> 的 <c>demo\</c> 子目录里生成示例谱面（已存在就覆盖重写），返回完整路径。
    /// 写失败的文件不会出现在返回列表里。
    /// </summary>
    /// <param name="storage">工具自己的数据目录（<c>Data\&lt;工具Id&gt;\</c>）。</param>
    /// <param name="includeInvalid">是否连那张“非 mania”的失败示例也生成。</param>
    public static IReadOnlyList<string> EnsureDemoBeatmaps(ToolStorage storage, bool includeInvalid = false)
    {
        var paths = new List<string>(3);

        Add(storage, paths, "demo", Demo4KName, BuildBeatmap(4, "Demo Song", "4K Demo", 8.0, withLongNotes: false));
        Add(storage, paths, "demo", Demo7KName, BuildBeatmap(7, "Demo LN Song", "7K LN Demo", 7.5, withLongNotes: true));
        if (includeInvalid)
        {
            Add(storage, paths, "demo", DemoInvalidName, BuildNotManiaBeatmap());
        }

        return paths;
    }

    private static void Add(ToolStorage storage, List<string> paths, string folder, string fileName, string content)
    {
        string? written = storage.WriteText(Path.Combine(folder, fileName), content);
        if (!string.IsNullOrEmpty(written))
        {
            paths.Add(written);
        }
    }

    /// <summary>第 <paramref name="column"/> 列在 512 宽画布上的 x（与 OsuManiaBeatmap.ColumnX 同一公式）。</summary>
    private static int ColumnX(int keys, int column) => (int)Math.Floor(512.0 * (column + 0.5) / keys);

    /// <summary>拼一张最小的 v14 mania 谱面：单红线 + 若干普通键（可带 LN）。</summary>
    private static string BuildBeatmap(int keys, string title, string difficulty, double od, bool withLongNotes)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("osu file format v14");
        sb.AppendLine();
        sb.AppendLine("[General]");
        sb.AppendLine("AudioFilename: demo-audio.mp3");
        sb.AppendLine("AudioLeadIn: 0");
        sb.AppendLine("PreviewTime: -1");
        sb.AppendLine("Countdown: 0");
        sb.AppendLine("SampleSet: Soft");
        sb.AppendLine("StackLeniency: 0.7");
        sb.AppendLine("Mode: 3");
        sb.AppendLine("LetterboxInBreaks: 0");
        sb.AppendLine("SpecialStyle: 0");
        sb.AppendLine("WidescreenStoryboard: 0");
        sb.AppendLine();
        sb.AppendLine("[Editor]");
        sb.AppendLine("DistanceSpacing: 1");
        sb.AppendLine("BeatDivisor: 4");
        sb.AppendLine("GridSize: 4");
        sb.AppendLine("TimelineZoom: 1");
        sb.AppendLine();
        sb.AppendLine("[Metadata]");
        sb.AppendLine("Title:" + title);
        sb.AppendLine("TitleUnicode:" + title);
        sb.AppendLine("Artist:Demo Artist");
        sb.AppendLine("ArtistUnicode:Demo Artist");
        sb.AppendLine("Creator:demo-mapper");
        sb.AppendLine("Version:" + difficulty);
        sb.AppendLine("Source:");
        sb.AppendLine("Tags:demo osu mania yuliangtoolkit");
        sb.AppendLine("BeatmapID:0");
        sb.AppendLine("BeatmapSetID:-1");
        sb.AppendLine();
        sb.AppendLine("[Difficulty]");
        sb.AppendLine("HPDrainRate:7");
        sb.AppendLine("CircleSize:" + keys);
        sb.AppendLine("OverallDifficulty:" + od.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        sb.AppendLine("ApproachRate:5");
        sb.AppendLine("SliderMultiplier:1.4");
        sb.AppendLine("SliderTickRate:1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("//Background and Video events");
        sb.AppendLine("//Break Periods");
        sb.AppendLine("//Storyboard Layer 0 (Background)");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        sb.AppendLine("0,500,4,2,1,60,1,0");
        sb.AppendLine("4000,-100,4,2,1,60,0,0");
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");

        // 64 个打击物：2 个一组（每 250ms），列按顺序轮换，偶尔跳一列让谱面别太规整；
        // withLongNotes 时每 11 个里挑一个做 300ms 的 LN。
        int time = 1000;
        for (int i = 0; i < 64; i++)
        {
            int column = i % keys;
            if (i % 8 == 7)
            {
                column = (column + 2) % keys;
            }

            bool isLongNote = withLongNotes && i % 11 == 5;
            int endTime = isLongNote ? time + 300 : time;
            int x = ColumnX(keys, column);

            if (isLongNote)
            {
                sb.AppendLine($"{x},192,{time},128,0,{endTime}:0:0:0:0:");
            }
            else
            {
                sb.AppendLine($"{x},192,{time},1,0,0:0:0:0:");
            }

            if (i % 2 == 1)
            {
                time += 250;
            }
        }

        return sb.ToString();
    }

    /// <summary>一张 Mode=0（std）的谱面：用来演示等级计算器里“不是 mania 谱面”的失败行。</summary>
    private static string BuildNotManiaBeatmap()
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("osu file format v14");
        sb.AppendLine();
        sb.AppendLine("[General]");
        sb.AppendLine("AudioFilename: demo-audio.mp3");
        sb.AppendLine("Mode: 0");
        sb.AppendLine();
        sb.AppendLine("[Metadata]");
        sb.AppendLine("Title:Not Mania Demo");
        sb.AppendLine("Artist:Demo Artist");
        sb.AppendLine("Creator:demo-mapper");
        sb.AppendLine("Version:std demo");
        sb.AppendLine();
        sb.AppendLine("[Difficulty]");
        sb.AppendLine("HPDrainRate:5");
        sb.AppendLine("CircleSize:4");
        sb.AppendLine("OverallDifficulty:9");
        sb.AppendLine("ApproachRate:9");
        sb.AppendLine("SliderMultiplier:1.4");
        sb.AppendLine("SliderTickRate:1");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        sb.AppendLine("0,500,4,2,1,60,1,0");
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");
        sb.AppendLine("256,192,1000,1,0,0:0:0:0:");
        sb.AppendLine("256,192,1500,1,0,0:0:0:0:");
        return sb.ToString();
    }
}
