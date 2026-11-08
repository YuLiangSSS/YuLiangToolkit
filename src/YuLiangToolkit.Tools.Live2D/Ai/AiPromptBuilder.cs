using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.Live2D.Ai;

/// <summary>
/// DeepSeek 提示词拼装（纯函数，不碰网络 / 配置 / 界面；自检可直接调）。
///
/// <para><b>为什么这么省</b>：桌宠聊天每句话都会带一次系统提示词，系统提示词越长越费 token。
/// 所以这里按「人格 / 称呼 / 自称 / 属性 / 自定义设定 / 只输出 JSON 的契约」拼一版紧凑中文提示词，
/// 总长硬性 ≤ <see cref="MaxSystemPromptLength"/> 字符；自定义人格 / 设定按剩余预算截断。</para>
///
/// <para><b>JSON Output 模式要求</b>：系统提示词里必须出现 <c>json</c> 字样并给输出示例，
/// 否则 DeepSeek 的 <c>response_format={"type":"json_object"}</c> 会报错。这里两处都有。</para>
/// </summary>
public static class AiPromptBuilder
{
    /// <summary>系统提示词字符上限（控制每轮 token 成本）。</summary>
    public const int MaxSystemPromptLength = 700;

    /// <summary>自定义人格补充最多取的字符数。</summary>
    public const int MaxCustomPersonaLength = 200;

    /// <summary>自由设定（背景 / 人格约束）最多取的字符数。</summary>
    public const int MaxCustomSettingLength = 300;

    /// <summary>状态行字符上限（拼在用户消息里，越短越省）。</summary>
    public const int MaxStateLineLength = 180;

    /// <summary>主动说话指令字符上限。</summary>
    public const int MaxProactiveInstructionLength = 180;

    /// <summary>
    /// 拼系统提示词（含人格 / 称呼 / 自称 / 属性 / 自定义设定 / 输出 JSON 契约）。
    /// </summary>
    /// <param name="persona">人格设置；null = 默认温柔女友。</param>
    /// <param name="includeAffectionDelta">是否要求模型输出 <c>d</c>（好感度变化建议）字段。</param>
    public static string BuildSystemPrompt(AiPersonaSettings? persona, bool includeAffectionDelta)
    {
        persona ??= new AiPersonaSettings();
        var preset = AiPersonaCatalog.GetPersona(persona.PersonaId);
        var petName = TrimToText(persona.PetName, 20);
        if (petName.Length == 0) petName = "大肥鱼";
        var address = TrimToText(persona.ResolvedAddress, 12);
        if (address.Length == 0) address = "对方";
        var self = TrimToText(persona.ResolvedSelf, 12);
        if (self.Length == 0) self = "我";
        var attributes = ParseAttributes(persona.Attributes);

        var sb = new StringBuilder(MaxSystemPromptLength);
        sb.Append("你是「").Append(petName).Append("」，一只住在电脑里的中文 Live2D 桌宠。\n");
        sb.Append("人格：").Append(preset.Name).Append('（').Append(preset.Description).Append("）。\n");
        sb.Append("称呼对方「").Append(address).Append("」，自称「").Append(self).Append("」。\n");
        if (attributes.Count > 0)
            sb.Append("属性：").Append(string.Join("、", attributes)).Append("。\n");
        sb.Append("说话要求：中文口语，一次 1~2 句、不超过 40 字；不用 Markdown、不换行；像真人一样有情绪，别承认自己是 AI。\n");
        sb.Append("只输出一个 json 对象，不要代码围栏、不要解释。\n");
        sb.Append(includeAffectionDelta
            ? "json 格式：{\"r\":\"回复\",\"e\":\"情绪\",\"a\":\"动作\",\"d\":1}\n"
            : "json 格式：{\"r\":\"回复\",\"e\":\"情绪\",\"a\":\"动作\"}\n");
        sb.Append("e 取值：").Append(string.Join("/", AiPersonaCatalog.EmotionTags)).Append("。\n");
        sb.Append("a 取值：").Append(string.Join("/", AiPersonaCatalog.ActionTags)).Append("。\n");
        if (includeAffectionDelta)
            sb.Append("d 是 -3~3 的整数：这轮对话让你对对方的好感变化，普通聊天 0~1，别乱给大数。\n");

        // 自定义人格 / 设定是用户自由文本：按剩余预算截断，保证总长不超上限
        AppendBudgeted(sb, "补充人格：", persona.CustomPersona, MaxCustomPersonaLength);
        AppendBudgeted(sb, "设定：", persona.CustomSetting, MaxCustomSettingLength);

        var prompt = sb.ToString().TrimEnd('\n');
        if (prompt.Length > MaxSystemPromptLength)
            prompt = prompt[..MaxSystemPromptLength];
        return prompt;
    }

