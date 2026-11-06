using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Tools.PptViewer;
using YuLiangToolkit.Tools.PptViewer.Parsing;
using YuLiangToolkit.Tools.PptViewer.Rendering;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「PPT 阅读器」自检：手写 pptx 解析（幻灯片顺序 / 版式继承 / 文字格式 / 图片 / 表格 / 组合 /
/// 备注 / 主题色 / 文档属性）、纯逻辑查找、配置与最近列表 / 阅读位置、坏文件中文异常、渲染器契约。
///
/// <para><b>不弹窗、不碰真实 Data/Config</b>：在系统临时目录里用 <see cref="ZipArchive"/> 现造一个最小 pptx
/// （含 slideMaster / slideLayout / theme / 两张幻灯片 / notesSlide / 一张 1×1 PNG），
/// 跑完 <c>finally</c> 删掉；配置对象直接 <c>new</c>，不经过 <c>ToolConfigStore</c>。</para>
///
/// <para><b>测试文稿的刻意安排</b>：<c>p:sldIdLst</c> 先 <c>rId2</c> 后 <c>rId1</c>，所以演示顺序第 1 页
/// 对应文件里的 <c>slide2.xml</c>（标题「第二页标题」、内容最丰富）；演示顺序第 2 页对应 <c>slide1.xml</c>
/// （标题「第一页标题」、背景纯色 + 备注）。断言全部按「演示顺序」找页，不按文件名。</para>
/// </summary>
internal static class PptViewerChecks
{
    /// <summary>1×1 透明 PNG（验证「关系 → media 字节 → 模型」链路；和 Word 阅读器自检用同一个）。</summary>
    private static readonly byte[] s_pixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        var root = Path.Combine(Path.GetTempPath(), "ylt-ppt-viewer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            CheckMetadata(check, report);

            var pptxPath = Path.Combine(root, "自检演示文稿.pptx");
            CreateMinimalPptx(pptxPath);

            var presentation = PptDocumentParser.Parse(pptxPath);

            CheckPresentation(check, presentation, pptxPath);
            CheckPlaceholderInheritance(check, presentation);
            CheckTextFormatting(check, presentation);
            CheckPicture(check, presentation);
            CheckTable(check, presentation);
            CheckGroupAndTree(check, presentation);
            CheckNotesAndPlainText(check, presentation);
            CheckSearch(check, presentation);
            CheckConfig(check, root);
            CheckErrors(check, root);
            CheckRenderer(check, presentation);
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
        var tool = new PptViewerTool();
        check("[PPT阅读器] Id / 名称 / 图标 / 分类 / 排序符合约定",
            tool.Id == "ppt-viewer" && tool.Name == "PPT 阅读器" && tool.EmojiIcon == "📽️"
            && tool.Category == "文档" && tool.Order == 20,
            $"{tool.Id} / {tool.Name} / {tool.EmojiIcon} / {tool.Category} / {tool.Order}");

        // 这条没有细节要展示，用 report 版（顺带验证另一个重载）
        report("[PPT阅读器] 描述明确写了「只读」", tool.Description.Contains("只读", StringComparison.Ordinal));

        check("[PPT阅读器] 标签覆盖 PPT / pptx / 只读 / 放映 / 缩略图",
            tool.Tags.Contains("PPT") && tool.Tags.Contains("pptx") && tool.Tags.Contains("只读")
            && tool.Tags.Contains("放映") && tool.Tags.Contains("缩略图"),
            string.Join(" / ", tool.Tags));

        check("[PPT阅读器] 配置 ToolId 与工具 Id 一致，扩展名认领 .pptx / .pptm / .ppsx",
            PptViewerConfig.ToolId == tool.Id
            && PptViewerConfig.SupportedExtensions.Contains(".pptx")
            && PptViewerConfig.SupportedExtensions.Contains(".pptm")
            && PptViewerConfig.SupportedExtensions.Contains(".ppsx"),
            $"{PptViewerConfig.ToolId} / {string.Join(", ", PptViewerConfig.SupportedExtensions)}");

        check("[PPT阅读器] 文件对话框过滤串包含三种演示文稿扩展名",
            PptViewerConfig.FileDialogFilter.Contains("*.pptx", StringComparison.Ordinal)
            && PptViewerConfig.FileDialogFilter.Contains("*.pptm", StringComparison.Ordinal)
            && PptViewerConfig.FileDialogFilter.Contains("*.ppsx", StringComparison.Ordinal),
            PptViewerConfig.FileDialogFilter);
    }

    // ---------------------------------------------------------------- 尺寸 / 顺序 / 文档属性

    private static void CheckPresentation(
        Action<string, bool, string> check, PptPresentation presentation, string pptxPath)
    {
        check("[PPT阅读器] 16:9 尺寸换算：12192000×6858000 EMU → 1280×720 DIP",
            Near(presentation.WidthDip, 1280) && Near(presentation.HeightDip, 720),
            $"{presentation.WidthDip:0.##} × {presentation.HeightDip:0.##} DIP");

        check("[PPT阅读器] 宽高比约 16:9、IsWideScreen=true、张数 2",
            Math.Abs(presentation.AspectRatio - 16.0 / 9.0) < 0.01
            && presentation.IsWideScreen && presentation.SlideCount == 2,
            $"{presentation.AspectRatio:0.####} / {presentation.SlideCount} 页");

        // sldIdLst 故意与文件名相反：第 1 页是 slide2.xml，第 2 页是 slide1.xml
        check("[PPT阅读器] 幻灯片顺序按 sldIdLst（不是文件名）：第 1 页是文件里的 slide2.xml",
            presentation.Slides.Count == 2
            && presentation.Slides[0].Index == 1
            && presentation.Slides[1].Index == 2
            && presentation.Slides[0].Title == "第二页标题"
            && presentation.Slides[1].Title == "第一页标题",
            string.Join(" | ", presentation.Slides.Select(slide => $"{slide.Index}. {slide.Title}")));

        check("[PPT阅读器] 文档属性：core.xml 的标题 / 作者生效，FilePath 保留",
            presentation.Title == "自检演示文稿"
            && presentation.Properties.Author == "自检作者"
            && presentation.FilePath == pptxPath,
            $"{presentation.Title} / {presentation.Properties.Author} / {presentation.FilePath}");

        check("[PPT阅读器] 主题：theme1.xml 的 accent1 解析成 #4472C4，颜色表至少有 10 个键",
            presentation.Theme.GetColor("accent1", Colors.Black) == Color.FromRgb(0x44, 0x72, 0xC4)
            && presentation.Theme.Colors.Count >= 10,
            $"accent1={presentation.Theme.GetColor("accent1", Colors.Black)} / {presentation.Theme.Colors.Count} 个颜色");
    }

    // ---------------------------------------------------------------- 版式继承

