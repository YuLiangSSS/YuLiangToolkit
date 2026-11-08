using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace YuLiangToolkit.Tools.Live2D.Speech;

/// <summary>
/// 一句语音的「语气参数包」：在全局语速 / 音调 / 音量之上，再按场景做一次覆盖。
///
/// <para>字段含义（都相对 <see cref="TtsService"/> 的全局设置再乘/加）：</para>
/// <list type="bullet">
///   <item><see cref="Style"/>：Edge 的 <c>mstts:express-as</c> 情感风格名（gentle / cheerful / embarrassed /
///         angry / sad / affectionate / serious / chat …）；空串 = 不用风格（老行为）。**晓伊等音色不一定都支持，
///         失败会自动回退无风格重试**（见 <see cref="EdgeTtsClient.LastStyleFallback"/>）。</item>
///   <item><see cref="StyleDegree"/>：风格强度（0.5 ~ 2.0，越大越夸张）。</item>
///   <item><see cref="Rate"/>：语速倍率（1.0 = 不变；1.12 = 快 12%）。</item>
///   <item><see cref="Pitch"/>：音调偏移百分比（沿用现有「百分比 ×1.2 → Hz」换算）。</item>
///   <item><see cref="Volume"/>：音量倍率（0.6 ~ 1.5，最终夹 0~1 播放音量；只影响播放，不写进 SSML 音量）。</item>
///   <item><see cref="BreakAfterExclaimMs"/>：在第一个 <c>！</c> 后面插一个 SSML 停顿（毫秒），
///         用来做「呀！……（小声）」这种先炸一下再弱下来的效果；0 = 不加。</item>
/// </list>
/// </summary>
public sealed record SpeechTone
{
    /// <summary>不做任何覆盖（保持全局设置）。</summary>
    public static SpeechTone Neutral { get; } = new();

    /// <summary>Edge 情感风格名（空 = 不用风格）。</summary>
    public string Style { get; init; } = "";

    /// <summary>风格强度（0.5 ~ 2.0）。</summary>
    public double StyleDegree { get; init; } = 1.6;

    /// <summary>语速倍率（相对全局语速）。</summary>
    public double Rate { get; init; } = 1.0;

    /// <summary>音调偏移（百分比，相对全局音调）。</summary>
    public double Pitch { get; init; }

    /// <summary>音量倍率（相对全局音量）。</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>第一个感叹号后插入的停顿（毫秒；0 = 不加）。</summary>
    public int BreakAfterExclaimMs { get; init; }

    /// <summary>
    /// 「先炸一下再弱下来」：把第一个感叹号前的短句用更快 / 更高 / 更响的 prosody 念，
    /// 后面插停顿，再把剩下的部分放慢 / 放低 / 放轻。只靠 SSML prosody 实现（Edge 不支持情感风格也能听出来）。
    /// </summary>
    public bool PunchFirstExclaim { get; init; }

    /// <summary>在每个「…」后插入的停顿（毫秒；0 = 不加；最多处理前 3 个，避免拖沓）。</summary>
    public int BreakAtEllipsisMs { get; init; }

    /// <summary>有没有情感风格。</summary>
    public bool HasStyle => !string.IsNullOrWhiteSpace(Style);

    /// <summary>去掉情感风格（本地 TTS / 风格回退时用）。</summary>
    public SpeechTone WithoutStyle() => this with { Style = "", StyleDegree = 1.6 };

    /// <summary>缓存键（相同文本 + 不同语气不能串音）。</summary>
    public string CacheKey =>
        Style + "/" + StyleDegree.ToString("0.##", CultureInfo.InvariantCulture) + "/" +
        Rate.ToString("0.###", CultureInfo.InvariantCulture) + "/" +
        Pitch.ToString("0.##", CultureInfo.InvariantCulture) + "/" +
        Volume.ToString("0.###", CultureInfo.InvariantCulture) + "/" +
        BreakAfterExclaimMs.ToString(CultureInfo.InvariantCulture) + "/" +
        (PunchFirstExclaim ? "punch" : "-") + "/" +
        BreakAtEllipsisMs.ToString(CultureInfo.InvariantCulture);

