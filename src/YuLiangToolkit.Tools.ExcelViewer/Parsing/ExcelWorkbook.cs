namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>
/// 解析出来的一个工作簿（只读数据，不含任何 WPF 对象）。
/// <para>为什么是一套「纯数据 + 只读列表」：解析在后台线程做，界面只在 UI 线程读它；
/// 两边不共享可变状态，就不用加锁，也不会出现「界面正读着、后台又改列表」的崩溃。</para>
/// </summary>
public sealed class ExcelWorkbook
{
    /// <summary>所有工作表（含隐藏工作表；隐藏表由 <see cref="ExcelSheet.IsVisible"/> 标记）。</summary>
    public IReadOnlyList<ExcelSheet> Sheets { get; init; } = Array.Empty<ExcelSheet>();

    /// <summary>工作簿是否使用 1904 日期系统（Apple 老 Excel 的默认值，影响日期显示）。</summary>
    public bool Uses1904DateSystem { get; init; }
}

/// <summary>
/// 一个工作表：行、列、合并区域 + 一些「界面需要知道」的统计信息。
/// </summary>
public sealed class ExcelSheet
{
    /// <summary>工作表名（页签上显示的名字）。</summary>
    public string Name { get; init; } = "";

    /// <summary>是否可见（<c>state="hidden"</c> / <c>veryHidden</c> 的工作表为 false）。</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>行数据（已按上限截断、已跳过隐藏行；行号保留原始 Excel 行号）。</summary>
    public IReadOnlyList<ExcelRow> Rows { get; init; } = Array.Empty<ExcelRow>();

    /// <summary>列信息（<b>包含隐藏列</b>；列索引 1 基，界面只给非隐藏列建 DataGrid 列）。</summary>
    public IReadOnlyList<ExcelColumn> Columns { get; init; } = Array.Empty<ExcelColumn>();

    /// <summary>合并单元格区域（1 基行列，已按解析上限裁剪）。</summary>
    public IReadOnlyList<ExcelMergedRange> MergedRanges { get; init; } = Array.Empty<ExcelMergedRange>();

    /// <summary>实际使用到的最大行号（1 基；空表为 0）。</summary>
    public int MaxRow { get; init; }

    /// <summary>实际使用到的最大列号（1 基；空表为 0）。</summary>
    public int MaxColumn { get; init; }

    /// <summary>
    /// 冻结窗格信息（来自 <c>sheetViews/sheetView/pane</c>）；没有冻结 / 只识别 <c>frozen</c> 与
    /// <c>frozenSplit</c> 时为 null（<c>split</c> 是「拆分」不是「冻结」，不还原）。
    /// </summary>
    public ExcelFreezePane? FreezePane { get; init; }

    /// <summary>
    /// 工作表的超链接映射：key = 单元格地址（A1 形式，例如 <c>A1</c> / <c>B12</c>），
    /// value = 该格的链接。<b>键忽略大小写</b>；没有超链接 / 缺 rels / 坏 XML 时为空字典。
    /// <para>单元格本身也有 <see cref="ExcelCell.Hyperlink"/>（界面直接读它，不用再查字典）；
    /// 这里保留映射是为了给「状态栏 / 自检 / 以后的导出」一个不遍历整张表的入口。</para>
    /// </summary>
    public IReadOnlyDictionary<string, ExcelHyperlink> Hyperlinks { get; init; }
        = new Dictionary<string, ExcelHyperlink>(StringComparer.OrdinalIgnoreCase);

    /// <summary>是否因为超过 <see cref="XlsxParseLimits"/> 被截断（界面要在状态栏提示）。</summary>
    public bool IsTruncated { get; init; }

    /// <summary>截断说明（中文，例如「工作表共 12000 行，只显示前 5000 行」）；没截断时为 null。</summary>
    public string? TruncationNote { get; init; }

    /// <summary>被跳过的隐藏行数量。</summary>
    public int HiddenRowCount { get; init; }

    /// <summary>不显示的隐藏列数量（界面不建列，在状态栏说明）。</summary>
    public int HiddenColumnCount { get; init; }
}

/// <summary>
/// 工作表里的一行。<see cref="Cells"/> 与 <see cref="ExcelSheet.Columns"/> <b>按下标一一对应</b>
/// （即 <c>Cells[i]</c> 是第 i 个 <see cref="ExcelColumn"/> 对应的格），界面就是靠这个对应关系绑定的。
/// </summary>
public sealed class ExcelRow
{
    /// <summary>原始行号（1 基；隐藏行被跳过，所以行号可能不连续，界面行号要显示它而不是列表序号）。</summary>
    public int RowIndex { get; init; }

    /// <summary>这一行的所有单元格（含隐藏列；空格子也会占位，保证下标对齐）。</summary>
    public IReadOnlyList<ExcelCell> Cells { get; init; } = Array.Empty<ExcelCell>();

    /// <summary>这一行是否隐藏（解析时会被跳过；保留字段是为了以后「显示隐藏行」用）。</summary>
    public bool IsHidden { get; init; }

