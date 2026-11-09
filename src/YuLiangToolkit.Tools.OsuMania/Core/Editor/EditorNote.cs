namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 编辑器里的一个打击物（普通键 / 长条）。**这是编辑器自己的轻量模型**，与
/// <see cref="OsuManiaBeatmap"/> 的 <c>ManiaNote</c> 分开：编辑时要频繁改时间 / 列、
/// 还要挂在撤销栈上，用不可变的小结构体最省事（值类型，复制即快照）。
///
/// <para><b>为什么不用 <c>ManiaNote</c></b>：它是可变 class + <c>EndTime</c> setter 带副作用
/// （赋相同值会改回普通键），在「拖尾巴 / 撤销 / 批量平移」场景里极易踩坑。</para>
/// </summary>
/// <remarks>
/// 【不可变】所有字段都是 <c>init</c>：改一个 note = 造一个新的（撤销栈直接存旧值即可）。
/// 【时间单位】毫秒（double）。.osu 原文是整数毫秒，但拖拽 / 吸附过程中会有小数，
/// 写回文件时统一四舍五入（<see cref="EditorBeatmap"/> 负责）。
/// </remarks>
public readonly record struct EditorNote
{
    /// <summary>列号（0 基）。</summary>
    public int Column { get; init; }

    /// <summary>开始时间（毫秒）。</summary>
    public double StartTime { get; init; }

    /// <summary>结束时间（毫秒）。普通键等于 <see cref="StartTime"/>。</summary>
    public double EndTime { get; init; }

    /// <summary>
    /// 是否是长条。**独立于 EndTime**：允许「长度 0 的 LN」（原文 <c>type=128</c> 且尾时间等于头时间），
    /// 这种谱面真实存在，不能被当成普通键改写。
    /// </summary>
    public bool IsLongNote { get; init; }

    /// <summary>打击音效位（原样保留回写；编辑器不改它，但不丢）。</summary>
    public int HitSound { get; init; }

    /// <summary>是否新连击段（原样保留）。</summary>
    public bool IsNewCombo { get; init; }

    /// <summary>连击偏移（原样保留）。</summary>
    public int ComboOffset { get; init; }

    /// <summary>采样组 / 附加组 / 自定义序号 / 音量 / 自定义文件名（原样保留）。</summary>
    public int SampleSet { get; init; }

    /// <summary>附加采样组（原样保留）。</summary>
    public int AdditionSet { get; init; }

    /// <summary>自定义采样序号（原样保留）。</summary>
    public int CustomIndex { get; init; }

    /// <summary>音量（原样保留）。</summary>
    public int Volume { get; init; }

    /// <summary>自定义采样文件名（原样保留）。</summary>
    public string SampleFileName { get; init; }

    /// <summary>是不是 LN 且真的有长度（拖尾巴时用它区分「零长度 LN」）。</summary>
    public bool HasLength => IsLongNote && EndTime > StartTime;

    /// <summary>等价于 EndTime 的「含尾」时间跨度（普通键为 0）。</summary>
    public double Duration => Math.Max(0, EndTime - StartTime);

    /// <summary>造一个普通键（保留全部采样字段）。</summary>
    public static EditorNote NewNote(int column, double time) => new()
    {
        Column = column,
        StartTime = time,
        EndTime = time,
        IsLongNote = false,
        SampleFileName = "",
    };

    /// <summary>造一个长条。</summary>
    public static EditorNote NewLongNote(int column, double start, double end)
    {
        if (end < start) (start, end) = (end, start);
        return new EditorNote
        {
            Column = column,
            StartTime = start,
            EndTime = end,
            IsLongNote = true,
            SampleFileName = "",
        };
    }

    /// <summary>改开始时间（普通键跟着一起动；LN 保持尾巴不动会变长度 —— 调用方按需要选）。</summary>
    public EditorNote WithStart(double start)
        => IsLongNote ? this with { StartTime = start } : this with { StartTime = start, EndTime = start };

    /// <summary>改结束时间（同时标记为 LN；结束早于开始会被夹到开始）。</summary>
    public EditorNote WithEnd(double end)
        => this with { EndTime = Math.Max(end, StartTime), IsLongNote = true };

    /// <summary>整体平移（普通键 / LN 都用）。</summary>
    public EditorNote Shift(double delta) => this with
    {
        StartTime = StartTime + delta,
        EndTime = IsLongNote ? EndTime + delta : StartTime + delta,
    };

    /// <summary>改列。</summary>
    public EditorNote WithColumn(int column) => this with { Column = column };

    /// <summary>调试用摘要。</summary>
    public override string ToString()
        => IsLongNote
            ? $"{Column}列 {StartTime:0}~{EndTime:0} LN"
            : $"{Column}列 {StartTime:0}";
}
