using System.Collections.ObjectModel;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Tools;

namespace YuLiangToolkit.Tools.MediaPlayer.ViewModels;

/// <summary>
/// 「视频 / 音乐播放器」的 ViewModel —— 界面状态与命令都在这里，**播放本身交给
/// <see cref="IMediaPlayback"/>**（界面里是 <c>MediaElement</c> 的适配器，自检里是假播放器）。
///
/// <para>分工：</para>
/// <list type="bullet">
///   <item><see cref="MediaTypes"/> / <see cref="MediaScanner"/> / <see cref="PlaylistMerger"/> /
///   <see cref="PlayQueue"/> / <see cref="MediaTime"/>：纯逻辑（自检直接断言）；</item>
///   <item><c>MediaPlayerViewModel.cs</c>（本文件）：配置桥接、界面状态、命令装配；</item>
///   <item><c>.Playlist.cs</c>：打开文件夹 / 打开文件 / 增删 / 记住上次的列表；</item>
///   <item><c>.Playback.cs</c>：播放 / 暂停 / 停止 / 快进快退 / 上一首下一首 / 播放顺序。</item>
/// </list>
///
/// <para>配置在 <c>Config\media-player\config.json</c>，列表快照在 <c>Data\media-player\playlist.json</c>。
/// 细节与踩过的坑见 AGENTS.md 2.14 节。</para>
/// </summary>
public sealed partial class MediaPlayerViewModel : ToolViewModelBase, IBackgroundTool
{
    /// <summary>播放顺序的算术（随机模式注入 <see cref="Random"/>，自检里换成固定种子）。</summary>
    private readonly PlayQueue _queue;

    private double _seekStep;
    private double _volume;
    private bool _isSettingsVisible;

    public MediaPlayerViewModel() : base(MediaPlayerConfig.ToolId)
    {
        Cfg = LoadConfig<MediaPlayerConfig>();

        _seekStep = Cfg.SeekStepEffective;
        _volume = Cfg.VolumeEffective;
        _queue = new PlayQueue { Mode = Cfg.PlayOrder };

        // 打开
        OpenFolderCommand = new AsyncRelayCommand(_ => OpenFolderAsync());
        OpenFilesCommand = new AsyncRelayCommand(_ => OpenFilesAsync());

        // 播放控制
        PlayPauseCommand = new RelayCommand(_ => TogglePlayPause());
        StopCommand = new RelayCommand(_ => StopPlayback());
        SeekBackwardCommand = new RelayCommand(_ => SeekBy(-SeekStepSeconds));
        SeekForwardCommand = new RelayCommand(_ => SeekBy(SeekStepSeconds));
        PreviousCommand = new RelayCommand(_ => MoveTrack(-1), _ => Items.Count > 0);
        NextCommand = new RelayCommand(_ => MoveTrack(+1), _ => Items.Count > 0);
        CyclePlayModeCommand = new RelayCommand(_ => PlayMode = PlayQueue.Cycle(PlayMode));
        ToggleFullscreenCommand = new RelayCommand(_ => ToggleFullscreen());

        // 列表
        PlaySelectedCommand = new RelayCommand(_ => PlayItem(SelectedItem), _ => SelectedItem is not null);
        RemoveSelectedCommand = new RelayCommand(_ => RemoveSelected(), _ => SelectedItem is not null);
        ClearPlaylistCommand = new RelayCommand(_ => ClearPlaylist(), _ => Items.Count > 0);
        RevealSelectedCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedItem is not null);

