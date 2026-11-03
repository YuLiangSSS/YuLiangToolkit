using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YuLiangToolkit.Tools.TextEditor.Core.Markdown;

/// <summary>
/// Markdown 预览的配色 / 字体（跟着编辑器配置走：背景色、文字色、字体、字号 × 缩放）。
/// 派生色（次要文字 / 分隔线 / 代码块底）由 <see cref="Create"/> 统一算好，调用方不用重复调色。
/// </summary>
public sealed record MarkdownPreviewStyle(
    FontFamily FontFamily,
    FontFamily CodeFontFamily,
    double FontSize,
    Color Background,
    Color Foreground,
    Color Muted,
    Color Accent,
    Color CodeBackground,
    Color Border,
    string? BaseDirectory)
{
    /// <summary>预览里的代码字体：等宽回退链。</summary>
    public const string CodeFontFamilyChain = "Cascadia Mono, Consolas, Courier New, Microsoft YaHei UI";

    /// <summary>由编辑器的背景 / 文字颜色 + 字体算出整套预览配色（亮度不同时自动加深 / 提亮代码块底色）。</summary>
    public static MarkdownPreviewStyle Create(
        Color background, Color foreground, string fontFamily, double fontSize, string? baseDirectory)
    {
        var backgroundLuminance = Luminance(background);
        var muted = Blend(foreground, background, 0.62);
        var accent = Color.FromRgb(0x56, 0x9C, 0xD6);
        var codeBackground = backgroundLuminance < 0.5
            ? Lighten(background, 18)
            : Darken(background, 14);
        var border = Blend(foreground, background, 0.24);
        var size = Math.Clamp(fontSize, 9, 48);
        return new MarkdownPreviewStyle(
            new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Microsoft YaHei UI" : fontFamily),
            new FontFamily(CodeFontFamilyChain),
            size,
            background,
            foreground,
            muted,
            accent,
            codeBackground,
            border,
            baseDirectory);
    }

    internal static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static double Luminance(Color color) => (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;

    private static Color Blend(Color foreground, Color background, double alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        return Color.FromRgb(
            (byte)Math.Clamp(foreground.R * alpha + background.R * (1 - alpha), 0, 255),
            (byte)Math.Clamp(foreground.G * alpha + background.G * (1 - alpha), 0, 255),
            (byte)Math.Clamp(foreground.B * alpha + background.B * (1 - alpha), 0, 255));
    }

    private static Color Lighten(Color color, int amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R + amount, 0, 255),
        (byte)Math.Clamp(color.G + amount, 0, 255),
        (byte)Math.Clamp(color.B + amount, 0, 255));

    private static Color Darken(Color color, int amount) => Color.FromRgb(
        (byte)Math.Clamp(color.R - amount, 0, 255),
        (byte)Math.Clamp(color.G - amount, 0, 255),
        (byte)Math.Clamp(color.B - amount, 0, 255));
}

/// <summary>
/// 把 <see cref="MarkdownBlock"/> 模型渲染成 WPF <see cref="FlowDocument"/>（Markdown「实际效果预览」）。
/// 标题 / 加粗 / 斜体 / 行内代码 / 链接 / 列表 / 表格 / 引用 / 代码块都会画成真正的排版效果；
/// 本地图片（相对当前文件目录）会真的显示，网络图片显示成占位文字（不联网）。
/// </summary>
public static class MarkdownPreviewRenderer
{
    /// <summary>图片最大宽度（像素）。</summary>
    private const double MaxImageWidth = 560;

