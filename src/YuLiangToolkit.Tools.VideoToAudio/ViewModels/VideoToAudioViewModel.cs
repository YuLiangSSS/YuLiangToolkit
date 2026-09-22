using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.VideoToAudio.ViewModels;

/// <summary>文件列表中的单个媒体项。</summary>
public class MediaFileItem : ObservableObject
{
    public required string FilePath { get; init; }
    public long SizeBytes { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string SizeText => FileSize.Format(SizeBytes);

    private string _outputName = "";
    public string OutputName { get => _outputName; set => Set(ref _outputName, value); }

    private string _status = "待转换";
    public string Status { get => _status; set => Set(ref _status, value); }

    private string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }
}

/// <summary>
/// 视频转音频视图模型。
/// 选项（模式/码率/并行数/文件夹…）全部存在 <see cref="Cfg"/> 里，由框架自动写入
/// <c>Config\video-to-audio\config.json</c>；本类只负责业务逻辑（扫描、转换、进度、日志）。
/// </summary>
public class VideoToAudioViewModel : ToolViewModelBase
{
    private static readonly string[] Mp4OnlyPatterns = { "*.mp4" };
    private static readonly string[] CommonVideoPatterns = { "*.mp4", "*.mkv", "*.mov", "*.webm", "*.avi", "*.m4v", "*.flv", "*.ts", "*.mts" };

    private CancellationTokenSource? _cts;
    private string? _ffmpegPath;

