using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using OsuParsers.Beatmaps;
using OsuParsers.Decoders;
using OsuParsers.Enums;
using OsuParsers.Enums.Beatmaps;
using OsuParsers.Beatmaps.Objects;
using YuLiangToolkit.Tools.OsuMania.Core.Difficulty;
// 本项目的 ManiaNote 与 OsuParsers 的同名类型会撞名（本命名空间优先），这里给解析库的两个类型起别名。
using ParserManiaNote = OsuParsers.Beatmaps.Objects.Mania.ManiaNote;
using ParserHoldNote = OsuParsers.Beatmaps.Objects.Mania.ManiaHoldNote;
using ParserExtras = OsuParsers.Beatmaps.Objects.Extras;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>
/// .osu 文件的解析 / 保存桥：OsuParsers（第三方解析库）↔ 本项目的 <see cref="OsuManiaBeatmap"/> 工作模型。
///
/// <para><b>为什么要有这一层</b>：早期版本自己手写解析器（<c>OsuFileV14</c>）；新版用 OsuParsers 读 / 写，
/// 但转谱 / 星数等算法不应该直接依赖第三方类型，所以统一在这里做双向映射。
/// 算法层只看到 <see cref="OsuManiaBeatmap"/> / <see cref="ManiaNote"/>。</para>
///
/// <para><b>线程安全</b>：OsuParsers 的解码 / 编码是 static 状态、非线程安全，本类的
/// <see cref="TryLoad"/> 与 <see cref="SaveTo"/> 内部用同一把静态锁串行化；界面 / 批量层也不要并行加载。
/// 转谱算法本身（<see cref="KeyConverterEngine"/> / <see cref="LnTransformerEngine"/>）是纯内存计算，
/// 但为了可复现的随机顺序，同一局里请单线程按文件顺序跑。</para>
///
/// <para><b>写文件不是覆盖原文件</b>（照搬早期版本 <c>OsuFileV14.WriteFile</c>）：
/// 输出到「原文件所在目录」+「按元数据拼出来的文件名」：
/// <c>{Artist} - {Title} ({Creator}) [{Difficulty}]</c>（去掉 Windows 非法字符 <c>\ / : * ? " &lt; &gt; |</c>）+ <c>.osu</c>。
/// 因为转谱工具会改 Creator / Difficulty，正常会自然生成新文件，不会覆盖原谱。</para>
/// </summary>
public sealed class OsuBeatmapFile
{
    /// <summary>Windows 文件名非法字符（早期版本 <c>OsuFileV14.ForbiddenChar</c>，逐字符照搬）。</summary>
    public static readonly char[] ForbiddenChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>早期版本固定扩展名。</summary>
    public const string FileExtension = ".osu";

    /// <summary>
    /// OsuParsers 的解码器 / 编码器内部是 <b>static 状态、非线程安全</b>：
    /// 两个线程同时 <c>BeatmapDecoder.Decode</c> 会串台（实测出现过 <c>FormatException: 'Combo2 : 0'</c>、
    /// 结果互相污染）。本桥用同一把锁把「解码」和「保存」都串行化；批量入口 <see cref="OsuFileBatch"/>
    /// 也保持逐个处理（不要改成 Parallel）。界面层也不要在多个线程里同时加载谱面。
    /// </summary>
    private static readonly object IoLock = new();

    private OsuBeatmapFile(string filePath, Beatmap source, OsuManiaBeatmap beatmap)
    {
        FilePath = filePath;
        Directory = Path.GetDirectoryName(filePath) ?? "";
        Source = source;
        Beatmap = beatmap;
    }

    /// <summary>原文件完整路径。</summary>
    public string FilePath { get; }

    /// <summary>原文件所在目录（输出文件写在这里）。</summary>
    public string Directory { get; }

    /// <summary>工作模型（算法直接改它，改完调用 <see cref="Save"/>）。</summary>
    public OsuManiaBeatmap Beatmap { get; }

    /// <summary>
    /// 原始 OsuParsers 谱面对象。算法层不需要它；界面想读一些工作模型没收录的字段
    /// （比如 AR / 事件 / 颜色）时可以从这里取。
    /// </summary>
    public Beatmap Source { get; }

    /// <summary>谱面星数（加载时算好；早期版本 <c>OsuFileV14.StarRating</c> 同样是加载时算一次）。</summary>
    public double StarRating => Beatmap.StarRating;

    /// <summary>原文件名（不含路径）。</summary>
    public string OriginalFileName => Path.GetFileName(FilePath);

