using System;
using System.Collections.Generic;
using System.Linq;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>
/// osu!mania 谱面的工作模型（算法层只碰这里的类型，不直接依赖 OsuParsers）。
///
/// <para>早期版本 用自己手写的 <c>OsuFileV14</c> 读谱面；
/// 改写后读 / 写交给 NuGet 的 OsuParsers（见 <see cref="OsuBeatmapFile"/>），
/// 但转 K / 转 LN / 星数 / 等级这些算法仍然只在这个模型上跑，行为与早期版本一致。</para>
/// </summary>
public sealed class OsuManiaBeatmap
{
    /// <summary>.osu 文件格式版本（早期版本只处理 v14，新版交给库读，这里记录原值便于回写）。</summary>
    public int FormatVersion { get; set; } = 14;

    /// <summary>键数（谱面 [Difficulty] 的 CircleSize；早期版本写作 General.CircleSize）。</summary>
    public int KeyCount { get; set; } = 4;

    /// <summary>整体难度 OD（[Difficulty] 的 OverallDifficulty）。</summary>
    public double OverallDifficulty { get; set; }

    /// <summary>[General] 的 AudioFilename（只读展示 / 回写用，算法不改它）。</summary>
    public string AudioFilename { get; set; } = "";

    /// <summary>谱面元数据。</summary>
    public OsuBeatmapMetadata Metadata { get; set; } = new();

    /// <summary>时间点（红 / 绿线）。算法只读取红线的 BeatLength，不改写。</summary>
    public List<OsuTimingPoint> TimingPoints { get; } = new();

    /// <summary>所有打击物（普通键 + 长条）。</summary>
    public List<ManiaNote> HitObjects { get; set; } = new();

    /// <summary>
    /// 谱面星数。由 <see cref="OsuBeatmapFile"/> 加载时用 <c>StarRatingCalculator</c> 算好
    /// （与早期版本 <c>OsuFileV14.StarRating</c> 在构造时算一次相同；转谱后早期版本不会重算，这里同样不自动重算）。
    /// </summary>
    public double StarRating { get; set; }

    /// <summary>用当前模型的键数 / OD / 打击物重新算一次星数（与早期版本构造时同一入口）。</summary>
    public double CalculateStarRating(double rate = 1.0)
        => Difficulty.StarRatingCalculator.CalculateStarRating(HitObjects, OverallDifficulty, KeyCount, rate);

    /// <summary>
    /// 取 <paramref name="time"/> 时刻生效的时间点（早期版本 <c>OsuFileV14.TimingPointAt</c> 的逐行改写）。
    ///
    /// <para>行为照旧：只考虑 <see cref="OsuTimingPoint.BeatLength"/> &gt;= 0 的红线；
    /// 只有一个红线就直接返回；否则找 <c>Time &lt;= time &lt; 下一条.Time</c> 的区间；
    /// 都匹配不上就返回最后一条红线。</para>
    /// <para>早期版本在 TimingPoints 为空、或只有绿线（没有红线）时会抛异常，这里保持“抛异常”的语义
    /// （调用方按早期版本习惯用 try/catch 兜底）。</para>
    /// </summary>
    public OsuTimingPoint TimingPointAt(double time)
    {
        if (TimingPoints.Count == 0)
        {
            throw new InvalidOperationException("TimingPoints is empty.");
        }

        var redPoints = TimingPoints.Where(tp => tp.BeatLength >= 0).OrderBy(tp => tp.Time).ToList();

        if (redPoints.Count == 0)
        {
            // 早期版本此时会去取 RedPoints[RedPoints.Count - 1]（-1）而抛 ArgumentOutOfRangeException；
            // 这里显式抛一个更清楚的异常，避免上层看到难以理解的越界错误。
            throw new InvalidOperationException("TimingPoints 里没有红线（BeatLength >= 0），无法取得节拍长度。");
        }

        if (redPoints.Count == 1)
        {
            return redPoints[0];
        }

        for (int i = 0; i < redPoints.Count - 1; i++)
        {
            if (redPoints[i].Time <= time && redPoints[i + 1].Time > time)
            {
                return redPoints[i];
            }
        }

        return redPoints[redPoints.Count - 1];
    }

