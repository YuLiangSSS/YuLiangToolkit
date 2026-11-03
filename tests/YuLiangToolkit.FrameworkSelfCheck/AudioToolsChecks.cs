using System.IO;
using YuLiangToolkit.Tools.AudioTools;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「音频工具」自检：分段视频合成的纯逻辑（分段识别 / 自然排序 / concat 清单 / FFmpeg 参数 /
/// 输出文件名 / 进度解析 / 配置包装）+ 工具元数据。
/// <b>不启动 FFmpeg、不联网、不碰真 Data</b>（真正合成由人工用本地 m3u8 / 分段文件夹验收）。
/// </summary>
internal static class AudioToolsChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        CheckMetadata(check);
        CheckSegmentDetection(check);
        CheckNaturalOrder(check);
        CheckConcatList(check);
        CheckArguments(check);
        CheckOutputNames(check);
        CheckProgressParsing(check);
        CheckConfigWrappers(check);
        CheckValidation(check);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new AudioToolsTool();
        check("[音频工具] 工具 Id / 名称 / 分类 / 排序符合约定",
            tool.Id == "audio-tools" && tool.Name == "音频工具"
            && tool.Category == "媒体" && tool.Order == 10,
            $"{tool.Id} / {tool.Name} / {tool.Category} / {tool.Order}");

        check("[音频工具] 标签 / 描述覆盖视频转音频、分段合成、m3u8、FFmpeg",
            tool.Tags.Contains("视频转音频") && tool.Tags.Contains("分段合成")
            && tool.Tags.Contains("m3u8") && tool.Tags.Contains("FFmpeg")
            && tool.Description.Contains("视频转音频") && tool.Description.Contains("分段")
            && tool.Description.Contains("m3u8"),
            tool.Description);

        // 视频转音频仍是旧 ToolId，老用户的 Config\video-to-audio 原样复用
        check("[音频工具] 视频转音频沿用原 ToolId（老配置不丢）",
            VideoToAudioConfig.ToolId == "video-to-audio",
            VideoToAudioConfig.ToolId);
    }

    // ---------------------------------------------------------------- 分段识别 / 自然排序

    private static void CheckSegmentDetection(Action<string, bool, string> check)
    {
        check("[音频工具] 分段扩展名识别：ts / m4s / mp4 / aac 大小写都认，m3u8 不算分段",
            SegmentMerger.IsSegmentFile("a.ts") && SegmentMerger.IsSegmentFile("A.M4S")
            && SegmentMerger.IsSegmentFile("b.mp4") && SegmentMerger.IsSegmentFile("c.aac")
            && !SegmentMerger.IsSegmentFile("playlist.m3u8") && !SegmentMerger.IsSegmentFile("note.txt")
            && !SegmentMerger.IsSegmentFile(null) && !SegmentMerger.IsSegmentFile(""),
            "ts / M4S / mp4 / aac / m3u8 / txt");
    }

    private static void CheckNaturalOrder(Action<string, bool, string> check)
    {
        var sorted = SegmentMerger.SortNaturally(new[]
        {
            "ep10.ts", "ep2.ts", "Ep1.ts", "ep20.ts",
        });
        check("[音频工具] 分段自然排序：ep2 在 ep10 前，大小写不敏感",
            string.Join(",", sorted) == "Ep1.ts,ep2.ts,ep10.ts,ep20.ts",
            string.Join(",", sorted));

        check("[音频工具] 自然比较：数字按数值比、普通字符忽略大小写",
            SegmentMerger.NaturalCompare("a2b", "a10b") < 0
            && SegmentMerger.NaturalCompare("Part2.ts", "part10.ts") < 0
            && SegmentMerger.NaturalCompare("abc", "ABC") == 0,
            "a2b<a10b · Part2<part10 · abc=ABC");

        var dir = Path.Combine(Path.GetTempPath(), "ylt-audio-segments-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "seg10.ts"), "x");
            File.WriteAllText(Path.Combine(dir, "seg2.ts"), "x");
            File.WriteAllText(Path.Combine(dir, "note.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "list.m3u8"), "x");
            var segments = SegmentMerger.CollectSegments(dir);
            check("[音频工具] 扫描分段文件夹：只收分段、按自然顺序",
                segments.Count == 2
                && Path.GetFileName(segments[0]) == "seg2.ts"
                && Path.GetFileName(segments[1]) == "seg10.ts",
                string.Join(",", segments.Select(Path.GetFileName)));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }

    private static void CheckConcatList(Action<string, bool, string> check)
    {
        var content = SegmentMerger.BuildConcatListContent(new[]
        {
            @"C:\videos\seg1.ts",
            @"C:\videos\it's.ts",
        });
        check("[音频工具] concat 清单：每行 file '路径'，单引号转义",
            content.Contains(@"file 'C:\videos\seg1.ts'", StringComparison.Ordinal)
            && content.Contains("it'\\''s.ts", StringComparison.Ordinal)
            && content.Split('\n').Length >= 3,
            content.Replace("\r", "").Replace("\n", " | "));
    }

    // ---------------------------------------------------------------- FFmpeg 参数

    private static void CheckArguments(Action<string, bool, string> check)
    {
        var playlistCopy = SegmentMerger.BuildArguments(new SegmentMergeOptions
        {
            SourceKind = SegmentSourceKind.Playlist,
            Source = "https://cdn.x.com/live/playlist.m3u8",
            OutputPath = @"D:\out\live.mp4",
            CopyStreams = true,
            OutputFormat = MergeOutputFormat.Mp4,
        });
        check("[音频工具] 播放列表复制流参数：协议白名单 / allowed_extensions / -c copy / faststart",
            ContainsSequence(playlistCopy, "-protocol_whitelist", SegmentMerger.ProtocolWhitelist)
            && ContainsSequence(playlistCopy, "-allowed_extensions", "ALL")
            && ContainsSequence(playlistCopy, "-i", "https://cdn.x.com/live/playlist.m3u8")
            && ContainsSequence(playlistCopy, "-c", "copy")
            && ContainsSequence(playlistCopy, "-movflags", "+faststart")
            && playlistCopy[^1] == @"D:\out\live.mp4"
            && !playlistCopy.Contains("libx264"),
            string.Join(" ", playlistCopy));

        var folderReencode = SegmentMerger.BuildArguments(new SegmentMergeOptions
        {
            SourceKind = SegmentSourceKind.Folder,
            ConcatListPath = @"C:\cache\concat.txt",
            OutputPath = @"D:\out\show.mkv",
            CopyStreams = false,
            OutputFormat = MergeOutputFormat.Mkv,
        });
        check("[音频工具] 分段文件夹重编码参数：-f concat / libx264 + aac / MKV 不加 faststart",
            ContainsSequence(folderReencode, "-f", "concat")
            && ContainsSequence(folderReencode, "-safe", "0")
            && ContainsSequence(folderReencode, "-i", @"C:\cache\concat.txt")
            && folderReencode.Contains("libx264") && folderReencode.Contains("aac")
            && !folderReencode.Contains("+faststart")
            && !ContainsSequence(folderReencode, "-c", "copy")
            && folderReencode[^1] == @"D:\out\show.mkv",
            string.Join(" ", folderReencode));

        check("[音频工具] 参数开头带 -nostdin / -loglevel error / -progress pipe:1（解析进度用）",
            playlistCopy.Contains("-nostdin") && playlistCopy.Contains("error")
            && ContainsSequence(playlistCopy, "-progress", "pipe:1"),
            "nostdin / error / pipe:1");
    }

    private static bool ContainsSequence(IReadOnlyList<string> args, string first, string second)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == first && args[i + 1] == second) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 输出命名

    private static void CheckOutputNames(Action<string, bool, string> check)
    {
        check("[音频工具] 输出名：播放列表 URL 取文件名，文件夹取目录名",
            SegmentMerger.SuggestOutputName("https://cdn.x.com/a/b/playlist.m3u8?token=1",
                SegmentSourceKind.Playlist, MergeOutputFormat.Mp4) == "playlist.mp4"
            && SegmentMerger.SuggestOutputName(@"D:\Videos\My Show",
                SegmentSourceKind.Folder, MergeOutputFormat.Mkv) == "My Show.mkv",
            SegmentMerger.SuggestOutputName("https://cdn.x.com/a/b/playlist.m3u8?token=1",
                SegmentSourceKind.Playlist, MergeOutputFormat.Mp4));

        check("[音频工具] 输出名：非法字符替换、空名兜底、扩展名归一",
            SegmentMerger.SanitizeFileName("a:b*c?.mp4") == "a_b_c_.mp4"
            && SegmentMerger.EnsureExtension("demo", MergeOutputFormat.Mp4) == "demo.mp4"
            && SegmentMerger.EnsureExtension("demo.mkv", MergeOutputFormat.Mp4) == "demo.mp4"
            && SegmentMerger.EnsureExtension("", MergeOutputFormat.Mkv) == "合并视频.mkv",
            $"{SegmentMerger.SanitizeFileName("a:b*c?.mp4")} · {SegmentMerger.EnsureExtension("demo.mkv", MergeOutputFormat.Mp4)}");

        check("[音频工具] 输出名：URL 没有文件名时用主机名兜底",
            SegmentMerger.SuggestOutputName("https://example.com/", SegmentSourceKind.Playlist, MergeOutputFormat.Mp4)
                == "example.com.mp4",
            SegmentMerger.SuggestOutputName("https://example.com/", SegmentSourceKind.Playlist, MergeOutputFormat.Mp4));
    }

    // ---------------------------------------------------------------- 进度 / 时长解析

    private static void CheckProgressParsing(Action<string, bool, string> check)
    {
        check("[音频工具] FFmpeg 进度行：out_time_us / out_time_ms 都能取",
            SegmentMerger.ParseProgressMicroseconds("out_time_us=1234567") == 1234567
            && SegmentMerger.ParseProgressMicroseconds("out_time_ms=2000000") == 2000000
            && SegmentMerger.ParseProgressMicroseconds("progress=continue") is null
            && SegmentMerger.ParseProgressMicroseconds("") is null,
            "1234567 / 2000000 / null");

        check("[音频工具] ffprobe 时长解析：正常数字 / N/A / 空",
            SegmentMerger.ParseDurationSeconds("12.5\n") == 12.5
            && SegmentMerger.ParseDurationSeconds("N/A") is null
            && SegmentMerger.ParseDurationSeconds("") is null,
            "12.5 / N/A / 空");
    }

    // ---------------------------------------------------------------- 配置单选包装

    private static void CheckConfigWrappers(Action<string, bool, string> check)
    {
        var config = new SegmentMergeConfig
        {
            SourceKind = SegmentSourceKind.Folder,
            OutputFormat = MergeOutputFormat.Mkv,
            CopyStreams = false,
        };
        check("[音频工具] 配置单选包装：文件夹 / MKV / 重编码状态与枚举一致",
            config.IsFolderMode && !config.IsPlaylistMode
            && config.IsMkvOutput && !config.IsMp4Output
            && config.IsReencodeMode && !config.IsCopyMode,
            $"{config.SourceKind} / {config.OutputFormat} / copy={config.CopyStreams}");

        config.IsPlaylistMode = true;
        config.IsMp4Output = true;
        config.IsCopyMode = true;
        check("[音频工具] 配置单选包装：反向赋值能切回播放列表 / MP4 / 复制流",
            config.IsPlaylistMode && !config.IsFolderMode
            && config.IsMp4Output && !config.IsMkvOutput
            && config.IsCopyMode && !config.IsReencodeMode,
            $"{config.SourceKind} / {config.OutputFormat} / copy={config.CopyStreams}");
    }

    // ---------------------------------------------------------------- 输入校验

    private static void CheckValidation(Action<string, bool, string> check)
    {
        check("[音频工具] 输入校验：URL 通过，空输入 / 不存在的本地地址报错",
            SegmentMerger.ValidateSource(SegmentSourceKind.Playlist, "https://x.com/live.m3u8") is null
            && SegmentMerger.ValidateSource(SegmentSourceKind.Playlist, "") is not null
            && SegmentMerger.ValidateSource(SegmentSourceKind.Playlist, @"C:\no\such\list.m3u8") is not null
            && SegmentMerger.ValidateSource(SegmentSourceKind.Folder, @"C:\no\such\dir") is not null,
            "url ok / 空 / 不存在");

        var dir = Path.Combine(Path.GetTempPath(), "ylt-audio-validate-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "a.ts"), "x");
            var withSegments = SegmentMerger.ValidateSource(SegmentSourceKind.Folder, dir);
            File.Delete(Path.Combine(dir, "a.ts"));
            var withoutSegments = SegmentMerger.ValidateSource(SegmentSourceKind.Folder, dir);
            check("[音频工具] 输入校验：文件夹有分段通过、没分段报错",
                withSegments is null && withoutSegments is not null,
                $"{withSegments ?? "ok"} / {withoutSegments}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }
    }
}
