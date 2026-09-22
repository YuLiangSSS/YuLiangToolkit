using YuLiangToolkit.App.Models;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 主页卡片的过滤 / 排序 / 标签收集逻辑（纯函数，不碰界面）。
///
/// <para>单独抽出来是为了<b>能脱离 UI 测试</b>：<c>tests\YuLiangToolkit.FrameworkSelfCheck</c>
/// 里对它有断言（搜索命中、关键词组合、各种排序），改逻辑跑一下自检就知道有没有坏。</para>
/// </summary>
public static class ToolFilter
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

    /// <summary>过滤 + 排序一步到位（主页每次搜索/改排序都调它）。</summary>
    public static IReadOnlyList<ToolCard> Apply(
        IEnumerable<ToolCard> cards,
        string? searchText,
        ToolSortMode sortMode)
        => Sort(Filter(cards, SplitKeywords(searchText)), sortMode);

    /// <summary>关键词过滤：所有关键词都要在卡片搜索索引里命中（AND 关系，大小写不敏感）。</summary>
    public static IEnumerable<ToolCard> Filter(IEnumerable<ToolCard> cards, IReadOnlyList<string> keywords)
        => keywords.Count == 0 ? cards : cards.Where(c => c.Matches(keywords));

    /// <summary>按排序方式排卡片。</summary>
    public static IReadOnlyList<ToolCard> Sort(IEnumerable<ToolCard> cards, ToolSortMode sortMode)
    {
        // 用当前系统区域比较：中文系统下按拼音排，英文按字母排
        var byName = StringComparer.CurrentCulture;

        return sortMode switch
        {
            // 从未用过的排最后（DateTime.MinValue 降序时自然垫底）
            ToolSortMode.RecentlyUsed => cards
                .OrderByDescending(c => c.LastUsed ?? DateTime.MinValue)
                .ThenBy(c => c.Tool.Order)
                .ThenBy(c => c.Name, byName)
                .ToList(),

            ToolSortMode.NameAsc => cards.OrderBy(c => c.Name, byName).ToList(),

            ToolSortMode.NameDesc => cards.OrderByDescending(c => c.Name, byName).ToList(),

            ToolSortMode.Category => cards
                .OrderBy(c => c.Category, byName)
                .ThenBy(c => c.Tool.Order)
                .ThenBy(c => c.Name, byName)
                .ToList(),

            _ => cards
                .OrderBy(c => c.Tool.Order)
                .ThenBy(c => c.Name, byName)
                .ToList(),
        };
    }

    /// <summary>收集所有卡片用到的标签（去重 + 排序），给主页“快速筛选”标签条用。</summary>
    public static IReadOnlyList<string> CollectTags(IEnumerable<ToolCard> cards)
        => cards
            .SelectMany(c => c.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.CurrentCulture)
            .ToList();
}
