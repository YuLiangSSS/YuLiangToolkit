using System;
using System.Collections.Generic;
using System.Linq;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>
/// 转 K（低 K 转高 K）参数。默认值与旧版界面控件默认值一致：
/// Seed=114514、Key=8、Gap=10、Clean=4、Blank=0、Creator 不勾选。
/// </summary>
public sealed class KeyConverterOptions
{
    /// <summary>随机种子文本（默认 "114514"；留空则每次随机）。固定种子转出来的结果可复现。</summary>
    public string SeedText { get; set; } = "114514";

    /// <summary>目标键数（旧版 Key 滑条 2~10；必须大于源键数，低转高）。</summary>
    public int TargetKeys { get; set; } = 8;

    /// <summary>空白列数（0~9；旧版 Blank 滑条。用来减轻高 K 密度，实际值会被夹到「目标键数 - 源键数」）。</summary>
    public int Blank { get; set; }

    /// <summary>清子弹用的等分数（0~16；旧版 Clean 滑条，例如 4 = 清掉 1/4 内的二连音）。</summary>
    public int Clean { get; set; } = 4;

    /// <summary>
    /// 转换间隔（0~20；旧版 Gap 滑条）。按 <c>(29998.8584 * e^(-0.3176 * Gap)) + 347.7248</c>
    /// 换算成毫秒；0 表示整张谱面一次处理完（间隔无限大）。
    /// </summary>
    public int Gap { get; set; } = 10;

    /// <summary>新谱师名（配合 <see cref="CreatorIsChecked"/> 使用）。</summary>
    public string Creator { get; set; } = "";

    /// <summary>勾选后才用 <see cref="Creator"/> 替换原谱师；否则旧版会在原谱师后面追加 " &amp; KeyConverter"。</summary>
    public bool CreatorIsChecked { get; set; }

    /// <summary>
    /// 只转换这些源键数的谱面（旧版 1k~9k 复选框；空 = 全部转换）。
    /// 不勾选时默认转换所有源键数（含 1k / 2k）。
    /// </summary>
    public IReadOnlyList<int> CheckKeys { get; set; } = Array.Empty<int>();
}

/// <summary>转 K 的结果。</summary>
public sealed class KeyConverterResult
{
    /// <summary>是否真的转换并改写了工作模型。</summary>
    public bool Converted { get; init; }

    /// <summary>跳过原因（中文）；<see cref="Converted"/> 为 true 时为 null。</summary>
    public string? SkipReason { get; init; }

    /// <summary>
    /// 输出文件路径。只有调用带保存的便捷入口（<c>Apply(OsuBeatmapFile, ...)</c> / <c>OsuFileBatch</c>）时才有值；
    /// 纯逻辑入口 <c>Apply(OsuManiaBeatmap, ...)</c> 不写文件，这里为 null。
    /// </summary>
    public string? OutputPath { get; init; }

    /// <summary>方便界面判断的成功结果。</summary>
    public static KeyConverterResult Success(string? outputPath = null) => new() { Converted = true, OutputPath = outputPath };

    /// <summary>方便界面判断的跳过结果。</summary>
    public static KeyConverterResult Skip(string reason) => new() { Converted = false, SkipReason = reason };
}

/// <summary>
/// 转 K 引擎（旧版 <c>KeyConverter.xaml.cs</c> 的 <c>ApplyToBeatmap</c> + <c>ProcessArea</c> 逐行移植）。
///
/// <para><b>与旧版的区别</b>：旧版一进来就读文件、转完直接写文件；这里只改
/// <see cref="OsuManiaBeatmap"/>（列号 / 时间 / 键数 / 元数据），写文件交给
/// <see cref="OsuBeatmapFile.Save"/>，方便批量、预览和自检。</para>
///
/// <para><b>行为照搬</b>（包括这些“看起来像 bug 但确实是旧版行为”的点）：</para>
/// <list type="bullet">
/// <item>打击音效 / 采样信息会丢：旧版在这个算法里新建打击物时用的是默认 HitSound / HitSample；</item>
/// <item>长度 0 的 LN（type=128）会被压回普通键（旧版重建对象时 endTime == startTime 就写 type=1）；</item>
/// <item>按 Gap 分段的“上一段末尾对象”会参与下一段开头（旧版 <c>checkList</c> 是跨段保留的）。</item>
/// </list>
/// </summary>
public static class KeyConverterEngine
{
    /// <summary>同列开始时间间隔判定阈值（旧版 <c>KeyConverter.INTERVAL</c>，毫秒）。</summary>
    public const double Interval = 50;

