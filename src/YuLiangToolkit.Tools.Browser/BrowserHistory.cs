using System.Text.Json;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;
using YuLiangToolkit.Core.Storage;

namespace YuLiangToolkit.Tools.Browser;

/// <summary>
/// 一条浏览历史（<c>Data\browser\history.json</c>，用户数据，清缓存不会被删）。
/// 继承 <see cref="ObservableObject"/>：同一地址再次访问时只更新标题 / 时间，界面能跟着刷新。
/// </summary>
public sealed class BrowserHistoryEntry : ObservableObject
{
    private string _url = "";
    private string _title = "";
    private DateTime _visitedAt;

    /// <summary>页面地址。</summary>
    public string Url
    {
        get => _url;
        set => Set(ref _url, value ?? "");
    }

    /// <summary>页面标题（可能为空，界面回退显示地址）。</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value ?? "");
    }

    /// <summary>访问时间。</summary>
    public DateTime VisitedAt
    {
        get => _visitedAt;
        set => Set(ref _visitedAt, value);
    }

    /// <summary>列表里显示的主标题：优先标题，没有就显示地址。</summary>
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;

    /// <summary>访问时间文本（列表右侧）。</summary>
    [JsonIgnore]
    public string VisitedText => VisitedAt == default ? "" : VisitedAt.ToString("yyyy-MM-dd HH:mm");

    public override string ToString() => $"{DisplayTitle} <{Url}>";
}

/// <summary>
/// 浏览历史的规则（纯逻辑，自检直接断言）：追加 / 连续同址去重 / 封顶 / 清洗 / 跳过无意义地址。
/// 文件读写由 <see cref="BrowserHistoryStore"/> 负责。
/// </summary>
public static class BrowserHistory
{
    /// <summary>最多保留多少条（防历史文件无限变大）。</summary>
    public const int MaxEntries = 2000;

    /// <summary>历史文件名（<c>Data\browser\history.json</c>）。</summary>
    public const string FileName = "history.json";

    /// <summary>不记录这些协议（空白页 / data URL 会刷屏，而且可能非常长）。</summary>
    private static readonly string[] SkipSchemes = { "about:", "data:", "blob:", "javascript:" };

    /// <summary>
    /// 追加一条历史：地址为空 / 无意义协议 / 超长时返回 <c>false</c>（不记）；
    /// 和最新一条地址相同则<b>只更新标题与时间</b>（刷新 / 重定向不刷屏），也返回 <c>false</c>。
    /// 新记录插到最前面，超过 <paramref name="max"/> 条时从末尾裁掉。
    /// </summary>
    public static bool Append(IList<BrowserHistoryEntry> entries, string? url, string? title, DateTime visitedAt, int max = MaxEntries)
    {
        var normalized = NormalizeUrl(url);
        if (normalized is null) return false;
        var normalizedTitle = (title ?? "").Trim();

        if (entries.Count > 0 && string.Equals(entries[0].Url, normalized, StringComparison.Ordinal))
        {
            if (normalizedTitle.Length > 0) entries[0].Title = normalizedTitle;
            entries[0].VisitedAt = visitedAt;
            return false;
        }

        entries.Insert(0, new BrowserHistoryEntry
        {
            Url = normalized,
            Title = normalizedTitle,
            VisitedAt = visitedAt,
        });

        if (entries.Count > Math.Max(1, max))
            entries.RemoveAt(entries.Count - 1);
        return true;
    }

    /// <summary>清洗从磁盘读出来的历史：去空地址 / 无意义协议、去空元素、封顶；保留原有顺序（新的在前）。</summary>
    public static List<BrowserHistoryEntry> Normalize(IEnumerable<BrowserHistoryEntry?>? raw, int max = MaxEntries)
    {
        var result = new List<BrowserHistoryEntry>();
        if (raw is null) return result;

        foreach (var entry in raw)
        {
            if (entry is null) continue;
            var url = NormalizeUrl(entry.Url);
            if (url is null) continue;
            result.Add(new BrowserHistoryEntry
            {
                Url = url,
                Title = entry.Title?.Trim() ?? "",
                VisitedAt = entry.VisitedAt,
            });
            if (result.Count >= Math.Max(1, max)) break;
        }
        return result;
    }

    /// <summary>地址清洗：去首尾空白、跳过空白页 / data URL / 超长地址；返回 null 表示不该记录。</summary>
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
}

/// <summary>
/// 历史文件读写：<c>Data\browser\history.json</c>。
/// 所有 IO 失败都静默降级（读失败当空列表、写失败返回 false），不抛异常。
/// </summary>
public sealed class BrowserHistoryStore
{
    private readonly ToolStorage _storage;

    public BrowserHistoryStore(ToolStorage storage) => _storage = storage;

    /// <summary>读历史（文件不存在 / 损坏都返回空列表）。</summary>
    public List<BrowserHistoryEntry> Load()
    {
        try
        {
            var json = _storage.ReadText(BrowserHistory.FileName);
            if (string.IsNullOrWhiteSpace(json)) return new List<BrowserHistoryEntry>();
            var entries = JsonSerializer.Deserialize<List<BrowserHistoryEntry>>(json, JsonFileStore.Options);
            return BrowserHistory.Normalize(entries);
        }
        catch
        {
            return new List<BrowserHistoryEntry>();
        }
    }

    /// <summary>写历史（自动清洗 + 封顶）；返回是否写成功。</summary>
    public bool Save(IEnumerable<BrowserHistoryEntry> entries)
        => _storage.WriteText(BrowserHistory.FileName,
            JsonFileStore.Serialize(BrowserHistory.Normalize(entries))) is not null;

    /// <summary>清空历史文件（「清空浏览记录」用）；返回是否确定已经没有了。</summary>
    public bool Clear()
        => _storage.Delete(BrowserHistory.FileName) || !_storage.FileExists(BrowserHistory.FileName);
}
