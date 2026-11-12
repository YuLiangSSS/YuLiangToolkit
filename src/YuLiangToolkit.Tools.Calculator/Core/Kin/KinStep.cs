namespace YuLiangToolkit.Tools.Calculator.Core.Kin;

/// <summary>
/// 辈分计算的「一步关系」：从当前这个人走到他的某个亲属（<c>爸爸</c> = 走到他爸爸）。
///
/// <para>枚举顺序就是界面上十个按钮的顺序（爸爸 / 妈妈 / 哥哥 / 弟弟 / 姐姐 / 妹妹 / 老公 / 老婆 / 儿子 / 女儿），
/// 长幼（哥 ↔ 弟、姐 ↔ 妹）是分开的，因为中文称呼里「爸爸的哥哥」和「爸爸的弟弟」叫法不一样。</para>
/// </summary>
public enum KinStep
{
    /// <summary>爸爸（上一辈，男性）。</summary>
    Father = 0,

    /// <summary>妈妈（上一辈，女性）。</summary>
    Mother = 1,

    /// <summary>哥哥（同辈，男性，比我大）。</summary>
    OlderBrother = 2,

    /// <summary>弟弟（同辈，男性，比我小）。</summary>
    YoungerBrother = 3,

    /// <summary>姐姐（同辈，女性，比我大）。</summary>
    OlderSister = 4,

    /// <summary>妹妹（同辈，女性，比我小）。</summary>
    YoungerSister = 5,

    /// <summary>老公 / 丈夫（配偶，男性）。</summary>
    Husband = 6,

    /// <summary>老婆 / 妻子（配偶，女性）。</summary>
    Wife = 7,

    /// <summary>儿子（下一辈，男性）。</summary>
    Son = 8,

    /// <summary>女儿（下一辈，女性）。</summary>
    Daughter = 9,
}

/// <summary>
/// <see cref="KinStep"/> 的元数据与换算（纯函数，自检直接断言）：中文叫法、辈分方向、性别、逆关系。
/// </summary>
public static class KinStepInfo
{
    /// <summary>界面上那十个按钮的顺序（跟枚举顺序一致，改枚举顺序会跟着变）。</summary>
    public static IReadOnlyList<KinStep> All { get; } = new[]
    {
        KinStep.Father, KinStep.Mother,
        KinStep.OlderBrother, KinStep.YoungerBrother, KinStep.OlderSister, KinStep.YoungerSister,
        KinStep.Husband, KinStep.Wife,
        KinStep.Son, KinStep.Daughter,
    };

    /// <summary>是不是向上走一步（爸爸 / 妈妈）。</summary>
    public static bool IsParent(this KinStep step) => step is KinStep.Father or KinStep.Mother;

    /// <summary>是不是向下走一步（儿子 / 女儿）。</summary>
    public static bool IsChild(this KinStep step) => step is KinStep.Son or KinStep.Daughter;

    /// <summary>是不是同辈的兄弟姐妹。</summary>
    public static bool IsSibling(this KinStep step)
        => step is KinStep.OlderBrother or KinStep.YoungerBrother or KinStep.OlderSister or KinStep.YoungerSister;

    /// <summary>是不是配偶。</summary>
    public static bool IsSpouse(this KinStep step) => step is KinStep.Husband or KinStep.Wife;

    /// <summary>这一步走到的人是不是男性（用来推后面的性别相关叫法）。</summary>
    public static bool IsMale(this KinStep step)
        => step is KinStep.Father or KinStep.OlderBrother or KinStep.YoungerBrother or KinStep.Husband or KinStep.Son;

    /// <summary>这一步往上是「长一辈」（+1），往下是「小一辈」（-1），同辈 / 配偶是 0。</summary>
    public static int GenerationDelta(this KinStep step)
        => step.IsParent() ? 1 : step.IsChild() ? -1 : 0;

    /// <summary>按钮 / 关系链里显示的中文（口语叫法）。</summary>
    public static string Label(this KinStep step) => step switch
    {
        KinStep.Father => "爸爸",
        KinStep.Mother => "妈妈",
        KinStep.OlderBrother => "哥哥",
        KinStep.YoungerBrother => "弟弟",
        KinStep.OlderSister => "姐姐",
        KinStep.YoungerSister => "妹妹",
        KinStep.Husband => "老公",
        KinStep.Wife => "老婆",
        KinStep.Son => "儿子",
        KinStep.Daughter => "女儿",
        _ => "",
    };

    /// <summary>
    /// 紧凑写法（存配置 / 当称谓表的键用）：<c>F</c> <c>M</c> <c>oB</c> <c>yB</c> <c>oS</c> <c>yS</c>
    /// <c>H</c> <c>W</c> <c>S</c> <c>D</c>。比枚举名短，配置文件和表都好读。
    /// </summary>
    public static string Token(this KinStep step) => step switch
    {
        KinStep.Father => "F",
        KinStep.Mother => "M",
        KinStep.OlderBrother => "oB",
        KinStep.YoungerBrother => "yB",
        KinStep.OlderSister => "oS",
        KinStep.YoungerSister => "yS",
        KinStep.Husband => "H",
        KinStep.Wife => "W",
        KinStep.Son => "S",
        KinStep.Daughter => "D",
        _ => "",
    };

    /// <summary>
    /// 逆关系：已知「B 是 A 的 <paramref name="step"/>」，反推「A 是 B 的什么」。
    /// <paramref name="fromIsMale"/> 是 A 的性别（爸爸 ↔ 儿子/女儿、哥哥 ↔ 弟弟/妹妹 都要靠它）。
    /// 推不出来（几乎不会）时返回 null。
    /// </summary>
    public static KinStep? Inverse(this KinStep step, bool fromIsMale) => step switch
    {
        KinStep.Father or KinStep.Mother => fromIsMale ? KinStep.Son : KinStep.Daughter,
        KinStep.Son or KinStep.Daughter => fromIsMale ? KinStep.Father : KinStep.Mother,
        KinStep.Husband => KinStep.Wife,
        KinStep.Wife => KinStep.Husband,
        KinStep.OlderBrother => fromIsMale ? KinStep.YoungerBrother : KinStep.YoungerSister,
        KinStep.YoungerBrother => fromIsMale ? KinStep.OlderBrother : KinStep.OlderSister,
        KinStep.OlderSister => fromIsMale ? KinStep.YoungerBrother : KinStep.YoungerSister,
        KinStep.YoungerSister => fromIsMale ? KinStep.OlderBrother : KinStep.OlderSister,
        _ => null,
    };

    /// <summary>紧凑写法 → 枚举；不认识返回 null（配置被手改坏时按「没有这一步」处理）。</summary>
    public static KinStep? ParseToken(string? token) => token switch
    {
        "F" => KinStep.Father,
        "M" => KinStep.Mother,
        "oB" => KinStep.OlderBrother,
        "yB" => KinStep.YoungerBrother,
        "oS" => KinStep.OlderSister,
        "yS" => KinStep.YoungerSister,
        "H" => KinStep.Husband,
        "W" => KinStep.Wife,
        "S" => KinStep.Son,
        "D" => KinStep.Daughter,
        _ => null,
    };
}
