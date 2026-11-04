using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.ScreenRecorder.ViewModels;
using YuLiangToolkit.Tools.ScreenRecorder.Views;

namespace YuLiangToolkit.Tools.ScreenRecorder;

/// <summary>
/// 「录屏 / GIF」：用系统 FFmpeg 的 gdigrab 采集屏幕，录成 GIF（两遍调色板）或 MP4，
/// 也支持 PNG 截图、区域框选、窗口选择、倒计时、置顶控制条和 F8 全局停止热键。
/// </summary>
public class ScreenRecorderTool : ITool
{
    public string Id => "screen-recorder";

    public string Name => "录屏 / GIF";

    public string Description
        => "OBS 风格但保持简单的录屏：全屏 / 框选区域 / 窗口 → GIF 或 MP4 / MKV / MOV / AVI / WebM，"
           + "编码器 / 画质 / 码率可选；支持麦克风 + 系统声音（录制中可调音量）、摄像头预览与屏幕叠加、"
           + "可自定义全局录制 / 截图快捷键，也能一键截 PNG。";

    public string EmojiIcon => "🎥";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "录屏", "GIF", "动图", "屏幕录制", "区域录制", "窗口录制", "摄像头", "麦克风", "系统声音",
        "MP4", "AVI", "WebM", "编码器", "快捷键", "截图", "FFmpeg", "媒体",
    };

    /// <summary>音频工具是 10，录屏接在它后面。</summary>
    public int Order => 11;

    public FrameworkElement CreateView()
        => new ScreenRecorderView { DataContext = new ScreenRecorderViewModel() };
}
