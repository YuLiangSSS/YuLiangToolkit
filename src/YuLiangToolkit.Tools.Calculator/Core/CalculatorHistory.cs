namespace YuLiangToolkit.Tools.Calculator.Core;

/// <summary>
/// 历史记录规则（纯逻辑）：每条存成 <c>"表达式 = 结果"</c> 的文本，新的在前、相邻去重、超上限裁剪。
///
/// <para>为什么存成一整串而不是拆成两个字段：配置里的历史是 <c>List&lt;string&gt;</c>（见
/// <c>CalculatorConfig.History</c>），一整串更方便手改 / 兼容旧配置，回填时再取等号左边即可。
/// 表达式里不可能出现 <c>=</c>（解析器不接受），所以按最后一个 <see cref="Separator"/> 切分是安全的。</para>
/// </summary>
public static class CalculatorHistory
{
    /// <summary>表达式和结果之间的分隔符。</summary>
    public const string Separator = " = ";

    /// <summary>拼一条历史记录：<c>表达式 = 结果</c>。</summary>
    public static string Compose(string? expression, string? resultText)
        => (expression?.Trim() ?? "") + Separator + (resultText ?? "");

    /// <summary>从历史记录里取出表达式（没有分隔符就把整条当表达式）。</summary>
    public static string GetExpression(string? entry)
    {
        entry ??= "";
        var index = entry.LastIndexOf(Separator, StringComparison.Ordinal);
        return index < 0 ? entry : entry[..index];
    }

    /// <summary>从历史记录里取出结果文本（没有分隔符返回空串）。</summary>
    public static string GetResultText(string? entry)
    {
        entry ??= "";
        var index = entry.LastIndexOf(Separator, StringComparison.Ordinal);
        return index < 0 ? "" : entry[(index + Separator.Length)..];
    }

    /// <summary>
    /// 新增一条历史：放到最前面；<b>和最近一条完全相同就跳过</b>（相邻去重，连按两次 Enter 不会刷屏），
    /// 最后按 <paramref name="limit"/> 裁剪（新的留下、旧的丢掉）。返回新列表，不改调用方的列表。
    /// </summary>
    public static List<string> Add(IReadOnlyList<string>? history, string? entry, int limit)
    {
        limit = Math.Max(1, limit);
        var result = new List<string>(limit);

        var text = entry?.Trim() ?? "";
        var isDuplicateOfNewest = string.IsNullOrEmpty(text)
                                  || (history is { Count: > 0 }
                                      && string.Equals(history[0], text, StringComparison.Ordinal));
        if (!isDuplicateOfNewest) result.Add(text);

        if (history is not null)
        {
            foreach (var item in history)
            {
                if (result.Count >= limit) break;
                if (string.IsNullOrWhiteSpace(item)) continue;
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>只按上限裁剪（配置里把「历史条数」改小时用），新条目留下。</summary>
    public static List<string> Trim(IReadOnlyList<string>? history, int limit)
    {
        limit = Math.Max(1, limit);
        var result = new List<string>(limit);
        if (history is null) return result;

        foreach (var item in history)
        {
            if (result.Count >= limit) break;
            if (string.IsNullOrWhiteSpace(item)) continue;
            result.Add(item);
        }
        return result;
    }

    /// <summary>清空历史。</summary>
    public static List<string> Clear() => new();
}

/// <summary>
/// 历史记录列表里的一行（给界面绑定的只读视图）：左边是当时的表达式，右边是当时算出的结果。
/// 点一行就把 <see cref="Expression"/> 回填进输入框。
/// </summary>
public sealed class CalculatorHistoryItem
{
    public CalculatorHistoryItem(string entry)
    {
        Entry = entry ?? "";
        Expression = CalculatorHistory.GetExpression(Entry);
        ResultText = CalculatorHistory.GetResultText(Entry);
    }

    /// <summary>原始文本（存进配置的那一串）。</summary>
    public string Entry { get; }

    /// <summary>表达式部分（回填输入框用）。</summary>
    public string Expression { get; }

    /// <summary>结果部分（显示用）。</summary>
    public string ResultText { get; }
}
