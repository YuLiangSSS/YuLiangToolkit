using System;
using System.Collections.Generic;
using System.Linq;
using YuLiangToolkit.Tools.Live2D.Speech;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「语音语气 / 分段韵律」自检：<see cref="SpeechTone"/> 参数换算、<see cref="SpeechToneCatalog"/> 场景映射、
/// 按标点分段、WAV 拼接器、SSML 组装、缓存键区分。全部纯逻辑、不联网、不弹窗。
/// </summary>
internal static class Live2DTtsToneChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkSimple)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        // ==================================================================
        // 一、SpeechTone 参数换算
        // ==================================================================
        C("语气：语速倍率 → Edge 参数（+0% / +12% / -8%）",
            SpeechTone.FormatRate(1.0) == "+0%" &&
            SpeechTone.FormatRate(1.12) == "+12%" &&
            SpeechTone.FormatRate(0.92) == "-8%",
            $"{SpeechTone.FormatRate(1.0)} / {SpeechTone.FormatRate(1.12)} / {SpeechTone.FormatRate(0.92)}");

        C("语气：语速倍率夹取（0.1 → 0.5，3.0 → 2.0）",
            SpeechTone.FormatRate(0.1) == "-50%" && SpeechTone.FormatRate(3.0) == "+100%",
            $"{SpeechTone.FormatRate(0.1)} / {SpeechTone.FormatRate(3.0)}");

        C("语气：音调百分比 → Edge 参数（×1.2 换算）",
            SpeechTone.FormatPitch(0) == "+0Hz" &&
            SpeechTone.FormatPitch(10) == "+12Hz" &&
            SpeechTone.FormatPitch(-5) == "-6Hz",
            $"{SpeechTone.FormatPitch(0)} / {SpeechTone.FormatPitch(10)} / {SpeechTone.FormatPitch(-5)}");

        C("语气：音调夹取（-100 → -60Hz，200 → +96Hz）",
            SpeechTone.FormatPitch(-100) == "-60Hz" && SpeechTone.FormatPitch(200) == "+96Hz",
            $"{SpeechTone.FormatPitch(-100)} / {SpeechTone.FormatPitch(200)}");

        var neutralKey = SpeechTone.Neutral.CacheKey;
        var shy = SpeechToneCatalog.Shy;
        C("语气：不同语气的缓存键不同（同文本不串音）",
            neutralKey != shy.CacheKey &&
            shy.CacheKey.Contains("embarrassed", StringComparison.Ordinal),
            shy.CacheKey);

        C("语气：WithoutStyle 去掉情感风格但保留韵律",
            !SpeechToneCatalog.Startled.WithoutStyle().HasStyle &&
            SpeechToneCatalog.Startled.WithoutStyle().PunchFirstExclaim &&
            Math.Abs(SpeechToneCatalog.Startled.WithoutStyle().Rate - SpeechToneCatalog.Startled.Rate) < 0.0001,
            SpeechToneCatalog.Startled.WithoutStyle().CacheKey);

        C("语气：TtsService 缓存键包含语气（同一句不同语气键不同）",
            TtsService.BuildCacheKey("edge:zh-CN-XiaoyiNeural", 0.95, 6, "你好", SpeechToneCatalog.Shy) !=
            TtsService.BuildCacheKey("edge:zh-CN-XiaoyiNeural", 0.95, 6, "你好", SpeechToneCatalog.Angry),
            "");

        // ==================================================================
        // 二、场景映射表
        // ==================================================================
        C("场景映射：摸头 = 温柔（语速 <1）且不开「呀！」重音",
            !SpeechToneCatalog.ForInteraction("pat_head").PunchFirstExclaim &&
            SpeechToneCatalog.ForInteraction("pat_head").Rate < 1.0 &&
            SpeechToneCatalog.ForInteraction("pat_head").Style == "gentle",
            SpeechToneCatalog.ForInteraction("pat_head").CacheKey);

        C("场景映射：捏脸 = 惊讶重音（punch + 更高音调 + 更快），比摸头更激烈",
            SpeechToneCatalog.ForInteraction("pinch").PunchFirstExclaim &&
            SpeechToneCatalog.ForInteraction("pinch").Pitch > SpeechToneCatalog.ForInteraction("pat_head").Pitch &&
            SpeechToneCatalog.ForInteraction("pinch").Rate > SpeechToneCatalog.ForInteraction("pat_head").Rate,
            SpeechToneCatalog.ForInteraction("pinch").CacheKey);

        C("场景映射：挠痒/护住/点手/换表情都有非空语气",
            new[] { "tickle", "protect", "hand", "face_cycle" }
                .All(e => SpeechToneCatalog.ForInteraction(e).HasStyle || SpeechToneCatalog.ForInteraction(e).Rate != 1.0),
            "");

        C("场景映射：AI 情绪 → 语气（love→撒娇 / shy→害羞 / angry→生气 / sad→难过 / neutral→温柔）",
            SpeechToneCatalog.ForAiEmotion("love").Style == "affectionate" &&
            SpeechToneCatalog.ForAiEmotion("shy").Style == "embarrassed" &&
            SpeechToneCatalog.ForAiEmotion("angry").Style == "angry" &&
            SpeechToneCatalog.ForAiEmotion("sad").Style == "sad" &&
            SpeechToneCatalog.ForAiEmotion("").Style == "gentle",
            "");

        C("场景映射：投喂三档语气不同（很饱更快更高 / 很饿更慢更低）",
            SpeechToneCatalog.ForFeedSatisfied().Rate > SpeechToneCatalog.ForFeedStillHungry().Rate &&
            SpeechToneCatalog.ForFeedStillHungry().Rate > SpeechToneCatalog.ForFeedStarving().Rate &&
            SpeechToneCatalog.ForFeedStarving().Pitch < SpeechToneCatalog.ForFeedSatisfied().Pitch,
            "");

        C("场景映射：饿了主动说话 = 慢 + 低 + 小音量",
            SpeechToneCatalog.ForHungryReminder().Rate < 0.95 &&
            SpeechToneCatalog.ForHungryReminder().Pitch < 0 &&
            SpeechToneCatalog.ForHungryReminder().Volume < 1.0,
            SpeechToneCatalog.ForHungryReminder().CacheKey);

        // ==================================================================
        // 三、按标点分段（Edge 不支持 <break> 的替代方案）
        // ==================================================================
        var noPunctuation = SpeechToneCatalog.BuildSegments("你好呀", SpeechToneCatalog.Gentle);
        C("分段：没有句末标点时只有一段，且不做额外韵律变化",
            noPunctuation.Count == 1 &&
            Math.Abs(noPunctuation[0].RateMultiplier - 1.0) < 0.0001 &&
            noPunctuation[0].PitchOffset == 0 &&
            noPunctuation[0].GapAfterMs == 0,
            $"段数={noPunctuation.Count}");

        var pinch = SpeechToneCatalog.BuildSegments("呀！别、别捏啦……", SpeechToneCatalog.Startled);
        C("分段：「呀！别、别捏啦……」切成 2 段（第一段重音、段后静音 > 0，最后一段静音=0）",
            pinch.Count == 2 &&
            pinch[0].Text == "呀！" &&
            pinch[0].RateMultiplier > 1.1 &&
            pinch[0].PitchOffset > 0 &&
            pinch[0].GapAfterMs > 0 &&
            pinch[1].GapAfterMs == 0,
            $"段数={pinch.Count}，第一段={pinch[0]}");

        var concatenated = string.Concat(pinch.Select(s => s.Text));
        C("分段：把片段拼回去等于原句（不丢字 / 不重复）",
            concatenated == "呀！别、别捏啦……",
            concatenated);

        var sleepy = SpeechToneCatalog.BuildSegments("唔…好困…想睡了。", SpeechToneCatalog.Gentle);
        C("分段：省略号后停顿取语气里的 BreakAtEllipsisMs",
            sleepy.Count == 3 &&
            sleepy[0].GapAfterMs == SpeechToneCatalog.Gentle.BreakAtEllipsisMs &&
            sleepy[2].GapAfterMs == 0,
            $"段数={sleepy.Count}，gaps={string.Join(",", sleepy.Select(s => s.GapAfterMs))}");

        var many = SpeechToneCatalog.BuildSegments("一。二。三。四。五。六。七。八。九。十。", SpeechToneCatalog.Gentle);
        C("分段：最多 8 段（多出来的并进最后一段）",
            many.Count == 8 &&
            string.Concat(many.Select(s => s.Text)) == "一。二。三。四。五。六。七。八。九。十。",
            $"段数={many.Count}");

        C("分段：空文本返回单段空串不抛异常",
            SpeechToneCatalog.BuildSegments("", SpeechToneCatalog.Gentle).Count == 1,
            "");

        // ==================================================================
        // 四、SSML 组装（情感风格 / 重音 / 省略号停顿）
        // ==================================================================
        var plain = EdgeTtsClient.BuildSsmlMessage("你好", "zh-CN-XiaoyiNeural", "+0%", "+0Hz");
        C("SSML：无情感风格时不出现 mstts:express-as，且保留 prosody",
            !plain.Contains("mstts", StringComparison.OrdinalIgnoreCase) &&
            plain.Contains("<prosody", StringComparison.Ordinal) &&
            plain.Contains("rate='+0%'", StringComparison.Ordinal),
            plain.Length > 200 ? plain[^200..] : plain);

        var styled = EdgeTtsClient.BuildSsmlMessage("你好", "zh-CN-XiaoyiNeural", "+5%", "+6Hz",
            "gentle", 1.6);
        C("SSML：有情感风格时包含 mstts 命名空间 / express-as / styledegree",
            styled.Contains("xmlns:mstts='https://www.w3.org/2001/mstts'", StringComparison.Ordinal) &&
            styled.Contains("<mstts:express-as style='gentle' styledegree='1.6'>", StringComparison.Ordinal) &&
            styled.Contains("rate='+5%'", StringComparison.Ordinal),
            "");

        var punched = EdgeTtsClient.BuildSsmlMessage("呀！别捏啦", "zh-CN-XiaoyiNeural", "+0%", "+0Hz",
            null, 1.0, 0, punchFirstExclaim: true);
        C("SSML：punchFirstExclaim 会拆成「重音段 + break + 放轻段」",
            punched.Contains("rate='+18%'", StringComparison.Ordinal) &&
            punched.Contains("<break time='130ms'/>", StringComparison.Ordinal) &&
            punched.Contains("rate='-8%'", StringComparison.Ordinal),
            "");

        var broken = EdgeTtsClient.BuildSsmlMessage("唔…好困…想睡…还不够…再来…", "zh-CN-XiaoyiNeural",
            "+0%", "+0Hz", null, 1.0, 0, false, 90);
        C("SSML：省略号后最多插 3 个停顿",
            broken.Split("<break time='90ms'/>", StringSplitOptions.None).Length - 1 == 3,
            "");

        var escaped = EdgeTtsClient.BuildSsmlMessage("A &amp; B", "zh-CN-XiaoyiNeural", "+0%", "+0Hz",
            "gentle</mstts:express-as>", 1.6);
        C("SSML：非法风格名被忽略（不会把任意字符串塞进标签）",
            !escaped.Contains("mstts", StringComparison.OrdinalIgnoreCase) &&
            !escaped.Contains("style=", StringComparison.Ordinal),
            "");

        // ==================================================================
        // 五、WAV 拼接（分段合成后重新打包）
        // ==================================================================
        var samples = new short[] { 0, 1000, -1000, 32767, -32768 };
        var wav = WaveAudio.BuildPcm16Wav(samples, 24000);
        C("WAV：BuildPcm16Wav 能被 TryParse 解析回来（单声道 / 16bit / 24k）",
            WaveAudio.TryParse(wav, out var info, out var error) &&
            info.SampleRate == 24000 && info.Channels == 1 && info.BitsPerSample == 16 &&
            info.FrameCount == samples.Length,
            error ?? $"{info.SampleRate}Hz/{info.Channels}ch/{info.BitsPerSample}bit/{info.FrameCount}帧");

        var readBack = WaveAudio.TryParse(wav, out var info2, out _)
            ? WaveAudio.ReadMonoSamples(wav, info2)
            : Array.Empty<short>();
        C("WAV：样本往返一致（含最大 / 最小值）",
            readBack.Length == samples.Length && readBack.SequenceEqual(samples),
            string.Join(",", readBack));

        // ==================================================================
        // 六、试听样本
        // ==================================================================
        var audition = SpeechToneCatalog.BuildAuditionSamples();
        C("试听：4 音色 × 6 台词 = 24 个样本，Id 唯一",
            audition.Count == 24 && audition.Select(s => s.Id).Distinct().Count() == 24,
            $"count={audition.Count}");

        C("试听：音色都在内置清单里，且文本非空",
            audition.All(s => EdgeTtsClient.Voices.Any(v => v.ShortName == s.VoiceShortName)) &&
            audition.All(s => s.Text.Length > 0),
            string.Join(",", SpeechToneCatalog.AuditionVoices.Select(v => v.ShortName)));

        C("试听：包含「原始基线」和「呀！重音」两类样本",
            audition.Any(s => s.IsBaseline) &&
            audition.Any(s => !s.IsBaseline && s.Tone.PunchFirstExclaim),
            $"baseline={audition.Count(s => s.IsBaseline)}");

        C("试听：文件名 slug 去掉前缀 / Neural（Xiaoyi / HsiaoYu）",
            SpeechToneCatalog.VoiceSlug("zh-CN-XiaoyiNeural") == "Xiaoyi" &&
            SpeechToneCatalog.VoiceSlug("zh-TW-HsiaoYuNeural") == "HsiaoYu",
            SpeechToneCatalog.VoiceSlug("zh-TW-HsiaoYuNeural"));
    }
}
