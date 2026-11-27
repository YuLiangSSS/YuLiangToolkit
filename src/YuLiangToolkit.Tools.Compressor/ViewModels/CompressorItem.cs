using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Compressor.ViewModels;

/// <summary>压缩列表中的一行（一个输入文件）。</summary>
public sealed class CompressorItem : ObservableObject
{
    private string _detailText = "读取中…";
    private string _statusText = "等待压缩";
    private double _progress;
    private string? _outputPath;
    private bool _isDone;
    private bool _isFailed;
    private double _durationSeconds;

    public CompressorItem(string path, CompressorFileKind kind)
    {
        InputPath = path;
        Kind = kind;
        FileSizeBytes = SafeLength(path);
    }

    public string InputPath { get; }
    public CompressorFileKind Kind { get; }
    public string FileName => Path.GetFileName(InputPath);
    public string ExtensionText => Path.GetExtension(InputPath).TrimStart('.').ToUpperInvariant();
    public string KindText => CompressorFiles.KindText(Kind);
    public string KindIcon => CompressorFiles.KindIcon(Kind);
    public long FileSizeBytes { get; private set; }
    public string SizeText => CompressorFiles.SizeText(FileSizeBytes);

    public string DetailText
    {
        get => _detailText;
        set
        {
            if (!Set(ref _detailText, value)) return;
            OnPropertyChanged(nameof(DisplayDetail));
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, Math.Clamp(value, 0, 100));
    }

    public string? OutputPath
    {
        get => _outputPath;
        set => Set(ref _outputPath, value);
    }

    public bool IsDone
    {
        get => _isDone;
        set => Set(ref _isDone, value);
    }

    public bool IsFailed
    {
        get => _isFailed;
        set => Set(ref _isFailed, value);
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set => Set(ref _durationSeconds, value);
    }

    public string DisplayDetail
    {
        get
        {
            var parts = new List<string> { KindText, ExtensionText, SizeText };
            if (!string.IsNullOrWhiteSpace(DetailText)) parts.Add(DetailText);
            return string.Join(" · ", parts);
        }
    }

    public void RefreshSize()
    {
        FileSizeBytes = SafeLength(InputPath);
        OnPropertyChanged(nameof(FileSizeBytes));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DisplayDetail));
    }

    public void NotifyDetailChanged() => OnPropertyChanged(nameof(DisplayDetail));

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }
}