    private static void CheckPlaceholderInheritance(Action<string, bool, string> check, PptPresentation presentation)
    {
        var slide = Slide(presentation, 1);
        var title = FlattenShapes(slide).OfType<PptTextShape>().FirstOrDefault(shape =>
            shape.IsPlaceholder && shape.PlaceholderType.Contains("title", StringComparison.OrdinalIgnoreCase));

        // layout 里标题占位符 xfrm：off(914400,685800) ext(10972800,1371600) → 96/72/1152/144 DIP
        check("[PPT阅读器] 版式继承：标题没写 xfrm → 从 slideLayout 占位符取到 96/72/1152/144 DIP",
            title is not null
            && Near(title.X, 96) && Near(title.Y, 72) && Near(title.Width, 1152) && Near(title.Height, 144)
            && title.Width > 0 && title.Height > 0,
            title is null
                ? "没有解析出标题占位符"
                : $"X={title.X:0.#} Y={title.Y:0.#} W={title.Width:0.#} H={title.Height:0.#}");

        var body = FindTextShape(slide, "粗体文字");
        // layout 里正文占位符 xfrm：off(914400,2286000) ext(10972800,4114800) → 96/240/1152/432 DIP
        check("[PPT阅读器] 版式继承：正文占位符（idx=1）位置来自 layout、IsPlaceholder=true",
            body is not null && body.IsPlaceholder
            && Near(body.X, 96) && Near(body.Y, 240) && Near(body.Width, 1152) && Near(body.Height, 432),
            body is null
                ? "没有解析出正文占位符"
                : $"IsPlaceholder={body.IsPlaceholder} / X={body.X:0.#} Y={body.Y:0.#} W={body.Width:0.#} H={body.Height:0.#}");

        check("[PPT阅读器] 标题占位符文字「第二页标题」、PlaceholderType 为 title",
            title is not null
            && title.Text.Paragraphs.Any(paragraph => paragraph.Text.Contains("第二页标题", StringComparison.Ordinal))
            && title.PlaceholderType.Contains("title", StringComparison.OrdinalIgnoreCase),
            title is null ? "没有标题占位符" : $"{title.PlaceholderType} / {title.Text.Text.Replace('\n', '/')}");
    }

    // ---------------------------------------------------------------- 文字格式

    private static void CheckTextFormatting(Action<string, bool, string> check, PptPresentation presentation)
    {
        var slide = Slide(presentation, 1);
        var body = FindTextShape(slide, "粗体文字");
        var paragraphs = body?.Text.Paragraphs ?? Array.Empty<PptParagraph>();

        PptTextRun? RunOf(string text) => paragraphs
            .SelectMany(paragraph => paragraph.Runs)
            .FirstOrDefault(run => run.Text.Contains(text, StringComparison.Ordinal));

        var bold = RunOf("粗体文字");
        var italic = RunOf("斜体文字");
        var underline = RunOf("下划线文字");
        check("[PPT阅读器] 文字格式：粗体 / 斜体 / 下划线分别识别",
            bold is { Bold: true } && italic is { Italic: true } && underline is { Underline: true },
            $"粗体={bold?.Bold} 斜体={italic?.Italic} 下划线={underline?.Underline}");

        var bigRun = RunOf("大号文字");
        check("[PPT阅读器] 文字格式：sz=2400（24 磅）× 96/72 → 32 DIP",
            bigRun is not null && Near(bigRun.FontSizeDip, 32),
            bigRun is null ? "没找到「大号文字」run" : $"{bigRun.FontSizeDip:0.##} DIP");

        // 没写 sz 的 run 走母版 bodyStyle 的 18 磅（= 24 DIP；和 PptDefaults.BodyFontSizeDip 一致）
        check("[PPT阅读器] 文字格式：正文 run 未写 sz → 母版默认 18 磅（24 DIP）",
            bold is not null && Near(bold.FontSizeDip, PptDefaults.BodyFontSizeDip),
            bold is null ? "没找到「粗体文字」run" : $"{bold.FontSizeDip:0.##} DIP");

        // 标题没写 sz：母版 titleStyle 44 磅（≈ 58.67 DIP；和 PptDefaults.TitleFontSizeDip 一致）
        var title = FlattenShapes(slide).OfType<PptTextShape>().FirstOrDefault(shape =>
            shape.IsPlaceholder && shape.PlaceholderType.Contains("title", StringComparison.OrdinalIgnoreCase));
        var titleRun = title?.Text.Paragraphs.SelectMany(paragraph => paragraph.Runs).FirstOrDefault();
        check("[PPT阅读器] 文字格式：标题 run 未写 sz → 母版默认 44 磅（≈ 58.67 DIP）",
            titleRun is not null && Near(titleRun.FontSizeDip, PptDefaults.TitleFontSizeDip, 1),
            titleRun is null ? "没找到标题 run" : $"{titleRun.FontSizeDip:0.##} DIP");

        var accentRun = RunOf("主题色文字");
        var themeAccent1 = presentation.Theme.GetColor("accent1", Colors.Black);
        check("[PPT阅读器] 文字格式：schemeClr accent1 解析成主题色 #4472C4",
            accentRun is not null && accentRun.Color == themeAccent1
            && themeAccent1 == Color.FromRgb(0x44, 0x72, 0xC4),
            accentRun is null ? "没找到「主题色文字」run" : $"{accentRun.Color} / 主题 {themeAccent1}");

        var bulletParagraph = FindParagraph(slide, "粗体文字");
        check("[PPT阅读器] 段落：一级项目符号段 Bullet=true / Level=0 / IndentDip≈36（marL 342900）",
            bulletParagraph is not null && bulletParagraph.Bullet
            && bulletParagraph.Level == 0 && Near(bulletParagraph.IndentDip, 36),
            bulletParagraph is null
                ? "没找到正文第一段"
                : $"Bullet={bulletParagraph.Bullet} Level={bulletParagraph.Level} Indent={bulletParagraph.IndentDip:0.#}");

        var levelTwo = FindParagraph(slide, "二级项目文字");
        check("[PPT阅读器] 段落：二级项目符号段 Level=1 / Bullet=true / IndentDip≈78（marL 742950）",
            levelTwo is not null && levelTwo.Bullet && levelTwo.Level == 1
            && Near(levelTwo.IndentDip, 78),
            levelTwo is null
                ? "没找到二级段落"
                : $"Level={levelTwo.Level} Bullet={levelTwo.Bullet} Indent={levelTwo.IndentDip:0.#}");

        var noBullet = FindParagraph(slide, "无符号文字");
        check("[PPT阅读器] 段落：显式 buNone → Bullet=false",
            noBullet is not null && !noBullet.Bullet,
            noBullet is null ? "没找到「无符号文字」段落" : $"Bullet={noBullet.Bullet}");

        var titleParagraph = title?.Text.Paragraphs.FirstOrDefault();
        check("[PPT阅读器] 段落：标题段落没有项目符号",
            titleParagraph is not null && !titleParagraph.Bullet,
            titleParagraph is null ? "没有标题段落" : $"Bullet={titleParagraph.Bullet}");
    }

    // ---------------------------------------------------------------- 图片

