using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>收件列表里的一封邮件（只含列表需要的轻量信息，正文 / 附件在选中时才拉）。</summary>
/// <param name="Account">来自哪个邮箱账号（界面显示账号标签、回连拉详情都用它）。</param>
/// <param name="UniqueId">IMAP UID（同一账号内唯一，重新连接后还能定位到同一封）。</param>
/// <param name="Subject">主题。</param>
/// <param name="FromText">发件人显示文本。</param>
/// <param name="Date">发送时间（取不到时用服务器内部时间；都取不到为 <see cref="DateTimeOffset.MinValue"/>）。</param>
/// <param name="IsUnread">是否未读（服务器上的 SEEN 标记）。</param>
public sealed record MailBrief(
    MailAccountConfig Account,
    uint UniqueId,
    string Subject,
    string FromText,
    DateTimeOffset Date,
    bool IsUnread);

/// <summary>邮件里的一个附件：显示名 + 大小 + 原始 MIME 部分（保存时直接解码，不占额外内存）。</summary>
public sealed class MailAttachment
{
    public MailAttachment(string fileName, long sizeBytes, MimePart part)
    {
        FileName = fileName;
        SizeBytes = sizeBytes;
        Part = part;
    }

    /// <summary>清洗后的文件名（用于「另存为」对话框默认值）。</summary>
    public string FileName { get; }

    /// <summary>解码后的大致字节数（0 = 服务器没给）。</summary>
    public long SizeBytes { get; }

    /// <summary>原始 MIME 部分（保存 / 预览时用 <c>Part.Content.DecodeTo(stream)</c>）。</summary>
    public MimePart Part { get; }

    /// <summary>人类可读大小。</summary>
    public string SizeText => MailText.FormatSize(SizeBytes);
}

/// <summary>选中一封邮件后拉到的完整内容（纯文本正文 + 附件列表）。</summary>
public sealed class MailDetail
{
    public string Subject { get; init; } = "";

    public string FromText { get; init; } = "";

    public string ToText { get; init; } = "";

    public string DateText { get; init; } = "";

    /// <summary>展示用正文：优先纯文本；只有 HTML 时转成纯文本。</summary>
    public string BodyText { get; init; } = "";

    /// <summary>正文是不是从 HTML 转过来的（界面可以提示一句）。</summary>
    public bool ConvertedFromHtml { get; init; }

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = Array.Empty<MailAttachment>();
}

/// <summary>
/// IMAP 只读代收服务：连接 → 认证 → 打开收件箱（<see cref="FolderAccess.ReadOnly"/>）→
/// 拉最近 N 封的摘要 / 单封完整内容 → 断开。
///
/// <para><b>只读保证</b>：收件箱按只读方式打开，<c>GetMessage</c> 拉取时服务器不会把邮件标成已读；
/// 本服务也没有删除 / 移动 / 标记命令，不会改动用户邮箱里的任何状态。</para>
///
/// <para><b>线程</b>：方法都是 async，由 UI 线程 await（MailKit 的网络 IO 不会阻塞 UI）；
/// 每个方法内部各开一条连接，互不共享状态，失败只影响当前账号。</para>
/// </summary>
public sealed class ImapMailService
{
    /// <summary>连接 / 读超时（毫秒）：Gmail / QQ 一般 1~3 秒内完成；超过这个时间按网络问题提示。</summary>
    public const int TimeoutMs = 20_000;

