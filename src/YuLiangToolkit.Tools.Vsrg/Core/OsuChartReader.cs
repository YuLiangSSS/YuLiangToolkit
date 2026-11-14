using System.Globalization;
using System.IO;
using System.Text;

namespace YuLiangToolkit.Tools.Vsrg.Core;

/// <summary>
/// **手写的 .osu 只读解析器**：只取本工具要用的字段（键数 / 元数据 / 音频 / 背景 / 打击物），
/// 其余段落原样忽略。
///
/// <para><b>为什么手写</b>：见 <see cref="Chart"/> 的注释 —— 扫描谱面库时要能并行解析多个文件，
/// 而 OsuParsers 的解码器是静态状态、非线程安全。带格式错误的行**跳过而不是抛异常**
/// （一张有小瑕疵的谱面也要能打开），有多少行被跳过记在 <see cref="OsuChartReader.Warnings"/> 里。</para>
///
/// <para><b>只认 mania</b>：<c>[General] Mode</c> 不是 3 的谱面一律拒绝；键数取
/// <c>[Difficulty] CircleSize</c>，必须是 1~10。</para>
/// </summary>
public static class OsuChartReader
{
    /// <summary>osu! 列坐标画布宽度（mania 的 x 在 0~512 之间）。</summary>
    public const int PlayfieldWidth = 512;

    /// <summary>mania 的 Mode 值。</summary>
    public const int ManiaMode = 3;

    /// <summary>键数下限。</summary>
    public const int MinKeyCount = 1;

    /// <summary>键数上限。</summary>
    public const int MaxKeyCount = 10;

    /// <summary>被打断跳过的坏行（最近一次解析；成功时不保留）。</summary>
    public sealed class ParseWarnings
    {
        /// <summary>坏行说明（中文，含行号）。</summary>
        public List<string> Lines { get; } = new();
    }

    /// <summary>
    /// 读一个 .osu 文件。失败不抛异常，返回 false 并给出中文原因。
    /// </summary>
    /// <param name="path">.osu 文件路径。</param>
    /// <param name="chart">成功时的谱面；失败为 null。</param>
    /// <param name="error">失败原因；成功为 null。</param>
    /// <param name="warnings">坏行提示（可为 null）。</param>
    public static bool TryLoad(string? path, out Chart? chart, out string? error,
        ParseWarnings? warnings = null)
    {
        chart = null;
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

        string[] lines;
        try
        {
            // 谱面元数据里有中文：按 UTF-8 读，非法字节用替换符兜住（不抛异常）
            lines = File.ReadAllLines(path, new UTF8Encoding(false, false));
        }
        catch (Exception ex)
        {
            error = $"读取失败（{ex.GetType().Name}）：{ex.Message}";
            return false;
        }

        return TryParse(lines, path, out chart, out error, warnings);
    }

