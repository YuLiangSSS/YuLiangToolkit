using System.IO;
using YuLiangToolkit.Tools.ScreenRecorder;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「录屏 / GIF」自检（screen-recorder 组）：只断言纯逻辑 —— 区域换算 / 参数归一化 /
/// FFmpeg 参数拼装（采集 / GIF 两遍 / MP4 / 截图）/ 文件名 / 计时文案 / 配置默认值 / 工具元数据。
///
/// <para><b>不启动 FFmpeg、不采集屏幕、不弹窗、不碰真实 Data/Config</b>：真正的录制与 GIF 合成
/// 由人工用「录 3 秒区域 GIF」验收（见 docs/tools/screen-recorder.md）。</para>
/// </summary>
internal static class ScreenRecorderChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckMetadata(check);
        CheckRect(check);
        CheckDpiMapping(check);
        CheckOptions(check);
        CheckCaptureArguments(check);
        CheckGifArguments(check);
        CheckMp4Arguments(check);
        CheckScreenshotArguments(check);
        CheckNames(check);
        CheckTime(check);
        CheckConfigDefaults(check);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new ScreenRecorderTool();
        check("[录屏] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "screen-recorder" && tool.Name == "录屏 / GIF"
            && tool.Category == "媒体" && tool.Order == 11,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[录屏] 标签覆盖录屏 / GIF / 截图 / MP4 / FFmpeg",
            tool.Tags.Contains("录屏") && tool.Tags.Contains("GIF") && tool.Tags.Contains("截图")
            && tool.Tags.Contains("MP4") && tool.Tags.Contains("FFmpeg"),
            string.Join(",", tool.Tags));

        check("[录屏] 描述说明 GIF / MP4 / 截图三种产出",
            tool.Description.Contains("GIF") && tool.Description.Contains("MP4")
            && tool.Description.Contains("PNG") && tool.Description.Contains("全屏"),
            tool.Description);
    }

    // ---------------------------------------------------------------- 区域换算

    private static void CheckRect(Action<string, bool, string> check)
    {
        check("[录屏] 区域：两点构造自动处理反向拖动 / 负坐标",
            CaptureRect.FromPoints(500, 400, 100, 50) == new CaptureRect(100, 50, 400, 350)
            && CaptureRect.FromPoints(-300, 20, -100, 120) == new CaptureRect(-300, 20, 200, 100),
            CaptureRect.FromPoints(500, 400, 100, 50).ToString());

        check("[录屏] 区域：空矩形 / 边界判断",
            CaptureRect.Empty.IsEmpty && new CaptureRect(0, 0, 0, 10).IsEmpty
            && !new CaptureRect(0, 0, 10, 10).IsEmpty
            && new CaptureRect(10, 20, 30, 40).Right == 40
            && new CaptureRect(10, 20, 30, 40).Bottom == 60
            && new CaptureRect(10, 20, 30, 40).Area == 1200,
            "Empty / Right / Bottom / Area");

        var screen = new CaptureRect(0, 0, 1920, 1080);

        check("[录屏] 区域：越界自动夹进屏幕",
            new CaptureRect(-50, -20, 300, 200).ClampTo(screen) == new CaptureRect(0, 0, 250, 180)
            && new CaptureRect(1800, 1000, 400, 300).ClampTo(screen) == new CaptureRect(1800, 1000, 120, 80),
            new CaptureRect(-50, -20, 300, 200).ClampTo(screen).ToString());

        check("[录屏] 区域：完全在屏幕外 → 空矩形",
            new CaptureRect(4000, 4000, 100, 100).ClampTo(screen).IsEmpty,
            "4000,4000");

        check("[录屏] 区域：负原点虚拟屏（副屏在左边）也能正确裁剪",
            new CaptureRect(-2000, -50, 500, 200).ClampTo(new CaptureRect(-1920, 0, 1920, 1080))
                == new CaptureRect(-1920, 0, 420, 150),
            new CaptureRect(-2000, -50, 500, 200).ClampTo(new CaptureRect(-1920, 0, 1920, 1080)).ToString());

        check("[录屏] 区域：宽高裁成偶数（H.264 / GIF 要求）",
            new CaptureRect(1, 1, 801, 601).MakeEven() == new CaptureRect(1, 1, 800, 600)
            && new CaptureRect(0, 0, 1, 1).MakeEven() == new CaptureRect(0, 0, 2, 2),
            new CaptureRect(1, 1, 801, 601).MakeEven().ToString());

        check("[录屏] 区域：等比缩宽只缩小 / 高度取偶数 / 不放大",
            new CaptureRect(0, 0, 1920, 1080).ScaleWidth(960) == new CaptureRect(0, 0, 960, 540)
            && new CaptureRect(0, 0, 800, 600).ScaleWidth(1920).Width == 800
            && new CaptureRect(0, 0, 1920, 1080).ScaleWidth(963).Width == 962,
            new CaptureRect(0, 0, 1920, 1080).ScaleWidth(963).ToString());
    }

    // ---------------------------------------------------------------- DPI / 框选坐标换算

    private static void CheckDpiMapping(Action<string, bool, string> check)
    {
        check("[录屏] 框选坐标：100% 缩放时 DIP = 物理像素",
            ScreenCoordinateMapper.DipToPhysical(100, 40, 800, 600, 0, 0, 1, 1)
                == new CaptureRect(100, 40, 800, 600),
            ScreenCoordinateMapper.DipToPhysical(100, 40, 800, 600, 0, 0, 1, 1).ToString());

        check("[录屏] 框选坐标：125% 缩放按主屏 DPI 放大成物理像素",
            ScreenCoordinateMapper.DipToPhysical(100, 40, 800, 600, 0, 0, 1.25, 1.25)
                == new CaptureRect(125, 50, 1000, 750),
            ScreenCoordinateMapper.DipToPhysical(100, 40, 800, 600, 0, 0, 1.25, 1.25).ToString());

        check("[录屏] 框选坐标：副屏在左边（虚拟屏负坐标）也换算正确",
            ScreenCoordinateMapper.DipToPhysical(0, 0, 400, 300, -1536, 0, 1.25, 1.25)
                == new CaptureRect(-1920, 0, 500, 375),
            ScreenCoordinateMapper.DipToPhysical(0, 0, 400, 300, -1536, 0, 1.25, 1.25).ToString());

        check("[录屏] 框选坐标：非法缩放 / 空选框安全兜底",
            ScreenCoordinateMapper.DipToPhysical(10, 10, 100, 100, 0, 0, 0, double.NaN)
                == new CaptureRect(10, 10, 100, 100)
            && ScreenCoordinateMapper.DipToPhysical(10, 10, 0, 100, 0, 0, 1, 1).IsEmpty,
            "scale 0/NaN / width=0");
    }

    // ---------------------------------------------------------------- 配置值归一化

    private static void CheckOptions(Action<string, bool, string> check)
    {
        check("[录屏] 来源解析：fullscreen / window / 未知回退区域",
            RecorderOptions.ParseSourceKind("fullscreen") == CaptureSourceKind.FullScreen
            && RecorderOptions.ParseSourceKind("WINDOW") == CaptureSourceKind.Window
            && RecorderOptions.ParseSourceKind("screen") == CaptureSourceKind.FullScreen
            && RecorderOptions.ParseSourceKind("wat") == CaptureSourceKind.Region
            && RecorderOptions.ParseSourceKind(null) == CaptureSourceKind.Region,
            "fullscreen / WINDOW / screen / wat / null");

        check("[录屏] 格式解析：只有 mp4 是视频，其余一律 GIF",
            RecorderOptions.NormalizeFormat("mp4") == "mp4"
            && RecorderOptions.NormalizeFormat("MP4") == "mp4"
            && RecorderOptions.NormalizeFormat("gif") == "gif"
            && RecorderOptions.NormalizeFormat("webm") == "gif"
            && RecorderOptions.NormalizeFormat(null) == "gif",
            "mp4 / MP4 / gif / webm / null");

        check("[录屏] 帧率夹到 1~30",
            RecorderOptions.NormalizeFps(0) == 1
            && RecorderOptions.NormalizeFps(-5) == 1
            && RecorderOptions.NormalizeFps(15) == 15
            && RecorderOptions.NormalizeFps(120) == 30,
            "0 / -5 / 15 / 120");

        check("[录屏] 输出宽度：0 = 原始；其它夹到 160~3840 且取偶数",
            RecorderOptions.NormalizeScaleWidth(0) == 0
            && RecorderOptions.NormalizeScaleWidth(-1) == 0
            && RecorderOptions.NormalizeScaleWidth(961) == 960
            && RecorderOptions.NormalizeScaleWidth(10) == 160
            && RecorderOptions.NormalizeScaleWidth(9000) == 3840,
            "0 / -1 / 961 / 10 / 9000");

        check("[录屏] GIF 颜色数 2~256、抖动白名单、CRF / 倒计时 / 最长时长夹取",
            RecorderOptions.NormalizeColors(0) == 2
            && RecorderOptions.NormalizeColors(999) == 256
            && RecorderOptions.NormalizeColors(128) == 128
            && RecorderOptions.NormalizeDither("BAYER") == "bayer"
            && RecorderOptions.NormalizeDither("none") == "none"
            && RecorderOptions.NormalizeDither("乱写") == "floyd_steinberg"
            && RecorderOptions.NormalizeCrf(0) == 23
            && RecorderOptions.NormalizeCrf(60) == 51
            && RecorderOptions.NormalizeCountdown(-1) == 0
            && RecorderOptions.NormalizeCountdown(100) == 10
            && RecorderOptions.NormalizeMaxSeconds(-1) == 0
            && RecorderOptions.NormalizeMaxSeconds(999999) == 86400,
            "colors / dither / crf / countdown / max seconds");

        var gif = GifExportSettings.Normalize(0, 9999, 0, "wat", true);
        check("[录屏] GifExportSettings.Normalize 一次把四个值拉回合法范围",
            gif == new GifExportSettings(1, 3840, 2, "floyd_steinberg", true),
            gif.ToString());

        var mp4 = Mp4ExportSettings.Normalize(-3, 18);
        check("[录屏] Mp4ExportSettings.Normalize 拉回合法范围",
            mp4 == new Mp4ExportSettings(0, 18),
            mp4.ToString());

        check("[录屏] 输出宽度换算：0 / 超过源宽 = 源宽（取偶数），中间值取偶数，不放大",
            FfmpegArguments.ResolveOutputWidth(0, 1921) == 1920
            && FfmpegArguments.ResolveOutputWidth(5000, 1920) == 1920
            && FfmpegArguments.ResolveOutputWidth(1200, 1920) == 1200
            && FfmpegArguments.ResolveOutputWidth(963, 1920) == 962
            && FfmpegArguments.ResolveOutputWidth(0, 800) == 800,
            "1921/0 → 1920 · 1920/5000 → 1920 · 1920/963 → 962");

        check("[录屏] 来源界面文案：全屏 / 区域 / 窗口",
            RecorderOptions.SourceLabel(CaptureSourceKind.FullScreen) == "全屏（主显示器）"
            && RecorderOptions.SourceLabel(CaptureSourceKind.Region) == "区域"
            && RecorderOptions.SourceLabel(CaptureSourceKind.Window) == "窗口",
            RecorderOptions.SourceLabel(CaptureSourceKind.FullScreen));
    }

    // ---------------------------------------------------------------- FFmpeg 参数

    private static void CheckCaptureArguments(Action<string, bool, string> check)
    {
        var plan = new CapturePlan(new CaptureRect(100, 50, 801, 601), 15, true, 0, "区域");
        var args = string.Join(" ", FfmpegArguments.BuildCapture(@"C:\Cache\screen-recorder\tmp.mkv", plan));

        check("[录屏] 采集参数：gdigrab + 帧率 + 鼠标 + 偏移 + 偶数尺寸 + 中间 MKV",
            args.Contains("-f gdigrab", StringComparison.Ordinal)
            && args.Contains("-framerate 15", StringComparison.Ordinal)
            && args.Contains("-draw_mouse 1", StringComparison.Ordinal)
            && args.Contains("-offset_x 100", StringComparison.Ordinal)
            && args.Contains("-offset_y 50", StringComparison.Ordinal)
            && args.Contains("-video_size 800x600", StringComparison.Ordinal)
            && args.Contains("-i desktop", StringComparison.Ordinal)
            && args.Contains("-c:v libx264", StringComparison.Ordinal)
            && args.Contains("-an", StringComparison.Ordinal)
            && args.EndsWith(@"tmp.mkv", StringComparison.Ordinal),
            args);

        check("[录屏] 采集参数不能带 -nostdin（否则没法写 q 正常收尾）",
            !args.Contains("-nostdin", StringComparison.Ordinal),
            args);

        var noMouse = new CapturePlan(new CaptureRect(0, 0, 1920, 1080), 30, false, 0, "全屏");
        var noMouseArgs = string.Join(" ", FfmpegArguments.BuildCapture("tmp.mkv", noMouse));
        check("[录屏] 采集参数：关鼠标 = -draw_mouse 0；30 FPS；不限制时不加 -t",
            noMouseArgs.Contains("-draw_mouse 0", StringComparison.Ordinal)
            && noMouseArgs.Contains("-framerate 30", StringComparison.Ordinal)
            && !noMouseArgs.Contains(" -t ", StringComparison.Ordinal),
            noMouseArgs);

        var limited = new CapturePlan(new CaptureRect(0, 0, 1920, 1080), 10, true, 60, "全屏");
        var limitedArgs = FfmpegArguments.BuildCapture("tmp.mkv", limited).ToList();
        var desktopIndex = limitedArgs.IndexOf("desktop");
        var durationIndex = limitedArgs.IndexOf("-t");
        check("[录屏] 采集参数：最长 60 秒 → -t 60，且 -t 位于 -i desktop 之后（输出时长，不是输入选项）",
            limitedArgs.Contains("-t") && limitedArgs.Contains("60")
            && desktopIndex >= 0 && durationIndex > desktopIndex,
            string.Join(" ", limitedArgs));
    }

    private static void CheckGifArguments(Action<string, bool, string> check)
    {
        var region = new CaptureRect(0, 0, 1920, 1080);
        var gif = new GifExportSettings(12, 960, 128, "bayer", true);

        var palette = string.Join(" ", FfmpegArguments.BuildGifPalette("tmp.mkv", "palette.png", gif, region));
        check("[录屏] GIF 第一遍：fps + 缩放 + palettegen（颜色数 / stats_mode=diff）",
            palette.Contains("-vf fps=12,scale=960:-2:flags=lanczos,palettegen=max_colors=128:stats_mode=diff", StringComparison.Ordinal)
            && palette.Contains("-i tmp.mkv", StringComparison.Ordinal)
            && palette.Contains("-frames:v 1", StringComparison.Ordinal)
            && palette.EndsWith("palette.png", StringComparison.Ordinal),
            palette);

        var gifArgs = string.Join(" ", FfmpegArguments.BuildGif("tmp.mkv", "palette.png", "out.gif", gif, region));
        check("[录屏] GIF 第二遍：paletteuse 抖动 + diff_mode + 无限循环 -loop 0",
            gifArgs.Contains("-filter_complex", StringComparison.Ordinal)
            && gifArgs.Contains("paletteuse=dither=bayer:diff_mode=rectangle", StringComparison.Ordinal)
            && gifArgs.Contains("-i tmp.mkv -i palette.png", StringComparison.Ordinal)
            && gifArgs.Contains("-loop 0", StringComparison.Ordinal)
            && gifArgs.EndsWith("out.gif", StringComparison.Ordinal),
            gifArgs);

        var once = string.Join(" ", FfmpegArguments.BuildGif("tmp.mkv", "palette.png", "out.gif",
            new GifExportSettings(10, 0, 256, "none", false), region));
        check("[录屏] GIF 只播一次 = -loop -1（-loop 1 其实会播两遍）；原始宽度时不加 scale 滤镜",
            once.Contains("-loop -1", StringComparison.Ordinal)
            && !once.Contains("scale=", StringComparison.Ordinal)
            && once.Contains("dither=none", StringComparison.Ordinal),
            once);

        // 宽度不小于原宽 → 不放大
        var noUpscale = string.Join(" ", FfmpegArguments.BuildGifPalette("tmp.mkv", "palette.png",
            new GifExportSettings(10, 1920, 256, "floyd_steinberg", true),
            new CaptureRect(0, 0, 800, 600)));
        check("[录屏] GIF 输出宽度不小于源宽时不放大（不加 scale）",
            !noUpscale.Contains("scale=", StringComparison.Ordinal),
            noUpscale);
    }

    private static void CheckMp4Arguments(Action<string, bool, string> check)
    {
        var args = string.Join(" ", FfmpegArguments.BuildMp4(
            "tmp.mkv", "out.mp4",
            new Mp4ExportSettings(1280, 23),
            new CaptureRect(0, 0, 1920, 1080)));

        check("[录屏] MP4：H.264 + CRF + yuv420p + faststart + 缩放 -2",
            args.Contains("-c:v libx264", StringComparison.Ordinal)
            && args.Contains("-crf 23", StringComparison.Ordinal)
            && args.Contains("-pix_fmt yuv420p", StringComparison.Ordinal)
            && args.Contains("-movflags +faststart", StringComparison.Ordinal)
            && args.Contains("-vf scale=1280:-2:flags=lanczos", StringComparison.Ordinal)
            && args.Contains("-an", StringComparison.Ordinal)
            && args.EndsWith("out.mp4", StringComparison.Ordinal),
            args);

        var original = string.Join(" ", FfmpegArguments.BuildMp4(
            "tmp.mkv", "out.mp4", new Mp4ExportSettings(0, 28), new CaptureRect(0, 0, 1920, 1080)));
        check("[录屏] MP4：原始宽度时不加缩放；CRF 28 原样传给 FFmpeg",
            !original.Contains("-vf ", StringComparison.Ordinal)
            && original.Contains("-crf 28", StringComparison.Ordinal),
            original);
    }

    private static void CheckScreenshotArguments(Action<string, bool, string> check)
    {
        var plan = new CapturePlan(new CaptureRect(10, 20, 801, 601), 1, true, 0, "截图");
        var args = string.Join(" ", FfmpegArguments.BuildScreenshot("shot.png", plan));
        check("[录屏] 截图参数：gdigrab 单帧 + 帧率 1 + 鼠标开关 + 偏移 + 偶数尺寸 + PNG",
            args.Contains("-f gdigrab", StringComparison.Ordinal)
            && args.Contains("-framerate 1", StringComparison.Ordinal)
            && args.Contains("-draw_mouse 1", StringComparison.Ordinal)
            && args.Contains("-offset_x 10", StringComparison.Ordinal)
            && args.Contains("-offset_y 20", StringComparison.Ordinal)
            && args.Contains("-video_size 800x600", StringComparison.Ordinal)
            && args.Contains("-frames:v 1", StringComparison.Ordinal)
            && args.Contains("-update 1", StringComparison.Ordinal)
            && args.EndsWith("shot.png", StringComparison.Ordinal),
            args);
    }

    // ---------------------------------------------------------------- 文件名 / 计时

    private static void CheckNames(Action<string, bool, string> check)
    {
        check("[录屏] 文件名前缀：非法字符替换、首尾点 / 空格清理、空值回退默认",
            RecordingNames.SanitizePrefix("a:b*c?d\"e<f>g|h") == "a_b_c_d_e_f_g_h"
            && RecordingNames.SanitizePrefix("  ..abc..  ") == "abc"
            && RecordingNames.SanitizePrefix("") == "录屏"
            && RecordingNames.SanitizePrefix(null) == "录屏",
            RecordingNames.SanitizePrefix("a:b*c?d\"e<f>g|h"));

        check("[录屏] 文件名前缀：超长截到 40 字符",
            RecordingNames.SanitizePrefix(new string('a', 100)).Length == RecordingNames.MaxPrefixLength,
            RecordingNames.SanitizePrefix(new string('a', 100)).Length.ToString());

        var time = new DateTime(2026, 1, 2, 3, 4, 5);
        check("[录屏] 文件名：前缀-日期-时间.扩展名；重名加 (2)",
            RecordingNames.BuildFileName("录屏", time, "gif") == "录屏-20260102-030405.gif"
            && RecordingNames.BuildFileName("录屏", time, ".mp4") == "录屏-20260102-030405.mp4"
            && RecordingNames.BuildFileName("录屏", time, "gif", 2) == "录屏-20260102-030405 (2).gif",
            RecordingNames.BuildFileName("录屏", time, "gif", 2));

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\out", "录屏-20260102-030405.gif"),
            Path.Combine(@"C:\out", "录屏-20260102-030405 (2).gif"),
        };
        var path = RecordingNames.BuildOutputPath(@"C:\out", "录屏", time, "gif", existing.Contains);
        check("[录屏] 输出路径：已存在前缀 … (2) 时自动用 (3)",
            path == Path.Combine(@"C:\out", "录屏-20260102-030405 (3).gif"),
            path);
    }

    private static void CheckTime(Action<string, bool, string> check)
    {
        check("[录屏] 计时文案：00:12 / 1:02:03 / 1:00:00 / 负数按 0",
            RecordingTime.FormatDuration(TimeSpan.FromSeconds(12)) == "00:12"
            && RecordingTime.FormatDuration(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(3)) == "1:02:03"
            && RecordingTime.FormatDuration(TimeSpan.FromHours(1)) == "1:00:00"
            && RecordingTime.FormatDuration(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(9)) == "05:09"
            && RecordingTime.FormatDuration(TimeSpan.FromSeconds(-5)) == "00:00",
            RecordingTime.FormatDuration(TimeSpan.FromHours(1)));
    }

    // ---------------------------------------------------------------- 配置默认值

    private static void CheckConfigDefaults(Action<string, bool, string> check)
    {
        var config = new ScreenRecorderConfig();
        check("[录屏] 配置默认值：区域来源 / GIF / 15 FPS / 960 宽 / 3 秒倒计时",
            config.SourceKind == "region" && config.Format == "gif" && config.Fps == 15
            && config.ScaleWidth == 960 && config.GifColors == 256
            && config.GifDither == "floyd_steinberg" && config.GifLoop
            && config.DrawMouse && config.CountdownSeconds == 3
            && config.MaxSeconds == 0 && config.ShowControlBar && config.UseStopHotkey,
            $"{config.SourceKind}/{config.Format}/{config.Fps}/{config.ScaleWidth}/{config.CountdownSeconds}");

        check("[录屏] 配置计算属性：IsGif / IsMp4 / HasRegion / 来源判断正确",
            config.IsGif && !config.IsMp4 && !config.HasRegion
            && config.IsRegionSource && !config.IsWindowSource && !config.IsFullScreenSource,
            $"{config.IsGif}/{config.IsMp4}/{config.HasRegion}");

        config.Format = "mp4";
        config.SourceKind = "window";
        config.RegionWidth = 800;
        config.RegionHeight = 600;
        check("[录屏] 配置计算属性随字段变化（MP4 / 窗口 / 有区域）",
            config.IsMp4 && !config.IsGif && config.IsWindowSource
            && !config.IsRegionSource && config.HasRegion,
            $"{config.IsMp4}/{config.SourceKind}/{config.HasRegion}");

        check("[录屏] 配置路径常量：Config\\screen-recorder\\，默认前缀「录屏」",
            ScreenRecorderConfig.ToolId == "screen-recorder"
            && ScreenRecorderConfig.DefaultFilePrefix == "录屏",
            ScreenRecorderConfig.ToolId);

        // 回归：联动属性必须发 PropertyChanged（自动属性不发 → GIF/MP4 选项块、窗口面板全都不刷新）。
        var changed = new List<string?>();
        var watched = new ScreenRecorderConfig();
        watched.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        watched.SourceKind = "window";
        watched.Format = "mp4";
        watched.Fps = 30;
        watched.ScaleWidth = 1280;
        watched.GifColors = 64;
        watched.MaxSeconds = 60;
        watched.UseStopHotkey = false;
        watched.OutputFolder = @"C:\录屏";
        check("[录屏] 配置联动属性会发 PropertyChanged（否则 OnConfigChanged 是死代码）",
            changed.Contains(nameof(ScreenRecorderConfig.SourceKind))
            && changed.Contains(nameof(ScreenRecorderConfig.Format))
            && changed.Contains(nameof(ScreenRecorderConfig.Fps))
            && changed.Contains(nameof(ScreenRecorderConfig.ScaleWidth))
            && changed.Contains(nameof(ScreenRecorderConfig.GifColors))
            && changed.Contains(nameof(ScreenRecorderConfig.MaxSeconds))
            && changed.Contains(nameof(ScreenRecorderConfig.UseStopHotkey))
            && changed.Contains(nameof(ScreenRecorderConfig.OutputFolder)),
            string.Join(",", changed));

        // 回归：手改坏 config → 归一化后界面显示 = 实际使用（吸附到最近档位）。
        var broken = new ScreenRecorderConfig
        {
            SourceKind = "乱写",
            Format = "webm",
            Fps = 7,
            ScaleWidth = 1000,
            GifColors = 200,
            GifDither = "乱写",
            Mp4Quality = 20,
            CountdownSeconds = 7,
            MaxSeconds = 45,
            FilePrefix = "a:b*c",
        };
        RecorderOptions.NormalizeConfig(broken);
        check("[录屏] 配置归一化：坏值都会吸附到界面档位（显示与实际不再不一致）",
            broken.SourceKind == "region" && broken.Format == "gif"
            && broken.Fps == 5 && broken.ScaleWidth == 960 && broken.GifColors == 256
            && broken.GifDither == "floyd_steinberg" && broken.Mp4Quality == 18
            && broken.CountdownSeconds == 5 && broken.MaxSeconds == 30
            && broken.FilePrefix == "a_b_c",
            $"{broken.SourceKind}/{broken.Format}/{broken.Fps}/{broken.ScaleWidth}/{broken.GifColors}/{broken.MaxSeconds}/{broken.FilePrefix}");

        // 正数不能吸附成 0（0 的含义是“原始分辨率 / 不限制”，会把 2 秒录制变成无限录制）
        var positive = new ScreenRecorderConfig { ScaleWidth = 320, MaxSeconds = 2 };
        RecorderOptions.NormalizeConfig(positive);
        check("[录屏] 配置归一化：正数宽度 / 最长录制不会吸附成 0（避免 2 秒变不限、320 变原始）",
            positive.ScaleWidth == 480 && positive.MaxSeconds == 30,
            $"width={positive.ScaleWidth} max={positive.MaxSeconds}");
    }
}
