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
public sealed class OsuManiaEditorViewModel : ToolViewModelBase, YuLiangToolkit.Core.Tools.ICloseConfirmer
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
        DeleteSelectedCommand = new RelayCommand(_ => DeleteSelected(), _ => (_editor?.SelectionCount ?? 0) > 0);
        SelectAllCommand = new RelayCommand(_ => SelectAllNotes(), _ => _editor?.Beatmap.HitObjects.Count > 0);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => (_editor?.SelectionCount ?? 0) > 0);
        JumpToFirstNoteCommand = new RelayCommand(_ => JumpToFirstNote(true), _ => _editor is not null);
        JumpToLastNoteCommand = new RelayCommand(_ => JumpToLastNote(true), _ => _editor is not null);
        ToggleFullscreenCommand = new RelayCommand(_ => ToggleFullscreen());
        ChooseBackgroundCommand = new RelayCommand(_ => ChooseBackground(), _ => _editor is not null);
        ClearBackgroundCommand = new RelayCommand(_ => ClearBackgroundImage(), _ => HasBackgroundImage);
        ResetLaneBackgroundCommand = new RelayCommand(_ => ResetLaneBackgroundColor());
        SetToolCommand = new RelayCommand(param =>
        {
            if (param is ChartEditor.EditTool tool) Tool = tool;
            else if (param is string text && Enum.TryParse<ChartEditor.EditTool>(text, out var parsed)) Tool = parsed;
        });
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

            // YLT_OSU_EDITOR_AUTOSAVE_PROBE=N：再改动一次谱面（加 N 个 note），
            // 用来验证「自动保存真的会把改动写盘」。正常使用不设就不跑。
            TryRunAutoSaveProbe();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 调试探针：<c>YLT_OSU_EDITOR_AUTOSAVE_PROBE=&lt;note 数&gt;</c> 时加几个 note 再放着不动，
    /// 让自动保存在空闲后把它们写盘（脚本 / 人工都能据此核对文件真的变了）。
    /// </summary>
    private void TryRunAutoSaveProbe()
    {
        var raw = Environment.GetEnvironmentVariable("YLT_OSU_EDITOR_AUTOSAVE_PROBE");
        if (string.IsNullOrWhiteSpace(raw)) return;
        if (_editor is null) return;

        if (!int.TryParse(raw, out var count) || count <= 0) count = 3;
        count = Math.Min(count, 50);

        var start = _editor.Beatmap.TimeRange().First + 500;
        for (var i = 0; i < count; i++)
        {
            var column = i % Math.Max(1, _editor.Beatmap.KeyCount);
            _editor.Beatmap.AddNote(EditorNote.NewNote(column, start + i * 250));
        }

        _editor.Beatmap.RefreshIndexes();
        RefreshSummary();
        BuildAndPublishState();
        IsDirty = true;
        StatusDetail = $"探针：已加 {count} 个 note，等自动保存（{AutoSaveIdleSeconds} 秒无操作）";
        Log($"探针：加了 {count} 个 note，等待自动保存");
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

    /// <summary>
    /// 当前编辑工具（模拟 osu! 编辑器左侧 TOOLS：选择 / 单点 / 长条）。
    /// </summary>
    public ChartEditor.EditTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value) return;
            _tool = value;
            if (_editor is not null) _editor.Tool = value;
            Cfg.Tool = (int)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSelectTool));
            OnPropertyChanged(nameof(IsNoteTool));
            OnPropertyChanged(nameof(IsLongNoteTool));
        }
    }

    private ChartEditor.EditTool _tool = ChartEditor.EditTool.Note;

    /// <summary>是不是选择工具（界面里单选按钮绑定它）。</summary>
    public bool IsSelectTool
    {
        get => Tool == ChartEditor.EditTool.Select;
        set { if (value) Tool = ChartEditor.EditTool.Select; }
    }

    /// <summary>是不是单点（普通键）工具。</summary>
    public bool IsNoteTool
    {
        get => Tool == ChartEditor.EditTool.Note;
        set { if (value) Tool = ChartEditor.EditTool.Note; }
    }

    /// <summary>是不是长条工具。</summary>
    public bool IsLongNoteTool
    {
        get => Tool == ChartEditor.EditTool.LongNote;
        set { if (value) Tool = ChartEditor.EditTool.LongNote; }
    }

    /// <summary>兼容旧绑定的「长条开关」（LongNoteTool = 切到长条 / 切回单点）。</summary>
    public bool LongNoteTool
    {
        get => Tool == ChartEditor.EditTool.LongNote;
        set
        {
            var target = value ? ChartEditor.EditTool.LongNote : ChartEditor.EditTool.Note;
            if (Tool == target) return;
            Tool = target;
        }
    }

    /// <summary>选中了几个 note。</summary>
    public int SelectionCount => _editor?.SelectionCount ?? 0;

    /// <summary>选择状态的文字（状态栏 / 检查器）。</summary>
    public string SelectionText => SelectionCount switch
    {
        0 => "未选择",
        1 => "已选 1 个 note",
        _ => $"已选 {SelectionCount} 个 note",
    };

    /// <summary>缩放文字（全屏左栏显示）。</summary>
    public string ZoomText => _editor is null ? "—" : $"{_editor.Viewport.ZoomRatio:0.##}×";

    /// <summary>当前播放头处的 BPM（全屏底部显示）。</summary>
    public string BpmText
    {
        get
        {
            if (_editor is null) return "";
            var bpm = _editor.Beatmap.BpmAt(PlayheadMs);
            return bpm > 0 ? $"{bpm:0.##} BPM" : "";
        }
    }

    /// <summary>右侧检查器标题（选中内容摘要）。</summary>
    public string InspectorTitle
    {
        get
        {
            if (_editor is null) return "没有打开谱面";
            if (SelectionCount == 0) return "No selection";

            if (SelectionCount > 1)
            {
                return $"{SelectionCount} notes";
            }

            var note = _editor.SelectedNote;
            var kind = note.IsLongNote ? "Long note" : "Note";
            var length = note.IsLongNote ? $" · {note.Duration:0}ms" : "";
            return $"{kind} · 列 {note.Column + 1}{length}";
        }
    }

    /// <summary>右侧检查器详情（时间 / 长度）。</summary>
    public string InspectorDetail
    {
        get
        {
            if (_editor is null) return "打开一个 .osu 谱面开始编辑";
            if (SelectionCount == 0) return "左键点 note 选中；选择工具可拖动框选，Ctrl 可多选";

            if (SelectionCount > 1)
            {
                var first = double.MaxValue;
                var last = double.MinValue;
                var longNotes = 0;
                foreach (var index in _editor.Selection.Indices)
                {
                    if (index < 0 || index >= _editor.Beatmap.HitObjects.Count) continue;
                    var item = _editor.Beatmap.HitObjects[index];
                    if (item.StartTime < first) first = item.StartTime;
                    if (item.StartTime > last) last = item.StartTime;
                    if (item.IsLongNote) longNotes++;
                }

                if (first > last) return "";
                return $"{FormatTime(first)} → {FormatTime(last)}\nLN {longNotes} 个";
            }

            var note = _editor.SelectedNote;
            var text = $"开始 {FormatTime(note.StartTime)}\n结束 {FormatTime(note.EndTime)}";
            return note.IsLongNote ? text + $"\n长度 {note.Duration:0}ms" : text;
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
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public RelayCommand JumpToFirstNoteCommand { get; }
    public RelayCommand JumpToLastNoteCommand { get; }
    public RelayCommand ToggleFullscreenCommand { get; }
    public RelayCommand ChooseBackgroundCommand { get; }
    public RelayCommand ClearBackgroundCommand { get; }
    public RelayCommand ResetLaneBackgroundCommand { get; }
    public RelayCommand SetToolCommand { get; }
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
                Tool = _tool,
            };
            _editor.Changed += (_, _) => OnEditorChanged();
            _editor.Selection.Changed += (_, _) => RaiseSelectionChanged();

            beatmap.RefreshIndexes();

            Cfg.LastFolder = Path.GetDirectoryName(path) ?? "";
            TitleText = BuildTitle(beatmap);
            IsDirty = false;
            SelectedTimingPoint = null;

            _audio.Volume = Cfg.Volume;
            LoadChartAudio(beatmap, path);
            LoadBackgroundForChart(beatmap);

            // 打开时的视野：用上次记住的缩放，从谱面第一个 note 开始看。
            // 不直接 FitRange 整张谱面 —— 十几万 note 的谱面一适配就缩成一条线，什么都看不清。
            var (firstTime, _) = beatmap.TimeRange();
            _editor.Viewport.SetScale(Math.Clamp(Cfg.PxPerMs, ChartViewport.MinPxPerMs, ChartViewport.MaxPxPerMs));
            _editor.Viewport.ScrollTo(firstTime);

            SeekTo(0);
            RefreshTimingPoints();
            RefreshSummary();
            BuildAndPublishState();

            // 主动推一次选择 / 检查器状态：打开谱面时选择是空的，Selection.Changed 不会触发，
            // 而全屏覆盖层的绑定可能是刚创建的（首次进入全屏）—— 不推就停在初始文字上（截图里踩过）。
            RaiseSelectionChanged();
            OnPropertyChanged(nameof(BpmText));
            OnPropertyChanged(nameof(ZoomText));

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

    /// <summary>保存（覆盖原谱面；界面上的「保存」按钮走它）。</summary>
    public void SaveChart() => SaveChart(silent: false, out _);

    /// <summary>
    /// 保存到原文件。
    /// </summary>
    /// <param name="silent">true = 自动保存：成功时不写日志（只在状态栏更新），失败才记日志。</param>
    /// <param name="error">失败原因（成功为 null）。</param>
    /// <returns>是否保存成功。</returns>
    public bool SaveChart(bool silent, out string? error)
    {
        error = null;
        if (_editor is null)
        {
            error = "没有打开谱面。";
            return false;
        }

        try
        {
            var path = _editor.Beatmap.FilePath;
            OsuFileDocument.Save(_editor.Beatmap, path);
            _editor.Beatmap.IsDirty = false;
            IsDirty = false;
            StatusDetail = silent
                ? $"已自动保存（无操作 {AutoSaveIdleSeconds} 秒）"
                : $"已保存 {Path.GetFileName(path)}";

            if (!silent) Log($"保存 {path}（{_editor.Beatmap.HitObjects.Count:N0} note）");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            StatusDetail = $"保存失败：{ex.Message}";
            Log($"✘ 保存失败（{ex.GetType().Name}）：{ex.Message}");
            return false;
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

    // ---------------------------------------------------------------- 自动保存 / 退出提醒

    /// <summary>自动保存的检查间隔（毫秒）。空闲计时靠它累加，250ms 精度足够。</summary>
    private const int AutoSaveCheckIntervalMs = 250;

    private DispatcherTimer? _autoSaveTimer;
    private int _autoSaveCount;

    /// <summary>空闲计时（纯逻辑，自检可直接断言；见 <see cref="IdleAutoSaveTimer"/>）。</summary>
    private readonly IdleAutoSaveTimer _idleTimer = new();

    /// <summary>
    /// 标记「刚发生过一次交互」。**所有**用户操作都要调它：摆放 / 拖动 / 删除 note、撤销重做、
    /// 滚时间轴、缩放、移动鼠标、按键……否则自动保存会在用户还在操作时把谱面写盘。
    ///
    /// <para>实现上是把空闲计时清零（不依赖事件频率），开销只有一个字段赋值。</para>
    /// </summary>
    public void NoteActivity() => _idleTimer.NoteActivity();

    /// <summary>自动保存间隔（秒）。界面绑定，夹在合法范围内。</summary>
    public int AutoSaveIdleSeconds
    {
        get => Math.Clamp(Cfg.AutoSaveIdleSeconds, OsuManiaEditorConfig.MinAutoSaveSeconds,
            OsuManiaEditorConfig.MaxAutoSaveSeconds);
        set
        {
            var clamped = Math.Clamp(value, OsuManiaEditorConfig.MinAutoSaveSeconds,
                OsuManiaEditorConfig.MaxAutoSaveSeconds);
            if (Cfg.AutoSaveIdleSeconds == clamped) return;
            Cfg.AutoSaveIdleSeconds = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoSaveHintText));
        }
    }

    /// <summary>自动保存开关（界面绑定）。</summary>
    public bool AutoSaveEnabled
    {
        get => Cfg.AutoSaveEnabled;
        set
        {
            if (Cfg.AutoSaveEnabled == value) return;
            Cfg.AutoSaveEnabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutoSaveHintText));
        }
    }

    /// <summary>自动保存的说明文字（当前设置 + 本次已自动保存几次）。</summary>
    public string AutoSaveHintText
    {
        get
        {
            if (!AutoSaveEnabled) return "自动保存已关闭（切走 / 退出前记得 Ctrl+S）";
            var text = $"无操作 {AutoSaveIdleSeconds} 秒后自动保存";
            return _autoSaveCount > 0 ? $"{text}（本次已自动保存 {_autoSaveCount} 次）" : text;
        }
    }

    /// <summary>
    /// 起自动保存计时器。**必须在视图挂上之后调**（视图会持续喂 <see cref="NoteActivity"/>）。
    /// 幂等：重复调只是重启计时。
    /// </summary>
    private void StartAutoSaveTimer()
    {
        if (_autoSaveTimer is null)
        {
            _autoSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(AutoSaveCheckIntervalMs),
            };
            _autoSaveTimer.Tick += (_, _) => OnAutoSaveTick();
        }

        _idleTimer.NoteActivity();
        _autoSaveTimer.Start();
    }

    private void StopAutoSaveTimer() => _autoSaveTimer?.Stop();

    // ---- 视图侧调用（后置代码用；包一层是为了不让 View 直接碰私有成员） ----

    /// <summary>视图挂上后启动自动保存计时（后置代码在 <c>Loaded</c> 里调）。</summary>
    public void StartAutoSaveTimerFromView() => StartAutoSaveTimer();

    /// <summary>视图离开屏幕时停计时（后置代码在 <c>Unloaded</c> 里调）。</summary>
    public void StopAutoSaveTimerFromView()
    {
        StopAutoSaveTimer();
        _idleTimer.NoteActivity();
    }

    /// <summary>视图离开屏幕时立刻落盘一次（不等空闲计时）。</summary>
    public void AutoSaveFromView()
    {
        if (_editor is null || !_editor.Beatmap.IsDirty || !Cfg.AutoSaveEnabled) return;
        if (AutoSave())
        {
            _autoSaveCount++;
            OnPropertyChanged(nameof(AutoSaveHintText));
        }
    }

    private void OnAutoSaveTick()
    {
        if (_editor is null) return;

        var enabled = Cfg.AutoSaveEnabled;
        _idleTimer.Enabled = enabled;
        _idleTimer.ThresholdMs = AutoSaveIdleSeconds * 1000.0;

        // 播放中（IsPlaying）不自动保存：写盘可能卡一下，正听节奏时闪一下很难受。
        var due = _idleTimer.Advance(AutoSaveCheckIntervalMs, suppressed: IsPlaying);
        if (!due) return;

        // 到期但没有未保存改动：什么都不用做（Advance 已经把空闲清零了）
        if (!_editor.Beatmap.IsDirty) return;

        if (AutoSave())
        {
            _autoSaveCount++;
            OnPropertyChanged(nameof(AutoSaveHintText));
        }
    }

    /// <summary>
    /// 静默保存（自动保存走它）：不弹窗、不打断操作。
    /// </summary>
    public bool AutoSave()
    {
        if (_editor is null || !_editor.Beatmap.IsDirty) return false;
        return SaveChart(silent: true, out _);
    }

    /// <summary>
    /// 真要退出了：还有没保存的改动就问一句（外壳通过 <see cref="YuLiangToolkit.Core.Tools.ICloseConfirmer"/> 调）。
    ///
    /// <para>三个选项：**保存**（写盘后继续退出）/ **不保存**（直接退）/ **取消**（中止退出）。
    /// 保存失败时返回 false，让用户回去处理（不然等于悄悄丢数据）。</para>
    /// </summary>
    public bool ConfirmClose()
    {
        // 先停播放，免得确认框背后还在响（取消时再恢复）
        var wasPlaying = IsPlaying;
        if (wasPlaying)
        {
            _audio.Pause();
            IsPlaying = false;
            _timer.Stop();
        }

        var dirty = _editor is not null && _editor.Beatmap.IsDirty;
        if (!dirty)
        {
            if (wasPlaying) TogglePlay();
            return true;
        }

        var name = Path.GetFileName(_editor!.Beatmap.FilePath);
        var result = System.Windows.MessageBox.Show(
            $"「{name}」还有没保存的改动。\n\n退出前要保存吗？",
            "osu!mania 谱面编辑器",
            System.Windows.MessageBoxButton.YesNoCancel,
            System.Windows.MessageBoxImage.Warning);

        switch (result)
        {
            case System.Windows.MessageBoxResult.Yes:
                if (SaveChart(silent: false, out var error)) return true;
                System.Windows.MessageBox.Show(
                    $"保存失败，已取消退出：\n{error}", "保存失败",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                if (wasPlaying) TogglePlay();
                return false;

            case System.Windows.MessageBoxResult.No:
                Log("退出时选择了「不保存」，改动已丢弃");
                return true;

            default:
                if (wasPlaying) TogglePlay();
                return false;
        }
    }


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
        if (_editor is null) return;

        var removed = _editor.DeleteSelection();
        if (removed <= 0) return;

        RefreshAfterEdit(removed == 1 ? "已删除选中的 note" : $"已删除 {removed} 个 note");
    }

    /// <summary>全选。</summary>
    private void SelectAllNotes()
    {
        if (_editor is null) return;

        var all = new List<int>(_editor.Beatmap.HitObjects.Count);
        for (var i = 0; i < _editor.Beatmap.HitObjects.Count; i++) all.Add(i);
        _editor.Selection.SetTo(all);

        BuildAndPublishState();
        RaiseSelectionChanged();
    }

    /// <summary>清空选择。</summary>
    private void ClearSelection()
    {
        if (_editor is null) return;

        _editor.Selection.Clear();
        _editor.ClearRangeAnchor();
        BuildAndPublishState();
        RaiseSelectionChanged();
    }

    // ---------------------------------------------------------------- Z / V 跳转

    /// <summary>
    /// 跳到第一个 note 的开始时间（Z 键）。
    /// </summary>
    /// <param name="alsoExtendSelection">
    /// true = 同时把「锚点 → 第一个 note」之间、锚点那些列上的 note 加进选择（osu! 编辑器的行为）。
    /// false = 只跳转不改选择。
    /// </param>
    public void JumpToFirstNote(bool alsoExtendSelection = true)
    {
        if (_editor is null) return;

        double? target;
        if (alsoExtendSelection)
        {
            target = _editor.JumpToFirstNote();
        }
        else
        {
            target = _editor.EarliestNoteTime();
        }

        if (target is null)
        {
            StatusDetail = "谱面里还没有 note";
            return;
        }

        ApplyJump(target.Value, "已跳到第一个 note");
    }

    /// <summary>跳到最后一个 note 的开始时间（V 键）。</summary>
    public void JumpToLastNote(bool alsoExtendSelection = true)
    {
        if (_editor is null) return;

        double? target;
        if (alsoExtendSelection)
        {
            target = _editor.JumpToLastNote();
        }
        else
        {
            target = _editor.LatestNoteStartTime();
        }

        if (target is null)
        {
            StatusDetail = "谱面里还没有 note";
            return;
        }

        ApplyJump(target.Value, "已跳到最后一个 note");
    }

    /// <summary>
    /// 跳转落地：把播放头移过去、滚进视野、刷新选择状态。
    /// </summary>
    private void ApplyJump(double targetTime, string message)
    {
        if (_editor is null) return;

        SeekTo(targetTime);

        // 目标放到视口偏上一点，方便看它下面的内容
        _editor.CenterOnTime(targetTime, ratio: 0.35);

        BuildAndPublishState();
        RaiseSelectionChanged();

        var extra = SelectionCount > 0 ? $"（已选 {SelectionCount} 个）" : "";
        StatusDetail = $"{message}：{FormatTime(targetTime)}{extra}";
    }

    /// <summary>把 Z/V 的区间锚点定在当前位置（例如用户刚框选完）。</summary>
    public void SetRangeAnchorHere()
    {
        if (_editor is null) return;
        _editor.SetRangeAnchor(PlayheadMs);
    }

    // ---------------------------------------------------------------- 全屏

    private bool _isFullscreen;

    /// <summary>是不是在全屏编辑覆盖层里。</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set
        {
            if (!Set(ref _isFullscreen, value)) return;
            OnPropertyChanged(nameof(FullscreenGlyph));
            OnPropertyChanged(nameof(FullscreenHint));
        }
    }

    /// <summary>全屏按钮的字形。</summary>
    public string FullscreenGlyph => IsFullscreen ? "⤡" : "⤢";

    /// <summary>全屏按钮的提示。</summary>
    public string FullscreenHint => IsFullscreen ? "退出全屏（F11）" : "全屏编辑（F11）";

    /// <summary>请求切换全屏（View 去做真正的窗口操作）。</summary>
    public event EventHandler<bool>? FullscreenRequested;

    /// <summary>切换全屏（F11 / 按钮 / 双击画布空白）。</summary>
    public void ToggleFullscreen() => SetFullscreen(!IsFullscreen);

    /// <summary>设置全屏状态（View 在真正进出全屏后回调这里同步状态）。</summary>
    public void SetFullscreen(bool on)
    {
        if (IsFullscreen == on) return;
        IsFullscreen = on;
        FullscreenRequested?.Invoke(this, on);
    }

    /// <summary>View 完成窗口操作后同步状态（避免两边状态不一致）。</summary>
    public void SyncFullscreenState(bool on) => IsFullscreen = on;

    /// <summary>选择状态变了（界面刷新检查器 / 按钮可用性）。</summary>
    private void RaiseSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(InspectorTitle));
        OnPropertyChanged(nameof(InspectorDetail));
        OnPropertyChanged(nameof(ZoomText));
        CommandManager.InvalidateRequerySuggested();
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
        RaiseSelectionChanged();
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

        // 横向布局：标尺 + 按用户比例居中的轨道区（窗口内与全屏共用同一套算法）
        var layout = LaneLayout.Compute(ChartCanvasWidth, ChartCanvas.RulerWidth, NoteAreaRatio);
        _editor.SetLayout(layout.LaneLeft, layout.LaneWidth, _editor.Beatmap.KeyCount);

        var state = ChartRenderState.Build(_editor, height, ChartCanvasWidth, NoteAreaRatio, NoteAreaAlign);
        RenderState = new ChartRenderState
        {
            Notes = state.Notes,
            GridLines = state.GridLines,
            TimingPoints = state.TimingPoints,
            StartTime = state.StartTime,
            EndTime = state.EndTime,
            PxPerMs = state.PxPerMs,
            LaneLeft = state.LaneLeft,
            LaneWidth = state.LaneWidth,
            RulerLeft = state.RulerLeft,
            KeyCount = state.KeyCount,
            PlayheadMs = state.PlayheadMs,
            ShowPlayhead = state.ShowPlayhead,
            CreatePreview = state.CreatePreview,
            SelectedIndices = state.SelectedIndices,
            Marquee = state.Marquee,
            SelectionCount = state.SelectionCount,
            TotalNoteCount = state.TotalNoteCount,
            IsDragging = state.IsDragging,
            BackgroundImage = ShowBackgroundImage ? _backgroundImage : null,
            BackgroundOpacity = BackgroundOpacity,
            LaneBackgroundColor = LaneBackgroundColor,
        };
        RequestRedraw();
    }

    /// <summary>画布总宽度（由 View 在尺寸变化时告诉 VM）。</summary>
    public double ChartCanvasWidth { get; set; } = 400;

    /// <summary>轨道区宽度（由 View 在尺寸变化时告诉 VM）。</summary>
    /// <summary>
    /// Note 浏览区（轨道）宽度占可用宽度的比例（0.2~1.0）。界面滑块绑定它。
    ///
    /// <para>调到 1.0 = 铺满；调小会把轨道收窄并居中（4K 谱面铺满时每列太宽、note 显得扁）。</para>
    /// </summary>
    public double NoteAreaRatio
    {
        get => Math.Clamp(Cfg.NoteAreaRatio, LaneLayout.MinRatio, LaneLayout.MaxRatio);
        set
        {
            var clamped = Math.Clamp(value, LaneLayout.MinRatio, LaneLayout.MaxRatio);
            if (Math.Abs(Cfg.NoteAreaRatio - clamped) < 1e-6) return;

            Cfg.NoteAreaRatio = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoteAreaPercent));
            OnPropertyChanged(nameof(NoteAreaPercentText));
            BuildAndPublishState();
        }
    }

    /// <summary>宽度比例的百分比文字（滑块旁显示）。</summary>
    public string NoteAreaPercentText => $"{NoteAreaRatio * 100:0}%";

    /// <summary>滑块用的百分比值（20~100）。</summary>
    public double NoteAreaPercent
    {
        get => NoteAreaRatio * 100;
        set => NoteAreaRatio = value / 100.0;
    }

    /// <summary>Note 浏览区的水平对齐（0 贴左 / 1 居中 / 2 贴右）。</summary>
    public LaneLayout.LaneAlign NoteAreaAlign
    {
        get => (LaneLayout.LaneAlign)Math.Clamp(Cfg.NoteAreaAlign, 0, 2);
        set
        {
            if ((int)value == Cfg.NoteAreaAlign) return;
            Cfg.NoteAreaAlign = (int)value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAlignLeft));
            OnPropertyChanged(nameof(IsAlignCenter));
            OnPropertyChanged(nameof(IsAlignRight));
            BuildAndPublishState();
        }
    }

    /// <summary>对齐方式是不是「贴左」（单选按钮绑定）。</summary>
    public bool IsAlignLeft
    {
        get => NoteAreaAlign == LaneLayout.LaneAlign.Left;
        set { if (value) NoteAreaAlign = LaneLayout.LaneAlign.Left; }
    }

    /// <summary>对齐方式是不是「居中」。</summary>
    public bool IsAlignCenter
    {
        get => NoteAreaAlign == LaneLayout.LaneAlign.Center;
        set { if (value) NoteAreaAlign = LaneLayout.LaneAlign.Center; }
    }

    /// <summary>对齐方式是不是「贴右」。</summary>
    public bool IsAlignRight
    {
        get => NoteAreaAlign == LaneLayout.LaneAlign.Right;
        set { if (value) NoteAreaAlign = LaneLayout.LaneAlign.Right; }
    }

    /// <summary>用文件对话框选一张背景图（除了拖入之外的另一条入口）。</summary>
    private void ChooseBackground()
    {
        if (_editor is null) return;

        var dialog = new OpenFileDialog
        {
            Title = "选择谱面背景图",
            Filter = "图片 (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };

        var chartDir = Path.GetDirectoryName(_editor.Beatmap.FilePath) ?? "";
        if (Directory.Exists(chartDir)) dialog.InitialDirectory = chartDir;

        if (dialog.ShowDialog() != true) return;

        if (!SetBackgroundImage(dialog.FileName, out var error))
        {
            System.Windows.MessageBox.Show($"设置背景失败：\n{error}", "设置背景图",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- 背景图（加载 / 换图 / 清除）

    /// <summary>
    /// 解码一张图片（不锁文件；超大图按宽度缩一下省内存）。
    /// </summary>
    private static System.Windows.Media.ImageSource? DecodeImage(string path)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;   // 不锁文件
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 1600;   // 背景只用来垫底，不需要原图分辨率
            image.EndInit();
            image.Freeze();                  // 冻结后才能跨线程 / 安全共享
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 打开谱面后加载背景：优先用 <c>[Events]</c> 里声明的那张。
    /// </summary>
    private void LoadBackgroundForChart(EditorBeatmap beatmap)
    {
        if (!string.IsNullOrEmpty(beatmap.BackgroundPath) && File.Exists(beatmap.BackgroundPath))
        {
            var image = DecodeImage(beatmap.BackgroundPath);
            if (image is not null)
            {
                BackgroundImage = image;
                BackgroundStatus = Path.GetFileName(beatmap.BackgroundPath);
                return;
            }

            BackgroundStatus = "背景图打不开：" + Path.GetFileName(beatmap.BackgroundPath);
            Log($"⚠ 背景图打不开：{beatmap.BackgroundPath}");
            BackgroundImage = null;
            return;
        }

        if (!string.IsNullOrWhiteSpace(beatmap.BackgroundFileName))
        {
            // 谱面声明了背景，但文件不在同目录（很常见：只拷了 .osu）
            BackgroundImage = null;
            BackgroundStatus = $"找不到背景图：{beatmap.BackgroundFileName}";
            return;
        }

        BackgroundImage = null;
        BackgroundStatus = "没有背景图（把 .jpg / .png 拖进来即可设置）";
    }

    /// <summary>
    /// 把一张图片设为谱面背景：复制到谱面同目录 → 写进 <c>[Events]</c> → 重新加载预览。
    ///
    /// <para>目标是**真的改谱面**（保存后 osu! 里也是这张图），所以同名文件会被覆盖。</para>
    /// </summary>
    public bool SetBackgroundImage(string sourcePath, out string? error)
    {
        error = null;
        if (_editor is null)
        {
            error = "没有打开谱面。";
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            error = "图片文件不存在。";
            return false;
        }

        var chartPath = _editor.Beatmap.FilePath;
        var chartDir = Path.GetDirectoryName(chartPath) ?? "";
        if (string.IsNullOrEmpty(chartDir) || !Directory.Exists(chartDir))
        {
            error = "谱面所在目录不可用。";
            return false;
        }

        var fileName = Path.GetFileName(sourcePath);
        if (!BeatmapBackground.IsSafeFileName(fileName))
        {
            error = "图片文件名里含有不允许的字符。";
            return false;
        }

        try
        {
            var sameFile = string.Equals(Path.GetFullPath(sourcePath),
                Path.GetFullPath(Path.Combine(chartDir, fileName)), StringComparison.OrdinalIgnoreCase);

            var target = Path.Combine(chartDir, fileName);
            if (!sameFile)
            {
                File.Copy(sourcePath, target, overwrite: true);
            }

            _editor.Beatmap.BackgroundFileName = fileName;
            _editor.Beatmap.BackgroundPath = target;
            _editor.Beatmap.IsDirty = true;
            IsDirty = true;

            var image = DecodeImage(target);
            if (image is null)
            {
                error = "图片解码失败（不是有效的图片文件？）";
                BackgroundStatus = error;
                return false;
            }

            BackgroundImage = image;
            BackgroundStatus = fileName;
            StatusDetail = $"已把 {fileName} 设为背景图（保存后生效）";
            Log($"背景图：{fileName}（复制到谱面目录 {chartDir}）");
            return true;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            BackgroundStatus = "设置背景失败：" + error;
            Log($"✘ 设置背景图失败：{error}");
            return false;
        }
    }

    /// <summary>
    /// 清除背景：只清编辑器里的显示 + <c>[Events]</c> 里那一行（不删用户的图片文件）。
    /// </summary>
    public void ClearBackgroundImage()
    {
        if (_editor is null) return;

        _editor.Beatmap.BackgroundFileName = "";
        _editor.Beatmap.BackgroundPath = "";
        _editor.Beatmap.IsDirty = true;
        IsDirty = true;

        BackgroundImage = null;
        BackgroundStatus = "没有背景图（把 .jpg / .png 拖进来即可设置）";
        StatusDetail = "已清除背景图（保存后生效；图片文件没删）";
        Log("已清除背景图");
    }

    /// <summary>
    /// 拖入了一个文件：是图片就问一句要不要换成背景图。
    /// </summary>
    /// <param name="paths">拖进来的路径。</param>
    /// <param name="inNoteArea">是否落在 Note 浏览区上（截图 / 冒烟会用到这个信息）。</param>
    /// <returns>true = 已经按「换背景」处理了。</returns>
    public bool TryHandleBackgroundDrop(IEnumerable<string>? paths, bool inNoteArea)
    {
        if (paths is null) return false;

        var imagePath = paths.FirstOrDefault(BeatmapBackground.IsSupportedImage);
        if (imagePath is null) return false;
        if (_editor is null)
        {
            StatusDetail = "先打开一个谱面，再把图片拖进来当背景";
            return false;
        }

        var where = inNoteArea ? "" : "（图片建议拖到 Note 浏览区里）";
        var answer = System.Windows.MessageBox.Show(
            $"把「{Path.GetFileName(imagePath)}」设为这张谱面的背景图？{where}\n\n" +
            $"图片会被复制到谱面所在目录，并写进 .osu 的 [Events]（保存后生效）。",
            "设置背景图",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes)
        {
            StatusDetail = "已取消设置背景图";
            return true;
        }

        if (!SetBackgroundImage(imagePath, out var error))
        {
            System.Windows.MessageBox.Show($"设置背景失败：\n{error}", "设置背景图",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        return true;
    }

    // ---------------------------------------------------------------- 背景图

    private System.Windows.Media.ImageSource? _backgroundImage;
    private string _backgroundStatus = "没有背景图";

    /// <summary>当前背景图（解码好的，可直接给 <c>DrawingContext.DrawImage</c>）。</summary>
    public System.Windows.Media.ImageSource? BackgroundImage
    {
        get => _backgroundImage;
        private set
        {
            if (!Set(ref _backgroundImage, value)) return;
            OnPropertyChanged(nameof(HasBackgroundImage));
            BuildAndPublishState();
        }
    }

    /// <summary>有没有背景图（界面据此启用「清除背景」）。</summary>
    public bool HasBackgroundImage => _backgroundImage is not null;

    /// <summary>背景图状态文字（文件名 / 没有 / 加载失败原因）。</summary>
    public string BackgroundStatus
    {
        get => _backgroundStatus;
        private set => Set(ref _backgroundStatus, value);
    }

    /// <summary>是否显示背景图。</summary>
    public bool ShowBackgroundImage
    {
        get => Cfg.ShowBackgroundImage;
        set
        {
            if (Cfg.ShowBackgroundImage == value) return;
            Cfg.ShowBackgroundImage = value;
            OnPropertyChanged();
            BuildAndPublishState();
        }
    }

    /// <summary>背景图不透明度（0.05~1.0）。</summary>
    public double BackgroundOpacity
    {
        get => Math.Clamp(Cfg.BackgroundOpacity, 0.05, 1.0);
        set
        {
            var clamped = Math.Clamp(value, 0.05, 1.0);
            if (Math.Abs(Cfg.BackgroundOpacity - clamped) < 1e-4) return;
            Cfg.BackgroundOpacity = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BackgroundOpacityPercentText));
            BuildAndPublishState();
        }
    }

    /// <summary>不透明度的百分比文字。</summary>
    public string BackgroundOpacityPercentText => $"{BackgroundOpacity * 100:0}%";

    /// <summary>不透明度滑块（0~100）。</summary>
    public double BackgroundOpacityPercent
    {
        get => BackgroundOpacity * 100;
        set => BackgroundOpacity = value / 100.0;
    }

    // ---- Note 浏览区底色（叠在背景图与 note 之间的那一层） ----

    /// <summary>Note 浏览区底色（含透明度）。</summary>
    public System.Windows.Media.Color LaneBackgroundColor
    {
        get
        {
            var argb = Cfg.LaneBackgroundArgb;
            return System.Windows.Media.Color.FromArgb(
                (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        set
        {
            var argb = (value.A << 24) | (value.R << 16) | (value.G << 8) | value.B;
            if (Cfg.LaneBackgroundArgb == argb) return;
            Cfg.LaneBackgroundArgb = argb;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LaneBackgroundHex));
            OnPropertyChanged(nameof(LaneBackgroundOpacityPercentText));
            OnPropertyChanged(nameof(LaneBackgroundOpacityPercent));
            BuildAndPublishState();
        }
    }

    /// <summary>Note 浏览区底色的十六进制文字（<c>#AARRGGBB</c>，可直接编辑）。</summary>
    public string LaneBackgroundHex
    {
        get
        {
            var color = LaneBackgroundColor;
            return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        set => ApplyLaneBackgroundHex(value);
    }

    /// <summary>Note 浏览区底色的不透明度（0~100，百分比）。</summary>
    public double LaneBackgroundOpacityPercent
    {
        get => LaneBackgroundColor.A / 255.0 * 100;
        set
        {
            var clamped = (byte)Math.Round(Math.Clamp(value, 0, 100) / 100.0 * 255);
            var color = LaneBackgroundColor;
            if (color.A == clamped) return;
            LaneBackgroundColor = System.Windows.Media.Color.FromArgb(clamped, color.R, color.G, color.B);
        }
    }

    /// <summary>不透明度文字。</summary>
    public string LaneBackgroundOpacityPercentText => $"{LaneBackgroundOpacityPercent:0}%";

    /// <summary>是不是还是默认底色（界面据此启用「恢复默认」）。</summary>
    public bool IsLaneBackgroundDefault
        => Cfg.LaneBackgroundArgb == OsuManiaEditorConfig.DefaultLaneBackgroundArgbInt;

    /// <summary>把十六进制文字写回底色（格式不对就忽略，不弹窗）。</summary>
    private void ApplyLaneBackgroundHex(string text)
    {
        if (TryParseHexColor(text, out var color))
        {
            LaneBackgroundColor = color;
        }
    }

    /// <summary>解析 <c>#AARRGGBB</c> / <c>#RRGGBB</c> / <c>#ARGB</c> / <c>#RGB</c>。</summary>
    private static bool TryParseHexColor(string? text, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var hex = text.Trim().TrimStart('#');
        if (hex.Length is not (3 or 4 or 6 or 8)) return false;

        // 展开短格式：#RGB → #RRGGBB、#ARGB → #AARRGGBB
        if (hex.Length is 3 or 4)
        {
            var expanded = new StringBuilder(hex.Length * 2);
            foreach (var ch in hex) expanded.Append(ch).Append(ch);
            hex = expanded.ToString();
        }

        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        byte a, r, g, b;
        if (hex.Length == 8)
        {
            a = (byte)(value >> 24);
            r = (byte)(value >> 16);
            g = (byte)(value >> 8);
            b = (byte)value;
        }
        else
        {
            a = 255;
            r = (byte)(value >> 16);
            g = (byte)(value >> 8);
            b = (byte)value;
        }

        color = System.Windows.Media.Color.FromArgb(a, r, g, b);
        return true;
    }

    /// <summary>把 Note 浏览区底色恢复成默认（半透明黑）。</summary>
    public void ResetLaneBackgroundColor()
    {
        Cfg.LaneBackgroundArgb = OsuManiaEditorConfig.DefaultLaneBackgroundArgbInt;
        OnPropertyChanged(nameof(LaneBackgroundColor));
        OnPropertyChanged(nameof(LaneBackgroundHex));
        OnPropertyChanged(nameof(LaneBackgroundOpacityPercent));
        OnPropertyChanged(nameof(LaneBackgroundOpacityPercentText));
        OnPropertyChanged(nameof(IsLaneBackgroundDefault));
        BuildAndPublishState();
        StatusDetail = "Note 浏览区底色已恢复默认";
    }

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

        // 上次用的工具：老配置里只有 longNoteTool，没有 tool 就按它推
        var saved = Cfg.Tool is >= 0 and <= 2 ? (ChartEditor.EditTool)Cfg.Tool : ChartEditor.EditTool.Note;
        if (Cfg.Tool is 0 && Cfg.LongNoteTool) saved = ChartEditor.EditTool.LongNote;   // 老配置转换
        _tool = saved;
        if (_editor is not null) _editor.Tool = saved;

        OnPropertyChanged(nameof(FollowPlayhead));
        OnPropertyChanged(nameof(GridDivisor));
        OnPropertyChanged(nameof(SnapToGrid));
        OnPropertyChanged(nameof(Tool));
        OnPropertyChanged(nameof(IsSelectTool));
        OnPropertyChanged(nameof(IsNoteTool));
        OnPropertyChanged(nameof(IsLongNoteTool));
        OnPropertyChanged(nameof(LongNoteTool));
        OnPropertyChanged(nameof(PxPerMs));
        OnPropertyChanged(nameof(AudioOffsetMs));
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(SelectionText));
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
        // 退出 / 视图被丢弃前先落盘一次：自动保存可能还没到点，OnClosed 之后就没机会了。
        // （外壳切走工具页时**不会**调这里，所以视图侧还要在 Unloaded 里保底保存一次。）
        if (_editor is not null && _editor.Beatmap.IsDirty) AutoSave();

        StopAutoSaveTimer();
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
    /// <summary>
    /// 列表里显示的主文字。**不带 emoji**（🔴 / 🟢 在等宽字体下没有字形，会渲染成豆腐块或空白），
    /// 红线 / 绿线靠视图里的色块区分。
    /// </summary>
    public string Text => IsRed
        ? $"{Point.Time:0}ms · {Bpm:0.##} BPM"
        : $"{Point.Time:0}ms · SV {Sv:0.##}x";

    /// <summary>时间文字（编辑框用）。</summary>
    public string TimeText => Point.Time.ToString("0");
}
