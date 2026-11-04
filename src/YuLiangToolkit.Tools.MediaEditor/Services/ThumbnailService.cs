using System.Globalization;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Tools.MediaEditor.Services;

/// <summary>一张时间轴缩略图。</summary>
public sealed record ThumbnailFile(int Index, double TimeSeconds, string FilePath);

/// <summary>
/// 生成时间轴缩略图（用 FFmpeg 抽帧成 JPG，UI 再加载成 BitmapImage）。
/// <para>不追求精确到帧：按总时长平均采样，缩略图上的时间只用于快速定位起点 / 终点。</para>
/// </summary>
internal static class ThumbnailService
{
    /// <summary>生成 <paramref name="count"/> 张缩略图；返回文件和可能的错误。</summary>
    public static async Task<(IReadOnlyList<ThumbnailFile> Files, string Error)> GenerateAsync(
        string ffmpegPath,
        string inputPath,
        double durationSeconds,
        int count,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        var files = new List<ThumbnailFile>();
        if (durationSeconds <= 0) return (files, "视频时长未知，无法生成缩略图");

        count = Math.Clamp(count, 4, 24);
        Directory.CreateDirectory(outputDirectory);
        var countCopy = count;

        // 均分采样：fps = count / duration；非常短的视频把采样率压到 30 以内
        var sampleRate = Math.Min(30.0, countCopy / durationSeconds);
        if (sampleRate < 0.01) sampleRate = 0.01;

        try
        {
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", inputPath,
                "-vf", $"fps={sampleRate.ToString("0.#####", CultureInfo.InvariantCulture)},scale=160:-2",
                "-frames:v", countCopy.ToString(CultureInfo.InvariantCulture),
                "-q:v", "4",
                Path.Combine(outputDirectory, "thumb-%03d.jpg"),
            };

            var result = await FFmpegRunner.RunAsync(ffmpegPath, args, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                var tail = string.Join(" | ", result.Error.Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .TakeLast(3));
                return (files, tail.Length > 0 ? "生成缩略图失败：" + tail : "生成缩略图失败");
            }

            for (var i = 1; i <= countCopy; i++)
            {
                var path = Path.Combine(outputDirectory, $"thumb-{i:000}.jpg");
                if (!File.Exists(path)) continue;
                var time = Math.Min(durationSeconds, (i - 1) * durationSeconds / countCopy);
                files.Add(new ThumbnailFile(i - 1, time, path));
            }

            return (files, files.Count == 0 ? "没有生成缩略图（FFmpeg 没有输出帧）" : "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return (files, "生成缩略图失败：" + ex.Message);
        }
    }

    /// <summary>把缩略图 JPG 加载成可跨线程使用的冻结位图。</summary>
    public static BitmapImage? Load(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
