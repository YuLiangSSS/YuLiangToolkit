using System.IO;
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
/// 三种滚动方式的真实版式用截图 + <c>YLT_PICTURE_MEASURE</c> 核对（见 AGENTS.md 2.19）。</para>
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

        Check("[图片] 翻页模式滚轮动作：默认翻页、坏值归一到翻页",
            PictureWheelActions.Normalize(PictureWheelAction.Zoom) == PictureWheelAction.Zoom
            && PictureWheelActions.Normalize((PictureWheelAction)99) == PictureWheelAction.TurnPage
            && PictureWheelActions.Describe(PictureWheelAction.TurnPage).Contains("翻页")
            && PictureWheelActions.Describe(PictureWheelAction.Zoom) == "缩放");

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
