using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace YuLiangToolkit.Core.Services;

/// <summary>
/// 托盘图标右键菜单里的一项。分隔线用 <see cref="Separator"/>；
/// <see cref="IsDefault"/> 的那一项在菜单里加粗（回车默认项）。
/// </summary>
public sealed class TrayMenuItem
{
    private TrayMenuItem(string text, Action? invoke, bool isSeparator, bool isDefault)
    {
        Text = text;
        Invoke = invoke;
        IsSeparator = isSeparator;
        IsDefault = isDefault;
    }

    /// <summary>分隔线。</summary>
    public static TrayMenuItem Separator { get; } = new("", null, isSeparator: true, isDefault: false);

    /// <summary>一条可点击的菜单项。</summary>
    public TrayMenuItem(string text, Action invoke, bool isDefault = false)
        : this(text, invoke, isSeparator: false, isDefault)
    {
    }

    /// <summary>显示文本（分隔线为空）。</summary>
    public string Text { get; }

    /// <summary>点击后执行的动作（在 UI 线程上执行；分隔线为 null）。</summary>
    public Action? Invoke { get; }

    /// <summary>是不是分隔线。</summary>
    public bool IsSeparator { get; }

    /// <summary>是不是默认项（菜单里加粗；目前只做视觉标记，不支持回车触发）。</summary>
    public bool IsDefault { get; }
}

