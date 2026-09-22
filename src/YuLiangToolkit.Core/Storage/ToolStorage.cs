using System.Collections.Concurrent;
using System.Diagnostics;

namespace YuLiangToolkit.Core.Storage;

/// <summary>数据文件夹里的一个文件（<see cref="ToolStorage.List"/> 的返回项）。</summary>
/// <param name="FullPath">完整路径。</param>
/// <param name="RelativePath">相对该功能数据文件夹的路径（可含子目录）。</param>
/// <param name="Name">文件名（不含目录）。</param>
/// <param name="SizeBytes">字节数。</param>
/// <param name="LastWriteTime">最后修改时间。</param>
public sealed record StorageFile(
    string FullPath,
    string RelativePath,
    string Name,
    long SizeBytes,
    DateTime LastWriteTime)
{
    /// <summary>人类可读大小，例如 "1.5 MB"。</summary>
    public string SizeText => FileSize.Format(SizeBytes);

    /// <summary>最后修改时间文本，例如 "2025-01-01 12:00"。</summary>
    public string ModifiedText => LastWriteTime.ToString("yyyy-MM-dd HH:mm");

    /// <summary>扩展名（小写，含点），例如 ".swf"。</summary>
    public string Extension => Path.GetExtension(Name).ToLowerInvariant();
}

/// <summary>
/// 功能数据文件夹门面：每个功能独占 <c>Data\&lt;功能名&gt;\</c>，
/// 以后要放什么资源（swf、字体、模板、缓存…）都丢进去，工具通过本类读写。
///
/// <para><b>设计要点</b>：</para>
/// <list type="bullet">
///   <item>文件夹“首次真正写入时才创建”，避免一堆空目录；</item>
///   <item>所有相对路径都做越界校验，禁止 <c>..</c> 逃出功能自己的数据目录；</item>
///   <item>所有 IO 都不抛异常，失败返回 null / false / 空列表，工具界面自行提示；</item>
///   <item>同一 <c>(功能, 子文件夹)</c> 复用同一实例，路径信息随处可查。</item>
/// </list>
///
/// <para><b>典型用法</b>：</para>
/// <code>
/// var data = ToolStorage.For("flash-downloader");       // Data\flash-downloader\
/// data.EnsureCreated();                                  // 确保文件夹存在
/// var swf = data.PathOf("player", "ruffle.swf");         // 组装路径（不检查存在）
/// data.WriteBytes("ruffle.swf", bytes);                  // 写入资源
/// foreach (var f in data.List("*.swf")) Log($"{f.Name} {f.SizeText}");
/// data.Import(@"D:\下载\game.swf");                      // 把外部文件拷进来（重名自动加序号）
/// data.OpenInExplorer();                                 // 让用户直接看到文件夹
/// </code>
/// </summary>
public sealed class ToolStorage
{
    private static readonly ConcurrentDictionary<string, ToolStorage> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private ToolStorage(string toolId, string folderName)
    {
        ToolId = toolId;
        Directory = Path.Combine(AppPaths.DataRoot, AppPaths.SanitizeFolderName(folderName));
    }

    /// <summary>取（或创建）某功能的数据文件夹门面。</summary>
    /// <param name="toolId">功能 Id，例如 "flash-downloader"。</param>
    /// <param name="folderName">自定义小文件夹名（默认与 toolId 相同，用于手工约定别的目录名）。</param>
    public static ToolStorage For(string toolId, string? folderName = null)
    {
        var finalName = string.IsNullOrWhiteSpace(folderName) ? toolId : folderName!;
        var key = AppPaths.SanitizeFolderName(finalName);
        return Cache.GetOrAdd(key, _ => new ToolStorage(toolId, finalName));
    }

    /// <summary>功能 Id。</summary>
    public string ToolId { get; }

    /// <summary>数据小文件夹完整路径：<c>Data\&lt;功能名&gt;</c>。</summary>
    public string Directory { get; }

    /// <summary>数据文件夹当前是否存在。</summary>
    public bool Exists => System.IO.Directory.Exists(Directory);

    /// <summary>确保文件夹存在，返回其路径（失败也返回路径，调用方可用 <see cref="Exists"/> 复核）。</summary>
    public string EnsureCreated()
    {
        try { System.IO.Directory.CreateDirectory(Directory); }
        catch { /* 只读环境忽略 */ }
        return Directory;
    }

    /// <summary>组装功能数据目录下的路径（只拼路径，不创建、不检查存在）。</summary>
    public string PathOf(string relativePath) => ResolvePath(relativePath);

    /// <summary>组装功能数据目录下的路径，例如 <c>PathOf("player", "a.swf")</c>。</summary>
    public string PathOf(params string[] parts) => ResolvePath(Path.Combine(parts));

    /// <summary>文件是否存在。</summary>
    public bool FileExists(string relativePath) => File.Exists(ResolvePath(relativePath));

    /// <summary>文件大小（字节）；不存在返回 -1。</summary>
    public long GetSize(string relativePath)
    {
        try
        {
            var info = new FileInfo(ResolvePath(relativePath));
            return info.Exists ? info.Length : -1;
        }
        catch { return -1; }
    }

