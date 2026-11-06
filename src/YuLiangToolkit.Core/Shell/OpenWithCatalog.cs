namespace YuLiangToolkit.Core.Shell;

/// <summary>
/// 「按文件类型打开」（<see cref="OpenWithMode.Auto"/>）的一个分流目标：工具 Id + 工具名 + 它认领的扩展名。
/// 工具 Id 必须与对应工具工程里的 <c>XxxConfig.ToolId</c> 一致（自检会逐个核对）。
/// </summary>
public sealed record OpenWithTarget(
    string ToolId,
    string ToolName,
    string EmojiIcon,
    string Hint,
    IReadOnlyList<string> Extensions);

/// <summary>
/// 「打开方式 → 按文件类型打开」的分流表（**纯数据 + 纯逻辑**，不碰界面、不碰注册表）。
///
/// <para>这里是「扩展名 → 工具」的唯一来源：主程序启动时按它把文件交给对应工具页，
/// 「打开方式」工具页也按它显示「哪些类型会打开哪个工具」。自检直接断言。</para>
///
/// <para><b>扩展名匹配规则</b>：只取文件名，忽略大小写；多段扩展名（<c>.tar.gz</c> / <c>.tar.bz2</c> 等）
/// 按「长的优先」匹配，所以 <c>a.tar.gz</c> 不会被 <c>.gz</c> 抢走。认不出的类型由调用方
/// 用 <see cref="FallbackToolId"/> 兜底（主程序用文本编辑器，并在工具日志里提示）。</para>
/// </summary>
public static class OpenWithCatalog
{
    /// <summary>兜底工具：认不出扩展名时用文本编辑器打开（自动保存会暂停，见 IFileLaunchTarget 文档）。</summary>
    public const string FallbackToolId = "text-editor";

    /// <summary>「以文本方式打开」固定用的工具 Id。</summary>
    public const string TextToolId = "text-editor";

    /// <summary>「以压缩包方式打开」固定用的工具 Id。</summary>
    public const string ArchiveToolId = "archive";

    /// <summary>压缩包工具认领的扩展名（与 ArchiveEngine 的格式表保持一致；多段扩展名靠长优先匹配）。</summary>
    public static IReadOnlyList<string> TextArchiveExtensions { get; } = new[]
    {
        ".zip", ".zipx", ".7z", ".rar", ".tar", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tbz",
        ".tar.xz", ".txz", ".tar.zst", ".tzst", ".tar.lz", ".tlz", ".gz", ".bz2", ".xz", ".zst",
        ".ace", ".arc", ".arj", ".lzw",
    };

    /// <summary>
    /// 播放器认领的扩展名（与 MediaTypes 的音频 / 视频表保持一致）。
    /// <para>⚠ 刻意<b>不含</b> <c>.ts</c>：它同时是 TypeScript 源码（文本编辑器）和 MPEG-TS 视频。
    /// 自动模式让给文本编辑器（见 <see cref="TextExtensions"/>）；播放器本身仍然能播 .ts 文件，
    /// 从播放器里打开 / 拖进去不受影响。</para>
    /// </summary>
    public static IReadOnlyList<string> MediaExtensions { get; } = new[]
    {
        ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma", ".ogg", ".opus", ".ape", ".aiff", ".aif",
        ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm", ".flv", ".mpg", ".mpeg", ".3gp",
    };