    private static void CheckPicture(Action<string, bool, string> check, PptPresentation presentation)
    {
        var slide = Slide(presentation, 1);
        var picture = FlattenShapes(slide).OfType<PptPictureShape>().FirstOrDefault();

        check("[PPT阅读器] 图片：r:embed → media 字节，和写入的 PNG 完全一致",
            picture?.ImageBytes is not null && picture.ImageBytes.SequenceEqual(s_pixelPng),
            picture?.ImageBytes is null
                ? "ImageBytes 为空"
                : $"{picture.ImageBytes.Length} 字节");

        check("[PPT阅读器] 图片：srcRect l=10000 r=20000 → CropLeft=0.1 / CropRight=0.2，AltText 生效",
            picture is not null
            && Near(picture.CropLeft, 0.1, 0.001) && Near(picture.CropRight, 0.2, 0.001)
            && Near(picture.CropTop, 0) && Near(picture.CropBottom, 0)
            && picture.AltText == "自检图片替代文字",
            picture is null
                ? "没找到图片形状"
                : $"L={picture.CropLeft:0.###} R={picture.CropRight:0.###} / Alt={picture.AltText}");

        check("[PPT阅读器] 图片：xfrm 位置尺寸换算成 DIP（96/480/200/150）",
            picture is not null
            && Near(picture.X, 96) && Near(picture.Y, 480)
            && Near(picture.Width, 200) && Near(picture.Height, 150),
            picture is null
                ? "没找到图片形状"
                : $"X={picture.X:0.#} Y={picture.Y:0.#} W={picture.Width:0.#} H={picture.Height:0.#}");
    }

    // ---------------------------------------------------------------- 表格

