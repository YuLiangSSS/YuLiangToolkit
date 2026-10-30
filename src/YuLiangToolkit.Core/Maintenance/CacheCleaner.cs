using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Core.Maintenance;

/// <summary>清理列表里的一条缓存（由 <see cref="CacheCleaner.List"/> 汇总出来，界面直接渲染）。</summary>
public sealed class CacheEntry
{
    /// <summary>唯一键（<c>folder:工具Id</c> / <c>工具Id:项Id</c> / <c>unknown:目录名</c>…）。</summary>
    public required string Key { get; init; }

    /// <summary>所属功能 Id（Cache 根目录残留项为空串）。</summary>
    public required string ToolId { get; init; }

    /// <summary>所属功能展示名。</summary>
    public required string ToolName { get; init; }

    /// <summary>缓存项显示名（「缓存文件夹」「最近浏览记录」…）。</summary>
    public required string Name { get; init; }

    /// <summary>一句话说明。</summary>
    public string Description { get; init; } = "";

    /// <summary>已知的磁盘占用；0 = 不统计（例如 Config 里的「N 条记录」只给 <see cref="Detail"/>）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>数量类提示（「3 个文件」「12 条」）；没有占用信息时显示在大小那一列。</summary>
    public string Detail { get; init; } = "";

    /// <summary>真正执行清理的动作。</summary>
    public required Action Clear { get; init; }

    /// <summary>右侧大小 / 数量文案。</summary>
    public string SizeText => SizeBytes > 0 ? FileSize.Format(SizeBytes) : Detail;
}

