using System.Windows.Media;
using System.Xml.Linq;
using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

/// <summary>
/// 版式 / 母版里一个占位符的「可继承信息」：占位符类型 / idx、位置（<c>a:xfrm</c>）、
/// 填充与边框（幻灯片上的占位符没写时继承）。
/// </summary>
internal sealed class PptPlaceholderInfo
{
    /// <summary>占位符类型（<c>p:ph type</c>；没写时为空串）。</summary>
    public string Type { get; init; } = "";

    /// <summary>占位符编号（<c>p:ph idx</c>；没写时为 null）。</summary>
    public int? Index { get; init; }

    /// <summary>位置（EMU 的 <c>a:xfrm</c> 元素；没写时为 null）。</summary>
    public XElement? Xfrm { get; init; }

    /// <summary>填充（版式 / 母版占位符显式写了填充时有效）。</summary>
    public PptFill? Fill { get; init; }

    /// <summary>显式写了填充元素（包括 <c>a:noFill</c>）—— 用来区分「没写」和「明确不填充」。</summary>
    public bool HasFill { get; init; }

    /// <summary>边框（显式写了 <c>a:ln</c> 时有效）。</summary>
    public PptLine? Line { get; init; }

    /// <summary>显式写了边框元素。</summary>
    public bool HasLine { get; init; }
}

/// <summary>
/// 组合（<c>p:grpSp</c>）的坐标变换：把子形状坐标从「组合子坐标系」换算到幻灯片坐标。
/// <para>公式：<c>x_幻灯片 = OffsetX + x_child * ScaleX</c>；缩放比例 = 组合 ext / chExt，
/// 子形状绝对坐标 = 组合位置 + (子形状位置 - chOff) × (ext / chExt)。多层组合时逐层复合。</para>
/// </summary>
internal readonly record struct PptShapeTransform(double OffsetX, double OffsetY, double ScaleX, double ScaleY)
{
    /// <summary>恒等变换（顶层形状）。</summary>
    public static readonly PptShapeTransform Identity = new(0, 0, 1, 1);

    /// <summary>换算 X 坐标。</summary>
    public double X(double value) => OffsetX + value * ScaleX;

    /// <summary>换算 Y 坐标。</summary>
    public double Y(double value) => OffsetY + value * ScaleY;
}

/// <summary>
/// 解析一张幻灯片 / 版式 / 母版形状树所需的环境（部件关系表、主题、版式与母版继承信息）。
/// </summary>
internal sealed class PptShapeContext
{
    /// <summary>图片字节缓存（整份演示文稿共享）。</summary>
    public required PptImageCache Images { get; init; }

    /// <summary>主题数据。</summary>
    public required PptThemeData ThemeData { get; init; }

    /// <summary>颜色上下文（主题 + clrMap，已代入幻灯片级 <c>p:clrMapOvr</c>）。</summary>
    public required PptColorContext Colors { get; init; }

    /// <summary>当前部件（幻灯片 / 版式 / 母版）自己的关系表。</summary>
    public required IReadOnlyDictionary<string, OoxmlRelationship> Relationships { get; init; }

    /// <summary>版式信息（占位符位置 / 背景继承）；没有版式时为 null。</summary>
    public PptLayoutInfo? Layout { get; init; }

    /// <summary>母版信息（占位符 / txStyles / 背景继承）；没有母版时为 null。</summary>
    public PptMasterInfo? Master { get; init; }

    /// <summary>母版的文字大纲样式（titleStyle / bodyStyle / otherStyle）。</summary>
    public PptMasterStyles? MasterStyles { get; init; }

    /// <summary>幻灯片宽度（DIP，兜底用）。</summary>
    public double SlideWidthDip { get; init; } = PptDefaults.DefaultWidthDip;

    /// <summary>幻灯片高度（DIP，兜底用）。</summary>
    public double SlideHeightDip { get; init; } = PptDefaults.DefaultHeightDip;
}

/// <summary>
/// 形状树解析：<c>p:spTree</c> → <see cref="PptShape"/> 列表。
/// <para>覆盖 <c>p:sp</c>（文字 / 自选图形）、<c>p:pic</c>（图片 + 裁剪 + 超链接）、
/// <c>p:graphicFrame</c>（表格；图表 / SmartArt 用占位形状）、<c>p:grpSp</c>（组合，坐标已展开）、
/// <c>p:cxnSp</c>（连接线）、<c>mc:AlternateContent</c>（取 Choice / Fallback）。</para>
/// <para>单个形状解析异常会被调用方 try/catch 跳过，不影响整页；图片字节通过
/// <see cref="PptImageCache"/> 去重。填充 / 主题色 / 版式与母版继承都在这里解析成最终值。</para>
/// </summary>
internal static class PptShapeParser
{
    /// <summary>单页形状安全上限（防畸形文件把渲染时间吃满）。</summary>
    private const int MaxShapesPerSlide = 3000;

    /// <summary>组合递归深度上限（正常演示文稿不超过 3 层）。</summary>
    private const int MaxGroupDepth = 12;

    /// <summary>解析形状树（按 XML 文档顺序 = z 序）。</summary>
    public static List<PptShape> ParseShapeTree(XElement? shapeTree, PptShapeContext context)
    {
        var shapes = new List<PptShape>();
        if (shapeTree is null) return shapes;

        ParseShapeChildren(shapeTree, context, PptShapeTransform.Identity, shapes, depth: 0);
        return shapes;
    }