    /// <summary>拉取某个账号收件箱最近 <paramref name="maxCount"/> 封的列表（只取摘要，不取正文）。</summary>
    public async Task<IReadOnlyList<MailBrief>> FetchRecentAsync(
        MailAccountConfig account,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(maxCount, 1, 500);
        using var client = await ConnectAsync(account, cancellationToken);
        try
        {
            var inbox = client.Inbox ?? throw new InvalidOperationException("这个账号没有 INBOX 收件箱");
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            if (inbox.Count == 0) return Array.Empty<MailBrief>();

            var start = Math.Max(0, inbox.Count - limit);
            var summaries = await inbox.FetchAsync(
                start,
                inbox.Count - 1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate,
                cancellationToken);

            var result = new List<MailBrief>(summaries.Count);
            foreach (var summary in summaries)
            {
                var uid = summary.UniqueId.Id;
                if (uid == 0) continue;

                var date = summary.Envelope?.Date
                           ?? summary.InternalDate
                           ?? DateTimeOffset.MinValue;
                var from = MailText.FormatFirstAddress(summary.Envelope?.From);
                var subject = string.IsNullOrWhiteSpace(summary.Envelope?.Subject)
                    ? "（无主题）"
                    : summary.Envelope!.Subject!;
                var unread = summary.Flags is { } flags && !flags.HasFlag(MessageFlags.Seen);

                result.Add(new MailBrief(account, uid, subject, from, date, unread));
            }

            return result;
        }
        finally
        {
            await SafeDisconnectAsync(client);
        }
    }

