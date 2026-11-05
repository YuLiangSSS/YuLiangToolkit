using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 一次「读剪贴板」的结果：复制的图片文件路径（可多张）+ 剪贴板里的位图（截图 / 网页复制，通常一张）。
/// </summary>
public sealed class ClipboardImageBatch
{
    /// <summary>剪贴板里的图片文件（来自「资源管理器里复制文件」；原文件直接用，不复制）。</summary>
    public List<string> FilePaths { get; } = new();

    /// <summary>剪贴板里的位图（截图 / 网页 / 聊天工具复制的一张图；已冻结，可跨线程）。</summary>
    public List<BitmapSource> Bitmaps { get; } = new();

    /// <summary>读取失败的原因（成功为 null）。</summary>
    public string? Error { get; internal set; }

    /// <summary>一批一共几张。</summary>
    public int TotalCount => FilePaths.Count + Bitmaps.Count;

    /// <summary>剪贴板里没有可用图片（且没有出错）。</summary>
    public bool IsEmpty => TotalCount == 0 && Error is null;
}

/// <summary>「从剪贴板贴图」的结果：成功几张、失败几张、失败原因。</summary>
public readonly record struct ClipboardPasteResult(int Added, int Failed, string? Error);

/// <summary>
/// 剪贴板读图：支持三类来源 ——
/// <list type="number">
///   <item><b>复制的图片文件</b>（资源管理器里可以一次复制多张）→ 直接用原文件贴，不复制；</item>
///   <item><b>PNG 自定义格式</b>（浏览器 / 截图工具常用，保留透明通道）→ 解码成位图；</item>
///   <item><b>标准 CF_BITMAP / CF_DIB</b>（截图、Word、聊天工具等）→ <c>Clipboard.GetImage()</c>。</item>
/// </list>
///
/// <para><b>位图为什么必须落成文件</b>：贴纸记录里存的是图片路径（重启恢复、最近使用都靠它），
/// 而剪贴板位图没有路径。所以统一编码成 PNG 存进 <c>Data\screen-sticker\clipboard\</c>：
/// 文件名 = <c>clipboard-&lt;内容哈希前16位&gt;.png</c>，同一张图反复粘贴只存一份（最近使用也不会重复）。
/// 放 Data 而不是 Cache：Cache 会被「清理缓存」删掉，贴纸就丢了。</para>
///
/// <para><b>线程</b>：<see cref="Read"/> 必须在 UI / STA 线程调用（WPF 剪贴板要求）；
/// <see cref="SaveBitmap"/> 的位图是冻结的，可以放后台线程编码。</para>
/// </summary>
public static class StickerClipboard
{
    /// <summary>剪贴板位图在功能数据目录下的子文件夹。</summary>
    public const string ClipboardFolder = "clipboard";

    /// <summary>文件名前缀。</summary>
    public const string FileNamePrefix = "clipboard-";

    /// <summary>剪贴板被占用时的重试次数与间隔（Windows 剪贴板是全局单例，经常被别的程序锁住）。</summary>
    private const int ReadAttempts = 5;
    private const int RetryDelayMs = 40;

    /// <summary>
    /// 读取当前剪贴板里的图片（UI / STA 线程调用）。失败不抛异常，原因写在
    /// <see cref="ClipboardImageBatch.Error"/>；剪贴板被占用会自动重试几次。
    /// </summary>
    public static ClipboardImageBatch Read()
    {
        var batch = new ClipboardImageBatch();

        for (var attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                ReadCore(batch);
                return batch;
            }
            catch (ExternalException)
            {
                // 剪贴板被别的程序占用（COMException 也是这个基类）：稍等再重试
                if (attempt < ReadAttempts) Thread.Sleep(RetryDelayMs);
            }
            catch (Exception ex)
            {
                batch.Error = $"读取剪贴板失败：{ex.Message}";
                return batch;
            }
        }

