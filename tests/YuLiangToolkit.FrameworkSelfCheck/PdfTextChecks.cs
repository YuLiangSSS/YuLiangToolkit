using System.Globalization;
using System.IO.Compression;
using System.Text;
using YuLiangToolkit.Tools.PdfReader.Rendering;
using YuLiangToolkit.Tools.PdfReader.Text;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「PDF 文字层 / 书签目录」自检：不依赖任何真实 PDF 文件，全部在临时目录里现场造。
///
/// <para>覆盖：文字层契约；主项目 <see cref="PdfDemoPdf"/> 生成的 3 页演示 PDF（含页序 / 关键字 / 页脚页码）；
/// 手写小 PDF 生成器造出的 ToUnicode CMap（bfchar + bfrange，2 字节码 → 中文）、FlateDecode 内容流、
/// 两级书签目录（中文标题 + 页号 + 层级 + 命名目标 + 各种 /Dest 形式）、xref 流 + /ObjStm 对象流、
/// 间接 /Length、损坏 startxref 的全文扫描兜底、/Prev 增量更新；以及 <see cref="PdfTextSearch"/>
/// 的搜索 / 片段逻辑和「文件不存在 / 假 PDF / 空文件 / 取消」这些异常路径（永不抛异常）。</para>
/// </summary>
internal static class PdfTextChecks
{
    private const string PdfFileHeader = "%PDF-1.7\n%YLT pdf-text self-check\n";

    /// <summary>自检断言回调（和 Program.cs 的 check 对齐；detail 可省略）。</summary>
    private delegate void Checker(string name, bool ok, string detail = "");

    public static void Run(Action<string, bool, string> check, Action<string, bool> checkPlain)
    {
        void Check(string name, bool ok, string detail = "") => check("[PDF文字] " + name, ok, detail);

        RunLayerContract(Check);
        RunDemoPdf(Check);
        RunChineseToUnicodeAndOutline(Check);
        RunSimpleFontDifferences(Check);
        RunFlateContentPdf(Check);
        RunObjectStreamAndXrefStreamPdf(Check);
        RunXrefStreamPredictorPdf(Check);
        RunIndirectLengthPdf(Check);
        RunCorruptStartXrefPdf(Check);
        RunIncrementalUpdatePdf(Check);
        RunSearch(Check);
        RunRobustness(Check);
    }

    // ================================================================== 1. 文字层契约

    private static void RunLayerContract(Checker check)
    {
        var empty = PdfTextLayer.Empty("解析失败原因");
        check("空层 HasText 为 false", !empty.HasText);
        check("空层 HasOutline 为 false", !empty.HasOutline);
        check("空层保留 Note", empty.Note == "解析失败原因", $"实际=[{empty.Note}]");
        check("空层 Pages 为空", empty.Pages.Count == 0, $"实际={empty.Pages.Count}");
        check("空层 Outline 为空", empty.Outline.Count == 0, $"实际={empty.Outline.Count}");
        check("Empty(null) 的 Note 是空串", PdfTextLayer.Empty(null!).Note.Length == 0,
            $"实际=[{PdfTextLayer.Empty(null!).Note}]");
        check("空层不算有文字", !PdfTextLayer.Empty("x").HasText);
    }

    // ================================================================== 2. 主项目演示 PDF

    private static void RunDemoPdf(Checker check)
    {
        var path = WriteTempPdf("pdftext-demo-3pages.pdf", PdfDemoPdf.Create(3));
        var layer = PdfTextExtractor.Extract(path);

        check("演示 PDF 提取到 3 页", layer.Pages.Count == 3, $"实际={layer.Pages.Count} Note=[{layer.Note}]");
        check("演示 PDF 有文字", layer.HasText);
        check("演示 PDF 自带 3 条书签（/Outlines）", layer.Outline.Count == 3, $"实际={layer.Outline.Count}");
        check("演示 PDF 书签层级都是 1", layer.Outline.All(item => item.Level == 1));
        check("演示 PDF 书签都指向合法页",
            layer.Outline.All(item => item.PageIndex >= 0 && item.PageIndex < layer.Pages.Count));
        check("演示 PDF 书签标题非空", layer.Outline.All(item => item.Title.Length > 0));
        check("演示 PDF 正常解析时 Note 为空", layer.Note.Length == 0, $"实际=[{layer.Note}]");

        for (var i = 0; i < Math.Min(3, layer.Pages.Count); i++)
        {
            check($"演示 PDF 第 {i + 1} 页页号是 {i}", layer.Pages[i].PageIndex == i,
                $"实际={layer.Pages[i].PageIndex}");
            check($"演示 PDF 第 {i + 1} 页有文字", layer.Pages[i].Text.Length > 20,
                $"实际长度={layer.Pages[i].Text.Length}");
            check($"演示 PDF 第 {i + 1} 页含 YuLiangToolkit PDF Reader demo",
                layer.Pages[i].Text.Contains("YuLiangToolkit PDF Reader demo", StringComparison.Ordinal),
                Excerpt(layer.Pages[i].Text));
            check($"演示 PDF 第 {i + 1} 页含 Page {i + 1} / 3",
                layer.Pages[i].Text.Contains($"Page {i + 1} / 3", StringComparison.Ordinal),
                Excerpt(layer.Pages[i].Text));
        }

        check("演示 PDF 页序正确（第 1 页不含 Page 2）",
            layer.Pages.Count > 0 && !layer.Pages[0].Text.Contains("Page 2", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? ""));
        check("演示 PDF 第 1 页含滚动说明（滚轮 / 拖动惯性的文案）",
            layer.Pages.Count > 0 && layer.Pages[0].Text.Contains("wheel to scroll", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? ""));
        check("演示 PDF 每页都有大号页码文字",
            layer.Pages.Count == 3 && layer.Pages.All(page => page.Text.Contains("Page", StringComparison.Ordinal)));
    }

