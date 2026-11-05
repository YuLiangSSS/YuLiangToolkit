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
