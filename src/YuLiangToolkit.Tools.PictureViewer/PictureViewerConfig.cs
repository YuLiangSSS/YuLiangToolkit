using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「图片连续阅读器」的设置 → <c>Config\picture-viewer\config.json</c>。
///
/// <para>都是「看一眼就懂」的项：滚动方式、滚轮一格多远、中键单点 / 按住的判定、离屏图片要不要自动释放、
/// 上次打开的文件夹。改一下就自动落盘，没有保存按钮（见 AGENTS.md 2.4）。</para>
/// </summary>
public sealed class PictureViewerConfig : ToolConfig
{
    /// <summary>功能 Id（= <c>Config\picture-viewer\</c> 与 <c>Data\picture-viewer\</c> 的文件夹名）。</summary>
    public const string ToolId = "picture-viewer";

    // ---------- 持久化字段 ----------

    /// <summary>滚动方式：纵向（默认）/ 横向左→右 / 横向右→左。枚举按 C# 原名存字符串。</summary>
    public PictureScrollMode ScrollMode { get; set; } = PictureScrollMode.Vertical;

    /// <summary>滚轮一格（像素），20 ~ 600，默认 120。</summary>
    public int WheelStepPixels { get; set; } = (int)PictureScrollMath.DefaultWheelStepPixels;

    /// <summary>中键「单点」和「按住」的分界（毫秒），60 ~ 2000，默认 250。</summary>
    public int MiddleHoldMilliseconds { get; set; } = PictureScrollMath.DefaultHoldMilliseconds;

    /// <summary>离屏的图自动释放（默认开）：长条几百张图不至于把内存吃满；再滚回来会重新解码。</summary>
    public bool AutoUnloadOffscreen { get; set; } = true;

    /// <summary>上次打开的文件夹（只用来当文件对话框的起始目录，不会自动打开）。</summary>
    public string LastFolder { get; set; } = "";

    // ---------- 计算属性（只读 → 不会被写进 JSON） ----------

    /// <summary>合法的滚动方式（手改坏配置时退回纵向）。</summary>
    [JsonIgnore]
    public PictureScrollMode ScrollModeEffective => PictureScrollModes.Normalize(ScrollMode);

    /// <summary>生效的滚轮步长。</summary>
    [JsonIgnore]
    public int WheelStepEffective => PictureScrollMath.EffectiveWheelStep(WheelStepPixels);

    /// <summary>生效的中键判定时长。</summary>
    [JsonIgnore]
    public int HoldMillisecondsEffective => PictureScrollMath.EffectiveHoldMilliseconds(MiddleHoldMilliseconds);

    /// <summary>当前方式的说明（设置面板里一行小字）。</summary>
    [JsonIgnore]
    public string ScrollModeHint => PictureScrollModes.Hint(ScrollModeEffective);
}
