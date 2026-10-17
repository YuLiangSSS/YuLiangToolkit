using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MediaPlayer.ViewModels;
using YuLiangToolkit.Tools.MediaPlayer.Views;

namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 「视频 / 音乐播放器」——左中右三栏：中间播放（视频 / 封面 + 进度条 + 播放控制），右边播放列表。
///
/// <para>能力：打开文件夹 / 文件 / 拖放（可按音频 / 视频 / 一并过滤、可递归、可选择追加还是覆盖）、
/// 列表循环 / 单曲循环 / 随机播放、可调步长的快进快退（**左键快进快退、右键上一首下一首**）、
/// 音量、记住上次的列表与位置。</para>
///
/// <para>分层与踩过的坑见根目录 <c>AGENTS.md</c> 的 2.14 节。</para>
/// </summary>
public class MediaPlayerTool : ITool
{
    public string Id => MediaPlayerConfig.ToolId;

    public string Name => "视频 / 音乐播放器";

    public string Description => "本地播放器：打开文件夹或文件就能听歌看片，支持列表 / 单曲 / 随机、可调步长的快进快退、音量与上次进度恢复";

    public string EmojiIcon => "🎬";

    public string Category => "媒体";

    public IReadOnlyList<string> Tags => new[]
    {
        "播放器", "播放", "音乐", "视频", "音频", "mp3", "flac", "mp4", "MKV",
        "播放列表", "随机播放", "单曲循环", "列表循环", "快进", "快退", "音量", "本地",
    };

    public int Order => 15;

    public FrameworkElement CreateView() => new MediaPlayerView { DataContext = new MediaPlayerViewModel() };
}
