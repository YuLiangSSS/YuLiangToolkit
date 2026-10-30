using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「PDF 阅读器」的设置 → <c>Config\pdf-reader\config.json</c>。
///
/// <para>只存「下次打开还要接着用」的三样：上次的阅读方式、拖动惯性开关、上次打开 PDF 所在文件夹。
/// 改一下自动落盘，没有保存按钮（见 AGENTS.md）。</para>
/// </summary>
public sealed class PdfReaderConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\pdf-reader\</c> 与 <c>Data\pdf-reader\</c> 的文件夹名）。</summary>
    public const string ToolId = "pdf-reader";

    /// <summary>连续滚动模式下滚轮一格的默认步长（像素）。</summary>
    public const int DefaultWheelStepPixels = 120;

    // ---------- 持久化字段 ----------

    /// <summary>阅读方式：纵向 上→下（默认）/ 横向 左→右 / 双页 翻页。枚举按 C# 原名存字符串。</summary>
    public PdfReadMode ReadMode { get; set; } = PdfReadMode.Vertical;

    /// <summary>左键拖动松手后的惯性（默认开）；关掉 = 松手即停。</summary>
    public bool InertiaEnabled { get; set; } = true;

    /// <summary>连续滚动模式滚轮一格的像素（20 ~ 600，默认 120）。</summary>
    public int WheelStepPixels { get; set; } = DefaultWheelStepPixels;

    /// <summary>上次打开 PDF 所在文件夹（只用来当文件对话框的起始目录，不会自动打开）。</summary>
    public string LastFolder { get; set; } = "";

    // ---------- 计算属性（只读 → 不会被写进 JSON） ----------

    /// <summary>合法的阅读方式（手改坏配置时退回纵向）。</summary>
    [JsonIgnore]
    public PdfReadMode ReadModeEffective => PdfReadModes.Normalize(ReadMode);

    /// <summary>生效的滚轮步长（夹到 20 ~ 600，手改坏配置也不会滚飞）。</summary>
    [JsonIgnore]
    public int WheelStepEffective => Math.Clamp(WheelStepPixels, 20, 600);
}
