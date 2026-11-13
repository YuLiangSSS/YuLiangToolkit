using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

/// <summary>摄像头下拉框里的一项（包一层是为了 ComboBox 的选中显示永远用友好的设备名）。</summary>
public sealed class CameraOption
{
    /// <summary>底层设备信息。</summary>
    public required CameraDeviceInfo Device { get; init; }

    /// <summary>显示名。</summary>
    public string Name => Device.Name;

    /// <summary>没写 DisplayMemberPath 时也能显示设备名。</summary>
    public override string ToString() => Name;
}

/// <summary>「最近生成」列表里的一项。</summary>
public sealed class RecordingItem
{
    /// <summary>完整路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>文件名。</summary>
    public required string FileName { get; init; }

    /// <summary>GIF / MP4 / MKV / MOV / AVI / WebM / PNG（带编码器说明）。</summary>
    public required string FormatText { get; init; }

    /// <summary>文件大小（字节）。</summary>
    public long SizeBytes { get; init; }

    /// <summary>生成时间（录制开始时间）。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>录制时长文案（截图 / 未知时为空）。</summary>
    public string DurationText { get; init; } = "";

    /// <summary>大小文案。</summary>
    public string SizeText => FileSize.Format(SizeBytes);

    /// <summary>列表右侧的摘要：<c>MP4 · 1.2 MB · 12:03:45</c>。</summary>
    public string SummaryText => $"{FormatText} · {SizeText} · {CreatedAt:HH:mm:ss}";
}

/// <summary>
/// 「录屏 / GIF」视图模型：OBS 风格但保持简单的录屏工具。
///
/// <para><b>画面</b>：gdigrab 采集全屏 / 区域 / 窗口；摄像头是可选的「屏幕覆盖」小窗，
/// 它本身就在屏幕上，所以预览和录制时都会以同样位置出现（gdigrab 会把它录进去）。</para>
///
/// <para><b>视频</b>：可选 MP4 / MKV / MOV / AVI / WebM，编码器 / 画质或码率可调；
/// 录制时先按选定编码器写中间 MKV，停止后只做「封装 + 混音」（视频不二次编码），所以导出很快。</para>
///
/// <para><b>声音</b>：麦克风 / 系统扬声器（WASAPI）在录制中分别采集，音量滑块实时生效；
/// 停止后混进视频（GIF 不含声音）。</para>
///
/// <para><b>快捷键</b>：开始/停止、截图两个全局热键可在界面里自己按组合键设置。</para>
/// </summary>
public sealed class ScreenRecorderViewModel : ToolViewModelBase
{
    private const string SourceFullScreen = "fullscreen";
    private const string SourceRegion = "region";
    private const string SourceWindow = "window";
    private const int StartStopHotkeyId = 0x5C52;
    private const int ScreenshotHotkeyId = 0x5C53;
    private const int MaxRecentItems = 30;

    private readonly ToolCache _cache = ToolCache.For(ScreenRecorderConfig.ToolId);
    private readonly GlobalHotkey _hotkeys = new();
    private readonly CameraPreviewSource _camera = new();
    private readonly DispatcherTimer _elapsedTimer;

    private string? _ffmpegPath;
    private ScreenCaptureSession? _session;
    private CancellationTokenSource? _countdownCts;
    private CancellationTokenSource? _exportCts;
    private RecordingOverlayWindow? _overlay;
    private CountdownWindow? _countdownWindow;
    private CameraOverlayWindow? _cameraOverlay;

    private AudioRecorder? _micRecorder;
    private AudioRecorder? _speakerRecorder;
    private string? _micTempPath;
    private string? _speakerTempPath;

    private IReadOnlySet<string>? _availableEncoders;
    private bool _shuttingDown;
    private bool _stopInProgress;
    private bool _cancelRequested;
    private Task? _finalizeTask;
    private DateTime _startedAt;
    private TimeSpan _recordedDuration;

    // 录制开始时把所有选项拍个快照：录制中界面会禁用选项，退出时也还能按快照收尾
    private CapturePlan? _activePlan;
    private GifExportSettings? _activeGif;
    private VideoEncoderSettings? _activeVideo;
    private string _activeFormat = VideoCatalog.GifFormat;
    private string _activeOutputPath = "";
    private string _activeTempPath = "";
    private IReadOnlyList<string> _activeAudioPaths = Array.Empty<string>();
    private int _activeAudioBitrate = 192;
    private int _activeAudioDelayMs;
    private string _activeVideoCodecName = "";
    private bool _activeOpenFolder;
    private string _lastAudioError = "";

    private bool _isRecording;
    private bool _isFinalizing;
    private string _elapsedText = "00:00";
    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    private string _hotkeyStatusText = "";
    private string _windowCountText = "";
    private string _lastOutputPath = "";
    private RecordingItem? _selectedRecording;

    private bool _isCameraPreviewOn;
    private bool _isCameraOverlayShown;
    private bool _isCameraRunning;
    private bool _cameraFrameLogged;
    private string _cameraStatusText = "摄像头未启用";
    private ImageSource? _cameraPreviewImage;

