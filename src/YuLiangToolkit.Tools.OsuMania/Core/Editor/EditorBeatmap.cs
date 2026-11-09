using System;
using System.Collections.Generic;
using System.Linq;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 编辑器的工作模型：一张 mania 谱面的**全部可编辑内容** + 撤销栈 + 查询索引。
///
/// <para><b>职责边界</b>：这里只做「数据 + 编辑操作 + 撤销」，不碰 WPF、不碰音频。
/// 界面层通过它改谱面，改完刷新 <see cref="Index"/> 再重绘。因此整层都能无头自检。</para>
///
/// <para><b>撤销栈</b>：存的是「操作前 / 操作后的 note 数组快照差异」而不是整份谱面拷贝 ——
/// 10 万 note 全量深拷贝一次约 4 MB，连续拖动会瞬间吃光内存。这里用
/// <see cref="EditAction"/> 记录被改 / 增 / 删的少量条目，撤销时精确回滚。</para>
///
/// <para><b>调试开关</b>：所有改动都会把 <see cref="IsDirty"/> 置 true（界面据此提示未保存）。</para>
/// </summary>
public sealed class EditorBeatmap
{
    // ---------------------------------------------------------------- 原始文件保真用

    /// <summary>原文件的全部行（保存时按行重建，只有两段正文会被替换）。</summary>
    public List<string> OriginalLines { get; } = new();

    /// <summary>[TimingPoints] 段标题行的下一行（0 基）。没有该段时为 -1。</summary>
    public int TimingSectionStart { get; set; } = -1;

    /// <summary>[TimingPoints] 段结束（下一段标题行；文件末尾时为行数）。</summary>
    public int TimingSectionEnd { get; set; } = -1;

    /// <summary>[HitObjects] 段标题行的下一行。</summary>
    public int ObjectsSectionStart { get; set; } = -1;

    /// <summary>[HitObjects] 段结束。</summary>
    public int ObjectsSectionEnd { get; set; } = -1;

    // ---------------------------------------------------------------- 元数据（原样保留 / 可改）

