using System.Text;
using System.Text.RegularExpressions;

namespace YuLiangToolkit.Tools.TextEditor.Core.Markdown;

/// <summary>
/// 轻量 Markdown 解析器（纯逻辑、无 WPF 依赖，自检直接断言）：把文本解析成
/// <see cref="MarkdownBlock"/> / <see cref="MarkdownInline"/> 模型，交给
/// <see cref="MarkdownPreviewRenderer"/> 画成 WPF FlowDocument 预览。
///
/// <para>支持的语法（够「实际效果预览」用，不追求 CommonMark 100%）：</para>
/// <list type="bullet">
///   <item>ATX 标题 <c>#</c> ~ <c>######</c>；</item>
///   <item>段落、硬换行（行尾两个空格 / 反斜杠）；</item>
///   <item>围栏代码块 <c>```语言</c> 与 <c>~~~</c>；</item>
///   <item>引用块 <c>&gt;</c>（可嵌套、内部可含其它块）；</item>
///   <item>有序 / 无序列表（支持嵌套与松散列表）；</item>
///   <item>表格（<c>| a | b |</c> + <c>|---|:--:|</c> 对齐行）；</item>
///   <item>分割线 <c>---</c> / <c>***</c> / <c>___</c>；</item>
///   <item>行内：<c>**加粗**</c> / <c>*斜体*</c> / <c>`代码`</c> / <c>~~删除线~~</c> /
///         <c>[链接](url)</c> / <c>![图片](url)</c> / <c>\</c> 转义 / 自动链接 <c>&lt;https://…&gt;</c>。</item>
/// </list>
/// </summary>
public static class MarkdownParser
{
    private static readonly Regex OrderedListMarker = new(@"^(\d{1,9})[.)]\s+(.*)$", RegexOptions.CultureInvariant);
    private static readonly Regex ThematicBreakPattern = new(@"^([-*_])\1{2,}$", RegexOptions.CultureInvariant);

