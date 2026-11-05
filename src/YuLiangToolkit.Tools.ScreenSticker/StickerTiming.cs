namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// GIF 播放的纯时间逻辑：默认速度 / 速度夹取、GIF 帧延时（1/100 秒）换算、帧数 / 内存预算守卫。
///
/// <para>抽成纯函数是为了自检能无头断言（见 <c>ScreenStickerChecks</c>）；窗口只负责按算出来的
/// 间隔切 <c>Image.Source</c>。</para>
/// </summary>
public static class StickerTiming
{
    /// <summary>默认播放速度（1× = 按 GIF 自带的帧延时播放）。</summary>
    public const double DefaultSpeed = 1.0;

    /// <summary>可调的最小速度（0.25× = 放慢到四分之一）。</summary>
    public const double MinSpeed = 0.25;

    /// <summary>可调的最大速度（3× = 三倍速）。</summary>
    public const double MaxSpeed = 3.0;

    /// <summary>GIF 帧延时缺失 / 为 0 时的兜底（浏览器惯例：100ms）。</summary>
    public const int DefaultFrameDelayMs = 100;

    /// <summary>比这更短的帧延时按「坏值」处理，用 <see cref="DefaultFrameDelayMs"/>（浏览器也这么夹）。</summary>
    public const int MinFrameDelayMs = 20;

    /// <summary>帧延时的上限（防止个别 GIF 里 65535 这种离谱值把动画卡死）。</summary>
    public const int MaxFrameDelayMs = 10000;

    /// <summary>一个 GIF 最多播多少帧；超过就按静态图显示（避免一个贴纸吃掉几百 MB 内存）。</summary>
    public const int MaxFrameCount = 800;

    /// <summary>
    /// 动画帧的总像素预算（约 120MB / 32bpp）：超过就按静态第一帧显示。
    /// 典型屏幕录制 GIF（几十帧、每帧几十万像素）远低于这个值。
    /// </summary>
    public const long MaxAnimatedPixels = 30_000_000;

    /// <summary>动画帧单边解码宽度上限（超大 GIF 缩放后再缓存，省内存）。</summary>
    public const int MaxAnimatedFrameWidth = 1280;

    /// <summary>界面上的「点一下换一档」预设档位（0.25× ~ 3×）。</summary>
    public static readonly IReadOnlyList<double> SpeedPresets = new[]
    {
        0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0,
    };

    /// <summary>把速度夹到 [<see cref="MinSpeed"/>, <see cref="MaxSpeed"/>]；NaN / 无穷 / 非正数回默认 1×。</summary>
    public static double ClampSpeed(double speed)
    {
        if (double.IsNaN(speed) || double.IsInfinity(speed) || speed <= 0) return DefaultSpeed;
        return Math.Clamp(speed, MinSpeed, MaxSpeed);
    }

    /// <summary>
    /// 把 GIF 元数据里的延时（单位 1/100 秒）换算成毫秒并夹取：0 / 10ms 这类值按浏览器惯例用 100ms，
    /// 超过 10 秒的按 10 秒。
    /// </summary>
    public static int NormalizeDelayMs(int rawDelayMs)
    {
        if (rawDelayMs > MaxFrameDelayMs) return MaxFrameDelayMs;
        if (rawDelayMs < MinFrameDelayMs) return DefaultFrameDelayMs;
        return rawDelayMs;
    }

    /// <summary>
    /// 实际播放间隔 = 帧延时 ÷ 速度。夹在 10ms ~ 10s（太快没意义、也会把 UI 线程打满）。
    /// </summary>
    public static double EffectiveIntervalMs(int baseDelayMs, double speed)
        => Math.Clamp(NormalizeDelayMs(baseDelayMs) / ClampSpeed(speed), 10, MaxFrameDelayMs);

    /// <summary>点一下速度按钮时换到下一档；不在预设档位上的速度换到最接近的下一档（循环）。</summary>
    public static double NextSpeed(double currentSpeed)
    {
        var current = ClampSpeed(currentSpeed);
        foreach (var preset in SpeedPresets)
        {
            if (preset > current + 0.001) return preset;
        }
        return SpeedPresets[0];
    }

    /// <summary>速度的显示文本：0.5× / 1× / 1.25×。</summary>
    public static string FormatSpeed(double speed) => ClampSpeed(speed).ToString("0.##") + "×";

    /// <summary>
    /// 这组帧值不值得做成动画：至少 2 帧、不超过 <see cref="MaxFrameCount"/> 帧、
    /// 总像素不超过 <see cref="MaxAnimatedPixels"/>。
    /// </summary>
    public static bool CanAnimate(int frameCount, long totalPixels)
        => frameCount > 1 && frameCount <= MaxFrameCount && totalPixels >= 0 && totalPixels <= MaxAnimatedPixels;

    /// <summary>按原图尺寸估算动画帧总像素（用于 <see cref="CanAnimate"/>；会先把宽度压到 1280 再算）。</summary>
    public static long EstimateAnimatedPixels(int frameCount, int frameWidth, int frameHeight)
    {
        if (frameCount <= 0 || frameWidth <= 0 || frameHeight <= 0) return 0;
        var scale = frameWidth > MaxAnimatedFrameWidth ? (double)MaxAnimatedFrameWidth / frameWidth : 1.0;
        var width = Math.Max(1, (int)Math.Round(frameWidth * scale));
        var height = Math.Max(1, (int)Math.Round(frameHeight * scale));
        return (long)frameCount * width * height;
    }
}