    /// <summary>[General] 键值。</summary>
    public Dictionary<string, string> General { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>[Metadata] 键值。</summary>
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>[Difficulty] 键值。</summary>
    public Dictionary<string, string> Difficulty { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>[Editor] 键值。</summary>
    public Dictionary<string, string> Editor { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>其它段落的键值（只读收集，写回时原样保留原行）。</summary>
    public SectionKeyValueStore OtherSections { get; } = new();

    /// <summary>加载时的警告（坏行 / 非 mania 等），界面上如实显示。</summary>
    public List<string> LoadWarnings { get; } = new();

    // ---------------------------------------------------------------- 谱面内容

    /// <summary>时间点（红 / 绿线），未排序也允许（查询前统一排序一次）。</summary>
    public List<EditorTimingPoint> TimingPoints { get; } = new();

    /// <summary>打击物。**编辑期间顺序不保证**（新增 / 删除都是 O(1) 追加 / 交换删除）。</summary>
    public List<EditorNote> HitObjects { get; } = new();

    /// <summary>键数（1~10）。</summary>
    public int KeyCount { get; set; } = 4;

    /// <summary>[General] Mode（mania = 3）。</summary>
    public int ModeId { get; set; }

    /// <summary>原文件完整路径。</summary>
    public string FilePath { get; set; } = "";

    /// <summary>是否有未保存改动。</summary>
    public bool IsDirty { get; set; }

    // ---------------------------------------------------------------- 派生状态

    private NoteIndex? _index;
    private EditorTimingPoint[]? _sortedRedLines;
    private bool _redLinesDirty = true;

    /// <summary>note 时间索引（渲染 / 命中测试用；改完 note 要 <see cref="InvalidateIndex"/>）。</summary>
    public NoteIndex Index => _index ??= new NoteIndex(HitObjects);

    /// <summary>按时间升序的红线快照（吸附 / 网格用）。会缓存，时间点改动后自动重建。</summary>
    public IReadOnlyList<EditorTimingPoint> RedLines
    {
        get
        {
            EnsureRedLines();
            return _sortedRedLines!;
        }
    }

    private void EnsureRedLines()
    {
        if (!_redLinesDirty && _sortedRedLines is not null) return;

        _sortedRedLines = TimingPoints
            .Where(tp => tp.Uninherited)
            .OrderBy(tp => tp.Time)
            .ToArray();
        _redLinesDirty = false;
    }

    /// <summary>note 集合变了：索引失效（下次访问 <see cref="Index"/> 重建）。</summary>
    public void InvalidateIndex() => _index = null;

    /// <summary>时间点集合变了：红线缓存失效。</summary>
    public void InvalidateTimingPoints()
    {
        _redLinesDirty = true;
        _sortedRedLines = null;
    }

    /// <summary>加载 / 批量改动后统一刷新一次（自检与界面都用）。</summary>
    public void RefreshIndexes()
    {
        InvalidateIndex();
        InvalidateTimingPoints();
        _ = Index;
        EnsureRedLines();
    }

    /// <summary>按时间升序的 note 快照（保存 / 统计用；不改变底层顺序）。</summary>
    public IEnumerable<EditorNote> SortedNotes()
        => HitObjects.OrderBy(n => n.StartTime).ThenBy(n => n.Column);

    /// <summary>按时间升序的时间点快照（保存用）。</summary>
    public IEnumerable<EditorTimingPoint> SortedTimingPoints()
        => TimingPoints.OrderBy(tp => tp.Time);

    /// <summary>谱面时间范围（含 LN 尾巴；空谱返回 0~0）。</summary>
    public (double First, double Last) TimeRange()
        => HitObjects.Count == 0 ? (0, 0) : (Index.FirstTime, Math.Max(Index.LastTime, Index.FirstTime));

    /// <summary>当前生效的 BPM（<paramref name="time"/> 时刻的红线换算；没有红线返回 0）。</summary>
    public double BpmAt(double time)
    {
        var reds = RedLines;
        var index = BeatGrid.RedLineIndexAt(reds, time);
        return index < 0 ? 0 : reds[index].Bpm;
    }

    /// <summary>
    /// 把时间吸附到当前网格；没有红线 / <paramref name="divisor"/> ≤ 0 时原样返回。
    /// </summary>
    public double Snap(double time, int divisor) => BeatGrid.Snap(RedLines, time, divisor);

    /// <summary>
    /// 该列在 <paramref name="time"/> 附近是否已经有 note（同一时刻 ± <paramref name="toleranceMs"/>）。
    /// 用于摆放时避免叠出一堆完全重合的 note。
    /// </summary>
    public bool HasNoteNear(int column, double time, double toleranceMs = 1.0)
    {
        var (start, end) = Index.RangeFor(time - toleranceMs, time + toleranceMs);
        for (var i = start; i < end; i++)
        {
            var index = Index.NoteIndexAt(i);
            if (index < 0) continue;
            var note = HitObjects[index];
            if (note.Column != column) continue;
            if (Math.Abs(note.StartTime - time) <= toleranceMs) return true;
        }
        return false;
    }

    /// <summary>
    /// 命中测试：给定时间与列，找出该处最应该被点到的 note（返回在 <see cref="HitObjects"/> 里的下标）。
    ///
    /// <para>优先级：① 长条身体（尾在下面也算，方便点尾巴）；② 头在该时刻附近；③ 普通键。
    /// 都按「时间距离最近」再挑一个。</para>
    /// </summary>
    public int HitTest(int column, double time, double toleranceMs)
    {
        var best = -1;
        var bestScore = double.MaxValue;

        var (start, end) = Index.RangeFor(time - toleranceMs, time + toleranceMs);
        for (var i = start; i < end; i++)
        {
            var index = Index.NoteIndexAt(i);
            if (index < 0) continue;

            var note = HitObjects[index];
            if (note.Column != column) continue;

            // 落在 LN 身体里（含两端容差）→ 最高优先级
            if (note.IsLongNote &&
                time >= note.StartTime - toleranceMs &&
                time <= note.EndTime + toleranceMs)
            {
                var score = note.HasLength ? 0 : 0.5;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = index;
                }
                continue;
            }

            var distance = Math.Abs(note.StartTime - time);
            if (distance <= toleranceMs && distance < bestScore)
            {
                bestScore = distance;
                best = index;
            }
        }

        return best;
    }

    // ================================================================
    // 编辑操作（每一次都会压栈，可撤销）
    // ================================================================

    private readonly Stack<EditAction> _undo = new();
    private readonly Stack<EditAction> _redo = new();

    /// <summary>可以撤销的步数。</summary>
    public int UndoCount => _undo.Count;

    /// <summary>可以重做的步数。</summary>
    public int RedoCount => _redo.Count;

    /// <summary>清空撤销栈（打开新谱面时调）。</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>新增一个 note（返回它在列表里的下标；失败返回 -1）。</summary>
    public int AddNote(EditorNote note)
    {
        if (note.EndTime < note.StartTime) note = note with { EndTime = note.StartTime };

        var index = HitObjects.Count;
        HitObjects.Add(note);
        InvalidateIndex();
        PushAction(EditAction.Added(index, note));
        IsDirty = true;
        return index;
    }

    /// <summary>删除一个 note（按下标）。</summary>
    public bool RemoveNoteAt(int index)
    {
        if (index < 0 || index >= HitObjects.Count) return false;

        var removed = HitObjects[index];
        HitObjects.RemoveAt(index);
        InvalidateIndex();
        PushAction(EditAction.Removed(index, removed));
        IsDirty = true;
        return true;
    }

    /// <summary>替换一个 note（移动 / 拉长 / 改列都走它）。</summary>
    public bool ReplaceNote(int index, EditorNote note)
    {
        if (index < 0 || index >= HitObjects.Count) return false;
        var before = HitObjects[index];
        if (before.Equals(note)) return false;

        if (note.EndTime < note.StartTime) note = note with { EndTime = note.StartTime };

        HitObjects[index] = note;
        InvalidateIndex();
        PushAction(EditAction.Replaced(index, before, note));
        IsDirty = true;
        return true;
    }

    /// <summary>
    /// 一次拖动确认后，把「拖动前 → 拖动后」的多条改动合成**一个撤销步骤**
    /// （拖动过程中只预览、不压栈；松手才提交，撤销一次回到拖动前）。
    /// </summary>
    public void CommitBatch(IReadOnlyList<NoteEdit> edits)
    {
        if (edits.Count == 0) return;

        var applied = new List<NoteEdit>(edits.Count);
        foreach (var edit in edits)
        {
            if (edit.Index < 0 || edit.Index >= HitObjects.Count) continue;
            HitObjects[edit.Index] = edit.After;
            applied.Add(edit);
        }

        if (applied.Count == 0) return;

        InvalidateIndex();
        PushAction(EditAction.Batch(applied));
        IsDirty = true;
    }

    /// <summary>时间点：新增 / 修改 / 删除（都各自成为一个撤销步骤）。</summary>
    public void AddTimingPoint(EditorTimingPoint point)
    {
        var index = TimingPoints.Count;
        TimingPoints.Add(point);
        InvalidateTimingPoints();
        PushAction(EditAction.TimingAdded(index, point));
        IsDirty = true;
    }

    /// <summary>改时间点。</summary>
    public bool ReplaceTimingPoint(int index, EditorTimingPoint point)
    {
        if (index < 0 || index >= TimingPoints.Count) return false;
        var before = TimingPoints[index];
        if (before.Equals(point)) return false;

        TimingPoints[index] = point;
        InvalidateTimingPoints();
        PushAction(EditAction.TimingReplaced(index, before, point));
        IsDirty = true;
        return true;
    }

    /// <summary>删时间点。</summary>
    public bool RemoveTimingPointAt(int index)
    {
        if (index < 0 || index >= TimingPoints.Count) return false;

        var removed = TimingPoints[index];
        TimingPoints.RemoveAt(index);
        InvalidateTimingPoints();
        PushAction(EditAction.TimingRemoved(index, removed));
        IsDirty = true;
        return true;
    }

    /// <summary>撤销一步；没有可撤销的返回 false。</summary>
    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var action = _undo.Pop();
        action.Undo(this);
        _redo.Push(action);
        InvalidateIndex();
        InvalidateTimingPoints();
        IsDirty = true;
        return true;
    }

    /// <summary>重做一步。</summary>
    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var action = _redo.Pop();
        action.Redo(this);
        _undo.Push(action);
        InvalidateIndex();
        InvalidateTimingPoints();
        IsDirty = true;
        return true;
    }

