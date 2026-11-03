using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Browser.ViewModels;

namespace YuLiangToolkit.Tools.Browser.Views;

/// <summary>
/// 「网页浏览器」界面：<b>多标签页</b>——每个标签一个 <see cref="WebView2"/>（Chromium 内核）实例，
/// 全部叠在 <c>WebHost</c> 里、只把激活标签的设为 Visible（WebView2 是独立 HWND，
/// <c>Visibility.Collapsed</c> 会真正隐藏原生窗口；没建过内核的标签在激活时才创建）。
///
/// <para><b>内核环境</b>：所有标签共享一次 <c>CoreWebView2Environment</c>（同一用户数据目录），
/// 所以 Cookie / 登录态在标签间互通，第二个标签起启动很快。</para>
///
/// <para><b>生命周期</b>：工具页被切走时外壳会缓存视图但不销毁；离开（<c>Unloaded</c>）时释放
/// 全部标签的内核、回来（<c>Loaded</c>）先建激活标签、其余标签切到时再建 —— 不会留下隐藏的
/// Edge 进程。标签列表（地址 / 标题）记在 ViewModel 与 <c>Config\browser\config.json</c>（会话恢复）里，
/// 切走再回来内容能接上（历史记录则从当前页重新开始）。</para>
///
/// <para><b>右键菜单</b>：接管 <c>ContextMenuRequested</c>（保持默认菜单开关为 true，否则事件根本
/// 不触发，微软文档明确），自己弹 WPF 菜单：刷新 / 后退 / 前进 / 复制 / 全选 / 开发者工具。</para>
/// </summary>
public partial class BrowserView : UserControl, IBrowserHost
{
    /// <summary>每个标签的内核窗口（只有激活标签 Visible，其余 Collapsed）。</summary>
    private readonly Dictionary<BrowserTab, WebView2> _webViews = new();

    /// <summary>正在创建内核的标签（防止连点标签时并发创建同一个）。</summary>
    private readonly HashSet<BrowserTab> _creatingTabs = new();

    /// <summary>进行中的下载（项 → WebView2 下载操作 + 所属标签；结束 / 关标签时从字典摘掉）。</summary>
    private readonly Dictionary<BrowserDownloadItem, (CoreWebView2DownloadOperation Operation, BrowserTab Tab)>
        _downloadOps = new();

    private CoreWebView2Environment? _environment;
    private bool _initializing;
    private bool _detached;
    private bool _vmWired;
    private bool _screenshotCaptured;
    private bool _menuTestDone;
    private bool _gearTestDone;
    private bool _panelTestDone;
    private bool _zoomTestDone;
    private bool _rightClickTestDone;
    private bool _debugUrlConsumed;
    private string? _debugUrlOverride;

