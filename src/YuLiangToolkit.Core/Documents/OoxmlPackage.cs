using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace YuLiangToolkit.Core.Documents;

/// <summary>
/// 打开 OOXML 文档时遇到的「不是有效文档 / 缺部件 / 解压体积过大」等问题。
/// <para>消息是给用户看的中文，界面可以直接显示（不要只写英文异常信息）。</para>
/// </summary>
public sealed class OoxmlException : Exception
{
    public OoxmlException(string message) : base(message)
    {
    }

    public OoxmlException(string message, Exception? inner) : base(message, inner)
    {
    }
}

/// <summary>OPC 关系文件（<c>_rels/*.rels</c>）里的一条关系。</summary>
/// <param name="Id">关系 Id：XML 里 <c>r:id="..."</c> 引用的就是它。</param>
/// <param name="Type">关系类型 URI（例如 officeDocument / image / hyperlink）。</param>
/// <param name="Target">目标：包内部件名（已按 OPC 规则解析成绝对部件名）；外部链接时是原 URL。</param>
/// <param name="IsExternal">是不是外部链接（<c>TargetMode="External"</c>）。</param>
public sealed record OoxmlRelationship(string Id, string Type, string Target, bool IsExternal);

/// <summary>
/// OOXML 压缩包的只读访问轮子 —— docx / xlsx / pptx 都是「ZIP + 一堆 XML 部件 + 关系文件」，
/// 这里把最容易写错的那层统一做好，两个文档阅读器（Word / Excel）共用：
/// <list type="bullet">
///   <item><b>部件名规范化</b>：统一 <c>/</c>、去掉开头斜杠、比较忽略大小写（ZIP 本身区分大小写，
///         但 Office 产出的部件名大小写不稳定，宽容一点更稳）；</item>
///   <item><b>关系解析</b>：<c>_rels/*.rels</c> + <c>../</c> 相对路径 + <c>TargetMode="External"</c>，
///         拿到图片 / 超链接的真实位置；</item>
///   <item><b>安全读 XML</b>：禁 DTD、禁外部解析（防 XXE），坏文件抛 <see cref="OoxmlException"/>；</item>
///   <item><b>体积上限</b>：单部件 128 MB / 整包 512 MB，防 zip 炸弹把内存吃满；</item>
///   <item><b>单位换算</b>：EMU / twip / 磅 → WPF 的 DIP（document 里全是这些单位，换算写错会「图变巨大」）。</item>
/// </list>
///
/// <para><b>用法</b>（见 Word / Excel 阅读器的解析器）：</para>
/// <code>
/// using var package = OoxmlPackage.Open(path);
/// var document = package.ReadXml("word/document.xml");            // 主文档
/// var rels = package.ReadRelationships("word/document.xml");      // 关系
/// var image = package.ReadBytes(rels.First(r =&gt; r.Id == "rId5").Target); // 图片字节
/// var widthDip = OoxmlPackage.EmuToDip(914400);                   // 1 英寸 = 96 DIP
/// </code>
///
/// <para><b>生命周期</b>：构造后持有底层流，<see cref="Dispose"/> 时一起释放；
/// 解析器里用 <c>using</c> 包住它，把需要的数据（文本 / 图片字节）拷出来后再释放，
/// 不要把 <see cref="XDocument"/> 或 ZIP 里的流长期留在内存里（工具页缓存 12 个视图，容易积少成多）。</para>
/// </summary>
public sealed class OoxmlPackage : IDisposable
{
    /// <summary>单个部件解压后允许的最大字节数（128 MB）。</summary>
    public const long MaxEntryBytes = 128L * 1024 * 1024;

    /// <summary>整个包解压后允许的最大字节数（512 MB）。</summary>
    public const long MaxTotalBytes = 512L * 1024 * 1024;

    /// <summary>1 个 WPF DIP = 1/96 英寸；1 英寸 = 914400 EMU，所以 1 DIP = 9525 EMU。</summary>
    public const double EmusPerDip = 9525.0;

    private readonly Stream _source;
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    private bool _disposed;

