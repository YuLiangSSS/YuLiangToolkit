using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Live2D.Rendering;

/// <summary>
/// 模型用到的整张纹理图集（model3.json 的 Textures 数组）：一张 PNG 里排满所有网格的 UV。
///
/// <para><b>实际尺寸</b>：内置 DeepSeek 模型并不是 5 张 4096×4096，而是
/// 4096×8192 / 4096×8192 / 2048×2048 / 8192×8192 / 4096×4096。所以这里统一按「最长边 ≤ maxSize」
/// 等比降采样（2048 档 = 长边 2048，5 张合计约 64 MB；4096 档更清晰但更占内存）。</para>
///
/// <para><b>降采样</b>：先让 WIC 按 2 倍目标尺寸解码（避免 8192² 一次性解出 256 MB），
/// 再用「预乘 alpha 加权」的箱式滤波缩到目标尺寸 —— 直接平均非预乘 RGBA 会让透明边缘发黑/发怪色。
/// 每一步都可取消，进度回调 0~1。</para>
///
/// <para><b>线程</b>：<see cref="Load"/> 可在后台线程调用（只产出普通数组，不碰 WPF 视觉对象）；
/// 加载完成后像素数组<b>只读</b>，可被多个渲染器共享。</para>
/// <para>实现范围：<c>Rendering/**</c>（渲染器 / 纹理 / 位图封装）。</para>
/// </summary>
public sealed class TextureAtlasSet : IDisposable
{
    private readonly TextureAtlas[] _atlases;

    private TextureAtlasSet(TextureAtlas[] atlases) => _atlases = atlases;

    /// <summary>图集张数（= model3.json Textures 数量）。</summary>
    public int Count => _atlases.Length;

