using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Mail.ViewModels;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>
/// 邮箱代收工具页的代码后置：
/// <list type="bullet">
///   <item>页面第一次显示时通知 ViewModel（按配置决定要不要自动刷新一次）；</item>
///   <item><b>授权码密码框同步</b>：WPF 的 <c>PasswordBox.Password</c> 不是依赖属性、不能直接绑定，
///         所以这里和 ViewModel 的 <c>SelectedAccount.Password</c> 手动双向同步（输入时写入账号，
///         切换账号 / 重新挂回页面时从账号读出）。这样界面上只显示圆点，不会把授权码明文摊在屏幕上；</item>
///   <item><b>HTML 富文本预览</b>：用 WebView2（Chromium 内核）渲染 Foxmail 那种带格式 / 图片 / 表格的邮件正文。
///         用户数据放 <c>Cache\mail-collector\WebView2</c>（可被「清理缓存」清掉）；关掉 JavaScript、
///         开发工具和宿主对象，只当“只读的 HTML 渲染器”用；链接点了用默认浏览器打开，不在邮件里导航。</item>
/// </list>
/// </summary>
public partial class MailCollectorView : UserControl
{
    private MailCollectorViewModel? _subscribed;
    private bool _syncingPassword;

    private CoreWebView2Environment? _webViewEnvironment;
    private WebView2? _htmlPreview;
    private bool _webViewInitializing;
    private bool _detached;
    private string? _lastRenderedHtml;

    public MailCollectorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private MailCollectorViewModel? ViewModel => DataContext as MailCollectorViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _detached = false;
        AttachViewModel();
        ViewModel?.OnViewLoaded();
        _ = InitializeHtmlPreviewAsync();

        // 开发截图冒烟用：YLT_MAIL_SETTINGS_BOTTOM=1 时把「账号设置」滚到底（看网络代理区域）
        if (Environment.GetEnvironmentVariable("YLT_MAIL_SETTINGS_BOTTOM") == "1")
        {
            Dispatcher.BeginInvoke(
                new Action(() => SettingsScroll.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _detached = true;
        DetachViewModel();
        DisposeHtmlPreview();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) => AttachViewModel();

    private void AttachViewModel()
    {
        var viewModel = ViewModel;
        if (ReferenceEquals(_subscribed, viewModel))
        {
            SyncPasswordFromViewModel();
            return;
        }

        DetachViewModel();
        _subscribed = viewModel;
        if (viewModel is null) return;

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        SyncPasswordFromViewModel();
    }

    private void DetachViewModel()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MailCollectorViewModel.SelectedAccount)) SyncPasswordFromViewModel();
        if (e.PropertyName is nameof(MailCollectorViewModel.MessageHtml)
            or nameof(MailCollectorViewModel.ShowHtmlPreview))
        {
            RenderCurrentHtml();
        }
    }

    // 授权码：PasswordBox ↔ ViewModel.SelectedAccount.Password

    private void SyncPasswordFromViewModel()
    {
        _syncingPassword = true;
        try
        {
            PasswordInput.Password = ViewModel?.SelectedAccount?.Password ?? "";
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword) return;
        if (ViewModel?.SelectedAccount is { } account) account.Password = PasswordInput.Password;
    }

    // HTML 富文本预览：WebView2 的创建 / 配置 / 渲染 / 释放

    private async Task InitializeHtmlPreviewAsync()
    {
        if (_webViewEnvironment is not null || _webViewInitializing) return;
        _webViewInitializing = true;
        try
        {
            // 和「网页浏览器」同一个思路：用户数据放 Cache\mail-collector\，随时可被「清理缓存」清掉
            var cache = ToolCache.For("mail-collector");
            cache.EnsureCreated();
            var userDataFolder = cache.PathOf("WebView2");
            Directory.CreateDirectory(userDataFolder);

            _webViewEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, null);
            if (_detached || !IsLoaded)
            {
                _webViewEnvironment = null;
                return;
            }

            var webView = new WebView2
            {
                // 邮件正文按白底渲染（和 Foxmail 类似），深色主题下不会先闪一块黑
                DefaultBackgroundColor = System.Drawing.Color.White,
            };
            HtmlHost.Children.Add(webView);
            await webView.EnsureCoreWebView2Async(_webViewEnvironment);
            if (_detached || !IsLoaded)
            {
                HtmlHost.Children.Remove(webView);
                try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
                return;
            }

            ConfigureCore(webView);
            _htmlPreview = webView;
            RenderCurrentHtml();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ViewModel?.SetHtmlPreviewUnavailable("没有找到 WebView2 运行时，已改用纯文本显示正文（安装 Edge WebView2 Runtime 后可看 HTML 邮件）。");
        }
        catch (Exception ex)
        {
            ViewModel?.SetHtmlPreviewUnavailable("HTML 富文本预览启动失败，已改用纯文本显示：" + ex.Message);
        }
        finally
        {
            _webViewInitializing = false;
        }
    }

    private void ConfigureCore(WebView2 webView)
    {
        var core = webView.CoreWebView2;
        if (core is null) return;

        var settings = core.Settings;
        settings.IsScriptEnabled = false;                 // 邮件里的脚本一律不跑
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;

        // 正文里的链接 / 新窗口：不在邮件里导航，交给默认浏览器
        core.NavigationStarting += (_, e) =>
        {
            if (IsInternalDocumentUrl(e.Uri)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!string.IsNullOrWhiteSpace(e.Uri)) OpenExternal(e.Uri);
        };
    }

    private void RenderCurrentHtml()
    {
        var viewModel = ViewModel;
        var webView = _htmlPreview;
        if (viewModel is null || webView?.CoreWebView2 is null) return;
        if (!viewModel.ShowHtmlPreview || string.IsNullOrWhiteSpace(viewModel.MessageHtml)) return;
        if (string.Equals(_lastRenderedHtml, viewModel.MessageHtml, StringComparison.Ordinal)) return;

        _lastRenderedHtml = viewModel.MessageHtml;
        try
        {
            webView.CoreWebView2.NavigateToString(viewModel.MessageHtml);
        }
        catch (Exception ex)
        {
            viewModel.SetHtmlPreviewUnavailable("HTML 渲染失败，已改用纯文本显示：" + ex.Message);
        }
    }

    private void DisposeHtmlPreview()
    {
        _lastRenderedHtml = null;
        _webViewEnvironment = null;

        var webView = _htmlPreview;
        _htmlPreview = null;
        if (webView is null) return;
        try { HtmlHost.Children.Remove(webView); } catch { /* 释放流程忽略 */ }
        try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
    }

    private static bool IsInternalDocumentUrl(string? url)
        => string.IsNullOrWhiteSpace(url)
           || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
           || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

    private static void OpenExternal(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto")) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch
        {
            // 打不开浏览器不影响阅读
        }
    }
}
