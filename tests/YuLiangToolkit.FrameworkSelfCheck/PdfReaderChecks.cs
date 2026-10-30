using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Configuration;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Core.Tools;
using YuLiangToolkit.Tools.PdfReader;
using YuLiangToolkit.Tools.PdfReader.Rendering;
using YuLiangToolkit.Tools.PdfReader.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「PDF 阅读器」（pdf-reader）的自检：三种阅读方式的分类与文案、连续滚动长带的排版数学
/// （纵向 / 横向 / 空页 / 坏页尺寸 / 视口没量出来）、双页翻页的配对与翻页步进、拖动惯性的采样 / 衰减 / 合成、
/// 配置默认值与落盘（枚举按名字存字符串）、工具展示契约，以及<b>真的走一遍 Windows.Data.Pdf 渲染链路</b>：
/// 用 <see cref="PdfDemoPdf"/> 生成一份 3 页的最小合法 PDF，打开后把第 1 页渲染成位图，
/// 断言位图是冻结的、宽度等于请求值、不同页画出来的底色不同。
///
/// <para>界面里的鼠标拖拽 / 滚动条同步 / 虚拟化要碰 WPF 视觉树，无法无头跑；
/// 那些「按了之后该滚多少 / 翻到哪一屏 / 松手还能滑多远」的数学都抽在
/// <see cref="PdfContinuousLayout"/> / <see cref="PdfSpreadLayout"/> / <see cref="PdfInertia"/> 里，这里逐条钉死。
/// 版式最终效果仍靠 <c>YLT_PDF_DEMO=1</c> 截图冒烟核对（见 docs/dev/pitfalls.md）。</para>
///
/// <para>注意：这里只在自检临时数据根（<c>YLT_HOME</c>）里写配置文件 / PDF，不碰仓库的 Data/Config。</para>
/// </summary>
internal static class PdfReaderChecks
{
    /// <summary>浮点断言容差（排版单位是 DIP；752 ÷ (400/600) 这类运算有二进制误差，不能直接比相等）。</summary>
    private const double Tolerance = 0.01;

    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check("[PDF] " + name, ok, detail);

        // 数 / 排版尺寸专用的断言：失败时把「实际 / 期望」都写进 detail，一眼看出是源码算错还是断言写错。
        void Near(string name, double actual, double expected, string label = "")
            => Check(name, Math.Abs(actual - expected) <= Tolerance,
                $"{(label.Length > 0 ? label + "：" : "")}实际 {actual:F4}，期望 {expected:F4}");

        // ================================================================
        // 1. 三种阅读方式（PdfReadModes）—— 分类 / 归一化 / 文案
        // ================================================================
        Check("枚举值稳定：Vertical=0 / Horizontal=1 / Spread=2、共 3 种",
            (int)PdfReadMode.Vertical == 0 && (int)PdfReadMode.Horizontal == 1
            && (int)PdfReadMode.Spread == 2 && Enum.GetValues<PdfReadMode>().Length == 3,
            string.Join(" / ", Enum.GetValues<PdfReadMode>().Select(m => $"{m}={(int)m}")));

        Check("纵向：算连续滚动、不是横向、不是双页",
            PdfReadModes.IsContinuous(PdfReadMode.Vertical)
            && !PdfReadModes.IsHorizontal(PdfReadMode.Vertical)
            && !PdfReadModes.IsSpread(PdfReadMode.Vertical));

        Check("横向：算连续滚动、是横向、不是双页",
            PdfReadModes.IsContinuous(PdfReadMode.Horizontal)
            && PdfReadModes.IsHorizontal(PdfReadMode.Horizontal)
            && !PdfReadModes.IsSpread(PdfReadMode.Horizontal));

        Check("双页：不算连续滚动、不是横向、是双页",
            !PdfReadModes.IsContinuous(PdfReadMode.Spread)
            && !PdfReadModes.IsHorizontal(PdfReadMode.Spread)
            && PdfReadModes.IsSpread(PdfReadMode.Spread));

        Check("合法值归一化后不变",
            PdfReadModes.Normalize(PdfReadMode.Vertical) == PdfReadMode.Vertical
            && PdfReadModes.Normalize(PdfReadMode.Horizontal) == PdfReadMode.Horizontal
            && PdfReadModes.Normalize(PdfReadMode.Spread) == PdfReadMode.Spread);

        Check("手改坏的枚举值归一化成纵向（99 / -1 / int.MaxValue）",
            PdfReadModes.Normalize((PdfReadMode)99) == PdfReadMode.Vertical
            && PdfReadModes.Normalize((PdfReadMode)(-1)) == PdfReadMode.Vertical
            && PdfReadModes.Normalize((PdfReadMode)int.MaxValue) == PdfReadMode.Vertical);

        Check("归一化幂等（合法值两次归一化还是自己，坏值稳定在纵向）",
            Enum.GetValues<PdfReadMode>().All(m => PdfReadModes.Normalize(PdfReadModes.Normalize(m)) == m)
            && PdfReadModes.Normalize(PdfReadModes.Normalize((PdfReadMode)42)) == PdfReadMode.Vertical);

        Check("坏值也按纵向参与分类（连续、非横向、非双页）",
            PdfReadModes.IsContinuous((PdfReadMode)99)
            && !PdfReadModes.IsHorizontal((PdfReadMode)99)
            && !PdfReadModes.IsSpread((PdfReadMode)99));

        var modeDescribes = Enum.GetValues<PdfReadMode>().Select(PdfReadModes.Describe).ToList();
        Check("三种方式都有名字且互不相同",
            modeDescribes.Count == 3 && modeDescribes.All(d => d.Length > 0)
            && modeDescribes.Distinct().Count() == 3,
            string.Join(" / ", modeDescribes));

        Check("名字符合产品约定（纵向 上 → 下 / 横向 左 → 右 / 双页 翻页）",
            PdfReadModes.Describe(PdfReadMode.Vertical) == "纵向 上 → 下"
            && PdfReadModes.Describe(PdfReadMode.Horizontal) == "横向 左 → 右"
            && PdfReadModes.Describe(PdfReadMode.Spread) == "双页 翻页",
            string.Join(" / ", modeDescribes));

        var modeHints = Enum.GetValues<PdfReadMode>().Select(PdfReadModes.Hint).ToList();
        Check("每种方式都有一句说明（非空、互不相同、都提到滚轮操作）",
            modeHints.Count == 3 && modeHints.All(h => h.Length > 8)
            && modeHints.Distinct().Count() == 3
            && modeHints.All(h => h.Contains("滚轮")),
            string.Join(" | ", modeHints));

        Check("Describe / Hint 对坏值退回纵向文案",
            PdfReadModes.Describe((PdfReadMode)99) == PdfReadModes.Describe(PdfReadMode.Vertical)
            && PdfReadModes.Hint((PdfReadMode)99) == PdfReadModes.Hint(PdfReadMode.Vertical));

        // ---------------------------------------------------------------- 1b. 一页的原始尺寸（PdfPageInfo）
        var portrait = new PdfPageInfo(0, 400, 600);
        Check("页信息：合法竖版页（400×600）尺寸有效、宽高比 2/3",
            portrait.HasValidSize && Math.Abs(portrait.AspectRatio - 2.0 / 3.0) <= Tolerance,
            $"HasValidSize={portrait.HasValidSize}，AspectRatio={portrait.AspectRatio:F4}");

        var landscape = new PdfPageInfo(1, 600, 400);
        Check("页信息：横版页（600×400）宽高比 1.5",
            landscape.HasValidSize && Math.Abs(landscape.AspectRatio - 1.5) <= Tolerance,
            $"AspectRatio={landscape.AspectRatio:F4}");

        var brokenPage = new PdfPageInfo(2, double.NaN, 600);
        Check("页信息：坏尺寸（NaN / 0 / 负数 / 无穷）判为无效、宽高比退回 A4 竖版 2/3",
            !brokenPage.HasValidSize
            && Math.Abs(brokenPage.AspectRatio - 2.0 / 3.0) <= Tolerance
            && !new PdfPageInfo(0, 400, 0).HasValidSize
            && !new PdfPageInfo(0, -1, 600).HasValidSize
            && !new PdfPageInfo(0, double.PositiveInfinity, 600).HasValidSize
            && !new PdfPageInfo(0, double.NegativeInfinity, 600).HasValidSize);

        Check("页信息：Index 原样保留（排版结果要能对回页号）",
            new PdfPageInfo(7, 400, 600).Index == 7);

        // ================================================================
        // 2. 连续滚动排版：纵向（页宽铺满视口宽，页高按宽高比算）
        // ================================================================
        // 3 页 400×600、视口 800×600、margin=24、gap=16：
        // 页宽 = 800 − 24×2 = 752；页高 = 752 ÷ (400/600) = 1128；
        // 内容高 = 3×1128 + 2×16 + 2×24 = 3464；最大偏移 = 3464 − 600 = 2864。
        var threePortraitPages = new[]
        {
            new PdfPageInfo(0, 400, 600),
            new PdfPageInfo(1, 400, 600),
            new PdfPageInfo(2, 400, 600),
        };
        var vertical = PdfContinuousLayout.Compute(PdfReadMode.Vertical, threePortraitPages, 800, 600);
        var verticalBoxes = vertical.Boxes;

        Check("纵向排版：3 页都排出来、非空",
            vertical.PageCount == 3 && !vertical.IsEmpty && verticalBoxes.Count == 3);

        Check("纵向排版：页宽 = 视口宽 − 两侧留白 = 752（三页一致）",
            verticalBoxes.Count == 3 && verticalBoxes.All(b => Math.Abs(b.Width - 752) <= Tolerance),
            string.Join(" / ", verticalBoxes.Select(b => b.Width.ToString("F2"))));

        Check("纵向排版：页高按宽高比算 = 1128（三页一致）",
            verticalBoxes.Count == 3 && verticalBoxes.All(b => Math.Abs(b.Height - 1128) <= Tolerance),
            string.Join(" / ", verticalBoxes.Select(b => b.Height.ToString("F2"))));

        Check("纵向排版：每页左边距 24",
            verticalBoxes.Count == 3 && verticalBoxes.All(b => Math.Abs(b.X - 24) <= Tolerance),
            string.Join(" / ", verticalBoxes.Select(b => b.X.ToString("F2"))));

        Check("纵向排版：页 Y 依次 24 / 1168 / 2312（页高 1128 + gap 16）",
            verticalBoxes.Count == 3
            && Math.Abs(verticalBoxes[0].Y - 24) <= Tolerance
            && Math.Abs(verticalBoxes[1].Y - 1168) <= Tolerance
            && Math.Abs(verticalBoxes[2].Y - 2312) <= Tolerance,
            string.Join(" / ", verticalBoxes.Select(b => b.Y.ToString("F2"))));

