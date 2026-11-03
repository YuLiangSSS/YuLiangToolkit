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
/// <para>解析（段落样式 / 表格 / 图片 / 大纲）在后台线程完成，排版在 UI 线程做；
/// 详细实现见 <c>Parsing/WordDocumentParser.cs</c> 与 <c>Rendering/WordFlowRenderer.cs</c>，
/// 用户文档见 <c>docs/tools/word-viewer.md</c>。</para>
/// </summary>
public class WordViewerTool : ITool, IToolCacheSource
{
    public string Id => WordViewerConfig.ToolId;

    public string Name => "Word 阅读器";

    public string Description => "只读浏览 .docx / .docm：段落样式、表格、图片、大纲、缩放与查找，不会改动原文件";

    public string EmojiIcon => "📘";

    public string Category => "文档";

    public IReadOnlyList<string> Tags => new[]
    {
        "Word", "docx", "docm", "文档", "阅读器", "只读", "浏览", "大纲", "缩放", "查找", "阅读",
    };

    public int Order => 18;

    public FrameworkElement CreateView() => new WordViewerView
    {
        DataContext = new WordViewerViewModel(),
    };

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    /// <para>本工具没有 Cache 目录；只有「最近打开记录」混在
    /// <c>Config\word-viewer\config.json</c> 里，所以只报告这一项，
    /// 清掉它不会动缩放、大纲开关等设置。记录为空时返回空数组（设置页不出现空条目）。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），只拿「最近打开记录」条数
        var count = ToolConfigPeek.Read<WordViewerConfig>(WordViewerConfig.ToolId)?.RecentEntries?.Count ?? 0;
        if (count == 0) return Array.Empty<ToolCacheItem>();

        return new[]
        {
            new ToolCacheItem
            {
                Id = "recent",
                Name = "最近打开记录",
                Description = "存在 Config\\word-viewer\\config.json 里；只清最近打开记录，不动缩放 / 大纲等设置",
                Detail = $"{count} 条",
                Clear = () => ToolConfigPeek.Mutate<WordViewerConfig>(
                    WordViewerConfig.ToolId, config => config.ClearRecent()),
            },
        };
    }
}
