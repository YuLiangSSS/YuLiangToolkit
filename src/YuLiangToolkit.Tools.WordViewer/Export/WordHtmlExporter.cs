using System.Globalization;
using YuLiangToolkit.Tools.WordViewer.Parsing;

namespace YuLiangToolkit.Tools.WordViewer.Export;

/// <summary>HTML 导出结果：文件字节数、因为体积上限省略的图片数、正文是否有内容（空文档提示用）。</summary>
/// <param name="ByteCount">写出的 HTML 字节数（UTF-8）。</param>
/// <param name="OmittedImageCount">超过总体积上限、导出成占位文字的图片数。</param>
/// <param name="HasBodyContent">正文是否有内容块（空文档导出的是提示页）。</param>
public sealed record WordHtmlExportResult(int ByteCount, int OmittedImageCount, bool HasBodyContent);

/// <summary>
/// 把解析好的 <see cref="WordDocument"/> 导出成**自包含的单文件 HTML**（纯逻辑，不含任何 WPF 类型）。
///
/// <para><b>覆盖范围</b>：标题 <c>h1 ~ h6</c>、段落（对齐 / 缩进 / 列表标记）、
/// run 的内联格式（<c>&lt;b&gt; / &lt;i&gt; / &lt;u&gt; / &lt;s&gt;</c>、颜色 / 字号 / 字体 / 高亮、上标下标）、
/// 超链接 <c>&lt;a&gt;</c>、表格（<c>colspan / rowspan</c>、底纹、表头 <c>&lt;th&gt;</c>）、
/// 图片（base64 data URI，总大小超过 <see cref="DefaultMaxImageBytes"/> 时退回占位文字），
/// 以及页眉 / 页脚 / 脚注 / 尾注的简单区块。</para>
///
/// <para><b>安全</b>：所有用户文字 / 属性都经过 HTML 转义（<c>&amp; &lt; &gt; " '</c>），
/// 不会把文档内容当成 HTML 标签执行；超链接只输出 <c>http/https/mailto</c> 协议，
/// 其它协议（例如 <c>javascript:</c>）只留纯文本；控制字符会被丢掉。</para>
///
/// <para><b>和界面的口径一致</b>：修订里「删除」的文字不导出（查找 / 复制全文也跳过它），
/// 修订「插入」的文字按普通文字导出；批注不会出现在 HTML 里（它属于阅读时的辅助信息）。</para>
/// </summary>
public static class WordHtmlExporter
{
    /// <summary>图片 data URI 的总体积上限（20 MB）：超过后剩余的图片显示成占位文字，避免导出一个巨大的 HTML。</summary>
    public const long DefaultMaxImageBytes = 20L * 1024 * 1024;

    /// <summary>图片 MIME 认不出来时的回退值。</summary>
    private const string FallbackImageMime = "image/png";

