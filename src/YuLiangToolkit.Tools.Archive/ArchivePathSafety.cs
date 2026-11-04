namespace YuLiangToolkit.Tools.Archive;

// =====================================================================================
// 压缩包路径安全工具（纯逻辑，不引用 WPF）：
//   · 把压缩包里的条目 key 规范成「用 / 分隔」的安全相对路径；
//   · 拒绝绝对路径 / 盘符 / 任何 .. 段 / 空 key（防 zip-slip 路径穿越）；
//   · 把条目落在目标目录内再用 Path.GetFullPath 复核一遍；
//   · 覆盖策略「自动改名」时找空位（a.txt → a (1).txt）。
// =====================================================================================

internal static class ArchivePathSafety
{
    /// <summary>
    /// 把压缩包条目 key 规范化成安全相对路径；不安全（越界 / 盘符 / .. / 空）时返回 false。
    /// </summary>
    public static bool TryNormalizeEntryKey(string? key, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(key)) return false;

        // 统一分隔符：ZIP / 7Z / TAR 用 /，RAR 常用 \。
        var replaced = key.Replace('\\', '/');
        if (replaced.IndexOf('\0') >= 0) return false;

        // 绝对路径 / 盘符 / 备用数据流（:）一律拒绝。
        if (replaced.StartsWith('/')) return false;
        if (replaced.Length >= 2 && char.IsLetter(replaced[0]) && replaced[1] == ':') return false;
        if (replaced.IndexOf(':') >= 0) return false;

        var segments = replaced.Split('/');
        var kept = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == ".") continue;
            // 任何一段是 .. 都拒绝（不允许通过 .. 逃出目标目录）。
            if (segment == "..") return false;
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            kept.Add(segment);
        }

        if (kept.Count == 0) return false;
        normalized = string.Join('/', kept);
        return true;
    }

    /// <summary>
    /// 把安全相对 key 拼到目标目录下，并复核最终路径确实落在目标目录内。
    /// </summary>
    public static bool TryResolveWithin(string destinationRoot, string normalizedKey, out string fullPath)
    {
        fullPath = "";
        try
        {
            var root = Path.GetFullPath(destinationRoot);
            var candidate = Path.GetFullPath(Path.Combine(root, normalizedKey.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            if (candidate.Equals(root, StringComparison.OrdinalIgnoreCase)) return false;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            fullPath = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>目录条目判断（key 以 / 或 \ 结尾，或调用方已经知道是目录）。</summary>
    public static bool IsDirectoryKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        return key.EndsWith('/') || key.EndsWith('\\');
    }

    /// <summary>是否「安全简单文件名」（单文件压缩解压时优先用条目名，要求没有目录分隔符）。</summary>
    public static bool IsSafeSimpleFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name is "." or "..") return false;
        if (name.IndexOfAny(['/', '\\', ':', '\0']) >= 0) return false;
        if (Path.IsPathRooted(name)) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    /// <summary>覆盖策略为「自动改名」时找一个不冲突的路径（<c>a.txt</c> → <c>a (1).txt</c>）。</summary>
    public static string MakeUniqueFilePath(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path) ?? "";
            var stem = Path.GetFileNameWithoutExtension(path);
            var extension = Path.GetExtension(path);
            for (var i = 1; i < 10_000; i++)
            {
                var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }

            return Path.Combine(directory, $"{stem} ({Guid.NewGuid():N}){extension}");
        }
        catch
        {
            return path;
        }
    }
}
