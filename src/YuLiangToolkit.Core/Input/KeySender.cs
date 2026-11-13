using System.Runtime.InteropServices;

namespace YuLiangToolkit.Core.Input;

/// <summary>
/// 模拟键盘输入（<c>SendInput</c>）：给「复制 / 粘贴到哪里」这类需要替别的程序按键的功能用。
///
/// <para><b>为什么放 Core</b>：好几个工具都要按 <c>Ctrl+C</c> / <c>Ctrl+V</c>
/// （文本转换器、表情包收藏夹…）。以前每个工具各抄一份 <c>INPUT</c> 结构，
/// 容易抄错 <c>cbSize</c>（x64 是 40 字节，手写常量必错）。</para>
///
/// <para>⚠ <c>cbSize</c> 用 <c>Marshal.SizeOf&lt;INPUT&gt;()</c> 自动取，**不要**改成手写常量。</para>
/// </summary>
public static class KeySender
{
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12;
    private const ushort VkV = 0x56;
    private const ushort VkC = 0x43;

    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    /// <summary>发 Ctrl+C（复制当前选区）。</summary>
    public static bool SendCtrlC() => SendCombo(VkControl, VkC);

    /// <summary>发 Ctrl+V（粘贴）。</summary>
    public static bool SendCtrlV() => SendCombo(VkControl, VkV);

    /// <summary>
    /// 发一个「修饰键 + 一个主键」的组合（按下再松开）。
    /// </summary>
    /// <param name="modifierVirtualKey">修饰键虚拟键码（例如 <c>0x11</c> = Ctrl）。</param>
    /// <param name="virtualKey">主键虚拟键码。</param>
    public static bool SendCombo(ushort modifierVirtualKey, ushort virtualKey)
    {
        var inputs = new[]
        {
            CreateKeyInput(modifierVirtualKey, down: true),
            CreateKeyInput(virtualKey, down: true),
            CreateKeyInput(virtualKey, down: false),
            CreateKeyInput(modifierVirtualKey, down: false),
        };

        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    /// <summary>
    /// 让当前前台程序「把选区复制到剪贴板」。
    /// <para>会发 Ctrl+C；调用方随后自己读剪贴板。</para>
    /// </summary>
    public static bool CopySelection() => SendCtrlC();

    /// <summary>把剪贴板内容粘贴到当前前台程序（发 Ctrl+V）。</summary>
    public static bool Paste() => SendCtrlV();

    /// <summary>构造一个键盘事件。</summary>
    private static INPUT CreateKeyInput(ushort virtualKey, bool down) => new()
    {
        type = InputKeyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = virtualKey,
                wScan = 0,
                dwFlags = down ? 0u : KeyEventFKeyUp,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };

    // ---------------------------------------------------------------- Win32

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);
}
