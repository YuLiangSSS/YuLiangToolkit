using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.MediaEditor;

/// <summary>
/// 「剪辑工具」配置 → <c>Config\media-editor\config.json</c>。
///
/// <para>只记录“用户偏好”（上次打开的文件、导出格式 / 编码器 / 画质 / 输出目录），
/// 不记录具体的片段列表：片段是这一次编辑的临时状态，重新打开文件会回到整段。</para>
/// </summary>
public sealed class MediaEditorConfig : ToolConfig
{
    /// <summary>工具 Id（Config / Cache 下的小文件夹名）。</summary>
    public const string ToolId = "media-editor";

    /// <summary>默认文件名前缀。</summary>
    public const string DefaultFilePrefix = "剪辑";

    private string _lastInputPath = "";

    /// <summary>上次打开的文件（启动时若文件还在会自动重新载入，方便接着剪）。</summary>
    public string LastInputPath
    {
        get => _lastInputPath;
        set => SetAndSave(ref _lastInputPath, value);
    }

    private string _format = "mp4";

    /// <summary>导出格式：<c>gif</c> 或视频容器（mp4 / mkv / mov / avi / webm）。</summary>
    public string Format
    {
        get => _format;
        set => SetAndSave(ref _format, value);
    }

    private string _videoCodec = "libx264";

    /// <summary>视频编码器 Id（见录屏工程的 <c>VideoCatalog</c>）。</summary>
    public string VideoCodec
    {
        get => _videoCodec;
        set => SetAndSave(ref _videoCodec, value);
    }

    private string _rateMode = "quality";

    /// <summary>画质优先 / 码率优先。</summary>
    public string RateMode
    {
        get => _rateMode;
        set => SetAndSave(ref _rateMode, value);
    }

    private int _quality = 23;

    /// <summary>画质优先模式下的 CRF / CQ（0~51）。</summary>
    public int Quality
    {
        get => _quality;
        set => SetAndSave(ref _quality, value);
    }

    private int _bitrateMbps = 8;

    /// <summary>码率优先模式下的目标码率（Mbps，1~100）。</summary>
    public int BitrateMbps
    {
        get => _bitrateMbps;
        set => SetAndSave(ref _bitrateMbps, value);
    }

    private int _fps;

    /// <summary>输出帧率（0 = 保持源；可选 15 / 24 / 30 / 60）。</summary>
    public int Fps
    {
        get => _fps;
        set => SetAndSave(ref _fps, value);
    }

    private int _scaleWidth;

    /// <summary>输出宽度（0 = 原始；只缩小不放大）。</summary>
    public int ScaleWidth
    {
        get => _scaleWidth;
        set => SetAndSave(ref _scaleWidth, value);
    }

    private int _gifColors = 256;

    /// <summary>GIF 调色板颜色数（64 / 128 / 256）。</summary>
    public int GifColors
    {
        get => _gifColors;
        set => SetAndSave(ref _gifColors, value);
    }

    private string _gifDither = "floyd_steinberg";

    /// <summary>GIF 抖动：floyd_steinberg / bayer / none。</summary>
    public string GifDither
    {
        get => _gifDither;
        set => SetAndSave(ref _gifDither, value);
    }

    private bool _gifLoop = true;

    /// <summary>GIF 是否无限循环。</summary>
    public bool GifLoop
    {
        get => _gifLoop;
        set => SetAndSave(ref _gifLoop, value);
    }

    private bool _keepAudio = true;

    /// <summary>视频导出时保留原音频（GIF 无音频）。</summary>
    public bool KeepAudio
    {
        get => _keepAudio;
        set => SetAndSave(ref _keepAudio, value);
    }

    private int _audioBitrateKbps = 192;

    /// <summary>音频码率（128 / 192 / 256 kbps）。</summary>
    public int AudioBitrateKbps
    {
        get => _audioBitrateKbps;
        set => SetAndSave(ref _audioBitrateKbps, value);
    }

    private string _outputFolder = "";

    /// <summary>输出文件夹（空 = 「视频\YuLiangToolkit 剪辑」）。</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set => SetAndSave(ref _outputFolder, value);
    }

    private bool _openFolderAfterExport = true;

    /// <summary>导出成功后自动打开输出文件夹。</summary>
    public bool OpenFolderAfterExport
    {
        get => _openFolderAfterExport;
        set => SetAndSave(ref _openFolderAfterExport, value);
    }

    private string _filePrefix = DefaultFilePrefix;

    /// <summary>输出文件名前缀。</summary>
    public string FilePrefix
    {
        get => _filePrefix;
        set => SetAndSave(ref _filePrefix, value);
    }

    private int _thumbnailCount = 12;

    /// <summary>时间轴缩略图数量（4~24）。</summary>
    public int ThumbnailCount
    {
        get => _thumbnailCount;
        set => SetAndSave(ref _thumbnailCount, value);
    }

    // ---------- 只读计算属性 ----------

    /// <summary>导出的是不是 GIF。</summary>
    [JsonIgnore]
    public bool IsGif => string.Equals(Format, "gif", StringComparison.OrdinalIgnoreCase);

    /// <summary>导出的是不是视频容器。</summary>
    [JsonIgnore]
    public bool IsVideo => !IsGif;
}
