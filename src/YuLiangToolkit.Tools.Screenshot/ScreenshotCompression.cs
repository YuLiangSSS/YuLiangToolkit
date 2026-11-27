using System.IO;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>截图压缩：不影响编辑覆盖层，只在成品输出前按档位做一次 JPEG 重编码。</summary>
public static class ScreenshotCompression
{
    public const string None = "none";
    public const string Light = "light";
    public const string Standard = "standard";
    public const string High = "high";

    /// <summary>档位对应的 JPEG 质量；0 = 不压缩。</summary>
    public static int QualityFor(string? mode) => mode switch
    {
        Light => 92,
        Standard => 82,
        High => 70,
        _ => 0,
    };

    /// <summary>
    /// 按档位压缩成新的冻结 BitmapSource；不压缩时直接返回原图。
    /// 开启压缩后最终保存由 ViewModel 统一使用 JPG，避免 PNG 容器把体积又拉回去。
    /// </summary>
    public static BitmapSource Apply(BitmapSource source, string? mode)
    {
        var quality = QualityFor(mode);
        if (quality <= 0) return source;

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }
}
