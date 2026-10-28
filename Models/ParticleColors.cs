using System.Globalization;
using System.Windows.Media;

namespace YuLiangToolkit.App.Models;

/// <summary>
/// 粒子自定义颜色的默认值 + <c>#RRGGBB</c> 解析 / 格式化（主程序与自检共用）。
///
/// <para>配置文件里颜色按 <c>#RRGGBB</c> 字符串存（人能直接看懂、也能手改）；
/// 拾色器控件只处理不透明颜色 —— 粒子的透明效果由引擎按寿命淡出，不靠颜色本身的 alpha。</para>
/// </summary>
public static class ParticleColors
{
    /// <summary>颜色 1 的默认值（原来的近白淡紫）。</summary>
    public static Color Default1 => Color.FromRgb(0xF2, 0xEE, 0xFF);

    /// <summary>颜色 2 的默认值（主题强调蓝的浅色版，和颜色 1 之间有明显的渐变区间）。</summary>
    public static Color Default2 => Color.FromRgb(0x8F, 0xB4, 0xFF);

    /// <summary>把 <c>#RRGGBB</c>（可省略 #）解析成颜色；格式不对返回 <paramref name="fallback"/>。</summary>
    public static Color Parse(string? text, Color fallback)
        => TryParse(text, out var color) ? color : fallback;

    /// <summary>把 <c>#RRGGBB</c>（可省略 #）解析成颜色。</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('#')) value = value[1..];
        if (value.Length != 6) return false;

        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)) return false;

        color = Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);
        return true;
    }

    /// <summary>颜色 → <c>#RRGGBB</c>（大写，忽略 alpha 通道）。</summary>
    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
