using System.Text;

namespace YuLiangToolkit.Tools.Kps.Services;

/// <summary>
/// Windows 虚拟键码（VK）→ 界面文字（中文短名）。
///
/// <para>键位格子上的文字、设置热键、Overlay 都走它：普通字母数字显示成大写字符，
/// 特殊键显示中文（空格 / 回车 / 左Shift / 方向键…），未知键返回 <c>VK_0xNN</c> 便于排错。</para>
/// </summary>
public static class KeyNames
{
    private static readonly Dictionary<int, string> Names = BuildNames();

    /// <summary>虚拟键码 → 显示名（不会抛异常 / 不会返回空）。</summary>
    public static string Display(int virtualKey)
    {
        if (virtualKey == 0) return "—";
        if (Names.TryGetValue(virtualKey, out var name)) return name;

        // 字母 / 数字以外的 OEM 键按范围兜底
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

    private static Dictionary<int, string> BuildNames()
    {
        var map = new Dictionary<int, string>
        {
            [0x08] = "退格", [0x09] = "Tab", [0x0D] = "回车", [0x13] = "暂停",
            [0x14] = "CapsLock", [0x1B] = "Esc", [0x20] = "空格", [0x21] = "PageUp",
            [0x22] = "PageDown", [0x23] = "End", [0x24] = "Home",
            [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓",
            [0x2C] = "PrintScreen", [0x2D] = "Insert", [0x2E] = "Delete",
            [0x5B] = "左Win", [0x5C] = "右Win", [0x5D] = "菜单键",
            [0x90] = "NumLock", [0x91] = "ScrollLock",
            [0xA0] = "左Shift", [0xA1] = "右Shift", [0xA2] = "左Ctrl", [0xA3] = "右Ctrl",
            [0xA4] = "左Alt", [0xA5] = "右Alt",
            [0xA6] = "浏览器后退", [0xA7] = "浏览器前进", [0xA8] = "浏览器刷新",
            [0xA9] = "浏览器停止", [0xAA] = "浏览器搜索", [0xAB] = "浏览器收藏",
            [0xAC] = "浏览器主页",
            [0xAD] = "静音", [0xAE] = "音量-", [0xAF] = "音量+",
            [0xB0] = "下一曲", [0xB1] = "上一曲", [0xB2] = "停止播放", [0xB3] = "播放/暂停",
            [0xBA] = "分号", [0xBB] = "等号", [0xBC] = "逗号", [0xBD] = "减号",
            [0xBE] = "句号", [0xBF] = "斜杠", [0xC0] = "反引号",
            [0xDB] = "左方括号", [0xDC] = "反斜杠", [0xDD] = "右方括号", [0xDE] = "单引号",
            [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt",
        };

        for (var i = 0x30; i <= 0x39; i++) map[i] = ((char)i).ToString();
        for (var i = 0x41; i <= 0x5A; i++) map[i] = ((char)i).ToString();
        for (var i = 0x60; i <= 0x69; i++) map[i] = "小键盘" + (i - 0x60);
        map[0x6A] = "小键盘*"; map[0x6B] = "小键盘+"; map[0x6D] = "小键盘-";
        map[0x6E] = "小键盘."; map[0x6F] = "小键盘/";
        for (var i = 0x70; i <= 0x87; i++) map[i] = "F" + (i - 0x6F);
        return map;
    }

    /// <summary>把「D F J K」这种空格分隔的文字解析回虚拟键码（不需要返回 false 的宽松版本）。</summary>
    public static IReadOnlyList<int> ParseList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
        var result = new List<int>();
        foreach (var token in text.Split(new[] { ' ', ',', '，', '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
            {
                result.Add(char.ToUpperInvariant(token[0]));
                continue;
            }
            var match = Names.FirstOrDefault(pair => string.Equals(pair.Value, token, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null) result.Add(match.Key);
        }
        return result;
    }

    /// <summary>调试用：把所有已知键名拼成一行。</summary>
    public static string DebugDump()
    {
        var builder = new StringBuilder();
        foreach (var pair in Names.OrderBy(p => p.Key))
            builder.Append($"0x{pair.Key:X2}={pair.Value} ");
        return builder.ToString().TrimEnd();
    }
}
