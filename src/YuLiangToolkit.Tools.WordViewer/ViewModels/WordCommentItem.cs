using YuLiangToolkit.Tools.WordViewer.Parsing;

namespace YuLiangToolkit.Tools.WordViewer.ViewModels;

/// <summary>
/// 右侧批注面板里的一行（对应 <see cref="WordComment"/>，已经把作者 / 日期 / 内容截断算好，XAML 直接绑）。
/// <para>面板只显示摘要（多行截断），鼠标悬停用 <see cref="FullText"/> 显示全文；
/// 点一行由 View 的代码后置调用 <c>WordViewerViewModel.BringCommentIntoView</c> 跳到正文。</para>
/// </summary>
public sealed class WordCommentItem
{
    /// <summary>面板摘要最多显示多少行。</summary>
    private const int PreviewMaxLines = 4;

    /// <summary>面板摘要最多显示多少字符（防止一条超长批注把面板撑爆）。</summary>
    private const int PreviewMaxChars = 160;

    /// <summary>从解析出来的批注建一行。</summary>
    public WordCommentItem(WordComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        Number = comment.Number;
        Author = string.IsNullOrWhiteSpace(comment.Author) ? "（未知作者）" : comment.Author.Trim();
        Initials = comment.Initials;
        DateText = comment.DateText;
        FullText = comment.Text;
        BlockIndex = comment.BlockIndex;

        PreviewText = BuildPreview(comment.Text);
    }

    /// <summary>批注显示序号（1 起，按正文出现顺序）。</summary>
    public int Number { get; }

    /// <summary>作者（没有时显示「（未知作者）」，保证这一行有作者信息）。</summary>
    public string Author { get; }

    /// <summary>作者缩写（<c>w:initials</c>），没有时为空串。</summary>
    public string Initials { get; }

    /// <summary>批注日期文本（本地时间）；文件里没写日期时为空串。</summary>
    public string DateText { get; }

    /// <summary>面板第一行：「批注 N · 作者 · 日期」。</summary>
    public string HeaderText =>
        $"批注 {Number} · {Author}" + (DateText.Length > 0 ? $" · {DateText}" : "");

    /// <summary>面板正文摘要（最多 4 行 / 160 字，超出加省略号）。</summary>
    public string PreviewText { get; }

    /// <summary>批注全文（鼠标悬停提示用；单条超过解析上限时末尾已带省略说明）。</summary>
    public string FullText { get; }

    /// <summary>被批注正文的起始块下标；-1 = 没有正文位置（点了只给提示）。</summary>
    public int BlockIndex { get; }

    /// <summary>有没有可跳转的正文位置。</summary>
    public bool HasAnchor => BlockIndex >= 0;

    /// <summary>悬停提示：标题行 + 全文（无内容时给一句说明）。</summary>
    public string ToolTipText =>
        FullText.Trim().Length == 0 ? HeaderText + Environment.NewLine + "（这条批注没有文字内容）"
        : HeaderText + Environment.NewLine + Environment.NewLine + FullText;

    /// <summary>
    /// 把批注全文截成面板摘要：先按行保留前 4 行，再按 160 字截断，两种情况都补省略号。
    /// 空白行跳过（避免第一行是空行时面板看起来像没有内容）。
    /// </summary>
    private static string BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "（没有文字内容）";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0)
            .ToList();

        var truncated = lines.Count > PreviewMaxLines;
        var preview = string.Join('\n', lines.Take(PreviewMaxLines));

        if (preview.Length > PreviewMaxChars)
        {
            preview = preview[..PreviewMaxChars];
            truncated = true;
        }

        return truncated ? preview.TrimEnd() + "…" : preview;
    }
}
