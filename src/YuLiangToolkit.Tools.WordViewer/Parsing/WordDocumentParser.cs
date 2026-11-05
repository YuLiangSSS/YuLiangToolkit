using System.IO;
using System.Xml.Linq;
using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.WordViewer.Parsing;

/// <summary>
/// 手写 docx 解析器：把 <c>word/document.xml</c>（连同 styles / numbering / rels / core 属性）
/// 读成与 WPF 无关的 <see cref="WordDocument"/> 模型。
///
/// <para><b>为什么自己写</b>：项目约定「能手写的小轮子就手写」，docx 的正文结构其实很直白
/// （<c>w:p</c> / <c>w:tbl</c> / <c>w:r</c>），为它引一个第三方库不划算。ZIP / 关系 / 单位换算这些
/// 容易写错的部分已经由 Core 的 <see cref="OoxmlPackage"/> 做好，这里只负责 XML 语义。</para>
///
/// <para><b>容错原则</b>：styles / numbering / rels / core 全都可以缺；不认识的元素直接跳过；
/// 只有「根本不是 docx / 主部件损坏」才抛 <see cref="OoxmlException"/>（中文消息，界面直接显示）。
/// 内容过多时按安全上限截断，并把说明记进 <see cref="WordDocument.Notes"/>。</para>
/// </summary>
public static class WordDocumentParser
{
    // ---------- 安全上限：防止畸形 / 超大文档把内存和渲染时间吃满 ----------
    private const int MaxBlocks = 8000;
    private const int MaxTableCells = 20000;
    private const int MaxImages = 200;
    private const long MaxTotalImageBytes = 48L * 1024 * 1024;

    /// <summary>脚注 / 尾注各最多解析多少条（正常文档远小于这个数；防畸形文件刷出几万条）。</summary>
    private const int MaxNotes = 300;

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";

    // ---------- 对外入口 ----------

    /// <summary>
    /// 扩展名是不是本阅读器支持的格式：<c>.docx</c> / <c>.docm</c>（忽略大小写）。
    /// <para><c>.doc</c>（Word 97-2003 的二进制格式）返回 <c>false</c> —— 它不是 OOXML，
    /// 界面要提示用户「用 Word 另存为 .docx」。</para>
    /// </summary>
    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim().Trim('"');
        var extension = Path.GetExtension(trimmed);
        return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".docm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析磁盘上的 docx / docm 文件（只读打开，不会改动被浏览的文件）。</summary>
    /// <exception cref="OoxmlException">扩展名不支持 / 文件损坏 / 缺少主部件时抛出（中文消息）。</exception>
    public static WordDocument Parse(string path)
    {
        if (!IsSupportedExtension(path))
        {
            throw new OoxmlException(Path.GetExtension(path?.Trim() ?? "")?.Equals(".doc", StringComparison.OrdinalIgnoreCase) == true
                ? "不支持旧版 .doc，请用 Word 另存为 .docx 再打开"
                : "只支持 .docx / .docm 格式的 Word 文档");
        }

        using var package = OoxmlPackage.Open(path);
        return ParseCore(package, Path.GetFullPath(path));
    }

    /// <summary>
    /// 解析一个流（例如从内存 / 下载的字节）。<b>流的释放交给 <see cref="OoxmlPackage.Dispose"/> 负责</b>，
    /// 解析结束后这里已经把需要的数据都拷出来了。
    /// </summary>
    /// <exception cref="OoxmlException">不是有效的 OOXML 文档时抛出（中文消息）。</exception>
    public static WordDocument Parse(Stream stream)
    {
        using var package = OoxmlPackage.Open(stream);
        return ParseCore(package, "");
    }

    // ---------- 解析主流程 ----------

    private static WordDocument ParseCore(OoxmlPackage package, string sourcePath)
    {
        var mainPart = ResolveMainPart(package);
        var documentXml = package.ReadXml(mainPart);
        var body = documentXml.Root?.Element(W + "body")
            ?? throw new OoxmlException("word/document.xml 里没有正文（文件可能损坏）");

        var document = new WordDocument { SourcePath = sourcePath };

        // 标题 / 作者 / 修改时间都从 docProps/core.xml 读；标题缺失时退回文件名
        ReadCoreProperties(package, document);
        if (string.IsNullOrWhiteSpace(document.Title))
            document.Title = Path.GetFileNameWithoutExtension(sourcePath);

        var context = new ParseContext(package, document)
        {
            Styles = ReadStyles(package),
            Numbering = ReadNumbering(package),
        };

        foreach (var relationship in package.ReadRelationships(mainPart))
        {
            context.DocumentRelationships.TryAdd(relationship.Id, relationship);
        }

        // 按出现顺序解析正文：段落 / 表格 / 内容控件（sdt）都要展开。
        foreach (var element in body.Elements())
        {
            ParseBlockElement(element, document.BlockList, context);
        }

        // 页眉 / 页脚与脚注 / 尾注：都属于「缺部件就跳过」的可选内容，任何一处坏掉都不能影响正文
        var sections = CollectSectionProperties(body);
        ParseHeaderOrFooter(package, context, sections.FirstOrDefault(), isHeader: true);
        ParseHeaderOrFooter(package, context, sections.LastOrDefault(), isHeader: false);
        ParseNotesPart(package, "word/footnotes.xml", context, document.FootnoteList);
        ParseNotesPart(package, "word/endnotes.xml", context, document.EndnoteList);
        AssignNoteNumbers(document);

        return document;
    }

    /// <summary>
    /// 找主文档部件：优先读包根 <c>_rels/.rels</c> 里的 officeDocument 关系，
    /// 读不到再退回约定俗成的 <c>word/document.xml</c>（手写 / 精简的 docx 常常省掉根关系）。
    /// </summary>
    private static string ResolveMainPart(OoxmlPackage package)
    {
        foreach (var relationship in package.ReadRelationships(""))
        {
            if (relationship.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)
                && !relationship.IsExternal
                && package.Contains(relationship.Target))
            {
                return relationship.Target;
            }
        }

