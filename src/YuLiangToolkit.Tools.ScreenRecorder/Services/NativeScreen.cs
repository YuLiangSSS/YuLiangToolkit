using System.Runtime.InteropServices;
using System.Text;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 屏幕 / 窗口相关的 Win32 小封装（只在 Windows 上跑）。
///
/// <para>本工具是 WPF 默认的「系统 DPI 感知」进程：这里返回的矩形统一是**物理像素**，
/// 正好等于 FFmpeg <c>gdigrab</c> 的 <c>offset_x / offset_y / video_size</c> 口径。
/// 区域选择浮窗里用 <see cref="System.Windows.Visual.PointToScreen"/> 把 DIP 换成物理像素再交过来。</para>
/// </summary>
internal static class NativeScreen
{
    /// <summary>一个可录制的顶层窗口。</summary>
    public readonly record struct WindowEntry(long Handle, string Title);

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;

    private const int DwmwaExtendedFrameBounds = 9;
    private const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>主显示器的物理尺寸（从 (0,0) 开始）。</summary>
    public static CaptureRect PrimaryScreen
    {
        get
        {
            var width = GetSystemMetrics(SmCxScreen);
            var height = GetSystemMetrics(SmCyScreen);
            if (width <= 0 || height <= 0) return CaptureRect.Empty;
            return new CaptureRect(0, 0, width, height);
        }
    }

    /// <summary>所有显示器组成的虚拟屏幕（物理像素，多屏时原点可能是负的）。</summary>
    public static CaptureRect VirtualScreen
    {
        get
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0) return PrimaryScreen;
            return new CaptureRect(x, y, width, height);
        }
    }

    /// <summary>
    /// 枚举当前能看到的顶层窗口（有标题、未最小化、不是工具窗），按标题排序去重。
    /// <para>包含本程序自己的窗口（方便录制工具集自己的界面）；排除标题为空的隐藏消息窗。</para>
    /// </summary>
    public static IReadOnlyList<WindowEntry> EnumerateWindows()
    {
        var list = new List<WindowEntry>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    if (IsIconic(hwnd)) return true;

                    var length = GetWindowTextLength(hwnd);
                    if (length <= 0) return true;

                    var buffer = new StringBuilder(length + 1);
                    if (GetWindowText(hwnd, buffer, buffer.Capacity) <= 0) return true;

                    var title = buffer.ToString().Trim();
                    if (title.Length == 0) return true;

                    var exStyle = GetExtendedStyle(hwnd);
                    if ((exStyle & WsExToolWindow) != 0) return true;
                    if ((exStyle & WsExNoActivate) != 0) return true;

                    list.Add(new WindowEntry((long)hwnd, title));
                }
                catch
                {
                    // 单个窗口读取失败不影响其它窗口
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败（极少见）就让用户用「区域」模式
        }

        return list
            .GroupBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.First())
            .OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>按标题查找窗口当前的屏幕矩形（物理像素）；找不到 / 已最小化返回 null。</summary>
    public static CaptureRect? FindWindowBoundsByTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var entry = EnumerateWindows()
            .FirstOrDefault(w => string.Equals(w.Title, title.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return entry.Handle == 0 ? null : GetWindowBounds(entry.Handle);
    }

    /// <summary>取某个窗口的屏幕矩形（优先 DWM 的“可见边框”，比 GetWindowRect 少一圈透明阴影）。</summary>
    public static CaptureRect? GetWindowBounds(long handle)
    {
        if (handle == 0) return null;
        var hwnd = new IntPtr(handle);
        try
        {
            if (!IsWindow(hwnd) || IsIconic(hwnd)) return null;

            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var dwmRect, Marshal.SizeOf<Win32Rect>()) == 0)
            {
                var rect = ToCaptureRect(dwmRect);
                if (!rect.IsEmpty) return rect;
            }

            if (GetWindowRect(hwnd, out var win32Rect)) return ToCaptureRect(win32Rect);
        }
        catch
        {
            // 窗口在枚举与查询之间关掉了
        }

        return null;
    }

    /// <summary>
    /// 尽力让某个窗口不被屏幕捕获录进去（Windows 10 2004+ 的 <c>WDA_EXCLUDEFROMCAPTURE</c>）。
    /// <para>gdigrab 走 GDI BitBlt，部分系统上仍会录到；调用失败也不影响录制，所以是 best-effort。</para>
    /// </summary>
    public static void TryExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try { SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture); }
        catch { /* 旧系统 / 权限问题忽略 */ }
    }

    private static CaptureRect ToCaptureRect(Win32Rect rect)
    {
        var left = Math.Min(rect.Left, rect.Right);
        var top = Math.Min(rect.Top, rect.Bottom);
        var width = Math.Abs(rect.Right - rect.Left);
        var height = Math.Abs(rect.Bottom - rect.Top);
        return new CaptureRect(left, top, width, height);
    }

    private static long GetExtendedStyle(IntPtr hwnd)
        => IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GwlExStyle).ToInt64()
            : GetWindowLong32(hwnd, GwlExStyle);

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Win32Rect value, int size);
}
