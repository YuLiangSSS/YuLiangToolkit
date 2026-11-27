using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Tools.Compressor;

/// <summary>图片压缩：使用 WPF 自带解码 / 编码，输出 JPG 或 PNG。</summary>
public static class CompressorImage
{
    public static void Compress(string inputPath, string outputPath, int jpegQuality, string format, int maxDimension = 0)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { CompressCore(inputPath, outputPath, jpegQuality, format, maxDimension); }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error is not null) throw new InvalidOperationException("图片压缩失败：" + error.Message, error);
            return;
        }

        CompressCore(inputPath, outputPath, jpegQuality, format, maxDimension);
    }

    private static void CompressCore(string inputPath, string outputPath, int jpegQuality, string format, int maxDimension)
    {
        using var stream = File.OpenRead(inputPath);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = ResizeIfNeeded(decoder.Frames[0], maxDimension);
        BitmapEncoder encoder;
        if (format == "png")
        {
            encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
        }
        else
        {
            encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(FlattenOnWhite(source)));
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        using var output = File.Create(outputPath);
        encoder.Save(output);
    }

    private static BitmapSource ResizeIfNeeded(BitmapSource source, int maxDimension)
    {
        if (maxDimension <= 0) return source;
        var maxSide = Math.Max(source.PixelWidth, source.PixelHeight);
        if (maxSide <= maxDimension) return source;
        if (source.CanFreeze && !source.IsFrozen) source.Freeze();
        var scale = maxDimension / (double)maxSide;
        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static BitmapSource FlattenOnWhite(BitmapSource source)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
        }
        var result = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        result.Freeze();
        return result;
    }
}

/// <summary>媒体探测：图片用 WPF，音频 / 视频用 ffprobe（如果系统有）。</summary>
public static class CompressorProbe
{
    public static async Task<CompressorProbeInfo?> ProbeAsync(
        string path,
        CompressorFileKind kind,
        string? ffmpegPath,
        CancellationToken cancellationToken = default)
    {
        if (kind == CompressorFileKind.Image) return ProbeImage(path);
        if (kind is not (CompressorFileKind.Audio or CompressorFileKind.Video)) return null;

        var ffmpeg = FFmpegLocator.Find(ffmpegPath);
        if (ffmpeg is null) return null;
        var ffprobe = FFmpegLocator.FindFFprobe(ffmpeg);
        if (ffprobe is null) return null;

        try
        {
            var result = await FFmpegRunner.RunAsync(
                ffprobe,
                new[]
                {
                    "-v", "error",
                    "-print_format", "json",
                    "-show_format",
                    "-show_streams",
                    path,
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.Success) return null;
            return ParseProbeJson(result.Output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static CompressorProbeInfo? ParseProbeJson(string? json)
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
                    var type = GetString(stream, "codec_type");
                    if (type == "video" && video is null) video = stream;
                    else if (type == "audio" && audio is null) audio = stream;
                }
            }

            var duration = GetDouble(root, "format", "duration");
            if (duration <= 0 && video is not null) duration = GetDouble(video.Value, "duration");
            if (duration <= 0 && audio is not null) duration = GetDouble(audio.Value, "duration");
            var width = video is null ? 0 : GetInt(video.Value, "width");
            var height = video is null ? 0 : GetInt(video.Value, "height");
            var videoCodec = video is null ? "" : GetString(video.Value, "codec_name") ?? "";
            var hasAudio = audio is not null;
            var audioCodec = hasAudio ? GetString(audio!.Value, "codec_name") ?? "" : "";
            return new CompressorProbeInfo(duration, width, height, videoCodec, hasAudio, audioCodec);
        }
        catch
        {
            return null;
        }
    }

    private static CompressorProbeInfo? ProbeImage(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return new CompressorProbeInfo(0, frame.PixelWidth, frame.PixelHeight, "", false, "");
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
        => double.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static double GetDouble(JsonElement element, string parent, string name)
        => element.TryGetProperty(parent, out var child) && double.TryParse(
            GetString(child, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
}
