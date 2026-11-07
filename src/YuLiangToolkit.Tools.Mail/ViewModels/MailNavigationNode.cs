using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Mail.ViewModels;

/// <summary>
/// 左侧邮件夹导航的一行：<see cref="Folder"/> 为 null 时代表账号汇总，否则代表该账号下的一个系统文件夹。
/// 账号和其中的文件夹平铺展示，既保留 Foxmail 式层级感，也让 WPF ListBox 的选择状态保持简单可靠。
/// </summary>
public sealed class MailNavigationNode : ObservableObject
{
    private int _messageCount;
    private string _statusText = "";

    public MailNavigationNode(MailAccountConfig account, MailFolderKind? folder)
    {
        Account = account;
        Folder = folder;
    }

    public MailAccountConfig Account { get; }

    /// <summary>null = 账号汇总（全部邮件），否则 = 收件箱 / 已发送 / 垃圾箱。</summary>
    public MailFolderKind? Folder { get; }

    public bool IsAccount => Folder is null;

    public bool IsFolder => Folder is not null;

    public string FolderKey => Folder is { } folder ? MailFolders.Key(folder) : "all";

    public string NavigationKey => $"{Account.Id}:{FolderKey}";

    public string DisplayName => Folder is { } folder ? MailFolders.Label(folder) : "全部邮件";

    public string Glyph => Folder is { } folder ? MailFolders.Glyph(folder) : "👤";

    public int MessageCount
    {
        get => _messageCount;
        set
        {
            if (Set(ref _messageCount, value)) OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => MessageCount.ToString();

    /// <summary>文件夹不可用 / 读取失败的短提示；正常为空。</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value ?? "");
    }
}
