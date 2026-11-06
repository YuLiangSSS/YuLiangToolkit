using System.Globalization;
using System.Windows.Media;
using System.Xml.Linq;
using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

/// <summary>
/// PPTX 解析共用的小工具：OOXML 命名空间、属性读取、颜色 / 几何 / 内容类型映射、图片字节缓存。
/// <para>这里只放「没有业务含义」的纯工具；主题色在 <see cref="PptThemeParser"/> 解析，
/// 文字在 <see cref="PptTextParser"/> 解析，形状在 <see cref="PptShapeParser"/> 解析。</para>
/// <para>所有读取方法都是宽容的：属性缺失 / 格式不对返回 null 或默认值，绝不抛异常
/// （坏文件要靠调用方降级，而不是让一份 PPT 打不开）。</para>
/// </summary>
internal static class PptXml
{
    /// <summary>PresentationML 主命名空间（presentation.xml / slide / layout / master / notes）。</summary>
    public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>DrawingML 主命名空间（形状属性 / 文字 / 颜色 / 表格）。</summary>
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>关系引用命名空间（<c>r:id</c> / <c>r:embed</c>）。</summary>
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>Markup Compatibility 命名空间（<c>mc:AlternateContent</c>）。</summary>
    public static readonly XNamespace MC = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    /// <summary>图表命名空间（识别图表占位用）。</summary>
    public static readonly XNamespace C = "http://schemas.openxmlformats.org/drawingml/2006/chart";

    /// <summary>SmartArt 命名空间（识别 SmartArt 占位用）。</summary>
    public static readonly XNamespace DGM = "http://schemas.openxmlformats.org/drawingml/2006/diagram";

    /// <summary>Excel 命名空间（图表引用 <c>c:chart</c> 的子元素识别用）。</summary>
    public static readonly XNamespace X = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    // ---------------------------------------------------------------- 属性读取

    /// <summary>读无命名空间属性（name / id / type / idx / rot / flipH / cx …），去掉首尾空白。</summary>
    public static string Attr(XElement? element, string name)
        => ((string?)element?.Attribute(name))?.Trim() ?? "";

    /// <summary>读带命名空间的属性（例如 <c>r:id</c>），去掉首尾空白。</summary>
    public static string Attr(XElement? element, XNamespace ns, string name)
        => ((string?)element?.Attribute(ns + name))?.Trim() ?? "";

    /// <summary>读无命名空间整数属性；缺失 / 非法返回 null。</summary>
    public static int? IntAttr(XElement? element, string name) => ReadInt((string?)element?.Attribute(name));

    /// <summary>读无命名空间长整数属性；缺失 / 非法返回 null。</summary>
    public static long? LongAttr(XElement? element, string name) => ReadLong((string?)element?.Attribute(name));

    /// <summary>读无命名空间小数值属性（一般不会有，兜底用）；缺失 / 非法返回 null。</summary>
    public static double? DoubleAttr(XElement? element, string name) => ReadDouble((string?)element?.Attribute(name));

    /// <summary>读无命名空间布尔属性：缺失返回 null；<c>1/true/on</c> = true，<c>0/false/off</c> = false。</summary>
    public static bool? BoolAttr(XElement? element, string name)
    {
        var value = (string?)element?.Attribute(name);
        return ReadBool(value);
    }

    /// <summary>读「出现即开」的开关属性（例如 <c>rot="0"</c> 仍是 0 度，但 <c>flipH="0"</c> 是关）。</summary>
    public static bool OnOffAttr(XElement? element, string name)
    {
        var value = (string?)element?.Attribute(name);
        if (value is null) return false;
        return ReadBool(value) ?? true;
    }

    /// <summary>OOXML 的开关元素（<c>a:buNone</c> 这类）：元素缺失 = 关；元素存在且 <c>val</c> 不是假值 = 开。</summary>
    public static bool GetOnOff(XElement? element)
    {
        if (element is null) return false;
        var value = (string?)element.Attribute("val");
        if (string.IsNullOrEmpty(value)) return true;
        return ReadBool(value) ?? true;
    }