    /// <summary>取 <paramref name="time"/> 时刻生效的节拍长度（毫秒）；等价于 <c>TimingPointAt(time).BeatLength</c>。</summary>
    public double BeatLengthAt(double time) => TimingPointAt(time).BeatLength;

    /// <summary>第一个时间点的时间（早期版本大量使用 <c>osu.TimingPoints.First().Time</c> 作为反算 offset）。</summary>
    public double FirstTimingPointTime => TimingPoints.Count > 0
        ? TimingPoints[0].Time
        : throw new InvalidOperationException("TimingPoints is empty.");

    /// <summary>支持的键数范围（早期版本转 K 只支持 1~10；3~10 有历史常量表）。</summary>
    public const int MinKeyCount = 1;
    /// <summary>支持的键数上限。</summary>
    public const int MaxKeyCount = 10;

    /// <summary>键数是否在支持范围内。</summary>
    public static bool IsSupportedKeyCount(int keys) => keys >= MinKeyCount && keys <= MaxKeyCount;

    /// <summary>
    /// 早期版本 <c>OsuFileV14.KeyX</c> 的列坐标常量表（3~10 键，逐值照搬）。
    /// 列坐标为 osu! 谱面 512 宽画布上的 x；早期版本用它给打击物换列。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int[]> KeyX = new Dictionary<int, int[]>
    {
        [3] = new[] { 85, 256, 426 },
        [4] = new[] { 64, 192, 320, 448 },
        [5] = new[] { 51, 153, 256, 358, 460 },
        [6] = new[] { 42, 128, 213, 298, 384, 469 },
        [7] = new[] { 36, 109, 182, 256, 329, 402, 475 },
        [8] = new[] { 32, 96, 160, 224, 288, 352, 416, 480 },
        [9] = new[] { 28, 85, 142, 199, 256, 312, 369, 426, 483 },
        [10] = new[] { 25, 76, 128, 179, 230, 281, 332, 384, 435, 486 },
    };

    /// <summary>
    /// 第 <paramref name="column"/> 列（0 基）在 <paramref name="keys"/> 键谱面里的 x 坐标。
    ///
    /// <para>3~10 键直接查早期版本 <see cref="KeyX"/> 常量表（保证与早期版本逐位一致）；
    /// 其它键数（1、2 键）用同一公式 <c>floor(512 * (column + 0.5) / keys)</c> 计算 ——
    /// 早期版本这里会 KeyNotFoundException（<see cref="KeyX"/> 表里没有 1k / 2k，属于早期版本 bug），已修掉。</para>
    /// </summary>
    public static int ColumnX(int keys, int column)
    {
        if (!IsSupportedKeyCount(keys))
        {
            throw new ArgumentOutOfRangeException(nameof(keys), keys, "键数需在 1~10 之间。");
        }
        if (column < 0 || column >= keys)
        {
            throw new ArgumentOutOfRangeException(nameof(column), column, $"列号需在 0~{keys - 1} 之间。");
        }

        if (keys is >= 3 and <= 10)
        {
            return KeyX[keys][column];
        }

        return (int)Math.Floor(512.0 * (column + 0.5) / keys);
    }
}

/// <summary>osu! 谱面元数据（对应早期版本 <c>Metadata</c> 结构体）。</summary>
public sealed class OsuBeatmapMetadata
{
    /// <summary>标题。</summary>
    public string Title { get; set; } = "";

    /// <summary>标题（Unicode）。</summary>
    public string TitleUnicode { get; set; } = "";

    /// <summary>艺术家。</summary>
    public string Artist { get; set; } = "";