    private void PushAction(EditAction action)
    {
        _undo.Push(action);
        _redo.Clear();   // 新操作让重做失效（标准行为）
    }

    /// <summary>一条「按下标改 note」的记录（拖动 / 批量用）。</summary>
    public readonly record struct NoteEdit(int Index, EditorNote Before, EditorNote After);

    /// <summary>撤销栈里的一步。</summary>
    private sealed class EditAction
    {
        private readonly Kind _kind;
        private readonly int _index;
        private readonly EditorNote _noteBefore;
        private readonly EditorNote _noteAfter;
        private readonly EditorTimingPoint _tpBefore;
        private readonly EditorTimingPoint _tpAfter;
        private readonly IReadOnlyList<NoteEdit>? _batch;

        private enum Kind
        {
            NoteAdded,
            NoteRemoved,
            NoteReplaced,
            NoteBatch,
            TimingAdded,
            TimingReplaced,
            TimingRemoved,
        }

        private EditAction(Kind kind, int index, EditorNote before, EditorNote after,
            EditorTimingPoint tpBefore, EditorTimingPoint tpAfter, IReadOnlyList<NoteEdit>? batch)
        {
            _kind = kind;
            _index = index;
            _noteBefore = before;
            _noteAfter = after;
            _tpBefore = tpBefore;
            _tpAfter = tpAfter;
            _batch = batch;
        }