        batch.Error = "剪贴板正被其它程序占用，请过一会儿再试";
        return batch;
    }

    private static void ReadCore(ClipboardImageBatch batch)
    {
        // ① 复制的图片文件（可多张）：用原文件，保留原始画质 / 路径，不写 Data
        if (Clipboard.ContainsFileDropList())
        {
            var files = CollectImageFiles(Clipboard.GetFileDropList().Cast<string?>());
            if (files.Count > 0)
            {
                batch.FilePaths.AddRange(files);
                return;
            }
        }

        // ② PNG 自定义格式：浏览器 / 截图工具走这条，能保留透明通道（CF_DIB 有时会丢 alpha）
        foreach (var format in new[] { "PNG", "image/png" })
        {
            if (!Clipboard.ContainsData(format)) continue;
            if (Clipboard.GetData(format) is not Stream stream) continue;
            var image = DecodePngStream(stream);
            if (image is null) continue;
            batch.Bitmaps.Add(image);
            return;
        }

        // ③ 标准 DIB / BITMAP（截图、Word、聊天工具最常见的格式）
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null) batch.Bitmaps.Add(FreezeCopy(image));
        }
    }

    /// <summary>
    /// 过滤剪贴板 / 拖入的文件列表：只留「存在的、后缀是支持图片」的路径，并去重（忽略大小写）。
    /// 返回完整路径。传 null / 空集合返回空列表。
    /// </summary>
    public static IReadOnlyList<string> CollectImageFiles(IEnumerable<string?>? paths)
    {
        var result = new List<string>();
        if (paths is null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var full = ScreenStickerConfig.NormalizePath(raw);
            if (full.Length == 0) continue;
            if (!StickerImage.IsSupportedExtension(full)) continue;
            if (!File.Exists(full)) continue;
            if (!seen.Add(full)) continue;
            result.Add(full);
        }
        return result;
    }

    /// <summary>按 PNG 内容算稳定文件名（同一张图永远同一个名字，用于去重）。</summary>
    public static string SuggestedFileName(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        var hash = Convert.ToHexString(SHA256.HashData(pngBytes)).ToLowerInvariant();
        return FileNamePrefix + hash[..16] + ".png";
    }

    /// <summary>
    /// 把剪贴板位图编码成 PNG 存进 <c>Data\screen-sticker\clipboard\</c>，返回文件完整路径；
    /// 同一张图已存在时直接复用（不重复写盘）。失败返回 null（调用方给提示，不抛异常）。
    /// </summary>
    public static string? SaveBitmap(BitmapSource? bitmap)
    {
        if (bitmap is null) return null;

        try
        {
            var bytes = StickerImage.EncodePng(bitmap);
            if (bytes is null || bytes.Length == 0) return null;

            var relativePath = Path.Combine(ClipboardFolder, SuggestedFileName(bytes));
            var storage = ToolStorage.For(ScreenStickerConfig.ToolId);
            if (storage.FileExists(relativePath)) return storage.PathOf(relativePath);

            return storage.WriteBytes(relativePath, bytes, overwrite: false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把剪贴板里的 PNG 流解码成冻结位图；解不了返回 null。</summary>
    private static BitmapSource? DecodePngStream(Stream stream)
    {
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>剪贴板位图不一定可冻结：先试直接冻结，不行就复制一份再冻结（之后要放后台线程编码）。</summary>
    private static BitmapSource FreezeCopy(BitmapSource image)
    {
        try
        {
            if (image.CanFreeze)
            {
                if (!image.IsFrozen) image.Freeze();
                return image;
            }
        }
        catch { /* 冻结失败就走下面的复制 */ }

        try
        {
            var copy = new WriteableBitmap(image);
            copy.Freeze();
            return copy;
        }
        catch
        {
            return image;   // 极端格式：留在 UI 线程编码（SaveBitmap 调用方会判断 IsFrozen 决定线程）
        }
    }
}
