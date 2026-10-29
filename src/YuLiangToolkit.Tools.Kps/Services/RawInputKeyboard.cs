using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>一次「真正按下 / 抬起」事件（长按自动重复已在输入层过滤）。</summary>
public sealed class KpsKeyEventArgs : EventArgs
{
    public KpsKeyEventArgs(int virtualKey)
    {
        VirtualKey = virtualKey;
    }

    /// <summary>Windows 虚拟键码。</summary>
    public int VirtualKey { get; }
}

/// <summary>
/// 全局键盘输入监听（Windows Raw Input，<c>RIDEV_INPUTSINK</c> 后台也收得到；失败时退回低级键盘钩子）。
///
/// <para><b>为什么用 Raw Input</b>：很多音游直接用 Raw Input 读键盘，低级钩子会被它们绕过 / 被反作弊拦；
/// DmNote 也是用 Raw Input API（见 docs/tools/kps.md 的来源说明）。</para>
///
/// <para><b>线程</b>：必须<b>在 UI 线程</b> <see cref="Start"/>，事件也在 UI 线程回调（内部隐藏窗口挂在调用线程的消息循环上）。
/// 任意线程可调 <see cref="Stop"/> / <see cref="Dispose"/>（内部把释放隐藏窗口的操作转回创建它的线程）。</para>
///
/// <para><b>自动重复</b>：Windows 长按会重复发 WM_KEYDOWN，但 Raw Input 只报「状态变化」；
/// 这里再用按下集合兜底去重，保证按住不放只算一次按下、抬起时才算一次抬起。</para>
///
/// <para><b>不抢焦点</b>：隐藏窗口 0×0、不 Show、<c>WS_POPUP + WS_EX_TOOLWINDOW</c>，
/// 但<b>不加 RIDEV_NOLEGACY</b> —— 本进程里的 WPF 文本框还要收传统键盘消息。</para>
/// </summary>
public sealed class RawInputKeyboard : IDisposable
{
    // ---------------- 消息 / 常量 ----------------

    private const int WM_INPUT = 0x00FF;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIM_TYPEKEYBOARD = 1;
    private const uint MAPVK_VSC_TO_VK_EX = 3;

    private const ushort RI_KEY_BREAK = 0x01;
    private const ushort RI_KEY_E0 = 0x02;
    private const ushort RI_KEY_E1 = 0x04;

    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_PAUSE = 0x13;
    private const ushort VK_LSHIFT = 0xA0;
    private const ushort VK_RSHIFT = 0xA1;
    private const ushort VK_LCONTROL = 0xA2;
    private const ushort VK_RCONTROL = 0xA3;
    private const ushort VK_LMENU = 0xA4;
    private const ushort VK_RMENU = 0xA5;

    /// <summary>键盘设备（UsagePage=0x01 Generic Desktop，Usage=0x06 Keyboard）。</summary>
    private const ushort KeyboardUsagePage = 0x01;
    private const ushort KeyboardUsage = 0x06;

    private const uint LLKHF_EXTENDED = 0x01;

    // ---------------- 状态 ----------------

    private readonly HashSet<int> _pressed = new();
    private readonly object _gate = new();

    private HwndSource? _source;
    private HwndSourceHook? _hook;
    private IntPtr _hwnd;
    private bool _rawRegistered;

    private LowLevelKeyboardProc? _lowLevelProc;
    private IntPtr _lowLevelHook;

    private bool _disposed;

    /// <summary>真正按下（从「没按」到「按下」的那一次）。</summary>
    public event EventHandler<KpsKeyEventArgs>? KeyDown;

    /// <summary>抬起。</summary>
    public event EventHandler<KpsKeyEventArgs>? KeyUp;

    /// <summary>是否正在监听。</summary>
    public bool IsRunning { get; private set; }

    /// <summary>实际使用的后端名（"Raw Input" / "低级键盘钩子"），给界面显示 / 排错用。</summary>
    public string BackendName { get; private set; } = "未启动";

    /// <summary>最近一次启动失败的原因（成功时清空）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 开始监听；返回是否成功（失败原因见 <see cref="LastError"/>，不抛异常）。
    /// 有 WPF <see cref="Application"/> 时若从后台线程调用，会自动转到 UI 线程启动。
    /// </summary>
    public bool Start()
    {
        if (_disposed)
        {
            LastError = "输入监听已释放，不能再次启动";
            return false;
        }

        if (IsRunning) return true;

        // 隐藏窗口 / 钩子都挂在调用线程的消息循环上：WPF 里必须回到 UI 线程
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            try
            {
                return app.Dispatcher.Invoke(StartCore);
            }
            catch (Exception ex)
            {
                LastError = "转到 UI 线程启动输入监听失败：" + ex.Message;
                return false;
            }
        }

