using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.MailSender.Views;

/// <summary>
/// 富文本编辑器（WPF <see cref="RichTextBox"/> / <see cref="FlowDocument"/>）与 HTML 的互转。
///
/// <para><b>为什么要手写</b>：项目里没有 HTML 解析 / 生成库，也不想为这一个功能引一个；
/// 这里只需要覆盖邮件正文常用的那一小撮标签，所以按「自己发出的标签一定能再读回来」来设计：
/// <see cref="ToHtml"/> 只输出下面这张清单里的东西，<see cref="FromHtml"/> 也认得它们，
/// 于是「富文本 → 源码 → 富文本」来回复制不会越改越乱。</para>
///
/// <para><b>支持的标签</b>：<c>p / div / br / h1~h3 / b·strong / i·em / u / s·strike·del /
/// span（color / background-color / font-size / font-family / font-weight / font-style / text-decoration）/
/// a[href] / ul / ol / li / blockquote / table · tr · td · th / img[src]</c>；
/// 其它标签被忽略（只保留里面的文字），<c>script / style / head</c> 整段丢掉。</para>
///
/// <para><b>行内图片</b>：编辑时用本机文件 / data: 显示，HTML 里的 <c>src</c> 由调用方通过
/// <paramref name="srcResolver"/>（见 <see cref="ToHtml"/>）决定——发送时换成 <c>cid:…</c> 连附件一起上传，
/// 预览时换成 <c>data:</c> 内联。解析时只认 <c>data:</c> / 本机路径 / <c>cid:</c>，
/// 远程图片不联网（留占位块），保证往返不丢。</para>
/// </summary>
public static class RichTextHtml
{
    /// <summary>编辑区的默认字号（生成 HTML 时等于它就不写 <c>font-size</c>）。</summary>
    public const double DefaultFontSize = 14;

    /// <summary>编辑区里图片的最大显示宽度（发送时不限，只是别把编辑区撑爆）。</summary>
    private const double MaxImageWidth = 640;

    private static readonly Regex TagPattern = new(
        @"<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>(?:\s+[^<>]*?)?)(?<self>/)?>",
        RegexOptions.Compiled);

    private static readonly Regex AttributePattern = new(
        @"(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s""'>]+))",
        RegexOptions.Compiled);

