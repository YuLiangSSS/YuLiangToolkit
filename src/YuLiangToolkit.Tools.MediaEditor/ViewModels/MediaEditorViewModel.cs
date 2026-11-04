using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Services;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.MediaEditor.Services;
using YuLiangToolkit.Tools.ScreenRecorder;

namespace YuLiangToolkit.Tools.MediaEditor.ViewModels;

/// <summary>时间轴上的一张缩略图。</summary>
public sealed class ThumbnailItem
{
    /// <summary>缩略图（已冻结，可跨线程）。</summary>
    public required BitmapImage Image { get; init; }

    /// <summary>这张图对应的时间（秒，近似值）。</summary>
    public required double TimeSeconds { get; init; }

    /// <summary>时间文案。</summary>
    public string TimeText => EditorTime.Format(TimeSeconds);
}

/// <summary>一个要保留的片段（界面列表项）。</summary>
public sealed class SegmentItem
{
    /// <summary>起点（秒）。</summary>
    public required double Start { get; init; }

    /// <summary>终点（秒）。</summary>
    public required double End { get; init; }

    /// <summary>长度（秒）。</summary>
    public double Duration => Math.Max(0, End - Start);

    /// <summary>起止文案。</summary>
    public string RangeText => $"{EditorTime.Format(Start)} → {EditorTime.Format(End)}";

    /// <summary>时长文案。</summary>
    public string DurationText => EditorTime.Format(Duration);
}

/// <summary>
/// 「剪辑工具」视图模型：不实时渲染画面，而是用「生成缩略图 + 生成低清预览」的方式帮用户定位和确认。
///
/// <para><b>基本流程</b>：选择视频 / GIF → 探测时长和分辨率 → 生成时间轴缩略图 →
/// 用播放头（滑块 / 点缩略图）设起点 / 终点 → 「添加片段」加入保留列表（可多个 / 可排序）→
/// 预览（低清 mp4，最长 10 秒，打开系统播放器）或导出（视频 / GIF）。</para>
///
/// <para><b>不做的事</b>：不做逐帧实时预览、不做多轨 / 特效 / 变速 / 字幕；只做裁剪 + 多段拼接。</para>
/// </summary>
public sealed class MediaEditorViewModel : ToolViewModelBase
{
    private readonly ToolCache _cache = ToolCache.For(MediaEditorConfig.ToolId);

    private string? _ffmpegPath;
    private IReadOnlySet<string>? _availableEncoders;
    private CancellationTokenSource? _cts;

    private MediaInfo? _media;
    private string _inputPath = "";
    private string _mediaSummary = "还没有选择文件。支持视频（MP4 / MKV / MOV / AVI / WebM / FLV / TS 等）和 GIF。";
    private double _positionSeconds;
    private string _startInputText = "00:00.000";
    private string _endInputText = "00:00.000";
    private string _lastPreviewPath = "";
    private string _lastOutputPath = "";
    private string _ffmpegStatusText = "正在检测 FFmpeg…";
    private ThumbnailItem? _selectedThumbnail;
    private SegmentItem? _selectedSegment;
    private ImageSource? _previewImage;

