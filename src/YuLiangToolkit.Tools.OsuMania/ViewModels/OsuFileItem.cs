using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.OsuMania.Core;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>文件列表里一行的状态（决定状态文字 / 颜色）。</summary>
public enum OsuFileState
{
    /// <summary>刚加入列表，还没读谱面。</summary>
    Pending,

    /// <summary>已确认能读，等待处理。</summary>
    Ready,

    /// <summary>已成功转换并写出新文件。</summary>
    Converted,

    /// <summary>被跳过（键数不满足 / 已转换过 / 用户取消）。</summary>
    Skipped,

    /// <summary>读不了 / 写不了 / 转换抛异常。</summary>
    Failed,
}

/// <summary>
/// 三个 osu 工具共用的「文件列表行」：文件名 + 完整路径 + 源键数 + 状态。
///
/// <para>只放界面状态，不碰算法；键数 / 失败原因由 ViewModel 串行 <c>OsuBeatmapFile.TryLoad</c> 后填进来
/// （OsuParsers 的解码器是 static 状态、非线程安全，**绝不能并发读**）。</para>
/// </summary>
public sealed class OsuFileItem : ObservableObject
{
    public OsuFileItem(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        Folder = Path.GetDirectoryName(filePath) ?? "";
    }

    /// <summary>完整路径（去重、执行、日志都用它）。</summary>
    public string FilePath { get; }

    /// <summary>文件名（列表第一列）。</summary>
    public string FileName { get; }

    /// <summary>所在文件夹（「打开输出文件夹」在还没转换过时兜底用它）。</summary>
    public string Folder { get; }

    private int _keyCount;
    /// <summary>源谱面键数；0 = 还没读到 / 读不了。</summary>
    public int KeyCount
    {
        get => _keyCount;
        set
        {
            if (Set(ref _keyCount, value)) OnPropertyChanged(nameof(KeyCountText));
        }
    }

    /// <summary>列表里显示的键数（"4K" / "—"）。</summary>
    public string KeyCountText => KeyCount is >= OsuManiaBeatmap.MinKeyCount and <= OsuManiaBeatmap.MaxKeyCount
        ? $"{KeyCount}K"
        : "—";

    private string _statusText = "待检测";
    /// <summary>状态文字（就绪 / 已转换 / 跳过 / 失败…）。</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    private string? _error;
    /// <summary>失败原因（状态列的 ToolTip 用）。</summary>
    public string? Error
    {
        get => _error;
        set => Set(ref _error, value);
    }

    private OsuFileState _state = OsuFileState.Pending;
    /// <summary>行状态（XAML 用触发器上色）。</summary>
    public OsuFileState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsConverted));
            }
        }
    }

    /// <summary>失败行标红。</summary>
    public bool IsFailed => State == OsuFileState.Failed;

    /// <summary>转换成功的行变暗一点（与视频转音频列表的约定一致）。</summary>
    public bool IsConverted => State == OsuFileState.Converted;

    /// <summary>状态列 ToolTip：优先显示失败原因。</summary>
    public string StatusTooltip => Error ?? StatusText;
}
