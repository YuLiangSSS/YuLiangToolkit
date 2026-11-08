using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.Live2D.Ai;

/// <summary>
/// 模型返回内容 → <see cref="AiReply"/>（纯函数；自检可直接调）。
///
/// <para><b>要能接住的脏东西</b>：DeepSeek 有时会把 JSON 包进 ```json 代码围栏、
/// 前后带一句解释、字段名写成 <c>reply</c> / <c>emotion</c> / <c>action</c> / <c>delta</c>、
/// 情绪给个不认识的标签、<c>d</c> 给成 5 或 -9、正文里带 Markdown / 换行 / 超长。</para>
///
/// <para><b>兜底</b>：完全解析不出 JSON 时，把原文清洗后当 <see cref="AiReply.Text"/> 返回（不丢话）；
/// JSON 能解析但正文为空（JSON Output 模式下内容可能被替换成全空格）时返回空 <see cref="AiReply.Text"/>，
/// 由聊天服务决定用离线兜底。</para>
/// </summary>
public static class AiReplyParser
{
    /// <summary>回复正文最大字符数（超长截断，避免 TTS / 气泡塞爆）。</summary>
    public const int MaxTextLength = 200;

    /// <summary>模型给出的好感度变化范围（与 <see cref="AffectionEngine.MaxDeltaPerChat"/> 不完全相同：这里只夹模型建议值）。</summary>
    public const int MinModelDelta = -3;

    /// <summary>模型给出的好感度变化上限。</summary>
    public const int MaxModelDelta = 3;

    private static readonly Regex LinkRegex = new(@"\[([^\]]*)\]\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex ListPrefixRegex = new(@"(?m)^\s*[-*+]\s+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] MarkdownMarks = { "**", "__", "~~", "`", "###", "##", "#", ">" };

    /// <summary>解析模型 content（可附带 usage 里的 token 统计）。</summary>
    public static AiReply Parse(string? content, int promptTokens = 0, int completionTokens = 0)
    {
        var raw = content ?? "";
        var stripped = StripCodeFences(raw).Trim();
        if (stripped.Length > 0 && TryReadJsonObject(stripped, out var root, out var document))
        {
            using (document)
            {
                var text = CleanText(ReadText(root, "r", "reply", "text", "content", "message"), MaxTextLength);
                if (text.Length > 0)
                {
                    return new AiReply
                    {
                        Text = text,
                        Emotion = NormalizeEmotion(ReadText(root, "e", "emotion", "mood", "feeling")),
                        Action = NormalizeAction(ReadText(root, "a", "action", "act", "motion")),
                        AffectionDelta = ReadDelta(root),
                        PromptTokens = Math.Max(0, promptTokens),
                        CompletionTokens = Math.Max(0, completionTokens),
                        RawContent = raw,
                    };
                }

                // JSON 解析成功但正文为空：可能被内容过滤 / 全空格替换，交给服务层兜底
                return new AiReply
                {
                    Text = "",
                    PromptTokens = Math.Max(0, promptTokens),
                    CompletionTokens = Math.Max(0, completionTokens),
                    RawContent = raw,
                };
            }
        }

        // 不是 JSON：把原文清洗后当正文返回（模型偶尔不守格式）
        var fallback = CleanText(stripped.Length > 0 ? stripped : raw, MaxTextLength);
        return new AiReply
        {
            Text = fallback,
            PromptTokens = Math.Max(0, promptTokens),
            CompletionTokens = Math.Max(0, completionTokens),
            RawContent = raw,
        };
    }

    /// <summary>
    /// 文本清洗：去代码围栏 / Markdown 标记 / 链接语法 / 换行，压缩空白，去首尾引号，截断到 <paramref name="maxLength"/>。
    /// </summary>
    public static string CleanText(string? text, int maxLength = MaxTextLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var value = text.Replace("```json", " ", StringComparison.OrdinalIgnoreCase)
                        .Replace("```JSON", " ", StringComparison.Ordinal)
                        .Replace("```", " ");
        value = LinkRegex.Replace(value, "$1");
        value = ListPrefixRegex.Replace(value, "");
        foreach (var mark in MarkdownMarks)
            value = value.Replace(mark, "", StringComparison.Ordinal);
        value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        value = WhitespaceRegex.Replace(value, " ").Trim();
        // 只去包裹用的引号 / 括号，句末标点保留（TTS 停顿更自然）
        value = value.Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '『', '』', '《', '》', ' ').Trim();