    /// <summary>
    /// 解析背景（<c>p:cSld/p:bg</c>）：<c>p:bgPr</c> 里的填充，或 <c>p:bgRef</c> 引用的主题背景填充样式。
    /// 没有背景返回 null（渲染为白底）。
    /// </summary>
    public static PptFill? ParseBackground(
        XElement? commonSlideData,
        PptColorContext colors,
        PptImageCache images,
        IReadOnlyDictionary<string, OoxmlRelationship> relationships)
    {
        var background = commonSlideData?.Element(PptXml.P + "bg");
        if (background is null) return null;

        var backgroundProperties = background.Element(PptXml.P + "bgPr");
        if (backgroundProperties is not null) return ParseFill(backgroundProperties, colors, images, relationships);

        // bgRef idx：1 ~ 999 引用 fillStyleLst，1001 起引用 bgFillStyleLst
        var backgroundReference = background.Element(PptXml.P + "bgRef");
        if (backgroundReference is null) return null;

        var index = PptXml.IntAttr(backgroundReference, "idx") ?? 0;
        XElement? style = null;
        if (index >= 1001)
        {
            var styleIndex = index - 1001;
            if (styleIndex >= 0 && styleIndex < colors.ThemeData.BackgroundFillStyles.Count)
                style = colors.ThemeData.BackgroundFillStyles[styleIndex];
        }
        else if (index > 0)
        {
            var styleIndex = index - 1;
            if (styleIndex >= 0 && styleIndex < colors.ThemeData.FillStyles.Count)
                style = colors.ThemeData.FillStyles[styleIndex];
        }

        if (style is null) return null;

        var referenceColor = backgroundReference.Elements().FirstOrDefault();
        var styleContext = referenceColor is not null
            ? colors.WithPlaceholder(colors.Resolve(referenceColor, colors.PlaceholderColor))
            : colors;
        return ParseFillElement(style, styleContext, images, relationships);
    }

    /// <summary>
    /// 从属性容器（<c>p:spPr</c> / <c>p:bgPr</c> / <c>a:tcPr</c> …）里解析填充；
    /// 没有填充元素（或 <c>a:noFill</c>）返回 null。
    /// </summary>
    public static PptFill? ParseFill(
        XElement? container,
        PptColorContext colors,
        PptImageCache? images = null,
        IReadOnlyDictionary<string, OoxmlRelationship>? relationships = null)
    {
        if (container is null) return null;
        var fillElement = container.Elements().FirstOrDefault(IsFillElement);
        return ParseFillElement(fillElement, colors, images, relationships);
    }

    /// <summary>容器里有没有显式填充元素（<c>a:noFill</c> 也算「写了」）。</summary>
    public static bool HasFillElement(XElement? container)
        => container?.Elements().Any(IsFillElement) ?? false;

    /// <summary>解析 <c>a:ln</c> 边框；<c>a:noFill</c> 或元素缺失返回 null。</summary>
    public static PptLine? ParseLine(XElement? lineElement, PptColorContext colors)
    {
        if (lineElement is null) return null;
        if (lineElement.Element(PptXml.A + "noFill") is not null) return null;

        var line = new PptLine();

        var width = PptXml.LongAttr(lineElement, "w");
        if (width is > 0) line.WidthDip = OoxmlPackage.EmuToDip(width.Value);

        // 边框颜色：solidFill 为主；渐变线取第一个色标
        var colorElement = lineElement.Element(PptXml.A + "solidFill")?.Elements().FirstOrDefault()
            ?? lineElement.Element(PptXml.A + "gradFill")
                ?.Element(PptXml.A + "gsLst")?.Elements(PptXml.A + "gs")
                .FirstOrDefault()?.Elements().FirstOrDefault();
        if (colorElement is not null) line.Color = colors.Resolve(colorElement, line.Color);

        var dash = PptXml.Attr(lineElement.Element(PptXml.A + "prstDash"), "val");
        if (dash.Length > 0 && !string.Equals(dash, "solid", StringComparison.OrdinalIgnoreCase))
            line.Dash = dash;

        return line;
    }

    /// <summary>
    /// 读版式 / 母版里的一个占位符（<c>p:sp</c> 的 <c>p:ph</c>）信息；不是占位符返回 null。
    /// </summary>
    public static PptPlaceholderInfo? ReadPlaceholder(
        XElement shapeElement,
        PptColorContext colors,
        IReadOnlyDictionary<string, OoxmlRelationship> relationships,
        PptImageCache images)
    {
        var placeholder = shapeElement.Element(PptXml.P + "nvSpPr")
            ?.Element(PptXml.P + "nvPr")?.Element(PptXml.P + "ph");
        if (placeholder is null) return null;

        var shapeProperties = shapeElement.Element(PptXml.P + "spPr");
        var hasFill = HasFillElement(shapeProperties);
        var lineElement = shapeProperties?.Element(PptXml.A + "ln");

        return new PptPlaceholderInfo
        {
            Type = PptXml.Attr(placeholder, "type"),
            Index = PptXml.IntAttr(placeholder, "idx"),
            Xfrm = shapeProperties?.Element(PptXml.A + "xfrm"),
            Fill = hasFill ? ParseFill(shapeProperties, colors, images, relationships) : null,
            HasFill = hasFill,
            Line = lineElement is not null ? ParseLine(lineElement, colors) : null,
            HasLine = lineElement is not null,
        };
    }

