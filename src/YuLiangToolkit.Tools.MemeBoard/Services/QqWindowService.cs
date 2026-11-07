using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace YuLiangToolkit.Tools.MemeBoard.Services;

/// <summary>
/// QQ 窗口的检测、会话名读取、置顶聚焦，以及聊天输入框 / 发送按钮的 UIA 定位。
///
/// <para><b>已在本机 QQNT 上实测过的关键点</b>：</para>
/// <list type="bullet">
///   <item>顶层窗口用 <c>EnumWindows</c> 枚举：进程名 QQ / TIM / QQEX，窗口类 <c>Chrome_WidgetWin_1</c>
///         （兼容老版本 <c>TXGuiFoundation</c>），矩形宽 ≥ 200、高 ≥ 150，排除空标题与小浮窗；</item>
///   <item>会话名从 UIA 的 <c>chat-header__contact-name</c> 元素读；读不到就跳过该窗口（不列为目标）；</item>
///   <item>置顶：<c>IsIconic</c> → <c>ShowWindow(SW_RESTORE)</c> → <c>SetForegroundWindow</c>；被前台锁挡住时
///         用 <c>AttachThreadInput</c> 临时附加线程再试，最后轮询 <c>GetForegroundWindow</c> 的根窗口最多 1.5s；</item>
///   <item>聊天输入框：Class 含 <c>ExEditor-qq-msg-editor</c> 的后代，<c>SetFocus()</c> 后校验
///         <c>AutomationElement.FocusedElement</c>；</item>
///   <item>发送按钮：Class 为 <c>send-msg</c> 的按钮，能用就 <c>InvokePattern</c>，否则聚焦输入框按回车。</item>
/// </list>
///
/// <para><b>UIA 的坑</b>：刚打开 / 刚切会话时 <c>FindFirst</c> 偶尔返回 0 个后代，所以读取统一重试 3 次 × 80ms。
/// 所有 UIA 调用都吞掉异常（QQ 更新 / 窗口关闭时会抛 ElementNotAvailable 之类），返回“读不到”。</para>
///
/// <para><b>线程</b>：本类的方法不碰 WPF 界面，可以在后台线程调用（ViewModel 里用 Task.Run 包一层，避免卡界面）。</para>
/// </summary>
public static class QqWindowService
{
    // ---------------------------------------------------------------- QQ 侧的选择器常量

    /// <summary>聊天头部会话名元素的 ClassName。</summary>
    public const string ChatHeaderClassName = "chat-header__contact-name";

    /// <summary>聊天输入框 ClassName 里包含的片段。</summary>
    public const string EditorClassNameFragment = "ExEditor-qq-msg-editor";

    /// <summary>输入框为空时 ClassName 里多出来的片段。</summary>
    public const string EditorEmptyClassNameFragment = "is-empty";

    /// <summary>发送按钮的 ClassName。</summary>
    public const string SendButtonClassName = "send-msg";

    private static readonly HashSet<string> SupportedProcessNames =
        new(StringComparer.OrdinalIgnoreCase) { "QQ", "TIM", "QQEX" };

    private static readonly HashSet<string> SupportedWindowClasses =
        new(StringComparer.OrdinalIgnoreCase) { "Chrome_WidgetWin_1", "TXGuiFoundation" };

    // ---------------------------------------------------------------- 1. 检测聊天窗口

