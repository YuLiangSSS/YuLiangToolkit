using System;
using System.Linq;
using YuLiangToolkit.Tools.Live2D;
using YuLiangToolkit.Tools.Live2D.Controls;
using YuLiangToolkit.Tools.Live2D.Model;
using YuLiangToolkit.Tools.Live2D.Play;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「互动动作 / 台词预设 + 聊天窗自由度 + 好感度试管满格」自检：
/// 好感度档位、InteractionScenarios 匹配、低好感不上亲密动作、台词去重、配置默认值、试管比例。
/// 全部纯逻辑、不联网、不弹窗、不碰真 Data。
/// </summary>
internal static class Live2DInteractionScenarioChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkSimple)
    {
        void C(string name, bool ok, string detail = "") => check(name, ok, detail);

        // ==================================================================
        // 一、好感度档位
        // ==================================================================
        C("互动档位：0~29 冷淡 / 30~59 普通 / 60~84 亲近 / 85+ 心动",
            InteractionScenarios.ResolveTier(0) == AffectionTier.Cold &&
            InteractionScenarios.ResolveTier(29.9) == AffectionTier.Cold &&
            InteractionScenarios.ResolveTier(30) == AffectionTier.Normal &&
            InteractionScenarios.ResolveTier(59.9) == AffectionTier.Normal &&
            InteractionScenarios.ResolveTier(60) == AffectionTier.Warm &&
            InteractionScenarios.ResolveTier(84.9) == AffectionTier.Warm &&
            InteractionScenarios.ResolveTier(85) == AffectionTier.Love &&
            InteractionScenarios.ResolveTier(100) == AffectionTier.Love,
            $"{InteractionScenarios.ResolveTier(0)}/{InteractionScenarios.ResolveTier(60)}/{InteractionScenarios.ResolveTier(85)}");

        C("互动档位：NaN 当 50（普通），不抛异常",
            InteractionScenarios.ResolveTier(double.NaN) == AffectionTier.Normal,
            "");

        // ==================================================================
        // 二、场景匹配：按好感度选门槛最高的一条
        // ==================================================================
        C("匹配：好感 10 摸头不触发亲密预设（默认礼貌反应）",
            InteractionScenarios.TryMatch("pat_head", 10) is null,
            "");

        C("匹配：好感 70 摸头 → pat_head.warm（只脸红，不前倾）",
            InteractionScenarios.TryMatch("pat_head", 70) is { Id: "pat_head.warm" } &&
            !InteractionScenarios.TryMatch("pat_head", 70)!.LeanForward,
            "");

        C("匹配：好感 90 摸头 → pat_head.love（爱心眼 + 脸红 + 前倾）",
            InteractionScenarios.TryMatch("pat_head", 90) is { Id: "pat_head.love", LeanForward: true } love &&
            love.Expressions.Contains("爱心眼") && love.Expressions.Contains("脸红"),
            "");

        C("匹配：好感 90 摸裙子 → skirt.love（脸红 + ＞＜ + 扭动 + 「不要这样啦」）",
            InteractionScenarios.TryMatch("skirt", 90) is { Id: "skirt.love" } skirt &&
            skirt.Expressions.Contains("脸红") && skirt.Expressions.Contains("＞＜") &&
            skirt.WiggleSeconds > 0 &&
            skirt.Lines.Any(l => l.Contains("不要这样啦", StringComparison.Ordinal)),
            "");

        C("匹配：低好感（20）摸裙子不会出现爱心眼 / 扭动",
            InteractionScenarios.TryMatch("skirt", 20) is null,
            "");

        C("匹配：高好感摸头不会爱心眼前倾发生在低好感角色身上（所有 Love 场景 MinAffection ≥ 85）",
            InteractionScenarios.All.Where(s => s.Expressions.Contains("爱心眼")).All(s => s.MinAffection >= 85),
            string.Join(",", InteractionScenarios.All.Where(s => s.Expressions.Contains("爱心眼")).Select(s => $"{s.Id}:{s.MinAffection}")));

        C("匹配：模板大小写不敏感 / 未知事件返回 null",
            InteractionScenarios.TryMatch("PAT_HEAD", 90) is { Id: "pat_head.love" } &&
            InteractionScenarios.TryMatch("not_a_region", 90) is null &&
            InteractionScenarios.TryMatch("", 90) is null,
            "");

        // ==================================================================
        // 三、目录数据完整性
        // ==================================================================
        C("预设：Id 唯一、事件/台词非空、门槛 0~100",
            InteractionScenarios.All.Select(s => s.Id).Distinct().Count() == InteractionScenarios.All.Count &&
            InteractionScenarios.All.All(s => s.VoiceEvent.Length > 0 && s.Lines.Length > 0 &&
                                              s.MinAffection is >= 0 and <= 100),
            $"count={InteractionScenarios.All.Count}");

        C("预设：同事件的多档门槛不重复（避免永远选不到低档）",
            InteractionScenarios.All
                .GroupBy(s => s.VoiceEvent, StringComparer.OrdinalIgnoreCase)
                .All(g => g.Select(s => s.MinAffection).Distinct().Count() == g.Count()),
            "");

        C("预设：扭动时长都在 0~6 秒（0 = 不扭）",
            InteractionScenarios.All.All(s => s.WiggleSeconds is >= 0 and <= 6),
            "");

        C("预设：每个表情名都是模型内置表情（Exp3 清单里的），且至少有一条前倾预设",
            new[] { "爱心眼", "脸红", "＞＜", "黑脸", "流冷汗", "流泪", "圈圈眼", "屑屑眼" }
                .All(name => InteractionScenarios.All.SelectMany(s => s.Expressions).Contains(name)) &&
            InteractionScenarios.All.Any(s => s.LeanForward),
            "");

        // ==================================================================
        // 四、台词去重
        // ==================================================================
        var scenario = InteractionScenarios.TryMatch("skirt", 90)!;
        var random = new Random(12345);
        var picks = Enumerable.Range(0, 6)
            .Select(_ => InteractionScenarios.PickLine(scenario, scenario.Lines[0], random))
            .ToArray();
        C("台词：PickLine 能挑到非上一句的台词",
            picks.Any(line => !string.Equals(line, scenario.Lines[0], StringComparison.Ordinal)),
            string.Join(" / ", picks));
        C("台词：PickLine 单句场景原样返回、空场景返回空串",
            InteractionScenarios.PickLine(
                new InteractionScenario("x", "x", 0, Array.Empty<string>(), 0, new[] { "唯一一句" }), "", random) == "唯一一句" &&
            InteractionScenarios.PickLine(
                new InteractionScenario("y", "y", 0, Array.Empty<string>(), 0, Array.Empty<string>()), "", random) == "",
            "");

        // ==================================================================
        // 五、聊天窗 / 好感度试管的配置与纯函数
        // ==================================================================
        var config = new Live2DConfig();
        C("聊天窗：默认跟随桌宠（AiChatWindowFollow=true），手动位置哨兵 = -99999",
            config.AiChatWindowFollow &&
            Math.Abs(config.AiChatWindowLeft + 99999) < 0.001 &&
            Math.Abs(config.AiChatWindowTop + 99999) < 0.001,
            $"{config.AiChatWindowFollow}/{config.AiChatWindowLeft}");

        C("试管：满好感（100）比例 = 1，空瓶（0）= 0，中间 50 = 0.5（满格时液面才会灌进爱心上半部）",
            Math.Abs(AffectionVial.LiquidRatio(100) - 1.0) < 0.0001 &&
            Math.Abs(AffectionVial.LiquidRatio(0)) < 0.0001 &&
            Math.Abs(AffectionVial.LiquidRatio(50) - 0.5) < 0.0001,
            AffectionVial.LiquidRatio(100).ToString("0.###"));

        C("试管：越界 / NaN 夹取（150→100、-20→0、NaN→50）",
            Math.Abs(AffectionVial.ClampValue(150) - 100) < 0.001 &&
            Math.Abs(AffectionVial.ClampValue(-20) - 0) < 0.001 &&
            Math.Abs(AffectionVial.ClampValue(double.NaN) - 50) < 0.001,
            AffectionVial.ClampValue(double.NaN).ToString("0.0"));

        // ==================================================================
        // 六、观察用户 / 逗你（场景 1 / 2：单眼瞪大、微眯、白眼、盯人、歪头）
        // ==================================================================
        C("观察：好感 10 → 嫌弃档（黑脸 + 不满脸 + 「脏东西黏鞋底」）",
            ObservationScenarios.TryMatch(ObservationScenarios.KindObserve, 10) is { Id: "observe.low" } low &&
            low.Expressions.Contains("黑脸") &&
            low.FacePoses.Contains("不满") &&
            low.Lines.Any(l => l.Contains("脏东西", StringComparison.Ordinal)),
            "");

        C("观察：好感 50 → 仔细看看档（单眼瞪大 / 双眼微眯 / 单眼闭眨 随机 + 前倾 + 歪头 + 盯人）",
            ObservationScenarios.TryMatch(ObservationScenarios.KindObserve, 50) is { Id: "observe.mid" } mid &&
            mid.FacePoses.Contains("单眼瞪大") && mid.FacePoses.Contains("双眼微眯") &&
            mid.FacePoses.Contains("单眼闭眨") &&
            mid.LeanForward && mid.LookAtUser && mid.TiltDegrees > 0 &&
            mid.Lines.Any(l => l.Contains("仔细看看", StringComparison.Ordinal)),
            "");

        C("观察：好感 92 → 可爱档（爱心眼 + 脸红 + 微眯偏睁 / 单眼闭眨 + 前倾 + 盯人）",
            ObservationScenarios.TryMatch(ObservationScenarios.KindObserve, 92) is { Id: "observe.high" } high &&
            high.Expressions.Contains("爱心眼") && high.Expressions.Contains("脸红") &&
            high.FacePoses.Contains("微眯偏睁") && high.FacePoses.Contains("单眼闭眨") &&
            high.LeanForward && high.LookAtUser,
            "");

        C("观察：脸预设清单包含「单眼闭眨」（一闭一睁 + 睁眼眨眼）",
            ObservationScenarios.FacePresetNames.Contains("单眼闭眨"),
            string.Join("/", ObservationScenarios.FacePresetNames));

        // ==================================================================
        // 六点五、单眼闭眨的眨眼计划：连眨 1~3 下，长动作两组
        // ==================================================================
        var shortPlan = ObservationBlinkPlan.Build(2.0, new Random(7));
        C("眨眼计划：短动作（2s）1 组，1~3 下，且动作结束前眨完",
            shortPlan.Count == 1 &&
            shortPlan[0].Count is >= 1 and <= 3 &&
            shortPlan[0].StartSeconds >= 0.5 &&
            shortPlan[0].EndSeconds <= 2.0 - 0.3,
            $"{shortPlan[0].StartSeconds:0.0}+{shortPlan[0].Count}下 → {shortPlan[0].EndSeconds:0.0}s");

        var longPlan = ObservationBlinkPlan.Build(4.0, new Random(7));
        C("眨眼计划：长动作（4s）2 组，各组 1~3 下，两组之间留间隔且结束前眨完",
            longPlan.Count == 2 &&
            longPlan.All(b => b.Count is >= 1 and <= 3) &&
            longPlan[1].StartSeconds > longPlan[0].EndSeconds + 0.5 &&
            longPlan[1].EndSeconds <= 4.0 - 0.3,
            string.Join(" / ", longPlan.Select(b => $"{b.StartSeconds:0.0}s+{b.Count}下")));

        var single = new[] { new ObservationBlinkPlan.Burst(0.5, 2) };
        C("眨眼脉冲：一组里闭→睁两次，间隔处为 0，组外为 0",
            ObservationBlinkPlan.BlinkPulse(0.5 + 0.17, single) > 0.8f &&
            ObservationBlinkPlan.BlinkPulse(0.5 + 0.5, single) < 0.2f &&
            ObservationBlinkPlan.BlinkPulse(1.6, single) < 0.2f &&
            ObservationBlinkPlan.BlinkPulse(5.0, single) == 0f &&
            ObservationBlinkPlan.BlinkPulse(-1, single) == 0f,
            "");

        C("逗你：好感 10 → 一直黑脸 + 白眼；好感 50 → 黑脸 50% 随机；好感 92 → 凑近 + 脸红 + 爱心眼",
            ObservationScenarios.TryMatch(ObservationScenarios.KindTease, 10) is { Id: "tease.low" } low2 &&
            low2.Expressions.Contains("黑脸") && !low2.RandomBlackFace &&
            ObservationScenarios.TryMatch(ObservationScenarios.KindTease, 50) is { RandomBlackFace: true } &&
            ObservationScenarios.TryMatch(ObservationScenarios.KindTease, 92) is { LeanForward: true } high2 &&
            high2.Expressions.Contains("爱心眼") && high2.Expressions.Contains("脸红"),
            "");

        C("观察/逗你：目录 Id 唯一、阈值不重复、脸预设名合法、台词非空",
            ObservationScenarios.All.Select(s => s.Id).Distinct().Count() == ObservationScenarios.All.Count &&
            ObservationScenarios.All
                .GroupBy(s => s.Kind, StringComparer.OrdinalIgnoreCase)
                .All(g => g.Select(s => s.MinAffection).Distinct().Count() == g.Count()) &&
            ObservationScenarios.All.All(s =>
                s.FacePoses.Length > 0 &&
                s.FacePoses.All(p => ObservationScenarios.FacePresetNames.Contains(p)) &&
                s.Lines.Length > 0),
            $"count={ObservationScenarios.All.Count}");

        C("观察台词：{user} 占位符存在（会被替换成用户称呼）",
            ObservationScenarios.All.SelectMany(s => s.Lines).Any(l => l.Contains("{user}", StringComparison.Ordinal)),
            "");

        C("观察台词：PickLine 不连续重复同一句",
            ObservationScenarios.TryMatch(ObservationScenarios.KindObserve, 50) is { } obs &&
            Enumerable.Range(0, 6)
                .Select(_ => ObservationScenarios.PickLine(obs, obs.Lines[0], new Random(42)))
                .Any(l => !string.Equals(l, obs.Lines[0], StringComparison.Ordinal)),
            "");

        C("盯人算法：桌宠在屏幕右边 → 眼球往左（X < 0），左边 → 往右（X > 0），正中 → 接近 0",
            UserGaze.Compute(1500, 500, 400, 800, 0, 0, 1920, 1080).X < -0.3 &&
            UserGaze.Compute(100, 500, 400, 800, 0, 0, 1920, 1080).X > 0.3 &&
            Math.Abs(UserGaze.Compute(760, 500, 400, 800, 0, 0, 1920, 1080).X) < 0.1,
            $"{UserGaze.Compute(1500, 500, 400, 800, 0, 0, 1920, 1080)}");

        C("盯人算法：桌宠在屏幕下半 → 用户在上方（Y > 0），并夹在 -1~1",
            UserGaze.Compute(700, 700, 400, 800, 0, 0, 1920, 1080).Y > 0.1 &&
            UserGaze.Compute(700, -5000, 100000, 100, 0, 0, 1920, 1080).Y <= 1f &&
            UserGaze.Compute(700, 500, 400, 800, 0, 0, 0, 0) == (0f, 0f),
            $"{UserGaze.Compute(700, 700, 400, 800, 0, 0, 1920, 1080)}");

        // ==================================================================
        // 七、动作时暂停鼠标跟随
        // ==================================================================
        C("鼠标跟随：做观察 / 手势 / 表演 / 舞蹈 / 互动时暂停（默认开）",
            ModelRuntime.ShouldPauseEyeTracking(true, observing: true, false, false, false, false) &&
            ModelRuntime.ShouldPauseEyeTracking(true, false, gestureActive: true, false, false, false) &&
            ModelRuntime.ShouldPauseEyeTracking(true, false, false, performanceActive: true, false, false) &&
            ModelRuntime.ShouldPauseEyeTracking(true, false, false, false, danceActive: true, false) &&
            ModelRuntime.ShouldPauseEyeTracking(true, false, false, false, false, interacting: true),
            "");

        C("鼠标跟随：戳脸彩蛋（晃脸）也暂停；空闲不暂停；关掉「动作时暂停」后完全不暂停",
            ModelRuntime.ShouldPauseEyeTracking(true, false, false, false, false, false, pokeReacting: true) &&
            !ModelRuntime.ShouldPauseEyeTracking(true, false, false, false, false, false) &&
            !ModelRuntime.ShouldPauseEyeTracking(false, true, true, true, true, true, pokeReacting: true),
            "");

        C("鼠标跟随：配置默认开启「动作时暂停」（EyeTrackingPauseOnAction）",
            new Live2DConfig().EyeTrackingPauseOnAction,
            "");

        // ==================================================================
        // 八、高好感触摸摇尾巴
        // ==================================================================
        C("摇尾巴：只在「高好感 ≥85 + 尾巴露着 + 35% 概率内」触发",
            TailWag.ShouldWag(85, true, 0) &&
            TailWag.ShouldWag(100, true, 34) &&
            !TailWag.ShouldWag(84.9, true, 0) &&
            !TailWag.ShouldWag(85, false, 0) &&
            !TailWag.ShouldWag(85, true, 35) &&
            !TailWag.ShouldWag(85, true, -1),
            "");

        C("摇尾巴：包络两端淡入淡出（0 → 1 → 0），动作外为 0",
            TailWag.Envelope(0, 3) == 0 &&
            Math.Abs(TailWag.Envelope(0.15, 3) - 0.5) < 0.01 &&
            TailWag.Envelope(1.5, 3) == 1 &&
            TailWag.Envelope(3, 3) == 0 &&
            TailWag.Envelope(5, 3) == 0,
            $"{TailWag.Envelope(0.15, 3):0.00}/{TailWag.Envelope(1.5, 3):0.00}");

        C("摇尾巴：尾尖幅度大于根部，偏移不超过设定值太多，动作外 / 幅度 0 时为 0",
            TailWag.AmplitudeScale(5) > TailWag.AmplitudeScale(0) &&
            TailWag.AmplitudeScale(5) > TailWag.AmplitudeScale(2) &&
            Math.Abs(TailWag.Offset(1.5, 3, 10, 5)) <= 12 &&
            TailWag.Offset(0, 3, 10, 0) == 0 &&
            TailWag.Offset(3, 3, 10, 0) == 0 &&
            TailWag.Offset(1.5, 3, 0, 0) == 0,
            $"{TailWag.AmplitudeScale(0):0.00}/{TailWag.AmplitudeScale(5):0.00}");

        // ==================================================================
        // 九、戳脸彩蛋：中/高好感 35% → ＞＜ + 「有点痒痒的」
        // ==================================================================
        C("戳脸彩蛋：中好感（≥30）35% 内触发，低好感 / 概率外不触发",
            PokeReaction.ShouldTrigger(30, 0) &&
            PokeReaction.ShouldTrigger(72, 34) &&
            !PokeReaction.ShouldTrigger(29.9, 0) &&
            !PokeReaction.ShouldTrigger(72, 35) &&
            !PokeReaction.ShouldTrigger(72, -1),
            "");

        C("戳脸彩蛋：表情名是「＞＜」、台词是「有点痒痒的」",
            PokeReaction.ExpressionName == "＞＜" && PokeReaction.Line == "有点痒痒的",
            $"{PokeReaction.ExpressionName} / {PokeReaction.Line}");
    }
}