    public ScreenRecorderViewModel() : base(ScreenRecorderConfig.ToolId)
    {
        Cfg = LoadConfig<ScreenRecorderConfig>();
        NormalizeConfigValues();

        SourceOptions = OptionItem.From(
            (SourceFullScreen, "全屏（主显示器）"),
            (SourceRegion, "区域（拖框选择）"),
            (SourceWindow, "窗口（录这个窗口的位置）"));

        var formats = new List<OptionItem<string>>
        {
            new(VideoCatalog.GifFormat, "动图 GIF（两遍调色板）"),
        };
        foreach (var container in VideoCatalog.Containers)
            formats.Add(new OptionItem<string>(container.Id, container.Label));
        FormatOptions = formats;

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

        RateModeOptions = OptionItem.From(
            (VideoEncoderSettings.RateQuality, "画质优先（CRF / CQ，推荐）"),
            (VideoEncoderSettings.RateBitrate, "码率优先（固定 Mbps）"));

        AudioBitrateOptions = OptionItem.FromValues(RecorderOptions.AudioBitrateChoices,
            value => $"{value} kbps");

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

        _camera.FrameArrived += OnCameraFrameArrived;

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
        ToggleCameraPreviewCommand = new AsyncRelayCommand(_ => ToggleCameraPreviewAsync(), _ => !IsFinalizing);
        ToggleCameraOverlayCommand = new AsyncRelayCommand(_ => ToggleCameraOverlayAsync(), _ => !IsFinalizing);
        RefreshCamerasCommand = new AsyncRelayCommand(_ => RefreshCamerasAsync());
        ResetCameraOverlayCommand = new RelayCommand(_ => ResetCameraOverlayPosition());
        SetStartStopHotkeyCommand = new RelayCommand(_ => SetHotkey(startStop: true));
        SetScreenshotHotkeyCommand = new RelayCommand(_ => SetHotkey(startStop: false));
        ClearScreenshotHotkeyCommand = new RelayCommand(_ => Cfg.ScreenshotHotkey = "");

        Cfg.PropertyChanged += OnConfigChanged;

        RebuildCodecOptions();
        ApplyHotkeys();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"录制素材缓存：{_cache.Directory}");
        Log($"输出文件夹：{ResolveOutputFolder()}");
        _ = InitFFmpegAsync();
        _ = RefreshCamerasAsync();
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
    public IReadOnlyList<OptionItem<string>> RateModeOptions { get; }
    public IReadOnlyList<OptionItem<int>> AudioBitrateOptions { get; }
    public IReadOnlyList<OptionItem<int>> CountdownOptions { get; }
    public IReadOnlyList<OptionItem<int>> MaxDurationOptions { get; }

    /// <summary>当前容器下可用的视频编码器（运行时按 ffmpeg -encoders 过滤）。</summary>
    public ObservableCollection<OptionItem<string>> VideoCodecOptions { get; } = new();

    /// <summary>可录制的窗口。</summary>
    public ObservableCollection<WindowOption> Windows { get; } = new();

    /// <summary>摄像头设备列表。</summary>
    public ObservableCollection<CameraOption> CameraDevices { get; } = new();

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

