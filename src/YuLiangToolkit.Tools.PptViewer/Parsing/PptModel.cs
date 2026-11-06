using System.Windows;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

// =============================================================================
// PPT 阅读器的「纯数据模型」——解析器（PptDocumentParser）负责把 .pptx 填进这些类型，
// 渲染器（Rendering/PptSlideRenderer）只读这些类型画 WPF 画面，ViewModel 也只用它们。
//
// 约定（改模型前先看这里，三个方向都靠它对齐）：
//   1. 长度单位一律是 WPF DIP（1 DIP = 1/96 英寸）；解析器用 OoxmlPackage.EmuToDip 换算。
//      幻灯片尺寸默认 16:9 = 1280 × 720 DIP，4:3 = 960 × 720 DIP。
//   2. 坐标一律是「相对幻灯片左上角」的绝对坐标，并且已经处理完组合（grpSp）的平移 / 缩放：
//      渲染器拿到 PptShape.X/Y 直接画，不需要再套父级变换。
//      （组合自身的旋转除外：PptGroupShape.RotationDegrees 由渲染器在画子形状前整体旋转。）
//   3. 颜色 / 字体 / 字号 / 项目符号都已在解析阶段解析成最终值（主题色、版式继承都算完），
//      渲染器不再查主题 —— 这样自检可以只断言解析结果。
//   4. 只读模型：界面 / 自检都不应修改这里的数据。
// =============================================================================

/// <summary>
/// 一份解析好的 PPT 演示文稿（<c>.pptx</c> / <c>.pptm</c> / <c>.ppsx</c>）。
/// <para>解析器在后台线程构造它（不包含任何 WPF 控件 / 图片句柄，只有字节数组和值类型），
/// 界面拿到后可以随意跨线程读取。</para>
/// </summary>
public sealed class PptPresentation
{
    /// <summary>源文件完整路径（打开方式 / 最近列表用；内存解析时可能为空）。</summary>
    public string FilePath { get; set; } = "";

    /// <summary>演示文稿标题（<c>docProps/core.xml</c> 的 dc:title；没有就用文件名）。</summary>
    public string Title { get; set; } = "";

    /// <summary>幻灯片宽度（DIP）。</summary>
    public double WidthDip { get; set; } = 1280;

    /// <summary>幻灯片高度（DIP）。</summary>
    public double HeightDip { get; set; } = 720;

    /// <summary>按演示顺序排好的幻灯片（<c>p:sldIdLst</c> 的顺序，不是文件名顺序）。</summary>
    public IReadOnlyList<PptSlide> Slides { get; set; } = Array.Empty<PptSlide>();

    /// <summary>主题（颜色方案 / 字体方案；解析阶段已把主题色代入各形状，这里主要给界面显示信息）。</summary>
    public PptTheme Theme { get; set; } = new();

    /// <summary>文档属性（作者 / 标题 / 创建修改时间）。</summary>
    public PptCoreProperties Properties { get; set; } = new();

    /// <summary>幻灯片张数。</summary>
    public int SlideCount => Slides.Count;

    /// <summary>是否 16:9 宽屏（宽高比 &gt; 1.5）。</summary>
    public bool IsWideScreen => HeightDip > 0 && WidthDip / HeightDip > 1.5;

    /// <summary>宽高比（宽 / 高；坏数据返回 16/9）。</summary>
    public double AspectRatio => HeightDip > 0 ? WidthDip / HeightDip : 16.0 / 9.0;
}

