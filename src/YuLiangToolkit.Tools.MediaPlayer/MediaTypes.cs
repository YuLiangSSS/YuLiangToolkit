namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>列表里一条媒体属于哪一类：音频（只出声）还是视频（有画面）。</summary>
public enum MediaKind
{
    Audio,
    Video,
}

/// <summary>
/// 「打开文件夹时读取哪些文件」（设置项）：一并读取 / 只读音频 / 只读视频。
///
/// <para>注意这是**读取**时的过滤，不是“能不能播”：手动打开（打开文件 / 拖放）的文件同样按它过滤，
/// 否则设置就没意义了。值会按字符串存进 <c>config.json</c>（加减成员不会让老配置错位）。</para>
/// </summary>
public enum MediaIncludeMode
{
    AudioAndVideo,
    AudioOnly,
    VideoOnly,
}

/// <summary>
/// 媒体扩展名的判定与展示（**纯逻辑**，自检直接断言）。
///
/// <para>⚠ 这里只负责“按扩展名归类”，**不保证系统真能播**：WPF 的 <c>MediaElement</c> 走 Windows
/// Media Foundation，能不能解码取决于本机装了什么编解码器（flac / mkv 之类在老系统上就可能失败）。
/// 播放失败会由 <c>MediaElement.MediaFailed</c> 报到界面上（见 docs/dev/pitfalls.md）。</para>
/// </summary>
public static class MediaTypes
{
    /// <summary>识别为音频的扩展名（小写，带点）。</summary>
    public static readonly IReadOnlyList<string> AudioExtensions = new[]
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".ape", ".aiff", ".aif",
    };

    /// <summary>识别为视频的扩展名（小写，带点）。</summary>
    public static readonly IReadOnlyList<string> VideoExtensions = new[]
    {
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".flv", ".ts", ".mpg", ".mpeg", ".3gp",
    };

    private static readonly HashSet<string> AudioSet = new(AudioExtensions, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> VideoSet = new(VideoExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>按扩展名归类；不是可识别的媒体返回 <c>null</c>。</summary>
    public static MediaKind? KindOf(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return null;
        var ext = Path.GetExtension(pathOrName.Trim());
        if (ext.Length == 0) return null;
        if (AudioSet.Contains(ext)) return MediaKind.Audio;
        if (VideoSet.Contains(ext)) return MediaKind.Video;
        return null;
    }

    /// <summary>是不是可识别的媒体文件（音频或视频）。</summary>
    public static bool IsMedia(string? pathOrName) => KindOf(pathOrName) is not null;

    /// <summary>是音频吗。</summary>
    public static bool IsAudio(string? pathOrName) => KindOf(pathOrName) == MediaKind.Audio;

    /// <summary>是视频吗。</summary>
    public static bool IsVideo(string? pathOrName) => KindOf(pathOrName) == MediaKind.Video;

    /// <summary>按设置里的「读取方式」过滤：这个文件该不该进列表。</summary>
    public static bool Accept(string? pathOrName, MediaIncludeMode mode) => mode switch
    {
        MediaIncludeMode.AudioOnly => IsAudio(pathOrName),
        MediaIncludeMode.VideoOnly => IsVideo(pathOrName),
        _ => IsMedia(pathOrName),
    };

    /// <summary>「读取方式」的中文说明（下拉框标签用）。</summary>
    public static string Describe(MediaIncludeMode mode) => mode switch
    {
        MediaIncludeMode.AudioOnly => "仅音频",
        MediaIncludeMode.VideoOnly => "仅视频",
        _ => "音频 + 视频",
    };

    /// <summary>类别的中文名（列表行、状态栏用）。</summary>
    public static string KindText(MediaKind kind) => kind == MediaKind.Video ? "视频" : "音频";

    /// <summary>类别的图标（列表行前面的小标记）。</summary>
    public static string KindGlyph(MediaKind kind) => kind == MediaKind.Video ? "🎬" : "🎵";

    /// <summary>这种读取方式会收下哪些扩展名（设置页的提示文字用）。</summary>
    public static string DescribeExtensions(MediaIncludeMode mode)
    {
        IReadOnlyList<string> list = mode switch
        {
            MediaIncludeMode.AudioOnly => AudioExtensions,
            MediaIncludeMode.VideoOnly => VideoExtensions,
            _ => AudioExtensions.Concat(VideoExtensions).ToList(),
        };
        return string.Join(" ", list.Select(e => e.TrimStart('.')));
    }
}
