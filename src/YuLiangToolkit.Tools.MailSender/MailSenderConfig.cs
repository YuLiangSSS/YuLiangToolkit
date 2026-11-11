using System.Text.Json;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>
/// 「邮件推送」的强类型配置 → <c>Config\mail-sender\config.json</c>。
///
/// <para>存的是「下次打开还要用」的东西：发件人 / 收件人 / 主题 / 正文草稿 / 附件清单 / 推送地址。
/// <b>API Key 不在这里明文落盘</b>：它加密后放在 <see cref="ApiKeyCipher"/>（DPAPI 密文）。</para>
///
/// <para>正文草稿 <see cref="BodyHtml"/> 存的是 HTML 源码：界面上的富文本编辑器加载时把它还原成
/// 可编辑内容，退出时再回写成 HTML —— 所以带格式的草稿重启后还在。</para>
/// </summary>
public sealed class MailSenderConfig : ToolConfig
{
    /// <summary>推送服务地址（默认 Resend 官方 API；可改成中转 / 自建兼容服务）。</summary>
    public string Endpoint { get; set; } = MailSenderDefaults.Endpoint;

    /// <summary>API Key 的 DPAPI 密文（<c>dpapi:...</c>）。明文只在内存里出现。</summary>
    public string ApiKeyCipher { get; set; } = "";

    /// <summary>发件人显示名（可空；留空就只发邮箱地址）。</summary>
    public string FromName { get; set; } = "";

    /// <summary>发件人邮箱。域名必须已经在推送服务里验证通过。</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>收件人（一行一个，也可用逗号 / 分号分隔）。</summary>
    public string To { get; set; } = "";

    /// <summary>抄送（可空）。</summary>
    public string Cc { get; set; } = "";

    /// <summary>密送（可空）。</summary>
    public string Bcc { get; set; } = "";

    /// <summary>回复地址（可空；留空就用发件人）。</summary>
    public string ReplyTo { get; set; } = "";

    /// <summary>主题。</summary>
    public string Subject { get; set; } = "";

    /// <summary>正文草稿（HTML 源码；富文本编辑器与源码框共用这一份）。</summary>
    public string BodyHtml { get; set; } = MailSenderDefaults.BodyHtml;

    /// <summary>附件清单（本机绝对路径；发送时才读文件内容并转 base64）。</summary>
    public List<string> Attachments { get; set; } = new();

    /// <summary>发送时附带纯文本正文（由 HTML 转换而来，给不显示 HTML 的客户端兜底）。</summary>
    public bool IncludePlainText { get; set; } = true;

    /// <summary>API Key 是否已填写（只读计算属性，不落盘）。</summary>
    [JsonIgnore]
    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKeyCipher);

    /// <summary>界面上的发件人完整写法：<c>名字 &lt;地址&gt;</c> 或只有地址。</summary>
    [JsonIgnore]
    public string FromDisplay => MailAddressLines.BuildFrom(FromName, FromAddress);

    /// <summary>
    /// 只保留仍然存在的附件（移动 / 删除过的路径从清单里剔掉），返回被剔除的数量。
    /// 打开工具时跑一遍，免得界面上挂着一排「文件不存在」。
    /// </summary>
    public int PruneMissingAttachments()
    {
        var before = Attachments.Count;
        Attachments.RemoveAll(path => string.IsNullOrWhiteSpace(path) || !File.Exists(path));
        return before - Attachments.Count;
    }

    /// <summary>把 JSON 里残留的空值收拾干净（手改配置写成 <c>null</c> 时不至于让界面崩）。</summary>
    public void Normalize()
    {
        ApiKeyCipher ??= "";
        FromName ??= "";
        FromAddress ??= "";
        To ??= "";
        Cc ??= "";
        Bcc ??= "";
        ReplyTo ??= "";
        Subject ??= "";
        BodyHtml ??= "";
        Attachments ??= new List<string>();
        if (string.IsNullOrWhiteSpace(Endpoint)) Endpoint = MailSenderDefaults.Endpoint;
    }
}

/// <summary>「邮件推送」的常量与默认草稿（工具本体与自检共用，避免两边各写一份字符串）。</summary>
public static class MailSenderDefaults
{
    /// <summary>Resend 官方发送邮件接口。</summary>
    public const string Endpoint = "https://api.resend.com/emails";

    /// <summary>请求里必须带 UA：Resend 会把没有 User-Agent 的请求直接挡在门外（403 / error code 1010）。</summary>
    public const string UserAgent = "YuLiangToolkit-MailSender/1.0";

    /// <summary>整封邮件上限（Resend 口径：附件按 base64 编码之后的体积算）。</summary>
    public const long MaxEmailBytesAfterBase64 = 40L * 1024 * 1024;

    /// <summary>单个请求里收件人（to + cc + bcc）的保守上限。</summary>
    public const int MaxRecipients = 50;

    /// <summary>第一次打开工具时正文里的示例草稿（让用户一眼看到格式效果，改掉就行）。</summary>
    public const string BodyHtml = """
        <h2>你好，这是一封测试邮件</h2>
        <p>这封邮件由 <b>Yu Liang Toolkit</b> 的「邮件推送」工具发出：
        富文本正文会在这里原样排版，<span style="color:#4c8dff;">彩色文字</span>、<i>斜体</i>、
        <u>下划线</u> 与 <a href="https://resend.com">链接</a> 都支持。</p>
        <ul>
          <li>右侧「预览」可以看真实渲染效果</li>
          <li>「HTML 源码」标签页可以直接改源码</li>
          <li>附件在下方添加，会随邮件一起推送</li>
        </ul>
        <p>祝使用愉快！</p>
        """;
}
