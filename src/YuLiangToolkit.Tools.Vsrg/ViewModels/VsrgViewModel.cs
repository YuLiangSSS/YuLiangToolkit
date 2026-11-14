using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.Vsrg.Core;

namespace YuLiangToolkit.Tools.Vsrg.ViewModels;

/// <summary>工具页的四个界面。</summary>
public enum VsrgPage
{
    /// <summary>选歌。</summary>
    SongSelect = 0,

    /// <summary>设置。</summary>
    Settings = 1,

    /// <summary>游玩。</summary>
    Play = 2,

    /// <summary>结算。</summary>
    Result = 3,
}

/// <summary>
/// 「VSRG」的 ViewModel：选歌 / 设置 / 游玩 / 结算四个页面 + 谱面集库 + 游玩会话。
///
/// <para><b>分工</b>：判定（<see cref="GameplayEngine"/>）/ 解析（<see cref="OsuChartReader"/>）/
/// 导入（<see cref="MapsetLibrary"/>）/ 时钟（<c>AudioClockEngine</c>）全是独立纯逻辑；
/// 这里只负责「页面怎么切」「什么时候 tick 一下」「界面显示什么」。</para>
///
/// <para><b>全屏</b>：VM 只发请求（<see cref="FullscreenRequested"/>），真正的窗口操作由界面层的
/// <c>IFullscreenHost</c>（外壳 MainWindow）做 —— 与播放器 / 阅读器同一套做法。</para>
/// </summary>
public sealed partial class VsrgViewModel : ToolViewModelBase
{
    private readonly MapsetLibrary _library;

    private VsrgPage _page = VsrgPage.SongSelect;
    private MapsetEntry? _selectedSong;
    private MapsetDifficulty? _selectedDifficulty;
    private ImageSource? _coverImage;
    private string _coverHint = "拖入 .osz 文件开始";
    private string _importStatus = "";
    private bool _isImporting;

    /// <summary>构造：加载配置、建谱面集库、进选歌页。</summary>
    public VsrgViewModel() : base(VsrgConfig.ToolId)
    {
        Cfg = LoadConfig<VsrgConfig>();
        Cfg.PropertyChanged += OnConfigChanged;

        _library = new MapsetLibrary(Storage);

        Songs = new ObservableCollection<MapsetEntry>();
        Difficulties = new ObservableCollection<MapsetDifficulty>();

        InitializeSongSelectCommands();
        InitializeSettings();
        InitializePlay();

        RefreshLibrary(silent: true);
        Page = VsrgPage.SongSelect;
    }

    // ---------------------------------------------------------------- 配置

    /// <summary>本工具的配置（全屏 / 偏移 / 滚动速度 / 键位 / 判定档 / 面条尾判）。</summary>
    public VsrgConfig Cfg { get; }

    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 滚动速度影响「note 从屏幕顶到判定线」的时间；游玩中改了也立刻生效（只影响观感，不影响判定）
        if (e.PropertyName is nameof(VsrgConfig.ScrollSpeed))
        {
            OnPropertyChanged(nameof(ApproachMs));
            OnPropertyChanged(nameof(ScrollSpeedText));
        }

