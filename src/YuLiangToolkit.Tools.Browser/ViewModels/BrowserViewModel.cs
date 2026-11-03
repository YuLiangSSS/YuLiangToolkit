using System.Windows.Input;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Browser.ViewModels;

/// <summary>
/// 「网页浏览器」的 ViewModel：只管地址栏文本、按钮状态、状态提示与配置，
/// 真正的网页内核由 <see cref="IBrowserHost"/>（= <c>BrowserView.xaml.cs</c>）负责。
/// </summary>
public sealed class BrowserViewModel : ToolViewModelBase
{
    private string _addressText = "";
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isLoading;
    private string _pageTitle = "";

    public BrowserViewModel() : base(BrowserConfig.ToolId)
    {
        Cfg = LoadConfig<BrowserConfig>();
        AddressText = StartPageUrl;

        GoCommand = new RelayCommand(_ => NavigateFromAddress());
        BackCommand = new RelayCommand(_ => Host?.GoBack(), _ => Host is { CanGoBack: true });
        ForwardCommand = new RelayCommand(_ => Host?.GoForward(), _ => Host is { CanGoForward: true });
        RefreshCommand = new RelayCommand(_ => Host?.Reload(), _ => Host is { IsReady: true });
        HomeCommand = new RelayCommand(_ => NavigateTo(HomePageUrl), _ => Host is { IsReady: true });
    }

    /// <summary>配置（<c>Config\browser\config.json</c>）。</summary>
    public BrowserConfig Cfg { get; }

    /// <summary>当前承载网页内核的视图（没启动 / 被切走时为 <c>null</c>）。</summary>
    internal IBrowserHost? Host { get; set; }

    /// <summary>地址栏文本（双向绑定；回车 / 点「前往」时解析）。</summary>
    public string AddressText
    {
        get => _addressText;
        set => Set(ref _addressText, value);
    }

    /// <summary>是否还能后退（工具栏按钮的可用状态）。</summary>
    public bool CanGoBack
    {
        get => _canGoBack;
        private set => Set(ref _canGoBack, value);
    }

    /// <summary>是否还能前进。</summary>
    public bool CanGoForward
    {
        get => _canGoForward;
        private set => Set(ref _canGoForward, value);
    }

    /// <summary>是否正在加载（工具栏右边的状态文字用）。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    /// <summary>当前网页标题（鼠标悬停在地址栏上显示）。</summary>
    public string PageTitle
    {
        get => _pageTitle;
        private set => Set(ref _pageTitle, value);
    }

    /// <summary>主页（配置为空时回退默认主页）。</summary>
    private string HomePageUrl => string.IsNullOrWhiteSpace(Cfg.HomePage) ? BrowserConfig.DefaultHomePage : Cfg.HomePage;

    /// <summary>打开浏览器时先去的页面：记住上次页面（可关）优先，否则主页。</summary>
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

    // ---------- 界面事件（由 BrowserView 在 UI 线程调用） ----------

    /// <summary>内核启动完成。</summary>
    internal void OnReady()
    {
        StatusText = "就绪";
        RefreshCommandState();
    }

    /// <summary>开始导航（WebView2 <c>NavigationStarting</c>）。</summary>
    internal void OnNavigationStarting(string url)
    {
        IsLoading = true;
        StatusText = "正在加载…";
        if (!string.IsNullOrWhiteSpace(url)) AddressText = url;
    }

    /// <summary>导航结束（成功 / 失败）。</summary>
    internal void OnNavigationCompleted(bool success, string? errorText)
    {
        IsLoading = false;
        if (success)
        {
            StatusText = "完成";
            if (Cfg.RememberLastPage && !string.IsNullOrWhiteSpace(AddressText)) Cfg.LastPage = AddressText;
        }
        else
        {
            StatusText = errorText ?? "加载失败";
        }
        RefreshCommandState();
    }

    /// <summary>页面地址变了（重定向 / 点链接 / 前进后退都会触发）。</summary>
    internal void OnSourceChanged(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) AddressText = url;
    }

    /// <summary>历史记录状态变了（前进后退按钮可用性）。</summary>
    internal void OnHistoryChanged(bool canGoBack, bool canGoForward)
    {
        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        RefreshCommandState();
    }

    /// <summary>网页标题变了。</summary>
    internal void OnTitleChanged(string title)
    {
        PageTitle = title ?? "";
        Cfg.LastPage = Cfg.RememberLastPage && !string.IsNullOrWhiteSpace(AddressText) ? AddressText : Cfg.LastPage;
    }

    /// <summary>内核异常 / 启动失败。</summary>
    internal void OnCoreError(string message)
    {
        IsLoading = false;
        StatusText = message;
        RefreshCommandState();
    }

    /// <summary>工具被真正关闭（视图从缓存里淘汰 / 程序退出）：释放网页内核。</summary>
    protected override void OnClosed()
    {
        Host?.Close();
        Host = null;
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