    /// <summary>语速倍率 → Edge 的 <c>+N%</c>（夹 0.5~2.0）。</summary>
    public static string FormatRate(double multiplier)
    {
        var percent = (int)Math.Round((Math.Clamp(multiplier, 0.5, 2.0) - 1.0) * 100.0);
        return (percent >= 0 ? "+" : "") + percent.ToString(CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>音调百分比 → Edge 的 <c>+NHz</c>（沿用现有 ×1.2 换算；夹 -50~80）。</summary>
    public static string FormatPitch(double percent)
    {
        var hz = (int)Math.Round(Math.Clamp(percent, -50, 80) * 1.2);
        return (hz >= 0 ? "+" : "") + hz.ToString(CultureInfo.InvariantCulture) + "Hz";
    }

    /// <summary>本语气实际用的语速参数。</summary>
    public string RateArg => FormatRate(Rate);

    /// <summary>本语气实际用的音调参数。</summary>
    public string PitchArg => FormatPitch(Pitch);
}

/// <summary>试听对比用的一条样本。</summary>
public sealed record SpeechAuditionSample(
    string Id,
    string VoiceShortName,
    string VoiceLabel,
    string Scene,
    string Text,
    SpeechTone Tone,
    bool IsBaseline);

/// <summary>一句话拆出来的一个韵律片段（分段合成用：每段可以有自己的语速 / 音调，段间插静音）。</summary>
public sealed record SpeechSegment(string Text, double RateMultiplier, double PitchOffset, int GapAfterMs);

/// <summary>
/// 语气目录：把「互动事件 / 投喂档位 / AI 情绪 / 饥饿」映射成 <see cref="SpeechTone"/>，
/// 以及试听对比要合成哪些样本。改语气只需要改这里。
///
/// <para><b>Edge 实测</b>：朗读接口不接受 <c>mstts:express-as</c>（情感风格）也不接受 <c>&lt;break&gt;</c>
/// （会直接返回空音频）。所以不换引擎时，情绪只能靠：**音色 + 语速 / 音调 / 音量 + 按标点分段合成**
/// （每段不同韵律，段间插静音）。<see cref="BuildSegments"/> 就是做这个的；情感风格字段留给
/// Azure / 商用 TTS 路线用。</para>
/// </summary>
public static class SpeechToneCatalog
{
    public const string VoiceXiaoyi = "zh-CN-XiaoyiNeural";
    public const string VoiceXiaoxiao = "zh-CN-XiaoxiaoNeural";
    public const string VoiceXiaobei = "zh-CN-liaoning-XiaobeiNeural";
    public const string VoiceHiuYu = "zh-TW-HsiaoYuNeural";

    // ---------- 通用 / AI 聊天 ----------

    /// <summary>普通平静（默认、日志 / 系统提示用）。</summary>
    public static SpeechTone Neutral { get; } = SpeechTone.Neutral;

    /// <summary>温柔软妹（闲聊、待机、普通 AI 回复）。</summary>
    public static SpeechTone Gentle { get; } = new()
    {
        Style = "gentle", StyleDegree = 1.3, Rate = 0.97, Pitch = 2, Volume = 1.0,
        BreakAtEllipsisMs = 90,
    };

    /// <summary>开心 / 元气。</summary>
    public static SpeechTone Cheerful { get; } = new()
    {
        Style = "cheerful", StyleDegree = 1.6, Rate = 1.04, Pitch = 4, Volume = 1.02,
    };

    /// <summary>害羞 / 脸红。</summary>
    public static SpeechTone Shy { get; } = new()
    {
        Style = "embarrassed", StyleDegree = 1.8, Rate = 0.96, Pitch = 5, Volume = 0.95,
    };

    /// <summary>心动 / 撒娇。</summary>
    public static SpeechTone Affectionate { get; } = new()
    {
        Style = "affectionate", StyleDegree = 1.9, Rate = 0.94, Pitch = 6, Volume = 0.98,
        BreakAtEllipsisMs = 130,
    };

    /// <summary>生气 / 吃醋（略快、略大声）。</summary>
    public static SpeechTone Angry { get; } = new()
    {
        Style = "angry", StyleDegree = 1.4, Rate = 1.06, Pitch = 3, Volume = 1.06,
    };

    /// <summary>难过 / 委屈。</summary>
    public static SpeechTone Sad { get; } = new()
    {
        Style = "sad", StyleDegree = 1.7, Rate = 0.9, Pitch = -3, Volume = 0.92,
        BreakAtEllipsisMs = 150,
    };

    /// <summary>
    /// 被捏 / 被吓一跳：先「呀！」用更快更高更响的 prosody 炸一下，停顿 130ms，
    /// 后面「别、别捏啦……」放慢放低放轻。
    /// </summary>
    public static SpeechTone Startled { get; } = new()
    {
        Style = "embarrassed", StyleDegree = 2.0, Rate = 1.10, Pitch = 9, Volume = 1.08,
        PunchFirstExclaim = true, BreakAtEllipsisMs = 80,
    };

    /// <summary>惊讶 / 兴奋。</summary>
    public static SpeechTone Surprised { get; } = new()
    {
        Style = "cheerful", StyleDegree = 2.0, Rate = 1.1, Pitch = 9, Volume = 1.05,
    };

    // ---------- 场景映射 ----------

    /// <summary>互动热区事件 → 语气（pat_head / pinch / tickle / protect / face_cycle / hand）。</summary>
    public static SpeechTone ForInteraction(string? voiceEvent) => (voiceEvent ?? "").Trim().ToLowerInvariant() switch
    {
        "pat_head" => Gentle with { Rate = 0.93, Pitch = 6, Volume = 0.95 },
        "pinch" => Startled,
        "tickle" => Cheerful with { Rate = 1.1, Pitch = 8, Volume = 1.05, StyleDegree = 1.8 },
        "protect" => Angry with { Rate = 1.04, Pitch = 2 },
        "face_cycle" => Cheerful,
        "hand" => Shy,
        _ => Gentle,
    };

    /// <summary>AI 回复情绪标签 → 语气。</summary>
    public static SpeechTone ForAiEmotion(string? emotion)
    {
        var tag = (emotion ?? "").Trim().ToLowerInvariant();
        if (tag.Contains("love") || tag.Contains("heart") || tag.Contains("爱")) return Affectionate;
        if (tag.Contains("shy") || tag.Contains("害羞") || tag.Contains("blush")) return Shy;
        if (tag.Contains("happy") || tag.Contains("cheer") || tag.Contains("开心") || tag.Contains("笑")) return Cheerful;
        if (tag.Contains("jealous") || tag.Contains("醋")) return Angry with { StyleDegree = 1.8 };
        if (tag.Contains("angry") || tag.Contains("生气") || tag.Contains("怒")) return Angry;
        if (tag.Contains("sad") || tag.Contains("难过") || tag.Contains("哭")) return Sad;
        if (tag.Contains("surprised") || tag.Contains("惊讶") || tag.Contains("惊")) return Surprised;
        if (tag.Contains("cold") || tag.Contains("黑脸") || tag.Contains("冷")) return Neutral with { Rate = 0.98, Pitch = -2 };
        return Gentle;
    }

    /// <summary>吃完之后：很饱 / 没吃饱 / 很饿。</summary>
    public static SpeechTone ForFeedSatisfied() => Cheerful with { Rate = 1.02, StyleDegree = 1.7 };

    public static SpeechTone ForFeedStillHungry() => Gentle with { Rate = 0.92, Pitch = 1, Volume = 0.93 };

    public static SpeechTone ForFeedStarving() => Sad with { Rate = 0.86, Pitch = -4, Volume = 0.88 };

    /// <summary>饿了主动说话。</summary>
    public static SpeechTone ForHungryReminder() => Sad with { Rate = 0.88, Pitch = -3, Volume = 0.85 };

    /// <summary>空闲随机说话 / 离线轻对话。</summary>
    public static SpeechTone ForIdle() => Gentle with { Rate = 0.98 };

    // ---------- 分段韵律（Edge 不支持 <break> / 情感风格时的替代方案） ----------

    /// <summary>
    /// 把一句话按标点切成 1~8 个片段，并给每段配「语速倍率 / 音调偏移 / 段后静音毫秒」。
    /// <para>规则（自检按这个断言）：</para>
    /// <list type="bullet">
    ///   <item>只在句末标点（！？。…；;、换行）后切；逗号 / 顿号不切，避免太碎；</item>
    ///   <item>只有一个片段（没有句末标点）时返回单段 (1.0, 0, 0) = 保持整句原样；</item>
    ///   <item><see cref="SpeechTone.PunchFirstExclaim"/> 时第一段加速 16% / 升调 +9，段后停 150ms；</item>
    ///   <item>以「！」结尾的段：加速 6% / 升调 +4 / 停 110ms；「？」：+2% / +3 / 95ms；
    ///         「…」：-5% / -1 / 停 <see cref="SpeechTone.BreakAtEllipsisMs"/>（0 时用 120ms）；
    ///          「。」：停 80ms；「；」：停 70ms；最后一段段后静音一律 0；</item>
    ///   <item>从第二段起隔段再放慢 2% / 降 1（避免整句一个味）；最多 8 段，多出来的并进最后一段。</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<SpeechSegment> BuildSegments(string? text, SpeechTone? tone)
    {
        var source = text ?? "";
        var clauses = SplitClauses(source);
        if (clauses.Count <= 1)
        {
            return new[] { new SpeechSegment(source, 1.0, 0, 0) };
        }

        if (clauses.Count > 8)
        {
            var merged = clauses.Take(7).ToList();
            merged.Add(string.Concat(clauses.Skip(7)));
            clauses = merged;
        }

        var punch = tone?.PunchFirstExclaim == true;
        var ellipsisGap = tone?.BreakAtEllipsisMs > 0 ? tone.BreakAtEllipsisMs : 120;
        var segments = new List<SpeechSegment>(clauses.Count);
        for (var i = 0; i < clauses.Count; i++)
        {
            var clause = clauses[i];
            var isLast = i == clauses.Count - 1;
            double rate = 1.0, pitch = 0.0;
            int gap;
            if (punch && i == 0)
            {
                rate = 1.16;
                pitch = 9;
                gap = 150;
            }
            else if (EndsWithAny(clause, '！', '!'))
            {
                rate = 1.06;
                pitch = 4;
                gap = 110;
            }
            else if (EndsWithAny(clause, '？', '?'))
            {
                rate = 1.02;
                pitch = 3;
                gap = 95;
            }
            else if (EndsWithAny(clause, '…'))
            {
                rate = 0.95;
                pitch = -1;
                gap = ellipsisGap;
            }
            else if (EndsWithAny(clause, '。'))
            {
                gap = 80;
            }
            else if (EndsWithAny(clause, '；', ';'))
            {
                gap = 70;
            }
            else
            {
                gap = 40;
            }

            if (i > 0 && (i & 1) == 1)
            {
                rate *= 0.98;
                pitch -= 1;
            }

            if (isLast) gap = 0;
            segments.Add(new SpeechSegment(clause, rate, pitch, gap));
        }
        return segments;
    }

    /// <summary>按句末标点切句（标点留在前一段；逗号 / 顿号不切）。</summary>
    private static List<string> SplitClauses(string text)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        void Flush()
        {
            if (current.Length == 0) return;
            result.Add(current.ToString());
            current.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                Flush();
                continue;
            }

            current.Append(c);
            if (!IsSentenceEnd(c)) continue;

            // 连续标点（「……」「！？」）归到同一段
            while (i + 1 < text.Length && IsSentenceEnd(text[i + 1]))
            {
                current.Append(text[++i]);
            }
            Flush();
        }

        Flush();
        return result;
    }

