using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace YuLiangToolkit.Tools.Live2D.Ai;

/// <summary>
/// DeepSeek Chat Completions 客户端（手写 <see cref="HttpClient"/>，不引入任何第三方包）。
///
/// <para><b>接口事实</b>：<c>POST {baseUrl}/chat/completions</c>、<c>Authorization: Bearer &lt;key&gt;</c>、
/// 默认模型 <c>deepseek-flash</c>。思考开关用 <c>{"thinking":{"type":"enabled"|"disabled"}}</c>，
/// 打开时再带 <c>reasoning_effort</c>（low / high / max；medium 映射 high），且不发 <c>temperature</c>；
/// 关闭时发 <c>temperature: 0.9</c>。结构化输出用 <c>response_format={"type":"json_object"}</c>。</para>
///
/// <para><b>自检</b>：构造函数可注入 <see cref="HttpMessageHandler"/>（假 handler 返回固定 JSON，不发真请求）；
/// 纯逻辑拆成了 <see cref="BuildRequestBody"/>（请求体 JSON 字符串）与 <see cref="ParseResponse"/>（响应 → <see cref="AiReply"/>）。</para>
/// </summary>
public sealed class DeepSeekClient : IDisposable
{
    /// <summary>对话端点路径。</summary>
    public const string ChatCompletionsPath = "/chat/completions";

    /// <summary>普通请求超时（秒）。</summary>
    public const int NormalTimeoutSeconds = 30;

    /// <summary>思考模式请求超时（秒；思考更慢）。</summary>
    public const int ThinkingTimeoutSeconds = 60;

    /// <summary>关闭思考时使用的 temperature（DeepSeek 官方推荐偏活泼的 0.9~1.0）。</summary>
    public const double DefaultTemperature = 0.9;

    /// <summary>请求体里 max_tokens 的兜底值（配置默认 160，越小越省）。</summary>
    public const int DefaultMaxTokens = 160;

    private const int MaxErrorDetailLength = 160;
    private const int MaxRawContentLength = 500;

    private readonly HttpClient _http;
    private bool _disposed;

