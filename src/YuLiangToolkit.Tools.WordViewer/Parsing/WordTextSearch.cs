namespace YuLiangToolkit.Tools.WordViewer.Parsing;

/// <summary>在 Word 文档里找到的一处文字命中。</summary>
/// <param name="BlockIndex">命中所在段落块在 <see cref="WordDocument.Blocks"/> 里的下标（界面靠它高亮 / 滚动）。</param>
/// <param name="Start">命中在段落纯文本里的起点（字符下标）。</param>
/// <param name="Length">命中长度（= 查询词长度）。</param>
/// <param name="Preview">给「查找」状态栏看的上下文片段（过长会截断并加省略号）。</param>
public sealed record WordSearchHit(int BlockIndex, int Start, int Length, string Preview);

/// <summary>
/// 纯逻辑的全文查找：不做任何 WPF 事情，输入模型、输出命中列表，界面负责高亮 / 滚动。
/// <para>为什么单独一层：查找规则（大小写、重叠、预览片段）最容易写错，抽成纯函数后
/// 自检能在控制台里逐条断言，不用起窗口。</para>
/// </summary>
public static class WordTextSearch
{
    /// <summary>预览片段最多多少字符。</summary>
    private const int PreviewMaxLength = 80;

    /// <summary>命中点前面保留多少字符的上下文（让用户看清命中词周围的文字）。</summary>
    private const int PreviewLeadingContext = 20;

    /// <summary>
    /// 在整篇文档里找 <paramref name="query"/>，返回按出现顺序排列的命中。
    /// <para>只查正文里的段落块（表格 / 图片块没有可直接高亮的段落，不参与查找）；
    /// 同一段落里的多处命中都会返回；<paramref name="matchCase"/> = false 时忽略大小写。</para>
    /// </summary>
    public static IReadOnlyList<WordSearchHit> Find(WordDocument document, string query, bool matchCase = false)
    {
        var hits = new List<WordSearchHit>();
        if (document is null || string.IsNullOrEmpty(query)) return hits;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (var blockIndex = 0; blockIndex < document.Blocks.Count; blockIndex++)
        {
            if (document.Blocks[blockIndex] is not WordParagraphBlock paragraph) continue;

            var text = paragraph.Text;
            if (text.Length == 0 || text.Length < query.Length) continue;

            // 从上一处命中之后继续找：不重复、也不会漏掉紧挨着的两处（"aa" 里找 "a"）
            var searchFrom = 0;
            while (searchFrom <= text.Length - query.Length)
            {
                var index = text.IndexOf(query, searchFrom, comparison);
                if (index < 0) break;

                hits.Add(new WordSearchHit(blockIndex, index, query.Length, BuildPreview(text, index, query.Length)));
                searchFrom = index + Math.Max(1, query.Length);
            }
        }

        return hits;
    }

    /// <summary>
    /// 截取命中点附近的一段文字做预览：保留前面 <see cref="PreviewLeadingContext"/> 个字符，
    /// 总长不超过 <see cref="PreviewMaxLength"/>；制表符 / 换行换成空格，两端被截断时补省略号。
    /// </summary>
    public static string BuildPreview(string text, int start, int length)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var from = Math.Max(0, start - PreviewLeadingContext);
        var take = Math.Min(text.Length - from, Math.Max(PreviewMaxLength, length + PreviewLeadingContext));
        var snippet = text.Substring(from, take)
            .Replace('\t', ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');

        if (from > 0) snippet = "…" + snippet;
        if (from + take < text.Length) snippet += "…";
        return snippet;
    }
}
