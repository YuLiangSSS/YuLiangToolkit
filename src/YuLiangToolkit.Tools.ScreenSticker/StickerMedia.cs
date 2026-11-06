using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>一个可播放的动图：冻结好的帧 + 每帧播放间隔（毫秒，已按 <see cref="StickerTiming"/> 归一）。</summary>
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
/// 一张贴纸要显示的内容：静态图（PNG / JPG / BMP / …）或一段动画（GIF / APNG / 动图 WebP）。
///
/// <para><b>格式分派</b>（按**文件真实内容**判断，不只看后缀 —— 聊天软件经常把动图存成别的后缀）：</para>
/// <list type="bullet">
///   <item>PNG + <c>acTL</c> 块 → 本项目手写的 <see cref="StickerApng"/>（WIC 只解 APNG 第一帧）；</item>
///   <item>GIF / WebP → <see cref="StickerSkia"/>（WIC 解不了 WebP，也拼不回很多「优化 GIF」的局部帧）；</item>
///   <item>其它静态图 → WPF / WIC（<see cref="StickerImage.Load"/>）。</item>
/// </list>
///
/// <para><b>内存守卫</b>：帧全部解码缓存意味着内存 ≈ 帧数 × 单帧像素 × 4 字节。
/// 超过 <see cref="StickerTiming.MaxFrameCount"/> 帧或总像素预算的动画会退回「静态第一帧」并给一句
/// <see cref="Warning"/>（页面会记到日志），避免一个贴纸把内存吃爆；单帧宽超过 1280 的帧会先等比缩小。</para>
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

    /// <summary>静态画面（静态图本身；动图是动画第一帧）。</summary>
    public BitmapSource Still { get; }

    /// <summary>动画（静态图为 null）。</summary>
    public StickerAnimation? Animation { get; }

    /// <summary>加载时的提示（例如「动画太大已按静态显示」）；正常为 null。</summary>
    public string? Warning { get; }

    /// <summary>是不是会动的动画。</summary>
    public bool IsAnimated => Animation is not null;

    /// <summary>高 / 宽比例（取自静态画面）。</summary>
    public double Aspect => StickerGeometry.AspectOf(Still.PixelWidth, Still.PixelHeight);

    /// <summary>
    /// 读取一张图片；GIF / APNG / 动图 WebP 会尝试拆成动画帧。失败返回 null（原因写在
    /// <paramref name="error"/>，不抛异常）；动画不可用但静态能读时仍返回静态媒体，原因写在
    /// <see cref="Warning"/>。
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

        var head = ReadHead(full);
        string? warning = null;

        // ① APNG：PNG 容器 + acTL 块（系统 WIC / Skia 都不播 APNG 动画）
        if (IsPng(head) && StickerApng.IsApngFile(full))
        {
            try
            {
                var bytes = File.ReadAllBytes(full);
                var animation = StickerApng.TryLoadAnimation(bytes, ref warning);
                if (animation is not null)
                {
                    return new StickerMedia(full, animation.Frames[0], animation, warning);
                }
            }
            catch (Exception ex)
            {
                warning = "APNG 解码失败，已按静态第一帧显示：" + ex.Message;
            }
        }

        // ② GIF / WebP（按真实内容判断，兼容「WebP 改名成 .gif」这类套壳）
        if (IsGif(head) || IsWebp(head))
        {
            var animation = StickerSkia.TryLoadAnimation(full, ref warning);
            if (animation is not null)
            {
                return new StickerMedia(full, animation.Frames[0], animation, warning);
            }

            if (IsWebp(head))
            {
                // WebP 静态图 WIC 一般也解不了，直接交给 Skia
                var webpStill = StickerSkia.TryLoadStill(full);
                if (webpStill is not null)
                {
                    return new StickerMedia(full, webpStill, null, warning);
                }
                error = warning ?? "WebP 图片解码失败（系统缺少 WebP 解码器）";
                return null;
            }
        }

        // ③ 其它静态图：WPF / WIC
        var still = StickerImage.Load(full);
        if (still is null)
        {
            error = warning ?? "图片解码失败（文件可能损坏）";
            return null;
        }
        return new StickerMedia(full, still, null, warning);
    }

    /// <summary>把一帧裁成「独立、冻结、可长期持有」的位图（必要时等比缩小）。</summary>
    internal static BitmapSource FinalizeFrame(BitmapSource source, double scale = 1.0)
    {
        try
        {
            BitmapSource scaled = scale < 0.999
                ? new TransformedBitmap(source, new ScaleTransform(scale, scale))
                : source;

            var copy = new WriteableBitmap(scaled);
            copy.Freeze();
            return copy;
        }
        catch
        {
            // 极端像素格式：至少冻结原帧（能显示优先）
            if (source.CanFreeze) source.Freeze();
            return source;
        }
    }

    /// <summary>读文件头 16 字节（判断真实格式用）；读不到返回空数组。</summary>
    private static byte[] ReadHead(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[16];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    private static bool IsPng(byte[] head)
        => head.Length >= 8
           && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
           && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A;

    private static bool IsGif(byte[] head)
        => head.Length >= 6 && head[0] == 0x47 && head[1] == 0x49 && head[2] == 0x46;

    private static bool IsWebp(byte[] head)
        => head.Length >= 12
           && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
           && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50;
}
