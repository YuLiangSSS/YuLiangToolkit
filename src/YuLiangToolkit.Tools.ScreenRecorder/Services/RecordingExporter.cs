using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>导出结果（失败时 <see cref="Error"/> 是给用户看的 FFmpeg 尾部错误）。</summary>
internal sealed record ExportResult(bool Success, string OutputPath, string Error);

/// <summary>
/// 把录屏素材导出成最终文件：GIF（两遍调色板）/ MP4（H.264）/ 单帧 PNG 截图。
/// 这里只做“跑 FFmpeg + 校验产物”，不碰 WPF，方便人工和自检复用。
/// </summary>
internal static class RecordingExporter
{
    /// <summary>GIF 两遍编码：palettegen → paletteuse。</summary>
    public static async Task<ExportResult> ExportGifAsync(
        string ffmpegPath,
        CaptureRect region,
        GifExportSettings gif,
        string intermediatePath,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var palettePath = intermediatePath + ".palette.png";
        try
        {
            progress?.Report("正在生成 GIF 调色板（第一遍）…");
            var palette = await FFmpegRunner.RunAsync(
                ffmpegPath,
                FfmpegArguments.BuildGifPalette(intermediatePath, palettePath, gif, region),
                cancellationToken).ConfigureAwait(false);
            if (!palette.Success || !HasContent(palettePath))
            {
                return new ExportResult(false, outputPath,
                    DescribeFailure(palette, "生成 GIF 调色板失败（可能是录制区域为空或素材损坏）"));
            }

            progress?.Report("正在合成 GIF（第二遍）…");
            var result = await FFmpegRunner.RunAsync(
                ffmpegPath,
                FfmpegArguments.BuildGif(intermediatePath, palettePath, outputPath, gif, region),
                cancellationToken).ConfigureAwait(false);
            return Validate(result, outputPath, "合成 GIF 失败");
        }
        finally
        {
            TryDelete(palettePath);
        }
    }

    /// <summary>把中间素材重新编码成 MP4。</summary>
    public static async Task<ExportResult> ExportMp4Async(
        string ffmpegPath,
        CaptureRect region,
        Mp4ExportSettings mp4,
        string intermediatePath,
        string outputPath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在生成 MP4…");
        var result = await FFmpegRunner.RunAsync(
            ffmpegPath,
            FfmpegArguments.BuildMp4(intermediatePath, outputPath, mp4, region),
            cancellationToken).ConfigureAwait(false);
        return Validate(result, outputPath, "生成 MP4 失败");
    }

    /// <summary>截一张 PNG（不经过中间素材）。</summary>
    public static async Task<ExportResult> CaptureScreenshotAsync(
        string ffmpegPath,
        CapturePlan plan,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await FFmpegRunner.RunAsync(
                ffmpegPath,
                FfmpegArguments.BuildScreenshot(outputPath, plan),
                cancellationToken).ConfigureAwait(false);
            return Validate(result, outputPath, "截图失败");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ExportResult(false, outputPath, ex.Message);
        }
    }

    private static ExportResult Validate(FFmpegRunner.RunResult result, string outputPath, string fallbackMessage)
    {
        if (!result.Success) return new ExportResult(false, outputPath, DescribeFailure(result, fallbackMessage));
        if (!HasContent(outputPath))
            return new ExportResult(false, outputPath, DescribeFailure(result, fallbackMessage + "（没有生成文件）"));
        return new ExportResult(true, outputPath, "");
    }

    /// <summary>FFmpeg 退出码 0 但产物没写出来时，把 stderr 最后几行带回去。</summary>
    private static string DescribeFailure(FFmpegRunner.RunResult result, string fallbackMessage)
    {
        var tail = LastLines(result.Error, 4);
        return tail.Length == 0 ? fallbackMessage : fallbackMessage + "：" + tail;
    }

    private static string LastLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n')
            .Select(line => line.TrimEnd('\r').Trim())
            .Where(line => line.Length > 0)
            .TakeLast(count)
            .ToArray();
        return string.Join(" | ", lines);
    }

    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 删除失败留给「清理缓存」 */ }
    }
}
