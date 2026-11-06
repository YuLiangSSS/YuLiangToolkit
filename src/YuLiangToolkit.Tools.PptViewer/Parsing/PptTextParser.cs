using System.Windows.Media;
using System.Xml.Linq;
using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

/// <summary>
/// 一套「文字默认值」（字号 / 字体 / 颜色 / 粗斜体 / 项目符号 / 缩进 / 段距）。
/// <para>解析时按「内置默认 → 母版 txStyles → 文本框 lstStyle → 段落 pPr → run rPr」逐层合并：
/// 每一层只覆盖自己写了的字段，所以下层的 null 表示「沿用上层」。</para>
/// </summary>
internal sealed class PptLevelStyle
{
    /// <summary>对齐："l" / "ctr" / "r" / "just" / "dist"。</summary>
    public string? Alignment { get; set; }

    /// <summary>左缩进（DIP）。</summary>
    public double? IndentDip { get; set; }

    /// <summary>是否显示项目符号。</summary>
    public bool? Bullet { get; set; }

    /// <summary>项目符号字符。</summary>
    public string? BulletChar { get; set; }

    /// <summary>是否自动编号。</summary>
    public bool? NumberedBullet { get; set; }

    /// <summary>段前距（DIP）。</summary>
    public double? SpaceBeforeDip { get; set; }

    /// <summary>段后距（DIP）。</summary>
    public double? SpaceAfterDip { get; set; }

    /// <summary>行距倍数（1.0 = 单倍）。</summary>
    public double? LineSpacing { get; set; }

    /// <summary>字号（DIP）。</summary>
    public double? FontSizeDip { get; set; }

    /// <summary>粗体。</summary>
    public bool? Bold { get; set; }

    /// <summary>斜体。</summary>
    public bool? Italic { get; set; }

    /// <summary>下划线。</summary>
    public bool? Underline { get; set; }

    /// <summary>删除线。</summary>
    public bool? Strike { get; set; }

    /// <summary>上标。</summary>
    public bool? Superscript { get; set; }

    /// <summary>下标。</summary>
    public bool? Subscript { get; set; }

    /// <summary>文字颜色。</summary>
    public Color? Color { get; set; }

    /// <summary>拉丁字体（已解析主题字体引用）。</summary>
    public string? LatinFont { get; set; }

    /// <summary>中日韩字体（已解析主题字体引用）。</summary>
    public string? EastAsianFont { get; set; }

    /// <summary>把另一层里「写了的」字段覆盖到当前层（null / 空串不覆盖）。</summary>
    public void MergeFrom(PptLevelStyle? other)
    {
        if (other is null) return;

        Alignment = other.Alignment ?? Alignment;
        IndentDip = other.IndentDip ?? IndentDip;
        Bullet = other.Bullet ?? Bullet;
        BulletChar = nonEmpty(other.BulletChar) ?? BulletChar;
        NumberedBullet = other.NumberedBullet ?? NumberedBullet;
        SpaceBeforeDip = other.SpaceBeforeDip ?? SpaceBeforeDip;
        SpaceAfterDip = other.SpaceAfterDip ?? SpaceAfterDip;
        LineSpacing = other.LineSpacing ?? LineSpacing;
        FontSizeDip = other.FontSizeDip ?? FontSizeDip;
        Bold = other.Bold ?? Bold;
        Italic = other.Italic ?? Italic;
        Underline = other.Underline ?? Underline;
        Strike = other.Strike ?? Strike;
        Superscript = other.Superscript ?? Superscript;
        Subscript = other.Subscript ?? Subscript;
        Color = other.Color ?? Color;
        LatinFont = nonEmpty(other.LatinFont) ?? LatinFont;
        EastAsianFont = nonEmpty(other.EastAsianFont) ?? EastAsianFont;
    }

    private static string? nonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>母版 <c>p:txStyles</c> 的三套大纲样式（titleStyle / bodyStyle / otherStyle），每套 9 级。</summary>
internal sealed class PptMasterStyles
{
    /// <summary>标题样式（1 ~ 9 级，下标 0 起；缺级为 null）。</summary>
    public PptLevelStyle?[] Title { get; } = new PptLevelStyle?[9];

