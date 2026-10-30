using System.Diagnostics;

namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// 功能缓存文件夹门面：每个功能独占 <c>Cache\&lt;功能名&gt;\</c>，放<b>随时可以删、丢了不心疼</b>的东西
/// （缩略图、解压出来的临时文件、网络请求缓存……）。
///
/// <para><b>和 Data 的区别</b>：<c>Data</c> 是用户数据（下载的游戏、聊天记录、模板），删了会丢东西；
/// <c>Cache</c> 只是加速用的中间产物，删了最多重新生成。主页设置里的「清理缓存」清的正是这里，
/// 所以<b>新功能写缓存请务必走本类</b>（或至少用 <see cref="AppPaths.ToolCacheDir"/> 拼路径），
/// 这样以后加的工具会自动出现在清理列表里，不用改设置页。</para>
///
/// <para><b>用法</b>：</para>
/// <code>
/// var cache = ToolCache.For("picture-viewer");      // Cache\picture-viewer\
/// var thumb = cache.PathOf("thumbs", "001.png");    // 只拼路径，不创建
/// cache.EnsureCreated();                            // 真要写了再建目录
/// File.WriteAllBytes(thumb, bytes);
/// var bytes = cache.TotalSize();                    // 当前占用
/// cache.Clear();                                    // 清空（保留文件夹本身）
/// </code>
///
/// <para>与 <see cref="ToolStorage"/> 一样：相对路径禁止 <c>..</c> / 绝对路径（防越界），
/// 所有 IO 失败都静默返回 false / 0，不抛异常。</para>
/// </summary>
public sealed class ToolCache
{
    private ToolCache(string toolId, string folderName)
    {
        ToolId = toolId;
        Directory = AppPaths.ToolCacheDir(folderName);
    }

    /// <summary>取某功能的缓存文件夹门面（只拼路径，不创建目录）。</summary>
    /// <param name="toolId">功能 Id，例如 "picture-viewer"。</param>
    /// <param name="folderName">自定义小文件夹名（默认与 toolId 相同）。</param>
    public static ToolCache For(string toolId, string? folderName = null)
    {
        var finalName = string.IsNullOrWhiteSpace(folderName) ? toolId : folderName!;
        return new ToolCache(toolId, finalName);
    }

    /// <summary>功能 Id。</summary>
    public string ToolId { get; }

    /// <summary>缓存小文件夹完整路径：<c>Cache\&lt;功能名&gt;</c>（不一定存在）。</summary>
    public string Directory { get; }

    /// <summary>缓存文件夹当前是否存在。</summary>
    public bool Exists => System.IO.Directory.Exists(Directory);

    /// <summary>确保文件夹存在，返回其路径（失败也返回路径，调用方可用 <see cref="Exists"/> 复核）。</summary>
    public string EnsureCreated()
    {
        try { System.IO.Directory.CreateDirectory(Directory); }
        catch { /* 只读环境忽略 */ }
        return Directory;
    }

    /// <summary>组装缓存目录下的路径（只拼路径，不创建、不检查存在）。</summary>
    public string PathOf(string relativePath) => ResolvePath(relativePath);

    /// <summary>组装缓存目录下的路径，例如 <c>PathOf("thumbs", "001.png")</c>。</summary>
    public string PathOf(params string[] parts) => ResolvePath(Path.Combine(parts));

    /// <summary>缓存文件夹当前占用（字节）；不存在或读不了返回 0。</summary>
    public long TotalSize()
    {
        if (!Exists) return 0;
        long total = 0;
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* 单个文件失败忽略 */ }
            }
        }
        catch { /* 目录不可读忽略 */ }
        return total;
    }

    /// <summary>缓存文件夹里的文件数（递归）；不存在返回 0。</summary>
    public int FileCount()
    {
        if (!Exists) return 0;
        try { return System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories).Count(); }
        catch { return 0; }
    }

    /// <summary>清空缓存文件夹里的全部内容（保留文件夹本身，下次写入照常）。</summary>
    public bool Clear()
    {
        try
        {
            if (!Exists) return true;
            foreach (var file in System.IO.Directory.GetFiles(Directory, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* 单个文件失败继续 */ }
            }
            foreach (var dir in System.IO.Directory.GetDirectories(Directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))   // 先删深的，再删浅的
            {
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* 忽略 */ }
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>在资源管理器里打开某个功能的缓存文件夹（不存在则先建）。</summary>
    public bool OpenInExplorer()
    {
        EnsureCreated();
        return OpenDirectory(Directory);
    }

    /// <summary>在资源管理器里打开缓存根目录 <c>Cache\</c>（主页设置的「打开缓存文件夹」用它）。</summary>
    public static bool OpenRootInExplorer()
    {
        try { System.IO.Directory.CreateDirectory(AppPaths.CacheRoot); }
        catch { /* 只读环境忽略 */ }
        return OpenDirectory(AppPaths.CacheRoot);
    }

    private static bool OpenDirectory(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// <summary>把相对路径解析为绝对路径，并阻止越出缓存目录（含 <c>..</c> 或绝对路径时抛异常）。</summary>
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
            throw new ArgumentException($"路径越出缓存目录（不允许 .. 跳出）：{relativePath}", nameof(relativePath));
        return full;
    }
}
