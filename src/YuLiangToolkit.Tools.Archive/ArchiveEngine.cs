using System.Diagnostics;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpCompress.Writers.SevenZip;
using SharpCompress.Writers.Zip;
using SharpCompressCryptographicException = SharpCompress.Common.CryptographicException;

namespace YuLiangToolkit.Tools.Archive;

// =====================================================================================
// 「压缩包工具」的纯逻辑层（不引用 WPF，方便自检直接跑）。
// 引擎对外只有本文件的类型；界面层（Views / ViewModels）只依赖这里的公开成员。
//
// 支持的格式与职责边界：
//   · 解压：ZIP、RAR（RAR4 / RAR5）、7Z、TAR、TAR.GZ / TGZ、TAR.BZ2 / TBZ2、TAR.XZ / TXZ、
//           TAR.ZST、GZ、BZ2、XZ、ZST；另外顺带支持 ACE / ARC / ARJ / LZW 等老格式（只解不压）。
//   · 创建：ZIP、7Z（RAR 因版权原因只能解不能压；TAR 系列暂不做创建）。
//   · 密码：解压支持密码（ZIP 传统加密 / AES、RAR、7Z）；创建暂不支持加密。
//   · 文件名编码：解压可指定「旧版压缩包文件名编码」（如 GBK/936），默认 UTF-8 + 自动回退。
//
// 实现要点（契约 2.1）：
//   · 所有公开方法都不抛异常，失败返回带中文 Error 的结果；
//   · Probe / Extract / Create 都支持取消，每 64KB 检查一次取消；
//   · 解压前把条目 key 统一成 / 分隔并复核最终路径落在目标目录内（防 zip-slip）；
//   · 覆盖策略 Overwrite / Skip / Rename；Rename 用「a (1).txt」；
//   · 7Z 用 ArchiveFactory（ReaderFactory 不支持 7z）；压缩版 TAR / RAR（含固实）用 ReaderFactory；
//   · 单独一个 .bz2 / .xz / .zst / .lzw 用 SharpCompress 压缩流解压（ReaderFactory 不认）；
//   · 创建 ZIP / 7Z 用 SharpCompress 写入器；7Z 只支持 LZMA/LZMA2（「仅存储」降级为 LZMA2 最低级别）。
// =====================================================================================

/// <summary>压缩包容器格式。</summary>
public enum ArchiveFormat
{
    Unknown = 0,
    Zip,
    SevenZip,
    Rar,
    Tar,
    TarGz,
    TarBz2,
    TarXz,
    TarZstd,
    TarLz,
    GZip,
    BZip2,
    Xz,
    Zstd,
    Ace,
    Arc,
    Arj,
    Lzw,
}

/// <summary>解压时目标文件已存在怎么办。</summary>
public enum OverwriteMode
{
    /// <summary>直接覆盖。</summary>
    Overwrite,
    /// <summary>跳过已存在的文件（保留原文件）。</summary>
    Skip,
    /// <summary>重命名新文件，例如 <c>a.txt</c> → <c>a (1).txt</c>（默认，最安全）。</summary>
    Rename,
}

/// <summary>压缩级别（界面上的「仅存储 / 最快 / 标准 / 极限」）。</summary>
public enum CompressionPreset
{
    Store,
    Fastest,
    Normal,
    Maximum,
}

/// <summary>一种格式的展示信息 / 能力。</summary>
public sealed class ArchiveFormatInfo
{
    public required ArchiveFormat Format { get; init; }

    /// <summary>界面显示名，例如「7z 压缩包」。</summary>
    public required string DisplayName { get; init; }

    /// <summary>主扩展名（带点），例如 <c>.7z</c>；无固定扩展名时为空。</summary>
    public string Extension { get; init; } = "";

    /// <summary>是否支持解压。</summary>
    public bool CanExtract { get; init; }

    /// <summary>是否支持创建（本工具只有 ZIP / 7Z 为 true）。</summary>
    public bool CanCreate { get; init; }

    /// <summary>是否「单文件压缩」（GZ / BZ2 / XZ / ZST：解出来只有一个文件，没有目录结构）。</summary>
    public bool IsSingleStream { get; init; }
}

/// <summary>压缩包里的一个条目（用于预览 / 判断总大小）。</summary>
public sealed class ArchiveEntryInfo
{
    /// <summary>包内路径（统一用 <c>/</c> 分隔；目录条目以 <c>/</c> 结尾或 IsDirectory=true）。</summary>
    public required string Key { get; init; }

    /// <summary>解压后大小（字节）；未知时为 -1。</summary>
    public long Size { get; init; }

    /// <summary>压缩后大小（字节）；未知时为 -1。</summary>
    public long CompressedSize { get; init; }

    public bool IsDirectory { get; init; }

    public bool IsEncrypted { get; init; }

    public DateTime? LastModifiedTime { get; init; }
}

/// <summary>探测压缩包的结果（永远不抛异常，失败时看 <see cref="Success"/> / <see cref="Error"/>）。</summary>
public sealed class ArchiveProbeResult
{
    public bool Success { get; init; }

    /// <summary>失败原因（中文，可直接显示给用户）。</summary>
    public string? Error { get; init; }

    public ArchiveFormat Format { get; init; }

    /// <summary>解压后的总字节数（所有文件条目之和；未知时 0）。</summary>
    public long TotalSize { get; init; }

    /// <summary>条目数（目录也算）。</summary>
    public int EntryCount { get; init; }

    /// <summary>文件条目数（不含目录）。</summary>
    public int FileCount { get; init; }

    /// <summary>是否加密（需要密码）。</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>压缩包注释（没有则为 null）。</summary>
    public string? Comment { get; init; }

    /// <summary>条目清单；条目特别多时只返回前 <c>maxEntries</c> 条（见 <see cref="EntriesTruncated"/>）。</summary>
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; init; } = Array.Empty<ArchiveEntryInfo>();

    public bool EntriesTruncated { get; init; }
}

/// <summary>解压选项。</summary>
public sealed class ArchiveExtractOptions
{
    /// <summary>解压到哪个目录（必须非空；不存在会自动创建）。</summary>
    public string DestinationDirectory { get; set; } = "";

    /// <summary>为每个压缩包单独建一个子文件夹（名字取压缩包文件名）。</summary>
    public bool CreateSubfolderPerArchive { get; set; }

    public OverwriteMode Overwrite { get; set; } = OverwriteMode.Rename;

    /// <summary>密码（没有密码就留空）。</summary>
    public string? Password { get; set; }

    /// <summary>旧版压缩包的文件名编码（例如 GBK = <c>Encoding.GetEncoding(936)</c>）；null = 自动（UTF-8 优先）。</summary>
    public Encoding? LegacyEncoding { get; set; }

    /// <summary>是否保留文件修改时间。</summary>
    public bool PreserveTimestamps { get; set; } = true;
}

/// <summary>创建压缩包选项。</summary>
public sealed class ArchiveCreateOptions
{
    /// <summary>目标格式（仅 <see cref="ArchiveFormats.CanCreate"/> 为 true 的格式可用）。</summary>
    public ArchiveFormat Format { get; set; } = ArchiveFormat.SevenZip;

    public CompressionPreset Level { get; set; } = CompressionPreset.Normal;

    /// <summary>是否把空文件夹也写进压缩包。</summary>
    public bool IncludeEmptyDirectories { get; set; } = true;
}

/// <summary>进度快照（回调可能在后台线程触发，界面层自己切 UI 线程）。</summary>
public sealed class ArchiveProgress
{
    public int FilesDone { get; init; }

