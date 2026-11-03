using System.IO;
using System.Text;

namespace YuLiangToolkit.Tools.AudioTools;

/// <summary>一次分段合成需要的全部输入（纯数据，方便构造 FFmpeg 参数 / 自检）。</summary>
public sealed record SegmentMergeOptions
{
    /// <summary>输入方式。</summary>
    public SegmentSourceKind SourceKind { get; init; } = SegmentSourceKind.Playlist;

    /// <summary>播放列表地址 / 本地 m3u8 路径。</summary>
    public string Source { get; init; } = "";

    /// <summary>分段文件夹模式下的 concat 清单路径。</summary>
    public string ConcatListPath { get; init; } = "";

    /// <summary>输出文件完整路径。</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>true = <c>-c copy</c>，false = 重新编码。</summary>
    public bool CopyStreams { get; init; } = true;

    /// <summary>输出容器。</summary>
    public MergeOutputFormat OutputFormat { get; init; } = MergeOutputFormat.Mp4;

    /// <summary>是否覆盖已存在的输出文件。</summary>
    public bool Overwrite { get; init; } = true;
}

/// <summary>
/// 分段视频合成的纯逻辑（<b>不启动 FFmpeg</b>，自检 <c>audio-tools</c> 组直接断言）：
/// 分段识别 / 自然排序 / concat 清单 / FFmpeg 参数 / 输出文件名。
///
/// <para>支持两路输入：</para>
/// <list type="bullet">
///   <item><b>m3u8 播放列表</b>（本地文件或 http(s) 链接）：交给 FFmpeg 的 HLS demuxer，
///         带 <c>-allowed_extensions ALL</c> + <c>-protocol_whitelist</c>，AES 加密的 HLS 也能解；</item>
///   <item><b>分段文件夹</b>：.ts / .m4s 等按自然顺序列出，写 concat 清单后 <c>-f concat</c> 合成。</item>
/// </list>
/// </summary>
public static class SegmentMerger
{
    /// <summary>分段文件夹里认哪些扩展名（按文件名自然排序后交给 concat）。</summary>
    public static readonly string[] SegmentExtensions =
    {
        ".ts", ".m4s", ".m4v", ".mp4", ".aac", ".m4a", ".mp3", ".webm", ".mkv", ".mov", ".flv",
    };

    /// <summary>播放列表模式的 FFmpeg 协议白名单（本地 / 网络 / AES key 都要放行）。</summary>
    public const string ProtocolWhitelist = "file,http,https,tcp,tls,crypto,data";