    /// <summary>读整数文本；失败返回 null。</summary>
    public static int? ReadInt(string? text)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>读长整数文本（EMU 可能超过 int）；失败返回 null。</summary>
    public static long? ReadLong(string? text)
        => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>读小数文本；失败返回 null。</summary>
    public static double? ReadDouble(string? text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    /// <summary>读布尔文本：<c>1/true/on</c> = true，<c>0/false/off/none</c> = false，其它 / 空 = null。</summary>
    public static bool? ReadBool(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "on" or "yes" => true,
            "0" or "false" or "off" or "no" or "none" => false,
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 单位换算

    /// <summary>EMU → DIP；非法值按 0 处理。</summary>
    public static double EmuToDip(long? emu) => OoxmlPackage.EmuToDip(emu ?? 0);

    /// <summary>角度（OOXML 的 1/60000 度）→ 0~360 度。</summary>
    public static double RotationDegrees(long? sixtyThousandths)
    {
        var degrees = (sixtyThousandths ?? 0) / 60000.0;
        return NormalizeDegrees(degrees);
    }

    /// <summary>把任意角度折算到 0 ~ 360。</summary>
    public static double NormalizeDegrees(double degrees)
    {
        if (double.IsNaN(degrees) || double.IsInfinity(degrees)) return 0;
        degrees %= 360.0;
        if (degrees < 0) degrees += 360.0;
        return degrees;
    }

    // ---------------------------------------------------------------- 颜色

    /// <summary>解析 <c>RRGGBB</c> / <c>AARRGGBB</c> / <c>#RRGGBB</c>；失败返回 null。</summary>
    public static Color? ParseHexColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 8)
        {
            // OOXML 的 8 位颜色通常是 AARRGGBB（前两位 alpha），和 WPF 的 #AARRGGBB 一致
            if (!value.All(Uri.IsHexDigit)) return null;
            var a = Convert.ToInt32(value[..2], 16);
            var r = Convert.ToInt32(value.Substring(2, 2), 16);
            var g = Convert.ToInt32(value.Substring(4, 2), 16);
            var b = Convert.ToInt32(value.Substring(6, 2), 16);
            return Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);
        }

