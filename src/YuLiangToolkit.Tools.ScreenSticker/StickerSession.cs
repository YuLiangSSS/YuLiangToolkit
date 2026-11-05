using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.ScreenSticker.Views;

namespace YuLiangToolkit.Tools.ScreenSticker;

/// <summary>
/// 「屏幕贴纸」的后台会话（整个进程一个）：管理所有贴纸窗口的存活、层级与持久化。
///
/// <para><b>为什么不是普通 ViewModel 生命周期</b>：贴纸窗口要在用户切到别的工具页、
/// 甚至把主窗口最小化之后继续待在桌面上；所以窗口归这个会话所有，工具页只是它的「管理面板」。
/// 这与 KPS 的 Overlay / Live2D 桌宠同一套思路（见各自 Session 的注释）。</para>
///
/// <para><b>生命周期</b>：</para>
/// <list type="bullet">
///   <item>启动时（工具被 <c>ToolDiscovery</c> 发现）如果配置里开着「启动时自动恢复」，会在
///         Dispatcher 空闲后调 <see cref="RestoreFromConfig"/> 把上次的贴纸放回来；</item>
///   <item>主窗口关闭 / 程序退出时 <see cref="Shutdown"/> 关掉所有贴纸窗口
///         —— 否则「最后一个窗口关闭」永远不成立，进程退不掉（KPS 踩过这个坑）；</item>
///   <item>平时窗口位置 / 大小 / 置顶改了 → <see cref="SaveSoon"/> 防抖写盘（不依赖工具页开着）。</item>
/// </list>
/// </summary>
public sealed class StickerSession
{
    /// <summary>整个进程唯一的会话。</summary>
    public static StickerSession Current { get; } = new();

    private readonly List<StickerWindow> _windows = new();

    private ScreenStickerConfig? _config;
    private ToolConfigStore? _store;
    private DispatcherTimer? _saveTimer;
    private bool _initialized;
    private bool _shutdown;

    private StickerSession()
    {
    }

    /// <summary>贴纸列表 / 某张的置顶 / 层级变了（工具页据此刷新列表）。</summary>
    public event EventHandler? StickersChanged;

    /// <summary>当前桌面上的贴纸窗口（顺序 = 层级，0 最靠下）。</summary>
    public IReadOnlyList<StickerWindow> Windows => _windows;

    /// <summary>已经加载的配置（工具页还没打开过时可能为 null；先调 <see cref="EnsureConfig"/>）。</summary>
    public ScreenStickerConfig? Config => _config;

    /// <summary>配置里保存的贴纸条数（含因「启动不恢复」而当前没显示的）。</summary>
    public int SavedStickerCount => _config?.Stickers?.Count ?? 0;

    /// <summary>最近一次操作失败的原因（成功时清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>是否已经进入退出流程。</summary>
    public bool IsShutdown => _shutdown;

    /// <summary>新贴纸默认摆放用主屏工作区（多显示器时也先贴在主屏）。</summary>
    public static Rect WorkArea => SystemParameters.WorkArea;

    /// <summary>贴纸允许待的屏幕范围（虚拟屏幕：多显示器时代码里不要假设左上角是 0,0）。</summary>
    public static Rect VirtualScreenBounds => new(
        SystemParameters.VirtualScreenLeft,
        SystemParameters.VirtualScreenTop,
        SystemParameters.VirtualScreenWidth,
        SystemParameters.VirtualScreenHeight);

    // ===================== 配置 / 生命周期 =====================

    /// <summary>取（必要时加载）配置；首次调用会接上程序退出钩子。</summary>
    public ScreenStickerConfig EnsureConfig()
    {
        if (_config is null)
        {
            _store = ToolConfigStore.For(ScreenStickerConfig.ToolId);
            _config = _store.Load<ScreenStickerConfig>();
            _config.Stickers ??= new List<StickerEntry>();
            _config.RecentEntries ??= new List<RecentFileEntry>();
            _config.RecentEntries.RemoveAll(entry => !StickerImage.CanOpen(entry.Path));
            EnsureInitialized();
        }
        return _config;
    }

