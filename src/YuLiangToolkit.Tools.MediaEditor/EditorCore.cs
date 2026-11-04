using System.Globalization;
using System.Text.Json;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ScreenRecorder;

namespace YuLiangToolkit.Tools.MediaEditor;

/// <summary>ffprobe 探测出来的媒体基本信息（纯数据，自检可直接断言解析结果）。</summary>
public sealed record MediaInfo(
    string FilePath,
    string FileName,
    double DurationSeconds,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    bool HasAudio,
    string AudioCodec,
    long FileSizeBytes)
{
    /// <summary>是不是 GIF（按扩展名 / 视频编码判断）。</summary>
    public bool IsGif
        => string.Equals(Path.GetExtension(FilePath), ".gif", StringComparison.OrdinalIgnoreCase)
           || string.Equals(VideoCodec, "gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>时长文案。</summary>
    public string DurationText => EditorTime.Format(DurationSeconds);

    /// <summary>分辨率文案。</summary>
    public string ResolutionText => Width > 0 && Height > 0 ? $"{Width}×{Height}" : "?";

    /// <summary>帧率文案。</summary>
    public string FpsText => Fps > 0 ? $"{Fps:0.##} FPS" : "? FPS";

    /// <summary>文件大小文案。</summary>
    public string SizeText => FileSize.Format(FileSizeBytes);

    /// <summary>音频文案。</summary>
    public string AudioText => HasAudio ? (string.IsNullOrWhiteSpace(AudioCodec) ? "有音轨" : AudioCodec) : "无音轨";
}

/// <summary>
/// ffprobe JSON 解析（纯函数）。只依赖 <see cref="JsonDocument"/>，自检可喂固定 JSON。
/// </summary>
public static class MediaProbe
{
    /// <summary>解析 <c>ffprobe -print_format json -show_format -show_streams</c> 的输出；失败返回 null。</summary>
    public static MediaInfo? ParseJson(string? json, string filePath)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            JsonElement? video = null;
            JsonElement? audio = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var codecType)) continue;
                    var type = codecType.GetString();
                    if (type == "video" && video is null) video = stream;
                    else if (type == "audio" && audio is null) audio = stream;
                }
            }

            if (video is null) return null;

            var width = GetInt(video.Value, "width");
            var height = GetInt(video.Value, "height");
            if (width <= 0 || height <= 0) return null;

            var duration = GetDouble(root, "format", "duration");
            if (duration <= 0) duration = GetDouble(video.Value, "duration");
            if (duration <= 0) duration = GetDouble(root, "format", "duration");
            if (duration <= 0) return null;

            var fps = ParseRational(GetString(video.Value, "avg_frame_rate"));
            if (fps <= 0) fps = ParseRational(GetString(video.Value, "r_frame_rate"));

            var videoCodec = GetString(video.Value, "codec_name") ?? "";
            var hasAudio = audio is not null;
            var audioCodec = hasAudio ? GetString(audio!.Value, "codec_name") ?? "" : "";

            long size = 0;
            try { size = new FileInfo(filePath).Length; } catch { /* 文件可能已被移走 */ }

            return new MediaInfo(
                filePath,
                Path.GetFileName(filePath),
                duration,
                width,
                height,
                fps,
                videoCodec,
                hasAudio,
                audioCodec,
                size);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static int GetInt(JsonElement element, string name)
        => int.TryParse(GetString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double GetDouble(JsonElement element, string name)
        => double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double GetDouble(JsonElement element, string parent, string name)
        => element.TryGetProperty(parent, out var child) ? GetDouble(child, name) : 0;

    private static double ParseRational(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
            return numerator / denominator;

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}

/// <summary>一个要保留的片段（秒；End 不含）。</summary>
public readonly record struct SegmentRange(double Start, double End)
{
    /// <summary>片段长度（秒）。</summary>
    public double Duration => Math.Max(0, End - Start);

    /// <summary>是否是一个可用的片段（至少 0.05 秒）。</summary>
    public bool IsValid => Duration >= EditorMath.MinSegmentSeconds;

    /// <summary>界面文案。</summary>
    public override string ToString() => $"{EditorTime.Format(Start)} → {EditorTime.Format(End)}";
}

/// <summary>片段 / 时间轴相关的纯数学（自检直接断言）。</summary>
public static class EditorMath
{
    /// <summary>片段最短长度（秒）：太短的片段 FFmpeg 容易出空输出。</summary>
    public const double MinSegmentSeconds = 0.05;

    /// <summary>
    /// 把用户输入的起止时间夹进 [0, duration]，并保证最短长度。
    /// 无效（NaN / 起止颠倒 / 太短）返回 null。
    /// </summary>
    public static SegmentRange? NormalizeSegment(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || !double.IsFinite(duration) || duration <= 0) return null;

        var from = Math.Clamp(Math.Min(start, end), 0, duration);
        var to = Math.Clamp(Math.Max(start, end), 0, duration);
        if (to - from < MinSegmentSeconds)
        {
            // 允许用户输入“整段”时因浮点误差差一点：补到 0.05 / 总长
            if (from <= 0.0001 && duration - to <= 0.0001) return new SegmentRange(0, duration);
            return null;
        }

        return new SegmentRange(
            Math.Round(from, 3, MidpointRounding.AwayFromZero),
            Math.Round(to, 3, MidpointRounding.AwayFromZero));
    }

    /// <summary>所有保留片段的总时长（秒）。</summary>
    public static double TotalDuration(IEnumerable<SegmentRange> segments)
        => segments.Sum(segment => segment.Duration);
}