        if (package.Contains("word/document.xml")) return "word/document.xml";
        throw new OoxmlException("这不是有效的 Word 文档（缺少 word/document.xml 主部件）");
    }

    /// <summary>
    /// 读 <c>docProps/core.xml</c> 的标题 / 作者 / 最后修改者 / 创建与修改时间。
    /// <para>这个部件是「可以没有」的：缺失 / 坏 XML 时保持空值，界面退回文件名与文件大小，
    /// 不会因此拒绝打开文档。</para>
    /// </summary>
    private static void ReadCoreProperties(OoxmlPackage package, WordDocument document)
    {
        var core = package.TryReadXml("docProps/core.xml");
        if (core?.Root is null) return;

        string? ValueOf(string localName) => core.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == localName)?
            .Value;

        var title = ValueOf("title");
        if (!string.IsNullOrWhiteSpace(title)) document.Title = title.Trim();

        var author = ValueOf("creator");
        if (!string.IsNullOrWhiteSpace(author)) document.Author = author.Trim();

        var lastModifiedBy = ValueOf("lastModifiedBy");
        if (!string.IsNullOrWhiteSpace(lastModifiedBy)) document.LastModifiedBy = lastModifiedBy.Trim();

        document.CreatedUtc = ParseUtcDate(ValueOf("created"));
        document.ModifiedUtc = ParseUtcDate(ValueOf("modified"));
    }

    /// <summary>
    /// 把 <c>dcterms:created</c> / <c>dcterms:modified</c> 的 W3CDTF 时间（例如
    /// <c>2024-05-01T08:30:00Z</c>）解析成 UTC 时间；格式不认识时返回 <c>null</c>（界面就不显示时间）。
    /// </summary>
    private static DateTime? ParseUtcDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (DateTime.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    // ---------- 节属性 / 页眉 / 页脚 ----------

    /// <summary>
    /// 按文档顺序收集所有 <c>w:sectPr</c>：段落里的（分节符）和 body 末尾的那个（最后一节）。
    /// 连续浏览只需要「首个节的页眉」和「最后一个节的页脚」，所以只要顺序正确即可。
    /// </summary>
    private static List<XElement> CollectSectionProperties(XElement body)
    {
        var sections = new List<XElement>();
        foreach (var element in body.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "sectPr":
                    sections.Add(element);
                    break;

                case "p":
                    if (element.Element(W + "pPr")?.Element(W + "sectPr") is { } section)
                        sections.Add(section);
                    break;
            }
        }

        return sections;
    }

    /// <summary>
    /// 解析某一节的页眉 / 页脚部件：<c>w:headerReference</c> / <c>w:footerReference</c> → <c>r:id</c>
    /// → <c>word/_rels/document.xml.rels</c> → <c>word/headerN.xml</c> / <c>word/footerN.xml</c>，
    /// 然后**用该部件自己的关系表**解析里面的图片 / 超链接。
    ///
    /// <para>连续浏览的简化：首个节的页眉显示在文档顶部、最后一个节的页脚显示在文档底部，
    /// 不做「每页重复」。部件缺失 / 坏 XML 时静默跳过，正文照常显示。</para>
    /// </summary>
    private static void ParseHeaderOrFooter(
        OoxmlPackage package, ParseContext context, XElement? sectionProperties, bool isHeader)
    {
        if (sectionProperties is null) return;

        var partName = ResolveHeaderFooterPart(package, context, sectionProperties, isHeader);
        if (string.IsNullOrWhiteSpace(partName)) return;

        XDocument xml;
        try
        {
            xml = package.ReadXml(partName);
        }
        catch (OoxmlException)
        {
            context.NoteOnce($"说明：{(isHeader ? "页眉" : "页脚")}部件损坏，已跳过");
            return;
        }

        if (xml.Root is null) return;

        var relationships = ReadPartRelationships(package, partName);
        var target = isHeader ? context.Document.HeaderBlockList : context.Document.FooterBlockList;

        context.RunInOtherPart(partName, relationships, () =>
        {
            foreach (var element in xml.Root.Elements())
            {
                if (element.Name.LocalName is "p" or "tbl" or "sdt")
                    ParseBlockElement(element, target, context);
            }
        });
    }

    /// <summary>
    /// 在节的 <c>w:headerReference</c> / <c>w:footerReference</c> 里按「default → first → even」的顺序
    /// 选一个，再顺着关系表找到部件名；找不到关系 / 部件不存在时返回 <c>null</c>（跳过页眉页脚）。
    /// </summary>
    private static string? ResolveHeaderFooterPart(
        OoxmlPackage package, ParseContext context, XElement sectionProperties, bool isHeader)
    {
        var referenceName = isHeader ? W + "headerReference" : W + "footerReference";
        var references = sectionProperties.Elements(referenceName).ToList();
        if (references.Count == 0) return null;

        // 连续浏览只看一种页眉页脚：默认页优先，其次首页，最后偶数页
        foreach (var preferredType in new[] { "default", "first", "even" })
        {
            foreach (var reference in references)
            {
                var type = (string?)reference.Attribute(W + "type");
                if (!string.IsNullOrEmpty(type)
                    && !string.Equals(type, preferredType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relationshipId = (string?)reference.Attribute(R + "id");
                if (string.IsNullOrEmpty(relationshipId)) continue;
                if (!context.DocumentRelationships.TryGetValue(relationshipId, out var relationship))
                    continue;   // 关系文件缺失 / id 对不上：当作没有页眉页脚
                if (relationship.IsExternal || !package.Contains(relationship.Target)) continue;
                return relationship.Target;
            }
        }

        return null;
    }

    /// <summary>读某个部件自己的关系表（页眉页脚里的图片 / 超链接要用它）；关系文件坏了返回空表。</summary>
    private static Dictionary<string, OoxmlRelationship> ReadPartRelationships(
        OoxmlPackage package, string partName)
    {
        var relationships = new Dictionary<string, OoxmlRelationship>(StringComparer.Ordinal);
        try
        {
            foreach (var relationship in package.ReadRelationships(partName))
                relationships.TryAdd(relationship.Id, relationship);
        }
        catch (OoxmlException)
        {
            // 关系文件缺失 / 坏 XML：文字照样显示，只是图片 / 链接解析不到
        }

        return relationships;
    }

    // ---------- 脚注 / 尾注 ----------

    /// <summary>
    /// 解析 <c>word/footnotes.xml</c> / <c>word/endnotes.xml</c>：过滤 <c>w:id &lt; 1</c> 的分隔符、
    /// 按出现顺序收集内容块。部件缺失 / 坏 XML 时什么都不加（正文里的引用仍会显示 [n] 标记）。
    /// </summary>
    private static void ParseNotesPart(
        OoxmlPackage package, string partName, ParseContext context, List<WordNote> target)
    {
        var xml = package.TryReadXml(partName);
        if (xml?.Root is null) return;

        var relationships = ReadPartRelationships(package, partName);

        context.RunInOtherPart(partName, relationships, () =>
        {
            foreach (var noteElement in xml.Root.Elements())
            {
                var localName = noteElement.Name.LocalName;
                if (localName is not ("footnote" or "endnote")) continue;

                // 分隔符（-1 / 0）与 continuationSeparator 等特殊条目不是用户内容
                var id = ReadInt((string?)noteElement.Attribute(W + "id")) ?? 0;
                if (id < 1) continue;
                var type = (string?)noteElement.Attribute(W + "type");
                if (type is not null && type.Contains("separator", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (target.Count >= MaxNotes)
                {
                    context.NoteOnce($"说明：脚注 / 尾注超过安全上限（{MaxNotes} 条），后面的已省略");
                    break;
                }

                var note = new WordNote { Id = id };
                foreach (var child in noteElement.Elements())
                {
                    if (child.Name.LocalName is "p" or "tbl" or "sdt")
                        ParseBlockElement(child, note.Blocks, context);
                }

                // 脚注里只要求文本 / 段落 / 简单表格：图片块去掉（文档里也这么写）
                note.Blocks.RemoveAll(block => block is WordImageBlock);
                if (note.Blocks.Count == 0) continue;

                target.Add(note);
            }
        });
    }

    /// <summary>
    /// 给脚注 / 尾注编号：先按**正文引用出现的先后**编 1、2、3…（Word 里显示的就是这个顺序），
    /// 再把编号写回 run；没被任何引用指向的脚注排在最后，照样列出来。
    /// </summary>
    private static void AssignNoteNumbers(WordDocument document)
    {
        AssignNoteNumbers(document, document.FootnoteList, isFootnote: true);
        AssignNoteNumbers(document, document.EndnoteList, isFootnote: false);
    }

    private static void AssignNoteNumbers(WordDocument document, List<WordNote> notes, bool isFootnote)
    {
        // 即使脚注部件缺失（notes 为空）也要给正文引用编号：界面至少要显示 [n] 标记，不能什么都不显示
        // 第一遍：按正文顺序收集引用，得到「w:id → 显示编号」
        var numberById = new Dictionary<int, int>();
        var next = 1;
        foreach (var run in EnumerateRuns(document.BlockList))
        {
            var id = isFootnote ? run.FootnoteId : run.EndnoteId;
            if (id is null || id.Value < 1) continue;
            if (numberById.ContainsKey(id.Value)) continue;
            numberById[id.Value] = next++;
        }

        // 第二遍：编号写回 run（找不到内容的引用也照样给号，界面只显示标记）
        foreach (var run in EnumerateRuns(document.BlockList))
        {
            var id = isFootnote ? run.FootnoteId : run.EndnoteId;
            if (id is null || id.Value < 1) continue;
            if (!numberById.TryGetValue(id.Value, out var number)) continue;
            if (isFootnote) run.FootnoteNumber = number;
            else run.EndnoteNumber = number;
        }

        // 第三遍：脚注列表按显示编号排序；没被引用的接在后面
        foreach (var note in notes)
        {
            if (numberById.TryGetValue(note.Id, out var number)) note.Number = number;
        }

        foreach (var note in notes.Where(note => note.Number <= 0).ToList())
            note.Number = next++;
        notes.Sort((left, right) => left.Number.CompareTo(right.Number));
    }

    /// <summary>按文档顺序遍历所有段落里的 run（含表格单元格里的段落）。</summary>
    private static IEnumerable<WordRun> EnumerateRuns(IReadOnlyList<WordBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case WordParagraphBlock paragraph:
                    foreach (var run in paragraph.Runs) yield return run;
                    break;

                case WordTableBlock table:
                    foreach (var row in table.Rows)
                    {
                        foreach (var cell in row.Cells)
                        {
                            foreach (var run in EnumerateRuns(cell.Blocks)) yield return run;
                        }
                    }

                    break;
            }
        }
    }

    // ---------- 样式 ----------

    private sealed class StyleInfo
    {
        public string Name { get; init; } = "";
        public int HeadingLevel { get; init; }
        public WordTextAlignment? Alignment { get; init; }
        public double? LeftIndentTwip { get; init; }

        /// <summary>
        /// 样式里带的列表编号（<c>w:pPr/w:numPr</c>）。Word 用「List Bullet / 项目符号」这类**样式**
        /// 挂编号时，段落自己身上没有 numPr，只在样式里有 —— 不往下找的话列表会变成一行行普通文字。
        /// </summary>
        public string? NumberId { get; init; }

        /// <summary>样式里带的列表层级（<c>w:numPr/w:ilvl</c>，0 起）。</summary>
        public int IndentLevel { get; init; }
    }

    private static Dictionary<string, StyleInfo> ReadStyles(OoxmlPackage package)
    {
        var map = new Dictionary<string, StyleInfo>(StringComparer.OrdinalIgnoreCase);
        var styles = package.TryReadXml("word/styles.xml");
        if (styles is null) return map;

        foreach (var style in styles.Descendants(W + "style"))
        {
            // 只关心段落样式（字符样式的格式这里不展开，直接格式已经够显示）
            if (!string.Equals((string?)style.Attribute(W + "type"), "paragraph", StringComparison.OrdinalIgnoreCase))
                continue;

            var styleId = (string?)style.Attribute(W + "styleId");
            if (string.IsNullOrWhiteSpace(styleId)) continue;

            var name = (string?)style.Element(W + "name")?.Attribute(W + "val") ?? "";
            var paragraphProperties = style.Element(W + "pPr");
            var outlineLevel = ReadInt((string?)paragraphProperties?.Element(W + "outlineLvl")?.Attribute(W + "val"));
            var headingLevel = DetectHeadingLevel(name, styleId, outlineLevel);
            var styleNumbering = paragraphProperties?.Element(W + "numPr");

            map[styleId] = new StyleInfo
            {
                Name = name,
                HeadingLevel = headingLevel,
                Alignment = ReadAlignment((string?)paragraphProperties?.Element(W + "jc")?.Attribute(W + "val")),
                LeftIndentTwip = ReadIndentLeft(paragraphProperties?.Element(W + "ind")),
                NumberId = (string?)styleNumbering?.Element(W + "numId")?.Attribute(W + "val"),
                IndentLevel = ReadInt((string?)styleNumbering?.Element(W + "ilvl")?.Attribute(W + "val")) ?? 0,
            };
        }

        return map;
    }

    /// <summary>
    /// 判断一个段落样式是不是标题：样式名（中英文）、styleId、<c>outlineLvl</c> 三路兜底。
    /// <para>Word 中文版把标题 1 存成 <c>w:name="heading 1"</c>，但用户自己改过样式名的情况很多，
    /// 所以「标题 1」/「Heading 1」/「heading 1」/「heading1」都认；<c>outlineLvl</c> 则是
    /// 段落直接的「大纲级别」设置，最可靠。</para>
    /// </summary>
    private static int DetectHeadingLevel(string name, string styleId, int? outlineLevel)
    {
        var text = name.Trim();
        var level = MatchHeadingText(text) ?? MatchHeadingText(styleId);
        if (level.HasValue && level.Value is >= 1 and <= 6) return level.Value;

        // outlineLvl 从 0 开始，9 = 正文
        if (outlineLevel.HasValue && outlineLevel.Value is >= 0 and <= 5) return outlineLevel.Value + 1;
        return 0;
    }

    private static int? MatchHeadingText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var normalized = text.Trim().ToLowerInvariant().Replace(" ", "");

        if (normalized is "title" or "标题")
            return 1;

        if (normalized.StartsWith("heading", StringComparison.Ordinal))
        {
            return ParseLevelSuffix(normalized["heading".Length..]);
        }

        if (normalized.StartsWith("标题", StringComparison.Ordinal))
        {
            return ParseLevelSuffix(normalized["标题".Length..]);
        }

        return null;
    }

    /// <summary>把 "1" / "一" / "1级" 之类解析成 1 ~ 6；不认识返回 null。</summary>
    private static int? ParseLevelSuffix(string suffix)
    {
        if (suffix.Length == 0) return 1;
        var chinese = "一二三四五六";
        var first = suffix[0];
        if (char.IsAsciiDigit(first) && first >= '1' && first <= '9')
            return Math.Min(first - '0', 6);
        var chineseIndex = chinese.IndexOf(first);
        return chineseIndex >= 0 ? chineseIndex + 1 : null;
    }

    // ---------- 编号（列表标记） ----------

    private sealed class NumberingLevel
    {
        public string Format { get; init; } = "";
        public string Text { get; init; } = "";
        public int Start { get; init; } = 1;
    }

    private sealed class NumberingDefinition
    {
        public List<NumberingLevel> Levels { get; } = new();
    }

    private static Dictionary<string, NumberingDefinition> ReadNumbering(OoxmlPackage package)
    {
        // numId → abstractNumId → 各级定义
        var result = new Dictionary<string, NumberingDefinition>(StringComparer.OrdinalIgnoreCase);
        var numbering = package.TryReadXml("word/numbering.xml");
        if (numbering is null) return result;

        var abstracts = new Dictionary<string, NumberingDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var abstractNumber in numbering.Descendants(W + "abstractNum"))
        {
            var abstractId = (string?)abstractNumber.Attribute(W + "abstractNumId");
            if (string.IsNullOrWhiteSpace(abstractId)) continue;

            var definition = new NumberingDefinition();
            foreach (var level in abstractNumber.Elements(W + "lvl"))
            {
                // 按 ilvl 排序填好（缺级的用空定义占位），保证 Levels[ilvl] 对得上
                var index = ReadInt((string?)level.Attribute(W + "ilvl")) ?? definition.Levels.Count;
                while (definition.Levels.Count <= index) definition.Levels.Add(new NumberingLevel());
                definition.Levels[index] = new NumberingLevel
                {
                    Format = (string?)level.Element(W + "numFmt")?.Attribute(W + "val") ?? "",
                    Text = (string?)level.Element(W + "lvlText")?.Attribute(W + "val") ?? "",
                    Start = ReadInt((string?)level.Element(W + "start")?.Attribute(W + "val")) ?? 1,
                };
            }

            abstracts[abstractId] = definition;
        }

        foreach (var number in numbering.Descendants(W + "num"))
        {
            var numberId = (string?)number.Attribute(W + "numId");
            var abstractId = (string?)number.Element(W + "abstractNumId")?.Attribute(W + "val");
            if (string.IsNullOrWhiteSpace(numberId) || string.IsNullOrWhiteSpace(abstractId)) continue;
            if (abstracts.TryGetValue(abstractId, out var definition)) result[numberId] = definition;
        }

        return result;
    }

    /// <summary>
    /// 生成列表标记：项目符号给 <c>"• "</c>，编号按格式递增（decimal / 字母 / 罗马数字 / 中文数字）。
    /// Word 的编号规则（重启、起始值覆盖）很复杂，这里按「同一 numId 的同一级递增」这一条常见规则实现，
    /// 复杂编号可能和 Word 里显示的数字不完全一致 —— 阅读体验影响很小。
    /// </summary>
    private static string BuildListMarker(ParseContext context, string numberId, int level)
    {
        if (!context.Numbering.TryGetValue(numberId, out var definition)
            || level < 0 || level >= definition.Levels.Count)
        {
            return "• ";
        }

        var numberingLevel = definition.Levels[level];
        if (string.Equals(numberingLevel.Format, "bullet", StringComparison.OrdinalIgnoreCase))
            return "• ";

        if (!context.NumberingCounters.TryGetValue(numberId, out var counters))
        {
            counters = new Dictionary<int, int>();
            context.NumberingCounters[numberId] = counters;
        }

        var value = counters.TryGetValue(level, out var previous) ? previous + 1 : numberingLevel.Start;
        counters[level] = value;

        // 高级别递增后，更深的级别重新从起始值开始（和 Word 的观感一致）
        foreach (var deeper in counters.Keys.Where(key => key > level).ToList()) counters.Remove(deeper);

        var format = numberingLevel.Text;
        if (string.IsNullOrEmpty(format)) format = "%" + (level + 1) + ".";

        for (var slot = 1; slot <= 9; slot++)
        {
            var placeholder = "%" + slot;
            if (!format.Contains(placeholder, StringComparison.Ordinal)) continue;

            var slotLevel = slot - 1;
            var slotValue = slotLevel == level
                ? value
                : counters.TryGetValue(slotLevel, out var existing)
                    ? existing
                    : definition.Levels.ElementAtOrDefault(slotLevel)?.Start ?? 1;
            format = format.Replace(placeholder, FormatNumber(numberingLevel.Format, slotValue), StringComparison.Ordinal);
        }

        return format + " ";
    }

    private static string FormatNumber(string format, int value) => format.ToLowerInvariant() switch
    {
        "decimalzero" => value.ToString("00"),
        "lowerletter" => ToLetter(value).ToLowerInvariant(),
        "upperletter" => ToLetter(value).ToUpperInvariant(),
        "lowerroman" => ToRoman(value).ToLowerInvariant(),
        "upperroman" => ToRoman(value).ToUpperInvariant(),
        "chinesecounting" or "chinesecountingthousand" or "chineselegalsimplified" => ToChineseNumber(value),
        _ => value.ToString(),
    };

    private static string ToLetter(int value)
    {
        // 1 → a、26 → z、27 → aa
        if (value <= 0) return "";
        var letter = (char)('a' + (value - 1) % 26);
        var repeat = (value - 1) / 26 + 1;
        return new string(letter, Math.Clamp(repeat, 1, 4));
    }

    private static string ToRoman(int value)
    {
        if (value <= 0 || value > 3999) return value.ToString();
        var pairs = new (int Value, string Text)[]
        {
            (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"),
            (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"),
        };

        var builder = new System.Text.StringBuilder();
        foreach (var (number, text) in pairs)
        {
            while (value >= number)
            {
                builder.Append(text);
                value -= number;
            }
        }

        return builder.ToString();
    }

    private static string ToChineseNumber(int value)
    {
        if (value <= 0 || value >= 100) return value.ToString();
        string[] digits = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        if (value < 10) return digits[value];
        var tens = value / 10;
        var ones = value % 10;
        return (tens == 1 ? "" : digits[tens]) + "十" + (ones == 0 ? "" : digits[ones]);
    }

    // ---------- 正文块 ----------

    /// <summary>解析 body / 单元格 / 内容控件里的一级子元素（段落、表格、嵌套 sdt）。</summary>
    private static void ParseBlockElement(XElement element, List<WordBlock> target, ParseContext context)
    {
        switch (element.Name.LocalName)
        {
            case "p":
            {
                var images = new List<WordImageBlock>();
                var paragraph = ParseParagraph(element, images, context);

                var countBefore = target.Count;
                context.AddBlock(target, paragraph);

                // 只有正文顶层的标题段落进大纲（表格单元格里的标题不参与侧栏）
                if (target.Count > countBefore && ReferenceEquals(target, context.Document.BlockList))
                    AddHeadingOutline(context, paragraph);

                foreach (var image in images)
                    context.AddBlock(target, image);
                break;
            }

            case "tbl":
                context.AddBlock(target, ParseTable(element, context));
                break;

            case "sdt":
            {
                // 内容控件：真正的内容在 w:sdtContent 里
                var content = element.Element(W + "sdtContent");
                if (content is not null)
                {
                    foreach (var child in content.Elements()) ParseBlockElement(child, target, context);
                }

                break;
            }

            case "sectPr":
            case "bookmarkStart":
            case "bookmarkEnd":
                break;

            default:
            {
                // AlternateContent / customXml 之类：只要里面还有段落 / 表格就继续展开
                foreach (var child in element.Elements())
                {
                    if (child.Name.LocalName is "p" or "tbl" or "sdt")
                        ParseBlockElement(child, target, context);
                }

                break;
            }
        }
    }

    private static WordParagraphBlock ParseParagraph(
        XElement paragraphElement, List<WordImageBlock> images, ParseContext context)
    {        var block = new WordParagraphBlock();
        var properties = paragraphElement.Element(W + "pPr");

        var styleId = (string?)properties?.Element(W + "pStyle")?.Attribute(W + "val");
        var style = styleId is not null && context.Styles.TryGetValue(styleId, out var found) ? found : null;

        // 标题级别：样式判定为准；段落里直接写的 outlineLvl 也认（两者取较大值）
        var level = style?.HeadingLevel ?? 0;
        var directOutline = ReadInt((string?)properties?.Element(W + "outlineLvl")?.Attribute(W + "val"));
        if (directOutline.HasValue && directOutline.Value is >= 0 and <= 5)
            level = Math.Max(level, directOutline.Value + 1);
        block.Style.HeadingLevel = level;

        block.Style.Alignment = ReadAlignment((string?)properties?.Element(W + "jc")?.Attribute(W + "val"))
            ?? style?.Alignment
            ?? WordTextAlignment.Left;

        var indentTwip = ReadIndentLeft(properties?.Element(W + "ind")) ?? style?.LeftIndentTwip;
        if (indentTwip.HasValue && indentTwip.Value > 0)
            block.Style.LeftIndentDip = OoxmlPackage.TwipToDip(indentTwip.Value);

        block.Style.IsPageBreakBefore = GetOnOff(properties?.Element(W + "pageBreakBefore"));

        // 列表：取 numPr 的 numId + ilvl，生成「• 」或递增编号。
        // ⚠ Word 内置的「项目符号 / 编号」列表样式把 numPr 写在 styles.xml 的样式里，
        // 段落自己身上没有 —— 直接只读段落会得到「列表变成一行行普通文字」，所以样式里也要兜底。
        var numberingProperties = properties?.Element(W + "numPr");
        var numberId = (string?)numberingProperties?.Element(W + "numId")?.Attribute(W + "val");
        var indentLevel = ReadInt((string?)numberingProperties?.Element(W + "ilvl")?.Attribute(W + "val"));

        if (string.IsNullOrEmpty(numberId) || numberId == "0")
        {
            numberId = style?.NumberId;
            indentLevel ??= style?.IndentLevel ?? 0;
        }

        if (!string.IsNullOrEmpty(numberId) && numberId != "0")
        {
            block.Style.IndentLevel = Math.Clamp(indentLevel ?? 0, 0, 8);
            block.Style.ListMarker = BuildListMarker(context, numberId, block.Style.IndentLevel);
        }

        ParseInline(paragraphElement, block, images, context, hyperlink: false, url: null,
            forcePageBreak: block.Style);

        return block;
    }

    /// <summary>
    /// 把标题段落记进大纲。<b>只收顶层正文段落</b>（表格单元格里的标题不进侧栏），
    /// 级别夹到 1 ~ 6；文字全空的标题跳过（点它也没有可见内容可以滚过去）。
    /// </summary>
    private static void AddHeadingOutline(ParseContext context, WordParagraphBlock paragraph)
    {
        var level = paragraph.Style.HeadingLevel;
        if (level < 1 || level > 6) return;

        var text = paragraph.Text.Trim();
        if (text.Length == 0) return;

        context.Document.OutlineList.Add(new WordHeading(level, text, paragraph.Index));
    }

    /// <summary>递归遍历段落的行内内容（run / 超链接 / 修订 / 域 / 内容控件）。</summary>
    private static void ParseInline(
        XElement container, WordParagraphBlock block, List<WordImageBlock> images,
        ParseContext context, bool hyperlink, string? url, WordParagraphStyle? forcePageBreak = null)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "r":
                    ParseRun(element, block, images, context, hyperlink, url, forcePageBreak);
                    break;

                case "hyperlink":
                {
                    var relationshipId = (string?)element.Attribute(R + "id");
                    string? target = null;
                    if (relationshipId is not null
                        && context.Relationships.TryGetValue(relationshipId, out var relationship)
                        && relationship.IsExternal)
                    {
                        target = relationship.Target;
                    }

                    ParseInline(element, block, images, context, hyperlink: true, url: target, forcePageBreak);
                    break;
                }

                case "fldSimple":
                {
                    var instruction = (string?)element.Attribute(W + "instr");
                    var target = ParseHyperlinkInstruction(instruction);
                    ParseInline(element, block, images, context, hyperlink: target is not null, url: target,
                        forcePageBreak);
                    break;
                }

                case "ins":          // 修订里的「插入」保留（和 Word 显示一致）
                case "moveTo":
                case "smartTag":
                case "customXml":
                case "sdtContent":
                    ParseInline(element, block, images, context, hyperlink, url, forcePageBreak);
                    break;

                case "sdt":
                {
                    var content = element.Element(W + "sdtContent");
                    if (content is not null)
                        ParseInline(content, block, images, context, hyperlink, url, forcePageBreak);
                    break;
                }

                case "del":          // 修订里的「删除」不显示
                case "moveFrom":
                case "pPr":
                case "instrText":    // 域代码本身不显示（域结果在 w:t 里）
                case "delText":
                case "bookmarkStart":
                case "bookmarkEnd":
                case "proofErr":
                    break;

                default:
                    // 不认识的容器（AlternateContent 等）如果里面还有 run，就继续展开
                    if (element.Elements().Any(child => child.Name.LocalName is "r" or "hyperlink" or "sdt"))
                        ParseInline(element, block, images, context, hyperlink, url, forcePageBreak);
                    break;
            }
        }
    }

    private static void ParseRun(
        XElement runElement, WordParagraphBlock block, List<WordImageBlock> images,
        ParseContext context, bool hyperlink, string? url, WordParagraphStyle? forcePageBreak)
    {
        var properties = runElement.Element(W + "rPr");
        var text = new System.Text.StringBuilder();
        var hasPageBreak = false;

        // 脚注 / 尾注引用：引用本身不占正文文字，只记下 w:id，编号在正文全部解析完后统一分配
        int? footnoteId = null;
        int? endnoteId = null;

        foreach (var child in runElement.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "footnoteReference":
                    footnoteId = ReadInt((string?)child.Attribute(W + "id"));
                    break;

                case "endnoteReference":
                    endnoteId = ReadInt((string?)child.Attribute(W + "id"));
                    break;
                case "t":
                    text.Append(child.Value);
                    break;

                case "tab":
                    text.Append('\t');
                    break;

                case "br":
                {
                    // 分页符：在段落上标记「段前分页」；普通换行当软回车
                    if (string.Equals((string?)child.Attribute(W + "type"), "page", StringComparison.OrdinalIgnoreCase))
                        hasPageBreak = true;
                    else
                        text.Append('\n');
                    break;
                }

                case "cr":
                    text.Append('\n');
                    break;

                case "noBreakHyphen":
                    text.Append('-');
                    break;

                case "drawing":
                    TryAddDrawing(child, images, context);
                    break;

                case "pict":
                    TryAddVmlImage(child, images, context);
                    break;

                case "AlternateContent":
                {
                    foreach (var drawing in child.Descendants(W + "drawing")) TryAddDrawing(drawing, images, context);
                    foreach (var pict in child.Descendants(W + "pict")) TryAddVmlImage(pict, images, context);
                    break;
                }

                // 旧格式的嵌入对象 / 批注引用 / 域字符：不支持，跳过
                case "instrText":
                case "delText":
                case "fldChar":
                case "object":
                case "commentReference":
                case "sym":
                case "softHyphen":
                    break;
            }
        }

        if (hasPageBreak && forcePageBreak is not null) forcePageBreak.IsPageBreakBefore = true;

        // 只有脚注 / 尾注引用的 run 没有文字，但必须在模型里留一个 run（渲染器据此补 [n] 上标）
        var hasNoteReference = footnoteId is >= 1 || endnoteId is >= 1;
        if (text.Length == 0 && !hasNoteReference) return;

        // w:sz 是「半点」：28 → 14 磅
        var halfPoints = ReadInt((string?)properties?.Element(W + "sz")?.Attribute(W + "val"));

        block.Runs.Add(new WordRun
        {
            Text = text.ToString(),
            Bold = GetOnOff(properties?.Element(W + "b")),
            Italic = GetOnOff(properties?.Element(W + "i")),
            Underline = ReadUnderline(properties?.Element(W + "u")),
            Strike = GetOnOff(properties?.Element(W + "strike")) || GetOnOff(properties?.Element(W + "dstrike")),
            IsSuperscript = string.Equals((string?)properties?.Element(W + "vertAlign")?.Attribute(W + "val"),
                "superscript", StringComparison.OrdinalIgnoreCase),
            IsSubscript = string.Equals((string?)properties?.Element(W + "vertAlign")?.Attribute(W + "val"),
                "subscript", StringComparison.OrdinalIgnoreCase),
            IsHyperlink = hyperlink,
            Url = url,
            ColorHex = NormalizeColor((string?)properties?.Element(W + "color")?.Attribute(W + "val")),
            FontFamily = ReadFontFamily(properties?.Element(W + "rFonts")),
            FontSizePt = halfPoints.HasValue && halfPoints.Value > 0 ? halfPoints.Value / 2.0 : null,
            HighlightHex = MapHighlight((string?)properties?.Element(W + "highlight")?.Attribute(W + "val")),
            FootnoteId = footnoteId is >= 1 ? footnoteId : null,
            EndnoteId = endnoteId is >= 1 ? endnoteId : null,
        });
    }

    // ---------- 图片 ----------

    private static void TryAddDrawing(XElement drawing, List<WordImageBlock> images, ParseContext context)
    {
        var blip = drawing.Descendants(A + "blip").FirstOrDefault(element => element.Attribute(R + "embed") is not null);
        var relationshipId = (string?)blip?.Attribute(R + "embed");
        var data = context.ReadImage(relationshipId);
        if (data is null) return;

        var image = new WordImageBlock
        {
            Data = data.Value.Bytes,
            ContentType = data.Value.ContentType,
        };

        // 显示尺寸：wp:extent（个别精简 docx 只写 a:ext），EMU → DIP
        var extent = drawing.Descendants(WP + "extent").FirstOrDefault()
            ?? drawing.Descendants(A + "ext").FirstOrDefault();
        if (extent is not null)
        {
            image.WidthDip = OoxmlPackage.EmuToDip(ReadLong((string?)extent.Attribute("cx")) ?? 0);
            image.HeightDip = OoxmlPackage.EmuToDip(ReadLong((string?)extent.Attribute("cy")) ?? 0);
        }

        var description = drawing.Descendants(WP + "docPr").FirstOrDefault();
        image.AltText = (string?)description?.Attribute("descr")
            ?? (string?)description?.Attribute("title");

        images.Add(image);
    }

    /// <summary>旧式 VML 图片（<c>w:pict/v:imagedata</c>）：没有尺寸信息，交给渲染器按原始像素显示。</summary>
    private static void TryAddVmlImage(XElement pict, List<WordImageBlock> images, ParseContext context)
    {
        var imageData = pict.Descendants().FirstOrDefault(element => element.Name.LocalName == "imagedata");
        var relationshipId = (string?)imageData?.Attribute(R + "id");
        var data = context.ReadImage(relationshipId);
        if (data is null) return;

        images.Add(new WordImageBlock { Data = data.Value.Bytes, ContentType = data.Value.ContentType });
    }

    // ---------- 表格 ----------

    private static WordTableBlock ParseTable(XElement tableElement, ParseContext context)
    {
        var table = new WordTableBlock();

        // 列宽（twip → DIP）
        var grid = tableElement.Element(W + "tblGrid");
        if (grid is not null)
        {
            foreach (var column in grid.Elements(W + "gridCol"))
            {
                var width = ReadDouble((string?)column.Attribute(W + "w"));
                if (width.HasValue && width.Value > 0)
                    table.ColumnWidthDip.Add(OoxmlPackage.TwipToDip(width.Value));
            }
        }

        // 每个单元格的解析状态：colspan / vMerge 需要跨行事后推算 RowSpan
        var rows = new List<(WordTableRow Row, List<(WordTableCell Cell, int Column, bool MergeStart, bool MergeContinue)> Cells)>();
        var nestedTableReported = false;

        foreach (var rowElement in tableElement.Elements(W + "tr"))
        {
            if (context.TableCellCount >= MaxTableCells)
            {
                context.NoteOnce("说明：表格单元格超过安全上限，后面的内容已省略");
                break;
            }

            var row = new WordTableRow
            {
                IsHeader = GetOnOff(rowElement.Element(W + "trPr")?.Element(W + "tblHeader")),
            };

            var cells = new List<(WordTableCell Cell, int Column, bool MergeStart, bool MergeContinue)>();
            var column = 0;

            foreach (var cellElement in rowElement.Elements(W + "tc"))
            {
                if (context.TableCellCount >= MaxTableCells) break;
                context.TableCellCount++;

                var properties = cellElement.Element(W + "tcPr");
                var columnSpan = Math.Clamp(ReadInt((string?)properties?.Element(W + "gridSpan")?.Attribute(W + "val")) ?? 1, 1, 100);

                var merge = properties?.Element(W + "vMerge");
                var mergeValue = (string?)merge?.Attribute(W + "val");
                var mergeContinue = merge is not null
                    && (string.IsNullOrEmpty(mergeValue)
                        || string.Equals(mergeValue, "continue", StringComparison.OrdinalIgnoreCase));
                var mergeStart = merge is not null && !mergeContinue;

                var cell = new WordTableCell
                {
                    ColumnSpan = columnSpan,
                    ShadingHex = NormalizeColor((string?)properties?.Element(W + "shd")?.Attribute(W + "fill")),
                    IsHeader = row.IsHeader,
                    IsMergedContinuation = mergeContinue,
                };

                foreach (var child in cellElement.Elements())
                {
                    if (child.Name.LocalName == "tcPr") continue;
                    if (child.Name.LocalName == "tbl")
                    {
                        // 嵌套表格不支持：每个单元格只提示一次，避免日志刷屏
                        if (!nestedTableReported)
                        {
                            context.NoteOnce("说明：表格里嵌套的表格已省略（只保留外层表格）");
                            nestedTableReported = true;
                        }

                        continue;
                    }

                    if (child.Name.LocalName is "p" or "sdt")
                        ParseBlockElement(child, cell.Blocks, context);
                }

                row.Cells.Add(cell);
                cells.Add((cell, column, mergeStart, mergeContinue));
                column += columnSpan;
            }

            rows.Add((row, cells));
        }

        // 纵向合并：从 restart 往下数，连续 continue 的行数就是 RowSpan
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            foreach (var (cell, column, mergeStart, _) in rows[rowIndex].Cells)
            {
                if (!mergeStart) continue;

                var span = 1;
                for (var nextRow = rowIndex + 1; nextRow < rows.Count; nextRow++)
                {
                    var continues = rows[nextRow].Cells.Any(candidate =>
                        candidate.Column == column && candidate.MergeContinue);
                    if (!continues) break;
                    span++;
                }

                cell.RowSpan = span;
            }
        }

        foreach (var (row, _) in rows) table.Rows.Add(row);
        return table;
    }

    // ---------- 小工具 ----------

    private static int? ReadInt(string? text) => int.TryParse(text, out var value) ? value : null;

    private static long? ReadLong(string? text) => long.TryParse(text, out var value) ? value : null;

    private static double? ReadDouble(string? text)
        => double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>读 <c>w:ind</c> 的左缩进（兼容旧写法 left 与新写法 start）。</summary>
    private static double? ReadIndentLeft(XElement? indent)
    {
        if (indent is null) return null;
        var left = ReadDouble((string?)indent.Attribute(W + "left"))
            ?? ReadDouble((string?)indent.Attribute(W + "start"));
        return left is > 0 ? left : null;
    }

    /// <summary>OOXML 的开关属性：元素在 = 开，<c>w:val="0"/"false"/"off"/"none"</c> = 关。</summary>
    private static bool GetOnOff(XElement? element)
    {
        if (element is null) return false;
        var value = (string?)element.Attribute(W + "val");
        if (string.IsNullOrEmpty(value)) return true;
        return value is not ("0" or "false" or "off" or "none");
    }

    private static bool ReadUnderline(XElement? underline)
    {
        if (underline is null) return false;
        var value = (string?)underline.Attribute(W + "val");
        return !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static WordTextAlignment? ReadAlignment(string? value) => value switch
    {
        "center" => WordTextAlignment.Center,
        "right" or "end" => WordTextAlignment.Right,
        "both" or "distribute" or "thaiDistribute" => WordTextAlignment.Justify,
        "left" or "start" => WordTextAlignment.Left,
        _ => null,
    };

    private static string? ReadFontFamily(XElement? fonts)
    {
        if (fonts is null) return null;
        foreach (var attributeName in new[] { "ascii", "hAnsi", "eastAsia", "cs" })
        {
            var value = (string?)fonts.Attribute(W + attributeName);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        return null;
    }

    /// <summary>把 <c>w:color</c> / <c>w:shd</c> 的值规范成 <c>#RRGGBB</c>；auto / 非法值返回 null。</summary>
    private static string? NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (string.Equals(text, "auto", StringComparison.OrdinalIgnoreCase)) return null;

        // 个别生成器会带 # 或 8 位带 alpha，只取后 6 位
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length == 8) text = text[2..];
        if (text.Length != 6 || !text.All(Uri.IsHexDigit)) return null;
        return "#" + text.ToUpperInvariant();
    }

    /// <summary><c>w:highlight</c> 给的是颜色名，换成十六进制给 WPF 当 <c>Run.Background</c>。</summary>
    private static string? MapHighlight(string? value) => value?.ToLowerInvariant() switch
    {
        "yellow" => "#FFFF00",
        "green" => "#00FF00",
        "cyan" => "#00FFFF",
        "magenta" => "#FF00FF",
        "blue" => "#0000FF",
        "red" => "#FF0000",
        "darkblue" => "#000080",
        "darkcyan" => "#008080",
        "darkgreen" => "#008000",
        "darkmagenta" => "#800080",
        "darkred" => "#800000",
        "darkyellow" => "#808000",
        "darkgray" => "#A9A9A9",
        "lightgray" => "#D3D3D3",
        "black" => "#000000",
        "white" => "#FFFFFF",
        _ => null,
    };

    /// <summary>从 <c>w:fldSimple</c> 的指令里抠出 <c>HYPERLINK "地址"</c> 的地址。</summary>
    private static string? ParseHyperlinkInstruction(string? instruction)
    {
        if (string.IsNullOrWhiteSpace(instruction)) return null;
        var marker = instruction.IndexOf("HYPERLINK", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return null;

        var rest = instruction[(marker + "HYPERLINK".Length)..];
        var open = rest.IndexOf('"');
        if (open < 0) return null;
        var close = rest.IndexOf('"', open + 1);
        if (close < 0) return null;

        var target = rest[(open + 1)..close].Trim();
        return target.Length == 0 ? null : target;
    }

    // ---------- 解析上下文（把包 / 关系 / 缓存 / 计数收在一起） ----------

    private sealed class ParseContext
    {
        private readonly OoxmlPackage _package;
        private readonly Dictionary<string, (byte[] Bytes, string ContentType)> _imageCache = new(StringComparer.Ordinal);
        private long _totalImageBytes;
        private int _imageCount;
        private int _totalBlocks;
        private readonly HashSet<string> _reportedNotes = new(StringComparer.Ordinal);

        public ParseContext(OoxmlPackage package, WordDocument document)
        {
            _package = package;
            Document = document;
            _relationships = DocumentRelationships;   // 默认按主文档的关系表解析
        }

        public WordDocument Document { get; }

        public Dictionary<string, StyleInfo> Styles { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, NumberingDefinition> Numbering { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>主文档 <c>word/document.xml</c> 的关系表（页眉页脚 / 脚注部件名都从它查）。</summary>
        public Dictionary<string, OoxmlRelationship> DocumentRelationships { get; } = new(StringComparer.Ordinal);

        /// <summary>当前正在解析的部件的关系表：正文是主文档，页眉页脚 / 脚注按部件切换。</summary>
        public Dictionary<string, OoxmlRelationship> Relationships => _relationships;

        private Dictionary<string, OoxmlRelationship> _relationships;

        /// <summary>当前部件名（图片缓存按「部件 + 关系 Id」区分，避免不同部件的 rId 撞车）。</summary>
        public string CurrentPartName { get; private set; } = "word/document.xml";

        /// <summary>是否把块计入文档统计（正文计；页眉页脚 / 脚注不计，否则状态栏数字会虚高）。</summary>
        public bool CountInDocument { get; private set; } = true;

        /// <summary>同级同 numId 的编号计数器。</summary>
        public Dictionary<string, Dictionary<int, int>> NumberingCounters { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>已解析的单元格总数（安全上限用）。</summary>
        public int TableCellCount { get; set; }

        /// <summary>把块加进目标列表，顺便维护块下标、计数与安全上限。</summary>
        public void AddBlock(List<WordBlock> target, WordBlock block)
        {
            if (_totalBlocks >= MaxBlocks)
            {
                NoteOnce($"说明：文档内容超过安全上限（{MaxBlocks} 个内容块），后面的内容已省略");
                return;
            }

            block.Index = target.Count;
            target.Add(block);

            // 页眉页脚 / 脚注的内容块不算进正文统计（但同样受总块数上限保护）
            if (!CountInDocument) return;

            _totalBlocks++;

            switch (block)
            {
                case WordParagraphBlock:
                    Document.ParagraphCount++;
                    break;
                case WordTableBlock:
                    Document.TableCount++;
                    break;
                case WordImageBlock:
                    _imageCount++;
                    Document.ImageCount = _imageCount;
                    break;
            }
        }

        /// <summary>
        /// 临时切到另一个部件（页眉 / 页脚 / 脚注）解析：换用该部件自己的关系表与图片缓存命名空间，
        /// 同时关掉「计入正文统计」。解析完自动恢复现场。
        /// </summary>
        public void RunInOtherPart(
            string partName, Dictionary<string, OoxmlRelationship> relationships, Action action)
        {
            var previousPart = CurrentPartName;
            var previousRelationships = _relationships;
            var previousCount = CountInDocument;

            CurrentPartName = partName;
            _relationships = relationships;
            CountInDocument = false;
            try
            {
                action();
            }
            finally
            {
                CurrentPartName = previousPart;
                _relationships = previousRelationships;
                CountInDocument = previousCount;
            }
        }

        /// <summary>按关系 Id 读图片字节（同一张图片只解压一次；超上限返回 null 并记录说明）。</summary>
        public (byte[] Bytes, string ContentType)? ReadImage(string? relationshipId)
        {
            if (string.IsNullOrWhiteSpace(relationshipId)) return null;

            // 不同部件里的关系 Id 可能重名（页眉的 rId1 ≠ 正文的 rId1），缓存键要带上部件名
            var cacheKey = CurrentPartName + "|" + relationshipId;
            if (_imageCache.TryGetValue(cacheKey, out var cached)) return cached;

            if (!Relationships.TryGetValue(relationshipId, out var relationship) || relationship.IsExternal)
                return null;   // 关系缺失 / 外链图片：跳过（不能联网取图）

            if (_imageCount >= MaxImages)
            {
                NoteOnce($"说明：图片数量超过安全上限（{MaxImages} 张），后面的图片已省略");
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = _package.ReadBytes(relationship.Target);
            }
            catch (OoxmlException)
            {
                return null;   // 图片部件缺失 / 损坏：跳过这一张，不影响整篇
            }

            if (bytes.Length == 0) return null;
            if (_totalImageBytes + bytes.Length > MaxTotalImageBytes)
            {
                NoteOnce("说明：图片总体积超过安全上限（48 MB），后面的图片已省略");
                return null;
            }

            _totalImageBytes += bytes.Length;
            var image = (bytes, ContentTypeFromPath(relationship.Target));
            _imageCache[cacheKey] = image;
            return image;
        }

        /// <summary>同一个说明只记一次，避免一节里几百个不支持元素把日志刷满。</summary>
        public void NoteOnce(string note)
        {
            if (_reportedNotes.Add(note)) Document.NoteList.Add(note);
        }

        private static string ContentTypeFromPath(string partName) =>
            Path.GetExtension(partName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".gif" => "image/gif",
                ".bmp" => "image/bmp",
                ".tif" or ".tiff" => "image/tiff",
                ".svg" => "image/svg+xml",
                ".emf" => "image/x-emf",
                ".wmf" => "image/x-wmf",
                _ => "application/octet-stream",
            };
    }
}
