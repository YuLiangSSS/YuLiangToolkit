using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.AudioTools.ViewModels;
using YuLiangToolkit.Tools.AudioTools.Views;

namespace YuLiangToolkit.Tools.AudioTools;

/// <summary>
/// 「音频工具」：把原来独立的「视频转音频」和新的「分段视频合成」收在一个工具页里，
/// 顶部两个页签切换。两块都靠系统 FFmpeg，只是参数不同。
///
/// <para><b>数据兼容</b>：视频转音频仍用 <c>video-to-audio</c> 作为 ToolId，
/// 老用户的 <c>Config\video-to-audio\config.json</c> 原样生效；分段合成用 <c>video-merge</c>。</para>
/// </summary>
public class AudioToolsTool : ITool
{
    public string Id => "audio-tools";

    public string Name => "音频工具";

    public string Description
        => "FFmpeg 媒体工具二合一：① 批量视频转音频（MP3 / FLAC / 原始音轨复制）；"
           + "② 分段视频合成（m3u8 播放列表 / ts 分段文件夹 → 整段 mp4/mkv，可复制流或重新编码）";

    public string EmojiIcon => "🎵";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "视频转音频", "分段合成", "m3u8", "ts", "合并视频", "FFmpeg", "mp3", "flac", "提取音轨", "媒体",
    };

    /// <summary>越小越靠前（原来的 video-to-audio 也是 10）。</summary>
    public int Order => 10;

    public FrameworkElement CreateView()
        => new AudioToolsView { DataContext = new AudioToolsViewModel() };
}
