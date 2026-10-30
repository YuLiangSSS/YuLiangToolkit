namespace YuLiangToolkit.Tools.PictureViewer;

/// <summary>
/// 「最近浏览」里的一条：图片连续阅读器是**按文件夹**打开的，所以记的是打开过的文件夹。
/// 存在 <c>Config\picture-viewer\config.json</c> 的 <c>recentEntries</c> 里。
/// </summary>
public sealed class PictureRecentEntry
{
    /// <summary>文件夹完整路径。</summary>
    public string Path { get; set; } = "";

    /// <summary>最后一次打开的时间（UTC），列表按它从新到旧排序。</summary>
    public DateTime LastOpenedUtc { get; set; } = DateTime.UtcNow;
}