    /// <summary>
    /// 枚举当前所有能作为粘贴目标的 QQ 聊天窗口（读不到会话名的窗口会被跳过）。
    /// 不抛异常：失败时返回空列表。
    /// </summary>
    public static IReadOnlyList<QqWindowInfo> FindChatWindows()
    {
        var result = new List<QqWindowInfo>();
        try
        {
            EnumWindows((handle, _) =>
            {
                try
                {
                    var info = TryDescribeWindow(handle);
                    if (info is not null) result.Add(info);
                }
                catch
                {
                    // 单个窗口探测失败不影响其它窗口
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举本身失败（极少见）就返回已拿到的
        }
        return result;
    }

    /// <summary>判断一个顶层窗口是不是 QQ 聊天窗口，是就打包成 <see cref="QqWindowInfo"/>。</summary>
    private static QqWindowInfo? TryDescribeWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;

        // 最小化的窗口 IsWindowVisible 仍是 true，但这里两个条件都放过；不可见且没最小化 = 不是聊天窗口。
        if (!IsWindowVisible(handle) && !IsIconic(handle)) return null;

        var className = GetClassNameText(handle);
        if (!SupportedWindowClasses.Contains(className)) return null;

        if (!TryGetProcess(handle, out var processId, out var processName)) return null;
        if (!SupportedProcessNames.Contains(processName)) return null;

        var title = GetWindowTextText(handle).Trim();
        if (title.Length == 0) return null;
        if (title.Contains("SoPY_Status", StringComparison.OrdinalIgnoreCase)) return null; // 输入法状态小窗

        if (!GetWindowRect(handle, out var rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width < 200 || height < 150) return null;

        // 只有读得到会话名的窗口才是可用的粘贴目标
        var chatName = ReadChatName(handle);
        if (string.IsNullOrWhiteSpace(chatName)) return null;

        return new QqWindowInfo(handle, processId, processName, title, chatName, IsIconic(handle));
    }

    /// <summary>取窗口所属进程名（QQ / TIM / QQEX）；失败返回 false。</summary>
    private static bool TryGetProcess(IntPtr handle, out uint processId, out string processName)
    {
        processId = 0;
        processName = "";
        try
        {
            GetWindowThreadProcessId(handle, out processId);
            if (processId == 0) return false;
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            return processName.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 2. 读取会话名

    /// <summary>
    /// 读取 QQ 聊天窗口当前会话名（UIA 的 <c>chat-header__contact-name</c>）。
    /// 读不到返回空字符串（调用方据此跳过该窗口）。
    /// </summary>
    public static string ReadChatName(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return "";
        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null) return "";

            var condition = new PropertyCondition(AutomationElement.ClassNameProperty, ChatHeaderClassName);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var element = root.FindFirst(TreeScope.Descendants, condition);
                    var name = element?.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
                }
                catch
                {
                    // UIA 偶发返回 0 后代 / 元素失效：等一会儿重试
                }

                if (attempt < 2) Thread.Sleep(80);
            }
        }
        catch
        {
            // 窗口刚关掉 / 不是 UIA 可识别窗口
        }
        return "";
    }

    // ---------------------------------------------------------------- 3. 置顶并聚焦

    /// <summary>
    /// 把 QQ 窗口置顶并抢到前台（需要时先取消最小化），成功返回 true。
    /// 最后会轮询「当前前台窗口的根窗口」最多 1.5 秒，确认真的切过去了。
    /// </summary>
    public static bool FocusWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;

        try
        {
            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);

            if (IsWindowForeground(handle)) return true;

            // 普通情况：直接 SetForegroundWindow
            SetForegroundWindow(handle);
            BringWindowToTop(handle);
            if (WaitForForeground(handle, 250)) return true;

            // 被前台锁（前台程序不允许别家抢焦点）挡住时：临时 AttachThreadInput 再试
            var foreground = GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, out _);
            var targetThread = GetWindowThreadProcessId(handle, out _);
            var currentThread = GetCurrentThreadId();

