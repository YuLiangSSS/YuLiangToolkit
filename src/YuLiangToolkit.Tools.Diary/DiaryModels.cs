using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Mvvm;

namespace YuLiangToolkit.Tools.Diary;

/// <summary>一篇日记的写法：富文本（HTML）或普通文本。</summary>
public enum DiaryFormat
{
    /// <summary>富文本——编辑器里的加粗 / 颜色 / 标题 / 列表 / 图片等都以 HTML 存。</summary>
    RichText,

    /// <summary>普通文本——只存纯文字，不解析任何标签。</summary>
    PlainText,
}

/// <summary>
/// 一篇日记。<see cref="Content"/> 的含义随 <see cref="Format"/> 变化：
/// 富文本时是 HTML 源码，普通文本时是纯文字（换行照原样）。
///
/// <para>展示用的 <see cref="DisplayTitle"/> / <see cref="Snippet"/> / <see cref="WordCount"/> 等都是
/// 只读计算属性（不写进 JSON，靠 <see cref="JsonIgnoreAttribute"/> 与 JsonFileStore 的只读属性规则兜底）。</para>
/// </summary>
public sealed class DiaryEntry : ObservableObject
{
    private string _title = "";
    private string _mood = "";
    private DiaryFormat _format = DiaryFormat.RichText;

    /// <summary>稳定 Id，同时是数据文件名（<c>entries\{Id}.json</c>）。</summary>
    public string Id { get; set; } = "";

