using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Browser.ViewModels;

/// <summary>
/// 「网页浏览器」的 ViewModel：<b>多标签页</b>版——地址栏文本、按钮状态、状态提示、配置、
/// 标签列表（<see cref="Tabs"/> / <see cref="ActiveTab"/>）、标签栏位置、历史记录、下载、页面缩放、
/// 主页设置与「启动时恢复上次标签页」开关。
/// 真正的网页内核由 <see cref="IBrowserHost"/>（= <c>BrowserView.xaml.cs</c>）负责：
/// 内核操作都作用于<b>激活标签</b>，每个标签一个 WebView2 实例（共享同一内核环境，Cookie / 登录态互通）。
/// </summary>
public sealed class BrowserViewModel : ToolViewModelBase
{
    /// <summary>历史记录落盘防抖（连续跳多个页面只写一次盘）。</summary>
    private static readonly TimeSpan HistorySaveDelay = TimeSpan.FromMilliseconds(1500);

    private enum SidePanelMode
    {
        History,
        Downloads,
        Bookmarks,
        Resources,
    }

    private readonly BrowserHistoryStore _historyStore;
    private readonly BrowserDownloadStore _downloadStore;
    private readonly BrowserBookmarkStore _bookmarkStore;
    private readonly DispatcherTimer _historySaveTimer;
    private List<BrowserDownloadRecord> _downloadRecords;

    private string _addressText = "";
    private bool _isLoading;
    private string _pageTitle = "";
    private BrowserTab? _activeTab;
    private int _nextTabId;
    private double _currentZoom = 1.0;
    private bool _isSidePanelOpen;
    private SidePanelMode _sidePanelMode = SidePanelMode.History;
    private bool _restoringSession;
    private string _homePageDraft = "";
    private string _homePageHint = "";

    public BrowserViewModel() : base(BrowserConfig.ToolId)
    {
        Cfg = LoadConfig<BrowserConfig>();
        BrowserConfig.MigrateLegacyDefaults(Cfg);
        Cfg.TabBarPosition = BrowserTabBar.NormalizePosition(Cfg.TabBarPosition);
        _homePageDraft = Cfg.HomePage;

        _historyStore = new BrowserHistoryStore(Storage);
        _downloadStore = new BrowserDownloadStore(Storage);
        _bookmarkStore = new BrowserBookmarkStore(Storage);
        _downloadRecords = _downloadStore.Load();

        History = new ObservableCollection<BrowserHistoryEntry>(_historyStore.Load());
        Downloads = new ObservableCollection<BrowserDownloadItem>(
            _downloadRecords.Select(record => new BrowserDownloadItem(record)));
        Bookmarks = new ObservableCollection<BrowserBookmark>(_bookmarkStore.Load());
        Bookmarks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
            RefreshCommandState();
        };

        _historySaveTimer = new DispatcherTimer { Interval = HistorySaveDelay };
        _historySaveTimer.Tick += (_, _) =>
        {
            _historySaveTimer.Stop();
            SaveHistoryNow();
        };

        Tabs = new ObservableCollection<BrowserTab>();
        Tabs.CollectionChanged += OnTabsCollectionChanged;
        RestoreSession();