/// <summary>文档属性（<c>docProps/core.xml</c> + <c>docProps/app.xml</c> 的只读部分）。</summary>
public sealed class PptCoreProperties
{
    public string Title { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Author { get; set; } = "";
    public string LastModifiedBy { get; set; } = "";
    public string Company { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
    public DateTime? ModifiedUtc { get; set; }
}

/// <summary>
/// 主题数据。颜色字典的键是 OOXML 的 scheme 名（<c>dk1</c> / <c>lt1</c> / <c>accent1</c> …，
/// 全部小写）；解析器用 <see cref="GetColor"/> 兜底，不存在时返回调用方给的默认色。
/// </summary>
public sealed class PptTheme
{
    /// <summary>主题色（键：dk1 / lt1 / dk2 / lt2 / accent1~accent6 / hlink / folHlink）。</summary>
    public Dictionary<string, Color> Colors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>标题字体（majorFont）。</summary>
    public string MajorFont { get; set; } = "Microsoft YaHei";

    /// <summary>正文字体（minorFont）。</summary>
    public string MinorFont { get; set; } = "Microsoft YaHei";

    /// <summary>标题的中日韩字体（majorFont 的 &lt;a:ea&gt;；没有时为空串）。</summary>
    public string MajorEastAsianFont { get; set; } = "";

    /// <summary>正文的中日韩字体（minorFont 的 &lt;a:ea&gt;；没有时为空串）。</summary>
    public string MinorEastAsianFont { get; set; } = "";

    /// <summary>取主题色，没有这个键时返回 <paramref name="fallback"/>（不抛异常）。</summary>
    public Color GetColor(string name, Color fallback)
        => !string.IsNullOrEmpty(name) && Colors.TryGetValue(name, out var color) ? color : fallback;

    /// <summary>默认主题（Office 默认配色 / 字体），解析失败或部件缺失时的兜底。</summary>
    public static PptTheme CreateDefault()
    {
        var theme = new PptTheme();
        theme.Colors["dk1"] = Color.FromRgb(0x00, 0x00, 0x00);
        theme.Colors["lt1"] = Color.FromRgb(0xFF, 0xFF, 0xFF);
        theme.Colors["dk2"] = Color.FromRgb(0x44, 0x54, 0x6A);
        theme.Colors["lt2"] = Color.FromRgb(0xE7, 0xE6, 0xE6);
        theme.Colors["accent1"] = Color.FromRgb(0x44, 0x72, 0xC4);
        theme.Colors["accent2"] = Color.FromRgb(0xED, 0x7D, 0x31);
        theme.Colors["accent3"] = Color.FromRgb(0xA5, 0xA5, 0xA5);
        theme.Colors["accent4"] = Color.FromRgb(0xFF, 0xC0, 0x00);
        theme.Colors["accent5"] = Color.FromRgb(0x5B, 0x9B, 0xD5);
        theme.Colors["accent6"] = Color.FromRgb(0x70, 0xAD, 0x47);
        theme.Colors["hlink"] = Color.FromRgb(0x05, 0x63, 0xC1);
        theme.Colors["folHlink"] = Color.FromRgb(0x95, 0x4F, 0x72);
        return theme;
    }
}

/// <summary>一张幻灯片。</summary>
public sealed class PptSlide
{
    /// <summary>第几页（1 开始，按演示顺序）。</summary>
    public int Index { get; set; }

    /// <summary>标题（从 title / ctrTitle 占位符取第一段文字；没有标题时为空串）。</summary>
    public string Title { get; set; } = "";

    /// <summary>演讲者备注的纯文本（多条按换行拼接；没有备注时为空串）。</summary>
    public string Notes { get; set; } = "";

    /// <summary>是否隐藏幻灯片（<c>p:sld show="0"</c>；放映模式可以跳过，浏览时照常显示并标注）。</summary>
    public bool IsHidden { get; set; }

    /// <summary>幻灯片背景填充；<c>null</c> = 默认白底。</summary>
    public PptFill? Background { get; set; }

    /// <summary>页面上所有形状，按 z 序（画的时候从前往后画，后面的盖住前面的）。</summary>
    public IReadOnlyList<PptShape> Shapes { get; set; } = Array.Empty<PptShape>();

    /// <summary>
    /// 本页所有可见文字拼成的纯文本（标题 + 正文 + 表格单元格，按阅读顺序，行间换行）。
    /// 查找只搜这个 + <see cref="Notes"/>；导出 / 复制本页文字也用它。
    /// </summary>
    public string PlainText { get; set; } = "";

    /// <summary>有没有备注（界面上决定备注按钮是否可用）。</summary>
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);

    /// <summary>缩略图 / 下拉里显示的名字：「1. 标题」，没有标题就「第 1 页」。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Title) ? $"第 {Index} 页" : $"{Index}. {Title}";

    /// <summary>形状总数（含组合里的子形状）。</summary>
    public int ShapeCount => Shapes.Sum(PptShapeTree.Count);

    /// <summary>图片数量（含组合里的）。</summary>
    public int PictureCount => Shapes.Sum(shape => PptShapeTree.CountOf<PptPictureShape>(shape));

    /// <summary>表格数量（含组合里的）。</summary>
    public int TableCount => Shapes.Sum(shape => PptShapeTree.CountOf<PptTableShape>(shape));
}

/// <summary>形状树遍历的小工具（模型自带的纯函数，自检也直接断言）。</summary>
public static class PptShapeTree
{
    /// <summary>这个形状 + 它所有子孙的数量。</summary>
    public static int Count(PptShape shape)
    {
        var count = 1;
        if (shape is PptGroupShape group)
        {
            foreach (var child in group.Children) count += Count(child);
        }

        return count;
    }

