using System.IO;
using YuLiangToolkit.Tools.ExcelViewer.Parsing;

namespace YuLiangToolkit.Tools.ExcelViewer.ViewModels;

/// <summary>
/// 工作表下拉框 / 页签里的一项。<see cref="Sheet"/> 是解析出来的只读数据，
/// 这里只加「界面需要但数据里没有」的显示信息（隐藏标记、行列表述等）。
/// </summary>
public sealed class ExcelSheetItem
{
    public ExcelSheetItem(ExcelSheet sheet, int index)
    {
        Sheet = sheet;
        Index = index;
    }

    /// <summary>解析结果（只读数据）。</summary>
    public ExcelSheet Sheet { get; }

    /// <summary>在工作簿里的顺序（0 起，和 Excel 页签顺序一致）。</summary>
    public int Index { get; }

    /// <summary>原始工作表名。</summary>
    public string Name => Sheet.Name;

    /// <summary>是否可见（隐藏工作表也能打开查看，只是名字后面带标记）。</summary>
    public bool IsVisible => Sheet.IsVisible;

    /// <summary>下拉框里显示的文字：隐藏的工作表加「（隐藏）」，免得用户以为丢了。</summary>
    public string DisplayName => Sheet.IsVisible ? Sheet.Name : Sheet.Name + "（隐藏）";

    /// <summary>「N 行 × M 列」的简短说明（给下拉项 ToolTip 用）。</summary>
    public string SizeText => $"{Sheet.Rows.Count} 行 × {Sheet.MaxColumn} 列";

    /// <summary>下拉框显示用（WPF 在没写 DisplayMemberPath 时调 ToString）。</summary>
    public override string ToString() => DisplayName;
}

/// <summary>
/// 「最近打开」下拉框里的一项。文件可能已经被移走 / 删掉，所以带 <see cref="IsMissing"/> 标记，
/// 界面把它显示成灰色并提示「文件已不存在」，点它只会在状态栏说明，不会崩。
/// </summary>
public sealed class ExcelRecentItem
{
    public ExcelRecentItem(string path, DateTime lastOpenedUtc)
    {
        Path = path;
        LastOpenedUtc = lastOpenedUtc;
        FileName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(FileName)) FileName = path;
        Folder = System.IO.Path.GetDirectoryName(path) ?? "";
        IsMissing = !File.Exists(path);
    }

    /// <summary>文件完整路径。</summary>
    public string Path { get; }

    /// <summary>最后一次打开时间（UTC，配置里存的原始值）。</summary>
    public DateTime LastOpenedUtc { get; }

    /// <summary>文件名（下拉框主文字）。</summary>
    public string FileName { get; }

    /// <summary>所在文件夹（下拉框次文字，方便区分同名文件）。</summary>
    public string Folder { get; }

    /// <summary>文件现在还在不在（不在了就显示「（文件已不存在）」）。</summary>
    public bool IsMissing { get; }

    /// <summary>下拉框里的附加说明。</summary>
    public string MissingText => IsMissing ? "（文件已不存在）" : "";

    /// <summary>悬停提示：完整路径 + 最近打开时间。</summary>
    public string ToolTipText
        => $"{Path}{Environment.NewLine}最近打开：{LastOpenedUtc.ToLocalTime():yyyy-MM-dd HH:mm}{MissingText}";
}

/// <summary>
/// 选中单元格后显示在状态栏的信息（地址 / 显示值 / 原始值 / 公式 / 格式）。
/// <para>不可变对象：每次选格新建一个，界面绑定不会出现「一半旧值一半新值」。</para>
/// </summary>
public sealed class ExcelCellSelection
{
    public ExcelCellSelection(string address, string displayText, string rawValue,
        string? formula, string formatText, bool isMergedAnchor, bool isMergedCovered)
    {
        Address = address;
        DisplayText = displayText;
        RawValue = rawValue;
        Formula = formula;
        FormatText = formatText;
        IsMergedAnchor = isMergedAnchor;
        IsMergedCovered = isMergedCovered;
    }

    /// <summary>单元格地址，例如 <c>B12</c>。</summary>
    public string Address { get; }

    /// <summary>界面显示的文本（已按数字格式格式化）。</summary>
    public string DisplayText { get; }

    /// <summary>文件里存的原始值。</summary>
    public string RawValue { get; }

    /// <summary>公式原文（没有公式为 null；<b>只显示不计算</b>）。</summary>
    public string? Formula { get; }

    /// <summary>格式说明（数字格式 / 粗体 / 字号 / 对齐等，中文拼接）。</summary>
    public string FormatText { get; }

    /// <summary>是否是合并区域的锚点格。</summary>
    public bool IsMergedAnchor { get; }

    /// <summary>是否被合并区域覆盖（内容显示为空）。</summary>
    public bool IsMergedCovered { get; }

    /// <summary>拼给状态栏的一行文字。</summary>
    public string SummaryText
    {
        get
        {
            var parts = new List<string> { Address };

            var display = string.IsNullOrEmpty(DisplayText) ? "（空）" : DisplayText;
            parts.Add($"值：{display}");

            if (!string.IsNullOrEmpty(RawValue) && RawValue != DisplayText)
                parts.Add($"原始值：{RawValue}");

            if (!string.IsNullOrEmpty(Formula))
                parts.Add($"公式：={Formula}（只显示缓存值，不计算）");

            if (!string.IsNullOrEmpty(FormatText))
                parts.Add($"格式：{FormatText}");

            if (IsMergedAnchor) parts.Add("合并单元格锚点");
            else if (IsMergedCovered) parts.Add("被合并单元格覆盖");

            return string.Join("　·　", parts);
        }
    }
}