    /// <summary>艺术家（Unicode）。</summary>
    public string ArtistUnicode { get; set; } = "";

    /// <summary>谱师（转谱后会被改写：替换或追加 " &amp; KeyConverter" / " &amp; LNTransformer"）。</summary>
    public string Creator { get; set; } = "";

    /// <summary>
    /// 难度名（.osu 文件 [Metadata] 的 <c>Version</c> 字段；早期版本叫 <c>Metadata.Difficulty</c>）。
    /// 转谱工具会在最前面插入 <c>[4To7C]</c> / <c>[LN-Lv3-C10]</c> 之类的前缀。
    /// </summary>
    public string Difficulty { get; set; } = "";

    /// <summary>来源。</summary>
    public string Source { get; set; } = "";

    /// <summary>标签（空格分隔的原始文本）。</summary>
    public string Tags { get; set; } = "";

    /// <summary>BeatmapID（早期版本是字符串，默认 "0"；这里用 int，默认 0）。</summary>
    public int BeatmapID { get; set; }

    /// <summary>BeatmapSetID（早期版本是字符串，默认 "-1"；这里用 int，默认 -1）。</summary>
    public int BeatmapSetID { get; set; } = -1;

    /// <summary>复制一份元数据（转谱工具会改 Creator / Difficulty，自检里也会用到）。</summary>
    public OsuBeatmapMetadata Clone() => (OsuBeatmapMetadata)MemberwiseClone();
}

/// <summary>时间点（早期版本 <c>TimingPoint</c> 结构体的简化版；算法只关心 Time / BeatLength / 红线判定）。</summary>
public sealed class OsuTimingPoint
{
    /// <summary>时间（毫秒，早期版本是 int）。</summary>
    public int Time { get; set; }

    /// <summary>节拍长度（毫秒）。绿线是负数（早期版本用 BeatLength &gt;= 0 判定红线）。</summary>
    public double BeatLength { get; set; }

    /// <summary>是否为继承时间点（绿线）。注意早期版本的红线判定用的是 BeatLength &gt;= 0，不是这个标记。</summary>
    public bool Inherited { get; set; }
}

/// <summary>
/// 打击物的采样信息（早期版本 <c>ManiaHitObject.HitSample</c> 字符串里那几个冒号分隔字段的简化类型）。
/// </summary>
public sealed class OsuNoteExtras
{
    /// <summary>采样组（0=auto/None，1=Normal，2=Soft，3=Drum）。</summary>
    public int SampleSet { get; set; }

    /// <summary>附加采样组。</summary>
    public int AdditionSet { get; set; }

    /// <summary>自定义采样序号。</summary>
    public int CustomIndex { get; set; }

    /// <summary>音量。</summary>
    public int Volume { get; set; }

    /// <summary>自定义采样文件名（可为空）。</summary>
    public string SampleFileName { get; set; } = "";

    /// <summary>复制一份。</summary>
    public OsuNoteExtras Clone() => (OsuNoteExtras)MemberwiseClone();
}

/// <summary>
/// mania 的一个打击物（普通键或长条）。
///
/// <para><b>与早期版本 <c>ManiaHitObject</c> 结构体的差异</b>：早期版本是可变结构体，代码里靠“复制一份再改”
/// 来避免互相影响；这里是 class，但保留了 <see cref="Clone"/> / <see cref="ToNote"/> 两个复制入口，
/// 改写后的算法在每个“早期版本会发生结构体复制”的位置都显式复制，输出与早期版本一致。</para>
///
/// <para><b>IsLongNote 的语义</b>：早期版本用 <c>Type == 128 || StartTime != EndTime</c> 判定 LN，
/// 其中“type=128 但长度 0”也是一种 LN（长条被压成 0 长度）。这里用独立的
/// <see cref="IsLongNote"/> 保留这个语义：<see cref="EndTime"/> 的 setter 与早期版本一样，
/// 赋值不等于 <see cref="StartTime"/> 就变成 LN，赋值等于 StartTime 就变回普通键。</para>
/// </summary>
public sealed class ManiaNote
{
    private int _endTime;
    private bool _isLongNote;

