using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Tools.ScreenRecorder;

namespace YuLiangToolkit.Tools.MediaEditor.Services;

/// <summary>剪辑导出 / 预览生成（执行系统 FFmpeg）。</summary>
internal static class EditorExporter
{
    /// <summary>视频导出：裁剪 + 拼接 + 缩放 / 帧率 + 可选音轨，一次编码。</summary>
    public static async Task<EditorExportResult> ExportVideoAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        string containerId,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        VideoEncoderSettings encoder,
        int outputWidth,
        int fps,
        bool keepAudio,
        int audioBitrateKbps,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在导出视频…");
        var result = await FFmpegRunner.RunAsync(
            ffmpegPath,
            MediaEditorArguments.BuildVideoExport(
                inputPath, outputPath, containerId, segments, media, encoder, outputWidth, fps, keepAudio, audioBitrateKbps),
            cancellationToken).ConfigureAwait(false);
        return Validate(result, outputPath, "导出视频失败");
    }

    /// <summary>GIF 导出：两遍调色板（裁剪 / 拼接后统计颜色再合成）。</summary>
    public static async Task<EditorExportResult> ExportGifAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        string palettePath,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        GifExportSettings gif,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            progress?.Report("正在生成 GIF 调色板（第一遍）…");
            var palette = await FFmpegRunner.RunAsync(
                ffmpegPath,
                MediaEditorArguments.BuildGifPalette(inputPath, palettePath, segments, media, gif),
                cancellationToken).ConfigureAwait(false);
            if (!palette.Success || !HasContent(palettePath))
                return new EditorExportResult(false, outputPath, DescribeFailure(palette, "生成 GIF 调色板失败"));

            progress?.Report("正在合成 GIF（第二遍）…");
            var result = await FFmpegRunner.RunAsync(
                ffmpegPath,
                MediaEditorArguments.BuildGif(inputPath, palettePath, outputPath, segments, media, gif),
                cancellationToken).ConfigureAwait(false);
            return Validate(result, outputPath, "合成 GIF 失败");
        }
        finally
        {
            try { if (File.Exists(palettePath)) File.Delete(palettePath); } catch { /* 留给清理缓存 */ }
        }
    }

    /// <summary>生成低清预览 MP4（最长 10 秒，打开系统播放器看效果）。</summary>
    public static async Task<EditorExportResult> GeneratePreviewAsync(
        string ffmpegPath,
        string inputPath,
        string outputPath,
        IReadOnlyList<SegmentRange> segments,
        MediaInfo media,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("正在生成预览（低清、最多 10 秒）…");
        var result = await FFmpegRunner.RunAsync(
            ffmpegPath,
            MediaEditorArguments.BuildPreview(inputPath, outputPath, segments, media),
            cancellationToken).ConfigureAwait(false);
        return Validate(result, outputPath, "生成预览失败");
    }

    private static EditorExportResult Validate(FFmpegRunner.RunResult result, string outputPath, string fallback)
    {
        if (!result.Success) return new EditorExportResult(false, outputPath, DescribeFailure(result, fallback));
        if (!HasContent(outputPath)) return new EditorExportResult(false, outputPath, DescribeFailure(result, fallback + "（没有生成文件）"));
        return new EditorExportResult(true, outputPath, "");
    }

    private static string DescribeFailure(FFmpegRunner.RunResult result, string fallback)
    {
        var tail = string.Join(" | ", (result.Error ?? "").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(4));
        return tail.Length == 0 ? fallback : fallback + "：" + tail;
    }

    private static bool HasContent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch { return false; }
    }
}
