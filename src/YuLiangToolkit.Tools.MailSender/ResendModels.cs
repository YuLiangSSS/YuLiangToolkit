using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.MailSender;

/// <summary>请求里的一个附件（Resend 的 <c>attachments[]</c>：<c>content</c> 是 base64 字符串）。</summary>
public sealed record ResendAttachment(
    [property: JsonPropertyName("filename")] string FileName,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("content_id")] string? ContentId = null);

/// <summary>
/// <c>POST /emails</c> 的请求体。
///
/// <para>字段名与 Resend 官方 OpenAPI 一致（<c>reply_to</c> / <c>content_type</c> 是下划线风格，
/// 别改成 camelCase，否则服务端当场 422）。空值用 <see cref="JsonIgnoreCondition.WhenWritingNull"/>
/// 忽略，避免把 <c>"cc": null</c> 这种脏字段发出去。</para>
/// </summary>
public sealed class ResendEmailRequest
{
    [JsonPropertyName("from")] public string From { get; set; } = "";

    [JsonPropertyName("to")] public List<string> To { get; set; } = new();

    [JsonPropertyName("subject")] public string Subject { get; set; } = "";

    [JsonPropertyName("html")] public string? Html { get; set; }

    [JsonPropertyName("text")] public string? Text { get; set; }

    [JsonPropertyName("cc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Cc { get; set; }

    [JsonPropertyName("bcc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Bcc { get; set; }

    [JsonPropertyName("reply_to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReplyTo { get; set; }

    [JsonPropertyName("attachments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ResendAttachment>? Attachments { get; set; }
}

/// <summary>成功的响应体：只有一个 <c>id</c>（形如 <c>49a3999c-…</c>）。</summary>
public sealed class ResendEmailResponse
{
    [JsonPropertyName("id")] public string? Id { get; set; }
}

/// <summary>失败时的响应体：<c>{ "name": …, "statusCode": …, "message": … }</c>（三个字段都可能缺）。</summary>
public sealed class ResendErrorResponse
{
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("statusCode")] public int? StatusCode { get; set; }

    [JsonPropertyName("message")] public string? Message { get; set; }
}

/// <summary>
/// 一次发送的结果：成功给 <see cref="EmailId"/>，失败给 HTTP 状态 + 服务端 <c>name</c> / <c>message</c>
/// 以及一句中文建议（界面直接显示）。
/// </summary>
public sealed record ResendSendResult(
    bool Success,
    int HttpStatus,
    string EmailId,
    string ErrorName,
    string ErrorMessage,
    string RawBody)
{
    /// <summary>没拿到有效响应（超时 / DNS / TLS）时的结果。</summary>
    public static ResendSendResult NetworkFailure(string message)
        => new(false, 0, "", "network_error", message, "");

    /// <summary>界面状态行 / 日志里的一行摘要。</summary>
    public string Summary => Success
        ? $"发送成功 · id {EmailId}"
        : HttpStatus == 0
            ? ErrorMessage
            : $"发送失败（HTTP {HttpStatus} · {ErrorName}）：{ErrorMessage}";
}
