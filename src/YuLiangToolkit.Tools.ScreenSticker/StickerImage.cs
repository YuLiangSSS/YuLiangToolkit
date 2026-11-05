using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 贴纸图片的解码 / 缩略图（全部走 WPF 自带的 WIC 解码，不引第三方包）。
///
/// <para>手写这几个小函数的原因：</para>
/// <list type="bullet">
///   <item>贴纸窗口会长期占着图，必须 <see cref="BitmapCacheOption.OnLoad"/>（立刻读进内存）再
///         <c>Freeze()</c>，否则原文件被占用 / 挪走后贴纸会变空白；</item>
///   <item>「最近使用」列表只要缩略图，用 <c>DecodePixelWidth</c> 让解码器直接缩图，别把几百张原图全解到内存；</item>
///   <item>超大图（手机 1 亿像素之类）按 <see cref="MaxDecodeWidth"/> 限制解码宽度，避免一个贴纸吃掉几百 MB。</item>
/// </list>
/// </summary>
public static class StickerImage
{
    /// <summary>支持贴到桌面的图片后缀（WPF / WIC 能解的常见格式）。</summary>
    public static readonly string[] Extensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp",
    };

    /// <summary>文件对话框的过滤器。</summary>
    public const string DialogFilter =
        "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.webp|所有文件|*.*";

    /// <summary>贴纸原图解码的最大宽度（像素）：超过就按比例缩小解码，省内存。</summary>
    public const int MaxDecodeWidth = 4096;

    /// <summary>「最近使用」缩略图的解码宽度（像素）。</summary>
    public const int ThumbnailWidth = 160;

    /// <summary>路径是不是支持的图片（只判断后缀，不读文件内容）。</summary>
    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return false;
        foreach (var candidate in Extensions)
        {
            if (string.Equals(extension, candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>路径存在、是支持的图片、并且能解码。给「最近使用」判断某条记录还能不能用。</summary>
    public static bool CanOpen(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        if (!IsSupportedExtension(path)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读取一张图片（文件立刻读进内存并冻结，之后可以跨线程 / 不占用原文件）。
    /// 失败（文件不在 / 格式不支持 / 解码异常）返回 null，调用方负责给用户提示，不抛异常。
    /// </summary>
    /// <param name="path">图片路径（相对路径也会按当前目录解析）。</param>
    /// <param name="decodePixelWidth">
    /// 解码宽度（像素）；0 = 按原图（但不会超过 <see cref="MaxDecodeWidth"/>）。
    /// 缩略图传 <see cref="ThumbnailWidth"/> 即可。
    /// </param>
    public static BitmapSource? Load(string? path, int decodePixelWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string full;
        try { full = Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return null; }
        if (!File.Exists(full)) return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(full, UriKind.Absolute);

            if (decodePixelWidth > 0)
            {
                image.DecodePixelWidth = Math.Clamp(decodePixelWidth, 8, 8192);
            }
            else
            {
                // 先探一次原图尺寸：特别大的图限制解码宽度（DecodePixelWidth 只会缩小不会放大）
                var pixels = ReadPixelSize(full);
                if (pixels is { } size && size.Width > MaxDecodeWidth)
                {
                    image.DecodePixelWidth = MaxDecodeWidth;
                }
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读图片的像素尺寸（不解码整张图，只读帧头）；失败返回 null。</summary>
    public static (int Width, int Height)? ReadPixelSize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return frame.PixelWidth > 0 && frame.PixelHeight > 0 ? (frame.PixelWidth, frame.PixelHeight) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>「最近使用」列表用的缩略图（固定解码宽度，失败返回 null）。</summary>
    public static BitmapSource? LoadThumbnail(string? path, int width = ThumbnailWidth)
        => Load(path, Math.Clamp(width, 8, 1024));
}
