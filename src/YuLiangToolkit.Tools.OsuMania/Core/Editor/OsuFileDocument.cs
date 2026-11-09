using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// **手写的 .osu 读写器**（编辑器专用）。只精确重建 <c>[TimingPoints]</c> 与 <c>[HitObjects]</c> 两段，
/// 其它段落 / 未知键 / 注释**原样逐行保留**。
///
/// <para><b>为什么不用 OsuParsers</b>（项目里另外三个 osu 工具用它）：实测它对一张谱面
/// 解码 → 保存后会 <b>丢掉不认识的键</b>（例如 <c>[General]</c> 里的自定义键、注释行），
/// 还会给 <c>[Events]</c> 补一堆空的 storyboard 行。批量转换工具不在乎这些（输出是派生新谱），
/// 但**编辑器是改用户原谱**，丢字段不可接受。这里改成「按行编辑」：读进内存保留原始行，
/// 保存时只替换两段的正文。</para>
///
/// <para><b>解析严格度</b>：坏行跳过而不是抛异常（一个格式小错误的谱面也要能打开），
/// 但会记进 <see cref="EditorBeatmap.LoadWarnings"/>，界面上如实显示。</para>
/// </summary>
public static class OsuFileDocument
{
    /// <summary>osu! 列坐标画布宽度（mania 的 x 在 0~512 之间）。</summary>
    public const int PlayfieldWidth = 512;

    /// <summary>
    /// 读一个 .osu 文件。失败抛 <see cref="InvalidDataException"/>（带中文原因）。
    /// </summary>
    public static EditorBeatmap Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("文件路径为空。");
        if (!File.Exists(path)) throw new InvalidDataException($"文件不存在：{path}");

