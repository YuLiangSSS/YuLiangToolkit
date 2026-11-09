using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 框选（矩形选择框）的纯逻辑：给定「时间区间 + 列区间」，找出所有落在里面的 note。
///
/// <para><b>和 osu! 编辑器的行为对齐</b>：框选看的是 note 的**开始时间点**和**列**，
/// 而不是 LN 身体的整个跨度 —— 这样「框住一小段就能选中那个头」比较符合直觉。
/// LN 的身体可能从框上方一直延伸下来，但它只要头没落在框里就不算选中。</para>
///
/// <para><b>性能</b>：用 <see cref="NoteIndex"/> 二分取时间窗口，再在窗口里筛列，
/// 而不是全表扫（十几万 note 的谱面框选也不会掉帧）。</para>
/// </summary>
public static class MarqueeSelection
{
    /// <summary>一个矩形框（时间 + 列；列是闭区间，允许只框住部分列）。</summary>
    public readonly record struct Box(double FromTime, double ToTime, int FromColumn, int ToColumn)
    {
        /// <summary>按两个角（可能反向拖）规整。</summary>
        public static Box FromCorners(double timeA, double timeB, int columnA, int columnB)
            => new(
                Math.Min(timeA, timeB),
                Math.Max(timeA, timeB),
                Math.Min(columnA, columnB),
                Math.Max(columnA, columnB));

        /// <summary>时间跨度（用来判断框是不是太小、该当成单击）。</summary>
        public double TimeSpan => Math.Abs(ToTime - FromTime);
    }

    /// <summary>
    /// 找出框里的 note 下标。
    /// </summary>
    /// <param name="beatmap">谱面。</param>
    /// <param name="box">选择框。</param>
    /// <param name="includeLongNoteBodies">
    /// true = LN 的身体只要和框有交集就算选中（含头尾）；
    /// false = 只认「开始时间落入框内」。
    /// 默认 false（和 osu! 编辑器的框选手感一致，也更不容易误选）。
    /// </param>
    public static List<int> Find(EditorBeatmap beatmap, Box box, bool includeLongNoteBodies = false)
    {
        var result = new List<int>();
        if (beatmap.HitObjects.Count == 0) return result;

        var from = box.FromTime;
        var to = box.ToTime;
        if (to < from) (from, to) = (to, from);

        // 二分取时间窗口：LN 可能有很长的跨度，下界放宽 MaxDuration 才能不漏
        var index = beatmap.Index;
        var (start, end) = index.RangeFor(from, to);

        for (var i = start; i < end; i++)
        {
            var noteIndex = index.NoteIndexAt(i);
            if (noteIndex < 0) continue;

            var note = beatmap.HitObjects[noteIndex];
            if (note.Column < box.FromColumn || note.Column > box.ToColumn) continue;

            if (includeLongNoteBodies && note.IsLongNote)
            {
                // LN 身体和框有交集即可（EndTime ≥ from 且 StartTime ≤ to）
                if (note.EndTime >= from && note.StartTime <= to) result.Add(noteIndex);
                continue;
            }

            if (note.StartTime < from || note.StartTime > to) continue;
            result.Add(noteIndex);
        }

        return result;
    }
}
