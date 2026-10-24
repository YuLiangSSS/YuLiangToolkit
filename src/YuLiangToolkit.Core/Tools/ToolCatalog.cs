namespace YuLiangToolkit.Core.Tools;

/// <summary>
/// 一个类别的展示元数据：给主页 / 类别页的「类别卡片」提供图标、描述与排序权重。
///
/// <para>类别本身是从各工具的 <see cref="ITool.CategoryPath"/> 自动长出来的；
/// 这里只是可选的“补妆”信息 —— 不给也能用（默认 📁、无描述、Order = 0）。</para>
/// </summary>
public sealed class ToolCategoryInfo
{
    /// <summary>类别全路径（与 <see cref="ITool.CategoryPath"/> 对应），例如 ["音游工具"]。</summary>
    public IReadOnlyList<string> Path { get; init; } = Array.Empty<string>();

    /// <summary>类别卡片上的 Emoji 图标（默认 📁）。</summary>
    public string EmojiIcon { get; init; } = "📁";

    /// <summary>类别卡片上的一句话说明（可以为空）。</summary>
    public string Description { get; init; } = "";

    /// <summary>同层类别的排序权重，越小越靠前（默认 0）。</summary>
    public int Order { get; init; }

    /// <summary>类别名 = 路径最后一段（空路径返回空串）。</summary>
    public string Name => Path.Count == 0 ? "" : Path[^1];
}

/// <summary>
/// 目录树里的一个节点：可能是「主页根」，也可能是一层类别（任意深度）。
///
/// <para>节点本身是纯数据（不碰界面），由 <see cref="ToolCatalog.Build"/> 一次性建好并排好序，
/// 所以界面/自检读到的顺序是稳定的。路径用名字数组表示（<see cref="Path"/>），
/// 面包屑用 <see cref="Breadcrumb"/>（根 → 当前），点祖先回跳就靠它。</para>
/// </summary>
public sealed class ToolCatalogNode
{
    internal ToolCatalogNode(string name, IReadOnlyList<string> path, ToolCatalogNode? parent,
        ToolCategoryInfo? category)
    {
        Name = name;
        Path = path;
        Parent = parent;
        Category = category;
    }

    /// <summary>节点名（根节点是「主页」）。</summary>
    public string Name { get; }

    /// <summary>从根往下的完整路径；根节点为空数组。</summary>
    public IReadOnlyList<string> Path { get; }

    /// <summary>父节点（根节点为 null）。</summary>
    public ToolCatalogNode? Parent { get; internal set; }

    /// <summary>该路径的展示元数据（没配就是 null）。</summary>
    public ToolCategoryInfo? Category { get; internal set; }

    /// <summary>子类别（已按 Order → 名称排好序）。</summary>
    public IReadOnlyList<ToolCatalogNode> Children => _children;

    /// <summary>直接挂在这个节点下的工具（已按 Order → 名称排好序）。</summary>
    public IReadOnlyList<ITool> Tools => _tools;

    /// <summary>本节点 + 所有子类别里的全部工具（已递归展开；类别的“N 个工具”计数用它）。</summary>
    public IReadOnlyList<ITool> AllTools { get; private set; } = Array.Empty<ITool>();

    /// <summary>是不是「主页」根节点。</summary>
    public bool IsRoot => Parent is null;

    /// <summary>递归工具数量（类别卡片上的计数）。</summary>
    public int ToolCount => AllTools.Count;

    /// <summary>类别卡片 / 侧边栏用的图标（没配元数据时默认 📁）。</summary>
    public string EmojiIcon => Category?.EmojiIcon ?? "📁";

    /// <summary>类别说明（没配就是空串，界面据此决定要不要显示）。</summary>
    public string Description => Category?.Description ?? "";

    /// <summary>"音游工具 / 谱面处理" 这样的可读路径（根节点显示「主页」）。</summary>
    public string PathText => Path.Count == 0 ? "主页" : string.Join(" / ", Path);

