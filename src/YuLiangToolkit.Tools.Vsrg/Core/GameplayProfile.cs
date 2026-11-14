using System.Globalization;

namespace YuLiangToolkit.Tools.Vsrg.Core;

/// <summary>
/// 一个判定档：名字 + 判定窗口（±毫秒）+ 准确率权重。
///
/// <para><b>档位顺序</b>：列表就是「从严格到宽松」的顺序；按下键时按顺序找**第一个**窗口够得着的档。
/// 顺序可以随便调（界面里 ↑↓），调完立刻生效。</para>
///
/// <para><b>Miss 档</b>：<see cref="IsMiss"/> 为 true 的那一档是兜底 —— 所有窗口都够不着时算它。
/// 它不参与「按顺序匹配」，所以排序怎么变都不影响它的语义。</para>
/// </summary>
public sealed class JudgementTier
{
    /// <summary>档位名（界面上显示、结算里统计）。</summary>
    public string Name { get; init; } = "";

    /// <summary>判定窗口（±毫秒）：|按键时间 − note 时间| ≤ 它就算命中本档。</summary>
    public double WindowMs { get; init; }

    /// <summary>准确率权重（0~100）：本档对准确率的贡献百分比。</summary>
    public double AccuracyWeight { get; init; }

    /// <summary>是不是兜底档（Miss）。</summary>
    public bool IsMiss { get; init; }

    /// <summary>是不是内置预设（预设档不能删除，但可以改成别的数值 / 排队）。</summary>
    public bool IsPreset { get; init; }

    /// <summary>窗口的界面文本（<c>±22ms</c>，Miss 档显示成兜底）。</summary>
    public string WindowText => IsMiss ? "兜底" : $"±{WindowMs:0.#}ms";

    /// <summary>权重的界面文本（<c>100%</c>）。</summary>
    public string WeightText => $"{AccuracyWeight:0.#}%";

    /// <summary>调试用摘要。</summary>
    public override string ToString() => $"{Name} {WindowText} {WeightText}";
}

/// <summary>
/// 一整套判定档（游玩引擎只认它，不认配置对象）。
///
/// <para>由 <see cref="GameplayProfile.FromConfig"/> 从配置里造出来，好处是：配置随时可改，
/// 一局开始时拍一份不可变快照，游玩途中改设置不会让这一局的判定突然变形。</para>
/// </summary>
public sealed class GameplayProfile
{
    /// <summary>全部档位（顺序 = 匹配顺序；Miss 档也在里面）。</summary>
    public IReadOnlyList<JudgementTier> Tiers { get; }

    /// <summary>非 Miss 的最大窗口（毫秒）—— 超过它还没按就算漏。</summary>
    public double MaxWindowMs { get; }

    /// <summary>最高准确率权重（准确率分母用它）。</summary>
    public double MaxWeight { get; }

    /// <summary>兜底档（Miss）。</summary>
    public JudgementTier MissTier { get; }

    /// <summary>是否开启面条尾部判定。</summary>
    public bool TailJudgementEnabled { get; }

    /// <summary>面条尾部判定窗口的乘数（头判窗口 × 它 = 尾判窗口）。</summary>
    public double TailWindowMultiplier { get; }

    /// <summary>构造（参数不合法时会被夹到安全值）。</summary>
    public GameplayProfile(IReadOnlyList<JudgementTier> tiers, bool tailJudgementEnabled, double tailWindowMultiplier)
    {
        if (tiers is null || tiers.Count == 0) throw new ArgumentException("至少要有一个判定档。", nameof(tiers));

        var miss = tiers.FirstOrDefault(t => t.IsMiss);
        if (miss is null)
        {
            // 没有 Miss 档：把最后一档当兜底（配置被手改坏时的兜底，界面里不允许这样保存）
            miss = tiers[^1];
        }

        Tiers = tiers;
        MissTier = miss;
        MaxWindowMs = tiers.Where(t => !ReferenceEquals(t, miss)).Select(t => t.WindowMs).DefaultIfEmpty(0).Max();
        MaxWeight = tiers.Select(t => t.AccuracyWeight).DefaultIfEmpty(0).Max();
        if (MaxWeight <= 0) MaxWeight = 1;
        TailJudgementEnabled = tailJudgementEnabled;
        TailWindowMultiplier = Math.Clamp(double.IsFinite(tailWindowMultiplier) ? tailWindowMultiplier : 1, 0.1, 10);
    }

