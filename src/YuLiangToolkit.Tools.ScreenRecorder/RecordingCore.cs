using System.Globalization;

namespace YuLiangToolkit.Tools.ScreenRecorder;

/// <summary>录制来源。</summary>
public enum CaptureSourceKind
{
    /// <summary>主显示器全屏。</summary>
    FullScreen,

    /// <summary>用户框选的一块屏幕区域。</summary>
    Region,

    /// <summary>某个窗口（录制它的矩形区域；窗口移动后不会跟着走）。</summary>
    Window,
}

/// <summary>
/// 屏幕矩形（**物理像素**，和 FFmpeg gdigrab 的 <c>offset_x/offset_y/video_size</c> 同一套坐标，
/// 多显示器时可以是负坐标）。纯值类型，方便自检断言。
/// </summary>
public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    /// <summary>空矩形（宽或高 ≤ 0）。</summary>
    public static CaptureRect Empty => default;

    /// <summary>宽 / 高至少有一个不是正数。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>右边界（不含）。</summary>
    public int Right => X + Width;

    /// <summary>下边界（不含）。</summary>
    public int Bottom => Y + Height;

    /// <summary>像素面积（空矩形 = 0）。</summary>
    public long Area => IsEmpty ? 0 : (long)Width * Height;

    /// <summary>由两个对角点构造（自动处理反向拖动）；两点相同 = 空矩形。</summary>
    public static CaptureRect FromPoints(int x1, int y1, int x2, int y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>由左 / 上 / 右 / 下边界构造（右 / 下不含）。</summary>
    public static CaptureRect FromBounds(int left, int top, int right, int bottom)
        => FromPoints(left, top, right, bottom);

    /// <summary>裁剪到另一个矩形内（用于防止框选超出屏幕）；完全不相交返回空矩形。</summary>
    public CaptureRect ClampTo(CaptureRect area)
    {
        if (IsEmpty || area.IsEmpty) return Empty;

        var left = Math.Max(X, area.X);
        var top = Math.Max(Y, area.Y);
        var right = Math.Min(Right, area.Right);
        var bottom = Math.Min(Bottom, area.Bottom);
        if (right <= left || bottom <= top) return Empty;
        return new CaptureRect(left, top, right - left, bottom - top);
    }

    /// <summary>把宽高裁成偶数（H.264 / GIF 的 yuv / 调色板对奇数尺寸不友好）；最小 2。</summary>
    public CaptureRect MakeEven()
    {
        if (IsEmpty) return Empty;
        var width = Width - (Width % 2);
        var height = Height - (Height % 2);
        if (width < 2) width = 2;
        if (height < 2) height = 2;
        return new CaptureRect(X, Y, width, height);
    }

    /// <summary>
    /// 等比缩放到指定宽度（<paramref name="targetWidth"/> ≤ 0 或 ≥ 原宽 = 不放大，直接用原宽）。
    /// 返回的矩形保留原来的屏幕坐标（FFmpeg 只拿 Width 做缩放参考）。
    /// </summary>
    public CaptureRect ScaleWidth(int targetWidth)
    {
        if (IsEmpty) return this;
        if (targetWidth <= 0 || targetWidth >= Width) return this;

        var width = targetWidth - (targetWidth % 2);
        if (width < 2) width = 2;
        var height = (int)Math.Round(Height * (double)width / Width, MidpointRounding.AwayFromZero);
        height = Math.Max(2, height - (height % 2));
        return new CaptureRect(X, Y, width, height);
    }

    /// <summary>界面上显示成 <c>960×540</c>。</summary>
    public string SizeText => IsEmpty ? "未选择" : $"{Width}×{Height}";

    /// <summary>界面上显示成 <c>(0, 0) 960×540</c>。</summary>
    public override string ToString() => IsEmpty ? "未选择" : $"({X}, {Y}) {Width}×{Height}";
}

