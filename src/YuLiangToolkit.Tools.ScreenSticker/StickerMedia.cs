using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>一个可播放的 GIF：冻结好的帧 + 每帧播放间隔（毫秒，已按 <see cref="StickerTiming"/> 归一）。</summary>
public sealed class StickerAnimation
{
    /// <summary>全部帧（已冻结、可跨线程；超大帧会先缩到 <see cref="StickerTiming.MaxAnimatedFrameWidth"/> 宽）。</summary>
    public IReadOnlyList<BitmapSource> Frames { get; }

    /// <summary>每帧的原始间隔（毫秒，未乘播放速度）。</summary>
    public IReadOnlyList<int> DelaysMs { get; }

    public StickerAnimation(IReadOnlyList<BitmapSource> frames, IReadOnlyList<int> delaysMs)
    {
        Frames = frames;
        DelaysMs = delaysMs;
    }

    /// <summary>帧数。</summary>
    public int FrameCount => Frames.Count;
}

/// <summary>
/// 一张贴纸要显示的内容：静态图（PNG/JPG/…）或一个 GIF 动画。
///
/// <para><b>为什么要这一层</b>：以前 <c>StickerWindow</c> 只拿一个 <see cref="BitmapSource"/>（第一帧），
/// 现在同一张贴纸可能是多帧动画。这里统一负责「读文件 → 判断是静态图还是 GIF → GIF 拆帧 / 读帧延时 /
/// 内存守卫」，窗口只按帧表切 <c>Image.Source</c>，会话 / 恢复逻辑完全不用关心格式。</para>
///
/// <para><b>GIF 内存守卫</b>：帧全部解码缓存意味着内存 ≈ 帧数 × 单帧像素 × 4 字节。
/// 超过 <see cref="StickerTiming.MaxFrameCount"/> 帧或总像素预算的 GIF 会退回「静态第一帧」并给一句
/// <see cref="Warning"/>（页面会记到日志），避免一个贴纸把内存吃爆；单帧宽超过 1280 的帧会先等比缩小。</para>
///
/// <para><b>GIF 帧尺寸不一致</b>（经过极端优化的 GIF）：WIC 解出来各帧一般已经是整幅画面；真遇到尺寸不一致的
/// 就退回静态第一帧，宁可不动也不要播成扭曲画面。</para>
/// </summary>
public sealed class StickerMedia
{
    private StickerMedia(string path, BitmapSource still, StickerAnimation? animation, string? warning)
    {
        Path = path;
        Still = still;
        Animation = animation;
        Warning = warning;
    }

    /// <summary>图片完整路径。</summary>
    public string Path { get; }

    /// <summary>静态画面（静态图本身；GIF 是动画第一帧）。</summary>
    public BitmapSource Still { get; }

    /// <summary>GIF 动画（静态图为 null）。</summary>
    public StickerAnimation? Animation { get; }

    /// <summary>加载时的提示（例如「GIF 太大已按静态显示」）；正常为 null。</summary>
    public string? Warning { get; }

    /// <summary>是不是会动的 GIF。</summary>
    public bool IsAnimated => Animation is not null;

    /// <summary>高 / 宽比例（取自静态画面）。</summary>
    public double Aspect => StickerGeometry.AspectOf(Still.PixelWidth, Still.PixelHeight);

    /// <summary>
    /// 读取一张图片；GIF 会尝试拆成动画帧。失败返回 null（原因写在 <paramref name="error"/>，不抛异常）；
    /// 动画不可用但静态能读时仍返回静态媒体，原因写在 <see cref="Warning"/>。
    /// </summary>
    public static StickerMedia? Load(string? path, out string? error)
    {
        error = null;

        var full = ScreenStickerConfig.NormalizePath(path);
        if (full.Length == 0 || !File.Exists(full))
        {
            error = "找不到这张图片（可能已被移动或删除）";
            return null;
        }
        if (!StickerImage.IsSupportedExtension(full))
        {
            error = "不支持的图片格式";
            return null;
        }

        string? warning = null;
        if (string.Equals(System.IO.Path.GetExtension(full), ".gif", StringComparison.OrdinalIgnoreCase))
        {
            var animated = TryLoadAnimatedGif(full, ref warning);
            if (animated is not null) return animated;
        }

        var still = StickerImage.Load(full);
        if (still is null)
        {
            error = "图片解码失败（文件可能损坏）";
            return null;
        }
        return new StickerMedia(full, still, null, warning);
    }

