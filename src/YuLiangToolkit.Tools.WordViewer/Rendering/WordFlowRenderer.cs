using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Tools.WordViewer.Parsing;

namespace YuLiangToolkit.Tools.WordViewer.Rendering;

/// <summary>
/// 渲染选项。目前只有「页眉页脚」一个开关（工具条上可切换，切换后从已解析模型重新排版、不重新读文件）。
/// </summary>
public sealed class WordRenderOptions
{
    /// <summary>是否渲染页眉 / 页脚（默认渲染）。关掉时正文、脚注照常显示。</summary>
    public bool ShowHeaderFooter { get; set; } = true;
}

/// <summary>渲染结果：<see cref="Document"/> 交给 <c>FlowDocumentScrollViewer</c>，映射表给查找 / 大纲跳转用。</summary>
/// <param name="Document">排好版的 FlowDocument（白色纸面）。</param>
/// <param name="ParagraphByBlockIndex">
/// 「模型块下标 → 渲染出来的段落」；表格 / 图片块没有单独的段落，对应项是 <c>null</c>。
/// </param>
public sealed record WordRenderResult(FlowDocument Document, IReadOnlyList<Paragraph?> ParagraphByBlockIndex)
{
    /// <summary>
    /// 「模型块下标 → 渲染出来的 <see cref="Block"/>」：段落 / 表格 / 图片都有映射。
    /// 查找命中表格时没有段落可高亮，就用它把整个表格滚进视野。
    /// </summary>
    public IReadOnlyList<Block?> BlockByBlockIndex { get; init; } = Array.Empty<Block?>();

    /// <summary>本次是否渲染了页眉 / 页脚（自检与状态栏提示用）。</summary>
    public bool HasHeaderFooter { get; init; }

    /// <summary>本次渲染的脚注 / 尾注条数（没有脚注小节时为 0）。</summary>
    public int RenderedNoteCount { get; init; }
}

/// <summary>
/// 把 <see cref="WordDocument"/> 模型渲染成 WPF 的 <see cref="FlowDocument"/>。
///
/// <para><b>为什么要单独一层</b>：解析器只产出纯数据（后台线程可跑），渲染必须在 UI 线程且只碰 WPF 类型；
/// 分开后自检可以直接断言解析结果，界面渲染也集中在同一个文件里调样式。</para>
///
/// <para><b>颜色说明</b>：纸面白底 / 近黑正文 / 表格灰线 / 链接蓝属于「文档纸张」的渲染，
/// 不跟外壳的深色主题走，所以在代码里写死；页面外框、工具条、大纲栏仍然全部用主题 token。</para>
/// </summary>
public static class WordFlowRenderer
{
    /// <summary>正文默认字体（Windows 10/11 都有，不用额外装字体）。</summary>
    public const string DefaultFontFamily = "Microsoft YaHei UI";

    /// <summary>正文默认字号（磅）。</summary>
    public const double BodyFontSizePt = 11;

    /// <summary>页眉 / 页脚字号（磅）：比正文小一圈，像「版面附属内容」。</summary>
    private const double HeaderFooterFontSizePt = 9;

    /// <summary>脚注 / 尾注正文字号（磅）。</summary>
    private const double NoteFontSizePt = 9;

    /// <summary>「脚注」「尾注」小节标题字号（磅）。</summary>
    private const double NoteTitleFontSizePt = 10;

    /// <summary>纸面内边距（左右 / 上下，DIP）。</summary>
    private const double PagePaddingHorizontal = 56;
    private const double PagePaddingVertical = 48;

    /// <summary>图片最大显示尺寸（超出等比缩小，避免一张大图把滚动条拉到很远）。</summary>
    private const double MaxImageWidth = 760;
    private const double MaxImageHeight = 1000;

    /// <summary>标题 1 ~ 6 级的字号（磅）：逐级递减，正文 11 磅收尾。</summary>
    private static readonly double[] HeadingFontSizesPt = { 22, 18, 15.5, 13.5, 12.5, 11.5 };