        if (value.Length != 6 || !value.All(Uri.IsHexDigit)) return null;
        var red = Convert.ToInt32(value[..2], 16);
        var green = Convert.ToInt32(value.Substring(2, 2), 16);
        var blue = Convert.ToInt32(value.Substring(4, 2), 16);
        return Color.FromRgb((byte)red, (byte)green, (byte)blue);
    }

    /// <summary>常用预设颜色名（<c>a:prstClr val</c>）；不认识的返回 null。</summary>
    public static Color? ParsePresetColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "black" => Color.FromRgb(0x00, 0x00, 0x00),
            "white" => Color.FromRgb(0xFF, 0xFF, 0xFF),
            "red" => Color.FromRgb(0xFF, 0x00, 0x00),
            "green" => Color.FromRgb(0x00, 0x80, 0x00),
            "blue" => Color.FromRgb(0x00, 0x00, 0xFF),
            "yellow" => Color.FromRgb(0xFF, 0xFF, 0x00),
            "orange" => Color.FromRgb(0xFF, 0xA5, 0x00),
            "purple" => Color.FromRgb(0x80, 0x00, 0x80),
            "cyan" or "aqua" => Color.FromRgb(0x00, 0xFF, 0xFF),
            "magenta" or "fuchsia" => Color.FromRgb(0xFF, 0x00, 0xFF),
            "gray" or "grey" => Color.FromRgb(0x80, 0x80, 0x80),
            "ltgray" or "lightgray" or "lightgrey" => Color.FromRgb(0xD3, 0xD3, 0xD3),
            "dkgray" or "darkgray" or "darkgrey" => Color.FromRgb(0xA9, 0xA9, 0xA9),
            "brown" => Color.FromRgb(0xA5, 0x2A, 0x2A),
            "pink" => Color.FromRgb(0xFF, 0xC0, 0xCB),
            "gold" => Color.FromRgb(0xFF, 0xD7, 0x00),
            "silver" => Color.FromRgb(0xC0, 0xC0, 0xC0),
            "navy" => Color.FromRgb(0x00, 0x00, 0x80),
            "teal" => Color.FromRgb(0x00, 0x80, 0x80),
            "olive" => Color.FromRgb(0x80, 0x80, 0x00),
            "lime" => Color.FromRgb(0x00, 0xFF, 0x00),
            "maroon" => Color.FromRgb(0x80, 0x00, 0x00),
            "beige" => Color.FromRgb(0xF5, 0xF5, 0xDC),
            "ivory" => Color.FromRgb(0xFF, 0xFF, 0xF0),
            "khaki" => Color.FromRgb(0xF0, 0xE6, 0x8C),
            "coral" => Color.FromRgb(0xFF, 0x7F, 0x50),
            "salmon" => Color.FromRgb(0xFA, 0x80, 0x72),
            "tan" => Color.FromRgb(0xD2, 0xB4, 0x8C),
            "violet" => Color.FromRgb(0xEE, 0x82, 0xEE),
            "indigo" => Color.FromRgb(0x4B, 0x00, 0x82),
            "plum" => Color.FromRgb(0xDD, 0xA0, 0xDD),
            "orchid" => Color.FromRgb(0xDA, 0x70, 0xD6),
            "turquoise" => Color.FromRgb(0x40, 0xE0, 0xD0),
            "skyblue" => Color.FromRgb(0x87, 0xCE, 0xEB),
            "steelblue" => Color.FromRgb(0x46, 0x82, 0xB4),
            "royalblue" => Color.FromRgb(0x41, 0x69, 0xE1),
            "forestgreen" => Color.FromRgb(0x22, 0x8B, 0x22),
            "seagreen" => Color.FromRgb(0x2E, 0x8B, 0x57),
            "darkblue" => Color.FromRgb(0x00, 0x00, 0x8B),
            "darkred" => Color.FromRgb(0x8B, 0x00, 0x00),
            "darkgreen" => Color.FromRgb(0x00, 0x64, 0x00),
            "darkcyan" => Color.FromRgb(0x00, 0x8B, 0x8B),
            "darkmagenta" => Color.FromRgb(0x8B, 0x00, 0x8B),
            "darkyellow" => Color.FromRgb(0x80, 0x80, 0x00),
            "darkorange" => Color.FromRgb(0xFF, 0x8C, 0x00),
            "lightblue" => Color.FromRgb(0xAD, 0xD8, 0xE6),
            "lightgreen" => Color.FromRgb(0x90, 0xEE, 0x90),
            "lightyellow" => Color.FromRgb(0xFF, 0xFF, 0xE0),
            "lightpink" => Color.FromRgb(0xFF, 0xB6, 0xC1),
            "whitesmoke" => Color.FromRgb(0xF5, 0xF5, 0xF5),
            "gainsboro" => Color.FromRgb(0xDC, 0xDC, 0xDC),
            _ => null,
        };
    }

    /// <summary>系统颜色名（<c>a:sysClr val</c>，没有 <c>lastClr</c> 时的兜底）；不认识返回 null。</summary>
    public static Color? ParseSystemColor(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return name.Trim().ToLowerInvariant() switch
        {
            "windowtext" or "btntext" or "menutext" or "captiontext" or "infotext"
                or "blacktext" or "graytext" or "hotlight" or "activecaption" => Color.FromRgb(0x00, 0x00, 0x00),
            "window" or "btnface" or "btnhighlight" or "btnlight" or "menu" or "background"
                or "infobk" or "activecaptiontext" => Color.FromRgb(0xFF, 0xFF, 0xFF),
            "highlight" or "menuhighlight" or "activetitle" or "gradientactivecaption" => Color.FromRgb(0x00, 0x00, 0x80),
            "highlighttext" or "menuhighlighttext" or "captiontextactive"
                or "activetitletext" or "gradientactivetitletext" => Color.FromRgb(0xFF, 0xFF, 0xFF),
            "inactivecaption" or "inactiveborder" or "graytextactive" => Color.FromRgb(0x80, 0x80, 0x80),
            "appworkspace" or "scrollbar" => Color.FromRgb(0xC0, 0xC0, 0xC0),
            _ => null,
        };
    }

    // ---------------------------------------------------------------- 几何 / 内容类型

    /// <summary>
    /// <c>a:prstGeom prst</c> → <see cref="PptGeometryKind"/>。
    /// 覆盖矩形 / 圆角矩形 / 椭圆 / 直线 / 箭头 / 三角 / 菱形 / 平行四边形 / 梯形 / 五边形 / 六边形 / 星形，
    /// 其它（流程图形状、标注等）返回 <see cref="PptGeometryKind.Unknown"/>，渲染器按矩形兜底。
    /// </summary>
    public static PptGeometryKind MapGeometry(string? preset)
    {
        if (string.IsNullOrWhiteSpace(preset)) return PptGeometryKind.Unknown;
        return preset.Trim().ToLowerInvariant() switch
        {
            "rect" => PptGeometryKind.Rectangle,
            "roundrect" or "round2samerect" or "round2diagrect" => PptGeometryKind.RoundedRectangle,
            "ellipse" or "circle" or "donut" => PptGeometryKind.Ellipse,
            "line" or "straightconnector1" or "straightconnector2" or "straightconnector3"
                or "bentconnector2" or "bentconnector3" or "bentconnector4" or "bentconnector5"
                or "curvedconnector2" or "curvedconnector3" or "curvedconnector4" or "curvedconnector5"
                => PptGeometryKind.Line,
            "rightarrow" or "leftarrow" or "uparrow" or "downarrow" or "leftrightarrow"
                or "updownarrow" or "quadarrow" or "stripedrightarrow"
                => PptGeometryKind.Arrow,
            "triangle" or "rtriangle" => PptGeometryKind.Triangle,
            "diamond" => PptGeometryKind.Diamond,
            "parallelogram" => PptGeometryKind.Parallelogram,
            "trapezoid" or "nonisoscelesTrapezoid" => PptGeometryKind.Trapezoid,
            "pentagon" or "homeplate" => PptGeometryKind.Pentagon,
            "hexagon" => PptGeometryKind.Hexagon,
            "star4" or "star5" or "star6" or "star7" or "star8" or "star10" or "star12" or "star16"
                or "star24" or "star32" => PptGeometryKind.Star,
            _ => PptGeometryKind.Unknown,
        };
    }

    /// <summary>图片部件扩展名 → MIME 内容类型（拿不到就空串，渲染器自己嗅探）。</summary>
    public static string ContentTypeFromPartName(string partName)
    {
        var extension = System.IO.Path.GetExtension(partName);
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" or ".dib" => "image/bmp",
            ".tif" or ".tiff" => "image/tiff",
            ".svg" => "image/svg+xml",
            ".emf" => "image/x-emf",
            ".wmf" => "image/x-wmf",
            ".ico" => "image/x-icon",
            ".webp" => "image/webp",
            _ => "",
        };
    }

    // ---------------------------------------------------------------- 关系表

    /// <summary>
    /// 读某个部件自己的关系表（键 = 关系 Id）。关系部件缺失 / XML 损坏时返回空表，
    /// 不抛异常 —— 图片 / 超链接解析不到只是降级，不该让整份 PPT 打不开。
    /// </summary>
    public static Dictionary<string, OoxmlRelationship> ReadRelationships(OoxmlPackage package, string partName)
    {
        var map = new Dictionary<string, OoxmlRelationship>(StringComparer.Ordinal);
        try
        {
            foreach (var relationship in package.ReadRelationships(partName))
                map.TryAdd(relationship.Id, relationship);
        }
        catch (OoxmlException)
        {
            // 关系文件缺失 / 坏 XML：返回空表
        }

        return map;
    }

    /// <summary>
    /// 在关系表里找一个「类型以 <paramref name="typeSuffix"/> 结尾、包内存在」的部件名；
    /// 找不到返回空串。
    /// </summary>
    public static string FindPartByRelationshipType(
        IReadOnlyDictionary<string, OoxmlRelationship> relationships,
        string typeSuffix,
        Func<string, bool>? exists = null)
    {
        foreach (var relationship in relationships.Values)
        {
            if (relationship.IsExternal) continue;
            if (!relationship.Type.EndsWith(typeSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(relationship.Target)) continue;
            if (exists is not null && !exists(relationship.Target)) continue;
            return relationship.Target;
        }

        return "";
    }
}

