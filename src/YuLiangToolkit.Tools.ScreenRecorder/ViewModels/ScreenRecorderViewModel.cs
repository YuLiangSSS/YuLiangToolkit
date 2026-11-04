using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ScreenRecorder.Services;
using YuLiangToolkit.Tools.ScreenRecorder.Views;

namespace YuLiangToolkit.Tools.ScreenRecorder.ViewModels;

/// <summary>「窗口」来源下拉框里的一项。</summary>
public sealed class WindowOption
{
    /// <summary>窗口标题。</summary>
    public required string Title { get; init; }

    /// <summary>ComboBox 没写 DisplayMemberPath 时也能显示标题。</summary>
    public override string ToString() => Title;
}

/// <summary>「最近录制」列表里的一项。</summary>
public sealed class RecordingItem
{
    /// <summary>完整路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>文件名。</summary>
    public required string FileName { get; init; }

    /// <summary>GIF / MP4 / PNG。</summary>
    public required string FormatText { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>生成时间（录制开始时间）。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>录制时长文案（截图 / 未知时为空）。</summary>
    public string DurationText { get; init; } = "";

    /// <summary>大小文案。</summary>
    public string SizeText => FileSize.Format(SizeBytes);

    /// <summary>列表右侧的摘要：<c>GIF · 1.2 MB · 12:03:45</c>。</summary>
    public string SummaryText => $"{FormatText} · {SizeText} · {CreatedAt:HH:mm:ss}";
}

/// <summary>
/// 「录屏」视图模型：采集（gdigrab）→ 停止 → 生成 GIF / MP4，另外支持 PNG 截图、区域框选、
/// 窗口选择、倒计时、置顶控制条、F8 全局停止热键、最近录制列表。
///
/// <para><b>线程</b>：命令都在 UI 线程执行；FFmpeg 采集 / 导出在后台进程里跑。
/// 界面状态修改通过基类的 <see cref="ToolViewModelBase.Ui"/> 兜底，导出进度用 <see cref="Progress{T}"/>
/// 自动回 UI 线程。</para>
///
/// <para><b>退出</b>：<see cref="OnClosed"/> 会先向 FFmpeg 送 q 正常收尾；如果正在录制，
/// 会在后台线程尽量把文件生成完（最多等 20 秒），失败则把中间素材留在 <c>Cache\screen-recorder\</c>。</para>
/// </summary>
public sealed class ScreenRecorderViewModel : ToolViewModelBase
{
    private const string SourceFullScreen = "fullscreen";
    private const string SourceRegion = "region";
    private const string SourceWindow = "window";
    private const string FormatGif = "gif";
    private const string FormatMp4 = "mp4";
    private const int StopHotkeyId = 0x5C52;
    private const int MaxRecentItems = 30;

    private readonly ToolCache _cache = ToolCache.For(ScreenRecorderConfig.ToolId);
    private readonly GlobalHotkey _stopHotkey = new(StopHotkeyId);
    private readonly DispatcherTimer _elapsedTimer;

    private string? _ffmpegPath;
    private ScreenCaptureSession? _session;
    private CancellationTokenSource? _countdownCts;
    private CancellationTokenSource? _exportCts;
    private RecordingOverlayWindow? _overlay;
    private CountdownWindow? _countdownWindow;
    private bool _shuttingDown;
    private bool _stopInProgress;
    private bool _cancelRequested;
    private bool _hotkeyActive;
    private Task? _finalizeTask;
    private DateTime _startedAt;
    private TimeSpan _recordedDuration;

    // 录制开始时把所有选项拍个快照：录制中界面会禁用选项，退出时也还能按快照收尾
    private CapturePlan? _activePlan;
    private GifExportSettings? _activeGif;
    private Mp4ExportSettings? _activeMp4;
    private string _activeFormat = FormatGif;
    private string _activeOutputPath = "";
    private string _activeTempPath = "";
    private bool _activeOpenFolder;

    private bool _isRecording;
    private bool _isFinalizing;
    private string _elapsedText = "00:00";
    private string _stopHotkeyState = "";
    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    private string _windowCountText = "";
    private string _lastOutputPath = "";
    private RecordingItem? _selectedRecording;

