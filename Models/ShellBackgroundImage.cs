using System.IO;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 主页设置「背景图」的**文件存储**（v0.43.0，纯 IO、不碰界面，自检可直接调用）。
///
/// <para><b>图片存哪</b>：<c>Data\shell\background\</c> —— 属于用户数据（不是 Config、也不是可删的 Cache），
/// 配置里只存**文件名**（<see cref="ShellConfig.BackgroundImage"/>）。这样整个程序目录拷走 / 发布后背景图仍然有效，
/// 而且不会把绝对路径写进 JSON（换电脑 / 换盘符不会失效）。</para>
///
/// <para><b>安全约定</b>：配置里只接受**纯文件名**（不能带目录、不能是绝对路径、不能带 <c>..</c>），
/// 解析后必须落在 <see cref="Directory"/> 里，防止别人手改 JSON 让它去读 / 删别的文件。</para>
///
/// <para>用户选图时**复制一份**进数据目录（不引用原图）：之后原图被移动 / 删除也不影响背景；
/// 换图时先复制新图成功、再删旧图，复制失败不会把原来的背景弄丢。</para>
/// </summary>
public static class ShellBackgroundImage
{
    /// <summary>外壳自己的功能 Id（和 <c>MainViewModel.ShellToolId</c> 一致，图片放 <c>Data\shell\</c> 下）。</summary>
    public const string ShellToolId = "shell";

    /// <summary>背景图子文件夹名。</summary>
    public const string FolderName = "background";

    /// <summary>背景图目录：<c>Data\shell\background\</c>（只拼路径，用到才建）。</summary>
    public static string Directory
        => Path.Combine(AppPaths.ToolDataDir(ShellToolId), FolderName);

    /// <summary>
    /// 把配置里的文件名解析成**真实存在的图片路径**；没设置 / 文件不在 / 文件名不合法都返回 <c>null</c>。
    /// </summary>
    public static string? Resolve(string? fileName)
    {
        if (!IsSafeFileName(fileName)) return null;
        var path = Path.Combine(Directory, fileName!);
        return File.Exists(path) ? path : null;
    }

    /// <summary>配置里的背景图是不是还存在（设置页显示状态 / 清理用）。</summary>
    public static bool Exists(string? fileName) => Resolve(fileName) is not null;

    /// <summary>
    /// 把用户选的图片**复制**进 <see cref="Directory"/>，返回新文件名（放进 <see cref="ShellConfig.BackgroundImage"/>）。
    /// 文件名带时间戳避免重名 / 覆盖；扩展名保留原样的安全小写形式，认不出就用 <c>.img</c>
    /// （WPF 按文件内容解码，扩展名只影响资源管理器里看得舒服）。
    /// </summary>
    /// <exception cref="IOException">复制失败（磁盘满 / 没权限 / 源文件被占用）时抛给调用方弹提示。</exception>
    public static string Save(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("没有选择图片文件。", nameof(sourcePath));

        var fullSource = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSource))
            throw new FileNotFoundException("选中的图片文件不存在。", fullSource);

        System.IO.Directory.CreateDirectory(Directory);

        var extension = SafeExtension(Path.GetExtension(fullSource));
        var fileName = $"bg-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}";
        var destination = Path.Combine(Directory, fileName);

        File.Copy(fullSource, destination, overwrite: false);
        return fileName;
    }

    /// <summary>删掉某个背景图文件（换图成功后删旧的 / 清除背景图）；文件名不合法或文件不在就静默返回。</summary>
    public static void Delete(string? fileName)
    {
        var path = Resolve(fileName);
        if (path is null) return;
        try { File.Delete(path); }
        catch { /* 文件被占用 / 没权限：留一个孤儿文件不影响使用（下次换图还会再试） */ }
    }

    /// <summary>文件大小的人类可读文本（设置页显示“已选择 xx KB”用）；文件不在返回空串。</summary>
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

    /// <summary>是不是合法的背景图文件名：纯文件名、无目录分隔符、无 <c>..</c>、无非法字符。</summary>
    public static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (fileName.Length > 180) return false;
        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)) return false;
        if (fileName is "." or "..") return false;
        if (fileName.Contains("..", StringComparison.Ordinal)) return false;
        return fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    /// <summary>扩展名白名单化：只保留字母数字、最多 10 个字符；认不出就 <c>.img</c>。</summary>
    private static string SafeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return ".img";
        var trimmed = extension.Trim().ToLowerInvariant();
        if (trimmed.Length < 2 || trimmed.Length > 11 || trimmed[0] != '.') return ".img";
        foreach (var ch in trimmed.AsSpan(1))
            if (!char.IsAsciiLetterOrDigit(ch)) return ".img";
        return trimmed;
    }
}
