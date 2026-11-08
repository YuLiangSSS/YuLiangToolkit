using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.Live2D;
using YuLiangToolkit.Tools.Live2D.Ai;
using YuLiangToolkit.Tools.Live2D.Controls;
using YuLiangToolkit.Tools.Live2D.Views;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「Live2D 桌宠 · AI 聊天（DeepSeek）· 好感度」自检：全部纯逻辑、无网络、无窗口、不碰真实 Data/Config。
///
/// <para><b>覆盖范围</b>（对应任务 15 大类）：</para>
/// <list type="number">
///   <item>思考强度映射（AiThinkingLevels）；</item>
///   <item>人格 / 称呼 / 自称 / 属性目录；</item>
///   <item>称呼 / 自称「自定义优先、空白回落」；</item>
///   <item>系统提示词拼装（含 JSON 契约、长度上限、截断）；</item>
///   <item>状态行 / 用户消息拼装；</item>
///   <item>主动说话指令；</item>
///   <item>模型回复解析（AiReplyParser：围栏 / 别名 / 未知标签 / d 夹取 / 纯文本 / 清洗）；</item>
///   <item>DeepSeek 请求体与响应解析（思考开关 / temperature / response_format / max_tokens / 401 / 429）；</item>
///   <item>网络类失败重试 1 次；</item>
///   <item>API Key 的 DPAPI 加解密；</item>
///   <item>好感度引擎（阈值 / 涨 / 掉 / 兜底 / 手动模式 / 脸红概率）；</item>
///   <item>AiChatService 全链路（假 HTTP handler + 临时 ToolStorage + 现读配置回调）；</item>
///   <item>聊天窗贴桌宠定位（AiChatPlacement）；</item>
///   <item>粉色好感度试管的液面比例（AffectionVial.LiquidRatio）；</item>
///   <item>配置默认值 + JSON 往返。</item>
/// </list>
///
/// <para><b>不联网</b>：所有 HTTP 都走 <see cref="FakeChatHandler"/>（自写假 handler，返回固定 chat completion JSON）；
/// 落盘走 <see cref="ToolStorage.For"/> 指向的自检临时数据根（Program 已把 YLT_HOME 指到临时目录）。
/// 请求里的 API Key 是假 key，不会发到真实网络。</para>
/// </summary>
internal static class Live2DAiChecks
{
    /// <summary>自检用假 API Key（只用于断言「Header 带 Bearer、请求体不含明文」，不是真实 Key）。</summary>
    private const string ApiKeyForTest = "sk-selftest-live2d-1234567890";

    /// <summary>自检用临时数据目录名（都挂在自检临时 YLT_HOME 下，不碰真实 Data）。</summary>
    private const string TempToolId = "live2d";

    public static void Run(Action<string, bool, string> check, Action<string, bool> checkSimple)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        // ① 思考强度映射
        Safe(C, "思考强度", () => CheckThinkingLevels(C));

        // ② 人格 / 称呼 / 自称 / 属性目录
        Safe(C, "人格目录", () => CheckPersonaCatalog(C));

        // ③ 称呼 / 自称解析
        Safe(C, "称呼解析", () => CheckPersonaResolve(C));

        // ④ 系统提示词
        Safe(C, "系统提示词", () => CheckSystemPrompt(C));

        // ⑤ 状态行 / 用户消息
        Safe(C, "状态行", () => CheckStateLine(C));

        // ⑥ 主动说话指令
        Safe(C, "主动指令", () => CheckProactiveInstruction(C));

        // ⑦ 回复解析
        Safe(C, "回复解析", () => CheckReplyParser(C));

        // ⑧⑨ DeepSeek 请求体 / 响应 / 错误 / 重试
        Safe(C, "DeepSeek 客户端", () => CheckDeepSeekClient(C));
        Safe(C, "DeepSeek 重试", () => CheckDeepSeekRetry(C));

        // ⑩ API Key DPAPI
        Safe(C, "API Key 加密", () => CheckSecretStore(C));

        // ⑪ 好感度引擎
        Safe(C, "好感度引擎", () => CheckAffectionEngine(C));

        // ⑫ AiChatService 全链路（成功 / 离线 / 历史裁剪 / 落盘 / 清空 / 测试连接 / 主动调度）
        Safe(C, "聊天服务", () => CheckChatServiceSuccess(C));
        Safe(C, "聊天服务离线兜底", () => CheckChatServiceOffline(C));
        Safe(C, "聊天历史与落盘", () => CheckChatServiceHistory(C));
        Safe(C, "聊天服务主动调度", () => CheckChatServiceSchedule(C));

        // ⑬ 聊天窗定位
        Safe(C, "聊天窗定位", () => CheckChatPlacement(C));

        // ⑭ 好感度试管液面
        Safe(C, "好感度试管", () => CheckAffectionVial(C));

