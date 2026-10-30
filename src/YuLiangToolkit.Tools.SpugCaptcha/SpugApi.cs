using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.SpugCaptcha;

/// <summary>一次调用 Spug 的结果：业务码 <c>code == 200</c> 才算成功（HTTP 状态码固定是 200，不能拿来判断）。</summary>
public sealed record SpugSendResult(int Code, string Message, string RequestId, string RawBody, int HttpStatus)
{
    /// <summary>Spug 的 JSON 业务码为 200 才算请求被受理。</summary>
    public bool Success => Code == 200;

    /// <summary>没拿到有效响应（超时 / 网络错误）时的结果。</summary>
    public static SpugSendResult NetworkFailure(string message) => new(-1, message, "", "", 0);
}

/// <summary>
/// Spug 推送助手接口的纯逻辑部分：URL / 参数校验、请求构造、响应解析。
/// 独立成静态类是为了让自检能直接断言（不碰网络）。
///
/// <para>接口约定（见 https://push.spug.cc/guide/sms 与 /guide/mail）：</para>
/// <list type="bullet">
///   <item><c>/sms/&lt;模板编码&gt;</c>：GET <c>?to=手机号&amp;code=验证码[&amp;number=有效分钟数]</c>；</item>
///   <item><c>/mail/&lt;模板编码&gt;</c>：POST JSON <c>{"to","scene","code","minute"}</c>；</item>
///   <item>响应形如 <c>{"code":200,"msg":"请求成功","request_id":"…"}</c>，失败时 code 是 400 / 404 等。</item>
/// </list>
///
/// <para>⚠ 模板里的 <c>${code}</c>、<c>${number}</c> 是 Spug <b>服务端</b>的模板占位符，
/// 由 Spug 用请求参数替换；不要把它们写进工具里的 URL 输入框。</para>
/// </summary>
public static class SpugApi
{
    /// <summary>短信模板推送 URL 的路径特征（控制台复制的地址形如 https://push.spug.cc/sms/示例编码）。</summary>
    public const string SmsPathMarker = "/sms/";

    /// <summary>邮箱模板推送 URL 的路径特征（形如 https://push.spug.cc/mail/示例编码）。</summary>
    public const string MailPathMarker = "/mail/";

    private static readonly Regex PhonePattern = new(@"^1[3-9]\d{9}$", RegexOptions.Compiled);
    private static readonly Regex CodePattern = new(@"^[A-Za-z0-9]{4,6}$", RegexOptions.Compiled);

    /// <summary>HTTP(S) 绝对地址（空字符串 / 相对地址都算无效）。</summary>
    public static bool IsHttpUrl(string? url)
        => Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>是不是 Spug 短信模板推送地址（看路径里有没有 /sms/）。</summary>
    public static bool IsSmsTemplateUrl(string? url) => HasPathMarker(url, SmsPathMarker);

    /// <summary>是不是 Spug 邮箱模板推送地址（看路径里有没有 /mail/）。</summary>
    public static bool IsMailTemplateUrl(string? url) => HasPathMarker(url, MailPathMarker);

