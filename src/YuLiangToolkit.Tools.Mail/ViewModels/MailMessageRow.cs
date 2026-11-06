using YuLiangToolkit.Tools.Mail;

namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>收件列表里的一行（把 <see cref="MailBrief"/> 包成界面直接能绑的显示属性）。</summary>
public sealed class MailMessageRow
{
    public MailMessageRow(MailBrief brief)
    {
        Account = brief.Account;
        UniqueId = brief.UniqueId;
        Subject = brief.Subject;
        FromText = brief.FromText;
        Date = brief.Date;
        IsUnread = brief.IsUnread;
    }

    /// <summary>来自哪个账号（列表里显示账号标签；拉详情时用它的配置重连）。</summary>
    public MailAccountConfig Account { get; }

    /// <summary>IMAP UID（同一账号内唯一）。</summary>
    public uint UniqueId { get; }

    public string Subject { get; }

    public string FromText { get; }

    public DateTimeOffset Date { get; }

    public bool IsUnread { get; }

    /// <summary>列表里的日期文本。</summary>
    public string DateText => MailText.FormatListDate(Date);

    /// <summary>详情里的完整日期文本。</summary>
    public string DateFullText => MailText.FormatFullDate(Date);

    /// <summary>账号标签。</summary>
    public string AccountBadge => Account.DisplayTitle;

    /// <summary>未读标记（未读时显示一个圆点）。</summary>
    public string UnreadMark => IsUnread ? "●" : "";
}
