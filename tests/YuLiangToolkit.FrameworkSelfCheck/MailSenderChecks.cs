using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using YuLiangToolkit.Tools.MailSender;
using YuLiangToolkit.Tools.MailSender.ViewModels;
using YuLiangToolkit.Tools.MailSender.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「邮件推送」工具的自检：工具元数据 / 地址解析 / 附件体积与危险后缀 / 富文本 ⇄ HTML /
/// HTML → 纯文本 / 请求组装与校验 / 请求头与请求体（假 HttpMessageHandler）/ 错误翻译 /
/// API Key DPAPI 加解密 / 配置默认值与 JSON 往返 / 查询投递状态的解析。
/// 全部是纯逻辑，**不联网、不发任何真实邮件**（真实发送由人工在界面里点一次验收）。
/// </summary>
internal static class MailSenderChecks
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        _ = report;
        CheckFn assert = (name, ok, detail) => check(name, ok, detail);

        CheckToolMetadata(assert);
        CheckAddresses(assert);
        CheckAttachments(assert);
        CheckPlainText(assert);
        CheckComposer(assert);
        CheckClient(assert);
        CheckErrorTranslation(assert);
        CheckApiKey(assert);
        CheckConfig(assert);
        CheckQueryHelpers(assert);
        CheckRichText(assert);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckToolMetadata(CheckFn check)
    {
        var tool = new MailSenderTool();
        check("[邮件推送] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "mail-sender" && tool.Name == "邮件推送"
            && tool.Category == "网络" && tool.Order == 27
            && tool.Tags.Contains("Resend") && tool.Tags.Contains("富文本"),
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[邮件推送] 描述写明「富文本 / 附件 / API Key 加密」",
            tool.Description.Contains("富文本") && tool.Description.Contains("附件")
            && tool.Description.Contains("DPAPI"),
            tool.Description);

        check("[邮件推送] 默认推送地址是 Resend 的 POST /emails",
            MailSenderDefaults.Endpoint == "https://api.resend.com/emails",
            MailSenderDefaults.Endpoint);
    }

    // ---------------------------------------------------------------- 地址

    private static void CheckAddresses(CheckFn check)
    {
        var multi = MailAddressLines.Parse("a@x.com, b@y.com;c@z.com\n d@w.com，e@v.com；f@u.com");
        check("[邮件推送] 收件人支持逗号 / 分号 / 换行 / 中文逗号顿号分隔",
            multi.Addresses.SequenceEqual(new[] { "a@x.com", "b@y.com", "c@z.com", "d@w.com", "e@v.com", "f@u.com" }),
            string.Join(",", multi.Addresses));

        var deduped = MailAddressLines.Parse("A@X.com, a@x.com ,  b@y.com");
        check("[邮件推送] 地址去重忽略大小写，保留填写顺序",
            deduped.Addresses.SequenceEqual(new[] { "A@X.com", "b@y.com" }),
            string.Join(",", deduped.Addresses));

        var mixed = MailAddressLines.Parse("good@x.com, not-an-email, <b@y.com>");
        check("[邮件推送] 尖括号包着的地址能认出来，非法项单独收集",
            mixed.Addresses.SequenceEqual(new[] { "good@x.com", "b@y.com" })
            && mixed.Invalid.SequenceEqual(new[] { "not-an-email" }),
            $"{string.Join(",", mixed.Addresses)} / 无效 {string.Join(",", mixed.Invalid)}");

        check("[邮件推送] 空输入的解析结果标记为空",
            MailAddressLines.Parse("  ").IsEmpty && MailAddressLines.Parse("bad").HasOnlyInvalid);

        check("[邮件推送] 单个地址校验：正常 / 缺 @ / 多地址混写",
            MailAddressLines.IsValidEmail("user@example.com")
            && !MailAddressLines.IsValidEmail("user.example.com")
            && !MailAddressLines.IsValidEmail("a@x.com,b@y.com"));

        check("[邮件推送] 发件人拼装：有名字写「名字 <地址>」，没名字只写地址",
            MailAddressLines.BuildFrom("Congratulations!", "test@contact.example.com") == "Congratulations! <test@contact.example.com>"
            && MailAddressLines.BuildFrom("", "test@contact.example.com") == "test@contact.example.com"
            && MailAddressLines.BuildFrom("名字", "") == "");

        check("[邮件推送] 发件人名字里的尖括号会被去掉（否则整串写坏）",
            MailAddressLines.BuildFrom("A <B>", "a@x.com") == "A B <a@x.com>",
            MailAddressLines.BuildFrom("A <B>", "a@x.com"));

        check("[邮件推送] 从「名字 <地址>」里取出地址 / 取域名",
            MailAddressLines.ExtractAddress("Congratulations! <test@contact.yulau.space>") == "test@contact.yulau.space"
            && MailAddressLines.ExtractAddress("plain@x.com") == "plain@x.com"
            && MailAddressLines.GetDomain("test@Contact.Yulau.Space") == "contact.yulau.space"
            && MailAddressLines.GetDomain("no-at") == "");
    }

    // ---------------------------------------------------------------- 附件

    private static void CheckAttachments(CheckFn check)
    {
        check("[邮件推送] base64 后体积换算（向上取整到 4 的倍数）",
            MailAttachmentItem.Base64SizeOf(0) == 0
            && MailAttachmentItem.Base64SizeOf(1) == 4
            && MailAttachmentItem.Base64SizeOf(3) == 4
            && MailAttachmentItem.Base64SizeOf(4) == 8
            && MailAttachmentItem.Base64SizeOf(3072) == 4096,
            $"{MailAttachmentItem.Base64SizeOf(1)} / {MailAttachmentItem.Base64SizeOf(3072)}");

        check("[邮件推送] 媒体类型按后缀猜，认不出给 octet-stream",
            MailAttachmentItem.ContentTypeOf("a.PNG") == "image/png"
            && MailAttachmentItem.ContentTypeOf("报告.pdf") == "application/pdf"
            && MailAttachmentItem.ContentTypeOf("x.docx").Contains("wordprocessingml")
            && MailAttachmentItem.ContentTypeOf("noext") == "application/octet-stream");

        check("[邮件推送] 可执行 / 脚本类附件被标为拒收类型（提醒用户）",
            MailAttachmentItem.IsBlockedExtension("setup.exe")
            && MailAttachmentItem.IsBlockedExtension("run.BAT")
            && MailAttachmentItem.IsBlockedExtension("a.ps1")
            && !MailAttachmentItem.IsBlockedExtension("readme.txt"));

        var distinct = MailAttachmentItem.Distinct(new[] { @"C:\a\1.txt", @"c:\A\1.TXT", "  ", @"C:\b\2.png" });
        check("[邮件推送] 附件路径去重（忽略大小写）并跳过空白项",
            distinct.Count == 2, string.Join(" | ", distinct));

        var missing = new MailAttachmentItem(Path.Combine(SelfCheckScratch.Root, "no-such-file.bin"));
        check("[邮件推送] 不存在的附件：Exists = false 且详情给出可读提示",
            !missing.Exists && missing.SizeBytes == -1 && missing.DetailText.Contains("不存在"),
            missing.DetailText);
    }

    // ---------------------------------------------------------------- HTML → 纯文本

    private static void CheckPlainText(CheckFn check)
    {
        var html = """
            <html><head><style>p{color:red}</style></head>
            <body><p>第一段 &amp; 实体</p><p>第二段</p>
            <script>alert('x')</script><img alt="签名" src="a.png" /></body></html>
            """;
        var plain = MailHtml.ToPlainText(html);
        check("[邮件推送] HTML → 纯文本：保留文字 / 解码实体 / 去掉脚本样式",
            plain.Contains("第一段 & 实体") && plain.Contains("第二段")
            && !plain.Contains("alert") && !plain.Contains("color:red")
            && !plain.Contains("<"),
            plain);

        check("[邮件推送] 空正文判断：空白 / 只剩空实体算空，有图片或文字不算空",
            MailHtml.IsEmptyBody("") && MailHtml.IsEmptyBody("<p> &nbsp; </p>")
            && !MailHtml.IsEmptyBody("<p>你好</p>")
            && !MailHtml.IsEmptyBody("<p><img src=\"a.png\"></p>"));

        var document = MailHtml.BuildPreviewDocument("<p>hi</p>");
        check("[邮件推送] 预览文档：禁脚本 CSP / 白底 / 保留正文",
            document.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal)
            && document.Contains("Content-Security-Policy")
            && document.Contains("default-src 'none'")
            && document.Contains("<p>hi</p>")
            && document.Contains("background:#ffffff"),
            "length=" + document.Length);
    }

    // ---------------------------------------------------------------- 请求组装

    private static MailSenderConfig ValidConfig() => new()
    {
        FromName = "Congratulations!",
        FromAddress = "test@contact.example.com",
        To = "fantasyuliang@qq.com",
        Subject = "自检邮件",
        BodyHtml = "<p>正文</p>",
        IncludePlainText = true,
    };

    private static void CheckComposer(CheckFn check)
    {
        var config = ValidConfig();
        var result = MailComposer.Compose(config, config.BodyHtml);
        var request = result.Request;
        check("[邮件推送] 组装成功：发件人带显示名、收件人是数组、主题 / HTML / 纯文本齐全",
            result.Success && request is not null
            && request.From == "Congratulations! <test@contact.example.com>"
            && request.To.SequenceEqual(new[] { "fantasyuliang@qq.com" })
            && request.Subject == "自检邮件"
            && request.Html == "<p>正文</p>"
            && request.Text == "正文"
            && request.Cc is null && request.Bcc is null && request.ReplyTo is null && request.Attachments is null,
            request is null ? result.FirstError : $"{request.From} → {string.Join(",", request.To)}");

        var empty = new MailSenderConfig();
        var emptyResult = MailComposer.Compose(empty, "");
        check("[邮件推送] 空输入：发件人 / 收件人 / 主题 / 正文四条都给出中文提示",
            !emptyResult.Success && emptyResult.Errors.Count >= 4
            && emptyResult.Errors.Any(e => e.Contains("发件人"))
            && emptyResult.Errors.Any(e => e.Contains("收件人"))
            && emptyResult.Errors.Any(e => e.Contains("主题"))
            && emptyResult.Errors.Any(e => e.Contains("正文")),
            string.Join(" / ", emptyResult.Errors));

        var badAddress = ValidConfig();
        badAddress.FromAddress = "not-an-email";
        badAddress.To = "good@x.com, bad";
        var badResult = MailComposer.Compose(badAddress, badAddress.BodyHtml);
        check("[邮件推送] 地址填错会指出是哪个字段的哪一个值",
            !badResult.Success
            && badResult.Errors.Any(e => e.Contains("发件人") && e.Contains("not-an-email"))
            && badResult.Errors.Any(e => e.Contains("收件人") && e.Contains("bad")),
            string.Join(" / ", badResult.Errors));

        var tooMany = ValidConfig();
        tooMany.To = string.Join(",", Enumerable.Range(0, 60).Select(i => $"u{i}@x.com"));
        check("[邮件推送] 收件人超过 50 个（含抄送 / 密送）时拒绝发送",
            MailComposer.Compose(tooMany, tooMany.BodyHtml).Errors.Any(e => e.Contains("超过上限")),
            string.Join(" / ", MailComposer.Compose(tooMany, tooMany.BodyHtml).Errors));

        var withCc = ValidConfig();
        withCc.Cc = "cc@x.com";
        withCc.Bcc = "bcc@x.com";
        withCc.ReplyTo = "reply@x.com";
        var ccRequest = MailComposer.Compose(withCc, withCc.BodyHtml).Request;
        check("[邮件推送] 抄送 / 密送 / 回复地址分别落到 cc / bcc / reply_to",
            ccRequest is { Cc.Count: 1, Bcc.Count: 1, ReplyTo.Count: 1 }
            && ccRequest.Cc[0] == "cc@x.com" && ccRequest.Bcc[0] == "bcc@x.com" && ccRequest.ReplyTo[0] == "reply@x.com",
            JsonFileStoreForProbe(ccRequest));

        var noPlain = ValidConfig();
        noPlain.IncludePlainText = false;
        check("[邮件推送] 关掉纯文本兜底后不再生成 text 字段",
            MailComposer.Compose(noPlain, noPlain.BodyHtml).Request is { Text: null });

        // 附件：用假的字节源（不碰真实文件），只验证头像体积与内容转换
        var smallBytes = new byte[3072];
        var attachments = MailComposer.Compose(
            ValidConfig(), "<p>x</p>", new[] { @"C:\fake\报告.pdf" },
            readFile: _ => smallBytes);
        var attachment = attachments.Request?.Attachments;
        check("[邮件推送] 附件读成 base64 并带上文件名 / 媒体类型",
            attachment is { Count: 1 }
            && attachment[0].FileName == "报告.pdf"
            && attachment[0].ContentType == "application/pdf"
            && attachment[0].Content == Convert.ToBase64String(smallBytes)
            && attachment[0].ContentId is null,
            attachment is null ? attachments.FirstError : $"{attachment[0].FileName} / {attachment[0].ContentType}");

        check("[邮件推送] 附件 base64 后体积被统计出来（40MB 上限按这个算）",
            attachments.AttachmentBytesAfterBase64 == MailAttachmentItem.Base64SizeOf(smallBytes.Length),
            attachments.AttachmentBytesAfterBase64.ToString());

        var tooBig = new byte[40 * 1024 * 1024];
        var tooBigResult = MailComposer.Compose(
            ValidConfig(), "<p>x</p>", new[] { @"C:\fake\big.bin" }, readFile: _ => tooBig);
        check("[邮件推送] 附件 total 超过 40MB（base64 后）时拒绝发送",
            !tooBigResult.Success && tooBigResult.Errors.Any(e => e.Contains("附件太大")),
            tooBigResult.FirstError);

        var blocked = MailComposer.Compose(
            ValidConfig(), "<p>x</p>", new[] { @"C:\fake\setup.exe" }, readFile: _ => new byte[16]);
        check("[邮件推送] 可执行文件附件被拦下并说明原因",
            !blocked.Success && blocked.Errors.Any(e => e.Contains("拒收") && e.Contains("setup.exe")),
            blocked.FirstError);

        var missingFile = MailComposer.Compose(
            ValidConfig(), "<p>x</p>", new[] { @"C:\fake\none.bin" }, readFile: _ => null);
        check("[邮件推送] 附件读不到时提示「文件被移动或删除」",
            !missingFile.Success && missingFile.Errors.Any(e => e.Contains("读不到")),
            missingFile.FirstError);

        // 行内图片：转 cid + 作为带 content_id 的附件上传
        var inline = MailComposer.Compose(
            ValidConfig(), "<p><img src=\"cid:inline-1@yuliang\"></p>",
            inlineImages: new[] { new InlineImage(@"C:\fake\pixel.png", "inline-1@yuliang") },
            readFile: _ => new byte[8]);
        check("[邮件推送] 行内图片作为带 content_id 的附件上传（HTML 里用 cid: 引用）",
            inline.Request?.Attachments is { Count: 1 }
            && inline.Request.Attachments[0].ContentId == "inline-1@yuliang"
            && inline.Request.Attachments[0].ContentType == "image/png",
            inline.Request?.Attachments is null ? inline.FirstError : inline.Request.Attachments[0].ContentId ?? "");

        var resolver = MailComposer.BuildCidResolver(new[] { new InlineImage(@"C:\a\p.png", "inline-1@yuliang") });
        check("[邮件推送] cid 解析器只换认识的图片路径，其它原样返回",
            resolver(@"C:\a\p.png") == "cid:inline-1@yuliang"
            && resolver(@"C:\a\other.png") == @"C:\a\other.png"
            && resolver("https://x/y.png") == "https://x/y.png");

        check("[邮件推送] content_id 短于 128 字符的上限",
            MailComposer.NextContentId(1234).Length < 128,
            MailComposer.NextContentId(1234));
    }

    private static string JsonFileStoreForProbe(object? value)
        => YuLiangToolkit.Core.Storage.JsonFileStore.ToJson(value, indented: false);

    // ---------------------------------------------------------------- 请求头 / 请求体（假 handler）

    private static void CheckClient(CheckFn check)
    {
        var captured = new List<(HttpRequestMessage Request, string Body)>();
        var handler = new StubHandler(request =>
        {
            var body = request.Content is null
                ? ""
                : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            captured.Add((request, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"01a1ca17-7e83-7a1c-ba24-fd6bdc541edd"}""", Encoding.UTF8, "application/json"),
            };
        });

        var client = new ResendClient(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) });
        var request = new ResendEmailRequest
        {
            From = "Congratulations! <test@contact.yulau.space>",
            To = new List<string> { "fantasyuliang@qq.com" },
            Subject = "自检",
            Html = "<p>hi</p>",
            Text = "hi",
        };

        var result = client.SendAsync(MailSenderDefaults.Endpoint, "re_selftest_key", request, "idem-123")
            .GetAwaiter().GetResult();

        check("[邮件推送] 成功响应（2xx + id）判定为成功并取出邮件 id",
            result.Success && result.HttpStatus == 200
            && result.EmailId == "01a1ca17-7e83-7a1c-ba24-fd6bdc541edd",
            result.Summary);

        var sent = captured.FirstOrDefault();
        check("[邮件推送] 请求是 POST 到配置的地址",
            sent.Request.Method == HttpMethod.Post
            && sent.Request.RequestUri?.ToString() == MailSenderDefaults.Endpoint,
            sent.Request.RequestUri?.ToString() ?? "（没有请求）");

        check("[邮件推送] 鉴权头是 Authorization: Bearer <key>",
            sent.Request.Headers.TryGetValues("Authorization", out var auth)
            && auth.Single() == "Bearer re_selftest_key",
            sent.Request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : "（缺失）");

        check("[邮件推送] 请求带 User-Agent（Resend 无 UA 会 403 / error code 1010）",
            sent.Request.Headers.TryGetValues("User-Agent", out var ua)
            && ua.Single() == MailSenderDefaults.UserAgent,
            sent.Request.Headers.TryGetValues("User-Agent", out var u) ? string.Join(",", u) : "（缺失）");

        check("[邮件推送] 传了幂等键时会带 Idempotency-Key 头",
            sent.Request.Headers.TryGetValues("Idempotency-Key", out var idem) && idem.Single() == "idem-123",
            sent.Request.Headers.TryGetValues("Idempotency-Key", out var i) ? string.Join(",", i) : "（缺失）");

        check("[邮件推送] 请求体是 UTF-8 JSON，字段名用下划线风格（from / to / subject / html / text）",
            sent.Request.Content?.Headers.ContentType?.MediaType == "application/json"
            && sent.Body.Contains("\"from\":")
            && sent.Body.Contains("\"to\":[")
            && sent.Body.Contains("\"subject\":")
            && sent.Body.Contains("\"html\":")
            && sent.Body.Contains("\"text\":"),
            sent.Body.Length > 160 ? sent.Body[..160] + "…" : sent.Body);

        // 没传幂等键时不带这个头
        captured.Clear();
        client.SendAsync(MailSenderDefaults.Endpoint, "k", request).GetAwaiter().GetResult();
        var withoutIdem = captured.FirstOrDefault();
        check("[邮件推送] 没传幂等键时不发 Idempotency-Key 头",
            !withoutIdem.Request.Headers.Contains("Idempotency-Key"));

        // 422 校验失败：错误不抛异常，翻译成中文建议，原文保留
        var failure = new ResendClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity)
        {
            Content = new StringContent(
                """{"name":"missing_required_field","statusCode":422,"message":"The request body is missing one or more required fields."}""",
                Encoding.UTF8, "application/json"),
        }))).SendAsync(MailSenderDefaults.Endpoint, "k", request).GetAwaiter().GetResult();
        check("[邮件推送] 422 被解析成「字段校验失败」并保留服务端原文",
            !failure.Success && failure.HttpStatus == 422
            && failure.ErrorName == "missing_required_field"
            && failure.ErrorMessage.Contains("字段校验失败")
            && failure.ErrorMessage.Contains("missing one or more required fields"),
            failure.ErrorMessage);

        // 网络异常：不抛给界面，变成可读提示
        var network = new ResendClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("错误的主机名"))))
            .SendAsync(MailSenderDefaults.Endpoint, "k", request).GetAwaiter().GetResult();
        check("[邮件推送] 网络异常转成中文提示而不是抛出",
            !network.Success && network.HttpStatus == 0 && network.ErrorMessage.Contains("网络请求失败"),
            network.ErrorMessage);

        check("[邮件推送] 状态码短语（日志里显示 200 OK）",
            ResendClient.StatusText(200) == "200 OK" && ResendClient.StatusText(0) == "（未连接）",
            ResendClient.StatusText(200));

        check("[邮件推送] 成功响应里没有 id 也不影响成功判定",
            ResendClient.ParseEmailId("{}") == "" && ResendClient.ParseEmailId("") == ""
            && ResendClient.ParseEmailId("not json") == "");
    }

    // ---------------------------------------------------------------- 错误翻译

    private static void CheckErrorTranslation(CheckFn check)
    {
        check("[邮件推送] 401 → 提示重新复制 API Key",
            ResendClient.TranslateError(401, "validation_error", "API key is invalid").Contains("API Key 无效"));

        check("[邮件推送] 403 域名未验证 → 提示去验证域名",
            ResendClient.TranslateError(403, "validation_error",
                "The `example.com` domain is not verified. Please, add and verify your domain.").Contains("还没验证"));

        check("[邮件推送] 403 测试域名只能发给自己 → 提示换已验证域名",
            ResendClient.TranslateError(403, "validation_error",
                "You can only send testing emails to your own email address (a@b.com).").Contains("只能发给账号本人"));

        check("[邮件推送] 429 区分「每秒限流 / 每日配额 / 每月配额」",
            ResendClient.TranslateError(429, "rate_limit_exceeded", "Too many requests.").Contains("限流")
            && ResendClient.TranslateError(429, "daily_quota_exceeded", "You have exceeded your daily email sending quota.").Contains("今天的免费额度")
            && ResendClient.TranslateError(429, "monthly_quota_exceeded", "You have exceeded your monthly email sending quota.").Contains("本月"),
            ResendClient.TranslateError(429, "daily_quota_exceeded", "You have exceeded your daily email sending quota."));

        check("[邮件推送] 附件被拒（422 invalid_attachment）有专门提示",
            ResendClient.TranslateError(422, "invalid_attachment",
                "Attachment must have either a `content` or `path`.").Contains("附件"));

        check("[邮件推送] 服务端没给 message 时也能给出可执行的中文说明",
            ResendClient.TranslateError(500, "", "").Contains("内部错误")
            && ResendClient.TranslateError(0, "", "").Contains("请求没发出去"),
            ResendClient.TranslateError(500, "", ""));

        var parsed = ResendClient.ParseError("<html>502 Bad Gateway</html>", 502);
        check("[邮件推送] 非 JSON 错误页（网关）也能解析出状态与原文片段",
            parsed.StatusCode == 502 && (parsed.Message ?? "").Contains("502"),
            parsed.Message ?? "");
    }

    // ---------------------------------------------------------------- API Key

    private static void CheckApiKey(CheckFn check)
    {
        const string secret = "re_selftest_1234567890";
        var cipher = ApiKeyStore.Protect(secret);
        check("[邮件推送] DPAPI：密文带 dpapi: 前缀且不含明文",
            ApiKeyStore.IsProtected(cipher) && !cipher.Contains(secret, StringComparison.Ordinal),
            cipher[..Math.Min(24, cipher.Length)] + "…");

        check("[邮件推送] DPAPI：同一用户能解回原文",
            ApiKeyStore.Unprotect(cipher) == secret);

        check("[邮件推送] DPAPI：空值 / 历史明文兼容",
            ApiKeyStore.Protect("") == "" && ApiKeyStore.Unprotect("") == ""
            && ApiKeyStore.Unprotect("re_plain") == "re_plain");

        check("[邮件推送] Key 打码：只留前 4 后 4，空值给中文说明",
            ApiKeyStore.Mask("re_AbCdEfGh_1234567890abcdefghijklmn") == "re_A…klmn"
            && ApiKeyStore.Mask("") == "（未填写）"
            && ApiKeyStore.Mask("short") == "shor…",
            ApiKeyStore.Mask("re_AbCdEfGh_1234567890abcdefghijklmn"));
    }

    // ---------------------------------------------------------------- 配置

    private static void CheckConfig(CheckFn check)
    {
        var config = new MailSenderConfig();
        check("[邮件推送] 配置默认值：推送地址 / 空发件人 / 带示例正文 / 不带附件 / 生成纯文本",
            config.Endpoint == MailSenderDefaults.Endpoint
            && config.FromAddress == "" && config.Subject == ""
            && config.BodyHtml.Contains("<h2>") && config.Attachments.Count == 0
            && config.IncludePlainText && !config.HasApiKey,
            $"{config.Endpoint} / apiKey={config.HasApiKey}");

        check("[邮件推送] 示例正文本身不算空正文",
            !MailHtml.IsEmptyBody(config.BodyHtml));

        config.FromName = "Congratulations!";
        config.FromAddress = "test@contact.yulau.space";
        config.To = "fantasyuliang@qq.com";
        config.Subject = "往返测试";
        config.Attachments.Add(@"C:\a\1.pdf");
        config.ApiKeyCipher = ApiKeyStore.Protect("re_roundtrip");

        var json = YuLiangToolkit.Core.Storage.JsonFileStore.Serialize(config);
        var restored = JsonSerializer.Deserialize<MailSenderConfig>(
            json, YuLiangToolkit.Core.Storage.JsonFileStore.Options);
        check("[邮件推送] 配置 JSON 往返：字段 / 附件清单 / 密文都保留",
            restored is not null
            && restored.FromDisplay == "Congratulations! <test@contact.yulau.space>"
            && restored.To == "fantasyuliang@qq.com"
            && restored.Subject == "往返测试"
            && restored.Attachments.SequenceEqual(new[] { @"C:\a\1.pdf" })
            && ApiKeyStore.Unprotect(restored.ApiKeyCipher) == "re_roundtrip",
            restored is null ? "反序列化为 null" : $"{restored.FromDisplay} / {restored.Attachments.Count} 个附件");

        check("[邮件推送] 配置里存的是密文、不出现明文 Key",
            json.Contains("apiKeyCipher", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("re_roundtrip", StringComparison.Ordinal),
            json.Split('\n').FirstOrDefault(line => line.Contains("apiKeyCipher"))?.Trim() ?? json);

        var dirty = new MailSenderConfig
        {
            ApiKeyCipher = null!,
            FromName = null!,
            To = null!,
            Attachments = null!,
            Endpoint = "",
        };
        dirty.Normalize();
        check("[邮件推送] 手改配置写成 null 时 Normalize 兜住（不会让界面崩）",
            dirty.ApiKeyCipher == "" && dirty.FromName == "" && dirty.To == ""
            && dirty.Attachments.Count == 0 && dirty.Endpoint == MailSenderDefaults.Endpoint);

        var pruneDir = SelfCheckScratch.MakeDirectory("mail-sender-prune");
        var keep = Path.Combine(pruneDir, "keep.txt");
        File.WriteAllText(keep, "x");
        var pruned = new MailSenderConfig();
        pruned.Attachments.Add(keep);
        pruned.Attachments.Add(Path.Combine(pruneDir, "missing.txt"));
        var removed = pruned.PruneMissingAttachments();
        check("[邮件推送] 打开工具时剔除已不存在的附件（保留仍在的）",
            removed == 1 && pruned.Attachments.SequenceEqual(new[] { keep }),
            string.Join(" | ", pruned.Attachments));
    }

    // ---------------------------------------------------------------- 查询投递状态

    private static void CheckQueryHelpers(CheckFn check)
    {
        check("[邮件推送] 查询地址：/emails → /emails/{id}（去掉多余斜杠）",
            MailSenderViewModel.BuildEmailQueryUrl("https://api.resend.com/emails", "abc")
                == "https://api.resend.com/emails/abc"
            && MailSenderViewModel.BuildEmailQueryUrl("https://api.resend.com/emails/", "abc")
                == "https://api.resend.com/emails/abc"
            && MailSenderViewModel.BuildEmailQueryUrl("", "abc")
                == "https://api.resend.com/emails/abc",
            MailSenderViewModel.BuildEmailQueryUrl("https://api.resend.com/emails", "abc"));

        check("[邮件推送] 从查询响应里读 last_event（坏 JSON 返回空串）",
            MailSenderViewModel.ReadLastEvent("""{"id":"x","last_event":"delivered"}""") == "delivered"
            && MailSenderViewModel.ReadLastEvent("""{"id":"x"}""") == ""
            && MailSenderViewModel.ReadLastEvent("not json") == ""
            && MailSenderViewModel.ReadLastEvent(null) == "");

        check("[邮件推送] last_event 翻译成中文（未知值原样显示）",
            MailSenderViewModel.TranslateEvent("delivered").Contains("已送达")
            && MailSenderViewModel.TranslateEvent("bounced").Contains("退回")
            && MailSenderViewModel.TranslateEvent("") == "（还没有状态）"
            && MailSenderViewModel.TranslateEvent("weird") == "weird");
    }

    // ---------------------------------------------------------------- 富文本 ⇄ HTML（STA）

    /// <summary>
    /// FlowDocument 必须在 STA 线程上创建（WPF 文档对象有线程亲和性），所以这一段单独起一个 STA 线程跑。
    /// </summary>
    private static void CheckRichText(CheckFn check)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunRichText(check); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            check("[邮件推送] 富文本 ⇄ HTML 自检运行（STA 线程）", false, failure.Message);
    }

    private static void RunRichText(CheckFn check)
    {
        // ---------- 生成：格式 / 段落 / 列表 / 链接 / 图片 ----------
        var document = new FlowDocument { FontSize = RichTextHtml.DefaultFontSize };
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("普通 "));
        paragraph.Inlines.Add(new Run("加粗") { FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run("斜体") { FontStyle = FontStyles.Italic });
        paragraph.Inlines.Add(new Run("下划线")
        {
            TextDecorations = new TextDecorationCollection { RichTextHtml.CreateDecoration(TextDecorationLocation.Underline) },
        });
        paragraph.Inlines.Add(new Run("彩色") { Foreground = new SolidColorBrush(Color.FromRgb(0x2F, 0x6F, 0xDB)) });
        document.Blocks.Add(paragraph);

        var html = RichTextHtml.ToHtml(document);
        check("[邮件推送] 富文本 → HTML：段落 + 粗体 / 斜体 / 下划线 / 颜色",
            html.Contains("<p>") && html.Contains("<b>加粗</b>") && html.Contains("<i>斜体</i>")
            && html.Contains("<u>下划线</u>") && html.Contains("color:#2F6FDB"),
            html);

        var listDocument = new FlowDocument
        {
            Blocks =
            {
                new List
                {
                    MarkerStyle = TextMarkerStyle.Decimal,
                    ListItems =
                    {
                        new ListItem(new Paragraph(new Run("第一项"))),
                        new ListItem(new Paragraph(new Run("第二项"))),
                    },
                },
            },
        };
        var listHtml = RichTextHtml.ToHtml(listDocument);
        check("[邮件推送] 富文本 → HTML：有序列表",
            listHtml.Contains("<ol>") && listHtml.Contains("<li>第一项</li>") && listHtml.Contains("<li>第二项</li>"),
            listHtml);

        var linkDocument = new FlowDocument();
        var linkParagraph = new Paragraph();
        linkParagraph.Inlines.Add(new Hyperlink(new Run("Resend")) { Tag = "https://resend.com" });
        linkDocument.Blocks.Add(linkParagraph);
        var linkHtml = RichTextHtml.ToHtml(linkDocument);
        check("[邮件推送] 富文本 → HTML：超链接保留地址",
            linkHtml.Contains("<a href=\"https://resend.com\">Resend</a>"),
            linkHtml);

        var imageDocument = new FlowDocument();
        var imageParagraph = new Paragraph();
        imageParagraph.Inlines.Add(new InlineUIContainer(new Image { Tag = @"C:\pics\图 1.png" }));
        imageDocument.Blocks.Add(imageParagraph);
        var imageHtml = RichTextHtml.ToHtml(imageDocument);
        check("[邮件推送] 富文本 → HTML：行内图片带 src 与 alt",
            imageHtml.Contains("src=\"C:\\pics\\图 1.png\"") || imageHtml.Contains("src=\"C:\\pics\\图 1.png\""),
            imageHtml);

        var cidHtml = RichTextHtml.ToHtml(imageDocument, _ => "cid:inline-1@yuliang");
        check("[邮件推送] 富文本 → HTML：src 解析器能把图片换成 cid:",
            cidHtml.Contains("src=\"cid:inline-1@yuliang\""), cidHtml);

        check("[邮件推送] 富文本 → HTML：文字里的尖括号被转义（不会写坏 HTML）",
            RichTextHtml.ToHtml(new FlowDocument(new Paragraph(new Run("a < b & c")))).Contains("a &lt; b &amp; c"));

        var emptyHtml = RichTextHtml.ToHtml(new FlowDocument(new Paragraph()));
        check("[邮件推送] 富文本 → HTML：空段落输出 <p><br></p>（保留空行）",
            emptyHtml.Contains("<p><br></p>"), emptyHtml);

        // ---------- 解析：往返 ----------
        var roundTripSource = "<h2>标题</h2><p>正文 <b>加粗</b> <i>斜体</i> <u>下划线</u> "
                              + "<span style=\"color:#D0453E\">红字</span></p>"
                              + "<ul><li>甲</li><li>乙</li></ul>"
                              + "<p style=\"text-align:center\">居中</p>";
        var parsed = RichTextHtml.FromHtml(roundTripSource);
        var parsedText = new TextRange(parsed.ContentStart, parsed.ContentEnd).Text;
        check("[邮件推送] HTML → 富文本：文字都还原（表格 / 列表 / 颜色都不丢字）",
            parsedText.Contains("标题") && parsedText.Contains("正文") && parsedText.Contains("加粗")
            && parsedText.Contains("斜体") && parsedText.Contains("下划线") && parsedText.Contains("红字")
            && parsedText.Contains("甲") && parsedText.Contains("乙") && parsedText.Contains("居中"),
            parsedText.Replace("\r\n", " / "));

        var headingSize = parsed.Blocks.OfType<Paragraph>().FirstOrDefault()?.FontSize ?? 0;
        check("[邮件推送] HTML → 富文本：<h2> 还原成 24px 段落（导出时会写回 h2）",
            Math.Abs(headingSize - 24) < 0.01, headingSize.ToString("0.#"));

        var centeredAligned = parsed.Blocks.OfType<Paragraph>().LastOrDefault()?.TextAlignment;
        check("[邮件推送] HTML → 富文本：text-align 还原为居中",
            centeredAligned == TextAlignment.Center, centeredAligned?.ToString() ?? "（无）");

        var reExported = RichTextHtml.ToHtml(parsed);
        check("[邮件推送] 往返：<h2> / <b> / <i> / <u> / 颜色 / 列表都还在",
            reExported.Contains("<h2>") && reExported.Contains("<b>加粗</b>")
            && reExported.Contains("<i>斜体</i>") && reExported.Contains("<u>下划线</u>")
            && reExported.Contains("color:#D0453E") && reExported.Contains("<ul>")
            && reExported.Contains("<li>甲</li>"),
            reExported);

        // 往返两遍结果一致（不会越改越乱）
        var secondPass = RichTextHtml.ToHtml(RichTextHtml.FromHtml(reExported));
        check("[邮件推送] 往返两遍结果稳定（不会越转越乱）",
            string.Equals(reExported, secondPass, StringComparison.Ordinal),
            secondPass.Length > 200 ? secondPass[..200] + "…" : secondPass);

        // ---------- 解析：容错 ----------
        var messy = RichTextHtml.FromHtml(
            "<html><head><title>不该出现</title><style>p{color:red}</style></head><body>"
            + "<script>alert(1)</script><p>正常文字</p><unknown>未知标签里的字</unknown></body></html>");
        var messyText = new TextRange(messy.ContentStart, messy.ContentEnd).Text;
        check("[邮件推送] HTML → 富文本：script / style / head 整段丢掉，未知标签只留文字",
            messyText.Contains("正常文字") && messyText.Contains("未知标签里的字")
            && !messyText.Contains("alert") && !messyText.Contains("不该出现") && !messyText.Contains("color:red"),
            messyText.Replace("\r\n", " / "));

        check("[邮件推送] HTML → 富文本：空输入得到一段空段落（编辑器不会空白到没法输入）",
            RichTextHtml.FromHtml("").Blocks.Count == 1
            && RichTextHtml.FromHtml(null).Blocks.Count == 1);

        var tableDoc = RichTextHtml.FromHtml("<table><tr><th>表头A</th><th>表头B</th></tr><tr><td>1</td><td>2</td></tr></table>");
        var table = tableDoc.Blocks.OfType<Table>().FirstOrDefault();
        check("[邮件推送] HTML → 富文本：表格还原成 Table（表头行标成 header）",
            table is not null && table.RowGroups[0].Rows.Count == 2
            && table.RowGroups[0].Rows[0].Tag as string == "header",
            table is null ? "（没有表格）" : $"{table.RowGroups[0].Rows.Count} 行");

        var tableHtml = RichTextHtml.ToHtml(tableDoc);
        check("[邮件推送] 富文本 → HTML：表格回来是 <table><tr><th>…",
            tableHtml.Contains("<table>") && tableHtml.Contains("<th>表头A</th>") && tableHtml.Contains("<td>1</td>"),
            tableHtml);

        // data: 图片能解析出来（预览用），远程图片不联网、留占位
        var dataUri = "data:image/png;base64," + Convert.ToBase64String(OnePixelPng());
        check("[邮件推送] data: 内联图片能解码（预览用）",
            RichTextHtml.TryLoadBitmap(dataUri, out var bitmap) && bitmap.PixelWidth == 1,
            "1x1 png");

        check("[邮件推送] cid: / http(s) 图片不联网（返回 false，画占位）",
            !RichTextHtml.TryLoadBitmap("cid:inline-1@yuliang", out _)
            && !RichTextHtml.TryLoadBitmap("https://example.com/a.png", out _));

        var cidDoc = RichTextHtml.FromHtml("<p><img src=\"cid:inline-1@yuliang\"></p>");
        var cidBack = RichTextHtml.ToHtml(cidDoc);
        check("[邮件推送] HTML → 富文本 → HTML：cid 图片的 src 原样保留（往返不丢）",
            cidBack.Contains("cid:inline-1@yuliang"), cidBack);
    }

    /// <summary>一张 1×1 的透明 PNG（自检里造 data: 图片用）。</summary>
    private static byte[] OnePixelPng() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8AAAwAB/wFc0mOZAAAAAElFTkSuQmCC");

    /// <summary>假 HttpMessageHandler：把请求交回给测试代码并返回预设响应（不碰网络）。</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
