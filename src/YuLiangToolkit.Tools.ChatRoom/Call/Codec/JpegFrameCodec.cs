using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.ChatRoom.Call;

/// <summary>
/// MJPEG 视频的编解码：本机摄像头的 BGRA 预览帧 ↔ 网上传的 JPEG 字节。
///
/// <para><b>为什么是 JPEG / MJPEG</b>：项目不引第三方编解码库，而 WPF 自带
/// <see cref="JpegBitmapEncoder"/> / <see cref="JpegBitmapDecoder"/> —— 局域网群聊里
/// 「每帧独立编码的 JPEG」实现最简单、丢了帧也不影响后续帧（没有关键帧依赖），
/// 代价是压缩效率不如 H.264，带宽就是「帧大小 × 帧率」（通话面板里能看到实测值）。</para>
///
/// <para><b>线程模型 / WPF 亲和性</b>：<see cref="BitmapSource"/> 是 <c>Freezable</c>，
/// 未冻结前有线程亲和性、不能跨线程用。本类返回的图像**全部在本方法内 <c>Freeze()</c>**，
/// 所以：摄像头 worker 线程编码 JPEG、网络读线程解码远端帧、UI 线程绑定 Image 都是安全的。
/// 这些操作不依赖 WPF 的 <c>Dispatcher</c>，后台线程（含 MTA 线程）可以放心调用。</para>
///
/// <para><b>像素格式</b>：BGRA32、行优先、自上而下，<c>stride = width * 4</c>，
/// 和 <see cref="CameraFrameData.Bgra"/> / <see cref="CallRawFrame.Bytes"/>（本机预览）
/// 完全一致；JPEG 编解码会丢掉 alpha（按不透明处理）。</para>
///
/// <para><b>边界</b>：质量参数会被夹到 30~90（太低面部糊成一团，太高带宽翻倍）；
/// 尺寸 ≤ 0 / 数组为空 / 长度不足 <c>width * height * 4</c> 时抛
/// <see cref="ArgumentException"/>，正常链路上不会出现（降采样后的帧尺寸是算出来的）。</para>
/// </summary>
public static class JpegFrameCodec
{
    /// <summary>JPEG 质量下限（再低面部细节就没了，通话画质档位最低 52）。</summary>
    private const int MinQuality = 30;

    /// <summary>JPEG 质量上限（再高对 320×240 的收益很小、带宽却明显上涨）。</summary>
    private const int MaxQuality = 90;

    /// <summary>
    /// 把一帧 BGRA32 编码成 JPEG 字节。返回的数组是新的，可以直接入队 / 发送。
    /// </summary>
    /// <param name="bgra">BGRA32 像素，长度至少 <c>width * height * 4</c>。</param>
    /// <param name="width">帧宽（像素）。</param>
    /// <param name="height">帧高（像素）。</param>
    /// <param name="quality">JPEG 质量（30~90，越界自动夹取）。</param>
    public static byte[] EncodeBgra(byte[] bgra, int width, int height, int quality)
    {
        ValidateBgra(bgra, width, height);

        var stride = width * 4;
        // 先建一张不依赖调用方缓冲的 BitmapSource：编码是同步的，这里不会把 bgra 的引用留到方法外
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
        source.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, MinQuality, MaxQuality) };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 把一张 JPEG 解码成已冻结的 <see cref="BitmapSource"/>（可以直接跨线程交给 UI）。
    /// </summary>
    /// <param name="jpeg">远端收到的 JPEG 字节。</param>
    public static BitmapSource DecodeToBitmap(byte[] jpeg)
    {
        if (jpeg is null || jpeg.Length == 0)
            throw new ArgumentException("JPEG 数据为空", nameof(jpeg));

        using var stream = new MemoryStream(jpeg, writable: false);
        var image = new BitmapImage();
        image.BeginInit();
        // OnLoad：解码在这一步一次做完，之后原字节数组 / 流都可以释放
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();   // 冻结后可以跨线程传（远程帧是在网络读线程解的）
        return image;
    }

    /// <summary>
    /// 把一帧 BGRA32 变成已冻结的 <see cref="BitmapSource"/>（本机预览显示用，不经过 JPEG）。
    /// </summary>
    public static BitmapSource FromBgra(byte[] bgra, int width, int height)
    {
        ValidateBgra(bgra, width, height);

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        image.Freeze();
        return image;
    }

    /// <summary>检查 BGRA 帧的尺寸与长度是否自洽。</summary>
    private static void ValidateBgra(byte[] bgra, int width, int height)
    {
        if (bgra is null) throw new ArgumentNullException(nameof(bgra));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), "帧宽必须为正数");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), "帧高必须为正数");
        if (width > int.MaxValue / 4) throw new ArgumentOutOfRangeException(nameof(width), "帧宽过大，stride 会溢出");
        if (bgra.Length < (long)width * height * 4)
            throw new ArgumentException($"BGRA 数据长度 {bgra.Length} 不够 {width}×{height} 一帧", nameof(bgra));
    }
}
