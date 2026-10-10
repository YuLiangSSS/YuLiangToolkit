using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Tools.FlashDownloader.Core;
using YuLiangToolkit.Tools.FlashDownloader.Engine;
using YuLiangToolkit.Tools.FlashDownloader.Web;

namespace YuLiangToolkit.Tools.FlashDownloader.ViewModels;

/// <summary>界面按钮统一返回的小结果。</summary>
/// <param name="Ok">成功没有。</param>
/// <param name="Message">提示 / 失败原因。</param>
public readonly record struct UiResult(bool Ok, string Message)
{
    public static UiResult Success(string message) => new(true, message);
    public static UiResult Fail(string message) => new(false, message);
}

/// <summary>
/// 「Flash 下载器」的 ViewModel：把站点解析、下载引擎、本地服务器、游戏列表串成一个界面。
///
/// <para>对应原始 Python 项目的 <c>Application</c>（主窗口）+ <c>RelatedDownloader</c>（关联文件下载器）
/// + <c>ToolDownloader</c>（自由下载工具）+ <c>EditNetPathDialog</c>（修改信息）
/// + <c>FlashRunner</c>（游玩），只是把四个弹窗做成了同一页里的可折叠面板。</para>
/// </summary>
public sealed partial class FlashDownloaderViewModel : ToolViewModelBase
{
    /// <summary>游戏文件默认放在功能数据目录的 games 子目录里。</summary>
    public const string GamesSubFolder = "games";

    /// <summary>工具目录（播放页 / 替代 API / Flash 播放器都在这）。</summary>
    public const string ToolsSubFolder = "tools";

    public const string ToolIdValue = "flash-downloader";

    private readonly FlashHttp _http;
    private readonly GameDownloader _downloader;
    private readonly RelatedFileDownloader _related;
    private readonly FreeFileDownloader _free;

    private CancellationTokenSource? _cts;
    private GameItem? _selectedGame;
    private string _gameSearchText = "";
    /// <summary>扫出来的全部游戏（未过滤）；<see cref="Games"/> 是它过滤+排序后的视图。</summary>
    private List<GameItem> _allGames = new();
    private string _gamesDirectory = "";
    private string _serverStatus = "未启动";
    private string _flashPlayerPath = "";
    private string _batchInput = "";
    private string _batchStatusText = "请输入要下载的文件（一行一个相对路径）";
    private bool _autoDownloadEnabled;
    private string _autoDownloadText = "";
    private string _autoDownloadStatus = "";
    private string _freeUrl = "";
    private string _freeReferer = "";
    private string _freeName = "";
    private string _freeStatus = "填好网址、上一级网址与文件名后点「下载」";
    private string _infoText = "";