    /// <summary>
    /// 在版式 / 母版占位符列表里找与给定 <paramref name="type"/> / <paramref name="index"/> 匹配的一项。
    /// <para>匹配优先级：类型 + idx 完全相同 → idx 相同 → 类型相同（优先 idx 也为空的那项）→ null。</para>
    /// </summary>
    public static PptPlaceholderInfo? MatchPlaceholder(
        IReadOnlyList<PptPlaceholderInfo>? placeholders,
        string? type,
        int? index)
    {
        if (placeholders is null || placeholders.Count == 0) return null;
        var normalizedType = type?.Trim() ?? "";

        if (index.HasValue)
        {
            var sameTypeAndIndex = placeholders.FirstOrDefault(candidate =>
                candidate.Index == index.Value && SameType(candidate.Type, normalizedType));
            if (sameTypeAndIndex is not null) return sameTypeAndIndex;

            var sameIndex = placeholders.FirstOrDefault(candidate => candidate.Index == index.Value);
            if (sameIndex is not null) return sameIndex;
        }

        if (normalizedType.Length > 0)
        {
            var sameType = placeholders.Where(candidate => SameType(candidate.Type, normalizedType)).ToList();
            if (sameType.Count > 0)
            {
                // 优先找 idx 也为空的那项（标题 / 副标题占位符通常都没有 idx）
                return sameType.FirstOrDefault(candidate => candidate.Index is null) ?? sameType[0];
            }
        }

        // 类型 / idx 都没写（极少见）：匹配第一项
        if (normalizedType.Length == 0 && !index.HasValue) return placeholders[0];
        return null;
    }

    // ---------------------------------------------------------------- 形状分发

    private static void ParseShapeChildren(
        XElement container,
        PptShapeContext context,
        PptShapeTransform transform,
        List<PptShape> target,
        int depth)
    {
        if (depth > MaxGroupDepth) return;

        foreach (var element in container.Elements())
        {
            if (target.Count >= MaxShapesPerSlide) break;

            var localName = element.Name.LocalName;
            if (localName is "nvGrpSpPr" or "grpSpPr" or "extLst") continue;

            PptShape? shape;
            try
            {
                shape = ParseShapeElement(element, context, transform, depth);
            }
            catch
            {
                // 单个形状解析异常：跳过它，其它形状照常显示
                shape = null;
            }

            if (shape is not null) target.Add(shape);
        }
    }

    private static PptShape? ParseShapeElement(
        XElement element,
        PptShapeContext context,
        PptShapeTransform transform,
        int depth)
    {
        return element.Name.LocalName switch
        {
            "sp" => ParseAutoShape(element, context, transform),
            "pic" => ParsePicture(element, context, transform),
            "graphicFrame" => ParseGraphicFrame(element, context, transform),
            "grpSp" => ParseGroup(element, context, transform, depth),
            "cxnSp" => ParseConnector(element, context, transform),
            "AlternateContent" => ParseAlternateContent(element, context, transform, depth),
            _ => null,
        };
    }

    /// <summary><c>mc:AlternateContent</c>：取 <c>mc:Choice</c> 的第一个子元素；没有就取 <c>mc:Fallback</c>。</summary>
    private static PptShape? ParseAlternateContent(
        XElement element,
        PptShapeContext context,
        PptShapeTransform transform,
        int depth)
    {
        var choice = element.Element(PptXml.MC + "Choice");
        var content = choice?.Elements().FirstOrDefault()
            ?? element.Element(PptXml.MC + "Fallback")?.Elements().FirstOrDefault();
        return content is null ? null : ParseShapeElement(content, context, transform, depth);
    }

    // ---------------------------------------------------------------- p:sp

    private static PptShape? ParseAutoShape(XElement element, PptShapeContext context, PptShapeTransform transform)
    {
        var nonVisual = element.Element(PptXml.P + "nvSpPr");
        var properties = nonVisual?.Element(PptXml.P + "cNvPr");
        var placeholder = nonVisual?.Element(PptXml.P + "nvPr")?.Element(PptXml.P + "ph");
        var shapeProperties = element.Element(PptXml.P + "spPr");
        var textBodyElement = element.Element(PptXml.P + "txBody");

        var placeholderType = PptXml.Attr(placeholder, "type");
        var placeholderIndex = PptXml.IntAttr(placeholder, "idx");

        // 位置：自己写了 a:xfrm 用自己的；占位符没写就继承版式 → 母版的同名占位符
        var transformElement = shapeProperties?.Element(PptXml.A + "xfrm")
            ?? ResolvePlaceholderXfrm(context, placeholderType, placeholderIndex);

        // 填充 / 边框：自己写了优先；占位符没写继承版式 → 母版；最后用 p:style 引用的主题样式兜底
        var fill = ParseFill(shapeProperties, context.Colors, context.Images, context.Relationships);
        if (fill is null && !HasFillElement(shapeProperties) && placeholder is not null)
            fill = ResolvePlaceholderFill(context, placeholderType, placeholderIndex);
        if (fill is null && !HasFillElement(shapeProperties))
            fill = ResolveStyleFill(element.Element(PptXml.P + "style"), context);

        var line = ParseLine(shapeProperties?.Element(PptXml.A + "ln"), context.Colors);
        var hasLineElement = shapeProperties?.Element(PptXml.A + "ln") is not null;
        if (line is null && !hasLineElement && placeholder is not null)
            line = ResolvePlaceholderLine(context, placeholderType, placeholderIndex);
        if (line is null && !hasLineElement)
            line = ResolveStyleLine(element.Element(PptXml.P + "style"), context);

        var geometryKind = ReadGeometryKind(shapeProperties);
        var hasText = HasVisibleText(textBodyElement);

        PptShape shape;
        if (hasText || geometryKind == PptGeometryKind.Unknown)
        {
            var textShape = new PptTextShape
            {
                Fill = fill,
                Line = line,
                Text = PptTextParser.ParseTextBody(
                    textBodyElement,
                    PptTextParser.CreateContext(
                        context.ThemeData,
                        context.Colors,
                        context.Relationships,
                        context.MasterStyles,
                        PptTextParser.CategoryOfPlaceholder(placeholder is null ? null : placeholderType))),
            };

            ApplyBodyProperties(textShape, textBodyElement);
            shape = textShape;
        }
        else
        {
            shape = new PptGeometryShape
            {
                Kind = geometryKind,
                Fill = fill,
                Line = line,
            };
        }

        ApplyCommonProperties(shape, properties, placeholder);
        ApplyTransform(shape, transformElement, transform);
        return shape;
    }