    /// <summary>这个形状 + 它所有子孙里指定类型的数量。</summary>
    public static int CountOf<T>(PptShape shape) where T : PptShape
    {
        var count = shape is T ? 1 : 0;
        if (shape is PptGroupShape group)
        {
            foreach (var child in group.Children) count += CountOf<T>(child);
        }

        return count;
    }

    /// <summary>把这个形状（组合则递归展开）按 z 序摊平成叶子形状列表（组合自身不出现）。</summary>
    public static void Flatten(PptShape shape, List<PptShape> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (shape is PptGroupShape group)
        {
            foreach (var child in group.Children) Flatten(child, output);
            return;
        }

        output.Add(shape);
    }
}

/// <summary>形状基类。所有坐标都是幻灯片坐标（DIP），组合已经在解析阶段展开过平移 / 缩放。</summary>
public abstract class PptShape
{
    /// <summary>形状名（<c>p:cNvPr name</c>；没有时为空串）。</summary>
    public string Name { get; set; } = "";

    /// <summary>形状 Id（<c>p:cNvPr id</c>；调试 / 自检用，没有时 0）。</summary>
    public int ShapeId { get; set; }

    /// <summary>左上角 X（幻灯片坐标，DIP）。</summary>
    public double X { get; set; }

    /// <summary>左上角 Y（幻灯片坐标，DIP）。</summary>
    public double Y { get; set; }

    /// <summary>宽度（DIP）。</summary>
    public double Width { get; set; }

    /// <summary>高度（DIP）。</summary>
    public double Height { get; set; }

    /// <summary>顺时针旋转角度（度，0 ~ 360）。</summary>
    public double RotationDegrees { get; set; }

    /// <summary>水平翻转。</summary>
    public bool FlipH { get; set; }

    /// <summary>垂直翻转。</summary>
    public bool FlipV { get; set; }

    /// <summary>是不是占位符（p:ph）。</summary>
    public bool IsPlaceholder { get; set; }

    /// <summary>占位符类型（title / body / ctrTitle / subTitle / pic / tbl / chart…；不是占位符为空串）。</summary>
    public string PlaceholderType { get; set; } = "";

    /// <summary>形状的最终包围盒（DIP）——渲染器 / 自检的快捷属性。</summary>
    public Rect Bounds => new(X, Y, Width, Height);
}

/// <summary>带文字的文本框 / 自选图形 / 占位符。</summary>
public sealed class PptTextShape : PptShape
{
    /// <summary>文字内容（段落 + run）。</summary>
    public PptTextBody Text { get; set; } = new();

    /// <summary>填充；<c>null</c> = 不填充（透明）。</summary>
    public PptFill? Fill { get; set; }

    /// <summary>边框；<c>null</c> = 无边框。</summary>
    public PptLine? Line { get; set; }

    /// <summary>垂直对齐（上 / 中 / 下）。</summary>
    public PptVerticalAnchor VerticalAnchor { get; set; } = PptVerticalAnchor.Top;

    /// <summary>文字与文本框之间的内边距（DIP，默认值 = PowerPoint 默认 0.1 英寸 / 0.05 英寸）。</summary>
    public double InsetLeftDip { get; set; } = 9.6;
    public double InsetRightDip { get; set; } = 9.6;
    public double InsetTopDip { get; set; } = 4.8;
    public double InsetBottomDip { get; set; } = 4.8;

    /// <summary>文字超出文本框时是否换行（<c>bodyPr wrap="none"</c> 时为 false）。</summary>
    public bool WrapText { get; set; } = true;

