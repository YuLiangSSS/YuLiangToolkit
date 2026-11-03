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

/// <summary>渲染结果：<see cref="Document"/> 交给 <c>FlowDocumentScrollViewer</c>，段落映射表给查找 / 大纲跳转用。</summary>
/// <param name="Document">排好版的 FlowDocument（白色纸面）。</param>
/// <param name="ParagraphByBlockIndex">
/// 「模型块下标 → 渲染出来的段落」；表格 / 图片块没有单独的段落，对应项是 <c>null</c>。
/// </param>
public sealed record WordRenderResult(FlowDocument Document, IReadOnlyList<Paragraph?> ParagraphByBlockIndex);

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

    /// <summary>纸面内边距（左右 / 上下，DIP）。</summary>
    private const double PagePaddingHorizontal = 56;
    private const double PagePaddingVertical = 48;

    /// <summary>图片最大显示尺寸（超出等比缩小，避免一张大图把滚动条拉到很远）。</summary>
    private const double MaxImageWidth = 760;
    private const double MaxImageHeight = 1000;

    /// <summary>标题 1 ~ 6 级的字号（磅）：逐级递减，正文 11 磅收尾。</summary>
    private static readonly double[] HeadingFontSizesPt = { 22, 18, 15.5, 13.5, 12.5, 11.5 };

    // 文档纸面用色（不随主题变化）：白纸、近黑字、浅灰网格线、表头浅灰底、链接蓝、空文档提示灰
    private static readonly Brush InkBrush = CreateFrozenBrush(0x1F, 0x23, 0x28);
    private static readonly Brush GridLineBrush = CreateFrozenBrush(0xD0, 0xD0, 0xD0);
    private static readonly Brush TableHeaderBrush = CreateFrozenBrush(0xF2, 0xF2, 0xF2);
    private static readonly Brush LinkBrush = CreateFrozenBrush(0x1A, 0x66, 0xCC);
    private static readonly Brush HintBrush = CreateFrozenBrush(0x88, 0x88, 0x88);

    /// <summary>把整篇文档渲染成 <see cref="FlowDocument"/>。<b>必须在 UI 线程调用</b>（要在里面建 WPF 对象）。</summary>
    public static WordRenderResult Build(WordDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

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

        foreach (var block in document.Blocks)
        {
            switch (block)
            {
                case WordParagraphBlock paragraphBlock:
                {
                    var paragraph = BuildParagraph(paragraphBlock, inTable: false, forceBold: false);
                    paragraphByBlockIndex.Add(paragraph);
                    flow.Blocks.Add(paragraph);
                    break;
                }

                case WordTableBlock tableBlock:
                    paragraphByBlockIndex.Add(null);
                    flow.Blocks.Add(BuildTable(tableBlock));
                    break;

                case WordImageBlock imageBlock:
                    paragraphByBlockIndex.Add(null);
                    flow.Blocks.Add(BuildImage(imageBlock));
                    break;

                default:
                    paragraphByBlockIndex.Add(null);
                    break;
            }
        }

        if (flow.Blocks.Count == 0) flow.Blocks.Add(BuildEmptyHint());

        return new WordRenderResult(flow, paragraphByBlockIndex);
    }

    // ---------------------------------------------------------------- 段落

    private static Paragraph BuildParagraph(WordParagraphBlock block, bool inTable, bool forceBold)
    {
        var paragraph = new Paragraph();
        var style = block.Style;

        // 标题：级别越高字号越大 + 粗体 + 明显的段前段后距
        var headingLevel = inTable ? 0 : Math.Clamp(style.HeadingLevel, 0, 6);
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
            paragraph.FontSize = OoxmlPackage.PointToDip(inTable ? BodyFontSizePt - 0.5 : BodyFontSizePt);
            topMargin = 0;
            bottomMargin = OoxmlPackage.PointToDip(inTable ? 2 : 4);
        }

        paragraph.TextAlignment = style.Alignment switch
        {
            WordTextAlignment.Center => TextAlignment.Center,
            WordTextAlignment.Right => TextAlignment.Right,
            WordTextAlignment.Justify => TextAlignment.Justify,
            _ => TextAlignment.Left,
        };

        // 缩进：列表按层级缩进，普通段落用 w:ind 的左缩进
        var leftIndent = 0d;
        if (headingLevel == 0 && !inTable)
        {
            leftIndent = style.LeftIndentDip > 0 ? style.LeftIndentDip : style.IndentLevel * 24;
            if (!string.IsNullOrEmpty(style.ListMarker)) leftIndent = Math.Max(leftIndent, (style.IndentLevel + 1) * 24);
        }

        paragraph.Margin = new Thickness(leftIndent, topMargin, 0, bottomMargin);
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
        if (string.IsNullOrEmpty(wordRun.Text) && !wordRun.IsHyperlink) return null;

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

        if (!wordRun.IsHyperlink) return run;

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

        return link;
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

    private static Table BuildTable(WordTableBlock block)
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
                                forceBold: modelRow.IsHeader));
                            break;
                        case WordImageBlock imageBlock:
                            cell.Blocks.Add(BuildImage(imageBlock));
                            break;
                        case WordTableBlock nestedTable:
                            cell.Blocks.Add(BuildTable(nestedTable));
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