    private const string HtmlHead = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        """;

    private const string HtmlStyle = """
        <style>
        :root { color-scheme: light; }
        body { margin: 0; padding: 28px 20px; background: #ffffff; color: #1f2328;
               font-family: "Microsoft YaHei UI", "Segoe UI", Arial, sans-serif; font-size: 16px; line-height: 1.75; }
        .wrap { max-width: 920px; margin: 0 auto; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.3; margin: 1.1em 0 0.45em; }
        h1 { font-size: 2em; } h2 { font-size: 1.6em; } h3 { font-size: 1.35em; }
        h4 { font-size: 1.18em; } h5 { font-size: 1.08em; } h6 { font-size: 1em; }
        p { margin: 0.35em 0; }
        table { border-collapse: collapse; margin: 0.7em 0; max-width: 100%; }
        th, td { border: 1px solid #d0d0d0; padding: 5px 9px; vertical-align: top; text-align: left; }
        th { background: #f2f2f2; font-weight: 600; }
        figure.image { margin: 0.7em 0; text-align: center; }
        img { max-width: 100%; height: auto; }
        .header, .footer { color: #787878; font-size: 13px; }
        .header { border-bottom: 1px solid #d6d6d6; padding-bottom: 8px; margin-bottom: 18px; }
        .footer { border-top: 1px solid #d6d6d6; padding-top: 8px; margin-top: 26px; }
        .notes { border-top: 1px solid #d6d6d6; margin-top: 26px; padding-top: 8px; font-size: 14px; color: #444; }
        .notes h2 { font-size: 15px; color: #666; margin: 0.8em 0 0.3em; }
        .note-number, .note-ref { color: #1a66cc; }
        .list-marker { margin-right: 0.35em; }
        .image-placeholder, .empty { color: #888888; font-size: 14px; }
        sup, sub { font-size: 0.75em; }
        </style>
        """;

    /// <summary>
    /// 从模型生成完整 HTML 文本（自包含、UTF-8、不写文件）。
    /// <paramref name="maxImageBytes"/> 是图片 data URI 的总体积上限（自检可以传很小值验证占位逻辑）。
    /// </summary>
    public static string BuildHtml(WordDocument document, long maxImageBytes = DefaultMaxImageBytes)
        => BuildHtml(document, maxImageBytes, out _);

    /// <summary>同 <see cref="BuildHtml(WordDocument, long)"/>，并带回被省略的图片数。</summary>
    public static string BuildHtml(WordDocument document, long maxImageBytes, out int omittedImageCount)
    {
        ArgumentNullException.ThrowIfNull(document);

        var state = new ExportState(document, maxImageBytes > 0 ? maxImageBytes : DefaultMaxImageBytes);
        state.AppendDocument();
        omittedImageCount = state.OmittedImageCount;
        return state.Build();
    }

    /// <summary>
    /// 生成 HTML 并写进文件（UTF-8 无 BOM）。写盘异常（路径非法 / 没权限 / 磁盘满）直接抛给调用方，
    /// 由界面转成状态栏提示；不会动被浏览的 docx。
    /// </summary>
    public static WordHtmlExportResult WriteToFile(
        WordDocument document, string path, long maxImageBytes = DefaultMaxImageBytes)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("导出路径为空", nameof(path));

        var html = BuildHtml(document, maxImageBytes, out var omitted);
        File.WriteAllText(path, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return new WordHtmlExportResult(
            Encoding.UTF8.GetByteCount(html),
            omitted,
            document.Blocks.Count > 0);
    }

    // ================================================================ 导出状态

    /// <summary>一次导出的临时状态（HTML 构造器 + 图片体积计数）。</summary>
    private sealed class ExportState
    {
        private readonly WordDocument _document;
        private readonly StringBuilder _builder = new(8192);
        private readonly long _maxImageBytes;
        private long _usedImageBytes;

        public ExportState(WordDocument document, long maxImageBytes)
        {
            _document = document;
            _maxImageBytes = maxImageBytes;
        }

        /// <summary>因为超过体积上限而没导出的图片数。</summary>
        public int OmittedImageCount { get; private set; }

        public string Build() => _builder.ToString();

        public void AppendDocument()
        {
            _builder.Append(HtmlHead);
            _builder.Append("<title>").Append(EscapeText(GetTitle())).Append("</title>\n");
            _builder.Append(HtmlStyle);
            _builder.Append("</head>\n<body>\n<div class=\"wrap\">\n");

            if (_document.HeaderBlocks.Count > 0)
                AppendHeaderFooter("header", _document.HeaderBlocks);

            if (_document.Blocks.Count == 0)
                _builder.Append("<p class=\"empty\">（这个文档没有可显示的正文内容）</p>\n");
            else
                AppendBlocks(_document.Blocks);

            if (_document.Footnotes.Count > 0) AppendNotes("脚注", _document.Footnotes);
            if (_document.Endnotes.Count > 0) AppendNotes("尾注", _document.Endnotes);

            if (_document.FooterBlocks.Count > 0)
                AppendHeaderFooter("footer", _document.FooterBlocks);

            _builder.Append("</div>\n</body>\n</html>\n");
        }

        private string GetTitle()
        {
            if (!string.IsNullOrWhiteSpace(_document.Title)) return _document.Title.Trim();
            if (!string.IsNullOrWhiteSpace(_document.SourcePath))
            {
                try { return Path.GetFileNameWithoutExtension(_document.SourcePath); }
                catch { /* 非法路径：用兜底标题 */ }
            }

            return "Word 文档";
        }

        // ---------------------------------------------------------------- 块

        private void AppendBlocks(IReadOnlyList<WordBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case WordParagraphBlock paragraph:
                        AppendParagraph(paragraph, inCell: false);
                        break;

                    case WordTableBlock table:
                        AppendTable(table);
                        break;

                    case WordImageBlock image:
                        AppendImage(image, blockLevel: true);
                        break;
                }
            }
        }

        private void AppendParagraph(
            WordParagraphBlock block, bool inCell, string? prefixHtml = null, bool plain = false)
        {
            var headingLevel = !inCell && !plain ? Math.Clamp(block.Style.HeadingLevel, 0, 6) : 0;
            var tag = headingLevel > 0 ? $"h{headingLevel}" : "p";

            _builder.Append('<').Append(tag);
            if (inCell) _builder.Append(" class=\"cell-paragraph\"");
            var style = BuildParagraphStyle(block);
            if (style.Length > 0) _builder.Append(" style=\"").Append(style).Append('"');
            _builder.Append('>');

            if (!string.IsNullOrEmpty(prefixHtml)) _builder.Append(prefixHtml);
            if (!string.IsNullOrEmpty(block.Style.ListMarker))
            {
                _builder.Append("<span class=\"list-marker\">")
                    .Append(EscapeText(block.Style.ListMarker))
                    .Append("</span>");
            }

            AppendRuns(block);
            _builder.Append("</").Append(tag).Append(">\n");
        }

        private static string BuildParagraphStyle(WordParagraphBlock block)
        {
            var styles = new List<string>(3);

            var alignment = block.Style.Alignment switch
            {
                WordTextAlignment.Center => "center",
                WordTextAlignment.Right => "right",
                WordTextAlignment.Justify => "justify",
                _ => "",
            };
            if (alignment.Length > 0) styles.Add("text-align:" + alignment);

            var indent = block.Style.LeftIndentDip;
            if (!string.IsNullOrEmpty(block.Style.ListMarker))
                indent = Math.Max(indent, (block.Style.IndentLevel + 1) * 24);
            if (indent > 0.5) styles.Add("margin-left:" + indent.ToString("0.#", CultureInfo.InvariantCulture) + "px");

            return string.Join(';', styles);
        }

        // ---------------------------------------------------------------- 行内

        private void AppendRuns(WordParagraphBlock block)
        {
            foreach (var run in block.Runs)
            {
                // 修订里「删除」的文字不导出（和查找 / 复制全文口径一致）；「插入」按普通文字导出
                if (run.Revision == WordRevisionKind.Deleted) continue;
                AppendRun(run);
            }
        }

        private void AppendRun(WordRun run)
        {
            var text = run.Text ?? "";
            var noteNumber = run.FootnoteNumber ?? run.EndnoteNumber;
            if (text.Length == 0 && noteNumber is null && !run.IsHyperlink) return;

            var closers = new List<string>(8);

            // 超链接：只输出安全协议；不安全（javascript: / data: 等）只留纯文本
            var href = SafeHref(run.Url);
            if (run.IsHyperlink && href.Length > 0)
            {
                _builder.Append("<a href=\"").Append(EscapeAttribute(href))
                    .Append("\" rel=\"noopener noreferrer\">");
                closers.Add("</a>");
            }

            var css = BuildRunCss(run);
            if (css.Length > 0)
            {
                _builder.Append("<span style=\"").Append(css).Append("\">");
                closers.Add("</span>");
            }

            if (run.Bold) { _builder.Append("<b>"); closers.Add("</b>"); }
            if (run.Italic) { _builder.Append("<i>"); closers.Add("</i>"); }
            if (run.Underline) { _builder.Append("<u>"); closers.Add("</u>"); }
            if (run.Strike) { _builder.Append("<s>"); closers.Add("</s>"); }
            if (run.IsSuperscript) { _builder.Append("<sup>"); closers.Add("</sup>"); }
            else if (run.IsSubscript) { _builder.Append("<sub>"); closers.Add("</sub>"); }

            if (text.Length > 0) _builder.Append(EscapeText(text));

            // 脚注 / 尾注引用：上标 [n]（和屏幕上的标记一致）
            if (noteNumber is not null)
            {
                _builder.Append("<sup class=\"note-ref\">[")
                    .Append(noteNumber.Value.ToString(CultureInfo.InvariantCulture))
                    .Append("]</sup>");
            }

            for (var index = closers.Count - 1; index >= 0; index--) _builder.Append(closers[index]);
        }

        /// <summary>run 的内联 CSS：颜色 / 字号 / 字体 / 高亮；非法值直接忽略（不让用户文本进 CSS 语法位置）。</summary>
        private static string BuildRunCss(WordRun run)
        {
            var styles = new List<string>(4);

            if (IsHexColor(run.ColorHex)) styles.Add("color:" + run.ColorHex);
            if (run.FontSizePt is > 0 and < 500)
                styles.Add("font-size:" + run.FontSizePt.Value.ToString("0.##", CultureInfo.InvariantCulture) + "pt");
            var fontFamily = SanitizeFontFamily(run.FontFamily);
            if (fontFamily.Length > 0) styles.Add("font-family:" + fontFamily);
            if (IsHexColor(run.HighlightHex)) styles.Add("background-color:" + run.HighlightHex);

            return string.Join(';', styles);
        }

        private static string SafeHref(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return "";
            return uri.Scheme switch
            {
                "http" or "https" or "mailto" => uri.AbsoluteUri,
                _ => "",
            };
        }

        // ---------------------------------------------------------------- 表格

        private void AppendTable(WordTableBlock table)
        {
            _builder.Append("<table>\n<tbody>\n");

            foreach (var row in table.Rows)
            {
                _builder.Append("<tr>");
                var cellTag = row.IsHeader ? "th" : "td";

                foreach (var cell in row.Cells)
                {
                    // 被纵向合并盖住的占位格不导出，否则表格会多出空格子
                    if (cell.IsMergedContinuation) continue;

                    _builder.Append('<').Append(cellTag);
                    if (cell.ColumnSpan > 1)
                        _builder.Append(" colspan=\"").Append(cell.ColumnSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                    if (cell.RowSpan > 1)
                        _builder.Append(" rowspan=\"").Append(cell.RowSpan.ToString(CultureInfo.InvariantCulture)).Append('"');
                    if (IsHexColor(cell.ShadingHex))
                        _builder.Append(" style=\"background-color:").Append(cell.ShadingHex).Append('"');
                    _builder.Append('>');

                    if (cell.Blocks.Count == 0)
                    {
                        _builder.Append("&nbsp;");
                    }
                    else
                    {
                        foreach (var child in cell.Blocks) AppendCellBlock(child);
                    }

                    _builder.Append("</").Append(cellTag).Append('>');
                }

                _builder.Append("</tr>\n");
            }

            _builder.Append("</tbody>\n</table>\n");
        }

        private void AppendCellBlock(WordBlock block)
        {
            switch (block)
            {
                case WordParagraphBlock paragraph:
                    AppendParagraph(paragraph, inCell: true);
                    break;

                case WordTableBlock nested:
                    AppendTable(nested);
                    break;

                case WordImageBlock image:
                    AppendImage(image, blockLevel: false);
                    break;
            }
        }

        // ---------------------------------------------------------------- 图片

        private void AppendImage(WordImageBlock image, bool blockLevel)
        {
            if (image.Data.Length == 0) return;

            // 图片总量上限：超出后不 base64（避免导出几百 MB 的 HTML），用占位文字说明
            if (_usedImageBytes + image.Data.Length > _maxImageBytes)
            {
                OmittedImageCount++;
                if (blockLevel)
                {
                    _builder.Append("<p class=\"image-placeholder\">（图片过大，已省略）</p>\n");
                }
                else
                {
                    _builder.Append("<span class=\"image-placeholder\">（图片过大，已省略）</span>");
                }

                return;
            }

            _usedImageBytes += image.Data.Length;
            var mime = NormalizeImageMime(image.ContentType);
            var base64 = Convert.ToBase64String(image.Data);

            var sizeStyle = "";
            if (image.WidthDip > 0.5 || image.HeightDip > 0.5)
            {
                var parts = new List<string>(2);
                if (image.WidthDip > 0.5)
                    parts.Add("width:" + image.WidthDip.ToString("0.#", CultureInfo.InvariantCulture) + "px");
                if (image.HeightDip > 0.5)
                    parts.Add("height:" + image.HeightDip.ToString("0.#", CultureInfo.InvariantCulture) + "px");
                sizeStyle = " style=\"" + string.Join(';', parts) + "\"";
            }

            var alt = EscapeAttribute(image.AltText ?? "");
            if (blockLevel)
            {
                _builder.Append("<figure class=\"image\"><img alt=\"").Append(alt)
                    .Append("\" src=\"data:").Append(mime).Append(";base64,").Append(base64).Append('"')
                    .Append(sizeStyle).Append("></figure>\n");
            }
            else
            {
                _builder.Append("<img alt=\"").Append(alt)
                    .Append("\" src=\"data:").Append(mime).Append(";base64,").Append(base64).Append('"')
                    .Append(sizeStyle).Append('>');
            }
        }

        private static string NormalizeImageMime(string? contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType)) return FallbackImageMime;
            var mime = contentType.Trim().ToLowerInvariant();
            if (!mime.StartsWith("image/", StringComparison.Ordinal)) return FallbackImageMime;
            foreach (var ch in mime)
            {
                if (!(char.IsAsciiLetterOrDigit(ch) || ch is '/' or '.' or '+' or '-')) return FallbackImageMime;
            }

            return mime;
        }

        // ---------------------------------------------------------------- 页眉页脚 / 脚注尾注

        private void AppendHeaderFooter(string cssClass, IReadOnlyList<WordBlock> blocks)
        {
            _builder.Append("<div class=\"").Append(cssClass).Append("\">\n");
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case WordParagraphBlock paragraph:
                        AppendParagraph(paragraph, inCell: false, plain: true);
                        break;

                    case WordTableBlock table:
                        AppendTable(table);
                        break;

                    case WordImageBlock image:
                        AppendImage(image, blockLevel: true);
                        break;
                }
            }

            _builder.Append("</div>\n");
        }

        private void AppendNotes(string title, IReadOnlyList<WordNote> notes)
        {
            _builder.Append("<section class=\"notes\">\n<h2>").Append(EscapeText(title)).Append("</h2>\n");

            foreach (var note in notes)
            {
                _builder.Append("<div class=\"note\">\n");
                var prefixUsed = false;

                foreach (var block in note.Blocks)
                {
                    switch (block)
                    {
                        case WordParagraphBlock paragraph:
                        {
                            var prefix = prefixUsed
                                ? null
                                : $"<span class=\"note-number\">[{note.Number}]</span> ";
                            AppendParagraph(paragraph, inCell: false, prefix, plain: true);
                            prefixUsed = true;
                            break;
                        }

                        case WordTableBlock table:
                        {
                            if (!prefixUsed)
                            {
                                _builder.Append("<p><span class=\"note-number\">[")
                                    .Append(note.Number.ToString(CultureInfo.InvariantCulture))
                                    .Append("]</span></p>\n");
                                prefixUsed = true;
                            }

                            AppendTable(table);
                            break;
                        }
                    }
                }

                _builder.Append("</div>\n");
            }

            _builder.Append("</section>\n");
        }
    }

    // ================================================================ 转义 / 取值小工具

    /// <summary>
    /// HTML 文字转义：<c>&amp; &lt; &gt; " '</c> 全部转成实体，并丢掉 HTML 里非法的控制字符
    /// （保留 <c>\t / \n / \r</c>）。用户文字永远不会被当成标签 / 属性注入。
    /// </summary>
    public static string EscapeText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var builder = new StringBuilder(text.Length + 16);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&#39;"); break;
                default:
                    // 非法控制字符（\u0000~\u001F 除了 \t \n \r）：直接丢掉，避免生成坏 HTML / XML
                    if (char.IsControl(ch) && ch is not ('\t' or '\n' or '\r')) continue;
                    builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>属性值转义（规则同 <see cref="EscapeText"/>）。</summary>
    public static string EscapeAttribute(string? text) => EscapeText(text);

    /// <summary><c>#RRGGBB</c> 校验：只允许合法的 6 位十六进制颜色进 CSS。</summary>
    private static bool IsHexColor(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length != 7 || text[0] != '#') return false;
        for (var index = 1; index < text.Length; index++)
        {
            if (!Uri.IsHexDigit(text[index])) return false;
        }

        return true;
    }

    /// <summary>
    /// 字体名进 CSS 前的过滤：只留字母 / 数字 / 空格 / 连字符 / 下划线 / 中文字符，
    /// 去掉引号、分号、尖括号等可能破坏 CSS 或 HTML 的字符。
    /// </summary>
    private static string SanitizeFontFamily(string? fontFamily)
    {
        if (string.IsNullOrWhiteSpace(fontFamily)) return "";
        var builder = new StringBuilder(fontFamily.Length);
        foreach (var ch in fontFamily.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_') builder.Append(ch);
        }

        // 字体名里有空格时用引号包起来（这里只拼安全字符，不会破坏属性）
        var name = builder.ToString().Trim();
        if (name.Length == 0) return "";
        return name.Contains(' ') ? "'" + name + "'" : name;
    }
}
