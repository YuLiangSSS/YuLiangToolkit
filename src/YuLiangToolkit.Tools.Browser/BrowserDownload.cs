using System.Text.Json;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 下载记录（<c>Data\browser\downloads.json</c>，只存已经结束的下载）。
/// </summary>
public sealed class BrowserDownloadRecord
{
    /// <summary>下载地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>文件名（显示用）。</summary>
    public string FileName { get; set; } = "";

    /// <summary>保存到磁盘的完整路径（可能已被用户移动 / 删除）。</summary>
    public string FilePath { get; set; } = "";

    /// <summary>文件总字节数；未知为 0。</summary>
    public long TotalBytes { get; set; }

    /// <summary>是否成功完成（false = 中断 / 取消 / 失败）。</summary>
    public bool Completed { get; set; }

    /// <summary>中断原因文本（<see cref="Completed"/> = false 时显示）。</summary>
    public string InterruptText { get; set; } = "";

    /// <summary>结束时间。</summary>
    public DateTime FinishedAt { get; set; }
}

/// <summary>
/// 下载列表 / 下载记录的规则（纯逻辑，自检直接断言）：进度文本、历史记录清洗 / 封顶 / 去重。
/// 真正的下载由 WebView2 的 <c>DownloadStarting</c> + <c>CoreWebView2DownloadOperation</c> 驱动（见 BrowserView）。
/// </summary>
public static class BrowserDownloads
{
    /// <summary>最多保留多少条历史下载记录。</summary>
    public const int MaxRecords = 100;

    /// <summary>下载记录文件名（<c>Data\browser\downloads.json</c>）。</summary>
    public const string FileName = "downloads.json";

    /// <summary>下载进度文本，例如 <c>1.2 MB / 3.4 MB</c>；总量未知时只显示已下载大小。</summary>
    public static string FormatProgress(long receivedBytes, ulong? totalBytes)
    {
        var received = Math.Max(0, receivedBytes);
        if (totalBytes is > 0)
            return $"{FileSize.Format(received)} / {FileSize.Format((long)Math.Min(totalBytes.Value, (ulong)long.MaxValue))}";
        return received > 0 ? $"已下载 {FileSize.Format(received)}" : "等待数据…";
    }

    /// <summary>把一条下载记录插到最前面：同一文件路径的旧记录先去掉，然后封顶。</summary>
    public static List<BrowserDownloadRecord> AddRecord(
        List<BrowserDownloadRecord> records, BrowserDownloadRecord record, int max = MaxRecords)
    {
        var result = records ?? new List<BrowserDownloadRecord>();
        if (!string.IsNullOrWhiteSpace(record.FilePath))
        {
            var path = record.FilePath;
            result.RemoveAll(r => string.Equals(r.FilePath, path, StringComparison.OrdinalIgnoreCase));
        }
        result.Insert(0, record);
        if (result.Count > Math.Max(1, max)) result.RemoveRange(max, result.Count - max);
        return result;
    }

    /// <summary>清洗从磁盘读出的下载记录：去掉没有文件路径的条目、封顶。</summary>
    public static List<BrowserDownloadRecord> Normalize(IEnumerable<BrowserDownloadRecord?>? raw, int max = MaxRecords)
    {
        var result = new List<BrowserDownloadRecord>();
        if (raw is null) return result;
        foreach (var record in raw)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.FilePath)) continue;
            result.Add(new BrowserDownloadRecord
            {
                Url = record.Url ?? "",
                FileName = string.IsNullOrWhiteSpace(record.FileName)
                    ? Path.GetFileName(record.FilePath)
                    : record.FileName,
                FilePath = record.FilePath,
                TotalBytes = Math.Max(0, record.TotalBytes),
                Completed = record.Completed,
                InterruptText = record.InterruptText ?? "",
                FinishedAt = record.FinishedAt,
            });
            if (result.Count >= Math.Max(1, max)) break;
        }
        return result;
    }
}

