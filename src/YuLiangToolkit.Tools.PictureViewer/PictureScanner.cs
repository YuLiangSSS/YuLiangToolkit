using System.IO;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>长带里的一页：图片路径 + 原始像素尺寸（排版要用宽高比，加载图片时还不知道尺寸会先塌成 0 高）。</summary>
public sealed record PicturePage(string FilePath, int PixelWidth, int PixelHeight)
{
    /// <summary>文件名（界面上显示 / 排错用）。</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>宽高比（宽 / 高）；尺寸不合法时按 1 处理。</summary>
    public double AspectRatio => PixelWidth <= 0 || PixelHeight <= 0 ? 1 : (double)PixelWidth / PixelHeight;
}

/// <summary>
/// 扫文件夹 → 按页序排好的图片清单（**本功能唯一碰磁盘的地方**，自检自己造临时目录真扫一遍）。
///
/// <para>规则：</para>
/// <list type="bullet">
///   <item>只看所选文件夹**当前层**（与 PictureView 一致；漫画 / 条漫通常就是一个文件夹一话）；</item>
///   <item>扩展名：jpg / jpeg / png / bmp / gif / webp / tif / tiff（WPF 自带的解码器都认）；</item>
///   <item>按**自然顺序**排：<c>2.png</c> 在 <c>10.png</c> 前面（不是字典序），
///         纯数字文件名的图集自然就按数字页序排；</item>
///   <item>读不出尺寸的坏图直接跳过（不抛异常），读不动的子目录也不影响其它文件。</item>
/// </list>
/// </summary>
public static class PictureScanner
{
    /// <summary>能当图片收进来的扩展名（小写、带点）。</summary>
    public static readonly IReadOnlyList<string> Extensions = new[]
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> ExtensionSet =
        new(Extensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>这个路径看起来是不是图片（只看扩展名，不碰磁盘）。</summary>
    public static bool IsImageFile(string? path)
        => !string.IsNullOrWhiteSpace(path) && ExtensionSet.Contains(Path.GetExtension(path));

    /// <summary>
    /// 扫一个文件夹，返回**按页序排好**的图片路径。
    /// 文件夹不存在 / 没权限时返回空列表（调用方显示「没找到图片」即可）。
    /// </summary>
    public static IReadOnlyList<string> EnumerateFiles(string? folder)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder)) return result;

        string full;
        try { full = Path.GetFullPath(folder); }
        catch { return result; }

        if (!Directory.Exists(full)) return result;

        string[] files;
        try { files = Directory.GetFiles(full); }
        catch { return result; }

        foreach (var file in files)
        {
            if (IsImageFile(file)) result.Add(file);
        }

        result.Sort((a, b) => CompareNatural(Path.GetFileName(a), Path.GetFileName(b)));
        return result;
    }

    /// <summary>读一张图的原始像素尺寸；读不动（坏文件 / 不认识的格式）返回 null。</summary>
    public static PicturePage? ReadPage(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        try
        {
            using var stream = File.OpenRead(filePath);
            // DelayCreation + None：只读文件头拿尺寸，不把整张图解码进内存（几百张图也不卡）
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            return new PicturePage(filePath, frame.PixelWidth, frame.PixelHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 扫文件夹并读出全部页的尺寸。图片多时用多线程读（只读文件头，很快）。
    ///
    /// <paramref name="progress"/> 的参数是 <c>(已完成, 总数)</c>，在**后台线程**上调用，
    /// 界面层记得用 <c>Ui(...)</c> 切回界面线程再改控件状态。
    /// </summary>
    public static IReadOnlyList<PicturePage> ScanFolder(
        string? folder,
        Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var files = EnumerateFiles(folder);
        if (files.Count == 0) return Array.Empty<PicturePage>();

        var pages = new PicturePage?[files.Count];
        var finished = 0;
        var degree = Math.Clamp(Environment.ProcessorCount, 1, 8);

        try
        {
            Parallel.For(0, files.Count, new ParallelOptions
            {
                MaxDegreeOfParallelism = degree,
                CancellationToken = cancellationToken,
            }, i =>
            {
                pages[i] = ReadPage(files[i]);
                var done = Interlocked.Increment(ref finished);
                progress?.Invoke(done, files.Count);
            });
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<PicturePage>();
        }

        var result = new List<PicturePage>(files.Count);
        foreach (var page in pages)
        {
            if (page is not null) result.Add(page);
        }
        return result;
    }

    /// <summary>
    /// 自然顺序比较：把文件名里的连续数字段按数值比，所以 <c>2.png</c> &lt; <c>10.png</c>、
    /// <c>001.png</c> = <c>1.png</c> 之后再往后比。非数字段大小写不敏感。
    ///
    /// <para>⚠ 与「视频 / 音乐播放器」里的 <c>NaturalStringComparer</c> 是同一种算法。
    /// 没有直接引用那个项目的类型（两个功能互不依赖），改规则时两边一起改。</para>
    /// </summary>
    public static int CompareNatural(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startX = i;
                var startY = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numX = x.Substring(startX, i - startX).TrimStart('0');
                var numY = y.Substring(startY, j - startY).TrimStart('0');

                if (numX.Length != numY.Length) return numX.Length.CompareTo(numY.Length);
                var byValue = string.CompareOrdinal(numX, numY);
                if (byValue != 0) return byValue;

                // 数值相等（"001" 与 "1"）时继续比后面的内容，结果对单个文件稳定
                continue;
            }

            var cx = char.ToUpperInvariant(x[i]);
            var cy = char.ToUpperInvariant(y[j]);
            if (cx != cy) return cx.CompareTo(cy);
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