    /// <summary>总文件数；未知时为 0。</summary>
    public int FilesTotal { get; init; }

    public long BytesDone { get; init; }

    /// <summary>总字节数；未知时为 0。</summary>
    public long BytesTotal { get; init; }

    /// <summary>当前处理的条目名。</summary>
    public string CurrentEntry { get; init; } = "";

    /// <summary>0.0 ~ 1.0；总量未知时返回 -1（界面可据此显示「不确定进度」）。</summary>
    public double Percent
    {
        get
        {
            if (BytesTotal > 0) return Math.Clamp((double)BytesDone / BytesTotal, 0, 1);
            if (FilesTotal > 0) return Math.Clamp((double)FilesDone / FilesTotal, 0, 1);
            return -1;
        }
    }
}

/// <summary>解压 / 创建的执行结果（失败 / 取消都不抛异常）。</summary>
public sealed class ArchiveOperationResult
{
    public bool Success { get; init; }

    /// <summary>用户取消。</summary>
    public bool Canceled { get; init; }

    /// <summary>失败原因（中文）；成功时为 null。</summary>
    public string? Error { get; init; }

    /// <summary>处理的文件数。</summary>
    public int FileCount { get; init; }

    /// <summary>处理的字节数（解压 = 写出字节数，创建 = 读入字节数）。</summary>
    public long TotalBytes { get; init; }

    /// <summary>因为「已存在 + 跳过」或「路径不安全」而没处理的条目数。</summary>
    public int SkippedCount { get; init; }

    /// <summary>解压目录或创建的压缩包完整路径。</summary>
    public string? OutputPath { get; init; }

    /// <summary>非致命提示（例如跳过越界路径、跳过无权限文件）。</summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public TimeSpan Elapsed { get; init; }
}

/// <summary>格式识别与格式能力表（纯函数）。</summary>
public static class ArchiveFormats
{
    static ArchiveFormats()
    {
        // .NET Core 默认不带 GBK/GB18030 等代码页，旧版 ZIP 文件名要靠它。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>所有格式（顺序即界面下拉框顺序）。</summary>
    public static IReadOnlyList<ArchiveFormatInfo> All { get; } =
    [
        new() { Format = ArchiveFormat.Zip, DisplayName = "ZIP 压缩包", Extension = ".zip", CanExtract = true, CanCreate = true },
        new() { Format = ArchiveFormat.SevenZip, DisplayName = "7Z 压缩包", Extension = ".7z", CanExtract = true, CanCreate = true },
        new() { Format = ArchiveFormat.Rar, DisplayName = "RAR 压缩包", Extension = ".rar", CanExtract = true },
        new() { Format = ArchiveFormat.Tar, DisplayName = "TAR 归档", Extension = ".tar", CanExtract = true },
        new() { Format = ArchiveFormat.TarGz, DisplayName = "TAR.GZ 压缩包", Extension = ".tar.gz", CanExtract = true },
        new() { Format = ArchiveFormat.TarBz2, DisplayName = "TAR.BZ2 压缩包", Extension = ".tar.bz2", CanExtract = true },
        new() { Format = ArchiveFormat.TarXz, DisplayName = "TAR.XZ 压缩包", Extension = ".tar.xz", CanExtract = true },
        new() { Format = ArchiveFormat.TarZstd, DisplayName = "TAR.ZST 压缩包", Extension = ".tar.zst", CanExtract = true },
        new() { Format = ArchiveFormat.GZip, DisplayName = "GZ 压缩文件", Extension = ".gz", CanExtract = true, IsSingleStream = true },
        new() { Format = ArchiveFormat.BZip2, DisplayName = "BZ2 压缩文件", Extension = ".bz2", CanExtract = true, IsSingleStream = true },
        new() { Format = ArchiveFormat.Xz, DisplayName = "XZ 压缩文件", Extension = ".xz", CanExtract = true, IsSingleStream = true },
        new() { Format = ArchiveFormat.Zstd, DisplayName = "ZST 压缩文件", Extension = ".zst", CanExtract = true, IsSingleStream = true },
        new() { Format = ArchiveFormat.TarLz, DisplayName = "TAR.LZ 压缩包", Extension = ".tar.lz", CanExtract = true },
        new() { Format = ArchiveFormat.Ace, DisplayName = "ACE 压缩包", Extension = ".ace", CanExtract = true },
        new() { Format = ArchiveFormat.Arc, DisplayName = "ARC 压缩包", Extension = ".arc", CanExtract = true },
        new() { Format = ArchiveFormat.Arj, DisplayName = "ARJ 压缩包", Extension = ".arj", CanExtract = true },
        new() { Format = ArchiveFormat.Lzw, DisplayName = "LZW 压缩文件", Extension = ".lzw", CanExtract = true, IsSingleStream = true },
    ];

    /// <summary>每个可解压格式的扩展名通配符（顺序有意义：复合扩展名在前）。</summary>
    private static readonly Dictionary<ArchiveFormat, string[]> s_patterns = new()
    {
        [ArchiveFormat.Zip] = ["*.zip", "*.zipx"],
        [ArchiveFormat.SevenZip] = ["*.7z"],
        [ArchiveFormat.Rar] = ["*.rar"],
        [ArchiveFormat.Tar] = ["*.tar"],
        [ArchiveFormat.TarGz] = ["*.tar.gz", "*.tgz"],
        [ArchiveFormat.TarBz2] = ["*.tar.bz2", "*.tbz2", "*.tbz"],
        [ArchiveFormat.TarXz] = ["*.tar.xz", "*.txz"],
        [ArchiveFormat.TarZstd] = ["*.tar.zst", "*.tzst"],
        [ArchiveFormat.TarLz] = ["*.tar.lz", "*.tlz"],
        [ArchiveFormat.GZip] = ["*.gz"],
        [ArchiveFormat.BZip2] = ["*.bz2", "*.bzip2"],
        [ArchiveFormat.Xz] = ["*.xz"],
        [ArchiveFormat.Zstd] = ["*.zst", "*.zstd"],
        [ArchiveFormat.Ace] = ["*.ace"],
        [ArchiveFormat.Arc] = ["*.arc"],
        [ArchiveFormat.Arj] = ["*.arj"],
        [ArchiveFormat.Lzw] = ["*.lzw", "*.lz"],
    };

    /// <summary>扩展名 → 格式（长扩展名在前，避免 <c>.tar.gz</c> 被 <c>.gz</c> 抢走）。</summary>
    private static readonly (string Extension, ArchiveFormat Format)[] s_extensionMap =
    [
        (".tar.gz", ArchiveFormat.TarGz),
        (".tar.bz2", ArchiveFormat.TarBz2),
        (".tar.xz", ArchiveFormat.TarXz),
        (".tar.zst", ArchiveFormat.TarZstd),
        (".tar.lz", ArchiveFormat.TarLz),
        (".tgz", ArchiveFormat.TarGz),
        (".tbz2", ArchiveFormat.TarBz2),
        (".tbz", ArchiveFormat.TarBz2),
        (".txz", ArchiveFormat.TarXz),
        (".tzst", ArchiveFormat.TarZstd),
        (".tlz", ArchiveFormat.TarLz),
        (".tar", ArchiveFormat.Tar),
        (".7z", ArchiveFormat.SevenZip),
        (".zipx", ArchiveFormat.Zip),
        (".zip", ArchiveFormat.Zip),
        (".rar", ArchiveFormat.Rar),
        (".bzip2", ArchiveFormat.BZip2),
        (".bz2", ArchiveFormat.BZip2),
        (".gz", ArchiveFormat.GZip),
        (".xz", ArchiveFormat.Xz),
        (".zstd", ArchiveFormat.Zstd),
        (".zst", ArchiveFormat.Zstd),
        (".ace", ArchiveFormat.Ace),
        (".arc", ArchiveFormat.Arc),
        (".arj", ArchiveFormat.Arj),
        (".lzw", ArchiveFormat.Lzw),
        (".lz", ArchiveFormat.Lzw),
    ];

    /// <summary>可解压格式的扩展名通配符清单（拖放判断用），例如 <c>*.zip</c>。</summary>
    public static IReadOnlyList<string> ExtractExtensionPatterns { get; } = BuildExtractExtensionPatterns();

    public static ArchiveFormatInfo Info(ArchiveFormat format)
        => All.FirstOrDefault(f => f.Format == format)
           ?? new ArchiveFormatInfo { Format = ArchiveFormat.Unknown, DisplayName = "未知格式" };

    /// <summary>扩展名判断（快，不看内容）。</summary>
    public static ArchiveFormat Detect(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return ArchiveFormat.Unknown;
            var trimmed = path.Trim().TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) return ArchiveFormat.Unknown;

            foreach (var (extension, format) in s_extensionMap)
            {
                if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return format;
            }

            return ArchiveFormat.Unknown;
        }
        catch
        {
            return ArchiveFormat.Unknown;
        }
    }

    /// <summary>读文件头判断（慢一点，但改了扩展名 / SFX 也认得出）；读不了返回 Unknown。</summary>
    public static ArchiveFormat DetectByContent(string path) => ArchiveContentDetector.Detect(path);

    /// <summary>先 <see cref="DetectByContent"/> 再退回 <see cref="Detect"/>（界面统一用这个）。</summary>
    public static ArchiveFormat DetectBest(string path)
    {
        try
        {
            var byContent = DetectByContent(path);
            return byContent != ArchiveFormat.Unknown ? byContent : Detect(path);
        }
        catch
        {
            return ArchiveFormat.Unknown;
        }
    }

    /// <summary>扩展名与内容都对不上时返回 false（拖入的普通文件不算压缩包）。</summary>
    public static bool IsArchivePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            if (Directory.Exists(path)) return false;
            return DetectBest(path) != ArchiveFormat.Unknown;
        }
        catch
        {
            return false;
        }
    }

    public static bool CanCreate(ArchiveFormat format) => Info(format).CanCreate;

    /// <summary>去掉压缩包扩展名后的文件名（<c>a.tar.gz</c> → <c>a</c>；<c>b.zip</c> → <c>b</c>）。</summary>
    public static string GetBaseName(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            var trimmed = path.Trim().TrimEnd('\\', '/');
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrEmpty(name)) return "";

            foreach (var (extension, _) in s_extensionMap)
            {
                if (name.Length > extension.Length && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return name[..^extension.Length];
                }
            }

            var withoutExtension = Path.GetFileNameWithoutExtension(name);
            return string.Equals(withoutExtension, name, StringComparison.Ordinal) ? name : withoutExtension;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>OpenFileDialog 的 Filter 串（含「所有支持的压缩包」聚合项）。</summary>
    public static string BuildOpenFileFilter()
    {
        var builder = new StringBuilder();
        builder.Append("所有支持的压缩包|").Append(string.Join(";", ExtractExtensionPatterns));
        foreach (var info in All)
        {
            if (!info.CanExtract) continue;
            var patterns = GetPatterns(info.Format);
            if (patterns.Count == 0) continue;
            builder.Append('|')
                .Append(info.DisplayName)
                .Append(" (").Append(string.Join("; ", patterns)).Append(")|")
                .Append(string.Join(";", patterns));
        }

        builder.Append("|所有文件|*.*");
        return builder.ToString();
    }

    /// <summary>SaveFileDialog 的 Filter 串（只有 ZIP / 7Z）。</summary>
    public static string BuildSaveFileFilter()
    {
        var parts = new List<string>();
        foreach (var info in All)
        {
            if (!info.CanCreate) continue;
            parts.Add($"{info.DisplayName} (*{info.Extension})|*{info.Extension}");
        }

        return string.Join("|", parts);
    }

    /// <summary>给输出路径补上格式扩展名（已有同格式扩展名就不动）。</summary>
    public static string EnsureExtension(string path, ArchiveFormat format)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            var extension = Info(format).Extension;
            if (string.IsNullOrEmpty(extension)) return path;
            var trimmed = path.TrimEnd();
            return trimmed.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? trimmed : trimmed + extension;
        }
        catch
        {
            return path ?? "";
        }
    }

    private static IReadOnlyList<string> GetPatterns(ArchiveFormat format)
        => s_patterns.TryGetValue(format, out var patterns) ? patterns : Array.Empty<string>();

    private static IReadOnlyList<string> BuildExtractExtensionPatterns()
    {
        var patterns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in All)
        {
            if (!info.CanExtract) continue;
            foreach (var pattern in GetPatterns(info.Format))
            {
                if (seen.Add(pattern)) patterns.Add(pattern);
            }
        }

        return patterns;
    }
}

