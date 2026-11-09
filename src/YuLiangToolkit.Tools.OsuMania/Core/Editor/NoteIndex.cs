using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 谱面时间轴查询用的**按时间排序的索引**（渲染 / 命中测试都靠它，是全工具性能最要紧的一块）。
///
/// <para><b>为什么要它</b>：一张 8K 谱面动辄几万到十几万 note。如果每帧都把全部 note 过一遍算
/// 「哪些在屏幕里」，10 万 note × 60fps = 每秒 600 万次无效计算，界面直接卡死。
/// 这里维护「按开始时间升序」的下标数组 + 二分，每帧只取视口对应的一小段。</para>
///
/// <para><b>LN 的可见性</b>：LN 的身体跨越一段时间，可能「头在屏幕外、身体还在屏幕里」。
/// 所以取可见集合时不能只按开始时间：本类额外维护
/// <see cref="MaxDuration"/>（全谱最长 LN 长度），查询时把下界放宽这么多 ——
/// 于是任何「结束时间 ≥ 视野起点」的 note 都会落进窗口里。</para>
///
/// <para><b>线程</b>：只在 UI 线程读写（编辑器所有改动都在 UI 线程），不加锁。</para>
/// </summary>
public sealed class NoteIndex
{
    private readonly List<EditorNote> _notes;
    private int[] _orderByStart = Array.Empty<int>();
    private double[] _starts = Array.Empty<double>();

    /// <summary>用底层 note 列表建索引（列表本身由 <see cref="EditorBeatmap"/> 持有）。</summary>
    public NoteIndex(List<EditorNote> notes)
    {
        _notes = notes ?? throw new ArgumentNullException(nameof(notes));
        Rebuild();
    }

    /// <summary>note 总数。</summary>
    public int Count => _notes.Count;

    /// <summary>全谱最长的 LN 长度（毫秒；没有 LN 时为 0）。</summary>
    public double MaxDuration { get; private set; }

    /// <summary>最早 / 最晚的时间（空谱为 0）。</summary>
    public double FirstTime => _notes.Count == 0 ? 0 : _starts[0];

    /// <summary>最晚的时间（含 LN 尾巴；空谱为 0）。</summary>
    public double LastTime { get; private set; }

    /// <summary>底层列表变了以后重建索引（**必须调用**，否则二分会对不上）。</summary>
    public void Rebuild()
    {
        var count = _notes.Count;
        if (_orderByStart.Length != count)
        {
            _orderByStart = new int[count];
            _starts = new double[count];
        }

        double maxDuration = 0;
        double lastTime = 0;

        for (int i = 0; i < count; i++)
        {
            _orderByStart[i] = i;
            _starts[i] = _notes[i].StartTime;
            var end = _notes[i].EndTime;
            if (_notes[i].IsLongNote)
            {
                var duration = end - _notes[i].StartTime;
                if (duration > maxDuration) maxDuration = duration;
                if (end > lastTime) lastTime = end;
            }
            if (_notes[i].StartTime > lastTime) lastTime = _notes[i].StartTime;
        }

        // 开始时间升序；相同时间按列号（保证每次渲染顺序稳定，避免闪烁）
        Array.Sort(_orderByStart, (a, b) =>
        {
            var cmp = _starts[a].CompareTo(_starts[b]);
            return cmp != 0 ? cmp : _notes[a].Column.CompareTo(_notes[b].Column);
        });

        // 排序后 _starts 也要按新顺序排好，二分才能直接用
        var sortedStarts = new double[count];
        for (int i = 0; i < count; i++) sortedStarts[i] = _notes[_orderByStart[i]].StartTime;
        _starts = sortedStarts;

        MaxDuration = maxDuration;
        LastTime = lastTime;
    }

    /// <summary>
    /// 取出「可能落在 [<paramref name="fromTime"/>, <paramref name="toTime"/>] 里」的 note 下标区间
    /// （左闭右开，返回的是 <see cref="_orderByStart"/> 里的位置）。
    ///
    /// <para>下界按 <see cref="MaxDuration"/> 放宽（LN 头可能在视野之上）；上界按开始时间。
    /// 调用方拿到区间后自己再按「实际矩形是否相交」精确裁剪。</para>
    /// </summary>
    public (int Start, int End) RangeFor(double fromTime, double toTime)
    {
        if (_starts.Length == 0) return (0, 0);

        var looseFrom = fromTime - MaxDuration;
        var start = LowerBound(looseFrom);
        var end = LowerBound(toTime);
        if (end < start) end = start;
        return (start, end);
    }

    /// <summary>时间 <= <paramref name="time"/> 的最后一个 note 的位置（命中测试 / 导航用）。</summary>
    public int IndexAtOrBefore(double time)
    {
        var index = LowerBound(time + 0.0001) - 1;
        return index < 0 ? -1 : index;
    }

    /// <summary>第 <paramref name="position"/> 个（按开始时间排序）note 的下标；越界返回 -1。</summary>
    public int NoteIndexAt(int position)
        => position < 0 || position >= _orderByStart.Length ? -1 : _orderByStart[position];

    /// <summary>按开始时间排序后，第 <paramref name="position"/> 个位置的时间。</summary>
    public double StartTimeAt(int position)
        => position < 0 || position >= _starts.Length ? 0 : _starts[position];

    /// <summary>第一个开始时间严格大于 <paramref name="time"/> 的位置（可见窗口的上界）。</summary>
    private int LowerBound(double time)
    {
        int lo = 0, hi = _starts.Length;
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (_starts[mid] < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
