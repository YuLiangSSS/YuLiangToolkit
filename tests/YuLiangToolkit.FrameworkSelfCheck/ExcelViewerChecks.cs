using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using YuLiangToolkit.Core.Documents;
using YuLiangToolkit.Tools.ExcelViewer;
using YuLiangToolkit.Tools.ExcelViewer.Parsing;

namespace YuLiangToolkit.FrameworkSelfCheck;

/// <summary>
/// 「Excel 阅读器」自检：手写一个最小的 .xlsx（ZipArchive + 手写各个 XML 部件），
/// 真跑一遍解析链路（共享字符串 / 富文本 / 数字格式 / 日期系统 / 布尔 / 错误值 / 公式缓存值 /
/// 样式 / 主题色 / 合并单元格 / 列宽 / 隐藏行列 / 截断），再断言配置默认值与「最近打开」规则。
/// <b>不弹窗、不联网、不碰真实 Data / Config</b>（临时目录里造文件，finally 清理）。
/// </summary>
internal static class ExcelViewerChecks
{
    public static void Run(Action<string, bool, string> check, Action<string, bool> report)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ylt-excel-viewer-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var workbookPath = Path.Combine(tempDir, "示例工作簿.xlsx");
            BuildSampleWorkbook(workbookPath, date1904: false);
            var workbook1904Path = Path.Combine(tempDir, "1904日期.xlsx");
            BuildSampleWorkbook(workbook1904Path, date1904: true);
            var minimalPath = Path.Combine(tempDir, "缺样式部件.xlsx");
            BuildMinimalWorkbook(minimalPath);

            CheckMetadata(check);
            CheckAddresses(check);
            CheckNumberFormat(check);