    /// <summary>正文样式（1 ~ 9 级）。</summary>
    public PptLevelStyle?[] Body { get; } = new PptLevelStyle?[9];

    /// <summary>其它文字样式（1 ~ 9 级）。</summary>
    public PptLevelStyle?[] Other { get; } = new PptLevelStyle?[9];

    /// <summary>按占位符类别（title / body / other）取对应的一套样式。</summary>
    public PptLevelStyle?[] For(string category) => category switch
    {
        "title" => Title,
        "body" => Body,
        _ => Other,
    };
}

/// <summary>
/// 解析一段文字所需的环境：主题 / 颜色上下文 / 当前部件的关系表（超链接）+ 这一层的默认样式。
/// 每个形状（或表格单元格 / 备注）构造一次，传给 <see cref="PptTextParser.ParseTextBody"/>。
/// </summary>
internal sealed class PptTextStyleContext
{
    /// <summary>主题数据（字体引用要用）。</summary>
    public required PptThemeData ThemeData { get; init; }

    /// <summary>颜色上下文（主题色 / clrMap）。</summary>
    public required PptColorContext Colors { get; init; }

    /// <summary>当前部件的关系表（run 超链接 r:id 要查）。</summary>
    public required IReadOnlyDictionary<string, OoxmlRelationship> Relationships { get; init; }

    /// <summary>母版对应类别的大纲样式（title / body / other 之一）。</summary>
    public required PptLevelStyle?[] LevelStyles { get; init; }

    /// <summary>这一类文字的默认拉丁字体。</summary>
    public required string BaseLatinFont { get; init; }

    /// <summary>这一类文字的默认中日韩字体。</summary>
    public required string BaseEastAsianFont { get; init; }

    /// <summary>这一类文字的默认字号（DIP）。</summary>
    public required double BaseFontSizeDip { get; init; }

    /// <summary>这一类文字的默认是否带项目符号。</summary>
    public required bool BaseBullet { get; init; }

    /// <summary>这一类文字的默认项目符号字符。</summary>
    public required string BaseBulletChar { get; init; }
}

/// <summary>
/// 文字解析：<c>a:txBody</c> → <see cref="PptTextBody"/>（段落 + run）。
/// <para>所有「继承」都在这里算完：母版 txStyles 各级默认 → 文本框 lstStyle → 段落 pPr →
/// run rPr，最后模型里每一段 / 每个 run 都是最终值，渲染器不用再查主题。</para>
/// </summary>
internal static class PptTextParser
{
    /// <summary>没有解析到颜色时的默认文字色（近黑，和 PptModel 的默认一致）。</summary>
    public static readonly Color DefaultTextColor = Color.FromRgb(0x26, 0x26, 0x26);

    private static readonly PptLevelStyle?[] EmptyLevels = new PptLevelStyle?[9];

    /// <summary>按占位符类型判断文字默认样式类别：标题 / 正文 / 其它。</summary>
    public static string CategoryOfPlaceholder(string? placeholderType)
    {
        return placeholderType?.Trim().ToLowerInvariant() switch
        {
            "title" or "ctrtitle" => "title",
            // 正文占位符经常省略 type（只写 idx），按 body 处理才能拿到项目符号 / 正文默认字号
            "body" or "obj" or "" => "body",
            _ => "other",
        };
    }

