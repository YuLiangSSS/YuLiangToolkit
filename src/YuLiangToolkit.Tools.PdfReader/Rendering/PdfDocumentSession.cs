using System.IO;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace YuLiangToolkit.Tools.PdfReader.Rendering;

/// <summary>PDF 打不开时给用户看的、话说清楚了的异常（ViewModel 直接把它当状态栏文案）。</summary>
public sealed class PdfReaderException : Exception
{
    public PdfReaderException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 一个已打开的 PDF：页数 / 每页原始尺寸 + 按需把某一页渲染成 WPF 位图。
///
/// <para><b>为什么用 Windows.Data.Pdf</b>：项目硬性规则不引第三方 NuGet 包，而 PDF 渲染必须有引擎；
/// <c>Windows.Data.Pdf</c> 是 Windows 10 1607+ 自带的系统 API（WinRT），不装任何东西、不进发布包。
/// 代价是主程序目标框架要升到 <c>net9.0-windows10.0.19041.0</c>（见 docs/dev/pitfalls.md）。</para>
///
/// <para><b>线程</b>：页面渲染在内部串行（<see cref="SemaphoreSlim"/>），
/// 渲染出来的 PNG 解码成冻结的 <see cref="BitmapSource"/>，可以安全交给 UI 线程显示。
/// 同一个实例不要在多个窗口里共用。</para>
/// </summary>
public sealed class PdfDocumentSession : IDisposable
{
    /// <summary>渲染宽度上限（像素）：再大也只是糊 / 吃内存，屏幕上看不出差别。</summary>
    public const int MaxRenderWidth = 1600;

    /// <summary>渲染宽度下限（像素）：太小的页也至少给这么多，避免糊成马赛克。</summary>
    public const int MinRenderWidth = 128;

    /// <summary>单页渲染像素总数上限（约 600 万，≈ 24 MB 位图）：极高 / 极宽的页也不会一次吃掉太多内存。</summary>
    public const long MaxRenderPixels = 6_000_000;

    private readonly PdfDocument _document;
    private readonly SemaphoreSlim _renderGate = new(1, 1);
    private bool _disposed;

    private PdfDocumentSession(string filePath, PdfDocument document, IReadOnlyList<PdfPageInfo> pages)
    {
        FilePath = filePath;
        _document = document;
        Pages = pages;
    }

    /// <summary>PDF 的完整路径。</summary>
    public string FilePath { get; }

    /// <summary>每一页的原始尺寸（DIP，只用宽高比排版）。</summary>
    public IReadOnlyList<PdfPageInfo> Pages { get; }

    /// <summary>页数。</summary>
    public int PageCount => Pages.Count;

    /// <summary>打开一个 PDF 文件（读页数 / 页尺寸；页面要等用到时才渲染）。</summary>
    public static async Task<PdfDocumentSession> OpenAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new PdfReaderException("没有选择 PDF 文件。");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(filePath.Trim().Trim('"'));
        }
        catch (Exception ex)
        {
            throw new PdfReaderException($"文件路径不合法：{filePath}", ex);
        }

        if (!File.Exists(fullPath))
            throw new PdfReaderException($"文件不存在：{fullPath}");

