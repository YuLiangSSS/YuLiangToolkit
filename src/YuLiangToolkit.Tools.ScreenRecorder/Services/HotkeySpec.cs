using System.Windows.Input;

namespace YuLiangToolkit.Tools.ScreenRecorder.Services;

/// <summary>
/// 一个全局热键组合：Win32 <c>RegisterHotKey</c> 的 <c>MOD_*</c> 修饰键位 + 虚拟键码（VK）。
///
/// <para><b>配置文件里存成字符串</b>：<see cref="DisplayText"/> 输出的就是规范写法
/// （<c>Ctrl+Shift+F9</c> / <c>F8</c> / <c>Alt+Space</c>），<see cref="TryParse"/> 一定能把
/// <see cref="DisplayText"/> 原样解析回来（往返一致）。</para>
///
/// <para><b>解析规则</b>：修饰键 <c>Ctrl</c>/<c>Control</c>、<c>Alt</c>、<c>Shift</c>、
/// <c>Win</c>/<c>Windows</c> 不分大小写、前后空格随意；主键支持 <c>F1</c>~<c>F24</c>、
/// <c>A</c>~<c>Z</c>、<c>0</c>~<c>9</c>、<c>Space</c>，以及 Tab / Enter / Backspace / Insert /
/// Delete / Home / End / PageUp / PageDown / 方向键 / PrintScreen / Pause。
/// 解析失败只返回 false，不抛异常。</para>
///
/// <para><b>有效组合</b>：<c>F1</c>~<c>F24</c> 可以单独注册；其余键（字母 / 数字 / Space / 导航键）
/// 必须至少带一个修饰键 —— 否则全局热键会把正常打字或回车、空格吃掉。</para>
/// </summary>
public sealed record HotkeySpec(uint Modifiers, int VirtualKey)
{
    /// <summary>Win32 <c>MOD_ALT</c>。</summary>
    public const uint ModAlt = 0x0001;

    /// <summary>Win32 <c>MOD_CONTROL</c>。</summary>
    public const uint ModControl = 0x0002;

    /// <summary>Win32 <c>MOD_SHIFT</c>。</summary>
    public const uint ModShift = 0x0004;

    /// <summary>Win32 <c>MOD_WIN</c>。</summary>
    public const uint ModWin = 0x0008;

    /// <summary>四个修饰键位的掩码（解析 / 格式化只认这四位，高位一律忽略）。</summary>
    public const uint ModMask = ModAlt | ModControl | ModShift | ModWin;

    /// <summary>空手势（没有主键）—— 不能注册。</summary>
    public bool IsEmpty => VirtualKey == 0;

    /// <summary>规范显示文本，例如 <c>Ctrl+Shift+F9</c> / <c>F8</c> / <c>Alt+Space</c>；空手势返回空字符串。</summary>
    public string DisplayText => FormatDisplayText(Modifiers, VirtualKey);

    /// <summary>同 <see cref="DisplayText"/>，方便日志 / 调试直接打印。</summary>
    public override string ToString() => DisplayText;

