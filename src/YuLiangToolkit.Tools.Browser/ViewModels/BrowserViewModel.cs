using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Browser.ViewModels;

/// <summary>
/// 「网页浏览器」的 ViewModel：<b>多标签页</b>版——地址栏文本、按钮状态、状态提示、配置，
/// 以及标签列表（<see cref="Tabs"/> / <see cref="ActiveTab"/>）与标签栏位置（上方 / 左侧）。
/// 真正的网页内核由 <see cref="IBrowserHost"/>（= <c>BrowserView.xaml.cs</c>）负责：
/// 内核操作都作用于<b>激活标签</b>，每个标签一个 WebView2 实例（共享同一内核环境，Cookie / 登录态互通）。
/// </summary>
public sealed class BrowserViewModel : ToolViewModelBase
{
    private string _addressText = "";
    private bool _isLoading;
    private string _pageTitle = "";
    private BrowserTab? _activeTab;
    private int _nextTabId;

    public BrowserViewModel() : base(BrowserConfig.ToolId)
    {
        Cfg = LoadConfig<BrowserConfig>();
        Cfg.TabBarPosition = BrowserTabBar.NormalizePosition(Cfg.TabBarPosition);

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
    }

    /// <summary>配置（<c>Config\browser\config.json</c>）。</summary>
    public BrowserConfig Cfg { get; }

    /// <summary>
    /// 当前承载网页内核的视图（没启动 / 被切走时为 <c>null</c>）。
    /// 内核操作（前进 / 后退 / 刷新…）都由它路由到<b>激活标签</b>。
    /// </summary>
    internal IBrowserHost? Host { get; set; }

    // ---------- 标签页 ----------

    /// <summary>打开着的所有标签（顺序即标签条顺序；关到 0 个时会立刻补一个主页标签）。</summary>
    public ObservableCollection<BrowserTab> Tabs { get; }

    /// <summary>激活标签（工具条 / 网页区域显示的就是它；切走再切回时内核按需重建）。</summary>
    public BrowserTab? ActiveTab
    {
        get => _activeTab;
        set { if (Set(ref _activeTab, value)) SyncToolbarFromActiveTab(); }
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

    /// <summary>把标签的内核状态变回「激活」时同步进工具条（地址栏 / 标题 / 按钮可用性）。</summary>
    internal void SyncToolbarFromActiveTab()
    {
        var tab = ActiveTab;
        if (!AddressBarEditing) AddressText = tab?.Url ?? "";
        PageTitle = tab?.Header ?? "";
        IsLoading = tab?.IsLoading ?? false;
        StatusText = tab?.LastError ?? (tab is null ? "就绪" : (tab.IsLoading ? "正在加载…" : "就绪"));
        RefreshCommandState();
    }

    /// <summary>激活标签的内核就绪（View 在创建完 WebView2 后调用）。</summary>
    internal void OnActiveTabReady()
    {
        StatusText = "就绪";
        SyncToolbarFromActiveTab();
    }

    /// <summary>激活标签导航完成（成功 / 失败）：状态文字 + 记住上次页面。</summary>
    internal void OnActiveTabNavigationCompleted(bool success, string? errorText)
    {
        IsLoading = false;
        StatusText = success ? "完成" : (errorText ?? "加载失败");
        if (success && Cfg.RememberLastPage && !string.IsNullOrWhiteSpace(ActiveTab?.Url))
            Cfg.LastPage = ActiveTab!.Url;
        RefreshCommandState();
    }

    /// <summary>内核异常 / 启动失败。</summary>
    internal void OnCoreError(string message)
    {
        IsLoading = false;
        StatusText = message;
        RefreshCommandState();
    }

    /// <summary>工具被真正关闭（视图从缓存里淘汰 / 程序退出）：记下会话，释放所有网页内核。</summary>
    protected override void OnClosed()
    {
        SaveSession();
        Host?.Close();
        Host = null;
    }

    // ---------- 会话（恢复 / 保存） ----------

    /// <summary>构造时按配置恢复上次的标签列表（<see cref="BrowserConfig.RememberLastPage"/> 关着就只开一个起始页）。</summary>
    private void RestoreSession()
    {
        var session = Cfg.RememberLastPage ? BrowserTabBar.SanitizeSession(Cfg.SessionTabs) : new List<BrowserSessionTab>();

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
        StatusText = $"已恢复 {Tabs.Count} 个标签页";
    }

    /// <summary>把当前标签列表写回配置（切走 / 退出时都会走到；<see cref="BrowserTabBar.SanitizeSession"/> 清洗）。</summary>
    private void SaveSession()
    {
        Cfg.SessionTabs = BrowserTabBar.SanitizeSession(
            Tabs.Select(t => new BrowserSessionTab { Url = t.Url, Title = t.Title }));
        var activeIndex = ActiveTab is null ? 0 : Tabs.IndexOf(ActiveTab);
        Cfg.ActiveTabIndex = BrowserTabBar.ClampActiveIndex(activeIndex < 0 ? 0 : activeIndex, Cfg.SessionTabs.Count);
        if (!string.IsNullOrWhiteSpace(ActiveTab?.Url)) Cfg.LastPage = ActiveTab.Url;
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
            }
            RefreshCommandState();
        };
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshCommandState();

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