    public MediaEditorViewModel() : base(MediaEditorConfig.ToolId)
    {
        Cfg = LoadConfig<MediaEditorConfig>();
        NormalizeConfigValues();

        var formats = new List<OptionItem<string>> { new("gif", "动图 GIF（两遍调色板）") };
        foreach (var container in VideoCatalog.Containers)
            formats.Add(new OptionItem<string>(container.Id, container.Label));
        FormatOptions = formats;

        RateModeOptions = OptionItem.From(
            (VideoEncoderSettings.RateQuality, "画质优先（CRF / CQ，推荐）"),
            (VideoEncoderSettings.RateBitrate, "码率优先（固定 Mbps）"));

        FpsOptions = OptionItem.FromValues(EditorOptions.FpsChoices,
            value => value <= 0 ? "保持源帧率" : $"{value} FPS");

        WidthOptions = OptionItem.FromValues(EditorOptions.WidthChoices,
            value => value <= 0 ? "原始分辨率" : $"{value} px 宽");

        ThumbnailCountOptions = OptionItem.FromValues(EditorOptions.ThumbnailCountChoices,
            value => $"{value} 张");

        GifColorOptions = OptionItem.FromValues(RecorderOptions.GifColorChoices, value => value switch
        {
            256 => "256 色（最像原图）",
            128 => "128 色（平衡）",
            _ => "64 色（最小）",
        });

        DitherOptions = OptionItem.From(
            ("floyd_steinberg", "Floyd–Steinberg（平滑渐变）"),
            ("bayer", "有序（像素风 / 文件更小）"),
            ("none", "无（色块明显 / 最小）"));

        AudioBitrateOptions = OptionItem.FromValues(RecorderOptions.AudioBitrateChoices,
            value => $"{value} kbps");

        PickInputCommand = new AsyncRelayCommand(_ => PickInputAsync(), _ => !IsBusy);
        ReloadInputCommand = new AsyncRelayCommand(_ => ReloadInputAsync(), _ => !IsBusy && File.Exists(InputPath));
        GenerateThumbnailsCommand = new AsyncRelayCommand(_ => GenerateThumbnailsAsync(), _ => !IsBusy && Media is not null);
        AddSegmentCommand = new RelayCommand(_ => AddSegment(), _ => !IsBusy && Media is not null);
        UpdateSegmentCommand = new RelayCommand(_ => UpdateSegment(), _ => !IsBusy && SelectedSegment is not null);
        DeleteSegmentCommand = new RelayCommand(_ => DeleteSegment(), _ => !IsBusy && SelectedSegment is not null);
        ClearSegmentsCommand = new RelayCommand(_ => ClearSegments(), _ => !IsBusy && Segments.Count > 0);
        RestoreFullSegmentCommand = new RelayCommand(_ => RestoreFullSegment(), _ => !IsBusy && Media is not null);
        MoveSegmentUpCommand = new RelayCommand(_ => MoveSegment(-1), _ => !IsBusy && CanMoveSegment(-1));
        MoveSegmentDownCommand = new RelayCommand(_ => MoveSegment(1), _ => !IsBusy && CanMoveSegment(1));
        SetStartCommand = new RelayCommand(_ => SetStartFromPosition(), _ => !IsBusy && Media is not null);
        SetEndCommand = new RelayCommand(_ => SetEndFromPosition(), _ => !IsBusy && Media is not null);
        SelectThumbnailCommand = new RelayCommand(parameter => SelectThumbnail(parameter as ThumbnailItem));
        GeneratePreviewCommand = new AsyncRelayCommand(_ => GeneratePreviewAsync(), _ => !IsBusy && Media is not null && Segments.Count > 0);
        ExportCommand = new AsyncRelayCommand(_ => ExportAsync(), _ => !IsBusy && Media is not null && Segments.Count > 0);
        CancelCommand = new RelayCommand(_ => CancelCurrent(), _ => IsBusy);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutputFolder(), _ => !IsBusy);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
        OpenInputCommand = new RelayCommand(_ => OpenFile(InputPath), _ => File.Exists(InputPath));
        RevealInputCommand = new RelayCommand(_ => RevealFile(InputPath), _ => File.Exists(InputPath));
        OpenPreviewCommand = new RelayCommand(_ => OpenFile(LastPreviewPath), _ => File.Exists(LastPreviewPath));
        OpenLastOutputCommand = new RelayCommand(_ => OpenFile(LastOutputPath), _ => File.Exists(LastOutputPath));
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        ClearLogCommand = new RelayCommand(_ => ClearLog());
        BrowseFFmpegCommand = new RelayCommand(_ => BrowseFFmpeg());
        OpenFfmpegDownloadCommand = new RelayCommand(_ => OpenFfmpegDownload());

        Cfg.PropertyChanged += OnConfigChanged;
        RebuildCodecOptions();

