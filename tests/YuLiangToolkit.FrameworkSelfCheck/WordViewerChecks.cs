using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.WordViewer;
using YuLiangToolkit.Tools.WordViewer.Parsing;
using YuLiangToolkit.Tools.WordViewer.Rendering;
using YuLiangToolkit.Tools.WordViewer.ViewModels;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「Word 阅读器」自检：手写 docx 解析（段落 / 字符格式 / 标题大纲 / 表格合并 / 图片 / 链接）、
/// 纯逻辑查找、配置与最近列表、渲染器映射。
///
/// <para><b>不弹窗、不碰真实 Data/Config</b>：在系统临时目录里用 <see cref="ZipArchive"/> 现造一个最小 docx，
/// 跑完 <c>finally</c> 删掉；配置对象直接 <c>new</c>，不经过 <c>ToolConfigStore</c>。</para>
/// </summary>
internal static class WordViewerChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        var root = Path.Combine(Path.GetTempPath(), "ylt-word-viewer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            CheckMetadata(check, report);

            var docxPath = Path.Combine(root, "自检文档.docx");
            CreateMinimalDocx(docxPath);

            var document = WordDocumentParser.Parse(docxPath);

            CheckParagraphs(check, document);
            CheckOutline(check, document);
            CheckTable(check, document);
            CheckImage(check, document);
            CheckCounts(check, document);
            CheckSearch(check, document);
            CheckRenderer(check, document);

            CheckExtensionsAndErrors(check, root, docxPath, document);
            CheckConfig(check, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch { /* 临时目录清理失败忽略 */ }
        }
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check, Action<string, bool> report)
    {
        var tool = new WordViewerTool();
        check("[Word阅读器] Id / 名称 / 图标 / 分类 / 排序符合约定",
            tool.Id == "word-viewer" && tool.Name == "Word 阅读器" && tool.EmojiIcon == "📘"
            && tool.Category == "文档" && tool.Order == 18,
            $"{tool.Id} / {tool.Name} / {tool.EmojiIcon} / {tool.Category} / {tool.Order}");

        // 这条没有细节要展示，用 report 版（顺带验证另一个重载）
        report("[Word阅读器] 描述明确写了「只读」",
            tool.Description.Contains("只读", StringComparison.Ordinal));

        check("[Word阅读器] 标签覆盖 Word / docx / 只读 / 大纲 / 缩放 / 查找",
            tool.Tags.Contains("Word") && tool.Tags.Contains("docx") && tool.Tags.Contains("文档")
            && tool.Tags.Contains("阅读器") && tool.Tags.Contains("只读") && tool.Tags.Contains("浏览")
            && tool.Tags.Contains("大纲") && tool.Tags.Contains("缩放") && tool.Tags.Contains("查找"),
            string.Join(" / ", tool.Tags));

        check("[Word阅读器] 配置里的 ToolId 与工具 Id 一致（Config 文件夹名不会错位）",
            WordViewerConfig.ToolId == tool.Id, WordViewerConfig.ToolId);
    }

    // ---------------------------------------------------------------- 段落 / 字符格式

    private static void CheckParagraphs(Action<string, bool, string> check, WordDocument document)
    {
        var heading = document.Blocks.FirstOrDefault(block =>
            block is WordParagraphBlock paragraph && paragraph.Text.Contains("第一章 简介", StringComparison.Ordinal))
            as WordParagraphBlock;
        check("[Word阅读器] 样式名「heading 1」识别成 1 级标题",
            heading is not null && heading.Style.HeadingLevel == 1,
            heading is null ? "没找到标题段落" : $"HeadingLevel={heading.Style.HeadingLevel}");

        check("[Word阅读器] 段落文字 / 制表符 w:tab 转成 \\t",
            heading?.Text == "第一章 简介"
            && document.Blocks.OfType<WordParagraphBlock>().Any(paragraph => paragraph.Text.Contains('\t')),
            heading?.Text ?? "");

        var formatParagraph = document.Blocks.OfType<WordParagraphBlock>()
            .FirstOrDefault(paragraph => paragraph.Text.Contains("彩色上标", StringComparison.Ordinal));
        var boldRun = formatParagraph?.Runs.FirstOrDefault(run => run.Text == "格式");
        check("[Word阅读器] 字符格式：粗体 / 斜体 / 下划线 / 删除线同时识别",
            boldRun is { Bold: true, Italic: true, Underline: true, Strike: true },
            boldRun is null ? "没找到「格式」run" : $"B={boldRun.Bold} I={boldRun.Italic} U={boldRun.Underline} S={boldRun.Strike}");

        var colorRun = formatParagraph?.Runs.FirstOrDefault(run => run.Text == "彩色上标");
        check("[Word阅读器] 字符格式：颜色 / 字号（半点→磅）/ 字体 / 上标",
            colorRun is { ColorHex: "#FF0000", FontSizePt: 14.0, FontFamily: "Consolas", IsSuperscript: true },
            colorRun is null
                ? "没找到「彩色上标」run"
                : $"{colorRun.ColorHex} / {colorRun.FontSizePt}pt / {colorRun.FontFamily} / 上标={colorRun.IsSuperscript}");

        var hyperlinkParagraph = document.Blocks.OfType<WordParagraphBlock>()
            .FirstOrDefault(paragraph => paragraph.Text.Contains("示例链接", StringComparison.Ordinal));
        var hyperlinkRun = hyperlinkParagraph?.Runs.FirstOrDefault(run => run.Text == "示例链接");
        check("[Word阅读器] 超链接：r:id → 外部地址，run 标记成链接",
            hyperlinkRun is { IsHyperlink: true, Url: "https://example.com/docs" },
            hyperlinkRun?.Url ?? "没找到链接 run");

        var listParagraphs = document.Blocks.OfType<WordParagraphBlock>()
            .Where(paragraph => paragraph.Text is "列表项一" or "列表项二")
            .ToList();
        check("[Word阅读器] 编号列表：同一 numId 递增，标记分别是「1. 」「2. 」",
            listParagraphs.Count == 2
            && listParagraphs[0].Style.ListMarker == "1. "
            && listParagraphs[1].Style.ListMarker == "2. ",
            string.Join(" | ", listParagraphs.Select(paragraph => paragraph.Style.ListMarker)));

        // Word 把「项目符号 / 编号」列表的 numPr 写在样式里（styles.xml）而不是段落上，
        // 只读段落会漏掉标记 —— 这条钉住「样式里的 numPr 也要兜底」。
        var styleList = document.Blocks.OfType<WordParagraphBlock>()
            .FirstOrDefault(paragraph => paragraph.Text == "样式项目符号");
        check("[Word阅读器] 列表样式带 numPr：段落自身没有 numPr 也能出「• 」标记",
            styleList is not null && (styleList.Style.ListMarker ?? "").Contains('•'),
            styleList is null ? "没找到「样式项目符号」段落" : $"marker={styleList.Style.ListMarker ?? "（空）"}");

        var centered = document.Blocks.OfType<WordParagraphBlock>()
            .FirstOrDefault(paragraph => paragraph.Text == "居中缩进");
        check("[Word阅读器] 段落格式：w:jc 居中 + w:ind 左缩进（720 twip = 48 DIP）",
            centered is not null && centered.Style.Alignment == WordTextAlignment.Center
            && Math.Abs(centered.Style.LeftIndentDip - 48) < 0.01,
            centered is null ? "没找到段落" : $"{centered.Style.Alignment} / {centered.Style.LeftIndentDip:0.##} DIP");

        var pageBreak = document.Blocks.OfType<WordParagraphBlock>()
            .FirstOrDefault(paragraph => paragraph.Text == "分页后");
        check("[Word阅读器] w:br type=\"page\" 记成段前分页标记",
            pageBreak is not null && pageBreak.Style.IsPageBreakBefore,
            pageBreak is null ? "没找到分页段落" : $"IsPageBreakBefore={pageBreak.Style.IsPageBreakBefore}");
    }

    // ---------------------------------------------------------------- 大纲

    private static void CheckOutline(Action<string, bool, string> check, WordDocument document)
    {
        check("[Word阅读器] 标题按出现顺序进大纲：2 条、级别 1 / 2",
            document.Outline.Count == 2
            && document.Outline[0] is { Level: 1, Text: "第一章 简介" }
            && document.Outline[1] is { Level: 2, Text: "1.1 小节" },
            string.Join(" | ", document.Outline.Select(heading => $"H{heading.Level} {heading.Text}")));

        var firstHeading = document.Outline.Count > 0 ? document.Outline[0] : null;
        check("[Word阅读器] 大纲里的 BlockIndex 能对应回正文段落",
            firstHeading is not null
            && document.GetParagraphText(firstHeading.BlockIndex) == "第一章 简介",
            firstHeading is null ? "没有大纲" : $"块 {firstHeading.BlockIndex}");

        check("[Word阅读器] 找不到的标题返回 -1（大纲联动兜底）",
            document.FindHeadingBlockIndex("不存在的标题") == -1,
            "FindHeadingBlockIndex(不存在的标题)");
    }

    // ---------------------------------------------------------------- 表格

    private static void CheckTable(Action<string, bool, string> check, WordDocument document)
    {
        var table = document.Blocks.OfType<WordTableBlock>().FirstOrDefault();
        check("[Word阅读器] 表格：3 行、列宽 1440/2880 twip → 96/192 DIP、首行是表头",
            table is not null && table.Rows.Count == 3
            && table.ColumnWidthDip.Count == 2
            && Math.Abs(table.ColumnWidthDip[0] - 96) < 0.01
            && Math.Abs(table.ColumnWidthDip[1] - 192) < 0.01
            && table.Rows[0].IsHeader,
            table is null
                ? "没找到表格"
                : $"{table.Rows.Count} 行 / 列宽 {string.Join(",", table.ColumnWidthDip.Select(width => width.ToString("0.#")))}");

        check("[Word阅读器] 表格：gridSpan=2 的合并表头 + 底纹色",
            table is not null
            && table.Rows[0].Cells.Count == 1
            && table.Rows[0].Cells[0].ColumnSpan == 2
            && table.Rows[0].Cells[0].ShadingHex == "#D9E2F3",
            table is null || table.Rows[0].Cells.Count == 0
                ? "解读失败"
                : $"ColumnSpan={table.Rows[0].Cells[0].ColumnSpan} / {table.Rows[0].Cells[0].ShadingHex}");

        var spanCell = table?.Rows.Count > 1 && table.Rows[1].Cells.Count > 0 ? table.Rows[1].Cells[0] : null;
        var continuation = table?.Rows.Count > 2 && table.Rows[2].Cells.Count > 0 ? table.Rows[2].Cells[0] : null;
        check("[Word阅读器] 表格：vMerge restart + continue 推算出 RowSpan=2，占位格标记成跳过",
            spanCell is not null && continuation is not null
            && spanCell.RowSpan == 2
            && continuation.IsMergedContinuation,
            spanCell is null ? "解读失败" : $"RowSpan={spanCell.RowSpan} / 占位={continuation?.IsMergedContinuation}");
    }

    // ---------------------------------------------------------------- 图片

    private static void CheckImage(Action<string, bool, string> check, WordDocument document)
    {
        var image = document.Blocks.OfType<WordImageBlock>().FirstOrDefault();
        check("[Word阅读器] 图片：drawing → media 字节 / MIME / wp:extent 尺寸（300×200 DIP）",
            image is not null && image.Data.Length > 0 && image.ContentType == "image/png"
            && Math.Abs(image.WidthDip - 300) < 0.01 && Math.Abs(image.HeightDip - 200) < 0.01,
            image is null
                ? "没找到图片"
                : $"{image.Data.Length} 字节 / {image.ContentType} / {image.WidthDip:0.#}×{image.HeightDip:0.#}");

        check("[Word阅读器] 图片：wp:docPr 的 descr 作替代文字",
            image?.AltText == "测试图片", image?.AltText ?? "");
    }

    // ---------------------------------------------------------------- 计数

    private static void CheckCounts(Action<string, bool, string> check, WordDocument document)
    {
        check("[Word阅读器] 计数：段落（含单元格）/ 表格 / 图片都统计到了",
            document.ParagraphCount >= 14 && document.TableCount == 1 && document.ImageCount == 1,
            $"{document.ParagraphCount} 段落 / {document.TableCount} 表格 / {document.ImageCount} 图片");

        check("[Word阅读器] 标题 / 文件名兜底：core.xml 的 dc:title 生效",
            document.Title == "自检标题", document.Title);
    }

    // ---------------------------------------------------------------- 查找

    private static void CheckSearch(Action<string, bool, string> check, WordDocument document)
    {
        var chineseHits = WordTextSearch.Find(document, "列表项");
        check("[Word阅读器] 查找：中文命中 2 处，预览含命中词",
            chineseHits.Count == 2
            && chineseHits.All(hit => hit.Preview.Contains("列表项", StringComparison.Ordinal))
            && chineseHits.All(hit => hit.Length == 3),
            string.Join(" | ", chineseHits.Select(hit => hit.Preview)));

        check("[Word阅读器] 查找：matchCase=false 忽略大小写、true 区分大小写",
            WordTextSearch.Find(document, "hello").Count == 1
            && WordTextSearch.Find(document, "hello", matchCase: true).Count == 0
            && WordTextSearch.Find(document, "Hello", matchCase: true).Count == 1,
            $"不区分={WordTextSearch.Find(document, "hello").Count} / 区分={WordTextSearch.Find(document, "hello", true).Count}");

        check("[Word阅读器] 查找：空查询返回 0 处，BlockIndex 指回正文块",
            WordTextSearch.Find(document, "").Count == 0
            && chineseHits.Count > 0
            && document.GetParagraphText(chineseHits[0].BlockIndex).Contains("列表项", StringComparison.Ordinal),
            chineseHits.Count > 0 ? $"块 {chineseHits[0].BlockIndex}" : "");
    }

    // ---------------------------------------------------------------- 渲染器（STA 线程）

    private static void CheckRenderer(Action<string, bool, string> check, WordDocument document)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var rendered = WordFlowRenderer.Build(document);

                check("[Word阅读器] 渲染：白色纸面 / 块映射数量与模型一致",
                    ReferenceEquals(rendered.Document.Background, Brushes.White)
                    && rendered.Document.Blocks.Count > 0
                    && rendered.ParagraphByBlockIndex.Count == document.Blocks.Count,
                    $"{rendered.Document.Blocks.Count} 个渲染块 / 映射 {rendered.ParagraphByBlockIndex.Count} 项");

                var headingIndex = document.Outline[0].BlockIndex;
                var headingParagraph = rendered.ParagraphByBlockIndex.ElementAtOrDefault(headingIndex);
                check("[Word阅读器] 渲染：标题段落在映射表里且是粗体",
                    headingParagraph is not null && headingParagraph.FontWeight == FontWeights.Bold,
                    headingParagraph is null ? "映射为 null" : $"FontWeight={headingParagraph.FontWeight}");

                check("[Word阅读器] 渲染：表格 / 图片块在映射表里是 null（没有可高亮段落）",
                    rendered.ParagraphByBlockIndex.Contains(null),
                    "至少一个表格 / 图片块映射 null");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            check("[Word阅读器] 渲染：FlowDocument 构建成功", false, $"{failure.GetType().Name}: {failure.Message}");
    }

    // ---------------------------------------------------------------- 扩展名 / 坏文件

    private static void CheckExtensionsAndErrors(
        Action<string, bool, string> check, string root, string docxPath, WordDocument document)
    {
        check("[Word阅读器] 扩展名：.docx / .docm（含大写）认，.doc / .txt / 空值不认",
            WordDocumentParser.IsSupportedExtension("a.docx")
            && WordDocumentParser.IsSupportedExtension("A.DOCM")
            && WordDocumentParser.IsSupportedExtension(@"D:\文档\说明.docx")
            && !WordDocumentParser.IsSupportedExtension("旧文档.doc")
            && !WordDocumentParser.IsSupportedExtension("a.txt")
            && !WordDocumentParser.IsSupportedExtension(null)
            && !WordDocumentParser.IsSupportedExtension(""),
            "docx / DOCM / 中文路径 / .doc / .txt / null / 空");

        var docPath = Path.Combine(root, "旧格式.doc");
        File.WriteAllText(docPath, "旧版二进制格式，不是 ZIP");
        var docRejected = false;
        var docMessage = "";
        try { WordDocumentParser.Parse(docPath); }
        catch (OoxmlException ex) { docRejected = true; docMessage = ex.Message; }

        check("[Word阅读器] .doc 拒绝打开，提示「另存为 .docx」",
            docRejected && docMessage.Contains(".docx", StringComparison.Ordinal),
            docMessage);

        var badPath = Path.Combine(root, "坏文件.docx");
        File.WriteAllText(badPath, "这不是一个 ZIP 文件");
        var badRejected = false;
        var badMessage = "";
        try { WordDocumentParser.Parse(badPath); }
        catch (OoxmlException ex) { badRejected = true; badMessage = ex.Message; }

        check("[Word阅读器] 坏文件抛 OoxmlException（中文消息，界面可直接显示）",
            badRejected && badMessage.Length > 0 && badMessage.Contains("ZIP", StringComparison.OrdinalIgnoreCase),
            badMessage);

        // 换后缀的 .docm 应当能解析；流式重载也要能读
        var docmPath = Path.Combine(root, "另存为.docm");
        File.Copy(docxPath, docmPath, overwrite: true);
        var docmDocument = WordDocumentParser.Parse(docmPath);
        check("[Word阅读器] .docm 按同样逻辑解析；Parse(Stream) 重载结果一致",
            docmDocument.Blocks.Count == document.Blocks.Count
            && docmDocument.Outline.Count == document.Outline.Count,
            $"{docmDocument.Blocks.Count} 块 / 大纲 {docmDocument.Outline.Count} 条");

        using var stream = File.OpenRead(docxPath);
        var streamDocument = WordDocumentParser.Parse(stream);
        check("[Word阅读器] 从流解析：标题 / 段落数与磁盘版一致",
            streamDocument.Title == document.Title
            && streamDocument.Blocks.Count == document.Blocks.Count,
            $"{streamDocument.Title} / {streamDocument.Blocks.Count} 块");
    }

    // ---------------------------------------------------------------- 配置与最近列表

    private static void CheckConfig(Action<string, bool, string> check, string root)
    {
        var config = new WordViewerConfig();
        check("[Word阅读器] 配置默认值：缩放 100 / 显示大纲 / 最近上限 50 / 文件夹为空",
            config.ZoomPercent == 100 && config.ZoomPercentEffective == 100
            && Math.Abs(config.ZoomScale - 1.0) < 0.0001
            && config.ShowOutline
            && config.RecentLimit == 50 && config.RecentLimitEffective == 50
            && config.RecordsRecent
            && config.LastFolder == "",
            $"Zoom={config.ZoomPercent} / Outline={config.ShowOutline} / Recent={config.RecentLimit}");

        config.ZoomPercent = 999;
        var upper = config.ZoomPercentEffective;
        config.ZoomPercent = 1;
        var lower = config.ZoomPercentEffective;
        check("[Word阅读器] 缩放夹取：999 → 300、1 → 50；步进 100 → 110 → 50 下限",
            upper == 300 && lower == 50
            && WordViewerConfig.StepZoomPercent(100, 1) == 110
            && WordViewerConfig.StepZoomPercent(295, 1) == 300
            && WordViewerConfig.StepZoomPercent(55, -1) == 50,
            $"upper={upper} / lower={lower}");

        // 最近列表：去重 + 置顶
        var fileA = Path.Combine(root, "最近甲.docx");
        var fileB = Path.Combine(root, "最近乙.docx");
        File.WriteAllText(fileA, "x");
        File.WriteAllText(fileB, "x");
        config.RememberRecent(fileA);
        config.RememberRecent(fileA);
        check("[Word阅读器] 最近列表：同一路径去重（记两次只有一条）",
            config.RecentEntries.Count == 1
            && string.Equals(Path.GetFullPath(config.RecentEntries[0].Path), Path.GetFullPath(fileA),
                StringComparison.OrdinalIgnoreCase),
            $"{config.RecentEntries.Count} 条");

        config.RememberRecent(fileB);
        config.RememberRecent(fileA);
        check("[Word阅读器] 最近列表：新打开的置顶（A → B → A 后 A 在最前）",
            config.RecentEntries.Count == 2
            && Path.GetFileName(config.RecentEntries[0].Path) == "最近甲.docx",
            string.Join(" | ", config.RecentEntries.Select(entry => Path.GetFileName(entry.Path))));

        config.RecentLimit = 1;
        config.TrimRecentToLimit();
        check("[Word阅读器] 最近列表：按上限裁剪（上限 1 → 只留最新一条）",
            config.RecentLimitEffective == 1 && config.RecentEntries.Count == 1,
            $"{config.RecentEntries.Count} 条");

        config.RecentLimit = 0;
        config.TrimRecentToLimit();
        check("[Word阅读器] 最近列表：上限 0 = 不记录并清空，RecordsRecent=false",
            config.RecentEntries.Count == 0 && !config.RecordsRecent, $"上限={config.RecentLimitEffective}");

        config.RecentLimit = 50;
        config.RememberRecent(fileA);
        config.ClearRecent();
        check("[Word阅读器] 最近列表：清空后为空（只删记录不删文件），且文件仍在磁盘上",
            config.RecentEntries.Count == 0 && File.Exists(fileA), $"{config.RecentEntries.Count} 条");

        config.ZoomPercent = 200;
        config.ShowOutline = false;
        config.RecentLimit = 5;
        config.ResetToDefaults();
        check("[Word阅读器] 恢复默认：缩放 100 / 大纲开 / 最近上限 50（不动上次文件夹）",
            config.ZoomPercent == 100 && config.ShowOutline && config.RecentLimit == 50,
            $"{config.ZoomPercent} / {config.ShowOutline} / {config.RecentLimit}");

        var missingItem = new WordRecentItem(Path.Combine(root, "已经没有的文件.docx"));
        var existingItem = new WordRecentItem(fileA);
        check("[Word阅读器] 最近项：文件不存在标 IsMissing，存在的可以打开",
            missingItem.IsMissing && !missingItem.IsAvailable
            && existingItem.IsAvailable && !existingItem.IsMissing,
            $"{missingItem.DisplayText} / {existingItem.DisplayText}");
    }

    // ---------------------------------------------------------------- 造一个最小 docx

    /// <summary>临时目录里写一个最小但功能齐全的 docx（样式 / 编号 / 表格 / 图片 / 链接 / 分页）。</summary>
    private static void CreateMinimalDocx(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteText(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="png" ContentType="image/png"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
              <Override PartName="/word/numbering.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
            </Types>
            """);

        WriteText(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
            </Relationships>
            """);

        WriteText(archive, "docProps/core.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties
                xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                xmlns:dc="http://purl.org/dc/elements/1.1/">
              <dc:title>自检标题</dc:title>
            </cp:coreProperties>
            """);

        WriteText(archive, "word/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:style w:type="paragraph" w:styleId="1"><w:name w:val="heading 1"/></w:style>
              <w:style w:type="paragraph" w:styleId="2">
                <w:name w:val="Heading 2"/>
                <w:pPr><w:outlineLvl w:val="1"/></w:pPr>
              </w:style>
              <w:style w:type="paragraph" w:styleId="Body"><w:name w:val="Normal"/></w:style>
              <w:style w:type="paragraph" w:styleId="ListStyle">
                <w:name w:val="List Bullet"/>
                <w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="2"/></w:numPr></w:pPr>
              </w:style>
            </w:styles>
            """);

        WriteText(archive, "word/numbering.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:abstractNum w:abstractNumId="0">
                <w:lvl w:ilvl="0">
                  <w:start w:val="1"/>
                  <w:numFmt w:val="decimal"/>
                  <w:lvlText w:val="%1."/>
                </w:lvl>
              </w:abstractNum>
              <w:abstractNum w:abstractNumId="1">
                <w:lvl w:ilvl="0">
                  <w:start w:val="1"/>
                  <w:numFmt w:val="bullet"/>
                  <w:lvlText w:val="•"/>
                </w:lvl>
              </w:abstractNum>
              <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
              <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
            </w:numbering>
            """);

        WriteText(archive, "word/_rels/document.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId10" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/pixel.png"/>
              <Relationship Id="rId11" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://example.com/docs" TargetMode="External"/>
            </Relationships>
            """);

        WriteText(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document
                xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="1"/></w:pPr><w:r><w:t>第一章 简介</w:t></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="2"/></w:pPr><w:r><w:t>1.1 小节</w:t></w:r></w:p>
                <w:p>
                  <w:r><w:rPr><w:b/><w:i/><w:u w:val="single"/><w:strike/></w:rPr><w:t>格式</w:t></w:r>
                  <w:r><w:t xml:space="preserve"> 普通 </w:t></w:r>
                  <w:r><w:rPr><w:color w:val="FF0000"/><w:sz w:val="28"/><w:rFonts w:ascii="Consolas"/><w:vertAlign w:val="superscript"/></w:rPr><w:t>彩色上标</w:t></w:r>
                  <w:r><w:tab/><w:t>制表</w:t></w:r>
                  <w:r><w:t xml:space="preserve"> Hello World</w:t></w:r>
                </w:p>
                <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>列表项一</w:t></w:r></w:p>
                <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>列表项二</w:t></w:r></w:p>
                <!-- 列表样式把 numPr 挂在 styles.xml 里（Word 内置「项目符号」就是这种），段落身上没有 numPr -->
                <w:p><w:pPr><w:pStyle w:val="ListStyle"/></w:pPr><w:r><w:t>样式项目符号</w:t></w:r></w:p>
                <w:p><w:pPr><w:ind w:left="720"/><w:jc w:val="center"/></w:pPr><w:r><w:t>居中缩进</w:t></w:r></w:p>
                <w:p><w:r><w:br w:type="page"/><w:t>分页后</w:t></w:r></w:p>
                <w:p><w:hyperlink r:id="rId11"><w:r><w:rPr><w:u w:val="single"/></w:rPr><w:t>示例链接</w:t></w:r></w:hyperlink></w:p>
                <w:tbl>
                  <w:tblGrid><w:gridCol w:w="1440"/><w:gridCol w:w="2880"/></w:tblGrid>
                  <w:tr>
                    <w:trPr><w:tblHeader/></w:trPr>
                    <w:tc><w:tcPr><w:gridSpan w:val="2"/><w:shd w:fill="D9E2F3"/></w:tcPr><w:p><w:r><w:t>合并表头</w:t></w:r></w:p></w:tc>
                  </w:tr>
                  <w:tr>
                    <w:tc><w:tcPr><w:vMerge w:val="restart"/></w:tcPr><w:p><w:r><w:t>纵向合并</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>第一行右</w:t></w:r></w:p></w:tc>
                  </w:tr>
                  <w:tr>
                    <w:tc><w:tcPr><w:vMerge/></w:tcPr><w:p><w:r><w:t>占位</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>第二行右</w:t></w:r></w:p></w:tc>
                  </w:tr>
                </w:tbl>
                <w:p>
                  <w:r>
                    <w:drawing>
                      <wp:inline>
                        <wp:extent cx="2857500" cy="1905000"/>
                        <wp:docPr id="1" name="测试图片" descr="测试图片"/>
                        <a:graphic>
                          <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                            <pic:pic>
                              <pic:blipFill><a:blip r:embed="rId10"/></pic:blipFill>
                            </pic:pic>
                          </a:graphicData>
                        </a:graphic>
                      </wp:inline>
                    </w:drawing>
                  </w:r>
                </w:p>
                <w:sectPr/>
              </w:body>
            </w:document>
            """);

        // 1×1 透明 PNG（足够验证「关系 → media 字节」这条链路）
        WriteBytes(archive, "word/media/pixel.png", Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
    }

    private static void WriteText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static void WriteBytes(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