    // ================================================================== 3. ToUnicode（中文）+ 书签目录

    private static void RunChineseToUnicodeAndOutline(Checker check)
    {
        var path = WriteTempPdf("pdftext-chinese-outline.pdf", BuildChineseOutlinePdf());
        var layer = PdfTextExtractor.Extract(path);

        check("中文 PDF 提取到 2 页", layer.Pages.Count == 2, $"实际={layer.Pages.Count} Note=[{layer.Note}]");
        check("中文 PDF 有文字", layer.HasText);
        check("中文 PDF 正常解析时 Note 为空", layer.Note.Length == 0, $"实际=[{layer.Note}]");
        check("中文 PDF 第 1 页经 bfchar 解出「中文测试第一页」",
            layer.Pages.Count > 0 && layer.Pages[0].Text.Contains("中文测试第一页", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? ""));
        check("中文 PDF 第 1 页经 bfrange（起始串自增）解出「012」",
            layer.Pages.Count > 0 && layer.Pages[0].Text.Contains("012", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? ""));
        check("中文 PDF 第 1 页 bfrange 数组映射生效（「中文」出现两次）",
            layer.Pages.Count > 0 && Occurrences(layer.Pages[0].Text, "中文") >= 2,
            $"实际次数={(layer.Pages.Count > 0 ? Occurrences(layer.Pages[0].Text, "中文") : 0)}");
        check("中文 PDF 第 1 页没有替换乱码字符",
            layer.Pages.Count > 0 && !layer.Pages[0].Text.Contains('\uFFFD'));
        check("中文 PDF 第 1 页没有把 2 字节码原样输出",
            layer.Pages.Count > 0 && !layer.Pages[0].Text.Contains("0001", StringComparison.Ordinal));
        check("中文 PDF 第 2 页解出「第二页」",
            layer.Pages.Count > 1 && layer.Pages[1].Text.Contains("第二页", StringComparison.Ordinal),
            Excerpt(layer.Pages.Count > 1 ? layer.Pages[1].Text : ""));
        check("中文 PDF 第 2 页不含第 1 页内容",
            layer.Pages.Count > 1 && !layer.Pages[1].Text.Contains("中文测试第一页", StringComparison.Ordinal));
        check("中文 PDF 页号连续（0、1）",
            layer.Pages.Count == 2 && layer.Pages[0].PageIndex == 0 && layer.Pages[1].PageIndex == 1);

        // ---- 书签目录：两级 + 三种目标写法（页引用 / /A /GoTo / 命名目标）----
        check("目录条数是 6", layer.Outline.Count == 6, $"实际={layer.Outline.Count}");
        if (layer.Outline.Count >= 6)
        {
            var expected = new (string Title, int Page, int Level)[]
            {
                ("第一章", 0, 1),
                ("第一节", 1, 2),
                ("第二章", 1, 1),
                ("第三章", 0, 1),
                ("Appendix A", 0, 1),
                ("A(1) \\ B", 1, 1),
            };

            for (var i = 0; i < expected.Length; i++)
            {
                var item = layer.Outline[i];
                check($"目录第 {i + 1} 条标题是「{expected[i].Title}」", item.Title == expected[i].Title,
                    $"实际=[{item.Title}]");
                check($"目录第 {i + 1} 条页号是 {expected[i].Page}", item.PageIndex == expected[i].Page,
                    $"实际={item.PageIndex}");
                check($"目录第 {i + 1} 条层级是 {expected[i].Level}", item.Level == expected[i].Level,
                    $"实际={item.Level}");
            }

            check("目录里的页号都在有效范围内",
                layer.Outline.All(item => item.PageIndex >= 0 && item.PageIndex < layer.Pages.Count));
            check("目录标题都非空", layer.Outline.All(item => item.Title.Length > 0));
        }
        else
        {
            for (var i = 0; i < 6; i++) check($"目录第 {i + 1} 条（缺数据，占位失败）", false, "目录条数不足");
        }
    }

    // ================================================================== 4. 简单字体 /Differences 字形名