    private static readonly Regex RemoveWholeBlocksPattern = new(
        @"<(script|style|head|title)\b[^>]*>.*?</\1\s*>|<!--.*?-->",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // ================================================================ 生成 HTML

    /// <summary>
    /// 把编辑区内容转成 HTML。
    /// <paramref name="srcResolver"/> 用来改写图片的 <c>src</c>（发送时 → <c>cid:…</c>，
    /// 预览时 → <c>data:…</c>）；传 null 时原样输出编辑时记录的地址。
    /// </summary>
    public static string ToHtml(FlowDocument? document, Func<string, string>? srcResolver = null)
    {
        if (document is null) return "";
        var builder = new StringBuilder();
        var defaults = DocumentDefaults.From(document);
        foreach (var block in document.Blocks) AppendBlock(builder, block, srcResolver, defaults);
        return builder.ToString().Trim();
    }

    /// <summary>
    /// 文档级的默认格式：生成 HTML 时只有「与文档默认不同」的格式才写出去。
    /// FlowDocument 自己的默认字号是 16、默认对齐是 Justify，而编辑区按 14 / 左对齐来用 ——
    /// 不比对的话正文里每段都会挂上 <c>font-size:16px</c> / <c>text-align:justify</c> 这类噪音。
    /// </summary>
    private readonly record struct DocumentDefaults(double FontSize, string FontFamily, TextAlignment Alignment)
    {
        public static DocumentDefaults From(FlowDocument document)
            => new(
                document.FontSize > 0 && !double.IsNaN(document.FontSize) ? document.FontSize : DefaultFontSize,
                document.FontFamily?.Source ?? "",
                document.TextAlignment);

        /// <summary>字号是否值得写进 HTML（忽略 0.5px 内的误差）。</summary>
        public bool IsCustomSize(double size) => !double.IsNaN(size) && size > 0 && Math.Abs(size - FontSize) > 0.5;
    }

    private static void AppendBlock(StringBuilder builder, Block block, Func<string, string>? resolver,
        DocumentDefaults defaults)
    {
        switch (block)
        {
            case Paragraph paragraph:
                AppendParagraph(builder, paragraph, resolver, defaults);
                break;
            case System.Windows.Documents.List list:
                AppendList(builder, list, resolver, defaults);
                break;
            case Table table:
                AppendTable(builder, table, resolver, defaults);
                break;
            case Section section:
                foreach (var child in section.Blocks) AppendBlock(builder, child, resolver, defaults);
                break;
            case BlockUIContainer container when container.Child is Image image:
                var html = ImageHtml(image, resolver);
                if (html.Length > 0) builder.AppendLine($"<p>{html}</p>");
                break;
        }
    }

    private static void AppendParagraph(StringBuilder builder, Paragraph paragraph, Func<string, string>? resolver,
        DocumentDefaults defaults)
    {
        var inner = InlinesToHtml(paragraph.Inlines, resolver, defaults).Trim();
        var tag = HeadingTag(paragraph, defaults);

        if (inner.Length == 0)
        {
            builder.AppendLine("<p><br></p>");
            return;
        }

        var styles = new List<string>();
        var align = paragraph.TextAlignment switch
        {
            TextAlignment.Center => "center",
            TextAlignment.Right => "right",
            TextAlignment.Justify when defaults.Alignment != TextAlignment.Justify => "justify",
            _ => "",
        };
        if (align.Length > 0) styles.Add($"text-align:{align}");
        if (paragraph.Margin.Left > 0.5) styles.Add($"margin-left:{Math.Round(paragraph.Margin.Left)}px");

        var styleText = styles.Count == 0 ? "" : $" style=\"{string.Join(";", styles)}\"";
        builder.AppendLine($"<{tag}{styleText}>{inner}</{tag}>");
    }

    /// <summary>
    /// 按段落字号还原成标题（<see cref="FromHtml"/> 里 h1/h2/h3 对应的字号就是这三档）。
    /// 只看**本地设置**的字号：普通段落只是继承文档默认字号，不能被误判成标题。
    /// </summary>
    private static string HeadingTag(Paragraph paragraph, DocumentDefaults defaults)
    {
        if (paragraph.ReadLocalValue(TextElement.FontSizeProperty) is not double localSize) return "p";
        if (!defaults.IsCustomSize(localSize)) return "p";
        return localSize switch
        {
            >= 28 => "h1",
            >= 22 => "h2",
            >= 18 => "h3",
            _ => "p",
        };
    }

    private static void AppendList(StringBuilder builder, System.Windows.Documents.List list,
        Func<string, string>? resolver, DocumentDefaults defaults)
    {
        var tag = list.MarkerStyle == TextMarkerStyle.Decimal ? "ol" : "ul";
        builder.AppendLine($"<{tag}>");
        foreach (var item in list.ListItems)
        {
            var content = new StringBuilder();
            foreach (var block in item.Blocks)
            {
                if (block is Paragraph paragraph) content.Append(InlinesToHtml(paragraph.Inlines, resolver, defaults));
                else AppendBlock(builder, block, resolver, defaults);   // 列表项里塞了别的块：直接摊开
            }
            builder.AppendLine($"<li>{content.ToString().Trim()}</li>");
        }
        builder.AppendLine($"</{tag}>");
    }

    private static void AppendTable(StringBuilder builder, Table table, Func<string, string>? resolver,
        DocumentDefaults defaults)
    {
        builder.AppendLine("<table>");
        foreach (var rowGroup in table.RowGroups)
        {
            foreach (var row in rowGroup.Rows)
            {
                var isHeader = row.Tag as string == "header";
                var cellTag = isHeader ? "th" : "td";
                builder.Append("<tr>");
                foreach (var cell in row.Cells)
                {
                    var content = new StringBuilder();
                    foreach (var block in cell.Blocks)
                    {
                        if (block is Paragraph paragraph) content.Append(InlinesToHtml(paragraph.Inlines, resolver, defaults));
                        else AppendBlock(builder, block, resolver, defaults);
                    }
                    var colspan = cell.ColumnSpan > 1 ? $" colspan=\"{cell.ColumnSpan}\"" : "";
                    var rowspan = cell.RowSpan > 1 ? $" rowspan=\"{cell.RowSpan}\"" : "";
                    builder.Append($"<{cellTag}{colspan}{rowspan}>{content.ToString().Trim()}</{cellTag}>");
                }
                builder.AppendLine("</tr>");
            }
        }
        builder.AppendLine("</table>");
    }

    private static string InlinesToHtml(InlineCollection inlines, Func<string, string>? resolver,
        DocumentDefaults defaults)
    {
        var builder = new StringBuilder();
        foreach (var inline in inlines) AppendInline(builder, inline, resolver, defaults);
        return builder.ToString();
    }

    private static void AppendInline(StringBuilder builder, Inline inline, Func<string, string>? resolver,
        DocumentDefaults defaults)
    {
        switch (inline)
        {
            case Hyperlink link:
                var inner = InlinesToHtml(link.Inlines, resolver, defaults);
                var href = link.Tag as string ?? link.NavigateUri?.ToString() ?? "";
                builder.Append($"<a href=\"{WebUtility.HtmlEncode(href)}\">{inner}</a>");
                break;
            case Run run:
                AppendWrapped(builder, Encode(run.Text), run, defaults);
                break;
            case Span span:
                AppendWrapped(builder, InlinesToHtml(span.Inlines, resolver, defaults), span, defaults);
                break;
            case LineBreak:
                builder.Append("<br>");
                break;
            case InlineUIContainer container when container.Child is Image image:
                builder.Append(ImageHtml(image, resolver));
                break;
        }
    }

    /// <summary>
    /// 把一段（已编码的）内容按 <paramref name="element"/> 上的格式包成 HTML，只写**用户显式设置过**的格式。
    /// 用 <see cref="DependencyObject.ReadLocalValue"/> 而不是属性值：这些属性都是继承属性，
    /// 直接读会拿到从文档继承来的默认值，结果每段文字都挂一串 <c>font-size</c> / <c>font-family</c>。
    /// </summary>
    private static void AppendWrapped(StringBuilder builder, string content, Inline element, DocumentDefaults defaults)
    {
        if (content.Length == 0) return;

        var styles = new List<string>();
        if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush fg && fg.Color != Colors.Black)
            styles.Add($"color:{Hex(fg.Color)}");
        if (element.ReadLocalValue(TextElement.BackgroundProperty) is SolidColorBrush bg && bg.Color != Colors.Transparent)
            styles.Add($"background-color:{Hex(bg.Color)}");

        if (element.ReadLocalValue(TextElement.FontSizeProperty) is double fontSize && defaults.IsCustomSize(fontSize))
            styles.Add($"font-size:{Math.Round(fontSize)}px");

        if (element.ReadLocalValue(TextElement.FontFamilyProperty) is FontFamily family
            && !string.IsNullOrWhiteSpace(family.Source)
            && !string.Equals(family.Source, defaults.FontFamily, StringComparison.OrdinalIgnoreCase))
            styles.Add($"font-family:{family.Source}");

        var open = new StringBuilder();
        var close = new StringBuilder();

        void Wrap(string tag)
        {
            open.Append('<').Append(tag).Append('>');
            close.Insert(0, $"</{tag}>");
        }

        if (element.ReadLocalValue(TextElement.FontWeightProperty) is FontWeight weight && weight >= FontWeights.SemiBold)
            Wrap("b");
        if (element.ReadLocalValue(TextElement.FontStyleProperty) is FontStyle style && style == FontStyles.Italic)
            Wrap("i");
        if (HasDecorationAt(element, TextDecorationLocation.Underline)) Wrap("u");
        if (HasDecorationAt(element, TextDecorationLocation.Strikethrough)) Wrap("s");

        if (styles.Count > 0)
        {
            open.Append($"<span style=\"{string.Join(";", styles)}\">");
            close.Insert(0, "</span>");
        }

        builder.Append(open).Append(content).Append(close);
    }