    public OptionItem<string> SelectedVideoCodec
    {
        get => OptionItem<string>.Find(VideoCodecOptions, Cfg.VideoCodec)
               ?? VideoCodecOptions.FirstOrDefault()
               ?? new OptionItem<string>("libx264", "H.264 · libx264");
        set
        {
            if (value is null || value.Value == Cfg.VideoCodec) return;
            Cfg.VideoCodec = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 编码器下拉框用 <see cref="System.Windows.Controls.ComboBox.SelectedIndex"/> 绑定：
    /// 重建 <see cref="VideoCodecOptions"/>（ObservableCollection）时 WPF 会先把 SelectedItem 清空，
    /// 用索引绑定 + 重建后重新通知更稳，不会出现“配置对了但下拉框空白”。
    /// </summary>
    public int SelectedCodecIndex
    {
        get => VideoCodecOptions.IndexOf(SelectedVideoCodec);
        set
        {
            if (value < 0 || value >= VideoCodecOptions.Count) return;
            Cfg.VideoCodec = VideoCodecOptions[value].Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<string> SelectedRateMode
    {
        get => OptionItem<string>.Find(RateModeOptions, Cfg.VideoRateMode) ?? RateModeOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.VideoRateMode) return;
            Cfg.VideoRateMode = value.Value;
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

    public OptionItem<int> SelectedAudioBitrate
    {
        get => OptionItem<int>.Find(AudioBitrateOptions, RecorderOptions.SnapToNearest(Cfg.AudioBitrateKbps, RecorderOptions.AudioBitrateChoices))
               ?? AudioBitrateOptions[1];
        set
        {
            if (value is null || value.Value == Cfg.AudioBitrateKbps) return;
            Cfg.AudioBitrateKbps = value.Value;
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

    public CameraOption? SelectedCamera
    {
        get => CameraDevices.FirstOrDefault(c => string.Equals(c.Device.Id, Cfg.CameraDeviceId, StringComparison.Ordinal))
               ?? CameraDevices.FirstOrDefault();
        set
        {
            if (value is null) return;
            if (Cfg.CameraDeviceId == value.Device.Id && Cfg.CameraDeviceName == value.Name) return;
            Cfg.CameraDeviceId = value.Device.Id;
            Cfg.CameraDeviceName = value.Name;
            OnPropertyChanged();
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
            OnPropertyChanged(nameof(IsQualityMode));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>是不是正在把中间素材转成最终文件 / 混音。</summary>
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

    /// <summary>停止提示（热键开了没 / 有没有注册成功）。</summary>
    public string StopHintText
    {
        get
        {
            if (!Cfg.EnableGlobalHotkeys) return "点控制条 / 本页按钮停止（全局快捷键已关闭）";
            var spec = ParseHotkey(Cfg.StartStopHotkey);
            return spec is null ? "停止按钮（快捷键未设置）" : $"{spec.DisplayText} 停止";
        }
    }

    /// <summary>FFmpeg 状态文案。</summary>
    public string FFmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => Set(ref _ffmpegStatusText, value);
    }

    /// <summary>全局快捷键注册状态。</summary>
    public string HotkeyStatusText
    {
        get => _hotkeyStatusText;
        private set => Set(ref _hotkeyStatusText, value);
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

    /// <summary>最近生成列表里选中的一项。</summary>
    public RecordingItem? SelectedRecording
    {
        get => _selectedRecording;
        set => Set(ref _selectedRecording, value);
    }

    /// <summary>摄像头是否正在采集。</summary>
    public bool IsCameraRunning
    {
        get => _isCameraRunning;
        private set => Set(ref _isCameraRunning, value);
    }

    /// <summary>工具页里的摄像头预览是否开着。</summary>
    public bool IsCameraPreviewOn
    {
        get => _isCameraPreviewOn;
        private set
        {
            if (!Set(ref _isCameraPreviewOn, value)) return;
            OnPropertyChanged(nameof(CameraPreviewButtonText));
        }
    }

    /// <summary>摄像头屏幕覆盖是否显示。</summary>
    public bool IsCameraOverlayShown
    {
        get => _isCameraOverlayShown;
        private set
        {
            if (!Set(ref _isCameraOverlayShown, value)) return;
            OnPropertyChanged(nameof(CameraOverlayButtonText));
        }
    }

    /// <summary>摄像头状态文案。</summary>
    public string CameraStatusText
    {
        get => _cameraStatusText;
        private set => Set(ref _cameraStatusText, value);
    }

    /// <summary>工具页里的摄像头预览图（BGRA、已冻结）。</summary>
    public ImageSource? CameraPreviewImage
    {
        get => _cameraPreviewImage;
        private set => Set(ref _cameraPreviewImage, value);
    }

    /// <summary>摄像头预览按钮文字。</summary>
    public string CameraPreviewButtonText => IsCameraPreviewOn ? "关闭预览" : "开启预览";

    /// <summary>摄像头屏幕覆盖按钮文字。</summary>
    public string CameraOverlayButtonText => IsCameraOverlayShown ? "隐藏屏幕覆盖" : "显示屏幕覆盖";

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

    /// <summary>当前格式是不是 GIF。</summary>
    public bool IsGif => Cfg.IsGif;

    /// <summary>当前格式是不是视频。</summary>
    public bool IsVideo => Cfg.IsVideo;

    /// <summary>视频画质优先模式（决定显示画质滑块还是码率滑块）。</summary>
    public bool IsQualityMode => Cfg.VideoRateMode != VideoEncoderSettings.RateBitrate;

    /// <summary>视频码率优先模式。</summary>
    public bool IsBitrateMode => !IsQualityMode;

    /// <summary>麦克风有声音时才显示音量说明。</summary>
    public string AudioSummary
    {
        get
        {
            var parts = new List<string>();
            if (Cfg.RecordMicrophone) parts.Add($"麦克风 {Cfg.MicrophoneVolume}%");
            if (Cfg.RecordSystemAudio) parts.Add($"系统声音 {Cfg.SpeakerVolume}%");
            if (parts.Count == 0) return "不录声音";
            return string.Join(" + ", parts) + $" · {VideoCatalog.AudioCodecLabel(Cfg.Format)} {Cfg.AudioBitrateKbps} kbps";
        }
    }

    /// <summary>摄像头摘要。</summary>
    public string CameraSummary
    {
        get
        {
            if (!Cfg.EnableCamera) return "摄像头未启用";
            var name = string.IsNullOrWhiteSpace(Cfg.CameraDeviceName) ? "默认摄像头" : Cfg.CameraDeviceName;
            var overlay = Cfg.CameraOverlayEnabled ? "屏幕覆盖开" : "屏幕覆盖关";
            return $"{name} · {overlay}" + (Cfg.CameraMirror ? " · 镜像" : "");
        }
    }

    /// <summary>当前帧率 / 格式 / 宽度的摘要。</summary>
    public string CaptureSummary
    {
        get
        {
            var fps = RecorderOptions.NormalizeFps(Cfg.Fps);
            var width = RecorderOptions.NormalizeScaleWidth(Cfg.ScaleWidth);
            var widthText = width <= 0 ? "原始分辨率" : $"{width} px 宽";
            if (Cfg.IsGif)
                return $"动图 GIF · {fps} FPS · {widthText} · {RecorderOptions.NormalizeColors(Cfg.GifColors)} 色";

            var container = VideoCatalog.GetContainer(Cfg.Format);
            var codec = VideoCatalog.GetCodec(Cfg.VideoCodec);
            var rate = Cfg.VideoRateMode == VideoEncoderSettings.RateBitrate
                ? $"{Cfg.VideoBitrateMbps} Mbps"
                : $"CRF {Cfg.VideoQuality}";
            var audio = Cfg.RecordMicrophone || Cfg.RecordSystemAudio
                ? $" · {VideoCatalog.AudioCodecLabel(container.Id)} 音频"
                : "";
            return $"{container.ShortLabel} · {codec.CodecName} · {fps} FPS · {widthText} · {rate}{audio}";
        }
    }

    /// <summary>开始/停止热键显示文本。</summary>
    public string StartStopHotkeyText
    {
        get
        {
            if (!Cfg.EnableGlobalHotkeys) return "已关闭";
            var spec = ParseHotkey(Cfg.StartStopHotkey);
            return spec?.DisplayText ?? "未设置";
        }
    }

    /// <summary>截图热键显示文本。</summary>
    public string ScreenshotHotkeyText
    {
        get
        {
            if (!Cfg.EnableGlobalHotkeys) return "已关闭";
            var spec = ParseHotkey(Cfg.ScreenshotHotkey);
            return spec?.DisplayText ?? "未设置";
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
    public AsyncRelayCommand ToggleCameraPreviewCommand { get; }
    public AsyncRelayCommand ToggleCameraOverlayCommand { get; }
    public AsyncRelayCommand RefreshCamerasCommand { get; }
    public RelayCommand ResetCameraOverlayCommand { get; }
    public RelayCommand SetStartStopHotkeyCommand { get; }
    public RelayCommand SetScreenshotHotkeyCommand { get; }
    public RelayCommand ClearScreenshotHotkeyCommand { get; }

    // ================================================================== 配置联动

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ScreenRecorderConfig.SourceKind):
                OnPropertyChanged(nameof(SelectedSource));
                if (Cfg.IsWindowSource) RefreshWindows();
                break;
            case nameof(ScreenRecorderConfig.Format):
                EnsureCodecCompatible();
                RebuildCodecOptions();
                OnPropertyChanged(nameof(SelectedFormat));
                break;
            case nameof(ScreenRecorderConfig.VideoCodec):
                OnPropertyChanged(nameof(SelectedVideoCodec));
                break;
            case nameof(ScreenRecorderConfig.VideoRateMode):
                OnPropertyChanged(nameof(SelectedRateMode));
                OnPropertyChanged(nameof(IsQualityMode));
                OnPropertyChanged(nameof(IsBitrateMode));
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
            case nameof(ScreenRecorderConfig.AudioBitrateKbps):
                OnPropertyChanged(nameof(SelectedAudioBitrate));
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
            case nameof(ScreenRecorderConfig.CameraDeviceId):
                OnPropertyChanged(nameof(SelectedCamera));
                _ = RestartCameraIfNeededAsync();
                break;
            case nameof(ScreenRecorderConfig.EnableCamera):
                if (!Cfg.EnableCamera)
                {
                    _ = StopCameraIfUnusedAsync(force: true);
                    if (IsCameraOverlayShown) HideCameraOverlay();
                }
                break;
            case nameof(ScreenRecorderConfig.CameraMirror):
            case nameof(ScreenRecorderConfig.CameraOverlayOpacity):
            case nameof(ScreenRecorderConfig.CameraOverlayWidth):
                ApplyCameraOverlayAppearance();
                break;
            case nameof(ScreenRecorderConfig.EnableGlobalHotkeys):
            case nameof(ScreenRecorderConfig.StartStopHotkey):
            case nameof(ScreenRecorderConfig.ScreenshotHotkey):
                ApplyHotkeys();
                break;
        }

        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(RegionText));
        OnPropertyChanged(nameof(CaptureSummary));
        OnPropertyChanged(nameof(AudioSummary));
        OnPropertyChanged(nameof(CameraSummary));
        OnPropertyChanged(nameof(IsGif));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(SelectedCodecIndex));
        OnPropertyChanged(nameof(StartStopHotkeyText));
        OnPropertyChanged(nameof(ScreenshotHotkeyText));
        OnPropertyChanged(nameof(StopHintText));
    }

    private static string CanonicalSource(string? value)
        => RecorderOptions.ParseSourceKind(value) switch
        {
            CaptureSourceKind.FullScreen => SourceFullScreen,
            CaptureSourceKind.Window => SourceWindow,
            _ => SourceRegion,
        };

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

    private void EnsureCodecCompatible()
    {
        if (!VideoCatalog.IsVideoFormat(Cfg.Format)) return;
        if (VideoCatalog.IsCompatible(Cfg.Format, Cfg.VideoCodec)) return;
        Cfg.VideoCodec = VideoCatalog.DefaultCodecFor(Cfg.Format, _availableEncoders);
    }

    private void RebuildCodecOptions()
    {
        var compatible = VideoCatalog.CompatibleCodecs(Cfg.Format, _availableEncoders);
        if (compatible.Count == 0)
        {
            // 编码器检测失败 / 当前容器一个都不支持时，至少把软件 x264 留作兜底
            compatible = VideoCatalog.CompatibleCodecs(Cfg.Format, null);
        }

        // 集合没变化就不要 Clear：ComboBox 在 ItemsSource 变动时会丢选中项。
        var same = compatible.Count == VideoCodecOptions.Count
                   && compatible.Select(c => c.Id).SequenceEqual(VideoCodecOptions.Select(o => o.Value), StringComparer.OrdinalIgnoreCase);
        if (!same)
        {
            VideoCodecOptions.Clear();
            foreach (var codec in compatible)
                VideoCodecOptions.Add(new OptionItem<string>(codec.Id, codec.Label));
        }

        if (VideoCodecOptions.Count > 0 && !VideoCodecOptions.Any(o => o.Value == Cfg.VideoCodec))
            Cfg.VideoCodec = VideoCodecOptions[0].Value;

        OnPropertyChanged(nameof(SelectedVideoCodec));
        OnPropertyChanged(nameof(SelectedCodecIndex));
        // ItemsSource 重建后的同一帧里 WPF 可能还会再清一次选中项：下一轮 Dispatcher 再通知一遍。
        Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            OnPropertyChanged(nameof(SelectedCodecIndex));
        }));
    }

    // ================================================================== 初始化

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

        var encoders = await FFmpegRunner.RunAsync(found, new[] { "-hide_banner", "-encoders" });
        _availableEncoders = EncoderCatalog.ParseAvailable(encoders.Output + Environment.NewLine + encoders.Error);
        if (_availableEncoders.Count == 0) _availableEncoders = null;
        RebuildCodecOptions();
        EnsureCodecCompatible();
        Log($"视频编码器：检测到 {_availableEncoders?.Count ?? 0} 个；当前格式 {Cfg.Format}，编码器 {VideoCatalog.GetCodec(Cfg.VideoCodec).CodecName}");
    }

    private void SetFFmpegStatus(string? path, string? version)
    {
        Ui(() =>
        {
            _ffmpegPath = path;
            FFmpegStatusText = path is null
                ? "⚠️ 未检测到 FFmpeg：请「选择 FFmpeg…」指定 ffmpeg.exe，或「下载 FFmpeg」后重试（录屏、转 GIF、混音都要用）"
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

        StatusText = "未检测到 FFmpeg：请先指定 ffmpeg.exe（录屏 / 转 GIF / 混音都靠它）";
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

    // ================================================================== 摄像头

    private async Task RefreshCamerasAsync()
    {
        var devices = await CameraPreviewSource.EnumerateAsync();
        CameraDevices.Clear();
        foreach (var device in devices) CameraDevices.Add(new CameraOption { Device = device });
        OnPropertyChanged(nameof(SelectedCamera));
        if (devices.Count == 0)
            CameraStatusText = "没有找到摄像头（或系统隐私设置不允许桌面应用访问相机）";
        else if (!Cfg.EnableCamera)
            CameraStatusText = $"找到 {devices.Count} 个摄像头";
        else if (!IsCameraRunning)
            CameraStatusText = $"找到 {devices.Count} 个摄像头，点「开启预览」启动";
    }

    private async Task<bool> EnsureCameraRunningAsync()
    {
        if (!Cfg.EnableCamera)
        {
            CameraStatusText = "摄像头未启用（先勾选「启用摄像头」）";
            return false;
        }

        if (_camera.IsRunning)
        {
            IsCameraRunning = true;
            return true;
        }

        if (CameraDevices.Count == 0) await RefreshCamerasAsync();
        var deviceId = string.IsNullOrWhiteSpace(Cfg.CameraDeviceId) ? null : Cfg.CameraDeviceId;
        CameraStatusText = "正在打开摄像头…";
        _cameraFrameLogged = false;
        var ok = await _camera.StartAsync(deviceId, 640, 15);
        IsCameraRunning = _camera.IsRunning;
        CameraStatusText = ok
            ? $"摄像头运行中{(Cfg.CameraMirror ? " · 镜像" : "")}"
            : "摄像头打开失败：" + (_camera.LastError ?? "未知原因");
        if (!ok) Log("✗ " + CameraStatusText);
        return ok;
    }

    private async Task ToggleCameraPreviewAsync()
    {
        if (IsCameraPreviewOn)
        {
            IsCameraPreviewOn = false;
            await StopCameraIfUnusedAsync();
            return;
        }

        if (!await EnsureCameraRunningAsync()) return;
        IsCameraPreviewOn = true;
        CameraStatusText = "摄像头预览中";
    }

    private async Task ToggleCameraOverlayAsync()
    {
        if (IsCameraOverlayShown)
        {
            HideCameraOverlay();
            await StopCameraIfUnusedAsync();
            return;
        }

        if (!Cfg.EnableCamera) Cfg.EnableCamera = true;
        if (!await EnsureCameraRunningAsync()) return;

        ShowCameraOverlay();
        Cfg.CameraOverlayEnabled = true;
        CameraStatusText = "摄像头屏幕覆盖已显示（录制时会一起录进去）";
    }

    private void ShowCameraOverlay()
    {
        if (_cameraOverlay is null)
        {
            _cameraOverlay = new CameraOverlayWindow();
            _cameraOverlay.PlacementChanged += OnCameraOverlayPlacementChanged;
            _cameraOverlay.MirrorChanged += OnCameraOverlayMirrorChanged;
            _cameraOverlay.ResetPositionRequested += (_, _) => ResetCameraOverlayPosition();
            _cameraOverlay.CloseRequested += (_, _) =>
            {
                HideCameraOverlay();
                Cfg.CameraOverlayEnabled = false;
                _ = StopCameraIfUnusedAsync();
            };
        }

        _cameraOverlay.SetOverlayWidth(Cfg.CameraOverlayWidth);
        ApplyCameraOverlayAppearance();
        _cameraOverlay.UpdateFrame(CameraPreviewImage as System.Windows.Media.Imaging.BitmapSource);

        if (Cfg.CameraOverlayX >= 0 && Cfg.CameraOverlayY >= 0)
        {
            _cameraOverlay.MoveTo(Cfg.CameraOverlayX, Cfg.CameraOverlayY);
        }
        else
        {
            PlaceCameraOverlayAuto();
        }

        _cameraOverlay.ShowOverlay();
        IsCameraOverlayShown = true;
    }

    private void HideCameraOverlay()
    {
        _cameraOverlay?.HideOverlay();
        IsCameraOverlayShown = false;
    }

    private void ApplyCameraOverlayAppearance()
    {
        _cameraOverlay?.ApplyAppearance(Cfg.CameraMirror, Cfg.CameraOverlayOpacity / 100.0);
        _cameraOverlay?.SetOverlayWidth(Cfg.CameraOverlayWidth);
    }

    private void PlaceCameraOverlayAuto()
    {
        if (_cameraOverlay is null) return;
        var work = SystemParameters.WorkArea;
        var width = Math.Clamp(Cfg.CameraOverlayWidth, 120, 800);
        var height = width * 9.0 / 16.0;
        _cameraOverlay.MoveTo(work.Right - width - 24, work.Bottom - height - 24);
    }

    private void ResetCameraOverlayPosition()
    {
        Cfg.CameraOverlayX = -1;
        Cfg.CameraOverlayY = -1;
        PlaceCameraOverlayAuto();
        StatusText = "摄像头覆盖已重置到屏幕右下角";
    }

    private void OnCameraOverlayPlacementChanged(object? sender, CameraOverlayPlacement placement)
    {
        Cfg.CameraOverlayX = (int)Math.Round(placement.Left);
        Cfg.CameraOverlayY = (int)Math.Round(placement.Top);
        Cfg.CameraOverlayWidth = (int)Math.Round(placement.Width);
    }

    private void OnCameraOverlayMirrorChanged(object? sender, bool mirror) => Cfg.CameraMirror = mirror;

    private void OnCameraFrameArrived(object? sender, CameraFrameEventArgs e)
    {
        if (_shuttingDown) return;
        var image = e.Image;
        Ui(() =>
        {
            if (!_cameraFrameLogged)
            {
                _cameraFrameLogged = true;
                Log($"摄像头已出画面：{image.PixelWidth}×{image.PixelHeight}");
            }
            CameraPreviewImage = image;
            _cameraOverlay?.UpdateFrame(image);
        });
    }

    private async Task StopCameraIfUnusedAsync(bool force = false)
    {
        if (_camera is null || !_camera.IsRunning) return;
        if (!force && (IsCameraPreviewOn || IsCameraOverlayShown || IsRecording)) return;

        await _camera.StopAsync();
        IsCameraRunning = false;
        CameraPreviewImage = null;
        CameraStatusText = Cfg.EnableCamera ? "摄像头已暂停" : "摄像头未启用";
    }

    private async Task RestartCameraIfNeededAsync()
    {
        if (!_camera.IsRunning || IsRecording) return;
        await _camera.StopAsync();
        IsCameraRunning = false;
        if (Cfg.EnableCamera) await EnsureCameraRunningAsync();
    }

    // ================================================================== 录音

    private void StartAudioRecorders(DateTime videoStartedAt)
    {
        StopAudioRecorders();
        _activeAudioDelayMs = 0;

        if (!VideoCatalog.IsVideoFormat(Cfg.Format))
        {
            if (Cfg.RecordMicrophone || Cfg.RecordSystemAudio)
                Log("提示：GIF 不含声音；想录声音请选 MP4 / MKV / MOV / AVI / WebM。");
            return;
        }

        _cache.EnsureCreated();
        var audioStartedAt = DateTime.Now;
        _activeAudioDelayMs = (int)Math.Clamp((audioStartedAt - videoStartedAt).TotalMilliseconds, 0, 5000);

        if (Cfg.RecordMicrophone)
        {
            _micTempPath = _cache.PathOf($"mic-{videoStartedAt:yyyyMMdd-HHmmss}.wav");
            _micRecorder = new AudioRecorder(AudioCaptureKind.Microphone, _micTempPath)
            {
                GainProvider = () => (float)(Cfg.MicrophoneVolume / 100.0),
            };
            if (!_micRecorder.Start())
            {
                Log("⚠ 麦克风录制启动失败：" + (_micRecorder.LastError ?? "未知原因"));
                _micRecorder = null;
            }
            else Log("🎙 麦克风录制已启动");
        }

        if (Cfg.RecordSystemAudio)
        {
            _speakerTempPath = _cache.PathOf($"speaker-{videoStartedAt:yyyyMMdd-HHmmss}.wav");
            _speakerRecorder = new AudioRecorder(AudioCaptureKind.SystemLoopback, _speakerTempPath)
            {
                GainProvider = () => (float)(Cfg.SpeakerVolume / 100.0),
            };
            if (!_speakerRecorder.Start())
            {
                Log("⚠ 系统声音录制启动失败：" + (_speakerRecorder.LastError ?? "未知原因"));
                _speakerRecorder = null;
            }
            else Log("🔊 系统声音录制已启动");
        }

        if (_activeAudioDelayMs > 20)
            Log($"声音比画面晚启动约 {_activeAudioDelayMs} ms，收尾时会自动补静音对齐。");
    }

    /// <summary>录制中轮询录音器错误（设备被拔 / 默认设备切换时只记日志，不打断画面）。</summary>
    private void CheckAudioRecorderErrors()
    {
        var error = _micRecorder?.LastError ?? _speakerRecorder?.LastError ?? "";
        if (error.Length == 0 || error == _lastAudioError) return;
        _lastAudioError = error;
        Log("⚠ 录音出错（已停止这一路声音）：" + error);
    }

    private void StopAudioRecorders()
    {
        try { _micRecorder?.Stop(); } catch { /* 忽略 */ }
        try { _speakerRecorder?.Stop(); } catch { /* 忽略 */ }
        try { _micRecorder?.Dispose(); } catch { /* 忽略 */ }
        try { _speakerRecorder?.Dispose(); } catch { /* 忽略 */ }
        _micRecorder = null;
        _speakerRecorder = null;
    }

    private List<string> CollectAudioPaths()
    {
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(_micTempPath) && File.Exists(_micTempPath) && new FileInfo(_micTempPath).Length > 44)
            paths.Add(_micTempPath);
        if (!string.IsNullOrWhiteSpace(_speakerTempPath) && File.Exists(_speakerTempPath) && new FileInfo(_speakerTempPath).Length > 44)
            paths.Add(_speakerTempPath);
        return paths;
    }

    private void CleanupAudioTemps()
    {
        TryDelete(_micTempPath);
        TryDelete(_speakerTempPath);
        _micTempPath = null;
        _speakerTempPath = null;
    }

    // ================================================================== 快捷键

    /// <summary>
    /// 按当前配置重新注册全局热键。
    /// <para>public 是给 <see cref="ScreenRecorderTool"/> 用的：主页设置的快捷键汇总改完键要让它重注册。</para>
    /// </summary>
    public void ApplyHotkeys()
    {
        if (_shuttingDown) return;

        _hotkeys.UnregisterAll();
        if (!Cfg.EnableGlobalHotkeys)
        {
            HotkeyStatusText = "全局快捷键已关闭";
            OnPropertyChanged(nameof(StartStopHotkeyText));
            OnPropertyChanged(nameof(ScreenshotHotkeyText));
            return;
        }

        var startSpec = ParseHotkey(Cfg.StartStopHotkey);
        var screenshotSpec = ParseHotkey(Cfg.ScreenshotHotkey);
        var errors = new List<string>();

        // 单条启用位（主页设置的快捷键汇总可以单独关掉某一条）
        if (!Cfg.StartStopHotkeyEnabled) startSpec = null;
        if (!Cfg.ScreenshotHotkeyEnabled) screenshotSpec = null;

        if (startSpec is not null)
        {
            if (_hotkeys.Register(StartStopHotkeyId, startSpec, () => _ = ToggleRecordingAsync(), out var startError))
                HotkeyStatusText = $"{startSpec.DisplayText} = 开始/停止";
            else
                errors.Add(startError ?? "开始/停止热键注册失败");
        }

        if (screenshotSpec is not null && screenshotSpec != startSpec)
        {
            if (_hotkeys.Register(ScreenshotHotkeyId, screenshotSpec, () => _ = TakeScreenshotAsync(), out var shotError))
                HotkeyStatusText += $" · {screenshotSpec.DisplayText} = 截图";
            else
                errors.Add(shotError ?? "截图热键注册失败");
        }

        if (errors.Count > 0)
        {
            HotkeyStatusText = string.Join("；", errors);
            Log("⚠ " + HotkeyStatusText);
        }

        OnPropertyChanged(nameof(StartStopHotkeyText));
        OnPropertyChanged(nameof(ScreenshotHotkeyText));
    }

    private static HotkeySpec? ParseHotkey(string? text)
        => HotkeySpec.TryParse(text, out var spec) && !spec.IsEmpty ? spec : null;

    private void SetHotkey(bool startStop)
    {
        if (IsRecording) return;
        var dialog = new HotkeyCaptureWindow(startStop ? Cfg.StartStopHotkey : Cfg.ScreenshotHotkey)
        {
            Owner = Application.Current?.MainWindow,
        };
        if (dialog.ShowDialog() != true || dialog.Result is not { } spec) return;

        if (startStop) Cfg.StartStopHotkey = spec.DisplayText;
        else Cfg.ScreenshotHotkey = spec.DisplayText;
        ApplyHotkeys();
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
            var container = VideoCatalog.GetContainer(format);
            VideoEncoderSettings? videoSettings = null;
            if (format != VideoCatalog.GifFormat)
            {
                EnsureCodecCompatible();
                var codec = VideoCatalog.GetCodec(Cfg.VideoCodec);
                if (_availableEncoders is not null && !_availableEncoders.Contains(codec.Id))
                {
                    StatusText = $"当前 FFmpeg 不支持编码器 {codec.Id}，已改用 {VideoCatalog.DefaultCodecFor(format, _availableEncoders)}";
                    Cfg.VideoCodec = VideoCatalog.DefaultCodecFor(format, _availableEncoders);
                }
                videoSettings = VideoEncoderSettings.Normalize(Cfg.VideoCodec, Cfg.VideoRateMode, Cfg.VideoQuality, Cfg.VideoBitrateMbps);
            }

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
            var stamp = DateTime.Now;
            var extension = format == VideoCatalog.GifFormat ? "gif" : container.Extension;
            var outputPath = RecordingNames.BuildOutputPath(
                outputFolder, RecordingNames.SanitizePrefix(Cfg.FilePrefix), stamp, extension);
            var tempPath = _cache.PathOf($"capture-{stamp:yyyyMMdd-HHmmss-fff}.mkv");

            // 摄像头屏幕覆盖：录之前确保它开着（它就是屏幕上的窗口，会被一起录到）
            if (Cfg.EnableCamera && Cfg.CameraOverlayEnabled && !IsCameraOverlayShown)
            {
                try
                {
                    if (await EnsureCameraRunningAsync()) ShowCameraOverlay();
                }
                catch (Exception ex)
                {
                    Log("⚠ 摄像头覆盖显示失败（继续录画面）：" + ex.Message);
                }
            }
            else if (Cfg.EnableCamera && !Cfg.CameraOverlayEnabled)
            {
                Log("提示：摄像头已启用但「屏幕覆盖」没开，摄像头不会录进画面。");
            }

            // 拍快照：录制中不允许改格式 / 来源 / 编码器（音量与摄像头位置仍可调）
            _activePlan = plan;
            _activeFormat = format;
            _activeGif = GifExportSettings.Normalize(Cfg.Fps, Cfg.ScaleWidth, Cfg.GifColors, Cfg.GifDither, Cfg.GifLoop);
            _activeVideo = videoSettings;
            _activeVideoCodecName = videoSettings is null ? "" : VideoCatalog.GetCodec(videoSettings.CodecId).CodecName;
            _activeOutputPath = outputPath;
            _activeTempPath = tempPath;
            _activeAudioBitrate = Cfg.AudioBitrateKbps;
            _activeAudioPaths = Array.Empty<string>();
            _activeAudioDelayMs = 0;
            _activeOpenFolder = Cfg.OpenFolderAfterSave;
            _finalizeTask = null;
            _stopInProgress = false;
            _cancelRequested = false;
            _lastAudioError = "";

            _startedAt = DateTime.Now;
            var videoStartedAt = _startedAt;

            try
            {
                _session = ScreenCaptureSession.Start(_ffmpegPath!, plan, tempPath, videoSettings, Cfg.ScaleWidth);
            }
            catch (Exception ex)
            {
                StatusText = "启动录制失败：" + ex.Message;
                Log("✗ 启动录制失败：" + ex);
                _session = null;
                CleanupAudioTemps();
                return;
            }

            // 先起画面再起声音：声音晚启动的毫秒数记下来，收尾时用 adelay 补静音对齐。
            try
            {
                StartAudioRecorders(videoStartedAt);
            }
            catch (Exception ex)
            {
                Log("⚠ 声音录制启动异常（继续录画面）：" + ex.Message);
                StopAudioRecorders();
            }

            IsRecording = true;
            ElapsedText = "00:00";
            _elapsedTimer.Start();
            ShowOverlay(plan, region.Value);
            UpdateElapsedText();
            StatusText = $"录制中… {SourceSummary}（{StopHintText}）";

            Log($"===== 开始录制：{plan.SourceLabel} · {plan.Region} · {plan.Fps} FPS · {format.ToUpperInvariant()} =====");
            if (videoSettings is not null)
                Log($"编码：{_activeVideoCodecName} · {(videoSettings.RateMode == VideoEncoderSettings.RateBitrate ? $"{videoSettings.BitrateMbps} Mbps" : $"CRF {videoSettings.Quality}")} · 输出宽度 {Cfg.ScaleWidth} · 容器 {container.ShortLabel}");
            else
                Log($"GIF 参数：{_activeGif.MaxColors} 色 · 抖动 {_activeGif.Dither} · 宽度 {_activeGif.ScaleWidth} · {(_activeGif.Loop ? "无限循环" : "只播一次")}");
            if (Cfg.RecordMicrophone || Cfg.RecordSystemAudio)
                Log("声音：" + AudioSummary);

            _ = WatchCaptureExitAsync(_session);
        }
        catch (Exception ex)
        {
            StatusText = "开始录制失败：" + ex.Message;
            Log("✗ " + ex);
        }
    }

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
        IsFinalizing = true;
        _elapsedTimer.Stop();
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

            _recordedDuration = DateTime.Now - _startedAt;
            UpdateElapsedText();

            StopAudioRecorders();
            _activeAudioPaths = CollectAudioPaths();
            if (_activeAudioPaths.Count > 0)
                Log($"声音轨道：{_activeAudioPaths.Count} 路");

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
            _ = StopCameraIfUnusedAsync();
        }
    }