/// <summary>解析阶段读到内存里的一份图片（同一部件只读一次、字节数组在模型里共享）。</summary>
internal sealed class PptImage
{
    public PptImage(byte[] bytes, string contentType)
    {
        Bytes = bytes;
        ContentType = contentType;
    }

    /// <summary>原始字节（PNG / JPEG / GIF / BMP…）。</summary>
    public byte[] Bytes { get; }

    /// <summary>MIME 内容类型（可能为空串）。</summary>
    public string ContentType { get; }
}

/// <summary>
/// 图片字节缓存：按部件名去重（同一张图被多页 / 多个形状引用时只解压一次，
/// 模型里的 <c>ImageBytes</c> 也指向同一个数组，符合「图片字节只在模型里存一份」的约定）。
/// </summary>
internal sealed class PptImageCache
{
    private readonly OoxmlPackage _package;
    private readonly Dictionary<string, PptImage?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public PptImageCache(OoxmlPackage package)
    {
        _package = package;
    }

    /// <summary>按部件名读图片；部件缺失 / 损坏 / 空内容返回 null（渲染占位框）。</summary>
    public PptImage? Read(string? partName)
    {
        var name = OoxmlPackage.NormalizePartName(partName);
        if (name.Length == 0) return null;
        if (_cache.TryGetValue(name, out var cached)) return cached;

        PptImage? image = null;
        try
        {
            if (_package.Contains(name))
            {
                var bytes = _package.ReadBytes(name);
                if (bytes.Length > 0) image = new PptImage(bytes, PptXml.ContentTypeFromPartName(name));
            }
        }
        catch (OoxmlException)
        {
            // 图片部件损坏：这一张跳过，不影响整页
            image = null;
        }

        _cache[name] = image;
        return image;
    }
}