    /// <summary>自动调整策略（<c>bodyPr/normAutofit</c> = 缩小文字，<c>spAutoFit</c> = 调整形状）。</summary>
    public PptAutoFit AutoFit { get; set; } = PptAutoFit.None;
}

/// <summary>图片。图片字节直接存在模型里（解析阶段从 zip 取出），渲染器负责缓存解码。</summary>
public sealed class PptPictureShape : PptShape
{
    /// <summary>图片原始字节（PNG / JPEG / GIF / BMP…）；取不到时为 null（渲染占位框）。</summary>
    public byte[]? ImageBytes { get; set; }

    /// <summary>图片内容类型（image/png 等；不需要也可为空）。</summary>
    public string ContentType { get; set; } = "";

    /// <summary>替代文字（<c>p:cNvPr descr</c>；自检 / 悬停提示用）。</summary>
    public string AltText { get; set; } = "";

    /// <summary>点击图片要打开的超链接（http/https 等；没有时 null）。</summary>
    public string? Hyperlink { get; set; }

    /// <summary>裁剪比例（0 ~ 1，表示从这一边裁掉多少；<c>srcRect</c>）。</summary>
    public double CropLeft { get; set; }
    public double CropTop { get; set; }
    public double CropRight { get; set; }
    public double CropBottom { get; set; }
}

/// <summary>表格（<c>a:tbl</c>）。</summary>
public sealed class PptTableShape : PptShape
{
    /// <summary>列宽（DIP，长度 = 列数）。</summary>
    public IReadOnlyList<double> ColumnWidthsDip { get; set; } = Array.Empty<double>();

    /// <summary>行（每行有高度和单元格）。</summary>
    public IReadOnlyList<PptTableRow> Rows { get; set; } = Array.Empty<PptTableRow>();
}

/// <summary>表格行。</summary>
public sealed class PptTableRow
{
    /// <summary>行高（DIP）。</summary>
    public double HeightDip { get; set; }

    /// <summary>单元格（从左到右）。</summary>
    public IReadOnlyList<PptTableCell> Cells { get; set; } = Array.Empty<PptTableCell>();
}

/// <summary>表格单元格（含文字与填充色）。</summary>
public sealed class PptTableCell
{
    /// <summary>单元格文字；空单元格为 null。</summary>
    public PptTextBody? Text { get; set; }

    /// <summary>单元格填充色；<c>null</c> = 透明。</summary>
    public PptFill? Fill { get; set; }

    /// <summary>横向跨列数（<c>gridSpan</c>，默认 1）。</summary>
    public int ColSpan { get; set; } = 1;

    /// <summary>纵向跨行数（<c>rowSpan</c>，默认 1）。</summary>
    public int RowSpan { get; set; } = 1;

    /// <summary>这个格子是不是被左 / 上方的合并单元格盖住了（渲染时跳过，但保留占位）。</summary>
    public bool IsCovered { get; set; }

    /// <summary>垂直对齐（默认居中，和 PowerPoint 一致）。</summary>
    public PptVerticalAnchor VerticalAnchor { get; set; } = PptVerticalAnchor.Middle;
}

/// <summary>自选图形（矩形 / 圆角矩形 / 椭圆 / 直线 / 箭头 / 三角形…）。</summary>
public sealed class PptGeometryShape : PptShape
{
    /// <summary>几何类型（渲染器只覆盖常用几种，其余画成矩形）。</summary>
    public PptGeometryKind Kind { get; set; } = PptGeometryKind.Rectangle;

    /// <summary>填充；<c>null</c> = 不填充。</summary>
    public PptFill? Fill { get; set; }

