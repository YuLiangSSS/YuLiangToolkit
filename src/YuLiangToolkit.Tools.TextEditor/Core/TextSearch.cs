using System.Diagnostics.CodeAnalysis;

namespace YuLiangToolkit.Tools.TextEditor.Core;

/// <summary>
/// 查找 / 替换的纯逻辑（自检直接断言）：在一段文字里找全部匹配、从光标处向前 / 向后环绕查找、
/// 全部替换。大小写开关映射成 Ordinal / OrdinalIgnoreCase（文本编辑不搞区域文化比较，规则可预期）。
/// </summary>
public static class TextSearch
{
    /// <summary>空模式不算任何匹配（查找框没输入时不乱跳）。</summary>
    public static bool IsValidPattern([NotNullWhen(true)] string? pattern)
        => !string.IsNullOrEmpty(pattern);

    /// <summary>找全部匹配的起点下标（模式为空返回空表）。</summary>
    public static List<int> FindAll(string text, string pattern, bool matchCase)
    {
        var result = new List<int>();
        if (!IsValidPattern(pattern) || text.Length == 0) return result;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var index = text.IndexOf(pattern, comparison);
        while (index >= 0)
        {
            result.Add(index);
            index = text.IndexOf(pattern, index + Math.Max(1, pattern.Length), comparison);
        }
        return result;
    }

    /// <summary>
    /// 从 <paramref name="fromOffset"/>（含）往后找第一个匹配，找不到就环绕到开头找。
    /// 返回匹配起点；没有匹配返回 <c>null</c>。
    /// </summary>
    public static int? NextMatch(string text, string pattern, bool matchCase, int fromOffset)
    {
        if (!IsValidPattern(pattern) || text.Length == 0) return null;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = Math.Clamp(fromOffset, 0, text.Length);
        var found = text.IndexOf(pattern, start, comparison);
        if (found >= 0) return found;

        // 环绕：从文件开头再找一次（找到的就是全局第一个匹配）。
        found = text.IndexOf(pattern, 0, comparison);
        return found >= 0 ? found : null;
    }

    /// <summary>
    /// 从 <paramref name="fromOffset"/>（不含，一般传当前匹配的起点）往前找最后一个匹配，
    /// 找不到就环绕到文件末尾。返回匹配起点；没有匹配返回 <c>null</c>。
    /// </summary>
    public static int? PreviousMatch(string text, string pattern, bool matchCase, int fromOffset)
    {
        if (!IsValidPattern(pattern) || text.Length == 0) return null;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var searchEnd = Math.Clamp(fromOffset, 0, text.Length);
        var head = text[..searchEnd];
        var found = head.LastIndexOf(pattern, comparison);
        if (found >= 0) return found;

        // 环绕：从文件末尾往前找
        found = text.LastIndexOf(pattern, comparison);
        return found >= 0 ? found : null;
    }

    /// <summary>全部替换：返回新文字与替换次数（模式为空时原样返回、0 次）。</summary>
    public static (string Text, int Count) ReplaceAll(string text, string pattern, string replacement, bool matchCase)
    {
        if (!IsValidPattern(pattern)) return (text, 0);
        if (text.Length == 0) return (text, 0);

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var count = 0;
        var sb = new System.Text.StringBuilder();
        var position = 0;
        while (position <= text.Length - pattern.Length)
        {
            var index = text.IndexOf(pattern, position, comparison);
            if (index < 0) break;
            sb.Append(text, position, index - position);
            sb.Append(replacement ?? "");
            position = index + pattern.Length;
            count++;
        }
        if (count == 0) return (text, 0);
        sb.Append(text[position..]);
        return (sb.ToString(), count);
    }
}
