using System.Globalization;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.ScreenRecorder;

/// <summary>一个视频容器（文件格式）。</summary>
public sealed record VideoContainerInfo(string Id, string Label, string Extension, bool SupportsFastStart)
{
    /// <summary>给界面下拉框 / 录制结果用的短标签（去掉括号说明）。</summary>
    public string ShortLabel => Label.Split('（')[0].Trim();
}

/// <summary>一种视频编码器。</summary>
public sealed record VideoCodecInfo(
    string Id,
    string Label,
    string CodecName,
    bool Hardware,
    IReadOnlyList<string> Containers)
{
    /// <summary>该编码器能不能放进指定容器。</summary>
    public bool Fits(string containerId)
        => Containers.Any(c => string.Equals(c, containerId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 视频容器 / 编码器目录 + 兼容性（纯逻辑，自检直接断言）。
/// <para>不依赖 FFmpeg 实际能力；运行时再用 <see cref="EncoderCatalog.ParseAvailable"/> 过滤掉本机 ffmpeg 没有的编码器。</para>
/// </summary>
public static class VideoCatalog
{
    /// <summary>GIF 不是视频容器，单独走两遍调色板。</summary>
    public const string GifFormat = "gif";

    /// <summary>支持的视频容器（顺序即界面顺序）。</summary>
    public static readonly VideoContainerInfo[] Containers =
    {
        new("mp4", "MP4（最通用）", "mp4", true),
        new("mkv", "MKV（兼容最好）", "mkv", false),
        new("mov", "MOV（苹果 / 剪辑）", "mov", true),
        new("avi", "AVI（老播放器 / 剪辑）", "avi", false),
        new("webm", "WebM（网页）", "webm", false),
    };

    /// <summary>支持的视频编码器（界面会在运行时按 ffmpeg -encoders 过滤）。</summary>
    public static readonly VideoCodecInfo[] Codecs =
    {
        new("libx264", "H.264 · libx264（软件，最稳）", "H.264", false, new[] { "mp4", "mkv", "mov", "avi" }),
        new("h264_nvenc", "H.264 · NVIDIA NVENC（硬件）", "H.264", true, new[] { "mp4", "mkv", "mov", "avi" }),
        new("h264_amf", "H.264 · AMD AMF（硬件）", "H.264", true, new[] { "mp4", "mkv", "mov", "avi" }),
        new("h264_qsv", "H.264 · Intel QSV（硬件）", "H.264", true, new[] { "mp4", "mkv", "mov", "avi" }),

        new("libx265", "H.265 / HEVC · libx265（软件，体积小）", "H.265", false, new[] { "mp4", "mkv", "mov" }),
        new("hevc_nvenc", "H.265 · NVIDIA NVENC（硬件）", "H.265", true, new[] { "mp4", "mkv", "mov" }),
        new("hevc_amf", "H.265 · AMD AMF（硬件）", "H.265", true, new[] { "mp4", "mkv", "mov" }),
        new("hevc_qsv", "H.265 · Intel QSV（硬件）", "H.265", true, new[] { "mp4", "mkv", "mov" }),

        new("libsvtav1", "AV1 · SVT-AV1（软件，压缩率高）", "AV1", false, new[] { "mp4", "mkv", "mov", "webm" }),
        new("av1_nvenc", "AV1 · NVIDIA NVENC（硬件）", "AV1", true, new[] { "mp4", "mkv", "mov", "webm" }),
        new("av1_amf", "AV1 · AMD AMF（硬件）", "AV1", true, new[] { "mp4", "mkv", "mov", "webm" }),
        new("av1_qsv", "AV1 · Intel QSV（硬件）", "AV1", true, new[] { "mp4", "mkv", "mov", "webm" }),

        new("libvpx-vp9", "VP9 · libvpx（软件，WebM 常用）", "VP9", false, new[] { "webm", "mkv" }),

        new("mpeg4", "MPEG-4 Part 2（老播放器 / AVI）", "MPEG-4", false, new[] { "avi", "mkv", "mp4", "mov" }),
        new("mjpeg", "Motion JPEG（AVI 剪辑）", "MJPEG", false, new[] { "avi", "mov", "mkv" }),
    };

    /// <summary>按 Id 取容器（认不出回退 mp4）。</summary>
    public static VideoContainerInfo GetContainer(string? id)
    {
        var value = id?.Trim().ToLowerInvariant();
        return Containers.FirstOrDefault(c => c.Id == value) ?? Containers[0];
    }

    /// <summary>格式是不是视频容器。</summary>
    public static bool IsVideoFormat(string? format)
        => Containers.Any(c => c.Id.Equals(format?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>按 Id 取编码器（认不出回退 libx264）。</summary>
    public static VideoCodecInfo GetCodec(string? id)
    {
        var value = id?.Trim();
        return Codecs.FirstOrDefault(c => string.Equals(c.Id, value, StringComparison.OrdinalIgnoreCase)) ?? Codecs[0];
    }

    /// <summary>容器 + 编码器是否兼容。</summary>
    public static bool IsCompatible(string containerId, string codecId)
        => GetCodec(codecId).Fits(GetContainer(containerId).Id);

    /// <summary>
    /// 该容器下可用的编码器列表：先按容器兼容性过滤，再按 ffmpeg 实际拥有的编码器过滤（<paramref name="available"/> 为 null 时不过滤）。
    /// </summary>
    public static IReadOnlyList<VideoCodecInfo> CompatibleCodecs(string containerId, IReadOnlySet<string>? available)
    {
        var container = GetContainer(containerId);
        return Codecs
            .Where(c => c.Fits(container.Id))
            .Where(c => available is null || available.Contains(c.Id))
            .ToArray();
    }

    /// <summary>容器下默认编码器：优先软件 H.264；WebM 优先 VP9（兼容性 / 速度都比 AV1 稳）。</summary>
    public static string DefaultCodecFor(string containerId, IReadOnlySet<string>? available)
    {
        var codecs = CompatibleCodecs(containerId, available);
        if (codecs.Count == 0) return "libx264";
        if (GetContainer(containerId).Id == "webm")
        {
            var vp9 = codecs.FirstOrDefault(c => c.Id == "libvpx-vp9");
            if (vp9 is not null) return vp9.Id;
        }

        var x264 = codecs.FirstOrDefault(c => c.Id == "libx264");
        return x264?.Id ?? codecs[0].Id;
    }

    /// <summary>容器对应的音频编码参数（视频容器才用；GIF 没有音频）。</summary>
    public static IReadOnlyList<string> BuildAudioEncodeArgs(string containerId, int bitrateKbps)
    {
        var container = GetContainer(containerId);
        var kbps = Math.Clamp(bitrateKbps, 64, 320);
        return container.Id switch
        {
            "webm" => new[] { "-c:a", "libopus", "-b:a", kbps.ToString(CultureInfo.InvariantCulture) + "k" },
            "avi" => new[] { "-c:a", "libmp3lame", "-b:a", kbps.ToString(CultureInfo.InvariantCulture) + "k" },
            _ => new[] { "-c:a", "aac", "-b:a", kbps.ToString(CultureInfo.InvariantCulture) + "k" },
        };
    }

    /// <summary>容器对应的音频流说明（给界面 / 日志用）。</summary>
    public static string AudioCodecLabel(string containerId) => GetContainer(containerId).Id switch
    {
        "webm" => "Opus",
        "avi" => "MP3",
        _ => "AAC",
    };
}

/// <summary>一次视频录制的编码参数（已归一化）。</summary>
public sealed record VideoEncoderSettings(string CodecId, string RateMode, int Quality, int BitrateMbps)
{
    /// <summary>画质优先（CRF / CQ）。</summary>
    public const string RateQuality = "quality";

    /// <summary>码率优先（Mbps）。</summary>
    public const string RateBitrate = "bitrate";

    /// <summary>归一化：编码器 / 速率模式 / 画质 / 码率都在合法范围内。</summary>
    public static VideoEncoderSettings Normalize(string? codecId, string? rateMode, int quality, int bitrateMbps)
        => new(
            VideoCatalog.GetCodec(codecId).Id,
            string.Equals(rateMode?.Trim(), RateBitrate, StringComparison.OrdinalIgnoreCase)
                ? RateBitrate
                : RateQuality,
            Math.Clamp(quality, 0, 51),
            Math.Clamp(bitrateMbps, 1, 100));
}

/// <summary>
/// 视频编码参数拼装（纯函数）。
/// <para>录制时先按这套参数写中间 MKV，停止后再 <c>-c:v copy</c> 封装进目标容器 + 混入音频 ——
/// 视频只编码一次，导出很快；硬件编码器不可用时由 FFmpeg 报错，界面提示换回软件编码。</para>
/// </summary>
public static class VideoEncodingArgs
{
    /// <summary>拼视频编码参数（不包含 <c>-an</c>，由调用方决定要不要音频）。</summary>
    public static IReadOnlyList<string> Build(VideoEncoderSettings settings)
    {
        var args = new List<string>();
        var codec = settings.CodecId;
        var quality = Math.Clamp(settings.Quality, 0, 51);
        var bitrateK = Math.Clamp(settings.BitrateMbps, 1, 100) * 1000;

        switch (codec)
        {
            case "libx264":
            case "libx265":
                args.AddRange(new[] { "-c:v", codec, "-preset", "veryfast" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality) AddCrf(args, quality);
                else AddBitrate(args, bitrateK);
                break;

            case "h264_nvenc":
            case "hevc_nvenc":
            case "av1_nvenc":
                args.AddRange(new[] { "-c:v", codec, "-preset", "p5" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality)
                    args.AddRange(new[] { "-rc", "vbr", "-cq", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0" });
                else
                {
                    args.AddRange(new[] { "-rc", "vbr", "-b:v", bitrateK.ToString(CultureInfo.InvariantCulture) + "k" });
                    AddBitrateSafety(args, bitrateK);
                }
                break;

            case "h264_amf":
            case "hevc_amf":
            case "av1_amf":
                args.AddRange(new[] { "-c:v", codec, "-quality", "speed" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality)
                {
                    var q = quality.ToString(CultureInfo.InvariantCulture);
                    args.AddRange(new[] { "-rc", "cqp", "-qp_i", q, "-qp_p", q, "-qp_b", q });
                }
                else
                {
                    args.AddRange(new[] { "-rc", "vbr", "-b:v", bitrateK.ToString(CultureInfo.InvariantCulture) + "k" });
                    AddBitrateSafety(args, bitrateK);
                }
                break;

            case "h264_qsv":
            case "hevc_qsv":
            case "av1_qsv":
                args.AddRange(new[] { "-c:v", codec, "-preset", "veryfast" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality)
                    args.AddRange(new[] { "-global_quality", quality.ToString(CultureInfo.InvariantCulture) });
                else
                {
                    args.AddRange(new[] { "-b:v", bitrateK.ToString(CultureInfo.InvariantCulture) + "k" });
                    AddBitrateSafety(args, bitrateK);
                }
                break;

            case "libsvtav1":
                args.AddRange(new[] { "-c:v", codec, "-preset", "10" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality) AddCrf(args, quality);
                else AddBitrate(args, bitrateK);
                break;

            case "libaom-av1":
                args.AddRange(new[] { "-c:v", codec, "-cpu-used", "8" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality)
                    args.AddRange(new[] { "-crf", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0" });
                else AddBitrate(args, bitrateK);
                break;

            case "libvpx-vp9":
                args.AddRange(new[] { "-c:v", codec, "-deadline", "realtime", "-cpu-used", "5" });
                if (settings.RateMode == VideoEncoderSettings.RateQuality)
                    args.AddRange(new[] { "-crf", quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0" });
                else AddBitrate(args, bitrateK);
                break;

            case "mpeg4":
            case "mjpeg":
                args.AddRange(new[] { "-c:v", codec });
                if (settings.RateMode == VideoEncoderSettings.RateQuality)
                {
                    // MPEG-4 / MJPEG 用 qscale（2 最好、31 最差），把 0~51 的 CRF 映射过去
                    var qscale = Math.Clamp(2 + (int)Math.Round(quality / 51.0 * 29), 2, 31);
                    args.AddRange(new[] { "-q:v", qscale.ToString(CultureInfo.InvariantCulture) });
                }
                else AddBitrate(args, bitrateK);
                break;

            default:
                // 认不出的编码器：退回最稳的 libx264
                args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast" });
                AddCrf(args, quality);
                break;
        }

        args.AddRange(new[] { "-pix_fmt", "yuv420p" });
        return args;
    }

    private static void AddCrf(List<string> args, int quality)
        => args.AddRange(new[] { "-crf", quality.ToString(CultureInfo.InvariantCulture) });

    private static void AddBitrate(List<string> args, int bitrateK)
    {
        args.AddRange(new[] { "-b:v", bitrateK.ToString(CultureInfo.InvariantCulture) + "k" });
        AddBitrateSafety(args, bitrateK);
    }

    private static void AddBitrateSafety(List<string> args, int bitrateK)
    {
        args.AddRange(new[]
        {
            "-maxrate", (bitrateK * 3 / 2).ToString(CultureInfo.InvariantCulture) + "k",
            "-bufsize", (bitrateK * 2).ToString(CultureInfo.InvariantCulture) + "k",
        });
    }
}

/// <summary>
/// 从 <c>ffmpeg -encoders</c> 的输出里解析本机实际拥有的编码器（纯函数，自检可喂假输出）。
/// </summary>
public static class EncoderCatalog
{
    /// <summary>
    /// 解析编码器名集合。行格式类似：
    /// <c> V....D libx264              libx264 H.264 / AVC ...</c>
    /// 第一段是 6 个能力标记，第二段是编码器名。
    /// </summary>
    public static IReadOnlySet<string> ParseAvailable(string? encodersOutput)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(encodersOutput)) return result;

        foreach (var rawLine in encodersOutput.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = Regex.Match(line, @"^\s*[A-Z.]{6}\s+([A-Za-z0-9_\-]+)");
            if (match.Success) result.Add(match.Groups[1].Value);
        }

        return result;
    }
}