    /// <summary>把块列表渲染成 FlowDocument；<paramref name="openLink"/> 传入时链接可点击（否则只显示成链接样式）。</summary>
    public static FlowDocument Build(
        IReadOnlyList<MarkdownBlock> blocks,
        MarkdownPreviewStyle style,
        Action<string>? openLink = null)
    {
        var document = new FlowDocument
        {
            Background = MarkdownPreviewStyle.Frozen(style.Background),
            Foreground = MarkdownPreviewStyle.Frozen(style.Foreground),
            FontFamily = style.FontFamily,
            FontSize = style.FontSize,
            PagePadding = new Thickness(16, 12, 16, 28),
            ColumnWidth = double.PositiveInfinity,
            LineHeight = style.FontSize * 1.55,
        };

        if (blocks.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("（空文档 · 在左边输入 Markdown，这里会实时预览）"))
            {
                Foreground = MarkdownPreviewStyle.Frozen(style.Muted),
            });
            return document;
        }

        foreach (var block in blocks)
            document.Blocks.Add(BuildBlock(block, style, openLink));
        return document;
    }

    // ---------------- 块 ----------------

    private static Block BuildBlock(MarkdownBlock block, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        return block switch
        {
            MarkdownHeading heading => BuildHeading(heading, style, openLink),
            MarkdownParagraph paragraph => BuildParagraph(paragraph, style, openLink),
            MarkdownCodeBlock code => BuildCodeBlock(code, style),
            MarkdownQuote quote => BuildQuote(quote, style, openLink),
            MarkdownList list => BuildList(list, style, openLink),
            MarkdownTable table => BuildTable(table, style, openLink),
            MarkdownThematicBreak => BuildThematicBreak(style),
            _ => new Paragraph(),
        };
    }

    private static Paragraph BuildHeading(MarkdownHeading heading, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        double[] scale = { 1.9, 1.55, 1.32, 1.15, 1.05, 1.0 };
        var factor = scale[Math.Clamp(heading.Level - 1, 0, scale.Length - 1)];
        var paragraph = new Paragraph
        {
            FontSize = style.FontSize * factor,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, heading.Level <= 2 ? 14 : 10, 0, 6),
            KeepWithNext = true,
        };
        if (heading.Level <= 2)
        {
            paragraph.BorderBrush = MarkdownPreviewStyle.Frozen(style.Border);
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 4);
        }
        AppendInlines(heading.Inlines, paragraph.Inlines, style, openLink);
        return paragraph;
    }

    private static Paragraph BuildParagraph(MarkdownParagraph paragraph, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        var result = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AppendInlines(paragraph.Inlines, result.Inlines, style, openLink);
        return result;
    }

    private static Block BuildCodeBlock(MarkdownCodeBlock block, MarkdownPreviewStyle style)
    {
        var code = new TextBlock
        {
            Text = string.IsNullOrEmpty(block.Text) ? " " : block.Text,
            FontFamily = style.CodeFontFamily,
            FontSize = Math.Max(10, style.FontSize * 0.95),
            Foreground = MarkdownPreviewStyle.Frozen(style.Foreground),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Max(12, style.FontSize * 1.5),
        };

        var border = new Border
        {
            Background = MarkdownPreviewStyle.Frozen(style.CodeBackground),
            BorderBrush = MarkdownPreviewStyle.Frozen(style.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 2, 0, 10),
        };

        if (string.IsNullOrWhiteSpace(block.Language))
        {
            border.Child = code;
        }
        else
        {
            var panel = new DockPanel();
            var label = new TextBlock
            {
                Text = block.Language,
                FontSize = Math.Max(9, style.FontSize * 0.75),
                Foreground = MarkdownPreviewStyle.Frozen(style.Muted),
                Margin = new Thickness(0, 0, 0, 4),
            };
            DockPanel.SetDock(label, Dock.Top);
            panel.Children.Add(label);
            panel.Children.Add(code);
            border.Child = panel;
        }

        return new BlockUIContainer(border);
    }

    private static Section BuildQuote(MarkdownQuote quote, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        var section = new Section
        {
            BorderBrush = MarkdownPreviewStyle.Frozen(style.Accent),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(10, 0, 0, 0),
            Margin = new Thickness(0, 4, 0, 8),
            Foreground = MarkdownPreviewStyle.Frozen(style.Muted),
        };

        if (quote.Blocks.Count == 0)
        {
            section.Blocks.Add(new Paragraph());
            return section;
        }

        foreach (var child in quote.Blocks)
            section.Blocks.Add(BuildBlock(child, style, openLink));
        return section;
    }

    private static List BuildList(MarkdownList list, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        var result = new List
        {
            MarkerStyle = list.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(22, 2, 0, 8),
            Padding = new Thickness(0),
        };
        if (list.Ordered && list.StartNumber > 1) result.StartIndex = list.StartNumber;

        foreach (var item in list.Items)
        {
            var listItem = new ListItem();
            if (item.Blocks.Count == 0)
            {
                listItem.Blocks.Add(new Paragraph());
            }
            else
            {
                foreach (var child in item.Blocks)
                    listItem.Blocks.Add(BuildBlock(child, style, openLink));
            }
            result.ListItems.Add(listItem);
        }
        return result;
    }

    private static Table BuildTable(MarkdownTable table, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        var result = new Table
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 4, 0, 12),
            BorderBrush = MarkdownPreviewStyle.Frozen(style.Border),
            BorderThickness = new Thickness(1),
        };

        foreach (var _ in table.Columns)
            result.Columns.Add(new TableColumn());

        var group = new TableRowGroup();
        result.RowGroups.Add(group);

        if (table.Header.Count > 0)
        {
            var headerRow = new TableRow { Background = MarkdownPreviewStyle.Frozen(style.CodeBackground) };
            for (var column = 0; column < table.Header.Count; column++)
                headerRow.Cells.Add(BuildCell(table.Header[column], style, openLink,
                    table.Columns[Math.Min(column, table.Columns.Count - 1)].Alignment, bold: true));
            group.Rows.Add(headerRow);
        }

        foreach (var row in table.Rows)
        {
            var tableRow = new TableRow();
            for (var column = 0; column < row.Count; column++)
                tableRow.Cells.Add(BuildCell(row[column], style, openLink,
                    table.Columns[Math.Min(column, table.Columns.Count - 1)].Alignment, bold: false));
            group.Rows.Add(tableRow);
        }

        return result;
    }

    private static TableCell BuildCell(MarkdownTableCell cell, MarkdownPreviewStyle style, Action<string>? openLink,
        MarkdownTableAlignment alignment, bool bold)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0) };
        AppendInlines(cell.Inlines, paragraph.Inlines, style, openLink);

        var tableCell = new TableCell(paragraph)
        {
            Padding = new Thickness(6, 4, 6, 4),
            BorderBrush = MarkdownPreviewStyle.Frozen(style.Border),
            BorderThickness = new Thickness(0, 0, 1, 1),
            TextAlignment = alignment switch
            {
                MarkdownTableAlignment.Center => TextAlignment.Center,
                MarkdownTableAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };
        if (bold) paragraph.FontWeight = FontWeights.Bold;
        return tableCell;
    }

    private static Paragraph BuildThematicBreak(MarkdownPreviewStyle style) => new()
    {
        Margin = new Thickness(0, 12, 0, 12),
        BorderBrush = MarkdownPreviewStyle.Frozen(style.Border),
        BorderThickness = new Thickness(0, 1, 0, 0),
        FontSize = 1,
    };

    // ---------------- 行内 ----------------

    private static void AppendInlines(
        IReadOnlyList<MarkdownInline> inlines, InlineCollection target, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownText text:
                    target.Add(new Run(text.Text));
                    break;

                case MarkdownHardBreak:
                    target.Add(new LineBreak());
                    break;

                case MarkdownInlineCode code:
                    target.Add(new Run(code.Text)
                    {
                        FontFamily = style.CodeFontFamily,
                        Background = MarkdownPreviewStyle.Frozen(style.CodeBackground),
                    });
                    break;

                case MarkdownEmphasis emphasis:
                {
                    var span = new Span { FontStyle = FontStyles.Italic };
                    AppendInlines(emphasis.Inlines, span.Inlines, style, openLink);
                    target.Add(span);
                    break;
                }

                case MarkdownStrong strong:
                {
                    var span = new Span { FontWeight = FontWeights.Bold };
                    AppendInlines(strong.Inlines, span.Inlines, style, openLink);
                    target.Add(span);
                    break;
                }

                case MarkdownStrikethrough strike:
                {
                    var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AppendInlines(strike.Inlines, span.Inlines, style, openLink);
                    target.Add(span);
                    break;
                }

                case MarkdownLink link:
                    target.Add(BuildLink(link, style, openLink));
                    break;

                case MarkdownImage image:
                    target.Add(BuildImage(image, style));
                    break;
            }
        }
    }

    private static Hyperlink BuildLink(MarkdownLink link, MarkdownPreviewStyle style, Action<string>? openLink)
    {
        var hyperlink = new Hyperlink
        {
            Foreground = MarkdownPreviewStyle.Frozen(style.Accent),
            TextDecorations = TextDecorations.Underline,
            ToolTip = link.Url,
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        AppendInlines(link.Text, hyperlink.Inlines, style, openLink);

        if (!string.IsNullOrWhiteSpace(link.Url))
        {
            if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri))
                hyperlink.NavigateUri = uri;

            if (openLink is not null)
            {
                var url = link.Url;
                hyperlink.RequestNavigate += (_, e) =>
                {
                    e.Handled = true;
                    openLink(url);
                };
            }
        }
        return hyperlink;
    }

    private static Inline BuildImage(MarkdownImage image, MarkdownPreviewStyle style)
    {
        var path = ResolveImagePath(style.BaseDirectory, image.Url);
        if (path is not null)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();

                var element = new Image
                {
                    Source = bitmap,
                    MaxWidth = MaxImageWidth,
                    MaxHeight = 480,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 4, 0, 4),
                };
                return new InlineUIContainer(element);
            }
            catch
            {
                // 图片坏了就走下面的占位文字
            }
        }

        var label = string.IsNullOrWhiteSpace(image.Alt) ? image.Url : image.Alt;
        return new Run($"[图片：{label}]")
        {
            Foreground = MarkdownPreviewStyle.Frozen(style.Muted),
            FontStyle = FontStyles.Italic,
        };
    }

    /// <summary>把 Markdown 里的图片地址解析成绝对路径：相对路径按当前文件所在目录；只认本地存在的文件。</summary>
    private static string? ResolveImagePath(string? baseDirectory, string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var candidate = url.Trim();
        if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile)
                candidate = uri.LocalPath;
            else if (!Path.IsPathRooted(candidate) && !string.IsNullOrWhiteSpace(baseDirectory))
                candidate = Path.Combine(baseDirectory!, candidate);

            candidate = Path.GetFullPath(candidate);
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
