using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

namespace YuLiangToolkit.Tools.Mail;

/// <summary>收件列表里的一封邮件（只含列表需要的轻量信息，正文 / 附件在选中时才拉）。</summary>
/// <param name="Account">来自哪个邮箱账号（界面显示账号标签、回连拉详情都用它）。</param>
/// <param name="Folder">来自哪个系统文件夹（收件箱 / 已发送 / 垃圾箱）。</param>
/// <param name="UniqueId">IMAP UID（同一账号内唯一，重新连接后还能定位到同一封）。</param>
/// <param name="Subject">主题。</param>
/// <param name="FromText">发件人显示文本。</param>
/// <param name="ToText">收件人显示文本。</param>
/// <param name="Date">发送时间（取不到时用服务器内部时间；都取不到为 <see cref="DateTimeOffset.MinValue"/>）。</param>
/// <param name="IsUnread">是否未读（服务器上的 SEEN 标记）。</param>
/// <param name="SizeBytes">服务器给的邮件大小（字节，0 = 服务器没给；列表排序用）。</param>
/// <param name="HasAttachments">摘要里的 MIME 结构是否含附件。</param>
/// <param name="Priority">优先级（从常见邮件头解析）。</param>
public sealed record MailBrief(
    MailAccountConfig Account,
    MailFolderKind Folder,
    uint UniqueId,
    string Subject,
    string FromText,
    string ToText,
    DateTimeOffset Date,
    bool IsUnread,
    long SizeBytes,
    bool HasAttachments,
    MailPriorityLevel Priority);

/// <summary>一个系统文件夹的读取结果；文件夹不存在或读取失败时不影响其它文件夹。</summary>
public sealed record MailFolderFetchResult(
    MailFolderKind Folder,
    bool Available,
    string ErrorText,
    IReadOnlyList<MailBrief> Messages);

/// <summary>一个账号一次连接后读取三个系统文件夹的结果。</summary>
public sealed record MailAccountFetchResult(IReadOnlyList<MailFolderFetchResult> Folders);

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

    /// <summary>
    /// 可直接交给 WebView2 渲染的完整 HTML 文档（已内联 cid 图片、已加安全 CSP / 基础样式）。
    /// null = 这封邮件没有 HTML 正文，界面用 <see cref="BodyText"/> 纯文本显示。
    /// </summary>
    public string? HtmlDocument { get; init; }

    public IReadOnlyList<MailAttachment> Attachments { get; init; } = Array.Empty<MailAttachment>();
}

/// <summary>
/// IMAP 只读代收服务：连接 → 认证 → 依次打开收件箱 / 已发送 / 垃圾箱
/// （<see cref="FolderAccess.ReadOnly"/>）→ 拉最近 N 封的摘要 / 单封完整内容 → 断开。
///
/// <para><b>只读保证</b>：三个系统文件夹都按只读方式打开，<c>GetMessage</c> 拉取时服务器不会把邮件标成已读；
/// 本服务也没有删除 / 移动 / 标记命令，不会改动用户邮箱里的任何状态。</para>
///
/// <para><b>线程</b>：方法都是 async，由 UI 线程 await（MailKit 的网络 IO 不会阻塞 UI）；
/// 每个方法内部各开一条连接，互不共享状态，失败只影响当前账号。</para>
/// </summary>
public sealed class ImapMailService
{
    /// <summary>连接 / 读超时（毫秒）：Gmail / QQ 一般 1~3 秒内完成；超过这个时间按网络问题提示。</summary>
    public const int TimeoutMs = 20_000;

    private readonly MailProxySettings _proxy;

    /// <summary>
    /// 创建服务。<paramref name="proxy"/> 为 null 时按直连（单元测试 / 特殊场景用）；
    /// 工具界面里总是传 <c>Cfg.Proxy</c>（默认跟随 Windows 系统代理）。
    /// </summary>
    public ImapMailService(MailProxySettings? proxy = null)
    {
        _proxy = proxy ?? new MailProxySettings { Mode = "direct" };
    }