    private OoxmlPackage(Stream source)
    {
        _source = source;

        try
        {
            _archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            source.Dispose();
            throw new OoxmlException("文件不是有效的 Office 文档（ZIP 结构损坏，或后缀与实际格式不符）", ex);
        }

        _entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        try
        {
            foreach (var entry in _archive.Entries)
            {
                // 目录项（以 / 结尾）没有内容，跳过
                if (string.IsNullOrEmpty(entry.Name)) continue;

                var name = NormalizePartName(entry.FullName);
                if (name.Length == 0) continue;

                _entries[name] = entry;

                if (entry.Length > MaxEntryBytes)
                {
                    throw new OoxmlException(
                        $"文档部件「{name}」解压后超过 {MaxEntryBytes / (1024 * 1024)} MB，已拒绝打开");
                }

                totalBytes += entry.Length;
                if (totalBytes > MaxTotalBytes)
                {
                    throw new OoxmlException(
                        $"文档解压后超过 {MaxTotalBytes / (1024 * 1024)} MB，已拒绝打开（可能是损坏文件或压缩炸弹）");
                }
            }
        }
        catch
        {
            _archive.Dispose();
            source.Dispose();
            throw;
        }
    }

    /// <summary>打开磁盘上的 OOXML 文件（允许别的程序同时读这个文件，比如 Word 正开着它）。</summary>
    public static OoxmlPackage Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new OoxmlException("没有选择文件");
        if (!File.Exists(path)) throw new OoxmlException($"文件不存在：{path}");

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
        }
        catch (Exception ex)
        {
            throw new OoxmlException($"打不开文件（可能被占用或没有读取权限）：{ex.Message}", ex);
        }

        return new OoxmlPackage(stream);
    }

    /// <summary>
    /// 打开任意流。<b>包的 Dispose 会一并释放这个流</b>（和 <see cref="ZipArchive"/> 的默认行为一致）。
    /// 流不可定位时会先整段拷进内存。
    /// </summary>
    public static OoxmlPackage Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanSeek)
        {
            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            stream = copy;
        }

        return new OoxmlPackage(stream);
    }

    /// <summary>包里所有部件名（不含目录项，已规范化）。</summary>
    public IReadOnlyCollection<string> PartNames
    {
        get
        {
            ThrowIfDisposed();
            return _entries.Keys;
        }
    }

    /// <summary>包里有没有这个部件（部件名忽略大小写）。</summary>
    public bool Contains(string partName)
    {
        ThrowIfDisposed();
        var name = NormalizePartName(partName);
        return name.Length > 0 && _entries.ContainsKey(name);
    }

    /// <summary>读一个部件的原始字节（超过 <see cref="MaxEntryBytes"/> 会被拒绝）。</summary>
    public byte[] ReadBytes(string partName)
    {
        ThrowIfDisposed();
        var entry = GetEntry(partName, out var name);

        if (entry.Length > MaxEntryBytes)
        {
            throw new OoxmlException(
                $"文档部件「{name}」解压后超过 {MaxEntryBytes / (1024 * 1024)} MB，已拒绝读取");
        }

        try
        {
            using var input = entry.Open();
            using var output = new MemoryStream((int)entry.Length);
            input.CopyTo(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is not OoxmlException)
        {
            throw new OoxmlException($"读取文档部件「{name}」失败：{ex.Message}", ex);
        }
    }

    /// <summary>读一个文本部件的字符串（默认 UTF-8，认 BOM；XML 部件请直接用 <see cref="ReadXml"/>）。</summary>
    public string ReadText(string partName) => DecodeText(ReadBytes(partName));

    /// <summary>读一个 XML 部件（禁 DTD / 外部实体；部件缺失或 XML 损坏时抛 <see cref="OoxmlException"/>）。</summary>
    public XDocument ReadXml(string partName)
    {
        GetEntry(partName, out var name);
        return ParseXml(ReadBytes(name), name);
    }

    /// <summary>部件存在且能正常解析时返回 XML，否则返回 null（用来读可选部件，例如样式 / 共享字符串）。</summary>
    public XDocument? TryReadXml(string partName)
    {
        ThrowIfDisposed();
        var name = NormalizePartName(partName);
        if (name.Length == 0 || !_entries.ContainsKey(name)) return null;

        try
        {
            return ReadXml(name);
        }
        catch (OoxmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// 读某个部件的 <c>_rels/*.rels</c> 关系（例如 <c>word/document.xml</c> 的图片 / 超链接关系）。
    /// 没有关系文件时返回空列表，不抛异常。
    /// </summary>
    public IReadOnlyList<OoxmlRelationship> ReadRelationships(string sourcePartName)
    {
        ThrowIfDisposed();

        var source = NormalizePartName(sourcePartName);
        var relsPart = GetRelationshipPartName(source);
        if (!_entries.ContainsKey(relsPart)) return Array.Empty<OoxmlRelationship>();

        var document = ReadXml(relsPart);
        var relationships = new List<OoxmlRelationship>();

        foreach (var element in document.Descendants())
        {
            if (element.Name.LocalName != "Relationship") continue;

            var id = (string?)element.Attribute("Id") ?? "";
            if (id.Length == 0) continue;

            var type = (string?)element.Attribute("Type") ?? "";
            var target = (string?)element.Attribute("Target") ?? "";
            var isExternal = string.Equals((string?)element.Attribute("TargetMode"), "External",
                StringComparison.OrdinalIgnoreCase);
            var resolved = isExternal ? target : ResolvePartPath(source, target);

            relationships.Add(new OoxmlRelationship(id, type, resolved, isExternal));
        }

        return relationships;
    }

    /// <summary>
    /// 部件名规范化：反斜杠换成 <c>/</c>、去掉开头斜杠、去首尾空白。
    /// 空 / 全空白返回空串。
    /// </summary>
    public static string NormalizePartName(string? partName)
    {
        if (string.IsNullOrWhiteSpace(partName)) return "";
        var name = partName.Trim().Replace('\\', '/');
        return name.TrimStart('/');
    }

    /// <summary>
    /// 某个部件对应的关系文件部件名：
    /// <c>word/document.xml</c> → <c>word/_rels/document.xml.rels</c>；
    /// 根部件（没有目录）→ <c>_rels/文件名.rels</c>。
    /// </summary>
    public static string GetRelationshipPartName(string sourcePartName)
    {
        var source = NormalizePartName(sourcePartName);
        if (source.Length == 0) return "_rels/.rels";

        var slash = source.LastIndexOf('/');
        var directory = slash < 0 ? "" : source[..(slash + 1)];
        var fileName = slash < 0 ? source : source[(slash + 1)..];
        return $"{directory}_rels/{fileName}.rels";
    }

    /// <summary>
    /// 按 OPC 规则把关系里的相对 Target 解析成包内绝对部件名：
    /// <c>source=word/document.xml, target=media/image1.png</c> → <c>word/media/image1.png</c>；
    /// 处理 <c>../</c>、<c>./</c>、开头 <c>/</c>；外部 URL 原样返回。
    /// </summary>
    public static string ResolvePartPath(string sourcePartName, string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "";

        var raw = target.Trim().Replace('\\', '/');
        if (raw.StartsWith('/')) return CollapseDotSegments(raw.TrimStart('/'));
        if (raw.Contains("://", StringComparison.Ordinal)) return raw;

        var source = NormalizePartName(sourcePartName);
        var slash = source.LastIndexOf('/');
        var directory = slash < 0 ? "" : source[..(slash + 1)];
        return CollapseDotSegments(directory + raw);
    }

    /// <summary>EMU → WPF DIP（<c>914400 EMU = 1 英寸 = 96 DIP</c>）。</summary>
    public static double EmuToDip(long emu) => emu / EmusPerDip;

    /// <summary>DIP → EMU（写入 / 反算时用）。</summary>
    public static long DipToEmu(double dip) => (long)Math.Round(dip * EmusPerDip);

    /// <summary>twip（1/20 磅）→ WPF DIP（<c>1 twip = 1/15 DIP</c>）。</summary>
    public static double TwipToDip(double twips) => twips / 15.0;

    /// <summary>磅（pt）→ WPF DIP（<c>1 pt = 96/72 DIP</c>）。</summary>
    public static double PointToDip(double points) => points * 96.0 / 72.0;

    /// <summary>WPF DIP → 磅（字号等需要按磅回报时用）。</summary>
    public static double DipToPoint(double dip) => dip * 72.0 / 96.0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _archive.Dispose(); } catch { /* 释放失败不影响使用 */ }
        try { _source.Dispose(); } catch { /* 同上 */ }
    }

    // ---------------------------------------------------------------- 内部实现

    private ZipArchiveEntry GetEntry(string partName, out string normalizedName)
    {
        normalizedName = NormalizePartName(partName);
        if (normalizedName.Length == 0)
            throw new OoxmlException("文档部件名为空");

        if (!_entries.TryGetValue(normalizedName, out var entry))
            throw new OoxmlException($"文档缺少部件「{normalizedName}」（可能不是这个格式，或文件已损坏）");

        return entry;
    }

    private static XDocument ParseXml(byte[] bytes, string partName)
    {
        if (bytes.Length == 0)
            throw new OoxmlException($"文档部件「{partName}」是空的");

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                CloseInput = false,
            };

            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new OoxmlException(
                $"文档部件「{partName}」不是合法 XML（文件可能损坏）：{ex.Message}", ex);
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string CollapseDotSegments(string path)
    {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
