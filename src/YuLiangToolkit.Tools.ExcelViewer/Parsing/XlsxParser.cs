using System.Globalization;
using System.IO;
using System.Text;
using System.Xml.Linq;
using YuLiangToolkit.Core.Documents;

namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>
/// 手写的 xlsx / xlsm 解析器（<b>只读</b>，永不修改原文件）。
///
/// <para><b>思路</b>：xlsx 本质是「ZIP + 一堆 XML」。ZIP 部件 / 关系 / 安全读 XML 这些容易写错的地方
/// 交给 Core 的 <see cref="OoxmlPackage"/>，这里只负责把
/// <c>workbook.xml</c>（工作表清单）、<c>sharedStrings.xml</c>（文本池）、
/// <c>styles.xml</c>（数字格式 / 字体 / 填充）、<c>worksheets/sheetN.xml</c>（单元格）
/// 拼成界面能直接用的只读数据模型。不引第三方 Excel 库，也不需要装 Excel。</para>
///
/// <para><b>刻意做的取舍</b>：</para>
/// <list type="bullet">
///   <item>公式<b>不算</b>，只显示 Excel 上次保存时写进文件的缓存值（见 <see cref="ExcelCell.Formula"/>）；</item>
///   <item>图表 / 图片 / 批注 / 数据透视表不解析（只影响显示，不影响单元格数据）；</item>
///   <item>超大表按 <see cref="XlsxParseLimits"/> 截断，并在 <see cref="ExcelSheet.TruncationNote"/> 里说明；</item>
///   <item>缺 styles / sharedStrings / rels 等部件时一律用默认值，坏文件才抛 <see cref="OoxmlException"/>。</item>
/// </list>
/// </summary>
public static class XlsxParser
{
    /// <summary>合并单元格最多标记多少个格子的覆盖状态（防止畸形文件里一个「覆盖全表」的合并区域拖死解析）。</summary>
    private const int MergedCellMarkBudget = 2_000_000;

    // ---------------------------------------------------------------- 对外入口

    /// <summary>支持的扩展名：<c>.xlsx</c> / <c>.xlsm</c>（大小写不敏感；<b>.xls 旧格式不支持</b>）。</summary>
    public static bool IsSupportedExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var extension = Path.GetExtension(path.Trim().Trim('"'));
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>解析磁盘上的工作簿（默认上限：5000 行 / 200 列）。</summary>
    public static ExcelWorkbook Parse(string path) => Parse(path, new XlsxParseLimits());

    /// <summary>解析一个流（<b>流的所有权归解析器</b>，解析完会被一并释放；不可定位的流会先拷进内存）。</summary>
    public static ExcelWorkbook Parse(Stream stream) => Parse(stream, new XlsxParseLimits());

    /// <summary>解析磁盘上的工作簿，并指定解析上限。</summary>
    public static ExcelWorkbook Parse(string path, XlsxParseLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (string.IsNullOrWhiteSpace(path)) throw new OoxmlException("没有选择文件");

        using var package = OoxmlPackage.Open(path);
        return ParsePackage(package, limits);
    }

    /// <summary>解析一个流，并指定解析上限（流的所有权归解析器）。</summary>
    public static ExcelWorkbook Parse(Stream stream, XlsxParseLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(stream);

        using var package = OoxmlPackage.Open(stream);
        return ParsePackage(package, limits);
    }

    // ---------------------------------------------------------------- 工作簿级解析

