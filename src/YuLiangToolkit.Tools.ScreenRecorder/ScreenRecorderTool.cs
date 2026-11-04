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
        => "用 FFmpeg 录屏：框选区域 / 全屏 / 某个窗口 → 录成 GIF（两遍调色板，颜色准）或 MP4；"
           + "支持倒计时、置顶控制条、F8 停止、最长录制时长，也能一键截 PNG。";

    public string EmojiIcon => "🎥";

    public string Category => "媒体";

    /// <summary>主页标签（卡片上显示成蓝色小字，也能被搜索命中）。</summary>
    public IReadOnlyList<string> Tags => new[]
    {
        "录屏", "GIF", "动图", "屏幕录制", "区域录制", "窗口录制", "截图", "MP4", "FFmpeg", "媒体",
    };

    /// <summary>音频工具是 10，录屏接在它后面。</summary>
    public int Order => 11;

    public FrameworkElement CreateView()
        => new ScreenRecorderView { DataContext = new ScreenRecorderViewModel() };
}
