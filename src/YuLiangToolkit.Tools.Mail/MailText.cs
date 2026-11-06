using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>
/// 邮件展示用的小工具（纯函数，自检里逐条断言）：
/// 地址格式化 / 邮箱打码 / HTML 转纯文本 / 附件文件名清洗 / 大小与日期格式化。
/// </summary>
public static class MailText
{
    /// <summary>把地址列表格式化成「名字 &lt;地址&gt;、名字 &lt;地址&gt;」；没有地址时返回 fallback。</summary>
    public static string FormatAddressList(InternetAddressList? addresses, string fallback = "（未知）")
    {
        if (addresses is null || addresses.Count == 0) return fallback;
        var parts = new List<string>();
        foreach (var address in addresses)
        {
            switch (address)
            {
                case MailboxAddress mailbox:
                    parts.Add(string.IsNullOrWhiteSpace(mailbox.Name)
                        ? mailbox.Address
                        : $"{mailbox.Name} <{mailbox.Address}>");
                    break;
                case GroupAddress group:
                    parts.Add(string.IsNullOrWhiteSpace(group.Name) ? group.Members.ToString() : group.Name);
                    break;
                default:
                    parts.Add(address.ToString());
                    break;
            }
        }

        return parts.Count == 0 ? fallback : string.Join("、", parts);
    }

    /// <summary>只取第一个发件人显示（列表里用）。</summary>
    public static string FormatFirstAddress(InternetAddressList? addresses, string fallback = "（未知发件人）")
    {
        if (addresses is null || addresses.Count == 0) return fallback;
        return addresses[0] is MailboxAddress mailbox
            ? (string.IsNullOrWhiteSpace(mailbox.Name) ? mailbox.Address : $"{mailbox.Name} <{mailbox.Address}>")
            : addresses[0].ToString();
    }

    /// <summary>
    /// 邮箱打码（日志里用）：<c>fantasyuliang@qq.com</c> → <c>f***g@qq.com</c>；
    /// 本地部分太短时保留首字符。
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return "（空）";
        var text = email.Trim();
        var at = text.LastIndexOf('@');
        if (at <= 0) return text.Length <= 2 ? text[..1] + "*" : text[..1] + new string('*', text.Length - 1);

        var local = text[..at];
        var domain = text[(at + 1)..];
        var maskedLocal = local.Length switch
        {
            0 => "",
            1 => local[..1] + "**",
            2 => local[..1] + "*",
            _ => local[..1] + "***" + local[^1..],
        };
        return $"{maskedLocal}@{domain}";
    }

    /// <summary>
    /// 把 HTML 正文转成可读的纯文本（工具页里不加载网页内核，避免为看一封邮件引入 WebView2）：
    /// 去掉 script / style / 注释和所有标签，保留段落换行，解码 <c>&amp;nbsp;</c> 等实体。
    /// 转换失败 / 输入为空返回空字符串。
    /// </summary>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = html;
        text = Regex.Replace(text, @"<(script|style|head)\b[^>]*>.*?</\1\s*>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(p|div|li|tr|h[1-6]|blockquote|table|ul|ol|section|article)\s*>", "\n",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<(p|div|li|tr|h[1-6]|blockquote|table|ul|ol|section|article)\b[^>]*>", "\n",
            RegexOptions.IgnoreCase);
        // 图片的 alt 文本至少保留一点信息（邮件里的签名 / 按钮常用图片）
        text = Regex.Replace(text, @"<img\b[^>]*\balt\s*=\s*""([^""]*)""[^>]*>", " $1 ",
            RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00A0', ' ');

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new List<string>();
        foreach (var rawLine in lines)
        {
            var line = Regex.Replace(rawLine, @"[ \t]+", " ").Trim();
            if (line.Length == 0)
            {
                // 连续空行只留一行
                if (result.Count > 0 && result[^1].Length > 0) result.Add("");
                continue;
            }

            result.Add(line);
        }

        while (result.Count > 0 && result[^1].Length == 0) result.RemoveAt(result.Count - 1);
        return string.Join("\n", result);
    }

    /// <summary>
    /// 清洗附件文件名：去掉路径、非法字符，过长截断，保留扩展名；空名用 fallback。
    /// 只用于「另存为」对话框的默认文件名，真正的路径由用户确认。
    /// </summary>
    public static string SanitizeFileName(string? fileName, string fallback = "附件")
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fallback;

        var name = fileName.Trim().Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(Array.IndexOf(invalid, ch) >= 0 || ch == ':' ? '_' : ch);

        name = builder.ToString().Trim().TrimEnd('.', ' ');
        if (name.Length == 0) return fallback;

        // 保留扩展名，主体最多 120 字符（Windows 路径上限兜底）
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length > 120) stem = stem[..120];
        name = stem + extension;
        return name.Length == 0 ? fallback : name;
    }

    /// <summary>字节数 → 人类可读大小（附件列表用）。</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.##} GB";
    }

    /// <summary>列表里的日期：未知日期显示「—」，当年只显示月-日 时:分，跨年显示完整日期。</summary>
    public static string FormatListDate(DateTimeOffset date)
    {
        if (date == DateTimeOffset.MinValue) return "—";
        var local = date.ToLocalTime();
        return local.Year == DateTime.Now.Year
            ? local.ToString("MM-dd HH:mm")
            : local.ToString("yyyy-MM-dd");
    }

    /// <summary>详情里的完整日期。</summary>
    public static string FormatFullDate(DateTimeOffset date)
        => date == DateTimeOffset.MinValue ? "—" : date.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    /// <summary>正文摘要（列表里暂未使用，留给后续做预览）。</summary>
    public static string Snippet(string? body, int maxLength = 120)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        var text = Regex.Replace(body, @"\s+", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