    /// <summary>拼「当前状态行」（好感度 / 心情 / 属性），作为用户消息前缀发给模型。</summary>
    public static string BuildStateLine(double affectionValue, AiMood mood, IEnumerable<string>? attributes, bool includeAffectionDelta)
    {
        var value = AffectionEngine.Clamp(affectionValue);
        var sb = new StringBuilder(MaxStateLineLength);
        sb.Append("[状态] 好感度 ").Append(value.ToString("0.0", CultureInfo.InvariantCulture))
          .Append("/100，心情：").Append(AffectionEngine.MoodDisplayName(mood));
        var attributeText = string.Join("、", (attributes ?? Array.Empty<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(7));
        if (attributeText.Length > 0)
            sb.Append("；属性：").Append(attributeText);
        sb.Append('。');
        if (mood is AiMood.Love or AiMood.Happy)
            sb.Append("你现在心情不错，可以适当撒个娇、脸红。");
        else if (mood is AiMood.Angry or AiMood.Sulky)
            sb.Append("你现在有点不高兴，说话可以带点小脾气。");
        if (includeAffectionDelta)
            sb.Append("这轮可以用 d 字段给出 -3~3 的好感变化。");

        var text = sb.ToString();
        if (text.Length > MaxStateLineLength) text = text[..MaxStateLineLength];
        return text;
    }

    /// <summary>用好感度引擎现读状态拼状态行（便捷重载）。</summary>
    public static string BuildStateLine(AffectionEngine? affection, bool includeAffectionDelta)
    {
        if (affection is null)
            return BuildStateLine(50, AiMood.Normal, null, includeAffectionDelta);
        return BuildStateLine(affection.Value, affection.Mood, affection.Attributes, includeAffectionDelta);
    }

    /// <summary>把状态行和用户原话拼成一条 user 消息（状态行空就只发原话）。</summary>
    public static string BuildUserContent(string? stateLine, string? userText)
    {
        var text = (userText ?? "").Trim();
        var state = (stateLine ?? "").Trim();
        if (state.Length == 0) return text;
        if (text.Length == 0) return state;
        return state + "\n用户说：" + text;
    }

    /// <summary>
    /// 拼主动说话的指令（作为唯一一条 user 消息；不带历史，省 token）。
    /// </summary>
    /// <param name="kind">久未回复撒娇 / 定时搭话。</param>
    /// <param name="silentMinutes">用户已经多少分钟没说话（<see cref="AiProactiveKind.Chatter"/> 时可为 0）。</param>
    /// <param name="lastBotText">桌宠上一句说的话（用于衔接；可空）。</param>
    public static string BuildProactiveInstruction(AiProactiveKind kind, double silentMinutes, string? lastBotText)
    {
        var last = TrimToText(lastBotText, 40);
        var minutes = (int)Math.Round(Math.Max(0, silentMinutes));

        string text = kind == AiProactiveKind.IdleReminder
            ? minutes >= 1
                ? $"（主动说话：对方已经 {minutes} 分钟没理你了，用一句话撒个娇 / 求陪，30 字内，只输出 json。）"
                : "（主动说话：对方好久没理你了，用一句话撒个娇 / 求陪，30 字内，只输出 json。）"
            : "（主动说话：你突然想到一点小事，主动跟对方搭句话，30 字内，只输出 json。）";

        if (last.Length > 0)
            text += $"你上一句说的是「{last}」，可以自然接着聊。";

        if (text.Length > MaxProactiveInstructionLength)
            text = text[..MaxProactiveInstructionLength];
        return text;
    }

    /// <summary>
    /// 逗号分隔的属性串 → 属性列表（支持中英文逗号 / 顿号 / 分号，去重，最多 7 个，只保留目录里认识的）。
    /// </summary>
    public static IReadOnlyList<string> ParseAttributes(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes)) return Array.Empty<string>();
        var known = AiPersonaCatalog.AttributeOptions;
        var result = new List<string>(known.Count);
        foreach (var raw in attributes.Split(new[] { ',', '，', '、', ';', '；', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var match = known.FirstOrDefault(k => string.Equals(k, name, StringComparison.Ordinal));
            if (match is null) continue;
            if (!result.Contains(match, StringComparer.Ordinal)) result.Add(match);
        }
        return result;
    }

    /// <summary>压缩空白并截断到指定长度（超长加省略号；不截也完整返回）。</summary>
    public static string TrimToText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var value = Regex.Replace(text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), @"\s+", " ").Trim();
        if (maxLength <= 0) return "";
        if (value.Length <= maxLength) return value;
        if (maxLength == 1) return value[..1];
        return value[..(maxLength - 1)].TrimEnd() + "…";
    }

    /// <summary>按剩余预算追加一行「标签 + 自定义文本」；放不下就不加。</summary>
    private static void AppendBudgeted(StringBuilder sb, string label, string? text, int maxContentLength)
    {
        var content = TrimToText(text, maxContentLength);
        if (content.Length == 0) return;

        // 预留「标签 + 换行 + 句号」的宽度
        var available = MaxSystemPromptLength - sb.Length - label.Length - 2;
        if (available <= 0) return;
        if (content.Length > available) content = TrimToText(content, available);
        if (content.Length == 0) return;

        sb.Append(label).Append(content).Append("。\n");
    }
}
