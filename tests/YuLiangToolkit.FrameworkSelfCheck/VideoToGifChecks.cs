using YuLiangToolkit.Tools.VideoToGif;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「视频转 GIF」自检：时间解析 / 参数归一化 / 输出命名 / FFmpeg 两遍调色板参数 / 进度解析 / 工具元数据。
/// <b>不启动 FFmpeg、不碰真实文件</b>（真实转换由人工用本地视频验收）。
/// </summary>
internal static class VideoToGifChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckMetadata(check);
        CheckTimeParsing(check);
        CheckNormalize(check);
        CheckOutputName(check);
        CheckArguments(check);
        CheckProgress(check);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new VideoToGifTool();
        check("[视频转GIF] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "video-to-gif" && tool.Name == "视频转 GIF"
            && tool.Category == "媒体" && tool.Order == 11,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[视频转GIF] 标签覆盖 视频转GIF / GIF / FFmpeg / 媒体，描述提到调色板",
            tool.Tags.Contains("视频转GIF") && tool.Tags.Contains("GIF")
            && tool.Tags.Contains("FFmpeg") && tool.Tags.Contains("媒体")
            && tool.Description.Contains("调色板"),
            tool.Description);

        check("[视频转GIF] 配置默认值合理（15 FPS / 256 色 / floyd_steinberg / 无限循环）",
            new VideoToGifConfig().Fps == 15
            && new VideoToGifConfig().Colors == 256
            && new VideoToGifConfig().Dither == "floyd_steinberg"
            && new VideoToGifConfig().Loop,
            "15 / 256 / floyd_steinberg / loop");
    }

    // ---------------------------------------------------------------- 时间解析

    private static void CheckTimeParsing(Action<string, bool, string> check)
    {
        check("[视频转GIF] 时间解析：纯秒 / mm:ss / hh:mm:ss / 毫秒",
            Math.Abs(GifConverter.ParseTime("83.5") - 83.5) < 1e-6
            && Math.Abs(GifConverter.ParseTime("01:23") - 83.0) < 1e-6
            && Math.Abs(GifConverter.ParseTime("01:23.5") - 83.5) < 1e-6
            && Math.Abs(GifConverter.ParseTime("1:02:03") - 3723.0) < 1e-6,
            $"{GifConverter.ParseTime("83.5")} / {GifConverter.ParseTime("01:23.5")} / {GifConverter.ParseTime("1:02:03")}");

        check("[视频转GIF] 时间解析：空 / 非法 / 负数 → 0",
            GifConverter.ParseTime("") == 0
            && GifConverter.ParseTime(null) == 0
            && GifConverter.ParseTime("abc") == 0
            && GifConverter.ParseTime("-5") == 0,
            "空 / abc / -5");

        check("[视频转GIF] 时间格式化：hh:mm:ss.mmm",
            GifConverter.FormatTime(83.5) == "00:01:23.500"
            && GifConverter.FormatTime(3723) == "01:02:03.000"
            && GifConverter.FormatTime(-1) == "00:00:00.000",
            GifConverter.FormatTime(83.5));
    }

    // ---------------------------------------------------------------- 参数归一化

    private static void CheckNormalize(Action<string, bool, string> check)
    {
        var g = GifSettings.Normalize(0, 481, 999, "bogus", true, -3, -1);
        check("[视频转GIF] 归一化：FPS 0→15，宽度奇数→偶数且夹上限，颜色夹 256，抖动非法回退，负时间归零",
            g.Fps == 15 && g.Width == 480 && g.Colors == 256 && g.Dither == "floyd_steinberg"
            && g.StartSeconds == 0 && g.DurationSeconds == 0,
            $"{g.Fps}/{g.Width}/{g.Colors}/{g.Dither}/{g.StartSeconds}/{g.DurationSeconds}");

        var g2 = GifSettings.Normalize(100, 0, 10, "bayer", false, 5, 2);
        check("[视频转GIF] 归一化：FPS 夹 50、宽度 0=原宽保 0、颜色夹下限、白名单抖动保留",
            g2.Fps == 50 && g2.Width == 0 && g2.Colors == 10 && g2.Dither == "bayer"
            && !g2.Loop && g2.StartSeconds == 5 && g2.DurationSeconds == 2,
            $"{g2.Fps}/{g2.Width}/{g2.Colors}/{g2.Dither}");

        check("[视频转GIF] 输出宽度：0=原宽，≥原宽不放大，小于原宽取偶数",
            GifSettings.ResolveOutputWidth(0, 1920) == 1920
            && GifSettings.ResolveOutputWidth(4000, 1920) == 1920
            && GifSettings.ResolveOutputWidth(481, 1920) == 480
            && GifSettings.ResolveOutputWidth(320, 0) == 320,
            $"{GifSettings.ResolveOutputWidth(0, 1920)}/{GifSettings.ResolveOutputWidth(4000, 1920)}/{GifSettings.ResolveOutputWidth(481, 1920)}");
    }

    // ---------------------------------------------------------------- 输出命名

    private static void CheckOutputName(Action<string, bool, string> check)
    {
        check("[视频转GIF] 输出名：去扩展名 + .gif；非法字符清洗；空名兜底",
            GifConverter.BuildOutputName("clip.mp4") == "clip.gif"
            && GifConverter.BuildOutputName("a*b?.mkv") == "a_b_.gif"
            && GifConverter.SanitizeFileName("  ..  ", "转换结果") == "转换结果",
            $"{GifConverter.BuildOutputName("clip.mp4")} / {GifConverter.BuildOutputName("a*b?.mkv")}");
    }

    // ---------------------------------------------------------------- FFmpeg 参数

    private static void CheckArguments(Action<string, bool, string> check)
    {
        var gif = GifSettings.Normalize(15, 480, 256, "floyd_steinberg", true, 83.5, 3);

        var palette = GifConverter.BuildPaletteArgs(@"D:\v\clip.mp4", @"C:\tmp\pal.png", gif, 1920);
        check("[视频转GIF] 第一遍 palettegen：-ss 起点 / -t 时长在 -i 前，滤镜 fps+scale，palettegen max_colors，-frames:v 1",
            ContainsSequence(palette, "-ss", "00:01:23.500")
            && ContainsSequence(palette, "-i", @"D:\v\clip.mp4")
            && ContainsSequence(palette, "-t", "00:00:03.000")
            && palette.Any(a => a.Contains("fps=15") && a.Contains("scale=480:-2:flags=lanczos"))
            && palette.Any(a => a.Contains("palettegen=max_colors=256"))
            && ContainsSequence(palette, "-frames:v", "1")
            && palette[^1] == @"C:\tmp\pal.png",
            string.Join(" ", palette));

        var gifArgs = GifConverter.BuildGifArgs(@"D:\v\clip.mp4", @"C:\tmp\pal.png", @"D:\out\clip.gif", gif, 1920);
        check("[视频转GIF] 第二遍 paletteuse：两路输入 + filter_complex + -loop 0 + 进度 pipe:1，输出在末尾",
            ContainsSequence(gifArgs, "-i", @"D:\v\clip.mp4")
            && ContainsSequence(gifArgs, "-i", @"C:\tmp\pal.png")
            && ContainsSequence(gifArgs, "-filter_complex", "[0:v]fps=15,scale=480:-2:flags=lanczos[v];[v][1:v]paletteuse=dither=floyd_steinberg:diff_mode=rectangle")
            && ContainsSequence(gifArgs, "-loop", "0")
            && ContainsSequence(gifArgs, "-progress", "pipe:1")
            && gifArgs[^1] == @"D:\out\clip.gif",
            string.Join(" ", gifArgs));

        var noLoopGif = GifSettings.Normalize(15, 0, 256, "bayer", false, 0, 0);
        var gifArgs2 = GifConverter.BuildGifArgs(@"D:\v\clip.mp4", @"C:\tmp\pal.png", @"D:\out\clip.gif", noLoopGif, 0);
        check("[视频转GIF] 只播一次 -loop -1；宽 0 不加 scale；起点 0 不加 -ss；时长 0 不加 -t",
            ContainsSequence(gifArgs2, "-loop", "-1")
            && !gifArgs2.Contains("-ss")
            && !gifArgs2.Contains("-t")
            && !gifArgs2.Contains("scale="),
            string.Join(" ", gifArgs2));
    }

    private static bool ContainsSequence(IReadOnlyList<string> args, string first, string second)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == first && args[i + 1] == second) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 进度解析

    private static void CheckProgress(Action<string, bool, string> check)
    {
        check("[视频转GIF] 进度解析：out_time_us / out_time_ms 转秒，其它行返回 null",
            Math.Abs((GifConverter.ParseProgressSeconds("out_time_us=1500000") ?? -1) - 1.5) < 1e-6
            && Math.Abs((GifConverter.ParseProgressSeconds("out_time_ms=2000000") ?? -1) - 2.0) < 1e-6
            && GifConverter.ParseProgressSeconds("progress=continue") is null
            && GifConverter.ParseProgressSeconds("") is null,
            "1.5 / 2.0 / null");

        check("[视频转GIF] 输入校验：空 / 不存在报错（不碰 FFmpeg）",
            GifConverter.Validate("") is not null
            && GifConverter.Validate(@"C:\no\such\video-xyz.mp4") is not null,
            "空 / 不存在");
    }
}
