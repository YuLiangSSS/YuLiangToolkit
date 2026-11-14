using YuLiangToolkit.Core.Input;

namespace YuLiangToolkit.Tools.Vsrg.Core;

/// <summary>
/// 键位解析：配置里存的是**显示名**（<c>A</c> / <c>分号</c> / <c>Space</c>），
/// 游玩时要的是 WPF 的 <see cref="System.Windows.Input.Key"/>。
///
/// <para><b>为什么存显示名而不是 Key 枚举名</b>：和项目里快捷键体系同一套口径
/// （<see cref="KeyComboNames.Display"/> / <see cref="KeyComboNames.TryParseKey"/>），
/// 配置文件人眼可读、可手改，界面也能直接显示中文键名。</para>
///
/// <para><b>转换路线</b>：显示名 → 虚拟键码（Core 的词表）→ <see cref="System.Windows.Input.Key"/>
/// （用 <c>KeyInterop.KeyFromVirtualKey</c> / <c>VirtualKeyFromKey</c> 互转）。</para>
///
/// <para>⚠ 类名不叫 <c>KeyBinding</c>：<see cref="System.Windows.Input.KeyBinding"/> 是 WPF 自带类型，
/// 两个命名空间都在 using 里时会撞名（CS0104）。</para>
/// </summary>
public static class LaneKeys
{
    /// <summary>把显示名解析成 WPF 的 Key；解析不出来返回 null。</summary>
    public static System.Windows.Input.Key? ToKey(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        if (!KeyComboNames.TryParseKey(displayName, out var virtualKey) || virtualKey == 0) return null;

        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey);
        return key == System.Windows.Input.Key.None ? null : key;
    }

    /// <summary>把 WPF 的 Key 转成显示名（存进配置用）；不认识的键返回空串。</summary>
    public static string FromKey(System.Windows.Input.Key key)
    {
        if (key is System.Windows.Input.Key.None or System.Windows.Input.Key.DeadCharProcessed) return "";

        // 左右修饰键（左 Shift / 右 Ctrl）在 WPF 里是 Key.LeftShift / RightCtrl，
        // 而 Core 词表的通用名 "Shift" / "Ctrl" 也能显示它们 —— 这里保持「按键本身」的精确名。
        var virtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return "";

        var name = KeyComboNames.Display(virtualKey);
        return name == "—" ? "" : name;
    }

    /// <summary>
    /// 这个键能不能当游玩键：修饰键 / 系统键不接受（否则「按一下 Shift 就打了一轨」很容易误触，
    /// 而且改键时按 Esc 要用来取消捕获）。
    /// </summary>
    public static bool IsBindable(System.Windows.Input.Key key)
    {
        if (key == System.Windows.Input.Key.None) return false;

        var virtualKey = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;

        if (KeyComboNames.IsModifier(virtualKey)) return false;

        return key switch
        {
            System.Windows.Input.Key.Tab => false,
            System.Windows.Input.Key.Escape => false,
            System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin => false,
            System.Windows.Input.Key.Apps => false,
            _ => true,
        };
    }

    /// <summary>
    /// 按当前键位表建「虚拟键码 → 轨道」的查找表（游玩引擎每帧要用，别在帧循环里现算）。
    ///
    /// <para>同一个键绑到两轨时**先出现的轨生效**（和界面里的提示一致；界面会标红冲突）。</para>
    /// </summary>
    /// <param name="keyNames">1~10 轨的显示名。</param>
    /// <param name="keyCount">这份谱面实际用到的轨道数。</param>
    public static IReadOnlyDictionary<int, int> BuildLaneMap(IReadOnlyList<string> keyNames, int keyCount)
    {
        var map = new Dictionary<int, int>();
        var limit = Math.Min(keyCount, keyNames.Count);

        for (var column = 0; column < limit; column++)
        {
            if (!KeyComboNames.TryParseKey(keyNames[column], out var virtualKey) || virtualKey == 0) continue;
            map.TryAdd(virtualKey, column);
        }

        return map;
    }

    /// <summary>键位表里有没有重复（界面据此标红「这个键已经绑到第 N 轨」）。</summary>
    /// <param name="keyNames">1~10 轨的显示名。</param>
    /// <param name="duplicateIndex">重复出现的那一轨（0 基）；没有重复是 -1。</param>
    /// <param name="firstIndex">同一键第一次出现的那一轨（0 基）；没有重复是 -1。</param>
    public static bool HasDuplicate(IReadOnlyList<string> keyNames, out int duplicateIndex, out int firstIndex)
    {
        duplicateIndex = -1;
        firstIndex = -1;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var column = 0; column < keyNames.Count; column++)
        {
            var name = (keyNames[column] ?? "").Trim();
            if (name.Length == 0) continue;

            if (seen.TryGetValue(name, out var earlier))
            {
                duplicateIndex = column;
                firstIndex = earlier;
                return true;
            }

            seen[name] = column;
        }

        return false;
    }
}
