using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.MediaPlayer;

/// <summary>
/// 播放列表里的一行（= 一个媒体文件）。
///
/// <para>只存「文件是谁」与几个界面状态，**不碰播放器**：真正出声的是
/// <see cref="IMediaPlayback"/> 的实现（界面里的 <c>MediaElement</c>），
/// 所以自检可以塞一个假播放器把整条流程无头跑完（见 docs/dev/pitfalls.md）。</para>
///
/// <para>⚠ 时长故意**不在这里探测**：一个文件夹几百首时，逐个 Open 一遍太慢（还会卡住界面），
/// 所以列表行不显示时长，只有当前这首在打开后才在下面显示（见 docs/dev/pitfalls.md 的说明）。</para>
/// </summary>
public sealed class MediaItem : ObservableObject
{
    /// <param name="fullPath">完整路径。</param>
    /// <param name="kind">音频 / 视频。</param>
    /// <param name="modified">
    /// 磁盘上的「修改时间」（排序用）；<c>null</c> = 没读到（文件不在 / 读不到），
    /// 排序时固定排在最后。扫描 / 恢复时才去读，别在界面线程逐个探测。
    /// </param>
    /// <param name="size">文件字节数（排序用；读不到时是 0）。</param>
    public MediaItem(string fullPath, MediaKind kind, DateTime? modified = null, long size = 0)
    {
        FullPath = fullPath;
        Kind = kind;
        FileName = Path.GetFileName(fullPath);
        Title = Path.GetFileNameWithoutExtension(fullPath);
        Folder = Path.GetDirectoryName(fullPath) ?? "";
        Modified = modified;
        Size = size;
    }

    /// <summary>完整路径（播放器要的就是它）。</summary>
    public string FullPath { get; }

    /// <summary>带扩展名的文件名。</summary>
    public string FileName { get; }

    /// <summary>去掉扩展名的名字（列表主标题）。</summary>
    public string Title { get; }

    /// <summary>所在文件夹（完整路径）。</summary>
    public string Folder { get; }

    /// <summary>音频 / 视频。</summary>
    public MediaKind Kind { get; }

    /// <summary>磁盘上的修改时间（排序用；读不到时 <c>null</c>，排序时永远排最后）。</summary>
    public DateTime? Modified { get; }

    /// <summary>文件字节数（排序用；读不到时 0）。</summary>
    public long Size { get; }

    /// <summary>
    /// 「添加顺序」序号（由 ViewModel 在条目第一次进列表时分配，此后不再变）：
    /// 排序里的「默认顺序」就按它排，所以换过别的排序也能回到最初的添加顺序。
    /// <c>-1</c> = 还没分配（自检 / 演示数据手工构造的条目）。
    /// </summary>
    public int AddedIndex { get; set; } = -1;

    /// <summary>
    /// 读一个文件的「修改时间 + 大小」给列表排序用。文件不在 / 读不到时安静地给
    /// <c>null</c> / 0，**不抛异常**（播放列表里随时可能有被删掉的条目）。
    /// </summary>
    public static (DateTime? Modified, long Size) ReadMetadata(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return (null, 0);
            return (info.LastWriteTime, info.Length);
        }
        catch
        {
            return (null, 0);
        }
    }

    /// <summary>「音频」/「视频」。</summary>
    public string KindText => MediaTypes.KindText(Kind);

    /// <summary>行首的小图标。</summary>
    public string KindGlyph => MediaTypes.KindGlyph(Kind);

    /// <summary>去重用的键（Windows 路径大小写不敏感，统一转小写）。</summary>
    public string Key => FullPath.ToLowerInvariant();

    /// <summary>悬停提示：完整路径 + 类别（列表列窄，路径放不下）。</summary>
    public string Tooltip => $"{FullPath}{Environment.NewLine}{KindText}";

    /// <summary>行号（1 起；列表增删后由 ViewModel 统一重排）。</summary>
    private int _number;
    public int Number
    {
        get => _number;
        set => Set(ref _number, value);
    }

    /// <summary>是不是正在播的那一首（列表行据此高亮 + 显示 ▶）。</summary>
    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        set => Set(ref _isPlaying, value);
    }

    /// <summary>列表里显示的一行文本（UI 自动化 / 日志里看得懂，而不是类型全名）。</summary>
    public override string ToString() => $"{Number:00}. {Title}（{KindText}）";
}
