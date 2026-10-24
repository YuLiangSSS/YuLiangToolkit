using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.OsuMania.Core;

/// <summary>
/// 转 LN（长短条转换）参数。默认值与早期版本界面控件默认值一致：
/// Level=3、LN 100%、Divide=4、Column=10、Gap=12、Ignore 勾选、Fix Error 勾选、Original LN 不勾选。
///
/// <para><b>Level 的含义</b>（照搬早期版本说明）：
/// -3 = Hold off（把所有长条压成普通键，再随机抽一部分换回 LN）；
/// -2 = 完全随机长度；-1 = 随机等分；0 = 常规 LN（1/100 节拍长度的正态分布）；
/// 1~9 = 微 / 轻 / 中 / 密 LN（mu = Level * 11，sigma = 0.85 / 8→0.9 / 9→1.0）；10 = Invert（反相）。</para>
/// </summary>
public sealed class LnTransformerOptions
{
    /// <summary>随机种子文本（默认 "114514"；留空则每次随机）。固定种子结果可复现。</summary>
    public string SeedText { get; set; } = "114514";

    /// <summary>转换等级（早期版本滑条 -3~10）。</summary>
    public int Level { get; set; } = 3;

    /// <summary>转换为 LN 的对象占比（0~100）。</summary>
    public double Percentage { get; set; } = 100;

    /// <summary>转换为 1/几 的 LN（早期版本滑条 1~16）。</summary>
    public int Divide { get; set; } = 4;

    /// <summary>转换多少条轨道（0~10；大于谱面键数时按全部轨道处理）。</summary>
    public int Column { get; set; } = 10;

    /// <summary>每隔多少行 Note 变换一次所转轨道（0~20；0 = 转换轨道不变）。</summary>
    public int Gap { get; set; } = 12;

    /// <summary>写回谱面的 OD（仅当 <see cref="KeepOverallDifficulty"/> 为 false 时生效）。</summary>
    public double OverallDifficulty { get; set; }

    /// <summary>
    /// 是否保留原谱 OD。默认 true（早期版本界面固定把 OD 写成输入框里的值，默认 0 会把谱面 OD 写坏，
    /// 这是已知 bug；新版默认不动 OD，只有界面显式关掉这个开关才写 <see cref="OverallDifficulty"/>）。
    /// </summary>
    public bool KeepOverallDifficulty { get; set; } = true;

    /// <summary>新谱师名（配合 <see cref="CreatorIsChecked"/>）。</summary>
    public string Creator { get; set; } = "";

    /// <summary>勾选后才替换谱师；否则早期版本会在原谱师后面追加 " &amp; LNTransformer"。</summary>
    public bool CreatorIsChecked { get; set; }

    /// <summary>忽略已转换过的谱面（早期版本默认勾选）。</summary>
    public bool Ignore { get; set; } = true;

    /// <summary>保留原谱面里的长条（早期版本 Original LN，默认不勾）。</summary>
    public bool OriginalLN { get; set; }

    /// <summary>用反算修复 LN 尾巴的细小误差（早期版本 Fix Error，默认勾选）。</summary>
    public bool FixError { get; set; } = true;

    /// <summary>只转换这些键数的谱面（1k~10k 复选框；空 = 全部）。</summary>
    public IReadOnlyList<int> CheckKeys { get; set; } = Array.Empty<int>();
}

/// <summary>转 LN 的结果。</summary>
public sealed class LnTransformResult
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

    /// <summary>成功结果。</summary>
    public static LnTransformResult Success(string? outputPath = null) => new() { Converted = true, OutputPath = outputPath };

    /// <summary>跳过结果。</summary>
    public static LnTransformResult Skip(string reason) => new() { Converted = false, SkipReason = reason };
}