        if (maxLength > 0 && value.Length > maxLength)
            value = maxLength == 1 ? value[..1] : value[..(maxLength - 1)].TrimEnd() + "…";
        return value;
    }

    /// <summary>情绪标签归一化：只认 <see cref="AiPersonaCatalog.EmotionTags"/>（大小写不敏感），未知返回空串。</summary>
    public static string NormalizeEmotion(string? tag)
        => NormalizeTag(tag, AiPersonaCatalog.EmotionTags);

    /// <summary>动作标签归一化：只认 <see cref="AiPersonaCatalog.ActionTags"/>（大小写不敏感），未知返回空串。</summary>
    public static string NormalizeAction(string? tag)
        => NormalizeTag(tag, AiPersonaCatalog.ActionTags);

    /// <summary>把模型给的 d（数字 / 数字字符串 / 空）夹到 -3 ~ +3，四舍五入成整数。</summary>
    public static int NormalizeDelta(object? value)
    {
        double number;
        switch (value)
        {
            case null:
                return 0;
            case int i:
                number = i;
                break;
            case long l:
                number = l;
                break;
            case double d:
                number = d;
                break;
            case float f:
                number = f;
                break;
            case decimal m:
                number = (double)m;
                break;
            case string s:
                if (!double.TryParse(s.Trim().TrimStart('+'), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
                    return 0;
                break;
            default:
                return 0;
        }

        if (double.IsNaN(number)) return 0;
        var rounded = (int)Math.Round(Math.Clamp(number, MinModelDelta, MaxModelDelta), MidpointRounding.AwayFromZero);
        return Math.Clamp(rounded, MinModelDelta, MaxModelDelta);
    }

    private static string NormalizeTag(string? tag, IReadOnlyList<string> known)
    {
        var value = (tag ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0) return "";
        // 容忍 "emotion:happy" / "action wave" 这类写法
        var sep = value.IndexOfAny(new[] { ':', '：', ' ', '-' });
        if (sep > 0) value = value[(sep + 1)..].Trim();
        foreach (var item in known)
        {
            if (string.Equals(item, value, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return "";
    }

    /// <summary>去代码围栏（逐行丢掉 ``` 开头的行，内容保留）。</summary>
    public static string StripCodeFences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) continue;
            builder.Append(line).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>在文本里找第一个能解析成 JSON 对象的片段。</summary>
    private static bool TryReadJsonObject(string text, out JsonElement root, out JsonDocument document)
    {
        root = default;
        document = null!;

        var start = text.IndexOf('{');
        if (start < 0) return false;
        var end = text.LastIndexOf('}');
        while (end > start)
        {
            try
            {
                var json = text[start..(end + 1)];
                var parsed = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip,
                });
                if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                {
                    root = parsed.RootElement;
                    document = parsed;
                    return true;
                }
                parsed.Dispose();
            }
            catch (JsonException)
            {
                // 可能是“前面那句解释里也有 {”：换更靠后的起始位置再试
                var next = text.IndexOf('{', start + 1);
                if (next < 0) break;
                start = next;
                continue;
            }

            end = text.LastIndexOf('}', end - 1);
        }

        return false;
    }

    /// <summary>按别名（大小写不敏感）读字符串字段。</summary>
    private static string? ReadText(JsonElement root, params string[] names)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    return property.Value.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return property.Value.GetRawText();
            }
        }
        return null;
    }

    /// <summary>按别名读好感度变化字段。</summary>
    private static int ReadDelta(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!new[] { "d", "delta", "affection", "affection_delta", "affectiondelta", "affectionchange" }
                    .Any(name => string.Equals(name, property.Name, StringComparison.OrdinalIgnoreCase)))
                continue;
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    return NormalizeDelta(property.Value.GetDouble());
                case JsonValueKind.String:
                    return NormalizeDelta(property.Value.GetString());
            }
        }
        return 0;
    }
}
