namespace YuLiangToolkit.Tools.TextEditor.Core.Markdown;

/// <summary>
/// Markdown 解析结果的「块」模型（纯数据，不依赖 WPF，方便自检直接断言）：
/// 标题 / 段落 / 代码块 / 引用 / 列表 / 表格 / 分割线。
/// </summary>
public abstract record MarkdownBlock;

/// <summary>ATX 标题（<c>#</c> ~ <c>######</c>），<paramref name="Level"/> 1~6。</summary>
public sealed record MarkdownHeading(int Level, IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>普通段落。</summary>
public sealed record MarkdownParagraph(IReadOnlyList<MarkdownInline> Inlines) : MarkdownBlock;

/// <summary>围栏代码块（``` 或 ~~~）。<paramref name="Language"/> 是围栏后的语言名（可空）。</summary>
public sealed record MarkdownCodeBlock(string? Language, string Text) : MarkdownBlock;

/// <summary>引用块（<c>&gt;</c>），内部可以有任意块。</summary>
public sealed record MarkdownQuote(IReadOnlyList<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>列表：<paramref name="Ordered"/> = 有序（1. 2. 3.），<paramref name="StartNumber"/> 是起始序号。</summary>
public sealed record MarkdownList(bool Ordered, int StartNumber, IReadOnlyList<MarkdownListItem> Items) : MarkdownBlock;

/// <summary>列表里的一项，内部可以有段落 / 嵌套列表。</summary>
public sealed record MarkdownListItem(IReadOnlyList<MarkdownBlock> Blocks);

/// <summary>表格单元格（行内内容）。</summary>
public sealed record MarkdownTableCell(IReadOnlyList<MarkdownInline> Inlines);

/// <summary>表格（第一行是表头，第二行是 <c>|---|:--:|</c> 对齐行）。</summary>
public sealed record MarkdownTable(
    IReadOnlyList<MarkdownTableColumn> Columns,
    IReadOnlyList<MarkdownTableCell> Header,
    IReadOnlyList<IReadOnlyList<MarkdownTableCell>> Rows) : MarkdownBlock;

/// <summary>表格列对齐。</summary>
public sealed record MarkdownTableColumn(MarkdownTableAlignment Alignment);

/// <summary>表格列对齐方式。</summary>
public enum MarkdownTableAlignment
{
    /// <summary>默认（左对齐）。</summary>
    None,

    /// <summary>左对齐 <c>:---</c>。</summary>
    Left,

    /// <summary>居中 <c>:---:</c>。</summary>
    Center,

    /// <summary>右对齐 <c>---:</c>。</summary>
    Right,
}

/// <summary>分割线（<c>---</c> / <c>***</c> / <c>___</c>）。</summary>
public sealed record MarkdownThematicBreak : MarkdownBlock;

/// <summary>行内元素（段落 / 标题 / 表格单元格里的一段内容）。</summary>
public abstract record MarkdownInline;

/// <summary>纯文本。</summary>
public sealed record MarkdownText(string Text) : MarkdownInline;

/// <summary>斜体 <c>*x*</c> / <c>_x_</c>。</summary>
public sealed record MarkdownEmphasis(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

/// <summary>加粗 <c>**x**</c> / <c>__x__</c>。</summary>
public sealed record MarkdownStrong(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

/// <summary>删除线 <c>~~x~~</c>。</summary>
public sealed record MarkdownStrikethrough(IReadOnlyList<MarkdownInline> Inlines) : MarkdownInline;

/// <summary>行内代码 <c>`code`</c>。</summary>
public sealed record MarkdownInlineCode(string Text) : MarkdownInline;

/// <summary>链接 <c>[文字](地址)</c>。</summary>
public sealed record MarkdownLink(IReadOnlyList<MarkdownInline> Text, string Url) : MarkdownInline;

/// <summary>图片 <c>![说明](地址)</c>。</summary>
public sealed record MarkdownImage(string Alt, string Url) : MarkdownInline;

/// <summary>硬换行（行尾两个空格或反斜杠）。</summary>
public sealed record MarkdownHardBreak : MarkdownInline;
