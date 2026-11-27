using System.IO;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Screenshot;

/// <summary>一张历史截图（文件路径 + 轻量显示信息）。</summary>
public sealed class ScreenshotHistoryEntry
{
    /// <summary>完整路径。</summary>
    public required string FullPath { get; init; }

    /// <summary>文件名。</summary>
    public string FileName => Path.GetFileName(FullPath);

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>人类可读大小。</summary>
    public string SizeText => FileSize.Format(SizeBytes);

    /// <summary>显示时间。</summary>
    public string TimeText => CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// 截屏历史：统一放在 <c>Cache\screenshot\history\</c>（缓存性质，随时可清），
/// 文件按时间命名，页面打开时扫描目录。不把历史记录写进配置，避免配置被大量路径撑大。
/// </summary>
public static class ScreenshotHistoryService
{
    /// <summary>历史子目录名。</summary>
    public const string HistoryFolder = "history";

    /// <summary>历史目录完整路径。</summary>
    public static string DirectoryPath => ToolCache.For(ScreenshotConfig.ToolId).PathOf(HistoryFolder);

    /// <summary>列出历史（新 → 旧）。</summary>
    public static IReadOnlyList<ScreenshotHistoryEntry> List()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return Array.Empty<ScreenshotHistoryEntry>();
            return Directory.EnumerateFiles(DirectoryPath, "*.png", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    try
                    {
                        var info = new FileInfo(path);
                        return new ScreenshotHistoryEntry
                        {
                            FullPath = info.FullName,
                            CreatedAt = info.LastWriteTime,
                            SizeBytes = info.Length,
                        };
                    }
                    catch { return null; }
                })
                .Where(x => x is not null)
                .Cast<ScreenshotHistoryEntry>()
                .OrderByDescending(x => x.CreatedAt)
                .ThenByDescending(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<ScreenshotHistoryEntry>();
        }
    }

    /// <summary>写入一张历史图，返回路径；失败返回 null。</summary>
    public static string? Save(BitmapSource image)
    {
        try
        {
            var cache = ToolCache.For(ScreenshotConfig.ToolId);
            cache.EnsureCreated();
            Directory.CreateDirectory(DirectoryPath);

            var path = ScreenshotGeometry.BuildOutputPath(DirectoryPath, "shot", ".png", DateTime.Now);
            ScreenCaptureService.Save(image, path, "png");
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>裁掉超过上限的旧历史，返回删除数量；上限 ≤ 0 = 清空。</summary>
    public static int Prune(int limit)
    {
        var entries = List();
        if (limit <= 0)
        {
            Clear();
            return entries.Count;
        }

        var removed = 0;
        foreach (var entry in entries.Skip(Math.Max(0, limit)))
        {
            try
            {
                File.Delete(entry.FullPath);
                removed++;
            }
            catch { /* 单个失败不影响其它 */ }
        }
        return removed;
    }

    /// <summary>删除一张历史。</summary>
    public static bool Delete(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (!File.Exists(full)) return true;
            File.Delete(full);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清空历史（保留目录；只删 history 子目录，不碰贴图）。</summary>
    public static bool Clear()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath)) return true;
            foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); } catch { /* 继续 */ }
            }
            foreach (var dir in Directory.EnumerateDirectories(DirectoryPath, "*", SearchOption.AllDirectories)
                         .OrderByDescending(x => x.Length))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* 继续 */ }
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
