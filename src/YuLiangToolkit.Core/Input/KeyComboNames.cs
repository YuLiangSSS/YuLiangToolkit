using System.Globalization;
using System.Text;

namespace YuLiangToolkit.Core.Input;

/// <summary>
/// Windows 虚拟键码（VK）⇄ 界面文字的**唯一来源**（快捷键体系的核心词表）。
///
/// <para>显示名同时就是解析名，所以 <see cref="Display"/> 的输出一定能被 <see cref="TryParseKey"/>
/// 原样解析回来（往返一致）—— 配置里存的是给人看的文字，不是裸数字。</para>
///
/// <para>命名口径偏“通用英文短名 + 中文别名”：显示成 <c>Space</c> / <c>PageUp</c> / <c>F9</c> / <c>A</c>，
/// 同时认 <c>空格</c> / <c>pgup</c> / <c>左箭头</c> 这类写法，方便手写配置与中文输入法环境下输入。</para>
///
/// <para>⚠ 本类只做「单个键」的翻译；组合键（<c>Ctrl+U+K</c>）的拼装 / 拆分在 <see cref="KeyCombo"/>。</para>
/// </summary>
public static class KeyComboNames
{
    /// <summary>虚拟键码 → 显示名（<see cref="Display"/> 用）。</summary>
    private static readonly Dictionary<int, string> Names = BuildNames();

    /// <summary>显示名 / 英文别名 / 口语别名 → 虚拟键码（<see cref="TryParseKey"/> 用）。</summary>
    private static readonly Dictionary<string, int> Aliases = BuildAliases();

    /// <summary>组合键里允许出现的修饰键（按显示顺序：Ctrl → Alt → Shift → Win）。</summary>
    public static IReadOnlyList<int> ModifierKeys { get; } = new[]
    {
        0x11, // Ctrl（通用）
        0x12, // Alt
        0x10, // Shift
        0x5B, // Win（左）
    };

    /// <summary>虚拟键码 → 显示名（不会抛异常、不会返回空）。</summary>
    public static string Display(int virtualKey)
    {
        if (virtualKey == 0) return "—";
        if (Names.TryGetValue(virtualKey, out var name)) return name;

        // 防御：表被改动后仍然给出可读结果
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return "F" + (virtualKey - 0x6F);
        return $"VK_0x{virtualKey:X2}";
    }

