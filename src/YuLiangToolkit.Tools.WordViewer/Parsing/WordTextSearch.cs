namespace YuLiangToolkit.Tools.WordViewer.Parsing;

/// <summary>在 Word 文档里找到的一处文字命中。</summary>
/// <param name="BlockIndex">命中所在块在 <see cref="WordDocument.Blocks"/> 里的下标（界面靠它高亮 / 滚动）。</param>
/// <param name="Start">命中在段落纯文本里的起点（字符下标；表格命中时是所在单元格段落的起点）。</param>
/// <param name="Length">命中长度（= 查询词长度）。</param>
/// <param name="Preview">给「查找」状态栏看的上下文片段（过长会截断并加省略号）。</param>
public sealed record WordSearchHit(int BlockIndex, int Start, int Length, string Preview)
{
    /// <summary>
    /// 命中是不是在表格单元格里。表格命中只能滚到**表格块**（用渲染器的「块 → 元素」映射），
    /// 没法像正文段落那样把整段高亮，界面会在状态栏说明。
    /// </summary>
    public bool InTable { get; init; }
}

/// <summary>
/// 纯逻辑的全文查找：不做任何 WPF 事情，输入模型、输出命中列表，界面负责高亮 / 滚动。
/// <para>为什么单独一层：查找规则（大小写、重叠、预览片段、表格展开）最容易写错，抽成纯函数后
/// 自检能在控制台里逐条断言，不用起窗口。</para>
/// <para><b>覆盖范围</b>：正文段落 + 表格单元格里的段落；页眉 / 页脚 / 脚注 / 尾注**不参与**查找
/// （它们不是正文，跳转语义也不一样）。</para>
/// </summary>
public static class WordTextSearch
{
    /// <summary>预览片段最多多少字符。</summary>
    private const int PreviewMaxLength = 80;

    /// <summary>命中点前面保留多少字符的上下文（让用户看清命中词周围的文字）。</summary>
    private const int PreviewLeadingContext = 20;

    /// <summary>
    /// 在整篇文档里找 <paramref name="query"/>，返回按出现顺序排列的命中。
    /// <para>正文段落：同一段落里的多处命中都会返回；表格：逐个单元格查，命中的
    /// <see cref="WordSearchHit.BlockIndex"/> 指向**表格块**、<see cref="WordSearchHit.InTable"/> = true。
    /// <paramref name="matchCase"/> = false 时忽略大小写。</para>
    /// </summary>
    public static IReadOnlyList<WordSearchHit> Find(WordDocument document, string query, bool matchCase = false)
    {
        var hits = new List<WordSearchHit>();
        if (document is null || string.IsNullOrEmpty(query)) return hits;

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        for (var blockIndex = 0; blockIndex < document.Blocks.Count; blockIndex++)
        {
            switch (document.Blocks[blockIndex])
            {
                case WordParagraphBlock paragraph:
                    FindInText(paragraph.Text, blockIndex, inTable: false, query, comparison, hits);
                    break;

                case WordTableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            // 纵向合并的占位格没有实际内容（上面那格的内容已经查过），跳过避免重复命中
                            if (cell.IsMergedContinuation) continue;
                            foreach (var child in cell.Blocks.OfType<WordParagraphBlock>())
                                FindInText(child.Text, blockIndex, inTable: true, query, comparison, hits);
                        }
                    }

                    break;
            }
        }

        return hits;
    }

    /// <summary>在一段文字里找出所有不重叠的命中并加到结果里。</summary>
    private static void FindInText(
        string text, int blockIndex, bool inTable, string query,
        StringComparison comparison, List<WordSearchHit> hits)
    {
        if (text.Length == 0 || text.Length < query.Length) return;

        // 从上一处命中之后继续找：不重复、也不会漏掉紧挨着的两处（"aa" 里找 "a"）
        var searchFrom = 0;
        while (searchFrom <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, searchFrom, comparison);
            if (index < 0) break;

            hits.Add(new WordSearchHit(blockIndex, index, query.Length, BuildPreview(text, index, query.Length))
            {
                InTable = inTable,
            });
            searchFrom = index + Math.Max(1, query.Length);
        }
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
