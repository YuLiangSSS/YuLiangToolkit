using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
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

    // ---------- 全屏浏览 ----------
    /// <summary>外壳（MainWindow 实现 IFullscreenHost）的全屏开关；卸载时置空。</summary>
    private IFullscreenHost? _fullscreenHost;
    private Window? _hostWindow;
    private bool _fullscreen;
    private WebView2? _fullscreenWebView;
    private string? _lastFullscreenRenderedHtml;

    // ---------- 全屏悬浮工具栏 + 缩放 ----------
    /// <summary>鼠标进入距顶部多少像素就叫出悬浮工具栏。</summary>
    private const double ToolbarRevealZonePixels = 96;

    /// <summary>鼠标离开后多久收起工具条。</summary>
    private static readonly TimeSpan ToolbarAutoHideDelay = TimeSpan.FromMilliseconds(1200);

    private const double MinZoomPercent = 25;
    private const double MaxZoomPercent = 400;

    /// <summary>当前缩放百分比（默认 100%）。纯文本改字号，HTML 走 WebView2 的 ZoomFactor。</summary>
    private double _zoomPercent = 100;
    /// <summary>100% 时纯文本正文的字号，缩放按它等比放大 / 缩小。</summary>
    private const double BasePlainFontSize = 14;
    /// <summary>HTML 那一层 100% 对应的 ZoomFactor（WebView2 默认值就是 1.0）。</summary>
    private const double BaseHtmlZoomFactor = 1.0;
    private double _htmlZoomFactor = BaseHtmlZoomFactor;

    private bool _toolbarShown;
    private DispatcherTimer? _toolbarAutoHideTimer;

    /// <summary>
    /// 截图 / 调试用：设了 <c>YLT_MAIL_FULLSCREEN=toolbar</c> 时把悬浮工具栏钉在屏幕上（不自动收起）。
    /// 正常使用永远走自动收起，这个标志只由环境变量置位。
    /// </summary>
    private bool _toolbarPinnedForScreenshot;

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

        _fullscreenHost = Window.GetWindow(this) as IFullscreenHost;
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
            _hostWindow.PreviewKeyDown += OnHostPreviewKeyDown;
        }

        FullscreenToolbar.SizeChanged -= OnFullscreenToolbarSizeChanged;
        FullscreenToolbar.SizeChanged += OnFullscreenToolbarSizeChanged;

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

        // 开发截图冒烟用：YLT_MAIL_FULLSCREEN=1 直接进全屏（核对悬浮工具栏 / 版式）；
        // =toolbar 时把工具栏钉住不放（截图时不用抢自动收起的时机）
        var fullscreenMode = Environment.GetEnvironmentVariable("YLT_MAIL_FULLSCREEN");
        if (!string.IsNullOrWhiteSpace(fullscreenMode))
        {
            _toolbarPinnedForScreenshot = fullscreenMode == "toolbar";
            Dispatcher.BeginInvoke(new Action(EnterFullscreen), System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _detached = true;
        // 先退全屏（让外壳还原标题栏 / 侧边栏），再摘窗口事件；反过来外壳会一直停在“藏了壳”的状态
        ExitFullscreen();
        if (_hostWindow is not null) _hostWindow.PreviewKeyDown -= OnHostPreviewKeyDown;
        _hostWindow = null;
        _fullscreenHost = null;
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
            if (_fullscreen)
            {
                _ = EnsureFullscreenWebViewAsync();
                RenderFullscreenHtml();
            }
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

    // ---------- 全屏浏览 ----------

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        if (_fullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void OnExitFullscreenClick(object sender, RoutedEventArgs e) => ExitFullscreen();

    /// <summary>
    /// 进全屏：盖出整页覆盖层 + 让外壳藏标题栏 / 侧边栏（IFullscreenHost）。
    /// 覆盖层里是自己独立的一份正文视图（纯文本 TextBox / WebView2），主界面那份不动。
    /// 进去时悬浮工具栏先藏着，鼠标移到顶部才滑下来（同 PDF 阅读器）。
    /// </summary>
    private void EnterFullscreen()
    {
        if (_fullscreen) return;
        _fullscreen = true;
        FullscreenOverlay.Visibility = Visibility.Visible;
        _fullscreenHost?.SetFullscreen(true);

        _toolbarShown = false;
        ApplyToolbarHiddenPosition();
        ApplyZoom();

        _ = EnsureFullscreenWebViewAsync();
        FullscreenOverlay.Focus();

        // 布局完成后按真实高度重算隐藏位置，然后先滑出来露一次脸：
        // 不然第一次进来的用户不知道顶部有悬浮工具栏（露一下就自动收起）。
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!_fullscreen) return;
            ApplyToolbarHiddenPosition();      // 先按真实高度归位
            if (!_toolbarShown) ShowToolbarAnimated();   // 露一次脸，随后自动收起
        }));
    }

    private void ExitFullscreen()
    {
        if (!_fullscreen) return;
        _fullscreen = false;
        _toolbarAutoHideTimer?.Stop();
        _toolbarShown = false;
        ApplyToolbarHiddenPosition();
        FullscreenOverlay.Visibility = Visibility.Collapsed;
        _fullscreenHost?.SetFullscreen(false);
    }

    /// <summary>外壳层 Esc：全屏时优先退出全屏（PreviewKeyDown 隧道，正文里的 TextBox 也拦不住）。</summary>
    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _fullscreen)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    /// <summary>覆盖层自己也要吃 Esc：外壳 PreviewKeyDown 已挂才到这里，双保险。</summary>
    private void OnFullscreenPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ExitFullscreen();
            e.Handled = true;
        }
    }

    // ---------- 全屏悬浮工具栏：靠近顶部滑下来，离开后自动收起 ----------

    /// <summary>工具条藏起来时整块平移到上方（保留可见性，滑动动画才有得动）。</summary>
    private double ToolbarHiddenOffset => Math.Max(FullscreenToolbar.ActualHeight, 40) + 16;

    private void ApplyToolbarHiddenPosition()
    {
        FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        FullscreenToolbarSlideTransform.Y = -ToolbarHiddenOffset;
        FullscreenToolbar.IsHitTestVisible = false;
    }

    private void OnFullscreenToolbarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fullscreen && !_toolbarShown) ApplyToolbarHiddenPosition();
    }

    /// <summary>工具条从上边滑下来。</summary>
    private void ShowToolbarAnimated()
    {
        if (!_fullscreen) return;

        _toolbarShown = true;
        FullscreenToolbar.IsHitTestVisible = true;

        var from = FullscreenToolbarSlideTransform.Y;
        if (double.IsNaN(from) || from > 0) from = -ToolbarHiddenOffset;

        // 基准值也放到起点：否则收起动画一清就会瞬间掉回去，看着像“瞬间消失”
        FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        FullscreenToolbarSlideTransform.Y = from;

        var animation = new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            if (!_toolbarShown) return;
            FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            FullscreenToolbarSlideTransform.Y = 0;
        };
        FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, animation);

        EnsureToolbarAutoHideTimer();
    }

    /// <summary>工具条向上滑回去。</summary>
    private void HideToolbarAnimated()
    {
        if (!_fullscreen || !_toolbarShown) return;

        _toolbarShown = false;
        FullscreenToolbar.IsHitTestVisible = false;

        // 点过按钮 / 输过百分比后焦点还留在工具条里：收起时交还出去，
        // 不然下次按回车还会去触发那个看不见的按钮。
        if (FullscreenToolbar.IsKeyboardFocusWithin)
        {
            try { Keyboard.ClearFocus(); } catch { /* 焦点清理失败不影响收起 */ }
        }

        var startY = FullscreenToolbarSlideTransform.Y;
        if (double.IsNaN(startY) || startY > 0) startY = 0;
        FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
        FullscreenToolbarSlideTransform.Y = startY;

        var target = -ToolbarHiddenOffset;
        var animation = new DoubleAnimation(startY, target, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        animation.Completed += (_, _) =>
        {
            if (_toolbarShown) return;
            FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            FullscreenToolbarSlideTransform.Y = target;
        };
        FullscreenToolbarSlideTransform.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>鼠标靠近顶部就叫出工具条；什么时候收起由倒计时 + 真实光标位置判断。</summary>
    private void OnFullscreenMouseMove(object sender, MouseEventArgs e)
    {
        if (!_fullscreen) return;
        if (!_toolbarShown && e.GetPosition(this).Y <= ToolbarRevealZonePixels) ShowToolbarAnimated();
    }

    private void EnsureToolbarAutoHideTimer()
    {
        _toolbarAutoHideTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ToolbarAutoHideDelay,
        };
        if (_toolbarAutoHideTimer.Tag is null)
        {
            _toolbarAutoHideTimer.Tick += OnToolbarAutoHideTick;
            _toolbarAutoHideTimer.Tag = true;
        }

        _toolbarAutoHideTimer.Stop();
        _toolbarAutoHideTimer.Start();
    }

    private void OnToolbarAutoHideTick(object? sender, EventArgs e)
    {
        _toolbarAutoHideTimer?.Stop();
        if (!_fullscreen || !_toolbarShown) return;
        if (_toolbarPinnedForScreenshot) return;

        // ⚠ 不能只看 IsMouseOver：正文是 WebView2 的 HWND，鼠标移进正文后 WPF 收不到 MouseMove，
        // 光靠事件判断会让工具条永远挂在上面。这里直接问系统光标位置，定时轮询最稳。
        // 正在输百分比（焦点在工具条里）时也别收。
        if (FullscreenToolbar.IsKeyboardFocusWithin || Mouse.GetPosition(this).Y <= ToolbarRevealZonePixels)
        {
            EnsureToolbarAutoHideTimer();
            return;
        }

        HideToolbarAnimated();
    }

    // ---------- 全屏缩放：放大 / 缩小 / 百分比 / Ctrl + 滚轮 ----------

    private void OnZoomInClick(object sender, RoutedEventArgs e) => SetZoom(_zoomPercent + 10);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => SetZoom(_zoomPercent - 10);

    /// <summary>
    /// Ctrl + 滚轮缩放：不按 Ctrl 时放行给滚动条。
    /// HTML 和纯文本都统一走 <see cref="SetZoom"/>，WebView2 那边的原生缩放关掉，免得两套百分比打架。
    /// </summary>
    private void OnFullscreenPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(_zoomPercent + (e.Delta > 0 ? 10 : -10));
        e.Handled = true;
    }

    private void SetZoom(double percent)
    {
        percent = Math.Clamp(percent, MinZoomPercent, MaxZoomPercent);
        if (Math.Abs(percent - _zoomPercent) < 0.01) return;

        _zoomPercent = percent;
        ApplyZoom();
    }

    /// <summary>把当前百分比落到两个正文视图上（纯文本改字号，HTML 改 WebView2 的 ZoomFactor）。</summary>
    private void ApplyZoom()
    {
        FullscreenPlainBody.FontSize = Math.Round(BasePlainFontSize * _zoomPercent / 100.0, 1);
        _htmlZoomFactor = BaseHtmlZoomFactor * _zoomPercent / 100.0;
        if (_fullscreenWebView is { } webView)
        {
            try { webView.ZoomFactor = _htmlZoomFactor; } catch { /* 内核未就绪时忽略，渲染时会再套一次 */ }
        }

        var text = _zoomPercent.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        if (FullscreenZoomBox.Text != text) FullscreenZoomBox.Text = text;
    }

    /// <summary>百分比输入框：回车 / 失焦时按输入值缩放，非法输入回退成当前值。</summary>
    private void CommitZoomBoxText()
    {
        if (double.TryParse(FullscreenZoomBox.Text?.Trim().TrimEnd('%'), out var percent)
            && percent >= MinZoomPercent && percent <= MaxZoomPercent)
        {
            SetZoom(percent);
        }

        ApplyZoom();   // 非法输入 → 显示值回到当前百分比
    }

    private void OnZoomBoxPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitZoomBoxText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ApplyZoom();   // 放弃输入，别退全屏
            e.Handled = true;
        }
    }

    private void OnZoomBoxGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        FullscreenZoomBox.SelectAll();
        _toolbarAutoHideTimer?.Stop();
    }

    private void OnZoomBoxLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitZoomBoxText();

    /// <summary>
    /// 全屏 HTML 正文：等主界面的 WebView2 环境就绪后，再建全屏这份（同一环境、同一缓存目录）。
    /// </summary>
    private async Task EnsureFullscreenWebViewAsync()
    {
        if (!_fullscreen || _fullscreenWebView is not null) return;
        if (ViewModel?.ShowHtmlPreview != true) return;

        if (_webViewEnvironment is null)
        {
            await InitializeHtmlPreviewAsync();
            if (_webViewEnvironment is null) return;   // 没有 WebView2 运行时：VM 已降级纯文本
        }
        if (!_fullscreen || _detached || _fullscreenWebView is not null) return;

        var webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        FullscreenHtmlHost.Children.Add(webView);
        try
        {
            await webView.EnsureCoreWebView2Async(_webViewEnvironment);
        }
        catch (Exception ex)
        {
            try { FullscreenHtmlHost.Children.Remove(webView); } catch { /* 释放流程忽略 */ }
            try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
            ViewModel?.SetHtmlPreviewUnavailable("全屏 HTML 预览启动失败，已改用纯文本显示：" + ex.Message);
            return;
        }
        if (!_fullscreen || _detached)
        {
            try { FullscreenHtmlHost.Children.Remove(webView); } catch { /* 释放流程忽略 */ }
            try { webView.Dispose(); } catch { /* 释放流程忽略 */ }
            return;
        }

        ConfigureCore(webView);
        _fullscreenWebView = webView;
        ApplyZoom();                 // 新内核默认 100%，把我们工具栏里设的百分比套上去
        RenderFullscreenHtml();
    }

    private void RenderFullscreenHtml()
    {
        var viewModel = ViewModel;
        var webView = _fullscreenWebView;
        if (viewModel is null || webView?.CoreWebView2 is null) return;
        if (!viewModel.ShowHtmlPreview || string.IsNullOrWhiteSpace(viewModel.MessageHtml)) return;
        if (string.Equals(_lastFullscreenRenderedHtml, viewModel.MessageHtml, StringComparison.Ordinal)) return;

        _lastFullscreenRenderedHtml = viewModel.MessageHtml;
        try
        {
            webView.CoreWebView2.NavigateToString(viewModel.MessageHtml);
        }
        catch (Exception ex)
        {
            viewModel.SetHtmlPreviewUnavailable("全屏 HTML 渲染失败，已改用纯文本显示：" + ex.Message);
        }
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
        // 关掉 WebView2 自带的 Ctrl+滚轮缩放：全屏里统一由工具栏 / Ctrl+滚轮改 ZoomFactor，
        // 否则两套缩放叠加，百分比输入框和实际显示会不一致
        settings.IsZoomControlEnabled = false;

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
        _lastFullscreenRenderedHtml = null;
        _webViewEnvironment = null;

        var fullscreenWebView = _fullscreenWebView;
        _fullscreenWebView = null;
        if (fullscreenWebView is not null)
        {
            try { FullscreenHtmlHost.Children.Remove(fullscreenWebView); } catch { /* 释放流程忽略 */ }
            try { fullscreenWebView.Dispose(); } catch { /* 释放流程忽略 */ }
        }

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