    private static void CheckTable(Action<string, bool, string> check, PptPresentation presentation)
    {
        var slide = Slide(presentation, 1);
        var table = FlattenShapes(slide).OfType<PptTableShape>().FirstOrDefault();

        check("[PPT阅读器] 表格：2 行 × 2 列、列宽各 240 DIP（2286000 EMU）",
            table is not null && table.Rows.Count == 2
            && table.ColumnWidthsDip.Count == 2
            && Near(table.ColumnWidthsDip[0], 240) && Near(table.ColumnWidthsDip[1], 240),
            table is null
                ? "没找到表格"
                : $"{table.Rows.Count} 行 / 列宽 {string.Join(",", table.ColumnWidthsDip.Select(width => width.ToString("0.#")))}");

        var header = table?.Rows.Count > 0 && table.Rows[0].Cells.Count > 0 ? table.Rows[0].Cells[0] : null;
        var covered = table?.Rows.Count > 0 && table.Rows[0].Cells.Count > 1 ? table.Rows[0].Cells[1] : null;
        check("[PPT阅读器] 表格：gridSpan=2 表头、被合并格 IsCovered、文字与行高（48 DIP）",
            table is not null && header is not null && covered is not null
            && header.ColSpan == 2 && header.RowSpan == 1 && covered.IsCovered
            && CellText(header).Contains("合并表头", StringComparison.Ordinal)
            && Near(table.Rows[0].HeightDip, 48),
            table is null || header is null
                ? "解读失败"
                : $"ColSpan={header.ColSpan} Covered={covered?.IsCovered} 文字「{CellText(header)}」行高 {table.Rows[0].HeightDip:0.#}");

        var normalLeft = table?.Rows.Count > 1 && table.Rows[1].Cells.Count > 0 ? table.Rows[1].Cells[0] : null;
        var normalRight = table?.Rows.Count > 1 && table.Rows[1].Cells.Count > 1 ? table.Rows[1].Cells[1] : null;
        check("[PPT阅读器] 表格：普通单元格文字与填充色（D9E2F3 / FFF2CC）",
            normalLeft is not null && normalRight is not null
            && CellText(normalLeft).Contains("甲", StringComparison.Ordinal)
            && CellText(normalRight).Contains("乙", StringComparison.Ordinal)
            && header?.Fill?.Color == Color.FromRgb(0xD9, 0xE2, 0xF3)
            && normalRight.Fill?.Color == Color.FromRgb(0xFF, 0xF2, 0xCC),
            $"甲「{CellText(normalLeft)}」/ 乙「{CellText(normalRight)}」/ 表头底色 {header?.Fill?.Color} / 乙底色 {normalRight?.Fill?.Color}");
    }

    // ---------------------------------------------------------------- 组合 / 形状树

    private static void CheckGroupAndTree(Action<string, bool, string> check, PptPresentation presentation)
    {
        var slide = Slide(presentation, 1);
        var group = slide.Shapes.OfType<PptGroupShape>().FirstOrDefault();

        check("[PPT阅读器] 组合：Children 数量 1、子形状坐标已换算成幻灯片绝对坐标",
            group is not null && group.Children.Count == 1
            && group.Children[0] is PptTextShape child
            && child.Text.Paragraphs.Any(paragraph => paragraph.Text.Contains("组合内文字", StringComparison.Ordinal))
            && child.X >= group.X - 0.5 && child.Y >= group.Y - 0.5
            && Near(child.X, 1008) && Near(child.Y, 120),
            group is null
                ? "没找到组合"
                : $"组合 X={group.X:0.#} Y={group.Y:0.#} / 子形状 "
                  + (group.Children.Count > 0 ? $"X={group.Children[0].X:0.#} Y={group.Children[0].Y:0.#}" : "（没有子形状）"));

        check("[PPT阅读器] 形状树计数：Count / CountOf<T> 与幻灯片统计一致（共 6 个形状）",
            group is not null
            && PptShapeTree.Count(group) == 2
            && PptShapeTree.CountOf<PptTextShape>(group) == 1
            && slide.ShapeCount == 6
            && slide.PictureCount == 1
            && slide.TableCount == 1,
            group is null
                ? "没找到组合"
                : $"组合子树 {PptShapeTree.Count(group)} / 文本 {PptShapeTree.CountOf<PptTextShape>(group)} / 全页 {slide.ShapeCount} 形状 / 图片 {slide.PictureCount} / 表格 {slide.TableCount}");
    }

    // ---------------------------------------------------------------- 备注 / 纯文本

    private static void CheckNotesAndPlainText(Action<string, bool, string> check, PptPresentation presentation)
    {
        var rich = Slide(presentation, 1);
        var plain = Slide(presentation, 2);

        check("[PPT阅读器] 备注：第 2 页 HasNotes=true、备注文本来自 notesSlide（多段）",
            plain.HasNotes
            && plain.Notes.Contains("自检备注关键字", StringComparison.Ordinal)
            && plain.Notes.Contains("备注第二段", StringComparison.Ordinal),
            plain.Notes.Replace('\n', '/').Replace('\r', ' '));

        check("[PPT阅读器] 备注：第 1 页没有备注 → HasNotes=false、Notes 为空",
            !rich.HasNotes && rich.Notes.Length == 0,
            $"HasNotes={rich.HasNotes} / Notes 长度 {rich.Notes.Length}");

        check("[PPT阅读器] PlainText：含标题 / 正文 / 表格文字，且不含备注",
            rich.PlainText.Contains("第二页标题", StringComparison.Ordinal)
            && rich.PlainText.Contains("粗体文字", StringComparison.Ordinal)
            && rich.PlainText.Contains("合并表头", StringComparison.Ordinal)
            && rich.PlainText.Contains("甲", StringComparison.Ordinal)
            && rich.PlainText.Contains("乙", StringComparison.Ordinal)
            && plain.PlainText.Contains("第一页标题", StringComparison.Ordinal)
            && !plain.PlainText.Contains("自检备注关键字", StringComparison.Ordinal),
            rich.PlainText.Replace('\n', '/').Replace('\r', ' '));

        check("[PPT阅读器] 第 2 页背景是纯色 #FFCC00",
            plain.Background is { Kind: PptFillKind.Solid }
            && plain.Background.Color == Color.FromRgb(0xFF, 0xCC, 0x00),
            plain.Background is null
                ? "Background 为空"
                : $"{plain.Background.Kind} / {plain.Background.Color}");
    }

    // ---------------------------------------------------------------- 查找

    private static void CheckSearch(Action<string, bool, string> check, PptPresentation presentation)
    {
        var tableHits = PptTextSearch.Search(presentation, "合并表头");
        check("[PPT阅读器] 查找：正文命中 1 处、页号 1、来源 SlideText、片段含关键字",
            tableHits.Count == 1
            && tableHits[0].SlideIndex == 1
            && tableHits[0].Source == PptSearchSource.SlideText
            && tableHits[0].MatchIndex >= 0
            && tableHits[0].MatchLength == 4
            && tableHits[0].Snippet.Contains("合并表头", StringComparison.Ordinal),
            tableHits.Count == 0
                ? "没有命中"
                : $"第 {tableHits[0].SlideIndex} 页 / {tableHits[0].Source} / 片段「{tableHits[0].Snippet}」");

        var noteHits = PptTextSearch.Search(presentation, "自检备注关键字");
        check("[PPT阅读器] 查找：备注命中 1 处、页号 2、来源 Notes",
            noteHits.Count == 1
            && noteHits[0].SlideIndex == 2
            && noteHits[0].Source == PptSearchSource.Notes
            && noteHits[0].Snippet.Contains("自检备注关键字", StringComparison.Ordinal),
            noteHits.Count == 0
                ? "没有命中"
                : $"第 {noteHits[0].SlideIndex} 页 / {noteHits[0].Source}");

        check("[PPT阅读器] 查找：默认忽略大小写，matchCase=true 时区分",
            PptTextSearch.Search(presentation, "hello ppt").Count == 1
            && PptTextSearch.Search(presentation, "hello ppt", matchCase: true).Count == 0
            && PptTextSearch.Search(presentation, "Hello PPT", matchCase: true).Count == 1,
            $"不区分={PptTextSearch.Search(presentation, "hello ppt").Count} / 区分={PptTextSearch.Search(presentation, "hello ppt", true).Count}");

        check("[PPT阅读器] 查找：空白查询返回空列表（空格 / 空串都算）",
            PptTextSearch.Search(presentation, "").Count == 0
            && PptTextSearch.Search(presentation, "   ").Count == 0,
            $"空串={PptTextSearch.Search(presentation, "").Count} / 空格={PptTextSearch.Search(presentation, "   ").Count}");

        var longText = new string('前', 100) + "关键字" + new string('后', 100);
        var snippet = PptTextSearch.BuildSnippet(longText, 100, 3, radius: 5);
        check("[PPT阅读器] BuildSnippet：前后截断时加省略号、命中词在片段里",
            snippet.StartsWith('…') && snippet.EndsWith('…')
            && snippet.Contains("关键字", StringComparison.Ordinal)
            && PptTextSearch.BuildSnippet("abc", 0, 3) == "abc",
            snippet);

        var manualHits = new List<PptSearchHit>
        {
            new(1, "1. 甲", PptSearchSource.SlideText, 0, 1, "甲"),
            new(1, "1. 甲", PptSearchSource.Notes, 3, 1, "甲"),
            new(2, "2. 乙", PptSearchSource.SlideText, 0, 1, "乙"),
        };
        check("[PPT阅读器] 查找统计：CountSlides 去重 / DescribeHits 文案（命中 / 无命中 / 空输入）",
            PptTextSearch.CountSlides(manualHits) == 2
            && PptTextSearch.DescribeHits(manualHits, "甲").Contains("找到 3 处", StringComparison.Ordinal)
            && PptTextSearch.DescribeHits(manualHits, "甲").Contains("2 页", StringComparison.Ordinal)
            && PptTextSearch.DescribeHits(Array.Empty<PptSearchHit>(), "找不到").Contains("没找到「找不到」", StringComparison.Ordinal)
            && PptTextSearch.DescribeHits(Array.Empty<PptSearchHit>()) == "没有输入查找内容",
            PptTextSearch.DescribeHits(manualHits, "甲"));
    }

    // ---------------------------------------------------------------- 配置 / 最近打开 / 阅读位置

    private static void CheckConfig(Action<string, bool, string> check, string root)
    {
        var config = new PptViewerConfig();
        check("[PPT阅读器] 配置默认值：适应窗口 / 缩放 100 / 缩略图开 / 备注关 / 放映跳过隐藏页 / 最近上限 50",
            config.FitToWindow && config.ZoomPercent == 100 && config.ZoomPercentEffective == 100
            && Math.Abs(config.ZoomScale - 1.0) < 1e-9
            && config.ShowThumbnails && !config.ShowNotes && config.SkipHiddenInShow
            && config.RememberReadingPositionEnabled && !config.SearchMatchCase
            && config.RecentLimit == 50 && config.RecentLimitEffective == 50 && config.RecordsRecent
            && config.LastFolder.Length == 0,
            $"适应={config.FitToWindow} / 缩放={config.ZoomPercent} / 缩略图={config.ShowThumbnails} / "
            + $"备注={config.ShowNotes} / 最近={config.RecentLimit}");

        config.ZoomPercent = 999;
        var upper = config.ZoomPercentEffective;
        config.ZoomPercent = -5;
        var lower = config.ZoomPercentEffective;
        config.ZoomPercent = 100;
        check("[PPT阅读器] 缩放夹取 25 ~ 400；步进 ±10 且不越界",
            upper == 400 && lower == 25
            && PptViewerConfig.StepZoomPercent(100, 1) == 110
            && PptViewerConfig.StepZoomPercent(100, -1) == 90
            && PptViewerConfig.StepZoomPercent(400, 1) == 400
            && PptViewerConfig.StepZoomPercent(25, -1) == 25
            && PptViewerConfig.StepZoomPercent(999, 0) == 400,
            $"999→{upper} / -5→{lower} / 100+1→{PptViewerConfig.StepZoomPercent(100, 1)}");

        var fileA = Path.Combine(root, "最近甲.pptx");
        var fileB = Path.Combine(root, "最近乙.pptx");
        File.WriteAllText(fileA, "x");
        File.WriteAllText(fileB, "x");

        config.RememberRecent(fileA);
        config.RememberRecent(fileA);
        check("[PPT阅读器] 最近打开：同一路径去重（记两次只有一条）",
            config.RecentEntries.Count == 1
            && string.Equals(Path.GetFullPath(config.RecentEntries[0].Path), Path.GetFullPath(fileA),
                StringComparison.OrdinalIgnoreCase),
            $"{config.RecentEntries.Count} 条");

        config.RememberRecent(fileB);
        config.RememberRecent(fileA);
        check("[PPT阅读器] 最近打开：新打开的置顶（A → B → A 后 A 在最前）",
            config.RecentEntries.Count == 2
            && Path.GetFileName(config.RecentEntries[0].Path) == "最近甲.pptx",
            string.Join(" | ", config.RecentEntries.Select(entry => Path.GetFileName(entry.Path))));

        config.RecentLimit = 1;
        config.TrimRecentToLimit();
        check("[PPT阅读器] 最近打开：按上限裁剪（上限 1 → 只留最新一条）",
            config.RecentLimitEffective == 1 && config.RecentEntries.Count == 1,
            $"{config.RecentEntries.Count} 条");

        config.RecentLimit = 0;
        config.TrimRecentToLimit();
        check("[PPT阅读器] 最近打开：上限 0 = 不记录并清空，RecordsRecent=false",
            config.RecentEntries.Count == 0 && !config.RecordsRecent,
            $"上限={config.RecentLimitEffective} / {config.RecentEntries.Count} 条");

        config.RecentLimit = 50;
        config.RememberRecent(fileA);
        config.ClearRecent();
        check("[PPT阅读器] 最近打开：清空只删记录、不删本地文件",
            config.RecentEntries.Count == 0 && File.Exists(fileA),
            $"{config.RecentEntries.Count} 条 / 文件存在={File.Exists(fileA)}");

        config.RememberReadingPosition(fileA, 3);
        check("[PPT阅读器] 阅读位置：保存 / 查询 / 路径大小写去重（同一份只存一条）",
            config.FindReadingPosition(fileA) is { SlideIndex: 3 }
            && config.ReadingPositions.Count == 1
            && config.FindReadingPosition(fileA.ToUpperInvariant()) is not null,
            $"{config.ReadingPositions.Count} 条 / 取回第 {config.FindReadingPosition(fileA)?.SlideIndex} 页");

        var key = PptViewerConfig.NormalizePositionKey(fileA);
        check("[PPT阅读器] NormalizePositionKey：合法路径 → 小写完整路径，空值 / null → 空串",
            key.Length > 0 && key == key.ToLowerInvariant() && Path.IsPathRooted(key)
            && PptViewerConfig.NormalizePositionKey("   ") == ""
            && PptViewerConfig.NormalizePositionKey(null) == "",
            key);

        config.RememberReadingPosition(fileB, 0);
        var countBefore = config.ReadingPositions.Count;
        config.RememberReadingPosition("", 5);
        check("[PPT阅读器] 阅读位置：页号下限夹到 1、非法路径不写脏数据",
            config.FindReadingPosition(fileB) is { SlideIndex: 1 }
            && countBefore == 2 && config.ReadingPositions.Count == 2
            && config.FindReadingPosition("") is null,
            $"页号={config.FindReadingPosition(fileB)?.SlideIndex} / {config.ReadingPositions.Count} 条");

        // 上限 200：批量写 205 份（保存时间递增），最老的应被淘汰
        var baseTime = DateTime.UtcNow.AddDays(-30);
        for (var index = 0; index < PptViewerConfig.MaxReadingPositions + 5; index++)
        {
            config.RememberReadingPosition(
                Path.Combine(root, $"批量阅读位置{index}.pptx"), index + 1, baseTime.AddMinutes(index));
        }

        check("[PPT阅读器] 阅读位置：超过上限（200）按保存时间淘汰最老的记录",
            config.ReadingPositions.Count == PptViewerConfig.MaxReadingPositions
            && config.FindReadingPosition(Path.Combine(root, "批量阅读位置0.pptx")) is null,
            $"{config.ReadingPositions.Count} 条");

        config.ClearReadingPositions();
        check("[PPT阅读器] 阅读位置：ClearReadingPositions 清空全部记录",
            config.ReadingPositions.Count == 0,
            $"{config.ReadingPositions.Count} 条");

        // 恢复默认：开关回默认，但「最近记录 / 阅读位置 / 上次文件夹」属于使用痕迹，不能被清
        config.ZoomPercent = 300;
        config.FitToWindow = false;
        config.ShowThumbnails = false;
        config.ShowNotes = true;
        config.SkipHiddenInShow = false;
        config.SearchMatchCase = true;
        config.RecentLimit = 5;
        config.LastFolder = root;
        config.RememberRecent(fileB);
        config.RememberReadingPosition(fileB, 2);
        config.ResetToDefaults();
        check("[PPT阅读器] 恢复默认：开关 / 缩放 / 最近上限回默认，但不动最近记录、阅读位置、上次文件夹",
            config.FitToWindow && config.ZoomPercent == 100 && config.ShowThumbnails && !config.ShowNotes
            && config.SkipHiddenInShow && !config.SearchMatchCase && config.RecentLimit == 50
            && config.RecentEntries.Count == 1 && !string.IsNullOrEmpty(config.LastFolder)
            && config.FindReadingPosition(fileB) is not null,
            $"缩放={config.ZoomPercent} / 最近 {config.RecentEntries.Count} 条 / 阅读位置 {config.ReadingPositions.Count} 条");

        var json = config.ToJson();
        check("[PPT阅读器] 配置序列化：开关 / 最近 / 阅读位置进 JSON，[JsonIgnore] 计算属性不进",
            json.Contains("\"fitToWindow\"", StringComparison.Ordinal)
            && json.Contains("\"showThumbnails\"", StringComparison.Ordinal)
            && json.Contains("\"searchMatchCase\"", StringComparison.Ordinal)
            && json.Contains("\"recentEntries\"", StringComparison.Ordinal)
            && json.Contains("\"readingPositions\"", StringComparison.Ordinal)
            && !json.Contains("\"zoomScale\"", StringComparison.Ordinal)
            && !json.Contains("\"zoomPercentEffective\"", StringComparison.Ordinal)
            && !json.Contains("\"recentLimitEffective\"", StringComparison.Ordinal),
            json.Length > 180 ? json[..180] + "…" : json);
    }

    // ---------------------------------------------------------------- 坏文件 / 降级

    private static void CheckErrors(Action<string, bool, string> check, string root)
    {
        var missingPath = Path.Combine(root, "不存在的演示文稿.pptx");
        var missingRejected = false;
        var missingMessage = "";
        try { PptDocumentParser.Parse(missingPath); }
        catch (OoxmlException ex) { missingRejected = true; missingMessage = ex.Message; }

        check("[PPT阅读器] 不存在的文件抛 OoxmlException，消息是中文（含「不存在」）",
            missingRejected && missingMessage.Contains("不存在", StringComparison.Ordinal),
            missingMessage);

        var badPath = Path.Combine(root, "坏文件.pptx");
        File.WriteAllText(badPath, "这不是一个 ZIP 文件");
        var badRejected = false;
        var badMessage = "";
        try { PptDocumentParser.Parse(badPath); }
        catch (OoxmlException ex) { badRejected = true; badMessage = ex.Message; }

        check("[PPT阅读器] 非 zip 文件抛 OoxmlException，消息含「不是有效」或 ZIP",
            badRejected
            && (badMessage.Contains("不是有效", StringComparison.Ordinal)
                || badMessage.Contains("ZIP", StringComparison.OrdinalIgnoreCase)),
            badMessage);

        // 多一个「缺 xfrm 的文本框 + 关系缺失的图片」：解析要降级成功，不能因为坏形状拒绝整份文件
        var degradedPath = Path.Combine(root, "坏形状降级.pptx");
        CreateMinimalPptx(degradedPath, withBrokenShape: true);
        Exception? degradationFailure = null;
        PptPresentation? degraded = null;
        try { degraded = PptDocumentParser.Parse(degradedPath); }
        catch (Exception ex) { degradationFailure = ex; }

        check("[PPT阅读器] 坏形状降级：缺 xfrm 的文本框 / 图片关系缺失不崩，整份仍能打开",
            degradationFailure is null && degraded is not null
            && degraded.SlideCount == 2
            && degraded.Slides[0].Title == "第二页标题",
            degradationFailure?.Message
                ?? $"{degraded?.SlideCount} 页 / 第 1 页标题"
                  + (degraded is { SlideCount: > 0 } ? $"「{degraded.Slides[0].Title}」" : "（没有幻灯片）"));
    }

    // ---------------------------------------------------------------- 渲染器（STA 线程）

    private static void CheckRenderer(Action<string, bool, string> check, PptPresentation presentation)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var slide = Slide(presentation, 1);

                var drawing = PptSlideRenderer.Render(presentation, slide);
                check("[PPT阅读器] 渲染：Render 返回非空 DrawingGroup、已冻结、Bounds ≈ 1280×720",
                    drawing is not null && drawing.IsFrozen
                    && Near(drawing.Bounds.Width, presentation.WidthDip, 1)
                    && Near(drawing.Bounds.Height, presentation.HeightDip, 1)
                    && Near(drawing.Bounds.Width, 1280, 1) && Near(drawing.Bounds.Height, 720, 1),
                    drawing is null
                        ? "Render 返回 null"
                        : $"{drawing.Bounds.Width:0.#}×{drawing.Bounds.Height:0.#} / 冻结={drawing.IsFrozen}");

                var image = PptSlideRenderer.CreateImage(presentation, slide);
                check("[PPT阅读器] 渲染：CreateImage 返回 DrawingImage，内部 DrawingGroup 已冻结",
                    image is DrawingImage drawingImage
                    && drawingImage.Drawing is DrawingGroup { IsFrozen: true },
                    image is null ? "CreateImage 返回 null" : image.GetType().Name);
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
            check("[PPT阅读器] 渲染：幻灯片绘制成功", false, $"{failure.GetType().Name}: {failure.Message}");
    }

