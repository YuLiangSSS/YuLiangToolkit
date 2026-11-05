using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.WordViewer.ViewModels;
using YuLiangToolkit.Tools.WordViewer.Views;

namespace YuLiangToolkit.Tools.WordViewer;

/// <summary>
/// 「Word 阅读器」——<b>只读</b>浏览 <c>.docx</c> / <c>.docm</c>：
/// 手写 OOXML 解析 + WPF FlowDocument 排版，不引第三方库，也不提供任何编辑 / 保存入口。
///
/// <para>解析（段落样式 / 表格 / 图片 / 大纲 / 页眉页脚 / 脚注尾注 / 文档属性）在后台线程完成，
/// 排版在 UI 线程做；详细实现见 <c>Parsing/WordDocumentParser.cs</c> 与
/// <c>Rendering/WordFlowRenderer.cs</c>，用户文档见 <c>docs/tools/word-viewer.md</c>。</para>
/// </summary>
public class WordViewerTool : ITool, IToolCacheSource
{
    public string Id => WordViewerConfig.ToolId;

    public string Name => "Word 阅读器";

    public string Description => "只读浏览 .docx / .docm：段落样式、表格、图片、页眉页脚、脚注尾注、大纲、缩放、查找与复制全文，不会改动原文件";

    public string EmojiIcon => "📘";

    public string Category => "文档";

    public IReadOnlyList<string> Tags => new[]
    {
        "Word", "docx", "docm", "文档", "阅读器", "只读", "浏览", "大纲", "缩放", "查找", "阅读",
        "页眉", "页脚", "脚注", "尾注", "复制全文", "阅读位置",
    };

    public int Order => 18;

    public FrameworkElement CreateView() => new WordViewerView
    {
        DataContext = new WordViewerViewModel(),
    };

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    /// <para>本工具没有 Cache 目录；只有两类「使用痕迹」混在
    /// <c>Config\word-viewer\config.json</c> 里：最近打开记录、每份文档的阅读位置。
    /// 两项分开报告、分开清理，清掉都不会动缩放、页眉页脚开关等设置。为空时不返回条目。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），只拿条数
        var config = ToolConfigPeek.Read<WordViewerConfig>(WordViewerConfig.ToolId);

        var recentCount = config?.RecentEntries?.Count ?? 0;
        var positionCount = config?.ReadingPositions?.Count ?? 0;
        if (recentCount == 0 && positionCount == 0) return Array.Empty<ToolCacheItem>();

        var items = new List<ToolCacheItem>();

        if (recentCount > 0)
        {
            items.Add(new ToolCacheItem
            {
                Id = "recent",
                Name = "最近打开记录",
                Description = "存在 Config\\word-viewer\\config.json 里；只清最近打开记录，不动缩放 / 大纲等设置",
                Detail = $"{recentCount} 条",
                Clear = () => ToolConfigPeek.Mutate<WordViewerConfig>(
                    WordViewerConfig.ToolId, target => target.ClearRecent()),
            });
        }

        if (positionCount > 0)
        {
            items.Add(new ToolCacheItem
            {
                Id = "reading-positions",
                Name = "阅读位置记录",
                Description = "每份文档「上次读到哪」的滚动比例；只清这些记录，不动缩放 / 页眉页脚等设置",
                Detail = $"{positionCount} 份文档",
                Clear = () => ToolConfigPeek.Mutate<WordViewerConfig>(
                    WordViewerConfig.ToolId, target => target.ClearReadingPositions()),
            });
        }

        return items;
    }
}
