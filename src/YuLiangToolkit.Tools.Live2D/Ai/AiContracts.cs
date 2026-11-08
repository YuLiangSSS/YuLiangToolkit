using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Live2D.Ai;

/// <summary>
/// DeepSeek 思考强度：<see cref="Off"/> = 关闭思考（最省 token，桌宠聊天默认），
/// <see cref="Low"/> / <see cref="Medium"/> / <see cref="High"/> = 打开思考并分别映射到
/// <c>reasoning_effort</c> 的 <c>low</c> / <c>high</c> / <c>max</c>。
///
/// <para>接口事实（DeepSeek 官方文档）：关闭用 <c>{"thinking":{"type":"disabled"}}</c>；
/// 打开用 <c>{"thinking":{"type":"enabled"}}</c> + <c>reasoning_effort</c>；
/// 思考模式不支持 <c>temperature</c>（传了也不报错但无效）。</para>
/// </summary>
public enum AiThinkingLevel
{
    /// <summary>关闭思考：最快、最省 token。</summary>
    Off = 0,

    /// <summary>低强度思考。</summary>
    Low = 1,

    /// <summary>中强度思考。</summary>
    Medium = 2,

    /// <summary>高强度思考（最费 token）。</summary>
    High = 3,
}

/// <summary>思考强度的配置字符串 / 界面文案 / 请求参数映射（纯静态，自检可测）。</summary>
public static class AiThinkingLevels
{
    /// <summary>界面下拉里的显示文案（顺序与 <see cref="AiThinkingLevel"/> 一致）。</summary>
    public static IReadOnlyList<string> DisplayNames { get; } = new[]
    {
        "关闭（最省 token）", "低", "中", "高",
    };

    /// <summary>配置里存的中文值（<c>AiThinkingLevel</c>）。</summary>
    public static string ToConfig(AiThinkingLevel level) => level switch
    {
        AiThinkingLevel.Low => "低",
        AiThinkingLevel.Medium => "中",
        AiThinkingLevel.High => "高",
        _ => "关闭",
    };

    /// <summary>从配置字符串解析（无法识别 = 关闭）。</summary>
    public static AiThinkingLevel FromConfig(string? text) => (text ?? "").Trim() switch
    {
        "低" or "low" or "Low" => AiThinkingLevel.Low,
        "中" or "medium" or "Medium" => AiThinkingLevel.Medium,
        "高" or "high" or "High" or "max" => AiThinkingLevel.High,
        _ => AiThinkingLevel.Off,
    };

    /// <summary>是否打开思考模式。</summary>
    public static bool IsThinkingEnabled(AiThinkingLevel level) => level != AiThinkingLevel.Off;

    /// <summary>请求体里的 <c>reasoning_effort</c>（关闭思考时返回空串 = 不发这个字段）。</summary>
    public static string ToReasoningEffort(AiThinkingLevel level) => level switch
    {
        AiThinkingLevel.Low => "low",
        AiThinkingLevel.Medium => "high",
        AiThinkingLevel.High => "max",
        _ => "",
    };

    /// <summary>把 <see cref="DisplayNames"/> 的下标转成枚举（越界 = 关闭）。</summary>
    public static AiThinkingLevel FromDisplayIndex(int index) => index switch
    {
        1 => AiThinkingLevel.Low,
        2 => AiThinkingLevel.Medium,
        3 => AiThinkingLevel.High,
        _ => AiThinkingLevel.Off,
    };

    /// <summary>枚举 → <see cref="DisplayNames"/> 下标。</summary>
    public static int ToDisplayIndex(AiThinkingLevel level) => (int)level;
}

/// <summary>一轮对话（发给 API 的 role / content；role 只有 user / assistant / system）。</summary>
public sealed class AiChatTurn
{
    /// <summary>角色：<c>system</c> / <c>user</c> / <c>assistant</c>。</summary>
    public string Role { get; init; } = "user";

    /// <summary>内容（纯文本；不传 <c>reasoning_content</c>）。</summary>
    public string Text { get; init; } = "";

    /// <summary>是不是用户发的。</summary>
    public bool IsUser => string.Equals(Role, "user", StringComparison.Ordinal);
}

