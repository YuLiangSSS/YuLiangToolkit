using System.Globalization;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.VideoToGif;

/// <summary>一帧转 GIF 的最终参数（已归一化，供 <see cref="GifConverter.Build*"/> 拼 FFmpeg 参数）。</summary>
public sealed record GifSettings(int Fps, int Width, int Colors, string Dither, bool Loop, double StartSeconds, double DurationSeconds)
{
    /// <summary>抖动算法白名单（paletteuse 实际支持的几档），其它值回退 Floyd–Steinberg。</summary>
    public static readonly IReadOnlyList<string> DitherChoices = new[]
    {
        "floyd_steinberg", "bayer", "sierra2", "sierra2_4a", "none",
    };

    public static readonly int[] ColorChoices = { 256, 128, 64, 32 };

    /// <summary>
    /// 归一化一组用户输入：FPS / 宽度 / 颜色 / 抖动 / 起止时间都夹到合法区间。
    /// 纯函数，自检直接断言。
    /// </summary>
    public static GifSettings Normalize(int fps, int width, int colors, string? dither,
        bool loop, double startSeconds, double durationSeconds)
    {
        // FPS：1~50，默认 15
        var safeFps = fps <= 0 ? 15 : Math.Clamp(fps, 1, 50);

        // 宽度：<=0 表示保持原宽（不缩放）；否则偶数化、夹到 16~1920
        var safeWidth = width <= 0
            ? 0
            : Math.Clamp(width - (width % 2), 16, 1920);

        // 颜色：夹到 2~256，并吸附到常用档
        var safeColors = Math.Clamp(colors, 2, 256);

        // 抖动：白名单外回退
        var safeDither = DitherChoices.Contains(dither) ? dither! : "floyd_steinberg";

        // 起始时间：>=0
        var safeStart = double.IsNaN(startSeconds) || startSeconds < 0 ? 0 : startSeconds;

        // 时长：>=0，0 = 到结尾
        var safeDuration = double.IsNaN(durationSeconds) || durationSeconds < 0 ? 0 : durationSeconds;

        return new GifSettings(safeFps, safeWidth, safeColors, safeDither, loop, safeStart, safeDuration);
    }

    /// <summary>实际输出宽度：宽度 0 = 保持原宽；宽度 ≥ 原宽也不放大。</summary>
    public static int ResolveOutputWidth(int requested, int sourceWidth)
    {
        if (sourceWidth <= 0) return Math.Max(0, requested);
        if (requested <= 0 || requested >= sourceWidth) return sourceWidth - (sourceWidth % 2);
        return Math.Max(2, requested - (requested % 2));
    }
}

/// <summary>
/// 视频转 GIF 的纯逻辑：时间解析 / FFmpeg 参数拼装 / 输出命名。
/// <para><b>两遍编码</b>：第一遍 <c>palettegen</c> 从截取片段里统计最优调色板，
/// 第二遍 <c>paletteuse</c> 套用调色板输出 GIF —— 比直接 <c>-f gif</c> 颜色准非常多。</para>
/// <para>本类不启动 FFmpeg、不碰文件，只拼参数（方便自检无头断言）。</para>
/// </summary>
public static class GifConverter
{
    /// <summary>公共前缀：安静 + 覆盖。转换是「读文件 → 写文件」不需要 stdin，加 -nostdin 防止抢终端。</summary>
    private static List<string> Base() => new() { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };

    /// <summary>
    /// 把 "1:23.5" / "83.5" / "01:23:45" 这类时间串解析成秒。
    /// 支持 <c>hh:mm:ss(.ms)</c>、<c>mm:ss(.ms)</c>、纯秒数。空 / 非法返回 0。
    /// </summary>
    public static double ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        text = text.Trim();