        Log($"配置文件：{Cfg.FilePath}");
        Log($"预览 / 缩略图缓存：{_cache.Directory}");
        Log($"输出文件夹：{ResolveOutputFolder()}");
        _ = InitAsync();
    }

    // ================================================================== 配置 / 选项

    /// <summary>本功能全部选项（改动自动保存）。</summary>
    public MediaEditorConfig Cfg { get; }

    public IReadOnlyList<OptionItem<string>> FormatOptions { get; }
    public IReadOnlyList<OptionItem<string>> RateModeOptions { get; }
    public IReadOnlyList<OptionItem<int>> FpsOptions { get; }
    public IReadOnlyList<OptionItem<int>> WidthOptions { get; }
    public IReadOnlyList<OptionItem<int>> ThumbnailCountOptions { get; }
    public IReadOnlyList<OptionItem<int>> GifColorOptions { get; }
    public IReadOnlyList<OptionItem<string>> DitherOptions { get; }
    public IReadOnlyList<OptionItem<int>> AudioBitrateOptions { get; }

    /// <summary>当前容器下可用的编码器。</summary>
    public ObservableCollection<OptionItem<string>> CodecOptions { get; } = new();

    /// <summary>时间轴缩略图。</summary>
    public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();

    /// <summary>要保留的片段（按列表顺序拼接）。</summary>
    public ObservableCollection<SegmentItem> Segments { get; } = new();

    public OptionItem<string> SelectedFormat
    {
        get => OptionItem<string>.Find(FormatOptions, EditorOptions.NormalizeFormat(Cfg.Format)) ?? FormatOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.Format) return;
            Cfg.Format = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<string> SelectedRateMode
    {
        get => OptionItem<string>.Find(RateModeOptions, Cfg.RateMode) ?? RateModeOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.RateMode) return;
            Cfg.RateMode = value.Value;
            OnPropertyChanged();
        }
    }

    public int SelectedCodecIndex
    {
        get => CodecOptions.IndexOf(OptionItem<string>.Find(CodecOptions, Cfg.VideoCodec) ?? CodecOptions.FirstOrDefault()!);
        set
        {
            if (value < 0 || value >= CodecOptions.Count) return;
            Cfg.VideoCodec = CodecOptions[value].Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedFps
    {
        get => OptionItem<int>.Find(FpsOptions, EditorOptions.NormalizeFps(Cfg.Fps)) ?? FpsOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.Fps) return;
            Cfg.Fps = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedWidth
    {
        get => OptionItem<int>.Find(WidthOptions, EditorOptions.NormalizeWidth(Cfg.ScaleWidth)) ?? WidthOptions[0];
        set
        {
            if (value is null || value.Value == Cfg.ScaleWidth) return;
            Cfg.ScaleWidth = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedThumbnailCount
    {
        get => OptionItem<int>.Find(ThumbnailCountOptions, Cfg.ThumbnailCount) ?? ThumbnailCountOptions[2];
        set
        {
            if (value is null || value.Value == Cfg.ThumbnailCount) return;
            Cfg.ThumbnailCount = value.Value;
            OnPropertyChanged();
        }
    }

    public OptionItem<int> SelectedGifColors
    {
        get => OptionItem<int>.Find(GifColorOptions, RecorderOptions.NormalizeColors(Cfg.GifColors)) ?? GifColorOptions[0];
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
        get => OptionItem<int>.Find(AudioBitrateOptions, EditorOptions.NormalizeAudioBitrate(Cfg.AudioBitrateKbps)) ?? AudioBitrateOptions[1];
        set
        {
            if (value is null || value.Value == Cfg.AudioBitrateKbps) return;
            Cfg.AudioBitrateKbps = value.Value;
            OnPropertyChanged();
        }
    }

    // ================================================================== 状态

    /// <summary>源文件路径。</summary>
    public string InputPath
    {
        get => _inputPath;
        private set
        {
            if (!Set(ref _inputPath, value)) return;
            OnPropertyChanged(nameof(InputFileName));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>源文件名。</summary>
    public string InputFileName => Path.GetFileName(InputPath);

    /// <summary>媒体信息（未载入为 null）。</summary>
    public MediaInfo? Media
    {
        get => _media;
        private set
        {
            if (!Set(ref _media, value)) return;
            OnPropertyChanged(nameof(HasMedia));
            OnPropertyChanged(nameof(MediaSummary));
            OnPropertyChanged(nameof(EditedDurationText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>是否已载入文件。</summary>
    public bool HasMedia => Media is not null;

    /// <summary>媒体信息摘要。</summary>
    public string MediaSummary
    {
        get
        {
            if (Media is not { } media) return _mediaSummary;
            return $"{media.FileName}\n"
                   + $"时长 {media.DurationText} · {media.ResolutionText} · {media.FpsText}\n"
                   + $"视频 {media.VideoCodec} · 音频 {media.AudioText} · {media.SizeText}";
        }
    }

    /// <summary>播放头位置（秒）。</summary>
    public double PositionSeconds
    {
        get => _positionSeconds;
        set
        {
            var max = Media?.DurationSeconds ?? 0;
            var clamped = Math.Clamp(value, 0, max);
            if (!Set(ref _positionSeconds, clamped)) return;
            OnPropertyChanged(nameof(PositionText));
            var nearest = Thumbnails
                .OrderBy(item => Math.Abs(item.TimeSeconds - clamped))
                .FirstOrDefault();
            if (nearest is not null)
            {
                SelectedThumbnail = nearest;
                PreviewImage = nearest.Image;
            }
        }
    }

    /// <summary>播放头时间文案。</summary>
    public string PositionText => EditorTime.Format(PositionSeconds) + $" / {EditorTime.Format(Media?.DurationSeconds ?? 0)}";

    /// <summary>起点输入文本（mm:ss.fff / 秒数都认）。</summary>
    public string StartInputText
    {
        get => _startInputText;
        set => Set(ref _startInputText, value);
    }

    /// <summary>终点输入文本。</summary>
    public string EndInputText
    {
        get => _endInputText;
        set => Set(ref _endInputText, value);
    }

    /// <summary>当前选中的缩略图。</summary>
    public ThumbnailItem? SelectedThumbnail
    {
        get => _selectedThumbnail;
        set => Set(ref _selectedThumbnail, value);
    }

    /// <summary>当前选中的片段。</summary>
    public SegmentItem? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            if (!Set(ref _selectedSegment, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>右侧大预览图（当前缩略图）。</summary>
    public ImageSource? PreviewImage
    {
        get => _previewImage;
        private set => Set(ref _previewImage, value);
    }

    /// <summary>所有保留片段的合计时长文案。</summary>
    public string EditedDurationText
    {
        get
        {
            var total = Segments.Sum(item => item.Duration);
            return $"保留 {Segments.Count} 个片段 · 合计 {EditorTime.Format(total)}";
        }
    }

    /// <summary>最近一次预览文件。</summary>
    public string LastPreviewPath
    {
        get => _lastPreviewPath;
        private set
        {
            if (!Set(ref _lastPreviewPath, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>最近一次导出文件。</summary>
    public string LastOutputPath
    {
        get => _lastOutputPath;
        private set
        {
            if (!Set(ref _lastOutputPath, value)) return;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>FFmpeg 状态。</summary>
    public string FFmpegStatusText
    {
        get => _ffmpegStatusText;
        private set => Set(ref _ffmpegStatusText, value);
    }

    /// <summary>当前是不是 GIF 导出。</summary>
    public bool IsGif => Cfg.IsGif;

    /// <summary>当前是不是视频导出。</summary>
    public bool IsVideo => Cfg.IsVideo;

    /// <summary>画质优先模式。</summary>
    public bool IsQualityMode => Cfg.RateMode != VideoEncoderSettings.RateBitrate;

    /// <summary>码率优先模式。</summary>
    public bool IsBitrateMode => !IsQualityMode;

    // ================================================================== 命令

    public AsyncRelayCommand PickInputCommand { get; }
    public AsyncRelayCommand ReloadInputCommand { get; }
    public AsyncRelayCommand GenerateThumbnailsCommand { get; }
    public RelayCommand AddSegmentCommand { get; }
    public RelayCommand UpdateSegmentCommand { get; }
    public RelayCommand DeleteSegmentCommand { get; }
    public RelayCommand ClearSegmentsCommand { get; }
    public RelayCommand RestoreFullSegmentCommand { get; }
    public RelayCommand MoveSegmentUpCommand { get; }
    public RelayCommand MoveSegmentDownCommand { get; }
    public RelayCommand SetStartCommand { get; }
    public RelayCommand SetEndCommand { get; }
    public RelayCommand SelectThumbnailCommand { get; }
    public AsyncRelayCommand GeneratePreviewCommand { get; }
    public AsyncRelayCommand ExportCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand OpenInputCommand { get; }
    public RelayCommand RevealInputCommand { get; }
    public RelayCommand OpenPreviewCommand { get; }
    public RelayCommand OpenLastOutputCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand BrowseFFmpegCommand { get; }
    public RelayCommand OpenFfmpegDownloadCommand { get; }

    // ================================================================== 配置联动

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MediaEditorConfig.Format):
                EnsureCodecCompatible();
                RebuildCodecOptions();
                OnPropertyChanged(nameof(SelectedFormat));
                break;
            case nameof(MediaEditorConfig.VideoCodec):
                OnPropertyChanged(nameof(SelectedCodecIndex));
                break;
            case nameof(MediaEditorConfig.RateMode):
                OnPropertyChanged(nameof(SelectedRateMode));
                OnPropertyChanged(nameof(IsQualityMode));
                OnPropertyChanged(nameof(IsBitrateMode));
                break;
            case nameof(MediaEditorConfig.Fps):
                OnPropertyChanged(nameof(SelectedFps));
                break;
            case nameof(MediaEditorConfig.ScaleWidth):
                OnPropertyChanged(nameof(SelectedWidth));
                break;
            case nameof(MediaEditorConfig.ThumbnailCount):
                OnPropertyChanged(nameof(SelectedThumbnailCount));
                break;
            case nameof(MediaEditorConfig.GifColors):
                OnPropertyChanged(nameof(SelectedGifColors));
                break;
            case nameof(MediaEditorConfig.GifDither):
                OnPropertyChanged(nameof(SelectedDither));
                break;
            case nameof(MediaEditorConfig.AudioBitrateKbps):
                OnPropertyChanged(nameof(SelectedAudioBitrate));
                break;
        }

        OnPropertyChanged(nameof(IsGif));
        OnPropertyChanged(nameof(IsVideo));
        OnPropertyChanged(nameof(SelectedCodecIndex));
    }

    private void NormalizeConfigValues()
    {
        EditorOptions.NormalizeConfig(Cfg);

        if (string.IsNullOrWhiteSpace(Cfg.OutputFolder))
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Cfg.OutputFolder = Path.Combine(videos, "YuLiangToolkit 剪辑");
        }

        EnsureCodecCompatible();
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
        if (compatible.Count == 0) compatible = VideoCatalog.CompatibleCodecs(Cfg.Format, null);

        var same = compatible.Count == CodecOptions.Count
                   && compatible.Select(c => c.Id).SequenceEqual(CodecOptions.Select(o => o.Value), StringComparer.OrdinalIgnoreCase);
        if (!same)
        {
            CodecOptions.Clear();
            foreach (var codec in compatible)
                CodecOptions.Add(new OptionItem<string>(codec.Id, codec.Label));
        }

        if (CodecOptions.Count > 0 && !CodecOptions.Any(o => o.Value == Cfg.VideoCodec))
            Cfg.VideoCodec = CodecOptions[0].Value;

        OnPropertyChanged(nameof(SelectedCodecIndex));
        Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(SelectedCodecIndex))));
    }

    // ================================================================== 初始化 / FFmpeg

    private async Task InitAsync()
    {
        var found = FFmpegLocator.Find(AppSettings.Settings.FFmpegPath);
        if (found is null)
        {
            FFmpegStatusText = "⚠️ 未检测到 FFmpeg：请「选择 FFmpeg…」指定 ffmpeg.exe";
            return;
        }

        _ffmpegPath = found;
        AppSettings.Settings.FFmpegPath ??= found;
        AppSettings.Save();
        var version = await FFmpegRunner.GetVersionAsync(found);
        Ui(() => FFmpegStatusText = $"✅ FFmpeg 已就绪（v{version ?? "?"}）· {found}");

        var encoders = await FFmpegRunner.RunAsync(found, new[] { "-hide_banner", "-encoders" });
        _availableEncoders = EncoderCatalog.ParseAvailable(encoders.Output + Environment.NewLine + encoders.Error);
        if (_availableEncoders.Count == 0) _availableEncoders = null;
        RebuildCodecOptions();
        EnsureCodecCompatible();

        // 接着上次的文件：只在文件还在时自动载入，不阻塞窗口
        if (!string.IsNullOrWhiteSpace(Cfg.LastInputPath) && File.Exists(Cfg.LastInputPath))
        {
            Log("自动载入上次的文件：" + Cfg.LastInputPath);
            await LoadInputAsync(Cfg.LastInputPath);
        }
    }

    private bool EnsureFFmpeg()
    {
        if (_ffmpegPath is not null && File.Exists(_ffmpegPath)) return true;
        var found = FFmpegLocator.Find(AppSettings.Settings.FFmpegPath);
        if (found is not null && File.Exists(found))
        {
            _ffmpegPath = found;
            AppSettings.Settings.FFmpegPath = found;
            AppSettings.Save();
            return true;
        }

        StatusText = "未检测到 FFmpeg：请先指定 ffmpeg.exe";
        return false;
    }

    private void BrowseFFmpeg()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 ffmpeg.exe",
            Filter = "FFmpeg 可执行文件|ffmpeg.exe|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;
        _ffmpegPath = dialog.FileName;
        AppSettings.Settings.FFmpegPath = dialog.FileName;
        AppSettings.Save();
        FFmpegStatusText = "正在校验：" + dialog.FileName + "…";
        _ = Task.Run(async () =>
        {
            var version = await FFmpegRunner.GetVersionAsync(dialog.FileName);
            var found = dialog.FileName;
            Ui(async () =>
            {
                FFmpegStatusText = $"✅ FFmpeg 已就绪（v{version ?? "?"}）· {found}";
                var encoders = await FFmpegRunner.RunAsync(found, new[] { "-hide_banner", "-encoders" });
                _availableEncoders = EncoderCatalog.ParseAvailable(encoders.Output + Environment.NewLine + encoders.Error);
                if (_availableEncoders.Count == 0) _availableEncoders = null;
                RebuildCodecOptions();
                EnsureCodecCompatible();
            });
        });
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

    private void SetBusy(bool busy)
    {
        IsBusy = busy;
        CommandManager.InvalidateRequerySuggested();
    }

    private void CancelCurrent()
    {
        try { _cts?.Cancel(); } catch { /* 已结束 */ }
        StatusText = "正在取消…";
    }

    // ================================================================== 载入 / 缩略图

    private async Task PickInputAsync()
    {
        if (IsBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择要剪辑的视频 / GIF",
            Filter = "视频 / GIF|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.gif;*.flv;*.ts;*.m4v;*.wmv;*.mpg;*.mpeg|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true) await LoadInputAsync(dialog.FileName);
    }

    private Task ReloadInputAsync() => File.Exists(InputPath) ? LoadInputAsync(InputPath) : Task.CompletedTask;

    private async Task LoadInputAsync(string path)
    {
        if (IsBusy) return;
        if (!File.Exists(path))
        {
            StatusText = "文件不存在：" + path;
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        StatusText = "正在读取媒体信息…";
        try
        {
            if (!EnsureFFmpeg()) return;

            var (info, error) = await MediaProbeRunner.ProbeAsync(_ffmpegPath!, path, token);
            token.ThrowIfCancellationRequested();
            if (info is null)
            {
                StatusText = error;
                Log("✗ " + error);
                return;
            }

            InputPath = path;
            Media = info;
            Cfg.LastInputPath = path;
            PositionSeconds = 0;
            StartInputText = EditorTime.Format(0);
            EndInputText = EditorTime.Format(info.DurationSeconds);
            Thumbnails.Clear();
            PreviewImage = null;
            Segments.Clear();
            var full = new SegmentItem { Start = 0, End = info.DurationSeconds };
            Segments.Add(full);
            SelectedSegment = full;
            NotifySegmentsChanged();

            StatusText = $"已载入：{info.FileName}";
            Log($"===== 载入：{path}");
            Log($"媒体：{info.DurationText} · {info.ResolutionText} · {info.FpsText} · 视频 {info.VideoCodec} · 音频 {info.AudioText}");

            await GenerateThumbnailsCoreAsync(info, token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消载入";
        }
        catch (Exception ex)
        {
            StatusText = "载入失败：" + ex.Message;
            Log("✗ 载入失败：" + ex);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            Progress = 0;
        }
    }

    private async Task GenerateThumbnailsAsync()
    {
        if (IsBusy || Media is null) return;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        try
        {
            await GenerateThumbnailsCoreAsync(Media, token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消生成缩略图";
        }
        catch (Exception ex)
        {
            StatusText = "生成缩略图失败：" + ex.Message;
            Log("✗ " + ex);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task GenerateThumbnailsCoreAsync(MediaInfo media, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_ffmpegPath)) return;
        StatusText = "正在生成时间轴缩略图…";
        var directory = _cache.PathOf($"thumbs-{Guid.NewGuid():N}");
        var (files, error) = await ThumbnailService.GenerateAsync(
            _ffmpegPath, media.FilePath, media.DurationSeconds, Cfg.ThumbnailCount, directory, token);
        token.ThrowIfCancellationRequested();

        Thumbnails.Clear();
        foreach (var file in files)
        {
            var image = ThumbnailService.Load(file.FilePath);
            if (image is null) continue;
            Thumbnails.Add(new ThumbnailItem { Image = image, TimeSeconds = file.TimeSeconds });
        }

        if (Thumbnails.Count > 0)
        {
            SelectedThumbnail = Thumbnails[0];
            PreviewImage = SelectedThumbnail.Image;
            StatusText = $"缩略图已生成（{Thumbnails.Count} 张）";
            Log($"缩略图：{Thumbnails.Count} 张 → {directory}");
        }
        else
        {
            StatusText = error.Length > 0 ? error : "没有生成缩略图";
            Log("⚠ " + StatusText);
        }
    }

    private void SelectThumbnail(ThumbnailItem? item)
    {
        if (item is null) return;
        PositionSeconds = item.TimeSeconds;
        SelectedThumbnail = item;
        PreviewImage = item.Image;
    }

    private void SetStartFromPosition() => StartInputText = EditorTime.Format(PositionSeconds);

    private void SetEndFromPosition() => EndInputText = EditorTime.Format(PositionSeconds);

    // ================================================================== 片段列表

    private bool TryReadInputRange(out SegmentRange range)
    {
        range = default;
        if (Media is not { } media)
        {
            StatusText = "先选择文件";
            return false;
        }
        if (!EditorTime.TryParse(StartInputText, out var start) || !EditorTime.TryParse(EndInputText, out var end))
        {
            StatusText = "起止时间格式不对：可以写 12.5、00:12.500、1:02:03.500";
            return false;
        }

        var normalized = EditorMath.NormalizeSegment(start, end, media.DurationSeconds);
        if (normalized is null)
        {
            StatusText = $"片段无效：需要 0 ~ {EditorTime.Format(media.DurationSeconds)}，且至少 {EditorMath.MinSegmentSeconds:0.##} 秒";
            return false;
        }

        range = normalized.Value;
        return true;
    }

    private void AddSegment()
    {
        if (!TryReadInputRange(out var range)) return;
        var item = new SegmentItem { Start = range.Start, End = range.End };
        Segments.Add(item);
        SelectedSegment = item;
        NotifySegmentsChanged();
        StatusText = "已添加片段：" + item.RangeText;
        Log("＋ 片段 " + item.RangeText);
    }

    private void UpdateSegment()
    {
        if (SelectedSegment is null || !TryReadInputRange(out var range)) return;
        var index = Segments.IndexOf(SelectedSegment);
        if (index < 0) return;
        var item = new SegmentItem { Start = range.Start, End = range.End };
        Segments[index] = item;
        SelectedSegment = item;
        NotifySegmentsChanged();
        StatusText = "已更新片段：" + item.RangeText;
    }

    private void DeleteSegment()
    {
        if (SelectedSegment is null) return;
        var index = Segments.IndexOf(SelectedSegment);
        if (index < 0) return;
        Segments.RemoveAt(index);
        SelectedSegment = Segments.Count == 0 ? null : Segments[Math.Min(index, Segments.Count - 1)];
        NotifySegmentsChanged();
        StatusText = "已删除片段";
    }

    private void ClearSegments()
    {
        Segments.Clear();
        SelectedSegment = null;
        NotifySegmentsChanged();
        StatusText = "已清空片段（导出前请至少保留一个）";
    }

    private void RestoreFullSegment()
    {
        if (Media is not { } media) return;
        Segments.Clear();
        var full = new SegmentItem { Start = 0, End = media.DurationSeconds };
        Segments.Add(full);
        SelectedSegment = full;
        StartInputText = EditorTime.Format(0);
        EndInputText = EditorTime.Format(media.DurationSeconds);
        NotifySegmentsChanged();
        StatusText = "已恢复整段";
    }

    private bool CanMoveSegment(int delta)
    {
        if (SelectedSegment is null) return false;
        var index = Segments.IndexOf(SelectedSegment);
        return index >= 0 && index + delta >= 0 && index + delta < Segments.Count;
    }

    private void MoveSegment(int delta)
    {
        if (SelectedSegment is null) return;
        var index = Segments.IndexOf(SelectedSegment);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= Segments.Count) return;
        Segments.Move(index, target);
        SelectedSegment = Segments[target];
        NotifySegmentsChanged();
    }

    private void NotifySegmentsChanged()
    {
        OnPropertyChanged(nameof(EditedDurationText));
        CommandManager.InvalidateRequerySuggested();
    }

    private List<SegmentRange>? BuildSegments()
    {
        if (Media is not { } media)
        {
            StatusText = "先选择文件";
            return null;
        }
        if (Segments.Count == 0)
        {
            StatusText = "至少保留一个片段才能导出 / 预览";
            return null;
        }

        var result = new List<SegmentRange>();
        foreach (var item in Segments)
        {
            var normalized = EditorMath.NormalizeSegment(item.Start, item.End, media.DurationSeconds);
            if (normalized is null)
            {
                StatusText = "有片段超出视频范围：" + item.RangeText;
                return null;
            }
            result.Add(normalized.Value);
        }
        return result;
    }

    // ================================================================== 预览 / 导出

    private async Task GeneratePreviewAsync()
    {
        if (Media is not { } media) return;
        var segments = BuildSegments();
        if (segments is null) return;
        if (!EnsureFFmpeg()) return;

        _cache.EnsureCreated();
        var outputPath = _cache.PathOf($"preview-{DateTime.Now:yyyyMMdd-HHmmss-fff}.mp4");
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        StatusText = "正在生成低清预览（最多 10 秒）…";
        var progress = new Progress<string>(message => StatusText = message);
        try
        {
            var result = await EditorExporter.GeneratePreviewAsync(
                _ffmpegPath!, media.FilePath, outputPath, segments, media, progress, token);
            token.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                TryDelete(outputPath);
                StatusText = result.Error;
                Log("✗ " + result.Error);
                return;
            }

            LastPreviewPath = outputPath;
            StatusText = "预览已生成，正在打开播放器…";
            Log($"✓ 预览：{outputPath}（{new FileInfo(outputPath).Length / 1024} KB）");
            OpenFile(outputPath);
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消生成预览";
            TryDelete(outputPath);
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            StatusText = "生成预览失败：" + ex.Message;
            Log("✗ 生成预览失败：" + ex);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            Progress = 0;
        }
    }

    private async Task ExportAsync()
    {
        if (Media is not { } media) return;
        var segments = BuildSegments();
        if (segments is null) return;
        if (!EnsureFFmpeg()) return;

        var format = EditorOptions.NormalizeFormat(Cfg.Format);
        var extension = MediaEditorArguments.OutputExtension(format);
        var folder = ResolveOutputFolder();
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            StatusText = "无法创建输出文件夹：" + ex.Message;
            return;
        }

        var outputPath = RecordingNames.BuildOutputPath(
            folder,
            string.IsNullOrWhiteSpace(Cfg.FilePrefix) ? MediaEditorConfig.DefaultFilePrefix : Cfg.FilePrefix,
            DateTime.Now,
            extension);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true);
        StatusText = format == "gif" ? "正在导出 GIF（两遍调色板）…" : "正在导出视频…";
        var progress = new Progress<string>(message => StatusText = message);
        try
        {
            EditorExportResult result;
            if (format == "gif")
            {
                var fps = Cfg.Fps <= 0 ? 15 : Cfg.Fps;
                var gif = GifExportSettings.Normalize(fps, Cfg.ScaleWidth, Cfg.GifColors, Cfg.GifDither, Cfg.GifLoop);
                var palettePath = _cache.PathOf($"palette-{Guid.NewGuid():N}.png");
                _cache.EnsureCreated();
                result = await EditorExporter.ExportGifAsync(
                    _ffmpegPath!, media.FilePath, outputPath, palettePath, segments, media, gif, progress, token);
            }
            else
            {
                EnsureCodecCompatible();
                var encoder = VideoEncoderSettings.Normalize(Cfg.VideoCodec, Cfg.RateMode, Cfg.Quality, Cfg.BitrateMbps);
                result = await EditorExporter.ExportVideoAsync(
                    _ffmpegPath!, media.FilePath, outputPath, format, segments, media, encoder,
                    EditorOptions.NormalizeWidth(Cfg.ScaleWidth),
                    EditorOptions.NormalizeFps(Cfg.Fps),
                    Cfg.KeepAudio,
                    Cfg.AudioBitrateKbps,
                    progress,
                    token);
            }

            token.ThrowIfCancellationRequested();
            if (!result.Success)
            {
                TryDelete(outputPath);
                StatusText = result.Error;
                Log("✗ " + result.Error);
                return;
            }

            LastOutputPath = outputPath;
            var size = new FileInfo(outputPath).Length;
            StatusText = $"已导出：{Path.GetFileName(outputPath)}（{FileSize.Format(size)}）";
            Log($"✓ 导出：{outputPath}（{FileSize.Format(size)}）");
            if (Cfg.OpenFolderAfterExport) OpenFolder(folder);
        }
        catch (OperationCanceledException)
        {
            TryDelete(outputPath);
            StatusText = "已取消导出";
        }
        catch (Exception ex)
        {
            TryDelete(outputPath);
            StatusText = "导出失败：" + ex.Message;
            Log("✗ 导出失败：" + ex);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            Progress = 0;
        }
    }

    // ================================================================== 输出 / 文件

    private string ResolveOutputFolder()
    {
        var folder = Cfg.OutputFolder?.Trim();
        if (string.IsNullOrWhiteSpace(folder))
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (string.IsNullOrWhiteSpace(videos)) videos = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            folder = Path.Combine(videos, "YuLiangToolkit 剪辑");
        }
        try { return Path.GetFullPath(folder, AppContext.BaseDirectory); }
        catch { return folder; }
    }

    private void BrowseOutputFolder()
    {
        if (IsBusy) return;
        var dialog = new OpenFolderDialog { Title = "选择剪辑输出文件夹" };
        if (Directory.Exists(ResolveOutputFolder())) dialog.InitialDirectory = ResolveOutputFolder();
        if (dialog.ShowDialog() == true) Cfg.OutputFolder = dialog.FolderName;
    }

    private void OpenOutputFolder() => OpenFolder(ResolveOutputFolder());

    private static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
        }
        catch { /* 打不开不影响主流程 */ }
    }

    private static void OpenFile(string path)
    {
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true }); }
        catch { /* 没有关联播放器时忽略 */ }
    }

    private static void RevealFile(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* 忽略 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 留给清理缓存 */ }
    }

    protected override void OnClosed()
    {
        try { _cts?.Cancel(); } catch { /* 忽略 */ }
        try { _cts?.Dispose(); } catch { /* 忽略 */ }
        _cts = null;
    }
}