    /// <summary>判断一段文字有没有放在某个位置的装饰线（下划线 / 删除线）。</summary>
    public static bool HasDecorationAt(Inline element, TextDecorationLocation location)
        => element.TextDecorations is { Count: > 0 } set && set.Any(d => d.Location == location);

    /// <summary>新建一条装饰线（null 画笔 = 用文字前景色，与 WPF 内置 <c>TextDecorations</c> 一致）。</summary>
    public static TextDecoration CreateDecoration(TextDecorationLocation location)
        => new(location, null, 0, TextDecorationUnit.FontRecommended, TextDecorationUnit.FontRecommended);

    private static string Encode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\n': builder.Append("<br>"); break;
                default: builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }

    private static string ImageHtml(Image image, Func<string, string>? resolver)
    {
        var source = image.Tag as string ?? "";
        if (source.Length == 0) return "";
        var resolved = resolver is null ? source : resolver(source);
        var name = source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? "inline-image"
            : Path.GetFileName(source);
        var alt = WebUtility.HtmlEncode(string.IsNullOrEmpty(name) ? "image" : name);
        var width = image.Width > 0 && !double.IsNaN(image.Width) ? $" width=\"{Math.Round(image.Width)}\"" : "";
        return $"<img src=\"{WebUtility.HtmlEncode(resolved)}\" alt=\"{alt}\"{width} style=\"max-width:100%;\">";
    }


    // ================================================================ 解析 HTML

    /// <summary>
    /// 把 HTML 还原成 <see cref="FlowDocument"/>（编辑器加载草稿 / 从源码切回富文本时用）。
    /// 只认识 <see cref="ToHtml"/> 会输出的那些标签，其它标签忽略、只留文字。
    /// </summary>
    public static FlowDocument FromHtml(string? html)
    {
        var document = new FlowDocument
        {
            FontSize = DefaultFontSize,
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
        };
        var walker = new HtmlWalker(document);
        walker.Walk(RemoveWholeBlocksPattern.Replace(html ?? "", ""));
        walker.Flush();
        if (document.Blocks.Count == 0) document.Blocks.Add(new Paragraph());
        return document;
    }

    /// <summary>行内格式的当前状态（栈里存的就是它）。</summary>
    private readonly record struct FormatState(
        bool Bold = false,
        bool Italic = false,
        bool Underline = false,
        bool Strike = false,
        Color? Foreground = null,
        Color? Background = null,
        double? FontSize = null,
        string? FontFamily = null);

    /// <summary>当前块的类型（决定段落字号 / 缩进）。</summary>
    private enum BlockKind { None, Heading1, Heading2, Heading3, Quote, ListItem }

    /// <summary>
    /// 极简 HTML 走查器：按 token 依次处理「开标签 / 闭标签 / 文本 / 图片」，
    /// 行内格式用栈保存，遇到块级标签就 flush 当前段落。
    /// </summary>
    private sealed class HtmlWalker
    {
        private readonly FlowDocument _document;
        private readonly Stack<FormatState> _formats = new();
        private readonly Stack<System.Windows.Documents.List> _lists = new();
        private readonly Stack<Table> _tables = new();
        private readonly Stack<TableRow> _rows = new();
        private readonly Stack<TableCell> _cells = new();

        private FormatState _format = new();
        private Paragraph? _paragraph;
        private BlockKind _block = BlockKind.None;
        private TextAlignment _alignment = TextAlignment.Left;
        private double _indent;

        public HtmlWalker(FlowDocument document) => _document = document;

        public void Walk(string html)
        {
            var index = 0;
            while (index < html.Length)
            {
                var open = html.IndexOf('<', index);
                if (open < 0)
                {
                    AppendText(html[index..]);
                    break;
                }
                if (open > index) AppendText(html[index..open]);

                var match = TagPattern.Match(html, open);
                if (!match.Success || match.Index != open)
                {
                    AppendText("<");            // 正文里孤零零的 "<"
                    index = open + 1;
                    continue;
                }

                var name = match.Groups["name"].Value.ToLowerInvariant();
                if (match.Groups["close"].Success) HandleClose(name);
                else HandleOpen(name, ParseAttributes(match.Groups["attrs"].Value));

                index = match.Index + match.Length;
            }
        }

        // ----- 文本 / 段落 -----

        private void AppendText(string raw)
        {
            if (raw.Length == 0) return;
            var text = WebUtility.HtmlDecode(raw);
            text = Regex.Replace(text, @"\s+", " ");
            if (text.Length == 0) return;

            // 块级标签之间的换行 / 缩进是排版用的空白，不能凭它新建空段落
            // （否则「导出的 HTML 再读回来」会越转越多空行）
            if (_paragraph is null && text.Trim().Length == 0) return;

            EnsureParagraph();
            var run = new Run(text);
            ApplyFormat(run, _format);
            _paragraph!.Inlines.Add(run);
        }

        private void EnsureParagraph()
        {
            if (_paragraph is not null) return;
            var paragraph = new Paragraph
            {
                FontSize = _block switch
                {
                    BlockKind.Heading1 => 30,
                    BlockKind.Heading2 => 24,
                    BlockKind.Heading3 => 19,
                    _ => DefaultFontSize,
                },
                TextAlignment = _alignment,
            };
            if (_block == BlockKind.Quote) paragraph.Margin = new Thickness(20 + _indent, 0, 0, 0);
            else if (_indent > 0) paragraph.Margin = new Thickness(_indent, 0, 0, 0);

            AddBlock(paragraph);
            _paragraph = paragraph;
        }

        /// <summary>收尾当前段落（换块级标签时调用）。</summary>
        public void Flush()
        {
            _paragraph = null;
            _alignment = TextAlignment.Left;
            _indent = 0;
        }

        /// <summary>当前该把块塞进哪：单元格 → 列表项 → 文档。</summary>
        private void AddBlock(Block block)
        {
            if (_cells.Count > 0)
            {
                _cells.Peek().Blocks.Add(block);
                return;
            }

            if (_lists.Count > 0 && _lists.Peek().ListItems.Count > 0)
            {
                ListItem? last = null;
                foreach (var item in _lists.Peek().ListItems) last = item;
                if (last is not null)
                {
                    last.Blocks.Add(block);
                    return;
                }
            }

            _document.Blocks.Add(block);
        }

        // ----- 标签 -----

        private void HandleOpen(string name, Dictionary<string, string> attrs)
        {
            switch (name)
            {
                case "p":
                case "div":
                case "pre":
                case "section":
                case "article":
                    Flush();
                    ReadAlignment(attrs);
                    _block = BlockKind.None;
                    break;
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                    Flush();
                    ReadAlignment(attrs);
                    _block = name switch
                    {
                        "h1" => BlockKind.Heading1,
                        "h2" => BlockKind.Heading2,
                        "h3" => BlockKind.Heading3,
                        _ => BlockKind.None,
                    };
                    break;
                case "blockquote":
                    Flush();
                    _block = BlockKind.Quote;
                    break;
                case "br":
                    EnsureParagraph();
                    _paragraph!.Inlines.Add(new LineBreak());
                    break;
                case "ul":
                case "ol":
                    Flush();
                    var list = new System.Windows.Documents.List
                    {
                        MarkerStyle = name == "ol" ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(20 + _indent, 0, 0, 4),
                    };
                    AddBlock(list);
                    _lists.Push(list);
                    break;
                case "li":
                    Flush();
                    _block = BlockKind.ListItem;
                    if (_lists.Count > 0) _lists.Peek().ListItems.Add(new ListItem());
                    break;
                case "table":
                    Flush();
                    var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
                    table.RowGroups.Add(new TableRowGroup());
                    AddBlock(table);
                    _tables.Push(table);
                    break;
                case "tr":
                    Flush();
                    if (_tables.Count > 0)
                    {
                        var row = new TableRow();
                        _tables.Peek().RowGroups[0].Rows.Add(row);
                        _rows.Push(row);
                    }
                    break;
                case "td":
                case "th":
                    Flush();
                    if (_rows.Count > 0)
                    {
                        var cell = new TableCell { Padding = new Thickness(6, 4, 6, 4) };
                        if (attrs.TryGetValue("colspan", out var colspan) && int.TryParse(colspan, out var span) && span > 1)
                            cell.ColumnSpan = span;
                        if (name == "th") _rows.Peek().Tag ??= "header";
                        _rows.Peek().Cells.Add(cell);
                        _cells.Push(cell);
                    }
                    break;
                case "img":
                    InsertImage(attrs);
                    break;
                case "a":
                    PushFormat(f => f);                       // 占位：保证闭合时能弹回上一层
                    break;
                case "b":
                case "strong":
                    PushFormat(f => f with { Bold = true });
                    break;
                case "i":
                case "em":
                    PushFormat(f => f with { Italic = true });
                    break;
                case "u":
                    PushFormat(f => f with { Underline = true });
                    break;
                case "s":
                case "strike":
                case "del":
                    PushFormat(f => f with { Strike = true });
                    break;
                case "span":
                case "font":
                case "small":
                case "big":
                case "mark":
                    PushFormat(f => ApplyStyleText(f, attrs));
                    break;
            }
        }

        private void HandleClose(string name)
        {
            switch (name)
            {
                case "p":
                case "div":
                case "pre":
                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                case "blockquote":
                case "li":
                    Flush();
                    _block = BlockKind.None;
                    break;
                case "ul":
                case "ol":
                    Flush();
                    if (_lists.Count > 0) _lists.Pop();
                    break;
                case "table":
                    Flush();
                    if (_tables.Count > 0)
                    {
                        var finished = _tables.Pop();
                        EnsureTableColumns(finished);
                    }
                    break;
                case "tr":
                    Flush();
                    if (_rows.Count > 0) _rows.Pop();
                    break;
                case "td":
                case "th":
                    Flush();
                    if (_cells.Count > 0) _cells.Pop();
                    break;
                case "a":
                case "b":
                case "strong":
                case "i":
                case "em":
                case "u":
                case "s":
                case "strike":
                case "del":
                case "span":
                case "font":
                case "small":
                case "big":
                case "mark":
                    if (_formats.Count > 0) _format = _formats.Pop();
                    break;
            }
        }

        private void PushFormat(Func<FormatState, FormatState> change)
        {
            _formats.Push(_format);
            _format = change(_format);
        }

        /// <summary>
        /// 按最宽的一行补足 <see cref="Table.Columns"/>：不补的话 WPF 只给 1 列宽度，
        /// 多出来的单元格会被挤到看不见（表现出来就是「表格少了一列」）。
        /// </summary>
        private static void EnsureTableColumns(Table table)
        {
            var widest = 0;
            foreach (var group in table.RowGroups)
            {
                foreach (var row in group.Rows)
                {
                    var count = 0;
                    foreach (var cell in row.Cells) count += Math.Max(1, cell.ColumnSpan);
                    widest = Math.Max(widest, count);
                }
            }

            while (table.Columns.Count < Math.Max(1, widest))
                table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        }

        private void ReadAlignment(Dictionary<string, string> attrs)
        {
            _alignment = TextAlignment.Left;
            _indent = 0;
            if (!attrs.TryGetValue("style", out var style) || style is null) return;
            foreach (var pair in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = pair.IndexOf(':');
                if (colon <= 0) continue;
                var key = pair[..colon].Trim().ToLowerInvariant();
                var value = pair[(colon + 1)..].Trim();
                if (key == "text-align")
                {
                    _alignment = value.ToLowerInvariant() switch
                    {
                        "center" => TextAlignment.Center,
                        "right" => TextAlignment.Right,
                        "justify" => TextAlignment.Justify,
                        _ => TextAlignment.Left,
                    };
                }
                else if (key == "margin-left" && double.TryParse(value.TrimEnd('p', 'x'), out var indent))
                {
                    _indent = Math.Clamp(indent, 0, 400);
                }
            }
        }

        private static FormatState ApplyStyleText(FormatState format, Dictionary<string, string> attrs)
        {
            if (attrs.TryGetValue("color", out var attrColor) && TryParseColor(attrColor, out var color))
                format = format with { Foreground = color };
            if (attrs.TryGetValue("face", out var face) && !string.IsNullOrWhiteSpace(face))
                format = format with { FontFamily = face };
            if (!attrs.TryGetValue("style", out var style) || style is null) return format;

            foreach (var pair in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = pair.IndexOf(':');
                if (colon <= 0) continue;
                var key = pair[..colon].Trim().ToLowerInvariant();
                var value = pair[(colon + 1)..].Trim();
                switch (key)
                {
                    case "color" when TryParseColor(value, out var fg):
                        format = format with { Foreground = fg };
                        break;
                    case "background-color" when TryParseColor(value, out var bg):
                        format = format with { Background = bg };
                        break;
                    case "font-size" when TryParseSize(value, out var size):
                        format = format with { FontSize = size };
                        break;
                    case "font-family":
                        format = format with { FontFamily = value.Split(',')[0].Trim().Trim('"', '\'') };
                        break;
                    case "font-weight":
                        format = format with
                        {
                            Bold = value.Equals("bold", StringComparison.OrdinalIgnoreCase)
                                   || (int.TryParse(value, out var weight) && weight >= 600),
                        };
                        break;
                    case "font-style":
                        format = format with { Italic = value.Equals("italic", StringComparison.OrdinalIgnoreCase) };
                        break;
                    case "text-decoration":
                    case "text-decoration-line":
                        if (value.Contains("underline", StringComparison.OrdinalIgnoreCase)) format = format with { Underline = true };
                        if (value.Contains("line-through", StringComparison.OrdinalIgnoreCase)) format = format with { Strike = true };
                        break;
                }
            }
            return format;
        }

        private void InsertImage(Dictionary<string, string> attrs)
        {
            if (!attrs.TryGetValue("src", out var src) || string.IsNullOrWhiteSpace(src)) return;

            var image = new Image
            {
                Tag = src,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = MaxImageWidth,
            };
            if (attrs.TryGetValue("width", out var widthText)
                && double.TryParse(widthText.TrimEnd('p', 'x'), out var width) && width > 0)
            {
                image.Width = Math.Min(width, MaxImageWidth);
            }

            if (TryLoadBitmap(src, out var bitmap))
            {
                image.Source = bitmap;
                if (double.IsNaN(image.Width) || image.Width <= 0)
                    image.Width = Math.Min(bitmap.PixelWidth, MaxImageWidth);
            }
            else
            {
                image.Width = 240;
                image.Height = 46;
                image.ToolTip = src;
            }

            EnsureParagraph();
            _paragraph!.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }

        private static void ApplyFormat(TextElement element, FormatState format)
        {
            element.FontWeight = format.Bold ? FontWeights.Bold : FontWeights.Normal;
            element.FontStyle = format.Italic ? FontStyles.Italic : FontStyles.Normal;
            if (element is Inline inline && (format.Underline || format.Strike))
            {
                var decorations = new TextDecorationCollection();
                if (format.Underline) decorations.Add(CreateDecoration(TextDecorationLocation.Underline));
                if (format.Strike) decorations.Add(CreateDecoration(TextDecorationLocation.Strikethrough));
                inline.TextDecorations = decorations;
            }
            if (format.Foreground is { } fg) element.Foreground = new SolidColorBrush(fg);
            if (format.Background is { } bg) element.Background = new SolidColorBrush(bg);
            if (format.FontFamily is { Length: > 0 } family) element.FontFamily = new FontFamily(family);
            if (format.FontSize is { } size && size > 0) element.FontSize = size;
        }
    }

    // ================================================================ 图片 / 颜色 / 单位

    /// <summary>
    /// 载入图片：只认 <c>data:</c> / 本机文件；<c>cid:</c> 与 http(s) 远程图片不联网，
    /// 返回 false 让调用方画占位块（往返时 <c>src</c> 原样保留，不会丢）。
    /// </summary>
    public static bool TryLoadBitmap(string src, out BitmapImage bitmap)
    {
        bitmap = new BitmapImage();
        try
        {
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = src.IndexOf(',');
                if (comma < 0) return false;
                var bytes = Convert.FromBase64String(Uri.UnescapeDataString(src[(comma + 1)..]));
                using var stream = new MemoryStream(bytes);
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return true;
            }

            if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                || src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!File.Exists(src)) return false;
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(src, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把本机图片转成 <c>data:</c> 内联地址（预览 HTML 用；发送时用 <c>cid:</c> 那一套）。</summary>
    public static string ToDataUri(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return filePath;
            var bytes = File.ReadAllBytes(filePath);
            return $"data:{MailAttachmentItem.ContentTypeOf(filePath)};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return filePath;
        }
    }

    private static Dictionary<string, string> ParseAttributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (Match match in AttributePattern.Matches(text))
        {
            result[match.Groups["name"].Value] = match.Groups["value"].Value;
        }
        return result;
    }

    private static bool TryParseSize(string value, out double size)
    {
        size = 0;
        var text = value.Trim().ToLowerInvariant();
        if (text.EndsWith("px", StringComparison.Ordinal))
        {
            text = text[..^2];
        }
        else if (text.EndsWith("pt", StringComparison.Ordinal))
        {
            if (!double.TryParse(text[..^2], out var points)) return false;
            size = Math.Clamp(Math.Round(points * 96 / 72), 8, 72);
            return true;
        }
        else if (text.EndsWith("em", StringComparison.Ordinal))
        {
            if (!double.TryParse(text[..^2], out var em)) return false;
            size = Math.Clamp(Math.Round(DefaultFontSize * em), 8, 72);
            return true;
        }

        if (!double.TryParse(text, out var px)) return false;
        size = Math.Clamp(px, 8, 72);
        return true;
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Colors.Black;
        var text = (value ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return false;

        if (text.StartsWith("rgb", StringComparison.Ordinal))
        {
            var numbers = Regex.Matches(text, @"\d+").Select(m => int.Parse(m.Value)).ToArray();
            if (numbers.Length < 3) return false;
            color = Color.FromRgb(
                (byte)Math.Clamp(numbers[0], 0, 255),
                (byte)Math.Clamp(numbers[1], 0, 255),
                (byte)Math.Clamp(numbers[2], 0, 255));
            return true;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(text)!;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>颜色 → <c>#RRGGBB</c>（调色板与工具栏的高亮显示共用）。</summary>
    public static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
