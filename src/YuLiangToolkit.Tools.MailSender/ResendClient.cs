using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>
/// 邮件推送接口（Resend <c>POST /emails</c>）的手写 HTTP 调用器。
///
/// <para><b>为什么不用官方 SDK</b>：这里只需要一个 POST；手写能完全控制请求头（尤其下面那条 UA）
/// 与错误文案，也避免为一个功能多引一个第三方包。</para>
///
/// <para><b>必须带 User-Agent</b>：Resend 会把没有 UA 的请求直接挡在 API 之前（403 / error code 1010）。
/// .NET 的 HttpClient 默认<b>不发送</b> UA，所以这里显式设置——这条踩过，见 docs/tools/mail-sender.md。</para>
///
/// <para><b>成功判定用 2xx</b>（实际返回 200，不是 201），响应体读 <c>id</c>；
/// 失败按 <c>{ statusCode, name, message }</c> 解析后翻成中文建议。</para>
///
/// <para>构造时可注入 <see cref="HttpClient"/>（自检用假 handler 断言请求长什么样），
/// 默认用带 UA 和 60 秒超时的共享实例——附件大时上传本身就慢，超时别设太短。</para>
/// </summary>
public sealed class ResendClient
{
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public ResendClient(HttpClient? http = null) => _http = http ?? SharedHttpClient;

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(MailSenderDefaults.UserAgent);
        return client;
    }

    /// <summary>
    /// 发送一封邮件。网络错误不抛给界面，统一转成 <see cref="ResendSendResult"/> 里的一条中文说明。
    /// </summary>
    /// <param name="endpoint">API 地址（默认 <see cref="MailSenderDefaults.Endpoint"/>）。</param>
    /// <param name="apiKey">API Key（Bearer）。</param>
    /// <param name="request">请求体。</param>
    /// <param name="idempotencyKey">可选：同一 key 重发不会重复投递（服务端保留 24 小时）。</param>
    public async Task<ResendSendResult> SendAsync(
        string endpoint,
        string apiKey,
        ResendEmailRequest request,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var url = string.IsNullOrWhiteSpace(endpoint) ? MailSenderDefaults.Endpoint : endpoint.Trim();
            var json = JsonSerializer.Serialize(request, JsonOptions);

            using var message = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            message.Headers.TryAddWithoutValidation("Authorization", "Bearer " + (apiKey ?? "").Trim());
            // 显式补一次 UA：HttpClient 默认不发，而 Resend 没有它就 403（error code 1010）
            message.Headers.TryAddWithoutValidation("User-Agent", MailSenderDefaults.UserAgent);
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
                message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey!.Trim());

            using var response = await _http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                var id = ParseEmailId(body);
                return new ResendSendResult(true, status, id, "", "", body);
            }

            var error = ParseError(body, status);
            return new ResendSendResult(
                false, status, "", error.Name ?? "", TranslateError(status, error.Name, error.Message), body);
        }
        catch (OperationCanceledException)
        {
            return ResendSendResult.NetworkFailure("请求超时（60 秒）或已取消，请检查网络 / 附件大小后重试");
        }
        catch (HttpRequestException ex)
        {
            return ResendSendResult.NetworkFailure($"网络请求失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ResendSendResult.NetworkFailure($"发送失败：{ex.Message}");
        }
    }

    /// <summary>从成功响应里取 <c>id</c>；取不到返回空串（不影响「成功」这个结论）。</summary>
    public static string ParseEmailId(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            var parsed = JsonSerializer.Deserialize<ResendEmailResponse>(body);
            return parsed?.Id ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>解析错误体（<c>statusCode</c> 以 HTTP 状态为准，body 里的只当参考）。</summary>
    public static ResendErrorResponse ParseError(string? body, int httpStatus)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ResendErrorResponse>(body);
                if (parsed is not null) return parsed;
            }
            catch
            {
                // 不是 JSON（网关返回的 HTML 错误页之类）：下面走兜底
            }
        }
        return new ResendErrorResponse { StatusCode = httpStatus, Message = Shorten(body) };
    }

    /// <summary>
    /// 把服务端错误翻成「用户能照着做」的中文。服务端原文照抄在括号里——排查时要看原文。
    /// </summary>
    public static string TranslateError(int httpStatus, string? name, string? message)
    {
        var original = (message ?? "").Trim();
        var nameText = (name ?? "").Trim();

        var hint = httpStatus switch
        {
            400 when nameText.StartsWith("invalid_idempotency_key", StringComparison.OrdinalIgnoreCase) =>
                "幂等键不合法（长度需在 1~256 之间）",
            400 => "请求格式有问题（检查各字段是否为空）",
            401 => "API Key 无效或已被删除：重新到控制台复制一个（注意别带上多余空格）",
            403 when original.Contains("not verified", StringComparison.OrdinalIgnoreCase) ||
                       original.Contains("verify your domain", StringComparison.OrdinalIgnoreCase) =>
                "发件域名还没验证：到推送服务控制台把发件地址所在的域名验证通过再发",
            403 when original.Contains("testing emails", StringComparison.OrdinalIgnoreCase) =>
                "测试域名（resend.dev）只能发给账号本人的邮箱：换一个已验证域名的发件地址",
            403 => "这个 Key 没有发送权限，或账号被停用 / 挂起",
            404 => "推送地址不存在：检查 API 地址是否写错",
            405 => "HTTP 方法不被允许：API 地址可能不是发送邮件接口",
            409 => "幂等键冲突：同一幂等键的请求内容变了，或上一次还没处理完",
            422 when nameText.Equals("invalid_attachment", StringComparison.OrdinalIgnoreCase) ||
                       original.Contains("content` or `path", StringComparison.OrdinalIgnoreCase) =>
                "附件格式不被接受（要么给文件内容，要么给公网地址）",
            422 => "字段校验失败：检查发件人 / 收件人 / 主题 / 正文是否有空值或格式错误",
            429 when original.Contains("daily", StringComparison.OrdinalIgnoreCase) =>
                "今天的免费额度用完了（免费版每天 100 封，UTC 0 点重置）",
            429 when original.Contains("monthly", StringComparison.OrdinalIgnoreCase) =>
                "本月的发送额度用完了",
            429 => "发送太频繁（服务端限流，约 10 次/秒）：等一两秒再试",
            500 => "推送服务内部错误：稍后重试",
            503 => "推送服务暂时不可用：稍后重试",
            _ => httpStatus == 0 ? "请求没发出去" : "推送服务返回了错误",
        };

        return original.Length == 0 ? hint : $"{hint}（服务端原文：{Shorten(original)}）";
    }

    private static string Shorten(string? text)
    {
        var value = (text ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        return value.Length <= 400 ? value : value[..400] + "…";
    }

    /// <summary>把 HTTP 状态码转成短语（日志里显示 <c>200 OK</c> 比只显示数字直观）。</summary>
    public static string StatusText(int status) => status == 0 ? "（未连接）" : $"{status} {(HttpStatusCode)status}";
}
