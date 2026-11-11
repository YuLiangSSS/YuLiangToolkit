using System.Windows;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.MailSender.ViewModels;
using YuLiangToolkit.Tools.MailSender.Views;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>
/// 「邮件推送」工具入口：实现 <see cref="ITool"/> 后会被外壳自动发现并挂到主页 / 侧边栏。
///
/// <para>功能定位：本机写一封<b>带格式的邮件</b>（富文本编辑器 / HTML 源码、附件、抄送密送），
/// 然后交给**邮件推送 API**发出去——目前接的是 Resend 的 <c>POST /emails</c>（手写 HTTP，不引官方 SDK）。
/// 和「邮箱代收」（IMAP 只读收信）正好互补：一个收，一个发。</para>
///
/// <para>API Key 用 Windows DPAPI 加密后写进配置，明文只在内存里；用法与踩坑见 <c>docs/tools/mail-sender.md</c>。</para>
/// </summary>
public sealed class MailSenderTool : ITool
{
    public string Id => "mail-sender";

    public string Name => "邮件推送";

    public string Description => "写好一封带格式的邮件（富文本编辑 / HTML 源码 / 附件 / 抄送密送 / 实时预览），交给邮件推送 API（Resend）发出去：请求摘要、返回 id、错误原文一目了然；API Key 用 DPAPI 加密保存。";

    public string EmojiIcon => "📮";

    public string Category => "网络";

    /// <summary>主页标签：点标签可快速筛出同类工具。</summary>
    public IReadOnlyList<string> Tags => new[] { "邮件", "发送", "推送", "API", "Resend", "富文本", "附件" };

    public int Order => 27;

    public FrameworkElement CreateView()
        => new MailSenderView { DataContext = new MailSenderViewModel() };
}
