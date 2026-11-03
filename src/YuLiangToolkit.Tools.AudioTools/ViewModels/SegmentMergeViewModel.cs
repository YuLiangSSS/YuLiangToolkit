using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.AudioTools.ViewModels;

/// <summary>
/// 「分段视频合成」的 ViewModel：把 m3u8 播放列表或一文件夹的 .ts / .m4s 分段，
/// 用系统 FFmpeg 合成一个整段 mp4 / mkv。
///
/// <para>配置落 <c>Config\video-merge\config.json</c>；FFmpeg 路径沿用全局设置
/// （<c>%AppData%\YuLiangToolkit\settings.json</c> 的 FFmpegPath）。</para>
/// </summary>
public sealed class SegmentMergeViewModel : ToolViewModelBase
{
    private CancellationTokenSource? _cts;
    private string? _ffmpegPath;
    private long _lastProgressUiTick;

    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    private string _ffmpegPathText = "";
    private bool _isProgressIndeterminate;
    private string _segmentSummary = "";

    public SegmentMergeViewModel() : base(SegmentMergeConfig.ToolId)
    {
        Cfg = LoadConfig<SegmentMergeConfig>();

        BrowsePlaylistCommand = new RelayCommand(_ => BrowsePlaylist());
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder());
        MergeCommand = new AsyncRelayCommand(_ => MergeAsync(), _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        BrowseFFmpegCommand = new RelayCommand(_ => BrowseFFmpeg());
        OpenFfmpegDownloadCommand = new RelayCommand(_ => OpenFfmpegDownload());
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());

        Cfg.PropertyChanged += OnConfigChanged;
        RefreshSegments();