    /// <summary>
    /// 构造一个文字解析上下文。<paramref name="masterStyles"/> 为 null（母版缺失）时用内置默认值。
    /// </summary>
    public static PptTextStyleContext CreateContext(
        PptThemeData themeData,
        PptColorContext colors,
        IReadOnlyDictionary<string, OoxmlRelationship> relationships,
        PptMasterStyles? masterStyles,
        string category)
    {
        var isTitle = category == "title";
        var isBody = category == "body";

        // 标题用主题 majorFont，正文 / 其它用 minorFont；主题缺字体时用默认中文字体
        var latin = isTitle ? themeData.Theme.MajorFont : themeData.Theme.MinorFont;
        var eastAsian = isTitle ? themeData.Theme.MajorEastAsianFont : themeData.Theme.MinorEastAsianFont;
        if (string.IsNullOrWhiteSpace(latin)) latin = PptDefaults.FontFamily;
        if (string.IsNullOrWhiteSpace(eastAsian)) eastAsian = PptDefaults.FontFamily;

        return new PptTextStyleContext
        {
            ThemeData = themeData,
            Colors = colors,
            Relationships = relationships,
            LevelStyles = masterStyles?.For(category) ?? EmptyLevels,
            BaseLatinFont = latin,
            BaseEastAsianFont = eastAsian,
            // 标题默认 44 磅、正文 / 其它默认 18 磅（PptDefaults 里是 DIP 常量）
            BaseFontSizeDip = isTitle ? PptDefaults.TitleFontSizeDip : PptDefaults.BodyFontSizeDip,
            BaseBullet = isBody,
            BaseBulletChar = PptDefaults.BulletChar,
        };
    }

    /// <summary>解析母版 <c>p:txStyles</c> 的三套大纲样式（titleStyle / bodyStyle / otherStyle 是 PresentationML 元素，级别是 DrawingML）。</summary>
    public static PptMasterStyles ParseMasterStyles(XElement? txStyles, PptColorContext colors)
    {
        var styles = new PptMasterStyles();
        if (txStyles is null) return styles;

        ParseLevelStyleContainer(txStyles.Element(PptXml.P + "titleStyle"), styles.Title, colors);
        ParseLevelStyleContainer(txStyles.Element(PptXml.P + "bodyStyle"), styles.Body, colors);
        ParseLevelStyleContainer(txStyles.Element(PptXml.P + "otherStyle"), styles.Other, colors);
        return styles;
    }

    /// <summary>解析 <c>p:txBody/a:lstStyle</c>（文本框级别的默认样式）为「级别 → 样式」字典。</summary>
    public static Dictionary<int, PptLevelStyle> ParseLevelStyles(XElement? container, PptColorContext colors)
    {
        var levels = new Dictionary<int, PptLevelStyle>();
        if (container is null) return levels;

        foreach (var child in container.Elements())
        {
            var level = ParseLevelNumber(child.Name.LocalName);
            if (level < 0) continue;
            levels[level] = ParseLevelStyle(child, colors);
        }

        return levels;
    }