    // ---------------------------------------------------------------- 断言 / 查形状用的小工具

    /// <summary>
    /// 按演示顺序取第 <paramref name="index"/> 页（1 开始）。
    /// 优先认 <see cref="PptSlide.Index"/>；解析器索引没写对时退回按位置取，
    /// 这样「索引错」只让顺序断言失败，不会把整组断言带崩。
    /// </summary>
    private static PptSlide Slide(PptPresentation presentation, int index)
        => presentation.Slides.FirstOrDefault(slide => slide.Index == index)
           ?? presentation.Slides.ElementAtOrDefault(index - 1)
           ?? throw new InvalidOperationException(
               $"没有第 {index} 页（共 {presentation.Slides.Count} 页）");

    /// <summary>把一页的形状（组合递归展开）摊平成叶子形状列表。</summary>
    private static IEnumerable<PptShape> FlattenShapes(PptSlide slide)
    {
        var output = new List<PptShape>();
        foreach (var shape in slide.Shapes) PptShapeTree.Flatten(shape, output);
        return output;
    }

    /// <summary>找第一个文字包含 <paramref name="text"/> 的文本形状（占位符 / 文本框 / 组合内的都算）。</summary>
    private static PptTextShape? FindTextShape(PptSlide slide, string text)
        => FlattenShapes(slide).OfType<PptTextShape>().FirstOrDefault(shape =>
            shape.Text.Paragraphs.Any(paragraph => paragraph.Text.Contains(text, StringComparison.Ordinal)));

