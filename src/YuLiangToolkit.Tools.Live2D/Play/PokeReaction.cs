namespace YuLiangToolkit.Tools.Live2D.Play;

/// <summary>
/// 「戳脸反应」小概率彩蛋：中 / 高好感时戳脸，有概率做「＞＜」表情并说「有点痒痒的」。
///
/// <para>纯策略（自检直接断言）：中好感 = ≥30（普通 / 亲近 / 心动都算），概率 35%。</para>
/// </summary>
public static class PokeReaction
{
    /// <summary>中 / 高好感门槛（普通档起）。</summary>
    public const int MinAffection = 30;

    /// <summary>戳脸触发概率（百分比）。</summary>
    public const int TriggerChancePercent = 35;

    /// <summary>触发表情（模型内置 exp3 名）。</summary>
    public const string ExpressionName = "＞＜";

    /// <summary>台词（用户指定）。</summary>
    public const string Line = "有点痒痒的";

    /// <summary>表情保持秒数（到点只撤掉这一个表情）。</summary>
    public const double ExpressionSeconds = 2.5;

    /// <summary>触发判定：好感 ≥30 + 随机数落在概率内。</summary>
    public static bool ShouldTrigger(double affection, int rollPercent)
        => affection >= MinAffection && rollPercent >= 0 && rollPercent < TriggerChancePercent;
}