    private static bool HasPathMarker(string? url, string marker)
        => IsHttpUrl(url)
           && Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri)
           && uri.AbsolutePath.Contains(marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Spug 短信接口的手机号：11 位、以 1 开头（多号码逗号分隔的场景本工具不做）。</summary>
    public static bool IsValidPhone(string? phone) => PhonePattern.IsMatch((phone ?? "").Trim());

    /// <summary>验证码：4~6 位数字或字母（Spug 文档口径）。</summary>
    public static bool IsValidCode(string? code) => CodePattern.IsMatch((code ?? "").Trim());

    /// <summary>短信模板的 number 参数 = 有效分钟数（文档口径），这里只放行 1~120 的整数，挡住明显笔误。</summary>
    public static bool IsValidSmsMinutes(string? minutes)
        => int.TryParse((minutes ?? "").Trim(), out var value) && value is >= 1 and <= 120;

    /// <summary>邮箱：用 .NET 的地址解析（比手写正则靠谱），并拒绝多收件人 / 带空格的写法。</summary>
    public static bool IsValidEmail(string? email)
    {
        var text = (email ?? "").Trim();
        if (text.Length == 0 || text.Contains(',') || text.Contains(' ')) return false;
        try
        {
            var address = new MailAddress(text);
            return string.Equals(address.Address, text, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// 拼接短信 GET 请求地址：保留用户粘贴的 URL（含其已有的 query），再追加 to / code / number。
    /// 参数值都做百分号编码，特殊字符不会把 query 拆坏。
    /// </summary>
    public static string BuildSmsRequestUrl(string url, string phone, string code, string? minutes)
    {
        var text = (url ?? "").Trim();
        var hashIndex = text.IndexOf('#');
        if (hashIndex >= 0) text = text[..hashIndex];

        var builder = new StringBuilder(text);
        var separator = text.Contains('?') ? (text.EndsWith('?') || text.EndsWith('&') ? "" : "&") : "?";

        void Append(string key, string value)
        {
            builder.Append(separator).Append(key).Append('=').Append(Uri.EscapeDataString(value));
            separator = "&";
        }

        Append("to", phone.Trim());
        Append("code", code.Trim());
        if (!string.IsNullOrWhiteSpace(minutes)) Append("number", minutes.Trim());
        return builder.ToString();
    }

    /// <summary>构造邮箱 POST 请求的 JSON（字段名与 Spug 文档一致；System.Text.Json 保证中文 scene 正确输出）。</summary>
    public static string BuildMailJson(string email, string scene, string code, string minute)
        => JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["to"] = email.Trim(),
            ["scene"] = scene.Trim(),
            ["code"] = code.Trim(),
            ["minute"] = minute.Trim(),
        });

    /// <summary>
    /// 解析 Spug 的 JSON 响应。HTTP 状态基本恒为 200，是否成功看 JSON 里的 <c>code == 200</c>；
    /// 非 JSON / 奇怪结构也不抛异常，转成一条可读的失败结果。
    /// </summary>
    public static SpugSendResult ParseResponse(string? body, int httpStatus)
    {
        var raw = body ?? "";
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new SpugSendResult(-1, $"接口返回格式无法识别：{Shorten(raw)}", "", raw, httpStatus);

            var code = root.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var value) ? value : -1;
            var message = root.TryGetProperty("msg", out var msgElement) && msgElement.ValueKind == JsonValueKind.String
                ? msgElement.GetString() ?? ""
                : "";
            var requestId = root.TryGetProperty("request_id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString() ?? ""
                : "";

            if (code < 0 && message.Length == 0) message = $"接口返回格式无法识别：{Shorten(raw)}";
            return new SpugSendResult(code, message, requestId, raw, httpStatus);
        }
        catch (JsonException)
        {
            return new SpugSendResult(-1, $"接口返回不是合法 JSON（HTTP {httpStatus}）：{Shorten(raw)}", "", raw, httpStatus);
        }
    }

    /// <summary>日志里用的 URL：把 /sms/ 或 /mail/ 后面的模板编码打码（模板编码是调用凭证）。</summary>
    public static string MaskUrl(string? url)
    {
        var text = (url ?? "").Trim();
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return text;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var isChannelPath = segments[i].Equals("sms", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("mail", StringComparison.OrdinalIgnoreCase);
            if (!isChannelPath) continue;
            segments[i + 1] = MaskToken(segments[i + 1]);
            break;
        }

        var builder = new UriBuilder(uri) { Path = "/" + string.Join('/', segments) };
        return builder.Uri.ToString();
    }

    /// <summary>手机号打码（138****8000）。</summary>
    public static string MaskPhone(string? phone)
    {
        var text = (phone ?? "").Trim();
        return text.Length >= 7 ? $"{text[..3]}****{text[^4..]}" : "***";
    }

    /// <summary>邮箱打码（demo***@example.com）。</summary>
    public static string MaskEmail(string? email)
    {
        var text = (email ?? "").Trim();
        var at = text.IndexOf('@');
        if (at <= 0) return "***";
        var local = text[..at];
        var domain = text[(at + 1)..];
        var maskedLocal = local.Length <= 2 ? new string('*', local.Length) : local[..2] + "***";
        return $"{maskedLocal}@{domain}";
    }

    private static string MaskToken(string token) => token.Length <= 8
        ? new string('*', token.Length)
        : $"{token[..4]}***{token[^4..]}";

    private static string Shorten(string raw)
    {
        var text = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}