        Near("纵向排版：内容高 = 3×1128 + 2×16 + 2×24 = 3464", vertical.ContentHeight, 3464, "ContentHeight");
        Near("纵向排版：内容宽不小于视口宽（800）", vertical.ContentWidth, 800, "ContentWidth");
        Near("纵向排版：阅读轴长 = 内容高 = 3464",
            PdfContinuousLayout.ReadingLength(vertical, PdfReadMode.Vertical), 3464, "ReadingLength");
        Near("纵向排版：视口在阅读轴上的长度 = 视口高 = 600",
            PdfContinuousLayout.ReadingViewport(800, 600, PdfReadMode.Vertical), 600, "ReadingViewport");
        Near("纵向排版：最大阅读偏移 = 3464 − 600 = 2864",
            PdfContinuousLayout.MaximumOffset(vertical, PdfReadMode.Vertical, 800, 600), 2864, "MaximumOffset");

        Check("纵向排版：每页在阅读轴上的起点 = 24 / 1168 / 2312",
            verticalBoxes.Count == 3
            && Math.Abs(verticalBoxes[0].MainStart(false) - 24) <= Tolerance
            && Math.Abs(verticalBoxes[1].MainStart(false) - 1168) <= Tolerance
            && Math.Abs(verticalBoxes[2].MainStart(false) - 2312) <= Tolerance,
            string.Join(" / ", verticalBoxes.Select(b => b.MainStart(false).ToString("F0"))));

        Check("纵向排版：每页在阅读轴上的终点 = 1152 / 2296 / 3440",
            verticalBoxes.Count == 3
            && Math.Abs(verticalBoxes[0].MainEnd(false) - 1152) <= Tolerance
            && Math.Abs(verticalBoxes[1].MainEnd(false) - 2296) <= Tolerance
            && Math.Abs(verticalBoxes[2].MainEnd(false) - 3440) <= Tolerance,
            string.Join(" / ", verticalBoxes.Select(b => b.MainEnd(false).ToString("F0"))));