    /// <summary>
    /// 是不是「修饰键」。左右同键（左 Ctrl / 右 Ctrl）在组合键里都归一成同一个修饰键位，
    /// 否则 <c>Ctrl+左Shift+A</c> 与 <c>Ctrl+Shift+A</c> 会被当成两个不同的快捷键。
    /// </summary>
    public static bool IsModifier(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12        // Shift / Ctrl / Alt（通用）
        or 0xA0 or 0xA1             // 左 / 右 Shift
        or 0xA2 or 0xA3             // 左 / 右 Ctrl
        or 0xA4 or 0xA5             // 左 / 右 Alt
        or 0x5B or 0x5C;            // 左 / 右 Win

    /// <summary>把左右同键归一成通用修饰键（非修饰键原样返回）。</summary>
    public static int NormalizeModifier(int virtualKey) => virtualKey switch
    {
        0xA0 or 0xA1 => 0x10,   // 左/右 Shift → Shift
        0xA2 or 0xA3 => 0x11,   // 左/右 Ctrl  → Ctrl
        0xA4 or 0xA5 => 0x12,   // 左/右 Alt   → Alt
        0x5C => 0x5B,           // 右 Win      → Win
        _ => virtualKey,
    };

    /// <summary>把一串虚拟键码变成「D F J K」这样的文字（日志 / 调试用）。</summary>
    public static string Join(IEnumerable<int> virtualKeys)
        => string.Join(" ", virtualKeys.Where(k => k != 0).Select(Display));

    /// <summary>
    /// 把单个键名解析成虚拟键码。支持：单个字母 / 数字、<c>F1</c>~<c>F24</c>、
    /// 显示名（大小写不敏感）、英文别名（<c>space</c> / <c>enter</c> / <c>pgup</c>…）、
    /// 标点的中文短名与符号本身（<c>分号</c> / <c>;</c>）、以及 <c>VK_0xNN</c>。
    /// </summary>
    public static bool TryParseKey(string? text, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();

        // 字母 / 数字：单个字符
        if (value.Length == 1)
        {
            var ch = char.ToUpperInvariant(value[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        // F1 ~ F24
        if (value.Length is 2 or 3 && (value[0] is 'F' or 'f')
            && int.TryParse(value.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fn)
            && fn is >= 1 and <= 24)
        {
            virtualKey = 0x6F + fn;
            return true;
        }

        // 「VK_0xNN」：Display 对未知键的输出，保证往返一致
        if (value.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            && hex is > 0 and <= 0xFF)
        {
            virtualKey = hex;
            return true;
        }

        return Aliases.TryGetValue(Normalize(value), out virtualKey);
    }

    /// <summary>修饰键文字 → 归一后的修饰键码（<c>Ctrl</c> / <c>控制</c> → 0x11 等）；不是修饰键返回 false。</summary>
    public static bool TryParseModifier(string? text, out int modifier)
    {
        modifier = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (Normalize(text))
        {
            case "ctrl" or "control" or "控制" or "ctrl键":
                modifier = 0x11;
                return true;
            case "alt" or "菜单" or "alt键":
                modifier = 0x12;
                return true;
            case "shift" or "上档" or "shift键":
                modifier = 0x10;
                return true;
            case "win" or "windows" or "meta" or "超级键" or "win键":
                modifier = 0x5B;
                return true;
            default:
                return false;
        }
    }

    /// <summary>修饰键键码 → 显示名（<c>Ctrl</c> / <c>Alt</c> / <c>Shift</c> / <c>Win</c>）。</summary>
    public static string ModifierDisplay(int modifier) => NormalizeModifier(modifier) switch
    {
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x10 => "Shift",
        0x5B => "Win",
        _ => Display(modifier),
    };

    private static string Normalize(string text) => text.Trim().ToLowerInvariant();

    private static Dictionary<int, string> BuildNames()
    {
        var map = new Dictionary<int, string>
        {
            // 编辑 / 功能键
            [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pause",
            [0x14] = "CapsLock", [0x1B] = "Esc", [0x20] = "Space", [0x21] = "PageUp",
            [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
            [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",

            // Win / 锁
            [0x5B] = "Win", [0x5C] = "右Win", [0x5D] = "Menu",
            [0x90] = "NumLock", [0x91] = "ScrollLock",

            // 修饰键
            [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
            [0xA0] = "左Shift", [0xA1] = "右Shift",
            [0xA2] = "左Ctrl", [0xA3] = "右Ctrl",
            [0xA4] = "左Alt", [0xA5] = "右Alt",

            // 浏览器 / 多媒体
            [0xA6] = "浏览器后退", [0xA7] = "浏览器前进", [0xA8] = "浏览器刷新",
            [0xA9] = "浏览器停止", [0xAA] = "浏览器搜索", [0xAB] = "浏览器收藏",
            [0xAC] = "浏览器主页",
            [0xAD] = "静音", [0xAE] = "音量-", [0xAF] = "音量+",
            [0xB0] = "下一曲", [0xB1] = "上一曲", [0xB2] = "停止播放", [0xB3] = "播放暂停",

            // 常用标点（OEM 键）
            [0xBA] = "分号", [0xBB] = "等号", [0xBC] = "逗号", [0xBD] = "减号",
            [0xBE] = "句号", [0xBF] = "斜杠", [0xC0] = "反引号",
            [0xDB] = "左方括号", [0xDC] = "反斜杠", [0xDD] = "右方括号", [0xDE] = "单引号",
        };

        for (var i = 0x30; i <= 0x39; i++) map[i] = ((char)i).ToString();
        for (var i = 0x41; i <= 0x5A; i++) map[i] = ((char)i).ToString();

        // 小键盘 0~9 与运算符
        for (var i = 0x60; i <= 0x69; i++) map[i] = "小键盘" + (i - 0x60);
        map[0x6A] = "小键盘*";
        map[0x6B] = "小键盘+";
        map[0x6D] = "小键盘-";
        map[0x6E] = "小键盘.";
        map[0x6F] = "小键盘/";

        for (var i = 0x70; i <= 0x87; i++) map[i] = "F" + (i - 0x6F);
        return map;
    }

    private static Dictionary<string, int> BuildAliases()
    {
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ① 显示名本身：保证 Display → TryParseKey 往返一致
        foreach (var pair in Names) aliases[Normalize(pair.Value)] = pair.Key;

        void Alias(int virtualKey, params string[] names)
        {
            foreach (var name in names) aliases[Normalize(name)] = virtualKey;
        }

        Alias(0x08, "退格", "bs");
        Alias(0x09, "制表");
        Alias(0x0D, "回车", "return");
        Alias(0x13, "break", "暂停");
        Alias(0x14, "caps", "大写锁定");
        Alias(0x1B, "escape", "退出键");
        Alias(0x20, "空格", "空格键", "spacebar");
        Alias(0x21, "pgup", "上一页");
        Alias(0x22, "pgdn", "下一页");
        Alias(0x25, "←", "arrowleft", "左方向键", "左箭头");
        Alias(0x26, "↑", "arrowup", "上方向键", "上箭头");
        Alias(0x27, "→", "arrowright", "右方向键", "右箭头");
        Alias(0x28, "↓", "arrowdown", "下方向键", "下箭头");
        Alias(0x2C, "prtsc", "print", "截图");
        Alias(0x2D, "ins", "插入");
        Alias(0x2E, "del", "删除");
        Alias(0x5B, "windows", "meta", "lwin", "leftwin", "win键", "超级键");
        Alias(0x5C, "rwin", "rightwin");
        Alias(0x5D, "apps", "contextmenu", "右键菜单");
        Alias(0x90, "数字锁定", "num");
        Alias(0x91, "scroll", "滚动锁定");
        Alias(0xA0, "lshift", "leftshift");
        Alias(0xA1, "rshift", "rightshift");
        Alias(0xA2, "lctrl", "leftctrl", "control", "控制");
        Alias(0xA3, "rctrl", "rightctrl");
        Alias(0xA4, "lalt", "leftalt", "菜单");
        Alias(0xA5, "ralt", "rightalt", "altgr");

        // 小键盘：英文名 + 运算符
        for (var i = 0; i <= 9; i++) Alias(0x60 + i, "numpad" + i, "num" + i, "小键盘" + i);
        Alias(0x6A, "numpad*", "numpadmul", "multiply");
        Alias(0x6B, "numpad+", "numpadadd", "add");
        Alias(0x6D, "numpad-", "numpadsub", "subtract");
        Alias(0x6E, "numpad.", "numpaddecimal", "decimal");
        Alias(0x6F, "numpad/", "numpaddiv", "divide");

        // 标点：符号本身 + 英文名
        Alias(0xBA, ";", "semicolon");
        Alias(0xBB, "=", "equals", "equal");
        Alias(0xBC, ",", "comma");
        Alias(0xBD, "-", "minus");
        Alias(0xBE, ".", "period", "dot");
        Alias(0xBF, "/", "slash");
        Alias(0xC0, "`", "backquote", "grave");
        Alias(0xDB, "[", "bracketleft", "左括号");
        Alias(0xDC, "\\", "backslash");
        Alias(0xDD, "]", "bracketright", "右括号");
        Alias(0xDE, "'", "quote", "apostrophe", "引号");

        return aliases;
    }

    /// <summary>调试用：把所有已知键名拼成一行（自检 / 排错用）。</summary>
    public static string DebugDump()
    {
        var builder = new StringBuilder();
        foreach (var pair in Names.OrderBy(p => p.Key))
            builder.Append($"0x{pair.Key:X2}={pair.Value} ");
        return builder.ToString().TrimEnd();
    }
}
