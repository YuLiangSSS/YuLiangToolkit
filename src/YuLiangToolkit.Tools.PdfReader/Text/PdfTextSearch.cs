namespace YuLiangToolkit.Tools.PdfReader.Text;

/// <summary>一次文字搜索命中的位置（哪一页、页内第几个字符、给界面显示的上下文片段）。</summary>
public sealed record PdfSearchHit(int PageIndex, int PageCharIndex, string Snippet);

/// <summary>
/// 在 <see cref="PdfTextLayer"/> 上做简单的全文搜索（大小写不敏感、支持中文；纯逻辑，自检直接断言）。
///
/// <para>结果按「先页序、后页内位置」排列；界面用 <see cref="PdfSearchHit.PageIndex"/> 跳页，
/// 用 <see cref="PdfSearchHit.Snippet"/> 在搜索栏里给用户看一句上下文（浏览器 Ctrl+F 那种手感）。</para>
/// </summary>
public static class PdfTextSearch
{
    /// <summary>一次最多返回多少个命中（超大 PDF 搜索单字时防止结果几十万条把界面卡死）。</summary>
    public const int DefaultMaxHits = 500;

    /// <summary>上下文片段里命中位置前后各保留多少个字符（再做空白压缩）。</summary>
    public const int SnippetRadius = 24;

    /// <summary>
    /// 在整个文字层里找 <paramref name="query"/>（忽略首尾空白；大小写不敏感）。
    /// 空查询 / 没有文字层 → 空结果。命中数上限 <paramref name="maxHits"/>。
    /// </summary>
    public static IReadOnlyList<PdfSearchHit> Find(PdfTextLayer? layer, string? query, int maxHits = DefaultMaxHits)
    {
        if (layer is null) return Array.Empty<PdfSearchHit>();

        var needle = query?.Trim() ?? "";
        if (needle.Length == 0 || layer.Pages.Count == 0) return Array.Empty<PdfSearchHit>();

        var limit = Math.Max(1, maxHits);
        var hits = new List<PdfSearchHit>();

        foreach (var page in layer.Pages)
        {
            if (string.IsNullOrEmpty(page.Text)) continue;

            var from = 0;
            while (from <= page.Text.Length - needle.Length)
            {
                var found = page.Text.IndexOf(needle, from, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;

                hits.Add(new PdfSearchHit(page.PageIndex, found, BuildSnippet(page.Text, found, needle.Length)));
                if (hits.Count >= limit) return hits;

                from = found + Math.Max(1, needle.Length);
            }
        }

        return hits;
    }

    /// <summary>
    /// 取命中位置前后 <see cref="SnippetRadius"/> 个字符做上下文，并把连续空白压成单个空格，
    /// 两端加省略号（例如「…毕业设计（论文）各阶段的时间节…」）。
    /// </summary>
    public static string BuildSnippet(string text, int matchIndex, int matchLength, int radius = SnippetRadius)
    {
        if (string.IsNullOrEmpty(text)) return "";

        radius = Math.Max(4, radius);
        var start = Math.Max(0, matchIndex - radius);
        var end = Math.Min(text.Length, matchIndex + Math.Max(0, matchLength) + radius);
        if (end <= start) return "";

        var piece = text[start..end];
        var builder = new StringBuilder(piece.Length);
        var lastWasWhite = false;
        foreach (var ch in piece)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhite) builder.Append(' ');
                lastWasWhite = true;
            }
            else
            {
                builder.Append(ch);
                lastWasWhite = false;
            }
        }

        var snippet = builder.ToString().Trim();
        if (start > 0) snippet = "…" + snippet;
        if (end < text.Length) snippet += "…";
        return snippet;
    }
}