    public BrowserView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        PreviewKeyDown += OnViewPreviewKeyDown;
    }

    private BrowserViewModel? Vm => DataContext as BrowserViewModel;

    /// <summary>激活标签的内核控件（没创建 / 已释放时为 null）。</summary>
    private WebView2? ActiveWebView
        => Vm?.ActiveTab is { } tab && _webViews.TryGetValue(tab, out var webView) ? webView : null;

    private CoreWebView2? ActiveCore => ActiveWebView?.CoreWebView2;

    // ---------- 视图加载 / 释放 ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var vm = Vm;
        if (vm is null || _environment is not null || _initializing) return;

        _detached = false;
        _screenshotCaptured = false;
        _menuTestDone = false;
        _gearTestDone = false;
        _panelTestDone = false;
        _zoomTestDone = false;
        _rightClickTestDone = false;
        _debugUrlConsumed = false;
        _debugUrlOverride = Environment.GetEnvironmentVariable("YLT_BROWSER_URL"); // 调试 / 截图用
        _initializing = true;
        WireViewModel(vm);
        vm.Host = this;
        StartupHintPanel.Visibility = Visibility.Visible;
        StartupHintText.Text = "正在启动 Chromium 内核…";
        Trace("loaded: starting chromium");

        try
        {
            // 用户数据（Cookie / 登录态 / 缓存）放 Cache\browser\，随时可被「清理缓存」清掉
            var cache = ToolCache.For(BrowserConfig.ToolId);
            cache.EnsureCreated();
            var userDataFolder = cache.PathOf("WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            if (_detached) return;
            _environment = environment;

            UpdateTabVisibility();
            await EnsureActiveTabCoreAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            if (!_detached)
            {
                ShowStartupError("没有找到 WebView2 运行时（Chromium 内核）。\n"
                                 + "请先安装 Microsoft Edge WebView2 Runtime，再切走 / 切回这个工具重试。");
            }
        }
        catch (Exception ex)
        {
            Trace("init-failed", ex);
            if (!_detached) ShowStartupError($"浏览器内核启动失败：{ex.Message}");
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _detached = true;
        DisposeAllWebViews();
        _environment = null;
        if (Vm is { } vm) vm.Host = null;
    }

    /// <summary>订阅 ViewModel（视图与 VM 同生命周期，只挂一次）：激活标签变化 → 按需建内核；标签移除 → 释放内核。</summary>
    private void WireViewModel(BrowserViewModel vm)
    {
        if (_vmWired) return;
        _vmWired = true;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(BrowserViewModel.ActiveTab)) return;
            Safe(() => _ = EnsureActiveTabCoreAsync());
        };
        vm.Tabs.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Remove || e.OldItems is null) return;
            foreach (var tab in e.OldItems.OfType<BrowserTab>()) DisposeTabWebView(tab);
        };
    }

    // ---------- 标签内核的创建 / 显隐 / 释放 ----------

    /// <summary>确保激活标签有内核：有就只调显隐，没有就创建（创建完导航到标签记录的待开地址）。</summary>
    private async Task EnsureActiveTabCoreAsync()
    {
        var vm = Vm;
        var tab = vm?.ActiveTab;
        if (vm is null || tab is null || _environment is null) return;
        if (_webViews.ContainsKey(tab) || _creatingTabs.Contains(tab))
        {
            UpdateTabVisibility();
            return;
        }

        _creatingTabs.Add(tab);
        Trace($"create-core tab#{tab.Id} url={tab.Url}");
        try
        {
            // 深色底：页面加载出来之前不会闪一大块白
            var webView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 16, 20),
            };
            WebHost.Children.Add(webView);
            _webViews[tab] = webView;

            await webView.EnsureCoreWebView2Async(_environment);
            if (_detached) return;

            // 创建期间标签被关掉（CollectionChanged 已释放）：这个实例作废
            if (!_webViews.TryGetValue(tab, out var registered) || !ReferenceEquals(registered, webView))
            {
                try { webView.Dispose(); } catch { /* 已释放忽略 */ }
                return;
            }

            ConfigureCore(webView, tab);
            tab.ZoomFactor = webView.ZoomFactor;   // 新内核默认 100%（或站点记忆缩放），同步工具条

            // 恢复会话 / 新标签的「待开地址」；首个内核 + 设了 YLT_BROWSER_URL 时优先走调试地址
            var pending = tab.Url;
            if (!_debugUrlConsumed && !string.IsNullOrWhiteSpace(_debugUrlOverride))
            {
                pending = _debugUrlOverride.Trim();
                _debugUrlConsumed = true;
            }
            if (!string.IsNullOrWhiteSpace(pending))
            {
                Trace($"navigate tab#{tab.Id} -> {pending}");
                webView.CoreWebView2.Navigate(pending);
            }

            UpdateTabVisibility();
            if (ReferenceEquals(vm.ActiveTab, tab)) vm.OnActiveTabReady();
            Trace($"core-ready tab#{tab.Id}");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            DropFailedWebView(tab);
            if (!_detached)
            {
                ShowStartupError("没有找到 WebView2 运行时（Chromium 内核）。\n"
                                 + "请先安装 Microsoft Edge WebView2 Runtime，再切走 / 切回这个工具重试。");
            }
        }
        catch (Exception ex)
        {
            DropFailedWebView(tab);
            Trace("create-core-failed", ex);
            if (!_detached) ShowStartupError($"浏览器内核启动失败：{ex.Message}");
        }
        finally
        {
            _creatingTabs.Remove(tab);
            UpdateTabVisibility();
        }
    }

    /// <summary>创建失败的内核从字典 / 视觉树里摘掉，下次激活重试。</summary>
    private void DropFailedWebView(BrowserTab tab)
    {
        if (!_webViews.Remove(tab, out var failed)) return;
        try { WebHost.Children.Remove(failed); } catch { /* 释放流程忽略 */ }
        try { failed.Dispose(); } catch { /* 释放流程忽略 */ }
    }

    /// <summary>只显示激活标签的内核；激活标签还没就绪时亮提示层（就绪后会被原生窗口盖住，也主动收起）。</summary>
    private void UpdateTabVisibility()
    {
        var active = Vm?.ActiveTab;
        foreach (var (tab, webView) in _webViews)
            webView.Visibility = ReferenceEquals(tab, active) ? Visibility.Visible : Visibility.Collapsed;

        var activeReady = active is not null
                          && _webViews.TryGetValue(active, out var activeView)
                          && activeView.CoreWebView2 is not null;
        StartupHintPanel.Visibility = activeReady ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DisposeTabWebView(BrowserTab tab)
    {
        FinishDownloadsOfTab(tab);
        if (!_webViews.Remove(tab, out var webView)) return;
        Trace($"dispose-core tab#{tab.Id}");
        try { WebHost.Children.Remove(webView); } catch { /* 释放流程忽略 */ }
        try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
    }

    /// <summary>释放全部标签的内核（可重复调用）。</summary>
    private void DisposeAllWebViews()
    {
        // 内核一释放，进行中的下载就没了：先把它们标成中断，别让下载面板一直停在「正在下载」
        var activeItems = _downloadOps.Keys.ToList();
        _downloadOps.Clear();
        foreach (var item in activeItems)
        {
            Vm?.Ui(() =>
            {
                if (!item.IsActive) return;
                item.MarkInterrupted("浏览器已关闭");
                Vm?.OnDownloadFinished(item);
            });
        }

        foreach (var webView in _webViews.Values)
        {
            try { WebHost.Children.Remove(webView); } catch { /* 释放流程忽略 */ }
            try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
        }
        _webViews.Clear();
    }

    private void ShowStartupError(string message)
    {
        StartupHintPanel.Visibility = Visibility.Visible;
        StartupHintText.Text = message;
        Vm?.OnCoreError(message.Split('\n')[0]);
    }

    // ---------- 内核配置 / 事件（每个标签各接各的） ----------

    private void ConfigureCore(WebView2 webView, BrowserTab tab)
    {
        var core = webView.CoreWebView2;

        // ⚠ 别把 AreDefaultContextMenusEnabled 设成 false：微软文档明确说那样会让
        // ContextMenuRequested 事件**完全不触发**（右键就没反应了）。正确做法是保持 true，
        // 在事件里 Handled = true 并自己弹菜单（见 ShowContextMenu）。
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;   // Ctrl+滚轮 / Ctrl+加减 原生缩放
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;

        // 用户 Ctrl+滚轮 / Ctrl+加减 缩放时回写标签状态（宿主自己设 ZoomFactor 不会触发这个事件，
        // 所以 SetZoom 里要主动同步一次，见 IBrowserHost.SetZoom）。
        webView.ZoomFactorChanged += (_, _) => Safe(() => OnZoomChanged(webView, tab));

        core.NavigationStarting += (_, e) => Safe(() =>
        {
            tab.IsLoading = true;
            tab.LastError = null;
            if (!string.IsNullOrWhiteSpace(e.Uri)) tab.Url = e.Uri;
        });
        core.SourceChanged += (_, _) => Safe(() =>
        {
            tab.Url = core.Source;
            SyncHistory(tab, core);
        });
        core.HistoryChanged += (_, _) => Safe(() => SyncHistory(tab, core));
        core.DocumentTitleChanged += (_, _) => Safe(() =>
        {
            tab.Title = core.DocumentTitle ?? "";
            // 标题经常比 NavigationCompleted 晚到：同址只更新标题 / 时间，不会重复插记录
            Vm?.RecordHistory(tab.Url, tab.Title);
        });
        core.NavigationCompleted += (_, e) => Safe(() =>
        {
            SyncHistory(tab, core);
            Trace($"navigation-completed tab#{tab.Id} success={e.IsSuccess} status={e.WebErrorStatus}");
            tab.IsLoading = false;
            tab.LastError = e.IsSuccess ? null : DescribeError(e.WebErrorStatus);
            if (e.IsSuccess) Vm?.RecordHistory(tab.Url, tab.Title);
            if (ReferenceEquals(tab, Vm?.ActiveTab))
            {
                Vm?.OnActiveTabNavigationCompleted(e.IsSuccess, tab.LastError);
                TryCaptureForScreenshot();
                TryOpenMenuForDebug();
                TryOpenDebugUi();
                TryRightClickForDebug();
            }
        });
        core.NewWindowRequested += (_, e) => Safe(() =>
        {
            // target=_blank / window.open：开进新标签页
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(e.Uri)) Vm?.OpenTab(e.Uri);
        });
        core.ContextMenuRequested += (_, e) => Safe(() => ShowContextMenu(core, e));
        core.DownloadStarting += (_, e) => Safe(() => BeginDownload(e, tab));
        core.ProcessFailed += (_, e) => Safe(() =>
            Vm?.OnCoreError($"浏览器内核异常（{e.ProcessFailedKind}），切走再切回这个工具可重启内核。"));
    }

    /// <summary>页面缩放变化（Ctrl+滚轮 / Ctrl+加减 / 站点记忆缩放）：回写到标签与工具条。</summary>
    private void OnZoomChanged(WebView2 webView, BrowserTab tab)
    {
        var factor = BrowserZoom.Clamp(webView.ZoomFactor);
        tab.ZoomFactor = factor;
        if (ReferenceEquals(tab, Vm?.ActiveTab)) Vm?.OnActiveTabZoomChanged(factor);
    }

    // ---------- 下载（WebView2 DownloadStarting + DownloadOperation 事件） ----------

    private void BeginDownload(CoreWebView2DownloadStartingEventArgs e, BrowserTab tab)
    {
        var operation = e.DownloadOperation;
        // 关掉 WebView2 自带的下载提示条：下载照常进行，进度统一显示在「下载」侧栏里
        e.Handled = true;

        var item = new BrowserDownloadItem(operation.Uri, operation.ResultFilePath, DateTime.Now)
        {
            CancelAction = () =>
            {
                try { operation.Cancel(); } catch { /* 已结束忽略 */ }
            },
        };
        _downloadOps[item] = (operation, tab);

        operation.BytesReceivedChanged += (_, _) => Safe(() => UpdateDownload(item));
        operation.StateChanged += (_, _) => Safe(() => UpdateDownload(item));

        Vm?.AddDownload(item);
        Trace($"download-start tab#{tab.Id} {operation.Uri} -> {operation.ResultFilePath}");
        UpdateDownload(item);
    }

    /// <summary>把 WebView2 下载操作的最新状态同步到下载项（必须在 UI 线程改绑定属性）。</summary>
    private void UpdateDownload(BrowserDownloadItem item)
    {
        if (!_downloadOps.TryGetValue(item, out var entry)) return;
        var operation = entry.Operation;
        var state = operation.State;
        var received = operation.BytesReceived;
        var total = operation.TotalBytesToReceive;
        var resultPath = operation.ResultFilePath;

        Vm?.Ui(() =>
        {
            item.UpdateProgress(received, total);
            switch (state)
            {
                case CoreWebView2DownloadState.Completed:
                    item.MarkCompleted(resultPath);
                    FinishDownload(item);
                    break;
                case CoreWebView2DownloadState.Interrupted:
                    item.MarkInterrupted(DescribeDownloadInterrupt(operation.InterruptReason));
                    FinishDownload(item);
                    break;
            }
        });
    }

    /// <summary>结束一项下载：从进行中字典摘掉并写入下载记录（重复状态事件不会重复记）。</summary>
    private void FinishDownload(BrowserDownloadItem item)
    {
        if (!_downloadOps.Remove(item)) return;
        Vm?.OnDownloadFinished(item);
        Trace($"download-finished {item.FileName} state={item.StateText}");
    }

    /// <summary>标签关闭 / 释放时，属于它的进行中下载标成中断（WebView2 实例没了，不会再有完成事件）。</summary>
    private void FinishDownloadsOfTab(BrowserTab tab)
    {
        var items = _downloadOps
            .Where(kv => ReferenceEquals(kv.Value.Tab, tab))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var item in items)
        {
            if (!_downloadOps.Remove(item)) continue;
            Vm?.Ui(() =>
            {
                item.MarkInterrupted("标签页已关闭");
                Vm?.OnDownloadFinished(item);
            });
        }
    }

    private static string DescribeDownloadInterrupt(CoreWebView2DownloadInterruptReason reason) => reason switch
    {
        CoreWebView2DownloadInterruptReason.UserCanceled => "已取消",
        CoreWebView2DownloadInterruptReason.UserShutdown => "程序已关闭",
        CoreWebView2DownloadInterruptReason.FileNoSpace => "磁盘空间不足",
        CoreWebView2DownloadInterruptReason.FileAccessDenied => "没有写入权限",
        CoreWebView2DownloadInterruptReason.FileNameTooLong => "文件名太长",
        CoreWebView2DownloadInterruptReason.FileBlockedByPolicy => "被策略阻止",
        CoreWebView2DownloadInterruptReason.NetworkFailed or CoreWebView2DownloadInterruptReason.NetworkDisconnected
            => "网络断开",
        CoreWebView2DownloadInterruptReason.NetworkTimeout => "网络超时",
        CoreWebView2DownloadInterruptReason.NetworkServerDown => "服务器不可用",
        CoreWebView2DownloadInterruptReason.ServerFailed or CoreWebView2DownloadInterruptReason.ServerUnexpectedResponse
            => "服务器返回错误",
        CoreWebView2DownloadInterruptReason.ServerCertificateProblem => "网站证书有问题",
        CoreWebView2DownloadInterruptReason.ServerForbidden or CoreWebView2DownloadInterruptReason.ServerUnauthorized
            => "服务器拒绝访问",
        CoreWebView2DownloadInterruptReason.DownloadProcessCrashed => "下载进程崩溃",
        CoreWebView2DownloadInterruptReason.None => "",
        _ => reason.ToString(),
    };

    private void SyncHistory(BrowserTab tab, CoreWebView2 core)
    {
        tab.CanGoBack = core.CanGoBack;
        tab.CanGoForward = core.CanGoForward;
    }

    private static string DescribeError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => "找不到服务器（域名解析失败）",
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset
            => "连接被中断",
        CoreWebView2WebErrorStatus.CannotConnect => "无法连接服务器",
        CoreWebView2WebErrorStatus.Timeout => "连接超时",
        CoreWebView2WebErrorStatus.Disconnected => "网络已断开",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
            or CoreWebView2WebErrorStatus.CertificateExpired
            or CoreWebView2WebErrorStatus.CertificateIsInvalid
            or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => "网站证书有问题",
        CoreWebView2WebErrorStatus.ServerUnreachable => "服务器不可达",
        _ => $"加载失败（{status}）",
    };

    // ---------- 右键菜单 ----------

    private void ShowContextMenu(CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        // 接住事件：默认菜单不弹，改弹我们自己的（不放 Handled 的话 Chromium 会弹出它自己的菜单）。
        // GetDeferral 是官方示例要求的写法：菜单还开着时让 WebView2 等我们处理完。
        e.Handled = true;
        var deferral = e.GetDeferral();
        var target = e.ContextMenuTarget;
        Trace($"context-menu-requested tab#{TabIdOf(core)} kind={target.Kind} " +
              $"link={target.HasLinkUri} source={target.HasSourceUri} location={e.Location.X:0},{e.Location.Y:0}");
        Trace("context-menu-items: " + string.Join(",",
            e.MenuItems.Select(item => item.Name ?? item.Label)));

        var menu = CreateContextMenu(core, e);
        Trace("custom-menu-items: " + string.Join(",",
            menu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString() ?? "")));
        menu.Closed += (_, _) =>
        {
            try { deferral.Complete(); } catch { /* 视图已释放 */ }
        };

        try
        {
            menu.IsOpen = true;
            Trace("context-menu-opened");
        }
        catch (Exception ex)
        {
            Trace("context-menu-failed", ex);
            try { deferral.Complete(); } catch { /* 同上 */ }
            throw;
        }
    }

    /// <summary>
    /// 组装浏览器右键菜单：<b>图片 / 链接上右键</b>先给上下文动作（图片另存为… / 复制图片 / 打开图片、
    /// 链接另存为… / 复制链接地址 / 新标签打开链接），然后才是通用的刷新 / 后退 / 前进 / 复制 / 全选 / 开发者工具。
    /// 菜单用 WPF 自己画，放在网页这块原生窗口的上方。
    ///
    /// <para>「另存为」不是自己下文件，而是把 WebView2 默认菜单里对应命令的 <c>SelectedCommandId</c>
    /// 报回去执行 —— 这样登录态 / Cookie / 文件命名规则都走 Chromium 原生逻辑（官方推荐的
    /// 「自定义菜单里执行默认菜单命令」写法）。</para>
    /// </summary>
    private ContextMenu CreateContextMenu(CoreWebView2 core, CoreWebView2ContextMenuRequestedEventArgs? args)
    {
        var menu = new ContextMenu
        {
            Style = (Style)Resources["BrowserContextMenuStyle"],
            PlacementTarget = ActiveWebView,
            Placement = PlacementMode.MousePoint,
        };

        if (args is not null)
        {
            AddContextTargetItems(menu, args);
        }

        // 用户要的「右键网页内部也能刷新」放第一条（纯页面右键时就是第一条）
        menu.Items.Add(CreateMenuItem("刷新", "F5", () => core.Reload()));
        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateMenuItem("后退", "Alt+←", () => core.GoBack(), core.CanGoBack));
        menu.Items.Add(CreateMenuItem("前进", "Alt+→", () => core.GoForward(), core.CanGoForward));
        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateMenuItem("复制", "Ctrl+C", () => _ = CopySelectionAsync(core)));
        menu.Items.Add(CreateMenuItem("全选", "Ctrl+A", () => _ = core.ExecuteScriptAsync("document.execCommand('selectAll')")));
        menu.Items.Add(CreateSeparator());
        menu.Items.Add(CreateMenuItem("开发者工具", "F12", () => core.OpenDevToolsWindow()));
        return menu;
    }

    /// <summary>按右键目标（图片 / 链接）加一组上下文动作；没有对应目标就什么都不加。</summary>
    private void AddContextTargetItems(ContextMenu menu, CoreWebView2ContextMenuRequestedEventArgs args)
    {
        var target = args.ContextMenuTarget;
        var added = false;

        if (target.Kind == CoreWebView2ContextMenuTargetKind.Image)
        {
            added |= AddDefaultCommandItem(menu, args, "图片另存为…", "saveImageAs");
            added |= AddDefaultCommandItem(menu, args, "复制图片", "copyImage");

            // 「复制图片地址」优先用 Chromium 默认命令（不受 HasSourceUri 影响）；
            // 个别版本没有这个命令时再退回 SourceUri。
            var copiedAddress = AddDefaultCommandItem(menu, args, "复制图片地址", "copyImageLocation");
            if (!copiedAddress && target.HasSourceUri)
            {
                var sourceUri = target.SourceUri;
                menu.Items.Add(CreateMenuItem("复制图片地址", "", () => CopyTextToClipboard(sourceUri)));
            }
            added = true;

            if (target.HasSourceUri)
            {
                var sourceUri = target.SourceUri;
                menu.Items.Add(CreateMenuItem("在新标签页打开图片", "", () => Vm?.OpenTab(sourceUri)));
            }
        }

        if (target.HasLinkUri)
        {
            added |= AddDefaultCommandItem(menu, args, "链接另存为…", "saveLinkAs");
            var linkUri = target.LinkUri;

            if (!AddDefaultCommandItem(menu, args, "复制链接地址", "copyLinkLocation"))
                menu.Items.Add(CreateMenuItem("复制链接地址", "", () => CopyTextToClipboard(linkUri)));

            menu.Items.Add(CreateMenuItem("在新标签页打开链接", "", () => Vm?.OpenTab(linkUri)));
            added = true;
        }

        if (added) menu.Items.Add(CreateSeparator());
    }

    /// <summary>把 WebView2 默认菜单里的某个命令搬到自定义菜单：点击时回报 <c>SelectedCommandId</c>。</summary>
    private bool AddDefaultCommandItem(
        ContextMenu menu, CoreWebView2ContextMenuRequestedEventArgs args, string header, string commandName)
    {
        var commandId = FindCommandId(args.MenuItems, commandName);
        if (commandId is null)
        {
            Trace($"context-menu-missing-command: {commandName}");
            return false;
        }

        var id = commandId.Value;
        menu.Items.Add(CreateMenuItem(header, "", () => args.SelectedCommandId = id));
        return true;
    }

    /// <summary>在默认菜单项树里按内部 Name（如 saveImageAs / saveLinkAs）找命令 Id。</summary>
    private static int? FindCommandId(IEnumerable<CoreWebView2ContextMenuItem> items, string commandName)
    {
        foreach (var item in items)
        {
            if (string.Equals(item.Name, commandName, StringComparison.OrdinalIgnoreCase))
                return item.CommandId;
            if (item.Kind == CoreWebView2ContextMenuItemKind.Submenu
                && FindCommandId(item.Children, commandName) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    /// <summary>复制纯文本到 Windows 剪贴板（剪贴板被占用时静默忽略）。</summary>
    private static void CopyTextToClipboard(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); } catch { /* 剪贴板被占用忽略 */ }
    }

    private Separator CreateSeparator()
        => new() { Style = (Style)Resources["BrowserMenuSeparatorStyle"] };

    private MenuItem CreateMenuItem(string header, string gesture, Action action, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture,
            IsEnabled = enabled,
            Style = (Style)Resources["BrowserMenuItemStyle"],
        };
        item.Click += (_, _) => Safe(action);
        return item;
    }

    /// <summary>反查某个 core 属于哪个标签（只是排错日志更好看）。</summary>
    private int TabIdOf(CoreWebView2 core)
        => _webViews.FirstOrDefault(kv => ReferenceEquals(kv.Value.CoreWebView2, core)).Key.Id;

    /// <summary>
    /// 复制网页里选中的文字：取 <c>window.getSelection()</c> 再塞进 Windows 剪贴板。
    /// 不用 <c>document.execCommand('copy')</c> —— 从 WPF 菜单点过来不算网页自己的用户手势，
    /// 浏览器可能拒绝执行，用 WPF 剪贴板更稳。
    /// </summary>
    private static async Task CopySelectionAsync(CoreWebView2 core)
    {
        try
        {
            var json = await core.ExecuteScriptAsync("window.getSelection().toString()");
            var text = System.Text.Json.JsonSerializer.Deserialize<string>(json);
            if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板被占用 / 页面已释放都忽略
        }
    }

    // ---------- 标签条交互（关标签 / 新建标签 / 快捷键） ----------

    private void OnTabCloseClick(object sender, RoutedEventArgs e)
        => Vm?.CloseTab((sender as FrameworkElement)?.DataContext as BrowserTab);

    /// <summary>中键点击标签 = 关闭（浏览器惯例）。</summary>
    private void OnTabListMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var tab = FindTabFromEvent(e.OriginalSource);
        if (tab is null) return;
        Vm?.CloseTab(tab);
        e.Handled = true;
    }

    /// <summary>双击标签条空白处 = 新建标签（点在标签上则不动）。</summary>
    private void OnTabListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindTabFromEvent(e.OriginalSource) is not null) return;
        Vm?.NewTabCommand.Execute(null);
    }

    private static BrowserTab? FindTabFromEvent(object source)
    {
        DependencyObject? current = source as DependencyObject;
        while (current is not null && current is not ListBoxItem)
            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        return (current as ListBoxItem)?.DataContext as BrowserTab;
    }

    /// <summary>
    /// 标签快捷键：Ctrl+T 新建、Ctrl+W 关闭、Ctrl+Tab / Ctrl+Shift+Tab 循环切换。
    /// ⚠ 焦点在地址栏 / 标签条这些 WPF 控件上时可靠；焦点在网页内部时 WebView2 可能自己吃掉按键，
    /// 属于 HwndHost 键盘路由的限制，标签条上的按钮永远可用。
    /// </summary>
    private void OnViewPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;
        var modifiers = e.KeyboardDevice.Modifiers;

        switch (e.Key)
        {
            case Key.T when modifiers == ModifierKeys.Control:
                vm.NewTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W when modifiers == ModifierKeys.Control:
                vm.CloseTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab when modifiers == ModifierKeys.Control:
                vm.CycleTab(1);
                e.Handled = true;
                break;
            case Key.Tab when modifiers == (ModifierKeys.Control | ModifierKeys.Shift):
                vm.CycleTab(-1);
                e.Handled = true;
                break;
        }
    }

    // ---------- 地址栏 ----------

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm is { } vm) vm.GoCommand.Execute(null);
        e.Handled = true;
    }

    private void OnAddressGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is { } vm) vm.AddressBarEditing = true; // 编辑中不覆盖地址栏文本
        AddressBox.SelectAll();
    }

    private void OnAddressLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Vm is { } vm) vm.AddressBarEditing = false;
    }

    // ---------- IBrowserHost（全部路由到激活标签） ----------

    public bool CanGoBack => ActiveCore?.CanGoBack ?? false;

    public bool CanGoForward => ActiveCore?.CanGoForward ?? false;

    public bool IsReady => ActiveCore is not null;

    public void Navigate(string url)
    {
        var core = ActiveCore;
        if (core is null || string.IsNullOrWhiteSpace(url)) return;
        core.Navigate(url);
    }

    public void GoBack()
    {
        var core = ActiveCore;
        if (core?.CanGoBack == true) core.GoBack();
    }

    public void GoForward()
    {
        var core = ActiveCore;
        if (core?.CanGoForward == true) core.GoForward();
    }

    public void Reload()
    {
        var core = ActiveCore;
        if (core is null) return;
        core.Reload();
    }

    public void Stop() => ActiveCore?.Stop();

    /// <summary>
    /// 设置激活标签的缩放。⚠ 官方文档：宿主自己设 <c>ZoomFactor</c> 时**不会**触发
    /// <c>ZoomFactorChanged</c>，所以这里要主动把标签 / 工具条状态同步一次。
    /// </summary>
    public void SetZoom(double factor)
    {
        var webView = ActiveWebView;
        if (webView is null) return;
        var clamped = BrowserZoom.Clamp(factor);
        webView.ZoomFactor = clamped;
        if (Vm?.ActiveTab is { } tab) tab.ZoomFactor = clamped;
        Vm?.OnActiveTabZoomChanged(clamped);
    }

    public void Close()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(DisposeAllWebViews);
            return;
        }
        DisposeAllWebViews();
    }
    // ---------- 齿轮菜单 / 侧栏（历史记录 / 下载） ----------

    private void OnGearClick(object sender, RoutedEventArgs e)
    {
        if (!GearPopup.IsOpen) Vm?.OnMenuOpened();
        GearPopup.IsOpen = !GearPopup.IsOpen;
    }

    /// <summary>菜单里点「历史记录 / 下载」：先收菜单，再打开右侧面板。</summary>
    private void OnGearHistoryClick(object sender, RoutedEventArgs e)
    {
        GearPopup.IsOpen = false;
        Vm?.ShowHistoryPanel();
    }

    private void OnGearDownloadsClick(object sender, RoutedEventArgs e)
    {
        GearPopup.IsOpen = false;
        Vm?.ShowDownloadsPanel();
    }

    private void OnGearBookmarksClick(object sender, RoutedEventArgs e)
    {
        GearPopup.IsOpen = false;
        Vm?.ShowBookmarksPanel();
    }

    /// <summary>点历史记录条目：打开 (当前标签)；侧栏保持打开，方便继续翻。</summary>
    private void OnHistoryListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: BrowserHistoryEntry entry })
            Vm?.OpenHistoryEntry(entry);
    }

    // ---------- 调试：截图模式下额外抓一张网页区域 ----------
    /// <summary>
    /// 调试：<c>YLT_BROWSER_MENU_TEST=1</c> 时在首屏加载完后自动弹一次右键菜单，
    /// 用来验证「WPF 菜单能盖在 WebView2 原生窗口上面」（合成的右键消息不一定能进 Chromium，
    /// 所以留这个直接触发菜单的钩子；正常使用不执行）。
    /// </summary>
    private void TryOpenMenuForDebug()
    {
        if (_menuTestDone) return;
        if (Environment.GetEnvironmentVariable("YLT_BROWSER_MENU_TEST") != "1") return;
        _menuTestDone = true;

        // 延迟几秒再弹：便于截图脚本先把窗口切到前台（否则菜单会跟着失活被关掉）
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(6000),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var core = ActiveCore;
            if (core is null) return;
            Safe(() =>
            {
                var menu = CreateContextMenu(core, null);
                menu.IsOpen = true;
                Trace("context-menu-opened (menu test)");
            });
        };
        timer.Start();
    }

    /// <summary>
    /// 调试钩子（截图验收用，正常使用不执行）：
    /// <c>YLT_BROWSER_GEAR_TEST=1</c> 延迟打开齿轮菜单；
    /// <c>YLT_BROWSER_PANEL_TEST=history|downloads</c> 直接打开右侧面板；
    /// <c>YLT_BROWSER_ZOOM_TEST=130</c> 把页面缩放到 130%（验证缩放状态 / 工具条指示）。
    /// </summary>
    private void TryOpenDebugUi()
    {
        var panel = Environment.GetEnvironmentVariable("YLT_BROWSER_PANEL_TEST");
        if (!_panelTestDone && !string.IsNullOrWhiteSpace(panel) && Vm is { } vm)
        {
            _panelTestDone = true;
            Safe(() =>
            {
                if (panel.Trim().StartsWith("download", StringComparison.OrdinalIgnoreCase))
                    vm.ShowDownloadsPanel();
                else if (panel.Trim().StartsWith("bookmark", StringComparison.OrdinalIgnoreCase))
                    vm.ShowBookmarksPanel();
                else
                    vm.ShowHistoryPanel();
            });
        }

        var zoomText = Environment.GetEnvironmentVariable("YLT_BROWSER_ZOOM_TEST");        if (!_zoomTestDone && double.TryParse(zoomText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var zoomPercent))
        {
            _zoomTestDone = true;
            var zoomTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200),
            };
            zoomTimer.Tick += (_, _) =>
            {
                zoomTimer.Stop();
                Safe(() => SetZoom(zoomPercent / 100.0));
            };
            zoomTimer.Start();
        }

        if (_gearTestDone || Environment.GetEnvironmentVariable("YLT_BROWSER_GEAR_TEST") != "1") return;
        _gearTestDone = true;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(3000),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Safe(() =>
            {
                Vm?.OnMenuOpened();
                GearPopup.IsOpen = true;
                Trace("gear-opened (test)");
            });
        };
        timer.Start();
    }

    /// <summary>
    /// 调试钩子：<c>YLT_BROWSER_RIGHTCLICK_TEST=x,y</c>（或 <c>=1</c> 用网页中心）会通过 DevTools 协议
    /// 在网页里发一次**真实右键**，用来验证图片 / 链接的上下文菜单（合成系统鼠标消息进不了 Chromium）。
    /// </summary>
    private void TryRightClickForDebug()
    {
        if (_rightClickTestDone) return;
        var spec = Environment.GetEnvironmentVariable("YLT_BROWSER_RIGHTCLICK_TEST");
        if (string.IsNullOrWhiteSpace(spec)) return;
        var core = ActiveCore;
        var webView = ActiveWebView;
        if (core is null || webView is null) return;
        _rightClickTestDone = true;

        var x = webView.ActualWidth / 2;
        var y = webView.ActualHeight / 2;
        var parts = spec.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedX)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedY))
        {
            x = parsedX;
            y = parsedY;
        }

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(4000),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _ = DispatchRightClickAsync(core, x, y);
        };
        timer.Start();
    }

    private async Task DispatchRightClickAsync(CoreWebView2 core, double x, double y)
    {
        try
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var pressed = $"{{\"type\":\"mousePressed\",\"x\":{x.ToString(inv)},\"y\":{y.ToString(inv)},\"button\":\"right\",\"clickCount\":1}}";
            var released = $"{{\"type\":\"mouseReleased\",\"x\":{x.ToString(inv)},\"y\":{y.ToString(inv)},\"button\":\"right\",\"clickCount\":1}}";
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", pressed);
            await Task.Delay(60);
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", released);
            Trace($"right-click-dispatched {x:0},{y:0}");
        }
        catch (Exception ex)
        {
            Trace("right-click-dispatch-failed", ex);
        }
    }

    /// <summary>
    /// <c>YLT_SHOT</c> 截图走的是 WPF RenderTargetBitmap，抓不到 WebView2（它是独立 HWND），
    /// 所以截图模式下用 WebView2 自己的 <c>CapturePreviewAsync</c> 另存一张 <c>&lt;截图&gt;.web.png</c>，
    /// 用来核对 Chromium 真的把网页渲染出来了（正常使用不会执行）。
    /// </summary>
    private async void TryCaptureForScreenshot()
    {
        var shotPath = Environment.GetEnvironmentVariable("YLT_SHOT");
        var core = ActiveCore;
        if (_screenshotCaptured || string.IsNullOrWhiteSpace(shotPath) || core is null) return;
        _screenshotCaptured = true;

        try
        {
            await Task.Delay(350);
            await using var stream = File.Create(shotPath + ".web.png");
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        }
        catch
        {
            // 截图只是开发辅助，失败不影响功能
        }
    }

    /// <summary>事件回调里统一兜异常：内核 / 视图释放过程中可能还在派发事件，不能让它崩界面。</summary>
    private static void Safe(Action action)
    {
        try { action(); } catch { /* 忽略释放竞态 */ }
    }

    /// <summary>
    /// 排错开关：设了环境变量 <c>YLT_BROWSER_TRACE=&lt;日志路径&gt;</c> 时把内核启动 / 导航 / 右键菜单
    /// 这些关键节点追加写进去（正常使用不产生文件），排查「右键没反应」「内核没起来」时用。
    /// </summary>
    private static void Trace(string message, Exception? exception = null)
    {
        var path = Environment.GetEnvironmentVariable("YLT_BROWSER_TRACE");
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var detail = exception is null ? "" : $" :: {exception.GetType().Name}: {exception.Message}";
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {message}{detail}{Environment.NewLine}");
        }
        catch
        {
            // 排错日志写不进去不影响功能
        }
    }
}
