using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using YuLiangToolkit.Tools.PptViewer.Parsing;

namespace YuLiangToolkit.Tools.PptViewer.Rendering;

/// <summary>
/// 幻灯片文字排版（<see cref="PptSlideRenderer"/> 内部专用）：把 <see cref="PptTextBody"/>
/// 里的段落 / run 拆成可以独立换行的 token，用 <see cref="FormattedText"/> 逐 token 量宽，
/// 再按文本框宽度贪心换行、按段落对齐摆放，最后画到 <see cref="DrawingContext"/> 上。
///
/// <para><b>为什么不用 TextBlock / RichTextBox</b>：渲染结果是矢量 <see cref="DrawingGroup"/>，
/// 要能冻结、能当缩略图复用、不依赖控件树；而且 PowerPoint 的 run 级格式（字体 / 字号 / 颜色 /
/// 上下标）与项目符号 / 缩进 / 段距都得按 OOXML 的语义自己控制。</para>
///
/// <para><b>换行规则</b>：中日韩文字按单字断行，拉丁文按「词 + 空格」断行；<c>\n</c> 是软换行。
/// 换行只发生在 token 之间，不会把一个英文单词劈开。</para>
///
/// <para><b>坐标</b>：传入的 <c>box</c> 是幻灯片坐标（DIP），所有输出都在同一个坐标系里；
/// 文字超出文本框时会被裁剪到文本框内（见 <see cref="DrawBody"/> 的 <c>PushClip</c>）。</para>
/// </summary>
internal static class PptTextLayout
{
    /// <summary>测量文字用的固定 DPI 缩放（按题目约定固定 1.0，屏幕缩放交给外层矢量缩放）。</summary>
    private const double PixelsPerDip = 1.0;

    /// <summary>自动缩小文字时最小缩到 50%（再小用户也看不清了）。</summary>
    private const double MinAutoFitScale = 0.5;

    /// <summary>自动缩小文字时每次乘的系数（放不下就整体缩一号，迭代到最小 50%）。</summary>
    private const double AutoFitStep = 0.9;

    /// <summary>项目符号与正文之间的间距（相对字号的比例）。</summary>
    private const double BulletGapRatio = 0.42;

    /// <summary>run 没有指定颜色时的默认文字色（近黑，和 PowerPoint 默认正文一致）。</summary>
    private static readonly Color DefaultTextColor = Color.FromRgb(0x26, 0x26, 0x26);

    // ---------------------------------------------------------------- 缓存（字体 / 画刷 / 下划线组合）

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, Typeface> TypefaceCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<Color, Brush> BrushCache = new();

    /// <summary>只有下划线。</summary>
    private static readonly TextDecorationCollection? UnderlineDecorations = BuildDecorations(underline: true, strike: false);

    /// <summary>只有删除线。</summary>
    private static readonly TextDecorationCollection? StrikeDecorations = BuildDecorations(underline: false, strike: true);

    /// <summary>下划线 + 删除线。</summary>
    private static readonly TextDecorationCollection? UnderlineStrikeDecorations = BuildDecorations(underline: true, strike: true);

    // ---------------------------------------------------------------- 对外入口