/// <summary>
/// 缓存汇总 / 清理（「主页设置 → 清理缓存」的引擎，纯逻辑、不碰界面，自检可直接调用）。
///
/// <para><b>汇总来源</b>（自动覆盖以后新加的工具）：</para>
/// <list type="number">
///   <item>每个已发现工具的 <c>Cache\&lt;工具Id&gt;\</c> 文件夹（有文件才列出）；</item>
///   <item>工具实现了 <see cref="IToolCacheSource"/> 时，它报告的额外缓存项（存在 Config 里的「最近浏览」等）；</item>
///   <item>Cache 根目录下没有对应工具的残留目录（插件删了 / 工具改名留下的）；</item>
///   <item>Cache 根目录下散落的零散文件（工具没按约定建子文件夹时留下的）。</item>
/// </list>
///
/// <para>清理 = 删除内容、保留文件夹本身（下次写入照常）；<b>不会动 Data 里的用户数据</b>，
/// 也不会删除配置 —— 那是「重建配置」的事。</para>
/// </summary>
public static class CacheCleaner
{
    /// <summary>列出当前所有缓存项（没内容的不会出现；全部为空时返回空列表）。顺序：按传入的工具顺序。</summary>
    public static IReadOnlyList<CacheEntry> List(IEnumerable<ITool>? tools)
    {
        var result = new List<CacheEntry>();
        var knownFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tool in tools ?? Enumerable.Empty<ITool>())
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.Id)) continue;
            var folderName = AppPaths.SanitizeFolderName(tool.Id);
            knownFolders.Add(folderName);

            // ① Cache\<工具Id>\ —— 约定内的缓存，自动认出，新工具不用登记
            var dir = AppPaths.ToolCacheDir(tool.Id);
            var fileCount = CountFiles(dir);
            if (fileCount > 0)
            {
                var targetDir = dir;
                result.Add(new CacheEntry
                {
                    Key = "folder:" + folderName,
                    ToolId = tool.Id,
                    ToolName = tool.Name,
                    Name = "缓存文件夹",
                    Description = $"Cache\\{folderName}\\ 里的临时文件（缩略图 / 解压产物等），删掉最多下次重新生成。",
                    SizeBytes = FolderSize(targetDir),
                    Detail = $"{fileCount} 个文件",
                    Clear = () => DeleteContents(targetDir),
                });
            }

            // ② 工具自己报告的额外缓存（存在 Config 里的最近浏览 / 最近会话…）
            if (tool is IToolCacheSource source)
            {
                IReadOnlyList<ToolCacheItem>? items = null;
                try { items = source.DescribeCacheItems(); }
                catch { /* 单个工具报告失败不影响别的功能 */ }

                foreach (var item in items ?? Array.Empty<ToolCacheItem>())
                {
                    if (item is null || item.Clear is null || string.IsNullOrWhiteSpace(item.Id)) continue;
                    if (string.IsNullOrWhiteSpace(item.Detail)) continue;   // 没有可清内容 → 不显示
                    var captured = item;
                    result.Add(new CacheEntry
                    {
                        Key = folderName + ":" + item.Id,
                        ToolId = tool.Id,
                        ToolName = tool.Name,
                        Name = item.Name,
                        Description = item.Description,
                        Detail = item.Detail,
                        Clear = () => captured.Clear(),
                    });
                }
            }
        }

        // ③ Cache 根目录下没有对应工具的残留目录
        try
        {
            if (Directory.Exists(AppPaths.CacheRoot))
            {
                foreach (var leftover in Directory.GetDirectories(AppPaths.CacheRoot)
                             .OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
                {
                    var folder = Path.GetFileName(leftover);
                    if (knownFolders.Contains(folder)) continue;
                    var fileCount = CountFiles(leftover);
                    if (fileCount == 0) continue;
                    var targetDir = leftover;
                    result.Add(new CacheEntry
                    {
                        Key = "unknown:" + folder,
                        ToolId = "",
                        ToolName = "未知功能（" + folder + "）",
                        Name = "残留缓存文件夹",
                        Description = "Cache 里这个文件夹已经没有对应的功能了（插件被删 / 工具改名），可以直接清掉。",
                        SizeBytes = FolderSize(targetDir),
                        Detail = $"{fileCount} 个文件",
                        Clear = () => DeleteContents(targetDir),
                    });
                }
            }
        }
        catch { /* 根目录不可读忽略 */ }

        // ④ Cache 根目录散落的零散文件（跳过程序自己写的说明文件：那不是缓存）
        try
        {
            if (Directory.Exists(AppPaths.CacheRoot))
            {
                var looseFiles = Directory.GetFiles(AppPaths.CacheRoot, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => !IsRuntimeReadme(file))
                    .ToArray();
                if (looseFiles.Length > 0)
                {
                    long size = 0;
                    foreach (var file in looseFiles)
                    {
                        try { size += new FileInfo(file).Length; } catch { /* 忽略 */ }
                    }
                    var targets = looseFiles;
                    result.Add(new CacheEntry
                    {
                        Key = "cache-root-files",
                        ToolId = "",
                        ToolName = "（Cache 根目录）",
                        Name = "散落的缓存文件",
                        Description = "直接放在 Cache\\ 根目录下的文件（按约定应放进各自功能的子文件夹），可以清掉。",
                        SizeBytes = size,
                        Detail = $"{looseFiles.Length} 个文件",
                        Clear = () =>
                        {
                            foreach (var file in targets)
                            {
                                try { File.Delete(file); } catch { /* 忽略 */ }
                            }
                        },
                    });
                }
            }
        }
        catch { /* 忽略 */ }

        return result;
    }

    /// <summary>清理选中的缓存项。返回成功清理的条数与已知占用合计。</summary>
    /// <param name="entries">要清理的缓存项（界面勾选的那些）。</param>
    /// <param name="onFailed">某一条清理失败时回调（可选；参数是那条缓存项）。</param>
    public static (int Cleared, long Bytes) Clear(IEnumerable<CacheEntry>? entries, Action<CacheEntry>? onFailed = null)
    {
        var cleared = 0;
        long bytes = 0;
        foreach (var entry in entries ?? Enumerable.Empty<CacheEntry>())
        {
            if (entry?.Clear is null) continue;
            try
            {
                entry.Clear();
                cleared++;
                bytes += entry.SizeBytes;
            }
            catch
            {
                onFailed?.Invoke(entry);
            }
        }
        return (cleared, bytes);
    }

    // ---------- 内部工具 ----------

    /// <summary>
    /// 是不是程序自己写在缓存根目录里的说明文件（<c>Cache\怎么用这个文件夹.md</c>）。
    /// 它不是缓存，不能列进「散落的缓存文件」、更不能被清掉。
    /// </summary>
    private static bool IsRuntimeReadme(string path)
        => string.Equals(Path.GetFileName(path), RuntimeReadme.CacheReadmeFileName,
            StringComparison.OrdinalIgnoreCase);

    private static int CountFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Take(int.MaxValue).Count()
                : 0;
        }
        catch { return 0; }
    }

    private static long FolderSize(string directory)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* 单个文件失败忽略 */ }
            }
        }
        catch { /* 目录不可读忽略 */ }
        return total;
    }

    private static bool DeleteContents(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* 单个文件失败继续 */ }
            }
            foreach (var child in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(p => p.Length))   // 先删深的
            {
                try { Directory.Delete(child, recursive: true); } catch { /* 忽略 */ }
            }
            return true;
        }
        catch { return false; }
    }
}