    public ScreenRecorderViewModel() : base(ScreenRecorderConfig.ToolId)
    {
        Cfg = LoadConfig<ScreenRecorderConfig>();
        NormalizeConfigValues();

        SourceOptions = OptionItem.From(
            (SourceFullScreen, "全屏（主显示器）"),
            (SourceRegion, "区域（拖框选择，推荐 GIF）"),
            (SourceWindow, "窗口（录这个窗口的位置）"));

        FormatOptions = OptionItem.From(
            (FormatGif, "动图 GIF（默认，两遍调色板）"),
            (FormatMp4, "视频 MP4（H.264，体积更小）"));

        FpsOptions = OptionItem.FromValues(RecorderOptions.FpsChoices, value => value switch
        {
            5 => "5 FPS（文件最小）",
            10 => "10 FPS（GIF 常用）",
            15 => "15 FPS（推荐）",
            20 => "20 FPS",
            24 => "24 FPS（接近电影）",
            _ => "30 FPS（最流畅）",
        });

        WidthOptions = OptionItem.FromValues(RecorderOptions.WidthChoices,
            value => value <= 0 ? "原始分辨率" : $"{value} px 宽");

        ColorOptions = OptionItem.FromValues(RecorderOptions.GifColorChoices, value => value switch
        {
            256 => "256 色（最像原图）",
            128 => "128 色（平衡）",
            _ => "64 色（最小）",
        });

        DitherOptions = OptionItem.From(
            ("floyd_steinberg", "Floyd–Steinberg（平滑渐变）"),
            ("bayer", "有序（像素风 / 文件更小）"),
            ("none", "无（色块明显 / 最小）"));

        QualityOptions = OptionItem.FromValues(RecorderOptions.QualityChoices, value => value switch
        {
            18 => "高画质（CRF 18）",
            23 => "标准（CRF 23）",
            _ => "小体积（CRF 28）",
        });

        CountdownOptions = OptionItem.FromValues(RecorderOptions.CountdownChoices,
            value => value <= 0 ? "不等待，立刻开始" : $"{value} 秒" + (value == 3 ? "（推荐）" : ""));

        MaxDurationOptions = OptionItem.FromValues(RecorderOptions.MaxSecondsChoices, value => value switch
        {
            0 => "不限制",
            30 => "最长 30 秒",
            60 => "最长 1 分钟",
            120 => "最长 2 分钟",
            _ => "最长 5 分钟",
        });

        _elapsedTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _elapsedTimer.Tick += (_, _) => UpdateElapsedText();

        ToggleRecordingCommand = new AsyncRelayCommand(_ => ToggleRecordingAsync(), _ => !IsFinalizing);
        SelectRegionCommand = new RelayCommand(_ => SelectRegion(), _ => IsIdle);
        RefreshWindowsCommand = new RelayCommand(_ => RefreshWindows(), _ => IsIdle);
        ScreenshotCommand = new AsyncRelayCommand(_ => TakeScreenshotAsync(), _ => IsIdle);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder(), _ => IsIdle);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
        OpenRecordingCommand = new RelayCommand(parameter => OpenRecording(parameter as RecordingItem));
        RevealRecordingCommand = new RelayCommand(parameter => RevealRecording(parameter as RecordingItem));
        ClearRecordingsCommand = new RelayCommand(_ => Recordings.Clear());
        BrowseFFmpegCommand = new RelayCommand(_ => BrowseFFmpeg());
        OpenFfmpegDownloadCommand = new RelayCommand(_ => OpenFfmpegDownload());
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        CopyLastPathCommand = new RelayCommand(_ => CopyLastPath(), _ => _lastOutputPath.Length > 0);
        CancelFinalizeCommand = new RelayCommand(_ => CancelFinalize(), _ => IsFinalizing);

        Cfg.PropertyChanged += OnConfigChanged;

        Log($"配置文件：{Cfg.FilePath}");
        Log($"录制素材缓存：{_cache.Directory}");
        Log($"输出文件夹：{ResolveOutputFolder()}");
        _ = InitFFmpegAsync();
        if (Cfg.IsWindowSource) RefreshWindows();
    }

    // ================================================================== 配置 / 选项

    /// <summary>本功能全部选项（绑定到界面，改动自动落盘）。</summary>
    public ScreenRecorderConfig Cfg { get; }

    public IReadOnlyList<OptionItem<string>> SourceOptions { get; }
    public IReadOnlyList<OptionItem<string>> FormatOptions { get; }
    public IReadOnlyList<OptionItem<int>> FpsOptions { get; }
    public IReadOnlyList<OptionItem<int>> WidthOptions { get; }
    public IReadOnlyList<OptionItem<int>> ColorOptions { get; }
    public IReadOnlyList<OptionItem<string>> DitherOptions { get; }
    public IReadOnlyList<OptionItem<int>> QualityOptions { get; }
    public IReadOnlyList<OptionItem<int>> CountdownOptions { get; }
    public IReadOnlyList<OptionItem<int>> MaxDurationOptions { get; }

    /// <summary>可录制的窗口。</summary>
    public ObservableCollection<WindowOption> Windows { get; } = new();

    /// <summary>最近生成的文件（本次运行内，最多 30 条）。</summary>
    public ObservableCollection<RecordingItem> Recordings { get; } = new();

    public OptionItem<string> SelectedSource
    {
        get => OptionItem<string>.Find(SourceOptions, CanonicalSource(Cfg.SourceKind)) ?? SourceOptions[1];
        set
        {
            if (value is null || value.Value == Cfg.SourceKind) return;
            Cfg.SourceKind = value.Value;
            OnPropertyChanged();
            if (value.Value == SourceWindow) RefreshWindows();
        }
    }

