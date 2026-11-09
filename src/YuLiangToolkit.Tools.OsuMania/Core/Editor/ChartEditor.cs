using System;
using System.Collections.Generic;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 编辑器的**操作状态机**：摆放 / 拖动 / 拉长 LN / 删除 / 选中 / 拖时间点，
/// 以及「鼠标坐标 ↔ 列号 + 时间」的换算。
///
/// <para>放在 Core（不碰 WPF）是为了让这些交互逻辑能无头自检：自检直接调
/// <see cref="PressAt"/> / <see cref="DragTo"/> / <see cref="Release"/> 就能验证
/// 「吸附后的时间对不对」「拖出画布会不会跑到别的列」「撤销能不能回滚整次拖动」。</para>
///
/// <para><b>拖动是一次原子操作</b>：按下时记住原值，移动过程中只改内存里的预览，
/// 松手时 <see cref="EditorBeatmap.CommitBatch"/> 合成一个撤销步骤 —— 撤销一次回到拖动前，
/// 而不是要按几十次。</para>
/// </summary>
public sealed class ChartEditor
{
    /// <summary>拖尾巴的判定范围（像素）：离 LN 尾部这么近就算抓尾巴。</summary>
    public const double TailGrabPixels = 7.0;

    /// <summary>命中测试的默认容差（像素）——与画布上 note 的绘制高度同一量级。</summary>
    public const double NoteHitPixels = 6.0;

    /// <summary>LN 最小长度（毫秒）：拖太短就当普通键（避免造出 0 长度的“隐形面条”）。</summary>
    public const double MinLongNoteMs = 30.0;

    /// <summary>摆放工具（模拟 osu! 编辑器左侧 TOOLS 区）。</summary>
    public enum EditTool
    {
        /// <summary>选择工具：点选 / Ctrl 多选 / 框选 / 拖动已有 note，不会误放新 note。</summary>
        Select = 0,

        /// <summary>点左键放普通键。</summary>
        Note = 1,

        /// <summary>按住左键往下拖放长条（不拖 = 普通键）。</summary>
        LongNote = 2,
    }

    /// <summary>鼠标当前落在编辑器上的动作种类（决定光标形状）。</summary>
    public enum HoverKind
    {
        /// <summary>空白处。</summary>
        Empty = 0,

        /// <summary>一个 note 的主体。</summary>
        NoteBody = 1,

        /// <summary>LN 的尾巴。</summary>
        NoteTail = 2,

        /// <summary>时间点标记。</summary>
        TimingPoint = 3,
    }

    private int _dragNoteIndex = -1;
    private EditorNote _dragNoteOriginal;
    private bool _draggingTail;
    private double _pressTime;

    // 新 LN 拖拽（还没写进模型，松手才落地）
    private bool _creatingLongNote;
    private int _creatingColumn;
    private double _creatingStartTime;

    public ChartEditor(EditorBeatmap beatmap)
    {
        Beatmap = beatmap ?? throw new ArgumentNullException(nameof(beatmap));
    }

    /// <summary>谱面数据。</summary>
    public EditorBeatmap Beatmap { get; }

    /// <summary>时间轴视口（缩放 / 滚动）。</summary>
    public ChartViewport Viewport { get; } = new();

    /// <summary>当前工具。</summary>
    public EditTool Tool { get; set; } = EditTool.Note;

    /// <summary>网格细分（每拍几格）：1 / 2 / 3 / 4 / 6 / 8 / 12 / 16。</summary>
    public int GridDivisor { get; set; } = 4;

    /// <summary>是否吸附网格。</summary>
    public bool SnapToGrid { get; set; } = true;

    /// <summary>多选状态（选中 / 框选 / Z-V 区间扩选都由它管）。</summary>
    public NoteSelection Selection { get; } = new();

    /// <summary>
    /// 当前「主选中」的 note 下标（-1 = 没选）。
    /// <para>多选时它是最小的那个下标：属性面板 / 删除选中都按它来。</para>
    /// </summary>
    public int SelectedIndex => Selection.First;

    /// <summary>当前选中 note 的副本（没选时是 default）。</summary>
    public EditorNote SelectedNote
        => SelectedIndex >= 0 && SelectedIndex < Beatmap.HitObjects.Count
            ? Beatmap.HitObjects[SelectedIndex]
            : default;

