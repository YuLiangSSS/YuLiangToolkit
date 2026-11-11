namespace YuLiangToolkit.Tools.MailSender;

/// <summary>
/// 附件清单里的一项（界面上的「📎 文件名 12.3 KB」）。
///
/// <para>附件内容**不在界面层读**：这里只记路径 + 元数据，真正发信时才把文件读成字节并转 base64
/// （见 <see cref="ResendClient"/>），这样界面上挂着几十个附件也不会占内存。</para>
/// </summary>
public sealed class MailAttachmentItem
{
    public MailAttachmentItem(string filePath)
    {
        FilePath = filePath ?? "";
        FileName = Path.GetFileName(FilePath);
        try
        {
            var info = new FileInfo(FilePath);
            Exists = info.Exists;
            SizeBytes = info.Exists ? info.Length : -1;
        }
        catch
        {
            Exists = false;
            SizeBytes = -1;
        }
    }

    /// <summary>本机完整路径。</summary>
    public string FilePath { get; }

    /// <summary>发出去时用的文件名（Resend 的 <c>filename</c>）。</summary>
    public string FileName { get; }

    /// <summary>文件现在还在不在（发信前会再查一次）。</summary>
    public bool Exists { get; }

    /// <summary>原始字节数；读不到时是 -1。</summary>
    public long SizeBytes { get; }

    /// <summary>base64 编码后的估算字节数（体积校验按这个算，见 <see cref="MailSenderDefaults.MaxEmailBytesAfterBase64"/>）。</summary>
    public long Base64Bytes => SizeBytes < 0 ? 0 : Base64SizeOf(SizeBytes);

    /// <summary>人类可读大小。</summary>
    public string SizeText => SizeBytes < 0 ? "（读不到）" : Core.Storage.FileSize.Format(SizeBytes);

    /// <summary>界面上的副标题：<c>12.3 KB · base64 后 16.4 KB</c>。</summary>
    public string DetailText => SizeBytes < 0
        ? "文件不存在或读不到"
        : $"{SizeText} · base64 后 {Core.Storage.FileSize.Format(Base64Bytes)}";

    /// <summary>媒体类型（按扩展名猜；认不出的给 <c>application/octet-stream</c>）。</summary>
    public string ContentType => ContentTypeOf(FileName);

    /// <summary>resend 明确拒收的危险扩展名（可执行文件 / 脚本 / 快捷方式），发之前先提醒用户。</summary>
    public bool IsBlockedType => IsBlockedExtension(FileName);

    /// <summary>base64 编码后的长度（4 字节一组，向上取整再加换行余量）。</summary>
    public static long Base64SizeOf(long rawBytes)
        => rawBytes <= 0 ? 0 : (rawBytes + 2) / 3 * 4;

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".csv"] = "text/csv",
        [".md"] = "text/markdown",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".xml"] = "application/xml",
        [".json"] = "application/json",
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".gz"] = "application/gzip",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".ogg"] = "audio/ogg",
        [".flac"] = "audio/flac",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mkv"] = "video/x-matroska",
        [".ico2"] = "image/x-icon",
    };

    /// <summary>危险扩展名（resend 会直接拒收这些类型，见官方「不支持的附件类型」清单）。</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".js", ".jse", ".vbs", ".vbe",
        ".ps1", ".psm1", ".msi", ".msp", ".reg", ".lnk", ".hta", ".jar", ".pif", ".cpl", ".wsf", ".wsh",
    };

    /// <summary>按扩展名猜媒体类型。</summary>
    public static string ContentTypeOf(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return ContentTypes.TryGetValue(ext, out var type) ? type : "application/octet-stream";
    }

    /// <summary>是不是 resend 会拒收的附件类型。</summary>
    public static bool IsBlockedExtension(string? fileName)
        => BlockedExtensions.Contains(Path.GetExtension(fileName ?? ""));

    /// <summary>去掉重复的附件路径（忽略大小写），保留填写顺序。</summary>
    public static List<string> Distinct(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var path in paths)
        {
            var trimmed = (path ?? "").Trim();
            if (trimmed.Length == 0) continue;
            if (seen.Add(trimmed)) list.Add(trimmed);
        }
        return list;
    }
}