/// <summary>一次录制 / 截图要采集的具体区域 + 参数（参数都已归一化）。</summary>
public sealed record CapturePlan(CaptureRect Region, int Fps, bool DrawMouse, int MaxSeconds, string SourceLabel)
{
    /// <summary>实际采集宽度（区域已裁成偶数）。</summary>
    public int Width => Region.Width;

    /// <summary>实际采集高度。</summary>
    public int Height => Region.Height;
}

/// <summary>GIF 导出参数（已归一化）。</summary>
public sealed record GifExportSettings(int Fps, int ScaleWidth, int MaxColors, string Dither, bool Loop)
{
    /// <summary>把配置 / 界面上的原始值归一化成合法参数。</summary>
    public static GifExportSettings Normalize(int fps, int scaleWidth, int maxColors, string? dither, bool loop)
        => new(
            RecorderOptions.NormalizeFps(fps),
            RecorderOptions.NormalizeScaleWidth(scaleWidth),
            RecorderOptions.NormalizeColors(maxColors),
            RecorderOptions.NormalizeDither(dither),
            loop);
}

/// <summary>MP4 导出参数（已归一化；帧率由采集时的 <see cref="CapturePlan.Fps"/> 决定）。</summary>
public sealed record Mp4ExportSettings(int ScaleWidth, int Crf)
{
    /// <summary>把配置 / 界面上的原始值归一化成合法参数。</summary>
    public static Mp4ExportSettings Normalize(int scaleWidth, int crf)
        => new(RecorderOptions.NormalizeScaleWidth(scaleWidth), RecorderOptions.NormalizeCrf(crf));
}

/// <summary>
/// 配置值归一化 / 枚举解析。所有“读配置 → 拼 FFmpeg 参数”的地方都先过这里，
/// 这样用户手改坏 config.json 也不会让录制炸掉（纯函数，自检直接断言）。
/// </summary>
public static class RecorderOptions
{
    /// <summary>GIF / MP4 都支持的帧率范围。</summary>
    public const int MinFps = 1;
    public const int MaxFps = 30;

    /// <summary>界面下拉框里的帧率档位（手改 config 时会吸附到最近一档，避免界面显示和实际不一致）。</summary>
    public static readonly int[] FpsChoices = { 5, 10, 15, 20, 24, 30 };

    /// <summary>界面下拉框里的输出宽度档位（0 = 原始分辨率）。</summary>
    public static readonly int[] WidthChoices = { 0, 1920, 1280, 960, 800, 640, 480 };

    /// <summary>输出宽度的“具体数值”档位（手改 config 时正数只会吸附到这些档，不会被吸成 0=原始）。</summary>
    public static readonly int[] WidthPositiveChoices = { 1920, 1280, 960, 800, 640, 480 };

    /// <summary>界面下拉框里的 GIF 颜色数档位。</summary>
    public static readonly int[] GifColorChoices = { 256, 128, 64 };

    /// <summary>界面下拉框里的 MP4 画质档位。</summary>
    public static readonly int[] QualityChoices = { 18, 23, 28 };

    /// <summary>界面下拉框里的倒计时档位。</summary>
    public static readonly int[] CountdownChoices = { 0, 3, 5, 10 };

    /// <summary>界面下拉框里的最长录制档位（0 = 不限）。</summary>
    public static readonly int[] MaxSecondsChoices = { 0, 30, 60, 120, 300 };

    /// <summary>最长录制的“具体数值”档位（手改 config 时正数只吸附到这些档，不会被吸成 0=不限）。</summary>
    public static readonly int[] MaxSecondsPositiveChoices = { 30, 60, 120, 300 };