    private static bool IsSentenceEnd(char c)
        => c is '！' or '!' or '？' or '?' or '。' or '…' or '；' or ';';

    private static bool EndsWithAny(string text, params char[] ends)
    {
        if (text.Length == 0) return false;
        var last = text[^1];
        foreach (var c in ends)
        {
            if (last == c) return true;
        }
        return false;
    }

    // ---------- 试听对比样本 ----------

    /// <summary>试听用音色（都在 <see cref="EdgeTtsClient.Voices"/> 内置清单里）。</summary>
    public static IReadOnlyList<(string ShortName, string Label)> AuditionVoices { get; } = new[]
    {
        (VoiceXiaoyi, "晓伊 · 少女音（当前默认）"),
        (VoiceXiaoxiao, "晓晓 · 温柔女声"),
        (VoiceXiaobei, "晓北 · 东北甜妹"),
        (VoiceHiuYu, "晓雨 · 台湾甜妹"),
    };

    /// <summary>
    /// 试听台词与语气：同一条台词既给「原始（无风格）」也给「语气化」，方便 A/B 对比。
    /// </summary>
    public static IReadOnlyList<(string Id, string Scene, string Text, SpeechTone Tone, bool Baseline)> AuditionLines { get; } =
        new (string Id, string Scene, string Text, SpeechTone Tone, bool Baseline)[]
        {
            ("pinch", "被捏脸（呀！+ 停顿 + 小声）", "呀！别、别捏啦……", Startled, false),
            ("pat", "摸头（温柔害羞）", "诶嘿…摸头会让我变笨的啦…再摸一下也不是不行。", ForInteraction("pat_head"), false),
            ("wait", "等待（原始，无风格，用于对比）", "主人今天回来得好晚…我一直趴在桌角等你呢。", SpeechTone.Neutral, true),
            ("wait_affection", "等待（心动/撒娇语气）", "主人今天回来得好晚…我一直趴在桌角等你呢。", Affectionate, false),
            ("hungry", "好饿（委屈）", "呜…好饿啊…有饭团吗？", ForHungryReminder(), false),
            ("tsundere", "傲娇（生气嘴硬）", "哼！才、才没有害羞呢！", Angry, false),
        };

    /// <summary>按 4 个音色 × 6 条台词展开成试听样本（24 个）。</summary>
    public static IReadOnlyList<SpeechAuditionSample> BuildAuditionSamples()
    {
        var list = new List<SpeechAuditionSample>();
        var index = 0;
        foreach ((string shortName, string label) in AuditionVoices)
        {
            foreach ((string id, string scene, string text, SpeechTone tone, bool baseline) in AuditionLines)
            {
                index++;
                list.Add(new SpeechAuditionSample(
                    $"{index:00}_{VoiceSlug(shortName)}_{id}",
                    shortName,
                    label,
                    scene,
                    text,
                    tone,
                    baseline));
            }
        }
        return list;
    }

    /// <summary>音色 ShortName → 文件名用的短标识（去掉 zh-CN- 前缀和 Neural）。</summary>
    public static string VoiceSlug(string shortName)
    {
        var slug = (shortName ?? "").Trim();
        var lastDash = slug.LastIndexOf('-');
        if (lastDash >= 0) slug = slug[(lastDash + 1)..];
        if (slug.EndsWith("Neural", StringComparison.OrdinalIgnoreCase))
            slug = slug[..^"Neural".Length];
        return slug.Length == 0 ? "voice" : slug;
    }
}
