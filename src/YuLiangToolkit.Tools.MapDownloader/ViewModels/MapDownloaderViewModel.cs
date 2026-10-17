using System.Collections.ObjectModel;
using System.Diagnostics;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.MapDownloader.Core;
using YuLiangToolkit.Tools.MapDownloader.Engine;

namespace YuLiangToolkit.Tools.MapDownloader.ViewModels;

/// <summary>
/// 「osu! 下图器」的 ViewModel（原版那两个 Tab 的内容合到一页里）。
///
/// <para>分工与原版一致：<see cref="OsuApiClient"/> 只管说话（换 Token / 搜索），
/// <see cref="OszDownloader"/> 只管下文件，界面状态由这里串起来 ——
/// 筛选、命名、URL 这些纯逻辑都在 <c>Core/</c> 里，自检直接断言（见 AGENTS.md 2.13）。</para>
/// </summary>
public sealed partial class MapDownloaderViewModel : ToolViewModelBase
{
    private readonly OsuApiClient _api = new();

    /// <summary>下载器（自检把它指向本地假镜像，所以公开出来；界面只用它的 <c>DownloadAsync</c> 之外什么都不碰）。</summary>
    public OszDownloader Downloader { get; } = new();

    /// <summary>搜索 / 加载更多 / 获取全部 / 下载共用一个取消源（同一时刻只跑一件事）。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>翻页游标（原版的 <c>_cursorString</c>，空 = 没有下一页）。</summary>
    private string _cursor = "";

