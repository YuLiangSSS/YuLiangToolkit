using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.Mail.ViewModels;
using YuLiangToolkit.Tools.Mail.Views;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>
/// 「邮箱代收」工具入口：实现 <see cref="ITool"/> 后会被外壳自动发现并挂到主页 / 侧边栏。
///
/// <para>功能定位：本机把多个邮箱（Gmail / QQ / 163 / 126 / Outlook / 企业邮箱…）的
/// 收件箱 / 已发送 / 垃圾箱通过 IMAP **只读**拉到同一个界面里看——不删除、不移动、不标记已读
/// （服务器上的状态保持不变），只解决「几个邮箱来回切太麻烦」。账号的授权码 / 应用专用密码
/// 用 Windows DPAPI 加密后写进配置。</para>
///
/// <para>QQ 邮箱使用「授权码」，Gmail 使用「应用专用密码」；具体申请方式见工具页右上角的帮助按钮，
/// 以及 <c>docs/tools/mail.md</c>。</para>
/// </summary>
public sealed class MailTool : ITool
{
    public string Id => "mail-collector";

    public string Name => "邮箱代收";

    public string Description => "把 Gmail / QQ / 163 / Outlook 等多个邮箱的收件箱、已发送、垃圾箱收到一起看：IMAP 只读拉取、账号文件夹导航、收件人 / 附件 / 优先级排序、附件拖到桌面；授权码用 DPAPI 加密保存，可跟随 Windows 系统代理。";

    public string EmojiIcon => "📬";

    public string Category => "网络";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "邮箱", "IMAP", "Gmail", "QQ", "代收" };

    public int Order => 26;

    public FrameworkElement CreateView()
        => new MailCollectorView { DataContext = new MailCollectorViewModel() };
}
