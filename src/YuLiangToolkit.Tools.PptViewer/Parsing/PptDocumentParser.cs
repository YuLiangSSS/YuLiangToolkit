using System.IO;
using System.Windows.Media;
using System.Xml.Linq;
using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

/// <summary>
/// 母版信息（解析阶段缓存）：颜色映射 / 占位符 / 背景 / 文字大纲样式（titleStyle / bodyStyle / otherStyle）。
/// </summary>
internal sealed class PptMasterInfo
{
    /// <summary>母版部件名（例如 <c>ppt/slideMasters/slideMaster1.xml</c>）。</summary>
    public string PartName { get; init; } = "";

    /// <summary>母版 <c>p:clrMap</c> 的颜色映射（bg1/tx1/bg2/tx2 → lt1/dk1…）；null = 默认映射。</summary>
    public IReadOnlyDictionary<string, string>? ColorMap { get; init; }

    /// <summary>母版上的占位符（版式 / 幻灯片位置继承用）。</summary>
    public IReadOnlyList<PptPlaceholderInfo> Placeholders { get; init; } = Array.Empty<PptPlaceholderInfo>();

    /// <summary>母版背景（版式 / 幻灯片都没写背景时继承）。</summary>
    public PptFill? Background { get; init; }

    /// <summary>母版三套文字大纲样式。</summary>
    public PptMasterStyles Styles { get; init; } = new();
}

/// <summary>版式信息（解析阶段缓存）：占位符 / 背景 / 关联母版。</summary>
internal sealed class PptLayoutInfo
{
    /// <summary>版式部件名（例如 <c>ppt/slideLayouts/slideLayout1.xml</c>）。</summary>
    public string PartName { get; init; } = "";

    /// <summary>版式上的占位符（幻灯片位置 / 填充 / 边框继承用）。</summary>
    public IReadOnlyList<PptPlaceholderInfo> Placeholders { get; init; } = Array.Empty<PptPlaceholderInfo>();

    /// <summary>版式背景（幻灯片没写背景时继承）。</summary>
    public PptFill? Background { get; init; }

    /// <summary>版式关联的母版（版式关系表 → 母版；找不到时用演示文稿的第一个母版）。</summary>
    public PptMasterInfo? Master { get; init; }
}

/// <summary>
/// .pptx 解析入口：把一份 OOXML 演示文稿读成 <see cref="PptPresentation"/> 纯数据模型。
///
/// <para>解析在后台线程跑（ViewModel 用 <c>Task.Run</c>），产出物里没有 WPF 控件 / 图片句柄，
/// 所以可以安全跨线程；渲染（建 WPF 对象）在 UI 线程单独做，见 <c>Rendering/PptSlideRenderer</c>。</para>
///
/// <para>读取顺序：<c>_rels/.rels</c> 的 officeDocument → <c>ppt/presentation.xml</c> →
/// <c>p:sldIdLst</c> 按列表顺序 → 关系表 → <c>ppt/slides/slideN.xml</c>（不按文件名数字排序）。
/// 主题从 presentation 的关系表找 <c>/theme</c>；版式 / 母版按幻灯片 / 版式自己的关系表解析。</para>
///
/// <para>解析覆盖：主题色与字体、母版 txStyles 文字默认值、占位符位置 / 填充继承、背景继承、
/// 形状（文字 / 自选图形 / 图片 + 裁剪 / 表格 / 组合 / 连接线）、文字格式、备注、文档属性。</para>
///
/// <para>文件损坏 / 不是 OOXML 时抛 <see cref="OoxmlException"/>（消息是中文，界面可以直接显示）。
/// 单页 / 单个形状的小问题一律降级（缺部件跳过、坏形状用占位框），不要因为一个小问题拒绝整份文件。</para>
/// </summary>
public static class PptDocumentParser
{
    /// <summary>幻灯片数量安全上限（正常演示文稿远小于这个数；防畸形文件）。</summary>
    private const int MaxSlides = 2000;

    /// <summary>解析磁盘上的 .pptx / .pptm / .ppsx。</summary>
    /// <exception cref="OoxmlException">文件不存在 / 不是有效 OOXML / 缺少 presentation.xml。</exception>
    public static PptPresentation Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new OoxmlException("没有选择文件");
        if (!File.Exists(path)) throw new OoxmlException($"文件不存在：{path}");

