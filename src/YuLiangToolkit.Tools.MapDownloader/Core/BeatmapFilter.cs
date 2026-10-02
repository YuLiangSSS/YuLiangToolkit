using System.Globalization;

namespace YuLiangToolkit.Tools.MapDownloader.Core;

/// <summary>
/// 结果列表的**本地筛选与展示文案**（纯函数）。
///
/// <para>原版把"日期"和"Key 数"这两项做成客户端筛选（osu! 的搜索接口不认这两个条件），
/// 命中才往表格里加行 —— 这里保留同样的语义，只是搬成了可断言的静态方法。</para>
/// </summary>
public static class BeatmapFilter
{
    /// <summary>日期筛选（<paramref name="from"/>/<paramref name="to"/> 为 null 表示那一头不限）。</summary>
    public static bool MatchesDate(BeatmapSet set, DateTime? from, DateTime? to)
    {
        if (from is null && to is null) return true;
        if (!TryParseDate(set.RankedDate, out var ranked)) return false;   // 没上架日期的一律筛掉
        if (from is { } f && ranked.Date < f.Date) return false;
        if (to is { } t && ranked.Date > t.Date) return false;
        return true;
    }

    /// <summary>
    /// Key 数筛选：<c>""</c>=不限、<c>"4"~"10"</c>=该键数、<c>"10+"</c>=10 键及以上。
    /// 判定用「任意一个难度命中所选键数」（原版如此）。
    /// </summary>
    public static bool MatchesKeyCount(BeatmapSet set, string? keyCount)
    {
        if (string.IsNullOrWhiteSpace(keyCount)) return true;
        if (set.Beatmaps is not { Count: > 0 } maps) return false;

        if (keyCount.Trim() == "10+")
            return maps.Any(m => (int)Math.Round(m.CS) >= 10);

        if (!int.TryParse(keyCount, out var target)) return true;
        return maps.Any(m => (int)Math.Round(m.CS) == target);
    }

    /// <summary>日期 + Key 数一起判（结果列表就按这一条决定收不收）。</summary>
    public static bool Passes(BeatmapSet set, DateTime? from, DateTime? to, string? keyCount)
        => MatchesDate(set, from, to) && MatchesKeyCount(set, keyCount);

    /// <summary>把配置里存的日期文本解析成日期（空/写错 → null = 不限）。</summary>
    public static DateTime? ParseDate(string? text)
        => DateTime.TryParse((text ?? "").Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var value)
            ? value.Date
            : null;

    /// <summary>上架日期 → <c>yyyy-MM-dd</c>（解析不出来就截前 10 个字符，原版如此）。</summary>
    public static string FormatDate(BeatmapSet set)
    {
        if (string.IsNullOrWhiteSpace(set.RankedDate)) return "";
        if (TryParseDate(set.RankedDate, out var date)) return date.ToString("yyyy-MM-dd");
        var raw = set.RankedDate.Trim();
        return raw.Length >= 10 ? raw[..10] : raw;
    }

    /// <summary>难度里的 CS 范围 → <c>4.0</c> 或 <c>4.0-7.0</c>（全一样就只写一个）。</summary>
    public static string FormatKeyRange(BeatmapSet set)
    {
        if (set.Beatmaps is not { Count: > 0 } maps) return "";

        var values = maps.Select(m => m.CS).Distinct().OrderBy(c => c).ToList();
        return values.Count == 1
            ? values[0].ToString("F1", CultureInfo.InvariantCulture)
            : $"{values[0].ToString("F1", CultureInfo.InvariantCulture)}-{values[^1].ToString("F1", CultureInfo.InvariantCulture)}";
    }

    /// <summary>状态文案（API 给的是小写；空就写 unknown）。</summary>
    public static string StatusText(BeatmapSet set)
    {
        var raw = (set.Status ?? "").Trim();
        if (raw.Length == 0) return "unknown";
        return raw.ToLowerInvariant() switch
        {
            "ranked" => "Ranked",
            "approved" => "Approved",
            "loved" => "Loved",
            "qualified" => "Qualified",
            "pending" => "Pending",
            "wip" => "WIP",
            "graveyard" => "Graveyard",
            _ => raw,
        };
    }

    /// <summary>难度个数（列表里那一列）。</summary>
    public static int DifficultyCount(BeatmapSet set) => set.Beatmaps?.Count ?? 0;

    /// <summary>下载时用的标题：标题为空就退回 <c>unknown</c>（免得文件名是空的）。</summary>
    public static string DisplayTitle(BeatmapSet set)
        => string.IsNullOrWhiteSpace(set.Title) ? "unknown" : set.Title.Trim();

    private static bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
               || DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value);
    }
}
