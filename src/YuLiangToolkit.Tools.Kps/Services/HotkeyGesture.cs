using System.Text;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// 一个全局热键组合（修饰键 + 主键），配置里存成 <c>"Ctrl+Alt+K"</c> 这样的字符串。
///
/// <para>修饰键位与 Win32 <c>RegisterHotKey</c> 的 <c>MOD_*</c> 一致：
/// Alt=1、Ctrl=2、Shift=4、Win=8；<see cref="VirtualKey"/> 是 Windows 虚拟键码。</para>
///
/// <para>解析规则：<c>Ctrl</c>/<c>Control</c>、<c>Alt</c>、<c>Shift</c>、<c>Win</c>/<c>Windows</c>
/// 不分大小写，用 <c>+</c> 连接；最后一段是主键（<c>K</c> / <c>F5</c> / <c>空格</c>…）。
/// 空字符串 / <c>"无"</c> / <c>"none"</c> = 不注册热键。</para>
/// </summary>
public readonly record struct HotkeyGesture(uint Modifiers, int VirtualKey)
{
    /// <summary>Win32 <c>MOD_ALT</c>。</summary>
    public const uint ModAlt = 0x0001;

    /// <summary>Win32 <c>MOD_CONTROL</c>。</summary>
    public const uint ModControl = 0x0002;

    /// <summary>Win32 <c>MOD_SHIFT</c>。</summary>
    public const uint ModShift = 0x0004;

    /// <summary>Win32 <c>MOD_WIN</c>。</summary>
    public const uint ModWin = 0x0008;

    /// <summary>空手势（不注册）。</summary>
    public bool IsEmpty => VirtualKey == 0;

    /// <summary>从修饰键位 + 虚拟键码构造（主键为 0 时视为空手势）。</summary>
    public static HotkeyGesture FromParts(uint modifiers, int virtualKey)
        => new(modifiers & 0x000F, virtualKey);

    /// <summary>
    /// 解析配置里的热键文字；失败返回 false（<paramref name="gesture"/> = 空手势）。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.Trim();
        if (trimmed is "无" or "none" or "None" or "NONE" or "-") return false;

        var parts = trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        uint modifiers = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control" or "控制":
                    modifiers |= ModControl;
                    break;
                case "alt" or "菜单":
                    modifiers |= ModAlt;
                    break;
                case "shift" or "上档":
                    modifiers |= ModShift;
                    break;
                case "win" or "windows" or "meta" or "超级键":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        var keyText = parts[^1];
        if (!TryParseMainKey(keyText, out var virtualKey)) return false;
        if (virtualKey == 0 || IsModifierVirtualKey(virtualKey)) return false;

        gesture = new HotkeyGesture(modifiers, virtualKey);
        return true;
    }

    /// <summary>
    /// 把一段文字解析成主键虚拟键码（供 <see cref="TryParse"/> 与界面热键捕获共用）。
    /// </summary>
    public static bool TryParseMainKey(string text, out int virtualKey)
    {
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var value = text.Trim();

        if (value.Length == 1)
        {
            var ch = char.ToUpperInvariant(value[0]);
            if (ch is >= 'A' and <= 'Z' || ch is >= '0' and <= '9')
            {
                virtualKey = ch;
                return true;
            }
        }

        if (value.Length is 2 or 3 && (value[0] is 'F' or 'f')
            && int.TryParse(value.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            virtualKey = 0x6F + fn;
            return true;
        }

        switch (value.ToLowerInvariant())
        {
            case "空格" or "space": virtualKey = 0x20; return true;
            case "回车" or "enter" or "return": virtualKey = 0x0D; return true;
            case "esc" or "escape": virtualKey = 0x1B; return true;
            case "tab": virtualKey = 0x09; return true;
            case "退格" or "backspace": virtualKey = 0x08; return true;
            case "delete" or "delete键": virtualKey = 0x2E; return true;
            case "insert": virtualKey = 0x2D; return true;
            case "home": virtualKey = 0x24; return true;
            case "end": virtualKey = 0x23; return true;
            case "pageup": virtualKey = 0x21; return true;
            case "pagedown": virtualKey = 0x22; return true;
            case "←" or "left" or "左方向键": virtualKey = 0x25; return true;
            case "↑" or "up" or "上方向键": virtualKey = 0x26; return true;
            case "→" or "right" or "右方向键": virtualKey = 0x27; return true;
            case "↓" or "down" or "下方向键": virtualKey = 0x28; return true;
            case "分号" or "semicolon" or ";": virtualKey = 0xBA; return true;
            case "空格键": virtualKey = 0x20; return true;
        }

        return false;
    }

    /// <summary>格式化回配置字符串，例如 <c>Ctrl+Alt+K</c>；空手势返回空字符串（= 不注册）。</summary>
    public override string ToString()
    {
        if (IsEmpty) return "";
        var builder = new StringBuilder();
        if ((Modifiers & ModControl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) builder.Append("Alt+");
        if ((Modifiers & ModShift) != 0) builder.Append("Shift+");
        if ((Modifiers & ModWin) != 0) builder.Append("Win+");
        builder.Append(KeyNames.Display(VirtualKey));
        return builder.ToString();
    }

    private static bool IsModifierVirtualKey(int virtualKey) => KeyNames.IsModifier(virtualKey);
}