    // 文档纸面用色（不随主题变化）：白纸、近黑字、浅灰网格线、表头浅灰底、链接蓝、空文档提示灰、
    // 页眉页脚 / 脚注的浅灰字与分隔线、脚注引用蓝
    private static readonly Brush InkBrush = CreateFrozenBrush(0x1F, 0x23, 0x28);
    private static readonly Brush GridLineBrush = CreateFrozenBrush(0xD0, 0xD0, 0xD0);
    private static readonly Brush TableHeaderBrush = CreateFrozenBrush(0xF2, 0xF2, 0xF2);
    private static readonly Brush LinkBrush = CreateFrozenBrush(0x1A, 0x66, 0xCC);
    private static readonly Brush HintBrush = CreateFrozenBrush(0x88, 0x88, 0x88);
    private static readonly Brush HeaderFooterBrush = CreateFrozenBrush(0x78, 0x78, 0x78);
    private static readonly Brush SeparatorBrush = CreateFrozenBrush(0xD6, 0xD6, 0xD6);
    private static readonly Brush NoteTextBrush = CreateFrozenBrush(0x44, 0x44, 0x44);
    private static readonly Brush NoteTitleBrush = CreateFrozenBrush(0x66, 0x66, 0x66);
    private static readonly Brush NoteReferenceBrush = CreateFrozenBrush(0x1A, 0x66, 0xCC);

    /// <summary>
    /// 把整篇文档渲染成 <see cref="FlowDocument"/>（默认显示页眉页脚）。<b>必须在 UI 线程调用</b>（要在里面建 WPF 对象）。
    /// <para>保留这个单参重载：老调用方（自检 / 界面）不用改。</para>
    /// </summary>
    public static WordRenderResult Build(WordDocument document) => Build(document, new WordRenderOptions());

    /// <summary>
    /// 按 <paramref name="options"/> 渲染：正文 → 脚注 / 尾注小节 → 页脚，页眉在最顶部。
    /// <para>页眉页脚开关只影响排版，不会重新解析文件（见 <c>WordViewerViewModel.ShowHeaderFooter</c>）。</para>
    /// </summary>
    public static WordRenderResult Build(WordDocument document, WordRenderOptions? options)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new WordRenderOptions();

        var flow = new FlowDocument
        {
            Background = Brushes.White,
            Foreground = InkBrush,
            FontFamily = new FontFamily(DefaultFontFamily),
            FontSize = OoxmlPackage.PointToDip(BodyFontSizePt),
            PagePadding = new Thickness(PagePaddingHorizontal, PagePaddingVertical,
                PagePaddingHorizontal, PagePaddingVertical),

            // 不分栏：阅读器是单栏长文档，ColumnWidth 设成无穷大让内容占满纸面宽度
            ColumnWidth = double.PositiveInfinity,
        };

        var paragraphByBlockIndex = new List<Paragraph?>(document.Blocks.Count);
        var blockByBlockIndex = new List<Block?>(document.Blocks.Count);
        var hasHeaderFooter = false;

        // 页眉：连续浏览的简化 —— 只在文档顶部显示一次（取首个节的页眉），不逐页重复
        if (options.ShowHeaderFooter && document.HeaderBlocks.Count > 0)
        {
            AppendHeaderFooterBlocks(flow, document.HeaderBlocks);
            AppendHeaderFooterDivider(flow, isHeader: true);
            hasHeaderFooter = true;
        }

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case WordParagraphBlock paragraphBlock:
                {
                    var paragraph = BuildParagraph(paragraphBlock, inTable: false, forceBold: false);
                    paragraphByBlockIndex.Add(paragraph);
                    blockByBlockIndex.Add(paragraph);
                    flow.Blocks.Add(paragraph);
                    break;
                }

                case WordTableBlock tableBlock:
                {
                    var table = BuildTable(tableBlock);
                    paragraphByBlockIndex.Add(null);
                    blockByBlockIndex.Add(table);
                    flow.Blocks.Add(table);
                    break;
                }

                case WordImageBlock imageBlock:
                {
                    var container = BuildImage(imageBlock);
                    paragraphByBlockIndex.Add(null);
                    blockByBlockIndex.Add(container);
                    flow.Blocks.Add(container);
                    break;
                }