    /// <summary>找第一个文字包含 <paramref name="text"/> 的段落。</summary>
    private static PptParagraph? FindParagraph(PptSlide slide, string text)
        => FlattenShapes(slide).OfType<PptTextShape>()
            .SelectMany(shape => shape.Text.Paragraphs)
            .FirstOrDefault(paragraph => paragraph.Text.Contains(text, StringComparison.Ordinal));

    /// <summary>单元格纯文本（空单元格返回空串）。</summary>
    private static string CellText(PptTableCell? cell)
        => cell?.Text?.Text ?? "";

    /// <summary>浮点容差比较（坐标 / 字号 / 比例都用它，避免实现取整方式不同导致假失败）。</summary>
    private static bool Near(double actual, double expected, double tolerance = 0.5)
        => Math.Abs(actual - expected) <= tolerance;

    // ---------------------------------------------------------------- 造一份最小 pptx

    /// <summary>
    /// 在临时目录写一个最小但覆盖主要特性的 pptx：
    /// <list type="bullet">
    ///   <item><c>sldIdLst</c> 先 rId2 后 rId1（验证按演示顺序而不是文件名排页）；</item>
    ///   <item>slide2.xml（演示第 1 页）：标题 / 正文多 run / 图片 + srcRect / 表格 gridSpan / 组合；</item>
    ///   <item>slide1.xml（演示第 2 页）：纯色背景 + notesSlide 备注；</item>
    ///   <item>slideMaster1.xml / slideLayout1.xml / theme1.xml：版式占位符 + 主题色 + 默认字号。</item>
    /// </list>
    /// <para><paramref name="withBrokenShape"/> = true 时再塞一个「缺 xfrm 的文本框 + 关系缺失的图片」，
    /// 验证解析器对坏形状降级而不是拒绝整份文件。</para>
    /// </summary>
    private static void CreateMinimalPptx(string path, bool withBrokenShape = false)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

        WriteText(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Default Extension="png" ContentType="image/png"/>
              <Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>
              <Override PartName="/ppt/slideMasters/slideMaster1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml"/>
              <Override PartName="/ppt/slideLayouts/slideLayout1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml"/>
              <Override PartName="/ppt/slides/slide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
              <Override PartName="/ppt/slides/slide2.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>
              <Override PartName="/ppt/notesSlides/notesSlide1.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml"/>
              <Override PartName="/ppt/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
              <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
            </Types>
            """);

        WriteText(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/>
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
              <dc:title>自检演示文稿</dc:title>
              <dc:creator>自检作者</dc:creator>
              <cp:lastModifiedBy>自检修改者</cp:lastModifiedBy>
              <dcterms:created xsi:type="dcterms:W3CDTF">2024-05-01T08:30:00Z</dcterms:created>
              <dcterms:modified xsi:type="dcterms:W3CDTF">2024-06-02T09:45:00Z</dcterms:modified>
            </cp:coreProperties>
            """);

        // sldIdLst 故意先 rId2（slide2.xml）后 rId1（slide1.xml）：验证按 sldIdLst 顺序而不是文件名顺序。
        // XML 里刻意不写注释（部分解析器按子节点遍历，注释会干扰）。
        WriteText(archive, "ppt/presentation.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:presentation
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldMasterIdLst>
                <p:sldMasterId id="2147483648" r:id="rId3"/>
              </p:sldMasterIdLst>
              <p:sldIdLst>
                <p:sldId id="257" r:id="rId2"/>
                <p:sldId id="256" r:id="rId1"/>
              </p:sldIdLst>
              <p:sldSz cx="12192000" cy="6858000" type="screen16x9"/>
              <p:notesSz cx="6858000" cy="9144000"/>
            </p:presentation>
            """);

        WriteText(archive, "ppt/_rels/presentation.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="slideMasters/slideMaster1.xml"/>
              <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/>
            </Relationships>
            """);