/// <summary>
/// 一次 AI 回复（已解析掉 JSON 外壳；<see cref="Text"/> 就是要显示 / 朗读的那句）。
/// </summary>
public sealed class AiReply
{
    /// <summary>回复正文（1~2 句，已去掉标签 / Markdown / 代码围栏）。</summary>
    public string Text { get; init; } = "";

    /// <summary>情绪标签（<c>shy / happy / love / angry / jealous / sad / cold / surprised / neutral</c>；可能为空）。</summary>
    public string Emotion { get; init; } = "";

    /// <summary>动作标签（<c>wave / heart / nod / akimbo / head_hold / point / grass / none</c>；可能为空）。</summary>
    public string Action { get; init; } = "";

    /// <summary>模型给出的好感度变化建议（-3 ~ +3；未启用 / 解析不到时 0）。</summary>
    public int AffectionDelta { get; init; }

    /// <summary>本次请求的 prompt token 数（服务端返回；未知 = 0）。</summary>
    public int PromptTokens { get; init; }

    /// <summary>本次请求的 completion token 数（服务端返回；未知 = 0）。</summary>
    public int CompletionTokens { get; init; }

    /// <summary>是否离线兜底 / 出错兜底（界面可据此提示）。</summary>
    public bool IsFallback { get; init; }

    /// <summary>原始返回内容（排错用，可能很长；界面不显示）。</summary>
    public string? RawContent { get; init; }

    /// <summary>失败原因（成功为空）。</summary>
    public string? Error { get; init; }
}

/// <summary>聊天窗里的一条消息（用户 / 桌宠 / 系统提示），同时用于落盘。</summary>
public sealed class AiChatMessage
{
    /// <summary>Unix 秒。</summary>
    public long TimestampUnix { get; set; }

    /// <summary>角色：<c>user</c> / <c>assistant</c> / <c>notice</c>（系统提示 / 错误，不发给模型）。</summary>
    public string Role { get; set; } = "user";

    /// <summary>正文。</summary>
    public string Text { get; set; } = "";

    /// <summary>情绪标签（桌宠消息才有，用于聊天窗气泡配色 / 表情）。</summary>
    public string Emotion { get; set; } = "";

    /// <summary>本地时间 HH:mm。</summary>
    public string TimeText =>
        DateTimeOffset.FromUnixTimeSeconds(TimestampUnix).ToLocalTime().ToString("HH:mm");

    /// <summary>是不是用户消息（WPF 绑定 / 气泡左右对齐用）。</summary>
    public bool IsUser => string.Equals(Role, "user", StringComparison.Ordinal);

    /// <summary>是不是系统提示（居中灰字）。</summary>
    public bool IsNotice => string.Equals(Role, "notice", StringComparison.Ordinal);

    /// <summary>是不是桌宠消息。</summary>
    public bool IsBot => string.Equals(Role, "assistant", StringComparison.Ordinal);
}

/// <summary>一次 API 请求的参数快照（从 <c>Live2DConfig</c> 现读，改配置立刻生效）。</summary>
public sealed class AiRequestOptions
{
    /// <summary>DeepSeek API Key（明文；落盘由 <see cref="AiSecretStore"/> 用 DPAPI 加密）。</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>接口根地址（默认 <c>https://api.deepseek.com</c>，末尾斜杠会被去掉）。</summary>
    public string BaseUrl { get; init; } = DefaultBaseUrl;

    /// <summary>模型名（默认 <c>deepseek-flash</c>；也可填 <c>deepseek-v4-pro</c>）。</summary>
    public string Model { get; init; } = DefaultModel;

    /// <summary>思考强度。</summary>
    public AiThinkingLevel Thinking { get; init; } = AiThinkingLevel.Off;

    /// <summary>单次回复最大 token（40~1000，默认 160；越小越省）。</summary>
    public int MaxTokens { get; init; } = 160;

    /// <summary>是否要求模型输出 JSON（结构化情绪 / 动作 / 好感度；默认 true）。</summary>
    public bool JsonOutput { get; init; } = true;

    /// <summary>是否可用（有 Key）。</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>默认接口根地址。</summary>
    public const string DefaultBaseUrl = "https://api.deepseek.com";

