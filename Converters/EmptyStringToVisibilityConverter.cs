using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.App.Converters;

/// <summary>
/// 字符串为空 / 仅空白 → <see cref="Visibility.Visible"/>，否则 Collapsed。
/// 用于输入框里的“占位提示文字”：框里有内容时提示自动隐藏，避免与内容重叠。
///
/// <para><see cref="Invert"/> = true 时反过来（空 → Collapsed，非空 → Visible），
/// 用于“有内容才显示”的提示（例如「搜不到匹配的游戏」）。</para>
/// </summary>
public class EmptyStringToVisibilityConverter : IValueConverter
{
    /// <summary>是否反转（默认 false：空 = 显示）。</summary>
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = string.IsNullOrWhiteSpace(value as string);
        var visible = Invert ? !isEmpty : isEmpty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