    /// <summary>从行数组解析（自检直接喂字符串用）。</summary>
    public static bool TryParse(IReadOnlyList<string> lines, string sourcePath,
        out Chart? chart, out string? error, ParseWarnings? warnings = null)
    {
        chart = null;
        error = null;

        var general = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var difficulty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 打击物先存原始 x：键数（CircleSize）可能写在 [HitObjects] 之后（少见但合法），
        // 所以列号换算一律等读完整份文件再做，避免拿到 -1 / 错误的键数。
        var rawNotes = new List<(double X, double Start, double End, bool IsLong)>();
        var section = "";
        string? backgroundFileName = null;
        var skipped = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i] ?? "";
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                section = close > 1 ? line[1..close] : line;
                continue;
            }

            if (line.StartsWith("//", StringComparison.Ordinal)) continue;

            switch (section)
            {
                case "General":
                    TryReadKeyValue(line, general);
                    break;
                case "Metadata":
                    TryReadKeyValue(line, metadata);
                    break;
                case "Difficulty":
                    TryReadKeyValue(line, difficulty);
                    break;
                case "Events":
                    // 只关心背景图那一行（0,0,"bg.jpg",0,0 / Background,0,0,"bg.jpg"）
                    backgroundFileName ??= ParseBackgroundFileName(line);
                    break;
                case "HitObjects":
                {
                    if (!TryParseHitObject(line, out var hit))
                    {
                        skipped++;
                        warnings?.Lines.Add($"第 {i + 1} 行打击物无法解析，已跳过");
                        break;
                    }

                    rawNotes.Add(hit);
                    break;
                }
            }
        }

        // ---- 模式 ----
        var modeId = ReadInt(general, "Mode", 0);
        if (modeId != ManiaMode)
        {
            error = $"不是 osu!mania 谱面（Mode={modeId}，需要 {ManiaMode}）。";
            return false;
        }

        // ---- 键数 ----
        var keys = ReadKeyCount(difficulty);
        if (keys < MinKeyCount || keys > MaxKeyCount)
        {
            error = $"键数 {keys} 不受支持（仅支持 {MinKeyCount}~{MaxKeyCount} 键）。";
            return false;
        }

        // ---- 打击物：x → 列号，夹取，排序 ----
        var sorted = new List<ChartNote>(rawNotes.Count);
        foreach (var raw in rawNotes)
        {
            var column = (int)Math.Floor(raw.X * keys / PlayfieldWidth);
            sorted.Add(new ChartNote(Math.Clamp(column, 0, keys - 1), raw.Start, raw.End, raw.IsLong));
        }

        sorted.Sort(static (a, b) =>
        {
            var byTime = a.StartMs.CompareTo(b.StartMs);
            return byTime != 0 ? byTime : a.Column.CompareTo(b.Column);
        });

        var noteCount = 0;
        var longCount = 0;
        var first = sorted.Count > 0 ? sorted[0].StartMs : 0;
        var last = 0.0;
        foreach (var note in sorted)
        {
            if (note.IsLongNote) longCount++;
            else noteCount++;

            var end = note.IsLongNote ? note.EndMs : note.StartMs;
            if (end > last) last = end;
        }

        var chartDir = Path.GetDirectoryName(sourcePath) ?? "";

        var audioName = general.TryGetValue("AudioFilename", out var audio) ? audio.Trim() : "";
        var backgroundName = backgroundFileName ?? "";

        chart = new Chart
        {
            FilePath = sourcePath,
            Title = Pick(metadata, "TitleUnicode", "Title"),
            Artist = Pick(metadata, "ArtistUnicode", "Artist"),
            Creator = Pick(metadata, "Creator"),
            DifficultyName = Pick(metadata, "Version"),
            Source = Pick(metadata, "Source"),
            Tags = Pick(metadata, "Tags"),
            KeyCount = keys,
            OverallDifficulty = ReadDouble(difficulty, "OverallDifficulty", 8),
            AudioFileName = audioName,
            AudioPath = ResolveExisting(chartDir, audioName) ?? "",
            BackgroundFileName = backgroundName,
            BackgroundPath = ResolveExisting(chartDir, backgroundName) ?? "",
            Notes = sorted,
            NoteCount = noteCount,
            LongNoteCount = longCount,
            FirstNoteMs = first,
            LastNoteMs = last,
        };

        return true;
    }

    /// <summary>
    /// 读键数（<c>[Difficulty] CircleSize</c>）；读不出来返回 -1。
    /// 谱面里可能写成 <c>4</c> 或 <c>4.0</c>，统一四舍五入取整。
    /// </summary>
    public static int ReadKeyCount(IReadOnlyDictionary<string, string> difficulty)
    {
        if (!difficulty.TryGetValue("CircleSize", out var text)) return -1;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return -1;
        return (int)Math.Round(value);
    }

    /// <summary>读键数（直接在文件里读一遍；失败返回 -1）。扫描谱面库时用。</summary>
    public static int ReadKeyCountFromFile(string path)
    {
        if (!TryLoad(path, out var chart, out _)) return -1;
        return chart?.KeyCount ?? -1;
    }

    /// <summary>
    /// 解析一行打击物：<c>x,y,time,type,hitSound,endTime:hitSample</c>。
    /// 长条判定看 <c>type</c> 的第 8 位（128）——与 osu! 原文一致（长度 0 的长条也算长条）。
    /// </summary>
    private static bool TryParseHitObject(string line, out (double X, double Start, double End, bool IsLong) raw)
    {
        raw = default;

        var parts = line.Split(',');
        if (parts.Length < 4) return false;

        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
        if (!double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
        if (!int.TryParse(parts[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var type)) return false;

        var isLongNote = (type & 128) != 0;
        var endTime = time;

        if (isLongNote && parts.Length > 5)
        {
            // 第 6 列是 "endTime:hitSample"；没有冒号时整段当 endTime
            var sixth = parts[5].Trim();
            var colon = sixth.IndexOf(':');
            var endText = colon >= 0 ? sixth[..colon] : sixth;
            if (double.TryParse(endText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedEnd))
                endTime = parsedEnd;
        }

        if (endTime < time) endTime = time;

        raw = (x, time, endTime, isLongNote);
        return true;
    }

    /// <summary>
    /// 从 <c>[Events]</c> 的一行里取背景图文件名（不是背景行返回 null）。
    /// 容忍 <c>0,0,"bg.jpg",0,0</c> / <c>0,0,bg.jpg</c> / <c>Background,0,0,"bg.jpg"</c>；
    /// 视频行（<c>1,</c> / <c>Video,</c>）不算。
    /// </summary>
    public static string? ParseBackgroundFileName(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var text = line.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal)) return null;
        if (text.StartsWith("1,", StringComparison.Ordinal) ||
            text.StartsWith("Video,", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var isKeyword = text.StartsWith("Background,", StringComparison.OrdinalIgnoreCase);
        if (!isKeyword && !text.StartsWith("0,", StringComparison.Ordinal)) return null;

        var quoted = ExtractQuoted(text);
        if (quoted is not null) return quoted.Length == 0 ? null : quoted;

        var parts = text.Split(',');
        if (parts.Length < 3) return null;

        var candidate = parts[2].Trim().Trim('"');
        return candidate.Length == 0 ? null : candidate;
    }

    private static string? ExtractQuoted(string text)
    {
        var start = text.IndexOf('"');
        if (start < 0) return null;

        var end = text.IndexOf('"', start + 1);
        if (end < 0) return null;

        return text[(start + 1)..end].Trim();
    }

    private static void TryReadKeyValue(string line, Dictionary<string, string> target)
    {
        var colon = line.IndexOf(':');
        if (colon <= 0) return;

        var key = line[..colon].Trim();
        var value = line[(colon + 1)..].Trim();
        if (key.Length > 0) target[key] = value;
    }

    /// <summary>取 Unicode 版本优先的文本（没有就用普通版本）。</summary>
    private static string Pick(IReadOnlyDictionary<string, string> map, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> map, string key, int fallback)
        => map.TryGetValue(key, out var text) &&
           int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double ReadDouble(IReadOnlyDictionary<string, string> map, string key, double fallback)
        => map.TryGetValue(key, out var text) &&
           double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    /// <summary>
    /// 把谱面里写的相对文件名解析成「谱面同目录下真实存在的完整路径」；不存在 / 不安全返回 null。
    /// 只接受纯文件名（不认 <c>..</c>、绝对路径、盘符）—— 谱面是外部文件，不能让它指到别处。
    /// </summary>
    public static string? ResolveExisting(string baseDirectory, string? relativeName)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory)) return null;
        if (!IsSafeRelativeName(relativeName)) return null;

        try
        {
            var full = Path.Combine(baseDirectory, relativeName!.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 相对文件名的安全检查：挡住绝对路径、盘符、<c>..</c>、备用数据流（<c>:</c>）。
    /// 允许子目录（<c>audio\hit.ogg</c> 这种老谱面确实存在）。
    /// </summary>
    public static bool IsSafeRelativeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        var text = name.Trim().Trim('"');
        if (text.Length == 0) return false;
        if (text.Contains("..", StringComparison.Ordinal)) return false;
        if (text.Contains(':', StringComparison.Ordinal)) return false;
        if (text.StartsWith('/') || text.StartsWith('\\')) return false;
        if (Path.IsPathRooted(text)) return false;

        return true;
    }
}