/// <summary>
/// 转 LN 引擎（早期版本 <c>LNTransformer.xaml.cs</c> 的 <c>ApplyToBeatmap</c> / <c>Transform</c> / <c>Invert</c>
/// / <c>TrueRandom</c> / <c>AfterTransform</c> / <c>GetDurationByDistribution</c> / <c>RandDistribution</c>
/// / <c>TimeRound</c> / <c>IsConverted</c> 的逐行改写）。
///
/// <para><b>与早期版本的区别（Lead 已确认要修）</b>：</para>
/// <list type="bullet">
/// <item>早期版本按列循环时把 <c>originalLNObjects</c> 覆盖掉了（只保留最后一列），导致 Original LN 只对最后一列生效；
/// 新版累加所有列（<c>AddRange</c>），Original LN 对全部列生效。</item>
/// <item>OD 默认保留原谱（<see cref="LnTransformerOptions.KeepOverallDifficulty"/> = true）；
/// 早期版本永远把 OD 写成界面输入框的值（默认 0，会把谱面 OD 写坏）。</item>
/// <item>TimingPoint 异常（没有红线等）会由最外层 catch 转成“跳过 + 原因”，不会让界面闪退。</item>
/// </list>
/// </summary>
public static class LnTransformerEngine
{
    /// <summary>早期版本 <c>LNTransformer.ERROR</c>（毫秒）：判断“最后一个音是否当作 LN / 尾巴是否贴边”。</summary>
    public const double Error = 2.0;

