namespace YuLiangToolkit.Tools.Vsrg.Core;

/// <summary>
/// 一个打击物（普通键或面条）。**不可变值类型**：解析后不再改，渲染与判定都只读它。
/// </summary>
/// <param name="Column">轨道号（0 基）。</param>
/// <param name="StartMs">开始时间（毫秒，谱面时间）。</param>
/// <param name="EndMs">结束时间（毫秒）；普通键等于 <paramref name="StartMs"/>。</param>
/// <param name="IsLongNote">是不是面条（osu! 的 <c>type=128</c>）。</param>
public readonly record struct ChartNote(int Column, double StartMs, double EndMs, bool IsLongNote)
{
    /// <summary>面条长度（毫秒）；普通键为 0。</summary>
    public double LengthMs => IsLongNote ? Math.Max(0, EndMs - StartMs) : 0;
}

/// <summary>
/// 一张 osu!mania 谱面的**只读模型**：元数据 + 全部打击物。解析由 <see cref="OsuChartReader"/> 负责。
///
/// <para><b>为什么不用第三方解析库</b>：这里只需要「读」，而且同一批文件要能并行解析（扫描谱面库时）。
/// OsuParsers 的解码器是静态状态、非线程安全（见 <c>Tools.OsuMania/Core/OsuBeatmapFile.cs</c> 的注释），
/// 用它反而要给扫描加锁；manis 的 .osu 格式足够简单，手写一个只读解析器更直接、也能无头自检。</para>
/// </summary>
public sealed class Chart
{
    /// <summary>谱面文件完整路径（<c>.osu</c>）。</summary>
    public string FilePath { get; init; } = "";

    /// <summary>标题（有 Unicode 版本时取 Unicode）。</summary>
    public string Title { get; init; } = "";

    /// <summary>艺术家（有 Unicode 版本时取 Unicode）。</summary>
    public string Artist { get; init; } = "";

    /// <summary>谱师。</summary>
    public string Creator { get; init; } = "";

    /// <summary>难度名（osu! 的 <c>[Metadata] Version</c>）。</summary>
    public string DifficultyName { get; init; } = "";

    /// <summary>来源曲目 / 作品。</summary>
    public string Source { get; init; } = "";

    /// <summary>标签（原始文本）。</summary>
    public string Tags { get; init; } = "";

    /// <summary>键数（1~10）。</summary>
    public int KeyCount { get; init; } = 4;

    /// <summary>OD（只用于展示；判定窗口由本工具的判定档决定）。</summary>
    public double OverallDifficulty { get; init; }

    /// <summary><c>[General] AudioFilename</c> 的原样值（可能带子目录）。</summary>
    public string AudioFileName { get; init; } = "";

    /// <summary>音频文件的完整路径（解析后已确认存在；没有就是空串）。</summary>
    public string AudioPath { get; init; } = "";

    /// <summary><c>[Events]</c> 里声明的背景图文件名（没有就是空串）。</summary>
    public string BackgroundFileName { get; init; } = "";

    /// <summary>背景图完整路径（已确认存在；没有就是空串）。</summary>
    public string BackgroundPath { get; init; } = "";

    /// <summary>全部打击物（按开始时间升序，同时刻按轨道升序）。</summary>
    public IReadOnlyList<ChartNote> Notes { get; init; } = Array.Empty<ChartNote>();

    /// <summary>普通键数量。</summary>
    public int NoteCount { get; init; }

    /// <summary>面条数量。</summary>
    public int LongNoteCount { get; init; }

    /// <summary>第一个打击物的时间（毫秒；空谱为 0）。</summary>
    public double FirstNoteMs { get; init; }

    /// <summary>最后一个打击物（含面条尾巴）的结束时间（毫秒；空谱为 0）。</summary>
    public double LastNoteMs { get; init; }

    /// <summary>「艺术家 - 标题」（界面标题行）。</summary>
    public string TitleLine => string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";

    /// <summary>「难度名 · 4K」这样的副标题。</summary>
    public string DifficultyLine
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(DifficultyName) ? "未命名难度" : DifficultyName;
            return $"{name} · {KeyCount}K";
        }
    }

    /// <summary>能不能真的开始游玩（要有音频文件）。</summary>
    public bool CanPlay => AudioPath.Length > 0;

    /// <summary>不能游玩的原因（能游玩时为空串）。</summary>
    public string PlayBlockReason
        => CanPlay ? "" : "这首歌缺音频文件（谱面 [General] AudioFilename 指向的文件不在 Mapset 文件夹里）。";

    /// <summary>谱面长度（秒，按最后一个 note 的结束时间算）。</summary>
    public double LengthSeconds => LastNoteMs / 1000.0;

    /// <summary>谱面长度的 <c>m:ss</c> 文本。</summary>
    public string LengthText
    {
        get
        {
            var total = (int)Math.Round(LengthSeconds);
            return $"{total / 60}:{total % 60:00}";
        }
    }

    /// <summary>调试用摘要。</summary>
    public override string ToString()
        => $"{TitleLine} [{DifficultyName}] {KeyCount}K {Notes.Count} notes";
}