    /// <summary>标题（可空；列表里空标题显示成「无标题」，正文照样能写）。</summary>
    public string Title
    {
        get => _title;
        set
        {
            var clean = value ?? "";
            if (!Set(ref _title, clean)) return;
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    /// <summary>创建时间（列表默认按它倒序，写日记时保持稳定、不会一编辑就跳位置）。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>最后修改时间（保存时更新，详情里显示）。</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>写法：富文本 / 普通文本。</summary>
    public DiaryFormat Format
    {
        get => _format;
        set
        {
            if (Set(ref _format, value))
            {
                OnPropertyChanged(nameof(IsRichText));
                OnPropertyChanged(nameof(FormatText));
            }
        }
    }

    /// <summary>正文（富文本 = HTML，普通文本 = 纯文字）。</summary>
    public string Content { get; set; } = "";

    /// <summary>心情 / 天气之类的一个 emoji（可空，纯装饰）。</summary>
    public string Mood
    {
        get => _mood;
        set
        {
            if (Set(ref _mood, value ?? "")) OnPropertyChanged(nameof(HasMood));
        }
    }

    // ---------------- 展示用只读计算属性（不落盘） ----------------

    /// <summary>列表标题：空标题显示成「（无标题）」，方便点击。</summary>
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "（无标题）" : Title.Trim();

    /// <summary>是不是富文本。</summary>
    [JsonIgnore]
    public bool IsRichText => Format == DiaryFormat.RichText;

    /// <summary>写法文字。</summary>
    [JsonIgnore]
    public string FormatText => Format == DiaryFormat.RichText ? "富文本" : "普通文本";

    /// <summary>有没有设置心情 emoji。</summary>
    [JsonIgnore]
    public bool HasMood => !string.IsNullOrEmpty(Mood);

    /// <summary>正文摘要（纯文字，压成一行，最多 60 字）。</summary>
    [JsonIgnore]
    public string Snippet => DiaryText.Snippet(Content, Format, 60);

    /// <summary>正文是否为空（空日记在列表里显示「（空白日记）」）。</summary>
    [JsonIgnore]
    public bool IsEmptyContent => DiaryText.IsEmpty(Content, Format);

    /// <summary>字数（按非空白字符数，中文一个算一个）。</summary>
    [JsonIgnore]
    public int WordCount => DiaryText.CountCharacters(DiaryText.ToPlainText(Content, Format));

    /// <summary>「N 字」。</summary>
    [JsonIgnore]
    public string WordCountText => $"{WordCount} 字";

    /// <summary>列表左侧的大号「日」。</summary>
    [JsonIgnore]
    public string DateDay => CreatedAt.ToString("dd");

    /// <summary>列表左侧的「月」。</summary>
    [JsonIgnore]
    public string DateMonth => CreatedAt.ToString("MM") + "月";

    /// <summary>列表里的时间（HH:mm）。</summary>
    [JsonIgnore]
    public string TimeText => CreatedAt.ToString("HH:mm");

    /// <summary>完整日期（编辑区顶部显示，例如 2026年11月18日 周三 17:40）。</summary>
    [JsonIgnore]
    public string FullDateText => $"{CreatedAt:yyyy年M月d日} {WeekdayText(CreatedAt)} {CreatedAt:HH:mm}";

    /// <summary>最后修改时间文字。</summary>
    [JsonIgnore]
    public string UpdatedText => $"最后修改：{UpdatedAt:yyyy-MM-dd HH:mm}";

    private static string WeekdayText(DateTime time) => time.DayOfWeek switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    /// <summary>把手改 JSON 里残留的空值收拾干净，避免界面崩。</summary>
    public void Normalize()
    {
        Title ??= "";
        Mood ??= "";
        Content ??= "";
    }
}

/// <summary>
/// 日记正文的纯文本工具（不依赖 WPF）：字数统计 / 摘要 / 富文本→纯文本 / 纯文本→富文本。
///
/// <para>富文本→纯文本只用于「统计字数 / 列表摘要 / 切换成普通文本」这类展示场景，
/// 不做完整的 HTML 解析；真正编辑用的富文本 ⇄ FlowDocument 转换在 <c>Views/RichTextHtml.cs</c>。</para>
/// </summary>
public static class DiaryText
{
    private static readonly Regex ScriptStylePattern = new(
        @"<(script|style|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CommentPattern = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BlockBreakPattern = new(
        @"</?(p|div|br|tr|h[1-6]|li|blockquote|table|ul|ol|section|article|header|footer)\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagPattern = new(@"<[^>]+>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BlankLinesPattern = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex ManyNewlinesPattern = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex MultiWhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>按写法把正文转成纯文本：富文本去掉标签 / 解码实体，普通文本原样。</summary>
    public static string ToPlainText(string? content, DiaryFormat format)
    {
        if (string.IsNullOrEmpty(content)) return "";
        if (format == DiaryFormat.PlainText) return content;
        return HtmlToPlainText(content);
    }

    /// <summary>HTML → 纯文本（去掉脚本 / 样式 / 注释，块级标签转换行，解码实体，压缩空行）。</summary>
    public static string HtmlToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = CommentPattern.Replace(html, "");
        text = ScriptStylePattern.Replace(text, "");
        text = BlockBreakPattern.Replace(text, "\n");
        text = TagPattern.Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = BlankLinesPattern.Replace(text, "\n");
        text = ManyNewlinesPattern.Replace(text, "\n\n");
        return text.Trim();
    }

    /// <summary>普通文本 → 富文本 HTML（转义特殊字符，换行变 &lt;br&gt;，包一层 &lt;p&gt;）。</summary>
    public static string FromPlainText(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var builder = new StringBuilder(plain.Length + 32);
        builder.Append("<p>");
        foreach (var ch in plain.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\n': builder.Append("<br>"); break;
                default: builder.Append(ch); break;
            }
        }
        builder.Append("</p>");
        return builder.ToString();
    }

    /// <summary>正文是不是空的：去掉标签与空白后没有任何文字、也没有图片。</summary>
    public static bool IsEmpty(string? content, DiaryFormat format)
    {
        if (string.IsNullOrEmpty(content)) return true;
        if (format == DiaryFormat.RichText && Regex.IsMatch(content, "<img\\b", RegexOptions.IgnoreCase)) return false;
        var text = ToPlainText(content, format);
        return text.Trim().Length == 0;
    }

    /// <summary>按非空白字符统计字数（中文一字算一个，英文单词里的字母也逐个算，跟写字楼一致）。</summary>
    public static int CountCharacters(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 0;
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch) && !char.IsControl(ch)) count++;
        }
        return count;
    }

    /// <summary>生成列表摘要：压成一行、最多 <paramref name="maxLength"/> 字，空日记给一句占位文字。</summary>
    public static string Snippet(string? content, DiaryFormat format, int maxLength = 60)
    {
        var plain = ToPlainText(content, format);
        if (string.IsNullOrWhiteSpace(plain)) return "（空白日记）";
        var collapsed = MultiWhitespacePattern.Replace(plain, " ").Trim();
        if (collapsed.Length <= maxLength) return collapsed;
        return collapsed[..maxLength] + "…";
    }
}
