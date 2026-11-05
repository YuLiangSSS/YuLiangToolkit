using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ExcelViewer.ViewModels;
using YuLiangToolkit.Tools.ExcelViewer.Views;

namespace YuLiangToolkit.Tools.ExcelViewer;

/// <summary>
/// 「Excel 阅读器」——只读浏览 <c>.xlsx</c> / <c>.xlsm</c> 工作簿：
/// 多工作表切换、按 Excel 列宽显示、合并单元格、数字 / 日期格式、单元格样式，
/// 选中单元格后在底部看地址 / 显示值 / 原始值 / 公式。
///
/// <para><b>只读</b>：不提供任何编辑 / 保存入口，也永远不会写回原文件；
/// 解析用 Core 的 <see cref="Core.Documents.OoxmlPackage"/> + 手写 XML 逻辑，不引第三方 Excel 库、不需要装 Excel。</para>
/// </summary>
public class ExcelViewerTool : ITool, IToolCacheSource
{
    public string Id => ExcelViewerConfig.ToolId;

    public string Name => "Excel 阅读器";

    public string Description => "只读浏览 .xlsx / .xlsm 工作簿：多工作表切换、单元格样式与数字格式、合并单元格，选中即看地址 / 值 / 公式";

    /// <summary>
    /// 卡片 / 侧边栏图标。<b>故意和 Word 阅读器的 📘 区分开</b>（两个文档阅读器一眼能分清）：
    /// 这里用「柱状图」📊 —— 表格数据 / 报表的通用意象，也不是别的工具在用的图标。
    /// </summary>
    public string EmojiIcon => "📊";

    public string Category => "文档";

    public IReadOnlyList<string> Tags => new[]
    {
        "Excel", "xlsx", "xlsm", "表格", "工作簿", "工作表", "单元格", "文档", "阅读器", "只读", "浏览",
    };

    public int Order => 19;

    public FrameworkElement CreateView() => new ExcelViewerView
    {
        DataContext = new ExcelViewerViewModel(),
    };

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    /// <para>本工具没有 Cache 目录；只有「最近打开记录」和「阅读位置」混在
    /// <c>Config\excel-viewer\config.json</c> 里，所以报告这两项，清理它们不会动网格线 / 冻结 / 行高等设置。
    /// 记录为空时返回空数组（设置页就不会出现空条目）。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），只拿条数
        var config = ToolConfigPeek.Read<ExcelViewerConfig>(ExcelViewerConfig.ToolId);
        var recentCount = config?.RecentEntries?.Count ?? 0;
        var positionCount = config?.ReadingPositionCount ?? 0;

        var items = new List<ToolCacheItem>();
        if (recentCount > 0)
        {
            items.Add(new ToolCacheItem
            {
                Id = "recent",
                Name = "最近打开记录",
                Description = "存在 Config\\excel-viewer\\config.json 里；只清最近打开记录，不动网格线等设置",
                Detail = $"{recentCount} 条",
                // 清空后配置照常自动落盘；下次打开 Excel 阅读器，「最近」下拉就是空的
                Clear = () => ToolConfigPeek.Mutate<ExcelViewerConfig>(
                    ExcelViewerConfig.ToolId, viewerConfig => viewerConfig.ClearRecent()),
            });
        }

        if (positionCount > 0)
        {
            items.Add(new ToolCacheItem
            {
                Id = "reading-positions",
                Name = "阅读位置记录",
                Description = "记住每本工作簿上次看到哪张表、滚到哪；只清记录，不删工作簿，也不动其它设置",
                Detail = $"{positionCount} 本",
                Clear = () => ToolConfigPeek.Mutate<ExcelViewerConfig>(
                    ExcelViewerConfig.ToolId, viewerConfig => viewerConfig.ClearReadingPositions()),
            });
        }

        return items;
    }
}
