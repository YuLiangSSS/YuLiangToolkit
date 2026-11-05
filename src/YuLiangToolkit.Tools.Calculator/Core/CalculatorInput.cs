namespace YuLiangToolkit.Tools.Calculator.Core;

/// <summary>
/// 计算器的「输入构造」规则（纯字符串处理，不碰界面）：按键盘按钮 / 打字时怎么改输入框里的表达式。
/// 单独抽出来是为了能脱离 WPF 直接被自检断言（见 <c>CalculatorChecks</c>）。
///
/// <para>约定：所有方法都容忍 null / 空串，返回的一定是可用字符串（不抛异常）。</para>
/// </summary>
public static class CalculatorInput
{
    /// <summary>
    /// 追加一段文本。唯一的特殊处理是<b>小数点去重</b>：当前正在输入的这个数字里已经有小数点时，
    /// 再按 <c>.</c> 不再追加（避免出现 <c>1.2.3</c> 这种必然解析失败的输入）。
    /// </summary>
    public static string Append(string? current, string? token)
    {
        current ??= "";
        if (string.IsNullOrEmpty(token)) return current;
        if (token == "." && !CanAppendDecimalPoint(current)) return current;
        return current + token;
    }

    /// <summary>当前（末尾那个数字里）还能不能再加小数点。</summary>
    public static bool CanAppendDecimalPoint(string? current)
    {
        current ??= "";
        var start = current.Length;
        while (start > 0 && (char.IsDigit(current[start - 1]) || current[start - 1] == '.')) start--;
        return !current.AsSpan(start).Contains('.');
    }

    /// <summary>退格：删掉最后一个字符（空串返回空串）。</summary>
    public static string Backspace(string? current)
        => string.IsNullOrEmpty(current) ? "" : current[..^1];

    /// <summary>清空输入。</summary>
    public static string Clear() => "";

    /// <summary>
    /// 正负号（±）：
    /// <list type="bullet">
    ///   <item>空输入 → <c>-</c>（接着打数字就是负数）；</item>
    ///   <item>整条就是一个数字（可带负号）→ 直接在数字前加 / 去负号，例如 <c>12</c> ↔ <c>-12</c>；</item>
    ///   <item>更复杂的表达式 → 整体取负 <c>-(…)</c>，再按一次还原（括号配平才还原，避免把用户自己的括号拆坏）。</item>
    /// </list>
    /// </summary>
    public static string ToggleSign(string? current)
    {
        current ??= "";
        if (current.Length == 0) return "-";

        if (IsPlainNumber(current))
            return current[0] == '-' ? current[1..] : "-" + current;

        // 已经被整体取负过（-(…)）→ 还原来
        if (current.StartsWith("-(", StringComparison.Ordinal) && current.EndsWith(')'))
        {
            var inner = current[2..^1];
            if (HasBalancedParentheses(inner)) return inner;
        }

        return "-(" + current + ")";
    }

    /// <summary>末尾是不是一个「还能继续接数字 / 可以当作操作数」的字符（Ans 插入时判断要不要补乘号）。</summary>
    public static bool EndsWithValue(string? current)
    {
        if (string.IsNullOrEmpty(current)) return false;
        var ch = current[^1];
        return char.IsDigit(ch) || ch == ')' || char.IsLetter(ch)
               || ch == 'π' || ch == '∞' || ch == '²' || ch == '³' || ch == '!';
    }

    /// <summary>
    /// 插入一个「数字文本」（Ans 上次结果用）：末尾已经是操作数时补一个省略乘号 <c>*</c>，
    /// 否则直接接上。例如 <c>2+</c> 插 <c>1.5</c> → <c>2+1.5</c>；<c>2</c> 插 <c>1.5</c> → <c>2*1.5</c>。
    /// </summary>
    public static string InsertNumber(string? current, string? numberText)
    {
        current ??= "";
        if (string.IsNullOrWhiteSpace(numberText)) return current;
        return EndsWithValue(current) ? current + "*" + numberText : current + numberText;
    }

    /// <summary>x²：在末尾操作数后面加 <c>²</c>（引擎会规范成 <c>^2</c>）；末尾不是操作数时不动。</summary>
    public static string AppendSquare(string? current)
    {
        current ??= "";
        return EndsWithValue(current) ? current + "²" : current;
    }

    /// <summary>1/x：空输入 → <c>1/</c>；已有表达式 → 整体取倒数 <c>1/(…)</c>。</summary>
    public static string WrapReciprocal(string? current)
    {
        current ??= "";
        return current.Length == 0 ? "1/" : "1/(" + current + ")";
    }

    /// <summary>整条是不是一个普通数字（可带一个负号、若干数字、最多一个小数点，且至少一位数字）。</summary>
    public static bool IsPlainNumber(string? text)
    {
        text ??= "";
        var i = text.StartsWith('-') ? 1 : 0;
        if (i >= text.Length) return false;

        var hasDigit = false;
        var hasDot = false;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsDigit(ch))
            {
                hasDigit = true;
                continue;
            }
            if (ch == '.' && !hasDot)
            {
                hasDot = true;
                continue;
            }
            return false;
        }
        return hasDigit;
    }

    /// <summary>括号是否配平（只数圆括号：用户手打表达式时最常错的就是它）。</summary>
    public static bool HasBalancedParentheses(string? text)
    {
        var depth = 0;
        foreach (var ch in text ?? "")
        {
            if (ch == '(') depth++;
            else if (ch == ')' && --depth < 0) return false;
        }
        return depth == 0;
    }
}