/// <summary>
/// 压缩包引擎：探测 / 解压 / 创建。
/// <para>所有方法都不碰界面，失败返回带中文 <see cref="ArchiveOperationResult.Error"/> 的结果。</para>
/// </summary>
public static class ArchiveEngine
{
    private const int CopyBufferSize = 64 * 1024;

    /// <summary>
    /// 读压缩包清单（不解压）。<paramref name="maxEntries"/> 限制返回的条目数（0 = 全部）。
    /// </summary>
    public static ArchiveProbeResult Probe(
        string archivePath,
        string? password = null,
        Encoding? legacyEncoding = null,
        int maxEntries = 1000,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return FailProbe("压缩包路径为空");
            if (!File.Exists(archivePath)) return FailProbe($"压缩包不存在：{archivePath}");
            return ProbeCore(archivePath, password, legacyEncoding, maxEntries, validatePassword: true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return FailProbe("已取消");
        }
        catch (Exception ex)
        {
            return FailProbe(TranslateError(ex, password));
        }
    }

    /// <summary>解压一个压缩包。</summary>
    public static ArchiveOperationResult Extract(
        string archivePath,
        ArchiveExtractOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        ExtractSession? session = null;
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return FailOperation("压缩包路径为空", null, watch);
            if (!File.Exists(archivePath)) return FailOperation($"压缩包不存在：{archivePath}", null, watch);
            if (options is null) return FailOperation("没有指定解压选项", null, watch);
            if (string.IsNullOrWhiteSpace(options.DestinationDirectory)) return FailOperation("没有指定解压目录", null, watch);

            var format = ArchiveFormats.DetectBest(archivePath);
            if (format == ArchiveFormat.Unknown) return FailOperation("不是有效的压缩包", null, watch);

            var destinationRoot = Path.GetFullPath(options.DestinationDirectory);
            if (options.CreateSubfolderPerArchive)
            {
                var subfolder = ArchiveFormats.GetBaseName(archivePath);
                if (!string.IsNullOrWhiteSpace(subfolder)) destinationRoot = Path.Combine(destinationRoot, subfolder);
            }

            Directory.CreateDirectory(destinationRoot);
            session = new ExtractSession(archivePath, format, destinationRoot, options, progress, cancellationToken, watch);

            // 能在开始前快速拿到总量的格式先 Probe 一次（压缩版 TAR 要边解边读，跳过）。
            if (!IsCompressedTar(format))
            {
                var probe = ProbeCore(archivePath, options.Password, options.LegacyEncoding, 0,
                    validatePassword: false, cancellationToken);
                if (probe.Success)
                {
                    session.FilesTotal = probe.FileCount;
                    session.BytesTotal = probe.TotalSize;
                }
                else if (!IsSingleStreamFormat(format))
                {
                    return FailOperation(probe.Error ?? "无法读取压缩包", destinationRoot, watch);
                }
            }

            if (format == ArchiveFormat.SevenZip)
            {
                ExtractSevenZip(archivePath, options.Password, options.LegacyEncoding, session, cancellationToken);
            }
            else
            {
                ExtractWithReader(archivePath, format, options.Password, options.LegacyEncoding, session, cancellationToken);
            }

            session.ApplyDirectoryTimestamps();
            return session.ToResult(success: true);
        }
        catch (OperationCanceledException)
        {
            return session?.ToCanceled() ?? FailOperation("已取消", null, watch, canceled: true);
        }
        catch (Exception ex)
        {
            return FailOperation(TranslateError(ex, options?.Password), session?.DestinationRoot, watch);
        }
    }

    /// <summary>把文件 / 文件夹创建成 ZIP 或 7Z 压缩包。</summary>
    public static ArchiveOperationResult Create(
        string outputPath,
        ArchiveCreateOptions options,
        IReadOnlyList<string> sourcePaths,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var skipped = 0;
        string? outputFull = null;
        try
        {
            if (options is null) return FailOperation("没有指定压缩选项", null, watch);
            if (!ArchiveFormats.CanCreate(options.Format))
            {
                var message = options.Format == ArchiveFormat.Rar
                    ? "RAR 是专有格式，受版权限制只能解压、不能创建"
                    : $"{ArchiveFormats.Info(options.Format).DisplayName} 不支持创建；本工具只能创建 ZIP / 7Z 压缩包";
                return FailOperation(message, null, watch);
            }

            if (string.IsNullOrWhiteSpace(outputPath)) return FailOperation("输出路径为空", null, watch);
            if (sourcePaths is null || sourcePaths.Count == 0) return FailOperation("没有要压缩的文件或文件夹", null, watch);

            outputFull = Path.GetFullPath(ArchiveFormats.EnsureExtension(outputPath.Trim(), options.Format));
            var plan = BuildCreatePlan(sourcePaths, outputFull, options.IncludeEmptyDirectories,
                warnings, ref skipped, cancellationToken);

            if (plan.Files.Count == 0 && plan.Directories.Count == 0)
            {
                return FailOperation("没有可压缩的文件或文件夹", outputFull, watch);
            }

            var outputDirectory = Path.GetDirectoryName(outputFull);
            if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

            var filesTotal = plan.Files.Count;
            var bytesTotal = plan.Files.Sum(f => f.Size);
            var filesDone = 0;
            long bytesDone = 0;

            try
            {
                using (var outputStream = new FileStream(outputFull, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = CreateWriter(outputStream, options.Format, options.Level, warnings))
                {
                    foreach (var directory in plan.Directories)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        writer.WriteDirectory(directory.Key, directory.LastWriteUtc);
                    }

                    foreach (var file in plan.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        FileStream source;
                        try
                        {
                            source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        }
                        catch (Exception ex) when (IsRecoverableIo(ex))
                        {
                            warnings.Add($"跳过无法读取的文件：{file.FullPath}（{ex.Message}）");
                            skipped++;
                            continue;
                        }

                        long readBytes;
                        using (source)
                        using (var cancelAware = new CancelCheckingStream(source, cancellationToken))
                        {
                            writer.Write(file.Key, cancelAware, file.LastWriteUtc);
                            readBytes = cancelAware.BytesRead;
                        }

                        filesDone++;
                        bytesDone += readBytes > 0 ? readBytes : file.Size;
                        SafeReport(progress, new ArchiveProgress
                        {
                            FilesDone = filesDone,
                            FilesTotal = filesTotal,
                            BytesDone = bytesDone,
                            BytesTotal = bytesTotal,
                            CurrentEntry = file.Key,
                        });
                    }
                }
            }
            catch
            {
                // 失败 / 取消：删掉写了一半的压缩包（界面会先问过用户是否覆盖旧文件）。
                TryDelete(outputFull);
                throw;
            }

            return new ArchiveOperationResult
            {
                Success = true,
                FileCount = filesDone,
                TotalBytes = bytesDone,
                OutputPath = outputFull,
                SkippedCount = skipped,
                Warnings = warnings.ToArray(),
                Elapsed = watch.Elapsed,
            };
        }
        catch (OperationCanceledException)
        {
            return FailOperation("已取消", outputFull, watch, canceled: true, warnings, skipped);
        }
        catch (Exception ex)
        {
            return FailOperation(TranslateError(ex, null), outputFull, watch, warnings: warnings, skipped: skipped);
        }
    }

    /// <summary>
    /// 给解压目标算默认目录：压缩包所在目录 + 去掉扩展名的文件名（<c>D:\a\b.7z</c> → <c>D:\a\b</c>）。
    /// </summary>
    public static string SuggestExtractDirectory(string archivePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(archivePath)) return "";
            var full = Path.GetFullPath(archivePath.Trim());
            var directory = Path.GetDirectoryName(full) ?? "";
            var baseName = ArchiveFormats.GetBaseName(full);
            if (string.IsNullOrWhiteSpace(baseName)) baseName = "解压";
            return Path.Combine(directory, baseName);
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 给创建压缩包算默认输出路径：放在第一个源旁边，名字取「单个文件/文件夹名」，多个源用「压缩包-年月日-时分秒」。
    /// </summary>
    public static string SuggestArchivePath(IReadOnlyList<string> sourcePaths, ArchiveFormat format, string? outputDirectory = null)
    {
        try
        {
            var (stem, directory) = GetSuggestedStemAndDirectory(sourcePaths);
            if (!string.IsNullOrWhiteSpace(outputDirectory)) directory = outputDirectory.Trim();
            if (string.IsNullOrWhiteSpace(directory)) directory = Environment.CurrentDirectory;

            var extension = ArchiveFormats.Info(format).Extension;
            if (string.IsNullOrEmpty(extension)) extension = ".zip";

            var path = Path.GetFullPath(Path.Combine(directory, stem + extension));
            var pathDirectory = Path.GetDirectoryName(path) ?? directory;
            var fileName = Path.GetFileNameWithoutExtension(path);
            var pathExtension = Path.GetExtension(path);

            for (var index = 1; File.Exists(path) || Directory.Exists(path); index++)
            {
                if (index > 10_000) break;
                path = Path.Combine(pathDirectory, $"{fileName} ({index}){pathExtension}");
            }

            return path;
        }
        catch
        {
            return ArchiveFormats.EnsureExtension(Path.Combine(Environment.CurrentDirectory, "压缩包"), format);
        }
    }

    /// <summary>统计源（文件 / 文件夹递归）里的文件数与总字节数（拖入时显示「共 N 个文件 · X MB」）。</summary>
    public static (int FileCount, long TotalBytes) MeasureSources(IReadOnlyList<string> sourcePaths)
    {
        var fileCount = 0;
        long totalBytes = 0;
        if (sourcePaths is null) return (0, 0);

        foreach (var sourcePath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) continue;
            try
            {
                AccumulateSource(Path.GetFullPath(sourcePath.Trim()), ref fileCount, ref totalBytes, depth: 0);
            }
            catch
            {
                // 单个源读不了就跳过，不影响其它源。
            }
        }

        return (fileCount, totalBytes);
    }

    // =================================================================================
    // Probe 内部实现
    // =================================================================================

    private static ArchiveProbeResult ProbeCore(
        string archivePath,
        string? password,
        Encoding? legacyEncoding,
        int maxEntries,
        bool validatePassword,
        CancellationToken cancellationToken)
    {
        var format = ArchiveFormats.DetectBest(archivePath);
        if (format == ArchiveFormat.Unknown) return FailProbe("不是有效的压缩包");

        var options = CreateReaderOptions(password, legacyEncoding);

        // ReaderFactory 列目录时遇到加密条目会直接抛「需要密码」，所以 ZIP / RAR / TAR / GZ
        // 和 7Z 一样走 ArchiveFactory（只读目录结构，不需要密码）；7Z 更是只支持 ArchiveFactory。
        // 压缩版 TAR / ACE / ARC / ARJ 只有 ReaderFactory 认，必须走它。
        switch (format)
        {
            case ArchiveFormat.SevenZip:
            case ArchiveFormat.Zip:
            case ArchiveFormat.Rar:
            case ArchiveFormat.Tar:
            case ArchiveFormat.GZip:
                return ProbeWithArchive(archivePath, format, options, maxEntries, validatePassword, password, cancellationToken);

            case ArchiveFormat.TarGz:
            case ArchiveFormat.TarBz2:
            case ArchiveFormat.TarXz:
            case ArchiveFormat.TarZstd:
            case ArchiveFormat.TarLz:
            case ArchiveFormat.Ace:
            case ArchiveFormat.Arc:
            case ArchiveFormat.Arj:
                return ProbeWithReader(archivePath, format, options, maxEntries, validatePassword, password, cancellationToken);
        }

        try
        {
            return ProbeWithReader(archivePath, format, options, maxEntries, validatePassword, password, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsSingleStreamFormat(format) && IsReaderUnsupported(ex))
        {
            // 单独一个 .bz2 / .xz / .zst / .lzw：ReaderFactory 不认，按单文件条目处理。
            return ProbeSingleStream(archivePath, format);
        }
    }

    private static ArchiveProbeResult ProbeWithReader(
        string archivePath,
        ArchiveFormat format,
        ReaderOptions options,
        int maxEntries,
        bool validatePassword,
        string? password,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = ReaderFactory.OpenReader(stream, options);

        var entries = new List<ArchiveEntryInfo>();
        var entryCount = 0;
        var fileCount = 0;
        long totalSize = 0;
        var isEncrypted = false;
        var truncated = false;
        var passwordValidated = false;

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = reader.Entry;
            var isDirectory = entry.IsDirectory || ArchivePathSafety.IsDirectoryKey(entry.Key);

            entryCount++;
            if (entry.IsEncrypted) isEncrypted = true;
            if (!isDirectory)
            {
                fileCount++;
                if (entry.Size > 0) totalSize += entry.Size;
            }

            if (maxEntries <= 0 || entries.Count < maxEntries)
            {
                entries.Add(ToEntryInfo(entry, isDirectory, ResolveEntryKey(format, archivePath, entry.Key)));
            }
            else
            {
                truncated = true;
            }

            // 用户给了密码就顺手校验一次（读第一个加密条目到结尾，AES 的校验码在末尾）。
            if (validatePassword && !passwordValidated && !string.IsNullOrEmpty(password) && entry.IsEncrypted && !isDirectory)
            {
                using var entryStream = reader.OpenEntryStream();
                Drain(entryStream, cancellationToken);
                passwordValidated = true;
            }
        }

        return new ArchiveProbeResult
        {
            Success = true,
            Format = format,
            TotalSize = totalSize,
            EntryCount = entryCount,
            FileCount = fileCount,
            IsEncrypted = isEncrypted,
            Entries = entries,
            EntriesTruncated = truncated,
        };
    }

    private static ArchiveProbeResult ProbeWithArchive(
        string archivePath,
        ArchiveFormat format,
        ReaderOptions options,
        int maxEntries,
        bool validatePassword,
        string? password,
        CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, options);

        var entries = new List<ArchiveEntryInfo>();
        var entryCount = 0;
        var fileCount = 0;
        long totalSize = 0;
        var isEncrypted = archive.IsEncrypted;
        var truncated = false;
        var passwordValidated = false;

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = entry.IsDirectory || ArchivePathSafety.IsDirectoryKey(entry.Key);

            entryCount++;
            if (entry.IsEncrypted) isEncrypted = true;
            if (!isDirectory)
            {
                fileCount++;
                if (entry.Size > 0) totalSize += entry.Size;
            }

            if (maxEntries <= 0 || entries.Count < maxEntries)
            {
                entries.Add(ToEntryInfo(entry, isDirectory, ResolveEntryKey(format, archivePath, entry.Key)));
            }
            else
            {
                truncated = true;
            }

            if (validatePassword && !passwordValidated && !string.IsNullOrEmpty(password) && entry.IsEncrypted && !isDirectory)
            {
                using var entryStream = entry.OpenEntryStream();
                Drain(entryStream, cancellationToken);
                passwordValidated = true;
            }
        }

        return new ArchiveProbeResult
        {
            Success = true,
            Format = format,
            TotalSize = totalSize,
            EntryCount = entryCount,
            FileCount = fileCount,
            IsEncrypted = isEncrypted,
            Entries = entries,
            EntriesTruncated = truncated,
        };
    }

    private static ArchiveProbeResult ProbeSingleStream(string archivePath, ArchiveFormat format)
    {
        var baseName = ArchiveFormats.GetBaseName(archivePath);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = Path.GetFileName(archivePath);

        return new ArchiveProbeResult
        {
            Success = true,
            Format = format,
            TotalSize = 0,
            EntryCount = 1,
            FileCount = 1,
            IsEncrypted = false,
            Entries =
            [
                new ArchiveEntryInfo
                {
                    Key = baseName,
                    Size = -1,
                    CompressedSize = TryGetFileLength(archivePath),
                    IsDirectory = false,
                },
            ],
        };
    }

    // =================================================================================
    // Extract 内部实现
    // =================================================================================

    private static void ExtractSevenZip(
        string archivePath,
        string? password,
        Encoding? legacyEncoding,
        ExtractSession session,
        CancellationToken cancellationToken)
    {
        using var archive = ArchiveFactory.OpenArchive(archivePath, CreateReaderOptions(password, legacyEncoding));
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = entry;
            session.ProcessEntry(current, () => current.OpenEntryStream());
        }
    }

    private static void ExtractWithReader(
        string archivePath,
        ArchiveFormat format,
        string? password,
        Encoding? legacyEncoding,
        ExtractSession session,
        CancellationToken cancellationToken)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        IReader reader;
        try
        {
            reader = ReaderFactory.OpenReader(stream, CreateReaderOptions(password, legacyEncoding));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsSingleStreamFormat(format) && IsReaderUnsupported(ex))
        {
            // 单独一个 .bz2 / .xz / .zst / .lzw：ReaderFactory 不认，直接用压缩流解。
            session.ExtractSingleStream(archivePath, format);
            return;
        }

        using (reader)
        {
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                session.ProcessEntry(entry, () => reader.OpenEntryStream());
            }
        }
    }

    /// <summary>一次解压任务的共享状态（进度、计数、目录时间戳）。</summary>
    private sealed class ExtractSession
    {
        private readonly Dictionary<string, DateTime> _directoryTimes = new(StringComparer.OrdinalIgnoreCase);

        public ExtractSession(string archivePath, ArchiveFormat format, string destinationRoot, ArchiveExtractOptions options,
            IProgress<ArchiveProgress>? progress, CancellationToken cancellationToken, Stopwatch watch)
        {
            ArchivePath = archivePath;
            Format = format;
            DestinationRoot = destinationRoot;
            Options = options;
            Progress = progress;
            CancellationToken = cancellationToken;
            Watch = watch;
        }

        public string ArchivePath { get; }

        public ArchiveFormat Format { get; }

        public string DestinationRoot { get; }

        public ArchiveExtractOptions Options { get; }

        public IProgress<ArchiveProgress>? Progress { get; }

        public CancellationToken CancellationToken { get; }

        public Stopwatch Watch { get; }

        public int FilesDone { get; private set; }

        public int FilesTotal { get; set; }

        public long BytesDone { get; private set; }

        public long BytesTotal { get; set; }

        public int SkippedCount { get; private set; }

        public List<string> Warnings { get; } = new();

        public void ProcessEntry(IEntry entry, Func<Stream> openEntryStream)
        {
            CancellationToken.ThrowIfCancellationRequested();

            var key = entry.Key ?? "";
            if (!string.IsNullOrEmpty(entry.LinkTarget))
            {
                Skip($"跳过符号链接条目：{DisplayKey(key)}");
                return;
            }

            var isDirectory = entry.IsDirectory || ArchivePathSafety.IsDirectoryKey(key);
            if (!ArchivePathSafety.TryNormalizeEntryKey(key, out var normalizedKey))
            {
                // 单文件压缩（GZ 的头部经常没写文件名）：条目名不可用时按契约退回压缩包文件名。
                if (!isDirectory && IsSingleStreamFormat(Format) && TryGetSingleStreamFallbackKey(out var fallbackKey))
                {
                    key = fallbackKey;
                    normalizedKey = fallbackKey;
                }
                else
                {
                    Skip($"跳过不安全的路径：{DisplayKey(key)}");
                    return;
                }
            }

            if (!ArchivePathSafety.TryResolveWithin(DestinationRoot, normalizedKey, out var targetPath))
            {
                Skip($"跳过越界路径：{DisplayKey(key)}");
                return;
            }

            if (isDirectory)
            {
                ProcessDirectoryEntry(targetPath, entry.LastModifiedTime);
                return;
            }

            ProcessFileEntry(targetPath, key, entry.LastModifiedTime, openEntryStream);
        }

        /// <summary>单文件压缩在条目名不可用时，退回压缩包文件名（去掉 .gz / .bz2 等）。</summary>
        private bool TryGetSingleStreamFallbackKey(out string fallbackKey)
        {
            fallbackKey = "";
            var baseName = ArchiveFormats.GetBaseName(ArchivePath);
            if (!ArchivePathSafety.IsSafeSimpleFileName(baseName)) return false;
            return ArchivePathSafety.TryNormalizeEntryKey(baseName, out fallbackKey);
        }

        public void ExtractSingleStream(string archivePath, ArchiveFormat format)
        {
            CancellationToken.ThrowIfCancellationRequested();

            var baseName = ArchiveFormats.GetBaseName(archivePath);
            var fileName = ArchivePathSafety.IsSafeSimpleFileName(baseName) ? baseName : "解压文件";
            if (!ArchivePathSafety.TryResolveWithin(DestinationRoot, fileName, out var targetPath))
            {
                Skip($"跳过不安全的文件名：{DisplayKey(baseName)}");
                return;
            }

            if (Directory.Exists(targetPath))
            {
                Skip($"跳过已存在的同名文件夹：{targetPath}");
                return;
            }

            if (File.Exists(targetPath))
            {
                switch (Options.Overwrite)
                {
                    case OverwriteMode.Skip:
                        SkippedCount++;
                        return;
                    case OverwriteMode.Rename:
                        targetPath = ArchivePathSafety.MakeUniqueFilePath(targetPath);
                        break;
                }
            }

            FilesTotal = Math.Max(FilesTotal, 1);
            using (var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var decompressed = ArchiveSingleStream.Create(source, format))
            using (var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CopyStream(decompressed, destination);
            }

            if (Options.PreserveTimestamps) TrySetFileTime(targetPath, File.GetLastWriteTimeUtc(archivePath));

            FilesDone++;
            SafeReport(Progress, new ArchiveProgress
            {
                FilesDone = FilesDone,
                FilesTotal = FilesTotal,
                BytesDone = BytesDone,
                BytesTotal = BytesTotal,
                CurrentEntry = fileName,
            });
        }

        public void ApplyDirectoryTimestamps()
        {
            // 子目录在前、父目录在后，避免写文件时把父目录时间又改了。
            foreach (var pair in _directoryTimes.OrderByDescending(p => p.Key.Length))
            {
                TrySetDirectoryTime(pair.Key, pair.Value);
            }
        }

        public ArchiveOperationResult ToResult(bool success, bool canceled = false, string? error = null)
            => new()
            {
                Success = success,
                Canceled = canceled,
                Error = error,
                FileCount = FilesDone,
                TotalBytes = BytesDone,
                SkippedCount = SkippedCount,
                OutputPath = DestinationRoot,
                Warnings = Warnings.ToArray(),
                Elapsed = Watch.Elapsed,
            };

        public ArchiveOperationResult ToCanceled() => ToResult(success: false, canceled: true, error: "已取消");

        private void ProcessDirectoryEntry(string targetPath, DateTime? lastModifiedTime)
        {
            try
            {
                if (File.Exists(targetPath))
                {
                    Skip($"跳过目录条目（同名文件已存在）：{targetPath}");
                    return;
                }

                Directory.CreateDirectory(targetPath);
                if (Options.PreserveTimestamps && lastModifiedTime.HasValue)
                {
                    _directoryTimes[targetPath] = lastModifiedTime.Value;
                }
            }
            catch (Exception ex) when (IsRecoverableIo(ex))
            {
                Skip($"无法创建文件夹：{targetPath}（{ex.Message}）");
            }
        }

        private void ProcessFileEntry(string targetPath, string key, DateTime? lastModifiedTime, Func<Stream> openEntryStream)
        {
            try
            {
                if (Directory.Exists(targetPath))
                {
                    Skip($"跳过文件条目（同名文件夹已存在）：{DisplayKey(key)}");
                    return;
                }

                if (File.Exists(targetPath))
                {
                    switch (Options.Overwrite)
                    {
                        case OverwriteMode.Skip:
                            SkippedCount++;
                            return;
                        case OverwriteMode.Rename:
                            targetPath = ArchivePathSafety.MakeUniqueFilePath(targetPath);
                            break;
                    }
                }

                var parent = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                using (var source = openEntryStream())
                using (var destination = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    CopyStream(source, destination);
                }

                if (Options.PreserveTimestamps && lastModifiedTime.HasValue)
                {
                    TrySetFileTime(targetPath, lastModifiedTime.Value);
                }

                FilesDone++;
                SafeReport(Progress, new ArchiveProgress
                {
                    FilesDone = FilesDone,
                    FilesTotal = FilesTotal,
                    BytesDone = BytesDone,
                    BytesTotal = BytesTotal,
                    CurrentEntry = key,
                });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsRecoverableIo(ex))
            {
                Skip($"无法写入文件：{targetPath}（{ex.Message}）");
            }
        }

        private void CopyStream(Stream source, Stream destination)
        {
            var buffer = new byte[CopyBufferSize];
            while (true)
            {
                CancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                destination.Write(buffer, 0, read);
                BytesDone += read;
            }
        }

        private void Skip(string warning)
        {
            SkippedCount++;
            Warnings.Add(warning);
        }
    }

    // =================================================================================
    // Create 内部实现
    // =================================================================================

    private sealed record PlannedFile(string Key, string FullPath, long Size, DateTime LastWriteUtc);

    private sealed record PlannedDirectory(string Key, DateTime LastWriteUtc);

    private sealed class CreatePlan
    {
        public List<PlannedFile> Files { get; } = new();

        public List<PlannedDirectory> Directories { get; } = new();
    }

    private static CreatePlan BuildCreatePlan(
        IReadOnlyList<string> sourcePaths,
        string outputFullPath,
        bool includeEmptyDirectories,
        List<string> warnings,
        ref int skipped,
        CancellationToken cancellationToken)
    {
        var plan = new CreatePlan();
        var usedTopNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawPath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                warnings.Add("跳过空路径");
                skipped++;
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath.Trim());
            }
            catch (Exception ex)
            {
                warnings.Add($"跳过无效路径：{rawPath}（{ex.Message}）");
                skipped++;
                continue;
            }

            try
            {
                if (File.Exists(fullPath))
                {
                    if (IsReparsePoint(fullPath))
                    {
                        warnings.Add($"跳过符号链接 / 快捷方式：{fullPath}");
                        skipped++;
                        continue;
                    }

                    if (SamePath(fullPath, outputFullPath)) continue;

                    var key = UniqueTopName(Path.GetFileName(fullPath), usedTopNames);
                    plan.Files.Add(new PlannedFile(key, fullPath, TryGetFileLength(fullPath), TryGetLastWriteTimeUtc(fullPath)));
                }
                else if (Directory.Exists(fullPath))
                {
                    if (IsReparsePoint(fullPath))
                    {
                        warnings.Add($"跳过符号链接 / 快捷方式：{fullPath}");
                        skipped++;
                        continue;
                    }

                    var topName = UniqueTopName(GetDirectoryTopName(fullPath), usedTopNames);
                    WalkDirectory(fullPath, topName, includeEmptyDirectories, plan, warnings, ref skipped,
                        outputFullPath, cancellationToken);
                }
                else
                {
                    warnings.Add($"路径不存在：{fullPath}");
                    skipped++;
                }
            }
            catch (Exception ex) when (IsRecoverableIo(ex))
            {
                warnings.Add($"跳过 {fullPath}：{ex.Message}");
                skipped++;
            }
        }

        return plan;
    }

    private static void WalkDirectory(
        string directoryFullPath,
        string keyPrefix,
        bool includeEmptyDirectories,
        CreatePlan plan,
        List<string> warnings,
        ref int skipped,
        string outputFullPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (includeEmptyDirectories)
        {
            plan.Directories.Add(new PlannedDirectory(keyPrefix + "/", TryGetLastWriteTimeUtc(directoryFullPath)));
        }

        FileSystemInfo[] children;
        try
        {
            children = new DirectoryInfo(directoryFullPath).GetFileSystemInfos();
        }
        catch (Exception ex) when (IsRecoverableIo(ex))
        {
            warnings.Add($"跳过无权限目录：{directoryFullPath}（{ex.Message}）");
            skipped++;
            return;
        }

        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add($"跳过符号链接：{child.FullName}");
                    skipped++;
                    continue;
                }

                if (child is DirectoryInfo directory)
                {
                    WalkDirectory(directory.FullName, keyPrefix + "/" + directory.Name, includeEmptyDirectories,
                        plan, warnings, ref skipped, outputFullPath, cancellationToken);
                }
                else if (child is FileInfo file)
                {
                    if (SamePath(file.FullName, outputFullPath)) continue;
                    plan.Files.Add(new PlannedFile(
                        keyPrefix + "/" + file.Name,
                        file.FullName,
                        TryGetFileLength(file.FullName),
                        TryGetLastWriteTimeUtc(file.FullName)));
                }
            }
            catch (Exception ex) when (IsRecoverableIo(ex))
            {
                warnings.Add($"跳过 {child.FullName}：{ex.Message}");
                skipped++;
            }
        }
    }

    private static IWriter CreateWriter(Stream destination, ArchiveFormat format, CompressionPreset level, List<string> warnings)
    {
        if (format == ArchiveFormat.Zip)
        {
            var isStore = level == CompressionPreset.Store;
            var compressionType = isStore ? CompressionType.None : CompressionType.Deflate;
            // CompressionType.None 不接受可配置级别，必须给 0。
            var compressionLevel = isStore
                ? 0
                : level switch
                {
                    CompressionPreset.Fastest => 1,
                    CompressionPreset.Maximum => 9,
                    _ => 6,
                };

            return new ZipWriter(destination, new ZipWriterOptions(compressionType)
            {
                UseZip64 = true,
                CompressionLevel = compressionLevel,
                ArchiveEncoding = new ArchiveEncoding { Default = Encoding.UTF8 },
            });
        }

        // SharpCompress 的 7Z 写入器只接受 LZMA / LZMA2；「仅存储」没有对应实现，
        // 只能降级成 LZMA2 最低级别，并在结果里给一条提示。
        if (level == CompressionPreset.Store)
        {
            warnings.Add("7Z 格式不支持「仅存储」，已改用 LZMA2 最低压缩级别");
        }

        var sevenZipLevel = level switch
        {
            CompressionPreset.Store => 1,
            CompressionPreset.Fastest => 1,
            CompressionPreset.Maximum => 9,
            _ => 5,
        };

        return new SevenZipWriter(destination, new SevenZipWriterOptions(CompressionType.LZMA2)
        {
            CompressionLevel = sevenZipLevel,
            LzmaProperties = new LzmaEncoderProperties(),
        });
    }

    /// <summary>包一层「读的时候检查取消 / 统计读入字节」的流，写入器照常读写。</summary>
    private sealed class CancelCheckingStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationToken _cancellationToken;

        public CancelCheckingStream(Stream inner, CancellationToken cancellationToken)
        {
            _inner = inner;
            _cancellationToken = cancellationToken;
        }

        public long BytesRead { get; private set; }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var read = _inner.Read(buffer);
            BytesRead += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void Flush() => _inner.Flush();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // 不关闭底层流：由外面的 using 负责。
        protected override void Dispose(bool disposing)
        {
        }
    }

    // =================================================================================
    // 通用小工具
    // =================================================================================

    private static ReaderOptions CreateReaderOptions(string? password, Encoding? legacyEncoding)
    {
        var options = new ReaderOptions();
        if (!string.IsNullOrEmpty(password)) options.Password = password;
        if (legacyEncoding is not null)
        {
            options.ArchiveEncoding = new ArchiveEncoding { Default = legacyEncoding };
        }

        return options;
    }

    private static ArchiveEntryInfo ToEntryInfo(IEntry entry, bool isDirectory, string? keyOverride = null)
        => new()
        {
            Key = keyOverride ?? (string.IsNullOrEmpty(entry.Key) ? "" : entry.Key.Replace('\\', '/')),
            Size = entry.Size,
            CompressedSize = entry.CompressedSize,
            IsDirectory = isDirectory,
            IsEncrypted = entry.IsEncrypted,
            LastModifiedTime = entry.LastModifiedTime,
        };

    /// <summary>单文件压缩（GZ）的条目名经常为空：优先条目名，不可用时用压缩包文件名。</summary>
    private static string ResolveEntryKey(ArchiveFormat format, string archivePath, string? key)
    {
        if (IsSingleStreamFormat(format) && !ArchivePathSafety.IsSafeSimpleFileName(key))
        {
            var fallback = ArchiveFormats.GetBaseName(archivePath);
            if (ArchivePathSafety.IsSafeSimpleFileName(fallback)) return fallback;
        }

        return key ?? "";
    }

    private static bool IsCompressedTar(ArchiveFormat format)
        => format is ArchiveFormat.TarGz or ArchiveFormat.TarBz2 or ArchiveFormat.TarXz or ArchiveFormat.TarZstd or ArchiveFormat.TarLz;

    private static bool IsSingleStreamFormat(ArchiveFormat format)
        => format is ArchiveFormat.GZip or ArchiveFormat.BZip2 or ArchiveFormat.Xz or ArchiveFormat.Zstd or ArchiveFormat.Lzw;

    private static bool IsReaderUnsupported(Exception exception)
        => exception is InvalidFormatException or ArchiveOperationException;

    private static bool IsRecoverableIo(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static string DisplayKey(string? key) => string.IsNullOrEmpty(key) ? "(空)" : key;

    private static ArchiveProbeResult FailProbe(string error)
        => new() { Success = false, Error = error };

    private static ArchiveOperationResult FailOperation(
        string error,
        string? outputPath,
        Stopwatch watch,
        bool canceled = false,
        IReadOnlyList<string>? warnings = null,
        int skipped = 0)
        => new()
        {
            Success = false,
            Canceled = canceled,
            Error = error,
            OutputPath = outputPath,
            Warnings = warnings ?? Array.Empty<string>(),
            SkippedCount = skipped,
            Elapsed = watch.Elapsed,
        };

    private static string TranslateError(Exception exception, string? password)
    {
        try
        {
            switch (exception)
            {
                case OperationCanceledException:
                    return "已取消";
                case SharpCompressCryptographicException:
                    return string.IsNullOrEmpty(password) ? "压缩包已加密，需要密码" : "密码错误或压缩包损坏";
                case IncompleteArchiveException:
                case ArchiveOperationException:
                case InvalidFormatException:
                    return "不是有效的压缩包（文件损坏或格式不受支持）";
                case UnauthorizedAccessException:
                    return "没有权限访问文件或目录";
                case IOException:
                    return "读写文件失败：" + exception.Message;
                case SharpCompressException sp when sp.GetType().Name == "DataErrorException":
                    return "密码错误或压缩包损坏";
                case SharpCompressException:
                    return "压缩包损坏或格式不受支持：" + exception.Message;
                default:
                    return "操作失败：" + exception.Message;
            }
        }
        catch
        {
            return "操作失败";
        }
    }

    private static void Drain(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[CopyBufferSize];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stream.Read(buffer, 0, buffer.Length) <= 0) break;
        }
    }

    private static void SafeReport(IProgress<ArchiveProgress>? progress, ArchiveProgress value)
    {
        try
        {
            progress?.Report(value);
        }
        catch
        {
            // 进度回调自己的异常不能影响压缩 / 解压。
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删不掉就留着，交给用户处理。
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool SamePath(string first, string second)
    {
        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static void TrySetFileTime(string path, DateTime lastModifiedTime)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, ToUtc(lastModifiedTime));
        }
        catch
        {
            // 时间戳只是锦上添花，设不上就算了。
        }
    }

    private static void TrySetDirectoryTime(string path, DateTime lastModifiedTime)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(path, ToUtc(lastModifiedTime));
        }
        catch
        {
            // 同上。
        }
    }

    private static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
        };

    private static string UniqueTopName(string name, HashSet<string> usedNames)
    {
        if (string.IsNullOrWhiteSpace(name)) name = "文件";
        if (usedNames.Add(name)) return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (string.IsNullOrEmpty(stem)) stem = name;
        var extension = Path.GetExtension(name);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = $"{stem} ({index}){extension}";
            if (usedNames.Add(candidate)) return candidate;
        }

        var fallback = name + " " + Guid.NewGuid().ToString("N")[..6];
        usedNames.Add(fallback);
        return fallback;
    }

    private static string GetDirectoryTopName(string fullPath)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
            var name = Path.GetFileName(trimmed);
            if (string.IsNullOrWhiteSpace(name)) name = new DirectoryInfo(fullPath).Name;
            if (string.IsNullOrWhiteSpace(name)) name = "压缩包";

            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '_');
            }

            name = name.Trim();
            if (name.Length == 0 || name is "." or "..") name = "压缩包";
            return name;
        }
        catch
        {
            return "压缩包";
        }
    }

    private static (string Stem, string Directory) GetSuggestedStemAndDirectory(IReadOnlyList<string>? sourcePaths)
    {
        var timestamp = $"压缩包-{DateTime.Now:yyyyMMdd-HHmmss}";
        if (sourcePaths is null || sourcePaths.Count == 0)
        {
            return (timestamp, Environment.CurrentDirectory);
        }

        if (sourcePaths.Count > 1)
        {
            // 多个源：用第一个源的旁边 + 时间戳命名。
            var firstDirectory = Environment.CurrentDirectory;
            try
            {
                var first = sourcePaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                if (!string.IsNullOrWhiteSpace(first))
                {
                    var full = Path.GetFullPath(first.Trim());
                    firstDirectory = Directory.Exists(full)
                        ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full)) ?? firstDirectory
                        : Path.GetDirectoryName(full) ?? firstDirectory;
                }
            }
            catch
            {
                // 第一个源算不出目录就用当前目录。
            }

            return (timestamp, firstDirectory);
        }

        var sourcePath = sourcePaths[0];
        if (string.IsNullOrWhiteSpace(sourcePath)) return (timestamp, Environment.CurrentDirectory);

        try
        {
            var full = Path.GetFullPath(sourcePath.Trim());
            if (Directory.Exists(full))
            {
                var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
                return (GetDirectoryTopName(full), parent ?? Environment.CurrentDirectory);
            }

            var stem = ArchiveFormats.GetBaseName(full);
            if (string.IsNullOrWhiteSpace(stem)) stem = timestamp;
            return (stem, Path.GetDirectoryName(full) ?? Environment.CurrentDirectory);
        }
        catch
        {
            return (timestamp, Environment.CurrentDirectory);
        }
    }

    private static void AccumulateSource(string fullPath, ref int fileCount, ref long totalBytes, int depth)
    {
        if (depth > 128) return;
        try
        {
            if (File.Exists(fullPath))
            {
                if (IsReparsePoint(fullPath)) return;
                fileCount++;
                try
                {
                    totalBytes += new FileInfo(fullPath).Length;
                }
                catch
                {
                    // 大小读不到就当 0。
                }

                return;
            }

            if (Directory.Exists(fullPath))
            {
                if (IsReparsePoint(fullPath)) return;
                foreach (var child in Directory.EnumerateFileSystemEntries(fullPath))
                {
                    AccumulateSource(child, ref fileCount, ref totalBytes, depth + 1);
                }
            }
        }
        catch
        {
            // 不存在的 / 没权限的路径直接跳过。
        }
    }
}