        // 纯数字秒
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain))
            return plain < 0 ? 0 : plain;

        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return 0;

        double total = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return 0;
            total = total * 60 + v;
        }
        return total;
    }

    /// <summary>把秒格式化成 ffmpeg 接受的 <c>hh:mm:ss.mmm</c>。</summary>
    public static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    /// <summary>
    /// 截取片段的公共输入参数：<c>-ss 起始 -t 时长 -i 源文件</c>。
    /// <c>-ss</c> 放在 <c>-i</c> 前（关键帧快速 seek）；时长 0 = 到结尾则不加 <c>-t</c>。
    /// </summary>
    public static List<string> BuildInputArgs(string inputPath, GifSettings gif)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        var args = new List<string>();
        if (gif.StartSeconds > 0)
        {
            args.Add("-ss");
            args.Add(FormatTime(gif.StartSeconds));
        }
        args.Add("-i");
        args.Add(inputPath);
        if (gif.DurationSeconds > 0)
        {
            args.Add("-t");
            args.Add(FormatTime(gif.DurationSeconds));
        }
        return args;
    }

    /// <summary>
    /// GIF 前半段滤镜：限定帧率 + 可选等比缩放（偶数宽，高度自适应 <c>-2</c>）。
    /// 缩放只缩不放。
    /// </summary>
    public static string BuildFilterPrefix(GifSettings gif, int sourceWidth)
    {
        var parts = new List<string> { "fps=" + gif.Fps.ToString(CultureInfo.InvariantCulture) };
        var width = GifSettings.ResolveOutputWidth(gif.Width, sourceWidth);
        if (width > 0 && (sourceWidth <= 0 || width < sourceWidth))
            parts.Add("scale=" + width.ToString(CultureInfo.InvariantCulture) + ":-2:flags=lanczos");
        return string.Join(",", parts);
    }

    /// <summary>第一遍：生成调色板 PNG。</summary>
    public static IReadOnlyList<string> BuildPaletteArgs(string inputPath, string palettePath, GifSettings gif, int sourceWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(palettePath);
        var args = Base();
        args.AddRange(BuildInputArgs(inputPath, gif));
        args.AddRange(new[]
        {
            "-vf", BuildFilterPrefix(gif, sourceWidth) + ",palettegen=max_colors=" + gif.Colors.ToString(CultureInfo.InvariantCulture) + ":stats_mode=diff",
            "-frames:v", "1",
            palettePath,
        });
        return args;
    }

    /// <summary>第二遍：把截取片段 + 调色板合成 GIF。</summary>
    public static IReadOnlyList<string> BuildGifArgs(string inputPath, string palettePath, string outputPath, GifSettings gif, int sourceWidth)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(palettePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var filter = $"[0:v]{BuildFilterPrefix(gif, sourceWidth)}[v];[v][1:v]paletteuse=dither={gif.Dither}:diff_mode=rectangle";
        var args = Base();
        args.AddRange(BuildInputArgs(inputPath, gif));
        args.AddRange(new[]
        {
            "-i", palettePath,
            "-filter_complex", filter,
            "-loop", gif.Loop ? "0" : "-1",
            "-progress", "pipe:1",   // 进度写 stdout，ViewModel 解析 out_time_us
            outputPath,
        });
        return args;
    }

    /// <summary>清洗文件名（去掉 Windows 非法字符、首尾点 / 空格）。</summary>
    public static string SanitizeFileName(string? name, string fallback = "转换结果")
    {
        var text = (name ?? "").Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            text = text.Replace(ch, '_');
        text = text.Trim(' ', '.');
        if (text.Length > 120) text = text[..120].Trim(' ', '.');
        return text.Length == 0 ? fallback : text;
    }

    /// <summary>输出文件名：源文件名 + .gif（去掉原扩展名）。</summary>
    public static string BuildOutputName(string inputFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(inputFileName);
        return SanitizeFileName(stem, "转换结果") + ".gif";
    }

    /// <summary>把 ffmpeg 的进度行 <c>out_time_us=123456</c> 解析成秒；<c>progress=end</c> 等其它行返回 null。</summary>
    public static double? ParseProgressSeconds(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = Regex.Match(line, @"out_time_(?:us|ms)=(\d+)");
        if (!m.Success) return null;
        if (!long.TryParse(m.Groups[1].Value, out var value)) return null;
        // out_time_us 是微秒，out_time_ms 是毫秒（老版本 ffmpeg 的 out_time_ms 实际也是微秒，这里按 us 处理更稳）
        return value / 1_000_000.0;
    }

    /// <summary>校验输入：返回错误信息，null = 通过。不碰 FFmpeg。</summary>
    public static string? Validate(string? inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath)) return "请选择源视频文件";
        if (!File.Exists(inputPath)) return "源视频文件不存在";
        return null;
    }
}
