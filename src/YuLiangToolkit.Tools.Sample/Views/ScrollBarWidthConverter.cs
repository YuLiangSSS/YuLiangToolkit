using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.Tools.Sample.Views;

/// <summary>
/// 返回系统的垂直滚动条宽度（px），给文件列表表头当「占位列」用。
///
/// <para><b>为什么要占这一列</b>：ListBox 里的滚动条会从数据行右边吃掉一条，
/// 而表头在滚动区域之外、拿的是整宽，不补这一列的话表头就比数据行宽一条，
/// 「大小 / 修改时间」两列必然错开。ListBox <b>无论滚动条可不可见都会预留</b>这一段（实测 17px），
/// 所以这里恒定占位即可，不必判断滚动条此刻是否显示 —— 也就不会出现"窗口一缩放就歪"。</para>
///
/// <para>用转换器而不是 XAML 里写 <c>&lt;sys:Double&gt;{x:Static …}</c>：后者不是合法 XAML（试过，直接崩溃）。</para>
/// </summary>
public sealed class ScrollBarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => SystemParameters.VerticalScrollBarWidth;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
