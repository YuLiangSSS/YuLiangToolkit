using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.Tools.MapDownloader.Views;

/// <summary>
/// 返回系统的垂直滚动条宽度（px），给结果表表头当「占位列」用 —— 与框架示例里的那个同理：
/// ListBox 无论滚动条可不可见都会给滚动条预留这一段，表头不补这一列就会比数据行宽一条，右边几列必然错开。
/// （为什么不直接引框架示例那个类：功能之间不互相引用，各留一份 20 行的转换器更干净。）
/// </summary>
public sealed class ScrollBarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SystemParameters.VerticalScrollBarWidth;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
