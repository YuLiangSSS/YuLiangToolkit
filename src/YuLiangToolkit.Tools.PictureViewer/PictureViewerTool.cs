using System.Windows;
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
/// <para>分层与实现细节见根目录 <c>AGENTS.md</c> 的 2.19 节。</para>
/// </summary>
public class PictureViewerTool : ITool
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
}