    /// <summary>取第 index 张图集（越界抛 <see cref="ArgumentOutOfRangeException"/>）。</summary>
    public TextureAtlas this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_atlases.Length) throw new ArgumentOutOfRangeException(nameof(index), index, $"纹理下标越界（共 {_atlases.Length} 张）");
            return _atlases[index];
        }
    }

    /// <summary>
    /// 加载全部图集（相对/绝对路径都行；后台线程可调）。
    /// </summary>
    /// <param name="texturePaths">按 model3.json Textures 顺序的 PNG 路径。</param>
    /// <param name="maxSize">最长边上限（建议 2048；2048/4096 两档都支持，其它值按同样规则处理）。</param>
    /// <param name="progress">进度回调（0~1，可能被调用多次）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static TextureAtlasSet Load(IReadOnlyList<string> texturePaths, int maxSize,
        Action<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texturePaths);
        if (maxSize < 16) maxSize = 16;
        if (maxSize > 8192) maxSize = 8192;

        var count = texturePaths.Count;
        var atlases = new TextureAtlas[count];
        progress?.Invoke(0);
        if (count == 0) return new TextureAtlasSet(atlases);

        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = texturePaths[i] ?? throw new ArgumentException("纹理路径里含 null", nameof(texturePaths));
            if (!File.Exists(path)) throw new FileNotFoundException("找不到纹理图集 PNG", path);

            var slot = i;
            // 单张内部进度：解码占 0.6，降采样占 0.4
            atlases[i] = LoadOne(path, maxSize, fraction =>
            {
                progress?.Invoke((slot + fraction) / count);
            }, cancellationToken);
            progress?.Invoke((slot + 1.0) / count);
        }

        return new TextureAtlasSet(atlases);
    }

    /// <summary>加载一张 PNG：解码成 BGRA32、按 maxSize 降采样。</summary>
    private static TextureAtlas LoadOne(string path, int maxSize, Action<double>? progress, CancellationToken ct)
    {
        var bytes = File.ReadAllBytes(path);

        // ---------- 读原始尺寸（PNG 头直接解析，避免为了量尺寸把 8192² 解出来） ----------
        int srcWidth, srcHeight;
        if (!TryReadPngSize(bytes, out srcWidth, out srcHeight))
        {
            // 非 PNG：交给 WIC 报尺寸（罕见分支）
            using var probeStream = new MemoryStream(bytes, false);
            var probe = BitmapDecoder.Create(probeStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var probeFrame = probe.Frames[0];
            srcWidth = probeFrame.PixelWidth;
            srcHeight = probeFrame.PixelHeight;
        }

        if (srcWidth <= 0 || srcHeight <= 0) throw new InvalidDataException($"纹理尺寸非法：{path}");

        // 等比缩到「最长边 ≤ maxSize」
        var longest = Math.Max(srcWidth, srcHeight);
        var scale = longest > maxSize ? (double)maxSize / longest : 1.0;
        var dstWidth = Math.Max(1, (int)Math.Round(srcWidth * scale));
        var dstHeight = Math.Max(1, (int)Math.Round(srcHeight * scale));

        uint[] pixels;
        if (dstWidth == srcWidth && dstHeight == srcHeight)
        {
            // 不需要降采样：直接解码
            ct.ThrowIfCancellationRequested();
            pixels = DecodeRaw(bytes, out _, out _);
            progress?.Invoke(1.0);
        }
        else
        {
            // 先按 2 倍目标尺寸解码（内存友好），再箱式滤波到目标尺寸
            var preWidth = Math.Min(srcWidth, Math.Max(dstWidth, dstWidth * 2));
            var preHeight = Math.Min(srcHeight, Math.Max(dstHeight, dstHeight * 2));
            // 只在确实大于目标时才让 WIC 缩放
            var prePixels = DecodeScaled(bytes, preWidth, ct, out var preW, out var preH);
            progress?.Invoke(0.6);
            ct.ThrowIfCancellationRequested();
            pixels = BoxDownsample(prePixels, preW, preH, dstWidth, dstHeight, ct);
            progress?.Invoke(1.0);
        }

        return new TextureAtlas
        {
            Width = dstWidth,
            Height = dstHeight,
            Pixels = pixels,
            Mips = BuildMips(pixels, dstWidth, dstHeight),
        };
    }

    /// <summary>
    /// 生成 mipmap 链（[0] = 原图；每级长宽减半，预乘 alpha 加权箱式平均）。
    /// <para>为什么需要：2048 档 5 张图集合计约 64 MB，远超 CPU 缓存；桌宠 420×860 实际只需
    /// 每个输出像素 ≈ 1 个纹素，采样更低级别的 mip 能把工作集压到几 MB，渲染快一个档次。</para>
    /// </summary>
    private static TextureMipLevel[] BuildMips(uint[] pixels, int width, int height)
    {
        const int maxLevels = 5; // 1/1、1/2、1/4、1/8、1/16

        var levels = new TextureMipLevel[maxLevels];
        var levelCount = 1;
        levels[0] = new TextureMipLevel(width, height, pixels);

        while (levelCount < maxLevels && width > 8 && height > 8)
        {
            var source = levels[levelCount - 1];
            var nextWidth = Math.Max(1, width / 2);
            var nextHeight = Math.Max(1, height / 2);
            var next = new uint[nextWidth * nextHeight];

            for (var y = 0; y < nextHeight; y++)
            {
                var sy0 = y * 2;
                var sy1 = Math.Min(sy0 + 2, height);
                for (var x = 0; x < nextWidth; x++)
                {
                    var sx0 = x * 2;
                    var sx1 = Math.Min(sx0 + 2, width);

                    int sumA = 0, sumR = 0, sumG = 0, sumB = 0, count = 0;
                    for (var sy = sy0; sy < sy1; sy++)
                    {
                        var row = sy * width;
                        for (var sx = sx0; sx < sx1; sx++)
                        {
                            var pixel = source.Pixels[row + sx];
                            var a = (int)(pixel >> 24);
                            count++;
                            if (a == 0) continue;
                            sumA += a;
                            sumR += (int)((pixel >> 16) & 0xFF) * a;
                            sumG += (int)((pixel >> 8) & 0xFF) * a;
                            sumB += (int)(pixel & 0xFF) * a;
                        }
                    }

                    if (count == 0 || sumA <= 0)
                    {
                        next[y * nextWidth + x] = 0;
                        continue;
                    }

                    var outA = sumA / count;
                    var outR = Math.Min(255, sumR / sumA);
                    var outG = Math.Min(255, sumG / sumA);
                    var outB = Math.Min(255, sumB / sumA);
                    next[y * nextWidth + x] = (uint)((outA << 24) | (outR << 16) | (outG << 8) | outB);
                }
            }

            width = nextWidth;
            height = nextHeight;
            levels[levelCount] = new TextureMipLevel(width, height, next);
            levelCount++;
        }

        if (levelCount == maxLevels) return levels;
        var trimmed = new TextureMipLevel[levelCount];
        Array.Copy(levels, trimmed, levelCount);
        return trimmed;
    }

    /// <summary>直接解码成 BGRA32 像素（不缩放）。</summary>
    private static uint[] DecodeRaw(byte[] bytes, out int width, out int height)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;

        var pixels = new uint[width * height];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>让 WIC 解码到接近 target 的尺寸（保持宽高比；返回实际尺寸）。</summary>
    private static uint[] DecodeScaled(byte[] bytes, int targetWidth, CancellationToken ct, out int width, out int height)
    {
        ct.ThrowIfCancellationRequested();
        var image = new BitmapImage();
        using (var stream = new MemoryStream(bytes, false))
        {
            image.BeginInit();
            image.StreamSource = stream;
            image.DecodePixelWidth = Math.Max(1, targetWidth);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
        }

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        ct.ThrowIfCancellationRequested();

        var pixels = new uint[width * height];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>
    /// 箱式滤波降采样（按面积平均，预乘 alpha 加权，避免透明边缘发黑）。
    /// 源尺寸 ≥ 目标尺寸；每目标像素覆盖源图的整数矩形区域。
    /// </summary>
    private static uint[] BoxDownsample(uint[] src, int srcWidth, int srcHeight, int dstWidth, int dstHeight, CancellationToken ct)
    {
        var dst = new uint[dstWidth * dstHeight];

        // 每列 / 每行覆盖的源区间边界（整数，保证不重不漏）
        var xs = new int[dstWidth + 1];
        for (var x = 0; x <= dstWidth; x++) xs[x] = (int)((long)x * srcWidth / dstWidth);
        var ys = new int[dstHeight + 1];
        for (var y = 0; y <= dstHeight; y++) ys[y] = (int)((long)y * srcHeight / dstHeight);

        // 逐行累加：先把「源行 (startX..endX) 的像素和」算成一维，再叠到目标行。
        var columnCount = new int[dstWidth];
        for (var x = 0; x < dstWidth; x++) columnCount[x] = Math.Max(1, xs[x + 1] - xs[x]);

        for (var dy = 0; dy < dstHeight; dy++)
        {
            if ((dy & 63) == 0) ct.ThrowIfCancellationRequested();
            var sy0 = ys[dy];
            var sy1 = ys[dy + 1];
            if (sy1 <= sy0) sy1 = Math.Min(srcHeight, sy0 + 1);

            for (var dx = 0; dx < dstWidth; dx++)
            {
                var sx0 = xs[dx];
                var sx1 = xs[dx + 1];
                if (sx1 <= sx0) sx1 = Math.Min(srcWidth, sx0 + 1);

                int sumA = 0, sumR = 0, sumG = 0, sumB = 0, count = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var rowStart = sy * srcWidth;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var pixel = src[rowStart + sx];
                        var a = (int)(pixel >> 24);
                        if (a == 0) { count++; continue; } // 全透明像素不贡献颜色
                        sumA += a;
                        sumR += (int)((pixel >> 16) & 0xFF) * a;
                        sumG += (int)((pixel >> 8) & 0xFF) * a;
                        sumB += (int)(pixel & 0xFF) * a;
                        count++;
                    }
                }

                if (count == 0) { dst[dy * dstWidth + dx] = 0; continue; }

                // 输出 alpha = 区域平均 alpha；RGB = 预乘加权平均后再解预乘
                var outA = sumA / count;
                if (outA <= 0) { dst[dy * dstWidth + dx] = 0; continue; }
                var outR = sumR / sumA;
                var outG = sumG / sumA;
                var outB = sumB / sumA;
                if (outR > 255) outR = 255;
                if (outG > 255) outG = 255;
                if (outB > 255) outB = 255;
                dst[dy * dstWidth + dx] = (uint)((outA << 24) | (outR << 16) | (outG << 8) | outB);
            }
        }

        return dst;
    }

    /// <summary>从 PNG 的数据块里读宽高（只支持 PNG；不是 PNG 返回 false）。</summary>
    private static bool TryReadPngSize(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        if (bytes.Length < 24) return false;
        // PNG 签名 8 字节 + 长度 4 + "IHDR" 4 + 宽 4 + 高 4
        if (bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47) return false;
        if (!(bytes[12] == (byte)'I' && bytes[13] == (byte)'H' && bytes[14] == (byte)'D' && bytes[15] == (byte)'R')) return false;
        width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width > 0 && height > 0;
    }

    /// <summary>
    /// 释放逻辑上的引用（像素数组本身被渲染器共享，不在这里清零；集合被回收后数组自然释放）。
    /// 调用 <see cref="Load"/> 的同一个实例不应在渲染器仍使用时 Dispose。
    /// </summary>
    public void Dispose()
    {
        // 故意留空：纹理只读共享；删除引用交给 GC。
    }
}

/// <summary>一张解码后的 BGRA32 纹理图集（非预乘；渲染时自己乘 alpha）。</summary>
public sealed class TextureAtlas
{
    /// <summary>实际宽度（降采样后）。</summary>
    public int Width { get; init; }

    /// <summary>实际高度（降采样后）。</summary>
    public int Height { get; init; }

    /// <summary>像素数据（内存里 B,G,R,A 顺序，小端读成 uint 即 0xAARRGGBB；长度 = Width * Height；非预乘）。</summary>
    public uint[] Pixels { get; init; } = Array.Empty<uint>();

    /// <summary>Mipmap 链：[0] 就是 <see cref="Pixels"/>，之后每级长宽减半（渲染器按输出分辨率选层）。</summary>
    public TextureMipLevel[] Mips { get; init; } = Array.Empty<TextureMipLevel>();
}

/// <summary>一级 mipmap：尺寸 + BGRA32 非预乘像素。</summary>
public readonly struct TextureMipLevel
{
    public TextureMipLevel(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>该级宽度。</summary>
    public int Width { get; }

    /// <summary>该级高度。</summary>
    public int Height { get; }

    /// <summary>该级像素数据（0xAARRGGBB，非预乘）。</summary>
    public uint[] Pixels { get; }
}