/// <summary>时间文案：<c>mm:ss.fff</c> / <c>h:mm:ss.fff</c>；也支持从这些格式解析。</summary>
public static class EditorTime
{
    /// <summary>格式化成 <c>00:03.500</c> / <c>1:02:03.500</c>。</summary>
    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}"
            : $"{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    /// <summary>解析 <c>秒数</c> / <c>mm:ss.fff</c> / <c>h:mm:ss.fff</c>；失败返回 false。</summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        var parts = value.Split(':');
        if (parts.Length > 3) return false;

        double total = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var part)
                || part < 0)
                return false;
            total = total * 60 + part;
        }

        if (!double.IsFinite(total)) return false;
        seconds = total;
        return true;
    }
}

/// <summary>编辑器的下拉档位 / 归一化（纯函数）。</summary>
public static class EditorOptions
{
    /// <summary>输出帧率档位（0 = 保持源）。</summary>
    public static readonly int[] FpsChoices = { 0, 15, 24, 30, 60 };

    /// <summary>输出宽度档位（0 = 原始）。</summary>
    public static readonly int[] WidthChoices = { 0, 1920, 1280, 960, 800, 640, 480 };

    /// <summary>时间轴缩略图数量档位。</summary>
    public static readonly int[] ThumbnailCountChoices = { 4, 8, 12, 16, 20, 24 };

    private static readonly int[] PositiveWidthChoices = { 1920, 1280, 960, 800, 640, 480 };

    /// <summary>帧率：0 或 1~60，并吸附到界面档位。</summary>
    public static int NormalizeFps(int value)
    {
        if (value <= 0) return 0;
        return RecorderOptions.SnapToNearest(Math.Clamp(value, 1, 60), FpsChoices[1..]);
    }

    /// <summary>输出宽度：0 / 正数取偶数，正数吸附到界面档位（不会吸成 0=原始）。</summary>
    public static int NormalizeWidth(int value)
    {
        var width = RecorderOptions.NormalizeScaleWidth(value);
        return width <= 0 ? 0 : RecorderOptions.SnapToNearest(width, PositiveWidthChoices);
    }

    /// <summary>音频码率吸附到 128 / 192 / 256。</summary>
    public static int NormalizeAudioBitrate(int value)
        => RecorderOptions.SnapToNearest(Math.Clamp(value, 64, 320), RecorderOptions.AudioBitrateChoices);

    /// <summary>导出格式归一化：gif 或五种视频容器，乱写回退 mp4（编辑器默认视频）。</summary>
    public static string NormalizeFormat(string? value)
    {
        var text = value?.Trim().ToLowerInvariant();
        if (text == "gif") return "gif";
        return VideoCatalog.IsVideoFormat(text) ? text! : "mp4";
    }

