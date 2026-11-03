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

    public string EmojiIcon => "📗";

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
    /// <para>本工具没有 Cache 目录；只有「最近打开记录」混在
    /// <c>Config\excel-viewer\config.json</c> 里，所以只报告这一项，清它不会动网格线等设置。
    /// 记录为空时返回空数组（设置页就不会出现空条目）。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），只拿「最近打开记录」条数
        var count = ToolConfigPeek.Read<ExcelViewerConfig>(ExcelViewerConfig.ToolId)?.RecentEntries?.Count ?? 0;
        if (count == 0) return Array.Empty<ToolCacheItem>();

        return new[]
        {
            new ToolCacheItem
            {
                Id = "recent",
                Name = "最近打开记录",
                Description = "存在 Config\\excel-viewer\\config.json 里；只清最近打开记录，不动网格线等设置",
                Detail = $"{count} 条",
                // 清空后配置照常自动落盘；下次打开 Excel 阅读器，「最近」下拉就是空的
                Clear = () => ToolConfigPeek.Mutate<ExcelViewerConfig>(
                    ExcelViewerConfig.ToolId, config => config.ClearRecent()),
            },
        };
    }
}
