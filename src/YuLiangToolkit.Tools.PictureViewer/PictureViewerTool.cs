using System.Windows;
using YuLiangToolkit.Core.Maintenance;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PictureViewer.ViewModels;
using YuLiangToolkit.Tools.PictureViewer.Views;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「图片连续阅读器」——把一整个文件夹的图片按页序首尾相接拼成一条长带，连续滚动浏览。
///
/// <para>解决普通看图器「一屏一张、看条漫 / 长图要一张张点」的痛点。三种滚动方式：纵向（上→下）、
/// 横向左→右（中式漫画）、横向右→左（日式漫画）。滚轮 / 左键拖拽 / 中键锚点（单点留锚点、按住松手消失）
/// 都能连续移动。</para>
///
/// <para>分层与实现细节见 <c>docs/dev/pitfalls.md</c>。</para>
/// </summary>
public class PictureViewerTool : ITool, IToolCacheSource
{
    public string Id => PictureViewerConfig.ToolId;

    public string Name => "图片连续阅读器";

    public string Description => "把文件夹里的图片拼成一条长带连续滚动：条漫上下滚，漫画可切成从左到右 / 从右到左；支持滚轮、拖拽与中键锚点";

    public string EmojiIcon => "🖼️";

    public string Category => "媒体";

    public IReadOnlyList<string> Tags => new[]
    {
        "图片", "看图", "图片查看器", "漫画", "条漫", "长图", "滚动", "连续", "画廊", "阅读器",
        "左右滚动", "上下滚动", "日式漫画", "右到左", "中式漫画", "左到右", "拖拽", "中键", "锚点",
    };

    public int Order => 16;

    public FrameworkElement CreateView() => new PictureViewerView
    {
        DataContext = new PictureViewerViewModel(),
    };

    /// <summary>
    /// 告诉主页「设置 → 清理缓存」本功能有哪些可以清的东西。
    ///
    /// <para>本功能的「最近浏览记录」和滚动方式 / 缩放等设置一起存在
    /// <c>Config\picture-viewer\config.json</c> 里；这里只清最近记录，不动其它设置。
    /// 记录为空时返回空数组，设置页就不会列出这一项。</para>
    ///
    /// <para><b>以后新工具怎么接</b>：让工具类多实现 <see cref="IToolCacheSource"/>，
    /// 在这里返回「Id + 名称 + 说明 + 当前数量 + 清理动作」（<see cref="ToolCacheItem"/>）即可，
    /// 外壳会自动收集所有实现了该接口的工具，不实现就等于没有缓存项。</para>
    /// </summary>
    public IReadOnlyList<ToolCacheItem> DescribeCacheItems()
    {
        // 直接读盘上的配置（不触发工具加载），拿「最近浏览记录」条数。
        var count = ToolConfigPeek.Read<PictureViewerConfig>(PictureViewerConfig.ToolId)?.RecentEntries?.Count ?? 0;
        if (count == 0) return Array.Empty<ToolCacheItem>();

        return new[]
        {
            new ToolCacheItem
            {
                Id = "recent",
                Name = "最近浏览记录",
                Description = "存在 Config\\picture-viewer\\config.json 里；只清最近浏览记录，不动其它设置",
                Detail = $"{count} 条",
                // 清空后配置照常自动落盘；下次打开图片阅读器，「最近」列表就是空的。
                Clear = () => ToolConfigPeek.Mutate<PictureViewerConfig>(
                    PictureViewerConfig.ToolId, config => config.ClearRecent()),
            },
        };
    }
}
