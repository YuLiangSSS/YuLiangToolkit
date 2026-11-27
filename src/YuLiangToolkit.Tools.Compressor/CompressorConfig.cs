using System.IO;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.Compressor;

/// <summary>「压缩器」的持久化设置。</summary>
public sealed class CompressorConfig : ToolConfig
{
    public const string ToolId = "compressor";
    public const string DefaultSuffix = "_压缩";

    private string _outputFolder = "";
    private string _fileSuffix = DefaultSuffix;
    private string _preset = CompressorProfiles.Standard;
    private string _imageFormat = "jpg";
    private string _audioFormat = "mp3";
    private string _videoFormat = "mp4";
    private bool _overwriteExisting;
    private bool _openFolderAfter = true;
    private string _lastInputFolder = "";

    /// <summary>输出目录；空 = 桌面\YuLiangToolkit 压缩输出。</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set => SetAndSave(ref _outputFolder, value ?? "");
    }

    /// <summary>输出文件名后缀。</summary>
    public string FileSuffix
    {
        get => _fileSuffix;
        set => SetAndSave(ref _fileSuffix, string.IsNullOrWhiteSpace(value) ? DefaultSuffix : value);
    }

    /// <summary>压缩程度预设：light / standard / extreme。</summary>
    public string Preset
    {
        get => _preset;
        set => SetAndSave(ref _preset, CompressorProfiles.Get(value).Key);
    }

    /// <summary>图片输出格式：jpg / png。</summary>
    public string ImageFormat
    {
        get => _imageFormat;
        set => SetAndSave(ref _imageFormat, value is "png" ? "png" : "jpg");
    }

    /// <summary>音频输出格式：mp3 / m4a / ogg。</summary>
    public string AudioFormat
    {
        get => _audioFormat;
        set => SetAndSave(ref _audioFormat, value is "m4a" or "ogg" ? value : "mp3");
    }

    /// <summary>视频输出格式（当前固定 MP4 / H.264）。</summary>
    public string VideoFormat
    {
        get => _videoFormat;
        set => SetAndSave(ref _videoFormat, "mp4");
    }

    /// <summary>同名输出是否覆盖；默认自动避让。</summary>
    public bool OverwriteExisting
    {
        get => _overwriteExisting;
        set => SetAndSave(ref _overwriteExisting, value);
    }

    /// <summary>完成后打开输出目录。</summary>
    public bool OpenFolderAfter
    {
        get => _openFolderAfter;
        set => SetAndSave(ref _openFolderAfter, value);
    }

    public string LastInputFolder
    {
        get => _lastInputFolder;
        set => SetAndSave(ref _lastInputFolder, value ?? "");
    }

    [JsonIgnore]
    public string EffectiveOutputFolder
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(OutputFolder)) return OutputFolder.Trim();
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(desktop, "YuLiangToolkit 压缩输出");
        }
    }
}
