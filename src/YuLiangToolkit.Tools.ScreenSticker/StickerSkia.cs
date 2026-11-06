using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// GIF / 动图 WebP 的逐帧解码（SkiaSharp，MIT）。
///
/// <para><b>为什么用 Skia 而不是 WIC</b>：Windows 自带的 WIC 能拆 GIF 帧，但很多聊天软件导出的 GIF
/// 是「优化 GIF」——WIC 每帧只返回一小块矩形（需要按 disposal/blend 自己拼回整幅画面，而且偏移量
/// 元数据并不总可靠）；WebP 更是系统没装解码器时完全打不开。Skia 的 <c>SKCodec</c> 对
/// GIF / WebP 都支持「按帧依赖合成」，直接给出整幅画面，且 WIC 的坑它都处理好了。</para>
///
/// <para>Skia 只负责解码成帧；动画播放仍然是 <see cref="Views.StickerWindow"/> 的定时器，
/// 速度 / 预算等逻辑继续走 <see cref="StickerTiming"/>。原生库缺失时降级为静态图，不抛异常。</para>
/// </summary>
internal static class StickerSkia
{
    /// <summary>
    /// 把 GIF / 动图 WebP 解码成逐帧位图。返回 null = 不是多帧 / 太大 / 解码失败 / 原生库缺失；
    /// 原因写进 warning。
    /// </summary>
    public static StickerAnimation? TryLoadAnimation(string fullPath, ref string? warning)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;

            var frameCount = codec.FrameCount;
            if (frameCount <= 1) return null;

            var canvasWidth = codec.Info.Width;
            var canvasHeight = codec.Info.Height;
            if (canvasWidth <= 0 || canvasHeight <= 0) return null;

            var estimate = StickerTiming.EstimateAnimatedPixels(frameCount, canvasWidth, canvasHeight);
            if (!StickerTiming.CanAnimate(frameCount, estimate))
            {
                warning = $"这个动图有 {frameCount} 帧、占内存较多，已按静态第一帧显示（避免贴纸占用过高内存）";
                return null;
            }

            var scale = canvasWidth > StickerTiming.MaxAnimatedFrameWidth
                ? (double)StickerTiming.MaxAnimatedFrameWidth / canvasWidth
                : 1.0;

            var info = new SKImageInfo(canvasWidth, canvasHeight, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var frameInfos = codec.FrameInfo;
            var frames = new List<BitmapSource>(frameCount);
            var delays = new List<int>(frameCount);

            // ⚠ Skia 的「帧合成」要复用同一个画布：partial 帧只写入变化区域，读之前画布里必须已经是
            // 上一帧的完整画面（新建一个空 SKBitmap 再喂进去只会得到一块局部图）。GIF / WebP 的
            // requiredFrame 一般是顺序链（0,1,2…），复用同一个 bitmap 即可；偶尔出现不按顺序的依赖
            // 就先退回静态图，别赌一副错画面。
            using var canvas = new SKBitmap(info);
            for (var i = 0; i < frameCount; i++)
            {
                var requiredFrame = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;
                if (requiredFrame >= 0 && requiredFrame != i - 1)
                {
                    warning = $"动图第 {i + 1} 帧依赖非前一帧（required={requiredFrame}），已按静态第一帧显示";
                    return null;
                }

                var options = requiredFrame >= 0
                    ? new SKCodecOptions(i, requiredFrame)
                    : new SKCodecOptions(i);

                var result = codec.GetPixels(info, canvas.GetPixels(), options);
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    warning = $"动图第 {i + 1} 帧解码失败（{result}），已按静态第一帧显示";
                    return null;
                }

                var duration = i < frameInfos.Length ? frameInfos[i].Duration : StickerTiming.DefaultFrameDelayMs;
                delays.Add(StickerTiming.NormalizeDelayMs(duration));
                frames.Add(StickerMedia.FinalizeFrame(ToBitmapSource(canvas), scale));
            }

            return new StickerAnimation(frames.AsReadOnly(), delays.AsReadOnly());
        }
        catch (Exception ex)
        {
            // 原生库缺失 / 版本不匹配 / 文件损坏：退回静态图，不让贴纸崩掉
            warning = "GIF / WebP 动画解码失败，已按静态第一帧显示：" + ex.Message;
            return null;
        }
    }

    /// <summary>单独解一张静态 WebP（WIC 通常解不了 WebP，所以静态也用 Skia）。失败返回 null。</summary>
    public static BitmapSource? TryLoadStill(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                SKColorType.Bgra8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(0));
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

            var image = ToBitmapSource(bitmap);
            var copy = new WriteableBitmap(image);
            copy.Freeze();
            return copy;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>SKBitmap（BGRA，直通 alpha）→ 冻结的 WPF 位图。</summary>
    private static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var stride = bitmap.RowBytes;
        var buffer = new byte[stride * height];
        Marshal.Copy(bitmap.GetPixels(), buffer, 0, buffer.Length);

        var writeable = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        writeable.WritePixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
        writeable.Freeze();
        return writeable;
    }
}
