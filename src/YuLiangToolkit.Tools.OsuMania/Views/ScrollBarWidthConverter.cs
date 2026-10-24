using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.Tools.OsuMania.Views;

/// <summary>
/// 返回系统的垂直滚动条宽度（px），给文件列表 / 表格的表头当「占位列」用。
///
/// <para>ListBox 会在右边给滚动条预留一条（无论此刻可见不可见），而表头写在 ListBox 模板里、
/// 拿的是整宽；不补这一列的话表头会比数据行宽一条，右边几列就错开了。
/// 做法与「框架示例 / osu! 下图器」完全一致（见 AGENTS.md 2.9）。</para>
/// </summary>
public sealed class ScrollBarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SystemParameters.VerticalScrollBarWidth;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
