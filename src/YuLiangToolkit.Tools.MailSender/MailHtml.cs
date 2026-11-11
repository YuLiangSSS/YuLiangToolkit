using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>
/// HTML ⇄ 纯文本的纯逻辑部分：给「不显示 HTML 的客户端」生成兜底正文，
/// 以及把用户写的 HTML 片段包成可预览的完整文档。
///
/// <para>富文本编辑器（<see cref="Views.RichTextHtml"/>）负责 WPF 文档 ↔ HTML，这里只管 HTML 文本本身。</para>
/// </summary>
public static class MailHtml
{
    private static readonly Regex ScriptStylePattern = new(
        @"<(script|style|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CommentPattern = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlockBreakPattern = new(
        @"</?(p|div|br|tr|h[1-6]|li|blockquote|table|ul|ol|section|article|header|footer)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlankLinesPattern = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex ManyNewlinesPattern = new(@"\n{3,}", RegexOptions.Compiled);

    /// <summary>
    /// HTML → 纯文本：去掉脚本 / 样式 / 注释，块级标签转换行，解码实体，压缩多余空行。
    /// 用来填 Resend 的 <c>text</c> 字段（部分客户端 / 通知预览只读纯文本）。
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = CommentPattern.Replace(html, "");
        text = ScriptStylePattern.Replace(text, "");
        text = BlockBreakPattern.Replace(text, "\n");
        text = TagPattern.Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = BlankLinesPattern.Replace(text, "\n");
        text = ManyNewlinesPattern.Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>
    /// 把一段 HTML 片段包成完整文档供预览：白底、正文自适应宽度、图片不超宽，并且**禁用脚本**。
    ///
    /// <para>预览用 WebView2 时脚本本来就关掉了（<c>IsScriptEnabled = false</c>），
    /// 这里再加一层 CSP，双保险——毕竟正文是用户从别处粘来的，不该有任何机会执行代码。</para>
    /// </summary>
    public static string BuildPreviewDocument(string? html)
    {
        var body = string.IsNullOrWhiteSpace(html) ? "<p style=\"color:#98a2b3;\">（正文还是空的）</p>" : html;
        var builder = new StringBuilder(body.Length + 900);
        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"zh-CN\"><head><meta charset=\"utf-8\">");
        builder.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src data: http: https: cid:; style-src 'unsafe-inline';\">");
        builder.AppendLine("<style>");
        builder.AppendLine("html,body{margin:0;padding:0;background:#ffffff;}");
        builder.AppendLine("body{padding:22px 26px;color:#1f2733;font-family:-apple-system,'Segoe UI','Microsoft YaHei UI',sans-serif;font-size:14px;line-height:1.7;}");
        builder.AppendLine("img{max-width:100%;height:auto;}");
        builder.AppendLine("table{border-collapse:collapse;}");
        builder.AppendLine("td,th{padding:6px 9px;border:1px solid #dfe4ee;}");
        builder.AppendLine("blockquote{margin:12px 0;padding:8px 14px;border-left:3px solid #4c8dff;background:#f4f7ff;}");
        builder.AppendLine("a{color:#2f6fdb;}");
        builder.AppendLine("</style></head><body>");
        builder.Append(body);
        builder.AppendLine("</body></html>");
        return builder.ToString();
    }

    /// <summary>
    /// 正文是不是「空」：去掉标签与空白后没有文字、也没有图片 —— 这时不允许发送
    /// （Resend 要求 html / text 至少有一个非空）。
    /// </summary>
    public static bool IsEmptyBody(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;
        if (Regex.IsMatch(html, "<img\\b", RegexOptions.IgnoreCase)) return false;
        var text = ToPlainText(html);
        if (text.Trim().Length > 0) return false;
        // 只剩 &nbsp; 之类的空实体也算空
        return WebUtility.HtmlDecode(text).Trim().Length == 0;
    }
}