    /// <summary>列出数据文件夹里的文件（默认递归全部），按相对路径排序。</summary>
    /// <param name="searchPattern">通配符，例如 <c>"*.swf"</c>；默认 <c>"*"</c>。</param>
    /// <param name="recursive">是否递归子文件夹，默认 true。</param>
    public IReadOnlyList<StorageFile> List(string searchPattern = "*", bool recursive = true)
    {
        var result = new List<StorageFile>();
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return result;
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, searchPattern, option))
            {
                try
                {
                    var info = new FileInfo(file);
                    result.Add(new StorageFile(
                        info.FullName,
                        Path.GetRelativePath(Directory, info.FullName),
                        info.Name,
                        info.Length,
                        info.LastWriteTime));
                }
                catch { /* 单个文件失败忽略 */ }
            }
        }
        catch { /* 目录不可读忽略 */ }

        return result.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>数据文件夹总占用（字节）。</summary>
    public long TotalSize() => List().Sum(f => f.SizeBytes);

    /// <summary>
    /// 把外部文件拷进数据文件夹（例如把下载好的 swf 收进 <c>Data\flash-downloader\</c>）。
    /// 目标已存在且 <paramref name="overwrite"/> 为 false 时自动改名 <c>name (1).ext</c>。
    /// </summary>
    /// <returns>落盘后的文件信息；失败返回 null。</returns>
    public StorageFile? Import(string sourceFilePath, string? targetRelativePath = null, bool overwrite = false)
    {
        try
        {
            if (!File.Exists(sourceFilePath)) return null;
            EnsureCreated();

            var relative = string.IsNullOrWhiteSpace(targetRelativePath)
                ? Path.GetFileName(sourceFilePath)
                : targetRelativePath!;

            var destination = ResolvePath(relative);
            if (File.Exists(destination) && !overwrite)
                destination = MakeUniquePath(destination);

            var dir = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

            File.Copy(sourceFilePath, destination, overwrite: true);
            return ToStorageFile(destination);
        }
        catch { return null; }
    }

    /// <summary>把文本写进数据文件夹（自动建子目录）。成功返回完整路径，失败返回 null。</summary>
    public string? WriteText(string relativePath, string content, bool overwrite = true)
    {
        try
        {
            var path = PrepareWrite(relativePath, overwrite);
            if (path is null) return null;
            File.WriteAllText(path, content);
            return path;
        }
        catch { return null; }
    }

    /// <summary>把字节写进数据文件夹（二进制资源用这个）。成功返回完整路径，失败返回 null。</summary>
    public string? WriteBytes(string relativePath, byte[] content, bool overwrite = true)
    {
        try
        {
            var path = PrepareWrite(relativePath, overwrite);
            if (path is null) return null;
            File.WriteAllBytes(path, content);
            return path;
        }
        catch { return null; }
    }

    /// <summary>读文本；失败返回 null。</summary>
    public string? ReadText(string relativePath)
    {
        try
        {
            var path = ResolvePath(relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch { return null; }
    }

    /// <summary>读字节；失败返回 null。</summary>
    public byte[]? ReadBytes(string relativePath)
    {
        try
        {
            var path = ResolvePath(relativePath);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch { return null; }
    }

    /// <summary>删除数据文件夹里的一个文件。</summary>
    public bool Delete(string relativePath)
    {
        try
        {
            var path = ResolvePath(relativePath);
            if (!File.Exists(path)) return true;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>清空整个功能数据文件夹（危险操作，界面需二次确认）。</summary>
    public bool ClearAll()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return true;
            System.IO.Directory.Delete(Directory, recursive: true);
            return true;
        }
        catch { return false; }
    }

    /// <summary>在资源管理器里打开该功能的数据文件夹（不存在则先建）。</summary>
    public bool OpenInExplorer()
    {
        EnsureCreated();
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Directory, UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>在资源管理器里定位到某个文件（自动选中）。</summary>
    public bool RevealInExplorer(string relativePath)
    {
        try
        {
            var path = ResolvePath(relativePath);
            if (!File.Exists(path)) return OpenInExplorer();
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>为已存在的路径生成不重名的副本路径：<c>a.swf</c> → <c>a (1).swf</c>。</summary>
    public static string MakeUniquePath(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath) && !System.IO.Directory.Exists(fullPath)) return fullPath;
            var dir = Path.GetDirectoryName(fullPath) ?? ".";
            var name = Path.GetFileNameWithoutExtension(fullPath);
            var ext = Path.GetExtension(fullPath);
            for (var i = 1; i < 10000; i++)
            {
                var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
                if (!File.Exists(candidate) && !System.IO.Directory.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
        }
        catch { return fullPath; }
    }

    /// <summary>把相对路径解析为绝对路径，并阻止越出功能数据目录（含 <c>..</c> 或绝对路径时抛异常）。</summary>
    private string ResolvePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("相对路径不能为空", nameof(relativePath));

        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar).Trim();
        if (Path.IsPathRooted(normalized))
            throw new ArgumentException($"请使用相对路径（不要带盘符/根目录）：{relativePath}", nameof(relativePath));

        var full = Path.GetFullPath(Path.Combine(Directory, normalized));
        var prefix = Directory.EndsWith(Path.DirectorySeparatorChar) ? Directory : Directory + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"路径越出数据目录（不允许 .. 跳出）：{relativePath}", nameof(relativePath));
        return full;
    }

    /// <summary>写文件前的准备：建目录 + 处理重名，返回最终要写入的完整路径。</summary>
    private string? PrepareWrite(string relativePath, bool overwrite)
    {
        EnsureCreated();
        var path = ResolvePath(relativePath);
        if (File.Exists(path) && !overwrite) path = MakeUniquePath(path);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        return path;
    }

    private StorageFile ToStorageFile(string fullPath)
    {
        var info = new FileInfo(fullPath);
        return new StorageFile(
            info.FullName,
            Path.GetRelativePath(Directory, info.FullName),
            info.Name,
            info.Length,
            info.LastWriteTime);
    }
}
