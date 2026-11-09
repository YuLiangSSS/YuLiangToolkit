using System;
using System.Collections.Generic;
using YuLiangToolkit.Tools.OsuMania.Core.Editor;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 一帧要画的东西（**不可变快照**）。画布只读它，不碰 ViewModel / EditorBeatmap。
///
/// <para><b>为什么抽一层快照</b>：绘制发生在 <c>OnRender</c> 里，直接读实时模型会让
/// 「拖动中模型正在变」和「WPF 异步重绘」打架；而且快照让渲染层能完全脱离 WPF 之外做断言。</para>
///
/// <para><b>可见 note 只在创建快照时算一次**：画布拿到的是已经裁剪 + 抽稀好的列表，
/// 每帧绘制只做坐标变换与画矩形，10 万 note 的谱面也只看得到几百个。</para>
/// </summary>
public sealed class ChartRenderState
{
    /// <summary>要画的 note（已按可见区间裁剪）。</summary>
    public IReadOnlyList<VisibleNote> Notes { get; init; } = Array.Empty<VisibleNote>();

    /// <summary>要画的网格线（已抽稀）。</summary>
    public IReadOnlyList<BeatGrid.GridLine> GridLines { get; init; } = Array.Empty<BeatGrid.GridLine>();

    /// <summary>要画的时间点（已裁剪）。</summary>
    public IReadOnlyList<VisibleTimingPoint> TimingPoints { get; init; } = Array.Empty<VisibleTimingPoint>();

    /// <summary>视口顶部时间（毫秒）。</summary>
    public double StartTime { get; init; }

    /// <summary>视口底部时间（毫秒）。</summary>
    public double EndTime { get; init; }

    /// <summary>每毫秒像素。</summary>
    public double PxPerMs { get; init; } = ChartViewport.DefaultPxPerMs;

    /// <summary>轨道区左边距。</summary>
    public double LaneLeft { get; init; }

    /// <summary>轨道区宽度。</summary>
    public double LaneWidth { get; init; }

    /// <summary>键数。</summary>
    public int KeyCount { get; init; } = 4;

    /// <summary>播放头时间（毫秒）。</summary>
    public double PlayheadMs { get; init; }

    /// <summary>是否显示播放头。</summary>
    public bool ShowPlayhead { get; init; } = true;

    /// <summary>正在创建的长条预览（松手前）。</summary>
    public (bool HasValue, int Column, double StartTime, double EndTime) CreatePreview { get; init; }

    /// <summary>当前选中的 note 下标在 <see cref="Notes"/> 里的位置（-1 = 没选 / 不在视野内）。</summary>
    public int SelectedSlot { get; init; } = -1;

    /// <summary>正在拖动的 note 在 <see cref="Notes"/> 里的位置（-1 = 没在拖）。</summary>
    public int DraggingSlot { get; init; } = -1;

    /// <summary>列宽（像素）。</summary>
    public double ColumnWidth => KeyCount > 0 ? LaneWidth / KeyCount : LaneWidth;

    /// <summary>note 总数（画布上显示统计用）。</summary>
    public int TotalNoteCount { get; init; }

    /// <summary>是否在拖动（拖动时不做播放跟随）。</summary>
    public bool IsDragging { get; init; }

    /// <summary>
    /// **从模型构建一帧**：这是「大量 note 也能流畅」的关键 ——
    /// 用 <see cref="NoteIndex"/> 二分取窗口，而不是遍历整个列表。
    /// </summary>
    /// <param name="editor">编辑器状态。</param>
    /// <param name="contentHeight">画布内容区高度（不含标尺）。</param>
    /// <param name="laneLeft">轨道区左边距。</param>
    /// <param name="laneWidth">轨道区宽度。</param>
    /// <param name="maxNotes">可见 note 的安全上限（超过就按时间抽稀，防止极端缩放下爆量）。</param>
    public static ChartRenderState Build(
        ChartEditor editor, double contentHeight, double laneLeft, double laneWidth, int maxNotes = 12000)
    {
        var beatmap = editor.Beatmap;
        var viewport = editor.Viewport;

        var from = viewport.StartTime;
        var to = viewport.StartTime + viewport.VisibleMs;

        var notes = new List<VisibleNote>(Math.Min(maxNotes, 512));
        var index = beatmap.Index;
        var (start, end) = index.RangeFor(from, to);

        var selected = editor.SelectedIndex;
        var dragging = editor.DraggingIndex;
        var selectedSlot = -1;
        var draggingSlot = -1;

        // 可见 note 上限：超了就在时间上均匀抽稀（宁可少画几个也不能卡死 UI）
        var windowCount = Math.Max(0, end - start);
        var stride = windowCount > maxNotes ? windowCount / maxNotes + 1 : 1;
        var slot = 0;

        for (var i = start; i < end; i += stride)
        {
            var noteIndex = index.NoteIndexAt(i);
            if (noteIndex < 0) continue;

            var note = beatmap.HitObjects[noteIndex];

            // 精确裁剪：普通键只看头；LN 看区间是否与视野相交
            var noteEnd = note.IsLongNote ? Math.Max(note.EndTime, note.StartTime) : note.StartTime;
            if (noteEnd < from || note.StartTime > to) continue;

            if (noteIndex == selected) selectedSlot = slot;
            if (noteIndex == dragging) draggingSlot = slot;

            notes.Add(new VisibleNote(note, noteIndex));
            slot++;
        }

        // 网格线（内部已抽稀）
        var gridLines = BeatGrid.Lines(beatmap.RedLines, from, to, editor.GridDivisor);

        // 时间点（通常只有几十条，直接筛）
        var timingPoints = new List<VisibleTimingPoint>(8);
        foreach (var point in beatmap.TimingPoints)
        {
            // 绿线可能紧贴在红线前后，给一点余量
            if (point.Time < from - 1000 || point.Time > to + 1000) continue;
            timingPoints.Add(new VisibleTimingPoint(point));
        }
        timingPoints.Sort((a, b) => a.Point.Time.CompareTo(b.Point.Time));

        return new ChartRenderState
        {
            Notes = notes,
            GridLines = gridLines,
            TimingPoints = timingPoints,
            StartTime = from,
            EndTime = to,
            PxPerMs = viewport.PxPerMs,
            LaneLeft = laneLeft,
            LaneWidth = laneWidth,
            KeyCount = beatmap.KeyCount,
            PlayheadMs = editor.PlayheadMs,
            ShowPlayhead = true,
            CreatePreview = editor.CreatePreview,
            SelectedSlot = selectedSlot,
            DraggingSlot = draggingSlot,
            TotalNoteCount = beatmap.HitObjects.Count,
            IsDragging = editor.IsDragging,
        };
    }
}

/// <summary>一个可见 note（值 + 它在模型里的下标，命中 / 选中高亮用）。</summary>
public readonly record struct VisibleNote(EditorNote Note, int Index);

/// <summary>一个可见时间点。</summary>
public readonly record struct VisibleTimingPoint(EditorTimingPoint Point)
{
    /// <summary>是不是红线（画法不同）。</summary>
    public bool IsRed => Point.Uninherited;
}
