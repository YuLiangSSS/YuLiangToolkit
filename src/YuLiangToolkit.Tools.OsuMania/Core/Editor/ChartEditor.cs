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

    /// <summary>摆放工具。</summary>
    public enum EditTool
    {
        /// <summary>点左键放普通键。</summary>
        Note = 0,

        /// <summary>按住左键往下拖放长条（不拖 = 普通键）。</summary>
        LongNote = 1,
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

    /// <summary>当前选中的 note 下标（-1 = 没选）。</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>当前选中 note 的副本（没选时是 default）。</summary>
    public EditorNote SelectedNote
        => SelectedIndex >= 0 && SelectedIndex < Beatmap.HitObjects.Count
            ? Beatmap.HitObjects[SelectedIndex]
            : default;

    /// <summary>播放头位置（毫秒）。由 ViewModel 的定时器更新。</summary>
    public double PlayheadMs { get; set; }

    /// <summary>内容版本号：每次编辑 / 换谱面 +1，渲染缓存据此失效。</summary>
    public int Version { get; private set; }

    /// <summary>当前是否有拖拽进行中（拖动时禁用播放跟随，避免画面乱跑）。</summary>
    public bool IsDragging => _dragNoteIndex >= 0 || _creatingLongNote;

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
        // 时间点标尺区：只认时间点
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
            SelectedIndex = -1;
            _dragNoteIndex = -1;
            _draggingTimingIndex = timingIndex;
            _draggingTimingOriginal = Beatmap.TimingPoints[timingIndex];
            RaiseChanged();
            return true;
        }

        _draggingTimingIndex = -1;

        // 抓到已有 note：移动 / 拉尾巴
        if (noteIndex >= 0)
        {
            SelectedIndex = noteIndex;
            _dragNoteIndex = noteIndex;
            _dragNoteOriginal = Beatmap.HitObjects[noteIndex];
            _draggingTail = kind == HoverKind.NoteTail;
            _pressTime = YToTime(y);
            RaiseChanged();
            return true;
        }

        // 空白处：取消选中
        SelectedIndex = -1;
        _dragNoteIndex = -1;

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
        SelectedIndex = Beatmap.HitObjects.Count - 1;
        BumpVersion();
        RaiseChanged();
        return true;
    }

    private int _draggingTimingIndex = -1;
    private EditorTimingPoint _draggingTimingOriginal;

    /// <summary>
    /// 左键按住移动。返回 true 表示吃掉这次移动（不要再去拖滚动条 / 播放头）。
    /// </summary>
    public bool DragTo(double x, double y)
    {
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

        // 拖已有 note
        if (_dragNoteIndex >= 0 && _dragNoteIndex < Beatmap.HitObjects.Count)
        {
            var note = Beatmap.HitObjects[_dragNoteIndex];
            var targetColumn = _draggingTail ? note.Column : ColumnAt(x);
            var targetTime = Snap(YToTime(y));

            EditorNote updated;
            if (_draggingTail)
            {
                updated = note.WithEnd(Math.Max(note.StartTime + MinLongNoteMs, targetTime));
            }
            else
            {
                // 移动：整体平移（LN 保持长度），列跟着鼠标
                var delta = targetTime - _pressTime;
                updated = note.Shift(delta).WithColumn(Math.Clamp(targetColumn, 0, Math.Max(0, Beatmap.KeyCount - 1)));
                // 平移后时间不能为负
                if (updated.StartTime < 0) updated = updated.Shift(-updated.StartTime);
            }

            if (!updated.Equals(note))
            {
                // 预览：直接改内存，松手时统一提交成一步撤销
                Beatmap.HitObjects[_dragNoteIndex] = updated;
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

        // 已有 note 的拖动
        if (_dragNoteIndex >= 0)
        {
            var index = _dragNoteIndex;
            _dragNoteIndex = -1;
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
                SelectedIndex = Beatmap.HitObjects.Count - 1;
            }
            else
            {
                // 拖得太短：当普通键
                Beatmap.AddNote(EditorNote.NewNote(column, start));
                SelectedIndex = Beatmap.HitObjects.Count - 1;
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
            if (SelectedIndex >= Beatmap.HitObjects.Count) SelectedIndex = Beatmap.HitObjects.Count - 1;
            BumpVersion();
            RaiseChanged();
            return true;
        }

        if (noteIndex < 0) return false;
        if (!Beatmap.RemoveNoteAt(noteIndex)) return false;

        if (SelectedIndex == noteIndex) SelectedIndex = -1;
        else if (SelectedIndex > noteIndex) SelectedIndex--;

        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>删除当前选中的 note（Delete 键）。</summary>
    public bool DeleteSelected()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Beatmap.HitObjects.Count) return false;
        if (!Beatmap.RemoveNoteAt(SelectedIndex)) return false;
        SelectedIndex = -1;
        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>选中指定 note（列表 / 命中测试用）。</summary>
    public void Select(int noteIndex)
    {
        SelectedIndex = noteIndex >= 0 && noteIndex < Beatmap.HitObjects.Count ? noteIndex : -1;
        RaiseChanged();
    }

    /// <summary>撤销。</summary>
    public bool Undo()
    {
        if (!Beatmap.Undo()) return false;
        if (SelectedIndex >= Beatmap.HitObjects.Count) SelectedIndex = -1;
        BumpVersion();
        RaiseChanged();
        return true;
    }

    /// <summary>重做。</summary>
    public bool Redo()
    {
        if (!Beatmap.Redo()) return false;
        if (SelectedIndex >= Beatmap.HitObjects.Count) SelectedIndex = -1;
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
        SelectedIndex = -1;
        BumpVersion();
    }

    private void BumpVersion()
    {
        Version++;
        Beatmap.IsDirty = true;
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
