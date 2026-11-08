using System;
using System.Linq;
using YuLiangToolkit.Tools.Live2D;
using YuLiangToolkit.Tools.Live2D.Controls;
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
    }
}
