using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;

namespace YuLiangToolkit.Tools.VideoToGif.ViewModels;

/// <summary>
/// 视频转 GIF 视图模型。
/// 选项（起止时间 / 帧率 / 宽度 / 颜色 / 抖动 / 循环…）全部存在 <see cref="Cfg"/> 里，
/// 由框架自动写入 <c>Config\video-to-gif\config.json</c>；本类只负责业务逻辑（探测 / 转换 / 进度 / 日志）。
/// </summary>
public class VideoToGifViewModel : ToolViewModelBase
{
    private CancellationTokenSource? _cts;
    private string? _ffmpegPath;
    private int _sourceWidth;           // 探测到的源视频宽度（0 = 未知，按不缩放处理）
    private string? _paletteTempPath;   // 调色板临时 PNG（转换结束删除）

    public VideoToGifViewModel() : base(VideoToGifConfig.ToolId)
    {
        Cfg = LoadConfig<VideoToGifConfig>();

        BrowseInputCommand = new RelayCommand(_ => BrowseInput());
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        BrowseFFmpegCommand = new RelayCommand(_ => BrowseFFmpeg());
        OpenFfmpegDownloadCommand = new RelayCommand(_ => OpenFfmpegDownload());
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
        ConvertCommand = new AsyncRelayCommand(_ => ConvertAsync());
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);

        Cfg.PropertyChanged += OnConfigChanged;