    /// <summary>默认模型。</summary>
    public const string DefaultModel = "deepseek-flash";
}

/// <summary>人格 / 称呼 / 属性设置快照（从配置现读；给 <see cref="AiPromptBuilder"/> 用）。</summary>
public sealed class AiPersonaSettings
{
    /// <summary>人格预设 Id（见 <see cref="AiPersonaCatalog.Personas"/>；空 = 温柔女友）。</summary>
    public string PersonaId { get; init; } = "gentle";

    /// <summary>自定义人格补充（可选，最多取前 200 字）。</summary>
    public string CustomPersona { get; init; } = "";

    /// <summary>用户称呼预设（用户 / 老公 / 主人）。</summary>
    public string AddressPreset { get; init; } = "主人";

    /// <summary>自定义用户称呼（非空时优先于预设）。</summary>
    public string AddressCustom { get; init; } = "";

    /// <summary>自称预设（我 / 本小姐 / 本姑娘 / 本鱼）。</summary>
    public string SelfPreset { get; init; } = "我";

    /// <summary>自定义自称（非空时优先于预设）。</summary>
    public string SelfCustom { get; init; } = "";

    /// <summary>属性（逗号分隔，见 <see cref="AiPersonaCatalog.AttributeOptions"/>）。</summary>
    public string Attributes { get; init; } = "";

    /// <summary>自由「自我设定 / 背景 / 人格约束」（可选，最多取前 300 字）。</summary>
    public string CustomSetting { get; init; } = "";

    /// <summary>桌宠名字（默认 大肥鱼）。</summary>
    public string PetName { get; init; } = "大肥鱼";

    /// <summary>实际称呼（自定义优先）。</summary>
    public string ResolvedAddress => string.IsNullOrWhiteSpace(AddressCustom) ? AddressPreset : AddressCustom.Trim();

    /// <summary>实际自称（自定义优先）。</summary>
    public string ResolvedSelf => string.IsNullOrWhiteSpace(SelfCustom) ? SelfPreset : SelfCustom.Trim();
}

/// <summary>人格预设（Id / 名字 / 一句说明）。</summary>
public sealed record AiPersonaPreset(string Id, string Name, string Description);

/// <summary>人格 / 称呼 / 属性 / 情绪与动作标签的目录（界面下拉 + 提示词生成共用；纯数据）。</summary>
public static class AiPersonaCatalog
{
    /// <summary>4 个人格预设（Id → 提示词里的性格描述）。</summary>
    public static IReadOnlyList<AiPersonaPreset> Personas { get; } = new[]
    {
        new AiPersonaPreset("gentle", "温柔女友", "温柔体贴、会照顾人、说话软软的"),
        new AiPersonaPreset("tsundere", "傲娇", "嘴硬心软、容易害羞脸红、被夸会嘴硬但心里开心"),
        new AiPersonaPreset("yandere", "病娇", "占有欲强、爱吃醋、提到别人会黑脸闹别扭，但不会伤害主人"),
        new AiPersonaPreset("genki", "元气妹妹", "活泼话多、爱撒娇、情绪外放"),
    };

    /// <summary>用户称呼预设。</summary>
    public static IReadOnlyList<string> AddressPresets { get; } = new[] { "用户", "老公", "主人" };

    /// <summary>桌宠自称预设。</summary>
    public static IReadOnlyList<string> SelfPresets { get; } = new[] { "我", "本小姐", "本姑娘", "本鱼" };

    /// <summary>可勾选的属性（逗号拼进 <see cref="AiPersonaSettings.Attributes"/>）。</summary>
    public static IReadOnlyList<string> AttributeOptions { get; } = new[]
    {
        "容易害羞", "爱吃醋", "黏人", "爱撒娇", "傲娇口癖", "容易生气", "爱夸奖",
    };

    /// <summary>情绪标签（模型输出 <c>e</c> 字段的合法值）。</summary>
    public static IReadOnlyList<string> EmotionTags { get; } = new[]
    {
        "shy", "happy", "love", "angry", "jealous", "sad", "cold", "surprised", "neutral",
    };