    /// <summary>列号（0 基）。</summary>
    public int Column { get; set; }

    /// <summary>开始时间（毫秒）。</summary>
    public int StartTime { get; set; }

    /// <summary>
    /// 结束时间（毫秒）。普通键等于 <see cref="StartTime"/>；LN 大于开始时间
    /// （也允许“type=128 且长度 0”的 LN：<see cref="IsLongNote"/> 为 true 但两端相等）。
    ///
    /// <para>setter 行为照早期版本：赋值不等于 StartTime → 变成长条；赋值等于 StartTime → 变回普通键。</para>
    /// </summary>
    public int EndTime
    {
        get => _endTime;
        set
        {
            _endTime = value;
            _isLongNote = value != StartTime;
        }
    }

    /// <summary>
    /// 是否为长条。早期版本 <c>IsLN =&gt; StartTime != EndTime || Type == 128</c>；
    /// 这里允许 EndTime == StartTime 但仍为 LN（保留“type=128 长度 0”的旧语义）。
    /// </summary>
    public bool IsLongNote
    {
        get => _isLongNote;
        set => _isLongNote = value;
    }

    /// <summary>旧的写法（原有代码里的 <c>IsLN</c>），等价于 <see cref="IsLongNote"/>。</summary>
    public bool IsLN => _isLongNote;

    /// <summary>打击音效（早期版本是按位叠加的 int：0=None、1=Normal、2=Whistle、4=Finish、8=Clap）。</summary>
    public int HitSound { get; set; }

    /// <summary>是否新连击段（早期版本 <c>IsNewCombo</c>）。</summary>
    public bool IsNewCombo { get; set; }

    /// <summary>连击偏移（早期版本 <c>ComboOffset</c>）。</summary>
    public int ComboOffset { get; set; }

    /// <summary>采样信息。</summary>
    public OsuNoteExtras Extras { get; set; } = new();

    /// <summary>构造一个普通键（开始时间 = 结束时间）。</summary>
    public ManiaNote()
    {
    }

    /// <summary>构造一个普通键（开始时间 = 结束时间）。</summary>
    public ManiaNote(int column, int startTime)
    {
        Column = column;
        StartTime = startTime;
        EndTime = startTime;
    }

    /// <summary>
    /// 构造一个打击物。结束时间等于开始时间时是普通键，否则是长条
    /// （对应早期版本 <c>new ManiaHitObject(column, keys, startTime, endTime: endTime)</c>）。
    /// </summary>
    public ManiaNote(int column, int startTime, int endTime)
    {
        Column = column;
        StartTime = startTime;
        EndTime = endTime;
    }

    /// <summary>
    /// 构造一个打击物并显式指定是否长条（保留“type=128 但长度 0”的 LN 用）。
    /// </summary>
    public ManiaNote(int column, int startTime, int endTime, bool isLongNote)
    {
        Column = column;
        StartTime = startTime;
        _endTime = endTime;
        IsLongNote = isLongNote;
    }

    /// <summary>复制一份（含 <see cref="Extras"/> 的深拷贝）。</summary>
    public ManiaNote Clone()
    {
        var copy = (ManiaNote)MemberwiseClone();
        copy.Extras = Extras.Clone();
        return copy;
    }

    /// <summary>
    /// 把 LN 压成普通键（早期版本 <c>ToNote()</c>：复制一份并把 EndTime 设回 StartTime）。
    /// 不修改原对象。
    /// </summary>
    public ManiaNote ToNote()
    {
        var note = Clone();
        note.EndTime = note.StartTime;
        return note;
    }

    /// <summary>一行便于调试的摘要。</summary>
    public override string ToString()
        => $"{Column}列 {StartTime}~{EndTime}{(IsLongNote ? "(LN)" : "")}";
}