    private static void RunSimpleFontDifferences(Checker check)
    {
        var pdf = new TinyPdf();
        pdf.AddObject("<< /Type /Catalog /Pages 2 0 R >>");                                             // 1
        pdf.AddObject("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                      // 2
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"); // 3
        // 65→é 66→’ 90→– ：三个码位都用 /Differences 改掉默认 WinAnsi 映射
        pdf.AddStream("", Ascii("BT /F1 12 Tf 72 700 Td (\\101\\102\\132) Tj ET"));                       // 4
        pdf.AddObject(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica " +
            "/Encoding << /BaseEncoding /WinAnsiEncoding " +
            "/Differences [ 65 /eacute /quoteright 90 /endash ] >> >>");                                  // 5

        var layer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-differences.pdf", pdf.Build(1)));
        var text = layer.Pages.FirstOrDefault()?.Text ?? "";
        check("简单字体 /Differences 字形名解码正确（é ’ –）", text.Contains("é’–", StringComparison.Ordinal),
            Excerpt(text.Length > 0 ? text : layer.Note));
    }

    // ================================================================== 5. FlateDecode 内容流

    private static void RunFlateContentPdf(Checker check)
    {
        var pdf = new TinyPdf();
        pdf.AddObject("<< /Type /Catalog /Pages 2 0 R >>");                                             // 1
        pdf.AddObject("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                      // 2
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"); // 3
        pdf.AddStream(
            "",
            Ascii("BT /F1 14 Tf 72 700 Td (Flate decompressed content works fine) Tj ET"),
            flate: true);                                                                                 // 4
        pdf.AddObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"); // 5

        var layer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-flate.pdf", pdf.Build(1)));
        check("FlateDecode 内容流能解压出文字",
            layer.Pages.Count == 1 && layer.Pages[0].Text.Contains("Flate decompressed content works fine",
                StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? layer.Note));
    }

    // ================================================================== 6. xref 流 + /ObjStm 对象流

    private static void RunObjectStreamAndXrefStreamPdf(Checker check)
    {
        var layer = PdfTextExtractor.Extract(
            WriteTempPdf("pdftext-xrefstream-objstm.pdf", BuildXrefStreamObjectStreamPdf()));

        check("xref 流 + 对象流 PDF 提取到 2 页", layer.Pages.Count == 2,
            $"实际={layer.Pages.Count} Note=[{layer.Note}]");
        check("xref 流 + 对象流 PDF 有文字", layer.HasText);
        check("xref 流 + 对象流第 1 页含 Alpha page one",
            layer.Pages.Count > 0 && layer.Pages[0].Text.Contains("Alpha page one", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? layer.Note));
        check("xref 流 + 对象流第 2 页含 Beta page two",
            layer.Pages.Count > 1 && layer.Pages[1].Text.Contains("Beta page two", StringComparison.Ordinal),
            Excerpt(layer.Pages.Count > 1 ? layer.Pages[1].Text : layer.Note));
        check("xref 流 + 对象流页序正确（第 1 页不含 Beta）",
            layer.Pages.Count > 0 && !layer.Pages[0].Text.Contains("Beta", StringComparison.Ordinal));
        check("xref 流 + 对象流正常解析时 Note 为空", layer.Note.Length == 0, $"实际=[{layer.Note}]");
    }

    // ================================================================== 7. xref 流 + PNG 预测器

    private static void RunXrefStreamPredictorPdf(Checker check)
    {
        var layer = PdfTextExtractor.Extract(
            WriteTempPdf("pdftext-xrefstream-predictor.pdf", BuildXrefStreamObjectStreamPdf(usePredictor: true)));

        check("xref 流带 PNG 预测器时仍能建对象表（2 页）", layer.Pages.Count == 2,
            $"实际={layer.Pages.Count} Note=[{layer.Note}]");
        check("xref 流带 PNG 预测器时文字正常",
            layer.Pages.Count > 0 && layer.Pages[0].Text.Contains("Alpha page one", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? layer.Note));
    }

    // ================================================================== 8. 间接 /Length + 字符串转义

    private static void RunIndirectLengthPdf(Checker check)
    {
        const string content =
            "BT /F1 12 Tf 72 700 Td (Indirect length test) Tj 0 -20 Td (A\\(B\\)\\\\C\\101) Tj 0 -20 Td <48656C6C6F> Tj ET";
        var contentBytes = Ascii(content);

        var pdf = new TinyPdf();
        pdf.AddObject("<< /Type /Catalog /Pages 2 0 R >>");                                             // 1
        pdf.AddObject("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                      // 2
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"); // 3
        pdf.AddObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"); // 4
        pdf.AddStream("", contentBytes, lengthExpression: "6 0 R");                                      // 5
        pdf.AddObject(contentBytes.Length.ToString(CultureInfo.InvariantCulture));                        // 6

        var layer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-indirect-length.pdf", pdf.Build(1)));
        var text = layer.Pages.FirstOrDefault()?.Text ?? "";
        check("间接 /Length 的内容流能提取", layer.Pages.Count == 1 && text.Length > 0,
            $"实际页数={layer.Pages.Count} Note=[{layer.Note}]");
        check("内容流里的字面量字符串能提取", text.Contains("Indirect length test", StringComparison.Ordinal),
            Excerpt(text));
        check("字符串转义（\\( \\) \\\\ \\101）解码正确", text.Contains(@"A(B)\CA", StringComparison.Ordinal),
            Excerpt(text));
        check("十六进制字符串 <48656C6C6F> 解出 Hello", text.Contains("Hello", StringComparison.Ordinal),
            Excerpt(text));
    }

    // ================================================================== 9. 损坏 startxref：全文扫描兜底

    private static void RunCorruptStartXrefPdf(Checker check)
    {
        var pdf = BuildSinglePagePdf("fallback search works");
        var bytes = pdf.Build(1, corruptStartXref: true);
        var layer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-corrupt-startxref.pdf", bytes));

        check("startxref 指向错误位置时全文扫描兜底找到页", layer.Pages.Count == 1,
            $"实际={layer.Pages.Count} Note=[{layer.Note}]");
        check("startxref 损坏时文字仍能提取",
            layer.Pages.Count > 0 && layer.Pages[0].Text.Contains("fallback search works", StringComparison.Ordinal),
            Excerpt(layer.Pages.FirstOrDefault()?.Text ?? layer.Note));
    }

    // ================================================================== 10. /Prev 增量更新链

    private static void RunIncrementalUpdatePdf(Checker check)
    {
        var pdf = BuildSinglePagePdf("old version text");
        var baseBytes = pdf.Build(1);
        var oldStartXref = FindLastStartXref(baseBytes);
        check("增量更新测试的基准 PDF 有 startxref", oldStartXref > 0, $"实际={oldStartXref}");

        // 追加一个新版本的 content（对象 4），再追加一节 xref，trailer 用 /Prev 指向旧 xref
        var newContent = Ascii("BT /F1 12 Tf 72 700 Td (new version text) Tj ET");
        using var output = new MemoryStream();
        output.Write(baseBytes);
        var newObjectOffset = output.Position;
        output.Write(Ascii("4 0 obj\n"));
        output.Write(Ascii($"<< /Length {newContent.Length} >>\nstream\n"));
        output.Write(newContent);
        output.Write(Ascii("\nendstream\nendobj\n"));
        var newXrefOffset = output.Position;
        output.Write(Ascii($"xref\n4 1\n{newObjectOffset:D10} 00000 n \n"));
        output.Write(Ascii($"trailer\n<< /Size 6 /Root 1 0 R /Prev {oldStartXref} >>\n"));
        output.Write(Ascii($"startxref\n{newXrefOffset}\n%%EOF\n"));

        var layer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-incremental.pdf", output.ToArray()));
        var text = layer.Pages.FirstOrDefault()?.Text ?? "";
        check("增量更新（/Prev 链）取到新版本内容", text.Contains("new version text", StringComparison.Ordinal),
            Excerpt(text.Length > 0 ? text : layer.Note));
        check("增量更新后旧版本内容已被替换", !text.Contains("old version text", StringComparison.Ordinal),
            Excerpt(text.Length > 0 ? text : layer.Note));
    }

    // ================================================================== 11. 搜索 / 片段

    private static void RunSearch(Checker check)
    {
        var searchLayer = PdfTextExtractor.Extract(
            WriteTempPdf("pdftext-search-fixture.pdf", BuildSearchFixturePdf()));
        check("搜索夹具 PDF 有 3 页", searchLayer.Pages.Count == 3, $"实际={searchLayer.Pages.Count}");

        var beta = PdfTextSearch.Find(searchLayer, "beta");
        check("搜索 beta 命中 4 次（大小写不敏感）", beta.Count == 4, $"实际={beta.Count}");
        check("搜索 BETA 命中 4 次（大小写不敏感）", PdfTextSearch.Find(searchLayer, "BETA").Count == 4);
        check("搜索 GaMmA 命中 1 次", PdfTextSearch.Find(searchLayer, "GaMmA").Count == 1);
        check("搜索支持首尾空白裁剪", PdfTextSearch.Find(searchLayer, "   beta   ").Count == 4);
        check("空查询返回空结果", PdfTextSearch.Find(searchLayer, "").Count == 0);
        check("全空白查询返回空结果", PdfTextSearch.Find(searchLayer, "   ").Count == 0);
        check("null 查询返回空结果", PdfTextSearch.Find(searchLayer, null).Count == 0);
        check("null 文字层返回空结果", PdfTextSearch.Find(null, "beta").Count == 0);
        check("空文字层返回空结果", PdfTextSearch.Find(PdfTextLayer.Empty("x"), "beta").Count == 0);
        check("maxHits 上限生效（2 条）", PdfTextSearch.Find(searchLayer, "beta", maxHits: 2).Count == 2);
        check("maxHits 为 0 时至少返回 1 条", PdfTextSearch.Find(searchLayer, "beta", maxHits: 0).Count == 1);
        check("maxHits 为负时至少返回 1 条", PdfTextSearch.Find(searchLayer, "beta", maxHits: -5).Count == 1);

        if (beta.Count > 0)
        {
            check("首个命中在第 0 页", beta[0].PageIndex == 0, $"实际={beta[0].PageIndex}");
            check("首个命中的页内位置正确", beta[0].PageCharIndex == 6, $"实际={beta[0].PageCharIndex}");
            check("命中片段含命中词", beta[0].Snippet.Contains("Beta", StringComparison.OrdinalIgnoreCase),
                $"片段=[{beta[0].Snippet}]");
            check("命中片段长度受控", beta[0].Snippet.Length <= 60, $"实际={beta[0].Snippet.Length}");

            var ordered = true;
            for (var i = 1; i < beta.Count; i++)
            {
                var previous = beta[i - 1];
                var current = beta[i];
                if (current.PageIndex < previous.PageIndex) ordered = false;
                if (current.PageIndex == previous.PageIndex && current.PageCharIndex <= previous.PageCharIndex)
                    ordered = false;
            }

            check("命中按页序 / 页内位置排序", ordered);
            check("多页命中都带页码", beta.Any(hit => hit.PageIndex == 1) && beta.Any(hit => hit.PageIndex == 0));
        }

        // 中文搜索走 ToUnicode PDF
        var chineseLayer = PdfTextExtractor.Extract(
            WriteTempPdf("pdftext-chinese-search.pdf", BuildChineseOutlinePdf()));
        var chineseHits = PdfTextSearch.Find(chineseLayer, "中文");
        check("中文搜索命中 2 次", chineseHits.Count == 2, $"实际={chineseHits.Count}");
        check("中文搜索命中都在第 0 页",
            chineseHits.Count > 0 && chineseHits.All(hit => hit.PageIndex == 0));
        check("中文短语「第二页」命中 1 次且在第 1 页",
            PdfTextSearch.Find(chineseLayer, "第二页") is { Count: 1 } pageTwoHits && pageTwoHits[0].PageIndex == 1);

        // 片段构造
        var longText = new string('x', 100) + "needle" + new string('y', 100);
        var snippet = PdfTextSearch.BuildSnippet(longText, 100, 6);
        check("片段两端带省略号", snippet.StartsWith('…') && snippet.EndsWith('…'), $"片段=[{snippet}]");
        check("片段包含命中词", snippet.Contains("needle", StringComparison.Ordinal));
        check("片段长度受控", snippet.Length <= 60, $"实际={snippet.Length}");
        check("片段压缩连续空白",
            PdfTextSearch.BuildSnippet("aaa   bbb\n\nccc", 6, 3) == "aaa bbb ccc",
            $"实际=[{PdfTextSearch.BuildSnippet("aaa   bbb\n\nccc", 6, 3)}]");
        check("空文本的片段为空串", PdfTextSearch.BuildSnippet("", 0, 1) == "");
        check("null 文本的片段为空串", PdfTextSearch.BuildSnippet(null!, 0, 1) == "");
    }

    // ================================================================== 12. 异常 / 健壮性（永不抛异常）

    private static void RunRobustness(Checker check)
    {
        var missingPath = Path.Combine(SelfCheckScratch.Root, $"pdftext-not-exist-{Guid.NewGuid():N}.pdf");
        var missing = PdfTextExtractor.Extract(missingPath);
        check("不存在的文件返回空层且不抛异常", missing.Pages.Count == 0, $"实际页数={missing.Pages.Count}");
        check("不存在的文件 Note 说明原因", missing.Note.Contains("不存在", StringComparison.Ordinal),
            $"实际=[{missing.Note}]");

        var emptyFile = WriteTempPdf("pdftext-empty.pdf", Array.Empty<byte>());
        var empty = PdfTextExtractor.Extract(emptyFile);
        check("空文件返回空层且 Note 非空", empty.Pages.Count == 0 && empty.Note.Length > 0,
            $"实际页数={empty.Pages.Count} Note=[{empty.Note}]");

        var random = new Random(20261030);
        var garbage = new byte[2048];
        random.NextBytes(garbage);
        var garbageLayer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-garbage.pdf", garbage));
        check("随机字节假 PDF 返回空层", garbageLayer.Pages.Count == 0, $"实际页数={garbageLayer.Pages.Count}");
        check("随机字节假 PDF 的 Note 说明不是有效 PDF",
            garbageLayer.Note.Contains("不是有效", StringComparison.Ordinal), $"实际=[{garbageLayer.Note}]");

        var headerThenGarbage = Combine(Ascii(PdfFileHeader), garbage);
        var headerLayer = PdfTextExtractor.Extract(WriteTempPdf("pdftext-header-garbage.pdf", headerThenGarbage));
        check("有文件头但内容是垃圾时返回空层", headerLayer.Pages.Count == 0,
            $"实际页数={headerLayer.Pages.Count}");
        check("有文件头但内容是垃圾时 Note 说明原因", headerLayer.Note.Length > 0,
            $"实际=[{headerLayer.Note}]");

        var textFile = WriteTempPdf("pdftext-not-pdf.pdf", Encoding.UTF8.GetBytes("这不是 PDF，只是一段普通文本。"));
        var textLayer = PdfTextExtractor.Extract(textFile);
        check("普通文本文件返回空层", textLayer.Pages.Count == 0 && textLayer.Note.Length > 0,
            $"实际页数={textLayer.Pages.Count} Note=[{textLayer.Note}]");

        var demoBytes = PdfDemoPdf.Create(3);
        var truncated = PdfTextExtractor.Extract(
            WriteTempPdf("pdftext-truncated.pdf", demoBytes[..(demoBytes.Length / 2)]));
        check("截断的 PDF 不崩溃（页数不超过原页数）", truncated.Pages.Count <= 3,
            $"实际页数={truncated.Pages.Count}");

        var directoryLayer = PdfTextExtractor.Extract(SelfCheckScratch.Root);
        check("传目录路径返回空层且不抛异常",
            directoryLayer.Pages.Count == 0 && directoryLayer.Note.Length > 0,
            $"实际页数={directoryLayer.Pages.Count} Note=[{directoryLayer.Note}]");

        var nullPath = PdfTextExtractor.Extract(null!);
        check("null 路径返回空层", nullPath.Pages.Count == 0 && nullPath.Note.Length > 0,
            $"实际=[{nullPath.Note}]");
        check("null 路径 Note 说明路径为空", nullPath.Note.Contains("路径为空", StringComparison.Ordinal),
            $"实际=[{nullPath.Note}]");

        var blankPath = PdfTextExtractor.Extract("   ");
        check("空白路径返回空层且 Note 非空", blankPath.Pages.Count == 0 && blankPath.Note.Length > 0,
            $"实际=[{blankPath.Note}]");

        var demoPath = WriteTempPdf("pdftext-cancel.pdf", PdfDemoPdf.Create(2));
        var canceled = PdfTextExtractor.Extract(demoPath, new CancellationToken(canceled: true));
        check("已取消的 token 返回空层", canceled.Pages.Count == 0, $"实际页数={canceled.Pages.Count}");
        check("已取消的 token Note 说明取消", canceled.Note.Contains("取消", StringComparison.Ordinal),
            $"实际=[{canceled.Note}]");

        var asyncMissing = PdfTextLayer.LoadAsync(missingPath).GetAwaiter().GetResult();
        check("LoadAsync 文件不存在返回空层", asyncMissing.Pages.Count == 0 && asyncMissing.Note.Length > 0,
            $"实际页数={asyncMissing.Pages.Count} Note=[{asyncMissing.Note}]");
        var asyncDemo = PdfTextLayer.LoadAsync(demoPath).GetAwaiter().GetResult();
        check("LoadAsync 正常 PDF 返回 2 页", asyncDemo.Pages.Count == 2, $"实际={asyncDemo.Pages.Count}");
    }

    // ================================================================== 手写 PDF 生成器

    /// <summary>
    /// 极简 PDF 写出器：按顺序 AddObject / AddStream，最后 Build 出「传统 xref 表 + trailer」的合法 PDF。
    /// 只够自检用，不追求通用。
    /// </summary>
    private sealed class TinyPdf
    {
        private readonly List<byte[]> _objects = new();

        /// <summary>加一个普通对象（内容是不带 <c>N 0 obj</c> 包裹的对象体）。</summary>
        public int AddObject(string body) => AddObject(Ascii(body));

        public int AddObject(byte[] body)
        {
            _objects.Add(body);
            return _objects.Count; // 对象号 = 1 起的序号
        }

        /// <summary>加一个流对象；<paramref name="lengthExpression"/> 可改成间接引用（例如 "6 0 R"）。</summary>
        public int AddStream(string extraEntries, byte[] data, bool flate = false, string? lengthExpression = null)
        {
            var payload = flate ? Flate(data) : data;
            var length = lengthExpression ?? payload.Length.ToString(CultureInfo.InvariantCulture);
            var dictionary =
                $"<< /Length {length}{(extraEntries.Length > 0 ? " " + extraEntries : "")}" +
                $"{(flate ? " /Filter /FlateDecode" : "")} >>";
            return AddObject(Combine(Ascii(dictionary + "\nstream\n"), payload, Ascii("\nendstream")));
        }

        /// <summary>生成完整文件字节；<paramref name="corruptStartXref"/> = 故意写一个错误的 startxref（测全文扫描兜底）。</summary>
        public byte[] Build(int rootObjectNumber, bool corruptStartXref = false)
        {
            using var output = new MemoryStream();
            output.Write(Ascii(PdfFileHeader));

            var offsets = new List<long>(_objects.Count);
            for (var i = 0; i < _objects.Count; i++)
            {
                offsets.Add(output.Position);
                output.Write(Ascii($"{i + 1} 0 obj\n"));
                output.Write(_objects[i]);
                output.Write(Ascii("\nendobj\n"));
            }

            var xrefPosition = output.Position;
            output.Write(Ascii($"xref\n0 {_objects.Count + 1}\n"));
            output.Write(Ascii("0000000000 65535 f \n"));
            foreach (var offset in offsets) output.Write(Ascii($"{offset:D10} 00000 n \n"));
            output.Write(Ascii($"trailer\n<< /Size {_objects.Count + 1} /Root {rootObjectNumber} 0 R >>\n"));
            output.Write(Ascii($"startxref\n{(corruptStartXref ? 12345 : xrefPosition)}\n%%EOF\n"));
            return output.ToArray();
        }
    }

    private static TinyPdf BuildSinglePagePdf(string pageText)
    {
        var pdf = new TinyPdf();
        pdf.AddObject("<< /Type /Catalog /Pages 2 0 R >>");                                             // 1
        pdf.AddObject("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");                                      // 2
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>"); // 3
        pdf.AddStream("", Ascii($"BT /F1 12 Tf 72 700 Td ({EscapePdfLiteral(pageText)}) Tj ET"));        // 4
        pdf.AddObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"); // 5
        return pdf;
    }

    /// <summary>
    /// 手写中文 PDF：Type0 字体 + ToUnicode CMap（bfchar + bfrange + bfrange 数组）、
    /// FlateDecode 内容流、两级书签目录（页引用 / /A /GoTo / 命名目标 / 字面量标题 / 转义标题）。
    /// </summary>
    private static byte[] BuildChineseOutlinePdf()
    {
        var pdf = new TinyPdf();

        pdf.AddObject("<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R /Names << /Dests 16 0 R >> >>"); // 1
        pdf.AddObject("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");                                 // 2
        pdf.AddObject(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>");                                  // 3
        pdf.AddObject(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>");                                  // 4
        pdf.AddObject("<< /Type /Font /Subtype /Type0 /BaseFont /SimSun /Encoding /Identity-H /ToUnicode 8 0 R >>"); // 5
        pdf.AddStream("", Ascii(
                "BT /F1 12 Tf 72 700 Td <0001000200070008000300040006> Tj " +
                "0 -20 Td <002000210022> Tj 0 -20 Td <00300031> Tj ET"),
            flate: true);                                                                                  // 6
        pdf.AddStream("", Ascii("BT /F1 12 Tf 72 700 Td <000300050006> Tj ET"), flate: true);              // 7
        pdf.AddStream("", Ascii(BuildToUnicodeCmap()), flate: true);                                       // 8
        pdf.AddObject("<< /Producer (YLT self-check) >>");                                                 // 9（占位，不引用）
        pdf.AddObject("<< /Type /Outlines /First 11 0 R /Last 19 0 R /Count 6 >>");                        // 10
        pdf.AddObject(
            $"<< /Title {Utf16BeHex("第一章")} /Parent 10 0 R /Dest [3 0 R /XYZ 0 792 0] " +
            "/First 13 0 R /Next 12 0 R >>");                                                             // 11
        pdf.AddObject(
            $"<< /Title {Utf16BeHex("第二章")} /Parent 10 0 R " +
            "/A << /S /GoTo /D [4 0 R /XYZ 0 700 0] >> /Prev 11 0 R /Next 15 0 R >>");                   // 12
        pdf.AddObject(
            $"<< /Title {Utf16BeHex("第一节")} /Parent 11 0 R /Dest [4 0 R /Fit] >>");                    // 13
        pdf.AddObject("<< >>");                                                                            // 14（占位）
        pdf.AddObject(
            $"<< /Title {Utf16BeHex("第三章")} /Parent 10 0 R /Dest (chap3) " +
            "/Prev 12 0 R /Next 18 0 R >>");                                                              // 15
        pdf.AddObject("<< /Names [ (chap3) 17 0 R ] >>");                                                  // 16
        pdf.AddObject("<< /D [3 0 R /Fit] >>");                                                            // 17
        pdf.AddObject(
            "<< /Title (Appendix A) /Parent 10 0 R /Dest [3 0 R /Fit] /Prev 15 0 R /Next 19 0 R >>");     // 18
        pdf.AddObject(
            "<< /Title (A\\(1\\) \\\\ B) /Parent 10 0 R /Dest [4 0 R /Fit] /Prev 18 0 R >>");             // 19

        return pdf.Build(1);
    }

    /// <summary>ToUnicode CMap：codespace 2 字节；bfchar 单码；bfrange 起始串自增 + 数组两种写法。</summary>
    private static string BuildToUnicodeCmap()
    {
        return string.Join("\n", new[]
        {
            "/CIDInit /ProcSet findresource begin",
            "12 dict begin",
            "begincmap",
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def",
            "/CMapName /Adobe-Identity-UCS def",
            "/CMapType 2 def",
            "1 begincodespacerange",
            "<0000> <FFFF>",
            "endcodespacerange",
            "8 beginbfchar",
            "<0001> <4E2D>", // 中
            "<0002> <6587>", // 文
            "<0003> <7B2C>", // 第
            "<0004> <4E00>", // 一
            "<0005> <4E8C>", // 二
            "<0006> <9875>", // 页
            "<0007> <6D4B>", // 测
            "<0008> <8BD5>", // 试
            "endbfchar",
            "2 beginbfrange",
            "<0020> <0022> <0030>",          // 0 1 2（起始串自增）
            "<0030> <0031> [<4E2D> <6587>]", // 中 文（目标数组）
            "endbfrange",
            "endcmap",
            "CMapName currentdict /CMap defineresource pop",
            "end",
            "end",
        });
    }

    /// <summary>搜索夹具：3 页英文，用来断言大小写不敏感 / 多命中顺序 / maxHits / 片段。</summary>
    private static byte[] BuildSearchFixturePdf()
    {
        var pdf = new TinyPdf();
        pdf.AddObject("<< /Type /Catalog /Pages 2 0 R >>");                                                 // 1
        pdf.AddObject("<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>");                              // 2
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 6 0 R >>"); // 3
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 7 0 R >>"); // 4
        pdf.AddObject("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> /Contents 8 0 R >>"); // 5
        pdf.AddStream("", Ascii(
            "BT /F1 12 Tf 72 700 Td (Alpha Beta gamma delta epsilon zeta eta theta iota kappa lambda) Tj ET")); // 6
        pdf.AddStream("", Ascii("BT /F1 12 Tf 72 700 Td (beta beta BETA) Tj ET"));                            // 7
        pdf.AddStream("", Ascii("BT /F1 12 Tf 72 700 Td (third page no match here) Tj ET"));                  // 8
        pdf.AddObject("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");   // 9
        return pdf.Build(1);
    }

    /// <summary>
    /// 手写 xref 流 + 对象流 PDF：目录 / 页树 / 页面 / 字体都塞进 /ObjStm 压缩对象流，
    /// 末尾用 /Type /XRef 的 FlateDecode 流当交叉引用表（/W [1 3 2]），完全不走传统 xref 表。
    /// </summary>
    private static byte[] BuildXrefStreamObjectStreamPdf(bool usePredictor = false)
    {
        // 6、7 是两页的内容流（普通对象），8 是对象流，9 是 xref 流
        var pageOneContent = Ascii("BT /F1 24 Tf 72 700 Td (Alpha page one) Tj ET");
        var pageTwoContent = Ascii("BT /F1 24 Tf 72 700 Td (Beta page two) Tj ET");

        var objectBodies = new List<byte[]>
        {
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
            Ascii("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                  "/Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"),
        };
        var (objectStreamBody, _) = BuildObjectStream(objectBodies);

        using var output = new MemoryStream();
        output.Write(Ascii(PdfFileHeader));
        var offsets = new long[10];

        void WriteObject(int number, byte[] body)
        {
            offsets[number] = output.Position;
            output.Write(Ascii($"{number} 0 obj\n"));
            output.Write(body);
            output.Write(Ascii("\nendobj\n"));
        }

        WriteObject(6, BuildFlateStreamBody("", pageOneContent));
        WriteObject(7, BuildFlateStreamBody("", pageTwoContent));
        WriteObject(8, objectStreamBody);

        var xrefOffset = output.Position;
        var xrefData = new MemoryStream();
        void Row(int type, long second, int third)
        {
            xrefData.WriteByte((byte)type);
            xrefData.WriteByte((byte)((second >> 16) & 0xFF));
            xrefData.WriteByte((byte)((second >> 8) & 0xFF));
            xrefData.WriteByte((byte)(second & 0xFF));
            xrefData.WriteByte((byte)((third >> 8) & 0xFF));
            xrefData.WriteByte((byte)(third & 0xFF));
        }

        Row(0, 0, 65535);                 // 对象 0：空闲
        for (var i = 0; i < 5; i++) Row(2, 8, i); // 对象 1~5 在对象流 8 里
        Row(1, offsets[6], 0);
        Row(1, offsets[7], 0);
        Row(1, offsets[8], 0);
        Row(1, xrefOffset, 0);            // 对象 9：xref 流自己

        var xrefRows = xrefData.ToArray();
        var encodedXref = usePredictor ? EncodePngUpPredictor(xrefRows, rowLength: 6) : xrefRows;
        var compressedXref = Flate(encodedXref);
        var predictorEntry = usePredictor ? " /DecodeParms << /Predictor 12 /Columns 6 >>" : "";
        var xrefDictionary =
            $"<< /Type /XRef /Size 10 /W [1 3 2] /Index [0 10] /Root 1 0 R " +
            $"/Filter /FlateDecode{predictorEntry} /Length {compressedXref.Length} >>";
        WriteObject(9, Combine(Ascii(xrefDictionary + "\nstream\n"), compressedXref, Ascii("\nendstream")));

        output.Write(Ascii($"startxref\n{xrefOffset}\n%%EOF\n"));
        return output.ToArray();
    }

    /// <summary>把若干对象体压进 /ObjStm（带 FlateDecode），返回对象体字节和 /First 值。</summary>
    private static (byte[] Body, int First) BuildObjectStream(List<byte[]> objectBodies)
    {
        var header = new StringBuilder();
        using var bodies = new MemoryStream();
        for (var i = 0; i < objectBodies.Count; i++)
        {
            header.Append(i + 1).Append(' ').Append(bodies.Length).Append(' ');
            bodies.Write(objectBodies[i]);
            bodies.WriteByte((byte)'\n');
        }

        var headerBytes = Ascii(header.ToString());
        var raw = Combine(headerBytes, bodies.ToArray());
        var compressed = Flate(raw);
        var dictionary =
            $"<< /Type /ObjStm /N {objectBodies.Count} /First {headerBytes.Length} " +
            $"/Filter /FlateDecode /Length {compressed.Length} >>";
        return (Combine(Ascii(dictionary + "\nstream\n"), compressed, Ascii("\nendstream")), headerBytes.Length);
    }

    /// <summary>把定长行数据编码成 PNG Up 预测器格式（每行前面加一个 filter=2 字节）；测 xref 流解码用。</summary>
    private static byte[] EncodePngUpPredictor(byte[] rows, int rowLength)
    {
        var rowCount = rows.Length / rowLength;
        var output = new byte[rowCount * (rowLength + 1)];
        var previous = new byte[rowLength];
        for (var row = 0; row < rowCount; row++)
        {
            output[row * (rowLength + 1)] = 2; // PNG Up
            for (var i = 0; i < rowLength; i++)
                output[row * (rowLength + 1) + 1 + i] = (byte)(rows[row * rowLength + i] - previous[i]);
            Array.Copy(rows, row * rowLength, previous, 0, rowLength);
        }

        return output;
    }

    private static byte[] BuildFlateStreamBody(string extraEntries, byte[] data)
    {
        var compressed = Flate(data);
        var dictionary =
            $"<< /Length {compressed.Length}{(extraEntries.Length > 0 ? " " + extraEntries : "")} " +
            "/Filter /FlateDecode >>";
        return Combine(Ascii(dictionary + "\nstream\n"), compressed, Ascii("\nendstream"));
    }

    // ================================================================== 小工具

    private static string WriteTempPdf(string fileName, byte[] bytes)
    {
        var path = SelfCheckScratch.MakeFile(fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Combine(params byte[][] parts)
    {
        var length = parts.Sum(part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }

    /// <summary>用 zlib（ZLibStream）压一段数据，给自检 PDF 的 FlateDecode 流用。</summary>
    private static byte[] Flate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            stream.Write(data, 0, data.Length);
        return output.ToArray();
    }

    /// <summary>PDF 字面量字符串转义（只处理括号与反斜杠，够自检用）。</summary>
    private static string EscapePdfLiteral(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '(': builder.Append("\\("); break;
                case ')': builder.Append("\\)"); break;
                default: builder.Append(ch); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>UTF-16BE 的 PDF 十六进制字符串（带 FE FF BOM），中文书签标题用。</summary>
    private static string Utf16BeHex(string text)
    {
        var builder = new StringBuilder("<FEFF", text.Length * 4 + 6);
        foreach (var ch in text) builder.Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture));
        builder.Append('>');
        return builder.ToString();
    }

    private static int Occurrences(string text, string needle)
    {
        if (text.Length == 0 || needle.Length == 0) return 0;
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static string Excerpt(string text)
    {
        if (text.Length == 0) return "（空）";
        var oneLine = text.Replace("\r", " ").Replace("\n", " | ");
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "…";
    }

    private static long FindLastStartXref(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var index = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (index < 0) return -1;
        var position = index + "startxref".Length;
        while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        var start = position;
        while (position < text.Length && char.IsDigit(text[position])) position++;
        return position > start && long.TryParse(text[start..position], out var value) ? value : -1;
    }
}