    /// <summary>读自选图形的几何类型：<c>a:prstGeom prst</c>；<c>a:custGeom</c> 记 Unknown。</summary>
    private static PptGeometryKind ReadGeometryKind(XElement? shapeProperties)
    {
        if (shapeProperties is null) return PptGeometryKind.Unknown;
        if (shapeProperties.Element(PptXml.A + "custGeom") is not null) return PptGeometryKind.Unknown;
        var preset = PptXml.Attr(shapeProperties.Element(PptXml.A + "prstGeom"), "prst");
        return PptXml.MapGeometry(preset);
    }

    /// <summary>应用 <c>a:bodyPr</c>：垂直对齐 / 内边距 / 自动换行 / 自动调整。</summary>
    private static void ApplyBodyProperties(PptTextShape shape, XElement? textBodyElement)
    {
        var bodyProperties = textBodyElement?.Element(PptXml.A + "bodyPr");
        if (bodyProperties is null) return;

        shape.VerticalAnchor = PptXml.Attr(bodyProperties, "anchor").ToLowerInvariant() switch
        {
            "ctr" => PptVerticalAnchor.Middle,
            "b" => PptVerticalAnchor.Bottom,
            _ => PptVerticalAnchor.Top,
        };

        var leftInset = PptXml.LongAttr(bodyProperties, "lIns");
        if (leftInset.HasValue) shape.InsetLeftDip = Math.Max(0, OoxmlPackage.EmuToDip(leftInset.Value));
        var rightInset = PptXml.LongAttr(bodyProperties, "rIns");
        if (rightInset.HasValue) shape.InsetRightDip = Math.Max(0, OoxmlPackage.EmuToDip(rightInset.Value));
        var topInset = PptXml.LongAttr(bodyProperties, "tIns");
        if (topInset.HasValue) shape.InsetTopDip = Math.Max(0, OoxmlPackage.EmuToDip(topInset.Value));
        var bottomInset = PptXml.LongAttr(bodyProperties, "bIns");
        if (bottomInset.HasValue) shape.InsetBottomDip = Math.Max(0, OoxmlPackage.EmuToDip(bottomInset.Value));

        shape.WrapText = !string.Equals(PptXml.Attr(bodyProperties, "wrap"), "none", StringComparison.OrdinalIgnoreCase);

        if (bodyProperties.Element(PptXml.A + "normAutofit") is not null) shape.AutoFit = PptAutoFit.ShrinkText;
        else if (bodyProperties.Element(PptXml.A + "spAutoFit") is not null) shape.AutoFit = PptAutoFit.ResizeShape;
        else shape.AutoFit = PptAutoFit.None;
    }

    // ---------------------------------------------------------------- p:pic

    private static PptShape? ParsePicture(XElement element, PptShapeContext context, PptShapeTransform transform)
    {
        var nonVisual = element.Element(PptXml.P + "nvPicPr");
        var properties = nonVisual?.Element(PptXml.P + "cNvPr");
        var placeholder = nonVisual?.Element(PptXml.P + "nvPr")?.Element(PptXml.P + "ph");
        var shapeProperties = element.Element(PptXml.P + "spPr");
        var blipFill = element.Element(PptXml.P + "blipFill");
        var blip = blipFill?.Element(PptXml.A + "blip");

        var picture = new PptPictureShape
        {
            AltText = PptXml.Attr(properties, "descr"),
            Hyperlink = ResolvePictureHyperlink(properties, blipFill, context.Relationships),
        };

        // 图片字节：r:embed → 关系表 → 包内部件
        var embedId = PptXml.Attr(blip, PptXml.R, "embed");
        if (embedId.Length == 0) embedId = PptXml.Attr(blip, PptXml.R, "link");
        if (embedId.Length > 0
            && context.Relationships.TryGetValue(embedId, out var relationship)
            && !relationship.IsExternal)
        {
            var image = context.Images.Read(relationship.Target);
            if (image is not null)
            {
                picture.ImageBytes = image.Bytes;
                picture.ContentType = image.ContentType;
            }
        }

        // 裁剪（srcRect 的 l / t / r / b 单位是 1/1000 百分比，100000 = 100%）
        var sourceRectangle = blipFill?.Element(PptXml.A + "srcRect");
        if (sourceRectangle is not null)
        {
            picture.CropLeft = ReadCropRatio(sourceRectangle, "l");
            picture.CropTop = ReadCropRatio(sourceRectangle, "t");
            picture.CropRight = ReadCropRatio(sourceRectangle, "r");
            picture.CropBottom = ReadCropRatio(sourceRectangle, "b");
        }

        var placeholderType = PptXml.Attr(placeholder, "type");
        var placeholderIndex = PptXml.IntAttr(placeholder, "idx");
        var transformElement = shapeProperties?.Element(PptXml.A + "xfrm")
            ?? ResolvePlaceholderXfrm(context, placeholderType, placeholderIndex);

        ApplyCommonProperties(picture, properties, placeholder);
        ApplyTransform(picture, transformElement, transform);
        return picture;
    }