    private Task FinalizeOnceAsync(bool autoStopped) => _finalizeTask ??= FinalizeCoreAsync(autoStopped);

    /// <summary>取消正在进行的最终封装 / 混音（中间素材不删）。</summary>
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
        var audioPaths = _activeAudioPaths;
        var audioBitrate = _activeAudioBitrate;
        var audioDelay = _activeAudioDelayMs;
        var videoCodecName = _activeVideoCodecName;
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
            if (format == VideoCatalog.GifFormat && _activeGif is not null)
            {
                result = await RecordingExporter.ExportGifAsync(
                    _ffmpegPath, plan.Region, _activeGif, tempPath, outputPath, progress, token);
            }
            else
            {
                result = await RecordingExporter.ExportVideoAsync(
                    _ffmpegPath, tempPath, outputPath, format, audioPaths, audioBitrate,
                    _activeVideo?.CodecId, audioDelay, progress, token);
            }

            if (!result.Success)
            {
                TryDelete(outputPath);
                StatusText = "生成文件失败：" + result.Error;
                Log("✗ " + result.Error);
                Log("中间素材保留在：" + tempPath);
                return;
            }

            var info = new FileInfo(outputPath);
            var duration = RecordingTime.FormatDuration(_recordedDuration);
            var formatText = format == VideoCatalog.GifFormat
                ? "GIF"
                : $"{VideoCatalog.GetContainer(format).ShortLabel} · {videoCodecName}";
            var item = new RecordingItem
            {
                FilePath = outputPath,
                FileName = Path.GetFileName(outputPath),
                FormatText = formatText,
                SizeBytes = info.Length,
                CreatedAt = _startedAt,
                DurationText = duration,
            };
            Recordings.Insert(0, item);
            while (Recordings.Count > MaxRecentItems) Recordings.RemoveAt(Recordings.Count - 1);

