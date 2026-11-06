using System.Windows.Media;
using System.Xml.Linq;

namespace YuLiangToolkit.Tools.PptViewer.Parsing;

/// <summary>
/// 解析阶段用到的主题数据：模型里的 <see cref="PptTheme"/>（颜色 / 字体）+
/// 主题格式方案（<c>a:fmtScheme</c> 的填充 / 线条样式列表，形状 <c>p:style</c> / 背景 <c>p:bgRef</c> 引用）。
/// <para>格式方案只给解析器内部用，不进入模型 —— 颜色已经代入各形状，渲染器不需要再查主题。</para>
/// </summary>
internal sealed class PptThemeData
{
    /// <summary>主题颜色 / 字体（会直接放进 <see cref="PptPresentation.Theme"/>）。</summary>
    public PptTheme Theme { get; } = PptTheme.CreateDefault();

    /// <summary><c>a:fmtScheme/a:fillStyleLst</c> 的子元素（形状 <c>a:fillRef idx</c> 引用）。</summary>
    public List<XElement> FillStyles { get; } = new();

    /// <summary><c>a:fmtScheme/a:lnStyleLst</c> 的子元素（形状 <c>a:lnRef idx</c> 引用）。</summary>
    public List<XElement> LineStyles { get; } = new();

    /// <summary><c>a:fmtScheme/a:bgFillStyleLst</c> 的子元素（背景 <c>p:bgRef idx</c> 引用）。</summary>
    public List<XElement> BackgroundFillStyles { get; } = new();
}

/// <summary>
/// 颜色解析上下文：主题色方案 + 颜色映射（母版 <c>p:clrMap</c> 的 bg1/tx1/bg2/tx2 映射）
/// + <c>phClr</c> 占位色（主题格式方案里的占位颜色，形状样式引用 / 背景引用时替换）。
/// </summary>
internal sealed class PptColorContext
{
    /// <summary>默认映射：背景 1 → lt1、文字 1 → dk1、背景 2 → lt2、文字 2 → dk2。</summary>
    private static readonly IReadOnlyDictionary<string, string> DefaultMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bg1"] = "lt1",
            ["tx1"] = "dk1",
            ["bg2"] = "lt2",
            ["tx2"] = "dk2",
        };

    public PptColorContext(
        PptThemeData themeData,
        IReadOnlyDictionary<string, string>? colorMap = null,
        Color? placeholderColor = null)
    {
        ThemeData = themeData;
        ColorMap = colorMap ?? DefaultMap;
        PlaceholderColor = placeholderColor ?? Color.FromRgb(0x44, 0x72, 0xC4); // 默认 accent1
    }

    /// <summary>主题数据。</summary>
    public PptThemeData ThemeData { get; }

    /// <summary>颜色映射（键 / 值都是小写 scheme 名，例如 tx1 → dk1）。</summary>
    public IReadOnlyDictionary<string, string> ColorMap { get; }

    /// <summary>主题格式方案里 <c>phClr</c> 要替换成的实际颜色。</summary>
    public Color PlaceholderColor { get; }

    /// <summary>换一个 <c>phClr</c> 占位色（解析主题填充 / 线条样式时用）。</summary>
    public PptColorContext WithPlaceholder(Color placeholderColor)
        => new(ThemeData, ColorMap, placeholderColor);

    /// <summary>解析一个颜色元素（<c>a:srgbClr</c> / <c>a:schemeClr</c> / …）；拿不到时返回 <paramref name="fallback"/>。</summary>
    public Color Resolve(XElement? colorElement, Color fallback)
        => PptThemeParser.ResolveColor(colorElement, this, fallback);
}

/// <summary>
/// 主题解析：<c>a:clrScheme</c>（颜色方案）/ <c>a:fontScheme</c>（字体方案）/
/// <c>a:fmtScheme</c>（填充与线条样式），以及主题色的 <c>schemeClr</c> 解析与颜色变换（lumMod / tint / shade…）。
/// </summary>
internal static class PptThemeParser
{
    /// <summary>解析 <c>ppt/theme/themeN.xml</c>；XML 为空 / 缺部件时返回一份默认主题数据。</summary>
    public static PptThemeData Parse(XDocument? themeXml)
    {
        var data = new PptThemeData();
        var root = themeXml?.Root;
        if (root is null) return data;

        var themeElements = root.Element(PptXml.A + "themeElements");
        if (themeElements is null) return data;

        ParseColorScheme(data, themeElements.Element(PptXml.A + "clrScheme"));
        ParseFontScheme(data, themeElements.Element(PptXml.A + "fontScheme"));
        ParseFormatScheme(data, themeElements.Element(PptXml.A + "fmtScheme"));
        return data;
    }

