namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>
/// 收件列表排序（纯函数，自检可断言）：Foxmail 那种「日期 / 发件人 / 收件人 / 主题 / 账号 / 附件 / 优先级 / 未读 / 大小」
/// + 升序 / 降序。排序键都来自列表摘要，不额外联网。
/// </summary>
public static class MailSort
{
    /// <summary>界面下拉框的排序键（存配置的是这些干净值，不是中文标签）。</summary>
    public static readonly string[] Keys =
    {
        "date", "from", "to", "subject", "account", "attachments", "priority", "unread", "size",
    };

    /// <summary>按 <paramref name="sortBy"/> / <paramref name="descending"/> 排序；未知键按日期处理。</summary>
    public static IReadOnlyList<MailMessageRow> Apply(
        IEnumerable<MailMessageRow> rows,
        string? sortBy,
        bool descending)
    {
        var key = (sortBy ?? "date").Trim().ToLowerInvariant();
        return key switch
        {
            "from" => Order(rows, row => row.FromText, StringComparer.OrdinalIgnoreCase, descending),
            "to" => Order(rows, row => row.ToText, StringComparer.OrdinalIgnoreCase, descending),
            "subject" => Order(rows, row => row.Subject, StringComparer.OrdinalIgnoreCase, descending),
            "account" => Order(rows, row => row.Account.DisplayTitle, StringComparer.OrdinalIgnoreCase, descending),
            "attachments" => descending
                ? rows.OrderByDescending(row => row.HasAttachments).ThenByDescending(row => row.Date).ToList()
                : rows.OrderBy(row => row.HasAttachments).ThenByDescending(row => row.Date).ToList(),
            "priority" => descending
                ? rows.OrderByDescending(row => row.Priority).ThenByDescending(row => row.Date).ToList()
                : rows.OrderBy(row => row.Priority).ThenByDescending(row => row.Date).ToList(),
            "unread" => descending
                ? rows.OrderByDescending(row => row.IsUnread).ThenByDescending(row => row.Date).ToList()
                : rows.OrderBy(row => row.IsUnread).ThenByDescending(row => row.Date).ToList(),
            "size" => descending
                ? rows.OrderByDescending(row => row.SizeBytes).ThenByDescending(row => row.Date).ToList()
                : rows.OrderBy(row => row.SizeBytes).ThenByDescending(row => row.Date).ToList(),
            _ => descending
                ? rows.OrderByDescending(row => row.Date).ToList()
                : rows.OrderBy(row => row.Date).ToList(),
        };
    }

    private static IReadOnlyList<MailMessageRow> Order<TKey>(
        IEnumerable<MailMessageRow> rows,
        Func<MailMessageRow, TKey> selector,
        IComparer<TKey> comparer,
        bool descending)
        => descending
            ? rows.OrderByDescending(selector, comparer).ThenByDescending(row => row.Date).ToList()
            : rows.OrderBy(selector, comparer).ThenByDescending(row => row.Date).ToList();
}
