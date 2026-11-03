using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Browser.ViewModels;

namespace YuLiangToolkit.Tools.Browser.Views;

/// <summary>
/// 「网页浏览器」界面：工具条（XAML）+ 动态创建的 <see cref="WebView2"/>（Chromium 内核）。
///
/// <para><b>生命周期</b>：工具页被切走时外壳会缓存视图但不销毁；WebView2 是独立进程，
/// 所以离开（<c>Unloaded</c>）时释放内核、回来（<c>Loaded</c>）重新启动 —— 不会留下隐藏的
/// Edge 进程。用户数据（Cookie / 登录态）放在 <c>Cache\browser\WebView2</c>，
/// 「主页设置 → 清理缓存」可以清掉。</para>
///
/// <para><b>右键菜单</b>：关掉 WebView2 自带菜单（<c>AreDefaultContextMenusEnabled = false</c>），
/// 在自己弹的 WPF 菜单里放「刷新 / 后退 / 前进 / 复制 / 全选 / 开发者工具」。</para>
/// </summary>
public partial class BrowserView : UserControl, IBrowserHost
{
    private WebView2? _webView;
    private bool _initializing;
    private bool _detached;
    private bool _screenshotCaptured;

    public BrowserView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private BrowserViewModel? Vm => DataContext as BrowserViewModel;

