using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>一次冻结的桌面截图：位图 + 它在虚拟屏幕中的物理像素矩形。</summary>
public sealed record CapturedDesktop(BitmapSource Image, ScreenshotPixelRect Bounds);

/// <summary>
/// 屏幕捕获 / 裁剪 / 保存的 Win32 + WPF 小封装。
///
/// <para>用 GDI <c>BitBlt</c> 直接抓整块虚拟屏幕（多显示器 / 负坐标都走同一张位图），
/// 不经过 FFmpeg，也不依赖 System.Drawing。截图后立刻 <see cref="BitmapSource.Freeze"/>，
/// 可以安全地在 UI / 后台线程之间传递。</para>
/// </summary>
public static class ScreenCaptureService
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private const uint Srccopy = 0x00CC0020;
    private const uint Captureblt = 0x40000000;

    private const int DwmwaExtendedFrameBounds = 9;

    /// <summary>所有显示器组成的虚拟屏幕（物理像素）。</summary>
    public static ScreenshotPixelRect VirtualScreen
    {
        get
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0) return PrimaryScreen;
            return new ScreenshotPixelRect(x, y, width, height);
        }
    }

    /// <summary>主显示器（物理像素）。</summary>
    public static ScreenshotPixelRect PrimaryScreen
        => new(0, 0, Math.Max(1, GetSystemMetrics(SmCxScreen)), Math.Max(1, GetSystemMetrics(SmCyScreen)));

    /// <summary>冻结整块虚拟屏幕。</summary>
    public static CapturedDesktop CaptureVirtualScreen()
    {
        var bounds = VirtualScreen;
        if (bounds.IsEmpty) throw new InvalidOperationException("读取屏幕尺寸失败");

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC 失败");
        var memDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var oldBitmap = IntPtr.Zero;
        try
        {
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC 失败");

            bitmap = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
            if (bitmap == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleBitmap 失败");

            oldBitmap = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, Srccopy | Captureblt))
                throw new InvalidOperationException("BitBlt 抓屏失败");

            var image = Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return new CapturedDesktop(image, bounds);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>取当前前台窗口的可见边框（物理像素）；失败返回 null。</summary>
    public static ScreenshotPixelRect? GetForegroundWindowBounds()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var dwmRect, Marshal.SizeOf<Win32Rect>()) == 0)
            {
                var rect = ToPixelRect(dwmRect);
                if (!rect.IsEmpty) return rect.ClampTo(VirtualScreen);
            }
            if (GetWindowRect(hwnd, out var win32Rect))
            {
                var rect = ToPixelRect(win32Rect);
                if (!rect.IsEmpty) return rect.ClampTo(VirtualScreen);
            }
        }
        catch
        {
            // 窗口在查询期间关掉
        }
        return null;
    }

    /// <summary>按桌面坐标裁剪出一块冻结位图。</summary>
    public static BitmapSource Crop(BitmapSource source, ScreenshotPixelRect desktop, ScreenshotPixelRect region)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var crop = region.ClampTo(desktop).ToCropRect(desktop);
        if (crop.Width <= 0 || crop.Height <= 0)
            throw new ArgumentException("裁剪区域为空", nameof(region));

        var cropped = new CroppedBitmap(source, crop);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>保存位图到 PNG / JPG；目录不存在会自动创建。</summary>
    public static void Save(BitmapSource image, string path, string? format = null, int jpegQuality = 92)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);

        var ext = ScreenshotGeometry.NormalizeExtension(format ?? Path.GetExtension(path));
        BitmapEncoder encoder = ext == ".jpg"
            ? new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 1, 100) }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    /// <summary>编码成 PNG 字节（贴图 / 剪贴板 / 历史都用它）。</summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>取一个像素的颜色（坐标是位图内的物理像素）。</summary>
    public static Color? SampleColor(BitmapSource source, int x, int y)
    {
        if (source is null) return null;
        if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight) return null;

        try
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var pixel = new byte[4];
            converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
        }
        catch
        {
            return null;
        }
    }

    private static ScreenshotPixelRect ToPixelRect(Win32Rect rect)
    {
        var left = Math.Min(rect.Left, rect.Right);
        var top = Math.Min(rect.Top, rect.Bottom);
        return new ScreenshotPixelRect(left, top, Math.Abs(rect.Right - rect.Left), Math.Abs(rect.Bottom - rect.Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Win32Rect value, int size);
}
