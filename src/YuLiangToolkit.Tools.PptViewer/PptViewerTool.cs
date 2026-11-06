using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PptViewer.ViewModels;
using YuLiangToolkit.Tools.PptViewer.Views;

namespace YuLiangToolkit.Tools.PptViewer;

/// <summary>
/// 「PPT 阅读器」——<b>只读</b>浏览 <c>.pptx</c> / <c>.pptm</c> / <c>.ppsx</c>：
/// 手写 OOXML 解析 + WPF 自绘幻灯片，不引第三方 PPT 库，也不需要安装 PowerPoint / WPS。
///
/// <para>能看：文字 / 图片 / 表格 / 常见自选图形 / 背景 / 主题色，左侧缩略图导航、备注栏、
/// 查找、缩放、全屏放映；解析在后台线程完成，界面只负责画和翻页。</para>
///
/// <para>详细实现见 <c>Parsing/PptDocumentParser.cs</c>、<c>Rendering/PptSlideRenderer.cs</c>，
/// 用户文档见 <c>docs/tools/ppt-viewer.md</c>。</para>
/// </summary>
public class PptViewerTool : ITool, IToolCacheSource
{
    public string Id => PptViewerConfig.ToolId;

    public string Name => "PPT 阅读器";

    public string Description =>
        "只读浏览 .pptx / .pptm / .ppsx：幻灯片原样还原（文字 / 图片 / 表格 / 形状 / 主题色）、缩略图导航、备注、查找、缩放与全屏放映，不会改动原文件";

    /// <summary>卡片图标：放映机（和 Word 📘 / Excel 📊 / PDF 📄 区分开）。</summary>
    public string EmojiIcon => "📽️";

    public string Category => "文档";

    public IReadOnlyList<string> Tags => new[]
    {
        "PPT", "pptx", "pptm", "ppsx", "幻灯片", "演示文稿", "幻灯", "演示", "放映", "全屏放映",
        "文档", "阅读器", "只读", "浏览", "缩略图", "备注", "缩放", "查找", "翻页",
    };

    public int Order => 20;

    public FrameworkElement CreateView() => new PptViewerView
    {
        DataContext = new PptViewerViewModel(),
    };

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    /// <para>本工具没有 Cache 目录；只有两类「使用痕迹」混在
    /// <c>Config\ppt-viewer\config.json</c> 里：最近打开记录、每份演示文稿看到第几页。
    /// 两项分开报告、分开清理，清掉都不会动缩放 / 缩略图 / 放映设置。为空时不返回条目。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），只拿条数
        var config = ToolConfigPeek.Read<PptViewerConfig>(PptViewerConfig.ToolId);

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
                Description = "存在 Config\\ppt-viewer\\config.json 里；只清最近打开记录，不动缩放 / 缩略图等设置",
                Detail = $"{recentCount} 条",
                Clear = () => ToolConfigPeek.Mutate<PptViewerConfig>(
                    PptViewerConfig.ToolId, target => target.ClearRecent()),
            });
        }

        if (positionCount > 0)
        {
            items.Add(new ToolCacheItem
            {
                Id = "reading-positions",
                Name = "阅读位置记录",
                Description = "每份演示文稿「上次看到第几页」；只清这些记录，不动缩放 / 放映等设置",
                Detail = $"{positionCount} 份",
                Clear = () => ToolConfigPeek.Mutate<PptViewerConfig>(
                    PptViewerConfig.ToolId, target => target.ClearReadingPositions()),
            });
        }

        return items;
    }
}