        using var package = OoxmlPackage.Open(path);
        return Parse(package, path);
    }

    /// <summary>解析一个已经打开的 OOXML 包（自检可以直接喂内存 zip；调用方负责 Dispose）。</summary>
    /// <exception cref="OoxmlException">不是 PPTX / 缺主部件。</exception>
    public static PptPresentation Parse(OoxmlPackage package, string filePath = "")
    {
        ArgumentNullException.ThrowIfNull(package);
        return new ParseSession(package, filePath).Run();
    }

    // =========================================================================
    // 一次解析的会话：持有包 / 主题 / 母版与版式缓存，避免同一部件反复解压。
    // =========================================================================

    private sealed class ParseSession
    {
        private readonly OoxmlPackage _package;
        private readonly PptImageCache _images;
        private readonly string _filePath;
        private readonly Dictionary<string, PptMasterInfo?> _masters = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PptLayoutInfo?> _layouts = new(StringComparer.OrdinalIgnoreCase);

        private PptThemeData _theme = new();
        private string _defaultMasterPart = "";
        private double _slideWidthDip = PptDefaults.DefaultWidthDip;
        private double _slideHeightDip = PptDefaults.DefaultHeightDip;

        public ParseSession(OoxmlPackage package, string filePath)
        {
            _package = package;
            _images = new PptImageCache(package);
            _filePath = filePath ?? "";
        }

        /// <summary>解析主流程（只对外抛 OoxmlException：主部件缺失 / 损坏）。</summary>
        public PptPresentation Run()
        {
            var (presentationPart, presentationXml) = ReadPresentationPart();
            var presentationRoot = presentationXml.Root!;
            var presentationRelationships = PptXml.ReadRelationships(_package, presentationPart);

            _theme = LoadThemeData(presentationRelationships);
            _defaultMasterPart = ResolveDefaultMasterPart(presentationRelationships);
            var defaultMaster = GetMaster(_defaultMasterPart);

            ReadSlideSize(presentationRoot);

            var properties = ReadProperties();
            var presentation = new PptPresentation
            {
                FilePath = _filePath,
                Title = !string.IsNullOrWhiteSpace(properties.Title)
                    ? properties.Title
                    : Path.GetFileName(_filePath),
                WidthDip = _slideWidthDip,
                HeightDip = _slideHeightDip,
                Theme = _theme.Theme,
                Properties = properties,
            };

            var slides = new List<PptSlide>();
            var slideIdList = presentationRoot.Element(PptXml.P + "sldIdLst");

            if (slideIdList is not null)
            {
                foreach (var slideId in slideIdList.Elements(PptXml.P + "sldId"))
                {
                    if (slides.Count >= MaxSlides) break;

                    var relationshipId = PptXml.Attr(slideId, PptXml.R, "id");
                    if (relationshipId.Length == 0) continue;
                    if (!presentationRelationships.TryGetValue(relationshipId, out var relationship)) continue;
                    if (relationship.IsExternal || string.IsNullOrWhiteSpace(relationship.Target)) continue;
                    if (!_package.Contains(relationship.Target)) continue;

                    AddSlide(slides, relationship.Target, defaultMaster);
                }
            }

            // 极端情况：没有 sldIdLst（手写 / 精简生成器）时按文件名序号兜底；
            // 有 sldIdLst 时永远按列表顺序，不按文件名排序。
            if (slides.Count == 0) AddSlidesByPartName(slides, defaultMaster);

            presentation.Slides = slides;
            return presentation;
        }

        // ------------------------------------------------------------ 主部件 / 主题

        /// <summary>
        /// 找主演示文稿部件：优先 <c>_rels/.rels</c> 的 officeDocument 关系；
        /// 读不到再退回约定俗成的 <c>ppt/presentation.xml</c>。找不到抛中文异常。
        /// </summary>
        private (string PartName, XDocument Xml) ReadPresentationPart()
        {
            var partName = "";
            foreach (var relationship in PptXml.ReadRelationships(_package, "").Values)
            {
                if (relationship.IsExternal) continue;
                if (!relationship.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase)) continue;
                if (_package.Contains(relationship.Target))
                {
                    partName = relationship.Target;
                    break;
                }
            }

            if (partName.Length == 0 && _package.Contains("ppt/presentation.xml"))
                partName = "ppt/presentation.xml";

            if (partName.Length == 0)
                throw new OoxmlException("不是 PowerPoint 演示文稿（缺少 ppt/presentation.xml）");

            XDocument xml;
            try
            {
                xml = _package.ReadXml(partName);
            }
            catch (OoxmlException ex)
            {
                throw new OoxmlException("PowerPoint 主部件损坏，无法读取演示文稿", ex);
            }

            // 打开的是 docx / xlsx 时，officeDocument 指向别的部件：这里统一报「不是 PowerPoint」
            if (xml.Root is null
                || xml.Root.Name.LocalName != "presentation"
                || xml.Root.Name.Namespace != PptXml.P)
            {
                throw new OoxmlException("不是 PowerPoint 演示文稿（缺少 ppt/presentation.xml）");
            }

            return (partName, xml);
        }

        /// <summary>读 <c>p:sldSz cx/cy</c>（EMU → DIP）；缺省 1280 × 720（16:9）。</summary>
        private void ReadSlideSize(XElement presentationRoot)
        {
            var size = presentationRoot.Element(PptXml.P + "sldSz");
            if (size is null) return;

            var cx = PptXml.LongAttr(size, "cx");
            var cy = PptXml.LongAttr(size, "cy");
            if (cx is > 0) _slideWidthDip = OoxmlPackage.EmuToDip(cx.Value);
            if (cy is > 0) _slideHeightDip = OoxmlPackage.EmuToDip(cy.Value);

            // 坏数据保护：尺寸为 0 / 负数 / 离谱大小时回默认值（渲染器按滑块缩放，不怕大，怕除零）
            if (!(_slideWidthDip > 0) || !(_slideHeightDip > 0))
            {
                _slideWidthDip = PptDefaults.DefaultWidthDip;
                _slideHeightDip = PptDefaults.DefaultHeightDip;
            }
        }

        /// <summary>
        /// 读主题：优先 presentation 关系表的 <c>/theme</c>；没有就顺着第一个母版的关系表找；
        /// 都没有时退回 <c>ppt/theme/theme1.xml</c>，最后用默认主题（不抛异常）。
        /// </summary>
        private PptThemeData LoadThemeData(IReadOnlyDictionary<string, OoxmlRelationship> presentationRelationships)
        {
            var themePart = PptXml.FindPartByRelationshipType(presentationRelationships, "/theme", _package.Contains);

            if (themePart.Length == 0)
            {
                var masterPart = PptXml.FindPartByRelationshipType(presentationRelationships, "/slideMaster", _package.Contains);
                if (masterPart.Length > 0)
                {
                    var masterRelationships = PptXml.ReadRelationships(_package, masterPart);
                    themePart = PptXml.FindPartByRelationshipType(masterRelationships, "/theme", _package.Contains);
                }
            }

            if (themePart.Length == 0) themePart = GuessPart("ppt/theme", "theme");
            return PptThemeParser.Parse(themePart.Length > 0 ? _package.TryReadXml(themePart) : null);
        }

        /// <summary>演示文稿的第一个母版（版式关系表里找不到母版时兜底）。</summary>
        private string ResolveDefaultMasterPart(IReadOnlyDictionary<string, OoxmlRelationship> presentationRelationships)
        {
            var partName = PptXml.FindPartByRelationshipType(presentationRelationships, "/slideMaster", _package.Contains);
            if (partName.Length > 0) return partName;

            // 有些生成器把母版关系漏了：按约定路径兜底
            return GuessPart("ppt/slideMasters", "slideMaster");
        }

        // ------------------------------------------------------------ 母版 / 版式

        /// <summary>读并缓存一个母版（部件缺失 / 坏 XML 返回 null，不抛异常）。</summary>
        private PptMasterInfo? GetMaster(string partName)
        {
            if (string.IsNullOrWhiteSpace(partName)) return null;
            if (_masters.TryGetValue(partName, out var cached)) return cached;

            // 先占位，防止版式 → 母版 → 版式 这种异常关系形成递归
            _masters[partName] = null;

            var xml = _package.TryReadXml(partName);
            if (xml?.Root is null) return null;

            var relationships = PptXml.ReadRelationships(_package, partName);
            var colorMap = PptThemeParser.ParseColorMap(xml.Root.Element(PptXml.P + "clrMap"));
            var colors = new PptColorContext(_theme, colorMap);

            var commonSlideData = xml.Root.Element(PptXml.P + "cSld");
            var info = new PptMasterInfo
            {
                PartName = partName,
                ColorMap = colorMap,
                Placeholders = ReadPlaceholders(commonSlideData, colors, relationships),
                Background = PptShapeParser.ParseBackground(commonSlideData, colors, _images, relationships),
                Styles = PptTextParser.ParseMasterStyles(xml.Root.Element(PptXml.P + "txStyles"), colors),
            };

            _masters[partName] = info;
            return info;
        }

        /// <summary>读并缓存幻灯片对应的版式（关系表里 type 以 /slideLayout 结尾）。</summary>
        private PptLayoutInfo? GetLayout(
            IReadOnlyDictionary<string, OoxmlRelationship> slideRelationships,
            PptMasterInfo? defaultMaster)
        {
            var partName = PptXml.FindPartByRelationshipType(slideRelationships, "/slideLayout", _package.Contains);
            if (partName.Length == 0) return null;
            if (_layouts.TryGetValue(partName, out var cached)) return cached;

            _layouts[partName] = null;

            var xml = _package.TryReadXml(partName);
            if (xml?.Root is null) return null;

            var relationships = PptXml.ReadRelationships(_package, partName);

            // 版式关系表 → 母版；找不到就用演示文稿的第一个母版
            var masterPart = PptXml.FindPartByRelationshipType(relationships, "/slideMaster", _package.Contains);
            if (masterPart.Length == 0) masterPart = _defaultMasterPart;
            var master = GetMaster(masterPart) ?? defaultMaster;

            // 版式上的占位符 / 背景按母版的 clrMap 解析（版式本身没有 clrMap）
            var colors = new PptColorContext(_theme, master?.ColorMap);
            var commonSlideData = xml.Root.Element(PptXml.P + "cSld");

            var info = new PptLayoutInfo
            {
                PartName = partName,
                Placeholders = ReadPlaceholders(commonSlideData, colors, relationships),
                Background = PptShapeParser.ParseBackground(commonSlideData, colors, _images, relationships),
                Master = master,
            };

            _layouts[partName] = info;
            return info;
        }

        /// <summary>读 <c>p:cSld/p:spTree</c> 里顶层 <c>p:sp</c> 的占位符信息（版式 / 母版用）。</summary>
        private List<PptPlaceholderInfo> ReadPlaceholders(
            XElement? commonSlideData,
            PptColorContext colors,
            IReadOnlyDictionary<string, OoxmlRelationship> relationships)
        {
            var placeholders = new List<PptPlaceholderInfo>();
            var shapeTree = commonSlideData?.Element(PptXml.P + "spTree");
            if (shapeTree is null) return placeholders;

            foreach (var shapeElement in shapeTree.Elements(PptXml.P + "sp"))
            {
                try
                {
                    var placeholder = PptShapeParser.ReadPlaceholder(shapeElement, colors, relationships, _images);
                    if (placeholder is not null) placeholders.Add(placeholder);
                }
                catch
                {
                    // 单个占位符解析失败：跳过，其它照常
                }
            }

            return placeholders;
        }

        // ------------------------------------------------------------ 幻灯片

        /// <summary>按 <c>p:sldIdLst</c> 的顺序解析一页并追加到列表。</summary>
        private void AddSlide(List<PptSlide> slides, string slidePart, PptMasterInfo? defaultMaster)
        {
            var index = slides.Count + 1;

            XDocument? xml;
            try
            {
                xml = _package.TryReadXml(slidePart);
            }
            catch (OoxmlException)
            {
                xml = null;
            }

            if (xml?.Root is null)
            {
                // 坏页也保留页序：至少页码、标题（第 N 页）对得上，不至于后面整体错位
                slides.Add(new PptSlide { Index = index });
                return;
            }

            var slide = ParseSlide(xml.Root, slidePart, defaultMaster, index);
            slides.Add(slide);
        }

        /// <summary>没有 <c>p:sldIdLst</c> 时按部件名序号兜底（正常文件不会走到这里）。</summary>
        private void AddSlidesByPartName(List<PptSlide> slides, PptMasterInfo? defaultMaster)
        {
            var parts = new List<(int Number, string PartName)>();
            foreach (var partName in _package.PartNames)
            {
                if (!partName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)) continue;
                if (!partName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

                var name = Path.GetFileNameWithoutExtension(partName);
                var digits = new string(name.SkipWhile(character => !char.IsAsciiDigit(character)).TakeWhile(char.IsAsciiDigit).ToArray());
                if (!int.TryParse(digits, out var number)) continue;
                parts.Add((number, partName));
            }

            parts.Sort((left, right) => left.Number.CompareTo(right.Number));
            foreach (var (_, partName) in parts)
            {
                if (slides.Count >= MaxSlides) break;
                AddSlide(slides, partName, defaultMaster);
            }
        }

        private PptSlide ParseSlide(
            XElement slideRoot,
            string slidePart,
            PptMasterInfo? defaultMaster,
            int index)
        {
            var slideRelationships = PptXml.ReadRelationships(_package, slidePart);
            var layout = GetLayout(slideRelationships, defaultMaster);
            var master = layout?.Master ?? defaultMaster;

            // 颜色映射：母版 clrMap 打底；幻灯片 p:clrMapOvr 可以覆盖
            var overrideMap = PptThemeParser.ParseColorMap(
                slideRoot.Element(PptXml.P + "clrMapOvr")?.Element(PptXml.A + "overrideClrMapping"));
            var colors = new PptColorContext(_theme, overrideMap ?? master?.ColorMap);

            var commonSlideData = slideRoot.Element(PptXml.P + "cSld");

            // 背景继承：幻灯片 → 版式 → 母版 → null（白底）
            var background = PptShapeParser.ParseBackground(commonSlideData, colors, _images, slideRelationships)
                ?? layout?.Background
                ?? master?.Background;

            var shapeContext = new PptShapeContext
            {
                Images = _images,
                ThemeData = _theme,
                Colors = colors,
                Relationships = slideRelationships,
                Layout = layout,
                Master = master,
                MasterStyles = master?.Styles,
                SlideWidthDip = _slideWidthDip,
                SlideHeightDip = _slideHeightDip,
            };

            var shapes = PptShapeParser.ParseShapeTree(commonSlideData?.Element(PptXml.P + "spTree"), shapeContext);

            var show = PptXml.ReadBool(PptXml.Attr(slideRoot, "show"));
            var slide = new PptSlide
            {
                Index = index,
                IsHidden = show == false,
                Background = background,
                Shapes = shapes,
            };

            slide.Title = FindTitle(shapes);
            slide.PlainText = BuildPlainText(shapes);
            slide.Notes = ParseNotes(slideRelationships);
            return slide;
        }

        /// <summary>找标题占位符（type = title / ctrTitle）的第一段文字；没有标题占位符返回空串。</summary>
        private static string FindTitle(IReadOnlyList<PptShape> shapes)
        {
            foreach (var shape in shapes)
            {
                var title = FindTitle(shape);
                if (title is not null) return title;
            }

            return "";
        }

        private static string? FindTitle(PptShape shape)
        {
            if (shape is PptGroupShape group)
            {
                foreach (var child in group.Children)
                {
                    var title = FindTitle(child);
                    if (title is not null) return title;
                }

                return null;
            }

            if (shape is not PptTextShape textShape || !textShape.IsPlaceholder) return null;
            var type = textShape.PlaceholderType.Trim().ToLowerInvariant();
            if (type is not ("title" or "ctrtitle")) return null;

            var paragraphs = textShape.Text.Paragraphs;
            foreach (var paragraph in paragraphs)
            {
                if (!string.IsNullOrWhiteSpace(paragraph.Text)) return paragraph.Text.Trim();
            }

            return "";
        }

        /// <summary>按形状顺序收集所有可见文字（含表格单元格、组合里的子形状），段间换行。</summary>
        private static string BuildPlainText(IReadOnlyList<PptShape> shapes)
        {
            var pieces = new List<string>();

            void Collect(PptShape shape)
            {
                switch (shape)
                {
                    case PptTextShape textShape:
                    {
                        var text = textShape.Text.Text;
                        if (!string.IsNullOrWhiteSpace(text)) pieces.Add(text);
                        break;
                    }

                    case PptTableShape table:
                    {
                        foreach (var row in table.Rows)
                        {
                            foreach (var cell in row.Cells)
                            {
                                if (cell.Text is null || !cell.Text.HasText) continue;
                                pieces.Add(cell.Text.Text);
                            }
                        }

                        break;
                    }

                    case PptGroupShape group:
                    {
                        foreach (var child in group.Children) Collect(child);
                        break;
                    }
                }
            }

            foreach (var shape in shapes) Collect(shape);
            return string.Join(Environment.NewLine, pieces);
        }

        /// <summary>
        /// 读备注：幻灯片关系表 type 以 /notesSlide 结尾 → notesSlide XML，
        /// 取 <c>p:ph type="body"</c> 的 txBody 文字；多段换行拼接，页码 / 页眉页脚等占位符跳过。
        /// </summary>
        private string ParseNotes(IReadOnlyDictionary<string, OoxmlRelationship> slideRelationships)
        {
            var notesPart = PptXml.FindPartByRelationshipType(slideRelationships, "/notesSlide", _package.Contains);
            if (notesPart.Length == 0) return "";

            var xml = _package.TryReadXml(notesPart);
            var shapeTree = xml?.Root?.Element(PptXml.P + "cSld")?.Element(PptXml.P + "spTree");
            if (shapeTree is null) return "";

            var notesRelationships = PptXml.ReadRelationships(_package, notesPart);
            var colors = new PptColorContext(_theme);
            var textContext = PptTextParser.CreateContext(_theme, colors, notesRelationships, null, "other");

            var notes = new List<string>();
            foreach (var shapeElement in shapeTree.Elements(PptXml.P + "sp"))
            {
                var placeholder = shapeElement.Element(PptXml.P + "nvSpPr")
                    ?.Element(PptXml.P + "nvPr")?.Element(PptXml.P + "ph");
                if (placeholder is null) continue;

                // 只取备注正文；页码 / 页眉 / 页脚 / 日期 / 幻灯片缩略图都跳过
                var type = PptXml.Attr(placeholder, "type").ToLowerInvariant();
                if (type is not ("body" or "notes")) continue;

                try
                {
                    var textBody = PptTextParser.ParseTextBody(
                        shapeElement.Element(PptXml.P + "txBody"), textContext);
                    var text = textBody.Text.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) notes.Add(text);
                }
                catch
                {
                    // 单段备注解析失败：跳过这一段
                }
            }

            return string.Join(Environment.NewLine, notes);
        }

        // ------------------------------------------------------------ 文档属性

        /// <summary>
        /// 读 <c>docProps/core.xml</c>（标题 / 主题 / 作者 / 最后修改者 / 创建修改时间）与
        /// <c>docProps/app.xml</c>（Company）。部件缺失 / 坏 XML 时保持空值，不影响打开。
        /// </summary>
        private PptCoreProperties ReadProperties()
        {
            var properties = new PptCoreProperties();

            var core = _package.TryReadXml("docProps/core.xml");
            if (core?.Root is not null)
            {
                string ValueOf(string localName) => core.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == localName)?.Value?.Trim() ?? "";

                properties.Title = ValueOf("title");
                properties.Subject = ValueOf("subject");
                properties.Author = ValueOf("creator");
                properties.LastModifiedBy = ValueOf("lastModifiedBy");
                properties.CreatedUtc = ParseUtcDate(ValueOf("created"));
                properties.ModifiedUtc = ParseUtcDate(ValueOf("modified"));
            }

            var app = _package.TryReadXml("docProps/app.xml");
            if (app?.Root is not null)
            {
                properties.Company = app.Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Company")?.Value?.Trim() ?? "";
            }

            return properties;
        }

        /// <summary>把 W3CDTF 时间解析成 UTC；格式不认识返回 null（界面就不显示时间）。</summary>
        private static DateTime? ParseUtcDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTime.TryParse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                    | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed.ToUniversalTime();
            }

            return null;
        }

        // ------------------------------------------------------------ 小工具

        /// <summary>按约定目录 / 前缀猜部件名（<c>theme1.xml</c> ~ <c>themeN.xml</c> 之类）。</summary>
        private string GuessPart(string directory, string fileNamePrefix)
        {
            for (var index = 1; index <= 9; index++)
            {
                var candidate = $"{directory}/{fileNamePrefix}{index}.xml";
                if (_package.Contains(candidate)) return candidate;
            }

            return "";
        }
    }
}