    /// <summary>同列 LN 尾巴与前一个头部的间隔阈值（旧版 <c>KeyConverter.LN_INTERVAL</c>，毫秒）。</summary>
    public const double LnInterval = 10;

    /// <summary>清子弹时允许的误差（旧版 <c>KeyConverter.ERROR</c>，毫秒）。</summary>
    public const double Error = 1.5;

    /// <summary>旧版常量（保留）：空白列最小单位。</summary>
    public const int BlankColumn = 1;

    /// <summary>
    /// 把一个谱面转成更高键数（原地修改 <paramref name="beatmap"/>）。不写文件。
    /// </summary>
    public static KeyConverterResult Apply(OsuManiaBeatmap beatmap, KeyConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(options);

        int keys = beatmap.KeyCount;
        int keyValue = options.TargetKeys;
        int blank = options.Blank;

        // ---- 有效性判定：顺序照旧版（先 keys>9 / 目标<=源，再夹 Blank，再看 CheckKeys） ----
        string? skipReason = null;
        if (keys > 9)
        {
            skipReason = $"源谱面 {keys}K 超过 9K，旧版转 K 器不支持（只支持源 1~9 键）。";
        }
        else if (keyValue <= keys)
        {
            skipReason = $"目标键数必须大于源键数（只支持低转高）：源 {keys}K → 目标 {keyValue}K。";
        }

        // 旧版即使随后判定不转换，也先把 Blank 夹到「目标 - 源」。
        if (blank > keyValue - keys)
        {
            blank = keyValue - keys;
        }

        if (options.CheckKeys.Count > 0 && !options.CheckKeys.Contains(keys))
        {
            skipReason ??= $"源谱面 {keys}K 不在勾选的键数范围内。";
        }

        if (skipReason is null && !OsuManiaBeatmap.IsSupportedKeyCount(keyValue))
        {
            skipReason = $"目标键数 {keyValue} 不受支持（仅支持 {OsuManiaBeatmap.MinKeyCount}~{OsuManiaBeatmap.MaxKeyCount} 键）。";
        }

        if (skipReason is not null)
        {
            return KeyConverterResult.Skip(skipReason);
        }

        try
        {
            ConvertCore(beatmap, options, keys, keyValue, blank);
            return KeyConverterResult.Success();
        }
        catch (Exception ex)
        {
            // 旧版没有这层保护：TimingPoint 缺失等异常会冒到 UI 线程（批量里吞掉、单文件可能闪退）。
            // 新版统一转成“跳过 + 原因”，保证界面不崩。
            return KeyConverterResult.Skip($"转换失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 转换并保存到旧版命名规则的位置（原目录 + 元数据拼文件名），结果里带 <see cref="KeyConverterResult.OutputPath"/>。
    /// 保存失败不抛异常，返回跳过结果并带原因。
    /// </summary>
    public static KeyConverterResult Apply(OsuBeatmapFile file, KeyConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = Apply(file.Beatmap, options);
        if (!result.Converted)
        {
            return result;
        }

        try
        {
            string outputPath = file.Save();
            return KeyConverterResult.Success(outputPath);
        }
        catch (Exception ex)
        {
            return KeyConverterResult.Skip($"转换成功但保存失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>转换主体（旧版 ApplyToBeatmap 里“判定通过之后”的部分）。</summary>
    private static void ConvertCore(OsuManiaBeatmap beatmap, KeyConverterOptions options, int keys, int keyValue, int blank)
    {
        var rng = OsuTransformUtils.CreateRandom(options.SeedText);

        // ---- 旧版 ManiaToKeys：把 General.CircleSize 换成目标键数，列号不变（X 由写文件时按新键数重算） ----
        beatmap.KeyCount = keyValue;

        var newObjects = new List<ManiaNote>();

        var locations = beatmap.HitObjects.OrderBy(h => h.StartTime).ToList();

        List<ManiaNote> area = new List<ManiaNote>();
        List<ManiaNote>? checkList = null;

        var tempObjects = locations.OrderBy(h => h.StartTime).ToList();

        int sumTime = 0;
        int lastTime = 0;

        foreach (var timingPoint in tempObjects.GroupBy(h => h.StartTime))
        {
            var newLocations = timingPoint.OrderBy(n => n.Column).ToList();

            List<ManiaNote> line = new List<ManiaNote>();

            foreach (var note in newLocations)
            {
                // 旧版这里用 (列, 开始, 结束) 新建对象，会把 HitSound / HitSample 丢成默认值（bug）；
                // 新版直接复制源音符（保留打击音效 / 采样 / 连击标记），列号与时间不变。
                line.Add(note.Clone());
            }

            sumTime += timingPoint.Key - lastTime;
            lastTime = timingPoint.Key;

            area.AddRange(line);

            double gap = (29998.8584 * Math.Pow(Math.E, -0.3176 * options.Gap)) + 347.7248;

            if (options.Gap == 0)
            {
                gap = double.MaxValue;
            }

            if (sumTime >= gap)
            {
                sumTime = 0;
                int cleanDivide = options.Clean;
                var processed = ProcessArea(beatmap, rng, area, keys, keyValue, blank, cleanDivide, Error, checkList);
                newObjects.AddRange(processed.Result);
                checkList = processed.CheckList.ToList();
                area.Clear();
            }
        }

        if (area.Count > 0)
        {
            int cleanDivide = options.Clean;
            var processed = ProcessArea(beatmap, rng, area, keys, keyValue, blank, cleanDivide, Error, checkList);
            newObjects.AddRange(processed.Result);
        }

        newObjects = newObjects.OrderBy(h => h.StartTime).ToList();

        var cleanObjects = new List<ManiaNote>();

        foreach (var column in newObjects.GroupBy(h => h.Column))
        {
            var newColumnObjects = new List<ManiaNote>();

            // 旧版只保留 (开始, 结束) 两个时间；新版额外记住“这个时间区间是谁贡献的”，
            // 合并后重建对象时把 HitSound / Extras 一起带回来（旧版在这一步又把音效丢了一次）。
            var cleanLocations = column.Select(n => (startTime: n.StartTime, endTime: n.EndTime, source: n)).OrderBy(h => h.startTime).ToList();

            int lastStartTime = cleanLocations[0].startTime;
            int lastEndTime = cleanLocations[0].endTime;
            ManiaNote lastSource = cleanLocations[0].source;

            for (int i = 0; i < cleanLocations.Count; i++)
            {
                if (i == 0)
                {
                    lastStartTime = cleanLocations[0].startTime;
                    lastEndTime = cleanLocations[0].endTime;
                    lastSource = cleanLocations[0].source;
                    continue;
                }

                if (cleanLocations[i].startTime >= lastStartTime && cleanLocations[i].startTime <= lastEndTime)
                {
                    cleanLocations.RemoveAt(i);
                    i--;
                    continue;
                } // if the note in a LN

                if (Math.Abs(cleanLocations[i].startTime - lastStartTime) <= Interval)
                {
                    lastStartTime = cleanLocations[i].startTime;
                    lastEndTime = cleanLocations[i].endTime;
                    lastSource = cleanLocations[i].source;
                    cleanLocations.RemoveAt(i);
                    i--;
                    continue;
                } // interval judgement

                if (Math.Abs(cleanLocations[i].startTime - lastEndTime) <= LnInterval)
                {
                    lastStartTime = cleanLocations[i].startTime;
                    lastEndTime = cleanLocations[i].endTime;
                    lastSource = cleanLocations[i].source;
                    cleanLocations.RemoveAt(i);
                    i--;
                    continue;
                } // LN interval judgement

                var lastNote = CreateMergedNote(column.Key, lastStartTime, lastEndTime, lastSource);

                newColumnObjects.Add(lastNote);
                lastStartTime = cleanLocations[i].startTime;
                lastEndTime = cleanLocations[i].endTime;
                lastSource = cleanLocations[i].source;
            }

            var newNote = CreateMergedNote(column.Key, lastStartTime, lastEndTime, lastSource);

            newColumnObjects.Add(newNote);

            cleanObjects.AddRange(newColumnObjects);
        }

        beatmap.HitObjects = cleanObjects.OrderBy(h => h.StartTime).ToList();

        // ---- 难度名前缀 / 谱师：照旧版 ----
        if (blank == 0)
        {
            beatmap.Metadata.Difficulty = beatmap.Metadata.Difficulty.Insert(0, $"[{keys}To{keyValue}C]");
        }
        else
        {
            beatmap.Metadata.Difficulty = beatmap.Metadata.Difficulty.Insert(0, $"[{keys}To{keyValue}C{blank}B]");
        }

        if (options.CreatorIsChecked && !string.IsNullOrEmpty(options.Creator))
        {
            beatmap.Metadata.Creator = options.Creator;
        }
        else
        {
            beatmap.Metadata.Creator += " & KeyConverter";
        }
    }

    /// <summary>
    /// 把「合并后的时间区间」还原成打击物：复制源对象的音效 / 采样 / 连击信息，再套上合并后的列与时间。
    /// 区间长度为 0 时是普通键（与旧版一致：旧版重建对象时 end == start 就写 type=1）。
    /// </summary>
    private static ManiaNote CreateMergedNote(int column, int startTime, int endTime, ManiaNote source)
    {
        var note = source.Clone();
        note.Column = column;
        note.StartTime = startTime;
        note.EndTime = endTime;
        return note;
    }

    /// <summary>
    /// 处理一段（按 Gap 切出来的一批同时刻打击物）：随机复制列、插入空白列、清子弹
    /// （旧版 <c>KeyConverter.ProcessArea</c> 的逐行移植，随机数调用顺序照搬）。
    /// </summary>
    /// <param name="beatmap">谱面（只用它的 TimingPoints 做清子弹的节拍换算）。</param>
    /// <param name="rng">随机数生成器（同一局里共享，顺序敏感）。</param>
    /// <param name="hitObjects">这一段的对象（已按开始时间 / 列号排好）。</param>
    /// <param name="fromKeys">源键数。</param>
    /// <param name="toKeys">目标键数。</param>
    /// <param name="blankNum">空白列数。</param>
    /// <param name="clean">清子弹等分数（0 = 不去重，但 checkList 命中会直接删）。</param>
    /// <param name="error">清子弹误差（旧版传 <see cref="Error"/>）。</param>
    /// <param name="checkList">上一段末尾的对象（用于清掉跨段重复）。</param>
    /// <returns>处理后的对象 + 本段末尾对象（供下一段当 checkList）。</returns>
    public static (List<ManiaNote> Result, List<ManiaNote> CheckList) ProcessArea(
        OsuManiaBeatmap beatmap, Random rng, List<ManiaNote> hitObjects, int fromKeys, int toKeys,
        int blankNum = 0, int clean = 0, double error = 0, List<ManiaNote>? checkList = null)
    {
        List<ManiaNote> newObjects = new List<ManiaNote>();
        List<(int column, bool isBlank)> copyColumn = [];
        List<int> insertColumn = [];
        List<ManiaNote> checkColumn = [];
        bool isFirst = true;

        // ---- 随机挑「要复制的源列」 ----
        int num = toKeys - fromKeys;
        if (num <= fromKeys)
        {
            // 与旧版完全一致：随机取一个源列，没复制过就记下，直到凑够 toKeys - fromKeys 个。
            while (num > 0)
            {
                int copy = rng.Next(fromKeys);
                if (!copyColumn.Contains((copy, false)))
                {
                    copyColumn.Add((copy, false));
                    num--;
                }
            }
        }
        else
        {
            // 旧版在这里会死循环（可复制的源列数 < 需要复制的数量，例如 2K→7K / 4K→9K）。
            // 这里兜底：先把所有源列各复制一次，剩下的从源列里随机重复复制，保证工具不会卡死。
            while (num > 0)
            {
                int copy = rng.Next(fromKeys);
                if (!copyColumn.Contains((copy, false)))
                {
                    copyColumn.Add((copy, false));
                    num--;
                }
                else if (copyColumn.Count >= fromKeys)
                {
                    copyColumn.Add((copy, false));
                    num--;
                }
            }
        }

        // ---- 随机挑「要标成空白的复制列」 ----
        num = blankNum;
        int blankAttempts = 0;
        int blankAttemptLimit = Math.Max(1000, fromKeys * 1000);
        while (num > 0)
        {
            int copy = rng.Next(fromKeys);
            if (copyColumn.Contains((copy, false)) && !copyColumn.Contains((copy, true)))
            {
                int index = copyColumn.IndexOf((copy, false));
                copyColumn[index] = (copy, true);
                num--;
                blankAttempts = 0;
            }
            else if (++blankAttempts > blankAttemptLimit)
            {
                // 旧版在“能标空白的列不够”时也会死循环；这里放弃剩余空白列，保证不卡死。
                break;
            }
        }

        // ---- 随机挑「插入位置」 ----
        num = toKeys - fromKeys;
        while (num > 0)
        {
            int insert = rng.Next(toKeys);
            if (!insertColumn.Contains(insert))
            {
                insertColumn.Add(insert);
                num--;
            }
        }
        insertColumn = insertColumn.OrderBy(c => c).ToList();

        // ---- 逐时刻处理：复制到插入列 + 原列右移 ----
        foreach (var timingPoint in hitObjects.GroupBy(h => h.StartTime))
        {
            var locations = timingPoint.ToList();
            var tempObjects = new List<ManiaNote>();
            int length = copyColumn.Count;

            for (int i = 0; i < locations.Count; i++)
            {
                int column = locations[i].Column;
                for (int j = 0; j < length; j++)
                {
                    if (column == copyColumn[j].column && !copyColumn[j].isBlank)
                    {
                        // 旧版是结构体复制一份再改列号；这里是 class，必须显式 Clone。
                        var temp = locations[i].Clone();
                        temp.Column = insertColumn[j];
                        tempObjects.Add(temp);
                    }

                    if (locations[i].Column >= insertColumn[j])
                    {
                        locations[i].Column += 1;
                    }
                }
                tempObjects.Add(locations[i]);
            }

            if (isFirst && checkList is not null && checkList.Count > 0 && clean > 0)
            {
                var checkC = checkList.Select(h => h.Column).ToList();
                var checkS = checkList.Select(h => h.StartTime).ToList();
                for (int i = 0; i < tempObjects.Count; i++)
                {
                    if (checkC.Contains(tempObjects[i].Column))
                    {
                        if (clean != 0)
                        {
                            double beatLength = beatmap.TimingPointAt(tempObjects[i].StartTime).BeatLength;
                            double timeDivide = beatLength / clean;
                            int index = checkC.IndexOf(tempObjects[i].Column);

                            if (tempObjects[i].StartTime - checkS[index] < timeDivide + error)
                            {
                                tempObjects.RemoveAt(i);
                                i--;
                            }
                        }
                        else
                        {
                            tempObjects.RemoveAt(i);
                            i--;
                        }
                    }
                }
                isFirst = false;
            }

            checkColumn.Clear();
            checkColumn.AddRange(tempObjects);
            newObjects.AddRange(tempObjects);
        }

        return (newObjects, checkColumn);
    }
}