        public static EditAction Added(int index, EditorNote note)
            => new(Kind.NoteAdded, index, note, note, default, default, null);

        public static EditAction Removed(int index, EditorNote note)
            => new(Kind.NoteRemoved, index, note, note, default, default, null);

        public static EditAction Replaced(int index, EditorNote before, EditorNote after)
            => new(Kind.NoteReplaced, index, before, after, default, default, null);

        public static EditAction Batch(IReadOnlyList<NoteEdit> edits)
            => new(Kind.NoteBatch, -1, default, default, default, default, edits);

        public static EditAction TimingAdded(int index, EditorTimingPoint point)
            => new(Kind.TimingAdded, index, default, default, point, point, null);

        public static EditAction TimingReplaced(int index, EditorTimingPoint before, EditorTimingPoint after)
            => new(Kind.TimingReplaced, index, default, default, before, after, null);

        public static EditAction TimingRemoved(int index, EditorTimingPoint point)
            => new(Kind.TimingRemoved, index, default, default, point, point, null);

        public void Undo(EditorBeatmap map)
        {
            switch (_kind)
            {
                case Kind.NoteAdded:
                    if (_index >= 0 && _index < map.HitObjects.Count) map.HitObjects.RemoveAt(_index);
                    break;
                case Kind.NoteRemoved:
                    map.HitObjects.Insert(Math.Clamp(_index, 0, map.HitObjects.Count), _noteBefore);
                    break;
                case Kind.NoteReplaced:
                    if (_index >= 0 && _index < map.HitObjects.Count) map.HitObjects[_index] = _noteBefore;
                    break;
                case Kind.NoteBatch:
                    if (_batch is not null)
                    {
                        foreach (var edit in _batch)
                        {
                            if (edit.Index >= 0 && edit.Index < map.HitObjects.Count)
                            {
                                map.HitObjects[edit.Index] = edit.Before;
                            }
                        }
                    }
                    break;
                case Kind.TimingAdded:
                    if (_index >= 0 && _index < map.TimingPoints.Count) map.TimingPoints.RemoveAt(_index);
                    break;
                case Kind.TimingRemoved:
                    map.TimingPoints.Insert(Math.Clamp(_index, 0, map.TimingPoints.Count), _tpBefore);
                    break;
                case Kind.TimingReplaced:
                    if (_index >= 0 && _index < map.TimingPoints.Count) map.TimingPoints[_index] = _tpBefore;
                    break;
            }
        }

        public void Redo(EditorBeatmap map)
        {
            switch (_kind)
            {
                case Kind.NoteAdded:
                    map.HitObjects.Insert(Math.Clamp(_index, 0, map.HitObjects.Count), _noteAfter);
                    break;
                case Kind.NoteRemoved:
                    if (_index >= 0 && _index < map.HitObjects.Count) map.HitObjects.RemoveAt(_index);
                    break;
                case Kind.NoteReplaced:
                    if (_index >= 0 && _index < map.HitObjects.Count) map.HitObjects[_index] = _noteAfter;
                    break;
                case Kind.NoteBatch:
                    if (_batch is not null)
                    {
                        foreach (var edit in _batch)
                        {
                            if (edit.Index >= 0 && edit.Index < map.HitObjects.Count)
                            {
                                map.HitObjects[edit.Index] = edit.After;
                            }
                        }
                    }
                    break;
                case Kind.TimingAdded:
                    map.TimingPoints.Insert(Math.Clamp(_index, 0, map.TimingPoints.Count), _tpAfter);
                    break;
                case Kind.TimingRemoved:
                    if (_index >= 0 && _index < map.TimingPoints.Count) map.TimingPoints.RemoveAt(_index);
                    break;
                case Kind.TimingReplaced:
                    if (_index >= 0 && _index < map.TimingPoints.Count) map.TimingPoints[_index] = _tpAfter;
                    break;
            }
        }
    }
}
