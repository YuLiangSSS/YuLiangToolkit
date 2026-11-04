using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 一个全局热键（默认只有录制中的 F8 = 停止录制）。
///
/// <para>用 Win32 <c>RegisterHotKey</c> + 一个隐藏消息窗；注册 / 注销都在 UI 线程。
/// 注册失败（例如组合被别的软件占用）不抛异常，返回 false 并把原因写进 <see cref="LastError"/>，
/// 由界面提示用户「改用按钮停止 / 关闭热键选项」。</para>
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    /// <summary>Win32 虚拟键码：F8。</summary>
    public const int KeyF8 = 0x77;

    private const int WmHotkey = 0x0312;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly int _id;
    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private Action? _callback;
    private bool _registered;
    private bool _disposed;

    public GlobalHotkey(int id) => _id = id;

    /// <summary>当前是否已经注册到系统。</summary>
    public bool IsRegistered => _registered;

    /// <summary>最近一次注册失败的原因（成功时为 null）。</summary>
    public string? LastError { get; private set; }

    /// <summary>注册热键（同一实例重复注册会先注销旧的）。</summary>
    public bool Register(int virtualKey, Action callback)
    {
        if (_disposed)
        {
            LastError = "热键管理器已释放";
            return false;
        }
        if (callback is null)
        {
            LastError = "热键回调为空";
            return false;
        }

        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            try { return app.Dispatcher.Invoke(() => RegisterCore(virtualKey, callback)); }
            catch (Exception ex)
            {
                LastError = "转到 UI 线程注册热键失败：" + ex.Message;
                return false;
            }
        }

        return RegisterCore(virtualKey, callback);
    }

    private bool RegisterCore(int virtualKey, Action callback)
    {
        try
        {
            if (_registered) Unregister();

            var source = EnsureWindow();
            if (!RegisterHotKey(source.Handle, _id, ModNoRepeat, virtualKey))
            {
                var code = Marshal.GetLastWin32Error();
                LastError = code == ErrorHotkeyAlreadyRegistered
                    ? $"热键被其它程序占用（Win32 错误 {code}）"
                    : $"注册热键失败（Win32 错误 {code}）";
                return false;
            }

            _callback = callback;
            _registered = true;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = "注册热键失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>注销热键（没注册也安全）。</summary>
    public void Unregister()
    {
        if (!_registered) return;
        _registered = false;
        _callback = null;
        try
        {
            var source = _source;
            if (source is not null && source.Handle != IntPtr.Zero) UnregisterHotKey(source.Handle, _id);
        }
        catch { /* 退出流程忽略 */ }
    }

    private HwndSource EnsureWindow()
    {
        if (_source is not null) return _source;

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
        if (msg == WmHotkey && wParam.ToInt32() == _id)
        {
            handled = true;
            try { _callback?.Invoke(); }
            catch { /* 热键回调异常不能漏进消息循环 */ }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();

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
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                dispatcher.Invoke(() => DisposeSource(source, hook));
                return;
            }

            DisposeSource(source, hook);
        }
        catch
        {
            try { DisposeSource(source, hook); } catch { /* 退出流程忽略 */ }
        }
    }

    private static void DisposeSource(HwndSource source, HwndSourceHook? hook)
    {
        try { if (hook is not null) source.RemoveHook(hook); } catch { /* 忽略 */ }
        try { source.Dispose(); } catch { /* 忽略 */ }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