    /// <summary>
    /// 在 <paramref name="box"/> 里排版并绘制一段文字（文字框 / 表格单元格共用）。
    /// </summary>
    /// <param name="context">目标绘制上下文（一般是形状自己的 <see cref="DrawingGroup"/>）。</param>
    /// <param name="body">文字内容；null / 全空白时直接返回。</param>
    /// <param name="box">文字框（幻灯片坐标，DIP）。</param>
    /// <param name="insetLeft">左内边距（DIP）。</param>
    /// <param name="insetTop">上内边距（DIP）。</param>
    /// <param name="insetRight">右内边距（DIP）。</param>
    /// <param name="insetBottom">下内边距（DIP）。</param>
    /// <param name="wrapText">是否自动换行（<c>bodyPr wrap="none"</c> 时 false）。</param>
    /// <param name="autoFit">自动调整策略（只有「缩小文字」会做迭代缩放）。</param>
    /// <param name="anchor">垂直对齐（上 / 中 / 下）。</param>
    public static void DrawBody(
        DrawingContext context,
        PptTextBody? body,
        Rect box,
        double insetLeft,
        double insetTop,
        double insetRight,
        double insetBottom,
        bool wrapText,
        PptAutoFit autoFit,
        PptVerticalAnchor anchor)
    {
        if (body is null || body.Paragraphs is null || !body.HasText) return;
        if (box.Width <= 1 || box.Height <= 1) return;

        // 内边距不能是负数（坏文件里出现过 -1），否则内容区会反向膨胀
        var left = Math.Max(0, insetLeft);
        var top = Math.Max(0, insetTop);
        var right = Math.Max(0, insetRight);
        var bottom = Math.Max(0, insetBottom);

        var content = new Rect(
            box.X + left,
            box.Y + top,
            Math.Max(1, box.Width - left - right),
            Math.Max(1, box.Height - top - bottom));

        // 「缩小文字」：先按 100% 排一次，放不下就整体字号 ×0.9 迭代（最小 50%）。
        // 用整体缩放近似 PowerPoint 的 normAutofit，比逐段缩更好控制，也不会把字号缩到 0。
        var scale = 1.0;
        var layout = BuildLayout(body, content, scale, wrapText);
        if (autoFit == PptAutoFit.ShrinkText)
        {
            var guard = 0;
            while (layout.TotalHeight > content.Height + 0.5 && scale > MinAutoFitScale + 1e-9 && guard++ < 24)
            {
                scale = Math.Max(MinAutoFitScale, scale * AutoFitStep);
                layout = BuildLayout(body, content, scale, wrapText);
            }
        }

        // 文字超出文本框时裁掉，绝不让它盖到别的形状上（PowerPoint 也是裁切的）
        var clip = new RectangleGeometry(box);
        clip.Freeze();
        context.PushClip(clip);
        try
        {
            DrawLayout(context, layout, content, anchor);
        }
        finally
        {
            context.Pop();
        }
    }

    /// <summary>
    /// 在矩形里画一行居中的小字（不支持形状 / 图片缺失占位用），超宽会省略号截断。
    /// </summary>
    public static void DrawCenteredLabel(DrawingContext context, string? text, Rect box, double fontSizeDip, Color color)
    {
        if (string.IsNullOrWhiteSpace(text) || box.Width <= 4 || box.Height <= 4) return;

        try
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                GetTypeface(null, bold: false, italic: false),
                Math.Clamp(fontSizeDip, 8, 40),
                GetBrush(color),
                PixelsPerDip)
            {
                MaxTextWidth = Math.Max(8, box.Width - 12),
                MaxTextHeight = Math.Max(8, box.Height - 8),
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center,
            };