    public MapDownloaderViewModel() : base(MapDownloaderConfig.ToolId)
    {
        Cfg = LoadConfig<MapDownloaderConfig>();

        // ⚠ 顺序要紧：先 LoadConfig → 再填默认下载目录 → 最后才让下载器拿目录
        //（写成字段初始化器会拿到空目录，见 AGENTS.md 第 5 节的踩坑）。
        Cfg.DefaultDownloadFolder = Storage.PathOf("downloads");
        ApplyDownloadSettings();

        GetTokenCommand = new AsyncRelayCommand(_ => GetTokenAsync(announce: true), _ => !IsBusy);
        OpenOAuthPageCommand = new RelayCommand(_ => OpenUrl(OsuQuery.OAuthAppUrl));
        SearchCommand = new AsyncRelayCommand(_ => SearchAsync(loadMore: false), _ => CanSearch);
        LoadMoreCommand = new AsyncRelayCommand(_ => SearchAsync(loadMore: true), _ => CanLoadMore && !IsBusy);
        FetchAllCommand = new AsyncRelayCommand(_ => FetchAllAsync(), _ => CanSearch || IsFetchingAll);
        SelectAllCommand = new RelayCommand(_ => SetSelection(true), _ => Sets.Count > 0);
        ClearSelectionCommand = new RelayCommand(_ => SetSelection(false), _ => SelectedCount > 0);
        DownloadSelectedNoVideoCommand = new AsyncRelayCommand(_ => DownloadAsync(true, false), _ => CanDownloadSelected);
        DownloadSelectedFullCommand = new AsyncRelayCommand(_ => DownloadAsync(true, true), _ => CanDownloadSelected);
        DownloadAllNoVideoCommand = new AsyncRelayCommand(_ => DownloadAsync(false, false), _ => CanDownloadAll);
        DownloadAllFullCommand = new AsyncRelayCommand(_ => DownloadAsync(false, true), _ => CanDownloadAll);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => IsBusy);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder());
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        Log($"配置：{Cfg.FilePath}");
        Log($"下载目录：{Cfg.EffectiveDownloadFolder}");
        if (IsFirstRun) Log("首次运行：已生成默认配置，先去「API 凭据」把 osu! 的 Client ID / Secret 填上");

        // 原版是「Shown 里自动换 Token」。这里同样自动来一发，但自检 / 无头构造 VM 时用
        // YLT_NO_SERVER=1 关掉（跟 Flash 下载器、联机聊天室一个约定，免得测试时偷偷联网）。
        if (Cfg.AutoGetToken && Cfg.HasCredentials && !Cfg.HasValidToken
            && Environment.GetEnvironmentVariable("YLT_NO_SERVER") is null)
        {
            _ = GetTokenAsync(announce: false);
        }

        // 截图 / 看版式用：塞几条假结果，不发任何请求（见 SeedDemoRows）
        if (Environment.GetEnvironmentVariable("YLT_MAP_DEMO") is not null) SeedDemoRows();
    }

    /// <summary>
    /// 往列表里塞几条**假结果**（<c>YLT_MAP_DEMO=1</c> 时）：只想看看表格与按钮长什么样时用，
    /// 不发任何请求、也不影响真实搜索与下载（跟联机聊天室的 <c>YLT_CHAT_DEMO</c> 一个用途）。
    /// </summary>
    private void SeedDemoRows()
    {
        foreach (var set in new[]
                 {
                     DemoSet(3141592, "Freedom Dive", "xi", "Nakagawa-Kanon", "ranked", "2013-04-01T07:00:00+00:00", 6.5, 7.0),
                     DemoSet(292301, "Blue Zenith", "xi", "Asphyxia", "ranked", "2015-07-31T09:00:00+00:00", 4.0, 4.3),
                     DemoSet(688151, "Yomi Yori Kikoyu, Koukoku no Touka, Honoo no Kuni", "Imperial Circus Dead Decadence",
                         "Shiirn", "ranked", "2018-10-06T18:00:00+00:00", 5.2, 6.7),
                     DemoSet(2012823, "Kira Kira Days", "Hiroshi Shimizu", "Sotarks", "loved", "2020-01-15T12:00:00+00:00", 4.0),
                     DemoSet(1234567, "Galaxy Collapse", "Kurokotei", "demo-mapper", "graveyard", null, 7.0),
                 })
        {
            Sets.Add(new BeatmapSetItem(set));
        }

        // 再多塞几行：让列表出滚动条，顺便核对"表头末尾那一列滚动条占位"到底对不对
        for (var i = 1; i <= 7; i++)
        {
            Sets.Add(new BeatmapSetItem(DemoSet(
                900000 + i, $"演示谱面 {i}（填满列表用）", "demo artist", "demo-mapper", "qualified",
                $"2019-0{i}-01T00:00:00+00:00", 4.0 + i)));
        }

        RefreshSelection();
        UpdateResultSummary();
        ResultSummary += "（演示数据）";
        Log("演示数据：YLT_MAP_DEMO 打开了，列表里这几行是假的（不影响搜索与下载）");
    }

    private static BeatmapSet DemoSet(int id, string title, string artist, string creator, string status,
        string? rankedDate, params double[] keyCounts) => new()
    {
        Id = id,
        Title = title,
        Artist = artist,
        Creator = creator,
        Status = status,
        RankedDate = rankedDate,
        PlayCount = 1_234_567,
        FavouriteCount = 4321,
        Beatmaps = keyCounts
            .Select((cs, i) => new BeatmapInfo { Id = id * 10 + i, CS = cs, Version = $"demo {i + 1}", ModeInt = 0 })
            .ToList(),
    };

    // ---------- 配置与选项 ----------

    public MapDownloaderConfig Cfg { get; }

    public IReadOnlyList<OptionItem<string>> ModeOptions => OsuOptions.Modes;
    public IReadOnlyList<OptionItem<string>> StatusOptions => OsuOptions.Statuses;
    public IReadOnlyList<OptionItem<string>> SortOptions => OsuOptions.Sorts;
    public IReadOnlyList<OptionItem<string>> KeyCountOptions => OsuOptions.KeyCounts;

    public OptionItem<string> SelectedMode
    {
        get => OsuOptions.Pick(OsuOptions.Modes, Cfg.Mode);
        set
        {
            if (value is null) return;
            Cfg.Mode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(KeyCountEnabled));   // 只有 mania 才让选 Key 数
            if (!KeyCountEnabled && !string.IsNullOrEmpty(Cfg.KeyCount))
            {
                Cfg.KeyCount = "";
                OnPropertyChanged(nameof(SelectedKeyCount));
            }
        }
    }

    public OptionItem<string> SelectedStatus
    {
        get => OsuOptions.Pick(OsuOptions.Statuses, Cfg.Status);
        set { if (value is null) return; Cfg.Status = value.Value; OnPropertyChanged(); }
    }

    public OptionItem<string> SelectedSort
    {
        get => OsuOptions.Pick(OsuOptions.Sorts, Cfg.Sort);
        set { if (value is null) return; Cfg.Sort = value.Value; OnPropertyChanged(); }
    }

    public OptionItem<string> SelectedKeyCount
    {
        get => OsuOptions.Pick(OsuOptions.KeyCounts, Cfg.KeyCount);
        set { if (value is null) return; Cfg.KeyCount = value.Value; OnPropertyChanged(); }
    }

    /// <summary>Key 数只在 mania 下有意义（原版也是这么灰掉的）。</summary>
    public bool KeyCountEnabled => OsuOptions.KeyCountApplies(Cfg.Mode);

    /// <summary>日期 / Key 数是**本地筛选**（osu! 搜索接口不认这两个条件），写清楚免得误会。</summary>
    public string LocalFilterHint => "日期与 Key 数是本地筛选（接口不支持），留空 = 不限";

    // ---------- 结果列表 ----------

    public ObservableCollection<BeatmapSetItem> Sets { get; } = new();

    public bool HasResults => Sets.Count > 0;

    public int SelectedCount => Sets.Count(s => s.IsSelected);

    public string SelectionText => $"已选 {SelectedCount} / 共 {Sets.Count}";

    public bool CanDownloadSelected => SelectedCount > 0 && !IsBusy;
    public bool CanDownloadAll => Sets.Count > 0 && !IsBusy;

    private string _resultSummary = "还没有结果";
    /// <summary>「已显示 30 条（共 1234 条）」这种统计。</summary>
    public string ResultSummary
    {
        get => _resultSummary;
        private set => Set(ref _resultSummary, value);
    }

    private int _totalAvailable;
    /// <summary>接口说一共有多少条（用于统计文案）。</summary>
    public int TotalAvailable
    {
        get => _totalAvailable;
        private set => Set(ref _totalAvailable, value);
    }

    // ---------- 认证状态 ----------

    public bool IsAuthenticated => Cfg.HasValidToken;

    public string TokenStatusText
    {
        get
        {
            if (!Cfg.HasCredentials) return "✗ 先填 Client ID / Client Secret";
            if (!Cfg.HasValidToken) return "○ 还没换 Token（点「获取 Token」）";
            var left = Cfg.TokenExpiresAtUtc - DateTime.UtcNow;
            var local = Cfg.TokenExpiresAtUtc.ToLocalTime();
            return $"✓ Token 有效，{local:yyyy-MM-dd HH:mm} 到期（还有 {left.TotalHours:F1} 小时）";
        }
    }

    public bool TokenIsValid => Cfg.HasValidToken;

    // ---------- 下载 ----------

    /// <summary>界面上的「保存到」（直接读配置，改了配置推一次通知即可，不留第二份状态）。</summary>
    public string FolderText => Cfg.EffectiveDownloadFolder;

    private string _downloadSummary = "还没开始下载";
    public string DownloadSummaryText
    {
        get => _downloadSummary;
        private set => Set(ref _downloadSummary, value);
    }

    private bool _isFetchingAll;
    /// <summary>正在「获取全部」翻页（此时那个按钮变成「停止获取」）。</summary>
    public bool IsFetchingAll
    {
        get => _isFetchingAll;
        private set
        {
            if (Set(ref _isFetchingAll, value)) OnPropertyChanged(nameof(FetchAllText));
        }
    }

    public string FetchAllText => IsFetchingAll ? "停止获取" : "获取全部";

    private bool _progressVisible;
    public bool ProgressVisible
    {
        get => _progressVisible;
        private set => Set(ref _progressVisible, value);
    }

    // ---------- 命令 ----------

    public AsyncRelayCommand GetTokenCommand { get; }
    public RelayCommand OpenOAuthPageCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand LoadMoreCommand { get; }
    public AsyncRelayCommand FetchAllCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand DownloadSelectedNoVideoCommand { get; }
    public AsyncRelayCommand DownloadSelectedFullCommand { get; }
    public AsyncRelayCommand DownloadAllNoVideoCommand { get; }
    public AsyncRelayCommand DownloadAllFullCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ChooseFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    private bool CanSearch => IsAuthenticated && !IsBusy;
    private bool CanLoadMore => CanSearch && _cursor.Length > 0;

    // ---------- 界面小动作 ----------

    private void SetSelection(bool selected)
    {
        foreach (var item in Sets) item.IsSelected = selected;
        RefreshSelection();
    }

    /// <summary>勾选状态变了就刷新"已选 N / 共 M"和那几个下载按钮的可用性。</summary>
    internal void RefreshSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanDownloadSelected));
        OnPropertyChanged(nameof(CanDownloadAll));
        // 命令的 CanExecute 挂在 CommandManager.RequerySuggested 上，这里推一把让它立刻重算
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>把配置里的下载目录 / 并发数推给下载器（改配置后要重新调一次）。</summary>
    private void ApplyDownloadSettings()
    {
        Downloader.DownloadFolder = Cfg.EffectiveDownloadFolder;
        Downloader.Concurrency = Math.Max(1, Cfg.ParallelDownloads);
        OnPropertyChanged(nameof(FolderText));
    }

    private void ChooseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 .osz 保存到哪个文件夹",
            InitialDirectory = Directory.Exists(Cfg.EffectiveDownloadFolder) ? Cfg.EffectiveDownloadFolder : Storage.Directory,
        };

        if (dialog.ShowDialog() != true) return;

        Cfg.DownloadFolder = dialog.FolderName;
        ApplyDownloadSettings();
        Log($"下载目录改成：{Cfg.EffectiveDownloadFolder}");
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(Cfg.EffectiveDownloadFolder);
            Process.Start(new ProcessStartInfo
            {
                FileName = Cfg.EffectiveDownloadFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            StatusText = "打不开文件夹：" + ex.Message;
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打不开浏览器：" + ex.Message;
        }
    }

    /// <summary>取消当前那件事（搜索 / 获取全部 / 下载）。</summary>
    private void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;
        _cts.Cancel();
        StatusText = "正在取消…";
        Log("已请求取消");
    }

    protected override void OnClosed()
    {
        Cancel();
        _api.Dispose();
    }
}
