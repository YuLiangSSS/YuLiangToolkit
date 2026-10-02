using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Tools.ChatRoom.Sessions;

namespace YuLiangToolkit.Tools.ChatRoom.Media;

/// <summary>
/// 图片与视频的“本地缩略”处理（纯本地，不占网络流量）。
///
/// <para><b>为什么不把预览图也发过去</b>：对方自己就能压，发预览等于白花流量；
/// 而单次流量上限正是要避免的东西。所以两端各自在收到 / 存好文件后本地生成一张缩略图，
/// 界面只加载缩略图（<c>DecodePixelWidth</c>），几 MB 的相册图不会把内存吃光。</para>
///
/// <para><b>EXIF 方向</b>：手机照片常常带 <c>Orientation</c> 标记，直接解码会躺倒 ——
/// 这里读那个标记并加上 <see cref="Rotation"/>，界面上看到的才是正的。</para>
/// </summary>
public static class ImageUtil
{
    /// <summary>能本地压出缩略图的扩展名（其它格式/损坏文件会安静地失败）。</summary>
    public static bool CanThumbnail(string? path)
        => ChatSessionNaming.KindOfFile(path) == ChatContentKind.Image;

    /// <summary>
    /// 生成缩略图文件。返回写成功的完整路径；失败（不是图片 / 解码不了）返回 null。
    /// </summary>
    /// <param name="sourcePath">原图。</param>
    /// <param name="targetPath">缩略图写到哪（<c>.jpg</c>）。</param>
    /// <param name="maxEdge">长边像素上限。</param>
    /// <param name="quality">JPEG 质量 1~100。</param>
    public static string? CreateThumbnail(string sourcePath, string targetPath, int maxEdge = 1600, int quality = 82)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            maxEdge = Math.Clamp(maxEdge, 64, 8000);
            quality = Math.Clamp(quality, 30, 100);

            using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];

            var rotation = ReadOrientation(frame);
            BitmapSource source = rotation == 0 ? frame : new TransformedBitmap(frame, new RotateTransform(rotation));

            var scale = Math.Min(1.0, (double)maxEdge / Math.Max(source.PixelWidth, source.PixelHeight));
            var target = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            target.Freeze();

            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
            encoder.Save(output);
            return targetPath;
        }
        catch
        {
            return null;   // 不是图片 / 解码器缺失 / 磁盘满：安静失败，界面照样能“打开原文件”
        }
    }

    /// <summary>把 EXIF 方向换算成需要旋转的角度（0 = 不用转）。</summary>
    private static double ReadOrientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is not BitmapMetadata metadata) return 0;
            const string query = "System.Photo.Orientation";
            if (!metadata.ContainsQuery(query)) return 0;
            return metadata.GetQuery(query) switch
            {
                2 => 0,        // 水平镜像：这里的简化处理不镜像，只求不躺倒
                3 => 180,
                4 => 0,
                5 => 0,
                6 => 90,
                7 => 0,
                8 => 270,
                _ => 0,
            };
        }
        catch { return 0; }
    }

    /// <summary>读一张图的像素尺寸（拿不到返回 <c>(0, 0)</c>）。</summary>
    public static (int Width, int Height) GetPixelSize(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (0, 0); }
    }

    /// <summary>估算某个长边上限 + 质量下，一张图大概会变成多大（界面上显示预估大小用）。</summary>
    public static string EstimateThumbnailText(string path, int maxEdge, int quality)
    {
        var (width, height) = GetPixelSize(path);
        if (width == 0) return "（读不出图片尺寸）";
        var scale = Math.Min(1.0, (double)maxEdge / Math.Max(width, height));
        var w = Math.Max(1, (int)(width * scale));
        var h = Math.Max(1, (int)(height * scale));
        var estimate = (long)(w * (long)h * (quality / 100.0) * 0.35);   // JPEG 经验系数
        return $"{w}×{h}（约 {Core.Storage.FileSize.Format(estimate)}）";
    }
}