    /// <summary>
    /// 文件里写的行高（已从磅换算成 WPF 的 DIP）；<b>null = 没有 ht，界面按内容自动撑高</b>。
    /// <para>Excel 的 <c>ht</c> 单位是磅（1 磅 = 96/72 DIP），这里统一换算好，
    /// 免得界面层到处再乘一遍、还容易把「磅」和「像素」搞混。</para>
    /// </summary>
    public double? HeightDip { get; init; }
}

/// <summary>
/// 一个工作表的冻结窗格（<c>sheetViews/sheetView/pane</c> 的 <c>frozen</c> / <c>frozenSplit</c>）。
///
/// <para><b>为什么只存「界面可见的行列数」而不是原始 xSplit / ySplit</b>：Excel 的 xSplit / ySplit
/// 是「冻结了几列 / 几行（含隐藏行列）」，而界面只显示可见行列；直接用数字会让隐藏列把冻结边界带偏。
/// 所以解析器优先用 <c>topLeftCell</c>（第一个可滚动单元格）换算出边界，再数「边界内有多少可见行列」，
/// 界面拿到的就是可以直接用的 <see cref="FrozenColumns"/> / <see cref="FrozenRows"/>。</para>
/// </summary>
public sealed class ExcelFreezePane
{
    /// <summary>界面里可见的冻结行数（主表上方第二个 DataGrid 显示前 N 行）。</summary>
    public int FrozenRows { get; init; }

    /// <summary>界面里可见的冻结列数（主 DataGrid 的 <c>FrozenColumnCount</c>）。</summary>
    public int FrozenColumns { get; init; }

    /// <summary>原始 <c>topLeftCell</c>（第一个可滚动单元格，例如 <c>B3</c>）；没有时为空串。</summary>
    public string TopLeftCell { get; init; } = "";

    /// <summary>是否真的冻结了行列（两个计数都为 0 时不算）。</summary>
    public bool IsFrozen => FrozenRows > 0 || FrozenColumns > 0;

    /// <summary>
    /// 状态栏用的一句话，例如「冻结：首行 · 前 2 列」「冻结：首列」「冻结：前 3 行」。
    /// </summary>
    public string Description
    {
        get
        {
            if (!IsFrozen) return "";

            if (FrozenRows > 0 && FrozenColumns > 0)
                return $"冻结：{DescribeRows()} · {DescribeColumns()}";

            return FrozenRows > 0 ? $"冻结：{DescribeRows()}" : $"冻结：{DescribeColumns()}";
        }
    }

    private string DescribeRows() => FrozenRows == 1 ? "首行" : $"前 {FrozenRows} 行";

    private string DescribeColumns() => FrozenColumns == 1 ? "首列" : $"前 {FrozenColumns} 列";
}

/// <summary>单元格的显示类型（来自 XML 的 <c>t</c> 属性 + 数字格式判断）。</summary>
public enum ExcelCellKind
{
    /// <summary>空格子（没有值）。</summary>
    Empty,

    /// <summary>数字（含百分比 / 千分位 / 科学计数等，显示值已按格式算好）。</summary>
    Number,

    /// <summary>文本（共享字符串 / 内联字符串 / 公式的文本结果）。</summary>
    Text,

    /// <summary>布尔值（显示 TRUE / FALSE）。</summary>
    Boolean,

    /// <summary>错误值（例如 #DIV/0!，原样显示）。</summary>
    Error,

    /// <summary>日期时间（数字 + 日期格式，或 ISO 日期类型）。</summary>
    Date,
}

/// <summary>单元格的水平对齐（Excel 的 horizontal 属性；General 交给界面默认对齐）。</summary>
public enum ExcelHorizontalAlignment
{
    General,
    Left,
    Center,
    Right,
}

/// <summary>
/// 一个单元格的样式（从 styles.xml 的 cellXfs 解析出来，<b>同一样式的所有格子共享一个实例</b>，
/// 5000 行 × 200 列也不会因此多出几百万个对象）。
/// </summary>
public sealed class ExcelCellStyle
{
    /// <summary>原始数字格式代码（空串 = 常规格式；解析器已把内置 id 换成代码）。</summary>
    public string NumberFormatCode { get; init; } = "";

    /// <summary>是否粗体。</summary>
    public bool Bold { get; init; }

    /// <summary>是否斜体。</summary>
    public bool Italic { get; init; }

    /// <summary>是否下划线。</summary>
    public bool Underline { get; init; }

    /// <summary>字号（磅）；null = 用界面默认字号。</summary>
    public double? FontSizePt { get; init; }

    /// <summary>字体颜色 ARGB（<c>#AARRGGBB</c>）；null = 用主题默认前景色（深色主题可读）。</summary>
    public string? FontColorArgb { get; init; }

    /// <summary>填充色 ARGB（<c>#AARRGGBB</c>）；null / 透明 = 不画底色。</summary>
    public string? FillColorArgb { get; init; }

