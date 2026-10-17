using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MapDownloader.ViewModels;
using YuLiangToolkit.Tools.MapDownloader.Views;

namespace YuLiangToolkit.Tools.MapDownloader;

/// <summary>
/// 「osu! 下图器」——从独立的小工具 MapDownloader 移植过来（原版是 WinForms，
/// 界面在本工具集里重写成 WPF + MVVM，网络与筛选逻辑照搬）。
///
/// <para>细节（为什么这么分层、踩过哪些坑）见根目录 <c>AGENTS.md</c> 的 2.13 节。</para>
/// </summary>
public class MapDownloaderTool : ITool
{
    public string Id => MapDownloaderConfig.ToolId;

    public string Name => "osu! 下图器";

    public string Description => "搜 osu! 谱面集（模式 / 状态 / 排序 / 日期 / Key 数都能筛），选中或全量下载 .osz（可带视频）";

    public string EmojiIcon => "🎧";

    public string Category => "游戏";

    public IReadOnlyList<string> Tags => new[]
    {
        "osu", "osu!", "谱面", "beatmap", "下图", "下图器", "osz", "下载", "搜索", "mania", "音游",
    };

    public int Order => 50;

    public FrameworkElement CreateView() => new MapDownloaderView { DataContext = new MapDownloaderViewModel() };
}