    /// <summary>图片超链接：优先 <c>p:cNvPr/a:hlinkClick</c>，其次 <c>p:blipFill</c> 里的超链接。</summary>
    private static string? ResolvePictureHyperlink(
        XElement? nonVisualProperties,
        XElement? blipFill,
        IReadOnlyDictionary<string, OoxmlRelationship> relationships)
    {
        var fromProperties = PptTextParser.ResolveHyperlink(nonVisualProperties, relationships);
        if (fromProperties is not null) return fromProperties;
        if (blipFill is null) return null;

        foreach (var click in blipFill.Descendants(PptXml.A + "hlinkClick"))
        {
            var target = PptTextParser.ResolveHyperlinkElement(click, relationships);
            if (target is not null) return target;
        }

        return null;
    }

    private static double ReadCropRatio(XElement sourceRectangle, string attributeName)
    {
        var value = PptXml.ReadDouble(PptXml.Attr(sourceRectangle, attributeName));
        if (!value.HasValue) return 0;
        return Math.Clamp(value.Value / 100000.0, 0, 1);
    }

    // ---------------------------------------------------------------- p:graphicFrame

    private static PptShape? ParseGraphicFrame(XElement element, PptShapeContext context, PptShapeTransform transform)
    {
        var nonVisual = element.Element(PptXml.P + "nvGraphicFramePr");
        var properties = nonVisual?.Element(PptXml.P + "cNvPr");
        var placeholder = nonVisual?.Element(PptXml.P + "nvPr")?.Element(PptXml.P + "ph");
        var frameTransform = element.Element(PptXml.P + "xfrm");

        var graphicData = element.Element(PptXml.A + "graphic")?.Element(PptXml.A + "graphicData");
        var tableElement = graphicData?.Element(PptXml.A + "tbl");

        PptShape shape;
        if (tableElement is not null)
        {
            var parsedTable = ParseTable(tableElement, context);
            shape = parsedTable;
        }
        else
        {
            shape = new PptUnsupportedShape { KindLabel = DetectUnsupportedKind(graphicData) };
        }

        ApplyCommonProperties(shape, properties, placeholder);

        // graphicFrame 的 p:xfrm 没有 rot / flip，只有子元素的 a:off / a:ext
        if (frameTransform is not null)
        {
            var offset = frameTransform.Element(PptXml.A + "off");
            var extent = frameTransform.Element(PptXml.A + "ext");
            if (offset is not null)
            {
                shape.X = transform.X(PptXml.EmuToDip(PptXml.LongAttr(offset, "x")));
                shape.Y = transform.Y(PptXml.EmuToDip(PptXml.LongAttr(offset, "y")));
            }

            if (extent is not null)
            {
                shape.Width = Math.Max(0, PptXml.EmuToDip(PptXml.LongAttr(extent, "cx"))) * transform.ScaleX;
                shape.Height = Math.Max(0, PptXml.EmuToDip(PptXml.LongAttr(extent, "cy"))) * transform.ScaleY;
            }
        }
        else
        {
            ApplyTransform(shape, ResolvePlaceholderXfrm(
                context,
                PptXml.Attr(placeholder, "type"),
                PptXml.IntAttr(placeholder, "idx")), transform);
        }

        // 表格列宽 / 行高缺失或全是 0 时按形状宽高平均分摊，渲染器至少能画出格子
        if (shape is PptTableShape table)
        {
            var columnCount = table.ColumnWidthsDip.Count;
            if (columnCount > 0 && table.ColumnWidthsDip.All(width => width <= 0) && shape.Width > 0)
                table.ColumnWidthsDip = Enumerable.Repeat(shape.Width / columnCount, columnCount).ToList();

            var rowCount = table.Rows.Count;
            if (rowCount > 0 && table.Rows.All(row => row.HeightDip <= 0) && shape.Height > 0)
            {
                var rowHeight = shape.Height / rowCount;
                foreach (var row in table.Rows) row.HeightDip = rowHeight;
            }
        }

        return shape;
    }

    /// <summary>判断 <c>a:graphicData</c> 里是不是图表 / SmartArt / 其它不支持的对象。</summary>
    private static string DetectUnsupportedKind(XElement? graphicData)
    {
        if (graphicData is null) return "图表或对象";

        if (graphicData.Descendants().Any(descendant => descendant.Name.LocalName == "chart")
            || graphicData.Descendants(PptXml.C + "chart").Any())
        {
            return "图表";
        }

        if (graphicData.Descendants().Any(descendant => descendant.Name.LocalName == "relIds")
            || graphicData.Descendants().Any(descendant => descendant.Name.Namespace == PptXml.DGM))
        {
            return "SmartArt";
        }

        return "图表或对象";
    }