    /// <summary>动作标签（模型输出 <c>a</c> 字段的合法值）。</summary>
    public static IReadOnlyList<string> ActionTags { get; } = new[]
    {
        "wave", "heart", "nod", "akimbo", "head_hold", "point", "grass", "none",
    };

    /// <summary>按 Id 取人格名（未知 → 温柔女友）。</summary>
    public static string GetPersonaName(string? id)
        => Personas.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name
           ?? Personas[0].Name;

    /// <summary>按 Id 取人格说明（未知 → 温柔女友）。</summary>
    public static AiPersonaPreset GetPersona(string? id)
        => Personas.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
           ?? Personas[0];
}

/// <summary>好感度情绪档位（由好感度数值决定；聊天时的高兴 / 黑脸表现靠它）。</summary>
public enum AiMood
{
    /// <summary>生气（&lt; 30）：容易黑脸、说话带刺。</summary>
    Angry = 0,

    /// <summary>不高兴 / 闹别扭（30 ~ 49）：气鼓鼓、爱答不理。</summary>
    Sulky = 1,

    /// <summary>平常（50 ~ 69）。</summary>
    Normal = 2,

    /// <summary>开心（70 ~ 84）：容易笑、被夸会害羞。</summary>
    Happy = 3,

    /// <summary>心动（&ge; 85）：爱冒爱心眼、容易脸红。</summary>
    Love = 4,
}

/// <summary>
/// 好感度自然模式的规则快照（从配置现读）。手动模式（<see cref="NaturalMode"/> = false）下
/// 数值只由用户自己拖，聊天 / 时间都不会改它。
/// </summary>
public sealed class AffectionSettings
{
    /// <summary>自然模式：聊得多涨、长时间不聊掉。</summary>
    public bool NaturalMode { get; init; } = true;

    /// <summary>自然模式下是否启用兜底（好感度最低降到 <see cref="Floor"/>）。</summary>
    public bool FloorEnabled { get; init; } = true;

    /// <summary>兜底数值（0 ~ 100，默认 20）。</summary>
    public double Floor { get; init; } = 20;

    /// <summary>每聊一句涨多少（0.2 ~ 3，默认 1.0；每轮上限 ±4）。</summary>
    public double GainPerChat { get; init; } = 1.0;

    /// <summary>每「一个不聊周期」掉多少（0 ~ 20，默认 3）。</summary>
    public double DecayPerDay { get; init; } = 3.0;

    /// <summary>多久不聊算一个掉值周期（小时，1 ~ 240，默认 24）。</summary>
    public double DecayAfterHours { get; init; } = 24;

    /// <summary>是否让模型的 <c>d</c> 字段（-3 ~ +3）参与好感度变化。</summary>
    public bool UseModelDelta { get; init; } = true;
}

/// <summary>好感度持久化状态（落盘到 <c>Data\live2d\ai\chat-state.json</c>）。</summary>
public sealed class AffectionState
{
    /// <summary>当前好感度 0 ~ 100。</summary>
    public double Value { get; set; } = 50;

    /// <summary>最近一次用户发言的 Unix 秒（0 = 还没聊过）。</summary>
    public long LastChatUnix { get; set; }

    /// <summary>最近一次结算掉值的 Unix 秒（0 = 还没结算过）。</summary>
    public long LastDecayUnix { get; set; }

    /// <summary>当天（本地日期 yyyy-MM-dd）已通过聊天涨了多少。</summary>
    public double TodayGain { get; set; }

    /// <summary>当天日期键（yyyy-MM-dd）。</summary>
    public string TodayKey { get; set; } = "";

    /// <summary>复制一份（防外部改到引擎内部状态）。</summary>
    public AffectionState Clone() => new()
    {
        Value = Value,
        LastChatUnix = LastChatUnix,
        LastDecayUnix = LastDecayUnix,
        TodayGain = TodayGain,
        TodayKey = TodayKey,
    };
}