    /// <summary>
    /// 把一个谱面转成 LN（原地修改 <paramref name="beatmap"/>）。不写文件。
    /// </summary>
    public static LnTransformResult Apply(OsuManiaBeatmap beatmap, LnTransformerOptions options)
    {
        ArgumentNullException.ThrowIfNull(beatmap);
        ArgumentNullException.ThrowIfNull(options);

        if (options.CheckKeys.Count > 0 && !options.CheckKeys.Contains(beatmap.KeyCount))
        {
            return LnTransformResult.Skip($"源谱面 {beatmap.KeyCount}K 不在勾选的键数范围内。");
        }

        if (options.Ignore && IsConverted(beatmap))
        {
            return LnTransformResult.Skip("谱面已经转换过（Creator / 难度名带 LNTransformer 标记），Ignore 已勾选，跳过。");
        }

        var rng = OsuTransformUtils.CreateRandom(options.SeedText);

        try
        {
            ApplyCore(beatmap, options, rng);
            return LnTransformResult.Success();
        }
        catch (Exception ex)
        {
            return LnTransformResult.Skip($"转换失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>
    /// 转换并保存到早期版本命名规则的位置（原目录 + 元数据拼文件名），结果里带 <see cref="LnTransformResult.OutputPath"/>。
    /// 保存失败不抛异常，返回跳过结果并带原因。
    /// </summary>
    public static LnTransformResult Apply(OsuBeatmapFile file, LnTransformerOptions options)
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
            return LnTransformResult.Success(outputPath);
        }
        catch (Exception ex)
        {
            return LnTransformResult.Skip($"转换成功但保存失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>转换主体。</summary>
    private static void ApplyCore(OsuManiaBeatmap beatmap, LnTransformerOptions options, Random rng)
    {
        int keys = beatmap.KeyCount;
        var newObjects = new List<ManiaNote>();
        var oldObjects = beatmap.HitObjects;
        // 早期版本这里在按列循环里被覆盖（只保留最后一列）；新版累加所有列，Original LN 才能对全部列生效。
        var originalLNObjects = new List<ManiaNote>();

        if (options.Level == -3)
        {
            // ---- Level -3：Hold off + 随机列 + 百分比 ----
            for (int i = 0; i < beatmap.HitObjects.Count; i++)
            {
                if (beatmap.HitObjects[i].IsLN)
                {
                    beatmap.HitObjects[i].EndTime = beatmap.HitObjects[i].StartTime;
                }
            }

            int transformColumnNum = options.Column;

            if (transformColumnNum > keys)
            {
                transformColumnNum = keys;
            }
            var randomColumnSet = OsuTransformUtils.SelectRandom(Enumerable.Range(0, keys), rng, transformColumnNum == 0 ? keys : transformColumnNum).ToHashSet();

            int gap = options.Gap;
            foreach (var timeGroup in oldObjects.GroupBy(x => x.StartTime))
            {
                foreach (var note in timeGroup)
                {
                    if (randomColumnSet.Contains(note.Column) && rng.Next(100) < options.Percentage)
                    {
                        newObjects.Add(note.ToNote());
                    }
                    else
                    {
                        newObjects.Add(note);
                    }
                }

                gap--;
                if (gap <= 0)
                {
                    // 早期版本这里再抽列时用的是 transformColumnNum 原值（没有 ==0 时取 keys 的兜底），照搬。
                    randomColumnSet = OsuTransformUtils.SelectRandom(Enumerable.Range(0, keys), rng, transformColumnNum).ToHashSet();
                    gap = options.Gap;
                }
            }
            beatmap.HitObjects = newObjects;
            ApplyOverallDifficulty(beatmap, options);
            beatmap.Metadata.Difficulty = beatmap.Metadata.Difficulty.Insert(0, $"[LN-Lv{options.Level:N0}-C{options.Column:N0}]");
            ApplyCreator(beatmap, options);
            return;
        }

        // ---- 其它 Level：按列分别处理 ----
        foreach (var column in beatmap.HitObjects.GroupBy(h => h.Column))
        {
            switch (options.Level)
            {
                case -2:
                    {
                        TrueRandom(newObjects, rng, column, options.OriginalLN, options.Percentage);
                    }
                    break;
                case 10:
                    {
                        originalLNObjects.AddRange(Invert(beatmap, newObjects, rng, column, options.Divide, options.OriginalLN, options.Percentage, options.FixError));
                    }
                    break;
                default:
                    {
                        double mu;
                        double sigma;

                        if (options.Level == -1)
                        {
                            mu = -1;
                            sigma = 1;
                        }
                        else if (options.Level == 0)
                        {
                            mu = 1;
                            sigma = 100;
                        }
                        else
                        {
                            mu = options.Level * 11;
                            sigma = 0.85;
                            if (options.Level == 8)
                            {
                                sigma = 0.9;
                            }
                            else if (options.Level == 9)
                            {
                                sigma = 1;
                            }
                        }
                        originalLNObjects.AddRange(Transform(rng, mu, sigma, options.Divide, beatmap, newObjects, column, options.OriginalLN, options.Percentage, options.FixError));
                    }
                    break;
            }
        }

        newObjects = newObjects.OrderBy(h => h.StartTime).ToList();
        originalLNObjects = originalLNObjects.OrderBy(h => h.StartTime).ToList();

        AfterTransform(newObjects, originalLNObjects, beatmap, rng, options.Column, options.OriginalLN, options.Gap);

        ApplyOverallDifficulty(beatmap, options);
        int columnNum = options.Column;
        if (columnNum > keys)
        {
            columnNum = keys;
        }
        if (columnNum != 0)
        {
            beatmap.Metadata.Difficulty = beatmap.Metadata.Difficulty.Insert(0, $"[LN-Lv{options.Level:N0}-C{columnNum:N0}]");
        }
        else
        {
            beatmap.Metadata.Difficulty = beatmap.Metadata.Difficulty.Insert(0, $"[LN-Lv{options.Level:N0}]");
        }

        ApplyCreator(beatmap, options);
    }

    /// <summary>
    /// Level -1 / 0 / 1~9 的按列转换（早期版本 <c>Transform</c> 的逐行改写）。
    /// </summary>
    /// <returns>本列里保留下来的原 LN（供 AfterTransform 判断）。</returns>
    public static List<ManiaNote> Transform(Random rng, double mu, double sigmaDivisor, double divide, OsuManiaBeatmap osu, List<ManiaNote> newObjects,
        IGrouping<int, ManiaNote> column, bool originalLNIsChecked, double percentageValue, bool fixErrorIsChecked,
        int divide2 = -1, double mu2 = -2, double mu1Dmu2 = -1)
    {
        var originalLNObjects = new List<ManiaNote>();
        var newColumnObjects = new List<ManiaNote>();
        var locations = column.OrderBy(h => h.StartTime).ToList();
        for (int i = 0; i < locations.Count - 1; i++)
        {
            // 早期版本这里有一行未使用的 offset 赋值（但对空的 TimingPoints 会抛异常），保留它的副作用。
            _ = osu.TimingPoints.First().Time;
            double fullDuration = locations[i + 1].StartTime - locations[i].StartTime; // Full duration of the hold note.
            double duration = GetDurationByDistribution(rng, osu, locations[i].StartTime, fullDuration, mu, sigmaDivisor, divide, divide2, mu2, mu1Dmu2);

            var obj = locations[i];
            obj.Column = column.Key;
            if (originalLNIsChecked && locations[i].StartTime != locations[i].EndTime)
            {
                newColumnObjects.Add(obj);
                originalLNObjects.Add(obj);
            }
            else if (rng.Next(100) < percentageValue && !double.IsNaN(duration))
            {
                var endTime = obj.StartTime + duration;
                if (fixErrorIsChecked)
                {
                    var point = osu.TimingPointAt(endTime);
                    endTime = OsuTransformUtils.PreciseTime(endTime, point.BeatLength, point.Time);
                }
                obj.EndTime = (int)endTime;
                newColumnObjects.Add(obj);
            }
            else
            {
                newColumnObjects.Add(obj.ToNote());
            }
        }

        // Dispose last note on the column

        if (Math.Abs(locations[locations.Count - 1].StartTime - locations[locations.Count - 1].EndTime) <= Error || rng.Next(100) >= percentageValue)
        {
            newColumnObjects.Add(locations[locations.Count - 1].ToNote());
        }
        else
        {
            newColumnObjects.Add(locations[locations.Count - 1]);
        }

        newObjects.AddRange(newColumnObjects);

        return originalLNObjects;
    }

    /// <summary>
    /// 由分布随机出 LN 时长（早期版本 <c>GetDurationByDistribution</c> 的逐行改写；随机数调用顺序照搬）。
    /// </summary>
    public static double GetDurationByDistribution(Random rng, OsuManiaBeatmap osu, int startTime, double limitDuration, double mu, double sigmaDivisor, double divide,
        int divide2 = -1, double mu2 = -2, double mu1Dmu2 = -1)
    {
        // Beat length at the end of the hold note.
        double beatLength = osu.TimingPointAt(startTime).BeatLength;
        double timeDivide = beatLength / divide;
        bool flag = true; // Can be transformed to LN
        double sigma = timeDivide / sigmaDivisor; // LN duration σ
        int timenum = (int)Math.Round(limitDuration / timeDivide, 0);
        int rdtime;
        double duration = TimeRound(timeDivide, RandDistribution(rng, limitDuration * mu / 100, sigma));

        if (mu1Dmu2 != -1)
        {
            if (rng.Next(100) >= mu1Dmu2)
            {
                timeDivide = beatLength / divide2;
                sigma = timeDivide / sigmaDivisor;
                timenum = (int)Math.Round(limitDuration / timeDivide, 0);
                duration = TimeRound(timeDivide, RandDistribution(rng, limitDuration * mu2 / 100, sigma));
            }
        }

        if (mu == -1)
        {
            if (timenum < 1)
            {
                duration = timeDivide;
            }
            else
            {
                rdtime = rng.Next(1, timenum);
                duration = rdtime * timeDivide;
                duration = TimeRound(timeDivide, duration);
            }
        }

        if (duration > limitDuration - timeDivide)
        {
            duration = limitDuration - timeDivide;
            duration = TimeRound(timeDivide, duration);
        }

        if (duration <= timeDivide)
        {
            duration = timeDivide;
        }

        if (duration >= limitDuration - Error) // Additional processing.
        {
            flag = false;
        }

        return flag ? duration : double.NaN;
    }

    /// <summary>
    /// Level 10：反相（早期版本 <c>Invert</c> 的逐行改写）。
    /// </summary>
    /// <returns>本列里保留下来的原 LN。</returns>
    public static List<ManiaNote> Invert(OsuManiaBeatmap osu, List<ManiaNote> newObjects, Random rng, IGrouping<int, ManiaNote> column, double divideValue,
        bool originalLNIsChecked, double percentageValue, bool fixErrorIsChecked)
    {
        var locations = column.OrderBy(h => h.StartTime).ToList();

        var newColumnObjects = new List<ManiaNote>();
        var originalLNObjects = new List<ManiaNote>();

        for (int i = 0; i < locations.Count - 1; i++)
        {
            // Full duration of the hold note.
            double fullDuration = locations[i + 1].StartTime - locations[i].StartTime;
            // Beat length at the end of the hold note.
            double beatLength = osu.TimingPointAt(locations[i + 1].StartTime).BeatLength;
            bool flag = true;
            double duration = fullDuration - (beatLength / divideValue);

            if (duration < beatLength / divideValue)
            {
                duration = beatLength / divideValue;
            }

            if (duration > fullDuration - 3)
            {
                flag = false;
            }

            var obj = locations[i];
            obj.Column = column.Key;

            if (originalLNIsChecked == true && locations[i].StartTime != locations[i].EndTime)
            {
                newColumnObjects.Add(obj);
                originalLNObjects.Add(obj);
            }
            else if (rng.Next(100) < percentageValue && flag)
            {
                var endTime = locations[i].StartTime + duration;
                if (fixErrorIsChecked == true)
                {
                    endTime = OsuTransformUtils.PreciseTime(endTime, osu.TimingPointAt(endTime).BeatLength, osu.TimingPoints.First().Time);
                }
                obj.EndTime = (int)endTime;
                newColumnObjects.Add(obj);
            }
            else
            {
                newColumnObjects.Add(obj.ToNote());
            }
        }

        double lastStartTime = locations[locations.Count - 1].StartTime;
        double lastEndTime = locations[locations.Count - 1].EndTime;
        if (originalLNIsChecked == true && lastStartTime != lastEndTime)
        {
            var obj = locations[locations.Count - 1];
            obj.Column = column.Key;
            newColumnObjects.Add(obj);
            originalLNObjects.Add(obj);
        }
        else
        {
            var obj = locations[locations.Count - 1];
            obj.Column = column.Key;
            newColumnObjects.Add(obj.ToNote());
        }

        newObjects.AddRange(newColumnObjects);

        return originalLNObjects;
    }

    /// <summary>
    /// Level -2：完全随机长度（早期版本 <c>TrueRandom</c> 的逐行改写）。
    /// </summary>
    /// <returns>本列里保留下来的原 LN。</returns>
    public static List<ManiaNote> TrueRandom(List<ManiaNote> newObjects, Random rng, IGrouping<int, ManiaNote> column, bool originalLNIsChecked, double percentageValue)
    {
        var locations = column.OrderBy(h => h.StartTime).ToList();

        var newColumnObjects = new List<ManiaNote>();
        var originalLNObjects = new List<ManiaNote>();

        for (int i = 0; i < locations.Count - 1; i++)
        {
            // Full duration of the hold note.
            double fullDuration = locations[i + 1].StartTime - locations[i].StartTime;

            double duration = rng.Next((int)fullDuration) + rng.NextDouble();
            while (duration > fullDuration)
                duration--;

            var obj = locations[i];
            obj.Column = column.Key;

            if (originalLNIsChecked == true && locations[i].StartTime != locations[i].EndTime)
            {
                newColumnObjects.Add(obj);
                originalLNObjects.Add(obj);
            }
            else if (rng.Next(100) < percentageValue)
            {
                obj.EndTime = obj.StartTime + (int)duration;
                newColumnObjects.Add(obj);
            }
            else
            {
                newColumnObjects.Add(obj.ToNote());
            }
        }

        newObjects.AddRange(newColumnObjects);

        return originalLNObjects;
    }

    /// <summary>
    /// 转换后的收尾：按 Gap 每隔几行重抽一次“保留 LN 的轨道”，并把不在轨道上的 LN 压成普通键
    /// （早期版本 <c>AfterTransform</c> 的逐行改写；注意第二次抽列用的是 raw transformColumnNum，照搬）。
    /// </summary>
    public static void AfterTransform(List<ManiaNote> afterObjects, List<ManiaNote> originalLNObjects, OsuManiaBeatmap osu, Random rng, int transformColumnNum,
        bool originalLNIsChecked, int gapValue)
    {
        var resultObjects = new List<ManiaNote>();
        var originalLNSet = new HashSet<ManiaNote>(originalLNObjects);
        int keys = osu.KeyCount;
        int maxGap = gapValue;
        int gap = maxGap;

        if (transformColumnNum > keys)
        {
            transformColumnNum = keys;
        }

        var randomColumnSet = OsuTransformUtils.SelectRandom(Enumerable.Range(0, keys), rng, transformColumnNum == 0 ? keys : transformColumnNum).ToHashSet();

        foreach (var timeGroup in afterObjects.OrderBy(h => h.StartTime).GroupBy(h => h.StartTime))
        {
            foreach (var note in timeGroup)
            {
                if (originalLNSet.Contains(note) && originalLNIsChecked == true)
                {
                    resultObjects.Add(note);
                }
                else if (randomColumnSet.Contains(note.Column) && note.StartTime != note.EndTime)
                {
                    resultObjects.Add(note);
                }
                else
                {
                    resultObjects.Add(note.ToNote());
                }
            }

            gap--;
            if (gap == 0)
            {
                // 早期版本第二次抽列没有 “transformColumnNum == 0 时取 keys” 的兜底，照搬。
                randomColumnSet = OsuTransformUtils.SelectRandom(Enumerable.Range(0, keys), rng, transformColumnNum).ToHashSet();
                gap = maxGap;
            }
        }

        osu.HitObjects = resultObjects.OrderBy(h => h.StartTime).ToList();
    }

    /// <summary>正态分布抽样（Box-Muller；早期版本 <c>RandDistribution</c> 的逐行改写）。</summary>
    public static double RandDistribution(Random rng, double u, double d)
    {
        double u1, u2, z, x;
        if (d <= 0)
        {
            return u;
        }
        u1 = rng.NextDouble();
        u2 = rng.NextDouble();
        z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Sin(2 * Math.PI * u2);
        x = u + (d * z);
        return x;
    }

    /// <summary>把时长四舍五入到 <paramref name="timedivide"/> 的整数倍（早期版本 <c>TimeRound</c> 的逐行改写）。</summary>
    public static double TimeRound(double timedivide, double num)
    {
        double remainder = num % timedivide;
        if (remainder < timedivide / 2)
            return num - remainder;
        return num + timedivide - remainder;
    }

    /// <summary>
    /// 判断谱面是否已被转 LN 过（早期版本 <c>IsConverted</c>：Creator 含 "LNTransformer"，或难度名匹配 <c>[LN...]</c>）。
    /// </summary>
    public static bool IsConverted(OsuManiaBeatmap beatmap)
    {
        if (beatmap.Metadata.Creator.Contains("LNTransformer"))
        {
            return true;
        }
        string difficulty = beatmap.Metadata.Difficulty;
        if (Regex.IsMatch(difficulty, @"\[LN.*\]"))
        {
            return true;
        }
        return false;
    }

    /// <summary>按早期版本规则处理 OD（默认保留原谱，见 <see cref="LnTransformerOptions.KeepOverallDifficulty"/>）。</summary>
    private static void ApplyOverallDifficulty(OsuManiaBeatmap beatmap, LnTransformerOptions options)
    {
        if (!options.KeepOverallDifficulty)
        {
            beatmap.OverallDifficulty = options.OverallDifficulty;
        }
    }

    /// <summary>按早期版本规则处理谱师：勾选且非空则替换，否则追加 " &amp; LNTransformer"。</summary>
    private static void ApplyCreator(OsuManiaBeatmap beatmap, LnTransformerOptions options)
    {
        if (options.CreatorIsChecked && !string.IsNullOrEmpty(options.Creator))
        {
            beatmap.Metadata.Creator = options.Creator;
        }
        else
        {
            beatmap.Metadata.Creator += " & LNTransformer";
        }
    }
}
