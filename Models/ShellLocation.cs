using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.App.Models;

/// <summary>导航历史里的页面类型（和 <see cref="ViewModels.ShellPageKind"/> 一一对应，但只有这三种）。</summary>
public enum ShellLocationKind
{
    /// <summary>主页。</summary>
    Home,

    /// <summary>类别页（任意层级）。</summary>
    Category,

    /// <summary>工具页。</summary>
    Tool,
}

/// <summary>
/// 外壳导航历史里的「一个页面位置」：主页 / 某个类别页 / 某个工具页。
///
/// <para>给 <see cref="YuLiangToolkit.Core.Mvvm.NavigationHistory{T}"/> 当记录用：
/// **相等性只看 <see cref="Key"/>**（主页固定 <c>home</c>、类别按完整路径、工具按 Id），
/// 所以同一个页面反复进入不会产生重复历史；类别页 / 工具页会顺带记住节点 / 工具对象，
/// 后退时直接拿它重建页面，不用再从路径反查。</para>
/// </summary>
public sealed class ShellLocation : IEquatable<ShellLocation>
{
    private ShellLocation(ShellLocationKind kind, string key, string title,
        ToolCatalogNode? node = null, ITool? tool = null)
    {
        Kind = kind;
        Key = key;
        Title = title;
        Node = node;
        Tool = tool;
    }

    /// <summary>页面类型。</summary>
    public ShellLocationKind Kind { get; }

    /// <summary>稳定标识（相等性依据）：<c>home</c> / <c>category:路径</c> / <c>tool:工具Id</c>。</summary>
    public string Key { get; }

    /// <summary>面包屑/提示里用的短标题（主页 / 类别名 / 工具名）。</summary>
    public string Title { get; }

    /// <summary>类别页对应的目录节点（主页 / 工具页为 null）。</summary>
    public ToolCatalogNode? Node { get; }

    /// <summary>工具页对应的工具（主页 / 类别页为 null）。</summary>
    public ITool? Tool { get; }

    /// <summary>主页（全局唯一）。</summary>
    public static ShellLocation Home { get; } = new(ShellLocationKind.Home, "home", "主页");

    /// <summary>某个类别页（按完整路径区分层级）。</summary>
    public static ShellLocation ForCategory(ToolCatalogNode node)
        => new(ShellLocationKind.Category, "category:" + node.PathText, node.Name, node: node);

    /// <summary>某个工具页（按工具 Id 区分，大小写不敏感）。</summary>
    public static ShellLocation ForTool(ITool tool)
        => new(ShellLocationKind.Tool, "tool:" + tool.Id, tool.Name, tool: tool);

    /// <inheritdoc />
    public bool Equals(ShellLocation? other)
        => other is not null && StringComparer.OrdinalIgnoreCase.Equals(Key, other.Key);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ShellLocation);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Key);

    /// <inheritdoc />
    public override string ToString() => Title;
}