        // ⑮ 配置默认值 + JSON 往返
        Safe(C, "AI 配置", () => CheckConfig(C));
    }

    // ====================================================================
    // 各组断言（组内异常只记一条 FAIL，不影响其它组）
    // ====================================================================

    /// <summary>运行一组断言；组内抛异常时只记一条 FAIL。</summary>
    private static void Safe(CheckFn C, string label, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            C($"AI 聊天：{label}断言运行期间抛出异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- ① 思考强度

    private static void CheckThinkingLevels(CheckFn C)
    {
        var toConfigOk = AiThinkingLevels.ToConfig(AiThinkingLevel.Off) == "关闭"
                         && AiThinkingLevels.ToConfig(AiThinkingLevel.Low) == "低"
                         && AiThinkingLevels.ToConfig(AiThinkingLevel.Medium) == "中"
                         && AiThinkingLevels.ToConfig(AiThinkingLevel.High) == "高";
        C("AI 聊天：思考强度配置值映射（关闭 / 低 / 中 / 高）", toConfigOk,
            $"{AiThinkingLevels.ToConfig(AiThinkingLevel.Off)} / {AiThinkingLevels.ToConfig(AiThinkingLevel.Low)} / " +
            $"{AiThinkingLevels.ToConfig(AiThinkingLevel.Medium)} / {AiThinkingLevels.ToConfig(AiThinkingLevel.High)}");

        var roundTripOk = Enum.GetValues<AiThinkingLevel>()
            .All(level => AiThinkingLevels.FromConfig(AiThinkingLevels.ToConfig(level)) == level);
        C("AI 聊天：思考强度配置字符串往返（ToConfig → FromConfig）", roundTripOk,
            string.Join("、", Enum.GetValues<AiThinkingLevel>().Select(level => AiThinkingLevels.ToConfig(level))));

        var effortOk = AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.Off) == ""
                       && AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.Low) == "low"
                       && AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.Medium) == "high"
                       && AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.High) == "max";
        C("AI 聊天：reasoning_effort 映射（关闭不发 / 低 low / 中 high / 高 max）", effortOk,
            $"低={AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.Low)}、" +
            $"中={AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.Medium)}、" +
            $"高={AiThinkingLevels.ToReasoningEffort(AiThinkingLevel.High)}");

        var enabledOk = !AiThinkingLevels.IsThinkingEnabled(AiThinkingLevel.Off)
                        && Enum.GetValues<AiThinkingLevel>()
                            .Where(level => level != AiThinkingLevel.Off)
                            .All(AiThinkingLevels.IsThinkingEnabled);
        C("AI 聊天：只有「关闭」不算思考模式（低 / 中 / 高都算）", enabledOk);

        var indexOk = AiThinkingLevels.FromDisplayIndex(0) == AiThinkingLevel.Off
                      && AiThinkingLevels.FromDisplayIndex(1) == AiThinkingLevel.Low
                      && AiThinkingLevels.FromDisplayIndex(2) == AiThinkingLevel.Medium
                      && AiThinkingLevels.FromDisplayIndex(3) == AiThinkingLevel.High
                      && AiThinkingLevels.FromDisplayIndex(-1) == AiThinkingLevel.Off
                      && AiThinkingLevels.FromDisplayIndex(4) == AiThinkingLevel.Off
                      && AiThinkingLevels.FromDisplayIndex(99) == AiThinkingLevel.Off;
        C("AI 聊天：下拉下标越界 / 负数回落「关闭」", indexOk);

        var unknownOk = AiThinkingLevels.FromConfig(null) == AiThinkingLevel.Off
                        && AiThinkingLevels.FromConfig("") == AiThinkingLevel.Off
                        && AiThinkingLevels.FromConfig("乱写的值") == AiThinkingLevel.Off
                        && AiThinkingLevels.FromConfig(" max ") == AiThinkingLevel.High;
        C("AI 聊天：无法识别的配置字符串回落「关闭」（兼容旧值 max）", unknownOk);
    }

    // ---------------------------------------------------------------- ② 人格目录

    private static void CheckPersonaCatalog(CheckFn C)
    {
        var personas = AiPersonaCatalog.Personas;
        var expectedIds = new[] { "gentle", "tsundere", "yandere", "genki" };
        C("AI 聊天：人格目录 4 套（gentle / tsundere / yandere / genki，名字与说明都不为空）",
            personas.Count == 4
            && expectedIds.All(id => personas.Any(persona => string.Equals(persona.Id, id, StringComparison.Ordinal)))
            && personas.All(persona => persona.Name.Length > 0 && persona.Description.Length > 0),
            string.Join("、", personas.Select(persona => $"{persona.Id}={persona.Name}")));

        C("AI 聊天：用户称呼预设 = 用户 / 老公 / 主人",
            AiPersonaCatalog.AddressPresets.SequenceEqual(new[] { "用户", "老公", "主人" }),
            string.Join("、", AiPersonaCatalog.AddressPresets));

        C("AI 聊天：桌宠自称预设 = 我 / 本小姐 / 本姑娘 / 本鱼",
            AiPersonaCatalog.SelfPresets.SequenceEqual(new[] { "我", "本小姐", "本姑娘", "本鱼" }),
            string.Join("、", AiPersonaCatalog.SelfPresets));

        var expectedAttributes = new[] { "容易害羞", "爱吃醋", "黏人", "爱撒娇", "傲娇口癖", "容易生气", "爱夸奖" };
        C("AI 聊天：人格属性 7 个（与设置卡勾选项一致）",
            AiPersonaCatalog.AttributeOptions.Count == 7
            && expectedAttributes.All(option => AiPersonaCatalog.AttributeOptions.Contains(option)),
            string.Join("、", AiPersonaCatalog.AttributeOptions));

        C("AI 聊天：未知人格 Id 回落温柔女友（gentle）",
            AiPersonaCatalog.GetPersonaName("不存在的人格") == "温柔女友"
            && AiPersonaCatalog.GetPersona(null).Id == "gentle"
            && AiPersonaCatalog.GetPersona("TSUNDERE").Id == "tsundere");

        C("AI 聊天：情绪标签 9 个 / 动作标签 8 个（提示词与解析器共用同一份目录）",
            AiPersonaCatalog.EmotionTags.Count == 9 && AiPersonaCatalog.ActionTags.Count == 8
            && AiPersonaCatalog.EmotionTags.Contains("shy") && AiPersonaCatalog.EmotionTags.Contains("love")
            && AiPersonaCatalog.ActionTags.Contains("wave") && AiPersonaCatalog.ActionTags.Contains("heart"),
            $"情绪 {AiPersonaCatalog.EmotionTags.Count} 个 / 动作 {AiPersonaCatalog.ActionTags.Count} 个");
    }

    // ---------------------------------------------------------------- ③ 称呼 / 自称

    private static void CheckPersonaResolve(CheckFn C)
    {
        var custom = new AiPersonaSettings
        {
            AddressPreset = "主人",
            AddressCustom = "  阿凉  ",
            SelfPreset = "我",
            SelfCustom = "  本鱼  ",
        };
        C("AI 聊天：自定义称呼 / 自称非空时优先于预设（去首尾空白）",
            custom.ResolvedAddress == "阿凉" && custom.ResolvedSelf == "本鱼",
            $"称呼={custom.ResolvedAddress} 自称={custom.ResolvedSelf}");

        var blank = new AiPersonaSettings
        {
            AddressPreset = "老公",
            AddressCustom = "   ",
            SelfPreset = "本小姐",
            SelfCustom = "",
        };
        C("AI 聊天：自定义称呼 / 自称空白时回落预设",
            blank.ResolvedAddress == "老公" && blank.ResolvedSelf == "本小姐",
            $"称呼={blank.ResolvedAddress} 自称={blank.ResolvedSelf}");
    }

    // ---------------------------------------------------------------- ④ 系统提示词

    private static void CheckSystemPrompt(CheckFn C)
    {
        var persona = new AiPersonaSettings
        {
            PersonaId = "tsundere",
            PetName = "大肥鱼",
            AddressPreset = "主人",
            AddressCustom = "阿凉",
            SelfPreset = "本小姐",
            Attributes = "容易害羞,爱吃醋",
            CustomSetting = "喜欢在雨天听白噪音",
        };

        var prompt = AiPromptBuilder.BuildSystemPrompt(persona, includeAffectionDelta: true);

        C("AI 聊天：系统提示词包含实际称呼 / 自称（自定义优先）",
            prompt.Contains("「阿凉」", StringComparison.Ordinal)
            && prompt.Contains("「本小姐」", StringComparison.Ordinal),
            $"长度 {prompt.Length} 字");

        C("AI 聊天：系统提示词包含人格预设描述关键词（傲娇 = 嘴硬心软）",
            prompt.Contains("嘴硬心软", StringComparison.Ordinal));

        C("AI 聊天：系统提示词包含勾选属性（容易害羞、爱吃醋）",
            prompt.Contains("容易害羞", StringComparison.Ordinal)
            && prompt.Contains("爱吃醋", StringComparison.Ordinal),
            "属性列表随人格设置拼进提示词");

        C("AI 聊天：系统提示词包含自定义自我设定", prompt.Contains("喜欢在雨天听白噪音", StringComparison.Ordinal));

        C("AI 聊天：系统提示词含 json 字样与 r/e/a/d 契约键（JSON Output 模式要求）",
            prompt.Contains("json", StringComparison.OrdinalIgnoreCase)
            && prompt.Contains("\"r\"", StringComparison.Ordinal)
            && prompt.Contains("\"e\"", StringComparison.Ordinal)
            && prompt.Contains("\"a\"", StringComparison.Ordinal)
            && prompt.Contains("\"d\"", StringComparison.Ordinal));

        var promptNoDelta = AiPromptBuilder.BuildSystemPrompt(persona, includeAffectionDelta: false);
        C("AI 聊天：关掉好感度建议时不要求模型输出 d 字段",
            !promptNoDelta.Contains("\"d\"", StringComparison.Ordinal));

        C("AI 聊天：系统提示词长度受硬上限约束（任务要求 ≤900，实现常数 ≤700）",
            prompt.Length <= 900 && prompt.Length <= AiPromptBuilder.MaxSystemPromptLength,
            $"长度 {prompt.Length} / 上限 {AiPromptBuilder.MaxSystemPromptLength}");

        var longSetting = new AiPersonaSettings
        {
            PersonaId = "gentle",
            CustomSetting = new string('甲', 320) + "尾巴标记乙乙",
        };
        var longPrompt = AiPromptBuilder.BuildSystemPrompt(longSetting, includeAffectionDelta: true);
        C("AI 聊天：自定义自我设定超 300 字被截断（不超提示词总上限）",
            longPrompt.Length <= AiPromptBuilder.MaxSystemPromptLength
            && longPrompt.Contains('甲')
            && !longPrompt.Contains("尾巴标记乙乙", StringComparison.Ordinal),
            $"长度 {longPrompt.Length} 字");

        var defaultPrompt = AiPromptBuilder.BuildSystemPrompt(null, includeAffectionDelta: true);
        C("AI 聊天：空人格设置用默认温柔女友 + 桌宠名「大肥鱼」",
            defaultPrompt.Contains("大肥鱼", StringComparison.Ordinal)
            && defaultPrompt.Contains("温柔女友", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- ⑤ 状态行 / 用户消息

    private static void CheckStateLine(CheckFn C)
    {
        var stateLine = AiPromptBuilder.BuildStateLine(72.5, AiMood.Happy, new[] { "容易害羞", "爱吃醋" }, includeAffectionDelta: true);
        C("AI 聊天：状态行包含好感度数值 / 心情文案 / 属性",
            stateLine.Contains("72.5", StringComparison.Ordinal)
            && stateLine.Contains("开心", StringComparison.Ordinal)
            && stateLine.Contains("容易害羞", StringComparison.Ordinal)
            && stateLine.Contains("爱吃醋", StringComparison.Ordinal),
            stateLine);

        var sulky = AiPromptBuilder.BuildStateLine(40, AiMood.Sulky, null, includeAffectionDelta: false);
        C("AI 聊天：状态行按好感度档位换心情（40 → 闹别扭）且不带 d 提示",
            sulky.Contains("40.0", StringComparison.Ordinal)
            && sulky.Contains("闹别扭", StringComparison.Ordinal)
            && !sulky.Contains("d 字段", StringComparison.Ordinal),
            sulky);

        var userContent = AiPromptBuilder.BuildUserContent(stateLine, "今天好累");
        C("AI 聊天：用户消息同时包含状态行和用户原话",
            userContent.Contains(stateLine, StringComparison.Ordinal)
            && userContent.Contains("今天好累", StringComparison.Ordinal)
            && userContent.Contains("用户说：", StringComparison.Ordinal),
            $"用户消息长度 {userContent.Length} 字");
    }

    // ---------------------------------------------------------------- ⑥ 主动说话指令

    private static void CheckProactiveInstruction(CheckFn C)
    {
        var idle = AiPromptBuilder.BuildProactiveInstruction(AiProactiveKind.IdleReminder, 12, "上一句说的话");
        C("AI 聊天：久未回复指令含「多久没理 / 分钟」语义并让模型主动说话",
            idle.Contains("主动说话", StringComparison.Ordinal)
            && idle.Contains("分钟", StringComparison.Ordinal)
            && (idle.Contains("没理", StringComparison.Ordinal) || idle.Contains("没回", StringComparison.Ordinal)),
            idle);

        var chatter = AiPromptBuilder.BuildProactiveInstruction(AiProactiveKind.Chatter, 0, null);
        C("AI 聊天：定时搭话指令含「主动」语义",
            chatter.Contains("主动", StringComparison.Ordinal),
            chatter);

        C("AI 聊天：主动指令不泄露系统提示原文",
            !idle.Contains("你是「", StringComparison.Ordinal)
            && !idle.Contains("e 取值", StringComparison.Ordinal)
            && !idle.Contains("说话要求", StringComparison.Ordinal)
            && !chatter.Contains("你是「", StringComparison.Ordinal)
            && !chatter.Contains("e 取值", StringComparison.Ordinal));

        C("AI 聊天：主动指令长度受 180 上限约束",
            idle.Length <= AiPromptBuilder.MaxProactiveInstructionLength
            && chatter.Length <= AiPromptBuilder.MaxProactiveInstructionLength,
            $"久未回复 {idle.Length} / 定时搭话 {chatter.Length} 字");
    }

    // ---------------------------------------------------------------- ⑦ 回复解析

    private static void CheckReplyParser(CheckFn C)
    {
        var standard = AiReplyParser.Parse("{\"r\":\"你好呀\",\"e\":\"happy\",\"a\":\"wave\",\"d\":1}");
        C("AI 聊天：回复解析标准 JSON（文本 / 情绪 / 动作 / 好感变化）",
            standard.Text == "你好呀" && standard.Emotion == "happy"
            && standard.Action == "wave" && standard.AffectionDelta == 1,
            $"文本={standard.Text} 情绪={standard.Emotion} 动作={standard.Action} d={standard.AffectionDelta}");

        var fenced = AiReplyParser.Parse("```json\n{\"r\":\"围栏里的话\",\"e\":\"shy\",\"a\":\"heart\",\"d\":-1}\n```");
        C("AI 聊天：回复解析容忍 ```json 代码围栏",
            fenced.Text == "围栏里的话" && fenced.Emotion == "shy"
            && fenced.Action == "heart" && fenced.AffectionDelta == -1,
            $"文本={fenced.Text}");

        var alias = AiReplyParser.Parse("{\"reply\":\"别名回复\",\"emotion\":\"love\",\"action\":\"point\",\"delta\":2}");
        C("AI 聊天：回复解析支持 r/reply/text、e/emotion、a/action、d/delta 别名",
            alias.Text == "别名回复" && alias.Emotion == "love"
            && alias.Action == "point" && alias.AffectionDelta == 2,
            $"文本={alias.Text} 情绪={alias.Emotion} 动作={alias.Action}");

        var unknown = AiReplyParser.Parse("{\"r\":\"嗯\",\"e\":\"unknown-tag\",\"a\":\"飞起来\"}");
        C("AI 聊天：未知情绪 / 动作标签置空（不把乱值传给表情系统）",
            unknown.Text == "嗯" && unknown.Emotion.Length == 0 && unknown.Action.Length == 0,
            $"情绪={unknown.Emotion} 动作={unknown.Action}");

        var deltaHigh = AiReplyParser.Parse("{\"r\":\"嗯\",\"d\":9}");
        var deltaLow = AiReplyParser.Parse("{\"r\":\"嗯\",\"d\":-9}");
        C("AI 聊天：模型给的好感变化夹在 -3 ~ +3",
            deltaHigh.AffectionDelta == 3 && deltaLow.AffectionDelta == -3
            && AiReplyParser.NormalizeDelta(7) == 3
            && AiReplyParser.NormalizeDelta(-7) == -3
            && AiReplyParser.NormalizeDelta("5") == 3
            && AiReplyParser.NormalizeDelta("不是数字") == 0,
            $"d=9 → {deltaHigh.AffectionDelta}；d=-9 → {deltaLow.AffectionDelta}");

        var plain = AiReplyParser.Parse("今天也很开心，抱抱你！");
        C("AI 聊天：模型不守格式时把纯文本当回复（不丢话）",
            plain.Text.Contains("今天也很开心", StringComparison.Ordinal)
            && plain.Emotion.Length == 0
            && plain.AffectionDelta == 0,
            plain.Text);

        var empty = AiReplyParser.Parse("");
        var emptyJsonText = AiReplyParser.Parse("{\"r\":\"   \"}");
        C("AI 聊天：空 content / 空正文返回空回复（交给离线兜底）",
            empty.Text.Length == 0 && emptyJsonText.Text.Length == 0);

        var dirty = AiReplyParser.CleanText("**你好**\n\n 今天    开心 ");
        C("AI 聊天：回复清洗去掉 Markdown / 换行并压缩空白",
            !dirty.Contains("**", StringComparison.Ordinal)
            && !dirty.Contains('\n')
            && !dirty.Contains("  ", StringComparison.Ordinal)
            && dirty.Contains("你好", StringComparison.Ordinal)
            && dirty.Contains("今天", StringComparison.Ordinal),
            dirty);

        var longText = AiReplyParser.Parse(new string('好', 260));
        C("AI 聊天：回复超长截断到 200 字以内（加省略号）",
            longText.Text.Length <= AiReplyParser.MaxTextLength
            && longText.Text.EndsWith("…", StringComparison.Ordinal),
            $"长度 {longText.Text.Length}");
    }

    // ---------------------------------------------------------------- ⑧ DeepSeek 客户端

    private static void CheckDeepSeekClient(CheckFn C)
    {
        var options = new AiRequestOptions
        {
            ApiKey = ApiKeyForTest,
            Model = "deepseek-flash",
            Thinking = AiThinkingLevel.Off,
            MaxTokens = 160,
            JsonOutput = true,
        };
        var turns = new[]
        {
            new AiChatTurn { Role = "system", Text = "系统提示词" },
            new AiChatTurn { Role = "user", Text = "你好" },
        };

        using var buildClient = new DeepSeekClient();
        var offBody = buildClient.BuildRequestBody(options, turns);
        C("AI 聊天：关闭思考的请求体带 thinking=disabled、不发 reasoning_effort、带 temperature",
            offBody.Contains("\"thinking\":{\"type\":\"disabled\"}", StringComparison.Ordinal)
            && !offBody.Contains("reasoning_effort", StringComparison.Ordinal)
            && offBody.Contains("\"temperature\"", StringComparison.Ordinal),
            "关闭思考 = 最省 token（桌宠聊天默认）");

        var highBody = buildClient.BuildRequestBody(
            new AiRequestOptions
            {
                ApiKey = ApiKeyForTest, Model = "deepseek-flash",
                Thinking = AiThinkingLevel.High, MaxTokens = 160, JsonOutput = true,
            }, turns);
        C("AI 聊天：打开思考的请求体带 thinking=enabled + reasoning_effort=max，且不发 temperature",
            highBody.Contains("\"thinking\":{\"type\":\"enabled\"}", StringComparison.Ordinal)
            && highBody.Contains("\"reasoning_effort\":\"max\"", StringComparison.Ordinal)
            && !highBody.Contains("\"temperature\"", StringComparison.Ordinal),
            "思考模式不吃 temperature（DeepSeek 接口事实）");

        var lowBody = buildClient.BuildRequestBody(
            new AiRequestOptions { Thinking = AiThinkingLevel.Low, JsonOutput = true }, turns);
        var mediumBody = buildClient.BuildRequestBody(
            new AiRequestOptions { Thinking = AiThinkingLevel.Medium, JsonOutput = true }, turns);
        C("AI 聊天：思考强度 low / medium / high 分别映射 low / high / max",
            lowBody.Contains("\"reasoning_effort\":\"low\"", StringComparison.Ordinal)
            && mediumBody.Contains("\"reasoning_effort\":\"high\"", StringComparison.Ordinal)
            && highBody.Contains("\"reasoning_effort\":\"max\"", StringComparison.Ordinal));

        C("AI 聊天：JSON 输出模式带 response_format=json_object",
            offBody.Contains("\"response_format\"", StringComparison.Ordinal)
            && offBody.Contains("\"type\":\"json_object\"", StringComparison.Ordinal));

        C("AI 聊天：max_tokens 覆盖值生效（主动说话 80 / 测试连接 16）",
            ReadJsonInt(buildClient.BuildRequestBody(options, turns, 80), "max_tokens") == 80
            && ReadJsonInt(buildClient.BuildRequestBody(options, turns, 16), "max_tokens") == 16);

        // 说明：客户端自身把 max_tokens 安全夹在 1 ~ 4096（防止 0 / 负数 / 离谱大值）；
        // 面向用户的「单次回复预算 40 ~ 1000」在 Live2DSession / 设置卡 ViewModel 夹取。
        C("AI 聊天：max_tokens 非法值被夹在客户端安全范围（0 / 负数 / 超大）",
            ReadJsonInt(buildClient.BuildRequestBody(options, turns, 0), "max_tokens") >= 1
            && ReadJsonInt(buildClient.BuildRequestBody(options, turns, -5), "max_tokens") >= 1
            && ReadJsonInt(buildClient.BuildRequestBody(options, turns, 5000), "max_tokens") <= 4096);

        // ---- 请求成功：字段解析 + Header 带 Key、请求体不含明文 Key ----
        var successHandler = new FakeChatHandler((_, _) => JsonResponse(
            ChatCompletionJson("{\"r\":\"你好呀\",\"e\":\"shy\",\"a\":\"heart\",\"d\":2}", promptTokens: 12, completionTokens: 34)));
        using var successClient = new DeepSeekClient(successHandler);
        var chatReply = Run(() => successClient.ChatAsync(options, turns));
        C("AI 聊天：请求成功解析出文本 / 情绪 / 动作 / 好感变化 / prompt / completion token",
            chatReply.Text == "你好呀" && chatReply.Emotion == "shy" && chatReply.Action == "heart"
            && chatReply.AffectionDelta == 2 && chatReply.PromptTokens == 12
            && chatReply.CompletionTokens == 34 && string.IsNullOrEmpty(chatReply.Error),
            $"文本={chatReply.Text} token={chatReply.PromptTokens}+{chatReply.CompletionTokens}");

        C("AI 聊天：API Key 只在 Authorization Header（Bearer），请求体里没有明文 Key",
            successHandler.Authorizations.Count == 1
            && successHandler.Authorizations[0].Contains("Bearer " + ApiKeyForTest, StringComparison.Ordinal)
            && successHandler.Bodies.Count == 1
            && !successHandler.Bodies[0].Contains(ApiKeyForTest, StringComparison.Ordinal),
            "请求体 = 模型 / 消息 / 参数，Key 不进 body");

        // ---- HTTP 401 / 429 中文错误文案 ----
        var unauthorizedHandler = new FakeChatHandler((_, _) => JsonResponse(
            "{\"error\":{\"message\":\"Authentication Fails\"}}", HttpStatusCode.Unauthorized));
        using var unauthorizedClient = new DeepSeekClient(unauthorizedHandler);
        var unauthorizedReply = Run(() => unauthorizedClient.ChatAsync(options, turns));
        C("AI 聊天：HTTP 401 错误文案包含「API Key」",
            unauthorizedReply.IsFallback && !string.IsNullOrEmpty(unauthorizedReply.Error)
            && unauthorizedReply.Error.Contains("API Key", StringComparison.Ordinal),
            unauthorizedReply.Error ?? "");

        var rateHandler = new FakeChatHandler((_, _) => JsonResponse(
            "{\"error\":{\"message\":\"Too many requests\"}}", HttpStatusCode.TooManyRequests));
        using var rateClient = new DeepSeekClient(rateHandler);
        var rateReply = Run(() => rateClient.ChatAsync(options, turns));
        C("AI 聊天：HTTP 429 错误文案包含「频繁 / 稍等（稍后）」",
            rateReply.IsFallback && !string.IsNullOrEmpty(rateReply.Error)
            && rateReply.Error.Contains("频繁", StringComparison.Ordinal)
            && (rateReply.Error.Contains("稍等", StringComparison.Ordinal)
                || rateReply.Error.Contains("稍后", StringComparison.Ordinal)),
            rateReply.Error ?? "");

        var emptyContentReply = new DeepSeekClient().ParseResponse(
            ChatCompletionJson("", promptTokens: 5, completionTokens: 0));
        C("AI 聊天：服务端返回空 content 时给出兜底错误（不把空话上屏）",
            emptyContentReply.IsFallback && emptyContentReply.Text.Length == 0
            && !string.IsNullOrEmpty(emptyContentReply.Error));

        C("AI 聊天：接口地址归一化（空 → 默认 / 去末尾斜杠 / 补 https）",
            DeepSeekClient.NormalizeBaseUrl("") == AiRequestOptions.DefaultBaseUrl
            && DeepSeekClient.NormalizeBaseUrl("api.deepseek.com/") == "https://api.deepseek.com"
            && DeepSeekClient.NormalizeBaseUrl("http://127.0.0.1:11434/") == "http://127.0.0.1:11434");
    }

    // ---------------------------------------------------------------- ⑨ 重试

    private static void CheckDeepSeekRetry(CheckFn C)
    {
        var options = new AiRequestOptions { ApiKey = ApiKeyForTest, Model = "deepseek-flash" };
        var turns = new[] { new AiChatTurn { Role = "user", Text = "在吗" } };
        var handler = new FakeChatHandler((index, _) =>
        {
            if (index == 0) throw new HttpRequestException("模拟断网（自检）");
            return JsonResponse(ChatCompletionJson("{\"r\":\"重试成功\",\"e\":\"happy\",\"a\":\"nod\",\"d\":0}", 8, 8));
        });
        using var client = new DeepSeekClient(handler);
        var reply = Run(() => client.ChatAsync(options, turns));
        C("AI 聊天：网络类失败自动重试 1 次后成功（请求共 2 次）",
            handler.CallCount == 2
            && string.IsNullOrEmpty(reply.Error)
            && reply.Text == "重试成功",
            $"请求次数={handler.CallCount} 文本={reply.Text}");
    }

    // ---------------------------------------------------------------- ⑩ API Key DPAPI

    private static void CheckSecretStore(CheckFn C)
    {
        const string secret = "sk-selftest-dpapi-123456";
        var cipher = AiSecretStore.Protect(secret);
        C("AI 聊天：API Key 落盘是 DPAPI 密文（dpapi: 前缀、不含明文）",
            AiSecretStore.IsProtected(cipher)
            && cipher.Length > AiSecretStore.Prefix.Length
            && !cipher.Contains(secret, StringComparison.Ordinal),
            cipher.Length <= 28 ? cipher : cipher[..28] + "…");

        C("AI 聊天：DPAPI 密文同一 Windows 用户能解回原文",
            AiSecretStore.Unprotect(cipher) == secret);

        C("AI 聊天：空值 / 历史明文兼容（空 ↔ 空，明文原样返回）",
            AiSecretStore.Protect("") == ""
            && AiSecretStore.Unprotect("") == ""
            && AiSecretStore.Unprotect("plain-text-key") == "plain-text-key"
            && !AiSecretStore.IsProtected("plain-text-key"));

        C("AI 聊天：已填 Key 的界面打码文案不泄露明文",
            AiSecretStore.Describe("").Contains("未填写", StringComparison.Ordinal)
            && AiSecretStore.Describe(cipher).Contains("密文保存", StringComparison.Ordinal)
            && !AiSecretStore.Describe(cipher).Contains(secret, StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- ⑪ 好感度引擎

    private static void CheckAffectionEngine(CheckFn C)
    {
        C("AI 聊天：好感度阈值（<30 生气 / 30 闹别扭 / 50 平常 / 70 开心 / 85 心动）",
            AffectionEngine.ResolveMood(29.9) == AiMood.Angry
            && AffectionEngine.ResolveMood(30) == AiMood.Sulky
            && AffectionEngine.ResolveMood(49.9) == AiMood.Sulky
            && AffectionEngine.ResolveMood(50) == AiMood.Normal
            && AffectionEngine.ResolveMood(69.9) == AiMood.Normal
            && AffectionEngine.ResolveMood(70) == AiMood.Happy
            && AffectionEngine.ResolveMood(84.9) == AiMood.Happy
            && AffectionEngine.ResolveMood(85) == AiMood.Love,
            "29.9/Angry、30/Sulky、50/Normal、70/Happy、85/Love");

        C("AI 聊天：好感度夹取 0 ~ 100 并保留 1 位小数（120→100、-5→0、33.36→33.4）",
            AffectionEngine.Clamp(120) == 100
            && AffectionEngine.Clamp(-5) == 0
            && AffectionEngine.Clamp(33.36) == 33.4
            && AffectionEngine.Clamp(50) == 50);

        var fixedNow = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(8));

        // ---- 手动模式：聊天 / 时间都不改值 ----
        var manual = new AffectionEngine(new AffectionState { Value = 50, LastChatUnix = fixedNow.ToUnixTimeSeconds() });
        manual.Settings = new AffectionSettings { NaturalMode = false };
        var manualBefore = manual.Value;
        var manualChat = manual.ApplyChat(3, fixedNow);
        var manualDecay = manual.ApplyDecay(fixedNow.AddDays(10));
        C("AI 聊天：手动模式（自然模式关）聊天 / 掉值都不改好感度",
            manualChat == 0 && manualDecay == 0 && Near(manual.Value, manualBefore),
            $"数值 {manualBefore} → {manual.Value}");

        // ---- 自然模式：聊天涨 ----
        var chatEngine = new AffectionEngine(new AffectionState { Value = 50 });
        chatEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = true, Floor = 20,
            GainPerChat = 1, UseModelDelta = false,
        };
        chatEngine.ApplyChat(0, fixedNow);
        chatEngine.ApplyChat(2, fixedNow); // 关掉模型 d 值时 d 不参与
        C("AI 聊天：自然模式每聊一轮涨 GainPerChat（关掉模型 d 值时 d 不参与）",
            Near(chatEngine.Value, 52) && chatEngine.State.LastChatUnix == fixedNow.ToUnixTimeSeconds(),
            $"50 → {chatEngine.Value}（每轮 +1）");

        var deltaEngine = new AffectionEngine(new AffectionState { Value = 50 });
        deltaEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = false, GainPerChat = 1, UseModelDelta = true,
        };
        deltaEngine.ApplyChat(2, fixedNow);   // 1 + 2 = +3 → 53
        deltaEngine.ApplyChat(10, fixedNow);  // 模型 d 夹到 3：1 + 3 = +4 → 57
        deltaEngine.ApplyChat(-9, fixedNow);  // 模型 d 夹到 -3：1 - 3 = -2 → 55
        C("AI 聊天：模型 d 参与并夹到 -3 ~ +3、单轮总变化夹在 ±4",
            Near(deltaEngine.Value, 55),
            $"50 → {deltaEngine.Value}（+3 / +4 / -2）");

        // ---- 每日涨幅上限 15 ----
        var capEngine = new AffectionEngine(new AffectionState { Value = 50 });
        capEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = false, GainPerChat = 10, UseModelDelta = false,
        };
        var gains = new List<int>();
        for (var i = 0; i < 5; i++) gains.Add(capEngine.ApplyChat(0, fixedNow));
        C("AI 聊天：自然模式每天聊天最多涨 15（第 5 轮不再涨）",
            Near(capEngine.Value, 65)
            && capEngine.State.TodayGain <= AffectionEngine.DailyGainCap + 1e-9
            && gains.Sum() == 15
            && gains[4] == 0,
            $"数值 {capEngine.Value}，今日累计 {capEngine.State.TodayGain:0.#}，五轮实际 {string.Join("/", gains)}");

        // ---- 掉值：只按完整周期 ----
        var decayState = new AffectionState { Value = 50, LastChatUnix = fixedNow.ToUnixTimeSeconds() };
        var decayEngine = new AffectionEngine(decayState);
        decayEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = false, DecayPerDay = 3, DecayAfterHours = 24,
        };
        var notFull = decayEngine.ApplyDecay(fixedNow.AddHours(23.9));
        var onePeriod = decayEngine.ApplyDecay(fixedNow.AddHours(24));
        var nextPeriod = decayEngine.ApplyDecay(fixedNow.AddHours(48));
        C("AI 聊天：掉值只按完整周期结算（不满周期不掉、每满 24 小时掉 3）",
            notFull == 0 && onePeriod == 3 && nextPeriod == 3
            && Near(decayEngine.Value, 44),
            $"23.9h 掉 {notFull}、24h 掉 {onePeriod}、48h 再掉 {nextPeriod}，剩 {decayEngine.Value}");

        var twoPeriodState = new AffectionState { Value = 50, LastChatUnix = fixedNow.ToUnixTimeSeconds() };
        var twoPeriodEngine = new AffectionEngine(twoPeriodState);
        twoPeriodEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = false, DecayPerDay = 3, DecayAfterHours = 24,
        };
        var twoPeriodLoss = twoPeriodEngine.ApplyDecay(fixedNow.AddHours(48));
        C("AI 聊天：一次补结算两个完整周期（48 小时掉 2 × 3）",
            twoPeriodLoss == 6 && Near(twoPeriodEngine.Value, 44),
            $"掉 {twoPeriodLoss}，剩 {twoPeriodEngine.Value}");

        // ---- 兜底 ----
        var floorState = new AffectionState { Value = 22, LastChatUnix = fixedNow.ToUnixTimeSeconds() };
        var floorEngine = new AffectionEngine(floorState);
        floorEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = true, Floor = 20, DecayPerDay = 3, DecayAfterHours = 24,
        };
        floorEngine.ApplyDecay(fixedNow.AddHours(48)); // 22 - 6 = 16，兜底到 20
        C("AI 聊天：兜底开启时自动掉值不跌破兜底值（22 - 6 → 20）",
            Near(floorEngine.Value, 20),
            $"剩 {floorEngine.Value:0.0}");

        var noFloorState = new AffectionState { Value = 22, LastChatUnix = fixedNow.ToUnixTimeSeconds() };
        var noFloorEngine = new AffectionEngine(noFloorState);
        noFloorEngine.Settings = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = false, Floor = 20, DecayPerDay = 3, DecayAfterHours = 24,
        };
        noFloorEngine.ApplyDecay(fixedNow.AddHours(240)); // 22 - 10 × 3 → 夹到 0
        C("AI 聊天：兜底关闭时好感度可以掉到 0",
            Near(noFloorEngine.Value, 0),
            $"剩 {noFloorEngine.Value:0.0}");

        var setEngine = new AffectionEngine(new AffectionState { Value = 50 });
        setEngine.Settings = new AffectionSettings { NaturalMode = true, FloorEnabled = true, Floor = 20 };
        setEngine.SetValue(5);
        C("AI 聊天：手动设置好感度不受兜底限制（可以设到 5）",
            Near(setEngine.Value, 5),
            $"剩 {setEngine.Value:0.0}");

        // ---- 脸红概率 ----
        var high = new AffectionEngine(new AffectionState { Value = 100 });
        var low = new AffectionEngine(new AffectionState { Value = 70 });
        C("AI 聊天：高好感更容易脸红（100 时 random 0.3 会脸红 / 70 时不会）",
            high.IsBlushLikely(0.3) && !low.IsBlushLikely(0.3));

        var shyAttr = new AffectionEngine(new AffectionState { Value = 65 })
        {
            Attributes = new[] { "容易害羞" },
        };
        var noAttr = new AffectionEngine(new AffectionState { Value = 65 });
        C("AI 聊天：「容易害羞」属性会提高脸红概率",
            shyAttr.IsBlushLikely(0.25) && !noAttr.IsBlushLikely(0.25));
    }

    // ---------------------------------------------------------------- ⑫ AiChatService：成功 / 离线

    private static void CheckChatServiceSuccess(CheckFn C)
    {
        var requestOptions = new AiRequestOptions
        {
            ApiKey = ApiKeyForTest,
            Model = "deepseek-flash",
            Thinking = AiThinkingLevel.Off,
            MaxTokens = 160,
            JsonOutput = true,
        };
        var affection = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = true, Floor = 20,
            GainPerChat = 1, UseModelDelta = false,
        };
        var handler = new FakeChatHandler((_, _) => JsonResponse(
            ChatCompletionJson("{\"r\":\"你好呀，主人～\",\"e\":\"shy\",\"a\":\"wave\",\"d\":0}", 37, 52)));
        var storage = ToolStorage.For(TempToolId, "live2d-ai-check-success");
        using var service = CreateService(storage, handler, () => requestOptions, affection: () => affection);

        var events = new List<AiReplyEventArgs>();
        service.ReplyReady += (_, e) => { lock (events) events.Add(e); };

        var reply = Run(() => service.SendUserMessageAsync("你好"));

        C("AI 聊天：发一句话后聊天记录 = 用户消息 + 桌宠回复（情绪随回复带上）",
            service.Messages.Count == 2
            && service.Messages[0].IsUser && service.Messages[0].Text == "你好"
            && service.Messages[1].IsBot && service.Messages[1].Text == "你好呀，主人～"
            && service.Messages[1].Emotion == "shy",
            $"共 {service.Messages.Count} 条");

        C("AI 聊天：拿到回复触发 ReplyReady（Speak=true、普通回复 Proactive 为空）",
            events.Count == 1 && events[0].Speak && events[0].Proactive is null
            && events[0].Reply.Text == "你好呀，主人～",
            $"事件 {events.Count} 次");

        C("AI 聊天：请求完成后 IsBusy = false",
            !service.IsBusy);

        C("AI 聊天：token 统计累加（prompt 37 + completion 52 = 89）",
            service.TodayPromptTokens == 37 && service.TodayCompletionTokens == 52
            && service.TodayTokens == 89,
            $"{service.TodayPromptTokens} + {service.TodayCompletionTokens} = {service.TodayTokens}");

        C("AI 聊天：自然模式聊一轮好感度 +1（50 → 51）",
            Near(service.Affection.Value, 51) && service.AffectionValue == service.Affection.Value,
            $"{service.Affection.Value:0.0} / {service.MoodText}");

        C("AI 聊天：请求走注入的假 handler 一次（不联网）",
            handler.CallCount == 1 && reply.Text.Length > 0 && string.IsNullOrEmpty(reply.Error));

        C("AI 聊天：聊天状态落盘到 Data\\live2d\\ai\\chat-state.json（ToolStorage 下）",
            storage.FileExists("ai/chat-state.json"),
            storage.PathOf("ai", "chat-state.json"));
    }

    private static void CheckChatServiceOffline(CheckFn C)
    {
        var requestOptions = new AiRequestOptions { ApiKey = "", JsonOutput = true };
        var affection = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = true, Floor = 20,
            GainPerChat = 1, UseModelDelta = false,
        };
        var handler = new FakeChatHandler((_, _) => throw new InvalidOperationException("离线兜底不应该发请求"));
        var storage = ToolStorage.For(TempToolId, "live2d-ai-check-offline");
        using var service = CreateService(storage, handler, () => requestOptions, affection: () => affection);

        var events = new List<AiReplyEventArgs>();
        service.ReplyReady += (_, e) => { lock (events) events.Add(e); };

        var reply = Run(() => service.SendUserMessageAsync("在吗"));

        C("AI 聊天：没填 API Key 时走离线兜底（IsFallback=true，不发网络请求）",
            reply.IsFallback && handler.CallCount == 0
            && reply.Text.Contains("离线兜底", StringComparison.Ordinal),
            reply.Text);

        C("AI 聊天：离线兜底也照样加两条消息、涨好感度、触发回复事件",
            service.Messages.Count == 2 && service.Messages[0].IsUser && service.Messages[1].IsBot
            && Near(service.Affection.Value, 51)
            && events.Count == 1 && events[0].Speak
            && !service.IsBusy,
            $"消息 {service.Messages.Count} 条，好感度 {service.Affection.Value:0.0}");

        C("AI 聊天：没 Key 时状态行提示离线模式",
            service.StatusText.Contains("离线", StringComparison.Ordinal),
            service.StatusText);
    }

    // ---------------------------------------------------------------- ⑫ AiChatService：历史裁剪 / 落盘 / 清空 / 测试连接

    private static void CheckChatServiceHistory(CheckFn C)
    {
        var requestOptions = new AiRequestOptions { ApiKey = ApiKeyForTest, JsonOutput = true };
        var staticAffection = new AffectionSettings { NaturalMode = false };
        var handler = new FakeChatHandler((_, _) => JsonResponse(
            ChatCompletionJson("{\"r\":\"收到\",\"e\":\"neutral\",\"a\":\"none\",\"d\":0}", 10, 10)));
        var storage = ToolStorage.For(TempToolId, "live2d-ai-check-history");
        using var service = CreateService(
            storage, handler, () => requestOptions,
            affection: () => staticAffection,
            historyTurns: () => 2);

        // 直接塞一条 notice 模拟系统提示 / 错误说明：它不能进模型历史
        service.Messages.Add(new AiChatMessage
        {
            TimestampUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            Role = "notice",
            Text = "系统提示不该进历史",
        });

        foreach (var text in new[] { "第一句", "第二句", "第三句", "第四句", "第五句" })
            Run(() => service.SendUserMessageAsync(text));

        var lastBody = handler.Bodies[^1];
        C("AI 聊天：HistoryTurns=2 时请求最多 = 1 系统 + 2×2 历史 + 1 当前（共 6 条）",
            handler.Bodies.Count == 5
            && ReadMessagesCount(lastBody) == 6,
            $"请求 {handler.Bodies.Count} 次，最后一次 messages = {ReadMessagesCount(lastBody)} 条");

        C("AI 聊天：只带最近 N 轮历史（第一 / 第二句已被裁掉，第三 / 第四句还在）",
            lastBody.Contains("第三句", StringComparison.Ordinal)
            && lastBody.Contains("第四句", StringComparison.Ordinal)
            && !lastBody.Contains("第一句", StringComparison.Ordinal)
            && !lastBody.Contains("第二句", StringComparison.Ordinal));

        C("AI 聊天：notice 系统提示不进发给模型的历史",
            !lastBody.Contains("系统提示不该进历史", StringComparison.Ordinal));

        // ---- 落盘：同一 Storage 新建第二个服务能读回 ----
        var persistOptions = new AiRequestOptions { ApiKey = ApiKeyForTest, JsonOutput = true };
        var persistAffection = new AffectionSettings
        {
            NaturalMode = true, FloorEnabled = true, Floor = 20,
            GainPerChat = 1, UseModelDelta = false,
        };
        var persistStorage = ToolStorage.For(TempToolId, "live2d-ai-check-persist");
        using (var first = CreateService(
                   persistStorage, handler, () => persistOptions, affection: () => persistAffection))
        {
            Run(() => first.SendUserMessageAsync("记住我"));
            first.SaveState();

            using var second = CreateService(
                persistStorage, handler, () => persistOptions, affection: () => persistAffection);
            C("AI 聊天：重启后从 chat-state.json 读回聊天记录 / 好感度 / 今日 token",
                second.Messages.Count == first.Messages.Count
                && second.Messages.Count == 2
                && second.Messages[0].Text == "记住我"
                && Near(second.Affection.Value, first.Affection.Value)
                && Near(second.Affection.Value, 51)
                && second.TodayTokens == 20,
                $"消息 {second.Messages.Count} 条，好感度 {second.Affection.Value:0.0}，token {second.TodayTokens}");

            var valueBeforeClear = second.Affection.Value;
            second.ClearConversation();
            C("AI 聊天：清空聊天记录不动好感度",
                second.Messages.Count == 0 && Near(second.Affection.Value, valueBeforeClear),
                $"清空后消息 {second.Messages.Count} 条，好感度 {second.Affection.Value:0.0}");
        }

        // ---- 测试连接 ----
        var connectionHandler = new FakeChatHandler((_, _) => JsonResponse(ChatCompletionJson("正常", 8, 2)));
        var connectionStorage = ToolStorage.For(TempToolId, "live2d-ai-check-testconn");
        using var connectionService = CreateService(
            connectionStorage, connectionHandler, () => new AiRequestOptions { ApiKey = ApiKeyForTest });
        var connection = Run(() => connectionService.TestConnectionAsync());
        C("AI 聊天：测试连接（假 handler）返回 Ok=true 并带中文提示",
            connection.Ok && connection.Message.Contains("连接成功", StringComparison.Ordinal),
            connection.Message);
    }

    // ---------------------------------------------------------------- ⑫ AiChatService：主动说话调度

    private static void CheckChatServiceSchedule(CheckFn C)
    {
        var requestOptions = new AiRequestOptions { ApiKey = ApiKeyForTest, JsonOutput = true };
        var proactive = new AiProactiveSettings
        {
            Enabled = true,
            IntervalMinutes = 20,
            IdleReminderEnabled = true,
            IdleSilenceMinutes = 1,
        };
        var handler = new FakeChatHandler((_, _) => JsonResponse(
            ChatCompletionJson("{\"r\":\"在呢～\",\"e\":\"shy\",\"a\":\"wave\",\"d\":0}", 10, 10)));
        var storage = ToolStorage.For(TempToolId, "live2d-ai-check-tick");
        using var service = CreateService(
            storage, handler, () => requestOptions,
            affection: () => new AffectionSettings { NaturalMode = false },
            proactive: () => proactive);

        var events = new List<AiReplyEventArgs>();
        service.ReplyReady += (_, e) => { lock (events) events.Add(e); };

        // ① 页面 / 桌宠都不可见：不主动说话、不结算
        service.TickSchedule(DateTimeOffset.Now.AddMinutes(30), userPresent: false);
        C("AI 聊天：页面 / 桌宠都不可见时不主动说话",
            handler.CallCount == 0 && ReadCount(events) == 0,
            $"请求 {handler.CallCount} 次");

        // ② 用户刚说完话：2 分钟内不打断
        Run(() => service.SendUserMessageAsync("在吗"));
        service.TickSchedule(DateTimeOffset.Now.AddMinutes(1), userPresent: true);
        C("AI 聊天：用户刚说完 2 分钟内不主动搭话",
            handler.CallCount == 1 && ReadCount(events) == 1,
            $"请求 {handler.CallCount} 次，事件 {ReadCount(events)} 次");

        // ③ 时钟前进到门限之后：触发「久未回复」撒娇
        service.TickSchedule(DateTimeOffset.Now.AddMinutes(5), userPresent: true);
        var triggered = WaitUntil(() => events.Any(args => args.Proactive == AiProactiveKind.IdleReminder), 5000);
        C("AI 聊天：门限到点由 TickSchedule 触发久未回复撒娇（ReplyReady.Proactive=IdleReminder）",
            triggered && handler.CallCount == 2,
            $"请求 {handler.CallCount} 次，事件 {ReadCount(events)} 次");

        C("AI 聊天：主动撒娇也会上屏一条桌宠消息",
            service.Messages.Count == 3 && service.Messages[^1].IsBot,
            $"聊天记录 {service.Messages.Count} 条");
    }

    // ---------------------------------------------------------------- ⑬ 聊天窗定位

    private static void CheckChatPlacement(CheckFn C)
    {
        const double windowWidth = 372;
        const double windowHeight = 524;

        var below = AiChatPlacement.Calculate(800, 100, 420, 400, windowWidth, windowHeight, 0, 0, 1920, 1080);
        C("AI 聊天：聊天窗下方放得下 → 水平居中、贴桌宠下方 8 像素",
            Near(below.Left, 824) && Near(below.Top, 508),
            $"{below.Left:0.##},{below.Top:0.##}");

        var leftSide = AiChatPlacement.Calculate(800, 500, 420, 862, windowWidth, windowHeight, 0, 0, 1920, 1080);
        C("AI 聊天：下方放不下 → 贴桌宠左侧且不越工作区",
            Near(leftSide.Left, 420) && Near(leftSide.Top, 552),
            $"{leftSide.Left:0.##},{leftSide.Top:0.##}");

        var rightSide = AiChatPlacement.Calculate(100, 500, 420, 862, windowWidth, windowHeight, 0, 0, 1920, 1080);
        C("AI 聊天：左侧也放不下 → 改贴桌宠右侧",
            Near(rightSide.Left, 528) && Near(rightSide.Top, 552),
            $"{rightSide.Left:0.##},{rightSide.Top:0.##}");

        var fallback = AiChatPlacement.Calculate(0, 0, 420, 862, windowWidth, windowHeight, 0, 0, 300, 300);
        C("AI 聊天：全部放不下 → 夹进工作区并留 4 像素边距",
            Near(fallback.Left, AiChatPlacement.EdgeMargin)
            && Near(fallback.Top, AiChatPlacement.EdgeMargin),
            $"{fallback.Left:0.##},{fallback.Top:0.##}");

        var dirty = AiChatPlacement.Calculate(
            double.NaN, double.NaN, 420, 862, double.NaN, double.NaN, 0, 0, 1920, 1080);
        C("AI 聊天：脏尺寸 / 坐标不返回 NaN（退回默认窗口尺寸）",
            !double.IsNaN(dirty.Left) && !double.IsNaN(dirty.Top)
            && dirty.Left >= AiChatPlacement.EdgeMargin
            && dirty.Top >= AiChatPlacement.EdgeMargin,
            $"{dirty.Left:0.##},{dirty.Top:0.##}");
    }

    // ---------------------------------------------------------------- ⑭ 好感度试管

    private static void CheckAffectionVial(CheckFn C)
    {
        var ratios = new (double Value, double Ratio)[]
        {
            (0, 0), (50, 0.5), (100, 1), (-20, 0), (150, 1),
        };
        C("AI 聊天：粉色试管的液面比例（0→0 / 50→0.5 / 100→1 / 越界夹取）",
            ratios.All(item => Near(AffectionVial.LiquidRatio(item.Value), item.Ratio)),
            string.Join("、", ratios.Select(item => $"{item.Value}→{AffectionVial.LiquidRatio(item.Value):0.##}")));

        C("AI 聊天：好感度 NaN 时试管按默认 50 显示（不画成空瓶）",
            Near(AffectionVial.LiquidRatio(double.NaN), 0.5));
    }

    // ---------------------------------------------------------------- ⑮ 配置默认值 + JSON 往返

    private static void CheckConfig(CheckFn C)
    {
        var defaults = new Live2DConfig();

        C("AI 配置默认值：接入（官方地址 / deepseek-flash / 思考关闭 / 160 token / 6 轮历史 / JSON 输出）",
            defaults.AiApiKeyProtected.Length == 0
            && defaults.AiBaseUrl == "https://api.deepseek.com"
            && defaults.AiModel == "deepseek-flash"
            && defaults.AiThinkingLevel == "关闭"
            && defaults.AiMaxTokens == 160
            && defaults.AiHistoryTurns == 6,
            $"{defaults.AiBaseUrl} · {defaults.AiModel} · 思考{defaults.AiThinkingLevel} · {defaults.AiMaxTokens} token · {defaults.AiHistoryTurns} 轮");

        C("AI 配置默认值：人格 / 称呼（gentle、人格补充空、称呼「主人」、自称「我」、属性空、自我设定空）",
            defaults.AiPersonaId == "gentle"
            && defaults.AiPersonaCustom.Length == 0
            && defaults.AiAddressPreset == "主人"
            && defaults.AiAddressCustom.Length == 0
            && defaults.AiSelfPreset == "我"
            && defaults.AiSelfCustom.Length == 0
            && defaults.AiAttributes.Length == 0
            && defaults.AiCustomSetting.Length == 0);

        C("AI 配置默认值：表现（朗读 / 表情动作联动开、聊天窗不常开、显示桌宠自动打开）",
            defaults.AiSpeakReply && defaults.AiMoodEffects
            && !defaults.AiChatWindowOpen && defaults.AiChatAutoOpen);

        C("AI 配置默认值：主动聊天（默认关 / 间隔 20 分钟 / 久未回复提醒开 / 门限 10 分钟）",
            !defaults.AiProactiveEnabled
            && Near(defaults.AiProactiveIntervalMinutes, 20)
            && defaults.AiIdleReminderEnabled
            && Near(defaults.AiIdleSilenceMinutes, 10));

        C("AI 配置默认值：好感度（自然模式开 / 兜底开 20 / 每轮 +1 / 每天掉 3 / 24 小时周期 / 模型 d 计入）",
            defaults.AiAffectionNatural
            && defaults.AiAffectionFloorEnabled
            && Near(defaults.AiAffectionFloor, 20)
            && Near(defaults.AiAffectionGainPerChat, 1.0)
            && Near(defaults.AiAffectionDecayPerDay, 3.0)
            && Near(defaults.AiAffectionDecayAfterHours, 24)
            && defaults.AiAffectionModelDelta);

        // ---- JSON 往返：全部 AI 配置项改成非默认值，序列化再反序列化后保持 ----
        var cfg = new Live2DConfig
        {
            AiApiKeyProtected = "dpapi:qwertyuiop",
            AiBaseUrl = "https://proxy.example.com/",
            AiModel = "deepseek-v4-pro",
            AiThinkingLevel = "高",
            AiMaxTokens = 320,
            AiHistoryTurns = 10,
            AiPersonaId = "tsundere",
            AiPersonaCustom = "人格补充自检",
            AiAddressPreset = "老公",
            AiAddressCustom = "阿凉",
            AiSelfPreset = "本鱼",
            AiSelfCustom = "小鱼",
            AiAttributes = "容易害羞,爱吃醋",
            AiCustomSetting = "自我设定自检",
            AiSpeakReply = false,
            AiMoodEffects = false,
            AiChatWindowOpen = true,
            AiChatAutoOpen = false,
            AiProactiveEnabled = true,
            AiProactiveIntervalMinutes = 45,
            AiIdleReminderEnabled = false,
            AiIdleSilenceMinutes = 30,
            AiAffectionNatural = false,
            AiAffectionFloorEnabled = false,
            AiAffectionFloor = 35,
            AiAffectionGainPerChat = 2.5,
            AiAffectionDecayPerDay = 5,
            AiAffectionDecayAfterHours = 12,
            AiAffectionModelDelta = false,
        };
        var json = JsonFileStore.Serialize(cfg);
        var restored = JsonSerializer.Deserialize<Live2DConfig>(json, JsonFileStore.Options);
        C("AI 配置 JSON 往返：全部 Ai* 配置项保持（Key 仍为密文）",
            restored is not null
            && restored.AiApiKeyProtected == "dpapi:qwertyuiop"
            && restored.AiBaseUrl == "https://proxy.example.com/"
            && restored.AiModel == "deepseek-v4-pro"
            && restored.AiThinkingLevel == "高"
            && restored.AiMaxTokens == 320
            && restored.AiHistoryTurns == 10
            && restored.AiPersonaId == "tsundere"
            && restored.AiPersonaCustom == "人格补充自检"
            && restored.AiAddressPreset == "老公"
            && restored.AiAddressCustom == "阿凉"
            && restored.AiSelfPreset == "本鱼"
            && restored.AiSelfCustom == "小鱼"
            && restored.AiAttributes == "容易害羞,爱吃醋"
            && restored.AiCustomSetting == "自我设定自检"
            && !restored.AiSpeakReply
            && !restored.AiMoodEffects
            && restored.AiChatWindowOpen
            && !restored.AiChatAutoOpen
            && restored.AiProactiveEnabled
            && Near(restored.AiProactiveIntervalMinutes, 45)
            && !restored.AiIdleReminderEnabled
            && Near(restored.AiIdleSilenceMinutes, 30)
            && !restored.AiAffectionNatural
            && !restored.AiAffectionFloorEnabled
            && Near(restored.AiAffectionFloor, 35)
            && Near(restored.AiAffectionGainPerChat, 2.5)
            && Near(restored.AiAffectionDecayPerDay, 5)
            && Near(restored.AiAffectionDecayAfterHours, 12)
            && !restored.AiAffectionModelDelta,
            restored is null ? "反序列化为 null" : $"JSON {json.Length} 字");

        C("AI 配置 JSON 不含运行时 Store / FilePath（ToolConfig 的 JsonIgnore 生效）",
            !json.Contains("\"store\"", StringComparison.OrdinalIgnoreCase)
            && !json.Contains("\"filePath\"", StringComparison.OrdinalIgnoreCase));
    }

    // ====================================================================
    // 聊天服务构造 / 假 HTTP handler / 小工具
    // ====================================================================

    /// <summary>
    /// 自检断言委托：带默认 <paramref name="detail"/>，组内可以只写「检查名 + 布尔」。
    /// （<see cref="Action{T1,T2,T3}"/> 的默认参数在调用点不生效，所以单独声明一个委托类型。）
    /// </summary>
    private delegate void CheckFn(string name, bool ok, string detail = "");

    /// <summary>构造一个走假 handler + 临时 Storage 的聊天服务（配置回调都「现读」局部变量）。</summary>
    private static AiChatService CreateService(
        ToolStorage storage,
        FakeChatHandler handler,
        Func<AiRequestOptions> requestOptions,
        Func<AiPersonaSettings>? persona = null,
        Func<AffectionSettings>? affection = null,
        Func<int>? historyTurns = null,
        Func<AiProactiveSettings>? proactive = null)
    {
        return new AiChatService(new AiChatServiceOptions
        {
            Storage = storage,
            HttpHandler = handler,
            RequestOptions = requestOptions,
            Persona = persona ?? (() => new AiPersonaSettings()),
            Affection = affection,
            HistoryTurns = historyTurns,
            Proactive = proactive,
            OfflineFallback = text => "离线兜底：" + text,
        });
    }

    /// <summary>同步等待异步操作（自检控制台没有 SynchronizationContext，不会死锁）。</summary>
    private static T Run<T>(Func<Task<T>> action) => action().GetAwaiter().GetResult();

    /// <summary>轮询等待条件成立（主动说话是 fire-and-forget，要等事件）。</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (condition()) return true;
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(25);
        }
    }

    /// <summary>线程安全读事件数量（事件可能在后台线程触发）。</summary>
    private static int ReadCount(List<AiReplyEventArgs> events)
    {
        lock (events) return events.Count;
    }

    /// <summary>浮点近似相等（配置 / 好感度用）。</summary>
    private static bool Near(double actual, double expected, double tolerance = 1e-9)
        => Math.Abs(actual - expected) <= tolerance;

    /// <summary>把请求体当 JSON 读一个整数字段（自检断言请求参数用）。</summary>
    private static int ReadJsonInt(string json, string propertyName)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty(propertyName).GetInt32();
    }

    /// <summary>读请求体里 messages 数组的长度（历史裁剪断言用）。</summary>
    private static int ReadMessagesCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("messages").GetArrayLength();
    }

    /// <summary>构造固定 chat completion JSON（content 会被正确转义成 JSON 字符串）。</summary>
    private static string ChatCompletionJson(string content, int promptTokens, int completionTokens)
    {
        return "{\"id\":\"chatcmpl-selftest\",\"object\":\"chat.completion\",\"model\":\"deepseek-flash\"," +
               "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" +
               JsonSerializer.Serialize(content) +
               "},\"finish_reason\":\"stop\"}]," +
               "\"usage\":{\"prompt_tokens\":" + promptTokens +
               ",\"completion_tokens\":" + completionTokens +
               ",\"total_tokens\":" + (promptTokens + completionTokens) + "}}";
    }

    /// <summary>构造 HTTP 响应（默认 200 + JSON）。</summary>
    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>
    /// 自检用假 <see cref="HttpMessageHandler"/>：不发真实网络请求，按调用序号返回固定响应，
    /// 并记录请求体与 Authorization Header（断言「Key 只在 Header」用）。
    /// </summary>
    private sealed class FakeChatHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly object _lock = new();
        private readonly List<string> _bodies = new();
        private readonly List<string> _authorizations = new();
        private int _callCount;

        public FakeChatHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        /// <summary>累计请求次数。</summary>
        public int CallCount => Volatile.Read(ref _callCount);

        /// <summary>每次请求的 body（按调用顺序）。</summary>
        public IReadOnlyList<string> Bodies
        {
            get { lock (_lock) return _bodies.ToArray(); }
        }

        /// <summary>每次请求的 Authorization Header（按调用顺序）。</summary>
        public IReadOnlyList<string> Authorizations
        {
            get { lock (_lock) return _authorizations.ToArray(); }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var authorization = request.Headers.Authorization?.ToString() ?? "";

            int index;
            lock (_lock)
            {
                index = _callCount++;
                _bodies.Add(body);
                _authorizations.Add(authorization);
            }

            return _responder(index, request);
        }
    }
}
