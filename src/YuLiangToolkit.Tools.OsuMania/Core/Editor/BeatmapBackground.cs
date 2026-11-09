using System;
using System.Collections.Generic;
using System.IO;

namespace YuLiangToolkit.Tools.OsuMania.Core.Editor;

/// <summary>
/// 谱面背景图：从 <c>[Events]</c> 段里解析背景行，以及「拖入一张图当背景」时拼出新的背景行。
///
/// <para><b>osu! 的背景事件格式</b>：<c>[Events]</c> 里逗号分隔的一行，
/// <c>0,0,"bg.jpg",0,0</c> 或 <c>0,0,bg.jpg</c>（老谱面不带引号）。
/// 第 1 列 <c>0</c> = 背景图（<c>1</c> / <c>Video</c> 是视频，<c>2</c> = break，其余是 storyboard 命令）。
/// 文件名**可能带引号**，也可能有尾随逗号写的额外参数（x,y 偏移）。</para>
///
/// <para><b>为什么要单独抽一层</b>：编辑器要能在「窗口内 / 全屏」两处显示同一张图，
/// 还要知道「同目录下到底有没有这张图」；这些判断不涉及 WPF，抽出来就能无头自检。</para>
/// </summary>
public static class BeatmapBackground
{
    /// <summary>能被当作背景图解码的扩展名（和 WPF 自带解码器一致）。</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
    };

    /// <summary>相对路径里允许出现的「文件名字符」检查：挡掉 <c>..</c>、绝对路径、驱动器号。</summary>
    public static bool IsSafeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        var name = fileName.Trim().Trim('"');
        if (name.Length == 0) return false;
        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.Contains(':', StringComparison.Ordinal)) return false;   // 驱动器 / 备用数据流
        if (name.StartsWith('/') || name.StartsWith('\\')) return false;  // 绝对路径
        if (Path.IsPathRooted(name)) return false;

        return true;
    }

    /// <summary>扩展名能不能当背景图。</summary>
    public static bool IsSupportedImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        foreach (var supported in SupportedExtensions)
        {
            if (ext.Equals(supported, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 从 <c>[Events]</c> 的背景行里取文件名（失败返回 null）。
    ///
    /// <para>容忍这些写法：<c>0,0,"bg.jpg",0,0</c>、<c>0,0,bg.jpg</c>、
    /// <c>0,0,"bg.jpg"</c>、<c>Background,0,0,"bg.jpg"</c>（新格式）。</para>
    /// </summary>
    public static string? ParseBackgroundLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var text = line.Trim();
        if (text.StartsWith("//", StringComparison.Ordinal)) return null;

        // 视频事件（1,...）不算背景图
        if (text.StartsWith("1,", StringComparison.Ordinal) ||
            text.StartsWith("Video,", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // 新版写法：Background,0,0,"bg.jpg"
        var isBackgroundKeyword = text.StartsWith("Background,", StringComparison.OrdinalIgnoreCase);

        if (!isBackgroundKeyword && !text.StartsWith("0,", StringComparison.Ordinal)) return null;

        // 取第 3 个字段（索引 2）：优先按引号抓，其次按逗号切
        var quoted = ExtractQuoted(text);
        if (quoted is not null) return quoted.Length == 0 ? null : quoted;

        var parts = text.Split(',');
        if (parts.Length < 3) return null;

        var candidate = parts[2].Trim().Trim('"');
        return candidate.Length == 0 ? null : candidate;
    }

    /// <summary>从整段行里抓第一个双引号串（osu! 用双引号包文件名）。</summary>
    private static string? ExtractQuoted(string text)
    {
        var start = text.IndexOf('"');
        if (start < 0) return null;

        var end = text.IndexOf('"', start + 1);
        if (end < 0) return null;

        return text[(start + 1)..end].Trim();
    }

    /// <summary>
    /// 把「用户选的图片」写成一行 osu! 背景事件。
    /// </summary>
    /// <param name="fileName">相对谱面目录的文件名（只允许纯文件名）。</param>
    public static string BuildBackgroundLine(string fileName)
    {
        if (!IsSafeFileName(fileName)) throw new ArgumentException("文件名不安全。", nameof(fileName));
        return $"0,0,\"{fileName.Trim().Trim('"')}\",0,0";
    }

    /// <summary>
    /// 在谱面的 <c>[Events]</c> 段里**替换**背景行（没有就插到段首）。
    ///
    /// <para><b>为什么要按行改</b>：编辑器的保存策略是「只重建 TimingPoints / HitObjects 两段正文，
    /// 其它行原样保留」；换背景属于改其它段，所以这里单独做一次精确的行替换，
    /// 不碰 <c>[Events]</c> 里的 break / storyboard 行。</para>
    /// </summary>
    /// <param name="lines">原始行（会被就地修改）。</param>
    /// <param name="newLine">新的背景行（<see cref="BuildBackgroundLine"/> 生成）。</param>
    /// <returns>true = 改动过；false = 没有 [Events] 段（调用方需要先补一段）。</returns>
    public static bool TryReplaceBackgroundLine(List<string> lines, string newLine)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));
        if (string.IsNullOrWhiteSpace(newLine)) return false;

        var section = "";
        var eventsHeader = -1;
        var firstReplaceable = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var raw = lines[i] ?? "";
            var text = raw.Trim();

            if (text.Length > 0 && text[0] == '[')
            {
                var close = text.IndexOf(']');
                section = close > 1 ? text[1..close] : text;
                if (section.Equals("Events", StringComparison.OrdinalIgnoreCase))
                {
                    eventsHeader = i;
                }
                else if (eventsHeader >= 0 && i > eventsHeader)
                {
                    // [Events] 段结束
                    break;
                }
                continue;
            }

            if (section != "Events") continue;
            if (text.StartsWith("//", StringComparison.Ordinal)) continue;

            if (ParseBackgroundLine(text) is not null)
            {
                lines[i] = newLine;
                return true;
            }

            // 记下第一个「非注释、非空」的位置：没有背景行时插在这里
            if (firstReplaceable < 0 && text.Length > 0) firstReplaceable = i;
        }

        if (eventsHeader < 0) return false;

        // 段里还没有背景行：插到段首（跳过段落标题后面紧跟的注释行）
        var insertAt = firstReplaceable >= 0 ? firstReplaceable : eventsHeader + 1;
        lines.Insert(insertAt, newLine);
        return true;
    }

    /// <summary>
    /// 在文件末尾补一个 <c>[Events]</c> 段并写入背景行（谱面原来没有这个段时用）。
    /// </summary>
    public static void AppendEventsSection(List<string> lines, string backgroundLine)
    {
        if (lines is null) throw new ArgumentNullException(nameof(lines));

        // 末尾先补个空行，别和上一行粘在一起
        if (lines.Count > 0 && (lines[^1] ?? "").Trim().Length > 0) lines.Add("");

        lines.Add("[Events]");
        lines.Add("//Background and Video events");
        lines.Add(backgroundLine);
        lines.Add("//Break Periods");
    }

    /// <summary>
    /// 由谱面目录 + 文件名得到候选完整路径（不存在返回 null）。
    /// </summary>
    public static string? ResolveExisting(string chartDirectory, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(chartDirectory)) return null;
        if (!IsSafeFileName(fileName)) return null;

        try
        {
            var full = Path.Combine(chartDirectory, fileName!.Trim().Trim('"'));
            return File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