    /// <summary>从根（含）到当前节点（不含）的祖先链，顺序：根 → … → 父。</summary>
    public IReadOnlyList<ToolCatalogNode> Ancestors
    {
        get
        {
            var list = new List<ToolCatalogNode>();
            for (var node = Parent; node is not null; node = node.Parent) list.Add(node);
            list.Reverse();
            return list;
        }
    }

    /// <summary>面包屑路径（根 → 当前，含自己）。</summary>
    public IReadOnlyList<ToolCatalogNode> Breadcrumb
    {
        get
        {
            var list = new List<ToolCatalogNode>(Ancestors) { this };
            return list;
        }
    }

    /// <summary>这个节点是不是 <paramref name="node"/> 的祖先（不含自己）。</summary>
    public bool IsAncestorOf(ToolCatalogNode? node)
    {
        for (var p = node?.Parent; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, this)) return true;
        }
        return false;
    }

    /// <summary>
    /// 从当前节点开始按路径找子节点（<paramref name="segments"/> 是**相对本节点**的路径）。
    /// 空路径返回自己；找不到返回 null。名称比较忽略大小写、忽略每段首尾空白。
    /// </summary>
    public ToolCatalogNode? FindByPath(IEnumerable<string>? segments)
    {
        var node = this;
        foreach (var raw in ToolCatalog.NormalizePath(segments))
        {
            node = node.FindChild(raw);
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>按名字找直接子类别（忽略大小写）。</summary>
    public ToolCatalogNode? FindChild(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        foreach (var child in _children)
        {
            if (string.Equals(child.Name, trimmed, StringComparison.OrdinalIgnoreCase)) return child;
        }
        return null;
    }

    // ---------- 建树用的内部成员 ----------

    private readonly List<ToolCatalogNode> _children = new();
    private readonly List<ITool> _tools = new();

    internal ToolCatalogNode AddChild(string name, ToolCategoryInfo? category)
    {
        var child = new ToolCatalogNode(name, new List<string>(Path) { name }.AsReadOnly(), this, category);
        _children.Add(child);
        return child;
    }

    internal void AddTool(ITool tool) => _tools.Add(tool);

    internal void AttachCategory(ToolCategoryInfo? category)
    {
        if (category is not null) Category = category;
    }

    /// <summary>排序（类别：Order → 名称；工具：Order → 名称）+ 递归汇总 <see cref="AllTools"/>。</summary>
    internal void SortAndAggregate()
    {
        // 中文系统下 CurrentCulture 就是拼音序；和主页 ToolFilter 的约定保持一致
        var byName = StringComparer.CurrentCulture;

        _children.Sort((a, b) =>
        {
            var order = (a.Category?.Order ?? 0).CompareTo(b.Category?.Order ?? 0);
            return order != 0 ? order : byName.Compare(a.Name, b.Name);
        });

        _tools.Sort((a, b) =>
        {
            var order = a.Order.CompareTo(b.Order);
            return order != 0 ? order : byName.Compare(a.Name, b.Name);
        });

        var all = new List<ITool>(_tools);
        foreach (var child in _children)
        {
            child.SortAndAggregate();
            all.AddRange(child.AllTools);
        }
        AllTools = all.AsReadOnly();
    }
}

/// <summary>
/// 工具目录树：把「实现了任意层级 <see cref="ITool.CategoryPath"/> 的工具」+「类别元数据」
/// 组装成一棵树，供主页 / 类别页 / 面包屑使用。
///
/// <para><b>纯逻辑</b>（不碰界面、不读配置），自检可以直接断言任意深度：
/// 类别合并、排序、面包屑、FindByPath、计数都在这里，改导航规则先跑自检。</para>
///
/// <code>
/// var root = ToolCatalog.Build(tools, ShellCategories.All);
/// var node = root.FindByPath(new[] { "音游工具" });      // 相对 root 的绝对路径
/// node!.Breadcrumb.Select(n => n.Name);                    // 主页 / 音游工具
/// </code>
/// </summary>
public static class ToolCatalog
{
    // 用「单元分隔符」拼 key：类别名里几乎不可能出现这个字符，比 "|" 安全
    private const char KeySeparator = '\u001F';

    /// <summary>
    /// 按各工具的 <see cref="ITool.CategoryPath"/> 建树（任意深度）。
    ///
    /// <list type="bullet">
    ///   <item>同一路径的多个工具合并到同一个节点；</item>
    ///   <item>空段 / 纯空白段被忽略（" 音游工具 " 会 trim 掉首尾空白）；</item>
    ///   <item><paramref name="categories"/> 里声明了但没人归入的类别也会建出来（数量 0，不崩）；</item>
    ///   <item>类别按 Order → 名称（CurrentCulture）排序，工具按 Order → 名称排序；</item>
    ///   <item>没写 <see cref="ITool.CategoryPath"/> 的工具挂在根节点下（保持老工具行为不变）。</item>
    /// </list>
    /// </summary>
    public static ToolCatalogNode Build(IEnumerable<ITool>? tools, IEnumerable<ToolCategoryInfo>? categories = null)
    {
        var root = new ToolCatalogNode("主页", Array.Empty<string>(), null, null);
        var index = new Dictionary<string, ToolCatalogNode>(StringComparer.OrdinalIgnoreCase);

        ToolCatalogNode EnsureNode(IReadOnlyList<string> path, ToolCategoryInfo? category)
        {
            var node = root;
            var walked = new List<string>(path.Count);
            for (var i = 0; i < path.Count; i++)
            {
                walked.Add(path[i]);
                var key = string.Join(KeySeparator, walked);
                if (!index.TryGetValue(key, out var child))
                {
                    // 只在最后一段挂元数据？不：中间层若也被声明了元数据也要生效，
                    // 所以元数据统一在建完后再按“精确路径”挂一次（见下面的 categories 循环）。
                    child = node.AddChild(path[i], null);
                    index[key] = child;
                }
                node = child;
            }

            node.AttachCategory(category);
            return node;
        }

        // ① 先建类别（这样“声明了但暂时没工具”的类别也在树里）
        if (categories is not null)
        {
            foreach (var category in categories)
            {
                if (category is null) continue;
                var path = NormalizePath(category.Path);
                if (path.Count == 0) continue;   // 根节点不接受元数据
                EnsureNode(path, category);
            }
        }

        // ② 再按工具声明的路径把工具挂进去
        if (tools is not null)
        {
            foreach (var tool in tools)
            {
                if (tool is null) continue;
                var path = NormalizePath(tool.CategoryPath);
                var node = EnsureNode(path, null);
                node.AddTool(tool);
            }
        }

        root.SortAndAggregate();
        return root;
    }

    /// <summary>
    /// 规范化路径：逐段 Trim，丢掉空段 / 纯空白段。
    /// 空路径返回空数组（= 根节点）。
    /// </summary>
    public static IReadOnlyList<string> NormalizePath(IEnumerable<string>? path)
    {
        if (path is null) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var raw in path)
        {
            var segment = raw?.Trim();
            if (string.IsNullOrEmpty(segment)) continue;
            list.Add(segment);
        }
        return list;
    }

    /// <summary>
    /// 从根节点按**绝对路径**找节点（例如 ["音游工具", "谱面处理"]）。
    /// 空路径返回根节点；找不到返回 null。
    /// </summary>
    public static ToolCatalogNode? FindByPath(ToolCatalogNode root, IEnumerable<string>? path)
        => root.FindByPath(path);

    /// <summary>
    /// 面包屑路径（根 → node，含自己）；<paramref name="node"/> 为 null 时返回空列表。
    /// 界面只负责把节点渲染成按钮，路径规则全在这里，自检直接断言。
    /// </summary>
    public static IReadOnlyList<ToolCatalogNode> BuildBreadcrumb(ToolCatalogNode? node)
        => node?.Breadcrumb ?? Array.Empty<ToolCatalogNode>();

    /// <summary>
    /// 某个工具所属的类别节点（没有 CategoryPath 的工具 → 根节点）。
    /// 打开工具时用它算面包屑（父类别路径 + 工具名）。
    /// </summary>
    public static ToolCatalogNode FindNodeOf(ToolCatalogNode root, ITool tool)
        => root.FindByPath(tool.CategoryPath) ?? root;
}
