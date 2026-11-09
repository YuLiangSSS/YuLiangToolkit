using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 节拍网格：把「毫秒时间」换算成「第几拍 / 第几格」，再吸附回去。
///
/// <para><b>红线是分段线性的</b>：两条红线之间 BPM 不变，节拍位置 = (t − 红线时间) / BeatLength。
/// 所以吸附必须先二分找到 t 落在哪一段，再在段内取整 —— 不能拿「全场第一个红线」一把算，
/// 否则中期变速的谱面会吸附到完全错的位置。</para>
///
/// <para><b>吸附精度</b>：结果四舍五入到 0.001 ms（浮点噪声会让「1/4 拍」算出 123.99999999，
/// 写回文件时差 1ms 就是可感知的错位）。</para>
///
/// <para>本类是纯函数式工具（无状态、只读快照），自检可以直接断言。</para>
/// </summary>
public static class BeatGrid
{
    /// <summary>吸附精度（毫秒）。</summary>
    public const double Rounding = 0.001;

    /// <summary>
    /// 取 <paramref name="time"/> 时刻生效的红线下标（<paramref name="redLines"/> 必须按时间升序、
    /// 且只含红线）。没有红线返回 -1；时间早于第一条返回 0。
    /// </summary>
    public static int RedLineIndexAt(IReadOnlyList<EditorTimingPoint> redLines, double time)
    {
        if (redLines.Count == 0) return -1;

        int lo = 0, hi = redLines.Count - 1, found = 0;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (redLines[mid].Time <= time)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }

    /// <summary>
    /// 把时间吸附到「红线 + <paramref name="divisor"/> 分拍」的网格上。
    ///
    /// <para><paramref name="divisor"/> 是「每拍几格」（osu! 的 Beat Divisor）：1 = 整拍、4 = 1/4 拍、
    /// 8 = 1/8 拍。传 0 或负数 = 不吸附，原样返回。</para>
    /// </summary>
    /// <param name="redLines">按时间升序的红线（空 = 不吸附）。</param>
    /// <param name="time">要吸附的时间（毫秒）。</param>
    /// <param name="divisor">每拍格数（1~16 常用）。</param>
    /// <param name="allowFractional">是否允许小数毫秒（false 时结果取整，与 .osu 原文一致）。</param>
    public static double Snap(IReadOnlyList<EditorTimingPoint> redLines, double time, int divisor, bool allowFractional = true)
    {
        if (divisor <= 0 || redLines.Count == 0) return time;

        var index = RedLineIndexAt(redLines, time);
        if (index < 0) return time;

        var line = redLines[index];
        var beatLength = line.BeatLength;
        if (beatLength <= 0 || !double.IsFinite(beatLength)) return time;

        var step = beatLength / divisor;
        if (step <= 0) return time;

        var offset = (time - line.Time) / step;
        var snapped = line.Time + Math.Round(offset) * step;

        return allowFractional
            ? Math.Round(snapped / Rounding) * Rounding
            : Math.Round(snapped);
    }

    /// <summary>
    /// 网格线时间列表（给渲染画小节线 / 拍线用）：返回落在 [from, to] 里的所有网格时间。
    ///
    /// <para>按「每小节第一步」再按小节内步骤展开，保证密集网格（1/16 拍）也只生成可见的那些。</para>
    /// </summary>
    /// <param name="redLines">按时间升序的红线。</param>
    /// <param name="from">可见起点（毫秒）。</param>
    /// <param name="to">可见终点（毫秒）。</param>
    /// <param name="divisor">每拍格数。</param>
    /// <param name="maxLines">安全上限（防止极端参数下生成几百万条，超了就按比例抽稀）。</param>
    public static List<GridLine> Lines(
        IReadOnlyList<EditorTimingPoint> redLines, double from, double to, int divisor, int maxLines = 4000)
    {
        var result = new List<GridLine>();
        if (redLines.Count == 0 || to <= from || divisor <= 0) return result;

        var index = RedLineIndexAt(redLines, from);
        if (index < 0) index = 0;

        // 从 from 所在的段开始，逐段往后铺
        for (var i = index; i < redLines.Count && !(result.Count > maxLines * 4); i++)
        {
            var line = redLines[i];
            var beatLength = line.BeatLength;
            if (beatLength <= 0 || !double.IsFinite(beatLength)) continue;

            var segmentStart = line.Time;
            var segmentEnd = i + 1 < redLines.Count ? redLines[i + 1].Time : double.PositiveInfinity;

            var step = beatLength / divisor;
            var signature = Math.Max(1, line.TimeSignature);
            var stepsPerBar = signature * divisor;

            // 段内第一个落在 [from, to] 里的步骤（四舍五入到整步，避免浮点误差累积）
            var firstStep = (long)Math.Ceiling((from - segmentStart) / step);
            if (firstStep < 0) firstStep = 0;

            for (var k = firstStep; ; k++)
            {
                var t = segmentStart + k * step;
                if (t > to) break;
                if (t >= segmentEnd) break;

                // 是不是小节线 / 拍线：按段内步数取模（用 long 避免大数下取模失真）
                var inBar = k % stepsPerBar;
                var kind = inBar == 0
                    ? GridLineKind.Bar
                    : (k % divisor == 0 ? GridLineKind.Beat : GridLineKind.Sub);
                result.Add(new GridLine(t, kind));

                if (result.Count > maxLines * 4) break;
            }

            if (segmentStart >= to) break;
            if (double.IsPositiveInfinity(segmentEnd)) break;
        }

        // 超上限时按比例抽稀（保留小节线，优先丢细分线）
        if (result.Count > maxLines)
        {
            var thinned = new List<GridLine>(maxLines);
            var keepEvery = (int)Math.Ceiling(result.Count / (double)maxLines);
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i].Kind != GridLineKind.Sub || i % keepEvery == 0) thinned.Add(result[i]);
            }
            if (thinned.Count > maxLines)
            {
                // 还是太多（说明小节线本身就过密）：均匀抽
                var stride = (int)Math.Ceiling(thinned.Count / (double)maxLines);
                var final = new List<GridLine>(maxLines);
                for (var i = 0; i < thinned.Count; i += stride) final.Add(thinned[i]);
                return final;
            }
            return thinned;
        }

        return result;
    }

    /// <summary>网格线种类（画多亮用）。</summary>
    public enum GridLineKind
    {
        /// <summary>小节线（1 整小节）。</summary>
        Bar = 0,

        /// <summary>拍线（整拍）。</summary>
        Beat = 1,

        /// <summary>细分线（1/2、1/4、1/8 拍…）。</summary>
        Sub = 2,
    }

    /// <summary>一条网格线（时间 + 种类）。</summary>
    public readonly record struct GridLine(double Time, GridLineKind Kind);
}