    /// <summary>文本编辑器认领的扩展名（只用于自动分流；手动「以文本方式打开」对任意文件都可用）。</summary>
    public static IReadOnlyList<string> TextExtensions { get; } = new[]
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".ini", ".cfg", ".conf", ".config",
        ".json", ".jsonc", ".json5", ".xml", ".xaml", ".xsd", ".xsl", ".xslt",
        ".yaml", ".yml", ".toml", ".properties", ".env", ".csv", ".tsv",
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".fish",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".hh",
        ".cs", ".csproj", ".sln", ".props", ".targets", ".fs", ".fsx", ".fsproj",
        ".vb", ".vbproj", ".java", ".kt", ".kts", ".gradle", ".py", ".pyw",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".vue", ".svelte",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".php",
        ".rb", ".go", ".rs", ".swift", ".m", ".mm", ".sql", ".lua", ".r", ".pl",
        ".asm", ".s", ".diff", ".patch", ".editorconfig", ".gitignore", ".gitattributes",
        ".osu",
    };

    /// <summary>自动分流的目标列表（顺序 = 界面里展示的顺序；PDF / 图片 / 文档优先于压缩包）。</summary>
    public static IReadOnlyList<OpenWithTarget> Targets { get; } = new[]
    {
        new OpenWithTarget(
            "pdf-reader", "PDF 阅读器", "📕",
            "用系统 Windows.Data.Pdf 渲染，支持文字搜索与书签目录",
            new[] { ".pdf" }),
        new OpenWithTarget(
            "picture-viewer", "图片连续阅读器", "🖼️",
            "打开图片所在文件夹，按连续滚动 / 双页方式阅读",
            new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tif", ".tiff" }),
        new OpenWithTarget(
            "word-viewer", "Word 阅读器", "📘",
            "只读浏览 .docx / .docm（段落、表格、图片、大纲）",
            new[] { ".docx", ".docm" }),
        new OpenWithTarget(
            "excel-viewer", "Excel 阅读器", "📊",
            "只读浏览 .xlsx / .xlsm 工作簿（多工作表、单元格样式与公式）",
            new[] { ".xlsx", ".xlsm" }),
        new OpenWithTarget(
            "ppt-viewer", "PPT 阅读器", "📽️",
            "只读浏览 .pptx / .pptm / .ppsx 演示文稿（幻灯片、图片、表格、备注与全屏放映）",
            new[] { ".pptx", ".pptm", ".ppsx" }),
        new OpenWithTarget(
            "archive", "压缩包工具", "📦",
            "解压 / 创建 ZIP、7Z，解压 RAR、TAR.*、GZ、BZ2、XZ、ZST 等",
            TextArchiveExtensions),
        new OpenWithTarget(
            "media-player", "视频 / 音乐播放器", "🎬",
            "本地音频 / 视频播放，支持播放列表、随机与记忆进度",
            MediaExtensions),
        new OpenWithTarget(
            TextToolId, "文本编辑器", "📝",
            "任意文本 / 代码文件按语法高亮编辑（从「打开方式」进来时自动保存会暂停）",
            TextExtensions),
    };

    /// <summary>所有认领的扩展名（去重、按字典序；界面展示 / 自检用）。</summary>
    public static IReadOnlyList<string> AllExtensions { get; } =
        Targets.SelectMany(t => t.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly Dictionary<string, string> s_extensionToTool = BuildExtensionMap();

    // 多段扩展名按长度倒序，保证 .tar.gz 先于 .gz 命中
    private static readonly string[] s_suffixesByLengthDesc =
        Targets.SelectMany(t => t.Extensions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(e => e.Length)
            .ThenBy(e => e, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>按文件路径判断该用哪个工具（自动模式）；认不出返回 null（调用方用文本编辑器兜底）。</summary>
    public static string? ResolveToolId(string? path)
    {
        var extension = ResolveExtension(path);
        return extension is not null && s_extensionToTool.TryGetValue(extension, out var toolId)
            ? toolId
            : null;
    }

    /// <summary>按文件路径取出命中的扩展名（小写、带点；多段扩展名原样返回），认不出返回 null。</summary>
    public static string? ResolveExtension(string? path)
    {
        var name = SafeFileName(path);
        if (name.Length == 0) return null;

        var lower = name.ToLowerInvariant();
        foreach (var suffix in s_suffixesByLengthDesc)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal)) return suffix;
        }

        return null;
    }

    /// <summary>找到这个文件对应的分流目标（找不到返回 null）。</summary>
    public static OpenWithTarget? FindTarget(string? path)
    {
        var toolId = ResolveToolId(path);
        return toolId is null ? null : Targets.FirstOrDefault(t => t.ToolId == toolId);
    }

    /// <summary>界面 / 日志里显示「这个文件会怎么打开」的一句话。</summary>
    public static string DescribeRoute(string? path)
    {
        var trimmed = path?.Trim().Trim('"') ?? "";
        if (trimmed.Length == 0) return "还没有选择文件。";

        var extension = ResolveExtension(trimmed);
        var target = FindTarget(trimmed);
        if (target is not null) return $"{(extension ?? "扩展名")} → {target.ToolName}（自动模式）";

        return extension is null
            ? "文件没有扩展名 → 自动模式会用文本编辑器兜底打开。"
            : $"不认识的扩展名 {extension} → 自动模式会用文本编辑器兜底打开。";
    }

    /// <summary>扩展名（大小写、前后空格都容忍）是否在文本编辑器认领表里。</summary>
    public static bool IsTextExtension(string? extension)
        => NormalizeExtension(extension) is { } ext && TextExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>扩展名是否在压缩包工具认领表里。</summary>
    public static bool IsArchiveExtension(string? extension)
        => NormalizeExtension(extension) is { } ext && TextArchiveExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>扩展名是否在播放器认领表里。</summary>
    public static bool IsMediaExtension(string? extension)
        => NormalizeExtension(extension) is { } ext && MediaExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);

    /// <summary>规范化扩展名：去空白、补前导点、转小写；空输入返回 null。</summary>
    public static string? NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        var trimmed = extension.Trim();
        if (trimmed.Length == 0) return null;
        return (trimmed[0] == '.' ? trimmed : "." + trimmed).ToLowerInvariant();
    }

    private static string SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try
        {
            return Path.GetFileName(path.Trim().Trim('"'));
        }
        catch
        {
            return "";
        }
    }

    private static Dictionary<string, string> BuildExtensionMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in Targets)
        {
            foreach (var extension in target.Extensions)
            {
                // 同一扩展名被两个工具认领时，先出现的目标优先（PDF / 图片 / 文档排在压缩包前面）
                map.TryAdd(extension, target.ToolId);
            }
        }

        return map;
    }
}
