using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.WordViewer;

/// <summary>
/// 一份 Word 文档的「上次阅读位置」（存在 <c>Config\word-viewer\config.json</c> 的
/// <c>readingPositions</c> 字典里，key = 规范化后的完整路径小写）。
///
/// <para><b>为什么记比例而不是行号</b>：Word 没有固定的「页」概念（排版随窗口宽度 / 缩放变化），
/// 记「竖向滚动比例 0 ~ 1」最稳：重新打开同一份文档时滚到差不多的位置；
/// 文件被改短了比例也仍然合法（夹到 0 ~ 1，只是停在新文档末尾附近）。</para>
/// </summary>
public sealed class WordReadingPosition
{
    /// <summary>竖向滚动比例：0 = 文档顶部，1 = 文档底部（读取时统一经过 <see cref="ClampScrollRatio"/>）。</summary>
    public double ScrollRatio { get; set; }

    /// <summary>保存时间（UTC）：超过 200 份文档时按它淘汰最老的记录。</summary>
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>生效的滚动比例：NaN / 无穷 / 超出 0 ~ 1 都会夹回合法范围（手改坏配置也不会滚飞）。</summary>
    [JsonIgnore]
    public double ScrollRatioEffective => ClampScrollRatio(ScrollRatio);

    /// <summary>把任意值夹成合法的滚动比例：NaN / 无穷 → 0，越界夹到 0 ~ 1。</summary>
    public static double ClampScrollRatio(double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return 0;
        return Math.Clamp(ratio, 0, 1);
    }
}