    /// <summary>边框；<c>null</c> = 无边框。</summary>
    public PptLine? Line { get; set; }
}

/// <summary>组合（<c>p:grpSp</c>）。子形状坐标已换算成幻灯片坐标；组合自身的旋转由渲染器整体处理。</summary>
public sealed class PptGroupShape : PptShape
{
    /// <summary>子形状（z 序）。</summary>
    public IReadOnlyList<PptShape> Children { get; set; } = Array.Empty<PptShape>();
}

/// <summary>
/// 不支持 / 只占位的形状（图表 / SmartArt / 媒体 / OLE 对象…）：
/// 渲染器画一个浅灰虚线框 + 类型名，保证「打不开的内容也看得见位置」，不会整页空白或崩掉。
/// </summary>
public sealed class PptUnsupportedShape : PptShape
{
    /// <summary>给用户看的类型说明（例如「图表」「SmartArt」「媒体对象」）。</summary>
    public string KindLabel { get; set; } = "";
}

/// <summary>文本块（一个文本框 / 单元格里的全部段落）。</summary>
public sealed class PptTextBody
{
    /// <summary>段落（按顺序）。</summary>
    public IReadOnlyList<PptParagraph> Paragraphs { get; set; } = Array.Empty<PptParagraph>();

    /// <summary>所有段落的纯文本（段间换行）。</summary>
    public string Text => string.Join(Environment.NewLine, Paragraphs.Select(paragraph => paragraph.Text));

    /// <summary>有没有可见文字（全空白视为没有）。</summary>
    public bool HasText => Paragraphs.Any(paragraph => !string.IsNullOrWhiteSpace(paragraph.Text));
}

/// <summary>段落（一段文字 + 项目符号 / 对齐 / 缩进）。</summary>
public sealed class PptParagraph
{
    /// <summary>对齐："l" 左 / "ctr" 居中 / "r" 右 / "just" 两端 / "dist" 分散（默认 "l"）。</summary>
    public string Alignment { get; set; } = "l";

    /// <summary>大纲级别 0 ~ 8（0 = 一级）。</summary>
    public int Level { get; set; }

    /// <summary>左边缩进（DIP，已经包含级别缩进；项目符号画在它左边）。</summary>
    public double IndentDip { get; set; }

    /// <summary>是否显示项目符号（默认 true；标题一般没有）。</summary>
    public bool Bullet { get; set; }

    /// <summary>项目符号字符（默认圆点 •）。</summary>
    public string BulletChar { get; set; } = "•";

    /// <summary>是不是自动编号（1. 2. 3.；此时 <see cref="BulletChar"/> 不用）。</summary>
    public bool NumberedBullet { get; set; }

    /// <summary>段前距（DIP；null = 默认）。</summary>
    public double? SpaceBeforeDip { get; set; }

    /// <summary>段后距（DIP；null = 默认）。</summary>
    public double? SpaceAfterDip { get; set; }

    /// <summary>行距倍数（1.0 = 单倍；0 或负数按 1.0 处理）。</summary>
    public double LineSpacing { get; set; } = 1.0;

    /// <summary>文字片段（一个段落里可以有不同格式的 run）。</summary>
    public IReadOnlyList<PptTextRun> Runs { get; set; } = Array.Empty<PptTextRun>();

    /// <summary>段落纯文本（run 拼接；软换行 <c>a:br</c> 用 \n 表示）。</summary>
    public string Text => string.Concat(Runs.Select(run => run.Text));
}

/// <summary>一段格式一致的文字（run）。</summary>
public sealed class PptTextRun
{
    /// <summary>文字（可能含 \n = 软换行）。</summary>
    public string Text { get; set; } = "";

    /// <summary>字体名（已解析主题字体；空串 = 用默认字体）。</summary>
    public string FontFamily { get; set; } = "";

    /// <summary>字号（DIP；例如 18 磅 = 24 DIP）。</summary>
    public double FontSizeDip { get; set; } = 24;

    /// <summary>粗体。</summary>
    public bool Bold { get; set; }

    /// <summary>斜体。</summary>
    public bool Italic { get; set; }

    /// <summary>下划线。</summary>
    public bool Underline { get; set; }

    /// <summary>删除线。</summary>
    public bool Strike { get; set; }

    /// <summary>文字颜色（已解析，默认近黑）。</summary>
    public Color Color { get; set; } = Color.FromRgb(0x26, 0x26, 0x26);

    /// <summary>上标。</summary>
    public bool IsSuperscript { get; set; }

    /// <summary>下标。</summary>
    public bool IsSubscript { get; set; }

    /// <summary>超链接（http/https…；没有时 null）。</summary>
    public string? Hyperlink { get; set; }
}

/// <summary>填充（纯色 / 双色渐变 / 图片）。</summary>
public sealed class PptFill
{
    /// <summary>填充类型。</summary>
    public PptFillKind Kind { get; set; } = PptFillKind.Solid;