        GoCommand = new RelayCommand(_ => NavigateFromAddress());
        BackCommand = new RelayCommand(_ => Host?.GoBack(), _ => Host is { CanGoBack: true });
        ForwardCommand = new RelayCommand(_ => Host?.GoForward(), _ => Host is { CanGoForward: true });
        RefreshCommand = new RelayCommand(_ => Host?.Reload(), _ => Host is { IsReady: true });
        HomeCommand = new RelayCommand(_ => NavigateTo(HomePageUrl), _ => Host is { IsReady: true });
        NewTabCommand = new RelayCommand(_ => OpenTab(HomePageUrl));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as BrowserTab ?? ActiveTab));
        ToggleTabBarSideCommand = new RelayCommand(_ => ToggleTabBarSide());

        // 页面缩放：按钮 / Ctrl+滚轮（原生）共用同一套状态
        ZoomInCommand = new RelayCommand(_ => ApplyZoom(BrowserZoom.In(CurrentZoom)), _ => Host is { IsReady: true });
        ZoomOutCommand = new RelayCommand(_ => ApplyZoom(BrowserZoom.Out(CurrentZoom)), _ => Host is { IsReady: true });
        ZoomResetCommand = new RelayCommand(_ => ApplyZoom(1.0), _ => Host is { IsReady: true });

        // 侧栏：历史记录 / 下载 / 书签
        ShowHistoryCommand = new RelayCommand(_ => ShowHistoryPanel());
        ShowDownloadsCommand = new RelayCommand(_ => ShowDownloadsPanel());
        ShowBookmarksCommand = new RelayCommand(_ => ShowBookmarksPanel());
        ShowResourcesCommand = new RelayCommand(_ => ShowResourcesPanel());
        CloseSidePanelCommand = new RelayCommand(_ => CloseSidePanel());
        ClearHistoryCommand = new RelayCommand(_ => ClearHistory(), _ => History.Count > 0);

        // 书签：工具条星标 + 侧栏列表
        ToggleBookmarkCommand = new RelayCommand(_ => ToggleBookmark(), _ => ActiveTab is { Url.Length: > 0 });
        OpenBookmarkCommand = new RelayCommand(p => OpenBookmark(p as BrowserBookmark));
        OpenBookmarkInNewTabCommand = new RelayCommand(p => OpenBookmarkInNewTab(p as BrowserBookmark));
        RemoveBookmarkCommand = new RelayCommand(p => RemoveBookmark(p as BrowserBookmark));

        // 资源嗅探：网络嗅探由 View 自动做，这里负责面板 / 重新爬 / 打开 / 保存
        RefreshResourcesCommand = new AsyncRelayCommand(_ => RefreshResourcesAsync());
        OpenResourceCommand = new RelayCommand(p => OpenResource(p as BrowserResource));
        CopyResourceUrlCommand = new RelayCommand(p => CopyResourceUrl(p as BrowserResource));
        DownloadResourceCommand = new AsyncRelayCommand(p => DownloadResourceAsync(p as BrowserResource));

        // 下载项上的操作（列表按钮用 CommandParameter 把 item 传进来）
        OpenDownloadFileCommand = new RelayCommand(p => OpenDownloadFile(p as BrowserDownloadItem));
        OpenDownloadFolderCommand = new RelayCommand(p => OpenDownloadFolder(p as BrowserDownloadItem));
        CancelDownloadCommand = new RelayCommand(p => CancelDownload(p as BrowserDownloadItem));

        // 主页设置
        SaveHomePageCommand = new RelayCommand(_ => SaveHomePage());
        ResetHomePageCommand = new RelayCommand(_ => ResetHomePage());
    }

    /// <summary>配置（<c>Config\browser\config.json</c>）。</summary>
    public BrowserConfig Cfg { get; }

    /// <summary>
    /// 当前承载网页内核的视图（没启动 / 被切走时为 <c>null</c>）。
    /// 内核操作（前进 / 后退 / 刷新 / 缩放…）都由它路由到<b>激活标签</b>。
    /// </summary>
    internal IBrowserHost? Host { get; set; }

    // ---------- 标签页 ----------

    /// <summary>打开着的所有标签（顺序即标签条顺序；关到 0 个时会立刻补一个主页标签）。</summary>
    public ObservableCollection<BrowserTab> Tabs { get; }

    /// <summary>激活标签（工具条 / 网页区域显示的就是它；切走再切回时内核按需重建）。</summary>
    public BrowserTab? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (!Set(ref _activeTab, value)) return;
            SyncToolbarFromActiveTab();
            SaveSession();
        }
    }

    /// <summary>标签栏在左侧（竖排一列）。</summary>
    public bool IsTabBarLeft
        => string.Equals(Cfg.TabBarPosition, BrowserTabBar.LeftPosition, StringComparison.Ordinal);

    /// <summary>标签栏在上方（工具条上面一行，默认）。</summary>
    public bool IsTabBarTop => !IsTabBarLeft;

    /// <summary>
    /// 地址栏是否正在被编辑（View 在地址框获焦 / 失焦时置位）：
    /// 编辑中不把激活标签的地址变化镜像进地址栏，避免打断用户输入。
    /// </summary>
    internal bool AddressBarEditing { get; set; }

    // ---------- 工具条状态（镜像激活标签） ----------

    /// <summary>地址栏文本（双向绑定；回车 / 点「前往」时解析后导航到激活标签）。</summary>
    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    /// <summary>激活标签是否正在加载。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    /// <summary>激活标签的网页标题（鼠标悬停在地址栏上显示）。</summary>
    public string PageTitle
    {
        get => _pageTitle;
        private set => Set(ref _pageTitle, value);
    }

    /// <summary>主页（配置为空时回退默认主页）。</summary>
    private string HomePageUrl => string.IsNullOrWhiteSpace(Cfg.HomePage) ? BrowserConfig.DefaultHomePage : Cfg.HomePage;

    /// <summary>没有可恢复会话时第一个标签去的页面：记住上次页面（可关）优先，否则主页。</summary>
    internal string StartPageUrl
    {
        get
        {
            if (Cfg.RememberLastPage && !string.IsNullOrWhiteSpace(Cfg.LastPage))
            {
                var restored = BrowserUrl.Resolve(Cfg.LastPage, Cfg.SearchUrl);
                if (restored is not null) return restored;
            }
            return HomePageUrl;
        }
    }

    // ---------- 页面缩放 ----------

    /// <summary>激活标签当前缩放（1.0 = 100%）；Ctrl+滚轮 / 按钮都会更新这里。</summary>
    public double CurrentZoom
    {
        get => _currentZoom;
        private set
        {
            if (!Set(ref _currentZoom, BrowserZoom.Clamp(value))) return;
            OnPropertyChanged(nameof(ZoomText));
            OnPropertyChanged(nameof(IsZoomed));
        }
    }

    /// <summary>缩放百分比文本（工具条 / 菜单显示）。</summary>
    public string ZoomText => BrowserZoom.Format(CurrentZoom);

    /// <summary>当前是否不是 100%（工具条上的缩放小标只在非 100% 时显示）。</summary>
    public bool IsZoomed => Math.Abs(CurrentZoom - 1.0) > 0.001;

    // ---------- 历史记录 ----------

    /// <summary>浏览历史（新的在前；<c>Data\browser\history.json</c>）。</summary>
    public ObservableCollection<BrowserHistoryEntry> History { get; }

    // ---------- 下载 ----------

    /// <summary>下载列表（进行中 + 最近完成 / 中断的记录，新的在前；记录落 <c>Data\browser\downloads.json</c>）。</summary>
    public ObservableCollection<BrowserDownloadItem> Downloads { get; }

    // ---------- 书签 ----------

    /// <summary>收藏的网址（新的在前；<c>Data\browser\bookmarks.json</c>）。</summary>
    public ObservableCollection<BrowserBookmark> Bookmarks { get; }

    /// <summary>当前激活标签的地址是否已经收藏（工具条星标实心 / 空心）。</summary>
    public bool IsCurrentPageBookmarked
        => ActiveTab is { } tab && BrowserBookmarks.ContainsUrl(Bookmarks, tab.Url);

    /// <summary>当前激活标签嗅探到的图片 / 音频 / 视频（每次浏览临时结果，不落盘）。</summary>
    public ObservableCollection<BrowserResource> Resources { get; } = new();

    // ---------- 侧栏（历史记录 / 下载 / 书签 / 资源嗅探） ----------

    /// <summary>右侧面板是否打开（打开后网页区域收窄，面板宽度可拖拽记忆）。</summary>
    public bool IsSidePanelOpen
    {
        get => _isSidePanelOpen;
        private set => Set(ref _isSidePanelOpen, value);
    }

    /// <summary>侧栏当前显示历史记录。</summary>
    public bool IsHistoryPanel => _sidePanelMode == SidePanelMode.History;

    /// <summary>侧栏当前显示下载。</summary>
    public bool IsDownloadsPanel => _sidePanelMode == SidePanelMode.Downloads;

    /// <summary>侧栏当前显示书签。</summary>
    public bool IsBookmarksPanel => _sidePanelMode == SidePanelMode.Bookmarks;

    /// <summary>侧栏当前显示资源嗅探结果。</summary>
    public bool IsResourcesPanel => _sidePanelMode == SidePanelMode.Resources;

    private void SetSidePanelMode(SidePanelMode mode)
    {
        if (_sidePanelMode == mode) return;
        _sidePanelMode = mode;
        OnPropertyChanged(nameof(IsHistoryPanel));
        OnPropertyChanged(nameof(IsDownloadsPanel));
        OnPropertyChanged(nameof(IsBookmarksPanel));
        OnPropertyChanged(nameof(IsResourcesPanel));
    }

    // ---------- 主页 / 启动设置 ----------

    /// <summary>菜单里「主页」输入框的草稿（点保存才写进配置，避免边输边改）。</summary>
    public string HomePageDraft
    {
        get => _homePageDraft;
        set => Set(ref _homePageDraft, value);
    }

    /// <summary>主页保存结果提示（成功 / 格式错误）。</summary>
    public string HomePageHint
    {
        get => _homePageHint;
        private set => Set(ref _homePageHint, value);
    }

    /// <summary>启动时恢复上次打开的标签页（绑定配置，自动落盘）。</summary>
    public bool RestoreLastSession
    {
        get => Cfg.RememberLastPage;
        set
        {
            if (Cfg.RememberLastPage == value) return;
            Cfg.RememberLastPage = value;
            OnPropertyChanged();
            StatusText = value
                ? "下次打开会恢复这次的标签页（只恢复标签，点到哪个才加载哪个）"
                : "下次打开只进主页";
        }
    }

    // ---------- 命令 ----------

    public RelayCommand GoCommand { get; }
    public RelayCommand BackCommand { get; }
    public RelayCommand ForwardCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand HomeCommand { get; }

    /// <summary>新建标签页（去主页；Ctrl+T / 标签条上的 + 按钮走这里）。</summary>
    public RelayCommand NewTabCommand { get; }

    /// <summary>关闭标签（参数 = 要关的标签，缺省 = 激活标签；关掉最后一个会补一个主页标签）。</summary>
    public RelayCommand CloseTabCommand { get; }

    /// <summary>切换标签栏位置：上方 ⇄ 左侧（写进配置，下次打开还是这个位置）。</summary>
    public RelayCommand ToggleTabBarSideCommand { get; }

    public RelayCommand ZoomInCommand { get; }
    public RelayCommand ZoomOutCommand { get; }
    public RelayCommand ZoomResetCommand { get; }

    public RelayCommand ShowHistoryCommand { get; }
    public RelayCommand ShowDownloadsCommand { get; }
    public RelayCommand ShowBookmarksCommand { get; }
    public RelayCommand ShowResourcesCommand { get; }
    public RelayCommand CloseSidePanelCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public RelayCommand ToggleBookmarkCommand { get; }
    public RelayCommand OpenBookmarkCommand { get; }
    public RelayCommand OpenBookmarkInNewTabCommand { get; }
    public RelayCommand RemoveBookmarkCommand { get; }

    public AsyncRelayCommand RefreshResourcesCommand { get; }
    public RelayCommand OpenResourceCommand { get; }
    public RelayCommand CopyResourceUrlCommand { get; }
    public AsyncRelayCommand DownloadResourceCommand { get; }

    public RelayCommand OpenDownloadFileCommand { get; }
    public RelayCommand OpenDownloadFolderCommand { get; }
    public RelayCommand CancelDownloadCommand { get; }

    public RelayCommand SaveHomePageCommand { get; }
    public RelayCommand ResetHomePageCommand { get; }

    // ---------- 标签操作（View / 内核事件会调用） ----------

    /// <summary>开一个新标签（<paramref name="activate"/> = 是否立刻切过去）；target=_blank 也走这里。</summary>
    internal BrowserTab OpenTab(string url, bool activate = true)
    {
        var tab = AddTab(url);
        if (activate || ActiveTab is null) ActiveTab = tab;
        return tab;
    }

    /// <summary>关闭标签；关的是激活标签就按 <see cref="BrowserTabBar.NextActiveAfterRemove"/> 补位。</summary>
    internal void CloseTab(BrowserTab? tab)
    {
        if (tab is null) return;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        var activeIndex = ActiveTab is null ? index : Tabs.IndexOf(ActiveTab);
        var nextIndex = BrowserTabBar.NextActiveAfterRemove(Tabs.Count, index, activeIndex < 0 ? index : activeIndex);
        Tabs.RemoveAt(index); // View 的 CollectionChanged 处理器负责释放这个标签的 WebView2

        if (nextIndex < 0)
        {
            OpenTab(HomePageUrl); // 关到 0 个：补一个主页标签，页面永远有内容
            StatusText = "已关闭全部标签页";
            return;
        }
        ActiveTab = Tabs[nextIndex];
    }

    /// <summary>按方向循环切标签（Ctrl+Tab = 下一个，Ctrl+Shift+Tab = 上一个）。</summary>
    internal void CycleTab(int delta)
    {
        if (Tabs.Count == 0 || delta == 0) return;
        var index = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab));
        var next = ((index + delta) % Tabs.Count + Tabs.Count) % Tabs.Count;
        ActiveTab = Tabs[next];
    }

    /// <summary>切换标签栏位置（上方 ⇄ 左侧），配置自动落盘。</summary>
    internal void ToggleTabBarSide()
    {
        Cfg.TabBarPosition = IsTabBarLeft ? BrowserTabBar.TopPosition : BrowserTabBar.LeftPosition;
        OnPropertyChanged(nameof(IsTabBarLeft));
        OnPropertyChanged(nameof(IsTabBarTop));
    }

    /// <summary>把标签的内核状态变回「激活」时同步进工具条（地址栏 / 标题 / 按钮可用性 / 缩放）。</summary>
    internal void SyncToolbarFromActiveTab()
    {
        var tab = ActiveTab;
        if (!AddressBarEditing) AddressText = tab?.Url ?? "";
        PageTitle = tab?.Header ?? "";
        IsLoading = tab?.IsLoading ?? false;
        CurrentZoom = tab?.ZoomFactor ?? 1.0;
        OnPropertyChanged(nameof(IsCurrentPageBookmarked));
        StatusText = tab?.LastError ?? (tab is null ? "就绪" : (tab.IsLoading ? "正在加载…" : "就绪"));
        RefreshCommandState();
    }

    /// <summary>激活标签的内核就绪（View 在创建完 WebView2 后调用）。</summary>
    internal void OnActiveTabReady()
    {
        StatusText = "就绪";
        SyncToolbarFromActiveTab();
    }

    /// <summary>激活标签导航完成（成功 / 失败）：状态文字 + 记住上次页面 + 保存会话。</summary>
    internal void OnActiveTabNavigationCompleted(bool success, string? errorText)
    {
        IsLoading = false;
        StatusText = success ? "完成" : (errorText ?? "加载失败");
        if (success && Cfg.RememberLastPage && !string.IsNullOrWhiteSpace(ActiveTab?.Url))
            Cfg.LastPage = ActiveTab!.Url;
        SaveSession();
        RefreshCommandState();
    }

    /// <summary>内核异常 / 启动失败。</summary>
    internal void OnCoreError(string message)
    {
        IsLoading = false;
        StatusText = message;
        RefreshCommandState();
    }

    /// <summary>View 在任意标签导航完成时调用：写一条浏览历史（同址只更新标题时间）。</summary>
    internal void RecordHistory(string? url, string? title)
    {
        if (BrowserHistory.NormalizeUrl(url) is null) return;
        BrowserHistory.Append(History, url, title, DateTime.Now);
        ScheduleHistorySave();
    }

    /// <summary>View 在 WebView2 下载开始时调用：把新下载项插到列表最前面。</summary>
    internal void AddDownload(BrowserDownloadItem item)
    {
        if (item is null) return;
        for (var i = Downloads.Count - 1; i >= 0; i--)
        {
            if (Downloads[i].IsActive) continue;
            if (string.Equals(Downloads[i].FilePath, item.FilePath, StringComparison.OrdinalIgnoreCase))
                Downloads.RemoveAt(i);
        }
        Downloads.Insert(0, item);
        ShowDownloadsPanel();   // 和 Chrome 的下载气泡一样：开始下载就让人看得见
        StatusText = $"开始下载：{item.FileName}";
    }

    /// <summary>View 在下载结束（完成 / 中断）时调用：把结果写进下载记录文件。</summary>
    internal void OnDownloadFinished(BrowserDownloadItem item)
    {
        if (item is null) return;
        _downloadRecords = BrowserDownloads.AddRecord(_downloadRecords, item.ToRecord());
        _downloadStore.Save(_downloadRecords);
        StatusText = item.IsCompleted ? $"下载完成：{item.FileName}" : $"下载中断：{item.FileName}";
    }

    /// <summary>切换到某个标签的缩放（菜单按钮用；Ctrl+滚轮由 WebView2 原生处理，View 回写这里）。</summary>
    internal void OnActiveTabZoomChanged(double factor)
    {
        CurrentZoom = factor;
        if (ActiveTab is { } tab) tab.ZoomFactor = factor;
    }

    /// <summary>工具被真正关闭（视图从缓存里淘汰 / 程序退出）：记下会话 / 历史，释放所有网页内核。</summary>
    protected override void OnClosed()
    {
        SaveSession();
        SaveHistoryNow();
        SaveBookmarks();
        Host?.Close();
        Host = null;
    }

    // ---------- 侧栏 / 历史 / 下载操作 ----------

    /// <summary>菜单打开时刷新主页草稿（用户可能手改过 config.json）。</summary>
    internal void OnMenuOpened()
    {
        HomePageDraft = Cfg.HomePage;
        HomePageHint = "";
    }

    internal void ShowHistoryPanel()
    {
        SetSidePanelMode(SidePanelMode.History);
        IsSidePanelOpen = true;
        StatusText = History.Count == 0 ? "还没有浏览记录" : $"浏览记录：{History.Count} 条";
    }

    internal void ShowDownloadsPanel()
    {
        SetSidePanelMode(SidePanelMode.Downloads);
        IsSidePanelOpen = true;
        StatusText = Downloads.Count == 0 ? "还没有下载记录" : $"下载：{Downloads.Count} 项";
    }

    internal void ShowBookmarksPanel()
    {
        SetSidePanelMode(SidePanelMode.Bookmarks);
        IsSidePanelOpen = true;
        StatusText = Bookmarks.Count == 0 ? "还没有收藏（点地址栏右边的 ☆ 收藏当前页）" : $"书签：{Bookmarks.Count} 个";
    }

    internal void ShowResourcesPanel()
    {
        SetSidePanelMode(SidePanelMode.Resources);
        IsSidePanelOpen = true;
        _ = RefreshResourcesAsync();
    }

    internal void CloseSidePanel()
    {
        IsSidePanelOpen = false;
    }

    // ---------- 书签操作 ----------

    /// <summary>收藏 / 取消收藏当前激活标签（工具条星标按钮）。</summary>
    private void ToggleBookmark()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        var url = BrowserBookmarks.NormalizeUrl(tab.Url);
        if (url is null)
        {
            StatusText = "这个页面不能收藏（空白页 / 临时数据页）";
            return;
        }

        if (BrowserBookmarks.RemoveByUrl(Bookmarks, url))
        {
            SaveBookmarks();
            StatusText = $"已取消收藏：{tab.Title}";
            OnPropertyChanged(nameof(IsCurrentPageBookmarked));
            return;
        }

        var bookmark = BrowserBookmarks.Create(url, string.IsNullOrWhiteSpace(tab.Title) ? url : tab.Title, DateTime.Now);
        if (bookmark is null) return;
        if (!BrowserBookmarks.Add(Bookmarks, bookmark))
        {
            StatusText = "这个页面已经收藏过了";
            return;
        }

        SaveBookmarks();
        StatusText = $"已收藏：{bookmark.DisplayTitle}";
        OnPropertyChanged(nameof(IsCurrentPageBookmarked));
    }

    /// <summary>点击书签：在当前标签打开（侧栏保持打开）。</summary>
    private void OpenBookmark(BrowserBookmark? bookmark)
    {
        if (bookmark is null || string.IsNullOrWhiteSpace(bookmark.Url)) return;
        NavigateTo(bookmark.Url);
        StatusText = $"打开书签：{bookmark.DisplayTitle}";
    }

    private void OpenBookmarkInNewTab(BrowserBookmark? bookmark)
    {
        if (bookmark is null || string.IsNullOrWhiteSpace(bookmark.Url)) return;
        OpenTab(bookmark.Url);
        StatusText = $"在新标签页打开书签：{bookmark.DisplayTitle}";
    }

    private void RemoveBookmark(BrowserBookmark? bookmark)
    {
        if (bookmark is null) return;
        if (!BrowserBookmarks.RemoveByUrl(Bookmarks, bookmark.Url)) return;
        SaveBookmarks();
        OnPropertyChanged(nameof(IsCurrentPageBookmarked));
        StatusText = $"已删除书签：{bookmark.DisplayTitle}";
    }

    private void SaveBookmarks() => _bookmarkStore.Save(Bookmarks);

    // ---------- 资源嗅探 ----------

    /// <summary>View 切标签时把该标签的资源列表同步过来。</summary>
    internal void SetResources(IEnumerable<BrowserResource>? resources)
    {
        Resources.Clear();
        if (resources is null) return;
        foreach (var resource in resources) Resources.Add(resource);
    }

    /// <summary>网络嗅探实时新增一条（同址去重 / 补全由 <see cref="BrowserResources.Merge"/> 处理）。</summary>
    internal void AddResource(BrowserResource? resource)
    {
        if (resource is null) return;
        BrowserResources.Merge(Resources, resource);
    }

    /// <summary>重新在当前页跑爬虫（DOM / CSS 背景 / performance 资源），把结果合并进侧栏。</summary>
    private async Task RefreshResourcesAsync()
    {
        if (Host is null)
        {
            StatusText = "浏览器内核还在启动…";
            return;
        }
        StatusText = "正在嗅探当前页…";
        await Host.SniffCurrentPageAsync();
        StatusText = Resources.Count == 0
            ? "没有嗅探到图片 / 音频 / 视频（页面可能是 canvas / blob 流）"
            : $"资源嗅探：{Resources.Count} 项";
    }

    private void OpenResource(BrowserResource? resource)
    {
        if (resource is null || string.IsNullOrWhiteSpace(resource.Url)) return;
        OpenTab(resource.Url);
        StatusText = $"在新标签页打开资源：{resource.DisplayName}";
    }

    private void CopyResourceUrl(BrowserResource? resource)
    {
        if (resource is null || string.IsNullOrWhiteSpace(resource.Url)) return;
        CopyTextToClipboard(resource.Url);
        StatusText = "资源地址已复制";
    }

    /// <summary>保存嗅探资源：先让 View 取原始字节（CDP 响应体优先），再弹系统另存为。</summary>
    private async Task DownloadResourceAsync(BrowserResource? resource)
    {
        if (resource is null || Host is null) return;
        StatusText = $"正在读取资源：{resource.DisplayName}";
        var bytes = await Host.ReadResourceBytesAsync(resource);
        if (bytes is null || bytes.Length == 0)
        {
            StatusText = "读取资源失败（可能已过期 / 需要登录 / 不允许下载）";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存嗅探到的资源",
            FileName = resource.DisplayName,
            Filter = "所有文件|*.*",
            AddExtension = false,
        };
        if (dialog.ShowDialog() != true)
        {
            StatusText = "已取消保存";
            return;
        }

        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            StatusText = $"已保存：{dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"保存失败：{ex.Message}";
        }
    }

    private static void CopyTextToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); } catch { /* 剪贴板被占用忽略 */ }
    }

    /// <summary>点击历史记录：在当前标签打开（和 Chrome 的侧栏行为一致），侧栏保持打开。</summary>
    internal void OpenHistoryEntry(BrowserHistoryEntry? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.Url)) return;
        NavigateTo(entry.Url);
        StatusText = $"打开历史记录：{entry.DisplayTitle}";
    }

    private void ClearHistory()
    {
        if (History.Count == 0) return;
        var confirm = MessageBox.Show(
            $"确定清空全部浏览记录（{History.Count} 条）？\n只清「历史记录」，Cookie / 登录态和下载记录不受影响。",
            "清空浏览记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        History.Clear();
        _historySaveTimer.Stop();
        _historyStore.Clear();
        StatusText = "浏览记录已清空";
        RefreshCommandState();
    }

    private void OpenDownloadFile(BrowserDownloadItem? item)
    {
        if (item is null) return;
        if (string.IsNullOrWhiteSpace(item.FilePath) || !File.Exists(item.FilePath))
        {
            StatusText = "文件不存在（可能已被移动或删除）";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.FilePath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"打开文件失败：{ex.Message}";
        }
    }

    private void OpenDownloadFolder(BrowserDownloadItem? item)
    {
        try
        {
            if (item is { HasFilePath: true } && File.Exists(item.FilePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{item.FilePath}\"",
                    UseShellExecute = true,
                });
                return;
            }

            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads)) downloads = Storage.Directory;
            Process.Start(new ProcessStartInfo { FileName = downloads, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = $"打开文件夹失败：{ex.Message}";
        }
    }

    private void CancelDownload(BrowserDownloadItem? item)
    {
        if (item is not { IsActive: true }) return;
        try
        {
            item.CancelAction?.Invoke();
            StatusText = $"已取消下载：{item.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"取消下载失败：{ex.Message}";
        }
    }

    // ---------- 缩放 / 主页实现 ----------

    private void ApplyZoom(double factor)
    {
        var clamped = BrowserZoom.Clamp(factor);
        if (Host is not { IsReady: true })
        {
            StatusText = "浏览器内核还在启动…";
            return;
        }
        Host.SetZoom(clamped);
        CurrentZoom = clamped;
        if (ActiveTab is { } tab) tab.ZoomFactor = clamped;
        StatusText = $"页面缩放：{BrowserZoom.Format(clamped)}";
    }

    private void SaveHomePage()
    {
        var url = BrowserUrl.ResolveHomePage(HomePageDraft);
        if (url is null)
        {
            HomePageHint = "请输入完整网址，例如 https://www.google.com/（不能把搜索词设成主页）";
            return;
        }
        Cfg.HomePage = url;
        HomePageDraft = url;
        HomePageHint = "已保存；⌂ 主页按钮和新建标签会去这里";
        StatusText = $"主页已改为：{url}";
    }

    private void ResetHomePage()
    {
        Cfg.HomePage = BrowserConfig.DefaultHomePage;
        HomePageDraft = Cfg.HomePage;
        HomePageHint = "已恢复默认主页（Google）";
        StatusText = "已恢复默认主页";
    }

    // ---------- 会话（恢复 / 保存） ----------

    /// <summary>
    /// 构造时按配置恢复上次的标签列表：<b>只恢复标签元数据（地址 / 标题），不创建 WebView2</b>；
    /// View 只给激活标签建内核，其余标签点到才加载（Chrome 式的懒加载）。
    /// <see cref="BrowserConfig.RememberLastPage"/> 关着就只开一个起始页。
    /// </summary>
    private void RestoreSession()
    {
        var session = Cfg.RememberLastPage ? BrowserTabBar.SanitizeSession(Cfg.SessionTabs) : new List<BrowserSessionTab>();

        _restoringSession = true;
        try
        {
            if (session.Count == 0)
            {
                ActiveTab = AddTab(StartPageUrl);
                return;
            }

            foreach (var snapshot in session)
            {
                AddTab(snapshot.Url).Title = snapshot.Title;
            }
            ActiveTab = Tabs[BrowserTabBar.ClampActiveIndex(Cfg.ActiveTabIndex, Tabs.Count)];
            StatusText = $"已恢复 {Tabs.Count} 个标签页（点哪个标签才加载哪个）";
        }
        finally
        {
            // 恢复过程中 AddTab 会触发 CollectionChanged → SaveSession；不挡住的话
            // 它会把还没选中的激活索引写回 0，恢复出来的激活标签就永远是第一个了（v0.63.0 踩过）。
            _restoringSession = false;
        }
    }

    /// <summary>把当前标签列表写回配置（标签增删 / 切换 / 导航完成 / 关闭时都会走到；写入前清洗）。</summary>
    private void SaveSession()
    {
        if (_restoringSession) return;

        Cfg.SessionTabs = BrowserTabBar.SanitizeSession(
            Tabs.Select(t => new BrowserSessionTab { Url = t.Url, Title = t.Title }));
        var activeIndex = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab);
        Cfg.ActiveTabIndex = BrowserTabBar.ClampActiveIndex(activeIndex < 0 ? 0 : activeIndex, Cfg.SessionTabs.Count);
        if (!string.IsNullOrWhiteSpace(ActiveTab?.Url)) Cfg.LastPage = ActiveTab.Url;
    }

    private void ScheduleHistorySave()
    {
        _historySaveTimer.Stop();
        _historySaveTimer.Start();
    }

    private void SaveHistoryNow()
    {
        _historySaveTimer.Stop();
        _historyStore.Save(History);
    }

    private BrowserTab AddTab(string url)
    {
        var tab = new BrowserTab(_nextTabId++, url);
        HookTab(tab);
        Tabs.Add(tab);
        return tab;
    }

    /// <summary>
    /// 监听标签状态：激活标签的地址 / 标题 / 加载状态 / 前进后退可用性实时镜像到工具条。
    /// 地址栏编辑中不覆盖地址（避免打断输入），其它时候跟着内核走。
    /// </summary>
    private void HookTab(BrowserTab tab)
    {
        tab.PropertyChanged += (_, e) =>
        {
            if (!ReferenceEquals(tab, ActiveTab)) return;
            switch (e.PropertyName)
            {
                case nameof(BrowserTab.Url):
                    if (!AddressBarEditing) AddressText = tab.Url;
                    OnPropertyChanged(nameof(PageTitle)); // Header 可能跟着变（悬停提示用）
                    OnPropertyChanged(nameof(IsCurrentPageBookmarked));
                    break;
                case nameof(BrowserTab.Title):
                    PageTitle = tab.Header;
                    break;
                case nameof(BrowserTab.IsLoading):
                    IsLoading = tab.IsLoading;
                    StatusText = tab.LastError ?? (tab.IsLoading ? "正在加载…" : StatusText);
                    break;
                case nameof(BrowserTab.LastError):
                    if (tab.LastError is not null) StatusText = tab.LastError;
                    break;
                case nameof(BrowserTab.ZoomFactor):
                    CurrentZoom = tab.ZoomFactor;
                    break;
            }
            RefreshCommandState();
        };
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SaveSession();
        RefreshCommandState();
    }

    // ---------- 实现 ----------

    private void NavigateFromAddress()
    {
        var url = BrowserUrl.Resolve(AddressText, Cfg.SearchUrl);
        if (url is null)
        {
            StatusText = "请输入网址或搜索内容";
            return;
        }
        NavigateTo(url);
    }

    private void NavigateTo(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (Host is null || !Host.IsReady)
        {
            StatusText = "浏览器内核还在启动…";
            return;
        }
        Host.Navigate(url);
    }

    /// <summary>按钮可用状态依赖 <see cref="Host"/>，而 RelayCommand 走 CommandManager 重查询。</summary>
    private static void RefreshCommandState() => CommandManager.InvalidateRequerySuggested();
}
