using System.Globalization;
using System.IO;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Compressor;

public enum CompressorFileKind
{
    Unknown,
    Image,
    Audio,
    Video,
}

/// <summary>一档压缩预设：图片质量 / 音频码率 / 视频 CRF 与音轨码率；后两项是整活档位用的尺寸上限。</summary>
public sealed record CompressionPreset(
    string Key,
    string Name,
    string Description,
    int ImageQuality,
    int AudioBitrateKbps,
    int VideoCrf,
    int VideoAudioBitrateKbps,
    int ImageMaxDimension = 0,
    int VideoMaxHeight = 0);

public static class CompressorProfiles
{
    public const string Light = "light";
    public const string Standard = "standard";
    public const string Extreme = "extreme";
    public const string Ultra = "ultra";
    public const string Mega = "mega";
    public const string TotalLoss = "total-loss";

    public static IReadOnlyList<CompressionPreset> All { get; } = new[]
    {
        new CompressionPreset(Light, "轻度压缩", "画质优先：图片 JPG 92，音频 192 kbps，视频 CRF 20", 92, 192, 20, 192),
        new CompressionPreset(Standard, "标准压缩", "体积与画质平衡：图片 JPG 82，音频 128 kbps，视频 CRF 26", 82, 128, 26, 128),
        new CompressionPreset(Extreme, "极限压缩", "优先减小体积：图片 JPG 70，音频 64 kbps，视频 CRF 32", 70, 64, 32, 64),
        new CompressionPreset(Ultra, "超糊压缩", "整活档：图片 JPG 45 / 长边 1600，音频 32 kbps，视频 CRF 38 / 最高 720p", 45, 32, 38, 32, 1600, 720),
        new CompressionPreset(Mega, "全损压缩", "整活档：图片 JPG 28 / 长边 1024，音频 16 kbps，视频 CRF 45 / 最高 480p", 28, 16, 45, 16, 1024, 480),
        new CompressionPreset(TotalLoss, "随缘压缩", "纯整活：图片 JPG 12 / 长边 640，音频 8 kbps，视频 CRF 51 / 最高 360p", 12, 8, 51, 8, 640, 360),
    };

    public static CompressionPreset Get(string? key)
        => All.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) ?? All[1];
}

public static class CompressorFiles
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".aac", ".m4a", ".ogg", ".opus", ".wma", ".aiff", ".aif", ".ape",
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv", ".wmv", ".m4v", ".ts", ".mts",
    };

    public static CompressorFileKind DetectKind(string? path)
    {
        var extension = Path.GetExtension(path ?? "").ToLowerInvariant();
        if (ImageExtensions.Contains(extension)) return CompressorFileKind.Image;
        if (AudioExtensions.Contains(extension)) return CompressorFileKind.Audio;
        if (VideoExtensions.Contains(extension)) return CompressorFileKind.Video;
        return CompressorFileKind.Unknown;
    }

    public static bool IsSupported(string? path) => DetectKind(path) != CompressorFileKind.Unknown;

    public static string KindText(CompressorFileKind kind) => kind switch
    {
        CompressorFileKind.Image => "图片",
        CompressorFileKind.Audio => "音频",
        CompressorFileKind.Video => "视频",
        _ => "文件",
    };

    public static string KindIcon(CompressorFileKind kind) => kind switch
    {
        CompressorFileKind.Image => "🖼️",
        CompressorFileKind.Audio => "🎵",
        CompressorFileKind.Video => "🎬",
        _ => "📄",
    };

    public static string SupportedFilter =>
        "支持的媒体|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff;*.mp3;*.wav;*.flac;*.aac;*.m4a;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.ape;*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts|"
        + "图片|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tif;*.tiff|"
        + "音频|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.ogg;*.opus;*.wma;*.aiff;*.aif;*.ape|"
        + "视频|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.m4v;*.ts;*.mts";

    public static string OutputExtension(CompressorFileKind kind, string imageFormat, string audioFormat, string videoFormat)
        => kind switch
        {
            CompressorFileKind.Image => imageFormat == "png" ? ".png" : ".jpg",
            CompressorFileKind.Audio => audioFormat switch
            {
                "m4a" => ".m4a",
                "ogg" => ".ogg",
                _ => ".mp3",
            },
            CompressorFileKind.Video => videoFormat == "mp4" ? ".mp4" : ".mp4",
            _ => ".bin",
        };

    public static string BuildOutputPath(string inputPath, string folder, string suffix, string extension, bool overwrite)
    {
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        if (string.IsNullOrWhiteSpace(stem)) stem = "媒体";
        var safeSuffix = string.IsNullOrWhiteSpace(suffix) ? "" : suffix;
        var candidate = Path.Combine(folder, stem + safeSuffix + extension);
        if (overwrite || !File.Exists(candidate)) return candidate;

        for (var i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem}{safeSuffix}-{i}{extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{stem}{safeSuffix}-{Guid.NewGuid():N}{extension}");
    }

    public static string SizeText(long bytes) => FileSize.Format(bytes);
}

/// <summary>FFmpeg 参数拼装（纯函数，自检可直接断言）。</summary>
public static class CompressorArguments
{
    public static List<string> BuildAudio(string input, string output, CompressionPreset preset, string format)
    {
        var codec = format switch
        {
            "m4a" => "aac",
            "ogg" => "libvorbis",
            _ => "libmp3lame",
        };
        return new List<string>
        {
            "-hide_banner", "-y", "-i", input, "-vn", "-map_metadata", "0",
            "-progress", "pipe:1", "-nostats",
            "-c:a", codec, "-b:a", $"{preset.AudioBitrateKbps}k", output,
        };
    }

    public static List<string> BuildVideo(string input, string output, CompressionPreset preset)
    {
        var args = new List<string>
        {
            "-hide_banner", "-y", "-i", input,
            "-map", "0:v:0", "-map", "0:a?",
        };
        if (preset.VideoMaxHeight > 0)
        {
            args.Add("-vf");
            args.Add($"scale=-2:{preset.VideoMaxHeight}");
        }
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "medium", "-crf", preset.VideoCrf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", $"{preset.VideoAudioBitrateKbps}k",
            "-movflags", "+faststart",
            "-progress", "pipe:1", "-nostats",
            output,
        });
        return args;
    }

    public static double? ParseProgressSeconds(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var text = line.Trim();
        if (text.StartsWith("out_time_us=", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(text[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                return microseconds / 1_000_000.0;
        }
        else if (text.StartsWith("out_time_ms=", StringComparison.OrdinalIgnoreCase))
        {
            if (long.TryParse(text[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                return microseconds / 1_000_000.0;
        }
        return null;
    }
}

/// <summary>ffprobe 得到的精简媒体信息。</summary>
public sealed record CompressorProbeInfo(
    double DurationSeconds,
    int Width,
    int Height,
    string VideoCodec,
    bool HasAudio,
    string AudioCodec)
{
    public string Summary
    {
        get
        {
            if (Width > 0 && Height > 0)
            {
                var duration = FormatDuration(DurationSeconds);
                return duration.Length > 0 ? $"{Width}×{Height} · {duration}" : $"{Width}×{Height}";
            }
            return FormatDuration(DurationSeconds);
        }
    }

    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}