        WriteText(archive, "ppt/theme/theme1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="自检主题">
              <a:themeElements>
                <a:clrScheme name="自检配色">
                  <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
                  <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
                  <a:dk2><a:srgbClr val="44546A"/></a:dk2>
                  <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
                  <a:accent1><a:srgbClr val="4472C4"/></a:accent1>
                  <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
                  <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
                  <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
                  <a:accent5><a:srgbClr val="5B9BD5"/></a:accent5>
                  <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
                  <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
                  <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
                </a:clrScheme>
                <a:fontScheme name="自检字体">
                  <a:majorFont>
                    <a:latin typeface="Georgia"/>
                    <a:ea typeface="Microsoft YaHei"/>
                    <a:cs typeface=""/>
                  </a:majorFont>
                  <a:minorFont>
                    <a:latin typeface="Calibri"/>
                    <a:ea typeface="Microsoft YaHei"/>
                    <a:cs typeface=""/>
                  </a:minorFont>
                </a:fontScheme>
                <a:fmtScheme name="自检格式">
                  <a:fillStyleLst>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                  </a:fillStyleLst>
                  <a:lnStyleLst>
                    <a:ln w="6350"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                    <a:ln w="12700"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                    <a:ln w="19050"><a:solidFill><a:schemeClr val="phClr"/></a:solidFill></a:ln>
                  </a:lnStyleLst>
                  <a:effectStyleLst>
                    <a:effectStyle><a:effectLst/></a:effectStyle>
                    <a:effectStyle><a:effectLst/></a:effectStyle>
                    <a:effectStyle><a:effectLst/></a:effectStyle>
                  </a:effectStyleLst>
                  <a:bgFillStyleLst>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                    <a:solidFill><a:schemeClr val="phClr"/></a:solidFill>
                  </a:bgFillStyleLst>
                </a:fmtScheme>
              </a:themeElements>
            </a:theme>
            """);

        WriteText(archive, "ppt/slideMasters/slideMaster1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldMaster
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld>
                <p:bg>
                  <p:bgPr>
                    <a:solidFill><a:schemeClr val="lt1"/></a:solidFill>
                    <a:effectLst/>
                  </p:bgPr>
                </p:bg>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="标题占位符 1"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="914400" y="274638"/><a:ext cx="10972800" cy="1143000"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                    <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="zh-CN"/></a:p></p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="正文占位符 2"/><p:cNvSpPr><a:spLocks noGrp="1"/></p:cNvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="914400" y="1600200"/><a:ext cx="10972800" cy="4525963"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                    <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="zh-CN"/></a:p></p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
              <p:clrMap bg1="lt1" tx1="dk1" bg2="lt2" tx2="dk2" accent1="accent1" accent2="accent2" accent3="accent3" accent4="accent4" accent5="accent5" accent6="accent6" hlink="hlink" folHlink="folHlink"/>
              <p:sldLayoutIdLst><p:sldLayoutId id="2147483649" r:id="rId1"/></p:sldLayoutIdLst>
              <p:txStyles>
                <p:titleStyle>
                  <a:lvl1pPr algn="ctr">
                    <a:buNone/>
                    <a:defRPr sz="4400" b="1">
                      <a:solidFill><a:schemeClr val="tx1"/></a:solidFill>
                      <a:latin typeface="+mj-lt"/>
                      <a:ea typeface="+mj-ea"/>
                    </a:defRPr>
                  </a:lvl1pPr>
                </p:titleStyle>
                <p:bodyStyle>
                  <a:lvl1pPr marL="342900" indent="-342900">
                    <a:buChar char="•"/>
                    <a:defRPr sz="1800"><a:latin typeface="+mn-lt"/><a:ea typeface="+mn-ea"/></a:defRPr>
                  </a:lvl1pPr>
                  <a:lvl2pPr marL="742950" indent="-285750">
                    <a:buChar char="•"/>
                    <a:defRPr sz="1600"><a:latin typeface="+mn-lt"/><a:ea typeface="+mn-ea"/></a:defRPr>
                  </a:lvl2pPr>
                </p:bodyStyle>
                <p:otherStyle><a:defPPr><a:defRPr lang="zh-CN"/></a:defPPr></p:otherStyle>
              </p:txStyles>
            </p:sldMaster>
            """);

        WriteText(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/>
            </Relationships>
            """);

        // 标题占位符（type=title）与正文占位符（idx=1）在这里写好 xfrm：
        // slide 上同名 / 同 idx 的占位符不写 xfrm，坐标就从这里继承（版式继承断言用）。
        WriteText(archive, "ppt/slideLayouts/slideLayout1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sldLayout
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                type="obj" preserve="1">
              <p:cSld name="标题和内容">
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="标题 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="914400" y="685800"/><a:ext cx="10972800" cy="1371600"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                    <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="zh-CN"/></a:p></p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="内容 2"/><p:cNvSpPr/><p:nvPr><p:ph idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="914400" y="2286000"/><a:ext cx="10972800" cy="4114800"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></p:spPr>
                    <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:endParaRPr lang="zh-CN"/></a:p></p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
              <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:sldLayout>
            """);

        WriteText(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster" Target="../slideMasters/slideMaster1.xml"/>
            </Relationships>
            """);

        // ---------- slide1.xml = 演示顺序第 2 页：纯色背景 + 备注 ----------
        WriteText(archive, "ppt/slides/slide1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld>
                <p:bg>
                  <p:bgPr>
                    <a:solidFill><a:srgbClr val="FFCC00"/></a:solidFill>
                    <a:effectLst/>
                  </p:bgPr>
                </p:bg>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="标题 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="zh-CN"/><a:t>第一页标题</a:t></a:r></a:p></p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
              <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:sld>
            """);

