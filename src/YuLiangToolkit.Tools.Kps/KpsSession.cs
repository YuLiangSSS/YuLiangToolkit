using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Kps.Services;
using YuLiangToolkit.Tools.Kps.Statistics;
using YuLiangToolkit.Tools.Kps.ViewModels;
using YuLiangToolkit.Tools.Kps.Views;

namespace YuLiangToolkit.Tools.Kps;

/// <summary>
/// KPS 工具的后台会话（整个进程一个）：把「全局键盘输入 → 统计引擎 → 键位格子 / Overlay」串起来，
/// 并管理 Overlay 窗口与全局热键的存活。
///
/// <para><b>为什么不是普通 ToolViewModel 生命周期</b>：KPS 的 Overlay 要在用户切到别的工具页、
/// 甚至把主窗口最小化之后继续工作（一边打游戏一边显示按键）。所以监听 / 统计 / Overlay 放在这个
/// 会话里，工具页面只是它的「设置面板」：页面关掉时如果 Overlay 还开着，会话继续跑；
/// 只有页面关掉且 Overlay 也关了，才停下来。</para>
///
/// <para><b>线程</b>：所有公开方法都应在 UI 线程调用（VM 命令、全局热键回调、Raw Input 事件、定时器
/// 都在 UI 线程）。配置自动保存由 <see cref="ToolConfigStore"/> 负责；页面关闭后会重新
/// <see cref="ToolConfigStore.StartWatching"/>，保证后台改的 Overlay 位置也能落盘。</para>
/// </summary>
public sealed class KpsSession
{
    /// <summary>全局唯一会话。</summary>
    public static KpsSession Current { get; } = new();

    // 热键 Id（同一个窗口里唯一即可）
    private const int HotkeyToggleOverlay = 101;
    private const int HotkeyToggleLock = 102;
    private const int HotkeyResetStats = 103;
    private const int HotkeyToggleListening = 104;

    private KpsViewModel? _vm;
    private KpsConfig? _cfg;
    private ToolConfigStore? _store;

    private KpsStatistics _stats = new();
    private RawInputKeyboard? _input;
    private GlobalHotkeyManager? _hotkeys;
    private KeySoundPlayer? _sound;
    private OverlayWindow? _overlay;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _demoTimer;
    private DispatcherTimer? _demoReleaseTimer;

    private readonly HashSet<int> _heldKeys = new();
    private readonly List<string> _hotkeyIssues = new();
    private Action<int>? _capture;

    private bool _initialized;
    private bool _pageActive;
    private bool _listening;
    private bool _userPaused;
    private bool _shutdown;
    private int _tickCount;

    private KpsSession()
    {
    }

    // ===================== 对外状态（VM 显示用） =====================

    /// <summary>是否正在监听全局键盘。</summary>
    public bool IsListening => _listening;

    /// <summary>输入后端名（"Raw Input" / "低级键盘钩子" / "未启动"）。</summary>
    public string BackendName => _input?.BackendName ?? "未启动";

    /// <summary>输入启动失败原因（成功为 null）。</summary>
    public string? InputError => _input?.LastError;

    /// <summary>Overlay 是否正在显示。</summary>
    public bool IsOverlayVisible => _overlay is not null;

    /// <summary>是否正在等待用户按键（改键 / 设置热键）。</summary>
    public bool IsCapturing => _capture is not null;

    /// <summary>当前按住的虚拟键码（热键捕获时判断修饰键用）。</summary>
    public IReadOnlyCollection<int> HeldKeys => _heldKeys;

    /// <summary>全局热键注册过程中的问题（空 = 全部正常）。</summary>
    public IReadOnlyList<string> HotkeyIssues => _hotkeyIssues;

    /// <summary>当前会话的统计引擎（VM 查单键会话计数用）。</summary>
    public KpsStatistics Statistics => _stats;

    // ===================== 页面生命周期 =====================

    /// <summary>取（或创建）工具页面的 ViewModel：整个进程只建一次，页面重开时复用同一份（统计 / 格子状态都在）。</summary>
    public KpsViewModel EnsureViewModel()
    {
        _vm ??= new KpsViewModel();
        return _vm;
    }

