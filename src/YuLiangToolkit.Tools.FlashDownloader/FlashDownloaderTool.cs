using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.FlashDownloader.ViewModels;
using YuLiangToolkit.Tools.FlashDownloader.Views;

namespace YuLiangToolkit.Tools.FlashDownloader;

/// <summary>
/// 「Flash 下载器」功能入口：4399 / 7k7k / 17yy / ArmorGames / Kongregate 五个网站的小游戏下载
/// + 本地服务器游玩 + 关联文件下载。
///
/// <para>它复刻的是仓库根目录那个早期版本（Python 的 FlashGameDownloader V2.0）的功能，
/// 细节与“为什么这么做”都写在 AGENTS.md 的 2.11 节。</para>
/// </summary>
public class FlashDownloaderTool : ITool
{
    /// <summary>功能 Id：同时是 <c>Data\flash-downloader</c> 与 <c>Config\flash-downloader</c> 的文件夹名。</summary>
    public string Id => FlashDownloaderConfig.ToolId;

    public string Name => "Flash 下载器";

    public string Description
        => "从 4399 / 7k7k / 17yy / ArmorGames / Kongregate 下载小游戏，"
           + "用 Flash 独立播放器或本地服务器离线游玩；支持关联文件批量下载与自动补全缺失资源";

    public string EmojiIcon => "⚡";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "flash", "swf", "小游戏", "下载", "4399", "7k7k", "17yy", "4399小游戏",
        "ArmorGames", "Kongregate", "本地服务器", "离线玩", "h5", "unity", "网页游戏",
    };

    /// <summary>越小越靠前（视频转音频 10，批量重命名 40）。</summary>
    public int Order => 20;

    public FrameworkElement CreateView()
        => new FlashDownloaderView { DataContext = new FlashDownloaderViewModel() };
}