        // 设置 / 文件夹
        ToggleSettingsCommand = new RelayCommand(_ => IsSettingsVisible = !IsSettingsVisible);
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        OpenDataFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());

        Log($"配置：{ConfigPathText}");
        Log($"数据：{StoragePathText}");

        if (Cfg.RestorePlaylist) RestorePlaylistState();
        RefreshPlaylistState();

        if (IsFirstRun) Log("首次运行：已生成默认配置（设置页里能改读取方式、播放顺序、快进快退步长…）");
        if (!string.IsNullOrEmpty(Cfg.LastFolder)) Log($"上次打开的文件夹：{Cfg.LastFolder}");

        // 截图 / 看版式用：塞几行假数据（不发任何请求、也不碰磁盘之外的路径）
        if (Environment.GetEnvironmentVariable("YLT_PLAYER_DEMO") is not null) SeedDemoItems();

        // 截图用：直接把设置面板打开（不然要点一下齿轮才看得到那一屏）
        if (Environment.GetEnvironmentVariable("YLT_PLAYER_SETTINGS") is not null) IsSettingsVisible = true;

        // 截图用：启动就进全屏（配合 YLT_PLAYER_DEMO 核对全屏版式）
        if (Environment.GetEnvironmentVariable("YLT_PLAYER_FULLSCREEN") is not null) IsFullscreen = true;

        // 截图用：进全屏后先把控制条藏起来，核对「沉浸」那一帧（正常是鼠标一动就出来）
        if (Environment.GetEnvironmentVariable("YLT_PLAYER_HIDE_CONTROLS") is not null) HideControls();
    }

    // ---------- 配置与命令 ----------

    /// <summary>强类型配置（改一下最多 400ms 自动落盘）。</summary>
    public MediaPlayerConfig Cfg { get; }

    public AsyncRelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand OpenFilesCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand SeekBackwardCommand { get; }
    public RelayCommand SeekForwardCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand CyclePlayModeCommand { get; }
    public RelayCommand ToggleFullscreenCommand { get; }
    public RelayCommand PlaySelectedCommand { get; }
    public RelayCommand RemoveSelectedCommand { get; }
    public RelayCommand ClearPlaylistCommand { get; }
    public RelayCommand RevealSelectedCommand { get; }
    public RelayCommand ToggleSettingsCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    // ---------- 列表数据 ----------

    /// <summary>播放列表（界面绑的就是它；行号在增删后统一重排）。</summary>
    public ObservableCollection<MediaItem> Items { get; } = new();

    private MediaItem? _selectedItem;

    /// <summary>列表里选中的那一行（单击选中，双击播放）。</summary>
    public MediaItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (Set(ref _selectedItem, value)) OnPropertyChanged(nameof(SelectedItemText));
        }
    }

    /// <summary>选中行的说明（列表下方那行小字）。</summary>
    public string SelectedItemText => SelectedItem is null
        ? "单击选中一行；双击开始播放"
        : $"{SelectedItem.KindText} · {SelectedItem.Folder}";

    /// <summary>列表统计：「12 个文件 · 音频 9 · 视频 3」。</summary>
    public string PlaylistSummary
    {
        get
        {
            if (Items.Count == 0) return "列表是空的";
            var audio = Items.Count(i => i.Kind == MediaKind.Audio);
            var video = Items.Count - audio;
            return $"共 {Items.Count} 个 · 音频 {audio} · 视频 {video}";
        }
    }

    /// <summary>列表空了才显示引导文字（绑 <c>EmptyToVisInverse</c> 那种提示是反过来的，这里用 Bool 取反再比）。</summary>
    public bool IsPlaylistEmpty => Items.Count == 0;

    /// <summary>列表变化后刷新一切跟条数有关的界面状态（增删 / 覆盖 / 恢复都要调）。</summary>
    private void RefreshPlaylistState()
    {
        for (var i = 0; i < Items.Count; i++) Items[i].Number = i + 1;
        OnPropertyChanged(nameof(PlaylistSummary));
        OnPropertyChanged(nameof(IsPlaylistEmpty));
        OnPropertyChanged(nameof(SelectedItemText));
        if (Items.Count == 0) CurrentItem = null;
    }

    // ---------- 当前曲目与进度 ----------

    private MediaItem? _currentItem;

    /// <summary>正在播（或准备播）的那一首。</summary>
    public MediaItem? CurrentItem
    {
        get => _currentItem;
        private set
        {
            if (!Set(ref _currentItem, value)) return;
            OnPropertyChanged(nameof(HasTrack));
            OnPropertyChanged(nameof(TitleText));
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(CoverGlyph));
            RaiseBackgroundState();   // 有没有东西可播 → 决定外壳要不要显示迷你条
        }
    }

    /// <summary>有没有当前曲目（中间区域的标题 / 进度条用）。</summary>
    public bool HasTrack => CurrentItem is not null;

    /// <summary>中间区域的大标题：当前曲目名，没有就一句引导。</summary>
    public string TitleText => CurrentItem?.Title ?? "还没有在播的内容";

    /// <summary>中间区域的副标题：类别 + 所在文件夹。</summary>
    public string SubtitleText => CurrentItem is null
        ? "左边那列是工具列表；打开右侧的文件夹按钮挑一个音乐 / 视频文件夹开始"
        : $"{CurrentItem.KindText} · {CurrentItem.Folder}";

    /// <summary>中间区域的大图标（音频时显示在封面上）。</summary>
    public string CoverGlyph => CurrentItem?.KindGlyph ?? "🎵";

    private string _positionText = "0:00";
    /// <summary>当前位置文本。</summary>
    public string PositionText
    {
        get => _positionText;
        private set => Set(ref _positionText, value);
    }

    private string _durationText = "--:--";
    /// <summary>总时长文本（没打开时是 <c>--:--</c>）。</summary>
    public string DurationText
    {
        get => _durationText;
        private set => Set(ref _durationText, value);
    }

    private double _progressRatio;
    /// <summary>进度 0.0 ~ 1.0（进度条绑它）。</summary>
    public double ProgressRatio
    {
        get => _progressRatio;
        private set => Set(ref _progressRatio, value);
    }

    // ---------- 播放状态 ----------

    private bool _isPlaying;
    /// <summary>是不是正在放（暂停按钮的图标 / 列表行高亮都看它）。</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            OnPropertyChanged(nameof(ShowPlayIcon));
            OnPropertyChanged(nameof(ShowPauseIcon));
            OnPropertyChanged(nameof(PlayTooltip));
        }
    }

    /// <summary>播放按钮里那个三角形要不要显示（视图用矢量 Path，不依赖字体字形）。</summary>
    public bool ShowPlayIcon => !IsPlaying;

    /// <summary>暂停图标（两根竖条）要不要显示。</summary>
    public bool ShowPauseIcon => IsPlaying;

    /// <summary>播放/暂停按钮的悬停提示。</summary>
    public string PlayTooltip => IsPlaying ? "暂停（空格）" : "播放（空格）";

    private bool _hasVideo;
    /// <summary>当前文件有没有画面（有就显示视频，没有就显示封面占位）。</summary>
    public bool HasVideo
    {
        get => _hasVideo;
        private set
        {
            if (!Set(ref _hasVideo, value)) return;
            OnPropertyChanged(nameof(CoverVisible));
        }
    }

    /// <summary>音频时盖在视频区上面的封面占位要不要显示。</summary>
    public bool CoverVisible => !HasVideo;

    // ---------- 音量 / 步长 / 播放顺序 ----------

    /// <summary>音量 0 ~ 100（滑块直接绑它，改一下就写进配置并作用到播放器）。</summary>
    public double VolumeValue
    {
        get => _volume;
        set
        {
            var clamped = double.IsNaN(value) ? _volume : Math.Clamp(Math.Round(value), 0, 100);
            if (!Set(ref _volume, clamped)) return;
            Cfg.Volume = clamped;
            if (Player is not null) Player.Volume = clamped / 100.0;
            OnPropertyChanged(nameof(VolumeText));
        }
    }

    /// <summary>「80%」。</summary>
    public string VolumeText => $"{VolumeValue:0}%";

    /// <summary>快进 / 快退一步多少秒（滑块 + 数字框都绑它，统一规整到 0.1 秒）。</summary>
    public double SeekStepSeconds
    {
        get => _seekStep;
        set
        {
            var step = MediaTime.RoundStep(value);
            if (!Set(ref _seekStep, step)) return;
            Cfg.SeekStepSeconds = step;
            OnPropertyChanged(nameof(SeekStepText));
            OnPropertyChanged(nameof(SeekBackwardTooltip));
            OnPropertyChanged(nameof(SeekForwardTooltip));
        }
    }

    /// <summary>「5.0 秒」。</summary>
    public string SeekStepText => $"{SeekStepSeconds:0.0} 秒";

    /// <summary>快退按钮的提示（顺手把「右键 = 上一首」写进去，不然没人发现）。</summary>
    public string SeekBackwardTooltip => $"左键：快退 {SeekStepText}（← 键）　右键：上一首";

    /// <summary>快进按钮的提示。</summary>
    public string SeekForwardTooltip => $"左键：快进 {SeekStepText}（→ 键）　右键：下一首";

    /// <summary>播放顺序（点按钮轮换；设置页里也能直接选）。</summary>
    public PlayOrderMode PlayMode
    {
        get => _queue.Mode;
        set
        {
            if (_queue.Mode == value) return;
            _queue.Mode = value;
            Cfg.PlayOrder = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayModeText));
            OnPropertyChanged(nameof(PlayModeGlyph));
            OnPropertyChanged(nameof(ModeShortText));
            OnPropertyChanged(nameof(PlayModeTooltip));
            OnPropertyChanged(nameof(SelectedPlayMode));
        }
    }

    public string PlayModeText => PlayQueue.ModeText(PlayMode);
    public string PlayModeGlyph => PlayQueue.ModeGlyph(PlayMode);

    /// <summary>
    /// 播放条上那个模式按钮的两个字（循环 / 单曲 / 随机）。
    /// 用文字而不是 🔁🔂🔀：那几个 emoji 在本机字体会变成豆腐块（见 2.14）。
    /// </summary>
    public string ModeShortText => PlayMode switch
    {
        PlayOrderMode.RepeatOne => "单曲",
        PlayOrderMode.Shuffle => "随机",
        _ => "循环",
    };

    public string PlayModeTooltip => PlayQueue.ModeTooltip(PlayMode);

    // ---------- 下拉框桥接（值 ↔ 显示文本分离，见 2.4） ----------

    public IReadOnlyList<OptionItem<PlayOrderMode>> PlayModeOptions { get; } = OptionItem.From(
        (PlayOrderMode.LoopAll, "列表循环"),
        (PlayOrderMode.RepeatOne, "单曲循环"),
        (PlayOrderMode.Shuffle, "随机播放"));

    /// <summary>设置页里的「播放顺序」下拉。</summary>
    public OptionItem<PlayOrderMode> SelectedPlayMode
    {
        get => OptionItem<PlayOrderMode>.Find(PlayModeOptions, PlayMode) ?? PlayModeOptions[0];
        set
        {
            if (value is null) return;
            PlayMode = value.Value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<OptionItem<MediaIncludeMode>> IncludeModeOptions { get; } = OptionItem.From(
        (MediaIncludeMode.AudioAndVideo, "音频 + 视频（一并读取）"),
        (MediaIncludeMode.AudioOnly, "仅音频"),
        (MediaIncludeMode.VideoOnly, "仅视频"));

    /// <summary>设置页里的「打开文件夹时读取哪些文件」下拉。</summary>
    public OptionItem<MediaIncludeMode> SelectedIncludeMode
    {
        get => OptionItem<MediaIncludeMode>.Find(IncludeModeOptions, Cfg.IncludeMode) ?? IncludeModeOptions[0];
        set
        {
            if (value is null || Cfg.IncludeMode == value.Value) return;
            Cfg.IncludeMode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IncludeModeHint));
            Log($"读取方式改为：{MediaTypes.Describe(value.Value)}（对**之后**打开的文件生效）");
        }
    }

    /// <summary>当前读取方式会收下的扩展名（设置页那一行灰字）。</summary>
    public string IncludeModeHint => $"会读取：{MediaTypes.DescribeExtensions(Cfg.IncludeMode)}";

    public IReadOnlyList<OptionItem<bool>> AppendModeOptions { get; } = OptionItem.From(
        (true, "只添加到列表（追加）"),
        (false, "先清空列表（覆盖）"));

    /// <summary>设置页里的「再开一个文件夹时怎么办」下拉。</summary>
    public OptionItem<bool> SelectedAppendMode
    {
        get => OptionItem<bool>.Find(AppendModeOptions, Cfg.AppendWhenOpeningFolder) ?? AppendModeOptions[0];
        set
        {
            if (value is null) return;
            Cfg.AppendWhenOpeningFolder = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>设置页里的「读取子文件夹」。</summary>
    public bool ScanRecursive
    {
        get => Cfg.ScanRecursive;
        set
        {
            if (Cfg.ScanRecursive == value) return;
            Cfg.ScanRecursive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>设置页里的「下次打开恢复上次的列表」。</summary>
    public bool RestorePlaylist
    {
        get => Cfg.RestorePlaylist;
        set
        {
            if (Cfg.RestorePlaylist == value) return;
            Cfg.RestorePlaylist = value;
            OnPropertyChanged();
            if (!value) return;
            SavePlaylistState();
        }
    }

    /// <summary>设置页里的「恢复上次听到的位置」。</summary>
    public bool ResumeLastPlayed
    {
        get => Cfg.ResumeLastPlayed;
        set
        {
            if (Cfg.ResumeLastPlayed == value) return;
            Cfg.ResumeLastPlayed = value;
            OnPropertyChanged();
        }
    }

    // ---------- 设置面板 ----------

    /// <summary>设置面板要不要显示（**同页面板**，不弹子窗口；盖住左边的播放区，列表照旧可用）。</summary>
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set
        {
            if (!Set(ref _isSettingsVisible, value)) return;
            ShowControls();          // 面板是从控制条上的齿轮打开的，别让控制条正好在这时候缩回去
            OnPropertyChanged(nameof(SettingsGlyph));
        }
    }

    /// <summary>
    /// 设置按钮的图标：Segoe MDL2 Assets 的「齿轮」（E713）；打开后变成「返回箭头」（E72B）。
    /// ⚠ 别换成 ⚙ 这类字符：本机字体不一定有，界面上会变成豆腐块（见 2.14 节的踩坑）。
    /// </summary>
    public string SettingsGlyph => IsSettingsVisible ? "\uE72B" : "\uE713";

    /// <summary>设置页的说明文字（当前配置文件在哪）。</summary>
    public string SettingsPathText => $"配置：{ConfigPathText}";

    // ---------- 全屏 ----------

    private bool _isFullscreen;
    private bool _controlsVisible = true;

    /// <summary>
    /// 是不是全屏。ViewModel 只记状态：真正的窗口操作由界面（外壳的 <c>IFullscreenHost</c>）做，
    /// 这样自检里没有窗口也能断言状态机（见 <see cref="FullscreenChanged"/>）。
    /// </summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set
        {
            if (!Set(ref _isFullscreen, value)) return;
            ControlsVisible = true;                  // 进 / 出全屏都先把控制条亮出来
            OnPropertyChanged(nameof(FullscreenGlyph));
            OnPropertyChanged(nameof(ShowPlaylist));
            OnPropertyChanged(nameof(FullscreenTooltip));
            OnPropertyChanged(nameof(ShowCornerFullscreenButton));
            FullscreenChanged?.Invoke(this, value);
        }
    }

    /// <summary>全屏时画面右下角那个圆钮只在非全屏显示（全屏时它会挡住画面，改用控制条里的按钮）。</summary>
    public bool ShowCornerFullscreenButton => !IsFullscreen;

    /// <summary>
    /// 全屏时控制条要不要露出来：鼠标一动就露，停 2.5 秒自动缩回去（只影响全屏，普通模式永远露着）。
    /// 计时器在界面代码后置里（见 <c>MediaPlayerView</c> 的 mouse-idle 逻辑）。
    /// </summary>
    public bool ControlsVisible
    {
        get => _controlsVisible;
        private set => Set(ref _controlsVisible, value);
    }

    /// <summary>鼠标动了 / 刚进全屏 → 把控制条亮出来。</summary>
    public void ShowControls()
    {
        if (!ControlsVisible) ControlsVisible = true;
    }

    /// <summary>鼠标闲置够久 → 把控制条缩回去（普通模式 / 正开着设置面板时不缩）。</summary>
    public void HideControls()
    {
        if (IsFullscreen && !IsSettingsVisible) ControlsVisible = false;
    }

    /// <summary>全屏按钮的图标：Segoe MDL2 的「全屏」E740 / 「退出全屏」E73F。</summary>
    public string FullscreenGlyph => IsFullscreen ? "\uE73F" : "\uE740";

    /// <summary>全屏按钮的悬停提示。</summary>
    public string FullscreenTooltip => IsFullscreen ? "退出全屏（Esc）" : "全屏（Esc 退出）";

    /// <summary>全屏时把右边的播放列表收起来，画面占满整个工具页。</summary>
    public bool ShowPlaylist => !IsFullscreen;

    /// <summary>
    /// 全屏状态变了（界面据此去操作真实窗口：藏标题栏 / 侧边栏、铺满屏幕）。
    /// <para>带 true/false 参数 = 目标状态。</para>
    /// </summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>切换全屏（按钮 / Esc / F11 都走它）。</summary>
    public void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    /// <summary>强制退出全屏（切走工具、界面卸载时调用，避免留下一个“藏了壳”的窗口）。</summary>
    public void ExitFullscreen()
    {
        if (IsFullscreen) IsFullscreen = false;
    }

    // ---------- 后台播放（IBackgroundTool） ----------

    /// <summary>设置里的「后台播放」；改一下会顺带通知外壳刷新迷你条。</summary>
    public bool BackgroundPlayback
    {
        get => Cfg.BackgroundPlayback;
        set
        {
            if (Cfg.BackgroundPlayback == value) return;
            Cfg.BackgroundPlayback = value;
            OnPropertyChanged();
            RaiseBackgroundState();
        }
    }

    /// <summary>切走时要不要留着这个视图（= 后台播放 + 当前有曲目）。</summary>
    public bool KeepAliveOnLeave => Cfg.BackgroundPlayback && CurrentItem is not null;

    /// <summary>不在前台时要不要显示左下角迷你播放条。</summary>
    public bool ShowMiniPanel => Cfg.BackgroundPlayback && CurrentItem is not null;

    /// <summary>上面两个值可能变了（外壳据此决定留不留视图、显不显示迷你条）。</summary>
    public event EventHandler? BackgroundStateChanged;

    private void RaiseBackgroundState() => BackgroundStateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>把设置恢复成出厂值（**不动播放列表**，也不动快进快退之外的播放状态）。</summary>
    public void ResetSettings()
    {
        Cfg.IncludeMode = MediaIncludeMode.AudioAndVideo;
        Cfg.ScanRecursive = true;
        Cfg.AppendWhenOpeningFolder = true;
        Cfg.Volume = 80;
        Cfg.PlayOrder = PlayOrderMode.LoopAll;
        Cfg.SeekStepSeconds = 5.0;
        Cfg.RestorePlaylist = true;
        Cfg.ResumeLastPlayed = true;
        Cfg.BackgroundPlayback = true;

        VolumeValue = Cfg.Volume;
        SeekStepSeconds = Cfg.SeekStepSeconds;
        PlayMode = Cfg.PlayOrder;

        OnPropertyChanged(nameof(SelectedIncludeMode));
        OnPropertyChanged(nameof(IncludeModeHint));
        OnPropertyChanged(nameof(SelectedAppendMode));
        OnPropertyChanged(nameof(ScanRecursive));
        OnPropertyChanged(nameof(RestorePlaylist));
        OnPropertyChanged(nameof(ResumeLastPlayed));
        OnPropertyChanged(nameof(BackgroundPlayback));
        RaiseBackgroundState();

        StatusText = "设置已恢复默认值";
        Log("设置已恢复默认值");
    }

    // ---------- 演示数据（只为截图看版式） ----------

    /// <summary>
    /// <c>YLT_PLAYER_DEMO=1</c> 时往列表里塞几行**假文件**：只想看列表与封面的版式时用，
    /// 不碰磁盘（点了会提示「文件不在了」），跟下载器的 <c>YLT_MAP_DEMO</c> 一个用途。
    /// </summary>
    private void SeedDemoItems()
    {
        var demo = new[]
        {
            (Name: "01 - 夜航星", Kind: MediaKind.Audio),
            (Name: "02 - 深海列车", Kind: MediaKind.Audio),
            (Name: "03 - 长安十二时辰（主题曲）", Kind: MediaKind.Audio),
            (Name: "Live - 千本樱（钢琴版）", Kind: MediaKind.Audio),
            (Name: "纪录片 - 星空下的城市", Kind: MediaKind.Video),
            (Name: "MV - 花火大会 4K", Kind: MediaKind.Video),
        };

        var folder = Storage.PathOf("demo");
        foreach (var (name, kind) in demo)
        {
            var ext = kind == MediaKind.Video ? ".mp4" : ".mp3";
            Items.Add(new MediaItem(Path.Combine(folder, name + ext), kind));
        }

        RefreshPlaylistState();
        SelectedItem = Items[0];
        CurrentItem = Items[0];
        StatusText = "演示数据（YLT_PLAYER_DEMO）：列表里这几行是假的，不读磁盘";
        UpdatePosition(TimeSpan.Zero, TimeSpan.FromSeconds(253));
    }

    // ---------- 生命周期 ----------

    protected override void OnClosed()
    {
        SavePlaylistState();

        // 全屏状态不能留给下一个页面（外壳会把标题栏 / 侧边栏一直藏着）
        ExitFullscreen();

        // 后台播放：外壳会保住这个视图（里面的 MediaElement 还在），所以这里**别**摘播放器、别停。
        if (KeepAliveOnLeave)
        {
            Log("后台播放：界面切走了，声音继续（左下角小窗可以暂停 / 换歌）");
            return;
        }

        DetachPlayer();
    }
}
