using System.Text.Json;
using YuLiangToolkit.Tools.MediaEditor;
using YuLiangToolkit.Tools.MediaEditor.ViewModels;
using YuLiangToolkit.Tools.ScreenRecorder;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「剪辑工具」自检（media-editor 组）：ffprobe JSON 解析 / 时间格式 / 片段数学 /
/// 裁剪拼接滤镜 / GIF 两遍参数 / 预览参数 / 配置归一化 / 工具元数据。
///
/// <para><b>不启动 FFmpeg、不做真实剪辑、不碰真实 Data/Config</b>：真正的剪辑与预览由人工冒烟验收。</para>
/// </summary>
internal static class MediaEditorChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckMetadata(check);
        CheckProbe(check);
        CheckTime(check);
        CheckSegments(check);
        CheckFilters(check);
        CheckExports(check);
        CheckOptions(check);
        CheckConfig(check);
        CheckDrop(check);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new MediaEditorTool();
        check("[剪辑] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "media-editor" && tool.Name == "剪辑工具"
            && tool.Category == "媒体" && tool.Order == 12,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[剪辑] 标签 / 描述覆盖视频、GIF、裁剪、拼接、预览",
            tool.Tags.Contains("视频剪辑") && tool.Tags.Contains("GIF剪辑")
            && tool.Tags.Contains("裁剪") && tool.Tags.Contains("拼接") && tool.Tags.Contains("预览")
            && tool.Description.Contains("GIF") && tool.Description.Contains("预览"),
            tool.Description);
    }

    // ---------------------------------------------------------------- ffprobe 解析

    private static void CheckProbe(Action<string, bool, string> check)
    {
        const string videoJson = """
            {
              "streams": [
                { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "avg_frame_rate": "30000/1001" },
                { "codec_type": "audio", "codec_name": "aac" }
              ],
              "format": { "duration": "12.345", "size": "1024" }
            }
            """;
        var media = MediaProbe.ParseJson(videoJson, @"C:\media\clip.mp4");
        check("[剪辑] ffprobe 解析：时长 / 分辨率 / 帧率（30000/1001）/ 音轨",
            media is not null
            && Math.Abs(media.DurationSeconds - 12.345) < 0.001
            && media.Width == 1920 && media.Height == 1080
            && Math.Abs(media.Fps - 29.97) < 0.02
            && media.HasAudio && media.AudioCodec == "aac"
            && media.VideoCodec == "h264",
            media is null ? "null" : $"{media.DurationText} / {media.ResolutionText} / {media.Fps:0.##}");

        const string gifJson = """
            { "streams": [ { "codec_type": "video", "codec_name": "gif", "width": 320, "height": 240, "avg_frame_rate": "10/1" } ],
              "format": { "duration": "2.5" } }
            """;
        var gif = MediaProbe.ParseJson(gifJson, @"C:\media\a.gif");
        check("[剪辑] ffprobe 解析：GIF 识别 / 无音轨 / 扩展名判断",
            gif is not null && gif.IsGif && !gif.HasAudio && gif.AudioText == "无音轨",
            gif?.VideoCodec ?? "null");

        check("[剪辑] ffprobe 解析：坏 JSON / 没有视频流 / 没有时长都返回 null",
            MediaProbe.ParseJson(null, "a.mp4") is null
            && MediaProbe.ParseJson("not json", "a.mp4") is null
            && MediaProbe.ParseJson("""{ "streams": [], "format": { "duration": "5" } }""", "a.mp4") is null
            && MediaProbe.ParseJson("""{ "streams": [ { "codec_type": "video", "width": 320, "height": 240 } ] }""", "a.mp4") is null,
            "null / 坏 JSON / 无视频 / 无时长");
    }

    // ---------------------------------------------------------------- 时间 / 片段数学

    private static void CheckTime(Action<string, bool, string> check)
    {
        check("[剪辑] 时间格式：mm:ss.fff / h:mm:ss.fff",
            EditorTime.Format(3.5) == "00:03.500"
            && EditorTime.Format(3723.5) == "1:02:03.500"
            && EditorTime.Format(-1) == "00:00.000",
            EditorTime.Format(3723.5));

        check("[剪辑] 时间解析：秒数 / mm:ss.fff / h:mm:ss.fff 都认，坏值返回 false",
            EditorTime.TryParse("12.5", out var a) && Math.Abs(a - 12.5) < 0.001
            && EditorTime.TryParse("00:12.500", out var b) && Math.Abs(b - 12.5) < 0.001
            && EditorTime.TryParse("1:02:03.5", out var c) && Math.Abs(c - 3723.5) < 0.001
            && !EditorTime.TryParse("abc", out _)
            && !EditorTime.TryParse("", out _)
            && !EditorTime.TryParse("1:2:3:4", out _),
            "12.5 / 00:12.500 / 1:02:03.5 / abc");
    }

    private static void CheckSegments(Action<string, bool, string> check)
    {
        check("[剪辑] 片段归一化：夹到 [0, 总长]、起止颠倒自动交换、保留 3 位小数",
            EditorMath.NormalizeSegment(1, 3, 10) == new SegmentRange(1, 3)
            && EditorMath.NormalizeSegment(5, 2, 10) == new SegmentRange(2, 5)
            && EditorMath.NormalizeSegment(-1, 100, 10) == new SegmentRange(0, 10)
            && EditorMath.NormalizeSegment(1.23456, 2.34567, 10) == new SegmentRange(1.235, 2.346),
            "1..3 / 5..2 / -1..100 / 小数");

        check("[剪辑] 片段归一化：太短 / 无效时长返回 null；整段浮点误差可容忍",
            EditorMath.NormalizeSegment(1, 1.01, 10) is null
            && EditorMath.NormalizeSegment(0, 0, 0) is null
            && EditorMath.NormalizeSegment(double.NaN, 2, 10) is null
            && EditorMath.NormalizeSegment(0, 9.99999, 10) == new SegmentRange(0, 10),
            "1..1.01 / 0 时长 / NaN / 整段");

        var total = EditorMath.TotalDuration(new[] { new SegmentRange(0, 1.5), new SegmentRange(2, 3.25) });
        check("[剪辑] 片段总时长 = 各段长度之和",
            Math.Abs(total - 2.75) < 0.001,
            total.ToString("0.###"));
    }

    // ---------------------------------------------------------------- 滤镜 / 参数

    private static void CheckFilters(Action<string, bool, string> check)
    {
        var media = MakeMedia(duration: 10);
        var segments = new[] { new SegmentRange(1, 3), new SegmentRange(5, 8) };

        var video = MediaEditorArguments.BuildVideoFilter(segments, 1280, 1920, 30);
        check("[剪辑] 视频滤镜：多段 trim + setpts + concat + fps + scale + [vout]",
            video.Contains("[0:v]trim=start=1:end=3,setpts=PTS-STARTPTS[v0]", StringComparison.Ordinal)
            && video.Contains("[0:v]trim=start=5:end=8,setpts=PTS-STARTPTS[v1]", StringComparison.Ordinal)
            && video.Contains("[v0][v1]concat=n=2:v=1:a=0[vc]", StringComparison.Ordinal)
            && video.Contains("[vc]fps=30,scale=1280:-2:flags=lanczos[vout]", StringComparison.Ordinal),
            video);

        var single = MediaEditorArguments.BuildVideoFilter(new[] { new SegmentRange(0, 10) }, 0, 1920, 0);
        check("[剪辑] 视频滤镜：单段 / 不缩放 / 不改帧率 = [v0]null[vout]",
            single.Contains("[v0]null[vout]", StringComparison.Ordinal),
            single);

        var audio = MediaEditorArguments.BuildAudioFilter(segments, hasAudio: true);
        check("[剪辑] 音频滤镜：多段 atrim + concat + [aout]；无音轨返回空",
            audio.Contains("[0:a]atrim=start=1:end=3,asetpts=PTS-STARTPTS[a0]", StringComparison.Ordinal)
            && audio.Contains("[0:a]atrim=start=5:end=8,asetpts=PTS-STARTPTS[a1]", StringComparison.Ordinal)
            && audio.Contains("[a0][a1]concat=n=2:v=0:a=1[ac]", StringComparison.Ordinal)
            && audio.Contains("[ac]anull[aout]", StringComparison.Ordinal)
            && MediaEditorArguments.BuildAudioFilter(segments, hasAudio: false) == "",
            audio);

        var palette = MediaEditorArguments.BuildVideoFilter(segments, 480, 1920, 10,
            "palettegen=max_colors=128:stats_mode=diff");
        check("[剪辑] GIF 调色板滤镜：fps + scale + palettegen 链在最后",
            palette.Contains("fps=10,scale=480:-2:flags=lanczos,palettegen=max_colors=128:stats_mode=diff[vout]", StringComparison.Ordinal),
            palette);
    }

    private static void CheckExports(Action<string, bool, string> check)
    {
        var media = MakeMedia(duration: 10);
        var segments = new[] { new SegmentRange(1, 3), new SegmentRange(5, 8) };
        var encoder = VideoEncoderSettings.Normalize("libx264", VideoEncoderSettings.RateQuality, 23, 8);
        var video = string.Join(" ", MediaEditorArguments.BuildVideoExport(
            @"C:\in.mp4", @"C:\out.mp4", "mp4", segments, media, encoder, 1280, 30, keepAudio: true, audioBitrateKbps: 192));

        check("[剪辑] 视频导出参数：filter_complex + map v/a + libx264 CRF23 + AAC + faststart",
            video.Contains("-filter_complex", StringComparison.Ordinal)
            && video.Contains("-map [vout]", StringComparison.Ordinal)
            && video.Contains("-map [aout]", StringComparison.Ordinal)
            && video.Contains("-c:v libx264", StringComparison.Ordinal)
            && video.Contains("-crf 23", StringComparison.Ordinal)
            && video.Contains("-c:a aac -b:a 192k", StringComparison.Ordinal)
            && video.Contains("-movflags +faststart", StringComparison.Ordinal)
            && video.EndsWith(@"C:\out.mp4", StringComparison.Ordinal),
            video);

        var muted = string.Join(" ", MediaEditorArguments.BuildVideoExport(
            @"C:\in.mp4", @"C:\out.avi", "avi", segments, media, encoder, 0, 0, keepAudio: false, audioBitrateKbps: 192));
        check("[剪辑] 视频导出：关掉音轨 = -an、不 map [aout]；AVI 不加 faststart",
            muted.Contains("-an", StringComparison.Ordinal)
            && !muted.Contains("[aout]", StringComparison.Ordinal)
            && !muted.Contains("+faststart", StringComparison.Ordinal)
            && muted.EndsWith(@"C:\out.avi", StringComparison.Ordinal),
            muted);

        var noAudioMedia = MakeMedia(duration: 10, hasAudio: false);
        var noAudio = string.Join(" ", MediaEditorArguments.BuildVideoExport(
            @"C:\in.gif", @"C:\out.webm", "webm", segments, noAudioMedia, encoder, 0, 0, keepAudio: true, audioBitrateKbps: 192));
        check("[剪辑] 视频导出：源无音轨时不生成音频滤镜、自动 -an",
            noAudio.Contains("-an", StringComparison.Ordinal) && !noAudio.Contains("[aout]", StringComparison.Ordinal),
            noAudio);

        var hevc = string.Join(" ", MediaEditorArguments.BuildVideoExport(
            @"C:\in.mp4", @"C:\out.mp4", "mp4", segments, media,
            VideoEncoderSettings.Normalize("libx265", VideoEncoderSettings.RateQuality, 23, 8), 0, 0, keepAudio: true, audioBitrateKbps: 192));
        check("[剪辑] H.265 进 MP4 自动加 hvc1 标记（苹果 / 剪辑软件兼容）",
            hevc.Contains("-c:v libx265", StringComparison.Ordinal)
            && hevc.Contains("-tag:v hvc1", StringComparison.Ordinal),
            hevc);

        var preview = string.Join(" ", MediaEditorArguments.BuildPreview(
            @"C:\in.mp4", @"C:\cache\preview.mp4", segments, media));
        check("[剪辑] 预览参数：480 宽 + libx264 CRF28 ultrafast + 最多 10 秒 + faststart",
            preview.Contains("scale=480:-2:flags=lanczos", StringComparison.Ordinal)
            && preview.Contains("-c:v libx264", StringComparison.Ordinal)
            && preview.Contains("-preset veryfast", StringComparison.Ordinal)
            && preview.Contains("-crf 28", StringComparison.Ordinal)
            && preview.Contains("-t 10", StringComparison.Ordinal)
            && preview.Contains("-movflags +faststart", StringComparison.Ordinal),
            preview);

        var gif = GifExportSettings.Normalize(10, 480, 128, "bayer", true);
        var palette = string.Join(" ", MediaEditorArguments.BuildGifPalette(
            @"C:\in.mp4", @"C:\cache\palette.png", segments, media, gif));
        check("[剪辑] GIF 第一遍：palettegen + 一帧调色板",
            palette.Contains("palettegen=max_colors=128:stats_mode=diff", StringComparison.Ordinal)
            && palette.Contains("-frames:v 1", StringComparison.Ordinal)
            && palette.EndsWith("palette.png", StringComparison.Ordinal),
            palette);

        var gifArgs = string.Join(" ", MediaEditorArguments.BuildGif(
            @"C:\in.mp4", @"C:\cache\palette.png", @"C:\out.gif", segments, media, gif));
        check("[剪辑] GIF 第二遍：paletteuse + 循环 + 输出 gif",
            gifArgs.Contains("[vout][1:v]paletteuse=dither=bayer:diff_mode=rectangle[gif]", StringComparison.Ordinal)
            && gifArgs.Contains("-loop 0", StringComparison.Ordinal)
            && gifArgs.EndsWith(@"C:\out.gif", StringComparison.Ordinal),
            gifArgs);

        check("[剪辑] 导出扩展名：gif / 容器各自正确",
            MediaEditorArguments.OutputExtension("gif") == "gif"
            && MediaEditorArguments.OutputExtension("mp4") == "mp4"
            && MediaEditorArguments.OutputExtension("avi") == "avi"
            && MediaEditorArguments.OutputExtension("webm") == "webm",
            "gif/mp4/avi/webm");
    }

    // ---------------------------------------------------------------- 归一化

    private static void CheckOptions(Action<string, bool, string> check)
    {
        check("[剪辑] 输出帧率：0 = 源；其它夹到 1~60 并吸附到 15/24/30/60",
            EditorOptions.NormalizeFps(0) == 0
            && EditorOptions.NormalizeFps(7) == 15
            && EditorOptions.NormalizeFps(45) == 30
            && EditorOptions.NormalizeFps(120) == 60,
            "0 / 7 / 45 / 120");

        check("[剪辑] 输出宽度：0 = 原始；正数取偶数并吸附档位、不吸成 0",
            EditorOptions.NormalizeWidth(0) == 0
            && EditorOptions.NormalizeWidth(961) == 960
            && EditorOptions.NormalizeWidth(320) == 480,
            "0 / 961 / 320");

        check("[剪辑] 音频码率吸附到 128/192/256；格式解析认 gif / 容器、乱写回退 mp4",
            EditorOptions.NormalizeAudioBitrate(130) == 128
            && EditorOptions.NormalizeAudioBitrate(999) == 256
            && EditorOptions.NormalizeFormat("gif") == "gif"
            && EditorOptions.NormalizeFormat("WEBM") == "webm"
            && EditorOptions.NormalizeFormat("乱写") == "mp4",
            "130 / 999 / gif / WEBM / 乱写");
    }

    private static void CheckConfig(Action<string, bool, string> check)
    {
        var config = new MediaEditorConfig();
        check("[剪辑] 配置默认值：MP4 / libx264 / 画质 CRF23 / 保持源帧率 / 原始宽度 / 保留音轨 / 12 张缩略图",
            config.Format == "mp4" && config.VideoCodec == "libx264"
            && config.RateMode == VideoEncoderSettings.RateQuality && config.Quality == 23
            && config.Fps == 0 && config.ScaleWidth == 0
            && config.KeepAudio && config.AudioBitrateKbps == 192
            && config.ThumbnailCount == 12 && config.FilePrefix == "剪辑"
            && config.IsVideo && !config.IsGif,
            $"{config.Format}/{config.VideoCodec}/{config.Quality}/{config.ThumbnailCount}");

        check("[剪辑] 配置联动属性会发 PropertyChanged（格式切换 / 编码器联动不失效）",
            PropertyChangedFires(config, c => c.Format = "gif")
            && PropertyChangedFires(config, c => c.VideoCodec = "libx265")
            && PropertyChangedFires(config, c => c.Fps = 30)
            && PropertyChangedFires(config, c => c.ThumbnailCount = 20),
            "Format / VideoCodec / Fps / ThumbnailCount");

        var broken = new MediaEditorConfig
        {
            Format = "乱写",
            Fps = 7,
            ScaleWidth = 1000,
            GifColors = 999,
            GifDither = "乱写",
            Quality = 200,
            BitrateMbps = 999,
            RateMode = "乱写",
            AudioBitrateKbps = 130,
            ThumbnailCount = 1,
            FilePrefix = "a:b*c",
        };
        EditorOptions.NormalizeConfig(broken);
        check("[剪辑] 配置归一化：坏值都会夹取 / 吸附，前缀非法字符替换",
            broken.Format == "mp4" && broken.Fps == 15 && broken.ScaleWidth == 960
            && broken.GifColors == 256 && broken.GifDither == "floyd_steinberg"
            && broken.Quality == 51 && broken.BitrateMbps == 100
            && broken.RateMode == VideoEncoderSettings.RateQuality
            && broken.AudioBitrateKbps == 128 && broken.ThumbnailCount == 4
            && broken.FilePrefix == "a_b_c",
            $"{broken.Format}/{broken.Fps}/{broken.ScaleWidth}/{broken.GifColors}/{broken.Quality}/{broken.AudioBitrateKbps}/{broken.ThumbnailCount}");

        var gifConfig = new MediaEditorConfig { Format = "gif" };
        EditorOptions.NormalizeConfig(gifConfig);
        check("[剪辑] 配置归一化：GIF 格式保持 gif，不被错误回退成视频",
            gifConfig.Format == "gif" && gifConfig.IsGif && !gifConfig.IsVideo,
            gifConfig.Format);
    }

    // ---------------------------------------------------------------- 拖入载入

    private static void CheckDrop(Action<string, bool, string> check)
    {
        check("[剪辑] IsSupportedInputPath：后缀白名单忽略大小写（含 .GIF / .MP4），其它后缀 / 无后缀 / 空 / null 拒绝",
            MediaEditorViewModel.IsSupportedInputPath(@"C:\media\clip.mp4")
            && MediaEditorViewModel.IsSupportedInputPath(@"C:\media\clip.MP4")
            && MediaEditorViewModel.IsSupportedInputPath(@"C:\media\动图.Gif")
            && MediaEditorViewModel.IsSupportedInputPath(@"C:\media\clip.webm")
            && MediaEditorViewModel.IsSupportedInputPath(@"C:\media\clip.mpeg")
            && !MediaEditorViewModel.IsSupportedInputPath(@"C:\media\clip.txt")
            && !MediaEditorViewModel.IsSupportedInputPath(@"C:\media\clip.mp4x")
            && !MediaEditorViewModel.IsSupportedInputPath("no-extension")
            && !MediaEditorViewModel.IsSupportedInputPath("")
            && !MediaEditorViewModel.IsSupportedInputPath("   ")
            && !MediaEditorViewModel.IsSupportedInputPath(null),
            "mp4 / MP4 / Gif / webm / mpeg / txt / mp4x / 无后缀 / 空 / null");

        // 拖入只认「文件真实存在 + 后缀受支持」：不存在的一律返回 false。
        // CanAcceptDrop 是静态判定，自检不需要（也不应该）构造 ViewModel —— 那样会触发后台 FFmpeg 探测。
        var missing = Path.Combine(Path.GetTempPath(), "ylt-media-editor-不存在的文件.mp4");
        check("[剪辑] CanAcceptDrop：文件不存在返回 false（空路径 / null / 后缀不支持也一样拒绝）",
            !MediaEditorViewModel.CanAcceptDrop(missing)
            && !MediaEditorViewModel.CanAcceptDrop(Path.Combine(Path.GetTempPath(), "ylt-media-editor-不存在的文件.txt"))
            && !MediaEditorViewModel.CanAcceptDrop("")
            && !MediaEditorViewModel.CanAcceptDrop(null),
            missing);
    }

    private static bool PropertyChangedFires(MediaEditorConfig config, Action<MediaEditorConfig> change)
    {
        var fired = false;
        config.PropertyChanged += (_, _) => fired = true;
        change(config);
        return fired;
    }

    private static MediaInfo MakeMedia(double duration, bool hasAudio = true)
        => new(@"C:\media\clip.mp4", "clip.mp4", duration, 1920, 1080, 30, "h264", hasAudio, hasAudio ? "aac" : "", 1024);
}