    /// <summary>
    /// 把整个配置归一化：格式 / 帧率 / 宽度 / GIF / 画质码率 / 音频码率 / 缩略图数量 / 前缀。
    /// <para>手改坏 config 时保证界面显示的值就是实际会用的值。</para>
    /// </summary>
    public static void NormalizeConfig(MediaEditorConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.Format = NormalizeFormat(config.Format);
        config.Fps = NormalizeFps(config.Fps);
        config.ScaleWidth = NormalizeWidth(config.ScaleWidth);
        config.GifColors = RecorderOptions.NormalizeColors(config.GifColors);
        config.GifDither = RecorderOptions.NormalizeDither(config.GifDither);
        config.Quality = Math.Clamp(config.Quality, 0, 51);
        config.BitrateMbps = Math.Clamp(config.BitrateMbps, 1, 100);
        config.RateMode = string.Equals(config.RateMode, VideoEncoderSettings.RateBitrate, StringComparison.OrdinalIgnoreCase)
            ? VideoEncoderSettings.RateBitrate
            : VideoEncoderSettings.RateQuality;
        config.AudioBitrateKbps = NormalizeAudioBitrate(config.AudioBitrateKbps);
        config.ThumbnailCount = Math.Clamp(config.ThumbnailCount, 4, 24);
        config.LastInputPath = config.LastInputPath?.Trim() ?? "";
        config.FilePrefix = string.IsNullOrWhiteSpace(config.FilePrefix)
            ? MediaEditorConfig.DefaultFilePrefix
            : RecordingNames.SanitizePrefix(config.FilePrefix);
    }
}

