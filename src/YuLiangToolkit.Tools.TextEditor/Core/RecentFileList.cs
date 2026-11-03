namespace YuLiangToolkit.Tools.TextEditor.Core;

/// <summary>
/// 最近打开的文件列表（纯逻辑，自检直接断言）：新打开的排最前、去重、封顶。
/// 路径比较用 OrdinalIgnoreCase（Windows 文件路径不区分大小写）。
/// </summary>
public static class RecentFileList
{
    /// <summary>把一个文件顶到最近列表最前（原来就在就先摘掉），封顶丢弃最旧的。返回新列表（不改传入）。</summary>
    public static List<string> Push(IEnumerable<string>? current, string? path)
    {
        var cleaned = Sanitize(current);
        if (string.IsNullOrWhiteSpace(path)) return cleaned;

        var trimmed = path.Trim();
        cleaned.RemoveAll(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase));
        cleaned.Insert(0, trimmed);
        if (cleaned.Count > TextEditorConfig.MaxRecentFiles)
            cleaned.RemoveRange(TextEditorConfig.MaxRecentFiles, cleaned.Count - TextEditorConfig.MaxRecentFiles);
        return cleaned;
    }

    /// <summary>从最近列表里摘掉一个文件（文件被删掉 / 打开失败时用）。返回新列表（不改传入）。</summary>
    public static List<string> Remove(IEnumerable<string>? current, string? path)
    {
        var cleaned = Sanitize(current);
        if (string.IsNullOrWhiteSpace(path)) return cleaned;

        var trimmed = path.Trim();
        cleaned.RemoveAll(existing => string.Equals(existing, trimmed, StringComparison.OrdinalIgnoreCase));
        return cleaned;
    }

    /// <summary>清洗：去空白、去重（保留最先出现的）、封顶；null 安全。</summary>
    public static List<string> Sanitize(IEnumerable<string>? current)
    {
        var result = new List<string>();
        if (current is null) return result;

        foreach (var raw in current)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (result.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(trimmed);
            if (result.Count >= TextEditorConfig.MaxRecentFiles) break;
        }
        return result;
    }

    /// <summary>会话恢复用的标签列表清洗：同上，但上限是会话上限。</summary>
    public static List<string> SanitizeSession(IEnumerable<string>? current)
    {
        var result = new List<string>();
        if (current is null) return result;

        foreach (var raw in current)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (result.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(trimmed);
            if (result.Count >= TextEditorConfig.MaxSessionFiles) break;
        }
        return result;
    }
}