    private static PptTableShape ParseTable(XElement tableElement, PptShapeContext context)
    {
        var table = new PptTableShape();

        // 列宽
        var columnWidths = new List<double>();
        var tableGrid = tableElement.Element(PptXml.A + "tblGrid");
        if (tableGrid is not null)
        {
            foreach (var column in tableGrid.Elements(PptXml.A + "gridCol"))
            {
                var width = PptXml.LongAttr(column, "w");
                if (width is > 0) columnWidths.Add(OoxmlPackage.EmuToDip(width.Value));
            }
        }

        // 单元格文字统一用「其它文字」默认样式（主题 minorFont、18 磅、无项目符号）
        var cellTextContext = PptTextParser.CreateContext(
            context.ThemeData,
            context.Colors,
            context.Relationships,
            context.MasterStyles,
            "other");

        var rows = new List<PptTableRow>();
        var maximumColumns = 0;

        foreach (var rowElement in tableElement.Elements(PptXml.A + "tr"))
        {
            var row = new PptTableRow
            {
                HeightDip = Math.Max(0, PptXml.EmuToDip(PptXml.LongAttr(rowElement, "h"))),
            };

            var cells = new List<PptTableCell>();
            var columnCount = 0;

            foreach (var cellElement in rowElement.Elements(PptXml.A + "tc"))
            {
                var cellProperties = cellElement.Element(PptXml.A + "tcPr");

                // DrawingML 规定 gridSpan / rowSpan / hMerge / vMerge 是 <a:tc> 的属性；
                // 少数生成器会写成 <a:tcPr> 的子元素，两种都兼容。
                var columnSpan = PptXml.IntAttr(cellElement, "gridSpan")
                    ?? PptXml.IntAttr(cellProperties?.Element(PptXml.A + "gridSpan"), "val")
                    ?? 1;
                var rowSpan = PptXml.IntAttr(cellElement, "rowSpan")
                    ?? PptXml.IntAttr(cellProperties?.Element(PptXml.A + "rowSpan"), "val")
                    ?? 1;
                var isCovered = PptXml.OnOffAttr(cellElement, "hMerge")
                    || PptXml.OnOffAttr(cellElement, "vMerge")
                    || PptXml.GetOnOff(cellProperties?.Element(PptXml.A + "hMerge"))
                    || PptXml.GetOnOff(cellProperties?.Element(PptXml.A + "vMerge"));

                var text = PptTextParser.ParseTextBody(cellElement.Element(PptXml.A + "txBody"), cellTextContext);
                var cell = new PptTableCell
                {
                    Text = text.HasText ? text : null,
                    Fill = ParseFill(cellProperties, context.Colors, context.Images, context.Relationships),
                    ColSpan = Math.Clamp(columnSpan, 1, 1000),
                    RowSpan = Math.Clamp(rowSpan, 1, 1000),
                    IsCovered = isCovered,
                    VerticalAnchor = PptXml.Attr(cellProperties, "anchor").ToLowerInvariant() switch
                    {
                        "t" => PptVerticalAnchor.Top,
                        "b" => PptVerticalAnchor.Bottom,
                        _ => PptVerticalAnchor.Middle,
                    },
                };

                cells.Add(cell);

                // 被合并覆盖的格子（hMerge / vMerge）是延续格，不占用新的表格列
                if (!isCovered) columnCount += cell.ColSpan;
            }

            maximumColumns = Math.Max(maximumColumns, columnCount);
            row.Cells = cells;
            rows.Add(row);
        }

        // 列宽缺失时按已有列宽的平均值补齐；网格列比单元格多时保留网格（合并单元格会少写格子）
        if (maximumColumns > 0 && columnWidths.Count < maximumColumns)
        {
            var average = columnWidths.Count > 0 ? columnWidths.Average() : 0;
            while (columnWidths.Count < maximumColumns) columnWidths.Add(average);
        }

        table.ColumnWidthsDip = columnWidths;
        table.Rows = rows;
        return table;
    }

    // ---------------------------------------------------------------- p:grpSp

    private static PptShape? ParseGroup(
        XElement element,
        PptShapeContext context,
        PptShapeTransform transform,
        int depth)
    {
        var nonVisual = element.Element(PptXml.P + "nvGrpSpPr");
        var properties = nonVisual?.Element(PptXml.P + "cNvPr");
        var groupProperties = element.Element(PptXml.P + "grpSpPr");
        var groupTransform = groupProperties?.Element(PptXml.A + "xfrm");

        var group = new PptGroupShape();
        ApplyCommonProperties(group, properties, placeholder: null);
        ApplyTransform(group, groupTransform, transform);

        var children = new List<PptShape>();
        ParseShapeChildren(element, context, ComposeGroupTransform(transform, groupTransform), children, depth + 1);
        group.Children = children;
        return group;
    }

    /// <summary>
    /// 组合变换复合：子形状绝对坐标 = 组合位置 + (子形状位置 - chOff) × (ext / chExt)，再套外层变换。
    /// 组合缺 <c>a:xfrm</c> / chExt 为 0 时保持外层变换（坐标原样）。
    /// </summary>
    private static PptShapeTransform ComposeGroupTransform(PptShapeTransform parent, XElement? groupTransform)
    {
        if (groupTransform is null) return parent;

        var offset = groupTransform.Element(PptXml.A + "off");
        var extent = groupTransform.Element(PptXml.A + "ext");
        if (offset is null || extent is null) return parent;

        var offsetX = PptXml.EmuToDip(PptXml.LongAttr(offset, "x"));
        var offsetY = PptXml.EmuToDip(PptXml.LongAttr(offset, "y"));
        var extentX = PptXml.EmuToDip(PptXml.LongAttr(extent, "cx"));
        var extentY = PptXml.EmuToDip(PptXml.LongAttr(extent, "cy"));
        if (extentX <= 0 || extentY <= 0) return parent;

        // chOff / chExt 缺失时按「子坐标系 = 组合坐标系」处理
        var childOffset = groupTransform.Element(PptXml.A + "chOff");
        var childExtent = groupTransform.Element(PptXml.A + "chExt");
        var childOffsetX = childOffset is not null ? PptXml.EmuToDip(PptXml.LongAttr(childOffset, "x")) : 0;
        var childOffsetY = childOffset is not null ? PptXml.EmuToDip(PptXml.LongAttr(childOffset, "y")) : 0;
        var childExtentX = childExtent is not null ? PptXml.EmuToDip(PptXml.LongAttr(childExtent, "cx")) : extentX;
        var childExtentY = childExtent is not null ? PptXml.EmuToDip(PptXml.LongAttr(childExtent, "cy")) : extentY;
        if (childExtentX <= 0 || childExtentY <= 0) return parent;

        var scaleX = extentX / childExtentX;
        var scaleY = extentY / childExtentY;

        return new PptShapeTransform(
            parent.OffsetX + (offsetX - childOffsetX * scaleX) * parent.ScaleX,
            parent.OffsetY + (offsetY - childOffsetY * scaleY) * parent.ScaleY,
            scaleX * parent.ScaleX,
            scaleY * parent.ScaleY);
    }

