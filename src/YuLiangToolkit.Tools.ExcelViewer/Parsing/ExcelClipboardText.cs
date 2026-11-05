namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>右键菜单里「复制」的四种内容。</summary>
public enum ExcelCopyKind
{
    /// <summary>复制界面显示值（已按数字格式 / 日期格式化）。</summary>
    DisplayValue,

    /// <summary>复制文件里保存的原始值。</summary>
    RawValue,

    /// <summary>复制公式原文（带等号，例如 <c>=SUM(A1:A2)</c>）。</summary>
    Formula,

    /// <summary>复制单元格地址（A1 形式）。</summary>
    Address,
}

/// <summary>
/// 右键复制用的<b>纯文本组装</b>：把「选中的单元格 + 复制种类」变成要放进剪贴板的一行文字。
///
/// <para>抽成纯函数的理由和查找一样：剪贴板调用（<c>Clipboard.SetText</c>）本身要 try/catch、
/// 属于界面线程的副作用；而「显示值 / 原始值 / 公式是否带等号 / 地址怎么拼」这些规则是纯逻辑，
/// 放这里自检可以直接断言，也保证四种复制命令的行为一致。</para>
/// </summary>
public static class ExcelClipboardText
{
    /// <summary>取某种复制内容对应的文字；没有单元格时返回空串。</summary>
    public static string ForCell(ExcelCell? cell, ExcelCopyKind kind) => kind switch
    {
        ExcelCopyKind.DisplayValue => cell?.DisplayText ?? "",
        ExcelCopyKind.RawValue => cell?.RawValue ?? "",
        ExcelCopyKind.Formula => FormulaText(cell),
        ExcelCopyKind.Address => AddressText(cell),
        _ => "",
    };

    /// <summary>复制出来的公式统一带前导等号（和 Excel 里复制公式的观感一致）；没有公式 = 空串。</summary>
    public static string FormulaText(ExcelCell? cell)
        => string.IsNullOrEmpty(cell?.Formula) ? "" : "=" + cell!.Formula;

    /// <summary>单元格地址（A1 形式）；没有单元格 = 空串。</summary>
    public static string AddressText(ExcelCell? cell)
        => cell is null ? "" : ExcelAddresses.CellReference(cell.Row, cell.Column);

    /// <summary>这个复制命令此刻是否可用（只有「复制公式」要求真的有公式）。</summary>
    public static bool CanCopy(ExcelCell? cell, ExcelCopyKind kind)
    {
        if (cell is null) return false;
        return kind != ExcelCopyKind.Formula || !string.IsNullOrEmpty(cell.Formula);
    }

    /// <summary>复制菜单项的中文名（状态栏提示也复用它，保证文字一致）。</summary>
    public static string Describe(ExcelCopyKind kind) => kind switch
    {
        ExcelCopyKind.DisplayValue => "显示值",
        ExcelCopyKind.RawValue => "原始值",
        ExcelCopyKind.Formula => "公式",
        ExcelCopyKind.Address => "地址",
        _ => "",
    };
}
