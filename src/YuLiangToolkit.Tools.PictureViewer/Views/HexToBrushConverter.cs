using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.PictureViewer.Views;

/// <summary>
/// <c>#AARRGGBB</c> / <c>#RRGGBB</c> 字符串 → WPF 画刷（设置面板里的颜色预览块、调色板小方块用）。
/// 解析不了就返回透明（不会因为手改坏配置把界面搞崩）。
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || !PictureBorderStyle.TryParseArgb(hex, out var argb))
            return Brushes.Transparent;

        var (r, g, b, a) = PictureBorderStyle.Split(argb);
        var brush = new SolidColorBrush(Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
