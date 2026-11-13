using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace YuLiangToolkit.Core.Notifications;

/// <summary>
/// 把「主题资源名」字符串转成画刷（例如 <c>"SuccessBrush"</c> → 主题里的成功色）。
///
/// <para>用途：ViewModel 里想按状态给圆点 / 文字换颜色时，不必在 ViewModel 里碰 <c>Brush</c>
/// （那样换主题不会跟着变），只要给出资源名即可。</para>
///
/// <para><b>解析顺序</b>：① 主题资源键（<c>AccentBrush</c>…）→ ② WPF 内置颜色名 / 已知画刷名
/// （<c>Transparent</c> / <c>Black</c>…）→ ③ 兜底中性色。</para>
///
/// <para>⚠ 第 ② 步不能少：早先只查资源，<c>"Transparent"</c> 查不到就被当成"未知"退回灰底，
/// 日历格子因此整片发灰（踩过）。</para>
/// </summary>
public sealed class ThemeBrushNameConverter : IValueConverter
{
    /// <summary>找不到资源、也不是已知颜色时用的兜底色（透明）。</summary>
    private static readonly Brush Fallback = CreateFrozen(Colors.Transparent);

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Brush brush) return brush;

        var key = value as string;
        if (string.IsNullOrWhiteSpace(key)) return Fallback;

        // ① 主题资源键
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush themed) return themed;
        }
        catch { /* 主题还没加载，继续往下试 */ }

        // ② WPF 内置颜色名（Transparent / Black / White / …）
        if (TryParseKnownColor(key) is { } known) return known;

        return Fallback;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    /// <summary>把 <c>Transparent</c> / <c>#RRGGBB</c> 这类写法转成画刷；不认识返回 null。</summary>
    private static Brush? TryParseKnownColor(string key)
    {
        // 常见几个直接给（避免依赖反射 / 类型转换器）
        switch (key.Trim().ToLowerInvariant())
        {
            case "transparent":
                return CreateFrozen(Colors.Transparent);
            case "black":
                return CreateFrozen(Colors.Black);
            case "white":
                return CreateFrozen(Colors.White);
        }

        try
        {
            if (ColorConverter.ConvertFromString(key) is Color color) return CreateFrozen(color);
        }
        catch
        {
            // 不是颜色写法
        }

        return null;
    }

    private static Brush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
