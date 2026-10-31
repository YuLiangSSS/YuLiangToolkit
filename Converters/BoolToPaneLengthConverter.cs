using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YuLiangToolkit.App.Converters;

/// <summary>
/// bool → 长度：给「可以整块关掉的页面级区块」用，让它的 <c>Grid</c> 行高 / <c>MinHeight</c>
/// 跟着开关一起收掉（true = 用 ConverterParameter 指定的长度，false = 0）。
///
/// <para><b>为什么同时处理两种目标类型</b>：同一个区块要绑两个属性 ——
/// <c>RowDefinition.Height</c>（<see cref="GridLength"/>）和 <c>RowDefinition.MinHeight</c>（<c>double</c>）。
/// 转换器按 <c>targetType</c> 返回对应类型，XAML 里两处写同一个资源 + 同一个参数即可，不用建两个转换器。</para>
///
/// <para>用法（棋牌「显示棋谱」关掉时把下面那条页面级横缝与棋谱整块收起来，不留空白）：</para>
/// <code>
/// &lt;RowDefinition Height="{Binding ShowMoveList, Converter={StaticResource BoolToPaneLength}, ConverterParameter=280}"
///                MinHeight="{Binding ShowMoveList, Converter={StaticResource BoolToPaneLength}, ConverterParameter=200}" /&gt;
/// </code>
/// <para>⚠ 配合 <c>PaneSplitter</c> 用时：分隔条本身也要绑同源 <c>Visibility</c>；
/// <c>PaneSplitter</c> 会跳过不可见状态的「套用记忆尺寸」，避免刚隐藏又被记忆值撑回来。</para>
/// </summary>
public class BoolToPaneLengthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var length = 0d;
        if (parameter is string text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            length = Math.Max(0, parsed);
        }

        var on = value is bool flag && flag;
        var result = on ? length : 0d;

        // RowDefinition.Height 要 GridLength；MinHeight 要 double。
        return targetType == typeof(GridLength) ? new GridLength(result) : result;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException("BoolToPaneLengthConverter 只支持单向绑定。");
}