    /// <summary>主色（纯色 / 渐变起点）。</summary>
    public Color Color { get; set; } = Colors.White;

    /// <summary>渐变终点色（<see cref="PptFillKind.Gradient"/> 时有效）。</summary>
    public Color Color2 { get; set; } = Colors.White;

    /// <summary>渐变角度（度，0 = 从左到右；默认 90 = 从上到下）。</summary>
    public double GradientAngleDegrees { get; set; } = 90;

    /// <summary>图片填充的原始字节（<see cref="PptFillKind.Picture"/> 时有效）。</summary>
    public byte[]? PictureBytes { get; set; }

    /// <summary>图片填充的内容类型。</summary>
    public string PictureContentType { get; set; } = "";

    /// <summary>造一个纯色填充。</summary>
    public static PptFill Solid(Color color) => new() { Kind = PptFillKind.Solid, Color = color, Color2 = color };
}

/// <summary>线条（边框）。</summary>
public sealed class PptLine
{
    /// <summary>颜色。</summary>
    public Color Color { get; set; } = Color.FromRgb(0x33, 0x33, 0x33);

    /// <summary>线宽（DIP）。</summary>
    public double WidthDip { get; set; } = 1;

    /// <summary>虚线样式（null = 实线；其它值见 <c>a:prstDash</c>，例如 dash / dot / dashDot / lgDash）。</summary>
    public string? Dash { get; set; }
}

/// <summary>填充类型。</summary>
public enum PptFillKind
{
    /// <summary>不填充（一般不会出现在 PptFill 里——需要「无填充」时用 null）。</summary>
    None,

    /// <summary>纯色。</summary>
    Solid,

    /// <summary>双色渐变（多于两个色标时只取首尾两个）。</summary>
    Gradient,

    /// <summary>图片填充。</summary>
    Picture,
}

/// <summary>文本框垂直对齐。</summary>
public enum PptVerticalAnchor
{
    /// <summary>顶部对齐（默认）。</summary>
    Top,

    /// <summary>垂直居中。</summary>
    Middle,

    /// <summary>底部对齐。</summary>
    Bottom,
}

/// <summary>文字自动调整策略。</summary>
public enum PptAutoFit
{
    /// <summary>不调整。</summary>
    None,

    /// <summary>缩小文字以塞进文本框。</summary>
    ShrinkText,

    /// <summary>调整形状高度（本阅读器只做「按内容缩小」的近似，不改用户看到的文本框大小）。</summary>
    ResizeShape,
}

/// <summary>自选图形几何类型（只覆盖常用几种；其余按矩形画）。</summary>
public enum PptGeometryKind
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
    Line,
    Arrow,
    Triangle,
    Diamond,
    Parallelogram,
    Trapezoid,
    Pentagon,
    Hexagon,
    Star,
    Unknown,
}

/// <summary>
/// 解析器 / 渲染器共用的一些默认值（PowerPoint 默认字号 / 字体 / 内边距 / 项目符号缩进）。
/// 集中在这里，避免两边各写一套魔法数字。
/// </summary>
public static class PptDefaults
{
    /// <summary>默认字体（主题缺字体时用；微软雅黑对中文演示文稿最稳）。</summary>
    public const string FontFamily = "Microsoft YaHei";

    /// <summary>默认正文字号（18 磅 = 24 DIP）。</summary>
    public const double BodyFontSizeDip = 24;

    /// <summary>默认标题字号（44 磅 ≈ 58.67 DIP）。</summary>
    public const double TitleFontSizeDip = 44 * 96.0 / 72.0;

    /// <summary>每级大纲的默认缩进（DIP，约 0.3 英寸）。</summary>
    public const double LevelIndentDip = 28.8;

    /// <summary>默认项目符号字符。</summary>
    public const string BulletChar = "•";

    /// <summary>默认幻灯片宽高（16:9，1280 × 720 DIP）。</summary>
    public const double DefaultWidthDip = 1280;

    /// <summary>默认幻灯片高宽（16:9，1280 × 720 DIP）。</summary>
    public const double DefaultHeightDip = 720;
}
