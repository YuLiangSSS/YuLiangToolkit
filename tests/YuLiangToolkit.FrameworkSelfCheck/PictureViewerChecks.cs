using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PictureViewer;
using YuLiangToolkit.Tools.PictureViewer.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「图片连续阅读器」（picture-viewer）的自检：滚轮 / 拖拽 / 中键锚点的偏移数学、中键单点-按住状态机、
/// 页序自然排序、排版尺寸、真扫一个临时图片文件夹、无头跑一遍 ViewModel 的加载流程。
///
/// <para>界面里的鼠标事件本身没法无头跑（要真的按鼠标），所以把「按了之后该滚多少」抽成
/// <see cref="PictureScrollMath"/> / <see cref="PictureAnchorMachine"/> 在这里逐条钉死；
/// 三种滚动方式的真实版式用截图 + <c>YLT_PICTURE_MEASURE</c> 核对（见 docs/dev/pitfalls.md）。</para>
/// </summary>
internal static class PictureViewerChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        // ---------------------------------------------------------------- 1. 三种滚动方式
        Check("[图片] 纵向不是横向、也不是反向",
            !PictureScrollModes.IsHorizontal(PictureScrollMode.Vertical)
            && !PictureScrollModes.IsReverse(PictureScrollMode.Vertical));
        Check("[图片] 左→右是横向但不反向",
            PictureScrollModes.IsHorizontal(PictureScrollMode.HorizontalLeftToRight)
            && !PictureScrollModes.IsReverse(PictureScrollMode.HorizontalLeftToRight));
        Check("[图片] 右→左是横向且反向（第一张摆最右边）",
            PictureScrollModes.IsHorizontal(PictureScrollMode.HorizontalRightToLeft)
            && PictureScrollModes.IsReverse(PictureScrollMode.HorizontalRightToLeft));
        Check("[图片] 坏枚举值归一化成纵向",
            PictureScrollModes.Normalize((PictureScrollMode)99) == PictureScrollMode.Vertical
            && PictureScrollModes.Normalize(PictureScrollMode.HorizontalRightToLeft) == PictureScrollMode.HorizontalRightToLeft);

        var describes = Enum.GetValues<PictureScrollMode>().Select(PictureScrollModes.Describe).ToList();
        Check("[图片] 六种阅读方式都有名字且互不相同",
            describes.Count == 6 && describes.All(d => d.Length > 0) && describes.Distinct().Count() == 6,
            string.Join(" / ", describes));
        Check("[图片] 每种方式都有说明文案",
            Enum.GetValues<PictureScrollMode>().All(m => PictureScrollModes.Hint(m).Length > 5));
        Check("[图片] 短名字（分段/按钮用）也互不相同",
            Enum.GetValues<PictureScrollMode>().Select(PictureScrollModes.DescribeCompact).Distinct().Count() == 6);

        // 连续滚动 vs 翻页 / 单张：界面、滚轮、拖拽都按这个分类走
        Check("[图片] 三种连续滚动方式归为「连续」",
            PictureScrollModes.IsContinuous(PictureScrollMode.Vertical)
            && PictureScrollModes.IsContinuous(PictureScrollMode.HorizontalLeftToRight)
            && PictureScrollModes.IsContinuous(PictureScrollMode.HorizontalRightToLeft)
            && !PictureScrollModes.IsPaged(PictureScrollMode.Vertical));
        Check("[图片] 双页 / 单页归为「翻页」类",
            PictureScrollModes.IsPaged(PictureScrollMode.SpreadLeftToRight)
            && PictureScrollModes.IsPaged(PictureScrollMode.SpreadRightToLeft)
            && PictureScrollModes.IsPaged(PictureScrollMode.SinglePage)
            && !PictureScrollModes.IsContinuous(PictureScrollMode.SinglePage));
        Check("[图片] 只有双页算 spread，单页不算",
            PictureScrollModes.IsSpread(PictureScrollMode.SpreadLeftToRight)
            && PictureScrollModes.IsSpread(PictureScrollMode.SpreadRightToLeft)
            && PictureScrollModes.IsSinglePage(PictureScrollMode.SinglePage)
            && !PictureScrollModes.IsSpread(PictureScrollMode.SinglePage));
        Check("[图片] 翻页模式不属于「横向」（不吃长带那套阅读轴换算）",
            !PictureScrollModes.IsHorizontal(PictureScrollMode.SpreadLeftToRight)
            && !PictureScrollModes.IsHorizontal(PictureScrollMode.SinglePage));
        Check("[图片] 日式双页也是反向（右→左翻）",
            PictureScrollModes.IsReverse(PictureScrollMode.SpreadRightToLeft)
            && !PictureScrollModes.IsReverse(PictureScrollMode.SpreadLeftToRight)
            && !PictureScrollModes.IsReverse(PictureScrollMode.SinglePage));

        // ---------------------------------------------------------------- 1b. 翻页排版 / 页号（PicturePaging）
        Check("[图片] 页号夹取：负数是第一页、超界是最后一页、没图是 0",
            PicturePaging.NormalizePageIndex(-3, 10) == 0
            && PicturePaging.NormalizePageIndex(99, 10) == 9
            && PicturePaging.NormalizePageIndex(4, 0) == 0);

        Check("[图片] 双页左→右：左槽当前页、右槽下一张",
            PicturePaging.VisiblePages(PictureScrollMode.SpreadLeftToRight, 2, 10) == (2, 3));
        Check("[图片] 双页右→左：右槽当前页、左槽下一张（日式镜像）",
            PicturePaging.VisiblePages(PictureScrollMode.SpreadRightToLeft, 2, 10) == (3, 2));
        Check("[图片] 单页：只显示当前页，右槽空",
            PicturePaging.VisiblePages(PictureScrollMode.SinglePage, 4, 10) == (4, null));

        Check("[图片] 最后一屏不足两页时另一侧留空（左→右）",
            PicturePaging.VisiblePages(PictureScrollMode.SpreadLeftToRight, 4, 5) == (4, null)
            && PicturePaging.VisiblePages(PictureScrollMode.SpreadRightToLeft, 4, 5) == (null, 4));

        Check("[图片] 翻页步进：前进 +1、后退 -1，两端夹住",
            PicturePaging.StepPage(0, 5, forward: true) == 1
            && PicturePaging.StepPage(4, 5, forward: true) == 4
            && PicturePaging.StepPage(0, 5, forward: false) == 0
            && PicturePaging.StepPage(3, 5, forward: false) == 2);
        Check("[图片] 还能不能前后翻",
            PicturePaging.CanGoPrevious(0, 5) == false
            && PicturePaging.CanGoPrevious(1, 5)
            && PicturePaging.CanGoNext(3, 5)
            && PicturePaging.CanGoNext(4, 5) == false);

        Check("[图片] 双页排版：两页各占一半宽、内容 = 视口 × 缩放",
            SpreadLayoutCheck());

        Check("[图片] 单页排版：内容 = 视口 × 缩放，一页占满",
            SingleLayoutCheck());

        Check("[图片] 平移夹取：内容不大于视口时不能平移、超出时可以",
            PicturePaging.ClampPan(50, 50, 800, 600, 1000, 800) == (0, 0)
            && PicturePaging.ClampPan(500, 300, 2000, 1200, 1000, 800) == (500, 300)
            && PicturePaging.ClampPan(-20, 9999, 2000, 1200, 1000, 800) == (0, 400));

        Check("[图片] 日式翻页：左右按钮的“上一张/下一张”互换",
            PicturePaging.ButtonDirections(PictureScrollMode.SpreadLeftToRight) == (true, false)
            && PicturePaging.ButtonDirections(PictureScrollMode.SpreadRightToLeft) == (false, true)
            && PicturePaging.ButtonDirections(PictureScrollMode.SinglePage) == (true, false));

        // 双击图片打开 → 自动跳到那一张：普通方向取该页主轴起点，日式右→左从右边量
        Check("[图片] 某页开头的阅读偏移：普通方向 = 页起点",
            PictureScrollMath.ReadingOffsetForPage(reverse: false, totalLength: 400, pageStart: 100, pageLength: 80) == 100);
        Check("[图片] 某页开头的阅读偏移：日式反向 = 总长 − 该页末尾",
            PictureScrollMath.ReadingOffsetForPage(reverse: true, totalLength: 400, pageStart: 100, pageLength: 80) == 220);

        Check("[图片] 翻页模式滚轮动作：默认翻页、坏值归一到翻页",
            PictureWheelActions.Normalize(PictureWheelAction.Zoom) == PictureWheelAction.Zoom
            && PictureWheelActions.Normalize((PictureWheelAction)99) == PictureWheelAction.TurnPage
            && PictureWheelActions.Describe(PictureWheelAction.TurnPage).Contains("翻页")
            && PictureWheelActions.Describe(PictureWheelAction.Zoom) == "缩放");

        // ---------------------------------------------------------------- 1c. 图片边框（内边框 → 填充 → 外边框）
        var borderDefault = PictureBorderStyle.Default;
        Check("[图片] 边框默认样式：开、1/6/1 像素、每边总共 8 像素",
            borderDefault.Enabled && borderDefault.InnerSize == 1 && borderDefault.FillSize == 6
            && borderDefault.OuterSize == 1 && borderDefault.ExtraPerSide == 8
            && borderDefault.TotalExtra == 16 && borderDefault.HasAnyBorder);

        Check("[图片] 边框颜色解析：支持 #AARRGGBB / #RRGGBB / 不带 # / 0x",
            PictureBorderStyle.TryParseArgb("#80FF0000", out var argb80)
            && argb80 == 0x80FF0000
            && PictureBorderStyle.TryParseArgb("#FF0000", out var argbOpaque) && argbOpaque == 0xFFFF0000
            && PictureBorderStyle.TryParseArgb("11223344", out var argbBare) && argbBare == 0x11223344
            && PictureBorderStyle.TryParseArgb("0xFF00FF00", out var argb0X) && argb0X == 0xFF00FF00
            && !PictureBorderStyle.TryParseArgb("zzz", out _)
            && !PictureBorderStyle.TryParseArgb("", out _));

        Check("[图片] 边框颜色：拆分 / 合成来回一致、通道越界被夹到 0~255",
            PictureBorderStyle.Split(0x80A1B2C3) == (0xA1, 0xB2, 0xC3, 0x80)
            && PictureBorderStyle.Compose(0xA1, 0xB2, 0xC3, 0x80) == 0x80A1B2C3
            && PictureBorderStyle.Compose(999, -5, 300, 999) == 0xFFFF00FF
            && PictureBorderStyle.FromRgba(0, 0, 0, 0) == "#00000000");

        Check("[图片] 边框粗细夹取：内/外 0~32、填充 0~128",
            PictureBorderStyle.ClampBorder(-5) == 0
            && PictureBorderStyle.ClampBorder(999) == 32
            && PictureBorderStyle.ClampBorder(7) == 7
            && PictureBorderStyle.ClampFill(-1) == 0
            && PictureBorderStyle.ClampFill(9999) == 128
            && PictureBorderStyle.ClampFill(20) == 20);

        Check("[图片] 边框关闭 / 三层都是 0 时不算厚度",
            (borderDefault with { Enabled = false }).ExtraPerSide == 0
            && (borderDefault with { InnerSize = 0, FillSize = 0, OuterSize = 0 }).HasAnyBorder == false
            && (borderDefault with { InnerSize = 999, FillSize = 999, OuterSize = 999 }).ExtraPerSide == 32 + 128 + 32);

        var borderConfig = new PictureViewerConfig
        {
            BorderInnerSize = 3,
            BorderFillSize = 9,
            BorderOuterSize = 2,
            BorderInnerColor = "#80FFFFFF",
            BorderFillColor = "#123456",
            BorderOuterColor = "不是颜色",
        };
        var borderStyle = PictureBorderStyle.FromConfig(borderConfig);
        Check("[图片] 从配置读边框：颜色规范化、坏颜色回退默认",
            borderStyle.InnerSize == 3 && borderStyle.FillSize == 9 && borderStyle.OuterSize == 2
            && borderStyle.InnerColor == "#80FFFFFF"
            && borderStyle.FillColor == "#FF123456"
            && borderStyle.OuterColor == PictureBorderStyle.DefaultOuterColor
            && PictureBorderStyle.NormalizeColor("#80ffffff", PictureBorderStyle.DefaultInnerColor) == "#80FFFFFF");

        // 恢复默认：所有阅读 / 显示项回默认（默认阅读方式 = 纵向）
        var resetConfig = new PictureViewerConfig
        {
            ScrollMode = PictureScrollMode.SinglePage,
            WheelStepPixels = 300,
            MiddleHoldMilliseconds = 900,
            ZoomPercent = 250,
            InertiaEnabled = false,
            InertiaAccelerate = false,
            AutoUnloadOffscreen = false,
            PagedWheel = PictureWheelAction.Zoom,
            PageTurnAnimation = false,
            BorderEnabled = false,
            BorderInnerSize = 9,
            BorderFillSize = 40,
            BorderOuterSize = 7,
            BorderInnerColor = "#FFFF0000",
            BorderFillColor = "#FF00FF00",
            BorderOuterColor = "#FF0000FF",
            SingleInstanceEnabled = false,
            LastFolder = @"D:\some",
        };
        resetConfig.ResetToDefaults();
        Check("[图片] 恢复默认配置：阅读方式回纵向、缩放 100%、所有项回默认",
            resetConfig.ScrollModeEffective == PictureScrollMode.Vertical
            && resetConfig.WheelStepEffective == 120
            && resetConfig.HoldMillisecondsEffective == 250
            && resetConfig.ZoomPercentEffective == 100
            && resetConfig.InertiaEnabled && resetConfig.InertiaAccelerate && resetConfig.AutoUnloadOffscreen
            && resetConfig.PagedWheelEffective == PictureWheelAction.TurnPage
            && resetConfig.PageTurnAnimation
            && resetConfig.BorderStyle == PictureBorderStyle.Default
            && resetConfig.SingleInstanceEnabled
            && resetConfig.LastFolder == "");


        // ---------------------------------------------------------------- 2. 偏移：夹取 / 滚轮 / 拖拽
        Check("[图片] 偏移夹在 0 ~ 最大值之间",
            PictureScrollMath.ClampOffset(-50, 1000) == 0
            && PictureScrollMath.ClampOffset(500, 1000) == 500
            && PictureScrollMath.ClampOffset(5000, 1000) == 1000
            && PictureScrollMath.ClampOffset(500, 0) == 0
            && PictureScrollMath.ClampOffset(double.NaN, 1000) == 0);

        // 滚轮向下（Delta < 0）= 阅读往前进；一格默认 120px
        Check("[图片] 滚轮向下 = 阅读偏移变大",
            PictureScrollMath.WheelOffset(0, -120, 120, 1000) == 120);
        Check("[图片] 滚轮向上 = 阅读偏移变小",
            PictureScrollMath.WheelOffset(500, 120, 120, 1000) == 380);
        Check("[图片] 滚轮在两头会被夹住（不会滚出内容）",
            PictureScrollMath.WheelOffset(0, 120, 120, 1000) == 0
            && PictureScrollMath.WheelOffset(1000, -120, 120, 1000) == 1000);
        Check("[图片] 高速滚轮一格（Delta 240）走两格",
            PictureScrollMath.WheelOffset(0, -240, 120, 1000) == 240);
        Check("[图片] 一格步长超过 600 会被夹到 600、低于 20 夹到 20",
            PictureScrollMath.WheelOffset(0, -120, 9999, 10000) == 600
            && PictureScrollMath.WheelOffset(0, -120, 1, 10000) == 20);
        Check("[图片] 步长生效值 / 判定时长生效值的夹取",
            PictureScrollMath.EffectiveWheelStep(-5) == 20
            && PictureScrollMath.EffectiveWheelStep(120) == 120
            && PictureScrollMath.EffectiveWheelStep(100000) == 600
            && PictureScrollMath.EffectiveHoldMilliseconds(1) == 60
            && PictureScrollMath.EffectiveHoldMilliseconds(250) == 250
            && PictureScrollMath.EffectiveHoldMilliseconds(99999) == 2000);

        // 中键锚点：往阅读方向移动 = 前进
        Check("[图片] 锚点纵向：鼠标往下偏 → 阅读前进（正增量）",
            PictureScrollMath.AnchorDelta(PictureScrollMode.Vertical, 0, 120, 0, 20) > 0);
        Check("[图片] 锚点左→右：鼠标往右偏 → 阅读前进（正增量）",
            PictureScrollMath.AnchorDelta(PictureScrollMode.HorizontalLeftToRight, 120, 0, 20, 0) > 0);
        Check("[图片] 锚点右→左：鼠标往左偏 → 阅读前进（正增量，方向反过来）",
            PictureScrollMath.AnchorDelta(PictureScrollMode.HorizontalRightToLeft, 20, 0, 120, 0) > 0);
        Check("[图片] 锚点停在原点 → 不滚",
            PictureScrollMath.AnchorDelta(PictureScrollMode.Vertical, 40, 50, 40, 50) == 0);
        Check("[图片] 锚点灵敏度不合法时退回默认值、过大的被夹住",
            Math.Abs(PictureScrollMath.AnchorDelta(PictureScrollMode.Vertical, 0, 100, 0, 0, double.NaN)
                     - 100 * PictureScrollMath.DefaultAnchorSensitivity) < 1e-9
            && PictureScrollMath.AnchorDelta(PictureScrollMode.Vertical, 0, 100, 0, 0, 99)
               == PictureScrollMath.AnchorDelta(PictureScrollMode.Vertical, 0, 100, 0, 0, 3.0));

        // 拖拽：内容跟着手走（纵向往下拖 = 看上面的内容 = 偏移变小）
        Check("[图片] 拖拽纵向：往下拖 → 阅读偏移变小",
            PictureScrollMath.DragDelta(PictureScrollMode.Vertical, 0, 60) < 0);
        Check("[图片] 拖拽左→右：往右拖 → 阅读偏移变小",
            PictureScrollMath.DragDelta(PictureScrollMode.HorizontalLeftToRight, 60, 0) < 0);
        Check("[图片] 拖拽右→左：往右拖 → 阅读偏移变大（阅读方向与物理方向相反）",
            PictureScrollMath.DragDelta(PictureScrollMode.HorizontalRightToLeft, 60, 0) > 0);
        Check("[图片] 拖拽只看滚动轴：纵向忽略横向位移、横向忽略纵向位移",
            PictureScrollMath.DragDelta(PictureScrollMode.Vertical, 500, 0) == 0
            && PictureScrollMath.DragDelta(PictureScrollMode.HorizontalLeftToRight, 0, 500) == 0);

        // ---------------------------------------------------------------- 缩放 / 跨轴拖拽（v0.13.0）
        Check("[图片] 缩放百分比夹在 25 ~ 800（默认 100）",
            PictureScrollMath.EffectiveZoomPercent(100) == 100
            && PictureScrollMath.EffectiveZoomPercent(10) == 25
            && PictureScrollMath.EffectiveZoomPercent(9999) == 800
            && PictureScrollMath.DefaultZoomPercent == 100);
        Check("[图片] 缩放百分比换算系数（100% → 1.0、200% → 2.0、25% → 0.25）",
            Math.Abs(PictureScrollMath.ZoomScale(100) - 1.0) < 1e-9
            && Math.Abs(PictureScrollMath.ZoomScale(200) - 2.0) < 1e-9
            && Math.Abs(PictureScrollMath.ZoomScale(25) - 0.25) < 1e-9);
        Check("[图片] 放大镜按钮一步 10%，到边界不会越界",
            PictureScrollMath.StepZoomPercent(100, 1) == 110
            && PictureScrollMath.StepZoomPercent(100, -1) == 90
            && PictureScrollMath.StepZoomPercent(800, 1) == 800
            && PictureScrollMath.StepZoomPercent(25, -1) == 25);

        Check("[图片] 缩放后跨轴也能拖：纵向看左右、横向看上下",
            PictureScrollMath.CrossDragDelta(PictureScrollMode.Vertical, 60, 0) < 0
            && PictureScrollMath.CrossDragDelta(PictureScrollMode.HorizontalLeftToRight, 0, 60) < 0
            && PictureScrollMath.CrossDragDelta(PictureScrollMode.HorizontalRightToLeft, 0, 60) < 0);
        Check("[图片] 跨轴拖拽忽略另一个轴：纵向看 dx、横向看 dy",
            PictureScrollMath.CrossDragDelta(PictureScrollMode.Vertical, 0, 500) == 0
            && PictureScrollMath.CrossDragDelta(PictureScrollMode.HorizontalLeftToRight, 500, 0) == 0);
        Check("[图片] 内容跟着手走：指针往右下拖，两个轴都往“前/左上”方向退",
            PictureScrollMath.DragDelta(PictureScrollMode.Vertical, 0, 60) < 0
            && PictureScrollMath.CrossDragDelta(PictureScrollMode.Vertical, 60, 0) < 0);

        // ---------------------------------------------------------------- 3. 按比例定位
        Check("[图片] 按比例换算偏移（0 / 50% / 100%）",
            PictureScrollMath.OffsetForRatio(0, 1000) == 0
            && PictureScrollMath.OffsetForRatio(0.5, 1000) == 500
            && PictureScrollMath.OffsetForRatio(1, 1000) == 1000);
        Check("[图片] 比例超界会被夹住、内容不够长时是 0",
            PictureScrollMath.OffsetForRatio(-1, 1000) == 0
            && PictureScrollMath.OffsetForRatio(9, 1000) == 1000
            && PictureScrollMath.OffsetForRatio(0.5, 0) == 0);
        Check("[图片] 偏移 → 比例 → 偏移 能还原",
            Math.Abs(PictureScrollMath.OffsetForRatio(
                PictureScrollMath.RatioOf(345, 1000), 1000) - 345) < 1e-9);

        // ---------------------------------------------------------------- 4. 阅读偏移 ↔ ScrollViewer 物理偏移
        // 日式右→左是「图片物理反着摆」：物理 0（最左）其实是阅读的末尾 —— 换方向保持位置、锚点 / 滚轮
        // 全建立在下面这几个换算上，必须断言（复核发现「换方向后位置失效」时这里一条都没有）。
        Check("[图片] 阅读总长度：横向取水平可滚范围、纵向取垂直",
            PictureScrollMath.ReadingMaximum(PictureScrollMode.Vertical, 500, 900) == 900
            && PictureScrollMath.ReadingMaximum(PictureScrollMode.HorizontalLeftToRight, 500, 900) == 500
            && PictureScrollMath.ReadingMaximum(PictureScrollMode.HorizontalRightToLeft, 500, 900) == 500
            && PictureScrollMath.ReadingMaximum(PictureScrollMode.Vertical, -1, -1) == 0);

        Check("[图片] 左→右：物理水平偏移 = 阅读偏移（往返一致）",
            PictureScrollMath.ReadingOffset(PictureScrollMode.HorizontalLeftToRight, 300, 0, 1000, 0) == 300
            && PictureScrollMath.PhysicalHorizontalOffset(PictureScrollMode.HorizontalLeftToRight, 300, 1000) == 300);

        Check("[图片] 右→左：物理最左是阅读末尾、物理最右才是开头",
            PictureScrollMath.ReadingOffset(PictureScrollMode.HorizontalRightToLeft, 1000, 0, 1000, 0) == 0
            && PictureScrollMath.ReadingOffset(PictureScrollMode.HorizontalRightToLeft, 0, 0, 1000, 0) == 1000);

        Check("[图片] 右→左：物理偏移 ↔ 阅读偏移 往返一致",
            Math.Abs(PictureScrollMath.PhysicalHorizontalOffset(
                PictureScrollMode.HorizontalRightToLeft,
                PictureScrollMath.ReadingOffset(PictureScrollMode.HorizontalRightToLeft, 260, 0, 1000, 0),
                1000) - 260) < 1e-9);

        Check("[图片] 纵向：阅读偏移就是垂直偏移",
            PictureScrollMath.ReadingOffset(PictureScrollMode.Vertical, 999, 420, 0, 800) == 420);

        Check("[图片] 换算后偏移仍被夹在 0 ~ 总长里",
            PictureScrollMath.ReadingOffset(PictureScrollMode.HorizontalRightToLeft, -500, 0, 1000, 0) == 1000
            && PictureScrollMath.PhysicalHorizontalOffset(PictureScrollMode.HorizontalRightToLeft, 5000, 1000) == 0);

        // ---------------------------------------------------------------- 6. 排版尺寸（统一宽度 / 统一高度）
        Check("[图片] 统一跨轴尺寸 = min(图最大边, 视口边)",
            PictureScrollMath.UniformCrossSize(2000, 1000) == 1000
            && PictureScrollMath.UniformCrossSize(600, 1000) == 600
            && PictureScrollMath.UniformCrossSize(0, 1000) == 1000
            && PictureScrollMath.UniformCrossSize(1000, 0) == 0);
        Check("[图片] 纵向：按统一宽度算高度（1000×1440 放到 1048 宽 → 1509.12）",
            Math.Abs(PictureScrollMath.MainLengthForAspect(false, 1000, 1440, 1048) - (1048.0 * 1440 / 1000)) < 1e-6);
        Check("[图片] 横向：按统一高度算宽度（1440×1000 放到 641 高 → 923.04）",
            Math.Abs(PictureScrollMath.MainLengthForAspect(true, 1440, 1000, 641) - (641.0 * 1440 / 1000)) < 1e-6);
        Check("[图片] 尺寸信息坏掉时退回统一尺寸（不除以 0）",
            PictureScrollMath.MainLengthForAspect(false, 0, 1440, 1048) == 1048
            && PictureScrollMath.MainLengthForAspect(true, 1440, 0, 641) == 641);

        // ---------------------------------------------------------------- 7. 中键锚点状态机（单点 / 按住）
        var anchor = new PictureAnchorMachine();
        Check("[图片] 中键初始没有锚点", anchor.Phase == PictureAnchorPhase.None && !anchor.IsActive);

        anchor.MiddleDown(0);
        Check("[图片] 按下中键 → 锚点按着（Pressed，立刻能滚）",
            anchor.Phase == PictureAnchorPhase.Pressed && anchor.IsActive && anchor.IsPressed && !anchor.IsLatched);

        anchor.MiddleUp(100);
        Check("[图片] 100ms 就松开 = 单点 → 锚点留下（Latched）",
            anchor.Phase == PictureAnchorPhase.Latched && anchor.IsActive && anchor.IsLatched);

        anchor.OtherButtonDown();
        Check("[图片] 锚点留着时按任意键 → 锚点消失（PictureView 原来的行为）",
            anchor.Phase == PictureAnchorPhase.None && !anchor.IsActive);

        anchor.MiddleDown(1000);
        anchor.MiddleUp(1300);
        Check("[图片] 按住超过判定时长再松开 = 按住 → 锚点跟着消失",
            anchor.Phase == PictureAnchorPhase.None);

        anchor.HoldThresholdMilliseconds = 500;
        anchor.MiddleDown(2000);
        anchor.MiddleUp(2400);
        Check("[图片] 判定时长改成 500ms 后，400ms 仍算单点",
            anchor.Phase == PictureAnchorPhase.Latched && anchor.HoldMillisecondsEffective == 500);

        anchor.MiddleDown(3000);
        Check("[图片] 锚点留着时再按中键 → 关掉锚点",
            anchor.Phase == PictureAnchorPhase.None);

        anchor.MiddleDown(4000);
        anchor.OtherButtonDown();
        Check("[图片] 按住中键的过程中按别的键不打断（手还按着，松手才结束）",
            anchor.Phase == PictureAnchorPhase.Pressed);
        anchor.MiddleUp(4080);
        Check("[图片] 按住 80ms（判定 500ms）松手 → 单点，锚点留下",
            anchor.Phase == PictureAnchorPhase.Latched);

        anchor.HoldThresholdMilliseconds = 10;
        Check("[图片] 判定时长过小会被夹到 60ms", anchor.HoldMillisecondsEffective == 60);
        anchor.Reset();
        Check("[图片] Reset 强制清掉锚点", anchor.Phase == PictureAnchorPhase.None);

        anchor.HoldThresholdMilliseconds = 999999;
        Check("[图片] 判定时长过大会被夹到 2000ms", anchor.HoldMillisecondsEffective == 2000);
        anchor.Reset();

        var idleAnchor = new PictureAnchorMachine();
        Check("[图片] 没按中键时松手中键 / 按别的键都不会凭空造出锚点",
            idleAnchor.MiddleUp(123) == PictureAnchorPhase.None
            && idleAnchor.OtherButtonDown() == PictureAnchorPhase.None
            && !idleAnchor.IsActive);

        // ---------------------------------------------------------------- 拖动惯性（纯逻辑：采样 / 衰减 / 叠加）
        var inertia = new PictureInertia();
        inertia.Reset();
        inertia.Note(0, 0, 0);
        inertia.Note(100, 300, -120);
        var velocity = inertia.ComputeVelocity();
        Check("[图片] 惯性速度 = 采样窗口里的位移 / 时间",
            Math.Abs(velocity.Main - 3.0) < 1e-9 && Math.Abs(velocity.Cross + 1.2) < 1e-9,
            $"main={velocity.Main} cross={velocity.Cross}");

        inertia.Reset();
        inertia.Note(0, 100, 100);
        inertia.Note(100, 100, 100);
        Check("[图片] 停着不动 → 速度为 0（不会自己滑）",
            inertia.ComputeVelocity() == (0.0, 0.0));
        inertia.Reset();
        Check("[图片] 只有一个采样点 / 没采样 → 速度为 0", inertia.ComputeVelocity() == (0.0, 0.0));

        var decayed = inertia.Decay(2.0, -1.0, 100);
        Check("[图片] 衰减：方向不变、速度变小、时间越久越小",
            decayed.Main > 0 && decayed.Main < 2.0
            && decayed.Cross < 0 && decayed.Cross > -1.0
            && inertia.Decay(2.0, -1.0, 200).Main < decayed.Main);
        Check("[图片] 衰减：时间为 0 / 负数时原样返回",
            inertia.Decay(2.0, -1.0, 0) == (2.0, -1.0)
            && inertia.Decay(2.0, -1.0, -5) == (2.0, -1.0));

        Check("[图片] 太慢就不起滑（合成速度低于阈值）",
            !inertia.IsFastEnough(0.01, 0.0)
            && inertia.IsFastEnough(0.1, 0.0)
            && !PictureInertia.IsFastEnough(double.NaN, 0, PictureInertia.DefaultMinimumSpeed));

        // 「越滑越快」：第二次甩动把还在滑的残余速度加上去，而不是清零
        var combined = PictureInertia.Combine(1.0, 0.0, 1.5, 0.0, accumulate: true);
        Check("[图片] 连续甩动会叠加：新速度 = 本次 + 残余（越滑越快）",
            Math.Abs(combined.Main - 2.5) < 1e-9 && combined.Cross == 0);
        var resetVelocity = PictureInertia.Combine(1.0, 0.0, 1.5, 0.0, accumulate: false);
        Check("[图片] 关掉叠加时只用本次甩动的速度（不把速度越滚越大）",
            Math.Abs(resetVelocity.Main - 1.0) < 1e-9);
        var clamped = PictureInertia.Combine(5.0, 0.0, 5.0, 0.0, accumulate: true);
        Check("[图片] 叠加有速度上限（不会失控滑穿整本）",
            Math.Abs(clamped.Main - PictureInertia.DefaultMaxSpeed) < 1e-9,
            $"main={clamped.Main}");
        var clampedDiagonal = PictureInertia.Combine(4.0, 4.0, 4.0, 4.0, accumulate: true);
        Check("[图片] 斜着叠加上限：合速度被压到上限、方向不变",
            Math.Abs(Math.Sqrt(clampedDiagonal.Main * clampedDiagonal.Main
                               + clampedDiagonal.Cross * clampedDiagonal.Cross)
                     - PictureInertia.DefaultMaxSpeed) < 1e-9
            && Math.Abs(clampedDiagonal.Main - clampedDiagonal.Cross) < 1e-9);

        // ---------------------------------------------------------------- 8. 页序与图片类型
        Check("[图片] 认识的图片扩展名",
            PictureScanner.IsImageFile("a.jpg") && PictureScanner.IsImageFile("B.JPEG")
            && PictureScanner.IsImageFile("c.png") && PictureScanner.IsImageFile("d.bmp")
            && PictureScanner.IsImageFile("e.gif") && PictureScanner.IsImageFile("f.webp")
            && PictureScanner.IsImageFile("g.tif") && PictureScanner.IsImageFile("h.TIFF"));
        Check("[图片] 不是图片的扩展名一律不收",
            !PictureScanner.IsImageFile("a.mp4") && !PictureScanner.IsImageFile("b.txt")
            && !PictureScanner.IsImageFile("") && !PictureScanner.IsImageFile(null));

        Check("[图片] 自然排序：2 在 10 前面（不是字典序）",
            PictureScanner.CompareNatural("2.png", "10.png") < 0
            && PictureScanner.CompareNatural("10.png", "2.png") > 0);
        Check("[图片] 自然排序：前导零不影响页序",
            PictureScanner.CompareNatural("001.png", "2.png") < 0
            && PictureScanner.CompareNatural("1.png", "01.png") == 0);
        Check("[图片] 自然排序：非数字名也能比较（大小写不敏感）",
            PictureScanner.CompareNatural("cover.png", "page2.png") < 0
            && PictureScanner.CompareNatural("A.png", "a.png") == 0);
        Check("[图片] 自然排序：数字段长度不同时按数值比",
            PictureScanner.CompareNatural("第2话.png", "第10话.png") < 0);

        // ---------------------------------------------------------------- 9. 真扫一个临时图片文件夹
        var folder = Path.Combine(AppPaths.Root, "picture-viewer-fixture");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        WritePng(Path.Combine(folder, "10.png"), 6, 7);
        WritePng(Path.Combine(folder, "2.png"), 4, 5);
        WritePng(Path.Combine(folder, "1.png"), 2, 3);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "不是图片");
        File.WriteAllText(Path.Combine(folder, "broken.png"), "这不是真的 PNG");

        var files = PictureScanner.EnumerateFiles(folder);
        // 枚举阶段只按扩展名收（坏图的扩展名也是 .png，要等读文件头时才知道读不出来）
        Check("[图片] 扫文件夹只按扩展名收图片、并按页序排好（坏图留到读尺寸时再筛）",
            files.Select(Path.GetFileName).SequenceEqual(new[] { "1.png", "2.png", "10.png", "broken.png" }),
            string.Join(",", files.Select(Path.GetFileName)));

        var pages = PictureScanner.ScanFolder(folder);
        Check("[图片] 读得出每张图的原始像素尺寸（读不出的坏图跳过）",
            pages.Count == 3
            && pages[0].PixelWidth == 2 && pages[0].PixelHeight == 3
            && pages[1].PixelWidth == 4 && pages[1].PixelHeight == 5
            && pages[2].PixelWidth == 6 && pages[2].PixelHeight == 7,
            string.Join(" / ", pages.Select(p => $"{p.FileName}:{p.PixelWidth}x{p.PixelHeight}")));
        Check("[图片] 页对象的宽高比算得对",
            Math.Abs(pages[2].AspectRatio - 6.0 / 7.0) < 1e-9);
        Check("[图片] 不存在的文件夹返回空清单（不抛异常）",
            PictureScanner.EnumerateFiles(Path.Combine(folder, "不存在")).Count == 0
            && PictureScanner.ScanFolder(null).Count == 0);

        var progressCalls = 0;
        var progressTotal = 0;
        PictureScanner.ScanFolder(folder, (done, total) => { progressTotal = total; Interlocked.Increment(ref progressCalls); });
        Check("[图片] 扫描进度回调拿得到总数（含读不出的那张，回调次数 = 文件数）",
            progressTotal == files.Count && progressCalls == files.Count,
            $"calls={progressCalls} total={progressTotal} files={files.Count}");

        // ---------------------------------------------------------------- 10. 配置：默认值 / 枚举按字符串存 / 夹取
        var configStore = ToolConfigStore.For(PictureViewerConfig.ToolId);
        var cfg = configStore.Load<PictureViewerConfig>();
        Check("[图片] 配置默认值（纵向 / 滚轮 120 / 判定 250ms / 缩放 100% / 惯性开 / 自动释放开）",
            cfg.ScrollModeEffective == PictureScrollMode.Vertical
            && cfg.WheelStepEffective == 120
            && cfg.HoldMillisecondsEffective == 250
            && cfg.ZoomPercentEffective == 100
            && Math.Abs(cfg.ZoomScale - 1.0) < 1e-9
            && cfg.InertiaEnabled
            && cfg.InertiaAccelerate
            && cfg.PagedWheelEffective == PictureWheelAction.TurnPage
            && cfg.PageTurnAnimation
            && cfg.SingleInstanceEnabled
            && cfg.AutoUnloadOffscreen,
            $"{cfg.ScrollModeEffective} / {cfg.WheelStepEffective} / {cfg.HoldMillisecondsEffective} / {cfg.ZoomPercentEffective}%");

        cfg.ScrollMode = PictureScrollMode.HorizontalRightToLeft;
        cfg.WheelStepPixels = 9999;
        cfg.MiddleHoldMilliseconds = 1;
        cfg.ZoomPercent = 9999;
        cfg.PagedWheel = PictureWheelAction.Zoom;
        cfg.PageTurnAnimation = false;
        Thread.Sleep(900);
        var savedJson = File.Exists(configStore.FilePath) ? File.ReadAllText(configStore.FilePath) : "";
        Check("[图片] 枚举按 C# 原名存字符串（手改配置看得懂）",
            savedJson.Contains("\"HorizontalRightToLeft\"") && savedJson.Contains("\"Zoom\""),
            savedJson.Replace(Environment.NewLine, " "));
        Check("[图片] 翻页设置也落盘（滚轮缩放 / 关掉滑动动画）",
            savedJson.Contains("\"pagedWheel\": \"Zoom\"") && savedJson.Contains("\"pageTurnAnimation\": false"),
            savedJson.Replace(Environment.NewLine, " "));
        Check("[图片] 手改坏的范围在生效值里被夹住（含缩放 25~800）",
            cfg.WheelStepEffective == 600 && cfg.HoldMillisecondsEffective == 60
            && cfg.ZoomPercentEffective == 800,
            $"{cfg.WheelStepEffective} / {cfg.HoldMillisecondsEffective} / {cfg.ZoomPercentEffective}");
        cfg.PagedWheel = (PictureWheelAction)99;
        Check("[图片] 翻页滚轮动作坏值归一化", cfg.PagedWheelEffective == PictureWheelAction.TurnPage);

        // ---------------------------------------------------------------- 11. 无头跑一遍 ViewModel
        var vm = new PictureViewerViewModel();
        var pagesChanged = 0;
        vm.PagesChanged += (_, _) => pagesChanged++;

        vm.FolderPicker = () => folder;
        vm.OpenFolderCommand.Execute(null);
        WaitUntil(() => vm is { IsBusy: false, HasPages: true }, 5000);

        Check("[图片] 选文件夹后加载出页清单（3 张、按页序）",
            vm.Pages.Count == 3 && vm.Pages[0].FileName == "1.png" && vm.Pages[2].FileName == "10.png",
            string.Join(",", vm.Pages.Select(p => p.FileName)));
        Check("[图片] 工具条文案：张数 + 文件夹名、路径",
            vm.ImageCountText.Contains("3 张") && vm.FolderPathText == folder,
            $"{vm.ImageCountText} / {vm.FolderPathText}");
        Check("[图片] 加载完记下最后打开的文件夹（下次对话框从这里开始）", vm.Cfg.LastFolder == folder);
        Check("[图片] 加载完通知界面重建长带（PagesChanged）", pagesChanged > 0);
        Check("[图片] 加载完不在忙碌状态、状态文字是人话",
            !vm.IsBusy && vm.StatusText.Contains("3"), vm.StatusText);

        // ---- 点工具条第 2 行的文件夹路径 → 在资源管理器里打开（自检换掉打开器，不真弹资源管理器） ----
        string? openedFolder = null;
        vm.FolderOpener = path => { openedFolder = path; return true; };
        Check("[图片] 有文件夹时路径行可点（HasFolder）", vm.HasFolder);
        vm.OpenFolderInExplorerCommand.Execute(null);
        Check("[图片] 点路径把当前文件夹交给资源管理器打开", openedFolder == folder, openedFolder ?? "(null)");
        vm.FolderOpener = _ => false;
        vm.OpenFolderInExplorerCommand.Execute(null);
        Check("[图片] 文件夹打不开时给提示", vm.StatusText.Contains("打开文件夹失败"), vm.StatusText);
        vm.FolderOpener = null;

        // ---- 双击图片打开（文件关联 / 命令行） ----
        Check("[图片] 文件关联：支持的后缀表合法（小写、带点、无重复、含常见格式）",
            PictureFileAssociation.SupportedExtensions.Count == 8
            && PictureFileAssociation.SupportedExtensions.All(e => e.StartsWith('.') && e == e.ToLowerInvariant())
            && PictureFileAssociation.SupportedExtensions.Distinct().Count() == 8
            && PictureFileAssociation.SupportedExtensions.Contains(".png")
            && PictureFileAssociation.SupportedExtensions.Contains(".webp"));
        Check("[图片] 文件关联：后缀匹配不区分大小写、不认识的扩展名拒绝",
            PictureFileAssociation.IsSupportedExtension(".PNG")
            && PictureFileAssociation.IsSupportedExtension(" .Jpeg ")
            && !PictureFileAssociation.IsSupportedExtension(".jxl")
            && !PictureFileAssociation.IsSupportedExtension(""));
        Check("[图片] 文件关联：打开命令给 exe 和 %1 都加引号（路径有空格也不会坏）",
            PictureFileAssociation.BuildOpenCommand(@"C:\Program Files\YuLiang Toolkit\app.exe")
            == "\"C:\\Program Files\\YuLiang Toolkit\\app.exe\" \"%1\"");
        Check("[图片] 文件关联：只有真实存在的受支持图片才当作启动参数",
            PictureFileAssociation.IsSupportedImageFile(Path.Combine(folder, "1.png"))
            && !PictureFileAssociation.IsSupportedImageFile(Path.Combine(folder, "不存在.png"))
            && !PictureFileAssociation.IsSupportedImageFile(Path.Combine(folder, "1.txt")));

        var launchTarget = Path.Combine(folder, "10.png");
        vm.LoadFolderAsync(folder, launchTarget).GetAwaiter().GetResult();
        Check("[图片] 双击打开：加载所在文件夹并定位到指定图片（10.png 是第 3 张）",
            vm.Pages.Count == 3
            && vm.ConsumePendingPageIndex() == 2
            && vm.StatusText.Contains("已定位到第 3 张"),
            vm.StatusText);
        Check("[图片] 定位只消费一次（换模式 / 重建不会反复跳回去）",
            vm.ConsumePendingPageIndex() is null);
        vm.LoadFolderAsync(folder, Path.Combine(folder, "没有这张.png")).GetAwaiter().GetResult();
        Check("[图片] 指定的图不在文件夹里：不定位（停在开头）",
            vm.ConsumePendingPageIndex() is null && !vm.StatusText.Contains("已定位"));

        // ---- 图片边框 / 恢复默认（无头跑） ----
        var borderEvents = 0;
        vm.BorderChanged += (_, _) => borderEvents++;

        // 窗口 / 多开：默认单实例；改一下要写回配置，恢复默认要回 true
        Check("[图片] 单实例开关默认开（设置面板绑定它）", vm.SingleInstanceEnabled);
        vm.SingleInstanceEnabled = false;
        Check("[图片] 关掉单实例开关会写回配置（多实例）", !vm.Cfg.SingleInstanceEnabled);
        vm.SingleInstanceEnabled = true;
        Check("[图片] 再打开单实例开关也写回配置", vm.Cfg.SingleInstanceEnabled);

        Check("[图片] 边框颜色编辑器：默认值与配置一致",
            vm.BorderInnerColorEditor.Hex == PictureBorderStyle.DefaultInnerColor
            && vm.BorderFillColorEditor.Hex == PictureBorderStyle.DefaultFillColor
            && vm.BorderOuterColorEditor.Hex == PictureBorderStyle.DefaultOuterColor,
            $"{vm.BorderInnerColorEditor.Hex} / {vm.BorderFillColorEditor.Hex} / {vm.BorderOuterColorEditor.Hex}");

        vm.BorderInnerColorEditor.R = 255;
        vm.BorderInnerColorEditor.G = 128;
        vm.BorderInnerColorEditor.B = 0;
        vm.BorderInnerColorEditor.A = 200;
        Check("[图片] 改 RGBA 立刻写回配置（0 ~ 255，透明通道也在）",
            vm.BorderInnerColorEditor.Hex == "#C8FF8000" && vm.Cfg.BorderInnerColor == "#C8FF8000",
            vm.Cfg.BorderInnerColor);

        vm.PickFillColorCommand.Execute("#FF112233");
        Check("[图片] 调色板：点一下把颜色填进填充色", vm.Cfg.BorderFillColor == "#FF112233" && vm.BorderFillColorEditor.Hex == "#FF112233");

        vm.BorderInnerSize = 999;
        vm.BorderFillSize = 999;
        vm.BorderOuterSize = -5;
        Check("[图片] 边框粗细越界会被夹取（内/外 32、填充 128、最小 0）",
            vm.BorderInnerSize == 32 && vm.BorderFillSize == 128 && vm.BorderOuterSize == 0);

        vm.BorderEnabled = false;
        Check("[图片] 关掉边框 / 改颜色 / 改粗细都会通知界面重排", borderEvents >= 4, $"events={borderEvents}");

        vm.ResetSettingsCommand.Execute(null);
        Check("[图片] 恢复默认：阅读方式回纵向、缩放 100%、边框回默认、编辑器同步",
            vm.Cfg.ScrollModeEffective == PictureScrollMode.Vertical
            && !vm.IsPagedMode
            && vm.Cfg.ZoomPercentEffective == 100
            && vm.BorderEnabled
            && vm.BorderInnerSize == PictureBorderStyle.DefaultInnerSize
            && vm.BorderFillSize == PictureBorderStyle.DefaultFillSize
            && vm.BorderOuterSize == PictureBorderStyle.DefaultOuterSize
            && vm.BorderInnerColorEditor.Hex == PictureBorderStyle.DefaultInnerColor
            && vm.BorderFillColorEditor.Hex == PictureBorderStyle.DefaultFillColor
            && vm.BorderOuterColorEditor.Hex == PictureBorderStyle.DefaultOuterColor
            && vm.SingleInstanceEnabled
            && vm.Cfg.LastFolder == ""
            && vm.StatusText.Contains("恢复默认"),
            $"{vm.StatusText} / {vm.BorderInnerColorEditor.Hex}");
        Check("[图片] 恢复默认也触发了界面重排（BorderChanged）", borderEvents >= 5, $"events={borderEvents}");

        // ---- 阅读方式 / 翻页设置（无头跑） ----
        vm.Cfg.ScrollMode = PictureScrollMode.Vertical;   // 前面的配置检查把它改成了横向，这里先复位
        Check("[图片] 设置面板的阅读方式下拉有 6 个选项",
            vm.ModeOptions.Count == 6 && vm.ModeOptions.Select(o => o.Value).Distinct().Count() == 6,
            string.Join(" / ", vm.ModeOptions.Select(o => o.Label)));
        Check("[图片] 默认方式 = 纵向、不算翻页模式",
            vm.Cfg.ScrollModeEffective == PictureScrollMode.Vertical && !vm.IsPagedMode);
        vm.SelectedMode = vm.ModeOptions.First(o => o.Value == PictureScrollMode.SpreadRightToLeft);
        Check("[图片] 选「双页 右→左」后：配置更新、进入翻页模式、按钮文字跟着变",
            vm.Cfg.ScrollModeEffective == PictureScrollMode.SpreadRightToLeft
            && vm.IsPagedMode
            && vm.CurrentModeText == PictureScrollModes.Describe(PictureScrollMode.SpreadRightToLeft),
            $"{vm.CurrentModeText} / paged={vm.IsPagedMode}");
        vm.SelectedMode = vm.ModeOptions.First(o => o.Value == PictureScrollMode.Vertical);
        Check("[图片] 切回纵向后又变回连续模式",
            !vm.IsPagedMode && !PictureScrollModes.IsPaged(vm.Cfg.ScrollModeEffective));

        Check("[图片] 翻页滚轮选项：翻页 / 缩放两项，默认翻页",
            vm.WheelActionOptions.Count == 2
            && vm.WheelActionOptions.Select(o => o.Value).SequenceEqual(
                new[] { PictureWheelAction.TurnPage, PictureWheelAction.Zoom })
            && vm.SelectedWheelAction.Value == PictureWheelAction.TurnPage);
        vm.SelectedWheelAction = vm.WheelActionOptions.First(o => o.Value == PictureWheelAction.Zoom);
        Check("[图片] 选「缩放」后写进配置",
            vm.Cfg.PagedWheelEffective == PictureWheelAction.Zoom);
        vm.SelectedWheelAction = vm.WheelActionOptions.First(o => o.Value == PictureWheelAction.TurnPage);
        Check("[图片] 再选回「翻页」也写进配置",
            vm.Cfg.PagedWheelEffective == PictureWheelAction.TurnPage);

        // ---- 缩放：输入框 + 放大镜命令（无头跑，不需要界面） ----
        vm.Cfg.ZoomPercent = 100;
        vm.ZoomText = "250";
        Check("[图片] 缩放输入框：输入 250 生效并显示成 250%",
            vm.Cfg.ZoomPercentEffective == 250 && vm.ZoomText == "250%");
        vm.ZoomText = "10";
        Check("[图片] 缩放输入框：低于下限被夹到 25%",
            vm.Cfg.ZoomPercentEffective == 25 && vm.ZoomText == "25%");
        vm.ZoomText = "abc";
        Check("[图片] 缩放输入框：非法输入不改当前值",
            vm.Cfg.ZoomPercentEffective == 25 && vm.ZoomText == "25%");
        vm.ZoomOutCommand.Execute(null);
        Check("[图片] 缩小按钮一步 10%，已经到 25% 就不再变小", vm.Cfg.ZoomPercentEffective == 25);
        vm.ZoomInCommand.Execute(null);
        vm.ZoomInCommand.Execute(null);
        Check("[图片] 放大按钮两步 = 45%，文字跟着变",
            vm.Cfg.ZoomPercentEffective == 45 && vm.ZoomText == "45%");
        var zoomChanged = 0;
        vm.ZoomChanged += (_, _) => zoomChanged++;
        vm.ZoomText = "150";
        Check("[图片] 缩放变化会通知界面重新排版", zoomChanged == 1, $"events={zoomChanged}");

        // 空文件夹：不崩、给出「没有找到图片」
        var emptyFolder = Path.Combine(AppPaths.Root, "picture-viewer-empty");
        if (Directory.Exists(emptyFolder)) Directory.Delete(emptyFolder, recursive: true);
        Directory.CreateDirectory(emptyFolder);
        vm.LoadFolderAsync(emptyFolder).GetAwaiter().GetResult();
        Check("[图片] 空文件夹：没有页、给出人话提示",
            !vm.HasPages && vm.StatusText.Contains("没有找到图片"), vm.StatusText);

        // ---------------------------------------------------------------- 12. 工具注册信息
        var tool = new PictureViewerTool();
        Check("[图片] 工具 Id / 名称 / 分类 / 排序",
            tool.Id == "picture-viewer" && tool.Name == "图片连续阅读器"
            && tool.Category == "媒体" && tool.Order == 16);
        Check("[图片] 主页标签能搜到「漫画 / 滚动 / 中键」",
            tool.Tags.Contains("漫画") && tool.Tags.Contains("滚动") && tool.Tags.Contains("中键"),
            string.Join(",", tool.Tags));
        Check("[图片] 默认挂在主页根层（没有 CategoryPath）", ((ITool)tool).CategoryPath.Count == 0);

        // ---------------------------------------------------------------- 13. 全屏（沉浸阅读）状态机
        // 只断言 ViewModel 的状态机：真正的窗口操作（藏标题栏 / 侧边栏、铺满屏幕）在 View / 外壳
        // IFullscreenHost 里做，自检没有窗口；这里把「进 / 出全屏、工具条自动显隐、切走还原」钉死。
        var previousPictureFullscreenEnv = Environment.GetEnvironmentVariable("YLT_PICTURE_FULLSCREEN");
        Environment.SetEnvironmentVariable("YLT_PICTURE_FULLSCREEN", null);
        try
        {
            var fsVm = new PictureViewerViewModel();
            try
            {
                Check("[图片] 全屏初始：不是全屏、工具条可见、图标 E740、提示含「全屏」、行高 Auto",
                    !fsVm.IsFullscreen && fsVm.ControlsVisible && fsVm.IsToolbarVisible
                    && fsVm.FullscreenGlyph == "\uE740"
                    && fsVm.FullscreenTooltip.Contains("全屏")
                    && !fsVm.FullscreenTooltip.Contains("退出全屏")
                    && fsVm.ToolbarRowHeight.Equals(GridLength.Auto),
                    $"{fsVm.FullscreenGlyph} / {fsVm.FullscreenTooltip}");

                fsVm.HideControls();
                Check("[图片] 窗口模式下调 HideControls 不生效（普通模式工具条永远露着）",
                    fsVm.ControlsVisible && fsVm.IsToolbarVisible);

                var fullscreenEvents = new List<bool>();
                fsVm.FullscreenChanged += (_, on) => fullscreenEvents.Add(on);
                var changedProperties = new List<string>();
                ((INotifyPropertyChanged)fsVm).PropertyChanged += (_, e) => changedProperties.Add(e.PropertyName ?? "");

                fsVm.ToggleFullscreen();
                Check("[图片] 切换全屏：进入全屏并发出 FullscreenChanged(true)",
                    fsVm.IsFullscreen && fullscreenEvents.Count == 1 && fullscreenEvents[0],
                    string.Join(",", fullscreenEvents));
                Check("[图片] 全屏后：图标 E73F、提示语变成「退出全屏」",
                    fsVm.FullscreenGlyph == "\uE73F" && fsVm.FullscreenTooltip.Contains("退出全屏"),
                    fsVm.FullscreenTooltip);
                Check("[图片] 全屏后：工具条先亮着、行高归零（阅读区铺满）",
                    fsVm.ControlsVisible && fsVm.IsToolbarVisible
                    && fsVm.ToolbarRowHeight.Equals(new GridLength(0)),
                    $"{fsVm.ToolbarRowHeight}");
                Check("[图片] 全屏状态变化会通知界面刷新图标 / 提示 / 工具条 / 行高",
                    new[] { "IsFullscreen", "FullscreenGlyph", "FullscreenTooltip", "IsToolbarVisible", "ToolbarRowHeight" }
                        .All(changedProperties.Contains),
                    string.Join(",", changedProperties.Distinct()));

                changedProperties.Clear();
                fsVm.HideControls();
                Check("[图片] 全屏闲置后自动收起工具条（ControlsVisible=false → IsToolbarVisible=false）",
                    !fsVm.ControlsVisible && !fsVm.IsToolbarVisible);
                Check("[图片] 收起工具条会通知 ControlsVisible 与 IsToolbarVisible",
                    changedProperties.Contains("ControlsVisible") && changedProperties.Contains("IsToolbarVisible"),
                    string.Join(",", changedProperties.Distinct()));

                changedProperties.Clear();
                fsVm.HideControls();
                Check("[图片] 已经收起后再闲置：不重复发通知",
                    changedProperties.Count == 0, string.Join(",", changedProperties));

                fsVm.ShowControls();
                Check("[图片] 鼠标动了 ShowControls 又把工具条亮出来（仍在全屏）",
                    fsVm.ControlsVisible && fsVm.IsToolbarVisible && fsVm.IsFullscreen);

                fsVm.IsSettingsVisible = true;
                fsVm.HideControls();
                Check("[图片] 全屏但设置面板开着：HideControls 不收起工具条（避免设置面板跟着闪）",
                    fsVm.ControlsVisible && fsVm.IsToolbarVisible);
                fsVm.IsSettingsVisible = false;
                fsVm.HideControls();
                Check("[图片] 关掉设置面板后再闲置：工具条正常收起",
                    !fsVm.ControlsVisible && !fsVm.IsToolbarVisible);
                fsVm.ShowControls();

                fsVm.HideControls();     // 先藏起来，验证退出全屏时会被强制恢复
                fsVm.ExitFullscreen();
                Check("[图片] 退出全屏：状态 / 图标 / 行高 / 工具条全部还原，并发出事件 false",
                    !fsVm.IsFullscreen && fsVm.FullscreenGlyph == "\uE740"
                    && fsVm.ToolbarRowHeight.Equals(GridLength.Auto)
                    && fsVm.ControlsVisible && fsVm.IsToolbarVisible
                    && fsVm.FullscreenTooltip.Contains("全屏")
                    && fullscreenEvents.Count == 2 && !fullscreenEvents[1],
                    string.Join(",", fullscreenEvents));

                var eventCountBeforeExtraExit = fullscreenEvents.Count;
                fsVm.ExitFullscreen();
                Check("[图片] 已经退出全屏时再 ExitFullscreen 不重复发事件",
                    fullscreenEvents.Count == eventCountBeforeExtraExit);

                fsVm.HideControls();
                Check("[图片] 退出全屏后 HideControls 也不生效（回到普通窗口模式）",
                    fsVm.ControlsVisible && fsVm.IsToolbarVisible);

                fsVm.ToggleFullscreenCommand.Execute(null);
                var enteredByCommand = fsVm.IsFullscreen;
                fsVm.ToggleFullscreenCommand.Execute(null);
                Check("[图片] 工具条全屏按钮（ToggleFullscreenCommand）能进能出",
                    enteredByCommand && !fsVm.IsFullscreen);

                fsVm.ToggleFullscreen();
                var eventCountBeforeClose = fullscreenEvents.Count;
                fsVm.NotifyClosed();
                Check("[图片] 切走工具（NotifyClosed）会退出全屏并还原工具条",
                    !fsVm.IsFullscreen && fsVm.ControlsVisible && fsVm.IsToolbarVisible
                    && fullscreenEvents.Count == eventCountBeforeClose + 1 && !fullscreenEvents[^1],
                    string.Join(",", fullscreenEvents));
            }
            finally
            {
                fsVm.Dispose();
            }

            // 调试环境变量：YLT_PICTURE_FULLSCREEN=1 → 新建 VM 直接全屏（截图核对沉浸式版式用）
            Environment.SetEnvironmentVariable("YLT_PICTURE_FULLSCREEN", "1");
            try
            {
                var envVm = new PictureViewerViewModel();
                try
                {
                    Check("[图片] YLT_PICTURE_FULLSCREEN=1 新建 VM 直接全屏（图标 / 工具条行高都按全屏算）",
                        envVm.IsFullscreen && envVm.FullscreenGlyph == "\uE73F"
                        && envVm.FullscreenTooltip.Contains("退出全屏")
                        && envVm.ControlsVisible && envVm.IsToolbarVisible
                        && envVm.ToolbarRowHeight.Equals(new GridLength(0)));
                    envVm.NotifyClosed();
                    Check("[图片] 环境变量全屏启动的 VM 切走后也退出全屏", !envVm.IsFullscreen);
                }
                finally
                {
                    envVm.Dispose();
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("YLT_PICTURE_FULLSCREEN", null);
            }

            Environment.SetEnvironmentVariable("YLT_PICTURE_FULLSCREEN", "0");
            try
            {
                var offVm = new PictureViewerViewModel();
                Check("[图片] YLT_PICTURE_FULLSCREEN=0 不算全屏", !offVm.IsFullscreen);
                offVm.Dispose();
            }
            finally
            {
                Environment.SetEnvironmentVariable("YLT_PICTURE_FULLSCREEN", null);
            }

            var normalVm = new PictureViewerViewModel();
            Check("[图片] 不设环境变量时新建 VM 默认普通窗口（不全屏）",
                !normalVm.IsFullscreen && normalVm.ControlsVisible
                && normalVm.ToolbarRowHeight.Equals(GridLength.Auto));
            normalVm.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable("YLT_PICTURE_FULLSCREEN", previousPictureFullscreenEnv);
        }

        // ---------------------------------------------------------------- 14. 最近浏览（配置 / 上限 0~100 / VM 记录与去重）
        // ---- 14a. 配置默认值 / 上限夹取 / 是否记录 ----
        var recentCfg = new PictureViewerConfig();
        Check("[图片] 最近浏览：默认上限 50、常量 50 / 100、初始列表非 null 且空",
            recentCfg.RecentLimit == 50
            && recentCfg.RecentLimit == PictureViewerConfig.DefaultRecentLimit
            && PictureViewerConfig.MaxRecentLimit == 100
            && recentCfg.RecentLimitEffective == 50
            && recentCfg.RecordsRecent
            && recentCfg.RecentEntries is not null && recentCfg.RecentEntries.Count == 0);

        Check("[图片] 最近浏览：生效上限夹到 0~100（-5→0、0→0、101→100、int.Max→100、50/100 原样）",
            new PictureViewerConfig { RecentLimit = -5 }.RecentLimitEffective == 0
            && !new PictureViewerConfig { RecentLimit = -5 }.RecordsRecent
            && new PictureViewerConfig { RecentLimit = 0 }.RecentLimitEffective == 0
            && !new PictureViewerConfig { RecentLimit = 0 }.RecordsRecent
            && new PictureViewerConfig { RecentLimit = 101 }.RecentLimitEffective == 100
            && new PictureViewerConfig { RecentLimit = int.MaxValue }.RecentLimitEffective == PictureViewerConfig.MaxRecentLimit
            && new PictureViewerConfig { RecentLimit = 50 }.RecentLimitEffective == 50
            && new PictureViewerConfig { RecentLimit = 100 }.RecentLimitEffective == 100
            && new PictureViewerConfig { RecentLimit = 50 }.RecordsRecent);

        // ---- 14b. NormalizeRecentPath：完整路径 + 去掉尾部斜杠；空 / 非法 → 空串 ----
        Check("[图片] 最近路径：null / 空 / 纯空白 → 空串",
            PictureViewerConfig.NormalizeRecentPath(null) == ""
            && PictureViewerConfig.NormalizeRecentPath("") == ""
            && PictureViewerConfig.NormalizeRecentPath("   ") == "");

        Check("[图片] 最近路径：前后引号 / 空白容错",
            PictureViewerConfig.NormalizeRecentPath("  \"C:\\Temp\\Recent A\"  ") == @"C:\Temp\Recent A");

        var relativeRecent = PictureViewerConfig.NormalizeRecentPath(@"sub\Recent");
        Check("[图片] 最近路径：相对路径补成完整路径",
            Path.IsPathFullyQualified(relativeRecent)
            && relativeRecent == Path.GetFullPath(@"sub\Recent").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            relativeRecent);

        Check("[图片] 最近路径：尾部目录分隔符会被去掉（同一文件夹两种写法可去重）",
            PictureViewerConfig.NormalizeRecentPath(@"C:\Temp\Recent A\") == @"C:\Temp\Recent A"
            && PictureViewerConfig.NormalizeRecentPath(@"C:\Temp\Recent A//") == @"C:\Temp\Recent A");

        Check("[图片] 最近路径：同一路径大小写不同按 Windows 规则视为同一处",
            string.Equals(
                PictureViewerConfig.NormalizeRecentPath(@"C:\Temp\Recent A"),
                PictureViewerConfig.NormalizeRecentPath(@"c:\temp\recent a"),
                StringComparison.OrdinalIgnoreCase));

        var invalidRecentPath = "C:\\bad" + '\0' + "recent";
        string invalidRecentNormalized;
        try { invalidRecentNormalized = PictureViewerConfig.NormalizeRecentPath(invalidRecentPath); }
        catch (Exception ex) { invalidRecentNormalized = "抛异常：" + ex.GetType().Name; }
        Check("[图片] 最近路径：非法路径不抛异常（返回空串）", invalidRecentNormalized == "",
            invalidRecentNormalized);

        // ---- 14c. RememberRecent：置顶 / 去重 / 裁剪 / 上限 0 不记录 ----
        var rememberCfg = new PictureViewerConfig();
        var recentA = @"C:\SelfCheck\Recent\folder-a";
        var recentB = @"C:\SelfCheck\Recent\folder-b";
        var recentFirstUtc = new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        rememberCfg.RememberRecent(recentA, recentFirstUtc);
        Check("[图片] 最近浏览：第一次记录 → 1 条、在最前、路径是完整路径、打开时间原样",
            rememberCfg.RecentEntries.Count == 1
            && rememberCfg.RecentEntries[0].Path == PictureViewerConfig.NormalizeRecentPath(recentA)
            && rememberCfg.RecentEntries[0].LastOpenedUtc == recentFirstUtc,
            string.Join(" | ", rememberCfg.RecentEntries.Select(e => e.Path)));

        var recentSecondUtc = recentFirstUtc.AddMinutes(5);
        rememberCfg.RememberRecent(@"c:\selfcheck\recent\FOLDER-A", recentSecondUtc);
        Check("[图片] 最近浏览：同一路径大小写不同仍只留 1 条、并更新打开时间",
            rememberCfg.RecentEntries.Count == 1
            && rememberCfg.RecentEntries[0].LastOpenedUtc == recentSecondUtc);

        rememberCfg.RememberRecent(recentB, recentSecondUtc.AddMinutes(1));
        Check("[图片] 最近浏览：记第 2 个后新的在最前",
            rememberCfg.RecentEntries.Count == 2
            && rememberCfg.RecentEntries[0].Path == PictureViewerConfig.NormalizeRecentPath(recentB)
            && string.Equals(rememberCfg.RecentEntries[1].Path,
                PictureViewerConfig.NormalizeRecentPath(recentA), StringComparison.OrdinalIgnoreCase));

        var trimmedByRemember = new PictureViewerConfig { RecentLimit = 3 };
        for (var i = 0; i < 5; i++)
            trimmedByRemember.RememberRecent($@"C:\SelfCheck\Recent\limit3-{i}", recentFirstUtc.AddMinutes(i));
        Check("[图片] 最近浏览：上限 3 时连续记 5 个 → 只留最新 3 个（最老 2 个被淘汰）",
            trimmedByRemember.RecentEntries.Count == 3
            && trimmedByRemember.RecentEntries[0].Path == PictureViewerConfig.NormalizeRecentPath(@"C:\SelfCheck\Recent\limit3-4")
            && trimmedByRemember.RecentEntries[2].Path == PictureViewerConfig.NormalizeRecentPath(@"C:\SelfCheck\Recent\limit3-2")
            && trimmedByRemember.RecentEntries.All(e =>
                !e.Path.EndsWith("limit3-0", StringComparison.Ordinal)
                && !e.Path.EndsWith("limit3-1", StringComparison.Ordinal)),
            string.Join(" | ", trimmedByRemember.RecentEntries.Select(e => e.Path)));

        var zeroLimitCfg = new PictureViewerConfig();
        zeroLimitCfg.RememberRecent(recentA, recentFirstUtc);
        zeroLimitCfg.RememberRecent(recentB, recentSecondUtc);
        zeroLimitCfg.RecentLimit = 0;
        zeroLimitCfg.RememberRecent(@"C:\SelfCheck\Recent\folder-c", recentSecondUtc);
        Check("[图片] 最近浏览：上限 0 = 不记录并清空（再记住也不新增）",
            zeroLimitCfg.RecentEntries.Count == 0 && !zeroLimitCfg.RecordsRecent);

        var hugeLimitCfg = new PictureViewerConfig { RecentLimit = 101 };
        for (var i = 0; i < 105; i++)
            hugeLimitCfg.RememberRecent($@"C:\SelfCheck\Recent\huge-{i}", recentFirstUtc.AddMinutes(i));
        Check("[图片] 最近浏览：上限 101 生效值夹到 100，记 105 个只留 100 个",
            hugeLimitCfg.RecentLimitEffective == 100 && hugeLimitCfg.RecentEntries.Count == 100,
            $"{hugeLimitCfg.RecentEntries.Count} 条");

        // ---- 14d. TrimRecentToLimit：手工塞 5 条，按上限保留前 N 条 ----
        var manualTrimCfg = new PictureViewerConfig { RecentLimit = 2 };
        manualTrimCfg.RecentEntries = new List<PictureRecentEntry>
        {
            new() { Path = @"C:\SelfCheck\Recent\manual-0", LastOpenedUtc = recentFirstUtc },
            new() { Path = @"C:\SelfCheck\Recent\manual-1", LastOpenedUtc = recentFirstUtc.AddMinutes(1) },
            new() { Path = @"C:\SelfCheck\Recent\manual-2", LastOpenedUtc = recentFirstUtc.AddMinutes(2) },
            new() { Path = @"C:\SelfCheck\Recent\manual-3", LastOpenedUtc = recentFirstUtc.AddMinutes(3) },
            new() { Path = @"C:\SelfCheck\Recent\manual-4", LastOpenedUtc = recentFirstUtc.AddMinutes(4) },
        };
        manualTrimCfg.TrimRecentToLimit();
        Check("[图片] 最近浏览裁剪：上限 2 时手工 5 条只留前 2 条（保持顺序）",
            manualTrimCfg.RecentEntries.Count == 2
            && manualTrimCfg.RecentEntries[0].Path.EndsWith("manual-0", StringComparison.Ordinal)
            && manualTrimCfg.RecentEntries[1].Path.EndsWith("manual-1", StringComparison.Ordinal));

        manualTrimCfg.RecentLimit = 0;
        manualTrimCfg.TrimRecentToLimit();
        Check("[图片] 最近浏览裁剪：上限 0 时直接清空", manualTrimCfg.RecentEntries.Count == 0);

        // ---- 14e. RemoveRecent / ClearRecent ----
        var removeCfg = new PictureViewerConfig();
        removeCfg.RememberRecent(recentA, recentFirstUtc);
        removeCfg.RememberRecent(recentB, recentSecondUtc);
        removeCfg.RemoveRecent(@"C:\SelfCheck\Recent\FOLDER-A");
        Check("[图片] 最近浏览删除：按路径删（大小写不敏感）只删指定那条",
            removeCfg.RecentEntries.Count == 1
            && removeCfg.RecentEntries[0].Path == PictureViewerConfig.NormalizeRecentPath(recentB));

        string? removeMissingError = null;
        try
        {
            removeCfg.RemoveRecent(@"C:\SelfCheck\Recent\不存在");
            removeCfg.RemoveRecent(null);
            removeCfg.RemoveRecent("   ");
        }
        catch (Exception ex) { removeMissingError = ex.Message; }
        Check("[图片] 最近浏览删除：删不存在的路径 / null 不抛异常、条数不变",
            removeMissingError is null && removeCfg.RecentEntries.Count == 1, removeMissingError ?? "ok");

        removeCfg.ClearRecent();
        string? clearAgainError = null;
        try
        {
            removeCfg.ClearRecent();
            removeCfg.ClearRecent();
        }
        catch (Exception ex) { clearAgainError = ex.Message; }
        Check("[图片] 最近浏览清空：清空后 0 条、重复清空也不抛异常",
            removeCfg.RecentEntries.Count == 0 && clearAgainError is null, clearAgainError ?? "ok");

        // ---- 14f. JSON 往返（自检临时根里的 ToolConfigStore） ----
        var recentStore = ToolConfigStore.For("picture-viewer-recent-check");
        var persistedRecentCfg = recentStore.Load<PictureViewerConfig>();
        persistedRecentCfg.RecentLimit = 7;
        persistedRecentCfg.ClearRecent();
        var jsonRecentUtc = new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc);
        persistedRecentCfg.RememberRecent(@"C:\SelfCheck\Recent\json-folder-a", jsonRecentUtc);
        persistedRecentCfg.RememberRecent(@"C:\SelfCheck\Recent\json-folder-b", jsonRecentUtc.AddMinutes(1));
        persistedRecentCfg.SaveNow();
        var recentJson = File.Exists(recentStore.FilePath) ? File.ReadAllText(recentStore.FilePath) : "";
        Check("[图片] 最近浏览落盘：JSON 里有 recentLimit / recentEntries / lastOpenedUtc，计算属性不写",
            recentJson.Contains("\"recentLimit\": 7", StringComparison.Ordinal)
            && recentJson.Contains("\"recentEntries\"", StringComparison.Ordinal)
            && recentJson.Contains("\"lastOpenedUtc\"", StringComparison.Ordinal)
            && !recentJson.Contains("recentLimitEffective", StringComparison.OrdinalIgnoreCase)
            && !recentJson.Contains("recordsRecent", StringComparison.OrdinalIgnoreCase),
            recentJson.Length <= 300 ? recentJson : recentJson[..300]);

        var rereadRecentCfg = JsonFileStore.Read<PictureViewerConfig>(recentStore.FilePath);
        Check("[图片] 最近浏览往返：读盘后 recentLimit=7、2 条记录、路径完整、时间能读回",
            rereadRecentCfg is not null
            && rereadRecentCfg.RecentLimit == 7
            && rereadRecentCfg.RecentEntries.Count == 2
            && rereadRecentCfg.RecentEntries[0].Path == PictureViewerConfig.NormalizeRecentPath(@"C:\SelfCheck\Recent\json-folder-b")
            && rereadRecentCfg.RecentEntries[0].LastOpenedUtc == jsonRecentUtc.AddMinutes(1)
            && rereadRecentCfg.RecentEntries[1].Path == PictureViewerConfig.NormalizeRecentPath(@"C:\SelfCheck\Recent\json-folder-a"),
            rereadRecentCfg is null ? "读回 null" : $"{rereadRecentCfg.RecentEntries.Count} 条");
        recentStore.StopWatching();

        // ---- 14g. ResetToDefaults：上限回 50，但保留已有最近浏览记录 ----
        var resetRecentCfg = new PictureViewerConfig
        {
            RecentLimit = 7,
            ScrollMode = PictureScrollMode.SinglePage,
            WheelStepPixels = 300,
            ZoomPercent = 250,
            InertiaEnabled = false,
            LastFolder = @"D:\some",
        };
        resetRecentCfg.RememberRecent(recentA, recentFirstUtc);
        resetRecentCfg.RememberRecent(recentB, recentSecondUtc);
        resetRecentCfg.RememberRecent(@"C:\SelfCheck\Recent\folder-c", recentSecondUtc.AddMinutes(1));
        resetRecentCfg.ResetToDefaults();
        Check("[图片] 最近浏览恢复默认：上限回 50、已有 3 条记录保留（只按上限裁剪不清空）",
            resetRecentCfg.RecentLimit == 50 && resetRecentCfg.RecentLimitEffective == 50
            && resetRecentCfg.RecentEntries.Count == 3);
        Check("[图片] 恢复默认设置：其它显示项也一起回默认（阅读方式 / 缩放 / 惯性 / 上次文件夹）",
            resetRecentCfg.ScrollModeEffective == PictureScrollMode.Vertical
            && resetRecentCfg.ZoomPercentEffective == 100
            && resetRecentCfg.InertiaEnabled
            && resetRecentCfg.LastFolder == "");

        // ---- 14h. VM：打开文件夹后记录 / 去重 / 裁剪 / 打开最近 / 清空 ----
        var pictureRecentA = SelfCheckScratch.MakeDirectory("picture-recent-a");
        var pictureRecentB = SelfCheckScratch.MakeDirectory("picture-recent-b");
        var pictureRecentGone = SelfCheckScratch.MakeDirectory("picture-recent-gone");
        try
        {
            foreach (var dir in new[] { pictureRecentA, pictureRecentB, pictureRecentGone })
            {
                WritePng(Path.Combine(dir, "1.png"), 2, 3);
                WritePng(Path.Combine(dir, "2.png"), 4, 5);
            }

            var recentVm = new PictureViewerViewModel();
            try
            {
                recentVm.Cfg.RecentLimit = 50;
                recentVm.Cfg.ClearRecent();
                recentVm.RefreshRecentItems();
                Check("[图片] 最近浏览 VM：初始没有记录、空状态提示「打开文件夹后自动记」、上限文字 50 条",
                    !recentVm.HasRecentItems && recentVm.RecentItems.Count == 0
                    && recentVm.RecentEmptyText.Contains("还没有最近浏览")
                    && recentVm.RecentLimitText == "50 条"
                    && !recentVm.IsRecentVisible);

                recentVm.ToggleRecent();
                Check("[图片] 最近浏览 VM：ToggleRecent 打开面板、并关掉设置面板",
                    recentVm.IsRecentVisible && !recentVm.IsSettingsVisible);

                recentVm.LoadFolderAsync(pictureRecentA).GetAwaiter().GetResult();
                Check("[图片] 最近浏览 VM：打开文件夹后记 1 条（名字 / 完整路径）并自动收起面板",
                    recentVm.RecentItems.Count == 1
                    && recentVm.RecentItems[0].Name == Path.GetFileName(pictureRecentA)
                    && recentVm.RecentItems[0].Path == PictureViewerConfig.NormalizeRecentPath(pictureRecentA)
                    && recentVm.Cfg.RecentEntries[0].Path == PictureViewerConfig.NormalizeRecentPath(pictureRecentA)
                    && recentVm.HasRecentItems
                    && !recentVm.IsRecentVisible,
                    string.Join(" | ", recentVm.RecentItems.Select(item => item.Path)));

                recentVm.LoadFolderAsync(pictureRecentB).GetAwaiter().GetResult();
                Check("[图片] 最近浏览 VM：再打开另一个文件夹 → 新的在最前、共 2 条",
                    recentVm.RecentItems.Count == 2
                    && recentVm.RecentItems[0].Path == PictureViewerConfig.NormalizeRecentPath(pictureRecentB)
                    && recentVm.RecentItems[1].Path == PictureViewerConfig.NormalizeRecentPath(pictureRecentA));

                recentVm.RecentLimit = 1;
                Check("[图片] 最近浏览 VM：上限调成 1 立刻裁剪到只剩最新的 B、文字变 1 条",
                    recentVm.RecentLimit == 1 && recentVm.RecentItems.Count == 1
                    && recentVm.RecentItems[0].Path == PictureViewerConfig.NormalizeRecentPath(pictureRecentB)
                    && recentVm.RecentLimitText == "1 条",
                    recentVm.RecentLimitText);

                recentVm.RecentLimit = 9999;
                Check("[图片] 最近浏览 VM：上限超 100 夹到 100、文字 100 条",
                    recentVm.RecentLimit == 100 && recentVm.Cfg.RecentLimit == 100
                    && recentVm.RecentLimitText == "100 条");

                recentVm.RecentLimit = -5;
                Check("[图片] 最近浏览 VM：上限负数夹到 0、清空列表、文字变「不记录」",
                    recentVm.RecentLimit == 0 && recentVm.RecentItems.Count == 0
                    && recentVm.Cfg.RecentEntries.Count == 0
                    && recentVm.RecentLimitText == "不记录"
                    && recentVm.RecentEmptyText.Contains("上限是 0"),
                    recentVm.RecentLimitText);

                recentVm.LoadFolderAsync(pictureRecentA).GetAwaiter().GetResult();
                Check("[图片] 最近浏览 VM：上限 0 时打开文件夹不记录", recentVm.RecentItems.Count == 0);

                recentVm.RecentLimit = 50;
                recentVm.LoadFolderAsync(pictureRecentA).GetAwaiter().GetResult();
                recentVm.LoadFolderAsync(pictureRecentB).GetAwaiter().GetResult();
                recentVm.ClearRecent();
                Check("[图片] 最近浏览 VM：ClearRecent 清空并给状态栏提示",
                    !recentVm.HasRecentItems && recentVm.RecentItems.Count == 0
                    && recentVm.Cfg.RecentEntries.Count == 0
                    && recentVm.StatusText.Contains("已清空最近浏览"),
                    recentVm.StatusText);

                // 点最近记录：文件夹还在 → 重新加载并置顶；文件夹已删 → 移除记录并提示
                recentVm.LoadFolderAsync(pictureRecentA).GetAwaiter().GetResult();
                recentVm.LoadFolderAsync(pictureRecentB).GetAwaiter().GetResult();
                recentVm.OpenRecentAsync(pictureRecentA).GetAwaiter().GetResult();
                Check("[图片] 最近浏览 VM：点最近记录重新打开 A → 当前文件夹 A、A 置顶",
                    recentVm.FolderPath == PictureViewerConfig.NormalizeRecentPath(pictureRecentA)
                    && recentVm.RecentItems.Count == 2
                    && recentVm.RecentItems[0].Path == PictureViewerConfig.NormalizeRecentPath(pictureRecentA),
                    recentVm.FolderPath);

                recentVm.LoadFolderAsync(pictureRecentGone).GetAwaiter().GetResult();
                Directory.Delete(pictureRecentGone, recursive: true);
                recentVm.OpenRecentAsync(pictureRecentGone).GetAwaiter().GetResult();
                Check("[图片] 最近浏览 VM：点已不存在的文件夹 → 从列表移除并提示",
                    recentVm.RecentItems.All(item => item.Path != PictureViewerConfig.NormalizeRecentPath(pictureRecentGone))
                    && recentVm.StatusText.Contains("已经不在了"),
                    recentVm.StatusText);

                recentVm.LoadFolderAsync(pictureRecentA).GetAwaiter().GetResult();
                recentVm.LoadFolderAsync(pictureRecentB).GetAwaiter().GetResult();
                recentVm.ToggleRecentCommand.Execute(null);
                var recentOpenedByCommand = recentVm.IsRecentVisible;
                recentVm.ClearRecentCommand.Execute(null);
                Check("[图片] 最近浏览 VM：工具条命令能打开最近面板 / 清空记录",
                    recentOpenedByCommand && recentVm.RecentItems.Count == 0
                    && recentVm.Cfg.RecentEntries.Count == 0,
                    recentVm.StatusText);

                recentVm.NotifyClosed();
                Check("[图片] 最近浏览 VM：切走工具后最近面板收起", !recentVm.IsRecentVisible);
            }
            finally
            {
                recentVm.Dispose();
            }
        }
        finally
        {
            foreach (var dir in new[] { pictureRecentA, pictureRecentB, pictureRecentGone })
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* 临时目录，删不掉无所谓 */ }
            }
        }

        // ---------------------------------------------------------------- 15. 滚轮惯性（纯逻辑 + 独立配置 + JSON）
        // 视图层：滚一格后按「实际走过的偏移」符号给主轴一个初速度，再按摩擦减速；
        // 这里只断言纯数学与配置，不碰 WPF（双页翻页 / 缩放不产生滚轮惯性在视图里）。
        var wheelCfg = new PictureViewerConfig();
        Check("[图片] 滚轮惯性：配置默认开，和拖动惯性是两个独立设置（默认都开）",
            wheelCfg.WheelInertiaEnabled && wheelCfg.InertiaEnabled);

        var wheelIndependentCfg = new PictureViewerConfig { InertiaEnabled = false };
        Check("[图片] 滚轮惯性：关掉拖动惯性不影响滚轮惯性",
            wheelIndependentCfg.WheelInertiaEnabled && !wheelIndependentCfg.InertiaEnabled);
        wheelIndependentCfg.WheelInertiaEnabled = false;
        wheelIndependentCfg.InertiaEnabled = true;
        Check("[图片] 滚轮惯性：关掉滚轮惯性也不影响拖动惯性",
            !wheelIndependentCfg.WheelInertiaEnabled && wheelIndependentCfg.InertiaEnabled);

        var wheelResetCfg = new PictureViewerConfig { InertiaEnabled = false, WheelInertiaEnabled = false };
        wheelResetCfg.ResetToDefaults();
        Check("[图片] 滚轮惯性：恢复默认设置把两个惯性开关都恢复成开",
            wheelResetCfg.WheelInertiaEnabled && wheelResetCfg.InertiaEnabled);

        Check("[图片] 滚轮惯性常量：每像素 0.014、封顶 4.0、最低速度 0.02",
            Math.Abs(PictureInertia.DefaultWheelVelocityPerPixel - 0.014) <= 1e-12
            && Math.Abs(PictureInertia.DefaultWheelMaxVelocity - 4.0) <= 1e-12
            && Math.Abs(PictureInertia.DefaultMinimumSpeed - 0.02) <= 1e-12);

        Check("[图片] 滚轮初速度：默认步长 120px → 120 × 0.014 ≈ 1.68 px/ms",
            Math.Abs(PictureInertia.WheelVelocity(120) - 1.68) <= 1e-12,
            PictureInertia.WheelVelocity(120).ToString("F6"));

        Check("[图片] 滚轮初速度：最小步长 20px → 0.28 px/ms",
            Math.Abs(PictureInertia.WheelVelocity(20) - 0.28) <= 1e-12);

        Check("[图片] 滚轮初速度：步长越大初速度越大（20 < 120 < 200）且都没到封顶",
            PictureInertia.WheelVelocity(20) < PictureInertia.WheelVelocity(120)
            && PictureInertia.WheelVelocity(120) < PictureInertia.WheelVelocity(200)
            && PictureInertia.WheelVelocity(200) < PictureInertia.DefaultWheelMaxVelocity);

        Check("[图片] 滚轮初速度：0 / NaN / ±无穷都退回最低速度（坏步长不产生速度）",
            Math.Abs(PictureInertia.WheelVelocity(0) - PictureInertia.DefaultMinimumSpeed) <= 1e-12
            && Math.Abs(PictureInertia.WheelVelocity(double.NaN) - PictureInertia.DefaultMinimumSpeed) <= 1e-12
            && Math.Abs(PictureInertia.WheelVelocity(double.PositiveInfinity) - PictureInertia.DefaultMinimumSpeed) <= 1e-12
            && Math.Abs(PictureInertia.WheelVelocity(double.NegativeInfinity) - PictureInertia.DefaultMinimumSpeed) <= 1e-12);

        Check("[图片] 滚轮初速度：负数按绝对值算（-120 与 120 相同，方向由实际偏移符号决定）",
            Math.Abs(PictureInertia.WheelVelocity(-120) - PictureInertia.WheelVelocity(120)) <= 1e-12);

        Check("[图片] 滚轮初速度：极小步长（1px）也夹到最低速度",
            Math.Abs(PictureInertia.WheelVelocity(1) - PictureInertia.DefaultMinimumSpeed) <= 1e-12);

        Check("[图片] 滚轮初速度：极大步长封顶 4.0 px/ms（99999 / 600 都不会一滚到底）",
            Math.Abs(PictureInertia.WheelVelocity(99999) - PictureInertia.DefaultWheelMaxVelocity) <= 1e-12
            && Math.Abs(PictureInertia.WheelVelocity(600) - PictureInertia.DefaultWheelMaxVelocity) <= 1e-12);

        var wheelStartVelocity = PictureInertia.WheelVelocity(120);
        var wheelInertia = new PictureInertia();
        var (wheelDecayedMain, _) = wheelInertia.Decay(wheelStartVelocity, 0, 16);
        Check("[图片] 滚轮初速度手感：一格速度在 (0.02, 4.0) 内、16ms 后仍在滑但已减速",
            wheelStartVelocity > PictureInertia.DefaultMinimumSpeed
            && wheelStartVelocity < PictureInertia.DefaultWheelMaxVelocity
            && wheelDecayedMain > 0 && wheelDecayedMain < wheelStartVelocity,
            $"v={wheelStartVelocity:F4}，16ms 后={wheelDecayedMain:F4}");

        Check("[图片] 滚轮初速度足够触发惯性滑行（IsFastEnough = true）",
            wheelInertia.IsFastEnough(wheelStartVelocity, 0)
            && PictureInertia.IsFastEnough(wheelStartVelocity, 0, PictureInertia.DefaultMinimumSpeed));

        var wheelJsonDefault = new PictureViewerConfig().ToJson();
        Check("[图片] 滚轮惯性落盘字段：JSON 里 wheelInertiaEnabled 与 inertiaEnabled 各存一份（默认 true）",
            wheelJsonDefault.Contains("\"wheelInertiaEnabled\": true", StringComparison.Ordinal)
            && wheelJsonDefault.Contains("\"inertiaEnabled\": true", StringComparison.Ordinal));

        var wheelStore = ToolConfigStore.For("picture-viewer-wheel-check");
        var wheelPersistedCfg = wheelStore.Load<PictureViewerConfig>();
        wheelPersistedCfg.InertiaEnabled = true;
        wheelPersistedCfg.WheelInertiaEnabled = false;
        wheelPersistedCfg.SaveNow();
        var wheelJson = File.Exists(wheelStore.FilePath) ? File.ReadAllText(wheelStore.FilePath) : "";
        Check("[图片] 滚轮惯性落盘：JSON 里 wheelInertiaEnabled=false、inertiaEnabled=true（互不影响）",
            wheelJson.Contains("\"wheelInertiaEnabled\": false", StringComparison.Ordinal)
            && wheelJson.Contains("\"inertiaEnabled\": true", StringComparison.Ordinal),
            wheelJson.Length <= 300 ? wheelJson : wheelJson[..300]);

        var wheelRereadCfg = JsonFileStore.Read<PictureViewerConfig>(wheelStore.FilePath);
        Check("[图片] 滚轮惯性往返：读盘后两个开关各自保持正确值",
            wheelRereadCfg is not null
            && !wheelRereadCfg.WheelInertiaEnabled
            && wheelRereadCfg.InertiaEnabled);
        wheelStore.StopWatching();

        // ---------------------------------------------------------------- 收尾：清掉临时测试文件夹
        try { Directory.Delete(folder, recursive: true); } catch { /* 临时目录，删不掉无所谓 */ }
        try { Directory.Delete(emptyFolder, recursive: true); } catch { /* 同上 */ }
    }

    /// <summary>双页排版：两页各占一半宽、接缝在中间；缩放 2 倍后内容翻倍。</summary>
    private static bool SpreadLayoutCheck()
    {
        var layout = PicturePaging.ComputeLayout(PictureScrollMode.SpreadLeftToRight, 1000, 800, 1.0, 10, 0);
        if (layout.ContentWidth != 1000 || layout.ContentHeight != 800 || layout.Slots.Count != 2) return false;

        var left = layout.Slots[0];
        var right = layout.Slots[1];
        if (left.X != 0 || right.X != 500 || left.Width != 500 || right.Width != 500) return false;
        if (left.PageIndex != 0 || right.PageIndex != 1) return false;
        if (layout.LeftPageIndex != 0 || layout.RightPageIndex != 1) return false;

        // 日式镜像：右槽是当前页
        var mirrored = PicturePaging.ComputeLayout(PictureScrollMode.SpreadRightToLeft, 1000, 800, 1.0, 10, 0);
        if (mirrored.Slots[0].PageIndex != 1 || mirrored.Slots[1].PageIndex != 0) return false;

        // 200%：内容和高都翻倍、第二页从 1000 开始
        var zoomed = PicturePaging.ComputeLayout(PictureScrollMode.SpreadLeftToRight, 1000, 800, 2.0, 10, 0);
        return zoomed.ContentWidth == 2000 && zoomed.ContentHeight == 1600
               && zoomed.Slots[0].Width == 1000 && zoomed.Slots[1].X == 1000;
    }

    /// <summary>单页排版：内容 = 视口 × 缩放，一页占满、右槽空。</summary>
    private static bool SingleLayoutCheck()
    {
        var layout = PicturePaging.ComputeLayout(PictureScrollMode.SinglePage, 1000, 800, 1.0, 10, 3);
        if (layout.ContentWidth != 1000 || layout.ContentHeight != 800 || layout.Slots.Count != 1) return false;

        var slot = layout.Slots[0];
        return slot.PageIndex == 3 && slot.X == 0 && slot.Y == 0
               && slot.Width == 1000 && slot.Height == 800
               && layout.LeftPageIndex == 3 && layout.RightPageIndex is null;
    }

    /// <summary>造一张真 PNG（宽高自定义），用来验证「扫文件夹会读出真实像素尺寸」。</summary>
    private static void WritePng(string path, int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x30;      // B
            pixels[i + 1] = 0x60;  // G
            pixels[i + 2] = 0xC0;  // R
            pixels[i + 3] = 0xFF;  // A
        }

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        for (var waited = 0; waited < timeoutMilliseconds; waited += 50)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