        string[] lines;
        try
        {
            // 谱面里有中文元数据：全部按 UTF-8 读，非法字节用替换符兜住（不抛异常）
            lines = File.ReadAllLines(path, new UTF8Encoding(false, false));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"读取失败（{ex.GetType().Name}）：{ex.Message}", ex);
        }

        return Parse(lines, path);
    }

    /// <summary>从行数组解析（自检直接喂字符串用）。</summary>
    public static EditorBeatmap Parse(IReadOnlyList<string> lines, string sourcePath)
    {
        var map = new EditorBeatmap { FilePath = sourcePath };
        map.OriginalLines.AddRange(lines);

        var section = "";
        var timingStart = -1;
        var timingEnd = -1;
        var objectsStart = -1;
        var objectsEnd = -1;
        string? backgroundFileName = null;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i] ?? "";
            var line = raw.Trim();

            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                var name = close > 1 ? line[1..close] : line;
                NameSection(name, ref section, ref timingStart, ref timingEnd, ref objectsStart, ref objectsEnd, i);
                continue;
            }

            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            switch (section)
            {
                case "General":
                    ParseKeyValue(map.General, line, "General");
                    break;
                case "Metadata":
                    ParseKeyValue(map.Metadata, line, "Metadata");
                    break;
                case "Difficulty":
                    ParseKeyValue(map.Difficulty, line, "Difficulty");
                    break;
                case "Editor":
                    ParseKeyValue(map.Editor, line, "Editor");
                    break;
                case "TimingPoints":
                    ParseTimingPoint(map, line, i);
                    break;
                case "HitObjects":
                    ParseHitObject(map, line, i);
                    break;
                case "Events":
                    // 只关心背景图那一行；其它（break / storyboard）原样留着，保存时不动
                    if (backgroundFileName is null)
                    {
                        backgroundFileName = BeatmapBackground.ParseBackgroundLine(line);
                    }
                    break;
                default:
                    if (TryParseKeyValue(line, out var key, out var value))
                    {
                        map.OtherSections.GetOrAdd(section).Add((key, value));
                    }
                    break;
            }
        }

        map.TimingSectionStart = timingStart + 1;
        map.TimingSectionEnd = timingEnd < 0 ? lines.Count : timingEnd;
        map.ObjectsSectionStart = objectsStart + 1;
        map.ObjectsSectionEnd = objectsEnd < 0 ? lines.Count : objectsEnd;

        // 键数 = CircleSize（mania 的 Keys），夹到 1~10
        var keys = 4;
        if (map.Difficulty.TryGetValue("CircleSize", out var cs) &&
            double.TryParse(cs, NumberStyles.Float, CultureInfo.InvariantCulture, out var keysValue))
        {
            keys = (int)Math.Round(keysValue);
        }
        map.KeyCount = Math.Clamp(keys, 1, 10);

        map.ModeId = map.General.TryGetValue("Mode", out var mode) &&
                     int.TryParse(mode, NumberStyles.Integer, CultureInfo.InvariantCulture, out var modeId)
            ? modeId
            : 0;

        // 背景图：记下文件名，并解析成实际存在的完整路径（相对谱面目录）
        if (!string.IsNullOrWhiteSpace(backgroundFileName))
        {
            map.BackgroundFileName = backgroundFileName!;
            var chartDir = Path.GetDirectoryName(sourcePath) ?? "";
            map.BackgroundPath = BeatmapBackground.ResolveExisting(chartDir, backgroundFileName) ?? "";
        }

        map.RefreshIndexes();
        return map;
    }

    private static void NameSection(string name, ref string section, ref int timingStart, ref int timingEnd,
        ref int objectsStart, ref int objectsEnd, int lineIndex)
    {
        // 上一段收尾
        switch (section)
        {
            case "TimingPoints" when timingEnd < 0:
                timingEnd = lineIndex;
                break;
            case "HitObjects" when objectsEnd < 0:
                objectsEnd = lineIndex;
                break;
        }

        section = name;

        switch (name)
        {
            case "TimingPoints" when timingStart < 0:
                timingStart = lineIndex;
                break;
            case "HitObjects" when objectsStart < 0:
                objectsStart = lineIndex;
                break;
        }
    }

    private static void ParseKeyValue(Dictionary<string, string> target, string line, string section)
    {
        if (TryParseKeyValue(line, out var key, out var value)) target[key] = value;
        else _ = section;
    }

    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0)
        {
            key = "";
            value = "";
            return false;
        }

        key = line[..colon].Trim();
        value = line[(colon + 1)..].Trim();
        return key.Length > 0;
    }

    /// <summary>解析 <c>time,beatLength,timeSignature,sampleSet,customSampleSet,volume,uninherited,effects</c>。</summary>
    private static void ParseTimingPoint(EditorBeatmap map, string line, int lineIndex)
    {
        var parts = line.Split(',');
        if (parts.Length < 2)
        {
            map.LoadWarnings.Add($"第 {lineIndex + 1} 行时间点字段不足，已跳过");
            return;
        }

        if (!TryNumber(parts[0], out var time) || !TryNumber(parts[1], out var beatLength))
        {
            map.LoadWarnings.Add($"第 {lineIndex + 1} 行时间点时间 / 长度无法解析，已跳过");
            return;
        }

        var uninherited = parts.Length > 6
            ? (int)TryNumberOrDefault(parts[6], 1) != 0
            : beatLength >= 0; // 老格式没有第 7 列：按 BeatLength 正负判

        var point = new EditorTimingPoint
        {
            Time = time,
            BeatLength = beatLength,
            Uninherited = uninherited,
            TimeSignature = parts.Length > 2 ? (int)TryNumberOrDefault(parts[2], 4) : 4,
            SampleSet = parts.Length > 3 ? (int)TryNumberOrDefault(parts[3], 0) : 0,
            CustomSampleSet = parts.Length > 4 ? (int)TryNumberOrDefault(parts[4], 0) : 0,
            Volume = parts.Length > 5 ? (int)TryNumberOrDefault(parts[5], 100) : 100,
            Effects = parts.Length > 7 ? (int)TryNumberOrDefault(parts[7], 0) : 0,
        };

        map.TimingPoints.Add(point);
    }

    /// <summary>解析 mania 打击物：<c>x,y,time,type,hitSound,endTime:hitSample</c>。</summary>
    private static void ParseHitObject(EditorBeatmap map, string line, int lineIndex)
    {
        var parts = line.Split(',');
        if (parts.Length < 4)
        {
            map.LoadWarnings.Add($"第 {lineIndex + 1} 行打击物字段不足，已跳过");
            return;
        }

        if (!TryNumber(parts[0], out var x) || !TryNumber(parts[2], out var time) ||
            !int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type))
        {
            map.LoadWarnings.Add($"第 {lineIndex + 1} 行打击物无法解析，已跳过");
            return;
        }

        var column = Math.Clamp((int)Math.Floor(x * map.KeyCount / PlayfieldWidth), 0, map.KeyCount - 1);

        int hitSound = 0;
        if (parts.Length > 4) hitSound = (int)TryNumberOrDefault(parts[4], 0);

        // 第 6 列：普通键直接是 "hitSample"，长条是 "endTime:hitSample"（osu! 原文规定）
        var isLongNote = (type & 128) != 0;
        var sixth = parts.Length > 5 ? parts[5].Trim() : "";
        var endTime = time;
        var samplePart = sixth;

        if (isLongNote)
        {
            var colon = sixth.IndexOf(':');
            if (colon >= 0)
            {
                samplePart = sixth[(colon + 1)..];
                if (TryNumber(sixth[..colon], out var parsedEnd)) endTime = parsedEnd;
            }
            else
            {
                if (TryNumber(sixth, out var parsedEnd)) endTime = parsedEnd;
                samplePart = "";
            }
        }

        var note = new EditorNote
        {
            Column = column,
            StartTime = time,
            EndTime = endTime,
            IsLongNote = isLongNote,
            HitSound = hitSound,
            IsNewCombo = (type & 4) != 0,
            ComboOffset = (type >> 4) & 7,
            SampleFileName = "",
        };

        // hitSample = normalSet:additionSet:index:volume:filename
        if (samplePart.Length > 0)
        {
            var sample = samplePart.Split(':');
            note = note with
            {
                SampleSet = sample.Length > 0 ? (int)TryNumberOrDefault(sample[0], 0) : 0,
                AdditionSet = sample.Length > 1 ? (int)TryNumberOrDefault(sample[1], 0) : 0,
                CustomIndex = sample.Length > 2 ? (int)TryNumberOrDefault(sample[2], 0) : 0,
                Volume = sample.Length > 3 ? (int)TryNumberOrDefault(sample[3], 0) : 0,
                SampleFileName = sample.Length > 4 ? sample[4].Trim() : "",
            };
        }

        map.HitObjects.Add(note);
    }

    private static bool TryNumber(string text, out double value)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static double TryNumberOrDefault(string text, double fallback)
        => TryNumber(text, out var value) ? value : fallback;

    /// <summary>
    /// 写回文件：只替换 <c>[TimingPoints]</c> 与 <c>[HitObjects]</c> 的正文，其余行原样输出。
    ///
    /// <para>段落缺失时补在文件末尾（不会挤掉已有内容）。写临时文件再 `File.Replace`，
    /// 避免写到一半崩溃把原谱弄坏。</para>
    /// </summary>
    public static void Save(EditorBeatmap map, string path)
    {
        var text = BuildText(map);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".ylt-tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(false));

        if (File.Exists(path))
        {
            // 保留原文件的创建时间等属性无必要，直接覆盖（目标已核对过是用户选定的谱面）
            File.Replace(temp, path, null);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// 把 <see cref="EditorBeatmap.BackgroundFileName"/> 写回 <c>[Events]</c> 段。
    /// 没有背景文件名（空串）时不动原文件 —— 保留谱面原本的背景声明。
    /// </summary>
    private static void ApplyBackgroundLine(List<string> lines, EditorBeatmap map)
    {
        if (string.IsNullOrWhiteSpace(map.BackgroundFileName)) return;
        if (!BeatmapBackground.IsSafeFileName(map.BackgroundFileName)) return;

        var line = BeatmapBackground.BuildBackgroundLine(map.BackgroundFileName);
        if (!BeatmapBackground.TryReplaceBackgroundLine(lines, line))
        {
            // 谱面原本没有 [Events] 段：补一个
            BeatmapBackground.AppendEventsSection(lines, line);
        }
    }

    /// <summary>生成保存后的完整文本（自检直接断言文本）。</summary>
    public static string BuildText(EditorBeatmap map)
    {
        var sb = new StringBuilder(map.OriginalLines.Count * 32 + map.HitObjects.Count * 24);
        var lines = map.OriginalLines;

        // 背景图：用户换了图就改 [Events] 里那一行（没有 [Events] 段就补一个）。
        // 放在这里做，保存（Save → BuildText）与文本断言（自检）走同一条路径。
        ApplyBackgroundLine(lines, map);

        // 段落边界：用加载时记下的行号（指向段标题行与下一段标题行）
        var timingHeader = map.TimingSectionStart - 1;
        var objectsHeader = map.ObjectsSectionStart - 1;

        // 两段都有：先输出 HitObjects 之前的内容，替换两段正文
        if (timingHeader >= 0 && objectsHeader >= 0)
        {
            // 保证 timing 段在 objects 段之前（正常谱面都这样）
            if (timingHeader < objectsHeader)
            {
                AppendLines(sb, lines, 0, timingHeader);
                sb.Append("[TimingPoints]").Append('\n');
                AppendTimingLines(sb, map);
                sb.Append('\n');

                // ⚠ 只复制「TimingPoints 正文结束」到「HitObjects 标题」之间的内容：
                // 用 TimingSectionEnd 而不是 timingHeader + 1 当起点，否则旧的时间点正文会被复制进去，
                // 和上面新写的时间点重复（自检里踩过：16 条变 17 条）。
                AppendLines(sb, lines, map.TimingSectionEnd, objectsHeader);
                sb.Append("[HitObjects]").Append('\n');
                AppendObjectLines(sb, map);
                AppendLines(sb, lines, map.ObjectsSectionEnd, lines.Count);
                return sb.ToString();
            }
        }
        else if (timingHeader >= 0)
        {
            AppendLines(sb, lines, 0, timingHeader);
            sb.Append("[TimingPoints]").Append('\n');
            AppendTimingLines(sb, map);
            sb.Append('\n');
            AppendLines(sb, lines, map.TimingSectionEnd, lines.Count);
            return sb.ToString();
        }
        else if (objectsHeader >= 0)
        {
            AppendLines(sb, lines, 0, objectsHeader);
            sb.Append("[HitObjects]").Append('\n');
            AppendObjectLines(sb, map);
            AppendLines(sb, lines, map.ObjectsSectionEnd, lines.Count);
            return sb.ToString();
        }

        // 两段都不存在：原样输出 + 末尾追加
        AppendLines(sb, lines, 0, lines.Count);
        sb.Append('\n').Append("[TimingPoints]").Append('\n');
        AppendTimingLines(sb, map);
        sb.Append('\n').Append("[HitObjects]").Append('\n');
        AppendObjectLines(sb, map);
        return sb.ToString();
    }

    private static void AppendLines(StringBuilder sb, IReadOnlyList<string> lines, int from, int to)
    {        for (var i = from; i < to && i < lines.Count; i++)
        {
            sb.Append(lines[i]).Append('\n');
        }
    }

    private static void AppendTimingLines(StringBuilder sb, EditorBeatmap map)
    {
        foreach (var point in map.SortedTimingPoints())
        {
            sb.Append(FormatTimingPoint(point)).Append('\n');
        }
    }

    private static void AppendObjectLines(StringBuilder sb, EditorBeatmap map)
    {
        foreach (var note in map.SortedNotes())
        {
            sb.Append(FormatHitObject(note, map.KeyCount)).Append('\n');
        }
    }

    /// <summary>
    /// 一条时间点的文本。时间写整数毫秒（osu! 原文就是整数）；BeatLength 保留到 1e-6，
    /// 避免 <c>375.00000000000006</c> 这种浮点尾巴污染文件。
    /// </summary>
    public static string FormatTimingPoint(EditorTimingPoint point)
    {
        var time = (long)Math.Round(point.Time);
        var beatLength = Math.Round(point.BeatLength, 6);
        var signature = point.TimeSignature <= 0 ? 4 : point.TimeSignature;
        var volume = Math.Clamp(point.Volume, 0, 100);

        return string.Join(',',
            time.ToString(CultureInfo.InvariantCulture),
            beatLength.ToString("0.######", CultureInfo.InvariantCulture),
            signature.ToString(CultureInfo.InvariantCulture),
            point.SampleSet.ToString(CultureInfo.InvariantCulture),
            point.CustomSampleSet.ToString(CultureInfo.InvariantCulture),
            volume.ToString(CultureInfo.InvariantCulture),
            (point.Uninherited ? 1 : 0).ToString(CultureInfo.InvariantCulture),
            point.Effects.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>一条打击物的文本（列坐标用</summary>
    public static string FormatHitObject(EditorNote note, int keyCount)
    {
        var time = (long)Math.Round(note.StartTime);
        var x = ColumnX(keyCount, note.Column);

        // type：mania 普通键 1、长条 128；新连击段 +4，连击偏移 <<4
        var type = note.IsLongNote ? 128 : 1;
        if (note.IsNewCombo) type |= 4;
        type |= (note.ComboOffset & 7) << 4;

        var head = string.Join(',',
            x.ToString(CultureInfo.InvariantCulture),
            "192",
            time.ToString(CultureInfo.InvariantCulture),
            type.ToString(CultureInfo.InvariantCulture),
            note.HitSound.ToString(CultureInfo.InvariantCulture));

        // hitSample = normalSet:additionSet:index:volume:filename（最后一段是文件名，可以为空）
        var sample = string.Join(':',
            note.SampleSet.ToString(CultureInfo.InvariantCulture),
            note.AdditionSet.ToString(CultureInfo.InvariantCulture),
            note.CustomIndex.ToString(CultureInfo.InvariantCulture),
            note.Volume.ToString(CultureInfo.InvariantCulture),
            note.SampleFileName ?? "");

        // 普通键：x,y,time,type,hitSound,hitSample
        // ⚠ 别在 sample 后面再补一个 ":" —— 那样会写成 "0:0:0:0::"（多一个空段），
        // 虽然 osu! 能容忍，但文件里会留一个假的空文件名（自检抓到过）。
        if (!note.IsLongNote) return head + "," + sample;

        // 长条：x,y,time,type,hitSound,endTime:hitSample
        var endTime = (long)Math.Round(note.EndTime);
        if (endTime < time) endTime = time;
        return head + "," + endTime.ToString(CultureInfo.InvariantCulture) + ":" + sample;
    }

    /// <summary>列号 → osu! 画布 x（直接用项目里既有的 KeyX 常量表，保证与转谱工具逐位一致）。</summary>
    public static int ColumnX(int keyCount, int column)
    {
        var keys = Math.Clamp(keyCount, 1, 10);
        var col = Math.Clamp(column, 0, keys - 1);
        return OsuManiaBeatmap.ColumnX(keys, col);
    }
}

/// <summary>「其它段落」的键值（保真用：原样写回不需要，但读取时收集，便于以后扩展）。</summary>
public sealed class SectionKeyValueStore : Dictionary<string, List<(string Key, string Value)>>
{
    /// <summary>取某段落，没有就建一个。</summary>
    public List<(string Key, string Value)> GetOrAdd(string section)
    {
        if (TryGetValue(section, out var list)) return list;
        list = new List<(string, string)>();
        this[section] = list;
        return list;
    }
}