    /// <summary>解析 <c>a:clrScheme</c>：dk1 / lt1 / dk2 / lt2 / accent1~6 / hlink / folHlink（键统一小写）。</summary>
    private static void ParseColorScheme(PptThemeData data, XElement? colorScheme)
    {
        if (colorScheme is null) return;

        // 颜色方案里的条目本身可以是 srgbClr / sysClr / prstClr，直接用默认上下文解析
        var context = new PptColorContext(data);
        foreach (var entry in colorScheme.Elements())
        {
            var key = entry.Name.LocalName.ToLowerInvariant();
            if (key is not ("dk1" or "lt1" or "dk2" or "lt2"
                or "accent1" or "accent2" or "accent3" or "accent4" or "accent5" or "accent6"
                or "hlink" or "folhlink"))
            {
                continue;
            }

            var colorElement = entry.Elements().FirstOrDefault();
            if (colorElement is null) continue;

            // 注意 folHlink 键在主题里是 folHlink，比对小写后统一成 folhlink
            var normalizedKey = key == "folhlink" ? "folHlink" : key;
            data.Theme.Colors[normalizedKey] =
                ResolveColor(colorElement, context, data.Theme.GetColor(normalizedKey, Colors.Black));
        }
    }

    /// <summary>解析 <c>a:fontScheme</c>：majorFont / minorFont 的 <c>a:latin</c> 与 <c>a:ea</c>。</summary>
    private static void ParseFontScheme(PptThemeData data, XElement? fontScheme)
    {
        if (fontScheme is null) return;

        var major = fontScheme.Element(PptXml.A + "majorFont");
        var minor = fontScheme.Element(PptXml.A + "minorFont");

        var majorLatin = PptXml.Attr(major?.Element(PptXml.A + "latin"), "typeface");
        if (!string.IsNullOrWhiteSpace(majorLatin)) data.Theme.MajorFont = majorLatin;

        var minorLatin = PptXml.Attr(minor?.Element(PptXml.A + "latin"), "typeface");
        if (!string.IsNullOrWhiteSpace(minorLatin)) data.Theme.MinorFont = minorLatin;

        // 模型约定：只认 a:ea；没有就保持空串（文字解析阶段再用默认中文字体兜底）
        data.Theme.MajorEastAsianFont = PptXml.Attr(major?.Element(PptXml.A + "ea"), "typeface");
        data.Theme.MinorEastAsianFont = PptXml.Attr(minor?.Element(PptXml.A + "ea"), "typeface");
    }

    /// <summary>解析 <c>a:fmtScheme</c>：填充 / 线条 / 背景填充样式列表（形状 <c>p:style</c>、背景 <c>p:bgRef</c> 用）。</summary>
    private static void ParseFormatScheme(PptThemeData data, XElement? formatScheme)
    {
        if (formatScheme is null) return;

        AddChildren(data.FillStyles, formatScheme.Element(PptXml.A + "fillStyleLst"));
        AddChildren(data.LineStyles, formatScheme.Element(PptXml.A + "lnStyleLst"));
        AddChildren(data.BackgroundFillStyles, formatScheme.Element(PptXml.A + "bgFillStyleLst"));
    }

    private static void AddChildren(List<XElement> target, XElement? parent)
    {
        if (parent is null) return;
        foreach (var child in parent.Elements()) target.Add(child);
    }

    /// <summary>
    /// 解析母版 / 版式 / 幻灯片上的颜色映射（<c>p:clrMap</c> 或 <c>a:overrideClrMapping</c>）。
    /// 缺失时返回 null（调用方用默认映射）。
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseColorMap(XElement? colorMapElement)
    {
        if (colorMapElement is null) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in colorMapElement.Attributes())
        {
            var name = attribute.Name.LocalName;
            var value = attribute.Value?.Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;
            map[name] = value;
        }