    /// <summary>选中了几个 note。</summary>
    public int SelectionCount => Selection.Count;

    /// <summary>
    /// Z / V 区间扩选用的锚点时间（上一次按 Z/V 之前播放头 / 光标所在的时间）。
    /// 第一次按之前是 <see cref="double.NaN"/>，表示「还没有锚点 → 用当前播放头」。
    /// </summary>
    private double _rangeAnchorTime = double.NaN;

    /// <summary>上一次按 Z / V 时选中的列（再次按 Z/V 时沿用，这样连按可以一直加同一批轨道）。</summary>
    private readonly HashSet<int> _rangeAnchorColumns = new();

    /// <summary>是否正在框选（拖动矩形选择框）。</summary>
    public bool IsMarqueeActive { get; private set; }

    /// <summary>框选矩形（时间 + 列）；没在框选时 <c>IsActive=false</c>。</summary>
    public (bool IsActive, MarqueeSelection.Box Box) Marquee
        => IsMarqueeActive ? (true, MarqueeSelection.Box.FromCorners(_marqueeTimeA, _marqueeTimeB,
            _marqueeColumnA, _marqueeColumnB)) : (false, default);

    private double _marqueeTimeA;
    private double _marqueeTimeB;
    private int _marqueeColumnA;
    private int _marqueeColumnB;
    private bool _marqueeAdditive;

    /// <summary>播放头位置（毫秒）。由 ViewModel 的定时器更新。</summary>
    public double PlayheadMs { get; set; }

    /// <summary>内容版本号：每次编辑 / 换谱面 +1，渲染缓存据此失效。</summary>
    public int Version { get; private set; }

    /// <summary>当前是否有拖拽进行中（拖动时禁用播放跟随，避免画面乱跑）。</summary>
    public bool IsDragging => _dragNoteIndex >= 0 || _creatingLongNote || IsMarqueeActive;

    /// <summary>正在创建的长条预览（松手前给渲染用；没在创建时 <c>HasValue=false</c>）。</summary>
    public (bool HasValue, int Column, double StartTime, double EndTime) CreatePreview
    {
        get
        {
            if (!_creatingLongNote) return (false, 0, 0, 0);
            var start = Math.Min(_creatingStartTime, CurrentDragTime);
            var end = Math.Max(_creatingStartTime, CurrentDragTime);
            return (true, _creatingColumn, start, end);
        }
    }

    /// <summary>拖拽过程中光标的当前时间（松手前）。</summary>
    private double CurrentDragTime { get; set; }

    /// <summary>某个 note 是不是当前拖拽中的那个（渲染高亮用）。</summary>
    public int DraggingIndex => _dragNoteIndex;

    /// <summary>编辑发生（界面据此刷新统计 / 脏标记）。</summary>
    public event EventHandler? Changed;

    // ================================================================
    // 坐标换算
    // ================================================================

    /// <summary>列宽（像素）。</summary>
    public double ColumnWidth { get; private set; } = 40;

    /// <summary>轨道区左边距（左边是时间点标尺）。</summary>
    public double LaneLeft { get; private set; } = 64;

    /// <summary>轨道区宽度。</summary>
    public double LaneWidth { get; private set; } = 200;

    /// <summary>控件尺寸变化时调（渲染前统一由画布推一次）。</summary>
    public void SetLayout(double laneLeft, double laneWidth, int keyCount)
    {
        LaneLeft = laneLeft;
        LaneWidth = Math.Max(1, laneWidth);
        var keys = Math.Clamp(keyCount, 1, 10);
        ColumnWidth = LaneWidth / keys;
    }

    /// <summary>X 像素 → 列号（越界夹取到合法列）。</summary>
    public int ColumnAt(double x)
    {
        var keys = Math.Max(1, Beatmap.KeyCount);
        var column = (int)Math.Floor((x - LaneLeft) / ColumnWidth);
        return Math.Clamp(column, 0, keys - 1);
    }

    /// <summary>这个 X 是不是落在轨道区里（留白区 / 标尺区都不算）。</summary>
    public bool IsInsideLane(double x) => x >= LaneLeft && x < LaneLeft + LaneWidth;

    /// <summary>列号 → 该列左边的 X 像素。</summary>
    public double ColumnLeft(int column) => LaneLeft + column * ColumnWidth;

    /// <summary>列号 → 该列中心的 X 像素。</summary>
    public double ColumnCenter(int column) => LaneLeft + (column + 0.5) * ColumnWidth;

