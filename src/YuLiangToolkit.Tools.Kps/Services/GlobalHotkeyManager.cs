using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// 全局热键注册（Win32 <c>RegisterHotKey</c> + 一个隐藏消息窗口）。热键回调在 UI 线程。
///
/// <para><b>线程</b>：<see cref="Register"/> 应在 UI 线程调用；若有 WPF <see cref="Application"/> 且从后台线程调用，
/// 会自动转到 UI 线程。同一个 Id 重复注册会先注销旧的（换键友好）。</para>
///
/// <para><b>失败处理</b>：不抛异常，返回 false 并写 <see cref="LastError"/>（含 Win32 错误码；
/// 1409 = <c>ERROR_HOTKEY_ALREADY_REGISTERED</c>，说明组合被别的程序占了）。</para>
///
/// <para>注册时带 <c>MOD_NOREPEAT</c>：长按热键只触发一次，避免「显示 / 隐藏 Overlay」这种开关动作被按住狂切。</para>
/// </summary>
public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>Win32 <c>MOD_NOREPEAT</c>：按住热键不重复触发（Win7+）。</summary>
    private const uint ModNoRepeat = 0x4000;

    private const uint ModMask = HotkeyGesture.ModAlt | HotkeyGesture.ModControl
                                 | HotkeyGesture.ModShift | HotkeyGesture.ModWin;

    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly HashSet<int> _registered = new();
    private readonly object _gate = new();

    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private bool _disposed;

    /// <summary>注册失败原因（最近一次）。</summary>
    public string? LastError { get; private set; }

    /// <summary>注册一个热键；成功返回 true（失败原因见 <see cref="LastError"/>）。<paramref name="id"/> 由调用方保证唯一。</summary>
    public bool Register(int id, HotkeyGesture gesture, Action callback)
    {
        if (_disposed)
        {
            LastError = "全局热键管理器已释放，不能继续注册";
            return false;
        }

        if (callback is null)
        {
            LastError = "热键回调为空";
            return false;
        }

        if (gesture.IsEmpty)
        {
            LastError = "未设置热键";
            return false;
        }

        // RegisterHotKey 必须和消息窗口在同一线程注册；有 WPF 应用时回 UI 线程
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            try
            {
                return app.Dispatcher.Invoke(() => RegisterCore(id, gesture, callback));
            }
            catch (Exception ex)
            {
                LastError = "转到 UI 线程注册热键失败：" + ex.Message;
                return false;
            }
        }

        return RegisterCore(id, gesture, callback);
    }

    /// <summary>注销一个热键（没注册过也安全；不抛异常）。</summary>
    public void Unregister(int id)
    {
        bool wasRegistered;
        lock (_gate)
        {
            _callbacks.Remove(id);
            wasRegistered = _registered.Remove(id);
        }

        if (!wasRegistered) return;

        try
        {
            var source = _source;
            if (source is not null && source.Handle != IntPtr.Zero)
                UnregisterHotKey(source.Handle, id);
        }
        catch { /* 退出流程忽略 */ }
    }

    /// <summary>注销全部热键。</summary>
    public void UnregisterAll()
    {
        int[] ids;
        lock (_gate) ids = _registered.ToArray();
        foreach (var id in ids) Unregister(id);
    }

    /// <summary>某个 Id 是否已经成功注册到系统（以 <see cref="Register"/> 返回值为准）。</summary>
    public bool IsRegistered(int id)
    {
        lock (_gate) return _registered.Contains(id);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { UnregisterAll(); }
        catch { /* 退出流程忽略 */ }

        var source = _source;
        var hook = _hook;
        _source = null;
        _hook = null;
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

    /// <summary>消息窗口收到 <c>WM_HOTKEY</c> 时调用（由 Win32 层实现调用）。</summary>
    internal void InvokeCallback(int id)
    {
        Action? callback;
        lock (_gate) _callbacks.TryGetValue(id, out callback);
        if (callback is null) return;

        try { callback(); }
        catch { /* 热键回调异常不能漏进消息循环 */ }
    }

    // ================================================================== 内部实现

    private bool RegisterCore(int id, HotkeyGesture gesture, Action callback)
    {
        try
        {
            // 同一 Id 重复注册：先注销旧的，避免「换键后旧组合还生效」
            Unregister(id);

            var source = EnsureWindow();
            var modifiers = (gesture.Modifiers & ModMask) | ModNoRepeat;
            if (!RegisterHotKey(source.Handle, id, modifiers, gesture.VirtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                LastError = code == ErrorHotkeyAlreadyRegistered
                    ? $"热键 {gesture} 已被其它程序占用（Win32 错误 {code}：ERROR_HOTKEY_ALREADY_REGISTERED）"
                    : $"注册热键 {gesture} 失败（Win32 错误 {code}）";
                return false;
            }

            lock (_gate)
            {
                _callbacks[id] = callback;
                _registered.Add(id);
            }

            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"注册热键 {gesture} 失败：{ex.Message}";
            return false;
        }
    }

    private HwndSource EnsureWindow()
    {
        if (_source is not null) return _source;

        // 隐藏的顶层窗口：收 WM_HOTKEY，不抢焦点、不进任务栏
        var source = new HwndSource(new HwndSourceParameters("YuLiangToolkitKpsHotkeyWindow")
        {
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
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
        if (msg == WM_HOTKEY)
        {
            handled = true;
            InvokeCallback(wParam.ToInt32());
        }

        return IntPtr.Zero;
    }

    private static void DisposeSource(HwndSource source, HwndSourceHook? hook)
    {
        try
        {
            if (hook is not null) source.RemoveHook(hook);
        }
        catch { /* 退出流程忽略 */ }

        try { source.Dispose(); }
        catch { /* 退出流程忽略 */ }
    }

    // ================================================================== Win32 声明

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
