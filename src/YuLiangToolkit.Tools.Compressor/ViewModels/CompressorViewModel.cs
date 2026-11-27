using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Tools.Compressor.ViewModels;

/// <summary>压缩器页面 ViewModel：拖入 / 批量选择文件，按预设压缩图片、音频、视频。</summary>
public sealed class CompressorViewModel : ToolViewModelBase
{
    private readonly List<string> _paths = new();
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private double _overallProgress;
    private CompressorItem? _selectedItem;
    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    private string? _ffmpegPath;

    public CompressorViewModel() : base(CompressorConfig.ToolId)
    {
        Cfg = LoadConfig<CompressorConfig>();
        Items = new ObservableCollection<CompressorItem>();
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(SummaryText));
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        };

        AddFilesCommand = new RelayCommand(_ => ChooseFiles());
        AddFolderCommand = new RelayCommand(_ => ChooseFolder());
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedItem is not null && !IsRunning);
        ClearCommand = new RelayCommand(_ => ClearItems(), _ => HasItems && !IsRunning);
        StartCommand = new AsyncRelayCommand(_ => StartAsync(), _ => CanStart);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsRunning);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder());
        OpenOutputCommand = new RelayCommand(_ => OpenOutputFolder());
        ChooseFfmpegCommand = new RelayCommand(_ => ChooseFfmpeg());
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());

        DetectFfmpeg();
    }

    public CompressorConfig Cfg { get; }
    public ObservableCollection<CompressorItem> Items { get; }

    public IReadOnlyList<OptionItem<CompressionPreset>> PresetOptions { get; } =
        CompressorProfiles.All.Select(p => new OptionItem<CompressionPreset>(p, p.Name)).ToArray();

    public OptionItem<CompressionPreset> SelectedPreset
    {
        get => PresetOptions.FirstOrDefault(p => p.Value.Key == Cfg.Preset) ?? PresetOptions[1];
        set
        {
            if (value is null) return;
            Cfg.Preset = value.Value.Key;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PresetDescription));
            OnPropertyChanged(nameof(PresetSummary));
        }
    }

    public string PresetDescription => SelectedPreset.Value.Description;

    public IReadOnlyList<OptionItem<string>> ImageFormatOptions { get; } = OptionItem.From(
        ("jpg", "JPG（有损，通用）"), ("png", "PNG（无损）"));

    public OptionItem<string> SelectedImageFormat
    {
        get => OptionItem<string>.Find(ImageFormatOptions, Cfg.ImageFormat) ?? ImageFormatOptions[0];
        set
        {
            if (value is null) return;
            Cfg.ImageFormat = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormatSummary));
        }
    }

    public IReadOnlyList<OptionItem<string>> AudioFormatOptions { get; } = OptionItem.From(
        ("mp3", "MP3（通用）"), ("m4a", "M4A / AAC"), ("ogg", "OGG"));

    public OptionItem<string> SelectedAudioFormat
    {
        get => OptionItem<string>.Find(AudioFormatOptions, Cfg.AudioFormat) ?? AudioFormatOptions[0];
        set
        {
            if (value is null) return;
            Cfg.AudioFormat = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FormatSummary));
        }
    }

    public string VideoFormatText => "MP4（H.264 / AAC）";
    public string OutputFolderText => Cfg.EffectiveOutputFolder;
    public string OutputSummary => $"输出到：{Cfg.EffectiveOutputFolder}";
    public string FormatSummary => $"图片 {Cfg.ImageFormat.ToUpperInvariant()} · 音频 {Cfg.AudioFormat.ToUpperInvariant()} · 视频 MP4";
    public string PresetSummary => $"{SelectedPreset.Value.Name} · {SelectedPreset.Value.Description}";

    public bool HasItems => Items.Count > 0;
    public bool CanStart => HasItems && !IsRunning;

    public string SummaryText
    {
        get
        {
            if (Items.Count == 0) return "拖入图片 / 音频 / 视频，或点上面的「添加文件」";
            long total = 0;
            foreach (var item in Items) total += item.FileSizeBytes;
            return $"{Items.Count} 个文件 · 共 {CompressorFiles.SizeText(total)}";
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            OnPropertyChanged(nameof(CanStart));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public double OverallProgress
    {
        get => _overallProgress;
        private set => Set(ref _overallProgress, Math.Clamp(value, 0, 100));
    }

    public CompressorItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!Set(ref _selectedItem, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string FfmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => Set(ref _ffmpegStatusText, value);
    }

    public RelayCommand AddFilesCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public RelayCommand ChooseFfmpegCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }

    public void OnPageLoaded()
    {
        NotifyOpened();
        DetectFfmpeg();
        OnPropertyChanged(nameof(OutputFolderText));
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(FormatSummary));
        OnPropertyChanged(nameof(PresetSummary));
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Directory.Exists(raw))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(raw, "*", SearchOption.AllDirectories))
                    {
                        if (CompressorFiles.IsSupported(file) && AddOne(file)) added++;
                        else skipped++;
                    }
                }
                catch { skipped++; }
                continue;
            }

            if (!File.Exists(raw) || !CompressorFiles.IsSupported(raw)) { skipped++; continue; }
            if (AddOne(raw)) added++;
            else skipped++;
        }

        StatusText = added > 0
            ? $"已添加 {added} 个文件" + (skipped > 0 ? $"，跳过 {skipped} 个不支持 / 重复文件" : "")
            : "没有添加新文件";
        OnPropertyChanged(nameof(SummaryText));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool AddOne(string path)
    {
        var full = Path.GetFullPath(path);
        if (_paths.Any(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) return false;
        var kind = CompressorFiles.DetectKind(full);
        if (kind == CompressorFileKind.Unknown) return false;
        _paths.Add(full);
        var item = new CompressorItem(full, kind);
        Items.Add(item);
        _ = ProbeAsync(item);
        return true;
    }

    private void ChooseFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要压缩的文件",
            Filter = CompressorFiles.SupportedFilter,
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(Cfg.LastInputFolder) ? Cfg.LastInputFolder : "",
        };
        if (dialog.ShowDialog() != true) return;
        Cfg.LastInputFolder = Path.GetDirectoryName(dialog.FileNames[0]) ?? "";
        AddPaths(dialog.FileNames);
    }

    private void ChooseFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择包含媒体的文件夹（会递归扫描）",
            InitialDirectory = Directory.Exists(Cfg.LastInputFolder) ? Cfg.LastInputFolder : "",
        };
        if (dialog.ShowDialog() != true) return;
        Cfg.LastInputFolder = dialog.FolderName;
        AddPaths(new[] { dialog.FolderName });
    }

    private void RemoveSelected()
    {
        if (SelectedItem is null) return;
        _paths.Remove(SelectedItem.InputPath);
        Items.Remove(SelectedItem);
        SelectedItem = null;
        OnPropertyChanged(nameof(SummaryText));
    }

    private void ClearItems()
    {
        _paths.Clear();
        Items.Clear();
        SelectedItem = null;
        OverallProgress = 0;
        StatusText = "列表已清空。";
        OnPropertyChanged(nameof(SummaryText));
    }

    private async Task ProbeAsync(CompressorItem item)
    {
        try
        {
            var info = await CompressorProbe.ProbeAsync(item.InputPath, item.Kind, _ffmpegPath);
            Ui(() =>
            {
                if (info is null)
                    item.DetailText = item.Kind == CompressorFileKind.Image ? "无法读取尺寸" : "无法读取媒体信息";
                else
                {
                    item.DetailText = info.Summary;
                    item.DurationSeconds = info.DurationSeconds;
                }
                item.NotifyDetailChanged();
            });
        }
        catch (Exception ex)
        {
            Ui(() => item.DetailText = "信息读取失败：" + ex.Message);
        }
    }

    private void DetectFfmpeg()
    {
        var found = FFmpegLocator.Find(AppSettings.Settings.FFmpegPath);
        if (found is null)
        {
            _ffmpegPath = null;
            FfmpegStatusText = "⚠️ 未检测到 FFmpeg：音频 / 视频压缩前请先点「选择 ffmpeg.exe」";
            return;
        }

        _ffmpegPath = found;
        AppSettings.Settings.FFmpegPath ??= found;
        AppSettings.Save();
        FfmpegStatusText = "正在校验 FFmpeg…";
        _ = Task.Run(async () =>
        {
            var version = await FFmpegRunner.GetVersionAsync(found);
            Ui(() => FfmpegStatusText = $"✅ FFmpeg {(string.IsNullOrWhiteSpace(version) ? "" : "v" + version)} · {found}");
        });
    }

    private void ChooseFfmpeg()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "FFmpeg|ffmpeg.exe|可执行文件|*.exe",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        AppSettings.Settings.FFmpegPath = dialog.FileName;
        AppSettings.Save();
        _ffmpegPath = dialog.FileName;
        DetectFfmpeg();
    }

    private void BrowseOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择压缩输出目录",
            InitialDirectory = Directory.Exists(Cfg.EffectiveOutputFolder) ? Cfg.EffectiveOutputFolder : "",
        };
        if (dialog.ShowDialog() != true) return;
        Cfg.OutputFolder = dialog.FolderName;
        OnPropertyChanged(nameof(OutputFolderText));
        OnPropertyChanged(nameof(OutputSummary));
    }

    private void OpenOutputFolder()
    {
        try
        {
            Directory.CreateDirectory(Cfg.EffectiveOutputFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Cfg.EffectiveOutputFolder}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = "打开输出目录失败：" + ex.Message;
        }
    }

    private void ResetSettings()
    {
        Cfg.OutputFolder = "";
        Cfg.FileSuffix = CompressorConfig.DefaultSuffix;
        Cfg.Preset = CompressorProfiles.Standard;
        Cfg.ImageFormat = "jpg";
        Cfg.AudioFormat = "mp3";
        Cfg.VideoFormat = "mp4";
        Cfg.OverwriteExisting = false;
        Cfg.OpenFolderAfter = true;
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(SelectedImageFormat));
        OnPropertyChanged(nameof(SelectedAudioFormat));
        OnPropertyChanged(nameof(OutputFolderText));
        OnPropertyChanged(nameof(OutputSummary));
        OnPropertyChanged(nameof(FormatSummary));
        OnPropertyChanged(nameof(PresetSummary));
        StatusText = "已恢复压缩器默认设置。";
    }

    private void Cancel()
    {
        try { _cts?.Cancel(); } catch { }
        StatusText = "正在取消…";
    }

    private async Task StartAsync()
    {
        if (IsRunning || Items.Count == 0) return;
        IsRunning = true;
        OverallProgress = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var preset = SelectedPreset.Value;
        var completed = 0;
        var failed = 0;
        var canceled = false;

        if (Items.Any(i => i.Kind is CompressorFileKind.Audio or CompressorFileKind.Video)
            && string.IsNullOrWhiteSpace(_ffmpegPath))
        {
            IsRunning = false;
            StatusText = "音频 / 视频需要 FFmpeg：请先在右侧点「选择 ffmpeg.exe」。";
            Log("✗ 未检测到 FFmpeg。");
            return;
        }

        try
        {
            Directory.CreateDirectory(Cfg.EffectiveOutputFolder);
            Log($"开始压缩：{Items.Count} 个文件，预设 {preset.Name}，输出到 {Cfg.EffectiveOutputFolder}");

            foreach (var item in Items)
            {
                token.ThrowIfCancellationRequested();
                item.IsDone = false;
                item.IsFailed = false;
                item.Progress = 0;
                item.StatusText = "压缩中…";
                item.OutputPath = null;
                var ext = CompressorFiles.OutputExtension(item.Kind, Cfg.ImageFormat, Cfg.AudioFormat, Cfg.VideoFormat);
                var output = CompressorFiles.BuildOutputPath(
                    item.InputPath, Cfg.EffectiveOutputFolder, Cfg.FileSuffix, ext, Cfg.OverwriteExisting);
                item.OutputPath = output;

                try
                {
                    if (item.Kind == CompressorFileKind.Image)
                    {
                        await Task.Run(() => CompressorImage.Compress(
                            item.InputPath,
                            output, preset.ImageQuality, Cfg.ImageFormat, preset.ImageMaxDimension), token);
                        item.Progress = 100;
                    }
                    else
                    {
                        var args = item.Kind == CompressorFileKind.Audio
                            ? CompressorArguments.BuildAudio(item.InputPath, output, preset, Cfg.AudioFormat)
                            : CompressorArguments.BuildVideo(item.InputPath, output, preset);
                        var result = await FFmpegRunner.RunAsync(
                            _ffmpegPath!, args, token,
                            onOutputLine: line =>
                            {
                                var seconds = CompressorArguments.ParseProgressSeconds(line);
                                if (seconds is null || item.DurationSeconds <= 0) return;
                                Ui(() => item.Progress = Math.Min(99, seconds.Value / item.DurationSeconds * 100));
                            });
                        if (!result.Success)
                        {
                            var tail = string.Join(" | ", result.Error.Split('\n')
                                .Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(3));
                            throw new InvalidOperationException(string.IsNullOrWhiteSpace(tail) ? "FFmpeg 返回失败" : tail);
                        }
                        item.Progress = 100;
                    }

                    item.IsDone = true;
                    item.StatusText = BuildDoneText(item, output);
                    completed++;
                    Log($"✓ {item.FileName} → {Path.GetFileName(output)}");
                }
                catch (OperationCanceledException)
                {
                    item.StatusText = "已取消";
                    canceled = true;
                    throw;
                }
                catch (Exception ex)
                {
                    item.IsFailed = true;
                    item.StatusText = "失败：" + ex.Message;
                    failed++;
                    Log($"✗ {item.FileName}：{ex.Message}");
                }
                finally
                {
                    item.RefreshSize();
                    OverallProgress = Items.Count == 0 ? 0 : (Items.IndexOf(item) + 1) * 100.0 / Items.Count;
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
            StatusText = "已取消压缩。";
        }
        catch (Exception ex)
        {
            StatusText = "压缩失败：" + ex.Message;
            Log("✗ 压缩失败：" + ex);
        }
        finally
        {
            IsRunning = false;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            if (!canceled)
            {
                OverallProgress = 100;
                StatusText = failed == 0
                    ? $"压缩完成：{completed} 个文件"
                    : $"压缩结束：成功 {completed} 个，失败 {failed} 个";
                if (Cfg.OpenFolderAfter && completed > 0) OpenOutputFolder();
            }
        }
    }

    private static string BuildDoneText(CompressorItem item, string output)
    {
        try
        {
            var before = item.FileSizeBytes;
            var after = new FileInfo(output).Length;
            if (before <= 0 || after <= 0) return "完成";
            var saved = Math.Max(0, 100.0 - after * 100.0 / before);
            return $"完成 · {CompressorFiles.SizeText(before)} → {CompressorFiles.SizeText(after)}（省 {saved:0}%）";
        }
        catch
        {
            return "完成";
        }
    }
}