            LastOutputPath = outputPath;
            TryDelete(tempPath);
            CleanupAudioTemps();
            StatusText = $"已保存：{item.FileName}（{item.SizeText}，录制 {duration}）";
            Log($"✓ 已保存：{outputPath}（{item.SizeText}）");

            if (_activeOpenFolder) TryOpenFolder(Path.GetDirectoryName(outputPath));
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
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
                TryDelete(outputPath);
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

    // ================================================================== 控制条 / 计时

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
        var formatText = _activeFormat == VideoCatalog.GifFormat ? "GIF" : VideoCatalog.GetContainer(_activeFormat).ShortLabel;
        _overlay.ShowFor($"{plan.Region.SizeText} · {plan.Fps} FPS · {formatText}", _startedAt, region, isFullScreen);
    }

    private void HideOverlay()
    {
        if (_overlay is null) return;
        _overlay.StopRequested -= OnOverlayStopRequested;
        _overlay.StopAndHide();
    }

    private void OnOverlayStopRequested(object? sender, EventArgs e) => _ = StopFromUiAsync();

    private void UpdateElapsedText()
    {
        if (!IsRecording) return;
        ElapsedText = RecordingTime.FormatDuration(DateTime.Now - _startedAt);
        CheckAudioRecorderErrors();
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

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 留给「清理缓存」 */ }
    }

    // ================================================================== 生命周期

    protected override void OnClosed()
    {
        _shuttingDown = true;

        try { _elapsedTimer.Stop(); } catch { /* 忽略 */ }
        try { _hotkeys.UnregisterAll(); } catch { /* 忽略 */ }
        try { _hotkeys.Dispose(); } catch { /* 忽略 */ }
        try { _countdownCts?.Cancel(); } catch { /* 忽略 */ }
        try { _overlay?.StopAndHide(); } catch { /* 忽略 */ }
        try { _overlay?.CloseNow(); } catch { /* 忽略 */ }
        _overlay = null;

        var countdown = _countdownWindow;
        _countdownWindow = null;
        try { countdown?.CloseNow(); } catch { /* 忽略 */ }

        try { _cameraOverlay?.HideOverlay(); } catch { /* 忽略 */ }
        try { _cameraOverlay?.CloseNow(); } catch { /* 忽略 */ }
        _cameraOverlay = null;

        try { Task.Run(() => _camera.StopAsync()).Wait(TimeSpan.FromSeconds(3)); } catch { /* 忽略 */ }
        try { _camera.Dispose(); } catch { /* 忽略 */ }

        StopAudioRecorders();

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
                    var appExiting = Application.Current?.MainWindow is not { IsLoaded: true };
                    _activeAudioPaths = CollectAudioPaths();
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

        if (Application.Current?.MainWindow is not { IsLoaded: true })
        {
            try { _exportCts?.Cancel(); } catch { /* 忽略 */ }
        }
    }

    private void FinalizeForShutdown()
    {
        var temp = _activeTempPath;
        if (string.IsNullOrWhiteSpace(temp) || !File.Exists(temp)) return;
        if (string.IsNullOrWhiteSpace(_ffmpegPath) || _activePlan is null) return;

        var result = _activeFormat == VideoCatalog.GifFormat && _activeGif is not null
            ? RecordingExporter.ExportGifAsync(_ffmpegPath, _activePlan.Region, _activeGif, temp, _activeOutputPath, null, CancellationToken.None)
                .GetAwaiter().GetResult()
            : RecordingExporter.ExportVideoAsync(_ffmpegPath, temp, _activeOutputPath, _activeFormat,
                    _activeAudioPaths, _activeAudioBitrate, _activeVideo?.CodecId, _activeAudioDelayMs, null, CancellationToken.None)
                .GetAwaiter().GetResult();

        if (result.Success)
        {
            TryDelete(temp);
            CleanupAudioTemps();
        }
    }
}