    /// <summary>工具页面打开：恢复配置保存、按设置开始监听 / 显示 Overlay。</summary>
    public void Activate(KpsViewModel vm)
    {
        if (_shutdown) return;
        _vm = vm;
        _cfg = vm.Cfg;
        _store = vm.ConfigStore;
        EnsureInitialized();

        try { vm.NotifyOpened(); } catch { /* 恢复监听失败不影响使用 */ }
        _store.StartWatching();
        _pageActive = true;

        vm.RefreshFromConfig();
        RefreshHotkeys();
        ApplyStatsSettings();
        ApplySoundSettings();

        if (_cfg.AutoStartListening && !_userPaused) StartListening(userInitiated: false);
        else UpdateRuntimeState();

        // 开发调试：截图 / 冒烟用（YLT_KPS_DEMO 造统计假数据；YLT_KPS_SHOT_OVERLAY 截 Overlay 后退出）
        var demo = Environment.GetEnvironmentVariable("YLT_KPS_DEMO");
        if (!string.IsNullOrWhiteSpace(demo)) SeedDemo();
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YLT_KPS_SHOT_OVERLAY")))
        {
            _cfg.OverlayEnabled = true;
        }

        if (_cfg.OverlayEnabled) ShowOverlay();
        else UpdateRuntimeState();

        UpdateTimer();
        ApplyOverlayConfig();
    }

    /// <summary>工具页面关闭（切走 / 回主页）。Overlay 还开着就继续跑，否则完全停下。</summary>
    public void Deactivate()
    {
        _pageActive = false;
        if (_capture is not null) CancelKeyCapture();

        if (_cfg is { OverlayEnabled: true } && _overlay is not null)
        {
            // Overlay 还要继续工作：页面关掉后重新开始自动保存（基类 NotifyClosed 刚停掉它）
            try { _store?.StartWatching(); } catch { /* 忽略 */ }
            _store?.SaveNow();
            UpdateTimer();
            UpdateRuntimeState();
            return;
        }

        SuspendAll();
    }

    /// <summary>程序退出：停监听、关 Overlay、把计数器落盘（不改变 OverlayEnabled，下次启动恢复上次显示状态）。</summary>
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