        return StartCore();
    }

    /// <summary>停止监听并释放隐藏窗口 / 钩子（可重复调用；任意线程可调）。</summary>
    public void Stop()
    {
        IsRunning = false;
        BackendName = "未启动";
        lock (_gate) _pressed.Clear();

        // ① 反注册 Raw Input：RIDEV_REMOVE 也是走 RegisterRawInputDevices（hwndTarget 必须为 NULL），
        //    必须在释放窗口之前做，否则窗口没了就反注册不掉。
        try { RemoveRawInputRegistration(); }
        catch { /* 退出流程忽略 */ }

        // ② 摘钩子 + 释放隐藏窗口（HwndSource.Dispose 只能在创建它的线程调用）
        try { DisposeWindow(); }
        catch { /* 退出流程忽略 */ }

        // ③ 低级键盘钩子
        try
        {
            if (_lowLevelHook != IntPtr.Zero) UnhookWindowsHookEx(_lowLevelHook);
        }
        catch { /* 退出流程忽略 */ }
        finally
        {
            _lowLevelHook = IntPtr.Zero;
            _lowLevelProc = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { /* 退出流程忽略 */ }
    }

    /// <summary>当前按住的键（诊断 / 切回界面时同步按下状态用）。</summary>
    public IReadOnlyCollection<int> PressedKeys
    {
        get { lock (_gate) return _pressed.ToArray(); }
    }

    /// <summary>输入层内部：处理一次按下（去重后发事件）。</summary>
    internal void RaiseKeyDown(int virtualKey)
    {
        if (virtualKey == 0 || !IsRunning) return;
        lock (_gate)
        {
            if (!_pressed.Add(virtualKey)) return;
        }

        try { KeyDown?.Invoke(this, new KpsKeyEventArgs(virtualKey)); }
        catch { /* 订阅方异常不能漏进消息循环 */ }
    }

    /// <summary>输入层内部：处理一次抬起。</summary>
    internal void RaiseKeyUp(int virtualKey)
    {
        if (virtualKey == 0) return;
        lock (_gate)
        {
            if (!_pressed.Remove(virtualKey)) return;
        }

        try { KeyUp?.Invoke(this, new KpsKeyEventArgs(virtualKey)); }
        catch { /* 订阅方异常不能漏进消息循环 */ }
    }

    // ================================================================== 启动

    private bool StartCore()
    {
        try
        {
            LastError = null;
            lock (_gate) _pressed.Clear();

            // 隐藏的顶层窗口（WS_POPUP、0×0、不 Show）：Raw Input 的 RIDEV_INPUTSINK 需要一个 hwndTarget
            var source = new HwndSource(new HwndSourceParameters("YuLiangToolkitKpsRawInputWindow")
            {
                WindowStyle = WS_POPUP,
                ExtendedWindowStyle = WS_EX_TOOLWINDOW,
                Width = 0,
                Height = 0,
                PositionX = -32000,
                PositionY = -32000,
            });

            _source = source;
            _hwnd = source.Handle;
            _hook = WndProc;
            source.AddHook(_hook);

            var keyboardDevice = new RAWINPUTDEVICE
            {
                usUsagePage = KeyboardUsagePage,
                usUsage = KeyboardUsage,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _hwnd,
            };

            if (RegisterRawInputDevices(
                    new[] { keyboardDevice }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            {
                _rawRegistered = true;
                BackendName = "Raw Input";
                IsRunning = true;
                return true;
            }

            // Raw Input 注册失败（少见：受限权限 / 特殊会话）→ 退回低级键盘钩子
            var rawErrorCode = Marshal.GetLastWin32Error();
            try { DisposeWindow(); } catch { }

            if (TryInstallLowLevelHook(out var hookError))
            {
                BackendName = "低级键盘钩子";
                IsRunning = true;
                LastError = $"Raw Input 注册失败（Win32 错误 {rawErrorCode}），已退回低级键盘钩子";
                return true;
            }

            BackendName = "未启动";
            LastError = $"Raw Input 注册失败（Win32 错误 {rawErrorCode}），低级键盘钩子也失败（{hookError}）";
            return false;
        }
        catch (Exception ex)
        {
            try { DisposeWindow(); } catch { }
            IsRunning = false;
            BackendName = "未启动";
            LastError = "启动输入监听失败：" + ex.Message;
            return false;
        }
    }

    private bool TryInstallLowLevelHook(out string error)
    {
        error = "";
        try
        {
            // 委托必须用字段保引用，否则被 GC 回收后钩子回调会崩
            _lowLevelProc = LowLevelKeyboardCallback;
            var module = GetModuleHandleW(null);
            _lowLevelHook = SetWindowsHookExW(WH_KEYBOARD_LL, _lowLevelProc, module, 0);
            if (_lowLevelHook != IntPtr.Zero) return true;

            var code = Marshal.GetLastWin32Error();
            error = $"Win32 错误 {code}";
            _lowLevelProc = null;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _lowLevelProc = null;
            return false;
        }
    }

    // ================================================================== 消息处理

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            // 保留 handled = false：交给 DefWindowProc 完成 Raw Input 的系统清理
            try { HandleRawInput(lParam); }
            catch { /* 单帧数据异常不能让消息循环挂掉 */ }
        }

        return IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr lParam)
    {
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        var queried = GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (queried == uint.MaxValue || size == 0) return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize);
            if (read == uint.MaxValue || read == 0) return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEKEYBOARD) return;

            // RAWINPUT 是 header + 联合体；键盘数据紧跟在 header 之后（header 在 x64 上 24 字节、8 字节对齐）
            var keyboard = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buffer, (int)headerSize));
            var virtualKey = NormalizeVirtualKey(keyboard);
            if (virtualKey <= 0) return;

            if ((keyboard.Flags & RI_KEY_BREAK) != 0) RaiseKeyUp(virtualKey);
            else RaiseKeyDown(virtualKey);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Raw Input 的键码归一：左右 Shift / Ctrl / Alt 拆开（左右 Alt 是音游常用键），
    /// <c>VKey</c> 为 0 / 0xFF 时用扫描码 + <c>MapVirtualKey</c> 恢复。
    /// </summary>
    private static int NormalizeVirtualKey(RAWKEYBOARD keyboard)
    {
        var flags = keyboard.Flags;
        var makeCode = keyboard.MakeCode;
        var extended = (flags & RI_KEY_E0) != 0;

        var virtualKey = (int)keyboard.VKey;
        switch (virtualKey)
        {
            case VK_SHIFT:
                // Raw Input 只报「Shift」，按扫描码区分左右
                return makeCode switch
                {
                    0x2A => VK_LSHIFT,
                    0x36 => VK_RSHIFT,
                    _ => VK_SHIFT,
                };
            case VK_CONTROL:
                return extended ? VK_RCONTROL : VK_LCONTROL;
            case VK_MENU:
                return extended ? VK_RMENU : VK_LMENU;
            case 0 or 0xFF:
                // Pause / Break 是 E1 前缀的怪键，扫描码 0x45，映射不回来，直接认
                if ((flags & RI_KEY_E1) != 0 && makeCode == 0x45) return VK_PAUSE;

                uint scan = makeCode;
                if (extended) scan |= 0xE000;
                var mapped = MapVirtualKeyW(scan, MAPVK_VSC_TO_VK_EX);
                if (mapped is > 0 and <= 0xFF) return (int)mapped;
                return 0;
            default:
                return virtualKey;
        }
    }

    private IntPtr LowLevelKeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var message = wParam.ToInt32();
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN) RaiseKeyDown(NormalizeLowLevelKey(data));
                else if (message is WM_KEYUP or WM_SYSKEYUP) RaiseKeyUp(NormalizeLowLevelKey(data));
            }
        }
        catch { /* 回调里异常不能让系统输入链挂掉 */ }

        return CallNextHookEx(_lowLevelHook, nCode, wParam, lParam);
    }

    /// <summary>低级钩子同样把 Shift / Ctrl / Alt 拆成左右键，口径与 Raw Input 一致。</summary>
    private static int NormalizeLowLevelKey(KBDLLHOOKSTRUCT data)
    {
        var virtualKey = (int)data.vkCode;
        var extended = (data.flags & LLKHF_EXTENDED) != 0;
        return virtualKey switch
        {
            VK_SHIFT => data.scanCode switch
            {
                0x2A => VK_LSHIFT,
                0x36 => VK_RSHIFT,
                _ => VK_SHIFT,
            },
            VK_CONTROL => extended ? VK_RCONTROL : VK_LCONTROL,
            VK_MENU => extended ? VK_RMENU : VK_LMENU,
            _ => virtualKey,
        };
    }

    // ================================================================== 释放

    private void RemoveRawInputRegistration()
    {
        if (!_rawRegistered) return;
        _rawRegistered = false;

        // 反注册 Raw Input：同一 UsagePage / Usage + RIDEV_REMOVE，hwndTarget 必须为 NULL
        var removal = new RAWINPUTDEVICE
        {
            usUsagePage = KeyboardUsagePage,
            usUsage = KeyboardUsage,
            dwFlags = RIDEV_REMOVE,
            hwndTarget = IntPtr.Zero,
        };
        RegisterRawInputDevices(new[] { removal }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    private void DisposeWindow()
    {
        var source = _source;
        var hook = _hook;
        _source = null;
        _hook = null;
        _hwnd = IntPtr.Zero;
        if (source is null) return;

        var dispatcher = source.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // HwndSource 只能在创建线程释放；线程已关就没必要了（句柄随进程回收）
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try { dispatcher.Invoke(() => DisposeSource(source, hook)); }
            catch { /* 退出流程忽略 */ }
            return;
        }

        DisposeSource(source, hook);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
