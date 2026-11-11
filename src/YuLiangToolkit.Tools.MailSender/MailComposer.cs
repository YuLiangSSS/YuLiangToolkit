namespace YuLiangToolkit.Tools.MailSender;

/// <summary>编辑器里插入的一张行内图片：本机路径 + 发送时用的 <c>content_id</c>（HTML 里写成 <c>cid:…</c>）。</summary>
public sealed record InlineImage(string FilePath, string ContentId);

/// <summary>组装结果：要么拿到可以发的请求，要么拿到一组给用户看的中文错误。</summary>
public sealed record MailComposeResult(
    ResendEmailRequest? Request,
    IReadOnlyList<string> Errors,
    long AttachmentBytesAfterBase64)
{
    /// <summary>校验通过、可以发送。</summary>
    public bool Success => Request is not null && Errors.Count == 0;

    /// <summary>第一条错误（状态行显示它，其余进日志）。</summary>
    public string FirstError => Errors.Count == 0 ? "" : Errors[0];
}

/// <summary>
/// 把界面上的输入组装成 <see cref="ResendEmailRequest"/>，并做发送前的全部校验。
///
/// <para>独立成静态类是为了让自检能直接断言（不建窗口、不联网）：收件人拆分与上限、主题 / 正文非空、
/// 附件体积（base64 之后 40MB）、危险扩展名、行内图片转 <c>cid:</c>、纯文本兜底。
/// 读文件通过 <c>readFile</c> 注入，自检里用假的字节源即可。</para>
/// </summary>
public static class MailComposer
{
    /// <summary>组装请求。</summary>
    /// <param name="config">当前配置（发件人 / 收件人 / 主题等）。</param>
    /// <param name="html">正文 HTML（行内图片的 <c>src</c> 已经是 <c>cid:…</c>，由界面侧的解析器换好）。</param>
    /// <param name="attachments">附件本机路径。</param>
    /// <param name="inlineImages">行内图片（作为带 <c>content_id</c> 的附件一起上传）。</param>
    /// <param name="readFile">读文件字节；null 用 <see cref="File.ReadAllBytes"/>（自检注入假数据）。</param>
    public static MailComposeResult Compose(
        MailSenderConfig config,
        string? html,
        IEnumerable<string>? attachments = null,
        IEnumerable<InlineImage>? inlineImages = null,
        Func<string, byte[]?>? readFile = null)
    {
        var errors = new List<string>();

        // ---- 发件人 ----
        var fromAddress = MailAddressLines.ExtractAddress(config.FromAddress);
        if (fromAddress.Length == 0) errors.Add("请填写发件人邮箱地址");
        else if (!MailAddressLines.IsValidEmail(fromAddress)) errors.Add($"发件人邮箱地址不合法：{fromAddress}");

        // ---- 收件人 / 抄送 / 密送 / 回复 ----
        var to = MailAddressLines.Parse(config.To);
        if (to.Addresses.Count == 0) errors.Add("请填写至少一个收件人");
        AddInvalidHint(errors, "收件人", to);

        var cc = MailAddressLines.Parse(config.Cc);
        AddInvalidHint(errors, "抄送", cc);

        var bcc = MailAddressLines.Parse(config.Bcc);
        AddInvalidHint(errors, "密送", bcc);

        var replyTo = MailAddressLines.Parse(config.ReplyTo);
        AddInvalidHint(errors, "回复地址", replyTo);

        var recipientCount = to.Addresses.Count + cc.Addresses.Count + bcc.Addresses.Count;
        if (recipientCount > MailSenderDefaults.MaxRecipients)
            errors.Add($"收件人（含抄送 / 密送）共 {recipientCount} 个，超过上限 {MailSenderDefaults.MaxRecipients} 个");

        // ---- 主题 / 正文 ----
        var subject = (config.Subject ?? "").Trim();
        if (subject.Length == 0) errors.Add("请填写邮件主题");

        var bodyHtml = html ?? "";
        if (MailHtml.IsEmptyBody(bodyHtml)) errors.Add("正文是空的：写点内容或插入一张图片再发送");

        // ---- 附件 ----
        var attachList = new List<ResendAttachment>();
        long encodedBytes = 0;
        var read = readFile ?? ReadAllBytesOrNull;

        foreach (var image in inlineImages ?? Enumerable.Empty<InlineImage>())
        {
            var bytes = read(image.FilePath);
            if (bytes is null)
            {
                errors.Add($"行内图片读不到：{Path.GetFileName(image.FilePath)}");
                continue;
            }
            encodedBytes += MailAttachmentItem.Base64SizeOf(bytes.Length);
            attachList.Add(new ResendAttachment(
                Path.GetFileName(image.FilePath),
                Convert.ToBase64String(bytes),
                MailAttachmentItem.ContentTypeOf(image.FilePath),
                image.ContentId));
        }

        foreach (var path in MailAttachmentItem.Distinct(attachments ?? Enumerable.Empty<string>()))
        {
            var name = Path.GetFileName(path);
            if (MailAttachmentItem.IsBlockedExtension(path))
            {
                errors.Add($"附件类型被推送服务拒收（可执行文件 / 脚本类）：{name}");
                continue;
            }

            var bytes = read(path);
            if (bytes is null)
            {
                errors.Add($"附件读不到（文件被移动或删除？）：{name}");
                continue;
            }

            encodedBytes += MailAttachmentItem.Base64SizeOf(bytes.Length);
            attachList.Add(new ResendAttachment(
                name,
                Convert.ToBase64String(bytes),
                MailAttachmentItem.ContentTypeOf(path)));
        }

        if (encodedBytes > MailSenderDefaults.MaxEmailBytesAfterBase64)
        {
            errors.Add($"附件太大：base64 编码后共 {Core.Storage.FileSize.Format(encodedBytes)}，" +
                       $"超过上限 {Core.Storage.FileSize.Format(MailSenderDefaults.MaxEmailBytesAfterBase64)}");
        }

        if (errors.Count > 0) return new MailComposeResult(null, errors, encodedBytes);

        var request = new ResendEmailRequest
        {
            From = MailAddressLines.BuildFrom(config.FromName, fromAddress),
            To = to.Addresses.ToList(),
            Subject = subject,
            Html = bodyHtml,
            Text = config.IncludePlainText ? NullIfEmpty(MailHtml.ToPlainText(bodyHtml)) : null,
            Cc = cc.Addresses.Count > 0 ? cc.Addresses.ToList() : null,
            Bcc = bcc.Addresses.Count > 0 ? bcc.Addresses.ToList() : null,
            ReplyTo = replyTo.Addresses.Count > 0 ? replyTo.Addresses.ToList() : null,
            Attachments = attachList.Count > 0 ? attachList : null,
        };

        return new MailComposeResult(request, errors, encodedBytes);
    }