        try { StopDemo(); } catch { /* 忽略 */ }
        try { CloseOverlayInternal(); } catch { /* 忽略 */ }
        try { StopListeningInternal(); } catch { /* 忽略 */ }
        try { UnregisterHotkeys(); } catch { /* 忽略 */ }
        try { _timer?.Stop(); } catch { /* 忽略 */ }
        try { FoldCountersAndReset(); } catch { /* 忽略 */ }
        try { _store?.SaveNow(); } catch { /* 忽略 */ }
        try { _input?.Dispose(); } catch { /* 忽略 */ }
        try { _hotkeys?.Dispose(); } catch { /* 忽略 */ }
        try { _sound?.Dispose(); } catch { /* 忽略 */ }
        _input = null;
        _hotkeys = null;
        _sound = null;
    }

    // ===================== 监听 =====================

    /// <summary>开始监听（失败时 <see cref="InputError"/> 有原因，界面显示）。</summary>
    public void StartListening(bool userInitiated = true)
    {
        if (_shutdown || _cfg is null) return;
        if (userInitiated) _userPaused = false;

        if (_input is null)
        {
            _input = new RawInputKeyboard();
            _input.KeyDown += OnInputKeyDown;
            _input.KeyUp += OnInputKeyUp;
        }

        if (!_input.IsRunning && !_input.Start())
        {
            _listening = false;
            UpdateRuntimeState();
            return;
        }

        _listening = true;
        UpdateTimer();
        UpdateRuntimeState();
    }

    /// <summary>停止监听（用户点「暂停」会记住暂停状态；会话内部停用不记）。</summary>
    public void StopListening(bool userInitiated = true)
    {
        if (userInitiated) _userPaused = true;
        StopListeningInternal();
        UpdateTimer();
        UpdateRuntimeState();
    }

    /// <summary>暂停 / 继续监听。</summary>
    public void ToggleListening()
    {
        if (_listening) StopListening();
        else StartListening();
    }

    /// <summary>把监听切到「继续」状态（打开页面 / 关闭 Overlay 后恢复用）。</summary>
    private void StopListeningInternal()
    {
        _listening = false;
        _heldKeys.Clear();
        try
        {
            if (_input is { IsRunning: true }) _input.Stop();
        }
        catch { /* 忽略 */ }
    }

    // ===================== 输入事件 =====================

    private void OnInputKeyDown(object? sender, KpsKeyEventArgs e)
    {
        var vk = e.VirtualKey;
        if (vk == 0) return;
        _heldKeys.Add(vk);

        // 改键 / 设置热键：吞掉这次按键，不计数
        if (_capture is { } capture)
        {
            _capture = null;
            try { capture(vk); } catch { /* 忽略回调异常 */ }
            UpdateRuntimeState();
            return;
        }

        if (!_listening || _cfg is null) return;
        if (_cfg.CountOnlyBoundKeys && !(_vm?.IsBoundKey(vk) ?? false)) return;

        _stats.RegisterPress(vk);
        try { _sound?.Play(); } catch { /* 键音失败不影响统计 */ }
        _vm?.OnKeyPressed(vk);
    }

    private void OnInputKeyUp(object? sender, KpsKeyEventArgs e)
    {
        var vk = e.VirtualKey;
        if (vk == 0) return;
        _heldKeys.Remove(vk);
        if (!_listening) return;
        _stats.RegisterRelease(vk);
        _vm?.OnKeyReleased(vk);
    }

    // ===================== 统计定时器 =====================

    private void EnsureTimer()
    {
        var need = _pageActive || _overlay is not null || _listening;
        if (need)
        {
            if (_timer is null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
                _timer.Tick += OnStatsTick;
            }
            if (!_timer.IsEnabled) _timer.Start();
        }
        else if (_timer is not null)
        {
            _timer.Stop();
        }
    }

    private void UpdateTimer() => EnsureTimer();

    private void OnStatsTick(object? sender, EventArgs e)
    {
        if (_shutdown) return;
        try
        {
            _stats.Tick();
            _tickCount++;
            // 统计 50ms 一次（峰值 / 平均口径），界面 10Hz 刷新就够（也省 CPU）
            if (_tickCount % 2 == 0) _vm?.ApplySnapshot(_stats.Capture());
            if (Environment.GetEnvironmentVariable("YLT_KPS_SHOT_OVERLAY") is { Length: > 0 }
                && _overlay is not null && _tickCount > 60 && !_overlayCaptureScheduled)
            {
                _overlayCaptureScheduled = true;
                BeginOverlayDebugCapture(Environment.GetEnvironmentVariable("YLT_KPS_SHOT_OVERLAY")!);
            }
        }
        catch { /* 定时器里绝不抛异常 */ }
    }

    /// <summary>重置统计与每键计数器（热键 / 按钮都走这里）。</summary>
    public void ResetStats()
    {
        _stats.Reset();
        _cfg?.SavedKeyCounters.Clear();
        _vm?.RefreshCounts();
        _vm?.ApplySnapshot(_stats.Capture());
        if (_cfg is not null) _cfg.SaveNow();
        _vm?.SetStatus("统计已重置");
    }

    // ===================== 计数器持久化 =====================

    /// <summary>某个键的显示计数 = 已持久化 + 本次会话。</summary>
    public long GetDisplayKeyCount(int virtualKey)
        => GetSavedKeyCount(virtualKey) + _stats.GetKeyPressCount(virtualKey);

    private long GetSavedKeyCount(int virtualKey)
    {
        if (_cfg is null) return 0;
        return _cfg.SavedKeyCounters.TryGetValue(virtualKey.ToString(), out var count) ? count : 0;
    }

    /// <summary>总按键数（所有键的已持久化 + 本次会话；Overlay / 统计卡片显示用）。</summary>
    public long TotalDisplayCount
    {
        get
        {
            var total = _stats.TotalPresses;
            if (_cfg is not null)
            {
                foreach (var value in _cfg.SavedKeyCounters.Values) total += value;
            }
            return total;
        }
    }

    /// <summary>把本次会话的按键计数并进持久化计数器，然后清零会话统计（停用 / 退出 / 重置时调用）。</summary>
    private void FoldCountersAndReset()
    {
        if (_cfg is { PersistKeyCounters: true } && _stats.TotalPresses > 0)
        {
            foreach (var pair in _stats.GetKeyCounts())
            {
                var key = pair.Key.ToString();
                _cfg.SavedKeyCounters[key] = _cfg.SavedKeyCounters.GetValueOrDefault(key) + pair.Value;
            }
        }
        _stats.Reset();
    }

    /// <summary>整个会话停下来：停监听、关 Overlay、注销热键、计数器落盘。</summary>
    private void SuspendAll()
    {
        try { StopListeningInternal(); } catch { /* 忽略 */ }
        try { StopDemo(); } catch { /* 忽略 */ }
        try { CloseOverlayInternal(); } catch { /* 忽略 */ }
        try { UnregisterHotkeys(); } catch { /* 忽略 */ }
        try { _timer?.Stop(); } catch { /* 忽略 */ }
        try { FoldCountersAndReset(); } catch { /* 忽略 */ }
        try
        {
            _store?.SaveNow();
            _store?.Flush();
            _store?.StopWatching();
        }
        catch { /* 忽略 */ }
        UpdateRuntimeState();
    }

    // ===================== Overlay =====================

    /// <summary>显示 Overlay（已显示则什么都不做）。</summary>
    public void ShowOverlay()
    {
        if (_shutdown || _cfg is null || _vm is null) return;
        _cfg.OverlayEnabled = true;
        if (_overlay is null)
        {
            _overlay = new OverlayWindow(_cfg) { DataContext = _vm };
            _overlay.PlacementChanged += OnOverlayPlacementChanged;
            try
            {
                _overlay.Show();
                _overlay.ApplyConfig(_cfg);
                _overlay.SetLocked(_cfg.OverlayLocked);
                _overlay.EnsureOnScreen();
            }
            catch
            {
                // 极端环境（无桌面）下别让工具崩：关掉窗口，状态照实显示
                try { _overlay.Close(); } catch { /* 忽略 */ }
                _overlay = null;
            }
        }

        UpdateTimer();
        UpdateRuntimeState();
    }

    /// <summary>隐藏 Overlay（页面也关着时整个会话随之停下）。</summary>
    public void HideOverlay()
    {
        if (_cfg is not null) _cfg.OverlayEnabled = false;
        CloseOverlayInternal();
        if (!_pageActive)
        {
            SuspendAll();
            return;
        }
        UpdateTimer();
        UpdateRuntimeState();
    }

    /// <summary>显示 / 隐藏 Overlay（热键用）。</summary>
    public void ToggleOverlay()
    {
        if (_overlay is not null) HideOverlay();
        else ShowOverlay();
    }

    /// <summary>锁定 / 解锁 Overlay（锁定 = 鼠标点击穿透）。</summary>
    public void SetOverlayLocked(bool locked)
    {
        if (_cfg is null) return;
        _cfg.OverlayLocked = locked;
        try { _overlay?.SetLocked(locked); } catch { /* 忽略 */ }
        UpdateRuntimeState();
    }

    /// <summary>锁定 / 解锁（热键用）。</summary>
    public void ToggleOverlayLock()
    {
        if (_cfg is null) return;
        SetOverlayLocked(!_cfg.OverlayLocked);
        _vm?.SetStatus(_cfg.OverlayLocked ? "Overlay 已锁定（点击穿透）" : "Overlay 已解锁：可拖动 / 滚轮缩放");
    }

    /// <summary>把 Overlay 放到屏幕中央。</summary>
    public void CenterOverlay()
    {
        if (_overlay is null) return;
        try
        {
            _overlay.CenterOnScreen();
            if (_cfg is not null)
            {
                _cfg.OverlayLeft = _overlay.Left;
                _cfg.OverlayTop = _overlay.Top;
                _cfg.SaveNow();
            }
        }
        catch { /* 忽略 */ }
        _vm?.SetStatus("Overlay 已居中");
    }

    /// <summary>设置变化后刷新 Overlay 外观 / 显隐相关属性。</summary>
    public void ApplyOverlayConfig()
    {
        if (_cfg is null || _overlay is null) return;
        try
        {
            _overlay.ApplyConfig(_cfg);
            _overlay.SetLocked(_cfg.OverlayLocked);
        }
        catch { /* 忽略 */ }
    }

    private void CloseOverlayInternal()
    {
        if (_overlay is null) return;
        var overlay = _overlay;
        _overlay = null;
        try { overlay.PlacementChanged -= OnOverlayPlacementChanged; } catch { /* 忽略 */ }
        try { overlay.Close(); } catch { /* 忽略 */ }
    }

    private void OnOverlayPlacementChanged(object? sender, EventArgs e)
    {
        // 窗口已经把位置 / 缩放写回 config；这里立刻落盘并刷新界面绑定
        try { _store?.SaveNow(); } catch { /* 忽略 */ }
        _vm?.RaiseOverlayBindings();
    }

    // ===================== 热键 =====================

    /// <summary>按当前配置重新注册全局热键（设置里改了热键字符串之后调用）。</summary>
    public void RefreshHotkeys()
    {
        _hotkeyIssues.Clear();
        if (_shutdown || _cfg is null)
        {
            UpdateRuntimeState();
            return;
        }

        if (_hotkeys is null) _hotkeys = new GlobalHotkeyManager();
        _hotkeys.UnregisterAll();

        TryRegisterHotkey(HotkeyToggleOverlay, _cfg.HotkeyToggleOverlay, ToggleOverlay, "显示/隐藏 Overlay");
        TryRegisterHotkey(HotkeyToggleLock, _cfg.HotkeyToggleLock, ToggleOverlayLock, "锁定/解锁 Overlay");
        TryRegisterHotkey(HotkeyResetStats, _cfg.HotkeyResetStats, ResetStats, "重置统计");
        TryRegisterHotkey(HotkeyToggleListening, _cfg.HotkeyToggleListening, ToggleListening, "暂停/继续监听");

        UpdateRuntimeState();
    }

    private void TryRegisterHotkey(int id, string? text, Action action, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return;   // 空 = 未设置，不算错误
        if (!HotkeyGesture.TryParse(text, out var gesture))
        {
            _hotkeyIssues.Add($"{label}「{text}」无法识别");
            return;
        }
        if (_hotkeys is null || !_hotkeys.Register(id, gesture, action))
        {
            _hotkeyIssues.Add($"{label}「{gesture}」注册失败" +
                             (_hotkeys?.LastError is { Length: > 0 } error ? $"：{error}" : ""));
        }
    }

    private void UnregisterHotkeys()
    {
        try { _hotkeys?.UnregisterAll(); } catch { /* 忽略 */ }
    }

    // ===================== 改键 / 热键捕获 =====================

    /// <summary>开始捕获一次按键（改键 / 设置热键）；成功与否看 <see cref="IsCapturing"/>。</summary>
    public void BeginKeyCapture(Action<int> onKey)
    {
        if (_shutdown) return;
        _capture = onKey;
        if (!_listening) StartListening(userInitiated: false);
        if (!_listening) _capture = null;
        UpdateRuntimeState();
    }

    /// <summary>取消捕获。</summary>
    public void CancelKeyCapture()
    {
        _capture = null;
        UpdateRuntimeState();
    }

    /// <summary>当前按住键里的修饰键状态（Win32 MOD_* 位，热键捕获用）。</summary>
    public uint GetHeldModifiers()
    {
        uint modifiers = 0;
        foreach (var vk in _heldKeys)
        {
            modifiers |= vk switch
            {
                0x10 or 0xA0 or 0xA1 => HotkeyGesture.ModShift,
                0x11 or 0xA2 or 0xA3 => HotkeyGesture.ModControl,
                0x12 or 0xA4 or 0xA5 => HotkeyGesture.ModAlt,
                0x5B or 0x5C => HotkeyGesture.ModWin,
                _ => 0u,
            };
        }
        return modifiers;
    }

    // ===================== 设置应用 =====================

    /// <summary>图表秒数变化后调整统计引擎的折线图容量。</summary>
    public void ApplyStatsSettings()
    {
        if (_cfg is null) return;
        _stats.HistoryCapacity = Math.Max(10, _cfg.GraphSeconds * 10);
    }

    /// <summary>键音设置变化后同步到播放器。</summary>
    public void ApplySoundSettings()
    {
        if (_cfg is null) return;
        if (_sound is null) _sound = new KeySoundPlayer();
        _sound.Enabled = _cfg.KeySoundEnabled;
        _sound.Volume = _cfg.KeySoundVolume;
        _sound.SoundFilePath = _cfg.KeySoundPath;
        try { _sound.Reload(); } catch { /* 音效文件坏了不影响统计 */ }
    }

    /// <summary>试听一次键音（设置页「试听」按钮）。</summary>
    public void TestSound()
    {
        ApplySoundSettings();
        if (_sound is null) return;
        var enabled = _sound.Enabled;
        try
        {
            _sound.Enabled = true;
            _sound.Play();
        }
        finally
        {
            _sound.Enabled = enabled;
        }
    }

    /// <summary>立即保存配置（Overlay 拖动 / 锁定等后台操作后调用）。</summary>
    public void SaveConfigNow()
    {
        try { _store?.SaveNow(); } catch { /* 忽略 */ }
    }

    /// <summary>当前统计数据快照（VM 刷新用）。</summary>
    public KpsSnapshot Capture() => _stats.Capture();

    /// <summary>某个键在本次会话里的按下次数（不含持久化部分）。</summary>
    public long GetSessionKeyCount(int virtualKey) => _stats.GetKeyPressCount(virtualKey);

    // ===================== 内部：初始化 / 退出 / 状态 =====================

    private void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        if (_cfg is not null) _stats.HistoryCapacity = Math.Max(10, _cfg.GraphSeconds * 10);

        // 主窗口关闭 = 退出程序：先把 Overlay 关掉，否则「最后一个窗口关闭」永远不成立、进程退不掉。
        try
        {
            if (Application.Current is { } app)
            {
                app.Exit += OnApplicationExit;
                if (app.MainWindow is { } window) window.Closed += OnMainWindowClosed;
            }
        }
        catch { /* 无 Application（理论上不会发生）忽略 */ }
    }

    private void OnApplicationExit(object? sender, ExitEventArgs e) => Shutdown();

    private void OnMainWindowClosed(object? sender, EventArgs e) => Shutdown();

    private void UpdateRuntimeState()
    {
        try { _vm?.UpdateRuntimeState(); } catch { /* 忽略 */ }
    }

    // ===================== 开发调试：截图 / 假数据 =====================

    private bool _overlayCaptureScheduled;

    /// <summary>
    /// 开发调试用（YLT_KPS_DEMO=1）：往每个键塞一些累计计数、并生成 ~30 次/秒的假按键，
    /// 这样截图里 KPS / 折线图 / 键位高亮都有内容。只在调试环境变量存在时生效。
    /// </summary>
    private void SeedDemo()
    {
        if (_vm is null || _cfg is null) return;

        var sample = new long[] { 1284, 976, 1502, 863, 1120, 745, 1346, 992, 655, 1183, 901, 1421, 1330, 802 };
        var counters = new Dictionary<string, long>();
        var index = 0;
        foreach (var row in _cfg.KeyRows)
        {
            foreach (var key in row.Keys)
            {
                if (key.VirtualKey == 0) continue;
                counters[key.VirtualKey.ToString()] = sample[index % sample.Length];
                index++;
            }
        }
        _cfg.SavedKeyCounters = counters;
        _vm.RefreshFromConfig();

        var pressedKeys = _cfg.KeyRows.SelectMany(row => row.Keys)
            .Select(key => key.VirtualKey)
            .Where(vk => vk != 0)
            .Distinct()
            .ToArray();
        if (pressedKeys.Length == 0) return;

        var demoIndex = 0;
        var releasePending = 0;
        var releaseTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(24) };
        _demoReleaseTimer = releaseTimer;
        _demoTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _demoTimer.Tick += (_, _) =>
        {
            if (_shutdown || pressedKeys.Length == 0) return;
            if (_vm is not null) _vm.OnKeyReleased(releasePending);
            var vk = pressedKeys[demoIndex++ % pressedKeys.Length];
            _stats.RegisterPress(vk);
            _vm?.OnKeyPressed(vk);
            releasePending = vk;
            if (!releaseTimer.IsEnabled) releaseTimer.Start();
        };
        releaseTimer.Tick += (_, _) =>
        {
            if (_vm is not null) _vm.OnKeyReleased(releasePending);
            releasePending = 0;
        };
        _demoTimer.Start();
        _vm.SetStatus("演示模式（YLT_KPS_DEMO）：正在生成假按键数据");
    }

    private void StopDemo()
    {
        try { _demoTimer?.Stop(); } catch { /* 忽略 */ }
        try { _demoReleaseTimer?.Stop(); } catch { /* 忽略 */ }
        _demoTimer = null;
        _demoReleaseTimer = null;
    }

    /// <summary>开发调试：把 Overlay 窗口渲染成 PNG（透明背景会垫深色底），然后退出程序。</summary>
    private void BeginOverlayDebugCapture(string path)
    {
        var overlay = _overlay;
        if (overlay is null || _shutdown) return;
        var dispatcher = Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            await Task.Delay(500);
            try
            {
                var width = Math.Max(1, (int)Math.Ceiling(overlay.ActualWidth));
                var height = Math.Max(1, (int)Math.Ceiling(overlay.ActualHeight));
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);

                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen())
                {
                    drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x18, 0x1C, 0x24)), null,
                        new Rect(0, 0, width, height));
                }
                bitmap.Render(background);
                if (overlay.Content is Visual content) bitmap.Render(content);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                await using var stream = File.Create(path);
                encoder.Save(stream);
            }
            catch (Exception ex)
            {
                try { await File.WriteAllTextAsync(path + ".err.txt", ex.ToString()); } catch { /* 忽略 */ }
            }

            try { HideOverlay(); } catch { /* 忽略 */ }
            Application.Current?.Shutdown();
        }));
    }
}