    /// <summary>把 GIF 拆成动画帧；返回 null = 不要动画（单帧 / 太大 / 帧尺寸不一致 / 解码失败），原因写进 warning。</summary>
    private static StickerMedia? TryLoadAnimatedGif(string full, ref string? warning)
    {
        try
        {
            // OnDemand + 内存流：逐帧解码完立刻拷成独立的 WriteableBitmap，解完就把解码器 / 流释放；
            // 不会因为「所有帧一次性 OnLoad」把原图尺寸的全帧同时压在内存里。
            var bytes = File.ReadAllBytes(full);
            using var stream = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnDemand);

            var frameCount = decoder.Frames.Count;
            if (frameCount <= 1) return null;   // 单帧 GIF = 静态图

            var first = decoder.Frames[0];
            var width = first.PixelWidth;
            var height = first.PixelHeight;
            if (width <= 0 || height <= 0) return null;

            // 极端优化过的 GIF 帧尺寸可能不一致：不播，避免拉伸出鬼影
            for (var i = 0; i < frameCount; i++)
            {
                var probe = decoder.Frames[i];
                if (probe.PixelWidth != width || probe.PixelHeight != height)
                {
                    warning = "这个 GIF 的帧尺寸不一致（经过优化的 GIF），已按静态第一帧显示";
                    return null;
                }
            }

            var estimate = StickerTiming.EstimateAnimatedPixels(frameCount, width, height);
            if (!StickerTiming.CanAnimate(frameCount, estimate))
            {
                warning = $"这个 GIF 有 {frameCount} 帧、占内存较多，已按静态第一帧显示（避免贴纸占用过高内存）";
                return null;
            }

            var scale = width > StickerTiming.MaxAnimatedFrameWidth
                ? (double)StickerTiming.MaxAnimatedFrameWidth / width
                : 1.0;

            var frames = new List<BitmapSource>(frameCount);
            var delays = new List<int>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var frame = decoder.Frames[i];
                delays.Add(ReadDelayMs(frame));
                frames.Add(DetachFrame(frame, scale));
            }

            return new StickerMedia(full, frames[0], new StickerAnimation(frames.AsReadOnly(), delays.AsReadOnly()), null);
        }
        catch (Exception ex)
        {
            // 动画解码失败不致命：退回静态第一帧，用户至少还能贴
            warning = "GIF 动画解码失败，已按静态第一帧显示：" + ex.Message;
            return null;
        }
    }

    /// <summary>读 GIF 每帧延时（元数据单位 1/100 秒 → 毫秒），缺失 / 坏值用 <see cref="StickerTiming.DefaultFrameDelayMs"/>。</summary>
    private static int ReadDelayMs(BitmapSource frame)
    {
        try
        {
            if (frame is BitmapFrame bitmapFrame && bitmapFrame.Metadata is BitmapMetadata metadata)
            {
                var value = metadata.GetQuery("/grctlext/Delay");
                var hundredths = value switch
                {
                    ushort us => us,
                    short s => Math.Max(0, (int)s),
                    int i => i,
                    uint ui => (int)Math.Min(uint.MaxValue / 10, ui),
                    _ => 0,
                };
                if (hundredths > 0) return StickerTiming.NormalizeDelayMs(hundredths * 10);
            }
        }
        catch
        {
            // 某些 GIF 没有 GCE / WIC 不支持查询：按默认 100ms
        }
        return StickerTiming.DefaultFrameDelayMs;
    }

    /// <summary>
    /// 把解码器的一帧拷成「不依赖解码器 / 文件流」的独立位图（必要时等比缩小），并冻结。
    /// 冻结后可以安全地长期持有、给多个窗口共用。
    /// </summary>
    private static BitmapSource DetachFrame(BitmapSource frame, double scale)
    {
        try
        {
            BitmapSource source = scale < 0.999
                ? new TransformedBitmap(frame, new ScaleTransform(scale, scale))
                : frame;

            var copy = new WriteableBitmap(source);
            copy.Freeze();
            return copy;
        }
        catch
        {
            // 极端像素格式：退而求其次，至少冻结原帧（不缩小，但能显示）
            if (frame.CanFreeze) frame.Freeze();
            return frame;
        }
    }
}
