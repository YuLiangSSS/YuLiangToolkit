using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Core.Storage;
using YuLiangToolkit.Tools.WordViewer;
using YuLiangToolkit.Tools.WordViewer.Export;
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

            // ---------- 新增浏览功能：页眉页脚 / 脚注尾注 / 阅读位置 / 复制全文 / 查找增强 / 文档属性 ----------
            var featurePath = Path.Combine(root, "功能自检.docx");
            CreateFeatureDocx(featurePath, withExtras: true);
            var feature = WordDocumentParser.Parse(featurePath);

            CheckHeaderFooter(check, feature);
            CheckFootnotes(check, feature);
            CheckDocumentProperties(check, feature);
            CheckPlainText(check, feature);
            CheckSearchTable(check, feature);
            CheckFeatureConfig(check, root);
            CheckRendererHeaderFooter(check, feature);

            // 缺少页眉页脚 / 脚注部件的文档：必须照常打开，只是那些内容为空
            var barePath = Path.Combine(root, "缺附属部件.docx");
            CreateFeatureDocx(barePath, withExtras: false);
            CheckMissingParts(check, barePath, feature);

            // ---------- 最后一批：批注 / 修订 / 打印与导出 ----------
            var annotatedPath = Path.Combine(root, "批注修订.docx");
            CreateCommentRevisionDocx(annotatedPath, withComments: true);
            var annotated = WordDocumentParser.Parse(annotatedPath);

            CheckComments(check, annotated);
            CheckCommentPanelItem(check, annotated);
            CheckRevisions(check, annotated);
            CheckRendererCommentsRevisions(check, annotated);

            CheckHtmlExport(check, document, annotated);
            CheckTextExport(check, annotated, root);
            CheckCommentRevisionConfig(check, root);

            // 缺 comments.xml：正文里的区间 / 引用标记仍在，批注列表为空，不能崩
            var noCommentPath = Path.Combine(root, "缺批注部件.docx");
            CreateCommentRevisionDocx(noCommentPath, withComments: false);
            CheckMissingComments(check, noCommentPath, annotated);
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

    // ---------------------------------------------------------------- 页眉 / 页脚

    private static void CheckHeaderFooter(Action<string, bool, string> check, WordDocument document)
    {
        var headerText = TextOfBlocks(document.HeaderBlocks);

        check("[Word阅读器] 页眉：sectPr 的 headerReference default → header 部件，文字解析出来",
            document.HasHeader && headerText.Contains("默认页眉文字", StringComparison.Ordinal),
            headerText.Length == 0 ? "没有页眉内容" : headerText);

        check("[Word阅读器] 页眉 type 优先级：default 优先于 first / even",
            !headerText.Contains("首页页眉", StringComparison.Ordinal)
            && !headerText.Contains("偶数页页眉", StringComparison.Ordinal),
            headerText);

        check("[Word阅读器] 页眉部件里的表格也被解析（段落 + 表格）",
            document.HeaderBlocks.OfType<WordTableBlock>().Any(table => table.Rows
                .SelectMany(row => row.Cells)
                .SelectMany(cell => cell.Blocks.OfType<WordParagraphBlock>())
                .Any(paragraph => paragraph.Text.Contains("页眉表格", StringComparison.Ordinal))),
            $"{document.HeaderBlocks.Count} 个页眉块");

        var footerText = TextOfBlocks(document.FooterBlocks);
        check("[Word阅读器] 页脚：footerReference default → footer 部件，文字解析出来",
            document.HasFooter && footerText.Contains("页脚文字", StringComparison.Ordinal),
            footerText.Length == 0 ? "没有页脚内容" : footerText);

        check("[Word阅读器] 页眉页脚不参与大纲 / 查找，页眉里的表格不计入正文表格数",
            document.Outline.All(heading => !heading.Text.Contains("默认页眉", StringComparison.Ordinal))
            && WordTextSearch.Find(document, "默认页眉文字").Count == 0
            && document.TableCount == 1,
            $"大纲 {document.Outline.Count} 条 / 正文表格 {document.TableCount} 个");
    }

    // ---------------------------------------------------------------- 脚注 / 尾注

    private static void CheckFootnotes(Action<string, bool, string> check, WordDocument document)
    {
        check("[Word阅读器] 脚注：过滤 w:id < 1 的分隔符，按正文引用顺序编号（id=2 → [1]、id=1 → [2]）",
            document.Footnotes.Count == 2
            && document.Footnotes[0] is { Id: 2, Number: 1 }
            && document.Footnotes[1] is { Id: 1, Number: 2 },
            string.Join(" | ", document.Footnotes.Select(note => $"id={note.Id}→[{note.Number}]")));

        check("[Word阅读器] 脚注内容：多段按段落保留、分隔符条目不出现",
            document.Footnotes.Any(note => note.Id == 2 && note.Blocks.Count == 2
                && note.Text.Contains("第二条脚注第二段", StringComparison.Ordinal))
            && document.Footnotes.Any(note => note.Id == 1
                && note.Text.Contains("第一条脚注内容", StringComparison.Ordinal)),
            string.Join(" | ", document.Footnotes.Select(note => note.Text.Replace('\n', '/'))));

        var footnoteRuns = document.Blocks.OfType<WordParagraphBlock>()
            .SelectMany(paragraph => paragraph.Runs)
            .Where(run => run.FootnoteId is not null)
            .ToList();
        check("[Word阅读器] 正文 run 记录 FootnoteId / 显示编号，纯引用 run（无文字）也保留",
            footnoteRuns.Count == 2
            && footnoteRuns.Any(run => run.FootnoteId == 2 && run.FootnoteNumber == 1 && run.Text.Length == 0)
            && footnoteRuns.Any(run => run.FootnoteId == 1 && run.FootnoteNumber == 2),
            string.Join(" | ", footnoteRuns.Select(run => $"id={run.FootnoteId}→[{run.FootnoteNumber}] '{run.Text}'")));

        var endnoteRuns = document.Blocks.OfType<WordParagraphBlock>()
            .SelectMany(paragraph => paragraph.Runs)
            .Where(run => run.EndnoteId is not null)
            .ToList();
        check("[Word阅读器] 尾注：endnotes.xml 解析 + run 记录 EndnoteId / 编号 1",
            document.Endnotes.Count == 1
            && document.Endnotes[0] is { Id: 1, Number: 1 }
            && document.Endnotes[0].Text.Contains("第一条尾注内容", StringComparison.Ordinal)
            && endnoteRuns.Any(run => run.EndnoteId == 1 && run.EndnoteNumber == 1),
            $"{document.Endnotes.Count} 条尾注 / {endnoteRuns.Count} 个引用");

        check("[Word阅读器] 脚注 / 尾注内容不参与查找与大纲，也不占用正文块下标",
            WordTextSearch.Find(document, "第一条脚注内容").Count == 0
            && document.Outline.All(heading => !heading.Text.Contains("脚注", StringComparison.Ordinal))
            && document.GetParagraphText(0).Contains("功能自检正文", StringComparison.Ordinal),
            document.GetParagraphText(0));
    }

    // ---------------------------------------------------------------- 文档属性

    private static void CheckDocumentProperties(Action<string, bool, string> check, WordDocument document)
    {
        check("[Word阅读器] 文档属性：作者 / 最后修改者 / 创建时间 / 修改时间（docProps/core.xml）",
            document.Author == "测试作者"
            && document.LastModifiedBy == "测试修改者"
            && document.CreatedUtc is { } created && created is { Year: 2024, Month: 5, Day: 1 }
            && document.ModifiedUtc is { } modified && modified is { Year: 2024, Month: 6, Day: 2 },
            $"作者={document.Author} / 修改者={document.LastModifiedBy} / 创建={document.CreatedUtc:o} / 修改={document.ModifiedUtc:o}");
    }

    // ---------------------------------------------------------------- 复制全文（纯文本抽取）

    private static void CheckPlainText(Action<string, bool, string> check, WordDocument document)
    {
        var text = document.ExtractPlainText();

        check("[Word阅读器] 复制全文：段落换行、表格单元格用 \\t、不含页眉页脚 / 脚注",
            text.Contains("功能自检正文", StringComparison.Ordinal)
            && text.Contains('\n')
            && text.Contains("表格甲\t表格乙", StringComparison.Ordinal)
            && !text.Contains("默认页眉文字", StringComparison.Ordinal)
            && !text.Contains("页脚文字", StringComparison.Ordinal)
            && !text.Contains("第一条脚注内容", StringComparison.Ordinal),
            text.Length > 120 ? text[..120] + "…" : text);
    }

    // ---------------------------------------------------------------- 查找增强（表格 / 大小写 / 无命中）

    private static void CheckSearchTable(Action<string, bool, string> check, WordDocument document)
    {
        var tableHits = WordTextSearch.Find(document, "表格甲");
        check("[Word阅读器] 查找表格文字：命中 1 处、InTable=true、块下标指向表格块",
            tableHits.Count == 1
            && tableHits[0].InTable
            && tableHits[0].BlockIndex >= 0
            && document.Blocks[tableHits[0].BlockIndex] is WordTableBlock,
            tableHits.Count == 0
                ? "没有命中"
                : $"块 {tableHits[0].BlockIndex} / InTable={tableHits[0].InTable}");

        check("[Word阅读器] 查找：区分大小写与无命中（正文 + 表格同规则）",
            WordTextSearch.Find(document, "hello").Count == 1
            && WordTextSearch.Find(document, "hello", matchCase: true).Count == 0
            && WordTextSearch.Find(document, "HELLO", matchCase: true).Count == 0
            && WordTextSearch.Find(document, "不存在的词").Count == 0
            && WordTextSearch.Find(document, "表格甲", matchCase: true).Count == 1,
            $"不区分={WordTextSearch.Find(document, "hello").Count} / 区分={WordTextSearch.Find(document, "hello", true).Count}");
    }

    // ---------------------------------------------------------------- 阅读位置 + 新开关配置

    private static void CheckFeatureConfig(Action<string, bool, string> check, string root)
    {
        var config = new WordViewerConfig();
        check("[Word阅读器] 新开关默认值：显示页眉页脚 / 记住阅读位置 开，查找不区分大小写",
            config.ShowHeaderFooter && config.RememberReadingPositionEnabled && !config.SearchMatchCase,
            $"页眉页脚={config.ShowHeaderFooter} / 记住位置={config.RememberReadingPositionEnabled} / 区分大小写={config.SearchMatchCase}");

        var fileA = Path.Combine(root, "阅读位置甲.docx");
        var fileB = Path.Combine(root, "阅读位置乙.docx");
        File.WriteAllText(fileA, "x");
        File.WriteAllText(fileB, "x");

        config.RememberReadingPosition(fileA,
            new WordReadingPosition { ScrollRatio = 0.42, SavedAtUtc = DateTime.UtcNow });
        check("[Word阅读器] 阅读位置：保存 / 查询 / 路径大小写去重（同一个文件只存一条）",
            config.FindReadingPosition(fileA) is { } saved
            && Math.Abs(saved.ScrollRatio - 0.42) < 1e-9
            && config.ReadingPositions.Count == 1
            && config.FindReadingPosition(fileA.ToUpperInvariant()) is not null,
            $"{config.ReadingPositions.Count} 条 / 取回比例 {config.FindReadingPosition(fileA)?.ScrollRatio}");

        check("[Word阅读器] 阅读位置：非法比例夹取（NaN → 0、5 → 1、-1 → 0）",
            Math.Abs(new WordReadingPosition { ScrollRatio = double.NaN }.ScrollRatioEffective) < 1e-9
            && Math.Abs(WordReadingPosition.ClampScrollRatio(5) - 1) < 1e-9
            && Math.Abs(WordReadingPosition.ClampScrollRatio(-1)) < 1e-9,
            "NaN / 5 / -1");

        // 上限 200：批量写 205 份（保存时间递增），最老的应被淘汰
        var baseTime = DateTime.UtcNow.AddDays(-30);
        for (var index = 0; index < WordViewerConfig.MaxReadingPositions + 5; index++)
        {
            config.RememberReadingPosition(Path.Combine(root, $"批量阅读位置{index}.docx"),
                new WordReadingPosition { ScrollRatio = index / 1000.0, SavedAtUtc = baseTime.AddMinutes(index) });
        }

        check("[Word阅读器] 阅读位置：超过上限（200）按保存时间淘汰最老的记录",
            config.ReadingPositions.Count == WordViewerConfig.MaxReadingPositions
            && config.FindReadingPosition(Path.Combine(root, "批量阅读位置0.docx")) is null,
            $"{config.ReadingPositions.Count} 条");

        config.RememberReadingPosition(fileA, new WordReadingPosition { ScrollRatio = 0.8 });
        var json = config.ToJson();
        check("[Word阅读器] 新开关与 readingPositions 能序列化，[JsonIgnore] 计算属性不进 JSON",
            json.Contains("\"showHeaderFooter\"", StringComparison.Ordinal)
            && json.Contains("\"rememberReadingPosition\"", StringComparison.Ordinal)
            && json.Contains("\"searchMatchCase\"", StringComparison.Ordinal)
            && json.Contains("\"readingPositions\"", StringComparison.Ordinal)
            && json.Contains("\"scrollRatio\"", StringComparison.Ordinal)
            && !json.Contains("\"scrollRatioEffective\"", StringComparison.Ordinal)
            && !json.Contains("\"zoomScale\"", StringComparison.Ordinal)
            && !json.Contains("\"recentLimitEffective\"", StringComparison.Ordinal),
            json.Length > 160 ? json[..160] + "…" : json);
    }

    // ---------------------------------------------------------------- 页眉页脚 / 脚注渲染（STA）

    private static void CheckRendererHeaderFooter(Action<string, bool, string> check, WordDocument document)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var withHeaderFooter = WordFlowRenderer.Build(document);
                var withoutHeaderFooter = WordFlowRenderer.Build(document, new WordRenderOptions { ShowHeaderFooter = false });

                check("[Word阅读器] 渲染：ShowHeaderFooter 开关生效（true 比 false 多页眉页脚块），映射数量不变",
                    withHeaderFooter.HasHeaderFooter && !withoutHeaderFooter.HasHeaderFooter
                    && withHeaderFooter.Document.Blocks.Count > withoutHeaderFooter.Document.Blocks.Count
                    && withHeaderFooter.BlockByBlockIndex.Count == document.Blocks.Count
                    && withHeaderFooter.ParagraphByBlockIndex.Count == document.Blocks.Count,
                    $"{withHeaderFooter.Document.Blocks.Count} / {withoutHeaderFooter.Document.Blocks.Count} 个渲染块");

                var blocks = withHeaderFooter.Document.Blocks.ToList();
                var bodyIndex = IndexOfText(blocks, "功能自检正文");
                var noteIndex = IndexOfText(blocks, "脚注", exact: true);
                var footerIndex = IndexOfText(blocks, "页脚文字");

                check("[Word阅读器] 渲染：页眉在最顶部（9 磅浅灰字）",
                    blocks.FirstOrDefault() is Paragraph headerParagraph
                    && TextOf(headerParagraph).Contains("默认页眉文字", StringComparison.Ordinal)
                    && Math.Abs(headerParagraph.FontSize - OoxmlPackage.PointToDip(9)) < 0.01
                    && headerParagraph.Foreground is SolidColorBrush headerBrush
                    && !headerBrush.Color.Equals(Colors.Black),
                    blocks.FirstOrDefault() is Paragraph first ? TextOf(first) : "第一个渲染块不是段落");

                check("[Word阅读器] 渲染：脚注小节在正文之后、页脚之前，正文里有 [1] 上标标记",
                    blocks.LastOrDefault() is Paragraph footerParagraph
                    && TextOf(footerParagraph).Contains("页脚文字", StringComparison.Ordinal)
                    && bodyIndex >= 0 && noteIndex >= 0 && footerIndex >= 0
                    && bodyIndex < noteIndex && noteIndex < footerIndex
                    && blocks.Any(block => TextOf(block).Contains("第一条脚注内容", StringComparison.Ordinal))
                    && TextOfBodyParagraph(blocks, "引用顺序测试").Contains("[1]", StringComparison.Ordinal),
                    $"正文 {bodyIndex} / 脚注 {noteIndex} / 页脚 {footerIndex}");

                var tableIndex = -1;
                for (var index = 0; index < document.Blocks.Count; index++)
                {
                    if (document.Blocks[index] is WordTableBlock)
                    {
                        tableIndex = index;
                        break;
                    }
                }

                check("[Word阅读器] 渲染：表格块也有 Block 映射（查找命中表格可 BringIntoView）",
                    tableIndex >= 0
                    && withHeaderFooter.BlockByBlockIndex[tableIndex] is Table
                    && withHeaderFooter.ParagraphByBlockIndex[tableIndex] is null,
                    tableIndex < 0 ? "没有表格块" : $"表格块下标 {tableIndex}");
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
            check("[Word阅读器] 渲染：页眉页脚 / 脚注小节渲染成功", false,
                $"{failure.GetType().Name}: {failure.Message}");
    }

    // ---------------------------------------------------------------- 缺部件容错

    private static void CheckMissingParts(
        Action<string, bool, string> check, string barePath, WordDocument reference)
    {
        Exception? failure = null;
        WordDocument? bare = null;
        try { bare = WordDocumentParser.Parse(barePath); }
        catch (Exception ex) { failure = ex; }

        check("[Word阅读器] 缺页眉页脚 / 脚注部件时正常打开：正文照常、附属内容为空、引用只显示标记",
            failure is null && bare is not null
            && bare.Blocks.Count == reference.Blocks.Count
            && bare.HeaderBlocks.Count == 0
            && bare.FooterBlocks.Count == 0
            && bare.Footnotes.Count == 0
            && bare.Endnotes.Count == 0
            && bare.Blocks.OfType<WordParagraphBlock>().SelectMany(paragraph => paragraph.Runs)
                .Any(run => run.FootnoteId == 1),
            failure?.Message
                ?? $"正文 {bare?.Blocks.Count} 块 / 页眉 {bare?.HeaderBlocks.Count} / 页脚 {bare?.FooterBlocks.Count} / 脚注 {bare?.Footnotes.Count}");
    }

    // ---------------------------------------------------------------- 批注解析

    private static void CheckComments(Action<string, bool, string> check, WordDocument document)
    {
        check("[Word阅读器] 批注解析：条数 / 作者 / 缩写 / 日期（comments.xml）",
            document.Comments.Count == 4
            && document.Comments.Any(comment => comment.Id == 5 && comment.Author == "张三"
                && comment.Initials == "ZS" && comment.DateUtc is { Year: 2024, Month: 4, Day: 1 })
            && document.Comments.Any(comment => comment.Id == 1 && comment.Author == "李四"
                && comment.Initials == "LS"),
            string.Join(" | ", document.Comments.Select(comment =>
                $"id={comment.Id}→#{comment.Number} {comment.Author} {comment.DateText}")));

        var first = document.FindComment(5);
        check("[Word阅读器] 批注内容：多段按段落解析，纯文本用换行连接",
            first is not null && first.Blocks.Count == 2
            && first.Text == "第一条批注第一段\n第一条批注第二段",
            first is null ? "没找到 id=5 的批注" : first.Text.Replace('\n', '/'));

        var coveredParagraph = FindParagraph(document, "批注覆盖一");
        var coveredRuns = coveredParagraph?.Runs.Where(run => run.CommentIds.Contains(5)).ToList() ?? new List<WordRun>();
        check("[Word阅读器] 批注区间跨 run：同一条批注覆盖的两个 run 都带上 w:id",
            coveredRuns.Count == 2
            && coveredRuns[0].Text == "批注覆盖一"
            && coveredRuns[1].Text == "批注覆盖二",
            coveredParagraph is null ? "没找到被批注段落" : string.Join(" | ", coveredRuns.Select(run => run.Text)));

        var crossFirst = FindParagraph(document, "跨段第一段");
        var crossSecond = FindParagraph(document, "跨段第二段");
        check("[Word阅读器] 批注区间跨段落：起点段的 run 与终点段的 run 都属于同一条批注",
            crossFirst is not null && crossSecond is not null
            && crossFirst.Runs.Count > 0 && crossFirst.Runs.All(run => run.CommentIds.Contains(1))
            && crossSecond.Runs.Any(run => run.Text == "跨段第二段" && run.CommentIds.Contains(1)),
            $"第一段 {crossFirst?.Runs.Count} run / 第二段 {crossSecond?.Runs.Count} run");

        check("[Word阅读器] 批注 → 起始块下标指向被批注段落（面板跳转用）",
            first is not null && first.BlockIndex >= 0
            && document.Blocks[first.BlockIndex] is WordParagraphBlock anchor
            && anchor.Text.Contains("批注覆盖一", StringComparison.Ordinal),
            first is null ? "没找到批注" : $"BlockIndex={first.BlockIndex}");

        check("[Word阅读器] 批注编号按正文出现顺序：id=5 先出现 → 批注 1，id=1 → 批注 2，无位置的排最后",
            first is { Number: 1 }
            && document.FindComment(1) is { Number: 2 }
            && document.FindComment(9) is { Number: 3, BlockIndex: -1 }
            && document.FindComment(99) is { Number: 4, BlockIndex: -1 }
            && document.Comments.Select(comment => comment.Number).SequenceEqual(new[] { 1, 2, 3, 4 }),
            string.Join(" | ", document.Comments.OrderBy(comment => comment.Number)
                .Select(comment => $"#{comment.Number}=id{comment.Id}@块{comment.BlockIndex}")));

        var referenceRuns = document.Blocks.OfType<WordParagraphBlock>()
            .SelectMany(paragraph => paragraph.Runs)
            .Where(run => run.CommentReferenceIds.Contains(5))
            .ToList();
        check("[Word阅读器] 批注引用 run（通常没有文字）保留，记录 w:commentReference w:id",
            referenceRuns.Count == 1
            && referenceRuns[0].Text.Length == 0
            && referenceRuns[0].CommentReferenceIds.SequenceEqual(new[] { 5 }),
            string.Join(" | ", referenceRuns.Select(run => $"'{run.Text}' → {string.Join(",", run.CommentReferenceIds)}")));

        var longComment = document.FindComment(99);
        check("[Word阅读器] 单条批注超长：按 20000 字上限截断并标记 IsTextTruncated",
            longComment is { IsTextTruncated: true }
            && longComment.Text.Length <= WordDocumentParser.MaxCommentTextLength + 40
            && longComment.Text.EndsWith("省略）", StringComparison.Ordinal),
            longComment is null ? "没找到超长批注" : $"{longComment.Text.Length} 字 / 截断={longComment.IsTextTruncated}");
    }

    /// <summary>批注面板条目（纯逻辑 ViewModel 对象）：标题 / 摘要 / ToolTip / 跳转下标。</summary>
    private static void CheckCommentPanelItem(Action<string, bool, string> check, WordDocument document)
    {
        var comment = document.FindComment(5);
        var item = comment is null ? null : new WordCommentItem(comment);
        check("[Word阅读器] 批注面板条目：标题含序号 / 作者 / 日期，ToolTip 含全文，带跳转下标",
            item is not null
            && item.HeaderText.Contains("批注 1", StringComparison.Ordinal)
            && item.HeaderText.Contains("张三", StringComparison.Ordinal)
            && item.DateText.Length > 0
            && item.ToolTipText.Contains("第一条批注第二段", StringComparison.Ordinal)
            && item.PreviewText.Contains("第一条批注第一段", StringComparison.Ordinal)
            && item.HasAnchor && item.BlockIndex == (comment?.BlockIndex ?? -2),
            item?.HeaderText ?? "没找到批注面板条目");

        var longItem = document.FindComment(99) is { } longComment ? new WordCommentItem(longComment) : null;
        check("[Word阅读器] 批注面板删除超长内容：摘要 ≤160 字 + 省略号，全文完整保留",
            longItem is not null
            && longItem.PreviewText.Length <= 161
            && longItem.PreviewText.EndsWith('…')
            && longItem.FullText.Length > WordDocumentParser.MaxCommentTextLength,
            longItem is null ? "没找到超长批注条目" : $"摘要 {longItem.PreviewText.Length} 字 / 全文 {longItem.FullText.Length} 字");
    }

    // ---------------------------------------------------------------- 修订解析

    private static void CheckRevisions(Action<string, bool, string> check, WordDocument document)
    {
        var allRuns = document.Blocks.OfType<WordParagraphBlock>()
            .SelectMany(paragraph => paragraph.Runs)
            .ToList();

        var inserted = allRuns.FirstOrDefault(run => run.Text == "插入的文字");
        check("[Word阅读器] 修订插入：w:ins 里的 run 标记 Inserted + 作者 / 日期",
            inserted is { Revision: WordRevisionKind.Inserted, RevisionAuthor: "王五" }
            && inserted.RevisionDateUtc is { Year: 2024, Month: 5, Day: 1 },
            inserted is null ? "没找到插入的文字" : $"{inserted.Revision} / {inserted.RevisionAuthor} / {inserted.RevisionDateUtc:o}");

        var deleted = allRuns.FirstOrDefault(run => run.Text == "删除的文字");
        check("[Word阅读器] 修订删除：w:del 的 w:delText 进模型，标记 Deleted + 作者 / 日期",
            deleted is { Revision: WordRevisionKind.Deleted, RevisionAuthor: "王五" }
            && deleted.RevisionDateUtc is { Year: 2024, Month: 5, Day: 2 },
            deleted is null ? "没找到删除的文字" : $"{deleted.Revision} / {deleted.RevisionAuthor} / {deleted.RevisionDateUtc:o}");

        check("[Word阅读器] 修订统计：插入 1 处 / 删除 1 处，HasRevisions=true",
            document.InsertedRunCount == 1 && document.DeletedRunCount == 1 && document.HasRevisions,
            $"插入 {document.InsertedRunCount} / 删除 {document.DeletedRunCount}");

        var revisionParagraph = FindParagraph(document, "修订前");
        check("[Word阅读器] 段落可见文字跳过删除内容（Text 不含删除，TextWithDeleted 含）",
            revisionParagraph is not null
            && revisionParagraph.Text == "修订前插入的文字修订后"
            && revisionParagraph.TextWithDeleted.Contains("删除的文字", StringComparison.Ordinal),
            revisionParagraph?.Text ?? "没找到修订段落");

        check("[Word阅读器] 查找跳过删除文字、插入文字正常命中",
            WordTextSearch.Find(document, "删除的文字").Count == 0
            && WordTextSearch.Find(document, "插入的文字").Count == 1,
            $"删除={WordTextSearch.Find(document, "删除的文字").Count} / 插入={WordTextSearch.Find(document, "插入的文字").Count}");

        var plain = document.ExtractPlainText();
        check("[Word阅读器] 复制全文（ExtractPlainText）跳过删除内容、保留插入内容",
            plain.Contains("插入的文字", StringComparison.Ordinal)
            && !plain.Contains("删除的文字", StringComparison.Ordinal),
            plain.Length > 80 ? plain[..80] + "…" : plain);
    }

    // ---------------------------------------------------------------- 批注 / 修订渲染 + 打印排版（STA）

    private static void CheckRendererCommentsRevisions(Action<string, bool, string> check, WordDocument document)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var withAll = WordFlowRenderer.Build(document, new WordRenderOptions
                {
                    ShowComments = true,
                    ShowRevisions = true,
                });
                var withoutComments = WordFlowRenderer.Build(document, new WordRenderOptions
                {
                    ShowComments = false,
                    ShowRevisions = true,
                });
                var withoutRevisions = WordFlowRenderer.Build(document, new WordRenderOptions
                {
                    ShowComments = true,
                    ShowRevisions = false,
                });

                check("[Word阅读器] 渲染：批注高亮 4 个 run、[批注 N] 标记 2 个（区间跨 run / 跨段落）",
                    withAll.CommentHighlightRunCount == 4 && withAll.CommentMarkerCount == 2,
                    $"高亮 {withAll.CommentHighlightRunCount} / 标记 {withAll.CommentMarkerCount}");

                var highlighted = FindRunText(withAll.Document, "批注覆盖一");
                check("[Word阅读器] 渲染：被批注的 run 加淡黄底 #FFF3C4",
                    HasBrushColor(highlighted?.Background, Color.FromRgb(0xFF, 0xF3, 0xC4)),
                    highlighted?.Background?.ToString() ?? "没找到被批注的 run");

                var allText = TextOf(withAll.Document);
                check("[Word阅读器] 渲染：引用处显示上标 [批注 1] / [批注 2] 标记",
                    allText.Contains("[批注 1]", StringComparison.Ordinal)
                    && allText.Contains("[批注 2]", StringComparison.Ordinal),
                    allText.Length > 120 ? allText[..120] + "…" : allText);

                check("[Word阅读器] 渲染：ShowComments=false 时没有高亮 / 标记，正文文字照常显示",
                    withoutComments.CommentHighlightRunCount == 0
                    && withoutComments.CommentMarkerCount == 0
                    && TextOf(withoutComments.Document).Contains("批注覆盖一", StringComparison.Ordinal)
                    && !TextOf(withoutComments.Document).Contains("[批注", StringComparison.Ordinal),
                    $"高亮 {withoutComments.CommentHighlightRunCount} / 标记 {withoutComments.CommentMarkerCount}");

                var insertedRun = FindRunText(withAll.Document, "插入的文字");
                var deletedRun = FindRunText(withAll.Document, "删除的文字");
                check("[Word阅读器] 渲染：插入蓝色下划线、删除红色删除线（同时显示）",
                    insertedRun is not null && HasBrushColor(insertedRun.Foreground, Color.FromRgb(0x1A, 0x66, 0xCC))
                    && HasDecoration(insertedRun, TextDecorationLocation.Underline)
                    && deletedRun is not null && HasBrushColor(deletedRun.Foreground, Color.FromRgb(0xC0, 0x00, 0x00))
                    && HasDecoration(deletedRun, TextDecorationLocation.Strikethrough),
                    $"插入={insertedRun?.Text ?? "无"} / 删除={deletedRun?.Text ?? "无"}");

                var noRevisionText = TextOf(withoutRevisions.Document);
                var plainInserted = FindRunText(withoutRevisions.Document, "插入的文字");
                check("[Word阅读器] 渲染：ShowRevisions=false 隐藏删除内容、插入按普通文字（无蓝字下划线）",
                    !noRevisionText.Contains("删除的文字", StringComparison.Ordinal)
                    && noRevisionText.Contains("插入的文字", StringComparison.Ordinal)
                    && !HasBrushColor(plainInserted?.Foreground, Color.FromRgb(0x1A, 0x66, 0xCC))
                    && !HasDecoration(plainInserted, TextDecorationLocation.Underline),
                    $"插入 run{(plainInserted is null ? "丢失" : "存在")} / 删除文字{(noRevisionText.Contains("删除的文字") ? "仍在" : "已隐藏")}");

                var printable = WordPrintLayout.CreatePrintableDocument(
                    document, new WordRenderOptions { ShowRevisions = false }, new Size(800, 1100));
                var paginator = ((IDocumentPaginatorSource)printable).DocumentPaginator;
                check("[Word阅读器] 打印排版：另建文档按可打印区域设页尺寸 / 有限列宽，且跟随渲染开关",
                    Math.Abs(printable.PageWidth - 800) < 0.01
                    && Math.Abs(printable.PageHeight - 1100) < 0.01
                    && printable.ColumnWidth > 0 && printable.ColumnWidth <= printable.PageWidth
                    && Math.Abs(paginator.PageSize.Width - 800) < 0.01
                    && !TextOf(printable).Contains("删除的文字", StringComparison.Ordinal),
                    $"{printable.PageWidth:0}×{printable.PageHeight:0} / 列宽 {printable.ColumnWidth:0} / 分页器 {paginator.PageSize.Width:0}×{paginator.PageSize.Height:0}");
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
            check("[Word阅读器] 渲染：批注 / 修订 / 打印排版构建成功", false,
                $"{failure.GetType().Name}: {failure.Message}");
    }

    // ---------------------------------------------------------------- HTML / 纯文本导出

    private static void CheckHtmlExport(
        Action<string, bool, string> check, WordDocument minimal, WordDocument annotated)
    {
        var html = WordHtmlExporter.BuildHtml(minimal);

        check("[Word阅读器] HTML 导出：DOCTYPE / UTF-8 meta / 文档标题",
            html.Contains("<!DOCTYPE html>", StringComparison.Ordinal)
            && html.Contains("<meta charset=\"utf-8\">", StringComparison.Ordinal)
            && html.Contains("<title>自检标题</title>", StringComparison.Ordinal),
            html.Length > 100 ? html[..100].Replace('\n', ' ') + "…" : html);

        check("[Word阅读器] HTML 导出：标题 1 / 标题 2 用 h1 / h2",
            html.Contains("<h1", StringComparison.Ordinal)
            && html.Contains("第一章 简介", StringComparison.Ordinal)
            && html.Contains("<h2", StringComparison.Ordinal)
            && html.Contains("1.1 小节", StringComparison.Ordinal),
            "h1 / h2");

        check("[Word阅读器] HTML 导出：粗体 / 斜体 / 下划线 / 删除线 run 输出 b/i/u/s",
            html.Contains("<b>", StringComparison.Ordinal)
            && html.Contains("<i>", StringComparison.Ordinal)
            && html.Contains("<u>", StringComparison.Ordinal)
            && html.Contains("<s>", StringComparison.Ordinal)
            && html.Contains(">格式<", StringComparison.Ordinal),
            "格式 run");

        check("[Word阅读器] HTML 导出：表格 gridSpan → colspan，表头行用 th 并加粗",
            html.Contains("colspan=\"2\"", StringComparison.Ordinal)
            && html.Contains("<th", StringComparison.Ordinal)
            && html.Contains("合并表头", StringComparison.Ordinal),
            "合并表头 / th");

        check("[Word阅读器] HTML 导出：图片输出自包含 data URI + alt",
            html.Contains("data:image/png;base64,", StringComparison.Ordinal)
            && html.Contains("alt=\"测试图片\"", StringComparison.Ordinal),
            "data URI / alt");

        check("[Word阅读器] HTML 导出：超链接输出 a href（只允许 http/https/mailto）",
            html.Contains("href=\"https://example.com/docs\"", StringComparison.Ordinal)
            && !WordHtmlExporter.BuildHtml(annotated).Contains("href=\"javascript:", StringComparison.Ordinal),
            "示例链接 / 危险链接");

        var capped = WordHtmlExporter.BuildHtml(minimal, 1, out var omitted);
        check("[Word阅读器] HTML 导出：图片总体积上限（超限图片输出占位文字并计数）",
            omitted == 1
            && !capped.Contains("data:image/png;base64,", StringComparison.Ordinal)
            && capped.Contains("图片过大，已省略", StringComparison.Ordinal),
            $"省略 {omitted} 张");

        var annotatedHtml = WordHtmlExporter.BuildHtml(annotated);
        check("[Word阅读器] HTML 导出：<script> 等用户文字被转义，不能变成标签",
            annotatedHtml.Contains("&lt;script&gt;alert", StringComparison.Ordinal)
            && !annotatedHtml.Contains("<script>alert", StringComparison.Ordinal)
            && !annotatedHtml.Contains("</script>", StringComparison.Ordinal),
            "script 转义");

        check("[Word阅读器] HTML 导出：& / 双引号 / 单引号都转成实体",
            annotatedHtml.Contains("&amp;", StringComparison.Ordinal)
            && annotatedHtml.Contains("&quot;", StringComparison.Ordinal)
            && annotatedHtml.Contains("&#39;单引号&#39;", StringComparison.Ordinal),
            "实体转义");

        check("[Word阅读器] HTML 导出：非法控制字符被丢弃（不生成坏 HTML）",
            WordHtmlExporter.EscapeText("a\u0001b\u0002c") == "abc"
            && !annotatedHtml.Contains('\u0001'),
            WordHtmlExporter.EscapeText("a\u0001b\u0002c"));

        check("[Word阅读器] HTML 导出：修订删除内容不导出、插入内容正常导出",
            annotatedHtml.Contains("插入的文字", StringComparison.Ordinal)
            && !annotatedHtml.Contains("删除的文字", StringComparison.Ordinal),
            "插入 / 删除");

        var empty = new WordDocument();
        check("[Word阅读器] HTML 导出：空文档也有提示页，不会抛异常",
            WordHtmlExporter.BuildHtml(empty).Contains("没有可显示的正文内容", StringComparison.Ordinal)
            && empty.ExtractPlainText().Length == 0,
            "空文档");
    }

    private static void CheckTextExport(Action<string, bool, string> check, WordDocument annotated, string root)
    {
        var path = Path.Combine(root, "导出纯文本.txt");
        var characters = WordTextExporter.Write(annotated, path);
        var bytes = File.ReadAllBytes(path);
        var text = File.ReadAllText(path);

        check("[Word阅读器] 纯文本导出：UTF-8 BOM + 段落换行 + 跳过修订删除内容",
            characters > 0
            && bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            && text.Contains("插入的文字", StringComparison.Ordinal)
            && !text.Contains("删除的文字", StringComparison.Ordinal)
            && text.Contains('\n'),
            $"{characters} 字 / {bytes.Length} 字节");

        var emptyPath = Path.Combine(root, "导出空文本.txt");
        var emptyCharacters = WordTextExporter.Write(new WordDocument(), emptyPath);
        check("[Word阅读器] 纯文本导出：空文档写 0 字文件不崩（仍带 BOM）",
            emptyCharacters == 0 && File.Exists(emptyPath) && File.ReadAllBytes(emptyPath).Length == 3,
            $"{emptyCharacters} 字");
    }

    // ---------------------------------------------------------------- 新开关配置

    private static void CheckCommentRevisionConfig(Action<string, bool, string> check, string root)
    {
        var config = new WordViewerConfig();
        check("[Word阅读器] 新开关默认值：showComments / showRevisions 都是 true",
            config.ShowComments && config.ShowRevisions,
            $"批注={config.ShowComments} / 修订={config.ShowRevisions}");

        config.ShowComments = false;
        config.ShowRevisions = false;
        config.ZoomPercent = 200;
        config.ResetToDefaults();
        check("[Word阅读器] 恢复默认：批注 / 修订开关回 true，缩放回 100",
            config.ShowComments && config.ShowRevisions && config.ZoomPercent == 100,
            $"{config.ShowComments} / {config.ShowRevisions} / {config.ZoomPercent}");

        var file = Path.Combine(root, "批注修订配置.docx");
        File.WriteAllText(file, "x");
        config.RememberRecent(file);
        config.RememberReadingPosition(file, new WordReadingPosition { ScrollRatio = 0.5, SavedAtUtc = DateTime.UtcNow });
        var json = config.ToJson();
        check("[Word阅读器] 新开关进 JSON；最近打开 / 阅读位置记录不受影响",
            json.Contains("\"showComments\"", StringComparison.Ordinal)
            && json.Contains("\"showRevisions\"", StringComparison.Ordinal)
            && json.Contains("\"recentEntries\"", StringComparison.Ordinal)
            && json.Contains("\"readingPositions\"", StringComparison.Ordinal)
            && config.RecentEntries.Count == 1
            && config.FindReadingPosition(file) is not null,
            json.Length > 200 ? json[..200] + "…" : json);
    }

    // ---------------------------------------------------------------- 缺 comments.xml 容错

    private static void CheckMissingComments(
        Action<string, bool, string> check, string barePath, WordDocument reference)
    {
        Exception? failure = null;
        WordDocument? bare = null;
        try { bare = WordDocumentParser.Parse(barePath); }
        catch (Exception ex) { failure = ex; }

        var taggedRun = bare?.Blocks.OfType<WordParagraphBlock>()
            .SelectMany(paragraph => paragraph.Runs)
            .FirstOrDefault(run => run.CommentIds.Contains(5));

        check("[Word阅读器] 缺 comments.xml 正常打开：批注列表为空、正文区间标记仍保留、统计不变",
            failure is null && bare is not null
            && bare.Comments.Count == 0
            && !bare.HasComments
            && taggedRun is not null
            && bare.Blocks.Count == reference.Blocks.Count
            && bare.ParagraphCount == reference.ParagraphCount,
            failure?.Message ?? $"批注 {bare?.Comments.Count} / 正文块 {bare?.Blocks.Count} / 引用标记{(taggedRun is null ? "丢失" : "保留")}");
    }

    // ---------------------------------------------------------------- 渲染相关小工具（STA 线程里用）

    /// <summary>整篇渲染文档的纯文本（段落 / 表格都取，用来断言「删除内容是否画出来」）。</summary>
    private static string TextOf(FlowDocument document)
        => string.Join("\n", document.Blocks.Select(TextOf));

    /// <summary>在渲染结果里找文字完全等于 <paramref name="text"/> 的 Run（含 Span / Hyperlink 里的）。</summary>
    private static Run? FindRunText(FlowDocument document, string text)
    {
        foreach (var block in document.Blocks)
        {
            var runs = block switch
            {
                Paragraph paragraph => EnumerateRuns(paragraph.Inlines),
                Table table => table.RowGroups
                    .SelectMany(group => group.Rows)
                    .SelectMany(row => row.Cells)
                    .SelectMany(cell => cell.Blocks.OfType<Paragraph>())
                    .SelectMany(paragraph => EnumerateRuns(paragraph.Inlines)),
                _ => Enumerable.Empty<Run>(),
            };

            foreach (var run in runs)
            {
                if (string.Equals(run.Text, text, StringComparison.Ordinal)) return run;
            }
        }

        return null;
    }

    /// <summary>递归展开 Inline（Run 直接返回；Span / Hyperlink 展开子节点）。</summary>
    private static IEnumerable<Run> EnumerateRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    yield return run;
                    break;
                case Span span:
                    foreach (var child in EnumerateRuns(span.Inlines)) yield return child;
                    break;
            }
        }
    }

    private static bool HasBrushColor(Brush? brush, Color expected)
        => brush is SolidColorBrush solid && solid.Color == expected;

    private static bool HasDecoration(Run? run, TextDecorationLocation location)
        => run?.TextDecorations?.Any(decoration => decoration.Location == location) == true;

    /// <summary>找第一个文字包含 <paramref name="text"/> 的正文段落（允许包含）。</summary>
    private static WordParagraphBlock? FindParagraph(WordDocument document, string text)
        => document.Blocks.OfType<WordParagraphBlock>()
            .FirstOrDefault(paragraph => paragraph.Text.Contains(text, StringComparison.Ordinal));

    // ---------------------------------------------------------------- 造一份「新功能」自检文档

    /// <summary>页眉 / 页脚 / 脚注 / 尾注 / 文档属性的文本，用于状态细节展示。</summary>
    private static string TextOfBlocks(IReadOnlyList<WordBlock> blocks) => string.Join("\n", blocks.Select(block =>
        block switch
        {
            WordParagraphBlock paragraph => paragraph.Text,
            WordTableBlock table => string.Join(" | ", table.Rows.Select(row => string.Join(" ",
                row.Cells.SelectMany(cell => cell.Blocks.OfType<WordParagraphBlock>())
                    .Select(paragraph => paragraph.Text)))),
            _ => "",
        }));

    /// <summary>渲染出来的块（段落 / 表格）的纯文本，供渲染断言用（STA 线程里调用）。</summary>
    private static string TextOf(Block block) => block switch
    {
        Paragraph paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text,
        Table table => string.Join(" ", table.RowGroups
            .SelectMany(group => group.Rows)
            .SelectMany(row => row.Cells)
            .SelectMany(cell => cell.Blocks.OfType<Paragraph>())
            .Select(paragraph => new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text)),
        _ => "",
    };

    /// <summary>找第一个文字包含（或完全等于）<paramref name="text"/> 的渲染块下标；找不到返回 -1。</summary>
    private static int IndexOfText(IReadOnlyList<Block> blocks, string text, bool exact = false)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            var blockText = TextOf(blocks[index]).Trim();
            if (exact
                ? string.Equals(blockText, text, StringComparison.Ordinal)
                : blockText.Contains(text, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static string TextOfBodyParagraph(IReadOnlyList<Block> blocks, string marker)
        => blocks.Select(TextOf).FirstOrDefault(text => text.Contains(marker, StringComparison.Ordinal)) ?? "";

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

    // ---------------------------------------------------------------- 造「新功能」自检 docx

    /// <summary>
    /// 造一份带**页眉 / 页脚 / 脚注 / 尾注 / 文档属性**的 docx（临时目录现造，跑完删）。
    /// <paramref name="withExtras"/> = false 时不写这些部件、也不写对应关系（但正文里仍有引用、
    /// sectPr 里仍有指向不存在关系的 r:id）：验证「缺部件不崩、正文照常、引用只显示标记」。
    /// </summary>
    private static void CreateFeatureDocx(string path, bool withExtras)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        var contentTypes = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
            </Types>
            """;

        var extraTypeOverrides = withExtras
            ? """
                <Override PartName="/word/header1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
                <Override PartName="/word/header2.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
                <Override PartName="/word/header3.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml"/>
                <Override PartName="/word/footer1.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footer+xml"/>
                <Override PartName="/word/footnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.footnotes+xml"/>
                <Override PartName="/word/endnotes.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.endnotes+xml"/>
              """
            : "";

        WriteText(archive, "[Content_Types].xml",
            contentTypes.Replace("</Types>", extraTypeOverrides + "</Types>"));

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
                xmlns:dc="http://purl.org/dc/elements/1.1/"
                xmlns:dcterms="http://purl.org/dc/terms/"
                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:title>功能自检标题</dc:title>
              <dc:creator>测试作者</dc:creator>
              <cp:lastModifiedBy>测试修改者</cp:lastModifiedBy>
              <dcterms:created xsi:type="dcterms:W3CDTF">2024-05-01T08:30:00Z</dcterms:created>
              <dcterms:modified xsi:type="dcterms:W3CDTF">2024-06-02T09:45:00Z</dcterms:modified>
            </cp:coreProperties>
            """);

        var extraRelationships = withExtras
            ? """
                <Relationship Id="rId20" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header1.xml"/>
                <Relationship Id="rId21" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>
                <Relationship Id="rId22" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header2.xml"/>
                <Relationship Id="rId23" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/header" Target="header3.xml"/>
              """
            : "";

        WriteText(archive, "word/_rels/document.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
            {extraRelationships}
            </Relationships>
            """);

        // 有附属部件：sectPr 指向真实关系；缺部件：指向不存在的 r:id（模拟关系文件缺失）
        var sectionXml = withExtras
            ? """
              <w:sectPr>
                <w:headerReference w:type="default" r:id="rId20"/>
                <w:headerReference w:type="first" r:id="rId22"/>
                <w:headerReference w:type="even" r:id="rId23"/>
                <w:footerReference w:type="default" r:id="rId21"/>
              </w:sectPr>
              """
            : """
              <w:sectPr>
                <w:headerReference w:type="default" r:id="rIdMissingHeader"/>
                <w:footerReference w:type="default" r:id="rIdMissingFooter"/>
              </w:sectPr>
              """;

        var documentXml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document
                xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <w:body>
                <w:p><w:r><w:t>功能自检正文</w:t></w:r></w:p>
                <w:tbl>
                  <w:tblGrid><w:gridCol w:w="1440"/><w:gridCol w:w="1440"/></w:tblGrid>
                  <w:tr>
                    <w:tc><w:p><w:r><w:t>表格甲</w:t></w:r></w:p></w:tc>
                    <w:tc><w:p><w:r><w:t>表格乙</w:t></w:r></w:p></w:tc>
                  </w:tr>
                </w:tbl>
                <w:p><w:r><w:t>引用顺序测试</w:t></w:r><w:r><w:footnoteReference w:id="2"/></w:r><w:r><w:t>（脚注甲）</w:t></w:r></w:p>
                <w:p><w:r><w:t>第二个引用</w:t></w:r><w:r><w:footnoteReference w:id="1"/></w:r></w:p>
                <w:p><w:r><w:t>带尾注</w:t></w:r><w:r><w:endnoteReference w:id="1"/></w:r></w:p>
                <w:p><w:r><w:t>Hello 表格外</w:t></w:r></w:p>
                <!-- §SECTION§ -->
              </w:body>
            </w:document>
            """.Replace("<!-- §SECTION§ -->", sectionXml, StringComparison.Ordinal);

        WriteText(archive, "word/document.xml", documentXml);

        if (!withExtras) return;

        WriteText(archive, "word/header1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                   xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <w:p><w:r><w:t>默认页眉文字</w:t></w:r></w:p>
              <w:tbl>
                <w:tblGrid><w:gridCol w:w="1440"/></w:tblGrid>
                <w:tr><w:tc><w:p><w:r><w:t>页眉表格</w:t></w:r></w:p></w:tc></w:tr>
              </w:tbl>
            </w:hdr>
            """);

        WriteText(archive, "word/header2.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:p><w:r><w:t>首页页眉文字</w:t></w:r></w:p>
            </w:hdr>
            """);

        WriteText(archive, "word/header3.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:hdr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:p><w:r><w:t>偶数页页眉文字</w:t></w:r></w:p>
            </w:hdr>
            """);

        WriteText(archive, "word/footer1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:ftr xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:p><w:r><w:t>页脚文字</w:t></w:r></w:p>
            </w:ftr>
            """);

        WriteText(archive, "word/footnotes.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:footnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:footnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:footnote>
              <w:footnote w:type="continuationSeparator" w:id="0"><w:p><w:r><w:continuationSeparator/></w:r></w:p></w:footnote>
              <w:footnote w:id="1"><w:p><w:r><w:t>第一条脚注内容</w:t></w:r></w:p></w:footnote>
              <w:footnote w:id="2">
                <w:p><w:r><w:t>第二条脚注第一段</w:t></w:r></w:p>
                <w:p><w:r><w:t>第二条脚注第二段</w:t></w:r></w:p>
              </w:footnote>
            </w:footnotes>
            """);

        WriteText(archive, "word/endnotes.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:endnotes xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:endnote w:type="separator" w:id="-1"><w:p><w:r><w:separator/></w:r></w:p></w:endnote>
              <w:endnote w:id="1"><w:p><w:r><w:t>第一条尾注内容</w:t></w:r></w:p></w:endnote>
            </w:endnotes>
            """);
    }

    // ---------------------------------------------------------------- 造「批注 / 修订」自检 docx

    /// <summary>
    /// 造一份带**批注（含跨 run / 跨段落区间、超长内容）与修订（w:ins / w:del + w:delText）**的 docx。
    /// <paramref name="withComments"/> = false 时不写 comments.xml 和对应关系，但正文里的
    /// commentRangeStart / commentReference 仍在：验证「缺批注部件不崩、区间标记保留」。
    /// 还带一段 HTML 转义用文字（<c>&amp; &lt;script&gt; 引号</c>）和一个 <c>javascript:</c> 危险链接。
    /// </summary>
    private static void CreateCommentRevisionDocx(string path, bool withComments)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        var commentsType = withComments
            ? """
                <Override PartName="/word/comments.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.comments+xml"/>
              """
            : "";

        WriteText(archive, "[Content_Types].xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
              {commentsType}
            </Types>
            """);

        WriteText(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);

        var commentsRelationship = withComments
            ? """
                <Relationship Id="rIdComments" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments" Target="comments.xml"/>
              """
            : "";

        WriteText(archive, "word/_rels/document.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rIdJs" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="javascript:alert(1)" TargetMode="External"/>
              {commentsRelationship}
            </Relationships>
            """);

        WriteText(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document
                xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <w:body>
                <!-- 批注 1：区间覆盖同一段里的两个 run，引用 run 在区间终点 -->
                <w:p>
                  <w:r><w:t>正文开头</w:t></w:r>
                  <w:commentRangeStart w:id="5"/>
                  <w:r><w:t>批注覆盖一</w:t></w:r>
                  <w:r><w:t>批注覆盖二</w:t></w:r>
                  <w:commentRangeEnd w:id="5"/>
                  <w:r><w:commentReference w:id="5"/></w:r>
                </w:p>
                <!-- 批注 2：区间跨两个段落 -->
                <w:p>
                  <w:commentRangeStart w:id="1"/>
                  <w:r><w:t>跨段第一段</w:t></w:r>
                </w:p>
                <w:p>
                  <w:r><w:t>跨段第二段</w:t></w:r>
                  <w:commentRangeEnd w:id="1"/>
                  <w:r><w:commentReference w:id="1"/></w:r>
                </w:p>
                <!-- 修订：插入（w:ins + w:t）与删除（w:del + w:delText） -->
                <w:p>
                  <w:r><w:t>修订前</w:t></w:r>
                  <w:ins w:id="10" w:author="王五" w:date="2024-05-01T08:00:00Z"><w:r><w:t>插入的文字</w:t></w:r></w:ins>
                  <w:del w:id="11" w:author="王五" w:date="2024-05-02T09:00:00Z"><w:r><w:delText>删除的文字</w:delText></w:r></w:del>
                  <w:r><w:t>修订后</w:t></w:r>
                </w:p>
                <!-- HTML 转义测试：尖括号 / & / 引号 / 单引号 -->
                <w:p><w:r><w:t>转义测试 &amp; &lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &#39;单引号&#39;</w:t></w:r></w:p>
                <!-- 危险协议链接：导出 HTML 时只能留文字 -->
                <w:p><w:hyperlink r:id="rIdJs"><w:r><w:t>危险链接</w:t></w:r></w:hyperlink></w:p>
                <w:sectPr/>
              </w:body>
            </w:document>
            """);

        if (!withComments) return;

        var longText = new string('长', WordDocumentParser.MaxCommentTextLength + 50);
        WriteText(archive, "word/comments.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:comments xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:comment w:id="5" w:author="张三" w:date="2024-04-01T12:00:00Z" w:initials="ZS">
                <w:p><w:r><w:t>第一条批注第一段</w:t></w:r></w:p>
                <w:p><w:r><w:t>第一条批注第二段</w:t></w:r></w:p>
              </w:comment>
              <w:comment w:id="1" w:author="李四" w:date="2024-04-02T12:00:00Z" w:initials="LS">
                <w:p><w:r><w:t>第二条批注内容</w:t></w:r></w:p>
              </w:comment>
              <w:comment w:id="9" w:author="王五" w:initials="WW">
                <w:p><w:r><w:t>没有正文位置的批注</w:t></w:r></w:p>
              </w:comment>
              <w:comment w:id="99" w:author="超长作者" w:initials="CC">
                <w:p><w:r><w:t>{longText}</w:t></w:r></w:p>
              </w:comment>
            </w:comments>
            """);
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
