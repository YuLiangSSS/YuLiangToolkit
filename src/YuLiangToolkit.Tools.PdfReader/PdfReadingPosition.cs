using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 一本 PDF 的「上次阅读位置」（存在 <c>Config\pdf-reader\config.json</c> 的
/// <c>readingPositions</c> 字典里，key = 规范化后的完整路径）。
///
/// <para><b>为什么同时存页号和比例</b>：连续滚动模式一页可能跨好几屏，只存页号回到的是页开头；
/// 存「整体阅读比例」可以回到几乎一模一样的位置。但换过阅读方式后比例含义会变，所以还存一个
/// <see cref="Mode"/>：模式没变时按比例恢复，模式变了就退回到「第几页」。</para>
/// </summary>
public sealed class PdfReadingPosition
{
    /// <summary>上次看到第几页（0 起）。</summary>
    public int PageIndex { get; set; }

    /// <summary>整体阅读进度 0 ~ 1（连续滚动模式恢复用；0 = 第一页开头，1 = 最后一页末尾）。</summary>
    public double ReadingRatio { get; set; }

    /// <summary>保存时用的阅读方式（<see cref="PdfReadMode"/> 原名；模式变了就不按比例恢复）。</summary>
    public PdfReadMode Mode { get; set; } = PdfReadMode.Vertical;

    /// <summary>保存时间（UTC）：超过上限时按它淘汰最老的。</summary>
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>合法的阅读方式（手改坏配置时退回纵向）。</summary>
    [JsonIgnore]
    public PdfReadMode ModeEffective => PdfReadModes.Normalize(Mode);
}