    /// <summary>时间 → Y 像素（相对内容区）。</summary>
    public double TimeToY(double timeMs) => Viewport.TimeToY(timeMs);

    /// <summary>Y 像素 → 时间。</summary>
    public double YToTime(double y) => Viewport.YToTime(y);

    /// <summary>按当前设置吸附时间（吸附关闭 / 无红线时原样返回）。</summary>
    public double Snap(double timeMs)
        => SnapToGrid ? BeatGrid.Snap(Beatmap.RedLines, timeMs, GridDivisor) : timeMs;

    // ================================================================
    // 命中测试
    // ================================================================

    /// <summary>
    /// 命中测试：给定屏幕坐标，找出该处的 note / 时间点。
    /// </summary>
    public (HoverKind Kind, int NoteIndex, int TimingIndex) HitTest(double x, double y)
    {
        // 时间点标尺区：只认时间点（标尺右边界就是轨道左边界）
        if (x < LaneLeft)
        {
            var timeAt = YToTime(y);
            var timingIndex = FindTimingPointNear(timeAt, TimingPointToleranceMs(y));
            return timingIndex >= 0
                ? (HoverKind.TimingPoint, -1, timingIndex)
                : (HoverKind.Empty, -1, -1);
        }

        var column = ColumnAt(x);
        var time = YToTime(y);
        var tolerance = Math.Max(NoteHitPixels, NoteHitPixels / Viewport.PxPerMs * 0.5);
        var noteIndex = Beatmap.HitTest(column, time, tolerance);
        if (noteIndex < 0) return (HoverKind.Empty, -1, -1);

        var note = Beatmap.HitObjects[noteIndex];
        var tailY = TimeToY(note.EndTime);
        var isTail = note.IsLongNote && Math.Abs(y - tailY) <= TailGrabPixels;
        return (isTail ? HoverKind.NoteTail : HoverKind.NoteBody, noteIndex, -1);
    }

    /// <summary>时间点命中容差（像素转毫秒，至少 4ms）。</summary>
    private double TimingPointToleranceMs(double y)
        => Math.Max(4.0, 8.0 / Math.Max(1e-6, Viewport.PxPerMs));

