using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.AudioTools;

/// <summary>分段视频合成的输入方式。</summary>
public enum SegmentSourceKind
{
    /// <summary>m3u8 播放列表（本地文件或 http(s) 链接）。</summary>
    Playlist,

    /// <summary>一个装着 .ts / .m4s 等分段的文件夹。</summary>
    Folder,
}

/// <summary>合成输出的容器。</summary>
public enum MergeOutputFormat
{
    /// <summary>MP4（最通用）。</summary>
    Mp4,

    /// <summary>MKV（对编码更宽容，复制流失败时可试）。</summary>
    Mkv,
}

/// <summary>
/// 「分段视频合成」的配置 → <c>Config\video-merge\config.json</c>。
/// 和视频转音频的配置互不影响。
/// </summary>
public sealed class SegmentMergeConfig : ToolConfig
{
    /// <summary>功能 Id（Config / Cache 下的文件夹名）。</summary>
    public const string ToolId = "video-merge";

    private SegmentSourceKind _sourceKind = SegmentSourceKind.Playlist;
    private MergeOutputFormat _outputFormat = MergeOutputFormat.Mp4;
    private bool _copyStreams = true;

    /// <summary>输入方式。</summary>
    public SegmentSourceKind SourceKind
    {
        get => _sourceKind;
        set
        {
            if (!Set(ref _sourceKind, value)) return;
            OnPropertyChanged(nameof(IsPlaylistMode));
            OnPropertyChanged(nameof(IsFolderMode));
        }
    }

    /// <summary>m3u8 地址 / 本地文件路径，或分段文件夹路径。</summary>
    public string SourcePath { get; set; } = "";

    /// <summary>输出文件夹（为空时按输入位置 / 系统下载目录自动推断）。</summary>
    public string OutputFolder { get; set; } = "";

    /// <summary>输出文件名（可带扩展名；不填自动按来源起名）。</summary>
    public string OutputName { get; set; } = "";

    /// <summary>输出容器。</summary>
    public MergeOutputFormat OutputFormat
    {
        get => _outputFormat;
        set
        {
            if (!Set(ref _outputFormat, value)) return;
            OnPropertyChanged(nameof(IsMp4Output));
            OnPropertyChanged(nameof(IsMkvOutput));
        }
    }

    /// <summary>true = 直接复制音视频流（快、无损，要求分段编码兼容）；false = 重新编码。</summary>
    public bool CopyStreams
    {
        get => _copyStreams;
        set
        {
            if (!Set(ref _copyStreams, value)) return;
            OnPropertyChanged(nameof(IsCopyMode));
            OnPropertyChanged(nameof(IsReencodeMode));
        }
    }

    // 单选按钮用的布尔包装（带 setter 的计算属性必须 [JsonIgnore]）
    [JsonIgnore]
    public bool IsPlaylistMode
    {
        get => SourceKind == SegmentSourceKind.Playlist;
        set { if (value) SourceKind = SegmentSourceKind.Playlist; }
    }

    [JsonIgnore]
    public bool IsFolderMode
    {
        get => SourceKind == SegmentSourceKind.Folder;
        set { if (value) SourceKind = SegmentSourceKind.Folder; }
    }

    [JsonIgnore]
    public bool IsMp4Output
    {
        get => OutputFormat == MergeOutputFormat.Mp4;
        set { if (value) OutputFormat = MergeOutputFormat.Mp4; }
    }

    [JsonIgnore]
    public bool IsMkvOutput
    {
        get => OutputFormat == MergeOutputFormat.Mkv;
        set { if (value) OutputFormat = MergeOutputFormat.Mkv; }
    }

    [JsonIgnore]
    public bool IsReencodeMode
    {
        get => !CopyStreams;
        set { if (value) CopyStreams = false; }
    }

    [JsonIgnore]
    public bool IsCopyMode
    {
        get => CopyStreams;
        set { if (value) CopyStreams = true; }
    }
}
