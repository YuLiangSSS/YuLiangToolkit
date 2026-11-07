using YuLiangToolkit.Tools.Mail;

namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>收件列表里的一行（把 <see cref="MailBrief"/> 包成界面直接能绑的显示属性）。</summary>
public sealed class MailMessageRow
{
    public MailMessageRow(MailBrief brief)
    {
        Account = brief.Account;
        Folder = brief.Folder;
        UniqueId = brief.UniqueId;
        Subject = brief.Subject;
        FromText = brief.FromText;
        ToText = brief.ToText;
        Date = brief.Date;
        IsUnread = brief.IsUnread;
        SizeBytes = brief.SizeBytes;
        HasAttachments = brief.HasAttachments;
        Priority = brief.Priority;
    }

    /// <summary>来自哪个账号（列表里显示账号标签；拉详情时用它的配置重连）。</summary>
    public MailAccountConfig Account { get; }

    /// <summary>来自哪个系统文件夹（决定详情从哪个 IMAP 文件夹读取）。</summary>
    public MailFolderKind Folder { get; }

    /// <summary>IMAP UID（同一账号内唯一）。</summary>
    public uint UniqueId { get; }

    public string Subject { get; }

    public string FromText { get; }

    public string ToText { get; }

    public DateTimeOffset Date { get; }

    public bool IsUnread { get; }

    /// <summary>邮件大小（字节；0 = 服务器没给，排序 / 展示用）。</summary>
    public long SizeBytes { get; }

    public bool HasAttachments { get; }

    public MailPriorityLevel Priority { get; }

    /// <summary>列表显示当前方向的关键联系人：收到的邮件显示发件人，发出的邮件显示收件人。</summary>
    public string PartyText => Folder == MailFolderKind.Sent ? ToText : FromText;

    public string PartyLabel => Folder == MailFolderKind.Sent ? "收件人" : "发件人";

    /// <summary>人类可读大小（详情 / 排序提示用）。</summary>
    public string SizeText => SizeBytes > 0 ? MailText.FormatSize(SizeBytes) : "—";

    /// <summary>列表里的日期文本。</summary>
    public string DateText => MailText.FormatListDate(Date);

    /// <summary>详情里的完整日期文本。</summary>
    public string DateFullText => MailText.FormatFullDate(Date);

    /// <summary>账号标签。</summary>
    public string AccountBadge => Account.DisplayTitle;

    /// <summary>邮件夹标签，避免账号汇总视图里混淆收件箱和已发送。</summary>
    public string FolderBadge => MailFolders.Label(Folder);

    /// <summary>未读标记（未读时显示一个圆点）。</summary>
    public string UnreadMark => IsUnread ? "●" : "";

    public string AttachmentMark => HasAttachments ? "📎" : "";

    public bool IsHighPriority => Priority == MailPriorityLevel.High;

    public string PriorityMark => IsHighPriority ? "!" : "";
}
