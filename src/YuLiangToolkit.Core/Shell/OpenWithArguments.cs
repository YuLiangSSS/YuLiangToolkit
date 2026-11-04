using System.IO;

namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// 「打开方式」命令行参数的解析 / 拼装（**纯逻辑**，自检直接断言）。
///
/// <para><b>注册表里的命令长这样</b>（三种入口）：</para>
/// <list type="bullet">
///   <item>按文件类型：<c>"主程序.exe" "%1"</c>（历史 PDF / 图片关联也是这么写的，所以普通启动 = 自动模式）；</item>
///   <item>以文本方式：<c>"主程序.open-text.exe" --open-as=text "%1"</c>；</item>
///   <item>以压缩包方式：<c>"主程序.open-archive.exe" --open-as=archive "%1"</c>。</item>
/// </list>
///
/// <para>解析时同时容忍 <c>--open-as=text</c> 与 <c>--open-as text</c> 两种写法；
/// 没有模式参数时（普通启动）按「第一个真实存在的文件参数 + 自动模式」处理。</para>
/// </summary>
public static class OpenWithArguments
{
    /// <summary>带等号的模式参数前缀。</summary>
    public const string ModeArgumentPrefix = "--open-as=";

    /// <summary>不带等号的模式参数（后面跟一个模式值）。</summary>
    public const string ModeArgumentName = "--open-as";

    /// <summary>把模式拼成命令行开关（注册表命令 / 单实例转发共用）。</summary>
    public static string BuildModeArgument(OpenWithMode mode) => mode switch
    {
        OpenWithMode.Text => ModeArgumentPrefix + "text",
        OpenWithMode.Archive => ModeArgumentPrefix + "archive",
        _ => ModeArgumentPrefix + "auto",
    };

    /// <summary>解析模式值（<c>text</c> / <c>archive</c> / <c>auto</c>，忽略大小写与前后空格）；不认识返回 null。</summary>
    public static OpenWithMode? ParseMode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Trim().ToLowerInvariant() switch
        {
            "text" or "txt" or "文本" => OpenWithMode.Text,
            "archive" or "zip" or "压缩包" => OpenWithMode.Archive,
            "auto" or "smart" or "自动" => OpenWithMode.Auto,
            _ => null,
        };
    }

    /// <summary>
    /// 从命令行参数里解析「模式 + 文件路径」。
    /// <para>返回 false = 这条命令行不是「用本工具打开某个文件」的请求（例如只是启动主程序）。</para>
    /// <para><b>没有模式参数</b>时要求文件参数真实存在（避免把陌生启动参数误当文件）；显式写了模式时
    /// 即使文件暂时不存在也返回 true，由具体工具给出「文件不存在」提示。</para>
    /// </summary>
    public static bool TryParse(IReadOnlyList<string>? args, out OpenWithMode mode, out string? filePath)
    {
        mode = OpenWithMode.Auto;
        filePath = null;
        if (args is null || args.Count == 0) return false;

        OpenWithMode? explicitMode = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i]?.Trim() ?? "";
            if (arg.Length == 0) continue;

            if (arg.StartsWith(ModeArgumentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                explicitMode = ParseMode(arg[ModeArgumentPrefix.Length..]) ?? explicitMode;
                continue;
            }

            if (string.Equals(arg, ModeArgumentName, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Count)
                {
                    explicitMode = ParseMode(args[i + 1]) ?? explicitMode;
                    i++;
                }

                continue;
            }

            // 其它开关（--xxx）忽略；不是开关的当候选文件路径，只取第一个
            if (arg.StartsWith("--", StringComparison.Ordinal)) continue;
            filePath ??= arg;
        }

        if (explicitMode is { } parsed)
        {
            mode = parsed;
            return !string.IsNullOrWhiteSpace(filePath);
        }

        if (string.IsNullOrWhiteSpace(filePath)) return false;

        // 普通启动：只有真实存在的文件才算「用打开方式打开」
        return IsExistingPath(filePath);
    }

    /// <summary>找出命令行里第一个真实存在的文件 / 文件夹参数（没有返回 null）。</summary>
    public static string? FindExistingPathArgument(IReadOnlyList<string>? args)
    {
        if (args is null) return null;
        foreach (var raw in args)
        {
            var arg = raw?.Trim().Trim('"') ?? "";
            if (arg.Length == 0 || arg.StartsWith("--", StringComparison.Ordinal)) continue;
            if (IsExistingPath(arg)) return arg;
        }

        return null;
    }

    /// <summary>
    /// 单实例转发前把文件参数转成绝对路径（两个进程的工作目录可能不同）；
    /// 开关参数原样保留。
    /// </summary>
    public static string[] NormalizeFileArguments(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0) return Array.Empty<string>();

        var result = new string[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i] ?? "";
            var trimmed = arg.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                result[i] = arg;
                continue;
            }

            if (!IsExistingPath(trimmed))
            {
                result[i] = arg;
                continue;
            }

            try { result[i] = Path.GetFullPath(trimmed.Trim('"')); }
            catch { result[i] = arg; }
        }

        return result;
    }

    private static bool IsExistingPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var trimmed = path.Trim().Trim('"');
            return File.Exists(trimmed) || Directory.Exists(trimmed);
        }
        catch
        {
            return false;
        }
    }
}
