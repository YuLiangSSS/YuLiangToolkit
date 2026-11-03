namespace YuLiangToolkit.Tools.WordViewer.Parsing;

/// <summary>
/// 段落 / 文字的对齐方式（对应 <c>w:jc</c> 里我们认识的那几种，不认识的退回左对齐）。
/// </summary>
public enum WordTextAlignment
{
    /// <summary>左对齐（默认）。</summary>
    Left,

    /// <summary>居中。</summary>
    Center,

    /// <summary>右对齐。</summary>
    Right,

    /// <summary>两端对齐。</summary>
    Justify,
}

/// <summary>
/// 一段文字里格式相同的一小截（≈ <c>w:r</c>）。解析时把「文字内容」和「怎么显示」分开存，
/// 这样模型层不依赖任何 WPF 类型，自检可以在控制台里直接断言；渲染交给 <c>WordFlowRenderer</c>。
/// </summary>
public sealed class WordRun
{
    /// <summary>这一截的纯文本（制表符 / 软换行已经换成 <c>\t</c> / <c>\n</c>）。</summary>
    public string Text { get; set; } = "";

    /// <summary>粗体（<c>w:b</c>）。</summary>
    public bool Bold { get; set; }

    /// <summary>斜体（<c>w:i</c>）。</summary>
    public bool Italic { get; set; }

    /// <summary>下划线（<c>w:u</c>，<c>w:val="none"</c> 视为没有）。</summary>
    public bool Underline { get; set; }

    /// <summary>删除线（<c>w:strike</c>）。</summary>
    public bool Strike { get; set; }

    /// <summary>上标（<c>w:vertAlign w:val="superscript"</c>）。</summary>
    public bool IsSuperscript { get; set; }

    /// <summary>下标（<c>w:vertAlign w:val="subscript"</c>）。</summary>
    public bool IsSubscript { get; set; }

    /// <summary>是不是超链接的一部分（渲染成蓝色下划线，并接 <c>RequestNavigate</c>）。</summary>
    public bool IsHyperlink { get; set; }

    /// <summary>超链接地址；文档内锚点（<c>#书签</c>）时是 <c>null</c>（只显示成链接样式，不跳转）。</summary>
    public string? Url { get; set; }

    /// <summary>文字颜色，<c>#RRGGBB</c>；<c>auto</c> / 非法值时为 <c>null</c>（用正文默认色）。</summary>
    public string? ColorHex { get; set; }

    /// <summary>字体名（<c>w:rFonts</c> 的 ascii / hAnsi / eastAsia 里第一个非空值）。</summary>
    public string? FontFamily { get; set; }

    /// <summary>字号（磅，来自 <c>w:sz</c> 的半点值 ÷ 2）；没写时为 <c>null</c>（用正文默认 11 磅）。</summary>
    public double? FontSizePt { get; set; }

    /// <summary>高亮底色，<c>#RRGGBB</c>（把 <c>w:highlight</c> 的颜色名换成十六进制）。</summary>
    public string? HighlightHex { get; set; }
}

/// <summary>
/// 段落格式（≈ <c>w:pPr</c> + 样式）。<see cref="HeadingLevel"/> = 0 表示正文，
/// 1 ~ 6 表示 1 ~ 6 级标题（渲染时字号递减、加粗、加段前段后距）。
/// </summary>
public sealed class WordParagraphStyle
{
    /// <summary>标题级别：0 = 正文，1 ~ 6 = 标题 1 ~ 6。</summary>
    public int HeadingLevel { get; set; }

    /// <summary>水平对齐方式。</summary>
    public WordTextAlignment Alignment { get; set; } = WordTextAlignment.Left;

    /// <summary>列表缩进层级（<c>w:numPr/w:ilvl</c>，0 = 第一级）。</summary>
    public int IndentLevel { get; set; }

    /// <summary>列表标记（例如 <c>"• "</c> / <c>"1. "</c>）；正文段落是 <c>null</c>。</summary>
    public string? ListMarker { get; set; }

    /// <summary>段前是否有分页（<c>w:pageBreakBefore</c> 或 <c>w:br w:type="page"</c>）。</summary>
    public bool IsPageBreakBefore { get; set; }

    /// <summary>左缩进（DIP，来自 <c>w:ind</c> 的 twip 值）；0 = 不缩进。</summary>
    public double LeftIndentDip { get; set; }
}

/// <summary>文档里的一个内容块（段落 / 表格 / 图片）的公共基类。</summary>
public abstract class WordBlock
{
    /// <summary>在 <see cref="WordDocument.Blocks"/> 里的下标（大纲、查找跳转都靠它定位）。</summary>
    public int Index { get; internal set; }
}

/// <summary>普通段落。</summary>
public sealed class WordParagraphBlock : WordBlock
{
    /// <summary>段落格式。</summary>
    public WordParagraphStyle Style { get; } = new();

    /// <summary>按出现顺序排列的文字片段。</summary>
    public List<WordRun> Runs { get; } = new();

    /// <summary>整段纯文本（查找、大纲、复制用）。</summary>
    public string Text
    {
        get
        {
            if (Runs.Count == 0) return "";
            var text = string.Concat(Runs.Select(run => run.Text));
            return text;
        }
    }
}

/// <summary>表格。</summary>
public sealed class WordTableBlock : WordBlock
{
    /// <summary>表格各行（按出现顺序）。</summary>
    public List<WordTableRow> Rows { get; } = new();

    /// <summary>各列宽度（DIP，来自 <c>w:tblGrid/w:gridCol</c> 的 twip 值）；缺失时为空列表。</summary>
    public List<double> ColumnWidthDip { get; } = new();
}

/// <summary>表格里的一行。</summary>
public sealed class WordTableRow
{
    /// <summary>这一行的单元格。</summary>
    public List<WordTableCell> Cells { get; } = new();

