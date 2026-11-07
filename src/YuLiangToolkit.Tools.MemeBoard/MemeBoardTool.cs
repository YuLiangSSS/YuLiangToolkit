using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MemeBoard.ViewModels;
using YuLiangToolkit.Tools.MemeBoard.Views;

namespace YuLiangToolkit.Tools.MemeBoard;

/// <summary>
/// 「表情包收藏夹」工具入口：实现 <see cref="ITool"/> 后会被外壳自动发现并挂到主页 / 侧边栏。
///
/// <para><b>功能定位</b>：把常用表情包图片收进一个网格；<b>单击</b>图片就把图片复制到剪贴板，
/// 自动检测并置顶 QQ 窗口、校验目标会话、聚焦聊天输入框、Ctrl+V 粘贴；设置里可以开 / 关
/// 自动发送（随机延迟）、切换「仅复制到剪贴板」模式、选择目标 QQ 会话。</para>
/// </summary>
public sealed class MemeBoardTool : ITool
{
    public string Id => "meme-board";

    public string Name => "表情包收藏夹";

    public string Description =>
        "收藏常用表情包图片，单击一张就复制到剪贴板并自动粘贴到 QQ；可开 / 关自动发送（随机延迟）、切换仅复制模式、选择目标 QQ 会话。";

    public string EmojiIcon => "😆";

    public string Category => "系统";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "表情包", "收藏", "QQ", "剪贴板", "自动粘贴" };

    public int Order => 45;

    public FrameworkElement CreateView()
        => new MemeBoardView { DataContext = new MemeBoardViewModel() };
}