            var y = box.Y + Math.Max(2, (box.Height - formatted.Height) / 2);
            context.DrawText(formatted, new Point(box.X + 6, y));
        }
        catch
        {
            // 字体 / 文字异常时宁可不画占位字，也不能让整页渲染失败
        }
    }

    // ---------------------------------------------------------------- 排版（换行 / 段距 / 项目符号）

    /// <summary>一次排版的结果：按段落顺序排好的行 + 总高度（含段前段后距）。</summary>
    private sealed class Layout
    {
        public List<Line> Lines { get; } = new();
        public double TotalHeight { get; set; }
    }

    /// <summary>排版出来的一行（软换行或贪心换行产生）。</summary>
    private sealed class Line
    {
        public List<Token> Tokens { get; } = new();
        public PptParagraph Paragraph { get; set; } = null!;
        public double Width { get; set; }
        public double Height { get; set; }
        public double Baseline { get; set; }
        public double SpaceBefore { get; set; }
        public double SpaceAfter { get; set; }
        public bool IsLastOfParagraph { get; set; }

        /// <summary>本行可用的正文宽度（内容区宽 - 段落缩进）。</summary>
        public double AvailableWidth { get; set; }

        /// <summary>本行正文起始 X（相对内容区左边界要再加缩进）。</summary>
        public double Indent { get; set; }

        /// <summary>项目符号（FormattedText）；没有项目符号时为 null。</summary>
        public FormattedText? Bullet { get; set; }

        /// <summary>项目符号宽度 / 基线 / 与正文的间距（已经按自动缩放比例算好）。</summary>
        public double BulletWidth { get; set; }
        public double BulletBaseline { get; set; }
        public double BulletGap { get; set; }
    }

    /// <summary>换行的最小单位：一个中日韩字 / 一个拉丁词 / 一个空格 / 一个软换行。</summary>
    private sealed class Token
    {
        public string Text { get; init; } = "";
        public FormattedText? Formatted { get; init; }
        public double Width { get; init; }
        public bool IsSpace { get; init; }
        public bool IsSoftBreak { get; init; }

        /// <summary>上标向上 / 下标向下的基线偏移（DIP，正数向上）。</summary>
        public double BaselineShift { get; init; }
    }

    /// <summary>把整段文字排成行（不绘制），<paramref name="scale"/> 用于自动缩小文字。</summary>
    private static Layout BuildLayout(PptTextBody body, Rect content, double scale, bool wrapText)
    {
        var layout = new Layout();

        // 自动编号的计数器：按大纲级别各一个，下一级出现新编号时更深级别归零
        var counters = new int[9];

        foreach (var paragraph in body.Paragraphs)
        {
            if (paragraph is null) continue;

            var level = Math.Clamp(paragraph.Level, 0, counters.Length - 1);

            // 先算这一段的项目符号（自动编号要按出现顺序递增）
            string? bulletText = null;
            if (paragraph.NumberedBullet)
            {
                counters[level]++;
                for (var deeper = level + 1; deeper < counters.Length; deeper++) counters[deeper] = 0;
                bulletText = $"{counters[level]}.";
            }
            else if (paragraph.Bullet)
            {
                bulletText = string.IsNullOrWhiteSpace(paragraph.BulletChar) ? PptDefaults.BulletChar : paragraph.BulletChar;
            }

            var indent = Math.Max(0, paragraph.IndentDip * scale);
            var available = Math.Max(8, content.Width - indent);
            var paragraphLines = Wrap(Tokenize(paragraph, scale), paragraph, available, wrapText);

            // 空段落也要占一行（PowerPoint 里空行有高度），高度用默认正文字号估算
            if (paragraphLines.Count == 0)
            {
                paragraphLines.Add(new Line
                {
                    Paragraph = paragraph,
                    Height = PptDefaults.BodyFontSizeDip * scale * 1.2,
                    Baseline = PptDefaults.BodyFontSizeDip * scale * 0.95,
                });
            }

            var lineSpacing = paragraph.LineSpacing > 0 ? paragraph.LineSpacing : 1.0;
            var firstRun = paragraph.Runs.FirstOrDefault();

            for (var index = 0; index < paragraphLines.Count; index++)
            {
                var line = paragraphLines[index];
                line.Indent = indent;
                line.AvailableWidth = available;
                line.Height *= lineSpacing;   // 行距倍数只影响行高，基线保持不动
                line.SpaceBefore = index == 0 ? Math.Max(0, (paragraph.SpaceBeforeDip ?? 0) * scale) : 0;
                line.SpaceAfter = index == paragraphLines.Count - 1
                    ? Math.Max(0, (paragraph.SpaceAfterDip ?? 0) * scale)
                    : 0;
                line.IsLastOfParagraph = index == paragraphLines.Count - 1;

                // 项目符号只画在段落第一行，字号取段落第一个 run（没有 run 就用默认字号）
                if (index == 0 && bulletText is not null)
                {
                    var bullet = CreateToken(bulletText, firstRun, scale);
                    line.Bullet = bullet.Formatted;
                    line.BulletWidth = bullet.Width;
                    line.BulletBaseline = bullet.Formatted?.Baseline ?? 0;
                    var bulletFontSize = Math.Max(8, (firstRun?.FontSizeDip ?? PptDefaults.BodyFontSizeDip) * scale);
                    line.BulletGap = bulletFontSize * BulletGapRatio;
                }

                layout.Lines.Add(line);
                layout.TotalHeight += line.SpaceBefore + line.Height + line.SpaceAfter;
            }
        }

        return layout;
    }

    /// <summary>把一段文字排成行：贪心换行 + 行尾去空格 + 行高 / 基线取本行最大值。</summary>
    private static List<Line> Wrap(List<Token> tokens, PptParagraph paragraph, double available, bool wrapText)
    {
        var lines = new List<Line>();
        var current = new Line { Paragraph = paragraph };
        var width = 0.0;

        void FinishLine()
        {
            FinalizeLine(current);
            lines.Add(current);
            current = new Line { Paragraph = paragraph };
            width = 0;
        }

        foreach (var token in tokens)
        {
            if (token.IsSoftBreak)
            {
                FinishLine();
                continue;
            }

            // 放不下就换行（只有「可以换行」的正文才这么做；wrap=none 时按原始软换行）
            if (wrapText && !token.IsSpace && current.Tokens.Count > 0 && width + token.Width > available + 0.01)
            {
                // 行尾空格不算宽度，避免居中 / 右对齐时偏左
                while (current.Tokens.Count > 0 && current.Tokens[^1].IsSpace)
                {
                    width -= current.Tokens[^1].Width;
                    current.Tokens.RemoveAt(current.Tokens.Count - 1);
                }

                FinishLine();

                // 新行开头的空格丢掉（上一行末尾的空白已经由换行代替）
                if (token.IsSpace) continue;
            }

            if (current.Tokens.Count == 0 && token.IsSpace) continue;

            current.Tokens.Add(token);
            width += token.Width;
        }

        // 最后一行：有内容或者整段压根没有行（纯软换行 / 空段落）时也要收尾
        if (current.Tokens.Count > 0 || lines.Count == 0) FinishLine();
        else if (lines.Count > 0) FinalizeLine(lines[^1]);

        return lines;
    }

    /// <summary>算一行宽度 / 行高 / 基线，并去掉行尾空格。</summary>
    private static void FinalizeLine(Line line)
    {
        while (line.Tokens.Count > 0 && line.Tokens[^1].IsSpace) line.Tokens.RemoveAt(line.Tokens.Count - 1);

        var width = 0.0;
        var height = 0.0;
        var baseline = 0.0;
        foreach (var token in line.Tokens)
        {
            width += token.Width;
            if (token.Formatted is null) continue;
            height = Math.Max(height, token.Formatted.Height);
            baseline = Math.Max(baseline, token.Formatted.Baseline);
        }

        line.Width = width;
        line.Height = height > 0 ? height : PptDefaults.BodyFontSizeDip;
        line.Baseline = baseline > 0 ? baseline : line.Height * 0.8;
    }

    // ---------------------------------------------------------------- 绘制排版结果

    /// <summary>把排版结果按垂直对齐放到内容区里逐行绘制。</summary>
    private static void DrawLayout(DrawingContext context, Layout layout, Rect content, PptVerticalAnchor anchor)
    {
        var y = content.Y;
        if (anchor == PptVerticalAnchor.Middle) y += Math.Max(0, (content.Height - layout.TotalHeight) / 2);
        else if (anchor == PptVerticalAnchor.Bottom) y += Math.Max(0, content.Height - layout.TotalHeight);

        foreach (var line in layout.Lines)
        {
            y += line.SpaceBefore;
            var lineTop = y;
            var paragraph = line.Paragraph;
            var textLeft = content.X + line.Indent;

            // 项目符号画在文字左边（右对齐到正文起点前 BulletGap 处）
            if (line.Bullet is not null)
            {
                var bulletX = textLeft - line.BulletGap - line.BulletWidth;
                if (bulletX < content.X) bulletX = content.X;   // 缩进太小时别画到内容区外面
                var bulletY = lineTop + line.Baseline - line.BulletBaseline;
                context.DrawText(line.Bullet, new Point(bulletX, bulletY));
            }

            var alignment = (paragraph.Alignment ?? "l").Trim().ToLowerInvariant();
            var startX = textLeft;
            var extraGap = 0.0;

            if (alignment is "just" or "dist" && !line.IsLastOfParagraph && line.Tokens.Count > 1)
            {
                // 两端对齐：把剩余宽度平均分到 token 之间的空隙（最后一行不拉，和 PowerPoint 一致）
                var extra = line.AvailableWidth - line.Width;
                if (extra > 0.5) extraGap = extra / (line.Tokens.Count - 1);
            }
            else if (alignment == "ctr")
            {
                startX = textLeft + Math.Max(0, (line.AvailableWidth - line.Width) / 2);
            }
            else if (alignment == "r")
            {
                startX = textLeft + Math.Max(0, line.AvailableWidth - line.Width);
            }

            var x = startX;
            foreach (var token in line.Tokens)
            {
                if (token.Formatted is not null)
                {
                    // 用基线对齐：同一行字号不同的 run / 上下标都能对齐在同一条基线上
                    var tokenY = lineTop + line.Baseline - token.Formatted.Baseline - token.BaselineShift;
                    context.DrawText(token.Formatted, new Point(x, tokenY));
                }

                x += token.Width + extraGap;
            }

            y += line.Height + line.SpaceAfter;
        }
    }

    // ---------------------------------------------------------------- 拆分 token / 造 FormattedText

    /// <summary>
    /// 把段落拆成 token：CJK / emoji 按单字，拉丁文按「词 + 空格」，<c>\n</c> 拆成软换行。
    /// 同一个 token 里的字符一定属于同一个 run（字体 / 字号 / 颜色相同），这样量宽才准。
    /// </summary>
    private static List<Token> Tokenize(PptParagraph paragraph, double scale)
    {
        var tokens = new List<Token>();
        if (paragraph.Runs is null) return tokens;

        foreach (var run in paragraph.Runs)
        {
            if (run is null || string.IsNullOrEmpty(run.Text)) continue;
            var text = run.Text;
            var word = new StringBuilder();

            void FlushWord()
            {
                if (word.Length == 0) return;
                tokens.Add(CreateToken(word.ToString(), run, scale));
                word.Clear();
            }

            void AddSpace()
            {
                // 连续空格只留一个（排版里多个空格没有意义，也避免行尾一大段空白）
                if (tokens.Count > 0 && tokens[^1].IsSpace) return;
                tokens.Add(CreateToken(" ", run, scale));
            }

            for (var index = 0; index < text.Length; index++)
            {
                var c = text[index];

                if (c == '\r')
                {
                    if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                    FlushWord();
                    tokens.Add(new Token { Text = "\n", IsSoftBreak = true });
                    continue;
                }

                if (c == '\n')
                {
                    FlushWord();
                    tokens.Add(new Token { Text = "\n", IsSoftBreak = true });
                    continue;
                }

                if (char.IsWhiteSpace(c))
                {
                    FlushWord();
                    AddSpace();
                    continue;
                }

                // emoji 等代理对：整体当一个 token，不要拆成两半
                if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
                {
                    FlushWord();
                    tokens.Add(CreateToken(text.Substring(index, 2), run, scale));
                    index++;
                    continue;
                }

                if (IsCjk(c))
                {
                    FlushWord();
                    tokens.Add(CreateToken(c.ToString(), run, scale));
                    continue;
                }

                word.Append(c);
            }

            FlushWord();
        }

        return tokens;
    }

    /// <summary>按 run 的格式造一个可量宽、可绘制的 token。</summary>
    private static Token CreateToken(string text, PptTextRun? run, double scale)
    {
        var isSuperscript = run?.IsSuperscript == true;
        var isSubscript = run?.IsSubscript == true;
        var baseFontSize = Math.Max(1, (run?.FontSizeDip ?? PptDefaults.BodyFontSizeDip) * scale);
        var fontSize = isSuperscript || isSubscript ? Math.Max(1, baseFontSize * 0.6) : baseFontSize;

        var typeface = GetTypeface(run?.FontFamily, run?.Bold == true, run?.Italic == true);
        var brush = GetBrush(run?.Color ?? DefaultTextColor);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            PixelsPerDip);

        var decorations = GetDecorations(run?.Underline == true, run?.Strike == true);
        if (decorations is not null) formatted.SetTextDecorations(decorations);

        // 上标抬 35%、下标压 18%（相对原始字号）；字号本身已经缩到 60%
        var baselineShift = isSuperscript ? baseFontSize * 0.35 : isSubscript ? -baseFontSize * 0.18 : 0;

        return new Token
        {
            Text = text,
            Formatted = formatted,
            Width = formatted.WidthIncludingTrailingWhitespace,
            IsSpace = string.IsNullOrWhiteSpace(text),
            BaselineShift = baselineShift,
        };
    }

    /// <summary>中日韩文字（含全角标点 / 假名 / 韩文 / 常用 CJK 扩展区）。</summary>
    private static bool IsCjk(char c)
        => c is >= '\u2E80' and <= '\u9FFF'
            or >= '\uAC00' and <= '\uD7AF'
            or >= '\uF900' and <= '\uFAFF'
            or >= '\uFE30' and <= '\uFE4F'
            or >= '\uFF00' and <= '\uFFEF'
            or >= '\u3000' and <= '\u303F';

    /// <summary>取（并缓存）字体；字体名非法 / 系统没装时退回默认字体。</summary>
    private static Typeface GetTypeface(string? fontFamily, bool bold, bool italic)
    {
        var name = string.IsNullOrWhiteSpace(fontFamily) ? PptDefaults.FontFamily : fontFamily.Trim();
        var key = $"{name}|{(bold ? "B" : "N")}|{(italic ? "I" : "N")}";

        lock (CacheLock)
        {
            if (TypefaceCache.TryGetValue(key, out var cached)) return cached;
        }

        var style = italic ? FontStyles.Italic : FontStyles.Normal;
        var weight = bold ? FontWeights.Bold : FontWeights.Normal;

        Typeface typeface;
        try
        {
            typeface = new Typeface(new FontFamily(name), style, weight, FontStretches.Normal);
        }
        catch
        {
            typeface = new Typeface(new FontFamily(PptDefaults.FontFamily), style, weight, FontStretches.Normal);
        }

        lock (CacheLock)
        {
            // 字体种类不会太多；真被坏文件刷爆时清一次即可
            if (TypefaceCache.Count > 256) TypefaceCache.Clear();
            TypefaceCache[key] = typeface;
        }

        return typeface;
    }

    /// <summary>取（并缓存）冻结的纯色画刷。</summary>
    private static Brush GetBrush(Color color)
    {
        lock (CacheLock)
        {
            if (BrushCache.TryGetValue(color, out var cached)) return cached;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();

        lock (CacheLock)
        {
            if (BrushCache.Count > 512) BrushCache.Clear();
            BrushCache[color] = brush;
        }

        return brush;
    }

    /// <summary>按「下划线 / 删除线」开关取共享的装饰集合（都没有时返回 null）。</summary>
    private static TextDecorationCollection? GetDecorations(bool underline, bool strike)
    {
        if (underline && strike) return UnderlineStrikeDecorations;
        if (underline) return UnderlineDecorations;
        return strike ? StrikeDecorations : null;
    }

    /// <summary>构造并冻结一个装饰集合（静态字段初始化用）。</summary>
    private static TextDecorationCollection? BuildDecorations(bool underline, bool strike)
    {
        if (!underline && !strike) return null;

        var collection = new TextDecorationCollection();
        if (underline) collection.Add(TextDecorations.Underline[0]);
        if (strike) collection.Add(TextDecorations.Strikethrough[0]);
        collection.Freeze();
        return collection;
    }
}