        Log($"配置文件：{Cfg.FilePath}");
        _ = InitFFmpegAsync();
    }

    /// <summary>本功能的选项（绑定界面，自动落盘）。</summary>
    public SegmentMergeConfig Cfg { get; }

    // ---------- 状态 ----------

    public string FFmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => Set(ref _ffmpegStatusText, value);
    }

    public string FFmpegPathText
    {
        get => _ffmpegPathText;
        private set => Set(ref _ffmpegPathText, value);
    }

    /// <summary>进度条是否走 indeterminate（拿不到总时长时）。</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => Set(ref _isProgressIndeterminate, value);
    }

    /// <summary>分段文件夹模式下扫描到的分段说明。</summary>
    public string SegmentSummary
    {
        get => _segmentSummary;
        private set => Set(ref _segmentSummary, value);
    }

    /// <summary>输入提示（随播放列表 / 分段文件夹切换）。</summary>
    public string SourceHintText => Cfg.SourceKind == SegmentSourceKind.Playlist
        ? "m3u8 本地文件或 http(s) 链接；AES 加密的 HLS 也支持（由 FFmpeg 处理）"
        : "选一个装着 .ts / .m4s 等分段的文件夹，按文件名自然顺序拼接";

    // ---------- 命令 ----------

    public RelayCommand BrowsePlaylistCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public AsyncRelayCommand MergeCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseFFmpegCommand { get; }
    public RelayCommand OpenFfmpegDownloadCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }

    // ---------- 配置联动 ----------

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SegmentMergeConfig.SourceKind):
                OnPropertyChanged(nameof(SourceHintText));
                RefreshSegments();
                RefreshSuggestedOutputName();
                break;
            case nameof(SegmentMergeConfig.SourcePath):
                RefreshSegments();
                RefreshSuggestedOutputName();
                break;
            case nameof(SegmentMergeConfig.OutputFormat):
                if (!string.IsNullOrWhiteSpace(Cfg.OutputName))
                    Cfg.OutputName = SegmentMerger.EnsureExtension(Cfg.OutputName, Cfg.OutputFormat);
                break;
        }
    }

    /// <summary>输出名称为空时按来源自动起名；用户手填过就不动。</summary>
    private void RefreshSuggestedOutputName()
    {
        if (!string.IsNullOrWhiteSpace(Cfg.OutputName)) return;
        var source = Cfg.SourcePath?.Trim() ?? "";
        if (source.Length == 0) return;
        Cfg.OutputName = SegmentMerger.SuggestOutputName(source, Cfg.SourceKind, Cfg.OutputFormat);
    }

    private void RefreshSegments()
    {
        if (Cfg.SourceKind != SegmentSourceKind.Folder)
        {
            SegmentSummary = "";
            return;
        }

        var segments = SegmentMerger.CollectSegments(Cfg.SourcePath);
        if (segments.Count == 0)
        {
            SegmentSummary = "没有找到分段文件";
            return;
        }

        var names = string.Join("、", segments.Take(3).Select(Path.GetFileName));
        SegmentSummary = segments.Count <= 3
            ? $"共 {segments.Count} 个分段：{names}"
            : $"共 {segments.Count} 个分段：{names} …";
    }

    // ---------- FFmpeg 检测 / 选择 ----------

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
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "FFmpeg 可执行文件|ffmpeg.exe|所有文件|*.*",
            CheckFileExists = true,
            InitialDirectory = _ffmpegPath is not null
                ? Path.GetDirectoryName(_ffmpegPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog() != true) return;

        var path = dialog.FileName;
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

    // ---------- 浏览输入 / 输出 ----------

    private void BrowsePlaylist()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 m3u8 播放列表",
            Filter = "m3u8 播放列表|*.m3u8;*.m3u|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        Cfg.SourceKind = SegmentSourceKind.Playlist;
        Cfg.SourcePath = dialog.FileName;
        if (string.IsNullOrWhiteSpace(Cfg.OutputFolder))
            Cfg.OutputFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        Cfg.OutputName = SegmentMerger.SuggestOutputName(dialog.FileName, SegmentSourceKind.Playlist, Cfg.OutputFormat);
    }

    private void BrowseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择分段文件夹", Multiselect = false };
        if (Directory.Exists(Cfg.SourcePath) && Cfg.SourceKind == SegmentSourceKind.Folder)
            dialog.InitialDirectory = Cfg.SourcePath;
        if (dialog.ShowDialog() != true) return;

        Cfg.SourceKind = SegmentSourceKind.Folder;
        Cfg.SourcePath = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(Cfg.OutputFolder)) Cfg.OutputFolder = dialog.FolderName;
        Cfg.OutputName = SegmentMerger.SuggestOutputName(dialog.FolderName, SegmentSourceKind.Folder, Cfg.OutputFormat);
    }

    private void BrowseOutputFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择输出文件夹", Multiselect = false };
        var current = ResolveOutputFolder();
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog() == true) Cfg.OutputFolder = dialog.FolderName;
    }

    private string ResolveOutputFolder()
    {
        // 用户自己填的目录优先（可能还不存在，MergeAsync 里会创建）
        if (!string.IsNullOrWhiteSpace(Cfg.OutputFolder)) return Cfg.OutputFolder;

        if (Cfg.SourceKind == SegmentSourceKind.Folder && Directory.Exists(Cfg.SourcePath))
            return Cfg.SourcePath;

        var source = Cfg.SourcePath?.Trim() ?? "";
        if (File.Exists(source))
        {
            var dir = Path.GetDirectoryName(source);
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
        }

        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : AppPaths.DataRoot;
    }

    private void OpenOutputFolder()
    {
        var folder = ResolveOutputFolder();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            StatusText = "输出文件夹尚不存在";
            return;
        }
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch { StatusText = "无法打开输出文件夹"; }
    }

    // ---------- 开始合成 ----------

    private async Task MergeAsync()
    {
        if (_ffmpegPath is null)
        {
            StatusText = "请先配置 FFmpeg（未检测到，将打开选择窗口）";
            BrowseFFmpeg();
            return;
        }

        var validation = SegmentMerger.ValidateSource(Cfg.SourceKind, Cfg.SourcePath);
        if (validation is not null)
        {
            StatusText = validation;
            return;
        }

        var outputFolder = ResolveOutputFolder();
        try { Directory.CreateDirectory(outputFolder); }
        catch
        {
            StatusText = "无法创建输出文件夹";
            return;
        }

        var outputName = SegmentMerger.EnsureExtension(
            string.IsNullOrWhiteSpace(Cfg.OutputName)
                ? SegmentMerger.SuggestOutputName(Cfg.SourcePath, Cfg.SourceKind, Cfg.OutputFormat)
                : Cfg.OutputName,
            Cfg.OutputFormat);
        var outputPath = Path.Combine(outputFolder, outputName);
        string? concatListPath = null;
        double? durationSeconds = null;

        try
        {
            var options = new SegmentMergeOptions
            {
                SourceKind = Cfg.SourceKind,
                Source = Cfg.SourcePath.Trim(),
                OutputPath = outputPath,
                CopyStreams = Cfg.CopyStreams,
                OutputFormat = Cfg.OutputFormat,
                Overwrite = true,
            };

            if (Cfg.SourceKind == SegmentSourceKind.Folder)
            {
                var segments = SegmentMerger.CollectSegments(Cfg.SourcePath);
                if (segments.Count == 0)
                {
                    StatusText = "这个文件夹里没找到分段文件";
                    return;
                }

                // 输出就在分段文件夹里时，别把上一次的成品当成新分段拼进去
                segments.RemoveAll(segment =>
                    string.Equals(Path.GetFullPath(segment), Path.GetFullPath(outputPath),
                        StringComparison.OrdinalIgnoreCase));

                var cache = ToolCache.For(SegmentMergeConfig.ToolId);
                cache.EnsureCreated();
                concatListPath = cache.PathOf($"concat-{Guid.NewGuid():N}.txt");
                File.WriteAllText(concatListPath, SegmentMerger.BuildConcatListContent(segments),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                options = options with { ConcatListPath = concatListPath };
                Log($"===== 分段合成：{segments.Count} 个分段 → {outputPath} =====");
            }
            else
            {
                Log($"===== 分段合成：{options.Source} → {outputPath} =====");
                durationSeconds = await ProbeDurationAsync(_ffmpegPath, options.Source);
            }

            Log($"方式：{(options.CopyStreams ? "复制流（-c copy，不转码）" : "重新编码（H.264 + AAC）")}"
                + $" · 容器：{Cfg.OutputFormat.ToString().ToUpperInvariant()}"
                + (durationSeconds is > 0 ? $" · 总时长约 {durationSeconds:0.0} 秒" : " · 总时长未知"));

            _cts = new CancellationTokenSource();
            IsBusy = true;
            Progress = 0;
            IsProgressIndeterminate = durationSeconds is not > 0;
            StatusText = "正在合成…";
            var started = Stopwatch.StartNew();

            var args = SegmentMerger.BuildArguments(options);
            var result = await FFmpegRunner.RunAsync(
                _ffmpegPath, args, _cts.Token,
                line => OnProgressLine(line, durationSeconds),
                line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    Log("[ffmpeg] " + line.Trim());
                });

            if (result.Success)
            {
                Progress = 1;
                StatusText = $"合成完成：{outputName}（用时 {started.Elapsed.TotalSeconds:0.0} 秒）";
                Log($"===== 完成：{outputPath} =====");
            }
            else
            {
                StatusText = "合成失败，看日志里的 ffmpeg 错误";
                var tail = string.Join(Environment.NewLine,
                    (result.Error ?? "").Split('\n').Select(l => l.TrimEnd('\r'))
                        .Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(6));
                Log("✗ 合成失败：" + tail);
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消合成";
            Log("已取消合成");
        }
        catch (Exception ex)
        {
            StatusText = "合成失败：" + ex.Message;
            Log("✗ " + ex.Message);
        }
        finally
        {
            if (concatListPath is not null)
            {
                try { File.Delete(concatListPath); } catch { /* 缓存文件删不掉下次清理 */ }
            }
            IsProgressIndeterminate = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnProgressLine(string line, double? durationSeconds)
    {
        var microseconds = SegmentMerger.ParseProgressMicroseconds(line);
        if (microseconds is null || durationSeconds is not > 0) return;

        var now = Environment.TickCount64;
        if (now - _lastProgressUiTick < 150) return;
        _lastProgressUiTick = now;

        var ratio = Math.Clamp(microseconds.Value / (durationSeconds.Value * 1_000_000.0), 0, 1);
        Ui(() =>
        {
            Progress = ratio;
            StatusText = $"正在合成… {ratio:P0}";
        });
    }

    private static async Task<double?> ProbeDurationAsync(string ffmpeg, string source)
    {
        var ffprobe = FFmpegLocator.FindFFprobe(ffmpeg);
        if (ffprobe is null) return null;
        try
        {
            var result = await FFmpegRunner.RunAsync(ffprobe, new[]
            {
                "-v", "error",
                "-protocol_whitelist", SegmentMerger.ProtocolWhitelist,
                "-allowed_extensions", "ALL",
                "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1",
                source,
            });
            return SegmentMerger.ParseDurationSeconds(result.Output);
        }
        catch
        {
            return null;
        }
    }

    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    protected override void OnClosed()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
    }
}