        Log($"配置文件：{Cfg.FilePath}");
        _ = InitFFmpegAsync();
    }

    /// <summary>本功能的所有选项（绑定到界面，改动自动落盘）。</summary>
    public VideoToGifConfig Cfg { get; }

    // ---------- 选项列表（界面用） ----------

    public IReadOnlyList<int> FpsOptions { get; } = new[] { 10, 12, 15, 20, 24, 30 };

    public IReadOnlyList<string> WidthOptions { get; } = new[] { "原宽", "320", "480", "640", "800", "960", "1280" };

    public IReadOnlyList<OptionItem<int>> ColorOptions { get; } = OptionItem.From(
        (256, "256（最佳）"), (128, "128（均衡）"), (64, "64（小体积）"), (32, "32（最小）"));

    public IReadOnlyList<OptionItem<string>> DitherOptions { get; } = OptionItem.From(
        ("floyd_steinberg", "Floyd–Steinberg（细腻）"),
        ("bayer", "Bayer（有序）"),
        ("sierra2", "Sierra2"),
        ("sierra2_4a", "Sierra2 4a（轻快）"),
        ("none", "不抖动"));

    /// <summary>下拉框选中项与配置值之间的桥接。</summary>
    public OptionItem<int> SelectedColors
    {
        get => OptionItem<int>.Find(ColorOptions, Cfg.Colors) ?? ColorOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Colors = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<string> SelectedDither
    {
        get => OptionItem<string>.Find(DitherOptions, Cfg.Dither) ?? DitherOptions[0];
        set
        {
            if (value is null) return;
            Cfg.Dither = value.Value;
            OnPropertyChanged();
        }
    }

    // ---------- 宽度下拉：文本 ↔ 数值 ----------

    /// <summary>宽度下拉选中的文本（"原宽" / "480" …），与 <see cref="VideoToGifConfig.Width"/> 双向映射。</summary>
    public string SelectedWidth
    {
        get => Cfg.Width <= 0 ? "原宽" : Cfg.Width.ToString(CultureInfo.InvariantCulture);
        set
        {
            var width = value == "原宽" || string.IsNullOrEmpty(value) ? 0
                : int.TryParse(value, out var w) ? w : 0;
            if (Cfg.Width == width) return;
            Cfg.Width = width;
            OnPropertyChanged();
        }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoToGifConfig.Colors):
                OnPropertyChanged(nameof(SelectedColors));
                break;
            case nameof(VideoToGifConfig.Dither):
                OnPropertyChanged(nameof(SelectedDither));
                break;
            case nameof(VideoToGifConfig.Width):
                OnPropertyChanged(nameof(SelectedWidth));
                break;
        }
        OnPropertyChanged(nameof(SummaryText));
    }

    /// <summary>参数摘要（界面状态文案用）。</summary>
    public string SummaryText => Cfg.SummaryText;

    // ---------- FFmpeg 状态 ----------

    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    public string FFmpegStatusText { get => _ffmpegStatusText; set => Set(ref _ffmpegStatusText, value); }

    // ---------- 命令 ----------

    public RelayCommand BrowseInputCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand BrowseFFmpegCommand { get; }
    public RelayCommand OpenFfmpegDownloadCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public AsyncRelayCommand ConvertCommand { get; }
    public RelayCommand CancelCommand { get; }

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
        AppSettings.Settings.FFmpegPath ??= found;
        AppSettings.Save();
        SetFFmpegStatus(found, version);
    }

    private void SetFFmpegStatus(string? path, string? version)
    {
        void Update()
        {
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
            InitialDirectory = _ffmpegPath is not null
                ? Path.GetDirectoryName(_ffmpegPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
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
        var dlg = new OpenFileDialog
        {
            Title = "选择源视频文件",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.m4v;*.flv;*.ts;*.mts;*.wmv|所有文件|*.*",
            CheckFileExists = true,
        };
        try
        {
            if (!string.IsNullOrWhiteSpace(Cfg.InputFile) && File.Exists(Cfg.InputFile))
                dlg.InitialDirectory = Path.GetDirectoryName(Cfg.InputFile);
        }
        catch { /* 忽略 */ }
        if (dlg.ShowDialog() != true) return;

        Cfg.InputFile = dlg.FileName;
        _ = ProbeSourceWidthAsync(dlg.FileName);
        StatusText = "已选择源文件";
    }

    private void BrowseOutput()
    {
        var dlg = new OpenFolderDialog { Title = "选择输出目录", Multiselect = false };
        if (Directory.Exists(Cfg.OutputFolder)) dlg.InitialDirectory = Cfg.OutputFolder;
        if (dlg.ShowDialog() == true) Cfg.OutputFolder = dlg.FolderName;
    }

    private string ResolveOutputFolder()
    {
        if (!string.IsNullOrWhiteSpace(Cfg.OutputFolder) && Directory.Exists(Cfg.OutputFolder))
            return Cfg.OutputFolder;
        try { return Path.GetDirectoryName(Cfg.InputFile) ?? ""; }
        catch { return ""; }
    }

    /// <summary>用 ffprobe 探测源视频宽度（缩放参数需要它判断「只缩不放」）。</summary>
    private async Task ProbeSourceWidthAsync(string input)
    {
        if (_ffmpegPath is null) return;
        var ffprobe = FFmpegLocator.FindFFprobe(_ffmpegPath);
        if (ffprobe is null) return;
        try
        {
            var result = await FFmpegRunner.RunAsync(ffprobe, new[]
            {
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width",
                "-of", "default=noprint_wrappers=1:nokey=1",
                input,
            });
            var text = result.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            _sourceWidth = int.TryParse(text, out var w) ? w : 0;
        }
        catch { _sourceWidth = 0; }
    }

    private async Task ConvertAsync()
    {
        if (_ffmpegPath is null)
        {
            StatusText = "请先配置 FFmpeg（未检测到，将打开选择窗口）";
            BrowseFFmpeg();
            return;
        }
        var error = GifConverter.Validate(Cfg.InputFile);
        if (error is not null)
        {
            StatusText = error;
            return;
        }

        var outputFolder = ResolveOutputFolder();
        try { Directory.CreateDirectory(outputFolder); }
        catch { StatusText = "无法创建输出目录"; return; }

        var input = Cfg.InputFile;
        // 源宽未知时补一次探测（转换前必须先知道，否则 scale 可能把画面放大）
        if (_sourceWidth <= 0) await ProbeSourceWidthAsync(input);

        var outputPath = Path.Combine(outputFolder, GifConverter.BuildOutputName(Path.GetFileName(input)));
        outputPath = MakeUniquePath(outputPath);
        _paletteTempPath = Path.Combine(Path.GetTempPath(), "ylt-gif-palette-" + Guid.NewGuid().ToString("N") + ".png");

        var gif = GifSettings.Normalize(Cfg.Fps, Cfg.Width, Cfg.Colors, Cfg.Dither,
            Cfg.Loop, Cfg.StartSeconds, Cfg.DurationSeconds);

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        ProgressText = "0%";
        StatusText = "正在转换…";
        var sw = Stopwatch.StartNew();

        Log("===== 开始视频转 GIF =====");
        Log($"源文件：{input}（宽 {( _sourceWidth > 0 ? _sourceWidth.ToString() : "未知")}）");
        Log($"输出：{outputPath}");
        Log($"参数：{gif.Fps} FPS · 宽 {(gif.Width <= 0 ? "原宽" : gif.Width.ToString())} · {gif.Colors} 色 · 抖动 {gif.Dither} · "
            + $"起点 {GifConverter.FormatTime(gif.StartSeconds)} · 时长 {(gif.DurationSeconds > 0 ? GifConverter.FormatTime(gif.DurationSeconds) : "到结尾")} · {(gif.Loop ? "无限循环" : "只播一次")}");

        try
        {
            // 第一遍：生成调色板
            StatusText = "第一遍：生成调色板…";
            Log("第一遍：palettegen");
            var paletteResult = await FFmpegRunner.RunAsync(_ffmpegPath,
                GifConverter.BuildPaletteArgs(input, _paletteTempPath, gif, _sourceWidth), _cts.Token);
            if (!paletteResult.Success)
            {
                var tail = Tail(paletteResult.Error);
                Log($"  ✗ 调色板生成失败：{tail}");
                StatusText = "调色板生成失败（详见日志）";
                return;
            }

            // 第二遍：合成 GIF（带进度）
            StatusText = "第二遍：合成 GIF…";
            Log("第二遍：paletteuse");
            var duration = gif.DurationSeconds;
            var lastUi = 0L;
            var gifResult = await FFmpegRunner.RunAsync(_ffmpegPath,
                GifConverter.BuildGifArgs(input, _paletteTempPath, outputPath, gif, _sourceWidth),
                _cts.Token,
                onOutputLine: line =>
                {
                    // -progress pipe:1 把进度写到 stdout：out_time_us=…
                    var sec = GifConverter.ParseProgressSeconds(line);
                    if (sec is null || duration <= 0) return;
                    var now = Environment.TickCount64;
                    if (now - Volatile.Read(ref lastUi) < 100) return;
                    Volatile.Write(ref lastUi, now);
                    var pct = Math.Clamp(sec.Value / duration, 0, 1);
                    Ui(() =>
                    {
                        Progress = pct;
                        ProgressText = $"{pct * 100:F0}%";
                    });
                });

            if (!gifResult.Success)
            {
                var tail = Tail(gifResult.Error);
                Log($"  ✗ 合成失败：{tail}");
                StatusText = "合成失败（详见日志）";
                return;
            }

            Progress = 1;
            ProgressText = "100%";
            sw.Stop();
            Log($"===== 完成：{outputPath}，用时 {sw.Elapsed.TotalSeconds:F1} 秒 =====");
            StatusText = $"✅ 转换完成：{Path.GetFileName(outputPath)}（用时 {sw.Elapsed.TotalSeconds:F1} 秒）";
        }
        catch (OperationCanceledException)
        {
            Log("已取消。");
            StatusText = "已取消";
        }
        catch (Exception ex)
        {
            Log($"  ✗ 出错：{ex.Message}");
            StatusText = "转换出错（详见日志）";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            CleanupPalette();
        }
    }

    private static string Tail(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "（无错误输出）";
        return string.Join(" | ", error.Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .TakeLast(3));
    }

    /// <summary>输出文件已存在时自动加 <c> (2)</c>、<c> (3)</c>，不覆盖已有文件。</summary>
    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 10000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return path;
    }

    private void CleanupPalette()
    {
        if (_paletteTempPath is null) return;
        try { if (File.Exists(_paletteTempPath)) File.Delete(_paletteTempPath); }
        catch { /* 删不掉留给清理缓存 */ }
        _paletteTempPath = null;
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

    /// <summary>工具关闭：取消进行中的转换、删掉临时调色板。</summary>
    protected override void OnClosed()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        CleanupPalette();
    }
}