        WriteText(archive, "ppt/slides/_rels/slide1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="../notesSlides/notesSlide1.xml"/>
            </Relationships>
            """);

        // ---------- slide2.xml = 演示顺序第 1 页：内容最丰富 ----------
        // 标题 / 正文占位符都故意不写 a:xfrm（验证从 slideLayout 继承）；
        // withBrokenShape = true 时在 </p:spTree> 前插入「缺 xfrm 的文本框 + 关系缺失的图片」。
        var brokenShapeXml = withBrokenShape
            ? """
              <p:sp>
                <p:nvSpPr><p:cNvPr id="90" name="坏形状（缺 xfrm）"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
                <p:spPr/>
                <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>坏形状文字</a:t></a:r></a:p></p:txBody>
              </p:sp>
              <p:pic>
                <p:nvPicPr><p:cNvPr id="91" name="坏图片（关系缺失）"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                <p:blipFill><a:blip r:embed="rIdMissingImage"/></p:blipFill>
                <p:spPr/>
              </p:pic>
              """
            : "";

        var slide2Xml = """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:sld
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="标题 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:lstStyle/>
                      <a:p><a:r><a:rPr lang="zh-CN" dirty="0"/><a:t>第二页标题</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="3" name="正文 2"/><p:cNvSpPr/><p:nvPr><p:ph idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:lstStyle/>
                      <a:p>
                        <a:pPr lvl="0" marL="342900" indent="-342900"><a:buChar char="•"/></a:pPr>
                        <a:r><a:rPr lang="zh-CN" b="1"/><a:t>粗体文字</a:t></a:r>
                        <a:r><a:rPr lang="zh-CN" i="1"/><a:t>斜体文字</a:t></a:r>
                        <a:r><a:rPr lang="zh-CN" u="sng"/><a:t>下划线文字</a:t></a:r>
                        <a:r><a:rPr lang="zh-CN" sz="2400"/><a:t>大号文字</a:t></a:r>
                        <a:r><a:rPr lang="zh-CN"><a:solidFill><a:schemeClr val="accent1"/></a:solidFill></a:rPr><a:t>主题色文字</a:t></a:r>
                      </a:p>
                      <a:p>
                        <a:pPr lvl="1" marL="742950" indent="-285750"><a:buChar char="•"/></a:pPr>
                        <a:r><a:rPr lang="zh-CN"/><a:t>二级项目文字</a:t></a:r>
                      </a:p>
                      <a:p>
                        <a:pPr lvl="0"><a:buNone/></a:pPr>
                        <a:r><a:rPr lang="zh-CN"/><a:t>无符号文字</a:t></a:r>
                        <a:r><a:rPr lang="en-US"/><a:t xml:space="preserve"> Hello PPT</a:t></a:r>
                      </a:p>
                    </p:txBody>
                  </p:sp>
                  <p:pic>
                    <p:nvPicPr>
                      <p:cNvPr id="4" name="图片 3" descr="自检图片替代文字"/>
                      <p:cNvPicPr><a:picLocks noChangeAspect="1"/></p:cNvPicPr>
                      <p:nvPr/>
                    </p:nvPicPr>
                    <p:blipFill>
                      <a:blip r:embed="rId1"/>
                      <a:srcRect l="10000" t="0" r="20000" b="0"/>
                      <a:stretch><a:fillRect/></a:stretch>
                    </p:blipFill>
                    <p:spPr>
                      <a:xfrm><a:off x="914400" y="4572000"/><a:ext cx="1905000" cy="1428750"/></a:xfrm>
                      <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                    </p:spPr>
                  </p:pic>
                  <p:graphicFrame>
                    <p:nvGraphicFramePr>
                      <p:cNvPr id="5" name="表格 4"/>
                      <p:cNvGraphicFramePr><a:graphicFrameLocks noGrp="1"/></p:cNvGraphicFramePr>
                      <p:nvPr/>
                    </p:nvGraphicFramePr>
                    <p:xfrm><a:off x="4572000" y="4572000"/><a:ext cx="4572000" cy="1371600"/></p:xfrm>
                    <a:graphic>
                      <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table">
                        <a:tbl>
                          <a:tblPr firstRow="1" bandRow="1"><a:tableStyleId>{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}</a:tableStyleId></a:tblPr>
                          <a:tblGrid><a:gridCol w="2286000"/><a:gridCol w="2286000"/></a:tblGrid>
                          <a:tr h="457200">
                            <a:tc gridSpan="2">
                              <a:txBody><a:bodyPr/><a:p><a:r><a:rPr lang="zh-CN"/><a:t>合并表头</a:t></a:r></a:p></a:txBody>
                              <a:tcPr><a:solidFill><a:srgbClr val="D9E2F3"/></a:solidFill></a:tcPr>
                            </a:tc>
                            <a:tc hMerge="1">
                              <a:txBody><a:bodyPr/><a:p/></a:txBody>
                              <a:tcPr/>
                            </a:tc>
                          </a:tr>
                          <a:tr h="457200">
                            <a:tc>
                              <a:txBody><a:bodyPr/><a:p><a:r><a:rPr lang="zh-CN"/><a:t>甲</a:t></a:r></a:p></a:txBody>
                              <a:tcPr/>
                            </a:tc>
                            <a:tc>
                              <a:txBody><a:bodyPr/><a:p><a:r><a:rPr lang="zh-CN"/><a:t>乙</a:t></a:r></a:p></a:txBody>
                              <a:tcPr><a:solidFill><a:srgbClr val="FFF2CC"/></a:solidFill></a:tcPr>
                            </a:tc>
                          </a:tr>
                        </a:tbl>
                      </a:graphicData>
                    </a:graphic>
                  </p:graphicFrame>
                  <p:grpSp>
                    <p:nvGrpSpPr><p:cNvPr id="6" name="组合 5"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                    <p:grpSpPr>
                      <a:xfrm>
                        <a:off x="9144000" y="914400"/>
                        <a:ext cx="1828800" cy="914400"/>
                        <a:chOff x="9144000" y="914400"/>
                        <a:chExt cx="1828800" cy="914400"/>
                      </a:xfrm>
                    </p:grpSpPr>
                    <p:sp>
                      <p:nvSpPr><p:cNvPr id="7" name="组合内文本框 6"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
                      <p:spPr>
                        <a:xfrm><a:off x="9601200" y="1143000"/><a:ext cx="1188720" cy="457200"/></a:xfrm>
                        <a:prstGeom prst="rect"><a:avLst/></a:prstGeom>
                      </p:spPr>
                      <p:txBody><a:bodyPr wrap="none"/><a:lstStyle/><a:p><a:r><a:rPr lang="zh-CN"/><a:t>组合内文字</a:t></a:r></a:p></p:txBody>
                    </p:sp>
                  </p:grpSp>
                </p:spTree>
              </p:cSld>
              <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:sld>
            """;

        // 降级用例：在 spTree 关闭前插入坏形状（此刻 XML 字符串里只有一个 </p:spTree>）
        if (withBrokenShape)
            slide2Xml = slide2Xml.Replace("</p:spTree>", brokenShapeXml + "</p:spTree>", StringComparison.Ordinal);

        WriteText(archive, "ppt/slides/slide2.xml", slide2Xml);

        WriteText(archive, "ppt/slides/_rels/slide2.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="../media/image1.png"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout" Target="../slideLayouts/slideLayout1.xml"/>
            </Relationships>
            """);

        WriteText(archive, "ppt/notesSlides/notesSlide1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <p:notes
                xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:cSld>
                <p:spTree>
                  <p:nvGrpSpPr><p:cNvPr id="1" name=""/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>
                  <p:grpSpPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="0" cy="0"/><a:chOff x="0" y="0"/><a:chExt cx="0" cy="0"/></a:xfrm></p:grpSpPr>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="备注占位符 1"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                    <p:spPr/>
                    <p:txBody>
                      <a:bodyPr/>
                      <a:lstStyle/>
                      <a:p><a:r><a:rPr lang="zh-CN"/><a:t>这是演讲者备注，含自检备注关键字</a:t></a:r></a:p>
                      <a:p><a:r><a:rPr lang="zh-CN"/><a:t>备注第二段</a:t></a:r></a:p>
                    </p:txBody>
                  </p:sp>
                </p:spTree>
              </p:cSld>
              <p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>
            </p:notes>
            """);

        WriteText(archive, "ppt/notesSlides/_rels/notesSlide1.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="../slides/slide1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="../theme/theme1.xml"/>
            </Relationships>
            """);

        // 1×1 透明 PNG（足够验证「关系 → media 字节」这条链路）
        WriteBytes(archive, "ppt/media/image1.png", s_pixelPng);
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
