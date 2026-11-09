using System;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 画布横向布局：算「左侧标尺 + Note 浏览区（轨道）」在给定宽度里怎么摆。
///
/// <para><b>为什么要单独算</b>：编辑器有两套界面（窗口内 / 全屏覆盖层），并且用户可以把
/// Note 浏览区调窄（<see cref="OsuManiaEditorConfig.NoteAreaRatio"/>）。如果两边各自手算，
/// 迟早出现「窗口里居中、全屏里偏左」这种不一致；抽成纯函数后两边共用，自检也能直接断言。</para>
///
/// <para><b>居中规则</b>：浏览区比可用宽度窄时**居中**（左右留白对半分），标尺始终贴着浏览区左边 ——
/// 这样把比例调小时，整块内容往中间收，看起来像 osu! 编辑器那种窄轨道。</para>
/// </summary>
public static class LaneLayout
{
    /// <summary>左侧时间点标尺宽度（像素）。画布与渲染快照都从这里取，别再各写一份。</summary>
    public const double RulerWidth = 64;

    /// <summary>Note 浏览区宽度占比的合法范围下限（再小就没法点 note 了）。</summary>
    public const double MinRatio = 0.2;

    /// <summary>占比上限（1.0 = 铺满可用宽度）。</summary>
    public const double MaxRatio = 1.0;

    /// <summary>可用宽度再小也要给浏览区留这么多像素（否则列宽会变成 0 / 负数）。</summary>
    public const double MinLaneWidth = 40;

    /// <summary>一次布局的结果（都是相对画布左边的 X 像素）。</summary>
    /// <param name="RulerLeft">标尺左边界。</param>
    /// <param name="LaneLeft">轨道区左边界（= 标尺右边界）。</param>
    /// <param name="LaneWidth">轨道区宽度。</param>
    /// <param name="LaneRight">轨道区右边界。</param>
    public readonly record struct Result(double RulerLeft, double LaneLeft, double LaneWidth, double LaneRight)
    {
        /// <summary>轨道区左边到标尺右边的留白（也就是“左侧留白”）。</summary>
        public double LaneLeftMargin => LaneLeft - RulerLeft;

        /// <summary>轨道区右边到画布右边的留白。</summary>
        public double RightMargin(double canvasWidth) => canvasWidth - LaneRight;
    }

    /// <summary>
    /// 计算布局。
    /// </summary>
    /// <param name="canvasWidth">画布宽度（像素）。</param>
    /// <param name="rulerWidth">左侧标尺宽度（像素）。</param>
    /// <param name="ratio">Note 浏览区占「可用宽度」的比例（0.2~1.0，越界会被夹取）。</param>
    public static Result Compute(double canvasWidth, double rulerWidth, double ratio)
    {
        if (!double.IsFinite(canvasWidth) || canvasWidth <= 0)
        {
            canvasWidth = 1;
        }

        if (!double.IsFinite(rulerWidth) || rulerWidth < 0) rulerWidth = 0;

        var clampedRatio = double.IsFinite(ratio) ? Math.Clamp(ratio, MinRatio, MaxRatio) : MaxRatio;

        var available = Math.Max(MinLaneWidth, canvasWidth - rulerWidth);
        var laneWidth = Math.Max(MinLaneWidth, available * clampedRatio);

        // 「标尺 + 轨道」这一整块居中 —— 但整块是靠左的：
        // 标尺贴着轨道左边（它是时间轴的刻度，必须挨着轨道），多余的宽度全部留在**右边**。
        // ⚠ 不要左右对半分：那样左边会空出一大块、标尺孤零零飘在中间（截图里踩过）。
        // 这样调小宽度时，轨道收窄，空白落在右侧（给未来的属性面板 / 波形留位置）。
        var rulerLeft = 0;
        var laneLeft = rulerWidth;

        return new Result(rulerLeft, laneLeft, laneWidth, laneLeft + laneWidth);
    }
}
