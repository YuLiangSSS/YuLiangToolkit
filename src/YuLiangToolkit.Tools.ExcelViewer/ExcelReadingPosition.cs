using System.Text.Json.Serialization;

namespace YuLiangToolkit.Tools.ExcelViewer;

/// <summary>
/// 一本工作簿的「上次阅读位置」（存在 <c>Config\excel-viewer\config.json</c> 的
/// <c>readingPositions</c> 字典里，key = 规范化后的完整路径小写）。
///
/// <para><b>为什么同时存工作表名和滚动比例</b>：工作簿里有多张表，只存比例的话再打开时
/// 会跳到「第一张表里的同一个比例」；存上当时看的工作表名，才能既回到那张表、又回到表里的大致位置。
/// 记录的工作表被删掉 / 改名时，界面回退到第一个可见工作表（不报错）。</para>
/// </summary>
public sealed class ExcelReadingPosition
{
    /// <summary>上次看的工作表名（大小写不敏感比较；空串 = 没记录）。</summary>
    public string SheetName { get; set; } = "";

    private double _scrollRatio;

    /// <summary>
    /// 上次的纵向滚动比例 0 ~ 1（0 = 表头，1 = 最后一行）。
    /// <b>赋值时自动夹取</b>（手改配置文件写成 2 / -1 / NaN 也不会让界面跳飞）。
    /// </summary>
    public double ScrollRatio
    {
        get => _scrollRatio;
        set => _scrollRatio = ClampRatio(value);
    }

    /// <summary>保存时间（UTC）：超过上限时按它淘汰最老的记录。</summary>
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>有没有工作表名（没有的话恢复时直接回退到第一个可见表）。</summary>
    [JsonIgnore]
    public bool HasSheetName => !string.IsNullOrWhiteSpace(SheetName);

    /// <summary>把比例夹到 0 ~ 1；NaN / Infinity 当作 0。</summary>
    public static double ClampRatio(double value)
        => double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Clamp(value, 0, 1);
}