    /// <summary>水平对齐。</summary>
    public ExcelHorizontalAlignment HorizontalAlignment { get; init; }

    /// <summary>是否自动换行（DataGrid 行高固定，超出会裁掉；完整内容看状态栏 / ToolTip）。</summary>
    public bool WrapText { get; init; }

    /// <summary>缩进级别（Excel 的 indent，0 起；界面按 8 像素 / 级近似）。</summary>
    public int Indent { get; init; }

    /// <summary>共享的默认样式（没有 style 索引 / 索引越界时用）。</summary>
    public static ExcelCellStyle Default { get; } = new();
}

/// <summary>一个单元格（只读）。</summary>
public sealed class ExcelCell
{
    /// <summary>行号（1 基）。</summary>
    public int Row { get; init; }

    /// <summary>列号（1 基）。</summary>
    public int Column { get; init; }

    /// <summary>
    /// 界面上显示的文字（已按数字格式 / 日期系统格式化；合并被覆盖的格子为空串）。
    /// <para>这几个显示字段是可写的：合并单元格信息在 <c>sheetData</c> 之后才读到，
    /// 解析器建完行后再按合并区域把「被覆盖格」清空（比先收集坐标、建两遍行简单，也不会被超大合并区域拖慢）。</para>
    /// </summary>
    public string DisplayText { get; set; } = "";

    /// <summary>文件里存的原始值（数字 / 布尔是原始文本；公式单元格是缓存值）。</summary>
    public string RawValue { get; set; } = "";

    /// <summary>公式原文（<b>只保留不计算</b>；没有公式时为 null）。</summary>
    public string? Formula { get; init; }

    /// <summary>显示类型。</summary>
    public ExcelCellKind Kind { get; set; }

    /// <summary>单元格样式（共享实例）。</summary>
    public ExcelCellStyle Style { get; init; } = ExcelCellStyle.Default;

    /// <summary>
    /// 这一格的超链接；没有链接时为 null。
    /// <para>可写：超链接信息在 <c>sheetData</c> 之后（<c>hyperlinks</c> 节点）才读到，
    /// 解析器建完行后再按 ref 范围挂到格子上，和合并单元格同一套处理方式。</para>
    /// </summary>
    public ExcelHyperlink? Hyperlink { get; set; }

    /// <summary>这一格有没有超链接（XAML 触发器 / 转换器用；不参与解析 / 序列化）。</summary>
    public bool HasHyperlink => Hyperlink is not null;

    /// <summary>是否是合并区域左上角的「锚点格」（只有锚点格显示内容）。</summary>
    public bool IsMergedAnchor { get; set; }

    /// <summary>是否被合并区域覆盖（显示为空）。</summary>
    public bool IsMergedCovered { get; set; }
}

/// <summary>一列的信息（列宽来自 worksheets 的 <c>cols/col</c>）。</summary>
public sealed class ExcelColumn
{
    /// <summary>列号（1 基）。</summary>
    public int ColumnIndex { get; init; }

    /// <summary>列标题（A / B ... AA / AB；用 <see cref="ExcelAddresses.ColumnName"/> 算出来）。</summary>
    public string Name { get; init; } = "";

    /// <summary>列宽（WPF DIP，已把 Excel 的字符宽近似换算成像素并夹到 24 ~ 600）。</summary>
    public double WidthDip { get; init; } = 64;

    /// <summary>是否隐藏（隐藏列不建界面列，数量在状态栏说明）。</summary>
    public bool IsHidden { get; init; }
}

/// <summary>合并单元格区域（1 基、包含端点）。</summary>
public sealed class ExcelMergedRange
{
    public int Top { get; init; }
    public int Left { get; init; }
    public int Bottom { get; init; }
    public int Right { get; init; }

    /// <summary>方便界面 / 日志显示，例如 <c>A1:C2</c>。</summary>
    public override string ToString()
        => $"{ExcelAddresses.CellReference(Top, Left)}:{ExcelAddresses.CellReference(Bottom, Right)}";
}

/// <summary>
/// 解析上限：超大表只解析前 N 行 / N 列，避免一个 50 万行的表把界面卡死。
/// <para>默认值 5000 行 / 200 列；调大也夹在 20000 行 / 1000 列以内
/// （再大 DataGrid 的滚动和内存就撑不住了，属于有意的硬上限）。</para>
/// </summary>
public sealed class XlsxParseLimits
{
    /// <summary>最多解析多少行（默认 5000，允许 1 ~ 20000）。</summary>
    public int MaxRows { get; set; } = 5000;

    /// <summary>最多解析多少列（默认 200，允许 1 ~ 1000）。</summary>
    public int MaxColumns { get; set; } = 200;

    /// <summary>生效的行上限（夹到 1 ~ 20000）。</summary>
    public int MaxRowsEffective => Math.Clamp(MaxRows, 1, 20000);

    /// <summary>生效的列上限（夹到 1 ~ 1000）。</summary>
    public int MaxColumnsEffective => Math.Clamp(MaxColumns, 1, 1000);
}