                default:
                    paragraphByBlockIndex.Add(null);
                    blockByBlockIndex.Add(null);
                    break;
            }
        }

        // 脚注 / 尾注小节：放在正文之后、页脚之前（内容不多，但用户需要能看到引用解释）
        var renderedNoteCount = 0;
        if (document.Footnotes.Count > 0)
        {
            AppendNotesSection(flow, "脚注", document.Footnotes);
            renderedNoteCount += document.Footnotes.Count;
        }

        if (document.Endnotes.Count > 0)
        {
            AppendNotesSection(flow, "尾注", document.Endnotes);
            renderedNoteCount += document.Endnotes.Count;
        }

        // 页脚：连续浏览的简化 —— 只在文档底部显示一次（取最后一个节的页脚）
        if (options.ShowHeaderFooter && document.FooterBlocks.Count > 0)
        {
            AppendHeaderFooterDivider(flow, isHeader: false);
            AppendHeaderFooterBlocks(flow, document.FooterBlocks);
            hasHeaderFooter = true;
        }

        if (flow.Blocks.Count == 0) flow.Blocks.Add(BuildEmptyHint());

        return new WordRenderResult(flow, paragraphByBlockIndex)
        {
            BlockByBlockIndex = blockByBlockIndex,
            HasHeaderFooter = hasHeaderFooter,
            RenderedNoteCount = renderedNoteCount,
        };
    }

    // ---------------------------------------------------------------- 段落

    /// <summary>
    /// 把一个段落模型渲染成 <see cref="Paragraph"/>。
    /// <para><paramref name="baseFontSizePt"/> / <paramref name="foreground"/> / <paramref name="margin"/>
    /// 是给页眉页脚、脚注这类「附属内容」用的覆盖值：给了它们就不再套标题样式，
    /// 统一用浅灰小字，和正文一眼能分开。</para>
    /// </summary>
    private static Paragraph BuildParagraph(
        WordParagraphBlock block, bool inTable, bool forceBold,
        double? baseFontSizePt = null, Brush? foreground = null, Thickness? margin = null)
    {
        var paragraph = new Paragraph();
        var style = block.Style;

        // 标题：级别越高字号越大 + 粗体 + 明显的段前段后距
        var headingLevel = inTable || baseFontSizePt.HasValue ? 0 : Math.Clamp(style.HeadingLevel, 0, 6);
        double topMargin;
        double bottomMargin;

        if (headingLevel > 0)
        {
            paragraph.FontSize = OoxmlPackage.PointToDip(HeadingFontSizesPt[headingLevel - 1]);
            paragraph.FontWeight = FontWeights.Bold;
            topMargin = paragraph.FontSize * 0.85;
            bottomMargin = paragraph.FontSize * 0.35;
        }
        else
        {
            var bodySizePt = baseFontSizePt ?? (inTable ? BodyFontSizePt - 0.5 : BodyFontSizePt);
            paragraph.FontSize = OoxmlPackage.PointToDip(bodySizePt);
            topMargin = 0;
            bottomMargin = OoxmlPackage.PointToDip(inTable ? 2 : 4);
        }

        if (foreground is not null) paragraph.Foreground = foreground;

        paragraph.TextAlignment = style.Alignment switch
        {
            WordTextAlignment.Center => TextAlignment.Center,
            WordTextAlignment.Right => TextAlignment.Right,
            WordTextAlignment.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };

        // 缩进：列表按层级缩进，普通段落用 w:ind 的左缩进（页眉页脚 / 脚注不缩进）
        var leftIndent = 0d;
        if (headingLevel == 0 && !inTable && !baseFontSizePt.HasValue)
        {
            leftIndent = style.LeftIndentDip > 0 ? style.LeftIndentDip : style.IndentLevel * 24;
            if (!string.IsNullOrEmpty(style.ListMarker)) leftIndent = Math.Max(leftIndent, (style.IndentLevel + 1) * 24);
        }

        paragraph.Margin = margin ?? new Thickness(leftIndent, topMargin, 0, bottomMargin);
        if (!string.IsNullOrEmpty(style.ListMarker))
        {
            // 列表标记挂在首行：负的 TextIndent 让标记比正文往左突出一点
            paragraph.TextIndent = -18;
            paragraph.Inlines.Add(new Run(style.ListMarker));
        }

        if (style.IsPageBreakBefore) paragraph.BreakPageBefore = true;

        foreach (var wordRun in block.Runs)
        {
            var inline = BuildInline(wordRun, forceBold || headingLevel > 0);
            if (inline is not null) paragraph.Inlines.Add(inline);
        }

        // 空段落也保留一行高度（空 Run 让它有一行的行高）
        if (paragraph.Inlines.Count == 0) paragraph.Inlines.Add(new Run(""));
        return paragraph;
    }

    private static Inline? BuildInline(WordRun wordRun, bool forceBold)
    {
        // 脚注 / 尾注引用常常是个「没有文字的 run」（w:footnoteReference 不占正文文字），
        // 这种 run 也要渲染：补一个上标 [n]，否则正文里看不到引用标记。
        var noteNumber = wordRun.FootnoteNumber ?? wordRun.EndnoteNumber;
        if (string.IsNullOrEmpty(wordRun.Text) && !wordRun.IsHyperlink && noteNumber is null) return null;

        var run = new Run(wordRun.Text)
        {
            FontWeight = forceBold || wordRun.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = wordRun.Italic ? FontStyles.Italic : FontStyles.Normal,
        };

        if (wordRun.Underline) run.TextDecorations = TextDecorations.Underline;
        else if (wordRun.Strike) run.TextDecorations = TextDecorations.Strikethrough;

        if (wordRun.IsSuperscript) run.BaselineAlignment = BaselineAlignment.Superscript;
        else if (wordRun.IsSubscript) run.BaselineAlignment = BaselineAlignment.Subscript;

        if (wordRun.FontSizePt.HasValue && wordRun.FontSizePt.Value > 0)
            run.FontSize = OoxmlPackage.PointToDip(wordRun.FontSizePt.Value);
        if (!string.IsNullOrWhiteSpace(wordRun.FontFamily))
        {
            try { run.FontFamily = new FontFamily(wordRun.FontFamily); }
            catch { /* 字体名非法：保持默认字体 */ }
        }

        if (TryCreateBrush(wordRun.ColorHex) is { } foreground) run.Foreground = foreground;
        if (TryCreateBrush(wordRun.HighlightHex) is { } highlight) run.Background = highlight;

        Inline inline;
        if (!wordRun.IsHyperlink)
        {
            inline = run;
        }
        else
        {
            // 超链接：蓝色 + 下划线；有地址就接 RequestNavigate，点击用系统默认浏览器打开
            var link = new Hyperlink
            {
                Foreground = LinkBrush,
                TextDecorations = TextDecorations.Underline,
                ToolTip = wordRun.Url,
            };
            link.Inlines.Add(run);

            if (!string.IsNullOrWhiteSpace(wordRun.Url)
                && Uri.TryCreate(wordRun.Url, UriKind.Absolute, out var uri))
            {
                link.NavigateUri = uri;
                link.RequestNavigate += OnHyperlinkRequestNavigate;
            }

            inline = link;
        }

        if (noteNumber is null) return inline;

        // 上标 [n]：蓝色小字 + 悬停提示；颜色属于「文档内容」，写死不跟主题
        var marker = new Run($"[{noteNumber}]")
        {
            BaselineAlignment = BaselineAlignment.Superscript,
            Foreground = NoteReferenceBrush,
        };
        ToolTipService.SetToolTip(
            marker,
            wordRun.FootnoteId is not null
                ? $"脚注 [{noteNumber}]（内容见文档末尾）"
                : $"尾注 [{noteNumber}]（内容见文档末尾）");

        var span = new Span();
        span.Inlines.Add(inline);
        span.Inlines.Add(marker);
        return span;
    }

    private static void OnHyperlinkRequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // 系统没有默认浏览器 / 地址不合法：静默失败，不能因为点链接把阅读器弄崩
        }
    }

    // ---------------------------------------------------------------- 表格

    private static Table BuildTable(WordTableBlock block, double? baseFontSizePt = null, Brush? foreground = null)
    {
        // 先按每一行实际占的列数算总列数（合并单元格会让行内单元格个数变少）
        var columnCount = block.ColumnWidthDip.Count;
        foreach (var row in block.Rows)
        {
            var used = 0;
            foreach (var cell in row.Cells)
            {
                if (cell.IsMergedContinuation) continue;
                used += Math.Max(1, cell.ColumnSpan);
            }

            columnCount = Math.Max(columnCount, used);
        }

        if (columnCount <= 0) columnCount = 1;

        var table = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, OoxmlPackage.PointToDip(5), 0, OoxmlPackage.PointToDip(8)),
        };

        for (var column = 0; column < columnCount; column++)
        {
            var width = column < block.ColumnWidthDip.Count && block.ColumnWidthDip[column] > 4
                ? block.ColumnWidthDip[column]
                : 120;   // 没写列宽时给一个能看的默认宽度
            table.Columns.Add(new TableColumn { Width = new GridLength(width) });
        }

        var rowGroup = new TableRowGroup();
        table.RowGroups.Add(rowGroup);

        foreach (var modelRow in block.Rows)
        {
            var row = new TableRow();
            foreach (var modelCell in modelRow.Cells)
            {
                // 被上面纵向合并盖住的占位格不渲染，否则 WPF 会把后面的格子整体右移
                if (modelCell.IsMergedContinuation) continue;

                var cell = new TableCell
                {
                    ColumnSpan = Math.Max(1, modelCell.ColumnSpan),
                    RowSpan = Math.Max(1, modelCell.RowSpan),
                    Padding = new Thickness(6, 4, 6, 4),
                    BorderBrush = GridLineBrush,
                    BorderThickness = new Thickness(1),
                };

                if (TryCreateBrush(modelCell.ShadingHex) is { } shading) cell.Background = shading;
                else if (modelRow.IsHeader) cell.Background = TableHeaderBrush;

                foreach (var child in modelCell.Blocks)
                {
                    switch (child)
                    {
                        case WordParagraphBlock paragraphBlock:
                            cell.Blocks.Add(BuildParagraph(paragraphBlock, inTable: true,
                                forceBold: modelRow.IsHeader, baseFontSizePt: baseFontSizePt,
                                foreground: foreground));
                            break;
                        case WordImageBlock imageBlock:
                            cell.Blocks.Add(BuildImage(imageBlock));
                            break;
                        case WordTableBlock nestedTable:
                            cell.Blocks.Add(BuildTable(nestedTable, baseFontSizePt, foreground));
                            break;
                    }
                }

                // 空单元格也放一个空段落，否则某些合并组合下单元格会塌成 0 高
                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new Paragraph(new Run("")) { Margin = new Thickness(0) });

                row.Cells.Add(cell);
            }

            rowGroup.Rows.Add(row);
        }

        return table;
    }

    // ---------------------------------------------------------------- 图片

    private static BlockUIContainer BuildImage(WordImageBlock block)
    {
        var container = new BlockUIContainer
        {
            Margin = new Thickness(0, OoxmlPackage.PointToDip(5), 0, OoxmlPackage.PointToDip(7)),
            TextAlignment = TextAlignment.Center,
        };

        try
        {
            var bitmap = LoadBitmap(block.Data);
            if (bitmap is null)
            {
                container.Child = CreateHintText("（图片无法解码）");
                return container;
            }

            // 尺寸优先用 docx 里写的（EMU → DIP）；没写就按像素 / DPI 换算
            var width = block.WidthDip > 1 ? block.WidthDip : bitmap.PixelWidth * 96.0 / Math.Max(1, bitmap.DpiX);
            var height = block.HeightDip > 1 ? block.HeightDip : bitmap.PixelHeight * 96.0 / Math.Max(1, bitmap.DpiY);
            if (width <= 1 || height <= 1)
            {
                width = Math.Max(1, bitmap.PixelWidth);
                height = Math.Max(1, bitmap.PixelHeight);
            }

            var scale = Math.Min(1.0, Math.Min(MaxImageWidth / width, MaxImageHeight / height));
            var image = new Image
            {
                Source = bitmap,
                Width = width * scale,
                Height = height * scale,
                Stretch = Stretch.Uniform,
            };

            if (!string.IsNullOrWhiteSpace(block.AltText))
                image.ToolTip = block.AltText;

            container.Child = image;
        }
        catch
        {
            // 损坏 / 不支持的图片格式（例如 EMF）：显示占位提示，不影响整篇阅读
            container.Child = CreateHintText("（图片无法显示）");
        }

        return container;
    }

    private static BitmapImage? LoadBitmap(byte[] data)
    {
        if (data.Length == 0) return null;

        using var stream = new MemoryStream(data, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        // OnLoad：EndInit 时就把像素读进内存，之后关掉流也不影响（不会锁住文件）
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();   // 冻结后可以安全地跨线程复用
        return bitmap;
    }

    // ---------------------------------------------------------------- 页眉 / 页脚 / 脚注

    /// <summary>
    /// 渲染页眉或页脚的内容块：统一 9 磅浅灰字（表格也一起缩小），和正文明显区分。
    /// 页眉页脚解析阶段已经保证只有段落 / 表格，这里遇到图片按正文图片处理（极少见）。
    /// </summary>
    private static void AppendHeaderFooterBlocks(FlowDocument flow, IReadOnlyList<WordBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case WordParagraphBlock paragraphBlock:
                    flow.Blocks.Add(BuildParagraph(paragraphBlock, inTable: false, forceBold: false,
                        baseFontSizePt: HeaderFooterFontSizePt, foreground: HeaderFooterBrush,
                        margin: new Thickness(0, 0, 0, OoxmlPackage.PointToDip(1))));
                    break;

                case WordTableBlock tableBlock:
                    flow.Blocks.Add(BuildTable(tableBlock, HeaderFooterFontSizePt, HeaderFooterBrush));
                    break;

                case WordImageBlock imageBlock:
                    flow.Blocks.Add(BuildImage(imageBlock));
                    break;
            }
        }
    }

    /// <summary>
    /// 页眉 / 页脚与正文之间的细分隔线：用一个「空段落 + 单边框」实现，
    /// 比插真的 <c>Border</c> 元素更省事，也不会在打印 / 缩放时变成一块色块。
    /// </summary>
    private static void AppendHeaderFooterDivider(FlowDocument flow, bool isHeader)
    {
        var divider = new Paragraph(new Run(""))
        {
            FontSize = 1,
            LineHeight = 1,
            Margin = isHeader
                ? new Thickness(0, OoxmlPackage.PointToDip(2), 0, OoxmlPackage.PointToDip(6))
                : new Thickness(0, OoxmlPackage.PointToDip(8), 0, OoxmlPackage.PointToDip(4)),
            Padding = new Thickness(0),
            BorderBrush = SeparatorBrush,
            BorderThickness = isHeader ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0),
        };

        flow.Blocks.Add(divider);
    }

    /// <summary>
    /// 文档末尾的「脚注」/「尾注」小节：灰色小标题 + 细分隔线，每条按 <c>[n] 内容</c> 排；
    /// 多段内容按段落排（表格照常渲染，图片在解析阶段已跳过）。
    /// </summary>
    private static void AppendNotesSection(FlowDocument flow, string title, IReadOnlyList<WordNote> notes)
    {
        flow.Blocks.Add(new Paragraph(new Run(title))
        {
            FontSize = OoxmlPackage.PointToDip(NoteTitleFontSizePt),
            FontWeight = FontWeights.SemiBold,
            Foreground = NoteTitleBrush,
            Margin = new Thickness(0, OoxmlPackage.PointToDip(14), 0, OoxmlPackage.PointToDip(4)),
            Padding = new Thickness(0, OoxmlPackage.PointToDip(6), 0, 0),
            BorderBrush = SeparatorBrush,
            BorderThickness = new Thickness(0, 1, 0, 0),
        });

        foreach (var note in notes)
        {
            var isFirstBlock = true;
            foreach (var block in note.Blocks)
            {
                switch (block)
                {
                    case WordParagraphBlock paragraphBlock:
                    {
                        var paragraph = BuildParagraph(paragraphBlock, inTable: false, forceBold: false,
                            baseFontSizePt: NoteFontSizePt, foreground: NoteTextBrush,
                            margin: new Thickness(0, 0, 0, OoxmlPackage.PointToDip(2)));

                        // 编号只加在这一条脚注的第一段前面
                        if (isFirstBlock)
                        {
                            var prefix = new Run($"[{note.Number}] ")
                            {
                                Foreground = NoteReferenceBrush,
                                FontWeight = FontWeights.SemiBold,
                            };

                            if (paragraph.Inlines.FirstInline is { } firstInline)
                                paragraph.Inlines.InsertBefore(firstInline, prefix);
                            else
                                paragraph.Inlines.Add(prefix);
                        }

                        flow.Blocks.Add(paragraph);
                        break;
                    }

                    case WordTableBlock tableBlock:
                        flow.Blocks.Add(BuildTable(tableBlock, NoteFontSizePt, NoteTextBrush));
                        break;
                }

                isFirstBlock = false;
            }
        }
    }

    // ---------------------------------------------------------------- 杂项

    private static Paragraph BuildEmptyHint()
    {
        return new Paragraph(new Run("这个文档没有可显示的内容（可能是空文档，或内容都在暂不支持的部件里）"))
        {
            Foreground = HintBrush,
            FontSize = OoxmlPackage.PointToDip(BodyFontSizePt),
            Margin = new Thickness(0, OoxmlPackage.PointToDip(12), 0, 0),
        };
    }

    private static TextBlock CreateHintText(string text) => new()
    {
        Text = text,
        Foreground = HintBrush,
        FontSize = 12,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    /// <summary>把 <c>#RRGGBB</c> 转成冻结画刷；非法 / 空值返回 null（用默认色）。</summary>
    private static Brush? TryCreateBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return null;
        }
    }

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
