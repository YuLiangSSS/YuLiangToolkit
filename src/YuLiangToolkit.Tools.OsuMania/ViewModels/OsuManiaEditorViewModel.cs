using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.OsuMania.Core.Editor;
using YuLiangToolkit.Tools.OsuMania.Views;

namespace YuLiangToolkit.Tools.OsuMania.ViewModels;

/// <summary>
/// 「osu!mania 谱面编辑器」的 ViewModel：把 **音频时钟**、**编辑器状态机** 和 **界面** 接起来。
///
/// <para><b>音频同步的关键在这里</b>（改之前先读）：</para>
/// <list type="number">
///   <item><b>一个 8ms 的 <see cref="DispatcherTimer"/></b> 每 tick 做三件事：
///         ① <see cref="ChartAudioEngine.Update"/> 用设备位置校准时钟；
///         ② 把时钟位置写进 <see cref="ChartEditor.PlayheadMs"/>；
///         ③ 需要时滚动视口 + 请求重绘。</item>
///   <item><b>播放头位置永远取自引擎的平滑时钟</b>，不是「上一次 tick 的时间 + 帧间隔」——
///         后者会在掉帧时累积误差。</item>
///   <item><b>重绘用 <c>InvalidateVisual</c> + 快照</b>：每 tick 只重建一次
///         <see cref="ChartRenderState"/>，绘制层只画可见的那批 note。</item>
///   <item><b>静音播放时也照样走时钟</b>（没有音频文件时可以「只用网格打谱」）。</item>
/// </list>
///
/// <para><b>编辑操作</b>全部转给 <see cref="ChartEditor"/>（在 Core 里，可无头自检）；
/// 这个类只负责界面状态（按钮可用性、状态文字、时间点列表、选中项）。</para>
/// </summary>
public sealed class OsuManiaEditorViewModel : ToolViewModelBase
{
    /// <summary>界面刷新间隔（毫秒）。8ms ≈ 125Hz，足够让播放头看起来连续，又不至于吃满 UI 线程。</summary>
    private const int TickIntervalMs = 8;

    /// <summary>拖时间轴时，离边缘多近就开始自动滚动（像素）。</summary>
    private const double EdgeScrollPixels = 28;

    private readonly ChartAudioEngine _audio = new();
    private readonly DispatcherTimer _timer;

    private string _currentTimeText = "0:00.000";
    private string _durationText = "0:00.000";
    private string _statusDetail = "打开一个 .osu 谱面开始编辑";
    private string _audioStatus = "未加载音频";
    private double _playheadMs;
    private bool _isPlaying;
    private bool _followPlayhead = true;
    private bool _isDirty;
    private string _titleText = "（没有打开谱面）";
    private string _chartSummary = "";

    private ChartEditor? _editor;

    /// <summary>编辑器状态（没打开谱面时为 null）。</summary>
    public ChartEditor? Editor => _editor;

    /// <summary>当前谱面的音频引擎（只读给 View 用；View 不该自己造）。</summary>
    public ChartAudioEngine Audio => _audio;

