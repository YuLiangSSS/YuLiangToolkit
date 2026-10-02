namespace YuLiangToolkit.Tools.MapDownloader.Core;

/// <summary>
/// <c>.osz</c> 的下载地址与文件名规则（纯函数）。原版的规则是：
/// <list type="bullet">
///   <item>地址：<c>https://dl.sayobot.cn/beatmaps/download/{full|novideo}/{谱面集 Id}</c>（第三方镜像，不是官方站）；</item>
///   <item>文件名：<c>{Id} {清洗过的标题}.osz</c>。</item>
/// </list>
/// </summary>
public static class OszNaming
{
    /// <summary>默认镜像根地址（自检会把它换成本地假镜像）。</summary>
    public const string DefaultMirrorBase = "https://dl.sayobot.cn/beatmaps/download";

    /// <summary>标题最长留多少个字符（原版 80）。</summary>
    public const int MaxTitleLength = 80;

    /// <summary>
    /// 把标题清洗成能当文件名的样子：非法字符换成 <c>_</c> → 截断到
    /// <see cref="MaxTitleLength"/> → 去掉首尾空白。空标题给 <c>_</c>（原版行为）。
    /// </summary>
    public static string Sanitize(string? name, int maxLength = MaxTitleLength)
    {
        if (string.IsNullOrEmpty(name)) return "_";

        var cleaned = name;
        foreach (var bad in Path.GetInvalidFileNameChars()) cleaned = cleaned.Replace(bad, '_');

        if (maxLength > 0 && cleaned.Length > maxLength) cleaned = cleaned[..maxLength];
        return cleaned.Trim();
    }

    /// <summary>下载落盘的文件名：<c>123456 标题.osz</c>。</summary>
    public static string FileName(int setId, string? title) => $"{setId} {Sanitize(title)}.osz";

    /// <summary>下载落盘的文件名（从条目取）。</summary>
    public static string FileName(OszItem item) => FileName(item.SetId, item.Title);

    /// <summary>下载地址；<paramref name="includeVideo"/> = 要不要带视频（<c>full</c> / <c>novideo</c>）。</summary>
    public static string BuildUrl(int setId, bool includeVideo, string mirrorBase = DefaultMirrorBase)
    {
        var basis = string.IsNullOrWhiteSpace(mirrorBase) ? DefaultMirrorBase : mirrorBase.TrimEnd('/');
        return $"{basis}/{(includeVideo ? "full" : "novideo")}/{setId}";
    }
}