    /// <summary>
    /// 解析热键文字；失败返回 false（<paramref name="spec"/> 为 null），不抛异常。
    /// </summary>
    public static bool TryParse(string? text, out HotkeySpec spec)
    {
        spec = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 全角加号统一成半角，中文输入法下也能直接敲
        var normalized = text.Trim().Replace('＋', '+');

        uint modifiers = 0;
        var virtualKey = 0;
        var hasMainKey = false;

        foreach (var raw in normalized.Split('+', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0) continue; // 容忍 "Ctrl + F9" 这种空格写法 / 结尾多余 '+'

            if (TryParseModifier(raw, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            // 主键只能有一个，例如 "Ctrl+F9+F10" 判为无效
            if (hasMainKey) return false;
            if (!TryParseKey(raw, out virtualKey)) return false;
            hasMainKey = true;
        }

        if (!hasMainKey || !IsValidCombination(modifiers, virtualKey)) return false;

        spec = new HotkeySpec(modifiers & ModMask, virtualKey);
        return true;
    }

    /// <summary>
    /// 从 WPF 按键 + 修饰键构造；返回 false 表示无效组合
    /// （只有修饰键、字母 / 数字 / Space 没加修饰键、或不支持的按键）。
    /// <para>注意：Alt 组合在 WPF 里是 <see cref="Key.System"/>，调用方应先用
    /// <c>KeyEventArgs.SystemKey</c> 换成真正的主键再传进来。</para>
    /// </summary>
    public static bool TryFromKey(Key key, ModifierKeys modifiers, out HotkeySpec spec)
    {
        spec = null!;

        // 修饰键本身 / 没有真实主键的情况一律无效
        if (key is Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed) return false;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;

        var win32Modifiers = ToWin32Modifiers(modifiers);
        if (!IsValidCombination(win32Modifiers, virtualKey)) return false;

        spec = new HotkeySpec(win32Modifiers, virtualKey);
        return true;
    }

    /// <summary>WPF <see cref="ModifierKeys"/> → Win32 <c>MOD_*</c> 位。</summary>
    public static uint ToWin32Modifiers(ModifierKeys modifiers)
    {
        uint result = 0;
        if ((modifiers & ModifierKeys.Control) != 0) result |= ModControl;
        if ((modifiers & ModifierKeys.Alt) != 0) result |= ModAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) result |= ModShift;
        if ((modifiers & ModifierKeys.Windows) != 0) result |= ModWin;
        return result;
    }

    /// <summary>
    /// 把「修饰键位 + 虚拟键码」格式化成显示文本（热键捕获对话框的实时预览也用它）。
    /// 修饰键固定按 Ctrl → Alt → Shift → Win 的顺序输出；<paramref name="virtualKey"/> = 0
    /// 时只输出修饰键（例如 <c>Ctrl+Alt</c>）。
    /// </summary>
    public static string FormatDisplayText(uint modifiers, int virtualKey)
    {
        modifiers &= ModMask;

        var parts = new List<string>(5);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        if (virtualKey != 0) parts.Add(DisplayKey(virtualKey));

        return string.Join("+", parts);
    }

    /// <summary>这个虚拟键码是否支持（<see cref="TryParse"/> / <see cref="TryFromKey"/> 的键名表里有）。</summary>
    public static bool IsSupportedKey(int virtualKey) => KeyDisplayNames.ContainsKey(virtualKey);

    /// <summary>虚拟键码 → 显示名；未知键返回 <c>VK_0xNN</c>（仅用于排错预览）。</summary>
    private static string DisplayKey(int virtualKey)
        => KeyDisplayNames.TryGetValue(virtualKey, out var name) ? name : $"VK_0x{virtualKey:X2}";

    /// <summary>组合是否有效：主键必须支持；F1~F24 可单独用，其余键必须带修饰键。</summary>
    private static bool IsValidCombination(uint modifiers, int virtualKey)
    {
        if (virtualKey == 0) return false;
        if (!IsSupportedKey(virtualKey)) return false;

        // F1~F24：可以单独注册
        if (virtualKey is >= 0x70 and <= 0x87) return true;

        // 字母 / 数字 / Space / 导航键：必须至少一个修饰键
        return (modifiers & ModMask) != 0;
    }

    /// <summary>解析一个修饰键片段（大小写不敏感）。</summary>
    private static bool TryParseModifier(string text, out uint modifier)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "ctrl" or "control" or "ctrl键" or "控制":
                modifier = ModControl;
                return true;
            case "alt" or "alt键" or "菜单":
                modifier = ModAlt;
                return true;
            case "shift" or "shift键" or "上档":
                modifier = ModShift;
                return true;
            case "win" or "windows" or "meta" or "win键" or "超级键":
                modifier = ModWin;
                return true;
            default:
                modifier = 0;
                return false;
        }
    }

    /// <summary>解析一个主键片段（字母 / 数字 / 键名，大小写不敏感）。</summary>
    private static bool TryParseKey(string text, out int virtualKey)
    {
        virtualKey = 0;
        var value = text.Trim();
        if (value.Length == 0) return false;

        // 单个字母 / 数字：直接就是 VK（大写归一）
        if (value.Length == 1)
        {
            var ch = char.ToUpperInvariant(value[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        return KeyAliases.TryGetValue(value, out virtualKey);
    }

    /// <summary>虚拟键码 → 显示名（显示名同时是解析别名，保证往返一致）。</summary>
    private static readonly Dictionary<int, string> KeyDisplayNames = BuildKeyDisplayNames();

    /// <summary>显示名 / 英文别名 → 虚拟键码（大小写不敏感）。</summary>
    private static readonly Dictionary<string, int> KeyAliases = BuildKeyAliases();

    private static Dictionary<int, string> BuildKeyDisplayNames()
    {
        var map = new Dictionary<int, string>
        {
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x13] = "Pause",
            [0x20] = "Space",
            [0x21] = "PageUp",
            [0x22] = "PageDown",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2C] = "PrintScreen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
        };

        for (var vk = 0x30; vk <= 0x39; vk++) map[vk] = ((char)vk).ToString(); // 0~9
        for (var vk = 0x41; vk <= 0x5A; vk++) map[vk] = ((char)vk).ToString(); // A~Z
        for (var vk = 0x70; vk <= 0x87; vk++) map[vk] = "F" + (vk - 0x6F);      // F1~F24

        return map;
    }

    private static Dictionary<string, int> BuildKeyAliases()
    {
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ① 显示名本身（往返一致）
        foreach (var pair in KeyDisplayNames) aliases[pair.Value] = pair.Key;

        // ② 英文 / 口语别名（手写配置更宽松）
        void Add(int virtualKey, params string[] names)
        {
            foreach (var name in names) aliases[name] = virtualKey;
        }

        Add(0x08, "bs");
        Add(0x0D, "return");
        Add(0x13, "break");
        Add(0x20, "spacebar");
        Add(0x21, "pgup");
        Add(0x22, "pgdn");
        Add(0x2C, "prtsc", "print");
        Add(0x2D, "ins");
        Add(0x2E, "del");

        return aliases;
    }
}
