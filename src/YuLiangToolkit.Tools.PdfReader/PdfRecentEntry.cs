namespace YuLiangToolkit.Tools.PdfReader;

/// <summary>
/// 「最近浏览」里的一条：PDF 阅读器记的是打开过的 PDF 文件。
/// 存在 <c>Config\pdf-reader\config.json</c> 的 <c>recentEntries</c> 里。
/// </summary>
public sealed class PdfRecentEntry
{
    /// <summary>PDF 文件完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>最后一次打开的时间（UTC），列表按它从新到旧排序。</summary>
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}