/// <summary>拼 FFmpeg 滤镜 / 参数（纯函数；自检直接断言字符串）。</summary>
public static class MediaEditorArguments
{
    private static List<string> Base() => new() { "-hide_banner", "-loglevel", "error", "-y" };

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// 生成“裁剪 + 拼接 + 可选缩放 / 帧率”的视频滤镜，输出标签固定为 <c>[vout]</c>。
    /// </summary>
    /// <param name="segments">按导出顺序排好的保留片段。</param>
    /// <param name="outputWidth">0 = 原始宽度；大于源宽不放大。</param>
    /// <param name="sourceWidth">源视频宽度。</param>
    /// <param name="fps">0 = 保持源帧率。</param>
    /// <param name="finalFilter">追加在缩放 / 帧率后的滤镜（例如 GIF 的 palettegen）；可为空。</param>
    public static string BuildVideoFilter(
        IReadOnlyList<SegmentRange> segments,
        int outputWidth,
        int sourceWidth,
        int fps,
        string finalFilter = "")
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count == 0) throw new ArgumentException("至少要保留一个片段", nameof(segments));

        var parts = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            parts.Add($"[0:v]trim=start={Num(segment.Start)}:end={Num(segment.End)},setpts=PTS-STARTPTS[v{i}]");
        }

        string previous;
        if (segments.Count == 1)
        {
            previous = "[v0]";
        }
        else
        {
            var labels = string.Concat(Enumerable.Range(0, segments.Count).Select(i => $"[v{i}]"));
            parts.Add($"{labels}concat=n={segments.Count}:v=1:a=0[vc]");
            previous = "[vc]";
        }

        var chain = new List<string>();
        if (fps > 0) chain.Add($"fps={fps}");
        var width = ResolveWidth(outputWidth, sourceWidth);
        if (width < sourceWidth) chain.Add($"scale={width}:-2:flags=lanczos");
        if (!string.IsNullOrWhiteSpace(finalFilter)) chain.Add(finalFilter.Trim());
        if (chain.Count == 0) chain.Add("null");

        parts.Add($"{previous}{string.Join(",", chain)}[vout]");
        return string.Join(";", parts);
    }

    /// <summary>
    /// 生成“裁剪 + 拼接”的音频滤镜，输出标签固定为 <c>[aout]</c>；源没有音轨返回空字符串。
    /// </summary>
    public static string BuildAudioFilter(IReadOnlyList<SegmentRange> segments, bool hasAudio)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (!hasAudio || segments.Count == 0) return "";

        var parts = new List<string>();
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            parts.Add($"[0:a]atrim=start={Num(segment.Start)}:end={Num(segment.End)},asetpts=PTS-STARTPTS[a{i}]");
        }

        if (segments.Count == 1)
        {
            parts.Add("[a0]anull[aout]");
        }
        else
        {
            var labels = string.Concat(Enumerable.Range(0, segments.Count).Select(i => $"[a{i}]"));
            parts.Add($"{labels}concat=n={segments.Count}:v=0:a=1[ac]");
            parts.Add("[ac]anull[aout]");
        }

        return string.Join(";", parts);
    }

    /// <summary>实际输出宽度：0 / 超过源宽 = 源宽；否则取偶数。</summary>
    public static int ResolveWidth(int requested, int sourceWidth)
    {
        if (sourceWidth <= 0) return Math.Max(2, requested);
        if (requested <= 0 || requested >= sourceWidth) return sourceWidth - (sourceWidth % 2);
        var width = requested - (requested % 2);
        return Math.Max(2, width);
    }

    /// <summary>视频导出（单次编码：裁剪 / 拼接 / 缩放 / 帧率 + 可选音轨）。</summary>
    public static IReadOnlyList<string> BuildVideoExport(
        string inputPath,
        string outputPath,
        string containerId,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        VideoEncoderSettings encoder,
        int outputWidth,
        int fps,
        bool keepAudio,
        int audioBitrateKbps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var videoFilter = BuildVideoFilter(segments, outputWidth, media.Width, fps);
        var audioFilter = keepAudio ? BuildAudioFilter(segments, media.HasAudio) : "";
        var filter = audioFilter.Length == 0 ? videoFilter : videoFilter + ";" + audioFilter;

        var args = Base();
        args.AddRange(new[] { "-i", inputPath });
        args.AddRange(new[] { "-filter_complex", filter, "-map", "[vout]" });
        if (audioFilter.Length > 0) args.AddRange(new[] { "-map", "[aout]" });
        args.AddRange(VideoEncodingArgs.Build(encoder));
        if (audioFilter.Length > 0)
            args.AddRange(VideoCatalog.BuildAudioEncodeArgs(containerId, audioBitrateKbps));
        else
            args.Add("-an");

        var container = VideoCatalog.GetContainer(containerId);
        if (VideoCatalog.GetCodec(encoder.CodecId).CodecName == "H.265"
            && (container.Id == "mp4" || container.Id == "mov"))
            args.AddRange(new[] { "-tag:v", "hvc1" });
        if (container.SupportsFastStart) args.AddRange(new[] { "-movflags", "+faststart" });
        args.Add(outputPath);
        return args;
    }

    /// <summary>生成低清预览 MP4（最长 <paramref name="maxSeconds"/> 秒，用于不能实时预览时快速看一眼）。</summary>
    public static IReadOnlyList<string> BuildPreview(
        string inputPath,
        string outputPath,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        int maxSeconds = 10)
    {
        var args = BuildVideoExport(
            inputPath, outputPath, "mp4", segments, media,
            VideoEncoderSettings.Normalize("libx264", VideoEncoderSettings.RateQuality, 28, 8),
            outputWidth: 480,
            fps: 0,
            keepAudio: media.HasAudio,
            audioBitrateKbps: 128);

        // `-t` 放在输出文件前：限制预览长度，同时保留 audio/faststart 参数
        var result = new List<string>(args);
        var outputIndex = result.Count - 1;
        result.Insert(outputIndex, "-t");
        result.Insert(outputIndex + 1, Math.Max(1, maxSeconds).ToString(CultureInfo.InvariantCulture));
        return result;
    }

    /// <summary>GIF 第一遍：生成调色板 PNG。</summary>
    public static IReadOnlyList<string> BuildGifPalette(
        string inputPath,
        string palettePath,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        GifExportSettings gif)
    {
        var videoFilter = BuildVideoFilter(
            segments,
            gif.ScaleWidth,
            media.Width,
            gif.Fps,
            $"palettegen=max_colors={gif.MaxColors}:stats_mode=diff");

        var args = Base();
        args.AddRange(new[] { "-i", inputPath, "-filter_complex", videoFilter, "-map", "[vout]", "-frames:v", "1", palettePath });
        return args;
    }

    /// <summary>GIF 第二遍：中间素材 + 调色板 → 最终 GIF。</summary>
    public static IReadOnlyList<string> BuildGif(
        string inputPath,
        string palettePath,
        string outputPath,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        GifExportSettings gif)
    {
        var videoFilter = BuildVideoFilter(segments, gif.ScaleWidth, media.Width, gif.Fps);
        var filter = $"{videoFilter};[vout][1:v]paletteuse=dither={gif.Dither}:diff_mode=rectangle[gif]";

        var args = Base();
        args.AddRange(new[] { "-i", inputPath, "-i", palettePath, "-filter_complex", filter, "-map", "[gif]" });
        args.AddRange(new[] { "-loop", gif.Loop ? "0" : "-1", outputPath });
        return args;
    }

    /// <summary>导出扩展名（gif / 视频容器）。</summary>
    public static string OutputExtension(string format)
        => string.Equals(format, "gif", StringComparison.OrdinalIgnoreCase)
            ? "gif"
            : VideoCatalog.GetContainer(format).Extension;
}

/// <summary>
/// 编辑结果 / 预览的导出执行（调用系统 FFmpeg；失败返回 FFmpeg 尾部错误）。
/// </summary>
public sealed record EditorExportResult(bool Success, string OutputPath, string Error);
