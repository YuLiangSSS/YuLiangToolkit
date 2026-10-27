using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Configuration;

namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「图片连续阅读器」的设置 → <c>Config\picture-viewer\config.json</c>。
///
/// <para>都是「看一眼就懂」的项：滚动方式、缩放、拖动惯性、滚轮一格多远、中键单点 / 按住的判定、
/// 离屏图片要不要自动释放、上次打开的文件夹。改一下就自动落盘，没有保存按钮（见 AGENTS.md 2.4）。</para>
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

    /// <summary>
    /// 缩放百分比：默认 100%（= 程序默认的铺满基准，**不是**图片原始像素 1:1），允许 25 ~ 800。
    /// 界面上的输入框 / 放大镜按钮 / Ctrl+滚轮都改它。
    /// </summary>
    public int ZoomPercent { get; set; } = PictureScrollMath.DefaultZoomPercent;

    /// <summary>拖动惯性（松手后继续滑一段）。默认开；关掉就是松手即停。</summary>
    public bool InertiaEnabled { get; set; } = true;

    /// <summary>
    /// 惯性「越滑越快」：下一次甩动时把还在滑的残余速度**加**上去，而不是清零重置
    /// （手机漫画 App 的手感）。默认开；关掉则每次松手都只用这一次甩动的速度。
    /// </summary>
    public bool InertiaAccelerate { get; set; } = true;

    /// <summary>翻页模式（双页 / 单页）里滚轮做什么：翻页（默认）或缩放。</summary>
    public PictureWheelAction PagedWheel { get; set; } = PictureWheelAction.TurnPage;

    /// <summary>翻页 / 单页切换图片时要不要滑动动画（默认开；关掉就是直接换）。</summary>
    public bool PageTurnAnimation { get; set; } = true;

    /// <summary>是否显示图片边框（用于隔开相邻两张图）。默认开。</summary>
    public bool BorderEnabled { get; set; } = true;

    /// <summary>内边框粗细（像素，0 ~ 32，默认 1）。</summary>
    public int BorderInnerSize { get; set; } = PictureBorderStyle.DefaultInnerSize;

    /// <summary>填充宽度（像素，0 ~ 128，默认 4）。</summary>
    public int BorderFillSize { get; set; } = PictureBorderStyle.DefaultFillSize;

    /// <summary>外边框粗细（像素，0 ~ 32，默认 1）。</summary>
    public int BorderOuterSize { get; set; } = PictureBorderStyle.DefaultOuterSize;

    /// <summary>内边框颜色（ARGB，默认 #FF3E4552）。</summary>
    public string BorderInnerColor { get; set; } = PictureBorderStyle.DefaultInnerColor;

    /// <summary>边框填充颜色（ARGB，默认 #FF151A22）。</summary>
    public string BorderFillColor { get; set; } = PictureBorderStyle.DefaultFillColor;

    /// <summary>外边框颜色（ARGB，默认 #FF090B0F）。</summary>
    public string BorderOuterColor { get; set; } = PictureBorderStyle.DefaultOuterColor;

    /// <summary>上次打开的文件夹（只用来当文件对话框的起始目录，不会自动打开）。</summary>
    public string LastFolder { get; set; } = "";

    /// <summary>
    /// 「恢复默认设置」：把所有阅读 / 显示相关的项改回默认（默认阅读方式 = 纵向 上→下）。
    /// 不动文件关联（那是系统级开关，去设置里单独勾），也不动当前已经打开的文件夹（界面上的页清单）。
    /// </summary>
    public void ResetToDefaults()
    {
        ScrollMode = PictureScrollMode.Vertical;
        WheelStepPixels = (int)PictureScrollMath.DefaultWheelStepPixels;
        MiddleHoldMilliseconds = PictureScrollMath.DefaultHoldMilliseconds;
        AutoUnloadOffscreen = true;
        ZoomPercent = PictureScrollMath.DefaultZoomPercent;
        InertiaEnabled = true;
        InertiaAccelerate = true;
        PagedWheel = PictureWheelAction.TurnPage;
        PageTurnAnimation = true;

        BorderEnabled = true;
        BorderInnerSize = PictureBorderStyle.DefaultInnerSize;
        BorderFillSize = PictureBorderStyle.DefaultFillSize;
        BorderOuterSize = PictureBorderStyle.DefaultOuterSize;
        BorderInnerColor = PictureBorderStyle.DefaultInnerColor;
        BorderFillColor = PictureBorderStyle.DefaultFillColor;
        BorderOuterColor = PictureBorderStyle.DefaultOuterColor;

        LastFolder = "";
    }

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

    /// <summary>生效的缩放百分比（夹到 25 ~ 800）。</summary>
    [JsonIgnore]
    public int ZoomPercentEffective => PictureScrollMath.EffectiveZoomPercent(ZoomPercent);

    /// <summary>缩放系数（100% → 1.0）。</summary>
    [JsonIgnore]
    public double ZoomScale => PictureScrollMath.ZoomScale(ZoomPercentEffective);

    /// <summary>生效的翻页模式滚轮动作（手改坏配置时退回翻页）。</summary>
    [JsonIgnore]
    public PictureWheelAction PagedWheelEffective => PictureWheelActions.Normalize(PagedWheel);

    /// <summary>当前方式的说明（设置面板里一行小字）。</summary>
    [JsonIgnore]
    public string ScrollModeHint => PictureScrollModes.Hint(ScrollModeEffective);

    /// <summary>生效的图片边框样式（坏值已夹取 / 颜色已规范化）。</summary>
    [JsonIgnore]
    public PictureBorderStyle BorderStyle => PictureBorderStyle.FromConfig(this);
}