    /// <summary>把 Markdown 文本解析成块列表（空文本返回空表；不会抛异常）。</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return Array.Empty<MarkdownBlock>();
        try
        {
            var lines = ToLines(markdown);
            var blocks = new List<MarkdownBlock>();
            var index = 0;
            ParseBlocks(lines, ref index, blocks);
            return blocks;
        }
        catch
        {
            // 预览是锦上添花：解析失败就把整篇当普通段落，绝不把编辑器搞崩
            return new MarkdownBlock[] { new MarkdownParagraph(new MarkdownInline[] { new MarkdownText(markdown) }) };
        }
    }

    /// <summary>解析行内元素（标题 / 表格单元格也会用到）。</summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string? text)
    {
        var result = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text)) return result;
        ParseInlineRange(text, 0, text.Length, result);
        return result;
    }

    // ---------------- 块级解析 ----------------

    private static List<string> ToLines(string markdown)
    {
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Split('\n').ToList();
    }

    private static void ParseBlocks(List<string> lines, ref int index, List<MarkdownBlock> blocks)
    {
        while (index < lines.Count)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                index++;
                continue;
            }

            // 围栏代码块
            if (TryFenceStart(line, out var fenceChar, out var fenceCount, out var fenceIndent, out var language))
            {
                blocks.Add(ParseFence(lines, ref index, fenceChar, fenceCount, fenceIndent, language));
                continue;
            }

            // 分割线
            if (IsThematicBreak(line))
            {
                blocks.Add(new MarkdownThematicBreak());
                index++;
                continue;
            }

            // 标题
            if (TryHeading(line, out var level, out var headingText))
            {
                blocks.Add(new MarkdownHeading(level, ParseInlines(headingText)));
                index++;
                continue;
            }

            // 引用
            if (line.TrimStart().StartsWith('>'))
            {
                blocks.Add(ParseQuote(lines, ref index));
                continue;
            }

            // 表格
            if (TryParseTable(lines, ref index, out var table))
            {
                blocks.Add(table);
                continue;
            }

            // 列表
            if (TryListMarker(line, out _, out _, out _, out _))
            {
                blocks.Add(ParseList(lines, ref index));
                continue;
            }

            // 段落
            blocks.Add(ParseParagraph(lines, ref index));
        }
    }

    private static MarkdownCodeBlock ParseFence(
        List<string> lines, ref int index, char fenceChar, int fenceCount, int fenceIndent, string? language)
    {
        index++;   // 跳过开始的围栏
        var code = new StringBuilder();
        while (index < lines.Count)
        {
            var raw = lines[index];
            if (IsFenceEnd(raw, fenceChar, fenceCount))
            {
                index++;
                break;
            }
            code.AppendLine(TrimIndent(raw, fenceIndent));
            index++;
        }
        return new MarkdownCodeBlock(
            string.IsNullOrWhiteSpace(language) ? null : language.Trim(),
            code.ToString().TrimEnd('\n'));
    }

    private static MarkdownQuote ParseQuote(List<string> lines, ref int index)
    {
        var inner = new List<string>();
        while (index < lines.Count)
        {
            var raw = lines[index];
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith('>'))
            {
                var content = trimmed[1..];
                if (content.StartsWith(' ')) content = content[1..];
                inner.Add(content.TrimEnd());
                index++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(raw))
            {
                // 引用块中间允许空行：空行之后还是引用才继续
                var next = index + 1;
                while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next])) next++;
                if (next < lines.Count && lines[next].TrimStart().StartsWith('>'))
                {
                    inner.Add("");
                    index = next;
                    continue;
                }
            }
            break;
        }

        var blocks = new List<MarkdownBlock>();
        var innerIndex = 0;
        ParseBlocks(inner, ref innerIndex, blocks);
        return new MarkdownQuote(blocks);
    }

    private static MarkdownList ParseList(List<string> lines, ref int index)
    {
        TryListMarker(lines[index], out var ordered, out var startNumber, out var baseIndent, out _);
        var items = new List<MarkdownListItem>();

        while (index < lines.Count)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) break;
            if (!TryListMarker(lines[index], out var itemOrdered, out _, out var itemIndent, out var content)) break;
            if (itemOrdered != ordered || itemIndent != baseIndent) break;

            index++;
            var itemBlocks = new List<MarkdownBlock>();
            var paragraph = new StringBuilder(content.Trim());

            while (index < lines.Count)
            {
                var raw = lines[index];
                if (string.IsNullOrWhiteSpace(raw)) break;

                if (TryListMarker(raw, out _, out _, out var nestedIndent, out _) && nestedIndent > baseIndent)
                {
                    if (paragraph.Length > 0)
                    {
                        itemBlocks.Add(new MarkdownParagraph(ParseInlines(paragraph.ToString())));
                        paragraph.Clear();
                    }
                    var before = index;
                    itemBlocks.Add(ParseList(lines, ref index));
                    if (index == before) index++;   // 防御：绝不原地打转
                    continue;
                }

                var continuationIndent = IndentOf(raw);
                if (continuationIndent <= baseIndent) break;
                paragraph.Append(' ').Append(raw.Trim());
                index++;
            }

            if (paragraph.Length > 0) itemBlocks.Add(new MarkdownParagraph(ParseInlines(paragraph.ToString())));
            items.Add(new MarkdownListItem(itemBlocks));

            // 松散列表：允许隔一两个空行后继续同一层级的项
            if (index < lines.Count && string.IsNullOrWhiteSpace(lines[index]))
            {
                var next = index;
                while (next < lines.Count && string.IsNullOrWhiteSpace(lines[next])) next++;
                if (next < lines.Count
                    && TryListMarker(lines[next], out var nextOrdered, out _, out var nextIndent, out _)
                    && nextOrdered == ordered && nextIndent == baseIndent)
                {
                    index = next;
                    continue;
                }
                break;
            }
        }

        return new MarkdownList(ordered, startNumber, items);
    }

    private static MarkdownParagraph ParseParagraph(List<string> lines, ref int index)
    {
        var text = new StringBuilder();
        while (index < lines.Count)
        {
            var raw = lines[index];
            if (string.IsNullOrWhiteSpace(raw)) break;
            if (text.Length > 0 && StartsBlock(lines, index)) break;

            var content = raw.Trim();
            var hardBreak = raw.EndsWith("  ", StringComparison.Ordinal) || raw.EndsWith('\\');
            text.Append(content);
            if (index + 1 < lines.Count && !string.IsNullOrWhiteSpace(lines[index + 1]) && !StartsBlock(lines, index + 1))
                text.Append(hardBreak ? '\n' : ' ');
            else if (hardBreak)
                text.Append('\n');
            index++;

            if (!hardBreak && text.Length > 0 && index < lines.Count && StartsBlock(lines, index)) break;
        }
        return new MarkdownParagraph(ParseInlines(text.ToString()));
    }

    /// <summary>当前行是不是「另一个块的开始」（段落遇到它就结束）。</summary>
    private static bool StartsBlock(List<string> lines, int index)
    {
        if (index < 0 || index >= lines.Count) return false;
        var line = lines[index];
        if (string.IsNullOrWhiteSpace(line)) return true;

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('>')) return true;
        if (TryFenceStart(line, out _, out _, out _, out _)) return true;
        if (IsThematicBreak(line)) return true;
        if (TryHeading(line, out _, out _)) return true;
        if (TryListMarker(line, out _, out _, out _, out _)) return true;
        if (index + 1 < lines.Count && IsTableSeparator(lines[index + 1]) && LineContainsPipe(line)) return true;
        return false;
    }

    private static bool TryParseTable(List<string> lines, ref int index, out MarkdownTable table)
    {
        table = null!;
        if (index + 1 >= lines.Count) return false;

        var headerLine = lines[index];
        if (!LineContainsPipe(headerLine)) return false;
        if (!IsTableSeparator(lines[index + 1])) return false;

        var headerCells = SplitTableCells(headerLine);
        var separatorCells = SplitTableCells(lines[index + 1]);
        if (headerCells.Count == 0 || separatorCells.Count != headerCells.Count) return false;

        var columns = separatorCells
            .Select(cell => new MarkdownTableColumn(ParseAlignment(cell)))
            .ToList();

        index += 2;
        var rows = new List<IReadOnlyList<MarkdownTableCell>>();
        while (index < lines.Count)
        {
            var raw = lines[index];
            if (string.IsNullOrWhiteSpace(raw) || !LineContainsPipe(raw)) break;
            var cells = SplitTableCells(raw);
            if (cells.Count == 0) break;

            var row = new List<MarkdownTableCell>();
            for (var column = 0; column < headerCells.Count; column++)
            {
                var value = column < cells.Count ? cells[column] : "";
                row.Add(new MarkdownTableCell(ParseInlines(value)));
            }
            rows.Add(row);
            index++;
        }

        table = new MarkdownTable(
            columns,
            headerCells.Select(cell => new MarkdownTableCell(ParseInlines(cell))).ToList(),
            rows);
        return true;
    }

    private static MarkdownTableAlignment ParseAlignment(string separatorCell)
    {
        var text = separatorCell.Trim();
        var left = text.StartsWith(':');
        var right = text.EndsWith(':') && text.Length > 1;
        return (left, right) switch
        {
            (true, true) => MarkdownTableAlignment.Center,
            (true, false) => MarkdownTableAlignment.Left,
            (false, true) => MarkdownTableAlignment.Right,
            _ => MarkdownTableAlignment.None,
        };
    }

    private static List<string> SplitTableCells(string line)
    {
        var text = line.Trim();
        if (text.StartsWith('|')) text = text[1..];
        if (text.EndsWith('|')) text = text[..^1];
        if (text.Length == 0) return new List<string>();
        return text.Split('|').Select(cell => cell.Trim()).ToList();
    }

    private static bool LineContainsPipe(string line) => line.Contains('|');

    // ---------------- 行内解析 ----------------

    private static void ParseInlineRange(string text, int start, int end, List<MarkdownInline> target)
    {
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length == 0) return;
            target.Add(new MarkdownText(buffer.ToString()));
            buffer.Clear();
        }

        var i = start;
        while (i < end)
        {
            var c = text[i];

            // 反斜杠转义
            if (c == '\\' && i + 1 < end)
            {
                buffer.Append(text[i + 1]);
                i += 2;
                continue;
            }

            // 硬换行（解析器在段落里用 \n 表示行尾两个空格 / 反斜杠）
            if (c == '\n')
            {
                Flush();
                target.Add(new MarkdownHardBreak());
                i++;
                continue;
            }

            // 行内代码
            if (c == '`')
            {
                var run = CountRun(text, i, '`', end);
                var close = FindClosingRun(text, i + run, '`', run, end);
                if (close >= 0)
                {
                    Flush();
                    target.Add(new MarkdownInlineCode(text[(i + run)..close].Trim()));
                    i = close + run;
                    continue;
                }
            }

            // 图片 ![alt](url)
            if (c == '!' && i + 1 < end && text[i + 1] == '['
                && TryParseLink(text, i + 1, end, out var alt, out var imageUrl, out var imageNext))
            {
                Flush();
                target.Add(new MarkdownImage(PlainText(ParseInlines(alt)), imageUrl));
                i = imageNext;
                continue;
            }

            // 链接 [text](url)
            if (c == '[' && TryParseLink(text, i, end, out var label, out var linkUrl, out var linkNext))
            {
                Flush();
                target.Add(new MarkdownLink(ParseInlines(label), linkUrl));
                i = linkNext;
                continue;
            }

            // 删除线 ~~text~~
            if (c == '~' && i + 1 < end && text[i + 1] == '~')
            {
                var close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close >= 0 && close < end)
                {
                    Flush();
                    target.Add(new MarkdownStrikethrough(ParseInlines(text[(i + 2)..close])));
                    i = close + 2;
                    continue;
                }
            }

            // 加粗 / 斜体
            if (c is '*' or '_')
            {
                var run = CountRun(text, i, c, end);
                if (run >= 2)
                {
                    var close = FindClosingRun(text, i + 2, c, 2, end);
                    if (close >= 0)
                    {
                        Flush();
                        target.Add(new MarkdownStrong(ParseInlines(text[(i + 2)..close])));
                        i = close + 2;
                        continue;
                    }
                }
                var single = FindClosingRun(text, i + 1, c, 1, end);
                if (single >= 0)
                {
                    Flush();
                    target.Add(new MarkdownEmphasis(ParseInlines(text[(i + 1)..single])));
                    i = single + 1;
                    continue;
                }
            }

            // 自动链接 <https://...>
            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i + 1 && close < end)
                {
                    var inner = text[(i + 1)..close];
                    if (inner.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                        || inner.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                        || inner.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                    {
                        Flush();
                        target.Add(new MarkdownLink(new MarkdownInline[] { new MarkdownText(inner) }, inner));
                        i = close + 1;
                        continue;
                    }
                }
            }

            buffer.Append(c);
            i++;
        }

        Flush();
    }

    private static bool TryParseLink(string text, int openBracket, int end, out string label, out string url, out int next)
    {
        label = "";
        url = "";
        next = openBracket;

        var closeBracket = text.IndexOf(']', openBracket + 1);
        if (closeBracket < 0 || closeBracket >= end) return false;
        if (closeBracket + 1 >= end || text[closeBracket + 1] != '(') return false;

        var closeParen = text.IndexOf(')', closeBracket + 2);
        if (closeParen < 0 || closeParen >= end) return false;

        label = text[(openBracket + 1)..closeBracket];
        var rawUrl = text[(closeBracket + 2)..closeParen].Trim();
        var space = rawUrl.IndexOf(' ');
        url = (space >= 0 ? rawUrl[..space] : rawUrl).Trim();
        if (url.Length == 0) return false;

        next = closeParen + 1;
        return true;
    }

    /// <summary>把行内元素拍平成纯文本（图片 alt / 提示用）。</summary>
    public static string PlainText(IReadOnlyList<MarkdownInline> inlines)
    {
        var builder = new StringBuilder();
        AppendPlainText(inlines, builder);
        return builder.ToString();
    }

    private static void AppendPlainText(IReadOnlyList<MarkdownInline> inlines, StringBuilder builder)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MarkdownText text: builder.Append(text.Text); break;
                case MarkdownInlineCode code: builder.Append(code.Text); break;
                case MarkdownHardBreak: builder.Append(' '); break;
                case MarkdownEmphasis emphasis: AppendPlainText(emphasis.Inlines, builder); break;
                case MarkdownStrong strong: AppendPlainText(strong.Inlines, builder); break;
                case MarkdownStrikethrough strike: AppendPlainText(strike.Inlines, builder); break;
                case MarkdownLink link: AppendPlainText(link.Text, builder); break;
                case MarkdownImage image: builder.Append(image.Alt); break;
            }
        }
    }

    // ---------------- 小工具 ----------------

    private static bool TryFenceStart(string raw, out char fenceChar, out int fenceCount, out int indent, out string? language)
    {
        fenceChar = '`';
        fenceCount = 0;
        indent = 0;
        language = null;

        var trimmedStart = raw.TrimStart();
        indent = raw.Length - trimmedStart.Length;
        if (trimmedStart.Length < 3) return false;

        var c = trimmedStart[0];
        if (c is not ('`' or '~')) return false;

        var count = 0;
        while (count < trimmedStart.Length && trimmedStart[count] == c) count++;
        if (count < 3) return false;

        fenceChar = c;
        fenceCount = count;
        language = trimmedStart[count..].Trim();
        return true;
    }

    private static bool IsFenceEnd(string raw, char fenceChar, int fenceCount)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < fenceCount) return false;
        for (var i = 0; i < trimmed.Length; i++)
            if (trimmed[i] != fenceChar) return false;
        return trimmed.Length >= fenceCount;
    }

    private static bool TryHeading(string raw, out int level, out string text)
    {
        level = 0;
        text = "";
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == '#') count++;
        if (count > 6) return false;
        if (count < trimmed.Length && trimmed[count] != ' ') return false;

        level = count;
        text = trimmed[count..].Trim().TrimEnd('#').Trim();
        return true;
    }

    private static bool IsThematicBreak(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length < 3) return false;
        if (trimmed[0] is not ('-' or '*' or '_')) return false;
        return ThematicBreakPattern.IsMatch(trimmed);
    }

    private static bool TryListMarker(string raw, out bool ordered, out int startNumber, out int indent, out string content)
    {
        ordered = false;
        startNumber = 1;
        indent = IndentOf(raw);
        content = "";

        var rest = raw[indent..];
        if (rest.Length >= 2 && rest[0] is '-' or '*' or '+' && rest[1] == ' ')
        {
            content = rest[2..].Trim();
            return true;
        }

        var match = OrderedListMarker.Match(rest);
        if (!match.Success) return false;
        ordered = true;
        _ = int.TryParse(match.Groups[1].ValueSpan, out startNumber);
        if (startNumber <= 0) startNumber = 1;
        content = match.Groups[2].Value.Trim();
        return true;
    }

    private static bool IsTableSeparator(string raw)
    {
        var cells = SplitTableCells(raw);
        if (cells.Count == 0) return false;

        foreach (var cell in cells)
        {
            var trimmed = cell.Trim();
            if (trimmed.Length < 1) return false;
            var core = trimmed.Trim(':');
            if (core.Length < 1) return false;
            foreach (var c in core)
                if (c != '-') return false;
        }
        return true;
    }

    private static int IndentOf(string raw)
    {
        var count = 0;
        foreach (var c in raw)
        {
            if (c == ' ') count++;
            else if (c == '\t') count += 4;
            else break;
        }
        return count;
    }

    private static string TrimIndent(string raw, int indent)
    {
        var removed = 0;
        var index = 0;
        while (index < raw.Length && removed < indent)
        {
            if (raw[index] == ' ') removed++;
            else if (raw[index] == '\t') removed += 4;
            else break;
            index++;
        }
        return raw[index..];
    }

    private static int CountRun(string text, int start, char c, int end)
    {
        var count = 0;
        while (start + count < end && text[start + count] == c) count++;
        return count;
    }

    private static int FindClosingRun(string text, int from, char c, int run, int end)
    {
        var i = from;
        while (i < end)
        {
            if (text[i] != c)
            {
                i++;
                continue;
            }

            var count = 0;
            while (i + count < end && text[i + count] == c) count++;
            if (count >= run) return i;
            i += count;
        }
        return -1;
    }
}
