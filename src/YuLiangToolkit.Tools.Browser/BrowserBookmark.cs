using System.Text.Json;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 一个书签（收藏的网址），存在 <c>Data\browser\bookmarks.json</c>。
/// </summary>
public sealed class BrowserBookmark
{
    /// <summary>收藏的地址。</summary>
    public string Url { get; set; } = "";

    /// <summary>收藏时的页面标题（空则列表显示地址）。</summary>
    public string Title { get; set; } = "";

    /// <summary>收藏时间。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>列表显示标题：优先标题，没有就显示地址。</summary>
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;

    /// <summary>收藏日期文本（面板里显示）。</summary>
    [JsonIgnore]
    public string CreatedText => CreatedAt == default ? "" : CreatedAt.ToString("yyyy-MM-dd");

    public override string ToString() => $"{DisplayTitle} <{Url}>";
}

/// <summary>
/// 书签规则（纯逻辑，自检 <c>browser</c> 组直接断言）：清洗 / 去重 / 添加 / 删除 / 查找。
/// 文件读写由 <see cref="BrowserBookmarkStore"/> 负责。
/// </summary>
public static class BrowserBookmarks
{
    /// <summary>最多收藏多少个（防止文件无限变大）。</summary>
    public const int MaxEntries = 500;

    /// <summary>书签文件名（<c>Data\browser\bookmarks.json</c>）。</summary>
    public const string FileName = "bookmarks.json";

    /// <summary>这些地址不能收藏（空白页 / 脚本 / 临时数据页）。</summary>
    private static readonly string[] SkipSchemes = { "about:", "data:", "javascript:", "blob:" };

    /// <summary>
    /// 规范化收藏地址：去首尾空白、拒绝空 / 无意义协议 / 超长地址；返回 <c>null</c> 表示不能收藏。
    /// </summary>
    public static string? NormalizeUrl(string? url)
    {
        var text = url?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 8000) return null;
        foreach (var scheme in SkipSchemes)
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return text;
    }

    /// <summary>创建书签（地址不合法返回 null；标题为空时用地址兜底）。</summary>
    public static BrowserBookmark? Create(string? url, string? title, DateTime createdAt)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null) return null;
        return new BrowserBookmark
        {
            Url = normalized,
            Title = title?.Trim() ?? "",
            CreatedAt = createdAt,
        };
    }

    /// <summary>是否已经收藏了这个地址（按原地址精确比较）。</summary>
    public static bool ContainsUrl(IEnumerable<BrowserBookmark?>? bookmarks, string? url)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null || bookmarks is null) return false;
        foreach (var bookmark in bookmarks)
        {
            if (bookmark is not null && string.Equals(bookmark.Url, normalized, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>添加书签（已在列表里返回 false）；新书签插到最前面，超过上限从末尾裁掉。</summary>
    public static bool Add(IList<BrowserBookmark> bookmarks, BrowserBookmark? bookmark, int max = MaxEntries)
    {
        if (bookmark is null) return false;
        var normalized = Create(bookmark.Url, bookmark.Title, bookmark.CreatedAt);
        if (normalized is null || ContainsUrl(bookmarks, normalized.Url)) return false;

        bookmarks.Insert(0, normalized);
        if (bookmarks.Count > Math.Max(1, max)) bookmarks.RemoveAt(bookmarks.Count - 1);
        return true;
    }

    /// <summary>按地址删除书签；返回是否真的删掉了。</summary>
    public static bool RemoveByUrl(IList<BrowserBookmark> bookmarks, string? url)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null) return false;
        for (var i = 0; i < bookmarks.Count; i++)
        {
            if (!string.Equals(bookmarks[i].Url, normalized, StringComparison.Ordinal)) continue;
            bookmarks.RemoveAt(i);
            return true;
        }
        return false;
    }

    /// <summary>清洗从磁盘读出的书签：去空地址 / 无意义协议 / null，保留顺序，按地址去重，封顶。</summary>
    public static List<BrowserBookmark> Normalize(IEnumerable<BrowserBookmark?>? raw, int max = MaxEntries)
    {
        var result = new List<BrowserBookmark>();
        if (raw is null) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bookmark in raw)
        {
            if (bookmark is null) continue;
            var normalized = Create(bookmark.Url, bookmark.Title, bookmark.CreatedAt);
            if (normalized is null || !seen.Add(normalized.Url)) continue;

            result.Add(normalized);
            if (result.Count >= Math.Max(1, max)) break;
        }
        return result;
    }
}

/// <summary>书签文件读写：<c>Data\browser\bookmarks.json</c>（失败静默降级）。</summary>
public sealed class BrowserBookmarkStore
{
    private readonly ToolStorage _storage;

    public BrowserBookmarkStore(ToolStorage storage) => _storage = storage;

    /// <summary>读书签（文件不存在 / 损坏都返回空列表）。</summary>
    public List<BrowserBookmark> Load()
    {
        try
        {
            var json = _storage.ReadText(BrowserBookmarks.FileName);
            if (string.IsNullOrWhiteSpace(json)) return new List<BrowserBookmark>();
            return BrowserBookmarks.Normalize(
                JsonSerializer.Deserialize<List<BrowserBookmark>>(json, JsonFileStore.Options));
        }
        catch
        {
            return new List<BrowserBookmark>();
        }
    }

    /// <summary>写书签（自动清洗 + 去重 + 封顶）；返回是否写成功。</summary>
    public bool Save(IEnumerable<BrowserBookmark> bookmarks)
        => _storage.WriteText(BrowserBookmarks.FileName,
            JsonFileStore.Serialize(BrowserBookmarks.Normalize(bookmarks))) is not null;
}
