namespace YuLiangToolkit.Tools.PdfReader.Text;

/// <summary>一页提取出来的纯文本（按页号 0 起，用于搜索 / 结果预览）。</summary>
public sealed record PdfPageText(int PageIndex, string Text);

/// <summary>目录 / 书签里的一项：标题 + 跳到第几页（0 起）+ 缩进层级（1 起）。</summary>
public sealed record PdfOutlineItem(string Title, int PageIndex, int Level);

/// <summary>
/// 一个 PDF 的「文字层」：每页纯文本 + 书签目录。
///
/// <para><b>为什么需要它</b>：显示用的系统 <c>Windows.Data.Pdf</c> 只负责把页渲染成图片，
/// 没有文字提取、也没有目录 API；文字搜索和书签目录只能自己解析 PDF 对象结构
/// （纯手写，不引第三方库，见 docs/dev/pitfalls.md「PDF 阅读器」）。</para>
///
/// <para><b>降级策略</b>：解析失败 / 字体没有 ToUnicode 映射 / 扫描版 PDF 没有任何文字时，
/// 这里返回一个 <see cref="Empty"/>（<see cref="Note"/> 里写原因），**绝不抛异常**，
/// 阅读器照常翻页，只是搜索和目录暂时不可用。</para>
/// </summary>
public sealed class PdfTextLayer
{
    internal PdfTextLayer(IReadOnlyList<PdfPageText> pages, IReadOnlyList<PdfOutlineItem> outline, string note)
    {
        Pages = pages;
        Outline = outline;
        Note = note;
    }

    /// <summary>每页的文字（页序与 PDF 页序一致；解析不出来的页 Text 为空串）。</summary>
    public IReadOnlyList<PdfPageText> Pages { get; }

    /// <summary>书签目录（按文档顺序；没有目录时为空）。</summary>
    public IReadOnlyList<PdfOutlineItem> Outline { get; }

    /// <summary>空层 / 部分失败时的中文说明；正常为空串。</summary>
    public string Note { get; }

    /// <summary>有没有任何可搜索的文字。</summary>
    public bool HasText => Pages.Count > 0 && Pages.Any(page => !string.IsNullOrWhiteSpace(page.Text));

    /// <summary>有没有书签目录。</summary>
    public bool HasOutline => Outline.Count > 0;

    /// <summary>一个空文字层（解析失败 / 扫描版 PDF 时用），<paramref name="note"/> 是给用户看的原因。</summary>
    public static PdfTextLayer Empty(string note)
        => new(Array.Empty<PdfPageText>(), Array.Empty<PdfOutlineItem>(), note ?? "");

    /// <summary>
    /// 后台解析一个 PDF 的文字层（永不抛异常：失败返回 <see cref="Empty"/>）。
    /// 解析含 xref / 对象流 / Flate 解压 / 字体 ToUnicode / 页目录，见 <see cref="PdfTextExtractor"/>。
    /// </summary>
    public static async Task<PdfTextLayer> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Task.Run(() => PdfTextExtractor.Extract(filePath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Empty("文字层提取已取消。");
        }
        catch (Exception ex)
        {
            return Empty($"文字层提取失败：{ex.Message}");
        }
    }
}
