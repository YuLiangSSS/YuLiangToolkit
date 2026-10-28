using YuLiangToolkit.App;
using YuLiangToolkit.App.Models;
using YuLiangToolkit.App.ViewModels;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 外壳分类导航自检：任意层级类别树 / 类别合并 / 排序 / 面包屑 / FindByPath / 根层共存 /
/// 默认 CategoryPath 为空 / 分类内工具也能被主页搜索命中 / 计数 / 空类别不崩。
///
/// <para>被测逻辑全在 <see cref="ToolCatalog"/>（纯逻辑，不碰界面）+ 两个展示模型
/// （<see cref="CategoryCard"/> / <see cref="BreadcrumbItem"/>，也只做字符串拼装），
/// 所以不需要 WPF Application / 窗口，控制台里直接断言。</para>
///
/// <para>接线方式（Program.cs 里加这一段即可）：</para>
/// <code>
/// // ---------- 外壳分类导航（任意层级目录树 / 面包屑 / 类别计数） ----------
/// try
/// {
///     NavigationChecks.Run(
///         (name, ok, detail) => Check($"[导航] {name}", ok, detail),
///         (name, ok) => Check($"[导航] {name}", ok));
/// }
/// catch (Exception ex)
/// {
///     Check($"[导航] 运行过程中抛出异常（{ex.GetType().Name}）", false,
///         $"{ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
/// }
/// </code>
/// </summary>
public static class NavigationChecks
{
    /// <summary>跑完整组断言（签名与其它 Checks 一致：带 detail 的 check + 不带 detail 的 plain）。</summary>
    public static void Run(Action<string, bool, string> checkWithDetail, Action<string, bool> plainCheck)
    {
        // 局部包装：断言多的时候不写 detail 更清爽（Program.cs 传进来的两个委托语义不变）
        void check(string name, bool ok, string detail = "") => checkWithDetail(name, ok, detail);
        void plain(string name, bool ok) => plainCheck(name, ok);

        // ============================ 0. 单实例唤醒的消息格式 ============================
        // 第二个进程把命令行经命名管道交给第一个进程；这里只测 JSON 编解码（不真的开管道）。
        var wakePayload = SingleInstance.SerializeMessage(new[] { @"C:\图 片\003.png", "" });
        var wakeOk = SingleInstance.TryDeserializeMessage(wakePayload, out var wakeArgs);
        check("[外壳] 单实例消息：序列化→反序列化保留参数（含空格 / 中文）",
            wakeOk && wakeArgs.Length == 2 && wakeArgs[0] == @"C:\图 片\003.png" && wakeArgs[1] == "",
            wakePayload);
        check("[外壳] 单实例消息：坏 JSON / 版本不对 / 参数不是字符串都忽略",
            !SingleInstance.TryDeserializeMessage("not json", out _)
            && !SingleInstance.TryDeserializeMessage("{\"protocolVersion\":999,\"arguments\":[]}", out _)
            && !SingleInstance.TryDeserializeMessage("{\"protocolVersion\":1,\"arguments\":[123]}", out _));
        check("[外壳] 单实例消息：没有参数也能还原（第二次启动只唤醒窗口）",
            SingleInstance.TryDeserializeMessage(
                SingleInstance.SerializeMessage(Array.Empty<string>()), out var wakeEmpty)
            && wakeEmpty.Length == 0);

        // ============================ 1. 默认 CategoryPath（老工具兼容） ============================
        ITool plainTool = new NavPlainTool("legacy-tool", "老工具", "媒体", 10);
        plain("默认 CategoryPath 为空（老工具不用改代码）", plainTool.CategoryPath.Count == 0);
        check("默认 CategoryPath 为空数组（不是 null）", plainTool.CategoryPath is not null,
            "CategoryPath 的实现应返回 Array.Empty<string>()");

        // ============================ 2. 建树：根层工具 + 任意层级类别 ============================
        var tools = new ITool[]
        {
            new NavPlainTool("root-media", "根层工具", "媒体", 30),
            new NavFakeTool("root-dev", "根层开发工具", 20, category: "开发"),
            new NavFakeTool("blank-path", "空白路径工具", 900, path: new[] { " ", "" }),   // 空段忽略 → 根层
            new NavFakeTool("music-down", "osu! 下图器", 40, category: "音游工具", path: new[] { "音游工具" },
                "osu", "下图"),
            new NavFakeTool("music-ln", "LN 转换器", 20, category: "音游工具", path: new[] { "音游工具" },
                "osu", "ln"),
            new NavFakeTool("music-rate", "等级计算器", 10, category: "音游工具",
                path: new[] { "音游工具", "谱面处理" }, "osu", "等级"),
            new NavFakeTool("music-convert", "谱面格式转换", 1, category: "音游工具",
                path: new[] { "音游工具", "谱面处理", "转换" }, "osu", "格式"),
            new NavFakeTool("media-video", "空格路径工具", 5, category: "媒体工具",
                path: new[] { " 媒体工具 ", "", "  视频  " }, "视频"),
            new NavFakeTool("english-path", "English Tool", 6, category: "Media",
                path: new[] { "Media", "Video" }, "english"),
        };

        // 类别元数据：🎵 音游工具（有工具）、🧩 空类别（声明了但暂时没工具）
        var categories = new[]
        {
            new ToolCategoryInfo
            {
                Path = new[] { "音游工具" },
                EmojiIcon = "🎵",
                Description = "osu! 谱面转换、LN 处理、等级计算与下图",
                Order = 10,
            },
            new ToolCategoryInfo
            {
                Path = new[] { "空类别" },
                EmojiIcon = "🧩",
                Description = "还没有工具的空类别",
                Order = 5,
            },
        };

        var root = ToolCatalog.Build(tools, categories);

        plain("根节点是「主页」且没有父节点", root.IsRoot && root.Parent is null && root.Name == "主页");
        check("根层工具与类别共存", root.Tools.Count > 0 && root.Children.Count > 0,
            $"根层工具 {root.Tools.Count} 个 / 根层类别 {root.Children.Count} 个");
        check("根层工具只包含没声明路径的（含空白路径 → 忽略空段）", root.Tools.Count == 3,
            string.Join(" / ", root.Tools.Select(t => t.Name)));
        check("根节点递归工具总数 = 全部工具数", root.ToolCount == tools.Length,
            $"root.ToolCount={root.ToolCount} / tools={tools.Length}");

        // ============================ 3. 任意深度 + 同一路径合并 ============================
        var music = root.FindByPath(new[] { "音游工具" });
        var chart = music?.FindByPath(new[] { "谱面处理" });
        var convert = chart?.FindByPath(new[] { "转换" });

        plain("按路径找得到任意深度的节点", music is not null && chart is not null && convert is not null);
        if (music is null || chart is null || convert is null)
        {
            plain("类别树没建出来，后面的深度断言跳过", false);
            return;
        }

        check("最深节点路径正确（3 层）",
            convert.Path.SequenceEqual(new[] { "音游工具", "谱面处理", "转换" }), convert.PathText);
        check("同一路径的多个工具合并到一个节点",
            music.Tools.Count == 2 && music.AllTools.Count == 4 && music.ToolCount == 4,
            $"直接 {music.Tools.Count} / 递归 {music.AllTools.Count}");
        check("递归计数 = 直接工具 + 所有子类别工具",
            music.ToolCount == music.Tools.Count + music.Children.Sum(c => c.ToolCount),
            $"音游工具 {music.ToolCount} = {music.Tools.Count} + 子类别");
        check("中间层节点只统计自己子树",
            chart.Tools.Count == 1 && chart.ToolCount == 2, $"谱面处理 ToolCount={chart.ToolCount}");
        check("叶子节点计数为 1", convert.ToolCount == 1 && convert.Children.Count == 0);
        check("子类别父指针正确", ReferenceEquals(chart.Parent, music) && ReferenceEquals(convert.Parent, chart));
        check("空格 / 空串路径段被忽略",
            root.FindByPath(new[] { " 媒体工具 ", "", "  视频  " }) is { } spaced
            && spaced.Name == "视频" && spaced.Path.Count == 2,
            "「 媒体工具 」「」「  视频  」→ 媒体工具 / 视频");
        check("路径里的英文段忽略大小写",
            root.FindByPath(new[] { "media", "VIDEO" }) is { } lower && lower.Name == "Video",
            "media/VIDEO 能找到 Media/Video");

        // ============================ 4. 排序 ============================
        var categoryNames = root.Children.Select(c => c.Name).ToList();
        // 媒体工具 / Media 没有元数据（Order 0），空类别 Order 5，音游工具 Order 10；
        // 同 Order 的两个按当前系统区域比较（中文系统里 "Media" 与 "媒体工具" 的先后由 CurrentCulture 决定）
        check("类别排序：Order 小的在前（同 Order 按名称）",
            categoryNames.SequenceEqual(new[] { "媒体工具", "Media", "空类别", "音游工具" })
            || categoryNames.SequenceEqual(new[] { "Media", "媒体工具", "空类别", "音游工具" }),
            string.Join(" / ", categoryNames));
        check("同层类别里 Order 5 的空类别排在 Order 10 的音游工具前面",
            categoryNames.IndexOf("空类别") < categoryNames.IndexOf("音游工具"), string.Join(" / ", categoryNames));
        var musicTools = music.Tools.Select(t => t.Name).ToList();
        check("工具排序：Order 小的在前（同 Order 按名称）",
            musicTools.SequenceEqual(new[] { "LN 转换器", "osu! 下图器" }), string.Join(" / ", musicTools));
        check("子类别只有一层时也排序正确", chart.Tools.Single().Name == "等级计算器");

        // ============================ 5. 类别元数据 ============================
        check("类别元数据生效（emoji / 描述 / 排序）",
            music.EmojiIcon == "🎵" && music.Description.Contains("osu!") && music.Category?.Order == 10,
            $"{music.EmojiIcon} {music.Description}");
        check("没配元数据的类别用默认 📁、无描述",
            root.FindByPath(new[] { "媒体工具" }) is { } plainCategory
            && plainCategory.EmojiIcon == "📁" && plainCategory.Description == "",
            "媒体工具没有 ToolCategoryInfo");
        var emptyCategory = root.FindByPath(new[] { "空类别" });
        check("声明了但没工具的空类别也在树里，且计数为 0（不崩）",
            emptyCategory is { ToolCount: 0, Tools.Count: 0, Children.Count: 0 }, "空类别");
        check("空类别元数据的 emoji / 描述也生效",
            emptyCategory is { EmojiIcon: "🧩" } && emptyCategory?.Description == "还没有工具的空类别");

        // ============================ 6. 面包屑 ============================
        var breadcrumb = convert.Breadcrumb;
        check("面包屑：根 → 当前（含自己）",
            breadcrumb.Select(n => n.Name).SequenceEqual(new[] { "主页", "音游工具", "谱面处理", "转换" }),
            string.Join(" > ", breadcrumb.Select(n => n.Name)));
        check("面包屑第一枚是根、最后一枚是当前",
            breadcrumb[0].IsRoot && !breadcrumb[^1].IsRoot && ReferenceEquals(breadcrumb[^1], convert));
        check("Ancestors 不含自己、从根到父",
            convert.Ancestors.Select(n => n.Name).SequenceEqual(new[] { "主页", "音游工具", "谱面处理" }),
            string.Join(" > ", convert.Ancestors.Select(n => n.Name)));
        check("根节点的面包屑只有自己", root.Breadcrumb.Count == 1 && ReferenceEquals(root.Breadcrumb[0], root));
        check("ToolCatalog.BuildBreadcrumb(null) 返回空列表", ToolCatalog.BuildBreadcrumb(null).Count == 0);
        check("ToolCatalog.BuildBreadcrumb(node) 与 node.Breadcrumb 一致",
            ToolCatalog.BuildBreadcrumb(chart).Select(n => n.Name).SequenceEqual(chart.Breadcrumb.Select(n => n.Name))
            && chart.Breadcrumb.Count == 3);
        check("祖先关系判断正确",
            music.IsAncestorOf(convert) && chart.IsAncestorOf(convert)
            && !convert.IsAncestorOf(music) && !music.IsAncestorOf(music),
            "音游工具 → 转换 是祖孙；反过来不是");
        check("PathText 是可读全路径",
            convert.PathText == "音游工具 / 谱面处理 / 转换" && root.PathText == "主页",
            convert.PathText);

        // ============================ 7. FindByPath / FindNodeOf ============================
        check("相对路径：空路径返回自己", ReferenceEquals(music.FindByPath(Array.Empty<string>()), music));
        check("相对路径：空数组也返回自己", ReferenceEquals(music.FindByPath(null), music));
        check("找不到的路径返回 null", root.FindByPath(new[] { "音游工具", "不存在的子类" }) is null);
        check("ToolCatalog.FindByPath(root, 绝对路径) 走同一套规则",
            ReferenceEquals(ToolCatalog.FindByPath(root, new[] { "音游工具", "谱面处理" }), chart));
        var deepTool = tools.First(t => t.Id == "music-convert");
        var rootTool = tools.First(t => t.Id == "root-media");
        check("FindNodeOf：分类里的工具找到最深层类别节点",
            ReferenceEquals(ToolCatalog.FindNodeOf(root, deepTool), convert));
        check("FindNodeOf：根层工具回落到根节点",
            ReferenceEquals(ToolCatalog.FindNodeOf(root, rootTool), root));
        check("FindNodeOf：空白路径工具也回落到根节点",
            ReferenceEquals(ToolCatalog.FindNodeOf(root, tools.First(t => t.Id == "blank-path")), root));

        // ============================ 8. 展示模型（类别卡片 / 面包屑项） ============================
        var musicCard = new CategoryCard(music);
        check("类别卡片：名称 / emoji / 描述 / 计数",
            musicCard.Name == "音游工具" && musicCard.Emoji == "🎵"
            && musicCard.HasDescription && musicCard.ToolCount == 4,
            musicCard.CountText);
        check("类别卡片计数文案：带子分类时一起显示",
            musicCard.CountText == "4 个工具 · 1 个子分类", musicCard.CountText);
        check("空的类别卡片显示 0 个工具、不带子分类",
            new CategoryCard(emptyCategory!).CountText == "0 个工具");
        check("类别卡片没有描述时 HasDescription=false（界面不显示空行）",
            !new CategoryCard(root.FindByPath(new[] { "媒体工具" })!).HasDescription);

        var homeCrumb = BreadcrumbItem.ForHome(root, isCurrent: false);
        var musicCrumb = BreadcrumbItem.ForCategory(music, isCurrent: true, isFirst: false);
        var toolCrumb = BreadcrumbItem.ForTool(deepTool, isFirst: false);
        check("面包屑项：主页 / 类别 / 工具 的显示文案与状态",
            homeCrumb.DisplayText == "🏠 主页" && homeCrumb.IsHome && homeCrumb.IsFirst
            && !homeCrumb.ShowSeparator && homeCrumb.IsClickable
            && musicCrumb.DisplayText == "🎵 音游工具" && musicCrumb.IsCurrent && !musicCrumb.IsClickable
            && musicCrumb.ShowSeparator
            && toolCrumb.DisplayText.StartsWith("🧪 ") && toolCrumb.IsCurrent && !toolCrumb.IsClickable,
            $"{homeCrumb.DisplayText} / {musicCrumb.DisplayText} / {toolCrumb.DisplayText}");
        check("面包屑项：祖先项可点（不是当前项时）",
            BreadcrumbItem.ForCategory(chart, isCurrent: false, isFirst: false).IsClickable);

        // ============================ 9. 搜索：分类里的工具也能被主页搜到 ============================
        // 主页搜索是「全部工具扁平过滤」（不管工具在不在类别里），这里用真正的 ToolCard / ToolFilter 断言。
        var cards = tools.Select(t => new ToolCard(t, null)).ToList();
        var byName = ToolFilter.Apply(cards, "下图器", ToolSortMode.Default);
        check("主页搜索能命中类别里的工具（按名称）",
            byName.Count == 1 && byName[0].Id == "music-down", byName.FirstOrDefault()?.Name ?? "(空)");
        var byTag = ToolFilter.Apply(cards, "osu", ToolSortMode.Default);
        check("主页搜索能命中类别里的工具（按标签）", byTag.Count >= 3, $"命中 {byTag.Count} 个");
        var byCategoryName = ToolFilter.Apply(cards, "音游工具", ToolSortMode.Default);
        check("主页搜索能按类别名命中（Category 仍是标签）",
            byCategoryName.Count == 4, $"命中 {byCategoryName.Count} 个");
        check("主页搜索能按子分类路径命中（CategoryPath 也进搜索索引）",
            ToolFilter.Apply(cards, "谱面处理", ToolSortMode.Default).Count == 2,
            "谱面处理（等级计算器 + 谱面格式转换）");

        // ============================ 10. 边界：空输入不崩 ============================
        var emptyRoot = ToolCatalog.Build(Array.Empty<ITool>(), null);
        check("空工具列表建出只有根节点的空树",
            emptyRoot.IsRoot && emptyRoot.Tools.Count == 0 && emptyRoot.Children.Count == 0
            && emptyRoot.ToolCount == 0);
        check("空输入时 FindByPath 不崩", emptyRoot.FindByPath(new[] { "没有" }) is null);

        var metadataOnly = ToolCatalog.Build(null, new[]
        {
            new ToolCategoryInfo { Path = new[] { "只声明元数据" }, Order = 1 },
        });
        check("只给类别元数据（不给工具）也能建树",
            metadataOnly.Children.Count == 1 && metadataOnly.Children[0].ToolCount == 0,
            metadataOnly.Children.FirstOrDefault()?.Name ?? "(空)");
        check("路径全为空白的类别元数据被忽略",
            ToolCatalog.Build(null, new[] { new ToolCategoryInfo { Path = new[] { " ", "" } } })
                .Children.Count == 0);
        check("NormalizePath：trim + 丢空段",
            ToolCatalog.NormalizePath(new[] { " A ", "", "B", "  " }).SequenceEqual(new[] { "A", "B" }));
    }
}

/// <summary>自检用的假工具：可声明任意层级 CategoryPath（CreateView 不会被调用）。</summary>
internal sealed class NavFakeTool : ITool
{
    public NavFakeTool(string id, string name, int order, string category = "测试",
        string[]? path = null, params string[] tags)
    {
        Id = id;
        Name = name;
        Order = order;
        Category = category;
        CategoryPath = path ?? Array.Empty<string>();
        Tags = tags;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description => $"{Name} 的描述";
    public string EmojiIcon => "🧪";
    public string Category { get; }
    public IReadOnlyList<string> Tags { get; }
    public IReadOnlyList<string> CategoryPath { get; }
    public int Order { get; }

    public System.Windows.FrameworkElement CreateView() => null!;
}

/// <summary>自检用：**不写** CategoryPath 的假工具（验证接口默认实现 = 根层）。</summary>
internal sealed class NavPlainTool : ITool
{
    public NavPlainTool(string id, string name, string category, int order)
    {
        Id = id;
        Name = name;
        Category = category;
        Order = order;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description => $"{Name} 的描述";
    public string EmojiIcon => "🧪";
    public string Category { get; }
    public int Order { get; }

    public System.Windows.FrameworkElement CreateView() => null!;
}