/// <summary>
/// 下载面板里的一项（进行中的下载 / 历史记录共用）。
/// View 在 WebView2 的下载事件里调 <see cref="UpdateProgress"/> / <see cref="MarkCompleted"/> /
/// <see cref="MarkInterrupted"/>；<see cref="CancelAction"/> 由 View 注入（取消进行中的下载）。
/// </summary>
public sealed class BrowserDownloadItem : ObservableObject
{
    private string _fileName = "";
    private string _filePath = "";
    private long _receivedBytes;
    private ulong? _totalBytes;
    private double _progress;
    private string _progressText = "";
    private string _stateText = "正在下载…";
    private bool _isActive = true;
    private bool _isCompleted;
    private bool _isInterrupted;

    /// <summary>下载地址。</summary>
    public string Url { get; }

    /// <summary>文件名。</summary>
    public string FileName
    {
        get => _fileName;
        private set => Set(ref _fileName, value);
    }

    /// <summary>保存路径。</summary>
    public string FilePath
    {
        get => _filePath;
        private set => Set(ref _filePath, value);
    }

    /// <summary>已下载字节数。</summary>
    public long ReceivedBytes
    {
        get => _receivedBytes;
        private set => Set(ref _receivedBytes, value);
    }

    /// <summary>总字节数（未知为 null / 0）。</summary>
    public ulong? TotalBytes
    {
        get => _totalBytes;
        private set => Set(ref _totalBytes, value);
    }