        try
        {
            var file = await StorageFile.GetFileFromPathAsync(fullPath);
            var document = await PdfDocument.LoadFromFileAsync(file);

            // 加密 PDF：本工具不弹密码框（简单功能），给一句明确提示。
            // 注意 PdfDocument 没有 Close/Dispose（WinRT 对象，靠 GC 释放），所以这里不手动释放。
            if (document.IsPasswordProtected)
                throw new PdfReaderException("这个 PDF 有密码保护，本工具暂不支持加密 PDF。");

            if (document.PageCount == 0)
                throw new PdfReaderException("这个 PDF 里没有任何页面。");

            var pageCount = (int)Math.Min(document.PageCount, int.MaxValue);
            var pages = new List<PdfPageInfo>(pageCount);
            for (var i = 0; i < pageCount; i++)
            {
                using var page = document.GetPage((uint)i);
                var size = page.Size;
                pages.Add(new PdfPageInfo(i, size.Width, size.Height));
            }

            return new PdfDocumentSession(fullPath, document, pages);
        }
        catch (PdfReaderException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PdfReaderException(
                "PDF 打不开：文件可能已加密（本工具暂不支持密码）、损坏，或者根本不是 PDF。" +
                Environment.NewLine + ex.Message, ex);
        }
    }

    /// <summary>
    /// 把第 <paramref name="pageIndex"/> 页渲染成位图（异步；内部串行）。
    /// <paramref name="pixelWidth"/> 是希望渲染成的设备像素宽，会自动夹到
    /// <see cref="MinRenderWidth"/> ~ <see cref="MaxRenderWidth"/>，并按 <see cref="MaxRenderPixels"/> 再缩。
    /// </summary>
    public async Task<BitmapSource> RenderPageAsync(int pageIndex, int pixelWidth)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), pageIndex, "页号超出范围。");

        var page = Pages[pageIndex];
        var width = EffectiveRenderWidth(page, pixelWidth);

        await _renderGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            byte[] png;
            using (var pdfPage = _document.GetPage((uint)pageIndex))
            {
                using var stream = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions { DestinationWidth = (uint)width };
                // WinRT 的 IAsyncAction 没有 ConfigureAwait（不是 Task），直接 await 即可。
                await pdfPage.RenderToStreamAsync(stream, options);
                png = await ReadStreamBytesAsync(stream);
            }

            if (png.Length == 0)
                throw new PdfReaderException($"第 {pageIndex + 1} 页渲染出来是空的。");

            return await Task.Run(() => DecodePng(png)).ConfigureAwait(false);
        }
        finally
        {
            _renderGate.Release();
        }
    }

    /// <summary>把渲染宽度夹进合理范围（纯函数，自检可以直接断言）。</summary>
    public static int EffectiveRenderWidth(PdfPageInfo page, int desiredPixelWidth)
    {
        var width = Math.Clamp(desiredPixelWidth, MinRenderWidth, MaxRenderWidth);
        if (!page.HasValidSize) return width;

        var height = width * page.HeightDip / page.WidthDip;
        var pixels = width * height;
        if (pixels <= MaxRenderPixels) return width;

        var scale = Math.Sqrt(MaxRenderPixels / pixels);
        return Math.Max(MinRenderWidth, (int)Math.Floor(width * scale));
    }

    private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
    {
        var length = (int)stream.Size;
        if (length <= 0) return Array.Empty<byte>();

        var bytes = new byte[length];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var total = 0;
        while (total < length)
        {
            var read = await reader.LoadAsync((uint)(length - total));
            if (read == 0) break;
            var chunk = new byte[read];
            reader.ReadBytes(chunk);
            System.Buffer.BlockCopy(chunk, 0, bytes, total, (int)read);
            total += (int)read;
        }

        if (total != length)
            throw new PdfReaderException("PDF 页面渲染出来的数据不完整。");
        return bytes;
    }

    private static BitmapSource DecodePng(byte[] png)
    {
        using var buffer = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.None;
        image.StreamSource = buffer;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>把打开 PDF 时的异常翻译成一句用户看得懂的话（界面上不会出现英文堆栈）。</summary>
    public static string DescribeOpenError(Exception exception)
    {
        if (exception is PdfReaderException known) return known.Message;
        if (exception is FileNotFoundException) return $"文件不存在：{exception.Message}";
        if (exception is UnauthorizedAccessException) return $"没有权限读取这个文件：{exception.Message}";
        return $"PDF 打不开：{exception.Message}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // ⚠ Windows.Data.Pdf.PdfDocument 没有 Close/Dispose（WinRT 对象，GC 回收）；
        // 这里只把渲染门关掉，防止 Dispose 后还有排队中的渲染继续动已废弃的会话。
        try { _renderGate.Dispose(); } catch { /* 忽略 */ }
    }
}