    /// <summary>
    /// 把 HTML 里的本机图片地址换成 <c>cid:…</c>（行内图片的解析器）。认不出的地址原样返回，
    /// 这样远程图片 / data: 地址不会被误改。
    /// </summary>
    public static Func<string, string> BuildCidResolver(IEnumerable<InlineImage> inlineImages)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in inlineImages)
        {
            if (!string.IsNullOrWhiteSpace(image.FilePath)) map[image.FilePath] = image.ContentId;
        }
        return src => map.TryGetValue(src, out var cid) ? "cid:" + cid : src;
    }

    /// <summary>
    /// 预览用的解析器：本机图片转 <c>data:</c> 内联（WebView2 拿不到本机路径，也不该给它文件访问权限）。
    /// </summary>
    public static Func<string, string> BuildPreviewResolver()
        => src => src.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? src
            : Views.RichTextHtml.ToDataUri(src);

    /// <summary>给行内图片分配 <c>content_id</c>（形如 <c>inline-1@yuliang</c>，远短于 128 字符上限）。</summary>
    public static string NextContentId(int index) => $"inline-{index}@yuliang";

    private static void AddInvalidHint(List<string> errors, string label, AddressParseResult parsed)
    {
        if (parsed.Invalid.Count == 0) return;
        errors.Add($"{label}里有 {parsed.Invalid.Count} 个地址不合法：{string.Join("、", parsed.Invalid)}");
    }

    private static string? NullIfEmpty(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static byte[]? ReadAllBytesOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