    // ---------------------------------------------------------------- p:cxnSp

    private static PptShape? ParseConnector(XElement element, PptShapeContext context, PptShapeTransform transform)
    {
        var nonVisual = element.Element(PptXml.P + "nvCxnSpPr");
        var properties = nonVisual?.Element(PptXml.P + "cNvPr");
        var placeholder = nonVisual?.Element(PptXml.P + "nvPr")?.Element(PptXml.P + "ph");
        var shapeProperties = element.Element(PptXml.P + "spPr");

        var placeholderType = PptXml.Attr(placeholder, "type");
        var placeholderIndex = PptXml.IntAttr(placeholder, "idx");
        var transformElement = shapeProperties?.Element(PptXml.A + "xfrm")
            ?? ResolvePlaceholderXfrm(context, placeholderType, placeholderIndex);

        var lineElement = shapeProperties?.Element(PptXml.A + "ln");
        var line = lineElement is not null
            ? ParseLine(lineElement, context.Colors)
            : new PptLine(); // 连接线不写 a:ln 时按默认细黑线画

        // 带箭头的连接线用 Arrow 近似（渲染器画箭头；模型没有单独的连接线类型）
        var kind = PptGeometryKind.Line;
        if (lineElement is not null && HasArrowEnd(lineElement)) kind = PptGeometryKind.Arrow;

        var connector = new PptGeometryShape
        {
            Kind = kind,
            Line = line,
            Fill = null,
        };

        ApplyCommonProperties(connector, properties, placeholder);
        ApplyTransform(connector, transformElement, transform);
        return connector;
    }

