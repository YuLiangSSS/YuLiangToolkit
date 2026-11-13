using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>
/// 把「主题资源名」字符串转成画刷（例如 <c>"SuccessBrush"</c> → 主题里的成功色）。
///
/// <para>用途：ViewModel 里想按状态给圆点 / 文字换颜色时，不必在 ViewModel 里碰 <c>Brush</c>
/// （那样换主题不会跟着变），只要给出资源名即可。找不到资源就退回一个中性灰。</para>
/// </summary>
public sealed class ThemeBrushNameConverter : IValueConverter
{
    /// <summary>找不到资源时用的兜底色。</summary>
    private static readonly Brush Fallback = CreateFallback();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) return Fallback;

        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch { /* 主题还没加载，走兜底 */ }

        return Fallback;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static Brush CreateFallback()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x8A, 0x90, 0x9C));
        brush.Freeze();
        return brush;
    }
}