        Check("纵向排版：OffsetOfPage 各页开头 = 24 / 1168 / 2312",
            verticalBoxes.Count == 3
            && Math.Abs(PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 0) - 24) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 1) - 1168) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 2) - 2312) <= Tolerance,
            $"{PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 0):F0} / " +
            $"{PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 1):F0} / " +
            $"{PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 2):F0}");

        Check("纵向排版：OffsetOfPage 越界页号夹到两端（-5 → 第 1 页、99 → 最后一页）",
            Math.Abs(PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, -5) - 24) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetOfPage(vertical, PdfReadMode.Vertical, 99) - 2312) <= Tolerance);

        Check("纵向排版：PageIndexAtOffset 定位最前面那一页（0 / 1000 → 第 1 页）",
            PdfContinuousLayout.PageIndexAtOffset(vertical, PdfReadMode.Vertical, 0) == 0
            && PdfContinuousLayout.PageIndexAtOffset(vertical, PdfReadMode.Vertical, 1000) == 0);

        Check("纵向排版：PageIndexAtOffset 页结束时切到下一页（1152 → 第 2 页、2312 → 第 3 页）",
            PdfContinuousLayout.PageIndexAtOffset(vertical, PdfReadMode.Vertical, 1152) == 1
            && PdfContinuousLayout.PageIndexAtOffset(vertical, PdfReadMode.Vertical, 2312) == 2);

        Check("纵向排版：偏移超过最后一页末尾 → 最后一页",
            PdfContinuousLayout.PageIndexAtOffset(vertical, PdfReadMode.Vertical, 5000) == 2
            && PdfContinuousLayout.PageIndexAtOffset(vertical, PdfReadMode.Vertical, 99999) == 2);

        Check("纵向排版：视口中心所在页（状态栏「第 x / n 页」用）",
            PdfContinuousLayout.PageIndexAtViewportCenter(vertical, PdfReadMode.Vertical, 0, 600) == 0
            && PdfContinuousLayout.PageIndexAtViewportCenter(vertical, PdfReadMode.Vertical, 1000, 600) == 1
            && PdfContinuousLayout.PageIndexAtViewportCenter(vertical, PdfReadMode.Vertical, 2864, 600) == 2);

        Check("纵向排版：可见页范围（第一屏只有第 1 页、中段 1~2 页、贴底 3~3 页）",
            PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 0, 600) == (0, 0)
            && PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 1000, 600) == (0, 1)
            && PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 2864, 600) == (2, 2),
            string.Join(" / ",
                PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 0, 600),
                PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 1000, 600),
                PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 2864, 600)));

        Check("纵向排版：偏移跑到内容之后也至少把最后一页算可见",
            PdfContinuousLayout.VisibleRange(vertical, PdfReadMode.Vertical, 99999, 600) == (2, 2));

        Check("纵向排版：按比例取偏移（0 → 0、0.5 → 1432、1 → 2864）",
            Math.Abs(PdfContinuousLayout.OffsetForRatio(0, 2864)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetForRatio(0.5, 2864) - 1432) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetForRatio(1, 2864) - 2864) <= Tolerance);

        Check("纵向排版：比例夹到 0~1（负数 → 0、超过 1 → 最大值、NaN → 0）",
            Math.Abs(PdfContinuousLayout.OffsetForRatio(-1, 2864)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetForRatio(2, 2864) - 2864) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetForRatio(double.NaN, 2864)) <= Tolerance);

        Check("纵向排版：当前偏移占总可滚范围的比例（0 / 0.5 / 1）",
            Math.Abs(PdfContinuousLayout.RatioOf(0, 2864)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.RatioOf(1432, 2864) - 0.5) <= Tolerance
            && Math.Abs(PdfContinuousLayout.RatioOf(2864, 2864) - 1) <= Tolerance);

        Check("纵向排版：偏移夹取 [0, 最大值]（负数 / 超界 / NaN / 没得滚）",
            Math.Abs(PdfContinuousLayout.ClampOffset(-5, 2864)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.ClampOffset(9999, 2864) - 2864) <= Tolerance
            && Math.Abs(PdfContinuousLayout.ClampOffset(double.NaN, 2864)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.ClampOffset(50, 0)) <= Tolerance);

        // ================================================================
        // 3. 连续滚动排版：横向（页高铺满视口高，页宽按宽高比算）
        // ================================================================
        // 同样 3 页 400×600、视口 800×600：页高 = 600 − 48 = 552；页宽 = 552 × (400/600) = 368；
        // 内容宽 = 3×368 + 2×16 + 2×24 = 1184；最大偏移 = 1184 − 800 = 384。
        var horizontal = PdfContinuousLayout.Compute(PdfReadMode.Horizontal, threePortraitPages, 800, 600);
        var horizontalBoxes = horizontal.Boxes;

        Check("横向排版：3 页都排出来、非空",
            horizontal.PageCount == 3 && !horizontal.IsEmpty && horizontalBoxes.Count == 3);

        Check("横向排版：页高 = 视口高 − 上下留白 = 552（三页一致）",
            horizontalBoxes.Count == 3 && horizontalBoxes.All(b => Math.Abs(b.Height - 552) <= Tolerance),
            string.Join(" / ", horizontalBoxes.Select(b => b.Height.ToString("F2"))));

        Check("横向排版：页宽按宽高比算 = 368（三页一致）",
            horizontalBoxes.Count == 3 && horizontalBoxes.All(b => Math.Abs(b.Width - 368) <= Tolerance),
            string.Join(" / ", horizontalBoxes.Select(b => b.Width.ToString("F2"))));

        Check("横向排版：每页上边距 24、页 X 依次 24 / 408 / 792",
            horizontalBoxes.Count == 3
            && horizontalBoxes.All(b => Math.Abs(b.Y - 24) <= Tolerance)
            && Math.Abs(horizontalBoxes[0].X - 24) <= Tolerance
            && Math.Abs(horizontalBoxes[1].X - 408) <= Tolerance
            && Math.Abs(horizontalBoxes[2].X - 792) <= Tolerance,
            string.Join(" / ", horizontalBoxes.Select(b => b.X.ToString("F2"))));

        Near("横向排版：内容宽 = 3×368 + 2×16 + 2×24 = 1184", horizontal.ContentWidth, 1184, "ContentWidth");
        Near("横向排版：内容高不小于视口高（600）", horizontal.ContentHeight, 600, "ContentHeight");
        Near("横向排版：阅读轴长 = 内容宽 = 1184",
            PdfContinuousLayout.ReadingLength(horizontal, PdfReadMode.Horizontal), 1184, "ReadingLength");
        Near("横向排版：视口在阅读轴上的长度 = 视口宽 = 800",
            PdfContinuousLayout.ReadingViewport(800, 600, PdfReadMode.Horizontal), 800, "ReadingViewport");
        Near("横向排版：最大阅读偏移 = 1184 − 800 = 384",
            PdfContinuousLayout.MaximumOffset(horizontal, PdfReadMode.Horizontal, 800, 600), 384, "MaximumOffset");

        Check("横向排版：每页在阅读轴上的起止 = 24~392 / 408~776 / 792~1160",
            horizontalBoxes.Count == 3
            && Math.Abs(horizontalBoxes[0].MainStart(true) - 24) <= Tolerance
            && Math.Abs(horizontalBoxes[0].MainEnd(true) - 392) <= Tolerance
            && Math.Abs(horizontalBoxes[1].MainStart(true) - 408) <= Tolerance
            && Math.Abs(horizontalBoxes[1].MainEnd(true) - 776) <= Tolerance
            && Math.Abs(horizontalBoxes[2].MainStart(true) - 792) <= Tolerance
            && Math.Abs(horizontalBoxes[2].MainEnd(true) - 1160) <= Tolerance,
            string.Join(" / ", horizontalBoxes.Select(b => $"{b.MainStart(true):F0}~{b.MainEnd(true):F0}")));

        Check("横向排版：OffsetOfPage 各页开头 = 24 / 408 / 792（越界夹到最后一页）",
            Math.Abs(PdfContinuousLayout.OffsetOfPage(horizontal, PdfReadMode.Horizontal, 0) - 24) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetOfPage(horizontal, PdfReadMode.Horizontal, 1) - 408) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetOfPage(horizontal, PdfReadMode.Horizontal, 99) - 792) <= Tolerance);

        Check("横向排版：PageIndexAtOffset 定位（24 → 第 1 页、400 → 第 2 页、1160 → 最后一页）",
            PdfContinuousLayout.PageIndexAtOffset(horizontal, PdfReadMode.Horizontal, 24) == 0
            && PdfContinuousLayout.PageIndexAtOffset(horizontal, PdfReadMode.Horizontal, 400) == 1
            && PdfContinuousLayout.PageIndexAtOffset(horizontal, PdfReadMode.Horizontal, 1160) == 2);

        Check("横向排版：可见页范围（贴底时可见范围跨到最后一页）",
            PdfContinuousLayout.VisibleRange(horizontal, PdfReadMode.Horizontal, 384, 800) == (0, 2),
            $"{PdfContinuousLayout.VisibleRange(horizontal, PdfReadMode.Horizontal, 384, 800)}");

        Check("横向排版：比例换算（0.5 → 192、192 / 384 → 0.5）",
            Math.Abs(PdfContinuousLayout.OffsetForRatio(0.5, 384) - 192) <= Tolerance
            && Math.Abs(PdfContinuousLayout.RatioOf(192, 384) - 0.5) <= Tolerance);

        // ================================================================
        // 4. 连续滚动排版：空页 / 视口没量出来 / 坏页尺寸（保证不崩、不除以 0）
        // ================================================================
        var emptyVertical = PdfContinuousLayout.Compute(
            PdfReadMode.Vertical, Array.Empty<PdfPageInfo>(), 800, 600);
        Check("空页：返回空排版、内容尺寸 = 视口、不崩",
            emptyVertical.IsEmpty && emptyVertical.PageCount == 0
            && Math.Abs(emptyVertical.ContentWidth - 800) <= Tolerance
            && Math.Abs(emptyVertical.ContentHeight - 600) <= Tolerance,
            $"{emptyVertical.PageCount} 页，内容 {emptyVertical.ContentWidth}×{emptyVertical.ContentHeight}");

        Check("空页：页码 / 偏移 / 可见范围查询都给安全默认值",
            Math.Abs(PdfContinuousLayout.ReadingLength(emptyVertical, PdfReadMode.Vertical) - 600) <= Tolerance
            && Math.Abs(PdfContinuousLayout.MaximumOffset(emptyVertical, PdfReadMode.Vertical, 800, 600)) <= Tolerance
            && PdfContinuousLayout.OffsetOfPage(emptyVertical, PdfReadMode.Vertical, 3) == 0
            && PdfContinuousLayout.PageIndexAtOffset(emptyVertical, PdfReadMode.Vertical, 100) == 0
            && PdfContinuousLayout.PageIndexAtViewportCenter(emptyVertical, PdfReadMode.Vertical, 0, 600) == 0
            && PdfContinuousLayout.VisibleRange(emptyVertical, PdfReadMode.Vertical, 0, 600) == (-1, -1));

        Check("pages 传 null 与空列表一样处理（内容 = 视口、没页）",
            PdfContinuousLayout.Compute(PdfReadMode.Vertical, null, 800, 600) is
            { IsEmpty: true, ContentWidth: var nullContentWidth, ContentHeight: var nullContentHeight }
            && Math.Abs(nullContentWidth - 800) <= Tolerance
            && Math.Abs(nullContentHeight - 600) <= Tolerance);

        var zeroViewportSizes = new (double Width, double Height)[]
        {
            (0, 600), (800, 0), (-800, 600), (800, -600), (double.NaN, 600), (800, double.NaN),
        };
        Check("视口没量出来（0 / 负数 / NaN）：返回空排版、内容尺寸 0，不抛异常",
            zeroViewportSizes.All(size =>
            {
                var result = PdfContinuousLayout.Compute(PdfReadMode.Vertical, threePortraitPages, size.Width, size.Height);
                return result.IsEmpty && result.PageCount == 0
                       && Math.Abs(result.ContentWidth) <= Tolerance
                       && Math.Abs(result.ContentHeight) <= Tolerance;
            }),
            string.Join(" / ", zeroViewportSizes.Select(s => $"{s.Width}×{s.Height}")));

        var brokenSizePages = new[]
        {
            new PdfPageInfo(0, double.NaN, 300),
            new PdfPageInfo(1, 0, 600),
            new PdfPageInfo(2, -5, 100),
        };
        var brokenSizeLayout = PdfContinuousLayout.Compute(PdfReadMode.Vertical, brokenSizePages, 800, 600);
        Check("坏页尺寸：按正方形兜底（边 = 视口宽 − 留白 = 752），不抛异常",
            brokenSizeLayout.PageCount == 3
            && brokenSizeLayout.Boxes.All(b =>
                Math.Abs(b.Width - 752) <= Tolerance && Math.Abs(b.Height - 752) <= Tolerance),
            string.Join(" / ", brokenSizeLayout.Boxes.Select(b => $"{b.Width:F0}×{b.Height:F0}")));
        Near("坏页尺寸：内容高照常算 = 3×752 + 2×16 + 2×24 = 2336",
            brokenSizeLayout.ContentHeight, 2336, "ContentHeight");

        // gap / margin 传坏值：负数按 0、NaN 退回默认（16 / 24），排版不塌
        var twoPortraitPages = new[]
        {
            new PdfPageInfo(0, 400, 600),
            new PdfPageInfo(1, 400, 600),
        };
        // gap / margin 传非法值（≤ 0 / NaN）：Compute 内部按「没给」处理，退回默认 16 / 24，不会把页贴在一起。
        var invalidSpacing = PdfContinuousLayout.Compute(PdfReadMode.Vertical, twoPortraitPages, 800, 600, -10, -5);
        Check("非法 gap / margin（负数）退回默认 16 / 24：页宽 752、第二页 Y = 24 + 1128 + 16 = 1168",
            invalidSpacing.Boxes.Count == 2
            && Math.Abs(invalidSpacing.Boxes[0].Width - 752) <= Tolerance
            && Math.Abs(invalidSpacing.Boxes[0].X - 24) <= Tolerance
            && Math.Abs(invalidSpacing.Boxes[0].Y - 24) <= Tolerance
            && Math.Abs(invalidSpacing.Boxes[1].Y - 1168) <= Tolerance,
            $"页宽 {invalidSpacing.Boxes.FirstOrDefault()?.Width:F2}，第二页 Y {invalidSpacing.Boxes.LastOrDefault()?.Y:F2}");

        var zeroSpacing = PdfContinuousLayout.Compute(PdfReadMode.Vertical, twoPortraitPages, 800, 600, 0, 0);
        Check("gap / margin 传 0 也按「没给」处理、退回默认 16 / 24（不会把页贴在一起）",
            zeroSpacing.Boxes.Count == 2 && Math.Abs(zeroSpacing.Boxes[1].Y - 1168) <= Tolerance,
            $"第二页 Y {zeroSpacing.Boxes.LastOrDefault()?.Y:F2}");

        var defaultSpacing = PdfContinuousLayout.Compute(PdfReadMode.Vertical, twoPortraitPages, 800, 600, double.NaN, double.NaN);
        Check("NaN gap / margin 退回默认（16 / 24）：页宽 752、第二页 Y = 24 + 1128 + 16 = 1168",
            defaultSpacing.Boxes.Count == 2
            && Math.Abs(defaultSpacing.Boxes[0].Width - 752) <= Tolerance
            && Math.Abs(defaultSpacing.Boxes[0].X - 24) <= Tolerance
            && Math.Abs(defaultSpacing.Boxes[1].Y - 1168) <= Tolerance,
            $"宽 {defaultSpacing.Boxes.FirstOrDefault()?.Width:F2}，第二页 Y {defaultSpacing.Boxes.LastOrDefault()?.Y:F2}");

        var shortContent = PdfContinuousLayout.Compute(
            PdfReadMode.Vertical, new[] { new PdfPageInfo(0, 400, 600) }, 800, 5000);
        Check("内容比视口短：最大偏移夹成 0（没得滚）、比例换算也给安全值",
            Math.Abs(PdfContinuousLayout.MaximumOffset(shortContent, PdfReadMode.Vertical, 800, 5000)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.OffsetForRatio(0.5, 0)) <= Tolerance
            && Math.Abs(PdfContinuousLayout.RatioOf(0, 0)) <= Tolerance);

        Check("空排版（null）：各函数给安全默认值，不抛空引用",
            PdfContinuousLayout.ReadingLength(null, PdfReadMode.Vertical) == 0
            && PdfContinuousLayout.MaximumOffset(null, PdfReadMode.Vertical, 800, 600) == 0
            && PdfContinuousLayout.OffsetOfPage(null, PdfReadMode.Vertical, 2) == 0
            && PdfContinuousLayout.PageIndexAtOffset(null, PdfReadMode.Vertical, 500) == 0
            && PdfContinuousLayout.PageIndexAtViewportCenter(null, PdfReadMode.Vertical, 500, 600) == 0
            && PdfContinuousLayout.VisibleRange(null, PdfReadMode.Vertical, 0, 600) == (-1, -1));

        Check("视口长度坏值（NaN / 0 / 负数）当作 0：纵向看视口高、横向看视口宽",
            PdfContinuousLayout.ReadingViewport(800, double.NaN, PdfReadMode.Vertical) == 0
            && PdfContinuousLayout.ReadingViewport(800, 0, PdfReadMode.Vertical) == 0
            && PdfContinuousLayout.ReadingViewport(800, -1, PdfReadMode.Vertical) == 0
            && PdfContinuousLayout.ReadingViewport(double.NaN, 600, PdfReadMode.Horizontal) == 0
            && PdfContinuousLayout.ReadingViewport(0, 600, PdfReadMode.Horizontal) == 0
            && PdfContinuousLayout.ReadingViewport(-1, 600, PdfReadMode.Horizontal) == 0
            && PdfContinuousLayout.ReadingViewport(800, 600, PdfReadMode.Vertical) == 600
            && PdfContinuousLayout.ReadingViewport(800, 600, PdfReadMode.Horizontal) == 800);

        // ================================================================
        // 5. 双页翻页（PdfSpreadLayout）—— 配对 / 翻页步进 / 槽位排版
        // ================================================================
        Check("双页配对：页数 5 时最后一屏从第 4 页开始（最后一屏只有左页）",
            PdfSpreadLayout.LastStartIndex(5) == 4);

        Check("双页配对：页数 0~6 的最后一屏起始页 = 0 / 0 / 0 / 2 / 2 / 4 / 4",
            PdfSpreadLayout.LastStartIndex(0) == 0 && PdfSpreadLayout.LastStartIndex(1) == 0
            && PdfSpreadLayout.LastStartIndex(2) == 0 && PdfSpreadLayout.LastStartIndex(3) == 2
            && PdfSpreadLayout.LastStartIndex(4) == 2 && PdfSpreadLayout.LastStartIndex(5) == 4
            && PdfSpreadLayout.LastStartIndex(6) == 4,
            string.Join(" / ", Enumerable.Range(0, 7).Select(n => $"{n}→{PdfSpreadLayout.LastStartIndex(n)}")));

        var (visibleLeft0, visibleRight0) = PdfSpreadLayout.VisiblePages(0, 5);
        Check("可见对：第 0 页 → (0, 1)", visibleLeft0 == 0 && visibleRight0 == 1,
            $"({visibleLeft0}, {visibleRight0})");
        var (visibleLeft2, visibleRight2) = PdfSpreadLayout.VisiblePages(2, 5);
        Check("可见对：第 2 页 → (2, 3)", visibleLeft2 == 2 && visibleRight2 == 3,
            $"({visibleLeft2}, {visibleRight2})");
        var (visibleLeft4, visibleRight4) = PdfSpreadLayout.VisiblePages(4, 5);
        Check("可见对：第 4 页 → (4, null)（奇数页最后一屏右槽空）",
            visibleLeft4 == 4 && visibleRight4 is null, $"({visibleLeft4}, {visibleRight4?.ToString() ?? "null"})");

        var (visibleLeft3, visibleRight3) = PdfSpreadLayout.VisiblePages(3, 5);
        Check("可见对：传入奇数页号也对齐到所在屏（3 → (2, 3)）",
            visibleLeft3 == 2 && visibleRight3 == 3, $"({visibleLeft3}, {visibleRight3})");

        var (singleLeft, singleRight) = PdfSpreadLayout.VisiblePages(0, 1);
        Check("可见对：只有 1 页 → (0, null)，1 屏就看完", singleLeft == 0 && singleRight is null);
        var (pairLeft, pairRight) = PdfSpreadLayout.VisiblePages(1, 2);
        Check("可见对：2 页 → (0, 1)（第 1 屏两页并排）", pairLeft == 0 && pairRight == 1);

        var (emptyLeft, emptyRight) = PdfSpreadLayout.VisiblePages(0, 0);
        Check("可见对：0 页 → (null, null)，不崩", emptyLeft is null && emptyRight is null);

        var (outLeft, outRight) = PdfSpreadLayout.VisiblePages(99, 5);
        Check("可见对：越界页号夹到最后一屏（99 → (4, null)）", outLeft == 4 && outRight is null,
            $"({outLeft}, {outRight?.ToString() ?? "null"})");

        Check("NormalizeStart：页号对齐到所在屏起始（0→0 / 1→0 / 2→2 / 3→2 / 4→4 / 5→4）",
            PdfSpreadLayout.NormalizeStart(0, 5) == 0 && PdfSpreadLayout.NormalizeStart(1, 5) == 0
            && PdfSpreadLayout.NormalizeStart(2, 5) == 2 && PdfSpreadLayout.NormalizeStart(3, 5) == 2
            && PdfSpreadLayout.NormalizeStart(4, 5) == 4 && PdfSpreadLayout.NormalizeStart(5, 5) == 4);

        Check("NormalizeStart：负数 / 越界 / 0 页都夹到安全值",
            PdfSpreadLayout.NormalizeStart(-3, 5) == 0
            && PdfSpreadLayout.NormalizeStart(99, 5) == 4
            && PdfSpreadLayout.NormalizeStart(3, 0) == 0,
            $"{PdfSpreadLayout.NormalizeStart(-3, 5)} / {PdfSpreadLayout.NormalizeStart(99, 5)} / {PdfSpreadLayout.NormalizeStart(3, 0)}");

        Check("Step：前进一次翻两页（0→2、2→4、4 停在 4）",
            PdfSpreadLayout.Step(0, 5, forward: true) == 2
            && PdfSpreadLayout.Step(2, 5, forward: true) == 4
            && PdfSpreadLayout.Step(4, 5, forward: true) == 4);

        Check("Step：后退一次翻两页（4→2、0 停在 0、奇数页号先对齐）",
            PdfSpreadLayout.Step(4, 5, forward: false) == 2
            && PdfSpreadLayout.Step(0, 5, forward: false) == 0
            && PdfSpreadLayout.Step(3, 5, forward: false) == 0
            && PdfSpreadLayout.Step(0, 0, forward: true) == 0);

        Check("CanGoNext：第一屏 / 中间屏能下翻、最后一屏不能",
            PdfSpreadLayout.CanGoNext(0, 5) && PdfSpreadLayout.CanGoNext(2, 5)
            && !PdfSpreadLayout.CanGoNext(4, 5) && !PdfSpreadLayout.CanGoNext(0, 0)
            && !PdfSpreadLayout.CanGoNext(0, 1));

        Check("CanGoPrevious：第一屏不能上翻、之后可以、0 页不行",
            !PdfSpreadLayout.CanGoPrevious(0, 5) && PdfSpreadLayout.CanGoPrevious(2, 5)
            && PdfSpreadLayout.CanGoPrevious(4, 5) && !PdfSpreadLayout.CanGoPrevious(0, 0));

        Check("0 页：LastStart / Step / 可见对 / 前后翻 都安全，不崩",
            PdfSpreadLayout.LastStartIndex(0) == 0
            && PdfSpreadLayout.Step(0, 0, forward: true) == 0
            && PdfSpreadLayout.ProgressRatio(0, 0) == 0
            && emptyLeft is null && emptyRight is null
            && !PdfSpreadLayout.CanGoNext(0, 0) && !PdfSpreadLayout.CanGoPrevious(0, 0));

        Check("StartForPage 与 NormalizeStart 一致（连续滚动切双页时用）",
            PdfSpreadLayout.StartForPage(3, 5) == 2
            && PdfSpreadLayout.StartForPage(99, 5) == 4
            && PdfSpreadLayout.StartForPage(0, 5) == 0);

        Check("ProgressRatio：整本翻到第几屏（0 / 0.5 / 1）",
            Math.Abs(PdfSpreadLayout.ProgressRatio(0, 5)) <= Tolerance
            && Math.Abs(PdfSpreadLayout.ProgressRatio(2, 5) - 0.5) <= Tolerance
            && Math.Abs(PdfSpreadLayout.ProgressRatio(4, 5) - 1) <= Tolerance
            && Math.Abs(PdfSpreadLayout.ProgressRatio(3, 5) - 0.5) <= Tolerance);

        Check("ProgressRatio：只有一屏 / 0 页时为 0（进度条不出现除以 0）",
            Math.Abs(PdfSpreadLayout.ProgressRatio(0, 1)) <= Tolerance
            && Math.Abs(PdfSpreadLayout.ProgressRatio(0, 0)) <= Tolerance);

        // ---- 双页排版：1000×800 视口、margin=24、gap=16 →
        // 可用宽 = 952、槽宽 = (952 − 16) / 2 = 468；400×600 页等比塞进 468×752 = 468×702（垂直居中 y = 24 + 25 = 49）。
        var spreadPages = new[]
        {
            new PdfPageInfo(0, 400, 600),
            new PdfPageInfo(1, 400, 600),
            new PdfPageInfo(2, 400, 600),
            new PdfPageInfo(3, 400, 600),
            new PdfPageInfo(4, 400, 600),
        };
        var spread = PdfSpreadLayout.Compute(spreadPages, 0, 1000, 800);
        var spreadLeft = spread.Left;
        var spreadRight = spread.Right;

        Near("双页排版：内容 = 视口（宽 1000）", spread.ContentWidth, 1000, "ContentWidth");
        Near("双页排版：内容 = 视口（高 800）", spread.ContentHeight, 800, "ContentHeight");

        Check("双页排版：左右槽分别显示第 0 / 1 页",
            spreadLeft is { PageIndex: 0 } && spreadRight is { PageIndex: 1 },
            $"左 {spreadLeft?.PageIndex.ToString() ?? "null"} / 右 {spreadRight?.PageIndex.ToString() ?? "null"}");

        Check("双页排版：左槽 x = 24、槽宽 468（(1000 − 48 − 16) / 2）",
            spreadLeft is { } leftSlotForSlotCheck
            && Math.Abs(leftSlotForSlotCheck.X - 24) <= Tolerance
            && Math.Abs(leftSlotForSlotCheck.Width - 468) <= Tolerance,
            spreadLeft is { } leftDetail ? $"x={leftDetail.X:F2}, w={leftDetail.Width:F2}" : "(左槽为空)");

        Check("双页排版：左页按 400:600 等比放进槽里 = 468×702",
            spreadLeft is { } leftPageCheck
            && Math.Abs(leftPageCheck.Width - 468) <= Tolerance
            && Math.Abs(leftPageCheck.Height - 702) <= Tolerance,
            spreadLeft is { } leftSizeDetail ? $"{leftSizeDetail.Width:F2}×{leftSizeDetail.Height:F2}" : "(左槽为空)");

        Check("双页排版：左右页垂直居中（y = 24 + (752 − 702) / 2 = 49）",
            spreadLeft is { } leftYCheck && Math.Abs(leftYCheck.Y - 49) <= Tolerance
            && spreadRight is { } rightYCheck && Math.Abs(rightYCheck.Y - 49) <= Tolerance,
            $"左 y={spreadLeft?.Y:F2}，右 y={spreadRight?.Y:F2}");

        Check("双页排版：右槽 x = 24 + 468 + 16 = 508、页 468×702",
            spreadRight is { } rightSlotCheck
            && Math.Abs(rightSlotCheck.X - 508) <= Tolerance
            && Math.Abs(rightSlotCheck.Width - 468) <= Tolerance
            && Math.Abs(rightSlotCheck.Height - 702) <= Tolerance,
            spreadRight is { } rightDetail ? $"x={rightDetail.X:F2}, {rightDetail.Width:F2}×{rightDetail.Height:F2}" : "(右槽为空)");

        Check("双页排版：左右页都保持 400:600 比例（不拉伸变形）",
            spreadLeft is { } leftRatioCheck && spreadRight is { } rightRatioCheck
            && Math.Abs(leftRatioCheck.Width / leftRatioCheck.Height - 2.0 / 3.0) <= 0.001
            && Math.Abs(rightRatioCheck.Width / rightRatioCheck.Height - 2.0 / 3.0) <= 0.001);

        var lastSpread = PdfSpreadLayout.Compute(spreadPages, 4, 1000, 800);
        Check("双页排版：最后一屏只有左页（页数 5 从第 4 页开始）",
            lastSpread.Left is { PageIndex: 4 } && lastSpread.Right is null,
            $"左 {lastSpread.Left?.PageIndex.ToString() ?? "null"} / 右 {lastSpread.Right?.PageIndex.ToString() ?? "null"}");

        var emptySpread = PdfSpreadLayout.Compute(Array.Empty<PdfPageInfo>(), 0, 1000, 800);
        Check("双页排版：0 页 → 内容 = 视口、左右都空，不崩",
            emptySpread.Left is null && emptySpread.Right is null
            && Math.Abs(emptySpread.ContentWidth - 1000) <= Tolerance
            && Math.Abs(emptySpread.ContentHeight - 800) <= Tolerance);

        var zeroViewportSpread = PdfSpreadLayout.Compute(spreadPages, 0, 0, 800);
        Check("双页排版：视口 0 → 左右都空、不崩",
            zeroViewportSpread.Left is null && zeroViewportSpread.Right is null
            && Math.Abs(zeroViewportSpread.ContentWidth) <= Tolerance);

        var brokenSizeSpread = PdfSpreadLayout.Compute(
            new[] { new PdfPageInfo(0, double.NaN, double.NaN) }, 0, 1000, 800);
        Check("双页排版：坏页尺寸直接铺满槽位（468×752），不除以 0",
            brokenSizeSpread.Left is { } brokenSlot
            && Math.Abs(brokenSlot.Width - 468) <= Tolerance
            && Math.Abs(brokenSlot.Height - 752) <= Tolerance,
            brokenSizeSpread.Left is { } brokenDetail
                ? $"{brokenDetail.Width:F2}×{brokenDetail.Height:F2}"
                : "(左槽为空)");

        var fitPortrait = PdfSpreadLayout.FitSize(portrait, 468, 752);
        Check("FitSize：400×600 塞进 468×752 → 468×702（受宽度限制）",
            Math.Abs(fitPortrait.Width - 468) <= Tolerance && Math.Abs(fitPortrait.Height - 702) <= Tolerance,
            $"{fitPortrait.Width:F2}×{fitPortrait.Height:F2}");

        var fitWide = PdfSpreadLayout.FitSize(landscape, 468, 752);
        Check("FitSize：横版 600×400 塞进 468×752 → 468×312（等比不拉高）",
            Math.Abs(fitWide.Width - 468) <= Tolerance && Math.Abs(fitWide.Height - 312) <= Tolerance,
            $"{fitWide.Width:F2}×{fitWide.Height:F2}");

        var fitTall = PdfSpreadLayout.FitSize(new PdfPageInfo(0, 100, 400), 468, 752);
        Check("FitSize：窄高页 100×400 塞进 468×752 → 188×752（受高度限制、等比放大）",
            Math.Abs(fitTall.Width - 188) <= Tolerance && Math.Abs(fitTall.Height - 752) <= Tolerance,
            $"{fitTall.Width:F2}×{fitTall.Height:F2}");

        var fitBroken = PdfSpreadLayout.FitSize(brokenPage, 468, 752);
        Check("FitSize：坏页尺寸直接铺满槽位（不按 2/3 比例硬算）",
            Math.Abs(fitBroken.Width - 468) <= Tolerance && Math.Abs(fitBroken.Height - 752) <= Tolerance,
            $"{fitBroken.Width:F2}×{fitBroken.Height:F2}");

        var centeredSlot = PdfSpreadLayout.FitIntoSlot(portrait, 3, 100, 50, 468, 752);
        Check("FitIntoSlot：槽位里水平 / 垂直居中（x = 100、y = 50 + 25）、页号保留",
            centeredSlot.PageIndex == 3
            && Math.Abs(centeredSlot.X - 100) <= Tolerance
            && Math.Abs(centeredSlot.Y - 75) <= Tolerance
            && Math.Abs(centeredSlot.Width - 468) <= Tolerance
            && Math.Abs(centeredSlot.Height - 702) <= Tolerance,
            $"({centeredSlot.X:F2}, {centeredSlot.Y:F2}) {centeredSlot.Width:F2}×{centeredSlot.Height:F2}");

        var brokenFitSlot = PdfSpreadLayout.FitIntoSlot(new PdfPageInfo(0, double.NaN, 0), 0, 100, 50, 468, 752);
        Check("FitIntoSlot：坏页尺寸铺满槽位（x / y = 槽位原点）",
            Math.Abs(brokenFitSlot.X - 100) <= Tolerance && Math.Abs(brokenFitSlot.Y - 50) <= Tolerance
            && Math.Abs(brokenFitSlot.Width - 468) <= Tolerance && Math.Abs(brokenFitSlot.Height - 752) <= Tolerance,
            $"({brokenFitSlot.X:F2}, {brokenFitSlot.Y:F2}) {brokenFitSlot.Width:F2}×{brokenFitSlot.Height:F2}");

        // ================================================================
        // 6. 拖动惯性（PdfInertia）—— 采样 / 衰减 / 合成 / 封顶
        // ================================================================
        Check("惯性参数与「图片连续阅读器」保持一套手感（摩擦 0.0035 / 最低速度 0.02 / 采样窗口 120ms / 封顶 6px/ms）",
            Math.Abs(PdfInertia.DefaultFrictionPerMillisecond - 0.0035) <= 1e-9
            && Math.Abs(PdfInertia.DefaultMinimumSpeed - 0.02) <= 1e-9
            && Math.Abs(PdfInertia.SampleWindowMilliseconds - 120) <= 1e-9
            && Math.Abs(PdfInertia.DefaultMaxSpeed - 6.0) <= 1e-9,
            $"{PdfInertia.DefaultFrictionPerMillisecond} / {PdfInertia.DefaultMinimumSpeed} / " +
            $"{PdfInertia.SampleWindowMilliseconds} / {PdfInertia.DefaultMaxSpeed}");

        var inertia = new PdfInertia();
        Check("惯性：实例默认值 = 类常量（配置改之前就是这套手感）",
            Math.Abs(inertia.FrictionPerMillisecond - PdfInertia.DefaultFrictionPerMillisecond) <= 1e-12
            && Math.Abs(inertia.MinimumSpeed - PdfInertia.DefaultMinimumSpeed) <= 1e-12);

        var (idleMain, idleCross) = inertia.ComputeVelocity();
        Check("静止：还没采样时速度为 0（不甩）", idleMain == 0 && idleCross == 0);

        inertia.Note(0, 10, 20);
        var (singleMain, singleCross) = inertia.ComputeVelocity();
        Check("单点采样：一个点算不出速度（0, 0）", singleMain == 0 && singleCross == 0);

        inertia.Reset();
        inertia.Note(0, 0, 0);
        inertia.Note(100, 50, 0);
        var (fastMain, fastCross) = inertia.ComputeVelocity();
        Check("采样速度：100ms 走 50px → 0.5 像素/毫秒",
            Math.Abs(fastMain - 0.5) <= 1e-9 && Math.Abs(fastCross) <= 1e-9,
            $"({fastMain:F4}, {fastCross:F4})");

        inertia.Reset();
        inertia.Note(0, 0, 0);
        inertia.Note(50, 0, 15);
        var (crossMain, crossSpeed) = inertia.ComputeVelocity();
        Check("采样速度：横轴（cross）也算得对（50ms 走 15px → 0.3）",
            Math.Abs(crossMain) <= 1e-9 && Math.Abs(crossSpeed - 0.3) <= 1e-9,
            $"({crossMain:F4}, {crossSpeed:F4})");

        inertia.Reset();
        inertia.Note(0, 0, 0);
        inertia.Note(100, -50, 0);
        var (reverseMain, _) = inertia.ComputeVelocity();
        Check("采样速度：反向拖动是负速度（-0.5）", Math.Abs(reverseMain + 0.5) <= 1e-9,
            reverseMain.ToString("F4"));

        inertia.Reset();
        inertia.Note(100, 0, 0);
        inertia.Note(100, 50, 0);
        var (sameTimeMain, sameTimeCross) = inertia.ComputeVelocity();
        Check("时间没走动（dt = 0）：不甩、不算出 Infinity", sameTimeMain == 0 && sameTimeCross == 0);

        inertia.Reset();
        inertia.Note(0, 0, 0);
        inertia.Note(200, 50, 0);
        var (pausedMain, pausedCross) = inertia.ComputeVelocity();
        Check("停顿超过采样窗口（120ms）：旧采样被丢掉 → 速度归 0（松手不甩）",
            pausedMain == 0 && pausedCross == 0, $"({pausedMain:F4}, {pausedCross:F4})");

        inertia.Note(250, 60, 0);
        var (resumedMain, _) = inertia.ComputeVelocity();
        Check("窗口内恢复采样后又能算出速度（50ms 走 10px → 0.2）",
            Math.Abs(resumedMain - 0.2) <= 1e-9, resumedMain.ToString("F4"));

        inertia.Reset();
        inertia.Note(0, 0, 0);
        inertia.Note(60, 30, 0);
        inertia.Note(100, 50, 0);
        var (windowMain, _) = inertia.ComputeVelocity();
        Check("速度取采样窗口里「最早到最新」的平均（100ms 走 50px → 0.5，不是只看最后两点）",
            Math.Abs(windowMain - 0.5) <= 1e-9, windowMain.ToString("F4"));

        inertia.Reset();
        var (afterResetMain, afterResetCross) = inertia.ComputeVelocity();
        Check("Reset 后重新开始（速度回到 0）", afterResetMain == 0 && afterResetCross == 0);

        Check("IsFastEnough：默认阈值 0.02（0.5 能甩、0.01 不能）",
            PdfInertia.IsFastEnough(0.5, 0, PdfInertia.DefaultMinimumSpeed)
            && !PdfInertia.IsFastEnough(0.01, 0, PdfInertia.DefaultMinimumSpeed));

        Check("IsFastEnough：按两轴合成速度判断（0.015 + 0.015 ≈ 0.0212 ≥ 0.02 能甩）",
            PdfInertia.IsFastEnough(0.015, 0.015, PdfInertia.DefaultMinimumSpeed));

        Check("IsFastEnough：NaN 速度不算快（不甩，避免把界面滚飞）",
            !PdfInertia.IsFastEnough(double.NaN, 0, PdfInertia.DefaultMinimumSpeed)
            && !PdfInertia.IsFastEnough(0, double.NaN, PdfInertia.DefaultMinimumSpeed));

        Check("IsFastEnough：阈值 ≤ 0 时任何速度都算快",
            PdfInertia.IsFastEnough(0, 0, 0) && PdfInertia.IsFastEnough(0, 0, -1));

        inertia.MinimumSpeed = 1.0;
        Check("IsFastEnough：实例方法用实例自己的阈值（改成 1.0 后 0.5 不够快）",
            !inertia.IsFastEnough(0.5, 0) && inertia.IsFastEnough(1.0, 0));

        var decayInertia = new PdfInertia();
        var (decayMain, decayCross) = decayInertia.Decay(2, -1, 200);
        Check("Decay：指数衰减后方向不变（正还是正、负还是负）且幅度变小",
            decayMain > 0 && decayCross < 0 && decayMain < 2 && Math.Abs(decayCross) < 1);
        Check("Decay：按 e^(−摩擦×毫秒) 衰减（200ms 后 ≈ 一半，e^−0.7 ≈ 0.4966）",
            Math.Abs(decayMain - 2 * Math.Exp(-0.7)) <= 1e-9
            && Math.Abs(decayCross + Math.Exp(-0.7)) <= 1e-9,
            $"({decayMain:F4}, {decayCross:F4})");

        var (zeroElapsedMain, zeroElapsedCross) = decayInertia.Decay(2, -1, 0);
        var (negativeElapsedMain, negativeElapsedCross) = decayInertia.Decay(2, -1, -5);
        var (nanElapsedMain, nanElapsedCross) = decayInertia.Decay(2, -1, double.NaN);
        Check("Decay：0 / 负数 / NaN 时间不减（原样返回）",
            zeroElapsedMain == 2 && zeroElapsedCross == -1
            && negativeElapsedMain == 2 && negativeElapsedCross == -1
            && nanElapsedMain == 2 && nanElapsedCross == -1);

        var frictionless = new PdfInertia { FrictionPerMillisecond = 0 };
        var (noFrictionMain, noFrictionCross) = frictionless.Decay(1.5, -0.5, 1000);
        Check("Decay：摩擦为 0 时速度不衰减（滑到底）",
            Math.Abs(noFrictionMain - 1.5) <= 1e-12 && Math.Abs(noFrictionCross + 0.5) <= 1e-12);

        var rough = new PdfInertia { FrictionPerMillisecond = 0.02 };
        var (roughMain, _) = rough.Decay(2, 0, 100);
        var (smoothMain, _) = decayInertia.Decay(2, 0, 100);
        Check("Decay：摩擦系数越大停得越快（0.02 衰减比默认快）", roughMain < smoothMain,
            $"{roughMain:F4} < {smoothMain:F4}");

        var (combineAccumMain, combineAccumCross) = PdfInertia.Combine(1, 0, 0.5, 0, accumulate: true);
        Check("Combine：累加模式把残余速度加上（1 + 0.5 = 1.5）",
            Math.Abs(combineAccumMain - 1.5) <= 1e-12 && Math.Abs(combineAccumCross) <= 1e-12);

        var (combineReplaceMain, combineReplaceCross) = PdfInertia.Combine(1, 0, 0.5, 0.5, accumulate: false);
        Check("Combine：不累加模式忽略残余速度（下一次拖动重新计）",
            combineReplaceMain == 1 && combineReplaceCross == 0);

        var (cappedMain, cappedCross) = PdfInertia.Combine(5, 0, 5, 0, accumulate: true);
        Check("Combine：叠加后按 6px/ms 封顶（5 + 5 → 6，不会滚穿整本）",
            Math.Abs(cappedMain - 6) <= 1e-9 && Math.Abs(cappedCross) <= 1e-9,
            $"({cappedMain:F4}, {cappedCross:F4})");

        var (diagonalMain, diagonalCross) = PdfInertia.Combine(3, 4, 3, 4, accumulate: true);
        Check("Combine：封顶时按比例压回、保持方向（3,4 + 3,4 → 3.6,4.8，合成速度 6）",
            Math.Abs(diagonalMain - 3.6) <= 1e-9 && Math.Abs(diagonalCross - 4.8) <= 1e-9
            && Math.Abs(Math.Sqrt(diagonalMain * diagonalMain + diagonalCross * diagonalCross) - 6) <= 1e-9,
            $"({diagonalMain:F4}, {diagonalCross:F4})");

        var (noCapMain, noCapCross) = PdfInertia.Combine(2, 0, 1, 0, accumulate: true);
        Check("Combine：没超过上限时不缩放（2 + 1 = 3）",
            Math.Abs(noCapMain - 3) <= 1e-12 && Math.Abs(noCapCross) <= 1e-12);

        var (nanCombineMain, nanCombineCross) = PdfInertia.Combine(double.NaN, double.NaN, double.NaN, double.NaN, accumulate: true);
        Check("Combine：NaN 速度当 0 处理（界面不会因为坏值卡死）",
            nanCombineMain == 0 && nanCombineCross == 0);

        var (nanCarriedMain, nanCarriedCross) = PdfInertia.Combine(1, 0.5, double.NaN, double.NaN, accumulate: true);
        Check("Combine：只有残余速度是 NaN 时不影响这一次甩（1, 0.5 保留）",
            Math.Abs(nanCarriedMain - 1) <= 1e-12 && Math.Abs(nanCarriedCross - 0.5) <= 1e-12);

        var (customCapMain, customCapCross) = PdfInertia.Combine(5, 0, 3, 0, accumulate: true, maxSpeed: 4);
        Check("Combine：可以传入更低的上限（8 压到 4）",
            Math.Abs(customCapMain - 4) <= 1e-9 && Math.Abs(customCapCross) <= 1e-9);

        var (unlimitedMain, unlimitedCross) = PdfInertia.Combine(10, 0, 0, 0, accumulate: false, maxSpeed: 0);
        Check("Combine：上限 ≤ 0 表示不封顶", unlimitedMain == 10 && unlimitedCross == 0);

        var (negativeCappedMain, negativeCappedCross) = PdfInertia.Combine(-10, 0, 0, 0, accumulate: false);
        Check("Combine：负方向也按上限压回（-10 → -6，方向不变）",
            Math.Abs(negativeCappedMain + 6) <= 1e-9 && Math.Abs(negativeCappedCross) <= 1e-9);

        // ================================================================
        // 7. 配置（PdfReaderConfig）—— 默认值 / 生效值夹取 / JSON 存字符串 / 自动落盘往返
        // ================================================================
        var freshConfig = new PdfReaderConfig();
        Check("配置默认值：纵向 / 惯性开 / 滚轮 120 / 上次文件夹空",
            freshConfig.ReadMode == PdfReadMode.Vertical
            && freshConfig.InertiaEnabled
            && freshConfig.WheelStepPixels == 120
            && freshConfig.WheelStepPixels == PdfReaderConfig.DefaultWheelStepPixels
            && freshConfig.LastFolder == "",
            $"{freshConfig.ReadMode} / {freshConfig.InertiaEnabled} / {freshConfig.WheelStepPixels} / \"{freshConfig.LastFolder}\"");

        Check("配置：工具 Id = pdf-reader（Config\\pdf-reader 与 Data\\pdf-reader 的文件夹名）",
            PdfReaderConfig.ToolId == "pdf-reader");

        Check("配置：滚轮步长生效值夹到 20~600（5→20、-100→20、9999→600、int.MaxValue→600）",
            new PdfReaderConfig { WheelStepPixels = 120 }.WheelStepEffective == 120
            && new PdfReaderConfig { WheelStepPixels = 20 }.WheelStepEffective == 20
            && new PdfReaderConfig { WheelStepPixels = 600 }.WheelStepEffective == 600
            && new PdfReaderConfig { WheelStepPixels = 5 }.WheelStepEffective == 20
            && new PdfReaderConfig { WheelStepPixels = -100 }.WheelStepEffective == 20
            && new PdfReaderConfig { WheelStepPixels = 9999 }.WheelStepEffective == 600
            && new PdfReaderConfig { WheelStepPixels = int.MaxValue }.WheelStepEffective == 600);

        Check("配置：阅读方式生效值把手改坏的值归一化（99 → 纵向、合法值不变）",
            new PdfReaderConfig().ReadModeEffective == PdfReadMode.Vertical
            && new PdfReaderConfig { ReadMode = PdfReadMode.Horizontal }.ReadModeEffective == PdfReadMode.Horizontal
            && new PdfReaderConfig { ReadMode = PdfReadMode.Spread }.ReadModeEffective == PdfReadMode.Spread
            && new PdfReaderConfig { ReadMode = (PdfReadMode)99 }.ReadModeEffective == PdfReadMode.Vertical);

        var defaultConfigJson = freshConfig.ToJson();
        Check("配置序列化：ReadMode 按 C# 原名存字符串（\"readMode\": \"Vertical\"，不是数字 0）",
            defaultConfigJson.Contains("\"readMode\": \"Vertical\"")
            && !defaultConfigJson.Contains("\"readMode\": 0")
            && !defaultConfigJson.Contains("\"readMode\": \"0\""),
            CompactJson(defaultConfigJson));

        Check("配置序列化：带 setter 的生效值属性有 [JsonIgnore]，不写进 JSON",
            !defaultConfigJson.Contains("effective", StringComparison.OrdinalIgnoreCase)
            && !defaultConfigJson.Contains("ReadModeEffective", StringComparison.Ordinal),
            CompactJson(defaultConfigJson));

        var serializedConfigPath = SelfCheckScratch.MakeFile("pdf-reader-config.json");
        var spreadConfig = new PdfReaderConfig
        {
            ReadMode = PdfReadMode.Spread,
            InertiaEnabled = false,
            WheelStepPixels = 333,
            LastFolder = @"C:\Users\test\PDFs",
        };
        _ = JsonFileStore.Write(serializedConfigPath, spreadConfig);
        var reloadedConfig = JsonFileStore.Read<PdfReaderConfig>(serializedConfigPath);
        Check("配置序列化：改过的值往返一致（方式 / 惯性 / 滚轮 / 上次文件夹）",
            reloadedConfig is not null
            && reloadedConfig.ReadMode == PdfReadMode.Spread
            && !reloadedConfig.InertiaEnabled
            && reloadedConfig.WheelStepPixels == 333
            && reloadedConfig.LastFolder == @"C:\Users\test\PDFs",
            reloadedConfig is null ? "读回为 null" : CompactJson(reloadedConfig.ToJson()));

        // 在自检临时数据根（YLT_HOME）里走一遍 ToolConfigStore 的 Load → SaveNow → 读回
        var configStore = ToolConfigStore.For("pdf-reader-check");
        var persistedConfig = configStore.Load<PdfReaderConfig>();
        Check("配置持久化：Load 后自动落盘到 Config\\pdf-reader-check\\config.json（无保存按钮）",
            File.Exists(configStore.FilePath)
            && Path.GetFileName(configStore.FilePath) == "config.json"
            && Path.GetFileName(Path.GetDirectoryName(configStore.FilePath)!) == "pdf-reader-check"
            && persistedConfig.FilePath == configStore.FilePath,
            configStore.FilePath);

        Check("配置持久化：首次 Load 的默认值正确（纵向 / 惯性开 / 滚轮 120）",
            persistedConfig.ReadModeEffective == PdfReadMode.Vertical
            && persistedConfig.InertiaEnabled
            && persistedConfig.WheelStepEffective == 120);

        persistedConfig.ReadMode = PdfReadMode.Spread;
        persistedConfig.InertiaEnabled = false;
        persistedConfig.WheelStepPixels = 333;
        persistedConfig.LastFolder = SelfCheckScratch.Root;
        persistedConfig.SaveNow();
        var persistedJson = File.Exists(configStore.FilePath) ? File.ReadAllText(configStore.FilePath) : "";
        Check("配置持久化：SaveNow 后磁盘上的 JSON 是字符串枚举 + 新值",
            persistedJson.Contains("\"readMode\": \"Spread\"")
            && persistedJson.Contains("\"inertiaEnabled\": false")
            && persistedJson.Contains("\"wheelStepPixels\": 333"),
            CompactJson(persistedJson));

        var rereadConfig = JsonFileStore.Read<PdfReaderConfig>(configStore.FilePath);
        Check("配置持久化：从磁盘读回和写入一致（含生效值夹取 333）",
            rereadConfig is not null
            && rereadConfig.ReadMode == PdfReadMode.Spread
            && !rereadConfig.InertiaEnabled
            && rereadConfig.WheelStepPixels == 333
            && rereadConfig.WheelStepEffective == 333
            && rereadConfig.LastFolder == SelfCheckScratch.Root,
            rereadConfig is null ? "读回为 null" : rereadConfig.FilePath);
        configStore.StopWatching();

        // ================================================================
        // 8. 工具展示契约（PdfReaderTool）—— 主页搜索 / 排序靠这些字段
        //    注意：不调 CreateView（构造 View 需要 WPF Application 资源）。
        // ================================================================
        var pdfTool = new PdfReaderTool();
        Check("工具契约：Id / 名称 / 分类与配置一致",
            pdfTool.Id == "pdf-reader" && pdfTool.Id == PdfReaderConfig.ToolId
            && pdfTool.Name == "PDF 阅读器" && pdfTool.Category == "媒体",
            $"{pdfTool.Id} / {pdfTool.Name} / {pdfTool.Category}");

        Check("工具契约：排序号 17（不是默认 0，主页排序不会乱）",
            pdfTool.Order == 17 && pdfTool.Order != 0);

        Check("工具契约：图标 📄、描述点明上下 / 左右 / 双页 / 惯性",
            pdfTool.EmojiIcon == "📄"
            && pdfTool.Description.Contains("PDF")
            && pdfTool.Description.Contains("上下")
            && pdfTool.Description.Contains("左右")
            && pdfTool.Description.Contains("双页")
            && pdfTool.Description.Contains("惯性"),
            pdfTool.Description);

        Check("工具契约：标签覆盖搜索关键词（PDF / 文档 / 双页 / 惯性 / 甩动）",
            pdfTool.Tags.Contains("PDF") && pdfTool.Tags.Contains("文档")
            && pdfTool.Tags.Contains("双页") && pdfTool.Tags.Contains("惯性")
            && pdfTool.Tags.Contains("甩动"));

        Check("工具契约：标签数量足够搜索命中、无空项 / 无重复 / 无前后空格",
            pdfTool.Tags.Count >= 10
            && pdfTool.Tags.All(t => t.Length > 0 && t == t.Trim())
            && pdfTool.Tags.Distinct().Count() == pdfTool.Tags.Count,
            string.Join(" / ", pdfTool.Tags));

        Check("工具契约：实现 ITool 接口（外壳靠它挂载到主页 / 类别页）",
            pdfTool is ITool);

        // ================================================================
        // 9. 演示 PDF 生成器（PdfDemoPdf）—— 真实渲染自检的数据源
        // ================================================================
        Check("演示 PDF：默认 6 页、页面 400×600 点（≈A4 竖版）",
            PdfDemoPdf.DefaultPageCount == 6
            && PdfDemoPdf.PageWidthPoints == 400
            && PdfDemoPdf.PageHeightPoints == 600);

        var demoBytes = PdfDemoPdf.Create(3);
        var demoText = Encoding.ASCII.GetString(demoBytes);
        Check("演示 PDF：真的是 PDF（%PDF-1.4 开头、%%EOF 结尾）",
            demoText.StartsWith("%PDF-1.4", StringComparison.Ordinal)
            && demoText.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal));

        Check("演示 PDF：3 页（/Count 3 且恰好 3 个 /Type /Page）",
            demoText.Contains("/Count 3", StringComparison.Ordinal)
            && CountOccurrences(demoText, "/Type /Page ") == 3,
            $"Page 对象 {CountOccurrences(demoText, "/Type /Page ")} 个");

        Check("演示 PDF：Create(0) 兜底成 1 页（不生成 0 页空文档）",
            Encoding.ASCII.GetString(PdfDemoPdf.Create(0)).Contains("/Count 1", StringComparison.Ordinal));

        var demoPdfPath = SelfCheckScratch.MakeFile("pdf-demo.pdf");
        File.WriteAllBytes(demoPdfPath, demoBytes);
        Check("演示 PDF：写入自检临时目录成功（几 KB 的小文件）",
            File.Exists(demoPdfPath) && new FileInfo(demoPdfPath).Length > 1000,
            $"{demoPdfPath}（{new FileInfo(demoPdfPath).Length} 字节）");

        // ================================================================
        // 10. 真实 PDF 会话（PdfDocumentSession）—— 打开 / 页尺寸 / 渲染宽度夹取 / 真渲染
        // ================================================================
        PdfDocumentSession? session = null;
        var openError = Catch(() => { session = PdfDocumentSession.OpenAsync(demoPdfPath).GetAwaiter().GetResult(); });
        Check("真实打开：PdfDemoPdf.Create(3) 生成的文件能被 Windows.Data.Pdf 打开",
            openError is null && session is not null,
            openError is null ? demoPdfPath : openError.ToString());

        if (session is not null)
        {
            Check("真实打开：页数 = 3、Pages 与 PageCount 一致", session.PageCount == 3 && session.Pages.Count == 3,
                $"PageCount={session.PageCount}");

            Check("真实打开：每页尺寸都合法（DIP 大于 0、不是 NaN）",
                session.Pages.All(p => p.HasValidSize && p.WidthDip > 0 && p.HeightDip > 0),
                string.Join(" / ", session.Pages.Select(p => $"{p.WidthDip:F1}×{p.HeightDip:F1}")));

            Check("真实打开：页尺寸保持 400×600 的 2:3 比例（DIP 按 96/72 放大，比例不变）",
                session.Pages.All(p => Math.Abs(p.AspectRatio - 2.0 / 3.0) <= Tolerance),
                string.Join(" / ", session.Pages.Select(p => p.AspectRatio.ToString("F4"))));

            Check("真实打开：页号从 0 连续编号",
                session.Pages.Select((p, i) => p.Index == i).All(ok => ok));

            Check("真实打开：FilePath 是完整路径（用于状态栏 / 上次文件夹）",
                session.FilePath == Path.GetFullPath(demoPdfPath), session.FilePath);

            Check("真实打开：400×600 点的页在 96 DPI 下约 533×800 DIP（排版只用比例）",
                session.Pages.All(p => Math.Abs(p.WidthDip - 400 * 96.0 / 72) <= 0.5
                                       && Math.Abs(p.HeightDip - 600 * 96.0 / 72) <= 0.5),
                $"{session.Pages[0].WidthDip:F2}×{session.Pages[0].HeightDip:F2}");

            // ---- 渲染宽度夹取（纯函数，先写死几条基准回归） ----
            Check("渲染宽度夹取常量：上限 1600 / 下限 128 / 单页像素上限 600 万",
                PdfDocumentSession.MaxRenderWidth == 1600
                && PdfDocumentSession.MinRenderWidth == 128
                && PdfDocumentSession.MaxRenderPixels == 6_000_000);

            Check("渲染宽度夹取：正常请求 800 → 800（400×600 页按 2:3 算约 800×1200 像素，远小于上限）",
                PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], 800) == 800,
                PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], 800).ToString());

            Check("渲染宽度夹取：太小夹到下限 128（传 1 / 0 / 负数都是 128）",
                PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], 1) == 128
                && PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], 0) == 128
                && PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], -100) == 128);

            Check("渲染宽度夹取：太大夹到上限 1600（传 99999 还是 1600）",
                PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], 99999) == 1600,
                PdfDocumentSession.EffectiveRenderWidth(session.Pages[0], 99999).ToString());

            var tallPage = new PdfPageInfo(0, 100, 2000);
            var tallWidth = PdfDocumentSession.EffectiveRenderWidth(tallPage, 1600);
            Check("渲染宽度夹取：极高页按 MaxRenderPixels 再缩（100×2000 页请求 1600 → 547）",
                tallWidth == 547,
                $"实际 {tallWidth}，像素约 {(long)tallWidth * (long)(tallWidth * 2000.0 / 100):N0}");

            Check("渲染宽度夹取：坏页尺寸只做上下限夹取（800 → 800、99999 → 1600）",
                PdfDocumentSession.EffectiveRenderWidth(new PdfPageInfo(0, double.NaN, 600), 800) == 800
                && PdfDocumentSession.EffectiveRenderWidth(new PdfPageInfo(0, 0, 0), 99999) == 1600);

            // ---- 真实渲染（Windows.Data.Pdf → PNG → 冻结 BitmapSource） ----
            BitmapSource? page0Bitmap = null;
            Exception? renderError = null;
            try
            {
                page0Bitmap = session.RenderPageAsync(0, 800).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                renderError = ex;
            }
            Check("真实渲染：RenderPageAsync(0, 800) 返回位图（WinRT 渲染链路没断）",
                renderError is null && page0Bitmap is not null,
                renderError is null ? "" : renderError.ToString());

            if (page0Bitmap is not null)
            {
                Check("真实渲染：位图宽 = 800（DestinationWidth 生效）",
                    page0Bitmap.PixelWidth == 800, page0Bitmap.PixelWidth.ToString());

                Check("真实渲染：位图是冻结的（可以安全交给 UI 线程显示）",
                    page0Bitmap.IsFrozen, $"IsFrozen={page0Bitmap.IsFrozen}");

                Check("真实渲染：位图高度按 2:3 比例（约 1200，不是被拉伸的方图）",
                    page0Bitmap.PixelHeight > 0
                    && Math.Abs((double)page0Bitmap.PixelWidth / page0Bitmap.PixelHeight - 2.0 / 3.0) <= 0.02,
                    $"{page0Bitmap.PixelWidth}×{page0Bitmap.PixelHeight}");

                BitmapSource? page1Bitmap = null;
                Exception? renderPage1Error = null;
                try
                {
                    page1Bitmap = session.RenderPageAsync(1, 200).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    renderPage1Error = ex;
                }
                Check("真实渲染：第 2 页也能渲染（内部串行渲染门可重复进入，不死锁）",
                    renderPage1Error is null && page1Bitmap is { PixelWidth: 200 },
                    renderPage1Error?.ToString() ?? $"PixelWidth={page1Bitmap?.PixelWidth}");

                BitmapSource? page0SmallBitmap = null;
                Exception? renderSmallError = null;
                try
                {
                    page0SmallBitmap = session.RenderPageAsync(0, 200).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    renderSmallError = ex;
                }
                Check("真实渲染：同一页可以按不同宽度重渲染（200 宽也生效）",
                    renderSmallError is null && page0SmallBitmap is { PixelWidth: 200 },
                    renderSmallError?.ToString() ?? $"PixelWidth={page0SmallBitmap?.PixelWidth}");

                if (page0SmallBitmap is not null && page1Bitmap is not null)
                {
                    var page0Color = SamplePixel(page0SmallBitmap, 0.10, 2.0 / 3.0);
                    var page1Color = SamplePixel(page1Bitmap, 0.10, 2.0 / 3.0);
                    var colorDistance = Math.Abs(page0Color.R - page1Color.R)
                                        + Math.Abs(page0Color.G - page1Color.G)
                                        + Math.Abs(page0Color.B - page1Color.B);
                    Check("真实渲染：第 1 页与第 2 页画出来的底色明显不同（不是同一张图）",
                        colorDistance > 60,
                        $"第 1 页 RGB=({page0Color.R},{page0Color.G},{page0Color.B})，" +
                        $"第 2 页 RGB=({page1Color.R},{page1Color.G},{page1Color.B})，差 {colorDistance}");

                    Check("真实渲染：第 1 页是演示 PDF 的蓝色底（B 通道明显高于 R）",
                        page0Color.B > page0Color.R + 40,
                        $"R={page0Color.R} G={page0Color.G} B={page0Color.B}");
                }
            }

            // ---- 页号越界 / 关闭后的行为 ----
            Exception? rangeError = null;
            try
            {
                session.RenderPageAsync(99, 800).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                rangeError = ex;
            }
            Check("真实渲染：渲染第 99 页抛 ArgumentOutOfRangeException",
                rangeError is ArgumentOutOfRangeException, rangeError?.GetType().Name ?? "没有抛异常");

            Exception? negativeRangeError = null;
            try
            {
                session.RenderPageAsync(-1, 800).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                negativeRangeError = ex;
            }
            Check("真实渲染：渲染第 -1 页也抛 ArgumentOutOfRangeException",
                negativeRangeError is ArgumentOutOfRangeException,
                negativeRangeError?.GetType().Name ?? "没有抛异常");

            session.Dispose();
            Exception? disposedError = null;
            try
            {
                session.RenderPageAsync(0, 800).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                disposedError = ex;
            }
            Check("关闭后：再用会话渲染抛 ObjectDisposedException（不会静默渲染已释放文档）",
                disposedError is ObjectDisposedException, disposedError?.GetType().Name ?? "没有抛异常");

            Exception? secondDisposeError = null;
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                secondDisposeError = ex;
            }
            Check("关闭后：重复 Dispose 不抛异常（可重入）",
                secondDisposeError is null, secondDisposeError?.Message ?? "");

            Check("关闭后：页数 / 页信息仍可读（纯数据，界面收尾用）",
                session.PageCount == 3 && session.Pages.Count == 3);
        }

        // ---- 打开失败的几种情况：都翻译成 PdfReaderException / 中文提示 ----
        var missingPath = SelfCheckScratch.MakeFile("missing.pdf");
        var missingError = Catch(() => PdfDocumentSession.OpenAsync(missingPath).GetAwaiter().GetResult());
        Check("打开不存在的文件：抛 PdfReaderException 并说清「文件不存在」",
            missingError is PdfReaderException && missingError.Message.Contains("不存在"),
            missingError?.Message ?? "没有抛异常");

        var emptyPathError = Catch(() => PdfDocumentSession.OpenAsync("   ").GetAwaiter().GetResult());
        Check("打开空路径：抛 PdfReaderException 并提示「没有选择 PDF 文件」",
            emptyPathError is PdfReaderException && emptyPathError.Message.Contains("没有选择"),
            emptyPathError?.Message ?? "没有抛异常");

        var directoryAsFileError = Catch(() => PdfDocumentSession.OpenAsync(SelfCheckScratch.Root).GetAwaiter().GetResult());
        Check("把文件夹当 PDF 打开：抛 PdfReaderException（不是未处理异常）",
            directoryAsFileError is PdfReaderException,
            directoryAsFileError?.Message ?? "没有抛异常");

        var corruptPath = SelfCheckScratch.MakeFile("corrupt.pdf");
        File.WriteAllText(corruptPath, "This is definitely not a PDF file.");
        var corruptError = Catch(() => PdfDocumentSession.OpenAsync(corruptPath).GetAwaiter().GetResult());
        Check("打开损坏 / 假的 PDF：抛 PdfReaderException 并以「PDF 打不开」开头（界面能直接显示）",
            corruptError is PdfReaderException
            && corruptError.Message.StartsWith("PDF 打不开", StringComparison.Ordinal),
            corruptError?.Message ?? "没有抛异常");

        // 文件对话框 / 拖进来 / 命令行启动的路径可能带引号和前后空格，OpenAsync 要能自己清理
        var spacedPdfPath = SelfCheckScratch.MakeFile("with space.pdf");
        File.WriteAllBytes(spacedPdfPath, demoBytes);
        PdfDocumentSession? quotedSession = null;
        var quotedOpenError = Catch(() =>
        {
            quotedSession = PdfDocumentSession.OpenAsync($"  \"{spacedPdfPath}\"  ").GetAwaiter().GetResult();
        });
        Check("打开路径带引号 / 前后空格 / 中间有空格：能自己清理后正常打开",
            quotedOpenError is null && quotedSession is { PageCount: 3 },
            quotedOpenError?.Message ?? $"PageCount={quotedSession?.PageCount}");
        quotedSession?.Dispose();

        Check("打开错误翻译：已知异常原样、文件不存在 / 没权限 / 其它都有中文说明",
            PdfDocumentSession.DescribeOpenError(new PdfReaderException("自定义错误")) == "自定义错误"
            && PdfDocumentSession.DescribeOpenError(new FileNotFoundException("找不到文件")).Contains("文件不存在")
            && PdfDocumentSession.DescribeOpenError(new UnauthorizedAccessException("拒绝访问")).Contains("没有权限")
            && PdfDocumentSession.DescribeOpenError(new InvalidOperationException("坏了")).StartsWith("PDF 打不开", StringComparison.Ordinal));

        // ================================================================
        // 11. 无头跑一遍 ViewModel（打开 / 换阅读方式 / 打开失败提示 / 关闭）
        //     与「图片连续阅读器」的自检同思路：界面事件没法无头跑，VM 可以。
        // ================================================================
        try
        {
            var vm = new PdfReaderViewModel();
            Check("VM 默认状态：没打开文档、页数 0、工具名 / 引导文案",
                !vm.HasDocument && vm.PageCount == 0
                && vm.FileTitleText == "PDF 阅读器"
                && vm.FilePathText.Contains("打开一个 PDF")
                && vm.LoadedHintText == "",
                $"{vm.FileTitleText} / {vm.FilePathText}");

            Check("VM 阅读方式下拉：三种选项、当前名字与配置生效值一致",
                vm.ModeOptions.Count == 3
                && vm.ModeOptions.Select(o => o.Value).Distinct().Count() == 3
                && vm.ModeOptions.Select(o => o.Value).SequenceEqual(
                    new[] { PdfReadMode.Vertical, PdfReadMode.Horizontal, PdfReadMode.Spread })
                && vm.CurrentModeText == PdfReadModes.Describe(vm.Cfg.ReadModeEffective)
                && vm.ModeHint == PdfReadModes.Hint(vm.Cfg.ReadModeEffective),
                string.Join(" / ", vm.ModeOptions.Select(o => o.Label)));

            var documentChangedCount = 0;
            vm.DocumentChanged += (_, _) => documentChangedCount++;
            vm.PdfPicker = () => demoPdfPath;
            vm.OpenPdfAsync().GetAwaiter().GetResult();
            Check("VM 打开 PDF：有文档、页数 3、标题「文件名 · 共 3 页」",
                vm.HasDocument && vm.PageCount == 3
                && vm.FileName == Path.GetFileName(demoPdfPath)
                && vm.FileTitleText.Contains("共 3 页"),
                $"{vm.FileTitleText} / {vm.FileName}");

            Check("VM 打开 PDF：状态栏是「已打开…共 3 页」、记下上次文件夹、通知界面重建",
                vm.StatusText.Contains("已打开") && vm.StatusText.Contains("共 3 页")
                && vm.Cfg.LastFolder == Path.GetDirectoryName(Path.GetFullPath(demoPdfPath))
                && documentChangedCount > 0,
                vm.StatusText);

            var modeChangedCount = 0;
            vm.ModeChanged += (_, _) => modeChangedCount++;
            var targetMode = vm.Cfg.ReadModeEffective == PdfReadMode.Spread
                ? PdfReadMode.Horizontal
                : PdfReadMode.Spread;
            vm.SelectedMode = OptionItem<PdfReadMode>.Find(vm.ModeOptions, targetMode)!;
            Check("VM 换阅读方式：写回配置、通知界面、当前文案 / 双页标记跟着变",
                vm.Cfg.ReadMode == targetMode
                && vm.CurrentModeText == PdfReadModes.Describe(targetMode)
                && vm.IsSpreadMode == (targetMode == PdfReadMode.Spread)
                && modeChangedCount > 0,
                $"{targetMode} / {vm.CurrentModeText} / IsSpreadMode={vm.IsSpreadMode}");

            vm.LoadPdfAsync(Path.Combine(SelfCheckScratch.Root, "不存在.pdf")).GetAwaiter().GetResult();
            Check("VM 打开失败：不抛异常、状态栏给中文提示、旧文档还留着",
                vm.HasDocument && vm.StatusText.Contains("文件不存在"), vm.StatusText);

            var pendingPage = vm.ConsumePendingPageIndex();
            Check("VM 跳页参数只消费一次（界面重建不会反复跳回去）",
                vm.ConsumePendingPageIndex() is null,
                pendingPage?.ToString() ?? "(null)");

            vm.NotifyClosed();
            Check("VM 关闭时释放文档（HasDocument 变 false、页数归 0）",
                !vm.HasDocument && vm.PageCount == 0);
        }
        catch (Exception ex)
        {
            Check("VM 无头流程（打开 / 换模式 / 打开失败 / 关闭）整体没抛异常", false, ex.ToString());
        }

        // ================================================================
        // 局部小工具（只在这个文件里用）
        // ================================================================

        // 捕获动作抛出的异常（都翻译成 FAIL，不让一个异常打断整组）；没抛返回 null。
        static Exception? Catch(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        // 数子串出现次数（验证演示 PDF 里恰好几页）。
        static int CountOccurrences(string text, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }
            return count;
        }

        // 取位图某个相对位置的像素（1×1 CopyPixels）；统一转 Bgra32 再读，不受解码后像素格式影响。
        static (byte R, byte G, byte B) SamplePixel(BitmapSource source, double fractionX, double fractionY)
        {
            BitmapSource readable = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var x = Math.Clamp((int)(readable.PixelWidth * fractionX), 0, readable.PixelWidth - 1);
            var y = Math.Clamp((int)(readable.PixelHeight * fractionY), 0, readable.PixelHeight - 1);
            var pixel = new byte[4];
            readable.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            return (pixel[2], pixel[1], pixel[0]); // Bgra32：B、G、R、A
        }

        // JSON 压成一行（写进 detail 时别把多行输出撑爆）。
        static string CompactJson(string json)
        {
            var oneLine = json.Replace("\r", " ").Replace("\n", " ").Trim();
            return oneLine.Length > 200 ? oneLine[..200] : oneLine;
        }
    }
}