    /// <summary>按序号取档位的显示名（越界给 <c>—</c>）。</summary>
    public string NameOf(int tierIndex)
        => tierIndex >= 0 && tierIndex < Tiers.Count ? Tiers[tierIndex].Name : "—";

    /// <summary>Miss 档在列表里的序号（结算统计用）。</summary>
    public int MissTierIndex
    {
        get
        {
            for (var i = 0; i < Tiers.Count; i++)
            {
                if (ReferenceEquals(Tiers[i], MissTier)) return i;
            }
            return -1;
        }
    }

    /// <summary>标准预设：Marvelous / Good / Bad / Miss 四档。</summary>
    public static IReadOnlyList<JudgementTier> StandardTiers() => new[]
    {
        new JudgementTier { Name = "Marvelous", WindowMs = 22, AccuracyWeight = 100, IsPreset = true },
        new JudgementTier { Name = "Good", WindowMs = 50, AccuracyWeight = 65, IsPreset = true },
        new JudgementTier { Name = "Bad", WindowMs = 90, AccuracyWeight = 30, IsPreset = true },
        new JudgementTier { Name = "Miss", WindowMs = 0, AccuracyWeight = 0, IsMiss = true, IsPreset = true },
    };

    /// <summary>标准预设的完整 profile（面条尾判开、乘数 1.5）。</summary>
    public static GameplayProfile Standard() => new(StandardTiers(), tailJudgementEnabled: true, tailWindowMultiplier: 1.5);

    /// <summary>
    /// 按 |差值| 找一个非 Miss 档；都够不着返回 null（调用方自己决定用不用 Miss）。
    /// </summary>
    public JudgementTier? Match(double deltaMs)
    {
        var delta = Math.Abs(deltaMs);
        if (!double.IsFinite(delta)) return null;

        foreach (var tier in Tiers)
        {
            if (tier.IsMiss) continue;
            if (delta <= tier.WindowMs && tier.WindowMs > 0) return tier;
        }

        return null;
    }

    /// <summary>
    /// 和 <see cref="Match"/> 一样，但窗口按 <paramref name="windowScale"/> 缩放
    /// （面条尾判用：尾判窗口 = 该档窗口 × 乘数）。
    /// </summary>
    public JudgementTier? MatchScaled(double deltaMs, double windowScale)
    {
        var delta = Math.Abs(deltaMs);
        if (!double.IsFinite(delta)) return null;

        var scale = double.IsFinite(windowScale) && windowScale > 0 ? windowScale : 1;
        foreach (var tier in Tiers)
        {
            if (tier.IsMiss) continue;
            if (delta <= tier.WindowMs * scale && tier.WindowMs > 0) return tier;
        }

        return null;
    }

    /// <summary>档位在列表里的序号（找不到返回 -1）。</summary>
    public int IndexOf(JudgementTier tier)
    {
        for (var i = 0; i < Tiers.Count; i++)
        {
            if (ReferenceEquals(Tiers[i], tier)) return i;
        }
        return -1;
    }

    /// <summary>不准 / 合法检查：返回中文原因；没问题返回 null。界面在保存判定设置前用它提示。</summary>
    public static string? ValidateTiers(IReadOnlyList<JudgementTier> tiers)
    {
        if (tiers.Count == 0) return "至少要有一个判定档。";

        var misses = tiers.Count(t => t.IsMiss);
        if (misses != 1) return "必须正好有一个 Miss（兜底）判定档。";

        foreach (var tier in tiers)
        {
            if (string.IsNullOrWhiteSpace(tier.Name)) return "判定档的名字不能为空。";
            if (!double.IsFinite(tier.WindowMs) || tier.WindowMs < 0) return $"「{tier.Name}」的判定窗口不合法。";
            if (!double.IsFinite(tier.AccuracyWeight) || tier.AccuracyWeight < 0 || tier.AccuracyWeight > 100)
                return $"「{tier.Name}」的准确率权重要在 0 ~ 100 之间。";
        }

        if (tiers.Count(t => !t.IsMiss && t.WindowMs > 0) == 0) return "至少要有一个窗口大于 0 的判定档。";

        return null;
    }

    /// <summary>把毫秒数写成 <c>±12.5ms</c> 这种文本（界面 / 结算用）。</summary>
    public static string FormatMs(double ms) => ms.ToString("0.#", CultureInfo.InvariantCulture);
}