    /// <summary>
    /// 加载一个 .osu 文件。失败不抛异常，返回 false 并给出中文原因
    /// （非 mania 谱面 / 键数超出 1~10 / 文件损坏读不了都会走到这里）。
    /// </summary>
    /// <param name="path">.osu 文件路径。</param>
    /// <param name="file">加载成功时的结果；失败为 null。</param>
    /// <param name="error">失败原因（中文）；成功为 null。</param>
    public static bool TryLoad(string? path, out OsuBeatmapFile? file, out string? error)
    {
        file = null;
        error = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            error = "文件路径为空。";
            return false;
        }

        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        Beatmap source;
        try
        {
            // OsuParsers 解码器是 static 状态、非线程安全：必须串行（见 IoLock 注释）。
            lock (IoLock)
            {
                source = BeatmapDecoder.Decode(path);
            }
        }
        catch (Exception ex)
        {
            error = $"谱面解析失败（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }

        if (source is null)
        {
            error = "谱面解析失败：解析结果为 null。";
            return false;
        }

        if (source.GeneralSection.ModeId != (int)Ruleset.Mania)
        {
            error = $"不是 osu!mania 谱面（Mode={source.GeneralSection.ModeId}，需要 3）。";
            return false;
        }

        int keys = KeyCountOf(source);
        if (!OsuManiaBeatmap.IsSupportedKeyCount(keys))
        {
            error = $"键数 {keys} 不受支持（仅支持 {OsuManiaBeatmap.MinKeyCount}~{OsuManiaBeatmap.MaxKeyCount} 键）。";
            return false;
        }

        var beatmap = new OsuManiaBeatmap
        {
            FormatVersion = source.Version,
            KeyCount = keys,
            OverallDifficulty = source.DifficultySection.OverallDifficulty,
            AudioFilename = source.GeneralSection.AudioFilename ?? "",
        };

        var meta = source.MetadataSection;
        beatmap.Metadata.Title = meta.Title ?? "";
        beatmap.Metadata.TitleUnicode = meta.TitleUnicode ?? "";
        beatmap.Metadata.Artist = meta.Artist ?? "";
        beatmap.Metadata.ArtistUnicode = meta.ArtistUnicode ?? "";
        beatmap.Metadata.Creator = meta.Creator ?? "";
        beatmap.Metadata.Difficulty = meta.Version ?? "";
        beatmap.Metadata.Source = meta.Source ?? "";
        beatmap.Metadata.Tags = meta.TagsString ?? "";
        beatmap.Metadata.BeatmapID = meta.BeatmapID;
        beatmap.Metadata.BeatmapSetID = meta.BeatmapSetID;

        foreach (var tp in source.TimingPoints)
        {
            beatmap.TimingPoints.Add(new OsuTimingPoint
            {
                Time = tp.Offset,
                BeatLength = tp.BeatLength,
                Inherited = tp.Inherited,
            });
        }

        foreach (var ho in source.HitObjects)
        {
            // OsuParsers 解码 mania 打击物时：普通键是 ManiaNote、长条是 ManiaHoldNote（继承 ManiaNote）。
            if (ho is not ParserManiaNote mania)
            {
                continue;
            }

            // 早期版本读文件时：actualColumn = floor(x * CircleSize / 512)。库的 GetColumn 就是同一公式。
            int column = mania.GetColumn(keys);
            // 早期版本对越界列号会直接抛异常（KeyX 越界）；这里夹取到合法列，避免一个坏 x 让整批文件失败。
            column = Math.Clamp(column, 0, keys - 1);

            bool isLongNote = ho is ParserHoldNote;
            int endTime = isLongNote ? ho.EndTime : ho.StartTime;

            var note = new ManiaNote(column, ho.StartTime, endTime, isLongNote)
            {
                HitSound = (int)ho.HitSound,
                IsNewCombo = ho.IsNewCombo,
                ComboOffset = ho.ComboOffset,
            };

            var extras = ho.Extras ?? new ParserExtras();
            note.Extras.SampleSet = (int)extras.SampleSet;
            note.Extras.AdditionSet = (int)extras.AdditionSet;
            note.Extras.CustomIndex = extras.CustomIndex;
            note.Extras.Volume = extras.Volume;
            note.Extras.SampleFileName = extras.SampleFileName ?? "";

            beatmap.HitObjects.Add(note);
        }

        // 与早期版本 OsuFileV14 构造时一致：加载后立刻算一次星数（失败返回 0）。
        try
        {
            beatmap.StarRating = StarRatingCalculator.CalculateStarRating(beatmap);
        }
        catch
        {
            beatmap.StarRating = 0;
        }

        file = new OsuBeatmapFile(path, source, beatmap);
        return true;
    }

    /// <summary>加载一个 .osu 文件；失败抛 <see cref="InvalidDataException"/>（带中文原因），自检 / 批量里请用 <see cref="TryLoad"/>。</summary>
    public static OsuBeatmapFile Load(string path)
    {
        if (!TryLoad(path, out var file, out var error))
        {
            throw new InvalidDataException(error ?? "谱面加载失败。");
        }

        return file!;
    }

    /// <summary>读一个文件里的键数（早期版本 <c>(int)General.CircleSize</c>，这里同样截断取整）。</summary>
    private static int KeyCountOf(Beatmap source) => (int)source.DifficultySection.CircleSize;

