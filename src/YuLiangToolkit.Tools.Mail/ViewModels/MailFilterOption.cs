namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>收件列表上方的「账号筛选」下拉项：<see cref="AccountId"/> 为 null = 全部账号。</summary>
public sealed class MailFilterOption
{
    public MailFilterOption(string? accountId, string label)
    {
        AccountId = accountId;
        Label = label;
    }

    /// <summary>要筛选的账号 Id；null = 不筛选。</summary>
    public string? AccountId { get; }

    /// <summary>下拉框显示文本。</summary>
    public string Label { get; }

    public override string ToString() => Label;
}