    private int FindTimingPointNear(double timeMs, double toleranceMs)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Beatmap.TimingPoints.Count; i++)
        {
            var distance = Math.Abs(Beatmap.TimingPoints[i].Time - timeMs);
            if (distance <= toleranceMs && distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    // ================================================================
    // 鼠标：按下 / 拖动 / 松开
    // ================================================================

    /// <summary>
    /// 左键按下。
    /// </summary>
    /// <returns>true = 这次按下被编辑器接管（调用方不要再去滚播放头）。</returns>
    public bool PressAt(double x, double y)
    {
        var (kind, noteIndex, timingIndex) = HitTest(x, y);

        // 点时间点：选中（拖动由拖拽逻辑处理，这里只记按下位置）
        if (kind == HoverKind.TimingPoint)
        {
            Selection.Clear();
            _dragNoteIndex = -1;
            _draggingTimingIndex = timingIndex;
            _draggingTimingOriginal = Beatmap.TimingPoints[timingIndex];
            RaiseChanged();
            return true;
        }

        _draggingTimingIndex = -1;

        var additive = IsCtrlDown;
        _pressX = x;

        // 抓到已有 note：移动 / 拉尾巴 / 多选拖动
        if (noteIndex >= 0)
        {
            if (additive)
            {
                // Ctrl + 点击：切换这个 note 的选中状态（不加时不重置其他选中）
                Selection.Toggle(noteIndex);
                _dragNoteIndex = -1;
                RaiseChanged();
                return true;
            }

            // 点的是「已经选中的其中之一」→ 保留整组选中一起拖（模拟 osu! 编辑器）
            if (!Selection.Contains(noteIndex))
            {
                Selection.SelectSingle(noteIndex);
            }

            _dragNoteIndex = noteIndex;
            _dragNoteOriginal = Beatmap.HitObjects[noteIndex];
            _draggingTail = kind == HoverKind.NoteTail;
            _pressTime = YToTime(y);
            BeginGroupDrag();
            RaiseChanged();
            return true;
        }

        // 空白处
        _dragNoteIndex = -1;

        // 点到轨道**外面**的留白区（Note 宽度调小后左右会留白）：
        // 不当成"空白轨道"，直接忽略（不然会在第 0 列 / 最后一列莫名冒出 note）。
        var insideLane = IsInsideLane(x);

        // 选择工具 / 按住 Ctrl 拖空白：开始框选（不清空旧的，松手时按 additive 决定合还是替）
        if (Tool == EditTool.Select || additive)
        {
            _marqueeAdditive = additive;
            if (!additive) Selection.Clear();

            IsMarqueeActive = true;
            _marqueeTimeA = YToTime(y);
            _marqueeTimeB = _marqueeTimeA;
            _marqueeColumnA = ColumnAt(x);
            _marqueeColumnB = _marqueeColumnA;
            RaiseChanged();
            return true;
        }

        if (!insideLane)
        {
            // 摆放工具点在留白区：只取消选中，不放 note
            Selection.Clear();
            RaiseChanged();
            return true;
        }

        // 摆放工具在空白处点击：先取消选中，再放
        Selection.Clear();

        var column = ColumnAt(x);
        var time = Snap(YToTime(y));

        if (Tool == EditTool.LongNote)
        {
            // 先记下起点，拖动过程中预览，松手才落地
            _creatingLongNote = true;
            _creatingColumn = column;
            _creatingStartTime = time;
            CurrentDragTime = time;
            RaiseChanged();
            return true;
        }

        // 普通键：立刻放
        Beatmap.AddNote(EditorNote.NewNote(column, time));
        Selection.SelectSingle(Beatmap.HitObjects.Count - 1);
        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>按下时的 X（判断有没有真的拖动：太小的位移当单击）。</summary>
    private double _pressX;

    /// <summary>Ctrl 是不是按着（多选）。</summary>
    public bool IsCtrlDown { get; set; }

    private int _draggingTimingIndex = -1;
    private EditorTimingPoint _draggingTimingOriginal;

    // 多选整体拖动：记下每个选中 note 的原值（松手时合成一步撤销）
    private readonly List<EditorBeatmap.NoteEdit> _groupDrag = new();
    private bool _groupDragging;

    /// <summary>
    /// 开始一次「多选整体拖动」：如果选中的不止一个，就为组里每个 note 记下原值。
    /// </summary>
    private void BeginGroupDrag()
    {
        _groupDrag.Clear();
        _groupDragging = false;
        if (Selection.Count <= 1) return;

        foreach (var index in Selection.Indices)
        {
            if (index < 0 || index >= Beatmap.HitObjects.Count) continue;
            _groupDrag.Add(new EditorBeatmap.NoteEdit(index, Beatmap.HitObjects[index], Beatmap.HitObjects[index]));
        }

        _groupDragging = _groupDrag.Count > 1;
    }

    /// <summary>
    /// 左键按住移动。返回 true 表示吃掉这次移动（不要再去拖滚动条 / 播放头）。
    /// </summary>
    public bool DragTo(double x, double y)
    {
        // 框选
        if (IsMarqueeActive)
        {
            _marqueeTimeB = YToTime(y);
            _marqueeColumnB = ColumnAt(x);
            RaiseChanged();
            return true;
        }

        // 拖时间点
        if (_draggingTimingIndex >= 0)
        {
            var newTime = Math.Max(0, Math.Round(Snap(YToTime(y))));
            var updated = _draggingTimingOriginal.WithTime(newTime);
            if (Beatmap.ReplaceTimingPoint(_draggingTimingIndex, updated))
            {
                BumpVersion();
                RaiseChanged();
            }
            return true;
        }

        // 拖已有 note（含多选整体拖动）
        if (_dragNoteIndex >= 0 && _dragNoteIndex < Beatmap.HitObjects.Count)
        {
            var note = Beatmap.HitObjects[_dragNoteIndex];
            var targetTime = Snap(YToTime(y));
            var targetColumn = ColumnAt(x);
            var keys = Math.Max(1, Beatmap.KeyCount);

            if (_draggingTail)
            {
                // 拉尾巴只作用于被抓住的那一个
                var updated = note.WithEnd(Math.Max(note.StartTime + MinLongNoteMs, targetTime));
                if (!updated.Equals(note))
                {
                    Beatmap.HitObjects[_dragNoteIndex] = updated;
                    Beatmap.InvalidateIndex();
                    BumpVersion();
                    RaiseChanged();
                }
                return true;
            }

            var deltaTime = targetTime - _pressTime;
            var deltaColumn = targetColumn - ColumnAt(_pressX);

            if (_groupDragging)
            {
                // 整组一起平移：时间 + 列（列统一按 deltaColumn 夹取，保证相对关系不变）
                for (var i = 0; i < _groupDrag.Count; i++)
                {
                    var edit = _groupDrag[i];
                    if (edit.Index < 0 || edit.Index >= Beatmap.HitObjects.Count) continue;

                    var original = edit.Before;
                    var column = Math.Clamp(original.Column + deltaColumn, 0, keys - 1);
                    var updated = original.Shift(deltaTime).WithColumn(column);
                    if (updated.StartTime < 0) updated = updated.Shift(-updated.StartTime);

                    Beatmap.HitObjects[edit.Index] = updated;
                    _groupDrag[i] = edit with { After = updated };
                }

                Beatmap.InvalidateIndex();
                BumpVersion();
                RaiseChanged();
                return true;
            }

            // 单个 note 拖动：时间 + 列
            var single = note.Shift(deltaTime).WithColumn(Math.Clamp(targetColumn, 0, keys - 1));
            if (single.StartTime < 0) single = single.Shift(-single.StartTime);

            if (!single.Equals(note))
            {
                // 预览：直接改内存，松手时统一提交成一步撤销
                Beatmap.HitObjects[_dragNoteIndex] = single;
                Beatmap.InvalidateIndex();
                BumpVersion();
                RaiseChanged();
            }
            return true;
        }

        // 创建长条：只更新预览
        if (_creatingLongNote)
        {
            CurrentDragTime = Snap(YToTime(y));
            BumpVersion();
            RaiseChanged();
            return true;
        }

        return false;
    }

    /// <summary>左键松开：把拖动 / 创建落地。</summary>
    public void Release()
    {
        // 时间点拖动：合成一步撤销（这里简化成“替换成当前值”，历史里已有 before）
        if (_draggingTimingIndex >= 0)
        {
            var index = _draggingTimingIndex;
            _draggingTimingIndex = -1;
            var now = Beatmap.TimingPoints[index];
            if (!now.Equals(_draggingTimingOriginal))
            {
                // 回滚成原值再用正规接口改一次，保证撤销栈里只有一步
                Beatmap.TimingPoints[index] = _draggingTimingOriginal;
                Beatmap.ReplaceTimingPoint(index, now);
                Beatmap.InvalidateTimingPoints();
                BumpVersion();
                RaiseChanged();
            }
            return;
        }

        // 框选：松手时把框里的 note 合进选择
        if (IsMarqueeActive)
        {
            IsMarqueeActive = false;
            var box = MarqueeSelection.Box.FromCorners(_marqueeTimeA, _marqueeTimeB,
                _marqueeColumnA, _marqueeColumnB);
            var hits = MarqueeSelection.Find(Beatmap, box);

            if (_marqueeAdditive) Selection.AddRange(hits);
            else Selection.SetTo(hits);

            // 框选之后把 Z/V 的锚点定在框的中心：接着按 Z/V 就能从这个范围继续扩选
            _rangeAnchorTime = (box.FromTime + box.ToTime) / 2;
            _rangeAnchorColumns.Clear();
            for (var column = box.FromColumn; column <= box.ToColumn; column++) _rangeAnchorColumns.Add(column);

            RaiseChanged();
            return;
        }

        // 已有 note 的拖动
        if (_dragNoteIndex >= 0)
        {
            var index = _dragNoteIndex;
            _dragNoteIndex = -1;

            if (_groupDragging)
            {
                // 多选整体拖动：把每个 note 的「原值 → 现值」合成一步撤销
                _groupDragging = false;
                var edits = new List<EditorBeatmap.NoteEdit>(_groupDrag.Count);
                foreach (var edit in _groupDrag)
                {
                    if (edit.Before.Equals(edit.After)) continue;
                    edits.Add(edit);
                }

                if (edits.Count > 0)
                {
                    // 先回滚成原值，再交给 CommitBatch 正规提交（撤销栈里只有一步）
                    foreach (var edit in edits)
                    {
                        if (edit.Index >= 0 && edit.Index < Beatmap.HitObjects.Count)
                        {
                            Beatmap.HitObjects[edit.Index] = edit.Before;
                        }
                    }

                    Beatmap.CommitBatch(edits);
                    BumpVersion();
                    RaiseChanged();
                }

                _groupDrag.Clear();
                return;
            }

            var now = Beatmap.HitObjects[index];
            if (!now.Equals(_dragNoteOriginal))
            {
                Beatmap.HitObjects[index] = _dragNoteOriginal;
                Beatmap.CommitBatch(new[] { new EditorBeatmap.NoteEdit(index, _dragNoteOriginal, now) });
                BumpVersion();
                RaiseChanged();
            }
            return;
        }

        // 新长条
        if (_creatingLongNote)
        {
            _creatingLongNote = false;
            var column = _creatingColumn;
            var start = Math.Min(_creatingStartTime, CurrentDragTime);
            var end = Math.Max(_creatingStartTime, CurrentDragTime);

            if (end - start >= MinLongNoteMs)
            {
                Beatmap.AddNote(EditorNote.NewLongNote(column, start, end));
                Selection.SelectSingle(Beatmap.HitObjects.Count - 1);
            }
            else
            {
                // 拖得太短：当普通键
                Beatmap.AddNote(EditorNote.NewNote(column, start));
                Selection.SelectSingle(Beatmap.HitObjects.Count - 1);
            }

            BumpVersion();
            RaiseChanged();
        }
    }

    /// <summary>右键：删除点到的 note（返回是否删掉）。</summary>
    public bool DeleteAt(double x, double y)
    {
        var (kind, noteIndex, timingIndex) = HitTest(x, y);

        if (kind == HoverKind.TimingPoint)
        {
            if (!Beatmap.RemoveTimingPointAt(timingIndex)) return false;
            BumpVersion();
            RaiseChanged();
            return true;
        }

        if (noteIndex < 0) return false;

        // 右键点在外面：如果要删的 note 不在选中集里，只在它自己（osu! 编辑器的行为）
        var deleteSelection = Selection.Contains(noteIndex) && Selection.Count > 1;

        if (deleteSelection)
        {
            DeleteSelection();
            return true;
        }

        if (!Beatmap.RemoveNoteAt(noteIndex)) return false;
        Selection.OnNoteRemoved(noteIndex);

        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// 删除所有选中的 note（Delete 键 / 右键点在选中集里时调）。
    /// <para>多个删除合成**一步撤销**（从大到小删，避免下标错位）。</para>
    /// </summary>
    public int DeleteSelection()
    {
        if (Selection.Count == 0) return 0;

        var indices = new List<int>(Selection.Indices);
        indices.Sort((a, b) => b.CompareTo(a));   // 从大到小删

        var removed = 0;
        foreach (var index in indices)
        {
            if (index < 0 || index >= Beatmap.HitObjects.Count) continue;
            if (Beatmap.RemoveNoteAt(index)) removed++;
        }

        if (removed == 0) return 0;

        Selection.Clear();
        BumpVersion();
        RaiseChanged();
        return removed;
    }

    /// <summary>删除当前选中的 note（Delete 键；多选时一次删光，合成一步撤销）。</summary>
    public bool DeleteSelected() => DeleteSelection() > 0;

    /// <summary>选中指定的一个 note（列表 / 命中测试用）。</summary>
    public void Select(int noteIndex)
    {
        Selection.SelectSingle(noteIndex >= 0 && noteIndex < Beatmap.HitObjects.Count ? noteIndex : -1);
        RaiseChanged();
    }

    // ================================================================
    // Z / V 跳转 + 区间扩选（模拟 osu! 编辑器）
    // ================================================================

    /// <summary>
    /// 跳转到第一个 note 的开始时间（Z 键）。
    /// <para>如果之前已经有锚点 / 有选中范围，会顺便把「锚点 → 第一个 note」之间、
    /// 在这些列上的所有 note 加进选择（这样连续按 Z 就是逐层扩选）。</para>
    /// </summary>
    /// <returns>目标时间（没有 note 时返回 NaN）。</returns>
    public double? JumpToFirstNote()
    {
        var firstTime = EarliestNoteTime();
        if (firstTime is null) return null;

        ExtendSelectionTo(firstTime.Value);
        return firstTime;
    }

    /// <summary>跳转到最后一个 note 的开始时间（V 键），并做同样的区间扩选。</summary>
    /// <returns>目标时间（没有 note 时返回 NaN）。</returns>
    public double? JumpToLastNote()
    {
        var lastTime = LatestNoteStartTime();
        if (lastTime is null) return null;

        ExtendSelectionTo(lastTime.Value);
        return lastTime;
    }

    /// <summary>
    /// 区间扩选的实际动作：从「上次锚点（没有就用播放头）」到 <paramref name="targetTime"/>，
    /// 在锚点列集合（没有就用当前选中的 note 所在列 / 光标列）上把 note 加进选择。
    /// </summary>
    private void ExtendSelectionTo(double targetTime)
    {
        // 锚点：第一次按 Z/V 之前没有锚点 → 用播放头（用户看着的那个位置）
        var anchor = double.IsNaN(_rangeAnchorTime) ? PlayheadMs : _rangeAnchorTime;

        // 列集合：优先用上次框选 / 扩选记下的列；没有就从当前选中项推；再没有就全列
        var columns = _rangeAnchorColumns.Count > 0 ? new List<int>(_rangeAnchorColumns) : null;
        if (columns is null && Selection.Count > 0)
        {
            var fromSelection = new HashSet<int>();
            foreach (var index in Selection.Indices)
            {
                if (index >= 0 && index < Beatmap.HitObjects.Count)
                {
                    fromSelection.Add(Beatmap.HitObjects[index].Column);
                }
            }
            if (fromSelection.Count > 0) columns = new List<int>(fromSelection);
        }

        Selection.SelectTimeRange(Beatmap, anchor, targetTime, columns);

        // 更新锚点：下次按 Z/V 从这次的目标继续（连按 Z 会一路往回扩）
        _rangeAnchorTime = targetTime;
        if (columns is not null)
        {
            _rangeAnchorColumns.Clear();
            foreach (var column in columns) _rangeAnchorColumns.Add(column);
        }
    }

    /// <summary>最早的 note 开始时间（没有返回 null）。</summary>
    public double? EarliestNoteTime()
    {
        if (Beatmap.HitObjects.Count == 0) return null;

        var min = double.MaxValue;
        foreach (var note in Beatmap.HitObjects)
        {
            if (note.StartTime < min) min = note.StartTime;
        }
        return min == double.MaxValue ? null : min;
    }

    /// <summary>最晚的 note 开始时间（注意：不是 LN 尾巴；没有返回 null）。</summary>
    public double? LatestNoteStartTime()
    {
        if (Beatmap.HitObjects.Count == 0) return null;

        // 走索引的排序结果：最后一个 = 最晚开始时间
        var count = Beatmap.Index.Count;
        return count == 0 ? null : Beatmap.Index.StartTimeAt(count - 1);
    }

    /// <summary>
    /// 设定 Z/V 扩选的锚点（例如用户先框选了一块，再按 Z 就从那块往回扩）。
    /// </summary>
    public void SetRangeAnchor(double timeMs, IEnumerable<int>? columns = null)
    {
        _rangeAnchorTime = timeMs;
        _rangeAnchorColumns.Clear();
        if (columns is null) return;
        foreach (var column in columns) _rangeAnchorColumns.Add(column);
    }

    /// <summary>清掉 Z/V 锚点（换谱面 / 点空白处时调）。</summary>
    public void ClearRangeAnchor()
    {
        _rangeAnchorTime = double.NaN;
        _rangeAnchorColumns.Clear();
    }

    /// <summary>撤销。</summary>
    public bool Undo()
    {
        if (!Beatmap.Undo()) return false;
        Selection.Clear();
        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>重做。</summary>
    public bool Redo()
    {
        if (!Beatmap.Redo()) return false;
        Selection.Clear();
        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>把播放头滚进视野（跳转后调）。</summary>
    public void CenterOnTime(double timeMs, double ratio = 0.5)
        => Viewport.ScrollTo(timeMs - Viewport.VisibleMs * ratio);

    /// <summary>换谱面后重置所有状态。</summary>
    public void ResetInteraction()
    {
        _dragNoteIndex = -1;
        _draggingTimingIndex = -1;
        _creatingLongNote = false;
        _groupDragging = false;
        _groupDrag.Clear();
        IsMarqueeActive = false;
        Selection.Clear();
        ClearRangeAnchor();
        BumpVersion();
    }

    private void BumpVersion()
    {
        Version++;
        Beatmap.IsDirty = true;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