    public OsuManiaEditorViewModel() : base(OsuManiaEditorConfig.ToolId)
    {
        Cfg = LoadConfig<OsuManiaEditorConfig>();

        OpenChartCommand = new RelayCommand(_ => OpenChart(), _ => !IsBusy);
        SaveChartCommand = new RelayCommand(_ => SaveChart(), _ => _editor is not null);
        ReloadChartCommand = new RelayCommand(_ => ReloadChart(), _ => _editor is not null);
        LoadAudioCommand = new RelayCommand(_ => ChooseAudio(), _ => _editor is not null);
        PlayPauseCommand = new RelayCommand(_ => TogglePlay());
        StopCommand = new RelayCommand(_ => StopPlayback(), _ => _editor is not null);
        UndoCommand = new RelayCommand(_ => DoUndo(), _ => _editor?.Beatmap.UndoCount > 0);
        RedoCommand = new RelayCommand(_ => DoRedo(), _ => _editor?.Beatmap.RedoCount > 0);
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected(), _ => _editor?.SelectedIndex >= 0);
        AddRedLineCommand = new RelayCommand(_ => AddTimingPoint(isRed: true), _ => _editor is not null);
        AddGreenLineCommand = new RelayCommand(_ => AddTimingPoint(isRed: false), _ => _editor is not null);
        DeleteTimingPointCommand = new RelayCommand(_ => DeleteSelectedTimingPoint(), _ => SelectedTimingPoint is not null);
        ApplyTimingPointCommand = new RelayCommand(_ => ApplyTimingPointValues(), _ => SelectedTimingPoint is not null);
        JumpToStartCommand = new RelayCommand(_ => SeekTo(0), _ => _editor is not null);
        JumpToEndCommand = new RelayCommand(_ => SeekTo(_editor?.Beatmap.TimeRange().Last ?? 0), _ => _editor is not null);
        RevealInExplorerCommand = new RelayCommand(_ => RevealInExplorer(), _ => _editor is not null);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(TickIntervalMs) };
        _timer.Tick += (_, _) => OnTick();

        _audio.PlaybackEnded += (_, _) => Ui(() =>
        {
            IsPlaying = false;
            StatusDetail = "播放完毕";
        });

        ApplyConfigToEditor();
        Log("就绪：点「打开谱面…」或把 .osu 文件拖进页面");

        // 截图 / 冒烟用：YLT_OSU_EDITOR_DEMO=1 时生成一张示例谱面并打开（正常使用不设就不跑）
        if (Environment.GetEnvironmentVariable("YLT_OSU_EDITOR_DEMO") is not null)
        {
            ScheduleDemoOpen();
        }
    }

    /// <summary>把「打开示例谱面」排到 Dispatcher 队列：此时视图还没挂完，立刻打开会量不到高度。</summary>
    private void ScheduleDemoOpen()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // YLT_OSU_EDITOR_DEMO=<.osu 路径> 时直接打开那个谱面（压测 / 截图用）；
                // 只设 =1 时生成合成演示谱面。
                var value = Environment.GetEnvironmentVariable("YLT_OSU_EDITOR_DEMO");
                if (value is { Length: > 1 } && File.Exists(value))
                {
                    LoadChart(value);
                    StatusDetail = "直接打开的谱面（YLT_OSU_EDITOR_DEMO=<路径>）";
                    return;
                }

                var paths = OsuDemoData.EnsureDemoBeatmaps(Storage, includeInvalid: false, longNotes: true);
                var chart = paths.FirstOrDefault(p => p.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
                if (chart is null) return;

                LoadChart(chart);
                StatusDetail = "演示谱面（YLT_OSU_EDITOR_DEMO=1）";
            }
            catch (Exception ex)
            {
                Log("✘ 演示数据生成失败：" + ex.Message);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- 配置

    /// <summary>编辑器偏好（自动保存）。</summary>
    public OsuManiaEditorConfig Cfg { get; }

    // ---------------------------------------------------------------- 界面状态

    /// <summary>谱面标题（艺术家 - 标题 [难度]）。</summary>
    public string TitleText { get { return _titleText; } private set => Set(ref _titleText, value); }

    /// <summary>谱面统计（键数 / note 数 / LN 数 / BPM 范围）。</summary>
    public string ChartSummary { get { return _chartSummary; } private set => Set(ref _chartSummary, value); }

    /// <summary>当前播放位置（m:ss.mmm）。</summary>
    public string CurrentTimeText { get { return _currentTimeText; } private set => Set(ref _currentTimeText, value); }

    /// <summary>音频总时长（m:ss.mmm）。</summary>
    public string DurationText { get { return _durationText; } private set => Set(ref _durationText, value); }

    /// <summary>状态栏的详细文字。</summary>
    public string StatusDetail { get { return _statusDetail; } private set => Set(ref _statusDetail, value); }

    /// <summary>音频状态（文件名 / 未加载 / 失败原因）。</summary>
    public string AudioStatus { get { return _audioStatus; } private set => Set(ref _audioStatus, value); }

    /// <summary>播放中。</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (Set(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayPauseGlyph));
        }
    }

    /// <summary>播放 / 暂停按钮的字形。</summary>
    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

    /// <summary>播放头位置（毫秒）。</summary>
    public double PlayheadMs
    {
        get => _playheadMs;
        private set => Set(ref _playheadMs, value);
    }

    /// <summary>播放时是否跟随滚动。</summary>
    public bool FollowPlayhead
    {
        get => _followPlayhead;
        set
        {
            if (!Set(ref _followPlayhead, value)) return;
            Cfg.FollowPlayhead = value;
        }
    }

    /// <summary>是否有未保存改动。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value)) OnPropertyChanged(nameof(SaveButtonHint));
        }
    }

    /// <summary>保存按钮的提示文字。</summary>
    public string SaveButtonHint => IsDirty ? "保存 *" : "保存";

    /// <summary>节拍细分（每拍几格）。</summary>
    public int GridDivisor
    {
        get => Cfg.GridDivisor;
        set
        {
            var clamped = Math.Clamp(value, 1, 48);
            if (Cfg.GridDivisor == clamped) return;
            Cfg.GridDivisor = clamped;
            if (_editor is not null) _editor.GridDivisor = clamped;
            OnPropertyChanged();
            RequestRedraw();
        }
    }

    /// <summary>是否吸附网格。</summary>
    public bool SnapToGrid
    {
        get => Cfg.SnapToGrid;
        set
        {
            if (Cfg.SnapToGrid == value) return;
            Cfg.SnapToGrid = value;
            if (_editor is not null) _editor.SnapToGrid = value;
            OnPropertyChanged();
        }
    }

    /// <summary>左键工具：true = 长条。</summary>
    public bool LongNoteTool
    {
        get => Cfg.LongNoteTool;
        set
        {
            if (Cfg.LongNoteTool == value) return;
            Cfg.LongNoteTool = value;
            if (_editor is not null)
            {
                _editor.Tool = value ? ChartEditor.EditTool.LongNote : ChartEditor.EditTool.Note;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>时间点列表（界面上那一列）。</summary>
    public ObservableCollection<TimingPointRow> TimingPoints { get; } = new();

    private TimingPointRow? _selectedTimingPoint;

    /// <summary>选中的时间点行。</summary>
    public TimingPointRow? SelectedTimingPoint
    {
        get => _selectedTimingPoint;
        set
        {
            if (!Set(ref _selectedTimingPoint, value)) return;
            OnPropertyChanged(nameof(CanEditTimingPoint));
            OnPropertyChanged(nameof(TimingBpmText));
            OnPropertyChanged(nameof(TimingSvText));
            OnPropertyChanged(nameof(TimingTimeText));
            OnPropertyChanged(nameof(IsSelectedTimingRed));
        }
    }

    /// <summary>有没有选中时间点（编辑框可用性）。</summary>
    public bool CanEditTimingPoint => _selectedTimingPoint is not null;

    /// <summary>选中时间点的 BPM（编辑框绑定）。</summary>
    public string TimingBpmText
    {
        get => _selectedTimingPoint is { } row && row.IsRed ? row.Bpm.ToString("0.###") : "";
        set => SetTimingValue(text: value, isBpm: true);
    }

    /// <summary>选中时间点的 SV（编辑框绑定）。</summary>
    public string TimingSvText
    {
        get => _selectedTimingPoint is { } row && !row.IsRed ? row.Sv.ToString("0.##") : "";
        set => SetTimingValue(text: value, isBpm: false);
    }

    /// <summary>选中时间点的时间（编辑框绑定）。</summary>
    public string TimingTimeText
    {
        get => _selectedTimingPoint?.TimeText ?? "";
        set => SetTimingTime(text: value);
    }

    /// <summary>选中的是不是红线（界面据此切换 BPM / SV 输入框）。</summary>
    public bool IsSelectedTimingRed => _selectedTimingPoint?.IsRed ?? false;

    /// <summary>节拍细分可选值（下拉框绑定）。</summary>
    public IReadOnlyList<int> DivisorOptions { get; } = OsuManiaEditorConfig.DivisorOptions;

    /// <summary>视口缩放（每毫秒像素）。</summary>
    public double PxPerMs
    {
        get => Cfg.PxPerMs;
        set
        {
            var clamped = Math.Clamp(value, ChartViewport.MinPxPerMs, ChartViewport.MaxPxPerMs);
            if (Math.Abs(Cfg.PxPerMs - clamped) < 1e-9) return;
            Cfg.PxPerMs = clamped;
            _editor?.Viewport.SetScale(clamped);
            OnPropertyChanged();
            RequestRedraw();
        }
    }

    /// <summary>
    /// 画布滚轮缩放后，把编辑器里的实际比例同步回配置 / 界面（避免两边各记一份）。
    /// </summary>
    public void SyncZoomFromEditor()
    {
        if (_editor is null) return;
        Cfg.PxPerMs = _editor.Viewport.PxPerMs;
        OnPropertyChanged(nameof(PxPerMs));
    }

    /// <summary>
    /// 音频偏移（毫秒）。**正数 = 播放头往后推**（音符看着比音乐早、就填正数把画面压后）。
    ///
    /// <para>每条声卡链路的缓冲延迟都不一样（本机实测 GetPosition 落后真实时间约 64ms），
    /// 用户耳朵最准：觉得音符整体和音乐错开，就用这个对齐。</para>
    /// </summary>
    public double AudioOffsetMs
    {
        get => Cfg.AudioOffsetMs;
        set
        {
            var clamped = Math.Clamp(value, -500, 500);
            if (Math.Abs(Cfg.AudioOffsetMs - clamped) < 1e-9) return;
            Cfg.AudioOffsetMs = clamped;
            _audio.UserOffsetMs = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>渲染快照（画布绑定它）。</summary>
    public ChartRenderState? RenderState { get; private set; }

    // ---------------------------------------------------------------- 命令

    public RelayCommand OpenChartCommand { get; }
    public RelayCommand SaveChartCommand { get; }
    public RelayCommand ReloadChartCommand { get; }
    public RelayCommand LoadAudioCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand DeleteSelectedCommand { get; }
    public RelayCommand AddRedLineCommand { get; }
    public RelayCommand AddGreenLineCommand { get; }
    public RelayCommand DeleteTimingPointCommand { get; }
    public RelayCommand ApplyTimingPointCommand { get; }
    public RelayCommand JumpToStartCommand { get; }
    public RelayCommand JumpToEndCommand { get; }
    public RelayCommand RevealInExplorerCommand { get; }

    /// <summary>请求重绘画布（View 订阅，转成 <c>InvalidateVisual</c>）。</summary>
    public event EventHandler? RedrawRequested;

    /// <summary>请求把某个时间滚进视野（跳转用）。</summary>
    public event EventHandler<double>? ScrollToTimeRequested;

    // ---------------------------------------------------------------- 打开 / 保存

    /// <summary>打开一个 .osu 谱面（也支持从拖放调用）。</summary>
    public void OpenChart(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var dialog = new OpenFileDialog
            {
                Title = "打开 osu!mania 谱面（.osu）",
                Filter = "osu! 谱面 (*.osu)|*.osu|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };

            if (!string.IsNullOrEmpty(Cfg.LastFolder) && Directory.Exists(Cfg.LastFolder))
            {
                dialog.InitialDirectory = Cfg.LastFolder;
            }

            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }

        LoadChart(path);
    }

    /// <summary>加载谱面（失败写状态栏，不抛异常）。</summary>
    public void LoadChart(string path)
    {
        try
        {
            var beatmap = OsuFileDocument.Load(path);

            if (beatmap.ModeId != 3)
            {
                StatusDetail = $"这不是 osu!mania 谱面（Mode={beatmap.ModeId}，需要 3）";
                Log($"✘ {Path.GetFileName(path)}：Mode={beatmap.ModeId}，不是 mania 谱面");
                return;
            }

            _editor = new ChartEditor(beatmap)
            {
                GridDivisor = Cfg.GridDivisor,
                SnapToGrid = Cfg.SnapToGrid,
                Tool = Cfg.LongNoteTool ? ChartEditor.EditTool.LongNote : ChartEditor.EditTool.Note,
            };
            _editor.Changed += (_, _) => OnEditorChanged();

            beatmap.RefreshIndexes();

            Cfg.LastFolder = Path.GetDirectoryName(path) ?? "";
            TitleText = BuildTitle(beatmap);
            IsDirty = false;
            SelectedTimingPoint = null;

            _audio.Volume = Cfg.Volume;
            LoadChartAudio(beatmap, path);

            // 打开时的视野：用上次记住的缩放，从谱面第一个 note 开始看。
            // 不直接 FitRange 整张谱面 —— 十几万 note 的谱面一适配就缩成一条线，什么都看不清。
            var (firstTime, _) = beatmap.TimeRange();
            _editor.Viewport.SetScale(Math.Clamp(Cfg.PxPerMs, ChartViewport.MinPxPerMs, ChartViewport.MaxPxPerMs));
            _editor.Viewport.ScrollTo(firstTime);

            SeekTo(0);
            RefreshTimingPoints();
            RefreshSummary();
            BuildAndPublishState();

            StatusDetail = $"已打开 {Path.GetFileName(path)}（{beatmap.HitObjects.Count:N0} note）";
            Log($"打开 {path}");
            if (beatmap.LoadWarnings.Count > 0)
            {
                Log($"⚠ 解析时有 {beatmap.LoadWarnings.Count} 条警告（已跳过坏行）");
                foreach (var warning in beatmap.LoadWarnings.Take(5)) Log("  · " + warning);
            }

            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            StatusDetail = $"打开失败：{ex.Message}";
            Log($"✘ 打开失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>按 .osu 的 <c>AudioFilename</c> 找音频（同目录），找不到就提示手选。</summary>
    private void LoadChartAudio(EditorBeatmap beatmap, string chartPath)
    {
        var folder = Path.GetDirectoryName(chartPath) ?? "";
        var name = beatmap.General.TryGetValue("AudioFilename", out var value) ? value : "";

        if (string.IsNullOrWhiteSpace(name))
        {
            AudioStatus = "谱面没写 AudioFilename";
            return;
        }

        var audioPath = Path.Combine(folder, name);
        if (!File.Exists(audioPath))
        {
            AudioStatus = $"找不到音频：{name}（可以手动选）";
            Log($"⚠ 找不到音频 {audioPath}");
            return;
        }

        if (_audio.Load(audioPath))
        {
            AudioStatus = name;
            DurationText = FormatTime(_audio.DurationMs);
            Log($"音频：{name}（{FormatTime(_audio.DurationMs)}）");
        }
        else
        {
            AudioStatus = _audio.LastError;
            Log($"✘ 音频加载失败：{_audio.LastError}");
        }
    }

    private void ChooseAudio()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择这张谱面的音频",
            Filter = ChartAudioEngine.OpenFileFilter,
            CheckFileExists = true,
        };

        if (_editor is not null)
        {
            var folder = Path.GetDirectoryName(_editor.Beatmap.FilePath) ?? "";
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog() != true) return;

        if (_audio.Load(dialog.FileName))
        {
            AudioStatus = Path.GetFileName(dialog.FileName);
            DurationText = FormatTime(_audio.DurationMs);
            StatusDetail = "音频已加载";
            Log($"音频：{dialog.FileName}");
        }
        else
        {
            AudioStatus = _audio.LastError;
            StatusDetail = "音频加载失败：" + _audio.LastError;
            Log($"✘ 音频加载失败：{_audio.LastError}");
        }
    }

    /// <summary>保存（覆盖原谱面）。</summary>
    public void SaveChart()
    {
        if (_editor is null) return;

        try
        {
            OsuFileDocument.Save(_editor.Beatmap, _editor.Beatmap.FilePath);
            _editor.Beatmap.IsDirty = false;
            IsDirty = false;
            StatusDetail = $"已保存 {Path.GetFileName(_editor.Beatmap.FilePath)}";
            Log($"保存 {_editor.Beatmap.FilePath}（{_editor.Beatmap.HitObjects.Count:N0} note）");
        }
        catch (Exception ex)
        {
            StatusDetail = $"保存失败：{ex.Message}";
            Log($"✘ 保存失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>放弃改动重新读盘。</summary>
    private void ReloadChart()
    {
        var path = _editor?.Beatmap.FilePath;
        if (string.IsNullOrEmpty(path)) return;

        if (IsDirty &&
            System.Windows.MessageBox.Show(
                "有未保存的改动，重新加载会丢弃它们。继续？", "重新加载",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning)
            != System.Windows.MessageBoxResult.OK)
        {
            return;
        }

        LoadChart(path);
    }

    private void RevealInExplorer()
    {
        var path = _editor?.Beatmap.FilePath;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusDetail = "打开资源管理器失败：" + ex.Message;
        }
    }

    // ---------------------------------------------------------------- 播放

    /// <summary>切换播放 / 暂停。</summary>
    public void TogglePlay()
    {
        if (_editor is null) return;

        if (IsPlaying)
        {
            _audio.Pause();
            IsPlaying = false;
            _timer.Stop();
            StatusDetail = "已暂停";
            return;
        }

        if (!_audio.IsLoaded)
        {
            StatusDetail = "没有加载音频（可以「选择音频…」，或者直接对着网格打谱）";
            Log("没有音频：先「选择音频…」");
            return;
        }

        if (_audio.Play())
        {
            IsPlaying = true;
            _timer.Start();
            StatusDetail = "播放中";
        }
        else
        {
            StatusDetail = _audio.LastError;
            Log("✘ " + _audio.LastError);
        }
    }

    /// <summary>停止（回到 0）。</summary>
    public void StopPlayback()
    {
        _audio.Stop();
        IsPlaying = false;
        _timer.Stop();
        SeekTo(0);
        StatusDetail = "已停止";
    }

    /// <summary>跳到指定毫秒（播放中会继续播）。</summary>
    public void SeekTo(double ms)
    {
        if (_editor is null) return;

        var duration = _audio.DurationMs > 0 ? _audio.DurationMs : Math.Max(0, _editor.Beatmap.TimeRange().Last);
        var target = Math.Clamp(ms, 0, Math.Max(0, duration));

        _audio.Seek(target);
        _editor.PlayheadMs = target;
        PlayheadMs = target;
        CurrentTimeText = FormatTime(target);

        if (IsPlaying) _timer.Start();

        RequestRedraw();
    }

    /// <summary>播放头前进 / 后退（界面的按钮和快捷键）。</summary>
    public void Nudge(double deltaMs) => SeekTo(PlayheadMs + deltaMs);

    // ---------------------------------------------------------------- 每帧 tick

    private void OnTick()
    {
        if (_editor is null) return;

        _audio.Update();

        var position = _audio.PositionMs;
        if (position < 0) position = 0;

        _editor.PlayheadMs = position;
        PlayheadMs = position;
        CurrentTimeText = FormatTime(position);

        if (FollowPlayhead && !_editor.IsDragging)
        {
            _editor.Viewport.FollowPlayhead(position);
        }

        BuildAndPublishState();
    }

    // ---------------------------------------------------------------- 编辑

    /// <summary>撤销。</summary>
    public void DoUndo()
    {
        if (_editor?.Undo() == true) RefreshAfterEdit("已撤销");
    }

    /// <summary>重做。</summary>
    public void DoRedo()
    {
        if (_editor?.Redo() == true) RefreshAfterEdit("已重做");
    }

    /// <summary>删除选中的 note。</summary>
    private void DeleteSelected()
    {
        if (_editor?.DeleteSelected() == true) RefreshAfterEdit("已删除选中的 note");
    }

    /// <summary>在当前播放头处加一条红线（BPM 沿用当前生效的）。</summary>
    private void AddTimingPoint(bool isRed)
    {
        if (_editor is null) return;

        var time = Math.Round(PlayheadMs);
        var bpm = _editor.Beatmap.BpmAt(time);
        if (bpm <= 0) bpm = 180;

        var point = isRed
            ? EditorTimingPoint.NewRedLine(time, bpm)
            : EditorTimingPoint.NewGreenLine(time, 1.0);

        _editor.Beatmap.AddTimingPoint(point);
        RefreshTimingPoints();
        RefreshAfterEdit(isRed ? $"已加红线（{bpm:0.##} BPM）" : "已加绿线（SV 1.0）");
    }

    private void DeleteSelectedTimingPoint()
    {
        if (_editor is null || SelectedTimingPoint is null) return;

        var index = SelectedTimingPoint.Index;
        if (!_editor.Beatmap.RemoveTimingPointAt(index)) return;

        SelectedTimingPoint = null;
        RefreshTimingPoints();
        RefreshAfterEdit("已删除时间点");
    }

    /// <summary>把编辑框里的值写回选中的时间点。</summary>
    private void ApplyTimingPointValues()
    {
        if (_editor is null || SelectedTimingPoint is null) return;

        var index = SelectedTimingPoint.Index;
        if (index < 0 || index >= _editor.Beatmap.TimingPoints.Count) return;

        var point = _editor.Beatmap.TimingPoints[index];

        if (double.TryParse(TimingBpmText, out var bpm) && bpm > 0 && point.Uninherited)
        {
            point = point.WithBpm(bpm);
        }

        if (double.TryParse(TimingSvText, out var sv) && sv > 0 && !point.Uninherited)
        {
            point = point.WithSv(sv);
        }

        if (double.TryParse(TimingTimeText, out var time) && time >= 0)
        {
            point = point.WithTime(Math.Round(time));
        }

        if (_editor.Beatmap.ReplaceTimingPoint(index, point))
        {
            RefreshTimingPoints();
            RefreshAfterEdit("时间点已更新");
        }
    }

    private void SetTimingValue(string? text, bool isBpm)
    {
        // 只有「离开输入框 / 回车」时才写回（UpdateSourceTrigger=LostFocus），避免打字过程中乱改
        if (_editor is null || SelectedTimingPoint is null) return;
        if (!double.TryParse(text, out var value) || value <= 0) return;

        var index = SelectedTimingPoint.Index;
        if (index < 0 || index >= _editor.Beatmap.TimingPoints.Count) return;

        var point = _editor.Beatmap.TimingPoints[index];
        var updated = isBpm ? point.WithBpm(value) : point.WithSv(value);

        if (_editor.Beatmap.ReplaceTimingPoint(index, updated))
        {
            RefreshTimingPoints();
            RefreshAfterEdit("时间点已更新");
        }
    }

    private void SetTimingTime(string? text)
    {
        if (_editor is null || SelectedTimingPoint is null) return;
        if (!double.TryParse(text, out var value) || value < 0) return;

        var index = SelectedTimingPoint.Index;
        if (index < 0 || index >= _editor.Beatmap.TimingPoints.Count) return;

        var updated = _editor.Beatmap.TimingPoints[index].WithTime(Math.Round(value));
        if (_editor.Beatmap.ReplaceTimingPoint(index, updated))
        {
            RefreshTimingPoints();
            RefreshAfterEdit("时间点已更新");
        }
    }

    /// <summary>编辑器内部发生变化（摆放 / 拖动 / 删除）。</summary>
    private void OnEditorChanged()
    {
        IsDirty = _editor?.Beatmap.IsDirty ?? false;
        RefreshSummary();
        RequestRedraw();
        CommandManager.InvalidateRequerySuggested();
    }

    private void RefreshAfterEdit(string message)
    {
        IsDirty = _editor?.Beatmap.IsDirty ?? false;
        RefreshSummary();
        BuildAndPublishState();
        StatusDetail = message;
        CommandManager.InvalidateRequerySuggested();
    }

    // ---------------------------------------------------------------- 供 View 调用

    /// <summary>
    /// 构建并发布渲染快照。**每帧只调一次**，避免重复做二分与抽稀。
    /// </summary>
    /// <param name="contentHeight">内容区高度（画布实际高度）。</param>
    public void BuildAndPublishState(double contentHeight = 0)
    {
        if (_editor is null)
        {
            RenderState = null;
            RequestRedraw();
            return;
        }

        var height = contentHeight > 0 ? contentHeight : _editor.Viewport.Height;
        _editor.Viewport.Resize(height);
        _editor.SetLayout(ChartCanvasLaneLeft, ChartCanvasLaneWidth, _editor.Beatmap.KeyCount);

        RenderState = ChartRenderState.Build(_editor, height, ChartCanvasLaneLeft, ChartCanvasLaneWidth);
        RequestRedraw();
    }

    /// <summary>轨道区左边距（与 <c>ChartCanvas.RulerWidth</c> 一致）。</summary>
    public const double ChartCanvasLaneLeft = 64;

    /// <summary>轨道区宽度（由 View 在尺寸变化时告诉 VM）。</summary>
    public double ChartCanvasLaneWidth { get; set; } = 200;

    private void RequestRedraw() => RedrawRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>把时间滚进视野（View 转成视口滚动）。</summary>
    public void RequestScrollToTime(double timeMs) => ScrollToTimeRequested?.Invoke(this, timeMs);

    // ---------------------------------------------------------------- 刷新辅助

    private void RefreshSummary()
    {
        if (_editor is null)
        {
            ChartSummary = "";
            return;
        }

        var beatmap = _editor.Beatmap;
        var longNotes = 0;
        foreach (var note in beatmap.HitObjects)
        {
            if (note.IsLongNote) longNotes++;
        }

        var bpmText = "";
        var reds = beatmap.RedLines;
        if (reds.Count > 0)
        {
            var min = double.MaxValue;
            var max = 0.0;
            foreach (var red in reds)
            {
                if (red.Bpm <= 0) continue;
                min = Math.Min(min, red.Bpm);
                max = Math.Max(max, red.Bpm);
            }
            if (max > 0)
            {
                bpmText = Math.Abs(max - min) < 0.01 ? $" · {max:0.##} BPM" : $" · {min:0.##}~{max:0.##} BPM";
            }
        }

        ChartSummary = $"{beatmap.KeyCount}K · {beatmap.HitObjects.Count:N0} note（LN {longNotes:N0}）{bpmText}";
    }

    private void RefreshTimingPoints()
    {
        TimingPoints.Clear();
        if (_editor is null) return;

        var beatmap = _editor.Beatmap;
        var indexed = beatmap.TimingPoints
            .Select((point, index) => new TimingPointRow(index, point))
            .OrderBy(row => row.Point.Time);

        foreach (var row in indexed) TimingPoints.Add(row);

        if (SelectedTimingPoint is not null)
        {
            SelectedTimingPoint = TimingPoints.FirstOrDefault(r => r.Index == SelectedTimingPoint.Index);
        }
    }

    private static string BuildTitle(EditorBeatmap beatmap)
    {
        static string Get(Dictionary<string, string> map, string key)
            => map.TryGetValue(key, out var value) ? value : "";

        var artist = Get(beatmap.Metadata, "Artist");
        var title = Get(beatmap.Metadata, "Title");
        var version = Get(beatmap.Metadata, "Version");

        var head = string.Join(" - ", new[] { artist, title }.Where(s => s.Length > 0));
        if (head.Length == 0) head = Path.GetFileNameWithoutExtension(beatmap.FilePath);
        return version.Length > 0 ? $"{head} [{version}]" : head;
    }

    /// <summary>毫秒 → <c>m:ss.mmm</c>。</summary>
    public static string FormatTime(double ms)
    {
        if (!double.IsFinite(ms) || ms < 0) ms = 0;
        var span = TimeSpan.FromMilliseconds(ms);
        return $"{(int)span.TotalMinutes}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    private void ApplyConfigToEditor()
    {
        _audio.Volume = Cfg.Volume;
        _audio.UserOffsetMs = Cfg.AudioOffsetMs;
        _followPlayhead = Cfg.FollowPlayhead;
        OnPropertyChanged(nameof(FollowPlayhead));
        OnPropertyChanged(nameof(GridDivisor));
        OnPropertyChanged(nameof(SnapToGrid));
        OnPropertyChanged(nameof(LongNoteTool));
        OnPropertyChanged(nameof(PxPerMs));
        OnPropertyChanged(nameof(AudioOffsetMs));
    }

    /// <summary>保存音频音量（滑块变化时调）。</summary>
    public void SetVolume(double volume)
    {
        Cfg.Volume = Math.Clamp(volume, 0, 1);
        _audio.Volume = Cfg.Volume;
    }

    // ---------------------------------------------------------------- 生命周期

    protected override void OnClosed()
    {
        _timer.Stop();
        _audio.Dispose();
    }
}

/// <summary>时间点列表的一行（界面绑定用）。</summary>
public sealed class TimingPointRow
{
    public TimingPointRow(int index, EditorTimingPoint point)
    {
        Index = index;
        Point = point;
    }

    /// <summary>在 <see cref="EditorBeatmap.TimingPoints"/> 里的下标。</summary>
    public int Index { get; }

    /// <summary>时间点本身。</summary>
    public EditorTimingPoint Point { get; }

    /// <summary>是不是红线。</summary>
    public bool IsRed => Point.Uninherited;

    /// <summary>BPM（红线；绿线为 0）。</summary>
    public double Bpm => Point.Bpm;

    /// <summary>SV 倍率（绿线；红线为 1）。</summary>
    public double Sv => Point.SvMultiplier;

    /// <summary>列表里显示的主文字。</summary>
    public string Text => IsRed
        ? $"🔴 {Point.Time:0}ms · {Bpm:0.##} BPM"
        : $"🟢 {Point.Time:0}ms · SV {Sv:0.##}x";

    /// <summary>时间文字（编辑框用）。</summary>
    public string TimeText => Point.Time.ToString("0");
}
