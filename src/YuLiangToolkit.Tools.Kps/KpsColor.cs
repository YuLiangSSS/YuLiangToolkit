using System.Globalization;
using System.Windows.Media;

namespace YuLiangToolkit.Tools.Kps;

/// <summary>
/// 颜色工具：配置里统一存 <c>#AARRGGBB</c> / <c>#RRGGBB</c> 字符串，界面用 <see cref="SolidColorBrush"/>。
/// 项目约定不引第三方包，所以自己解析；解析失败退回默认色（绝不因为用户手改配置抛异常）。
/// </summary>
public static class KpsColor
{
    /// <summary>把配置字符串解析成颜色；失败返回 <paramref name="fallback"/>。</summary>
    public static Color Parse(string? text, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        try
        {
            var value = text.Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>解析成冻结的画刷（冻结 = 跨线程安全、渲染更快）。</summary>
    public static SolidColorBrush ParseBrush(string? text, Color fallback)
    {
        var brush = new SolidColorBrush(Parse(text, fallback));
        brush.Freeze();
        return brush;
    }

    /// <summary>颜色 → <c>#AARRGGBB</c> 配置字符串。</summary>
    public static string ToHex(Color color)
        => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>只取 RGB 部分（<c>#RRGGBB</c>）。</summary>
    public static string ToRgbHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>把不透明度（0~1）乘到颜色的 alpha 上（Overlay 背景 / 音符效果用）。</summary>
    public static Color WithAlpha(Color color, double opacity)
    {
        var alpha = (byte)Math.Clamp(Math.Round(color.A * Math.Clamp(opacity, 0, 1)), 0, 255);
        return Color.FromArgb(alpha, color.R, color.G, color.B);
    }

    /// <summary>颜色字符串是否合法（编辑框实时校验用）。</summary>
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var value = text.Trim();
            if (!value.StartsWith('#')) value = "#" + value;
            _ = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把颜色转成界面上的十六进制文本（保留 alpha）。</summary>
    public static string Format(Color color) => ToHex(color);

    /// <summary>按文化与固定格式格式化百分比 / 颜色辅助文本用。</summary>
    public static string Percent(double ratio) =>
        (Math.Clamp(ratio, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
