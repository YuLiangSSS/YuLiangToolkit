using System.IO;

namespace YuLiangToolkit.Core.Storage;

/// <summary>
/// 「最近打开的文件」里的一条记录（直接存进功能的 <c>config.json</c>）。
/// <para>Word / Excel 两个阅读器共用；PDF 阅读器早先自己写过一份，语义一致，别改它的类型。</para>
/// </summary>
public sealed class RecentFileEntry
{
    /// <summary>文件完整路径（写入前会经过 <see cref="RecentFiles.NormalizePath"/>）。</summary>
    public string Path { get; set; } = "";

    /// <summary>最后一次打开的时间（UTC）；列表按它从新到旧排。</summary>
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 「最近打开」列表的通用逻辑：去重、置顶、按上限裁剪、清理。
/// <para>两个文档阅读器（Word / Excel）的最近列表规则完全一样，所以抽到这里；
/// 配置类只负责把列表存进 JSON，具体怎么增删都调这里（写单元自检也方便）。</para>
/// </summary>
public static class RecentFiles
{
    /// <summary>「最近打开」默认记多少条。</summary>
    public const int DefaultLimit = 50;

    /// <summary>「最近打开」上限的最大值（范围 0 ~ 100；0 = 不记录）。</summary>
    public const int MaxLimit = 100;

    /// <summary>把上限夹到 0 ~ <see cref="MaxLimit"/>（手改坏配置也不会存出离谱的条数）。</summary>
    public static int ClampLimit(int limit) => Math.Clamp(limit, 0, MaxLimit);

    /// <summary>把路径规范成完整路径（去重 / 落盘都用它）；非法路径返回空串。</summary>
    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFullPath(path.Trim().Trim('"')); }
        catch { return ""; }
    }

    /// <summary>
    /// 记一次「打开过的文件」：放到最前面、同一路径去重（Windows 不区分大小写）、超过上限砍掉最旧的。
    /// 上限是 0 时按「不记录」处理，并顺手清空已有列表。
    /// </summary>
    /// <param name="entries">要修改的列表（一般是配置里的 <c>RecentEntries</c>）。</param>
    /// <param name="path">刚打开的文件路径。</param>
    /// <param name="limit">上限（会先夹到 0 ~ <see cref="MaxLimit"/>）。</param>
    /// <param name="nowUtc">记录时间，默认当前 UTC（自检可以传固定值）。</param>
    public static void Remember(List<RecentFileEntry> entries, string? path, int limit, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var full = NormalizePath(path);
        if (full.Length == 0) return;

        var effectiveLimit = ClampLimit(limit);
        if (effectiveLimit == 0)
        {
            entries.Clear();
            return;
        }

        entries.RemoveAll(entry => string.Equals(
            NormalizePath(entry.Path), full, StringComparison.OrdinalIgnoreCase));

        entries.Insert(0, new RecentFileEntry { Path = full, LastOpenedUtc = nowUtc ?? DateTime.UtcNow });
        Trim(entries, effectiveLimit);
    }

    /// <summary>按当前上限裁剪列表（上限 0 = 清空）。</summary>
    public static void Trim(List<RecentFileEntry> entries, int limit)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var effectiveLimit = ClampLimit(limit);
        if (effectiveLimit == 0)
        {
            entries.Clear();
            return;
        }

        if (entries.Count > effectiveLimit)
            entries.RemoveRange(effectiveLimit, entries.Count - effectiveLimit);
    }

    /// <summary>从最近列表里去掉某个文件（文件已不存在时用）。</summary>
    public static void Remove(List<RecentFileEntry> entries, string? path)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var full = NormalizePath(path);
        if (full.Length == 0) return;

        entries.RemoveAll(entry => string.Equals(
            NormalizePath(entry.Path), full, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>这条记录对应的文件现在还在不在（界面据此显示「文件已不存在」并禁用点击）。</summary>
    public static bool IsAvailable(RecentFileEntry? entry)
        => entry is not null && entry.Path.Length > 0 && File.Exists(entry.Path);
}
