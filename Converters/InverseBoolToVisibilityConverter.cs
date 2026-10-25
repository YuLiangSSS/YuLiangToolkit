using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.App.Converters;

/// <summary>
/// bool 取反后转成 <see cref="Visibility"/>（true → Collapsed，false → Visible）。
///
/// <para>⚠ 别拿 <see cref="InverseBoolConverter"/> 直接绑 <c>Visibility</c>：它返回的是 bool，
/// WPF 不会自动把 bool 转成 Visibility，转换失败时控件会保持默认的 Visible（看起来"绑定没生效"，
/// 只在输出窗口留一条绑定错误）。需要"取反再控显隐"就用这个（v0.11.0 棋牌房间面板踩过）。</para>
/// </summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v != Visibility.Visible;
}
