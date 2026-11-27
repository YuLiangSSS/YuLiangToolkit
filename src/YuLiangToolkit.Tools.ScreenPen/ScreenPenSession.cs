using System.Windows;
using System.Windows.Threading;
using YuLiangToolkit.Core.Input;
using YuLiangToolkit.Tools.ScreenPen.Views;

namespace YuLiangToolkit.Tools.ScreenPen;

/// <summary>整个进程唯一的屏幕画笔会话：持有覆盖层 / 工具栏，页面关掉也继续工作。</summary>
public sealed class ScreenPenSession
{
    public static ScreenPenSession Current { get; } = new();

    private ScreenPenConfig? _config;
    private ScreenPenOverlayWindow? _overlay;
    private ScreenPenToolbarWindow? _toolbar;
    private bool _initialized;
    private bool _shutdown;
    private bool _screenshotActive;

    private ScreenPenSession() { }

    public event EventHandler? StateChanged;
    public event EventHandler? AnnotationChanged;

    public ScreenPenConfig? Config => _config;
    public bool IsOverlayVisible => _overlay?.IsVisible == true;
    public bool IsDrawingMode => _overlay?.IsDrawingMode ?? true;
    public int AnnotationCount => _overlay?.AnnotationCount ?? 0;

    public void Initialize(ScreenPenConfig config)
    {
        if (_initialized) return;
        _initialized = true;
        _config = config;
        config.PropertyChanged += OnConfigChanged;
        ScreenOverlayState.ScreenshotActiveChanged += OnScreenshotActiveChanged;

        if (Application.Current is { } app)
        {
            app.Exit += OnApplicationExit;
            if (app.MainWindow is { } window) window.Closed += OnMainWindowClosed;
        }

        if (config.StartVisible)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Show()), DispatcherPriority.ApplicationIdle);
        }
    }

    public void Show(bool? showToolbar = null)
    {
        if (_shutdown || _config is null) return;
        var app = Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) ShowCore(showToolbar);
        else app.Dispatcher.Invoke(() => ShowCore(showToolbar));
    }

    private void ShowCore(bool? showToolbar)
    {
        if (_config is null) return;

        if (_overlay is null)
        {
            _overlay = new ScreenPenOverlayWindow(_config)
            {
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
            };
            _overlay.AnnotationChanged += (_, _) => AnnotationChanged?.Invoke(this, EventArgs.Empty);
            _overlay.Show();
            _overlay.SetDrawingMode(true);
        }
        else if (!_overlay.IsVisible)
        {
            _overlay.Show();
        }

        var shouldShowToolbar = showToolbar ?? _config.ShowToolbar;
        if (shouldShowToolbar) EnsureToolbar().Show();
        else _toolbar?.Hide();

        if (_screenshotActive) SetScreenshotActive(true);
        RaiseState();
    }

    private ScreenPenToolbarWindow EnsureToolbar()
    {
        if (_toolbar is not null) return _toolbar;

        _toolbar = new ScreenPenToolbarWindow(
            _config!,
            tool =>
            {
                _config!.ActiveTool = tool;
                _overlay?.SetDrawingMode(true);
                _toolbar?.SyncToolState();
                RaiseState();
            },
            () => Undo(),
            () => Redo(),
            Clear,
            Hide,
            () =>
            {
                _toolbar?.Hide();
                RaiseState();
            });

        var area = SystemParameters.WorkArea;
        _toolbar.Left = Math.Max(area.Left + 8, area.Right - _toolbar.Width - 24);
        _toolbar.Top = area.Top + Math.Max(80, area.Height * 0.16);
        return _toolbar;
    }

    public void Hide()
    {
        _toolbar?.Hide();
        _overlay?.Hide();
        RaiseState();
    }

    public void ToggleVisible()
    {
        if (IsOverlayVisible) Hide();
        else Show();
    }

    public void ToggleDrawingMode()
    {
        if (_overlay is null || !_overlay.IsVisible) Show();
        if (_overlay is null) return;
        _overlay.SetDrawingMode(!_overlay.IsDrawingMode);
        RaiseState();
    }

    public void SetTool(string tool)
    {
        if (_config is null) return;
        _config.ActiveTool = tool;
        _overlay?.SetDrawingMode(true);
        _toolbar?.SyncToolState();
        RaiseState();
    }

    public void Clear()
    {
        _overlay?.Clear();
        AnnotationChanged?.Invoke(this, EventArgs.Empty);
        RaiseState();
    }

    public void SetBackdrop(string backdrop)
    {
        if (_config is null) return;
        _config.Backdrop = backdrop;
        _overlay?.SetBackdrop(_config.Backdrop);
        RaiseState();
    }

    public bool Undo()
    {
        var result = _overlay?.Undo() == true;
        if (result) AnnotationChanged?.Invoke(this, EventArgs.Empty);
        RaiseState();
        return result;
    }

    public bool Redo()
    {
        var result = _overlay?.Redo() == true;
        if (result) AnnotationChanged?.Invoke(this, EventArgs.Empty);
        RaiseState();
        return result;
    }

    public void RefreshColors() => _toolbar?.RefreshColors();

    private void OnConfigChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_config is null) return;
        switch (e.PropertyName)
        {
            case nameof(ScreenPenConfig.ShowToolbar):
                if (_config.ShowToolbar && IsOverlayVisible && !_screenshotActive) EnsureToolbar().Show();
                else if (!_config.ShowToolbar) _toolbar?.Hide();
                break;
            case nameof(ScreenPenConfig.PaletteColors):
                _toolbar?.RefreshColors();
                break;
            case nameof(ScreenPenConfig.Backdrop):
                _overlay?.SetBackdrop(_config.Backdrop);
                break;
        }
        RaiseState();
    }

    private void OnScreenshotActiveChanged(object? sender, EventArgs e) => SetScreenshotActive(ScreenOverlayState.IsScreenshotActive);

    private void SetScreenshotActive(bool active)
    {
        _screenshotActive = active;
        if (_overlay is null) return;
        if (active)
        {
            _toolbar?.Hide();
            _overlay.SetSuspended(true);
        }
        else
        {
            _overlay.SetSuspended(false);
            if (_config?.ShowToolbar == true) EnsureToolbar().Show();
        }
        RaiseState();
    }

    private void RaiseState() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Shutdown();
    private void OnMainWindowClosed(object? sender, EventArgs e) => Shutdown();

    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        try
        {
            if (Application.Current is { } app)
            {
                app.Exit -= OnApplicationExit;
                if (app.MainWindow is { } window) window.Closed -= OnMainWindowClosed;
            }
            ScreenOverlayState.ScreenshotActiveChanged -= OnScreenshotActiveChanged;
            if (_config is not null) _config.PropertyChanged -= OnConfigChanged;
        }
        catch { /* 退出流程忽略 */ }

        try { _toolbar?.Close(); } catch { }
        try { _overlay?.Close(); } catch { }
        _toolbar = null;
        _overlay = null;
    }
}
