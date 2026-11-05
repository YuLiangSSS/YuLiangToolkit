namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>查找命中在单元格的哪个字段上（状态栏 / 自检用来解释「为什么这格算命中」）。</summary>
public enum ExcelSearchField
{
    /// <summary>界面上显示的文本（已按数字格式 / 日期格式化）。</summary>
    DisplayText,

    /// <summary>文件里存的原始值。</summary>
    RawValue,

    /// <summary>公式原文（不含等号）。</summary>
    Formula,
}

/// <summary>
/// 一处查找命中：行号 / 列号（1 基，Excel 原始坐标）+ 命中的字段 + 地址 + 预览文字。
/// <para>纯数据对象：查找在后台线程构建，结果回 UI 线程只读使用。</para>
/// </summary>
public sealed class ExcelSearchHit
{
    /// <summary>Excel 原始行号（1 基；隐藏行被跳过，所以可能不连续）。</summary>
    public int RowIndex { get; init; }

    /// <summary>列号（1 基，含隐藏列）。</summary>
    public int ColumnIndex { get; init; }

    /// <summary>命中的字段。</summary>
    public ExcelSearchField Field { get; init; }

    /// <summary>单元格地址，例如 <c>B12</c>。</summary>
    public string Address { get; init; } = "";

    /// <summary>命中格的预览文字（优先显示值，没有就公式 / 原始值）。</summary>
    public string PreviewText { get; init; } = "";

    /// <summary>命中的字段名（中文，状态栏 / 日志用）。</summary>
    public string FieldText => Field switch
    {
        ExcelSearchField.RawValue => "原始值",
        ExcelSearchField.Formula => "公式",
        _ => "显示值",
    };
}

/// <summary>
/// 一次查找的结果。<see cref="IsTruncated"/> = 命中数超过上限、只保留了前 <see cref="ExcelSearch.MaxHits"/> 个。
/// </summary>
public sealed class ExcelSearchResult
{
    /// <summary>命中列表（按「行 → 列」顺序）。</summary>
    public IReadOnlyList<ExcelSearchHit> Hits { get; init; } = Array.Empty<ExcelSearchHit>();

    /// <summary>是否因为有更多命中而截断（已经扫到第 <see cref="ExcelSearch.MaxHits"/> + 1 个）。</summary>
    public bool IsTruncated { get; init; }
}

/// <summary>
/// 「查找单元格」的<b>纯逻辑</b>：在当前工作表已解析的单元格里找一串文字。
///
/// <para><b>为什么抽成纯函数</b>：界面要跑 <c>Task.Run</c> 还不能卡 UI，查找逻辑本身又需要
/// 区分大小写、按三个字段命中、限制上限、支持取消 —— 放在 ViewModel 里既难测也容易和界面状态搅在一起。
/// 这里只吃「行列表 + 关键字」、吐结果对象，自检可以直接断言。</para>
///
/// <para><b>匹配规则</b>：<see cref="ExcelCell.DisplayText"/> / <see cref="ExcelCell.RawValue"/> /
/// <see cref="ExcelCell.Formula"/> 任一包含关键字就算命中；同一格只记一条，字段按上面顺序取第一个命中的。
/// 大小写按 <c>Ordinal</c> / <c>OrdinalIgnoreCase</c>（不做文化相关比较，避免「ß vs ss」这类意外）。</para>
/// </summary>
public static class ExcelSearch
{
    /// <summary>一次最多保留多少处命中（再多界面也翻不过来，状态栏会提示「只显示前 N 处」）。</summary>
    public const int MaxHits = 2000;

    /// <summary>
    /// 在给定行里查找。<paramref name="query"/> 为空 / 全空白、没有行、上限非正时返回空结果（不抛异常）。
    /// <paramref name="cancellationToken"/> 会在每行开始与每 64 格检查一次，取消时抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    public static ExcelSearchResult Search(
        IReadOnlyList<ExcelRow>? rows,
        string? query,
        bool caseSensitive,
        int maxHits = MaxHits,
        CancellationToken cancellationToken = default)
    {
        if (rows is null || rows.Count == 0 || string.IsNullOrEmpty(query) || maxHits <= 0)
        {
            return new ExcelSearchResult();
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var hits = new List<ExcelSearchHit>();
        var truncated = false;

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var row = rows[rowIndex];
            if (row?.Cells is null) continue;

            for (var cellIndex = 0; cellIndex < row.Cells.Count; cellIndex++)
            {
                // 每 64 格查一次取消：5000 × 200 的表也能在用户改关键字时立刻停下
                if ((cellIndex & 0x3F) == 0) cancellationToken.ThrowIfCancellationRequested();

                var cell = row.Cells[cellIndex];
                if (cell is null) continue;

                var field = MatchField(cell, query, comparison);
                if (field is null) continue;

                if (hits.Count >= maxHits)
                {
                    // 已经装满了，再发现一个命中就说明「还有更多」，不需要继续扫
                    truncated = true;
                    break;
                }

                hits.Add(new ExcelSearchHit
                {
                    RowIndex = cell.Row > 0 ? cell.Row : row.RowIndex,
                    ColumnIndex = cell.Column > 0 ? cell.Column : cellIndex + 1,
                    Field = field.Value,
                    Address = ExcelAddresses.CellReference(
                        cell.Row > 0 ? cell.Row : row.RowIndex,
                        cell.Column > 0 ? cell.Column : cellIndex + 1),
                    PreviewText = BuildPreview(cell),
                });
            }

            if (truncated) break;
        }

        return new ExcelSearchResult { Hits = hits, IsTruncated = truncated };
    }

    /// <summary>判断一格命中哪个字段；不命中返回 null。</summary>
    private static ExcelSearchField? MatchField(ExcelCell cell, string query, StringComparison comparison)
    {
        if (!string.IsNullOrEmpty(cell.DisplayText) && cell.DisplayText.Contains(query, comparison))
            return ExcelSearchField.DisplayText;

        if (!string.IsNullOrEmpty(cell.RawValue) && cell.RawValue.Contains(query, comparison))
            return ExcelSearchField.RawValue;

        if (!string.IsNullOrEmpty(cell.Formula) && cell.Formula.Contains(query, comparison))
            return ExcelSearchField.Formula;

        return null;
    }

    /// <summary>命中格的预览：优先显示值，其次公式（带等号），最后原始值。</summary>
    private static string BuildPreview(ExcelCell cell)
    {
        if (!string.IsNullOrEmpty(cell.DisplayText)) return cell.DisplayText;
        if (!string.IsNullOrEmpty(cell.Formula)) return "=" + cell.Formula;
        return cell.RawValue;
    }
}