    /// <summary>在工具页 / 启动恢复之前先确保配置可用（页面直接绑 <see cref="Config"/> 时用）。</summary>
    public void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            if (Application.Current is { } app)
            {
                app.Exit += OnApplicationExit;
                if (app.MainWindow is { } window) window.Closed += OnMainWindowClosed;
            }
        }
        catch { /* 无 Application（理论上不会发生）忽略 */ }

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer?.Stop();
            SaveNow();
        };
    }

    /// <summary>程序退出 / 主窗口关闭：关掉所有贴纸窗口（保留配置记录，下次启动恢复）。</summary>
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
        }
        catch { /* 退出流程忽略 */ }

        try { _saveTimer?.Stop(); } catch { /* 忽略 */ }

        foreach (var window in _windows.ToList())
        {
            try { window.CloseForShutdown(); } catch { /* 单张失败不影响其它 */ }
        }
        _windows.Clear();

        SaveNow();
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Shutdown();

    private void OnMainWindowClosed(object? sender, EventArgs e) => Shutdown();

    // ===================== 新增 / 恢复 / 关闭 =====================

    /// <summary>
    /// 把一张图片贴到桌面上（多选时调用方对每个路径各调一次）。
    /// 失败返回 null，原因写在 <see cref="LastError"/>（调用方给用户提示，不弹窗）。
    /// </summary>
    /// <param name="path">图片路径。</param>
    /// <param name="topmost">null = 用配置里的「新贴纸默认置顶」。</param>
    public StickerWindow? AddSticker(string? path, bool? topmost = null)
    {
        LastError = null;
        if (_shutdown) { LastError = "程序正在退出，贴纸已停止工作"; return null; }

        var full = ScreenStickerConfig.NormalizePath(path);
        if (full.Length == 0 || !File.Exists(full)) { LastError = "找不到这张图片（可能已被移动或删除）"; return null; }
        if (!StickerImage.IsSupportedExtension(full)) { LastError = "不支持的图片格式"; return null; }

        var image = StickerImage.Load(full);
        if (image is null) { LastError = "图片解码失败（文件可能损坏）"; return null; }

        var config = EnsureConfig();
        var workArea = WorkArea;
        var aspect = StickerGeometry.AspectOf(image.PixelWidth, image.PixelHeight);
        var size = StickerGeometry.FitInside(workArea, config.DefaultWidthEffective, aspect);
        var placement = StickerGeometry.PlacementForNew(workArea, size, _windows.Count);

        var entry = new StickerEntry
        {
            Path = full,
            Left = placement.Left,
            Top = placement.Top,
            Width = placement.Width,
            Height = placement.Height,
            Topmost = topmost ?? config.DefaultTopmost,
            CreatedUtc = DateTime.UtcNow,
        };
        config.Stickers.Add(entry);

        var window = CreateWindow(entry, image);
        window.Show();
        window.BringToFront();

        config.RememberRecent(full);
        SaveSoon();
        RaiseStickersChanged();
        return window;
    }

    /// <summary>
    /// 按配置把「还没显示」的贴纸放回桌面（启动自动恢复 / 工具页「恢复上次贴纸」按钮）。
    /// 图片已经不存在的记录会被顺手删掉。返回恢复了几张。
    /// </summary>
    public int RestoreFromConfig()
    {
        if (_shutdown) return 0;

        var config = EnsureConfig();
        var removed = 0;
        var restored = 0;

        foreach (var entry in config.Stickers.ToList())
        {
            if (_windows.Any(window => ReferenceEquals(window.Entry, entry))) continue;

            var full = ScreenStickerConfig.NormalizePath(entry.Path);
            if (full.Length == 0 || !File.Exists(full) || !StickerImage.IsSupportedExtension(full))
            {
                config.Stickers.Remove(entry);
                removed++;
                continue;
            }

            var image = StickerImage.Load(full);
            if (image is null)
            {
                config.Stickers.Remove(entry);
                removed++;
                continue;
            }

            entry.Path = full;
            var window = CreateWindow(entry, image);
            restored++;
            window.Show();
        }

        ApplyZOrder();
        if (restored > 0 || removed > 0)
        {
            SaveSoon();
            RaiseStickersChanged();
        }
        return restored;
    }

    private StickerWindow CreateWindow(StickerEntry entry, BitmapSource image)
    {
        var window = new StickerWindow(this, entry, image);
        _windows.Add(window);
        return window;
    }

    /// <summary>关闭一张（用户主动关 = 从配置里删掉，下次启动不再恢复）。</summary>
    public void RemoveSticker(StickerWindow window)
    {
        try { window.Close(); } catch { /* 忽略 */ }
    }

    /// <summary>关闭全部贴纸并清空配置记录（工具页「全部关闭」）。</summary>
    public void CloseAllStickers()
    {
        if (_config is not null) _config.Stickers.Clear();

        foreach (var window in _windows.ToList())
        {
            try { window.CloseForShutdown(); } catch { /* 忽略 */ }
        }
        _windows.Clear();

        SaveSoon();
        RaiseStickersChanged();
    }

    // ===================== 置顶 / 层级 =====================

    /// <summary>设置某张贴纸是否强制置顶。</summary>
    public void SetTopmost(StickerWindow window, bool topmost)
    {
        window.Entry.Topmost = topmost;
        window.ApplyTopmost(topmost);
        SaveSoon();
        RaiseStickersChanged();
    }

    /// <summary>切换某张贴纸的强制置顶。</summary>
    public void ToggleTopmost(StickerWindow window) => SetTopmost(window, !window.Entry.Topmost);

    /// <summary>层级上移一层；已经在最上层返回 false。</summary>
    public bool MoveStickerUp(StickerWindow window) => ApplyLayerChange(StickerLayerOrder.MoveUp(EnsureConfig().Stickers, window.Entry));

    /// <summary>层级下移一层；已经在最下层返回 false。</summary>
    public bool MoveStickerDown(StickerWindow window) => ApplyLayerChange(StickerLayerOrder.MoveDown(EnsureConfig().Stickers, window.Entry));

    /// <summary>置于最上层。</summary>
    public bool MoveStickerToTop(StickerWindow window) => ApplyLayerChange(StickerLayerOrder.MoveToTop(EnsureConfig().Stickers, window.Entry));

    /// <summary>置于最下层。</summary>
    public bool MoveStickerToBottom(StickerWindow window) => ApplyLayerChange(StickerLayerOrder.MoveToBottom(EnsureConfig().Stickers, window.Entry));

    /// <summary>把所有贴纸按配置顺序从下往上重新排一遍 z-order（改了层级 / 恢复之后调用）。</summary>
    public void ApplyZOrder()
    {
        foreach (var window in _windows)
        {
            try { window.BringToFront(); } catch { /* 单张失败不影响其它 */ }
        }
    }

    private bool ApplyLayerChange(bool changed)
    {
        if (!changed) return false;
        ReorderWindowsFromConfig();
        ApplyZOrder();
        SaveSoon();
        RaiseStickersChanged();
        return true;
    }

    /// <summary>按配置里的顺序重排窗口列表（配置是层级唯一来源）。</summary>
    private void ReorderWindowsFromConfig()
    {
        if (_config is null) return;

        var index = new Dictionary<StickerEntry, int>();
        for (var i = 0; i < _config.Stickers.Count; i++) index[_config.Stickers[i]] = i;
        _windows.Sort((a, b) =>
            index.GetValueOrDefault(a.Entry, int.MaxValue).CompareTo(index.GetValueOrDefault(b.Entry, int.MaxValue)));
    }

    /// <summary>把某张贴纸宽度重置成配置里的默认宽度（中心点不动）。</summary>
    public void ResetStickerZoom(StickerWindow window) => window.ResetZoom(EnsureConfig().DefaultWidthEffective);

    // ===================== 窗口回调 =====================

    /// <summary>窗口位置 / 大小变了（拖动 / 滚轮 / 手柄）→ 防抖落盘。</summary>
    public void NotifyWindowGeometryChanged(StickerWindow window) => SaveSoon();

    /// <summary>
    /// 某张贴纸被用户点中 / 激活：Windows 已经把它抬到普通窗口最上面，
    /// 这里把它的层级同步到列表末尾（工具页看到的顺序和桌面一致）。
    /// </summary>
    public void NotifyWindowActivated(StickerWindow window)
    {
        if (_shutdown || _config is null) return;
        if (!StickerLayerOrder.MoveToTop(_config.Stickers, window.Entry)) return;
        ReorderWindowsFromConfig();
        SaveSoon();
        RaiseStickersChanged();
    }

    /// <summary>窗口因为「程序退出 / 关闭全部」而关闭：只从窗口列表里移除，保留配置记录。</summary>
    public void NotifyWindowClosedKeepEntry(StickerWindow window)
    {
        _windows.Remove(window);
        RaiseStickersChanged();
    }

    /// <summary>窗口被用户自己关掉：从窗口列表与配置记录里一起移除。</summary>
    public void NotifyWindowClosedByUser(StickerWindow window)
    {
        _windows.Remove(window);
        _config?.Stickers.Remove(window.Entry);
        SaveSoon();
        RaiseStickersChanged();
    }

    // ===================== 保存 =====================

    /// <summary>防抖保存（0.5 秒合并；拖窗口时不会每一帧都写盘）。</summary>
    public void SaveSoon()
    {
        if (_shutdown) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            SaveNow();
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(SaveSoon));
            return;
        }

        if (_saveTimer is null)
        {
            SaveNow();
            return;
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>立刻写盘（程序退出 / 工具页关闭时用）。</summary>
    public void SaveNow()
    {
        try { _store?.SaveNow(); } catch { /* 保存失败不影响贴纸 */ }
    }

    private void RaiseStickersChanged()
    {
        if (_shutdown) return;
        try { StickersChanged?.Invoke(this, EventArgs.Empty); } catch { /* 订阅方异常不影响窗口 */ }
    }
}
