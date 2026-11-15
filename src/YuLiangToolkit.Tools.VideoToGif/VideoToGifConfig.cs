using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.VideoToGif;

/// <summary>
/// 「视频转 GIF」的选项配置 → <c>Config\video-to-gif\config.json</c>。
/// 界面上每个选项（文本框 / 下拉框 / 勾选）改一下都会自动写进这个文件，下次打开自动恢复。
/// </summary>
public sealed class VideoToGifConfig : ToolConfig
{
    /// <summary>功能 Id（Data / Config 下的小文件夹名）。</summary>
    public const string ToolId = "video-to-gif";

    /// <summary>源视频文件路径（单个或批量用分号 / 换行分隔时取第一个做主图，见 ViewModel）。</summary>
    public string InputFile { get; set; } = "";

    /// <summary>输出目录（空 = 与源文件同目录）。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>起始时间（秒），0 = 从头截。</summary>
    public double StartSeconds { get; set; }

    /// <summary>截取时长（秒），0 = 一直截到结尾（不建议，GIF 会非常大）。</summary>
    public double DurationSeconds { get; set; } = 3;

    /// <summary>帧率（FPS）。</summary>
    private int _fps = 15;
    public int Fps { get => _fps; set => SetAndSave(ref _fps, value); }

    /// <summary>输出宽度（像素，偶数；0 = 保持原宽不缩放）。高度按比例自适应。</summary>
    public int Width { get; set; } = 480;

    /// <summary>调色板颜色数（GIF 最多 256）。</summary>
    private int _colors = 256;
    public int Colors { get => _colors; set => SetAndSave(ref _colors, value); }

    /// <summary>抖动算法：ffmpeg paletteuse 支持 bayer / floyd_steinberg / sierra2 / sierra2_4a 等。</summary>
    private string _dither = "floyd_steinberg";
    public string Dither { get => _dither; set => SetAndSave(ref _dither, value); }

    /// <summary>无限循环。</summary>
    private bool _loop = true;
    public bool Loop { get => _loop; set => SetAndSave(ref _loop, value); }

    /// <summary>
    /// 归一化后的「宽度」：偶数化、负数归零、限制上限。
    /// 带 setter 的计算属性必须标 <see cref="JsonIgnore"/>，否则会被写进配置文件。
    /// </summary>
    [JsonIgnore]
    public string SummaryText
    {
        get
        {
            var widthText = Width <= 0 ? "原宽" : $"{Width}px 宽";
            var loopText = Loop ? "无限循环" : "只播一次";
            return $"{Fps} FPS · {widthText} · {Colors} 色 · 抖动 {DitherLabel(Dither)} · {loopText}";
        }
    }

    /// <summary>抖动算法的中文展示名（界面下拉框 / 状态文案用）。</summary>
    public static string DitherLabel(string? dither) => dither switch
    {
        "bayer" => "Bayer（有序抖动）",
        "floyd_steinberg" => "Floyd–Steinberg",
        "sierra2" => "Sierra2",
        "sierra2_4a" => "Sierra2 4a（轻快）",
        "none" => "不抖动",
        _ => "Floyd–Steinberg",
    };
}
