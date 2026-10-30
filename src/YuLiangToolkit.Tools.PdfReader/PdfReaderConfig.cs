using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「PDF 阅读器」的设置 → <c>Config\pdf-reader\config.json</c>。
///
/// <para>存「下次打开还要接着用」的选项：阅读方式、拖动惯性、滚轮步长、缩放百分比、
/// 双页页面间距、上次打开 PDF 所在文件夹。改一下自动落盘，没有保存按钮（见 AGENTS.md）。</para>
/// </summary>
public sealed class PdfReaderConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\pdf-reader\</c> 与 <c>Data\pdf-reader\</c> 的文件夹名）。</summary>
    public const string ToolId = "pdf-reader";

    /// <summary>连续滚动模式下滚轮一格的默认步长（像素）。</summary>
    public const int DefaultWheelStepPixels = 120;

    /// <summary>缩放百分比：默认 100%（= 铺满基准，**不是** PDF 原始点数 1:1），允许 25 ~ 800。</summary>
    public const int DefaultZoomPercent = 100;
    public const int MinZoomPercent = 25;
    public const int MaxZoomPercent = 800;

    /// <summary>双页模式左右两页之间的默认间距（像素）：默认 2（贴近，像翻书）；允许 0 ~ 64。</summary>
    public const int DefaultSpreadGapPixels = 2;
    public const int MinSpreadGapPixels = 0;
    public const int MaxSpreadGapPixels = 64;

    /// <summary>连续滚动模式页与页之间的间距（像素）：固定 16，跟图片阅读器一致。</summary>
    public const int ContinuousPageGapPixels = 16;

    /// <summary>长带两端 / 铺满方向上留的边距（像素）：固定 24。</summary>
    public const int SideMarginPixels = 24;

    // ---------- 持久化字段 ----------

    /// <summary>阅读方式：纵向 上→下（默认）/ 横向 左→右 / 双页 翻页。枚举按 C# 原名存字符串。</summary>
    public PdfReadMode ReadMode { get; set; } = PdfReadMode.Vertical;

    /// <summary>左键拖动松手后的惯性（默认开）；关掉 = 松手即停。</summary>
    public bool InertiaEnabled { get; set; } = true;

    /// <summary>连续滚动模式滚轮一格的像素（20 ~ 600，默认 120）。</summary>
    public int WheelStepPixels { get; set; } = DefaultWheelStepPixels;

    /// <summary>
    /// 缩放百分比：默认 100%（= 程序默认的铺满基准，**不是** PDF 原始点数 1:1），允许 25 ~ 800。
    /// 工具条的百分比输入框 / 放大镜按钮 / Ctrl+滚轮都改它。
    /// </summary>
    public int ZoomPercent { get; set; } = DefaultZoomPercent;

    /// <summary>双页翻页时左右两页之间的间距（像素，0 ~ 64，默认 2 = 贴近）。</summary>
    public int SpreadGapPixels { get; set; } = DefaultSpreadGapPixels;

    /// <summary>上次打开 PDF 所在文件夹（只用来当文件对话框的起始目录，不会自动打开）。</summary>
    public string LastFolder { get; set; } = "";

    // ---------- 计算属性（只读 → 不会被写进 JSON） ----------

    /// <summary>合法的阅读方式（手改坏配置时退回纵向）。</summary>
    [JsonIgnore]
    public PdfReadMode ReadModeEffective => PdfReadModes.Normalize(ReadMode);

    /// <summary>生效的滚轮步长（夹到 20 ~ 600，手改坏配置也不会滚飞）。</summary>
    [JsonIgnore]
    public int WheelStepEffective => Math.Clamp(WheelStepPixels, 20, 600);

    /// <summary>生效的缩放百分比（夹到 25 ~ 800）。</summary>
    [JsonIgnore]
    public int ZoomPercentEffective => Math.Clamp(ZoomPercent, MinZoomPercent, MaxZoomPercent);

    /// <summary>缩放系数（100% → 1.0，200% → 2.0）。</summary>
    [JsonIgnore]
    public double ZoomScale => ZoomPercentEffective / 100.0;

    /// <summary>生效的双页页面间距（夹到 0 ~ 64）。</summary>
    [JsonIgnore]
    public int SpreadGapEffective => Math.Clamp(SpreadGapPixels, MinSpreadGapPixels, MaxSpreadGapPixels);

    /// <summary>
    /// 「恢复默认设置」：阅读方式回纵向、惯性开、滚轮 120、缩放 100%、双页间距 2。
    /// **不动「上次打开的文件夹」**（那不是显示设置，清了反而给用户添麻烦）。
    /// </summary>
    public void ResetToDefaults()
    {
        ReadMode = PdfReadMode.Vertical;
        InertiaEnabled = true;
        WheelStepPixels = DefaultWheelStepPixels;
        ZoomPercent = DefaultZoomPercent;
        SpreadGapPixels = DefaultSpreadGapPixels;
    }

    /// <summary>把当前缩放百分比按步数增减并夹好（放大镜按钮 / Ctrl+滚轮用）。</summary>
    public static int StepZoomPercent(int currentPercent, int steps, int stepPercent = 10)
        => Math.Clamp(
            Math.Clamp(currentPercent, MinZoomPercent, MaxZoomPercent) + steps * stepPercent,
            MinZoomPercent,
            MaxZoomPercent);
}
