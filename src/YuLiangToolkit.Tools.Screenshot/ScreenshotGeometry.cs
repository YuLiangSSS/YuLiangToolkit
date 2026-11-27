using System.IO;
using System.Windows;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>
/// 屏幕像素矩形（物理像素；多显示器时坐标可为负）。与 WPF 的 DIP 矩形分开，
/// 避免“框选在 DIP、裁图在物理像素”混用造成缩放偏移。
/// </summary>
public readonly record struct ScreenshotPixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>空矩形。</summary>
    public static ScreenshotPixelRect Empty => default;

    /// <summary>宽或高不是正数。</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>右边界（不含）。</summary>
    public int Right => X + Width;

    /// <summary>下边界（不含）。</summary>
    public int Bottom => Y + Height;

    /// <summary>由两个对角点构造（自动处理反向拖动）。</summary>
    public static ScreenshotPixelRect FromPoints(int x1, int y1, int x2, int y2)
        => new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>夹到另一个矩形内；完全不相交返回空。</summary>
    public ScreenshotPixelRect ClampTo(ScreenshotPixelRect area)
    {
        if (IsEmpty || area.IsEmpty) return Empty;
        var left = Math.Max(X, area.X);
        var top = Math.Max(Y, area.Y);
        var right = Math.Min(Right, area.Right);
        var bottom = Math.Min(Bottom, area.Bottom);
        if (right <= left || bottom <= top) return Empty;
        return new ScreenshotPixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>转成相对桌面左上角的裁剪矩形（CroppedBitmap 要非负坐标）。</summary>
    public Int32Rect ToCropRect(ScreenshotPixelRect desktop)
        => IsEmpty ? new Int32Rect(0, 0, 0, 0)
                   : new Int32Rect(X - desktop.X, Y - desktop.Y, Width, Height);

    /// <summary>显示成 <c>960×540</c>。</summary>
    public string SizeText => IsEmpty ? "未选择" : $"{Width}×{Height}";
}

/// <summary>
/// 截图坐标换算（纯函数，自检可以直接断言）：
/// WPF 覆盖层里框选的是 DIP，最终裁图是物理像素；覆盖层铺满虚拟屏幕，
/// 因此用“截图像素宽 / 覆盖层 DIP 宽”的比例换算，既支持 100%，也支持系统缩放。
/// </summary>
public static class ScreenshotGeometry
{
    /// <summary>
    /// 把覆盖层 DIP 选区映射成桌面物理像素矩形。
    /// </summary>
    public static ScreenshotPixelRect MapSelection(Rect selection, double overlayWidth, double overlayHeight,
        ScreenshotPixelRect desktop)
    {
        if (!double.IsFinite(overlayWidth) || !double.IsFinite(overlayHeight)
            || overlayWidth <= 0 || overlayHeight <= 0 || desktop.IsEmpty)
            return ScreenshotPixelRect.Empty;

        if (!double.IsFinite(selection.X) || !double.IsFinite(selection.Y)
            || !double.IsFinite(selection.Width) || !double.IsFinite(selection.Height))
            return ScreenshotPixelRect.Empty;

        var width = Math.Min(selection.Width, overlayWidth);
        var height = Math.Min(selection.Height, overlayHeight);
        if (width <= 0 || height <= 0) return ScreenshotPixelRect.Empty;

        var sx = desktop.Width / overlayWidth;
        var sy = desktop.Height / overlayHeight;

        var left = (int)Math.Round(desktop.X + selection.X * sx, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(desktop.Y + selection.Y * sy, MidpointRounding.AwayFromZero);
        var right = (int)Math.Round(desktop.X + (selection.X + width) * sx, MidpointRounding.AwayFromZero);
        var bottom = (int)Math.Round(desktop.Y + (selection.Y + height) * sy, MidpointRounding.AwayFromZero);

        return ScreenshotPixelRect.FromPoints(left, top, right, bottom).ClampTo(desktop);
    }

    /// <summary>把物理像素矩形换回覆盖层 DIP（用于“重复上次区域”预先框选）。</summary>
    public static Rect MapSelectionBack(ScreenshotPixelRect physical, double overlayWidth, double overlayHeight,
        ScreenshotPixelRect desktop)
    {
        if (physical.IsEmpty || desktop.IsEmpty || overlayWidth <= 0 || overlayHeight <= 0)
            return Rect.Empty;

        var sx = overlayWidth / desktop.Width;
        var sy = overlayHeight / desktop.Height;
        return new Rect(
            (physical.X - desktop.X) * sx,
            (physical.Y - desktop.Y) * sy,
            physical.Width * sx,
            physical.Height * sy);
    }

    /// <summary>把矩形夹到画布内（DIP）。</summary>
    public static Rect ClampToCanvas(Rect rect, double width, double height)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(width) || !double.IsFinite(height)) return Rect.Empty;

        var left = Math.Max(0, Math.Min(rect.Left, width));
        var top = Math.Max(0, Math.Min(rect.Top, height));
        var right = Math.Max(left, Math.Min(rect.Right, width));
        var bottom = Math.Max(top, Math.Min(rect.Bottom, height));
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>选区是否够大（小于 2 像素视为误点）。</summary>
    public static bool IsUsable(Rect rect) => rect.Width >= 2 && rect.Height >= 2
        && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    /// <summary>计算输出文件完整路径（目录不存在自动建；重名自动加 (1)）。</summary>
    public static string BuildOutputPath(string folder, string prefix, string extension, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("输出目录不能为空", nameof(folder));
        if (string.IsNullOrWhiteSpace(prefix)) prefix = ScreenshotConfig.DefaultFilePrefix;
        extension = NormalizeExtension(extension);

        Directory.CreateDirectory(folder);
        var safePrefix = SanitizeFileName(prefix.Trim());
        var stamp = now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(folder, $"{safePrefix}-{stamp}{extension}");
        var index = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(folder, $"{safePrefix}-{stamp}-{index}{extension}");
            index++;
        }
        return candidate;
    }

    /// <summary>后缀统一成带点的小写；只接受 png / jpg / jpeg。</summary>
    public static string NormalizeExtension(string? extension)
    {
        var value = (extension ?? "").Trim().TrimStart('.').ToLowerInvariant();
        return value switch
        {
            "jpg" or "jpeg" => ".jpg",
            _ => ".png",
        };
    }

    /// <summary>清理 Windows 文件名非法字符。</summary>
    public static string SanitizeFileName(string? name)
    {
        var text = string.IsNullOrWhiteSpace(name) ? ScreenshotConfig.DefaultFilePrefix : name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) text = text.Replace(c, '_');
        text = text.Trim().TrimEnd('.');
        return text.Length == 0 ? ScreenshotConfig.DefaultFilePrefix : text;
    }
}