    public OptionItem<string> SelectedFormat
    {
        get => OptionItem<string>.Find(FormatOptions, RecorderOptions.NormalizeFormat(Cfg.Format)) ?? FormatOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.Format) return;
            Cfg.Format = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedFps
    {
        get => OptionItem<int>.Find(FpsOptions, RecorderOptions.NormalizeFps(Cfg.Fps)) ?? FpsOptions[2];
        set
        {
            if (value is null || value.Value == Cfg.Fps) return;
            Cfg.Fps = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedWidth
    {
        get => OptionItem<int>.Find(WidthOptions, RecorderOptions.NormalizeScaleWidth(Cfg.ScaleWidth)) ?? WidthOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.ScaleWidth) return;
            Cfg.ScaleWidth = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedColors
    {
        get => OptionItem<int>.Find(ColorOptions, RecorderOptions.NormalizeColors(Cfg.GifColors)) ?? ColorOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.GifColors) return;
            Cfg.GifColors = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<string> SelectedDither
    {
        get => OptionItem<string>.Find(DitherOptions, RecorderOptions.NormalizeDither(Cfg.GifDither)) ?? DitherOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.GifDither) return;
            Cfg.GifDither = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedQuality
    {
        get => OptionItem<int>.Find(QualityOptions, RecorderOptions.NormalizeCrf(Cfg.Mp4Quality)) ?? QualityOptions[1];
        set
        {
            if (value is null || value.Value == Cfg.Mp4Quality) return;
            Cfg.Mp4Quality = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedCountdown
    {
        get => OptionItem<int>.Find(CountdownOptions, RecorderOptions.NormalizeCountdown(Cfg.CountdownSeconds)) ?? CountdownOptions[1];
        set
        {
            if (value is null || value.Value == Cfg.CountdownSeconds) return;
            Cfg.CountdownSeconds = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedMaxDuration
    {
        get => OptionItem<int>.Find(MaxDurationOptions, RecorderOptions.NormalizeMaxSeconds(Cfg.MaxSeconds)) ?? MaxDurationOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.MaxSeconds) return;
            Cfg.MaxSeconds = value.Value;
            OnPropertyChanged();
        }
    }

    public WindowOption? SelectedWindow
    {
        get => Windows.FirstOrDefault(w => string.Equals(w.Title, Cfg.WindowTitle, StringComparison.CurrentCultureIgnoreCase));
        set
        {
            if (value is null || string.Equals(value.Title, Cfg.WindowTitle, StringComparison.CurrentCultureIgnoreCase)) return;
            Cfg.WindowTitle = value.Title;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceSummary));
        }
    }

    // ================================================================== 状态

    /// <summary>是不是正在录（已经开始采集）。</summary>
    public bool IsRecording
    {
        get => _isRecording;
        private set
        {
            if (!Set(ref _isRecording, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(RecordButtonText));
            OnPropertyChanged(nameof(StopHintText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>是不是正在把中间素材转成 GIF / MP4。</summary>
    public bool IsFinalizing
    {
        get => _isFinalizing;
        private set
        {
            if (!Set(ref _isFinalizing, value)) return;
            OnPropertyChanged(nameof(IsIdle));
            OnPropertyChanged(nameof(RecordButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>空闲（既没在录、也没在生成文件）。</summary>
    public bool IsIdle => !IsRecording && !IsFinalizing;

    /// <summary>主按钮文案。</summary>
    public string RecordButtonText => IsRecording ? "■ 停止录制" : IsFinalizing ? "正在生成文件…" : "● 开始录制";

    /// <summary>录制计时文案。</summary>
    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value);
    }

    /// <summary>停止提示（热键开了没 / 注册成功没）。</summary>
    public string StopHintText
    {
        get
        {
            if (!Cfg.UseStopHotkey) return "点控制条 / 本页按钮停止（F8 热键已关闭）";
            return IsRecording && !_hotkeyActive
                ? "F8 未注册成功：请用控制条 / 本页按钮停止"
                : "F8 停止";
        }
    }

    /// <summary>录制中热键状态（注册失败时告诉用户）。</summary>
    public string StopHotkeyState
    {
        get => _stopHotkeyState;
        private set => Set(ref _stopHotkeyState, value);
    }

    /// <summary>FFmpeg 状态文案。</summary>
    public string FFmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => Set(ref _ffmpegStatusText, value);
    }

    /// <summary>窗口列表数量提示。</summary>
    public string WindowCountText
    {
        get => _windowCountText;
        private set => Set(ref _windowCountText, value);
    }

    /// <summary>最近一次成功生成的文件路径（「复制路径」用）。</summary>
    public string LastOutputPath
    {
        get => _lastOutputPath;
        private set => Set(ref _lastOutputPath, value);
    }

    /// <summary>最近生成列表里选中的一项（打开 / 定位按钮用）。</summary>
    public RecordingItem? SelectedRecording
    {
        get => _selectedRecording;
        set => Set(ref _selectedRecording, value);
    }

    /// <summary>录制来源摘要（全屏 / 区域尺寸 / 窗口标题）。</summary>
    public string SourceSummary
    {
        get
        {
            switch (RecorderOptions.ParseSourceKind(Cfg.SourceKind))
            {
                case CaptureSourceKind.FullScreen:
                    return "全屏 · " + NativeScreen.PrimaryScreen.SizeText;
                case CaptureSourceKind.Window:
                    return string.IsNullOrWhiteSpace(Cfg.WindowTitle) ? "窗口 · 还没选择" : "窗口 · " + Cfg.WindowTitle;
                default:
                    return Cfg.HasRegion ? $"区域 · {Cfg.RegionWidth}×{Cfg.RegionHeight}" : "区域 · 还没框选";
            }
        }
    }

    /// <summary>已选区域文案。</summary>
    public string RegionText => Cfg.HasRegion
        ? $"({Cfg.RegionX}, {Cfg.RegionY}) {Cfg.RegionWidth}×{Cfg.RegionHeight}"
        : "还没有框选区域";

    /// <summary>当前帧率 / 格式 / 宽度的摘要。</summary>
    public string CaptureSummary
    {
        get
        {
            var fps = RecorderOptions.NormalizeFps(Cfg.Fps);
            var width = RecorderOptions.NormalizeScaleWidth(Cfg.ScaleWidth);
            var widthText = width <= 0 ? "原始分辨率" : $"{width} px 宽";
            return Cfg.IsGif
                ? $"动图 GIF · {fps} FPS · {widthText} · {RecorderOptions.NormalizeColors(Cfg.GifColors)} 色"
                : $"视频 MP4 · {fps} FPS · {widthText} · 画质 CRF {RecorderOptions.NormalizeCrf(Cfg.Mp4Quality)}";
        }
    }

    // ================================================================== 命令

    public AsyncRelayCommand ToggleRecordingCommand { get; }
    public RelayCommand SelectRegionCommand { get; }
    public RelayCommand RefreshWindowsCommand { get; }
    public AsyncRelayCommand ScreenshotCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand OpenRecordingCommand { get; }
    public RelayCommand RevealRecordingCommand { get; }
    public RelayCommand ClearRecordingsCommand { get; }
    public RelayCommand BrowseFFmpegCommand { get; }
    public RelayCommand OpenFfmpegDownloadCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand CopyLastPathCommand { get; }
    public RelayCommand CancelFinalizeCommand { get; }

    // ================================================================== 配置联动

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScreenRecorderConfig.SourceKind):
                OnPropertyChanged(nameof(SelectedSource));
                OnPropertyChanged(nameof(IsRegionSource));
                OnPropertyChanged(nameof(IsWindowSource));
                if (Cfg.IsWindowSource) RefreshWindows();
                break;
            case nameof(ScreenRecorderConfig.Format):
                OnPropertyChanged(nameof(SelectedFormat));
                OnPropertyChanged(nameof(IsGif));
                OnPropertyChanged(nameof(IsMp4));
                break;
            case nameof(ScreenRecorderConfig.Fps):
                OnPropertyChanged(nameof(SelectedFps));
                break;
            case nameof(ScreenRecorderConfig.ScaleWidth):
                OnPropertyChanged(nameof(SelectedWidth));
                break;
            case nameof(ScreenRecorderConfig.GifColors):
                OnPropertyChanged(nameof(SelectedColors));
                break;
            case nameof(ScreenRecorderConfig.GifDither):
                OnPropertyChanged(nameof(SelectedDither));
                break;
            case nameof(ScreenRecorderConfig.Mp4Quality):
                OnPropertyChanged(nameof(SelectedQuality));
                break;
            case nameof(ScreenRecorderConfig.CountdownSeconds):
                OnPropertyChanged(nameof(SelectedCountdown));
                break;
            case nameof(ScreenRecorderConfig.MaxSeconds):
                OnPropertyChanged(nameof(SelectedMaxDuration));
                break;
            case nameof(ScreenRecorderConfig.WindowTitle):
                OnPropertyChanged(nameof(SelectedWindow));
                break;
            case nameof(ScreenRecorderConfig.UseStopHotkey):
                OnPropertyChanged(nameof(StopHintText));
                break;
        }

        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(CaptureSummary));
        OnPropertyChanged(nameof(IsGif));
        OnPropertyChanged(nameof(IsMp4));
        OnPropertyChanged(nameof(IsRegionSource));
        OnPropertyChanged(nameof(IsWindowSource));
    }

    /// <summary>格式变了（IsGif 影响两块设置的显示）。</summary>
    public bool IsGif => Cfg.IsGif;

    /// <summary>当前格式是不是 MP4。</summary>
    public bool IsMp4 => !Cfg.IsGif;

    /// <summary>来源是不是区域。</summary>
    public bool IsRegionSource => Cfg.IsRegionSource;

    /// <summary>来源是不是窗口。</summary>
    public bool IsWindowSource => Cfg.IsWindowSource;

    // ================================================================== 初始化

    private void NormalizeConfigValues()
    {
        RecorderOptions.NormalizeConfig(Cfg);

        if (string.IsNullOrWhiteSpace(Cfg.OutputFolder))
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Cfg.OutputFolder = Path.Combine(videos, "YuLiangToolkit 录屏");
        }
    }

    private static string CanonicalSource(string? value)
        => RecorderOptions.ParseSourceKind(value) switch
        {
            CaptureSourceKind.FullScreen => SourceFullScreen,
            CaptureSourceKind.Window => SourceWindow,
            _ => SourceRegion,
        };

    private async Task InitFFmpegAsync()
    {
        var found = FFmpegLocator.Find(AppSettings.Settings.FFmpegPath);
        if (found is null)
        {
            SetFFmpegStatus(null, null);
            return;
        }

        var version = await FFmpegRunner.GetVersionAsync(found);
        _ffmpegPath = found;
        AppSettings.Settings.FFmpegPath ??= found;
        AppSettings.Save();
        SetFFmpegStatus(found, version);
    }

    private void SetFFmpegStatus(string? path, string? version)
    {
        Ui(() =>
        {
            _ffmpegPath = path;
            FFmpegStatusText = path is null
                ? "⚠️ 未检测到 FFmpeg：请「选择 FFmpeg…」指定 ffmpeg.exe，或「下载 FFmpeg」后重试（录屏和 GIF 合成都要用）"
                : $"✅ FFmpeg 已就绪（v{version ?? "?"}）· {path}";
        });
    }

    private async Task<bool> EnsureFFmpegAsync()
    {
        if (_ffmpegPath is not null && File.Exists(_ffmpegPath)) return true;

        var found = FFmpegLocator.Find(AppSettings.Settings.FFmpegPath);
        if (found is not null && File.Exists(found))
        {
            _ffmpegPath = found;
            AppSettings.Settings.FFmpegPath = found;
            AppSettings.Save();
            SetFFmpegStatus(found, await FFmpegRunner.GetVersionAsync(found));
            return true;
        }

        StatusText = "未检测到 FFmpeg：请先指定 ffmpeg.exe（录屏 / 转 GIF 都靠它）";
        BrowseFFmpeg();
        return _ffmpegPath is not null && File.Exists(_ffmpegPath);
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
        _ = Task.Run(async () => SetFFmpegStatus(path, await FFmpegRunner.GetVersionAsync(path)));
    }

    private void OpenFfmpegDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.gyan.dev/ffmpeg/builds/",
                UseShellExecute = true,
            });
        }
        catch
        {
            StatusText = "无法打开浏览器，请手动访问 https://www.gyan.dev/ffmpeg/builds/";
        }
    }

    // ================================================================== 来源 / 区域 / 窗口

    private void SelectRegion()
    {
        if (!IsIdle) return;

        var picker = new RegionPickerWindow();
        if (Cfg.HasRegion)
            picker.UseInitialRegion(new CaptureRect(Cfg.RegionX, Cfg.RegionY, Cfg.RegionWidth, Cfg.RegionHeight));

        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsLoaded) picker.Owner = owner;

        if (picker.ShowDialog() != true || picker.SelectedRegion is not { } region)
        {
            StatusText = "已取消框选";
            return;
        }

        Cfg.SourceKind = SourceRegion;
        Cfg.RegionX = region.X;
        Cfg.RegionY = region.Y;
        Cfg.RegionWidth = region.Width;
        Cfg.RegionHeight = region.Height;
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(SourceSummary));
        StatusText = $"已选择录制区域：{region}";
        Log($"框选区域：{region}");
    }

    private void RefreshWindows()
    {
        Windows.Clear();
        foreach (var entry in NativeScreen.EnumerateWindows())
            Windows.Add(new WindowOption { Title = entry.Title });

        WindowCountText = Windows.Count == 0
            ? "没有找到可录制的窗口（最小化的窗口不显示）"
            : $"共 {Windows.Count} 个窗口；录制的是它当前的屏幕矩形，窗口移动后不会跟着走";
        OnPropertyChanged(nameof(SelectedWindow));
    }

    private CaptureRect? ResolveCaptureRegion(bool promptForRegion)
    {
        var kind = RecorderOptions.ParseSourceKind(Cfg.SourceKind);
        if (kind == CaptureSourceKind.FullScreen)
        {
            var screen = NativeScreen.PrimaryScreen;
            if (screen.IsEmpty)
            {
                StatusText = "读不到主显示器尺寸";
                return null;
            }
            return screen.MakeEven();
        }

        if (kind == CaptureSourceKind.Window)
        {
            if (string.IsNullOrWhiteSpace(Cfg.WindowTitle))
            {
                StatusText = "请先选择要录制的窗口（点「刷新窗口」/ 下拉框选一个）";
                if (IsIdle) RefreshWindows();
                return null;
            }

            var bounds = NativeScreen.FindWindowBoundsByTitle(Cfg.WindowTitle);
            if (bounds is null)
            {
                StatusText = $"找不到窗口「{Cfg.WindowTitle}」：可能已关闭 / 最小化，请重新选择";
                RefreshWindows();
                return null;
            }

            var windowRect = bounds.Value.ClampTo(NativeScreen.VirtualScreen).MakeEven();
            if (windowRect.IsEmpty || windowRect.Width < 16 || windowRect.Height < 16)
            {
                StatusText = "窗口不可见或太小，无法录制";
                return null;
            }
            return windowRect;
        }

        // 区域
        if (!Cfg.HasRegion)
        {
            if (!promptForRegion)
            {
                StatusText = "请先框选录制区域";
                return null;
            }
            SelectRegion();
            if (!Cfg.HasRegion) return null;
        }

        var region = new CaptureRect(Cfg.RegionX, Cfg.RegionY, Cfg.RegionWidth, Cfg.RegionHeight)
            .ClampTo(NativeScreen.VirtualScreen)
            .MakeEven();
        if (region.IsEmpty || region.Width < 16 || region.Height < 16)
        {
            StatusText = "上次的录制区域已经不在屏幕上（或太小），请重新框选";
            return null;
        }
        return region;
    }

    // ================================================================== 录制主流程

    private async Task ToggleRecordingAsync()
    {
        if (IsRecording)
        {
            await StopFromUiAsync();
            return;
        }

        if (IsFinalizing) return;
        await StartRecordingAsync();
    }

    private async Task StartRecordingAsync()
    {
        try
        {
            if (!await EnsureFFmpegAsync()) return;

            var region = ResolveCaptureRegion(promptForRegion: true);
            if (region is null) return;

            var plan = new CapturePlan(
                region.Value.MakeEven(),
                RecorderOptions.NormalizeFps(Cfg.Fps),
                Cfg.DrawMouse,
                RecorderOptions.NormalizeMaxSeconds(Cfg.MaxSeconds),
                RecorderOptions.SourceLabel(RecorderOptions.ParseSourceKind(Cfg.SourceKind)));

            if (plan.Width < 16 || plan.Height < 16)
            {
                StatusText = "录制区域太小（至少 16×16）";
                return;
            }

            if (!await RunCountdownAsync(Cfg.CountdownSeconds)) return;

            var format = RecorderOptions.NormalizeFormat(Cfg.Format);
            var outputFolder = ResolveOutputFolder();
            try
            {
                Directory.CreateDirectory(outputFolder);
            }
            catch (Exception ex)
            {
                StatusText = "无法创建输出文件夹：" + ex.Message;
                return;
            }

            _cache.EnsureCreated();
            var tempPath = _cache.PathOf($"capture-{DateTime.Now:yyyyMMdd-HHmmss-fff}.mkv");
            var extension = format == FormatGif ? "gif" : "mp4";
            var outputPath = RecordingNames.BuildOutputPath(
                outputFolder, RecordingNames.SanitizePrefix(Cfg.FilePrefix), DateTime.Now, extension);

            // 拍快照：录制中不允许改选项（界面会禁用），收尾 / 退出都用这份
            _activePlan = plan;
            _activeFormat = format;
            _activeGif = GifExportSettings.Normalize(Cfg.Fps, Cfg.ScaleWidth, Cfg.GifColors, Cfg.GifDither, Cfg.GifLoop);
            _activeMp4 = Mp4ExportSettings.Normalize(Cfg.ScaleWidth, Cfg.Mp4Quality);
            _activeOutputPath = outputPath;
            _activeTempPath = tempPath;
            _activeOpenFolder = Cfg.OpenFolderAfterSave;
            _finalizeTask = null;
            _stopInProgress = false;
            _cancelRequested = false;

            try
            {
                _session = ScreenCaptureSession.Start(_ffmpegPath!, plan, tempPath);
            }
            catch (Exception ex)
            {
                StatusText = "启动录制失败：" + ex.Message;
                Log("✗ 启动录制失败：" + ex);
                _session = null;
                return;
            }

            _startedAt = DateTime.Now;
            IsRecording = true;
            ElapsedText = "00:00";
            _elapsedTimer.Start();
            RegisterStopHotkey();
            ShowOverlay(plan, region.Value);
            UpdateElapsedText();
            StatusText = $"录制中… {SourceSummary}（{StopHintText}）";

            Log($"===== 开始录制：{plan.SourceLabel} · {plan.Region} · {plan.Fps} FPS · {format.ToUpperInvariant()} =====");
            if (!Cfg.ShowControlBar && !Cfg.UseStopHotkey)
                Log("提示：控制条和 F8 都关掉了，停止时请回到本页点「停止录制」。");
            if (format == FormatGif)
                Log($"GIF 参数：{_activeGif.MaxColors} 色 · 抖动 {_activeGif.Dither} · 宽度 {_activeGif.ScaleWidth} · {(_activeGif.Loop ? "无限循环" : "只播一次")}");

            _ = WatchCaptureExitAsync(_session);
        }
        catch (Exception ex)
        {
            StatusText = "开始录制失败：" + ex.Message;
            Log("✗ " + ex);
        }
    }

    /// <summary>倒计时浮窗；用户点浮窗 / Esc 取消时返回 false。</summary>
    private async Task<bool> RunCountdownAsync(int seconds)
    {
        seconds = RecorderOptions.NormalizeCountdown(seconds);
        if (seconds <= 0) return true;

        _countdownCts = new CancellationTokenSource();
        var token = _countdownCts.Token;
        _countdownWindow = new CountdownWindow();
        _countdownWindow.CancelRequested += (_, _) =>
        {
            try { if (!token.IsCancellationRequested) _countdownCts?.Cancel(); }
            catch { /* 已结束 */ }
        };
        _countdownWindow.Show();

        try
        {
            for (var remaining = seconds; remaining > 0; remaining--)
            {
                _countdownWindow.SetNumber(remaining);
                StatusText = $"{remaining} 秒后开始录制…（点浮窗 / Esc 取消）";
                await Task.Delay(1000, token);
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消录制";
            return false;
        }
        finally
        {
            var window = _countdownWindow;
            _countdownWindow = null;
            if (window is not null)
            {
                try { window.CloseNow(); } catch { /* 已关闭 */ }
            }
            _countdownCts?.Dispose();
            _countdownCts = null;
        }
    }

    /// <summary>监视采集进程：达到最长时长 / 采集报错时 FFmpeg 会自己退出，这里负责自动收尾。</summary>
    private async Task WatchCaptureExitAsync(ScreenCaptureSession session)
    {
        try { await session.WaitForExitAsync(); }
        catch { /* 取消 / 进程结束 */ }

        try
        {
            await UiAsync(async () =>
            {
                if (_shuttingDown || _stopInProgress || !IsRecording) return;
                if (!ReferenceEquals(_session, session)) return;

                Log("FFmpeg 已自行退出（达到最长录制时长或采集出错），正在自动停止并生成文件…");
                await StopRecordingCoreAsync(autoStopped: true);
            });
        }
        catch (Exception ex)
        {
            Log("自动停止时出错：" + ex.Message);
        }
    }

    private async Task StopFromUiAsync()
    {
        if (!IsRecording || _stopInProgress) return;
        await StopRecordingCoreAsync(autoStopped: false);
    }

    private async Task StopRecordingCoreAsync(bool autoStopped)
    {
        if (_stopInProgress || _shuttingDown) return;
        _stopInProgress = true;
        IsRecording = false;

        // 停止 / 收尾期间也当成“忙碌”：否则 F8 / 控制条停止后，用户手快再点开始，
        // 会在上一个文件还没生成完时覆盖录制快照（两条收尾流程打架）。
        IsFinalizing = true;

        _elapsedTimer.Stop();
        UnregisterStopHotkey();
        HideOverlay();

        var session = _session;
        _session = null;
        try
        {
            StatusText = autoStopped ? "录制已自动停止，正在收尾…" : "正在停止录制…";
            if (session is not null)
            {
                var result = await session.StopAsync(TimeSpan.FromSeconds(8));
                if (!result.Exited) Log("FFmpeg 没有在超时内退出，已强制结束（文件可能不完整）");
                if (result.ExitCode != 0 && result.ExitCode != -1) Log($"FFmpeg 退出码 {result.ExitCode}");
                if (!string.IsNullOrWhiteSpace(result.ErrorTail)) Log("[FFmpeg] " + result.ErrorTail);
            }

            // 录制时长要在“停止采集”这一刻记下来；生成 GIF / MP4 的编码时间不能算进录制时长。
            _recordedDuration = DateTime.Now - _startedAt;
            UpdateElapsedText();

            if (_cancelRequested)
            {
                StatusText = "已取消，未生成文件（中间素材保留在缓存里）";
                Log("已取消生成文件（中间素材保留在：" + _activeTempPath + "）");
                return;
            }

            await FinalizeOnceAsync(autoStopped);
        }
        catch (Exception ex)
        {
            StatusText = "停止录制失败：" + ex.Message;
            Log("✗ 停止录制失败：" + ex);
        }
        finally
        {
            session?.Dispose();
            _stopInProgress = false;
            IsFinalizing = false;
            UpdateElapsedText();
        }
    }

    private Task FinalizeOnceAsync(bool autoStopped) => _finalizeTask ??= FinalizeCoreAsync(autoStopped);

    /// <summary>取消正在进行的 GIF / MP4 生成（FFmpeg 进程会被一起结束，中间素材不删）。</summary>
    private void CancelFinalize()
    {
        _cancelRequested = true;
        try
        {
            if (_exportCts is null)
            {
                StatusText = "正在停止录制，稍后会取消生成…";
                return;
            }

            _exportCts.Cancel();
            StatusText = "正在取消生成文件…";
        }
        catch { /* 已经结束 */ }
    }

    /// <summary>把中间素材转成最终 GIF / MP4（录制结束 / 自动停止都走这里）。</summary>
    private async Task FinalizeCoreAsync(bool autoStopped)
    {
        IsFinalizing = true;
        StatusText = "正在生成文件…";
        IsRecording = false;
        _elapsedTimer.Stop();

        var plan = _activePlan;
        var format = _activeFormat;
        var outputPath = _activeOutputPath;
        var tempPath = _activeTempPath;
        var startedAt = _startedAt;
        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        try
        {
            if (plan is null || string.IsNullOrWhiteSpace(_ffmpegPath))
            {
                StatusText = "生成文件失败：缺少录制参数";
                return;
            }
            if (string.IsNullOrWhiteSpace(tempPath) || !File.Exists(tempPath) || new FileInfo(tempPath).Length == 0)
            {
                StatusText = "录制失败：没有拿到画面（FFmpeg 没有输出，检查录制区域是否在屏幕内）";
                Log("✗ 没有生成中间素材：" + tempPath);
                return;
            }

            var progress = new System.Progress<string>(message =>
            {
                StatusText = message;
                Log(message);
            });

            ExportResult result;
            if (format == FormatGif && _activeGif is not null)
            {
                result = await RecordingExporter.ExportGifAsync(
                    _ffmpegPath, plan.Region, _activeGif, tempPath, outputPath, progress, token);
            }
            else if (_activeMp4 is not null)
            {
                result = await RecordingExporter.ExportMp4Async(
                    _ffmpegPath, plan.Region, _activeMp4, tempPath, outputPath, progress, token);
            }
            else
            {
                StatusText = "生成文件失败：缺少导出参数";
                return;
            }

            if (!result.Success)
            {
                TryDelete(outputPath);   // 失败时不要留下 0 字节 / 截断的半成品
                StatusText = "生成文件失败：" + result.Error;
                Log("✗ " + result.Error);
                Log("中间素材保留在：" + tempPath);
                return;
            }

            var info = new FileInfo(outputPath);
            var duration = RecordingTime.FormatDuration(_recordedDuration);
            var item = new RecordingItem
            {
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                FormatText = format == FormatGif ? "GIF" : "MP4",
                SizeBytes = info.Length,
                CreatedAt = startedAt,
                DurationText = duration,
            };
            Recordings.Insert(0, item);
            while (Recordings.Count > MaxRecentItems) Recordings.RemoveAt(Recordings.Count - 1);

            LastOutputPath = outputPath;
            TryDelete(tempPath);
            StatusText = $"已保存：{item.FileName}（{item.SizeText}，录制 {duration}）";
            Log($"✓ 已保存：{outputPath}（{item.SizeText}）");

            if (_activeOpenFolder) TryOpenFolder(Path.GetDirectoryName(outputPath));
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);   // 取消 / 失败都不留半成品
            StatusText = "已取消生成文件";
            Log("已取消生成文件（中间素材保留在缓存里：" + tempPath + "）");
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            StatusText = "生成文件失败：" + ex.Message;
            Log("✗ 生成文件失败：" + ex);
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
            IsFinalizing = false;
            Progress = 0;
            ProgressText = "";
            if (autoStopped) Log("（录制自动停止：达到最长录制时长或 FFmpeg 退出）");
        }
    }

    // ================================================================== 截图

    private async Task TakeScreenshotAsync()
    {
        if (!IsIdle) return;

        try
        {
            if (!await EnsureFFmpegAsync()) return;

            var region = ResolveCaptureRegion(promptForRegion: true);
            if (region is null) return;

            var plan = new CapturePlan(region.Value.MakeEven(), 1, Cfg.DrawMouse, 0, "截图");
            var folder = ResolveOutputFolder();
            Directory.CreateDirectory(folder);
            var outputPath = RecordingNames.BuildOutputPath(
                folder, RecordingNames.SanitizePrefix(Cfg.FilePrefix), DateTime.Now, "png");

            StatusText = "正在截图…";
            var result = await RecordingExporter.CaptureScreenshotAsync(_ffmpegPath!, plan, outputPath);
            if (!result.Success)
            {
                TryDelete(outputPath);   // 失败不要留半截 PNG
                StatusText = "截图失败：" + result.Error;
                Log("✗ 截图失败：" + result.Error);
                return;
            }

            var info = new FileInfo(outputPath);
            var item = new RecordingItem
            {
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                FormatText = "PNG",
                SizeBytes = info.Length,
                CreatedAt = DateTime.Now,
            };
            Recordings.Insert(0, item);
            while (Recordings.Count > MaxRecentItems) Recordings.RemoveAt(Recordings.Count - 1);

            LastOutputPath = outputPath;
            StatusText = $"已截图：{item.FileName}（{item.SizeText}）";
            Log($"✓ 已截图：{outputPath}（{item.SizeText}）");
            if (Cfg.OpenFolderAfterSave) TryOpenFolder(folder);
        }
        catch (Exception ex)
        {
            StatusText = "截图失败：" + ex.Message;
            Log("✗ 截图失败：" + ex);
        }
    }

    // ================================================================== 控制条 / 热键 / 计时

    private void ShowOverlay(CapturePlan plan, CaptureRect region)
    {
        if (!Cfg.ShowControlBar)
        {
            HideOverlay();
            return;
        }

        _overlay ??= new RecordingOverlayWindow();
        _overlay.StopRequested -= OnOverlayStopRequested;
        _overlay.StopRequested += OnOverlayStopRequested;

        var isFullScreen = RecorderOptions.ParseSourceKind(Cfg.SourceKind) == CaptureSourceKind.FullScreen;
        var formatText = _activeFormat == FormatGif ? "GIF" : "MP4";
        _overlay.ShowFor($"{plan.Region.SizeText} · {plan.Fps} FPS · {formatText}", _startedAt, region, isFullScreen);
    }

    private void HideOverlay()
    {
        if (_overlay is null) return;
        _overlay.StopRequested -= OnOverlayStopRequested;
        _overlay.StopAndHide();
    }

    private void OnOverlayStopRequested(object? sender, EventArgs e) => _ = StopFromUiAsync();

    private void RegisterStopHotkey()
    {
        UnregisterStopHotkey();
        if (!Cfg.UseStopHotkey)
        {
            _hotkeyActive = false;
            OnPropertyChanged(nameof(StopHintText));
            StopHotkeyState = "热键已关闭，请用控制条 / 本页按钮停止";
            return;
        }

        if (_stopHotkey.Register(GlobalHotkey.KeyF8, () => _ = StopFromUiAsync()))
        {
            _hotkeyActive = true;
            OnPropertyChanged(nameof(StopHintText));
            StopHotkeyState = "F8 = 停止录制";
            return;
        }

        _hotkeyActive = false;
        OnPropertyChanged(nameof(StopHintText));
        StopHotkeyState = (_stopHotkey.LastError ?? "F8 注册失败") + "；请用控制条 / 本页按钮停止";
        Log("⚠ " + StopHotkeyState);
    }

    private void UnregisterStopHotkey()
    {
        _hotkeyActive = false;
        try { _stopHotkey.Unregister(); }
        catch { /* 忽略 */ }
        OnPropertyChanged(nameof(StopHintText));
    }

    private void UpdateElapsedText()
    {
        if (!IsRecording) return;
        ElapsedText = RecordingTime.FormatDuration(DateTime.Now - _startedAt);
        // 不再每 200ms 覆盖 StatusText：录制中如果出现“无法打开文件夹”这类提示，要留给用户看。
    }

    // ================================================================== 输出 / 最近文件

    private string ResolveOutputFolder()
    {
        var folder = Cfg.OutputFolder?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            folder = Path.Combine(videos, "YuLiangToolkit 录屏");
        }

        // 手输相对路径时按程序目录解析，避免落到“当前工作目录”这种看不见的地方
        try { return Path.GetFullPath(folder, AppContext.BaseDirectory); }
        catch { return folder; }
    }

    private void BrowseOutputFolder()
    {
        if (!IsIdle) return;
        var dialog = new OpenFolderDialog
        {
            Title = "选择录屏保存文件夹",
            Multiselect = false,
        };
        var current = ResolveOutputFolder();
        if (Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog() == true) Cfg.OutputFolder = dialog.FolderName;
    }

    private void OpenOutputFolder()
    {
        var folder = ResolveOutputFolder();
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "无法打开输出文件夹：" + ex.Message;
        }
    }

    private void OpenRecording(RecordingItem? item)
    {
        item ??= Recordings.FirstOrDefault();
        if (item is null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.FilePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打不开文件：" + ex.Message;
        }
    }

    private void RevealRecording(RecordingItem? item)
    {
        item ??= Recordings.FirstOrDefault();
        if (item is null) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{item.FilePath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = "无法在资源管理器中显示：" + ex.Message;
        }
    }

    private void CopyLastPath()
    {
        if (_lastOutputPath.Length == 0) return;
        try
        {
            Clipboard.SetText(_lastOutputPath);
            StatusText = "已复制路径：" + _lastOutputPath;
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }

    private void TryOpenFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try { Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true }); }
        catch { /* 打不开不影响主流程 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 留给「清理缓存」 */ }
    }

    // ================================================================== 生命周期

    /// <summary>
    /// 工具被真正关闭（切换走的普通缓存不调用这里，缓存被淘汰 / 重建配置 / 程序退出才调用）。
    /// 正在录制时尽量正常收尾并把文件生成出来；失败就保留中间素材。
    /// </summary>
    protected override void OnClosed()
    {
        _shuttingDown = true;

        try { _elapsedTimer.Stop(); } catch { /* 忽略 */ }
        try { UnregisterStopHotkey(); } catch { /* 忽略 */ }
        try { _stopHotkey.Dispose(); } catch { /* 忽略 */ }
        try { _countdownCts?.Cancel(); } catch { /* 忽略 */ }
        try { _overlay?.StopAndHide(); } catch { /* 忽略 */ }
        try { _overlay?.CloseNow(); } catch { /* 忽略 */ }
        _overlay = null;

        var countdown = _countdownWindow;
        _countdownWindow = null;
        try { countdown?.CloseNow(); } catch { /* 忽略 */ }

        var session = _session;
        _session = null;
        if (session is not null)
        {
            try
            {
                var stop = session.StopAndWait(TimeSpan.FromSeconds(3));
                if (!string.IsNullOrWhiteSpace(stop.ErrorTail)) Log("[FFmpeg] " + stop.ErrorTail);
            }
            catch { /* 忽略 */ }

            var temp = _activeTempPath;
            if (!IsFinalizing && !_cancelRequested && !string.IsNullOrWhiteSpace(temp) && File.Exists(temp))
            {
                try
                {
                    // 只有程序真的在退出时才阻塞等文件生成完；缓存淘汰 / 重建配置只是把这个页面丢掉，
                    // 丢到后台生成即可（不卡 UI，也不丢已经录到的内容）。
                    var appExiting = Application.Current?.MainWindow is not { IsLoaded: true };
                    var task = Task.Run(FinalizeForShutdown);
                    if (appExiting)
                    {
                        if (!task.Wait(TimeSpan.FromSeconds(20)))
                            Log("退出时生成文件超时，中间素材保留在：" + temp);
                    }
                    else
                    {
                        Log("录制已停止，正在后台生成文件（不阻塞界面）：" + _activeOutputPath);
                    }
                }
                catch (Exception ex)
                {
                    Log("退出时生成文件失败：" + ex.Message + "；中间素材保留在：" + temp);
                }
            }

            try { session.Dispose(); } catch { /* 忽略 */ }
        }

        // 只有退出流程才取消进行中的导出；缓存淘汰时让后台导出自己跑完。
        if (Application.Current?.MainWindow is not { IsLoaded: true })
        {
            try { _exportCts?.Cancel(); } catch { /* 忽略 */ }
        }
    }

    /// <summary>退出时的收尾导出：后台线程跑，不碰任何界面对象。</summary>
    private void FinalizeForShutdown()
    {
        var temp = _activeTempPath;
        if (string.IsNullOrWhiteSpace(temp) || !File.Exists(temp)) return;
        if (string.IsNullOrWhiteSpace(_ffmpegPath) || _activePlan is null) return;

        var result = _activeFormat == FormatGif && _activeGif is not null
            ? RecordingExporter.ExportGifAsync(_ffmpegPath, _activePlan.Region, _activeGif, temp, _activeOutputPath, null, CancellationToken.None)
                .GetAwaiter().GetResult()
            : _activeMp4 is not null
                ? RecordingExporter.ExportMp4Async(_ffmpegPath, _activePlan.Region, _activeMp4, temp, _activeOutputPath, null, CancellationToken.None)
                    .GetAwaiter().GetResult()
                : new ExportResult(false, _activeOutputPath, "缺少导出参数");

        if (result.Success) TryDelete(temp);
    }
}