    public VideoToAudioViewModel() : base("video-to-audio")
    {
        // 首次运行时从老配置（%AppData% 的 settings.json）迁移过来；之后一律以本文件为准
        Cfg = LoadConfig(VideoToAudioConfig.FromLegacySettings);

        BrowseInputCommand = new RelayCommand(_ => BrowseInput());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync());
        ConvertCommand = new AsyncRelayCommand(_ => ConvertAsync());
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        BrowseFFmpegCommand = new RelayCommand(_ => BrowseFFmpeg());
        OpenFfmpegDownloadCommand = new RelayCommand(_ => OpenFfmpegDownload());
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());

        // 配置变化时做界面联动（保存由框架负责，这里只管刷新）
        Cfg.PropertyChanged += OnConfigChanged;

        Log($"配置文件：{Cfg.FilePath}");
        _ = InitFFmpegAsync();
    }

    /// <summary>本功能的所有选项（绑定到界面，改动自动落盘）。</summary>
    public VideoToAudioConfig Cfg { get; }

    // ---------- 选项列表（界面用） ----------

    public IReadOnlyList<string> BitrateOptions { get; } = new[] { "128", "192", "256", "320" };

    /// <summary>并行转换数选项：0 = 自动（按 CPU 核心数）。</summary>
    public IReadOnlyList<OptionItem<int>> ParallelismOptions { get; } = OptionItem.From(
        (0, "自动"), (1, "1"), (2, "2"), (4, "4"), (8, "8"), (16, "16"));

    /// <summary>FLAC 压缩级别 0-8（仅 FLAC 模式生效）。</summary>
    public IReadOnlyList<OptionItem<int>> FlacLevelOptions { get; } = OptionItem.From(
        (0, "0（最快）"), (1, "1"), (2, "2"), (3, "3"), (4, "4"),
        (5, "5（推荐）"), (6, "6"), (7, "7"), (8, "8（最小）"));

    /// <summary>下拉框选中项与配置值之间的桥接（ComboBox 需要 Options 里的同一个实例）。</summary>
    public OptionItem<int> SelectedParallelism
    {
        get => OptionItem<int>.Find(ParallelismOptions, Cfg.Parallelism) ?? ParallelismOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Parallelism = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedFlacLevel
    {
        get => OptionItem<int>.Find(FlacLevelOptions, Cfg.FlacCompressionLevel) ?? FlacLevelOptions[5];
        set
        {
            if (value is null) return;
            Cfg.FlacCompressionLevel = value.Value;
            OnPropertyChanged();
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoToAudioConfig.Mode):
            case nameof(VideoToAudioConfig.Bitrate):
                RefreshOutputNames();
                break;
            case nameof(VideoToAudioConfig.Parallelism):
                OnPropertyChanged(nameof(SelectedParallelism));
                break;
            case nameof(VideoToAudioConfig.FlacCompressionLevel):
                OnPropertyChanged(nameof(SelectedFlacLevel));
                break;
            case nameof(VideoToAudioConfig.InputFolder):
                // 勾选了“输出到源文件夹”时，源文件夹一改，输出目录跟着走
                if (Cfg.UseSourceFolder) Cfg.OutputFolder = Cfg.InputFolder;
                break;
            case nameof(VideoToAudioConfig.UseSourceFolder):
                if (Cfg.UseSourceFolder) Cfg.OutputFolder = Cfg.InputFolder;
                break;
        }
    }

    // ---------- 文件 / 状态 ----------

    public ObservableCollection<MediaFileItem> Files { get; } = new();

    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    public string FFmpegStatusText { get => _ffmpegStatusText; set => Set(ref _ffmpegStatusText, value); }

    private string _ffmpegPathText = "";
    public string FFmpegPathText { get => _ffmpegPathText; set => Set(ref _ffmpegPathText, value); }

    // ---------- 命令 ----------

    public RelayCommand BrowseInputCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand ConvertCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseFFmpegCommand { get; }
    public RelayCommand OpenFfmpegDownloadCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    // ---------- 实现 ----------

    private async Task InitFFmpegAsync()
    {
        var found = FFmpegLocator.Find(AppSettings.Settings.FFmpegPath);
        if (found is null)
        {
            SetFFmpegStatus(null, null);
            return;
        }
        _ffmpegPath = found;
        var version = await FFmpegRunner.GetVersionAsync(found);
        // FFmpeg 路径是跨功能共享的全局设置，仍存在 %AppData%\YuLiangToolkit\settings.json
        AppSettings.Settings.FFmpegPath ??= found;
        AppSettings.Save();
        SetFFmpegStatus(found, version);
    }

    private void SetFFmpegStatus(string? path, string? version)
    {
        void Update()
        {
            FFmpegPathText = path ?? "";
            FFmpegStatusText = path is null
                ? "⚠️ 未检测到 FFmpeg，请「选择 FFmpeg…」指定 ffmpeg.exe，或「下载 FFmpeg」后重新检测"
                : $"✅ FFmpeg 已就绪（v{version ?? "?"}）· {path}";
        }
        Ui(Update);
    }

    private void BrowseFFmpeg()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "FFmpeg 可执行文件|ffmpeg.exe|所有文件|*.*",
            CheckFileExists = true,
            InitialDirectory = _ffmpegPath is not null ? Path.GetDirectoryName(_ffmpegPath) : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FileName;
        if (!File.Exists(path)) return;

        _ffmpegPath = path;
        AppSettings.Settings.FFmpegPath = path;
        AppSettings.Save();
        FFmpegStatusText = $"正在校验：{path}…";
        _ = Task.Run(async () =>
        {
            var version = await FFmpegRunner.GetVersionAsync(path);
            SetFFmpegStatus(path, version);
        });
    }

    private void OpenFfmpegDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "https://www.gyan.dev/ffmpeg/builds/", UseShellExecute = true });
        }
        catch
        {
            StatusText = "无法打开浏览器，请手动访问 https://www.gyan.dev/ffmpeg/builds/";
        }
    }

    private void BrowseInput()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择源文件夹",
            Multiselect = false,
        };
        if (Directory.Exists(Cfg.InputFolder)) dlg.InitialDirectory = Cfg.InputFolder;
        if (dlg.ShowDialog() == true) Cfg.InputFolder = dlg.FolderName;
    }

    private void BrowseOutput()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择输出目录",
            Multiselect = false,
        };
        if (Directory.Exists(Cfg.OutputFolder)) dlg.InitialDirectory = Cfg.OutputFolder;
        if (dlg.ShowDialog() == true) Cfg.OutputFolder = dlg.FolderName;
    }

    private string ResolveOutputFolder()
    {
        if (Cfg.UseSourceFolder) return Cfg.InputFolder ?? "";
        return string.IsNullOrWhiteSpace(Cfg.OutputFolder) ? Cfg.InputFolder ?? "" : Cfg.OutputFolder;
    }

    private async Task ScanAsync()
    {
        var input = Cfg.InputFolder?.Trim();
        if (string.IsNullOrEmpty(input) || !Directory.Exists(input))
        {
            StatusText = "请先选择有效的源文件夹";
            return;
        }

        StatusText = "正在扫描…";
        var recursive = Cfg.IncludeSubfolders;
        var patterns = Cfg.OnlyMp4 ? Mp4OnlyPatterns : CommonVideoPatterns;

        var found = await Task.Run(() =>
        {
            var list = new List<MediaFileItem>();
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var p in patterns)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(input, p, option))
                    {
                        try { list.Add(new MediaFileItem { FilePath = f, SizeBytes = new FileInfo(f).Length }); }
                        catch { /* 单个文件读取失败忽略 */ }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            return list;
        });

        Files.Clear();
        foreach (var f in found.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)) Files.Add(f);
        RefreshOutputNames();
        Progress = 0;
        ProgressText = $"0 / {Files.Count}";
        StatusText = Files.Count > 0 ? $"扫描完成：共 {Files.Count} 个视频文件" : "未找到视频文件（请检查文件夹与筛选条件）";
    }

    private void RefreshOutputNames()
    {
        var ext = Cfg.Mode switch
        {
            ConvertMode.Mp3Lossy => "mp3",
            ConvertMode.FlacLossless => "flac",
            _ => "m4a", // 复制模式：转换前用 ffprobe 探测后修正
        };
        foreach (var f in Files)
            f.OutputName = Path.GetFileNameWithoutExtension(f.FilePath) + "." + ext;
    }

    private async Task ConvertAsync()
    {
        if (_ffmpegPath is null)
        {
            StatusText = "请先配置 FFmpeg（未检测到，将打开选择窗口）";
            BrowseFFmpeg();
            return;
        }
        if (string.IsNullOrWhiteSpace(Cfg.InputFolder) || !Directory.Exists(Cfg.InputFolder))
        {
            StatusText = "请先选择有效的源文件夹";
            return;
        }
        if (Files.Count == 0)
        {
            await ScanAsync();
            if (Files.Count == 0) return;
        }

        var outputFolder = ResolveOutputFolder();
        try { Directory.CreateDirectory(outputFolder); }
        catch { StatusText = "无法创建输出目录"; return; }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        ProgressText = $"0 / {Files.Count}";
        var sw = Stopwatch.StartNew();
        var total = Files.Count;
        var done = 0;
        var ok = 0;
        var failed = 0;
        var cancelled = false;
        var parallelism = ResolveParallelism();

        Log($"===== 开始批量转换：{total} 个文件 → {outputFolder} =====");
        Log($"模式：{Cfg.Mode switch
        {
            ConvertMode.Mp3Lossy => $"MP3（{Cfg.Bitrate} kbps，有损）",
            ConvertMode.FlacLossless => $"FLAC（无损，压缩级别 {Cfg.FlacCompressionLevel}）",
            _ => "复制原始音轨（无损，不重新编码）",
        }} · 并行数 {parallelism}");
        StatusText = $"开始转换（{total} 个文件，并行 {parallelism}）…";

        // 输出路径固定为“源文件名.扩展名”，已存在则直接覆盖（ffmpeg 带 -y）；
        // 同一输出路径的转换用 per-path 信号量串行化，避免并行写同一个文件导致损坏
        var pathLocks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        var lastUiUpdate = 0L;

        try
        {
            var files = Files.ToList();
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = _cts.Token,
            }, async (item, ct) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    item.IsCurrent = true;
                    item.Status = "转换中…";
                    item.Detail = "";

                    // 复制模式：先用 ffprobe 探测音轨编码，决定输出扩展名
                    if (Cfg.Mode == ConvertMode.CopyOriginalAudio)
                    {
                        var ext = await ProbeAudioExtensionAsync(_ffmpegPath, item.FilePath, ct);
                        item.OutputName = Path.GetFileNameWithoutExtension(item.FilePath) + "." + ext;
                    }

                    // 输出路径 = 源文件名 + 目标扩展名；已存在则覆盖
                    var outPath = Path.Combine(outputFolder, item.OutputName);
                    Log($"[{item.FileName}] → {item.OutputName}");

                    // 同一输出路径串行执行，避免并行进程同时写一个文件
                    var gate = pathLocks.GetOrAdd(outPath, _ => new SemaphoreSlim(1, 1));
                    await gate.WaitAsync(ct);
                    FileResult r;
                    try
                    {
                        r = await RunForFileAsync(_ffmpegPath, item, outPath, ct);
                    }
                    finally
                    {
                        gate.Release();
                    }
                    if (r.Success)
                    {
                        item.Status = "✓ 完成";
                        Interlocked.Increment(ref ok);
                    }
                    else
                    {
                        item.Status = "✗ 失败";
                        item.Detail = r.ErrorTail;
                        Interlocked.Increment(ref failed);
                        Log($"  ✗ {item.FileName}：{r.ErrorTail}");
                    }
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已取消";
                    throw;
                }
                catch (Exception ex)
                {
                    item.Status = "✗ 失败";
                    item.Detail = ex.Message;
                    Interlocked.Increment(ref failed);
                    Log($"  ✗ {item.FileName}：{ex.Message}");
                }
                finally
                {
                    item.IsCurrent = false;
                    var n = Interlocked.Increment(ref done);
                    // 节流刷新 UI（约每 100ms 一次，避免频繁跨线程调度）
                    var now = Environment.TickCount64;
                    if (n == total || now - Volatile.Read(ref lastUiUpdate) > 100)
                    {
                        Volatile.Write(ref lastUiUpdate, now);
                        Progress = (double)n / total;
                        ProgressText = $"{n} / {total}";
                        StatusText = $"进度 {n}/{total} · 成功 {Volatile.Read(ref ok)} · 失败 {Volatile.Read(ref failed)}";
                    }
                }
            });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            IsBusy = false;
            _cts = null;
            sw.Stop();
        }

        Log($"===== 结束：成功 {ok}，失败 {failed}，用时 {sw.Elapsed.TotalSeconds:F1} 秒 =====");
        StatusText = cancelled
            ? $"已取消：成功 {ok} 个，失败 {failed} 个"
            : $"转换完成：成功 {ok} 个，失败 {failed} 个，用时 {sw.Elapsed.TotalSeconds:F1} 秒";
    }

    private sealed record FileResult(bool Success, string ErrorTail);

    private async Task<FileResult> RunForFileAsync(string ffmpeg, MediaFileItem item, string outPath, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y",
            "-loglevel", "error", // 只输出真正的错误，避免 1000 个文件刷爆日志/UI
            "-i", item.FilePath,
            "-map", "0:a:0",
            "-vn", "-sn", "-dn", // 不处理视频/字幕/数据流，只提音频
            "-map_metadata", "0",
        };

        switch (Cfg.Mode)
        {
            case ConvertMode.Mp3Lossy:
                args.AddRange(new[] { "-c:a", "libmp3lame", "-b:a", $"{Cfg.Bitrate}k", "-id3v2_version", "3" });
                break;
            case ConvertMode.FlacLossless:
                // 压缩级别越低编码越快：0 最快 / 8 最小（无损，音质无差别）
                args.AddRange(new[] { "-c:a", "flac", "-compression_level", Cfg.FlacCompressionLevel.ToString() });
                break;
            case ConvertMode.CopyOriginalAudio:
                args.AddRange(new[] { "-c:a", "copy" });
                break;
        }
        args.Add(outPath);

        var result = await FFmpegRunner.RunAsync(ffmpeg, args, ct);

        var errorTail = string.Join(Environment.NewLine, (result.Error ?? "")
            .Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .TakeLast(3));

        return new FileResult(result.Success, errorTail);
    }

    /// <summary>解析并行度设置：0/自动 → CPU 核心数（上限 16）。</summary>
    private int ResolveParallelism()
    {
        if (Cfg.Parallelism > 0) return Math.Clamp(Cfg.Parallelism, 1, 16);
        return Math.Clamp(Environment.ProcessorCount, 1, 16);
    }

    /// <summary>用 ffprobe 探测第一个音频流的编码，返回合适的容器扩展名。</summary>
    private static async Task<string> ProbeAudioExtensionAsync(string ffmpeg, string input, CancellationToken ct)
    {
        var ffprobe = FFmpegLocator.FindFFprobe(ffmpeg);
        if (ffprobe is null) return "m4a";
        try
        {
            var result = await FFmpegRunner.RunAsync(ffprobe, new[]
            {
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=codec_name",
                "-of", "default=noprint_wrappers=1:nokey=1",
                input,
            }, ct);
            var codec = result.Output.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
            return codec switch
            {
                "aac" or "alac" => "m4a",
                "mp3" => "mp3",
                "opus" => "opus",
                "vorbis" => "ogg",
                "flac" => "flac",
                "ac3" => "ac3",
                "eac3" => "eac3",
                "dts" => "dts",
                "pcm_s16le" or "pcm_s24le" or "pcm_f32le" => "wav",
                _ => "m4a",
            };
        }
        catch (OperationCanceledException) { throw; }
        catch { return "m4a"; }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    private void OpenOutputFolder()
    {
        var folder = ResolveOutputFolder();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusText = "输出目录尚不存在";
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch { StatusText = "无法打开输出目录"; }
    }
}
