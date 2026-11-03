using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.TextEditor.Views;

/// <summary>
/// <c>#AARRGGBB</c> / <c>#RRGGBB</c> 字符串 → WPF 画刷（设置面板的颜色预览块、背景色板小方块用）。
/// 解析不了就返回透明（不会因为手改坏配置把界面搞崩）。
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string hex || !TryParseHex(hex, out var color))
            return Brushes.Transparent;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>宽松解析颜色串：可有可无的 <c>#</c>，6 位 RGB 或 8 位 ARGB。</summary>
    internal static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;

        var text = hex.Trim().TrimStart('#');
        if (text.Length == 6 && int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        if (text.Length == 8 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
        {
            color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }
        return false;
    }
}
