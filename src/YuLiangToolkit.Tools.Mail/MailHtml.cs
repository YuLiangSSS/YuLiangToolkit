namespace YuLiangToolkit.Tools.Mail;

/// <summary>
/// HTML 邮件的渲染包装（纯函数，自检可断言）：
/// 把邮件自带的 HTML 片段包成完整文档，并加一层安全 / 阅读样式。
///
/// <para><b>安全</b>：CSP 禁掉脚本 / 表单 / 框架 / 插件，只允许内联样式与图片（data: 和 http(s):）；
/// 工具里 WebView2 还会再关掉 JavaScript、开发工具和宿主对象。邮件里的远程图片会按原样加载
/// （和 Foxmail 一样方便看签名 / 商品图，但可能有追踪像素，见 docs/tools/mail.md 的安全提示）。</para>
/// </summary>
public static class MailHtml
{
    private const string ContentSecurityPolicy =
        "default-src 'none'; "
        + "img-src data: http: https:; "
        + "style-src 'unsafe-inline' data: http: https:; "
        + "font-src data:; "
        + "media-src data: http: https:;";

    /// <summary>把邮件 HTML 片段包成可以直接 <c>NavigateToString</c> 的完整文档。</summary>
    public static string BuildDocument(string htmlBody)
    {
        var body = htmlBody ?? "";
        return "<!DOCTYPE html>\n"
               + "<html><head><meta charset=\"utf-8\">\n"
               + "<meta name=\"referrer\" content=\"no-referrer\">\n"
               + "<meta http-equiv=\"Content-Security-Policy\" content=\"" + ContentSecurityPolicy + "\">\n"
               + "<style>\n"
               + "  :root { color-scheme: light; }\n"
               + "  html, body { margin: 0; padding: 0; background: #ffffff; }\n"
               + "  body { margin: 14px; color: #1f1f1f; font-family: \"Segoe UI\", \"Microsoft YaHei UI\", \"Microsoft YaHei\", Arial, sans-serif; "
               + "font-size: 14px; line-height: 1.65; word-wrap: break-word; overflow-wrap: anywhere; }\n"
               + "  img { max-width: 100%; height: auto; }\n"
               + "  table { max-width: 100%; border-collapse: collapse; }\n"
               + "  pre { white-space: pre-wrap; }\n"
               + "  a { color: #1a73e8; }\n"
               + "  blockquote { margin: 8px 0; padding-left: 10px; border-left: 3px solid #e0e0e0; color: #555; }\n"
               + "</style></head>\n"
               + "<body>" + body + "</body></html>";
    }
}
