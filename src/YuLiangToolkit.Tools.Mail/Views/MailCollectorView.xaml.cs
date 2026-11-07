using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Mail.ViewModels;

namespace YuLiangToolkit.Tools.Mail.Views;

/// <summary>
/// 邮箱代收工具页的代码后置：
/// <list type="bullet">
///   <item>页面显示时通知 ViewModel 自动刷新；</item>
///   <item><b>账号 / 代理 / 日志</b> 都走弹窗（账号列表双击 = 编辑），不再占用主界面；</item>
///   <item><b>HTML 富文本预览</b>：WebView2（Chromium 内核）渲染 Foxmail 那种带格式 / 图片 / 表格的邮件正文。
///         用户数据放 <c>Cache\mail-collector\WebView2</c>（可被「清理缓存」清掉）；关掉 JavaScript、
///         开发工具和宿主对象，只当“只读的 HTML 渲染器”用；链接点了用默认浏览器打开，不在邮件里导航。
///         Ctrl+滚轮 / Ctrl+加减 缩放由 WebView2 原生处理（IsZoomControlEnabled）。</item>
/// </list>
/// </summary>
public partial class MailCollectorView : UserControl
{
    private MailCollectorViewModel? _subscribed;

    private CoreWebView2Environment? _webViewEnvironment;
    private WebView2? _htmlPreview;
    private bool _webViewInitializing;
    private bool _detached;
    private string? _lastRenderedHtml;
    private Point _attachmentDragStart;
    private MailAttachmentItem? _pendingAttachmentDrag;

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
        ScrollSelectedNavigationIntoView();

        // 开发截图冒烟用：YLT_MAIL_DIALOG=account / proxy / log 时自动打开对应弹窗
        var dialogMode = Environment.GetEnvironmentVariable("YLT_MAIL_DIALOG");
        if (!string.IsNullOrWhiteSpace(dialogMode))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (dialogMode == "account") OnEditAccountClick(this, new RoutedEventArgs());
                else if (dialogMode == "proxy") OnProxyClick(this, new RoutedEventArgs());
                else if (dialogMode == "log") OnLogClick(this, new RoutedEventArgs());
            }), System.Windows.Threading.DispatcherPriority.Background);
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
        if (ReferenceEquals(_subscribed, viewModel)) return;

        DetachViewModel();
        _subscribed = viewModel;
        if (viewModel is null) return;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void DetachViewModel()
    {
        if (_subscribed is not null) _subscribed.PropertyChanged -= OnViewModelPropertyChanged;
        _subscribed = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MailCollectorViewModel.MessageHtml)
            or nameof(MailCollectorViewModel.ShowHtmlPreview))
        {
            RenderCurrentHtml();
        }

        if (e.PropertyName == nameof(MailCollectorViewModel.SelectedNavigationItem))
            ScrollSelectedNavigationIntoView();
    }

    private void ScrollSelectedNavigationIntoView()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ViewModel?.SelectedNavigationItem is { } node)
                NavigationListBox.ScrollIntoView(node);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------- 账号 / 代理 / 日志弹窗 ----------

    private void OnAddAccountClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;

        var draft = new MailAccountConfig
        {
            DisplayName = "",
            Email = "",
            Host = "",
            Port = 993,
            UseSsl = true,
            Enabled = true,
        };
        var dialog = new MailAccountDialog(draft, viewModel.Cfg.Proxy) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        viewModel.Cfg.Accounts.Add(draft);
        viewModel.SelectAccount(draft, MailFolderKind.Inbox);
    }

    private void OnEditAccountClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        var account = viewModel?.SelectedAccount;
        if (viewModel is null || account is null) return;

        var draft = MailAccountDialog.Clone(account);
        var dialog = new MailAccountDialog(draft, viewModel.Cfg.Proxy) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;

        MailAccountDialog.CopyTo(draft, account);
        viewModel.SelectedAccount = account;
    }

    private void OnAccountDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel?.SelectedNavigationItem is not { IsAccount: true }) return;
        OnEditAccountClick(sender, e);
    }

    // ---------- 附件拖到桌面 / 资源管理器 ----------

    private void OnAttachmentPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pendingAttachmentDrag = null;
        if (sender is not FrameworkElement { DataContext: MailAttachmentItem item }) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;

        _attachmentDragStart = e.GetPosition(null);
        _pendingAttachmentDrag = item;
    }

    private async void OnAttachmentPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pendingAttachmentDrag is null || e.LeftButton != MouseButtonState.Pressed) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _attachmentDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - _attachmentDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var item = _pendingAttachmentDrag;
        _pendingAttachmentDrag = null;
        var viewModel = ViewModel;
        if (viewModel is null) return;

        try
        {
            var path = await viewModel.PrepareAttachmentDragFileAsync(item);
            if (path is null || _detached || Mouse.LeftButton != MouseButtonState.Pressed) return;

            var data = new DataObject(DataFormats.FileDrop, new[] { path });
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            viewModel.Log("附件拖拽失败：" + ex.Message);
        }
    }

    private void OnAttachmentPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _pendingAttachmentDrag = null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnProxyClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;

        var dialog = new MailProxyDialog(viewModel.Cfg.Proxy) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true) return;
        viewModel.ApplyProxySettings(dialog.Draft);
    }

    private void OnLogClick(object sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null) return;

        var dialog = new MailLogDialog { Owner = Window.GetWindow(this), DataContext = viewModel };
        dialog.ShowDialog();
    }

    // ---------- HTML 富文本预览：WebView2 创建 / 配置 / 渲染 / 释放 ----------

    private async Task InitializeHtmlPreviewAsync()
    {
        if (_webViewEnvironment is not null || _webViewInitializing) return;
        _webViewInitializing = true;
        try
        {
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
        settings.IsZoomControlEnabled = true;             // Ctrl+滚轮 / Ctrl+加减 原生缩放

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