    // ---------- 内核启动 / 释放 ----------

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_webView is not null || _initializing) return;
        var vm = Vm;
        if (vm is null) return;

        _detached = false;
        _screenshotCaptured = false;
        _initializing = true;
        vm.Host = this;
        StartupHintPanel.Visibility = Visibility.Visible;
        StartupHintText.Text = "正在启动 Chromium 内核…";

        try
        {
            // 用户数据（Cookie / 登录态 / 缓存）放 Cache\browser\，随时可被「清理缓存」清掉
            var cache = ToolCache.For(BrowserConfig.ToolId);
            cache.EnsureCreated();
            var userDataFolder = cache.PathOf("WebView2");
            Directory.CreateDirectory(userDataFolder);

            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            if (_detached) return;

            // 深色底：页面加载出来之前不会闪一大块白
            var webView = new WebView2
            {
                DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 16, 20),
            };
            WebHost.Children.Add(webView);
            _webView = webView;

            await webView.EnsureCoreWebView2Async(environment);
            if (_detached || webView.CoreWebView2 is null) return;

            ConfigureCore(webView.CoreWebView2);
            StartupHintPanel.Visibility = Visibility.Collapsed;
            vm.OnReady();

            var overrideUrl = Environment.GetEnvironmentVariable("YLT_BROWSER_URL"); // 调试 / 截图用
            webView.CoreWebView2.Navigate(string.IsNullOrWhiteSpace(overrideUrl) ? vm.StartPageUrl : overrideUrl.Trim());
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
        DisposeWebView();
        if (Vm is { } vm) vm.Host = null;
    }

    /// <summary>释放 WebView2（可重复调用）。</summary>
    private void DisposeWebView()
    {
        var webView = _webView;
        _webView = null;
        if (webView is null) return;

        try { WebHost.Children.Remove(webView); } catch { /* 卸载流程忽略 */ }
        try { webView.Dispose(); } catch { /* 卸载流程忽略 */ }
    }

    private void ShowStartupError(string message)
    {
        StartupHintPanel.Visibility = Visibility.Visible;
        StartupHintText.Text = message;
        Vm?.OnCoreError(message.Split('\n')[0]);
    }

    // ---------- 内核配置 / 事件 ----------

    private void ConfigureCore(CoreWebView2 core)
    {
        // 右键菜单自己画（要带「刷新」），不要 Chromium 自带的
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.Settings.IsSwipeNavigationEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = true;

        core.NavigationStarting += (_, e) => Safe(() => Vm?.OnNavigationStarting(e.Uri));
        core.SourceChanged += (_, _) => Safe(() =>
        {
            var current = _webView?.CoreWebView2;
            if (current is null) return;
            Vm?.OnSourceChanged(current.Source);
            SyncHistory(current);
        });
        core.HistoryChanged += (_, _) => Safe(() =>
        {
            var current = _webView?.CoreWebView2;
            if (current is not null) SyncHistory(current);
        });
        core.DocumentTitleChanged += (_, _) => Safe(() =>
            Vm?.OnTitleChanged(_webView?.CoreWebView2?.DocumentTitle ?? ""));
        core.NavigationCompleted += (_, e) => Safe(() => OnNavigationCompleted(e));
        core.NewWindowRequested += (_, e) => Safe(() => OnNewWindowRequested(e));
        core.ContextMenuRequested += (_, e) => Safe(() => ShowContextMenu(core, e));
        core.ProcessFailed += (_, e) => Safe(() =>
            Vm?.OnCoreError($"浏览器内核异常（{e.ProcessFailedKind}），切走再切回这个工具可重启内核。"));
    }

    private void OnNavigationCompleted(CoreWebView2NavigationCompletedEventArgs e)
    {
        var vm = Vm;
        if (vm is null) return;

        SyncHistory(_webView?.CoreWebView2);
        vm.OnNavigationCompleted(e.IsSuccess, e.IsSuccess ? null : DescribeError(e.WebErrorStatus));
        TryCaptureForScreenshot();
    }

    /// <summary>target=_blank / window.open：本工具暂时单页，就在当前页打开。</summary>
    private void OnNewWindowRequested(CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(e.Uri)) Navigate(e.Uri);
    }

    private void SyncHistory(CoreWebView2? core)
    {
        if (core is null) return;
        Vm?.OnHistoryChanged(core.CanGoBack, core.CanGoForward);
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
        e.Handled = true;

        var menu = new ContextMenu
        {
            Style = (Style)Resources["BrowserContextMenuStyle"],
            PlacementTarget = _webView,
            Placement = PlacementMode.MousePoint,
        };

        // 用户要的「右键网页内部也能刷新」放第一条
        menu.Items.Add(CreateMenuItem("刷新", "F5", () => core.Reload()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("后退", "Alt+←", () => core.GoBack(), core.CanGoBack));
        menu.Items.Add(CreateMenuItem("前进", "Alt+→", () => core.GoForward(), core.CanGoForward));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("复制", "Ctrl+C", () => _ = CopySelectionAsync(core)));
        menu.Items.Add(CreateMenuItem("全选", "Ctrl+A", () => _ = core.ExecuteScriptAsync("document.execCommand('selectAll')")));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("开发者工具", "F12", () => core.OpenDevToolsWindow()));

        menu.IsOpen = true;
    }

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

    // ---------- IBrowserHost ----------

    public bool CanGoBack => _webView?.CoreWebView2?.CanGoBack ?? false;

    public bool CanGoForward => _webView?.CoreWebView2?.CanGoForward ?? false;

    public bool IsReady => _webView?.CoreWebView2 is not null;

    public void Navigate(string url)
    {
        var core = _webView?.CoreWebView2;
        if (core is null || string.IsNullOrWhiteSpace(url)) return;
        core.Navigate(url);
    }

    public void GoBack()
    {
        var core = _webView?.CoreWebView2;
        if (core?.CanGoBack == true) core.GoBack();
    }

    public void GoForward()
    {
        var core = _webView?.CoreWebView2;
        if (core?.CanGoForward == true) core.GoForward();
    }

    public void Reload()
    {
        var core = _webView?.CoreWebView2;
        if (core is null) return;
        core.Reload();
    }

    public void Stop() => _webView?.CoreWebView2?.Stop();

    public void Close()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(DisposeWebView);
            return;
        }
        DisposeWebView();
    }

    // ---------- 地址栏 ----------

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm is { } vm) vm.GoCommand.Execute(null);
        e.Handled = true;
    }

    private void OnAddressGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressBox.SelectAll();

    // ---------- 调试：截图模式下额外抓一张网页区域 ----------

    /// <summary>
    /// <c>YLT_SHOT</c> 截图走的是 WPF RenderTargetBitmap，抓不到 WebView2（它是独立 HWND），
    /// 所以截图模式下用 WebView2 自己的 <c>CapturePreviewAsync</c> 另存一张 <c>&lt;截图&gt;.web.png</c>，
    /// 用来核对 Chromium 真的把网页渲染出来了（正常使用不会执行）。
    /// </summary>
    private async void TryCaptureForScreenshot()
    {
        var shotPath = Environment.GetEnvironmentVariable("YLT_SHOT");
        var core = _webView?.CoreWebView2;
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
}
