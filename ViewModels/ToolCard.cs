using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.ViewModels;

/// <summary>
/// 主页上的一张功能卡片（搜索 + 排序的展示单位）。
/// 把 <see cref="ITool"/> 的展示字段、标签、上次使用时间和**预拼好的搜索索引**揉在一起，
/// 界面只绑这个对象；搜索时逐个关键词在索引里找子串即可。
/// </summary>
public sealed class ToolCard : ObservableObject
{
    public ToolCard(ITool tool, DateTime? lastUsed)
    {
        Tool = tool;
        Tags = BuildTags(tool);
        TagsText = string.Join(" · ", Tags);
        SearchIndex = BuildSearchIndex(tool, TagsText);
        _lastUsed = lastUsed;
    }

    /// <summary>原始工具实例（打开功能时把它传给 <c>OpenToolCommand</c>）。</summary>
    public ITool Tool { get; }

    public string Id => Tool.Id;
    public string Name => Tool.Name;
    public string Emoji => Tool.EmojiIcon;
    public string Description => Tool.Description;
    public string Category => Tool.Category;

    /// <summary>类别路径（多层用 " / " 连接；根层工具为空串）。</summary>
    public string CategoryPathText => Tool.CategoryPath.Count == 0
        ? ""
        : string.Join(" / ", Tool.CategoryPath);

    /// <summary>这个工具是不是被收进了某个类别（根层工具为 false）。</summary>
    public bool HasCategoryPath => Tool.CategoryPath.Count > 0;

    /// <summary>分类 + 标签（去重后的顺序列表）。</summary>
    public IReadOnlyList<string> Tags { get; }

    /// <summary>卡片上那行蓝色小字（用 " · " 连接所有标签）。</summary>
    public string TagsText { get; }

    /// <summary>鼠标悬停提示：描述 + 标签 + 类别路径 + 上次使用 + Id。</summary>
    public string ToolTipText
        => $"{Name}\n{Description}\n\n标签：{TagsText}\n"
           + (HasCategoryPath ? $"分类：{CategoryPathText}\n" : "")
           + $"上次使用：{LastUsedText}\nId：{Id}";

    /// <summary>
    /// 搜索索引：名称 + 分类 + 标签 + 描述 + Id + 类别路径（一次性拼好，小写不敏感比较在 Matches 里做）。
    /// 带上类别路径是为了「搜子分类名也能找到里面的工具」（例如搜「谱面处理」）。
    /// </summary>
    private string SearchIndex { get; }

    /// <summary>
    /// 是否命中所有关键词（关键词已按空格切分好；空数组表示不过滤）。
    /// 英文大小写不敏感，所以 "MP3" 能搜到 "mp3"、"ffmpeg" 能搜到 "FFmpeg"。
    /// </summary>
    public bool Matches(IReadOnlyList<string> keywords)
    {
        for (var i = 0; i < keywords.Count; i++)
        {
            if (!SearchIndex.Contains(keywords[i], StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private DateTime? _lastUsed;
    /// <summary>上次使用时间（null = 从未用过）。</summary>
    public DateTime? LastUsed
    {
        get => _lastUsed;
        set
        {
            if (Set(ref _lastUsed, value)) OnPropertyChanged(nameof(LastUsedText));
        }
    }

    /// <summary>"刚刚用过" / "12 分钟前用过" / "从未使用"。</summary>
    public string LastUsedText => FormatLastUsed(_lastUsed);

    private bool _showLastUsed;
    /// <summary>是否显示“上次使用”那行（只在按“最近使用”排序时显示，避免平时干扰）。</summary>
    public bool ShowLastUsed
    {
        get => _showLastUsed;
        set => Set(ref _showLastUsed, value);
    }

    /// <summary>把时间差说成人话。</summary>
    public static string FormatLastUsed(DateTime? time)
    {
        if (time is null) return "从未使用";
        var span = DateTime.Now - time.Value;
        if (span < TimeSpan.Zero) return "刚刚用过";
        if (span.TotalMinutes < 1) return "刚刚用过";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前用过";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前用过";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前用过";
        return $"{time.Value:yyyy-MM-dd} 用过";
    }

    /// <summary>分类在前，工具自带的标签在后，去重（忽略大小写）并丢掉空项。</summary>
    private static IReadOnlyList<string> BuildTags(ITool tool)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? tag)
        {
            var value = tag?.Trim();
            if (string.IsNullOrEmpty(value)) return;
            if (seen.Add(value)) list.Add(value);
        }

        Add(tool.Category);
        foreach (var tag in tool.Tags) Add(tag);
        return list;
    }

    private static string BuildSearchIndex(ITool tool, string tagsText)
        => $"{tool.Name} {tool.Category} {tagsText} {tool.Description} {tool.Id} "
           + string.Join(" ", tool.CategoryPath);
}
