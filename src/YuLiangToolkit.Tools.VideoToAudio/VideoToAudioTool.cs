using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.VideoToAudio.ViewModels;
using YuLiangToolkit.Tools.VideoToAudio.Views;

namespace YuLiangToolkit.Tools.VideoToAudio;

/// <summary>
/// 第一个工具：视频转音频。
/// 支持三种模式：MP3 有损压缩（可选码率）、FLAC 无损转换、原始音轨无损复制（不重新编码）。
/// 后端使用 FFmpeg。
/// </summary>
public class VideoToAudioTool : ITool
{
    public string Id => "video-to-audio";
    public string Name => "视频转音频";
    public string Description => "批量提取文件夹内视频的音频：MP3 有损压缩（可选码率）、FLAC 无损、或直接复制原始音轨";
    public string EmojiIcon => "🎵";
    public string Category => "媒体";

    /// <summary>主页标签：卡片上显示成蓝色小字，点主页的标签即可快速筛选。</summary>
    public IReadOnlyList<string> Tags => new[] { "视频", "音频", "批量转换", "FFmpeg", "mp3", "flac", "提取音轨" };

    public int Order => 10;

    public FrameworkElement CreateView()
        => new VideoToAudioView { DataContext = new VideoToAudioViewModel() };
}
