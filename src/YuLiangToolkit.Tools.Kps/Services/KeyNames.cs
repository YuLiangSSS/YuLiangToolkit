using System.Globalization;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// Windows 虚拟键码（VK）→ 界面文字（中文短名），以及反过来的解析。
///
/// <para>键位格子上的文字、设置热键、Overlay 都走它：普通字母数字显示成大写字符，
/// 特殊键显示中文 / 通用英文名（空格 / 回车 / 左Shift / ← …），未知键返回 <c>VK_0xNN</c> 便于排错。</para>
///
/// <para><b>唯一来源</b>：显示名表 <see cref="Names"/> 同时生成解析别名表，所以
/// <see cref="Display"/> 的输出（除「—」与 <c>VK_0xNN</c> 外的未知键）一定能被 <see cref="TryParseKey"/> 解析回来；
/// 另配英文名 / 口语别名（<c>space</c>、<c>pgup</c>、<c>numpad0</c>…），方便手写配置。</para>
/// </summary>
public static class KeyNames
{
    /// <summary>虚拟键码 → 显示名（<see cref="Display"/> 用）。</summary>
    private static readonly Dictionary<int, string> Names = BuildNames();

    /// <summary>显示名 / 英文别名 / 口语别名 → 虚拟键码（<see cref="TryParseKey"/> 用）。</summary>
    private static readonly Dictionary<string, int> Aliases = BuildAliases();

    /// <summary>虚拟键码 → 显示名（不会抛异常 / 不会返回空）。</summary>
    public static string Display(int virtualKey)
    {
        if (virtualKey == 0) return "—";
        if (Names.TryGetValue(virtualKey, out var name)) return name;

        // 字母 / 数字以外的已知范围兜底（Names 里其实已铺满，这里防御未来改表）
        if (virtualKey is >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x41 and <= 0x5A) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return "F" + (virtualKey - 0x6F);
        return $"VK_0x{virtualKey:X2}";
    }

    /// <summary>是不是修饰键（热键捕获时用来跳过「只按下 Ctrl」这种情况）。</summary>
    public static bool IsModifier(int virtualKey) => virtualKey is
        0x10 or 0x11 or 0x12        // Shift / Ctrl / Alt（通用）
        or 0xA0 or 0xA1             // 左 / 右 Shift
        or 0xA2 or 0xA3             // 左 / 右 Ctrl
        or 0xA4 or 0xA5             // 左 / 右 Alt
        or 0x5B or 0x5C;            // Win

    /// <summary>把一串虚拟键码变成「D F J K」这样的文字（日志 / 预设展示用）。</summary>
    public static string Join(IEnumerable<int> virtualKeys)
        => string.Join(" ", virtualKeys.Where(k => k != 0).Select(Display));

    /// <summary>
    /// 把单个键名解析成虚拟键码（<see cref="HotkeyGesture.TryParseMainKey"/> 与 <see cref="ParseList"/> 共用）。
    ///
    /// <para>支持：单个字母 / 数字、<c>F1</c>~<c>F24</c>、<see cref="Display"/> 的显示名（大小写不敏感）、
    /// 英文别名（<c>space</c> / <c>enter</c> / <c>left</c> / <c>numpad0</c>…）、
    /// 标点的中文短名与符号本身（<c>分号</c> / <c>;</c> …）、以及 <c>VK_0xNN</c> 十六进制形式。</para>
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
            if (ch is >= 'A' and <= 'Z' || ch is >= '0' and <= '9')
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

        // 「VK_0xNN」：Display 对未知键的输出，保证 ToString → TryParse 往返一致
        if (value.StartsWith("VK_0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value.AsSpan(5), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
            && hex is > 0 and <= 0xFF)
        {
            virtualKey = hex;
            return true;
        }

        if (Aliases.TryGetValue(NormalizeKey(value), out var known))
        {
            virtualKey = known;
            return true;
        }

        return false;
    }

    /// <summary>把「D F J K」这种空格分隔的文字解析回虚拟键码（宽松版本：解析不了的片段直接跳过）。</summary>
    public static IReadOnlyList<int> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();

        var trimmed = text.Trim();
        // 先整体解析：兼容「小键盘+」这种名字里自带加号、分开切会切坏的键
        if (TryParseKey(trimmed, out var single)) return new[] { single };

