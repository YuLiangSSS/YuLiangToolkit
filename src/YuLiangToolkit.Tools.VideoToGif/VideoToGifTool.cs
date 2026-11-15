using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.VideoToGif.ViewModels;
using YuLiangToolkit.Tools.VideoToGif.Views;

namespace YuLiangToolkit.Tools.VideoToGif;

/// <summary>
/// 「视频转 GIF」：把一个视频文件截取一段转成 GIF 动图（FFmpeg 两遍调色板编码）。
/// 和「录屏 / GIF」（录屏 → GIF）、「剪辑工具」（裁剪 + 转 GIF）是不同入口：
/// 本工具专注「已有视频文件 → GIF」，可精确设起始时间 / 时长 / 帧率 / 宽度 / 颜色 / 抖动。
/// </summary>
public class VideoToGifTool : ITool
{
    public string Id => "video-to-gif";

    public string Name => "视频转 GIF";

    public string Description
        => "把视频文件截取一段转成 GIF 动图：可设起始时间 / 时长 / 帧率 / 宽度 / 颜色数 / 抖动算法，"
           + "FFmpeg 两遍调色板编码（palettegen → paletteuse），颜色准、体积小";

    public string EmojiIcon => "🖼️";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "视频转GIF", "转GIF", "动图", "GIF", "视频", "截图动图", "FFmpeg", "调色板", "媒体",
    };

    /// <summary>排在「音频工具」后面（音频工具是 10）。</summary>
    public int Order => 11;

    public FrameworkElement CreateView()
        => new VideoToGifView { DataContext = new VideoToGifViewModel() };
}