    /// <summary>创建客户端；<paramref name="handler"/> 留空走默认网络栈（自检可注入假 handler）。</summary>
    public DeepSeekClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false); // 注入的 handler 由调用方释放
        _http.Timeout = Timeout.InfiniteTimeSpan; // 超时按请求单独控制（思考 60s / 普通 30s）
    }

    /// <summary>整段请求体（自检断言字段用）。</summary>
    /// <param name="options">请求参数。</param>
    /// <param name="messages">消息列表（system + 历史 + 当前用户消息）。</param>
    /// <param name="maxTokensOverride">覆盖 max_tokens（主动说话 80 / 测试连接 16）。</param>
    public string BuildRequestBody(AiRequestOptions options, IReadOnlyList<AiChatTurn>? messages, int? maxTokensOverride = null)
    {
        options ??= new AiRequestOptions();
        var model = string.IsNullOrWhiteSpace(options.Model) ? AiRequestOptions.DefaultModel : options.Model.Trim();
        var maxTokens = Math.Clamp(maxTokensOverride ?? options.MaxTokens, 1, 4096);
        var thinkingEnabled = AiThinkingLevels.IsThinkingEnabled(options.Thinking);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   // 中文原样输出（不转义成 \uXXXX），body 更短、日志可读
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
               }))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);

            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            foreach (var turn in messages ?? Array.Empty<AiChatTurn>())
            {
                if (turn is null) continue;
                writer.WriteStartObject();
                writer.WriteString("role", NormalizeRole(turn.Role));
                writer.WriteString("content", turn.Text ?? "");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("max_tokens", maxTokens);
            writer.WriteBoolean("stream", false);

            // 思考开关：关闭 = {"type":"disabled"}；打开 = {"type":"enabled"} + reasoning_effort
            writer.WritePropertyName("thinking");
            writer.WriteStartObject();
            writer.WriteString("type", thinkingEnabled ? "enabled" : "disabled");
            writer.WriteEndObject();

            if (thinkingEnabled)
            {
                var effort = AiThinkingLevels.ToReasoningEffort(options.Thinking);
                if (effort.Length > 0) writer.WriteString("reasoning_effort", effort);
            }
            else
            {
                // 思考模式不支持 temperature（传了也无效），所以只在关闭时发
                writer.WriteNumber("temperature", DefaultTemperature);
            }

            if (options.JsonOutput)
            {
                writer.WritePropertyName("response_format");
                writer.WriteStartObject();
                writer.WriteString("type", "json_object");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>发一次对话请求；失败返回带中文 <see cref="AiReply.Error"/> 的兜底结果（不抛网络异常）。</summary>
    public Task<AiReply> ChatAsync(AiRequestOptions options, IReadOnlyList<AiChatTurn>? messages, CancellationToken cancellationToken = default)
        => ChatAsync(options, messages, null, cancellationToken);

    /// <summary>发一次对话请求（可覆盖 max_tokens）。</summary>
    public async Task<AiReply> ChatAsync(AiRequestOptions options, IReadOnlyList<AiChatTurn>? messages, int? maxTokensOverride, CancellationToken cancellationToken = default)
    {
        if (_disposed) return Failure("客户端已释放");
        options ??= new AiRequestOptions();
        if (!options.IsUsable) return Failure("还没填 DeepSeek API Key");

        var body = BuildRequestBody(options, messages, maxTokensOverride);
        return await SendCoreAsync(options, body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>解析服务端返回 JSON → <see cref="AiReply"/>（含 usage 里的 token 统计）。</summary>
    public AiReply ParseResponse(string? responseJson)
    {
        var json = responseJson ?? "";
        if (string.IsNullOrWhiteSpace(json))
            return Failure("服务返回了空内容");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException)
        {
            return Failure("服务返回的不是合法 JSON，无法解析", json);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failure("服务返回的 JSON 格式不对（不是对象）", json);

            // 个别情况下会以 200 + error 返回
            if (root.TryGetProperty("error", out var error))
            {
                var message = ReadErrorMessage(error);
                return Failure(string.IsNullOrWhiteSpace(message) ? "服务返回错误（未说明原因）" : message, json);
            }

            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
                return Failure("服务返回里没有 choices（可能被限流或过滤）", json);

            var first = choices[0];
            var content = first.TryGetProperty("message", out var messageElement)
                ? ReadContent(messageElement)
                : null;
            if (content is null && first.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                content = textElement.GetString();

            var promptTokens = 0;
            var completionTokens = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptValue))
                    promptTokens = Math.Max(0, promptValue);
                if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var completionValue))
                    completionTokens = Math.Max(0, completionValue);
            }

            var reply = AiReplyParser.Parse(content, promptTokens, completionTokens);
            if (string.IsNullOrWhiteSpace(reply.Text))
            {
                return new AiReply
                {
                    Text = "",
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    IsFallback = true,
                    RawContent = content ?? TrimRaw(json),
                    Error = promptTokens > 0
                        ? "模型没有返回可见内容（可能触发了内容过滤），请换个说法再试"
                        : "模型返回的内容是空的，请稍后再试",
                };
            }

            return reply;
        }
    }

    /// <summary>
    /// 测试连通性：发一条 max_tokens=16 的极短请求（关闭思考、不要 JSON 外壳），返回耗时与回复摘要。
    /// </summary>
    public async Task<AiConnectionTestResult> TestConnectionAsync(AiRequestOptions options, CancellationToken cancellationToken = default)
    {
        if (_disposed) return new AiConnectionTestResult { Ok = false, Message = "客户端已释放" };
        if (options is null || !options.IsUsable)
            return new AiConnectionTestResult { Ok = false, Message = "还没填 DeepSeek API Key：请先在设置里填写" };

        var testOptions = new AiRequestOptions
        {
            ApiKey = options.ApiKey,
            BaseUrl = options.BaseUrl,
            Model = options.Model,
            Thinking = AiThinkingLevel.Off, // 测试连接不求思考，快一点 / 省一点
            MaxTokens = 16,
            JsonOutput = false,             // 测试只要一句纯文本
        };

        var messages = new[]
        {
            new AiChatTurn { Role = "system", Text = "你是连通性测试助手，只回复指定的两个字，不要多说。" },
            new AiChatTurn { Role = "user", Text = "只回复两个字：正常" },
        };

        var watch = Stopwatch.StartNew();
        var reply = await ChatAsync(testOptions, messages, 16, cancellationToken).ConfigureAwait(false);
        watch.Stop();
        var latency = (int)watch.ElapsedMilliseconds;

        if (!string.IsNullOrWhiteSpace(reply.Error))
            return new AiConnectionTestResult { Ok = false, Message = "连接失败：" + reply.Error, LatencyMs = latency };

        var summary = AiReplyParser.CleanText(reply.Text, 30);
        var model = string.IsNullOrWhiteSpace(testOptions.Model) ? AiRequestOptions.DefaultModel : testOptions.Model.Trim();
        return new AiConnectionTestResult
        {
            Ok = true,
            Message = $"连接成功：模型 {model}，往返 {latency} 毫秒，回复「{summary}」",
            LatencyMs = latency,
        };
    }

    /// <summary>把 401 / 402 / 429 / 5xx 等 HTTP 错误翻译成中文提示（自检可直接调）。</summary>
    public static string MapHttpError(HttpStatusCode statusCode, string? responseBody = null)
    {
        var code = (int)statusCode;
        var detail = ReadErrorDetail(responseBody);
        var tail = string.IsNullOrWhiteSpace(detail) ? "" : "（" + detail + "）";
        return statusCode switch
        {
            HttpStatusCode.Unauthorized => "API Key 无效或已失效（401），请在设置里重新填写" + tail,
            HttpStatusCode.PaymentRequired => "DeepSeek 账户余额不足（402），充值后再试" + tail,
            HttpStatusCode.Forbidden => "没有访问权限（403），请检查 API Key 或模型名" + tail,
            HttpStatusCode.TooManyRequests => "请求太频繁（429），稍等一会儿再聊" + tail,
            HttpStatusCode.BadRequest => "请求参数有误（400）" + tail,
            HttpStatusCode.NotFound => "接口地址不对（404），请检查 BaseUrl 是不是 https://api.deepseek.com" + tail,
            _ when code >= 500 => $"DeepSeek 服务繁忙（{code}），稍后再试" + tail,
            _ => $"请求失败（{code}）" + tail,
        };
    }

    /// <summary>去掉末尾斜杠；空值回默认；没写协议时自动补 https。</summary>
    public static string NormalizeBaseUrl(string? baseUrl)
    {
        var value = (baseUrl ?? "").Trim();
        if (value.Length == 0) value = AiRequestOptions.DefaultBaseUrl;
        value = value.TrimEnd('/');
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;
        return value;
    }

    /// <summary>释放 HttpClient。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _http.Dispose(); } catch { /* 退出流程忽略 */ }
    }

    // ---------- 内部实现 ----------

    /// <summary>真正的 HTTP 往返：超时控制 + 网络类失败重试一次。</summary>
    private async Task<AiReply> SendCoreAsync(AiRequestOptions options, string body, CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(
            AiThinkingLevels.IsThinkingEnabled(options.Thinking) ? ThinkingTimeoutSeconds : NormalTimeoutSeconds);
        var requestUri = NormalizeBaseUrl(options.BaseUrl) + ChatCompletionsPath;
        var key = options.ApiKey.Trim();

        Exception? lastError = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (attempt > 0)
            {
                // 网络抖动 / 服务端 5xx：等一小会儿再重试一次
                try { await Task.Delay(900, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeout);

                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                    .ConfigureAwait(false);
                var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // HTTP 状态错误按中文提示返回；网络类失败（超时 / 连接断开）才重试，业务错误不重试
                    var error = MapHttpError(response.StatusCode, responseText);
                    return new AiReply { IsFallback = true, Error = error, RawContent = TrimRaw(responseText) };
                }

                return ParseResponse(responseText);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // 调用方主动取消（含 Dispose）：交给上层处理
            }
            catch (OperationCanceledException)
            {
                lastError = new TimeoutException($"请求超时（{timeout.TotalSeconds:0} 秒没收到响应），网络可能不稳定");
            }
            catch (HttpRequestException ex)
            {
                lastError = new HttpRequestException("网络不通（HTTP 请求失败）：" + ex.Message, ex);
            }
            catch (IOException ex)
            {
                lastError = new IOException("网络不通（连接被中断）：" + ex.Message, ex);
            }
        }

        return Failure(lastError?.Message ?? "网络请求失败");
    }

    private static AiReply Failure(string error, string? rawContent = null) => new()
    {
        Text = "",
        IsFallback = true,
        Error = error,
        RawContent = rawContent is null ? null : TrimRaw(rawContent),
    };

    private static string NormalizeRole(string? role)
    {
        var value = (role ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "system" => "system",
            "assistant" => "assistant",
            "user" => "user",
            _ => "user",
        };
    }

    /// <summary>从 message 里读 content（字符串；个别兼容层会给 [{type,text}] 数组）。</summary>
    private static string? ReadContent(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (!message.TryGetProperty("content", out var content)) return null;
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString();
            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(part.GetString());
                    }
                    else if (part.ValueKind == JsonValueKind.Object &&
                             part.TryGetProperty("text", out var text) &&
                             text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }
                return builder.ToString();
            default:
                return null;
        }
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? "";
        if (error.ValueKind == JsonValueKind.Object &&
            error.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.String)
            return message.GetString() ?? "";
        return "";
    }

    /// <summary>从错误响应体里抠 <c>error.message</c>；没有就返回压缩后的原文摘要。</summary>
    private static string ReadErrorDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "";
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var error))
            {
                var message = ReadErrorMessage(error);
                if (message.Length > 0)
                    return AiPromptBuilder.TrimToText(message, MaxErrorDetailLength);
            }
        }
        catch (JsonException)
        {
            // 不是 JSON：下面按原文摘要处理
        }

        return AiPromptBuilder.TrimToText(body, MaxErrorDetailLength);
    }

    private static string TrimRaw(string? text)
        => text is null ? "" : (text.Length <= MaxRawContentLength ? text : text[..MaxRawContentLength] + "…");
}