    /// <summary>
    /// 拉取某个账号收件箱 / 已发送 / 垃圾箱最近 <paramref name="maxCount"/> 封的摘要（不取正文）。
    /// 某个文件夹未声明特殊用途时会尝试常见中英文名称；仍找不到就标记为不可用。
    /// </summary>
    public async Task<MailAccountFetchResult> FetchRecentAsync(
        MailAccountConfig account,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(maxCount, 1, 500);
        using var client = await ConnectAsync(account, cancellationToken);
        try
        {
            var results = new List<MailFolderFetchResult>(MailFolders.All.Length);
            foreach (var folderKind in MailFolders.All)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IMailFolder? folder = null;
                try
                {
                    folder = await ResolveFolderAsync(client, folderKind, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    results.Add(new MailFolderFetchResult(
                        folderKind, false, ImapMailService.DescribeError(ex), Array.Empty<MailBrief>()));
                    continue;
                }

                results.Add(await FetchFolderAsync(account, folderKind, folder, limit, cancellationToken));
            }

            return new MailAccountFetchResult(results);
        }
        finally
        {
            await SafeDisconnectAsync(client);
        }
    }

    /// <summary>从指定系统文件夹拉取一封邮件的完整内容（正文 + 附件），只读，不改变已读状态。</summary>
    public async Task<MailDetail> GetMessageAsync(
        MailAccountConfig account,
        MailFolderKind folderKind,
        uint uniqueId,
        CancellationToken cancellationToken = default)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        try
        {
            var folder = await ResolveFolderAsync(client, folderKind, cancellationToken)
                         ?? throw new InvalidOperationException($"服务器未提供「{MailFolders.Label(folderKind)}」文件夹");
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var message = await folder.GetMessageAsync(new UniqueId(uniqueId), cancellationToken);

            var textBody = TryGetTextBody(message);
            var htmlBody = TryGetHtmlBody(message);
            var convertedFromHtml = false;
            if (string.IsNullOrWhiteSpace(textBody) && !string.IsNullOrWhiteSpace(htmlBody))
            {
                textBody = MailText.HtmlToPlainText(htmlBody);
                convertedFromHtml = true;
            }

            if (string.IsNullOrWhiteSpace(textBody)) textBody = "（这封邮件没有可显示的正文）";

            // HTML 邮件：把 cid: 内嵌图片换成 data URI，再包成带 CSP / 字体 / 图片宽度限制的完整文档，
            // 交给 WebView2 渲染（Foxmail 那种效果）。失败就退化为纯文本，不影响阅读。
            string? htmlDocument = null;
            if (!string.IsNullOrWhiteSpace(htmlBody))
            {
                try { htmlDocument = MailHtml.BuildDocument(InlineCidImages(message, htmlBody)); }
                catch { htmlDocument = null; }
            }

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
                HtmlDocument = htmlDocument,
                Attachments = attachments,
            };
        }
        finally
        {
            await SafeDisconnectAsync(client);
        }
    }

    /// <summary>测试连接：连上 / 认证 / 读取三个系统文件夹后返回一句话结果。</summary>
    public async Task<string> TestConnectionAsync(MailAccountConfig account, CancellationToken cancellationToken = default)
    {
        using var client = await ConnectAsync(account, cancellationToken);
        try
        {
            var parts = new List<string>();
            foreach (var folderKind in MailFolders.All)
            {
                var folder = await ResolveFolderAsync(client, folderKind, cancellationToken);
                if (folder is null)
                {
                    parts.Add($"{MailFolders.Label(folderKind)}未提供");
                    continue;
                }

                try
                {
                    await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                    parts.Add($"{MailFolders.Label(folderKind)} {folder.Count} 封");
                }
                finally
                {
                    await TryCloseFolderAsync(folder);
                }
            }

            return $"✅ 连接成功：{string.Join(" / ", parts)}（只读）";
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
        // 代理连接失败的服务层包装（信息量最大，优先返回，别被 inner 的 TimeoutException 覆盖成泛泛的“连接超时”）
        if (exception is IOException && exception.Message.StartsWith("代理连接失败", StringComparison.Ordinal))
            return exception.Message;

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

    private static async Task<MailFolderFetchResult> FetchFolderAsync(
        MailAccountConfig account,
        MailFolderKind folderKind,
        IMailFolder? folder,
        int limit,
        CancellationToken cancellationToken)
    {
        if (folder is null)
        {
            return new MailFolderFetchResult(
                folderKind, false, "服务器未提供该文件夹", Array.Empty<MailBrief>());
        }

        try
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
            if (folder.Count == 0)
                return new MailFolderFetchResult(folderKind, true, "", Array.Empty<MailBrief>());

            var start = Math.Max(0, folder.Count - limit);
            var summaries = await folder.FetchAsync(
                start,
                folder.Count - 1,
                MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope
                | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate
                | MessageSummaryItems.Size | MessageSummaryItems.BodyStructure
                | MessageSummaryItems.Headers,
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
                var to = MailText.FormatAddressList(summary.Envelope?.To, "（未填写）");
                var subject = string.IsNullOrWhiteSpace(summary.Envelope?.Subject)
                    ? "（无主题）"
                    : summary.Envelope!.Subject!;
                var unread = summary.Flags is { } flags && !flags.HasFlag(MessageFlags.Seen);
                var hasAttachments = SummaryHasAttachments(summary);
                var priority = MailText.ParsePriority(
                    TryGetHeader(summary, HeaderId.Priority),
                    TryGetHeader(summary, HeaderId.XPriority),
                    TryGetHeader(summary, HeaderId.Importance));

                result.Add(new MailBrief(
                    account, folderKind, uid, subject, from, to, date, unread,
                    summary.Size ?? 0, hasAttachments, priority));
            }

            return new MailFolderFetchResult(folderKind, true, "", result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MailFolderFetchResult(
                folderKind, false, DescribeError(ex), Array.Empty<MailBrief>());
        }
        finally
        {
            await TryCloseFolderAsync(folder);
        }
    }

    /// <summary>
    /// 优先使用 IMAP SPECIAL-USE；服务器没有声明时按常见中英文文件夹名兜底。
    /// 找不到返回 null，不把「没有垃圾箱」当成整个账号连接失败。
    /// </summary>
    private static async Task<IMailFolder?> ResolveFolderAsync(
        ImapClient client,
        MailFolderKind folderKind,
        CancellationToken cancellationToken)
    {
        if (folderKind == MailFolderKind.Inbox) return client.Inbox;

        var special = folderKind == MailFolderKind.Sent ? SpecialFolder.Sent : SpecialFolder.Trash;
        try
        {
            var folder = client.GetFolder(special);
            if (folder is not null) return folder;
        }
        catch
        {
            // 老服务器可能不认识 SPECIAL-USE，继续按名称查找。
        }

        var candidates = folderKind == MailFolderKind.Sent
            ? new[] { "Sent", "Sent Items", "Sent Messages", "Sent Mail", "已发送", "已发送邮件", "发件箱" }
            : new[] { "Trash", "Deleted Items", "Deleted Messages", "已删除", "已删除邮件", "垃圾箱" };

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await client.GetFolderAsync(candidate, cancellationToken);
            }
            catch (FolderNotFoundException)
            {
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 单个候选名不存在 / 服务器拒绝时继续找下一个。
            }
        }

        foreach (var ns in client.PersonalNamespaces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var folders = await client.GetFoldersAsync(ns, StatusItems.None, false, cancellationToken);
                var match = folders.FirstOrDefault(folder =>
                    candidates.Any(candidate =>
                        string.Equals(folder.Name, candidate, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(folder.FullName, candidate, StringComparison.OrdinalIgnoreCase)
                        || folder.FullName.EndsWith("/" + candidate, StringComparison.OrdinalIgnoreCase)));
                if (match is not null) return match;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 某些服务器不允许 LIST 整个命名空间；沿用前面的 SPECIAL-USE / 路径探测结果。
            }
        }

        return null;
    }

    private static string? TryGetHeader(IMessageSummary summary, HeaderId headerId)
    {
        try
        {
            return summary.Headers?[headerId];
        }
        catch
        {
            return null;
        }
    }

    private static bool SummaryHasAttachments(IMessageSummary summary)
    {
        try
        {
            // Attachments 是遍历 BodyStructure 的惰性集合；服务器没给 BodyStructure 时返回空。
            return summary.Attachments?.Any() == true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task TryCloseFolderAsync(IMailFolder folder)
    {
        try
        {
            if (folder.IsOpen) await folder.CloseAsync(expunge: false, CancellationToken.None);
        }
        catch
        {
            // 文件夹关闭失败不覆盖已经拿到的数据
        }
    }

    private async Task<ImapClient> ConnectAsync(MailAccountConfig account, CancellationToken cancellationToken)
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

        // 代理：默认跟随 Windows 系统代理，并遵守 ProxyOverride 例外列表（QQ 通常直连，Gmail 走代理）。
        var route = _proxy.Resolve(account.Host);
        var client = new ImapClient { Timeout = TimeoutMs, ProxyClient = route.Client };
        var options = account.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

        try
        {
            await client.ConnectAsync(account.Host.Trim(), account.Port, options, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            client.Dispose();
            throw;
        }
        catch (Exception ex) when (route.IsProxy)
        {
            client.Dispose();
            throw new IOException(
                $"代理连接失败（{route.Description}）：{ex.Message}。"
                + "请确认代理软件正在运行、端口 / 类型填对；也可以把「网络代理」改成「直连」再试。", ex);
        }

        // QQ / 163 等国内邮箱有些要求先发 IMAP ID（RFC 2971）才允许登录；只发送客户端名称 / 版本，
        // 不包含任何用户信息。服务器不声明 ID 能力就跳过；声明了但拒绝也不影响后面登录。
        if (client.Capabilities.HasFlag(ImapCapabilities.Id))
        {
            try
            {
                await client.IdentifyAsync(
                    new ImapImplementation { Name = "YuLiangToolkit", Version = "1.8.0" },
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
        if (part.ContentDisposition?.Disposition == ContentDisposition.Attachment) return true;
        // 有 Content-ID 的是正文内嵌资源（cid: 图片 / 样式），不算附件；正文里会内联成 data URI
        if (!string.IsNullOrEmpty(part.ContentId)) return false;
        return !string.IsNullOrEmpty(part.FileName);
    }

    /// <summary>
    /// 把 HTML 正文里的 <c>cid:xxx</c> 内嵌图片替换成 data URI（WebView2 离线渲染用）。
    /// 单个邮件最多内联 25 MB，防止超大邮件把内存吃满；解不出来的图片保留原样（显示为坏图，不影响其它内容）。
    /// </summary>
    private static string InlineCidImages(MimeMessage message, string html)
    {
        const long maxTotalBytes = 25L * 1024 * 1024;
        long totalBytes = 0;

        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            var contentId = (part.ContentId ?? "").Trim().Trim('<', '>');
            if (contentId.Length == 0) continue;
            if (part.ContentDisposition?.Disposition == ContentDisposition.Attachment) continue;

            try
            {
                using var stream = new MemoryStream();
                part.Content?.DecodeTo(stream);
                if (stream.Length == 0) continue;
                totalBytes += stream.Length;
                if (totalBytes > maxTotalBytes) break;

                var mimeType = part.ContentType?.MimeType ?? "application/octet-stream";
                var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(stream.ToArray())}";

                foreach (var token in new[] { "cid:" + contentId, "cid:" + Uri.EscapeDataString(contentId) })
                {
                    if (html.Contains(token, StringComparison.OrdinalIgnoreCase))
                        html = html.Replace(token, dataUri, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // 单个内嵌资源解码失败不影响正文
            }
        }

        return html;
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