    /// <summary>进度 0~1（总量未知时保持 0，进度条走 indeterminate）。</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, Math.Clamp(value, 0, 1));
    }

    /// <summary>进度文本（已下载 / 总量）。</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>状态文本：正在下载… / 已完成 / 已中断（原因）。</summary>
    public string StateText
    {
        get => _stateText;
        private set => Set(ref _stateText, value);
    }

    /// <summary>是否还在下载（进行中才显示进度条和取消按钮）。</summary>
    public bool IsActive
    {
        get => _isActive;
        private set
        {
            if (!Set(ref _isActive, value)) return;
            OnPropertyChanged(nameof(ShowProgress));
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    /// <summary>是否成功完成。</summary>
    public bool IsCompleted
    {
        get => _isCompleted;
        private set => Set(ref _isCompleted, value);
    }

    /// <summary>是否中断 / 取消 / 失败。</summary>
    public bool IsInterrupted
    {
        get => _isInterrupted;
        private set => Set(ref _isInterrupted, value);
    }

    /// <summary>是否显示进度条（进行中的下载才显示）。</summary>
    public bool ShowProgress => _isActive;

    /// <summary>进度条是否 indeterminate（总量未知）。</summary>
    public bool IsIndeterminate => _isActive && (_totalBytes is null or 0);

    /// <summary>打开文件按钮是否可用（历史记录可能已被用户移走，命令里再检查一次）。</summary>
    public bool HasFilePath => !string.IsNullOrWhiteSpace(_filePath);

    /// <summary>开始时间（界面按时间倒序由 ViewModel 维护）。</summary>
    public DateTime StartedAt { get; private set; }

    /// <summary>取消进行中下载的动作（由 View 注入；历史记录为 null）。</summary>
    [JsonIgnore]
    public Action? CancelAction { get; set; }

    /// <summary>新建一个「正在下载」的项。</summary>
    public BrowserDownloadItem(string url, string? filePath, DateTime startedAt)
    {
        Url = url ?? "";
        StartedAt = startedAt;
        SetFile(filePath);
        StateText = "正在下载…";
        UpdateProgress(0, null);
    }

    /// <summary>从磁盘里的历史记录还原一项（已结束状态）。</summary>
    public BrowserDownloadItem(BrowserDownloadRecord record)
    {
        Url = record.Url ?? "";
        StartedAt = record.FinishedAt;
        SetFile(record.FilePath);
        _totalBytes = record.TotalBytes > 0 ? (ulong)record.TotalBytes : null;
        _receivedBytes = record.TotalBytes;
        _progress = record.Completed ? 1 : 0;
        _progressText = record.TotalBytes > 0 ? FileSize.Format(record.TotalBytes) : "";
        _isActive = false;
        _isCompleted = record.Completed;
        _isInterrupted = !record.Completed;
        _stateText = record.Completed
            ? "已完成"
            : string.IsNullOrWhiteSpace(record.InterruptText) ? "已中断" : $"已中断：{record.InterruptText}";
    }

    /// <summary>更新进度（进行中）。</summary>
    public void UpdateProgress(long receivedBytes, ulong? totalBytes)
    {
        ReceivedBytes = Math.Max(0, receivedBytes);
        TotalBytes = totalBytes is > 0 ? totalBytes : null;
        Progress = TotalBytes is > 0 ? (double)ReceivedBytes / TotalBytes.Value : 0;
        ProgressText = BrowserDownloads.FormatProgress(ReceivedBytes, TotalBytes);
    }

    /// <summary>标成成功完成（WebView2 的 Completed 状态）。</summary>
    public void MarkCompleted(string? filePath)
    {
        if (!string.IsNullOrWhiteSpace(filePath)) SetFile(filePath);
        IsCompleted = true;
        IsInterrupted = false;
        IsActive = false;
        Progress = 1;
        if (ReceivedBytes > 0) ProgressText = FileSize.Format(ReceivedBytes);
        StateText = "已完成";
    }

    /// <summary>标成中断 / 取消 / 失败。</summary>
    public void MarkInterrupted(string? reason)
    {
        IsInterrupted = true;
        IsCompleted = false;
        IsActive = false;
        StateText = string.IsNullOrWhiteSpace(reason) ? "已中断" : $"已中断：{reason}";
    }

    /// <summary>转成要落盘的记录。</summary>
    public BrowserDownloadRecord ToRecord()
        => new()
        {
            Url = Url,
            FileName = FileName,
            FilePath = FilePath,
            TotalBytes = TotalBytes is > 0 ? (long)Math.Min(TotalBytes.Value, (ulong)long.MaxValue) : ReceivedBytes,
            Completed = IsCompleted,
            InterruptText = IsInterrupted ? (StateText.StartsWith("已中断：", StringComparison.Ordinal)
                ? StateText["已中断：".Length..]
                : "") : "",
            FinishedAt = DateTime.Now,
        };

    private void SetFile(string? filePath)
    {
        FilePath = filePath ?? "";
        FileName = string.IsNullOrWhiteSpace(filePath) ? "未命名文件" : Path.GetFileName(filePath);
        OnPropertyChanged(nameof(HasFilePath));
    }

    public override string ToString() => $"{FileName} <{Url}>";
}

/// <summary>下载记录文件读写：<c>Data\browser\downloads.json</c>（失败静默降级）。</summary>
public sealed class BrowserDownloadStore
{
    private readonly ToolStorage _storage;

    public BrowserDownloadStore(ToolStorage storage) => _storage = storage;

    public List<BrowserDownloadRecord> Load()
    {
        try
        {
            var json = _storage.ReadText(BrowserDownloads.FileName);
            if (string.IsNullOrWhiteSpace(json)) return new List<BrowserDownloadRecord>();
            return BrowserDownloads.Normalize(
                JsonSerializer.Deserialize<List<BrowserDownloadRecord>>(json, JsonFileStore.Options));
        }
        catch
        {
            return new List<BrowserDownloadRecord>();
        }
    }

    public bool Save(IEnumerable<BrowserDownloadRecord> records)
        => _storage.WriteText(BrowserDownloads.FileName,
            JsonFileStore.Serialize(BrowserDownloads.Normalize(records))) is not null;
}
