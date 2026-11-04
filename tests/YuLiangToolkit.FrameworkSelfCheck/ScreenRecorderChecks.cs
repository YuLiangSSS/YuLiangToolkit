using System.IO;
using YuLiangToolkit.Tools.ScreenRecorder;
using YuLiangToolkit.Tools.ScreenRecorder.Services;

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
        CheckVideoEncoding(check);
        CheckRemux(check);
        CheckEncoderDetection(check);
        CheckHotkeys(check);
        CheckCaptureArguments(check);
        CheckGifArguments(check);
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

        check("[录屏] 格式解析：gif / 五种视频容器都认，乱写回退 GIF",
            RecorderOptions.NormalizeFormat("gif") == "gif"
            && RecorderOptions.NormalizeFormat("MP4") == "mp4"
            && RecorderOptions.NormalizeFormat("mkv") == "mkv"
            && RecorderOptions.NormalizeFormat("mov") == "mov"
            && RecorderOptions.NormalizeFormat("avi") == "avi"
            && RecorderOptions.NormalizeFormat("webm") == "webm"
            && RecorderOptions.NormalizeFormat("乱写") == "gif"
            && RecorderOptions.NormalizeFormat(null) == "gif",
            "gif / MP4 / mkv / mov / avi / webm / 乱写 / null");

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

        check("[录屏] GIF 颜色数 2~256、抖动白名单、画质 / 倒计时 / 最长时长夹取",
            RecorderOptions.NormalizeColors(0) == 2
            && RecorderOptions.NormalizeColors(999) == 256
            && RecorderOptions.NormalizeColors(128) == 128
            && RecorderOptions.NormalizeDither("BAYER") == "bayer"
            && RecorderOptions.NormalizeDither("none") == "none"
            && RecorderOptions.NormalizeDither("乱写") == "floyd_steinberg"
            && VideoEncoderSettings.Normalize("libx264", "quality", -5, 1).Quality == 0
            && VideoEncoderSettings.Normalize("libx264", "quality", 99, 1).Quality == 51
            && RecorderOptions.NormalizeCountdown(-1) == 0
            && RecorderOptions.NormalizeCountdown(100) == 10
            && RecorderOptions.NormalizeMaxSeconds(-1) == 0
            && RecorderOptions.NormalizeMaxSeconds(999999) == 86400,
            "colors / dither / quality / countdown / max seconds");

        var gif = GifExportSettings.Normalize(0, 9999, 0, "wat", true);
        check("[录屏] GifExportSettings.Normalize 一次把四个值拉回合法范围",
            gif == new GifExportSettings(1, 3840, 2, "floyd_steinberg", true),
            gif.ToString());

        var mp4 = VideoEncoderSettings.Normalize("libx264", "bitrate", 120, 500);
        check("[录屏] 编码参数归一化：模式 / 画质 / 码率都在合法范围",
            mp4.RateMode == VideoEncoderSettings.RateBitrate
            && mp4.Quality == 51 && mp4.BitrateMbps == 100
            && VideoEncoderSettings.Normalize("libx264", "乱写", -5, 0).Quality == 0
            && VideoEncoderSettings.Normalize("libx264", "乱写", -5, 0).RateMode == VideoEncoderSettings.RateQuality,
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

        var videoPlan = new CapturePlan(new CaptureRect(0, 0, 1920, 1080), 15, true, 0, "全屏");
        var videoArgs = string.Join(" ", FfmpegArguments.BuildCapture(
            "tmp.mkv", videoPlan, new VideoEncoderSettings("libx264", "quality", 20, 8), 1280));
        check("[录屏] 视频采集：按选定编码器 / CRF 录中间素材，并在采集阶段等比缩到输出宽度",
            videoArgs.Contains("-c:v libx264", StringComparison.Ordinal)
            && videoArgs.Contains("-crf 20", StringComparison.Ordinal)
            && videoArgs.Contains("-vf scale=1280:-2:flags=lanczos", StringComparison.Ordinal)
            && videoArgs.Contains("-an", StringComparison.Ordinal)
            && !videoArgs.Contains("-preset ultrafast", StringComparison.Ordinal),
            videoArgs);

        var videoOriginal = string.Join(" ", FfmpegArguments.BuildCapture(
            "tmp.mkv", videoPlan, new VideoEncoderSettings("libx264", "quality", 20, 8), 0));
        check("[录屏] 视频采集：输出宽度 0（原始）时不加缩放滤镜",
            !videoOriginal.Contains("-vf ", StringComparison.Ordinal),
            videoOriginal);
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

    // ---------------------------------------------------------------- 视频编码 / 封装

    private static void CheckHotkeys(Action<string, bool, string> check)
    {
        var parsedF8 = HotkeySpec.TryParse("F8", out var f8);
        var parsedCombo = HotkeySpec.TryParse(" ctrl + shift + f9 ", out var combo);
        var parsedRoundtrip = HotkeySpec.TryParse(combo.DisplayText, out var roundtrip);
        check("[录屏] 快捷键解析：F8 / Ctrl+Shift+F9 / 大小写与空格容错",
            parsedF8 && f8.DisplayText == "F8"
            && parsedCombo && combo.DisplayText == "Ctrl+Shift+F9"
            && parsedRoundtrip && roundtrip == combo,
            f8.DisplayText + " / " + combo.DisplayText);

        check("[录屏] 快捷键校验：普通字母必须带修饰键，只有修饰键 / 空值无效",
            !HotkeySpec.TryParse("A", out _)
            && !HotkeySpec.TryParse("Ctrl", out _)
            && !HotkeySpec.TryParse("", out _)
            && !HotkeySpec.TryParse(null, out _)
            && HotkeySpec.TryParse("Ctrl+A", out var ctrlA) && ctrlA.DisplayText == "Ctrl+A",
            "A / Ctrl / empty / null / Ctrl+A");

        check("[录屏] 快捷键解析：多个修饰键按 Win32 位组合",
            HotkeySpec.TryParse("Ctrl+Alt+Shift+Win+F12", out var all)
            && all.Modifiers == (HotkeySpec.ModControl | HotkeySpec.ModAlt | HotkeySpec.ModShift | HotkeySpec.ModWin)
            && all.DisplayText == "Ctrl+Alt+Shift+Win+F12",
            all.DisplayText);

        var keyF8Ok = HotkeySpec.TryFromKey(System.Windows.Input.Key.F8, System.Windows.Input.ModifierKeys.None, out var keyF8);
        var keyAOk = HotkeySpec.TryFromKey(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.None, out _);
        var keyCtrlAOk = HotkeySpec.TryFromKey(System.Windows.Input.Key.A, System.Windows.Input.ModifierKeys.Control, out var keyCtrlA);
        check("[录屏] 快捷键捕获：F8 可单独用；字母无修饰键无效；Ctrl+A 有效",
            keyF8Ok && keyF8.DisplayText == "F8"
            && !keyAOk
            && keyCtrlAOk && keyCtrlA.DisplayText == "Ctrl+A",
            keyF8.DisplayText + " / " + keyCtrlA.DisplayText);
    }

    private static void CheckVideoEncoding(Action<string, bool, string> check)
    {
        var x264 = string.Join(" ", VideoEncodingArgs.Build(new VideoEncoderSettings("libx264", "quality", 23, 8)));
        check("[录屏] 视频编码：libx264 画质模式 = veryfast + CRF + yuv420p",
            x264.Contains("-c:v libx264", StringComparison.Ordinal)
            && x264.Contains("-preset veryfast", StringComparison.Ordinal)
            && x264.Contains("-crf 23", StringComparison.Ordinal)
            && x264.Contains("-pix_fmt yuv420p", StringComparison.Ordinal),
            x264);

        var nvenc = string.Join(" ", VideoEncodingArgs.Build(new VideoEncoderSettings("h264_nvenc", "quality", 20, 8)));
        check("[录屏] 视频编码：NVENC 画质模式 = vbr + cq + b:v 0；码率模式 = 目标码率 + maxrate/bufsize",
            nvenc.Contains("-rc vbr", StringComparison.Ordinal)
            && nvenc.Contains("-cq 20", StringComparison.Ordinal)
            && nvenc.Contains("-b:v 0", StringComparison.Ordinal)
            && string.Join(" ", VideoEncodingArgs.Build(new VideoEncoderSettings("h264_nvenc", "bitrate", 20, 12)))
                .Contains("-b:v 12000k -maxrate 18000k -bufsize 24000k", StringComparison.Ordinal),
            nvenc);

        var mpeg4 = string.Join(" ", VideoEncodingArgs.Build(new VideoEncoderSettings("mpeg4", "quality", 23, 8)));
        check("[录屏] 视频编码：MPEG-4 / MJPEG 把 CRF 映射成 qscale（23 → 15）",
            mpeg4.Contains("-c:v mpeg4", StringComparison.Ordinal)
            && mpeg4.Contains("-q:v 15", StringComparison.Ordinal),
            mpeg4);

        var vp9 = string.Join(" ", VideoEncodingArgs.Build(new VideoEncoderSettings("libvpx-vp9", "quality", 30, 8)));
        check("[录屏] 视频编码：VP9 画质模式 = crf + b:v 0（WebM 常用）",
            vp9.Contains("-c:v libvpx-vp9", StringComparison.Ordinal)
            && vp9.Contains("-crf 30", StringComparison.Ordinal)
            && vp9.Contains("-b:v 0", StringComparison.Ordinal),
            vp9);

        var unknown = string.Join(" ", VideoEncodingArgs.Build(new VideoEncoderSettings("不存在的编码器", "quality", 20, 8)));
        check("[录屏] 视频编码：认不出的编码器安全回退 libx264",
            unknown.Contains("-c:v libx264", StringComparison.Ordinal)
            && unknown.Contains("-crf 20", StringComparison.Ordinal),
            unknown);

        check("[录屏] 编码器目录：容器兼容性（H.264 能进 MP4/AVI 不进 WebM；VP9 能进 WebM）",
            VideoCatalog.IsCompatible("mp4", "libx264")
            && VideoCatalog.IsCompatible("avi", "mpeg4")
            && !VideoCatalog.IsCompatible("avi", "hevc_nvenc")
            && !VideoCatalog.IsCompatible("webm", "libx264")
            && VideoCatalog.IsCompatible("webm", "libvpx-vp9"),
            "mp4/libx264 · avi/mpeg4 · avi/hevc · webm/libx264 · webm/vp9");

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "libx265", "libvpx-vp9" };
        var codecs = VideoCatalog.CompatibleCodecs("mp4", available);
        check("[录屏] 编码器目录：按容器 + 本机 ffmpeg 可用列表过滤",
            codecs.Count == 1 && codecs[0].Id == "libx265"
            && VideoCatalog.DefaultCodecFor("mp4", available) == "libx265"
            && VideoCatalog.CompatibleCodecs("webm", available).Single().Id == "libvpx-vp9",
            string.Join(",", codecs.Select(c => c.Id)));
    }

    private static void CheckRemux(Action<string, bool, string> check)
    {
        var videoOnly = string.Join(" ", FfmpegArguments.BuildRemux(
            "tmp.mkv", "out.avi", "avi", Array.Empty<string>(), 192));
        check("[录屏] 封装：没有音轨 = -c:v copy 直接转容器，不加音频参数",
            videoOnly.Contains("-i tmp.mkv", StringComparison.Ordinal)
            && videoOnly.Contains("-map 0:v -c:v copy", StringComparison.Ordinal)
            && !videoOnly.Contains("-c:a", StringComparison.Ordinal)
            && !videoOnly.Contains("+faststart", StringComparison.Ordinal)
            && videoOnly.EndsWith("out.avi", StringComparison.Ordinal),
            videoOnly);

        var oneAudio = string.Join(" ", FfmpegArguments.BuildRemux(
            "tmp.mkv", "out.mp4", "mp4", new[] { "mic.wav" }, 192));
        check("[录屏] 封装：单路音频 = 混音滤镜 + AAC + faststart + -shortest",
            oneAudio.Contains("-i mic.wav", StringComparison.Ordinal)
            && oneAudio.Contains("aresample=48000:async=1:first_pts=0", StringComparison.Ordinal)
            && oneAudio.Contains("alimiter=limit=0.98,apad", StringComparison.Ordinal)
            && oneAudio.Contains("-map 0:v", StringComparison.Ordinal)
            && oneAudio.Contains("-map [a]", StringComparison.Ordinal)
            && oneAudio.Contains("-c:v copy", StringComparison.Ordinal)
            && oneAudio.Contains("-c:a aac -b:a 192k", StringComparison.Ordinal)
            && oneAudio.Contains("-movflags +faststart", StringComparison.Ordinal)
            && oneAudio.Contains("-shortest", StringComparison.Ordinal),
            oneAudio);

        var delayed = string.Join(" ", FfmpegArguments.BuildRemux(
            "tmp.mkv", "out.mp4", "mp4", new[] { "mic.wav" }, 192, "libx264", 120));
        check("[录屏] 封装：音画延迟补偿 = adelay 补静音（声音比画面晚启动 120ms）",
            delayed.Contains("adelay=120|120,aresample=48000", StringComparison.Ordinal),
            delayed);

        var twoAudio = string.Join(" ", FfmpegArguments.BuildRemux(
            "tmp.mkv", "out.webm", "webm", new[] { "mic.wav", "speaker.wav" }, 160));
        check("[录屏] 封装：双路音频 = amix normalize=0；WebM 用 Opus",
            twoAudio.Contains("amix=inputs=2:duration=longest:normalize=0", StringComparison.Ordinal)
            && twoAudio.Contains("-c:a libopus -b:a 160k", StringComparison.Ordinal)
            && twoAudio.Contains("-i speaker.wav", StringComparison.Ordinal),
            twoAudio);

        var aviAudio = string.Join(" ", FfmpegArguments.BuildRemux(
            "tmp.mkv", "out.avi", "avi", new[] { "mic.wav" }, 128));        check("[录屏] 容器音频编码：AVI 用 MP3、WebM 用 Opus、MP4/MKV 用 AAC",
            aviAudio.Contains("-c:a libmp3lame", StringComparison.Ordinal)
            && VideoCatalog.AudioCodecLabel("webm") == "Opus"
            && VideoCatalog.AudioCodecLabel("avi") == "MP3"
            && VideoCatalog.AudioCodecLabel("mkv") == "AAC",
            aviAudio);

        var hevc = string.Join(" ", FfmpegArguments.BuildRemux(
            "tmp.mkv", "out.mp4", "mp4", Array.Empty<string>(), 192, "libx265"));
        check("[录屏] 封装：H.265 进 MP4/MOV 时加 hvc1 tag（苹果 / 剪辑软件兼容）",
            hevc.Contains("-tag:v hvc1", StringComparison.Ordinal),
            hevc);
    }

    private static void CheckEncoderDetection(Action<string, bool, string> check)
    {
        const string fake = """
             Encoders:
             V....D libx264              libx264 H.264 / AVC (codec h264)
             V....D h264_nvenc           NVIDIA NVENC H.264 encoder (codec h264)
             A....D aac                  AAC (Advanced Audio Coding)
             ------
            """;
        var set = EncoderCatalog.ParseAvailable(fake);
        check("[录屏] 编码器检测：从 ffmpeg -encoders 输出解析出编码器名",
            set.Contains("libx264") && set.Contains("h264_nvenc") && set.Contains("aac") && set.Count == 3,
            string.Join(",", set));
        check("[录屏] 编码器检测：空 / 乱输入安全返回空集合",
            EncoderCatalog.ParseAvailable(null).Count == 0
            && EncoderCatalog.ParseAvailable("").Count == 0
            && EncoderCatalog.ParseAvailable("Encoders:\n------").Count == 0,
            "null / empty / no encoders");
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
            && config.MaxSeconds == 0 && config.ShowControlBar,
            $"{config.SourceKind}/{config.Format}/{config.Fps}/{config.ScaleWidth}/{config.CountdownSeconds}");

        check("[录屏] 配置默认值：视频编码器 / 画质 / 声音 / 摄像头 / 快捷键",
            config.VideoCodec == "libx264" && config.VideoRateMode == "quality"
            && config.VideoQuality == 23 && config.VideoBitrateMbps == 8
            && !config.RecordMicrophone && config.MicrophoneVolume == 100
            && !config.RecordSystemAudio && config.SpeakerVolume == 100
            && config.AudioBitrateKbps == 192
            && !config.EnableCamera && config.CameraMirror && !config.CameraOverlayEnabled
            && config.CameraOverlayWidth == 320 && config.CameraOverlayOpacity == 100
            && config.EnableGlobalHotkeys && config.StartStopHotkey == "F8"
            && config.ScreenshotHotkey == "",
            $"{config.VideoCodec}/{config.VideoQuality}/{config.AudioBitrateKbps}/{config.StartStopHotkey}");

        check("[录屏] 配置计算属性：IsGif / IsMp4 / IsVideo / HasRegion / 来源判断正确",
            config.IsGif && !config.IsMp4 && !config.IsVideo && !config.HasRegion
            && config.IsRegionSource && !config.IsWindowSource && !config.IsFullScreenSource,
            $"{config.IsGif}/{config.IsMp4}/{config.IsVideo}/{config.HasRegion}");

        config.Format = "mp4";
        config.SourceKind = "window";
        config.RegionWidth = 800;
        config.RegionHeight = 600;
        check("[录屏] 配置计算属性随字段变化（MP4 / 窗口 / 有区域）",
            config.IsMp4 && !config.IsGif && config.IsVideo && config.IsWindowSource
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
        watched.EnableGlobalHotkeys = false;
        watched.OutputFolder = @"C:\录屏";
        check("[录屏] 配置联动属性会发 PropertyChanged（否则 OnConfigChanged 是死代码）",
            changed.Contains(nameof(ScreenRecorderConfig.SourceKind))
            && changed.Contains(nameof(ScreenRecorderConfig.Format))
            && changed.Contains(nameof(ScreenRecorderConfig.Fps))
            && changed.Contains(nameof(ScreenRecorderConfig.ScaleWidth))
            && changed.Contains(nameof(ScreenRecorderConfig.GifColors))
            && changed.Contains(nameof(ScreenRecorderConfig.MaxSeconds))
            && changed.Contains(nameof(ScreenRecorderConfig.EnableGlobalHotkeys))
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
            VideoQuality = 200,
            VideoRateMode = "乱写",
            VideoBitrateMbps = 999,
            AudioBitrateKbps = 130,
            MicrophoneVolume = 999,
            SpeakerVolume = -5,
            CameraOverlayWidth = 9999,
            CameraOverlayOpacity = 5,
            CountdownSeconds = 7,
            MaxSeconds = 45,
            FilePrefix = "a:b*c",
        };
        RecorderOptions.NormalizeConfig(broken);
        check("[录屏] 配置归一化：坏值都会吸附 / 夹取到合法范围",
            broken.SourceKind == "region" && broken.Format == "webm"
            && broken.Fps == 5 && broken.ScaleWidth == 960 && broken.GifColors == 256
            && broken.GifDither == "floyd_steinberg"
            && broken.VideoQuality == 51 && broken.VideoRateMode == VideoEncoderSettings.RateQuality
            && broken.VideoBitrateMbps == 100 && broken.AudioBitrateKbps == 128
            && broken.MicrophoneVolume == 200 && broken.SpeakerVolume == 0
            && broken.CameraOverlayWidth == 800 && broken.CameraOverlayOpacity == 20
            && broken.CountdownSeconds == 5 && broken.MaxSeconds == 30
            && broken.FilePrefix == "a_b_c",
            $"{broken.SourceKind}/{broken.Format}/{broken.Fps}/{broken.ScaleWidth}/{broken.GifColors}/{broken.VideoQuality}/{broken.AudioBitrateKbps}/{broken.MicrophoneVolume}/{broken.CameraOverlayOpacity}");

        check("[录屏] 配置归一化：WebM 下不兼容的 libx264 自动换成 VP9",
            broken.Format == "webm" && broken.VideoCodec == "libvpx-vp9",
            broken.VideoCodec);

        // 正数不能吸附成 0（0 的含义是“原始分辨率 / 不限制”，会把 2 秒录制变成无限录制）
        var positive = new ScreenRecorderConfig { ScaleWidth = 320, MaxSeconds = 2 };
        RecorderOptions.NormalizeConfig(positive);
        check("[录屏] 配置归一化：正数宽度 / 最长录制不会吸附成 0（避免 2 秒变不限、320 变原始）",
            positive.ScaleWidth == 480 && positive.MaxSeconds == 30,
            $"width={positive.ScaleWidth} max={positive.MaxSeconds}");
    }
}