    /// <summary>是不是表头行（<c>w:trPr/w:tblHeader</c>）：渲染时加粗 + 浅灰底。</summary>
    public bool IsHeader { get; set; }
}

/// <summary>表格里的一个单元格。</summary>
public sealed class WordTableCell
{
    /// <summary>单元格里的内容块（目前只有段落与图片；嵌套表格会跳过）。</summary>
    public List<WordBlock> Blocks { get; } = new();

    /// <summary>横向合并了多少列（<c>w:gridSpan</c>，默认 1）。</summary>
    public int ColumnSpan { get; set; } = 1;

    /// <summary>纵向合并了多少行（由 <c>w:vMerge</c> 的 restart / continue 推算，默认 1）。</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>底纹颜色，<c>#RRGGBB</c>；没有底纹时为 <c>null</c>。</summary>
    public string? ShadingHex { get; set; }

    /// <summary>是不是表头单元格（整行 <c>tblHeader</c>）。</summary>
    public bool IsHeader { get; set; }

    /// <summary>
    /// 是不是「被上面某个纵向合并单元格盖住」的占位格：<b>渲染时跳过</b>，
    /// 否则 WPF Table 会把它排到下一列，整张表错位。
    /// </summary>
    public bool IsMergedContinuation { get; set; }
}

/// <summary>图片（字节已经从 docx 里拷出来，可脱离 <c>OoxmlPackage</c> 独立使用）。</summary>
public sealed class WordImageBlock : WordBlock
{
    /// <summary>图片原始字节（png / jpeg / gif / bmp…）。</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();

    /// <summary>图片 MIME 类型（按扩展名猜，多半是 <c>image/png</c> / <c>image/jpeg</c>）。</summary>
    public string ContentType { get; set; } = "";

    /// <summary>显示宽度（DIP，来自 <c>wp:extent/@cx</c> 的 EMU）；≤ 0 表示没写尺寸。</summary>
    public double WidthDip { get; set; }

    /// <summary>显示高度（DIP，来自 <c>wp:extent/@cy</c> 的 EMU）；≤ 0 表示没写尺寸。</summary>
    public double HeightDip { get; set; }

    /// <summary>替代文字（<c>wp:docPr/@descr</c>，鼠标悬停时显示）。</summary>
    public string? AltText { get; set; }
}

/// <summary>大纲里的一条（标题级别 + 文字 + 对应正文块下标）。</summary>
/// <param name="Level">标题级别（1 ~ 6）。</param>
/// <param name="Text">标题文字。</param>
/// <param name="BlockIndex">在 <see cref="WordDocument.Blocks"/> 里的下标，点大纲时跳到这里。</param>
public sealed record WordHeading(int Level, string Text, int BlockIndex);

/// <summary>
/// 解析完成的 Word 文档模型。<b>纯数据、不依赖 WPF</b>：解析器在后台线程产出它，
/// 渲染器在 UI 线程把它变成 <c>FlowDocument</c>，自检可以在控制台里直接断言。
/// <para>解析时的安全上限（块数 / 图片数 / 单元格数）触发后会把说明写进 <see cref="Notes"/>，
/// 界面用日志提示用户「这份文档太大，已简化显示」，而不是直接失败。</para>
/// </summary>
public sealed class WordDocument
{
    private readonly List<WordBlock> _blocks = new();
    private readonly List<WordHeading> _outline = new();
    private readonly List<string> _notes = new();

    /// <summary>源文件完整路径；从流解析时为空串。</summary>
    public string SourcePath { get; internal set; } = "";

    /// <summary>文档标题（<c>docProps/core.xml</c> 的 <c>dc:title</c>，缺失时用文件名）。</summary>
    public string Title { get; internal set; } = "";

    /// <summary>正文内容块（段落 / 表格 / 图片，按出现顺序）。</summary>
    public IReadOnlyList<WordBlock> Blocks => _blocks;

    /// <summary>标题大纲（按出现顺序，级别 1 ~ 6）。</summary>
    public IReadOnlyList<WordHeading> Outline => _outline;

    /// <summary>段落总数（含表格单元格里的段落）。</summary>
    public int ParagraphCount { get; internal set; }

    /// <summary>表格总数（只数正文里的表格；嵌套表格不支持）。</summary>
    public int TableCount { get; internal set; }

    /// <summary>图片总数。</summary>
    public int ImageCount { get; internal set; }

    /// <summary>解析时触发的简化 / 截断说明（中文，可直接显示在日志里）。</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>内部用的可变列表（解析器写，外部只读）。</summary>
    internal List<WordBlock> BlockList => _blocks;

    /// <summary>内部用的可变大纲列表。</summary>
    internal List<WordHeading> OutlineList => _outline;

    /// <summary>内部用的说明列表。</summary>
    internal List<string> NoteList => _notes;

    /// <summary>
    /// 取某个正文块的纯文本；下标越界、或这个块不是段落（表格 / 图片）时返回空串。
    /// <para>自检用它验证「块下标 → 文字」的对应关系；界面的查找高亮也按这个下标找段落。</para>
    /// </summary>
    public string GetParagraphText(int blockIndex)
    {
        if (blockIndex < 0 || blockIndex >= _blocks.Count) return "";
        return _blocks[blockIndex] is WordParagraphBlock paragraph ? paragraph.Text : "";
    }

    /// <summary>按标题大纲的文字找块下标；找不到返回 -1（保留给以后的大纲联动）。</summary>
    public int FindHeadingBlockIndex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        foreach (var heading in _outline)
        {
            if (string.Equals(heading.Text, text, StringComparison.Ordinal)) return heading.BlockIndex;
        }

        return -1;
    }
}
