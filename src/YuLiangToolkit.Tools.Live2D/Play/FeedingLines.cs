using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>
/// 投喂 / 饥饿相关的台词池（会话负责说出来：TTS 语音 + 头顶气泡；称呼用 {0} 占位）。
///
/// <para>分三档「吃完之后」+ 一档「一直没吃东西」：</para>
/// <list type="bullet">
///   <item>很饱（&ge;80）：满足、撒娇、说吃撑了；</item>
///   <item>没吃饱（45~79 / 20~44）：还想要，会恳求；</item>
///   <item>很饿（&lt;20）：难受、抱怨肚子饿；</item>
///   <item>饿的时候主动提醒：也是这一档的语气，循环间隔由设置控制。</item>
/// </list>
/// </summary>
public static class FeedingLines
{
    // ---------- 吃完之后：很饱（满足） ----------
    private static readonly string[] SatisfiedAfterEat =
    {
        "呼…吃得好饱，肚皮都圆滚滚的了～",
        "嗝…好满足，{0}喂的饭团最好吃了！",
        "嘿嘿，饱饱的，感觉整条鱼都暖起来了。",
        "这一口下去，尾巴都开心得翘起来了～",
        "好幸福…下次还要{0}喂我。",
        "吃饱了！现在可以安心趴一会儿了…",
        "唔…有点撑，不过好快乐。",
    };

    // ---------- 吃完之后：还没吃饱（还想要，恳求） ----------
    private static readonly string[] HungryAfterEat =
    {
        "{0}…我还想要一个，就一个嘛。",
        "唔…好像只吃了个半饱，肚子还在叫呢。",
        "再来一个饭团好不好？我保证乖乖的。",
        "这点根本不够嘛…{0}，再喂我一口？",
        "嚼嚼…吃完了，可是还想吃…",
    };

    // ---------- 吃完之后：仍然很饿（很难受） ----------
    private static readonly string[] StarvingAfterEat =
    {
        "好饿啊…这点根本不够…",
        "{0}，我还没吃饱呢，肚子咕咕叫了…",
        "呜…还有饭团吗？我快饿扁了…",
        "吃完了…可是更饿了…",
        "饿得没力气说话了…{0}…饭团…",
    };

    // ---------- 一直没吃东西（主动抱怨 / 求投喂） ----------
    private static readonly string[] HungryReminders =
    {
        "好饿啊～…{0}，我肚子扁扁的。",
        "呜…已经很久没吃东西了，有饭团吗？",
        "肚子在咕咕叫…{0}听见了吗？",
        "饿得只能趴着了…求投喂…",
        "再不吃东西，尾巴都要没力气摇啦…",
        "想吃大白饭团…三角形的也行…",
        "饿…{0}，我可以用卖萌换一个饭团吗？",
    };

    /// <summary>饱食度 → 档位：&lt;20 很饿 / &lt;45 没吃饱 / &lt;80 正常 / &ge;80 很饱。</summary>
    public static FullnessTier ResolveTier(double fullness) => fullness switch
    {
        < 20 => FullnessTier.Starving,
        < 45 => FullnessTier.Hungry,
        < 80 => FullnessTier.Normal,
        _ => FullnessTier.Full,
    };

    /// <summary>档位 → 中文名（界面显示）。</summary>
    public static string TierText(FullnessTier tier) => tier switch
    {
        FullnessTier.Starving => "很饿",
        FullnessTier.Hungry => "没吃饱",
        FullnessTier.Full => "很饱",
        _ => "正常",
    };

    /// <summary>
    /// 挑一句「吃完之后」的话：<paramref name="tier"/> 是吃完之后的档位。
    /// 很饱 / 正常（没吃饱） / 很饿各有一组；<paramref name="address"/> 是用户称呼（主人 / 老公 / 名字…）。
    /// </summary>
    public static string PickAfterEat(FullnessTier tier, string? address, Random? random = null)
    {
        random ??= Random.Shared;
        var pool = tier switch
        {
            FullnessTier.Full => SatisfiedAfterEat,
            FullnessTier.Starving => StarvingAfterEat,
            _ => HungryAfterEat,
        };
        return Format(pool[random.Next(pool.Length)], address);
    }

    /// <summary>挑一句「一直没吃东西」的抱怨（饿的时候主动说）。</summary>
    public static string PickHungryReminder(string? address, Random? random = null)
    {
        random ??= Random.Shared;
        return Format(HungryReminders[random.Next(HungryReminders.Length)], address);
    }

    /// <summary>
    /// 投喂台词 → 表情文件名关键词（会话按名字包含匹配 runtime.Expressions）：
    /// 很饱 → ＞＜（眯眼笑，满足）；没吃饱 → 流泪（恳求）；很饿 → 流冷汗（难受）。
    /// 返回空串 = 不换表情。
    /// </summary>
    public static string GetExpressionKeyword(FullnessTier tier, bool afterEat) => tier switch
    {
        FullnessTier.Full => "＞＜",
        FullnessTier.Hungry => afterEat ? "流泪" : "",
        FullnessTier.Starving => "流冷汗",
        _ => afterEat ? "＞＜" : "",
    };

    /// <summary>台词里的 {0} 换成用户称呼；称呼为空时用「主人」。</summary>
    private static string Format(string template, string? address)
    {
        var name = string.IsNullOrWhiteSpace(address) ? "主人" : address.Trim();
        return template.Contains("{0}", StringComparison.Ordinal)
            ? template.Replace("{0}", name, StringComparison.Ordinal)
            : template;
    }
}