/// <summary>
/// 好感度引擎：自然模式下的涨 / 掉 / 兜底 / 每日上限，以及数值 → 情绪档位。
///
/// <para><b>规则（自检断言就按这个）</b>：</para>
/// <list type="number">
///   <item>聊天涨：每轮 = <see cref="AffectionSettings.GainPerChat"/>（+ 模型 d 值，若启用），单轮夹在 -4 ~ +4；
///         每天（本地日期）通过聊天最多涨 15（<c>DailyGainCap</c> 常量）；用户发言与桌宠回复各记一轮？<b>不</b>：
///         一轮「用户发言 → 桌宠回复」只在用户发言时结算一次，回复不重复加。</item>
///   <item>掉值：距 <see cref="AffectionState.LastChatUnix"/> 每满 <see cref="AffectionSettings.DecayAfterHours"/>
///         小时掉 <see cref="AffectionSettings.DecayPerDay"/>；只按「已经过去的完整周期」结算（一次可能补多天）。</item>
///   <item>兜底：<see cref="AffectionSettings.FloorEnabled"/> 时任何自动变化都不能让值低于
///         <see cref="AffectionSettings.Floor"/>；手动 <see cref="SetValue"/> 不受兜底限制（用户想设 0 就 0）。</item>
///   <item>手动模式：聊天 / 时间都不改值（<see cref="ApplyChat"/> / <see cref="ApplyDecay"/> 返回 0）。</item>
///   <item>区间：0 ~ 100，四舍五入到 1 位小数。</item>
/// </list>
/// </summary>
public sealed class AffectionEngine
{
    /// <summary>每天聊天最多涨的好感度（自然模式防刷）。</summary>
    public const double DailyGainCap = 15.0;

    /// <summary>单轮好感度变化上限（防止模型给出离谱数值）。</summary>
    public const double MaxDeltaPerChat = 4.0;

    /// <summary>最低 / 最高好感度。</summary>
    public const double MinValue = 0.0;

    /// <summary>最高好感度。</summary>
    public const double MaxValue = 100.0;

    /// <summary>创建引擎（<paramref name="state"/> 为 null 时用默认 50 起）。</summary>
    public AffectionEngine(AffectionState? state = null)
    {
        State = state ?? new AffectionState();
        State.Value = Clamp(State.Value);
    }

    /// <summary>持久化状态（服务层负责落盘）。</summary>
    public AffectionState State { get; }

    /// <summary>当前设置（用户改配置时由服务层覆盖）。</summary>
    public AffectionSettings Settings { get; set; } = new();

    /// <summary>人格属性（由服务层按 <see cref="AiPersonaSettings.Attributes"/> 填；影响脸红概率）。</summary>
    public IReadOnlyCollection<string> Attributes { get; set; } = Array.Empty<string>();

    /// <summary>当前好感度。</summary>
    public double Value => State.Value;

    /// <summary>当前情绪档位（数值决定）。</summary>
    public AiMood Mood => ResolveMood(Value);

    /// <summary>情绪档位的中文文案（界面显示）。</summary>
    public string MoodText => MoodDisplayName(Mood);

    /// <summary>好感度数值 → 情绪档位：&lt;30 生气 / 30~49 不高兴 / 50~69 平常 / 70~84 开心 / &ge;85 心动。</summary>
    public static AiMood ResolveMood(double value) => value switch
    {
        < 30 => AiMood.Angry,
        < 50 => AiMood.Sulky,
        < 70 => AiMood.Normal,
        < 85 => AiMood.Happy,
        _ => AiMood.Love,
    };

    /// <summary>情绪档位的中文名。</summary>
    public static string MoodDisplayName(AiMood mood) => mood switch
    {
        AiMood.Angry => "生气",
        AiMood.Sulky => "闹别扭",
        AiMood.Happy => "开心",
        AiMood.Love => "心动",
        _ => "平常",
    };