    /// <summary>
    /// 解析一个 <c>a:lvlNpPr</c> / <c>a:pPr</c> / <c>a:rPr</c> / <c>a:defRPr</c>。
    /// 同一个方法兼容段落属性和 run 属性 —— 不认识的子元素忽略，所以不需要按元素类型分支。
    /// </summary>
    public static PptLevelStyle ParseLevelStyle(XElement? properties, PptColorContext colors)
    {
        var style = new PptLevelStyle();
        if (properties is null) return style;

        // ---- 段落属性 ----
        var alignment = NormalizeAlignment(PptXml.Attr(properties, "algn"));
        if (alignment is not null) style.Alignment = alignment;

        var marLeft = PptXml.LongAttr(properties, "marL");
        if (marLeft.HasValue) style.IndentDip = Math.Max(0, OoxmlPackage.EmuToDip(marLeft.Value));

        style.SpaceBeforeDip = ReadSpacing(properties.Element(PptXml.A + "spcBef"));
        style.SpaceAfterDip = ReadSpacing(properties.Element(PptXml.A + "spcAft"));

        var lineSpacing = ReadLineSpacing(properties.Element(PptXml.A + "lnSpc"));
        if (lineSpacing.HasValue) style.LineSpacing = lineSpacing.Value;

        // ---- 项目符号 ----
        if (properties.Element(PptXml.A + "buNone") is not null)
        {
            style.Bullet = false;
            style.NumberedBullet = false;
        }
        else if (properties.Element(PptXml.A + "buAutoNum") is not null)
        {
            style.Bullet = true;
            style.NumberedBullet = true;
        }
        else if (properties.Element(PptXml.A + "buChar") is { } bulletChar)
        {
            style.Bullet = true;
            var character = PptXml.Attr(bulletChar, "char");
            style.BulletChar = character.Length > 0 ? character : PptDefaults.BulletChar;
        }
        else if (properties.Element(PptXml.A + "buBlip") is not null)
        {
            style.Bullet = true;
            style.BulletChar = PptDefaults.BulletChar;
        }

        // ---- run 属性 ----
        var size = PptXml.IntAttr(properties, "sz");
        if (size is > 0) style.FontSizeDip = OoxmlPackage.PointToDip(size.Value / 100.0);

        var bold = PptXml.BoolAttr(properties, "b");
        if (bold.HasValue) style.Bold = bold.Value;

        var italic = PptXml.BoolAttr(properties, "i");
        if (italic.HasValue) style.Italic = italic.Value;

        var underline = PptXml.Attr(properties, "u");
        if (underline.Length > 0)
            style.Underline = !string.Equals(underline, "none", StringComparison.OrdinalIgnoreCase);

        var strike = PptXml.Attr(properties, "strike");
        if (strike.Length > 0)
            style.Strike = !string.Equals(strike, "noStrike", StringComparison.OrdinalIgnoreCase);

        var baseline = PptXml.IntAttr(properties, "baseline");
        if (baseline.HasValue)
        {
            style.Superscript = baseline.Value > 0;
            style.Subscript = baseline.Value < 0;
        }

        var solidFill = properties.Element(PptXml.A + "solidFill");
        if (solidFill is not null)
        {
            var colorElement = solidFill.Elements().FirstOrDefault();
            if (colorElement is not null) style.Color = colors.Resolve(colorElement, DefaultTextColor);
        }

        var (latinFont, eastAsianFont) = PptThemeParser.ReadFonts(properties, colors.ThemeData);
        if (latinFont.Length > 0) style.LatinFont = latinFont;
        if (eastAsianFont.Length > 0) style.EastAsianFont = eastAsianFont;

        // 段落 / 级别的默认 run 属性：继续合并进来（字体 / 字号 / 颜色 / 粗斜体）
        if (properties.Element(PptXml.A + "defRPr") is { } defaultRunProperties)
            style.MergeFrom(ParseLevelStyle(defaultRunProperties, colors));

        return style;
    }

    /// <summary>解析 <c>a:txBody</c> 为模型文本块；<paramref name="txBody"/> 为 null 时返回空文本块。</summary>
    public static PptTextBody ParseTextBody(XElement? txBody, PptTextStyleContext context)
    {
        if (txBody is null) return new PptTextBody();

        var listStyleLevels = ParseLevelStyles(txBody.Element(PptXml.A + "lstStyle"), context.Colors);
        var paragraphs = new List<PptParagraph>();

        foreach (var paragraphElement in txBody.Elements(PptXml.A + "p"))
        {
            try
            {
                paragraphs.Add(ParseParagraph(paragraphElement, context, listStyleLevels));
            }
            catch
            {
                // 单个坏段落跳过：不影响同一文本框里的其它内容
            }
        }

        return new PptTextBody { Paragraphs = paragraphs };
    }

    /// <summary>从段落 / 文字块里收集「有可见文字」的纯文本（用于标题 / PlainText / 备注）。</summary>
    public static string PlainTextOf(PptTextBody? body)
        => body is null ? "" : body.Text.Trim('\r', '\n');

    // ---------------------------------------------------------------- 内部实现

