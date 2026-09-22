using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.VideoToAudio;

/// <summary>转换模式。</summary>
public enum ConvertMode
{
    /// <summary>有损压缩为 MP3。</summary>
    Mp3Lossy,

    /// <summary>无损转换为 FLAC。</summary>
    FlacLossless,

    /// <summary>直接复制原始音轨（不重新编码）。</summary>
    CopyOriginalAudio,
}

/// <summary>
/// 「视频转音频」的选项配置 → <c>Config\video-to-audio\config.json</c>。
/// 界面上每个选项（Checkbox / Textbox / 下拉框）改一下都会自动写进这个文件，下次打开自动恢复。
/// </summary>
public sealed class VideoToAudioConfig : ToolConfig
{
    /// <summary>源文件夹。</summary>
    public string InputFolder { get; set; } = "";

    /// <summary>输出目录（<see cref="UseSourceFolder"/> 为 true 时等于源文件夹）。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>输出到源文件夹（勾选时忽略 <see cref="OutputFolder"/>）。</summary>
    public bool UseSourceFolder { get; set; } = true;

    /// <summary>是否递归扫描子文件夹。</summary>
    public bool IncludeSubfolders { get; set; } = true;

    /// <summary>是否只扫描 .mp4（不勾选则含 mkv/mov/webm 等）。</summary>
    public bool OnlyMp4 { get; set; } = true;

    /// <summary>MP3 码率（kbps），如 "192"。</summary>
    public string Bitrate { get; set; } = "192";

    /// <summary>并行转换数：0 = 自动（按 CPU 核心数），1 = 串行。</summary>
    public int Parallelism { get; set; }

    /// <summary>FLAC 压缩级别 0-8：越低越快、文件略大；默认 5。</summary>
    public int FlacCompressionLevel { get; set; } = 5;

    private ConvertMode _mode = ConvertMode.Mp3Lossy;

    /// <summary>转换模式。用 <c>Set</c> 是为了发出通知，让三个单选按钮随配置刷新。</summary>
    public ConvertMode Mode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsMp3Mode));
            OnPropertyChanged(nameof(IsFlacMode));
            OnPropertyChanged(nameof(IsCopyMode));
        }
    }

    // 下面三个是给单选按钮用的布尔包装（带 setter 的计算属性必须标 [JsonIgnore]，否则会被写进配置文件）
    [JsonIgnore]
    public bool IsMp3Mode
    {
        get => Mode == ConvertMode.Mp3Lossy;
        set { if (value) Mode = ConvertMode.Mp3Lossy; }
    }

    [JsonIgnore]
    public bool IsFlacMode
    {
        get => Mode == ConvertMode.FlacLossless;
        set { if (value) Mode = ConvertMode.FlacLossless; }
    }

    [JsonIgnore]
    public bool IsCopyMode
    {
        get => Mode == ConvertMode.CopyOriginalAudio;
        set { if (value) Mode = ConvertMode.CopyOriginalAudio; }
    }

    /// <summary>
    /// 从老版本的全剧设置（<c>%AppData%\YuLiangToolkit\settings.json</c> 里的 Last* 字段）
    /// 迁移选项，只在首次运行（配置文件还不存在）时使用，老用户不会丢设置。
    /// </summary>
    public static VideoToAudioConfig FromLegacySettings()
    {
        var legacy = JsonFileStore.Read<LegacyAppSettings>(AppSettingsService.SettingsFilePath);
        if (legacy is null) return new VideoToAudioConfig();

        var config = new VideoToAudioConfig
        {
            InputFolder = legacy.LastInputFolder ?? "",
            OutputFolder = legacy.LastOutputFolder ?? "",
            UseSourceFolder = legacy.LastUseSourceFolder,
            IncludeSubfolders = legacy.LastIncludeSubfolders,
            OnlyMp4 = legacy.LastOnlyMp4,
            Bitrate = legacy.LastBitrate,
            Parallelism = legacy.Parallelism,
            FlacCompressionLevel = legacy.FlacCompressionLevel,
        };
        if (Enum.TryParse<ConvertMode>(legacy.LastMode, out var mode)) config.Mode = mode;
        return config;
    }
}
