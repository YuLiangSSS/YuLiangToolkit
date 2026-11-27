using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.ScreenPen;

/// <summary>把配置里的 #RRGGBB / #AARRGGBB 转成画刷，供调色板色块绑定。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => new SolidColorBrush(ScreenPenColor.Parse(value as string));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is SolidColorBrush brush ? ScreenPenColor.ToHex(brush.Color) : "#FFFF3B30";
}