    private static PptParagraph ParseParagraph(
        XElement paragraphElement,
        PptTextStyleContext context,
        IReadOnlyDictionary<int, PptLevelStyle> listStyleLevels)
    {
        var paragraphProperties = paragraphElement.Element(PptXml.A + "pPr");
        var level = Math.Clamp(PptXml.IntAttr(paragraphProperties, "lvl") ?? 0, 0, 8);

        // 合并顺序：内置默认（上下文）→ 母版级别样式 → 文本框 lstStyle → 段落 pPr
        var style = new PptLevelStyle();
        style.MergeFrom(context.LevelStyles.ElementAtOrDefault(level));
        if (listStyleLevels.TryGetValue(level, out var listStyle)) style.MergeFrom(listStyle);
        style.MergeFrom(ParseLevelStyle(paragraphProperties, context.Colors));

        var paragraph = new PptParagraph
        {
            Level = level,
            Alignment = style.Alignment ?? "l",
            IndentDip = style.IndentDip ?? level * PptDefaults.LevelIndentDip,
            SpaceBeforeDip = style.SpaceBeforeDip,
            SpaceAfterDip = style.SpaceAfterDip,
            LineSpacing = style.LineSpacing is > 0 ? style.LineSpacing.Value : 1.0,
        };

        // 项目符号：显式写法 > 级别默认 > 类别默认（标题无符号、正文圆点、其它无符号）
        var bullet = style.Bullet ?? context.BaseBullet;
        var numbered = style.NumberedBullet ?? false;
        paragraph.Bullet = bullet || numbered;
        paragraph.NumberedBullet = numbered;
        paragraph.BulletChar = !string.IsNullOrWhiteSpace(style.BulletChar)
            ? style.BulletChar!
            : context.BaseBulletChar;

        var runs = new List<PptTextRun>();
        foreach (var child in paragraphElement.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "r":
                {
                    var text = child.Element(PptXml.A + "t")?.Value ?? "";
                    if (text.Length == 0) break;
                    runs.Add(ParseRun(text, child.Element(PptXml.A + "rPr"), style, context));
                    break;
                }

                case "br":
                    // 软换行：模型里用一个 "\n" run 表示（保留当前字体 / 字号）
                    runs.Add(ParseRun("\n", child.Element(PptXml.A + "rPr"), style, context));
                    break;

                case "fld":
                {
                    // 域：取显示结果（a:t），域代码本身不显示
                    var text = child.Element(PptXml.A + "t")?.Value ?? "";
                    if (text.Length == 0) break;
                    runs.Add(ParseRun(text, child.Element(PptXml.A + "rPr"), style, context));
                    break;
                }
            }
        }

        paragraph.Runs = runs;
        return paragraph;
    }

    private static PptTextRun ParseRun(
        string text,
        XElement? runProperties,
        PptLevelStyle paragraphStyle,
        PptTextStyleContext context)
    {
        var style = new PptLevelStyle();
        style.MergeFrom(paragraphStyle);
        style.MergeFrom(ParseLevelStyle(runProperties, context.Colors));

        var fontSizeDip = style.FontSizeDip is > 0 ? style.FontSizeDip.Value : context.BaseFontSizeDip;
        var latinFont = style.LatinFont ?? context.BaseLatinFont;
        var eastAsianFont = style.EastAsianFont ?? context.BaseEastAsianFont;

        return new PptTextRun
        {
            Text = text,
            FontFamily = ChooseFont(text, latinFont, eastAsianFont),
            FontSizeDip = fontSizeDip,
            Bold = style.Bold ?? false,
            Italic = style.Italic ?? false,
            Underline = style.Underline ?? false,
            Strike = style.Strike ?? false,
            Color = style.Color ?? DefaultTextColor,
            IsSuperscript = style.Superscript ?? false,
            IsSubscript = style.Subscript ?? false,
            Hyperlink = ResolveHyperlink(runProperties, context.Relationships),
        };
    }

    /// <summary>文本里有中日韩字符时优先用东亚字体，否则用拉丁字体；都没有就用默认字体。</summary>
    private static string ChooseFont(string text, string latinFont, string eastAsianFont)
    {
        if (ContainsCjk(text) && !string.IsNullOrWhiteSpace(eastAsianFont)) return eastAsianFont;
        if (!string.IsNullOrWhiteSpace(latinFont)) return latinFont;
        if (!string.IsNullOrWhiteSpace(eastAsianFont)) return eastAsianFont;
        return PptDefaults.FontFamily;
    }