    private static bool HasArrowEnd(XElement lineElement)
    {
        foreach (var endName in new[] { "headEnd", "tailEnd" })
        {
            var end = lineElement.Element(PptXml.A + endName);
            if (end is null) continue;
            var type = PptXml.Attr(end, "type");
            if (type.Length > 0 && !string.Equals(type, "none", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- 公共属性 / 变换 / 继承

    private static void ApplyCommonProperties(PptShape shape, XElement? nonVisualProperties, XElement? placeholder)
    {
        shape.Name = PptXml.Attr(nonVisualProperties, "name");
        shape.ShapeId = PptXml.IntAttr(nonVisualProperties, "id") ?? 0;

        if (placeholder is not null)
        {
            shape.IsPlaceholder = true;
            shape.PlaceholderType = PptXml.Attr(placeholder, "type");
        }
    }

    /// <summary>把形状自己的 <c>a:xfrm</c>（或继承来的占位符 xfrm）应用到模型坐标（DIP）。</summary>
    private static void ApplyTransform(PptShape shape, XElement? transformElement, PptShapeTransform transform)
    {
        if (transformElement is null) return;

        var offset = transformElement.Element(PptXml.A + "off");
        if (offset is not null)
        {
            shape.X = transform.X(PptXml.EmuToDip(PptXml.LongAttr(offset, "x")));
            shape.Y = transform.Y(PptXml.EmuToDip(PptXml.LongAttr(offset, "y")));
        }

        var extent = transformElement.Element(PptXml.A + "ext");
        if (extent is not null)
        {
            shape.Width = Math.Max(0, PptXml.EmuToDip(PptXml.LongAttr(extent, "cx"))) * transform.ScaleX;
            shape.Height = Math.Max(0, PptXml.EmuToDip(PptXml.LongAttr(extent, "cy"))) * transform.ScaleY;
        }

        shape.RotationDegrees = PptXml.RotationDegrees(PptXml.LongAttr(transformElement, "rot"));
        shape.FlipH = PptXml.OnOffAttr(transformElement, "flipH");
        shape.FlipV = PptXml.OnOffAttr(transformElement, "flipV");
    }

    /// <summary>占位符位置继承：版式同名占位符 → 母版同名占位符。</summary>
    private static XElement? ResolvePlaceholderXfrm(PptShapeContext context, string type, int? index)
    {
        if (type.Length == 0 && !index.HasValue) return null;

        var layoutPlaceholder = MatchPlaceholder(context.Layout?.Placeholders, type, index);
        if (layoutPlaceholder?.Xfrm is not null) return layoutPlaceholder.Xfrm;

        var masterPlaceholder = MatchPlaceholder(context.Master?.Placeholders, type, index);
        return masterPlaceholder?.Xfrm;
    }

    /// <summary>占位符填充继承：版式写了用版式的，否则母版。</summary>
    private static PptFill? ResolvePlaceholderFill(PptShapeContext context, string type, int? index)
    {
        var layoutPlaceholder = MatchPlaceholder(context.Layout?.Placeholders, type, index);
        if (layoutPlaceholder is { HasFill: true }) return layoutPlaceholder.Fill;

        var masterPlaceholder = MatchPlaceholder(context.Master?.Placeholders, type, index);
        return masterPlaceholder is { HasFill: true } ? masterPlaceholder.Fill : null;
    }

    /// <summary>占位符边框继承：版式写了用版式的，否则母版。</summary>
    private static PptLine? ResolvePlaceholderLine(PptShapeContext context, string type, int? index)
    {
        var layoutPlaceholder = MatchPlaceholder(context.Layout?.Placeholders, type, index);
        if (layoutPlaceholder is { HasLine: true }) return layoutPlaceholder.Line;

        var masterPlaceholder = MatchPlaceholder(context.Master?.Placeholders, type, index);
        return masterPlaceholder is { HasLine: true } ? masterPlaceholder.Line : null;
    }

    /// <summary>形状 <c>p:style/a:fillRef</c> 引用的主题填充样式（形状自身没写填充时兜底）。</summary>
    private static PptFill? ResolveStyleFill(XElement? style, PptShapeContext context)
    {
        var fillReference = style?.Element(PptXml.A + "fillRef");
        if (fillReference is null) return null;

        var index = PptXml.IntAttr(fillReference, "idx") ?? 0;
        if (index <= 0) return null;

        var styleIndex = index - 1;
        if (styleIndex < 0 || styleIndex >= context.ThemeData.FillStyles.Count) return null;

        var referenceColor = fillReference.Elements().FirstOrDefault();
        var styleContext = referenceColor is not null
            ? context.Colors.WithPlaceholder(context.Colors.Resolve(referenceColor, context.Colors.PlaceholderColor))
            : context.Colors;
        return ParseFillElement(context.ThemeData.FillStyles[styleIndex], styleContext,
            context.Images, context.Relationships);
    }

    /// <summary>形状 <c>p:style/a:lnRef</c> 引用的主题线条样式。</summary>
    private static PptLine? ResolveStyleLine(XElement? style, PptShapeContext context)
    {
        var lineReference = style?.Element(PptXml.A + "lnRef");
        if (lineReference is null) return null;

        var index = PptXml.IntAttr(lineReference, "idx") ?? 0;
        if (index <= 0) return null;

        var styleIndex = index - 1;
        if (styleIndex < 0 || styleIndex >= context.ThemeData.LineStyles.Count) return null;

        var referenceColor = lineReference.Elements().FirstOrDefault();
        var styleContext = referenceColor is not null
            ? context.Colors.WithPlaceholder(context.Colors.Resolve(referenceColor, context.Colors.PlaceholderColor))
            : context.Colors;
        return ParseLine(context.ThemeData.LineStyles[styleIndex], styleContext);
    }

    // ---------------------------------------------------------------- 填充解析内部实现

    private static bool IsFillElement(XElement element)
        => element.Name.LocalName is "solidFill" or "gradFill" or "blipFill" or "pattFill" or "noFill" or "grpFill";

    private static PptFill? ParseFillElement(
        XElement? fillElement,
        PptColorContext colors,
        PptImageCache? images,
        IReadOnlyDictionary<string, OoxmlRelationship>? relationships)
    {
        if (fillElement is null) return null;

        switch (fillElement.Name.LocalName)
        {
            case "noFill":
            case "grpFill":
                return null;

            case "solidFill":
            {
                var colorElement = fillElement.Elements().FirstOrDefault();
                var color = colors.Resolve(colorElement, Colors.White);
                return PptFill.Solid(color);
            }

            case "pattFill":
            {
                // 图案填充近似成前景色纯色（阅读器不画图案纹理）
                var foreground = fillElement.Element(PptXml.A + "fgClr")?.Elements().FirstOrDefault();
                return PptFill.Solid(colors.Resolve(foreground, Colors.White));
            }

            case "gradFill":
            {
                var stops = new List<(double Position, Color Color)>();
                var stopList = fillElement.Element(PptXml.A + "gsLst");
                if (stopList is not null)
                {
                    foreach (var stop in stopList.Elements(PptXml.A + "gs"))
                    {
                        var colorElement = stop.Elements().FirstOrDefault();
                        if (colorElement is null) continue;
                        var position = (PptXml.DoubleAttr(stop, "pos") ?? 0) / 100000.0;
                        stops.Add((Math.Clamp(position, 0, 1), colors.Resolve(colorElement, Colors.White)));
                    }
                }

                stops.Sort((left, right) => left.Position.CompareTo(right.Position));
                var startColor = stops.Count > 0 ? stops[0].Color : Colors.White;
                var endColor = stops.Count > 0 ? stops[^1].Color : Colors.White;

                var angle = 90.0;
                var linear = fillElement.Element(PptXml.A + "lin");
                if (linear is not null)
                {
                    var angleValue = PptXml.LongAttr(linear, "ang");
                    if (angleValue.HasValue) angle = PptXml.RotationDegrees(angleValue.Value);
                }

                return new PptFill
                {
                    Kind = PptFillKind.Gradient,
                    Color = startColor,
                    Color2 = endColor,
                    GradientAngleDegrees = angle,
                };
            }

            case "blipFill":
            {
                var blip = fillElement.Element(PptXml.A + "blip");
                var embedId = PptXml.Attr(blip, PptXml.R, "embed");
                if (embedId.Length == 0) return null;
                if (images is null || relationships is null) return null;
                if (!relationships.TryGetValue(embedId, out var relationship) || relationship.IsExternal) return null;

                var image = images.Read(relationship.Target);
                if (image is null) return null;

                return new PptFill
                {
                    Kind = PptFillKind.Picture,
                    Color = Colors.White,
                    Color2 = Colors.White,
                    PictureBytes = image.Bytes,
                    PictureContentType = image.ContentType,
                };
            }

            default:
                return null;
        }
    }

    private static bool SameType(string left, string right)
        => string.Equals(left?.Trim() ?? "", right?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

    /// <summary>文本框 / 表格单元格里有没有可见文字（<c>a:t</c> 非空白）。</summary>
    private static bool HasVisibleText(XElement? textBodyElement)
        => textBodyElement?.Descendants(PptXml.A + "t")
            .Any(text => !string.IsNullOrWhiteSpace(text.Value)) ?? false;
}
