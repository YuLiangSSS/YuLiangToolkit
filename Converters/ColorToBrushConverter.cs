using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace YuLiangToolkit.App.Converters;

/// <summary>把 <see cref="Color"/> 转成冻结的 <see cref="SolidColorBrush"/>（颜色拾取器的色块 / 预览用）。</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color) return Brushes.Transparent;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
