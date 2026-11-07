namespace YuLiangToolkit.Tools.Mail;

/// <summary>邮箱代收支持浏览的系统文件夹。</summary>
public enum MailFolderKind
{
    Inbox,
    Sent,
    Trash,
}

/// <summary>邮件摘要里的优先级（来自 Priority / X-Priority / Importance 头）。</summary>
public enum MailPriorityLevel
{
    Low,
    Normal,
    High,
}

/// <summary>系统文件夹的稳定键、中文名和列表图标。</summary>
public static class MailFolders
{
    public static readonly MailFolderKind[] All =
    {
        MailFolderKind.Inbox,
        MailFolderKind.Sent,
        MailFolderKind.Trash,
    };

    /// <summary>配置文件 / 筛选用的稳定键。</summary>
    public static string Key(MailFolderKind folder) => folder switch
    {
        MailFolderKind.Sent => "sent",
        MailFolderKind.Trash => "trash",
        _ => "inbox",
    };

    /// <summary>界面显示名。</summary>
    public static string Label(MailFolderKind folder) => folder switch
    {
        MailFolderKind.Sent => "已发送",
        MailFolderKind.Trash => "垃圾箱",
        _ => "收件箱",
    };

    /// <summary>左侧导航图标。</summary>
    public static string Glyph(MailFolderKind folder) => folder switch
    {
        MailFolderKind.Sent => "📤",
        MailFolderKind.Trash => "🗑",
        _ => "📥",
    };

    /// <summary>把配置里的字符串恢复成文件夹；未知值按收件箱处理。</summary>
    public static MailFolderKind ParseKey(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "sent" => MailFolderKind.Sent,
        "trash" => MailFolderKind.Trash,
        _ => MailFolderKind.Inbox,
    };
}