            var workbook = XlsxParser.Parse(workbookPath);
            CheckWorkbook(check, workbook, workbookPath);
            CheckSharedStrings(check, workbook);
            CheckCellValues(check, workbook);
            CheckStyles(check, workbook);
            CheckMergedCells(check, workbook);
            CheckLayout(check, workbook);
            CheckDate1904(check, workbook1904Path);
            CheckTruncation(check, workbookPath);
            CheckTolerance(check, minimalPath, tempDir);
            CheckLimits(check);
            CheckConfig(check, tempDir);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
        }

        report("[Excel 阅读器] 手写 xlsx 解析 / 数字格式 / 合并单元格 / 截断 / 配置与最近列表", true);
    }

    // ---------------------------------------------------------------- 工具元数据

    private static void CheckMetadata(Action<string, bool, string> check)
    {
        var tool = new ExcelViewerTool();
        check("[Excel 阅读器] 工具 Id / 名称 / Emoji / 分类 / 排序符合约定",
            tool.Id == "excel-viewer" && tool.Name == "Excel 阅读器"
            && tool.EmojiIcon == "📗" && tool.Category == "文档" && tool.Order == 19,
            $"{tool.Id} / {tool.Name} / {tool.EmojiIcon} / {tool.Category} / {tool.Order}");

        check("[Excel 阅读器] 标签与描述覆盖 Excel / xlsx / 表格 / 只读 / 工作表",
            tool.Tags.Contains("Excel") && tool.Tags.Contains("xlsx") && tool.Tags.Contains("表格")
            && tool.Tags.Contains("文档") && tool.Tags.Contains("阅读器") && tool.Tags.Contains("只读")
            && tool.Tags.Contains("浏览") && tool.Tags.Contains("工作表") && tool.Tags.Contains("单元格")
            && tool.Description.Contains("只读") && tool.Description.Contains("xlsx"),
            string.Join(" / ", tool.Tags));

        check("[Excel 阅读器] 配置 Id 与工具 Id 一致（Config\\excel-viewer\\config.json）",
            ExcelViewerConfig.ToolId == "excel-viewer" && ExcelViewerConfig.ToolId == tool.Id,
            ExcelViewerConfig.ToolId);
    }

    // ---------------------------------------------------------------- 地址换算

    private static void CheckAddresses(Action<string, bool, string> check)
    {
        check("[Excel 阅读器] 列字母换算：1→A、26→Z、27→AA、702→ZZ、703→AAA",
            ExcelAddresses.ColumnName(1) == "A" && ExcelAddresses.ColumnName(26) == "Z"
            && ExcelAddresses.ColumnName(27) == "AA" && ExcelAddresses.ColumnName(702) == "ZZ"
            && ExcelAddresses.ColumnName(703) == "AAA",
            $"{ExcelAddresses.ColumnName(1)} / {ExcelAddresses.ColumnName(26)} / {ExcelAddresses.ColumnName(27)} / "
            + $"{ExcelAddresses.ColumnName(702)} / {ExcelAddresses.ColumnName(703)}");

        check("[Excel 阅读器] 列字母反解 + 单元格引用往返：A→1、AA→27、AZ→52、AAA→703",
            ExcelAddresses.ParseColumn("A") == 1 && ExcelAddresses.ParseColumn("aa") == 27
            && ExcelAddresses.ParseColumn("AZ") == 52 && ExcelAddresses.ParseColumn("AAA") == 703
            && ExcelAddresses.ParseColumn("3A") == 0 && ExcelAddresses.ParseColumn("") == 0
            && ExcelAddresses.CellReference(3, 27) == "AA3"
            && ExcelAddresses.TryParseCellReference("$AA$3", out var row, out var column)
            && row == 3 && column == 27
            && !ExcelAddresses.TryParseCellReference("A", out _, out _)
            && !ExcelAddresses.TryParseCellReference("A0", out _, out _),
            "AA / AZ / AAA / $AA$3");
    }

    // ---------------------------------------------------------------- 数字格式纯函数

    private static void CheckNumberFormat(Action<string, bool, string> check)
    {
        check("[Excel 阅读器] 日期格式识别：日期 / 时间 / [h] 算日期，数字 / 文本格式不算",
            ExcelNumberFormat.IsDateFormat("yyyy-mm-dd")
            && ExcelNumberFormat.IsDateFormat("hh:mm:ss")
            && ExcelNumberFormat.IsDateFormat("[h]:mm:ss")
            && ExcelNumberFormat.IsDateFormat("m\"月\"d\"日\"")
            && !ExcelNumberFormat.IsDateFormat("0.00")
            && !ExcelNumberFormat.IsDateFormat("#,##0;[Red](#,##0)")
            && !ExcelNumberFormat.IsDateFormat("@")
            && !ExcelNumberFormat.IsDateFormat(""),
            "yyyy-mm-dd / hh:mm:ss / [h] / 0.00 / [Red]");

        check("[Excel 阅读器] 数字格式：百分比 / 千分位 / 小数 / 科学计数 / 文本跑字面量",
            ExcelNumberFormat.Format(0.256, "0.00%", false) == "25.60%"
            && ExcelNumberFormat.Format(1234.5, "#,##0.00", false) == "1,234.50"
            && ExcelNumberFormat.Format(1234567, "#,##0", false) == "1,234,567"
            && ExcelNumberFormat.Format(1234567, "0.00E+00", false) == "1.23E+06"
            && ExcelNumberFormat.Format(3.14159, "0.00", false) == "3.14"
            && ExcelNumberFormat.Format(12, "\"共\"0\"个\"", false) == "共12个"
            && ExcelNumberFormat.Format(-1234.5, "#,##0.00", false) == "-1,234.50"
            && ExcelNumberFormat.Format(-1234.5, "0.00;(0.00)", false) == "(1234.50)",
            $"{ExcelNumberFormat.Format(0.256, "0.00%", false)} / {ExcelNumberFormat.Format(1234567, "0.00E+00", false)}");

        check("[Excel 阅读器] 日期格式化 + 1900 假闰日 / 1904 日期系统",
            ExcelNumberFormat.Format(45292, "yyyy-mm-dd", false) == "2024-01-01"
            && ExcelNumberFormat.Format(0.5, "hh:mm", false) == "12:00"
            && ExcelNumberFormat.Format(0, "yyyy-mm-dd", true) == "1904-01-01"
            && ExcelSerialDate.ToDateTime(45292, false) == new DateTime(2024, 1, 1)
            && ExcelSerialDate.ToDateTime(1, false) == new DateTime(1900, 1, 1)
            && ExcelSerialDate.ToDateTime(0, true) == new DateTime(1904, 1, 1),
            $"{ExcelNumberFormat.Format(45292, "yyyy-mm-dd", false)} / {ExcelNumberFormat.Format(0, "yyyy-mm-dd", true)}");

        check("[Excel 阅读器] 内置格式 id 表：15 是日期、0 是常规、49 是文本",
            ExcelNumberFormat.IsBuiltInDateFormat(14) && ExcelNumberFormat.IsBuiltInDateFormat(22)
            && ExcelNumberFormat.IsBuiltInDateFormat(46)
            && !ExcelNumberFormat.IsBuiltInDateFormat(0) && !ExcelNumberFormat.IsBuiltInDateFormat(4)
            && ExcelNumberFormat.BuiltInFormatCode(0) == ""
            && ExcelNumberFormat.BuiltInFormatCode(2) == "0.00"
            && ExcelNumberFormat.BuiltInFormatCode(49) == "@",
            "14/22/46 日期 · 0/4 数字 · 49 @");
    }

    // ---------------------------------------------------------------- 工作簿级

    private static void CheckWorkbook(Action<string, bool, string> check, ExcelWorkbook workbook, string path)
    {
        check("[Excel 阅读器] 多工作表 + 隐藏工作表（state=hidden → IsVisible=false）",
            workbook.Sheets.Count == 2
            && workbook.Sheets[0].Name == "数据" && workbook.Sheets[0].IsVisible
            && workbook.Sheets[1].Name == "隐藏表" && !workbook.Sheets[1].IsVisible
            && !workbook.Uses1904DateSystem,
            string.Join(" / ", workbook.Sheets.Select(sheet => sheet.Name + (sheet.IsVisible ? "" : "（隐藏）"))));

        // 流式重载也走一遍（保证解析器不依赖磁盘路径）
        using (var stream = File.OpenRead(path))
        {
            var fromStream = XlsxParser.Parse(stream);
            check("[Excel 阅读器] Parse(Stream) 重载可用（流所有权归解析器）",
                fromStream.Sheets.Count == 2 && fromStream.Sheets[0].Name == "数据",
                $"{fromStream.Sheets.Count} 个工作表");
        }
    }

    private static void CheckSharedStrings(Action<string, bool, string> check, ExcelWorkbook workbook)
    {
        var sheet = workbook.Sheets[0];
        check("[Excel 阅读器] 共享字符串 / 富文本 run 拼接 / 跳过 rPh 注音",
            CellText(sheet, 1, 1) == "普通文本"
            && CellText(sheet, 1, 2) == "富文本"
            && CellText(sheet, 1, 3) == "富文",
            $"{CellText(sheet, 1, 1)} / {CellText(sheet, 1, 2)} / {CellText(sheet, 1, 3)}");

        check("[Excel 阅读器] 内联字符串（inlineStr 的 <is> 富文本）",
            CellText(sheet, 1, 4) == "内联文本" && FindCell(sheet, 1, 4)?.Kind == ExcelCellKind.Text,
            CellText(sheet, 1, 4));
    }

    private static void CheckCellValues(Action<string, bool, string> check, ExcelWorkbook workbook)
    {
        var sheet = workbook.Sheets[0];

        check("[Excel 阅读器] 布尔值显示 TRUE / FALSE（原始值是 1 / 0）",
            FindCell(sheet, 1, 5) is { DisplayText: "TRUE", Kind: ExcelCellKind.Boolean, RawValue: "1" }
            && FindCell(sheet, 1, 12) is { DisplayText: "FALSE", Kind: ExcelCellKind.Boolean },
            $"{CellText(sheet, 1, 5)} / {CellText(sheet, 1, 12)}");

        check("[Excel 阅读器] 错误值原样显示（#DIV/0!）+ 类型是 Error",
            FindCell(sheet, 1, 6) is { DisplayText: "#DIV/0!", Kind: ExcelCellKind.Error },
            CellText(sheet, 1, 6));

        check("[Excel 阅读器] 数字按内置格式显示：常规 / 百分比 / 千分位",
            CellText(sheet, 1, 7) == "1234.5"
            && CellText(sheet, 1, 8) == "25.60%"
            && CellText(sheet, 1, 9) == "1,234,567",
            $"{CellText(sheet, 1, 7)} / {CellText(sheet, 1, 8)} / {CellText(sheet, 1, 9)}");

        check("[Excel 阅读器] 日期序列号 + 日期格式 → 日期文字，类型是 Date",
            FindCell(sheet, 1, 10) is { DisplayText: "2024-01-01", Kind: ExcelCellKind.Date },
            CellText(sheet, 1, 10));

        check("[Excel 阅读器] 公式只显示缓存值，Formula 字段保留原始公式文本",
            FindCell(sheet, 1, 11) is { DisplayText: "1234.5", Formula: "SUM(G1:G2)" },
            $"{CellText(sheet, 1, 11)} · ={FindCell(sheet, 1, 11)?.Formula}");

        check("[Excel 阅读器] 自定义数字格式（numFmtId=164 → 0.0%）",
            CellText(sheet, 1, 13) == "50.0%",
            CellText(sheet, 1, 13));
    }

    private static void CheckStyles(Action<string, bool, string> check, ExcelWorkbook workbook)
    {
        var sheet = workbook.Sheets[0];
        var styled = FindCell(sheet, 2, 1)?.Style;

        check("[Excel 阅读器] 单元格样式：粗体 / 斜体 / 字号 / 字体色 / 填充色 / 居中 / 换行 / 缩进",
            styled is not null
            && styled.Bold && styled.Italic && !styled.Underline
            && styled.FontSizePt == 14
            && styled.FontColorArgb == "#FFFF0000"
            && styled.FillColorArgb == "#FFFFFF00"
            && styled.HorizontalAlignment == ExcelHorizontalAlignment.Center
            && styled.WrapText && styled.Indent == 2,
            $"{styled?.FontColorArgb} / {styled?.FillColorArgb} / {styled?.HorizontalAlignment} / {styled?.FontSizePt}");

        check("[Excel 阅读器] styles.xml 的主题色（theme=4 → theme1.xml 的 accent1）+ 右对齐",
            FindCell(sheet, 2, 2)?.Style.FontColorArgb == "#FF12AB34"
            && FindCell(sheet, 2, 2)?.Style.HorizontalAlignment == ExcelHorizontalAlignment.Right,
            $"{FindCell(sheet, 2, 2)?.Style.FontColorArgb} / {FindCell(sheet, 2, 2)?.Style.HorizontalAlignment}");
    }

    private static void CheckMergedCells(Action<string, bool, string> check, ExcelWorkbook workbook)
    {
        var sheet = workbook.Sheets[0];

        check("[Excel 阅读器] 合并单元格：锚点格显示内容、其余格标记被覆盖且显示为空",
            FindCell(sheet, 2, 3) is { IsMergedAnchor: true, DisplayText: "合并文本" }
            && FindCell(sheet, 2, 4) is { IsMergedCovered: true, DisplayText: "" }
            && CellText(sheet, 2, 4) == "",
            $"{CellText(sheet, 2, 3)} / {CellText(sheet, 2, 4)}");

        check("[Excel 阅读器] 跨行合并区域（A5:B6）锚点与覆盖格都正确",
            FindCell(sheet, 5, 1) is { IsMergedAnchor: true, DisplayText: "跨行合并" }
            && FindCell(sheet, 5, 2) is { IsMergedCovered: true }
            && FindCell(sheet, 6, 1) is { IsMergedCovered: true }
            && FindCell(sheet, 6, 2) is { IsMergedCovered: true, DisplayText: "" }
            && sheet.MergedRanges.Count == 2,
            string.Join(" / ", sheet.MergedRanges.Select(range => range.ToString())));
    }

    private static void CheckLayout(Action<string, bool, string> check, ExcelWorkbook workbook)
    {
        var sheet = workbook.Sheets[0];

        check("[Excel 阅读器] 隐藏行跳过 + 行号保留原始值（1、2、4、5、6）",
            sheet.HiddenRowCount == 1
            && sheet.Rows.Count == 5
            && string.Join(",", sheet.Rows.Select(row => row.RowIndex)) == "1,2,4,5,6",
            $"隐藏 {sheet.HiddenRowCount} 行 · 行号 {string.Join(",", sheet.Rows.Select(row => row.RowIndex))}");

        check("[Excel 阅读器] 列宽换算（width × 7 + 5）+ 隐藏列标记 + 列名",
            sheet.Columns.Count == 13
            && sheet.Columns[0].Name == "A" && Math.Abs(sheet.Columns[0].WidthDip - 145) < 0.01
            && sheet.Columns[1].Name == "B" && sheet.Columns[1].IsHidden
            && sheet.Columns[2].Name == "C" && Math.Abs(sheet.Columns[2].WidthDip - 89) < 0.01
            && sheet.HiddenColumnCount == 1,
            $"A={sheet.Columns[0].WidthDip} · B 隐藏={sheet.Columns[1].IsHidden} · C={sheet.Columns[2].WidthDip}");

        check("[Excel 阅读器] 稀疏行补齐空单元格：Cells 下标与列一一对应",
            sheet.Rows.First(row => row.RowIndex == 4).Cells[0].Kind == ExcelCellKind.Empty
            && sheet.Rows.First(row => row.RowIndex == 4).Cells[1].Kind == ExcelCellKind.Empty
            && CellText(sheet, 4, 4) == "99",
            $"A4 空 / D4={CellText(sheet, 4, 4)}");
    }

    private static void CheckDate1904(Action<string, bool, string> check, string workbook1904Path)
    {
        var workbook = XlsxParser.Parse(workbook1904Path);
        check("[Excel 阅读器] 1904 日期系统：serial 0 + 日期格式 → 1904-01-01",
            workbook.Uses1904DateSystem
            && CellText(workbook.Sheets[0], 2, 13) == "1904-01-01",
            $"{workbook.Uses1904DateSystem} · {CellText(workbook.Sheets[0], 2, 13)}");
    }

    private static void CheckTruncation(Action<string, bool, string> check, string workbookPath)
    {
        var limits = new XlsxParseLimits { MaxRows = 2, MaxColumns = 5 };
        var workbook = XlsxParser.Parse(workbookPath, limits);
        var sheet = workbook.Sheets[0];

        check("[Excel 阅读器] 超过解析上限时截断，并在 TruncationNote 里说明（中文）",
            sheet.IsTruncated
            && sheet.Rows.Count == 2
            && sheet.Columns.Count == 5
            && sheet.MaxRow == 6
            && sheet.MaxColumn == 13
            && sheet.TruncationNote is not null
            && sheet.TruncationNote.Contains("只显示前 2 行", StringComparison.Ordinal)
            && sheet.TruncationNote.Contains("只显示前 5 列", StringComparison.Ordinal),
            sheet.TruncationNote ?? "（没有截断说明）");
    }

    private static void CheckTolerance(Action<string, bool, string> check, string minimalPath, string tempDir)
    {
        // 缺 styles.xml / sharedStrings.xml / theme：用默认值，不崩
        var workbook = XlsxParser.Parse(minimalPath);
        check("[Excel 阅读器] 缺 styles / sharedStrings 等部件时用默认值，不抛异常",
            workbook.Sheets.Count == 1
            && CellText(workbook.Sheets[0], 1, 1) == "42"
            && workbook.Sheets[0].Rows[0].Cells[0].Style.NumberFormatCode == "",
            $"工作表 {workbook.Sheets.Count} 个 · A1={CellText(workbook.Sheets[0], 1, 1)}");

        // 坏文件 / .xls：抛 OoxmlException（中文消息，界面直接显示）
        var fakeXls = Path.Combine(tempDir, "旧格式.xls");
        File.WriteAllText(fakeXls, "这不是 ZIP，是旧版二进制格式的占位内容");
        var threw = false;
        var message = "";
        try
        {
            XlsxParser.Parse(fakeXls);
        }
        catch (OoxmlException ex)
        {
            threw = true;
            message = ex.Message;
        }
        catch (Exception ex)
        {
            message = "抛了非 OoxmlException：" + ex.GetType().Name;
        }

        check("[Excel 阅读器] 坏文件 / .xls 抛 OoxmlException（中文消息，可直接展示）",
            threw && message.Length > 0 && message.Any(ch => ch >= '\u4e00' && ch <= '\u9fff'),
            message);

        check("[Excel 阅读器] .xls 不被当成受支持扩展名（提示用户另存为 .xlsx）",
            !XlsxParser.IsSupportedExtension("a.xls")
            && XlsxParser.IsSupportedExtension("a.xlsx")
            && XlsxParser.IsSupportedExtension("A.XLSM")
            && !XlsxParser.IsSupportedExtension(null),
            "xls=false / xlsx=true / XLSM=true");
    }

    private static void CheckLimits(Action<string, bool, string> check)
    {
        var defaults = new XlsxParseLimits();
        var clamped = new XlsxParseLimits { MaxRows = 999_999, MaxColumns = 99_999 };
        check("[Excel 阅读器] XlsxParseLimits 默认 5000 行 / 200 列，调大时夹到 20000 / 1000",
            defaults.MaxRows == 5000 && defaults.MaxColumns == 200
            && defaults.MaxRowsEffective == 5000 && defaults.MaxColumnsEffective == 200
            && clamped.MaxRowsEffective == 20000 && clamped.MaxColumnsEffective == 1000
            && new XlsxParseLimits { MaxRows = 0, MaxColumns = -3 }.MaxRowsEffective == 1,
            $"{defaults.MaxRowsEffective} / {defaults.MaxColumnsEffective} · {clamped.MaxRowsEffective} / {clamped.MaxColumnsEffective}");
    }

    // ---------------------------------------------------------------- 配置与最近列表

    private static void CheckConfig(Action<string, bool, string> check, string tempDir)
    {
        var config = new ExcelViewerConfig();
        check("[Excel 阅读器] 配置默认值：网格线开、最近上限 50、最近列表空、上次文件夹空",
            config.ShowGridLines && config.RecentLimit == 50 && config.RecentLimitEffective == 50
            && config.RecordsRecent && config.RecentEntries.Count == 0 && config.LastFolder == "",
            $"网格线={config.ShowGridLines} · 上限={config.RecentLimitEffective} · 记录={config.RecentEntries.Count}");

        var first = Path.Combine(tempDir, "一.xlsx");
        var second = Path.Combine(tempDir, "二.xlsx");
        File.WriteAllText(first, "占位");
        File.WriteAllText(second, "占位");

        config.RememberRecent(first);
        config.RememberRecent(second);
        config.RememberRecent(first);
        check("[Excel 阅读器] 最近列表：去重 + 置顶（同一路径只留一条）",
            config.RecentEntries.Count == 2
            && string.Equals(config.RecentEntries[0].Path, Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase),
            string.Join(" → ", config.RecentEntries.Select(entry => Path.GetFileName(entry.Path))));

        config.RecentLimit = 1;
        config.TrimRecentToLimit();
        check("[Excel 阅读器] 最近列表：上限 1 时裁掉多余的老记录",
            config.RecentEntries.Count == 1 && config.RecentLimitEffective == 1,
            $"{config.RecentEntries.Count} 条");

        config.RecentLimit = 500;
        check("[Excel 阅读器] 最近上限夹到 0 ~ 100（手改坏配置也不会存出离谱值）",
            config.RecentLimit == 100 && config.RecentLimitEffective == 100,
            config.RecentLimit.ToString());

        config.ClearRecent();
        check("[Excel 阅读器] 清空最近列表（只删记录，不动文件）",
            config.RecentEntries.Count == 0 && File.Exists(first) && File.Exists(second),
            $"{config.RecentEntries.Count} 条 · 文件仍在");

        config.RecentLimit = 3;
        config.ShowGridLines = false;
        config.ResetToDefaults();
        check("[Excel 阅读器] 恢复默认设置：网格线开、最近上限回 50",
            config.ShowGridLines && config.RecentLimit == 50 && config.RecentLimitEffective == 50,
            $"网格线={config.ShowGridLines} · 上限={config.RecentLimitEffective}");

        var ignoredProperties = new[] { nameof(ExcelViewerConfig.RecentLimitEffective), nameof(ExcelViewerConfig.RecordsRecent) };
        var allIgnored = ignoredProperties.All(name =>
            typeof(ExcelViewerConfig).GetProperty(name)?.GetCustomAttribute<JsonIgnoreAttribute>() is not null);
        check("[Excel 阅读器] 计算属性带 [JsonIgnore]（不会被写进 config.json）",
            allIgnored,
            string.Join(" / ", ignoredProperties));
    }

    // ---------------------------------------------------------------- 最小 xlsx 生成器

    /// <summary>
    /// 造一个最小的 .xlsx：两个工作表（一个隐藏）、共享字符串（含富文本 / rPh）、内联字符串、
    /// 数字 / 百分比 / 千分位 / 日期 / 自定义格式、布尔 / 错误值、公式缓存值、样式（含主题色）、
    /// 合并单元格、隐藏行列、稀疏行 —— 自检要覆盖的点都在这一个文件里。
    /// </summary>
    private static void BuildSampleWorkbook(string path, bool date1904)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        AddPart(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
              <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
              <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
              <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
              <Override PartName="/xl/theme/theme1.xml" ContentType="application/vnd.openxmlformats-officedocument.theme+xml"/>
            </Types>
            """);

        AddPart(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        var date1904Attribute = date1904 ? " date1904=\"1\"" : "";
        AddPart(zip, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <workbookPr{date1904Attribute}/>
              <sheets>
                <sheet name="数据" sheetId="1" r:id="rId1"/>
                <sheet name="隐藏表" sheetId="2" state="hidden" r:id="rId2"/>
              </sheets>
            </workbook>
            """);

        AddPart(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
              <Relationship Id="rId4" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
              <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme" Target="theme/theme1.xml"/>
            </Relationships>
            """);

        AddPart(zip, "xl/sharedStrings.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="3" uniqueCount="3">
              <si><t>普通文本</t></si>
              <si><r><t>富</t></r><r><t>文本</t></r></si>
              <si><r><t>富</t></r><rPh sb="0" eb="1"><t>ふ</t></rPh><r><t>文</t></r></si>
            </sst>
            """);

        AddPart(zip, "xl/styles.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <numFmts count="1"><numFmt numFmtId="164" formatCode="0.0%"/></numFmts>
              <fonts count="3">
                <font><sz val="11"/><color theme="1"/><name val="Calibri"/></font>
                <font><b/><i/><sz val="14"/><color rgb="FFFF0000"/><name val="Calibri"/></font>
                <font><sz val="11"/><color theme="4"/><name val="Calibri"/></font>
              </fonts>
              <fills count="3">
                <fill><patternFill patternType="none"/></fill>
                <fill><patternFill patternType="gray125"/></fill>
                <fill><patternFill patternType="solid"><fgColor rgb="FFFFFF00"/><bgColor indexed="64"/></patternFill></fill>
              </fills>
              <cellXfs count="7">
                <xf numFmtId="0" fontId="0" fillId="0"/>
                <xf numFmtId="10" fontId="0" fillId="0" applyNumberFormat="1"/>
                <xf numFmtId="3" fontId="0" fillId="0" applyNumberFormat="1"/>
                <xf numFmtId="14" fontId="0" fillId="0" applyNumberFormat="1"/>
                <xf numFmtId="164" fontId="0" fillId="0" applyNumberFormat="1"/>
                <xf numFmtId="0" fontId="1" fillId="2" applyFont="1" applyFill="1">
                  <alignment horizontal="center" wrapText="1" indent="2"/>
                </xf>
                <xf numFmtId="0" fontId="2" fillId="0" applyFont="1">
                  <alignment horizontal="right"/>
                </xf>
              </cellXfs>
            </styleSheet>
            """);

        AddPart(zip, "xl/theme/theme1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <a:theme xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" name="Office">
              <a:themeElements>
                <a:clrScheme name="Office">
                  <a:dk1><a:sysClr val="windowText" lastClr="000000"/></a:dk1>
                  <a:lt1><a:sysClr val="window" lastClr="FFFFFF"/></a:lt1>
                  <a:dk2><a:srgbClr val="44546A"/></a:dk2>
                  <a:lt2><a:srgbClr val="E7E6E6"/></a:lt2>
                  <a:accent1><a:srgbClr val="12AB34"/></a:accent1>
                  <a:accent2><a:srgbClr val="ED7D31"/></a:accent2>
                  <a:accent3><a:srgbClr val="A5A5A5"/></a:accent3>
                  <a:accent4><a:srgbClr val="FFC000"/></a:accent4>
                  <a:accent5><a:srgbClr val="4472C4"/></a:accent5>
                  <a:accent6><a:srgbClr val="70AD47"/></a:accent6>
                  <a:hlink><a:srgbClr val="0563C1"/></a:hlink>
                  <a:folHlink><a:srgbClr val="954F72"/></a:folHlink>
                </a:clrScheme>
              </a:themeElements>
            </a:theme>
            """);

        AddPart(zip, "xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <dimension ref="A1:M6"/>
              <sheetFormatPr defaultColWidth="10"/>
              <cols>
                <col min="1" max="1" width="20" customWidth="1"/>
                <col min="2" max="2" width="5" hidden="1" customWidth="1"/>
                <col min="3" max="13" width="12" customWidth="1"/>
              </cols>
              <sheetData>
                <row r="1">
                  <c r="A1" t="s"><v>0</v></c>
                  <c r="B1" t="s"><v>1</v></c>
                  <c r="C1" t="s"><v>2</v></c>
                  <c r="D1" t="inlineStr"><is><t>内联文本</t></is></c>
                  <c r="E1" t="b"><v>1</v></c>
                  <c r="F1" t="e"><v>#DIV/0!</v></c>
                  <c r="G1"><v>1234.5</v></c>
                  <c r="H1" s="1"><v>0.256</v></c>
                  <c r="I1" s="2"><v>1234567</v></c>
                  <c r="J1" s="3"><v>45292</v></c>
                  <c r="K1"><f>SUM(G1:G2)</f><v>1234.5</v></c>
                  <c r="L1" t="b"><v>0</v></c>
                  <c r="M1" s="4"><v>0.5</v></c>
                </row>
                <row r="2">
                  <c r="A2" s="5" t="inlineStr"><is><t>样式文本</t></is></c>
                  <c r="B2" s="6"><v>42</v></c>
                  <c r="C2" t="inlineStr"><is><t>合并文本</t></is></c>
                  <c r="D2"/>
                  <c r="M2" s="3"><v>0</v></c>
                </row>
                <row r="3" hidden="1"><c r="A3"><v>隐藏行</v></c></row>
                <row r="4"><c r="D4"><v>99</v></c></row>
                <row r="5"><c r="A5" t="inlineStr"><is><t>跨行合并</t></is></c></row>
                <row r="6"><c r="B6"><v>被覆盖</v></c></row>
              </sheetData>
              <mergeCells count="2">
                <mergeCell ref="C2:D2"/>
                <mergeCell ref="A5:B6"/>
              </mergeCells>
            </worksheet>
            """);

        AddPart(zip, "xl/worksheets/sheet2.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData><row r="1"><c r="A1"><v>7</v></c></row></sheetData>
            </worksheet>
            """);
    }

    /// <summary>只放 workbook + 一个 sheet（没有 styles / sharedStrings / theme），验证「缺部件用默认值」。</summary>
    private static void BuildMinimalWorkbook(string path)
    {
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);

        AddPart(zip, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
            </Types>
            """);

        AddPart(zip, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
            </Relationships>
            """);

        AddPart(zip, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="简表" sheetId="1" r:id="rId1"/></sheets>
            </workbook>
            """);

        AddPart(zip, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);

        AddPart(zip, "xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1">
                  <c r="A1"><v>42</v></c>
                  <c r="B1" t="s"><v>0</v></c>
                </row>
              </sheetData>
            </worksheet>
            """);
    }

    private static void AddPart(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    // ---------------------------------------------------------------- 单元格查询小工具

    private static ExcelCell? FindCell(ExcelSheet sheet, int rowIndex, int columnIndex)
    {
        var row = sheet.Rows.FirstOrDefault(item => item.RowIndex == rowIndex);
        if (row is null || columnIndex < 1 || columnIndex > row.Cells.Count) return null;
        return row.Cells[columnIndex - 1];
    }

    private static string CellText(ExcelSheet sheet, int rowIndex, int columnIndex)
        => FindCell(sheet, rowIndex, columnIndex)?.DisplayText ?? "";
}
