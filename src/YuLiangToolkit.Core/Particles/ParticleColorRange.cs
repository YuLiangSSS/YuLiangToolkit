using System.Windows.Media;

namespace YuLiangToolkit.Core.Particles;

/// <summary>
/// 自定义粒子颜色的取色数学（**纯函数**，自检直接断言）。
///
/// <para>主页设置里可以启用 1~2 种颜色：</para>
/// <list type="bullet">
///   <item>只启用 1 种 → 粒子固定用那个颜色；</item>
///   <item>启用 2 种 → 每颗粒子生成时拿到 0~1 的随机 <see cref="Particle.ColorMix"/>，
///   用 <see cref="Resolve"/> 在这两种颜色之间**线性插值**（RGB 三通道分别插值，包含透明度通道）；</item>
///   <item>两种都不启用 / 没开自定义颜色 → 界面层沿用原来的默认淡紫白（不走这里）。</item>
/// </list>
///
/// <para>插值在 sRGB 分量的 0~255 区间上做（直观、和取色器里看到的颜色一致），
/// 不对物理亮度做线性化 —— 这是装饰性粒子，不追求色彩管理精度。</para>
/// </summary>
public static class ParticleColorRange
{
    /// <summary>在两种颜色之间线性插值；<paramref name="mix"/> 会被夹到 0~1（0 = <paramref name="from"/>，1 = <paramref name="to"/>）。</summary>
    public static Color Lerp(Color from, Color to, double mix)
    {
        var t = double.IsNaN(mix) ? 0 : Math.Clamp(mix, 0.0, 1.0);
        return Color.FromArgb(
            Mix(from.A, to.A, t),
            Mix(from.R, to.R, t),
            Mix(from.G, to.G, t),
            Mix(from.B, to.B, t));
    }

    /// <summary>
    /// 按粒子的随机混合系数取色：<paramref name="onlyOne"/> 时（只启用了一种颜色）固定返回 <paramref name="first"/>。
    /// </summary>
    public static Color Resolve(double colorMix, Color first, Color second, bool onlyOne)
        => onlyOne ? first : Lerp(first, second, colorMix);

    private static byte Mix(byte from, byte to, double t)
    {
        var value = from + (to - from) * t;
        return (byte)Math.Round(Math.Clamp(value, 0.0, 255.0));
    }
}