    /// <summary>拉取一封邮件的完整内容（正文 + 附件），只读，不改变已读状态。</summary>
    public async Task<MailDetail> GetMessageAsync(
        MailAccountConfig account,
        uint uniqueId,
        CancellationToken cancellationToken = default)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        try
        {
            var inbox = client.Inbox ?? throw new InvalidOperationException("这个账号没有 INBOX 收件箱");
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var message = await inbox.GetMessageAsync(new UniqueId(uniqueId), cancellationToken);

            var textBody = TryGetTextBody(message);
            var htmlBody = TryGetHtmlBody(message);
            var convertedFromHtml = false;
            if (string.IsNullOrWhiteSpace(textBody) && !string.IsNullOrWhiteSpace(htmlBody))
            {
                textBody = MailText.HtmlToPlainText(htmlBody);
                convertedFromHtml = true;
            }

            if (string.IsNullOrWhiteSpace(textBody)) textBody = "（这封邮件没有可显示的正文）";

            var attachments = new List<MailAttachment>();
            foreach (var part in message.BodyParts.OfType<MimePart>())
            {
                if (!IsAttachment(part)) continue;
                var name = MailText.SanitizeFileName(part.FileName, $"附件-{attachments.Count + 1}");
                var size = TryGetAttachmentSize(part);
                attachments.Add(new MailAttachment(name, size, part));
            }

            return new MailDetail
            {
                Subject = string.IsNullOrWhiteSpace(message.Subject) ? "（无主题）" : message.Subject,
                FromText = MailText.FormatAddressList(message.From, "（未知发件人）"),
                ToText = MailText.FormatAddressList(message.To, "（未填写）"),
                DateText = MailText.FormatFullDate(message.Date),
                BodyText = textBody,
                ConvertedFromHtml = convertedFromHtml,
                Attachments = attachments,
            };
        }
        finally
        {
            await SafeDisconnectAsync(client);
        }
    }

    /// <summary>测试连接：连上 / 认证 / 打开收件箱后返回一句话结果（失败抛异常，由调用方转成错误提示）。</summary>
    public async Task<string> TestConnectionAsync(MailAccountConfig account, CancellationToken cancellationToken = default)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        try
        {
            var inbox = client.Inbox ?? throw new InvalidOperationException("这个账号没有 INBOX 收件箱");
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            return $"✅ 连接成功：{inbox.FullName} 共 {inbox.Count} 封（只读）";
        }
        finally
        {
            await SafeDisconnectAsync(client);
        }
    }

    /// <summary>把附件写进指定路径（用户已经在「另存为」里确认过路径）。</summary>
    public static void SaveAttachment(MailAttachment attachment, string destinationPath)
    {
        using var stream = File.Create(destinationPath);
        attachment.Part.Content?.DecodeTo(stream);
    }

    /// <summary>把异常翻译成给用户看的中文提示（认证 / SSL / 网络 / 服务器拒绝分开说）。</summary>
    public static string DescribeError(Exception exception)
    {
        var chain = new List<Exception>();
        for (var current = exception; current is not null; current = current.InnerException) chain.Add(current);

        if (chain.Any(e => e is OperationCanceledException)) return "已取消";
        if (chain.Any(e => e is TimeoutException)) return "连接超时：检查网络 / 服务器 / 端口，或稍后再试";

        if (chain.Any(e => e.GetType().Name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)))
        {
            return "认证失败：邮箱地址或授权码 / 应用专用密码不对"
                   + "（QQ 要填「授权码」、Gmail 要填「应用专用密码」，都不是登录密码）";
        }

        if (chain.Any(e => e.GetType().Name.Contains("SslHandshake", StringComparison.OrdinalIgnoreCase)))
            return "SSL 握手失败：检查「使用 SSL」选项和端口（一般是 993）";

        if (chain.Any(e => e is System.Net.Sockets.SocketException))
            return "网络连接失败：检查网络、服务器地址和端口";

        if (chain.Any(e => e.GetType().Name.Contains("ImapCommand", StringComparison.OrdinalIgnoreCase)))
            return "IMAP 服务器拒绝了请求：" + exception.Message;

        if (chain.Any(e => e.GetType().Name.Contains("ImapProtocol", StringComparison.OrdinalIgnoreCase)))
            return "IMAP 协议错误：" + exception.Message;

        return exception.Message;
    }

    // ---------- 内部：连接 / 断开 ----------

    private static async Task<ImapClient> ConnectAsync(MailAccountConfig account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account.Host))
            throw new InvalidOperationException("IMAP 服务器地址为空：先选一个预设，或手动填服务器");
        if (account.Port is <= 0 or > 65535)
            throw new InvalidOperationException("IMAP 端口不合法（应为 1 ~ 65535，SSL 一般是 993）");
        if (string.IsNullOrWhiteSpace(account.Email))
            throw new InvalidOperationException("邮箱地址为空");
        var password = account.Password.Trim();
        if (password.Length == 0)
        {
            throw new InvalidOperationException(
                "授权码 / 应用专用密码为空：QQ 邮箱填「授权码」，Gmail 填「应用专用密码」");
        }

        var client = new ImapClient { Timeout = TimeoutMs };
        var options = account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
        await client.ConnectAsync(account.Host.Trim(), account.Port, options, cancellationToken);

        // QQ / 163 等国内邮箱有些要求先发 IMAP ID（RFC 2971）才允许登录；只发送客户端名称 / 版本，
        // 不包含任何用户信息。服务器不声明 ID 能力就跳过；声明了但拒绝也不影响后面登录。
        if (client.Capabilities.HasFlag(ImapCapabilities.Id))
        {
            try
            {
                await client.IdentifyAsync(
                    new ImapImplementation { Name = "YuLiangToolkit", Version = "1.2.0" },
                    cancellationToken);
            }
            catch
            {
                // 兼容性尝试，失败继续走普通登录
            }
        }

        await client.AuthenticateAsync(account.Email.Trim(), password, cancellationToken);
        return client;
    }

    private static async Task SafeDisconnectAsync(ImapClient client)
    {
        try
        {
            if (client.IsConnected) await client.DisconnectAsync(quit: true, CancellationToken.None);
        }
        catch
        {
            // 断开失败不影响已经拿到的数据
        }
    }

    private static bool IsAttachment(MimePart part)
    {
        if (!string.IsNullOrEmpty(part.FileName)) return true;
        return part.ContentDisposition?.Disposition == ContentDisposition.Attachment;
    }

    /// <summary>附件大小（取编码后的原始流长度，解不出来时返回 -1，界面显示「—」）。</summary>
    private static long TryGetAttachmentSize(MimePart part)
    {
        try
        {
            var stream = part.Content?.Stream;
            return stream is { CanSeek: true } ? stream.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static string? TryGetTextBody(MimeMessage message)
    {
        try { return message.TextBody; }
        catch { return null; }
    }

    private static string? TryGetHtmlBody(MimeMessage message)
    {
        try { return message.HtmlBody; }
        catch { return null; }
    }
}
