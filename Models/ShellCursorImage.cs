using System.IO;
using System.Linq;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 主页设置「应用内光标」的自定义图片存储（v0.91.0，纯 IO）。
/// 图片放 <c>Data\shell\cursor\</c>，配置只存文件名；历史文件不删，设置页可切回。
/// 文件名安全校验复用 <see cref="ShellBackgroundImage"/> 的规则。
/// </summary>
public static class ShellCursorImage
{
    public const string ShellToolId = "shell";
    public const string FolderName = "cursor";

    /// <summary>光标图片目录：<c>Data\shell\cursor\</c>（用到才建）。</summary>
    public static string Directory
        => Path.Combine(AppPaths.ToolDataDir(ShellToolId), FolderName);

    /// <summary>把配置里的文件名解析成真实存在的图片路径；不合法 / 文件不在返回 null。</summary>
    public static string? Resolve(string? fileName)
    {
        if (!ShellBackgroundImage.IsSafeFileName(fileName)) return null;
        var path = Path.Combine(Directory, fileName!);
        return File.Exists(path) ? path : null;
    }

    public static bool IsSafeFileName(string? fileName) => ShellBackgroundImage.IsSafeFileName(fileName);

    /// <summary>复制用户选的小图片进来，返回新文件名（时间戳避免重名）。</summary>
    /// <exception cref="IOException">复制失败时抛给调用方提示。</exception>
    public static string Save(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("没有选择图片文件。", nameof(sourcePath));

        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
            throw new FileNotFoundException("选中的图片文件不存在。", fullSource);

        System.IO.Directory.CreateDirectory(Directory);

        var extension = Path.GetExtension(fullSource);
        if (extension.Length is < 2 or > 11 || extension.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '.'))
            extension = ".img";
        var fileName = $"cursor-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension.ToLowerInvariant()}";
        File.Copy(fullSource, Path.Combine(Directory, fileName), overwrite: false);
        return fileName;
    }

    /// <summary>设置页显示状态用（文件名 / 没有 / 文件丢了）。</summary>
    public static string SizeText(string? fileName)
    {
        var path = Resolve(fileName);
        if (path is null) return "";
        try
        {
            var length = new FileInfo(path).Length;
            return length >= 1024 * 1024
                ? $"{length / 1024.0 / 1024.0:0.0} MB"
                : $"{Math.Max(1, length / 1024)} KB";
        }
        catch { return ""; }
    }
}
