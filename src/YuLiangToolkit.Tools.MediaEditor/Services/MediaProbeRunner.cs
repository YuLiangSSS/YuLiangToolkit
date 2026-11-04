using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Tools.MediaEditor.Services;

/// <summary>用 ffprobe 读取媒体信息（不依赖第三方库）。</summary>
internal static class MediaProbeRunner
{
    /// <summary>探测文件；失败时返回中文错误。</summary>
    public static async Task<(MediaInfo? Info, string Error)> ProbeAsync(
        string ffmpegPath,
        string inputPath,
        CancellationToken cancellationToken = default)
    {
        var ffprobe = FFmpegLocator.FindFFprobe(ffmpegPath);
        if (ffprobe is null)
            return (null, "找不到 ffprobe.exe（它通常和 ffmpeg.exe 在同一目录；请重新选择 FFmpeg 目录里的 ffmpeg.exe）");

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
                    inputPath,
                },
                cancellationToken).ConfigureAwait(false);

            var info = MediaProbe.ParseJson(result.Output, inputPath);
            if (info is not null) return (info, "");
            var tail = LastLines(result.Error, 3);
            return (null, tail.Length > 0 ? "读取媒体信息失败：" + tail : "读取媒体信息失败：文件里没有可用的视频流");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (null, "读取媒体信息失败：" + ex.Message);
        }
    }

    private static string LastLines(string? text, int count)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return string.Join(" | ", text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .TakeLast(count));
    }
}