    /// <summary>把配置里的值吸附到最近的界面档位；正好在档位上就原样返回。</summary>
    public static int SnapToNearest(int value, IReadOnlyList<int> choices)
    {
        if (choices.Count == 0) return value;
        foreach (var choice in choices)
        {
            if (choice == value) return value;
        }

        var best = choices[0];
        var bestDistance = Math.Abs(choices[0] - value);
        foreach (var choice in choices)
        {
            var distance = Math.Abs(choice - value);
            if (distance < bestDistance)   // 距离相同时保留更小的一档
            {
                best = choice;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// 把整个配置归一化：枚举回退 / 数值夹取 + 吸附到界面档位 / 文件名前缀清洗。
    /// <para>手改坏 <c>config.json</c> 时保证界面显示的值就是实际会用的值（不出现“显示 15 FPS、实际 7 FPS”）。</para>
    /// </summary>
    public static void NormalizeConfig(ScreenRecorderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config.SourceKind = ParseSourceKind(config.SourceKind) switch
        {
            CaptureSourceKind.FullScreen => "fullscreen",
            CaptureSourceKind.Window => "window",
            _ => "region",
        };
        config.Format = NormalizeFormat(config.Format);
        config.Fps = SnapToNearest(NormalizeFps(config.Fps), FpsChoices);
        var scaleWidth = NormalizeScaleWidth(config.ScaleWidth);
        config.ScaleWidth = scaleWidth <= 0 ? 0 : SnapToNearest(scaleWidth, WidthPositiveChoices);
        config.GifColors = SnapToNearest(NormalizeColors(config.GifColors), GifColorChoices);
        config.GifDither = NormalizeDither(config.GifDither);
        config.Mp4Quality = SnapToNearest(NormalizeCrf(config.Mp4Quality), QualityChoices);
        config.CountdownSeconds = SnapToNearest(NormalizeCountdown(config.CountdownSeconds), CountdownChoices);
        var maxSeconds = NormalizeMaxSeconds(config.MaxSeconds);
        config.MaxSeconds = maxSeconds <= 0 ? 0 : SnapToNearest(maxSeconds, MaxSecondsPositiveChoices);
        config.FilePrefix = RecordingNames.SanitizePrefix(config.FilePrefix);
    }

    /// <summary>来源解析：认不出的值回退「区域」。</summary>
    public static CaptureSourceKind ParseSourceKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "fullscreen" or "full" or "screen" => CaptureSourceKind.FullScreen,
        "window" => CaptureSourceKind.Window,
        _ => CaptureSourceKind.Region,
    };

    /// <summary>格式解析：只有 <c>mp4</c> 算 MP4，其余一律 GIF。</summary>
    public static string NormalizeFormat(string? value)
        => string.Equals(value?.Trim(), "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4" : "gif";

    /// <summary>帧率夹到 1~30。</summary>
    public static int NormalizeFps(int value) => Math.Clamp(value, MinFps, MaxFps);

    /// <summary>输出宽度：0 = 原始；其它夹到 160~3840 且取偶数。</summary>
    public static int NormalizeScaleWidth(int value)
    {
        if (value <= 0) return 0;
        var clamped = Math.Clamp(value, 160, 3840);
        return clamped - (clamped % 2);
    }

    /// <summary>GIF 调色板颜色数：2~256。</summary>
    public static int NormalizeColors(int value) => Math.Clamp(value, 2, 256);

    /// <summary>GIF 抖动：只认 ffmpeg paletteuse 支持的三种，其它回退 Floyd–Steinberg。</summary>
    public static string NormalizeDither(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => "none",
        "bayer" => "bayer",
        "floyd_steinberg" or "floydsteinberg" or "floyd" => "floyd_steinberg",
        _ => "floyd_steinberg",
    };

    /// <summary>MP4 画质（H.264 CRF）：0~51，默认 23。</summary>
    public static int NormalizeCrf(int value) => value <= 0 ? 23 : Math.Clamp(value, 0, 51);

    /// <summary>倒计时秒数：0~10。</summary>
    public static int NormalizeCountdown(int value) => Math.Clamp(value, 0, 10);

    /// <summary>最长录制秒数：0 = 不限；其它 1 秒~24 小时。</summary>
    public static int NormalizeMaxSeconds(int value) => value <= 0 ? 0 : Math.Clamp(value, 1, 86400);

    /// <summary>来源的界面文案。</summary>
    public static string SourceLabel(CaptureSourceKind kind) => kind switch
    {
        CaptureSourceKind.FullScreen => "全屏（主显示器）",
        CaptureSourceKind.Window => "窗口",
        _ => "区域",
    };
}

/// <summary>
/// FFmpeg 参数拼装（纯函数）。
///
/// <para><b>录制</b>：<c>gdigrab</c> 按 <c>-framerate / -draw_mouse / -offset_x / -offset_y / -video_size</c>
/// 采集桌面到中间文件（H.264 超快预设，只当临时素材）；<b>停止</b>时向 FFmpeg 的 stdin 写一个
/// <c>q</c>，让它正常收尾（MKV 不收尾会缺索引、下次读不了）。</para>
///
/// <para><b>GIF</b>：两遍编码 —— 第一遍 <c>palettegen</c> 生成调色板，第二遍 <c>paletteuse</c> 套用；
/// 比“直接录成 GIF”颜色准得多。GIF 默认 <c>-loop 0</c>（无限循环）。</para>
/// </summary>
public static class FfmpegArguments
{
    private const string CaptureCodec = "libx264";
    private const string CapturePreset = "ultrafast";
    private const string CaptureCrf = "18";

    /// <summary>公共前缀：安静 + 覆盖输出。<b>注意不能加 <c>-nostdin</c></b>（停止要靠 stdin 写 q）。</summary>
    private static List<string> Base() => new() { "-hide_banner", "-loglevel", "error", "-y" };

    /// <summary>
    /// 拼「开始采集」参数：gdigrab 桌面 → 中间 MKV。
    /// 停止方式是向进程 stdin 写 <c>q</c>（不是 kill），所以这里不能加 <c>-nostdin</c>。
    /// </summary>
    public static IReadOnlyList<string> BuildCapture(string intermediatePath, CapturePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediatePath);
        if (plan.Region.IsEmpty) throw new ArgumentException("采集区域不能为空", nameof(plan));

        var region = plan.Region.MakeEven();
        var args = Base();
        args.AddRange(new[]
        {
            "-f", "gdigrab",
            "-framerate", plan.Fps.ToString(CultureInfo.InvariantCulture),
            "-draw_mouse", plan.DrawMouse ? "1" : "0",
            "-offset_x", region.X.ToString(CultureInfo.InvariantCulture),
            "-offset_y", region.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", region.Width.ToString(CultureInfo.InvariantCulture) + "x" + region.Height.ToString(CultureInfo.InvariantCulture),
            "-i", "desktop",
        });

        if (plan.MaxSeconds > 0)
        {
            args.Add("-t");
            args.Add(plan.MaxSeconds.ToString(CultureInfo.InvariantCulture));
        }

        args.AddRange(new[]
        {
            "-an",
            "-c:v", CaptureCodec,
            "-preset", CapturePreset,
            "-crf", CaptureCrf,
            "-pix_fmt", "yuv420p",
            intermediatePath,
        });
        return args;
    }

    /// <summary>拼「截一张 PNG」参数（一次采集一帧，不需要中间文件）。</summary>
    public static IReadOnlyList<string> BuildScreenshot(string outputPath, CapturePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (plan.Region.IsEmpty) throw new ArgumentException("截图区域不能为空", nameof(plan));

        var region = plan.Region.MakeEven();
        var args = Base();
        args.AddRange(new[]
        {
            "-f", "gdigrab",
            "-framerate", "1",
            "-draw_mouse", plan.DrawMouse ? "1" : "0",
            "-offset_x", region.X.ToString(CultureInfo.InvariantCulture),
            "-offset_y", region.Y.ToString(CultureInfo.InvariantCulture),
            "-video_size", region.Width.ToString(CultureInfo.InvariantCulture) + "x" + region.Height.ToString(CultureInfo.InvariantCulture),
            "-i", "desktop",
            "-frames:v", "1",
            "-update", "1",
            outputPath,
        });
        return args;
    }

    /// <summary>第一遍：生成 GIF 调色板 PNG。</summary>
    public static IReadOnlyList<string> BuildGifPalette(string intermediatePath, string palettePath, GifExportSettings gif, CaptureRect region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(palettePath);

        var args = Base();
        args.AddRange(new[]
        {
            "-i", intermediatePath,
            "-vf", BuildGifFilterPrefix(gif, region) + ",palettegen=max_colors=" + gif.MaxColors.ToString(CultureInfo.InvariantCulture) + ":stats_mode=diff",
            "-frames:v", "1",
            palettePath,
        });
        return args;
    }

    /// <summary>第二遍：把中间素材 + 调色板合成 GIF。</summary>
    public static IReadOnlyList<string> BuildGif(string intermediatePath, string palettePath, string outputPath, GifExportSettings gif, CaptureRect region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(palettePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var filter = $"[0:v]{BuildGifFilterPrefix(gif, region)}[v];[v][1:v]paletteuse=dither={gif.Dither}:diff_mode=rectangle";
        var args = Base();
        args.AddRange(new[]
        {
            "-i", intermediatePath,
            "-i", palettePath,
            "-filter_complex", filter,
            "-loop", gif.Loop ? "0" : "-1",
            outputPath,
        });
        return args;
    }

    /// <summary>把中间素材重新编码成 MP4（H.264 + faststart，方便网页 / 手机直接播）。</summary>
    public static IReadOnlyList<string> BuildMp4(string intermediatePath, string outputPath, Mp4ExportSettings mp4, CaptureRect region)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var args = Base();
        args.AddRange(new[] { "-i", intermediatePath });

        var width = ResolveOutputWidth(mp4.ScaleWidth, region.Width);
        if (width < region.Width)
            args.AddRange(new[] { "-vf", $"scale={width.ToString(CultureInfo.InvariantCulture)}:-2:flags=lanczos" });

        args.AddRange(new[]
        {
            "-an",
            "-c:v", "libx264",
            "-preset", "medium",
            "-crf", mp4.Crf.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outputPath,
        });
        return args;
    }

    /// <summary>
    /// 实际输出宽度：<paramref name="requested"/> ≤ 0 或 ≥ 原宽 = 原宽（不放大）；
    /// 否则取偶数。GIF 和 MP4 共用同一口径。
    /// </summary>
    public static int ResolveOutputWidth(int requested, int sourceWidth)
    {
        if (sourceWidth <= 0) return Math.Max(2, requested);
        if (requested <= 0 || requested >= sourceWidth) return sourceWidth - (sourceWidth % 2);
        var width = requested - (requested % 2);
        return Math.Max(2, width);
    }

    /// <summary>GIF 两遍共用的前半段滤镜：限定帧率 + 可选等比缩放。</summary>
    private static string BuildGifFilterPrefix(GifExportSettings gif, CaptureRect region)
    {
        var parts = new List<string> { "fps=" + gif.Fps.ToString(CultureInfo.InvariantCulture) };
        var width = ResolveOutputWidth(gif.ScaleWidth, region.Width);
        if (width < region.Width)
            parts.Add("scale=" + width.ToString(CultureInfo.InvariantCulture) + ":-2:flags=lanczos");
        return string.Join(",", parts);
    }
}

/// <summary>输出文件名（纯函数）：清理前缀 / 拼时间戳 / 重名自动加序号。</summary>
public static class RecordingNames
{
    /// <summary>前缀最长保留多少字符（防止生成超长文件名）。</summary>
    public const int MaxPrefixLength = 40;

    /// <summary>
    /// 清洗文件前缀：去掉 Windows 不允许的字符、首尾空格 / 点，空值回退默认值。
    /// 注意 <c>Path.GetInvalidFileNameChars()</c> 在 Windows 上包含 <c>\ / : * ? " &lt; &gt; |</c>。
    /// </summary>
    public static string SanitizePrefix(string? prefix)
    {
        var text = (prefix ?? "").Trim();
        foreach (var ch in Path.GetInvalidFileNameChars())
            text = text.Replace(ch, '_');

        text = text.Trim(' ', '.');
        if (text.Length > MaxPrefixLength) text = text[..MaxPrefixLength].Trim(' ', '.');
        return text.Length == 0 ? ScreenRecorderConfig.DefaultFilePrefix : text;
    }

    /// <summary>拼文件名（不含目录）：<c>录屏-20260101-120000.gif</c>；<paramref name="suffix"/> ≥ 2 时加 <c> (2)</c>。</summary>
    public static string BuildFileName(string? prefix, DateTime time, string extension, int suffix = 1)
    {
        var ext = extension.Trim().TrimStart('.');
        if (ext.Length == 0) ext = "gif";

        var name = $"{SanitizePrefix(prefix)}-{time:yyyyMMdd-HHmmss}";
        if (suffix > 1) name += $" ({suffix.ToString(CultureInfo.InvariantCulture)})";
        return name + "." + ext;
    }

    /// <summary>
    /// 拼完整的输出路径；同名文件已存在时自动加 <c> (2)</c>、<c> (3)</c>…
    /// <paramref name="exists"/> 可注入（自检不碰真实文件系统）。
    /// </summary>
    public static string BuildOutputPath(
        string folder,
        string? prefix,
        DateTime time,
        string extension,
        Func<string, bool>? exists = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        exists ??= File.Exists;

        for (var suffix = 1; suffix <= 9999; suffix++)
        {
            var candidate = Path.Combine(folder, BuildFileName(prefix, time, extension, suffix));
            if (!exists(candidate)) return candidate;
        }

        // 理论到不了：时间戳 + 序号都没位置了，随便加个随机尾巴兜底
        return Path.Combine(folder, BuildFileName(prefix, time, extension) + "-" + Guid.NewGuid().ToString("N")[..6]);
    }
}

/// <summary>时间显示（纯函数）：<c>00:12</c> / <c>1:02:03</c>。</summary>
public static class RecordingTime
{
    /// <summary>把时长格式化成录制计时器文案（负数按 0 处理）。</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}"
            : $"{duration.Minutes:00}:{duration.Seconds:00}";
    }
}

/// <summary>
/// 区域框选浮窗的坐标换算（纯函数，自检可断言）：WPF 里框选到的是 **DIP**（设备无关像素），
/// FFmpeg <c>gdigrab</c> 要的是**物理像素**，两者差一个系统 DPI 缩放。
///
/// <para>换算口径：物理坐标 = (虚拟屏幕左上角 DIP + 框选相对坐标 DIP) × 缩放。
/// 程序是 WPF 默认的「系统 DPI 感知」，主屏缩放对全局有效；多显示器每台缩放不同时会有 1~2 像素误差。</para>
/// </summary>
public static class ScreenCoordinateMapper
{
    /// <summary>
    /// 把窗口内的 DIP 选框转成物理像素矩形。非法输入（NaN / 无穷 / 非正缩放）安全兜底：
    /// 缩放按 1 处理，坐标无效返回空矩形。
    /// </summary>
    public static CaptureRect DipToPhysical(
        double left,
        double top,
        double width,
        double height,
        double virtualScreenLeft,
        double virtualScreenTop,
        double dpiScaleX,
        double dpiScaleY)
    {
        if (!double.IsFinite(dpiScaleX) || dpiScaleX <= 0) dpiScaleX = 1;
        if (!double.IsFinite(dpiScaleY) || dpiScaleY <= 0) dpiScaleY = 1;
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(width) || !double.IsFinite(height))
            return CaptureRect.Empty;
        if (width <= 0 || height <= 0) return CaptureRect.Empty;

        var physicalLeft = (int)Math.Round((virtualScreenLeft + left) * dpiScaleX, MidpointRounding.AwayFromZero);
        var physicalTop = (int)Math.Round((virtualScreenTop + top) * dpiScaleY, MidpointRounding.AwayFromZero);
        var physicalRight = (int)Math.Round((virtualScreenLeft + left + width) * dpiScaleX, MidpointRounding.AwayFromZero);
        var physicalBottom = (int)Math.Round((virtualScreenTop + top + height) * dpiScaleY, MidpointRounding.AwayFromZero);
        return CaptureRect.FromPoints(physicalLeft, physicalTop, physicalRight, physicalBottom);
    }
}
