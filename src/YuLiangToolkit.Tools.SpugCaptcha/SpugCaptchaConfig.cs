using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.SpugCaptcha;

/// <summary>
/// Spug 验证码测试工具的强类型配置 → <c>Config\spug-captcha\config.json</c>。
///
/// <para>只存「下次还要用」的输入：两栏的推送 URL、手机号 / 邮箱、有效分钟数、邮件场景。
/// 验证码本身（刚生成、等待本地校验的那串数字）是运行时状态，<b>不写配置</b>。</para>
///
/// <para>⚠ 推送 URL 里含模板编码，属于调用凭证；配置文件是明文，请勿把 Config 目录外传。</para>
/// </summary>
public sealed class SpugCaptchaConfig : ToolConfig
{
    /// <summary>短信模板推送 URL（Spug 控制台复制的完整调用地址，形如 https://push.spug.cc/sms/&lt;模板编码&gt;）。</summary>
    public string SmsUrl { get; set; } = "";

    /// <summary>接收短信的手机号（11 位，作为接口的 to 参数）。</summary>
    public string Phone { get; set; } = "";

    /// <summary>
    /// 短信模板的 number 参数 = 有效分钟数。模板含 <c>${number}</c> 时必填；
    /// 只含 <c>${code}</c> 的模板留空即可（留空就不传 number）。
    /// </summary>
    public string SmsMinutes { get; set; } = "5";

    /// <summary>邮箱模板推送 URL（形如 https://push.spug.cc/mail/&lt;模板编码&gt;）。</summary>
    public string MailUrl { get; set; } = "";

    /// <summary>接收邮箱（作为接口的 to 参数）。</summary>
    public string Email { get; set; } = "";

    /// <summary>邮件验证码的验证场景（Spug 的 scene 参数，最多 12 个字符，不能含链接 / 域名）。</summary>
    public string MailScene { get; set; } = "登录验证";

    /// <summary>邮件验证码有效分钟数（Spug 的 minute 参数，支持数字或中文数字）。</summary>
    public string MailMinutes { get; set; } = "5";
}
