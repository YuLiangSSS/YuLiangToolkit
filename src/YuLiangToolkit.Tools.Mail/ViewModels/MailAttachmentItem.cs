namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>详情面板里的一个附件（只用于界面展示 / 保存）。</summary>
public sealed class MailAttachmentItem
{
    public MailAttachmentItem(MailAttachment attachment, MailAccountConfig account)
    {
        Attachment = attachment;
        Account = account;
    }

    public MailAttachment Attachment { get; }

    public MailAccountConfig Account { get; }

    /// <summary>「另存为」对话框的默认文件名。</summary>
    public string FileName => Attachment.FileName;

    /// <summary>人类可读大小。</summary>
    public string SizeText => Attachment.SizeText;

    /// <summary>列表里显示的一行文本。</summary>
    public string DisplayText => $"{FileName}（{SizeText}）";
}
