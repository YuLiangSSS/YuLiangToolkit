using System.Text;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// 一个全局热键组合（修饰键 + 主键），配置里存成 <c>"Ctrl+Alt+K"</c> 这样的字符串。
///
/// <para>修饰键位与 Win32 <c>RegisterHotKey</c> 的 <c>MOD_*</c> 一致：
/// Alt=1、Ctrl=2、Shift=4、Win=8；<see cref="VirtualKey"/> 是 Windows 虚拟键码。</para>
///
/// <para>解析规则：<c>Ctrl</c>/<c>Control</c>/<c>控制</c>、<c>Alt</c>/<c>菜单</c>、
/// <c>Shift</c>/<c>上档</c>、<c>Win</c>/<c>Windows</c> 不分大小写，用 <c>+</c> 连接（全角 <c>＋</c> 也认）；
/// 最后一段是主键（<c>K</c> / <c>F5</c> / <c>空格</c> / <c>←</c> / <c>小键盘+</c> …，解析见 <see cref="KeyNames.TryParseKey"/>）。
/// 空字符串 / <c>"无"</c> / <c>"none"</c> / <c>"-"</c> = 不注册热键。</para>
///
/// <para><see cref="ToString"/> 固定按 Ctrl、Alt、Shift、Win 的顺序输出，且能被 <see cref="TryParse"/>
/// 原样解析回来（往返一致）。</para>
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

    /// <summary>从修饰键位 + 虚拟键码构造（只保留 Win32 <c>MOD_*</c> 的低四位；主键为 0 时视为空手势）。</summary>
    public static HotkeyGesture FromParts(uint modifiers, int virtualKey)
        => new(modifiers & 0x000F, virtualKey);

    /// <summary>
    /// 解析配置里的热键文字；失败返回 false（<paramref name="gesture"/> = 空手势）。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 全角加号统一成半角，方便中文输入法下直接敲
        var trimmed = text.Trim().Replace('＋', '+');

        // 禁用写法：空 / 无 / none / - / — / 禁用
        var lower = trimmed.ToLowerInvariant();
        if (lower is "无" or "none" or "-" or "—" or "禁用" or "不设置" or "无热键") return false;

        // 从头按「修饰键 +」推进；遇到第一个不是修饰键的片段，剩下的整段都当主键
        // （所以「Ctrl+小键盘+」这种主键名字里带加号的写法也能正确解析）。
        uint modifiers = 0;
        var rest = trimmed;
        while (true)
        {
            var plus = rest.IndexOf('+');
            if (plus < 0) break;

            var head = rest[..plus].Trim();
            if (!TryParseModifier(head, out var modifier)) break;

            modifiers |= modifier;
            rest = rest[(plus + 1)..];
        }

        rest = rest.Trim();
        if (rest.Length == 0) return false;

        if (!TryParseMainKey(rest, out var virtualKey)) return false;
        if (virtualKey == 0 || KeyNames.IsModifier(virtualKey)) return false;

        gesture = new HotkeyGesture(modifiers & 0x000F, virtualKey);
        return true;
    }

    /// <summary>
    /// 把一段文字解析成主键虚拟键码（供 <see cref="TryParse"/> 与界面热键捕获共用）。
    /// 修饰键不算主键（只按下 Ctrl 时由界面继续等主键）。
    /// </summary>
    public static bool TryParseMainKey(string text, out int virtualKey)
    {
        virtualKey = 0;
        if (!KeyNames.TryParseKey(text, out var parsed)) return false;
        if (parsed == 0 || KeyNames.IsModifier(parsed)) return false;
        virtualKey = parsed;
        return true;
    }

    /// <summary>格式化回配置字符串，例如 <c>Ctrl+Alt+K</c>；空手势 / 只有修饰键的无效手势返回空字符串（= 不注册）。</summary>
    public override string ToString()
    {
        if (IsEmpty || KeyNames.IsModifier(VirtualKey)) return "";

        var builder = new StringBuilder();
        if ((Modifiers & ModControl) != 0) builder.Append("Ctrl+");
        if ((Modifiers & ModAlt) != 0) builder.Append("Alt+");
        if ((Modifiers & ModShift) != 0) builder.Append("Shift+");
        if ((Modifiers & ModWin) != 0) builder.Append("Win+");
        builder.Append(KeyNames.Display(VirtualKey));
        return builder.ToString();
    }

    /// <summary>解析一个修饰键片段（大小写不敏感；不认识返回 false）。</summary>
    private static bool TryParseModifier(string text, out uint modifier)
    {
        modifier = 0;
        switch (text.Trim().ToLowerInvariant())
        {
            case "ctrl" or "control" or "控制" or "ctrl键":
                modifier = ModControl;
                return true;
            case "alt" or "菜单" or "alt键":
                modifier = ModAlt;
                return true;
            case "shift" or "上档" or "shift键":
                modifier = ModShift;
                return true;
            case "win" or "windows" or "meta" or "超级键" or "win键":
                modifier = ModWin;
                return true;
            default:
                return false;
        }
    }
}