    private static bool ContainsCjk(string text)
    {
        foreach (var character in text)
        {
            if (character >= '\u2E80') return true; // CJK 部首起：覆盖中日韩统一表意文字 / 假名 / 谚文
        }

        return false;
    }

    /// <summary>解析 <c>a:hlinkClick</c>（外链关系或 action 里的 URL）；内部跳转返回 null。</summary>
    public static string? ResolveHyperlink(
        XElement? properties,
        IReadOnlyDictionary<string, OoxmlRelationship> relationships)
        => ResolveHyperlinkElement(properties?.Element(PptXml.A + "hlinkClick"), relationships);

    /// <summary>解析一个 <c>a:hlinkClick</c> 元素（外链关系或 action 里的 URL）；内部跳转返回 null。</summary>
    public static string? ResolveHyperlinkElement(
        XElement? click,
        IReadOnlyDictionary<string, OoxmlRelationship> relationships)
    {
        if (click is null) return null;

        var relationshipId = PptXml.Attr(click, PptXml.R, "id");
        if (relationshipId.Length > 0
            && relationships.TryGetValue(relationshipId, out var relationship)
            && relationship.IsExternal
            && !string.IsNullOrWhiteSpace(relationship.Target))
        {
            return relationship.Target;
        }

        // 有些超链接只写 action（例如邮件 / 网页），能认成 URL 才用；幻灯片内部跳转不了，忽略
        var action = PptXml.Attr(click, "action");
        if (action.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return action;
        }

        return null;
    }

    private static void ParseLevelStyleContainer(
        XElement? container,
        PptLevelStyle?[] target,
        PptColorContext colors)
    {
        if (container is null) return;

        foreach (var child in container.Elements())
        {
            var level = ParseLevelNumber(child.Name.LocalName);
            if (level < 0) continue;
            target[level] = ParseLevelStyle(child, colors);
        }
    }

    /// <summary>从 <c>lvl1pPr</c> ~ <c>lvl9pPr</c> 解析出 0 ~ 8 的级别下标；不是这种元素返回 -1。</summary>
    private static int ParseLevelNumber(string localName)
    {
        // "lvl1pPr" 共 7 个字符；"lvl10pPr" 之类不存在，按 7 ~ 8 位容错
        if (localName.Length < 7) return -1;
        if (!localName.StartsWith("lvl", StringComparison.Ordinal)
            || !localName.EndsWith("pPr", StringComparison.Ordinal))
        {
            return -1;
        }

        var digits = localName[3..^3];
        return int.TryParse(digits, out var level) && level is >= 1 and <= 9 ? level - 1 : -1;
    }

    /// <summary>读段前 / 段后距：<c>a:spcPts val</c> 是 1/100 磅；<c>a:spcPct</c>（百分比）拿不到字号，忽略。</summary>
    private static double? ReadSpacing(XElement? spacing)
    {
        if (spacing is null) return null;

        var points = PptXml.DoubleAttr(spacing.Element(PptXml.A + "spcPts"), "val");
        if (points.HasValue) return OoxmlPackage.PointToDip(points.Value / 100.0);
        return null;
    }

    /// <summary>读行距：只认 <c>a:spcPct</c>（百分比换算成倍数）；固定磅值行距忽略（按单倍处理）。</summary>
    private static double? ReadLineSpacing(XElement? lineSpacing)
    {
        if (lineSpacing is null) return null;

        var percent = PptXml.DoubleAttr(lineSpacing.Element(PptXml.A + "spcPct"), "val");
        if (percent is > 0) return percent.Value / 100000.0;
        return null;
    }

    private static string? NormalizeAlignment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant() switch
        {
            "l" or "ctr" or "r" or "just" or "dist" => value.Trim().ToLowerInvariant(),
            "left" => "l",
            "center" => "ctr",
            "right" => "r",
            "justify" => "just",
            _ => null,
        };
    }
}
