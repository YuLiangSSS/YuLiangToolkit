using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 主页 / 类别页上的「类别卡片」展示模型（一个分类节点一张）。
/// 界面只绑它，点击时把整个对象交给 <c>NavigateCategoryCommand</c>。
/// </summary>
public sealed class CategoryCard
{
    public CategoryCard(ToolCatalogNode node) => Node = node;

    /// <summary>对应的目录树节点（导航用）。</summary>
    public ToolCatalogNode Node { get; }

    public string Name => Node.Name;
    public string Emoji => Node.EmojiIcon;
    public string Description => Node.Description;

    /// <summary>描述是不是空的（空的就不显示那行，别留一条空白）。</summary>
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    /// <summary>这个类别下的工具总数（含子类别，递归）。</summary>
    public int ToolCount => Node.ToolCount;

    /// <summary>直接子类别数量。</summary>
    public int CategoryCount => Node.Children.Count;

    /// <summary>「4 个工具」/「4 个工具 · 2 个子分类」。</summary>
    public string CountText => CategoryCount > 0
        ? $"{ToolCount} 个工具 · {CategoryCount} 个子分类"
        : $"{ToolCount} 个工具";

    /// <summary>完整路径文案（鼠标悬停提示用）。</summary>
    public string PathText => Node.PathText;

    /// <summary>鼠标悬停提示：路径 + 描述 + 计数。</summary>
    public string ToolTipText
        => $"类别：{PathText}\n{(HasDescription ? Description + "\n\n" : "")}{CountText}";
}
