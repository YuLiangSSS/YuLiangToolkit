using System.Globalization;

namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>
/// Excel 地址换算：列字母 ↔ 序号、单元格引用（A1 形式）。
///
/// <para><b>为什么单独抽一个文件</b>：地址换算看起来简单，但它是「列标题 A..Z/AA..」、
/// 「合并单元格范围」、「公式里引用的坐标」三处共用的基础；写错一位（比如 AA 当 27 还是 28）
/// 会让整张表错列，而且靠肉眼很难发现。所以这里集中实现，自检直接断言。</para>
///
/// <para>约定：<b>行 / 列都是 1 基</b>（和 Excel 界面一致）；非法输入不抛异常的地方返回 0 / false，
/// 解析器遇到坏数据时按「没有这一格」处理，不让一个坏地址毁掉整张表。</para>
/// </summary>
public static class ExcelAddresses
{
    /// <summary>Excel 2007 及以后的最大列号（XFD = 16384）与最大行号（1048576）。</summary>
    public const int MaxColumnIndex = 16384;
    public const int MaxRowIndex = 1048576;

    /// <summary>
    /// 列序号 → 列字母：<c>1 → A</c>、<c>26 → Z</c>、<c>27 → AA</c>、<c>702 → ZZ</c>、<c>703 → AAA</c>。
    /// 这就是 Excel 的「26 进制但 0 用不到」的写法。
    /// </summary>
    public static string ColumnName(int column)
    {
        if (column <= 0) return "";

        Span<char> buffer = stackalloc char[8];   // 16384 → XFD，最多 3 位；留足余量
        var position = buffer.Length;
        var value = column;

        while (value > 0)
        {
            value--;                              // 先减 1：把 1..26 映射到 0..25，避免出现 0 字母
            buffer[--position] = (char)('A' + value % 26);
            value /= 26;
        }

        return new string(buffer[position..]);
    }

    /// <summary>
    /// 列字母 → 列序号（大小写不敏感）：<c>A → 1</c>、<c>Z → 26</c>、<c>AA → 27</c>、<c>AAA → 703</c>。
    /// 空串 / 含非字母 / 越界时返回 <b>0</b>（调用方按「没有这一列」处理）。
    /// </summary>
    public static int ParseColumn(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return 0;

        var value = 0;
        foreach (var raw in name.Trim())
        {
            var c = char.ToUpperInvariant(raw);
            if (c is < 'A' or > 'Z') return 0;

            value = value * 26 + (c - 'A' + 1);
            if (value > MaxColumnIndex) return 0;   // 超过 XFD 的字母组合直接当非法
        }

        return value;
    }

    /// <summary>行 / 列（1 基）→ 单元格引用，例如 <c>(3, 27) → "AA3"</c>。</summary>
    public static string CellReference(int row, int column)
        => column <= 0 ? "" : ColumnName(column) + row.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 解析单元格引用（A1 形式，可带 <c>$</c> 绝对引用符号，例如 <c>$AA$3</c>）。
    /// 成功返回 true 并输出 1 基行列；格式非法 / 行号越界返回 false 且输出 0。
    /// </summary>
    public static bool TryParseCellReference(string? text, out int row, out int column)
    {
        row = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.Trim();
        var index = 0;

        // 列部分：可选 $ + 若干字母
        if (index < span.Length && span[index] == '$') index++;
        var columnStart = index;
        while (index < span.Length && char.IsAsciiLetter(span[index])) index++;
        if (index == columnStart) return false;

        column = ParseColumn(span[columnStart..index]);
        if (column <= 0) return false;

        // 行部分：可选 $ + 若干数字
        if (index < span.Length && span[index] == '$') index++;
        var rowStart = index;
        while (index < span.Length && char.IsAsciiDigit(span[index])) index++;
        if (index == rowStart || index != span.Length) return false;

        if (!int.TryParse(span[rowStart..index], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedRow))
            return false;
        if (parsedRow is <= 0 or > MaxRowIndex) return false;

        row = parsedRow;
        return true;
    }

    /// <summary>
    /// 把 <c>"A1:C3"</c> 形式的区域拆成两个角（用于 mergeCells / dimension）。
    /// 任何一个角非法都返回 false。
    /// </summary>
    public static bool TryParseRange(string? text, out int topRow, out int leftColumn, out int bottomRow, out int rightColumn)
    {
        topRow = leftColumn = bottomRow = rightColumn = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var separator = text.IndexOf(':');
        if (separator < 0)
        {
            // 单个单元格也算一个 1×1 区域
            if (!TryParseCellReference(text, out topRow, out leftColumn)) return false;
            bottomRow = topRow;
            rightColumn = leftColumn;
            return true;
        }

        if (!TryParseCellReference(text[..separator], out topRow, out leftColumn)) return false;
        if (!TryParseCellReference(text[(separator + 1)..], out bottomRow, out rightColumn)) return false;

        // 用户手改的文件里偶尔出现 B2:A1 这种反着写的区域，这里按矩形归一
        if (bottomRow < topRow) (topRow, bottomRow) = (bottomRow, topRow);
        if (rightColumn < leftColumn) (leftColumn, rightColumn) = (rightColumn, leftColumn);
        return true;
    }
}