    public FlashDownloaderViewModel() : base(FlashDownloaderConfig.ToolId)
    {
        // 游戏目录默认落在 Data\flash-downloader\games\，用户可以在界面上改成任意目录。
        // ⚠ 顺序要紧：先 LoadConfig → 填 DefaultGamesDirectory → 再算 GamesDirectory → 最后才建 Library。
        // （曾经把 Library 写成字段初始化器，它在构造函数体之前执行，于是拿到空目录，
        // 首次运行的说明文件被写到了仓库根目录——自检/截图时才发现。）
        Cfg = LoadConfig<FlashDownloaderConfig>();
        Cfg.DefaultGamesDirectory = Storage.PathOf(GamesSubFolder);
        GamesDirectory = Cfg.EffectiveGamesDirectory;

        ToolsDirectory = Storage.PathOf(ToolsSubFolder);
        Library = new GamesLibrary(GamesDirectory);
        _http = new FlashHttp(Cfg.ConnectTimeoutSeconds, Cfg.ReadTimeoutSeconds);
        _downloader = new GameDownloader(_http, Cfg);
        _related = new RelatedFileDownloader(_http);
        _free = new FreeFileDownloader(_http);
        Server = new FlashWebServer(Library, ToolsDirectory, Log);
        Server.FileFetcher = FetchForAutoDownloadAsync;

        // ------- 命令 -------
        DownloadCommand = new AsyncRelayCommand(_ => DownloadAsync(false));
        DownloadAndPlayCommand = new AsyncRelayCommand(_ => DownloadAsync(true));
        PlayCommand = new RelayCommand(_ => PlaySelected(), _ => SelectedGame is not null);
        RefreshListCommand = new RelayCommand(_ => { RefreshGames(true); });
        ClearGameSearchCommand = new RelayCommand(_ => GameSearchText = "", _ => HasGameSearchText);
        ToggleSortDirectionCommand = new RelayCommand(_ => ToggleSortDirection());
        DeleteCommand = new RelayCommand(_ => DeleteSelected(), _ => SelectedGame is not null);
        DeleteAllCommand = new RelayCommand(_ => DeleteAll());
        MoveIntoFolderCommand = new AsyncRelayCommand(_ => MoveIntoFolderAsync(), _ => SelectedGame?.Entry.CanMoveIntoFolder == true);
        ConvertToHtmCommand = new AsyncRelayCommand(_ => ConvertToHtmAsync(), _ => SelectedGame?.Entry.CanConvertToHtm == true);
        RevealCommand = new RelayCommand(_ => RevealSelected(), _ => SelectedGame is not null);
        OpenGamesFolderCommand = new RelayCommand(_ => OpenGamesFolder());
        OpenToolsFolderCommand = new RelayCommand(_ => Storage.OpenInExplorer());
        ChooseGamesFolderCommand = new RelayCommand(_ => ChooseGamesFolder());
        UseDefaultFolderCommand = new RelayCommand(_ => UseDefaultFolder());
        OpenConfigFolderCommand = new RelayCommand(_ => ConfigStore.OpenInExplorer());
        OpenSiteCommand = new RelayCommand(p => OpenUrl(p as string));

        StartServerCommand = new RelayCommand(_ => StartServer());
        StopServerCommand = new RelayCommand(_ => StopServer(), _ => Server.IsRunning);
        OpenServerHomeCommand = new RelayCommand(_ => OpenUrl(Server.BaseUrl), _ => Server.IsRunning);
        CopyServerUrlCommand = new RelayCommand(_ => CopyServerUrl(), _ => Server.IsRunning);
        LocateFlashPlayerCommand = new RelayCommand(_ => LocateFlashPlayer(true));
        ChooseFlashPlayerCommand = new RelayCommand(_ => ChooseFlashPlayer());
        ClearLogCommand = new RelayCommand(_ => ClearLog());

        BatchDownloadCommand = new AsyncRelayCommand(_ => RunRelatedDownloadAsync());
        CancelBatchCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsBatchRunning);
        ClearBatchInputCommand = new RelayCommand(_ => { BatchInput = ""; RelatedRows.Clear(); UpdateBatchStatus("请输入要下载的文件（一行一个相对路径）"); });
        ToggleAutoDownloadCommand = new RelayCommand(_ => ToggleAutoDownload());
        ClearAutoDownloadLogCommand = new RelayCommand(_ => { AutoDownloadText = ""; Server.Session?.ClearLog(); });

        FreeDownloadCommand = new AsyncRelayCommand(_ => RunFreeDownloadAsync());
        OpenGamePageCommand = new RelayCommand(_ => OpenSelectedGamePage(), _ => SelectedGame is not null);

        IsBatchRunning = false;

        // 配置里的路径 / 并发数变了要跟着动
        Cfg.PropertyChanged += (_, _) => Ui(OnConfigChanged);

        // ------- 首次运行准备 -------
        var templates = FlashWebAssets.EnsureTemplates(ToolsDirectory);
        if (IsFirstRun || templates > 0) Log($"已准备播放页与替代 API：{ToolsDirectory}");
        LocateFlashPlayer(false);