            var attachedForeground = foregroundThread != 0 && AttachThreadInput(currentThread, foregroundThread, true);
            var attachedTarget = targetThread != 0 && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                SetForegroundWindow(handle);
                BringWindowToTop(handle);
            }
            finally
            {
                if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
                if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            }

            return WaitForForeground(handle, 1500);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前前台窗口是不是指定 QQ 窗口（前台窗口可能只是它的子窗口，所以比较根窗口）。</summary>
    public static bool IsWindowForeground(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        if (foreground == handle) return true;
        try
        {
            return GetAncestor(foreground, GA_ROOT) == handle;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>轮询等待指定窗口变成前台窗口。</summary>
    private static bool WaitForForeground(IntPtr handle, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Environment.TickCount64 <= deadline)
        {
            if (IsWindowForeground(handle)) return true;
            Thread.Sleep(40);
        }
        return IsWindowForeground(handle);
    }

    // ---------------------------------------------------------------- 4. 聊天输入框

    /// <summary>
    /// 找 QQ 聊天输入框（Class 含 <c>ExEditor-qq-msg-editor</c> 的后代）；找不到返回 null。
    /// 先按 Document / Edit 控件类型找（快），找不到再退回全量后代过滤。
    /// </summary>
    public static AutomationElement? FindEditor(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return null;
        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null) return null;

            foreach (var controlType in new[] { ControlType.Document, ControlType.Edit })
            {
                try
                {
                    var found = root.FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, controlType));
                    for (var i = 0; i < found.Count; i++)
                    {
                        if (IsEditorClass(ClassNameOf(found[i]))) return found[i];
                    }
                }
                catch
                {
                    // 单次枚举失败无妨，继续尝试下一种
                }
            }

            try
            {
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                for (var i = 0; i < all.Count; i++)
                {
                    if (IsEditorClass(ClassNameOf(all[i]))) return all[i];
                }
            }
            catch
            {
                // 全量查找失败就算了
            }
        }
        catch
        {
            // 窗口没了
        }
        return null;
    }

    /// <summary>
    /// 聚焦聊天输入框并校验真的聚焦上了（<c>AutomationElement.FocusedElement</c>），最多重试 3 次。
    /// </summary>
    public static bool FocusEditor(IntPtr handle)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var editor = FindEditor(handle);
            if (editor is not null)
            {
                try
                {
                    editor.SetFocus();
                    var focused = AutomationElement.FocusedElement;
                    if (focused is not null && IsEditorClass(ClassNameOf(focused))) return true;
                }
                catch
                {
                    // 元素失效 / 焦点被抢：重试
                }
            }

            if (attempt < 2) Thread.Sleep(120);
        }
        return false;
    }

    /// <summary>
    /// 读取输入框现在是不是空的。「空」= ClassName 里带 <c>is-empty</c>。
    /// 返回值：true 空 / false 非空 / null 读不到（元素失效）。
    /// </summary>
    public static bool? TryGetEditorEmpty(AutomationElement? editor)
    {
        if (editor is null) return null;
        try
        {
            var className = ClassNameOf(editor);
            return className.Contains(EditorEmptyClassNameFragment, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前输入框是不是空的（找不到输入框按“空”处理，用于发送后的确认）。</summary>
    public static bool IsEditorEmpty(IntPtr handle)
    {
        var editor = FindEditor(handle);
        return TryGetEditorEmpty(editor) ?? true;
    }

    private static bool IsEditorClass(string? className)
        => className is not null
           && className.Contains(EditorClassNameFragment, StringComparison.OrdinalIgnoreCase);

    private static string ClassNameOf(AutomationElement? element)
    {
        if (element is null) return "";
        try { return element.Current.ClassName ?? ""; }
        catch { return ""; }
    }

    // ---------------------------------------------------------------- 5. 发送按钮 / 键盘输入

    /// <summary>
    /// 尝试用 UIA 点 <c>send-msg</c> 发送按钮：按钮存在、可用且支持 InvokePattern 时返回 true。
    /// </summary>
    public static bool TryInvokeSendButton(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return false;
        try
        {
            var root = AutomationElement.FromHandle(handle);
            if (root is null) return false;

            var button = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, SendButtonClassName));
            if (button is null) return false;
            if (!button.Current.IsEnabled) return false;
            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) return false;
            if (pattern is not InvokePattern invoke) return false;

            invoke.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>发 Ctrl+V（4 个键盘事件）；<c>SendInput</c> 返回 4 视为成功。</summary>
    /// <remarks>cbSize 用 <c>Marshal.SizeOf&lt;INPUT&gt;()</c> 自动取（x64 实测 = 40 字节），不要改成手写常量。</remarks>
    public static bool SendCtrlV()
    {
        var inputs = new[]
        {
            CreateKeyInput(VK_CONTROL, down: true),
            CreateKeyInput(VK_V, down: true),
            CreateKeyInput(VK_V, down: false),
            CreateKeyInput(VK_CONTROL, down: false),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>发回车键（2 个键盘事件）；<c>SendInput</c> 返回 2 视为成功。</summary>
    public static bool SendEnter()
    {
        var inputs = new[]
        {
            CreateKeyInput(VK_RETURN, down: true),
            CreateKeyInput(VK_RETURN, down: false),
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    // ---------------------------------------------------------------- 6. Win32 互操作

    private const int SW_RESTORE = 9;
    private const uint GA_ROOT = 2;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool down) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = down ? 0u : KEYEVENTF_KEYUP,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    // Win32 互操作统一按 Unicode 明确（中文窗口标题 / 类名不会走 ANSI 版截断）
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    // 必须显式 Unicode：默认 ANSI 版本按系统代码页算长度，中文标题（独立聊天窗口）会截断 / 缓冲区不稳
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    private static string GetClassNameText(IntPtr handle)
    {
        try
        {
            var buffer = new StringBuilder(256);
            var length = GetClassName(handle, buffer, buffer.Capacity);
            return length > 0 ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetWindowTextText(IntPtr handle)
    {
        try
        {
            var length = GetWindowTextLength(handle);
            if (length <= 0) return "";
            var buffer = new StringBuilder(length + 1);
            var copied = GetWindowText(handle, buffer, buffer.Capacity);
            return copied > 0 ? buffer.ToString() : "";
        }
        catch
        {
            return "";
        }
    }
}
