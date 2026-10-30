using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PdfReader.ViewModels;
using YuLiangToolkit.Tools.PdfReader.Views;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「PDF 阅读器」——只做三件简单的事：
///
/// <list type="number">
///   <item><b>上下滚动</b>：所有页从上到下排成一条长带，滚轮 / 左键拖动连续看；</item>
///   <item><b>左右滚动</b>：所有页从左到右排成一条长带，滚轮 / 左键拖动连续看；</item>
///   <item><b>双页翻页</b>：左右两页并排，一次翻过去（滚轮 / 方向键 / 屏幕按钮）。</item>
/// </list>
///
/// <para>两种滚动模式下按住左键拖动，松手都会按甩动速度**继续滑一段**（惯性）；
/// PDF 解码用系统自带的 <c>Windows.Data.Pdf</c>，不引入第三方 PDF 库。</para>
/// </summary>
public class PdfReaderTool : ITool, IToolCacheSource
{
    public string Id => PdfReaderConfig.ToolId;

    public string Name => "PDF 阅读器";

    public string Description => "简单 PDF 阅读器：上下 / 左右连续滚动，双页并排翻页阅读，左键拖动带惯性";

    public string EmojiIcon => "📄";

    public string Category => "媒体";

    public IReadOnlyList<string> Tags => new[]
    {
        "PDF", "文档", "阅读器", "阅读", "滚动", "上下滚动", "左右滚动", "连续滚动",
        "双页", "翻页", "两页", "阅读模式", "拖拽", "惯性", "甩动",
    };

    public int Order => 17;

    public FrameworkElement CreateView() => new PdfReaderView
    {
        DataContext = new PdfReaderViewModel(),
    };

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    ///
    /// <para>本功能的「最近打开记录」和阅读方式 / 缩放等设置一起存在
    /// <c>Config\pdf-reader\config.json</c> 里；这里只清最近记录，不动其它设置
    /// （每本 PDF 的阅读位置也保留，不受影响）。记录为空时返回空数组。</para>
    ///
    /// <para><b>以后新工具怎么接</b>：让工具类多实现 <see cref="IToolCacheSource"/>，
    /// 在这里返回「Id + 名称 + 说明 + 当前数量 + 清理动作」（<see cref="ToolCacheItem"/>）即可，
    /// 外壳会自动收集所有实现了该接口的工具，不实现就等于没有缓存项。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），拿「最近打开记录」条数。
        var count = ToolConfigPeek.Read<PdfReaderConfig>(PdfReaderConfig.ToolId)?.RecentEntries?.Count ?? 0;
        if (count == 0) return Array.Empty<ToolCacheItem>();

        return new[]
        {
            new ToolCacheItem
            {
                Id = "recent",
                Name = "最近打开记录",
                Description = "存在 Config\\pdf-reader\\config.json 里；只清最近打开记录，不动阅读设置与阅读位置",
                Detail = $"{count} 条",
                // 清空后配置照常自动落盘；下次打开 PDF 阅读器，「最近」列表就是空的。
                Clear = () => ToolConfigPeek.Mutate<PdfReaderConfig>(
                    PdfReaderConfig.ToolId, config => config.ClearRecent()),
            },
        };
    }
}