        return map.Count == 0 ? null : map;
    }

    /// <summary>
    /// 解析一个 DrawingML 颜色元素（<c>a:srgbClr</c> / <c>a:schemeClr</c> / <c>a:sysClr</c> /
    /// <c>a:prstClr</c> / <c>a:scrgbClr</c> / <c>a:hslClr</c>）并应用子元素的颜色变换。
    /// 不认识的颜色返回 <paramref name="fallback"/>。
    /// </summary>
    public static Color ResolveColor(XElement? colorElement, PptColorContext context, Color fallback)
    {
        if (colorElement is null) return fallback;

        Color color;
        switch (colorElement.Name.LocalName)
        {
            case "srgbClr":
                color = PptXml.ParseHexColor(PptXml.Attr(colorElement, "val")) ?? fallback;
                break;

            case "schemeClr":
            {
                var name = PptXml.Attr(colorElement, "val").ToLowerInvariant();
                if (name.Length == 0)
                {
                    color = fallback;
                }
                else if (name == "phclr")
                {
                    color = context.PlaceholderColor;
                }
                else
                {
                    var key = context.ColorMap.TryGetValue(name, out var mapped) ? mapped : name;
                    color = context.ThemeData.Theme.GetColor(key, fallback);
                }

                break;
            }

            case "sysClr":
            {
                // sysClr 一般带 lastClr（实际 RGB），没有就按系统色名兜底
                color = PptXml.ParseHexColor(PptXml.Attr(colorElement, "lastClr"))
                    ?? PptXml.ParseSystemColor(PptXml.Attr(colorElement, "val"))
                    ?? fallback;
                break;
            }

            case "prstClr":
                color = PptXml.ParsePresetColor(PptXml.Attr(colorElement, "val")) ?? fallback;
                break;

            case "scrgbClr":
            {
                // 线性 scRGB：r / g / b 是 0 ~ 100000 的比例
                var red = RatioToByte(PptXml.IntAttr(colorElement, "r"));
                var green = RatioToByte(PptXml.IntAttr(colorElement, "g"));
                var blue = RatioToByte(PptXml.IntAttr(colorElement, "b"));
                color = Color.FromArgb(AlphaFrom(PptXml.IntAttr(colorElement, "a")), red, green, blue);
                break;
            }

            case "hslClr":
            {
                // hue 单位 1/60000 度；sat / lum 是 0 ~ 100000 的比例
                var hue = (PptXml.DoubleAttr(colorElement, "hue") ?? 0) / 60000.0 / 360.0;
                var saturation = Clamp01((PptXml.DoubleAttr(colorElement, "sat") ?? 0) / 100000.0);
                var luminance = Clamp01((PptXml.DoubleAttr(colorElement, "lum") ?? 0) / 100000.0);
                var (r, g, b) = HslToRgb(hue, saturation, luminance);
                color = Color.FromArgb(AlphaFrom(PptXml.IntAttr(colorElement, "a")), r, g, b);
                break;
            }

            default:
                return fallback;
        }

        return ApplyTransforms(color, colorElement);
    }

    /// <summary>
    /// 应用颜色变换子元素：lumMod / lumOff / satMod / satOff / tint / shade / hueMod / alpha。
    /// 只做近似（在 HSL 空间调整），做不到的变换直接忽略，不抛异常。
    /// </summary>
    private static Color ApplyTransforms(Color color, XElement colorElement)
    {
        if (!colorElement.HasElements) return color;

        var (hue, saturation, luminance) = RgbToHsl(color);
        var alpha = color.A / 255.0;
        var changed = false;

        foreach (var transform in colorElement.Elements())
        {
            var value = PptXml.ReadDouble(PptXml.Attr(transform, "val")) ?? 0;
            var ratio = value / 100000.0;

            switch (transform.Name.LocalName)
            {
                case "lumMod":
                    luminance = Clamp01(luminance * ratio);
                    changed = true;
                    break;

                case "lumOff":
                    luminance = Clamp01(luminance + ratio);
                    changed = true;
                    break;

                case "satMod":
                    saturation = Clamp01(saturation * ratio);
                    changed = true;
                    break;

                case "satOff":
                    saturation = Clamp01(saturation + ratio);
                    changed = true;
                    break;

                case "hueMod":
                    if (ratio > 0)
                    {
                        hue = (hue * ratio) % 1.0;
                        changed = true;
                    }

                    break;

                case "hueOff":
                    hue = (hue + value / 60000.0 / 360.0) % 1.0;
                    changed = true;
                    break;

                case "tint":
                    // 向白色混合：val = 100000 时保持原色，越小越白
                    luminance = Clamp01(luminance + (1 - luminance) * (1 - Clamp01(ratio)));
                    changed = true;
                    break;

                case "shade":
                    // 向黑色混合：val = 100000 时保持原色，越小越暗
                    luminance = Clamp01(luminance * Clamp01(ratio));
                    changed = true;
                    break;

                case "alpha":
                    alpha = Clamp01(alpha * ratio);
                    changed = true;
                    break;
            }
        }

        if (!changed) return color;

        var (red, green, blue) = HslToRgb(hue, saturation, luminance);
        return Color.FromArgb((byte)Math.Round(alpha * 255), red, green, blue);
    }

    /// <summary>
    /// 解析字体引用：支持 <c>+mj-lt</c> / <c>+mn-lt</c> / <c>+mj-ea</c> / <c>+mn-ea</c> 这类主题字体引用；
    /// 普通字体名原样返回；空串返回空串。东亚字体缺失时退回对应的拉丁字体。
    /// </summary>
    public static string ResolveThemeFont(string? typeface, PptThemeData theme, bool eastAsian)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return "";
        var value = typeface.Trim();
        if (!value.StartsWith('+')) return value;

        var reference = value.ToLowerInvariant();
        var major = reference.Contains("mj", StringComparison.Ordinal);
        var eastern = eastAsian
            || reference.EndsWith("-ea", StringComparison.Ordinal)
            || reference.EndsWith("-cs", StringComparison.Ordinal);

        if (eastern)
        {
            var eastFont = major ? theme.Theme.MajorEastAsianFont : theme.Theme.MinorEastAsianFont;
            if (!string.IsNullOrWhiteSpace(eastFont)) return eastFont;
        }

        var latinFont = major ? theme.Theme.MajorFont : theme.Theme.MinorFont;
        if (!string.IsNullOrWhiteSpace(latinFont)) return latinFont;
        return PptDefaults.FontFamily;
    }

    /// <summary>读一组文字属性里的 <c>a:latin</c> / <c>a:ea</c> 字体（已经解析主题引用）。</summary>
    public static (string Latin, string EastAsian) ReadFonts(XElement? runProperties, PptThemeData theme)
    {
        if (runProperties is null) return ("", "");

        var latin = ResolveThemeFont(
            PptXml.Attr(runProperties.Element(PptXml.A + "latin"), "typeface"), theme, eastAsian: false);
        var eastAsian = ResolveThemeFont(
            PptXml.Attr(runProperties.Element(PptXml.A + "ea"), "typeface"), theme, eastAsian: true);
        return (latin, eastAsian);
    }

    // ---------------------------------------------------------------- 颜色空间小工具

    private static byte RatioToByte(int? value)
        => (byte)Math.Clamp(Math.Round((value ?? 0) / 100000.0 * 255), 0, 255);

    private static byte AlphaFrom(int? value)
        => value.HasValue ? (byte)Math.Clamp(Math.Round(value.Value / 100000.0 * 255), 0, 255) : (byte)255;

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    /// <summary>RGB → HSL（H / S / L 都是 0 ~ 1）。</summary>
    private static (double Hue, double Saturation, double Luminance) RgbToHsl(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var luminance = (max + min) / 2.0;

        if (Math.Abs(max - min) < 0.00001) return (0, 0, luminance);

        var delta = max - min;
        var saturation = luminance > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);

        double hue;
        if (Math.Abs(max - r) < 0.00001) hue = (g - b) / delta + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 0.00001) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;

        hue /= 6.0;
        return (hue, saturation, luminance);
    }

    /// <summary>HSL → RGB（H / S / L 都是 0 ~ 1）。</summary>
    private static (byte Red, byte Green, byte Blue) HslToRgb(double hue, double saturation, double luminance)
    {
        if (saturation <= 0)
        {
            var gray = ToByte(luminance);
            return (gray, gray, gray);
        }

        var q = luminance < 0.5
            ? luminance * (1 + saturation)
            : luminance + saturation - luminance * saturation;
        var p = 2 * luminance - q;

        return (ToByte(HueToRgb(p, q, hue + 1.0 / 3.0)),
            ToByte(HueToRgb(p, q, hue)),
            ToByte(HueToRgb(p, q, hue - 1.0 / 3.0)));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(Math.Round(value * 255), 0, 255);
}
