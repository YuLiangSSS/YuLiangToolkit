using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Compressor.ViewModels;
using YuLiangToolkit.Tools.Compressor.Views;

namespace YuLiangToolkit.Tools.Compressor;

/// <summary>
/// 「压缩器」：拖入图片 / 音频 / 视频，选择压缩程度后批量输出更小的成品。
/// 图片走 WPF 编码，音频 / 视频走系统 FFmpeg。
/// </summary>
public sealed class CompressorTool : ITool
{
    private readonly CompressorViewModel _viewModel = new();

    public string Id => CompressorConfig.ToolId;
    public string Name => "压缩器";
    public string Description =>
        "拖入图片 / 音频 / 视频即可批量压缩：可调轻度 / 标准 / 极限三档压缩程度，"
        + "图片输出 JPG / PNG，音频输出 MP3 / M4A / OGG，视频输出 MP4（H.264）；支持批量拖入、进度、取消和打开输出目录。";
    public string EmojiIcon => "🗜️";
    public string Category => "文件";
    public IReadOnlyList<string> CategoryPath => new[] { "文件" };
    public IReadOnlyList<string> Tags => new[]
    {
        "压缩", "图片压缩", "音频压缩", "视频压缩", "批量压缩", "FFmpeg", "有损", "无损", "文件",
    };
    public int Order => 15;

    public FrameworkElement CreateView() => new CompressorView { DataContext = _viewModel };
}
