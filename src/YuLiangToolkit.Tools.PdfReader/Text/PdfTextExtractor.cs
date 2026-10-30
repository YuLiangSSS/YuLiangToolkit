using YuLiangToolkit.Tools.PdfReader.Text.Extraction;

namespace YuLiangToolkit.Tools.PdfReader.Text;

/// <summary>
/// PDF 文字层 / 书签目录的提取器（纯手写解析，不引第三方库）。
///
/// <para>输入一个 PDF 文件路径，输出 <see cref="PdfTextLayer"/>：每页纯文本（页号 0 起、与 PDF 页序一致）
/// + 书签目录。内部实现拆在 <c>Text/Extraction/</c> 下：文件结构 / xref（传统表 + xref 流 + /Prev 链）/
/// 对象流 /ObjStm / Flate 解压 / 页树 / 内容流文本操作符 / 字体 ToUnicode CMap / 书签目录。</para>
///
/// <para><b>永不抛异常</b>：文件不存在、损坏、加密、扫描版、字体没映射……一律返回
/// <see cref="PdfTextLayer.Empty"/>（或每页文字为空的层），<see cref="PdfTextLayer.Note"/> 里写中文原因。
/// 阅读器照常翻页，只是搜索 / 目录暂时不可用。</para>
/// </summary>
public static class PdfTextExtractor
{
    /// <summary>解析一个 PDF 的文字层；失败时返回带说明的空层（不抛异常）。</summary>
    public static PdfTextLayer Extract(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return PdfTextLayer.Empty("PDF 文件路径为空。");

            if (cancellationToken.IsCancellationRequested)
                return PdfTextLayer.Empty("文字层提取已取消。");

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(filePath);
            }
            catch (Exception)
            {
                return PdfTextLayer.Empty("PDF 文件路径不合法。");
            }

            if (!fileInfo.Exists) return PdfTextLayer.Empty("PDF 文件不存在。");
            if (fileInfo.Length == 0) return PdfTextLayer.Empty("PDF 文件是空的。");

            // 解析结构（xref / 对象表 / 页树）：结构性失败会抛 PdfParseException（中文原因）
            var document = PdfDocument.Open(filePath, cancellationToken);
            var pages = document.GetPages(cancellationToken);
            if (pages.Count == 0) return PdfTextLayer.Empty("PDF 里没有找到任何页面。");

            var contentExtractor = new PdfContentTextExtractor(document);
            var pageTexts = new List<PdfPageText>(pages.Count);
            var pagesWithText = 0;

            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // 单页失败只让这一页文字为空，不影响其它页
                var text = "";
                try
                {
                    text = contentExtractor.ExtractPageText(page, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception)
                {
                    text = "";
                }

                if (!string.IsNullOrWhiteSpace(text)) pagesWithText++;
                pageTexts.Add(new PdfPageText(page.Index, text));
            }

            var outline = document.GetOutline(cancellationToken);
            var note = pagesWithText == 0
                ? "没有可提取的文字层（可能是扫描版 PDF，或字体缺少 ToUnicode 映射）。"
                : "";

            return new PdfTextLayer(pageTexts, outline, note);
        }
        catch (OperationCanceledException)
        {
            return PdfTextLayer.Empty("文字层提取已取消。");
        }
        catch (PdfParseException ex)
        {
            return PdfTextLayer.Empty(ex.Message);
        }
        catch (Exception ex)
        {
            return PdfTextLayer.Empty($"文字层提取失败：{ex.Message}");
        }
    }
}