        if (e.PropertyName is nameof(VsrgConfig.AudioOffsetMs)) OnPropertyChanged(nameof(AudioOffsetText));
        if (e.PropertyName is nameof(VsrgConfig.MusicVolume)) OnPropertyChanged(nameof(MusicVolume));
        if (e.PropertyName is nameof(VsrgConfig.TailWindowMultiplier)) OnPropertyChanged(nameof(TailWindowText));
        if (e.PropertyName is nameof(VsrgConfig.TailJudgementEnabled)) OnPropertyChanged(nameof(TailWindowText));
    }

    // ---------------------------------------------------------------- 页面切换

    /// <summary>当前页面。</summary>
    public VsrgPage Page
    {
        get => _page;
        private set
        {
            if (!Set(ref _page, value)) return;
            OnPropertyChanged(nameof(IsSongSelectPage));
            OnPropertyChanged(nameof(IsSettingsPage));
            OnPropertyChanged(nameof(IsPlayPage));
            OnPropertyChanged(nameof(IsResultPage));
        }
    }

    /// <summary>现在是不是选歌页（XAML 绑定显隐）。</summary>
    public bool IsSongSelectPage => Page == VsrgPage.SongSelect;

    /// <summary>现在是不是设置页。</summary>
    public bool IsSettingsPage => Page == VsrgPage.Settings;

    /// <summary>现在是不是游玩页。</summary>
    public bool IsPlayPage => Page == VsrgPage.Play;

    /// <summary>现在是不是结算页。</summary>
    public bool IsResultPage => Page == VsrgPage.Result;

    /// <summary>请求外壳进入 / 退出全屏（界面层订阅后转给 <c>IFullscreenHost</c>）。</summary>
    public event EventHandler<bool>? FullscreenRequested;

    /// <summary>发一次全屏请求（界面层用；自检不订阅就能安全调用）。</summary>
    public void RequestFullscreen(bool on) => FullscreenRequested?.Invoke(this, on);

    // ---------------------------------------------------------------- 选歌页

    /// <summary>全部谱面集（按艺术家 / 标题排序）。</summary>
    public ObservableCollection<MapsetEntry> Songs { get; }

    /// <summary>当前选中谱面集里的难度。</summary>
    public ObservableCollection<MapsetDifficulty> Difficulties { get; }

    /// <summary>当前选中的谱面集。</summary>
    public MapsetEntry? SelectedSong
    {
        get => _selectedSong;
        set
        {
            if (!Set(ref _selectedSong, value)) return;

            RefreshDifficulties();
            OnPropertyChanged(nameof(HasSelectedSong));
            OnPropertyChanged(nameof(SelectedSongTitle));
            OnPropertyChanged(nameof(SelectedSongSubtitle));
            OnPropertyChanged(nameof(StorageHintText));
            OnPropertyChanged(nameof(StartBlockReason));
            OnPropertyChanged(nameof(StartBlockIsError));
        }
    }

    /// <summary>当前选中的难度。</summary>
    public MapsetDifficulty? SelectedDifficulty
    {
        get => _selectedDifficulty;
        set
        {
            if (!Set(ref _selectedDifficulty, value)) return;

            OnPropertyChanged(nameof(HasSelectedDifficulty));
            OnPropertyChanged(nameof(SelectedDifficultyText));
            OnPropertyChanged(nameof(CanStartPlay));
            OnPropertyChanged(nameof(StartBlockReason));
            OnPropertyChanged(nameof(StartBlockIsError));
        }
    }

    /// <summary>有没有选中谱面集。</summary>
    public bool HasSelectedSong => SelectedSong is not null;

    /// <summary>有没有选中难度。</summary>
    public bool HasSelectedDifficulty => SelectedDifficulty is not null;

    /// <summary>选中谱面集的标题行。</summary>
    public string SelectedSongTitle => SelectedSong?.TitleLine ?? "";

    /// <summary>选中谱面集的副标题（难度数 + 键数）。</summary>
    public string SelectedSongSubtitle => SelectedSong?.SubtitleLine ?? "";

    /// <summary>选中难度的展示文本。</summary>
    public string SelectedDifficultyText => SelectedDifficulty?.DisplayLine ?? "先选一首歌";

    /// <summary>封面图（谱面集背景图；没有就是 null）。</summary>
    public ImageSource? CoverImage
    {
        get => _coverImage;
        private set
        {
            if (!Set(ref _coverImage, value)) return;
            OnPropertyChanged(nameof(HasCover));
        }
    }

    /// <summary>有没有封面图。</summary>
    public bool HasCover => CoverImage is not null;

    /// <summary>封面区提示文字（没有封面时显示）。</summary>
    public string CoverHint
    {
        get => _coverHint;
        private set => Set(ref _coverHint, value);
    }

    /// <summary>谱面集里没有任何歌时的空状态提示。</summary>
    public bool IsLibraryEmpty => Songs.Count == 0;

    /// <summary>导入 / 刷新相关的一行状态。</summary>
    public string ImportStatus
    {
        get => _importStatus;
        private set
        {
            if (!Set(ref _importStatus, value)) return;
            OnPropertyChanged(nameof(HasImportStatus));
        }
    }

    /// <summary>有没有导入状态要显示。</summary>
    public bool HasImportStatus => ImportStatus.Length > 0;

    /// <summary>正在导入（禁用按钮，避免连点）。</summary>
    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (!Set(ref _isImporting, value)) return;
            OnPropertyChanged(nameof(CanStartPlay));
        }
    }

    /// <summary>数据目录的一行说明（「谱面集放在哪」）。</summary>
    public string StorageHintText => $"谱面集放在 {_library.MapsetRoot}";

    /// <summary>能不能开始游玩。</summary>
    public bool CanStartPlay => !IsImporting && SelectedSong is not null && SelectedDifficulty is not null;

    /// <summary>不能开始的原因（能开始为空串）。</summary>
    public string StartBlockReason
    {
        get
        {
            if (SelectedSong is null) return "先在左边选一首歌。";
            if (SelectedDifficulty is null) return "再选一个难度。";

            var chart = MapsetLibrary.LoadChart(SelectedSong, SelectedDifficulty);
            if (chart is null) return "这个谱面读不出来（文件可能被删了）。";
            return chart.CanPlay ? "" : chart.PlayBlockReason;
        }
    }

    /// <summary>
    /// 不能开始的原因算不算「错误」（标红用）。
    /// <para>「还没选歌 / 还没选难度」是正常引导，用次要文字色；缺音频 / 读不出来才算真问题。</para>
    /// </summary>
    public bool StartBlockIsError
    {
        get
        {
            if (SelectedSong is null || SelectedDifficulty is null) return false;
            var chart = MapsetLibrary.LoadChart(SelectedSong, SelectedDifficulty);
            return chart is null || !chart.CanPlay;
        }
    }

    private void RefreshDifficulties()
    {
        Difficulties.Clear();
        if (SelectedSong is not null)
        {
            foreach (var difficulty in SelectedSong.Index.Difficulties) Difficulties.Add(difficulty);
        }

        SelectedDifficulty = Difficulties.FirstOrDefault();

        // 封面：优先用谱面集里第一张真实存在的图
        var background = SelectedSong?.BackgroundPath ?? "";
        CoverImage = string.IsNullOrEmpty(background) ? null : TryLoadImage(background);
        CoverHint = SelectedSong is null
            ? "把 .osz 拖进这个窗口就能导入"
            : (CoverImage is null ? "这张谱面没有背景图" : "");
    }

    private static ImageSource? TryLoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;   // 读完就放开文件（否则删歌会失败）
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 720;                    // 缩略图没必要原尺寸解码（省内存）
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 库操作

    /// <summary>重新扫描谱面集（保留当前选中，选中项没了就选第一首）。</summary>
    public void RefreshLibrary(bool silent = false)
    {
        var keepFolder = SelectedSong?.FolderPath;

        IReadOnlyList<MapsetEntry> scanned;
        try
        {
            scanned = _library.Scan();
        }
        catch (Exception ex)
        {
            StatusText = $"扫描谱面集失败：{ex.Message}";
            return;
        }

        Songs.Clear();
        foreach (var entry in scanned) Songs.Add(entry);

        OnPropertyChanged(nameof(IsLibraryEmpty));

        SelectedSong = Songs.FirstOrDefault(s =>
                           string.Equals(s.FolderPath, keepFolder, StringComparison.OrdinalIgnoreCase))
                       ?? Songs.FirstOrDefault();

        if (!silent)
        {
            StatusText = Songs.Count == 0
                ? "谱面集还是空的：把 .osz 文件拖进窗口就能导入。"
                : $"共 {Songs.Count} 张谱面集。";
        }
    }

    /// <summary>
    /// 导入若干 <c>.osz</c>（拖放 / 选文件都走这里）。后台跑，期间界面显示「导入中」。
    /// </summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> paths)
    {
        var oszFiles = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => Path.GetExtension(p).Equals(".osz", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (oszFiles.Count == 0)
        {
            ImportStatus = "只支持 .osz 文件（osu! 谱面集压缩包）。";
            return;
        }

        IsImporting = true;
        ImportStatus = $"正在导入 {oszFiles.Count} 个 .osz…";

        var lines = new List<string>();
        try
        {
            foreach (var file in oszFiles)
            {
                var result = await Task.Run(() => _library.Import(file));
                lines.Add(Path.GetFileName(file) + "：" + result.Describe());

                // 只把最后一次成功导入的谱面集选中（多个文件时选中最后一个）
                if (result.Success) _pendingSelectFolder = result.FolderPath;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"导入出错（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            IsImporting = false;
        }

        RefreshLibrary(silent: true);

        if (!string.IsNullOrEmpty(_pendingSelectFolder))
        {
            SelectedSong = Songs.FirstOrDefault(s =>
                               string.Equals(s.FolderPath, _pendingSelectFolder, StringComparison.OrdinalIgnoreCase))
                           ?? SelectedSong;
            _pendingSelectFolder = "";
        }

        ImportStatus = string.Join(Environment.NewLine, lines);
        StatusText = ImportStatus;
    }

    private string _pendingSelectFolder = "";

    /// <summary>删除当前选中的谱面集（界面负责二次确认）。</summary>
    public bool DeleteSelectedSong()
    {
        if (SelectedSong is null) return false;

        var name = SelectedSong.TitleLine;
        if (!_library.Delete(SelectedSong))
        {
            StatusText = $"删除「{name}」失败（文件可能被占用）。";
            return false;
        }

        SelectedSong = null;
        RefreshLibrary(silent: true);
        StatusText = $"已删除「{name}」。";
        ImportStatus = $"已删除「{name}」。";
        return true;
    }

    /// <summary>在资源管理器里打开谱面集根目录。</summary>
    public void OpenMapsetFolder() => _library.OpenRootInExplorer();

    /// <summary>在资源管理器里定位当前谱面集。</summary>
    public void RevealSelectedSong()
    {
        if (SelectedSong is not null) _library.RevealInExplorer(SelectedSong);
    }

    /// <summary>
    /// 截图 / 调试验收：<c>YLT_VSRG_PAGE=settings|play|pause|result</c> 时打开工具后直接切到那个页面
    /// （<c>play</c> / <c>pause</c> 会真的开始游玩当前选中的难度 —— 需要临时数据根里已经有谱面集）。
    /// 只认一次；正常使用不设这个环境变量。
    /// </summary>
    private bool _debugPageApplied;

    internal void ApplyDebugPage()
    {
        if (_debugPageApplied) return;

        _debugPageApplied = true;

        var page = Environment.GetEnvironmentVariable("YLT_VSRG_PAGE");
        if (string.IsNullOrWhiteSpace(page)) return;

        switch (page.Trim().ToLowerInvariant())
        {
            case "settings":
                Page = VsrgPage.Settings;
                break;
            case "result":
                Page = VsrgPage.Result;
                break;
            case "pause":
            case "paused":
                // 开始游玩后立刻暂停（截图验收暂停遮罩用）
                StartPlay();
                SetPaused(true);
                break;
            case "play":
            case "game":
                StartPlay();
                break;
        }
    }

    // ---------------------------------------------------------------- 生命周期

    /// <inheritdoc />
    protected override void OnClosed()
    {
        // 切走 / 关掉工具页：游玩必须停干净（音频设备、定时器、全屏）
        AbandonPlayback(exitFullscreen: true);
    }
}