    /// <summary>路径是不是分段文件（按扩展名，大小写不敏感）。</summary>
    public static bool IsSegmentFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return SegmentExtensions.Contains(ext, StringComparer.Ordinal);
    }

    /// <summary>扫描文件夹顶层的分段文件，按自然顺序返回完整路径。</summary>
    public static List<string> CollectSegments(string? folder)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (IsSegmentFile(file)) result.Add(file);
            }
        }
        catch
        {
            return result;
        }
        return SortNaturally(result);
    }

    /// <summary>按文件名自然排序：<c>part2.ts</c> 排在 <c>part10.ts</c> 前面。</summary>
    public static List<string> SortNaturally(IEnumerable<string>? paths)
    {
        var list = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
        list.Sort((a, b) =>
        {
            var byName = NaturalCompare(Path.GetFileName(a), Path.GetFileName(b));
            return byName != 0 ? byName : NaturalCompare(a, b);
        });
        return list;
    }

    /// <summary>自然比较字符串：连续数字按数值比，其它按字符（忽略大小写）比。</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        a ??= "";
        b ??= "";
        var i = 0;
        var j = 0;

        while (i < a.Length && j < b.Length)
        {
            var ca = a[i];
            var cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                var ia = i;
                var jb = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a[ia..i].TrimStart('0');
                var nb = b[jb..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                var cmp = string.CompareOrdinal(na, nb);
                if (cmp != 0) return cmp;
                continue;
            }

            var lower = char.ToLowerInvariant(ca).CompareTo(char.ToLowerInvariant(cb));
            if (lower != 0) return lower;
            i++;
            j++;
        }

        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>检查输入是否可用；可用返回 null，否则返回给用户看的错误文本。</summary>
    public static string? ValidateSource(SegmentSourceKind kind, string? source)
    {
        var text = source?.Trim() ?? "";
        if (text.Length == 0) return "请先选择 / 填写输入";

        if (kind == SegmentSourceKind.Folder)
        {
            if (!Directory.Exists(text)) return "分段文件夹不存在";
            var count = CollectSegments(text).Count;
            return count == 0 ? "这个文件夹里没找到 .ts / .m4s 等分段文件" : null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return null;
        }
        if (!File.Exists(text)) return "m3u8 文件不存在（也可以直接粘贴 http(s) 链接）";
        var extension = Path.GetExtension(text).ToLowerInvariant();
        return extension is ".m3u8" or ".m3u" ? null : "请选择 .m3u8 / .m3u 播放列表（或粘贴 http(s) 链接）";
    }

    /// <summary>按来源推断一个默认输出文件名（带容器扩展名）。</summary>
    public static string SuggestOutputName(string? source, SegmentSourceKind kind, MergeOutputFormat format)
    {
        var extension = format == MergeOutputFormat.Mp4 ? ".mp4" : ".mkv";
        var name = "";

        if (kind == SegmentSourceKind.Folder)
        {
            var trimmed = (source ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            name = Path.GetFileName(trimmed);
        }
        else
        {
            var text = (source ?? "").Trim();
            Uri? uri = null;
            if (Uri.TryCreate(text, UriKind.Absolute, out var parsed))
            {
                uri = parsed;
                name = Path.GetFileName(parsed.AbsolutePath);
            }
            else
            {
                name = Path.GetFileName(text);
            }
            name = Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(name) && uri?.Host is { Length: > 0 } host) name = host;
        }

        name = SanitizeFileName(name);
        if (name.Length == 0) name = "合并视频";
        return name + extension;
    }

    /// <summary>把用户输入的文件名补成目标容器扩展名（已有正确扩展名则原样保留）。</summary>
    public static string EnsureExtension(string? name, MergeOutputFormat format)
    {
        var extension = format == MergeOutputFormat.Mp4 ? ".mp4" : ".mkv";
        var text = SanitizeFileName(name);
        if (text.Length == 0) return "合并视频" + extension;
        if (text.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return text;
        var stem = Path.GetFileNameWithoutExtension(text);
        return (stem.Length == 0 ? "合并视频" : stem) + extension;
    }

    /// <summary>去掉 Windows 文件名非法字符（并去掉首尾空白 / 点）。</summary>
    public static string SanitizeFileName(string? name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return "";
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text) sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        return sb.ToString().Trim().Trim('.', ' ');
    }

    /// <summary>生成 FFmpeg concat demuxer 的清单内容（路径里的单引号要转义）。</summary>
    public static string BuildConcatListContent(IEnumerable<string> segmentPaths)
    {
        var sb = new StringBuilder();
        foreach (var path in segmentPaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            sb.Append("file '").Append(path.Replace("'", "'\\''")).Append("'\n");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 组装 FFmpeg 参数（不含 exe 路径）。播放列表直接 <c>-i</c>；文件夹模式读 concat 清单。
    /// </summary>
    public static List<string> BuildArguments(SegmentMergeOptions options)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-progress", "pipe:1", "-nostats",
            options.Overwrite ? "-y" : "-n",
        };

        if (options.SourceKind == SegmentSourceKind.Playlist)
        {
            args.AddRange(new[]
            {
                "-protocol_whitelist", ProtocolWhitelist,
                "-allowed_extensions", "ALL",
                "-i", options.Source,
            });
        }
        else
        {
            args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", options.ConcatListPath });
        }

        if (options.CopyStreams)
        {
            args.AddRange(new[] { "-c", "copy" });
        }
        else
        {
            args.AddRange(new[]
            {
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
                "-c:a", "aac", "-b:a", "192k",
            });
        }

        if (options.OutputFormat == MergeOutputFormat.Mp4)
            args.AddRange(new[] { "-movflags", "+faststart" });

        args.Add(options.OutputPath);
        return args;
    }

    /// <summary>从 FFmpeg <c>-progress pipe:1</c> 的一行里取已处理微秒数；不是进度行返回 null。</summary>
    public static long? ParseProgressMicroseconds(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var text = line.Trim();
        foreach (var key in new[] { "out_time_us=", "out_time_ms=" })
        {
            if (!text.StartsWith(key, StringComparison.Ordinal)) continue;
            return long.TryParse(text[key.Length..], out var value) ? value : null;
        }
        return null;
    }

    /// <summary>从 FFprobe 的 <c>format=duration</c> 输出里取秒数；失败返回 null。</summary>
    public static double? ParseDurationSeconds(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n'))
        {
            if (double.TryParse(line.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }
        return null;
    }
}
