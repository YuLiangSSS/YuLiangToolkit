namespace YuLiangToolkit.Tools.FlashDownloader.Core;

/// <summary>游戏列表的排序方式（枚举按名字进 JSON，加减成员不会错位，别改名）。</summary>
public enum GameSortMode
{
    /// <summary>默认：按列表扫出来的顺序（文件名）。</summary>
    Default = 0,

    /// <summary>按上次游玩时间：刚玩过的排最前，从没玩过的垫底。</summary>
    LastPlayed = 1,

    /// <summary>按名字。</summary>
    Name = 2,

    /// <summary>按大小。</summary>
    Size = 3,

    /// <summary>按站点（4399 / 7k7k / 17yy / 其它）。</summary>
    Site = 4,
}

/// <summary>
/// 游戏列表的搜索 / 排序（纯函数，不碰界面、不碰 WPF）—— 与主页的 <c>ToolFilter</c> 一个套路，
/// 单独抽出来是为了能在自检里逐条断言。
/// </summary>
public static class GameFilter
{
    /// <summary>
    /// 把搜索词切成关键词：空格 / 制表符 / 中英文逗号 / 顿号 / 分号都算分隔符。
    /// 空搜索词返回空数组（调用方据此跳过过滤）。
    /// </summary>
    public static string[] SplitKeywords(string? searchText)
        => string.IsNullOrWhiteSpace(searchText)
            ? Array.Empty<string>()
            : searchText.Split(new[] { ' ', '\t', '，', ',', '、', ';', '；' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>过滤 + 排序一步到位。</summary>
    /// <param name="entries">全部游戏。</param>
    /// <param name="searchText">搜索词（空 = 不过滤）。</param>
    /// <param name="sortMode">排序方式。</param>
    /// <param name="descending">是否反序。</param>
    /// <param name="lastPlayed">上次游玩时间查表（键 = 文件名/文件夹名；没有就当作从没玩过）。</param>
    public static IReadOnlyList<GameEntry> Apply(
        IEnumerable<GameEntry> entries,
        string? searchText,
        GameSortMode sortMode,
        bool descending = false,
        IReadOnlyDictionary<string, DateTime>? lastPlayed = null)
        => Sort(Filter(entries, SplitKeywords(searchText)), sortMode, descending, lastPlayed);

    /// <summary>关键词过滤：所有关键词都要命中（AND 关系，大小写不敏感）。</summary>
    public static IEnumerable<GameEntry> Filter(IEnumerable<GameEntry> entries, IReadOnlyList<string> keywords)
        => keywords.Count == 0 ? entries : entries.Where(e => Matches(e, keywords));

    /// <summary>
    /// 一个游戏是否命中全部关键词。搜索索引 = 文件名 + 游戏名 + 站点后缀 + 站点名 + 类型 + 本地路径，
    /// 所以「4399」「网页」「a90302」「打羽毛球」都能搜到。
    /// </summary>
    public static bool Matches(GameEntry entry, IReadOnlyList<string> keywords)
    {
        var index = string.Join('\n',
            entry.FileOrFolderName,
            entry.GameName,
            entry.Appendix,
            entry.SiteText,
            entry.TypeText,
            entry.OpenModeText,
            entry.FullPath);

        foreach (var keyword in keywords)
        {
            if (index.IndexOf(keyword, StringComparison.CurrentCultureIgnoreCase) < 0) return false;
        }
        return true;
    }

    /// <summary>按指定方式排序；<paramref name="descending"/> 为 true 时整体反序。</summary>
    public static IReadOnlyList<GameEntry> Sort(
        IEnumerable<GameEntry> entries,
        GameSortMode sortMode,
        bool descending = false,
        IReadOnlyDictionary<string, DateTime>? lastPlayed = null)
    {
        // 中文用当前区域比较：中文系统下按拼音排，英文按字母排
        var byName = StringComparer.CurrentCulture;

        IEnumerable<GameEntry> sorted = sortMode switch
        {
            // 从没玩过的垫底：DateTime.MinValue 在这里排最后（下面统一决定方向）
            GameSortMode.LastPlayed => entries
                .OrderByDescending(e => Lookup(lastPlayed, e))
                .ThenBy(e => e.FileOrFolderName, byName),

            GameSortMode.Name => entries.OrderBy(e => e.GameName, byName)
                .ThenBy(e => e.FileOrFolderName, byName),

            GameSortMode.Size => entries.OrderBy(e => e.SizeBytes)
                .ThenBy(e => e.FileOrFolderName, byName),

            GameSortMode.Site => entries.OrderBy(e => e.SiteSortKey)
                .ThenBy(e => e.FileOrFolderName, byName),

            _ => entries.OrderBy(e => e.FileOrFolderName, byName),
        };

        var list = sorted.ToList();
        // LastPlayed 的语义是「刚玩过的在最上面」，所以它自己的默认方向已经是“倒序”，
        // 反序开关对它就是把顺序翻过来（从没玩过的反而在最上面）——这里如实照做，不搞特例。
        if (descending) list.Reverse();
        return list;
    }

    private static DateTime Lookup(IReadOnlyDictionary<string, DateTime>? table, GameEntry entry)
    {
        if (table is null) return DateTime.MinValue;
        return table.TryGetValue(entry.FileOrFolderName, out var at) ? at : DateTime.MinValue;
    }

    /// <summary>提醒文案：搜索没结果时说清楚“是筛掉了”还是“目录里真没有”。</summary>
    public static string EmptyHint(int total, int shown, string? searchText)
        => total == 0
            ? "游戏目录里还没有游戏：把游戏页网址粘到上面点「下载」即可。"
            : shown == 0
                ? $"没有匹配「{searchText}」的游戏（共 {total} 个）。"
                : "";
}