        var result = new List<int>();
        var tokens = trimmed.Split(
            new[] { ' ', '\t', ',', '，', '、', ';', '；' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            // 先按整段解析：兼容「小键盘+」这种名字里自带加号、再切就会切坏的键
            if (TryParseKey(token, out var virtualKey))
            {
                result.Add(virtualKey);
                continue;
            }

            // 再兼容「D+F+J」这种加号分隔写法
            foreach (var part in token.Split(new[] { '+', '＋' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryParseKey(part, out var partKey)) result.Add(partKey);
            }
        }

        return result;
    }

    /// <summary>调试用：把所有已知键名拼成一行（自检 / 排错用）。</summary>
    public static string DebugDump()
    {
        var builder = new StringBuilder();
        foreach (var pair in Names.OrderBy(p => p.Key))
            builder.Append($"0x{pair.Key:X2}={pair.Value} ");
        return builder.ToString().TrimEnd();
    }

    /// <summary>规范化别名（去首尾空格 + 忽略大小写）。</summary>
    private static string NormalizeKey(string text) => text.Trim().ToLowerInvariant();

    private static Dictionary<int, string> BuildNames()
    {
        var map = new Dictionary<int, string>
        {
            // 编辑 / 功能键
            [0x08] = "退格", [0x09] = "Tab", [0x0D] = "回车", [0x13] = "Pause",
            [0x14] = "CapsLock", [0x1B] = "Esc", [0x20] = "空格", [0x21] = "PageUp",
            [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓",
            [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",

            // Win / 锁
            [0x5B] = "左Win", [0x5C] = "右Win", [0x5D] = "菜单键",
            [0x90] = "NumLock", [0x91] = "ScrollLock",

            // 左右修饰键（Raw Input 归一后的键码）
            [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
            [0xA0] = "左Shift", [0xA1] = "右Shift",
            [0xA2] = "左Ctrl", [0xA3] = "右Ctrl",
            [0xA4] = "左Alt", [0xA5] = "右Alt",

            // 浏览器 / 多媒体
            [0xA6] = "浏览器后退", [0xA7] = "浏览器前进", [0xA8] = "浏览器刷新",
            [0xA9] = "浏览器停止", [0xAA] = "浏览器搜索", [0xAB] = "浏览器收藏",
            [0xAC] = "浏览器主页",
            [0xAD] = "静音", [0xAE] = "音量-", [0xAF] = "音量+",
            [0xB0] = "下一曲", [0xB1] = "上一曲", [0xB2] = "停止播放", [0xB3] = "播放/暂停",

            // 常用标点（OEM 键）
            [0xBA] = "分号", [0xBB] = "等号", [0xBC] = "逗号", [0xBD] = "减号",
            [0xBE] = "句号", [0xBF] = "斜杠", [0xC0] = "反引号",
            [0xDB] = "左方括号", [0xDC] = "反斜杠", [0xDD] = "右方括号", [0xDE] = "单引号",
        };

        for (var i = 0x30; i <= 0x39; i++) map[i] = ((char)i).ToString();
        for (var i = 0x41; i <= 0x5A; i++) map[i] = ((char)i).ToString();

        // 小键盘 0~9 与运算符
        for (var i = 0x60; i <= 0x69; i++) map[i] = "小键盘" + (i - 0x60);
        map[0x6A] = "小键盘*"; map[0x6B] = "小键盘+"; map[0x6D] = "小键盘-";
        map[0x6E] = "小键盘."; map[0x6F] = "小键盘/";

        for (var i = 0x70; i <= 0x87; i++) map[i] = "F" + (i - 0x6F);
        return map;
    }

    private static Dictionary<string, int> BuildAliases()
    {
        var aliases = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ① 显示名本身：保证 Display → TryParseKey 往返一致
        foreach (var pair in Names) aliases[NormalizeKey(pair.Value)] = pair.Key;

        // ② 英文 / 口语别名（手写配置友好）
        void Alias(int virtualKey, params string[] names)
        {
            foreach (var name in names) aliases[NormalizeKey(name)] = virtualKey;
        }

        Alias(0x08, "backspace", "bs");
        Alias(0x09, "tab");
        Alias(0x0D, "enter", "return");
        Alias(0x13, "pause", "break", "暂停");
        Alias(0x14, "capslock", "caps", "大写锁定");
        Alias(0x1B, "esc", "escape");
        Alias(0x20, "space", "spacebar", "空格键");
        Alias(0x21, "pageup", "pgup", "上一页");
        Alias(0x22, "pagedown", "pgdn", "下一页");
        Alias(0x23, "end");
        Alias(0x24, "home");
        Alias(0x25, "left", "arrowleft", "左方向键", "左箭头");
        Alias(0x26, "up", "arrowup", "上方向键", "上箭头");
        Alias(0x27, "right", "arrowright", "右方向键", "右箭头");
        Alias(0x28, "down", "arrowdown", "下方向键", "下箭头");
        Alias(0x2C, "printscreen", "prtsc", "print", "截图");
        Alias(0x2D, "insert", "ins", "插入");
        Alias(0x2E, "delete", "del", "删除");
        Alias(0x5B, "lwin", "leftwin", "win键");
        Alias(0x5C, "rwin", "rightwin");
        Alias(0x5D, "apps", "menu", "contextmenu", "右键菜单");
        Alias(0x90, "numlock", "数字锁定");
        Alias(0x91, "scrolllock", "scroll", "滚动锁定");
        Alias(0xA0, "lshift", "leftshift");
        Alias(0xA1, "rshift", "rightshift");
        Alias(0xA2, "lctrl", "leftctrl", "lcontrol");
        Alias(0xA3, "rctrl", "rightctrl", "rcontrol");
        Alias(0xA4, "lalt", "leftalt");
        Alias(0xA5, "ralt", "rightalt", "altgr");

        // 小键盘：英文名 + 运算符
        for (var i = 0; i <= 9; i++) Alias(0x60 + i, "numpad" + i, "num" + i);
        Alias(0x6A, "numpad*", "numpadmul", "multiply");
        Alias(0x6B, "numpad+", "numpadadd", "add");
        Alias(0x6D, "numpad-", "numpadsub", "subtract");
        Alias(0x6E, "numpad.", "numpaddecimal", "decimal");
        Alias(0x6F, "numpad/", "numpaddiv", "divide");

        // 标点：符号本身 + 英文名（中文名已在显示名表里）
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
}
