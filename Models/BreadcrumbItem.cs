using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 顶部工具条上的一枚面包屑（可点击分层导航）。
///
/// <para>规则：主页永远是最左边第一枚；类别页 = 主页 + 各级祖先 + 当前类别（当前项高亮、不可点）；
/// 工具页 = 主页 + 父类别路径 + 工具名（工具名当前项）。点祖先 → 回到对应类别 / 主页。</para>
/// </summary>
public sealed class BreadcrumbItem
{
    private BreadcrumbItem(string title, string emoji, bool isCurrent, bool isFirst,
        ToolCatalogNode? node, ITool? tool)
    {
        Title = title;
        Emoji = emoji;
        IsCurrent = isCurrent;
        IsFirst = isFirst;
        Node = node;
        Tool = tool;
    }

    /// <summary>显示名（主页 / 类别名 / 工具名）。</summary>
    public string Title { get; }

    /// <summary>显示图标（🏠 / 类别 emoji / 工具 emoji）。</summary>
    public string Emoji { get; }

    /// <summary>按钮上的文字："🏠 主页" / "🎵 音游工具"。</summary>
    public string DisplayText => string.IsNullOrWhiteSpace(Emoji) ? Title : $"{Emoji} {Title}";

    /// <summary>是不是当前所在层级（当前项高亮、不可点）。</summary>
    public bool IsCurrent { get; }

    /// <summary>是不是第一枚（第一枚前面不画分隔符）。</summary>
    public bool IsFirst { get; }

    /// <summary>可点击 = 不是当前项。</summary>
    public bool IsClickable => !IsCurrent;

    /// <summary>非第一枚时显示 "›" 分隔符。</summary>
    public bool ShowSeparator => !IsFirst;

    /// <summary>点击后要导航到的类别节点（工具项为 null）。</summary>
    public ToolCatalogNode? Node { get; }

    /// <summary>点击后要打开的工具（类别项为 null）。</summary>
    public ITool? Tool { get; }

    /// <summary>是不是「主页」那一枚。</summary>
    public bool IsHome => Tool is null && Node is { IsRoot: true };

    /// <summary>生成「主页」面包屑（<paramref name="root"/> 是目录树根节点）。</summary>
    public static BreadcrumbItem ForHome(ToolCatalogNode root, bool isCurrent, bool isFirst = true)
        => new("主页", "🏠", isCurrent, isFirst, root, null);

    /// <summary>生成「类别」面包屑。</summary>
    public static BreadcrumbItem ForCategory(ToolCatalogNode node, bool isCurrent, bool isFirst)
        => new(node.Name, node.EmojiIcon, isCurrent, isFirst, node, null);

    /// <summary>生成「工具」面包屑（当前项）。</summary>
    public static BreadcrumbItem ForTool(ITool tool, bool isFirst)
        => new(tool.Name, tool.EmojiIcon, true, isFirst, null, tool);
}