    /// <summary>
    /// 按早期版本规则拼输出文件名：<c>{Artist} - {Title} ({Creator}) [{Difficulty}].osu</c>，去掉非法字符。
    /// </summary>
    public string BuildOutputFileName()
    {
        var meta = Beatmap.Metadata;
        string original = meta.Artist + " - " + meta.Title + " (" + meta.Creator + ") " + "[" + meta.Difficulty + "]";
        string safe = string.Concat(original.Where(c => !ForbiddenChars.Contains(c)));
        return safe + FileExtension;
    }

    /// <summary>按早期版本规则拼输出文件完整路径（写回原文件所在目录）。</summary>
    public string BuildOutputPath() => Path.Combine(Directory, BuildOutputFileName());

    /// <summary>
    /// 把工作模型写回谱面并保存：输出到原目录 + 按元数据拼的文件名（早期版本行为）。
    /// </summary>
    /// <returns>实际写出的文件完整路径。</returns>
    public string Save() => SaveTo(BuildOutputPath());

    /// <summary>
    /// 把工作模型写回谱面并保存到指定路径（自检用；父目录不存在会自动创建）。
    /// </summary>
    /// <returns>实际写出的文件完整路径。</returns>
    public string SaveTo(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("输出路径为空。", nameof(outputPath));
        }

        if (!OsuManiaBeatmap.IsSupportedKeyCount(Beatmap.KeyCount))
        {
            throw new InvalidOperationException($"键数 {Beatmap.KeyCount} 不受支持（仅支持 1~10 键）。");
        }

        ApplyToSource();

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            System.IO.Directory.CreateDirectory(dir);
        }

        // 编码器同样是 static 状态、非线程安全，与解码共用同一把锁。
        lock (IoLock)
        {
            Source.Save(outputPath);
        }

        return outputPath;
    }

    /// <summary>把工作模型里工具会改的字段同步回 OsuParsers 的谱面对象。</summary>
    private void ApplyToSource()
    {
        var beatmap = Beatmap;

        Source.Version = beatmap.FormatVersion;
        Source.GeneralSection.ModeId = (int)Ruleset.Mania;
        Source.GeneralSection.Mode = Ruleset.Mania;
        Source.GeneralSection.AudioFilename = beatmap.AudioFilename ?? "";
        Source.DifficultySection.CircleSize = beatmap.KeyCount;
        Source.DifficultySection.OverallDifficulty = (float)beatmap.OverallDifficulty;

        var meta = beatmap.Metadata;
        Source.MetadataSection.Title = meta.Title ?? "";
        Source.MetadataSection.TitleUnicode = meta.TitleUnicode ?? "";
        Source.MetadataSection.Artist = meta.Artist ?? "";
        Source.MetadataSection.ArtistUnicode = meta.ArtistUnicode ?? "";
        Source.MetadataSection.Creator = meta.Creator ?? "";
        Source.MetadataSection.Version = meta.Difficulty ?? "";
        Source.MetadataSection.Source = meta.Source ?? "";
        Source.MetadataSection.TagsString = meta.Tags ?? "";
        Source.MetadataSection.BeatmapID = meta.BeatmapID;
        Source.MetadataSection.BeatmapSetID = meta.BeatmapSetID;

        // 重建打击物：注意必须把 HitSound / Extras / IsNewCombo / ComboOffset 一起带过去，
        // 否则 OsuParsers 写出时会按对象字段重新生成第 5 列（打击音效），全谱的 hitsound 会变成 Normal。
        var objects = new List<OsuParsers.Beatmaps.Objects.HitObject>(beatmap.HitObjects.Count);
        foreach (var note in beatmap.HitObjects)
        {
            int column = Math.Clamp(note.Column, 0, beatmap.KeyCount - 1);
            var position = new Vector2(OsuManiaBeatmap.ColumnX(beatmap.KeyCount, column), 192);
            var extras = new ParserExtras(
                (SampleSet)note.Extras.SampleSet,
                (SampleSet)note.Extras.AdditionSet,
                note.Extras.CustomIndex,
                note.Extras.Volume,
                note.Extras.SampleFileName ?? "");
            var hitSound = (HitSoundType)note.HitSound;

            if (note.IsLongNote)
            {
                // 长条（含“type=128 但长度 0”的 LN）：写出 type=128，第 6 列第一段是结束时间。
                objects.Add(new ParserHoldNote(position, note.StartTime, note.EndTime, hitSound, extras, note.IsNewCombo, note.ComboOffset));
            }
            else
            {
                // 普通键：早期版本写 type=1（结束时间字段等于开始时间）。
                objects.Add(new ParserManiaNote(position, note.StartTime, note.StartTime, hitSound, extras, note.IsNewCombo, note.ComboOffset));
            }
        }

        Source.HitObjects = objects;
    }
}
