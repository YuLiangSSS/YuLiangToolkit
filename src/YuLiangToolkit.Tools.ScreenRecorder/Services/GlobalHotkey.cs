using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 全局热键管理器：一个隐藏消息窗 + Win32 <c>RegisterHotKey</c>，可以同时管理多个热键 id
/// （例如「开始 / 停止录制」「截图」各占一个 id）。
///
/// <para><b>线程</b>：注册 / 注销都应在 UI 线程；若已有 WPF <see cref="Application"/> 且从后台线程调用，
/// 会自动转回 UI 线程（<c>RegisterHotKey</c> 必须和消息窗在同一线程注册）。热键回调也在 UI 线程触发。</para>
///
/// <para><b>失败处理</b>：不抛异常；<see cref="Register"/> 返回 false，并通过 <c>error</c> /
/// <see cref="LastError"/> 给出中文原因（含 Win32 错误码；1409 = 组合被别的程序占用）。
/// 调用方可以提示用户换组合键，或改用界面按钮。</para>
///
/// <para>注册时带 <c>MOD_NOREPEAT</c>：长按热键只触发一次，避免「开始 / 停止」这种开关被按住狂切。</para>
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>一个热键 id 注册成功后的记录（组合 + 回调）。</summary>
    private sealed class Entry
    {
        public Entry(HotkeySpec spec, Action callback)
        {
            Spec = spec;
            Callback = callback;
        }

        public HotkeySpec Spec { get; }

        public Action Callback { get; }
    }

    private readonly Dictionary<int, Entry> _entries = new();
    private readonly object _gate = new();

    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private bool _disposed;

    /// <summary>最近一次注册失败的原因（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 注册一个热键（同一个 id 重复注册会先注销旧的，方便「改键」）。
    /// </summary>
    /// <param name="id">热键 id，由调用方保证与其它 id 不冲突。</param>
    /// <param name="spec">热键组合（不能是空手势）。</param>
    /// <param name="callback">按下热键时在 UI 线程回调。</param>
    /// <param name="error">失败原因（成功时为 null）。</param>
    public bool Register(int id, HotkeySpec spec, Action callback, out string? error)
    {
        if (_disposed)
        {
            error = "热键管理器已释放，不能继续注册";
            LastError = error;
            return false;
        }

        if (spec.IsEmpty)
        {
            error = "热键无效：没有主键";
            LastError = error;
            return false;
        }

        if (callback is null)
        {
            error = "热键回调为空";
            LastError = error;
            return false;
        }

        // RegisterHotKey 必须和消息窗在同一线程注册；有 WPF 应用时回 UI 线程
        var dispatcher = ResolveDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                error = "界面线程已退出，无法注册热键";
                LastError = error;
                return false;
            }

            try
            {
                var result = dispatcher.Invoke(() => RegisterCore(id, spec, callback));
                error = result.Error;
                LastError = result.Error;
                return result.Ok;
            }
            catch (Exception ex)
            {
                error = "转到 UI 线程注册热键失败：" + ex.Message;
                LastError = error;
                return false;
            }
        }

        var local = RegisterCore(id, spec, callback);
        error = local.Error;
        LastError = local.Error;
        return local.Ok;
    }

    /// <summary>注销一个热键（没注册过也安全，不抛异常）。</summary>
    public void Unregister(int id)
    {
        var dispatcher = ResolveDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // 界面线程已经退出时窗口随进程回收，这里只清掉登记表
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                ClearEntry(id);
                return;
            }

            try { dispatcher.Invoke(() => UnregisterCore(id)); }
            catch { /* 退出流程忽略 */ }
            return;
        }

        UnregisterCore(id);
    }

    /// <summary>注销全部热键。</summary>
    public void UnregisterAll()
    {
        var dispatcher = ResolveDispatcher();
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                ClearEntries();
                return;
            }

            try { dispatcher.Invoke(UnregisterAllCore); }
            catch { /* 退出流程忽略 */ }
            return;
        }

        UnregisterAllCore();
    }

    /// <summary>某个 id 当前是否已经成功注册到系统。</summary>
    public bool IsRegistered(int id)
    {
        lock (_gate) return _entries.ContainsKey(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { UnregisterAll(); }
        catch { /* 退出流程忽略 */ }

        HwndSource? source = null;
        HwndSourceHook? hook = null;
        lock (_gate)
        {
            source = _source;
            hook = _hook;
            _source = null;
            _hook = null;
            _entries.Clear();
        }

        if (source is null) return;

        try
        {
            var dispatcher = source.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                // 窗口只能在创建它的线程释放；线程已关就让窗口随进程回收
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                dispatcher.Invoke(() => DisposeSource(source, hook));
                return;
            }

            DisposeSource(source, hook);
        }
        catch
        {
            try { DisposeSource(source, hook); }
            catch { /* 退出流程忽略 */ }
        }
    }

    // ================================================================== 内部实现

    /// <summary>确定处理热键的 UI 线程（没有 <see cref="Application"/> 时退回到消息窗自己的线程）。</summary>
    private Dispatcher? ResolveDispatcher()
    {
        var app = Application.Current;
        if (app is not null) return app.Dispatcher;

        lock (_gate) return _source?.Dispatcher;
    }

    private (bool Ok, string? Error) RegisterCore(int id, HotkeySpec spec, Action callback)
    {
        try
        {
            // 同一个 id 重复注册：先注销旧组合，避免「换键后旧组合还生效」
            UnregisterCore(id);

            var source = EnsureWindow();
            var modifiers = (spec.Modifiers & HotkeySpec.ModMask) | ModNoRepeat;
            if (!RegisterHotKey(source.Handle, id, modifiers, spec.VirtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                var message = code == ErrorHotkeyAlreadyRegistered
                    ? $"热键 {spec.DisplayText} 已被其它程序占用（Win32 错误 {code}：ERROR_HOTKEY_ALREADY_REGISTERED）"
                    : $"注册热键 {spec.DisplayText} 失败（Win32 错误 {code}）";
                return (false, message);
            }

            lock (_gate) _entries[id] = new Entry(spec, callback);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"注册热键 {spec.DisplayText} 失败：{ex.Message}");
        }
    }

    private void UnregisterCore(int id)
    {
        Entry? removed = null;
        lock (_gate)
        {
            _entries.TryGetValue(id, out removed);
            _entries.Remove(id);
        }

        if (removed is null) return;

        try
        {
            var source = _source;
            if (source is not null && source.Handle != IntPtr.Zero) UnregisterHotKey(source.Handle, id);
        }
        catch { /* 退出流程忽略 */ }
    }

    private void UnregisterAllCore()
    {
        var ids = new List<int>();
        lock (_gate)
        {
            ids.AddRange(_entries.Keys);
            _entries.Clear();
        }

        try
        {
            var source = _source;
            if (source is null || source.Handle == IntPtr.Zero) return;
            foreach (var id in ids) UnregisterHotKey(source.Handle, id);
        }
        catch { /* 退出流程忽略 */ }
    }

    private void ClearEntry(int id)
    {
        lock (_gate) _entries.Remove(id);
    }

    private void ClearEntries()
    {
        lock (_gate) _entries.Clear();
    }

    private HwndSource EnsureWindow()
    {
        if (_source is not null) return _source;

        // 隐藏的顶层窗口：只收 WM_HOTKEY，不抢焦点、不进任务栏
        var source = new HwndSource(new HwndSourceParameters("YuLiangToolkitScreenRecorderHotkey")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        });
        _hook = WndProc;
        source.AddHook(_hook);
        _source = source;
        return source;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;

        handled = true;

        Entry? entry = null;
        lock (_gate) _entries.TryGetValue(wParam.ToInt32(), out entry);
        if (entry is null) return IntPtr.Zero;

        try { entry.Callback(); }
        catch { /* 热键回调异常不能漏进消息循环 */ }

        return IntPtr.Zero;
    }

    private static void DisposeSource(HwndSource source, HwndSourceHook? hook)
    {
        try { if (hook is not null) source.RemoveHook(hook); } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
    }

    // ================================================================== Win32 声明

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
