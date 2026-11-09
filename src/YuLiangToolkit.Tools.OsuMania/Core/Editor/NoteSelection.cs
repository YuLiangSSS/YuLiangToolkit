using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 多选状态（一组 note 下标）+ 框选 / 区间选择的纯逻辑。
///
/// <para><b>为什么单独抽出来</b>：多选的边界条件特别多 —— 按住 Ctrl 加选要不要去重、
/// 框选要不要清掉旧选中、按 Z/V 之后怎么按「列 + 时间区间」扩选、删掉中间一个之后下标还对不对……
/// 这些塞在控件里就只能靠手点，抽成纯类后自检能逐条断言。</para>
///
/// <para><b>下标有效性</b>：这里存的是 note 在 <see cref="EditorBeatmap.HitObjects"/> 里的下标。
/// 删除 note 会让后面的下标前移，所以**每次删完必须调 <see cref="OnNotesRemoved"/>
/// （或者干脆 <see cref="Clear"/>）**，否则选择会错位到别的 note 上。</para>
///
/// <para><b>不排序、不重复</b>：内部是 <see cref="SortedSet{T}"/>，遍历永远按下标升序
/// （渲染顺序稳定），加选自动去重。</para>
/// </summary>
public sealed class NoteSelection
{
    private readonly SortedSet<int> _indices = new();

    /// <summary>选中变化（清空 / 加选 / 移除 / 批量替换都触发）。</summary>
    public event EventHandler? Changed;

    /// <summary>选中的 note 数。</summary>
    public int Count => _indices.Count;

    /// <summary>有没有选中东西。</summary>
    public bool IsEmpty => _indices.Count == 0;

    /// <summary>升序遍历选中下标。</summary>
    public IEnumerable<int> Indices => _indices;

    /// <summary>最小的选中下标（没有返回 -1）。合并后的单个 note 编辑用。</summary>
    public int First => _indices.Count == 0 ? -1 : _indices.Min;

    /// <summary>是不是选中了这个下标。</summary>
    public bool Contains(int index) => _indices.Contains(index);

    /// <summary>清空。</summary>
    /// <returns>真的有变化才返回 true。</returns>
    public bool Clear()
    {
        if (_indices.Count == 0) return false;
        _indices.Clear();
        RaiseChanged();
        return true;
    }

    /// <summary>只选中这一个（普通左键点击）。</summary>
    public bool SelectSingle(int index)
    {
        if (index < 0)
        {
            return Clear();
        }

        if (_indices.Count == 1 && _indices.Contains(index)) return false;

        _indices.Clear();
        _indices.Add(index);
        RaiseChanged();
        return true;
    }

    /// <summary>加选一个（Ctrl + 点击）。已经选中的会被**取消选中**（切换语义，和 osu! / 资源管理器一致）。</summary>
    public bool Toggle(int index)
    {
        if (index < 0) return false;

        if (!_indices.Remove(index)) _indices.Add(index);
        RaiseChanged();
        return true;
    }

    /// <summary>加入一个（不改已选的其他项）。</summary>
    public bool Add(int index)
    {
        if (index < 0 || !_indices.Add(index)) return false;
        RaiseChanged();
        return true;
    }

    /// <summary>批量替换（框选的结果直接塞进来）。</summary>
    public bool SetTo(IEnumerable<int> indices)
    {
        var next = new SortedSet<int>();
        foreach (var index in indices)
        {
            if (index >= 0) next.Add(index);
        }

        if (SameAs(next)) return false;

        _indices.Clear();
        foreach (var index in next) _indices.Add(index);
        RaiseChanged();
        return true;
    }

    /// <summary>批量加入（Ctrl + 框选：并到现有选择上）。</summary>
    public bool AddRange(IEnumerable<int> indices)
    {
        var changed = false;
        foreach (var index in indices)
        {
            if (index < 0) continue;
            if (_indices.Add(index)) changed = true;
        }

        if (changed) RaiseChanged();
        return changed;
    }

    /// <summary>
    /// 删掉了一个下标为 <paramref name="removedIndex"/> 的 note：把选择里比它大的都前移一位。
    /// **每次 <see cref="EditorBeatmap.RemoveNoteAt"/> 之后必须调**。
    /// </summary>
    public void OnNoteRemoved(int removedIndex)
    {
        if (_indices.Count == 0) return;

        var next = new SortedSet<int>();
        var changed = false;
        foreach (var index in _indices)
        {
            if (index == removedIndex)
            {
                changed = true;
                continue;
            }

            next.Add(index > removedIndex ? index - 1 : index);
            if (index > removedIndex) changed = true;
        }

        if (!changed) return;

        _indices.Clear();
        foreach (var index in next) _indices.Add(index);
        RaiseChanged();
    }

    /// <summary>批量删除之后重建选择（给「删除选中」用：删完直接清空最省事）。</summary>
    public void ClearAfterDelete() => Clear();

    /// <summary>
    /// 区间扩选（按 Z / V 之后的行为，模拟 osu! 编辑器）：
    /// 从 <paramref name="anchorTime"/> 到 <paramref name="targetTime"/>、在这些**列**上，
    /// 把所有「开始时间落在这个区间内」的 note 都加进选择。
    ///
    /// <para>参数里的 <paramref name="columns"/> 为 null 或空 = 所有列（只限制时间区间）。</para>
    /// </summary>
    /// <param name="beatmap">谱面。</param>
    /// <param name="anchorTime">锚点时间（按下 Z/V 之前光标所在的时间）。</param>
    /// <param name="targetTime">目标时间（第一个 / 最后一个 note 的开始时间）。</param>
    /// <param name="columns">只选这些列；null / 空 = 全部列。</param>
    /// <returns>新加进来的数量。</returns>
    public int SelectTimeRange(EditorBeatmap beatmap, double anchorTime, double targetTime,
        IReadOnlyCollection<int>? columns = null)
    {
        if (beatmap.HitObjects.Count == 0) return 0;

        var from = Math.Min(anchorTime, targetTime);
        var to = Math.Max(anchorTime, targetTime);

        var usesColumns = columns is { Count: > 0 };

        var newly = new List<int>();
        for (var i = 0; i < beatmap.HitObjects.Count; i++)
        {
            var note = beatmap.HitObjects[i];
            if (note.StartTime < from || note.StartTime > to) continue;
            if (usesColumns && !Contains(columns!, note.Column)) continue;
            newly.Add(i);
        }

        if (newly.Count == 0) return 0;
        return AddRange(newly) ? newly.Count : 0;
    }

    private static bool Contains(IReadOnlyCollection<int> columns, int column)
    {
        foreach (var candidate in columns)
        {
            if (candidate == column) return true;
        }
        return false;
    }

    private bool SameAs(SortedSet<int> other)
    {
        if (_indices.Count != other.Count) return false;
        using var a = _indices.GetEnumerator();
        using var b = other.GetEnumerator();
        while (a.MoveNext() && b.MoveNext())
        {
            if (a.Current != b.Current) return false;
        }
        return true;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