    /// <summary>把任意值夹到 0 ~ 100 并保留 1 位小数。</summary>
    public static double Clamp(double value)
    {
        if (double.IsNaN(value)) return MinValue;
        return Math.Round(Math.Clamp(value, MinValue, MaxValue), 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>手动设置（用户自己拖；不受兜底限制）。</summary>
    public void SetValue(double value) => State.Value = Clamp(value);

    /// <summary>应用设置；若当前低于兜底值且自然模式 + 兜底开启，把值抬到兜底值。</summary>
    public void ApplySettings(AffectionSettings settings)
    {
        Settings = settings ?? new AffectionSettings();
        if (Settings.NaturalMode && Settings.FloorEnabled && State.Value < Settings.Floor)
            State.Value = Clamp(Settings.Floor);
    }

    /// <summary>
    /// 记一轮聊天（用户发言时调用一次）：返回实际好感度变化（可能为 0）。
    /// 手动模式返回 0；自然模式下 = GainPerChat +（启用了模型 d 值时的 d），夹 -4 ~ +4、受每日上限与兜底约束。
    /// </summary>
    public int ApplyChat(double modelDelta, DateTimeOffset now)
    {
        if (!Settings.NaturalMode) return 0;

        var dayKey = now.ToLocalTime().ToString("yyyy-MM-dd");
        if (!string.Equals(State.TodayKey, dayKey, StringComparison.Ordinal))
        {
            State.TodayKey = dayKey;
            State.TodayGain = 0;
        }

        var delta = Settings.GainPerChat;
        if (Settings.UseModelDelta && modelDelta != 0)
            delta += Math.Clamp(modelDelta, -3, 3);
        delta = Math.Clamp(delta, -MaxDeltaPerChat, MaxDeltaPerChat);

        if (delta > 0)
        {
            var remaining = Math.Max(0, DailyGainCap - State.TodayGain);
            delta = Math.Min(delta, remaining);
            State.TodayGain += delta;
        }

        var before = State.Value;
        State.Value = Clamp(State.Value + delta);
        if (Settings.FloorEnabled && State.Value < Settings.Floor)
            State.Value = Clamp(Settings.Floor);
        State.LastChatUnix = now.ToUnixTimeSeconds();
        return (int)Math.Round(State.Value - before);
    }

    /// <summary>
    /// 结算「长时间不聊」的掉值：返回掉了多少（正数）。只掉完整周期，一次补多天；
    /// 手动模式 / 还没聊过 / 设置不为正时返回 0。
    /// </summary>
    public int ApplyDecay(DateTimeOffset now)
    {
        if (!Settings.NaturalMode || Settings.DecayPerDay <= 0 || Settings.DecayAfterHours <= 0) return 0;
        if (State.LastChatUnix <= 0) return 0;

        // 结算基点 = 最近一次聊天 / 最近一次已结算掉值里更晚的那个：
        // TickSchedule 每秒都会调这里，不这样会反复掉同一个周期。
        var baseline = Math.Max(State.LastChatUnix, State.LastDecayUnix);
        var periodSeconds = Settings.DecayAfterHours * 3600.0;
        var since = now.ToUnixTimeSeconds() - baseline;
        if (since < periodSeconds) return 0;

        // 只掉「已经过去的完整周期」，一次可补多天
        var periods = (int)Math.Floor(since / periodSeconds);
        if (periods <= 0) return 0;

        var before = State.Value;
        var loss = periods * Settings.DecayPerDay;
        var next = State.Value - loss;
        if (Settings.FloorEnabled && next < Settings.Floor) next = Settings.Floor;
        State.Value = Clamp(next);

        // 记住已经结算到什么时候，避免同一段不聊时间被重复扣
        State.LastDecayUnix = now.ToUnixTimeSeconds();
        return (int)Math.Round(before - State.Value);
    }

    /// <summary>好感度高时是否容易脸红 / 冒爱心眼（<paramref name="random01"/> 传 0~1 随机数，自检可注入）。</summary>
    public bool IsBlushLikely(double random01)
    {
        if (Value < 65) return false;
        var chance = Math.Clamp((Value - 55) / 90.0, 0, 0.9);
        if (HasAttribute("容易害羞")) chance = Math.Min(1, chance + 0.2);
        return random01 < chance;
    }

    /// <summary>当前人格是否带某个属性（属性列表由服务层填）。</summary>
    public bool HasAttribute(string attribute)
        => Attributes.Any(a => string.Equals(a, attribute, StringComparison.Ordinal));
}

/// <summary>主动说话的触发原因。</summary>
public enum AiProactiveKind
{
    /// <summary>用户很久没回：说一句 / 撒个娇求陪。</summary>
    IdleReminder = 0,

    /// <summary>普通主动搭话（按设定间隔）。</summary>
    Chatter = 1,
}

/// <summary>分享给会话的回复事件参数（会话据此说 TTS + 弹气泡 + 换表情动作）。</summary>
public sealed class AiReplyEventArgs : EventArgs
{
    /// <summary>回复内容。</summary>
    public required AiReply Reply { get; init; }

    /// <summary>是否要真的「说」出来（主动说话 / 普通回复都为 true；测试连接类不触发）。</summary>
    public bool Speak { get; init; } = true;

    /// <summary>触发原因（普通回复 / 主动搭话 / 久未回复）。</summary>
    public AiProactiveKind? Proactive { get; init; }
}

/// <summary>测试连通性的结果。</summary>
public sealed class AiConnectionTestResult
{
    /// <summary>是否成功。</summary>
    public bool Ok { get; init; }

    /// <summary>界面文案（成功：模型 / 耗时 / 回复摘要；失败：中文原因）。</summary>
    public string Message { get; init; } = "";

    /// <summary>往返耗时（毫秒）。</summary>
    public int LatencyMs { get; init; }
}

/// <summary>构造 <see cref="AiChatService"/> 需要的回调（配置现读 + 日志 + 离线兜底）。</summary>
public sealed class AiChatServiceOptions
{
    /// <summary>数据落盘目录（<c>Data\live2d\</c>，内部用 <c>ai\chat-state.json</c>）。</summary>
    public ToolStorage? Storage { get; init; }

    /// <summary>现读请求参数（API Key / 模型 / 思考强度 …）。</summary>
    public Func<AiRequestOptions>? RequestOptions { get; init; }

    /// <summary>现读人格设置。</summary>
    public Func<AiPersonaSettings>? Persona { get; init; }

    /// <summary>现读好感度设置。</summary>
    public Func<AffectionSettings>? Affection { get; init; }

    /// <summary>现读「聊天历史保留轮数」（0 = 只带当前这句）。</summary>
    public Func<int>? HistoryTurns { get; init; }

    /// <summary>现读主动聊天设置。</summary>
    public Func<AiProactiveSettings>? Proactive { get; init; }

    /// <summary>离线兜底回复（没填 API Key / 网络失败时用；参数 = 用户原话）。</summary>
    public Func<string, string>? OfflineFallback { get; init; }

    /// <summary>日志（工具页日志 / 会话日志）。</summary>
    public Action<string>? Log { get; init; }

    /// <summary>可选：注入 HttpClient（自检用假 handler；正常运行留空）。</summary>
    public System.Net.Http.HttpMessageHandler? HttpHandler { get; init; }
}

/// <summary>主动聊天的设置快照（从配置现读）。</summary>
public sealed class AiProactiveSettings
{
    /// <summary>是否允许桌宠主动搭话。</summary>
    public bool Enabled { get; init; }

    /// <summary>主动搭话间隔（分钟，默认 20，夹 3 ~ 360）。</summary>
    public double IntervalMinutes { get; init; } = 20;

    /// <summary>用户多久没回就撒娇提醒（分钟，默认 10，夹 1 ~ 720）。</summary>
    public double IdleSilenceMinutes { get; init; } = 10;

    /// <summary>是否启用「久未回复」提醒。</summary>
    public bool IdleReminderEnabled { get; init; } = true;
}

/// <summary>
/// 桌宠 AI 聊天服务：拼提示词 → 调 DeepSeek → 解析回复 → 更新好感度 / 历史 / token 统计，
/// 并把结果通过 <see cref="ReplyReady"/> / <see cref="MessageAdded"/> 交给界面层。
///
/// <para><b>线程</b>：构造与公开方法都应在 UI 线程调用；内部 await 后回到构造时的
/// <see cref="SynchronizationContext"/>（没有就回线程池），事件也在该上下文触发。</para>
///
/// <para><b>隐私</b>：只把「人格提示词 + 最近若干轮对话 + 好感度状态行」发给 DeepSeek；
/// API Key 只在内存里，落盘是 DPAPI 密文（见 <see cref="AiSecretStore"/>）。</para>
/// </summary>
public sealed partial class AiChatService : INotifyPropertyChanged, IDisposable
{
    // 本文件只保留对外契约（类型 / 公开成员签名与注释）；构造、字段与具体实现
    // 拆在 Ai/AiChatService.cs（同一个 partial 类），避免契约文件被实现细节淹没。

    /// <summary>构造参数。</summary>
    public AiChatServiceOptions Options { get; }

    /// <summary>聊天记录（最新在最后；最多保留 200 条）。</summary>
    public ObservableCollection<AiChatMessage> Messages { get; } = new();

    /// <summary>好感度引擎（界面读 <c>Value</c> / <c>MoodText</c>）。</summary>
    public AffectionEngine Affection { get; }

    /// <summary>是否正在等 API 返回（界面显示「正在输入…」）。</summary>
    public bool IsBusy { get; private set; }

    /// <summary>状态行（界面底部显示：就绪 / 正在思考 / 出错 / 离线模式）。</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>今天消耗的 token 总数（UI 显示，帮用户控制成本）。</summary>
    public long TodayTokens => _todayTokens;

    /// <summary>今天消耗的 prompt token。</summary>
    public long TodayPromptTokens => _todayPromptTokens;

    /// <summary>今天消耗的 completion token。</summary>
    public long TodayCompletionTokens => _todayCompletionTokens;

    /// <summary>是否填了可用 API Key（没填 = 离线兜底模式）。</summary>
    public bool HasUsableApiKey => Options.RequestOptions?.Invoke().IsUsable == true;

    /// <summary>好感度数值（供 WPF 绑定；变化时发 PropertyChanged）。</summary>
    public double AffectionValue => Affection.Value;

    /// <summary>情绪档位中文（供 WPF 绑定）。</summary>
    public string MoodText => Affection.MoodText;

    /// <summary>是否设置变化 / 状态变化（界面刷新）。</summary>
    public event EventHandler? StateChanged;

    /// <summary>新消息加入（用户 / 桌宠 / 系统提示）。</summary>
    public event EventHandler<AiChatMessage>? MessageAdded;

    /// <summary>拿到一条要朗读 / 表现回复（会话订阅它来联动 TTS / 气泡 / 表情 / 动作）。</summary>
    public event EventHandler<AiReplyEventArgs>? ReplyReady;

    /// <summary>属性变化（INotifyPropertyChanged）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 用户发一句话：立刻加入用户消息 → 调用 API（或离线兜底）→ 加入桌宠消息 → 更新好感度 →
    /// 触发 <see cref="ReplyReady"/>。返回这条回复（失败也会有兜底回复；<see cref="AiReply.IsFallback"/> 标明）。
    /// </summary>
    public partial Task<AiReply> SendUserMessageAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>让桌宠主动说一句（久未回复 / 定时搭话）；成功后触发 <see cref="ReplyReady"/>。</summary>
    public partial Task<AiReply> SendProactiveAsync(AiProactiveKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// 调度（会话每 ~1 秒调一次，<paramref name="userPresent"/> = 页面或桌宠可见）：
    /// 结算掉值、判断「久未回复」与「主动搭话」的时机，到点就 <see cref="SendProactiveAsync"/>。
    /// </summary>
    public partial void TickSchedule(DateTimeOffset now, bool userPresent);

    /// <summary>聊天历史保留轮数改了 / 好感度设置改了：重新套用（界面滑杆拖动时调）。</summary>
    public partial void ApplySettings();

    /// <summary>手动设置好感度（用户自己拖；不走自然模式规则）。</summary>
    public partial void SetAffection(double value);

    /// <summary>清空聊天记录（好感度不动）。</summary>
    public partial void ClearConversation();

    /// <summary>测试 API 连通性（设置卡「测试连接」按钮）。</summary>
    public partial Task<AiConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>把状态（聊天记录 + 好感度 + token 统计）立刻落盘。</summary>
    public partial void SaveState();

    /// <summary>释放（停掉所有在途请求；落盘一次）。</summary>
    public partial void Dispose();

    /// <summary>发 <see cref="PropertyChanged"/>（内部 / 协作类用）。</summary>
    internal void OnPropertyChanged(string? name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