    private static ExcelWorkbook ParsePackage(OoxmlPackage package, XlsxParseLimits limits)
    {
        try
        {
            var workbookXml = package.TryReadXml("xl/workbook.xml")
                ?? throw new OoxmlException(
                    "不是有效的 Excel 工作簿：缺少 xl/workbook.xml（.xls 旧格式不支持，请用 Excel 另存为 .xlsx）");

            var uses1904 = ReadUses1904DateSystem(workbookXml);
            var themeColors = ReadThemeColors(package);
            var styles = ReadStyles(package, themeColors);
            var sharedStrings = ReadSharedStrings(package);
            var relationships = ReadWorkbookRelationships(package);

            var sheets = new List<ExcelSheet>();
            foreach (var sheetElement in workbookXml.Descendants().Where(e => e.Name.LocalName == "sheet"))
            {
                var name = (string?)sheetElement.Attribute("name");
                if (string.IsNullOrWhiteSpace(name)) name = $"工作表{sheets.Count + 1}";

                var state = (string?)sheetElement.Attribute("state") ?? "";
                var isVisible = !state.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                             && !state.Equals("veryHidden", StringComparison.OrdinalIgnoreCase);

                // r:id 在不同命名空间下本地名都叫 id（按本地名匹配，避免写死命名空间前缀）
                var relationshipId = sheetElement.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName == "id")?.Value ?? "";

                var partName = relationships.TryGetValue(relationshipId, out var target) ? target : "";
                var sheet = partName.Length > 0 && package.Contains(partName)
                    ? ReadSheet(package, partName, name, isVisible, limits, styles, sharedStrings, uses1904)
                    : new ExcelSheet { Name = name, IsVisible = isVisible };   // 缺工作表部件：空表，不崩

                sheets.Add(sheet);
            }

            return new ExcelWorkbook { Sheets = sheets, Uses1904DateSystem = uses1904 };
        }
        catch (OoxmlException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OoxmlException($"解析 Excel 文件失败：{ex.Message}", ex);
        }
    }

    private static Dictionary<string, string> ReadWorkbookRelationships(OoxmlPackage package)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in package.ReadRelationships("xl/workbook.xml"))
        {
            if (relationship.IsExternal) continue;
            map[relationship.Id] = relationship.Target;
        }
        return map;
    }

    private static bool ReadUses1904DateSystem(XDocument workbookXml)
    {
        var workbookPr = workbookXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
        return IsOn(workbookPr?.Attribute("date1904"));
    }

    // ---------------------------------------------------------------- 共享字符串

    private static IReadOnlyList<string> ReadSharedStrings(OoxmlPackage package)
    {
        var xml = package.TryReadXml("xl/sharedStrings.xml");
        if (xml is null) return Array.Empty<string>();

        var container = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "sst");
        if (container is null) return Array.Empty<string>();

        var strings = new List<string>();
        foreach (var si in container.Elements().Where(e => e.Name.LocalName == "si"))
        {
            strings.Add(ReadRichText(si));
        }
        return strings;
    }

    /// <summary>
    /// 读一段富文本（<c>si</c> / <c>is</c>）：把里面所有 <c>t</c> 拼起来，
    /// 但跳过 <c>rPh</c>（日文注音）子树 —— 不跳过的话「東京」会显示成「東京とうきょう」。
    /// </summary>
    private static string ReadRichText(XElement? container)
    {
        if (container is null) return "";

        var builder = new StringBuilder();
        foreach (var text in container.Descendants().Where(e => e.Name.LocalName == "t"))
        {
            if (HasPhoneticAncestor(text, container)) continue;
            builder.Append(text.Value);
        }
        return builder.ToString();
    }

    private static bool HasPhoneticAncestor(XElement node, XElement stopAt)
    {
        for (var current = node.Parent; current is not null && current != stopAt; current = current.Parent)
        {
            if (current.Name.LocalName == "rPh") return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- 样式

    /// <summary>字体信息（styles.xml 的 font 列表）。</summary>
    private readonly record struct FontInfo(
        bool Bold, bool Italic, bool Underline, double? SizePt, string? ColorArgb);

    private static List<ExcelCellStyle> ReadStyles(OoxmlPackage package, string?[] themeColors)
    {
        var styles = new List<ExcelCellStyle>();
        var xml = package.TryReadXml("xl/styles.xml");
        if (xml is null)
        {
            styles.Add(ExcelCellStyle.Default);
            return styles;
        }

        // 自定义数字格式：numFmtId → formatCode
        var customFormats = new Dictionary<int, string>();
        var numFmts = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "numFmts");
        if (numFmts is not null)
        {
            foreach (var numFmt in numFmts.Elements().Where(e => e.Name.LocalName == "numFmt"))
            {
                var id = ParseIntAttribute(numFmt.Attribute("numFmtId"));
                var code = (string?)numFmt.Attribute("formatCode");
                if (id is > 0 && code is not null) customFormats[id.Value] = code;
            }
        }

        // 字体
        var fonts = new List<FontInfo>();
        var fontsElement = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "fonts");
        if (fontsElement is not null)
        {
            foreach (var font in fontsElement.Elements().Where(e => e.Name.LocalName == "font"))
            {
                var sizeElement = font.Elements().FirstOrDefault(e => e.Name.LocalName == "sz");
                var sizeText = sizeElement is null ? null : (string?)sizeElement.Attribute("val");
                double? size = double.TryParse(sizeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize)
                    ? parsedSize
                    : null;

                var colorElement = font.Elements().FirstOrDefault(e => e.Name.LocalName == "color");
                fonts.Add(new FontInfo(
                    Bold: IsOn(font.Elements().FirstOrDefault(e => e.Name.LocalName == "b")),
                    Italic: IsOn(font.Elements().FirstOrDefault(e => e.Name.LocalName == "i")),
                    Underline: IsOn(font.Elements().FirstOrDefault(e => e.Name.LocalName == "u")),
                    SizePt: size,
                    ColorArgb: ResolveColor(colorElement, themeColors)));
            }
        }

        // 填充
        var fills = new List<string?>();
        var fillsElement = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "fills");
        if (fillsElement is not null)
        {
            foreach (var fill in fillsElement.Elements().Where(e => e.Name.LocalName == "fill"))
            {
                var patternFill = fill.Elements().FirstOrDefault(e => e.Name.LocalName == "patternFill");
                var patternType = (string?)patternFill?.Attribute("patternType");
                if (patternFill is null
                    || patternType is not null && patternType.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    fills.Add(null);
                    continue;
                }

                // 只取前景色近似「纯色填充」；渐变色 / 图案填充按第一个颜色处理
                var foreground = patternFill.Elements().FirstOrDefault(e => e.Name.LocalName == "fgColor");
                var background = patternFill.Elements().FirstOrDefault(e => e.Name.LocalName == "bgColor");
                fills.Add(ResolveColor(foreground, themeColors) ?? ResolveColor(background, themeColors));
            }
        }

        // 单元格格式（cellXfs）：一个 xf = 一个 ExcelCellStyle（同一样式的格子共享这个实例）
        var cellXfs = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfs is null)
        {
            styles.Add(ExcelCellStyle.Default);
            return styles;
        }

        foreach (var xf in cellXfs.Elements().Where(e => e.Name.LocalName == "xf"))
        {
            var numFmtId = ParseIntAttribute(xf.Attribute("numFmtId")) ?? 0;
            var fontId = ParseIntAttribute(xf.Attribute("fontId")) ?? 0;
            var fillId = ParseIntAttribute(xf.Attribute("fillId")) ?? 0;

            var font = fontId >= 0 && fontId < fonts.Count ? fonts[fontId] : default;
            var fillColor = fillId >= 0 && fillId < fills.Count ? fills[fillId] : null;

            var formatCode = customFormats.TryGetValue(numFmtId, out var custom)
                ? custom
                : ExcelNumberFormat.BuiltInFormatCode(numFmtId);

            var alignment = xf.Elements().FirstOrDefault(e => e.Name.LocalName == "alignment");
            var horizontal = (string?)alignment?.Attribute("horizontal") ?? "";

            styles.Add(new ExcelCellStyle
            {
                NumberFormatCode = formatCode,
                Bold = font.Bold,
                Italic = font.Italic,
                Underline = font.Underline,
                FontSizePt = font.SizePt,
                FontColorArgb = font.ColorArgb,
                FillColorArgb = fillColor,
                HorizontalAlignment = horizontal switch
                {
                    "left" or "justify" or "distributed" => ExcelHorizontalAlignment.Left,
                    "center" or "centerContinuous" => ExcelHorizontalAlignment.Center,
                    "right" => ExcelHorizontalAlignment.Right,
                    _ => ExcelHorizontalAlignment.General,
                },
                WrapText = IsOn(alignment?.Attribute("wrapText")),
                Indent = ParseIntAttribute(alignment?.Attribute("indent")) ?? 0,
            });
        }

        if (styles.Count == 0) styles.Add(ExcelCellStyle.Default);
        return styles;
    }

    // ---------------------------------------------------------------- 主题颜色

    /// <summary>
    /// 读 <c>xl/theme/theme1.xml</c> 的主题色板（顺序 dk1 / lt1 / dk2 / lt2 / accent1..6 / hlink / folHlink）。
    /// 读不出来就全 null，界面退回主题默认色（深色主题下仍然可读）。
    /// </summary>
    private static string?[] ReadThemeColors(OoxmlPackage package)
    {
        var colors = new string?[12];
        try
        {
            var partName = package.PartNames.FirstOrDefault(
                p => p.StartsWith("xl/theme/", StringComparison.OrdinalIgnoreCase)
                     && p.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            if (partName is null) return colors;

            var xml = package.TryReadXml(partName);
            var scheme = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
            if (scheme is null) return colors;

            // 顺序必须和 theme 索引一致（0 = dk1, 1 = lt1, 2 = dk2, 3 = lt2, 4..9 = accent1..6）
            var names = new[]
            {
                "dk1", "lt1", "dk2", "lt2",
                "accent1", "accent2", "accent3", "accent4", "accent5", "accent6",
                "hlink", "folHlink",
            };

            for (var index = 0; index < names.Length; index++)
            {
                var slot = scheme.Elements().FirstOrDefault(e => e.Name.LocalName == names[index]);
                var colorNode = slot?.Elements().FirstOrDefault();
                if (colorNode is null) continue;

                // srgbClr 直接给 val；sysClr 给 lastClr（实际显示色）
                var value = (string?)colorNode.Attribute("val")
                            ?? (string?)colorNode.Attribute("lastClr");
                colors[index] = NormalizeArgb(value);
            }
        }
        catch
        {
            // 主题是可选的「锦上添花」，解析失败不影响主流程
        }

        return colors;
    }

    /// <summary>解析 styles.xml 里的颜色节点：rgb / theme+tint / indexed 三种来源。</summary>
    private static string? ResolveColor(XElement? color, string?[] themeColors)
    {
        if (color is null) return null;
        if (IsOn(color.Attribute("auto"))) return null;   // auto = 让程序自己决定（交给主题默认色）

        var rgb = NormalizeArgb((string?)color.Attribute("rgb"));
        if (rgb is not null) return rgb;

        var themeIndex = ParseIntAttribute(color.Attribute("theme"));
        if (themeIndex is >= 0 and < 12)
        {
            var baseColor = themeColors[themeIndex.Value];
            if (baseColor is null) return null;

            var tintText = (string?)color.Attribute("tint");
            var tint = double.TryParse(tintText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTint)
                ? parsedTint
                : 0;
            return ApplyTint(baseColor, tint);
        }

        var indexed = ParseIntAttribute(color.Attribute("indexed"));
        if (indexed is not null) return IndexedColor(indexed.Value);

        return null;
    }

    /// <summary>把 6 / 8 位 RGB 文本统一成 <c>#AARRGGBB</c>（不合法返回 null）。</summary>
    private static string? NormalizeArgb(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var text = value.Trim().TrimStart('#');
        if (text.Length is not (6 or 8)) return null;
        if (!text.All(Uri.IsHexDigit)) return null;

        return text.Length == 6 ? "#FF" + text.ToUpperInvariant() : "#" + text.ToUpperInvariant();
    }

    /// <summary>
    /// Excel 的 tint（明暗微调）：负值变暗、正值变亮，公式来自 ECMA-376 的 luminanceModulation。
    /// 不处理 tint 的话，很多「蓝色表头 + 白色文字」会显示成纯蓝底 + 纯蓝字（看不见）。
    /// </summary>
    private static string ApplyTint(string argb, double tint)
    {
        if (Math.Abs(tint) < 0.0001) return argb;

        var alpha = Convert.ToInt32(argb.Substring(1, 2), 16);
        var red = Convert.ToInt32(argb.Substring(3, 2), 16);
        var green = Convert.ToInt32(argb.Substring(5, 2), 16);
        var blue = Convert.ToInt32(argb.Substring(7, 2), 16);

        int Adjust(int channel)
        {
            var value = tint < 0
                ? channel * (1.0 + tint)
                : channel + (255.0 - channel) * tint;
            return (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, 255);
        }

        return $"#{alpha:X2}{Adjust(red):X2}{Adjust(green):X2}{Adjust(blue):X2}";
    }

    /// <summary>旧式 indexed 索引色（BIFF8 调色板里最常用的前二十几个；其余交给主题默认）。</summary>
    private static string? IndexedColor(int index) => index switch
    {
        0 => "#FF000000",
        1 => "#FFFFFFFF",
        2 => "#FFFF0000",
        3 => "#FF00FF00",
        4 => "#FF0000FF",
        5 => "#FFFFFF00",
        6 => "#FFFF00FF",
        7 => "#FF00FFFF",
        8 => "#FF000000",
        9 => "#FFFFFFFF",
        10 => "#FFFF0000",
        11 => "#FF00FF00",
        12 => "#FF0000FF",
        13 => "#FFFFFF00",
        14 => "#FFFF00FF",
        15 => "#FF00FFFF",
        16 => "#FF800000",
        17 => "#FF008000",
        18 => "#FF000080",
        19 => "#FF808000",
        20 => "#FF800080",
        21 => "#FF008080",
        22 => "#FFC0C0C0",
        23 => "#FF808080",
        24 => "#FF9999FF",
        25 => "#FF993366",
        _ => null,   // 64 = 系统前景 / 65 = 系统背景：交给主题默认色
    };

    // ---------------------------------------------------------------- 工作表解析

    /// <summary>cols/col 里的一条列宽信息（可能覆盖一段列）。</summary>
    private readonly record struct ColumnInfo(int Min, int Max, double WidthChars, bool Hidden);

    private static ExcelSheet ReadSheet(
        OoxmlPackage package,
        string partName,
        string name,
        bool isVisible,
        XlsxParseLimits limits,
        IReadOnlyList<ExcelCellStyle> styles,
        IReadOnlyList<string> sharedStrings,
        bool uses1904)
    {
        var xml = package.TryReadXml(partName);
        if (xml is null)
        {
            return new ExcelSheet { Name = name, IsVisible = isVisible };
        }

        var rowsLimit = limits.MaxRowsEffective;
        var columnsLimit = limits.MaxColumnsEffective;

        var sheetData = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        var rowElements = sheetData is null
            ? new List<XElement>()
            : sheetData.Elements().Where(e => e.Name.LocalName == "row").ToList();

        var columnInfos = ReadColumnInfos(xml);
        var defaultColumnWidth = ReadDefaultColumnWidth(xml);

        // 第一遍：统计总行数 / 隐藏行 / 使用范围（只看行的 r 与最后一个格子的地址，不解析全部单元格内容）
        var totalDataRows = 0;
        var hiddenDataRows = 0;
        var maxRow = 0;
        var maxColumn = 0;
        var expectedRowIndex = 1;

        foreach (var rowElement in rowElements)
        {
            var rowIndex = ParseIntAttribute(rowElement.Attribute("r")) ?? expectedRowIndex;
            if (rowIndex <= 0) rowIndex = expectedRowIndex;
            expectedRowIndex = rowIndex + 1;

            var lastColumn = 0;
            var hasCell = false;
            var inferredColumn = 1;
            foreach (var cellElement in rowElement.Elements().Where(e => e.Name.LocalName == "c"))
            {
                hasCell = true;
                if (ExcelAddresses.TryParseCellReference((string?)cellElement.Attribute("r"), out _, out var column))
                {
                    lastColumn = Math.Max(lastColumn, column);
                    inferredColumn = column + 1;
                }
                else
                {
                    lastColumn = Math.Max(lastColumn, inferredColumn);
                    inferredColumn++;
                }
            }

            if (!hasCell) continue;

            totalDataRows++;
            if (IsOn(rowElement.Attribute("hidden"))) hiddenDataRows++;
            maxRow = Math.Max(maxRow, rowIndex);
            maxColumn = Math.Max(maxColumn, lastColumn);
        }

        // 使用范围为空时，退而用 dimension（有的文件只在 dimension 里写了范围）
        if (maxRow == 0 || maxColumn == 0)
        {
            var dimension = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "dimension");
            if (ExcelAddresses.TryParseRange((string?)dimension?.Attribute("ref"),
                    out _, out _, out var dimensionBottom, out var dimensionRight))
            {
                if (maxRow == 0) maxRow = dimensionBottom;
                if (maxColumn == 0) maxColumn = dimensionRight;
            }
        }

        var columnCount = Math.Min(maxColumn, columnsLimit);
        if (columnCount < 1 && maxColumn > 0) columnCount = 1;

        // 第二遍：真正建行 / 建单元格（只解析前 N 行，超过就只标记截断）
        var rows = new List<ExcelRow>(Math.Min(rowsLimit, 128));
        var includedRows = 0;
        var rowsTruncated = false;
        var hiddenRows = 0;

        foreach (var rowElement in rowElements)
        {
            var rowIndex = ParseIntAttribute(rowElement.Attribute("r"))
                           ?? (rows.Count == 0 ? 1 : rows[^1].RowIndex + 1);

            var hasCell = rowElement.Elements().Any(e => e.Name.LocalName == "c");
            if (!hasCell) continue;

            if (IsOn(rowElement.Attribute("hidden")))
            {
                hiddenRows++;   // 隐藏行跳过（数量在状态栏说明）
                continue;
            }

            if (includedRows >= rowsLimit)
            {
                rowsTruncated = true;
                continue;
            }

            var cellArray = new ExcelCell[columnCount];
            var nextColumn = 1;

            foreach (var cellElement in rowElement.Elements().Where(e => e.Name.LocalName == "c"))
            {
                if (!ExcelAddresses.TryParseCellReference((string?)cellElement.Attribute("r"), out _, out var column)
                    || column <= 0)
                {
                    column = nextColumn;
                }

                // 稀疏行：中间没有 XML 节点的格子补成空格子，保证 Cells[i] 与 Columns[i] 一一对应
                for (var gapColumn = nextColumn; gapColumn < column && gapColumn <= columnCount; gapColumn++)
                {
                    cellArray[gapColumn - 1] = CreateEmptyCell(rowIndex, gapColumn);
                }

                if (column > columnCount)
                {
                    nextColumn = column + 1;
                    continue;
                }

                cellArray[column - 1] = ParseCell(cellElement, rowIndex, column, styles, sharedStrings, uses1904);
                nextColumn = column + 1;
            }

            for (var tailColumn = nextColumn; tailColumn <= columnCount; tailColumn++)
            {
                cellArray[tailColumn - 1] = CreateEmptyCell(rowIndex, tailColumn);
            }

            rows.Add(new ExcelRow { RowIndex = rowIndex, Cells = cellArray });
            includedRows++;
        }

        // 合并单元格信息在 sheetData 之后：这里再按区域把「被覆盖格」清空、给锚点格打标
        var mergedRanges = ReadMergedRanges(xml);
        ApplyMergedCells(rows, mergedRanges, columnCount);

        // 列信息（含隐藏列；界面只给非隐藏列建 DataGrid 列）
        var columns = new List<ExcelColumn>(columnCount);
        for (var columnIndex = 1; columnIndex <= columnCount; columnIndex++)
        {
            var info = columnInfos.FirstOrDefault(c => columnIndex >= c.Min && columnIndex <= c.Max);
            var widthChars = info.WidthChars > 0 ? info.WidthChars : defaultColumnWidth;
            columns.Add(new ExcelColumn
            {
                ColumnIndex = columnIndex,
                Name = ExcelAddresses.ColumnName(columnIndex),
                WidthDip = Math.Clamp(widthChars * 7.0 + 5.0, 24.0, 600.0),
                IsHidden = info.Hidden,
            });
        }

        var hiddenColumns = columns.Count(c => c.IsHidden);

        // 截断说明（中文，界面直接显示）
        var notes = new List<string>();
        var expectedIncludedRows = totalDataRows - hiddenDataRows;
        if (rowsTruncated || includedRows < expectedIncludedRows)
        {
            notes.Add($"工作表共 {totalDataRows} 行，只显示前 {includedRows} 行");
        }
        if (maxColumn > columnCount)
        {
            notes.Add($"工作表共 {maxColumn} 列，只显示前 {columnCount} 列");
        }

        return new ExcelSheet
        {
            Name = name,
            IsVisible = isVisible,
            Rows = rows,
            Columns = columns,
            MergedRanges = mergedRanges
                .Where(range => range.Top <= Math.Max(maxRow, 1) && range.Left <= Math.Max(columnCount, 1))
                .ToList(),
            MaxRow = maxRow,
            MaxColumn = maxColumn,
            IsTruncated = notes.Count > 0,
            TruncationNote = notes.Count > 0 ? string.Join("；", notes) : null,
            HiddenRowCount = hiddenRows,
            HiddenColumnCount = hiddenColumns,
        };
    }

    private static ExcelCell CreateEmptyCell(int rowIndex, int columnIndex) => new()
    {
        Row = rowIndex,
        Column = columnIndex,
        Kind = ExcelCellKind.Empty,
        Style = ExcelCellStyle.Default,
    };

    private static List<ColumnInfo> ReadColumnInfos(XDocument worksheetXml)
    {
        var infos = new List<ColumnInfo>();
        var cols = worksheetXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "cols");
        if (cols is null) return infos;

        foreach (var col in cols.Elements().Where(e => e.Name.LocalName == "col"))
        {
            var min = ParseIntAttribute(col.Attribute("min")) ?? 0;
            var max = ParseIntAttribute(col.Attribute("max")) ?? min;
            if (min <= 0 || max < min) continue;

            var widthText = (string?)col.Attribute("width");
            var width = double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

            infos.Add(new ColumnInfo(min, max, width, IsOn(col.Attribute("hidden"))));
        }

        return infos;
    }

    private static double ReadDefaultColumnWidth(XDocument worksheetXml)
    {
        var sheetFormatPr = worksheetXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "sheetFormatPr");
        if (sheetFormatPr is not null)
        {
            foreach (var attributeName in new[] { "defaultColWidth", "baseColWidth" })
            {
                var text = (string?)sheetFormatPr.Attribute(attributeName);
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var width) && width > 0)
                    return width;
            }
        }

        return 8.43;   // Excel 新工作簿的默认列宽（字符数）
    }

    private static List<ExcelMergedRange> ReadMergedRanges(XDocument worksheetXml)
    {
        var ranges = new List<ExcelMergedRange>();
        var mergeCells = worksheetXml.Descendants().FirstOrDefault(e => e.Name.LocalName == "mergeCells");
        if (mergeCells is null) return ranges;

        foreach (var mergeCell in mergeCells.Elements().Where(e => e.Name.LocalName == "mergeCell"))
        {
            if (ExcelAddresses.TryParseRange((string?)mergeCell.Attribute("ref"),
                    out var top, out var left, out var bottom, out var right))
            {
                ranges.Add(new ExcelMergedRange { Top = top, Left = left, Bottom = bottom, Right = right });
            }
        }

        return ranges;
    }

    /// <summary>
    /// 把合并区域落到已建好的行上：锚点格打 <see cref="ExcelCell.IsMergedAnchor"/>，
    /// 其余格标记 <see cref="ExcelCell.IsMergedCovered"/> 并清空显示内容。
    /// <para>只遍历「真正建出来的行」（隐藏行 / 被截断的行不在列表里），并带标记预算，
    /// 所以畸形文件里一个「A1:XFD1048576」的合并区域也不会把解析拖死。</para>
    /// </summary>
    private static void ApplyMergedCells(List<ExcelRow> rows, List<ExcelMergedRange> ranges, int columnCount)
    {
        if (ranges.Count == 0 || rows.Count == 0 || columnCount == 0) return;

        var markedCells = 0;

        foreach (var range in ranges)
        {
            if (markedCells >= MergedCellMarkBudget) break;

            var left = Math.Max(range.Left, 1);
            var right = Math.Min(range.Right, columnCount);
            if (left > right) continue;

            foreach (var row in rows)
            {
                if (row.RowIndex < range.Top || row.RowIndex > range.Bottom) continue;

                if (row.RowIndex == range.Top && left <= columnCount)
                {
                    row.Cells[left - 1].IsMergedAnchor = true;
                }

                for (var column = left; column <= right; column++)
                {
                    if (row.RowIndex == range.Top && column == left) continue;

                    var cell = row.Cells[column - 1];
                    cell.IsMergedCovered = true;
                    cell.DisplayText = "";
                    cell.RawValue = "";
                    cell.Kind = ExcelCellKind.Empty;
                    markedCells++;

                    if (markedCells >= MergedCellMarkBudget) break;
                }

                if (markedCells >= MergedCellMarkBudget) break;
            }
        }
    }

    // ---------------------------------------------------------------- 单元格解析

    private static ExcelCell ParseCell(
        XElement cellElement,
        int rowIndex,
        int columnIndex,
        IReadOnlyList<ExcelCellStyle> styles,
        IReadOnlyList<string> sharedStrings,
        bool uses1904)
    {
        var styleIndex = ParseIntAttribute(cellElement.Attribute("s")) ?? 0;
        var style = styleIndex >= 0 && styleIndex < styles.Count ? styles[styleIndex] : ExcelCellStyle.Default;

        var type = (string?)cellElement.Attribute("t") ?? "n";
        var valueElement = cellElement.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
        var rawValueText = valueElement?.Value ?? "";
        var formulaElement = cellElement.Elements().FirstOrDefault(e => e.Name.LocalName == "f");
        var formula = formulaElement?.Value;
        if (string.IsNullOrWhiteSpace(formula)) formula = null;

        var inlineText = type == "inlineStr"
            ? ReadRichText(cellElement.Elements().FirstOrDefault(e => e.Name.LocalName == "is"))
            : null;

        var value = BuildCellValue(type, rawValueText, inlineText, formula, style, sharedStrings, uses1904);

        return new ExcelCell
        {
            Row = rowIndex,
            Column = columnIndex,
            DisplayText = value.DisplayText,
            RawValue = value.RawValue,
            Formula = formula,
            Kind = value.Kind,
            Style = style,
        };
    }

    /// <summary>单元格值 + 显示文本（显示值按类型 / 数字格式算好）。</summary>
    private readonly record struct CellValue(string DisplayText, string RawValue, ExcelCellKind Kind);

    private static CellValue BuildCellValue(
        string type,
        string rawValue,
        string? inlineText,
        string? formula,
        ExcelCellStyle style,
        IReadOnlyList<string> sharedStrings,
        bool uses1904)
    {
        switch (type)
        {
            case "s":
            {
                // 共享字符串：v 是 sharedStrings 里的下标
                if (!int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || index < 0 || index >= sharedStrings.Count)
                {
                    return new CellValue("", "", ExcelCellKind.Empty);
                }

                var text = sharedStrings[index];
                return new CellValue(text, text, ExcelCellKind.Text);
            }

            case "inlineStr":
            {
                // 内联字符串：内容在 <is> 里（可能带富文本 run）
                var text = inlineText ?? "";
                return new CellValue(text, text, ExcelCellKind.Text);
            }

            case "str":
                // 公式结果字符串（缓存值就是文本）
                return new CellValue(rawValue, rawValue, ExcelCellKind.Text);

            case "b":
            {
                var isTrue = rawValue.Trim() is "1" or "true" or "TRUE";
                return new CellValue(isTrue ? "TRUE" : "FALSE", rawValue, ExcelCellKind.Boolean);
            }

            case "e":
                return new CellValue(rawValue, rawValue, ExcelCellKind.Error);

            case "d":
            {
                // ISO 8601 日期（比较少见，Excel 2010 之后才有）
                if (!DateTime.TryParse(rawValue, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var date))
                {
                    return new CellValue(rawValue, rawValue, ExcelCellKind.Text);
                }

                var code = ExcelNumberFormat.IsDateFormat(style.NumberFormatCode)
                    ? style.NumberFormatCode
                    : date.TimeOfDay == TimeSpan.Zero ? "yyyy-mm-dd" : "yyyy-mm-dd hh:mm:ss";
                return new CellValue(ExcelNumberFormat.FormatDate(date, code), rawValue, ExcelCellKind.Date);
            }

            default:
            {
                // 数字（t 缺省 / t="n"）：按单元格的数字格式显示
                if (string.IsNullOrWhiteSpace(rawValue))
                {
                    // 公式没有缓存值（例如文件被别的程序写过、还没计算过）→ 显示为空，公式仍保留在 Formula
                    return new CellValue("", "", formula is null ? ExcelCellKind.Empty : ExcelCellKind.Number);
                }

                if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return new CellValue(rawValue, rawValue, ExcelCellKind.Text);
                }

                var isDate = ExcelNumberFormat.IsDateFormat(style.NumberFormatCode);
                var display = ExcelNumberFormat.Format(number, style.NumberFormatCode, uses1904);
                return new CellValue(display, rawValue, isDate ? ExcelCellKind.Date : ExcelCellKind.Number);
            }
        }
    }

    // ---------------------------------------------------------------- 小工具

    private static bool IsOn(XAttribute? attribute)
    {
        if (attribute is null) return false;
        var text = attribute.Value.Trim();
        return text is "1" or "true" or "on" or "True" or "TRUE";
    }

    /// <summary>
    /// 判断 <c>&lt;b/&gt;</c> / <c>&lt;b val="1"/&gt;</c> 这类「开关元素」是否打开。
    /// 元素存在且没有 val 时视为打开（Excel 写的就是 <c>&lt;b/&gt;</c>）。
    /// </summary>
    private static bool IsOn(XElement? element)
    {
        if (element is null) return false;
        var value = (string?)element.Attribute("val");
        if (value is null) return true;
        return value.Trim() is "1" or "true" or "on";
    }

    private static int? ParseIntAttribute(XAttribute? attribute)
    {
        if (attribute is null) return null;
        return int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