        RefreshGames(false);
        UpdateServerVisibility();
        SeedReadmeIfFirstRun();
        FreeUrl = Cfg.LastFreeUrl;
        FreeReferer = Cfg.LastFreeReferer;
        FreeName = Cfg.LastFreeName;
        if (Cfg.LastGameUrl.Length > 0) GameUrl = Cfg.LastGameUrl;

        // YLT_NO_SERVER=1 时不自动起服务器（自检 / 无头构造用，免得跟正在跑的实例抢端口）
        if (Cfg.AutoStartServer && Environment.GetEnvironmentVariable("YLT_NO_SERVER") is null) StartServer();
        StartFolderWatcher();

        Log($"游戏目录：{GamesDirectory}");
        Log($"配置文件：{Cfg.FilePath}");
        Log("支持 4399 / 7k7k / 17yy / ArmorGames / Kongregate。粘贴游戏页网址 → 「下载并游玩」；多文件游戏请用「关联文件下载器」。");
    }

    // ---------------------------------------------------------------- 核心对象

    /// <summary>功能选项（改一下就写进 <c>Config\flash-downloader\config.json</c>）。</summary>
    public FlashDownloaderConfig Cfg { get; }

    /// <summary>游戏目录门面（扫列表 / 删除 / 装入文件夹）。改目录时会整个换掉。</summary>
    public GamesLibrary Library { get; private set; }

    /// <summary>本地 HTTP 服务器（网页游玩 + 自动下载所需文件）。</summary>
    public FlashWebServer Server { get; }

    /// <summary>播放页 / 替代 API / FlashPlayer.exe 所在目录。</summary>
    public string ToolsDirectory { get; }

    /// <summary>游戏目录（绑定界面显示）。</summary>
    public string GamesDirectory
    {
        get => _gamesDirectory;
        private set
        {
            if (!Set(ref _gamesDirectory, value)) return;
            Cfg.DefaultGamesDirectory = Path.Combine(Storage.PathOf(GamesSubFolder));
            OnPropertyChanged(nameof(GamesDirectorySourceText));
        }
    }

    /// <summary>当前游戏目录是自定义的还是默认的。</summary>
    public string GamesDirectorySourceText => Cfg.HasCustomFolder ? "自定义目录" : "默认目录（Data 里）";

    // ---------------------------------------------------------------- 界面数据

    /// <summary>游戏列表（已经过搜索过滤 + 排序，直接绑 ListBox）。</summary>
    public ObservableCollection<GameItem> Games { get; } = new();

    /// <summary>搜索词：输入即过滤（关键词用空格分隔，需全部命中）。**不持久化**。</summary>
    public string GameSearchText
    {
        get => _gameSearchText;
        set
        {
            if (!Set(ref _gameSearchText, value)) return;
            OnPropertyChanged(nameof(HasGameSearchText));
            RebuildGameList();
        }
    }

    /// <summary>搜索框里有没有内容（控制「✕」按钮显不显示）。</summary>
    public bool HasGameSearchText => !string.IsNullOrWhiteSpace(GameSearchText);

    /// <summary>排序方式下拉框的选项。</summary>
    public IReadOnlyList<OptionItem<GameSortMode>> GameSortOptions { get; } = OptionItem.From(
        (GameSortMode.LastPlayed, "按上次游玩"),
        (GameSortMode.Name, "按名字"),
        (GameSortMode.Size, "按大小"),
        (GameSortMode.Site, "按站点"),
        (GameSortMode.Default, "按默认顺序"));

    /// <summary>下拉框与配置之间的桥接属性。</summary>
    public OptionItem<GameSortMode> SelectedGameSort
    {
        get => OptionItem<GameSortMode>.Find(GameSortOptions, Cfg.GameSortMode) ?? GameSortOptions[0];
        set
        {
            if (value is null) return;
            Cfg.GameSortMode = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SortDirectionText));
            OnPropertyChanged(nameof(SortDirectionToolTip));
            RebuildGameList();
        }
    }
    /// <summary>正序 / 逆序切换按钮上的文字。</summary>
    public string SortDirectionText => Cfg.GameSortDescending ? "↓ 逆序" : "↑ 正序";

    /// <summary>正序 / 逆序按钮的提示。</summary>
    public string SortDirectionToolTip => Cfg.GameSortDescending
        ? "当前：逆序（点一下换成正序）"
        : "当前：正序（点一下换成逆序）";

    private string _gamesEmptyHint = "";
    /// <summary>列表被搜索筛过之后的提示（空 = 不用提示）。</summary>
    public string GamesEmptyHint
    {
        get => _gamesEmptyHint;
        private set => Set(ref _gamesEmptyHint, value);
    }

    /// <summary>关联文件下载的逐行结果。</summary>
    public ObservableCollection<RelatedRow> RelatedRows { get; } = new();

    /// <summary>网址输入框。</summary>
    public string GameUrl { get; set; } = "";

    /// <summary>当前选中的游戏。</summary>
    public GameItem? SelectedGame
    {
        get => _selectedGame;
        set
        {
            if (!Set(ref _selectedGame, value)) return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedSummary));
            UpdateRelatedPanel();
        }
    }

    public bool HasSelection => SelectedGame is not null;

    /// <summary>选中游戏的详细信息（「修改信息」面板的只读展示）。</summary>
    public string SelectedSummary
    {
        get
        {
            var item = SelectedGame;
            if (item is null) return "未选中游戏";
            var e = item.Entry;
            var lines = new List<string>
            {
                $"名称：{e.FileOrFolderName}",
                $"类型：{e.TypeText}（{e.OpenModeText}）",
                $"站点：{e.SiteText}    后缀：{e.AppendixText}    大小：{e.SizeText}",
            };
            if (e.IsFolder)
            {
                var ini = SwfNetPathIni.FromFile(e.IniFullPath, e.FolderExt);
                lines.Add($"网络路径：{(ini.NetPath.Length > 0 ? ini.NetPath : "（空）")}");
                lines.Add($"相关信息：{ini.Describe()}");
                if (ini.HasProblem) lines.Add($"⚠ {ini.ErrorText}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>列表统计文本。</summary>
    /// <summary>
    /// 列表统计文本：搜索时显示「筛出 N / 共 M 个」，否则只显示总数；顺带提醒旧版命名。
    /// </summary>
    public string GamesSummary
    {
        get
        {
            var total = _allGames.Count;
            var shown = Games.Count;
            var text = HasGameSearchText && shown != total ? $"筛出 {shown} / 共 {total} 个游戏" : $"共 {total} 个游戏";
            var legacy = _allGames.Count(g => g.Entry.IsLegacyFolderName);
            if (legacy > 0) text += $"（其中 {legacy} 个是旧版命名，请在名字后面加 s 或 h）";
            return text;
        }
    }

    /// <summary>「找到的 SWF 路径」那一行（状态栏 1）。</summary>
    public string InfoText
    {
        get => _infoText;
        private set => Set(ref _infoText, value);
    }

    /// <summary>本地服务器状态文本（状态栏 2）。</summary>
    public string ServerStatus
    {
        get => _serverStatus;
        private set => Set(ref _serverStatus, value);
    }

    /// <summary>Flash 播放器路径（空 = 没找到）。</summary>
    public string FlashPlayerPath
    {
        get => _flashPlayerPath;
        private set
        {
            if (!Set(ref _flashPlayerPath, value)) return;
            OnPropertyChanged(nameof(HasFlashPlayer));
            OnPropertyChanged(nameof(FlashPlayerText));
        }
    }

    public bool HasFlashPlayer => FlashPlayerPath.Length > 0 && File.Exists(FlashPlayerPath);

    public string FlashPlayerText => HasFlashPlayer ? FlashPlayerPath : "未找到 FlashPlayer.exe（点「寻找」或「指定…」）";

    // ---------------------------------------------------------------- 关联文件下载器

    public string BatchInput
    {
        get => _batchInput;
        set => Set(ref _batchInput, value);
    }

    public string BatchStatusText
    {
        get => _batchStatusText;
        private set => Set(ref _batchStatusText, value);
    }

    public bool IsBatchRunning { get; private set; }

    // ---------------------------------------------------------------- 自动下载

    public bool AutoDownloadEnabled
    {
        get => _autoDownloadEnabled;
        private set
        {
            if (!Set(ref _autoDownloadEnabled, value)) return;
            OnPropertyChanged(nameof(AutoDownloadButtonText));
        }
    }

    public string AutoDownloadButtonText => AutoDownloadEnabled ? "停止自动下载" : "自动下载所需文件";

    public string AutoDownloadText
    {
        get => _autoDownloadText;
        private set => Set(ref _autoDownloadText, value);
    }

    public string AutoDownloadStatus
    {
        get => _autoDownloadStatus;
        private set => Set(ref _autoDownloadStatus, value);
    }

    // ---------------------------------------------------------------- 自由下载工具

    public string FreeUrl
    {
        get => _freeUrl;
        set => Set(ref _freeUrl, value);
    }

    public string FreeReferer
    {
        get => _freeReferer;
        set => Set(ref _freeReferer, value);
    }

    public string FreeName
    {
        get => _freeName;
        set => Set(ref _freeName, value);
    }

    public string FreeStatus
    {
        get => _freeStatus;
        private set => Set(ref _freeStatus, value);
    }

    // ---------------------------------------------------------------- 命令

    public AsyncRelayCommand DownloadCommand { get; }
    public AsyncRelayCommand DownloadAndPlayCommand { get; }
    public RelayCommand PlayCommand { get; }
    public RelayCommand RefreshListCommand { get; }
    /// <summary>清空游戏列表搜索词。</summary>
    public RelayCommand ClearGameSearchCommand { get; }
    /// <summary>正序 / 逆序切换。</summary>
    public RelayCommand ToggleSortDirectionCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand DeleteAllCommand { get; }
    public AsyncRelayCommand MoveIntoFolderCommand { get; }
    public AsyncRelayCommand ConvertToHtmCommand { get; }
    public RelayCommand RevealCommand { get; }
    public RelayCommand OpenGamesFolderCommand { get; }
    public RelayCommand OpenToolsFolderCommand { get; }
    public RelayCommand ChooseGamesFolderCommand { get; }
    public RelayCommand UseDefaultFolderCommand { get; }
    public RelayCommand OpenConfigFolderCommand { get; }
    public RelayCommand OpenSiteCommand { get; }

    public RelayCommand StartServerCommand { get; }
    public RelayCommand StopServerCommand { get; }
    public RelayCommand OpenServerHomeCommand { get; }
    public RelayCommand CopyServerUrlCommand { get; }
    public RelayCommand LocateFlashPlayerCommand { get; }
    public RelayCommand ChooseFlashPlayerCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand BatchDownloadCommand { get; }
    public RelayCommand CancelBatchCommand { get; }
    public RelayCommand ClearBatchInputCommand { get; }
    public RelayCommand ToggleAutoDownloadCommand { get; }
    public RelayCommand ClearAutoDownloadLogCommand { get; }

    public AsyncRelayCommand FreeDownloadCommand { get; }

    /// <summary>打开选中游戏的原始游戏页（浏览器）。</summary>
    public RelayCommand OpenGamePageCommand { get; }

    /// <summary>需要用户二次确认时的出口（自检里替换成“总是同意”，就能无头跑完整流程）。</summary>
    public Func<string, string, bool> Confirm { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
}