/// <summary>
/// 任务栏右侧（通知区域）的小图标：**手写 Win32 <c>Shell_NotifyIcon</c>，不引 WinForms**。
///
/// <para><b>行为</b>：左键单击 / 双击 → 触发 <see cref="Activated"/>（外壳用来把主窗口叫回前台）；
/// 右键 → 弹原生菜单（<see cref="TrayMenuItem"/> 列表）；鼠标悬停 → 显示 <see cref="Tooltip"/>。
/// 菜单项的动作通过 <see cref="Dispatcher"/> 排队执行，不在 Win32 消息处理里直接跑。</para>
///
/// <para><b>图标从哪来</b>：优先加载 exe 旁边 <c>Assets\app.ico</c>（构建时随程序一起拷过去）；
/// 找不到再从 exe 内嵌图标（csproj 的 <c>&lt;ApplicationIcon&gt;</c>）里取；再不行退回系统默认应用图标。
/// 三档兜底保证「单独拷一个 exe 出来」也不会没有图标。</para>
///
/// <para><b>健壮性</b>：explorer 重启后通知区域会清空，这里监听 <c>TaskbarCreated</c> 广播自动加回去；
/// 首次添加失败（开机自启时任务栏可能还没准备好）会每 2 秒重试、最多 5 次。</para>
///
/// <para>⚠ 必须在有 WPF Dispatcher 的 UI 线程上创建 / 释放（内部建了一个隐藏的顶层窗口收托盘消息，
/// 并依赖 <c>DispatcherTimer</c> 重试）。</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ---------------- Win32 常量 ----------------

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    /// <summary>托盘回调消息（WM_APP 起，避开系统消息）。</summary>
    private const int TrayCallbackMessage = 0x8000 + 0x51;

    private const int WM_NULL = 0x0000;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const int SM_CXSMICON = 49;
    private const int SM_CYSMICON = 50;
    private const int IDI_APPLICATION = 32512;

    // ---------------- 状态 ----------------

    private readonly string _tooltip;
    private readonly Func<IReadOnlyList<TrayMenuItem>>? _menuProvider;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreatedMessage;
    private readonly DispatcherTimer _retryTimer;
    private readonly uint _iconId = 1;

    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _visible;
    private bool _disposed;
    private int _retryCount;

    /// <summary>托盘图标被单击 / 双击（左键）时触发。可能在 UI 线程的消息循环里同步触发，别做重活。</summary>
    public event EventHandler? Activated;

    /// <summary>当前图标是否在通知区域显示着。</summary>
    public bool IsVisible => _visible;

    /// <summary>创建托盘图标并立刻尝试显示。</summary>
    /// <param name="tooltip">鼠标悬停提示。</param>
    /// <param name="menuProvider">右键菜单内容（每次右键时现取，动作里可以引用最新状态）。</param>
    public TrayIcon(string tooltip, Func<IReadOnlyList<TrayMenuItem>>? menuProvider = null)
    {
        _tooltip = string.IsNullOrWhiteSpace(tooltip) ? "YuLiang 工具集" : tooltip;
        _menuProvider = menuProvider;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // 一个**隐藏的顶层窗口**（不是 message-only）：托盘回调消息发到这里，同时也必须能收到
        // explorer 重启时广播的 TaskbarCreated（message-only 窗口收不到广播），
        // 还要能当 SetForegroundWindow 的目标，否则右键菜单点外面不会收起。
        var parameters = new HwndSourceParameters("YuLiangToolkitTrayWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        // 首次添加失败（开机自启时任务栏可能还没就绪）就每 2 秒重试，最多 5 次；
        // explorer 重启后图标会消失，TaskbarCreated 里也会重新 AddIcon()
        _retryTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(2), DispatcherPriority.Background,
            (_, _) => RetryAdd(), _source.Dispatcher);

        (_icon, _ownsIcon) = LoadAppIcon();
        if (!AddIcon()) _retryTimer.Start();
    }

    // ================================================================== 显示 / 隐藏

    private bool AddIcon()
    {
        if (_disposed || _source.Handle == IntPtr.Zero) return false;

        var data = new NotifyIconData
        {
            cbSize = Marshal.SizeOf<NotifyIconData>(),
            hWnd = _source.Handle,
            uID = _iconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _icon,
            szTip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
        if (!Shell_NotifyIcon(NIM_ADD, ref data)) return false;

        _visible = true;
        _retryTimer.Stop();
        return true;
    }

    /// <summary>首次添加失败时（任务栏还没就绪）隔 2 秒重试，最多 5 次。</summary>
    private void RetryAdd()
    {
        if (_disposed || _visible)
        {
            _retryTimer.Stop();
            return;
        }

        if (AddIcon() || ++_retryCount >= 5) _retryTimer.Stop();
    }

    // ================================================================== 消息处理

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TrayCallbackMessage)
        {
            var mouseMessage = (int)((long)lParam & 0xFFFF);
            switch (mouseMessage)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    handled = true;
                    _source.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        new Action(() => Activated?.Invoke(this, EventArgs.Empty)));
                    break;

                case WM_RBUTTONUP:
                    handled = true;
                    // 菜单是阻塞式（TrackPopupMenuEx 自己跑消息循环），排到消息处理之后再做
                    _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(ShowMenu));
                    break;
            }
        }
        else if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
        {
            // explorer 重启：通知区域被清空，重新把图标加回去（图标句柄还在，直接复用）
            _visible = false;
            if (!AddIcon())
            {
                _retryCount = 0;
                _retryTimer.Start();
            }
            handled = true;
        }

        return IntPtr.Zero;
    }

    // ================================================================== 右键菜单

    private void ShowMenu()
    {
        if (_disposed) return;

        var items = _menuProvider?.Invoke();
        if (items is null || items.Count == 0) return;

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        var actions = new Dictionary<int, Action>();
        var nextId = 100;
        try
        {
            foreach (var item in items)
            {
                if (item.IsSeparator)
                {
                    AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, null);
                    continue;
                }

                var id = nextId++;
                if (item.Invoke is not null) actions[id] = item.Invoke;
                AppendMenuW(menu, MF_STRING, new IntPtr(id), item.Text);
                if (item.IsDefault) SetMenuDefaultItem(menu, (uint)id, 0);
            }

            if (actions.Count == 0) return;

            GetCursorPos(out var point);
            // 经典两件套：先给窗口前台权限，菜单关掉后再发一个 WM_NULL —— 否则点菜单外面不会收起
            SetForegroundWindow(_source.Handle);
            var command = TrackPopupMenuEx(
                menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                point.X, point.Y, _source.Handle, IntPtr.Zero);
            PostMessage(_source.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (actions.TryGetValue(command, out var action))
            {
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    // ================================================================== 图标

    /// <summary>三档兜底取托盘图标：程序目录 Assets\app.ico → exe 内嵌图标 → 系统默认应用图标。</summary>
    private static (IntPtr Handle, bool Owns) LoadAppIcon()
    {
        var small = GetSystemMetrics(SM_CXSMICON);
        var big = GetSystemMetrics(SM_CYSMICON);
        if (small <= 0) small = 16;
        if (big <= 0) big = 16;

        // ① 构建时随程序拷贝过来的 ico（publish 也会带上）
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(path))
            {
                var icon = LoadImage(IntPtr.Zero, path, IMAGE_ICON, small, big, LR_LOADFROMFILE);
                if (icon != IntPtr.Zero) return (icon, true);
            }
        }
        catch
        {
            // 读不到就换下一档
        }

        // ② exe 里内嵌的图标（csproj 的 ApplicationIcon）；dotnet run 时可能拿不到，那就走 ③
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                var count = ExtractIconEx(exe, 0, out var large, out var tiny, 1);
                if (count > 0)
                {
                    if (tiny != IntPtr.Zero)
                    {
                        if (large != IntPtr.Zero) DestroyIcon(large);
                        return (tiny, true);
                    }
                    if (large != IntPtr.Zero) return (large, true);
                }
            }
        }
        catch
        {
            // 继续兜底
        }

        // ③ 系统默认应用图标（共享句柄，不能 DestroyIcon）
        return (LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION)), false);
    }

    // ================================================================== 释放

    /// <summary>移除托盘图标、销毁 message-only 窗口和图标句柄（可重复调用）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _retryTimer.Stop();

        if (_visible)
        {
            var data = new NotifyIconData
            {
                cbSize = Marshal.SizeOf<NotifyIconData>(),
                hWnd = _source.Handle,
                uID = _iconId,
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _visible = false;
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();

        if (_ownsIcon && _icon != IntPtr.Zero) DestroyIcon(_icon);
        _icon = IntPtr.Zero;
    }

    // ================================================================== Win32 声明

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uTimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
}
