using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.App.Converters;

/// <summary>
/// 字符串为空 / 仅空白 → <see cref="Visibility.Visible"/>，否则 Collapsed。
/// 用于输入框里的“占位提示文字”：框里有内容时提示自动隐藏，避免与内容重叠。
/// </summary>
public class EmptyStringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
