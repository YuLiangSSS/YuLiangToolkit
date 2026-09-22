using System.Globalization;
using System.Windows.Data;

namespace YuLiangToolkit.App.Converters;

/// <summary>bool 取反转换器。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : (object)true;
}
