namespace YuLiangToolkit.Tools.ExcelViewer.Parsing;

/// <summary>
/// 单元格「显示公式」模式的<b>纯文本逻辑</b>：决定单元格在界面上该显示什么、悬停提示该给什么。
///
/// <para><b>为什么要单独一个纯函数</b>：显示公式是「一个开关影响 5000 × 200 个格子」的功能，
/// 绝不能在开关时给每个格子克隆一份模型（内存和恢复都受不了），所以界面用
/// <c>MultiBinding(单元格, ShowFormulas)</c> 把计算推迟到模板里；而「有公式显示 =公式、没公式显示值、
/// 公式为空时怎么办」这些规则放在这里，主表 / 冻结行表 / 以后的导出都能共用，自检也能直接断言。</para>
///
/// <para>公式原文在 <see cref="ExcelCell.Formula"/> 里<b>不带前导等号</b>（解析器从
/// <c>&lt;f&gt;</c> 里取的就是原文），显示时统一加 <c>=</c>，和 Excel 里的观感一致。</para>
/// </summary>
public static class ExcelCellText
{
    /// <summary>显示值超过这个长度才给悬停提示（太短的文本悬停提示只是噪音）。</summary>
    public const int ToolTipLengthThreshold = 40;

    /// <summary>
    /// 单元格在界面上显示的文字：
    /// <list type="bullet">
    ///   <item><paramref name="showFormulas"/> 开 + 有公式 → <c>=公式原文</c>；</item>
    ///   <item>其它情况 → 正常显示值（<see cref="ExcelCell.DisplayText"/>）。</item>
    /// </list>
    /// 单元格为空（null）返回空串。
    /// </summary>
    public static string For(ExcelCell? cell, bool showFormulas)
    {
        if (cell is null) return "";

        if (showFormulas && HasFormula(cell)) return "=" + cell.Formula;

        return cell.DisplayText;
    }

    /// <summary>
    /// 单元格的悬停提示：显示公式时给完整公式；带超链接时给链接地址；很长的显示值给全文；
    /// 都没有时返回 null（不显示空提示框）。
    /// </summary>
    public static string? ToolTipFor(ExcelCell? cell, bool showFormulas)
    {
        if (cell is null) return null;

        var parts = new List<string>();

        if (showFormulas && HasFormula(cell)) parts.Add("=" + cell.Formula);

        if (cell.Hyperlink is { } link) parts.Add(link.ToolTipText);

        if (parts.Count == 0 && !string.IsNullOrEmpty(cell.DisplayText)
            && cell.DisplayText.Length > ToolTipLengthThreshold)
        {
            parts.Add(cell.DisplayText);
        }

        return parts.Count == 0 ? null : string.Join(Environment.NewLine, parts);
    }

    /// <summary>这个格子有没有可显示的公式（Formula 非空 / 非空白）。</summary>
    public static bool HasFormula(ExcelCell? cell)
        => cell is not null && !string.IsNullOrWhiteSpace(cell.Formula);
}
