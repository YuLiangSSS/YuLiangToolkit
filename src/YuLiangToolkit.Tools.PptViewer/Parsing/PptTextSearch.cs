using System.Text;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

/// <summary>查找命中的来源：幻灯片正文 / 演讲者备注。</summary>
public enum PptSearchSource
{
    /// <summary>幻灯片上的可见文字（标题 + 正文 + 表格）。</summary>
    SlideText,

    /// <summary>演讲者备注。</summary>
    Notes,
}

/// <summary>一条查找命中。</summary>
/// <param name="SlideIndex">第几页（1 开始）。</param>
/// <param name="SlideTitle">页显示名（「3. 标题」/「第 3 页」）。</param>
/// <param name="Source">命中在正文还是备注。</param>
/// <param name="MatchIndex">在来源文本里的起始下标。</param>
/// <param name="MatchLength">命中文字长度。</param>
/// <param name="Snippet">给界面显示的上下文片段（已去掉换行、超长截断）。</param>
public sealed record PptSearchHit(
    int SlideIndex,
    string SlideTitle,
    PptSearchSource Source,
    int MatchIndex,
    int MatchLength,
    string Snippet);

/// <summary>
/// 跨幻灯片查找（纯逻辑，不碰 WPF / 文件）：在每页的 <see cref="PptSlide.PlainText"/> 和
/// <see cref="PptSlide.Notes"/> 里找子串，命中一条记录一条（同一页出现多次就多条）。
///
/// <para>大小写规则：默认忽略大小写（<c>OrdinalIgnoreCase</c>），<paramref name="matchCase"/>
/// 为 true 时用 <c>Ordinal</c>；空白查询返回空列表。</para>
/// </summary>
public static class PptTextSearch
{
    /// <summary>片段前后各留多少个字符。</summary>
    public const int SnippetRadius = 24;

    /// <summary>在整份演示文稿里查找（按页序、正文在备注前）。</summary>
    public static IReadOnlyList<PptSearchHit> Search(PptPresentation presentation, string? query, bool matchCase = false)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        var needle = query?.Trim() ?? "";
        if (needle.Length == 0) return Array.Empty<PptSearchHit>();

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new List<PptSearchHit>();

        foreach (var slide in presentation.Slides)
        {
            Collect(hits, slide, slide.PlainText, PptSearchSource.SlideText, needle, comparison);
            Collect(hits, slide, slide.Notes, PptSearchSource.Notes, needle, comparison);
        }

        return hits;
    }

    /// <summary>截取命中位置前后的上下文（换行压成空格；太长时前后加省略号）。</summary>
    public static string BuildSnippet(string text, int matchStart, int matchLength, int radius = SnippetRadius)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (matchStart < 0) matchStart = 0;
        if (matchStart > text.Length) return "";
        if (radius < 0) radius = 0;

        var start = Math.Max(0, matchStart - radius);
        var end = Math.Min(text.Length, matchStart + Math.Max(0, matchLength) + radius);
        var snippet = text[start..end].Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

        var prefix = start > 0 ? "…" : "";
        var suffix = end < text.Length ? "…" : "";
        return prefix + snippet.Trim() + suffix;
    }

    /// <summary>命中了多少页（去重，用来显示「找到 N 处 / M 页」）。</summary>
    public static int CountSlides(IReadOnlyList<PptSearchHit> hits)
        => hits.Select(hit => hit.SlideIndex).Distinct().Count();

    private static void Collect(
        List<PptSearchHit> hits,
        PptSlide slide,
        string text,
        PptSearchSource source,
        string needle,
        StringComparison comparison)
    {
        if (string.IsNullOrEmpty(text)) return;

        var index = 0;
        while (index <= text.Length - needle.Length)
        {
            var found = text.IndexOf(needle, index, comparison);
            if (found < 0) break;

            hits.Add(new PptSearchHit(
                slide.Index,
                slide.DisplayName,
                source,
                found,
                needle.Length,
                BuildSnippet(text, found, needle.Length)));

            index = found + Math.Max(1, needle.Length);
        }
    }

    /// <summary>把一段搜索结果拼成日志 / 状态栏用的一行（自检和界面共用）。</summary>
    public static string DescribeHits(IReadOnlyList<PptSearchHit> hits, string? query = null)
    {
        ArgumentNullException.ThrowIfNull(hits);
        if (hits.Count == 0) return string.IsNullOrWhiteSpace(query) ? "没有输入查找内容" : $"没找到「{query}」";

        var builder = new StringBuilder();
        builder.Append($"找到 {hits.Count} 处 / {CountSlides(hits)} 页");
        if (!string.IsNullOrWhiteSpace(query)) builder.Append($"（查找：{query}）");
        return builder.ToString();
    }
}
