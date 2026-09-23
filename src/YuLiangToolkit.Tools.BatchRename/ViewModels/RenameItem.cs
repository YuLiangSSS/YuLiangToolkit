using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.BatchRename.ViewModels;

/// <summary>
/// 预览列表里的一行：一个待改名的文件。
/// 「原名 / 新名 / 状态」都由 <see cref="Apply"/> 从 <see cref="RenamePlanEntry"/> 灌进来。
/// </summary>
public sealed class RenameItem : ObservableObject
{
    private string _sourcePath;
    private string _originalName;
    private string _newName = "";
    private string _message = "";
    private RenameStatus _status = RenameStatus.Unchanged;
    private bool _isChecked = true;

    public RenameItem(string sourcePath, long sizeBytes, DateTime lastWriteTime)
    {
        _sourcePath = sourcePath;
        _originalName = Path.GetFileName(sourcePath);
        SizeBytes = sizeBytes;
        LastWriteTime = lastWriteTime;
    }

    /// <summary>当前文件完整路径（改名成功后会被更新成新路径）。</summary>
    public string SourcePath
    {
        get => _sourcePath;
        private set
        {
            if (Set(ref _sourcePath, value))
            {
                OnPropertyChanged(nameof(FolderText));
                OnPropertyChanged(nameof(FullPathText));
            }
        }
    }

    /// <summary>当前文件名（改名成功后会被更新成新名字）。</summary>
    public string OriginalName
    {
        get => _originalName;
        private set => Set(ref _originalName, value);
    }

    /// <summary>按规则算出来的新文件名。</summary>
    public string NewName
    {
        get => _newName;
        set => Set(ref _newName, value);
    }

    /// <summary>状态说明（冲突/失败原因等）。</summary>
    public string Message
    {
        get => _message;
        set => Set(ref _message, value);
    }

    /// <summary>状态。</summary>
    public RenameStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(WillRename));
        }
    }

    /// <summary>是否参与本次改名（界面上每行前面的勾选框）。</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; }

    /// <summary>最后修改时间。</summary>
    public DateTime LastWriteTime { get; }

    public string SizeText => FileSize.Format(SizeBytes);

    /// <summary>所在文件夹名（列表里区分同名文件用）。</summary>
    public string FolderText => Path.GetFileName(Path.GetDirectoryName(SourcePath) ?? "") is { Length: > 0 } folder
        ? folder
        : SourcePath;

    /// <summary>鼠标悬停时显示的完整路径。</summary>
    public string FullPathText => SourcePath;

    /// <summary>名字是否真的会变（决定这一行要不要高亮）。</summary>
    public bool WillRename => Status == RenameStatus.Ok;

    /// <summary>界面上显示的状态文案。</summary>
    public string StatusText => Status switch
    {
        RenameStatus.Ok => "将改名",
        RenameStatus.Unchanged => "不变",
        RenameStatus.Skipped => "跳过",
        RenameStatus.Conflict => "冲突",
        RenameStatus.Error => "错误",
        RenameStatus.Done => "已改名",
        RenameStatus.Failed => "失败",
        RenameStatus.Undone => "已撤销",
        _ => Status.ToString(),
    };

    /// <summary>把引擎算出来的结果灌进这一行。</summary>
    public void Apply(RenamePlanEntry entry)
    {
        NewName = entry.NewName;
        Message = entry.Message;
        Status = entry.Status;
    }

    /// <summary>改名成功后更新自身路径信息（这样再点「执行」不会拿旧路径去改）。</summary>
    public void MarkRenamed(string targetPath)
    {
        SourcePath = targetPath;
        OriginalName = Path.GetFileName(targetPath);
        NewName = OriginalName;
        Message = "已改名";
        Status = RenameStatus.Done;
    }

    /// <summary>撤销后恢复成原来的路径信息。</summary>
    public void MarkUndone(string originalPath)
    {
        SourcePath = originalPath;
        OriginalName = Path.GetFileName(originalPath);
        NewName = OriginalName;
        Message = "已撤销";
        Status = RenameStatus.Undone;
    }
}
